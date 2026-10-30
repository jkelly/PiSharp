using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W4, T4): presets in a browser (faux, failing on any page or console error). The Playground's request is saved as a
// preset through the shared save dialog (a name, tags and what to include), shows on /presets with its card, loads into Compare through
// the shared load dialog (a fresh comparison with the preset's system prompt and prompt, its use counted), and its card's Open in
// Playground loads it into the Playground.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> W4PresetCases() =>
    [
        Browse("browser.w4.presets.save-on-the-playground-and-load-into-compare", async (app, window) =>
        {
            var page = window.Page;
            await page.GotoAsync("/playground");
            await page.Locator("[data-msg][data-msg-prompt='true'] [data-msg-text]").FillAsync("Answer in one line.");
            await page.Locator("[data-msg][data-role='user'] [data-msg-text]").First.FillAsync("Describe a lighthouse.");
            var save = page.Locator("[data-preset-save]");
            await Expect(save).ToBeEnabledAsync();
            var dialog = await page.OpenModalAsync(save, "modal-save-preset");
            await dialog.Locator("#sp-name").FillAsync("One-line answers");
            await dialog.Locator("#sp-tags").FillAsync("short, test");
            await dialog.Locator("[data-preset-save-submit]").ClickAsync();
            await Expect(dialog).ToBeHiddenAsync();
            await page.ToastAsync("Preset saved");
            var presets = (await app.Get("/api/presets")).Body!["presets"]!.AsArray();
            Equal(1, presets.Count, "one preset");
            var id = presets[0]!["id"]!.GetValue<string>();
            Equal("One-line answers", presets[0]!["name"]!.GetValue<string>(), "its name");

            // /presets shows it.
            await page.GotoAsync("/presets");
            var card = page.Locator($"[data-preset-card='{id}']");
            await Expect(card).ToBeVisibleAsync();
            await Expect(card).ToContainTextAsync("One-line answers");
            await Expect(card).ToHaveAttributeAsync("data-preset-uses", "0");

            // Load it into Compare: a fresh comparison with the preset's prompts.
            await page.GotoAsync("/compare");
            var load = page.Locator("[data-preset-load]:not([disabled]) >> visible=true").First;
            var loader = await page.OpenModalAsync(load, "modal-load-preset");
            await loader.Locator($"[data-lp-item='{id}']").ClickAsync();
            await loader.Locator("[data-lp-load]").ClickAsync();
            await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex(@"/compare\?preset=" + System.Text.RegularExpressions.Regex.Escape(id)));
            await page.ToastAsync("Preset loaded");
            await Expect(page.Locator("#cmpSystemText")).ToHaveValueAsync("Answer in one line.");
            await Expect(page.Locator("#cmpPrompt")).ToHaveValueAsync("Describe a lighthouse.");
            await WaitUntilAsync(async () => (await app.Get("/api/presets")).Body!["presets"]![0]!["uses"]!.GetValue<int>() == 1, "the load is counted");

            // Open in Playground from its card.
            await page.GotoAsync("/presets");
            await card.Locator("[data-preset-open='playground']").ClickAsync();
            await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/playground"));
            await Expect(page.Locator("[data-msg][data-msg-prompt='true'] [data-msg-text]")).ToHaveValueAsync("Answer in one line.");
            await Expect(page.Locator("[data-msg][data-role='user'] [data-msg-text]").First).ToHaveValueAsync("Describe a lighthouse.");
        }),
    ];
}
