using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class RuntimeToolProgressTests
{
    private static readonly ModelDescriptor Model = new("runtime-progress-model", "openai-responses", "authored-provider");
    private static readonly JsonData Declaration = JsonData.Parse("""{"name":"probe","description":"Authored progress probe","parameters":{"type":"object","additionalProperties":false}}""");
    private static readonly ToolResult Partial = new([new("partial\0\U0001F642")], JsonData.Parse("""{"phase":"live","scale":1.00,"nil":null}"""))
        { StructuredContent = JsonData.Parse("""{"preview":"live\u0000","ordered":[2,1],"nil":null}""") };
    private static readonly ToolResult Final = new([new("final\0\U0001F642")], JsonData.Parse("""{"truncation":{"content":"final\u0000\uD83D\uDE42"},"scale":1.0,"nil":null}"""))
        { StructuredContent = JsonData.Parse("""{"output":"final\u0000\uD83D\uDE42","exit_code":0,"opaque":null}""") };

    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("Runtime named adapter forwards owned awaited progress through Agent durable next request", DurableForwarding),
        ("Runtime named adapter sink failure and cancellation await actual executor cleanup", CancellationAndSinkFailure),
        ("Runtime named adapter legacy fallback preserves final metadata and mandatory policy limits", LegacyPolicyAndLimits)
    ];

    private static async Task DurableForwarding()
    {
        using var files = new Files();
        var adapter = new ReportingAdapter(); var transport = new Script(); var policy = new Policy();
        var registry = Registry(transport, adapter, policy);
        adapter.CurrentName = "changed-trusted-getter";
        var session = await Create(files, registry,
            new(ProgressDelivery: new(Mode: ToolProgressDeliveryMode.NativeAwaited)));
        var updateEntered = Gate(); var updateRelease = Gate(); var updates = new List<ToolExecutionUpdated>(); var ends = new List<ToolExecutionEnded>();
        using var subscription = session.Subscribe(new Sink(async (observation, _) =>
        {
            if (observation is ToolExecutionUpdated update)
            {
                updates.Add(update);
                Equal("probe", update.Invocation.Call.Name);
                Check(ReferenceEquals(Partial.Details, update.PartialResult.Details) &&
                    ReferenceEquals(Partial.StructuredContent, update.PartialResult.StructuredContent), "Registry changed owned partial metadata.");
                Equal("partial\0\U0001F642", update.PartialResult.Content.Single().Text);
                Equal("1.00", update.PartialResult.Details.Value.GetProperty("scale").GetRawText());
                updateEntered.TrySetResult(); await updateRelease.Task;
            }
            if (observation is ToolExecutionEnded end) ends.Add(end);
        }));
        var running = session.PromptAsync(User("run forwarded tool"));
        try
        {
            await Within(updateEntered.Task);
            var idle = session.WaitForIdleAsync();
            Equal(1, adapter.ProgressCalls); Equal(0, adapter.LegacyCalls); Equal(0, adapter.AfterProgress);
            Equal(1, transport.Requests.Count); Equal(0, ends.Count);
            Check(!running.IsCompleted && !idle.IsCompleted, "Finalization or next provider escaped the actual awaited listener.");
            Check(session.Snapshot.Context.LlmMessages[^1].Role == "assistant", "Progress replaced the canonical assistant barrier.");
            var acknowledged = await ReadAcknowledged(files.Path);
            Check(acknowledged.SourceComplete && acknowledged.Status == SessionLogReadStatus.Complete &&
                acknowledged.ValidatedPrefix.Length - 1 == session.Snapshot.Log.Entries.Length, "Listener saw unacknowledged durable state.");
            updateRelease.TrySetResult();
            var result = await Within(running);
            Equal(2, result.Turns.Length); Equal(1, updates.Count); Equal(1, ends.Count);
            Equal(1, adapter.AfterProgress); Equal(1, adapter.Cleanups);
            Check(ReferenceEquals(policy.Action, adapter.Action), "Registration changed final-action policy identity.");
            Check(ReferenceEquals(Final.StructuredContent, ends[0].Outcome.Result.StructuredContent), "Final programmatic output changed.");
            Equal(Final.Details.ToString(), ends[0].Outcome.Result.Details.ToString());
            var next = transport.Requests[1].Messages.Single(message => message.Role == "toolResult").WireBody.Value;
            Equal("final\0\U0001F642", next.GetProperty("content")[0].GetProperty("text").GetString());
            Equal("1.0", next.GetProperty("details").GetProperty("scale").GetRawText());
            Check(!next.TryGetProperty("structuredContent", out _) && !next.GetRawText().Contains("preview", StringComparison.Ordinal),
                "Progress/programmatic metadata entered the canonical next request.");
            var finalLog = await ReadAcknowledged(files.Path);
            Equal(new FileInfo(files.Path).Length, session.Snapshot.Log.CommittedByteLength);
            Check(finalLog.ValidatedPrefix.Any(record => record.Entry.Type == "message" &&
                record.Entry.WireBody.Value.GetProperty("message").GetProperty("role").GetString() == "toolResult"),
                "Actual final tool message was not acknowledged on disk.");
        }
        finally
        {
            updateRelease.TrySetResult(); adapter.CleanupRelease.TrySetResult(); session.Abort();
            await Ignore(running); await session.DisposeAsync();
        }
        await using var reopened = await SessionLogStore.OpenAsync(files.Path);
        Equal(new FileInfo(files.Path).Length, reopened.Snapshot.CommittedByteLength);
    }

    private static async Task CancellationAndSinkFailure()
    {
        foreach (var failSink in new[] { false, true })
        {
            using var files = new Files();
            var adapter = new ReportingAdapter { BlockAfterProgress = true, HoldCleanup = true };
            var transport = new Script(); var registry = Registry(transport, adapter, new Policy());
            var session = await Create(files, registry);
            var entered = Gate(); var release = Gate();
            using var subscription = session.Subscribe(new Sink(async (observation, _) =>
            {
                if (observation is not ToolExecutionUpdated) return;
                entered.TrySetResult();
                await release.Task;
                if (failSink) throw new IOException("authored failing progress listener");
            }));
            var running = session.PromptAsync(User("join forwarded work"));
            try
            {
                await Within(entered.Task); Equal(1, adapter.ProgressCalls); Equal(0, adapter.LegacyCalls);
                if (!failSink)
                {
                    Check(session.Abort(), "Actual Agent abort was not admitted.");
                    Check(!running.IsCompleted && !session.WaitForIdleAsync().IsCompleted, "Cancel skipped the already admitted listener.");
                }
                release.TrySetResult();
                await Within(adapter.CleanupEntered.Task);
                Check(!running.IsCompleted && !session.WaitForIdleAsync().IsCompleted, "Publication failure/cancel returned before owned executor cleanup.");
                Equal(1, transport.Requests.Count); Equal(0, adapter.AfterProgress);
                adapter.CleanupRelease.TrySetResult();
                if (failSink)
                {
                    await Failed(running);
                    Check(running.IsFaulted && session.Snapshot.Fault?.Failure == PersistentAgentSessionFailure.RunFailed,
                        "Failed listener was converted into a successful session generation.");
                }
                else
                {
                    await IgnoreCancellation(running);
                    Check(session.Snapshot.Agent.CancellationRequested, "Work lost actual cancellation identity.");
                }
                Equal(1, adapter.Cleanups); Equal(1, transport.Requests.Count);
                Check(!session.Snapshot.Agent.IsRunning, "Generation did not settle after actual cleanup.");
                var read = await ReadAcknowledged(files.Path);
                Check(read.SourceComplete && read.Status == SessionLogReadStatus.Complete, "Failure/cancel damaged the acknowledged log.");
                Check(read.ValidatedPrefix.Any(record => record.Entry.Type == "message" &&
                    record.Entry.WireBody.Value.GetProperty("message").GetProperty("role").GetString() == "assistant"),
                    "Actual pre-tool assistant commit disappeared.");
            }
            finally
            {
                release.TrySetResult(); adapter.CleanupRelease.TrySetResult(); session.Abort();
                await Ignore(running); await session.DisposeAsync();
            }
            await using var reopened = await SessionLogStore.OpenAsync(files.Path);
            Equal(new FileInfo(files.Path).Length, reopened.Snapshot.CommittedByteLength);
        }
    }

    private static async Task LegacyPolicyAndLimits()
    {
        var legacy = new LegacyAdapter(); var transport = new Script(); var policy = new Policy();
        var registry = Registry(transport, legacy, policy);
        var observations = new List<AgentEvent>();
        var sink = new Sink((observation, _) => { observations.Add(observation); return ValueTask.CompletedTask; });
        var tools = registry.Resolve(Model, [SystemEntry()]).Configuration.Tools;
        var batch = await new ToolBatchScheduler(tools, executionMode: ToolExecutionMode.Sequential).RunAsync(Message(tool: true), sink);
        Equal(1, legacy.Calls); Equal(0, observations.OfType<ToolExecutionUpdated>().Count());
        Equal(1, observations.OfType<ToolExecutionEnded>().Count());
        Check(ReferenceEquals(Final.StructuredContent, batch.Outcomes.Single().Result.StructuredContent) &&
            ReferenceEquals(Final.Details, batch.Outcomes.Single().Result.Details), "Legacy final metadata changed through the named wrapper.");
        Check(!batch.Messages.Single().IsError, "Legacy fallback synthesized an error.");

        var denied = new ReportingAdapter(); var denial = new Policy { Allow = false };
        var deniedTools = Registry(transport, denied, denial).Resolve(Model, [SystemEntry()]).Configuration.Tools;
        var deniedBatch = await new ToolBatchScheduler(deniedTools).RunAsync(Message(tool: true), sink);
        Equal(ToolFailureKind.Blocked, deniedBatch.Outcomes.Single().Result.Failure?.Kind);
        Equal(0, denied.ProgressCalls); Equal(0, denied.LegacyCalls);
        Check(denial.Action is not null && ReferenceEquals(denial.Action, denied.Action),
            "Policy did not receive the prepared final action.");

        var narrow = new ReportingAdapter();
        var narrowRegistry = Registry(transport, narrow, new Policy(),
            new(ToolInvokerOptions: new(MaximumResultCharacters: 1)));
        var before = observations.OfType<ToolExecutionUpdated>().Count();
        var narrowBatch = await new ToolBatchScheduler(narrowRegistry.Resolve(Model, [SystemEntry()]).Configuration.Tools)
            .RunAsync(Message(tool: true), sink);
        Equal(1, narrow.ProgressCalls); Equal(0, narrow.LegacyCalls); Equal(1, narrow.Cleanups);
        Equal(before, observations.OfType<ToolExecutionUpdated>().Count());
        Equal(ToolFailureKind.ExecutionError, narrowBatch.Outcomes.Single().Result.Failure?.Kind);
    }

    private static SessionRuntimeRegistry Registry(Script transport, IPreparedToolAdapter adapter, Policy policy,
        SessionRuntimeRegistryOptions? options = null) =>
        new([new(Model, transport, ExecutionMode: ToolExecutionMode.Sequential)], [new(Declaration, adapter)], policy, options);
    private static async Task<PersistentAgentSession> Create(Files files, SessionRuntimeRegistry registry,
        AgentOptions? agentOptions = null)
    {
        var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
        { type = "session", version = 3, id = "runtime-progress-header", timestamp = "2026-10-01T00:00:00.000Z", cwd = files.Root }));
        var session = await PersistentAgentSession.CreateAsync(files.Path, header, registry, Model, () => 123, files.NextId,
            options: new(AgentOptions: agentOptions));
        try { await session.ConfigureAsync(new(SystemMessage: SystemEntry())); return session; }
        catch { await session.DisposeAsync(); throw; }
    }
    private static TranscriptEntry SystemEntry() => new("system", JsonData.Parse(JsonSerializer.Serialize(new
    { role = "system", content = "Explicit trusted progress tool", timestamp = 123, toolsAdded = new[] { Declaration.Value } })));
    private static TranscriptEntry User(string text) => new("user", JsonData.Parse(JsonSerializer.Serialize(new { role = "user", content = text, timestamp = 123 })));
    private static AssistantMessage Message(bool tool) => new(Model.Api, Model.Provider, Model.Id, 123,
        tool ? [new ToolCallContent("call-probe", "probe", JsonData.EmptyObject)] : [new TextContent("after acknowledged final")],
        TokenUsage.Zero, tool ? StopReason.ToolUse : StopReason.Stop);
    private static PreparedToolAction Prepare(ToolInvocation invocation) =>
        new("probe", "probe", PreparedToolActionKind.Path, "/authored-inert-target", invocation.Call.Arguments, [], null,
            ImmutableDictionary<string, string>.Empty);

    private sealed class ReportingAdapter : IPreparedToolAdapter
    {
        public string CurrentName = "probe";
        public string Name => CurrentName;
        public bool BlockAfterProgress, HoldCleanup;
        public int LegacyCalls, ProgressCalls, AfterProgress, Cleanups;
        public PreparedToolAction? Action;
        public readonly TaskCompletionSource CleanupEntered = Gate(), CleanupRelease = Gate();
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token)
        { token.ThrowIfCancellationRequested(); var action = Prepare(invocation); Action = action; return ValueTask.FromResult(action); }
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(ReferenceEquals(action, Action)); }
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); LegacyCalls++; return ValueTask.FromResult(Final); }
        public async ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, ToolProgressCallback progress, CancellationToken token)
        {
            ProgressCalls++;
            try
            {
                await progress(Partial, token); token.ThrowIfCancellationRequested();
                if (BlockAfterProgress) await Gate().Task.WaitAsync(token);
                AfterProgress++; return Final;
            }
            finally
            {
                CleanupEntered.TrySetResult();
                if (HoldCleanup) await CleanupRelease.Task;
                Cleanups++;
            }
        }
    }
    private sealed class LegacyAdapter : IPreparedToolAdapter
    {
        public string Name => "probe";
        public int Calls;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(Prepare(invocation));
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(true);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; return ValueTask.FromResult(Final); }
    }
    private sealed class Policy : IToolActionPolicy
    {
        public bool Allow = true;
        public PreparedToolAction? Action;
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Action = action; return ValueTask.FromResult(new ToolActionAuthorization(Allow)); }
    }
    private sealed class Sink(Func<AgentEvent, CancellationToken, ValueTask> observe) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) => observe(observation, token); }
    private sealed class Script : IChatTransport
    {
        public readonly List<ChatRequest> Requests = [];
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var final = Message(tool: Requests.Count == 0); Requests.Add(request);
            yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
            if (final.Content[0] is ToolCallContent call)
            { yield return new ToolCallStarted(0, call with { Arguments = JsonData.EmptyObject }); yield return new ToolCallEnded(0, call); }
            else { yield return new TextStarted(0, new("")); yield return new TextEnded(0, "after acknowledged final"); }
            await Task.CompletedTask;
            yield return new StreamDone(final.StopReason, final);
        }
    }
    private sealed class Files : IDisposable
    {
        private readonly string _parent = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
        private int _id;
        public string Root { get; }
        public string Path => System.IO.Path.Combine(Root, "session.jsonl");
        public string NextId() => "runtime-progress-" + Interlocked.Increment(ref _id);
        public Files() { Root = System.IO.Path.Combine(_parent, "pisharp-runtime-progress-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); }
        public void Dispose()
        {
            var target = System.IO.Path.GetFullPath(Root);
            if (System.IO.Path.GetDirectoryName(target) != _parent || !System.IO.Path.GetFileName(target).StartsWith("pisharp-runtime-progress-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing unowned runtime progress cleanup.");
            Directory.Delete(target, recursive: true);
        }
    }
    private static async Task<SessionLogReadResult> ReadAcknowledged(string path)
    {
        // The coordinator retains a live ReadWrite writer lease. Inspect only at a held
        // event barrier/settled run, with sharing compatible with that existing owner.
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await new SessionLogReader().ReadAsync(source);
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Within(Task work) => await work.WaitAsync(TimeSpan.FromSeconds(6));
    private static async Task<T> Within<T>(Task<T> work) => await work.WaitAsync(TimeSpan.FromSeconds(6));
    private static async Task Failed(Task work)
    { try { await Within(work); } catch (IOException) { return; } throw new InvalidOperationException("Expected actual progress sink failure."); }
    private static async Task IgnoreCancellation(Task work) { try { await Within(work); } catch (OperationCanceledException) { } }
    private static async Task Ignore(Task work) { try { await Within(work); } catch (Exception) { } }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Values differ.");
}
