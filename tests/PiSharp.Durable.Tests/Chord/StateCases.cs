using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/chord/test/replicated-state.test.ts, replicated-state-delivery.test.ts,
// state-value.test.ts, state-draft.test.ts, state-diff.test.ts and state-fuzz.test.ts (v1.2.0 moved the transactional replicated state
// cases out of state.test.ts, which now tests the excluded host runtime's cells, and dropped the exact source listeners and the service
// replica they covered). Adaptations, each repeated where it applies:
// - Drafts are the explicit draft API (decision 0005, as in TrackerCases.cs): property assignment is Set, delete is Delete, a nested
//   draft is GetObject/GetArray, push/unshift/pop/shift are Add/Insert/RemoveAt, fill/copyWithin are expressed with Set, and JS
//   TypeError/Error are InvalidOperationException with upstream's message words.
// - Promise deferreds are TaskCompletionSource barriers; subscription continuations run synchronously when a task completes, so the
//   state upstream reaches after `await` / vi.waitFor is asserted right after SetResult/SetException, with no sleeps.
// - Object.isFrozen checks are dropped: JsonValue trees are immutable, never frozen copies. toBe is ReferenceEquals.
// - The default error reporter (reportErrorAsync, whose queueMicrotask upstream mocks to capture reports) is the
//   ReplicatedState.UnhandledError event, raised synchronously.
internal static partial class Program
{
    private static readonly DurableContext Bg = DurableContext.Background;

    private static JsonValue V(int value) => JsonValue.Object(KeyValuePair.Create("value", (JsonValue)JsonValue.Number(value)));
    private static int ValueOf(JsonValue value) => (int)((JsonNumberValue)At(value, "value")).Value;
    private static IReadOnlyList<Op> SetValue(int value) => [new SetOp(DeltaPath.Of("value"), JsonValue.Number(value))];
    // The update sequences a synchronous subscriber sees: every publication, in order (it never has pending deliveries to overflow).
    private static List<long> Updates(IReplicatedState<JsonValue> state)
    {
        var updates = new List<long>();
        state.Subscribe((_, _, delivery) =>
        {
            if (delivery.Kind == ReplicatedStateDeliveryKind.Update) lock (updates) updates.Add(delivery.Sequence);
        });
        return updates;
    }

    private static void WithUnhandledReports(Action<List<Exception>> body)
    {
        var reports = new List<Exception>();
        Action<Exception> handler = error => { lock (reports) reports.Add(error); };
        ReplicatedState.UnhandledError += handler;
        try { body(reports); }
        finally { ReplicatedState.UnhandledError -= handler; }
    }
    private static void Seq<T>(IEnumerable<T> expected, IEnumerable<T> actual, string what) =>
        Check(expected.SequenceEqual(actual), $"{what}: expected [{string.Join(", ", expected)}], actual [{string.Join(", ", actual)}]");

