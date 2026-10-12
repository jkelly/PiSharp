// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/session-watches.test.ts. Verbatim apart from the adaptations in
// SessionSupport.cs and these:
// - "never invokes the listener inline": a watch started from outside the line delivers in its own line job on the thread pool, so the
//   listener checks it is not running inside Start on the starting thread, and upstream's "no delivery yet right after start" becomes that
//   same check (the pool may already have delivered).
// - AbortController/withAbortSignal is a CancellationTokenSource through DurableContext.WithCancellation; withCancel is WithCancel.
// - applyImmutable is DeltaApply.Apply (immutable application of a batch).
// - A reload after unloading is shown by the storage read, not a new object (an immutable stored value may be handed out again).
using System.Collections.Immutable;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using static Program;

internal static partial class SessionTests
{
    private static IEnumerable<(string, Func<Task>)> Watches(string prefix, Func<IStorage> storageFactory)
    {
        var stateDoc = Documents.DefineSessionDoc("watch.state", 1, () => O("{\"value\":0,\"items\":[\"a\",\"b\"],\"retained\":{\"label\":\"stable\"}}"));
        static double ValueOf(JsonObjectValue? value) => value is null ? -1 : N(value["value"]);
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

        yield return Case(prefix + "keeps-the-acquisition-revision-until-start-and-delivers-exact-committed-frames", async () =>
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
            var deliveries = new List<(JsonObjectValue? Value, ImmutableArray<Op> Ops)>();
            watch.Start((value, ops, _) =>
            {
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
                Seq([1.0, 2.0], deliveries.Select(delivery => ValueOf(delivery.Value)), "values");
                Same(firstPublished.Value, deliveries[0].Value, "first value");
                SameOps(firstPublished.Ops, deliveries[0].Ops, "first ops");
                Same(secondPublished.Value, deliveries[1].Value, "second value");
                SameOps(secondPublished.Ops, deliveries[1].Ops, "second ops");
            }
            Equal(0.0, ValueOf(initial), "initial unchanged");
            await watch.StopAsync();
        });

        yield return Case(prefix + "serializes-callbacks-and-buffers-exact-frames-committed-while-one-is-in-flight", async () =>
        {
            var (_, session, _) = await CreateState();
            var watch = (await session.WatchDocAsync(stateDoc, Context))!;
            var entered = new Deferred(inline: false);
            var release = new Deferred();
            var values = new List<double>();
            int active = 0, maxActive = 0;
            watch.Start(async (value, _, _) =>
            {
                var now = Interlocked.Increment(ref active);
                int count;
                lock (values)
                {
                    maxActive = Math.Max(maxActive, now);
                    values.Add(ValueOf(value));
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
            lock (values) Seq(Enumerable.Range(1, 20).Select(value => (double)value), values, "every frame in order");
            await watch.StopAsync();
        });

        yield return Case(prefix + "allows-a-listener-to-initiate-a-later-session-commit", async () =>
        {
            var (_, session, _) = await CreateState();
            var watch = (await session.WatchDocAsync(stateDoc, Context))!;
            var completed = new Deferred(inline: false);
            var values = new List<double>();
            watch.Start(async (value, _, _) =>
            {
                var current = ValueOf(value);
                lock (values) values.Add(current);
                if (current == 1) await SetValue(session, 2);
                else completed.Resolve();
            });
            await SetValue(session, 1);
            await completed.Task;
            lock (values) Seq([1.0, 2.0], values, "values");
            await watch.StopAsync();
        });

        yield return Case(prefix + "collapses-101-pending-commits-to-one-root-replacement", async () =>
        {
            var (_, session, _) = await CreateState();
            var watch = (await session.WatchDocAsync(stateDoc, Context))!;
            for (var value = 1; value <= 101; value++) await SetValue(session, value);
            var deliveries = new List<(double Value, string Ops)>();
            watch.Start((value, ops, _) =>
            {
                lock (deliveries) deliveries.Add((ValueOf(value), OpsJson(ops)));
                return Task.CompletedTask;
            });
            await Flush(session);
            lock (deliveries)
            {
                Equal(1, deliveries.Count, "one delivery");
                Equal(101.0, deliveries[0].Value, "newest value");
                Equal("[[\"r\"," + watch.Value!.ToJson() + "]]", deliveries[0].Ops, "root replacement");
            }
            await watch.StopAsync();
        });

        yield return Case(prefix + "never-folds-the-in-flight-frame-into-an-overflow-reset", async () =>
        {
            var (_, session, _) = await CreateState();
            var watch = (await session.WatchDocAsync(stateDoc, Context))!;
            var entered = new Deferred(inline: false);
            var release = new Deferred();
            var deliveries = new List<(double Value, string Ops)>();
            watch.Start(async (value, ops, _) =>
            {
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
                Equal((102.0, "[[\"r\"," + watch.Value!.ToJson() + "]]"), deliveries[1], "overflow reset");
                Equal(2, deliveries.Count, "deliveries");
            }
            await watch.StopAsync();
        });

        yield return Case(prefix + "folds-retirement-into-an-overflow-reset-and-then-closes", async () =>
        {
            var (_, session, _) = await CreateState();
            var watch = (await session.WatchDocAsync(stateDoc, Context))!;
            for (var value = 1; value <= 100; value++) await SetValue(session, value);
            await session.CommitAsync(tx => tx.RetireDocAsync(stateDoc), Context);
            var deliveries = new List<(JsonObjectValue? Value, string Ops)>();
            watch.Start((value, ops, _) =>
            {
                lock (deliveries) deliveries.Add((value, OpsJson(ops)));
                return Task.CompletedTask;
            });
            Equal(WatchEnd.Retired, await watch.Closed, "closed");
            lock (deliveries)
            {
                Equal(1, deliveries.Count, "one delivery");
                Check(deliveries[0].Value is null && deliveries[0].Ops == "[[\"r\",null]]", "retirement reset");
            }
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
            watch.Start((value, ops, _) =>
            {
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
            watch.Start(async (_, _, delivered) =>
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
            watch.Start((value, _, _) =>
            {
                delivered = value;
                return Task.CompletedTask;
            });
            await SetValue(session, 1);
            await Flush(session);
            Same(watch.Value, delivered, "delivered is the current value");
            Check(!ReferenceEquals(delivered, initial), "a new revision");
            Same(initial!["retained"], delivered!["retained"], "retained subtree shared");
            Equal(0.0, ValueOf(initial), "initial unchanged");
            await watch.StopAsync();
        });

        yield return Case(prefix + "delivers-retirement-and-does-not-follow-recreation", async () =>
        {
            var (_, session, _) = await CreateState();
            var oldWatch = (await session.WatchDocAsync(stateDoc, Context))!;
            var values = new List<JsonObjectValue?>();
            oldWatch.Start((value, _, _) =>
            {
                lock (values) values.Add(value);
                return Task.CompletedTask;
            });
            await session.CommitAsync(async tx =>
            {
                await tx.RetireDocAsync(stateDoc);
                (await tx.DocAsync(stateDoc))["value"] = 10;
            }, Context);
            Equal(WatchEnd.Retired, await oldWatch.Closed, "closed");
            lock (values) Check(values is [null], "retirement delivered");
            Check(oldWatch.Value is null, "value null");

            var replacement = (await session.WatchDocAsync(stateDoc, Context))!;
            Equal(10.0, ValueOf(replacement.Value), "replacement");
            await SetValue(session, 11);
            await Flush(session);
            Check(oldWatch.Value is null, "old watch stays retired");
            await replacement.StopAsync();
        });

        yield return Case(prefix + "session-close-discards-retirement-buffered-before-start", async () =>
        {
            var (_, session, _) = await CreateState();
            var watch = (await session.WatchDocAsync(stateDoc, Context))!;
            var baseline = watch.Value;
            await session.CommitAsync(tx => tx.RetireDocAsync(stateDoc), Context);
            await session.CloseAsync(Context);
            Equal(WatchEnd.SessionClosed, await watch.Closed, "closed");
            Same(baseline, watch.Value, "baseline kept");
            Throws(() => watch.Start((_, _, _) => Task.CompletedTask), "stopped", "late start");
        });

        yield return Case(prefix + "session-close-discards-retirement-behind-an-in-flight-callback", async () =>
        {
            var (_, session, _) = await CreateState();
            var watch = (await session.WatchDocAsync(stateDoc, Context))!;
            var entered = new Deferred(inline: false);
            var release = new Deferred();
            var values = new List<double?>();
            watch.Start(async (value, _, _) =>
            {
                lock (values) values.Add(value is null ? null : ValueOf(value));
                entered.Resolve();
                await release.Task;
            });
            await SetValue(session, 1);
            await entered.Task;
            await session.CommitAsync(tx => tx.RetireDocAsync(stateDoc), Context);
            await session.CloseAsync(Context);
            Equal(WatchEnd.SessionClosed, await watch.Closed, "closed");
            release.Resolve();
            await Flush(session);
            lock (values) Seq([(double?)1.0], values, "values");
        });

        yield return Case(prefix + "supports-idempotent-stop-and-rejects-repeated-or-late-start", async () =>
        {
            var (_, session, _) = await CreateState();
            var started = (await session.WatchDocAsync(stateDoc, Context))!;
            started.Start((_, _, _) => Task.CompletedTask);
            Throws(() => started.Start((_, _, _) => Task.CompletedTask), "already started", "second start");
            var first = started.StopAsync();
            var second = started.StopAsync();
            Same(first, second, "the same terminal task");
            Equal(WatchEnd.Stopped, await first, "stopped");

            var stopped = (await session.WatchDocAsync(stateDoc, Context))!;
            await stopped.StopAsync();
            Throws(() => stopped.Start((_, _, _) => Task.CompletedTask), "stopped", "late start");
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
            watch.Start(async (_, _, delivered) =>
            {
                deliveryContext = delivered;
                entered.Resolve();
                await release.Task;
            });
            await SetValue(session, 1);
            await entered.Task;
            child.Cancel();
            Equal(WatchEnd.Cancelled, await watch.Closed, "cancelled");
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
            watch.Start(async (_, _, delivered) =>
            {
                deliveryContext = delivered;
                entered.Resolve();
                await release.Task;
            });
            await SetValue(session, 1);
            await entered.Task;
            await session.CloseAsync(Context);
            Check(!deliveryContext!.HasCancellation, "no cancellation");
            Equal(WatchEnd.SessionClosed, await watch.Closed, "closed");
            release.Resolve();
        });

        yield return Case(prefix + "settles-listener-failure-on-only-the-affected-watch", async () =>
        {
            var (_, session, _) = await CreateState();
            var failed = (await session.WatchDocAsync(stateDoc, Context))!;
            var healthy = (await session.WatchDocAsync(stateDoc, Context))!;
            failed.Start((_, _, _) => throw new InvalidOperationException("listener failed"));
            var healthyCalls = 0;
            healthy.Start((_, _, _) =>
            {
                Interlocked.Increment(ref healthyCalls);
                return Task.CompletedTask;
            });
            await SetValue(session, 1);
            var end = await failed.Closed;
            Equal(WatchEndReason.ListenerError, end.Reason, "reason");
            Equal("listener failed", end.Error!.Message, "error");
            await Flush(session);
            Equal(1, Volatile.Read(ref healthyCalls), "healthy calls");
            await healthy.StopAsync();
        });

        yield return Case(prefix + "hydrates-migration-without-writing-and-observes-the-later-exact-edit", async () =>
        {
            var old = Documents.DefineSessionDoc("watch.migration", 1, () => O("{\"value\":3}"));
            var current = Documents.DefineSessionDoc("watch.migration", 2, () => O("{\"value\":0,\"migrated\":false}"),
                migrate: (value, _) => Obj(("value", value["value"]), ("migrated", true)));
            var (storage, session, _) = Open(storageFactory);
            await session.CommitAsync(tx => tx.DocAsync(old), Context);
            await session.UnloadDocumentsAsync();
            var commits = storage.CommitCount;
            var watch = (await session.WatchDocAsync(current, Context))!;
            SameJson("{\"value\":3,\"migrated\":true}", watch.Value, "migrated");
            Equal(commits, storage.CommitCount, "commits");
            var values = new List<double>();
            watch.Start((value, _, _) =>
            {
                lock (values) values.Add(ValueOf(value));
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
            watch.Start((value, ops, _) =>
            {
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
            watch.Start((_, _, _) => Task.CompletedTask);
            await SetValue(session, 7);
            await Flush(session);
            Equal(7.0, ValueOf(watch.Value), "value");
            await watch.StopAsync();
        });
    }
}
