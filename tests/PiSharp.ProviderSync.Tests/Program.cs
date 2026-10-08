using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.AI;
using PiSharp.AI.Authentication;
using PiSharp.AI.Catalogs;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.AI.Protocols.AzureResponses;
using PiSharp.AI.Protocols.GoogleGenerativeAI;
using PiSharp.AI.Protocols.MistralConversations;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.AI.Providers;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Configuration;
using PiSharp.Contracts;

// Authored expectations derived by reading Pi v1.1.0 (abe508e1b89912adde45528136c3221eb69acdd7) source text:
// api/anthropic-messages.ts, api/constrained-sampling.ts, api/simple-options.ts, api/openai-completions.ts,
// api/openai-responses.ts, api/azure-openai-responses.ts, providers/azure.ts, env-api-keys.ts, utils/retry.ts,
// utils/provider-retry.ts, utils/overflow.ts, api/mistral-conversations.ts, utils/estimate.ts, utils/pi-user-agent.ts
// and the @earendil-works/pi-ai@1.1.0 catalog shards. Nothing here executes upstream code or is an upstream capture;
// no network, provider or credential is used.
internal static class Program
{
    private const string SourceSha = "abe508e1b89912adde45528136c3221eb69acdd7";
    private const string Key = "inert-provider-sync-key";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static int bodyComparisons;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--report")) throw new ArgumentException("Use [--report <fresh path>].");
        var cases = new (string Id, Func<Task> Run)[]
        {
            ("anthropic.inline-tools-full-requests-across-add-redefine-remove", AnthropicInlineTools),
            ("anthropic.strict-rejected-keywords-send-non-strict", AnthropicStrictKeywords),
            ("sampling.completions-model-level-request-precedence", CompletionsSampling),
            ("sampling.responses-model-level-request-precedence-and-metadata-binding", ResponsesSampling),
            ("sampling.azure-responses-level-precedence", AzureSampling),
            ("azure.provider-rename-environment-and-legacy-provider-id", AzureRename),
            ("retry.busy-capacity-and-stream-cancel-classification", RetryClassification),
            ("retry.zai-cn-overflow-classification", OverflowClassification),
            ("retry.mistral-finish-reason-error-is-retryable", MistralFinishError),
            ("retry.completions-unusable-delays-backoff-and-no-retry-statuses", CompletionsBackoff),
            ("retry.google-unusable-delays-backoff-and-no-retry-statuses", GoogleBackoff),
            ("estimate.request-estimators-use-three-and-a-half-chars-per-token", TokenEstimates),
            ("google.user-agent-retained-with-header-precedence", GoogleUserAgent),
            ("catalog.pi-ai-1.1.0-shards-hashes-and-additions", Catalog)
        };
        var results = new List<object>(); var failures = 0;
        foreach (var test in cases)
        {
            try { await test.Run().WaitAsync(TimeSpan.FromSeconds(60)); results.Add(new { test.Id, status = "PASS_AUTHORED_NATIVE_ONLY" }); }
            catch (Exception error) { failures++; results.Add(new { test.Id, status = "FAIL", failure = error.ToString() }); }
        }
        var report = new { suite = "provider-sync-1.1.0", sourceSha = SourceSha, status = "AUTHORED NATIVE; NOT UPSTREAM CAPTURES", failures,
            bodyComparisons, genuineSourceCasesCaptured = 0, results };
        if (args.Length == 2)
        {
            await using var file = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await JsonSerializer.SerializeAsync(file, report, new JsonSerializerOptions { WriteIndented = true });
        }
        else Console.WriteLine(JsonSerializer.Serialize(report));
        return failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- 1. Anthropic inline tools and strict keywords

    private static readonly ModelDescriptor Haiku = new("fixture-haiku", "anthropic-messages", "anthropic");
    private const string Cache = ""","cache_control":{"type":"ephemeral"}""";
    private const string Placeholder = """{"name":"__pi_deferred_placeholder__","description":"Reserved placeholder. Never available. Never call this.","input_schema":{"type":"object","properties":{},"required":[]},"defer_loading":true}""";