    private static IEnumerable<(string, Func<Task>)> ChordStateCases() =>
    [
        Case("chord.state.publishes-one-immutable-structurally-shared-revision", () =>
        {
            var state = ReplicatedState.Create(J("{\"changed\":{\"value\":1},\"retained\":{\"value\":2}}"));
            var previous = state.Value;
            var deliveries = new List<long>();
            state.Subscribe((_, _, delivery) => deliveries.Add(delivery.Sequence));
            state.Change(Bg, draft =>
            {
                draft.AsObject().GetObject("changed").Set("value", 3);
                draft.AsObject().GetObject("changed").Set("value", 4);
            });
            Same("{\"changed\":{\"value\":4},\"retained\":{\"value\":2}}", state.Value, "value");
            Check(!ReferenceEquals(state.Value, previous), "new root");
            Check(!ReferenceEquals(At(state.Value, "changed"), At(previous, "changed")), "changed branch copied");
            Check(ReferenceEquals(At(state.Value, "retained"), At(previous, "retained")), "retained branch shared");
            Seq([0L, 1L], deliveries, "deliveries");
        }),
        Case("chord.state.rolls-back-callback-failures-and-revokes-escaped-drafts", () =>
        {
            var state = ReplicatedState.Create(J("{\"nested\":{\"value\":1}}"));
            var previous = state.Value;
            DraftObject? escaped = null;
            Equal("stop", Throws<InvalidOperationException>(() => state.Change(Bg, draft =>
            {
                escaped = draft.AsObject().GetObject("nested");
                draft.AsObject().GetObject("nested").Set("value", 2);
                throw new InvalidOperationException("stop");
            }), "change throws").Message, "message");
            Check(ReferenceEquals(previous, state.Value), "rolled back");
            Throws<InvalidOperationException>(() => escaped!.Get("value"), "escaped draft revoked");
        }),
        // Adapted: a C# PromiseLike is a returned Task, through the Func<Draft, object?> overload (a non-async lambda returning a task).
        Case("chord.state.rejects-promiselike-callbacks-and-aborts-their-draft", () =>
        {
            var state = ReplicatedState.Create(V(0));
            var previous = state.Value;
            var error = Throws<InvalidOperationException>(() => state.Change(Bg, draft =>
            {
                draft.AsObject().Set("value", 1);
                return Task.CompletedTask;
            }), "promise-like callback");
            Check(error.Message.Contains("synchronous", StringComparison.Ordinal), "message: " + error.Message);
            Check(ReferenceEquals(previous, state.Value), "draft aborted");
        }),
        Case("chord.state.rejects-nested-changes-and-replacements-without-losing-the-outer-rollback", () =>
        {
            var state = ReplicatedState.Create(J("{\"left\":0,\"right\":0}"));
            var nested = Throws<InvalidOperationException>(() => state.Change(Bg, draft =>
            {
                draft.AsObject().Set("left", 1);
                state.Change(Bg, inner => inner.AsObject().Set("right", 2));
            }), "nested change");
            Check(nested.Message.Contains("reentrantly", StringComparison.Ordinal), "message: " + nested.Message);
            Same("{\"left\":0,\"right\":0}", state.Value, "outer rolled back");
            var replaced = Throws<InvalidOperationException>(() => state.Change(Bg, _ =>
            {
                state.Replace(Bg, J("{\"left\":1,\"right\":2}"));
            }), "nested replace");
            Check(replaced.Message.Contains("change callback", StringComparison.Ordinal), "message: " + replaced.Message);
            Same("{\"left\":0,\"right\":0}", state.Value, "replacement rejected");
        }),
        Case("chord.state.queues-listener-triggered-changes-in-sequence-order", () =>
        {
            var state = ReplicatedState.Create(V(0));
            var deliveries = new List<(int Value, long Sequence)>();
            var lateDeliveries = new List<ReplicatedStateDelivery>();
            var nested = false;
            state.Subscribe((_, _, delivery) =>
            {
                if (delivery.Kind != ReplicatedStateDeliveryKind.Update || nested) return;
                nested = true;
                state.Change(Bg, draft => draft.AsObject().Set("value", 2));
                state.Subscribe((_, _, late) => lateDeliveries.Add(late));
            });
            state.Subscribe((value, _, delivery) =>
            {
                if (delivery.Kind == ReplicatedStateDeliveryKind.Update) deliveries.Add((ValueOf(value), delivery.Sequence));
            });
            state.Change(Bg, draft => draft.AsObject().Set("value", 1));
            Seq([(1, 1L), (2, 2L)], deliveries, "deliveries");
            Seq([ReplicatedStateDelivery.Hydrate(2)], lateDeliveries, "late deliveries");
        }),
        Case("chord.state.isolates-listener-failures-after-committing-the-revision", () => WithUnhandledReports(reports =>
        {
            var state = ReplicatedState.Create(V(0));
            var received = new List<long>();
            state.Subscribe((_, _, delivery) =>
            {
                if (delivery.Kind == ReplicatedStateDeliveryKind.Update) throw new InvalidOperationException("listener failed");
            });
            state.Subscribe((_, _, delivery) => received.Add(delivery.Sequence));
            state.Change(Bg, draft => draft.AsObject().Set("value", 1));
            Same("{\"value\":1}", state.Value, "committed");
            Seq([0L, 1L], received, "received");
            Check(reports is [InvalidOperationException { Message: "listener failed" }], "one report of the failure");
        })),
        // Adapted: the placed value is immutable, so it cannot change and two placements need no copies; the draft's edit of one slot
        // copies that slot only (left is a new object, right keeps the placed reference).
        Case("chord.state.copies-assigned-values-by-value", () =>
        {
            var external = V(1);
            var state = ReplicatedState.Create(J("{\"left\":null,\"right\":null}"));
            state.Change(Bg, draft =>
            {
                var root = draft.AsObject();
                root.Set("left", external);
                root.Set("right", external);
                root.GetObject("left").Set("value", 2);
            });
            Equal(1, ValueOf(external), "external unchanged");
            Same("{\"left\":{\"value\":2},\"right\":{\"value\":1}}", state.Value, "value");
            Check(!ReferenceEquals(At(state.Value, "left"), At(state.Value, "right")), "left and right independent");
        }),
        // Adapted: alias freedom of the replacement is structural for immutable values.
        Case("chord.state.takes-immutable-ownership-of-alias-free-replacements", () =>
        {
            var state = ReplicatedState.Create(J("{\"left\":{\"value\":1},\"right\":{\"value\":2}}"));
            var left = At(state.Value, "left");
            var replacement = JsonValue.Object(KeyValuePair.Create("left", J(left.ToJson())), KeyValuePair.Create("right", J(left.ToJson())));
            state.Replace(Bg, replacement);
            Check(ReferenceEquals(replacement, state.Value), "replacement adopted as is");
            Check(!ReferenceEquals(At(state.Value, "left"), At(state.Value, "right")), "no alias");
            state.Change(Bg, draft => draft.AsObject().GetObject("left").Set("value", 9));
            Same("{\"left\":{\"value\":9},\"right\":{\"value\":1}}", state.Value, "value");
        }),
        Case("chord.state.replaces-atomically-and-ignores-deeply-equal-replacements", () =>
        {
            var state = ReplicatedState.Create(J("{\"value\":{\"nested\":1},\"retained\":{\"nested\":2}}"));
            var previous = state.Value;
            state.Replace(Bg, J("{\"value\":{\"nested\":1},\"retained\":{\"nested\":2}}"));
            Check(ReferenceEquals(previous, state.Value), "deeply equal replacement ignored");
            state.Replace(Bg, ((JsonObjectValue)state.Value).SetItem("value", J("{\"nested\":2}")));
            Same("{\"value\":{\"nested\":2},\"retained\":{\"nested\":2}}", state.Value, "value");
            Check(ReferenceEquals(At(previous, "retained"), At(state.Value, "retained")), "retained shared");
        }),

        // authoritative replicated state sources
        Case("chord.state.source.captures-before-activation-and-drains-queued-commits-in-order", () =>
        {
            var first = V(1);
            var second = V(2);
            var source = new TestSource<JsonValue>(V(0), 10);
            source.OnAttach = () =>
            {
                source.Commit(first, SetValue(1));
                source.Commit(second, SetValue(2));
            };
            var state = ReplicatedState.Attach(source);
            var deliveries = new List<(JsonValue Value, long Sequence)>();
            state.Subscribe((value, _, delivery) => deliveries.Add((value, delivery.Sequence)));
            Check(ReferenceEquals(second, state.Value), "value is the last buffered frame");
            Check(deliveries is [{ Sequence: 2 } only] && ReferenceEquals(only.Value, second), "one hydration at sequence 2");
        }),
        Case("chord.state.source.hydrates-at-sequence-zero-when-attaching-after-existing-commits", () =>
        {
            var current = V(1);
            var source = new TestSource<JsonValue>(V(0), 40);
            source.Commit(current, SetValue(1));
            var state = ReplicatedState.Attach(source);
            var deliveries = new List<long>();
            state.Subscribe((_, _, delivery) => deliveries.Add(delivery.Sequence));
            Check(ReferenceEquals(current, state.Value), "value");
            Seq([0L], deliveries, "deliveries");
        }),
        Case("chord.state.source.publishes-exact-source-values-without-applying-or-re-diffing", () =>
        {
            var source = new TestSource<JsonValue>(V(0));
            var state = ReplicatedState.Attach(source);
            var next = V(1);
            // Deliberately not the operation that produces next: Chord must publish the source value as is.
            var ops = SetValue(7);
            JsonValue? publishedValue = null;
            state.Subscribe((value, _, delivery) =>
            {
                if (delivery.Kind == ReplicatedStateDeliveryKind.Update) publishedValue = value;
            });
            source.Commit(next, ops);
            Check(ReferenceEquals(next, state.Value), "value reference");
            Check(ReferenceEquals(next, publishedValue), "published value reference");
        }),
        Case("chord.state.source.buffers-reentrant-frames-and-skips-updates-covered-by-a-late-hydration", () =>
        {
            var source = new TestSource<JsonValue>(V(0));
            var state = ReplicatedState.Attach(source);
            var received = new List<int>();
            var late = new List<(ReplicatedStateDelivery Delivery, int Value)>();
            var nested = false;
            state.Subscribe((_, _, delivery) =>
            {
                if (delivery.Kind != ReplicatedStateDeliveryKind.Update || delivery.Sequence != 1 || nested) return;
                nested = true;
                source.Commit(V(2), SetValue(2));
                state.Subscribe((value, _, lateDelivery) => late.Add((lateDelivery, ValueOf(value))));
            });
            state.Subscribe((value, _, delivery) =>
            {
                if (delivery.Kind == ReplicatedStateDeliveryKind.Update) received.Add(ValueOf(value));
            });
            source.Commit(V(1), SetValue(1));
            Seq([1, 2], received, "received");
            Seq([(ReplicatedStateDelivery.Hydrate(2), 2)], late, "late");
        }),
        Case("chord.state.source.reports-listener-failures-without-throwing-them-into-the-source", () =>
        {
            var source = new TestSource<JsonValue>(V(0));
            var errors = new List<Exception>();
            var state = ReplicatedState.Attach(source, new ReplicatedStateSourceOptions { OnError = errors.Add });
            var received = new List<int>();
            state.Subscribe((_, _, delivery) =>
            {
                if (delivery.Kind == ReplicatedStateDeliveryKind.Update) throw new InvalidOperationException("listener failed");
            });
            state.Subscribe((value, _, delivery) =>
            {
                if (delivery.Kind == ReplicatedStateDeliveryKind.Update) received.Add(ValueOf(value));
            });
            source.Commit(V(1), SetValue(1));
            source.Commit(V(2), SetValue(2));
            Seq([1, 2], received, "received");
            Seq(["listener failed", "listener failed"], errors.Select(error => error.Message), "errors");
        }),
        Case("chord.state.source.reports-cursor-gaps-disposes-the-broken-attachment-and-ignores-later-frames", () =>
        {
            var source = new TestSource<JsonValue>(V(0), 5);
            var errors = new List<Exception>();
            var state = ReplicatedState.Attach(source, new ReplicatedStateSourceOptions { OnError = errors.Add });
            source.Commit(V(2), SetValue(2), Bg, 7);
            Check(errors.Count == 1 && errors[0].Message.Contains("expected 6, received 7"), "gap reported");
            Equal(0, source.Attachments.Count, "attachment disposed");
            Same("{\"value\":0}", state.Value, "value unchanged");
            source.Commit(V(3), SetValue(3), Bg, 8);
            Same("{\"value\":0}", state.Value, "later frames ignored");
        }),
        Case("chord.state.source.keeps-attachments-independent-and-disposes-each-idempotently", () =>
        {
            var source = new TestSource<JsonValue>(V(0));
            var first = ReplicatedState.Attach(source);
            var second = ReplicatedState.Attach(source);
            Equal(2, source.Attachments.Count, "two attachments");
            source.Commit(V(1), SetValue(1));
            Equal(1, ValueOf(first.Value), "first");
            Equal(1, ValueOf(second.Value), "second");

            first.Dispose();
            first.Dispose();
            Equal(1, source.Attachments.Count, "one attachment left");
            source.Commit(V(2), SetValue(2));
            Equal(1, ValueOf(first.Value), "first keeps its last value");
            Equal(2, ValueOf(second.Value), "second advances");
            second.Dispose();
            Equal(0, source.Attachments.Count, "none left");
        }),

        // replicated-state-delivery.test.ts: standalone case
        // Adapted: queueMicrotask is mocked upstream to capture the default report; here the default report is the
        // ReplicatedState.UnhandledError event, raised synchronously.
        Case("chord.state.delivery.mutable-state-reports-rejected-callbacks-through-its-default-error-reporter", () =>
        {
            var reports = new List<Exception>();
            Action<Exception> handler = reports.Add;
            ReplicatedState.UnhandledError += handler;
            try
            {
                var state = ReplicatedState.Create(V(0));
                var failure = new InvalidOperationException("async mutable hydration");
                var received = new List<int>();
                state.Subscribe((value, _, _) =>
                {
                    received.Add(ValueOf(value));
                    return ValueOf(value) == 0 ? Task.FromException(failure) : Task.CompletedTask;
                });
                state.Replace(Bg, V(1));
                Seq([0, 1], received, "received");
                Check(reports is [var only] && ReferenceEquals(only, failure), "one report of the failure");
            }
            finally
            {
                ReplicatedState.UnhandledError -= handler;
            }
        }),
        // Native: the concurrency model. Commits from several threads are delivered to a synchronous subscription in sequence order and to
        // an asynchronous one without overlapping callbacks, in increasing sequence, ending with the newest revision.
        Case("chord.state.native.concurrent-commits-deliver-serially-in-sequence-order", async () =>
        {
            const int threads = 4, perThread = 200, total = threads * perThread;
            var state = ReplicatedState.Create(J("{}"));
            var exact = Updates(state);
            var delivered = new List<long>();
            var active = 0;
            var overlapped = false;
            var last = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            state.Subscribe(async (_, _, delivery) =>
            {
                if (Interlocked.Increment(ref active) != 1) overlapped = true;
                await Task.Yield();
                delivered.Add(delivery.Sequence);
                Interlocked.Decrement(ref active);
                if (delivery.Sequence == total) last.TrySetResult();
            });
            await Task.WhenAll(Enumerable.Range(0, threads).Select(thread => Task.Run(() =>
            {
                for (var index = 0; index < perThread; index++)
                    state.Change(Bg, draft => draft.AsObject().Set($"t{thread}-{index}", JsonValue.Number(index)));
            })));
            await last.Task;
            Equal(total, ((JsonObjectValue)state.Value).Count, "every commit applied");
            lock (exact) Seq(Enumerable.Range(1, total).Select(sequence => (long)sequence), exact, "a synchronous subscriber sees every sequence in order");
            Check(!overlapped, "callbacks of one subscription never overlap");
            Check(delivered[0] == 0 && delivered.Zip(delivered.Skip(1)).All(pair => pair.First < pair.Second), "deliveries increase");
        }),
        // Native: a change on another thread waits for a running mutation and drafts over its committed revision, never a stale base.
        Case("chord.state.native.a-concurrent-change-waits-for-the-running-mutation", async () =>
        {
            var state = ReplicatedState.Create(J("{\"a\":0}"));
            var sequences = Updates(state);
            using var entered = new ManualResetEventSlim();
            using var otherStarted = new ManualResetEventSlim();
            JsonValue? seen = null;
            var first = Task.Run(() => state.Change(Bg, draft =>
            {
                draft.AsObject().Set("a", 1);
                entered.Set();
                Check(otherStarted.Wait(TimeSpan.FromSeconds(30)), "other change started");
            }));
            Check(entered.Wait(TimeSpan.FromSeconds(30)), "first mutation entered");
            var second = Task.Run(() =>
            {
                otherStarted.Set();
                state.Change(Bg, draft =>
                {
                    seen = draft.AsObject()["a"];
                    draft.AsObject().Set("b", 2);
                });
            });
            await Task.WhenAll(first, second);
            Same("1", seen!, "second mutation drafts over the first commit");
            Same("{\"a\":1,\"b\":2}", state.Value, "both committed");
            lock (sequences) Seq([1L, 2L], sequences, "published in order");
        }),
        // Native: an async lambda binds to Action<Draft> as async void; it is rejected before it runs, so no continuation can touch a
        // revoked draft. Returned ValueTasks and faulted tasks are PromiseLike too.
        Case("chord.state.native.rejects-async-mutations", () =>
        {
            var state = ReplicatedState.Create(V(0));
            var previous = state.Value;
            var updates = Updates(state);
            var ran = 0;
            var asyncLambda = Throws<InvalidOperationException>(() => state.Change(Bg, async draft =>
            {
                ran++;
                draft.AsObject().Set("value", 1);
                await Task.Yield();
            }), "async lambda");
            Check(asyncLambda.Message.Contains("synchronous", StringComparison.Ordinal), "message: " + asyncLambda.Message);
            Equal(0, ran, "async lambda never ran");
            Throws<InvalidOperationException>(() => state.Change(Bg, draft =>
            {
                draft.AsObject().Set("value", 2);
                return ValueTask.CompletedTask;
            }), "returned ValueTask");
            Throws<InvalidOperationException>(() => state.Change(Bg, draft =>
            {
                draft.AsObject().Set("value", 3);
                return Task.FromException(new InvalidOperationException("observed"));
            }), "returned faulted task");
            Check(ReferenceEquals(previous, state.Value), "nothing committed");
            Equal(0, updates.Count, "nothing published");
            state.Change(Bg, draft => draft.AsObject().Delete("missing"));
            Equal(0, updates.Count, "a non-awaitable return value is ignored");
        }),
        // Native: upstream's replace goes through the tracker's prepareReplace, which adopts the root replacement itself (no diff), and a
        // no-op change or replacement publishes nothing and keeps the current value.
        Case("chord.state.native.replace-publishes-the-root-and-no-ops-publish-nothing", () =>
        {
            var state = ReplicatedState.Create(J("{\"value\":0,\"nested\":{\"value\":1}}"));
            var previous = state.Value;
            var published = new List<(JsonValue Value, long Sequence)>();
            state.Subscribe((value, _, delivery) =>
            {
                if (delivery.Kind == ReplicatedStateDeliveryKind.Update) published.Add((value, delivery.Sequence));
            });
            state.Change(Bg, draft => draft.AsObject().Set("value", 0));
            state.Change(Bg, draft => draft.AsObject().GetObject("nested").Set("value", 1));
            state.Replace(Bg, J("{\"value\":0,\"nested\":{\"value\":1}}"));
            Check(ReferenceEquals(previous, state.Value), "no-ops keep the value");
            Equal(0, published.Count, "no-ops publish nothing");
            var replacement = J("{\"value\":1,\"nested\":{\"value\":1}}");
            state.Replace(Bg, replacement);
            Check(ReferenceEquals(replacement, state.Value), "replacement adopted");
            Check(published is [{ Sequence: 1 } only] && ReferenceEquals(only.Value, replacement), "one publication of the replacement root");
        }),
        // Native: v1.2.0 publishes no operations, so the compact operations the dropped state-level case checked through the exact
        // listeners are checked on the tracker a change prepares through (MutableReplicatedState.Change is tracker.BeginChange, Prepare
        // and Adopt).
        Case("chord.state.native.changes-prepare-compact-string-splice-and-permutation-operations", () =>
        {
            var (a, b, c) = (J("{\"id\":\"a\"}"), J("{\"id\":\"b\"}"), J("{\"id\":\"c\"}"));
            var tracker = Tracker.Track(Obj(("text", "abcdefgh"), ("values", JsonValue.Array(a, b, c))));
            var batches = new List<JsonValue>();
            void Change(Action<DraftObject> mutate)
            {
                var change = tracker.BeginChange();
                mutate(change.State.AsObject());
                var prepared = change.Prepare();
                tracker.Adopt(prepared);
                batches.Add(OpsJson(prepared.Ops));
            }
            Change(draft =>
            {
                draft.Set("text", "defghxyz");
                draft.GetArray("values").RemoveAt(0);
            });
            Change(draft => draft.GetArray("values").Reverse());
            Same("[[[\"t\",[\"text\"],3],[\"a\",[\"text\"],\"xyz\"],[\"p\",[\"values\"],0,1,[]]],[[\"m\",[\"values\"],[1,0]]]]",
                JsonValue.Array(batches), "batches");
        }),
        .. StateDeliveryCases(StateKind.Mutable),
        .. StateDeliveryCases(StateKind.Attached),
        .. StateListenerErrorCases(StateKind.Attached),
    ];

