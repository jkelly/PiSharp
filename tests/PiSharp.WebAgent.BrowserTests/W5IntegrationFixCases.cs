using System.Text.Json.Nodes;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W5, integration fix 1): in the browser, what the server cases (PiSharp.WebAgent.Tests' W5IntegrationFixCases) cannot
// see. Compare's ?blind=1 (the leaderboard's Blind vote links) turns Blind on for that visit, on the request and the agent setups, without
// remembering it; and Settings › Tools' "Always allowed this session" lists a chat's "Always allow in this chat" grant and its Revoke button
// takes it away, so the next call of that kind asks again.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> W5IntegrationFixCases() =>
    [
        Browse("browser.w5-integration.compare-blind-1-turns-blind-on-for-the-visit", async (agent, window) =>
        {
            var page = window.Page;
            foreach (var (path, id) in new[] { ("/compare", "cmpBlind"), ("/compare?mode=agent", "acBlind") })
            {
                await page.GotoAsync(path);
                await Expect(page.Locator($"#{id}[data-arena-blind]")).Not.ToBeCheckedAsync();
                await page.GotoAsync(path + (path.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "blind=1");
                await Expect(page.Locator($"#{id}[data-arena-blind]")).ToBeCheckedAsync();
                await page.GotoAsync(path);
                await Expect(page.Locator($"#{id}[data-arena-blind]")).Not.ToBeCheckedAsync();
            }
        }),

        Browse("browser.w5-integration.settings-tools-revokes-a-session-grant", async (agent, window) =>
        {
            var chat = await agent.CreateChatAsync("!echo granted >> marker.txt", "w5f-grant");
            JsonNode? approvals = null;
            await WaitUntilAsync(async () => (approvals = (await agent.Get($"/api/chats/{chat}/approvals")).Body?["items"])?.AsArray().Count == 1,
                "the bash call asks", TimeSpan.FromSeconds(30));
            var task = approvals![0]!["taskId"]!.GetValue<long>();
            var decided = await agent.PostAsync($"/api/chats/{chat}/approvals/{task}", new JsonObject { ["decision"] = "allowChat" });
            Equal(200, decided.Status, "always allowed in this chat: " + decided.Text);
            await agent.WaitIdleAsync(chat);

            var page = window.Page;
            await page.GotoAsync("/settings/tools");
            var card = page.Locator("[data-session-grants]");
            await Expect(card.Locator("[data-session-grant]")).ToHaveCountAsync(1);
            await Expect(card.Locator($"[data-session-grant][data-chat='{chat}'][data-tool='bash']")).ToBeVisibleAsync();
            await Expect(card.Locator("[data-session-grants-empty]")).ToBeHiddenAsync();
            await card.Locator("[data-grant-revoke]").ClickAsync();
            await Expect(card.Locator("[data-session-grant]")).ToHaveCountAsync(0);
            await Expect(card.Locator("[data-session-grants-empty]")).ToBeVisibleAsync();
            Equal(0, (await agent.Get($"/api/chats/{chat}/grants")).Body?["items"]?.AsArray().Count, "the chat has no grant left");
            await page.ReloadAsync();
            await Expect(page.Locator("[data-session-grant]")).ToHaveCountAsync(0);
        }),
    ];
}
