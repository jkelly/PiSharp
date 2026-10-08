using System.Collections.Concurrent;
using System.Collections.Immutable;
using PiSharp.Agent;
using PiSharp.Contracts;

internal static class NestedToolInvocationTests
{
    internal const string Prefix = "nested-broker-core.";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "failed middle start retains all three queued children until predecessor settles", FailedQueuedStart),
        (Prefix + "nested listener failure cannot be swallowed by parent", NestedListenerFailure),
        (Prefix + "source nested listener failure cancels execution and joins original cleanup", NestedSourceFailure),
        (Prefix + "descendant usage sums once while results retain their own usage", Usage),
        (Prefix + "omitted records and error results still contribute usage", OmittedUsage),
        (Prefix + "transformed inner action is revalidated and denied before any effect", FinalAction),
        (Prefix + "same-name recursion retains IDs and stops only at the depth limit", Recursion),
        (Prefix + "all descendants share the admission budget", SharedBudget),
        (Prefix + "ignored child task joins before parent settlement and stale calls reject", IgnoredChild),
        (Prefix + "cancellation waits for the original child finally", Cancellation),
        (Prefix + "sequential siblings queue while exclusive descendants reenter", Sequential),
        (Prefix + "nested source progress shares quotas and joins actual receipts", Progress),
        (Prefix + "unknown invalid blocked and thrown inner calls are outcomes", Failures),
        (Prefix + "policy and ancestor capabilities cannot admit nested effects", Authority),
        (Prefix + "bounded descendant record appears only on the root transcript", Record)
    ];

    private static async Task FailedQueuedStart()
    {
        var releaseFirst = Gate(); var middleStarted = Gate(); var failMiddle = Gate(); var thirdStarted = Gate();
        var cancelledFirst = Gate(); var error = new IOException("middle start failed");
        var children = new List<Task<ToolOutcome>>(); var effects = 0;
        var inner = new Adapter("inner", async (_, _, _, token) =>
        {
            Interlocked.Increment(ref effects);
            using var registration = token.Register(() => cancelledFirst.TrySetResult());
            await releaseFirst.Task; return ToolResult.Success("first settled");
        });
        var outer = Outer((context, _) =>
        {
            for (var i = 0; i < 3; i++) children.Add(context.ExecuteToolAsync("inner", JsonData.EmptyObject).AsTask());
            return ValueTask.FromResult(ToolResult.Success("ignored children"));
        });
        var sink = new Sink(async value =>
        {
            if (value is ToolExecutionStarted start && start.Invocation.Call.Id == "root/2")
            { middleStarted.TrySetResult(); await failMiddle.Task; throw error; }
            if (value is ToolExecutionStarted third && third.Invocation.Call.Id == "root/3") thirdStarted.TrySetResult();
        });
        var executor = ToolInvoker.WithNestedCalls([outer, inner], new Policy(), new(50) { ExecutionMode = ToolExecutionMode.Sequential });
        var work = new ToolBatchScheduler([new("outer", executor)]).RunAsync(Call().AssistantMessage, sink);
        try
        {
            await Reach(middleStarted.Task, work); await Reach(thirdStarted.Task, work);
            failMiddle.TrySetResult(); await Reach(cancelledFirst.Task, work);
            Equal(1, effects); Check(!work.IsCompleted);
            Check(children.All(child => !child.IsCompleted));
        }
        finally { failMiddle.TrySetResult(); releaseFirst.TrySetResult(); await ExpectListener(work, error); }
        Equal(1, effects); Check(children.All(child => child.IsCompleted));
    }

    private static async Task NestedListenerFailure()
    {
        foreach (var mode in new[] { ToolExecutionMode.Parallel, ToolExecutionMode.Sequential })
        {
            var error = new IOException("nested start listener"); var swallowed = false;
            var inner = new Adapter("inner", (_, _, _, _) => throw new InvalidOperationException("must not execute"));
            var outer = Outer(async (context, token) =>
            {
                var result = await context.ExecuteToolAsync("inner", JsonData.EmptyObject, token);
                swallowed = result.IsError; return ToolResult.Success("handled");
            });
            var executor = ToolInvoker.WithNestedCalls([outer, inner], new Policy(), new(51));
            var sink = new Sink(value => value is ToolExecutionStarted start && start.Invocation.ParentToolCallId is not null
                ? ValueTask.FromException(error) : ValueTask.CompletedTask);
            await ExpectListener(new ToolBatchScheduler([new("outer", executor)], executionMode: mode)
                .RunAsync(Call().AssistantMessage, sink), error);
            Check(swallowed); Check(!sink.Events.OfType<ToolResultMessageStarted>().Any());
        }
    }

    private static async Task NestedSourceFailure()
    {
        var error = new IOException("nested source update listener"); var cleanup = Gate(); var release = Gate();
        var inner = new Adapter("inner", async (_, _, progress, token) =>
        {
            try
            {
                Check(progress(ToolResult.Success("update"), token).IsCompletedSuccessfully);
                await Gate().Task.WaitAsync(token); return ToolResult.Success("unreachable");
            }
            finally { cleanup.TrySetResult(); await release.Task; }
        });
        var outer = Outer(async (context, token) =>
        { await context.ExecuteToolAsync("inner", JsonData.EmptyObject, token); return ToolResult.Success("handled"); });
        var executor = ToolInvoker.WithNestedCalls([outer, inner], new Policy(), new(52));
        var sink = new Sink(value => value is ToolExecutionUpdated update && update.Invocation.ParentToolCallId is not null
            ? ValueTask.FromException(error) : ValueTask.CompletedTask);
        var work = new ToolBatchScheduler([new("outer", executor)], progressOptions: new(Mode: ToolProgressDeliveryMode.SourceCompatible))
            .RunAsync(Call().AssistantMessage, sink);
        try { await Reach(cleanup.Task, work); Check(!work.IsCompleted); }
        finally { release.TrySetResult(); await ExpectListener(work, error); }
        Check(!sink.Events.OfType<ToolExecutionEnded>().Any());
    }

    private static async Task ExpectListener(Task work, Exception expected)
    {
        try { await work; }
        catch (Exception error) { Check(ReferenceEquals(error, expected)); return; }
        throw new InvalidOperationException("Listener failure was swallowed.");
    }

    private static JsonData UsageValue(int value, bool splits = false) => JsonData.Parse(
        System.Text.Json.JsonSerializer.Serialize(new { input = value, output = value, cacheRead = value,
            cacheWrite = value, totalTokens = value * 4,
            cost = new { input = value, output = value, cacheRead = value, cacheWrite = value, total = value * 4 } })[..^1] +
            (splits ? ",\"cacheWrite1h\":2,\"reasoning\":3}" : "}"));

    private static async Task Usage()
    {
        ToolOutcome? middleResult = null;
        var leaf = new Adapter("leaf", (_, _, _, _) => ValueTask.FromResult(ToolResult.Success("leaf") with { Usage = UsageValue(4, true) }));
        var middle = new Adapter("middle", async (invocation, _, _, token) =>
        {
            await invocation.Context!.ExecuteToolAsync("leaf", JsonData.EmptyObject, token);
            return ToolResult.Success("middle") with { Usage = UsageValue(2) };
        });
        var outer = Outer(async (context, token) =>
        {
            middleResult = await context.ExecuteToolAsync("middle", JsonData.EmptyObject, token);
            return ToolResult.Success("root") with { Usage = UsageValue(1) };
        });
        var invoker = ToolInvoker.WithNestedCalls([outer, middle, leaf], new Policy(), new(40));
        var batch = await new ToolBatchScheduler([new("outer", invoker)]).RunAsync(Call().AssistantMessage, new Sink());
        Equal(2, middleResult!.Result.Usage!.Value.GetProperty("input").GetInt32());
        Equal(1, batch.Outcomes.Single().Result.Usage!.Value.GetProperty("input").GetInt32());
        var usage = batch.Messages.Single().Usage!.Value;
        Equal(7, usage.GetProperty("input").GetInt32()); Equal(28, usage.GetProperty("totalTokens").GetInt32());
        Equal(28, usage.GetProperty("cost").GetProperty("total").GetInt32());
        Equal(2, usage.GetProperty("cacheWrite1h").GetInt32()); Equal(3, usage.GetProperty("reasoning").GetInt32());
        Equal(7, ToolResultMessageMaterializer.ToTranscript(batch.Messages.Single(), 2).WireBody.Value.GetProperty("usage").GetProperty("input").GetInt32());
        // Materialization is pure: repeat projection cannot add descendants a second time.
        Equal(7, ToolResultMessageMaterializer.Create(batch.Outcomes.Single()).Usage!.Value.GetProperty("input").GetInt32());
    }

    private static async Task OmittedUsage()
    {
        var inner = new Adapter("inner", (_, _, _, _) => ValueTask.FromResult(
            ToolResult.Error(ToolFailureKind.ExecutionError, "reported failure") with { Usage = UsageValue(3) }));
        var outer = Outer(async (context, token) =>
        {
            await context.ExecuteToolAsync("inner", JsonData.EmptyObject, token);
            await context.ExecuteToolAsync("inner", JsonData.EmptyObject, token);
            return ToolResult.Success("handled");
        });
        var invoker = ToolInvoker.WithNestedCalls([outer, inner], new Policy(), new(41));
        var batch = await new ToolBatchScheduler([new("outer", invoker)]).RunAsync(Call(id: new string('r', 65533)).AssistantMessage, new Sink());
        var message = batch.Messages.Single();
        Equal(0, message.NestedCalls!.Value.GetProperty("calls").GetArrayLength());
        Check(!message.NestedCalls.Value.GetProperty("complete").GetBoolean());
        Equal(6, message.Usage!.Value.GetProperty("input").GetInt32());
        Check(!message.Usage.Value.TryGetProperty("reasoning", out _));
    }

    private static async Task FinalAction()
    {
        var order = new List<string>(); var effects = 0;
        var inner = new Adapter("inner", (_, _, _, _) => { effects++; return ValueTask.FromResult(ToolResult.Success("effect")); })
        {
            Validate = (action, _) => { order.Add("validate:" + action.Target); return ValueTask.FromResult(true); }
        };
        ToolOutcome? nested = null;
        var outer = Outer(async (context, token) =>
        { nested = await context.ExecuteToolAsync("inner", JsonData.Parse("{\"path\":\"allowed\"}"), token); return ToolResult.Success("handled"); });
        var policy = new Policy((invocation, action, _) =>
        {
            if (invocation.ParentToolCallId is not null)
            {
                Equal("root", invocation.ParentToolCallId); Equal("root/1", invocation.Call.Id);
                Equal(19L, invocation.Context!.SessionGeneration); Equal(1, invocation.Context.CallDepth);
                order.Add("policy:" + action.Target);
            }
            return ValueTask.FromResult(new ToolActionAuthorization(action.Target != "denied"));
        });
        var executor = ToolInvoker.WithNestedCalls([outer, inner], policy, new(19), transforms:
            [(invocation, action, _) => ValueTask.FromResult(invocation.ParentToolCallId is null ? action : action with { Target = "denied" })]);
        await executor.ExecuteAsync(Call(), default);
        Equal(ToolFailureKind.Blocked, nested!.Result.Failure!.Kind); Equal(0, effects);
        Equal("validate:allowed|validate:denied|policy:denied", string.Join("|", order));
    }

    private static async Task Recursion()
    {
        var ids = new List<string>(); ToolOutcome? refused = null;
        var loop = new Adapter("loop", async (invocation, _, _, token) =>
        {
            ids.Add(invocation.Call.Id);
            var child = await invocation.Context!.ExecuteToolAsync("loop", JsonData.EmptyObject, token);
            if (child.Result.Failure?.Kind == ToolFailureKind.Blocked) refused = child;
            return ToolResult.Success("completed");
        });
        await ToolInvoker.WithNestedCalls([loop], new Policy(), new(20, MaximumDepth: 2)).ExecuteAsync(Call("loop"), default);
        Equal("root|root/1|root/1/1", string.Join("|", ids));
        Equal("root/1/1/1", refused!.Invocation.Call.Id); Equal(ToolFailureKind.Blocked, refused.Result.Failure!.Kind);
    }

    private static async Task SharedBudget()
    {
        var effects = 0;
        var inner = new Adapter("inner", (_, _, _, _) => { effects++; return ValueTask.FromResult(ToolResult.Success("done")); });
        var outcomes = new List<ToolOutcome>();
        var outer = Outer(async (context, token) =>
        {
            for (var index = 0; index < 3; index++) outcomes.Add(await context.ExecuteToolAsync("inner", JsonData.EmptyObject, token));
            return ToolResult.Success("done");
        });
        await ToolInvoker.WithNestedCalls([outer, inner], new Policy(), new(21, MaximumNestedCalls: 2)).ExecuteAsync(Call(), default);
        Equal(2, effects); Check(outcomes.Take(2).All(value => !value.IsError)); Equal(ToolFailureKind.Blocked, outcomes[2].Result.Failure!.Kind);
    }

    private static async Task IgnoredChild()
    {
        var entered = Gate(); var release = Gate(); var effects = 0; ToolInvocationContext? retained = null;
        var inner = new Adapter("inner", async (_, _, _, _) => { effects++; entered.TrySetResult(); await release.Task; return ToolResult.Success("done"); });
        var outer = Outer((context, token) =>
        { retained = context; _ = context.ExecuteToolAsync("inner", JsonData.EmptyObject).AsTask(); return ValueTask.FromResult(ToolResult.Success("returned")); });
        var work = ToolInvoker.WithNestedCalls([outer, inner], new Policy(), new(22)).ExecuteAsync(Call(), default).AsTask();
        try
        {
            await Reach(entered.Task, work); Check(!work.IsCompleted);
            var late = await retained!.ExecuteToolAsync("inner", JsonData.EmptyObject);
            Equal(ToolFailureKind.Blocked, late.Result.Failure!.Kind); Equal(1, effects);
        }
        finally { release.TrySetResult(); await work; }
        Check(!work.Result.IsError);
        Equal(ToolFailureKind.Blocked, (await retained!.ExecuteToolAsync("inner", JsonData.EmptyObject)).Result.Failure!.Kind);
    }

    private static async Task Cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = Gate(); var cancelled = Gate(); var cleanup = Gate(); var closed = false;
        var inner = new Adapter("inner", async (_, _, _, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); return ToolResult.Success("unexpected"); }
            finally { cancelled.TrySetResult(); await cleanup.Task; closed = true; }
        });
        var outer = Outer(async (context, token) => (await context.ExecuteToolAsync("inner", JsonData.EmptyObject, token)).Result);
        var work = ToolInvoker.WithNestedCalls([outer, inner], new Policy(), new(23, cancellation.Token)).ExecuteAsync(Call(), default).AsTask();
        try
        {
            await Reach(entered.Task, work); cancellation.Cancel(); await Reach(cancelled.Task, work);
            Check(!work.IsCompleted && !closed);
        }
        finally { cancellation.Cancel(); cleanup.TrySetResult(); await work; }
        Check(closed); Equal(ToolFailureKind.Canceled, work.Result.Failure!.Kind);
    }

    private static async Task Sequential()
    {
        var leafEntered = Gate(); var release = Gate(); var order = new List<string>();
        var leaf = new Adapter("leaf", async (invocation, _, _, _) =>
        { order.Add(invocation.Call.Id); leafEntered.TrySetResult(); await release.Task; return ToolResult.Success("leaf"); });
        var middle = new Adapter("middle", async (invocation, _, _, token) =>
        { order.Add(invocation.Call.Id); return (await invocation.Context!.ExecuteToolAsync("leaf", JsonData.EmptyObject, token)).Result; });
        var outer = Outer(async (context, token) =>
        {
            var first = context.ExecuteToolAsync("middle", JsonData.EmptyObject, token).AsTask();
            var second = context.ExecuteToolAsync("middle", JsonData.EmptyObject, token).AsTask();
            await Task.WhenAll(first, second); return ToolResult.Success("joined");
        });
        var scopes = new ToolInvocationScopeOptions(24) { ExecutionMode = ToolExecutionMode.Sequential };
        var work = ToolInvoker.WithNestedCalls([outer, middle, leaf], new Policy(), scopes).ExecuteAsync(Call(), default).AsTask();
        try { await Reach(leafEntered.Task, work); Equal("root/1|root/1/1", string.Join("|", order)); Check(!work.IsCompleted); }
        finally { release.TrySetResult(); await work; }
        Equal("root/1|root/1/1|root/2|root/2/1", string.Join("|", order));
    }

    private static async Task Progress()
    {
        var entered = Gate(); var release = Gate(); var updates = 0;
        var inner = new Adapter("inner", async (_, _, progress, token) =>
        { await progress(ToolResult.Success("first"), token); await progress(ToolResult.Success("second"), token); return ToolResult.Success("unused"); });
        var outer = Outer(async (context, token) => (await context.ExecuteToolAsync("inner", JsonData.EmptyObject, token)).Result);
        var executor = ToolInvoker.WithNestedCalls([outer, inner], new Policy(), new(25));
        var sink = new Sink(async observation =>
        {
            if (observation is ToolExecutionUpdated update)
            { Equal("root", update.Invocation.ParentToolCallId); updates++; entered.TrySetResult(); await release.Task; }
        });
        var work = new ToolBatchScheduler([new("outer", executor)], progressOptions:
            new(ToolProgressDeliveryMode.SourceCompatible, MaximumUpdates: 1, MaximumPendingUpdates: 1)).RunAsync(Call().AssistantMessage, sink);
        try { await Reach(entered.Task, work); Check(!work.IsCompleted); Equal(1, updates); }
        finally { release.TrySetResult(); await work; }
        Equal(1, updates); Check(work.Result.Outcomes.Single().IsError);
    }

    private static async Task Failures()
    {
        var effects = 0; var outcomes = new List<ToolOutcome>();
        ToolInvocationContext? captured = null;
        var inner = new Adapter("inner", (_, _, _, _) => { effects++; throw new InvalidOperationException("private failure"); });
        var hooks = new Hooks((invocation, _, _) => ValueTask.FromResult(new PreparedToolCallHookResult(Block:
            invocation.ParentToolCallId is not null && invocation.Call.Arguments.Value.TryGetProperty("block", out _))));
        var outer = Outer(async (context, token) =>
        {
            captured = context;
            ToolProgressCallback multicast = static (_, _) => ValueTask.CompletedTask;
            multicast += multicast;
            Equal(ToolFailureKind.InvalidArguments, (await context.ExecuteToolAsync("inner", JsonData.EmptyObject, multicast, token)).Result.Failure!.Kind);
            outcomes.Add(await context.ExecuteToolAsync("missing", JsonData.EmptyObject, token));
            outcomes.Add(await context.ExecuteToolAsync("inner", JsonData.Parse("[]"), token));
            outcomes.Add(await context.ExecuteToolAsync("inner", JsonData.Parse("{\"block\":true}"), token));
            outcomes.Add(await context.ExecuteToolAsync("inner", JsonData.EmptyObject, token));
            // Deliberately malformed trusted input: no null/empty identity may be invented in the record.
            outcomes.Add(await context.ExecuteToolAsync(null!, JsonData.EmptyObject, token));
            return ToolResult.Success("handled");
        });
        await ToolInvoker.WithNestedCalls([outer, inner], new Policy(), new(26), hooks).ExecuteAsync(Call(), default);
        Equal("UnknownTool|InvalidArguments|Blocked|ExecutionError|InvalidArguments", string.Join("|", outcomes.Select(value => value.Result.Failure!.Kind)));
        Equal(1, effects); Check(outcomes.All(value => value.IsError && !value.Result.Content.Any(part => part.Text.Contains("private failure", StringComparison.Ordinal))));
        var record = captured!.NestedCalls!.Value;
        Check(!record.GetProperty("complete").GetBoolean());
        Equal(4, record.GetProperty("calls").GetArrayLength());
        Check(record.GetProperty("calls").EnumerateArray().All(call => call.GetProperty("name").GetString() is { Length: > 0 }));
    }

    private static async Task Authority()
    {
        ToolInvocationContext? ancestor = null; var effects = 0; ToolOutcome? before = null, forged = null;
        var leaf = new Adapter("leaf", (_, _, _, _) => { effects++; return ValueTask.FromResult(ToolResult.Success("leaf")); });
        var inner = new Adapter("inner", async (invocation, _, _, token) =>
        {
            forged = await ancestor!.ExecuteToolAsync("leaf", JsonData.EmptyObject, token);
            return (await invocation.Context!.ExecuteToolAsync("leaf", JsonData.EmptyObject, token)).Result;
        });
        var outer = Outer(async (context, token) => { ancestor = context; return (await context.ExecuteToolAsync("inner", JsonData.EmptyObject, token)).Result; });
        var policy = new Policy(async (invocation, _, token) =>
        {
            if (invocation.ParentToolCallId is null) before = await invocation.Context!.ExecuteToolAsync("leaf", JsonData.EmptyObject, token);
            return new ToolActionAuthorization(true);
        });
        await ToolInvoker.WithNestedCalls([outer, inner, leaf], policy, new(27)).ExecuteAsync(Call(), default);
        Equal(ToolFailureKind.Blocked, before!.Result.Failure!.Kind); Equal(ToolFailureKind.Blocked, forged!.Result.Failure!.Kind); Equal(1, effects);
    }

    private static async Task Record()
    {
        var inner = new Adapter("inner", (_, _, _, _) => ValueTask.FromResult(ToolResult.Success("done")));
        var outer = Outer(async (context, token) =>
        {
            await context.ExecuteToolAsync("inner", JsonData.Parse("{\"large\":\"" + new string('x', 9000) + "\"}"), token);
            await context.ExecuteToolAsync("missing", JsonData.EmptyObject, token);
            return ToolResult.Success("root");
        });
        var sink = new Sink();
        var batch = await new ToolBatchScheduler([new("outer", ToolInvoker.WithNestedCalls([outer, inner], new Policy(), new(28)))]).RunAsync(Call().AssistantMessage, sink);
        Equal(1, batch.Messages.Length); Equal(1, sink.Events.OfType<ToolResultMessageStarted>().Count());
        Equal(3, sink.Events.OfType<ToolExecutionEnded>().Count());
        var transcript = ToolResultMessageMaterializer.ToTranscript(batch.Messages.Single(), 1).WireBody.Value;
        var record = transcript.GetProperty("nestedCalls"); Check(!record.GetProperty("complete").GetBoolean());
        Equal(2, record.GetProperty("calls").GetArrayLength());
        Check(!record.GetProperty("calls")[0].TryGetProperty("arguments", out _));
        Check(record.GetProperty("calls")[0].GetProperty("argumentsBytes").GetInt32() > 8192);
        Equal("error", record.GetProperty("calls")[1].GetProperty("status").GetString());
        Check(!batch.Outcomes.Single().Result.HasProperty("nestedCalls"));
        var longBatch = await new ToolBatchScheduler([new("outer", ToolInvoker.WithNestedCalls([outer, inner], new Policy(), new(29)))]).
            RunAsync(Call(id: new string('r', 65533)).AssistantMessage, new Sink());
        var longRecord = longBatch.Messages.Single().NestedCalls!.Value;
        Check(!longRecord.GetProperty("complete").GetBoolean()); Equal(0, longRecord.GetProperty("calls").GetArrayLength());
        var deep = "0";
        for (var index = 0; index < 31; index++) deep = "{\"n\":" + deep + "}";
        var deepOuter = Outer(async (context, token) => (await context.ExecuteToolAsync("inner", JsonData.Parse(deep), token)).Result);
        var deepBatch = await new ToolBatchScheduler([new("outer", ToolInvoker.WithNestedCalls([deepOuter, inner], new Policy(), new(30)))]).
            RunAsync(Call().AssistantMessage, new Sink());
        var deepRecord = deepBatch.Messages.Single().NestedCalls!.Value;
        Check(!deepRecord.GetProperty("complete").GetBoolean());
        Check(!deepRecord.GetProperty("calls")[0].TryGetProperty("arguments", out _));
        ToolResultMessageMaterializer.ToTranscript(deepBatch.Messages.Single(), 2);
    }

    private static Adapter Outer(Func<ToolInvocationContext, CancellationToken, ValueTask<ToolResult>> execute) =>
        new("outer", (invocation, _, _, token) => execute(invocation.Context!, token));
    private sealed class Adapter(string name, Func<ToolInvocation, PreparedToolAction, ToolProgressCallback, CancellationToken, ValueTask<ToolResult>> execute)
        : IInvocationPreparedToolAdapter
    {
        public string Name => name;
        internal Func<PreparedToolAction, CancellationToken, ValueTask<bool>>? Validate;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) =>
            ValueTask.FromResult(new PreparedToolAction(Name, "invoke", PreparedToolActionKind.Path,
                invocation.Call.Arguments.Value.TryGetProperty("path", out var path) ? path.GetString()! : "allowed",
                invocation.Call.Arguments, [], null, ImmutableDictionary<string, string>.Empty));
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => Validate?.Invoke(action, token) ?? ValueTask.FromResult(true);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token) => throw new InvalidOperationException("Invocation-aware execution was bypassed.");
        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, PreparedToolAction action, ToolProgressCallback progress, CancellationToken token) =>
            execute(invocation, action, progress, token);
    }
    private sealed class Policy(Func<ToolInvocation, PreparedToolAction, CancellationToken, ValueTask<ToolActionAuthorization>>? authorize = null) : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => authorize?.Invoke(invocation, action, token) ?? ValueTask.FromResult(new ToolActionAuthorization(true)); }
    private sealed class Hooks(Func<ToolInvocation, PreparedToolAction, CancellationToken, ValueTask<PreparedToolCallHookResult>> before) : IPreparedToolHooks
    {
        public ValueTask<PreparedToolCallHookResult> BeforeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => before(invocation, action, token);
        public ValueTask<JsonData?> AfterAsync(ToolInvocation invocation, PreparedToolAction action, ToolResult result, bool isError, CancellationToken token) => ValueTask.FromResult<JsonData?>(null);
    }
    private sealed class Sink(Func<AgentEvent, ValueTask>? emit = null) : IAgentEventSink
    {
        internal ConcurrentQueue<AgentEvent> Events { get; } = new();
        public async ValueTask EmitAsync(AgentEvent value, CancellationToken token) { Events.Enqueue(value); if (emit is not null) await emit(value); }
    }
    private static ToolInvocation Call(string name = "outer", string id = "root")
    { var call = new ToolCallContent(id, name, JsonData.EmptyObject); return new(new("fixture-api", "fixture-provider", "fixture-model", 1, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Reach(Task entered, Task work) { if (await Task.WhenAny(entered, work) == work && !entered.IsCompleted) { await work; throw new InvalidOperationException("Work settled before the required boundary."); } await entered; }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual));
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Nested tool invocation assertion failed."); }
}
