using System.Text.Json.Nodes;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W5, fix 2, tests-ci): what a blind battle's own controls do in a browser, failing on any page or console error. Each
// one goes through the page and is read back from the Arena's API:
//   - A request battle (priced faux, a slow: answer, so it is held): Stream anyway shows both answers as they stream; Stop all (after its
//     confirmation) voids the round, which is revealed and never counted; New round opens a fresh battle on the same prompt and models; and
//     the vote bar's New tag… refuses an empty name before anything is posted, then saves the vote under the typed tag (normalized), the
//     only vote the leaderboard counts.
//   - An agent battle with tools on Ask: each column asks before its bash call; A is allowed and B denied, then the vote's Keep A… opens
//     the run, where A's call ran in its own copy and B's never ran.
//   - An agent battle on priced faux with a run cap of $0.10 on Ask: each column's first turn would pass its share ($0.05), so both ask;
//     A goes on for the rest of the run, B for one turn, which asks again before its next turn, and then for the rest; both finish.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> W5ArenaPlayCases() =>
    [
        Browse("browser.w5.arena.stream-anyway-stop-all-new-round-and-a-new-tag", AgentMode.FauxMetered, async (app, window) =>
        {
            var page = window.Page;
            var first = await app.W5BattleAsync("slow: " + Words(250), "held", "echo", "echo-small");
            await page.GotoAsync("/compare/arena/" + first);
            var root = page.Locator("[data-arena]");
            await Expect(root).ToHaveAttributeAsync("data-status", "held");
            await Expect(page.Locator("[data-arena-held]")).ToHaveCountAsync(2);
            await Expect(page.Locator("[data-arena-stop]")).ToBeVisibleAsync();
            await Expect(page.Locator("[data-arena-again]")).ToBeHiddenAsync();

            // Stream anyway: both answers show as they stream, and the status says so.
            var streamAnyway = page.Locator("[data-arena-stream-anyway]");
            await Expect(streamAnyway).Not.ToBeCheckedAsync();
            await Expect(streamAnyway).ToBeEnabledAsync();
            var switched = await page.RunAndWaitForResponseAsync(() => streamAnyway.CheckAsync(),
                response => response.Url.EndsWith("/stream-anyway", StringComparison.Ordinal) && response.Request.Method == "POST");
            Equal(200, switched.Status, "Stream anyway was saved");
            await Expect(page.Locator("[data-arena-held]")).ToHaveCountAsync(0);
            var streaming = page.Locator("[data-arena-streaming]");
            await Expect(streaming).ToHaveCountAsync(2);
            await Expect(streaming.First).ToContainTextAsync("w00");
            await Expect(streaming.Last).ToContainTextAsync("w00");
            await Expect(page.Locator("[data-arena-status]")).ToContainTextAsync("streaming as they come");
            var shown = (await streaming.First.TextContentAsync())!.Length;
            await WaitUntilAsync(async () => await streaming.CountAsync() == 2 && (await streaming.First.TextContentAsync())!.Length > shown,
                "the streamed answer grows on the page", TimeSpan.FromSeconds(20));
            var streamed = (await app.Get("/api/evals/arena/" + first)).Body!;
            Check(streamed["streamAnyway"]?.GetValue<bool>() == true && streamed["status"]?.GetValue<string>() == "held",
                "the battle streams anyway and is still held: " + streamed.ToJsonString());

            // Stop all, after its confirmation: the round is void and revealed, and New round takes Stop all's place.
            await page.Locator("[data-arena-stop]").ClickAsync();
            await page.ModalShownAsync("modal-confirm");
            await Expect(page.Locator("#modal-confirm")).ToContainTextAsync("Stop this round?");
            await Expect(page.Locator("#modal-confirm [data-confirm-ok]")).ToHaveTextAsync("Stop all");
            await page.Locator("#modal-confirm [data-confirm-ok]").ClickAsync();
            await Expect(root).ToHaveAttributeAsync("data-status", "void");
            await Expect(page.Locator("[data-arena-stop]")).ToBeHiddenAsync();
            await Expect(page.Locator("[data-arena-again]")).ToBeVisibleAsync();
            var stopped = (await app.Get("/api/evals/arena/" + first)).Body!;
            Equal("void", stopped["status"]?.GetValue<string>(), "Stop all voided the round");
            Check(stopped["revealed"] is JsonArray { Count: 2 } && stopped["vote"] is null, "a stopped round is revealed, with no vote: " + stopped.ToJsonString());

            // New round: a fresh battle on the same prompt and models, followed until every answer is in.
            await page.Locator("[data-arena-again]").ClickAsync();
            await page.WaitForURLAsync(url => url.Contains("/compare/arena/", StringComparison.Ordinal) && !url.Contains(first, StringComparison.Ordinal));
            var next = new Uri(page.Url).AbsolutePath.Split('/').Last();
            Check(next.Length > 0 && next != first, "New round opened another battle: " + page.Url);
            await Expect(root).ToHaveAttributeAsync("data-status", "ready", new() { Timeout = 60_000 });
            await Expect(page.Locator("[data-arena-answer]")).ToHaveCountAsync(2);
            await Expect(page.Locator("[data-arena-answer]").First).ToContainTextAsync("w250");
            var round = (await app.Get("/api/evals/arena/" + next)).Body!;
            Check(round["status"]?.GetValue<string>() == "ready" && round["mode"]?.GetValue<string>() == "request" && round["total"]?.GetValue<int>() == 2,
                "the new round is a ready request battle of two: " + round.ToJsonString());

            // New tag…: an empty name is refused before anything is posted; the typed one is saved (normalized) with the vote.
            var tag = page.Locator("[data-arena-tag]");
            var input = page.Locator("[data-arena-tag-input]");
            await Expect(input).ToBeHiddenAsync();
            var label = (await page.Locator("[data-arena-tag] [data-arena-new-tag]").TextContentAsync())!.Trim();
            Equal("New tag…", label, "the tag list ends with New tag…");
            await tag.SelectOptionAsync(new SelectOptionValue { Label = label });
            await Expect(input).ToBeVisibleAsync();
            await Expect(input).ToBeFocusedAsync();
            var pickA = page.Locator("[data-arena-vote='pick'][data-letter='A']");
            await pickA.ClickAsync();
            await Expect(page.Locator("[data-arena-vote-error]")).ToHaveTextAsync("Name the new tag first.");
            await Expect(input).ToBeFocusedAsync();
            Equal("ready", (await app.Get("/api/evals/arena/" + next)).Body?["status"]?.GetValue<string>(), "nothing was posted for the unnamed tag");
            await input.FillAsync("  SemVer   Rules ");
            await pickA.ClickAsync();
            await Expect(root).ToHaveAttributeAsync("data-status", "voted");
            await Expect(page.Locator("[data-arena-voted='pick']")).ToBeVisibleAsync();
            var voted = (await app.Get("/api/evals/arena/" + next)).Body!;
            Equal("semver rules", voted["vote"]?["tag"]?.GetValue<string>(), "the vote is saved under the typed tag: " + voted.ToJsonString());
            Equal("A", voted["vote"]?["pick"]?.GetValue<string>(), "picking A");
            var board = (await app.Get("/api/evals/leaderboard?mode=request")).Body!;
            var tags = board["tags"]!.AsArray().Where(item => item?["mode"]?.GetValue<string>() == "request").ToList();
            Check(tags.Count == 1 && tags[0]!["tag"]!.GetValue<string>() == "semver rules" && tags[0]!["votes"]!.GetValue<int>() == 1,
                "the leaderboard counts the one vote under the new tag, and never the stopped round: " + board.ToJsonString());
        }),

        Browse("browser.w5.arena.an-agent-battle-asks-before-its-tools-and-keep-opens-its-run", async (app, window) =>
        {
            var page = window.Page;
            var created = await app.PostAsync("/api/evals/arena", new JsonObject
            {
                ["mode"] = "agent",
                ["agent"] = new JsonObject
                {
                    ["requestId"] = "w5-play-approve",
                    ["task"] = "!echo done > result.txt",
                    ["projectPath"] = app.W4Project("arena-approve"),
                    ["models"] = W5FauxModels("echo", "echo-small"),
                    ["isolation"] = "copy",
                    ["approval"] = "ask",
                },
            });
            Equal(200, created.Status, "a blind agent battle with tools on Ask: " + created.Text);
            var id = created.Body!["battleId"]!.GetValue<string>();
            await page.GotoAsync("/compare/arena/" + id);
            var root = page.Locator("[data-arena]");
            await Expect(root).ToHaveAttributeAsync("data-status", "held");

            // Each column asks before its bash call.
            var cards = page.Locator("[data-arena-approval]");
            await Expect(cards).ToHaveCountAsync(2, new() { Timeout = 30_000 });
            await Expect(page.Locator("[data-arena-col='A'] [data-arena-approval]")).ToContainTextAsync("echo done > result.txt");
            await Expect(page.Locator("[data-arena-col='B'] [data-arena-approval]")).ToContainTextAsync("echo done > result.txt");

            // A is allowed, B denied; then both finish and the vote opens.
            await page.Locator("[data-arena-col='A'] [data-arena-approve='allow'][data-letter='A']").ClickAsync();
            await Expect(page.Locator("[data-arena-col='A'] [data-arena-approval]")).ToHaveCountAsync(0);
            await Expect(page.Locator("[data-arena-col='B'] [data-arena-approval]")).ToHaveCountAsync(1);
            await page.Locator("[data-arena-col='B'] [data-arena-approve='deny'][data-letter='B']").ClickAsync();
            await Expect(root).ToHaveAttributeAsync("data-status", "ready", new() { Timeout = 60_000 });
            await Expect(cards).ToHaveCountAsync(0);

            // The vote; Keep A… opens the run, where A's call ran in its own copy and B's never ran.
            await page.Locator("[data-arena-vote='pick'][data-letter='A']").ClickAsync();
            await Expect(root).ToHaveAttributeAsync("data-status", "voted");
            var keep = page.Locator("[data-arena-keep]");
            await Expect(keep).ToBeVisibleAsync();
            await Expect(keep).ToHaveTextAsync("Keep A…");
            var href = (await keep.GetAttributeAsync("href"))!;
            Check(href.StartsWith("/compare?mode=agent&run=", StringComparison.Ordinal), "Keep opens the agent run: " + href);
            var run = Uri.UnescapeDataString(href["/compare?mode=agent&run=".Length..]);
            await keep.ClickAsync();
            await page.WaitForURLAsync(url => url.Contains("/compare?mode=agent&run=", StringComparison.Ordinal));
            await Expect(page.Locator("[data-ac-keep]").First).ToBeVisibleAsync();
            var status = (await app.Get("/api/compare/agent/" + run)).Body!;
            var models = status["models"]!.AsArray();
            JsonNode Slot(string slot) => models.Single(model => model?["slot"]?.GetValue<string>() == slot)!;
            Check(File.Exists(Path.Join(Slot("a")["workingDirectory"]!.GetValue<string>(), "result.txt")), "A's allowed call ran: " + Slot("a").ToJsonString());
            Check(!File.Exists(Path.Join(Slot("b")["workingDirectory"]!.GetValue<string>(), "result.txt")), "B's denied call never ran: " + Slot("b").ToJsonString());
        }),

        Browse("browser.w5.arena.an-agent-battle-asks-to-go-over-its-share-and-continue-answers", AgentMode.FauxMetered, async (app, window) =>
        {
            var page = window.Page;
            var created = await app.PostAsync("/api/evals/arena", new JsonObject
            {
                ["mode"] = "agent",
                ["agent"] = new JsonObject
                {
                    ["requestId"] = "w5-play-budget",
                    ["task"] = "!echo spent > spent.txt",
                    ["projectPath"] = app.W4Project("arena-budget"),
                    ["models"] = W5FauxModels("echo", "echo-small"),
                    ["isolation"] = "copy",
                    ["approval"] = "auto",
                    ["capUsd"] = 0.10,
                    ["capAction"] = "ask",
                },
            });
            Equal(200, created.Status, "a blind agent battle with a run cap on Ask: " + created.Text);
            var id = created.Body!["battleId"]!.GetValue<string>();
            await page.GotoAsync("/compare/arena/" + id);
            var root = page.Locator("[data-arena]");
            await Expect(root).ToHaveAttributeAsync("data-status", "held");

            // Each column's first turn would pass its share: both ask, and name no amount before the vote.
            var asks = page.Locator("[data-arena-budget]");
            await Expect(asks).ToHaveCountAsync(2, new() { Timeout = 30_000 });
            await Expect(asks.First).ToContainTextAsync("The amounts show after you vote.");
            Check(!(await asks.First.InnerTextAsync()).Contains('$'), "the ask names no amount before the vote");

            // A goes on for the rest of the run: it finishes without asking again.
            await page.Locator("[data-arena-col='A'] [data-arena-continue='run'][data-letter='A']").ClickAsync();
            await Expect(page.Locator("[data-arena-col='A'] [data-arena-budget]")).ToHaveCountAsync(0);

            // B goes on for one turn, which runs its call; its next turn asks again, and then it goes on for the rest.
            await page.Locator("[data-arena-col='B'] [data-arena-continue='turn'][data-letter='B']").ClickAsync();
            var askedAgain = page.Locator("[data-arena-col='B'] [data-arena-budget]");
            await WaitUntilAsync(async () => await askedAgain.CountAsync() == 0 || (await root.GetAttributeAsync("data-status")) != "held",
                "B's first ask was answered", TimeSpan.FromSeconds(15));
            await Expect(askedAgain).ToHaveCountAsync(1, new() { Timeout = 30_000 });
            Equal("held", await root.GetAttributeAsync("data-status"), "the battle waits for B's second answer");
            await page.Locator("[data-arena-col='B'] [data-arena-continue='run'][data-letter='B']").ClickAsync();
            await Expect(root).ToHaveAttributeAsync("data-status", "ready", new() { Timeout = 60_000 });
            await Expect(asks).ToHaveCountAsync(0);

            // After the vote the run shows both finished, B after two turns it was let through.
            await page.Locator("[data-arena-vote='tie']").ClickAsync();
            await Expect(root).ToHaveAttributeAsync("data-status", "voted");
            var href = (await page.Locator("[data-arena-keep]").GetAttributeAsync("href"))!;
            var run = Uri.UnescapeDataString(href["/compare?mode=agent&run=".Length..]);
            var status = (await app.Get("/api/compare/agent/" + run)).Body!;
            var models = status["models"]!.AsArray();
            JsonNode Slot(string slot) => models.Single(model => model?["slot"]?.GetValue<string>() == slot)!;
            foreach (var slot in new[] { "a", "b" })
            {
                Equal("finished", Slot(slot)["outcome"]?.GetValue<string>(), $"{slot} finished: " + Slot(slot).ToJsonString());
                Check(File.Exists(Path.Join(Slot(slot)["workingDirectory"]!.GetValue<string>(), "spent.txt")), $"{slot}'s call ran");
            }
        }),
    ];

    /// <summary>Faux <paramref name="models"/> as an agent run's <c>models</c>.</summary>
    private static JsonArray W5FauxModels(params string[] models) =>
        new([.. models.Select(model => (JsonNode)new JsonObject { ["provider"] = "faux", ["model"] = model })]);
}