    private static IEnumerable<(string, Func<Task>)> ChordApplyTrustedCases() =>
    [
        // Native: applyImmutableTrusted is an internal option of DeltaApply (see ApplyImmutableTrusted.cs); it must agree with it.
        Case("chord.apply-immutable-trusted.matches-validated-apply-and-shares-untouched-subtrees", () =>
        {
            var input = J("{\"text\":\"a\",\"values\":[1,2],\"nested\":{\"value\":1},\"stable\":{\"value\":9}}");
            var operations = Ops("[[\"a\",[\"text\"],\"b\"],[\"t\",[\"text\"],1],[\"p\",[\"values\"],1,1,[3,4]],[\"m\",[\"values\"],[2,0,1]],[\"s\",[\"nested\",\"value\"],2],[\"d\",[\"values\",0]]]");
            var trusted = DeltaApplyTrusted.Apply(input, operations);
            Same(DeltaApply.Apply(input, operations).ToJson(), trusted, "same result");
            Check(ReferenceEquals(At(input, "stable"), At(trusted, "stable")), "untouched subtree shared");
            Throws<PathError>(() => DeltaApplyTrusted.Apply(input, Ops("[[\"a\",[\"stable\"],\"x\"]]")), "unresolvable still throws");
        }),
    ];

    // state-value.test.ts
    private static IEnumerable<(string, Func<Task>)> ChordStateValueCases() =>
    [
        Case("chord.state.value.takes-ownership-of-an-alias-free-mutable-json-root-without-freezing", () =>
        {
            var input = J("{\"left\":{\"value\":1},\"right\":{\"value\":1}}");
            var tracker = Tracker.Track(input);
            Check(ReferenceEquals(input, tracker.Value), "root taken as is");
            Check(!ReferenceEquals(At(tracker.Value, "left"), At(tracker.Value, "right")), "no alias");
        }),
        Case("chord.state.value.commits-transaction-copies-in-place-while-sharing-unchanged-branches", () =>
        {
            var tracker = Tracker.Track(J("{\"changed\":{\"value\":1},\"retained\":{\"value\":2}}"));
            var baseRoot = tracker.Value;
            var baseSnapshot = baseRoot.ToJson();
            var change = tracker.BeginChange();
            Root(change).GetObject("changed").Set("value", 3);
            var prepared = change.Prepare();
            Check(!ReferenceEquals(At(prepared.Value, "changed"), At(baseRoot, "changed")), "changed branch copied");
            Check(ReferenceEquals(At(prepared.Value, "retained"), At(baseRoot, "retained")), "retained branch shared");
            tracker.Adopt(prepared);
            Equal(baseSnapshot, baseRoot.ToJson(), "base unchanged");
            Check(ReferenceEquals(prepared.Value, tracker.Value), "adopted");
        }),
        // Adapted: placing one immutable value twice may share its reference (nothing can mutate it), so the guarded independence is
        // checked by editing one placement through the draft: the other keeps the placed value.
        Case("chord.state.value.makes-repeated-placements-independent", () =>
        {
            var tracker = Tracker.Track(J("{\"values\":[]}"));
            var change = tracker.BeginChange();
            var shared = V(1);
            Root(change).GetArray("values").AddRange([shared, shared]);
            Root(change).GetArray("values").GetObject(0).Set("value", 2);
            var prepared = change.Prepare();
            Same("[{\"value\":2},{\"value\":1}]", At(prepared.Value, "values"), "values");
            Check(!ReferenceEquals(At(prepared.Value, "values", 0), At(prepared.Value, "values", 1)), "independent placements");
            Equal(1, ValueOf(shared), "placed value unchanged");
        }),
    ];

