// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/ai/src/utils/retry.ts, utils/overflow.ts, api/openai-responses.ts,
// api/google-shared.ts, api/transform-messages.ts, api/anthropic-messages.ts, api/openai-completions.ts, api/openai-responses-shared.ts,
// utils/transcript.ts and utils/identity.ts.
using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.AI.Protocols.GoogleGenerativeAI;
using PiSharp.AI.Protocols.MistralConversations;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.AI.Providers;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Configuration;
using PiSharp.Contracts;

// Pi 1.5.0 sync (ai-providers). Authored expectations derived by reading Pi v1.5.0 source and test text (packages/ai/test/retry.test.ts,
// overflow.test.ts, google-thinking-level-map.test.ts, google-shared-convert-tools.test.ts, transform-messages-cross-model-thinking.test.ts,
// anthropic-error-tool-result-images.test.ts, transcript-tool-changes.test.ts, identity.test.ts and openai-responses-mid-convo-effort.test.ts). Nothing here executes upstream code or is an upstream
// capture; every HTTP peer is an in-process fake.
internal static partial class Program
{
    private static readonly (string Id, Func<Task> Run)[] Sync150Cases =
    [
        ("sync150.retry-gateway-service-unavailable-node-fetch-and-codex-disconnect", Sync150RetryTransient),
        ("sync150.retry-standalone-status-codes", Sync150RetryStatusCodes),
        ("sync150.retry-chatgpt-workspace-and-zhipu-1308-limits-not-retried", Sync150RetryPermanentLimits),
        ("sync150.overflow-http-413-and-litellm-context-window", Sync150Overflow),
        ("sync150.responses-chatgpt-workspace-usage-limit-hint", Sync150WorkspaceLimitHint),
        ("sync150.google-gemini-4-and-later-thinking-level", Sync150GeminiThinkingLevel),
        ("sync150.google-const-rewritten-as-enum-in-parameters-json-schema", Sync150GoogleConstAsEnum),
        ("sync150.cross-model-thinking-dropped-on-every-api", Sync150CrossModelThinking),
        ("sync150.completions-thinking-kept-only-from-the-target-model", Sync150AlternatingThinking),
        ("sync150.anthropic-system-update-held-past-a-dropped-assistant", Sync150AnthropicSystemHold),
        ("sync150.anthropic-managed-effort-top-level-low", Sync150AnthropicLowEffort),
        ("sync150.anthropic-error-tool-result-images-follow-the-results", Sync150AnthropicErrorImages),
        ("sync150.identity-user-agent-merge-and-attribution-headers", Sync150IdentityFormats),
        ("sync150.identity-headers-on-chat-requests", Sync150IdentityOnRequests),
        ("sync150.completions-kimi-tool-additions-fold-to-the-last-removal", Sync150KimiToolFold),
        ("sync150.responses-mid-conversation-effort-configuration-updates", Sync150ResponsesMidEffort),
    ];

    // ---------------------------------------------------------------- retry.ts and overflow.ts

    private static Task Sync150RetryTransient()
    {
        foreach (var message in new[]
        {
            // #10965 and Bedrock's ServiceUnavailableException as formatBedrockError prints it.
            "The service is temporarily unavailable. Please retry later.",
            "Service unavailable: The service is temporarily unavailable.",
            "service is unavailable",
            // #10996: node-fetch failures of the Google ADC token refresh, with an empty or a system reason.
            "request to https://oauth2.googleapis.com/token failed, reason:",
            "request to https://oauth2.googleapis.com/token failed, reason: connect ECONNREFUSED 127.0.0.1:9",
            // #10920: gateway upstream failures relayed as response.failed.
            "unknown: Unable to forward upstream stream",
            "upstream_error: Upstream request failed",
            "upstream_stream_read_error: Upstream response stream was interrupted",
            "Upstream HTTP/2 stream failed",
            "Upstream HTTP2 stream failed",
            // #11057: a Codex CLI mid-stream disconnect relayed by a proxy.
            "stream disconnected before completion: stream closed before response.completed",
            // JavaScript's \S: U+0085 is not JavaScript whitespace, so it can sit inside the request target.
            "request to https://a\u0085b failed, reason:",
        })
            Check(AgentRetryPolicy.IsRetryableError(StopReason.Error, message), "Not retried: " + message);
        foreach (var message in new[]
        {
            // U+FEFF is JavaScript whitespace, so \S+ cannot cross it.
            "request to https://a\uFEFFb failed, reason:",
            "request to failed, reason:",
            "service will be unavailable",
        })
            Check(!AgentRetryPolicy.IsRetryableError(StopReason.Error, message), "Unexpectedly retried: " + message);
        return Task.CompletedTask;
    }

    private static Task Sync150RetryStatusCodes()
    {
        foreach (var message in new[] { "429", "Error 500", "HTTP 502: bad gateway", "(503)", "status=504", "520 origin error", "upstream_524_timeout", "é503" })
            Check(AgentRetryPolicy.IsRetryableError(StopReason.Error, message), "Not retried: " + message);
        foreach (var message in new[]
        {
            // #11057: a status code inside a request id or a longer number is not a status code.
            """401 {"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"},"request_id":"req_011CU503xyz"}""",
            "400 image exceeds 5 MB maximum: 5316784 bytes > 5242880 bytes",
            "id A503", "5290 items", "4290", "x500y"
        })
            Check(!AgentRetryPolicy.IsRetryableError(StopReason.Error, message), "Unexpectedly retried: " + message);
        return Task.CompletedTask;
    }

    private static Task Sync150RetryPermanentLimits()
    {
        foreach (var message in new[]
        {
            """OpenAI API error (429): {"code":"subscription_sharing_workplace_individual_usage_limit_exceeded","message":"Usage limit reached."}""",
            // #11537: Zhipu / Z.ai plan usage limit, sent as HTTP 429.
            """429 {"error":{"code":"1308","message":"Usage limit reached for 5 hour."}}""",
            """429 {"code": 1308, "message":"Usage limit reached"}""",
            "429 {\"CODE\"\t:\t\"1308\"}",
        })
            Check(!AgentRetryPolicy.IsRetryableError(StopReason.Error, message), "Unexpectedly retried: " + message);
        // Another code that starts with 1308 is not the plan limit; the 429 keeps it transient.
        Check(AgentRetryPolicy.IsRetryableError(StopReason.Error, """429 {"code":13080,"message":"slow down"}"""), "code 13080 not retried");
        Check(AgentRetryPolicy.IsRetryableError(StopReason.Error, """429 {"code":"1309"}"""), "code 1309 not retried");
        return Task.CompletedTask;
    }

