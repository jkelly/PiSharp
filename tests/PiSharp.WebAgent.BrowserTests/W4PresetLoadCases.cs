using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W4, fix 2 presets): a preset loads in place on the Playground and on Compare, with its account. On a sealed live app
// (real routing; every outbound request goes to a closed loopback proxy, so nothing is sent) with two OpenAI keys, Personal (the active
// account) and Work: a Playground preset naming Work selects Work in Model & auth's account choice without a page load, the next estimate
// sends it, the choice comes back after a switch to another provider and back, and a preset saved from the page keeps it; a Compare preset
// with one column on Work and one on the active account loads in place (no page load, ?preset=), each column carries its account (the
// overrides' choice and the chip), the plan sends it, and a preset saved from the page keeps each column's account. On the faux app, a
// Compare preset loaded over a comparison that ran replaces it in place: the group leaves the page (it stays in History), the Metrics tab is
// empty, and Run all starts a new group. Every case fails on any page or console error.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> W4PresetLoadCases() =>
    [
        Browse("browser.w4.presets.a-preset-account-loads-in-place-on-the-playground-and-compare", AgentMode.Live, async (app, window) =>
        {
            var personal = await W4StoreKeyAsync(app, "Personal");
            var work = await W4StoreKeyAsync(app, "Work");
            var model = await W4OpenAIModelAsync(app);
            var playgroundPreset = await W4SavePresetAsync(app, "playground", new JsonObject
            {
                ["spec"] = new JsonObject
                {
                    ["provider"] = "openai", ["model"] = model, ["account"] = work, ["systemPrompt"] = "Answer for Work.",
                    ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = "Which account?" }),
                },
            }, "Work answers");
            var comparePreset = await W4SavePresetAsync(app, "compare", new JsonObject
            {
                ["draft"] = new JsonObject
                {
                    ["prompt"] = "Say hi.",
                    ["columns"] = new JsonArray(
                        new JsonObject { ["provider"] = "openai", ["model"] = model, ["account"] = work },
                        new JsonObject { ["provider"] = "openai", ["model"] = model }),
                },
            }, "Two accounts");
            var page = window.Page;

            // The Playground offers the choice (two accounts), on the active account.
            await page.GotoAsync($"/playground?provider=openai&model={Uri.EscapeDataString(model)}");
            var choice = page.Locator("[data-account-choice]");
            var select = page.Locator("#pg-account");
            await Expect(choice).ToBeVisibleAsync();
            await Expect(select).ToHaveValueAsync("");
            await Expect(select.Locator("option")).ToHaveCountAsync(3);
            await Expect(select.Locator("option").First).ToHaveTextAsync("Active account (Personal)");
            await Expect(page.Locator("[data-auth-source]")).ToHaveTextAsync("The provider's active account");
            await page.EvaluateAsync("() => { window.__presetStay = true; }");

            // Load the preset: in place, Work selected, and the next estimate sends it.
            var estimated = page.WaitForRequestAsync(request => request.Url.EndsWith("/api/playground/estimate", StringComparison.Ordinal)
                && (request.PostData ?? "").Contains($"\"account\":\"{work}\"", StringComparison.Ordinal));
            await W4LoadPresetAsync(page, playgroundPreset);
            await page.ToastAsync("Preset loaded");
            await Expect(select).ToHaveValueAsync(work);
            await Expect(page.Locator("[data-auth-source]")).ToHaveTextAsync("The account you chose");
            await Expect(page.Locator("[data-auth-account]")).ToHaveTextAsync("Work");
            await Expect(page.Locator("#toasts .toast").Last).Not.ToContainTextAsync("account");
            await estimated;
            Check(await page.EvaluateAsync<bool>("() => window.__presetStay === true"), "the Playground loaded the preset in place");
            Check(page.Url.Contains("preset=" + playgroundPreset, StringComparison.Ordinal), "the address names the preset: " + page.Url);

            // Another provider has none of these accounts; back on OpenAI, Work is chosen again.
            var providers = page.Locator("[data-request-config] [data-provider-model] [data-provider-select]");
            var another = await providers.EvaluateAsync<string>("select => [...select.options].map(option => option.value).find(value => value && value !== 'openai')");
            await providers.SelectOptionAsync(another);
            await Expect(choice).ToBeHiddenAsync();
            await Expect(select).ToHaveValueAsync("");
            await providers.SelectOptionAsync("openai");
            await Expect(choice).ToBeVisibleAsync();
            await Expect(select).ToHaveValueAsync(work);

            // A preset saved from the page keeps the account.
            var saveDialog = await page.OpenModalAsync(page.Locator("[data-preset-save] >> visible=true").First, "modal-save-preset");
            await saveDialog.Locator("#sp-name").FillAsync("Saved on Work");
            await saveDialog.Locator("[data-preset-save-submit]").ClickAsync();
            await Expect(saveDialog).ToBeHiddenAsync();
            await page.ToastAsync("Preset saved");
            var savedOnWork = await W4PresetNamedAsync(app, "Saved on Work");
            Equal(work, savedOnWork["account"]?.GetValue<string>(), "the saved preset names Work");

            // Compare: in place, each column on its own account, and the plan sends it.
            await page.GotoAsync("/compare");
            await Expect(page.Locator("[data-compare-grid] [data-compare-col]")).ToHaveCountAsync(2);
            await page.EvaluateAsync("() => { window.__presetStay = true; }");
            var planned = page.WaitForRequestAsync(request => request.Url.EndsWith("/api/compare/plan", StringComparison.Ordinal)
                && (request.PostData ?? "").Contains($"\"account\":\"{work}\"", StringComparison.Ordinal));
            await W4LoadPresetAsync(page, comparePreset);
            await page.ToastAsync("Preset loaded");
            await Expect(page).ToHaveURLAsync(new Regex(@"/compare\?preset=" + Regex.Escape(comparePreset) + "$"));
            Check(await page.EvaluateAsync<bool>("() => window.__presetStay === true"), "Compare loaded the preset in place");
            var columns = page.Locator("[data-compare-grid] [data-compare-col]");
            await Expect(columns).ToHaveCountAsync(2);
            await Expect(columns.Nth(0)).ToHaveAttributeAsync("data-account", work);
            await Expect(page.Locator("#cmp-c1-account")).ToHaveValueAsync(work);
            await Expect(columns.Nth(0).Locator("[data-col-account]")).ToBeVisibleAsync();
            await Expect(columns.Nth(0).Locator("[data-col-account]")).ToHaveTextAsync("Work");
            await Expect(page.Locator("#cmp-c2-account")).ToHaveValueAsync("");
            await Expect(columns.Nth(1).Locator("[data-col-account]")).ToBeHiddenAsync();
            await Expect(page.Locator("#cmpPrompt")).ToHaveValueAsync("Say hi.");
            await planned;

            // The second column moves to Personal through its overrides; a preset saved from the page keeps both accounts.
            await columns.Nth(1).Locator("[data-col-overrides] > button").ClickAsync();
            await page.Locator("#cmp-c2-account").SelectOptionAsync(personal);
            await Expect(columns.Nth(1).Locator("[data-col-account]")).ToHaveTextAsync("Personal");
            await page.Keyboard.PressAsync("Escape");
            var compareSave = await page.OpenModalAsync(page.Locator("[data-preset-save] >> visible=true").First, "modal-save-preset");
            await compareSave.Locator("#sp-name").FillAsync("Work and Personal");
            await compareSave.Locator("[data-preset-save-submit]").ClickAsync();
            await Expect(compareSave).ToBeHiddenAsync();
            await page.ToastAsync("Preset saved");
            var both = await W4PresetNamedAsync(app, "Work and Personal");
            Equal($"{work}|{personal}", string.Join("|", both["columns"]!.AsArray().Select(column => column?["account"]?.GetValue<string>())), "each column's account is saved");
        }),

        Browse("browser.w4.presets.compare-loads-a-preset-in-place-over-a-comparison-that-ran", async (app, window) =>
        {
            var (group, _) = await app.CompareGroupAsync("First question", "echo", "echo-small");
            var preset = await W4SavePresetAsync(app, "compare", new JsonObject
            {
                ["draft"] = new JsonObject
                {
                    ["systemPrompt"] = "Be brief.",
                    ["prompt"] = "Second question",
                    ["columns"] = new JsonArray(new JsonObject { ["provider"] = "faux", ["model"] = "echo-small" }, new JsonObject { ["provider"] = "faux", ["model"] = "echo" }),
                },
            }, "Second question");
            var page = window.Page;
            await page.GotoAsync("/compare?group=" + group);
            var host = page.Locator("[data-compare]");
            await Expect(host).ToHaveAttributeAsync("data-group", group);
            await Expect(page.Locator("[data-compare-grid] [data-compare-col][data-state='completed']")).ToHaveCountAsync(2);
            await page.EvaluateAsync("() => { window.__presetStay = true; }");

            await W4LoadPresetAsync(page, preset);
            await page.ToastAsync("Preset loaded");
            await Expect(page).ToHaveURLAsync(new Regex(@"/compare\?preset=" + Regex.Escape(preset) + "$"));
            Check(await page.EvaluateAsync<bool>("() => window.__presetStay === true"), "the preset loaded in place");
            Check(string.IsNullOrEmpty(await host.GetAttributeAsync("data-group")), "the comparison that ran left the page");
            var columns = page.Locator("[data-compare-grid] [data-compare-col]");
            await Expect(columns).ToHaveCountAsync(2);
            await Expect(page.Locator("[data-compare-grid] [data-compare-col][data-state='empty']")).ToHaveCountAsync(2);
            await Expect(columns.Nth(0)).ToHaveAttributeAsync("data-model", "echo-small");
            await Expect(page.Locator("#cmpPrompt")).ToHaveValueAsync("Second question");
            await Expect(page.Locator("#cmpSystemText")).ToHaveValueAsync("Be brief.");
            await Expect(page.Locator("[data-metrics-host]")).ToContainTextAsync("No metrics");
            await Expect(page.Locator("[data-compare-status]")).ToHaveTextAsync("Not run yet");
            await WaitUntilAsync(async () => (await app.Get($"/api/presets/{preset}")).Body?["preset"]?["uses"]?.GetValue<int>() == 1, "the load is counted once");
            Equal(200, (await app.Get($"/api/compare/groups/{group}")).Status, "the earlier comparison stays in History");

            // Run all starts a new group.
            await page.Locator("[data-run-all]").ClickAsync();
            var next = await page.QueryAsync("group");
            Check(next != group, "a new group: " + next);
            await Expect(page.Locator("[data-compare-grid] [data-compare-col][data-state='completed']")).ToHaveCountAsync(2, new() { Timeout = 30_000 });
            await Expect(columns.Nth(0)).ToHaveAttributeAsync("data-model", "echo-small");
        }),
    ];

    /// <summary>Stores an OpenAI API key account labelled <paramref name="label"/> (a planted key: the app is sealed, so it is never sent).</summary>
    private static async Task<string> W4StoreKeyAsync(Agent app, string label)
    {
        var stored = await app.PostAsync("/api/credentials/accounts", new JsonObject
        {
            ["provider"] = "openai", ["apiKey"] = "sk-proj-PLANTED" + Guid.NewGuid().ToString("N") + label, ["label"] = label,
        });
        Equal(200, stored.Status, "a stored key: " + stored.Text);
        return stored.Body!["account"]!["id"]!.GetValue<string>();
    }

    /// <summary>An OpenAI model the run engine knows.</summary>
    private static async Task<string> W4OpenAIModelAsync(Agent app)
    {
        foreach (var model in new[] { "gpt-4.1", "gpt-4o", "gpt-5", "gpt-4.1-mini" })
            if ((await app.Get($"/api/playground/model?provider=openai&model={model}")).Status == 200) return model;
        throw new InvalidOperationException("No OpenAI model is known.");
    }

    private static async Task<string> W4SavePresetAsync(Agent app, string page, JsonObject capture, string name)
    {
        var saved = await app.PostAsync("/api/presets", new JsonObject { ["page"] = page, ["capture"] = capture, ["name"] = name });
        Check(saved.Status == 200 && saved.Body?["created"]?.GetValue<bool>() == true, $"a {page} preset: {saved.Text}");
        return saved.Body!["preset"]!["id"]!.GetValue<string>();
    }

    private static async Task<JsonNode> W4PresetNamedAsync(Agent app, string name)
    {
        var presets = (await app.Get("/api/presets")).Body!["presets"]!.AsArray();
        var id = presets.FirstOrDefault(preset => preset?["name"]?.GetValue<string>() == name)?["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"No preset {name}: " + presets.ToJsonString());
        return (await app.Get("/api/presets/" + id)).Body!["preset"]!;
    }

    /// <summary>Loads preset <paramref name="id"/> through the page's Load preset dialog.</summary>
    private static async Task W4LoadPresetAsync(IPage page, string id)
    {
        var loader = await page.OpenModalAsync(page.Locator("[data-preset-load][data-preset-ready] >> visible=true").First, "modal-load-preset");
        await loader.Locator($"[data-lp-item='{id}']").ClickAsync();
        await loader.Locator("[data-lp-load]").ClickAsync();
        await Expect(loader).ToBeHiddenAsync();
    }
}
