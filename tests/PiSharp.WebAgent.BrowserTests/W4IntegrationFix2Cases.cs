using System.Text.Json.Nodes;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W4, integration fixes 2): the browser cases of three fixes. Settings rows at a mid desktop width: in a 1000 px window
// (the app sidebar and the settings nav leave the content about 440 px) every setting row of Data & storage, Tools, Defaults, Appearance,
// Accounts and Spend either stacks its control under its label or keeps a label column of at least 240 px (it was 52 px), and at 1280 px
// the rows still sit side by side. The shared dialog footer: Data & storage's import review and confirmation keep their buttons 16 px or
// more from the dialog's sides in both themes at 375 px and at 1000 px (they sat 5 px from the right edge; FitsPhoneAsync now checks every
// phone dialog the same way). An agent compare column's Transcript: while the model waits for approval, opening it shows the model's
// transcript, read-only, from the chat stream; answering in the column streams the rest in, and the column's server renders keep it open.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> W4IntegrationFix2Cases() =>
    [
        Browse("browser.w4.fix2.settings-rows-fit-a-mid-desktop-window", AgentMode.Faux, async (app, window) =>
        {
            var page = window.Page;
            var pages = new[] { "/settings/data", "/settings/tools", "/settings/defaults", "/settings/appearance", "/settings/accounts", "/settings/spend" };
            foreach (var width in new[] { 1000, 1100 })
            {
                await page.SetViewportSizeAsync(width, 800);
                foreach (var path in pages)
                {
                    await page.GotoAsync(path);
                    await Expect(page.Locator(".settings-content")).ToBeVisibleAsync();
                    Check(!await page.OverflowsAsync(), $"{path} at {width} px does not scroll sideways: " + await page.WideElementsAsync());
                    var squeezed = await SqueezedRowAsync(page);
                    Check(squeezed is null, $"{path} at {width} px: a setting row squeezes its label: {squeezed}");
                }
            }
            await page.SetViewportSizeAsync(1000, 800);
            await page.GotoAsync("/settings/data");
            Check(await page.Locator(".setting-row").CountAsync() >= 8, "Data & storage has its rows");
            var stacked = await page.EvaluateAsync<bool>("""
                () => [...document.querySelectorAll('.setting-row')].filter(row => row.offsetParent && row.children.length > 1).every(row => {
                  const [label, control] = row.children;
                  return control.getBoundingClientRect().top >= label.getBoundingClientRect().bottom - 1;
                })
                """);
            Check(stacked, "at 1000 px Data & storage's rows stack");
            await page.SetViewportSizeAsync(1280, 800);
            await page.GotoAsync("/settings/data");
            var side = await page.EvaluateAsync<string>("""
                () => [...document.querySelectorAll('.setting-row')].filter(row => row.offsetParent && row.children.length > 1).map(row => {
                  const [label, control] = row.children;
                  const a = label.getBoundingClientRect(), b = control.getBoundingClientRect();
                  return Math.abs(a.top - b.top) < a.height && b.left > a.right ? 'side' : 'stacked';
                }).join(',')
                """);
            Check(side.Length > 0 && side.Split(',').All(item => item == "side"), "at 1280 px the rows sit side by side: " + side);
        }, width: 1000, height: 800),

        Browse("browser.w4.fix2.dialog-footers-keep-their-gutter", AgentMode.Faux, async (app, window) =>
        {
            var exported = await app.Get("/api/data/export");
            Equal(200, exported.Status, "a settings export to review");
            var export = Path.Join(app.Root, "settings-export.json");
            await File.WriteAllTextAsync(export, exported.Text);
            var page = window.Page;
            foreach (var (width, theme) in new[] { (375, "dark"), (375, "light"), (1000, "dark") })
            {
                await page.SetViewportSizeAsync(width, 812);
                await page.GotoAsync("/settings/data");
                if (theme == "light") await page.UseThemeAsync("light");
                // The confirmation as the danger zone opens it (its buttons are disabled while there is nothing to delete, so open it directly).
                await page.EvaluateAsync("() => window.bootstrap.Modal.getOrCreateInstance(document.getElementById('modal-data-confirm')).show()");
                await page.ModalShownAsync("modal-data-confirm");
                var confirm = await page.FooterEdgeGapAsync();
                Check(confirm is null, $"at {width} px ({theme}) the confirmation's footer keeps its gutter: {confirm}");
                var gap = await RightGapAsync(page, "modal-data-confirm", "[data-confirm-ok]");
                Check(gap is >= FooterEdgeGap and <= 28, $"Confirm sits {gap} px from the right edge at {width} px ({theme})");
                await page.DismissModalAsync("modal-data-confirm");

                await page.Locator("[data-import-file]").SetInputFilesAsync(export);
                await page.ModalShownAsync("modal-data-import");
                var import = await page.FooterEdgeGapAsync();
                Check(import is null, $"at {width} px ({theme}) the import review's footer keeps its gutter: {import}");
                gap = await RightGapAsync(page, "modal-data-import", "[data-import-apply]");
                Check(gap is >= FooterEdgeGap and <= 28, $"Import sits {gap} px from the right edge at {width} px ({theme})");
                await page.DismissModalAsync("modal-data-import");
                if (theme == "light") await page.UseThemeAsync("dark");
            }
        }, width: 375, height: 812),

        Browse("browser.w4.fix2.agent-column-shows-its-live-transcript", AgentMode.Faux, async (app, window) =>
        {
            var created = await app.PostAsync("/api/compare/agent", new JsonObject
            {
                ["requestId"] = "fix2-transcript",
                ["task"] = "!echo watched > watched.txt",
                ["projectPath"] = app.W4Project(),
                ["models"] = new JsonArray(new JsonObject { ["provider"] = "faux", ["model"] = "echo" }),
                ["approval"] = "ask",
                ["isolation"] = "copy",
            });
            Equal(200, created.Status, "an agent compare run: " + created.Text);
            var id = created.Body!["id"]!.GetValue<string>();
            await WaitUntilAsync(async () => (await app.Get("/api/compare/agent/" + id)).Body!["models"]![0]!["waiting"]?.GetValue<string>() == "approval",
                "the model asks", TimeSpan.FromSeconds(60));

            var page = window.Page;
            await page.GotoAsync("/compare?mode=agent&run=" + id);
            var column = page.Locator("[data-ac-col][data-slot='a']");
            await Expect(column.Locator("[data-ac-approval]")).ToBeVisibleAsync(new() { Timeout = 20_000 });
            var transcript = column.Locator("details[data-ac-transcript]");
            await Expect(transcript).Not.ToHaveAttributeAsync("open", "");
            await transcript.Locator("summary").ClickAsync();
            var items = column.Locator("[data-ac-transcript-items]");
            await Expect(items.Locator(".msg").First).ToBeVisibleAsync(new() { Timeout = 20_000 });
            await Expect(items).ToContainTextAsync("echo watched");
            Equal(0, await items.Locator("[data-action], [data-approval]").CountAsync(), "read-only: no actions and no approval card in the transcript");

            // Allow in the column: the transcript follows live, and the column's server renders keep it open and filled.
            await column.Locator("[data-ac-approve='allow']").ClickAsync();
            await Expect(items).ToContainTextAsync("Tool finished", new() { Timeout = 45_000 });
            await Expect(page.Locator("[data-agent-compare]")).ToHaveAttributeAsync("data-state", "finished", new() { Timeout = 60_000 });
            await Expect(column.Locator("details[data-ac-transcript]")).ToHaveAttributeAsync("open", "");
            await Expect(column.Locator("[data-ac-transcript-items]")).ToContainTextAsync("Tool finished");
            Check(File.Exists(Path.Join((await app.Get("/api/compare/agent/" + id)).Body!["models"]![0]!["workingDirectory"]!.GetValue<string>(), "watched.txt")),
                "the allowed command ran in the workspace");
        }, width: 1440, height: 900),
    ];

    /// <summary>The first visible setting row whose label column is under 240 px while its control sits beside it, described; null when
    /// every row stacks or keeps its label readable.</summary>
    private static Task<string?> SqueezedRowAsync(IPage page) => page.EvaluateAsync<string?>("""
        () => {
          for (const row of document.querySelectorAll('.setting-row')) {
            if (!row.offsetParent || row.children.length < 2) continue;
            const [label, control] = row.children;
            const a = label.getBoundingClientRect(), b = control.getBoundingClientRect();
            const beside = b.top < a.bottom - 1 && b.left >= a.right - 1;
            if (beside && a.width < 240) return `"${(label.textContent || '').trim().slice(0, 40)}" label ${Math.round(a.width)} px beside a ${Math.round(b.width)} px control`;
          }
          return null;
        }
        """);

    /// <summary>How far <paramref name="button"/> sits from the right edge of dialog <paramref name="id"/>'s content, in px.</summary>
    private static Task<int> RightGapAsync(IPage page, string id, string button) => page.EvaluateAsync<int>("""
        ([id, button]) => {
          const content = document.querySelector(`#${id} .modal-content`).getBoundingClientRect();
          return Math.round(content.right - document.querySelector(`#${id} ${button}`).getBoundingClientRect().right);
        }
        """, new[] { id, button });
}
