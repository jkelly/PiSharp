// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/session-watches.test.ts. Verbatim apart from the adaptations in
// SessionSupport.cs and these:
// - "never invokes the listener inline": a watch started from outside the line delivers in its own line job on the thread pool, so the
//   listener checks it is not running inside Start on the starting thread, and upstream's "no delivery yet right after start" becomes that
//   same check (the pool may already have delivered).
// - AbortController/withAbortSignal is a CancellationTokenSource through DurableContext.WithCancellation; withCancel is WithCancel.
// - applyImmutable is DeltaApply.Apply (immutable application of a batch).
// - A reload after unloading is shown by the storage read, not a new object (an immutable stored value may be handed out again).
// - A listener takes one value call before the context: Change(call), or the call itself where upstream compares whole calls.
// - The garbage-collection cases use WeakReference and GC.Collect for WeakRef and --expose-gc, with what they hold built in a method the
//   JIT does not inline; as upstream's collect() waits a timer tick before each gc(), each round waits one, so a line job or I/O completion
//   that is still unwinding on another thread (SQL Server's) has let go first. A delegate that captures nothing is one cached instance
//   here, so a listener whose collection is checked captures a marker. "keeps no earlier incarnation alive" opens its Sessions on the
//   group's storage, as upstream's on a new MemoryStorage.
// Pi v1.5.0 sync: the watch is Chord's (Chord/Watch.cs, area watches); a Session close ends a watch Ended, a cancellation Aborted, a
// listener failure ListenerError. Area watch-folding (d8d99950a, 8b0caefc2 and 5f312dac4): the commits made while a listener is busy or not
// yet started arrive folded, and a watch follows its address (Session.DocumentFollower): a retirement is a Retired call with the last value
// and the watch goes on, a recreation arrives as a root replacement, and a watch's value is never null.
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using static Program;

