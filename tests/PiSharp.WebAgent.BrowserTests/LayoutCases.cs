using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// The main pages on a phone (decision 0011, run W2, WU10): at 375 × 812, in the dark theme (the default) and then in the light theme the
// toggle stores, no page scrolls sideways. A chat with an answer, a tool call and an approval card waiting stands for the chat page.
internal static partial class Program
{
    private static readonly string[] PhonePages =
    [
        "/chat/new", "/settings/providers", "/settings/providers/openai", "/settings/providers/faux", "/settings/accounts", "/settings/models",
        "/settings/defaults", "/settings/tools", "/playground",
    ];

    private static IEnumerable<(string, Func<Task>)> LayoutCases() =>
    [
        Browse("browser.layout.the-main-pages-fit-a-phone-in-both-themes", async (agent, window) =>
        {
            var answered = await agent.CreateChatAsync("slow: " + Words(40), "phone-answer");
            await agent.WaitIdleAsync(answered);
            var waiting = await agent.CreateChatAsync("!echo phone >> marker.txt", "phone-approval");
            await WaitUntilAsync(async () => (await agent.Get($"/api/chats/{waiting}/approvals")).Body?["items"]?.AsArray().Count == 1,
                "the bash call waits for approval", TimeSpan.FromSeconds(30));
            var page = window.Page;

            foreach (var theme in new[] { "dark", "light" })
            {
                foreach (var path in PhonePages)
                {
                    await page.GotoAsync(path);
                    await FitsAsync(page, path, theme);
                }
                foreach (var chat in new[] { answered, waiting })
                {
                    await page.OpenChatAsync(chat);
                    if (chat == waiting) await Expect(page.Thread().Locator(".awaiting-card[data-approval]")).ToBeVisibleAsync();
                    else await Expect(page.Answers().Last).ToContainTextAsync("w040");
                    await FitsAsync(page, "/chat/" + chat, theme);
                }
                if (theme == "dark")
                {
                    var toggle = page.Locator("[data-theme-toggle] >> visible=true").First;
                    await toggle.ClickAsync();
                    await Expect(page.Locator("html")).ToHaveAttributeAsync("data-bs-theme", "light");
                    Equal("light", await page.EvaluateAsync<string?>("() => localStorage.getItem('pisharp-theme')"), "the toggle stores the theme");
                }
            }
            Equal(0, agent.Lines("marker.txt").Length, "looking at the approval decided nothing");
        }, width: 375, height: 812),
    ];

    /// <summary>The page shows <paramref name="theme"/> and does not scroll sideways.</summary>
    private static async Task FitsAsync(IPage page, string path, string theme)
    {
        await Expect(page.Locator("html")).ToHaveAttributeAsync("data-bs-theme", theme);
        await Expect(page.Locator(".app-topbar")).ToBeVisibleAsync();
        if (await page.OverflowsAsync())
            throw new InvalidOperationException($"{path} ({theme}) scrolls sideways at {page.ViewportSize?.Width}px: " + await page.WideElementsAsync());
    }
}