    // state-draft.test.ts
    private static IEnumerable<(string, Func<Task>)> ChordStateDraftCases() =>
    [
        Case("chord.state.draft.copies-only-changed-branches", () =>
        {
            var tracker = Tracker.Track(J("{\"changed\":{\"count\":1,\"sibling\":{\"value\":\"kept\"}},\"untouched\":{\"value\":2}}"));
            var baseRoot = tracker.Value;
            var change = tracker.BeginChange();
            var first = Root(change).GetObject("changed");
            Check(ReferenceEquals(first, Root(change).GetObject("changed")), "one draft per slot");
            Root(change).GetObject("changed").Set("count", 3);
            var prepared = change.Prepare();
            Check(!ReferenceEquals(baseRoot, prepared.Value), "new root");
            Check(!ReferenceEquals(At(baseRoot, "changed"), At(prepared.Value, "changed")), "changed copied");
            Check(ReferenceEquals(At(baseRoot, "changed", "sibling"), At(prepared.Value, "changed", "sibling")), "sibling shared");
            Check(ReferenceEquals(At(baseRoot, "untouched"), At(prepared.Value, "untouched")), "untouched shared");
        }),
        // Adapted: frozen bases are every base (immutable); fill(9, 1, 3) and copyWithin(1, 0, 2) are written as the Sets they perform.
        Case("chord.state.draft.supports-frozen-bases-deletion-and-native-array-mutators", () =>
        {
            var tracker = Tracker.Track(J("{\"optional\":\"remove\",\"values\":[3,1,2]}"));
            var change = tracker.BeginChange();
            Root(change).Delete("optional");
            var values = Root(change).GetArray("values");
            values.Add(4);
            Equal(4d, Num(values.RemoveAt(values.Count - 1)), "pop");
            values.Insert(0, 0);
            Equal(0d, Num(values.RemoveAt(0)), "shift");
            Same("[1]", JsonValue.Array(values.Splice(1, 1, 5, 4)), "splice");
            values.Sort((left, right) => Num(left).CompareTo(Num(right)));
            values.Reverse();
            values.Set(1, 9);
            values.Set(2, 9);
            var (copy0, copy1) = (values[0], values[1]);
            values.Set(1, copy0);
            values.Set(2, copy1);
            Same("{\"values\":[5,5,9,2]}", change.Prepare().Value, "value");
        }),
        // Adapted: unshift(held) places the held draft's current value (held.ToValue()); the held handle keeps following its element.
        Case("chord.state.draft.copies-inserted-draft-values-without-aliasing-their-original-handle", () =>
        {
            var tracker = Tracker.Track(J("{\"values\":[{\"value\":1},{\"value\":2}]}"));
            var change = tracker.BeginChange();
            var held = Root(change).GetArray("values").GetObject(0);
            Root(change).GetArray("values").Insert(0, held.ToValue());
            held.Set("value", 9);
            var prepared = change.Prepare();
            Same("[{\"value\":1},{\"value\":9},{\"value\":2}]", At(prepared.Value, "values"), "values");
            Check(!ReferenceEquals(At(prepared.Value, "values", 0), At(prepared.Value, "values", 1)), "no alias");
        }),
        // Adapted: a C# sort comparison sees immutable element values and must not use the draft (DraftArray.Sort), so the comparator's
        // per-row edits are recorded during the sort and written through the element drafts after it, which follow their rows.
        Case("chord.state.draft.keeps-comparator-edits-when-sorting-object-drafts", () =>
        {
            var tracker = Tracker.Track(J("{\"rows\":[{\"rank\":2,\"comparisons\":0},{\"rank\":1,\"comparisons\":0}]}"));
            var change = tracker.BeginChange();
            var rows = Root(change).GetArray("rows");
            var drafts = Enumerable.Range(0, rows.Count).Select(rows.GetObject).ToArray();
            var originals = drafts.Select(draft => draft.ToValue()).ToArray();
            var comparisons = new Dictionary<JsonValue, int>(ReferenceEqualityComparer.Instance);
            rows.Sort((left, right) =>
            {
                comparisons[left] = comparisons.GetValueOrDefault(left) + 1;
                comparisons[right] = comparisons.GetValueOrDefault(right) + 1;
                return Num(At(left, "rank")).CompareTo(Num(At(right, "rank")));
            });
            for (var index = 0; index < drafts.Length; index++)
                drafts[index].Set("comparisons", Num(drafts[index]["comparisons"]) + comparisons.GetValueOrDefault(originals[index]));
            var prepared = change.Prepare();
            Seq([1d, 2d], ((JsonArrayValue)At(prepared.Value, "rows")).Select(row => Num(At(row, "rank"))), "ranks");
            Seq([1d, 1d], ((JsonArrayValue)At(prepared.Value, "rows")).Select(row => Num(At(row, "comparisons"))), "comparisons");
        }),
        // Adapted: shift() returning the removed element's draft is the element draft taken before RemoveAt(0).
        Case("chord.state.draft.drops-writes-through-detached-child-handles", () =>
        {
            var tracker = Tracker.Track(J("{\"child\":{\"value\":\"removed\"},\"items\":[{\"value\":\"removed\"},{\"value\":\"kept\"}]}"));
            var change = tracker.BeginChange();
            var child = Root(change).GetObject("child");
            var shifted = Root(change).GetArray("items").GetObject(0);
            Root(change).GetArray("items").RemoveAt(0);
            Root(change).Delete("child");
            child.Set("value", "detached child");
            shifted.Set("value", "detached item");
            Same("{\"items\":[{\"value\":\"kept\"}]}", change.Prepare().Value, "value");
        }),
        Case("chord.state.draft.inserts-100000-items-with-unshift-without-argument-overflow", () => InsertsHundredThousand(unshift: true)),
        Case("chord.state.draft.inserts-100000-items-with-splice-without-argument-overflow", () => InsertsHundredThousand(unshift: false)),
        // Adapted: undefined is a null reference and is rejected (ArgumentNullException) before the draft changes, also inside a batch
        // placement; NaN cannot become a JsonValue (strict JSON); a Date has no JsonValue form, so that assertion is dropped.
        Case("chord.state.draft.rejects-non-strict-json-placements-without-mutating-the-draft-or-base", () =>
        {
            var tracker = Tracker.Track(J("{\"number\":0,\"payload\":null,\"values\":[1,2]}"));
            var change = tracker.BeginChange();
            Throws<ArgumentNullException>(() => Root(change).GetArray("values").Set(0, null!), "undefined element");
            Throws<ArgumentNullException>(() => Root(change).GetArray("values").AddRange([J("{\"nested\":1}"), null!]), "undefined in a placement batch");
            var nan = Throws<ArgumentException>(() => Root(change).Set("number", JsonValue.Number(double.NaN)), "NaN");
            Check(nan.Message.Contains("strict JSON", StringComparison.Ordinal), "message: " + nan.Message);
            Check(JsonValue.DeepEquals(tracker.Value, change.State.ToValue()), "draft unchanged");
            change.Abort();
            Same("{\"number\":0,\"payload\":null,\"values\":[1,2]}", tracker.Value, "base unchanged");
        }),
        // Adapted: `delete values[0]` has no C# form; the hole it would make is the write past the end, rejected the same way.
        Case("chord.state.draft.rejects-array-holes-without-mutating-the-committed-base", () =>
        {
            var tracker = Tracker.Track(J("{\"values\":[1,2]}"));
            var change = tracker.BeginChange();
            var error = Throws<InvalidOperationException>(() => Root(change).GetArray("values").Set(3, 0), "hole");
            Check(error.Message.Contains("dense", StringComparison.Ordinal) || error.Message.Contains("holes", StringComparison.Ordinal), "message: " + error.Message);
            change.Abort();
            Same("[1,2]", At(tracker.Value, "values"), "base unchanged");
        }),
    ];