internal static partial class SessionTests
{
    private static IEnumerable<(string, Func<Task>)> Watches(string prefix, Func<IStorage> storageFactory)
    {
        var stateDoc = Documents.DefineSessionDoc("watch.state", 1, () => O("{\"value\":0,\"items\":[\"a\",\"b\"],\"retained\":{\"label\":\"stable\"}}"),
            checkpointWhen: (_, _, _) => false);
        var keysDoc = Documents.DefineSessionDoc("watch.keys", 1, () => O("{\"keys\":{}}"), checkpointWhen: (_, _, _) => false);
        static double ValueOf(JsonObjectValue value) => N(value["value"]);
        Task SetValue(Session session, double value) => session.CommitAsync(async tx => (await tx.DocAsync(stateDoc))["value"] = value, Context);

        async Task<TestSession> CreateState()
        {
            var harness = Open(storageFactory);
            await harness.Session.CommitAsync(tx => tx.DocAsync(stateDoc), Context);
            return harness;
        }

        yield return Case(prefix + "never-creates-an-absent-document", async () =>
        {
            var (storage, session, _) = Open(storageFactory);
            var commits = storage.CommitCount;
            Check(await session.WatchDocAsync(stateDoc, Context) is null, "absent");
            Equal(commits, storage.CommitCount, "commits");
            Equal(0, storage.MintCount, "mints");
        });

        yield return Case(prefix + "keeps-the-acquisition-revision-until-start-then-delivers-the-commits-since-in-one-call", async () =>
        {
            var (_, session, publications) = await CreateState();
            var watch = (await session.WatchDocAsync(stateDoc, Context))!;
            var initial = watch.Value;
            await SetValue(session, 1);
            await Flush(session);
            var firstPublished = DocumentChanges(Last(publications))[0];
            await SetValue(session, 2);
            await Flush(session);
            var secondPublished = DocumentChanges(Last(publications))[0];
            Same(initial, watch.Value, "acquisition revision until start");

            var startingThread = Environment.CurrentManagedThreadId;
            var inStart = true;
            var inline = false;
            var deliveries = new List<(JsonObjectValue Value, ImmutableArray<Op> Ops)>();
            watch.Start((call, _) =>
            {
                var (value, ops, _) = Change(call);
                if (Volatile.Read(ref inStart) && Environment.CurrentManagedThreadId == startingThread) inline = true;
                Check(ReferenceEquals(watch.Value, value), "watch.value advances before the callback");
                lock (deliveries) deliveries.Add((value, ops));
                return Task.CompletedTask;
            });
            Volatile.Write(ref inStart, false);
            await Flush(session);
            Check(!inline, "the listener is never invoked inline");
            lock (deliveries)
            {
                // Both commits set `value`: folded, the second set replaces the first.
                Equal("[[\"s\",[\"value\"],1]]", OpsJson(firstPublished.Ops), "first commit's ops");
                Equal(1, deliveries.Count, "one call");
                Same(secondPublished.Value, deliveries[0].Value, "newest value");
                Equal(OpsJson(secondPublished.Ops), OpsJson(deliveries[0].Ops), "folded ops");
            }
            Equal(0.0, ValueOf(initial), "initial unchanged");

            // A listener that keeps up gets each commit's ops as published.
            await SetValue(session, 3);
            await Flush(session);
            var thirdPublished = DocumentChanges(Last(publications))[0];
            lock (deliveries)
            {
                Equal(2, deliveries.Count, "deliveries");
                Same(thirdPublished.Value, deliveries[1].Value, "third value");
                SameOps(thirdPublished.Ops, deliveries[1].Ops, "third ops");
            }
            await watch.StopAsync();
        });

        yield return Case(prefix + "serializes-callbacks-and-folds-the-commits-made-while-one-is-in-flight-into-the-next", async () =>
        {
            var (_, session, _) = await CreateState();
            var watch = (await session.WatchDocAsync(stateDoc, Context))!;
            var entered = new Deferred(inline: false);
            var release = new Deferred();
            var values = new List<double>();
            var batches = new List<string>();
            int active = 0, maxActive = 0;
            watch.Start(async (call, _) =>
            {
                var (value, ops, _) = Change(call);
                var now = Interlocked.Increment(ref active);
                int count;
                lock (values)
                {
                    maxActive = Math.Max(maxActive, now);
                    values.Add(ValueOf(value));
                    batches.Add(OpsJson(ops));
                    count = values.Count;
                }
                if (count == 1)
                {
                    entered.Resolve();
                    await release.Task;
                }
                Interlocked.Decrement(ref active);
            });
            await SetValue(session, 1);
            await entered.Task;
            for (var value = 2; value <= 20; value++) await SetValue(session, value);
            lock (values) Seq([1.0], values, "only the first is in flight");
            release.Resolve();
            await Flush(session);
            Equal(1, maxActive, "no overlap");
            lock (values)
            {
                Seq([1.0, 20.0], values, "the in-flight call, then the rest folded");
                Equal("[[\"s\",[\"value\"],20]]", batches[1], "folded ops");
            }
            await watch.StopAsync();
        });

        yield return Case(prefix + "allows-a-listener-to-initiate-a-later-session-commit", async () =>
        {
            var (_, session, _) = await CreateState();
            var watch = (await session.WatchDocAsync(stateDoc, Context))!;
            var completed = new Deferred(inline: false);
            var values = new List<double>();
            watch.Start(async (call, _) =>
            {
                var current = ValueOf(Change(call).Value);
                lock (values) values.Add(current);
                if (current == 1) await SetValue(session, 2);
                else completed.Resolve();
            });
            await SetValue(session, 1);
            await completed.Task;
            lock (values) Seq([1.0, 2.0], values, "values");
            await watch.StopAsync();
        });

        yield return Case(prefix + "folds-101-pending-commits-of-one-field-into-one-set", async () =>
        {
            var (_, session, _) = await CreateState();
            var watch = (await session.WatchDocAsync(stateDoc, Context))!;
            for (var value = 1; value <= 101; value++) await SetValue(session, value);
            var deliveries = new List<(double Value, string Ops)>();
            watch.Start((call, _) =>
            {
                var (value, ops, _) = Change(call);
                lock (deliveries) deliveries.Add((ValueOf(value), OpsJson(ops)));
                return Task.CompletedTask;
            });
            await Flush(session);
            lock (deliveries) Seq([(101.0, "[[\"s\",[\"value\"],101]]")], deliveries, "one set");
            await watch.StopAsync();
        });

        yield return Case(prefix + "replaces-the-root-once-more-than-10000-ops-are-pending-after-folding", async () =>
        {
            var (_, session, _) = Open(storageFactory);
            await session.CommitAsync(tx => tx.DocAsync(keysDoc), Context);
            var watch = (await session.WatchDocAsync(keysDoc, Context))!;
            // Sets of distinct keys, in commits small enough to stay ops: 4,000 + 4,000 + 2,001 pending ops.
            foreach (var (from, to) in new[] { (0, 4_000), (4_000, 8_000), (8_000, 10_001) })
            {
                await session.CommitAsync(async tx =>
                {
                    var keys = (await tx.DocAsync(keysDoc)).GetObject("keys");
                    for (var index = from; index < to; index++) keys["k" + index] = index;
                }, Context);
            }
            var deliveries = new List<(JsonObjectValue Value, string Ops)>();
            watch.Start((call, _) =>
            {
                var (value, ops, _) = Change(call);
                lock (deliveries) deliveries.Add((value, OpsJson(ops)));
                return Task.CompletedTask;
            });
            await Flush(session);
            lock (deliveries)
            {
                Equal(1, deliveries.Count, "one call");
                Same(watch.Value, deliveries[0].Value, "newest value");
                Equal("[[\"r\"," + watch.Value.ToJson() + "]]", deliveries[0].Ops, "root replacement");
            }
            Equal(10_001, ((JsonObjectValue)watch.Value["keys"]).Count, "keys");
            await watch.StopAsync();
        });

        yield return Case(prefix + "never-folds-the-in-flight-frame-into-the-next-call", async () =>
        {
            var (_, session, _) = await CreateState();
            var watch = (await session.WatchDocAsync(stateDoc, Context))!;
            var entered = new Deferred(inline: false);
            var release = new Deferred();
            var deliveries = new List<(double Value, string Ops)>();
            watch.Start(async (call, _) =>
            {
                var (value, ops, _) = Change(call);
                int count;
                lock (deliveries)
                {
                    deliveries.Add((ValueOf(value), OpsJson(ops)));
                    count = deliveries.Count;
                }
                if (count == 1)
                {
                    entered.Resolve();
                    await release.Task;
                }
            });
            await SetValue(session, 1);
            await entered.Task;
            for (var value = 2; value <= 102; value++) await SetValue(session, value);
            release.Resolve();
            await Flush(session);
            lock (deliveries)
            {
                Equal(1.0, deliveries[0].Value, "in-flight frame");
                Equal((102.0, "[[\"s\",[\"value\"],102]]"), deliveries[1], "the rest folded");
                Equal(2, deliveries.Count, "deliveries");
            }
            await watch.StopAsync();
        });

        yield return Case(prefix + "folds-a-retirement-into-the-pending-commits-call-which-reports-it-with-the-last-value-and-goes-on", async () =>
        {
            var (_, session, _) = await CreateState();
            var watch = (await session.WatchDocAsync(stateDoc, Context))!;
            for (var value = 1; value <= 100; value++) await SetValue(session, value);
            var last = (await session.SnapshotAsync(stateDoc, Context))!;
            await session.CommitAsync(tx => tx.RetireDocAsync(stateDoc), Context);
            var changes = new List<WatchValueCall<JsonObjectValue>>();
            watch.Start((call, _) =>
            {
                lock (changes) changes.Add(Change(call));
                return Task.CompletedTask;
            });
            await Flush(session);
            lock (changes)
            {
                Equal(1, changes.Count, "one call");
                Same(last, changes[0].Value, "the last value");
                Equal("[[\"s\",[\"value\"],100]]", OpsJson(changes[0].Ops), "folded ops");
                Check(changes[0].Retired, "retired");
            }
            Same(last, watch.Value, "watch.value");
            await SetValue(session, 7);
            await Flush(session);
            const string recreated = "{\"value\":7,\"items\":[\"a\",\"b\"],\"retained\":{\"label\":\"stable\"}}";
            lock (changes)
            {
                Equal(2, changes.Count, "calls");
                Equal(recreated, changes[1].Value.ToJson(), "the new incarnation");
                Equal("[[\"r\"," + recreated + "]]", OpsJson(changes[1].Ops), "a root replacement");
                Check(!changes[1].Retired, "not retired");
            }
            await watch.StopAsync();
        });

        yield return Case(prefix + "delivers-replayable-structural-no-op-commits-instead-of-suppressing-them", async () =>
        {
            var (_, session, _) = await CreateState();
            var watch = (await session.WatchDocAsync(stateDoc, Context))!;
            var initial = watch.Value;
            await session.CommitAsync(async tx =>
            {
                var items = (await tx.DocAsync(stateDoc)).GetArray("items");
                var first = items.RemoveAt(0);
                items.Insert(0, first);
            }, Context);
            var newest = (await session.SnapshotAsync(stateDoc, Context))!;
            Check(!ReferenceEquals(newest, initial), "a new revision");
            Check(JsonValue.DeepEquals(newest, initial), "equal value");
            var batches = new List<ImmutableArray<Op>>();
            watch.Start((call, _) =>
            {
                var (value, ops, _) = Change(call);
                Check(ReferenceEquals(value, newest), "the newest value");
                lock (batches) batches.Add(ops);
                return Task.CompletedTask;
            });
            await Flush(session);
            lock (batches)
            {
                Equal(1, batches.Count, "batches");
                Check(batches[0].Length > 0, "nonempty batch");
            }
            await watch.StopAsync();
        });

        yield return Case(prefix + "preserves-commit-context-values-without-inheriting-producer-cancellation", async () =>
        {
            var (_, session, _) = await CreateState();
            var watch = (await session.WatchDocAsync(stateDoc, Context))!;
            var key = new ContextKey<string>("watch-test");
            using var parentController = new CancellationTokenSource();
            var commitContext = DurableContext.Background.WithCancellation(parentController.Token).With(key, "newest-commit");
            var entered = new Deferred(inline: false);
            var release = new Deferred();
            DurableContext? deliveryContext = null;
            watch.Start(async (_, delivered) =>
            {
                deliveryContext = delivered;
                entered.Resolve();
                await release.Task;
            });
            await session.CommitAsync(async tx => (await tx.DocAsync(stateDoc))["value"] = 1, commitContext);
            await entered.Task;
            Equal("newest-commit", deliveryContext!.Get(key), "context value");
            Check(!deliveryContext.HasCancellation, "no producer cancellation");
            parentController.Cancel();
            var stopped = watch.StopAsync();
            Equal(WatchEnd.Stopped, await stopped, "stopped");
            Check(!deliveryContext.HasCancellation, "still no cancellation");
            release.Resolve();
        });

        yield return Case(prefix + "keeps-earlier-immutable-revisions-stable", async () =>
        {
            var (_, session, _) = await CreateState();
            var watch = (await session.WatchDocAsync(stateDoc, Context))!;
            var initial = watch.Value;
            JsonObjectValue? delivered = null;
            watch.Start((call, _) =>
            {
                delivered = Change(call).Value;
                return Task.CompletedTask;
            });
            await SetValue(session, 1);
            await Flush(session);
            Same(watch.Value, delivered, "delivered is the current value");
            Check(!ReferenceEquals(delivered, initial), "a new revision");
            Same(initial["retained"], delivered!["retained"], "retained subtree shared");
            Equal(0.0, ValueOf(initial), "initial unchanged");
            await watch.StopAsync();
        });

        yield return Case(prefix + "reports-a-change-and-a-retirement-in-one-commit-as-the-retirement-with-the-value-it-was-given-before", async () =>
        {
            var (storage, session, _) = await CreateState();
            var watch = (await session.WatchDocAsync(stateDoc, Context))!;
            var before = watch.Value;
            var changes = new List<WatchValueCall<JsonObjectValue>>();
            watch.Start((call, _) =>
            {
                lock (changes) changes.Add(Change(call));
                return Task.CompletedTask;
            });
            await session.CommitAsync(async tx =>
            {
                (await tx.DocAsync(stateDoc))["value"] = 5;
                await tx.RetireDocAsync(stateDoc);
            }, Context);
            await Flush(session);
            // Storage got the change, then the retirement; the commit publishes the retirement only.
            Seq(["document.change", "document.retire"], storage.LastCommit.Select(write => write.Type), "writes");
            lock (changes)
            {
                Equal(1, changes.Count, "one call");
                Same(before, changes[0].Value, "the value it was given before");
                Equal("[]", OpsJson(changes[0].Ops), "no ops");
                Check(changes[0].Retired, "retired");
            }
            await watch.StopAsync();
        });

        yield return Case(prefix + "follows-its-address-across-a-retirement-and-a-recreation-in-one-commit-in-one-call", async () =>
        {
            var (_, session, _) = await CreateState();
            var watch = (await session.WatchDocAsync(stateDoc, Context))!;
            var changes = new List<WatchValueCall<JsonObjectValue>>();
            watch.Start((call, _) =>
            {
                lock (changes) changes.Add(Change(call));
                return Task.CompletedTask;
            });
            await session.CommitAsync(async tx =>
            {
                await tx.RetireDocAsync(stateDoc);
                (await tx.DocAsync(stateDoc))["value"] = 10;
            }, Context);
            await Flush(session);
            var recreated = (await session.SnapshotAsync(stateDoc, Context))!;
            lock (changes)
            {
                Equal(1, changes.Count, "one call");
                Same(recreated, changes[0].Value, "the new incarnation");
                Equal("[[\"r\"," + recreated.ToJson() + "]]", OpsJson(changes[0].Ops), "a root replacement");
                Check(changes[0].Retired, "retired");
            }
            await SetValue(session, 11);
            await Flush(session);
            lock (changes)
            {
                Same(watch.Value, changes[1].Value, "the newest value");
                Equal("[[\"s\",[\"value\"],11]]", OpsJson(changes[1].Ops), "its ops");
                Check(!changes[1].Retired, "not retired");
            }
            Equal(11.0, ValueOf(watch.Value), "value");
            Check(!watch.Closed.IsCompleted, "still open");
            await watch.StopAsync();
        });

        yield return Case(prefix + "session-close-discards-retirement-buffered-before-start", async () =>
        {
            var (_, session, _) = await CreateState();
            var watch = (await session.WatchDocAsync(stateDoc, Context))!;
            var baseline = watch.Value;
            await session.CommitAsync(tx => tx.RetireDocAsync(stateDoc), Context);
            await session.CloseAsync(Context);
            Equal(WatchEnd.Ended, await watch.Closed, "closed");
            Same(baseline, watch.Value, "baseline kept");
            Throws(() => watch.Start((_, _) => Task.CompletedTask), "stopped", "late start");
        });

        yield return Case(prefix + "session-close-discards-retirement-behind-an-in-flight-callback", async () =>
        {
            var (_, session, _) = await CreateState();
            var watch = (await session.WatchDocAsync(stateDoc, Context))!;
            var entered = new Deferred(inline: false);
            var release = new Deferred();
            var values = new List<double>();
            watch.Start(async (call, _) =>
            {
                lock (values) values.Add(ValueOf(Change(call).Value));
                entered.Resolve();
                await release.Task;
            });
            await SetValue(session, 1);
            await entered.Task;
            await session.CommitAsync(tx => tx.RetireDocAsync(stateDoc), Context);
            await session.CloseAsync(Context);
            Equal(WatchEnd.Ended, await watch.Closed, "closed");
            release.Resolve();
            await Flush(session);
            lock (values) Seq([1.0], values, "values");
        });

        yield return Case(prefix + "supports-idempotent-stop-and-rejects-repeated-or-late-start", async () =>
        {
            var (_, session, _) = await CreateState();
            var started = (await session.WatchDocAsync(stateDoc, Context))!;
            started.Start((_, _) => Task.CompletedTask);
            Throws(() => started.Start((_, _) => Task.CompletedTask), "already started", "second start");
            var first = started.StopAsync();
            var second = started.StopAsync();
            Same(first, second, "the same terminal task");
            Equal(WatchEnd.Stopped, await first, "stopped");

            var stopped = (await session.WatchDocAsync(stateDoc, Context))!;
            await stopped.StopAsync();
            Throws(() => stopped.Start((_, _) => Task.CompletedTask), "stopped", "late start");
        });

        yield return Case(prefix + "cancels-acquisition-without-leaking-a-registered-watch", async () =>
        {
            var (storage, session, _) = await CreateState();
            await session.UnloadDocumentsAsync();
            var gate = storage.HoldFindDocument();
            var child = Context.WithCancel();
            var acquisition = session.WatchDocAsync(stateDoc, child.Context);
            await gate.Entered;
            child.Cancel(new InvalidOperationException("cancel acquisition"));
            gate.Release();
            await RejectsAsync(() => acquisition, "cancel acquisition", "acquisition");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "cancels-future-delivery-without-aborting-an-in-flight-callback", async () =>
        {
            var (_, session, _) = await CreateState();
            var child = Context.WithCancel();
            var watch = (await session.WatchDocAsync(stateDoc, child.Context))!;
            var entered = new Deferred(inline: false);
            var release = new Deferred();
            DurableContext? deliveryContext = null;
            watch.Start(async (_, delivered) =>
            {
                deliveryContext = delivered;
                entered.Resolve();
                await release.Task;
            });
            await SetValue(session, 1);
            await entered.Task;
            child.Cancel();
            Equal(WatchEnd.Aborted, await watch.Closed, "aborted");
            Check(!deliveryContext!.HasCancellation, "the callback context carries no cancellation");
            release.Resolve();
        });

        yield return Case(prefix + "session-close-stops-future-delivery-without-joining-an-in-flight-callback", async () =>
        {
            var (_, session, _) = await CreateState();
            var watch = (await session.WatchDocAsync(stateDoc, Context))!;
            var entered = new Deferred(inline: false);
            var release = new Deferred();
            DurableContext? deliveryContext = null;
            watch.Start(async (_, delivered) =>
            {
                deliveryContext = delivered;
                entered.Resolve();
                await release.Task;
            });
            await SetValue(session, 1);
            await entered.Task;
            await session.CloseAsync(Context);
            Check(!deliveryContext!.HasCancellation, "no cancellation");
            Equal(WatchEnd.Ended, await watch.Closed, "closed");
            release.Resolve();
        });

        yield return Case(prefix + "settles-listener-failure-on-only-the-affected-watch", async () =>
        {
            var (_, session, _) = await CreateState();
            var failed = (await session.WatchDocAsync(stateDoc, Context))!;
            var healthy = (await session.WatchDocAsync(stateDoc, Context))!;
            failed.Start((_, _) => throw new InvalidOperationException("listener failed"));
            var healthyCalls = 0;
            healthy.Start((_, _) =>
            {
                Interlocked.Increment(ref healthyCalls);
                return Task.CompletedTask;
            });
            await SetValue(session, 1);
            var end = await failed.Closed;
            Equal(WatchEndKind.ListenerError, end.Kind, "kind");
            Equal("listener failed", ((Exception)end.Error!).Message, "error");
            await Flush(session);
            Equal(1, Volatile.Read(ref healthyCalls), "healthy calls");
            await healthy.StopAsync();
        });

        yield return Case(prefix + "hydrates-migration-without-writing-and-observes-the-later-exact-edit", async () =>
        {
            var old = Documents.DefineSessionDoc("watch.migration", 1, () => O("{\"value\":3}"), checkpointWhen: (_, _, _) => false);
            var current = Documents.DefineSessionDoc("watch.migration", 2, () => O("{\"value\":0,\"migrated\":false}"),
                checkpointWhen: (_, _, _) => false,
                migrate: (value, _) => Obj(("value", value["value"]), ("migrated", true)));
            var (storage, session, _) = Open(storageFactory);
            await session.CommitAsync(tx => tx.DocAsync(old), Context);
            await session.UnloadDocumentsAsync();
            var commits = storage.CommitCount;
            var watch = (await session.WatchDocAsync(current, Context))!;
            SameJson("{\"value\":3,\"migrated\":true}", watch.Value, "migrated");
            Equal(commits, storage.CommitCount, "commits");
            var values = new List<double>();
            watch.Start((call, _) =>
            {
                lock (values) values.Add(ValueOf(Change(call).Value));
                return Task.CompletedTask;
            });
            await session.CommitAsync(tx => tx.DocAsync(current), Context);
            await Flush(session);
            lock (values) Equal(0, values.Count, "no frame for the version base");
            await session.CommitAsync(async tx => (await tx.DocAsync(current))["value"] = 4, Context);
            await Flush(session);
            lock (values) Seq([4.0], values, "values");
            await watch.StopAsync();
        });

        yield return Case(prefix + "can-replay-every-delivered-exact-operation-batch-from-the-acquisition-revision", async () =>
        {
            var (_, session, _) = await CreateState();
            var watch = (await session.WatchDocAsync(stateDoc, Context))!;
            JsonValue? replica = watch.Value;
            watch.Start((call, _) =>
            {
                var (value, ops, _) = Change(call);
                replica = DeltaApply.Apply(replica, ops);
                Check(JsonValue.DeepEquals(replica, value), "replayed revision");
                return Task.CompletedTask;
            });
            await session.CommitAsync(async tx =>
            {
                var state = await tx.DocAsync(stateDoc);
                state["value"] = 3;
                state.GetArray("items").Splice(0, 1);
            }, Context);
            await Flush(session);
            Check(JsonValue.DeepEquals(replica, watch.Value), "replica");
            await watch.StopAsync();
        });

        yield return Case(prefix + "continues-after-the-tracker-cache-unloads", async () =>
        {
            var (storage, session, _) = await CreateState();
            var watch = (await session.WatchDocAsync(stateDoc, Context))!;
            var baseline = watch.Value;
            var reads = storage.DocumentReadCount;
            await session.UnloadDocumentsAsync();
            var reloaded = await session.SnapshotAsync(stateDoc, Context);
            Check(JsonValue.DeepEquals(reloaded, baseline), "equal");
            Check(storage.DocumentReadCount > reads, "read from storage");
            watch.Start((_, _) => Task.CompletedTask);
            await SetValue(session, 7);
            await Flush(session);
            Equal(7.0, ValueOf(watch.Value), "value");
            await watch.StopAsync();
        });

        yield return Case(prefix + "ignores-the-same-kind-in-another-conversation-and-under-another-family-key", async () =>
        {
            var notesDoc = Documents.DefineConversationDoc("watch.scoped-notes", 1, DocumentFork.Initial, () => O("{\"text\":\"\"}"),
                checkpointWhen: (_, _, _) => false);
            var membersDoc = Documents.DefineSessionDocFamily("watch.members", 1, _ => O("{\"text\":\"\"}"), checkpointWhen: (_, _, _) => false);
            var (_, session, _) = Open(storageFactory);
            var watched = await CreateConversation(session);
            var other = await CreateConversation(session);
            await session.CommitAsync(async tx =>
            {
                (await tx.DocAsync(notesDoc, watched))["text"] = "a";
                (await tx.DocAsync(membersDoc, "watched", JsonValue.Null))["text"] = "a";
            }, Context);
            var notes = (await session.WatchDocAsync(notesDoc, watched, Context))!;
            var member = (await session.WatchDocAsync(membersDoc, "watched", Context))!;
            var changes = new List<WatchCall<JsonObjectValue>>();
            notes.Start((call, _) =>
            {
                lock (changes) changes.Add(call);
                return Task.CompletedTask;
            });
            member.Start((call, _) =>
            {
                lock (changes) changes.Add(call);
                return Task.CompletedTask;
            });
            // The same kinds at other addresses: created, changed, retired, and created again.
            await session.CommitAsync(async tx =>
            {
                (await tx.DocAsync(notesDoc, other))["text"] = "other";
                (await tx.DocAsync(membersDoc, "other", JsonValue.Null))["text"] = "other";
            }, Context);
            await session.CommitAsync(async tx =>
            {
                await tx.RetireDocAsync(notesDoc, other);
                await tx.RetireDocAsync(membersDoc, "other");
            }, Context);
            await session.CommitAsync(async tx =>
            {
                (await tx.DocAsync(notesDoc, other))["text"] = "again";
                (await tx.DocAsync(membersDoc, "other", JsonValue.Null))["text"] = "again";
            }, Context);
            await Flush(session);
            lock (changes) Equal(0, changes.Count, "calls");
            Equal(2, session.DocumentCacheSizes.Attached, "attached");
            await notes.StopAsync();
            await member.StopAsync();
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "takes-the-version-of-a-recreation-written-by-a-newer-definition-then-its-ops", async () =>
        {
            var old = Documents.DefineSessionDoc("watch.recreated-version", 1, () => O("{\"value\":1}"), checkpointWhen: (_, _, _) => false);
            var current = Documents.DefineSessionDoc("watch.recreated-version", 2, () => O("{\"value\":0,\"migrated\":false}"),
                checkpointWhen: (_, _, _) => false,
                migrate: (value, _) => Obj(("value", value["value"]), ("migrated", true)));
            var (_, session, _) = Open(storageFactory);
            await session.CommitAsync(tx => tx.DocAsync(old), Context);
            var watch = (await session.WatchDocAsync(old, Context))!;
            var changes = new List<WatchValueCall<JsonObjectValue>>();
            watch.Start((call, _) =>
            {
                lock (changes) changes.Add(Change(call));
                return Task.CompletedTask;
            });
            await session.CommitAsync(async tx =>
            {
                await tx.RetireDocAsync(current);
                (await tx.DocAsync(current))["value"] = 5;
            }, Context);
            await Flush(session);
            await session.CommitAsync(async tx => (await tx.DocAsync(current))["value"] = 6, Context);
            await Flush(session);
            lock (changes)
            {
                Seq(["{\"value\":5,\"migrated\":false}", "{\"value\":6,\"migrated\":false}"], changes.Select(change => change.Value.ToJson()),
                    "values");
                Seq(["[[\"r\",{\"value\":5,\"migrated\":false}]]", "[[\"s\",[\"value\"],6]]"], changes.Select(change => OpsJson(change.Ops)),
                    "ops");
                Seq([true, false], changes.Select(change => change.Retired), "retired");
            }
            await watch.StopAsync();
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-no-earlier-incarnation-alive-after-a-retirement-and-a-recreation-while-it-goes-on", async () =>
        {
            // Retention 0 evicts the retired incarnation at once; the default keeps the cache until it is unloaded.
            foreach (var (retention, drop) in new[] { (0.0, false), (600_000.0, true) })
            {
                var session = new Session(storageFactory(), null, () => retention);
                var (watch, first) = await FollowRecreation(session, stateDoc, drop);
                await CollectGarbage();
                Equal(2.0, ValueOf(watch.Value), "value");
                SameJson("{\"label\":\"stable\"}", watch.Value["retained"], "retained");
                Check(!first.IsAlive, $"the first incarnation is gone (retention {retention})");
                await watch.StopAsync();
                await session.CloseAsync(Context);
            }
        });

        yield return Case(prefix + "keeps-a-watch-of-a-retired-conversation-document-until-it-is-stopped-then-nothing-of-it", async () =>
        {
            var notesDoc = Documents.DefineConversationDoc("watch.notes", 1, DocumentFork.Initial, () => O("{\"text\":\"\"}"),
                checkpointWhen: (_, _, _) => false);
            var (_, session, _) = Open(storageFactory);
            var conversationId = await CreateConversation(session);
            // A watch follows its address, so a forgotten one stays until it is stopped or the Session ends.
            var forgotten = await RetiredNotesWatch(session, notesDoc, conversationId, stop: false);
            await CollectGarbage();
            Check(forgotten.Watch.IsAlive, "a forgotten watch stays");
            await ((IWatchHandle<JsonObjectValue>)forgotten.Watch.Target!).StopAsync();

            var stopped = await RetiredNotesWatch(session, notesDoc, conversationId, stop: true);
            await CollectGarbage();
            Check(!stopped.Watch.IsAlive, "the stopped watch is gone");
            Check(!stopped.Listener.IsAlive, "its listener is gone");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "retains-neither-its-session-nor-its-listener-once-stopped-while-still-referenced", async () =>
        {
            // Control: a running watch keeps its Session, so the collection below is real.
            var running = await UnreferencedWatch(storageFactory, stateDoc, stop: false);
            await CollectGarbage();
            Check(running.Session.IsAlive, "a running watch keeps its Session");
            await running.Watch.StopAsync();

            var stopped = await UnreferencedWatch(storageFactory, stateDoc, stop: true);
            await CollectGarbage();
            Check(!stopped.Session.IsAlive, "the stopped watch released its Session");
            Check(!stopped.Listener.IsAlive, "the stopped watch released its listener");
            // What it delivered last stays readable.
            Equal(1.0, ValueOf(stopped.Watch.Value), "last value");
            Equal(WatchEnd.Stopped, await stopped.Watch.Closed, "stopped");
        });
    }

    /// <summary>A started watch of a Session nothing else references (upstream's <c>acquire</c>), after one delivered commit; stopped first
    /// when <paramref name="stop"/> says so. The documents stay and no timer is armed, so nothing but the watch can keep the Session alive.
    /// The listener captures a marker, so it is its own delegate instance.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(IWatchHandle<JsonObjectValue> Watch, WeakReference Session, WeakReference Listener)> UnreferencedWatch(
        Func<IStorage> storageFactory, SessionDocToken stateDoc, bool stop)
    {
        var session = new Session(storageFactory(), null, () => double.PositiveInfinity, setTimer: (_, _, _) => () => { });
        await session.CommitAsync(tx => tx.DocAsync(stateDoc), Context);
        var watch = (await session.WatchDocAsync(stateDoc, Context))!;
        var marker = new object();
        Func<WatchCall<JsonObjectValue>, DurableContext, Task> listener = (_, _) =>
        {
            GC.KeepAlive(marker);
            return Task.CompletedTask;
        };
        watch.Start(listener);
        await session.CommitAsync(async tx => (await tx.DocAsync(stateDoc))["value"] = 1, Context);
        await Flush(session);
        if (stop) await watch.StopAsync();
        return (watch, new WeakReference(session), new WeakReference(listener));
    }

    /// <summary>Upstream's <c>follow</c>: watches the document, retires and recreates it, and delivers the new value; <paramref name="drop"/>
    /// then unloads the cache. <c>First</c> refers to the first incarnation's <c>retained</c> object.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(IWatchHandle<JsonObjectValue> Watch, WeakReference First)> FollowRecreation(Session session,
        SessionDocToken stateDoc, bool drop)
    {
        await session.CommitAsync(async tx => (await tx.DocAsync(stateDoc))["retained"] = O("{\"label\":\"first incarnation\"}"), Context);
        var watch = (await session.WatchDocAsync(stateDoc, Context))!;
        var first = new WeakReference(watch.Value["retained"]);
        watch.Start((_, _) => Task.CompletedTask);
        await session.CommitAsync(async tx =>
        {
            await tx.RetireDocAsync(stateDoc);
            (await tx.DocAsync(stateDoc))["value"] = 2;
        }, Context);
        await Flush(session);
        if (drop) await session.UnloadDocumentsAsync();
        return (watch, first);
    }

    /// <summary>Upstream's <c>acquire</c> in "keeps a watch of a retired conversation document": a started watch of a document that is then
    /// retired, stopped first when <paramref name="stop"/> says so. The listener captures a marker, so it is its own delegate instance.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference Watch, WeakReference Listener)> RetiredNotesWatch(Session session, ConversationDocToken notesDoc,
        ConversationId conversationId, bool stop)
    {
        await session.CommitAsync(async tx => (await tx.DocAsync(notesDoc, conversationId))["text"] = "kept", Context);
        var watch = (await session.WatchDocAsync(notesDoc, conversationId, Context))!;
        var marker = new object();
        Func<WatchCall<JsonObjectValue>, DurableContext, Task> listener = (_, _) =>
        {
            GC.KeepAlive(marker);
            return Task.CompletedTask;
        };
        watch.Start(listener);
        await session.CommitAsync(tx => tx.RetireDocAsync(notesDoc, conversationId), Context);
        await Flush(session);
        if (stop) await watch.StopAsync();
        return (new WeakReference(watch), new WeakReference(listener));
    }

    /// <summary>Upstream's <c>collect()</c>: rounds of a timer tick (its <c>setTimeout(resolve, 0)</c>) and a full collection.</summary>
    private static async Task CollectGarbage()
    {
        for (var round = 0; round < 5; round++)
        {
            await Task.Delay(1);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }
}
