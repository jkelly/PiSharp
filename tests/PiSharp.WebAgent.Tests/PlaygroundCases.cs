// Decision 0011 (run W4, R1): the Playground and the shared request partials. The page renders the mockups' panes with nothing the CSP
// would block; the output pane renders each state (empty, streaming, completed, stopped, error) from real runs of the priced faux models,
// with model text through the sanitizing MarkdownRenderer; a faux run goes round trip through POST /api/runs, its SSE events and the output
// fragment the page swaps in, and a tool call is answered with a toolResult; options a model rejects are refused before anything is sent
// (and the config keeps their values, marked); View code, over real routing with a planted key, never carries the key, has LF line
// endings only (the cURL tab's continuations hold as run.sh), and links "Open last run in inspector" to the run the page names.
// W4 fix: every option RunSpec takes has a live control the page sends (text verbosity, reasoning summary, session id, timeout, parallel
// tool calls and a tool's strict flag, checked against the modules the page loads), each enabled only where the model's API takes it; the
// JSON the page posts reaches the run, History and an OpenAI Responses request body; a model that rejects one is refused before anything
// is sent, and the config keeps the value, marked. The Speed list and the Reasoning levels are RunSpec's own (Fast, UltraFast and max).
// W5 L4: the options the run engine carries (cache retention, retries, the retry delay, thinking budgets, the Azure API version) have live
// rows the page sends and restores, reach the run, History and the provider's request (an Anthropic thinking budget, Azure's api-version),
// and are enabled where the model's API takes them; the options a run refuses by design say so and where they are set; Sampling's
// read-only samplingParams row shows the model's catalog sampling values.
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Playground;
using PiSharp.WebAgent.Routing;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Shell;

internal static partial class Program
{
    private static readonly string[] PlaygroundSections = ["model", "sampling", "output", "reasoning", "tools", "cache", "tier", "transport", "headers", "provider"];

    // The options the run engine carries that a run takes (W5 L4: live rows); RunModelOptions.Carried's others are refused by design.
    private static readonly string[] PlaygroundCarried =
        [RunModelOptions.CacheRetention, RunModelOptions.MaxRetries, RunModelOptions.MaxRetryDelayMs, RunModelOptions.ThinkingBudgets, RunModelOptions.AzureApiVersion];

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
            // Every option RunSpec takes has a live row (a tool's strict flag is the tool editor's switch); none is an unsent row. W5 L4: so do
            // the options the run engine carries (cache retention, retries, the retry delay, thinking budgets, the Azure API version); the
            // ones a run refuses by design are the unsent rows, and no row says an option is "not sent yet".
            foreach (var option in RunModelOptions.All.Except([RunModelOptions.Images, RunModelOptions.StrictTools]).Concat(PlaygroundCarried))
                Check(html.Contains($"data-option=\"{option}\"", StringComparison.Ordinal), "a row for " + option);
            foreach (var option in RunModelOptions.All.Concat(PlaygroundCarried))
                Check(!html.Contains($"data-unsent=\"{option}\"", StringComparison.Ordinal), option + " is not shown as unsent");
            foreach (var unsent in RequestConfigView.Unsent.Keys)
                Check(Regex.IsMatch(html, $"data-unsent=\"{unsent}\""), "the unsent option " + unsent + " is shown");
            foreach (var option in RunModelOptions.Carried.Except(PlaygroundCarried))
                Check(RequestConfigView.Unsent.ContainsKey(option) && !html.Contains($"data-option=\"{option}\"", StringComparison.Ordinal), option + " is refused by design: an unsent row");
            Check(!html.Contains("not sent yet", StringComparison.Ordinal) && !html.Contains("not sent from this page yet", StringComparison.Ordinal), "no row says an option is not sent yet");
            Check(RequestConfigView.Unsent.Where(pair => pair.Key != "apiKey").All(pair => pair.Value.Reason.Contains("refused per run by design", StringComparison.OrdinalIgnoreCase)
                && pair.Value.Reason.Contains("Settings › Providers", StringComparison.Ordinal)), "each refused-by-design row says so, and where it is set");
            var sampling = Regex.Match(html, "data-sampling-params data-applied=\"false\">.*?data-sampling-chip>([^<]*)</span>.*?data-sampling-note>([^<]*)</div>", RegexOptions.Singleline);
            Check(sampling.Success && sampling.Groups[1].Value == "not applied by this api" && sampling.Groups[2].Value.EndsWith(" has none.", StringComparison.Ordinal),
                "Sampling's samplingParams row: the faux model has none and its API applies none: " + Cut(sampling.Value));
            Check(html.IndexOf("data-sampling-params", StringComparison.Ordinal) > html.IndexOf("data-option=\"temperature\"", StringComparison.Ordinal)
                && html.IndexOf("data-sampling-params", StringComparison.Ordinal) < html.IndexOf("data-option=\"topP\"", StringComparison.Ordinal), "the row follows temperature");
            Check(Regex.IsMatch(html, "id=\"tool-strict\"(?![^>]*disabled)[^>]*data-tool-field=\"strict\""), "the tool editor's Strict switch is live");
            Check(Regex.IsMatch(html, "<input type=\"password\"[^>]*id=\"pg-api-key\"[^>]*disabled"), "the API key field is never usable");
            Check(html.Contains("<option value=\"ultrafast\">UltraFast</option>", StringComparison.Ordinal) && !html.Contains("not sent yet</option>", StringComparison.Ordinal),
                "UltraFast is listed and sent (the run engine takes every tier PLAN.md lists)");
            Check(Regex.IsMatch(html, "id=\"pg-th-max\" value=\"max\" disabled=\"disabled\""), "max is listed, off for a model that does not think");
            // The Speed list and the Reasoning levels are the run engine's own: Fast, UltraFast and max are sent where the model takes them.
            Equal(string.Join(",", RunSpec.ServiceTiers), string.Join(",", RequestConfigView.Tiers.Select(tier => tier.Value).Where(value => value.Length > 0)),
                "the Speed list is RunSpec.ServiceTiers");
            Check(RequestConfigView.Tiers.All(tier => tier.Sent), "every listed tier is sent");
            Equal(string.Join(",", RunSpec.ThinkingLevels), string.Join(",", RequestConfigView.Levels), "the Reasoning levels are RunSpec.ThinkingLevels, max last");
            foreach (var tier in RunSpec.ServiceTiers)
                Check(Regex.IsMatch(html, $"<option value=\"{tier}\"[^>]*>[^<]+</option>"), "the Speed list has " + tier);
            foreach (var level in RunSpec.ThinkingLevels)
                Check(html.Contains($"id=\"pg-th-{level}\" value=\"{level}\"", StringComparison.Ordinal), "the Reasoning control lists " + level);
            await PlaygroundWiredAsync(app, html);
            Check(Regex.IsMatch(html, "data-provider-pane=\"azure\"") && Regex.IsMatch(html, "data-option=\"azureApiVersion\"") && Regex.IsMatch(html, "data-unsent=\"azureBaseUrl\"")
                && html.Contains("data-provider-pane=\"bedrock\" data-unsent=\"bedrock\"", StringComparison.Ordinal), "the Azure and Bedrock panes");
            Check(Regex.IsMatch(html, "data-preset-save>") && Regex.IsMatch(html, "data-preset-load>") && !html.Contains("Presets are not available yet", StringComparison.Ordinal),
                "Save and Load preset: enabled, for presets-host.js to wire");
            Check(html.Contains("id=\"modal-test-case\"", StringComparison.Ordinal) && !html.Contains("data-pin-run=", StringComparison.Ordinal),
                "W5 (E7): the Pin as test case modal is hosted; nothing to pin before a run");
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

