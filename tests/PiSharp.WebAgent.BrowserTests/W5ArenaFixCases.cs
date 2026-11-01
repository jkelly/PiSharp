using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W5, fix 1 arena): two Arena cases the first W5 browser cases left out.
//   1. The vote bar's Rank pane, on a ready battle of three faux answers: Rank hides the pick buttons and shows one select per place, and
//      Pick one hides the selects and shows the buttons again (the panes are .d-flex, which beat [hidden] until arena.css said otherwise).
//   2. An agent battle on the scripted provider (W5SupportCases), whose names (zephyr, kestrel, osprey) no page shows unless it names a
//      contestant: until the vote, the create answer, the battle's JSON while it runs, its event stream, the page (as served and as the tab
//      shows it), its body fragment and both diffs name none, and neither do History, /api/runs, History › Spend's ledger, the ledger's
//      JSON and CSV, or Settings › Data. The vote reveals them.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> W5ArenaFixCases() =>
    [
        Browse("browser.w5.arena.the-rank-pane-and-the-pick-pane-take-turns", AgentMode.FauxMetered, async (app, window) =>
        {
            var page = window.Page;
            var battle = await app.W5BattleAsync("Name three prime numbers below twenty.", "ready", "echo", "echo-small", "echo-thinking");
            await page.GotoAsync("/compare/arena/" + battle);
            await Expect(page.Locator("[data-arena-battle]")).ToHaveAttributeAsync("data-status", "ready");
            var picks = page.Locator("[data-arena-vote='pick']");
            var ranks = page.Locator("[data-arena-rank]");
            await Expect(picks).ToHaveCountAsync(3);
            await Expect(ranks).ToHaveCountAsync(3);
            await Expect(picks.First).ToBeVisibleAsync();
            await Expect(ranks.First).ToBeHiddenAsync();

            await page.Locator("[data-arena-show-pane='av-rank']").ClickAsync();
            await Expect(page.Locator("[data-arena-show-pane='av-rank']")).ToHaveAttributeAsync("aria-pressed", "true");
            for (var index = 0; index < 3; index++)
            {
                await Expect(picks.Nth(index)).ToBeHiddenAsync();
                await Expect(ranks.Nth(index)).ToBeVisibleAsync();
            }
            await Expect(page.Locator("[data-arena-vote='tie']")).ToBeHiddenAsync();
            await Expect(page.Locator("[data-arena-vote='rank']")).ToBeVisibleAsync();
            await Expect(page.Locator("[data-pane-only='av-rank']")).ToBeVisibleAsync();
            await Expect(page.Locator("[data-pane-only='av-pick']")).ToBeHiddenAsync();

            await page.Locator("[data-arena-show-pane='av-pick']").ClickAsync();
            await Expect(page.Locator("[data-arena-show-pane='av-pick']")).ToHaveAttributeAsync("aria-pressed", "true");
            for (var index = 0; index < 3; index++)
            {
                await Expect(picks.Nth(index)).ToBeVisibleAsync();
                await Expect(ranks.Nth(index)).ToBeHiddenAsync();
            }
            await Expect(page.Locator("[data-arena-vote='rank']")).ToBeHiddenAsync();
            await Expect(page.Locator("[data-pane-only='av-pick']")).ToBeVisibleAsync();
            await Expect(page.Locator("[data-pane-only='av-rank']")).ToBeHiddenAsync();
        }),

        BrowseScripted("browser.w5.arena.an-agent-battle-names-no-contestant-anywhere-before-the-vote", async (app, provider, window) =>
        {
            var page = window.Page;
            var project = Path.Join(app.Root, "project");
            Directory.CreateDirectory(project);
            await File.WriteAllTextAsync(Path.Join(project, "README.md"), "# Sample\n");
            var created = await app.PostAsync("/api/evals/arena", new JsonObject
            {
                ["mode"] = "agent",
                ["agent"] = new JsonObject
                {
                    ["requestId"] = "w5-arena-agent-1",
                    ["task"] = "In one line: what is a semantic version?",
                    ["projectPath"] = project,
                    ["models"] = new JsonArray([.. W5Models.Select(model => (JsonNode)new JsonObject { ["provider"] = W5Provider, ["model"] = model.Id })]),
                    ["isolation"] = "copy",
                    ["approval"] = "auto",
                },
            });
            Equal(200, created.Status, "a blind agent battle: " + created.Text);
            W5Blind(created.Text, "the create answer");
            var id = created.Body!["battleId"]!.GetValue<string>();
            var events = W5EventsAsync(app, "/api/evals/arena/" + id + "/events");
            JsonNode? state = null;
            await WaitUntilAsync(async () =>
            {
                var answer = await app.Get("/api/evals/arena/" + id);
                W5Blind(answer.Text, "the battle's JSON while it runs");
                state = answer.Body;
                return state?["status"]?.GetValue<string>() is "ready" or "void";
            }, $"agent battle {id} finished", TimeSpan.FromSeconds(90));
            Equal("ready", state?["status"]?.GetValue<string>(), "both agents are done: " + state?.ToJsonString());
            W5Blind(await events, "the battle's event stream");

            await page.GotoAsync("/compare/arena/" + id);
            await Expect(page.Locator("[data-arena-battle]")).ToHaveAttributeAsync("data-status", "ready");
            await Expect(page.Locator("[data-arena-diff='A']").First).ToBeVisibleAsync();
            W5Blind(await page.ContentAsync(), "the agent battle page in the tab");
            W5Blind((await app.Get("/compare/arena/" + id)).Text, "the agent battle page as served");
            W5Blind((await app.Get("/compare/arena/" + id + "/body")).Text, "the agent battle's body fragment");
            foreach (var letter in new[] { "A", "B" })
            {
                var diff = await app.Get($"/compare/arena/{id}/diff?letter={letter}");
                Equal(200, diff.Status, "diff " + letter + ": " + diff.Text);
                W5Blind(diff.Text, "diff " + letter);
            }

            // Outside the battle too.
            var history = await app.Get("/history");
            W5Blind(history.Text, "History");
            Check(history.Text.Contains("data-run-blind", StringComparison.Ordinal), "History lists the round masked");
            var runs = await app.Get("/api/runs");
            W5Blind(runs.Text, "/api/runs");
            Check(runs.Body?["items"]?.AsArray().Any(item => item?["blind"] is not null) == true, "/api/runs lists the round masked: " + runs.Text);
            var spend = await app.Get("/history/spend");
            var table = W5Region(spend.Text, "data-spend-ledger", "</table>");
            W5Blind(table, "History › Spend's ledger");
            Check(table.Contains("data-spend-blind", StringComparison.Ordinal), "History › Spend lists the round's requests masked");
            var ledger = await app.Get("/api/spend/ledger");
            W5Blind(ledger.Text, "/api/spend/ledger");
            Check(ledger.Body?["items"]?.AsArray() is { Count: > 0 } items && items.All(item => item?["blind"] is not null), "the ledger lists them masked: " + ledger.Text);
            W5Blind((await app.Get("/api/spend/ledger.csv")).Text, "the ledger CSV");
            var data = await app.Get("/settings/data");
            W5Blind(data.Text, "Settings › Data");
            Check(System.Net.WebUtility.HtmlDecode(data.Text).Contains("blind round · workspace a", StringComparison.Ordinal), "Settings › Data lists the workspaces without their models");

            // The vote reveals them.
            var voted = await app.PostAsync("/api/evals/arena/" + id + "/vote", new JsonObject { ["kind"] = "tie" });
            Check(voted.Status == 200 && voted.Body?["revealed"] is JsonArray, "a vote: " + voted.Text);
            await page.ReloadAsync();
            await Expect(page.Locator("[data-arena-battle]")).ToContainTextAsync("kestrel-7b");
            await Expect(page.Locator("[data-arena-battle]")).ToContainTextAsync("osprey-9b");
            Check((await app.Get("/api/runs")).Body?["items"]?.AsArray().All(item => item?["blind"] is null) == true, "/api/runs unmasks the round after the vote");
            Check(provider.Other.Count == 0, "every call went to the scripted provider's completions: " + string.Join(", ", provider.Other));
        }),
    ];

    /// <summary>The part of <paramref name="html"/> from <paramref name="start"/> to <paramref name="end"/> (all of it when either is missing).</summary>
    private static string W5Region(string html, string start, string end)
    {
        var from = html.IndexOf(start, StringComparison.Ordinal);
        var to = from < 0 ? -1 : html.IndexOf(end, from, StringComparison.Ordinal);
        return from >= 0 && to > from ? html[from..to] : html;
    }

    /// <summary>Reads an event stream of <paramref name="app"/> to its end (120 s at most).</summary>
    private static async Task<string> W5EventsAsync(W5LiveApp app, string path)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(app.Base, path));
        using var response = await app.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
        Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType, "an event stream");
        await using var stream = await response.Content.ReadAsStreamAsync(limit.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(limit.Token);
    }
}
