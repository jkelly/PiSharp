using System.Text.Json.Nodes;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W5, T5): every W5 page and dialog on a phone, on priced faux (--faux-metered) so the cost parts show real numbers: at
// 375 × 812, in the dark theme (the default) and then in the light theme, no page scrolls sideways and no open dialog or dropdown menu
// leaves the screen or crowds its footer (FitsPhoneAsync). The app holds what the pages show in use, made through the pages' own APIs
// (W5SuiteAsync and W5BattleAsync in W5SupportCases): a suite with two targets and two cases, run #1 as its baseline, a check changed so
// run #2 suspects a broken case and run #3 confirms it (drift), and a daily schedule; a voted battle, one ready to vote and one whose answers are still held (then
// stopped, so void); and an agent battle before and after its vote.
//   1. The pages, empty and in use: the leaderboard (request and agent), Suites, a suite, its first and last runs, Drift Watch, a suite or
//      run that does not exist (served 404, in a profile of its own that allows that one error), Compare's request and agent setups with
//      Blind on, and the battle pages (held, ready, voted, void).
//   2. An agent battle's page before the vote (with its blind View diff dialog) and after it.
//   3. The dialogs and menus: the schedule (each repeat pane), Set baseline (from the suite's menu, a run and Drift Watch), the check editor
//      (each kind's pane), the suite's and a case's menus, Delete suite's confirmation, the leaderboard's tag menu, and Pin as test case on
//      a battle's reveal (with Add check open); each is dismissed, so nothing changes.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> W5LayoutCases() =>
    [
        Browse("browser.w5.layout.every-w5-page-fits-a-phone-in-both-themes", AgentMode.FauxMetered, async (app, window) =>
        {
            var page = window.Page;
            var missing = new[] { "/evals/suites/su" + new string('0', 32), "/evals/runs/sr" + new string('0', 32) };
            var empty = new (string Path, string Ready)[]
            {
                ("/evals/leaderboard", "[data-evals='leaderboard'] [data-leaderboard-empty]"),
                ("/evals/leaderboard?mode=agent", "[data-evals='leaderboard'][data-mode='agent']"),
                ("/evals/suites", "[data-evals='suites'] [data-suites-empty]"),
                ("/evals/drift", "[data-evals='drift'] [data-drift-empty]"),
            };
            await W5FitPagesAsync(page, empty);
            await W5FitMissingAsync(app, missing);

            var suite = await app.W5SuiteAsync("Release notes");
            var voted = await app.W5BattleAsync("Name a prime number below ten.", "ready", "echo", "echo-small");
            await app.W5VoteAsync(voted, "pick", "A");
            var ready = await app.W5BattleAsync("Is 1.0.0-rc.1 valid SemVer?", "ready", "echo", "echo-small");
            var held = await app.W5BattleAsync("slow: " + Words(3000), "held", "echo", "echo-small");
            var pages = new (string Path, string Ready)[]
            {
                ("/evals/leaderboard", "[data-evals='leaderboard'] [data-leaderboard-row]"),
                ("/evals/leaderboard?tag=general", "[data-evals='leaderboard'] [data-leaderboard-row]"),
                ("/evals/suites", "[data-evals='suites'] [data-suite-card]"),
                ("/evals/suites/" + suite.Suite, "[data-evals='suite'] [data-suite-runs]"),
                ("/evals/runs/" + suite.First, "[data-evals='run'] [data-eval-run-live][data-running='false']"),
                ("/evals/runs/" + suite.Last, "[data-evals='run'] [data-run-signal='broken-case'][data-level='drift']"),
                ("/evals/drift", "[data-evals='drift'] [data-drift-target][data-level='drift']"),
                ("/compare/arena/" + held, "[data-arena-battle][data-status='held']"),
                ("/compare/arena/" + ready, "[data-arena-battle][data-status='ready']"),
                ("/compare/arena/" + voted, "[data-arena-voted]"),
            };
            await W5FitPagesAsync(page, pages, async theme =>
            {
                // Compare's request and agent setups with Blind on (remembered in this browser).
                foreach (var (path, id) in new[] { ("/compare", "cmpBlind"), ("/compare?mode=agent", "acBlind") })
                {
                    await page.GotoAsync(path);
                    var blind = page.Locator($"#{id}[data-arena-blind]");
                    if (!await blind.IsCheckedAsync()) await blind.CheckAsync();
                    await Expect(blind).ToBeCheckedAsync();
                    await page.FitsPhoneAsync(path + " (Blind)", theme);
                }
            });
            Equal("held", (await app.Get("/api/evals/arena/" + held)).Body?["status"]?.GetValue<string>(), "the held battle was held while its page was looked at");

            // Stop all: the battle is void, and its page says so.
            var stopped = await app.PostAsync($"/api/evals/arena/{held}/stop", new JsonObject());
            Equal("void", stopped.Body?["status"]?.GetValue<string>(), "stopped: " + stopped.Text);
            await W5FitPagesAsync(page, [("/compare/arena/" + held, "[data-arena-void]")]);
            await W5FitMissingAsync(app, missing);
        }, width: 375, height: 812),

        Browse("browser.w5.layout.agent-battles-fit-a-phone-in-both-themes", AgentMode.FauxMetered, async (app, window) =>
        {
            var page = window.Page;
            var battle = await app.W5AgentBattleAsync("phone-agent-1", "echo", "echo-small");
            foreach (var theme in new[] { "dark", "light" })
            {
                await page.GotoAsync("/compare/arena/" + battle);
                if (theme == "light") await page.UseThemeAsync("light");
                await Expect(page.Locator("[data-arena-battle][data-status='ready']")).ToBeAttachedAsync();
                await page.FitsPhoneAsync("an agent battle before the vote", theme);
                await page.OpenModalAsync(page.Locator("[data-arena-diff='A'] >> visible=true").First, "modal-agent-diff");
                await Expect(page.Locator("#modal-agent-diff")).ToContainTextAsync("result.txt");
                await page.FitsPhoneAsync("an agent battle › modal-agent-diff", theme);
                await page.DismissModalAsync("modal-agent-diff");
            }
            await page.UseThemeAsync("dark");
            await app.W5VoteAsync(battle, "tie");
            await W5FitPagesAsync(page, [("/compare/arena/" + battle, "[data-arena-voted]"), ("/evals/leaderboard?mode=agent", "[data-leaderboard-row]")]);
        }, width: 375, height: 812),

        Browse("browser.w5.layout.w5-dialogs-fit-a-phone-in-both-themes", AgentMode.FauxMetered, async (app, window) =>
        {
            var suite = await app.W5SuiteAsync("Release notes");
            var voted = await app.W5BattleAsync("Name a prime number below ten.", "ready", "echo", "echo-small");
            await app.W5VoteAsync(voted, "pick", "B");
            var page = window.Page;
            foreach (var theme in new[] { "dark", "light" })
            {
                await page.GotoAsync("/evals/suites/" + suite.Suite);
                if (theme == "light") await page.UseThemeAsync("light");

                // A suite: the schedule (each repeat), its menu, Set baseline…, Delete suite…, a case's menu and the check editor.
                await Expect(page.Locator("[data-evals='suite']")).ToBeVisibleAsync();
                var schedule = await page.OpenModalAsync(page.Locator("[data-suite-header] [data-open-schedule]"), "modal-suite-schedule");
                await Expect(schedule.Locator("[data-ss-save]")).ToBeVisibleAsync();
                foreach (var repeat in new[] { "hours", "daily", "weekly" })
                {
                    await schedule.Locator($"[data-ss-repeat='{repeat}']").ClickAsync();
                    await Expect(schedule.Locator($"[data-ss-pane='ss-{repeat}']")).ToBeVisibleAsync();
                    await page.FitsPhoneAsync("a suite › modal-suite-schedule › " + repeat, theme);
                }
                await page.DismissModalAsync("modal-suite-schedule");

                var more = page.Locator("[data-suite-header] [data-bs-toggle='dropdown']");
                await more.ClickAsync();
                await page.FitsPhoneAsync("a suite › its menu", theme);
                var baseline = await page.OpenModalAsync(page.Locator("[data-suite-header] [data-open-baseline]"), "modal-baseline");
                await Expect(baseline.Locator("[data-bl-save]")).ToBeVisibleAsync();
                await page.FitsPhoneAsync("a suite › modal-baseline", theme);
                await page.DismissModalAsync("modal-baseline");

                await more.ClickAsync();
                await page.OpenModalAsync(page.Locator("[data-suite-header] [data-delete-suite]"), "modal-confirm");
                await page.FitsPhoneAsync("a suite › Delete suite…", theme);
                await page.DismissModalAsync("modal-confirm");

                var caseMenu = page.Locator("[data-case-row] [data-bs-toggle='dropdown']").First;
                await caseMenu.ClickAsync();
                await page.FitsPhoneAsync("a suite › a case's menu", theme);
                await caseMenu.ClickAsync();

                var editor = await page.OpenModalAsync(page.Locator($"[data-edit-check][data-case-id='{suite.Case}'][data-check-id]").First, "modal-check-editor");
                var kinds = editor.Locator("#ceKind");
                await Expect(kinds).ToBeVisibleAsync();
                var options = await kinds.Locator("option").EvaluateAllAsync<string[]>("options => options.map((option) => option.value)");
                Check(options.Length >= 5, "the check kinds: " + string.Join(", ", options));
                foreach (var kind in options)
                {
                    await kinds.SelectOptionAsync(kind);
                    await Expect(editor.Locator("[data-ce-pane]:visible")).ToHaveCountAsync(1);
                    await page.FitsPhoneAsync("a suite › modal-check-editor › " + kind, theme);
                }
                await page.DismissModalAsync("modal-check-editor");

                // The suites list's schedule.
                await page.GotoAsync("/evals/suites");
                await page.OpenModalAsync(page.Locator("[data-suite-card] [data-open-schedule]").First, "modal-suite-schedule");
                await Expect(page.Locator("#modal-suite-schedule [data-ss-save]")).ToBeVisibleAsync();
                await page.FitsPhoneAsync("Suites › modal-suite-schedule", theme);
                await page.DismissModalAsync("modal-suite-schedule");

                // A run: Set as baseline, and its menu.
                await page.GotoAsync("/evals/runs/" + suite.First);
                await page.OpenModalAsync(page.Locator("[data-eval-run-header] [data-open-baseline]"), "modal-baseline");
                await Expect(page.Locator("#modal-baseline [data-bl-run]")).ToHaveValueAsync(suite.First);
                await page.FitsPhoneAsync("a run › modal-baseline", theme);
                await page.DismissModalAsync("modal-baseline");
                var runMenu = page.Locator("[data-eval-run-header] [data-bs-toggle='dropdown']");
                await runMenu.ClickAsync();
                await page.FitsPhoneAsync("a run › its menu", theme);
                await runMenu.ClickAsync();

                // Drift Watch: Accept as baseline.
                await page.GotoAsync("/evals/drift");
                await page.OpenModalAsync(page.Locator("[data-drift-target] [data-open-baseline]").First, "modal-baseline");
                await Expect(page.Locator("#modal-baseline [data-bl-save]")).ToBeVisibleAsync();
                await page.FitsPhoneAsync("Drift Watch › modal-baseline", theme);
                await page.DismissModalAsync("modal-baseline");

                // The leaderboard's tag menu.
                await page.GotoAsync("/evals/leaderboard");
                var tags = page.Locator("[data-tag-menu]");
                await tags.ClickAsync();
                await page.FitsPhoneAsync("the leaderboard › its tag menu", theme);
                await tags.ClickAsync();

                // Pin as test case on a battle's reveal, with Add check open.
                await page.GotoAsync("/compare/arena/" + voted);
                var pin = await page.OpenModalAsync(page.Locator("[data-arena-pin]"), "modal-test-case");
                await Expect(pin.Locator("[data-pin-content]")).ToHaveAttributeAsync("data-pin-state", "ready");
                await page.FitsPhoneAsync("a reveal › modal-test-case", theme);
                await pin.Locator("[data-bs-target='#tcaseAddCheck']").ClickAsync();
                await Expect(pin.Locator("#tcaseAddCheck")).ToBeVisibleAsync();
                await page.FitsPhoneAsync("a reveal › modal-test-case › Add check", theme);
                await page.DismissModalAsync("modal-test-case");
            }
            var after = (await app.Get("/api/evals/suites/" + suite.Suite)).Body!;
            Equal(3, after["runs"]!.AsArray().Count, "the dismissed dialogs ran nothing");
            Equal(suite.First, after["baselines"]![0]!["runId"]!.GetValue<string>(), "and kept the baseline");
            Equal(2, after["cases"]!.AsArray().Count, "and kept the cases");
            Check(after["overview"]?["schedule"]?["label"]?.GetValue<string>() == "Daily at 07:00", "and the schedule: " + after["overview"]?["schedule"]?.ToJsonString());
        }, width: 375, height: 812),
    ];

    /// <summary>Each of <paramref name="pages"/> fits the phone, in the dark theme and then in the light one (and <paramref name="more"/> for
    /// each theme); the profile is left on dark.</summary>
    private static async Task W5FitPagesAsync(IPage page, (string Path, string Ready)[] pages, Func<string, Task>? more = null)
    {
        foreach (var theme in new[] { "dark", "light" })
        {
            await page.GotoAsync(pages[0].Path);
            if (theme == "light") await page.UseThemeAsync("light");
            foreach (var (path, ready) in pages)
            {
                await page.GotoAsync(path);
                await Expect(page.Locator(ready).First).ToBeAttachedAsync(new() { Timeout = 20_000 });
                await page.FitsPhoneAsync(path, theme);
            }
            if (more is not null) await more(theme);
        }
        await page.UseThemeAsync("dark");
    }

    /// <summary>The Evals pages of what does not exist fit the phone in both themes. They are served 404, which the browser logs as a console
    /// error, so they open in a profile of their own that fails on any other error.</summary>
    private static async Task W5FitMissingAsync(Agent app, string[] paths)
    {
        var context = await (await BrowserAsync()).NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = app.Base.ToString(),
            ViewportSize = new ViewportSize { Width = 375, Height = 812 },
            ReducedMotion = ReducedMotion.Reduce,
        });
        try
        {
            context.SetDefaultTimeout(15_000);
            var page = await context.NewPageAsync();
            var errors = new List<string>();
            page.PageError += (_, error) => { lock (errors) errors.Add("page error: " + error); };
            page.Console += (_, message) =>
            {
                if (message.Type == "error" && !message.Text.Contains("status of 404", StringComparison.Ordinal)) lock (errors) errors.Add("console error: " + message.Text);
            };
            foreach (var theme in new[] { "dark", "light" })
            {
                foreach (var path in paths)
                {
                    var response = await page.GotoAsync(path);
                    Equal(404, response?.Status, path + " is served 404");
                    if (theme == "light" && await page.Locator("html").GetAttributeAsync("data-bs-theme") != "light") await page.UseThemeAsync("light");
                    await Expect(page.Locator("[data-eval-not-found]")).ToBeVisibleAsync();
                    await page.FitsPhoneAsync(path, theme);
                }
            }
            lock (errors) Check(errors.Count == 0, "the not-found pages: " + string.Join(" | ", errors));
        }
        finally
        {
            await context.DisposeAsync();
        }
    }
}
