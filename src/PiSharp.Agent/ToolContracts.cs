using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using PiSharp.Contracts;

namespace PiSharp.Agent;

public enum ToolExecutionMode { Parallel, Sequential }
public enum ToolFailureKind { UnknownTool, InvalidArguments, Blocked, ExecutionError, HookError, Canceled, Truncated }
public sealed record ToolFailure(ToolFailureKind Kind, string Message);

/// <summary>Finalized, owned native input. SourceIndex is the assistant content index.</summary>
public sealed record ToolInvocation(AssistantMessage AssistantMessage, ToolCallContent Call, int SourceIndex)
{
    /// <summary>Host-owned nested-call authority; absent on the legacy invocation path.</summary>
    public ToolInvocationContext? Context { get; internal init; }
    internal IAgentEventSink? EventSink { get; init; }
    /// <summary>The batch's opt-in monotonic clock; finalized executors time the tool's own execution with it.</summary>
    internal TimeProvider? Clock { get; init; }
    public string? ParentToolCallId => Context?.ParentToolCallId;
}

/// <summary>Owned result properties. Typed updates change the same immutable map used by wire projection.</summary>
public sealed record ToolResult
{
    private ImmutableDictionary<string, object?> _properties = ImmutableDictionary.Create<string, object?>(StringComparer.Ordinal);
    public ToolResult(ImmutableArray<TextContent> Content, JsonData Details, bool IsError = false,
        bool Terminate = false, ToolFailure? Failure = null)
    {
        this.Content = Content; this.Details = Details; this.Failure = Failure;
        if (IsError) this.IsError = true;
        if (Terminate) this.Terminate = true;
    }
    private ToolResult(ImmutableDictionary<string, object?> properties) => _properties = properties;
    public ImmutableArray<TextContent> Content
    {
        get => !_properties.TryGetValue("content", out var value) || value is JsonData { Value.ValueKind: System.Text.Json.JsonValueKind.Null }
            ? [] : value is ImmutableArray<TextContent> content ? content : ToolResultValueCodec.ReadContent((JsonData)value!);
        init => _properties = _properties.SetItem("content", value);
    }
    /// <summary>Complete owned text/image execution content; absent/null raw content has an empty array view.</summary>
    public JsonData ContentValue
    {
        get => ToolResultValueCodec.ExecutionContent(Property("content"));
        init => _properties = _properties.SetItem("content", ToolResultValueCodec.AdmitContent(value));
    }
    // An absent property has a compatibility view of {}, without inserting it into the owned result.
    public JsonData Details
    {
        get => _properties.TryGetValue("details", out var value) ? (JsonData)value! : JsonData.EmptyObject;
        init => _properties = _properties.SetItem("details", value);
    }
    public bool IsError
    {
        get => Failure is not null || IsTrue("isError");
        init => _properties = _properties.SetItem("isError", JsonData.Parse(value ? "true" : "false"));
    }
    public bool Terminate
    {
        get => IsTrue("terminate");
        init => _properties = _properties.SetItem("terminate", JsonData.Parse(value ? "true" : "false"));
    }
    /// <summary>Native failure classification; excluded from the source result object.</summary>
    public ToolFailure? Failure { get; init; }
    /// <summary>Optional programmatic output. Null means absent; JsonData.Null means explicit JSON null.</summary>
    public JsonData? StructuredContent
    {
        get => Property("structuredContent");
        init => _properties = value is null ? _properties.Remove("structuredContent") : _properties.SetItem("structuredContent", value);
    }
    /// <summary>Source tool usage is opaque owned JSON, not assistant TokenUsage.</summary>
    public JsonData? Usage
    {
        get => Property("usage");
        init => _properties = value is null ? _properties.Remove("usage") : _properties.SetItem("usage", value);
    }
    public bool HasProperty(string name) => _properties.ContainsKey(name);
    public JsonData? Property(string name) => !_properties.TryGetValue(name, out var value) ? null :
        value is ImmutableArray<TextContent> content ? ToolResultValueCodec.ContentJson(content) : (JsonData?)value;
    public ToolResult WithProperty(string name, JsonData? value) => new(value is null ? _properties.Remove(name) : _properties.SetItem(name, value)) { Failure = Failure };
    /// <summary>Admits a complete strict JSON object; preserves all owned property values and presence.</summary>
    public static ToolResult FromJson(JsonData value, ToolResultValueOptions? options = null) => ToolResultValueCodec.Read(value, options);
    public JsonData ToJson(ToolResultValueOptions? options = null) => ToolResultValueCodec.Write(this, options ?? ToolResultValueOptions.ExecutionBoundary);
    internal IEnumerable<KeyValuePair<string, object?>> OwnedProperties => _properties;
    internal object? OwnedContent => _properties.TryGetValue("content", out var value) ? value : null;
    internal static ToolResult FromProperties(ImmutableDictionary<string, object?> properties) => new(properties);
    private bool IsTrue(string name) => Property(name)?.Value.ValueKind == System.Text.Json.JsonValueKind.True;
    public void Deconstruct(out ImmutableArray<TextContent> Content, out JsonData Details, out bool IsError,
        out bool Terminate, out ToolFailure? Failure)
    { Content = this.Content; Details = this.Details; IsError = this.IsError; Terminate = this.Terminate; Failure = this.Failure; }

