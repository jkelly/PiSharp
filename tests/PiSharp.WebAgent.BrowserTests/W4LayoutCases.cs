using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W4, T4): every W4 page on a phone, on priced faux (--faux-metered) so the cost parts show real numbers: at 375 × 812,
// in the dark theme (the default) and then in the light theme, no page scrolls sideways and no open dialog or dropdown menu leaves the
// screen (FitsPhoneAsync). The app holds what the pages show when in use, made through the pages' own APIs: a Playground run, a three-model
// Compare group, a finished agent compare run on a plain folder (copies), a preset and a bench session paused on a tool call. The pages:
// the Playground (empty and with the run), Compare (empty and the group), Compare's agent mode (setup and the finished run), the Tool Bench
// (empty and paused), History (all runs and a filtered view), the inspector of a run and of the agent compare run, Presets, Settings › Data
// & storage and Settings › Tools (with its agent compare defaults). The second case opens the W4 dialogs on a phone in both themes: the
// model picker, View code, Save preset and Load preset (Playground), the tool editor and the Templates menu (Tool Bench), the workspace,
// diff and keep dialogs of an agent compare run, Edit preset and the card menu (Presets), Clear history, the Saved filters menu, Save
// current filters… and Manage saved filters (History), and the workspace clean-up confirmation and the import review of a settings export
// (Data & storage); each is dismissed, so nothing is changed. The error details dialog is left out: offline, no faux run ends in an error.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> W4LayoutCases() =>
    [
        Browse("browser.w4.layout.every-w4-page-fits-a-phone-in-both-themes", AgentMode.FauxMetered, async (app, window) =>
        {
            var seed = await W4SeedAsync(app);
            var pages = new (string Path, string Ready)[]
            {
                ("/playground", "[data-playground-toolbar]"),
                ("/playground?run=" + seed.Run, "[data-run-output][data-state='completed']"),
                ("/compare", "[data-compare-col]"),
                ("/compare?group=" + seed.Group, "[data-compare-metrics]"),
                ("/compare?mode=agent", "[data-agent-compare='setup']"),
                ("/compare?mode=agent&run=" + seed.Agent, "[data-agent-compare='run'][data-state='finished']"),
                ("/bench", "[data-bench]"),
                ("/bench?session=" + seed.Bench, "[data-bench][data-state='awaiting']"),
                ("/history", "[data-run-history]"),
                ("/history?source=compare", "[data-run-history]"),
                ("/runs/" + seed.Run, $"[data-run-inspector='{seed.Run}']"),
                ("/runs/" + seed.Agent, $"[data-run-inspector='{seed.Agent}']"),
                ("/presets", "[data-preset-card]"),
                ("/settings/data", "[data-ws-row]"),
                ("/settings/tools", "[data-agent-compare-defaults]"),
            };
            var page = window.Page;
            foreach (var theme in new[] { "dark", "light" })
            {
                await page.GotoAsync("/playground");
                if (theme == "light") await page.UseThemeAsync("light");
                foreach (var (path, ready) in pages)
                {
                    await page.GotoAsync(path);
                    await Expect(page.Locator(ready).First).ToBeAttachedAsync(new() { Timeout = 20_000 });
                    await page.FitsPhoneAsync(path, theme);
                }
            }
        }, width: 375, height: 812),

        Browse("browser.w4.layout.w4-dialogs-fit-a-phone-in-both-themes", AgentMode.FauxMetered, async (app, window) =>
        {
            var seed = await W4SeedAsync(app);
            var exported = await app.Get("/api/data/export");
            Equal(200, exported.Status, "a settings export to review: " + exported.Text[..Math.Min(200, exported.Text.Length)]);
            var export = Path.Join(app.Root, "settings-export.json");
            await File.WriteAllTextAsync(export, exported.Text);
            var page = window.Page;
            foreach (var theme in new[] { "dark", "light" })
            {
                await page.GotoAsync("/playground");
                if (theme == "light") await page.UseThemeAsync("light");

                // The Playground's dialogs.
                await page.GotoAsync("/playground?run=" + seed.Run);
                await Expect(page.Locator("[data-run-output]")).ToHaveAttributeAsync("data-state", "completed");
                foreach (var (opener, id) in new[]
                {
                    ("[data-bs-target='#modal-model-picker']", "modal-model-picker"),
                    ("[data-view-code]", "modal-view-code"),
                    ("[data-preset-save]", "modal-save-preset"),
                    ("[data-preset-load]", "modal-load-preset"),
                })
                {
                    await page.OpenModalAsync(page.Locator(opener + " >> visible=true").First, id);
                    await page.FitsPhoneAsync("Playground › " + id, theme);
                    await page.DismissModalAsync(id);
                }

                // The Tool Bench's tool editor.
                await page.GotoAsync("/bench");
                await page.Locator("[data-bench-part='tools'] [data-bs-toggle='dropdown'] >> visible=true").First.ClickAsync();
                await page.FitsPhoneAsync("Tool Bench › Templates", theme);
                await page.OpenModalAsync(page.Locator("[data-bench-template*='get_weather'] >> visible=true").First, "modal-tool-editor");
                await page.FitsPhoneAsync("Tool Bench › modal-tool-editor", theme);
                await page.DismissModalAsync("modal-tool-editor");

                // An agent compare run's dialogs.
                await page.GotoAsync("/compare?mode=agent&run=" + seed.Agent);
                await Expect(page.Locator("[data-agent-compare]")).ToHaveAttributeAsync("data-state", "finished");
                foreach (var (opener, id) in new[]
                {
                    ("[data-ac-iso]", "modal-agent-workspace"),
                    ("[data-ac-col][data-slot='a'] [data-ac-diff]", "modal-agent-diff"),
                    ("[data-ac-col][data-slot='a'] [data-ac-keep]", "modal-keep-result"),
                })
                {
                    await page.OpenModalAsync(page.Locator(opener + " >> visible=true").First, id);
                    await page.FitsPhoneAsync("Agent compare › " + id, theme);
                    await page.DismissModalAsync(id);
                }

                // Presets: Edit opens the save dialog's edit pane.
                await page.GotoAsync("/presets");
                var card = page.Locator($"[data-preset-card='{seed.Preset}']");
                await card.Locator("[data-bs-toggle='dropdown']").ClickAsync();
                await page.FitsPhoneAsync("Presets › the card menu", theme);
                await page.OpenModalAsync(card.Locator("[data-preset-edit]"), "modal-save-preset");
                await page.FitsPhoneAsync("Presets › Edit preset", theme);
                await page.DismissModalAsync("modal-save-preset");

                // History: Clear history.
                await page.GotoAsync("/history");
                var clear = page.Locator("[data-bs-target='#modal-clear-history']");
                if (!await clear.First.IsVisibleAsync())
                    await page.Locator(".dropdown:has([data-bs-target='#modal-clear-history']) > [data-bs-toggle='dropdown']").First.ClickAsync();
                await page.OpenModalAsync(clear.First, "modal-clear-history");
                await page.FitsPhoneAsync("History › Clear history", theme);
                await page.DismissModalAsync("modal-clear-history");

                // History's Saved filters on a filtered view: the menu, Save current filters… and Manage saved filters.
                await page.GotoAsync("/history?source=compare");
                var savedToggle = page.Locator(".dropdown:has([data-saved-filters]) > [data-bs-toggle='dropdown']");
                foreach (var id in new[] { "modal-save-run-filter", "modal-manage-run-filters" })
                {
                    await savedToggle.ClickAsync();
                    await Expect(page.Locator("[data-saved-filters]")).ToBeVisibleAsync();
                    await page.FitsPhoneAsync("History › Saved filters", theme);
                    await page.OpenModalAsync(page.Locator($"[data-saved-filters] [data-bs-target='#{id}']"), id);
                    await page.FitsPhoneAsync("History › " + id, theme);
                    await page.DismissModalAsync(id);
                }

                // Data & storage: the clean-up confirmation, and the import review of a settings export.
                await page.GotoAsync("/settings/data#compare-workspaces");
                await page.OpenModalAsync(page.Locator("[data-ws-cleanup]"), "modal-data-confirm");
                await page.FitsPhoneAsync("Data & storage › Clean up", theme);
                await page.DismissModalAsync("modal-data-confirm");
                await page.Locator("[data-import-file]").SetInputFilesAsync(export);
                await page.ModalShownAsync("modal-data-import");
                await page.FitsPhoneAsync("Data & storage › Import", theme);
                await page.DismissModalAsync("modal-data-import");
            }
            Equal(2, (await app.Get("/api/compare/agent/" + seed.Agent)).Body!["models"]!.AsArray().Count(model => model!["discarded"]?.GetValue<bool>() != true),
                "the dismissed dialogs kept and discarded nothing");
            Check((await app.Get("/api/runs/" + seed.Run)).Status == 200, "nothing was cleared");
        }, width: 375, height: 812),
    ];

    /// <summary>What the W4 phone cases show: a Playground run, a three-model Compare group, a finished agent compare run (copies), a preset
    /// and a bench session paused on a tool call.</summary>
    private static async Task<(string Run, string Group, string Agent, string Preset, string Bench)> W4SeedAsync(Agent app)
    {
        var run = await app.PlaygroundRunAsync("A phone-sized question about lighthouses and their keepers.", system: "Answer in one line.");
        var (group, _) = await app.CompareGroupAsync("Name three rivers.", "echo", "echo-small", "echo-thinking");
        var agent = (await app.AgentCompareAsync("phone-ac-1", "!echo phone > phone.txt", app.W4Project(), "copy", "echo", "echo-small"))["id"]!.GetValue<string>();
        var preset = await app.PostAsync("/api/presets", new System.Text.Json.Nodes.JsonObject
        {
            ["page"] = "presets",
            ["name"] = "Kind echo",
            ["capture"] = new System.Text.Json.Nodes.JsonObject { ["provider"] = "faux", ["model"] = "echo", ["systemPrompt"] = "Be kind." },
        });
        Equal(200, preset.Status, "a preset: " + preset.Text);
        var presetId = (await app.Get("/api/presets")).Body!["presets"]![0]!["id"]!.GetValue<string>();
        var bench = await W4BenchAwaitingAsync(app);
        return (run, group, agent, presetId, bench);
    }

    /// <summary>A bench session (<c>/api/bench</c>) with one tool and no mock, paused on the model's call to it; returns its id.</summary>
    private static async Task<string> W4BenchAwaitingAsync(Agent app)
    {
        var tool = new System.Text.Json.Nodes.JsonObject
        {
            ["name"] = "get_weather",
            ["description"] = "The weather in a city.",
            ["parameters"] = System.Text.Json.Nodes.JsonNode.Parse("""{"type":"object","properties":{"location":{"type":"string"}},"required":["location"]}"""),
        };
        var created = await app.PostAsync("/api/bench/sessions", new System.Text.Json.Nodes.JsonObject
        {
            ["setup"] = new System.Text.Json.Nodes.JsonObject { ["provider"] = "faux", ["model"] = "echo", ["tools"] = new System.Text.Json.Nodes.JsonArray(tool) },
        });
        Equal(200, created.Status, "a bench session: " + created.Text);
        var id = created.Body!["id"]!.GetValue<string>();
        var sent = await app.PostAsync($"/api/bench/sessions/{id}/messages", new System.Text.Json.Nodes.JsonObject
        {
            ["text"] = "tool: get_weather {\"location\":\"Oslo, Norway\"}",
            ["run"] = true,
        });
        Check(sent.Status == 200 && sent.Body?["posted"]?.GetValue<bool>() != false, "the bench message ran: " + sent.Text);
        await WaitUntilAsync(async () => (await app.Get($"/api/bench/sessions/{id}/view")).Body?["state"]?.GetValue<string>() == "awaiting",
            "the bench session waits for a tool result", TimeSpan.FromSeconds(30));
        return id;
    }
}
