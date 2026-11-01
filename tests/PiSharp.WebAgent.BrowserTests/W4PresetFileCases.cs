using System.Text.Json.Nodes;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W4, fix 2, tests-ci): Presets' JSON files in a browser (presets.js), on faux, failing on any page or console error.
// With two presets saved and an API key stored (a planted value), Export all downloads pisharp-presets-<date>.json holding both, and a
// card's Export JSON downloads <id>.preset.json holding only that one; neither file holds the key. The card's preset is then deleted
// (after its confirm) and imported again through the page's file picker (the Import JSON label opens the browser's chooser): it comes
// back under its own id, and importing the whole export afterwards finds nothing new (both are already here, unchanged).
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> W4PresetFileCases() =>
    [
        Browse("browser.w4.presets.export-and-import-through-the-file-picker", async (app, window) =>
        {
            var secret = "sk-proj-PLANTED" + Guid.NewGuid().ToString("N") + "Pr3s";
            var key = await app.PostAsync("/api/credentials/accounts", new JsonObject { ["provider"] = "openai", ["apiKey"] = secret, ["label"] = "Planted key" });
            Equal(200, key.Status, "a stored key: " + key.Text.Replace(secret, "[secret]", StringComparison.Ordinal));
            foreach (var (name, system) in new[] { ("Kind echo", "Be kind."), ("Short answers", "Answer in one line.") })
            {
                var saved = await app.PostAsync("/api/presets", new JsonObject
                {
                    ["page"] = "presets",
                    ["name"] = name,
                    ["capture"] = new JsonObject { ["provider"] = "faux", ["model"] = "echo", ["systemPrompt"] = system },
                });
                Equal(200, saved.Status, "a preset: " + saved.Text);
            }
            var presets = (await app.Get("/api/presets")).Body!["presets"]!.AsArray();
            var kind = presets.Single(preset => preset!["name"]!.GetValue<string>() == "Kind echo")!["id"]!.GetValue<string>();
            var page = window.Page;
            await page.GotoAsync("/presets");
            var cards = page.Locator("[data-preset-card]");
            await Expect(cards).ToHaveCountAsync(2);

            // Export all: one file with both presets, and no key.
            var all = await page.RunAndWaitForDownloadAsync(() => page.Locator("[data-preset-export-all]").ClickAsync());
            Check(all.SuggestedFilename.StartsWith("pisharp-presets-", StringComparison.Ordinal) && all.SuggestedFilename.EndsWith(".json", StringComparison.Ordinal),
                "the export's file name: " + all.SuggestedFilename);
            await page.ToastAsync("All presets exported");
            var allFile = Path.Join(app.Root, all.SuggestedFilename);
            await all.SaveAsAsync(allFile);
            var allText = await File.ReadAllTextAsync(allFile);
            Check(allText.Contains("Kind echo", StringComparison.Ordinal) && allText.Contains("Short answers", StringComparison.Ordinal), "both presets: " + allText[..Math.Min(400, allText.Length)]);
            Check(!allText.Contains(secret, StringComparison.Ordinal) && !allText.Contains(secret[^8..], StringComparison.Ordinal), "no key in the export");
            _ = JsonNode.Parse(allText);

            // A card's Export JSON: that preset alone.
            var card = page.Locator($"[data-preset-card='{kind}']");
            await card.Locator("[data-bs-toggle='dropdown']").ClickAsync();
            var one = await page.RunAndWaitForDownloadAsync(() => card.Locator("[data-preset-export]").ClickAsync());
            Equal(kind + ".preset.json", one.SuggestedFilename, "the preset's file name");
            var oneFile = Path.Join(app.Root, one.SuggestedFilename);
            await one.SaveAsAsync(oneFile);
            var oneText = await File.ReadAllTextAsync(oneFile);
            Check(oneText.Contains("Kind echo", StringComparison.Ordinal) && oneText.Contains("Be kind.", StringComparison.Ordinal), "the preset: " + oneText);
            Check(!oneText.Contains("Short answers", StringComparison.Ordinal), "only that preset");
            Check(!oneText.Contains(secret, StringComparison.Ordinal), "no key in the preset's file");

            // Delete it (after its confirm).
            await card.Locator("[data-bs-toggle='dropdown']").ClickAsync();
            await card.Locator("[data-preset-delete]").ClickAsync();
            await page.ModalShownAsync("modal-confirm");
            await Expect(page.Locator("#modal-confirm")).ToContainTextAsync("Delete preset?");
            await page.Locator("#modal-confirm [data-confirm-ok]").ClickAsync();
            await page.ToastAsync("Preset deleted");
            await Expect(cards).ToHaveCountAsync(1);
            await Expect(card).ToHaveCountAsync(0);

            // Import JSON, through the page's file picker: it comes back under its own id.
            var chooser = await page.RunAndWaitForFileChooserAsync(() => page.Locator("label[for='importPreset']").ClickAsync());
            Check(!chooser.IsMultiple, "one file at a time");
            await chooser.SetFilesAsync(oneFile);
            await page.ToastAsync("Presets imported");
            await page.ToastAsync("1 preset imported from " + one.SuggestedFilename);
            await Expect(cards).ToHaveCountAsync(2);
            await Expect(page.Locator($"[data-preset-card='{kind}']")).ToContainTextAsync("Kind echo");
            var back = (await app.Get("/api/presets")).Body!["presets"]!.AsArray();
            Equal(2, back.Count, "two presets again");
            Check(back.Any(preset => preset!["id"]!.GetValue<string>() == kind), "under its own id");

            // The whole export again: nothing new.
            chooser = await page.RunAndWaitForFileChooserAsync(() => page.Locator("label[for='importPreset']").ClickAsync());
            await chooser.SetFilesAsync(allFile);
            await page.ToastAsync("Nothing new to import");
            await page.ToastAsync("2 presets were already here unchanged.");
            await Expect(cards).ToHaveCountAsync(2);
            Equal(2, (await app.Get("/api/presets")).Body!["presets"]!.AsArray().Count, "still two presets");
            Check(!(await page.ContentAsync()).Contains(secret, StringComparison.Ordinal), "no key in the page");
        }),
    ];
}