    private static async Task AnthropicInlineTools()
    {
        var sys0 = Entry("""{"role":"system","content":"Base","toolsAdded":[{"name":"read","description":"Read a file","parameters":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}}],"timestamp":0}""");
        var u1 = Entry("""{"role":"user","content":"one","timestamp":1}""");
        var a1 = AnthropicAssistant(StopReason.ToolUse, new ToolCallContent("call-1", "read", JsonData.Parse("""{"path":"a.txt"}""")));
        var r1 = Entry("""{"role":"toolResult","toolCallId":"call-1","toolName":"read","content":[{"type":"text","text":"done"}],"isError":false,"timestamp":3}""");
        var s1 = Entry("""{"role":"system","toolsAdded":[{"name":"write","description":"Write a file","parameters":{"type":"object","properties":{"path":{"type":"string"},"text":{"type":"string"}},"required":["path","text"]}}],"timestamp":4}""");
        var u2 = Entry("""{"role":"user","content":"two","timestamp":5}""");
        var a2 = AnthropicAssistant(StopReason.Stop, new TextContent("ok"));
        var s2 = Entry("""{"role":"system","toolsRemoved":[{"name":"write"}],"toolsAdded":[{"name":"write","description":"Write a file (v2)","parameters":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}}],"timestamp":7}""");
        var u3 = Entry("""{"role":"user","content":"three","timestamp":8}""");
        var a3 = AnthropicAssistant(StopReason.Stop, new TextContent("ok2"));
        var s3 = Entry("""{"role":"system","toolsRemoved":[{"name":"write"}],"timestamp":10}""");
        var u4 = Entry("""{"role":"user","content":"four","timestamp":11}""");

        const string tools = "\"tools\":[" + """{"name":"read","description":"Read a file","input_schema":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]},"eager_input_streaming":true""" + Cache + "}," + Placeholder + "]";
        const string system = "\"system\":[{\"type\":\"text\",\"text\":\"Base\"" + Cache + "}]";
        const string writeV1 = """{"name":"write","description":"Write a file","input_schema":{"type":"object","properties":{"path":{"type":"string"},"text":{"type":"string"}},"required":["path","text"]},"eager_input_streaming":true}""";
        const string writeV2 = """{"name":"write","description":"Write a file (v2)","input_schema":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]},"eager_input_streaming":true}""";
        static string Addition(string definition, bool cached) => """{"type":"tool_addition","tool":{"type":"tool_definition","definition":""" + definition + "}" + (cached ? Cache : "") + "}";
        static string Body(params string[] messages) => """{"model":"fixture-haiku","messages":[""" + string.Join(",", messages) + """],"max_tokens":256,"stream":true,""" + system + "," + tools + "}";
        const string mu1 = """{"role":"user","content":"one"}""";
        const string ma1 = """{"role":"assistant","content":[{"type":"tool_use","id":"call-1","name":"read","input":{"path":"a.txt"}}]}""";
        const string mr1 = """{"role":"user","content":[{"type":"tool_result","tool_use_id":"call-1","content":"done","is_error":false}]}""";
        const string mu2 = """{"role":"user","content":"two"}""";
        const string ma2 = """{"role":"assistant","content":[{"type":"text","text":"ok"}]}""";
        const string mu3 = """{"role":"user","content":"three"}""";
        const string ma3 = """{"role":"assistant","content":[{"type":"text","text":"ok2"}]}""";
        const string mu4 = """{"role":"user","content":"four"}""";
        static string System(string blocks) => """{"role":"system","content":[""" + blocks + "]}";

        var turns = new (TranscriptEntry[] Messages, string Expected)[]
        {
            ([sys0, u1], Body("{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"one\"" + Cache + "}]}")),
            // Addition: the later tool is defined by value; the request-level tool list does not change.
            ([sys0, u1, a1, r1, s1, u2], Body(mu1, ma1, mr1, mu2, System(Addition(writeV1, true)))),
            // Redefinition under the same name: no tool_removal and no fallback to a resent tool list.
            ([sys0, u1, a1, r1, s1, u2, a2, s2, u3], Body(mu1, ma1, mr1, mu2, System(Addition(writeV1, false)), ma2, mu3, System(Addition(writeV2, true)))),
            // Removal stays a tool_reference.
            ([sys0, u1, a1, r1, s1, u2, a2, s2, u3, a3, s3, u4], Body(mu1, ma1, mr1, mu2, System(Addition(writeV1, false)), ma2, mu3, System(Addition(writeV2, false)), ma3, mu4,
                System("""{"type":"tool_removal","tool":{"type":"tool_reference","name":"write"}""" + Cache + "}")))
        };
        var options = new AnthropicMessagesRequestOptions(256, SupportsMidConversationSystemMessages: true, SupportsMidConversationToolChanges: true);
        var factory = new AnthropicMessagesKeyAuthRequestFactory(new("https://anthropic.invalid"), Haiku, options);
        foreach (var (messages, expected) in turns)
        {
            using var request = factory.Create(new(Haiku, messages.ToImmutableArray(), 1), Key);
            BodyEqual(expected, await request.Content!.ReadAsStringAsync());
            Equal("inline-tools-2026-09-15", request.Headers.GetValues("anthropic-beta").Single());
        }
        // Without the tool-change capability the same redefinition history is sent as the current static list (Pi fallback).
        var fallback = new AnthropicMessagesRequestProjector(options with { SupportsMidConversationToolChanges = false })
            .Project(new(Haiku, turns[2].Messages.ToImmutableArray(), 1)).Value;
        Equal("""[{"name":"read","description":"Read a file","input_schema":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]},"eager_input_streaming":true},{"name":"write","description":"Write a file (v2)","input_schema":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]},"eager_input_streaming":true,"cache_control":{"type":"ephemeral"}}]""",
            fallback.GetProperty("tools").GetRawText());
        Check(!fallback.TryGetProperty("betas", out _), "Fallback enabled the inline tools beta.");
    }