        Case("playground.options.new-options-reach-the-request", async () =>
        {
            // The JSON the page posts (collectSpec: each set row by RunSpec's name, a strict tool with strict: true).
            JsonObject Page(string provider, string model, string thinking) => new()
            {
                ["provider"] = provider,
                ["model"] = model,
                ["source"] = "playground",
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = "new options run" }),
                ["tools"] = new JsonArray(new JsonObject
                {
                    ["name"] = "lookup",
                    ["description"] = "Look a word up",
                    ["parameters"] = JsonNode.Parse("""{"type":"object","properties":{"word":{"type":"string"}},"required":["word"],"additionalProperties":false}"""),
                    ["strict"] = true,
                }),
                ["thinking"] = thinking,
                ["parallelToolCalls"] = false,
                ["textVerbosity"] = "low",
                ["reasoningSummary"] = "detailed",
                ["timeoutMs"] = 120_000,
            };
            string[] added = [RunModelOptions.ParallelToolCalls, RunModelOptions.StrictTools, RunModelOptions.TextVerbosity, RunModelOptions.ReasoningSummary, RunModelOptions.TimeoutMs];

            // Over HTTP on a faux thinking model: the run takes each option, History records it, and ?run= reopens it with every control set.
            await using (var app = await WebAgentApp.StartAsync("pg-new-options"))
            {
                var posted = await app.SendAsync(HttpMethod.Post, "/api/runs", Page("faux", "echo-thinking", "high"), origin: app.Origin);
                Check(posted.Status == 200 && posted.Body?["posted"]?.GetValue<bool>() == true, "the run is taken: " + posted.Text);
                var id = posted.Body!["id"]!.GetValue<string>();
                Equal(RunStatus.Completed, (await RunsSseAsync(app, $"/api/runs/{id}/events"))[^1].Data?["status"]?.GetValue<string>(), "completed");
                var options = (await app.Get($"/api/runs/{id}")).Body?["run"]?["options"];
                var applied = options?["applied"]?.AsArray().Select(item => item!.GetValue<string>()).ToList() ?? [];
                foreach (var option in added) Check(applied.Contains(option), option + " is applied: " + options?.ToJsonString());
                Equal(("low", "detailed", 120_000, false), (options?["textVerbosity"]?.GetValue<string>(), options?["reasoningSummary"]?.GetValue<string>(),
                    options?["timeoutMs"]?.GetValue<int>(), options?["parallelToolCalls"]?.GetValue<bool>()), "History records each value");

                var reopened = (await app.Get("/playground?run=" + id)).Text;
                foreach (var option in added.Except([RunModelOptions.StrictTools]))
                    Check(Regex.IsMatch(reopened, $"data-option=\"{option}\" data-set=\"true\" data-conflict=\"\""), option + "'s row is set, not refused: "
                        + Cut(reopened[Math.Max(0, reopened.IndexOf($"data-option=\"{option}\"", StringComparison.Ordinal) - 40)..]));
                Check(Regex.IsMatch(reopened, "id=\"pg-vb-low\" value=\"low\" checked=\"checked\""), "low verbosity is checked");
                Check(Regex.IsMatch(reopened, "<option value=\"detailed\" selected=\"selected\">detailed</option>"), "the detailed summary is selected");
                Check(Regex.IsMatch(reopened, "id=\"pg-timeoutMs\"[^>]*value=\"120000\""), "the timeout");
                Check(Regex.IsMatch(reopened, "id=\"pg-parallel\"(?![^>]*checked)[^>]*data-opt=\"parallelToolCalls\""), "parallel tool calls off");
                Check(reopened.Contains("&quot;strict&quot;:true}\" data-tool-name=\"lookup\"", StringComparison.Ordinal)
                    && Regex.IsMatch(reopened, "<span class=\"ps-chip fs-8\" data-tool-strict title="), "the strict tool keeps its flag, and says so");
                Check(Regex.IsMatch(reopened, "data-config-conflicts hidden"), "nothing is refused");
                Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
            }