    private static void InsertsHundredThousand(bool unshift)
    {
        var tracker = Tracker.Track(J("{\"values\":[-1]}"));
        var change = tracker.BeginChange();
        var items = Enumerable.Range(0, 100_000).Select(value => (JsonValue)value).ToArray();
        var values = Root(change).GetArray("values");
        if (unshift) values.InsertRange(0, items);
        else values.Splice(1, 0, items);
        var prepared = change.Prepare();
        Equal(100_001, ((JsonArrayValue)At(prepared.Value, "values")).Count, "length");
        Equal(0d, Num(At(prepared.Value, "values", unshift ? 0 : 1)), "first inserted");
        Equal(99_999d, Num(At(prepared.Value, "values", unshift ? 99_999 : 100_000)), "last inserted");
        Check(JsonValue.DeepEquals(DeltaApply.Apply(prepared.Base, prepared.Ops), prepared.Value), "replay");
    }

    // state-diff.test.ts is delta-diff.test.ts (ported in DiffCases.cs) without "keeps a large rotation payload-free" and with its last
    // case renamed "falls back to a base operation when leaf operations are larger" (that copy drops one replay assertion, which the
    // shared body keeps). The inputs and expected operations are identical, so the bodies are shared under chord.state.diff ids.
    private static IEnumerable<(string, Func<Task>)> ChordStateDiffCases()
    {
        const string prefix = "chord.diff.";
        foreach (var (id, run) in ChordDiffCases())
        {
            if (id == prefix + "keeps-a-large-rotation-payload-free") continue;
            var name = id == prefix + "bounds-a-wide-normalized-array-fallback-with-a-root-replacement"
                ? "falls-back-to-a-base-operation-when-leaf-operations-are-larger"
                : id[prefix.Length..];
            yield return ("chord.state.diff." + name, run);
        }
    }