    public static ToolResult Success(string text, bool terminate = false) =>
        new([new TextContent(text)], JsonData.EmptyObject, Terminate: terminate);

    public static ToolResult Error(ToolFailureKind kind, string message, bool terminate = false) =>
        new([new TextContent(message)], JsonData.EmptyObject, Terminate: terminate, Failure: new(kind, message));
}

public sealed record ToolOutcome(ToolInvocation Invocation, ToolResult Result)
{
    public JsonData? NestedCalls { get; init; }
    public JsonData? NestedUsage { get; init; }
    /// <summary>Execution/after-hook error disposition, independent of the result object's own isError property.</summary>
    public bool IsError { get; init; } = Result.IsError;
    /// <summary>Milliseconds the tool's execution took, measured with a monotonic clock; null when the tool did not run.</summary>
    public long? DurationMs { get; init; }
}

/// <summary>Native transcript value, not a Pi wire serializer or a durable commit acknowledgement.</summary>
public sealed record ToolResultMessage
{
    private object _content = ImmutableArray<TextContent>.Empty;
    private JsonData _details = JsonData.EmptyObject;
    private bool _hasDetails;
    public ToolResultMessage(string ToolCallId, string ToolName, ImmutableArray<TextContent> Content, JsonData Details, bool IsError)
    { this.ToolCallId = ToolCallId; this.ToolName = ToolName; this.Content = Content; this.Details = Details; this.IsError = IsError; }
    public string ToolCallId { get; init; }
    public string ToolName { get; init; }
    public ImmutableArray<TextContent> Content
    {
        get => _content is ImmutableArray<TextContent> text ? text : ToolResultValueCodec.ReadContent((JsonData)_content);
        init => _content = value;
    }
    /// <summary>One owned canonical text/image array, shared with the legacy text accessor.</summary>
    public JsonData ContentValue
    {
        get => _content is ImmutableArray<TextContent> text ? ToolResultValueCodec.ContentJson(text) : (JsonData)_content;
        init => _content = ToolResultValueCodec.ExecutionContent(ToolResultValueCodec.AdmitContent(value));
    }
    public JsonData Details { get => _details; init { _details = value; _hasDetails = true; } }
    public bool HasDetails => _hasDetails;
    public JsonData? Usage { get; init; }
    /// <summary>Bounded record of brokered descendants; descendants are never independent transcript messages.</summary>
    public JsonData? NestedCalls { get; init; }
    public bool IsError { get; init; }
    /// <summary>Milliseconds the tool's execution took; absent for tools that did not run and legacy results.</summary>
    public long? DurationMs { get; init; }
    internal ToolResultValueOptions ValueOptions { get; init; } = ToolResultValueOptions.ExecutionBoundary;
    internal object OwnedContent => _content;
    internal ToolResultMessage WithoutDetails() => this with { _hasDetails = false };
    // The materializer already admitted this value under the caller's configured limits.
    internal ToolResultMessage WithOwnedContent(JsonData content) => this with { _content = content };
    public void Deconstruct(out string ToolCallId, out string ToolName, out ImmutableArray<TextContent> Content, out JsonData Details, out bool IsError)
    { ToolCallId = this.ToolCallId; ToolName = this.ToolName; Content = this.Content; Details = this.Details; IsError = this.IsError; }
}

