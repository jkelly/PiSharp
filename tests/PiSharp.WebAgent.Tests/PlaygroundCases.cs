// Decision 0011 (run W4, R1): the Playground and the shared request partials. The page renders the mockups' panes with nothing the CSP
// would block; the output pane renders each state (empty, streaming, completed, stopped, error) from real runs of the priced faux models,
// with model text through the sanitizing MarkdownRenderer; a faux run goes round trip through POST /api/runs, its SSE events and the output
// fragment the page swaps in, and a tool call is answered with a toolResult; options a model rejects are refused before anything is sent
// (and the config keeps their values, marked); View code, over real routing with a planted key, never carries the key, has LF line
// endings only (the cURL tab's continuations hold as run.sh), and links "Open last run in inspector" to the run the page names.
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Playground;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Shell;

internal static partial class Program
{
    private static readonly string[] PlaygroundSections = ["model", "sampling", "output", "reasoning", "tools", "cache", "tier", "transport", "headers", "provider"];

    private static readonly string[] PlaygroundScripts =
        ["playground.js", "request-api.js", "request-spec.js", "request-config.js", "request-messages.js", "request-tools.js", "request-output.js", "request-code.js"];

    internal static IEnumerable<(string, Func<Task>)> PlaygroundCases() =>
    [
        Case("playground.page.renders-the-empty-request", async () =>
        {
            await using var app = await WebAgentApp.StartAsync("pg-page");
            var page = await app.Get("/playground");
            Equal(200, page.Status, "GET /playground");
            var html = page.Text;
            PlaygroundNoInline("/playground", html);
            Check(html.Contains("<h1 class=\"app-title\">Playground</h1>", StringComparison.Ordinal), "the top bar title");
            Check(html.Contains("data-run-output data-state=\"empty\"", StringComparison.Ordinal) && html.Contains("Nothing run <em>yet</em>", StringComparison.Ordinal),
                "the output pane's empty state");
            Check(html.Contains("data-playground-toolbar data-state=\"empty\"", StringComparison.Ordinal) && html.Contains("not run yet", StringComparison.Ordinal), "the toolbar's");
            foreach (var section in PlaygroundSections)
                Check(html.Contains($"id=\"pg-cfg-{section}\"", StringComparison.Ordinal), "the config accordion has " + section);
            // The options the run engine took after R1 (parallel tool calls, strict tools, text verbosity, reasoning summary, session id,
            // timeout) have no control here yet: the last four show as unsent rows (below), the first two have none.
            string[] notWired = [RunModelOptions.ParallelToolCalls, RunModelOptions.StrictTools, RunModelOptions.TextVerbosity, RunModelOptions.ReasoningSummary,
                RunModelOptions.SessionId, RunModelOptions.TimeoutMs];
            foreach (var option in RunModelOptions.All.Except([RunModelOptions.Images, .. notWired]))
                Check(html.Contains($"data-option=\"{option}\"", StringComparison.Ordinal), "a row for " + option);
            foreach (var option in notWired)
                Check(!html.Contains($"data-option=\"{option}\"", StringComparison.Ordinal), "no control sends " + option + " yet");
            foreach (var unsent in RequestConfigView.Unsent.Keys.Except(["strict"]))
                Check(Regex.IsMatch(html, $"data-unsent=\"{unsent}\""), "the unsent option " + unsent + " is shown");
            Check(Regex.IsMatch(html, "<input type=\"password\"[^>]*id=\"pg-api-key\"[^>]*disabled"), "the API key field is never usable");
            Check(html.Contains("<option value=\"ultrafast\">UltraFast</option>", StringComparison.Ordinal) && !html.Contains("not sent yet</option>", StringComparison.Ordinal),
                "UltraFast is listed and sent (the run engine takes every tier PLAN.md lists)");
            Check(Regex.IsMatch(html, "id=\"pg-th-max\" value=\"max\" disabled=\"disabled\""), "max is listed, off for a model that does not think");
            Check(Regex.IsMatch(html, "data-provider-pane=\"azure\"") && Regex.IsMatch(html, "data-unsent=\"azureApiVersion\"") && html.Contains("data-provider-pane=\"bedrock\"", StringComparison.Ordinal),
                "the Azure and Bedrock panes");
            Check(Regex.IsMatch(html, "data-preset-save>") && Regex.IsMatch(html, "data-preset-load>") && !html.Contains("Presets are not available yet", StringComparison.Ordinal),
                "Save and Load preset: enabled, for presets-host.js to wire");
            Check(!html.Contains("Pin as test", StringComparison.Ordinal), "no Pin as test (W5)");
            Check(html.Contains("data-msg-prompt=\"true\"", StringComparison.Ordinal) && html.Contains("<template data-msg-template>", StringComparison.Ordinal), "the system prompt editor and the template");
            Check(html.Contains("data-provider-model=\"pg\"", StringComparison.Ordinal) && html.Contains("data-provider-model=\"mp\"", StringComparison.Ordinal), "Provider → Model in the config and the picker");
            foreach (var modal in new[] { "modal-model-picker", "modal-view-code", "modal-tool-editor", "modal-error-details", "modal-spend-confirm", "modal-shortcuts" })
                Check(html.Contains($"id=\"{modal}\"", StringComparison.Ordinal), "the page hosts #" + modal);
            Check(Regex.IsMatch(html, "<script type=\"module\" src=\"/js/playground\\.js\\?v="), "its module");
            Check(Regex.IsMatch(html, "<link href=\"/css/playground\\.css\\?v="), "its stylesheet");
            foreach (var script in PlaygroundScripts)
            {
                var asset = await app.Get("/js/" + script);
                Equal(200, asset.Status, script);
                Check(!Regex.IsMatch(asset.Text, "\\.innerHTML\\s*=\\s*[^'\";\\n]*\\b(delta|text|message)\\b"), script + " never writes model text as HTML");
            }
            Equal(200, (await app.Get("/css/playground.css")).Status, "playground.css");

            var missing = (await app.Get("/playground?run=nope")).Text;
            Check(missing.Contains("There is no run nope", StringComparison.Ordinal), "an unknown run says so");
            var thinking = (await app.Get("/playground?provider=faux&model=echo-thinking")).Text;
            Check(Regex.IsMatch(thinking, "id=\"pg-th-high\" value=\"high\"(?![^>]*disabled)") && thinking.Contains("data-capabilities=\"{", StringComparison.Ordinal),
                "a thinking model's levels are enabled and its capabilities are on the page");
            Check(Regex.IsMatch(thinking, "<option value=\"echo-thinking\" selected=\"selected\""), "?provider=&model= opens that model");
            Equal(404, (await app.Get("/playground/runs/nope/output")).Status, "an unknown run's output");
            Check((await app.Get("/playground/output")).Text.Contains("data-state=\"empty\"", StringComparison.Ordinal), "the empty output fragment (Clear output)");
            Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
        }),

        Case("playground.output.renders-each-state", async () =>
        {
            await using var runs = RunsHost.Faux("pg-output-runs");
            await using var render = RenderHost.Create("pg-output-render", (_, _) => { });
            var pages = new PlaygroundPages(runs.Services);

            var empty = await render.RenderAsync("_RunOutput", new RunOutputView());
            Check(empty.Contains("data-state=\"empty\"", StringComparison.Ordinal) && empty.Contains("Nothing run", StringComparison.Ordinal), "empty");
            Check(Regex.Matches(empty, "class=\"metric\"").Count == 8 && empty.Contains("data-metric=\"TTFT\"", StringComparison.Ordinal), "the metric strip, all dashes");

            var slow = await runs.Engine.StartAsync(new RunSpec { Provider = "faux", Model = "echo", Messages = [new RunMessage { Text = "slow: " + string.Join(" ", Enumerable.Range(1, 300)) }] });
            await WaitUntilAsync(() => Task.FromResult(runs.Services.GetRequiredService<RunLive>().Find(slow.Id!)?.Items.Count > 3), "the slow run streams");
            var streaming = await render.RenderAsync("_RunOutput", pages.Output(slow.Id!)!);
            Check(streaming.Contains("data-state=\"streaming\"", StringComparison.Ordinal) && streaming.Contains("data-stream-text", StringComparison.Ordinal)
                && streaming.Contains("stream-cursor", StringComparison.Ordinal), "streaming: the text so far, with the cursor");
            Check(Regex.IsMatch(streaming, $"data-run-id=\"{slow.Id}\" data-last-seq=\"[1-9]"), "streaming: the run and the last event seen, to resume from");
            Check(streaming.Contains("class=\"sse-row\" data-group=\"text\"", StringComparison.Ordinal), "streaming: the events so far");
            Check(runs.Engine.Cancel(slow.Id!), "cancelled");
            var aborted = await RunsFinishedAsync(runs.Store, slow.Id!);
            var stopped = await render.RenderAsync("_RunOutput", pages.Output(aborted.Id)!);
            Check(stopped.Contains("data-state=\"stopped\"", StringComparison.Ordinal) && stopped.Contains("Stopped before the answer finished", StringComparison.Ordinal), "stopped");

            var done = await runs.Engine.StartAsync(new RunSpec { Provider = "faux", Model = "echo", Messages = [new RunMessage { Text = "hello output" }] });
            var completedRun = await RunsFinishedAsync(runs.Store, done.Id!);
            var completed = await render.RenderAsync("_RunOutput", pages.Output(completedRun.Id)!);
            Check(completed.Contains("data-state=\"completed\"", StringComparison.Ordinal) && completed.Contains("Echo: hello output", StringComparison.Ordinal), "completed: the answer");
            foreach (var tab in new[] { "rendered", "text", "json", "events" })
                Check(completed.Contains($"id=\"pg-out-{tab}\"", StringComparison.Ordinal), "the " + tab + " tab");
            Check(completed.Contains("data-output-plain>Echo: hello output", StringComparison.Ordinal) && completed.Contains("data-output-json", StringComparison.Ordinal), "the Text and JSON tabs");
            Check(Regex.IsMatch(completed, "<span class=\"ev text-ps-ok\">done</span>"), "the Events tab ends with done");
            Check(Regex.IsMatch(completed, "data-metric=\"Cost\">.{0,160}\\$", RegexOptions.Singleline), "the cost metric (micro-USD as dollars): " + Cut(completed));

            // Model text goes through the sanitizing renderer: markup renders, script does not.
            var marked = completedRun with
            {
                Output = JsonNode.Parse("""{"role":"assistant","content":[{"type":"text","text":"**bold** <script>alert(1)</script>"},{"type":"toolCall","id":"call_1","name":"get_weather","arguments":{"city":"Oslo"}}]}"""),
            };
            var sanitized = await render.RenderAsync("_RunOutput", new RunOutputView { Run = marked });
            Check(sanitized.Contains("<strong>bold</strong>", StringComparison.Ordinal) && !sanitized.Contains("<script>alert", StringComparison.Ordinal), "Markdown, sanitized: " + Cut(sanitized));
            Check(sanitized.Contains("data-add-tool-result data-call-id=\"call_1\" data-tool-name=\"get_weather\"", StringComparison.Ordinal), "a tool call awaits its result");
            Check(sanitized.Contains("data-tool-calls=\"[{&quot;id&quot;:&quot;call_1&quot;", StringComparison.Ordinal), "with the calls for the message editor");

            var failed = await render.RenderAsync("_RunOutput", new RunOutputView { Run = completedRun with { Status = RunStatus.Error, Error = "The provider said <no>.", Output = null, OutputText = null } });
            Check(failed.Contains("data-state=\"error\"", StringComparison.Ordinal) && failed.Contains("The provider said &lt;no&gt;.", StringComparison.Ordinal), "error: the message, encoded");
            Check(failed.Contains("data-run-retry", StringComparison.Ordinal) && failed.Contains($"data-error-details=\"{completedRun.Id}\"", StringComparison.Ordinal)
                && failed.Contains("data-bs-target=\"#modal-model-picker\"", StringComparison.Ordinal), "error: Retry, Switch model and Details");

            var details = await render.RenderAsync("_ErrorDetailsModal", new ErrorDetailsView { Message = "429", RawBody = "{\"error\":\"<b>x</b>\"}", HttpStatus = "429" });
            Check(details.Contains("&lt;b&gt;x&lt;/b&gt;", StringComparison.Ordinal) && details.Contains("id=\"modal-error-details\"", StringComparison.Ordinal), "the details modal encodes the raw body");
            var shell = await render.RenderAsync("_ErrorDetailsModal", null);
            Check(shell.Contains("No failed run is selected.", StringComparison.Ordinal), "the details modal's empty shell");
            PlaygroundNoInline("_RunOutput", completed + sanitized + failed + details);
        }),

        Case("playground.run.faux-round-trip", async () =>
        {
            await using var app = await WebAgentApp.StartAsync("pg-round-trip");
            var spec = new JsonObject
            {
                ["provider"] = "faux",
                ["model"] = "echo",
                ["source"] = "playground",
                ["systemPrompt"] = "Be brief.",
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = "hello playground" }),
            };
            var estimate = await app.SendAsync(HttpMethod.Post, "/api/playground/estimate", spec.DeepClone(), origin: app.Origin);
            Equal(200, estimate.Status, "the estimate: " + estimate.Text);
            Check(estimate.Body?["valid"]?.GetValue<bool>() == true && estimate.Body["tokens"]?.GetValue<double>() > 0, "a valid draft with its input tokens: " + estimate.Text);
            Check(estimate.Body?["parts"]?["messages"]?.AsArray().Count == 1, "the tokens of each message");
            var model = await app.Get("/api/playground/model?provider=faux&model=echo");
            Check(model.Body?["capabilities"]?["api"]?.GetValue<string>() == "faux" && model.Body["auth"]?["offline"]?.GetValue<bool>() == true, "a model's options and account: " + model.Text);
            Equal(404, (await app.Get("/api/playground/model?provider=faux&model=nope")).Status, "an unknown model");

            var posted = await app.SendAsync(HttpMethod.Post, "/api/runs", spec.DeepClone(), origin: app.Origin);
            Check(posted.Status == 200 && posted.Body?["posted"]?.GetValue<bool>() == true, "Run posts to the run engine: " + posted.Text);
            var id = posted.Body!["id"]!.GetValue<string>();
            var frames = await RunsSseAsync(app, $"/api/runs/{id}/events");
            Equal("status", frames[^1].Event, "the stream ends with the status");
            Equal(RunStatus.Completed, frames[^1].Data?["status"]?.GetValue<string>(), "completed");
            var streamed = string.Concat(frames.Where(frame => frame.Event == "event" && frame.Data?["event"]?["type"]?.GetValue<string>() == "text_delta")
                .Select(frame => frame.Data!["event"]!["delta"]!.GetValue<string>()));
            Equal("Echo: hello playground", streamed, "the deltas the page appends");
            var fragment = await app.Get($"/playground/runs/{id}/output");
            Equal(200, fragment.Status, "the output fragment");
            Check(fragment.Text.TrimStart().StartsWith("<section class=\"pane pg-output\"", StringComparison.Ordinal), "one root, for the page to swap in");
            Check(fragment.Text.Contains("data-state=\"completed\"", StringComparison.Ordinal) && fragment.Text.Contains("Echo: hello playground", StringComparison.Ordinal), "the answer");
            Check(fragment.Response.Headers.CacheControl?.NoStore == true, "never cached");

            var reopened = (await app.Get("/playground?run=" + id)).Text;
            Check(reopened.Contains(">hello playground</textarea>", StringComparison.Ordinal) && reopened.Contains(">Be brief.</textarea>", StringComparison.Ordinal), "?run= reopens its request");
            Check(reopened.Contains("data-run-output data-state=\"completed\"", StringComparison.Ordinal) && reopened.Contains("last run ", StringComparison.Ordinal), "and its output");
            Check(reopened.Contains($"href=\"/runs/{id}\"", StringComparison.Ordinal), "Open in inspector names the run");

            // A tool call, answered with a toolResult the user writes.
            var tools = new JsonArray(new JsonObject { ["name"] = "get_weather", ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["city"] = new JsonObject { ["type"] = "string" } } } });
            var call = await app.SendAsync(HttpMethod.Post, "/api/runs", new JsonObject
            {
                ["provider"] = "faux",
                ["model"] = "echo",
                ["tools"] = tools.DeepClone(),
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = "tool: get_weather {\"city\":\"Oslo\"}" }),
            }, origin: app.Origin);
            var callId = call.Body!["id"]!.GetValue<string>();
            await RunsSseAsync(app, $"/api/runs/{callId}/events");
            var awaiting = (await app.Get($"/playground/runs/{callId}/output")).Text;
            var toolCall = Regex.Match(awaiting, "data-add-tool-result data-call-id=\"([^\"]+)\" data-tool-name=\"get_weather\"");
            Check(toolCall.Success, "the call awaits a result: " + Cut(awaiting));
            var answered = await app.SendAsync(HttpMethod.Post, "/api/runs", new JsonObject
            {
                ["provider"] = "faux",
                ["model"] = "echo",
                ["tools"] = tools.DeepClone(),
                ["messages"] = new JsonArray(
                    new JsonObject { ["role"] = "user", ["text"] = "tool: get_weather {\"city\":\"Oslo\"}" },
                    new JsonObject
                    {
                        ["role"] = "assistant",
                        ["toolCalls"] = new JsonArray(new JsonObject { ["id"] = toolCall.Groups[1].Value, ["name"] = "get_weather", ["arguments"] = new JsonObject { ["city"] = "Oslo" } }),
                    },
                    new JsonObject { ["role"] = "toolResult", ["toolCallId"] = toolCall.Groups[1].Value, ["toolName"] = "get_weather", ["text"] = "7 °C, light rain" }),
            }, origin: app.Origin);
            var answeredId = answered.Body!["id"]!.GetValue<string>();
            var last = await RunsSseAsync(app, $"/api/runs/{answeredId}/events");
            Equal(RunStatus.Completed, last[^1].Data?["status"]?.GetValue<string>(), "the conversation goes on after the toolResult");
            Check((await app.Get($"/playground/runs/{answeredId}/output")).Text.Contains("Tool finished", StringComparison.Ordinal), "the faux model's answer to the result");

            // A run streams while the page is open, and Stop cancels it.
            var slow = await app.SendAsync(HttpMethod.Post, "/api/runs", new JsonObject
            {
                ["provider"] = "faux",
                ["model"] = "echo",
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = "slow: " + string.Join(" ", Enumerable.Range(1, 400)) }),
            }, origin: app.Origin);
            var slowId = slow.Body!["id"]!.GetValue<string>();
            await RunsSseAsync(app, $"/api/runs/{slowId}/events", take: 3);
            var live = (await app.Get("/playground?run=" + slowId)).Text;
            Check(live.Contains("data-run-output data-state=\"streaming\"", StringComparison.Ordinal) && live.Contains("data-playground-toolbar data-state=\"streaming\"", StringComparison.Ordinal)
                && Regex.IsMatch(live, "data-stop(?![^>]*disabled)"), "a reopened running run streams, with Stop enabled");
            Equal(200, (await app.SendAsync(HttpMethod.Post, $"/api/runs/{slowId}/cancel", new JsonObject(), origin: app.Origin)).Status, "Stop");
            var ended = await RunsSseAsync(app, $"/api/runs/{slowId}/events");
            Equal(RunStatus.Aborted, ended[^1].Data?["status"]?.GetValue<string>(), "aborted");
            Check((await app.Get($"/playground/runs/{slowId}/output")).Text.Contains("data-state=\"stopped\"", StringComparison.Ordinal), "the stopped state");
            Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
        }),

        Case("playground.options.a-model-rejects-are-not-sent", async () =>
        {
            await using var app = await WebAgentApp.StartAsync("pg-refused");
            async Task Refused(JsonObject spec, string option, string what)
            {
                var answer = await app.SendAsync(HttpMethod.Post, "/api/runs", spec, origin: app.Origin);
                Equal(400, answer.Status, what + ": " + answer.Text);
                Check(answer.Body?["error"]?["refused"]?.AsArray().Any(item => item?["option"]?.GetValue<string>() == option) == true, what + " names " + option + ": " + answer.Text);
                Check(answer.Body?["error"]?["message"]?.GetValue<string>().Length > 0, what + " says why");
            }
            JsonObject Spec(string model, params (string Key, JsonNode Value)[] options)
            {
                var spec = new JsonObject { ["provider"] = "faux", ["model"] = model, ["messages"] = new JsonArray(new JsonObject { ["text"] = "hi" }) };
                foreach (var (key, value) in options) spec[key] = value;
                return spec;
            }
            await Refused(Spec("echo-thinking", ("thinking", "high"), ("temperature", 0.5)), RunModelOptions.Temperature, "temperature while the model thinks");
            await Refused(Spec("echo", ("thinking", "high")), RunModelOptions.Thinking, "thinking on a model that does not think");
            await Refused(Spec("echo", ("serviceTier", "flex")), RunModelOptions.ServiceTier, "a service tier off the OpenAI Responses API");
            await Refused(Spec("echo", ("headers", new JsonObject { ["x-tag"] = "1" })), RunModelOptions.Headers, "headers the faux provider has no request for");
            var list = await app.Get("/api/runs");
            Equal(0, list.Body?["total"]?.GetValue<int>(), "nothing ran: " + list.Text);
            var estimate = await app.SendAsync(HttpMethod.Post, "/api/playground/estimate", Spec("echo-thinking", ("thinking", "high"), ("temperature", 0.5)), origin: app.Origin);
            Check(estimate.Body?["valid"]?.GetValue<bool>() == false && estimate.Body["refused"]?.AsArray().Any(item => item?["option"]?.GetValue<string>() == "temperature") == true,
                "the estimate says so before Run: " + estimate.Text);
            Equal(400, (await app.SendAsync(HttpMethod.Post, "/api/runs", Spec("echo", ("thinking", "max")), origin: app.Origin)).Status, "max on a model that does not think");

            // The config keeps a value the model does not take, marked, and editable so it can be cleared; an unset one is disabled.
            await using var runs = RunsHost.Faux("pg-refused-render");
            var caps = runs.Engine.Capabilities("faux", "echo-thinking")!;
            var draft = new RunSpec { Provider = "faux", Model = "echo-thinking", Thinking = "high", Temperature = 0.5, Messages = [new RunMessage { Text = "hi" }] };
            var view = new RequestConfigView { Draft = draft, Capabilities = caps, ShowModel = false };
            Check(view.Conflict(RunModelOptions.Temperature) && !view.Off(RunModelOptions.Temperature) && view.Off(RunModelOptions.TopP), "the view's verdicts");
            await using var render = CatalogHost("pg-refused-host", runs.Services.GetRequiredService<IModelCatalog>());
            var html = await render.RenderAsync("_RequestConfig", view);
            var temperature = Regex.Match(html, "<div class=\"param-row\" data-option=\"temperature\" data-set=\"true\" data-conflict=\"true\"[^>]*>");
            Check(temperature.Success, "the temperature row is set and marked: " + Cut(html));
            Check(Regex.IsMatch(html, "id=\"pg-temperature\"[^>]*value=\"0.5\"(?![^>]*disabled)"), "its value is kept and it can be cleared");
            Check(Regex.IsMatch(html, "<div class=\"param-row param-disabled\" data-option=\"topP\" data-set=\"false\"") && Regex.IsMatch(html, "id=\"pg-top-p\"[^>]*disabled"),
                "top_p, unset and not taken while thinking, is disabled");
            Check(Regex.IsMatch(html, "data-config-conflicts(?![^>]*hidden)"), "the banner says Run will refuse the request");
            Check(Regex.IsMatch(html, "id=\"pg-th-high\" value=\"high\" checked=\"checked\"(?![^>]*disabled)"), "the model's level is checked");
            Check(html.Contains("data-capabilities=\"{", StringComparison.Ordinal), "the capabilities for request-config.js");
            PlaygroundNoInline("_RequestConfig", html);

            var messages = await render.RenderAsync("_MessageListEditor", new MessageListView
            {
                SystemPrompt = "sys <b>",
                Messages =
                [
                    new RunMessage { Role = "user", Text = "q", Images = [new RunImage("image/png", "AAAA")] },
                    new RunMessage { Role = "assistant", ToolCalls = [new RunToolCall("call_9", "get_weather", new JsonObject { ["city"] = "Oslo" })] },
                    new RunMessage { Role = "toolResult", ToolCallId = "call_9", ToolName = "get_weather", Text = "7" },
                ],
            });
            Check(messages.Contains(">sys &lt;b&gt;</textarea>", StringComparison.Ordinal) && Regex.Matches(messages, "data-msg ").Count == 5, "the system prompt, three messages and the template: " + Cut(messages));
            Check(Regex.IsMatch(messages, "<option value=\"call_9\" data-tool-name=\"get_weather\" selected=\"selected\">call_9</option>"), "the result answers the earlier call");
            Check(messages.Contains("data-image-mime=\"image/png\"", StringComparison.Ordinal) && Regex.IsMatch(messages, "data-msg-attach[^>]*disabled"), "the image, and Attach image off for a text-only model");
            PlaygroundNoInline("_MessageListEditor", messages);
        }),

        Case("playground.code.view-code-has-no-secret", async () =>
        {
            await using var host = RunsHost.Routing("pg-code");
            var key = Planted("sk-proj-");
            await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("openai", key));
            var model = ModelOf(host.Services.GetRequiredService<IModelCatalog>(), "openai", "openai-responses", "gpt-4.1");
            var pages = new PlaygroundPages(host.Services);
            var spec = new RunSpec
            {
                Provider = "openai",
                Model = model,
                SystemPrompt = "Be brief.",
                Temperature = 0.3,
                MaxTokens = 300,
                ServiceTier = "flex",
                Headers = new Dictionary<string, string> { ["x-request-tag"] = "playground" },
                Tools = [new RunTool("get_weather", "Weather", new JsonObject { ["type"] = "object" })],
                Messages = [new RunMessage { Text = "hello" }, new RunMessage { Text = "look", Images = [new RunImage("image/png", new string('A', 4000))] }],
            };
            var code = pages.Code(spec, "http://127.0.0.1:5180");
            var all = string.Join("\n", code.CSharp, code.Curl, code.Json, code.Provider, string.Join("\n", code.Notes));
            NoSecret(all, "View code", key);
            Check(!all.Contains("PLANTED", StringComparison.Ordinal), "no part of the key");
            Check(code.Curl.Contains("http://127.0.0.1:5180/api/runs", StringComparison.Ordinal) && code.Curl.Contains("••••", StringComparison.Ordinal), "cURL posts to this app, credential masked: " + code.Curl);
            Check(code.Provider?.Contains("x-request-tag", StringComparison.Ordinal) == true && code.Provider.Contains("\"service_tier\": \"flex\"", StringComparison.Ordinal),
                "what the run adds to the provider's request: " + code.Provider);
            Check(code.CSharp.Contains("GetModel(\"openai\", \"" + model + "\")", StringComparison.Ordinal) && code.CSharp.Contains("Be brief.", StringComparison.Ordinal), "the C# names the model: " + code.CSharp);
            Check(!code.Json.Contains(new string('A', 4000), StringComparison.Ordinal), "image data is abbreviated");

            var found = await pages.ModelAsync("openai", model, null);
            Check(found is { Auth.AccountLabel: not null }, "the account is bound");
            NoSecret(JsonSerializer.Serialize(found!.Value.Auth, SettingsStore.JsonOptions), "the account hint", key);
            var draft = await pages.DraftAsync(spec);
            Check(draft.Valid && draft.Estimate.Shown, "the estimate by Run over real routing: " + draft.Message);
            NoSecret(draft.Estimate.Tooltip + draft.Estimate.Label, "the estimate", key);
            Equal(0, host.Provider.Requests.Count, "View code and the estimate send nothing to the provider");

            await using var app = await WebAgentApp.StartAsync("pg-code-app");
            var answer = await app.SendAsync(HttpMethod.Post, "/api/playground/code", new JsonObject
            {
                ["provider"] = "faux", ["model"] = "echo", ["messages"] = new JsonArray(new JsonObject { ["text"] = "hi" }),
            }, origin: app.Origin);
            Equal(200, answer.Status, "POST /api/playground/code: " + answer.Text);
            Check(answer.Body?["cSharp"]?.GetValue<string>().Contains("StreamSimple", StringComparison.Ordinal) == true && answer.Body["curl"] is not null && answer.Body["json"] is not null,
                "C#, cURL and JSON: " + answer.Text);
            Check(answer.Body?["curl"]?.GetValue<string>().Contains(app.Origin + "/api/runs", StringComparison.Ordinal) == true, "for this app's origin");
            Equal(400, (await app.SendAsync(HttpMethod.Post, "/api/playground/code", new JsonObject { ["provider"] = "faux" }, origin: app.Origin)).Status, "an invalid request");
        }),

        Case("playground.code.view-code-is-lf-only-and-links-the-last-run", async () =>
        {
            // Every tab has LF endings only (AppendLine and indented JSON write CRLF on Windows; "\" + CRLF breaks run.sh's continuations).
            // The openai plan has every part: Pi wire messages, options, the body patch, headers and the provider JSON.
            await using (var host = RunsHost.Routing("pg-code-lf"))
            {
                var model = ModelOf(host.Services.GetRequiredService<IModelCatalog>(), "openai", "openai-responses", "gpt-4.1");
                var pages = new PlaygroundPages(host.Services);
                var spec = new RunSpec
                {
                    Provider = "openai",
                    Model = model,
                    SystemPrompt = "Be brief.\r\nAnswer in English.",
                    MaxTokens = 300,
                    ServiceTier = "flex",
                    Headers = new Dictionary<string, string> { ["x-request-tag"] = "playground" },
                    Tools = [new RunTool("get_weather", "Weather", new JsonObject { ["type"] = "object" })],
                    Messages = [new RunMessage { Text = "hello\r\nthere" }],
                };
                var code = pages.Code(spec, "http://127.0.0.1:5180");
                PlaygroundLfOnly(code.CSharp, "C#");
                PlaygroundLfOnly(code.Curl, "cURL");
                PlaygroundLfOnly(code.Json, "JSON");
                PlaygroundLfOnly(code.Provider ?? "", "the provider request");
                Check(code.Provider is not null && code.CSharp.Contains("JsonValue.Parse(\"\"\"\n", StringComparison.Ordinal), "the parts that were joined with \"\\n\" are there too");
                Equal(2, code.Curl.Split('\n').Count(line => line.EndsWith('\\')), "two continuation lines, each \"\\\" right before its LF: " + code.Curl);
                Check(code.Curl.Contains("/api/runs \\\n  -H \"content-type: application/json\" \\\n  --data-binary @run.json", StringComparison.Ordinal), "one curl command over three lines: " + code.Curl);
                Check(code.InspectorUrl is null, "no run named: no \"Open last run in inspector\"");
                Check(pages.Code(spec, "http://127.0.0.1:5180", "no-such-run").InspectorUrl is null, "a run runs.db does not have: no link");
            }

            // Over HTTP after a faux run: ?run= names the run the page shows, and the answer links its inspector page.
            await using var app = await WebAgentApp.StartAsync("pg-code-link");
            var request = new JsonObject
            {
                ["provider"] = "faux", ["model"] = "echo", ["source"] = "playground",
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = "hi" }),
            };
            var posted = await app.SendAsync(HttpMethod.Post, "/api/runs", request.DeepClone(), origin: app.Origin);
            Check(posted.Status == 200 && posted.Body?["posted"]?.GetValue<bool>() == true, "a faux run: " + posted.Text);
            var id = posted.Body!["id"]!.GetValue<string>();
            await RunsSseAsync(app, $"/api/runs/{id}/events");
            var linked = await app.SendAsync(HttpMethod.Post, "/api/playground/code?run=" + Uri.EscapeDataString(id), request.DeepClone(), origin: app.Origin);
            Equal(200, linked.Status, "POST /api/playground/code?run=: " + linked.Text);
            Equal("/runs/" + Uri.EscapeDataString(id), linked.Body?["inspectorUrl"]?.GetValue<string>(), "\"Open last run in inspector\" goes to the run's inspector page");
            foreach (var tab in new[] { "cSharp", "curl", "json" })
                PlaygroundLfOnly(linked.Body?[tab]?.GetValue<string>() ?? throw new InvalidOperationException("no " + tab + ": " + linked.Text), tab + " over HTTP");
            var unknown = await app.SendAsync(HttpMethod.Post, "/api/playground/code?run=no-such-run", request.DeepClone(), origin: app.Origin);
            Check(unknown.Status == 200 && unknown.Body?["inspectorUrl"] is null, "an unknown run: no link: " + unknown.Text);
            var none = await app.SendAsync(HttpMethod.Post, "/api/playground/code", request.DeepClone(), origin: app.Origin);
            Check(none.Status == 200 && none.Body?["inspectorUrl"] is null, "no run: no link: " + none.Text);
        }),
    ];

    /// <summary><paramref name="text"/> has no carriage return: LF line endings only.</summary>
    private static void PlaygroundLfOnly(string text, string what)
    {
        var at = text.IndexOf('\r', StringComparison.Ordinal);
        Check(at < 0, $"{what} has LF line endings only (a CR at {at}: {(at < 0 ? "" : text.Substring(Math.Max(0, at - 60), Math.Min(120, text.Length - Math.Max(0, at - 60))).Replace("\r", "<CR>", StringComparison.Ordinal))})");
    }

    /// <summary>Nothing the Content-Security-Policy (script-src 'self') would block: no inline script, no inline event handler.</summary>
    private static void PlaygroundNoInline(string where, string html)
    {
        foreach (Match script in Regex.Matches(html, "<script\\b[^>]*>"))
            Check(script.Value.Contains("src=", StringComparison.Ordinal), $"{where}: no inline script ({script.Value})");
        var handler = Regex.Match(html, "\\son[a-z]+\\s*=", RegexOptions.IgnoreCase);
        Check(!handler.Success, $"{where}: no inline event handlers ({(handler.Success ? html.Substring(Math.Max(0, handler.Index - 80), Math.Min(160, html.Length - Math.Max(0, handler.Index - 80))) : "")})");
    }
}