    private static Task AnthropicStrictKeywords()
    {
        var projector = new AnthropicMessagesRequestProjector(new(128, SupportsStrictTools: true, CacheRetention: AnthropicCacheRetention.None));
        string Tool(string name, string parameters, string strict = "prefer") => projector.Project(new(Haiku, [Entry(
            "{\"role\":\"system\",\"content\":\"\",\"toolsAdded\":[{\"name\":\"" + name + "\",\"description\":\"D\",\"parameters\":" + parameters +
            ",\"constrainedSampling\":{\"type\":\"json_schema\",\"strict\":\"" + strict + "\"}}]}")], 1)).Value.GetProperty("tools")[0].GetRawText();
        string Legacy(string name, string properties, string required = "[]") =>
            "{\"name\":\"" + name + "\",\"description\":\"D\",\"input_schema\":{\"type\":\"object\",\"properties\":" + properties + ",\"required\":" + required + "},\"eager_input_streaming\":true}";
        // Rejected keyword values: prefer falls back to non-strict, require fails before any request exists.
        const string minimum = """{"type":"object","properties":{"n":{"type":"integer","minimum":1}},"required":["n"]}""";
        BodyEqual(Legacy("a", """{"n":{"type":"integer","minimum":1}}""", """["n"]"""), Tool("a", minimum));
        ThrowsAnthropic(AnthropicRequestFailure.UnsupportedStrictSchema, () => Tool("a", minimum, "require"));
        const string nestedMaximum = """{"type":"object","properties":{"m":{"type":"array","items":{"type":"object","properties":{"k":{"type":"integer","maximum":3}},"required":["k"]}}},"required":["m"]}""";
        BodyEqual(Legacy("b", """{"m":{"type":"array","items":{"type":"object","properties":{"k":{"type":"integer","maximum":3}},"required":["k"]}}}""", """["m"]"""), Tool("b", nestedMaximum));
        BodyEqual(Legacy("c", """{"v":{"anyOf":[{"type":"string","format":"regex"},{"type":"null"}]}}"""),
            Tool("c", """{"type":"object","properties":{"v":{"anyOf":[{"type":"string","format":"regex"},{"type":"null"}]}}}"""));
        BodyEqual(Legacy("d", """{"list":{"type":"array","items":{"type":"integer"},"minItems":2}}""", """["list"]"""),
            Tool("d", """{"type":"object","properties":{"list":{"type":"array","items":{"type":"integer"},"minItems":2}},"required":["list"]}"""));
        // Accepted values stay strict: minItems 0/1, listed formats, and property names that merely spell a keyword.
        BodyEqual("""{"name":"e","description":"D","input_schema":{"type":"object","properties":{"tags":{"type":"array","items":{"type":"string","format":"email"},"minItems":1}},"required":["tags"],"additionalProperties":false},"eager_input_streaming":true,"strict":true}""",
            Tool("e", """{"type":"object","properties":{"tags":{"type":"array","items":{"type":"string","format":"email"},"minItems":1}},"required":["tags"]}""", "require"));
        BodyEqual("""{"name":"f","description":"D","input_schema":{"type":"object","properties":{"minimum":{"type":"number"},"format":{"anyOf":[{"type":"string"},{"type":"null"}]}},"required":["minimum","format"],"additionalProperties":false},"eager_input_streaming":true,"strict":true}""",
            Tool("f", """{"type":"object","properties":{"minimum":{"type":"number"},"format":{"type":"string"}},"required":["minimum"]}"""));
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- 2. samplingParamsByThinkingLevel

    private const string ByLevel = ""","samplingParams":{"temperature":0.7,"top_p":0.9},"samplingParamsByThinkingLevel":{"off":{"temperature":0.2},"high":{"temperature":1,"top_k":40},"minimal":null}""";
    private static TranscriptEntry Ask => Entry("""{"role":"user","content":"ask","timestamp":1}""");

    private static async Task CompletionsSampling()
    {
        var model = new ModelDescriptor("sampling-model", "openai-completions", "fixture");
        var endpoint = new Uri("https://completions.invalid/v1/chat/completions");
        string Metadata(bool reasoning, string levels = ByLevel) => """{"id":"sampling-model","name":"Sampling","api":"openai-completions","provider":"fixture","baseUrl":"https://completions.invalid/v1","reasoning":""" +
            (reasoning ? "true" : "false") + ""","input":["text"],"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0},"contextWindow":1000,"maxTokens":100""" + levels + "}";
        async Task<string> Body(string? effort, string? request = null, bool reasoning = true)
        {
            var factory = new CompletionsKeyAuthRequestFactory(endpoint, model, null, new CompletionsKeyAuthRequestOptions(ReasoningEffort: effort)
            { ModelMetadata = JsonData.Parse(Metadata(reasoning)), SamplingParams = request is null ? null : JsonData.Parse(request) });
            using var http = factory.Create(new(model, [Ask], 1), Key);
            return await http.Content!.ReadAsStringAsync();
        }
        const string prefix = """{"model":"sampling-model","messages":[{"role":"user","content":"ask"}],"stream":true,"stream_options":{"include_usage":true},"store":false""";
        BodyEqual(prefix + ""","reasoning_effort":"high","temperature":1,"top_p":0.9,"top_k":40}""", await Body("high"));
        BodyEqual(prefix + ""","temperature":0.2,"top_p":0.9}""", await Body(null));
        BodyEqual(prefix + ""","reasoning_effort":"high","temperature":1,"top_p":0.5,"top_k":40,"seed":1}""", await Body("high", """{"top_p":0.5,"seed":1}"""));
        // xhigh is unsupported without a thinkingLevelMap entry and clamps to high for sampling; the effort itself is not clamped here.
        BodyEqual(prefix + ""","reasoning_effort":"xhigh","temperature":1,"top_p":0.9,"top_k":40}""", await Body("xhigh"));
        // A null level entry adds nothing over the model defaults.
        BodyEqual(prefix + ""","reasoning_effort":"minimal","temperature":0.7,"top_p":0.9}""", await Body("minimal"));
        // A non-reasoning model clamps every level to off.
        BodyEqual(prefix + ""","temperature":0.2,"top_p":0.9}""", await Body("high", reasoning: false));
        var rejected = false;
        try { _ = new CompletionsKeyAuthRequestFactory(endpoint, model, null, new() { ModelMetadata = JsonData.Parse(Metadata(true, ""","samplingParamsByThinkingLevel":{"high":[1]}""")) }); }
        catch (CompletionsRequestException error) when (error.Failure == CompletionsRequestFailure.InvalidConfiguration) { rejected = true; }
        Check(rejected, "Malformed samplingParamsByThinkingLevel was admitted.");
    }

    private static async Task ResponsesSampling()
    {
        var model = new ModelDescriptor("responses-model", "openai-responses", "openai");
        var levels = JsonData.Parse("""{"off":{"temperature":0.2},"high":{"temperature":1,"top_k":40},"minimal":null}""");
        async Task<string> Body(ResponsesKeyAuthRequestOptions options)
        {
            var factory = new ResponsesKeyAuthRequestFactory(new("https://responses.invalid/v1/responses"), model, new(true), options with
            { ModelSamplingParams = JsonData.Parse("""{"temperature":0.7,"top_p":0.9}"""), ModelSamplingParamsByThinkingLevel = levels });
            using var http = factory.Create(new(model, [Ask], 1), Key);
            return await http.Content!.ReadAsStringAsync();
        }
        const string prefix = """{"model":"responses-model","input":[{"role":"user","content":[{"type":"input_text","text":"ask"}]}],"stream":true,"store":false""";
        const string reasoning = ""","reasoning":{"effort":"high","summary":"auto"},"include":["reasoning.encrypted_content"]""";
        BodyEqual(prefix + reasoning + ""","temperature":1,"top_p":0.9,"top_k":40}""", await Body(new(ReasoningEffort: "high")));
        BodyEqual(prefix + reasoning + ""","temperature":1,"top_p":0.5,"top_k":40,"seed":1}""", await Body(new(ReasoningEffort: "high") { SamplingParams = JsonData.Parse("""{"top_p":0.5,"seed":1}""") }));
        BodyEqual(prefix + ""","reasoning":{"effort":"none"},"temperature":0.2,"top_p":0.9}""", await Body(new()));
        // A summary without an effort requests medium, which has no entry.
        BodyEqual(prefix + ""","reasoning":{"effort":"medium","summary":"concise"},"include":["reasoning.encrypted_content"],"temperature":0.7,"top_p":0.9}""",
            await Body(new(ReasoningSummary: "concise")));
        var rejected = false;
        try { _ = new ResponsesKeyAuthRequestFactory(new("https://responses.invalid/v1/responses"), model, new(true), new() { ModelSamplingParamsByThinkingLevel = JsonData.Parse("""{"high":true}""") }); }
        catch (ResponsesKeyAuthRequestException error) when (error.Failure == ResponsesKeyAuthRequestFailure.InvalidConfiguration) { rejected = true; }
        Check(rejected, "Malformed samplingParamsByThinkingLevel was admitted.");

        // Native provider composition binds the catalog metadata field and the per-request thinking level.
        var bodies = new List<string>();
        using var handler = new Handler(async request =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync());
            return Sse("""{"type":"response.completed","response":{"id":"r","status":"completed","output":[],"usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}}""");
        });
        var metadata = JsonData.Parse("""{"id":"responses-model","name":"Responses","api":"openai-responses","provider":"openai","baseUrl":"https://api.openai.com/v1","reasoning":true,"input":["text"],"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0},"contextWindow":1000,"maxTokens":100""" + ByLevel + "}");
        using var provider = NativeProviderFactory.CreateResponses(model, new("https://api.openai.com/v1/responses"), Key, new(true), null, handler, metadata);
        _ = await new ChatClient(provider).CompleteAsync(new ChatRequest(model, [Ask], 1) { ThinkingLevel = "high" }).WaitAsync(Deadline);
        _ = await new ChatClient(provider).CompleteAsync(new ChatRequest(model, [Ask], 1) { ThinkingLevel = "off" }).WaitAsync(Deadline);
        Equal(2, bodies.Count);
        BodyEqual(prefix + reasoning + ""","temperature":1,"top_p":0.9,"top_k":40}""", bodies[0]);
        BodyEqual(prefix + ""","reasoning":{"effort":"none"},"temperature":0.2,"top_p":0.9}""", bodies[1]);
    }

    private static Task AzureSampling()
    {
        var model = new ModelDescriptor("azure-model", "azure-openai-responses", "azure");
        var metadata = JsonData.Parse("""{"id":"azure-model","api":"azure-openai-responses","provider":"azure","baseUrl":"https://r.openai.azure.com/openai/v1","reasoning":true""" + ByLevel + "}");
        string Body(string? effort) => new AzureResponsesRequestFactory(model, new AzureResponsesOptions(metadata, new(true)) { ReasoningEffort = effort })
            .ProjectPayload(new(model, [Ask], 1)).ToString();
        const string prefix = """{"model":"azure-model","input":[{"role":"user","content":[{"type":"input_text","text":"ask"}]}],"stream":true,"store":false""";
        BodyEqual(prefix + ""","reasoning":{"effort":"high","summary":"auto"},"include":["reasoning.encrypted_content"],"temperature":1,"top_p":0.9,"top_k":40}""", Body("high"));
        BodyEqual(prefix + ""","reasoning":{"effort":"none"},"temperature":0.2,"top_p":0.9}""", Body(null));
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- 3. Azure provider rename

    private static Task AzureRename()
    {
        var environment = new ProviderEnvironmentSnapshot(new Dictionary<string, string?> { ["AZURE_OPENAI_API_KEY"] = Key });
        Check(InjectedAuthenticationResolver.FindEnvKeys("azure", environment).SequenceEqual(["AZURE_OPENAI_API_KEY"]), "azure lost AZURE_OPENAI_API_KEY.");
        var resolved = InjectedAuthenticationResolver.GetEnvApiKey("azure", environment);
        Check(resolved.Diagnostic == AuthenticationDiagnostic.Resolved && resolved.Authentication?.Secret == Key &&
            resolved.Authentication.EnvironmentName == "AZURE_OPENAI_API_KEY", "azure did not resolve its unchanged variable.");
        // Upstream drops the old id from the env map without an alias: it is an unknown provider for key discovery.
        Check(InjectedAuthenticationResolver.FindEnvKeys("azure-openai-responses", environment).IsEmpty, "Legacy provider id still finds Azure keys.");
        Equal(AuthenticationDiagnostic.UnknownProvider, InjectedAuthenticationResolver.GetEnvApiKey("azure-openai-responses", environment).Diagnostic);
        // The api id is unchanged. A model that keeps the legacy provider id still projects, but foreign Responses call ids are no
        // longer paired for it (AZURE_TOOL_CALL_PROVIDERS now names "azure"): "azure" keeps the call id and a hashed foreign
        // fc_ item id (openai-responses-shared.ts buildForeignResponsesItemId), the legacy id flattens "call|item" into one call id.
        var foreign = new AssistantMessage("openai-responses", "openai", "foreign-model", 2,
            [new ToolCallContent("call-prev|fc_prev", "inspect", JsonData.Parse("{\"x\":1}"))], TokenUsage.Zero, StopReason.ToolUse);
        string Body(string provider)
        {
            var model = new ModelDescriptor("azure-model", "azure-openai-responses", provider);
            var metadata = JsonData.Parse("{\"id\":\"azure-model\",\"api\":\"azure-openai-responses\",\"provider\":\"" + provider + "\",\"baseUrl\":\"https://r.openai.azure.com/openai/v1\",\"reasoning\":false}");
            return new AzureResponsesRequestFactory(model, new AzureResponsesOptions(metadata, new(false))).ProjectPayload(new(model, [new("assistant", PiWireJson.WriteMessage(foreign)),
                Entry("""{"role":"toolResult","toolCallId":"call-prev|fc_prev","toolName":"inspect","content":[{"type":"text","text":"done"}],"isError":false,"timestamp":3}""")], 1)).ToString();
        }
        const string template = """{"model":"azure-model","input":[{"type":"function_call","call_id":"CALL","name":"inspect","arguments":"{\"x\":1}"ITEM},{"type":"function_call_output","call_id":"CALL","output":"done"}],"stream":true,"store":false}""";
        BodyEqual(template.Replace("CALL", "call-prev", StringComparison.Ordinal).Replace("ITEM", ",\"id\":\"fc_n1cvm3dhq11\"", StringComparison.Ordinal), Body("azure"));
        BodyEqual(template.Replace("CALL", "call-prev_fc_prev", StringComparison.Ordinal).Replace("ITEM", "", StringComparison.Ordinal), Body("azure-openai-responses"));
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- 4. Retry and overflow classification

    private static Task RetryClassification()
    {
        foreach (var message in new[]
        {
            """{"type":"error","error":{"type":"server_busy","message":"Please slow down"}}""",
            "Our servers are currently busy. Please wait.",
            "Selected model is at capacity. Please try a different model.",
            "The pending stream has been canceled (caused by: ERR_HTTP2_STREAM_CANCEL)",
            "Provider stopped with: error (server error)"
        })
            Check(AgentRetryPolicy.IsRetryableError(StopReason.Error, message), "Not retried: " + message);
        // Permanent limits still win, unrelated text and non-errors stay terminal, and the pre-1.1.0 Mistral text was not retryable.
        foreach (var (reason, message) in new[] { (StopReason.Error, "server_busy: insufficient_quota"), (StopReason.Error, "Provider stopped with: error"),
            (StopReason.Error, "Invalid request body"), (StopReason.Stop, "server_busy") })
            Check(!AgentRetryPolicy.IsRetryableError(reason, message), "Unexpectedly retried: " + message);
        return Task.CompletedTask;
    }

    private static Task OverflowClassification()
    {
        AssistantMessage Failed(string text) => new("openai-completions", "zai-coding-cn", "glm", 1, [], TokenUsage.Zero, StopReason.Error,
            JsonFields.Empty.Set("errorMessage", JsonData.Parse(JsonSerializer.Serialize(text))));
        Check(SessionRecoveryClassifier.IsContextOverflow(Failed("""400 {"error":{"code":"1261","message":"Prompt exceeds max length"}}""")), "Z.AI CN overflow missed.");
        Check(SessionRecoveryClassifier.IsContextOverflow(Failed("""{"code":"1261","message":"Prompt too long"}""")), "Z.AI overflow missed.");
        Check(!SessionRecoveryClassifier.IsContextOverflow(Failed("Prompt exceeds rate limit")), "Rate limit classified as overflow.");
        Check(!AgentRetryPolicy.IsRetryableError(StopReason.Error, "Prompt exceeds max length"), "Overflow classified as transient.");
        return Task.CompletedTask;
    }

    private static async Task MistralFinishError()
    {
        var model = new ModelDescriptor("mistral-fixture", "mistral-conversations", "mistral");
        var endpoint = new Uri("https://api.mistral.ai/");
        foreach (var (finish, expected, retryable) in new[] { ("error", "Provider stopped with: error (server error)", true), ("content_filter", "Provider stopped with: content_filter", false) })
        {
            using var handler = new Handler(_ => Task.FromResult(Sse("{\"id\":\"m\",\"choices\":[{\"delta\":{\"content\":\"partial\"},\"finish_reason\":\"" + finish + "\"}]}")));
            using var provider = NativeProviderFactory.CreateMistral(model, endpoint, Key, new MistralTextOptions(endpoint, true, new(0, 0, 0, 0), "offline-fixture"), handler);
            var result = await new ChatClient(provider).CompleteAsync(new(model, [Ask], 1)).WaitAsync(Deadline);
            Equal(StopReason.Error, result.Message.StopReason);
            var error = result.Message.ExtraProperties!.TryGet("errorMessage", out var value) ? value!.Value.GetString() : null;
            Equal(expected, error);
            Equal(retryable, AgentRetryPolicy.IsRetryableError(result.Message.StopReason, error));
        }
    }

    private static async Task CompletionsBackoff()
    {
        var model = new ModelDescriptor("retry-model", "openai-completions", "fixture");
        var factory = new CompletionsKeyAuthRequestFactory(new("https://retry.invalid/v1/chat/completions"), model);
        const string success = "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        foreach (var (statuses, headers, noRetry, expected) in new (int[], (string, string)[], int[], double?[])[]
        {
            // Pi 0.99.2: unparseable dates and non-finite values fall back to exponential backoff (null = 375..500 ms jitter window).
            ([429, 200], [("retry-after", "invalid-date")], [], [null]),
            ([429, 200], [("retry-after-ms", "Infinity")], [], [null]),
            ([429, 200], [("retry-after-ms", "-Infinity")], [], [null]),
            ([429, 200], [("retry-after", "Infinity")], [], [null]),
            // Finite server delays still apply: a parseable date and a millisecond prefix.
            ([429, 200], [("retry-after", "Sat, 03 Oct 2026 00:00:01 GMT")], [], [1000]),
            ([500, 200], [("retry-after-ms", "5")], [503], [5]),
            // noRetryStatuses fails at once, even when x-should-retry asks for a retry.
            ([503], [("x-should-retry", "true"), ("retry-after-ms", "5")], [503], []),
        })
        {
            var clock = new Clock(); var decisions = new List<CompletionsRetryObservation>(); var sends = 0;
            using var handler = new Handler(_ =>
            {
                var status = statuses[sends++];
                var response = status == 200 ? Sse(null, success) : new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("{\"error\":{\"message\":\"busy\"}}") };
                if (status != 200) foreach (var (name, value) in headers) response.Headers.TryAddWithoutValidation(name, value);
                return Task.FromResult(response);
            });
            using var client = new HttpClient(handler);
            var transport = CompletionsHttpSseTransport.FromAsyncRequestFactory(client, (request, token) => ValueTask.FromResult(factory.Create(request, Key, token)),
                new CompletionsHttpSseOptions { Retry = new CompletionsRetryOptions(1) { TimeProvider = clock, OnRetry = decisions.Add, NoRetryStatuses = [.. noRetry] } });
            var pending = new ChatClient(transport).CompleteAsync(new(model, [Ask], 1));
            var timers = new List<TimeSpan>();
            foreach (var _ in expected) timers.Add(await clock.FireNextAsync());
            var result = await pending.WaitAsync(Deadline);
            Equal(expected.Length == 0 ? StopReason.Error : StopReason.Stop, result.Message.StopReason);
            Equal(statuses.Length, sends); Equal(expected.Length, decisions.Count);
            for (var index = 0; index < expected.Length; index++)
            {
                var delay = decisions[index].Delay.TotalMilliseconds;
                Check(expected[index] is { } exact ? delay == exact : delay is >= 375 and <= 500, "Unexpected backoff " + delay);
                Timer(decisions[index].Delay, timers[index]);
            }
        }
    }

    private static readonly ModelDescriptor Gemini = new("gemini-3-flash-preview", "google-generative-ai", "google");
    private static JsonData GoogleMetadata(double window = 100000, double maximum = 1000, string headers = "") => JsonData.Parse(
        """{"type":"chat","id":"gemini-3-flash-preview","api":"google-generative-ai","provider":"google","name":"authored","baseUrl":"https://google.invalid/v1beta","reasoning":false,"input":["text","image"],"contextWindow":""" +
        window.ToString(System.Globalization.CultureInfo.InvariantCulture) + ""","maxTokens":""" + maximum.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        ""","cost":{"input":2,"output":3,"cacheRead":0.5,"cacheWrite":0}""" + headers + "}");

    private static async Task GoogleBackoff()
    {
        const string success = """{"candidates":[{"content":{"parts":[{"text":"owned"}]},"finishReason":"STOP"}]}""";
        foreach (var (statuses, headers, noRetry, expected) in new (int[], (string, string)[], int[], double[])[]
        {
            // Jitter sample 0.5: 500 ms * (1 - 0.5 * 0.25) = 437.5 ms.
            ([429, 200], [("retry-after", "invalid-date")], [], [437.5]),
            ([429, 200], [("retry-after", "Infinity")], [], [437.5]),
            ([429, 200], [("retry-after-ms", "Infinity")], [], [437.5]),
            ([429, 200], [("retry-after", "2")], [503], [2000]),
            ([429], [("x-should-retry", "true")], [429], []),
        })
        {
            var clock = new Clock(); var decisions = new List<GoogleRetryObservation>(); var sends = 0;
            using var handler = new Handler(_ =>
            {
                var status = statuses[sends++];
                var response = status == 200 ? Sse(null, "data: " + success + "\n\n") : new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("{\"error\":{\"message\":\"busy\"}}") };
                if (status != 200) foreach (var (name, value) in headers) response.Headers.TryAddWithoutValidation(name, value);
                return Task.FromResult(response);
            });
            using var client = new HttpClient(handler);
            var transport = new GoogleGenerativeAIHttpTransport(client, Gemini, new GoogleGenerativeAIOptions(GoogleMetadata(), Key)
            { MaxRetries = 1, RetryTimeProvider = clock, RetryJitterSample = () => 0.5, NoRetryStatuses = [.. noRetry], Hooks = new() { OnRetry = decisions.Add } });
            var pending = new ChatClient(transport).CompleteAsync(new(Gemini, [Ask], 1));
            var timers = new List<TimeSpan>();
            foreach (var _ in expected) timers.Add(await clock.FireNextAsync());
            var result = await pending.WaitAsync(Deadline);
            Equal(expected.Length == 0 ? StopReason.Error : StopReason.Stop, result.Message.StopReason);
            Equal(statuses.Length, sends); Equal(expected.Length, decisions.Count);
            for (var index = 0; index < expected.Length; index++) { Equal(expected[index], decisions[index].Delay.TotalMilliseconds); Timer(decisions[index].Delay, timers[index]); }
        }
    }

    // ---------------------------------------------------------------- 5. Token estimate

    private static Task TokenEstimates()
    {
        // 36 text characters: ceil(36 / 3.5) = 11 (was 9 at four characters per token).
        // Text plus image: ceil((8 + 4800) / 3.5) = 1374 (was 1202).
        // Tool call: ceil((4 + 12) / 3.5) = 5 (was 4). Total 1390; 10000 - 1390 - 4096 = 4514 output tokens.
        var call = new AssistantMessage("fixture", "fixture", "other", 2, [new ToolCallContent("c", "read", JsonData.Parse("""{"path":"a"}"""))], TokenUsage.Zero, StopReason.ToolUse);
        ImmutableArray<TranscriptEntry> Messages() => [Entry("{\"role\":\"user\",\"content\":\"" + new string('x', 36) + "\",\"timestamp\":1}"),
            Entry("""{"role":"user","content":[{"type":"text","text":"abcdefgh"},{"type":"image","mimeType":"image/png","data":"AA=="}],"timestamp":2}"""),
            new("assistant", PiWireJson.WriteMessage(call))];
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("No HTTP in resolution fixture.")));

        var anthropicMetadata = JsonData.Parse("""{"id":"fixture-haiku","api":"anthropic-messages","provider":"anthropic","contextWindow":10000,"maxTokens":8000,"reasoning":false}""");
        var anthropic = new AnthropicMessagesSimpleRequestFactory(client, new("https://anthropic.invalid"), Haiku, new(anthropicMetadata, new(8000)) { ApiKey = Key })
            .Resolve(new(Haiku, Messages(), 3));
        Equal(1390, anthropic.ContextEstimate.Tokens); Equal(4514, anthropic.MaxTokens);

        var google = new GoogleSimpleRequestFactory(client, Gemini, new(new GoogleGenerativeAIOptions(GoogleMetadata(10000, 8000), Key))).Resolve(new(Gemini, Messages(), 3));
        Equal(1390d, google.ContextEstimate.Tokens); Equal(4514d, google.MaxTokens);

        var mistralModel = new ModelDescriptor("mistral-fixture", "mistral-conversations", "mistral");
        var mistralMetadata = JsonData.Parse("""{"id":"mistral-fixture","api":"mistral-conversations","provider":"mistral","contextWindow":10000,"maxTokens":8000,"reasoning":false}""");
        var mistral = new MistralSimpleHttpSseTransport(client, mistralModel, mistralMetadata,
            new MistralTextOptions(new("https://api.mistral.ai/"), true, new(0, 0, 0, 0), "offline-fixture") { ApiKey = Key }).Resolve(new(mistralModel, Messages(), 3));
        Equal(new MistralSimpleResolution(1390, 4514), mistral);
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- 6. Google user agent

    private static async Task GoogleUserAgent()
    {
        // Decision: keep the pinned product token. Upstream's getPiUserAgent() (unchanged blob 93b23dd at both refs) reports the
        // host OS ("pi (<platform> <release>; <arch>)"); model and caller headers override it in both implementations.
        async Task<string> Agent(JsonData metadata, string? headers = null)
        {
            var options = new GoogleGenerativeAIOptions(metadata, Key) { Headers = headers is null ? null : JsonData.Parse(headers) };
            using var request = await new GoogleKeyAuthRequestFactory(Gemini, options).CreateAsync(new(Gemini, [Ask], 1));
            return string.Join(" ", request.Headers.GetValues("User-Agent"));
        }
        Equal("pi/1.1.0", await Agent(GoogleMetadata()));
        Equal("model-agent", await Agent(GoogleMetadata(headers: ""","headers":{"User-Agent":"model-agent"}""")));
        Equal("caller-agent", await Agent(GoogleMetadata(headers: ""","headers":{"User-Agent":"model-agent"}"""), """{"user-agent":"caller-agent"}"""));
    }

    // ---------------------------------------------------------------- 7. Catalog shards

    private static Task Catalog()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PiSharp.slnx"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Repository root (PiSharp.slnx) not found.");
        FrozenModelCatalog Shard(string provider, string sha256)
        {
            var bytes = File.ReadAllBytes(Path.Combine(root.FullName, "src", "PiSharp.Cli", "Models", provider + ".json"));
            Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            var catalog = FrozenModelCatalog.ReadProviderJson(provider, bytes);
            Check(catalog.Models.All(model => model.Provider == provider), "Shard contains a foreign provider identity.");
            return catalog;
        }
        // Byte-for-byte package/dist/providers/data shards of @earendil-works/pi-ai@1.1.0 (tarball SHA256 6caab33c...d829).
        var anthropic = Shard("anthropic", "aa4342dfb96feb1619794113619d6630088d6ac544c547a4f9899a0a7f26419b");
        var openai = Shard("openai", "f4c1ac9f8f84cb9f2a952b0ceec51c90a38b31b4cdf33200e018ece9d408e95f");
        var openrouter = Shard("openrouter", "c86aa3b95d412465dac54cb902402cbdb40f47a1fe33f12b002913834724d8f0");
        _ = Shard("mistral", "10f33bff9adf1248f7e6848e5890c265399f1f94e5b42cfdc28c109d547af98d"); // Separate v0.99.1 source provenance, unchanged.
        Check(anthropic.TryGetModel(CatalogModelType.Chat, "claude-haiku-5-5", out var haiku), "Claude Haiku 5.5 missing.");
        Equal("anthropic-messages", haiku!.DeclaredApi);
        Equal("""[{"inputTokensAbove":100000,"input":0.5,"output":2.5,"cacheRead":0.05,"cacheWrite":0.625}]""", haiku.Cost.Value.GetProperty("tiers").GetRawText());
        Check(haiku.Raw.Value.GetProperty("compat").GetProperty("supportsMidConvoToolChanges").GetBoolean(), "Haiku 5.5 lost native tool changes.");
        Check(openai.TryGetModel(CatalogModelType.Classifier, "gpt-6-luna", out var luna), "GPT-6 Luna classifier missing.");
        Equal("openai-decisions", luna!.DeclaredApi);
        Check(openrouter.TryGetModel(CatalogModelType.Chat, "anthropic/claude-haiku-5.5", out _), "OpenRouter Haiku 5.5 missing.");
        Check(anthropic.TryGetModel(CatalogModelType.Chat, "claude-sonnet-4-5", out _) && openai.TryGetModel(CatalogModelType.Chat, "gpt-4o", out _),
            "Models used by existing CLI selections disappeared.");
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- helpers

    private static TranscriptEntry Entry(string json) { var owned = JsonData.Parse(json); return new(owned.Value.GetProperty("role").GetString()!, owned); }
    private static TranscriptEntry AnthropicAssistant(StopReason reason, params AssistantContent[] content) =>
        new("assistant", PiWireJson.WriteMessage(new("anthropic-messages", "anthropic", "fixture-haiku", 2, content.ToImmutableArray(), TokenUsage.Zero, reason)));
    private static HttpResponseMessage Sse(string? dto, string? raw = null) => new(HttpStatusCode.OK)
    { Content = new StringContent(raw ?? "data: " + dto + "\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
    private static void BodyEqual(string expected, string actual) { bodyComparisons++; Equal(expected, actual); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}{Environment.NewLine}Actual   {actual}"); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    // The owned timer receives the backoff at whole-millisecond resolution.
    private static void Timer(TimeSpan decided, TimeSpan due) => Check(due <= decided && decided - due < TimeSpan.FromMilliseconds(1), $"Timer {due} differs from backoff {decided}.");
    private static void ThrowsAnthropic(AnthropicRequestFailure failure, Action action)
    {
        try { action(); } catch (AnthropicRequestException error) when (error.Failure == failure) { return; }
        throw new InvalidOperationException("Expected " + failure);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request);
    }

    /// <summary>Controlled clock: timers wait until the test fires them and report their requested delay.</summary>
    private sealed class Clock : TimeProvider
    {
        private readonly Channel<(ManualTimer Timer, TimeSpan Due)> _pending = Channel.CreateUnbounded<(ManualTimer, TimeSpan)>();
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new ManualTimer(callback, state); _pending.Writer.TryWrite((timer, dueTime)); return timer; }
        public async Task<TimeSpan> FireNextAsync()
        { var (timer, due) = await _pending.Reader.ReadAsync().AsTask().WaitAsync(Deadline); timer.Fire(); return due; }
        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private int _disposed;
            public void Fire() { if (Volatile.Read(ref _disposed) == 0) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
