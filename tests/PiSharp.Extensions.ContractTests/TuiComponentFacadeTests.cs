using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;

// Authored source controls. Root owns runner registration and all execution.
internal static class TuiComponentFacadeTests
{
    internal const string Prefix = "tui-terminal-input.";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "ordered-transform-consume-and-empty-replacement", Transform),
        (Prefix + "unsubscribe-idempotence-and-synchronous-handler-identity", Unsubscribe),
        (Prefix + "unsubscribe-skips-not-yet-admitted-callback", UnsubscribeHeld),
        (Prefix + "scope-retirement-joins-held-original-and-suppresses-output", ScopeRetirement),
        (Prefix + "disconnect-joins-original-nested-duplicate-faults", DisconnectFault),
        (Prefix + "faulted-oce-preserves-original-task-and-fault-status", FaultedOce),
        (Prefix + "synchronous-oce-remains-fault-own-close-rejected", SyncOce),
        (Prefix + "genuine-cancellation-is-no-input-and-successful-retirement", GenuineCancellation),
        (Prefix + "generation-stamp-cancellation-and-context-identity", Generation),
        (Prefix + "cancellation-callback-fault-preserved-through-owner-close", CancellationFault),
        (Prefix + "bounded-admission-and-retired-scope-rejection", Bounds),
        (Prefix + "async-own-scope-close-rejected-before-mutation", () => OwnClose(false)),
        (Prefix + "async-own-hub-close-rejected-before-mutation", () => OwnClose(true)),
        (Prefix + "nested-callback-ancestor-close-rejected-unrelated-scope-allowed", NestedClose),
        (Prefix + "inactive-inherited-frame-allows-later-close", InactiveFrame),
        (Prefix + "captured-cancellation-handler-close-reentry-rejected", () => CleanupClose(false)),
        (Prefix + "unsafe-cancellation-handler-close-reentry-rejected", () => CleanupClose(true)),
        (Prefix + "captured-cancellation-context-retains-cleanup-ancestor", CleanupAncestor),
        (Prefix + "uncaught-own-close-reentry-retains-original-fault", ReentryFault),
        (Prefix + "uncaught-cleanup-reentry-retained-through-external-close", CleanupReentryFault),
        (Prefix + "frame-free-register-cleanup-close-rejected-unrelated-scope-allowed", () => FrameFreeCleanup(false)),
        (Prefix + "frame-free-register-nested-cleanup-retains-synchronous-ancestor", () => FrameFreeCleanup(true)),
        (Prefix + "frame-free-register-uncaught-close-fault-retained", FrameFreeCleanupFault)
    ];
    private static void Check(bool condition, string message)
    { if (!condition) throw new IOException(message); }
    private static TaskCompletionSource<T> Gate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Observe(Task task) { try { await task; } catch (Exception) { } }
    private static IEnumerable<Exception> Walk(Exception error)
    {
        yield return error;
        if (error is AggregateException aggregate)
            foreach (var inner in aggregate.InnerExceptions) foreach (var item in Walk(inner)) yield return item;
        else if (error.InnerException is { } inner)
            foreach (var item in Walk(inner)) yield return item;
    }
    private static void Contains(Task task, Exception error)
    { Check(task.IsFaulted && Walk(task.Exception!).Any(item => ReferenceEquals(item, error)), "Original fault missing."); }
    private sealed class Context : IExtensionContext
    {
        public string OwnerId => "component-controls";
        public long OwnerGeneration => 7;
        public CancellationToken OperationCancellationToken { get; init; }
        public CancellationToken SessionCancellationToken { get; init; }
        public CancellationToken ExtensionLifetimeCancellationToken { get; init; }
    }
    private static async Task Transform()
    {
        await using var hub = new ExtensionTerminalInputHub(1, 2);
        await using var scope = hub.OpenScope(new Context());
        var seen = new List<string>();
        using var a = scope.OnTerminalInput(data => { seen.Add(data); return new(Data: ""); });
        using var b = scope.OnTerminalInput(data => { seen.Add(data); return new(Data: "replacement"); });
        using var c = scope.OnTerminalInput(data => { seen.Add(data); return null; });
        var outcome = await hub.DispatchAsync("raw", 1, 2);
        Check(outcome.Data == "replacement" && seen.SequenceEqual(new[] { "raw", "", "replacement" }), "Transform order/default changed.");
        b.Dispose(); c.Dispose();
        Check((await hub.DispatchAsync("raw", 1, 2)).Disposition == ExtensionTerminalInputDisposition.Consumed, "Empty final string escaped.");
        using var consume = scope.OnTerminalInput(_ => new(Consume: true, Data: "must not forward"));
        using var forbidden = scope.OnTerminalInput(_ => throw new IOException("Consume must stop."));
        Check((await hub.DispatchAsync("raw", 1, 2)).Disposition == ExtensionTerminalInputDisposition.Consumed, "Consume failed.");
    }
    private static async Task Unsubscribe()
    {
        await using var hub = new ExtensionTerminalInputHub(1, 2);
        await using var scope = hub.OpenScope(new Context());
        var calls = 0;
        ExtensionTerminalInputHandler handler = _ => { calls++; return null; };
        var first = scope.OnTerminalInput(handler); var duplicate = scope.OnTerminalInput(handler);
        await hub.DispatchAsync("raw", 1, 2);
        Check(calls == 1 && ReferenceEquals(first, duplicate), "Synchronous handler identity lost.");
        duplicate.Dispose(); first.Dispose();
        Check((await hub.DispatchAsync("raw", 1, 2)).Data == "raw" && calls == 1, "Unsubscribe did not remove handler.");
        using var again = scope.OnTerminalInput(handler);
        first.Dispose(); using var duplicateAgain = scope.OnTerminalInput(handler);
        await hub.DispatchAsync("raw", 1, 2); Check(calls == 2, "Resubscribe failed.");
        again.Dispose(); duplicateAgain.Dispose();
        IDisposable? self = null;
        self = scope.OnTerminalInput(_ => { calls++; self!.Dispose(); return null; });
        await hub.DispatchAsync("raw", 1, 2); await hub.DispatchAsync("raw", 1, 2);
        Check(calls == 3, "Synchronous own unsubscribe was rejected or did not remove handler.");
    }
    private static async Task UnsubscribeHeld()
    {
        await using var hub = new ExtensionTerminalInputHub(1, 2);
        await using var scope = hub.OpenScope(new Context());
        var original = Gate<ExtensionTerminalInputResult?>();
        using var first = scope.OnTerminalInputAsync((_, _, _) => original.Task);
        var calls = 0; var second = scope.OnTerminalInput(_ => { calls++; return null; });
        var dispatch = hub.DispatchAsync("raw", 1, 2);
        second.Dispose(); original.SetResult(new(Data: "changed"));
        Check((await dispatch).Data == "changed" && calls == 0, "Removed callback admitted after held input.");
    }
    private static async Task ScopeRetirement()
    {
        await using var hub = new ExtensionTerminalInputHub(1, 2);
        var context = new Context(); var scope = hub.OpenScope(context);
        var original = Gate<ExtensionTerminalInputResult?>(); CancellationToken captured = default;
        scope.OnTerminalInputAsync((_, actual, token) => { Check(ReferenceEquals(context, actual), "Context replaced."); captured = token; return original.Task; });
        var dispatch = hub.DispatchAsync("raw", 1, 2); var close = scope.DisposeAsync().AsTask();
        Check(!close.IsCompleted && !dispatch.IsCompleted && captured.IsCancellationRequested, "Owner close detached original.");
        original.SetResult(new(Data: "stale output"));
        Check((await dispatch).UnavailableReason == ExtensionUiUnavailableReason.StaleContext, "Retired callback output forwarded.");
        await close; Check(ReferenceEquals(close, scope.DisposeAsync().AsTask()), "Close original not retained.");
        Check((await hub.DispatchAsync("fresh", 1, 2)).Data == "fresh", "Retired scope blocked unrelated input.");
    }
    private static async Task DisconnectFault()
    {
        var hub = new ExtensionTerminalInputHub(1, 2); var scope = hub.OpenScope(new Context());
        var original = Gate<ExtensionTerminalInputResult?>();
        var leaf = new IOException("shared leaf"); var nested = new AggregateException("nested", leaf, leaf);
        scope.OnTerminalInputAsync((_, _, _) => original.Task);
        var dispatch = hub.DispatchAsync("raw", 1, 2); var close = hub.DisposeAsync().AsTask();
        Check(!close.IsCompleted, "Disconnect detached original.");
        original.SetException(new Exception[] { nested, leaf, nested });
        await Observe(dispatch); await Observe(close);
        Check(ReferenceEquals(scope.FaultedCallbacks.Single(), original.Task), "Callback task replaced.");
        foreach (var task in new Task[] { dispatch, close })
        {
            var faults = task.Exception!.InnerExceptions;
            Check(faults.Count == 3 && ReferenceEquals(faults[0], nested) &&
                ReferenceEquals(faults[1], leaf) && ReferenceEquals(faults[2], nested), "Fault DAG/multiplicity changed.");
        }
        var ownerClose = scope.DisposeAsync().AsTask(); await Observe(ownerClose); Contains(ownerClose, leaf);
        Check(ReferenceEquals(close, hub.DisposeAsync().AsTask()), "Disconnect task replaced.");
        Check((await hub.DispatchAsync("raw", 1, 2)).UnavailableReason == ExtensionUiUnavailableReason.StaleContext, "Disconnected hub admitted input.");
    }
    private static async Task FaultedOce()
    {
        var hub = new ExtensionTerminalInputHub(1, 2); var scope = hub.OpenScope(new Context());
        var error = new OperationCanceledException("faulted original");
        var original = Task.FromException<ExtensionTerminalInputResult?>(error);
        scope.OnTerminalInputAsync((_, _, _) => original);
        var dispatch = hub.DispatchAsync("raw", 1, 2); await Observe(dispatch); Contains(dispatch, error);
        var close = scope.DisposeAsync().AsTask(); await Observe(close); Contains(close, error);
        Check(ReferenceEquals(scope.FaultedCallbacks.Single(), original) && !dispatch.IsCanceled && !close.IsCanceled, "Faulted OCE relabeled.");
        await Observe(hub.DisposeAsync().AsTask());
    }
    private static async Task SyncOce()
    {
        var hub = new ExtensionTerminalInputHub(1, 2); var scope = hub.OpenScope(new Context());
        var error = new OperationCanceledException("sync throw"); var rejected = false;
        scope.OnTerminalInput(data =>
        {
            try { _ = scope.DisposeAsync(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Synchronous own close was not rejected.");
            throw error;
        });
        var dispatch = hub.DispatchAsync("raw", 1, 2); await Observe(dispatch); Contains(dispatch, error);
        using var stillOpen = scope.OnTerminalInput(_ => null);
        var close = scope.DisposeAsync().AsTask(); await Observe(close); Contains(close, error);
        Check(ReferenceEquals(close, scope.DisposeAsync().AsTask()), "External close task replaced.");
        await Observe(hub.DisposeAsync().AsTask());
    }
    private static async Task GenuineCancellation()
    {
        await using var hub = new ExtensionTerminalInputHub(1, 2);
        await using var scope = hub.OpenScope(new Context());
        var original = Task.FromCanceled<ExtensionTerminalInputResult?>(new CancellationToken(true));
        scope.OnTerminalInputAsync((_, _, _) => original);
        Check((await hub.DispatchAsync("raw", 1, 2)).Disposition == ExtensionTerminalInputDisposition.Cancelled &&
            scope.FaultedCallbacks.IsEmpty, "Genuine cancellation became fault/input.");
    }
    private static async Task Generation()
    {
        await using var hub = new ExtensionTerminalInputHub(11, 22);
        using var lifetime = new CancellationTokenSource();
        var context = new Context { ExtensionLifetimeCancellationToken = lifetime.Token };
        await using var scope = hub.OpenScope(context); var calls = 0;
        scope.OnTerminalInputAsync((_, actual, token) => { Check(ReferenceEquals(actual, context), "Original context lost."); calls++; return Task.FromResult<ExtensionTerminalInputResult?>(null); });
        Check((await hub.DispatchAsync("raw", 12, 22)).UnavailableReason == ExtensionUiUnavailableReason.StaleContext &&
            (await hub.DispatchAsync("raw", 11, 23)).UnavailableReason == ExtensionUiUnavailableReason.StaleContext && calls == 0, "Generation admission bypassed.");
        Check((await hub.DispatchAsync("raw", 11, 22, new CancellationToken(true))).Disposition == ExtensionTerminalInputDisposition.Cancelled && calls == 0, "Canceled input admitted.");
        await hub.DispatchAsync("raw", 11, 22); Check(calls == 1, "Valid input not admitted.");
        lifetime.Cancel(); await hub.DispatchAsync("raw", 11, 22); Check(calls == 1, "Canceled owner called.");
    }
    private static async Task CancellationFault()
    {
        var hub = new ExtensionTerminalInputHub(1, 2); var scope = hub.OpenScope(new Context());
        var original = Gate<ExtensionTerminalInputResult?>(); var error = new IOException("cancellation callback");
        CancellationTokenRegistration registration = default;
        scope.OnTerminalInputAsync((_, _, token) => { registration = token.Register(() => throw error); return original.Task; });
        var dispatch = hub.DispatchAsync("raw", 1, 2); var close = scope.DisposeAsync().AsTask();
        Check(!close.IsCompleted, "Cancellation fault detached input.");
        original.SetResult(null); Check((await dispatch).UnavailableReason == ExtensionUiUnavailableReason.StaleContext, "Cleanup fault allowed retired input.");
        await Observe(close); Contains(close, error); registration.Dispose();
        await Observe(hub.DisposeAsync().AsTask());
    }
    private static async Task Bounds()
    {
        await using var hub = new ExtensionTerminalInputHub(1, 2);
        var scope = hub.OpenScope(new Context()); var handles = new List<IDisposable>();
        for (var index = 0; index < 256; index++) handles.Add(scope.OnTerminalInputAsync((_, _, _) => Task.FromResult<ExtensionTerminalInputResult?>(null)));
        try { scope.OnTerminalInputAsync((_, _, _) => Task.FromResult<ExtensionTerminalInputResult?>(null)); throw new IOException("Unbounded subscription admission."); }
        catch (InvalidOperationException) { }
        foreach (var handle in handles) handle.Dispose();
        var original = Gate<ExtensionTerminalInputResult?>();
        scope.OnTerminalInputAsync((_, _, _) => original.Task);
        var dispatches = Enumerable.Range(0, 256).Select(_ => hub.DispatchAsync("raw", 1, 2)).ToArray();
        Check((await hub.DispatchAsync("overflow", 1, 2)).UnavailableReason == ExtensionUiUnavailableReason.ResourceLimit,
            "Pending dispatch bound bypassed.");
        var close = scope.DisposeAsync().AsTask(); Check(!close.IsCompleted, "Bounded close detached admitted inputs.");
        original.SetResult(null);
        Check((await Task.WhenAll(dispatches)).All(result => result.UnavailableReason == ExtensionUiUnavailableReason.StaleContext),
            "Bounded retirement released stale input.");
        await close;
        try { scope.OnTerminalInput(_ => null); throw new IOException("Retired scope admitted subscription."); }
        catch (ObjectDisposedException) { }
    }
    private static async Task MustReject(Func<ValueTask> close)
    {
        try { await close(); }
        catch (InvalidOperationException) { return; }
        throw new IOException("Own or ancestor lifecycle close was allowed.");
    }
    private static async Task OwnClose(bool wholeHub)
    {
        await using var hub = new ExtensionTerminalInputHub(1, 2);
        await using var scope = hub.OpenScope(new Context());
        var calls = 0;
        scope.OnTerminalInputAsync(async (_, _, _) =>
        {
            await Task.Yield();
            if (wholeHub) await MustReject(hub.DisposeAsync); else await MustReject(scope.DisposeAsync);
            using var extra = scope.OnTerminalInput(_ => null);
            calls++; return new(Data: "live");
        });
        Check((await hub.DispatchAsync("raw", 1, 2)).Data == "live" && calls == 1, "Rejected close changed admission/output.");
        Check((await hub.DispatchAsync("raw", 1, 2)).Data == "live" && calls == 2, "Rejected close retired owner/hub.");
    }
    private static async Task NestedClose()
    {
        await using var ancestor = new ExtensionTerminalInputHub(1, 2);
        await using var child = new ExtensionTerminalInputHub(3, 4);
        await using var owner = ancestor.OpenScope(new Context());
        await using var nested = child.OpenScope(new Context());
        var unrelated = ancestor.OpenScope(new Context());
        nested.OnTerminalInputAsync(async (_, _, _) =>
        {
            await Task.Yield();
            await MustReject(owner.DisposeAsync); await MustReject(ancestor.DisposeAsync);
            await MustReject(nested.DisposeAsync); await MustReject(child.DisposeAsync);
            await unrelated.DisposeAsync();
            return new(Data: "nested live");
        });
        owner.OnTerminalInputAsync(async (_, _, _) =>
        {
            var result = await child.DispatchAsync("child raw", 3, 4);
            return new(Data: result.Data);
        });
        Check((await ancestor.DispatchAsync("ancestor raw", 1, 2)).Data == "nested live", "Nested rejection mutated ancestor state.");
        using var liveOwner = owner.OnTerminalInput(_ => null);
        try { unrelated.OnTerminalInput(_ => null); throw new IOException("Unrelated scope close did not occur."); }
        catch (ObjectDisposedException) { }
        Check(ReferenceEquals(unrelated.DisposeAsync().AsTask(), unrelated.DisposeAsync().AsTask()), "Unrelated close task replaced.");
    }
    private static async Task InactiveFrame()
    {
        await using var hub = new ExtensionTerminalInputHub(1, 2);
        var scope = hub.OpenScope(new Context()); var release = Gate<bool>(); Task? later = null;
        async Task LaterClose() { await release.Task; await scope.DisposeAsync(); }
        scope.OnTerminalInputAsync((_, _, _) =>
        {
            later = LaterClose();
            return Task.FromResult<ExtensionTerminalInputResult?>(null);
        });
        Check((await hub.DispatchAsync("raw", 1, 2)).Data == "raw", "Initial input failed.");
        release.SetResult(true); await later!;
        Check(ReferenceEquals(scope.DisposeAsync().AsTask(), scope.DisposeAsync().AsTask()), "Inactive frame blocked stable later close.");
    }
    private static async Task CleanupClose(bool unsafeRegistration)
    {
        var hub = new ExtensionTerminalInputHub(1, 2); var scope = hub.OpenScope(new Context());
        var original = Gate<ExtensionTerminalInputResult?>(); var rejections = 0;
        void RejectSynchronously(Func<ValueTask> close)
        {
            try { close().AsTask().GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { rejections++; return; }
            throw new IOException("Cancellation handler joined an in-progress lifecycle close.");
        }
        CancellationTokenRegistration registration = default;
        void Handler() { RejectSynchronously(scope.DisposeAsync); RejectSynchronously(hub.DisposeAsync); }
        scope.OnTerminalInputAsync((_, _, token) =>
        {
            registration = unsafeRegistration ? token.UnsafeRegister(_ => Handler(), null) : token.Register(Handler);
            return original.Task;
        });
        var dispatch = hub.DispatchAsync("raw", 1, 2); var close = scope.DisposeAsync().AsTask();
        Check(rejections == 2 && !close.IsCompleted && !dispatch.IsCompleted, "Cleanup guard failed or external close detached original.");
        original.SetResult(new(Data: "retired"));
        Check((await dispatch).UnavailableReason == ExtensionUiUnavailableReason.StaleContext, "Cleanup allowed stale output.");
        await close; registration.Dispose(); await hub.DisposeAsync();
        Check(ReferenceEquals(close, scope.DisposeAsync().AsTask()), "Cleanup changed external close task.");
    }
    private static async Task CleanupAncestor()
    {
        await using var ancestor = new ExtensionTerminalInputHub(1, 2);
        await using var child = new ExtensionTerminalInputHub(3, 4);
        await using var owner = ancestor.OpenScope(new Context());
        var cleanupOwner = child.OpenScope(new Context());
        var original = Gate<ExtensionTerminalInputResult?>(); var rejections = 0;
        CancellationTokenRegistration registration = default;
        cleanupOwner.OnTerminalInputAsync((_, _, token) =>
        {
            // This captured context predates the ancestor's callback. Follow the
            // cleanup-owner link to retain the later active cleanup ancestry.
            registration = token.Register(() =>
            {
                foreach (var close in new Func<ValueTask>[] { owner.DisposeAsync, ancestor.DisposeAsync, cleanupOwner.DisposeAsync, child.DisposeAsync })
                {
                    try { close().AsTask().GetAwaiter().GetResult(); }
                    catch (InvalidOperationException) { rejections++; }
                }
                original.TrySetResult(null);
            });
            return original.Task;
        });
        var heldChild = child.DispatchAsync("held", 3, 4);
        owner.OnTerminalInputAsync(async (_, _, _) =>
        {
            await Task.Yield(); await cleanupOwner.DisposeAsync();
            return new(Data: "ancestor live");
        });
        Check((await ancestor.DispatchAsync("raw", 1, 2)).Data == "ancestor live" && rejections == 4,
            "Restored cancellation context hid active ancestor cleanup.");
        Check((await heldChild).UnavailableReason == ExtensionUiUnavailableReason.StaleContext, "Child retired output escaped.");
        registration.Dispose(); using var stillLive = owner.OnTerminalInput(_ => null);
    }
    private static async Task ReentryFault()
    {
        var hub = new ExtensionTerminalInputHub(1, 2); var scope = hub.OpenScope(new Context());
        Task<ExtensionTerminalInputResult?>? original = null;
        async Task<ExtensionTerminalInputResult?> RejectOwnClose()
        { await Task.Yield(); await scope.DisposeAsync(); return null; }
        scope.OnTerminalInputAsync((_, _, _) => original = RejectOwnClose());
        var dispatch = hub.DispatchAsync("raw", 1, 2); await Observe(dispatch);
        var fault = original!.Exception!.InnerExceptions.Single();
        Check(fault is InvalidOperationException && ReferenceEquals(scope.FaultedCallbacks.Single(), original), "Reentry fault replaced original task.");
        Contains(dispatch, fault); using var live = scope.OnTerminalInput(_ => null);
        var close = scope.DisposeAsync().AsTask(); await Observe(close); Contains(close, fault);
        Check(ReferenceEquals(close, scope.DisposeAsync().AsTask()), "Reentry fault changed external close original.");
        await Observe(hub.DisposeAsync().AsTask());
    }
    private static async Task CleanupReentryFault()
    {
        var hub = new ExtensionTerminalInputHub(1, 2); var scope = hub.OpenScope(new Context());
        var original = Gate<ExtensionTerminalInputResult?>(); Exception? rejection = null;
        CancellationTokenRegistration registration = default;
        scope.OnTerminalInputAsync((_, _, token) =>
        {
            registration = token.Register(() =>
            {
                try { scope.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch (InvalidOperationException error) { rejection = error; throw; }
            });
            return original.Task;
        });
        var dispatch = hub.DispatchAsync("raw", 1, 2); var close = scope.DisposeAsync().AsTask();
        Check(rejection is not null && !close.IsCompleted, "Uncaught cleanup reentry detached input or escaped guard.");
        original.SetResult(null); await dispatch; await Observe(close); Contains(close, rejection!);
        Check(scope.FaultedCallbacks.IsEmpty && ReferenceEquals(close, scope.DisposeAsync().AsTask()), "Cleanup failure relabeled original callback or close task.");
        registration.Dispose(); await Observe(hub.DisposeAsync().AsTask());
    }
    private static async Task FrameFreeCleanup(bool nestedCleanup)
    {
        var hub = new ExtensionTerminalInputHub(1, 2); var scope = hub.OpenScope(new Context());
        var unrelated = hub.OpenScope(new Context());
        var nestedHub = new ExtensionTerminalInputHub(3, 4); var nestedScope = nestedHub.OpenScope(new Context());
        var original = Gate<ExtensionTerminalInputResult?>(); var ready = Gate<bool>();
        var nestedOriginal = Gate<ExtensionTerminalInputResult?>(); var nestedReady = Gate<bool>();
        var ownRejections = 0; var nestedRejections = 0;
        CancellationTokenRegistration registration = default, nestedRegistration = default;
        void RejectSynchronously(Func<ValueTask> close, bool nested)
        {
            try { close().AsTask().GetAwaiter().GetResult(); }
            catch (InvalidOperationException)
            { if (nested) nestedRejections++; else ownRejections++; return; }
            throw new IOException("Frame-free Register restored an unguarded cleanup context.");
        }
        async Task<CancellationTokenRegistration> RegisterFrameFree(CancellationToken token, Action handler)
        {
            Task<CancellationTokenRegistration> joined;
            using (ExecutionContext.SuppressFlow())
                joined = Task.Run(() => token.Register(handler));
            // Suppression ends synchronously before awaiting the owned worker.
            return await joined;
        }
        Action nestedHandler = () =>
        {
            RejectSynchronously(scope.DisposeAsync, true); RejectSynchronously(hub.DisposeAsync, true);
            RejectSynchronously(nestedScope.DisposeAsync, true); RejectSynchronously(nestedHub.DisposeAsync, true);
            nestedOriginal.TrySetResult(null);
        };
        nestedScope.OnTerminalInputAsync(async (_, _, token) =>
        {
            nestedRegistration = await RegisterFrameFree(token, nestedHandler); nestedReady.TrySetResult(true);
            return await nestedOriginal.Task;
        });
        Task<ExtensionTerminalInputOutcome>? nestedDispatch = null;
        if (nestedCleanup)
        { nestedDispatch = nestedHub.DispatchAsync("nested held", 3, 4); await nestedReady.Task; }
        scope.OnTerminalInputAsync(async (_, _, token) =>
        {
            Action handler = () =>
            {
                RejectSynchronously(scope.DisposeAsync, false); RejectSynchronously(hub.DisposeAsync, false);
                unrelated.DisposeAsync().AsTask().GetAwaiter().GetResult();
                if (nestedCleanup) nestedScope.DisposeAsync().AsTask().GetAwaiter().GetResult();
            };
            registration = await RegisterFrameFree(token, handler); ready.TrySetResult(true);
            return await original.Task;
        });
        var dispatch = hub.DispatchAsync("held", 1, 2); await ready.Task;
        var close = scope.DisposeAsync().AsTask();
        Check(ownRejections == 2 && (!nestedCleanup || nestedRejections == 4) && !close.IsCompleted && !dispatch.IsCompleted,
            "Synchronous cleanup seed failed or external close detached input.");
        original.SetResult(null);
        Check((await dispatch).UnavailableReason == ExtensionUiUnavailableReason.StaleContext, "Frame-free cleanup allowed stale output.");
        await close;
        if (nestedDispatch is not null)
            Check((await nestedDispatch).UnavailableReason == ExtensionUiUnavailableReason.StaleContext, "Nested cleanup allowed stale output.");
        try { unrelated.OnTerminalInput(_ => null); throw new IOException("Unrelated scope close was blocked."); }
        catch (ObjectDisposedException) { }
        Check(ReferenceEquals(close, scope.DisposeAsync().AsTask()), "Frame-free cleanup changed stable close task.");
        registration.Dispose(); nestedRegistration.Dispose();
        await hub.DisposeAsync(); await nestedScope.DisposeAsync(); await nestedHub.DisposeAsync();
    }
    private static async Task FrameFreeCleanupFault()
    {
        var hub = new ExtensionTerminalInputHub(1, 2); var scope = hub.OpenScope(new Context());
        var original = Gate<ExtensionTerminalInputResult?>(); var ready = Gate<bool>();
        Exception? rejection = null; CancellationTokenRegistration registration = default;
        scope.OnTerminalInputAsync(async (_, _, token) =>
        {
            Task joined;
            using (ExecutionContext.SuppressFlow())
                joined = Task.Run(() => registration = token.Register(() =>
                {
                    try { scope.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                    catch (InvalidOperationException error) { rejection = error; throw; }
                }));
            await joined; ready.TrySetResult(true);
            return await original.Task;
        });
        var dispatch = hub.DispatchAsync("held", 1, 2); await ready.Task;
        var close = scope.DisposeAsync().AsTask();
        Check(rejection is not null && !close.IsCompleted, "Frame-free cleanup fault missed guard or detached callback.");
        original.SetResult(null);
        Check((await dispatch).UnavailableReason == ExtensionUiUnavailableReason.StaleContext, "Frame-free cleanup fault released stale output.");
        await Observe(close); Contains(close, rejection!);
        Check(scope.FaultedCallbacks.IsEmpty && ReferenceEquals(close, scope.DisposeAsync().AsTask()), "Frame-free cleanup fault changed callback status or close task.");
        registration.Dispose(); var hubClose = hub.DisposeAsync().AsTask(); await Observe(hubClose); Contains(hubClose, rejection!);
    }
}
