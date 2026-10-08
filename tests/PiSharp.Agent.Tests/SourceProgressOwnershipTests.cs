using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Contracts;
using NativeAgent = PiSharp.Agent.Agent;

internal static class SourceProgressOwnershipTests
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("source progress nested prepared adapters join actual receipts before result transforms", NestedReceipts),
        ("source progress enforces shared pending, completed-count and retained-character budgets", SharedBudgets),
        ("source progress listener failure and concurrent disposal join all admitted work and cleanup", FaultAndDisposal),
        ("source progress caller cancellation and protocol abort admit until execution settlement", CancellationBoundaries),
        ("high-level Agent exposes explicit serialized native progress mode", ExplicitNativeMode)
    ];
    private static readonly ModelDescriptor Model = new("progress-model", "offline-fake", "offline-authored");

    private static async Task NestedReceipts()
    {
        var entered = Gate(); var release = Gate(); var returning = Gate(); var transforms = 0; var hooks = 0; var delivered = 0;
        ToolProgressCallback? retained = null;
        var adapter = new Adapter("lookup", (_, progress, token) =>
        {
            retained = progress;
            Check(progress(ToolResult.Success("one"), token).IsCompletedSuccessfully, "Prepared source callback blocked.");
            Check(progress(ToolResult.Success("two"), token).IsCompletedSuccessfully, "Second prepared source callback blocked.");
            returning.TrySetResult(); return ValueTask.FromResult(ToolResult.Success("final"));
        });
        var invoker = new ToolInvoker([adapter], new Policy(), resultTransforms:
            [(_, _, value, _) => { transforms++; return ValueTask.FromResult(value); }]);
        var sink = new Sink(async (value, _) =>
        {
            if (value is ToolExecutionUpdated update)
            { delivered++; if (update.PartialResult.Content[0].Text == "one") { entered.TrySetResult(); await release.Task; } }
        });
        var runner = new ToolBatchScheduler([new("lookup", invoker)], new Hooks((_, value, _) =>
            { hooks++; return ValueTask.FromResult(value); }), progressOptions: SourceOptions());
        var running = runner.RunAsync(Message("lookup"), sink);
        try
        {
            await entered.Task; await returning.Task;
            Equal(2, delivered); Equal(0, transforms); Equal(0, hooks);
            Check(!running.IsCompleted, "Nested execution escaped its outer listener receipt.");
            // The adapter has settled; its retained callback ignores even invalid data/canceled arguments.
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            Check(retained!(null!, canceled.Token).IsCompletedSuccessfully, "Late prepared callback performed admission."); Equal(2, delivered);
            release.TrySetResult(); var result = await running;
            Equal(1, transforms); Equal(1, hooks); Equal("final", result.Messages.Single().Content.Single().Text);
            Check(retained!(ToolResult.Success("late"), default).IsCompletedSuccessfully, "Settled callback rejected."); Equal(2, delivered);
        }
        finally { release.TrySetResult(); try { await running; } catch { } }
    }

    private static async Task SharedBudgets()
    {
        // Two source invocations share the pending/character budget; a completed report still consumes total-count quota.
        foreach (var limit in new[] { "pending", "characters", "total" })
        {
            var entered = Gate(); var release = Gate(); var firstReturned = Gate(); var otherStart = Gate(); var otherReport = Gate();
            var secondRejected = Gate(); var updates = 0; var ends = 0;
            var options = limit switch
            {
                "pending" => SourceOptions() with { MaximumPendingUpdates = 1 },
                "characters" => SourceOptions() with { MaximumRetainedCharacters = 6 },
                _ => SourceOptions() with { MaximumUpdates = 1 }
            };
            var a = new Executor(async (_, progress, token) =>
            {
                await otherStart.Task;
                Check(progress(ToolResult.Success("one"), token).IsCompletedSuccessfully, "First quota report failed."); firstReturned.TrySetResult();
                return ToolResult.Success("a final");
            });
            var b = new Executor(async (invocation, progress, token) =>
            {
                otherStart.TrySetResult(); await otherReport.Task;
                try { _ = progress(ToolResult.Success("two"), token); throw new InvalidOperationException("Quota report unexpectedly admitted."); }
                catch (InvalidOperationException error) when (error.Message == "Tool progress limit reached.") { secondRejected.TrySetResult(); }
                return ToolResult.Success("b final");
            });
            var scheduler = new ToolBatchScheduler([new("a", a), new("b", b)], progressOptions: options);
            var running = scheduler.RunAsync(Message("a", "b"), new Sink(async (value, _) =>
            {
                if (value is ToolExecutionUpdated)
                {
                    updates++; entered.TrySetResult();
                    if (limit != "total") await release.Task;
                }
                if (value is ToolExecutionEnded) ends++;
            }));
            try
            {
                await entered.Task; await firstReturned.Task;
                // In the total case delivery has already completed. Count quota must still reject the next invocation.
                otherReport.TrySetResult(); await secondRejected.Task;
                if (limit != "total") Check(!running.IsCompleted, "Rejected quota report bypassed another admitted receipt.");
                release.TrySetResult(); await ThrowsAsync<InvalidOperationException>(() => running);
                Equal(1, updates); Check(ends <= 1, "Quota-rejected tool emitted a final end.");
            }
            finally { otherReport.TrySetResult(); release.TrySetResult(); try { await running; } catch { } }
        }
    }

    private static async Task FaultAndDisposal()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var firstEntered = Gate(); var firstRelease = Gate(); var cleanupEntered = Gate(); var cleanupRelease = Gate(); var executeEntered = Gate();
        var reportRelease = Gate(); var error = new IOException("owned listener failed"); var ends = 0; var cleaned = false;
        var executor = new Executor(async (_, progress, token) =>
        {
            executeEntered.TrySetResult();
            try
            {
                await reportRelease.Task;
                Check(progress(ToolResult.Success("one"), token).IsCompletedSuccessfully, "First source report blocked.");
                Check(progress(ToolResult.Success("two"), token).IsCompletedSuccessfully, "Faulted source report returned its receipt to tool code.");
                await Gate().Task.WaitAsync(token); return ToolResult.Success("unreachable");
            }
            finally { cleanupEntered.TrySetResult(); await cleanupRelease.Task; cleaned = true; }
        });
        var primary = new Sink((value, token) =>
        { token.ThrowIfCancellationRequested(); if (value is ToolExecutionEnded) ends++; return ValueTask.CompletedTask; });
        var agent = Agent(executor, primary);
        using var subscription = agent.Subscribe(new Sink(async (value, _) =>
        {
            if (value is ToolExecutionUpdated update)
            {
                if (update.PartialResult.Content[0].Text == "one") { firstEntered.TrySetResult(); await firstRelease.Task; }
                else throw error;
            }
        }));
        var running = agent.PromptAsync(Input());
        Task? dispose = null;
        try
        {
            await Stage(executeEntered.Task, running, "tool execution", deadline.Token);
            reportRelease.TrySetResult(); await Stage(firstEntered.Task, running, "first listener", deadline.Token);
            await Stage(cleanupEntered.Task, running, "listener failure canceled actual execution and entered cleanup", deadline.Token);
            dispose = agent.DisposeAsync().AsTask(); var concurrent = agent.DisposeAsync().AsTask();
            Check(!running.IsCompleted && !dispose.IsCompleted && !concurrent.IsCompleted, "Fault/disposal abandoned executor cleanup or listener work.");
            cleanupRelease.TrySetResult(); Check(!running.IsCompleted, "Executor cleanup alone bypassed an outstanding listener.");
            firstRelease.TrySetResult(); var caught = await ThrowsAsync<IOException>(() => running);
            Check(ReferenceEquals(error, caught), "Listener failure was converted into a tool result.");
            await dispose; await concurrent; Equal(0, ends); Check(cleaned && agent.Snapshot.IsDisposed && !agent.Snapshot.IsRunning, "Shared cleanup was incomplete.");
        }
        finally
        { reportRelease.TrySetResult(); cleanupRelease.TrySetResult(); firstRelease.TrySetResult(); agent.Abort(); try { await running; } catch { } await agent.DisposeAsync(); }
    }

    private static async Task CancellationBoundaries()
    {
        foreach (var callerCancellation in new[] { false, true })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var caller = new CancellationTokenSource();
            var firstEntered = Gate(); var firstRelease = Gate(); var executeLeaving = Gate(); var callbacks = 0; var cleanups = 0; var ended = 0;
            var executor = new Executor(async (invocation, progress, token) =>
            {
                try
                {
                    _ = progress(ToolResult.Success("one"), token);
                    await Gate().Task.WaitAsync(token);
                    return ToolResult.Success("unreachable");
                }
                catch (OperationCanceledException)
                {
                    Check(progress(ToolResult.Success("after cancellation"), token).IsCompletedSuccessfully, "Cancellation prematurely closed callback admission.");
                    throw;
                }
                finally { cleanups++; executeLeaving.TrySetResult(); }
            });
            await using var agent = Agent(executor, new Sink((value, token) =>
            { token.ThrowIfCancellationRequested(); if (value is ToolExecutionEnded) ended++; return ValueTask.CompletedTask; }));
            using var subscription = agent.Subscribe(new Sink(async (value, token) =>
            {
                if (value is ToolExecutionUpdated)
                {
                    callbacks++;
                    if (callbacks == 1) { firstEntered.TrySetResult(); await firstRelease.Task; }
                    else Check(token.IsCancellationRequested, "Post-cancel listener lost actual abort signal.");
                }
            }));
            var running = agent.PromptAsync(Input(), caller.Token);
            try
            {
                await Stage(firstEntered.Task, running, "listener before cancellation", deadline.Token);
                if (callerCancellation) caller.Cancel(); else Check(agent.Abort(), "Protocol abort was not admitted.");
                await Stage(executeLeaving.Task, running, "post-cancel report and execution cleanup", deadline.Token);
                Equal(2, callbacks); Equal(1, cleanups); Equal(0, ended);
                Check(!running.IsCompleted, "Cancellation bypassed an admitted listener.");
                firstRelease.TrySetResult(); await running; await agent.WaitForIdleAsync(); Equal(1, ended);
            }
            finally { firstRelease.TrySetResult(); agent.Abort(); try { await running; } catch { } }
        }
    }

    private static async Task ExplicitNativeMode()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entered = Gate(); var release = Gate(); var after = false; var callbacks = 0;
        var executor = new Executor(async (_, progress, token) =>
        { await progress(ToolResult.Success("one"), token); after = true; await progress(ToolResult.Success("two"), token); return ToolResult.Success("final"); });
        await using var agent = Agent(executor, new Sink((_, _) => ValueTask.CompletedTask),
            new(ProgressDelivery: new(Mode: ToolProgressDeliveryMode.NativeAwaited)));
        using var subscription = agent.Subscribe(new Sink(async (value, _) =>
        { if (value is ToolExecutionUpdated && ++callbacks == 1) { entered.TrySetResult(); await release.Task; } }));
        var running = agent.PromptAsync(Input());
        try { await Stage(entered.Task, running, "native awaited listener", deadline.Token); Check(!after && callbacks == 1, "Explicit native mode lost backpressure."); release.TrySetResult(); await running; Check(after && callbacks == 2, "Native execution did not finish."); }
        finally { release.TrySetResult(); agent.Abort(); try { await running; } catch { } }
    }

    private static NativeAgent Agent(IToolExecutor executor, IAgentEventSink sink, AgentOptions? options = null) =>
        new(new(Model, new Source(Message("lookup")), [new("lookup", executor)],
            Hooks: new(FinishTurnDecision: (_, _) => ValueTask.FromResult(AgentLoopFinishAction.End))), () => 123, sink, options);
    private static ToolProgressDeliveryOptions SourceOptions() => new(Mode: ToolProgressDeliveryMode.SourceCompatible);
    private static AssistantMessage Message(params string[] tools) => new(Model.Api, Model.Provider, Model.Id, 123,
        tools.Select(name => (AssistantContent)new ToolCallContent("call-" + name, name, JsonData.EmptyObject)).ToImmutableArray(), TokenUsage.Zero, StopReason.ToolUse);
    private static TranscriptEntry Input() => new("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"run tools\",\"timestamp\":123}"));
    private sealed class Source(AssistantMessage message) : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            yield return new StreamStarted(message with { Content = [], StopReason = StopReason.Pending });
            for (var index = 0; index < message.Content.Length; index++)
            {
                var call = (ToolCallContent)message.Content[index];
                yield return new ToolCallStarted(index, call with { Arguments = JsonData.EmptyObject });
                yield return new ToolCallDelta(index, call.Arguments.ToString());
                yield return new ToolCallEnded(index, call);
            }
            yield return new StreamDone(StopReason.ToolUse, message); await Task.CompletedTask;
        }
    }
    private sealed class Executor(Func<ToolInvocation, ToolProgressCallback, CancellationToken, ValueTask<ToolResult>> callback) : IToolExecutor
    {
        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken token) => ExecuteAsync(invocation, (_, _) => ValueTask.CompletedTask, token);
        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, ToolProgressCallback progress, CancellationToken token) => callback(invocation, progress, token);
    }
    private sealed class Adapter(string name, Func<PreparedToolAction, ToolProgressCallback, CancellationToken, ValueTask<ToolResult>> callback) : IPreparedToolAdapter
    {
        public string Name => name;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(new PreparedToolAction(name, "read", PreparedToolActionKind.Path,
            "/authored", invocation.Call.Arguments, [], null, ImmutableDictionary<string, string>.Empty));
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(true);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token) => ExecuteAsync(action, (_, _) => ValueTask.CompletedTask, token);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, ToolProgressCallback progress, CancellationToken token) => callback(action, progress, token);
    }
    private sealed class Policy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(true)); }
    private sealed class Hooks(Func<ToolInvocation, ToolResult, CancellationToken, ValueTask<ToolResult>> callback) : IToolHooks
    {
        public ValueTask<ToolPreflightDecision> BeforeExecutionAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(ToolPreflightDecision.Allow);
        public ValueTask<ToolResult> AfterExecutionAsync(ToolInvocation invocation, ToolResult result, CancellationToken token) => callback(invocation, result, token);
    }
    private sealed class Sink(Func<AgentEvent, CancellationToken, ValueTask> callback) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent value, CancellationToken token) => callback(value, token); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Stage(Task witness, Task<AgentLoopResult> running, string stage, CancellationToken deadline)
    {
        try { await Task.WhenAny(witness, running).WaitAsync(deadline); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        { throw new InvalidOperationException("Actual progress stage did not settle: " + stage); }
        if (!witness.IsCompleted)
        {
            var completed = await running;
            throw new InvalidOperationException("Agent completed before progress stage " + stage + ": " + completed.Reason +
                "; " + completed.Turns.LastOrDefault()?.Result.Chat.Failure?.Message);
        }
        await witness;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, actual {actual}.");
    private static async Task<T> ThrowsAsync<T>(Func<Task> run) where T : Exception
    { try { await run(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
