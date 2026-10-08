using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Contracts;
using NativeAgent = PiSharp.Agent.Agent;

internal static class BoundedToolProgressDeliveryTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly ModelDescriptor Model = new("bounded-progress-model", "offline-fake", "offline-authored");
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("native progress receipt joins nested prepared delivery, charges once and releases before next report", NestedReceipts);
        yield return ("native progress receipt preserves ordinary source overlap, fallback and closed-source admission", OrdinaryAndFallback);
        yield return ("native progress receipt retains individual, total, shared pending and character quotas", UnchangedQuotas);
        yield return ("native progress receipt joins listener faults, actual abort and concurrent owned disposal", FaultAbortAndClose);
    }

    private static async Task NestedReceipts()
    {
        foreach (var mode in new[] { ToolProgressDeliveryMode.SourceCompatible, ToolProgressDeliveryMode.NativeAwaited })
        {
            var entered = new[] { Gate(), Gate() }; var release = new[] { Gate(), Gate() }; var reported = Gate();
            var delivered = 0; var resumed = 0; var transforms = 0; var hooks = 0; var ends = 0; var pendingReceipt = false;
            ToolProgressCallback? retained = null;
            var adapter = new Adapter("read", async (_, progress, token) =>
            {
                retained = progress;
                for (var index = 0; index < 2; index++)
                {
                    var receipt = ToolProgressDelivery.ReportAndWaitAsync(progress, ToolResult.Success(index == 0 ? "one" : "two"), token);
                    if (index == 0) { pendingReceipt = !receipt.IsCompleted; reported.TrySetResult(); }
                    await receipt; resumed++;
                }
                return ToolResult.Success("final");
            });
            var invoker = new ToolInvoker([adapter], new Policy(), resultTransforms:
                [(_, _, result, _) => { transforms++; return ValueTask.FromResult(result); }]);
            // A report costs exactly {} (2) + text (3). A second charge in Forward would
            // reject the very first report; failure to release would reject the second.
            var options = new ToolProgressDeliveryOptions(mode, MaximumUpdates: 2, MaximumPendingUpdates: 1, MaximumRetainedCharacters: 5);
            var scheduler = new ToolBatchScheduler([new("read", invoker)], new Hooks((_, value, _) => { hooks++; return ValueTask.FromResult(value); }), progressOptions: options);
            var running = scheduler.RunAsync(Message("read"), new Sink(async (observation, _) =>
            {
                if (observation is ToolExecutionUpdated)
                { var index = delivered++; entered[index].TrySetResult(); await release[index].Task; }
                if (observation is ToolExecutionEnded) ends++;
            }));
            try
            {
                await Stage(entered[0].Task, running, "first nested actual delivery"); await Stage(reported.Task, running, "returned pending receipt");
                Check(pendingReceipt && resumed == 0 && transforms == 0 && hooks == 0 && ends == 0 && !running.IsCompleted,
                    "The producer or finalization overtook its nested owned delivery.");
                release[0].TrySetResult(); await Stage(entered[1].Task, running, "released budget admits the second report");
                Equal(1, resumed); Equal(0, transforms); Equal(0, hooks); Equal(0, ends);
                release[1].TrySetResult(); var result = await running.WaitAsync(Deadline);
                Equal(2, resumed); Equal(2, delivered); Equal(1, transforms); Equal(1, hooks); Equal(1, ends);
                Check(!result.Outcomes.Single().Result.IsError && result.Messages.Single().Content[0].Text == "final", "Receipt pacing changed finalized output.");
                using var canceled = new CancellationTokenSource(); canceled.Cancel();
                if (mode == ToolProgressDeliveryMode.SourceCompatible)
                    Check(ToolProgressDelivery.ReportAndWaitAsync(retained!, null!, canceled.Token).IsCompletedSuccessfully, "Late source receipt performed admission.");
                else await Throws<InvalidOperationException>(() => ToolProgressDelivery.ReportAndWaitAsync(retained!, ToolResult.Success("late"), canceled.Token).AsTask());
                Equal(2, delivered);
            }
            finally { foreach (var item in release) item.TrySetResult(); await Ignore(running); }
        }
    }

    private static async Task OrdinaryAndFallback()
    {
        var entered = Gate(); var release = Gate(); var returned = Gate(); var updates = 0; var ends = 0;
        ToolProgressCallback? retained = null;
        var executor = new Executor((_, progress, token) =>
        {
            retained = progress;
            Check(progress(ToolResult.Success("one"), token).IsCompletedSuccessfully, "First ordinary source callback blocked.");
            Check(progress(ToolResult.Success("two"), token).IsCompletedSuccessfully, "Ordinary source overlap was disabled.");
            returned.TrySetResult(); return ValueTask.FromResult(ToolResult.Success("final"));
        });
        var running = new ToolBatchScheduler([new("read", executor)], progressOptions: SourceOptions(MaximumUpdates: 2, MaximumPendingUpdates: 2, MaximumRetainedCharacters: 10))
            .RunAsync(Message("read"), new Sink(async (observation, _) =>
            {
                if (observation is ToolExecutionUpdated && ++updates == 1) { entered.TrySetResult(); await release.Task; }
                if (observation is ToolExecutionEnded) ends++;
            }));
        try
        {
            await Stage(entered.Task, running, "ordinary blocked first listener"); await Stage(returned.Task, running, "ordinary overlapping callback returns");
            Equal(2, updates); Equal(0, ends); Check(!running.IsCompleted, "Ordinary callback return bypassed owned settlement.");
            release.TrySetResult(); await running.WaitAsync(Deadline); Equal(1, ends);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            Check(ToolProgressDelivery.ReportAndWaitAsync(retained!, null!, canceled.Token).IsCompletedSuccessfully, "Source late receipt stopped being ignored."); Equal(2, updates);
        }
        finally { release.TrySetResult(); await Ignore(running); }

        var fallbackEntered = Gate(); var fallbackRelease = Gate(); var fallbackCalls = 0;
        ToolProgressCallback fallback = async (_, _) => { fallbackCalls++; fallbackEntered.TrySetResult(); await fallbackRelease.Task; };
        var work = ToolProgressDelivery.ReportAndWaitAsync(fallback, ToolResult.Success("fallback")).AsTask();
        try { await fallbackEntered.Task.WaitAsync(Deadline); Check(!work.IsCompleted, "Opaque callback return was discarded."); fallbackRelease.TrySetResult(); await work.WaitAsync(Deadline); Equal(1, fallbackCalls); }
        finally { fallbackRelease.TrySetResult(); await Ignore(work); }
        var calls = 0;
        ToolProgressCallback combined = (_, _) => { calls++; return ValueTask.CompletedTask; };
        combined += (_, _) => { calls++; return ValueTask.CompletedTask; };
        await ToolProgressDelivery.ReportAndWaitAsync(combined, ToolResult.Success("multicast")); Equal(2, calls);
    }

    private static async Task UnchangedQuotas()
    {
        foreach (var kind in new[] { "individual", "total" })
        {
            var updates = 0; var afterRejected = 0;
            var executor = new Executor(async (_, progress, token) =>
            {
                await ToolProgressDelivery.ReportAndWaitAsync(progress, ToolResult.Success(kind == "individual" ? "four" : "one"), token);
                await ToolProgressDelivery.ReportAndWaitAsync(progress, ToolResult.Success("two"), token);
                afterRejected++; return ToolResult.Success("unexpected");
            });
            var scheduler = new ToolBatchScheduler([new("read", executor)], progressOptions: SourceOptions(MaximumUpdates: 1, MaximumPendingUpdates: 1, MaximumRetainedCharacters: 5));
            var error = await Throws<InvalidOperationException>(() => scheduler.RunAsync(Message("read"), new Sink((observation, _) =>
            { if (observation is ToolExecutionUpdated) updates++; return ValueTask.CompletedTask; })));
            Equal("Tool progress limit reached.", error.Message); Equal(kind == "individual" ? 0 : 1, updates); Equal(0, afterRejected);
        }
        foreach (var kind in new[] { "pending", "characters" })
        {
            var entered = Gate(); var release = Gate(); var rejected = Gate(); var updates = 0; var afterRejected = 0; var ends = 0;
            var first = new Executor(async (_, progress, token) =>
            {
                await ToolProgressDelivery.ReportAndWaitAsync(progress, ToolResult.Success("one"), token);
                token.ThrowIfCancellationRequested(); return ToolResult.Success("first final");
            });
            var second = new Executor(async (_, progress, token) =>
            {
                await entered.Task;
                try { await ToolProgressDelivery.ReportAndWaitAsync(progress, ToolResult.Success("two"), token); afterRejected++; return ToolResult.Success("unexpected"); }
                catch (InvalidOperationException error) when (error.Message == "Tool progress limit reached.") { rejected.TrySetResult(); throw; }
            });
            var options = SourceOptions(MaximumUpdates: 2, MaximumPendingUpdates: kind == "pending" ? 1 : 2, MaximumRetainedCharacters: kind == "characters" ? 5 : 10);
            var running = new ToolBatchScheduler([new("first", first), new("second", second)], progressOptions: options).RunAsync(Message("first", "second"), new Sink(async (observation, _) =>
            {
                if (observation is ToolExecutionUpdated) { updates++; entered.TrySetResult(); await release.Task; }
                if (observation is ToolExecutionEnded) ends++;
            }));
            try
            {
                await Stage(entered.Task, running, "shared admitted receipt"); await Stage(rejected.Task, running, "unchanged shared " + kind + " quota");
                Check(!running.IsCompleted, "Quota failure abandoned the other admitted delivery."); Equal(1, updates); Equal(0, afterRejected); Equal(0, ends);
                release.TrySetResult(); var error = await Throws<InvalidOperationException>(() => running); Equal("Tool progress limit reached.", error.Message);
                Equal(1, updates); Equal(0, afterRejected); Equal(0, ends);
            }
            finally { release.TrySetResult(); await Ignore(running); }
        }
    }

    private static async Task FaultAbortAndClose()
    {
        foreach (var fault in new[] { false, true })
        {
            var entered = Gate(); var release = Gate(); var reported = Gate(); var cleanupEntered = Gate(); var cleanupRelease = Gate();
            var receiptPending = false; var receiptSettled = false; var receiptFailed = false; var cancellationAtCleanup = false; var cleaned = false;
            var effects = 0; var updates = 0; ToolProgressCallback? retained = null;
            var adapter = new Adapter("read", async (_, progress, token) =>
            {
                retained = progress;
                try
                {
                    var receipt = ToolProgressDelivery.ReportAndWaitAsync(progress, ToolResult.Success("partial"), token);
                    receiptPending = !receipt.IsCompleted; reported.TrySetResult();
                    try { await receipt; receiptSettled = true; } catch { receiptFailed = true; throw; }
                    token.ThrowIfCancellationRequested(); effects++; return ToolResult.Success("unexpected");
                }
                finally { cancellationAtCleanup = token.IsCancellationRequested; cleanupEntered.TrySetResult(); await cleanupRelease.Task; cleaned = true; }
            });
            var provider = new Source(Message("read"));
            await using var agent = new NativeAgent(new(Model, provider, [new("read", new ToolInvoker([adapter], new Policy()))],
                Hooks: new(FinishTurnDecision: (_, _) => ValueTask.FromResult(AgentLoopFinishAction.End))), () => 123,
                new Sink((_, _) => ValueTask.CompletedTask));
            using var subscription = agent.Subscribe(new Sink(async (observation, _) =>
            {
                if (observation is ToolExecutionUpdated && Interlocked.Increment(ref updates) == 1)
                { entered.TrySetResult(); await release.Task; if (fault) throw new InvalidOperationException("authored receipt listener fault"); }
            }));
            var running = agent.PromptAsync(new TranscriptEntry("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"run\",\"timestamp\":123}")));
            var idle = agent.WaitForIdleAsync(); Task? firstDispose = null, concurrentDispose = null;
            try
            {
                await Stage(entered.Task, running, "actual owned source receipt listener"); await Stage(reported.Task, running, "producer receives pending ownership receipt");
                Check(receiptPending && !receiptSettled && !receiptFailed && !cleanupEntered.Task.IsCompleted, "Receipt did not await actual delivery.");
                if (!fault)
                {
                    Check(agent.Abort(), "Actual public abort was not admitted.");
                    using var canceled = new CancellationTokenSource(); canceled.Cancel();
                    Check(ToolProgressDelivery.ReportAndWaitAsync(retained!, ToolResult.Success("admitted after abort"), canceled.Token).IsCompletedSuccessfully,
                        "Receipt utility rejected an open source callback after abort.");
                    Equal(2, updates); Check(!receiptSettled && !cleanupEntered.Task.IsCompleted && !idle.IsCompleted, "Abort cut an admitted receipt's ownership wait.");
                }
                release.TrySetResult(); await Stage(cleanupEntered.Task, running, "actual executor cleanup after receipt settlement");
                Check(cancellationAtCleanup && effects == 0 && (fault ? receiptFailed : receiptSettled), "Actual cancellation/fault did not precede further tool effects.");
                firstDispose = agent.DisposeAsync().AsTask(); concurrentDispose = agent.DisposeAsync().AsTask();
                Check(!firstDispose.IsCompleted && !concurrentDispose.IsCompleted && !running.IsCompleted && !idle.IsCompleted,
                    "Owned concurrent disposal skipped actual execution cleanup.");
                cleanupRelease.TrySetResult();
                if (fault) await Throws<InvalidOperationException>(() => running);
                else await running.WaitAsync(Deadline);
                await firstDispose.WaitAsync(Deadline); await concurrentDispose.WaitAsync(Deadline); await idle.WaitAsync(Deadline);
                Check(cleaned && agent.Snapshot.IsDisposed && !agent.Snapshot.IsRunning, "Owned receipt/cleanup did not finish shared settlement.");
                Equal(0, effects); Equal(1, provider.Requests);
                var count = updates; using var canceledLate = new CancellationTokenSource(); canceledLate.Cancel();
                Check(ToolProgressDelivery.ReportAndWaitAsync(retained!, null!, canceledLate.Token).IsCompletedSuccessfully, "Closed source receipt performed admission after fault/abort settlement.");
                Equal(count, updates);
                Check(agent.Snapshot.Messages.All(value => !value.WireBody.ToString().Contains("partial", StringComparison.Ordinal) &&
                    !value.WireBody.ToString().Contains("admitted after abort", StringComparison.Ordinal)), "Partial report acquired canonical tool output authority.");
            }
            finally
            {
                release.TrySetResult(); cleanupRelease.TrySetResult(); agent.Abort(); await Ignore(running); await Ignore(idle);
                if (firstDispose is not null) await Ignore(firstDispose); if (concurrentDispose is not null) await Ignore(concurrentDispose);
            }
        }
    }

    private static ToolProgressDeliveryOptions SourceOptions(int MaximumUpdates = 4096, int MaximumPendingUpdates = 256, int MaximumRetainedCharacters = 8 * 1024 * 1024) =>
        new(ToolProgressDeliveryMode.SourceCompatible, MaximumUpdates, MaximumPendingUpdates, MaximumRetainedCharacters);
    private static AssistantMessage Message(params string[] names) => new(Model.Api, Model.Provider, Model.Id, 123,
        names.Select(name => (AssistantContent)new ToolCallContent("call-" + name, name, JsonData.EmptyObject)).ToImmutableArray(), TokenUsage.Zero, StopReason.ToolUse);
    private sealed class Adapter(string name, Func<PreparedToolAction, ToolProgressCallback, CancellationToken, ValueTask<ToolResult>> callback) : IPreparedToolAdapter
    {
        public string Name => name;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(new PreparedToolAction(name, "read", PreparedToolActionKind.Path,
            "/authored", invocation.Call.Arguments, [], null, ImmutableDictionary<string, string>.Empty));
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(true);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token) => callback(action, (_, _) => ValueTask.CompletedTask, token);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, ToolProgressCallback progress, CancellationToken token) => callback(action, progress, token);
    }
    private sealed class Executor(Func<ToolInvocation, ToolProgressCallback, CancellationToken, ValueTask<ToolResult>> callback) : IToolExecutor
    {
        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken token) => callback(invocation, (_, _) => ValueTask.CompletedTask, token);
        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, ToolProgressCallback progress, CancellationToken token) => callback(invocation, progress, token);
    }
    private sealed class Policy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(true)); }
    private sealed class Hooks(Func<ToolInvocation, ToolResult, CancellationToken, ValueTask<ToolResult>> after) : IToolHooks
    {
        public ValueTask<ToolPreflightDecision> BeforeExecutionAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(ToolPreflightDecision.Allow);
        public ValueTask<ToolResult> AfterExecutionAsync(ToolInvocation invocation, ToolResult result, CancellationToken token) => after(invocation, result, token);
    }
    private sealed class Sink(Func<AgentEvent, CancellationToken, ValueTask> callback) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) => callback(observation, token); }
    private sealed class Source(AssistantMessage message) : IChatTransport
    {
        public int Requests;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            Requests++; yield return new StreamStarted(message with { Content = [], StopReason = StopReason.Pending });
            for (var index = 0; index < message.Content.Length; index++)
            { var call = (ToolCallContent)message.Content[index]; yield return new ToolCallStarted(index, call with { Arguments = JsonData.EmptyObject }); yield return new ToolCallEnded(index, call); }
            yield return new StreamDone(StopReason.ToolUse, message); await Task.CompletedTask;
        }
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Stage(Task witness, Task running, string stage)
    {
        await Task.WhenAny(witness, running).WaitAsync(Deadline);
        if (!witness.IsCompleted) { await running; throw new InvalidOperationException("Run completed before receipt stage: " + stage); }
        await witness;
    }
    private static async Task<T> Throws<T>(Func<Task> callback) where T : Exception
    { try { await callback().WaitAsync(Deadline); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task Ignore(Task running) { try { await running.WaitAsync(Deadline); } catch { } }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
}
