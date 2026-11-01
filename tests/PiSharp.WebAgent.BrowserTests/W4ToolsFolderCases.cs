using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W4, fix 2, tests-ci): Settings › Tools & workspace's folder browser (_FolderBrowser, settings-forms.js on GET
// /api/settings/folders) in a browser, on faux, failing on any page or console error; the W4 retest's server rule (no network or device
// path) is covered by the unit tests. Browse opens at the working directory; a row opens a subfolder (a dot folder is not listed, a git
// repository and a folder with AGENTS.md are marked), ".." goes up, a crumb goes back to its folder and "This PC" lists the drives (where
// there is no folder to use); Cancel closes it and Browse opens it at the field's folder again. Use this folder fills the field, which marks
// the setting modified, and Save keeps it: the page then shows the folder's context files.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> W4ToolsFolderCases() =>
    [
        Browse("browser.w4.settings.tools-folder-browser-navigates-and-uses-a-folder", async (app, window) =>
        {
            var tree = Path.Join(app.Work, "browse");
            Directory.CreateDirectory(Path.Join(tree, "alpha", ".git"));
            Directory.CreateDirectory(Path.Join(tree, "beta"));
            await File.WriteAllTextAsync(Path.Join(tree, "beta", "AGENTS.md"), "# Beta\n");
            Directory.CreateDirectory(Path.Join(tree, "gamma", "inner"));
            Directory.CreateDirectory(Path.Join(tree, ".hidden"));

            var page = window.Page;
            await page.GotoAsync("/settings/tools");
            var form = page.Locator("form[data-settings-form='tools']");
            var input = form.Locator("[data-working-directory]");
            var work = await input.InputValueAsync();
            Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(app.Work)), work, "the case's working directory");
            var browser = page.Locator("[data-folder-browser]");
            var current = browser.Locator("[data-folder-current]");
            var crumbs = browser.Locator("[data-folder-crumbs]");
            var rows = browser.Locator("[data-folder-list] > button");
            var use = browser.Locator("[data-folder-use]");
            ILocator Row(string name) => rows.Filter(new() { Has = page.Locator("span.text-break", new() { HasTextRegex = new Regex("^" + Regex.Escape(name) + "$") }) });
            var open = form.Locator("[data-bs-target='#folderBrowser'][aria-controls='folderBrowser']");

            // Browse opens at the working directory.
            await open.ClickAsync();
            await Expect(browser).ToBeVisibleAsync();
            await Expect(current).ToHaveTextAsync(work);
            await Expect(crumbs.Locator("span").Last).ToHaveTextAsync(Path.GetFileName(work));
            await Expect(use).ToBeEnabledAsync();

            // A subfolder: its folders by name, marked, without the dot folder.
            await Row("browse").ClickAsync();
            await Expect(current).ToHaveTextAsync(Path.Join(work, "browse"));
            await Expect(rows).ToHaveCountAsync(4);
            await Expect(Row("..")).ToHaveCountAsync(1);
            await Expect(Row(".hidden")).ToHaveCountAsync(0);
            await Expect(Row("alpha").Locator(".ps-chip")).ToHaveTextAsync("git");
            await Expect(Row("beta").Locator(".ps-chip")).ToHaveTextAsync("AGENTS.md");
            await Expect(Row("gamma").Locator(".ps-chip")).ToHaveCountAsync(0);

            // Down two levels, an empty folder, and up again.
            await Row("gamma").ClickAsync();
            await Expect(current).ToHaveTextAsync(Path.Join(work, "browse", "gamma"));
            await Row("inner").ClickAsync();
            await Expect(current).ToHaveTextAsync(Path.Join(work, "browse", "gamma", "inner"));
            await Expect(browser.Locator("[data-folder-list]")).ToContainTextAsync("No folders here.");
            await Expect(rows).ToHaveCountAsync(1);
            await Row("..").ClickAsync();
            await Expect(current).ToHaveTextAsync(Path.Join(work, "browse", "gamma"));
            await Expect(crumbs.Locator("span").Last).ToHaveTextAsync("gamma");

            // A crumb goes back to its folder.
            await crumbs.Locator("button", new() { HasTextRegex = new Regex("^browse$") }).ClickAsync();
            await Expect(current).ToHaveTextAsync(Path.Join(work, "browse"));

            // This PC: the drives, and no folder to use.
            if (OperatingSystem.IsWindows())
            {
                await crumbs.Locator("button", new() { HasTextRegex = new Regex("^This PC$") }).ClickAsync();
                await Expect(current).ToHaveTextAsync("Choose a drive");
                await Expect(use).ToBeDisabledAsync();
                await Expect(Row("..")).ToHaveCountAsync(0);
                await Expect(Row(Path.GetPathRoot(work)!.TrimEnd('\\', '/'))).ToHaveCountAsync(1);
                await Expect(crumbs).ToHaveTextAsync("This PC");
            }

            // Cancel closes it; Browse opens it at the field's folder again.
            await browser.Locator("[data-bs-target='#folderBrowser']", new() { HasText = "Cancel" }).ClickAsync();
            await Expect(browser).ToBeHiddenAsync();
            await Expect(form.Locator("[data-savebar]")).ToBeHiddenAsync();
            await open.ClickAsync();
            await Expect(browser).ToBeVisibleAsync();
            await Expect(current).ToHaveTextAsync(work);

            // Use this folder fills the field (a change to save), and Save keeps it.
            await Row("browse").ClickAsync();
            await Row("beta").ClickAsync();
            var beta = Path.Join(work, "browse", "beta");
            await Expect(current).ToHaveTextAsync(beta);
            await use.ClickAsync();
            await Expect(browser).ToBeHiddenAsync();
            await Expect(input).ToHaveValueAsync(beta);
            await Expect(form.Locator("[data-setting-row='workingDirectory'] [data-modified-chip]")).ToBeVisibleAsync();
            await Expect(form.Locator("[data-savebar]")).ToBeVisibleAsync();
            await Expect(form.Locator("[data-savebar-names]")).ToHaveTextAsync("tools.workingDirectory");
            await form.Locator("[data-save]").ClickAsync();
            await page.ToastAsync("Tool settings saved");
            await Expect(input).ToHaveValueAsync(beta);
            await Expect(page.Locator("[data-context-files]")).ToContainTextAsync("AGENTS.md");
            await Expect(form.Locator("[data-savebar]")).ToBeHiddenAsync();
            Equal(beta, (await app.Get("/api/settings/tools")).Body!["workingDirectory"]!.GetValue<string>(), "the saved working directory");
        }),
    ];
}
