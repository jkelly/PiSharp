using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Contracts;
using NativeAgent = PiSharp.Agent.Agent;

internal static class AgentTests
{
    private static readonly ModelDescriptor Model = new("agent-model", "openai-responses", "fixture-provider");
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("Agent tool continuation and successive prompts retain complete immutable history", FunctionalGenerations),
        ("Agent rejects busy prompt/config/history mutation and snapshots generation configuration", BusyAdmission),
        ("Agent gated steering/follow-up all modes select three complete turns", QueueTurns),
        ("Agent Continue preserves user/assistant tails and avoids double steering dequeue", ContinueSelection),
        ("Agent cancellation/concurrent disposal share awaited cleanup and record callback faults", CancellationAndDisposal),
        ("Agent commits message/tool state before sink/finish faults", CommittedFaultState),
        ("Agent retains uncommitted input through limits and rejects silent recovery", PendingRecovery),
        ("Agent end listeners delay idle and callback self-waits fail without lock deadlock", EndBarrierAndReentrancy),
        ("Agent subscription leases await ordered committed-state barriers and remove future delivery", SubscriptionBarriers)
    ];

    private static async Task FunctionalGenerations()
    {
        var transport = new ScriptTransport([Message(tool: true), Message(), Message()]);
        var executor = new Executor();
        await using var agent = Create(transport, [new("read", executor)]);
        var first = await agent.PromptAsync(Input("initial"));
        Equal(2, first.Turns.Length); Equal(2, transport.Requests.Count); Equal(1, executor.Calls);
        var retained = agent.Snapshot;
        Equal(4, retained.Messages.Length);
        Equal("toolResult", retained.Messages[2].Role);
        Equal("1.0", retained.Messages[2].WireBody.Value.GetProperty("details").GetProperty("scale").GetRawText());
        var second = await agent.PromptAsync(Input("next"));
        Equal(1, second.Turns.Length); Equal(3, transport.Requests.Count); Equal(6, agent.Snapshot.Messages.Length);
        Equal(2L, agent.Snapshot.Generation);
        Equal(4, retained.Messages.Length);
        RawPrefix(retained.Messages, transport.Requests[2].Messages);
        Equal(3, transport.Cleanups);
        Check(!agent.Snapshot.IsRunning && agent.Snapshot.Failure is null, "Completed Agent remained busy/failed.");
    }

    private static async Task BusyAdmission()
    {
        var entered = Gate(); var release = Gate();
        var transport = new ScriptTransport([Message()]);
        var sink = new Sink(async (observation, _) =>
        { if (observation is AgentLoopStarted) { entered.TrySetResult(); await release.Task; } });
        await using var agent = Create(transport, sink: sink);
        var run = agent.PromptAsync(Input("accepted"));
        try
        {
            await entered.Task;
            var snapshot = agent.Snapshot;
            Check(snapshot.IsRunning && snapshot.PendingInputs.Length == 1 && snapshot.Messages.IsEmpty, "Admission state was not immutable/exclusive.");
            var rejected = Input("rejected");
            Throws<InvalidOperationException>(() => agent.PromptAsync(rejected));
            Throws<InvalidOperationException>(() => agent.ContinueAsync());
            Throws<InvalidOperationException>(() => agent.ReplaceMessages([rejected]));
            Throws<InvalidOperationException>(() => agent.Configure(new(Model with { Id = "unsafe" }, transport, [])));
            Equal(0, agent.Snapshot.SteeringCount); Equal(0, agent.Snapshot.FollowUpCount);
            Equal(Model, agent.Snapshot.Model);
            release.TrySetResult(); await run;
            Equal(Model, transport.Requests.Single().Model);
            Check(agent.Snapshot.Messages.All(message => !message.WireBody.ToString().Contains("rejected", StringComparison.Ordinal)), "Busy prompt was silently queued/committed.");
            var replacement = new ScriptTransport([Message(model: "changed-model")]);
            var changed = Model with { Id = "changed-model" };
            agent.Configure(new(changed, replacement, []));
            await agent.PromptAsync(Input("after"));
            Equal(changed, replacement.Requests.Single().Model);
            Equal(Model, snapshot.Model);
            RawPrefix(transport.Requests[0].Messages, replacement.Requests[0].Messages);
        }
        finally { release.TrySetResult(); try { await run; } catch { } }
    }

    private static async Task QueueTurns()
    {
        var entered = Gate(); var release = Gate();
        var transport = new ScriptTransport([Message(), Message(), Message()]);
        var hooks = new AgentHooks(FinishTurn: async (turn, _) =>
        { if (turn.TurnIndex == 0) { entered.TrySetResult(); await release.Task; } });
        await using var agent = Create(transport, hooks: hooks);
        var run = agent.PromptAsync(Input("initial"));
        try
        {
            await entered.Task;
            agent.Steer(Input("s1")); agent.Steer(Input("s2")); agent.FollowUp(Input("f1")); agent.FollowUp(Input("f2"));
            agent.SteeringMode = AgentPendingInputMode.All; agent.FollowUpMode = AgentPendingInputMode.All;
            Equal(2, agent.Snapshot.SteeringCount); Equal(2, agent.Snapshot.FollowUpCount);
            Sequence(["s1", "s2"], agent.PeekQueuedMessages().Select(Label));
            release.TrySetResult();
            var result = await run;
            Equal(3, result.Turns.Length);
            Sequence(["initial"], Users(transport.Requests[0]));
            Sequence(["initial", "s1", "s2"], Users(transport.Requests[1]));
            Sequence(["initial", "s1", "s2", "f1", "f2"], Users(transport.Requests[2]));
            Equal(0, agent.Snapshot.SteeringCount); Equal(0, agent.Snapshot.FollowUpCount);
            Check(agent.Snapshot.PendingInputs.IsEmpty, "Consumed pending input remained in recovery state.");
        }
        finally { release.TrySetResult(); try { await run; } catch { } }
    }

    private static async Task ContinueSelection()
    {
        var transport = new ScriptTransport([Message(), Message(), Message(), Message()]);
        await using var agent = Create(transport);
        Throws<InvalidOperationException>(() => agent.ContinueAsync());
        agent.ReplaceMessages([Input("system-only", "system")]);
        Throws<InvalidOperationException>(() => agent.ContinueAsync());
        var seed = Input("seed");
        agent.ReplaceMessages([seed]);
        await agent.ContinueAsync();
        var prior = agent.Snapshot.Messages;
        Throws<InvalidOperationException>(() => agent.ContinueAsync());
        agent.Steer(Input("one")); agent.Steer(Input("two")); agent.FollowUp(Input("follow"));
        var result = await agent.ContinueAsync();
        Equal(3, result.Turns.Length);
        Sequence(["seed", "one"], Users(transport.Requests[1]));
        Sequence(["seed", "one", "two"], Users(transport.Requests[2]));
        Sequence(["seed", "one", "two", "follow"], Users(transport.Requests[3]));
        RawPrefix(prior, transport.Requests[1].Messages);
        Equal(2L, agent.Snapshot.Generation);
        Check(agent.Snapshot.PendingInputs.IsEmpty, "Continue lost pending ownership.");
    }

    private static async Task CancellationAndDisposal()
    {
        var transport = new ScriptTransport([Message()], block: true, gateCleanup: true, throwCancelCallback: true);
        var agent = Create(transport);
        var run = agent.PromptAsync(Input("retained"));
        await transport.Started.Task;
        Check(agent.Abort(), "Abort did not select active generation.");
        await transport.CleanupEntered.Task;
        var idle = agent.WaitForIdleAsync();
        var first = agent.DisposeAsync().AsTask(); var second = agent.DisposeAsync().AsTask();
        try
        {
            Check(ReferenceEquals(first, second) && !first.IsCompleted && !idle.IsCompleted && !run.IsCompleted, "Disposal/idle did not share awaited settlement.");
            Check(agent.Snapshot.IsDisposed && agent.Snapshot.IsRunning, "Disposed admission failed to retain active cleanup state.");
            Throws<ObjectDisposedException>(() => agent.PromptAsync(Input("forbidden")));
            Throws<ObjectDisposedException>(() => agent.Steer(Input("forbidden")));
            using var waiter = new CancellationTokenSource(); waiter.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => agent.WaitForIdleAsync(waiter.Token));
            Check(!idle.IsCompleted, "Cancelling a waiter canceled/settled the Agent.");
            transport.ReleaseCleanup.TrySetResult();
            await first; await second; await idle;
            var canceledResult = await run;
            Equal(AgentLoopStopReason.ChatFailure, canceledResult.Reason);
            Equal(StopReason.Aborted, canceledResult.Turns.Single().Result.Chat.Message.StopReason);
            Equal(1, transport.Cleanups);
            Check(!agent.Snapshot.IsRunning && agent.Snapshot.CancellationCallbackFailed && agent.Snapshot.Failure == AgentFailureKind.Canceled,
                "Cancellation callback failure skipped cleanup or disappeared from state.");
            Sequence(["retained"], agent.Snapshot.Messages.Where(message => message.Role == "user").Select(Label));
            Equal(2, agent.Snapshot.Messages.Length);
            Check(agent.Snapshot.CancellationRequested, "Settled cancellation request disappeared.");
            Check(!agent.Abort(), "Idle abort acquired a disposed run.");
        }
        finally { transport.ReleaseCleanup.TrySetResult(); await agent.DisposeAsync(); }
        await CancellationDuringTool();
    }

    private static async Task CancellationDuringTool()
    {
        var entered = Gate(); var release = Gate();
        var executor = new GatedExecutor(entered, release);
        var transport = new ScriptTransport([Message(tool: true)]);
        await using var agent = Create(transport, [new("read", executor)]);
        using var cancellation = new CancellationTokenSource();
        using var alreadyCanceled = new CancellationTokenSource(); alreadyCanceled.Cancel();
        Throws<OperationCanceledException>(() => agent.PromptAsync(Input("never-admitted"), alreadyCanceled.Token));
        Equal(0L, agent.Snapshot.Generation); Equal(0, transport.Requests.Count);
        var run = agent.PromptAsync(Input("effect-may-complete"), cancellation.Token);
        try
        {
            await entered.Task;
            Equal(2, agent.Snapshot.Messages.Length);
            cancellation.Cancel();
            var idle = agent.WaitForIdleAsync();
            Check(!run.IsCompleted && !idle.IsCompleted && agent.Snapshot.IsRunning,
                "Cancellation released a still-running tool.");
            release.TrySetResult(); await idle;
            Equal(AgentLoopStopReason.ChatFailure, (await run).Reason);
            Equal(1, executor.Calls); Equal(1, agent.Snapshot.CompletedToolOutcomes.Length);
            Check(!agent.Snapshot.CompletedToolOutcomes[0].Result.IsError, "Successful settled effect was changed into cancellation.");
            Equal(3, agent.Snapshot.Messages.Length);
            Equal("toolResult", agent.Snapshot.Messages[^1].Role);
            Equal(AgentFailureKind.Canceled, agent.Snapshot.Failure);
            Equal(1, transport.Cleanups); Equal(1, transport.Requests.Count);
        }
        finally { release.TrySetResult(); try { await run; } catch { } }
    }

    private static async Task CommittedFaultState()
    {
        foreach (var failAtSink in new[] { false, true })
        {
            var executor = new Executor();
            var transport = new ScriptTransport([Message(tool: true)]);
            var sink = new Sink((observation, _) => failAtSink && observation is ToolResultMessageEnded
                ? throw new InvalidOperationException("private sink failure") : ValueTask.CompletedTask);
            var hooks = failAtSink ? null : new AgentHooks(FinishTurn: (_, _) => throw new InvalidOperationException("private finish failure"));
            await using var agent = Create(transport, [new("read", executor)], sink, hooks);
            await ThrowsAsync<InvalidOperationException>(() => agent.PromptAsync(Input("committed")));
            await agent.WaitForIdleAsync();
            var snapshot = agent.Snapshot;
            Equal(3, snapshot.Messages.Length); Equal(1, snapshot.CompletedToolOutcomes.Length);
            Equal("assistant", snapshot.Messages[1].Role); Equal("toolResult", snapshot.Messages[2].Role);
            Equal("effect", snapshot.Messages[2].WireBody.Value.GetProperty("content")[0].GetProperty("text").GetString());
            Equal("1.0", snapshot.Messages[2].WireBody.Value.GetProperty("details").GetProperty("scale").GetRawText());
            Equal(1, executor.Calls); Equal(1, transport.Cleanups); Equal(1, transport.Requests.Count);
            Equal(AgentFailureKind.RunFault, snapshot.Failure);
            Check(snapshot.PendingInputs.IsEmpty && !snapshot.IsRunning, "Fault discarded commit or failed settlement.");
        }
    }

    private static async Task PendingRecovery()
    {
        var transport = new ScriptTransport([Message(), Message()]);
        NativeAgent? observed = null;
        var queued = Input("recover");
        var hooks = new AgentHooks(FinishTurn: (_, _) => { observed!.Steer(queued); return ValueTask.CompletedTask; });
        await using var agent = observed = Create(transport, hooks: hooks, options: new(Loop: new(MaximumTurns: 1)));
        var result = await agent.PromptAsync(Input("initial"));
        Equal(AgentLoopStopReason.TurnLimit, result.Reason);
        Equal(1, agent.Snapshot.PendingInputs.Length); Equal(0, agent.Snapshot.SteeringCount);
        Check(ReferenceEquals(queued, agent.Snapshot.PendingInputs[0]), "Polled uncommitted canonical value was lost.");
        Throws<InvalidOperationException>(() => agent.PromptAsync(Input("silent-recovery")));
        var recovery = agent.TakePendingInputs();
        Check(ReferenceEquals(queued, recovery.Single()), "Recovery replaced canonical input.");
        agent.Configure(new(Model, transport, [])); // End the hook that supplied additional input.
        await agent.PromptAsync(recovery);
        Equal(2, transport.Requests.Count);
        Sequence(["initial", "recover"], Users(transport.Requests[1]));
        Check(agent.Snapshot.PendingInputs.IsEmpty, "Explicit recovered prompt did not commit.");
    }

    private static async Task EndBarrierAndReentrancy()
    {
        NativeAgent? observed = null;
        var entered = Gate(); var release = Gate();
        var transport = new ScriptTransport([Message()]);
        var sink = new Sink(async (observation, _) =>
        {
            Check(observed!.Snapshot.IsRunning, "Callback snapshot lost its active generation.");
            if (observation is AgentLoopEnded)
            {
                Throws<InvalidOperationException>(() => observed!.WaitForIdleAsync());
                Throws<InvalidOperationException>(() => observed!.DisposeAsync());
                entered.TrySetResult(); await release.Task;
            }
        });
        await using var agent = observed = Create(transport, sink: sink);
        var run = agent.PromptAsync(Input("end"));
        try
        {
            await entered.Task;
            var idle = agent.WaitForIdleAsync();
            Check(!run.IsCompleted && !idle.IsCompleted && agent.Snapshot.IsRunning, "Loop end bypassed awaited high-level settlement.");
            Equal(2, agent.Snapshot.Messages.Length); Equal(1, transport.Cleanups);
            release.TrySetResult(); await run; await idle;
            Check(!agent.Snapshot.IsRunning, "Awaited end sink did not release admission.");
        }
        finally { release.TrySetResult(); try { await run; } catch { } }
    }

    private static async Task SubscriptionBarriers()
    {
        var firstEntered = Gate(); var firstRelease = Gate(); var secondEntered = Gate(); var secondRelease = Gate();
        var trace = new List<string>();
        var firstStarts = 0; var secondStarts = 0; var removedStarts = 0;
        var transport = new ScriptTransport([Message(tool: true), Message(), Message()]);
        var executor = new Executor();
        await using var agent = Create(transport, [new("read", executor)], new Sink((observation, _) =>
        {
            if (observation is AssistantMessageEnded { Message.StopReason: StopReason.ToolUse }) trace.Add("primary");
            return ValueTask.CompletedTask;
        }), options: new(MaximumSubscribers: 2));
        var removed = agent.Subscribe(new Sink((observation, _) =>
        { if (observation is AgentLoopStarted) removedStarts++; return ValueTask.CompletedTask; }));
        removed.Dispose(); removed.Dispose();
        var first = agent.Subscribe(new Sink(async (observation, _) =>
        {
            if (observation is AgentLoopStarted) firstStarts++;
            if (observation is AssistantMessageEnded { Message.StopReason: StopReason.ToolUse })
            {
                Equal("assistant", agent.Snapshot.Messages[^1].Role);
                trace.Add("first"); firstEntered.TrySetResult(); await firstRelease.Task;
            }
        }));
        using var second = agent.Subscribe(new Sink(async (observation, _) =>
        {
            if (observation is AgentLoopStarted) secondStarts++;
            if (observation is AssistantMessageEnded { Message.StopReason: StopReason.ToolUse })
            { trace.Add("second"); secondEntered.TrySetResult(); await secondRelease.Task; }
        }));
        Throws<InvalidOperationException>(() => agent.Subscribe(new Sink()));
        var run = agent.PromptAsync(Input("subscribed"));
        try
        {
            await firstEntered.Task;
            Sequence(["primary", "first"], trace);
            Equal(0, executor.Calls); Check(!secondEntered.Task.IsCompleted, "Subscribers overlapped.");
            firstRelease.TrySetResult(); await secondEntered.Task;
            Sequence(["primary", "first", "second"], trace);
            Equal(0, executor.Calls); Equal(2, agent.Snapshot.Messages.Length);
            secondRelease.TrySetResult(); await run;
            Equal(1, executor.Calls);
            first.Dispose(); first.Dispose();
            await agent.PromptAsync(Input("after-unsubscribe"));
            Equal(1, firstStarts); Equal(2, secondStarts); Equal(0, removedStarts);
            await agent.DisposeAsync();
            Throws<ObjectDisposedException>(() => agent.Subscribe(new Sink()));
        }
        finally { firstRelease.TrySetResult(); secondRelease.TrySetResult(); first.Dispose(); try { await run; } catch { } }
    }

    private sealed class ScriptTransport(AssistantMessage[] messages, bool block = false, bool gateCleanup = false,
        bool throwCancelCallback = false) : IChatTransport
    {
        public readonly List<ChatRequest> Requests = [];
        public readonly TaskCompletionSource Started = Gate(), CleanupEntered = Gate(), ReleaseCleanup = Gate();
        public int Cleanups;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            var final = messages[Requests.Count]; Requests.Add(request);
            using var registration = throwCancelCallback ? token.Register(() => throw new InvalidOperationException("private cancellation callback")) : default;
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
                if (gateCleanup) await ReleaseCleanup.Task;
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
    private sealed class GatedExecutor(TaskCompletionSource entered, TaskCompletionSource release) : IToolExecutor
    {
        public int Calls;
        public async ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken token)
        {
            entered.TrySetResult();
            await release.Task; // Deliberately finishes an admitted effect despite cancellation.
            Calls++;
            return new([new("completed effect")], JsonData.EmptyObject);
        }
    }
    private sealed class Sink(Func<AgentEvent, CancellationToken, ValueTask>? callback = null) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) => callback?.Invoke(observation, token) ?? ValueTask.CompletedTask; }
    private static NativeAgent Create(ScriptTransport transport, ImmutableArray<ToolDefinition> tools = default,
        IAgentEventSink? sink = null, AgentHooks? hooks = null, AgentOptions? options = null) =>
        new(new(Model, transport, tools.IsDefault ? [] : tools, Hooks: hooks), () => 123, sink ?? new Sink(), options);
    private static AssistantMessage Message(bool tool = false, string? model = null) => new(Model.Api, Model.Provider, model ?? Model.Id, 0,
        tool ? [new ToolCallContent("call-read", "read", JsonData.Parse("""{"path":"/fake"}"""))] : [new TextContent("done")],
        TokenUsage.Zero, tool ? StopReason.ToolUse : StopReason.Stop);
    private static TranscriptEntry Input(string text, string role = "user") =>
        new(role, JsonData.FromElement(System.Text.Json.JsonSerializer.SerializeToElement(new { role, content = text, timestamp = 123 })));
    private static string Label(TranscriptEntry entry) => entry.WireBody.Value.GetProperty("content").GetString()!;
    private static IEnumerable<string> Users(ChatRequest request) => request.Messages.Where(entry => entry.Role == "user").Select(Label);
    private static void RawPrefix(ImmutableArray<TranscriptEntry> prefix, ImmutableArray<TranscriptEntry> actual)
    { Check(actual.Length >= prefix.Length, "History prefix was omitted."); for (var index = 0; index < prefix.Length; index++) Equal(prefix[index].WireBody.ToString(), actual[index].WireBody.ToString()); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Values differ.");
    private static void Sequence(IEnumerable<string> expected, IEnumerable<string> actual) => Check(expected.SequenceEqual(actual), "Canonical order changed.");
    private static void Throws<T>(Action run) where T : Exception
    { try { run(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task ThrowsAsync<T>(Func<Task> run) where T : Exception
    { try { await run(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
