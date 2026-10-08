using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.Agent;

/// <summary>One immutable host binding. Retire its session token before publishing a replacement generation.</summary>
public sealed record ToolInvocationScopeOptions(long SessionGeneration, CancellationToken SessionCancellationToken = default,
    int MaximumDepth = 8, int MaximumNestedCalls = 128)
{
    public ToolExecutionMode ExecutionMode { get; init; } = ToolExecutionMode.Parallel;
    public ImmutableHashSet<string> SequentialTools { get; init; } = ImmutableHashSet<string>.Empty;
    /// <summary>Tools whose nested call trees are not limited by <see cref="MaximumDepth"/> or <see cref="MaximumNestedCalls"/>, because the original sets
    /// no limit on them (codemode scripts). Cancellation, deadlines and the session lifetime still apply.</summary>
    public ImmutableHashSet<string> UncountedNestedCallTools { get; init; } = ImmutableHashSet<string>.Empty;
    internal void Validate()
    {
        if (SessionGeneration <= 0 || MaximumDepth is < 1 or > 64 || MaximumNestedCalls is < 1 or > 4096 ||
            !Enum.IsDefined(ExecutionMode) || SequentialTools is null || SequentialTools.Count > 4096 ||
            UncountedNestedCallTools is null || UncountedNestedCallTools.Count > 4096)
            throw new ArgumentOutOfRangeException(nameof(ToolInvocationScopeOptions));
    }
}

/// <summary>A capability valid only during this actual tool executor. All admitted children join executor settlement.</summary>
public sealed class ToolInvocationContext
{
    private static readonly AsyncLocal<ToolInvocationContext?> Current = new();
    private readonly object gate = new();
    private readonly ToolInvocationScopeOptions options;
    private readonly Func<ToolInvocationContext, ToolCallContent, ToolProgressCallback, CancellationToken, Task<ToolOutcome>> execute;
    private readonly List<Child> pending = [];
    private bool active, closed;
    private int nextId;
    internal ToolInvocation Invocation { get; }
    internal NestedToolCallRecord Record { get; }
    internal bool HoldsExclusiveQueue { get; }
    internal ToolProgressBudget? ProgressBudget { get; }
    public string ToolCallId => Invocation.Call.Id;
    public string RootToolCallId { get; }
    /// <summary>Name of the tool call at the root of this nested tree.</summary>
    public string RootToolName { get; }
    public string? ParentToolCallId { get; }
    public int CallDepth { get; }
    public long SessionGeneration => options.SessionGeneration;
    public CancellationToken OperationCancellationToken { get; }
    public CancellationToken SessionCancellationToken => options.SessionCancellationToken;
    public ImmutableArray<string> Tools { get; }
    public JsonData? NestedCalls => Record.Snapshot();
    internal JsonData? NestedUsage => Record.Usage;

    internal ToolInvocationContext(ToolInvocation invocation, ToolInvocationContext? parent,
        ToolInvocationScopeOptions options, CancellationToken token, ImmutableArray<string> tools,
        Func<ToolInvocationContext, ToolCallContent, ToolProgressCallback, CancellationToken, Task<ToolOutcome>> execute,
        bool holdsExclusiveQueue = false, ToolProgressBudget? progressBudget = null, int maximumRecordedArgumentDepth = 29)
    {
        Invocation = invocation; this.options = options; this.execute = execute;
        OperationCancellationToken = token; Tools = tools; ParentToolCallId = parent?.ToolCallId;
        RootToolCallId = parent?.RootToolCallId ?? invocation.Call.Id;
        RootToolName = parent?.RootToolName ?? invocation.Call.Name;
        CallDepth = parent is null ? 0 : parent.CallDepth + 1;
        Record = parent?.Record ?? new(options.MaximumNestedCalls, maximumRecordedArgumentDepth);
        HoldsExclusiveQueue = holdsExclusiveQueue;
        ProgressBudget = parent?.ProgressBudget ?? progressBudget;
    }

