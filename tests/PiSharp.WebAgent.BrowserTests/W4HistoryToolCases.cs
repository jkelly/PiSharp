using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W4, fix 2, tests-ci): what History › Runs' script (history.js and history-actions.js) does beyond T4's list, filter,
// inspect and bulk delete, in a browser on faux, failing on any page or console error. Saved filters: Save current filters… (offered only
// while a filter is set) names the page's query, the Saved filters menu then lists it beside the three starter filters and opens it
// (marked current), and Manage saved filters renames it and removes a starter filter (a removal can be undone until Save changes). Page
// size: 27 runs show 25 a page with a second page; Rows per page 50 shows all 27 and keeps the size when a filter changes (25 drops it from
// the address again). Clear history: a run pinned from its row menu stays when "Keep pinned runs" is checked and every other run goes;
// cleared again without it, History is empty.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> W4HistoryToolCases() =>
    [
        Browse("browser.w4.history.saved-filters-are-saved-opened-renamed-and-removed", async (app, window) =>
        {
            var first = await app.PlaygroundRunAsync("saved filters one");
            var (_, columns) = await app.CompareGroupAsync("saved filters compare", "echo", "echo-small");
            var page = window.Page;
            var menu = page.Locator("[data-saved-filters]");
            var toggle = page.Locator(".dropdown:has([data-saved-filters]) > [data-bs-toggle='dropdown']");

            // Without a filter there is nothing to save; the three starter filters are listed.
            await page.GotoAsync("/history");
            await toggle.ClickAsync();
            await Expect(menu).ToBeVisibleAsync();
            await Expect(menu.Locator("[data-saved-filter]")).ToHaveCountAsync(3);
            foreach (var name in new[] { "Errors only", "Tool calls only", "Playground only" })
                await Expect(menu.Locator($"[data-saved-filter='{name}']")).Not.ToHaveAttributeAsync("aria-current", "true");
            await Expect(menu.Locator("[data-bs-target='#modal-save-run-filter']")).ToBeDisabledAsync();
            await Expect(menu.Locator("[data-bs-target='#modal-manage-run-filters']")).ToBeEnabledAsync();
            await page.Keyboard.PressAsync("Escape");

            // Save a filtered view: it is listed, marked current.
            await W4SaveRunFilterAsync(page, "/history?source=compare", "Compare runs");
            await toggle.ClickAsync();
            await Expect(menu.Locator("[data-saved-filter]")).ToHaveCountAsync(4);
            await Expect(menu.Locator("[data-saved-filter='Compare runs']")).ToHaveAttributeAsync("aria-current", "true");
            await Expect(menu.Locator("[data-saved-filter='Errors only']")).Not.ToHaveAttributeAsync("aria-current", "true");
            await page.Keyboard.PressAsync("Escape");

            // Opened from the menu on the unfiltered page, a saved filter is the page's filter, marked current.
            await page.GotoAsync("/history");
            await toggle.ClickAsync();
            await menu.Locator("[data-saved-filter='Compare runs']").ClickAsync();
            await Expect(page).ToHaveURLAsync(new Regex(@"/history\?source=compare$"));
            await Expect(page.Locator("[data-run-history]")).ToHaveAttributeAsync("data-state", "filtered");
            await Expect(page.Locator("tr[data-run-row]")).ToHaveCountAsync(2);
            foreach (var column in columns) await Expect(page.Locator($"tr#run-{column}")).ToBeVisibleAsync();
            await Expect(page.Locator($"tr#run-{first}")).ToHaveCountAsync(0);
            await Expect(page.Locator("[data-run-count]")).ToHaveTextAsync("2 of 3 runs");
            await toggle.ClickAsync();
            await Expect(menu.Locator("[data-saved-filter='Compare runs']")).ToHaveAttributeAsync("aria-current", "true");

            // Manage: rename one, remove another (Undo brings it back until Save changes).
            var manage = await page.OpenModalAsync(menu.Locator("[data-bs-target='#modal-manage-run-filters']"), "modal-manage-run-filters");
            await Expect(manage.Locator("[data-rmf-row]")).ToHaveCountAsync(4);
            var compare = manage.Locator("[data-rmf-row][data-query='source=compare']");
            var tools = manage.Locator("[data-rmf-row][data-query='status=toolUse']");
            await Expect(compare.Locator("[data-rmf-name]")).ToHaveValueAsync("Compare runs");
            await compare.Locator("[data-rmf-name]").FillAsync("Compare columns");
            await tools.Locator("[data-rmf-remove]").ClickAsync();
            await Expect(tools.Locator("[data-rmf-removed]")).ToBeVisibleAsync();
            await Expect(tools.Locator("[data-rmf-name]")).ToBeDisabledAsync();
            await tools.Locator("[data-rmf-undo]").ClickAsync();
            await Expect(tools.Locator("[data-rmf-removed]")).ToBeHiddenAsync();
            await Expect(tools.Locator("[data-rmf-name]")).ToBeEnabledAsync();
            await tools.Locator("[data-rmf-remove]").ClickAsync();
            await Expect(tools).ToHaveAttributeAsync("data-removed", "true");
            await manage.Locator("[type='submit']").ClickAsync();
            await page.ToastAsync("Saved filters updated");
            await toggle.ClickAsync();
            await Expect(menu.Locator("[data-saved-filter]")).ToHaveCountAsync(3);
            await Expect(menu.Locator("[data-saved-filter='Compare columns']")).ToHaveAttributeAsync("href", new Regex(@"source=compare"));
            await Expect(menu.Locator("[data-saved-filter='Compare columns']")).ToHaveAttributeAsync("aria-current", "true");
            await Expect(menu.Locator("[data-saved-filter='Tool calls only']")).ToHaveCountAsync(0);
            await Expect(menu.Locator("[data-saved-filter='Compare runs']")).ToHaveCountAsync(0);
            await Expect(menu.Locator("[data-saved-filter='Errors only']")).ToHaveCountAsync(1);
        }),

        Browse("browser.w4.history.page-size-and-clear-history-keeping-a-pinned-run", async (app, window) =>
        {
            var runs = new List<string>();
            for (var i = 1; i <= 27; i++) runs.Add(await app.PlaygroundRunAsync($"page size run {i:00}"));
            var page = window.Page;
            var rows = page.Locator("tr[data-run-row]");
            var pagination = page.Locator("[data-run-pagination]");

            // 25 a page by default, with a second page.
            await page.GotoAsync("/history");
            await Expect(rows).ToHaveCountAsync(25);
            await Expect(pagination).ToContainTextAsync("Showing 1–25 of 27 runs");
            await Expect(pagination.Locator(".page-link", new() { HasText = "2" })).ToBeVisibleAsync();
            await Expect(page.Locator("#pageSize")).ToHaveValueAsync("25");

            // 50 a page shows them all; the size stays when a filter changes.
            await page.Locator("#pageSize").SelectOptionAsync("50");
            await Expect(page).ToHaveURLAsync(new Regex(@"/history\?size=50$"));
            await Expect(rows).ToHaveCountAsync(27);
            await Expect(pagination).ToContainTextAsync("Showing 1–27 of 27 runs");
            await Expect(page.Locator("#pageSize")).ToHaveValueAsync("50");
            await page.Locator("#fSource").SelectOptionAsync("playground");
            await Expect(page).ToHaveURLAsync(new Regex(@"source=playground"));
            Check(page.Url.Contains("size=50", StringComparison.Ordinal), "the filter keeps the page size: " + page.Url);
            await Expect(rows).ToHaveCountAsync(27);
            await page.GotoAsync("/history?size=50");
            await Expect(rows).ToHaveCountAsync(27);
            await page.Locator("#pageSize").SelectOptionAsync("25");
            await Expect(page).ToHaveURLAsync(new Regex(@"/history$"));
            await Expect(rows).ToHaveCountAsync(25);

            // Pin the oldest run from its row menu (it is on the second page).
            var pinned = runs[0];
            await page.GotoAsync("/history?page=2");
            await Expect(rows).ToHaveCountAsync(2);
            var row = page.Locator($"tr#run-{pinned}");
            await row.Locator("[data-bs-toggle='dropdown']").ClickAsync();
            await row.Locator($"[data-run-pin='{pinned}']").ClickAsync();
            await page.ToastAsync("Run pinned");
            await Expect(row.Locator($"[data-run-pin='{pinned}']")).ToHaveAttributeAsync("data-pinned", "true");

            // Clear history, keeping pinned runs: only the pinned one stays.
            await page.GotoAsync("/history");
            var clear = await page.OpenModalAsync(page.Locator("[data-bs-target='#modal-clear-history']"), "modal-clear-history");
            await Expect(clear).ToContainTextAsync("All 27 runs");
            await Expect(clear).ToContainTextAsync("1 pinned run stays");
            await Expect(clear.Locator("[data-clear-keep-pinned]")).ToBeCheckedAsync();
            await clear.Locator("[data-clear-confirm]").ClickAsync();
            await page.ToastAsync("History cleared");
            await page.ToastAsync("26 runs deleted; pinned runs kept.");
            await Expect(rows).ToHaveCountAsync(1);
            await Expect(page.Locator($"tr#run-{pinned}")).ToBeVisibleAsync();
            Equal(200, (await app.Get("/api/runs/" + pinned)).Status, "the pinned run stays in runs.db");
            Equal(404, (await app.Get("/api/runs/" + runs[1])).Status, "the others are gone");

            // Cleared without keeping pinned runs: History is empty.
            clear = await page.OpenModalAsync(page.Locator("[data-bs-target='#modal-clear-history']"), "modal-clear-history");
            await Expect(clear).ToContainTextAsync("All 1 runs");
            await clear.Locator("[data-clear-keep-pinned]").UncheckAsync();
            await clear.Locator("[data-clear-confirm]").ClickAsync();
            await page.ToastAsync("1 run deleted.");
            await Expect(page.Locator("[data-run-history]")).ToHaveAttributeAsync("data-state", "empty");
            await Expect(page.Locator("[data-runs-empty]")).ToBeVisibleAsync();
            Equal(404, (await app.Get("/api/runs/" + pinned)).Status, "the pinned run went too");
        }),
    ];

    /// <summary>Opens <paramref name="path"/> (a filtered History view) and saves its filters as <paramref name="name"/> through the Saved
    /// filters menu.</summary>
    private static async Task W4SaveRunFilterAsync(IPage page, string path, string name)
    {
        await page.GotoAsync(path);
        await Expect(page.Locator("[data-run-history]")).ToHaveAttributeAsync("data-state", "filtered");
        await page.Locator(".dropdown:has([data-saved-filters]) > [data-bs-toggle='dropdown']").ClickAsync();
        var save = await page.OpenModalAsync(page.Locator("[data-saved-filters] [data-bs-target='#modal-save-run-filter']"), "modal-save-run-filter");
        await Expect(save.Locator("[type='submit']")).ToBeEnabledAsync();
        await save.Locator("[data-rsf-name]").FillAsync(name);
        await save.Locator("[type='submit']").ClickAsync();
        await page.ToastAsync("Filter saved");
        await Expect(page.Locator($"[data-saved-filters] [data-saved-filter='{name}']")).ToBeAttachedAsync();
    }
}
