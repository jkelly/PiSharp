using System.Text.Json.Nodes;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W5, fix 2, tests-ci): Pin as test case (E7's modal-test-case) from every page that hosts it but the Arena's reveal (which
// W5FlowCases pins from), on priced faux, failing on any page or console error. Each source opens the modal on its own run (or chat
// answer), the server's draft shows that run's prompt and answer as the reference, and Save stores a case the API reads back with the
// source, the run (or the chat and its entry), the request and the reference: the Playground's output into a new suite; a chat answer's
// footer; History's row menu into the suite that already exists (the modal offers the newest suite first); the inspector's More menu; and
// a Compare column; and a Tool Bench turn (W5 live test). On a suite page, Add case writes a request by hand (with a quick Contains check, no reference) and Edit case renames it,
// tags it and drops its check; each save reloads the page with the case as saved.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> W5PinCases() =>
    [
        Browse("browser.w5.pins.playground-output-pins-into-a-new-suite", AgentMode.FauxMetered, async (app, window) =>
        {
            var page = window.Page;
            await page.GotoAsync("/playground");
            await page.Locator("[data-msg][data-role='user'] [data-msg-text]").First.FillAsync("Pin me from the Playground.");
            await page.Locator("[data-run]").ClickAsync();
            var output = page.Locator("[data-run-output]");
            await Expect(output).ToHaveAttributeAsync("data-state", "completed", new() { Timeout = 30_000 });
            var run = await page.QueryAsync("run");
            var pinned = await page.W5PinAsync(output.Locator($"[data-pin-run='{run}'][data-pin-source='playground']"), "Pin me from the Playground.",
                "Echo: Pin me from the Playground.", "From the Playground", newSuite: "Playground pins");
            await app.W5PinnedAsync(pinned, "playground", run, "Pin me from the Playground.", "Echo: Pin me from the Playground.");
        }),

        Browse("browser.w5.pins.chat-answer-footer-pins-the-answer", AgentMode.FauxMetered, async (app, window) =>
        {
            var chat = await app.CreateChatAsync("Pin me from a chat.", "w5-pin-chat");
            var page = window.Page;
            await page.OpenChatAsync(chat);
            await page.IdleAsync(app, chat);
            var answer = page.Answers().Last;
            await Expect(answer).ToContainTextAsync("Echo: Pin me from a chat.");
            var pin = answer.Locator($"[data-pin-chat='{chat}'][data-pin-source='chat']");
            await Expect(pin).ToHaveCountAsync(1);
            var entry = (await pin.GetAttributeAsync("data-pin-entry"))!;
            var pinned = await page.W5PinAsync(pin, "Pin me from a chat.", "Echo: Pin me from a chat.", "From a chat", newSuite: "Chat pins");
            var stored = await app.W5PinnedAsync(pinned, "chat", null, "Pin me from a chat.", "Echo: Pin me from a chat.");
            Equal(chat, stored["origin"]?["chatId"]?.GetValue<string>(), "the case names its chat");
            Equal(entry, stored["origin"]?["entryId"]?.GetValue<string>(), "and the answer it was pinned from");
        }),

        Browse("browser.w5.pins.history-row-menu-pins-into-the-existing-suite", AgentMode.FauxMetered, async (app, window) =>
        {
            var suite = await app.W5PlainSuiteAsync("Existing suite");
            var run = await app.PlaygroundRunAsync("Pin me from History.");
            var page = window.Page;
            await page.GotoAsync("/history");
            var row = page.Locator($"tr#run-{run}[data-run-row]");
            await Expect(row).ToHaveAttributeAsync("data-run-status", "completed");
            await row.Locator("[data-bs-toggle='dropdown']").ClickAsync();
            var pinned = await page.W5PinAsync(row.Locator($"[data-pin-run='{run}'][data-pin-source='history']"), "Pin me from History.",
                "Echo: Pin me from History.", "From History", existingSuite: suite);
            var stored = await app.W5PinnedAsync(pinned, "history", run, "Pin me from History.", "Echo: Pin me from History.");
            Equal(suite, stored["suiteId"]?.GetValue<string>(), "saved into the suite that was there");
            Equal(1, (await app.Get("/api/evals/suites")).Body?["items"]?.AsArray().Count, "no other suite was made");
        }),

        Browse("browser.w5.pins.inspector-more-menu-pins-the-run", AgentMode.FauxMetered, async (app, window) =>
        {
            var run = await app.PlaygroundRunAsync("Pin me from the inspector.", system: "Answer in one line.");
            var page = window.Page;
            await page.GotoAsync("/runs/" + run);
            var inspector = page.Locator($"[data-run-inspector='{run}']");
            await Expect(inspector).ToHaveAttributeAsync("data-state", "default");
            await inspector.Locator("[data-run-header] [aria-label='More run actions']").ClickAsync();
            var pinned = await page.W5PinAsync(inspector.Locator($"[data-pin-run='{run}'][data-pin-source='inspector']"), "Pin me from the inspector.",
                "Echo: Pin me from the inspector.", "From the inspector", newSuite: "Inspector pins");
            var stored = await app.W5PinnedAsync(pinned, "inspector", run, "Pin me from the inspector.", "Echo: Pin me from the inspector.");
            Equal("Answer in one line.", stored["spec"]?["systemPrompt"]?.GetValue<string>(), "the request keeps its system prompt");
        }),

        Browse("browser.w5.pins.compare-column-pins-its-answer", AgentMode.FauxMetered, async (app, window) =>
        {
            var page = window.Page;
            await page.GotoAsync("/compare");
            var columns = page.Locator("[data-compare-col]");
            await Expect(columns).ToHaveCountAsync(2);
            await page.Locator("#cmpPrompt").FillAsync("Pin me from Compare.");
            await page.Locator("[data-run-all]").ClickAsync();
            await Expect(page.Locator("[data-compare-status-text]")).ToContainTextAsync("2 of 2 done", new() { Timeout = 30_000 });
            var column = columns.Nth(1);
            await Expect(column).ToHaveAttributeAsync("data-state", "completed");
            var run = (await column.GetAttributeAsync("data-run-id"))!;
            var model = (await column.GetAttributeAsync("data-model"))!;
            var pinned = await page.W5PinAsync(column.Locator($"[data-pin-run='{run}'][data-pin-source='compare']"), "Pin me from Compare.",
                "Echo: Pin me from Compare.", "From Compare", newSuite: "Compare pins");
            var stored = await app.W5PinnedAsync(pinned, "compare", run, "Pin me from Compare.", "Echo: Pin me from Compare.");
            Equal(model, stored["spec"]?["model"]?.GetValue<string>(), "the case asks the column's model");
        }),

        // W5 live test: Pin as test from a Tool Bench turn (W5 fix 2 (pins) added the button after these cases were written). The answer after
        // a manual tool result pins with the whole conversation: the user's message, the call, the result and the tool's definition.
        Browse("browser.w5.pins.tool-bench-turn-pins-its-conversation", AgentMode.FauxMetered, async (app, window) =>
        {
            var page = window.Page;
            await page.GotoAsync("/bench");
            var bench = page.Locator("[data-bench]");
            await Expect(bench).ToBeVisibleAsync();
            await W4BenchTemplateAsync(page);
            const string prompt = "tool: get_weather {\"location\":\"Oslo, Norway\"}";
            await W4BenchSendAsync(page, prompt);
            await Expect(bench).ToHaveAttributeAsync("data-state", "awaiting", new() { Timeout = 30_000 });
            var awaiting = page.Locator("[data-awaiting]");
            await awaiting.Locator("[data-result-text]").FillAsync("{\"tempC\": 4, \"sky\": \"drizzle\"}");
            await awaiting.Locator("[data-result-send='continue']").ClickAsync();
            await Expect(bench).ToHaveAttributeAsync("data-bench-state", "finished", new() { Timeout = 30_000 });

            await Expect(page.Locator("[data-pin-run][data-pin-source='bench']")).ToHaveCountAsync(2);
            var pin = page.Locator("[data-pin-run][data-pin-source='bench'][aria-label='Pin turn 2 as a test case']");
            var run = (await pin.GetAttributeAsync("data-pin-run"))!;
            var pinned = await page.W5PinAsync(pin, prompt, "Tool finished: get_weather", "From the Tool Bench", newSuite: "Bench pins");
            var stored = await app.W5PinnedAsync(pinned, "bench", run, prompt, "Tool finished: get_weather");
            Check(stored["spec"]?["tools"]?.AsArray().Any(tool => tool?["name"]?.GetValue<string>() == "get_weather") == true,
                "the case sends the bench's tool: " + stored["spec"]?.ToJsonString());
            Check(stored["spec"]?["messages"]?.AsArray().Any(message => message?["text"]?.GetValue<string>()?.Contains("drizzle", StringComparison.Ordinal) == true) == true,
                "and the tool result the answer read: " + stored["spec"]?.ToJsonString());
        }),

        Browse("browser.w5.pins.suite-page-adds-a-case-by-hand-and-edits-it", AgentMode.FauxMetered, async (app, window) =>
        {
            var suite = await app.W5PlainSuiteAsync("Written cases");
            var page = window.Page;
            await page.GotoAsync("/evals/suites/" + suite);
            await Expect(page.Locator("[data-no-cases]")).ToBeVisibleAsync();

            // Add case: a request written here, with a quick Contains check; it has no reference output.
            var modal = await page.OpenModalAsync(page.Locator($"[data-suite-cases] [data-pin-source='suite'][data-pin-suite='{suite}']"), "modal-test-case");
            var content = modal.Locator("[data-pin-content]");
            await Expect(content).ToHaveAttributeAsync("data-pin-state", "ready");
            await Expect(content).ToHaveAttributeAsync("data-pin-mode", "add");
            await Expect(content).ToHaveAttributeAsync("data-pin-reference", "false");
            await Expect(modal.Locator("#tcaseSuite")).ToBeDisabledAsync();
            var caseId = (await content.GetAttributeAsync("data-pin-case"))!;
            await modal.Locator("#tcaseName").FillAsync("Written by hand");
            await modal.Locator("#tcasePrompt").FillAsync("Is 2.0.0-beta.1 valid SemVer?");
            await modal.Locator("#tcaseMaxTokens").FillAsync("128");
            await modal.Locator("[data-bs-target='#tcaseAddCheck']").ClickAsync();
            await Expect(modal.Locator("#tcaseAddCheck")).ToBeVisibleAsync();
            await modal.Locator("#tcaseCheckKind").SelectOptionAsync("contains");
            await modal.Locator("#tcaseCheckValue").FillAsync("SemVer");
            await modal.Locator("[data-pin-check-add]").ClickAsync();
            await Expect(modal.Locator("[data-pin-checks] [data-pin-check]")).ToHaveCountAsync(1);
            await Expect(modal.Locator("[data-pin-checks-line]")).ToContainTextAsync("1 check");
            await modal.Locator("[data-pin-save]").ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.Load);
            var row = page.Locator($"[data-case-row='{caseId}']");
            await Expect(row).ToContainTextAsync("Written by hand", new() { Timeout = 15_000 });
            await Expect(row.Locator("[data-edit-check][data-check-id]")).ToHaveCountAsync(1);
            var added = (await app.Get("/api/evals/cases/" + caseId)).Body!["case"]!;
            Equal("suite", added["origin"]?["source"]?.GetValue<string>(), "added on the suite page");
            Equal("Is 2.0.0-beta.1 valid SemVer?", added["spec"]?["messages"]?[0]?["text"]?.GetValue<string>(), "the written request");
            Equal(128, added["spec"]?["maxTokens"]?.GetValue<int>(), "its max tokens");
            Check(added["reference"] is null, "no reference output: " + added.ToJsonString());
            Equal("contains", added["checks"]?[0]?["kind"]?.GetValue<string>(), "the quick check");

            // Edit case: the name, the tags and the checks; the request stays as written.
            await row.Locator("[data-bs-toggle='dropdown']").ClickAsync();
            modal = await page.OpenModalAsync(row.Locator($"[data-pin-source='suite'][data-pin-case='{caseId}']"), "modal-test-case");
            content = modal.Locator("[data-pin-content]");
            await Expect(content).ToHaveAttributeAsync("data-pin-mode", "edit");
            await Expect(content).ToHaveAttributeAsync("data-pin-case", caseId);
            await Expect(modal.Locator("#tcaseName")).ToHaveValueAsync("Written by hand");
            await modal.Locator("#tcaseName").FillAsync("Written by hand, edited");
            await modal.Locator("#tcaseTags").FillAsync("semver, prerelease");
            await modal.Locator("[data-pin-checks] [data-pin-check] [data-pin-remove]").ClickAsync();
            await Expect(modal.Locator("[data-pin-checks] [data-pin-check]")).ToHaveCountAsync(0);
            await modal.Locator("[data-pin-save]").ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.Load);
            await Expect(row).ToContainTextAsync("Written by hand, edited", new() { Timeout = 15_000 });
            await Expect(row.Locator("[data-edit-check][data-check-id]")).ToHaveCountAsync(0);
            var edited = (await app.Get("/api/evals/cases/" + caseId)).Body!["case"]!;
            Equal("semver,prerelease", string.Join(",", edited["tags"]!.AsArray().Select(tag => tag!.GetValue<string>())), "tagged");
            Equal(0, edited["checks"]?.AsArray().Count ?? 0, "its check removed");
            Equal("Is 2.0.0-beta.1 valid SemVer?", edited["spec"]?["messages"]?[0]?["text"]?.GetValue<string>(), "the request kept");
            Equal(1, (await app.Get("/api/evals/suites/" + suite)).Body?["cases"]?.AsArray().Count, "one case, edited in place");
        }),
    ];

    /// <summary>A suite of faux/echo (k = 1, a $1 suite-run cap) with no case, through the Suites unit's API; returns its id.</summary>
    internal static async Task<string> W5PlainSuiteAsync(this Agent app, string name)
    {
        var created = await app.PostAsync("/api/evals/suites", new JsonObject
        {
            ["name"] = name,
            ["k"] = 1,
            ["capMicros"] = 1_000_000,
            ["targets"] = new JsonArray(new JsonObject { ["provider"] = "faux", ["model"] = "echo" }),
        });
        Check(created.Status == 200 && created.Body?["created"]?.GetValue<bool>() == true, "a suite: " + created.Text);
        return created.Body!["suite"]!["id"]!.GetValue<string>();
    }

    /// <summary>Opens Pin as test case from <paramref name="opener"/>, checks the server's draft (the prompt and the reference output), names
    /// the case and saves it into a new suite (<paramref name="newSuite"/>) or the existing one the modal offers first
    /// (<paramref name="existingSuite"/>); returns the case id the content came with, once the toast says where it went (Made the suite …, or
    /// Added to …).</summary>
    private static async Task<string> W5PinAsync(this IPage page, ILocator opener, string prompt, string reference, string name,
        string? newSuite = null, string? existingSuite = null)
    {
        var modal = await page.OpenModalAsync(opener, "modal-test-case");
        var content = modal.Locator("[data-pin-content]");
        await Expect(content).ToHaveAttributeAsync("data-pin-state", "ready");
        await Expect(content).ToHaveAttributeAsync("data-pin-mode", "pin");
        await Expect(content).ToHaveAttributeAsync("data-pin-reference", "true");
        await Expect(modal.Locator("[data-pin-prompt-text]")).ToContainTextAsync(prompt);
        await Expect(modal.Locator("[data-pin-reference-text]")).ToContainTextAsync(reference);
        var caseId = (await content.GetAttributeAsync("data-pin-case"))!;
        await modal.Locator("#tcaseName").FillAsync(name);
        string toast;
        if (existingSuite is not null)
        {
            await Expect(modal.Locator("#tcaseSuite")).ToHaveValueAsync(existingSuite);
            await Expect(modal.Locator($"[data-pin-pane='{existingSuite}']")).ToBeVisibleAsync();
            toast = "Added to " + (await modal.Locator($"#tcaseSuite option[value='{existingSuite}']").TextContentAsync())!.Split(" · ")[0].Trim();
        }
        else
        {
            await modal.Locator("#tcaseSuite").SelectOptionAsync("new");
            await Expect(modal.Locator("[data-pin-pane='new']")).ToBeVisibleAsync();
            await modal.Locator("#tcaseNewSuite").FillAsync(newSuite ?? throw new ArgumentException("Name a new suite or an existing one."));
            toast = "Made the suite " + newSuite;
        }
        await modal.Locator("[data-pin-save]").ClickAsync();
        await Expect(modal).ToBeHiddenAsync();
        await page.ToastAsync(toast);
        await Expect(page.Locator("#toasts [data-pin-open-suite]").Last).ToBeVisibleAsync();
        return caseId;
    }

    /// <summary>The stored case <paramref name="caseId"/> (<c>GET /api/evals/cases/{id}</c>), checked: pinned from <paramref name="source"/>
    /// and <paramref name="run"/> (when given), asking <paramref name="prompt"/>, with <paramref name="reference"/> as its reference output.</summary>
    private static async Task<JsonNode> W5PinnedAsync(this Agent app, string caseId, string source, string? run, string prompt, string reference)
    {
        var read = await app.Get("/api/evals/cases/" + caseId);
        Equal(200, read.Status, "the pinned case is stored: " + read.Text);
        var stored = read.Body!["case"]!;
        Equal(source, stored["origin"]?["source"]?.GetValue<string>(), "pinned from " + source);
        if (run is not null) Equal(run, stored["origin"]?["runId"]?.GetValue<string>(), "from its run");
        Check(stored["spec"]?["messages"]?.AsArray().Any(message => message?["text"]?.GetValue<string>() == prompt) == true,
            "the request asks the prompt: " + stored["spec"]?.ToJsonString());
        Check(stored["reference"]?["text"]?.GetValue<string>()?.Contains(reference, StringComparison.Ordinal) == true,
            "the reference output is the answer: " + stored["reference"]?.ToJsonString());
        Check(stored["spec"]?["provider"]?.GetValue<string>() == "faux", "a runnable request on the faux provider: " + stored["spec"]?.ToJsonString());
        return stored;
    }
}