public sealed record ToolBatchResult(
    ImmutableArray<ToolOutcome> Outcomes,
    ImmutableArray<ToolResultMessage> Messages,
    bool IsCanceled)
{
    public bool Terminate => Outcomes.Length > 0 && Outcomes.All(outcome => outcome.Result.Terminate);
    public bool ShouldContinue => Outcomes.Length > 0 && !Terminate && !IsCanceled;
}

public interface IToolExecutor
{
    ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken);

    ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, ToolProgressCallback onProgress,
        CancellationToken cancellationToken) => ExecuteAsync(invocation, cancellationToken);
}

public enum ToolProgressDeliveryMode { NativeAwaited, SourceCompatible }

/// <summary>Batch-wide limits include completed reports as well as outstanding delivery receipts.</summary>
public sealed record ToolProgressDeliveryOptions(ToolProgressDeliveryMode Mode = ToolProgressDeliveryMode.NativeAwaited,
    int MaximumUpdates = 4096, int MaximumPendingUpdates = 256, int MaximumRetainedCharacters = 8 * 1024 * 1024,
    int MaximumContentBlocks = 1024)
{
    internal void Validate()
    {
        if (!Enum.IsDefined(Mode) || MaximumUpdates <= 0 || MaximumPendingUpdates <= 0 || MaximumRetainedCharacters <= 0 || MaximumContentBlocks <= 0)
            throw new ArgumentOutOfRangeException(nameof(ToolProgressDeliveryOptions));
    }
}

/// <summary>Native mode awaits delivery; source mode returns immediately and joins admitted delivery at execution settlement.</summary>
public delegate ValueTask ToolProgressCallback(ToolResult partialResult, CancellationToken cancellationToken);

