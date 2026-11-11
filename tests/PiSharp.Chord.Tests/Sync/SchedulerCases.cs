// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/sync/client.ts.
// Authored native regression; not an upstream execution capture.
using PiSharp.Chord.Sync;

// Authored native regressions based on Pi v1.5.2 test/sync/sync.test.ts.
internal static partial class Program
{
    private sealed class SyncClock
    {
        private sealed class Timer(double at, Action callback)
        {
            internal double At = at;
            internal Action Callback = callback;
            internal bool Armed = true;
        }
        private readonly List<Timer> timers = [];
        internal double Now;
        internal Action SetTimer(Action action, double delay)
        {
            var timer = new Timer(Now + delay, action);
            timers.Add(timer);
            return () => timer.Armed = false;
        }
        internal async Task Advance(double milliseconds)
        {
            var end = Now + milliseconds;
            while (timers.Where(timer => timer.Armed && timer.At <= end).MinBy(timer => timer.At) is { } next)
            {
                Now = next.At;
                next.Armed = false;
                next.Callback();
                await Task.Delay(5);
            }
            Now = end;
        }
    }

    private static IEnumerable<(string, Func<Task>)> SyncSchedulerCases() =>
    [
        Case("sync.scheduler.ordinary-window-urgent-notification-and-close", async () =>
        {
            var clock = new SyncClock(); var times = new List<double>();
            var scheduler = new FlushScheduler(new()
            {
                Flush = () => { times.Add(clock.Now); return Task.CompletedTask; },
                SetTimer = clock.SetTimer, Now = () => clock.Now, OnError = error => throw error,
            });
            scheduler.Notify(false); scheduler.Notify(false);
            await clock.Advance(4000); Equal(0, times.Count, "not yet");
            scheduler.Notify(true); await clock.Advance(200); Sequence(new double[] { 4200 }, times, "urgent moved forward");
            await clock.Advance(5000); Equal(1, times.Count, "one per window");
            await scheduler.CloseAsync(); Sequence(new double[] { 4200, 9200 }, times, "closing flush");
            scheduler.Notify(true); await clock.Advance(10_000); Equal(2, times.Count, "closed ignores notify");
        }),
        Case("sync.scheduler.pending-urgency-survives-active-flush", async () =>
        {
            var clock = new SyncClock(); var times = new List<double>();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var scheduler = new FlushScheduler(new()
            {
                Flush = () =>
                {
                    times.Add(clock.Now);
                    if (times.Count == 1) return release.Task;
                    completed.TrySetResult(); return Task.CompletedTask;
                },
                SetTimer = clock.SetTimer, Now = () => clock.Now, OnError = error => throw error,
            });
            scheduler.Notify(false); await clock.Advance(5000);
            scheduler.Notify(false); scheduler.Notify(true);
            release.SetResult(); await Task.Delay(20);
            await clock.Advance(200); await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Sequence(new double[] { 5000, 5200 }, times, "pending urgency kept");
            await scheduler.CloseAsync();
        }),
        Case("sync.scheduler.retry-backoff-is-capped-and-needs-no-new-commit", async () =>
        {
            var clock = new SyncClock(); var times = new List<double>(); var errors = new List<Exception>();
            var scheduler = new FlushScheduler(new()
            {
                Flush = () =>
                {
                    times.Add(clock.Now);
                    return times.Count <= 4 ? Task.FromException(new IOException("offline")) : Task.CompletedTask;
                },
                SetTimer = clock.SetTimer, Now = () => clock.Now, OnError = errors.Add,
                RetryMs = 1000, MaxRetryMs = 2500,
            });
            scheduler.Notify(true); await clock.Advance(9000);
            Sequence(new double[] { 200, 1200, 3200, 5700, 8200 }, times, "exponential capped");
            Equal(4, errors.Count, "errors reported");
            await clock.Advance(50_000); Equal(5, times.Count, "success stopped retry");
            await scheduler.CloseAsync();
        }),
        Case("sync.scheduler.close-waits-then-flushes-once-and-propagates-final-failure", async () =>
        {
            var clock = new SyncClock(); var calls = 0; var active = 0; var peak = 0;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Flush()
            {
                calls++; active++; peak = Math.Max(peak, active);
                try
                {
                    if (calls == 1) await release.Task;
                    else throw new IOException("final flush failed");
                }
                finally { active--; }
            }
            var scheduler = new FlushScheduler(new()
            { Flush = Flush, SetTimer = clock.SetTimer, Now = () => clock.Now, OnError = _ => { } });
            scheduler.Notify(true); await clock.Advance(200);
            var closing = scheduler.CloseAsync(); var alsoClosing = scheduler.CloseAsync();
            Check(ReferenceEquals(closing, alsoClosing), "concurrent closes share final flush");
            Check(!closing.IsCompleted, "waits for active flush");
            release.SetResult(); await SyncFails<IOException>(() => closing);
            Equal(2, calls, "one final flush"); Equal(1, peak, "never simultaneous");
        }),
        Case("sync.scheduler.disarmed-stale-timer-cannot-flush", async () =>
        {
            var now = 0d; var callbacks = new List<Action>(); var calls = 0;
            var scheduler = new FlushScheduler(new()
            {
                Flush = () => { calls++; return Task.CompletedTask; }, Now = () => now,
                // A timer already queued at disarm is allowed to invoke its callback anyway.
                SetTimer = (callback, _) => { callbacks.Add(callback); return () => { }; }, OnError = _ => { },
            });
            scheduler.Notify(false); now = 100; scheduler.Notify(true);
            callbacks[0](); Equal(0, calls, "superseded callback");
            callbacks[1](); Equal(1, calls, "latest callback");
            await scheduler.CloseAsync(); callbacks[1](); Equal(2, calls, "closed callback ignored");
        }),
    ];
}