            // Over real routing (the fake provider: no request leaves the process): the same JSON reaches an OpenAI Responses reasoning
            // model's request body, and the session id becomes its prompt cache key.
            await using var host = RunsHost.Routing("pg-new-options-openai");
            await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("openai", Planted("sk-proj-")));
            var catalog = (RuntimeModelCatalog)host.Services.GetRequiredService<IModelCatalog>();
            var reasoner = catalog.Models("openai").First(model => model.ThinkingLevels.Count > 1 && catalog.Find("openai", model.Id)!.Durable.Api == "openai-responses");
            var level = reasoner.ThinkingLevels.First(item => item != "off");
            var json = Page("openai", reasoner.Id, level);
            json["sessionId"] = "pg-1a2b3c4d";
            var spec = RunSpec.Parse(json);
            var start = await host.Engine.StartAsync(spec);
            var run = await RunsFinishedAsync(host.Store, start.Id!);
            Equal(RunStatus.Completed, run.Status, "completed: " + run.Error + " " + run.Notes);
            var body = JsonNode.Parse(host.Provider.Requests.Single(request => request.Body?.Contains("new options run", StringComparison.Ordinal) == true).Body!)!;
            Equal(("low", "detailed", false, "pg-1a2b3c4d"), (body["text"]?["verbosity"]?.GetValue<string>(), body["reasoning"]?["summary"]?.GetValue<string>(),
                body["parallel_tool_calls"]?.GetValue<bool>(), body["prompt_cache_key"]?.GetValue<string>()), "verbosity, summary, parallel switch and session id: " + body.ToJsonString());
            Equal(true, body["tools"]?.AsArray().OfType<JsonObject>().Single(tool => tool["name"]?.GetValue<string>() == "lookup")["strict"]?.GetValue<bool>(), "the tool is strict");
            Check(run.Options?["applied"]?.AsArray().Select(item => item!.GetValue<string>()).Contains(RunModelOptions.TimeoutMs) == true, "the timeout is applied (the capture hook's)");

            // Each control is live where the model's API takes it (RunModelOptions, as the run engine plans): the Responses, Azure and Codex
            // APIs for verbosity, summary (while thinking) and session id; any API for the timeout; the parallel switch where the API has one.
            await using var render = CatalogHost("pg-new-options-render", catalog);
            // The config of a model on `api` (of `provider` when given; one that thinks, at its first level, when `think`), or "" when the
            // catalog has none.
            async Task<string> Config(string api, bool think, string? provider = null)
            {
                var found = catalog.Providers.Select(item => item.Id).Where(id => provider is null || id == provider)
                    .SelectMany(id => catalog.Models(id))
                    .FirstOrDefault(model => catalog.Find(model.Provider, model.Id)?.Durable.Api == api && (!think || model.ThinkingLevels.Count > 1));
                if (found is null) return "";
                var caps = host.Engine.Capabilities(found.Provider, found.Id)!;
                var draft = new RunSpec { Provider = found.Provider, Model = found.Id, Thinking = think ? caps.ThinkingLevels[1] : null, Messages = [new RunMessage { Text = "x" }] };
                return await render.RenderAsync("_RequestConfig", new RequestConfigView { Draft = draft, Capabilities = caps, ShowModel = false });
            }
            var responses = await Config("openai-responses", think: true, "openai");
            foreach (var option in new[] { RunModelOptions.TextVerbosity, RunModelOptions.ReasoningSummary, RunModelOptions.SessionId, RunModelOptions.TimeoutMs, RunModelOptions.ParallelToolCalls })
                Check(PlaygroundLive(responses, option), option + " is live on the Responses API");
            Check(!PlaygroundLive(await Config("openai-responses", think: false, "openai"), RunModelOptions.ReasoningSummary), "the summary only while the model thinks");
            var codex = await Config("openai-codex-responses", think: false);
            Check(codex.Length > 0, "the catalog has a Codex model");
            foreach (var other in new[] { codex, await Config("azure-openai-responses", think: false) }.Where(html => html.Length > 0))
                foreach (var option in new[] { RunModelOptions.TextVerbosity, RunModelOptions.SessionId, RunModelOptions.TimeoutMs })
                    Check(PlaygroundLive(other, option), $"{option} is live on " + Regex.Match(other, "data-api=\"([^\"]*)\"").Groups[1].Value);
            var anthropic = await Config("anthropic-messages", think: true, "anthropic");
            foreach (var option in new[] { RunModelOptions.TextVerbosity, RunModelOptions.ReasoningSummary, RunModelOptions.SessionId })
                Check(!PlaygroundLive(anthropic, option), option + " is off on Anthropic");
            Check(PlaygroundLive(anthropic, RunModelOptions.TimeoutMs) && PlaygroundLive(anthropic, RunModelOptions.ParallelToolCalls), "the timeout and the parallel switch are live on Anthropic");
            if (await Config("google-generative-ai", think: false, "google") is { Length: > 0 } google)
                Check(!PlaygroundLive(google, RunModelOptions.ParallelToolCalls) && PlaygroundLive(google, RunModelOptions.TimeoutMs), "Gemini: no parallel switch, a timeout");
            PlaygroundNoInline("_RequestConfig", responses + anthropic);
        }),

        Case("playground.options.a-model-that-rejects-a-new-option-is-refused", async () =>
        {
            // Over HTTP on the faux models: a refused option is named with its reason and nothing runs.
            await using (var app = await WebAgentApp.StartAsync("pg-new-refused"))
            {
                JsonObject Spec(string model, params (string Key, JsonNode Value)[] options)
                {
                    var spec = new JsonObject { ["provider"] = "faux", ["model"] = model, ["messages"] = new JsonArray(new JsonObject { ["text"] = "hi" }) };
                    foreach (var (key, value) in options) spec[key] = value;
                    return spec;
                }
                async Task RefusedHttp(JsonObject spec, string option, string reason)
                {
                    var answer = await app.SendAsync(HttpMethod.Post, "/api/runs", spec, origin: app.Origin);
                    Equal(400, answer.Status, option + ": " + answer.Text);
                    Check(answer.Body?["error"]?["refused"]?.AsArray().Any(item => item?["option"]?.GetValue<string>() == option
                        && item["reason"]?.GetValue<string>().Contains(reason, StringComparison.Ordinal) == true) == true, $"{option} is refused ({reason}): " + answer.Text);
                }
                async Task Invalid(JsonObject spec, string message)
                {
                    var answer = await app.SendAsync(HttpMethod.Post, "/api/runs", spec, origin: app.Origin);
                    Equal(400, answer.Status, message + ": " + answer.Text);
                    Check(answer.Body?["error"]?["message"]?.GetValue<string>().Contains(message, StringComparison.Ordinal) == true, "says " + message + ": " + answer.Text);
                }
                await RefusedHttp(Spec("echo", ("sessionId", "pg-1")), RunModelOptions.SessionId, "sends no HTTP request");
                await RefusedHttp(Spec("echo-thinking", ("reasoningSummary", "concise")), RunModelOptions.ReasoningSummary, "only while the model thinks");
                await Invalid(Spec("echo", ("parallelToolCalls", true)), "Parallel tool calls need at least one tool.");
                await Invalid(Spec("echo", ("timeoutMs", 500)), "The timeout is ");
                await Invalid(Spec("echo", ("textVerbosity", "loud")), "Text verbosity is low, medium, high");
                Equal(0, (await app.Get("/api/runs")).Body?["total"]?.GetValue<int>(), "nothing ran");
            }

            // Over real routing: Anthropic and Gemini refuse what their APIs do not have, before anything is sent, and the Playground's
            // estimate says so before Run; the config keeps the values, marked (the tools row names the strict tool).
            await using var host = RunsHost.Routing("pg-new-refused-routing");
            await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("anthropic", Planted("sk-ant-")));
            var catalog = (RuntimeModelCatalog)host.Services.GetRequiredService<IModelCatalog>();
            var pages = new PlaygroundPages(host.Services);
            var strictTool = new RunTool("lookup", "Look a word up", new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }, Strict: true);
            var sent = host.Provider.Requests.Count;
            var claude = catalog.Models("anthropic").First(model => model.ThinkingLevels.Count > 1 && catalog.Find("anthropic", model.Id)!.Durable.Api == "anthropic-messages");
            var plain = new RunSpec { Provider = "anthropic", Model = claude.Id, Messages = [new RunMessage { Text = "x" }] };
            async Task Refused(RunSpec spec, string option, string reason)
            {
                var error = await ThrowsAsync<RunOptionException>(() => host.Engine.StartAsync(spec), option);
                Check(error.Plan.Refused.Any(item => item.Option == option && item.Reason?.Contains(reason, StringComparison.Ordinal) == true), $"{option} refused ({reason}): " + error.Message);
                var draft = await pages.DraftAsync(spec);
                Check(!draft.Valid && draft.Refused.Any(item => item.Option == option), option + ": the estimate says Run will be refused: " + draft.Message);
            }
            await Refused(plain with { TextVerbosity = "high" }, RunModelOptions.TextVerbosity, "only the OpenAI Responses and Codex APIs");
            await Refused(plain with { Thinking = claude.ThinkingLevels[1], ReasoningSummary = "auto" }, RunModelOptions.ReasoningSummary, "only the OpenAI Responses and Codex APIs");
            await Refused(plain with { SessionId = "pg-1" }, RunModelOptions.SessionId, "only the OpenAI Responses and Codex APIs");
            await Refused(plain with { Tools = [strictTool] }, RunModelOptions.StrictTools, "strict tools");
            if (catalog.Models("google").FirstOrDefault(model => catalog.Find("google", model.Id)!.Durable.Api == "google-generative-ai") is { } gemini)
            {
                var googleSpec = new RunSpec { Provider = "google", Model = gemini.Id, Messages = [new RunMessage { Text = "x" }], Tools = [strictTool with { Strict = null }] };
                await Refused(googleSpec with { ParallelToolCalls = false }, RunModelOptions.ParallelToolCalls, "no parallel tool call switch");
                await Refused(googleSpec with { Tools = [strictTool] }, RunModelOptions.StrictTools, "no strict tools");
            }
            Equal(sent, host.Provider.Requests.Count, "nothing was sent");

            var caps = host.Engine.Capabilities("anthropic", claude.Id)!;
            var view = new RequestConfigView { Draft = plain with { TextVerbosity = "high", SessionId = "pg-1", Tools = [strictTool] }, Capabilities = caps, ShowModel = false };
            Check(view.Conflict(RunModelOptions.TextVerbosity) && view.Conflict(RunModelOptions.SessionId) && view.ToolsConflict && view.Off(RunModelOptions.ReasoningSummary),
                "the view's verdicts");
            await using var render = CatalogHost("pg-new-refused-render", catalog);
            var html = await render.RenderAsync("_RequestConfig", view);
            Check(Regex.IsMatch(html, "data-option=\"textVerbosity\" data-set=\"true\" data-conflict=\"true\"") && Regex.IsMatch(html, "id=\"pg-vb-high\" value=\"high\" checked=\"checked\"(?![^>]*disabled)"),
                "the verbosity is kept, marked and can be cleared");
            Check(Regex.IsMatch(html, "data-option=\"sessionId\" data-set=\"true\" data-conflict=\"true\"") && Regex.IsMatch(html, "id=\"pg-session\"[^>]*value=\"pg-1\"(?![^>]*disabled)"),
                "the session id is kept, marked and can be cleared");
            var tools = Regex.Match(html, "data-option=\"tools\" data-set=\"true\" data-conflict=\"true\">.*?data-option-note>([^<]*)</div>", RegexOptions.Singleline);
            Check(tools.Success && tools.Groups[1].Value.Contains("1 strict tool:", StringComparison.Ordinal), "the tools row names the strict tool: " + Cut(tools.Value));
            Check(Regex.IsMatch(html, "data-config-conflicts(?![^>]*hidden)"), "the banner says Run will refuse the request");
            PlaygroundNoInline("_RequestConfig", html);
        }),

        Case("playground.options.carried-options-reach-the-request", async () =>
        {
            // W5 L4: the JSON the page posts for the options the run engine carries (collectSpec: cacheRetention by its checked radio,
            // maxRetries and maxRetryDelayMs as numbers, thinkingBudgets as { level: tokens } of the levels filled in, azureApiVersion as text).
            JsonObject Page(string provider, string model, string text) => new()
            {
                ["provider"] = provider,
                ["model"] = model,
                ["source"] = "playground",
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = text }),
                ["maxRetries"] = 2,
                ["maxRetryDelayMs"] = 5000,
            };
            string[] faux = [RunModelOptions.CacheRetention, RunModelOptions.MaxRetries, RunModelOptions.MaxRetryDelayMs, RunModelOptions.ThinkingBudgets];

            // Over HTTP on a faux thinking model (it takes each, and ignores it): the run applies each, History records it, and ?run= reopens
            // it with every control set and nothing refused.
            await using (var app = await WebAgentApp.StartAsync("pg-carried"))
            {
                var json = Page("faux", "echo-thinking", "carried options run");
                json["thinking"] = "high";
                json["cacheRetention"] = "long";
                json["thinkingBudgets"] = new JsonObject { ["low"] = 2048, ["high"] = 8192 };
                var posted = await app.SendAsync(HttpMethod.Post, "/api/runs", json, origin: app.Origin);
                Check(posted.Status == 200 && posted.Body?["posted"]?.GetValue<bool>() == true, "the run is taken: " + posted.Text);
                var id = posted.Body!["id"]!.GetValue<string>();
                Equal(RunStatus.Completed, (await RunsSseAsync(app, $"/api/runs/{id}/events"))[^1].Data?["status"]?.GetValue<string>(), "completed");
                var options = (await app.Get($"/api/runs/{id}")).Body?["run"]?["options"];
                var applied = options?["applied"]?.AsArray().Select(item => item!.GetValue<string>()).ToList() ?? [];
                foreach (var option in faux) Check(applied.Contains(option), option + " is applied: " + options?.ToJsonString());
                Equal(("long", 2, 5000, 2048, 8192), (options?["cacheRetention"]?.GetValue<string>(), options?["maxRetries"]?.GetValue<int>(), options?["maxRetryDelayMs"]?.GetValue<int>(),
                    options?["thinkingBudgets"]?["low"]?.GetValue<int>(), options?["thinkingBudgets"]?["high"]?.GetValue<int>()), "History records each value");

                var reopened = (await app.Get("/playground?run=" + id)).Text;
                foreach (var option in faux)
                    Check(Regex.IsMatch(reopened, $"data-option=\"{option}\" data-set=\"true\" data-conflict=\"\""), option + "'s row is set, not refused: "
                        + Cut(reopened[Math.Max(0, reopened.IndexOf($"data-option=\"{option}\"", StringComparison.Ordinal) - 40)..]));
                Check(Regex.IsMatch(reopened, "id=\"pg-cr-long\" value=\"long\" checked=\"checked\"") && !Regex.IsMatch(reopened, "id=\"pg-cr-short\" value=\"short\" checked"), "long retention is checked");
                Check(Regex.IsMatch(reopened, "id=\"pg-maxRetries\"[^>]*value=\"2\"") && Regex.IsMatch(reopened, "id=\"pg-maxRetryDelayMs\"[^>]*value=\"5000\""), "the retries and the delay");
                Check(Regex.IsMatch(reopened, "id=\"pg-budget-low\"[^>]*value=\"2048\"") && Regex.IsMatch(reopened, "id=\"pg-budget-high\"[^>]*value=\"8192\"")
                    && Regex.IsMatch(reopened, "id=\"pg-budget-medium\"[^>]*value=\"\""), "the budgets set, and a level left empty");
                Check(Regex.IsMatch(reopened, "data-config-conflicts hidden"), "nothing is refused");
                Check(reopened.Contains("<span data-modified-count>5</span>", StringComparison.Ordinal),
                    "the modified count counts them (thinking and the four)");

                // A model that does not take one refuses it with its reason, and nothing runs: an Azure API version off Azure, a retry delay
                // without retries.
                async Task RefusedHttp(JsonObject spec, string option, string reason)
                {
                    var answer = await app.SendAsync(HttpMethod.Post, "/api/runs", spec, origin: app.Origin);
                    Equal(400, answer.Status, option + ": " + answer.Text);
                    Check(answer.Body?["error"]?["refused"]?.AsArray().Any(item => item?["option"]?.GetValue<string>() == option
                        && item["reason"]?.GetValue<string>().Contains(reason, StringComparison.Ordinal) == true) == true, $"{option} is refused ({reason}): " + answer.Text);
                }
                var azureOff = Page("faux", "echo", "refused");
                azureOff["azureApiVersion"] = "2025-04-01-preview";
                await RefusedHttp(azureOff, RunModelOptions.AzureApiVersion, "only Azure OpenAI takes it");
                var delayOnly = Page("faux", "echo", "refused");
                delayOnly.Remove("maxRetries");
                await RefusedHttp(delayOnly, RunModelOptions.MaxRetryDelayMs, "needs max retries of 1 or more");
                Equal(1, (await app.Get("/api/runs")).Body?["total"]?.GetValue<int>(), "only the first ran");

                // Model & auth's answer carries the model's catalog sampling values for Sampling's read-only row.
                var model = await app.Get("/api/playground/model?provider=faux&model=echo");
                Check(model.Body?["samplingParams"] is JsonObject sampling && sampling["applied"]?.GetValue<bool>() == false && sampling["values"] is null
                    && sampling["chip"]?.GetValue<string>() == "not applied by this api" && sampling["note"]?.GetValue<string>() == "echo has none.", "samplingParams: " + model.Text);
                Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
            }

            // Over real routing (the fake provider: no request leaves the process): the same JSON reaches an Anthropic budget-thinking model's
            // body as its thinking budget, and an Azure model's request as its api-version.
            await using var host = RunsHost.Routing("pg-carried-routing");
            await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("anthropic", Planted("sk-ant-")));
            await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("azure", Planted("az-")) { Settings = new Dictionary<string, string> { ["AZURE_OPENAI_RESOURCE_NAME"] = "pg-carried" } });
            var catalog = (RuntimeModelCatalog)host.Services.GetRequiredService<IModelCatalog>();
            async Task<FakeRequest> SentAsync(JsonObject json, string text)
            {
                var start = await host.Engine.StartAsync(RunSpec.Parse(json));
                Check(start.Posted, "posted: " + start.Refusal?.Message);
                var run = await RunsFinishedAsync(host.Store, start.Id!);
                Equal(RunStatus.Completed, run.Status, "completed: " + run.Error + " " + run.Notes);
                return host.Provider.Requests.Single(request => request.Body?.Contains(text, StringComparison.Ordinal) == true);
            }
            var budgetModel = catalog.Models("anthropic").FirstOrDefault(model =>
                host.Engine.Capabilities("anthropic", model.Id)?.Find(RunModelOptions.ThinkingBudgets) is { Supported: false, WithThinking: true });
            Check(budgetModel is not null, "the catalog has an Anthropic model that thinks by budget");
            var level = budgetModel!.ThinkingLevels.First(item => item != "off");
            var budgets = new JsonObject { ["minimal"] = 1536, ["low"] = 2048, ["medium"] = 3072, ["high"] = 4096 };
            var anthropicJson = Page("anthropic", budgetModel.Id, "carried budget run");
            anthropicJson["thinking"] = level;
            anthropicJson["thinkingBudgets"] = budgets;
            var anthropicBody = JsonNode.Parse((await SentAsync(anthropicJson, "carried budget run")).Body!)!;
            Equal(budgets[level is "xhigh" or "max" ? "high" : level]!.GetValue<int>(), anthropicBody["thinking"]?["budget_tokens"]?.GetValue<int>(), "the level's budget: " + anthropicBody["thinking"]?.ToJsonString());
            var azureModel = ModelOf(catalog, "azure", "azure-openai-responses", "gpt-4.1");
            var azureJson = Page("azure", azureModel, "carried azure run");
            azureJson["azureApiVersion"] = "2025-04-01-preview";
            Equal("?api-version=2025-04-01-preview", (await SentAsync(azureJson, "carried azure run")).Url.Query, "the run's API version");

            // Each row is live where the model's API takes it (RunModelOptions, as the run engine plans), and a set option the model does not
            // take is kept, marked, and its Provider-specific pane shown.
            await using var render = CatalogHost("pg-carried-render", catalog);
            async Task<string> Config(RunSpec draft) =>
                await render.RenderAsync("_RequestConfig", new RequestConfigView { Draft = draft, Capabilities = host.Engine.Capabilities(draft.Provider, draft.Model), ShowModel = false });
            RunSpec Draft(string provider, string model, string? thinking = null) => new() { Provider = provider, Model = model, Thinking = thinking, Messages = [new RunMessage { Text = "x" }] };
            var thinkingHtml = await Config(Draft("anthropic", budgetModel.Id, level));
            Check(PlaygroundLive(thinkingHtml, RunModelOptions.ThinkingBudgets), "budgets are live while the Anthropic model thinks");
            Check(!PlaygroundLive(await Config(Draft("anthropic", budgetModel.Id)), RunModelOptions.ThinkingBudgets), "and off while it does not");
            foreach (var option in new[] { RunModelOptions.CacheRetention, RunModelOptions.MaxRetries, RunModelOptions.MaxRetryDelayMs })
                Check(PlaygroundLive(thinkingHtml, option), option + " is live on Anthropic");
            Check(!PlaygroundLive(thinkingHtml, RunModelOptions.AzureApiVersion), "no Azure API version on Anthropic");
            var openai = catalog.Models("openai").First(model => model.ThinkingLevels.Count > 1 && catalog.Find("openai", model.Id)!.Durable.Api == "openai-responses");
            var responsesHtml = await Config(Draft("openai", openai.Id, openai.ThinkingLevels[1]));
            Check(!PlaygroundLive(responsesHtml, RunModelOptions.ThinkingBudgets) && PlaygroundLive(responsesHtml, RunModelOptions.CacheRetention)
                && PlaygroundLive(responsesHtml, RunModelOptions.MaxRetries), "OpenAI Responses: cache retention and retries, no budget (it takes an effort)");
            var azureHtml = await Config(Draft("azure", azureModel));
            Check(PlaygroundLive(azureHtml, RunModelOptions.AzureApiVersion) && Regex.IsMatch(azureHtml, "data-provider-pane=\"azure\">"), "Azure: the API version is live, in the pane shown first");
            var view = new RequestConfigView
            {
                Draft = Draft("openai", openai.Id) with { AzureApiVersion = "v1", ThinkingBudgets = new Dictionary<string, int> { ["low"] = 2048 } },
                Capabilities = host.Engine.Capabilities("openai", openai.Id),
                ShowModel = false,
            };
            Check(view.Conflict(RunModelOptions.AzureApiVersion) && view.Conflict(RunModelOptions.ThinkingBudgets) && view.ProviderPane == "azure", "the view's verdicts");
            var marked = await render.RenderAsync("_RequestConfig", view);
            Check(Regex.IsMatch(marked, "data-option=\"azureApiVersion\" data-set=\"true\" data-conflict=\"true\"") && Regex.IsMatch(marked, "id=\"pg-azureApiVersion\"[^>]*value=\"v1\"(?![^>]*disabled)")
                && Regex.IsMatch(marked, "data-provider-pane=\"azure\">"), "the Azure API version is kept, marked, can be cleared, and its pane shows");
            Check(Regex.IsMatch(marked, "data-option=\"thinkingBudgets\" data-set=\"true\" data-conflict=\"true\"") && Regex.IsMatch(marked, "id=\"pg-budget-low\"[^>]*value=\"2048\"(?![^>]*disabled)"),
                "the budget is kept, marked and can be cleared");
            Check(Regex.IsMatch(marked, "data-config-conflicts(?![^>]*hidden)"), "the banner says Run will refuse the request");
            PlaygroundNoInline("_RequestConfig", thinkingHtml + marked);
        }),

        Case("playground.sampling.the-catalog-values-show-read-only", async () =>
        {
            // Sampling's samplingParams row (the mockup's): the model's catalog values and by-level values, and whether its API applies them.
            // A model whose catalog sets none says so; nothing on the row is a control the page sends.
            var wire = (PiSharp.Durable.Chord.JsonObjectValue)PiSharp.Durable.Chord.JsonValue.Parse(
                """{"id":"local-qwen","samplingParams":{"top_p":0.8,"top_k":20},"samplingParamsByThinkingLevel":{"high":{"temperature":0.6}}}""");
            PiSharp.Durable.DurableModel Model(string api) => new() { Id = "local-qwen", Name = "Local Qwen", Api = api, Provider = "lmstudio", Wire = wire };
            var completions = RequestSamplingView.Of(Model("openai-completions"));
            Equal(("{\"top_p\":0.8,\"top_k\":20}", "{\"high\":{\"temperature\":0.6}}", true, "applied by this api"),
                (completions.ValuesText, completions.ByLevelText, completions.Applied, completions.Chip), "Chat Completions merges them");
            foreach (var api in new[] { "openai-responses", "azure-openai-responses" }) Check(RequestSamplingView.Of(Model(api)).Applied, api + " merges them");
            var anthropic = RequestSamplingView.Of(Model("anthropic-messages"));
            Check(!anthropic.Applied && anthropic.Chip == "not applied by this api" && anthropic.Note.Contains("never sent", StringComparison.Ordinal), "Anthropic does not: " + anthropic.Note);
            var none = RequestSamplingView.Of(new PiSharp.Durable.DurableModel { Id = "plain", Name = "Plain", Api = "openai-completions", Provider = "x" });
            Check(!none.Any && none.Note == "plain has none." && none.ValuesText.Length == 0, "a model with none says so");
            Equal("Choose a model PiSharp knows.", RequestSamplingView.Of(null).Note, "no model");

            await using var runs = RunsHost.Faux("pg-sampling");
            await using var render = RenderHost.Create("pg-sampling-render", (_, _) => { });
            var draft = new RunSpec { Provider = "faux", Model = "echo", Messages = [new RunMessage { Text = "x" }] };
            var html = await render.RenderAsync("_RequestConfig", new RequestConfigView { Draft = draft, Capabilities = runs.Engine.Capabilities("faux", "echo"), ShowModel = false, Sampling = completions });
            var row = Regex.Match(html, "<div class=\"param-row\" data-sampling-params data-applied=\"true\">(.*?)data-sampling-note>([^<]*)</div>", RegexOptions.Singleline);
            Check(row.Success && row.Groups[1].Value.Contains("data-sampling-chip>applied by this api</span>", StringComparison.Ordinal)
                && row.Groups[1].Value.Contains("{&quot;top_p&quot;:0.8,&quot;top_k&quot;:20}", StringComparison.Ordinal)
                && row.Groups[1].Value.Contains("data-sampling-by-level-text>{&quot;high&quot;:{&quot;temperature&quot;:0.6}}</span>", StringComparison.Ordinal), "the row shows the values: " + Cut(row.Value));
            Check(!Regex.IsMatch(row.Value, "data-opt=|data-option="), "nothing on the row is sent");
            var plain = await render.RenderAsync("_RequestConfig", new RequestConfigView { Draft = draft, Capabilities = runs.Engine.Capabilities("faux", "echo"), ShowModel = false });
            Check(!plain.Contains("data-sampling-params", StringComparison.Ordinal), "a page that does not set it leaves the row out");
            PlaygroundNoInline("_RequestConfig", html);
        }),
    ];

    /// <summary>The page's controls reach the request: request-spec.js sends every option that has a row (and a tool's strict flag), reads
    /// every control kind the page renders, and request-config.js counts what the server counts and offers the levels the run engine takes.
    /// Static checks of the modules the page loads, so a control whose value is never sent fails here.</summary>
    private static async Task PlaygroundWiredAsync(WebAgentApp app, string html)
    {
        var spec = (await app.Get("/js/request-spec.js")).Text;
        var config = (await app.Get("/js/request-config.js")).Text;
        var tools = (await app.Get("/js/request-tools.js")).Text;
        var sent = PlaygroundJsList(spec, "SENT_OPTIONS");
        foreach (var option in RunModelOptions.All.Except([RunModelOptions.Images, RunModelOptions.Tools, RunModelOptions.StrictTools]).Concat(PlaygroundCarried))
            Check(sent.Contains(option), "request-spec.js sends " + option + ": " + string.Join(",", sent));
        Check(spec.Contains("for (const option of SENT_OPTIONS)", StringComparison.Ordinal), "collectSpec reads SENT_OPTIONS");
        // applySpec restores every option it sends (Presets, Compare columns, History's "Open in Playground" in place).
        var apply = Regex.Match(spec, @"export function applyOptions\(config, spec\) \{(.*?)\n\}", RegexOptions.Singleline).Groups[1].Value;
        foreach (var option in sent)
            Check(apply.Contains($"'{option}'", StringComparison.Ordinal), "applyOptions sets " + option);
        Check(spec.Contains("if (tool.strict === true) sent.strict = true;", StringComparison.Ordinal), "collectTools sends a tool's strict flag");
        Check(tools.Contains("if (fields.strict?.checked) tool.strict = true;", StringComparison.Ordinal), "the tool editor keeps the Strict switch on the tool");
        // Each control the generic reader handles has a kind it knows (a kind it does not know reads as null: never sent).
        var kinds = Regex.Match(spec, @"const KINDS = \{(.*?)\n\};", RegexOptions.Singleline).Groups[1].Value;
        string[] ownReaders = ["thinking", "responseFormat", "toolChoice", "serviceTier", "headers", "providerOptions"];
        var controls = Regex.Matches(html, "data-opt=\"([A-Za-z]+)\" data-kind=\"([a-z]+)\"").Select(match => (Option: match.Groups[1].Value, Kind: match.Groups[2].Value)).Distinct().ToList();
        foreach (var option in sent.Except(ownReaders))
            Check(controls.Any(control => control.Option == option), "a control for " + option);
        foreach (var (option, kind) in controls.Where(control => !ownReaders.Contains(control.Option)))
            Check(Regex.IsMatch(kinds, $"\\n  {kind}: "), $"request-spec.js reads {option}'s kind {kind}");
        Equal(string.Join(",", RequestConfigView.Counted), string.Join(",", PlaygroundJsList(config, "COUNTED")), "request-config.js counts the server's options");
        Equal(string.Join(",", RunSpec.ThinkingLevels), string.Join(",", PlaygroundJsList(config, "SEND_LEVELS")), "the levels the page sends are RunSpec's, max included");
    }

    /// <summary>The strings of the JS array constant <paramref name="name"/> in <paramref name="script"/>.</summary>
    private static List<string> PlaygroundJsList(string script, string name)
    {
        var list = Regex.Match(script, $@"const {name} = \[([^\]]*)\]");
        Check(list.Success, "the module has " + name);
        return [.. Regex.Matches(list.Groups[1].Value, "'([^']+)'").Select(match => match.Groups[1].Value)];
    }

    /// <summary>The class of the row of <paramref name="option"/> in rendered <c>_RequestConfig</c> markup.</summary>
    private static string PlaygroundRow(string html, string option)
    {
        var row = Regex.Match(html, $"<div class=\"([^\"]*)\" data-option=\"{option}\"");
        Check(row.Success, "a row for " + option);
        return row.Groups[1].Value;
    }

    /// <summary>Whether <paramref name="option"/>'s row and every control of it are enabled in rendered <c>_RequestConfig</c> markup.</summary>
    private static bool PlaygroundLive(string html, string option)
    {
        var controls = Regex.Matches(html, $"<(?:input|select)\\b[^>]*data-opt=\"{option}\"[^>]*>").Select(match => match.Value).ToList();
        Check(controls.Count > 0, "a control for " + option);
        var rowOn = !PlaygroundRow(html, option).Contains("param-disabled", StringComparison.Ordinal);
        var controlsOn = controls.All(control => !Regex.IsMatch(control, "\\sdisabled"));
        Check(rowOn == controlsOn, $"{option}'s row and controls agree ({rowOn}, {controlsOn})");
        return rowOn && controlsOn;
    }

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
