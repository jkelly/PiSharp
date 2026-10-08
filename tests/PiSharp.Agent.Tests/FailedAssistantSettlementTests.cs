using System.Collections.Immutable;
using PiSharp.Agent;
using PiSharp.Contracts;

internal static class FailedAssistantSettlementTests
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("failed and aborted provisional identities commit as owned nonexecutable history", FailedBodies),
        ("failed assistant settlement awaits the authoritative commit barrier", AssistantBarrier),
        ("failed assistant commit faults propagate without tool admission", SinkFailure),
        ("failed assistant cancellation retains the sink delivery contract", Cancellation),
        ("failed settlement preserves unfinished envelope and executable identity rejection", Admission)
    ];

    private static async Task FailedBodies()
    {
        foreach (var reason in new[] { StopReason.Error, StopReason.Aborted })
        foreach (var mode in new[] { ToolExecutionMode.Parallel, ToolExecutionMode.Sequential })
        foreach (var body in ProvisionalBodies())
        {
            var probe = new Probe(); var sink = new Sink(); var message = Message(reason, body);
            var before = PiWireJson.WriteMessage(message).ToString();
            var result = await Scheduler(probe, mode).RunAsync(message, sink);
            Empty(result, reason == StopReason.Aborted); probe.Untouched();
            Equal(1, sink.Events.Count);
            var committed = (sink.Events.Single() as AssistantMessageEnded)?.Message
                ?? throw new InvalidOperationException("Failed history did not reach the assistant commit barrier.");
            Check(ReferenceEquals(message, committed), "Settlement reconstructed the failed assistant.");
            Equal(before, PiWireJson.WriteMessage(committed).ToString());
            var roundtrip = PiWireJson.ReadMessage(PiWireJson.WriteMessage(committed).Value);
            Equal(before, PiWireJson.WriteMessage(roundtrip).ToString());
            foreach (var call in committed.Content.OfType<ToolCallContent>())
                Check(body.Any(part => ReferenceEquals(part, call)), "Settlement replaced an owned provisional call.");
        }
    }

    private static async Task AssistantBarrier()
    {
        foreach (var reason in new[] { StopReason.Error, StopReason.Aborted })
        foreach (var mode in new[] { ToolExecutionMode.Parallel, ToolExecutionMode.Sequential })
        {
            var probe = new Probe(); var entered = Gate(); var release = Gate(); var committed = false;
            var message = Message(reason, ProvisionalBodies()[0]);
            var sink = new Sink(async (observation, _) =>
            {
                Check(observation is AssistantMessageEnded, "Failure settlement emitted a tool observation.");
                entered.TrySetResult(); await release.Task; committed = true;
            });
            var running = Scheduler(probe, mode).RunAsync(message, sink);
            await entered.Task;
            var returnedBeforeCommit = running.IsCompleted; var committedBeforeRelease = committed;
            var callsBeforeRelease = probe.Total; var eventsBeforeRelease = sink.Events.Count;
            release.TrySetResult();
            var result = await running;
            Check(!returnedBeforeCommit && !committedBeforeRelease, "Failure settlement bypassed the awaited commit.");
            Equal(0, callsBeforeRelease); Equal(1, eventsBeforeRelease);
            Check(committed, "The authoritative commit did not complete.");
            Empty(result, reason == StopReason.Aborted); probe.Untouched(); Equal(1, sink.Events.Count);
        }
    }

    private static async Task SinkFailure()
    {
        foreach (var reason in new[] { StopReason.Error, StopReason.Aborted })
        foreach (var mode in new[] { ToolExecutionMode.Parallel, ToolExecutionMode.Sequential })
        {
            var probe = new Probe(); var entered = Gate(); var release = Gate();
            var failure = new IOException("authoritative failed-assistant commit rejected");
            var sink = new Sink(async (_, _) => { entered.TrySetResult(); await release.Task; throw failure; });
            var running = Scheduler(probe, mode).RunAsync(Message(reason, ProvisionalBodies()[3]), sink);
            await entered.Task;
            var returnedBeforeFailure = running.IsCompleted; var callsBeforeFailure = probe.Total;
            release.TrySetResult();
            var caught = await ThrowsAsync<IOException>(() => running);
            Check(!returnedBeforeFailure, "Failure settlement returned before the commit fault.");
            Check(ReferenceEquals(failure, caught), "An authoritative sink fault was converted or replaced.");
            Equal(0, callsBeforeFailure); probe.Untouched(); Equal(1, sink.Events.Count);
        }
    }

    private static async Task Cancellation()
    {
        foreach (var reason in new[] { StopReason.Error, StopReason.Aborted })
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var probe = new Probe(); CancellationToken received = default;
            var accepting = new Sink((_, token) => { received = token; return ValueTask.CompletedTask; });
            var result = await Scheduler(probe).RunAsync(Message(reason, ProvisionalBodies()[0]), accepting, cancellation.Token);
            Equal(cancellation.Token, received); Empty(result, canceled: true);
            Equal(1, accepting.Events.Count); probe.Untouched();

            var rejecting = new Sink((_, token) => { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; });
            var caught = await ThrowsAsync<OperationCanceledException>(() =>
                Scheduler(probe).RunAsync(Message(reason, ProvisionalBodies()[3]), rejecting, cancellation.Token));
            Equal(cancellation.Token, caught.CancellationToken); Equal(1, rejecting.Events.Count); probe.Untouched();
        }
    }

    private static async Task Admission()
    {
        var probe = new Probe(); var scheduler = Scheduler(probe);
        foreach (var reason in new[] { StopReason.Pending, StopReason.Deferred })
        {
            var sink = new Sink();
            await ThrowsAsync<ArgumentException>(() => scheduler.RunAsync(Message(reason, ProvisionalBodies()[0]), sink));
            Equal(0, sink.Events.Count);
        }
        foreach (var reason in new[] { StopReason.Error, StopReason.Aborted, StopReason.ToolUse, StopReason.Length })
        {
            var sink = new Sink();
            await ThrowsAsync<ArgumentException>(() => scheduler.RunAsync(Message(reason, default), sink));
            Equal(0, sink.Events.Count);
        }
        foreach (var reason in new[] { StopReason.ToolUse, StopReason.Length })
        foreach (var body in ProvisionalBodies().Append(ImmutableArray.Create<AssistantContent>(new ToolCallContent("valid-call", "read", null!))))
        {
            var sink = new Sink();
            await ThrowsAsync<ArgumentException>(() => scheduler.RunAsync(Message(reason, body), sink));
            Equal(0, sink.Events.Count);
        }
        probe.Untouched();

        // Calibrate the same real invoker so zero failed-path counters prove those stages were bypassed.
        var valid = await scheduler.RunAsync(Message(StopReason.ToolUse, [new ToolCallContent("valid-call", "read", JsonData.EmptyObject)]), new Sink());
        Check(valid.Outcomes.Length == 1 && !valid.Outcomes[0].IsError, "Executable counter control did not run.");
        Equal(1, probe.Prepared); Check(probe.Validated > 0, "The schema-validation counter control was not reached.");
        Equal(1, probe.Authorized); Equal(1, probe.Executed); Equal(1, probe.Before); Equal(1, probe.After);
    }

    private static ImmutableArray<AssistantContent>[] ProvisionalBodies() =>
    [
        [new TextContent("partial before failure"), new ToolCallContent("", "", JsonData.EmptyObject)],
        [new ToolCallContent("", "read", JsonData.Parse("{\"nil\":null,\"scale\":1.0,\"precise\":9007199254740993,\"zero\":-0}"))],
        [new ToolCallContent("known-call", "", JsonData.EmptyObject)],
        [new ToolCallContent("same-call", "read", JsonData.EmptyObject),
            new ToolCallContent("same-call", "read", JsonData.Parse("{\"unfinishedDisplay\":\"{\\\"x\\\":\"}"))]
    ];

    private static AssistantMessage Message(StopReason reason, ImmutableArray<AssistantContent> content) =>
        new("authored-api", "authored-provider", "authored-model", 17, content, TokenUsage.Zero, reason);

    private static ToolBatchScheduler Scheduler(Probe probe, ToolExecutionMode mode = ToolExecutionMode.Parallel) =>
        new([new("read", new ToolInvoker([probe], probe))], probe, mode);

    private sealed class Probe : IPreparedToolAdapter, IToolActionPolicy, IToolHooks
    {
        public string Name => "read";
        public int Prepared, Validated, Authorized, Executed, Before, After;
        public int Total => Prepared + Validated + Authorized + Executed + Before + After;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token)
        {
            Prepared++;
            return ValueTask.FromResult(new PreparedToolAction(Name, "read", PreparedToolActionKind.Path, "/synthetic/no-effect",
                invocation.Call.Arguments, [], null, ImmutableDictionary<string, string>.Empty));
        }
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token)
        { Validated++; return ValueTask.FromResult(true); }
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { Authorized++; return ValueTask.FromResult(new ToolActionAuthorization(true)); }
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token)
        { Executed++; return ValueTask.FromResult(ToolResult.Success("unexpected execution")); }
        public ValueTask<ToolPreflightDecision> BeforeExecutionAsync(ToolInvocation invocation, CancellationToken token)
        { Before++; return ValueTask.FromResult(ToolPreflightDecision.Allow); }
        public ValueTask<ToolResult> AfterExecutionAsync(ToolInvocation invocation, ToolResult result, CancellationToken token)
        { After++; return ValueTask.FromResult(result); }
        public void Untouched() => Check(Total == 0, "Failed history entered tool preparation, validation, policy, hooks or execution.");
    }

    private sealed class Sink(Func<AgentEvent, CancellationToken, ValueTask>? emit = null) : IAgentEventSink
    {
        public List<AgentEvent> Events { get; } = [];
        public ValueTask EmitAsync(AgentEvent observation, CancellationToken token)
        { Events.Add(observation); return emit?.Invoke(observation, token) ?? ValueTask.CompletedTask; }
    }

    private static void Empty(ToolBatchResult result, bool canceled)
    {
        Check(result.Outcomes.IsEmpty && result.Messages.IsEmpty && !result.ShouldContinue && !result.Terminate,
            "Failed history produced a tool batch or continuation.");
        Equal(canceled, result.IsCanceled);
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task<T> ThrowsAsync<T>(Func<Task> run) where T : Exception
    { try { await run(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
