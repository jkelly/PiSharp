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

internal static class AtomicQueueTakeTests
{
    private static readonly ModelDescriptor Model = new("queue-take", "openai-responses", "fixture-provider");
    public const string Prefix = "atomic queue take ";
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        (Prefix + "rejects stale foreign forged and ABA snapshots without changing queues", Identity),
        (Prefix + "capacity preflight cannot remove concurrent growth through Agent", CapacityRace),
        (Prefix + "same revision has exactly one winning concurrent remover", CompetingRemovers),
        (Prefix + "cancellation while waiting for the actual queue lock cannot remove state", CancellationWhileWaiting),
        (Prefix + "cancellation before commit retains values and budgets", Cancellation),
        (Prefix + "durable active work survives removal with original cleanup joined", DurableActive),
        (Prefix + "host preflight and disposal guards match existing clear", HostGuards)
    ];

    private static Task Identity()
    {
        var queue = new AgentPendingInputQueue(); var first = Input("first"); var second = Input("second");
        queue.EnqueueSteering(first); queue.EnqueueSteering(first); queue.EnqueueFollowUp(second);
        var before = queue.GetSnapshot();
        using (var json = JsonDocument.Parse(JsonSerializer.Serialize(before)))
            Equal(4, json.RootElement.EnumerateObject().Count());
        void Reject(AgentPendingInputQueueSnapshot expected)
        {
            var current = queue.GetSnapshot(); Check(!queue.TryClearAndSnapshot(expected, out var removed) && removed is null, "Invalid snapshot removed values.");
            Same(current.SteeringMessages, queue.GetSnapshot().SteeringMessages); Same(current.FollowUpMessages, queue.GetSnapshot().FollowUpMessages);
        }
        Reject(new(before.SteeringMessages, before.FollowUpMessages, before.SteeringMode, before.FollowUpMode));
        var foreign = new AgentPendingInputQueue(); foreign.EnqueueSteering(first); foreign.EnqueueSteering(first); foreign.EnqueueFollowUp(second); Reject(foreign.GetSnapshot());
        Reject(before with { SteeringMessages = before.SteeringMessages.SetItem(0, Input("first")) });
        Reject(before with { FollowUpMessages = [] });
        Reject(before with { SteeringMode = AgentPendingInputMode.All });
        queue.SteeringMode = AgentPendingInputMode.All; queue.SteeringMode = AgentPendingInputMode.OneAtATime; Reject(before);
        before = queue.GetSnapshot(); queue.ClearAll(); queue.EnqueueSteering(first); queue.EnqueueSteering(first); queue.EnqueueFollowUp(second); Reject(before);
        before = queue.GetSnapshot(); queue.DrainFollowUp(); queue.EnqueueFollowUp(second); Reject(before);
        var exact = queue.GetSnapshot(); queue.SteeringMode = queue.SteeringMode; queue.PeekSteering();
        Check(queue.TryClearAndSnapshot(exact, out var receipt), "Read/no-op mode invalidated exact state.");
        Same([first, first], receipt!.SteeringMessages); Same([second], receipt!.FollowUpMessages);
        Equal(exact.SteeringMode, queue.SteeringMode); Equal(exact.FollowUpMode, queue.FollowUpMode);
        Check(!queue.TryClearAndSnapshot(exact, out _), "Old nonempty receipt authorized another clear.");
        var empty = queue.GetSnapshot(); queue.ClearAll();
        Check(queue.TryClearAndSnapshot(empty, out var emptyReceipt) && emptyReceipt.SteeringMessages.IsEmpty, "No-op empty clear changed revision.");
        return Task.CompletedTask;
    }

    private static async Task CapacityRace()
    {
        await using var agent = new NativeAgent(new(Model, new Script(), []), () => 1, new Sink((_, _) => ValueTask.CompletedTask));
        var first = Input(new string('a', 40_000)); var growth = Input(new string('b', 40_000)); agent.Steer(first);
        var expected = agent.GetPendingInputQueueSnapshot();
        Check(expected.SteeringMessages.Sum(message => Text(message).Length) <= 65_536, "Initial editor preflight did not fit.");
        var release = Gate(); var producer = Task.Run(async () => { await release.Task; agent.FollowUp(growth); });
        try
        {
            release.TrySetResult(); await producer; // Growth is deterministically between preflight and compare-and-clear.
            Check(!agent.TryClearPendingInputQueues(expected, out var removed) && removed is null, "Stale preflight removed concurrent text.");
            var current = agent.GetPendingInputQueueSnapshot();
            Check(current.SteeringMessages.Concat(current.FollowUpMessages).Sum(message => Text(message).Length) > 65_536, "Capacity race was not exercised.");
            Same([first], current.SteeringMessages); Same([growth], current.FollowUpMessages);
            // A larger caller may preflight the new revision; the core does not invent an editor capacity.
            Check(agent.TryClearPendingInputQueues(current, out var exact), "Fresh revision failed.");
            Same([first], exact!.SteeringMessages); Same([growth], exact!.FollowUpMessages);
        }
        finally { release.TrySetResult(); await producer; }
    }

    private static async Task CompetingRemovers()
    {
        var queue = new AgentPendingInputQueue(); queue.EnqueueSteering(Input("s")); queue.EnqueueFollowUp(Input("f"));
        var expected = queue.GetSnapshot(); var release = Gate(); var winners = new ConcurrentBag<AgentPendingInputQueueSnapshot>();
        var operations = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        { await release.Task; if (queue.TryClearAndSnapshot(expected, out var removed)) winners.Add(removed); })).ToArray();
        try
        {
            release.TrySetResult(); await Task.WhenAll(operations);
            Equal(1, winners.Count); Same(expected.SteeringMessages, winners.Single().SteeringMessages); Same(expected.FollowUpMessages, winners.Single().FollowUpMessages);
            Check(!queue.HasQueuedMessages, "Winning remover did not clear both queues.");
        }
        finally { release.TrySetResult(); await Task.WhenAll(operations); }
    }

    private static async Task CancellationWhileWaiting()
    {
        var queue = new AgentPendingInputQueue(); queue.EnqueueSteering(Input("retained")); var expected = queue.GetSnapshot();
        var gate = typeof(AgentPendingInputQueue).GetField("_gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(queue)!;
        using var cancellation = new CancellationTokenSource(); var started = Gate(); Task<bool> take;
        // Hold the real commit lock so cancellation is guaranteed to precede admission to it.
        lock (gate)
        {
            take = Task.Run(() => { started.TrySetResult(); return queue.TryClearAndSnapshot(expected, out _, cancellation.Token); });
            started.Task.GetAwaiter().GetResult(); cancellation.Cancel();
        }
        try { await take; throw new InvalidOperationException("Canceled waiting take succeeded."); }
        catch (OperationCanceledException) { }
        Same(expected.SteeringMessages, queue.GetSnapshot().SteeringMessages);
        Check(queue.TryClearAndSnapshot(expected, out _), "Canceled waiter invalidated the retained state.");
    }

    private static Task Cancellation()
    {
        var message = Input("quota"); var bytes = message.WireBody.ToString().Length;
        var queue = new AgentPendingInputQueue(new(MaximumMessagesPerQueue: 1, MaximumMessageCharacters: bytes, MaximumCharactersPerQueue: bytes));
        queue.EnqueueSteering(message); queue.EnqueueFollowUp(message); var expected = queue.GetSnapshot();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Throws<OperationCanceledException>(() => queue.TryClearAndSnapshot(expected, out _, cancellation.Token));
        Same([message], queue.GetSnapshot().SteeringMessages); Same([message], queue.GetSnapshot().FollowUpMessages);
        Check(queue.TryClearAndSnapshot(expected, out var removed), "Cancellation changed the queue revision.");
        Same([message], removed!.SteeringMessages); queue.EnqueueSteering(message); queue.EnqueueFollowUp(message);
        Check(queue.TryClearAndSnapshot(queue.GetSnapshot(), out _), "Successful take did not release capacity.");
        using var after = new CancellationTokenSource(); var empty = queue.GetSnapshot();
        Check(queue.TryClearAndSnapshot(empty, out _, after.Token), "Empty take failed."); after.Cancel();
        Check(!queue.HasQueuedMessages, "Cancellation after commit changed state."); return Task.CompletedTask;
    }

    private static async Task DurableActive()
    {
        using var files = new Files(); var ids = 0; var transport = new Script();
        await using var session = await Create(files, transport, () => "entry-" + Interlocked.Increment(ref ids));
        var entered = Gate(); var release = Gate(); var original = Input("admitted");
        using var subscription = session.Subscribe(new Sink(async (observation, _) =>
        { if (observation is AgentLoopInputMessageStarted input && ReferenceEquals(input.Message, original)) { entered.TrySetResult(); await release.Task; } }));
        var run = session.PromptAsync(original);
        try
        {
            if (await Task.WhenAny(entered.Task, run) == run && !entered.Task.IsCompleted) { await run; throw new InvalidOperationException("Missing held input boundary."); }
            await entered.Task; var bytes = await ReadSharedAsync(files.Path);
            var first = Input("pending"); session.Steer(first); var stale = session.GetPendingInputQueueSnapshot(); session.FollowUp(Input("concurrent"));
            Check(!session.TryClearPendingInputQueues(stale, out _), "Host accepted stale queue state.");
            using var canceled = new CancellationTokenSource(); canceled.Cancel(); var expected = session.GetPendingInputQueueSnapshot();
            Throws<OperationCanceledException>(() => session.TryClearPendingInputQueues(expected, out _, canceled.Token));
            Check(session.TryClearPendingInputQueues(expected, out var removed), "Host did not remove exact state.");
            Same(expected.SteeringMessages, removed!.SteeringMessages); Same(expected.FollowUpMessages, removed!.FollowUpMessages);
            Check(!run.IsCompleted && session.Snapshot.Agent.IsRunning, "Queue take settled active work.");
            Same([original], session.Snapshot.Agent.PendingInputs);
            var afterTakeBytes = await ReadSharedAsync(files.Path);
            Check(bytes.SequenceEqual(afterTakeBytes), "Pending take wrote durable history.");
            release.TrySetResult(); await run;
            Equal(1, transport.Requests.Count); Check(transport.Requests[0].Messages.Any(message => ReferenceEquals(message, original)), "Admitted input was removed.");
        }
        finally { release.TrySetResult(); await run; }
    }

    // The active store has ReadWrite access. The observer must share both access modes on Windows.
    private static async Task<byte[]> ReadSharedAsync(string path)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 4096, options: FileOptions.Asynchronous);
        using var bytes = new MemoryStream();
        await input.CopyToAsync(bytes);
        return bytes.ToArray();
    }

    private static async Task HostGuards()
    {
        using var files = new Files(); var ids = 0; await using var session = await Create(files, new Script(), () => "entry-" + Interlocked.Increment(ref ids));
        session.Steer(Input("keep")); var expected = session.GetPendingInputQueueSnapshot(); var observed = false;
        await session.SubmitInputAsync(new("new", PromptInputSource.Rpc, StreamingBehavior: PromptInputStreamingBehavior.FollowUp),
            options: new() { QueueOnly = true, BeforeQueueCommit = (_, _, _) =>
            { Throws<InvalidOperationException>(() => session.TryClearPendingInputQueues(expected, out _)); observed = true; } });
        Check(observed && session.GetPendingInputQueueSnapshot().SteeringMessages.Length == 1, "Preflight mutation guard was bypassed.");
        await session.DisposeAsync();
        Throws<PersistentAgentSessionException>(() => session.TryClearPendingInputQueues(session.GetPendingInputQueueSnapshot(), out _));
        Equal(1, session.GetPendingInputQueueSnapshot().SteeringMessages.Length);
        await using var agent = new NativeAgent(new(Model, new Script(), []), () => 1, new Sink((_, _) => ValueTask.CompletedTask));
        agent.Steer(Input("keep")); await agent.DisposeAsync();
        Throws<ObjectDisposedException>(() => agent.TryClearPendingInputQueues(agent.GetPendingInputQueueSnapshot(), out _));
        Equal(1, agent.GetPendingInputQueueSnapshot().SteeringMessages.Length);
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

    private static void Same(ImmutableArray<TranscriptEntry> expected, ImmutableArray<TranscriptEntry> actual)
    { Equal(expected.Length, actual.Length); for (var index = 0; index < expected.Length; index++) Check(ReferenceEquals(expected[index], actual[index]), "Canonical immutable input identity/order changed."); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Values differ.");
    private static T Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