    private static AssistantMessage Sync150Failed(string text, string provider = "github-copilot", StopReason reason = StopReason.Error) =>
        new("openai-responses", provider, "fixture", 1, [], TokenUsage.Zero, reason,
            JsonFields.Empty.Set("errorMessage", JsonData.Parse(JsonSerializer.Serialize(text))));

    private static Task Sync150Overflow()
    {
        foreach (var message in new[]
        {
            """github-copilot API error (413): {"message":"failed to parse request","code":""}""",
            "custom-provider API error (413): Payload Too Large",
            """413 {"error":{"message":"failed to parse request","code":""}}""",
            "413",
        })
        {
            Check(SessionRecoveryClassifier.IsContextOverflow(Sync150Failed(message), 1_000_000), "413 missed with a window: " + message);
            Check(SessionRecoveryClassifier.IsContextOverflow(Sync150Failed(message)), "413 missed: " + message);
        }
        foreach (var message in new[]
        {
            """github-copilot API error (400): {"message":"failed to parse request","code":""}""",
            "custom-provider API error (429): Too many requests",
            """github-copilot API error (400): {"message":"failed to parse request: invalid value 413"}""",
            "custom-provider API error (400): upstream mentioned OpenAI API error (413): invalid input",
            "custom-provider API error (4130): invalid input",
            "4130 failed to parse request",
        })
            Check(!SessionRecoveryClassifier.IsContextOverflow(Sync150Failed(message)), "Unexpected overflow: " + message);
        var stopped = """OpenAI API error (413): {"message":"failed to parse request"}""";
        Check(!SessionRecoveryClassifier.IsContextOverflow(Sync150Failed(stopped, reason: StopReason.Aborted)), "aborted 413 is overflow");
        Check(!SessionRecoveryClassifier.IsContextOverflow(Sync150Failed(stopped, reason: StopReason.Stop)), "stopped 413 is overflow");
        // #11492: LiteLLM's pre-call ContextWindowExceededError.
        Check(SessionRecoveryClassifier.IsContextOverflow(Sync150Failed(
            "400: {\"message\":\"litellm.ContextWindowExceededError: litellm.BadRequestError: litellm._pre_call_checks: Context Window exceeded for given call. No models have context window large enough for this call.\\nModel=Qwen3.8-Flash-Next, Max Input Tokens=262144, Got=265262\"}",
            "litellm"), 262144), "LiteLLM overflow missed");
        // A bodyless 400 stays Cerebras-only.
        Check(SessionRecoveryClassifier.IsContextOverflow(Sync150Failed("400 status code (no body)", "cerebras"), 131072), "Cerebras bodyless 400 missed");
        Check(!SessionRecoveryClassifier.IsContextOverflow(Sync150Failed("400 status code (no body)", "opencode-go"), 1_000_000), "bodyless 400 overflow elsewhere");
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- openai-responses.ts error hint

    private static async Task Sync150WorkspaceLimitHint()
    {
        const string body = """{"error":{"code":"subscription_sharing_workplace_individual_usage_limit_exceeded","message":"Usage limit reached."}}""";
        var text = await ResponsesError(() => new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        Equal("""OpenAI API error (429): {"code":"subscription_sharing_workplace_individual_usage_limit_exceeded","message":"Usage limit reached."}""" +
            "\nYou've reached your ChatGPT workspace's usage limit. Ask your workspace admin to increase it at admin.openai.com \u2192 Workspace \u2192 Usage limits.", text);
        Check(!AgentRetryPolicy.IsRetryableError(StopReason.Error, text), "workspace limit retried");
        // The shared subscription limit keeps its own hint, which wins when both codes appear.
        var shared = await ResponsesError(() => new HttpResponseMessage((HttpStatusCode)429)
        { Content = new StringContent("""{"error":{"code":"subscription_sharing_usage_limit_exceeded","message":"subscription_sharing_workplace_individual_usage_limit_exceeded"}}""", Encoding.UTF8, "application/json") });
        Check(shared.EndsWith("\nCheck your ChatGPT usage: https://chatgpt.com/settings/usage", StringComparison.Ordinal) && !shared.Contains("workspace admin", StringComparison.Ordinal),
            "shared limit hint: " + shared);
    }

    // ---------------------------------------------------------------- google-shared.ts

    private static JsonData Sync150GoogleRow(string provider, string id)
    {
        var row = JsonNode.Parse(CatalogRow(provider, "gemini-3-flash-preview").Value.GetRawText())!.AsObject();
        row["id"] = id; row["name"] = id; row["thinkingLevelMap"] = new JsonObject();
        return JsonData.Parse(row.ToJsonString());
    }

    private static async Task<JsonNode> Sync150GoogleBody(string id, bool vertex, string? level, ImmutableArray<TranscriptEntry> messages, JsonData? row = null)
    {
        string? sent = null;
        using var handler = new Handler(async request =>
        {
            sent = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        });
        if (!vertex)
        {
            var model = new ModelDescriptor(id, "google-generative-ai", "google"); row ??= Sync150GoogleRow("google", id);
            using var provider = NativeProviderFactory.CreateCatalogGoogle(model, Key, row, new GoogleGenerativeAIOptions(row) { MaxTokens = 1000 }, handler);
            await new ChatClient(provider.Transport).CompleteAsync(new(model, messages, 9) { ThinkingLevel = level }).WaitAsync(Deadline);
        }
        else
        {
            var model = new ModelDescriptor(id, "google-vertex", "google-vertex"); row ??= Sync150GoogleRow("google-vertex", id);
            using var provider = NativeProviderFactory.CreateGoogleVertexRoute(model, row, _ => ValueTask.FromResult(new GoogleVertexRequestAuth(
                PiSharp.AI.Protocols.GoogleVertex.GoogleVertexEndpoints.ApiKey(id, null), "vertex-key", true, "-", "-")), 1000, false, handler);
            await new ChatClient(provider.Transport).CompleteAsync(new(model, messages, 9) { ThinkingLevel = level }).WaitAsync(Deadline);
        }
        return JsonNode.Parse(sent ?? throw new InvalidOperationException(id + ": no request"))!;
    }

    // #10890: Gemini 4 and later use thinkingLevel on both Google APIs, like Gemini 3.
    private static async Task Sync150GeminiThinkingLevel()
    {
        foreach (var vertex in new[] { false, true })
        {
            foreach (var id in new[] { "gemini-4-argon", "gemini-9-argon", "gemini-10-argon" })
                Equal("""{"includeThoughts":true,"thinkingLevel":"MEDIUM"}""",
                    (await Sync150GoogleBody(id, vertex, "medium", [Ask]))["generationConfig"]!["thinkingConfig"]!.ToJsonString());
            // Gemini 2 keeps a thinking budget.
            var budget = (await Sync150GoogleBody("gemini-2-argon", vertex, "medium", [Ask]))["generationConfig"]!["thinkingConfig"]!.AsObject();
            Check(budget.ContainsKey("thinkingBudget") && !budget.ContainsKey("thinkingLevel"), "gemini-2 thinking config: " + budget.ToJsonString());
        }
    }

    // #11308: Gemini ignores const in parametersJsonSchema, so string and number consts are sent as one-value enums; literal data and
    // names under name maps are kept.
    private static async Task Sync150GoogleConstAsEnum()
    {
        const string parameters = """{"type":"object","properties":{"origin":{"type":"string","const":"zq-src-7"},"mode":{"anyOf":[{"type":"string","const":"a"},{"type":"string","const":"b"}]},"level":{"oneOf":[{"const":1},{"const":2.5}]},"flag":{"type":"boolean","const":true},"none":{"const":null},"list":{"type":"array","items":{"const":"x"},"prefixItems":[{"const":2}],"contains":{"const":4}},"const":{"type":"string"},"literal":{"type":"object","default":{"const":"literal-data"},"examples":[{"const":"e"}],"enum":[{"const":"n"}],"x-custom":{"const":"c"}},"kept":{"const":"k","enum":["k","j"]},"cond":{"allOf":[{"if":{"const":"x"},"then":{"const":"y"},"else":{"not":{"const":"z"}}}]}},"additionalProperties":{"const":"extra"},"propertyNames":{"const":"name"},"$defs":{"__proto__":{"const":"p"},"const":{"const":"q"},"properties":{"const":"r"}},"dependencies":{"list":["other"]},"required":["origin"]}""";
        const string expected = """{"type":"object","properties":{"origin":{"type":"string","enum":["zq-src-7"]},"mode":{"anyOf":[{"type":"string","enum":["a"]},{"type":"string","enum":["b"]}]},"level":{"oneOf":[{"enum":[1]},{"enum":[2.5]}]},"flag":{"type":"boolean","const":true},"none":{"const":null},"list":{"type":"array","items":{"enum":["x"]},"prefixItems":[{"enum":[2]}],"contains":{"enum":[4]}},"const":{"type":"string"},"literal":{"type":"object","default":{"const":"literal-data"},"examples":[{"const":"e"}],"enum":[{"const":"n"}],"x-custom":{"const":"c"}},"kept":{"const":"k","enum":["k","j"]},"cond":{"allOf":[{"if":{"enum":["x"]},"then":{"enum":["y"]},"else":{"not":{"enum":["z"]}}}]}},"additionalProperties":{"enum":["extra"]},"propertyNames":{"enum":["name"]},"$defs":{"__proto__":{"enum":["p"]},"const":{"enum":["q"]},"properties":{"enum":["r"]}},"dependencies":{"list":["other"]},"required":["origin"]}""";
        ImmutableArray<TranscriptEntry> messages =
        [
            Entry("""{"role":"system","content":"SYS","toolsAdded":[{"name":"pick","description":"Pick one","parameters":""" + parameters + """}],"timestamp":0}"""),
            Entry("""{"role":"user","content":"go","timestamp":1}"""),
        ];
        foreach (var vertex in new[] { false, true })
        {
            var id = "gemini-3-flash-preview";
            var body = await Sync150GoogleBody(id, vertex, null, messages, CatalogRow(vertex ? "google-vertex" : "google", id));
            BodyEqual(expected, body["tools"]![0]!["functionDeclarations"]![0]!["parametersJsonSchema"]!.ToJsonString());
            // The transcript's own definition is not rewritten.
            Check(messages[0].WireBody.Value.GetProperty("toolsAdded")[0].GetProperty("parameters").GetRawText() == parameters, "tool definition mutated");
        }
    }

    // ---------------------------------------------------------------- transform-messages.ts: thinking from another model

    private static string Sync150Assistant(string api, string provider, string model, string content, string stopReason = "stop", int timestamp = 2) =>
        "{\"role\":\"assistant\",\"content\":" + content + ",\"api\":\"" + api + "\",\"provider\":\"" + provider + "\",\"model\":\"" + model + "\",\"usage\":" +
        """{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"totalTokens":0,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0}}""" +
        ",\"stopReason\":\"" + stopReason + "\",\"timestamp\":" + timestamp + "}";

    private static ImmutableArray<TranscriptEntry> Sync150ForeignThinking(string api, string provider, string model, string signature) =>
    [
        Entry("""{"role":"user","content":"one","timestamp":1}"""),
        Entry(Sync150Assistant(api, provider, model, """[{"type":"thinking","thinking":"FOREIGN-PLAN","thinkingSignature":""" +
            JsonSerializer.Serialize(signature) + """},{"type":"text","text":"visible-a"}]""")),
        Entry("""{"role":"user","content":"two","timestamp":3}"""),
    ];

    // #10995: another model's thinking is dropped, never sent as text (signed, unsigned or redacted alike).
    private static async Task Sync150CrossModelThinking()
    {
        void Dropped(string label, string body) =>
            Check(!body.Contains("FOREIGN-PLAN", StringComparison.Ordinal) && body.Contains("visible-a", StringComparison.Ordinal), label + ": " + body);
        string? captured = null;
        Handler Capture() => new(async request => { captured = await request.Content!.ReadAsStringAsync(); return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{}") }; });

        var anthropic = new AnthropicMessagesRequestProjector(new(MaximumTokens: 1000));
        Dropped("anthropic", anthropic.Project(new(Haiku, Sync150ForeignThinking("anthropic-messages", "anthropic", "claude-other", "c2ln"), 4)).ToString());
        Dropped("anthropic other provider", anthropic.Project(new(Haiku, Sync150ForeignThinking("openai-completions", "openrouter", "glm", ""), 4)).ToString());
        // The same model's signed thinking still replays.
        var same = anthropic.Project(new(Haiku, [Entry("""{"role":"user","content":"one","timestamp":1}"""),
            Entry(Sync150Assistant("anthropic-messages", "anthropic", "fixture-haiku", """[{"type":"thinking","thinking":"SAME-PLAN","thinkingSignature":"c2ln"},{"type":"text","text":"visible-a"}]""")),
            Entry("""{"role":"user","content":"two","timestamp":3}""")], 4)).ToString();
        Check(same.Contains("""{"type":"thinking","thinking":"SAME-PLAN","signature":"c2ln"}""", StringComparison.Ordinal), "same-model thinking: " + same);

        var completions = new ModelDescriptor("fixture", "openai-completions", "openrouter");
        using (var handler = Capture())
        using (var provider = NativeProviderFactory.CreateCompletions(completions, new("https://openrouter.ai/api/v1/chat/completions"), Key, new(Reasoning: false),
            new(MaxTokensField: "max_tokens", SupportsStore: false), handler))
        {
            await new ChatClient(provider).CompleteAsync(new(completions, Sync150ForeignThinking("openai-completions", "openrouter", "other", "reasoning_content"), 4)).WaitAsync(Deadline);
            Dropped("completions", captured!);
        }

        captured = null;
        var responses = new ModelDescriptor("gpt-fixture", "openai-responses", "openai");
        using (var handler = Capture())
        using (var provider = NativeProviderFactory.CreateResponses(responses, new("https://api.openai.com/v1/responses"), Key, new(false), null, handler))
        {
            await new ChatClient(provider).CompleteAsync(new(responses, Sync150ForeignThinking("openai-responses", "openai", "gpt-other",
                """{"type":"reasoning","id":"rs_1","summary":[{"type":"summary_text","text":"FOREIGN-PLAN"}]}"""), 4)).WaitAsync(Deadline);
            Dropped("responses", captured!);
        }

        captured = null;
        using (var handler = Capture())
        using (var client = new HttpClient(handler))
        {
            var transport = new GoogleGenerativeAIHttpTransport(client, Gemini, new GoogleGenerativeAIOptions(GoogleMetadata(), Key));
            await new ChatClient(transport).CompleteAsync(new(Gemini, Sync150ForeignThinking("google-generative-ai", "google", "gemini-2.5-pro", "c2ln"), 4)).WaitAsync(Deadline);
            Dropped("google", captured!);
        }

        captured = null;
        var mistral = new ModelDescriptor("mistral-fixture", "mistral-conversations", "mistral");
        var endpoint = new Uri("https://api.mistral.ai/");
        using (var handler = Capture())
        using (var provider = NativeProviderFactory.CreateMistral(mistral, endpoint, Key, new MistralTextOptions(endpoint, true, new(0, 0, 0, 0), "offline-fixture"), handler))
        {
            await new ChatClient(provider).CompleteAsync(new(mistral, Sync150ForeignThinking("mistral-conversations", "mistral", "mistral-other", ""), 4)).WaitAsync(Deadline);
            Dropped("mistral", captured!);
        }
    }

    private static async Task Sync150AlternatingThinking()
    {
        static string Turn(string model, string thinking, string text, int timestamp) => Sync150Assistant("openai-completions", "openrouter", model,
            "[{\"type\":\"thinking\",\"thinking\":\"" + thinking + "\",\"thinkingSignature\":\"reasoning_content\"},{\"type\":\"text\",\"text\":\"" + text + "\"}]", timestamp: timestamp);
        var kimi = new ModelDescriptor("moonshotai/Kimi-K3", "openai-completions", "openrouter");
        ImmutableArray<TranscriptEntry> messages =
        [
            Entry("""{"role":"user","content":"1","timestamp":1}"""), Entry(Turn(kimi.Id, "kimi one", "visible-a", 2)),
            Entry("""{"role":"user","content":"2","timestamp":3}"""), Entry(Turn("zai-org/GLM-5.3", "glm one", "visible-b", 4)),
            Entry("""{"role":"user","content":"3","timestamp":5}"""), Entry(Turn(kimi.Id, "kimi two", "visible-c", 6)),
            Entry("""{"role":"user","content":"4","timestamp":7}"""), Entry(Turn("zai-org/GLM-5.3", "glm two", "visible-d", 8)),
            Entry("""{"role":"user","content":"hi","timestamp":9}"""),
        ];
        string? captured = null;
        using var handler = new Handler(async request => { captured = await request.Content!.ReadAsStringAsync(); return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{}") }; });
        using var provider = NativeProviderFactory.CreateCompletions(kimi, new("https://openrouter.ai/api/v1/chat/completions"), Key, new(Reasoning: true),
            new(MaxTokensField: "max_tokens", SupportsStore: false), handler);
        await new ChatClient(provider).CompleteAsync(new(kimi, messages, 10)).WaitAsync(Deadline);
        var assistants = JsonNode.Parse(captured!)!["messages"]!.AsArray().Where(message => (string?)message!["role"] == "assistant")
            .Select(message => ((string?)message!["reasoning_content"] ?? "-") + "|" + (string?)message["content"]).ToArray();
        Equal("kimi one|visible-a,-|visible-b,kimi two|visible-c,-|visible-d", string.Join(",", assistants));
    }

    // ---------------------------------------------------------------- anthropic-messages.ts

    // #11091: a held system update goes directly before the next assistant message that is sent, so an assistant message that converts
    // to no blocks (here another model's empty reply) never puts it before a user message.
    private static Task Sync150AnthropicSystemHold()
    {
        var model = new ModelDescriptor("claude-opus-5", "anthropic-messages", "anthropic");
        var projector = new AnthropicMessagesRequestProjector(new(MaximumTokens: 1000, ModelReasoning: false, SupportsMidConversationSystemMessages: true));
        var history = ImmutableArray.Create(
            Entry("""{"role":"system","content":"base prompt","timestamp":0}"""),
            Entry("""{"role":"user","content":"hi","timestamp":1}"""),
            Entry("""{"role":"system","content":"updated guidance","timestamp":2}"""),
            Entry(Sync150Assistant("openai-codex-responses", "openai-codex", "gpt-x", """[{"type":"text","text":""}]""", timestamp: 3)),
            Entry("""{"role":"user","content":"again","timestamp":4}"""));
        static string Roles(JsonData payload) => string.Join(",", payload.Value.GetProperty("messages").EnumerateArray().Select(message => message.GetProperty("role").GetString()));
        Equal("user,user,system", Roles(projector.Project(new(model, history, 5))));
        var later = history.Add(Entry(Sync150Assistant("anthropic-messages", "anthropic", "claude-opus-5", """[{"type":"text","text":"answer"}]""", timestamp: 5)))
            .Add(Entry("""{"role":"user","content":"third","timestamp":6}"""));
        var payload = projector.Project(new(model, later, 7));
        Equal("user,user,system,assistant,user", Roles(payload));
        Equal("""[{"type":"text","text":"updated guidance"}]""", payload.Value.GetProperty("messages")[2].GetProperty("content").GetRawText());
        return Task.CompletedTask;
    }

    // Managed effort: the top-level effort is part of the cached prompt and fixed at "low" (an organization effort cap rejects any level
    // above it); the active effort rides the last marker.
    private static Task Sync150AnthropicLowEffort()
    {
        var model = new ModelDescriptor("claude-sonnet-5-5", "anthropic-messages", "anthropic");
        foreach (var effort in new[] { "low", "high", "max" })
        {
            var payload = new AnthropicMessagesRequestProjector(new(MaximumTokens: 1000, ModelReasoning: true, Effort: effort, SupportsMidConversationEffort: true))
                .Project(new(model, [Ask], 2)).Value;
            Equal("""{"effort":"low"}""", payload.GetProperty("output_config").GetRawText());
            Equal("""{"type":"adaptive","display":"summarized","block_binding":{"prefix_mismatch_behavior":"drop_block"}}""", payload.GetProperty("thinking").GetRawText());
            var messages = payload.GetProperty("messages");
            Equal(Marker(effort), messages[messages.GetArrayLength() - 1].GetRawText());
        }
        return Task.CompletedTask;
    }

    // #10794: Anthropic rejects error tool results with images, so their images follow all tool results of the turn after a caption.
    private static Task Sync150AnthropicErrorImages()
    {
        static string Image(string data) => "{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/png\",\"data\":\"" + data + "\"}}";
        var projector = new AnthropicMessagesRequestProjector(new(MaximumTokens: 1000, CacheRetention: AnthropicCacheRetention.None));
        var calls = Entry(Sync150Assistant("anthropic-messages", "anthropic", "fixture-haiku",
            """[{"type":"toolCall","id":"call-1","name":"codemode","arguments":{}},{"type":"toolCall","id":"call-2","name":"read","arguments":{}}]""", "toolUse"));
        var payload = projector.Project(new(Haiku, [
            Entry("""{"role":"user","content":"Show the screenshots","timestamp":1}"""), calls,
            Entry("""{"role":"toolResult","toolCallId":"call-1","toolName":"codemode","content":[{"type":"text","text":"Script failed"},{"type":"image","data":"AAAA","mimeType":"image/png"},{"type":"image","data":"CCCC","mimeType":"image/png"},{"type":"text","text":"Script error"}],"isError":true,"timestamp":3}"""),
            Entry("""{"role":"toolResult","toolCallId":"call-2","toolName":"read","content":[{"type":"image","data":"BBBB","mimeType":"image/png"}],"isError":false,"timestamp":4}""")], 5)).Value;
        BodyEqual("""{"role":"user","content":[{"type":"tool_result","tool_use_id":"call-1","content":"Script failed\n(image attached after the tool results)\nScript error","is_error":true},""" +
            """{"type":"tool_result","tool_use_id":"call-2","content":[{"type":"text","text":"(see attached image)"},""" + Image("BBBB") + """],"is_error":false},""" +
            """{"type":"text","text":"Images from the error result of tool call call-1 (codemode):"},""" + Image("AAAA") + "," + Image("CCCC") + "]}",
            payload.GetProperty("messages")[2].GetRawText());
        // An error result without images is unchanged.
        var plain = projector.Project(new(Haiku, [
            Entry("""{"role":"user","content":"Run it","timestamp":1}"""),
            Entry(Sync150Assistant("anthropic-messages", "anthropic", "fixture-haiku", """[{"type":"toolCall","id":"call-1","name":"codemode","arguments":{}}]""", "toolUse")),
            Entry("""{"role":"toolResult","toolCallId":"call-1","toolName":"codemode","content":[{"type":"text","text":"Script failed"}],"isError":true,"timestamp":3}""")], 4)).Value;
        BodyEqual("""{"role":"user","content":[{"type":"tool_result","tool_use_id":"call-1","content":"Script failed","is_error":true}]}""", plain.GetProperty("messages")[2].GetRawText());
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- identity.ts

    private static string Sync150Headers(IEnumerable<KeyValuePair<string, string>> headers) => string.Join("|", headers.Select(header => header.Key + "=" + header.Value));

    // HttpHeaders parses a User-Agent into its product and comment tokens and writes them space-separated on the wire.
    private static string UserAgentSeparator(string name, string otherwise = ", ") => name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) ? " " : otherwise;

    // formatUserAgent, mergeClientIdentity and getAttributionHeaders, after identity.test.ts. The detailed user agent's runtime field
    // names .NET (decision 0013) where Pi names node/<version> or bun/<version>. PiSharp defines no default attribution.
    private static Task Sync150IdentityFormats()
    {
        var detailed = ClientIdentities.FormatUserAgent(null);
        Check(System.Text.RegularExpressions.Regex.IsMatch(detailed, @"^pi/1\.5\.0 \([a-z0-9]+; dotnet/" +
            System.Text.RegularExpressions.Regex.Escape(Environment.Version.ToString()) + @"; [a-z0-9]+\)$"), "detailed user agent: " + detailed);
        Equal("1.5.0", ClientIdentities.GetClientVersion(null));
        Equal(detailed, ClientIdentities.FormatUserAgent(new() { UserAgent = ClientUserAgent.Detailed }));
        Equal("pi/9.9.9 " + detailed["pi/1.5.0 ".Length..], ClientIdentities.FormatUserAgent(new() { Version = "9.9.9" }));
        Equal("pi", ClientIdentities.FormatUserAgent(new() { UserAgent = ClientUserAgent.Short, Version = "9.9.9" }));
        Equal("acme/1.0", ClientIdentities.FormatUserAgent(new() { UserAgent = ClientUserAgent.Custom("acme/1.0") }));

        // Field by field: later sources win, unset fields and null sources are skipped, and attribution off clears a merged attribution.
        var acme = new ClientAttribution("acme", "https://acme.example", ["cli-agent"]);
        Check(ClientIdentities.Merge(null, null) is null, "merging nothing gives nothing");
        var merged = ClientIdentities.Merge(new() { Attribution = acme, UserAgent = ClientUserAgent.Short }, null, new() { Version = "1.0.0" })!;
        Check(merged == new ClientIdentity { Attribution = acme, UserAgent = ClientUserAgent.Short, Version = "1.0.0" }, "merged identity");
        var off = ClientIdentities.Merge(new() { Attribution = acme }, new() { AttributionOff = true })!;
        Check(off.Attribution is null && off.AttributionOff, "attribution turned off by a later identity");
        Equal(acme, ClientIdentities.Merge(off, new() { Attribution = acme })!.Attribution);

        // No attribution unless an identity sets one.
        Equal("", Sync150Headers(ClientIdentities.GetAttributionHeaders("openrouter", "https://openrouter.ai/api/v1", null)));
        Equal("", Sync150Headers(ClientIdentities.GetAttributionHeaders("openrouter", "https://openrouter.ai/api/v1", off)));
        Equal("User-Agent=" + detailed, Sync150Headers(ClientIdentities.GetIdentityHeaders("openrouter", "https://openrouter.ai/api/v1", null)));
        // OpenRouter by provider or by a base URL containing openrouter.ai (also one that is not a valid URL); no categories, no header.
        var identity = new ClientIdentity { Attribution = acme with { Categories = ["cli-agent", "cloud-agent"] } };
        const string openRouter = "HTTP-Referer=https://acme.example|X-OpenRouter-Title=acme|X-OpenRouter-Categories=cli-agent,cloud-agent";
        Equal(openRouter, Sync150Headers(ClientIdentities.GetAttributionHeaders("openrouter", "https://openrouter.ai/api/v1", identity)));
        Equal(openRouter, Sync150Headers(ClientIdentities.GetAttributionHeaders("custom", "not-a-url-openrouter.ai", identity)));
        Equal("HTTP-Referer=https://acme.example|X-OpenRouter-Title=acme",
            Sync150Headers(ClientIdentities.GetAttributionHeaders("openrouter", "https://openrouter.ai/api/v1", new() { Attribution = acme with { Categories = [] } })));
        Equal("HTTP-Referer=https://acme.example|X-OpenRouter-Title=acme",
            Sync150Headers(ClientIdentities.GetAttributionHeaders("openrouter", "https://openrouter.ai/api/v1", new() { Attribution = new("acme", "https://acme.example") })));
        // NVIDIA NIM by provider or exact host, with a capitalized name; OpenCode by provider or host; nothing for gateways and others.
        Equal("X-BILLING-INVOKE-ORIGIN=Acme", Sync150Headers(ClientIdentities.GetAttributionHeaders("nvidia", "https://example.test/v1", identity)));
        Equal("X-BILLING-INVOKE-ORIGIN=Acme", Sync150Headers(ClientIdentities.GetAttributionHeaders("custom-nim", "https://integrate.api.nvidia.com/v1", identity)));
        Equal("", Sync150Headers(ClientIdentities.GetAttributionHeaders("custom-nim", "https://integrate.api.nvidia.com.example/v1", identity)));
        foreach (var (provider, url) in new[] { ("opencode", "https://opencode.ai/zen/v1"), ("opencode-go", "https://example.test/v1"), ("custom", "https://opencode.ai/zen/v1") })
            Equal("x-opencode-client=acme", Sync150Headers(ClientIdentities.GetAttributionHeaders(provider, url, identity)));
        Equal("", Sync150Headers(ClientIdentities.GetAttributionHeaders("vercel-ai-gateway", "https://ai-gateway.vercel.sh/v1", identity)));
        Check(!ClientIdentities.GetAttributionHeaders("openrouter", "https://openrouter.ai/api/v1", identity).Any(header => header.Key == "X-BILLING-INVOKE-ORIGIN"),
            "NVIDIA models routed through OpenRouter are attributed to OpenRouter");
        Equal("User-Agent=pi|x-opencode-client=acme", Sync150Headers(ClientIdentities.GetIdentityHeaders("opencode", "", identity with { UserAgent = ClientUserAgent.Short })));
        return Task.CompletedTask;
    }

    // getIdentityHeaders on chat requests: below the model and request headers, so a model header (an impersonating user agent) wins
    // and a null request header removes one; a request identity (StreamOptions.identity) merges over the configured identity.
    private static async Task Sync150IdentityOnRequests()
    {
        var detailed = ClientIdentities.FormatUserAgent(null);
        static string? Header(HttpRequestMessage request, string name) => request.Headers.TryGetValues(name, out var values) ? string.Join(UserAgentSeparator(name), values) : null;
        var acme = new ClientAttribution("acme", "https://acme.example", ["cli-agent"]);

        // openai-completions
        var router = new ModelDescriptor("test-model", "openai-completions", "openrouter");
        var routerUrl = new Uri("https://openrouter.ai/api/v1/chat/completions");
        using (var sent = new CompletionsKeyAuthRequestFactory(routerUrl, router).Create(new(router, [Ask], 1), Key))
        {
            Equal(detailed, Header(sent, "User-Agent"));
            Check(Header(sent, "HTTP-Referer") is null && Header(sent, "X-OpenRouter-Title") is null, "completions: no attribution by default");
        }
        var configured = new CompletionsKeyAuthRequestFactory(routerUrl, router, null, new(ModelHeaders: JsonData.Parse("""{"X-OpenRouter-Title":"model-title"}"""),
            Headers: JsonData.Parse("""{"HTTP-Referer":null}""")) { Identity = new() { Attribution = acme, UserAgent = ClientUserAgent.Short } });
        using (var sent = configured.Create(new(router, [Ask], 1), Key))
        {
            Equal("pi", Header(sent, "User-Agent")); Equal("model-title", Header(sent, "X-OpenRouter-Title")); Equal("cli-agent", Header(sent, "X-OpenRouter-Categories"));
            Check(Header(sent, "HTTP-Referer") is null, "completions: a null request header removes an attribution header");
        }
        using (var sent = configured.Create(new(router, [Ask], 1) { Identity = new() { AttributionOff = true } }, Key))
        {
            Equal("pi", Header(sent, "User-Agent")); Equal("model-title", Header(sent, "X-OpenRouter-Title"));
            Check(Header(sent, "X-OpenRouter-Categories") is null, "completions: a request identity turns attribution off");
        }
        var copilot = new ModelDescriptor("test-model", "openai-completions", "github-copilot");
        using (var sent = new CompletionsKeyAuthRequestFactory(new("https://api.githubcopilot.com/chat/completions"), copilot, null,
            new(ModelHeaders: JsonData.Parse("""{"User-Agent":"GitHubCopilotChat/0.35.0"}""")) { Identity = new() { UserAgent = ClientUserAgent.Custom("acme") } })
            .Create(new(copilot, [Ask], 1), Key))
            Equal("GitHubCopilotChat/0.35.0", Header(sent, "User-Agent"));

        // openai-responses
        var gpt = new ModelDescriptor("gpt-identity", "openai-responses", "openai");
        var responses = new ResponsesKeyAuthRequestFactory(new("https://responses.invalid/v1/responses"), gpt, new(true));
        using (var sent = responses.Create(new(gpt, [Ask], 1), Key)) Equal(detailed, Header(sent, "User-Agent"));
        using (var sent = responses.Create(new(gpt, [Ask], 1) { Identity = new() { UserAgent = ClientUserAgent.Custom("acme/2.0") } }, Key)) Equal("acme/2.0", Header(sent, "User-Agent"));
        using (var sent = new ResponsesKeyAuthRequestFactory(new("https://responses.invalid/v1/responses"), gpt, new(true),
            new() { Headers = JsonData.Parse("""{"user-agent":"caller-agent"}""") }).Create(new(gpt, [Ask], 1), Key))
            Equal("caller-agent", Header(sent, "User-Agent"));

        // anthropic-messages (API key; the OAuth path sends Claude Code's headers instead)
        var opencode = new ModelDescriptor("claude-identity", "anthropic-messages", "opencode");
        var anthropic = new AnthropicMessagesKeyAuthRequestFactory(new("https://opencode.ai/zen"), opencode, new(256), new() { Identity = new() { Attribution = acme } });
        using (var sent = anthropic.Create(new(opencode, [Ask], 1), Key)) { Equal(detailed, Header(sent, "User-Agent")); Equal("acme", Header(sent, "x-opencode-client")); }
        using (var sent = anthropic.Create(new(opencode, [Ask], 1) { Identity = new() { UserAgent = ClientUserAgent.Short, AttributionOff = true } }, Key))
        {
            Equal("pi", Header(sent, "User-Agent"));
            Check(Header(sent, "x-opencode-client") is null, "anthropic: a request identity turns attribution off");
        }
        using (var sent = new AnthropicMessagesKeyAuthRequestFactory(new("https://anthropic.invalid"), Haiku, new(256)).Create(new(Haiku, [Ask], 1), Key))
        {
            Equal(detailed, Header(sent, "User-Agent"));
            Check(Header(sent, "x-opencode-client") is null && Header(sent, "HTTP-Referer") is null, "anthropic: no attribution by default");
        }

        // google-generative-ai
        using (var sent = await new GoogleKeyAuthRequestFactory(Gemini, new GoogleGenerativeAIOptions(GoogleMetadata(), Key)).CreateAsync(new(Gemini, [Ask], 1) { Identity = new() { UserAgent = ClientUserAgent.Short } }))
            Equal("pi", Header(sent, "User-Agent"));
    }

    // ---------------------------------------------------------------- transcript.ts resolveTranscriptTools("additions")

    // #10940: Kimi tool messages cannot remove or redefine tools, so the top-level list becomes the tool state after the last removal
    // or redeclaration (a name declared again after its removal counts) and later additions stay anchored in tool-bearing system
    // messages. The cached prefix changes once per removal instead of the whole list moving to the top level on every later load.
    private static async Task Sync150KimiToolFold()
    {
        static string Tool(string name) => "{\"name\":\"" + name + "\",\"description\":\"" + name + "\",\"parameters\":{\"type\":\"object\",\"properties\":{}}}";
        static TranscriptEntry System(string content, string changes, int timestamp) =>
            Entry("{\"role\":\"system\",\"content\":\"" + content + "\"," + changes + ",\"timestamp\":" + timestamp + "}");
        var kimi = new ModelDescriptor("kimi-k3", "openai-completions", "moonshotai");
        var factory = new CompletionsKeyAuthRequestFactory(new("https://api.moonshot.invalid/v1/chat/completions"), kimi,
            new(SupportsMidConversationSystemMessages: true) { SupportsMidConversationToolAdditions = true });
        async Task<string> Shape(ImmutableArray<TranscriptEntry> messages)
        {
            using var sent = factory.Create(new(kimi, messages, 20), Key);
            var body = JsonNode.Parse(await sent.Content!.ReadAsStringAsync())!;
            static string Names(JsonNode? tools) => string.Join(",", tools!.AsArray().Select(tool => (string?)tool!["function"]!["name"]));
            var anchored = body["messages"]!.AsArray().Where(message => message!["tools"] is not null).Select(message => Names(message!["tools"]));
            return "top:" + (body["tools"] is { } top ? Names(top) : "") + " anchored:" + string.Join("|", anchored);
        }
        var history = ImmutableArray.Create(
            System("base prompt", "\"toolsAdded\":[" + Tool("base_tool") + "," + Tool("drop_tool") + "]", 0),
            Entry("""{"role":"user","content":"hi","timestamp":1}"""));
        Equal("top:base_tool,drop_tool anchored:", await Shape(history));
        history = history.Add(System("", "\"toolsAdded\":[" + Tool("late_tool") + "]", 2)).Add(Entry("""{"role":"user","content":"load","timestamp":3}"""));
        Equal("top:base_tool,drop_tool anchored:late_tool", await Shape(history));
        // A removal moves the boundary to its message: the top level holds the state after it, the later addition stays anchored.
        var removal = history.Insert(2, System("", "\"toolsRemoved\":[{\"name\":\"drop_tool\"}]", 2));
        Equal("top:base_tool anchored:late_tool", await Shape(removal));
        var later = removal.Add(System("", "\"toolsAdded\":[" + Tool("later_tool") + "]", 4)).Add(Entry("""{"role":"user","content":"more","timestamp":5}"""));
        Equal("top:base_tool anchored:late_tool|later_tool", await Shape(later));
        // Declaring the removed tool again is a redeclaration: the boundary moves to it; the addition after it stays anchored.
        var again = later.Add(System("", "\"toolsAdded\":[" + Tool("drop_tool") + "]", 6)).Add(System("", "\"toolsAdded\":[" + Tool("last_tool") + "]", 7))
            .Add(Entry("""{"role":"user","content":"end","timestamp":8}"""));
        Equal("top:base_tool,late_tool,later_tool,drop_tool anchored:last_tool", await Shape(again));
    }

    // ---------------------------------------------------------------- openai-responses.ts configuration_update

    // #10930: with compat.supportsMidConvoEffort, reasoning.effort stays "medium" and the level travels as configuration_update items:
    // one before each earlier reply of this model with its recorded providerThinkingLevel and one at the end, so earlier input never
    // changes. Sign in with ChatGPT (an openai key without "sk-" at api.openai.com/v1), the flag off and thinking off keep reasoning.effort.
    private static async Task Sync150ResponsesMidEffort()
    {
        static string Metadata(bool flag) => """
            {"id":"gpt-6-luna","name":"GPT-6 Luna","api":"openai-responses","provider":"openai","baseUrl":"https://api.openai.com/v1","reasoning":true,
            "thinkingLevelMap":{"off":"none","minimal":null,"low":"low","medium":"medium","high":"high","xhigh":"xhigh","max":"max"},"input":["text"],
            "cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0},"contextWindow":272000,"maxTokens":128000,"compat":{"supportsMidConvoEffort":FLAG}}
            """.Replace("FLAG", flag ? "true" : "false", StringComparison.Ordinal);
        const string item = """{"type":"message","id":"msg_r1","role":"assistant","status":"completed","content":[{"type":"output_text","text":"answer","annotations":[]}]}""";
        var stream = Frames("""{"type":"response.created","response":{"id":"r1"}}""",
            """{"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"msg_r1","role":"assistant","status":"in_progress","content":[]}}""",
            """{"type":"response.content_part.added","output_index":0,"item_id":"msg_r1","content_index":0,"part":{"type":"output_text","text":"","annotations":[]}}""",
            """{"type":"response.output_text.delta","output_index":0,"item_id":"msg_r1","content_index":0,"delta":"answer"}""",
            """{"type":"response.output_item.done","output_index":0,"item":""" + item + "}",
            """{"type":"response.completed","response":{"id":"r1","status":"completed","output":[""" + item + """],"usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}}""");
        var model = new ModelDescriptor("gpt-6-luna", "openai-responses", "openai");
        static TranscriptEntry User(string text) => Entry("{\"role\":\"user\",\"content\":\"" + text + "\",\"timestamp\":0}");
        async Task<(JsonNode Payload, AssistantMessage Message)> Run(ImmutableArray<TranscriptEntry> messages, string level, string key = "sk-inert-provider-sync", bool flag = true)
        {
            string? body = null;
            using var handler = new Handler(async request => { body = await request.Content!.ReadAsStringAsync(); return Sse(null, stream); });
            using var provider = NativeProviderFactory.CreateResponses(model, new("https://api.openai.com/v1/responses"), key, new(true), null, handler, JsonData.Parse(Metadata(flag)));
            AssistantMessage? done = null;
            await foreach (var progress in provider.Transport.StreamAsync(new(model, messages, 1) { ThinkingLevel = level }).WithCancellation(CancellationToken.None))
                if (progress is StreamDone finished) done = finished.Message;
            Check(done is { StopReason: StopReason.Stop }, "mid-effort reply completed");
            return (JsonNode.Parse(body!)!, done!);
        }
        static string Updates(JsonNode payload) => string.Join(",", payload["input"]!.AsArray().Where(entry => (string?)entry!["type"] == "configuration_update")
            .Select(entry => entry!["reasoning"]!.ToJsonString()));
        static string? Level(AssistantMessage message) => message.ExtraProperties?.TryGet("providerThinkingLevel", out var value) == true ? value!.Value.GetString() : null;
        static TranscriptEntry Reply(AssistantMessage message) => new("assistant", PiWireJson.WriteMessage(message));

        // The level is appended only when it changes; earlier input is rebuilt unchanged.
        var history = ImmutableArray.Create(User("one"));
        var first = await Run(history, "high");
        Equal("""[{"role":"user","content":[{"type":"input_text","text":"one"}]},{"type":"configuration_update","reasoning":{"effort":"high"}}]""", first.Payload["input"]!.ToJsonString());
        history = history.Add(Reply(first.Message)).Add(User("two"));
        var second = await Run(history, "high");
        history = history.Add(Reply(second.Message)).Add(User("three"));
        var third = await Run(history, "low");
        foreach (var (payload, _) in new[] { first, second, third }) Equal("""{"effort":"medium","summary":"auto"}""", payload["reasoning"]!.ToJsonString());
        Check(second.Payload["input"]!.ToJsonString().StartsWith(first.Payload["input"]!.ToJsonString()[..^1], StringComparison.Ordinal) &&
            third.Payload["input"]!.ToJsonString().StartsWith(second.Payload["input"]!.ToJsonString()[..^1], StringComparison.Ordinal), "growing input prefix");
        Equal("""{"effort":"high"},{"effort":"high"},{"effort":"low"}""", Updates(third.Payload));
        Equal("""{"type":"configuration_update","reasoning":{"effort":"low"}}""", third.Payload["input"]!.AsArray()[^1]!.ToJsonString());
        Equal("high,high,low", string.Join(",", new[] { first, second, third }.Select(run => Level(run.Message))));

        // Items go only before replies recorded by this model.
        var unstamped = (await Run([User("one")], "high", flag: false)).Message;
        var recorded = first.Message;
        var otherProvider = recorded with { Provider = "opencode" };
        var otherModel = recorded with { Model = "gpt-6-sol" };
        var mixed = await Run([User("one"), Reply(unstamped), User("two"), Reply(otherProvider), User("three"), Reply(otherModel), User("four"), Reply(recorded), User("five")], "low");
        Equal("user,message,user,message,user,message,user,configuration_update,message,user,configuration_update",
            string.Join(",", mixed.Payload["input"]!.AsArray().Select(entry => (string?)entry!["type"] ?? (string?)entry!["role"])));
        Equal("""{"effort":"high"},{"effort":"low"}""", Updates(mixed.Payload));

        // Sign in with ChatGPT, the compat flag off and thinking off send the level as reasoning.effort, with no items and no recorded level.
        foreach (var (key, flag, level, effort) in new[] { ("chatgpt-access-token", true, "low", "low"), ("sk-inert-provider-sync", false, "low", "low"), ("sk-inert-provider-sync", true, "off", "none") })
        {
            var before = await Run([User("one")], "high", key, flag);
            var (payload, message) = await Run([User("one"), Reply(before.Message), User("two")], level, key, flag);
            Equal(effort, (string?)payload["reasoning"]!["effort"]);
            Check(Updates(payload).Length == 0 && Level(message) is null, "no configuration_update for " + key + " flag=" + flag + " level=" + level);
        }
    }
}