    // state-fuzz.test.ts: the same mulberry32 generator (Mulberry32, TrackerCases.cs) and seeds 1..100, 100 steps each; the plain-object `expected` is a mutable model.
    private static IEnumerable<(string, Func<Task>)> ChordStateFuzzCases() =>
    [
        Case("chord.state.fuzz.converges-across-randomized-prepared-revisions", () =>
        {
            for (var seed = 1; seed <= 100; seed++)
            {
                var random = Mulberry32((uint)seed);
                var expected = new FuzzDocument([.. Enumerable.Range(0, 4).Select(id => new FuzzItem(id, $"item-{id}", 0))], "start", 0, null);
                var tracker = Tracker.Track(expected.ToValue());
                var replica = J(tracker.Value.ToJson());
                for (var step = 0; step < 100; step++)
                {
                    var choice = (int)Math.Floor(random() * 14);
                    var value = seed * 1_000 + step;
                    var where = $"seed {seed} step {step} choice {choice}";
                    var baseRoot = tracker.Value;
                    var baseJson = baseRoot.ToJson();
                    expected.Mutate(choice, value);
                    var change = tracker.BeginChange();
                    FuzzMutate(change.State.AsObject(), choice, value);
                    var prepared = change.Prepare();
                    var operations = Ops(OpsJson(prepared.Ops).ToJson());
                    Equal(baseJson, baseRoot.ToJson(), "prepare base " + where);
                    Check(ReferenceEquals(baseRoot, prepared.Base), "prepared base " + where);
                    replica = DeltaApply.Apply(replica, operations);
                    Check(JsonValue.DeepEquals(replica, prepared.Value), "replay " + where);
                    tracker.Adopt(prepared);
                    Equal(baseJson, baseRoot.ToJson(), "adopt base " + where);
                    Check(ReferenceEquals(prepared.Value, tracker.Value), "adopted " + where);
                    var model = expected.ToValue();
                    Check(JsonValue.DeepEquals(model, tracker.Value), $"state {where}: expected {model.ToJson()}, actual {tracker.Value.ToJson()}");
                    Check(JsonValue.DeepEquals(model, replica), "replica " + where);
                    AssertAliasFree(tracker.Value);
                }
            }
        }),
    ];

    private static void FuzzMutate(DraftObject document, int choice, int value)
    {
        JsonValue Item() => Obj(("id", value), ("text", $"item-{value}"), ("score", value % 7));
        var items = document.GetArray("items");
        switch (choice)
        {
            case 0: document.Set("text", Str(document["text"]) + $"-{value}"); break;
            case 1:
            {
                var text = Str(document["text"]);
                document.Set("text", text[Math.Min(2, text.Length)..] + value);
                break;
            }
            case 2: items.Add(Item()); break;
            case 3: items.Insert(0, Item()); break;
            case 4: if (items.Count > 0) items.RemoveAt(0); break;
            case 5: if (items.Count > 0) items.RemoveAt(items.Count - 1); break;
            case 6:
            {
                var index = items.Count == 0 ? 0 : value % (items.Count + 1);
                items.Splice(index, items.Count == 0 ? 0 : value % 2, Item());
                break;
            }
            case 7: items.Reverse(); break;
            case 8: items.Sort((left, right) => Num(At(left, "id")).CompareTo(Num(At(right, "id")))); break;
            case 9: if (items.Count > 0) items.GetObject(value % items.Count).Set("score", value); break;
            case 10:
            {
                var meta = document.GetObject("meta");
                meta.Set("revision", Num(meta["revision"]) + 1);
                meta.Set("label", $"revision-{value}");
                break;
            }
            case 11: document.GetObject("meta").Delete("label"); break;
            case 12: if (items.Count > 1) items.Set(1, J(items[0].ToJson())); break;
            default:
                for (var index = 0; index < Math.Min(2, items.Count); index++) items.Set(index, Item());
                break;
        }
    }

    private sealed record FuzzItem(int Id, string Text, int Score)
    {
        public int Score { get; set; } = Score;
        public JsonValue ToValue() => Obj(("id", Id), ("text", Text), ("score", Score));
    }

