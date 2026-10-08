using System.Collections.Immutable;
using PiSharp.Agent;
using PiSharp.Contracts;

internal static class ToolInvokerTests
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("tool invoker prepare/transform/revalidate/policy/execute/finalize order", PipelineOrder),
        ("tool invoker transformed denied target has no effects", FinalTargetDenial),
        ("tool invoker final revalidation blocks invalid action and tool substitution", FinalRevalidation),
        ("tool invoker policy and executor share exact command/cwd/env/argv action", ExactCommandAction),
        ("tool invoker direct finalized-call admission rejects truncated/invalid/unknown", DirectAdmission),
        ("tool invoker bounded arguments/actions/results and malformed configuration", Limits),
        ("tool invoker result overrides and sanitized stage failures", OverridesAndFailures),
        ("tool invoker cooperative cancellation awaits cleanup and preserves completed output", CancellationAndCleanup),
        ("tool invoker scheduler assistant/final-result barriers await pipeline", SchedulerBarriers)
    ];

    private static async Task PipelineOrder()
    {
        var order = new List<string>();
        var adapter = new Adapter();
        adapter.Prepare = (invocation, _) =>
        {
            order.Add("prepare");
            return ValueTask.FromResult(Action(invocation.Call.Arguments) with { Arguments = JsonData.Parse("""{"path":"/allowed/resolved","normalized":true}""") });
        };
        adapter.Validate = (action, _) => { order.Add("validate"); return ValueTask.FromResult(action.Arguments.Value.GetProperty("normalized").GetBoolean()); };
        adapter.Execute = (action, _) => { order.Add("execute"); return ValueTask.FromResult(ToolResult.Success("executed")); };
        var policy = new Policy((_, _, _) => { order.Add("policy"); return ValueTask.FromResult(new ToolActionAuthorization(true)); });
        var invoker = new ToolInvoker([adapter], policy,
        [
            (_, action, _) => { order.Add("transform-1"); return ValueTask.FromResult(action with { Target = "/allowed/transformed" }); },
            (_, action, _) => { order.Add("transform-2"); return ValueTask.FromResult(action with { Operation = "write-final" }); }
        ], [(_, _, result, _) => { order.Add("finalize"); return ValueTask.FromResult(result with { Content = [new TextContent("final")], Terminate = true }); }]);
        var invocation = Invocation();
        var original = invocation.Call.Arguments.ToString();
        var result = await invoker.ExecuteAsync(invocation, default);
        Check(!result.IsError && result.Terminate, "Final result did not retain override.");
        Equal("final", result.Content.Single().Text);
        Sequence(["prepare", "validate", "transform-1", "transform-2", "validate", "policy", "execute", "finalize"], order);
        Equal(original, invocation.Call.Arguments.ToString());
        Equal(2, adapter.Validations.Count);
        Check(ReferenceEquals(policy.Actions.Single(), adapter.Executed.Single()), "Execution did not use authorized action instance.");
        Equal("/allowed/transformed", adapter.Executed.Single().Target);
    }

    private static async Task FinalTargetDenial()
    {
        var adapter = new Adapter();
        var policy = new Policy((_, action, _) => ValueTask.FromResult(new ToolActionAuthorization(action.Target.StartsWith("/allowed/", StringComparison.Ordinal), Terminate: true)));
        var invoker = new ToolInvoker([adapter], policy,
            [(_, action, _) => ValueTask.FromResult(action with { Target = "/denied/private-target" })]);
        var result = await invoker.ExecuteAsync(Invocation(), default);
        Failure(result, ToolFailureKind.Blocked);
        Check(result.Terminate, "Policy denial termination was lost.");
        Equal(0, adapter.Executed.Count); Equal(2, adapter.Validations.Count);
        Equal("/denied/private-target", policy.Actions.Single().Target);
        Check(!result.Content.Single().Text.Contains("private", StringComparison.Ordinal), "Denial leaked action target.");
    }

    private static async Task FinalRevalidation()
    {
        foreach (var transform in new ToolActionTransform[]
        {
            (_, action, _) => ValueTask.FromResult(action with { Arguments = JsonData.Parse("[]") }),
            (_, action, _) => ValueTask.FromResult(action with { ToolName = "other" }),
            (_, action, _) => ValueTask.FromResult(action with { Target = "\uD800" }),
            (_, action, _) => ValueTask.FromResult(action with { Target = "/invalid-semantic-target" })
        })
        {
            var adapter = new Adapter { Validate = (action, _) => ValueTask.FromResult(action.Target != "/invalid-semantic-target") };
            var policy = new Policy();
            var result = await new ToolInvoker([adapter], policy, [transform]).ExecuteAsync(Invocation(), default);
            Failure(result, ToolFailureKind.InvalidArguments);
            Equal(0, policy.Actions.Count); Equal(0, adapter.Executed.Count);
        }
        var invalidInitial = new Adapter { Validate = (_, _) => ValueTask.FromResult(false) };
        var transforms = 0;
        var rejected = await new ToolInvoker([invalidInitial], new Policy(),
            [(_, action, _) => { transforms++; return ValueTask.FromResult(action); }]).ExecuteAsync(Invocation(), default);
        Failure(rejected, ToolFailureKind.InvalidArguments); Equal(0, transforms);
    }

    private static async Task ExactCommandAction()
    {
        var environment = ImmutableDictionary<string, string>.Empty.Add("MODE", "transformed").Add("OPAQUE", "\u6587\U0001F642");
        var adapter = new Adapter();
        PreparedToolAction? final = null;
        var policy = new Policy((_, action, _) =>
        {
            Check(ReferenceEquals(final, action), "Policy received a projected or reconstructed action.");
            Equal(PreparedToolActionKind.Command, action.Kind); Equal("run", action.Operation);
            Equal("/synthetic/program", action.Target); Equal("/synthetic/work", action.WorkingDirectory);
            Sequence(["--literal", "a b", ""], action.CommandArguments);
            Equal("transformed", action.Environment["MODE"]); Equal("\u6587\U0001F642", action.Environment["OPAQUE"]);
            Equal("9007199254740993", action.Arguments.Value.GetProperty("precise").GetRawText());
            Equal("1.0", action.Arguments.Value.GetProperty("scale").GetRawText());
            return ValueTask.FromResult(new ToolActionAuthorization(true));
        });
        var invoker = new ToolInvoker([adapter], policy, [(_, action, _) =>
        {
            final = action with { Kind = PreparedToolActionKind.Command, Operation = "run", Target = "/synthetic/program",
                Arguments = JsonData.Parse("""{"precise":9007199254740993,"scale":1.0}"""),
                CommandArguments = ["--literal", "a b", ""], WorkingDirectory = "/synthetic/work", Environment = environment };
            return ValueTask.FromResult(final);
        }]);
        Check(!(await invoker.ExecuteAsync(Invocation(), default)).IsError, "Supported command envelope failed.");
        Check(ReferenceEquals(final, adapter.Executed.Single()), "Executor rederived action fields after policy.");
        Equal("transformed", environment["MODE"]);
    }

    private static async Task DirectAdmission()
    {
        var adapter = new Adapter(); var policy = new Policy();
        var invoker = new ToolInvoker([adapter], policy);
        Failure(await invoker.ExecuteAsync(Invocation(name: "missing"), default), ToolFailureKind.UnknownTool);
        Failure(await invoker.ExecuteAsync(Invocation(stop: StopReason.Length), default), ToolFailureKind.Truncated);
        foreach (var stop in new[] { StopReason.Pending, StopReason.Deferred, StopReason.Error, StopReason.Aborted, StopReason.Stop })
            Failure(await invoker.ExecuteAsync(Invocation(stop: stop), default), ToolFailureKind.InvalidArguments);
        foreach (var raw in new[] { "[]", "null", "1", "\"{partial-private\"" })
            Failure(await invoker.ExecuteAsync(Invocation(arguments: JsonData.Parse(raw)), default), ToolFailureKind.InvalidArguments);
        var original = Invocation();
        foreach (var invocation in new[]
        {
            null!, original with { SourceIndex = -1 }, original with { SourceIndex = 1 },
            original with { Call = original.Call with { } },
            original with { AssistantMessage = original.AssistantMessage with { Content = default } },
            original with { AssistantMessage = original.AssistantMessage with { Content = [original.Call, null!] } },
            original with { AssistantMessage = original.AssistantMessage with { Content = [original.Call, original.Call with { Name = "other" }] } }
        }) Failure(await invoker.ExecuteAsync(invocation, default), ToolFailureKind.InvalidArguments);
        Failure(await invoker.ExecuteAsync(Invocation(arguments: JsonData.Parse("""{"text":"\uD800"}""")), default), ToolFailureKind.InvalidArguments);
        Equal(0, adapter.Prepares); Equal(0, policy.Actions.Count); Equal(0, adapter.Executed.Count);
    }

    private static async Task Limits()
    {
        var invocation = Invocation();
        var argumentSize = invocation.Call.Arguments.ToString().Length;
        var action = Action(invocation.Call.Arguments);
        var actionSize = action.ToolName.Length + action.Operation.Length + action.Target.Length + argumentSize;
        Check(!(await new ToolInvoker([new Adapter()], new Policy(), options: new(MaximumArgumentCharacters: argumentSize,
            MaximumActionCharacters: actionSize, MaximumResultCharacters: 8, MaximumJsonDepth: 1)).ExecuteAsync(invocation, default)).IsError,
            "Exact input/action/result boundaries rejected supported action.");
        foreach (var options in new ToolInvokerOptions[]
        {
            new(MaximumArgumentCharacters: argumentSize - 1), new(MaximumActionCharacters: actionSize - 1)
        })
        {
            var adapter = new Adapter(); var policy = new Policy();
            Failure(await new ToolInvoker([adapter], policy, options: options).ExecuteAsync(invocation, default), ToolFailureKind.InvalidArguments);
            Equal(0, adapter.Executed.Count); Equal(0, policy.Actions.Count);
        }
        Failure(await new ToolInvoker([new Adapter()], new Policy(), options: new(MaximumResultCharacters: 7)).ExecuteAsync(invocation, default), ToolFailureKind.ExecutionError);
        var slots = new Adapter { Prepare = (input, _) => ValueTask.FromResult(Action(input.Call.Arguments) with
            { Kind = PreparedToolActionKind.Command, WorkingDirectory = "/work", CommandArguments = ["", ""] }) };
        Failure(await new ToolInvoker([slots], new Policy(), options: new(MaximumActionEntries: 1)).ExecuteAsync(invocation, default), ToolFailureKind.InvalidArguments);
        var blocks = new Adapter { Execute = (_, _) => ValueTask.FromResult(new ToolResult([new(""), new("")], JsonData.EmptyObject)) };
        Failure(await new ToolInvoker([blocks], new Policy(), options: new(MaximumResultContentBlocks: 1)).ExecuteAsync(invocation, default), ToolFailureKind.ExecutionError);
        var extra = new Adapter { Execute = (_, _) => ValueTask.FromResult(new ToolResult([new("", JsonFields.Empty.Set("opaque", JsonData.Parse("\"private-extra\"")))], JsonData.EmptyObject)) };
        Failure(await new ToolInvoker([extra], new Policy(), options: new(MaximumResultCharacters: 8)).ExecuteAsync(invocation, default), ToolFailureKind.ExecutionError);
        var nested = Invocation(arguments: JsonData.Parse("""{"nested":{}}"""));
        Failure(await new ToolInvoker([new Adapter()], new Policy(), options: new(MaximumJsonDepth: 1)).ExecuteAsync(nested, default), ToolFailureKind.InvalidArguments);
        Throws<ArgumentException>(() => new ToolInvoker([new Adapter(), new Adapter()], new Policy()));
        Throws<ArgumentException>(() => new ToolInvoker([new Adapter()], new Policy(), options: new(MaximumTools: 0)));
        Throws<ArgumentException>(() => new ToolInvoker([new Adapter()], new Policy(), [(_, value, _) => ValueTask.FromResult(value)], options: new(MaximumTransforms: 0)));
    }

    private static async Task OverridesAndFailures()
    {
        foreach (var stage in new[] { "prepare", "transform", "policy", "execute", "finalize" })
        {
            var adapter = new Adapter();
            if (stage == "prepare") adapter.Prepare = (_, _) => throw new InvalidOperationException("private prepare payload");
            if (stage == "execute") adapter.Execute = (_, _) => throw new InvalidOperationException("private execute payload");
            var policy = stage == "policy" ? new Policy((_, _, _) => throw new InvalidOperationException("private policy payload")) : new Policy();
            var invoker = new ToolInvoker([adapter], policy,
                stage == "transform" ? [(_, _, _) => throw new InvalidOperationException("private transform payload")] : null,
                stage == "finalize" ? [(_, _, _, _) => throw new InvalidOperationException("private final payload")] : null);
            var result = await invoker.ExecuteAsync(Invocation(), default);
            Failure(result, stage == "prepare" ? ToolFailureKind.InvalidArguments : stage == "execute" ? ToolFailureKind.ExecutionError : ToolFailureKind.HookError);
            Check(result.Content.All(part => !part.Text.Contains("private", StringComparison.Ordinal)) &&
                !result.Failure!.Message.Contains("private", StringComparison.Ordinal), "Stage exception leaked payload.");
            if (stage == "finalize") Equal("effect", result.Content[0].Text);
        }
        var failedAdapter = new Adapter { Execute = (_, _) => throw new InvalidOperationException("private execution") };
        var finalized = await new ToolInvoker([failedAdapter], new Policy(), resultTransforms:
            [(_, _, result, _) =>
            {
                Failure(result, ToolFailureKind.ExecutionError);
                return ValueTask.FromResult(new ToolResult([new("overridden")], JsonData.Parse("""{"replacement":true}"""), Terminate: true));
            }]).ExecuteAsync(Invocation(), default);
        Check(!finalized.IsError && finalized.Terminate && finalized.Failure is null, "Trusted final result override was not retained.");
        Equal("overridden", finalized.Content.Single().Text);
        Check(finalized.Details.Value.GetProperty("replacement").GetBoolean(), "Result details override was lost.");
    }

    private static async Task CancellationAndCleanup()
    {
        using var before = new CancellationTokenSource(); before.Cancel();
        var unused = new Adapter(); var unusedPolicy = new Policy();
        Failure(await new ToolInvoker([unused], unusedPolicy).ExecuteAsync(Invocation(), before.Token), ToolFailureKind.Canceled);
        Equal(0, unused.Prepares); Equal(0, unusedPolicy.Actions.Count);

        // A false validation result cannot bypass post-await cancellation at either stage.
        // Non-cancelled false controls still report InvalidArguments and never authorize.
        foreach (var validationStage in new[] { 1, 2 })
        foreach (var cancel in new[] { false, true })
        {
            using var validationCancellation = new CancellationTokenSource();
            var validationAdapter = new Adapter();
            validationAdapter.Validate = (_, _) =>
            {
                if (validationAdapter.Validations.Count != validationStage) return ValueTask.FromResult(true);
                if (cancel) validationCancellation.Cancel();
                return ValueTask.FromResult(false);
            };
            var validationPolicy = new Policy();
            var transforms = 0;
            var validationResult = await new ToolInvoker([validationAdapter], validationPolicy,
                [(_, action, _) => { transforms++; return ValueTask.FromResult(action); }])
                .ExecuteAsync(Invocation(), validationCancellation.Token);
            Failure(validationResult, cancel ? ToolFailureKind.Canceled : ToolFailureKind.InvalidArguments);
            Equal(validationStage, validationAdapter.Validations.Count);
            Equal(validationStage - 1, transforms);
            Equal(0, validationPolicy.Actions.Count);
            Equal(0, validationAdapter.Executed.Count);
        }

        using var during = new CancellationTokenSource();
        var entered = Gate(); var release = Gate();
        var transformAdapter = new Adapter();
        var transformed = new ToolInvoker([transformAdapter], new Policy(), [async (_, action, token) =>
            { entered.TrySetResult(); await release.Task.WaitAsync(token); return action; }]).ExecuteAsync(Invocation(), during.Token).AsTask();
        await entered.Task; during.Cancel();
        Failure(await transformed, ToolFailureKind.Canceled); Equal(0, transformAdapter.Executed.Count);

        using var execution = new CancellationTokenSource();
        var started = Gate(); var cleanup = Gate(); var cleanupRelease = Gate(); var never = Gate();
        var adapter = new Adapter { Execute = async (_, token) =>
        {
            started.TrySetResult();
            try { await never.Task.WaitAsync(token); return ToolResult.Success("unreachable"); }
            finally { cleanup.TrySetResult(); await cleanupRelease.Task; }
        } };
        var running = new ToolInvoker([adapter], new Policy()).ExecuteAsync(Invocation(), execution.Token).AsTask();
        try
        {
            await started.Task; execution.Cancel(); await cleanup.Task;
            Check(!running.IsCompleted, "Cancellation returned before admitted adapter cleanup settled.");
            cleanupRelease.TrySetResult(); Failure(await running, ToolFailureKind.Canceled); Equal(1, adapter.Executed.Count);
        }
        finally { cleanupRelease.TrySetResult(); try { await running; } catch { } }

        using var completedCancellation = new CancellationTokenSource();
        var completed = new Adapter { Execute = (_, _) =>
        {
            completedCancellation.Cancel();
            return ValueTask.FromResult(new ToolResult([new("committed")], JsonData.Parse("""{"completed":true}""")));
        } };
        var result = await new ToolInvoker([completed], new Policy()).ExecuteAsync(Invocation(), completedCancellation.Token);
        Failure(result, ToolFailureKind.Canceled);
        Equal("committed", result.Content[0].Text);
        Check(result.Details.Value.GetProperty("completed").GetBoolean(), "Cancellation concealed completed effect details.");
    }

    private static async Task SchedulerBarriers()
    {
        var assistantEntered = Gate(); var assistantRelease = Gate(); var finalEntered = Gate(); var finalRelease = Gate();
        var adapter = new Adapter();
        var invoker = new ToolInvoker([adapter], new Policy(), resultTransforms: [async (_, _, result, _) =>
        {
            finalEntered.TrySetResult(); await finalRelease.Task;
            return result with { Content = [new TextContent("finalized")], Terminate = true };
        }]);
        var sink = new Sink(async observation =>
        {
            if (observation is AssistantMessageEnded) { assistantEntered.TrySetResult(); await assistantRelease.Task; }
        });
        var running = new ToolBatchScheduler([new("edit", invoker)]).RunAsync(Invocation().AssistantMessage, sink);
        try
        {
            await assistantEntered.Task; Equal(0, adapter.Prepares); Equal(0, adapter.Executed.Count);
            assistantRelease.TrySetResult(); await finalEntered.Task;
            Check(!running.IsCompleted && !sink.Events.Any(value => value is ToolExecutionEnded), "Scheduler observed an unfinalized result.");
            finalRelease.TrySetResult();
            var batch = await running;
            Equal(1, batch.Outcomes.Length); Equal(1, batch.Messages.Length);
            Equal("finalized", batch.Messages[0].Content.Single().Text);
            Check(batch.Terminate && !batch.ShouldContinue, "Finalized termination did not reach accepted scheduler.");
        }
        finally { assistantRelease.TrySetResult(); finalRelease.TrySetResult(); try { await running; } catch { } }
    }

    private sealed class Adapter : IPreparedToolAdapter
    {
        public string Name => "edit";
        public int Prepares;
        public readonly List<PreparedToolAction> Validations = [];
        public readonly List<PreparedToolAction> Executed = [];
        public Func<ToolInvocation, CancellationToken, ValueTask<PreparedToolAction>>? Prepare;
        public Func<PreparedToolAction, CancellationToken, ValueTask<bool>>? Validate;
        public Func<PreparedToolAction, CancellationToken, ValueTask<ToolResult>>? Execute;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Prepares++; return Prepare?.Invoke(invocation, token) ?? ValueTask.FromResult(Action(invocation.Call.Arguments)); }
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Validations.Add(action); return Validate?.Invoke(action, token) ?? ValueTask.FromResult(true); }
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Executed.Add(action); return Execute?.Invoke(action, token) ?? ValueTask.FromResult(ToolResult.Success("effect")); }
    }
    private sealed class Policy(Func<ToolInvocation, PreparedToolAction, CancellationToken, ValueTask<ToolActionAuthorization>>? authorize = null) : IToolActionPolicy
    {
        public readonly List<PreparedToolAction> Actions = [];
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Actions.Add(action); return authorize?.Invoke(invocation, action, token) ?? ValueTask.FromResult(new ToolActionAuthorization(true)); }
    }
    private sealed class Sink(Func<AgentEvent, ValueTask> emit) : IAgentEventSink
    {
        public readonly List<AgentEvent> Events = [];
        public async ValueTask EmitAsync(AgentEvent observation, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Events.Add(observation); await emit(observation); }
    }
    private static PreparedToolAction Action(JsonData arguments) => new("edit", "write", PreparedToolActionKind.Path,
        "/allowed/file", arguments, [], null, ImmutableDictionary<string, string>.Empty);
    private static ToolInvocation Invocation(string name = "edit", JsonData? arguments = null, StopReason stop = StopReason.ToolUse)
    {
        var call = new ToolCallContent("call-edit", name, arguments ?? JsonData.Parse("""{"path":"./file"}"""));
        var message = new AssistantMessage("fixture-api", "fixture-provider", "fixture-model", 0, [call], TokenUsage.Zero, stop);
        return new(message, call, 0);
    }
    private static void Failure(ToolResult result, ToolFailureKind kind)
    { Check(result.IsError && result.Failure?.Kind == kind, "Expected structured " + kind + " failure."); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Sequence(IEnumerable<string> expected, IEnumerable<string> actual) => Check(expected.SequenceEqual(actual), "Pipeline order changed.");
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Values differ.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static T Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
