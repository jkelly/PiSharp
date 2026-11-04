// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/harness-sleep.test.ts (f75b3240a, ec1be23eb), ported in the
// Pi v1.5.0 sync (area sleep). Durable sleep: a task ends a phase asleep with runtime.sleepUntil(), holds no invocation until its time, and
// resumes at its checkpoint once due; Harness.wake(), HarnessOptions.onWake and HarnessOptions.setTimer. Verbatim apart from the
// adaptations in HarnessSupport.cs, and these:
// - openNodeSqliteStorage is SqliteStorage.OpenAsync over a file in a fresh temporary directory. The store pass (StoreStorageNode.cs)
//   reruns the cases as harness.sleep.store.*, opening that file as a StoreStorage, as upstream's vitest.store.config.ts does, and,
//   as its other groups do here, running the cases upstream keeps on MemoryStorage on a fresh StoreStorage too.
// - toEqual of a Wake is record equality (a Wake without `at` is new Wake(false), as its At is null); toEqual and toMatchObject of
//   states and outcomes compare their JSON.
// - settle() is Flush(), a 20 ms delay, and Flush().
// - The fake host timers record each foreground timer with its callback and declined background ones, as upstream's fakeTimers();
//   their lists are read under a lock, as the scheduler arms and disarms timers on thread-pool threads.
// - onWake runs on a thread-pool thread, which reports its throw after the wake is recorded: "reports a throwing onWake ..." awaits the
//   report with Eventually where upstream's synchronous report is there at once.
// - The `eventually(() => !harness.wake().active)` waits are 440a5fcdb's, which counts open's recovery pass and reconcile work as
//   activity (ported by area reports).
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using static HarnessTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessSleepCases() =>
        SleepTests.Cases("harness.sleep.", () => new MemoryStorage(), async path => await SqliteStorage.OpenAsync(path));
}

