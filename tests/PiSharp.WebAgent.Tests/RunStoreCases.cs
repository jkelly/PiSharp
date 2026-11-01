// Decision 0011 (run W4, R0): runs.db. In process over a temporary file: its own migration (id 1, once, never in webagent.db), retention
// (30 days by default; pinned runs, their parents and a pinned parent's runs kept; running runs never removed; null keeps everything), raw
// payloads removed after seven days whatever the run's retention (the run's raw state becomes expired) and never read after them even
// before a cleanup (a fixed clock), a store opened after a stop marks another process's running runs interrupted, the history query
// (filters, search, paging, totals), pins, deletes, and the redaction of captured requests (headers, query parameters, the planted key
// wherever it appears). RunRetention, the hosted service: it cleans at start and every interval on the app's clock with the saved
// settings, a failed pass is logged and the next one runs; and the built app cleans a seeded runs.db when it starts.
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Spend;

internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> RunStoreCases() =>
    [
        Case("runs.store.migration-applies-once-in-runs-db", () =>
        {
            var root = Temp("runs-store-migration");
            try
            {
                var path = Path.Join(root, RunsDb.FileName);
                var db = new RunsDb(path);
                db.Db.Migrate();
                db.Db.Migrate();
                Equal("1:runs: runs, events, raw payloads,2:runs: response model", string.Join(",", ReadDb(path, "SELECT id, name FROM schema_migrations ORDER BY id", reader => $"{reader.GetInt64(0)}:{reader.GetString(1)}")),
                    "its migrations (W5 E4 added 2), each applied once");
                var tables = ReadDb(path, "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name", reader => reader.GetString(0));
                Check(tables.Contains("runs") && tables.Contains("run_events") && tables.Contains("run_raw"), "its tables: " + string.Join(",", tables));
                Equal(2, RunsMigrations.All.Count, "runs.db's own list");
                Check(MigrationArea.All.All(area => area.Name != "runs"), "not a webagent.db area");
                Equal(("presets", "compare"), (MigrationArea.Of(400)!.Name, MigrationArea.Of(500)!.Name), "W4's webagent.db ranges");
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("runs.store.retention-removes-old-unpinned-runs-and-keeps-pinned-ones", () =>
        {
            var root = Temp("runs-store-retention");
            try
            {
                var store = new RunStore(new RunsDb(Path.Join(root, RunsDb.FileName)));
                var now = DateTimeOffset.UtcNow;
                var old = now.AddDays(-40);
                void Add(string id, DateTimeOffset created, string status = RunStatus.Completed, bool pinned = false, string? parent = null, string kind = RunKind.Call)
                {
                    store.Insert(RunsRow(id, created, status, kind) with { Pinned = pinned, ParentId = parent });
                }
                Add("old", old);
                Add("old-pinned", old, pinned: true);
                Add("old-running", old, RunStatus.Running);
                Add("recent", now.AddDays(-2));
                Add("edge", now.AddDays(-29));
                Add("old-parent", old, kind: RunKind.Parent);
                Add("old-child-pinned", old, pinned: true, parent: "old-parent");
                Add("old-parent-pinned", old, kind: RunKind.Parent, pinned: true);
                Add("old-child", old, parent: "old-parent-pinned");
                Add("old-parent-plain", old, kind: RunKind.Parent);
                Add("old-child-plain", old, parent: "old-parent-plain");

                var cleanup = store.Cleanup(RunHistorySettings.CreateDefault(), now);
                Equal(3, cleanup.Runs, "the old unpinned runs are removed");
                var left = Ids(store);
                Equal("edge,old-child,old-child-pinned,old-parent,old-parent-pinned,old-pinned,old-running,recent", left,
                    "pinned runs, their parents, a pinned parent's runs, running runs and recent runs stay");
                Equal(0, store.Cleanup(RunHistorySettings.CreateDefault(), now).Runs, "a second cleanup removes nothing");

                Equal(0, store.Cleanup(new RunHistorySettings(RetentionDays: null), now.AddYears(5)).Runs, "null keeps runs forever");
                Equal(2, store.Cleanup(new RunHistorySettings(RetentionDays: 1), now).Runs, "a shorter retention removes more");
                Equal("old-child,old-child-pinned,old-parent,old-parent-pinned,old-pinned,old-running", Ids(store), "the 29-day and 2-day runs went");

                Check(store.Pin("old-pinned", false), "unpinned");
                Equal(1, store.Cleanup(RunHistorySettings.CreateDefault(), now).Runs, "then it expires like any other");
                Throws<ArgumentException>(() => new RunHistorySettings(RetentionDays: 0).Normalize(), "at least a day");
                Throws<ArgumentException>(() => new RunHistorySettings(RetentionDays: RunHistorySettings.MaxRetentionDays + 1).Normalize(), "at most ten years");
                Equal((30, false, true), (RunHistorySettings.CreateDefault().RetentionDays!.Value, RunHistorySettings.CreateDefault().StoreRawPayloads,
                    RunHistorySettings.CreateDefault().StoreSseEvents), "the defaults: 30 days, raw payloads off, events on");
                Equal("history", RunHistorySettings.SectionName, "the settings section");
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("runs.store.raw-payloads-expire-after-seven-days", () =>
        {
            var root = Temp("runs-store-raw");
            try
            {
                var path = Path.Join(root, RunsDb.FileName);
                var now = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
                var clock = new RunsClock(now);
                var store = new RunStore(new RunsDb(path), time: clock);
                var week = TimeSpan.FromDays(RunHistorySettings.RawRetentionDays);
                foreach (var (id, age) in new[] { ("raw-old", TimeSpan.FromDays(8)), ("raw-past", week + TimeSpan.FromHours(1)), ("raw-new", week - TimeSpan.FromHours(1)) })
                    RunsWithRaw(store, id, now - age);
                int OnDisk(string id) => ReadDb(path, $"SELECT COUNT(*) FROM run_raw WHERE run_id = '{id}'", reader => reader.GetInt32(0)).Single();

                // Never read past seven days, though a cleanup has not removed them yet.
                Equal((RunRawState.Expired, 0), (store.Get("raw-past")!.RawState, store.Raw("raw-past").Count), "a payload 7 days and 1 hour old is not returned");
                Equal(RunRawState.Expired, store.List(new RunQuery()).Items.Single(item => item.Id == "raw-past").RawState, "History reads it as expired too");
                Equal(2, OnDisk("raw-past"), "until the next cleanup removes it");
                Equal((RunRawState.Stored, 2), (store.Get("raw-new")!.RawState, store.Raw("raw-new").Count), "one an hour short of seven days is");

                var cleanup = store.Cleanup(RunHistorySettings.CreateDefault(), now);
                Equal((0, 2), (cleanup.Runs, cleanup.RawRuns), "the runs stay; their raw payloads go");
                Equal((0, 0), (OnDisk("raw-old"), OnDisk("raw-past")), "removed from runs.db");
                Equal((RunRawState.Expired, 0), (store.Get("raw-old")!.RawState, store.Raw("raw-old").Count), "expired");
                Equal(1, store.Events("raw-old").Count, "its events stay");
                Equal((RunRawState.Stored, 2), (store.Get("raw-new")!.RawState, store.Raw("raw-new").Count), "a newer run keeps them");

                clock.Advance(TimeSpan.FromHours(2));
                Equal((RunRawState.Expired, 0), (store.Get("raw-new")!.RawState, store.Raw("raw-new").Count), "and stops showing them once they are seven days old");
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("runs.retention.cleans-at-start-and-every-interval-with-the-saved-settings", async () =>
        {
            var root = Temp("runs-retention-service");
            var now = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
            var clock = new RunsClock(now);
            var logs = new ConcurrentQueue<string>();
            ServiceProvider? provider = null;
            try
            {
                var options = new WebAgentOptions { DataDirectory = Path.Join(root, "data"), Faux = true };
                options.EnsureDirectories();
                var services = new ServiceCollection();
                services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug).AddProvider(new ListLoggerProvider(logs)));
                services.AddSingleton<TimeProvider>(clock);
                services.AddWebAgentCore(options);
                services.AddWebAgentFallbacks(options);
                services.AddSpendArea(options);
                services.AddRunsArea(options);
                services.AddSingleton<ISettingsStore>(registered => new RunsFlakySettings(new SettingsStore(registered.GetRequiredService<WebAgentDb>())));
                provider = services.BuildServiceProvider();
                provider.GetRequiredService<WebAgentDb>().Migrate();
                var settings = (RunsFlakySettings)provider.GetRequiredService<ISettingsStore>();
                var store = provider.GetRequiredService<RunStore>();
                store.Insert(RunsRow("old", now.AddDays(-40)));
                store.Insert(RunsRow("old-pinned", now.AddDays(-40)) with { Pinned = true });
                store.Insert(RunsRow("two-days", now.AddDays(-2)));
                RunsWithRaw(store, "raw-old", now.AddDays(-8));
                RunsWithRaw(store, "raw-new", now.AddHours(-1));
                int OnDisk(string id) => ReadDb(store.Db.Path, $"SELECT COUNT(*) FROM run_raw WHERE run_id = '{id}'", reader => reader.GetInt32(0)).Single();

                var retention = provider.GetServices<IHostedService>().OfType<RunRetention>().Single();
                settings.FailOnce();
                await retention.StartAsync(CancellationToken.None);
                await WaitUntilAsync(() => Task.FromResult(logs.Any(line => line.Contains("Run history cleanup failed", StringComparison.Ordinal)) && clock.Waiting > 0),
                    "the first pass fails, is logged, and the next one is due");
                Check(store.Get("old") is not null && OnDisk("raw-old") == 2, "the failed pass removed nothing");

                clock.Advance(RunRetention.Interval);
                await WaitUntilAsync(() => Task.FromResult(store.Get("old") is null), "the next pass runs on time and removes the expired run");
                Equal("old-pinned,raw-new,raw-old,two-days", Ids(store), "pinned and recent runs stay");
                Equal((0, RunRawState.Expired), (OnDisk("raw-old"), store.Get("raw-old")!.RawState), "raw payloads past seven days are removed");
                Equal((2, RunRawState.Stored), (OnDisk("raw-new"), store.Get("raw-new")!.RawState), "newer ones stay");
                Check(retention.ExecuteTask is { IsCompleted: false }, "the service keeps going after a failed pass");

                await settings.SaveAsync(new RunHistorySettings(RetentionDays: 1));
                Equal(2, retention.CleanNow().Runs, "a cleanup reads the saved retention (one day)");
                Equal("old-pinned,raw-new", Ids(store), "the two-day and eight-day runs went; the pinned run stays");
                await retention.StopAsync(CancellationToken.None);
                Check(retention.ExecuteTask?.IsCompleted == true && clock.Waiting == 0, "it stops with the app");
            }
            finally
            {
                if (provider is not null) await provider.DisposeAsync();
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("runs.app.cleans-runs-db-when-it-starts", async () =>
        {
            var root = Temp("runs-app-retention");
            try
            {
                var data = Path.Join(root, "data");
                Directory.CreateDirectory(data);
                var now = DateTimeOffset.UtcNow;
                var seed = new RunStore(new RunsDb(Path.Join(data, RunsDb.FileName)));
                seed.Insert(RunsRow("old", now.AddDays(-40)));
                seed.Insert(RunsRow("old-pinned", now.AddDays(-40)) with { Pinned = true });
                seed.Insert(RunsRow("recent", now.AddDays(-2)));
                RunsWithRaw(seed, "raw-old", now.AddDays(-8));
                RunsWithRaw(seed, "raw-new", now.AddDays(-1));
                SqliteClearPools();
                await using var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = data, Arguments = ["--faux"] });
                await WaitUntilAsync(async () => (await app.Get("/api/runs/old")).Status == 404, "the expired run is removed at start");
                Equal((200, 200), ((await app.Get("/api/runs/old-pinned")).Status, (await app.Get("/api/runs/recent")).Status), "pinned and recent runs stay");
                await WaitUntilAsync(() => Task.FromResult(ReadDb(Path.Join(data, RunsDb.FileName), "SELECT COUNT(*) FROM run_raw WHERE run_id = 'raw-old'",
                    reader => reader.GetInt32(0)).Single() == 0), "raw payloads past seven days are removed at start");
                var expired = await app.Get("/api/runs/raw-old/raw");
                Equal(("expired", 0), (expired.Body!["rawState"]!.GetValue<string>(), expired.Body["items"]!.AsArray().Count), "and read as expired: " + expired.Text);
                var kept = await app.Get("/api/runs/raw-new/raw");
                Equal(("stored", 2), (kept.Body!["rawState"]!.GetValue<string>(), kept.Body["items"]!.AsArray().Count), "a newer run keeps its payloads: " + kept.Text);
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("runs.store.a-new-process-marks-running-runs-interrupted", () =>
        {
            var root = Temp("runs-store-interrupted");
            try
            {
                var path = Path.Join(root, RunsDb.FileName);
                var before = new RunStore(new RunsDb(path));
                before.Insert(RunsRow("left-running", DateTimeOffset.UtcNow, RunStatus.Running));
                before.Insert(RunsRow("done", DateTimeOffset.UtcNow));
                Equal(0, before.InterruptStale(), "a store never interrupts its own runs");
                Equal(RunStatus.Running, before.Get("left-running")!.Status, "still running");

                var after = new RunStore(new RunsDb(path));
                var row = after.Get("left-running")!;
                Equal((RunStatus.Interrupted, RunStore.InterruptedMessage), (row.Status, row.Error), "the next process marks it interrupted");
                Check(row.Finished is not null, "and ended");
                Equal(RunStatus.Completed, after.Get("done")!.Status, "a finished run is untouched");
                Equal(0, after.InterruptStale(), "once");
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("runs.store.history-query-pins-and-deletes", () =>
        {
            var root = Temp("runs-store-query");
            try
            {
                var store = new RunStore(new RunsDb(Path.Join(root, RunsDb.FileName)));
                var now = DateTimeOffset.UtcNow;
                for (var index = 0; index < 5; index++)
                {
                    store.Insert(RunsRow("p" + index, now.AddMinutes(index), RunStatus.Running) with { Title = "playground " + index });
                    store.Finish("p" + index, new RunOutcome
                    {
                        Status = index == 4 ? RunStatus.Error : RunStatus.Completed,
                        Error = index == 4 ? "boom" : null,
                        Usage = new RunUsage(10, 5, 0, 0, 15),
                        CostMicros = 100,
                        TtftMs = 50,
                        Finished = now.AddMinutes(index),
                    });
                }
                store.Insert(RunsRow("chat-call", now, RunStatus.Completed) with { Source = "chat", Session = "chat-1", Title = "about llamas" });
                var page = store.List(new RunQuery { Source = "playground", Limit = 2 });
                Equal((5L, "p4,p3"), (page.Total, string.Join(",", page.Items.Select(item => item.Id))), "newest first, paged");
                Equal("p2,p1", string.Join(",", store.List(new RunQuery { Source = "playground", Limit = 2, Offset = 2 }).Items.Select(item => item.Id)), "the next page");
                Equal("chat-call", store.List(new RunQuery { Search = "llama" }).Items.Single().Id, "search");
                Equal("p4", store.List(new RunQuery { Status = RunStatus.Error }).Items.Single().Id, "by status");
                var totals = store.Totals(new RunQuery { Source = "playground" });
                Equal((5L, 1L, 500L, 50.0), (totals.Count, totals.Errors, totals.CostMicros, totals.InputTokens), "totals");
                Throws<ArgumentException>(() => store.List(new RunQuery { Limit = RunQuery.MaxLimit + 1 }), "a page has at most " + RunQuery.MaxLimit);

                Check(store.Pin("p1", true) && store.Get("p1")!.Pinned, "pinned");
                Check(!store.Pin("no-such-run", true), "an unknown run");
                Equal("p1", store.List(new RunQuery { Pinned = true }).Items.Single().Id, "pinned filter");
                Equal(2, store.Delete(["p0", "p2", "no-such-run"]), "deleted what exists");
                Equal(3, store.Clear(keepPinned: true), "clear keeps the pinned run");
                Equal("p1", Ids(store), "only the pinned run is left");
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("runs.capture.redacts-credentials-wherever-they-appear", async () =>
        {
            var key = Planted("sk-proj-");
            var capture = new RunCapture(() => 7);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.example.test/v1/responses?key={key}&alt=sse")
            {
                Content = new StringContent($$"""{"input":"echo {{key}} back","model":"m"}""", System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Headers.TryAddWithoutValidation("x-goog-api-key", key);
            request.Headers.TryAddWithoutValidation("x-run-test", "visible");
            await capture.RequestAsync(request, CancellationToken.None);
            capture.Add("sse", $$"""data: {"echo":"{{key}}"}""");
            var items = capture.Items();
            var text = string.Join("\n", items.Select(item => item.Data));
            NoSecret(text, "the captured request", key);
            Check(text.Contains("visible", StringComparison.Ordinal) && text.Contains("alt=sse", StringComparison.Ordinal), "the rest stays: " + text);
            Check(text.Contains(RunRedaction.Redacted, StringComparison.Ordinal), "redacted in place");
            Equal($"{{\"input\":\"echo {key} back\",\"model\":\"m\"}}", await request.Content!.ReadAsStringAsync(), "the request itself is unchanged");
            Check(RunSpec.SecretName("X-Api-Key") && RunSpec.SecretName("cookie") && !RunSpec.SecretName("x-run-test"), "credential header names");
        }),
    ];

    private static RunRecord RunsRow(string id, DateTimeOffset created, string status = RunStatus.Completed, string kind = RunKind.Call) => new()
    {
        Id = id,
        Op = "op-" + id,
        Kind = kind,
        Source = "playground",
        Status = status,
        Provider = "faux",
        Model = "echo",
        Created = created,
        Started = created,
        Updated = created,
    };

    /// <summary>Inserts the finished run <paramref name="id"/> with two raw payload items stored at <paramref name="at"/>.</summary>
    private static void RunsWithRaw(RunStore store, string id, DateTimeOffset at)
    {
        store.Insert(RunsRow(id, at, RunStatus.Running));
        store.Finish(id, new RunOutcome
        {
            Status = RunStatus.Completed,
            Finished = at,
            Events = [new RunEventRecord(1, 5, new JsonObject { ["type"] = "start" })],
            Raw = [new RunRawItem(1, "request", 0, "{}"), new RunRawItem(2, "sse", 4, "data: {}")],
        });
    }

    /// <summary>A clock that moves only when told to, with timers that fire when it passes their time (Task.Delay on it waits for that).</summary>
    internal sealed class RunsClock(DateTimeOffset start) : TimeProvider
    {
        private readonly Lock gate = new();
        private readonly List<Timer> timers = [];
        private DateTimeOffset now = start;

        /// <summary>How many timers wait to fire.</summary>
        public int Waiting
        {
            get
            {
                lock (gate) return timers.Count(timer => timer.Due is not null);
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (gate) return now;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            lock (gate) timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        /// <summary>Moves the clock on by <paramref name="by"/> and fires every timer that is then due.</summary>
        public void Advance(TimeSpan by)
        {
            List<Timer> due;
            lock (gate)
            {
                now += by;
                due = [.. timers.Where(timer => timer.Due is { } at && at <= now)];
                foreach (var timer in due) timer.Due = timer.Period is { } period ? now + period : null;
            }
            foreach (var timer in due) timer.Fire();
        }

        private sealed class Timer(RunsClock clock, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? Due { get; set; }

            public TimeSpan? Period { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock.gate)
                {
                    if (!clock.timers.Contains(this)) return false;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.now + dueTime;
                    Period = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : period;
                }
                return true;
            }

            public void Fire() => callback(state);

            public void Dispose()
            {
                lock (clock.gate) clock.timers.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>The settings store, with run history's section failing once when told to (a cleanup pass that fails).</summary>
    private sealed class RunsFlakySettings(ISettingsStore inner) : ISettingsStore
    {
        private int fail;

        public void FailOnce() => Volatile.Write(ref fail, 1);

        public T Get<T>() where T : class, ISettingsSection<T> =>
            typeof(T) == typeof(RunHistorySettings) && Interlocked.Exchange(ref fail, 0) == 1
                ? throw new IOException("webagent.db could not be read (test).") : inner.Get<T>();

        public Task<T> SaveAsync<T>(T value, CancellationToken cancellationToken = default) where T : class, ISettingsSection<T> =>
            inner.SaveAsync(value, cancellationToken);

        public Task<T> UpdateAsync<T>(Func<T, T> change, CancellationToken cancellationToken = default) where T : class, ISettingsSection<T> =>
            inner.UpdateAsync(change, cancellationToken);

        public Task<T> ResetAsync<T>(CancellationToken cancellationToken = default) where T : class, ISettingsSection<T> => inner.ResetAsync<T>(cancellationToken);

        public event EventHandler<SettingsChangedEventArgs>? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }
    }

    private static string Ids(RunStore store) =>
        string.Join(",", store.List(new RunQuery { Limit = RunQuery.MaxLimit }).Items.Select(item => item.Id).Order(StringComparer.Ordinal));
}
