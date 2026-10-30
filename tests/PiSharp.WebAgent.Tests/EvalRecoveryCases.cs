using System.Text.Json.Nodes;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Evals;
using PiSharp.WebAgent.Evals.Store;
using PiSharp.WebAgent.Evals.Suites;
using PiSharp.WebAgent.Runs;

// Decision 0011 (run W5, T5): a suite run when the server dies, with the real server process. The built app runs on the priced faux provider
// (--faux-metered: offline, Metered at a list price, reporting usage) over a case folder, and each case kills its process tree (Process.Kill,
// no shutdown: a crash) and starts it again on the same data. A suite run is not durable: the run engine marks its cut-off calls interrupted
// and never sends them again, and the suite runner, at the next start, marks the run interrupted (its finished answers stay, its unfinished
// ones are cancelled, the holds of the calls it never sent are released). So a crash can only end a run, never repeat or double it:
//   - killed with four answers done, four streaming a "slow:" answer and four not yet asked, the run is interrupted with 4 done and 8
//     cancelled results; runs.db keeps its 8 calls (4 completed, 4 interrupted) and gains none; the ledger has one row per call (the 4
//     answered settled at their actual cost, the 4 cut off uncertain, as the Spend Guard keeps any dropped stream until the user resolves it)
//     and every hold released, so the run's cost (the done answers' actual costs) is counted once; the next Run now is run #2, complete on
//     its own rows, and one more crash leaves both runs and every ledger row as they were;
//   - a schedule whose slots were missed while the app was down (its EnabledAt moved 13 hours back while the app was stopped: an "every
//     6 hours" schedule turned on earlier) starts one run for its latest slot when the app starts (missed slots collapse into one); killed in the
//     middle of that run and started again, the run is interrupted and the slot keeps its one "started" decision: the scheduler never fires
//     a second run for it, and a third start changes nothing either.
// Nothing here calls a provider.
internal static partial class Program
{
    /// <summary>The words of a faux <c>slow:</c> answer long enough (about 12 s) to be cut off by a kill.</summary>
    private static readonly string RecSlowPrompt = "slow: " + string.Join(' ', Enumerable.Range(1, 300).Select(index => $"w{index:000}"));