/// <summary>The harness.sleep.* cases (upstream harness-sleep.test.ts).</summary>
internal static class SleepTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private static string SqlitePath() => Path.Combine(Temp("pi-durable-sleep"), "session.sqlite");

    private sealed class Runs
    {
        private int after;
        public int After => Volatile.Read(ref after);
        public void Ran() => Interlocked.Increment(ref after);
    }

    /// <summary>Sleeps <paramref name="delay"/> ms on the Harness clock, then completes with the time it resumed at (upstream
    /// <c>napTask</c>).</summary>
    private static DurableTask NapTask(double delay, Runs? runs = null) => Tasks.DefineTask(new TaskDefinition
    {
        Name = "test.nap",
        Version = 1,
        Initial = _ => O("""{"phase":"start"}"""),
        Phases = new Dictionary<string, TaskPhaseHandler>
        {
            ["start"] = (_, runtime, ctx) =>
                runtime.SleepUntilAsync(runtime.Now() + delay, JsonValue.Object([new("phase", "after"), new("slept", delay)]), ctx),
            ["after"] = async (_, runtime, ctx) =>
            {
                runs?.Ran();
                var resumed = runtime.Now();
                await runtime.CommitAsync((_, _) => Completed(resumed), ctx);
            },
        },
        Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => AbortedWith("test"), ctx),
    });

    private static async Task<TaskId> StartNap(IHarness harness, DurableTask task)
    {
        var root = await harness.RootAsync(Context);
        return await root.CommitAsync(tx => tx.CreateTaskAsync(task, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation)), Context);
    }

    private static async Task<TaskState?> StateOf(IHarness harness, TaskId id) => (await harness.GetTaskAsync(id, Context))?.State;

    private static async Task<DurableTaskStatus?> StatusOf(IHarness harness, TaskId id) => (await StateOf(harness, id))?.Status;

    private sealed class FakeTimer(double delayMs, Action callback)
    {
        private int disarmed;
        public double DelayMs { get; } = delayMs;
        public Action Callback { get; } = callback;
        public bool Disarmed => Volatile.Read(ref disarmed) == 1;
        public void Disarm() => Volatile.Write(ref disarmed, 1);
    }

    /// <summary>A host <c>setTimer</c> that records the timers that pace work and declines background ones (upstream
    /// <c>fakeTimers</c>).</summary>
    private sealed class FakeTimers
    {
        private readonly SyncList<FakeTimer> armed = new();

        public FakeTimers() => SetTimer = (callback, delayMs, options) =>
        {
            if (options.Background) return null;
            var timer = new FakeTimer(delayMs, callback);
            armed.Add(timer);
            return timer.Disarm;
        };

        public SetTimer SetTimer { get; }
        public List<FakeTimer> Armed => armed.Snapshot();
        public List<FakeTimer> Live() => [.. armed.Snapshot().Where(timer => !timer.Disarmed)];
    }

    /// <summary>Lets pending work and a few timer turns run (upstream <c>settle</c>).</summary>
    private static async Task Settle()
    {
        await Flush();
        await Task.Delay(20);
        await Flush();
    }

    /// <param name="prefix">The case ID prefix: harness.sleep., or harness.sleep.store. for the store pass (StoreStorageNode.cs).</param>
    /// <param name="fresh">Opens the storage of a case upstream runs on MemoryStorage.</param>
    /// <param name="openFile">Upstream's openNodeSqliteStorage: opens the storage over the file at a path.</param>
    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix, Func<IStorage> fresh, Func<string, Task<IStorage>> openFile)
    {

        yield return Case(prefix + "sleeps-without-an-invocation-and-resumes-at-its-checkpoint-once-due", async () =>
        {
            double clock = 1_000;
            var wakes = new SyncList<Wake>();
            var runs = new Runs();
            var nap = NapTask(1_000, runs);
            var (harness, _, _) = await OpenTasks(fresh(), [nap], now: () => Volatile.Read(ref clock), onWake: wakes.Add);
            var graph = await harness.WatchTaskGraphAsync(Context);
            // A started watch keeps its value current.
            graph.Start((_, _, _) => Task.CompletedTask);
            var id = await StartNap(harness, nap);
            harness.Resume();
            await Eventually(async () => await StatusOf(harness, id) == DurableTaskStatus.Sleeping);
            SameJson("""{"status":"sleeping","checkpoint":{"phase":"after","slept":1000},"until":2000}""", (await StateOf(harness, id))?.ToJson(),
                "sleeping state");
            var inspection = await harness.InspectAsync(Context);
            var inspected = inspection.Tasks.First(task => task.Record.Id == id).State;
            Equal(TaskInspectionKind.Sleeping, inspected.Kind, "inspection kind");
            Equal<double?>(2_000, inspected.Until, "inspection until");
            SameJson("""{"status":"sleeping","phase":"after","until":2000}""", graph.Value.Node(id)?.State.ToJson(), "graph state");
            await Eventually(() => !harness.Wake().Active);
            Equal(new Wake(false, 2_000), harness.Wake(), "wake while asleep");
            await Settle();
            Equal(new Wake(false, 2_000), wakes.Snapshot()[^1], "last onWake while asleep");

            // Early and repeated wakes run nothing.
            Volatile.Write(ref clock, 1_999);
            harness.Resume();
            harness.Resume();
            await Settle();
            Equal(0, runs.After, "after runs before its time");
            Equal(DurableTaskStatus.Sleeping, await StatusOf(harness, id), "still sleeping");

            Volatile.Write(ref clock, 2_000);
            harness.Resume();
            harness.Resume();
            var receipt = await harness.WaitForTaskAsync(id, Context);
            SameJson("""{"status":"completed","result":2000}""", ((SettlingTaskState)receipt.State).Outcome.ToJson(), "outcome");
            Equal(1, runs.After, "after runs");
            await Settle();
            await Eventually(() => !harness.Wake().Active);
            Equal(new Wake(false), harness.Wake(), "wake when idle");
            var all = wakes.Snapshot();
            Equal(new Wake(false), all[^1], "last onWake when idle");
            // Only changes: idle at open, running, asleep until 2000, running again, idle.
            for (var index = 1; index < all.Count; index++) Check(all[index] != all[index - 1], $"onWake {index} repeats {all[index]}");
            Check(all.Contains(new Wake(true)), "an active wake");
            Check(all.Contains(new Wake(false, 2_000)), "a wake at 2000");
            await graph.StopAsync();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-a-sleep-across-reopen", async () =>
        {
            var path = SqlitePath();
            double clock = 1_000;
            var nap = NapTask(60_000);
            async Task<IHarness> Open(IStorage storage) =>
                (await OpenTasks(storage, [nap], now: () => Volatile.Read(ref clock), onWake: _ => { })).Harness;
            var harness = await Open(await openFile(path));
            var id = await StartNap(harness, nap);
            harness.Resume();
            await Eventually(async () => await StatusOf(harness, id) == DurableTaskStatus.Sleeping);
            await harness.CloseAsync(Context);

            harness = await Open(await openFile(path));
            // Known from storage before scheduling starts, once open's recovery pass is done.
            await Eventually(() => !harness.Wake().Active);
            Equal(new Wake(false, 61_000), harness.Wake(), "wake after reopen");
            harness.Resume();
            await Settle();
            Equal(DurableTaskStatus.Sleeping, await StatusOf(harness, id), "still sleeping");
            await harness.CloseAsync(Context);

            Volatile.Write(ref clock, 61_000);
            harness = await Open(await openFile(path));
            Equal(new Wake(true), harness.Wake(), "wake once due");
            harness.Resume();
            SameJson("""{"status":"completed","result":61000}""",
                ((SettlingTaskState)(await harness.WaitForTaskAsync(id, Context)).State).Outcome.ToJson(), "outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "resumes-a-due-sleep-with-its-timer-without-onwake-with-background-timers-declined", async () =>
        {
            double clock = 1_000;
            var timers = new FakeTimers();
            var nap = NapTask(500);
            var (harness, _, _) = await OpenTasks(fresh(), [nap], now: () => Volatile.Read(ref clock), setTimer: timers.SetTimer);
            var id = await StartNap(harness, nap);
            harness.Resume();
            await Eventually(() => timers.Live().Any(timer => timer.DelayMs == 500));
            Equal(DurableTaskStatus.Sleeping, await StatusOf(harness, id), "sleeping");
            Volatile.Write(ref clock, 1_500);
            timers.Live().First(timer => timer.DelayMs == 500).Callback();
            // Polled: WaitForTaskAsync() would call Resume(), which wakes due sleeps itself.
            await Eventually(async () => await StatusOf(harness, id) == DurableTaskStatus.Terminal);
            ChatTests.MatchJson("""{"outcome":{"status":"completed","result":1500}}""", (await StateOf(harness, id))?.ToJson(), "state");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "runs-a-due-sleep-at-any-scheduling-pass-before-its-timer-fires", async () =>
        {
            double clock = 1_000;
            var timers = new FakeTimers();
            var nap = NapTask(500);
            var (harness, registry, _) = await OpenTasks(fresh(), [nap], now: () => Volatile.Read(ref clock),
                setTimer: timers.SetTimer);
            var id = await StartNap(harness, nap);
            harness.Resume();
            await Eventually(async () => await StatusOf(harness, id) == DurableTaskStatus.Sleeping);
            // The deadline passes, and an unrelated change starts a pass before the timer fires.
            Volatile.Write(ref clock, 1_500);
            registry.Install(Define.Extension(new Extension { Name = "unrelated" }));
            // Polled: WaitForTaskAsync() would call Resume(), which wakes due sleeps itself.
            await Eventually(async () => await StatusOf(harness, id) == DurableTaskStatus.Terminal);
            ChatTests.MatchJson("""{"outcome":{"status":"completed","result":1500}}""", (await StateOf(harness, id))?.ToJson(), "state");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "disarms-the-sleep-timer-when-the-sleep-is-aborted-and-when-the-harness-closes", async () =>
        {
            const double clock = 1_000;
            var timers = new FakeTimers();
            var shortNap = NapTask(500);
            var (harness, _, _) = await OpenTasks(fresh(), [shortNap], now: () => clock, setTimer: timers.SetTimer);
            var aborted = await StartNap(harness, shortNap);
            harness.Resume();
            await Eventually(() => timers.Live().Any(timer => timer.DelayMs == 500));
            await harness.AbortTaskAsync(aborted, Context);
            await harness.WaitForTaskAsync(aborted, Context);
            await Eventually(() => timers.Live().Count == 0);
            var kept = await StartNap(harness, shortNap);
            await Eventually(async () => await StatusOf(harness, kept) == DurableTaskStatus.Sleeping);
            Seq([500d], timers.Live().Select(timer => timer.DelayMs), "live timers");
            await harness.CloseAsync(Context);
            Seq([], timers.Live().Select(timer => timer.DelayMs), "live timers after close");
        });

        yield return Case(prefix + "disarms-an-invocation-s-timers-when-it-ends-and-a-late-fire-runs-nothing", async () =>
        {
            var fired = new SyncList<string>();
            var timed = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.timed",
                Version = 1,
                Initial = _ => O("""{"phase":"run"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["run"] = async (_, runtime, ctx) =>
                    {
                        runtime.SetTimer(() => fired.Add("late"), 60_000);
                        await runtime.CommitAsync((_, _) => Completed(null), ctx);
                    },
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var timers = new FakeTimers();
            var (harness, _, _) = await OpenTasks(fresh(), [timed], setTimer: timers.SetTimer);
            var root = await harness.RootAsync(Context);
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(timed, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation)), Context);
            harness.Resume();
            await harness.WaitForTaskAsync(id, Context);
            var timer = timers.Armed.First(armed => armed.DelayMs == 60_000);
            await Eventually(() => timer.Disarmed);
            // A host timer that fires anyway: the ended invocation's callback does not run.
            timer.Callback();
            Seq([], fired.Snapshot(), "fired");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "arms-no-timer-for-sleeps-with-onwake", async () =>
        {
            double clock = 1_000;
            var timers = new FakeTimers();
            var nap = NapTask(500);
            var (harness, _, _) = await OpenTasks(fresh(), [nap], now: () => Volatile.Read(ref clock), setTimer: timers.SetTimer,
                onWake: _ => { });
            var id = await StartNap(harness, nap);
            harness.Resume();
            await Eventually(async () => await StatusOf(harness, id) == DurableTaskStatus.Sleeping);
            await Settle();
            Seq([], timers.Armed.Select(timer => timer.DelayMs), "armed timers");
            Volatile.Write(ref clock, 1_500);
            Equal(new Wake(true), harness.Wake(), "wake once due");
            harness.Resume();
            Equal(TaskOutcomeStatus.Completed, ((SettlingTaskState)(await harness.WaitForTaskAsync(id, Context)).State).Outcome.Status, "outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "arms-a-foreground-timer-the-host-declines-with-settimeout-reporting-it-once", async () =>
        {
            var nap = NapTask(10);
            var (harness, _, reports) = await OpenTasks(fresh(), [nap], setTimer: (_, _, _) => null);
            var first = await StartNap(harness, nap);
            var second = await StartNap(harness, nap);
            harness.Resume();
            Equal(TaskOutcomeStatus.Completed, ((SettlingTaskState)(await harness.WaitForTaskAsync(first, Context)).State).Outcome.Status, "first");
            Equal(TaskOutcomeStatus.Completed, ((SettlingTaskState)(await harness.WaitForTaskAsync(second, Context)).State).Outcome.Status, "second");
            Seq(["HarnessOptions.setTimer declined a timer that paces work; using setTimeout"], reports.Snapshot().Select(error => error.Message),
                "reports");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "counts-neither-a-wait-on-a-sleeping-task-nor-an-outcome-held-over-one-as-active", async () =>
        {
            const double clock = 1_000;
            var child = NapTask(500);
            var parent = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.parent",
                Version = 1,
                Initial = _ => O("""{"phase":"start"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["start"] = (task, runtime, ctx) => runtime.CommitAsync(async (tx, _) =>
                    {
                        var created = await tx.CreateTaskAsync(child, JsonValue.Null, new TaskOptions(TaskOwnership.Task(task.Id)));
                        var joined = JsonValue.Object([new("phase", "joined"), new("child", created.ToJson())]);
                        // Held: completes next, holding its outcome over the sleeping child. Otherwise waits for it.
                        return (TaskState?)(((JsonObjectValue)task.Input)["hold"] is JsonBooleanValue { Value: true }
                            ? NextTaskState.Running(joined)
                            : NextTaskState.Waiting(joined, [created], JoinPolicy.AllSettled));
                    }, ctx),
                    ["joined"] = (_, runtime, ctx) => runtime.CommitAsync((_, _) => Completed(null), ctx),
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            foreach (var hold in new[] { false, true })
            {
                var (harness, _, _) = await OpenTasks(fresh(), [parent, child], now: () => clock, onWake: _ => { });
                var root = await harness.RootAsync(Context);
                var id = await root.CommitAsync(tx => tx.CreateTaskAsync(parent, JsonValue.Object([new("hold", JsonValue.Boolean(hold))]),
                    new TaskOptions(TaskOwnership.Conversation)), Context);
                harness.Resume();
                var parked = hold ? DurableTaskStatus.Completing : DurableTaskStatus.Waiting;
                await Eventually(async () => await StatusOf(harness, id) == parked);
                await Eventually(() => harness.Wake().At == 1_500);
                await Eventually(() => !harness.Wake().Active);
                Equal(new Wake(false, 1_500), harness.Wake(), $"wake (hold {hold})");
                await harness.CloseAsync(Context);
            }
        });

        yield return Case(prefix + "runs-the-abort-handler-of-a-sleeping-task-without-waiting-for-its-time", async () =>
        {
            const double clock = 1_000;
            var nap = NapTask(60_000);
            var (harness, _, _) = await OpenTasks(fresh(), [nap], now: () => clock, onWake: _ => { });
            var id = await StartNap(harness, nap);
            harness.Resume();
            await Eventually(async () => await StatusOf(harness, id) == DurableTaskStatus.Sleeping);
            await harness.AbortTaskAsync(id, Context);
            SameJson("""{"status":"aborted","reason":"test"}""", ((SettlingTaskState)(await harness.WaitForTaskAsync(id, Context)).State).Outcome.ToJson(),
                "outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "rejects-a-sleep-until-something-that-is-not-a-time-and-a-sleep-in-an-abort-handler", async () =>
        {
            var errors = new SyncList<string>();
            var invalid = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.invalid-sleep",
                Version = 1,
                Initial = _ => O("""{"phase":"run"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["run"] = async (_, runtime, ctx) =>
                    {
                        foreach (var until in new[] { double.NaN, -1, double.PositiveInfinity })
                        {
                            try { await runtime.SleepUntilAsync(until, O("""{"phase":"next"}"""), ctx); }
                            catch (Exception error) { errors.Add(error.Message); }
                        }
                        await runtime.CommitAsync((_, _) => Completed(null), ctx);
                    },
                    ["next"] = (_, _, _) => Task.CompletedTask,
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var aborting = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.sleep-in-abort",
                Version = 1,
                Initial = _ => O("""{"phase":"run"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler> { ["run"] = (_, runtime, _) => Aborted(runtime.Signal) },
                Abort = async (_, runtime, ctx) =>
                {
                    try { await runtime.SleepUntilAsync(runtime.Now() + 10, O("""{"phase":"run"}"""), ctx); }
                    catch (Exception error) { errors.Add(error.Message); }
                    await runtime.CommitAsync((_, _) => AbortedWith("test"), ctx);
                },
            });
            var (harness, _, _) = await OpenTasks(fresh(), [invalid, aborting]);
            var root = await harness.RootAsync(Context);
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(invalid, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation)), Context);
            var abortingId = await root.CommitAsync(tx => tx.CreateTaskAsync(aborting, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation)), Context);
            harness.Resume();
            Equal(TaskOutcomeStatus.Completed, ((SettlingTaskState)(await harness.WaitForTaskAsync(id, Context)).State).Outcome.Status, "invalid");
            await harness.AbortTaskAsync(abortingId, Context);
            Equal(TaskOutcomeStatus.Aborted, ((SettlingTaskState)(await harness.WaitForTaskAsync(abortingId, Context)).State).Outcome.Status, "aborting");
            Seq(
            [
                $"Task {id} cannot sleep until NaN: not a time in milliseconds",
                $"Task {id} cannot sleep until -1: not a time in milliseconds",
                $"Task {id} cannot sleep until Infinity: not a time in milliseconds",
                $"Abort handler of task {abortingId} cannot sleep",
            ], errors.Snapshot(), "errors");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "does-not-count-a-task-without-a-definition-as-active", async () =>
        {
            var missing = NapTask(10);
            var (harness, _, _) = await OpenTasks(fresh(), []);
            var root = await harness.RootAsync(Context);
            // Created through a token the registry does not hold: nothing can run it.
            await root.CommitAsync(tx => tx.CreateTaskAsync(missing, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation)), Context);
            await Eventually(() => !harness.Wake().Active);
            Equal(new Wake(false), harness.Wake(), "wake before resume");
            harness.Resume();
            await Settle();
            await Eventually(() => !harness.Wake().Active);
            Equal(new Wake(false), harness.Wake(), "wake after resume");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "reports-a-throwing-onwake-and-offers-the-same-wake-again-at-the-next-pass", async () =>
        {
            double clock = 1_000;
            var wakes = new SyncList<Wake>();
            var failure = new InvalidOperationException("alarm failed");
            var failing = 1;
            var nap = NapTask(1_000);
            var (harness, registry, reports) = await OpenTasks(fresh(), [nap], now: () => Volatile.Read(ref clock), onWake: wake =>
            {
                wakes.Add(wake);
                if (Volatile.Read(ref failing) == 1 && wake.At is not null) throw failure;
            });
            var id = await StartNap(harness, nap);
            harness.Resume();
            await Eventually(() => wakes.Snapshot().Any(wake => wake.At == 2_000 && !wake.Active));
            await Eventually(() => reports.Snapshot().Contains(failure));
            // The host recovers; an unrelated change offers the same, undelivered wake again.
            Volatile.Write(ref failing, 0);
            var offered = wakes.Count;
            registry.Install(Define.Extension(new Extension { Name = "unrelated" }));
            await Eventually(() => wakes.Count > offered);
            Equal(new Wake(false, 2_000), wakes.Snapshot()[^1], "offered again");
            Volatile.Write(ref clock, 2_000);
            harness.Resume();
            await harness.WaitForTaskAsync(id, Context);
            await Eventually(() => wakes.Snapshot()[^1] is { Active: false, At: null });
            await harness.CloseAsync(Context);
        });
    }
}
