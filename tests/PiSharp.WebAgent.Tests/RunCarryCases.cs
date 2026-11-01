// Decision 0011 (run W4, fix 2, R0): the Playground options the run engine did not carry. Each plan takes or refuses cache retention,
// retries and the retry delay, thinking budgets and the Azure API version, and refuses with its reason a per-run base URL, the other Azure
// options and the Bedrock options (the account's credential goes only where the account says; a signed request cannot change). The cache
// edit is checked on the request body itself (breakpoints placed or rewritten, never inside a tool's schema), then end to end over a fake
// provider (no request leaves the process): a capped Anthropic run keeps its prompt caching, long and none reach the wire, a 429 is sent
// again before any output while a server delay over the run's limit is not waited for, the Azure api-version query is the run's, and a Codex
// run with raw payloads on goes over SSE, where the capture hook records it.
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PiSharp.Durable;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Routing;
using PiSharp.WebAgent.Runs;
using ChordJson = PiSharp.Durable.Chord.JsonValue;
using ChordObject = PiSharp.Durable.Chord.JsonObjectValue;

internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> RunCarryCases() =>
    [
        Case("runs.carry.each-option-is-taken-or-refused-with-its-reason", () =>
        {
            var claude = CarryModel("anthropic", "claude-test", "anthropic-messages");
            var gpt = CarryModel("openai", "gpt-test", "openai-responses", reasoning: false);
            var o3 = CarryModel("openai", "o-test", "openai-responses");
            var azure = CarryModel("azure", "gpt-azure-test", "azure-openai-responses", reasoning: false);
            var codex = CarryModel("openai-codex", "gpt-codex-test", "openai-codex-responses");
            var bedrock = CarryModel("amazon-bedrock", "anthropic.claude-sonnet-4-5-20250929-v1:0", "bedrock-converse-stream");

            // The new options are listed after the Playground's own rows, never among them.
            Check(!RunModelOptions.All.Intersect(RunModelOptions.Carried).Any(), "the carried options are a list of their own");
            var caps = RunModelOptions.Capabilities(claude, null);
            Check(caps.Find(RunModelOptions.CacheRetention) is { Supported: true } && caps.Find(RunModelOptions.MaxRetries) is { Supported: true } &&
                caps.Find(RunModelOptions.ThinkingBudgets) is { Supported: false, WithThinking: true } && caps.Find(RunModelOptions.BaseUrl) is { Supported: false, Reason: not null },
                "capabilities list the carried options: " + string.Join(", ", caps.Options.Where(entry => RunModelOptions.Carried.Contains(entry.Option))));

            // Cache retention: the edit of each API's shape, or a refusal that names why.
            foreach (var retention in RunSpec.CacheRetentions)
            {
                var plan = CarryPlan(claude, new RunSpec { CacheRetention = retention });
                Check(plan.Allowed && plan.Cache == new RunCacheEdit(retention, "anthropic") && plan.PatchPath == "/messages", $"Anthropic takes {retention}: " + plan.RefusalMessage);
            }
            var noLong = claude with { Wire = (ChordObject)ChordJson.Parse("""{"compat":{"supportsLongCacheRetention":false}}""") };
            CarryRefused(CarryPlan(noLong, new RunSpec { CacheRetention = "long" }), RunModelOptions.CacheRetention, "hour-long");
            Equal(new RunCacheEdit("long", "responses"), CarryPlan(gpt, new RunSpec { CacheRetention = "long" }).Cache, "OpenAI's own API keeps 24 hours");
            CarryRefused(CarryPlan(CarryModel("xai", "grok-test", "openai-responses"), new RunSpec { CacheRetention = "long" }), RunModelOptions.CacheRetention, "only OpenAI");
            CarryRefused(CarryPlan(gpt, new RunSpec { CacheRetention = "none", SessionId = "s-1" }), RunModelOptions.CacheRetention, "session id");
            CarryRefused(CarryPlan(azure, new RunSpec { CacheRetention = "long" }), RunModelOptions.CacheRetention, "Azure");
            CarryRefused(CarryPlan(codex, new RunSpec { CacheRetention = "long" }), RunModelOptions.CacheRetention, "Codex");
            Check(CarryPlan(codex, new RunSpec { CacheRetention = "none" }).Allowed, "Codex takes none");
            var explicitMode = CarryModel("gateway", "gpt-gateway", "openai-responses", reasoning: false) with
            {
                Wire = (ChordObject)ChordJson.Parse("""{"compat":{"supportsExplicitPromptCacheMode":true}}"""),
            };
            Equal(new RunCacheEdit("long", "responses-explicit"), CarryPlan(explicitMode, new RunSpec { CacheRetention = "long" }).Cache, "explicit cache mode");
            var gemini = CarryModel("google", "gemini-2.5-flash", "google-generative-ai");
            var geminiShort = CarryPlan(gemini, new RunSpec { CacheRetention = "short" });
            Check(geminiShort.Allowed && geminiShort.Cache is null, "Gemini takes short, its own, with nothing to edit");
            CarryRefused(CarryPlan(gemini, new RunSpec { CacheRetention = "none" }), RunModelOptions.CacheRetention, "Gemini");
            CarryRefused(CarryPlan(bedrock, new RunSpec { CacheRetention = "short" }), RunModelOptions.CacheRetention, "signed");
            var openRouterClaude = CarryModel("openrouter", "anthropic/claude-test", "openai-completions", reasoning: false);
            Equal(new RunCacheEdit("short", "completions-anthropic"), CarryPlan(openRouterClaude, new RunSpec { CacheRetention = "short" }).Cache,
                "an Anthropic model behind Chat Completions takes breakpoints");

            // A capped send keeps the main route's caching: the summary route sends no breakpoints, the edit puts them back.
            var capped = CarryPlan(claude, new RunSpec { MaxTokens = 500 });
            Equal(new RunCacheEdit("short", "anthropic"), capped.CappedCache, "a capped Anthropic run gets its breakpoints back");
            Check(!capped.Notes.Any(note => note.Contains("without prompt caching", StringComparison.Ordinal)), "and no note says caching is off: " + string.Join(" ", capped.Notes));
            var send = RunModelOptions.Send(capped, 500);
            Check(send.Cache == capped.CappedCache && send.Path == "/messages", "the capped send carries the edit to the model request");
            Check(RunModelOptions.Send(CarryPlan(claude, new RunSpec()), null).Cache is null, "an uncapped send leaves the route's own caching");
            Check(RunModelOptions.Send(CarryPlan(claude, new RunSpec()), 3000).Cache is not null, "the Spend Guard's cap gets the breakpoints back too");
            Check(CarryPlan(claude, new RunSpec { MaxTokens = 500, CacheRetention = "none" }).CappedCache is null, "the run's own retention wins");

            // Retries: taken where the hook knows the model request; the delay needs retries.
            var retried = CarryPlan(gpt, new RunSpec { MaxRetries = 2, MaxRetryDelayMs = 5000 });
            Check(retried.Allowed && retried.Notes.Any(note => note.Contains("up to 2 times", StringComparison.Ordinal)), "retries are taken and said: " + string.Join(" ", retried.Notes));
            CarryRefused(CarryPlan(gpt, new RunSpec { MaxRetryDelayMs = 5000 }), RunModelOptions.MaxRetryDelayMs, "max retries");
            CarryRefused(CarryPlan(bedrock, new RunSpec { MaxRetries = 1 }), RunModelOptions.MaxRetries, "retries only");

            // Thinking budgets: Anthropic budget thinking and Gemini 2.5; never adaptive, Gemini 3, OpenAI's efforts or Bedrock.
            var budget = CarryPlan(claude, new RunSpec { Thinking = "high", ThinkingBudgets = new Dictionary<string, int> { ["high"] = 5000 } });
            Equal((5000, 5000), (budget.ThinkingBudget ?? 0, budget.Patch?["thinking"]?["budget_tokens"]?.GetValue<int>() ?? 0), "the level's budget is sent");
            var budgetCapped = CarryPlan(claude, new RunSpec { Thinking = "high", MaxTokens = 4000, ThinkingBudgets = new Dictionary<string, int> { ["high"] = 2000 } });
            Equal((4000, 2000), (budgetCapped.Patch!["max_tokens"]!.GetValue<int>(), budgetCapped.Patch["thinking"]!["budget_tokens"]!.GetValue<int>()),
                "inside max tokens, the run's budget is the one sent");
            Equal(1976, RunModelOptions.Send(budgetCapped, 3000).Patch!["thinking"]!["budget_tokens"]!.GetValue<int>(), "a smaller guard cap still leaves 1,024 to answer");
            CarryRefused(CarryPlan(claude, new RunSpec { Thinking = "high", MaxTokens = 4000, ThinkingBudgets = new Dictionary<string, int> { ["high"] = 3500 } }),
                RunModelOptions.ThinkingBudgets, "max tokens");
            CarryRefused(CarryPlan(claude, new RunSpec { Thinking = "high", ThinkingBudgets = new Dictionary<string, int> { ["high"] = 500 } }), RunModelOptions.ThinkingBudgets, "1,024");
            CarryRefused(CarryPlan(claude, new RunSpec { ThinkingBudgets = new Dictionary<string, int> { ["high"] = 5000 } }), RunModelOptions.ThinkingBudgets, "only while the model thinks");
            var otherLevel = CarryPlan(claude, new RunSpec { Thinking = "high", ThinkingBudgets = new Dictionary<string, int> { ["low"] = 3000 } });
            Check(otherLevel.Allowed && otherLevel.ThinkingBudget is null && otherLevel.Notes.Any(note => note.Contains("no high budget", StringComparison.Ordinal)),
                "a budget for another level is said, not sent: " + string.Join(" ", otherLevel.Notes));
            var adaptive = claude with { Wire = (ChordObject)ChordJson.Parse("""{"compat":{"forceAdaptiveThinking":true}}""") };
            CarryRefused(CarryPlan(adaptive, new RunSpec { Thinking = "high", ThinkingBudgets = new Dictionary<string, int> { ["high"] = 5000 } }), RunModelOptions.ThinkingBudgets, "adaptively");
            var geminiBudget = CarryPlan(gemini, new RunSpec { Thinking = "medium", ThinkingBudgets = new Dictionary<string, int> { ["medium"] = 4096 } });
            Equal(4096, geminiBudget.Patch?["generationConfig"]?["thinkingConfig"]?["thinkingBudget"]?.GetValue<int>(), "Gemini 2.5 takes thinkingConfig.thinkingBudget");
            CarryRefused(CarryPlan(CarryModel("google", "gemini-3-pro-preview", "google-generative-ai"),
                new RunSpec { Thinking = "high", ThinkingBudgets = new Dictionary<string, int> { ["high"] = 4096 } }), RunModelOptions.ThinkingBudgets, "Gemini 3");
            CarryRefused(CarryPlan(o3, new RunSpec { Thinking = "high", ThinkingBudgets = new Dictionary<string, int> { ["high"] = 4096 } }), RunModelOptions.ThinkingBudgets, "reasoning effort");
            CarryRefused(CarryPlan(bedrock, new RunSpec { Thinking = "high", ThinkingBudgets = new Dictionary<string, int> { ["high"] = 4096 } }), RunModelOptions.ThinkingBudgets, "signed");

            // Never per run: a host for the account's credential, Azure's deployment (pricing), Bedrock (signed). The API version is taken.
            CarryRefused(CarryPlan(gpt, new RunSpec { BaseUrl = "https://elsewhere.example.test/v1" }), RunModelOptions.BaseUrl, "account's own host");
            CarryRefused(CarryPlan(azure, new RunSpec { AzureBaseUrl = "https://other.openai.azure.com/openai/v1" }), RunModelOptions.AzureBaseUrl, "key goes to");
            CarryRefused(CarryPlan(azure, new RunSpec { AzureResourceName = "other" }), RunModelOptions.AzureResourceName, "key goes to");
            CarryRefused(CarryPlan(azure, new RunSpec { AzureDeploymentName = "gpt-5-deployment" }), RunModelOptions.AzureDeploymentName, "Spend Guard");
            var version = CarryPlan(azure, new RunSpec { AzureApiVersion = "2025-04-01-preview" });
            Check(version.Allowed && version.Applied.Contains(RunModelOptions.AzureApiVersion) && version.PatchPath == "/responses", "Azure takes the API version: " + version.RefusalMessage);
            CarryRefused(CarryPlan(gpt, new RunSpec { AzureApiVersion = "2025-04-01-preview" }), RunModelOptions.AzureApiVersion, "only Azure");
            CarryRefused(CarryPlan(bedrock, new RunSpec { Bedrock = new JsonObject { ["region"] = "eu-west-1" } }), RunModelOptions.Bedrock, "signed");
            CarryRefused(CarryPlan(gpt, new RunSpec { Bedrock = new JsonObject { ["region"] = "eu-west-1" } }), RunModelOptions.Bedrock, "only Amazon Bedrock");

            // What no model takes is refused before any plan.
            var spec = new RunSpec { Provider = "openai", Model = "gpt-test", Messages = [new RunMessage { Text = "hi" }] };
            Throws<ArgumentException>(() => (spec with { CacheRetention = "forever" }).Normalize(), "an unknown retention");
            Throws<ArgumentException>(() => (spec with { MaxRetries = RunSpec.MaxRetryCount + 1 }).Normalize(), "too many retries");
            Throws<ArgumentException>(() => (spec with { MaxRetryDelayMs = -1 }).Normalize(), "a negative delay");
            Throws<ArgumentException>(() => (spec with { ThinkingBudgets = new Dictionary<string, int> { ["xhigh"] = 1000 } }).Normalize(), "a level budgets do not name");
            Throws<ArgumentException>(() => (spec with { AzureApiVersion = "v1&key=x" }).Normalize(), "an API version that is not a name");
            Throws<ArgumentException>(() => (spec with { Bedrock = new JsonObject { ["accessKeyId"] = "x" } }).Normalize(), "a Bedrock option it does not list");
            var parsed = RunSpec.Parse(JsonNode.Parse("""{"provider":"openai","model":"gpt-test","messages":[{"text":"hi"}],"cacheRetention":"long","maxRetries":2,"maxRetryDelayMs":0,"thinkingBudgets":{"high":4096},"azureApiVersion":"v1"}"""));
            Check(parsed is { CacheRetention: "long", MaxRetries: 2, MaxRetryDelayMs: 0, AzureApiVersion: "v1" } && parsed.ThinkingBudgets?["high"] == 4096, "a page posts them by name");
            return Task.CompletedTask;
        }),

        Case("runs.carry.the-cache-edit-places-or-rewrites-breakpoints-only-where-they-go", () =>
        {
            JsonObject Anthropic() => JsonNode.Parse("""
                {"system":"Be brief.","messages":[{"role":"assistant","content":"earlier"},{"role":"user","content":"hello"}],
                 "tools":[{"name":"a","input_schema":{"type":"object","properties":{"cache_control":{"type":"string"}}}},{"name":"b","input_schema":{"type":"object"}}]}
                """)!.AsObject();
            static int Marks(JsonNode body) => CountMarks(body);

            var placed = Anthropic();
            RunModelOptions.ApplyCache(placed, new RunCacheEdit("short", "anthropic"));
            Equal(3, Marks(placed), "the last tool, the system prompt and the last user message: " + placed.ToJsonString());
            Equal("ephemeral", placed["system"]![0]!["cache_control"]!["type"]!.GetValue<string>(), "the system prompt became a block with a breakpoint");
            Check(placed["tools"]![1]!["cache_control"] is not null && placed["tools"]![0]!["cache_control"] is null, "only the last tool");
            Equal("ephemeral", placed["messages"]![1]!["content"]![0]!["cache_control"]!["type"]!.GetValue<string>(), "the last user message");
            Check(placed["tools"]![0]!["input_schema"]!["properties"]!["cache_control"]!["type"]!.GetValue<string>() == "string", "a tool's schema is never touched");

            var noTool = Anthropic();
            RunModelOptions.ApplyCache(noTool, new RunCacheEdit("short", "anthropic", ToolMarker: false));
            Check(noTool["tools"]![1]!["cache_control"] is null && Marks(noTool) == 2, "no tool breakpoint where the provider takes none");

            var longer = (JsonObject)placed.DeepClone();
            RunModelOptions.ApplyCache(longer, new RunCacheEdit("long", "anthropic"));
            Equal(3, Marks(longer), "long rewrites the breakpoints there are");
            Check(longer.ToJsonString().Split("\"ttl\":\"1h\"").Length - 1 == 3, "each with an hour: " + longer.ToJsonString());

            var none = (JsonObject)longer.DeepClone();
            RunModelOptions.ApplyCache(none, new RunCacheEdit("none", "anthropic"));
            Equal(0, Marks(none), "none takes them all out");
            Check(none["tools"]![0]!["input_schema"]!["properties"]!["cache_control"] is not null, "but not a schema property of that name");

            var responses = JsonNode.Parse("""{"model":"gpt","prompt_cache_key":"s-1","prompt_cache_retention":"24h"}""")!.AsObject();
            RunModelOptions.ApplyCache(responses, new RunCacheEdit("short", "responses"));
            Check(responses["prompt_cache_retention"] is null && responses["prompt_cache_key"] is not null, "short keeps the key, drops the 24 hours");
            RunModelOptions.ApplyCache(responses, new RunCacheEdit("long", "responses"));
            Equal("24h", responses["prompt_cache_retention"]?.GetValue<string>(), "long keeps 24 hours");
            RunModelOptions.ApplyCache(responses, new RunCacheEdit("none", "responses"));
            Check(responses["prompt_cache_retention"] is null && responses["prompt_cache_key"] is null, "none sends no key: " + responses.ToJsonString());
            var explicitBody = new JsonObject { ["model"] = "gpt" };
            RunModelOptions.ApplyCache(explicitBody, new RunCacheEdit("none", "responses-explicit"));
            Equal("explicit", explicitBody["prompt_cache_options"]?["mode"]?.GetValue<string>(), "explicit cache mode: none");
            RunModelOptions.ApplyCache(explicitBody, new RunCacheEdit("long", "responses-explicit"));
            Check(explicitBody["prompt_cache_options"]?["ttl"]?.GetValue<string>() == "30m" && explicitBody["prompt_cache_options"]?["mode"] is null, "explicit cache mode: long");

            var completions = JsonNode.Parse("""
                {"messages":[{"role":"system","content":"Be brief."},{"role":"user","content":"hello"},{"role":"assistant","content":""}],
                 "tools":[{"type":"function","function":{"name":"a"}}]}
                """)!.AsObject();
            RunModelOptions.ApplyCache(completions, new RunCacheEdit("short", "completions-anthropic"));
            Equal(3, Marks(completions), "the first system message, the last tool, the last message with text: " + completions.ToJsonString());
            Check(completions["messages"]![1]!["content"]![0]!["cache_control"] is not null && completions["messages"]![2]!["content"]!.GetValue<string>() == "",
                "an empty answer takes no breakpoint");
            return Task.CompletedTask;
        }),

        Case("runs.carry.anthropic-prompt-caching-reaches-the-wire-and-a-capped-run-keeps-it", async () =>
        {
            await using var host = RunsHost.Routing("runs-carry-anthropic");
            await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("anthropic", Planted("sk-ant-api03-")));
            var model = ModelOf(host.Services.GetRequiredService<IModelCatalog>(), "anthropic", "anthropic-messages", "claude-haiku-4-5");
            RunSpec Spec(string text) => new() { Provider = "anthropic", Model = model, SystemPrompt = "Be brief.", Messages = [new RunMessage { Text = text }] };
            async Task<JsonNode> SentAsync(RunSpec spec, string text)
            {
                var start = await host.Engine.StartAsync(spec);
                Check(start.Posted, "posted: " + start.Refusal?.Message);
                var run = await RunsFinishedAsync(host.Store, start.Id!);
                Equal(RunStatus.Completed, run.Status, "completed: " + run.Error + " " + run.Notes);
                return JsonNode.Parse(host.Provider.Requests.Single(request => request.Body?.Contains(text, StringComparison.Ordinal) == true).Body!)!;
            }

            var plain = await SentAsync(Spec("default caching"), "default caching");
            var routeMarks = CountMarks(plain);
            Check(routeMarks > 0, "PiSharp's main route caches by default: " + plain.ToJsonString());
            var capped = await SentAsync(Spec("capped caching") with { MaxTokens = 500 }, "capped caching");
            Equal(500, capped["max_tokens"]?.GetValue<int>(), "the cap is sent");
            Equal(routeMarks, CountMarks(capped), "and the capped route keeps the main route's breakpoints: " + capped.ToJsonString());
            var longer = await SentAsync(Spec("long caching") with { CacheRetention = "long" }, "long caching");
            Check(CountMarks(longer) == routeMarks && longer.ToJsonString().Split("\"ttl\":\"1h\"").Length - 1 == routeMarks, "long sends an hour: " + longer.ToJsonString());
            var none = await SentAsync(Spec("no caching") with { CacheRetention = "none" }, "no caching");
            Check(!none.ToJsonString().Contains("cache_control", StringComparison.Ordinal), "none sends no breakpoint: " + none.ToJsonString());
            var cappedLong = await SentAsync(Spec("capped long caching") with { MaxTokens = 500, CacheRetention = "long" }, "capped long caching");
            Check(CountMarks(cappedLong) == routeMarks && cappedLong.ToJsonString().Contains("\"ttl\":\"1h\"", StringComparison.Ordinal), "a capped run takes long too");

            await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("openai", Planted("sk-proj-")));
            var gpt = ModelOf(host.Services.GetRequiredService<IModelCatalog>(), "openai", "openai-responses", "gpt-4.1");
            var responses = await SentAsync(new RunSpec { Provider = "openai", Model = gpt, CacheRetention = "long", Messages = [new RunMessage { Text = "openai long" }] }, "openai long");
            Equal("24h", responses["prompt_cache_retention"]?.GetValue<string>(), "OpenAI keeps the prompt cache 24 hours");
        }),

        Case("runs.carry.retries-send-the-model-request-again-before-any-output", async () =>
        {
            await using var host = RunsHost.Routing("runs-carry-retries");
            await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("openai", Planted("sk-proj-")));
            var model = ModelOf(host.Services.GetRequiredService<IModelCatalog>(), "openai", "openai-responses", "gpt-4.1");
            await host.Services.GetRequiredService<ISettingsStore>().SaveAsync(new RunHistorySettings(StoreRawPayloads: true));
            var answers = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
            host.Provider.Answer = request =>
            {
                foreach (var (marker, status, header, value) in new[]
                {
                    ("once busy", 429, "retry-after-ms", "5"),
                    ("never retried", 429, "retry-after-ms", "5"),
                    ("waits too long", 429, "retry-after", "120"),
                    ("told not to", 503, "x-should-retry", "false"),
                })
                {
                    if (request.Body?.Contains(marker, StringComparison.Ordinal) != true) continue;
                    var seen = answers.AddOrUpdate(marker, 1, (_, count) => count + 1);
                    if (marker == "once busy" && seen > 1) return null;
                    var answer = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("""{"error":{"message":"busy"}}""", Encoding.UTF8, "application/json") };
                    answer.Headers.TryAddWithoutValidation(header, value);
                    return answer;
                }
                return null;
            };
            RunSpec Spec(string text) => new() { Provider = "openai", Model = model, Messages = [new RunMessage { Text = text }] };
            async Task<RunRecord> RunAsync(RunSpec spec)
            {
                var start = await host.Engine.StartAsync(spec);
                Check(start.Posted, "posted: " + start.Refusal?.Message);
                return await RunsFinishedAsync(host.Store, start.Id!);
            }
            int Sent(string text) => host.Provider.Requests.Count(request => request.Body?.Contains(text, StringComparison.Ordinal) == true);

            var retried = await RunAsync(Spec("once busy") with { MaxRetries = 2 });
            Equal(RunStatus.Completed, retried.Status, "a 429 is sent again and answered: " + retried.Error);
            Equal("Hello world", retried.OutputText, "the answer of the second request");
            Equal(2, Sent("once busy"), "two model requests");
            Check(retried.Notes?.Contains("again 1 time", StringComparison.Ordinal) == true, "History says so: " + retried.Notes);
            var raw = host.Store.Raw(retried.Id);
            Check(raw.Count(item => item.Kind == "request") == 2 && raw.Any(item => item.Kind == "timing" && item.Data.Contains("\"mark\":\"retry\"", StringComparison.Ordinal)),
                "both attempts and the retry are recorded: " + string.Join(",", raw.Select(item => item.Kind)));

            var unretried = await RunAsync(Spec("never retried"));
            Check(unretried.Status == RunStatus.Error && Sent("never retried") == 1, "without retries a 429 ends the run: " + unretried.Status);

            var patient = await RunAsync(Spec("waits too long") with { MaxRetries = 3, MaxRetryDelayMs = 1000 });
            Check(patient.Status == RunStatus.Error && Sent("waits too long") == 1, "a server delay over the run's limit is not waited for: " + patient.Status + " " + Sent("waits too long"));
            Check(host.Store.Raw(patient.Id).Any(item => item.Data.Contains("retry-refused", StringComparison.Ordinal)), "and the inspector says why");

            var told = await RunAsync(Spec("told not to") with { MaxRetries = 3 });
            Check(told.Status == RunStatus.Error && Sent("told not to") == 1, "x-should-retry: false is not retried");

            // The hook's reading of the provider's answer.
            using var asked = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            asked.Headers.TryAddWithoutValidation("retry-after-ms", "250");
            Equal((long?)250, RunCaptureHandler.ServerDelay(asked), "retry-after-ms");
            using var seconds = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            seconds.Headers.TryAddWithoutValidation("retry-after", "2");
            Equal((long?)2000, RunCaptureHandler.ServerDelay(seconds), "retry-after in seconds");
            using var conflict = new HttpResponseMessage(HttpStatusCode.Conflict);
            using var bad = new HttpResponseMessage(HttpStatusCode.BadRequest);
            using var badRetry = new HttpResponseMessage(HttpStatusCode.BadRequest);
            badRetry.Headers.TryAddWithoutValidation("x-should-retry", "true");
            Check(RunCaptureHandler.Retryable(conflict) && !RunCaptureHandler.Retryable(bad) && RunCaptureHandler.Retryable(badRetry), "409 retries, 400 not unless the provider says so");
        }),

        Case("runs.carry.the-azure-api-version-is-the-runs", async () =>
        {
            await using var host = RunsHost.Routing("runs-carry-azure");
            await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("azure", Planted("az-")) { Settings = new Dictionary<string, string> { ["AZURE_OPENAI_RESOURCE_NAME"] = "carry-test" } });
            var model = ModelOf(host.Services.GetRequiredService<IModelCatalog>(), "azure", "azure-openai-responses", "gpt-4.1");
            async Task<FakeRequest> SentAsync(RunSpec spec, string text)
            {
                var start = await host.Engine.StartAsync(spec);
                Check(start.Posted, "posted: " + start.Refusal?.Message);
                var run = await RunsFinishedAsync(host.Store, start.Id!);
                Equal(RunStatus.Completed, run.Status, "completed: " + run.Error + " " + run.Notes);
                return host.Provider.Requests.Single(request => request.Body?.Contains(text, StringComparison.Ordinal) == true);
            }
            RunSpec Spec(string text) => new() { Provider = "azure", Model = model, Messages = [new RunMessage { Text = text }] };

            var plain = await SentAsync(Spec("azure default"), "azure default");
            Check(plain.Url.Host == "carry-test.openai.azure.com" && plain.Url.Query.Contains("api-version=", StringComparison.Ordinal), "the account's endpoint: " + plain.Url);
            var versioned = await SentAsync(Spec("azure versioned") with { AzureApiVersion = "2025-04-01-preview" }, "azure versioned");
            Equal("?api-version=2025-04-01-preview", versioned.Url.Query, "the run's API version, once");
            Equal(plain.Url.GetLeftPart(UriPartial.Path), versioned.Url.GetLeftPart(UriPartial.Path), "to the same endpoint");
            var count = host.Provider.Requests.Count;
            await ThrowsAsync<RunOptionException>(() => host.Engine.StartAsync(Spec("azure elsewhere") with { AzureBaseUrl = "https://elsewhere.example.test/openai/v1" }),
                "a per-run Azure base URL is refused");
            Equal(count, host.Provider.Requests.Count, "nothing was sent for it");
            Equal("https://a.example.test/v1/responses?x=1&api-version=v2",
                RunCaptureHandler.WithApiVersion(new Uri("https://a.example.test/v1/responses?api-version=v1&x=1"), "v2").AbsoluteUri, "the query keeps its other fields");
        }),

        Case("runs.carry.raw-payloads-send-codex-over-sse-where-they-are-recorded", async () =>
        {
            // CodexTransport auto (WebSocket first): with raw payloads on, a run with nothing to patch still goes over SSE.
            await using var host = RunsHost.Routing("runs-carry-codex", codexTransport: null);
            var vault = host.Services.GetRequiredService<CredentialVault>();
            await vault.SaveSignInAsync("openai-codex", new PiSharp.AI.Authentication.OAuth.OAuthCredentialSnapshot(CodexJwt("acct-carry"), Planted("rt-"),
                DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(), new Dictionary<string, string> { ["accountId"] = "acct-carry" }),
                "acct-carry", new Dictionary<string, string>(), "ChatGPT acct-carry", null, default);
            var model = ModelOf(host.Services.GetRequiredService<IModelCatalog>(), "openai-codex", "openai-codex-responses");
            await host.Services.GetRequiredService<ISettingsStore>().SaveAsync(new RunHistorySettings(StoreRawPayloads: true));
            var start = await host.Engine.StartAsync(new RunSpec { Provider = "openai-codex", Model = model, Messages = [new RunMessage { Text = "codex raw run" }] });
            Check(start.Posted, "posted: " + start.Refusal?.Message);
            var run = await RunsFinishedAsync(host.Store, start.Id!);
            Equal(RunStatus.Completed, run.Status, "completed: " + run.Error + " " + run.Notes);
            Check(host.Provider.Requests.All(request => request.Method == "POST"),
                "over SSE, never the WebSocket the hook cannot record: " + string.Join(", ", host.Provider.Requests.Select(request => request.Key)));
            Equal(RunRawState.Stored, run.RawState, "raw payloads stored");
            var raw = host.Store.Raw(run.Id);
            Check(raw.Any(item => item.Kind == "request" && item.Data.Contains("codex raw run", StringComparison.Ordinal)) && raw.Any(item => item.Kind == "sse"),
                "the request and its SSE lines are recorded: " + string.Join(",", raw.Select(item => item.Kind)));
            Check(run.Notes?.Contains("not captured", StringComparison.Ordinal) != true, "and nothing says it was not: " + run.Notes);
        }),
    ];

    private static DurableModel CarryModel(string provider, string id, string api, bool reasoning = true) =>
        new() { Id = id, Name = id, Api = api, Provider = provider, Reasoning = reasoning, MaxTokens = 64_000, ContextWindow = 200_000 };

    private static RunRequestPlan CarryPlan(DurableModel model, RunSpec spec) =>
        RunModelOptions.Plan((spec with { Provider = model.Provider, Model = model.Id, Messages = [new RunMessage { Text = "hi" }] }).Normalize(), model, null, 0);

    private static void CarryRefused(RunRequestPlan plan, string option, string reason)
    {
        var refused = plan.Refused.SingleOrDefault(item => item.Option == option)
            ?? throw new InvalidOperationException($"{option} was not refused on {plan.Model.Api}: applied {string.Join(",", plan.Applied)}");
        Check(refused.Reason?.Contains(reason, StringComparison.Ordinal) == true && plan.Patch is null,
            $"{option} on {plan.Model.Provider}/{plan.Model.Id} is refused for \"{reason}\": " + refused.Reason);
    }

    /// <summary>How many tools, system blocks, messages and message blocks of a request body carry a cache breakpoint.</summary>
    private static int CountMarks(JsonNode body)
    {
        static bool Marked(JsonNode? node) => node is JsonObject value && value.ContainsKey("cache_control");
        var count = (body["tools"] as JsonArray ?? []).Count(Marked) + (body["system"] as JsonArray ?? []).Count(Marked);
        foreach (var message in body["messages"] as JsonArray ?? [])
        {
            if (Marked(message)) count++;
            if (message is JsonObject entry && entry["content"] is JsonArray content) count += content.Count(Marked);
        }
        return count;
    }
}
