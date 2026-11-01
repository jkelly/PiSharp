using System.Collections.Concurrent;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W4, fix 2 workspaces): Settings › Tools' "Also skip .gitignore patterns" (#acDefGitignore) is a working switch on
// --faux. Turning it off is one unsaved change in the agent compare save bar; Discard turns it back on; Save posts only the switch
// (useGitignore: false), and a reload shows it off with nothing unsaved; it can be turned on again the same way.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> W4Fix2WorkspacesCases() =>
    [
        Browse("browser.w4.fix2-workspaces.gitignore-switch-saves-and-discards", AgentMode.Faux, async (app, window) =>
        {
            var page = window.Page;
            var posts = new ConcurrentBag<IRequest>();
            page.Request += (_, request) =>
            {
                if (request.Method == "POST" && new Uri(request.Url).AbsolutePath == "/api/settings/agent-compare") posts.Add(request);
            };
            await page.GotoAsync("/settings/tools");
            var toggle = page.Locator("#acDefGitignore");
            var bar = page.Locator("[data-ac-savebar]");
            await Expect(toggle).ToBeEnabledAsync();
            await Expect(toggle).ToBeCheckedAsync();
            await Expect(bar).ToBeHiddenAsync();

            await toggle.ClickAsync();
            await Expect(toggle).Not.ToBeCheckedAsync();
            await Expect(bar).ToBeVisibleAsync();
            await Expect(page.Locator("[data-ac-savebar-count]")).ToHaveTextAsync("1 unsaved change");
            await Expect(page.Locator("[data-ac-savebar-names]")).ToHaveTextAsync("agentCompare.useGitignore");
            await page.Locator("[data-ac-discard]").ClickAsync();
            await Expect(toggle).ToBeCheckedAsync();
            await Expect(bar).ToBeHiddenAsync();
            Equal(0, posts.Count, "Discard posts nothing");

            await toggle.ClickAsync();
            await page.Locator("[data-ac-save]").ClickAsync();
            await page.ToastAsync("Agent compare defaults saved");
            await Expect(bar).ToBeHiddenAsync();
            Equal("{\"useGitignore\":false}", posts.Single().PostData, "Save posts the switch alone");
            Equal(false, (await app.Get("/api/settings/agent-compare")).Body!["useGitignore"]!.GetValue<bool>(), "stored off");

            await page.ReloadAsync();
            await Expect(toggle).Not.ToBeCheckedAsync();
            await Expect(toggle).ToBeEnabledAsync();
            await Expect(bar).ToBeHiddenAsync();
            await toggle.ClickAsync();
            await Expect(page.Locator("[data-ac-savebar-count]")).ToHaveTextAsync("1 unsaved change");
            await page.Locator("[data-ac-save]").ClickAsync();
            await page.ToastAsync("Agent compare defaults saved");
            Equal(true, (await app.Get("/api/settings/agent-compare")).Body!["useGitignore"]!.GetValue<bool>(), "stored on again");
            await Expect(toggle).ToBeCheckedAsync();
        }),
    ];
}
