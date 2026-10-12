using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/chord/test/state.test.ts and state-delivery.test.ts (the cases that do
// not need the draft tracker or the diff). Adaptations, each repeated where it applies:
// - change(context, draft => ...) is MutableReplicatedState.Apply(context, ops) with the operations that draft would have produced;
//   the cases using it guard publication, sequencing and sharing, not the draft.
// - replace publishes [["r", value]] until the diff lands; no ported case asserts the replace operations.
// - Promise deferreds are TaskCompletionSource barriers; subscription continuations run synchronously when a task completes, so the
//   state upstream reaches after `await` / vi.waitFor is asserted right after SetResult/SetException, with no sleeps.
// - Object.isFrozen checks are dropped: JsonValue trees are immutable, never frozen copies. toBe is ReferenceEquals.
// - getReplicatedStateInternals(state) is ReplicatedStateInternals.Get<JsonValue>(state).
internal static partial class Program
{
    private static readonly DurableContext Bg = DurableContext.Background;

    private static JsonValue V(int value) => JsonValue.Object(KeyValuePair.Create("value", (JsonValue)JsonValue.Number(value)));
    private static int Num(JsonValue value) => (int)((JsonNumberValue)At(value, "value")).Value;
    private static IReadOnlyList<Op> SetValue(int value) => [new SetOp(DeltaPath.Of("value"), JsonValue.Number(value))];
    private static IReplicatedStateInternals<JsonValue>? Internals(object state) => ReplicatedStateInternals.Get<JsonValue>(state);
    private static void Seq<T>(IEnumerable<T> expected, IEnumerable<T> actual, string what) =>
        Check(expected.SequenceEqual(actual), $"{what}: expected [{string.Join(", ", expected)}], actual [{string.Join(", ", actual)}]");

    private static IEnumerable<(string, Func<Task>)> ChordStateCases() =>
    [
        // Adapted: the draft's two writes to changed.value are the two set operations it would record.
        Case("chord.state.publishes-one-immutable-structurally-shared-revision", () =>
        {
            var state = ReplicatedState.Create(J("{\"changed\":{\"value\":1},\"retained\":{\"value\":2}}"));
            var previous = state.Value;
            var deliveries = new List<long>();
            state.Subscribe((_, _, delivery) => deliveries.Add(delivery.Sequence));
            state.Apply(Bg, Ops("[[\"s\",[\"changed\",\"value\"],3],[\"s\",[\"changed\",\"value\"],4]]"));
            Same("{\"changed\":{\"value\":4},\"retained\":{\"value\":2}}", state.Value, "value");
            Check(!ReferenceEquals(state.Value, previous), "new root");
            Check(!ReferenceEquals(At(state.Value, "changed"), At(previous, "changed")), "changed branch copied");
            Check(ReferenceEquals(At(state.Value, "retained"), At(previous, "retained")), "retained branch shared");
            Seq([0L, 1L], deliveries, "deliveries");
        }),
        // Adapted: draft writes become set operations.
        Case("chord.state.queues-listener-triggered-changes-in-sequence-order", () =>
        {
            var state = ReplicatedState.Create(V(0));
            var sourceSequences = new List<long>();
            var deliveries = new List<(int Value, long Sequence)>();
            var lateDeliveries = new List<ReplicatedStateDelivery>();
            var nested = false;
            Internals(state)!.SubscribeSource((_, _, _) =>
            {
                if (nested) return;
                nested = true;
                state.Apply(Bg, SetValue(2));
                state.Subscribe((_, _, delivery) => lateDeliveries.Add(delivery));
            });
            Internals(state)!.SubscribeSource((_, sequence, _) => sourceSequences.Add(sequence));
            state.Subscribe((value, _, delivery) =>
            {
                if (delivery.Kind == ReplicatedStateDeliveryKind.Update) deliveries.Add((Num(value), delivery.Sequence));
            });
            state.Apply(Bg, SetValue(1));
            Seq([1L, 2L], sourceSequences, "source sequences");
            Seq([(1, 1L), (2, 2L)], deliveries, "deliveries");
            Seq([ReplicatedStateDelivery.Hydrate(2)], lateDeliveries, "late deliveries");
        }),
        // Adapted: draft write becomes a set operation.
        Case("chord.state.isolates-listener-failures-after-committing-the-revision", () =>
        {
            var state = ReplicatedState.Create(V(0));
            var received = new List<long>();
            Internals(state)!.SubscribeSource((_, _, _) => throw new InvalidOperationException("listener failed"));
            Internals(state)!.SubscribeSource((_, sequence, _) => received.Add(sequence));
            Equal("listener failed", Throws<InvalidOperationException>(() => state.Apply(Bg, SetValue(1)), "change throws").Message, "message");
            Same("{\"value\":1}", state.Value, "committed");
            Seq([1L], received, "received");
        }),
        // Adapted: the follow-up draft write is a set operation; alias freedom is structural for immutable values.
        Case("chord.state.takes-immutable-ownership-of-alias-free-replacements", () =>
        {
            var state = ReplicatedState.Create(J("{\"left\":{\"value\":1},\"right\":{\"value\":2}}"));
            var left = At(state.Value, "left");
            var replacement = JsonValue.Object(KeyValuePair.Create("left", J(left.ToJson())), KeyValuePair.Create("right", J(left.ToJson())));
            state.Replace(Bg, replacement);
            Check(ReferenceEquals(replacement, state.Value), "replacement adopted as is");
            Check(!ReferenceEquals(At(state.Value, "left"), At(state.Value, "right")), "no alias");
            state.Apply(Bg, Ops("[[\"s\",[\"left\",\"value\"],9]]"));
            Same("{\"left\":{\"value\":9},\"right\":{\"value\":1}}", state.Value, "value");
        }),
        Case("chord.state.validates-replica-revisions-without-freezing-shared-immutable-payloads", () =>
        {
            var replica = new ReplicatedStateReplica(_ => { });
            var initial = J("{\"rows\":[{\"value\":1}]}");
            replica.Hydrate(0, [new ReplaceOp(initial)], Bg);
            Check(ReferenceEquals(initial, replica.Value), "hydrated value is the snapshot payload");

            var inserted = J("{\"value\":2}");
            replica.Update(1, [new SpliceOp(DeltaPath.Of("rows"), 1, 0, [inserted])], Bg);
            Check(!ReferenceEquals(initial, replica.Value), "new root");
            Same("[{\"value\":1},{\"value\":2}]", At(replica.Value!, "rows"), "rows");
            Check(ReferenceEquals(At(initial, "rows", 0), At(replica.Value!, "rows", 0)), "retained row shared");
            Check(ReferenceEquals(inserted, At(replica.Value!, "rows", 1)), "inserted payload shared");
            Equal(1, ((JsonArrayValue)At(initial, "rows")).Count, "initial unchanged");
        }),
        // Adapted: NaN cannot be represented (JsonValue.Number rejects it, chord.json.strict-json-only), so the invalid adopted update
        // is one whose path does not resolve; the before-hydration and malformed-permutation halves are verbatim.
        Case("chord.state.clears-a-replica-when-an-adopted-update-is-invalid", () =>
        {
            var errors = new List<Exception>();
            var replica = new ReplicatedStateReplica(errors.Add);
            replica.Hydrate(0, [new ReplaceOp(V(0))], Bg);
            Throws<PathError>(() => replica.Update(1, Ops("[[\"s\",[\"missing\",\"value\"],1]]"), Bg), "invalid update");
            Check(replica.Value is null, "cleared");
            Check(Throws<InvalidOperationException>(() => replica.Update(2, SetValue(2), Bg), "update after clear").Message.Contains("before hydration"), "message");

            var malformed = new ReplicatedStateReplica(errors.Add);
            malformed.Hydrate(0, [new ReplaceOp(J("{\"values\":[1,2]}"))], Bg);
            Throws<PathError>(() => malformed.Update(1, [new MoveOp(DeltaPath.Of("values"), [0])], Bg), "malformed permutation");
            Check(malformed.Value is null, "malformed cleared");
            Equal(0, errors.Count, "nothing reported");
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
            var snapshot = Internals(state)!.Snapshot();
            Check(ReferenceEquals(second, snapshot.Value) && snapshot.Sequence == 2, "snapshot");
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
        Case("chord.state.source.publishes-exact-source-value-and-operation-references-without-applying-or-re-diffing", () =>
        {
            var source = new TestSource<JsonValue>(V(0));
            var state = ReplicatedState.Attach(source);
            var next = V(1);
            var ops = SetValue(1);
            IReadOnlyList<Op>? publishedOps = null;
            Internals(state)!.SubscribeSource((received, _, _) => publishedOps = received);
            JsonValue? publishedValue = null;
            state.Subscribe((value, _, delivery) =>
            {
                if (delivery.Kind == ReplicatedStateDeliveryKind.Update) publishedValue = value;
            });
            source.Commit(next, ops);
            Check(ReferenceEquals(next, state.Value), "value reference");
            Check(ReferenceEquals(next, publishedValue), "published value reference");
            Check(ReferenceEquals(ops, publishedOps), "published ops reference");
        }),
        Case("chord.state.source.buffers-reentrant-frames-and-skips-updates-covered-by-a-late-hydration", () =>
        {
            var source = new TestSource<JsonValue>(V(0));
            var state = ReplicatedState.Attach(source);
            var received = new List<int>();
            var late = new List<(ReplicatedStateDelivery Delivery, int Value)>();
            var nested = false;
            Internals(state)!.SubscribeSource((_, sequence, _) =>
            {
                if (sequence != 1 || nested) return;
                nested = true;
                source.Commit(V(2), SetValue(2));
                state.Subscribe((value, _, delivery) => late.Add((delivery, Num(value))));
            });
            state.Subscribe((value, _, delivery) =>
            {
                if (delivery.Kind == ReplicatedStateDeliveryKind.Update) received.Add(Num(value));
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
                if (delivery.Kind == ReplicatedStateDeliveryKind.Update) received.Add(Num(value));
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
            Equal(1, Num(first.Value), "first");
            Equal(1, Num(second.Value), "second");

            first.Dispose();
            first.Dispose();
            Equal(1, source.Attachments.Count, "one attachment left");
            source.Commit(V(2), SetValue(2));
            Equal(1, Num(first.Value), "first keeps its last value");
            Equal(2, Num(second.Value), "second advances");
            second.Dispose();
            Equal(0, source.Attachments.Count, "none left");
        }),

        // state-delivery.test.ts: standalone cases
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
                    received.Add(Num(value));
                    return Num(value) == 0 ? Task.FromException(failure) : Task.CompletedTask;
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
        Case("chord.state.delivery.replica-disconnect-drops-obsolete-pending-work-but-waits-for-the-running-callback-before-rehydration", () =>
        {
            var replica = new ReplicatedStateReplica(_ => { });
            var gate = new TaskCompletionSource();
            var received = new List<ReplicatedStateDelivery>();
            replica.Subscribe((_, _, delivery) =>
            {
                received.Add(delivery);
                return delivery.Sequence == 0 ? gate.Task : Task.CompletedTask;
            });
            replica.Hydrate(0, [new ReplaceOp(V(0))], Bg);
            replica.Update(1, SetValue(1), Bg);
            replica.Clear();
            replica.Hydrate(50, [new ReplaceOp(V(50))], Bg);
            replica.Update(51, SetValue(51), Bg);
            Seq([ReplicatedStateDelivery.Hydrate(0)], received, "before release");
            gate.SetResult();
            Seq([ReplicatedStateDelivery.Hydrate(0), ReplicatedStateDelivery.Hydrate(50), ReplicatedStateDelivery.Update(51)], received, "after release");
        }),
        Case("chord.state.delivery.cold-replicas-hydrate-all-listeners-before-their-reentrant-updates", () =>
        {
            var replica = new ReplicatedStateReplica(_ => { });
            var second = new List<ReplicatedStateDelivery>();
            replica.Subscribe((_, _, delivery) =>
            {
                if (delivery.Kind == ReplicatedStateDeliveryKind.Hydrate) replica.Update(1, SetValue(1), Bg);
            });
            replica.Subscribe((_, _, delivery) => second.Add(delivery));
            replica.Hydrate(0, [new ReplaceOp(V(0))], Bg);
            Seq([ReplicatedStateDelivery.Hydrate(0), ReplicatedStateDelivery.Update(1)], second, "second listener");
        }),

        // Native: the concurrency model. Commits from several threads are delivered to exact listeners in sequence order and to one
        // subscription without overlapping callbacks, in increasing sequence, ending with the newest revision.
        Case("chord.state.native.concurrent-commits-deliver-serially-in-sequence-order", async () =>
        {
            const int threads = 4, perThread = 200, total = threads * perThread;
            var state = ReplicatedState.Create(J("{}"));
            var exact = new List<long>();
            var exactGate = new object();
            Internals(state)!.SubscribeSource((_, sequence, _) => { lock (exactGate) exact.Add(sequence); });
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
                    state.Apply(Bg, [new SetOp(DeltaPath.Of($"t{thread}-{index}"), JsonValue.Number(index))]);
            })));
            await last.Task;
            Equal(total, ((JsonObjectValue)state.Value).Count, "every commit applied");
            lock (exactGate) Seq(Enumerable.Range(1, total).Select(sequence => (long)sequence), exact, "exact listeners see every sequence in order");
            Check(!overlapped, "callbacks of one subscription never overlap");
            Check(delivered[0] == 0 && delivered.Zip(delivered.Skip(1)).All(pair => pair.First < pair.Second), "deliveries increase");
        }),
        .. StateDeliveryCases(StateKind.Mutable),
        .. StateDeliveryCases(StateKind.Attached),
        .. StateDeliveryCases(StateKind.Replica),
        .. StateListenerErrorCases(StateKind.Attached),
        .. StateListenerErrorCases(StateKind.Replica),
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

    private enum StateKind { Mutable, Attached, Replica }

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
            case StateKind.Attached:
            {
                Action<ReplicatedStateSourceFrame<JsonValue>>? receive = null;
                var state = ReplicatedState.Attach(new DelegateSource<JsonValue>(new ReplicatedStateSnapshot<JsonValue>(V(0), 0), listener => receive = listener),
                    new ReplicatedStateSourceOptions { OnError = onError });
                return new StateFixture(state, (value, context) => receive!(new ReplicatedStateSourceFrame<JsonValue>(Num(value), value, SetValue(Num(value)), context)));
            }
            default:
            {
                var replica = new ReplicatedStateReplica(onError);
                replica.Hydrate(0, [new ReplaceOp(V(0))], Bg);
                return new StateFixture(replica, (value, context) => replica.Update(Num(value), SetValue(Num(value)), context));
            }
        }
    }

    // state-delivery.test.ts: describe.each(["mutable", "attached", "replica"]) public delivery
    private static IEnumerable<(string, Func<Task>)> StateDeliveryCases(StateKind kind)
    {
        var prefix = "chord.state.delivery." + kind.ToString().ToLowerInvariant() + ".";
        yield return Case(prefix + "awaits-hydration-and-each-update-independently-of-other-subscribers-and-exact-listeners", async () =>
        {
            var (state, publish) = Fixture(kind);
            var hydration = new TaskCompletionSource();
            var update = new TaskCompletionSource();
            var events = new List<string>();
            var exact = new List<long>();
            var fast = new List<int>();
            Internals(state)?.SubscribeSource((_, sequence, _) => exact.Add(sequence));
            state.Subscribe(async (value, _, _) =>
            {
                events.Add($"start:{Num(value)}");
                if (Num(value) == 0) await hydration.Task;
                if (Num(value) == 1) await update.Task;
                events.Add($"end:{Num(value)}");
            });
            state.Subscribe((value, _, _) => fast.Add(Num(value)));
            publish(V(1), Bg);
            publish(V(2), Bg);
            Seq(["start:0"], events, "events");
            Seq([0, 1, 2], fast, "fast");
            Same("{\"value\":2}", state.Value!, "value");
            if (kind != StateKind.Replica) Seq([1L, 2L], exact, "exact");
            hydration.SetResult();
            Seq(["start:0", "end:0", "start:1"], events, "after hydration");
            update.SetResult();
            Seq(["start:0", "end:0", "start:1", "end:1", "start:2", "end:2"], events, "after update");
            await Task.CompletedTask;
        });
        foreach (var count in new[] { 100, 101, 102, 201, 202 })
        {
            yield return Case(prefix + $"bounds-{count}-pending-deliveries-without-changing-exact-publication", () =>
            {
                var (state, publish) = Fixture(kind);
                var hydration = new TaskCompletionSource();
                var received = new List<int>();
                var exact = new List<long>();
                Internals(state)?.SubscribeSource((_, sequence, _) => exact.Add(sequence));
                state.Subscribe((value, _, _) =>
                {
                    received.Add(Num(value));
                    return Num(value) == 0 ? hydration.Task : Task.CompletedTask;
                });
                for (var value = 1; value <= count; value += 1) publish(V(value), Bg);
                Seq([0], received, "before release");
                if (kind != StateKind.Replica) Seq(Enumerable.Range(1, count).Select(index => (long)index), exact, "exact");
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
                return Num(value) == 1 ? gate.Task : Task.CompletedTask;
            });
            for (var value = 1; value < 102; value += 1) publish(V(value), Bg);
            publish(V(102), context);
            var adopted = state.Value;
            publish(V(103), Bg);
            Seq([0, 1], received.Select(entry => Num(entry.Value)), "before release");
            gate.SetResult();
            Seq([0, 1, 102, 103], received.Select(entry => Num(entry.Value)), "after release");
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
                events.Add($"start:{Num(value)}");
                if (Num(value) < 2) publish(V(Num(value) + 1), Bg);
                events.Add($"end:{Num(value)}");
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
                received.Add(Num(value));
                return Num(value) == 0 ? gate.Task : Task.CompletedTask;
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
                received.Add(Num(value));
                if (Num(value) == 1)
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

    // state-delivery.test.ts: describe.each(["attached", "replica"]) listener errors
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
                received.Add(Num(value));
                if (Num(value) == 0) throw failure;
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
                received.Add(Num(value));
                return Num(value) switch
                {
                    0 => gate.Task,
                    1 => throw new InvalidOperationException("sync update"),
                    2 => Task.FromException(new InvalidOperationException("async update")),
                    _ => Task.CompletedTask,
                };
            });
            state.Subscribe((value, _, _) => fast.Add(Num(value)));
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
                received.Add(Num(value));
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

    /// <summary>state.test.ts TestSource: commits buffer per attachment until activation, then deliver synchronously.</summary>
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