    internal static IEnumerable<(string, Func<Task>)> EvalRecoveryCases() =>
    [
        Case("evals.recovery.a-suite-run-cut-off-by-a-crash-is-interrupted-and-counted-once", async () =>
        {
            await using var app = await RecApp.StartAsync("rec-suite-run");
            var suiteId = await app.SuiteAsync("Crash test", k: 2, targets: ["echo", "echo-small"]);
            await app.CaseAsync(suiteId, "Quick question", "Is 1.0.0-rc.1 valid SemVer?", "Echo");
            await app.CaseAsync(suiteId, "Long answer", RecSlowPrompt, "w300");
            await app.CaseAsync(suiteId, "Another quick one", "Name a prime number.", "Echo");

            var runId = await app.RunNowAsync(suiteId);
            // Answers run four at a time in the order case, target, repetition: the first case's four finish at once, the long case's four
            // stream for about 12 seconds, and the third case's four wait for a free slot.
            JsonNode view = new JsonObject();
            await WaitUntilAsync(async () =>
            {
                view = await app.RunViewAsync(runId);
                var results = RecResults(view);
                return results.Count(result => result.Status == "done") == 4 &&
                    results.Count(result => result.Status == "running" && result.Call is not null) == 4 &&
                    results.Count(result => result.Status == "queued") == 4;
            }, "four answers done, four streaming and four waiting", TimeSpan.FromSeconds(30));
            var streaming = RecResults(view).Where(result => result.Status == "running").Select(result => result.Call!).ToHashSet(StringComparer.Ordinal);
            var answered = RecResults(view).Where(result => result.Status == "done").Select(result => result.Call!).ToHashSet(StringComparer.Ordinal);
            await WaitUntilAsync(async () =>
            {
                var calls = (await app.Get("/api/runs?group=" + runId + "&limit=100")).Body?["items"]?.AsArray() ?? [];
                return calls.Count(call => call?["status"]?.GetValue<string>() == "running" && streaming.Contains(call["id"]!.GetValue<string>())) == 4;
            }, "the four long answers stream", TimeSpan.FromSeconds(30));

            await app.KillAsync();
            var before = app.Ledger(runId);
            Check(before.Where(row => row.Op.StartsWith(SuiteRunner.HoldPrefix, StringComparison.Ordinal)).Count(row => row.State == "reserved") == 4,
                "at the crash, the holds of the four answers never asked are still reserved: " + RecRows(before));
            Check(before.Count(row => !row.Op.StartsWith(SuiteRunner.HoldPrefix, StringComparison.Ordinal) && row.State == "reserved") == 4,
                "and the four streaming calls are reserved: " + RecRows(before));

            await app.StartAgainAsync();
            await WaitUntilAsync(async () => (await app.RunViewAsync(runId))["run"]?["status"]?.GetValue<string>() == "interrupted",
                "the run is marked interrupted at the start", TimeSpan.FromSeconds(30));
            // Anything the app would send again would show within a few seconds; nothing may.
            await Task.Delay(TimeSpan.FromSeconds(3));
            view = await app.RunViewAsync(runId);
            var run = view["run"]!;
            Equal(SuiteRunner.InterruptedProblem, run["problem"]?.GetValue<string>(), "the interrupted run says so");
            Check(view["running"]?.GetValue<bool>() == false, "nothing of it is going");
            var after = RecResults(view);
            Equal(4, after.Count(result => result.Status == "done"), "the four finished answers stay");
            Equal(8, after.Count(result => result.Status == "cancelled"), "the cut-off and unasked answers are cancelled");
            Check(after.Where(result => result.Status == "done").All(result => result.Verdict == "passed" && answered.Contains(result.Call!)),
                "the done answers are the ones answered before the crash, still checked");
            Check(after.Where(result => result.Status == "cancelled").All(result => result.Call is null || streaming.Contains(result.Call)),
                "no cancelled answer was asked after the crash");
            Equal(1, (await app.SuiteViewAsync(suiteId))["runs"]!.AsArray().Count, "still one run: the crash started none");

            await app.KillAsync();
            var calls = app.Calls(runId);
            Equal(8, calls.Count, "runs.db keeps the 8 calls made before the crash and has no new one: " + string.Join(", ", calls.Select(call => call.Status)));
            Check(calls.Where(call => answered.Contains(call.Id)).All(call => call.Status == RunStatus.Completed), "the answered calls completed");
            Check(calls.Where(call => streaming.Contains(call.Id)).All(call => call.Status == RunStatus.Interrupted), "the cut-off calls are interrupted, not sent again");
            var ledger = app.Ledger(runId);
            var holds = ledger.Where(row => row.Op.StartsWith(SuiteRunner.HoldPrefix, StringComparison.Ordinal)).ToList();
            var callRows = ledger.Where(row => !row.Op.StartsWith(SuiteRunner.HoldPrefix, StringComparison.Ordinal)).ToList();
            Equal(12, holds.Count, "one hold per answer the run planned");
            Check(holds.All(row => row.State == "released" && row.Counted == 0), "every hold is released and counts nothing: " + RecRows(holds));
            Equal(8, callRows.Count, "one ledger row per call, none for a call sent again: " + RecRows(callRows));
            Check(callRows.All(row => !row.Op.Contains('~', StringComparison.Ordinal)), "no op was reserved a second time");
            var settled = callRows.Where(row => row.State == "settled").ToList();
            var uncertain = callRows.Where(row => row.State == "uncertain").ToList();
            Check(settled.Count == 4 && settled.All(row => row.Actual > 0), "the four answered calls settled at their actual cost: " + RecRows(callRows));
            Check(uncertain.Count == 4 && uncertain.All(row => row.Counted > 0),
                "the four cut-off calls are uncertain and still counted until the user resolves them: " + RecRows(callRows));
            var stored = app.SuiteRun(runId);
            Equal(settled.Sum(row => row.Actual), stored.CostMicros, "the run's cost is its answered calls' actual costs, each counted once");
            Equal(SuiteRunStatus.Interrupted, stored.Status, "stored interrupted");

            // The next run is a run of its own.
            await app.StartAgainAsync();
            var next = await app.RunNowAsync(suiteId);
            await WaitUntilAsync(async () => (await app.RunViewAsync(next))["run"]?["status"]?.GetValue<string>() == "complete", "run #2 completes",
                TimeSpan.FromSeconds(60));
            var second = await app.RunViewAsync(next);
            Equal(2, second["run"]?["number"]?.GetValue<int>(), "run #2");
            Check(RecResults(second).Count == 12 && RecResults(second).All(result => result.Status == "done" && result.Verdict == "passed"), "every answer of run #2 passed");

            // One more crash changes nothing that ended.
            await app.KillAsync();
            var firstRows = app.Ledger(runId);
            var secondRows = app.Ledger(next);
            await app.StartAgainAsync();
            Equal("interrupted", (await app.RunViewAsync(runId))["run"]?["status"]?.GetValue<string>(), "run #1 stays interrupted");
            Equal("complete", (await app.RunViewAsync(next))["run"]?["status"]?.GetValue<string>(), "run #2 stays complete");
            await app.KillAsync();
            Equal(RecRows(firstRows), RecRows(app.Ledger(runId)), "run #1's ledger rows are as they were");
            Equal(RecRows(secondRows), RecRows(app.Ledger(next)), "run #2's ledger rows are as they were");
            Check(secondRows.Where(row => !row.Op.StartsWith(SuiteRunner.HoldPrefix, StringComparison.Ordinal)).All(row => row.State == "settled") &&
                secondRows.Where(row => row.Op.StartsWith(SuiteRunner.HoldPrefix, StringComparison.Ordinal)).All(row => row.State == "released"),
                "run #2's calls settled and its holds released: " + RecRows(secondRows));
            Equal(20, app.Calls(runId).Count + app.Calls(next).Count, "8 calls in run #1 and 12 in run #2, nothing more");
        }),

        Case("evals.recovery.a-crash-during-a-scheduled-run-never-fires-its-slot-again", async () =>
        {
            await using var app = await RecApp.StartAsync("rec-slot");
            var suiteId = await app.SuiteAsync("Hourly", k: 2, targets: ["echo"]);
            await app.CaseAsync(suiteId, "Long answer", RecSlowPrompt, "w300");
            // Every 6 hours from three hours ago (UTC), so the latest slot is about three hours back and the next about three hours ahead:
            // no slot falls due while the case runs.
            var at = DateTimeOffset.UtcNow.AddHours(-3).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            var saved = await app.PostAsync($"{EvalRoutes.ApiSuites}/{suiteId}/schedule", new JsonObject
            {
                ["enabled"] = true, ["repeat"] = "hours", ["everyHours"] = 6, ["at"] = at, ["timeZone"] = "UTC", ["capMicros"] = 5_000_000,
            });
            Check(saved["saved"]?.GetValue<bool>() == true, "every 6 hours, with a $5 suite-run cap: " + saved.ToJsonString());

            // Turned on 13 hours ago, while the app was down: the slots 9 and 3 hours ago were missed.
            await app.KillAsync();
            Equal(0, app.RunCount(suiteId), "saving the schedule ran nothing: its slots so far are before it was turned on");
            var now = DateTimeOffset.UtcNow;
            var enabledAt = now.AddHours(-13);
            app.EditSuite(suiteId, suite => suite with { Schedule = suite.Schedule! with { EnabledAt = enabledAt } });
            var slot = app.StoredSuite(suiteId).Schedule!.Latest(now)!.Value;
            Check(slot > now.AddHours(-4) && slot < now.AddHours(-2), $"the latest slot {slot:u} is the one about three hours ago");

            await app.StartAgainAsync();
            string runId = "";
            await WaitUntilAsync(async () =>
            {
                var runs = (await app.SuiteViewAsync(suiteId))["runs"]!.AsArray();
                runId = runs.Count == 1 ? runs[0]!["id"]!.GetValue<string>() : "";
                return runId.Length > 0;
            }, "the scheduler starts the missed slot's run at the start", TimeSpan.FromSeconds(30));
            var started = (await app.RunViewAsync(runId))["run"]!;
            Check(started["trigger"]?.GetValue<string>() == "schedule" && started["slot"] is not null && DateTimeOffset.Parse(started["slot"]!.GetValue<string>(),
                System.Globalization.CultureInfo.InvariantCulture) == slot, "a scheduled run for the latest slot: " + started.ToJsonString());
            await WaitUntilAsync(async () => RecResults(await app.RunViewAsync(runId)).Count(result => result.Status == "running" && result.Call is not null) == 2,
                "both answers stream", TimeSpan.FromSeconds(30));
            await app.KillAsync();
            var slots = app.Slots(suiteId);
            Check(slots.Count == 1 && slots[0].Slot == slot && slots[0].Outcome == "started" && slots[0].Run == runId,
                "one decision, for the latest slot only (the missed ones collapsed into it): " + string.Join(", ", slots));

            await app.StartAgainAsync();
            await WaitUntilAsync(async () => (await app.RunViewAsync(runId))["run"]?["status"]?.GetValue<string>() == "interrupted",
                "the cut-off run is interrupted", TimeSpan.FromSeconds(30));
            // The scheduler looks at the slot right after marking the run; give it time to (wrongly) start another.
            await Task.Delay(TimeSpan.FromSeconds(3));
            Equal(1, (await app.SuiteViewAsync(suiteId))["runs"]!.AsArray().Count, "no second run");
            Check((await app.SuiteViewAsync(suiteId))["overview"]?["activeRun"] is null, "nothing is going");
            await app.KillAsync();
            var afterSlots = app.Slots(suiteId);
            Check(afterSlots.Count == 1 && afterSlots[0].Outcome == "started" && afterSlots[0].Run == runId,
                "the slot keeps its one started decision: " + string.Join(", ", afterSlots));
            Equal(2, app.Calls(runId).Count, "the two cut-off calls, not sent again");
            Check(app.Calls(runId).All(call => call.Status == RunStatus.Interrupted), "both interrupted");
            Equal(1, app.RunCount(suiteId), "one run in webagent.db");

            // A third start changes nothing.
            await app.StartAgainAsync();
            await Task.Delay(TimeSpan.FromSeconds(3));
            Equal(1, (await app.SuiteViewAsync(suiteId))["runs"]!.AsArray().Count, "still one run after a third start");
            await app.KillAsync();
            Equal(1, app.Slots(suiteId).Count, "still one slot decision");
            Equal(2, app.Calls(runId).Count, "still two calls");
            Check(app.StoredSuite(suiteId).Schedule is { Active: true }, "the schedule stays on for its next slot");
        }),
    ];