    /// <summary>The plain mutable document upstream mutates alongside the draft.</summary>
    private sealed class FuzzDocument(List<FuzzItem> items, string text, int revision, string? label)
    {
        public void Mutate(int choice, int value)
        {
            FuzzItem Item() => new(value, $"item-{value}", value % 7);
            switch (choice)
            {
                case 0: text += $"-{value}"; break;
                case 1: text = text[Math.Min(2, text.Length)..] + value; break;
                case 2: items.Add(Item()); break;
                case 3: items.Insert(0, Item()); break;
                case 4: if (items.Count > 0) items.RemoveAt(0); break;
                case 5: if (items.Count > 0) items.RemoveAt(items.Count - 1); break;
                case 6:
                {
                    var index = items.Count == 0 ? 0 : value % (items.Count + 1);
                    var remove = items.Count == 0 ? 0 : Math.Min(value % 2, items.Count - index);
                    items.RemoveRange(index, remove);
                    items.Insert(index, Item());
                    break;
                }
                case 7: items.Reverse(); break;
                case 8:
                {
                    var sorted = items.OrderBy(item => item.Id).ToList();
                    items.Clear();
                    items.AddRange(sorted);
                    break;
                }
                case 9: if (items.Count > 0) items[value % items.Count].Score = value; break;
                case 10:
                    revision += 1;
                    label = $"revision-{value}";
                    break;
                case 11: label = null; break;
                case 12: if (items.Count > 1) items[1] = items[0] with { }; break;
                default:
                    for (var index = 0; index < Math.Min(2, items.Count); index++) items[index] = Item();
                    break;
            }
        }

        public JsonValue ToValue()
        {
            var meta = label is null ? Obj(("revision", revision)) : Obj(("revision", revision), ("label", label));
            return Obj(("items", JsonValue.Array(items.Select(item => item.ToValue()))), ("text", text), ("meta", meta));
        }
    }

    /// <summary>Upstream's expectAliasFree: no container appears twice in one revision.</summary>
    private static void AssertAliasFree(JsonValue root)
    {
        var seen = new Dictionary<JsonValue, string>(ReferenceEqualityComparer.Instance);
        void Visit(JsonValue current, string path)
        {
            if (current is not (JsonObjectValue or JsonArrayValue)) return;
            if (seen.TryGetValue(current, out var previous)) throw new InvalidOperationException($"container at {path} aliases {previous}");
            seen[current] = path;
            if (current is JsonArrayValue array)
            {
                for (var index = 0; index < array.Count; index++) Visit(array[index], $"{path}[{index}]");
                return;
            }
            foreach (var (key, value) in (JsonObjectValue)current) Visit(value, $"{path}.{key}");
        }
        Visit(root, "$root");
    }

    private enum StateKind { Mutable, Attached }

    private sealed record StateFixture(IReplicatedState<JsonValue> State, Action<JsonValue, DurableContext> Publish);

    private static StateFixture Fixture(StateKind kind, Action<Exception>? onError = null)
    {
        onError ??= _ => { };
        switch (kind)
        {
            case StateKind.Mutable:
            {
                var mutable = ReplicatedState.Create(V(0));
                return new StateFixture(mutable, (value, context) => mutable.Replace(context, value));
            }
            default:
            {
                Action<ReplicatedStateSourceFrame<JsonValue>>? receive = null;
                var state = ReplicatedState.Attach(new DelegateSource<JsonValue>(new ReplicatedStateSnapshot<JsonValue>(V(0), 0), listener => receive = listener),
                    new ReplicatedStateSourceOptions { OnError = onError });
                return new StateFixture(state, (value, context) => receive!(new ReplicatedStateSourceFrame<JsonValue>(ValueOf(value), value, SetValue(ValueOf(value)), context)));
            }
        }
    }

    // replicated-state-delivery.test.ts: describe.each(["mutable", "attached"]) public delivery
    private static IEnumerable<(string, Func<Task>)> StateDeliveryCases(StateKind kind)
    {
        var prefix = "chord.state.delivery." + kind.ToString().ToLowerInvariant() + ".";
        yield return Case(prefix + "awaits-hydration-and-each-update-independently-of-other-subscribers", async () =>
        {
            var (state, publish) = Fixture(kind);
            var hydration = new TaskCompletionSource();
            var update = new TaskCompletionSource();
            var events = new List<string>();
            var fast = new List<int>();
            state.Subscribe(async (value, _, _) =>
            {
                events.Add($"start:{ValueOf(value)}");
                if (ValueOf(value) == 0) await hydration.Task;
                if (ValueOf(value) == 1) await update.Task;
                events.Add($"end:{ValueOf(value)}");
            });
            state.Subscribe((value, _, _) => fast.Add(ValueOf(value)));
            publish(V(1), Bg);
            publish(V(2), Bg);
            Seq(["start:0"], events, "events");
            Seq([0, 1, 2], fast, "fast");
            Same("{\"value\":2}", state.Value!, "value");
            hydration.SetResult();
            Seq(["start:0", "end:0", "start:1"], events, "after hydration");
            update.SetResult();
            Seq(["start:0", "end:0", "start:1", "end:1", "start:2", "end:2"], events, "after update");
            await Task.CompletedTask;
        });
        foreach (var count in new[] { 100, 101, 102, 201, 202 })
        {
            yield return Case(prefix + $"bounds-{count}-pending-deliveries", () =>
            {
                var (state, publish) = Fixture(kind);
                var hydration = new TaskCompletionSource();
                var received = new List<int>();
                state.Subscribe((value, _, _) =>
                {
                    received.Add(ValueOf(value));
                    return ValueOf(value) == 0 ? hydration.Task : Task.CompletedTask;
                });
                for (var value = 1; value <= count; value += 1) publish(V(value), Bg);
                Seq([0], received, "before release");
                hydration.SetResult();
                var first = (count - 1) / 100 * 100 + 1;
                Seq([0, .. Enumerable.Range(first, count - first + 1)], received, "after release");
            });
        }
        yield return Case(prefix + "excludes-a-running-update-from-overflow-and-retains-exact-value-context-delivery", () =>
        {
            var (state, publish) = Fixture(kind);
            var gate = new TaskCompletionSource();
            var context = Bg.WithCancel().Context;
            var received = new List<(JsonValue Value, DurableContext Context, ReplicatedStateDelivery Delivery)>();
            state.Subscribe((value, deliveryContext, delivery) =>
            {
                received.Add((value, deliveryContext, delivery));
                return ValueOf(value) == 1 ? gate.Task : Task.CompletedTask;
            });
            for (var value = 1; value < 102; value += 1) publish(V(value), Bg);
            publish(V(102), context);
            var adopted = state.Value;
            publish(V(103), Bg);
            Seq([0, 1], received.Select(entry => ValueOf(entry.Value)), "before release");
            gate.SetResult();
            Seq([0, 1, 102, 103], received.Select(entry => ValueOf(entry.Value)), "after release");
            Check(ReferenceEquals(adopted, received[2].Value), "adopted value");
            Check(ReferenceEquals(context, received[2].Context), "exact context");
            Equal(ReplicatedStateDelivery.Update(102), received[2].Delivery, "delivery");
        });
        yield return Case(prefix + "serializes-reentrant-hydration-and-update-callbacks", () =>
        {
            var (state, publish) = Fixture(kind);
            var events = new List<string>();
            state.Subscribe((value, _, _) =>
            {
                events.Add($"start:{ValueOf(value)}");
                if (ValueOf(value) < 2) publish(V(ValueOf(value) + 1), Bg);
                events.Add($"end:{ValueOf(value)}");
            });
            Seq(["start:0", "end:0", "start:1", "end:1", "start:2", "end:2"], events, "events");
        });
        yield return Case(prefix + "treats-two-subscriptions-of-the-same-callback-independently", () =>
        {
            var (state, publish) = Fixture(kind);
            var gate = new TaskCompletionSource();
            var received = new List<int>();
            Func<JsonValue, DurableContext, ReplicatedStateDelivery, Task> listener = (value, _, _) =>
            {
                received.Add(ValueOf(value));
                return ValueOf(value) == 0 ? gate.Task : Task.CompletedTask;
            };
            var stopFirst = state.Subscribe(listener);
            var stopSecond = state.Subscribe(listener);
            publish(V(1), Bg);
            stopFirst.Dispose();
            stopFirst.Dispose();
            gate.SetResult();
            Seq([0, 0, 1], received, "received");
            stopSecond.Dispose();
        });
        // Adapted: `await Promise.resolve()` (letting the resolved hydration promise settle) is unnecessary because completed tasks
        // continue inline; `expect(stop()).toBeUndefined()` is Dispose returning void.
        yield return Case(prefix + "unsubscribe-drops-queued-callbacks-without-joining-or-aborting-the-running-callback", () =>
        {
            var (state, publish) = Fixture(kind);
            var gate = new TaskCompletionSource();
            var context = Bg.WithCancel().Context;
            var received = new List<int>();
            var completed = false;
            var stop = state.Subscribe(async (value, _, _) =>
            {
                received.Add(ValueOf(value));
                if (ValueOf(value) == 1)
                {
                    await gate.Task;
                    completed = true;
                }
            });
            publish(V(1), context);
            publish(V(2), context);
            stop.Dispose();
            publish(V(3), context);
            Check(!context.IsCancellationRequested, "context not aborted");
            Check(!completed, "running callback not joined");
            gate.SetResult();
            Check(completed, "running callback completes");
            Seq([0, 1], received, "received");
        });
    }

