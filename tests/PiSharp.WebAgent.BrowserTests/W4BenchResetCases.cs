using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W5, L4: W4 leftovers): the last W4 page scripts no browser case ran, and the Playground's W5 controls with presets, in
// a browser, failing on any page or console error.
// - The Tool Bench (priced faux): after a plain answer and a tool call that the rules answer with no mock, Export as JSON downloads the
//   call log (bench-calls-<time>.json: the session, its provider, model and group, every turn and the call); removing the second user
//   message drops it, every turn after it and the call (the Export link goes with the last call); Reset conversation keeps the user
//   messages and clears the turns, the calls and the group (idle). Every turn's ledger reservation ends.
// - The Playground (faux): a preset with every option the run engine carries (cache retention, retries, the retry delay, thinking budgets,
//   the Azure API version) and the options W4 wired, plus a strict tool, loads in place: each control shows its value, the Azure API version
//   (which faux does not take) is marked and its Provider-specific pane shown, and the next estimate sends them all. A preset saved from the
//   page keeps them and the strict flag. A budget typed in is sent; the budgets row clears as one; "Clear them" clears the Azure API
//   version; a run then carries the rest, and its ?run= address shows them again. Sampling's read-only samplingParams row follows the model.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> W4BenchResetCases() =>
    [
        Browse("browser.w5.bench.export-as-json-remove-a-message-and-reset", AgentMode.FauxMetered, async (app, window) =>
        {
            var page = window.Page;
            await page.GotoAsync("/bench");
            var bench = page.Locator("[data-bench]");
            await Expect(bench).ToBeVisibleAsync();
            var session = (await bench.GetAttributeAsync("data-session"))!;
            await W4BenchTemplateAsync(page);
            // The template has no mock: with "Pause when no mock matches" off, the bench answers the call itself.
            await page.Locator("[aria-label='Auto-respond rules']").ClickAsync();
            await page.Locator("#ruleNoMock").UncheckAsync();
            await WaitUntilAsync(async () => (await W4BenchSessionAsync(app, session))["setup"]!["rules"]!["pauseOnNoMock"]!.GetValue<bool>() == false,
                "the rule is saved");
            await page.Keyboard.PressAsync("Escape");
            var export = page.Locator("[data-bench-part='log'] [data-bench-export]");
            await Expect(export).ToHaveCountAsync(0);

            // A plain answer, then a call the bench answers and the model's answer to it: three turns, one call.
            await W4BenchSendAsync(page, "First question.");
            await W4BenchCallsAsync(app, session, 0, "finished");
            await W4BenchSendAsync(page, "tool: get_weather {\"location\":\"Oslo, Norway\"}");
            var calls = await W4BenchCallsAsync(app, session, 1, "finished");
            Equal("no-mock", calls[0]!["result"]?["source"]?.GetValue<string>(), "the bench answered the call: " + calls[0]!.ToJsonString());
            var held = await W4BenchSessionAsync(app, session);
            var entries = held["entries"]!.AsArray();
            Equal("user|assistant|user|assistant|assistant", string.Join("|", entries.Select(entry => entry?["kind"]?.GetValue<string>())), "the conversation: " + entries.ToJsonString());
            var group = held["group"]?.GetValue<string>();
            Check(!string.IsNullOrEmpty(group), "the turns run under a group: " + held.ToJsonString());
            await Expect(page.Locator("[data-bench-remove]")).ToHaveCountAsync(2);

            // Export as JSON: the call log as a file.
            await Expect(export).ToBeVisibleAsync();
            await Expect(export).ToHaveAttributeAsync("href", $"/api/bench/sessions/{Uri.EscapeDataString(session)}/log.json");
            var download = await page.RunAndWaitForDownloadAsync(() => export.ClickAsync());
            Check(Regex.IsMatch(download.SuggestedFilename, @"^bench-calls-\d{4}-\d{2}-\d{2}T\d{2}-\d{2}\.json$"), "the file name: " + download.SuggestedFilename);
            var saved = Path.Join(app.Root, "bench-calls.json");
            await download.SaveAsAsync(saved);
            var log = JsonNode.Parse(await File.ReadAllTextAsync(saved))!;
            Equal(session, log["session"]?.GetValue<string>(), "the file is this session's");
            Equal(held["setup"]!["provider"]?.GetValue<string>(), log["provider"]?.GetValue<string>(), "its provider");
            Equal(held["setup"]!["model"]?.GetValue<string>(), log["model"]?.GetValue<string>(), "its model");
            Equal(group, log["group"]?.GetValue<string>(), "its group");
            Check(DateTimeOffset.TryParse(log["exported"]?.GetValue<string>(), out _), "when it was exported: " + log["exported"]);
            var turns = log["turns"]!.AsArray();
            Equal(3, turns.Count, "every turn: " + turns.ToJsonString());
            Check(turns.All(turn => turn?["runId"]?.GetValue<string>() is { Length: > 0 } && turn["status"]?.GetValue<string>() == "completed"),
                "each turn's run, completed: " + turns.ToJsonString());
            var logged = log["calls"]!.AsArray().Single()!;
            Equal("get_weather", logged["tool"]?.GetValue<string>(), "the call's tool");
            Equal("Oslo, Norway", logged["arguments"]?["location"]?.GetValue<string>(), "its arguments");
            Equal("no-mock", logged["result"]?["source"]?.GetValue<string>(), "its result");

            // Remove the second user message: it, the turns after it and their call go; the first exchange stays.
            await page.Locator("[data-bench-remove='2']").ClickAsync();
            await Expect(page.Locator("[data-bench-remove]")).ToHaveCountAsync(1);
            await Expect(export).ToHaveCountAsync(0);
            await Expect(page.Locator("[data-bench-part='log']")).ToContainTextAsync("No tool calls yet");
            await Expect(page.Locator("[data-bench-part='timeline']")).Not.ToContainTextAsync("get_weather");
            var removed = await W4BenchSessionAsync(app, session);
            Equal("user|assistant", string.Join("|", removed["entries"]!.AsArray().Select(entry => entry?["kind"]?.GetValue<string>())), "the first exchange stays: " + removed.ToJsonString());
            Equal(0, removed["calls"]!.AsArray().Count, "the call went with its turn");
            Equal(group, removed["group"]?.GetValue<string>(), "the group stays while a turn does");

            // Reset conversation: the user message stays; the turn, the calls and the group go, and the bench is idle.
            var reset = page.Locator("[data-bench-reset]");
            await Expect(reset).ToBeEnabledAsync();
            await reset.ClickAsync();
            await Expect(bench).ToHaveAttributeAsync("data-bench-state", "idle");
            await Expect(page.Locator("[data-bench-remove]")).ToHaveCountAsync(1);
            await Expect(page.Locator("[data-bench-part='timeline'] .tl-user")).ToContainTextAsync("First question.");
            var cleared = await W4BenchSessionAsync(app, session);
            Equal("user", string.Join("|", cleared["entries"]!.AsArray().Select(entry => entry?["kind"]?.GetValue<string>())), "only the user message: " + cleared.ToJsonString());
            Equal("First question.", cleared["entries"]![0]!["text"]?.GetValue<string>(), "its text");
            Equal(0, cleared["calls"]!.AsArray().Count, "no calls");
            Check(cleared["group"] is null, "no group: " + cleared.ToJsonString());
            Equal("idle", cleared["state"]?.GetValue<string>(), "idle");

            // Three model turns, each a bench run, each reservation ended.
            var rows = await app.SettledLedgerAsync(3);
            Check(rows.All(row => Reservation(row, "source") == "bench"), "bench rows: " + string.Join(" | ", rows.Select(row => row.ToJsonString())));
        }),

        Browse("browser.w5.playground.a-preset-keeps-every-option-and-the-controls-send-them", AgentMode.Faux, async (app, window) =>
        {
            var preset = await W4SavePresetAsync(app, "playground", new JsonObject
            {
                ["spec"] = new JsonObject
                {
                    ["provider"] = "faux", ["model"] = "echo-thinking", ["thinking"] = "low",
                    ["textVerbosity"] = "low", ["reasoningSummary"] = "concise", ["timeoutMs"] = 120000, ["parallelToolCalls"] = false,
                    ["cacheRetention"] = "long", ["maxRetries"] = 2, ["maxRetryDelayMs"] = 5000,
                    ["thinkingBudgets"] = new JsonObject { ["low"] = 2048, ["high"] = 8192 }, ["azureApiVersion"] = "2025-04-01-preview",
                    ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = "Which options?" }),
                },
                ["tools"] = new JsonArray(new JsonObject
                {
                    ["name"] = "get_weather", ["description"] = "The weather in a city.", ["enabled"] = true, ["strict"] = true,
                    ["parameters"] = JsonNode.Parse("""{"type":"object","properties":{"location":{"type":"string"}},"required":["location"],"additionalProperties":false}"""),
                }),
            }, "Every option");
            var page = window.Page;
            await page.GotoAsync("/playground?provider=faux&model=echo");
            var config = page.Locator("[data-request-config]").First;
            var sampling = config.Locator("[data-sampling-params]");
            await Expect(sampling.Locator("[data-sampling-chip]")).ToHaveTextAsync("not applied by this api");
            await Expect(sampling.Locator("[data-sampling-note]")).ToHaveTextAsync("echo has none.");
            await page.EvaluateAsync("() => { window.__presetStay = true; }");

            // Loaded in place: every control shows its value, and the next estimate sends them all.
            var sent = new[]
            {
                "\"thinking\":\"low\"", "\"textVerbosity\":\"low\"", "\"reasoningSummary\":\"concise\"", "\"timeoutMs\":120000", "\"parallelToolCalls\":false",
                "\"cacheRetention\":\"long\"", "\"maxRetries\":2", "\"maxRetryDelayMs\":5000", "\"low\":2048", "\"high\":8192",
                "\"azureApiVersion\":\"2025-04-01-preview\"", "\"strict\":true",
            };
            var estimated = page.WaitForRequestAsync(request => request.Url.EndsWith("/api/playground/estimate", StringComparison.Ordinal)
                && sent.All(part => (request.PostData ?? "").Contains(part, StringComparison.Ordinal)));
            await W4LoadPresetAsync(page, preset);
            await page.ToastAsync("Preset loaded");
            await estimated;
            Check(await page.EvaluateAsync<bool>("() => window.__presetStay === true"), "the Playground loaded the preset in place");
            await Expect(sampling.Locator("[data-sampling-note]")).ToHaveTextAsync("echo-thinking has none.");
            await Expect(page.Locator("#pg-vb-low")).ToBeCheckedAsync();
            await Expect(page.Locator("#pg-summary")).ToHaveValueAsync("concise");
            await Expect(page.Locator("#pg-timeoutMs")).ToHaveValueAsync("120000");
            await Expect(page.Locator("#pg-parallel")).Not.ToBeCheckedAsync();
            await Expect(page.Locator("#pg-cr-long")).ToBeCheckedAsync();
            await Expect(page.Locator("#pg-maxRetries")).ToHaveValueAsync("2");
            await Expect(page.Locator("#pg-maxRetryDelayMs")).ToHaveValueAsync("5000");
            await Expect(page.Locator("#pg-budget-minimal")).ToHaveValueAsync("");
            await Expect(page.Locator("#pg-budget-low")).ToHaveValueAsync("2048");
            await Expect(page.Locator("#pg-budget-medium")).ToHaveValueAsync("");
            await Expect(page.Locator("#pg-budget-high")).ToHaveValueAsync("8192");
            await Expect(page.Locator("#pg-azureApiVersion")).ToHaveValueAsync("2025-04-01-preview");
            await Expect(config.Locator("[data-tool-name='get_weather'] [data-tool-strict]")).ToBeVisibleAsync();
            foreach (var option in new[] { "cacheRetention", "maxRetries", "maxRetryDelayMs", "thinkingBudgets", "azureApiVersion" })
                await Expect(config.Locator($"[data-option='{option}']")).ToHaveAttributeAsync("data-set", "true");
            await Expect(config.Locator("[data-modified-count]")).ToHaveTextAsync("10");

            // Faux takes no Azure API version: its row is marked, the banner says Run will refuse it, and Azure's pane is the one shown.
            await Expect(config.Locator("[data-option][data-conflict='true']")).ToHaveCountAsync(1);
            await Expect(config.Locator("[data-option='azureApiVersion']")).ToHaveAttributeAsync("data-conflict", "true");
            await Expect(config.Locator("[data-option='azureApiVersion'] [data-option-note]")).ToContainTextAsync("only Azure OpenAI takes it");
            await Expect(config.Locator("[data-config-conflicts]")).ToBeVisibleAsync();
            await Expect(config.Locator("[data-config-conflicts-text]")).ToContainTextAsync("An option you set isn't taken by this model");
            await Expect(config.Locator("[data-provider-pane-select]")).ToHaveValueAsync("azure");
            await config.Locator("[data-bs-target='#pg-cfg-provider']").ClickAsync();
            await Expect(page.Locator("#pg-azureApiVersion")).ToBeVisibleAsync();
            await Expect(config.Locator("[data-provider-pane='current']")).ToBeHiddenAsync();
            await Expect(config.Locator("[data-provider-pane='bedrock']")).ToBeHiddenAsync();

            // A preset saved from the page keeps every option and the strict flag.
            var saveDialog = await page.OpenModalAsync(page.Locator("[data-preset-save] >> visible=true").First, "modal-save-preset");
            await saveDialog.Locator("#sp-name").FillAsync("Every option again");
            await saveDialog.Locator("[data-preset-save-submit]").ClickAsync();
            await Expect(saveDialog).ToBeHiddenAsync();
            await page.ToastAsync("Preset saved");
            var again = await W4PresetNamedAsync(app, "Every option again");
            var options = again["options"]!;
            Equal("low|low|concise|120000|False|long|2|5000|2048|8192|2025-04-01-preview",
                string.Join("|", options["thinking"]?.GetValue<string>(), options["textVerbosity"]?.GetValue<string>(), options["reasoningSummary"]?.GetValue<string>(),
                    options["timeoutMs"]?.GetValue<int>(), options["parallelToolCalls"]?.GetValue<bool>(), options["cacheRetention"]?.GetValue<string>(),
                    options["maxRetries"]?.GetValue<int>(), options["maxRetryDelayMs"]?.GetValue<int>(), options["thinkingBudgets"]?["low"]?.GetValue<int>(),
                    options["thinkingBudgets"]?["high"]?.GetValue<int>(), options["azureApiVersion"]?.GetValue<string>()),
                "the saved options: " + options.ToJsonString());
            var savedTool = again["tools"]!.AsArray().Single()!;
            Equal("get_weather", savedTool["name"]?.GetValue<string>(), "the saved tool");
            Equal(true, savedTool["strict"]?.GetValue<bool>(), "and its strict flag");

            // A budget typed in is sent; the budgets row then clears as one.
            var typed = page.WaitForRequestAsync(request => request.Url.EndsWith("/api/playground/estimate", StringComparison.Ordinal)
                && (request.PostData ?? "").Contains("\"medium\":4096", StringComparison.Ordinal));
            await page.Locator("#pg-budget-medium").FillAsync("4096");
            await typed;
            var budgets = config.Locator("[data-option='thinkingBudgets']");
            await budgets.Locator("[data-option-clear]").ClickAsync();
            await Expect(budgets).ToHaveAttributeAsync("data-set", "false");
            foreach (var level in new[] { "minimal", "low", "medium", "high" })
                await Expect(page.Locator($"#pg-budget-{level}")).ToHaveValueAsync("");
            await Expect(config.Locator("[data-modified-count]")).ToHaveTextAsync("9");

            // "Clear them" clears the Azure API version; the run then carries the rest.
            await config.Locator("[data-config-clear-conflicts]").ClickAsync();
            await Expect(config.Locator("[data-config-conflicts]")).ToBeHiddenAsync();
            await Expect(page.Locator("#pg-azureApiVersion")).ToHaveValueAsync("");
            await Expect(config.Locator("[data-option='azureApiVersion']")).ToHaveAttributeAsync("data-set", "false");
            await Expect(config.Locator("[data-modified-count]")).ToHaveTextAsync("8");
            await page.Locator("[data-run]").ClickAsync();
            var id = await page.QueryAsync("run");
            await Expect(page.Locator("[data-run-output]")).ToHaveAttributeAsync("data-state", "completed", new() { Timeout = 30_000 });
            Equal("completed", await app.RunEndedAsync(id), "the run completed");

            // The run's address shows its options again (server-rendered from the run's request).
            await page.GotoAsync("/playground?run=" + Uri.EscapeDataString(id));
            await Expect(page.Locator("[data-run-output]")).ToHaveAttributeAsync("data-state", "completed", new() { Timeout = 30_000 });
            await Expect(page.Locator("#pg-cr-long")).ToBeCheckedAsync();
            await Expect(page.Locator("#pg-maxRetries")).ToHaveValueAsync("2");
            await Expect(page.Locator("#pg-maxRetryDelayMs")).ToHaveValueAsync("5000");
            await Expect(page.Locator("#pg-timeoutMs")).ToHaveValueAsync("120000");
            await Expect(page.Locator("#pg-budget-low")).ToHaveValueAsync("");
            await Expect(page.Locator("#pg-azureApiVersion")).ToHaveValueAsync("");
            await Expect(config.Locator("[data-option='thinkingBudgets']")).ToHaveAttributeAsync("data-set", "false");
            await Expect(config.Locator("[data-option='azureApiVersion']")).ToHaveAttributeAsync("data-set", "false");
            await Expect(config.Locator("[data-modified-count]")).ToHaveTextAsync("8");
            await Expect(config.Locator("[data-option][data-conflict='true']")).ToHaveCountAsync(0);

            // The samplingParams row follows the model.
            await Expect(sampling.Locator("[data-sampling-note]")).ToHaveTextAsync("echo-thinking has none.");
            await config.Locator("[data-provider-model] [data-provider='faux'] [data-model-select]").SelectOptionAsync("echo");
            await Expect(sampling.Locator("[data-sampling-note]")).ToHaveTextAsync("echo has none.");
            await Expect(sampling).ToHaveAttributeAsync("data-applied", "false");
        }),
    ];
}