    public ValueTask<ToolOutcome> ExecuteToolAsync(string name, JsonData arguments, CancellationToken cancellationToken = default) =>
        ExecuteToolAsync(name, arguments, static (_, _) => ValueTask.CompletedTask, cancellationToken);

    public ValueTask<ToolOutcome> ExecuteToolAsync(string name, JsonData arguments, ToolProgressCallback onProgress,
        CancellationToken cancellationToken = default)
    {
        Child child;
        ToolCallContent call;
        lock (gate)
        {
            call = new(ToolCallId + "/" + (++nextId).ToString(CultureInfo.InvariantCulture), name, arguments);
            if (OperationCancellationToken.IsCancellationRequested || SessionCancellationToken.IsCancellationRequested || cancellationToken.IsCancellationRequested)
                return Rejected(call, ToolFailureKind.Canceled, "Nested tool invocation was cancelled.");
            if (closed || !active || !ReferenceEquals(Current.Value, this))
                return Rejected(call, ToolFailureKind.Blocked, "Nested tool invocation context is inactive.");
            if (onProgress is null || onProgress.GetInvocationList().Length != 1)
                return Rejected(call, ToolFailureKind.InvalidArguments, "Nested tool update callback is invalid.");
            if (!options.UncountedNestedCallTools.Contains(RootToolName) && (CallDepth >= options.MaximumDepth || !Record.Admit()))
                return Rejected(call, ToolFailureKind.Blocked, "Nested tool invocation depth or call limit exceeded.");
            pending.Add(child = new());
        }
        // Publish the actual task after releasing the state lock; CloseAsync also waits for publication.
        try { child.Work = execute(this, call, onProgress, cancellationToken); _ = Settled(child); }
        catch (Exception)
        {
            child.Work = Task.FromResult(new ToolOutcome(new(Invocation.AssistantMessage with { Content = [call], StopReason = StopReason.ToolUse }, call, 0),
                ToolResult.Error(ToolFailureKind.ExecutionError, "Nested tool dispatch failed.")));
        }
        finally { child.Published.TrySetResult(); }
        return new(child.Work!);
    }

    /// <summary>A settled child leaves the pending list, so a tree with any number of calls keeps only its running ones.</summary>
    private async Task Settled(Child child)
    {
        try { await child.Work!.ConfigureAwait(false); } catch (Exception) { }
        lock (gate) pending.Remove(child);
    }

