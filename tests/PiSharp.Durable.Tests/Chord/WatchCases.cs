using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/watch.test.ts. Adaptations:
// - Upstream delivers in a microtask and flush() is setTimeout(0). Each watch here takes a WatchJobs queue as its scheduler, and
//   jobs.Flush() runs what was posted, and what that posts, until nothing is left. Gates are TaskCompletionSources whose continuations run
//   synchronously, so the state upstream reaches after `gate.resolve(); await flush()` holds right after SetResult() and Flush().
// - vi.useFakeTimers() is WatchClock: Delay(ms) and Advance(ms), which fires due timers in order and flushes between them.
// - A call is compared through its JSON; toBe is ReferenceEquals, and for ops the same underlying array (ImmutableArray's ==). A pending
//   `closed` (endOf) is Closed.IsCompleted.
// - A context value and an abort signal are DurableContext.With and WithCancellation (a CancellationToken); AbortSignal.abort() is an
//   already-cancelled token.
// - "wraps a thrown non-Error in an Error": .NET throws only exceptions, so the counterpart is a listener whose task is cancelled: the
//   watch ends listener_error with the cancellation exception.
// - "hands a listener plain data only": structuredClone and JSON round trips become the value call's public properties (exactly Kind,
//   Value, Ops and Retired, none a context), its record copy and its JSON form.
// - The GC cases use WeakReference and GC.Collect in place of WeakRef and --expose-gc, building what they drop in methods the JIT does not
//   inline; a listener captures a marker so it is its own delegate instance (a capture-free lambda is cached for good).
// The last two cases are native: the default thread-pool scheduler, and a producer thread racing a slow listener.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> ChordWatchCases() =>
    [
        // ─── Watch delivery ───
        Case("chord.watch.delivery.keeps-the-acquisition-value-until-start-then-delivers-the-frames-since-in-one-call", () =>
        {
            var jobs = new WatchJobs();
            var (watch, _) = RecordedWatch(jobs, J("{\"n\":0,\"text\":\"\"}"));
            watch.Advance(WatchFrame("{\"n\":1,\"text\":\"\"}", "[[\"s\",[\"n\"],1]]"), Bg);
            watch.Advance(WatchFrame("{\"n\":2,\"text\":\"a\"}", "[[\"s\",[\"n\"],2],[\"a\",[\"text\"],\"a\"]]"), Bg);
            watch.Advance(WatchFrame("{\"n\":2,\"text\":\"ab\"}", "[[\"a\",[\"text\"],\"b\"]]"), Bg);
            jobs.Flush();
            Same("{\"n\":0,\"text\":\"\"}", watch.Value, "acquisition value");

            var frames = new List<string>();
            watch.Start(OnValue(call => frames.Add(FrameJson(call))));
            Equal(0, frames.Count, "not inline");
            jobs.Flush();
            WatchFrames(["{\"value\":{\"n\":2,\"text\":\"ab\"},\"ops\":[[\"s\",[\"n\"],2],[\"a\",[\"text\"],\"ab\"]]}"], frames, "frames");
            Same("{\"n\":2,\"text\":\"ab\"}", watch.Value, "delivered value");
        }),
        Case("chord.watch.delivery.passes-a-frames-ops-on-by-identity-to-a-listener-that-keeps-up", () =>
        {
            var jobs = new WatchJobs();
            var (watch, _) = RecordedWatch(jobs, 0);
            var delivered = new List<ImmutableArray<Op>>();
            watch.Start(OnValue(call => delivered.Add(call.Ops)));
            var first = Ops("[[\"r\",1]]");
            var second = Ops("[[\"r\",2]]");
            watch.Advance(new WatchValueCall<JsonValue>(1, first), Bg);
            jobs.Flush();
            watch.Advance(new WatchValueCall<JsonValue>(2, second), Bg);
            jobs.Flush();
            Equal(2, delivered.Count, "calls");
            Check(delivered[0] == first, "first ops by identity");
            Check(delivered[1] == second, "second ops by identity");
        }),
        Case("chord.watch.delivery.never-invokes-the-listener-inline", () =>
        {
            var jobs = new WatchJobs();
            var (watch, _, frames) = CollectingWatch(jobs, 0);
            watch.Advance(WatchFrame("1", "[[\"r\",1]]"), Bg);
            Equal(0, frames.Count, "not inline");
            jobs.Flush();
            WatchFrames(["{\"value\":1,\"ops\":[[\"r\",1]]}"], frames, "frames");
        }),
        Case("chord.watch.delivery.awaits-each-callback-and-folds-frames-advanced-while-one-is-in-flight-into-the-next-call", () =>
        {
            var jobs = new WatchJobs();
            var (watch, _) = RecordedWatch(jobs, J("{\"text\":\"\"}"));
            var gate = new TaskCompletionSource();
            var calls = new List<string>();
            var frames = new List<string>();
            watch.Start(OnValueAsync(async (call, _) =>
            {
                calls.Add("start " + call.Value.ToJson());
                frames.Add(FrameJson(call));
                if (frames.Count == 1) await gate.Task;
                calls.Add("end " + call.Value.ToJson());
            }));
            watch.Advance(WatchFrame("{\"text\":\"a\"}", "[[\"a\",[\"text\"],\"a\"]]"), Bg);
            jobs.Flush();
            watch.Advance(WatchFrame("{\"text\":\"ab\"}", "[[\"a\",[\"text\"],\"b\"]]"), Bg);
            watch.Advance(WatchFrame("{\"text\":\"abc\"}", "[[\"a\",[\"text\"],\"c\"]]"), Bg);
            jobs.Flush();
            Seq(["start {\"text\":\"a\"}"], calls, "in flight");
            gate.SetResult();
            jobs.Flush();
            Seq(["start {\"text\":\"a\"}", "end {\"text\":\"a\"}", "start {\"text\":\"abc\"}", "end {\"text\":\"abc\"}"], calls, "calls");
            Equal(J("{\"value\":{\"text\":\"abc\"},\"ops\":[[\"a\",[\"text\"],\"bc\"]]}").ToJson(), frames[1], "folded frame");
        }),
        Case("chord.watch.delivery.delivers-the-newest-frames-context-without-its-abort-signal", () =>
        {
            var jobs = new WatchJobs();
            var (watch, _) = RecordedWatch(jobs, 0);
            var key = new ContextKey<string>("frame");
            using var controller = new CancellationTokenSource();
            var delivered = new List<DurableContext>();
            watch.Start((_, deliveryContext) =>
            {
                delivered.Add(deliveryContext);
                return Task.CompletedTask;
            });
            watch.Advance(WatchFrame("1", "[[\"r\",1]]"), Bg.With(key, "first"));
            watch.Advance(WatchFrame("2", "[[\"r\",2]]"), Bg.WithCancellation(controller.Token).With(key, "second"));
            jobs.Flush();
            Equal(1, delivered.Count, "one call");
            Equal("second", delivered[0].Get(key), "newest frame's context");
            Check(!delivered[0].HasCancellation, "without its abort signal");
        }),

        // ─── Watch with a slow listener ───
        Case("chord.watch.slow-listener.gets-one-call-per-batch-whose-ops-turn-the-previous-delivered-value-into-the-newest", async () =>
        {
            var jobs = new WatchJobs();
            var clock = new WatchClock(jobs);
            var tracker = Tracker.Track(J("{\"entries\":[],\"tokens\":0}"));
            var (watch, _) = RecordedWatch(jobs, tracker.Value);
            void Commit(Action<DraftObject> edit)
            {
                var change = tracker.BeginChange();
                edit(change.State.AsObject());
                var prepared = change.Prepare();
                tracker.Adopt(prepared);
                watch.Advance(new WatchValueCall<JsonValue>(tracker.Value, prepared.Ops), Bg);
            }
            var replica = tracker.Value;
            var calls = new List<(int Commits, int Ops)>();
            var commits = 0;
            watch.Start(OnValueAsync(async (call, _) =>
            {
                replica = DeltaApply.Apply(replica, call.Ops);
                Check(JsonValue.DeepEquals(replica, call.Value), "the replica equals the delivered value");
                calls.Add((commits, call.Ops.Length));
                // A listener taking a second per call, as a slow client would.
                await clock.Delay(1000);
            }));
            // Ten seconds of a streamed reply at 60 tokens per second, a new entry every second.
            for (var tick = 0; tick < 600; tick++)
            {
                if (tick % 60 == 0) Commit(draft => draft.GetArray("entries").Add(J("{\"text\":\"\",\"status\":\"streaming\"}")));
                Commit(draft =>
                {
                    var entries = draft.GetArray("entries");
                    entries.GetObject(entries.Count - 1).AppendString("text", "tok ");
                    draft.Set("tokens", ((JsonNumberValue)draft.Get("tokens")!).Value + 1);
                });
                commits++;
                clock.Advance(1000.0 / 60);
            }
            clock.Advance(2000);
            Check(JsonValue.DeepEquals(replica, tracker.Value), "the replica reaches the newest value");
            // One call per second, each carrying at most one op per path written since the previous one.
            Check(calls.Count is >= 10 and <= 12, $"calls: {calls.Count}");
            foreach (var call in calls) Check(call.Ops <= 5, $"ops per call: {call.Ops} after {call.Commits} commits");
            await watch.StopAsync();
        }),

        // ─── Watch overflow ───
        Case("chord.watch.overflow.keeps-max-pending-ops-after-folding-and-replaces-the-root-past-them", () =>
        {
            var jobs = new WatchJobs();
            var exact = CollectingWatch(jobs, J("{\"k\":[]}"));
            AdvanceDistinct(exact.Watch, PendingOps.MaxPendingOps);
            jobs.Flush();
            Equal(1, exact.Frames.Count, "exact: one call");
            Equal(PendingOps.MaxPendingOps, ((JsonArrayValue)At(J(exact.Frames[0]), "ops")).Count, "exact: every op");

            var over = CollectingWatch(jobs, J("{\"k\":[]}"));
            var newest = AdvanceDistinct(over.Watch, PendingOps.MaxPendingOps + 1);
            jobs.Flush();
            WatchFrames([FrameJson(newest, [new ReplaceOp(newest)])], over.Frames, "over: root replacement");
        }),
        Case("chord.watch.overflow.never-folds-the-in-flight-call-into-an-overflow", () =>
        {
            var jobs = new WatchJobs();
            var (watch, _) = RecordedWatch(jobs, J("{\"k\":[]}"));
            var gate = new TaskCompletionSource();
            var frames = new List<string>();
            watch.Start(OnValueAsync(async (call, _) =>
            {
                frames.Add(FrameJson(call));
                if (frames.Count == 1) await gate.Task;
            }));
            watch.Advance(WatchFrame("{\"k\":[],\"first\":true}", "[[\"s\",[\"first\"],true]]"), Bg);
            jobs.Flush();
            var newest = AdvanceDistinct(watch, PendingOps.MaxPendingOps + 1);
            gate.SetResult();
            jobs.Flush();
            WatchFrames(["{\"value\":{\"k\":[],\"first\":true},\"ops\":[[\"s\",[\"first\"],true]]}", FrameJson(newest, [new ReplaceOp(newest)])], frames, "frames");
        }),
        Case("chord.watch.overflow.starts-folding-again-after-an-overflow-was-delivered", () =>
        {
            var jobs = new WatchJobs();
            var (watch, _, frames) = CollectingWatch(jobs, J("{\"k\":[]}"));
            AdvanceDistinct(watch, PendingOps.MaxPendingOps + 1);
            jobs.Flush();
            watch.Advance(WatchFrame("{\"n\":1}", "[[\"r\",{\"n\":1}]]"), Bg);
            watch.Advance(WatchFrame("{\"n\":2}", "[[\"s\",[\"n\"],2]]"), Bg);
            jobs.Flush();
            Equal(J("{\"value\":{\"n\":2},\"ops\":[[\"r\",{\"n\":1}],[\"s\",[\"n\"],2]]}").ToJson(), frames[^1], "folded after the overflow");
        }),

        // ─── Watch retirement ───
        Case("chord.watch.retirement.reports-a-retirement-in-the-next-call-with-the-value-the-source-gives-and-goes-on", async () =>
        {
            var jobs = new WatchJobs();
            var (watch, recorded, frames) = CollectingWatch(jobs, 1);
            watch.Advance(WatchFrame("2", "[[\"r\",2]]"), Bg);
            jobs.Flush();
            watch.Advance(WatchRetirement("2"), Bg);
            jobs.Flush();
            watch.Advance(WatchFrame("3", "[[\"r\",3]]"), Bg);
            jobs.Flush();
            WatchFrames(["{\"value\":2,\"ops\":[[\"r\",2]]}", "{\"value\":2,\"ops\":[],\"retired\":true}", "{\"value\":3,\"ops\":[[\"r\",3]]}"], frames, "frames");
            Same("3", watch.Value, "value");
            Check(!watch.Closed.IsCompleted, "still open");
            Equal(0, recorded.Detached, "not detached");
            await watch.StopAsync();
        }),
        Case("chord.watch.retirement.folds-a-retirement-and-the-frames-around-it-into-one-call-that-reports-it", async () =>
        {
            var jobs = new WatchJobs();
            var (watch, _) = RecordedWatch(jobs, J("{\"n\":0}"));
            watch.Advance(WatchFrame("{\"n\":1}", "[[\"s\",[\"n\"],1]]"), Bg);
            watch.Advance(WatchRetirement("{\"n\":1}"), Bg);
            watch.Advance(WatchFrame("{\"m\":1}", "[[\"r\",{\"m\":1}]]"), Bg);
            var changes = new List<WatchCall<JsonValue>>();
            watch.Start(Recording(changes));
            jobs.Flush();
            WatchCalls(["{\"kind\":\"value\",\"value\":{\"m\":1},\"ops\":[[\"r\",{\"m\":1}]],\"retired\":true}"], changes, "folded retirement");
            watch.Advance(WatchFrame("{\"m\":2}", "[[\"s\",[\"m\"],2]]"), Bg);
            jobs.Flush();
            Equal(J("{\"kind\":\"value\",\"value\":{\"m\":2},\"ops\":[[\"s\",[\"m\"],2]],\"retired\":false}").ToJson(), CallJson(changes[1]), "after it");
            await watch.StopAsync();
        }),
        Case("chord.watch.retirement.keeps-reporting-the-retirement-past-an-overflow", async () =>
        {
            var jobs = new WatchJobs();
            var (watch, _) = RecordedWatch(jobs, J("{\"k\":[]}"));
            watch.Advance(WatchRetirement("{\"k\":[]}"), Bg);
            var newest = AdvanceDistinct(watch, PendingOps.MaxPendingOps + 1);
            var changes = new List<WatchCall<JsonValue>>();
            watch.Start(Recording(changes));
            jobs.Flush();
            Equal(1, changes.Count, "one call");
            var call = (WatchValueCall<JsonValue>)changes[0];
            Check(call.Retired && JsonValue.DeepEquals(call.Value, newest), "retired, newest value");
            Equal(OpsJson([new ReplaceOp(newest)]).ToJson(), OpsJson(call.Ops).ToJson(), "root replacement");
            await watch.StopAsync();
        }),
        Case("chord.watch.retirement.treats-a-null-value-as-a-value-never-as-a-retirement", async () =>
        {
            var jobs = new WatchJobs();
            var (watch, _, frames) = CollectingWatch(jobs, 1);
            watch.Advance(WatchFrame("null", "[[\"r\",null]]"), Bg);
            jobs.Flush();
            watch.Advance(WatchFrame("2", "[[\"r\",2]]"), Bg);
            jobs.Flush();
            WatchFrames(["{\"value\":null,\"ops\":[[\"r\",null]]}", "{\"value\":2,\"ops\":[[\"r\",2]]}"], frames, "frames");
            Check(!watch.Closed.IsCompleted, "still open");
            Equal(WatchEnd.Stopped, await watch.StopAsync(), "stopped");
        }),
        Case("chord.watch.retirement.discards-a-retirement-still-pending-when-the-source-ends", async () =>
        {
            var jobs = new WatchJobs();
            var (watch, _) = RecordedWatch(jobs, 1);
            watch.Advance(WatchRetirement("1"), Bg);
            watch.End(WatchEnd.Ended);
            Equal(WatchEnd.Ended, await watch.Closed, "ended");
            Same("1", watch.Value, "value");
        }),

        // ─── Watch failure calls ───
        Case("chord.watch.failure.delivers-a-failure-as-a-call-stays-open-keeps-the-last-value-and-replaces-the-root-on-recovery", async () =>
        {
            var jobs = new WatchJobs();
            var (watch, recorded, calls) = RecordingWatch(jobs, J("{\"n\":0}"));
            watch.Advance(WatchFrame("{\"n\":1}", "[[\"s\",[\"n\"],1]]"), Bg);
            jobs.Flush();
            var error = new InvalidOperationException("cannot compute");
            watch.Fail(error, Bg);
            jobs.Flush();
            Check(calls[1] is WatchFailedCall<JsonValue> failed && ReferenceEquals(failed.Error, error), "a failed call with the error");
            Same("{\"n\":1}", watch.Value, "the last value");
            Check(!watch.Closed.IsCompleted, "still open");
            Equal(0, recorded.Detached, "not detached");
            watch.Advance(WatchFrame("{\"n\":2}", "[[\"s\",[\"n\"],2]]"), Bg);
            jobs.Flush();
            Equal(J("{\"kind\":\"value\",\"value\":{\"n\":2},\"ops\":[[\"r\",{\"n\":2}]],\"retired\":false}").ToJson(), CallJson(calls[2]), "recovery");
            // Recovered: frames pass on as the source made them again.
            var next = WatchFrame("{\"n\":3}", "[[\"s\",[\"n\"],3]]");
            watch.Advance(next, Bg);
            jobs.Flush();
            Check(ReferenceEquals(next, calls[3]), "passed on as made");
            Same("{\"n\":3}", watch.Value, "value");
            await watch.StopAsync();
        }),
        Case("chord.watch.failure.takes-a-failure-through-advance-as-through-fail", async () =>
        {
            var jobs = new WatchJobs();
            var (watch, _, calls) = RecordingWatch(jobs, 0);
            WatchCall<JsonValue> failure = new WatchFailedCall<JsonValue>("down");
            watch.Advance(failure, Bg);
            jobs.Flush();
            Equal(1, calls.Count, "one call");
            Check(ReferenceEquals(failure, calls[0]), "the failure itself");
            await watch.StopAsync();
        }),
        Case("chord.watch.failure.folds-pending-values-followed-by-a-failure-into-the-failure", async () =>
        {
            var jobs = new WatchJobs();
            var (watch, _) = RecordedWatch(jobs, J("{\"n\":0}"));
            watch.Advance(WatchFrame("{\"n\":1}", "[[\"s\",[\"n\"],1]]"), Bg);
            watch.Advance(WatchFrame("{\"n\":2}", "[[\"s\",[\"n\"],2]]"), Bg);
            var first = new InvalidOperationException("first");
            var second = new InvalidOperationException("second");
            watch.Fail(first, Bg);
            watch.Fail(second, Bg);
            var calls = new List<WatchCall<JsonValue>>();
            watch.Start(Recording(calls));
            jobs.Flush();
            Check(calls is [WatchFailedCall<JsonValue> { Error: var error }] && ReferenceEquals(error, second), "the newest failure only");
            Same("{\"n\":0}", watch.Value, "value");
            await watch.StopAsync();
        }),
        Case("chord.watch.failure.folds-a-pending-failure-followed-by-values-into-one-root-replacement-with-the-newest-value", async () =>
        {
            var jobs = new WatchJobs();
            var (watch, _) = RecordedWatch(jobs, J("{\"n\":0}"));
            var key = new ContextKey<string>("frame");
            watch.Fail(new InvalidOperationException("down"), Bg.With(key, "failure"));
            watch.Advance(WatchFrame("{\"n\":1}", "[[\"s\",[\"n\"],1]]"), Bg);
            watch.Advance(WatchRetirement("{\"n\":1}"), Bg);
            watch.Advance(WatchFrame("{\"m\":2}", "[[\"r\",{\"m\":2}]]"), Bg.With(key, "newest"));
            var calls = new List<(WatchCall<JsonValue> Call, string? Frame)>();
            watch.Start((call, deliveryContext) =>
            {
                calls.Add((call, deliveryContext.Get(key)));
                return Task.CompletedTask;
            });
            jobs.Flush();
            Equal(1, calls.Count, "one call");
            Equal(J("{\"kind\":\"value\",\"value\":{\"m\":2},\"ops\":[[\"r\",{\"m\":2}]],\"retired\":true}").ToJson(), CallJson(calls[0].Call), "root replacement");
            Equal("newest", calls[0].Frame, "newest frame's context");
            await watch.StopAsync();
        }),
        Case("chord.watch.failure.delivers-a-failure-that-arrives-during-a-call-after-it-then-the-recovery", async () =>
        {
            var jobs = new WatchJobs();
            var (watch, _) = RecordedWatch(jobs, 0);
            var gate = new TaskCompletionSource();
            var calls = new List<WatchCall<JsonValue>>();
            watch.Start(async (call, _) =>
            {
                calls.Add(call);
                if (calls.Count == 1) await gate.Task;
            });
            watch.Advance(WatchFrame("1", "[[\"r\",1]]"), Bg);
            jobs.Flush();
            watch.Advance(WatchFrame("2", "[[\"r\",2]]"), Bg);
            watch.Fail("down", Bg);
            gate.SetResult();
            jobs.Flush();
            watch.Advance(WatchFrame("3", "[[\"r\",3]]"), Bg);
            jobs.Flush();
            Seq(["1", "\"down\"", "3"], calls.Select(call => call switch
            {
                WatchValueCall<JsonValue> value => value.Value.ToJson(),
                WatchFailedCall<JsonValue> failed => JsonValue.String((string)failed.Error!).ToJson(),
                _ => "?",
            }), "calls");
            Same("3", watch.Value, "value");
            await watch.StopAsync();
        }),
        Case("chord.watch.failure.drops-a-pending-failure-when-the-source-ends-and-ignores-one-after", async () =>
        {
            var jobs = new WatchJobs();
            var (watch, _, calls) = RecordingWatch(jobs, 0);
            watch.Fail("down", Bg);
            watch.End(WatchEnd.Ended);
            watch.Fail("later", Bg);
            jobs.Flush();
            Equal(0, calls.Count, "no call");
            Equal(WatchEnd.Ended, await watch.Closed, "ended");
        }),
        Case("chord.watch.failure.passes-every-lone-value-call-on-by-identity-so-a-value-call-costs-the-watch-no-allocation", async () =>
        {
            var jobs = new WatchJobs();
            var (watch, _, calls) = RecordingWatch(jobs, 0);
            var sent = new List<WatchCall<JsonValue>>();
            for (var n = 1; n <= 1_000; n++)
            {
                var call = new WatchValueCall<JsonValue>(n, [new ReplaceOp(n)]);
                sent.Add(call);
                watch.Advance(call, Bg);
                jobs.Flush();
            }
            jobs.Flush();
            Equal(sent.Count, calls.Count, "calls");
            for (var index = 0; index < calls.Count; index++) Check(ReferenceEquals(sent[index], calls[index]), $"call {index} by identity");
            await watch.StopAsync();
        }),

        // ─── Watch end after delivery ───
        Case("chord.watch.end-after-delivery.takes-no-more-frames-delivers-what-is-pending-then-ends-ended-and-only-then-detaches", () =>
        {
            var jobs = new WatchJobs();
            var (watch, recorded, frames) = CollectingWatch(jobs, 1);
            watch.Advance(WatchRetirement("1"), Bg);
            watch.EndAfterDelivery();
            Equal(0, recorded.Detached, "attached until delivered");
            // The source sends nothing more: a later frame is not taken.
            watch.Advance(WatchFrame("2", "[[\"r\",2]]"), Bg);
            jobs.Flush();
            WatchFrames(["{\"value\":1,\"ops\":[],\"retired\":true}"], frames, "frames");
            Check(watch.Closed.IsCompleted && watch.Closed.Result == WatchEnd.Ended, "ended");
            Equal(1, recorded.Detached, "detached");
        }),
        Case("chord.watch.end-after-delivery.waits-for-start-to-deliver-and-ends-at-once-with-nothing-pending", async () =>
        {
            var jobs = new WatchJobs();
            var (watch, recorded) = RecordedWatch(jobs, 1);
            watch.Advance(WatchFrame("2", "[[\"r\",2]]"), Bg);
            watch.EndAfterDelivery();
            jobs.Flush();
            Check(!watch.Closed.IsCompleted, "open until started");
            Equal(0, recorded.Detached, "attached");
            var values = new List<string>();
            watch.Start(OnValue(call => values.Add(call.Value.ToJson())));
            jobs.Flush();
            Equal(WatchEnd.Ended, await watch.Closed, "ended");
            Seq(["2"], values, "delivered");
            Equal(1, recorded.Detached, "detached");

            var idle = CollectingWatch(jobs, 1);
            idle.Watch.EndAfterDelivery();
            Equal(WatchEnd.Ended, await idle.Watch.Closed, "idle ended");
            Equal(0, idle.Frames.Count, "idle frames");
            Equal(1, idle.Recorded.Detached, "idle detached");
        }),
        Case("chord.watch.end-after-delivery.ends-a-never-started-watch-at-its-sources-end-dropping-what-is-pending", async () =>
        {
            var jobs = new WatchJobs();
            var (watch, recorded) = RecordedWatch(jobs, 1);
            watch.Advance(WatchRetirement("1"), Bg);
            watch.EndAfterDelivery();
            var failure = new InvalidOperationException("source failed");
            watch.End(WatchEnd.Failed(failure));
            Equal(WatchEnd.Failed(failure), await watch.Closed, "failed");
            Equal(1, recorded.Detached, "detached");
            Equal("Watch is stopped", Throws<InvalidOperationException>(() => watch.Start((_, _) => Task.CompletedTask), "late start").Message, "message");
        }),
        Case("chord.watch.end-after-delivery.ends-ended-only-after-the-running-call-returns-when-nothing-is-pending-detached-then", async () =>
        {
            var jobs = new WatchJobs();
            var ending = EndingWhileRunning(jobs, () => Task.CompletedTask);
            Check(!ending.Watch.Closed.IsCompleted, "open while the call runs");
            Equal(0, ending.Recorded.Detached, "attached");
            ending.Gate.SetResult();
            Equal(WatchEnd.Ended, await ending.Watch.Closed, "ended");
            Check(ending.Returned, "the call returned");
            Seq(["1"], ending.Values, "values");
            Equal(1, ending.Recorded.Detached, "detached");
        }),
        Case("chord.watch.end-after-delivery.ends-listener-error-and-reports-it-when-the-running-call-throws-after-end-after-delivery", async () =>
        {
            var jobs = new WatchJobs();
            var failure = new InvalidOperationException("listener failed");
            var ending = EndingWhileRunning(jobs, () => Task.FromException(failure));
            Check(!ending.Watch.Closed.IsCompleted, "open while the call runs");
            ending.Gate.SetResult();
            Equal(WatchEnd.ListenerError(failure), await ending.Watch.Closed, "listener_error");
            Check(ending.Recorded.Reported is [var reported] && ReferenceEquals(reported, failure), "reported");
            Equal(1, ending.Recorded.Detached, "detached");
        }),
        Case("chord.watch.end-after-delivery.ends-stopped-when-stopped-while-ending-without-awaiting-the-running-call", async () =>
        {
            var jobs = new WatchJobs();
            var ending = EndingWhileRunning(jobs, () => Task.CompletedTask);
            Equal(WatchEnd.Stopped, await ending.Watch.StopAsync(), "stopped");
            Equal(1, ending.Recorded.Detached, "detached");
            Check(!ending.Returned, "the call still runs");
            ending.Gate.SetResult();
            jobs.Flush();
            Equal(WatchEnd.Stopped, await ending.Watch.Closed, "still stopped");
        }),

        // ─── Watch call objects ───
        Case("chord.watch.calls.hands-a-listener-plain-data-only-kind-value-ops-retired", async () =>
        {
            var properties = typeof(WatchValueCall<JsonValue>).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            Seq(["Kind", "Ops", "Retired", "Value"], properties.Select(property => property.Name).Order(StringComparer.Ordinal), "a value call's data");
            Check(properties.All(property => !typeof(DurableContext).IsAssignableFrom(property.PropertyType)), "no context in a call");
            var jobs = new WatchJobs();
            var (watch, _) = RecordedWatch(jobs, J("{\"n\":0}"));
            var changes = new List<WatchCall<JsonValue>>();
            watch.Start(Recording(changes));
            watch.Advance(WatchFrame("{\"n\":1}", "[[\"s\",[\"n\"],1]]"), WatchProducerContext(new object()));
            jobs.Flush();
            // Folded: the watch builds the change.
            watch.Advance(WatchFrame("{\"n\":2}", "[[\"s\",[\"n\"],2]]"), WatchProducerContext(new object()));
            watch.Advance(WatchRetirement("{\"n\":2}"), WatchProducerContext(new object()));
            jobs.Flush();
            Equal(2, changes.Count, "calls");
            foreach (var change in changes)
            {
                Equal(change, change with { }, "a record copy equals the call");
                Equal(CallJson(change), J(CallJson(change)).ToJson(), "the JSON form round-trips");
            }
            Equal(J("{\"kind\":\"value\",\"value\":{\"n\":2},\"ops\":[[\"s\",[\"n\"],2]],\"retired\":true}").ToJson(), CallJson(changes[1]), "folded call");
            await watch.StopAsync();
        }),
        Case("chord.watch.calls.passes-a-frame-delivered-alone-on-as-the-source-made-it", async () =>
        {
            var jobs = new WatchJobs();
            var (watch, _) = RecordedWatch(jobs, 0);
            var changes = new List<WatchCall<JsonValue>>();
            watch.Start(Recording(changes));
            var alone = WatchFrame("1", "[[\"r\",1]]");
            watch.Advance(alone, Bg);
            jobs.Flush();
            Check(ReferenceEquals(alone, changes[0]), "as made");
            await watch.StopAsync();
        }),
        Case("chord.watch.calls.keeps-no-frame-context-alive-through-a-call-a-listener-keeps", () =>
        {
            var kept = new List<object>();
            foreach (var native in new[] { false, true })
            {
                var payload = DeliverKept(kept, native);
                CollectGarbage();
                Equal(native ? 2 : 1, kept.Count, "kept calls");
                Check(!payload.IsAlive, (native ? "native" : "JSON") + ": the frame context is released");
            }
            var nativeCall = (NativeWatchValueCall<int>)kept[^1];
            Seq(["Kind", "Value"], nativeCall.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => property.Name).Order(StringComparer.Ordinal),
                "a native call's data");
            Equal(new NativeWatchValueCall<int>(1), nativeCall, "a native call is { kind: value, value }");
        }),

        // ─── Watch ends ───
        Case("chord.watch.ends.stops-idempotently-and-rejects-a-repeated-or-late-start", async () =>
        {
            var jobs = new WatchJobs();
            var (watch, recorded) = RecordedWatch(jobs, 0);
            watch.Start((_, _) => Task.CompletedTask);
            Equal("Watch is already started", Throws<InvalidOperationException>(() => watch.Start((_, _) => Task.CompletedTask), "repeated start").Message, "message");
            Equal(WatchEnd.Stopped, await watch.StopAsync(), "stopped");
            Equal(WatchEnd.Stopped, await watch.StopAsync(), "stopped again");
            Equal(1, recorded.Detached, "detached once");

            var late = RecordedWatch(jobs, 0).Watch;
            await late.StopAsync();
            Equal("Watch is stopped", Throws<InvalidOperationException>(() => late.Start((_, _) => Task.CompletedTask), "late start").Message, "late message");
        }),
        Case("chord.watch.ends.ends-ended-or-failed-from-its-source-dropping-pending-frames", async () =>
        {
            var jobs = new WatchJobs();
            var (watch, _, frames) = CollectingWatch(jobs, 0);
            watch.Advance(WatchFrame("1", "[[\"r\",1]]"), Bg);
            watch.End(WatchEnd.Ended);
            jobs.Flush();
            Equal(0, frames.Count, "dropped");
            Equal(WatchEnd.Ended, await watch.Closed, "ended");

            var failure = new InvalidOperationException("source failed");
            var failed = RecordedWatch(jobs, 0).Watch;
            failed.End(WatchEnd.Failed(failure));
            failed.End(WatchEnd.Ended);
            Equal(WatchEnd.Failed(failure), await failed.Closed, "failed, once");
        }),
        Case("chord.watch.ends.stops-future-delivery-without-aborting-or-awaiting-an-in-flight-callback", async () =>
        {
            var jobs = new WatchJobs();
            var (watch, _) = RecordedWatch(jobs, 0);
            var gate = new TaskCompletionSource();
            var calls = new List<string>();
            watch.Start(OnValueAsync(async (call, _) =>
            {
                calls.Add("start " + call.Value.ToJson());
                await gate.Task;
                calls.Add("end " + call.Value.ToJson());
            }));
            watch.Advance(WatchFrame("1", "[[\"r\",1]]"), Bg);
            jobs.Flush();
            watch.Advance(WatchFrame("2", "[[\"r\",2]]"), Bg);
            Equal(WatchEnd.Stopped, await watch.StopAsync(), "stopped");
            gate.SetResult();
            jobs.Flush();
            Seq(["start 1", "end 1"], calls, "calls");
        }),
        Case("chord.watch.ends.ends-aborted-when-its-acquisition-signal-aborts-also-when-already-aborted", async () =>
        {
            var jobs = new WatchJobs();
            using var controller = new CancellationTokenSource();
            var (watch, recorded) = RecordedWatch(jobs, 0);
            watch.ObserveAbort(controller.Token);
            Equal("Watch abort is already installed", Throws<InvalidOperationException>(() => watch.ObserveAbort(controller.Token), "twice").Message, "message");
            controller.Cancel();
            Equal(WatchEnd.Aborted, await watch.Closed, "aborted");
            Equal(1, recorded.Detached, "detached");

            var aborted = RecordedWatch(jobs, 0).Watch;
            aborted.ObserveAbort(new CancellationToken(canceled: true));
            Equal(WatchEnd.Aborted, await aborted.Closed, "already aborted");

            var direct = RecordedWatch(jobs, 0).Watch;
            direct.Abort();
            Equal(WatchEnd.Aborted, await direct.Closed, "abort()");
        }),
        Case("chord.watch.ends.ends-listener-error-on-a-throw-reports-it-and-delivers-nothing-more", async () =>
        {
            var jobs = new WatchJobs();
            var (watch, recorded) = RecordedWatch(jobs, 0);
            var failure = new InvalidOperationException("listener failed");
            var gate = new TaskCompletionSource();
            var values = new List<string>();
            watch.Start(OnValueAsync(async (call, _) =>
            {
                values.Add(call.Value.ToJson());
                await gate.Task;
                throw failure;
            }));
            watch.Advance(WatchFrame("1", "[[\"r\",1]]"), Bg);
            jobs.Flush();
            watch.Advance(WatchFrame("2", "[[\"r\",2]]"), Bg);
            gate.SetResult();
            Equal(WatchEnd.ListenerError(failure), await watch.Closed, "listener_error");
            Seq(["1"], values, "values");
            Check(recorded.Reported is [var reported] && ReferenceEquals(reported, failure), "reported");
            Equal(1, recorded.Detached, "detached");
        }),
        // Adapted: "wraps a thrown non-Error in an Error". .NET throws only exceptions; a cancelled task is the rejection that carries none
        // of the listener's own.
        Case("chord.watch.ends.ends-listener-error-with-the-cancellation-of-a-cancelled-listener-task", async () =>
        {
            var jobs = new WatchJobs();
            var (watch, _) = RecordedWatch(jobs, 0);
            watch.Start((_, _) => Task.FromCanceled(new CancellationToken(canceled: true)));
            watch.Advance(WatchFrame("1", "[[\"r\",1]]"), Bg);
            jobs.Flush();
            var end = await watch.Closed;
            Equal(WatchEndKind.ListenerError, end.Kind, "kind");
            Check(end.Error is OperationCanceledException, "the cancellation: " + end.Error);
        }),

        // ─── Watch references ───
        Case("chord.watch.references.retain-neither-source-listener-nor-signal-once-ended-while-still-referenced", async () =>
        {
            var jobs = new WatchJobs();
            // Control: an open watch keeps its source, so the collections below are real.
            var open = AcquireWatch(jobs, _ => { });
            CollectGarbage();
            Check(open.Source.IsAlive && open.Listener.IsAlive && open.Signal.IsAlive, "an open watch keeps its source, listener and signal");
            await open.Watch.StopAsync();

            foreach (var (name, end) in new (string, Action<Watch<JsonValue>>)[]
            {
                ("stop", watch => _ = watch.StopAsync()),
                ("abort", watch => watch.Abort()),
                ("end", watch => watch.End(WatchEnd.Ended)),
                ("endAfterDelivery", watch => watch.EndAfterDelivery()),
            })
            {
                var ended = AcquireWatch(jobs, end);
                jobs.Flush();
                CollectGarbage();
                Check(ended.Watch.Closed.IsCompleted, name + ": ended");
                Check(!ended.Source.IsAlive, name + ": source released");
                Check(!ended.Listener.IsAlive, name + ": listener released");
                Check(!ended.Signal.IsAlive, name + ": signal released");
            }
        }),
        Case("chord.watch.references.keeps-no-ops-a-fold-or-an-overflow-dropped-while-it-waits", async () =>
        {
            var jobs = new WatchJobs();
            var (watch, batches, newest) = OverflowWatch(jobs);
            CollectGarbage();
            Seq([false, false, false, false], batches.Select(batch => batch.IsAlive), "every batch released");
            var changes = new List<WatchCall<JsonValue>>();
            watch.Start(Recording(changes));
            jobs.Flush();
            Equal(1, changes.Count, "one call");
            Equal("{\"kind\":\"value\",\"value\":" + newest.ToJson() + ",\"ops\":[[\"r\"," + newest.ToJson() + "]],\"retired\":false}", CallJson(changes[0]), "root replacement");
            await watch.StopAsync();
        }),
        Case("chord.watch.references.delivers-a-native-backlog-that-never-drains-in-order", async () =>
        {
            var jobs = new WatchJobs();
            var watch = new NativeWatch<int>(0, () => { }, _ => { }, schedule: jobs.Post);
            var values = new List<int>();
            var next = 50;
            // Each call queues one more value behind a backlog of 50, so the queue never empties for 1,000 values.
            watch.Start((call, _) =>
            {
                values.Add(((NativeWatchValueCall<int>)call).Value);
                if (next < 1_000) watch.Advance(next++, Bg);
                return Task.CompletedTask;
            });
            for (var n = 0; n < 50; n++) watch.Advance(n, Bg);
            jobs.Flush();
            Seq(Enumerable.Range(0, 1_000), values, "values");
            await watch.StopAsync();
        }),
        Case("chord.watch.references.releases-its-pending-queue-and-leaf-index-once-delivered-or-ended", async () =>
        {
            var jobs = new WatchJobs();
            var delivered = PendingPayloadWatch(jobs);
            delivered.Gate.SetResult();
            jobs.Flush();
            CollectGarbage();
            Check(!delivered.Payload.IsAlive, "delivered: released");
            Same("3", delivered.Watch.Value, "delivered value");

            var stopped = PendingPayloadWatch(jobs);
            await stopped.Watch.StopAsync();
            CollectGarbage();
            Check(!stopped.Payload.IsAlive, "stopped: released");
        }),

        // ─── NativeWatch ───
        Case("chord.watch.native.delivers-values-by-identity-in-order-with-a-context-never-inline", () =>
        {
            var jobs = new WatchJobs();
            var first = new object();
            var second = new object();
            var watch = new NativeWatch<object>(new object(), () => { }, _ => { }, schedule: jobs.Post);
            var key = new ContextKey<string>("frame");
            var delivered = new List<(object Value, string? Frame)>();
            watch.Start((call, deliveryContext) =>
            {
                delivered.Add((((NativeWatchValueCall<object>)call).Value, deliveryContext.Get(key)));
                return Task.CompletedTask;
            });
            watch.Advance(first, Bg.With(key, "first"));
            watch.Advance(second, Bg.With(key, "second"));
            Equal(0, delivered.Count, "not inline");
            jobs.Flush();
            Equal(2, delivered.Count, "calls");
            Check(ReferenceEquals(first, delivered[0].Value) && ReferenceEquals(second, delivered[1].Value), "by identity, in order");
            Seq(["first", "second"], delivered.Select(entry => entry.Frame), "contexts");
            Check(ReferenceEquals(second, watch.Value), "value");
        }),
        Case("chord.watch.native.collapses-101-pending-values-to-the-newest", () =>
        {
            var jobs = new WatchJobs();
            var watch = new NativeWatch<int>(0, () => { }, _ => { }, schedule: jobs.Post);
            for (var n = 1; n <= 101; n++) watch.Advance(n, Bg);
            var values = new List<int>();
            watch.Start(OnNativeValue<int>(value => values.Add(value)));
            jobs.Flush();
            Seq([101], values, "values");
        }),
        Case("chord.watch.native.collapses-101-pending-values-to-the-value-of-replace", () =>
        {
            var jobs = new WatchJobs();
            var watch = new NativeWatch<string>("", () => { }, _ => { }, () => "replacement", jobs.Post);
            for (var n = 1; n <= 101; n++) watch.Advance($"value {n}", Bg);
            var values = new List<string>();
            watch.Start(OnNativeValue<string>(value => values.Add(value)));
            jobs.Flush();
            Seq(["replacement"], values, "values");
        }),
        Case("chord.watch.native.ends-with-every-reason-of-its-lifecycle", async () =>
        {
            var jobs = new WatchJobs();
            NativeWatch<int> Create() => new(0, () => { }, _ => { }, schedule: jobs.Post);
            Equal(WatchEnd.Stopped, await Create().StopAsync(), "stopped");

            var aborted = Create();
            aborted.ObserveAbort(new CancellationToken(canceled: true));
            Equal(WatchEnd.Aborted, await aborted.Closed, "aborted");

            var ended = Create();
            ended.End(WatchEnd.Ended);
            Equal(WatchEnd.Ended, await ended.Closed, "ended");

            var finishing = Create();
            var finished = new List<int>();
            finishing.Advance(1, Bg);
            finishing.Advance(2, Bg);
            finishing.EndAfterDelivery();
            finishing.Advance(3, Bg);
            finishing.Start(OnNativeValue<int>(value => finished.Add(value)));
            jobs.Flush();
            Equal(WatchEnd.Ended, await finishing.Closed, "ended after delivery");
            Seq([1, 2], finished, "finished");

            var error = new InvalidOperationException("source failed");
            var failed = Create();
            failed.End(WatchEnd.Failed(error));
            Equal(WatchEnd.Failed(error), await failed.Closed, "failed");

            var reported = new List<Exception>();
            var throwing = new NativeWatch<int>(0, () => { }, reported.Add, schedule: jobs.Post);
            var failure = new InvalidOperationException("listener failed");
            throwing.Start((_, _) => Task.FromException(failure));
            throwing.Advance(1, Bg);
            jobs.Flush();
            Equal(WatchEnd.ListenerError(failure), await throwing.Closed, "listener_error");
            Check(reported is [var only] && ReferenceEquals(only, failure), "reported");
        }),
        Case("chord.watch.native.delivers-a-failure-as-a-call-stays-open-and-folds-it-with-the-values-around-it", async () =>
        {
            var jobs = new WatchJobs();
            var reported = new List<Exception>();
            var watch = new NativeWatch<int>(0, () => { }, reported.Add, schedule: jobs.Post);
            var calls = new List<NativeWatchCall<int>>();
            watch.Start((call, _) =>
            {
                calls.Add(call);
                return Task.CompletedTask;
            });
            watch.Advance(1, Bg);
            jobs.Flush();
            // Values followed by a failure: the failure; a failure followed by values: the values, in order.
            watch.Advance(2, Bg);
            watch.Advance(3, Bg);
            watch.Fail("down", Bg);
            jobs.Flush();
            Equal(1, watch.Value, "value kept");
            watch.Fail("again", Bg);
            watch.Advance(4, Bg);
            watch.Advance(5, Bg);
            jobs.Flush();
            Seq<NativeWatchCall<int>>([new NativeWatchValueCall<int>(1), new NativeWatchFailedCall<int>("down"), new NativeWatchValueCall<int>(4), new NativeWatchValueCall<int>(5)],
                calls, "calls");
            Equal(5, watch.Value, "value");
            Equal(0, reported.Count, "nothing reported");
            await watch.StopAsync();
        }),
        Case("chord.watch.native.retains-neither-its-source-nor-its-listener-once-stopped-while-still-referenced", async () =>
        {
            var jobs = new WatchJobs();
            var open = AcquireNativeWatch(jobs, stop: false);
            CollectGarbage();
            Check(open.Source.IsAlive, "an open watch keeps its source");
            await open.Watch.StopAsync();

            var stopped = AcquireNativeWatch(jobs, stop: true);
            CollectGarbage();
            Check(!stopped.Source.IsAlive, "source released");
            Check(!stopped.Listener.IsAlive, "listener released");
            Equal(1, stopped.Watch.Value, "value kept");
        }),

        // ─── Native: the default scheduler and concurrent callers ───
        Case("chord.watch.threads.default-scheduler-never-runs-the-listener-inside-advance", async () =>
        {
            var inside = new ThreadLocal<bool>();
            var violations = 0;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var values = new List<int>();
            var watch = new NativeWatch<int>(0, () => { }, _ => { });
            watch.Start((call, _) =>
            {
                if (inside.Value) Interlocked.Increment(ref violations);
                var value = ((NativeWatchValueCall<int>)call).Value;
                lock (values) values.Add(value);
                if (value == 100) done.TrySetResult();
                return Task.CompletedTask;
            });
            for (var n = 1; n <= 100; n++)
            {
                inside.Value = true;
                watch.Advance(n, Bg);
                inside.Value = false;
            }
            await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Equal(0, violations, "listener calls inside Advance");
            lock (values) Seq(Enumerable.Range(1, 100), values, "every value in order");
            await watch.StopAsync();
        }),
        Case("chord.watch.threads.a-producer-thread-and-a-slow-listener-converge-on-the-newest-value", async () =>
        {
            var tracker = Tracker.Track(J("{\"text\":\"\",\"n\":0,\"list\":[]}"));
            var gate = new Lock();
            JsonValue replica = tracker.Value;
            var calls = 0;
            var failures = new List<string>();
            var watch = new Watch<JsonValue>(tracker.Value, () => { }, error => { lock (gate) failures.Add(error.Message); });
            watch.Start(async (call, _) =>
            {
                var valueCall = (WatchValueCall<JsonValue>)call;
                lock (gate)
                {
                    replica = DeltaApply.Apply(replica, valueCall.Ops);
                    if (!JsonValue.DeepEquals(replica, valueCall.Value)) failures.Add("replica diverged at call " + calls);
                    calls++;
                }
                if (calls % 7 == 0) await Task.Delay(1);
                else await Task.Yield();
            });
            const int commits = 2_000;
            await Task.Run(() =>
            {
                for (var index = 0; index < commits; index++)
                {
                    var change = tracker.BeginChange();
                    var draft = change.State.AsObject();
                    draft.AppendString("text", "t");
                    draft.Set("n", index);
                    if (index % 50 == 0) draft.GetArray("list").Add(index);
                    var prepared = change.Prepare();
                    tracker.Adopt(prepared);
                    watch.Advance(new WatchValueCall<JsonValue>(tracker.Value, prepared.Ops), Bg);
                }
            });
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                lock (gate) if (JsonValue.DeepEquals(replica, tracker.Value) || failures.Count > 0) break;
                if (DateTime.UtcNow > deadline) throw new InvalidOperationException("the listener never reached the newest value");
                await Task.Delay(5);
            }
            lock (gate)
            {
                Equal(0, failures.Count, "failures: " + string.Join("; ", failures));
                Check(calls >= 1 && calls <= commits, $"calls: {calls}");
            }
            Check(JsonValue.DeepEquals(watch.Value, tracker.Value), "value");
            Check(!watch.Closed.IsCompleted, "still open");
            await watch.StopAsync();
        }),
    ];

    /// <summary>The microtask queue a test's watches post their delivery to; <see cref="Flush"/> is upstream's flush().</summary>
    private sealed class WatchJobs
    {
        private readonly Queue<Action> jobs = new();

        public void Post(Action job)
        {
            lock (jobs) jobs.Enqueue(job);
        }

        public void Flush()
        {
            while (true)
            {
                Action? job;
                lock (jobs)
                {
                    if (!jobs.TryDequeue(out job)) return;
                }
                job();
            }
        }
    }

    /// <summary>
    /// vi.useFakeTimers(): timers that fire when <see cref="Advance"/> passes them, flushing the jobs between them. The clock counts whole
    /// milliseconds and carries the fraction of an advance as whole nanoseconds, so the same advances always reach the same millisecond
    /// (summing 1000 / 60 as doubles lands a hair either side of a second, which moves a timer across a tick boundary).
    /// </summary>
    private sealed class WatchClock(WatchJobs jobs)
    {
        private readonly List<(long Due, long Order, TaskCompletionSource Timer)> timers = [];
        private long order;
        private long now;
        private long nanos;

        public Task Delay(long ms)
        {
            var timer = new TaskCompletionSource();
            timers.Add((now + ms, order++, timer));
            return timer.Task;
        }

        public void Advance(double ms)
        {
            var target = now + (long)Math.Floor(ms);
            nanos += (long)Math.Floor(ms * 1e6 % 1e6);
            if (nanos >= 1_000_000)
            {
                target++;
                nanos -= 1_000_000;
            }
            jobs.Flush();
            while (true)
            {
                var due = timers.Where(timer => timer.Due <= target).OrderBy(timer => timer.Due).ThenBy(timer => timer.Order).ToList();
                if (due.Count == 0) break;
                var next = due[0];
                timers.Remove(next);
                now = next.Due;
                next.Timer.SetResult();
                jobs.Flush();
            }
            now = target;
        }
    }

    /// <summary>A watch's detach count and reported listener errors.</summary>
    private sealed class WatchRecord
    {
        public int Detached;
        public readonly List<Exception> Reported = [];
    }

    private sealed record EndingWatch(Watch<JsonValue> Watch, WatchRecord Recorded, List<string> Values, TaskCompletionSource Gate, StrongBox<bool> ReturnedBox)
    {
        public bool Returned => ReturnedBox.Value;
    }

    private static WatchValueCall<JsonValue> WatchFrame(string value, string ops) => new(J(value), Ops(ops));

    private static WatchValueCall<JsonValue> WatchRetirement(string value, string ops = "[]") => new(J(value), Ops(ops), Retired: true);

    /// <summary>A watch whose detach and reports are recorded.</summary>
    private static (Watch<JsonValue> Watch, WatchRecord Recorded) RecordedWatch(WatchJobs jobs, JsonValue value)
    {
        var recorded = new WatchRecord();
        var watch = new Watch<JsonValue>(value, () => recorded.Detached++, recorded.Reported.Add, schedule: jobs.Post);
        return (watch, recorded);
    }

    /// <summary>A started watch collecting its value calls as frames.</summary>
    private static (Watch<JsonValue> Watch, WatchRecord Recorded, List<string> Frames) CollectingWatch(WatchJobs jobs, JsonValue value)
    {
        var (watch, recorded) = RecordedWatch(jobs, value);
        var frames = new List<string>();
        watch.Start(OnValue(call => frames.Add(FrameJson(call))));
        return (watch, recorded, frames);
    }

    /// <summary>A started watch recording every call, failures included.</summary>
    private static (Watch<JsonValue> Watch, WatchRecord Recorded, List<WatchCall<JsonValue>> Calls) RecordingWatch(WatchJobs jobs, JsonValue value)
    {
        var (watch, recorded) = RecordedWatch(jobs, value);
        var calls = new List<WatchCall<JsonValue>>();
        watch.Start(Recording(calls));
        return (watch, recorded, calls);
    }

    private static Func<WatchCall<JsonValue>, DurableContext, Task> Recording(List<WatchCall<JsonValue>> calls) => (call, _) =>
    {
        calls.Add(call);
        return Task.CompletedTask;
    };

    /// <summary>A listener for value calls only: a failed call throws.</summary>
    private static Func<WatchCall<JsonValue>, DurableContext, Task> OnValue(Action<WatchValueCall<JsonValue>> listener) =>
        OnValueAsync((call, _) =>
        {
            listener(call);
            return Task.CompletedTask;
        });

    private static Func<WatchCall<JsonValue>, DurableContext, Task> OnValueAsync(Func<WatchValueCall<JsonValue>, DurableContext, Task> listener) =>
        (call, deliveryContext) => call is WatchValueCall<JsonValue> value ? listener(value, deliveryContext) : throw new InvalidOperationException($"Unexpected {call.Kind} call");

    private static Func<NativeWatchCall<T>, DurableContext, Task> OnNativeValue<T>(Action<T> listener) => (call, _) =>
    {
        if (call is not NativeWatchValueCall<T> value) throw new InvalidOperationException($"Unexpected {call.Kind} call");
        listener(value.Value);
        return Task.CompletedTask;
    };

    /// <summary>Upstream's collected frame: <c>{ value, ops }</c>, with <c>retired: true</c> only for a retirement.</summary>
    private static string FrameJson(WatchValueCall<JsonValue> call) => FrameJson(call.Value, call.Ops, call.Retired);

    private static string FrameJson(JsonValue value, IEnumerable<Op> ops, bool retired = false)
    {
        var properties = new List<KeyValuePair<string, JsonValue>> { new("value", value), new("ops", OpsJson(ops)) };
        if (retired) properties.Add(new("retired", true));
        return JsonValue.Object(properties).ToJson();
    }

    /// <summary>A call as upstream's plain object: <c>{ kind, value, ops, retired }</c> or <c>{ kind, error }</c>.</summary>
    private static string CallJson(WatchCall<JsonValue> call) => call switch
    {
        WatchValueCall<JsonValue> value => JsonValue.Object(KeyValuePair.Create("kind", (JsonValue)"value"), KeyValuePair.Create("value", value.Value),
            KeyValuePair.Create("ops", (JsonValue)OpsJson(value.Ops)), KeyValuePair.Create("retired", (JsonValue)value.Retired)).ToJson(),
        WatchFailedCall<JsonValue> failed => JsonValue.Object(KeyValuePair.Create("kind", (JsonValue)"failed"),
            KeyValuePair.Create("error", (JsonValue)(failed.Error?.ToString() ?? "null"))).ToJson(),
        _ => throw new InvalidOperationException("Unknown call"),
    };

    private static void WatchFrames(IEnumerable<string> expected, IEnumerable<string> actual, string what) =>
        Seq(expected.Select(frame => J(frame).ToJson()), actual, what);

    private static void WatchCalls(IEnumerable<string> expected, IEnumerable<WatchCall<JsonValue>> actual, string what) =>
        Seq(expected.Select(call => J(call).ToJson()), actual.Select(CallJson), what);

    /// <summary>Advances <paramref name="watch"/> by one frame per element of <c>{ k: [0, ..., count - 1] }</c>, each setting its own path.</summary>
    private static JsonValue AdvanceDistinct(Watch<JsonValue> watch, int count)
    {
        var values = ImmutableArray.CreateBuilder<JsonValue>(count);
        for (var index = 0; index < count; index++)
        {
            values.Add(index);
            watch.Advance(new WatchValueCall<JsonValue>(JsonValue.Object(KeyValuePair.Create("k", (JsonValue)new JsonArrayValue(values.ToImmutable()))),
                [new SetOp(DeltaPath.Of("k", index), index)]), Bg);
        }
        return JsonValue.Object(KeyValuePair.Create("k", (JsonValue)new JsonArrayValue(values.ToImmutable())));
    }

    /// <summary>A frame context carrying <paramref name="payload"/> and an abort signal, as a producer's is.</summary>
    private static DurableContext WatchProducerContext(object payload) =>
        Bg.With(new ContextKey<object>("payload"), payload).WithCancellation(new CancellationTokenSource().Token);

    /// <summary>A started watch whose listener is in a call held by its gate, with nothing pending, then EndAfterDelivery().</summary>
    private static EndingWatch EndingWhileRunning(WatchJobs jobs, Func<Task> settle)
    {
        var (watch, recorded) = RecordedWatch(jobs, 0);
        var gate = new TaskCompletionSource();
        var values = new List<string>();
        var returned = new StrongBox<bool>();
        watch.Start(OnValueAsync(async (call, _) =>
        {
            values.Add(call.Value.ToJson());
            await gate.Task;
            await settle();
            returned.Value = true;
        }));
        watch.Advance(WatchRetirement("1", "[[\"r\",1]]"), Bg);
        jobs.Flush();
        Seq(["1"], values, "running");
        watch.EndAfterDelivery();
        jobs.Flush();
        return new EndingWatch(watch, recorded, values, gate, returned);
    }

    /// <summary>Delivers one frame whose context alone references a payload, keeps the call, and stops the watch.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DeliverKept(List<object> kept, bool native)
    {
        var jobs = new WatchJobs();
        var payload = new object[] { new string('x', 1000) };
        if (native)
        {
            var watch = new NativeWatch<int>(0, () => { }, _ => { }, schedule: jobs.Post);
            watch.Start((call, _) =>
            {
                kept.Add(call);
                return Task.CompletedTask;
            });
            watch.Advance(1, WatchProducerContext(payload));
            jobs.Flush();
            _ = watch.StopAsync();
        }
        else
        {
            var (watch, _) = RecordedWatch(jobs, 0);
            watch.Start((call, _) =>
            {
                kept.Add(call);
                return Task.CompletedTask;
            });
            watch.Advance(WatchFrame("1", "[[\"r\",1]]"), WatchProducerContext(payload));
            jobs.Flush();
            _ = watch.StopAsync();
        }
        return new WeakReference(payload);
    }

    /// <summary>A watch over a source only its closures reference, its listener and its signal; <paramref name="end"/> ends it before
    /// returning.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Watch<JsonValue> Watch, WeakReference Source, WeakReference Listener, WeakReference Signal) AcquireWatch(WatchJobs jobs, Action<Watch<JsonValue>> end)
    {
        var source = new object[] { 1 };
        var controller = new CancellationTokenSource();
        var watch = new Watch<JsonValue>(0, () => GC.KeepAlive(source), _ => GC.KeepAlive(source), schedule: jobs.Post);
        var marker = new object();
        Func<WatchCall<JsonValue>, DurableContext, Task> listener = (_, _) =>
        {
            GC.KeepAlive(marker);
            return Task.CompletedTask;
        };
        watch.ObserveAbort(controller.Token);
        watch.Start(listener);
        watch.Advance(WatchFrame("1", "[[\"r\",1]]"), Bg);
        jobs.Flush();
        end(watch);
        return (watch, new WeakReference(source), new WeakReference(listener), new WeakReference(controller));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (NativeWatch<int> Watch, WeakReference Source, WeakReference Listener) AcquireNativeWatch(WatchJobs jobs, bool stop)
    {
        var source = new object[] { 1 };
        var watch = new NativeWatch<int>(0, () => GC.KeepAlive(source), _ => GC.KeepAlive(source), schedule: jobs.Post);
        var marker = new object();
        Func<NativeWatchCall<int>, DurableContext, Task> listener = (_, _) =>
        {
            GC.KeepAlive(marker);
            return Task.CompletedTask;
        };
        watch.Start(listener);
        watch.Advance((int)source[0], Bg);
        jobs.Flush();
        if (stop) _ = watch.StopAsync();
        return (watch, new WeakReference(source), new WeakReference(listener));
    }

    /// <summary>Feeds an unstarted watch four tracker batches of 4,000 sets each: the third overflows, the fourth is ignored.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Watch<JsonValue> Watch, WeakReference[] Batches, JsonValue Newest) OverflowWatch(WatchJobs jobs)
    {
        var builder = JsonObjectValue.CreateBuilder();
        for (var i = 0; i < 16_000; i++) builder[$"k{i}"] = 0;
        JsonValue initial = builder.ToImmutable();
        var tracker = Tracker.Track(initial);
        var watch = new Watch<JsonValue>(initial, () => { }, _ => { }, schedule: jobs.Post);
        var batches = new WeakReference[4];
        for (var round = 0; round < 4; round++)
        {
            var change = tracker.BeginChange();
            var state = change.State.AsObject();
            for (var i = round * 4_000; i < (round + 1) * 4_000; i++) state.Set($"k{i}", 1);
            var prepared = change.Prepare();
            tracker.Adopt(prepared);
            batches[round] = new WeakReference(ImmutableCollectionsMarshal.AsArray(prepared.Ops));
            watch.Advance(new WatchValueCall<JsonValue>(tracker.Value, prepared.Ops), Bg);
        }
        return (watch, batches, tracker.Value);
    }

    /// <summary>Two pending frames, folded, whose ops alone reference a payload, behind a call held by the gate.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Watch<JsonValue> Watch, WeakReference Payload, TaskCompletionSource Gate) PendingPayloadWatch(WatchJobs jobs)
    {
        var payload = J("{\"big\":\"" + new string('x', 1000) + "\"}");
        var watch = new Watch<JsonValue>(0, () => { }, _ => { }, schedule: jobs.Post);
        var gate = new TaskCompletionSource();
        var calls = 0;
        watch.Start(async (_, _) =>
        {
            if (++calls == 1) await gate.Task;
        });
        watch.Advance(WatchFrame("1", "[[\"s\",[\"n\"],1]]"), Bg);
        jobs.Flush();
        watch.Advance(new WatchValueCall<JsonValue>(2, [new SetOp(DeltaPath.Of("o"), payload)]), Bg);
        watch.Advance(WatchFrame("3", "[[\"s\",[\"n\"],3]]"), Bg);
        return (watch, new WeakReference(payload), gate);
    }
}
