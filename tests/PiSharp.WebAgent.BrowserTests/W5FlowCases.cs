using System.Text.Json.Nodes;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W5, T5): W5 end to end in a browser, on a sealed live app whose one provider is the scripted one (W5SupportCases: its
// answers report a model the case switches, as a provider's silent update would). Compare's setup with two of its models and Blind on: Run
// all opens the battle, and until the vote nothing the tab receives (the page, its fragments, the battle's JSON) names a contestant. The vote
// (A, tag general) reveals the names and saves; Pin as test case (E7's modal on the reveal) saves A's answer as a case of a new suite, and
// the toast's Open suite opens it. Run now opens run #1, which completes with every answer passing; Set as baseline accepts it. Then the
// provider's answers start to report another model: Run again (run #2) raises response-model drift at once, and Run again (run #3) shows
// the confirmation ("Run #2 saw it first, and this run confirms it."); Drift Watch lists the target as drift. The leaderboard counts the
// vote, provisional (one comparison is under the threshold). Every call went to the scripted provider and nowhere else. W5 fix 2: run #2's
// answers are held until its page shows it running, so the page's own poll is what first shows it complete, and that very render already
// has the response-model drift row (the integration stores a run's signals before it reads complete); W5RunRenderProbe records the renders.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> W5FlowCases() =>
    [
        BrowseScripted("browser.w5.flow.blind-vote-pin-suite-baseline-and-confirmed-drift", async (app, provider, window) =>
        {
            var page = window.Page;

            // Compare's setup: two models of the scripted provider, Blind on.
            await page.GotoAsync("/compare");
            var columns = page.Locator("[data-compare-col]");
            await Expect(columns).ToHaveCountAsync(2);
            for (var index = 0; index < W5Models.Length; index++)
            {
                var picker = await page.OpenModalAsync(columns.Nth(index).Locator("[data-col-model]"), "modal-model-picker");
                await picker.Locator("#mp-provider").SelectOptionAsync(W5Provider);
                await picker.Locator("#mp-model-" + W5Provider).SelectOptionAsync(W5Models[index].Id);
                await picker.Locator("[type='submit']").ClickAsync();
                await Expect(picker).ToBeHiddenAsync();
                await Expect(columns.Nth(index)).ToHaveAttributeAsync("data-model", W5Models[index].Id);
                await Expect(columns.Nth(index)).ToHaveAttributeAsync("data-provider", W5Provider);
            }
            var blind = page.Locator("#cmpBlind[data-arena-blind='request']");
            await blind.CheckAsync();
            await Expect(blind).ToBeCheckedAsync();
            await page.Locator("#cmpPrompt").FillAsync("In one line: what does a semantic version's pre-release label mean?");

            // Everything the tab receives from now until the vote (an event stream aside: it never ends while the battle is held).
            var received = new List<(string Url, string Body)>();
            var recording = true;
            page.Response += async (_, response) =>
            {
                if (!recording || (await response.HeaderValueAsync("content-type") ?? "").Contains("event-stream", StringComparison.Ordinal)) return;
                try
                {
                    var body = await response.TextAsync();
                    lock (received) if (recording) received.Add((response.Url, body));
                }
                catch (PlaywrightException) { /* A navigation dropped it. */ }
            };
            await page.Locator("[data-run-all]").ClickAsync();
            await page.WaitForURLAsync("**/compare/arena/**");
            var battleId = new Uri(page.Url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
            Check(battleId.StartsWith("ab", StringComparison.Ordinal), "the battle's address: " + page.Url);
            var battle = page.Locator("[data-arena-battle]");
            await Expect(battle).ToHaveAttributeAsync("data-status", "ready", new() { Timeout = 30_000 });
            await Expect(page.Locator("[data-arena-vote='pick'][data-letter='A']")).ToBeEnabledAsync();
            Check(await page.Locator("[data-arena-voted]").CountAsync() == 0, "not voted yet");

            // Blind means blind: the page, what it fetched, and the battle's JSON name no contestant before the vote.
            W5Blind(await page.ContentAsync(), "the battle page");
            var api = await app.Get("/api/evals/arena/" + battleId);
            Check(api.Status == 200 && api.Body?["revealed"] is null, "the battle's JSON is blind: " + api.Text);
            W5Blind(api.Text, "the battle's JSON");
            W5Blind((await app.Get("/compare/arena/" + battleId)).Text, "the battle page as the server sends it");
            List<(string Url, string Body)> before;
            lock (received)
            {
                recording = false;
                before = [.. received];
            }
            Check(before.Any(item => item.Url.Contains("/compare/arena/" + battleId, StringComparison.Ordinal)), "the battle page was recorded: " + string.Join(", ", before.Select(item => item.Url)));
            foreach (var (url, body) in before) W5Blind(body, url);
            Equal(2, provider.Asked.Count, "one call per contestant: " + string.Join(", ", provider.Asked));

            // The vote reveals the names.
            await Expect(page.Locator("[data-arena-tag]")).ToHaveValueAsync("general");
            await page.Locator("[data-arena-vote='pick'][data-letter='A']").ClickAsync();
            var voted = page.Locator("[data-arena-voted]");
            await Expect(voted).ToHaveAttributeAsync("data-arena-voted", "pick");
            var revealed = (await app.Get("/api/evals/arena/" + battleId)).Body!["revealed"]!.AsArray();
            var winner = revealed.Single(item => item!["letter"]!.GetValue<string>() == "A")!["model"]!.GetValue<string>();
            var loser = revealed.Single(item => item!["letter"]!.GetValue<string>() == "B")!["model"]!.GetValue<string>();
            var winnerName = W5Models.Single(model => model.Id == winner).Name;
            await Expect(page.Locator("[data-arena-summary]")).ToContainTextAsync($"You picked A: {winnerName}.");
            await Expect(page.Locator("[data-arena-body]")).ToContainTextAsync(W5Models.Single(model => model.Id == loser).Name);

            // Pin as test case: A's answer becomes a case of a new suite.
            var pin = page.Locator("[data-arena-pin]");
            await Expect(pin).ToBeVisibleAsync();
            var modal = await page.OpenModalAsync(pin, "modal-test-case");
            var content = modal.Locator("[data-pin-content]");
            await Expect(content).ToHaveAttributeAsync("data-pin-state", "ready");
            await Expect(modal.Locator("[data-pin-reference-text]")).ToContainTextAsync("Answer: In one line");
            await modal.Locator("#tcaseName").FillAsync("Pre-release label");
            await Expect(modal.Locator("#tcaseSuite")).ToHaveValueAsync("new");
            await modal.Locator("#tcaseNewSuite").FillAsync("Release notes");
            await modal.Locator("[data-pin-save]").ClickAsync();
            await Expect(modal).ToBeHiddenAsync();
            await page.ToastAsync("Release notes");
            await page.Locator("#toasts [data-pin-open-suite]").Last.ClickAsync();
            await page.WaitForURLAsync("**/evals/suites/su*");
            var suite = page.Locator("[data-evals='suite']");
            var suiteId = (await suite.GetAttributeAsync("data-suite-id"))!;
            await Expect(page.Locator("[data-suite-name]")).ToHaveTextAsync("Release notes");
            await Expect(page.Locator("[data-suite-cases]")).ToContainTextAsync("Pre-release label");
            await Expect(page.Locator("[data-suite-targets]")).ToContainTextAsync(winner);
            // The keyless custom provider has no account to be active: the target names its key (after the W5 live test).
            await Expect(page.Locator("[data-suite-targets]")).ToContainTextAsync("no key needed");
            await Expect(page.Locator("[data-suite-targets]")).Not.ToContainTextAsync("Active account");

            // Run now: run #1 completes and every answer passes; Set as baseline accepts it.
            await page.Locator("[data-suite-header] [data-run-suite]").ClickAsync();
            await page.WaitForURLAsync("**/evals/runs/sr*");
            await page.W5RunPageEndedAsync(1, "complete");
            var first = (await page.Locator("[data-evals='run']").GetAttributeAsync("data-run-id"))!;
            var run1 = (await app.Get("/api/evals/runs/" + first)).Body!;
            var answers = run1["results"]!.AsArray();
            Check(answers.Count == 3 && answers.All(item => item!["status"]!.GetValue<string>() == "done" && item["verdict"]?.GetValue<string>() == "passed"
                && item["responseModel"]?.GetValue<string>() == winner + "-2026-05"), "run #1: three answers (k = 3) passed from " + winner + "-2026-05: " + answers.ToJsonString());
            var baseline = await page.OpenModalAsync(page.Locator($"[data-eval-run-header] [data-open-baseline][data-run-id='{first}']"), "modal-baseline");
            await Expect(baseline.Locator("[data-bl-run]")).ToHaveValueAsync(first);
            await Expect(baseline.Locator("[data-bl-target]")).ToBeCheckedAsync();
            await baseline.Locator("[data-bl-save]").ClickAsync();
            await page.ToastAsync("Baseline set");
            var accepted = (await app.Get("/api/evals/suites/" + suiteId)).Body!["baselines"]!.AsArray();
            Check(accepted.Count == 1 && accepted[0]!["runId"]?.GetValue<string>() == first, "run #1 is the baseline: " + accepted.ToJsonString());

            // The provider updates its model silently: run #2 sees the change, run #3 confirms it. Run #2's answers wait until its page shows
            // it running; the render that first shows it complete (the page's poll) already has the drift row.
            provider.Suffix = "2026-09";
            await page.AddInitScriptAsync(W5RunRenderProbe);
            provider.Hold();
            await page.Locator("[data-eval-run-header] [data-run-suite]").ClickAsync();
            await page.WaitForURLAsync(url => url.Contains("/evals/runs/sr", StringComparison.Ordinal) && !url.Contains(first, StringComparison.Ordinal));
            await Expect(page.Locator("[data-eval-run-header]")).ToContainTextAsync("Run #2");
            await Expect(page.Locator("[data-eval-run-live]")).ToHaveAttributeAsync("data-running", "true");
            await WaitUntilAsync(() => Task.FromResult(provider.Asked.Count == 2 + 3 + 3), "run #2 asked its three answers, held");
            provider.Release();
            await page.W5RunPageEndedAsync(2, "complete");
            var renders = await page.W5RunRendersAsync();
            Check(renders.Count >= 2 && renders[0] is { How: "load", Status: not "complete" }, "run #2's page opened while it ran: " + string.Join(" | ", renders));
            var shown = renders.First(render => render.Status == "complete");
            Check(shown is { How: "poll", Drift: "drift" }, "the poll that first shows run #2 complete shows its response-model drift too: " + string.Join(" | ", renders));
            var raised = page.Locator("[data-run-signal='response-model']");
            await Expect(raised).ToHaveAttributeAsync("data-level", "drift", new() { Timeout = 15_000 });
            await Expect(raised).ToContainTextAsync($"{winner}-2026-05");
            await Expect(raised).ToContainTextAsync($"{winner}-2026-09");
            await page.Locator("[data-eval-run-header] [data-run-suite]").ClickAsync();
            await page.W5RunPageEndedAsync(3, "complete");
            var confirmed = page.Locator("[data-run-signal='response-model']");
            await Expect(confirmed).ToHaveAttributeAsync("data-level", "drift", new() { Timeout = 15_000 });
            await Expect(confirmed).ToContainTextAsync("Run #2 saw it first, and this run confirms it.");

            // Drift Watch, from the notice.
            await confirmed.Locator("a[href='/evals/drift']").ClickAsync();
            await Expect(page.Locator("[data-evals='drift']")).ToBeVisibleAsync();
            var row = page.Locator($"[data-drift-suite] [data-drift-target]");
            await Expect(row).ToHaveCountAsync(1);
            await Expect(row).ToHaveAttributeAsync("data-level", "drift");
            await Expect(row).ToContainTextAsync(winner);

            // The leaderboard counts the vote, provisional.
            await page.GotoAsync("/evals/leaderboard?tag=general");
            var board = page.Locator("[data-evals='leaderboard']");
            await Expect(board).ToHaveAttributeAsync("data-tag", "general");
            await Expect(page.Locator("[data-provisional]").First).ToBeVisibleAsync();
            var rows = page.Locator("[data-leaderboard-row]");
            await Expect(rows).ToHaveCountAsync(2);
            await Expect(page.Locator($"[data-leaderboard-row][data-model='{W5Provider}/{winner}']")).ToBeVisibleAsync();
            await Expect(page.Locator($"[data-leaderboard-row][data-model='{W5Provider}/{loser}']")).ToBeVisibleAsync();
            Equal(1, (await app.Get("/api/evals/leaderboard?tag=general")).Body?["tags"]?.AsArray()
                .Where(tag => tag?["tag"]?.GetValue<string>() == "general").Select(tag => tag!["votes"]!.GetValue<int>()).SingleOrDefault(), "one vote in general");

            // Two battle calls and three runs of three answers, all to the scripted provider.
            Equal(2 + 9, provider.Asked.Count, "every call reached the scripted provider: " + string.Join(", ", provider.Asked));
            Check(provider.Asked.Skip(2).All(model => model == winner), "the suite asked the pinned model only");
            Equal(0, provider.Other.Count, "nothing else was asked of it: " + string.Join(", ", provider.Other));
        }),
    ];

    /// <summary>One render of a run page's live part (<c>[data-eval-run-live]</c>): as the page loaded or as its poll swapped it in, the run's
    /// status, whether it was still running, and the level of its response-model drift row (empty when there is none).</summary>
    internal sealed record W5RunRender(string How, string Status, string Running, string Drift);

    /// <summary>An init script (W5 fix 2) that records each render of a run page's live part in <c>window.__w5RunRenders</c>: the one the page
    /// loaded with, then every one its poll swaps in.</summary>
    internal const string W5RunRenderProbe = """
        (() => {
          const renders = [];
          window.__w5RunRenders = renders;
          const note = (live, how) => renders.push({
            how, status: live.dataset.runStatus ?? '', running: live.dataset.running ?? '',
            drift: live.querySelector("[data-run-signal='response-model']")?.dataset.level ?? '' });
          document.addEventListener('DOMContentLoaded', () => {
            const live = document.querySelector('[data-eval-run-live]');
            if (!live) return;
            note(live, 'load');
            new MutationObserver((records) => {
              for (const record of records)
                for (const node of record.addedNodes) if (node instanceof Element && node.matches('[data-eval-run-live]')) note(node, 'poll');
            }).observe(document.body, { childList: true, subtree: true });
          });
        })();
        """;

    /// <summary>The renders <see cref="W5RunRenderProbe"/> recorded on the page's current document, in order.</summary>
    internal static async Task<List<W5RunRender>> W5RunRendersAsync(this IPage page)
    {
        var items = await page.EvaluateAsync<System.Text.Json.JsonElement>("() => window.__w5RunRenders ?? []");
        return [.. items.EnumerateArray().Select(item => new W5RunRender(item.GetProperty("how").GetString() ?? "", item.GetProperty("status").GetString() ?? "",
            item.GetProperty("running").GetString() ?? "", item.GetProperty("drift").GetString() ?? ""))];
    }
}
