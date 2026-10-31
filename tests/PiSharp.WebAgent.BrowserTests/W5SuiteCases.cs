using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W5, fix 2, tests-ci): what a suite page and the leaderboard do in a browser, on priced faux (every suite call is
// reserved and settled at a list price), failing on any page or console error. Each change goes through the page's own controls and is read
// back from the Suites unit's API after the page reloads with it:
//   - Add target (Provider then Model) and Remove target (asks first); the suite's Rename; and the schedule dialog's cap field: a cap below
//     the worst case is refused (Save disabled), one above it fits, Save schedule turns it on daily with that cap, and Turn off turns it off.
//   - The check editor: a regex check tried on the reference output (passes) and saved; a tool check with argument rules (one added and
//     removed again, one kept) tried (fails: the reference calls no tool) and saved; then the regex check opened again and removed.
//   - Cancel run on a run that is still answering (a faux slow: answer), after its confirmation; and Remove from suite on a case.
//   - The leaderboard's Export votes (CSV): one row for the one vote, under its tag.
//   - A scheduled run: a schedule every hour from the next minute (UTC) starts its slot's run on the scheduler's tick (at most 30 s after
//     the slot); the suite page lists it as scheduled and its run page says so.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> W5SuiteCases() =>
    [
        Browse("browser.w5.suite.targets-rename-and-the-schedule-cap", AgentMode.FauxMetered, async (app, window) =>
        {
            var suite = await app.W5PlainSuiteAsync("Targets and schedule");
            await app.W5CaseAsync(suite, "Pre-release", "Is 1.0.0-rc.1 valid SemVer?");
            var page = window.Page;
            await page.GotoAsync("/evals/suites/" + suite);
            var targets = page.Locator("[data-suite-targets] [data-target-row]");
            await Expect(targets).ToHaveCountAsync(1);

            // Add target: Provider, then Model.
            await page.Locator("[data-suite-targets] [aria-controls='addTarget']").ClickAsync();
            var form = page.Locator("#addTarget [data-add-target]");
            await Expect(form).ToBeVisibleAsync();
            await form.Locator("#st-provider").SelectOptionAsync("faux");
            await form.Locator("#st-model-faux").SelectOptionAsync("echo-small");
            await form.Locator("[type='submit']").ClickAsync();
            await Expect(targets).ToHaveCountAsync(2, new() { Timeout = 15_000 });
            await page.ToastAsync("Target added");
            var added = (await app.Get("/api/evals/suites/" + suite)).Body!["overview"]!["suite"]!["targets"]!.AsArray();
            Equal("echo,echo-small", string.Join(",", added.Select(target => target!["model"]!.GetValue<string>())), "the suite's targets");

            // Remove target asks first.
            var small = (await targets.Filter(new() { HasText = "echo-small" }).GetAttributeAsync("data-target-row"))!;
            await page.Locator($"[data-remove-target='{small}']").ClickAsync();
            await page.ModalShownAsync("modal-confirm");
            await Expect(page.Locator("#modal-confirm")).ToContainTextAsync("Remove");
            await page.Locator("#modal-confirm [data-confirm-ok]").ClickAsync();
            await Expect(targets).ToHaveCountAsync(1, new() { Timeout = 15_000 });
            await page.ToastAsync("Target removed");
            await Expect(page.Locator($"[data-target-row='{small}']")).ToHaveCountAsync(0);

            // Rename, from the suite's menu.
            await page.Locator("[data-suite-header] [aria-label='More suite actions']").ClickAsync();
            await page.Locator("[data-suite-header] [data-rename-suite]").ClickAsync();
            var rename = page.Locator("[data-rename-form]");
            await Expect(rename).ToBeVisibleAsync();
            await rename.Locator("#suiteRename").FillAsync("Renamed suite");
            await rename.Locator("[type='submit']").ClickAsync();
            await Expect(page.Locator("[data-suite-name]")).ToHaveTextAsync("Renamed suite", new() { Timeout = 15_000 });
            await page.ToastAsync("Suite renamed");
            Equal("Renamed suite", (await app.Get("/api/evals/suites/" + suite)).Body?["overview"]?["suite"]?["name"]?.GetValue<string>(), "renamed");

            // The schedule's cap field: below the worst case it is refused and Save is off; above it, it fits.
            var schedule = await page.OpenModalAsync(page.Locator("[data-suite-header] [data-open-schedule]"), "modal-suite-schedule");
            var content = schedule.Locator("[data-modal-content][data-suite-id]");
            await Expect(content).ToBeVisibleAsync();
            await schedule.Locator("[data-ss-repeat='daily']").ClickAsync();
            await Expect(schedule.Locator("[data-ss-pane='ss-daily']")).ToBeVisibleAsync();
            await schedule.Locator("#ssDailyTime").FillAsync("07:00");
            var cap = schedule.Locator("[data-cap-input]");
            await cap.FillAsync("0.000001");
            await Expect(schedule.Locator("[data-ss-pane='ss-refused']")).ToBeVisibleAsync();
            await Expect(schedule.Locator("[data-ss-refused-message]")).Not.ToBeEmptyAsync();
            await Expect(schedule.Locator("[data-ss-save]")).ToBeDisabledAsync();
            await Expect(cap).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bis-invalid\b"));
            await cap.FillAsync("5.00");
            await Expect(schedule.Locator("[data-ss-pane='ss-fits']")).ToBeVisibleAsync();
            await Expect(schedule.Locator("[data-ss-pane='ss-refused']")).ToBeHiddenAsync();
            await Expect(schedule.Locator("[data-ss-pane='ss-fits']")).ToContainTextAsync("$5.00 cap");
            await Expect(schedule.Locator("[data-ss-save]")).ToBeEnabledAsync();
            await schedule.Locator("[data-ss-save]").ClickAsync();
            await page.ToastAsync("Schedule saved");
            await Expect(page.Locator("[data-suite-header]")).ToContainTextAsync("Daily at 07:00", new() { Timeout = 15_000 });
            await Expect(page.Locator("[data-suite-header]")).ToContainTextAsync("$5.00 · Block");
            var saved = (await app.Get("/api/evals/suites/" + suite)).Body!["overview"]!["suite"]!;
            Check(saved["schedule"]?["enabled"]?.GetValue<bool>() == true && saved["schedule"]?["at"]?.GetValue<string>() == "07:00" &&
                saved["schedule"]?["repeat"]?.GetValue<string>() == "daily" && saved["capMicros"]?.GetValue<long>() == 5_000_000,
                "daily at 07:00 with a $5 suite-run cap: " + saved.ToJsonString());

            // Turn off.
            schedule = await page.OpenModalAsync(page.Locator("[data-suite-header] [data-open-schedule]"), "modal-suite-schedule");
            await Expect(schedule.Locator("[data-modal-content][data-enabled='true']")).ToBeVisibleAsync();
            await schedule.Locator("[data-ss-off]").ClickAsync();
            await page.ToastAsync("Schedule turned off");
            await Expect(page.Locator("[data-suite-header]")).Not.ToContainTextAsync("Daily at 07:00", new() { Timeout = 15_000 });
            Equal(false, (await app.Get("/api/evals/suites/" + suite)).Body?["overview"]?["suite"]?["schedule"]?["enabled"]?.GetValue<bool>() ?? false,
                "the schedule is off");
        }),

        Browse("browser.w5.suite.check-editor-tries-saves-and-removes-checks", AgentMode.FauxMetered, async (app, window) =>
        {
            var suite = await app.W5PlainSuiteAsync("Checks");
            var caseId = await app.W5CaseAsync(suite, "Pre-release", "Is 1.0.0-rc.1 valid SemVer?", reference: "Echo: Is 1.0.0-rc.1 valid SemVer?");
            var page = window.Page;
            await page.GotoAsync("/evals/suites/" + suite);
            var row = page.Locator($"[data-case-row='{caseId}']");
            var checks = row.Locator("[data-edit-check][data-check-id]");
            await Expect(checks).ToHaveCountAsync(1);

            // A regex check: Try passes on the reference output; Save adds it.
            var editor = await page.OpenModalAsync(row.Locator("[data-edit-check]:not([data-check-id])"), "modal-check-editor");
            var content = editor.Locator($"[data-modal-content][data-case-id='{caseId}']");
            await Expect(content).ToBeVisibleAsync();
            await editor.Locator("#ceKind").SelectOptionAsync("regex");
            await Expect(editor.Locator("[data-ce-pane='ce-regex']")).ToBeVisibleAsync();
            await editor.Locator("#ceRegexPattern").FillAsync(@"rc\.\d");
            await editor.Locator("[data-ce-try-button]").ClickAsync();
            await Expect(editor.Locator("[data-ce-try-result] [data-try-verdict='passed']")).ToBeVisibleAsync();
            await editor.Locator("[data-ce-save]").ClickAsync();
            await page.ToastAsync("Check saved");
            await Expect(checks).ToHaveCountAsync(2, new() { Timeout = 15_000 });
            var stored = (await app.Get("/api/evals/cases/" + caseId)).Body!["case"]!["checks"]!.AsArray();
            var regex = stored.Single(check => check!["kind"]!.GetValue<string>() == "regex")!;
            Equal(@"rc\.\d", regex["pattern"]?.GetValue<string>(), "the regex check is stored");

            // A tool check with argument rules: one added and removed again, one kept; Try fails (the reference calls no tool).
            editor = await page.OpenModalAsync(row.Locator("[data-edit-check]:not([data-check-id])"), "modal-check-editor");
            await Expect(editor.Locator($"[data-modal-content][data-case-id='{caseId}']")).ToBeVisibleAsync();
            await editor.Locator("#ceKind").SelectOptionAsync("tool");
            await Expect(editor.Locator("[data-ce-pane='ce-tool']")).ToBeVisibleAsync();
            await editor.Locator("#ceToolName").FillAsync("get_weather");
            await editor.Locator("#ceToolCount").SelectOptionAsync("exactly-once");
            var arguments = editor.Locator("[data-ce-args] [data-ce-arg]");
            await editor.Locator("[data-ce-add-arg]").ClickAsync();
            await editor.Locator("[data-ce-add-arg]").ClickAsync();
            await Expect(arguments).ToHaveCountAsync(2);
            await arguments.Nth(0).Locator("[data-arg-name]").FillAsync("location");
            await arguments.Nth(0).Locator("[data-arg-rule]").SelectOptionAsync("equals");
            await arguments.Nth(0).Locator("[data-arg-value]").FillAsync("Oslo");
            await arguments.Nth(1).Locator("[data-arg-name]").FillAsync("units");
            await arguments.Nth(1).Locator("[data-ce-remove-arg]").ClickAsync();
            await Expect(arguments).ToHaveCountAsync(1);
            await editor.Locator("[data-ce-try-button]").ClickAsync();
            await Expect(editor.Locator("[data-ce-try-result] [data-try-verdict='failed']")).ToBeVisibleAsync();
            await editor.Locator("[data-ce-save]").ClickAsync();
            await page.ToastAsync("Check saved");
            await Expect(checks).ToHaveCountAsync(3, new() { Timeout = 15_000 });
            stored = (await app.Get("/api/evals/cases/" + caseId)).Body!["case"]!["checks"]!.AsArray();
            var tool = stored.Single(check => check!["kind"]!.GetValue<string>() == "tool")!;
            Check(tool["tool"]?.GetValue<string>() == "get_weather" && tool["calls"]?.GetValue<string>() == "exactly-once" &&
                tool["arguments"] is JsonArray { Count: 1 } rules && rules[0]?["name"]?.GetValue<string>() == "location" &&
                rules[0]?["rule"]?.GetValue<string>() == "equals" && rules[0]?["value"]?.GetValue<string>() == "Oslo",
                "the tool check keeps its one argument rule: " + tool.ToJsonString());

            // The regex check, opened again: Remove check.
            var regexId = regex["id"]!.GetValue<string>();
            editor = await page.OpenModalAsync(row.Locator($"[data-edit-check][data-check-id='{regexId}']"), "modal-check-editor");
            await Expect(editor.Locator($"[data-modal-content][data-check-id='{regexId}']")).ToBeVisibleAsync();
            await Expect(editor.Locator("#ceRegexPattern")).ToHaveValueAsync(@"rc\.\d");
            await editor.Locator("[data-ce-remove]").ClickAsync();
            await page.ToastAsync("Check removed");
            await Expect(checks).ToHaveCountAsync(2, new() { Timeout = 15_000 });
            stored = (await app.Get("/api/evals/cases/" + caseId)).Body!["case"]!["checks"]!.AsArray();
            Equal("contains,tool", string.Join(",", stored.Select(check => check!["kind"]!.GetValue<string>())), "the regex check is gone, the others stay");
        }),

        Browse("browser.w5.suite.cancel-a-run-and-remove-a-case", AgentMode.FauxMetered, async (app, window) =>
        {
            var suite = await app.W5PlainSuiteAsync("Cancel and remove");
            var slow = await app.W5CaseAsync(suite, "Long answer", "slow: " + Words(400));
            var quick = await app.W5CaseAsync(suite, "Short answer", "Name a prime number below ten.");
            var page = window.Page;
            await page.GotoAsync("/evals/suites/" + suite);

            // Run now, then Cancel run while the long answer streams.
            await page.Locator("[data-suite-header] [data-run-suite]").ClickAsync();
            await page.WaitForURLAsync("**/evals/runs/sr*");
            var live = page.Locator("[data-eval-run-live]");
            await Expect(live).ToHaveAttributeAsync("data-running", "true");
            var runId = (await page.Locator("[data-evals='run']").GetAttributeAsync("data-run-id"))!;
            await page.Locator($"[data-cancel-run='{runId}']").ClickAsync();
            await page.ModalShownAsync("modal-confirm");
            await Expect(page.Locator("#modal-confirm")).ToContainTextAsync("Cancel this run?");
            await page.Locator("#modal-confirm [data-confirm-ok]").ClickAsync();
            await page.ToastAsync("Run cancelled");
            await page.W5RunPageEndedAsync(1, "cancelled");
            var run = (await app.Get("/api/evals/runs/" + runId)).Body!;
            Equal("cancelled", run["run"]?["status"]?.GetValue<string>(), "the run is cancelled");
            Check(run["results"]!.AsArray().Any(result => result?["caseId"]?.GetValue<string>() == slow && result["status"]?.GetValue<string>() == "cancelled"),
                "the long answer was cut off: " + run["results"]!.ToJsonString());

            // Remove from suite, after its confirmation.
            await page.GotoAsync("/evals/suites/" + suite);
            var row = page.Locator($"[data-case-row='{quick}']");
            await row.Locator("[data-bs-toggle='dropdown']").ClickAsync();
            await row.Locator($"[data-delete-case='{quick}']").ClickAsync();
            await page.ModalShownAsync("modal-confirm");
            await Expect(page.Locator("#modal-confirm")).ToContainTextAsync("Remove Short answer?");
            await page.Locator("#modal-confirm [data-confirm-ok]").ClickAsync();
            await page.ToastAsync("Case removed");
            await Expect(row).ToHaveCountAsync(0, new() { Timeout = 15_000 });
            await Expect(page.Locator($"[data-case-row='{slow}']")).ToBeVisibleAsync();
            Equal(404, (await app.Get("/api/evals/cases/" + quick)).Status, "the case is gone");
            Equal(1, (await app.Get("/api/evals/suites/" + suite)).Body?["cases"]?.AsArray().Count, "one case left");
        }),

        Browse("browser.w5.leaderboard.exports-the-votes-csv", AgentMode.FauxMetered, async (app, window) =>
        {
            var battle = await app.W5BattleAsync("Name a colour.", "ready", "echo", "echo-small");
            await app.W5VoteAsync(battle, "pick", "A");
            var page = window.Page;
            await page.GotoAsync("/evals/leaderboard?tag=general");
            await Expect(page.Locator("[data-evals='leaderboard']")).ToHaveAttributeAsync("data-tag", "general");
            await page.Locator("[aria-label='Leaderboard actions']").ClickAsync();
            var link = page.Locator("[data-votes-csv]");
            await Expect(link).ToBeVisibleAsync();
            var download = await page.RunAndWaitForDownloadAsync(() => link.ClickAsync());
            Check(download.SuggestedFilename.EndsWith(".csv", StringComparison.OrdinalIgnoreCase), "a CSV file: " + download.SuggestedFilename);
            var path = await download.PathAsync();
            var lines = (await File.ReadAllLinesAsync(path)).Where(line => line.Length > 0).ToList();
            Equal("voted_at,mode,tag,battle,vote,best,ranking,A,B,C,D", lines[0], "the header");
            Equal(2, lines.Count, "one row for the one vote: " + string.Join(" / ", lines));
            var cells = lines[1].Split(',');
            Check(cells.Length == 11 && cells[1] == "request" && cells[2] == "general" && cells[3] == battle && cells[4] == "pick" && cells[5] == "A",
                "the vote's row: " + lines[1]);
            Check(DateTimeOffset.TryParse(cells[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _), "when it was cast: " + cells[0]);
            Check(new[] { cells[7], cells[8] }.Order(StringComparer.Ordinal).SequenceEqual(["faux/echo", "faux/echo-small"]), "the two revealed models: " + lines[1]);
        }),

        Browse("browser.w5.suite.a-scheduled-run-starts-on-its-slot", AgentMode.FauxMetered, async (app, window) =>
        {
            var suite = await app.W5PlainSuiteAsync("Every hour");
            await app.W5CaseAsync(suite, "Pre-release", "Is 1.0.0-rc.1 valid SemVer?");
            // Every hour from the next minute (UTC): a slot before the schedule is turned on never runs, so the first slot is the coming one,
            // at least 15 s ahead so it is not passed while the schedule is saved.
            var now = DateTimeOffset.UtcNow;
            var slot = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero).AddMinutes(now.Second < 50 ? 1 : 2);
            var saved = await app.PostAsync($"/api/evals/suites/{suite}/schedule", new JsonObject
            {
                ["enabled"] = true, ["repeat"] = "hours", ["everyHours"] = 1, ["at"] = slot.ToString("HH:mm", CultureInfo.InvariantCulture),
                ["timeZone"] = "UTC", ["capMicros"] = 1_000_000,
            });
            Check(saved.Status == 200 && saved.Body?["saved"]?.GetValue<bool>() == true, "every hour, with a $1 cap: " + saved.Text);
            var page = window.Page;
            await page.GotoAsync("/evals/suites/" + suite);
            await Expect(page.Locator("[data-suite-header]")).ToContainTextAsync("Every hour from " + slot.ToString("HH:mm", CultureInfo.InvariantCulture));
            await Expect(page.Locator("[data-no-runs]")).ToBeVisibleAsync();

            // The scheduler starts the slot's run on its next tick after the slot (every 30 s).
            string runId = "";
            await WaitUntilAsync(async () =>
            {
                var runs = (await app.Get("/api/evals/suites/" + suite)).Body?["runs"]?.AsArray() ?? [];
                runId = runs.Count > 0 ? runs[0]!["id"]!.GetValue<string>() : "";
                return runId.Length > 0;
            }, "the scheduler starts the slot's run", TimeSpan.FromSeconds(150));
            var started = (await app.Get("/api/evals/runs/" + runId)).Body!["run"]!;
            Check(started["trigger"]?.GetValue<string>() == "schedule" &&
                DateTimeOffset.Parse(started["slot"]!.GetValue<string>(), CultureInfo.InvariantCulture) == slot, "a scheduled run for the slot: " + started.ToJsonString());

            // The suite page lists it as scheduled, and its run page says so.
            await page.ReloadAsync();
            var row = page.Locator($"[data-suite-runs] [data-run-row='{runId}']");
            await Expect(row).ToContainTextAsync("scheduled");
            await row.Locator("a").First.ClickAsync();
            await page.WaitForURLAsync("**/evals/runs/" + runId);
            await page.W5RunPageEndedAsync(1, "complete");
            await Expect(page.Locator("[data-eval-run-header]")).ToContainTextAsync("scheduled · every hour from " + slot.ToString("HH:mm", CultureInfo.InvariantCulture));
            Equal(1, (await app.Get("/api/evals/suites/" + suite)).Body?["runs"]?.AsArray().Count, "one run for the one slot");
        }),
    ];

    /// <summary>A case of <paramref name="suite"/> asking faux/echo <paramref name="prompt"/> (at most 256 tokens), checked for what the echo
    /// says, with <paramref name="reference"/> as its reference output when given, through the Suites unit's API; returns its id.</summary>
    internal static async Task<string> W5CaseAsync(this Agent app, string suite, string name, string prompt, string? reference = null)
    {
        var body = new JsonObject
        {
            ["suiteId"] = suite,
            ["name"] = name,
            ["spec"] = new JsonObject
            {
                ["provider"] = "faux", ["model"] = "echo", ["maxTokens"] = 256,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = prompt }),
            },
            ["checks"] = new JsonArray(new JsonObject { ["kind"] = "contains", ["text"] = "Echo" }),
        };
        if (reference is not null) body["reference"] = new JsonObject { ["text"] = reference, ["provider"] = "faux", ["model"] = "echo" };
        var added = await app.PostAsync("/api/evals/cases", body);
        Check(added.Status == 200 && added.Body?["created"]?.GetValue<bool>() == true, "a case: " + added.Text);
        return added.Body!["case"]!["id"]!.GetValue<string>();
    }
}
