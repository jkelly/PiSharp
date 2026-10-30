using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W3, T3): W3's settings pages in a browser, on --faux. Settings › Custom providers adds a provider with a stored key
// typed into its editor: the key is posted once, in the body of the save, and from then on the page shows only its masked hint; it is not in
// the DOM, a field, browser storage, a URL, any page or API answer the app serves, the exported models.json, any file of the data folder in
// plain text, or the app's log. Settings › Appearance switches the theme for real: Light applies at once, survives a reload with the theme
// already set when the body starts (no flash), reaches every page, and follows the top bar's toggle; System follows the browser's colour
// scheme. Settings › About copies its diagnostics report to the clipboard exactly as shown, without the planted key of a stored account,
// and its connectivity check says, offline, that nothing was sent. The planted keys are made-up values; nothing calls a provider.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> W3SettingsCases() =>
    [
        Browse("browser.w3.custom-providers.a-stored-key-never-reaches-the-page", AgentMode.Faux, async (app, window) =>
        {
            var secret = "sk-cp-PLANTED" + Guid.NewGuid().ToString("N") + "Zq9Lw2";
            var page = window.Page;
            var requests = new ConcurrentBag<IRequest>();
            page.Request += (_, request) => requests.Add(request);

            await page.GotoAsync("/settings/custom-providers");
            await Expect(page.Locator("[data-cp-empty]")).ToBeVisibleAsync();
            await page.Locator("[data-cp-add]").First.ClickAsync();
            var modal = page.Locator("#modal-custom-provider");
            await Expect(modal).ToBeVisibleAsync();
            await modal.Locator("[data-cp-field='id']").FillAsync("lab-box");
            await modal.Locator("[data-cp-field='name']").FillAsync("Lab box");
            await modal.Locator("[data-cp-field='baseUrl']").FillAsync("https://llm.example.test/v1");
            await modal.Locator("label[for='cpKeyStored']").ClickAsync();
            var key = modal.Locator("[data-cp-key-input]");
            await Expect(key).ToBeVisibleAsync();
            await Expect(key).ToHaveAttributeAsync("type", "password");
            await key.FillAsync(secret);
            await modal.Locator("[data-cp-tab='models']").ClickAsync();
            await modal.Locator("[data-cp-model-add]").ClickAsync();
            await modal.Locator("[data-cp-model] [data-cp-model-field='id']").Last.FillAsync("lab-model");
            // The preview never carries the typed key.
            await modal.Locator("[data-cp-tab='preview']").ClickAsync();
            await Expect(modal.Locator("[data-cp-preview-json]")).ToContainTextAsync("lab-model");
            await Expect(modal.Locator("[data-cp-preview-json]")).Not.ToContainTextAsync(secret);
            await modal.Locator("[data-cp-save]").ClickAsync();

            var row = page.Locator("[data-cp-row='lab-box']");
            await Expect(row).ToBeVisibleAsync();
            await page.ToastAsync("models.json saved");
            var chip = row.Locator("[data-cp-key]");
            await Expect(chip).ToContainTextAsync(secret[^4..]);
            await Expect(chip).ToContainTextAsync("…");
            await NoSecretInPageAsync(page, secret, "Settings › Custom providers after saving");
            Check(!(await page.ContentAsync()).Contains(secret["sk-cp-".Length..^4], StringComparison.Ordinal), "no hidden part of the key is in the page");

            // Edit shows the masked hint only, with an empty key field.
            await row.Locator("[data-cp-edit]").ClickAsync();
            await Expect(modal).ToBeVisibleAsync();
            await Expect(modal.Locator("input#cpKeyStored")).ToBeCheckedAsync();
            await Expect(modal.Locator("[data-cp-key-hint-text]")).ToContainTextAsync(secret[^4..]);
            await Expect(key).ToHaveValueAsync("");
            await NoSecretInPageAsync(page, secret, "the editor of a saved provider");
            await modal.Locator(".modal-footer [data-bs-dismiss='modal']").First.ClickAsync();
            await Expect(modal).ToBeHiddenAsync();

            // The browser sent the key exactly once, in the body of the save, and never in a URL.
            var sent = requests.Where(request => (request.PostData ?? "").Contains(secret, StringComparison.Ordinal)).ToList();
            Equal(1, sent.Count, "requests that carried the key");
            Equal("POST /api/custom-providers", sent[0].Method + " " + new Uri(sent[0].Url).AbsolutePath, "the save");
            Check(!requests.Any(request => Uri.UnescapeDataString(request.Url).Contains(secret, StringComparison.Ordinal)), "no URL carries the key");

            // What the server serves and keeps: pages, answers, the export, the data folder and the log.
            foreach (var path in new[] { "/settings/custom-providers", "/api/custom-providers", "/api/custom-providers/lab-box", "/api/custom-providers/export",
                         "/chat/new", "/settings/models", "/settings/about", "/api/about/diagnostics" })
            {
                var served = await app.Get(path);
                Equal(200, served.Status, path);
                Check(!served.Text.Contains(secret, StringComparison.Ordinal) && !served.Text.Contains(secret["sk-cp-".Length..^4], StringComparison.Ordinal),
                    path + " leaves the key out");
            }
            var export = (await app.Get("/api/custom-providers/export")).Text;
            Check(export.Contains("\"lab-box\"", StringComparison.Ordinal) && export.Contains("lab-model", StringComparison.Ordinal), "the export lists the provider: " + export);
            Equal(0, FilesWith(app.Data, secret).Count, "no file of the data folder holds the key in plain text: " + string.Join(", ", FilesWith(app.Data, secret)));
            Check(File.Exists(Path.Join(app.Data, "models.json")), "the providers live in models.json");
            Check(!app.Log.Contains(secret, StringComparison.Ordinal), "the app's log leaves the key out");
        }),

        Browse("browser.w3.appearance.the-theme-switches-and-survives-a-reload", AgentMode.Faux, async (app, window) =>
        {
            var page = window.Page;
            // Records the theme at the moment <body> appears, before any paint: it must already be the stored one.
            await page.AddInitScriptAsync("""
                new MutationObserver((_, observer) => {
                  if (!document.body) return;
                  window.__themeAtBody = document.documentElement.getAttribute('data-bs-theme');
                  observer.disconnect();
                }).observe(document, { childList: true, subtree: true });
                """);
            await page.GotoAsync("/settings/appearance");
            var html = page.Locator("html");
            await Expect(html).ToHaveAttributeAsync("data-bs-theme", "dark");
            await Expect(page.Locator("input#theme-dark")).ToBeCheckedAsync();

            await page.Locator("label[for='theme-light']").ClickAsync();
            await Expect(html).ToHaveAttributeAsync("data-bs-theme", "light");
            await Expect(html).ToHaveAttributeAsync("data-theme-choice", "light");
            Equal("light", await page.EvaluateAsync<string?>("() => localStorage.getItem('pisharp-theme')"), "the choice is stored in this browser");

            await page.ReloadAsync();
            await Expect(html).ToHaveAttributeAsync("data-bs-theme", "light");
            Equal("light", await page.EvaluateAsync<string?>("() => window.__themeAtBody"), "light before the first paint");
            await Expect(page.Locator("input#theme-light")).ToBeCheckedAsync();
            await page.GotoAsync("/chat/new");
            await Expect(html).ToHaveAttributeAsync("data-bs-theme", "light");
            Equal("light", await page.EvaluateAsync<string?>("() => window.__themeAtBody"), "on every page");

            // The top bar's toggle switches back, and the page follows it.
            await page.Locator("[data-theme-toggle] >> visible=true").First.ClickAsync();
            await Expect(html).ToHaveAttributeAsync("data-bs-theme", "dark");
            await page.GotoAsync("/settings/appearance");
            await Expect(page.Locator("input#theme-dark")).ToBeCheckedAsync();
            Equal("dark", await page.EvaluateAsync<string?>("() => window.__themeAtBody"), "dark again, from the start");

            // System follows the browser's colour scheme: at once, when the scheme changes while the page is open, and at load.
            await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });
            await page.Locator("label[for='theme-system']").ClickAsync();
            await Expect(html).ToHaveAttributeAsync("data-theme-choice", "system");
            await Expect(html).ToHaveAttributeAsync("data-bs-theme", "light");
            await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Dark });
            await Expect(html).ToHaveAttributeAsync("data-bs-theme", "dark");
            await page.ReloadAsync();
            await Expect(html).ToHaveAttributeAsync("data-bs-theme", "dark");
            Equal("dark", await page.EvaluateAsync<string?>("() => window.__themeAtBody"), "the system's dark before the first paint");
            await Expect(page.Locator("input#theme-system")).ToBeCheckedAsync();
            Equal("system", await page.EvaluateAsync<string?>("() => localStorage.getItem('pisharp-theme')"), "System is stored");
        }),

        Browse("browser.w3.about.copies-the-diagnostics-report", AgentMode.Faux, async (app, window) =>
        {
            var secret = "sk-proj-PLANTED" + Guid.NewGuid().ToString("N") + "Ab3Cd4";
            var stored = await app.PostAsync("/api/credentials/accounts", new JsonObject { ["provider"] = "openai", ["apiKey"] = secret, ["label"] = "About key" });
            Equal(200, stored.Status, "a stored key the report must leave out: " + stored.Text);
            await window.Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"], new() { Origin = app.Base.GetLeftPart(UriPartial.Authority) });
            var page = window.Page;
            await page.GotoAsync("/settings/about");
            var report = page.Locator("[data-diagnostics-report]");
            await Expect(report).ToContainTextAsync("PiSharp");
            var shown = await report.TextContentAsync() ?? "";
            Check(!shown.Contains(secret, StringComparison.Ordinal) && !shown.Contains(secret[^4..], StringComparison.Ordinal), "the report leaves the key and its hint out");

            await page.GetByRole(AriaRole.Button, new() { Name = "Copy report", Exact = true }).ClickAsync();
            await page.ToastAsync("Copied");
            var copied = await page.EvaluateAsync<string>("() => navigator.clipboard.readText()");
            Equal(Lines(shown), Lines(copied), "the clipboard holds the report as shown");
            Check(!copied.Contains(secret, StringComparison.Ordinal), "the copy leaves the key out");

            // Offline, the connectivity check runs only on the click and says nothing was sent.
            var check = page.Locator("[data-connectivity]");
            await Expect(check).ToHaveAttributeAsync("data-state", "idle");
            await check.Locator("[data-connectivity-run]").ClickAsync();
            await Expect(check).ToHaveAttributeAsync("data-state", "results");
            await Expect(check.Locator("[data-pane='results']")).ToContainTextAsync("nothing was sent");
            await NoSecretInPageAsync(page, secret, "Settings › About");
        }),
    ];

    /// <summary>The text's lines, trimmed at the end, without blank ends (a clipboard may change line endings).</summary>
    private static string Lines(string text) => string.Join("\n", text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => line.TrimEnd())).Trim();
}
