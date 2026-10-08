using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;
using NativeAgent = PiSharp.Agent.Agent;

internal static class PendingInputQueueSnapshotTests
{
    private static readonly ModelDescriptor Model = new("queue-snapshot", "openai-responses", "fixture-provider");
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("full queue snapshot preserves both FIFO contents beyond mode-selected peek and cancellation", FullQueues),
        ("atomic full queue clears partition all concurrently admitted immutable messages", ConcurrentClears),
        ("Agent full clear excludes already-drained generation inputs and supports awaited callbacks", AgentRunning),
        ("durable coordinator queue snapshot/clear preserves history and last-end settlement", DurableRunning),
        ("faulted/disposed coordinator queues remain observable while mutation is rejected", FaultAndDisposal)
    ];

    private static Task FullQueues()
    {
        var first = Input("s1"); var second = Input("s2"); var follow = Input("f1", "system");
        var size = Math.Max(first.WireBody.ToString().Length, follow.WireBody.ToString().Length);
        var queue = new AgentPendingInputQueue(new(MaximumMessagesPerQueue: 2,
            MaximumMessageCharacters: size, MaximumCharactersPerQueue: size * 2L));
        queue.EnqueueSteering(first); queue.EnqueueSteering(second); queue.EnqueueFollowUp(follow);
        Same([first], queue.PeekQueuedMessages());
        var snapshot = queue.GetSnapshot();
        Same([first, second], snapshot.SteeringMessages); Same([follow], snapshot.FollowUpMessages);
        Equal(AgentPendingInputMode.OneAtATime, snapshot.SteeringMode);
        Check(ReferenceEquals(first, snapshot.SteeringMessages[0]), "Owned canonical input was rewritten.");
        Equal("1.0", snapshot.SteeringMessages[0].WireBody.Value.GetProperty("opaque").GetProperty("scale").GetRawText());
        Check(snapshot.SteeringMessages[0].WireBody.Value.GetProperty("opaque").GetProperty("missing").ValueKind == JsonValueKind.Null, "Explicit null changed.");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Throws<OperationCanceledException>(() => queue.GetSnapshot(canceled.Token));
        Throws<OperationCanceledException>(() => queue.ClearAndSnapshot(canceled.Token));
        Same(snapshot.SteeringMessages, queue.GetSnapshot().SteeringMessages);
        queue.FollowUpMode = AgentPendingInputMode.All;
        var cleared = queue.ClearAndSnapshot();
        Same([first, second], cleared.SteeringMessages); Same([follow], cleared.FollowUpMessages);
        Equal(AgentPendingInputMode.All, cleared.FollowUpMode);
        Check(!queue.HasQueuedMessages, "Clear retained part of a one-at-a-time queue.");
        queue.EnqueueSteering(second); queue.EnqueueSteering(first); queue.EnqueueFollowUp(follow);
        Same([second, first], queue.GetSnapshot().SteeringMessages);
        Same([first, second], snapshot.SteeringMessages);
        var replaced = cleared.SteeringMessages.SetItem(0, second);
        Check(!ReferenceEquals(replaced[0], cleared.SteeringMessages[0]), "Returned array update mutated clear receipt.");
        queue.ClearAll();
        Check(queue.GetSnapshot().SteeringMessages.IsEmpty && queue.GetSnapshot().FollowUpMessages.IsEmpty, "Old clear API changed.");
        return Task.CompletedTask;
    }

    private static async Task ConcurrentClears()
    {
        const int count = 64;
        var queue = new AgentPendingInputQueue();
        var start = Gate(); var receipts = new ConcurrentBag<AgentPendingInputQueueSnapshot>();
        var producers = Enumerable.Range(0, count).Select(index => Task.Run(async () =>
        {
            await start.Task;
            queue.EnqueueSteering(Input("s-" + index));
            queue.EnqueueFollowUp(Input("f-" + index));
        })).ToArray();
        var clearers = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        { await start.Task; receipts.Add(queue.ClearAndSnapshot()); })).ToArray();
        start.TrySetResult(); await Within(Task.WhenAll(producers.Concat(clearers)));
        receipts.Add(queue.ClearAndSnapshot());
        var steering = receipts.SelectMany(receipt => receipt.SteeringMessages).Select(Text).ToArray();
        var followUp = receipts.SelectMany(receipt => receipt.FollowUpMessages).Select(Text).ToArray();
        Equal(count, steering.Length); Equal(count, followUp.Length);
        Check(steering.Order().SequenceEqual(Enumerable.Range(0, count).Select(index => "s-" + index).Order()) &&
            followUp.Order().SequenceEqual(Enumerable.Range(0, count).Select(index => "f-" + index).Order()),
            "Atomic clear lost or duplicated concurrent queue admissions.");
        Check(receipts.All(receipt => !receipt.SteeringMessages.IsDefault && !receipt.FollowUpMessages.IsDefault &&
            receipt.SteeringMode == AgentPendingInputMode.OneAtATime && receipt.FollowUpMode == AgentPendingInputMode.OneAtATime),
            "Concurrent receipt contained an uninitialized queue or changed drain mode.");
        Check(!queue.HasQueuedMessages, "Final full clear was incomplete.");
    }

    private static async Task AgentRunning()
    {
        var entered = Gate(); var release = Gate(); var drained = Input("drained");
        var transport = new Script(); AgentPendingInputQueueSnapshot? callbackClear = null;
        NativeAgent? current = null;
        await using var agent = new NativeAgent(new(Model, transport, []), () => 1, new Sink(async (observation, _) =>
        {
            if (observation is AgentLoopInputMessageStarted input && ReferenceEquals(input.Message, drained))
            { entered.TrySetResult(); await release.Task; }
            if (observation is AssistantMessageEnded)
            {
                current!.Steer(Input("callback steering")); current.FollowUp(Input("callback followup"));
                callbackClear = current.ClearPendingInputQueues();
                Check(current.GetPendingInputQueueSnapshot().SteeringMessages.IsEmpty, "Reentrant callback clear failed.");
            }
        }));
        current = agent; agent.Steer(drained);
        var run = agent.PromptAsync(Input("prompt"));
        try
        {
            await Within(entered.Task);
            Same([drained], agent.Snapshot.PendingInputs);
            var s1 = Input("s1"); var s2 = Input("s2"); var f1 = Input("f1"); var f2 = Input("f2");
            agent.Steer(s1); agent.Steer(s2); agent.FollowUp(f1); agent.FollowUp(f2);
            Same([s1], agent.PeekQueuedMessages());
            var queued = agent.GetPendingInputQueueSnapshot();
            Same([s1, s2], queued.SteeringMessages); Same([f1, f2], queued.FollowUpMessages);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            Throws<OperationCanceledException>(() => agent.ClearPendingInputQueues(canceled.Token));
            var removed = agent.ClearPendingInputQueues();
            Same(queued.SteeringMessages, removed.SteeringMessages); Same(queued.FollowUpMessages, removed.FollowUpMessages);
            Same([drained], agent.Snapshot.PendingInputs);
            Check(agent.Snapshot.IsRunning && !run.IsCompleted, "Queue clear aborted or settled the generation.");
            release.TrySetResult(); var result = await Within(run);
            Equal(1, result.Turns.Length); Equal(1, transport.Requests.Count);
            Check(transport.Requests[0].Messages.Any(message => ReferenceEquals(message, drained)), "Already-drained input was lost.");
            Check(callbackClear is not null && callbackClear.SteeringMessages.Length == 1 && callbackClear.FollowUpMessages.Length == 1,
                "Awaited callback did not receive full clear values.");
            agent.Steer(Input("old API")); agent.ClearQueues();
            Check(agent.GetPendingInputQueueSnapshot().SteeringMessages.IsEmpty, "Existing Agent clear changed.");
        }
        finally { release.TrySetResult(); await Ignore(run); }
    }

    private static async Task DurableRunning()
    {
        using var files = new Files(); var ids = 0; var transport = new Script();
        var session = await Create(files, transport, () => "entry-" + Interlocked.Increment(ref ids));
        var entered = Gate(); var release = Gate();
        using var lease = session.Subscribe(new Sink(async (observation, _) =>
        { if (observation is AgentLoopEnded) { entered.TrySetResult(); await release.Task; } }));
        var run = session.PromptAsync(Input("durable"));
        try
        {
            await Within(entered.Task);
            var checkpoint = session.Snapshot;
            Check(checkpoint.Context.LlmMessages.Length == 2 && !run.IsCompleted, "Test did not gate the committed final listener.");
            var s1 = Input("s1"); var s2 = Input("s2"); var f1 = Input("f1"); var f2 = Input("f2");
            session.Steer(s1); session.Steer(s2); session.FollowUp(f1); session.FollowUp(f2);
            session.FollowUpMode = AgentPendingInputMode.All;
            var pending = session.GetPendingInputQueueSnapshot();
            Same([s1, s2], pending.SteeringMessages); Same([f1, f2], pending.FollowUpMessages);
            Equal(AgentPendingInputMode.All, pending.FollowUpMode);
            var cleared = session.ClearPendingInputQueues();
            Same(pending.SteeringMessages, cleared.SteeringMessages); Same(pending.FollowUpMessages, cleared.FollowUpMessages);
            Equal(checkpoint.Log.CommittedByteLength, session.Snapshot.Log.CommittedByteLength);
            Equal(checkpoint.Context.LeafId, session.Snapshot.Context.LeafId);
            Equal(checkpoint.Log.Entries.Length, session.Snapshot.Log.Entries.Length);
            Check(!session.WaitForIdleAsync().IsCompleted && session.Snapshot.Agent.IsRunning, "Queue mutation bypassed last-end settlement.");
            release.TrySetResult(); await Within(run); await Within(session.WaitForIdleAsync());
            Equal(1, transport.Requests.Count);
        }
        finally { release.TrySetResult(); await Ignore(run); await session.DisposeAsync(); }
        Check(session.GetPendingInputQueueSnapshot().SteeringMessages.IsEmpty, "Disposed snapshot gained queued data.");
        var ack = session.Snapshot.Log.CommittedByteLength;
        Equal(ack, new FileInfo(files.Path).Length);
        var bytes = await File.ReadAllBytesAsync(files.Path);
        Throws<PersistentAgentSessionException>(() => session.ClearPendingInputQueues());
        var after = await File.ReadAllBytesAsync(files.Path);
        Check(bytes.SequenceEqual(after), "Disposed queue operation changed durable bytes.");
    }

    private static async Task FaultAndDisposal()
    {
        using var files = new Files(); var ids = 0; var transport = new Script();
        var session = await Create(files, transport, () => "entry-" + Interlocked.Increment(ref ids));
        var entered = Gate(); var release = Gate();
        using var lease = session.Subscribe(new Sink(async (observation, _) =>
        {
            if (observation is AgentLoopInputMessageStarted)
            { entered.TrySetResult(); await release.Task; throw new InvalidOperationException("authored callback fault"); }
        }));
        var run = session.PromptAsync(Input("uncommitted"));
        try
        {
            await Within(entered.Task);
            var steering = Input("retain steering"); var followUp = Input("retain followup");
            session.Steer(steering); session.FollowUp(followUp);
            var before = session.Snapshot.Log.CommittedByteLength;
            release.TrySetResult(); await ThrowsAsync<InvalidOperationException>(() => run);
            Check(session.Snapshot.Fault is not null && transport.Requests.Count == 0, "Fault classification or acquisition changed.");
            Same([steering], session.GetPendingInputQueueSnapshot().SteeringMessages);
            Same([followUp], session.GetPendingInputQueueSnapshot().FollowUpMessages);
            var error = Throws<PersistentAgentSessionException>(() => session.ClearPendingInputQueues());
            Equal(PersistentAgentSessionFailure.Faulted, error.Fault.Failure);
            Equal(before, session.Snapshot.Log.CommittedByteLength);
            await session.DisposeAsync();
            Same([steering], session.GetPendingInputQueueSnapshot().SteeringMessages);
            Same([followUp], session.GetPendingInputQueueSnapshot().FollowUpMessages);
            Equal(PersistentAgentSessionFailure.Disposed, Throws<PersistentAgentSessionException>(() => session.ClearPendingInputQueues()).Fault.Failure);
            Equal(before, new FileInfo(files.Path).Length);
        }
        finally { release.TrySetResult(); await Ignore(run); await session.DisposeAsync(); }
        await using var agent = new NativeAgent(new(Model, new Script(), []), () => 1, new Sink((_, _) => ValueTask.CompletedTask));
        agent.Steer(Input("visible after close")); await agent.DisposeAsync();
        Equal(1, agent.GetPendingInputQueueSnapshot().SteeringMessages.Length);
        Throws<ObjectDisposedException>(() => agent.ClearPendingInputQueues());
    }

    private static Task<PersistentAgentSession> Create(Files files, IChatTransport transport, Func<string> nextId) =>
        PersistentAgentSession.CreateAsync(files.Path, new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
        { type = "session", version = 3, id = "header", timestamp = "2026-10-01T00:00:00.000Z", cwd = files.Root })),
        new AgentConfiguration(Model, transport, []), () => 1, nextId);
    private static TranscriptEntry Input(string text, string role = "user") => new(role, JsonData.Parse(
        "{\"role\":" + JsonSerializer.Serialize(role) + ",\"content\":" + JsonSerializer.Serialize(text) +
        ",\"timestamp\":1,\"opaque\":{\"scale\":1.0,\"missing\":null}}"));
    private static string Text(TranscriptEntry message) => message.WireBody.Value.GetProperty("content").GetString()!;
    private sealed class Script : IChatTransport
    {
        public readonly List<ChatRequest> Requests = [];
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Requests.Add(request); await Task.CompletedTask;
            var message = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 1, [new TextContent("done")], TokenUsage.Zero, StopReason.Stop);
            yield return new StreamStarted(message with { Content = [], StopReason = StopReason.Pending });
            yield return new TextStarted(0, new("")); yield return new TextEnded(0, "done"); yield return new StreamDone(StopReason.Stop, message);
        }
    }
    private sealed class Sink(Func<AgentEvent, CancellationToken, ValueTask> emit) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) => emit(observation, token); }
    private sealed class Files : IDisposable
    {
        private readonly string _parent = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
        public string Root { get; }
        public string Path => System.IO.Path.Combine(Root, "session.jsonl");
        public Files() { Root = System.IO.Path.Combine(_parent, "pisharp-queue-snapshot-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); }
        public void Dispose()
        {
            var target = System.IO.Path.GetFullPath(Root);
            if (System.IO.Path.GetDirectoryName(target) != _parent || !System.IO.Path.GetFileName(target).StartsWith("pisharp-queue-snapshot-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing unowned test cleanup.");
            Directory.Delete(target, recursive: true);
        }
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Within(Task task) => await task.WaitAsync(TimeSpan.FromSeconds(10));
    private static async Task<T> Within<T>(Task<T> task) => await task.WaitAsync(TimeSpan.FromSeconds(10));
    private static async Task Ignore(Task task) { try { await Within(task); } catch (Exception) { } }
    private static void Same(ImmutableArray<TranscriptEntry> expected, ImmutableArray<TranscriptEntry> actual)
    { Equal(expected.Length, actual.Length); for (var index = 0; index < expected.Length; index++) Check(ReferenceEquals(expected[index], actual[index]), "Canonical immutable input identity/order changed."); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Values differ.");
    private static T Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await Within(action()); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