// One retained delivery per invocation, with no detached tasks or queue of callback work.
// Close rejects new reports and joins the admitted delivery even if a trusted executor did not await it.
internal sealed class ToolProgressScope
{
    private readonly ToolProgressCallback _publish;
    private readonly CancellationToken _lifetime;
    private readonly ToolProgressBudget? _budget;
    private readonly object _gate = new();
    private Delivery? _lastDelivery;
    private readonly List<Delivery> _pending = [];
    private Exception? _failure;
    private bool _closed, _active;
    private sealed class Delivery
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? Work;
    }

    public ToolProgressScope(ToolProgressCallback publish, CancellationToken lifetime, ToolProgressBudget? budget = null)
    { _publish = publish; _lifetime = lifetime; _budget = budget; }
    public bool IsSourceCompatible => _budget is not null;
    internal ToolProgressBudget? Budget => _budget;
    public Exception? DeliveryFailure { get { lock (_gate) return _failure; } }

    // A prepared adapter needs the actual outer delivery receipt even though its source-facing
    // callback returns immediately. Otherwise its result transforms could overtake listeners.
    public static ToolProgressScope Forward(ToolProgressCallback publish, Func<ToolResult, ToolResult> normalize,
        CancellationToken lifetime) => publish.Target is ToolProgressScope { _budget: not null } parent
        ? new((partial, token) => parent.ReportCore(normalize(partial), token, receipt: true, charged: true), lifetime, parent._budget)
        : new((partial, token) => publish(normalize(partial), token), lifetime);

    public ValueTask ReportAsync(ToolResult partialResult, CancellationToken cancellationToken) =>
        ReportCore(partialResult, cancellationToken, receipt: false, charged: false);

    internal ValueTask ReportReceiptAsync(ToolResult partialResult, CancellationToken cancellationToken) =>
        ReportCore(partialResult, cancellationToken, receipt: true, charged: false);

    private ValueTask ReportCore(ToolResult partialResult, CancellationToken cancellationToken, bool receipt, bool charged)
    {
        Delivery delivery;
        long characters = 0;
        lock (_gate)
        {
            if (_closed)
            {
                if (_budget is not null) return ValueTask.CompletedTask;
                throw new InvalidOperationException("Tool progress is closed.");
            }
            if (_budget is null)
            {
                if (_active) throw new InvalidOperationException("Tool progress must be awaited before another report.");
                if (_failure is not null) throw new InvalidOperationException("Tool progress has failed.");
                cancellationToken.ThrowIfCancellationRequested(); _lifetime.ThrowIfCancellationRequested();
            }
            else if (!charged)
            {
                try { characters = _budget.Admit(partialResult); }
                catch (Exception error) { _failure ??= error; throw; }
            }
            _active = true; _lastDelivery = delivery = new();
            if (_budget is not null) _pending.Add(delivery);
        }
        delivery.Work = DeliverAsync(delivery, partialResult, cancellationToken, characters, charged, receipt).AsTask();
        delivery.Started.TrySetResult();
        return _budget is not null && !receipt ? ValueTask.CompletedTask : new(delivery.Work!);
    }

    private async ValueTask DeliverAsync(Delivery delivery, ToolResult partialResult, CancellationToken cancellationToken,
        long characters, bool charged, bool receipt)
    {
        try
        {
            if (_budget is not null) await _publish(partialResult, _lifetime).ConfigureAwait(false);
            else
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime, cancellationToken);
                await _publish(partialResult, linked.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            lock (_gate) _failure ??= error;
            if (_budget is null || receipt) throw;
        }
        finally
        {
            lock (_gate) { _active = false; _pending.Remove(delivery); }
            if (_budget is not null && !charged) _budget.Release(characters);
        }
    }

    public async ValueTask CloseAsync()
    {
        Delivery? delivery;
        Delivery[] pending;
        lock (_gate) { _closed = true; delivery = _lastDelivery; pending = _pending.ToArray(); }
        if (_budget is not null)
        {
            foreach (var admitted in pending)
            {
                await admitted.Started.Task.ConfigureAwait(false);
                try { await admitted.Work!.ConfigureAwait(false); }
                catch (Exception error) { lock (_gate) _failure ??= error; }
            }
        }
        else if (delivery is not null)
        {
            await delivery.Started.Task.ConfigureAwait(false);
            await delivery.Work!.ConfigureAwait(false);
        }
        Exception? failure;
        lock (_gate) failure = _failure;
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

internal sealed class ToolProgressBudget(ToolProgressDeliveryOptions options, ToolResultValueOptions resultOptions)
{
    private readonly object _gate = new();
    private int _updates, _pending;
    private long _characters;
    public long Admit(ToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            if (_updates >= options.MaximumUpdates || _pending >= options.MaximumPendingUpdates)
                throw new InvalidOperationException("Tool progress limit reached.");
            // Reject exhausted quotas before parsing retained JSON; serialize admission work
            // across tools so validation itself cannot become an unbounded parallel allocation.
            // Structural/result admission uses the bounded execution profile. Progress quotas
            // below measure decoded retained characters and blocks, preserving their own error.
            ToolResultValueCodec.Validate(result, resultOptions);
            if (result.Details is null) throw new InvalidOperationException("Invalid tool progress result.");
            var content = result.ContentValue;
            if (content.Value.GetArrayLength() > options.MaximumContentBlocks) throw new InvalidOperationException("Tool progress limit reached.");
            long characters = result.Details.ToString().Length + (long)(result.StructuredContent?.ToString().Length ?? 0) +
                (result.Failure?.Message?.Length ?? 0) + ToolResultValueCodec.AdditionalCharacters(result);
            if (result.OwnedContent is ImmutableArray<TextContent> typed)
            {
                // Preserve legacy typed extra-field charging, including fields shadowed on the wire.
                foreach (var block in typed)
                {
                    characters += block.Text.Length;
                    if (block.ExtraProperties is { } fields)
                        foreach (var (name, value) in fields.Values) characters += name.Length + (long)value.ToString().Length;
                }
            }
            else characters += ToolResultValueCodec.ContentCharacters(content);
            if (characters > options.MaximumRetainedCharacters - _characters) throw new InvalidOperationException("Tool progress limit reached.");
            _updates++; _pending++; _characters += characters;
            return characters;
        }
    }
    public void Release(long characters) { lock (_gate) { _pending--; _characters -= characters; } }
}

public sealed record ToolDefinition(string Name, IToolExecutor Executor,
    ToolExecutionMode ExecutionMode = ToolExecutionMode.Parallel);

public sealed record ToolPreflightDecision(bool Block = false, string? Reason = null, bool Terminate = false)
{
    public static ToolPreflightDecision Allow { get; } = new();
}

/// <summary>Hook doubles for this prototype. This interface does not enforce authorization or schema validation.</summary>
public interface IToolHooks
{
    ValueTask<ToolPreflightDecision> BeforeExecutionAsync(ToolInvocation invocation, CancellationToken cancellationToken);
    ValueTask<ToolResult> AfterExecutionAsync(ToolInvocation invocation, ToolResult result, CancellationToken cancellationToken);
}