    /// <summary>A suite result as the run's JSON gives it.</summary>
    private sealed record RecResult(string Status, string? Verdict, string? Call);

    /// <summary>A ledger row of a suite run (webagent.db's spend_reservations).</summary>
    private sealed record RecLedgerRow(string Op, string State, long Counted, long? Actual)
    {
        public override string ToString() => $"{Op}:{State}:{Counted}:{Actual}";
    }

    /// <summary>A call of a suite run (runs.db's runs).</summary>
    private sealed record RecCall(string Id, string Status);

    /// <summary>A schedule slot decision (webagent.db's eval_schedule_slots).</summary>
    private sealed record RecSlot(DateTimeOffset Slot, string Outcome, string? Run);

    private static List<RecResult> RecResults(JsonNode view) =>
        [.. (view["results"]?.AsArray() ?? []).Select(item => new RecResult(item!["status"]!.GetValue<string>(), item["verdict"]?.GetValue<string>(),
            item["callRunId"]?.GetValue<string>()))];

    private static string RecRows(IEnumerable<RecLedgerRow> rows) => string.Join(", ", rows.OrderBy(row => row.Op, StringComparer.Ordinal));

    /// <summary>The app on <c>--faux-metered</c> as a child process over a case folder, which <see cref="KillAsync"/> crashes and
    /// <see cref="StartAgainAsync"/> starts again on the same data; while it is stopped, its databases are read (and a suite edited) directly.
    /// Disposing stops it and removes the folder.</summary>
    private sealed class RecApp : IAsyncDisposable
    {
        private readonly AppStart start;
        private WebAgentApp? app;

