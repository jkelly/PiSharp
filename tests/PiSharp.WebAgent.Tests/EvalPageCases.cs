using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Web.Common;
using PiSharp.WebAgent.Evals;

// Decision 0011 (run W5, E6): the Evals pages in the built app (--faux, or --faux-metered where a suite needs a price; a temporary data
// folder; no provider is called): Leaderboard, Suites, a suite, a suite run and Drift, each in the states the mockups draw (empty, default,
// provisional, running, paused), and the fragments their scripts swap into the modal shells (_SuiteScheduleModal, _BaselineModal,
// _CheckEditorModal). Every page is a shell page with Evals the active sidebar item, its tab marked, no inline script and no Razor text;
// the Evals placeholder is gone. Data is made only through the Suites, Cases and Arena JSON APIs a browser uses, never written to the
// pages. The schedule modal refuses a cap below the suite's worst case, and a suite whose worst case outgrows its cap shows its schedule
// paused on the Suites and Drift pages. A case that starts failing after the baseline is a suspect on the run and Drift pages (Run now to
// confirm), and the next run's page shows the drift it confirmed (Accept as baseline).
internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> EvalPageCases() =>
    [
        Case("evals.pages.empty-states-nav-and-not-found", async () =>
        {
            await using var app = await WebAgentApp.StartAsync("ev-pages-empty");

            var home = await app.Get(EvalRoutes.Home);
            Equal(200, home.Status, "/evals: " + Cut(home.Text));
            Equal(EvalRoutes.Leaderboard, home.Response.RequestMessage?.RequestUri?.AbsolutePath, "/evals goes to the leaderboard");

            var leaderboard = await EvPageAsync(app, EvalRoutes.Leaderboard, "Leaderboard");
            Check(leaderboard.Contains("data-evals=\"leaderboard\" data-state=\"empty\"", StringComparison.Ordinal), "the leaderboard is empty: " + Cut(leaderboard));
            Check(leaderboard.Contains("data-leaderboard-empty", StringComparison.Ordinal) && leaderboard.Contains("No votes yet", StringComparison.Ordinal), "_LeaderboardEmpty");
            Check(WebUtility.HtmlDecode(leaderboard).Contains("href=\"/compare?blind=1\"", StringComparison.Ordinal), "Blind vote opens Compare with Blind on");
            Check(!leaderboard.Contains("data-leaderboard-table", StringComparison.Ordinal) && !leaderboard.Contains("data-position-bias", StringComparison.Ordinal),
                "no table and no bias line without votes");

            var suites = await EvPageAsync(app, EvalRoutes.Suites, "Suites");
            Check(suites.Contains("data-evals=\"suites\" data-state=\"empty\"", StringComparison.Ordinal) && suites.Contains("data-suites-empty", StringComparison.Ordinal) &&
                suites.Contains("No suites yet", StringComparison.Ordinal), "_SuitesEmpty");
            Check(!suites.Contains("data-suite-card", StringComparison.Ordinal), "no card");
            Check(suites.Contains("id=\"modal-suite-schedule\"", StringComparison.Ordinal) && suites.Contains("id=\"modal-spend-confirm\"", StringComparison.Ordinal),
                "the schedule and spend modal shells");

            var drift = await EvPageAsync(app, EvalRoutes.Drift, "Drift");
            Check(drift.Contains("data-evals=\"drift\" data-state=\"empty\"", StringComparison.Ordinal) && drift.Contains("data-drift-empty", StringComparison.Ordinal) &&
                drift.Contains("No baselines yet", StringComparison.Ordinal), "_DriftEmpty");
            Check(!drift.Contains("data-price-changes", StringComparison.Ordinal), "no price list while nothing changed and no suite is watched");

            foreach (var (path, what) in new[] { (EvalRoutes.Suite("su_missing"), "suite"), (EvalRoutes.Run("run_missing"), "run") })
            {
                var missing = await app.Get(path);
                Equal(404, missing.Status, path);
                Check(missing.Text.Contains($"data-eval-not-found=\"{what}\"", StringComparison.Ordinal), path + " says so: " + Cut(missing.Text));
                CheckShell(path, missing.Text);
                NoRazorText(missing.Text, path);
                Check(Regex.IsMatch(missing.Text, "<a class=\"nav-link active\" aria-current=\"page\" href=\"/evals/leaderboard\" data-nav=\"evals\">"), path + ": Evals is the active item");
            }
            foreach (var fragment in new[] { "/evals/suites/su_missing/schedule", "/evals/suites/su_missing/baseline", "/evals/cases/ca_missing/check", "/evals/runs/run_missing/live" })
                Equal(404, (await app.Get(fragment)).Status, fragment);
            Equal(404, (await app.Get(EvalRoutes.Leaderboard + "/votes.csv")).Status, "no votes CSV without a tag");

            foreach (var asset in new[] { "/js/evals.js", "/css/evals.css" })
                Equal(200, (await app.Get(asset)).Status, asset);
            Check((await app.Get("/css/evals.css")).Text.Contains("[data-evals] [hidden], [data-eval-modal] [hidden] { display: none !important; }", StringComparison.Ordinal),
                "hidden wins over Bootstrap's display utilities on the Evals pages and modals");
            Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
        }),

        Case("evals.pages.leaderboard-tags-default-provisional-bias-and-csv", async () =>
        {
            await using var app = await WebAgentApp.StartAsync("ev-pages-board");
            var battles = new List<string>();
            // Ten votes tagged coding give every model ten comparisons (the provisional threshold): a settled board. One tagged writing does not.
            foreach (var tag in Enumerable.Repeat("coding", 10).Append("writing"))
            {
                var created = await app.SendAsync(HttpMethod.Post, EvalRoutes.ApiArena, ArenaDraftJson("What is two plus two? (" + tag + " " + battles.Count + ")", ArenaThree),
                    origin: app.Origin);
                Check(created.Status == 200 && created.Body?["posted"]?.GetValue<bool>() == true, "battle posted: " + created.Text);
                var id = created.Body!["battleId"]!.GetValue<string>();
                await WaitUntilAsync(async () => (await app.Get($"{EvalRoutes.ApiArena}/{id}")).Body?["status"]?.GetValue<string>() == "ready", "the battle is ready",
                    TimeSpan.FromSeconds(60));
                var vote = await app.SendAsync(HttpMethod.Post, $"{EvalRoutes.ApiArena}/{id}/vote", new JsonObject { ["kind"] = "pick", ["pick"] = "B", ["tag"] = tag },
                    origin: app.Origin);
                Check(vote.Status == 200 && vote.Body?["status"]?.GetValue<string>() == "voted", "voted: " + vote.Text);
                battles.Add(id);
            }

            var page = await EvPageAsync(app, EvalRoutes.Leaderboard, "Leaderboard");
            Check(page.Contains("data-evals=\"leaderboard\" data-state=\"default\" data-mode=\"request\" data-tag=\"coding\"", StringComparison.Ordinal),
                "the first tag's board, settled after ten votes: " + Cut(Regex.Match(page, "<div class=\"page\"[^>]*>").Value));
            Equal(3, Regex.Matches(page, "data-leaderboard-row").Count, "a row per model");
            Check(page.Contains("data-tag-link=\"coding\"", StringComparison.Ordinal) && page.Contains("data-tag-link=\"writing\"", StringComparison.Ordinal), "the tag menu lists both tags");
            Check(!page.Contains("data-provisional", StringComparison.Ordinal), "no provisional chip on a settled board");
            Check(WebUtility.HtmlDecode(page).Contains("data-leaderboard-summary>10 votes", StringComparison.Ordinal), "its votes: " +
                Regex.Match(page, "data-leaderboard-summary>[^<]*").Value);
            var decoded = WebUtility.HtmlDecode(page);
            Check(Regex.IsMatch(decoded, "data-position-bias>You picked the left answer \\d+% of the time\\."), "the left-position line: " +
                Regex.Match(decoded, "data-position-bias>[^<]*").Value);
            Check(decoded.Contains("href=\"/evals/leaderboard/votes.csv?mode=request&tag=coding\"", StringComparison.Ordinal), "Export votes for this tag");
            Check(!page.Contains("data-leaderboard-empty", StringComparison.Ordinal), "not empty");

            var writing = await EvPageAsync(app, EvalRoutes.LeaderboardOf("writing"), "Leaderboard");
            Check(writing.Contains("data-state=\"provisional\" data-mode=\"request\" data-tag=\"writing\"", StringComparison.Ordinal), "?tag=writing shows the writing board, provisional");
            Check(writing.Contains("data-provisional", StringComparison.Ordinal) && writing.Contains("data-position-bias", StringComparison.Ordinal), "the provisional chip and the bias line");
            Equal(3, Regex.Matches(writing, "data-leaderboard-row").Count, "a row per model");
            Check(Regex.IsMatch(writing, "aria-current=\"true\" href=\"[^\"]*\" data-tag-link=\"writing\""), "writing is the chosen tag");
            Check(writing.Contains("data-tag-menu>Tag: writing</button>", StringComparison.Ordinal), "the tag menu names it");
            var unknown = await app.Get(EvalRoutes.LeaderboardOf("no-such-tag"));
            Check(unknown.Status == 200 && unknown.Text.Contains("data-tag=\"coding\"", StringComparison.Ordinal), "an unvoted tag shows the first board");

            var csv = await app.Get(EvalRoutes.Leaderboard + "/votes.csv?mode=request&tag=coding");
            Equal(200, csv.Status, "the votes CSV");
            Equal("text/csv", csv.Response.Content.Headers.ContentType?.MediaType, "as CSV");
            var lines = csv.Text.TrimStart('\uFEFF').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Equal("voted_at,mode,tag,battle,vote,best,ranking,A,B,C,D", lines[0], "the header");
            Equal(11, lines.Length, "the ten votes of the tag, and none of another");
            var first = lines.Single(line => line.Contains("," + battles[0] + ",", StringComparison.Ordinal));
            Check(first.Contains(",request,coding," + battles[0] + ",pick,B,", StringComparison.Ordinal) && first.Contains("faux/echo", StringComparison.Ordinal),
                "the vote, its battle and its models: " + first);
            Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
        }),

        Case("evals.pages.schedule-modal-refuses-a-cap-below-the-worst-case-and-pauses", async () =>
        {
            var root = Temp("ev-pages-schedule");
            try
            {
                await using var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = Path.Join(root, "data"), Arguments = ["--faux-metered"] });
                var (suiteId, caseIds) = await EvSuiteAsync(app, "Schedule suite", slow: false);
                var api = EvalRoutes.ApiSuites + "/" + suiteId;
                var modal = "/evals/suites/" + suiteId + "/schedule";

                var noCap = await EvFragmentAsync(app, modal, "id=\"modal-suite-schedule\"");
                Check(noCap.Contains("data-refused=\"no-cap\"", StringComparison.Ordinal) && noCap.Contains("<div data-ss-pane=\"ss-refused\">", StringComparison.Ordinal),
                    "no cap yet: refused, a schedule needs one");
                Check(noCap.Contains("<div data-ss-pane=\"ss-daily\">", StringComparison.Ordinal) && noCap.Contains("<div data-ss-pane=\"ss-hours\" hidden=\"hidden\">", StringComparison.Ordinal),
                    "daily first");

                var low = await EvFragmentAsync(app, modal + "?cap=0.000001", "id=\"modal-suite-schedule\"");
                Check(low.Contains("data-refused=\"over-cap\"", StringComparison.Ordinal) && low.Contains("data-ss-refused=\"over-cap\"", StringComparison.Ordinal),
                    "a $0.000001 cap is below the worst case: " + Cut(Regex.Match(low, "data-ss-cap-status.{0,900}", RegexOptions.Singleline).Value));
                Check(low.Contains("<div data-ss-pane=\"ss-refused\">", StringComparison.Ordinal) && low.Contains("<div data-ss-pane=\"ss-fits\" hidden=\"hidden\">", StringComparison.Ordinal),
                    "the refused pane shows, the fits pane is hidden");
                Check(low.Contains("Schedule refused", StringComparison.Ordinal) && WebUtility.HtmlDecode(low).Contains("Raise the cap to at least", StringComparison.Ordinal), "why it is refused");
                Check(low.Contains("data-ss-save disabled=\"disabled\">Save schedule", StringComparison.Ordinal), "Save schedule is disabled");
                Check(low.Contains("form-control is-invalid\" id=\"ssCap\"", StringComparison.Ordinal) && low.Contains("value=\"0.000001\"", StringComparison.Ordinal), "the cap field is marked");
                Equal(400, (await app.Get(modal + "?cap=lots")).Status, "a cap that is not an amount");

                var refused = await EvPostAsync(app, api + "/schedule", new JsonObject
                    { ["enabled"] = true, ["repeat"] = "daily", ["at"] = "07:00", ["timeZone"] = "UTC", ["capMicros"] = 1 });
                Check(refused["saved"]?.GetValue<bool>() == false && refused["refused"]?["code"]?.GetValue<string>() == "over-cap", "the server refuses it too: " + refused.ToJsonString());
                var worst = refused["estimate"]!["worstMicros"]!.GetValue<long>();
                Check(worst > 1, "a priced worst case");
                Check(low.Contains($"data-worst-micros=\"{worst.ToString(CultureInfo.InvariantCulture)}\"", StringComparison.Ordinal), "the modal's worst case is the server's");

                var below = await EvFragmentAsync(app, modal + "?cap=" + EvDollars(worst - 1), "id=\"modal-suite-schedule\"");
                Check(below.Contains("data-refused=\"over-cap\"", StringComparison.Ordinal), "a micro-dollar below the worst case is refused");
                var fits = await EvFragmentAsync(app, modal + "?cap=" + EvDollars(worst), "id=\"modal-suite-schedule\"");
                // A data- attribute is never left out by Razor: no refusal is an empty one.
                Check(fits.Contains("data-refused=\"\">", StringComparison.Ordinal) && fits.Contains("<div data-ss-pane=\"ss-fits\">", StringComparison.Ordinal) &&
                    fits.Contains("<span class=\"ps-chip\" data-tone=\"ok\">fits</span>", StringComparison.Ordinal), "a cap equal to the worst case fits: " +
                    Cut(Regex.Match(fits, "data-ss-cap-status.{0,900}", RegexOptions.Singleline).Value));
                Check(fits.Contains("data-ss-save>Save schedule", StringComparison.Ordinal) && fits.Contains("<div data-ss-pane=\"ss-refused\" hidden=\"hidden\">", StringComparison.Ordinal),
                    "Save schedule is enabled");
                Check(!fits.Contains("data-ss-off", StringComparison.Ordinal), "no Turn off while the schedule is off");

                var at = DateTime.UtcNow.AddHours(3).ToString("HH:00", CultureInfo.InvariantCulture);
                var saved = await EvPostAsync(app, api + "/schedule", new JsonObject
                    { ["enabled"] = true, ["repeat"] = "hours", ["everyHours"] = 6, ["at"] = at, ["timeZone"] = "UTC", ["capMicros"] = worst });
                Check(saved["saved"]?.GetValue<bool>() == true, "saved: " + saved.ToJsonString());
                var hours = await EvFragmentAsync(app, modal, "id=\"modal-suite-schedule\"");
                Check(hours.Contains("<div data-ss-pane=\"ss-hours\">", StringComparison.Ordinal) && hours.Contains("data-ss-repeat=\"hours\" aria-pressed=\"true\"", StringComparison.Ordinal),
                    "the saved repeat shows");
                Check(Regex.IsMatch(WebUtility.HtmlDecode(hours), "data-ss-day-text>4 runs a day can reserve up to \\$"), "4 runs a day and what they can reserve");
                Check(hours.Contains("id=\"ssHours\" type=\"number\" min=\"1\"", StringComparison.Ordinal) && hours.Contains("value=\"6\"", StringComparison.Ordinal), "every 6 hours");
                Check(hours.Contains($"value=\"{EvDollars(worst)}\" aria-describedby=\"ssCapStatus\"", StringComparison.Ordinal), "the saved cap reads back to the micro-dollar");
                Check(hours.Contains("data-enabled=\"true\"", StringComparison.Ordinal) && hours.Contains("data-ss-off", StringComparison.Ordinal), "Turn off");
                Check(hours.Contains("data-time-zone=\"UTC\"", StringComparison.Ordinal), "the saved time zone");

                var listed = await EvPageAsync(app, EvalRoutes.Suites, "Suites");
                Check(listed.Contains("data-evals=\"suites\" data-state=\"default\"", StringComparison.Ordinal) && listed.Contains($"data-suite-card=\"{suiteId}\"", StringComparison.Ordinal),
                    "the suite's card");
                Check(listed.Contains($"data-run-suite=\"{suiteId}\" title", StringComparison.Ordinal) || listed.Contains($"data-run-suite=\"{suiteId}\">", StringComparison.Ordinal),
                    "Run now is enabled: " + Regex.Match(listed, "data-run-suite=[^>]*>").Value);
                Check(!listed.Contains("data-schedule-paused", StringComparison.Ordinal) && !listed.Contains("data-paused", StringComparison.Ordinal), "not paused");
                Check(WebUtility.HtmlDecode(listed).Contains("next: ", StringComparison.Ordinal), "the next slot");

                // A third target makes the worst case outgrow the cap: the Suites unit pauses the schedule.
                var grown = await EvPostAsync(app, api, new JsonObject
                {
                    ["targets"] = new JsonArray([.. new[] { "echo", "echo-small", "echo-thinking" }.Select(model => (JsonNode)new JsonObject { ["provider"] = "faux", ["model"] = model })]),
                });
                Equal("over-cap", grown["suite"]?["schedule"]?["paused"]?["code"]?.GetValue<string>(), "paused: " + grown.ToJsonString());

                var paused = await EvPageAsync(app, EvalRoutes.Suites, "Suites");
                Check(paused.Contains("data-evals=\"suites\" data-state=\"paused\"", StringComparison.Ordinal) && paused.Contains($"data-schedule-paused=\"{suiteId}\"", StringComparison.Ordinal),
                    "the Suites page's paused notice");
                var pausedText = WebUtility.HtmlDecode(paused);
                Check(pausedText.Contains("The Schedule suite schedule is paused. Its worst case of $", StringComparison.Ordinal), "why: " +
                    Regex.Match(pausedText, "The Schedule suite schedule.{0,160}").Value);
                Check(paused.Contains("See the price change", StringComparison.Ordinal) && paused.Contains($"data-open-schedule=\"{suiteId}\">Raise cap", StringComparison.Ordinal) &&
                    paused.Contains("data-paused>paused</span>", StringComparison.Ordinal), "Raise cap, and the card's paused chip");

                var pausedModal = await EvFragmentAsync(app, modal, "id=\"modal-suite-schedule\"");
                Check(pausedModal.Contains("Saving the schedule with a cap that fits resumes it.", StringComparison.Ordinal) && pausedModal.Contains("data-refused=\"over-cap\"", StringComparison.Ordinal),
                    "the modal says it is paused, and the saved cap is refused now");

                var drift = await EvPageAsync(app, EvalRoutes.Drift, "Drift");
                Check(drift.Contains("data-evals=\"drift\" data-state=\"paused\"", StringComparison.Ordinal) && drift.Contains($"data-drift-suite=\"{suiteId}\"", StringComparison.Ordinal) &&
                    drift.Contains($"data-schedule-paused=\"{suiteId}\"", StringComparison.Ordinal) && drift.Contains("schedule paused</span>", StringComparison.Ordinal),
                    "Drift shows the scheduled suite paused");
                Equal(3, Regex.Matches(drift, "data-drift-target=").Count, "a row per target");
                Check(drift.Contains("data-price-changes", StringComparison.Ordinal) && drift.Contains("data-no-price-changes", StringComparison.Ordinal), "no catalog price change recorded");

                var suitePage = await EvPageAsync(app, EvalRoutes.Suite(suiteId), null);
                Check(Regex.IsMatch(WebUtility.HtmlDecode(suitePage), "<span class=\"ps-chip ms-1\" data-tone=\"warn\" title=\"Its worst case of \\$[^\"]+\">paused</span>"),
                    "the suite header's paused chip");
                Equal(3, Regex.Matches(suitePage, "data-target-row").Count, "three targets");
                Check(caseIds.Count == 1 && suitePage.Contains($"data-case-row=\"{caseIds[0]}\"", StringComparison.Ordinal), "its case");
                Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("evals.pages.suite-run-check-editor-baseline-and-drift", async () =>
        {
            var root = Temp("ev-pages-run");
            try
            {
                await using var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = Path.Join(root, "data"), Arguments = ["--faux-metered"] });
                var (suiteId, caseIds) = await EvSuiteAsync(app, "SemVer suite", slow: true);
                var api = EvalRoutes.ApiSuites + "/" + suiteId;
                var refused = await EvPostAsync(app, api + "/schedule", new JsonObject
                    { ["enabled"] = true, ["repeat"] = "daily", ["at"] = "07:00", ["timeZone"] = "UTC", ["capMicros"] = 1 });
                var worst = refused["estimate"]!["worstMicros"]!.GetValue<long>();
                Check((await EvPostAsync(app, api, new JsonObject { ["capMicros"] = worst }))["suite"]?["capMicros"]?.GetValue<long>() == worst, "a suite-run cap");

                var suitePath = EvalRoutes.Suite(suiteId);
                var suite = await EvPageAsync(app, suitePath, null);
                Check(suite.Contains($"data-evals=\"suite\" data-suite-id=\"{suiteId}\" data-state=\"default\"", StringComparison.Ordinal), "the suite page: " +
                    Regex.Match(suite, "<div class=\"page\"[^>]*>").Value);
                Check(suite.Contains("<h1 class=\"app-title current fs-7 mb-0 text-truncate\">SemVer suite</h1>", StringComparison.Ordinal), "the breadcrumb ends with the suite");
                Equal(2, Regex.Matches(suite, "data-target-row").Count, "two targets");
                Equal(2, Regex.Matches(suite, "data-no-baseline>no baseline</span>").Count, "no target has a baseline yet");
                Equal(2, Regex.Matches(suite, "data-case-row=").Count, "two cases");
                Check(suite.Contains($"data-edit-check data-case-id=\"{caseIds[0]}\" data-check-id=\"k1\"", StringComparison.Ordinal), "the first case's check opens the editor");
                foreach (var shell in new[] { "modal-suite-schedule", "modal-baseline", "modal-check-editor", "modal-spend-confirm" })
                    Check(suite.Contains($"id=\"{shell}\"", StringComparison.Ordinal), "the " + shell + " shell");
                Check(suite.Contains("data-no-runs", StringComparison.Ordinal) && suite.Contains("None yet", StringComparison.Ordinal), "no run and no baseline yet");
                Check(suite.Contains($"data-open-baseline=\"{suiteId}\" disabled=\"disabled\"", StringComparison.Ordinal), "Set baseline waits for a run");
                Check(!suite.Contains("data-suite-running", StringComparison.Ordinal), "not running");

                var editor = "/evals/cases/" + caseIds[0] + "/check";
                var contains = await EvFragmentAsync(app, editor + "?check=k1", "id=\"modal-check-editor\"");
                Check(contains.Contains($"data-case-id=\"{caseIds[0]}\" data-check-id=\"k1\" data-suite-id=\"{suiteId}\" data-kind=\"contains\"", StringComparison.Ordinal),
                    "the check being edited: " + Cut(Regex.Match(contains, "data-modal-content[^>]*>").Value));
                Check(contains.Contains("<div data-ce-pane=\"ce-contains\">", StringComparison.Ordinal) && contains.Contains("<div data-ce-pane=\"ce-judge\" hidden=\"hidden\">", StringComparison.Ordinal),
                    "its pane shows, the others are hidden");
                Check(contains.Contains("id=\"ceContainsText\" value=\"SemVer\"", StringComparison.Ordinal), "its text");
                Check(contains.Contains("Edit check", StringComparison.Ordinal) && contains.Contains("Remove check", StringComparison.Ordinal), "Edit check, with Remove");
                Check(contains.Contains("no reference output to try the check on", StringComparison.Ordinal), "a case without a reference output has nothing to try");
                Equal(6, Regex.Matches(contains, "data-ce-pane=\"ce-").Count, "one pane per check kind");
                var judge = await EvFragmentAsync(app, editor + "?kind=judge", "id=\"modal-check-editor\"");
                Check(judge.Contains("Add check", StringComparison.Ordinal) && !judge.Contains("Remove check", StringComparison.Ordinal), "a new check");
                Check(judge.Contains("<div data-ce-pane=\"ce-judge\">", StringComparison.Ordinal) && judge.Contains("id=\"cej-provider\"", StringComparison.Ordinal) &&
                    judge.Contains("data-ce-judge-select", StringComparison.Ordinal), "the judge pane with Provider then Model");
                Check(judge.Contains("data-ce-try hidden=\"hidden\"", StringComparison.Ordinal), "a judge is not tried here");
                Equal(404, (await app.Get(editor + "?check=k9")).Status, "a check the case does not have");

                var noRuns = await EvFragmentAsync(app, "/evals/suites/" + suiteId + "/baseline", "id=\"modal-baseline\"");
                Check(noRuns.Contains("data-bl-no-runs", StringComparison.Ordinal) && noRuns.Contains("data-bl-save disabled=\"disabled\"", StringComparison.Ordinal), "nothing to set as the baseline yet");

                // Run now (the confirm's answer, if the Spend Guard asks), then the pages while it runs.
                var runId = EvalIds.New(EvalIds.Run);
                var started = await EvPostAsync(app, api + "/run", new JsonObject { ["runId"] = runId });
                if (started["posted"]?.GetValue<bool>() != true && started["spend"] is not null)
                    started = await EvPostAsync(app, api + "/run", new JsonObject { ["runId"] = runId, ["spend"] = new JsonObject { ["choice"] = "proceed-once" } });
                Check(started["posted"]?.GetValue<bool>() == true && started["url"]?.GetValue<string>() == EvalRoutes.Run(runId), "started: " + started.ToJsonString());

                var runPath = EvalRoutes.Run(runId);
                var running = await EvPageAsync(app, runPath, null);
                Check(running.Contains($"data-evals=\"run\" data-run-id=\"{runId}\" data-suite-id=\"{suiteId}\"", StringComparison.Ordinal), "the run page");
                Check(running.Contains("data-state=\"running\" data-running=\"true\"", StringComparison.Ordinal) && running.Contains($"data-cancel-run=\"{runId}\"", StringComparison.Ordinal),
                    "running, with Cancel run: " + Cut(Regex.Match(running, "<div data-eval-run-live[^>]*>").Value));
                Check(!running.Contains("<tfoot>", StringComparison.Ordinal) && !running.Contains("Set as baseline", StringComparison.Ordinal), "no totals and no baseline while it runs");
                Check(running.Contains("data-run-grid", StringComparison.Ordinal) && running.Contains("<h1 class=\"app-title current", StringComparison.Ordinal), "the grid and the breadcrumb");
                var live = await app.Get(runPath + "/live");
                Check(live.Status == 200 && live.Text.TrimStart().StartsWith("<div data-eval-run-live", StringComparison.Ordinal) && !live.Text.Contains("<!doctype", StringComparison.Ordinal),
                    "the live fragment: " + Cut(live.Text));
                var suiteRunning = await EvPageAsync(app, suitePath, null);
                Check(suiteRunning.Contains($"data-state=\"running\" data-active-run=\"{runId}\"", StringComparison.Ordinal) &&
                    suiteRunning.Contains($"data-suite-running=\"{runId}\"", StringComparison.Ordinal), "the suite page shows the running card");
                var cardRunning = await EvPageAsync(app, EvalRoutes.Suites, "Suites");
                Check(WebUtility.HtmlDecode(cardRunning).Contains($"href=\"{runPath}\"><span class=\"status-dot live me-1\" aria-hidden=\"true\"></span>Running…</a>", StringComparison.Ordinal),
                    "the card links to the run while it goes");

                await WaitUntilAsync(async () => (await app.Get(EvalRoutes.ApiRuns + "/" + runId)).Body?["run"]?["status"]?.GetValue<string>() is "complete" or "failed" or "cancelled",
                    "the run ends", TimeSpan.FromSeconds(75));
                var done = await EvPageAsync(app, runPath, null);
                Check(done.Contains("data-state=\"default\" data-running=\"false\" data-run-status=\"complete\"", StringComparison.Ordinal), "complete: " +
                    Cut(Regex.Match(done, "<div data-eval-run-live[^>]*>").Value));
                Equal(2, Regex.Matches(done, "data-run-column=").Count, "a column per target");
                Equal(2, Regex.Matches(done, "<tr data-case-row=").Count, "a row per case");
                Equal(4, Regex.Matches(done, "<td data-cell-state=").Count, "case × target cells");
                Check(done.Contains("<tfoot>", StringComparison.Ordinal) && done.Contains("Pass rate", StringComparison.Ordinal) && done.Contains("Response model", StringComparison.Ordinal),
                    "the totals");
                Check(Regex.IsMatch(WebUtility.HtmlDecode(done), "data-run-passed>8 of 8 answers<"), "every answer passed: " + Regex.Match(done, "data-run-passed>[^<]*").Value);
                Check(done.Contains($"data-open-baseline=\"{suiteId}\" data-run-id=\"{runId}\"", StringComparison.Ordinal) && done.Contains("Run again", StringComparison.Ordinal),
                    "Set as baseline and Run again");
                Check(!done.Contains("data-run-signal", StringComparison.Ordinal), "no drift without a baseline");
                Check(done.Contains("data-run-legend", StringComparison.Ordinal), "the legend");

                var choose = await EvFragmentAsync(app, "/evals/suites/" + suiteId + "/baseline?run=" + runId, "id=\"modal-baseline\"");
                Check(Regex.IsMatch(choose, $"<option value=\"{runId}\" selected=\"selected\" data-targets=\"t[0-9a-f]{{12}} t[0-9a-f]{{12}}\">"), "the run, with its targets: " +
                    Regex.Match(choose, "<option[^>]*>").Value);
                Equal(2, Regex.Matches(choose, "data-bl-target checked=\"checked\"").Count, "both targets, checked: they have no baseline yet");
                Check(choose.Contains("data-bl-save>Set baseline", StringComparison.Ordinal), "Set baseline is enabled");

                var accepted = await EvPostAsync(app, api + "/baseline", new JsonObject { ["runId"] = runId });
                Check(accepted["accepted"] is JsonArray { Count: 2 }, "accepted for both targets: " + accepted.ToJsonString());

                var drift = await EvPageAsync(app, EvalRoutes.Drift, "Drift");
                Check(drift.Contains("data-evals=\"drift\" data-state=\"default\"", StringComparison.Ordinal) && drift.Contains($"data-drift-suite=\"{suiteId}\"", StringComparison.Ordinal),
                    "Drift watches the suite");
                Equal(2, Regex.Matches(drift, "data-drift-target=\"t[0-9a-f]{12}\" data-level=\"ok\"").Count, "both targets match their baseline: " +
                    string.Join(" | ", Regex.Matches(drift, "data-drift-target=[^>]*>").Select(match => match.Value)));
                Check(drift.Contains("No run since the baseline yet.", StringComparison.Ordinal) && drift.Contains("baseline run #1 (", StringComparison.Ordinal),
                    "its baseline, and nothing compared with it yet: " + Cut(Regex.Match(drift, $"data-drift-suite=\"{suiteId}\".{{0,900}}", RegexOptions.Singleline).Value));
                Check(drift.Contains("data-price-changes", StringComparison.Ordinal), "the price changes list");

                var after = await EvPageAsync(app, suitePath, null);
                Check(after.Contains("data-state=\"default\"", StringComparison.Ordinal) && after.Contains($"<a href=\"{runPath}\">Run #1", StringComparison.Ordinal), "the baseline is run #1");
                Check(!after.Contains("data-no-baseline", StringComparison.Ordinal) && Regex.Matches(after, "title=\"No open signal against the baseline\">ok</span>").Count == 2,
                    "both targets are ok against their baseline");
                Check(after.Contains($"data-run-row", StringComparison.Ordinal) && after.Contains("data-tone=\"outline\">baseline</span>", StringComparison.Ordinal), "the run row's baseline chip");
                Check(after.Contains($"data-open-baseline=\"{suiteId}\">", StringComparison.Ordinal), "Set baseline is enabled");
                Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("evals.pages.run-and-drift-pages-show-a-suspect-then-its-confirmation", async () =>
        {
            var root = Temp("ev-pages-drift");
            try
            {
                await using var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = Path.Join(root, "data"), Arguments = ["--faux-metered"] });
                // k 3: a case that passed all its repeats in the baseline and fails all of them now is a broken case (one case is too few for
                // the pass-rate test). No schedule, so a suspect waits for Run now.
                var (suiteId, caseIds) = await EvSuiteAsync(app, "Broken suite", slow: false, k: 3);
                var api = EvalRoutes.ApiSuites + "/" + suiteId;
                var refused = await EvPostAsync(app, api + "/schedule", new JsonObject
                    { ["enabled"] = true, ["repeat"] = "daily", ["at"] = "07:00", ["timeZone"] = "UTC", ["capMicros"] = 1 });
                await EvPostAsync(app, api, new JsonObject { ["capMicros"] = refused["estimate"]!["worstMicros"]!.GetValue<long>() });

                var first = await EvRunAsync(app, suiteId);
                Check((await EvPostAsync(app, api + "/baseline", new JsonObject { ["runId"] = first }))["accepted"] is JsonArray { Count: 2 }, "the baseline");
                // The case's check now fails every answer.
                await EvPostAsync(app, EvalRoutes.ApiCases + "/" + caseIds[0], new JsonObject
                    { ["checks"] = new JsonArray(new JsonObject { ["kind"] = "contains", ["text"] = "Not in the answer" }) });

                var second = await EvRunAsync(app, suiteId);
                await EvSignalsAsync(app, second, "suspect");
                var suspect = await EvPageAsync(app, EvalRoutes.Run(second), null);
                Equal(2, Regex.Matches(suspect, "data-run-signal=\"broken-case\" data-level=\"suspect\"").Count, "a suspect broken case per target: " +
                    string.Join(" | ", Regex.Matches(suspect, "data-run-signal=[^>]*>").Select(match => match.Value)));
                var suspectText = WebUtility.HtmlDecode(suspect);
                Check(suspectText.Contains("Broken case: ", StringComparison.Ordinal) && suspectText.Contains("The next run confirms it or clears it.", StringComparison.Ordinal) &&
                    suspect.Contains("Open Drift</a>", StringComparison.Ordinal), "the notice says what it is and what happens next");
                Check(Regex.IsMatch(suspectText, "data-run-passed>0 of 6 answers<"), "every answer fails: " + Regex.Match(suspect, "data-run-passed>[^<]*").Value);
                Check(suspect.Contains($"<a href=\"{EvalRoutes.Run(first)}\">Run #1", StringComparison.Ordinal), "compared with the baseline, run #1");
                Equal(2, Regex.Matches(suspect, "<td data-cell-state=").Count, "one case × two targets");

                var watched = await EvPageAsync(app, EvalRoutes.Drift, "Drift");
                Equal(2, Regex.Matches(watched, "data-drift-target=\"t[0-9a-f]{12}\" data-level=\"suspect\"").Count, "both targets suspect");
                Check(watched.Contains($"data-run-suite=\"{suiteId}\">Run now to confirm", StringComparison.Ordinal) &&
                    watched.Contains("The suite has no schedule, so run it to confirm or clear this.", StringComparison.Ordinal), "Run now to confirm, with no schedule to do it");
                Check(watched.Contains($"href=\"{EvalRoutes.Run(second)}\">Compare answers", StringComparison.Ordinal), "Compare answers opens the run that saw it");
                Check(!watched.Contains("Accept as baseline", StringComparison.Ordinal), "a suspect is not accepted yet");

                var third = await EvRunAsync(app, suiteId);
                await EvSignalsAsync(app, third, "drift");
                var confirmed = await EvPageAsync(app, EvalRoutes.Run(third), null);
                Equal(2, Regex.Matches(confirmed, "data-run-signal=\"broken-case\" data-level=\"drift\"").Count, "the next run confirms it: drift");
                Check(WebUtility.HtmlDecode(confirmed).Contains("Run #2 saw it first, and this run confirms it.", StringComparison.Ordinal), "the drift it confirmed");

                var drifted = await EvPageAsync(app, EvalRoutes.Drift, "Drift");
                var targets = Regex.Matches(drifted, "data-drift-target=\"(t[0-9a-f]{12})\" data-level=\"drift\"").Select(match => match.Groups[1].Value).ToList();
                Equal(2, targets.Count, "both targets drifted");
                Check(drifted.Contains($"data-open-baseline=\"{suiteId}\" data-target-id=\"{targets[0]}\">Accept as baseline", StringComparison.Ordinal) &&
                    !drifted.Contains("Run now to confirm", StringComparison.Ordinal), "Accept as baseline on each drifted target");
                Check(Regex.IsMatch(WebUtility.HtmlDecode(drifted), "Broken case: [^<]* Run #2 saw it first and run #3 saw it again\\.<"), "where it was first seen: " +
                    Cut(Regex.Match(WebUtility.HtmlDecode(drifted), "Broken case: [^<]*").Value));

                var accept = await EvFragmentAsync(app, $"/evals/suites/{suiteId}/baseline?run={third}&target={targets[0]}", "id=\"modal-baseline\"");
                Check(accept.Contains($"value=\"{targets[0]}\" data-bl-target checked=\"checked\"", StringComparison.Ordinal) &&
                    Regex.Matches(accept, "data-bl-target checked=\"checked\"").Count == 1, "Accept as baseline checks only that target");
                Check(Regex.IsMatch(accept, $"<option value=\"{third}\" selected=\"selected\""), "on the run that confirmed it");

                var suite = await EvPageAsync(app, EvalRoutes.Suite(suiteId), null);
                Equal(2, Regex.Matches(suite, "title=\"Drift: confirmed against the baseline\">drift</span>\\s*<span class=\"d-block fs-8 text-ps-faint mt-1\">Broken case</span>").Count,
                    "each target's drift chip and rule");
                Equal(3, Regex.Matches(suite, "data-run-row=").Count, "three runs");
                var cards = await EvPageAsync(app, EvalRoutes.Suites, "Suites");
                Check(cards.Contains(">2 drift</span>", StringComparison.Ordinal) && WebUtility.HtmlDecode(cards).Contains("Run #3 · ", StringComparison.Ordinal) &&
                    cards.Contains("0 of 6 passed", StringComparison.Ordinal), "the card's last run: run #3, two drift signals");
                Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),
    ];

    // ----- helpers -----

    /// <summary>GET <paramref name="path"/>: 200, no-store, a shell page under the CSP with Evals the active sidebar item, the
    /// <paramref name="tab"/> tab marked when the page has the tabs, no Razor text and no placeholder.</summary>
    private static async Task<string> EvPageAsync(WebAgentApp app, string path, string? tab)
    {
        var page = await app.Get(path);
        Equal(200, page.Status, path + ": " + Cut(page.Text));
        Check(page.Response.Headers.CacheControl?.NoStore == true, path + " is never cached");
        Equal(HostGuardOptions.DefaultContentSecurityPolicy, string.Join("", page.Response.Headers.GetValues("Content-Security-Policy")), path + " CSP");
        var html = page.Text;
        CheckShell(path, html);
        NoRazorText(html, path);
        Check(Regex.IsMatch(html, "<a class=\"nav-link active\" aria-current=\"page\" href=\"/evals/leaderboard\" data-nav=\"evals\">"), path + ": Evals is the active item");
        Check(!html.Contains("Coming in a later milestone", StringComparison.Ordinal), path + " is not the placeholder");
        Check(html.Contains("/css/evals.css", StringComparison.Ordinal) && html.Contains("/js/evals.js", StringComparison.Ordinal), path + " loads the Evals style and module");
        if (tab is not null)
        {
            var marked = Regex.Matches(html, "<a class=\"nav-link active\" aria-current=\"page\" href=\"(/evals/[a-z]+)\">([A-Za-z]+)</a>").Select(match => match.Groups[2].Value).ToList();
            Equal(tab, string.Join(",", marked), path + ": the open tab");
            Equal(3, Regex.Matches(html, "href=\"/evals/(leaderboard|suites|drift)\">(Leaderboard|Suites|Drift)</a>").Count, path + ": the three tabs");
        }
        return html;
    }

    /// <summary>GET a modal fragment: 200, no-store, the modal <paramref name="marker"/>, no layout and no Razor text.</summary>
    private static async Task<string> EvFragmentAsync(WebAgentApp app, string path, string marker)
    {
        var fragment = await app.Get(path);
        Equal(200, fragment.Status, path + ": " + Cut(fragment.Text));
        Check(fragment.Response.Headers.CacheControl?.NoStore == true, path + " is never cached");
        Check(fragment.Text.TrimStart().StartsWith("<div class=\"modal fade\"", StringComparison.Ordinal) && fragment.Text.Contains(marker, StringComparison.Ordinal) &&
            !fragment.Text.Contains("<!doctype", StringComparison.Ordinal), path + " is the modal alone: " + Cut(fragment.Text));
        Check(fragment.Text.Contains("data-modal-content data-", StringComparison.Ordinal), path + " is filled, not the loading shell");
        NoRazorText(fragment.Text, path);
        Check(!Regex.IsMatch(fragment.Text, "<script\\b|\\son[a-z]+\\s*=", RegexOptions.IgnoreCase), path + ": no script and no inline handlers");
        return fragment.Text;
    }

    /// <summary>A same-origin JSON POST that must answer 200.</summary>
    private static async Task<JsonNode> EvPostAsync(WebAgentApp app, string path, JsonObject body)
    {
        var answer = await app.SendAsync(HttpMethod.Post, path, body, origin: app.Origin);
        Equal(200, answer.Status, "POST " + path + ": " + answer.Text);
        return answer.Body!;
    }

    /// <summary>A suite on faux/echo and faux/echo-small (<paramref name="k"/> repeats), made through the Suites API, with a case that must contain "SemVer" and, with
    /// <paramref name="slow"/>, a second one answered slowly (so a run is still going when its pages are read).</summary>
    private static async Task<(string SuiteId, List<string> CaseIds)> EvSuiteAsync(WebAgentApp app, string name, bool slow, int k = 2)
    {
        var suiteId = EvalIds.New(EvalIds.Suite);
        var made = await EvPostAsync(app, EvalRoutes.ApiSuites, new JsonObject
        {
            ["id"] = suiteId, ["name"] = name, ["k"] = k,
            ["targets"] = new JsonArray(new JsonObject { ["provider"] = "faux", ["model"] = "echo" }, new JsonObject { ["provider"] = "faux", ["model"] = "echo-small" }),
        });
        Check(made["created"]?.GetValue<bool>() == true, "suite made: " + made.ToJsonString());
        var prompts = new List<(string Name, string Prompt, int MaxTokens)> { ("Is it valid SemVer", "Is 1.0.0-rc.1 valid SemVer?", 256) };
        if (slow) prompts.Add(("SemVer, slowly", "slow: SemVer " + string.Join(' ', Enumerable.Range(1, 100).Select(item => "word" + item)), 1024));
        var ids = new List<string>();
        foreach (var (caseName, prompt, maxTokens) in prompts)
        {
            var caseId = EvalIds.New(EvalIds.Case);
            var answer = await EvPostAsync(app, EvalRoutes.ApiCases, new JsonObject
            {
                ["id"] = caseId, ["suiteId"] = suiteId, ["name"] = caseName,
                ["spec"] = new JsonObject
                {
                    ["provider"] = "faux", ["model"] = "echo", ["maxTokens"] = maxTokens,
                    ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = prompt }),
                },
                ["checks"] = new JsonArray(new JsonObject { ["kind"] = "contains", ["text"] = "SemVer" }),
            });
            Check(answer["created"]?.GetValue<bool>() == true, "case made: " + answer.ToJsonString());
            ids.Add(caseId);
        }
        return (suiteId, ids);
    }

    /// <summary>Run now on <paramref name="suiteId"/> (answering the Spend Guard's confirm if it asks), waited for until it ends; the run
    /// must complete.</summary>
    private static async Task<string> EvRunAsync(WebAgentApp app, string suiteId)
    {
        var api = EvalRoutes.ApiSuites + "/" + suiteId + "/run";
        var runId = EvalIds.New(EvalIds.Run);
        var started = await EvPostAsync(app, api, new JsonObject { ["runId"] = runId });
        if (started["posted"]?.GetValue<bool>() != true && started["spend"] is not null)
            started = await EvPostAsync(app, api, new JsonObject { ["runId"] = runId, ["spend"] = new JsonObject { ["choice"] = "proceed-once" } });
        Check(started["posted"]?.GetValue<bool>() == true, "started: " + started.ToJsonString());
        string? status = null;
        await WaitUntilAsync(async () =>
        {
            status = (await app.Get(EvalRoutes.ApiRuns + "/" + runId)).Body?["run"]?["status"]?.GetValue<string>();
            return status is "complete" or "failed" or "cancelled" or "interrupted";
        }, "the run ends", TimeSpan.FromSeconds(60));
        Equal("complete", status, "the run completes");
        return runId;
    }

    /// <summary>Waits for two signals of <paramref name="level"/> on <paramref name="runId"/>: the Suites unit stores a run's drift signals
    /// just after it marks the run complete.</summary>
    private static Task EvSignalsAsync(WebAgentApp app, string runId, string level) =>
        WaitUntilAsync(async () => (await app.Get(EvalRoutes.ApiRuns + "/" + runId)).Body?["signals"] is JsonArray signals &&
            signals.Count(signal => signal?["level"]?.GetValue<string>() == level) >= 2, "the run's " + level + " signals", TimeSpan.FromSeconds(30));

    /// <summary><paramref name="micros"/> as the dollars a cap field holds: <c>0.012345</c>.</summary>
    private static string EvDollars(long micros) => (micros / 1_000_000m).ToString("0.00####", CultureInfo.InvariantCulture);
}
