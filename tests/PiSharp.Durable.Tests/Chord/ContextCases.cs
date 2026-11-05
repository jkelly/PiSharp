using PiSharp.Durable.Chord;
using PiSharp.Telemetry;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/context.test.ts. Adaptations: an AbortController whose abort
// carries a reason is DurableContext.WithCancel() (a CancellationTokenSource carries no reason), so the parent controller of
// "inherits parent cancellation" is Background.WithCancel(), equivalent to withAbortSignal(controller.signal, BACKGROUND_CONTEXT); an
// abort-event listener is a token registration; `value(key)` returning undefined is TryGet returning false. The last case is native:
// it pins the CancellationToken interop that has no upstream counterpart.
// Pi v1.5.0 sync (area telemetry, 48909d987, 2ec55d808, 92ab53602): Context.telemetry on the roots, and the two telemetry cases of
// f9a26365's context.test.ts. withTelemetry and traceIn are DurableContext's WithTelemetry and TraceInAsync; withContextValue is With;
// abortSignal identity is CancellationToken equality. JavaScript throws any value; .NET throws exceptions, so the failure that must
// come back "the same value" is one exception instance, thrown and rejected. These three commits are every change to the file since
// v1.2.0 (92ab53602 is also area chord-watch's, which leaves this file to this area), so the citation is on v1.5.0.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> ChordContextCases() =>
    [
        Case("chord.context.provides-distinct-empty-root-contexts", () =>
        {
            var key = new ContextKey<string>("value");
            Check(!ReferenceEquals(DurableContext.Todo, DurableContext.Background), "TODO and BACKGROUND are distinct");
            Check(!DurableContext.Todo.HasCancellation, "TODO has no abort signal");
            Check(ReferenceEquals(NoopTelemetry.Context, DurableContext.Todo.Telemetry), "TODO telemetry is the no-op span");
            Check(ReferenceEquals(NoopTelemetry.Context, DurableContext.Background.Telemetry), "BACKGROUND telemetry is the no-op span");
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
        Case("chord.context.derives-telemetry-independently-from-values-and-cancellation", async () =>
        {
            var telemetry = new InMemoryTelemetryContext();
            var other = new InMemoryTelemetryContext();
            await telemetry.StartSpanAsync("caller", parent => other.StartSpanAsync("other", async otherSpan =>
            {
                var key = new ContextKey<string>("principal");
                var controller = DurableContext.Background.WithCancel();
                var @base = controller.Context.With(key, "owner");
                var context = @base.WithTelemetry(parent);
                var replaced = context.WithTelemetry(otherSpan);
                Check(ReferenceEquals(NoopTelemetry.Context, @base.Telemetry), "base telemetry");
                Check(ReferenceEquals(parent, context.Telemetry), "context telemetry");
                Check(ReferenceEquals(otherSpan, replaced.Telemetry), "replaced telemetry");
                Equal("owner", replaced.Get(key), "replaced value");
                Equal(@base.CancellationToken, replaced.CancellationToken, "replaced abort signal");
                Check(ReferenceEquals(parent, context.WithoutCancellation().Telemetry), "withoutAbortSignal keeps telemetry");
                await context.TraceInAsync("outer", async child =>
                {
                    Equal("owner", child.Get(key), "child value");
                    Equal(context.CancellationToken, child.CancellationToken, "child abort signal");
                    child.Telemetry.AddEvent("started");
                    await child.TraceInAsync("inner", nested =>
                    {
                        nested.Telemetry.AddLink(new SpanLink(child.Telemetry));
                        return 42;
                    });
                    return 0;
                });
                var spans = telemetry.GetSpans();
                var (caller, outer, inner) = (spans[0], spans[1], spans[2]);
                Equal<int?>(caller.Id, outer.ParentId, "outer parent");
                Equal<int?>(outer.Id, inner.ParentId, "inner parent");
                Check(inner.Links[0].Context.SequenceEqual(outer.Context), "inner links to outer");
                Check(ReferenceEquals(parent, context.Telemetry), "context telemetry after traceIn");
                controller.Cancel();
                Check(context.IsCancellationRequested, "context aborted");
            }));
        }),
        Case("chord.context.isolates-concurrent-tracein-branches-and-preserves-callback-results-and-errors", async () =>
        {
            var telemetry = new InMemoryTelemetryContext();
            await telemetry.StartSpanAsync("caller", async parent =>
            {
                var context = DurableContext.Background.WithTelemetry(parent);
                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var first = context.TraceInAsync("first", async child =>
                {
                    await gate.Task;
                    return await child.TraceInAsync("first.child", _ => "first");
                });
                var second = context.TraceInAsync("second", async child =>
                {
                    var result = await child.TraceInAsync("second.child", _ => "second");
                    gate.TrySetResult();
                    return result;
                });
                Check((await Task.WhenAll(first, second)).SequenceEqual(["first", "second"]), "results");
                var spans = telemetry.GetSpans();
                foreach (var name in new[] { "first", "second" })
                {
                    var branch = spans.First(span => span.Name == name);
                    Equal<int?>(branch.Id, spans.First(span => span.Name == $"{name}.child").ParentId, $"{name}.child parent");
                }
                var failure = new InvalidOperationException("same value");
                Check(ReferenceEquals(failure, await ThrowsAsync<InvalidOperationException>(
                    () => context.TraceInAsync("throw", new Func<DurableContext, int>(_ => throw failure)), "throw")), "a throw rejects with the same value");
                Check(ReferenceEquals(failure, await ThrowsAsync<InvalidOperationException>(
                    () => context.TraceInAsync("reject", _ => Task.FromException<int>(failure)), "reject")), "a rejection rejects with the same value");
                Equal(42, await DurableContext.Background.TraceInAsync("noop", child =>
                {
                    child.Telemetry.AddLink(new SpanLink(parent));
                    return 42;
                }), "noop result");
            });
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
