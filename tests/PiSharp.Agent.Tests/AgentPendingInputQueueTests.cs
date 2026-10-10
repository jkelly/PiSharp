using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Contracts;

internal static class AgentPendingInputQueueTests
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("pending input independent FIFO modes and immutable peek", ModesAndOwnership),
        ("pending input exact limits reject atomically and release budgets", LimitsAndBudgetReuse),
        ("pending input invalid roles/options retain queued canonical values", Validation),
        ("pending input gated concurrent producers/consumers preserve every admitted value", ConcurrentDelivery),
        ("pending input concurrent capacity admits exactly the bound without silent loss", ConcurrentCapacity),
        ("pending input cancellation prevents admission/drain/clear without consuming state", Cancellation),
        ("pending input callbacks drive steering before follow-up across gated loop turns", LoopCallbacks)
    ];

    public static Task ModesAndOwnership()
    {
        var queue = new AgentPendingInputQueue();
        Equal(AgentPendingInputMode.OneAtATime, queue.SteeringMode);
        Equal(AgentPendingInputMode.OneAtATime, queue.FollowUpMode);
        Check(!queue.HasQueuedMessages && queue.PeekQueuedMessages().IsEmpty, "New queue has pending inputs.");
        var first = Entry("s1"); var second = Entry("s2"); var follow = Entry("f1", "system");
        queue.EnqueueFollowUp(follow); queue.EnqueueSteering(first); queue.EnqueueSteering(second);
        var peek = queue.PeekQueuedMessages();
        SameEntries([first], peek);
        Check(ReferenceEquals(first, peek[0]), "Canonical immutable entry was replaced.");
        var changed = peek.SetItem(0, Entry("local replacement"));
        SameEntries([first], queue.PeekSteering());
        Check(!ReferenceEquals(changed[0], peek[0]), "Immutable snapshot update changed the original.");
        Equal(2, queue.SteeringCount); Equal(1, queue.FollowUpCount);
        SameEntries([first], queue.DrainSteering());
        queue.SteeringMode = AgentPendingInputMode.All;
        queue.EnqueueSteering(first);
        var selectedSnapshot = queue.PeekQueuedMessages();
        SameEntries([second, first], selectedSnapshot);
        queue.FollowUpMode = AgentPendingInputMode.All;
        SameEntries([second, first], queue.DrainSteering());
        SameEntries([follow], queue.PeekQueuedMessages());
        SameEntries([follow], queue.DrainFollowUp());
        Check(!queue.HasQueuedMessages && queue.DrainSteering().IsEmpty && queue.DrainFollowUp().IsEmpty, "Empty drain returned uninitialized or retained state.");
        SameEntries([second, first], selectedSnapshot);
        return Task.CompletedTask;
    }

    public static Task LimitsAndBudgetReuse()
    {
        var a = Entry("same"); var b = Entry("next");
        var size = a.WireBody.ToString().Length;
        var queue = new AgentPendingInputQueue(new(MaximumMessagesPerQueue: 2, MaximumMessageCharacters: size,
            MaximumCharactersPerQueue: size * 2L, MaximumJsonDepth: 2));
        queue.EnqueueSteering(a); queue.EnqueueSteering(b);
        queue.EnqueueFollowUp(a); queue.EnqueueFollowUp(b);
        var before = queue.PeekSteering();
        Equal(AgentPendingInputFailure.ResourceLimit, Failure(() => queue.EnqueueSteering(Entry("more"))).Failure);
        Equal(2, queue.SteeringCount); Equal(2, queue.FollowUpCount); SameEntries(before, queue.PeekSteering());
        var one = queue.DrainSteering(); SameEntries([a], one);
        queue.EnqueueSteering(a); queue.SteeringMode = AgentPendingInputMode.All;
        SameEntries([b, a], queue.DrainSteering());
        queue.EnqueueSteering(a); queue.ClearSteering(); Equal(0, queue.SteeringCount); Equal(2, queue.FollowUpCount);
        queue.EnqueueSteering(a); queue.ClearFollowUp(); Equal(1, queue.SteeringCount); Equal(0, queue.FollowUpCount);
        queue.ClearAll(); Check(!queue.HasQueuedMessages, "Clear did not reset both queues.");
        queue.EnqueueSteering(a); queue.EnqueueFollowUp(a);
        var characterBound = new AgentPendingInputQueue(new(MaximumMessagesPerQueue: 3, MaximumMessageCharacters: size,
            MaximumCharactersPerQueue: size * 2L - 1));
        characterBound.EnqueueSteering(a);
        Equal(AgentPendingInputFailure.ResourceLimit, Failure(() => characterBound.EnqueueSteering(b)).Failure);
        Equal(1, characterBound.SteeringCount);
        Equal(AgentPendingInputFailure.ResourceLimit, Failure(() => new AgentPendingInputQueue(new(MaximumMessageCharacters: size - 1)).EnqueueSteering(a)).Failure);
        var nested = new TranscriptEntry("user", JsonData.Parse("""{"role":"user","content":"x","future":{"nested":[]}}"""));
        new AgentPendingInputQueue(new(MaximumJsonDepth: 3)).EnqueueSteering(nested);
        Equal(AgentPendingInputFailure.ResourceLimit, Failure(() => new AgentPendingInputQueue(new(MaximumJsonDepth: 2)).EnqueueSteering(nested)).Failure);
        return Task.CompletedTask;
    }

    public static Task Validation()
    {
        var queue = new AgentPendingInputQueue();
        TranscriptEntry owned;
        using (var document = JsonDocument.Parse("""{"role":"user","content":"exact\r\nline","opaque":{"n":1.0,"wide":9007199254740993,"null":null,"array":["b","a"]}}"""))
            owned = new("user", JsonData.FromElement(document.RootElement));
        queue.EnqueueSteering(owned);
        var raw = owned.WireBody.ToString();
        foreach (var entry in new TranscriptEntry[]
        {
            null!, new("assistant", JsonData.Parse("""{"role":"assistant"}""")),
            new("user", JsonData.Parse("""{"role":"system","private":"secret"}""")),
            new("user", JsonData.EmptyObject), new("user", JsonData.Null), new("user", null!),
            new("user", JsonData.Parse("""{"role":"\uD800","private":"secret"}"""))
        })
        {
            var failure = Failure(() => queue.EnqueueSteering(entry));
            Equal(AgentPendingInputFailure.InvalidInput, failure.Failure);
            Check(!failure.Message.Contains("private", StringComparison.Ordinal) && !failure.Message.Contains("secret", StringComparison.Ordinal), "Failure leaked input data.");
            Equal(1, queue.SteeringCount); Equal(0, queue.FollowUpCount);
        }
        foreach (var options in new AgentPendingInputQueueOptions[]
        {
            new(MaximumMessagesPerQueue: 0), new(MaximumMessageCharacters: 0), new(MaximumCharactersPerQueue: 0),
            new(MaximumJsonDepth: 0), new(MaximumJsonDepth: 1001), new(SteeringMode: (AgentPendingInputMode)99), new(FollowUpMode: (AgentPendingInputMode)99)
        }) Throws<ArgumentException>(() => new AgentPendingInputQueue(options));
        Throws<ArgumentException>(() => queue.SteeringMode = (AgentPendingInputMode)99);
        Throws<ArgumentException>(() => queue.FollowUpMode = (AgentPendingInputMode)99);
        Equal(AgentPendingInputMode.OneAtATime, queue.SteeringMode);
        Equal(raw, queue.DrainSteering().Single().WireBody.ToString());
        Equal("1.0", owned.WireBody.Value.GetProperty("opaque").GetProperty("n").GetRawText());
        Equal("9007199254740993", owned.WireBody.Value.GetProperty("opaque").GetProperty("wide").GetRawText());
        return Task.CompletedTask;
    }

    public static async Task ConcurrentDelivery()
    {
        var queue = new AgentPendingInputQueue(new(SteeringMode: AgentPendingInputMode.All, MaximumMessagesPerQueue: 128));
        var start = Gate();
        var producers = Enumerable.Range(0, 8).Select(producer => Task.Run(async () =>
        {
            await start.Task;
            for (var index = 0; index < 16; index++) queue.EnqueueSteering(Entry($"{producer}:{index}"));
        })).ToArray();
        start.TrySetResult(); await Task.WhenAll(producers);
        var snapshot = queue.PeekSteering();
        Equal(128, snapshot.Length);
        for (var producer = 0; producer < 8; producer++)
            Sequence(Enumerable.Range(0, 16).Select(index => $"{producer}:{index}"), snapshot.Select(Label).Where(value => value.StartsWith(producer + ":", StringComparison.Ordinal)));
        queue.SteeringMode = AgentPendingInputMode.OneAtATime;
        var consumerStart = Gate();
        var delivered = new ConcurrentBag<TranscriptEntry>();
        var consumers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await consumerStart.Task;
            while (true)
            {
                var next = queue.DrainSteering();
                if (next.IsEmpty) return;
                delivered.Add(next.Single());
            }
        })).ToArray();
        consumerStart.TrySetResult(); await Task.WhenAll(consumers);
        Equal(128, delivered.Count);
        Equal(128, delivered.Select(Label).Distinct(StringComparer.Ordinal).Count());
        Check(snapshot.Select(Label).ToHashSet(StringComparer.Ordinal).SetEquals(delivered.Select(Label)), "Concurrent drains lost or invented input.");
        Equal(0, queue.SteeringCount);
        queue.EnqueueSteering(Entry("later"));
        Equal(128, snapshot.Length);
        Check(snapshot.All(entry => Label(entry) != "later"), "Snapshot changed with later queue mutations.");
    }

    public static async Task ConcurrentCapacity()
    {
        var queue = new AgentPendingInputQueue(new(SteeringMode: AgentPendingInputMode.All, MaximumMessagesPerQueue: 8));
        var accepted = new ConcurrentBag<TranscriptEntry>(); var rejected = new ConcurrentBag<TranscriptEntry>();
        var start = Gate();
        var tasks = Enumerable.Range(0, 32).Select(index => Task.Run(async () =>
        {
            await start.Task;
            var entry = Entry("candidate:" + index);
            try { queue.EnqueueSteering(entry); accepted.Add(entry); }
            catch (AgentPendingInputException error) when (error.Failure == AgentPendingInputFailure.ResourceLimit) { rejected.Add(entry); }
        })).ToArray();
        start.TrySetResult(); await Task.WhenAll(tasks);
        Equal(8, accepted.Count); Equal(24, rejected.Count); Equal(8, queue.SteeringCount);
        var drained = queue.DrainSteering();
        Check(accepted.Select(Label).ToHashSet(StringComparer.Ordinal).SetEquals(drained.Select(Label)), "Accepted capacity inputs were silently dropped.");
        Check(!rejected.Select(Label).Intersect(drained.Select(Label), StringComparer.Ordinal).Any(), "Rejected input entered queue.");
        queue.EnqueueFollowUp(Entry("independent"));
        Equal(1, queue.FollowUpCount);
        var retainedRejected = rejected.First();
        queue.EnqueueSteering(retainedRejected);
        SameEntries([retainedRejected], queue.DrainSteering());
    }

    public static async Task Cancellation()
    {
        var queue = new AgentPendingInputQueue(new(SteeringMode: AgentPendingInputMode.All));
        var a = Entry("retained steering"); var b = Entry("retained follow-up");
        queue.EnqueueSteering(a); queue.EnqueueFollowUp(b);
        using var cancellation = new CancellationTokenSource();
        var start = Gate();
        var producers = Enumerable.Range(0, 8).Select(index => Task.Run(async () =>
        {
            await start.Task;
            var error = Throws<OperationCanceledException>(() => queue.EnqueueSteering(Entry("cancelled:" + index), cancellation.Token));
            Equal(cancellation.Token, error.CancellationToken);
        })).ToArray();
        cancellation.Cancel(); start.TrySetResult(); await Task.WhenAll(producers);
        foreach (var action in new Action[]
        {
            () => queue.EnqueueFollowUp(null!, cancellation.Token),
            () => queue.PeekSteering(cancellation.Token), () => queue.PeekFollowUp(cancellation.Token), () => queue.PeekQueuedMessages(cancellation.Token),
            () => queue.DrainSteering(cancellation.Token), () => queue.DrainFollowUp(cancellation.Token),
            () => queue.ClearSteering(cancellation.Token), () => queue.ClearFollowUp(cancellation.Token), () => queue.ClearAll(cancellation.Token),
            () => queue.GetSteeringMessagesAsync(cancellation.Token), () => queue.GetFollowUpMessagesAsync(cancellation.Token)
        }) Throws<OperationCanceledException>(action);
        SameEntries([a], queue.DrainSteering()); SameEntries([b], queue.DrainFollowUp());
        Check(!queue.HasQueuedMessages, "Cancelled operations mutated retained state.");
    }

    public static async Task LoopCallbacks()
    {
        var queue = new AgentPendingInputQueue();
        var transport = new TextTransport();
        var entered = Gate(); var release = Gate();
        var callbacks = new AgentLoopCallbacks((snapshot, token) =>
        {
            token.ThrowIfCancellationRequested();
            _ = queue.PeekQueuedMessages(); // Reentrant observer call is independent of prior queue polling.
            return ValueTask.FromResult(new ChatRequest(snapshot.Model, snapshot.Transcript));
        }, GetSteeringMessages: queue.GetSteeringMessagesAsync, GetFollowUpMessages: queue.GetFollowUpMessagesAsync,
            FinishTurn: async (turn, token) => { if (turn.TurnIndex == 0) { entered.TrySetResult(); await release.Task.WaitAsync(token); } });
        var runner = new AgentLoopRunner(new TurnRunner(new ChatClient(transport, capacity: 1), new ToolBatchScheduler([])),
            TextTransport.Model, () => 123);
        var running = runner.RunAsync([Entry("initial")], callbacks, new Sink());
        try
        {
            await entered.Task;
            queue.EnqueueFollowUp(Entry("follow-up")); queue.EnqueueSteering(Entry("steering-1")); queue.EnqueueSteering(Entry("steering-2"));
            Equal(1, transport.Requests.Count);
            Sequence(["steering-1"], queue.PeekQueuedMessages().Select(Label));
            release.TrySetResult();
            var result = await running;
            Equal(AgentLoopStopReason.Completed, result.Reason);
            Equal(4, transport.Requests.Count); Equal(4, transport.Cleanups); Equal(4, result.Turns.Length);
            Sequence(["initial", "steering-1", "steering-2", "follow-up"], result.Transcript.Where(entry => entry.Role == "user").Select(Label));
            for (var index = 0; index < transport.Requests.Count; index++)
                Sequence(new[] { "initial", "steering-1", "steering-2", "follow-up" }.Take(index + 1),
                    transport.Requests[index].Messages.Where(entry => entry.Role == "user").Select(Label));
            Check(!queue.HasQueuedMessages, "Loop callbacks left consumed messages queued.");
        }
        finally { release.TrySetResult(); try { await running; } catch { } }
    }

    private sealed class TextTransport : IChatTransport
    {
        public static readonly ModelDescriptor Model = new("pending-input-model", "openai-responses", "fixture-provider");
        public readonly List<ChatRequest> Requests = [];
        public int Cleanups;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            var final = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 0, [new TextContent("done")], TokenUsage.Zero, StopReason.Stop);
            try
            {
                yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
                yield return new TextStarted(0, new(""));
                yield return new TextEnded(0, "done");
                yield return new StreamDone(StopReason.Stop, final);
                await Task.CompletedTask;
            }
            finally { Cleanups++; }
        }
    }
    private sealed class Sink : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; } }
    private static TranscriptEntry Entry(string text, string role = "user") =>
        new(role, JsonData.FromElement(JsonSerializer.SerializeToElement(new { role, content = text, timestamp = 0 })));
    private static string Label(TranscriptEntry message) => message.WireBody.Value.GetProperty("content").GetString()!;
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void SameEntries(IEnumerable<TranscriptEntry> expected, IEnumerable<TranscriptEntry> actual) =>
        Check(expected.SequenceEqual(actual), "Canonical input sequence changed.");
    private static void Sequence(IEnumerable<string> expected, IEnumerable<string> actual) => Check(expected.SequenceEqual(actual), "Input order changed.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Values differ.");
    private static AgentPendingInputException Failure(Action action) => Throws<AgentPendingInputException>(action);
    private static T Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
