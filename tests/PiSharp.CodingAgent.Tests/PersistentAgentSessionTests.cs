using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class PersistentAgentSessionTests
{
    private static readonly ModelDescriptor Model = new("durable-model", "openai-responses", "fixture-provider");
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("Persistent Agent writes real canonical tool turns and reopens complete history", DurableReopen),
        ("Persistent Agent input checkpoint precedes provider acquisition", InputBarrier),
        ("Persistent Agent assistant/tool checkpoints precede tool effects and next provider", ToolBarriers),
        ("Persistent Agent uncertain checkpoint poisons continuation with separate received state", UncertainAppend),
        ("Persistent Agent explicit leaf appends a branch without inserting sibling history", SelectedBranch),
        ("Persistent Agent rejects mismatched model/thinking/influences and closes failed opens", OpenAdmission),
        ("Persistent Agent cancellation and concurrent disposal await durable aborted settlement", CancellationSettlement),
        ("Persistent Agent subscriptions see acknowledged bytes and hold idle/end settlement", ObserverBarrier)
    ];

    private static async Task DurableReopen()
    {
        using var files = new Files();
        var ids = new Ids();
        var transport = new Script([Message(tool: true), Message(), Message()]);
        var effects = new Executor();
        var config = Configuration(transport, [new("read", effects)]);
        ImmutableArray<TranscriptEntry> retained;
        long acknowledged;
        await using (var session = await Create(files, config, ids))
        {
            Equal(files.Directory, session.WorkingDirectory);
            Equal(2, session.Snapshot.Log.Entries.Length);
            Equal("off", session.Snapshot.Context.ThinkingLevel);
            Equal(Model.Id, session.Snapshot.Context.Model!.ModelId);
            var result = await session.PromptAsync(Input("first"));
            Equal(2, result.Turns.Length);
            Equal(1, effects.Calls);
            Equal(6, session.Snapshot.Log.Entries.Length);
            Equal(4, session.Snapshot.Context.LlmMessages.Length);
            Equal("toolResult", session.Snapshot.Context.LlmMessages[2].Role);
            Equal("1.0", session.Snapshot.Context.LlmMessages[2].WireBody.Value.GetProperty("details").GetProperty("scale").GetRawText());
            retained = session.Snapshot.Context.LlmMessages;
            acknowledged = session.Snapshot.Log.CommittedByteLength;
            Equal(new FileInfo(files.Path).Length, acknowledged);
            await session.PromptAsync(Input("next"));
            Equal(6, session.Snapshot.Context.LlmMessages.Length);
            Prefix(retained, transport.Requests[2].Messages);
            Equal(4, retained.Length);
        }
        var resumed = new Script([Message()]);
        await using var opened = await PersistentAgentSession.OpenAsync(files.Path, Configuration(resumed), () => 123, ids.Next);
        Equal(6, opened.Snapshot.Agent.Messages.Length);
        Equal(6, opened.Snapshot.Context.LlmMessages.Length);
        Check(opened.Snapshot.Log.CommittedByteLength > acknowledged, "Later committed records disappeared.");
        var prior = opened.Snapshot.Context.LlmMessages;
        await opened.PromptAsync(Input("resumed"));
        Prefix(prior, resumed.Requests.Single().Messages);
        Equal(8, opened.Snapshot.Context.LlmMessages.Length);
        Equal(10, opened.Snapshot.Log.Entries.Length);
        Check(opened.Snapshot.Fault is null, "Successful resume was poisoned.");
    }

    private static async Task InputBarrier()
    {
        using var files = new Files();
        var storage = new ObservedFactory();
        var transport = new Script([Message()]);
        await using var session = await Create(files, Configuration(transport), new(), StorageOptions(storage));
        var old = session.Snapshot;
        var barrier = storage.Storage!.Arm();
        var run = session.PromptAsync(Input("durable before send"));
        try
        {
            await Within(barrier.Entered.Task);
            Equal(1, session.Snapshot.Agent.Messages.Length);
            Equal(0, session.Snapshot.Context.LlmMessages.Length);
            Equal(old.Log.CommittedByteLength, session.Snapshot.Log.CommittedByteLength);
            Equal(0, transport.Requests.Count);
            Check(!run.IsCompleted && !session.WaitForIdleAsync().IsCompleted, "Checkpoint did not hold generation settlement.");
            Check(new FileInfo(files.Path).Length > old.Log.CommittedByteLength, "Test did not perform a real file append.");
            barrier.Release.TrySetResult();
            await Within(run);
            Equal(1, transport.Requests.Count);
            Equal(2, session.Snapshot.Context.LlmMessages.Length);
        }
        finally { barrier.Release.TrySetResult(); await Ignore(run); }
    }

    private static async Task ToolBarriers()
    {
        using var files = new Files();
        var storage = new ObservedFactory();
        var transport = new Script([Message(tool: true), Message()]);
        var executor = new Executor();
        await using var session = await Create(files, Configuration(transport, [new("read", executor)]), new(), StorageOptions(storage));
        var assistant = storage.Storage!.Arm(skipCheckpoints: 1);
        var run = session.PromptAsync(Input("tools"));
        Barrier? result = null;
        try
        {
            await Within(assistant.Entered.Task);
            Equal(2, session.Snapshot.Agent.Messages.Length);
            Equal(1, session.Snapshot.Context.LlmMessages.Length);
            Equal(0, executor.Calls); Equal(1, transport.Requests.Count);
            result = storage.Storage.Arm();
            assistant.Release.TrySetResult();
            await Within(result.Entered.Task);
            Equal(1, executor.Calls);
            Equal(1, transport.Requests.Count);
            Equal("toolResult", session.Snapshot.Agent.Messages[^1].Role);
            Equal("assistant", session.Snapshot.Context.LlmMessages[^1].Role);
            result.Release.TrySetResult();
            await Within(run);
            Equal(2, transport.Requests.Count);
            Equal("toolResult", transport.Requests[1].Messages[^1].Role);
            Equal(4, session.Snapshot.Context.LlmMessages.Length);
        }
        finally { assistant.Release.TrySetResult(); result?.Release.TrySetResult(); await Ignore(run); }
    }

    private static async Task UncertainAppend()
    {
        using var files = new Files();
        var storage = new ObservedFactory();
        var transport = new Script([Message(tool: true)]);
        var executor = new Executor();
        var ids = new Ids();
        var session = await Create(files, Configuration(transport, [new("read", executor)]), ids, StorageOptions(storage));
        var barrier = storage.Storage!.Arm(skipCheckpoints: 1, fail: true);
        var run = session.PromptAsync(Input("private-input-marker"));
        try
        {
            await Within(barrier.Entered.Task);
            var before = session.Snapshot.Log.CommittedByteLength;
            barrier.Release.TrySetResult();
            var error = await ThrowsAsync<PersistentAgentSessionException>(() => run);
            Equal(PersistentAgentSessionFailure.AppendFailed, error.Fault.Failure);
            Equal(SessionLogStoreFailure.CheckpointFailed, error.Fault.StorageFailure);
            Check(error.Fault.MayHaveWritten && error.Fault.DurableFlushCompleted, "Checkpoint uncertainty flags were lost.");
            Check(!error.ToString().Contains("private", StringComparison.Ordinal), "Storage failure leaked rejected payload.");
            Equal(before, session.Snapshot.Log.CommittedByteLength);
            Equal(2, session.Snapshot.Agent.Messages.Length);
            Equal(1, session.Snapshot.Context.LlmMessages.Length);
            Equal(0, executor.Calls);
            var rejected = await ThrowsAsync<PersistentAgentSessionException>(() => session.PromptAsync(Input("retry")));
            Equal(PersistentAgentSessionFailure.Faulted, rejected.Fault.Failure);
            Equal(1, transport.Requests.Count);
        }
        finally { barrier.Release.TrySetResult(); await Ignore(run); await session.DisposeAsync(); }
        // Explicit reopen inspects durable bytes; it does not execute the recorded tool call.
        var unused = new Script([Message()]);
        await using var reopened = await PersistentAgentSession.OpenAsync(files.Path, Configuration(unused), () => 123, ids.Next);
        Equal(2, reopened.Snapshot.Context.LlmMessages.Length);
        Equal("assistant", reopened.Snapshot.Context.LlmMessages[^1].Role);
        Equal(0, unused.Requests.Count); Equal(0, executor.Calls);
    }

    private static async Task SelectedBranch()
    {
        using var files = new Files();
        await Seed(files, [Entry("a", null, Input("branch-a")), Entry("b", null, Input("sibling-b"))]);
        var ids = new Ids();
        var transport = new Script([Message()]);
        await using (var session = await PersistentAgentSession.OpenAsync(files.Path, Configuration(transport), () => 123, ids.Next,
            new(UseLatestLeaf: false, SelectedLeafId: "a")))
        {
            Equal("b", session.Snapshot.Log.LeafId); Equal("a", session.Snapshot.Context.LeafId);
            Equal(1, session.Snapshot.Context.LlmMessages.Length);
            await session.PromptAsync(Input("new branch-a"));
            Equal("a", session.Snapshot.Log.Entries[2].ParentId);
            Check(transport.Requests.Single().Messages.All(message => !message.WireBody.ToString().Contains("sibling-b", StringComparison.Ordinal)), "Sibling leaked into model history.");
            Equal(4, session.Snapshot.Log.Entries.Length);
        }
        await using var root = await PersistentAgentSession.OpenAsync(files.Path, Configuration(new Script([Message()])), () => 123, ids.Next,
            new(UseLatestLeaf: false));
        Check(root.Snapshot.Context.LlmMessages.IsEmpty && root.Snapshot.Context.Ancestry.IsEmpty, "Explicit null leaf selected physical latest.");
        Equal(4, root.Snapshot.Log.Entries.Length);
        await root.PromptAsync(Input("new root"));
        Check(root.Snapshot.Log.Entries[4].ParentId is null, "Root branch inherited unrelated ancestry.");
    }

    private static async Task OpenAdmission()
    {
        using var files = new Files();
        var model = Meta("model_change", "model", null, new { provider = "other", modelId = "different" });
        var level = Meta("thinking_level_change", "thinking", null, new { thinkingLevel = "high" });
        var influence = Meta("context_edit", "edit", null, new { targetId = "none", replacement = (object?)null });
        await Seed(files, [model, level, influence]);
        var original = await File.ReadAllBytesAsync(files.Path);
        var transport = new Script([Message()]);
        var ids = new Ids();
        var mismatch = await ThrowsAsync<PersistentAgentSessionException>(() => PersistentAgentSession.OpenAsync(files.Path,
            Configuration(transport), () => 123, ids.Next, new(UseLatestLeaf: false, SelectedLeafId: "model")));
        Equal(PersistentAgentSessionFailure.ModelMismatch, mismatch.Fault.Failure);
        var thinking = await ThrowsAsync<PersistentAgentSessionException>(() => PersistentAgentSession.OpenAsync(files.Path,
            Configuration(transport), () => 123, ids.Next, new(UseLatestLeaf: false, SelectedLeafId: "thinking")));
        Equal(PersistentAgentSessionFailure.UnsupportedThinkingLevel, thinking.Fault.Failure);
        await using (var inertEdit = await PersistentAgentSession.OpenAsync(files.Path, Configuration(transport), () => 123, ids.Next))
        {
            Equal(0, inertEdit.Snapshot.Context.Messages.Length);
            Equal(3, inertEdit.Snapshot.Log.Entries.Length);
        }
        var missing = await ThrowsAsync<SessionContextProjectionException>(() => PersistentAgentSession.OpenAsync(files.Path,
            Configuration(transport), () => 123, ids.Next, new(UseLatestLeaf: false, SelectedLeafId: "absent")));
        Equal(SessionContextProjectionFailure.MissingLeaf, missing.Failure);
        var afterRejectedOpens = await File.ReadAllBytesAsync(files.Path);
        Check(original.SequenceEqual(afterRejectedOpens), "Rejected opens mutated imported log.");
        Equal(0, transport.Requests.Count);
        // Every failed open released the actual writer lease.
        await using var valid = await PersistentAgentSession.OpenAsync(files.Path, Configuration(transport), () => 123, ids.Next,
            new(UseLatestLeaf: false));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => valid.PromptAsync(Input("canceled"), canceled.Token));
        Equal(0, transport.Requests.Count);
        var notCreated = System.IO.Path.Combine(files.Directory, "invalid.jsonl");
        await ThrowsAsync<PersistentAgentSessionException>(() => PersistentAgentSession.CreateAsync(notCreated, Header(files),
            Configuration(transport), () => 123, ids.Next, new(AgentOptions: new(CancellationBehavior: AgentCancellationBehavior.Propagate))));
        Check(!File.Exists(notCreated), "Invalid settlement profile acquired a file.");
    }

    private static async Task CancellationSettlement()
    {
        using var files = new Files();
        var storage = new ObservedFactory();
        var transport = new Script([Message()], block: true, gateCleanup: true);
        var ids = new Ids();
        var session = await Create(files, Configuration(transport), ids, StorageOptions(storage));
        var run = session.PromptAsync(Input("abort"));
        Barrier? abortCommit = null;
        try
        {
            await Within(transport.Started.Task);
            abortCommit = storage.Storage!.Arm();
            Check(session.Abort(), "Active abort was rejected.");
            await Within(transport.CleanupEntered.Task);
            var firstDispose = session.DisposeAsync().AsTask();
            var secondDispose = session.DisposeAsync().AsTask();
            Check(!firstDispose.IsCompleted && !secondDispose.IsCompleted && !run.IsCompleted, "Disposal bypassed owned provider cleanup.");
            transport.CleanupRelease.TrySetResult();
            await Within(abortCommit.Entered.Task);
            Check(!firstDispose.IsCompleted && !secondDispose.IsCompleted, "Disposal closed writer before abort checkpoint.");
            Equal(1, session.Snapshot.Context.LlmMessages.Length);
            Equal(2, session.Snapshot.Agent.Messages.Length);
            abortCommit.Release.TrySetResult();
            var result = await Within(run);
            await Within(Task.WhenAll(firstDispose, secondDispose));
            Equal(AgentLoopStopReason.ChatFailure, result.Reason);
            Equal(StopReason.Aborted, PiWireJson.ReadMessage(session.Snapshot.Context.LlmMessages[^1].WireBody.Value).StopReason);
            Equal(1, transport.Cleanups);
            Equal(1, storage.Storage.Disposals);
            Equal(new FileInfo(files.Path).Length, session.Snapshot.Log.CommittedByteLength);
        }
        finally { transport.CleanupRelease.TrySetResult(); abortCommit?.Release.TrySetResult(); await Ignore(run); await session.DisposeAsync(); }
        await using var reopened = await PersistentAgentSession.OpenAsync(files.Path, Configuration(new Script([Message()])), () => 123, ids.Next);
        Equal(StopReason.Aborted, PiWireJson.ReadMessage(reopened.Snapshot.Agent.Messages[^1].WireBody.Value).StopReason);
    }

    private static async Task ObserverBarrier()
    {
        using var files = new Files();
        await using var session = await Create(files, Configuration(new Script([Message()])), new());
        var entered = Gate(); var release = Gate();
        using var subscription = session.Subscribe(new Sink(async (observation, token) =>
        {
            if (observation is AssistantMessageEnded)
            {
                Equal("assistant", session.Snapshot.Context.LlmMessages[^1].Role);
                Equal(new FileInfo(files.Path).Length, session.Snapshot.Log.CommittedByteLength);
            }
            if (observation is AgentLoopEnded)
            {
                Check(!token.IsCancellationRequested, "Terminal observer inherited canceled work.");
                Throws<InvalidOperationException>(() => session.WaitForIdleAsync());
                Throws<InvalidOperationException>(() => session.DisposeAsync());
                entered.TrySetResult(); await release.Task;
            }
        }));
        var run = session.PromptAsync(Input("listeners"));
        try
        {
            await Within(entered.Task);
            var idle = session.WaitForIdleAsync();
            Check(!run.IsCompleted && !idle.IsCompleted, "Public listener did not hold coordinator settlement.");
            Throws<InvalidOperationException>(() => session.PromptAsync(Input("busy")));
            release.TrySetResult(); await Within(run); await Within(idle);
        }
        finally { release.TrySetResult(); await Ignore(run); }
    }

    private static AgentConfiguration Configuration(Script transport, ImmutableArray<ToolDefinition> tools = default) =>
        new(Model, transport, tools.IsDefault ? [] : tools);
    private static Task<PersistentAgentSession> Create(Files files, AgentConfiguration configuration, Ids ids,
        PersistentAgentSessionOptions? options = null) =>
        PersistentAgentSession.CreateAsync(files.Path, Header(files), configuration, () => 123, ids.Next, options);
    private static PersistentAgentSessionOptions StorageOptions(ObservedFactory storage) =>
        new(SessionLogStoreOptions: new(StorageFactory: storage));
    private static SessionEntry Header(Files files) => new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
    { type = "session", version = 3, id = "header", timestamp = "2026-10-01T00:00:00.000Z", cwd = files.Directory, future = (object?)null }));
    private static SessionEntry Entry(string id, string? parent, TranscriptEntry message) =>
        new SessionEntryCodec().Parse("""{"type":"message","id":""" + JsonSerializer.Serialize(id) +
            ""","parentId":""" + JsonSerializer.Serialize(parent) + ""","timestamp":"2026-10-01T00:00:00.000Z","message":""" +
            message.WireBody.Value.GetRawText() + "}");
    private static SessionEntry Meta(string type, string id, string? parent, object fields)
    {
        var extra = JsonSerializer.SerializeToElement(fields);
        return new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type, id, parentId = parent, timestamp = "2026-10-01T00:00:00.000Z" })[..^1] +
            "," + extra.GetRawText()[1..]);
    }
    private static async Task Seed(Files files, ImmutableArray<SessionEntry> entries)
    {
        await using var store = await SessionLogStore.CreateNewAsync(files.Path, Header(files));
        await store.AppendAsync(entries);
    }
    private static TranscriptEntry Input(string text) => new("user", JsonData.Parse(
        """{"role":"user","content":""" + JsonSerializer.Serialize(text) + ""","timestamp":123,"future":{"scale":1.0,"null":null}}"""));
    private static AssistantMessage Message(bool tool = false) => new(Model.Api, Model.Provider, Model.Id, 123,
        tool ? [new ToolCallContent("call-read", "read", JsonData.Parse("""{"path":"/fake","n":1.0}"""))] : [new TextContent("done")],
        TokenUsage.Zero, tool ? StopReason.ToolUse : StopReason.Stop);
    private sealed class Ids
    {
        private int _value;
        public string Next() => "entry-" + Interlocked.Increment(ref _value);
    }
    private sealed class Files : IDisposable
    {
        public string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PiSharp-persistent-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Directory, "session.jsonl");
        public Files() => System.IO.Directory.CreateDirectory(Directory);
        public void Dispose()
        {
            var target = System.IO.Path.GetFullPath(Directory);
            var parent = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
            if (System.IO.Path.GetDirectoryName(target) != parent || !System.IO.Path.GetFileName(target).StartsWith("PiSharp-persistent-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the owned session test directory.");
            System.IO.Directory.Delete(target, recursive: true);
        }
    }
    private sealed class Script(AssistantMessage[] messages, bool block = false, bool gateCleanup = false) : IChatTransport
    {
        public readonly List<ChatRequest> Requests = [];
        public readonly TaskCompletionSource Started = Gate(), CleanupEntered = Gate(), CleanupRelease = Gate();
        public int Cleanups;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            var final = messages[Requests.Count]; Requests.Add(request);
            try
            {
                yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
                Started.TrySetResult();
                if (block) await Gate().Task.WaitAsync(token);
                for (var index = 0; index < final.Content.Length; index++)
                    if (final.Content[index] is ToolCallContent call)
                    { yield return new ToolCallStarted(index, call with { Arguments = JsonData.EmptyObject }); yield return new ToolCallEnded(index, call); }
                    else if (final.Content[index] is TextContent text)
                    { yield return new TextStarted(index, new("")); yield return new TextEnded(index, text.Text); }
                yield return new StreamDone(final.StopReason, final);
            }
            finally
            {
                CleanupEntered.TrySetResult();
                if (gateCleanup) await CleanupRelease.Task;
                Cleanups++;
            }
        }
    }
    private sealed class Executor : IToolExecutor
    {
        public int Calls;
        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; return ValueTask.FromResult(new ToolResult([new("effect")], JsonData.Parse("""{"scale":1.0,"opaque":null}"""))); }
    }
    private sealed class Sink(Func<AgentEvent, CancellationToken, ValueTask> callback) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) => callback(observation, token); }
    private sealed class Barrier(bool fail)
    {
        public readonly TaskCompletionSource Entered = Gate(), Release = Gate();
        public async ValueTask WaitAsync()
        {
            Entered.TrySetResult(); await Release.Task;
            if (fail) throw new IOException("private storage marker");
        }
    }
    private sealed class ObservedFactory : ISessionLogStorageFactory
    {
        public ObservedStorage? Storage;
        public async ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token) =>
            Storage = new(await SessionLogStore.DefaultStorageFactory.OpenAsync(path, createNew, token));
    }
    private sealed class ObservedStorage(ISessionLogStorage inner) : ISessionLogStorage
    {
        private readonly object _gate = new();
        private Barrier? _barrier;
        private int _skip;
        public int Disposals;
        public Stream ReadStream => inner.ReadStream;
        public SessionLogStorageDurability Durability => inner.Durability;
        public long Length => inner.Length;
        public void PositionForAppend(long expected) => inner.PositionForAppend(expected);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes) => inner.WriteAsync(bytes);
        public ValueTask FlushAsync() => inner.FlushAsync();
        public void FlushToDisk() => inner.FlushToDisk();
        public Barrier Arm(int skipCheckpoints = 0, bool fail = false)
        { lock (_gate) { _skip = skipCheckpoints; return _barrier = new(fail); } }
        public async ValueTask BeforeCheckpointAsync()
        {
            Barrier? barrier = null;
            lock (_gate)
            {
                if (_barrier is not null && _skip-- <= 0) { barrier = _barrier; _barrier = null; }
            }
            await inner.BeforeCheckpointAsync();
            if (barrier is not null) await barrier.WaitAsync();
        }
        public async ValueTask DisposeAsync() { Disposals++; await inner.DisposeAsync(); }
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Within(Task task) => await task.WaitAsync(TimeSpan.FromSeconds(10));
    private static async Task<T> Within<T>(Task<T> task) => await task.WaitAsync(TimeSpan.FromSeconds(10));
    private static async Task Ignore(Task task) { try { await Within(task); } catch (Exception) { } }
    private static void Prefix(ImmutableArray<TranscriptEntry> expected, ImmutableArray<TranscriptEntry> actual)
    { Check(actual.Length >= expected.Length, "History was truncated."); for (var i = 0; i < expected.Length; i++) Equal(expected[i].WireBody.Value.GetRawText(), actual[i].WireBody.Value.GetRawText()); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Values differ.");
    private static void Throws<T>(Action run) where T : Exception
    { try { run(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task<T> ThrowsAsync<T>(Func<Task> run) where T : Exception
    { try { await Within(run()); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
