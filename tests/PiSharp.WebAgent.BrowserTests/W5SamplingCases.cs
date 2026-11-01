using System.Text.Json.Nodes;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W5, after the live test): Sampling's read-only samplingParams row (L4) with values, and a run that carries them on a
// real wire format. No built-in model has samplingParams and faux sends no request, so the case runs on the scripted provider
// (W5SupportCases): Settings › Providers' import gives its kestrel-7b samplingParams ({"temperature":0.3,"top_p":0.8}), which the Playground
// shows read-only, applied by openai-completions; osprey-9b has none. A run on kestrel-7b with temperature 0.7 and seed 42 then sends a Chat
// Completions body (as the scripted provider received it) that carries the model's top_p, the page's seed, and the page's temperature in
// place of the model's (an option set on the page replaces the same key). The run's own address shows the row again, as the server renders it.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> W5SamplingCases() =>
    [
        BrowseScripted("browser.w5.sampling.a-models-sampling-params-show-read-only-and-go-on-the-wire", async (app, provider, window) =>
        {
            var page = window.Page;
            var models = new JsonArray(
                new JsonObject { ["id"] = W5Models[0].Id, ["name"] = W5Models[0].Name, ["samplingParams"] = new JsonObject { ["temperature"] = 0.3, ["top_p"] = 0.8 } },
                new JsonObject { ["id"] = W5Models[1].Id, ["name"] = W5Models[1].Name });
            var file = new JsonObject
            {
                ["providers"] = new JsonObject
                {
                    [W5Provider] = new JsonObject { ["name"] = W5ProviderName, ["baseUrl"] = provider.BaseUrl, ["api"] = "openai-completions", ["models"] = models },
                },
            };
            var imported = await app.PostAsync("/api/custom-providers/import", new JsonObject { ["text"] = file.ToJsonString() });
            Equal(200, imported.Status, "the import gives kestrel-7b samplingParams: " + imported.Text);

            await page.GotoAsync("/playground");
            var picker = page.Locator("[data-request-config] [data-provider-model]");
            await picker.Locator("[data-provider-select]").SelectOptionAsync(W5Provider);
            var model = picker.Locator($"[data-pane][data-provider='{W5Provider}'] [data-model-select]");
            var row = page.Locator("[data-request-config] [data-sampling-params]");
            var values = row.Locator("[data-sampling-values]");
            await model.SelectOptionAsync(W5Models[0].Id);
            await Expect(values).ToBeVisibleAsync();
            await Expect(values).ToHaveTextAsync("""{"temperature":0.3,"top_p":0.8}""");
            await Expect(row).ToHaveAttributeAsync("data-applied", "true");
            await Expect(row.Locator("[data-sampling-chip]")).ToHaveTextAsync("applied by this api");
            await Expect(row.Locator("[data-sampling-note]")).ToContainTextAsync("openai-completions merges them into the request body");
            await Expect(row.Locator("[data-sampling-by-level]")).ToBeHiddenAsync();
            Equal(0, await row.Locator("input, select, textarea").CountAsync(), "the row has nothing to edit");

            // osprey-9b has none; back on kestrel-7b they show again.
            await model.SelectOptionAsync(W5Models[1].Id);
            await Expect(row.Locator("[data-sampling-note]")).ToHaveTextAsync(W5Models[1].Id + " has none.");
            await Expect(values).ToBeHiddenAsync();
            await model.SelectOptionAsync(W5Models[0].Id);
            await Expect(values).ToHaveTextAsync("""{"temperature":0.3,"top_p":0.8}""");

            // A run with the page's temperature and seed: the body carries the model's top_p, the seed, and the page's temperature.
            await page.Locator("#pg-temperature").FillAsync("0.7");
            await Expect(page.Locator("[data-request-config] [data-option='temperature'] [data-option-value]")).ToHaveTextAsync("0.7");
            await page.Locator("#pg-seed").FillAsync("42");
            await page.Locator("[data-msg][data-role='user'] [data-msg-text]").First.FillAsync("Name a bird of prey.");
            await page.Locator("[data-run]").ClickAsync();
            var output = page.Locator("[data-run-output]");
            await Expect(output).ToHaveAttributeAsync("data-state", "completed", new() { Timeout = 30_000 });
            await Expect(output.Locator("[data-output-plain]")).ToContainTextAsync("Answer: Name a bird of prey.");
            var id = await page.QueryAsync("run");
            var run = await app.Get("/api/runs/" + id);
            Equal("completed", run.Body?["run"]?["status"]?.GetValue<string>(), "the run completed: " + run.Text);

            var body = provider.Bodies.Last();
            Equal(W5Models[0].Id, body["model"]?.GetValue<string>(), "the body asks kestrel-7b: " + body.ToJsonString());
            Equal(0.8, body["top_p"]?.GetValue<double>(), "the model's top_p is carried: " + body.ToJsonString());
            Equal(0.7, body["temperature"]?.GetValue<double>(), "the page's temperature replaces the model's: " + body.ToJsonString());
            Equal(42, body["seed"]?.GetValue<int>(), "the page's seed is carried: " + body.ToJsonString());

            // The run's address renders the row on the server, with the same values.
            await page.GotoAsync("/playground?run=" + Uri.EscapeDataString(id));
            await Expect(output).ToHaveAttributeAsync("data-state", "completed");
            await Expect(values).ToBeVisibleAsync();
            await Expect(values).ToHaveTextAsync("""{"temperature":0.3,"top_p":0.8}""");
            await Expect(row).ToHaveAttributeAsync("data-applied", "true");
        }),
    ];
}
