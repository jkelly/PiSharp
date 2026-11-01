using System.Text.Json.Nodes;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W4, fix 2, tests-ci): the Tool Bench's own script (bench.js and bench-tools.js) in a browser, on priced faux
// (--faux-metered), failing on any page or console error; T4's case covers only a manual result. The tool editor saves an edit: a tool
// added from a template is opened again with its pencil, its description changed and a mock response added (a name, a match object and a
// result), and Save tool stores it in the bench's setup; opened again, the editor shows the saved mock. The auto-respond rules then answer
// the model's calls with no click: a call whose arguments the mock matches gets the mock's result; with "Pause when no mock matches" off, a
// call no mock matches gets the bench's own "no mock" error result; with "Auto-respond with mock results" off, even a matching call pauses
// for the user, whose quick fill from the mock is sent as a manual result. Stop ends a running turn mid-stream (the turn's run ends, the
// Run button comes back and the answer never reaches its last word) and, through Esc, a bench paused on a call (the call is skipped). Every
// turn's ledger reservation ends.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> W4BenchToolCases() =>
    [
        Browse("browser.w4.bench.the-tool-editor-saves-a-mock-and-the-auto-respond-rules-answer-calls", AgentMode.FauxMetered, async (app, window) =>
        {
            var page = window.Page;
            await page.GotoAsync("/bench");
            var bench = page.Locator("[data-bench]");
            await Expect(bench).ToBeVisibleAsync();
            var session = (await bench.GetAttributeAsync("data-session"))!;
            await W4BenchTemplateAsync(page);

            // The pencil opens the editor on the tool; a new description and a mock response are saved with Save tool.
            var editor = await page.OpenModalAsync(page.Locator("[data-bench-edit='get_weather']"), "modal-tool-editor");
            await Expect(editor.Locator("#modal-tool-editor-title")).ToHaveTextAsync("Edit tool");
            await Expect(editor.Locator("[data-tool-submit-text]")).ToHaveTextAsync("Save tool");
            await Expect(editor.Locator("[data-bench-delete-tool]")).ToBeVisibleAsync();
            await Expect(editor.Locator("[data-mock-count]")).ToHaveTextAsync("0");
            await editor.Locator("[data-tool-field='description']").FillAsync("The weather in a city, edited on the bench.");
            await editor.Locator("[data-mock-add]").ClickAsync();
            var mock = editor.Locator("[data-mock]").Last;
            await mock.Locator("[data-mock-field='name']").FillAsync("Oslo drizzle");
            await mock.Locator("[data-mock-field='match']").FillAsync("{\"location\":\"Oslo, Norway\"}");
            await mock.Locator("[data-mock-field='text']").FillAsync("{\"tempC\": 4, \"sky\": \"drizzle\"}");
            await Expect(editor.Locator("[data-mock-count]")).ToHaveTextAsync("1");
            await Expect(editor.Locator("[data-mock-empty]")).ToBeHiddenAsync();
            await Expect(editor.Locator("[data-tool-check]")).ToContainTextAsync("Valid JSON Schema");
            await editor.Locator("[data-tool-submit]").ClickAsync();
            await Expect(editor).ToBeHiddenAsync();
            await page.ToastAsync("Tool saved");
            var tool = (await W4BenchSessionAsync(app, session))["setup"]!["tools"]!.AsArray().Single()!;
            Equal("The weather in a city, edited on the bench.", tool["description"]?.GetValue<string>(), "the saved description");
            var saved = tool["mocks"]!.AsArray().Single()!;
            Equal("Oslo drizzle", saved["name"]?.GetValue<string>(), "the saved mock");
            Equal("Oslo, Norway", saved["match"]?["location"]?.GetValue<string>(), "its match");
            Check(saved["text"]?.GetValue<string>().Contains("drizzle", StringComparison.Ordinal) == true, "its result: " + saved.ToJsonString());
            await Expect(page.Locator("[data-tool-def='get_weather']")).ToHaveAttributeAsync("data-definition", new System.Text.RegularExpressions.Regex("edited on the bench"));

            // Opened again, the editor shows what was saved.
            await page.OpenModalAsync(page.Locator("[data-bench-edit='get_weather']"), "modal-tool-editor");
            await Expect(editor.Locator("[data-tool-field='description']")).ToHaveValueAsync("The weather in a city, edited on the bench.");
            await Expect(editor.Locator("[data-mock]")).ToHaveCountAsync(1);
            await Expect(editor.Locator("[data-mock] [data-mock-field='name']")).ToHaveValueAsync("Oslo drizzle");
            await page.DismissModalAsync("modal-tool-editor");

            // Auto-respond (on by default): the matching mock answers the call, and the model's next turn ends the run.
            await W4BenchSendAsync(page, "tool: get_weather {\"location\":\"Oslo, Norway\"}");
            var calls = await W4BenchCallsAsync(app, session, 1, "finished");
            Equal("mock", calls[0]!["result"]?["source"]?.GetValue<string>(), "the mock answered: " + calls[0]!.ToJsonString());
            Equal("Oslo drizzle", calls[0]!["result"]?["mock"]?.GetValue<string>(), "the mock that matched");
            await Expect(bench).ToHaveAttributeAsync("data-bench-state", "finished");
            await Expect(page.Locator("[data-bench-part='timeline']")).ToContainTextAsync("Tool finished: get_weather");
            await Expect(page.Locator("[data-bench-part='log']")).ToContainTextAsync("Oslo drizzle");
            Check(!await page.Locator("[data-awaiting]").IsVisibleAsync(), "nothing waited for the user");

            // "Pause when no mock matches" off: a call no mock matches gets the bench's own "no mock" error result.
            await page.Locator("[aria-label='Auto-respond rules']").ClickAsync();
            await Expect(page.Locator("#ruleNoMock")).ToBeCheckedAsync();
            await page.Locator("#ruleNoMock").UncheckAsync();
            await WaitUntilAsync(async () => (await W4BenchSessionAsync(app, session))["setup"]!["rules"]!["pauseOnNoMock"]!.GetValue<bool>() == false,
                "the rule is saved");
            await page.Keyboard.PressAsync("Escape");
            await W4BenchSendAsync(page, "tool: get_weather {\"location\":\"Bergen, Norway\"}");
            calls = await W4BenchCallsAsync(app, session, 2, "finished");
            Equal("no-mock", calls[1]!["result"]?["source"]?.GetValue<string>(), "the no-mock result: " + calls[1]!.ToJsonString());
            Equal(true, calls[1]!["result"]?["isError"]?.GetValue<bool>(), "sent as an error");
            Check(!await page.Locator("[data-awaiting]").IsVisibleAsync(), "it did not pause");

            // Auto-respond off: even a call the mock matches waits for the user; the quick fill sends the mock's text as a manual result.
            await page.Locator("#autoRespond").UncheckAsync();
            await WaitUntilAsync(async () => (await W4BenchSessionAsync(app, session))["setup"]!["rules"]!["autoRespond"]!.GetValue<bool>() == false,
                "auto-respond is off");
            await W4BenchSendAsync(page, "tool: get_weather {\"location\":\"Oslo, Norway\"}");
            calls = await W4BenchCallsAsync(app, session, 3, "awaiting");
            Equal("manual", calls[2]!["pause"]?.GetValue<string>(), "it paused because auto-respond is off: " + calls[2]!.ToJsonString());
            await Expect(bench).ToHaveAttributeAsync("data-state", "awaiting");
            var awaiting = page.Locator("[data-awaiting]");
            await Expect(awaiting).ToBeVisibleAsync();
            await awaiting.Locator("[data-fill-name='Oslo drizzle']").ClickAsync();
            await Expect(awaiting.Locator("[data-result-text]")).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("drizzle"));
            await Expect(awaiting.Locator("[data-result-check]")).ToContainTextAsync("valid JSON");
            await awaiting.Locator("[data-result-send='continue']").ClickAsync();
            calls = await W4BenchCallsAsync(app, session, 3, "finished");
            Equal("manual", calls[2]!["result"]?["source"]?.GetValue<string>(), "sent by the user");
            Equal("Oslo drizzle", calls[2]!["result"]?["mock"]?.GetValue<string>(), "filled from the mock");
            await Expect(bench).ToHaveAttributeAsync("data-bench-state", "finished");

            // Six model turns, each a bench run under one group, each reservation ended.
            var rows = await app.SettledLedgerAsync(6);
            Check(rows.All(row => Reservation(row, "source") == "bench"), "bench rows: " + string.Join(" | ", rows.Select(row => row.ToJsonString())));
        }),

        Browse("browser.w4.bench.stop-ends-a-running-turn-and-esc-ends-a-pause", AgentMode.FauxMetered, async (app, window) =>
        {
            var page = window.Page;
            await page.GotoAsync("/bench");
            var bench = page.Locator("[data-bench]");
            var session = (await bench.GetAttributeAsync("data-session"))!;
            await W4BenchTemplateAsync(page);

            // A slow answer streams; Stop ends it before its last word.
            await W4BenchSendAsync(page, "slow: " + Words(150));
            await Expect(bench).ToHaveAttributeAsync("data-bench-state", "running");
            await Expect(page.Locator("[data-bench-stream]").Last).ToContainTextAsync("w002", new() { Timeout = 15_000 });
            var run = (await bench.GetAttributeAsync("data-run-id"))!;
            Check(run.Length > 0, "the running turn's run id");
            var stop = page.Locator("[data-bench-part='controls'] [data-bench-stop]");
            await Expect(stop).ToBeVisibleAsync();
            await stop.ClickAsync();
            await Expect(bench).ToHaveAttributeAsync("data-bench-state", "stopped");
            await Expect(page.Locator("[data-bench-part='controls'] [data-bench-run]")).ToBeVisibleAsync();
            Check(await app.RunEndedAsync(run) != "completed", "the turn's run did not complete");
            var stopped = await W4BenchSessionAsync(app, session);
            Equal("stopped", stopped["state"]?.GetValue<string>(), "the session stopped");
            var answers = string.Join("\n", stopped["entries"]!.AsArray().Where(entry => entry?["kind"]?.GetValue<string>() != "user").Select(entry => entry!.ToJsonString()));
            Check(!answers.Contains("w150", StringComparison.Ordinal), "the answer never reached its last word: " + answers[..Math.Min(400, answers.Length)]);

            // Paused on a call (no mock), Esc stops: the call is skipped and the card goes.
            await W4BenchSendAsync(page, "tool: get_weather {\"location\":\"Oslo, Norway\"}");
            await Expect(bench).ToHaveAttributeAsync("data-bench-state", "awaiting", new() { Timeout = 30_000 });
            var awaiting = page.Locator("[data-awaiting]");
            await Expect(awaiting).ToBeVisibleAsync();
            await Expect(page.Locator("[data-bench-part='controls'] [data-bench-stop]")).ToBeVisibleAsync();
            await awaiting.Locator("[data-result-text]").FocusAsync();
            await page.Keyboard.PressAsync("Escape");
            await Expect(bench).ToHaveAttributeAsync("data-bench-state", "stopped");
            await Expect(awaiting).ToHaveCountAsync(0);
            var calls = (await W4BenchSessionAsync(app, session))["calls"]!.AsArray();
            Equal(true, calls.Single()!["skipped"]?.GetValue<bool>(), "the waiting call is skipped: " + calls.ToJsonString());
            Check(calls.Single()!["result"] is null, "and got no result");

            // Neither turn left a reservation open.
            var rows = await app.SettledLedgerAsync(2);
            Check(rows.All(row => Reservation(row, "source") == "bench"), "bench rows: " + string.Join(" | ", rows.Select(row => row.ToJsonString())));
        }),
    ];

    /// <summary>Adds get_weather from the bench's Templates menu through the tool editor.</summary>
    private static async Task W4BenchTemplateAsync(IPage page)
    {
        await page.Locator("[data-bench-part='tools'] [data-bs-toggle='dropdown'] >> visible=true").First.ClickAsync();
        var editor = await page.OpenModalAsync(page.Locator("[data-bench-template*='get_weather'] >> visible=true").First, "modal-tool-editor");
        await Expect(editor.Locator("#modal-tool-editor-title")).ToHaveTextAsync("New tool");
        await editor.Locator("[data-tool-submit]").ClickAsync();
        await Expect(editor).ToBeHiddenAsync();
        await page.ToastAsync("Tool added");
        await Expect(page.Locator("[data-tool-def='get_weather']")).ToBeVisibleAsync();
    }

    /// <summary>Types a user message into the bench's box and presses Add &amp; run; returns once the box empties.</summary>
    private static async Task W4BenchSendAsync(IPage page, string text)
    {
        var box = page.Locator("#benchUserMsg");
        await Expect(box).ToBeEnabledAsync(new() { Timeout = 30_000 });
        await box.FillAsync(text);
        await page.Locator("[data-bench-add-run]").ClickAsync();
        await Expect(box).ToHaveValueAsync("", new() { Timeout = 15_000 });
    }

    /// <summary>The bench session as the server holds it (<c>GET /api/bench/sessions/{id}</c>).</summary>
    private static async Task<JsonNode> W4BenchSessionAsync(Agent app, string session)
    {
        var answer = await app.Get("/api/bench/sessions/" + Uri.EscapeDataString(session));
        Equal(200, answer.Status, "the bench session: " + answer.Text);
        return answer.Body!["session"]!;
    }

    /// <summary>The session's calls once it holds <paramref name="count"/> and is in <paramref name="state"/>.</summary>
    private static async Task<JsonArray> W4BenchCallsAsync(Agent app, string session, int count, string state)
    {
        JsonNode current = null!;
        await WaitUntilAsync(async () =>
        {
            current = await W4BenchSessionAsync(app, session);
            return current["calls"]?.AsArray().Count == count && current["state"]?.GetValue<string>() == state;
        }, $"the bench holds {count} call(s) and is {state}", TimeSpan.FromSeconds(30));
        return current["calls"]!.AsArray();
    }
}