        private RecApp(string root, AppStart start)
        {
            Root = root;
            this.start = start;
        }

        public string Root { get; }

        private string Data => Path.Join(Root, "data");

        private WebAgentApp App => app ?? throw new InvalidOperationException("The app is stopped.");

        public static async Task<RecApp> StartAsync(string name)
        {
            var root = Temp(name);
            var recovery = new RecApp(root, new AppStart { DataDirectory = Path.Join(root, "data"), Arguments = ["--faux-metered"] });
            try
            {
                await recovery.StartAgainAsync();
                return recovery;
            }
            catch
            {
                await recovery.DisposeAsync();
                throw;
            }
        }

        public async Task StartAgainAsync()
        {
            Check(app is null, "the app is stopped before it starts again");
            app = await WebAgentApp.StartAsync(Root, start);
        }

        /// <summary>Kills the app's process tree (no shutdown, as in a crash).</summary>
        public async Task KillAsync()
        {
            if (app is null) return;
            await app.DisposeAsync();
            app = null;
            SqliteClearPools();
        }

        public Task<(int Status, JsonNode? Body, string Text, HttpResponseMessage Response)> Get(string path) => App.Get(path);

        public async Task<JsonNode> PostAsync(string path, JsonObject body)
        {
            var answer = await App.SendAsync(HttpMethod.Post, path, body, origin: App.Origin);
            Equal(200, answer.Status, "POST " + path + ": " + answer.Text);
            return answer.Body!;
        }

