using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Contracts;

internal static class ToolProgressTests
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("tool progress preserves legacy executor and adapter implementations", LegacyImplementers),
        ("tool invoker normalizes awaited progress after authorization", InvokerProgress),
        ("tool progress rejects malformed owned metadata Unicode finite and budget violations", ProgressAdmission),
        ("parallel tool progress and completions share awaited ordered sink delivery", ParallelBackpressure),
        ("tool finalization joins admitted progress before end and rejects late publication", JoinBeforeEnd),
        ("tool progress cancellation waits for owned execution cleanup", CancellationCleanup),
        ("tool progress sink fault releases bounded queue writers and joins cleanup", SinkFaultCleanup),
        ("tool progress rejects concurrent and callback self-wait publication", CallbackMisuse)
    ];

    private static async Task LegacyImplementers()
    {
        IToolExecutor executor = new LegacyExecutor();
        var observed = 0;
        Equal("legacy executor", (await executor.ExecuteAsync(Invocation(), (_, _) => { observed++; return ValueTask.CompletedTask; }, default)).Content[0].Text);
        IPreparedToolAdapter adapter = new LegacyAdapter();
        Equal("legacy adapter", (await adapter.ExecuteAsync(Action(), (_, _) => { observed++; return ValueTask.CompletedTask; }, default)).Content[0].Text);
        var result = await new ToolInvoker([adapter], new Policy()).ExecuteAsync(Invocation(), (_, _) => { observed++; return ValueTask.CompletedTask; }, default);
        Equal("legacy adapter", result.Content[0].Text); Equal(0, observed);
        var sink = new Sink();
        var batch = await new ToolBatchScheduler([new("lookup", executor)]).RunAsync(Invocation().AssistantMessage, sink);
        Equal("legacy executor", batch.Messages.Single().Content[0].Text);
        Equal(0, sink.Events.OfType<ToolExecutionUpdated>().Count()); Equal(1, sink.Events.OfType<ToolExecutionEnded>().Count());
    }

    private static async Task InvokerProgress()
    {
        var raw = JsonData.Parse("""{ "output":"partial","scale":1.0,"precise":9007199254740993,"nil":null }""");
        var partial = ToolResult.Success("excerpt") with { StructuredContent = raw, Failure = new(ToolFailureKind.ExecutionError, "status") };
        var order = new List<string>(); var callbackEntered = Gate(); var callbackRelease = Gate(); var transforms = 0;
        var policy = new Policy((_, action, _) => { order.Add("policy"); return ValueTask.FromResult(new ToolActionAuthorization(true)); });
        var adapter = new Adapter("lookup", async (action, progress, token) =>
        {
            Check(ReferenceEquals(policy.Action, action), "Progress execution changed the authorized action.");
            order.Add("execute"); await progress(partial, token); order.Add("resumed");
            return ToolResult.Success("final");
        });
        var invoker = new ToolInvoker([adapter], policy, resultTransforms:
            [(_, _, value, _) => { transforms++; return ValueTask.FromResult(value); }]);
        var running = invoker.ExecuteAsync(Invocation(), async (value, token) =>
        {
            Check(value.IsError && ReferenceEquals(raw, value.StructuredContent), "Partial normalization lost failure/metadata.");
            Equal(raw.ToString(), value.StructuredContent!.ToString()); order.Add("update"); callbackEntered.TrySetResult();
            await callbackRelease.Task.WaitAsync(token); order.Add("delivered");
        }, default).AsTask();
        try
        {
            await callbackEntered.Task; Equal(0, transforms); Check(!running.IsCompleted, "Adapter escaped progress backpressure.");
            callbackRelease.TrySetResult(); var result = await running;
            Sequence(["policy", "execute", "update", "delivered", "resumed"], order);
            Equal(1, transforms); Equal("final", result.Content[0].Text);
            Check(result.StructuredContent is null && !result.IsError, "Partial fields leaked into the final result.");
        }
        finally { callbackRelease.TrySetResult(); try { await running; } catch { } }
        var denied = new Policy((_, _, _) => ValueTask.FromResult(new ToolActionAuthorization(false)));
        var unused = new Adapter("lookup"); var updates = 0;
        Failure(await new ToolInvoker([unused], denied).ExecuteAsync(Invocation(), (_, _) => { updates++; return ValueTask.CompletedTask; }, default), ToolFailureKind.Blocked);
        Equal(0, unused.Executions); Equal(0, updates);
    }

    private static async Task ProgressAdmission()
    {
        foreach (var raw in new[] { "{\"output\":\"x\",}", "{\"output\":/*comment*/\"x\"}", "{\"nested\":[1,]}", "{\"nested\":{\"x\":1,}}" })
            await Rejected(ToolResult.Success("") with { StructuredContent = Permissive(raw) });
        foreach (var raw in new[] { "1e309", "{\"number\":-1e999}", "\"\\uD800\"", "\"\\uDC00\"" })
            await Rejected(ToolResult.Success("") with { StructuredContent = JsonData.Parse(raw) });
        foreach (var raw in new[] { "{\"truncation\":{\"content\":\"x\",}}", "{\"detail\":/*comment*/\"x\"}", "{\"detail\":[1,]}" })
            await Rejected(ToolResult.Success("") with { Details = Permissive(raw) });
        foreach (var raw in new[] { "1e309", "{\"number\":-1e999}", "\"\\uDC00\"" })
            await Rejected(ToolResult.Success("") with { Details = JsonData.Parse(raw) });
        foreach (var partial in new ToolResult[]
        {
            null!, new(default, JsonData.EmptyObject), new([null!], JsonData.EmptyObject),
            new([new("\uD800")], JsonData.EmptyObject), new([], JsonData.Parse("{\"detail\":\"\\uD800\"}")), new([], null!)
        }) await Rejected(partial);
        await Rejected(ToolResult.Success("1234567"), new(MaximumResultCharacters: 8));
        await Rejected(new([new(""), new("")], JsonData.EmptyObject), new(MaximumResultContentBlocks: 1));
        await Rejected(ToolResult.Success("") with { StructuredContent = JsonData.Parse("{\"nested\":{}}") }, new(MaximumJsonDepth: 1));
        await Rejected(ToolResult.Success("") with { StructuredContent = JsonData.Null }, new() { MaximumStructuredContentCharacters = 3 });
        await Rejected(ToolResult.Success("") with { Details = JsonData.Parse("{\"nested\":{}}") }, new(MaximumJsonDepth: 1));
        var details = JsonData.Parse("{ \"truncation\":{\"content\":\"tail\\u0000\"},\"scale\":1.0,\"\\u0000opaque\":null }");
        var valid = ToolResult.Success("text\0output") with { Details = details, StructuredContent = JsonData.Parse("\"\\u0000\\uD83D\\uDE42\"") };
        var admitted = 0;
        var result = await Invoker(new Adapter("lookup", async (_, progress, token) => { await progress(valid, token); return ToolResult.Success(""); }))
            .ExecuteAsync(Invocation(), (value, _) =>
            {
                Equal("text\0output", value.Content.Single().Text); Check(ReferenceEquals(valid.StructuredContent, value.StructuredContent), "Valid Unicode metadata was reconstructed.");
                Check(ReferenceEquals(details, value.Details), "Valid output details were reconstructed."); Equal(details.ToString(), value.Details.ToString());
                Equal("tail\0", value.Details.Value.GetProperty("truncation").GetProperty("content").GetString()); admitted++; return ValueTask.CompletedTask;
            }, default);
        Check(!result.IsError, "Supported programmatic output was rejected."); Equal(1, admitted);
        var exactCharacters = valid.Content.Single().Text.Length + details.ToString().Length;
        await Rejected(valid, new(MaximumResultCharacters: exactCharacters - 1));
        var exactUpdates = 0;
        var exact = await Invoker(new Adapter("lookup", async (_, progress, token) => { await progress(valid, token); return valid; }), new(MaximumResultCharacters: exactCharacters))
            .ExecuteAsync(Invocation(), (_, _) => { exactUpdates++; return ValueTask.CompletedTask; }, default);
        Check(!exact.IsError && ReferenceEquals(details, exact.Details), "Exact progress/final details budget was rejected."); Equal(1, exactUpdates);

        static async Task Rejected(ToolResult partial, ToolInvokerOptions? options = null)
        {
            var updates = 0;
            var adapter = new Adapter("lookup", async (_, progress, token) => { await progress(partial, token); return ToolResult.Success(""); });
            var result = await Invoker(adapter, options).ExecuteAsync(Invocation(), (_, _) => { updates++; return ValueTask.CompletedTask; }, default);
            Failure(result, ToolFailureKind.ExecutionError); Equal(0, updates);
            Check(result.Content.All(value => !value.Text.Contains("comment", StringComparison.Ordinal)), "Admission diagnostic leaked rejected metadata.");
        }
    }

    private static async Task ParallelBackpressure()
    {
        var updateAEntered = Gate(); var updateARelease = Gate(); var updateBEntered = Gate(); var updateBRelease = Gate();
        var reportB = Gate(); var finalA = Gate(); var endedB = Gate(); var afterA = 0; var afterB = 0;
        var a = new Adapter("a", async (_, progress, token) => { await progress(ToolResult.Success("a partial"), token); afterA++; await finalA.Task.WaitAsync(token); return ToolResult.Success("a final"); });
        var b = new Adapter("b", async (_, progress, token) => { await reportB.Task.WaitAsync(token); await progress(ToolResult.Success("b partial"), token); afterB++; return ToolResult.Success("b final"); });
        var active = 0; var maximumActive = 0;
        var sink = new Sink(async observation =>
        {
            active++; maximumActive = Math.Max(maximumActive, active);
            try
            {
                if (observation is ToolExecutionUpdated update)
                {
                    Check(ReferenceEquals(update.Invocation.AssistantMessage.Content[update.Invocation.SourceIndex], update.Invocation.Call), "Update lost original call correlation.");
                    if (update.Invocation.Call.Name == "a") { updateAEntered.TrySetResult(); await updateARelease.Task; }
                    else { updateBEntered.TrySetResult(); await updateBRelease.Task; }
                }
                if (observation is ToolExecutionEnded { Outcome.Invocation.Call.Name: "b" }) endedB.TrySetResult();
            }
            finally { active--; }
        });
        var invoker = new ToolInvoker([a, b], new Policy());
        var running = new ToolBatchScheduler([new("a", invoker), new("b", invoker)]).RunAsync(Message("a", "b"), sink);
        try
        {
            await updateAEntered.Task; reportB.TrySetResult(); Equal(0, afterA); Equal(0, afterB);
            Check(!sink.Events.OfType<ToolExecutionEnded>().Any(), "Final end overtook a pending update.");
            updateARelease.TrySetResult(); await updateBEntered.Task; Equal(0, afterB);
            updateBRelease.TrySetResult(); await endedB.Task;
            Check(!running.IsCompleted, "The batch returned before the other tool settled.");
            finalA.TrySetResult(); var batch = await running; Equal(1, maximumActive); Equal(1, afterA); Equal(1, afterB);
            Sequence(["update:a", "update:b", "end:b", "end:a"], Trace(sink));
            Sequence(["a", "b"], batch.Messages.Select(value => value.ToolName));
            Equal("a final", batch.Messages[0].Content[0].Text); Equal("b final", batch.Messages[1].Content[0].Text);
        }
        finally { reportB.TrySetResult(); updateARelease.TrySetResult(); updateBRelease.TrySetResult(); finalA.TrySetResult(); try { await running; } catch { } }
    }

    private static async Task JoinBeforeEnd()
    {
        foreach (var mode in new[] { ToolExecutionMode.Parallel, ToolExecutionMode.Sequential })
        {
            var entered = Gate(); var release = Gate(); var returned = Gate(); var finalized = 0; var afterCalls = 0; ToolProgressCallback? retained = null; Task? pending = null;
            var metadata = JsonData.Parse("{\"progressOnly\":true}");
            var adapter = new Adapter("lookup", (_, progress, token) =>
            {
                retained = progress; pending = progress(ToolResult.Success("partial only") with { StructuredContent = metadata }, token).AsTask();
                returned.TrySetResult(); return ValueTask.FromResult(ToolResult.Success("final only"));
            });
            var invoker = new ToolInvoker([adapter], new Policy(), resultTransforms:
                [(_, _, value, _) => { finalized++; return ValueTask.FromResult(value); }]);
            var hooks = new Hooks((_, value, _) =>
            {
                afterCalls++; Equal("final only", value.Content[0].Text);
                return ValueTask.FromResult(ToolResult.Success("complete hook replacement"));
            });
            var sink = new Sink(async observation => { if (observation is ToolExecutionUpdated) { entered.TrySetResult(); await release.Task; } });
            var running = new ToolBatchScheduler([new("lookup", invoker)], hooks, mode).RunAsync(Invocation().AssistantMessage, sink);
            try
            {
                await entered.Task; await returned.Task; Equal(0, finalized); Equal(0, afterCalls);
                Check(!running.IsCompleted && !sink.Events.OfType<ToolExecutionEnded>().Any(), "Finalization escaped an admitted unawaited update.");
                await ThrowsAsync<InvalidOperationException>(() => retained!(ToolResult.Success("late during close"), default).AsTask());
                release.TrySetResult(); var batch = await running; await pending!; Equal(1, finalized); Equal(1, afterCalls);
                Sequence(["update:lookup", "end:lookup"], Trace(sink));
                Check(ReferenceEquals(metadata, sink.Events.OfType<ToolExecutionUpdated>().Single().PartialResult.StructuredContent), "Partial metadata was lost.");
                Equal("complete hook replacement", batch.Messages.Single().Content[0].Text);
                Check(batch.Outcomes.Single().Result.StructuredContent is null, "Partial metadata entered the final outcome.");
                var count = sink.Events.Count;
                await ThrowsAsync<InvalidOperationException>(() => retained!(ToolResult.Success("late after settlement"), default).AsTask());
                Equal(count, sink.Events.Count);
                Check(!JsonSerializer.Serialize(batch.Messages).Contains("progressOnly", StringComparison.Ordinal), "Progress entered model messages.");
            }
            finally { release.TrySetResult(); try { await running; } catch { } }
        }
    }

    private static async Task CancellationCleanup()
    {
        foreach (var mode in new[] { ToolExecutionMode.Parallel, ToolExecutionMode.Sequential })
        {
            using var cancellation = new CancellationTokenSource(); var update = Gate(); var cleanup = Gate(); var cleanupRelease = Gate(); var never = Gate();
            var adapter = new Adapter("lookup", async (_, progress, token) =>
            {
                try { await progress(ToolResult.Success("running"), token); return ToolResult.Success("unreachable"); }
                finally { cleanup.TrySetResult(); await cleanupRelease.Task; }
            });
            var sink = new Sink(async observation => { if (observation is ToolExecutionUpdated) { update.TrySetResult(); await never.Task.WaitAsync(cancellation.Token); } });
            var running = new ToolBatchScheduler([new("lookup", Invoker(adapter))], executionMode: mode).RunAsync(Invocation().AssistantMessage, sink, cancellation.Token);
            try
            {
                await update.Task; cancellation.Cancel(); await cleanup.Task;
                Check(!running.IsCompleted, "Canceled publication returned before executor cleanup.");
                cleanupRelease.TrySetResult(); await ThrowsAsync<OperationCanceledException>(() => running);
                Equal(0, sink.Events.OfType<ToolExecutionEnded>().Count());
            }
            finally { cleanupRelease.TrySetResult(); try { await running; } catch { } }
        }
    }

    private static async Task SinkFaultCleanup()
    {
        foreach (var mode in new[] { ToolExecutionMode.Parallel, ToolExecutionMode.Sequential })
        {
            var count = mode == ToolExecutionMode.Parallel ? 3 : 1;
            var started = Enumerable.Range(0, count).Select(_ => Gate()).ToArray();
            var report = Enumerable.Range(0, count).Select(_ => Gate()).ToArray();
            var reporting = Enumerable.Range(0, count).Select(_ => Gate()).ToArray();
            var cleanup = Enumerable.Range(0, count).Select(_ => Gate()).ToArray();
            var cleanupRelease = Gate(); var entered = Gate(); var fail = Gate(); var error = new IOException("owned output failed");
            var adapters = Enumerable.Range(0, count).Select(index => new Adapter("tool" + index, async (_, progress, token) =>
            {
                started[index].TrySetResult();
                try
                {
                    await report[index].Task.WaitAsync(token);
                    var publication = progress(ToolResult.Success("partial" + index), token).AsTask(); reporting[index].TrySetResult();
                    await publication; return ToolResult.Success("final");
                }
                finally { cleanup[index].TrySetResult(); await cleanupRelease.Task; }
            })).ToArray();
            var invoker = new ToolInvoker(adapters, new Policy());
            var sink = new Sink(async observation => { if (observation is ToolExecutionUpdated) { entered.TrySetResult(); await fail.Task; throw error; } });
            var running = new ToolBatchScheduler(adapters.Select(value => new ToolDefinition(value.Name, invoker)), executionMode: mode)
                .RunAsync(Message(adapters.Select(value => value.Name).ToArray()), sink);
            try
            {
                await Task.WhenAll(started.Select(value => value.Task)); report[0].TrySetResult(); await entered.Task;
                for (var index = 1; index < count; index++) report[index].TrySetResult();
                await Task.WhenAll(reporting.Select(value => value.Task));
                Check(cleanup.All(value => !value.Task.IsCompleted), "A queued update escaped sink backpressure.");
                fail.TrySetResult(); await Task.WhenAll(cleanup.Select(value => value.Task));
                Check(!running.IsCompleted, "Sink failure did not await all owned cleanup.");
                cleanupRelease.TrySetResult(); var caught = await ThrowsAsync<IOException>(() => running);
                Check(ReferenceEquals(error, caught), "Sink failure was replaced by an executor error.");
                Equal(0, sink.Events.OfType<ToolExecutionEnded>().Count());
            }
            finally { foreach (var gate in report) gate.TrySetResult(); fail.TrySetResult(); cleanupRelease.TrySetResult(); try { await running; } catch { } }
        }
    }

    private static async Task CallbackMisuse()
    {
        var entered = Gate(); var release = Gate(); ToolProgressCallback? retained = null; var updates = 0;
        var adapter = new Adapter("lookup", async (_, progress, token) => { retained = progress; await progress(ToolResult.Success("first"), token); return ToolResult.Success("final"); });
        var running = Invoker(adapter).ExecuteAsync(Invocation(), async (_, _) =>
        {
            updates++; await ThrowsAsync<InvalidOperationException>(() => retained!(ToolResult.Success("recursive"), default).AsTask());
            entered.TrySetResult(); await release.Task;
        }, default).AsTask();
        try
        {
            await entered.Task;
            await ThrowsAsync<InvalidOperationException>(() => retained!(ToolResult.Success("concurrent"), default).AsTask());
            release.TrySetResult(); Check(!(await running).IsError, "Rejected reentrant attempts changed a delivered update."); Equal(1, updates);
            await ThrowsAsync<InvalidOperationException>(() => retained!(ToolResult.Success("late"), default).AsTask());
        }
        finally { release.TrySetResult(); try { await running; } catch { } }

        // A sink must not enqueue another invocation's update and await the same single consumer.
        var bReady = Gate(); var bRelease = Gate(); ToolProgressCallback? bProgress = null; var rejected = false;
        var a = new Adapter("a", async (_, progress, token) => { await bReady.Task.WaitAsync(token); await progress(ToolResult.Success("a"), token); return ToolResult.Success("a final"); });
        var b = new Adapter("b", async (_, progress, token) => { bProgress = progress; bReady.TrySetResult(); await bRelease.Task.WaitAsync(token); return ToolResult.Success("b final"); });
        var sink = new Sink(async observation =>
        {
            if (observation is ToolExecutionUpdated)
            {
                await ThrowsAsync<InvalidOperationException>(() => bProgress!(ToolResult.Success("self wait"), default).AsTask());
                rejected = true; bRelease.TrySetResult();
            }
        });
        var invoker = new ToolInvoker([a, b], new Policy());
        var batch = await new ToolBatchScheduler([new("a", invoker), new("b", invoker)]).RunAsync(Message("a", "b"), sink);
        Check(rejected, "Batch sink did not exercise callback self-wait rejection.");
        Equal(1, sink.Events.OfType<ToolExecutionUpdated>().Count()); Failure(batch.Outcomes.Single(value => value.Invocation.Call.Name == "b").Result, ToolFailureKind.ExecutionError);
    }

    private sealed class LegacyExecutor : IToolExecutor
    { public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(ToolResult.Success("legacy executor")); }

    private sealed class LegacyAdapter : IPreparedToolAdapter
    {
        public string Name => "lookup";
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(Action());
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(true);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(ToolResult.Success("legacy adapter"));
    }

    private sealed class Adapter(string name, Func<PreparedToolAction, ToolProgressCallback, CancellationToken, ValueTask<ToolResult>>? execute = null) : IPreparedToolAdapter
    {
        public string Name => name;
        public int Executions;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(Action(name));
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(true);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token) => ExecuteAsync(action, static (_, _) => ValueTask.CompletedTask, token);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, ToolProgressCallback progress, CancellationToken token)
        { Executions++; return execute?.Invoke(action, progress, token) ?? ValueTask.FromResult(ToolResult.Success("final")); }
    }

    private sealed class Policy(Func<ToolInvocation, PreparedToolAction, CancellationToken, ValueTask<ToolActionAuthorization>>? authorize = null) : IToolActionPolicy
    {
        public PreparedToolAction? Action;
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { Action = action; return authorize?.Invoke(invocation, action, token) ?? ValueTask.FromResult(new ToolActionAuthorization(true)); }
    }

    private sealed class Hooks(Func<ToolInvocation, ToolResult, CancellationToken, ValueTask<ToolResult>> after) : IToolHooks
    {
        public ValueTask<ToolPreflightDecision> BeforeExecutionAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(ToolPreflightDecision.Allow);
        public ValueTask<ToolResult> AfterExecutionAsync(ToolInvocation invocation, ToolResult result, CancellationToken token) => after(invocation, result, token);
    }

    private sealed class Sink(Func<AgentEvent, ValueTask>? emit = null) : IAgentEventSink
    {
        public List<AgentEvent> Events { get; } = [];
        public ValueTask EmitAsync(AgentEvent observation, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Events.Add(observation); return emit?.Invoke(observation) ?? ValueTask.CompletedTask; }
    }

    private static ToolInvoker Invoker(IPreparedToolAdapter adapter, ToolInvokerOptions? options = null) => new([adapter], new Policy(), options: options);
    private static PreparedToolAction Action(string name = "lookup") => new(name, "read", PreparedToolActionKind.Path, "/synthetic/file", JsonData.EmptyObject,
        [], null, ImmutableDictionary<string, string>.Empty);
    private static AssistantMessage Message(params string[] names) => new("fixture-api", "fixture-provider", "fixture-model", 0,
        names.Select(name => (AssistantContent)new ToolCallContent("call-" + name, name, JsonData.EmptyObject)).ToImmutableArray(), TokenUsage.Zero, StopReason.ToolUse);
    private static ToolInvocation Invocation()
    { var message = Message("lookup"); return new(message, (ToolCallContent)message.Content[0], 0); }
    private static JsonData Permissive(string raw)
    { using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); return JsonData.FromElement(document.RootElement); }
    private static IEnumerable<string> Trace(Sink sink) => sink.Events.Select(value => value switch
    { ToolExecutionUpdated update => "update:" + update.Invocation.Call.Name, ToolExecutionEnded end => "end:" + end.Outcome.Invocation.Call.Name, _ => null }).OfType<string>();
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Failure(ToolResult result, ToolFailureKind kind) => Check(result.IsError && result.Failure?.Kind == kind, "Expected " + kind + " failure.");
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Values differ.");
    private static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual) => Check(expected.SequenceEqual(actual), "Sequence differs.");
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task<T> ThrowsAsync<T>(Func<Task> run) where T : Exception
    { try { await run(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