    private ValueTask<ToolOutcome> Rejected(ToolCallContent call, ToolFailureKind kind, string message)
    {
        var child = new ToolInvocation(Invocation.AssistantMessage with { Content = [call], StopReason = StopReason.ToolUse }, call, 0);
        var context = new ToolInvocationContext(child, this, options, OperationCancellationToken, Tools, execute);
        context.StopAdmission(); child = child with { Context = context };
        return ValueTask.FromResult(new ToolOutcome(child, ToolResult.Error(kind, message)));
    }
    internal void Activate() { lock (gate) if (!closed) active = true; }
    internal void StopAdmission() { lock (gate) { closed = true; active = false; } }
    internal async ValueTask CloseAsync()
    {
        Child[] children;
        lock (gate) { closed = true; active = false; children = pending.ToArray(); }
        // Every admitted task is retained, including children a trusted executor chose not to await.
        await Task.WhenAll(children.Select(Join)).ConfigureAwait(false);
        lock (gate) pending.Clear();
        static async Task Join(Child child)
        { await child.Published.Task.ConfigureAwait(false); await child.Work!.ConfigureAwait(false); }
    }
    internal IDisposable Enter()
    {
        var previous = Current.Value; Current.Value = this;
        return new Frame(previous);
    }
    private sealed class Frame(ToolInvocationContext? previous) : IDisposable
    { public void Dispose() => Current.Value = previous; }
    private sealed class Child
    {
        public TaskCompletionSource Published { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ToolOutcome>? Work { get; set; }
    }
}

/// <summary>Source-shaped bounded descendant record, separate from the native execution admission limit.</summary>
internal sealed class NestedToolCallRecord(int maximumCalls, int maximumArgumentDepth)
{
    private readonly object gate = new();
    private readonly List<Entry> entries = [];
    private int admitted, argumentBytes, metadataBytes;
    private bool complete = true;
    private JsonData? usage;
    internal JsonData? Usage { get { lock (gate) return usage; } }
    internal sealed class Entry(string id, string name, JsonData? arguments, int? omittedBytes)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public JsonData? Arguments { get; } = arguments;
        public int? OmittedBytes { get; } = omittedBytes;
        public long Started { get; } = Stopwatch.GetTimestamp();
        public long? Duration { get; set; }
        public string Status { get; set; } = "unfinished";
        public string? Error { get; set; }
    }
    internal bool Admit() { lock (gate) { if (admitted >= maximumCalls) return false; admitted++; return true; } }
    internal Entry? Start(ToolCallContent call)
    {
        lock (gate)
        {
            if (entries.Count >= 256) { complete = false; return null; }
            var id = call.Id;
            var name = call.Name;
            // Invalid calls still reach the normal error pipeline, but cannot fabricate a retained identity.
            if (id is null || name is null) { complete = false; return null; }
            // Keep retained identities exact; an oversized native record is explicitly incomplete.
            var identityBytes = Encoding.UTF8.GetByteCount(id) + Encoding.UTF8.GetByteCount(name);
            if (identityBytes > 65536 - metadataBytes) { complete = false; return null; }
            metadataBytes += identityBytes;
            var bytes = call.Arguments is null ? 0 : Encoding.UTF8.GetByteCount(call.Arguments.ToString());
            var omit = bytes > 8192 || bytes > 32768 - argumentBytes ||
                call.Arguments is not null && !FitsDepth(call.Arguments.Value, 0);
            if (omit) complete = false; else argumentBytes += bytes;
            var entry = new Entry(id, name, omit ? null : call.Arguments, omit ? bytes : null);
            entries.Add(entry); return entry;
        }
    }
    internal void Finish(Entry? entry, ToolOutcome outcome)
    {
        lock (gate)
        {
            usage = NestedToolUsage.Combine(usage, outcome.Result.Usage);
            if (entry is null) return;
            entry.Status = outcome.IsError ? "error" : "ok";
            entry.Duration = (long)Math.Round(Stopwatch.GetElapsedTime(entry.Started).TotalMilliseconds);
            if (outcome.IsError)
            {
                var text = string.Join("\n", outcome.Result.Content.Select(part => part.Text));
                var count = Math.Min(text.Length, 500);
                if (count > 0 && count < text.Length && char.IsHighSurrogate(text[count - 1])) count--;
                entry.Error = text[..count];
            }
        }
    }
    private bool FitsDepth(JsonElement value, int depth)
    {
        if (depth > maximumArgumentDepth) return false;
        return value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().All(property => FitsDepth(property.Value, depth + 1)),
            JsonValueKind.Array => value.EnumerateArray().All(item => FitsDepth(item, depth + 1)),
            _ => true
        };
    }
    internal JsonData? Snapshot()
    {
        lock (gate)
        {
            if (entries.Count == 0 && complete) return null;
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject(); writer.WritePropertyName("calls"); writer.WriteStartArray();
                foreach (var entry in entries)
                {
                    writer.WriteStartObject(); writer.WriteString("id", entry.Id); writer.WriteString("name", entry.Name);
                    writer.WriteString("status", entry.Status);
                    if (entry.Arguments is { } args) { writer.WritePropertyName("arguments"); writer.WriteRawValue(args.ToString()); }
                    if (entry.OmittedBytes is { } bytes) writer.WriteNumber("argumentsBytes", bytes);
                    if (entry.Duration is { } duration) writer.WriteNumber("durationMs", duration);
                    if (!string.IsNullOrEmpty(entry.Error)) writer.WriteString("error", entry.Error);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray(); writer.WriteBoolean("complete", complete && entries.All(entry => entry.Status != "unfinished"));
                writer.WriteEndObject();
            }
            return JsonData.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
        }
    }
}