        public async Task<JsonNode> GetJsonAsync(string path)
        {
            var answer = await App.Get(path);
            Equal(200, answer.Status, "GET " + path + ": " + answer.Text);
            return answer.Body!;
        }

        /// <summary>Creates a suite on faux <paramref name="targets"/>; returns its id.</summary>
        public async Task<string> SuiteAsync(string name, int k, string[] targets)
        {
            var id = EvalIds.New(EvalIds.Suite);
            var created = await PostAsync(EvalRoutes.ApiSuites, new JsonObject
            {
                ["id"] = id, ["name"] = name, ["k"] = k,
                ["targets"] = new JsonArray([.. targets.Select(model => (JsonNode)new JsonObject { ["provider"] = "faux", ["model"] = model })]),
            });
            Check(created["created"]?.GetValue<bool>() == true, "the suite is made: " + created.ToJsonString());
            return id;
        }

        /// <summary>Adds a case asking faux/echo <paramref name="prompt"/>, checked for <paramref name="contains"/>.</summary>
        public async Task CaseAsync(string suiteId, string name, string prompt, string contains)
        {
            var created = await PostAsync(EvalRoutes.ApiCases, new JsonObject
            {
                ["id"] = EvalIds.New(EvalIds.Case), ["suiteId"] = suiteId, ["name"] = name,
                ["spec"] = new JsonObject
                {
                    ["provider"] = "faux", ["model"] = "echo", ["maxTokens"] = 512,
                    ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = prompt }),
                },
                ["checks"] = new JsonArray(new JsonObject { ["kind"] = "contains", ["text"] = contains }),
            });
            Check(created["created"]?.GetValue<bool>() == true, "the case is made: " + created.ToJsonString());
        }

        /// <summary>Run now; returns the run's id.</summary>
        public async Task<string> RunNowAsync(string suiteId)
        {
            var started = await PostAsync($"{EvalRoutes.ApiSuites}/{suiteId}/run", new JsonObject());
            Check(started["posted"]?.GetValue<bool>() == true, "Run now started a run: " + started.ToJsonString());
            return started["run"]!["id"]!.GetValue<string>();
        }

        public Task<JsonNode> RunViewAsync(string runId) => GetJsonAsync(EvalRoutes.ApiRuns + "/" + runId);

        public Task<JsonNode> SuiteViewAsync(string suiteId) => GetJsonAsync(EvalRoutes.ApiSuites + "/" + suiteId);

        // ----- the stopped app's databases -----

        private string WebAgentDbPath => Path.Join(Data, "webagent.db");

        /// <summary>The ledger rows of the suite run <paramref name="runId"/> (its holds and its calls).</summary>
        public List<RecLedgerRow> Ledger(string runId)
        {
            Check(app is null, "read while the app is stopped");
            return ReadDb(WebAgentDbPath, $"SELECT op, state, counted_micros, actual_micros FROM spend_reservations WHERE run = '{runId}' ORDER BY op",
                reader => new RecLedgerRow(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetInt64(3)));
        }

        /// <summary>The runs.db calls of the suite run <paramref name="runId"/> (its group).</summary>
        public List<RecCall> Calls(string runId)
        {
            Check(app is null, "read while the app is stopped");
            return ReadDb(Path.Join(Data, RunsDb.FileName), $"SELECT id, status FROM runs WHERE group_id = '{runId}' ORDER BY id",
                reader => new RecCall(reader.GetString(0), reader.GetString(1)));
        }

        public List<RecSlot> Slots(string suiteId)
        {
            Check(app is null, "read while the app is stopped");
            return ReadDb(WebAgentDbPath, $"SELECT slot, outcome, run_id FROM eval_schedule_slots WHERE suite_id = '{suiteId}' ORDER BY slot",
                reader => new RecSlot(DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        public int RunCount(string suiteId)
        {
            Check(app is null, "read while the app is stopped");
            return ReadDb(WebAgentDbPath, $"SELECT COUNT(*) FROM eval_runs WHERE suite_id = '{suiteId}'", reader => reader.GetInt32(0)).Single();
        }

        /// <summary>The stored suite run <paramref name="runId"/>, read through the eval store.</summary>
        public SuiteRun SuiteRun(string runId) => Store(store => store.Run(runId)) ?? throw new InvalidOperationException("No suite run " + runId);

        public Suite StoredSuite(string suiteId) => Store(store => store.Suite(suiteId)) ?? throw new InvalidOperationException("No suite " + suiteId);

        /// <summary>Changes the stored suite while the app is stopped (what an earlier session left in webagent.db).</summary>
        public void EditSuite(string suiteId, Func<Suite, Suite> edit) =>
            Check(Store(store => store.UpdateSuite(edit(store.Suite(suiteId)!))), "the suite is saved");

        private T Store<T>(Func<EvalStore, T> read)
        {
            Check(app is null, "the store is opened while the app is stopped");
            try
            {
                return read(new EvalStore(new WebAgentDb(WebAgentDbPath, [])));
            }
            finally
            {
                SqliteClearPools();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await KillAsync();
            DeleteTemp(Root);
        }
    }
}
