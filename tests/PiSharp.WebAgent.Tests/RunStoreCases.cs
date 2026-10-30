// Decision 0011 (run W4, R0): runs.db. In process over a temporary file: its own migration (id 1, once, never in webagent.db), retention
// (30 days by default; pinned runs, their parents and a pinned parent's runs kept; running runs never removed; null keeps everything), raw
// payloads removed after seven days whatever the run's retention (the run's raw state becomes expired), a store opened after a stop marks
// another process's running runs interrupted, the history query (filters, search, paging, totals), pins, deletes, and the redaction of
// captured requests (headers, query parameters, the planted key wherever it appears).
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Runs;

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
                Equal("1:runs: runs, events, raw payloads", string.Join(",", ReadDb(path, "SELECT id, name FROM schema_migrations", reader => $"{reader.GetInt64(0)}:{reader.GetString(1)}")),
                    "one migration, applied once");
                var tables = ReadDb(path, "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name", reader => reader.GetString(0));
                Check(tables.Contains("runs") && tables.Contains("run_events") && tables.Contains("run_raw"), "its tables: " + string.Join(",", tables));
                Equal(1, RunsMigrations.All.Count, "runs.db's own list");
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
                var store = new RunStore(new RunsDb(Path.Join(root, RunsDb.FileName)));
                var now = DateTimeOffset.UtcNow;
                foreach (var (id, age) in new[] { ("raw-old", 8), ("raw-new", 2) })
                {
                    store.Insert(RunsRow(id, now.AddDays(-age), RunStatus.Running));
                    store.Finish(id, new RunOutcome
                    {
                        Status = RunStatus.Completed,
                        Finished = now.AddDays(-age),
                        Events = [new RunEventRecord(1, 5, new JsonObject { ["type"] = "start" })],
                        Raw = [new RunRawItem(1, "request", 0, "{}"), new RunRawItem(2, "sse", 4, "data: {}")],
                    });
                }
                Equal((RunRawState.Stored, 2), (store.Get("raw-old")!.RawState, store.Raw("raw-old").Count), "stored");
                var cleanup = store.Cleanup(RunHistorySettings.CreateDefault(), now);
                Equal((0, 1), (cleanup.Runs, cleanup.RawRuns), "the run stays; its raw payloads go");
                Equal((RunRawState.Expired, 0), (store.Get("raw-old")!.RawState, store.Raw("raw-old").Count), "expired");
                Equal(1, store.Events("raw-old").Count, "its events stay");
                Equal((RunRawState.Stored, 2), (store.Get("raw-new")!.RawState, store.Raw("raw-new").Count), "a newer run keeps them");
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

    private static string Ids(RunStore store) =>
        string.Join(",", store.List(new RunQuery { Limit = RunQuery.MaxLimit }).Items.Select(item => item.Id).Order(StringComparer.Ordinal));
}