    // replicated-state-delivery.test.ts: describe("attached listener errors")
    private static IEnumerable<(string, Func<Task>)> StateListenerErrorCases(StateKind kind)
    {
        var prefix = "chord.state.delivery." + kind.ToString().ToLowerInvariant() + ".";
        yield return Case(prefix + "isolates-a-synchronous-hydration-failure-without-removing-the-subscription", () =>
        {
            var errors = new List<Exception>();
            var (state, publish) = Fixture(kind, errors.Add);
            var received = new List<int>();
            var failure = new InvalidOperationException("sync hydration");
            state.Subscribe((value, _, _) =>
            {
                received.Add(ValueOf(value));
                if (ValueOf(value) == 0) throw failure;
            });
            publish(V(1), Bg);
            Check(errors is [var only] && ReferenceEquals(only, failure), "one report of the failure");
            Seq([0, 1], received, "received");
        });
        yield return Case(prefix + "observes-hydration-rejection-synchronous-throw-and-update-rejection-while-continuing-delivery", () =>
        {
            var errors = new List<Exception>();
            var (state, publish) = Fixture(kind, errors.Add);
            var gate = new TaskCompletionSource();
            var received = new List<int>();
            var fast = new List<int>();
            state.Subscribe((value, _, _) =>
            {
                received.Add(ValueOf(value));
                return ValueOf(value) switch
                {
                    0 => gate.Task,
                    1 => throw new InvalidOperationException("sync update"),
                    2 => Task.FromException(new InvalidOperationException("async update")),
                    _ => Task.CompletedTask,
                };
            });
            state.Subscribe((value, _, _) => fast.Add(ValueOf(value)));
            for (var value = 1; value <= 3; value += 1) publish(V(value), Bg);
            gate.SetException(new InvalidOperationException("async hydration"));
            Seq([0, 1, 2, 3], received, "received");
            Seq(["async hydration", "sync update", "async update"], errors.Select(error => error.Message), "errors");
            Seq([0, 1, 2, 3], fast, "fast");
        });
        yield return Case(prefix + "still-observes-a-callback-rejection-after-unsubscribe", () =>
        {
            var errors = new List<Exception>();
            var (state, publish) = Fixture(kind, errors.Add);
            var gate = new TaskCompletionSource();
            var received = new List<int>();
            var stop = state.Subscribe((value, _, _) =>
            {
                received.Add(ValueOf(value));
                return gate.Task;
            });
            publish(V(1), Bg);
            stop.Dispose();
            var failure = new InvalidOperationException("stopped callback");
            gate.SetException(failure);
            Check(errors is [var only] && ReferenceEquals(only, failure), "one report of the failure");
            Seq([0], received, "received");
        });
    }

    /// <summary>replicated-state.test.ts TestSource: commits buffer per attachment until activation, then deliver synchronously.</summary>
    private sealed class TestSource<T>(T value, long cursor = 0) : IReplicatedStateSource<T>
    {
        private T value = value;
        private long cursor = cursor;
        public List<TestSourceAttachment<T>> Attachments { get; } = [];
        public Action? OnAttach { get; set; }

        public IReplicatedStateSourceAttachment<T> Attach()
        {
            TestSourceAttachment<T>? attachment = null;
            attachment = new TestSourceAttachment<T>(new ReplicatedStateSnapshot<T>(value, cursor), () => Attachments.Remove(attachment!));
            Attachments.Add(attachment);
            OnAttach?.Invoke();
            return attachment;
        }

        public void Commit(T next, IReadOnlyList<Op> ops, DurableContext? context = null, long? at = null)
        {
            value = next;
            cursor = at ?? cursor + 1;
            var frame = new ReplicatedStateSourceFrame<T>(cursor, next, ops, context ?? Bg);
            foreach (var attachment in Attachments.ToArray()) attachment.Publish(frame);
        }
    }

    private sealed class TestSourceAttachment<T>(ReplicatedStateSnapshot<T> snapshot, Action onDispose) : IReplicatedStateSourceAttachment<T>
    {
        private readonly List<ReplicatedStateSourceFrame<T>> buffer = [];
        private Action<ReplicatedStateSourceFrame<T>>? listener;
        private bool activated;
        private bool disposed;

        public ReplicatedStateSnapshot<T> Snapshot { get; } = snapshot;

        public void Activate(Action<ReplicatedStateSourceFrame<T>> activeListener)
        {
            if (activated) throw new InvalidOperationException("attachment is already active");
            activated = true;
            listener = activeListener;
            var frames = buffer.ToArray();
            buffer.Clear();
            foreach (var frame in frames) activeListener(frame);
        }

        public void Publish(ReplicatedStateSourceFrame<T> frame)
        {
            if (disposed) return;
            if (listener is null) buffer.Add(frame);
            else listener(frame);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            buffer.Clear();
            onDispose();
        }
    }

    /// <summary>The inline source of the delivery fixture: a fixed snapshot whose activation hands out the frame listener.</summary>
    private sealed class DelegateSource<T>(ReplicatedStateSnapshot<T> snapshot, Action<Action<ReplicatedStateSourceFrame<T>>> activate)
        : IReplicatedStateSource<T>, IReplicatedStateSourceAttachment<T>
    {
        public ReplicatedStateSnapshot<T> Snapshot => snapshot;
        public IReplicatedStateSourceAttachment<T> Attach() => this;
        public void Activate(Action<ReplicatedStateSourceFrame<T>> listener) => activate(listener);
        public void Dispose() { }
    }
}
