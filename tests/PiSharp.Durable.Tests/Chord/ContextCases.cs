using PiSharp.Durable.Chord;

// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/chord/test/context.test.ts. Adaptations: an AbortController whose abort
// carries a reason is DurableContext.WithCancel() (a CancellationTokenSource carries no reason), so the parent controller of
// "inherits parent cancellation" is Background.WithCancel(), equivalent to withAbortSignal(controller.signal, BACKGROUND_CONTEXT); an
// abort-event listener is a token registration; `value(key)` returning undefined is TryGet returning false. The last case is native:
// it pins the CancellationToken interop that has no upstream counterpart.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> ChordContextCases() =>
    [
        Case("chord.context.provides-distinct-empty-root-contexts", () =>
        {
            var key = new ContextKey<string>("value");
            Check(!ReferenceEquals(DurableContext.Todo, DurableContext.Background), "TODO and BACKGROUND are distinct");
            Check(!DurableContext.Todo.HasCancellation, "TODO has no abort signal");
            Check(!DurableContext.Todo.TryGet(key, out _), "TODO has no values");
            Equal("[Context BACKGROUND_CONTEXT]", DurableContext.Background.ToString(), "BACKGROUND string");
            Equal("[Context TODO_CONTEXT]", DurableContext.Todo.ToString(), "TODO string");
        }),
        Case("chord.context.layers-typed-values-without-modifying-parents", () =>
        {
            var firstKey = new ContextKey<string>("first");
            var secondKey = new ContextKey<int>("second");
            var first = DurableContext.Background.With(firstKey, "one");
            var second = first.With(secondKey, 2);
            var replaced = second.With(firstKey, "updated");

            Check(!DurableContext.Background.TryGet(firstKey, out _), "background unchanged");
            Equal("one", first.Get(firstKey), "first.first");
            Check(!first.TryGet(secondKey, out _), "first has no second");
            Equal("one", second.Get(firstKey), "second.first");
            Equal(2, second.Get(secondKey), "second.second");
            Equal("updated", replaced.Get(firstKey), "replaced.first");
            Equal("one", second.Get(firstKey), "second.first after replace");
            Equal("[Context BACKGROUND_CONTEXT].WithValue(first).WithValue(second).WithValue(first)", replaced.ToString(), "chain string");
        }),
        Case("chord.context.inherits-parent-cancellation-and-isolates-child-cancellation", () =>
        {
            var parentController = DurableContext.Background.WithCancel();
            var parent = parentController.Context;
            var child = parent.WithCancel();
            var sibling = parent.WithCancel();
            var childListener = 0;
            child.Context.CancellationToken.Register(() => childListener++);

            child.Cancel("child");
            Check(child.Context.IsCancellationRequested, "child aborted");
            Equal<object?>("child", child.Context.CancellationReason, "child reason");
            Check(!sibling.Context.IsCancellationRequested, "sibling not aborted");
            Check(!parent.IsCancellationRequested, "parent not aborted");
            Equal(1, childListener, "child listener called once");

            parentController.Cancel("parent");
            Check(sibling.Context.IsCancellationRequested, "sibling aborted by parent");
            Equal<object?>("parent", sibling.Context.CancellationReason, "sibling reason");
        }),
        Case("chord.context.masks-caller-cancellation-for-mandatory-cleanup", () =>
        {
            using var controller = new CancellationTokenSource();
            var key = new ContextKey<string>("value");
            var context = DurableContext.Background.WithCancellation(controller.Token).With(key, "preserved");
            var cleanup = context.WithoutCancellation();

            controller.Cancel();
            Check(context.IsCancellationRequested, "context aborted");
            Check(!cleanup.HasCancellation, "cleanup has no abort signal");
            Equal(CancellationToken.None, cleanup.CancellationToken, "cleanup token");
            Equal("preserved", cleanup.Get(key), "cleanup keeps values");
        }),
        Case("chord.context.stops-waiting-when-the-invocation-is-cancelled", async () =>
        {
            var controller = DurableContext.Background.WithCancel();
            var context = controller.Context;
            var work = new TaskCompletionSource<string>();
            var waiting = context.WaitAsync(work.Task);
            var cancellation = new InvalidOperationException("cancelled");

            controller.Cancel(cancellation);
            var thrown = await ThrowsAsync<InvalidOperationException>(() => waiting, "waiter rejects");
            Check(ReferenceEquals(cancellation, thrown), "rejects with the cancellation reason itself");
            work.SetResult("completed later");
            Equal("completed later", await work.Task, "work completes independently");
            Equal("completed", await DurableContext.Background.WaitAsync(Task.FromResult("completed")), "background passes through");
        }),
        Case("chord.context.cancellation-token-interop", async () =>
        {
            // Native: plain tokens cancel without a reason, so waiters see OperationCanceledException; parent and own tokens combine.
            using var outer = new CancellationTokenSource();
            using var inner = new CancellationTokenSource();
            var parent = DurableContext.Background.WithCancellation(outer.Token);
            var combined = parent.WithCancellation(inner.Token);
            Equal("[Context BACKGROUND_CONTEXT].WithValue(chord.abortSignal).WithValue(chord.abortSignal)", combined.ToString(), "chain string");
            var pending = new TaskCompletionSource();
            var waiting = combined.WaitAsync(pending.Task);
            inner.Cancel();
            Check(combined.IsCancellationRequested && !parent.IsCancellationRequested, "own token cancels only the child");
            Check(combined.CancellationReason is null, "plain tokens carry no reason");
            await ThrowsAsync<OperationCanceledException>(() => waiting, "waiter cancelled");
            Throws<OperationCanceledException>(combined.ThrowIfCancellationRequested, "throw if cancelled");
            parent.ThrowIfCancellationRequested();

            var second = parent.WithCancellation(CancellationToken.None);
            outer.Cancel();
            Check(second.IsCancellationRequested, "parent token cancels the child");
            await ThrowsAsync<OperationCanceledException>(() => second.WaitAsync(Task.Delay(Timeout.Infinite)), "already cancelled");

            var cancelled = DurableContext.Background.WithCancel();
            var failure = new TimeoutException("reason");
            cancelled.Cancel(failure);
            cancelled.Cancel(new InvalidOperationException("ignored"));
            Check(ReferenceEquals(failure, Throws<TimeoutException>(cancelled.Context.ThrowIfCancellationRequested, "throws the reason")), "first cancel wins");
            Check(ReferenceEquals(failure, cancelled.Context.WithCancel().Context.CancellationReason), "already-cancelled parent passes its reason");
            Equal(42, await DurableContext.Background.WithCancel().Context.WaitAsync(Task.FromResult(42)), "completed work passes through");
        }),
    ];
}
