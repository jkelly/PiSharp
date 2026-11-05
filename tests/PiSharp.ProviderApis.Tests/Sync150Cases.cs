// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/ai/src/api/bedrock-converse-stream.ts, api/transform-messages.ts,
// api/constrained-sampling.ts, api/openai-responses.ts, api/openai-codex-responses.ts, utils/overflow.ts, utils/identity.ts, auth/oauth/radius.ts and
// auth/oauth/trusted-url.ts.
using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.AI.Protocols.Bedrock;
using PiSharp.AI.Protocols.OpenAICodexResponses;
using PiSharp.AI.Providers;
using PiSharp.CodingAgent;
using PiSharp.Contracts;

// Pi 1.5.0 sync (ai-providers). Authored expectations derived by reading Pi v1.5.0 source and test text (packages/ai/test/
// bedrock-convert-messages.test.ts, bedrock-strict-tool-fallback.test.ts, github-copilot-request-size-overflow.test.ts, radius-oauth.test.ts and identity.test.ts). Nothing here
// executes upstream code or is an upstream capture; every HTTP peer is an in-process fake.
// Register with: cases = [.. cases, .. Sync150Cases].
internal static partial class Program
{
    internal static readonly (string Id, Func<Task> Run)[] Sync150Cases =
    [
        ("sync150.copilot-responses-http-413-is-context-overflow", Sync150CopilotRequestSize),
        ("sync150.bedrock-claude-error-tool-result-images-follow-the-results", Sync150BedrockErrorImages),
        ("sync150.bedrock-strict-tool-limit-rejection-retries-preferred-tools-once", Sync150BedrockStrictFallback),
        ("sync150.radius-oauth-trusts-only-http-urls", Sync150RadiusTrustedUrls),
        ("sync150.identity-bedrock-and-codex-user-agents", Sync150IdentityUserAgents),
    ];

    private static string Sync150Assistant(string model, string content, string stopReason = "toolUse") =>
        "{\"role\":\"assistant\",\"content\":" + content + ",\"api\":\"bedrock-converse-stream\",\"provider\":\"amazon-bedrock\",\"model\":\"" + model + "\",\"usage\":" +
        """{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"totalTokens":0,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0}}""" +
        ",\"stopReason\":\"" + stopReason + "\",\"timestamp\":2}";

    // Copilot answers a request over its byte limit with HTTP 413 "failed to parse request"; any 413 is a context overflow, a 400 is not.
    private static async Task Sync150CopilotRequestSize()
    {
        const string error = """{"message":"failed to parse request","code":""}""";
        foreach (var (status, nested, overflow) in new[] { (413, true, true), (413, false, true), (400, true, false) })
        {
            var row = CatalogRow("github-copilot", "gpt-5.6-sol"); var http = new FakeHttp();
            http.OnUrl("https://", _ => Json(nested ? "{\"error\":" + error + "}" : error, (HttpStatusCode)status));
            using var route = NativeProviderFactory.CreateProviderRoute(Descriptor(row), row.Raw, new(_ => ValueTask.FromResult(new ProviderRequestAuth(CopilotToken,
                ProviderHeaderPolicies.CopilotBaseUrl(CopilotToken, null)))) { MaxTokens = 512 }, http);
            var terminal = (StreamTerminalEvent)(await Collect(route, new(Descriptor(row), [Entry("""{"role":"user","content":"Continue","timestamp":0}""")], 1)))[^1];
            var text = ErrorMessage(terminal);
            Equal(1, http.All.Count, status + " attempts");
            Check(terminal.Message.StopReason == StopReason.Error && terminal.Message.Provider == "github-copilot" && terminal.Message.Usage.TotalTokens == 0, status + " terminal");
            Check(text.Contains("(" + status + ")", StringComparison.Ordinal) && text.Contains("failed to parse request", StringComparison.Ordinal), status + " text: " + text);
            Equal(overflow, SessionRecoveryClassifier.IsContextOverflow(terminal.Message, 1_000_000), status + " nested=" + nested + " overflow: " + text);
        }
    }

    // #10794: Claude on Bedrock rejects error tool results that contain images; other models keep them.
    private static async Task Sync150BedrockErrorImages()
    {
        static string Image(string data) => "{\"image\":{\"format\":\"png\",\"source\":{\"bytes\":\"" + data + "\"}}}";
        var claude = Bedrock(SonnetRow, options: new() { CacheRetention = "none" });
        using (var request = await claude.Transport.CreateRequestAsync(new(claude.Model, [
            Entry("""{"role":"user","content":"Show the screenshots","timestamp":1}"""),
            Entry(Sync150Assistant(claude.Model.Id, """[{"type":"toolCall","id":"call-1","name":"codemode","arguments":{}},{"type":"toolCall","id":"call-2","name":"read","arguments":{}}]""")),
            Entry("""{"role":"toolResult","toolCallId":"call-1","toolName":"codemode","content":[{"type":"text","text":"Script failed"},{"type":"image","data":"AAAA","mimeType":"image/png"},{"type":"image","data":"CCCC","mimeType":"image/png"},{"type":"text","text":"Script error"}],"isError":true,"timestamp":3}"""),
            Entry("""{"role":"toolResult","toolCallId":"call-2","toolName":"read","content":[{"type":"image","data":"BBBB","mimeType":"image/png"}],"isError":false,"timestamp":4}""")], 5)))
        {
            var messages = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!["messages"]!;
            JsonEqual("""{"role":"user","content":[{"toolResult":{"toolUseId":"call-1","content":[{"text":"Script failed"},{"text":"(image attached after the tool results)"},{"text":"Script error"}],"status":"error"}},""" +
                """{"toolResult":{"toolUseId":"call-2","content":[""" + Image("BBBB") + """],"status":"success"}},{"text":"Images from the error result of tool call call-1 (codemode):"},""" +
                Image("AAAA") + "," + Image("CCCC") + "]}", messages[2]!.ToJsonString(), "claude error images after the results");
        }
        var nova = Bedrock(NovaRow, options: new() { CacheRetention = "none" });
        using (var request = await nova.Transport.CreateRequestAsync(new(nova.Model, [
            Entry("""{"role":"user","content":"Run it","timestamp":1}"""),
            Entry(Sync150Assistant(nova.Model.Id, """[{"type":"toolCall","id":"call-1","name":"codemode","arguments":{}}]""")),
            Entry("""{"role":"toolResult","toolCallId":"call-1","toolName":"codemode","content":[{"type":"text","text":"Script failed"},{"type":"image","data":"AAAA","mimeType":"image/png"}],"isError":true,"timestamp":3}""")], 4)))
        {
            var messages = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!["messages"]!;
            JsonEqual("""{"role":"user","content":[{"toolResult":{"toolUseId":"call-1","content":[{"text":"Script failed"},""" + Image("AAAA") + """],"status":"error"}}]}""",
                messages[2]!.ToJsonString(), "nova keeps error images");
        }
    }

    // #11416: Bedrock rejects strict tool sets over Claude's limits with a ValidationException before the stream starts; the request is
    // sent once more with strict "prefer" tools non-strict ("require" tools stay strict) and a bedrock_strict_tools_fallback diagnostic.
    private static async Task Sync150BedrockStrictFallback()
    {
        const string row = """{"id":"us.anthropic.claude-opus-5-5","name":"Claude Opus 5.5 (US)","api":"bedrock-converse-stream","provider":"amazon-bedrock","baseUrl":"https://bedrock-runtime.us-east-1.amazonaws.com","reasoning":false,"input":["text"],"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0},"contextWindow":200000,"maxTokens":100,"compat":{"supportsStrictMode":true},"type":"chat"}""";
        const string union = "The model returned the following errors: Schemas contains too many parameters with union types (20 parameters with type arrays or anyOf). This causes exponential compilation cost. Reduce the number of nullable or union-typed parameters (limit: 16 parameters with unions).";
        const string parameters = """{"type":"object","properties":{"value":{"type":"string"},"optional":{"type":"number"}},"required":["value"]}""";
        static string Tool(string name, string? strict) => "{\"name\":\"" + name + "\",\"description\":\"Look up a value\",\"parameters\":" + parameters +
            (strict is null ? "" : ",\"constrainedSampling\":{\"type\":\"json_schema\",\"strict\":\"" + strict + "\"}") + "}";
        static ImmutableArray<TranscriptEntry> Messages(params string[] tools) =>
            [Entry("{\"role\":\"system\",\"content\":\"\",\"toolsAdded\":[" + string.Join(",", tools) + "],\"timestamp\":0}"), Entry("""{"role":"user","content":"Hello","timestamp":1}""")];
        static HttpResponseMessage Rejected(string message) => new(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { message })),
            Headers = { { "x-amzn-ErrorType", "ValidationException:http://internal.amazon.com/coral/com.amazon.bedrock/" }, { "x-amzn-RequestId", "rid-400" } }
        };
        static HttpResponseMessage Served() => EventStream(messages: [EventMessage("messageStart", """{"role":"assistant"}"""), EventMessage("messageStop", """{"stopReason":"end_turn"}""")]);
        const string strictSchema = """{"json":{"type":"object","properties":{"value":{"type":"string"},"optional":{"anyOf":[{"type":"number"},{"type":"null"}]}},"required":["value","optional"],"additionalProperties":false}}""";
        static string Spec(string name, string schema, bool? strict) => "{\"toolSpec\":{\"name\":\"" + name + "\",\"inputSchema\":" + schema +
            ",\"description\":\"Look up a value\"" + (strict is null ? "" : ",\"strict\":true") + "}}";

        // A strict-limit rejection: one more request with the preferred tool non-strict; the payload hook runs for both.
        var payloads = 0;
        var fixture = Bedrock(row, options: new()
        {
            CacheRetention = "none",
            OnPayload = (payload, _, _) =>
            {
                payloads++;
                var edited = JsonNode.Parse(payload.ToString())!.AsObject(); edited["requestMetadata"] = new JsonObject { ["team"] = "a" };
                return ValueTask.FromResult<JsonData?>(JsonData.Parse(edited.ToJsonString()));
            }
        });
        var attempt = 0;
        fixture.Http.OnUrl("https://", _ => ++attempt == 1 ? Rejected(union) : Served());
        var events = await Collect(fixture.Transport, new(fixture.Model, Messages(Tool("preferred", "prefer"), Tool("required", "require"), Tool("ordinary", null)), 7));
        Check(events[^1] is StreamDone { Reason: StopReason.Stop }, "fallback request served: " + ErrorMessage(events[^1]));
        Equal(2, payloads, "payload hook calls");
        var sent = fixture.Http.All.Select(request => JsonNode.Parse(request.Body)!).ToArray();
        Equal(2, sent.Length, "requests");
        JsonEqual("[" + Spec("preferred", strictSchema, true) + "," + Spec("required", strictSchema, true) + "," + Spec("ordinary", "{\"json\":" + parameters + "}", null) + "]",
            sent[0]["toolConfig"]!["tools"]!.ToJsonString(), "first tools");
        JsonEqual("[" + Spec("preferred", "{\"json\":" + parameters + "}", null) + "," + Spec("required", strictSchema, true) + "," + Spec("ordinary", "{\"json\":" + parameters + "}", null) + "]",
            sent[1]["toolConfig"]!["tools"]!.ToJsonString(), "fallback tools");
        JsonEqual("""{"team":"a"}""", sent[1]["requestMetadata"]!.ToJsonString(), "hooked fallback payload");
        Check(sent.All(request => request["toolConfig"]!.AsObject().Count == 1), "no tool choice");
        JsonEqual("[{\"type\":\"bedrock_strict_tools_fallback\",\"timestamp\":" + BedrockTime.ToUnixTimeMilliseconds() + ",\"error\":{\"message\":" + JsonSerializer.Serialize(union) +
            "},\"details\":{\"reason\":\"strict_tool_limits_exceeded\"}}]", ((StreamDone)events[^1]).Message.ExtraProperties!.Values["diagnostics"].ToString(), "fallback diagnostic");

        // No preferred strict tool: no retry, the modeled message is reported.
        var required = Bedrock(row, options: new() { CacheRetention = "none" });
        required.Http.OnUrl("https://", _ => Rejected(union));
        var refused = await Collect(required.Transport, new(required.Model, Messages(Tool("required", "require"), Tool("ordinary", null)), 7));
        Equal("Validation error: " + union, ErrorMessage(refused[^1]), "required-only rejection");
        Equal(1, required.Http.All.Count, "required-only attempts");

        // Another validation error is not a strict-limit rejection.
        var other = Bedrock(row, options: new() { CacheRetention = "none" });
        other.Http.OnUrl("https://", _ => Rejected("messages: field required"));
        Check((await Collect(other.Transport, new(other.Model, Messages(Tool("preferred", "prefer")), 7)))[^1] is StreamError, "other validation error");
        Equal(1, other.Http.All.Count, "other validation attempts");

        // Only once: a second rejection fails the turn, keeping both diagnostics.
        var twice = Bedrock(row, options: new() { CacheRetention = "none" });
        twice.Http.OnUrl("https://", _ => Rejected(union));
        var failed = (StreamError)(await Collect(twice.Transport, new(twice.Model, Messages(Tool("preferred", "prefer")), 7)))[^1];
        Equal(2, twice.Http.All.Count, "second rejection attempts");
        Equal("Validation error: " + union, ErrorMessage(failed), "second rejection message");
        Equal("bedrock_strict_tools_fallback,bedrock_response_failure", string.Join(",", JsonNode.Parse(failed.Message.ExtraProperties!.Values["diagnostics"].ToString())!
            .AsArray().Select(item => (string?)item!["type"])), "second rejection diagnostics");
    }

    // Login flows open server-supplied URLs with the platform handler, so Radius now accepts only normalized http(s) URLs for its
    // discovered authorization endpoint and its device verification_uri, like Copilot, Kimi and Meta.
    private static async Task Sync150RadiusTrustedUrls()
    {
        const string payload = "file:///Users/victim/Downloads/payload.app";
        var time = new FixedTime(1_800_000_000_000);
        var browser = new FakeHttp().OnUrl("https://radius.example/v1/oauth", _ => Json("{\"authorizationEndpoint\":\"" + payload + "\"}"));
        var browserInteraction = new ScriptedInteraction((prompt, _) => prompt.Kind == AuthPromptKind.Select ? "browser" : "");
        Equal("Invalid Radius OAuth config from https://radius.example", (await Throws<InvalidOperationException>(() =>
            new RadiusOAuth(new HttpMessageInvoker(browser, disposeHandler: false), "Radius", "radius.example", time).LoginAsync(browserInteraction, null, default))).Message,
            "untrusted authorization endpoint");
        Check(browserInteraction.Events.IsEmpty, "no event before the rejected endpoint");
        var missing = new FakeHttp().OnUrl("https://radius.example/v1/oauth", _ => Json("""{"authorizationEndpoint":42}"""));
        Equal("Invalid Radius OAuth config from https://radius.example", (await Throws<InvalidOperationException>(() =>
            new RadiusOAuth(new HttpMessageInvoker(missing, disposeHandler: false), "Radius", "radius.example", time).LoginAsync(browserInteraction, null, default))).Message,
            "non-string authorization endpoint");

        var device = new FakeHttp().OnUrl("https://radius.example/v1/oauth/device", _ => Json("{\"device_code\":\"device-code\",\"user_code\":\"ABCD-1234\",\"verification_uri\":\"" + payload + "\",\"expires_in\":600}"));
        var deviceInteraction = new ScriptedInteraction((prompt, _) => prompt.Kind == AuthPromptKind.Select ? "device-code" : "");
        Equal("Untrusted verification_uri in Radius OAuth device authorization response", (await Throws<InvalidOperationException>(() =>
            new RadiusOAuth(new HttpMessageInvoker(device, disposeHandler: false), "Radius", "radius.example", time).LoginAsync(deviceInteraction, null, default))).Message,
            "untrusted verification_uri");
        Check(deviceInteraction.Events.IsEmpty, "no device code event for the rejected verification_uri");
        // A missing field is still reported first.
        var incomplete = new FakeHttp().OnUrl("https://radius.example/v1/oauth/device", _ => Json("{\"device_code\":\"device-code\",\"user_code\":\"ABCD-1234\",\"verification_uri\":\"" + payload + "\"}"));
        Equal("Radius OAuth device authorization response is missing required fields", (await Throws<InvalidOperationException>(() =>
            new RadiusOAuth(new HttpMessageInvoker(incomplete, disposeHandler: false), "Radius", "radius.example", time).LoginAsync(deviceInteraction, null, default))).Message,
            "missing expires_in");

        // A trusted verification_uri is shown normalized.
        var trusted = new FakeHttp()
            .OnUrl("https://radius.example/v1/oauth/device", _ => Json("""{"device_code":"rd","user_code":"RAD","verification_uri":"HTTPS://Radius.Example/d","expires_in":60,"interval":1}"""))
            .OnUrl("https://radius.example/v1/oauth/token", _ => Json("""{"access_token":"radius-1","refresh_token":"rr","expires_in":600}"""));
        var trustedInteraction = new ScriptedInteraction((prompt, _) => prompt.Kind == AuthPromptKind.Select ? "device-code" : "");
        var credential = await new RadiusOAuth(new HttpMessageInvoker(trusted, disposeHandler: false), "Radius", "radius.example", time).LoginAsync(trustedInteraction, null, default);
        Equal("radius-1", credential.Access, "trusted login");
        Equal("https://radius.example/d", trustedInteraction.Events.Single(item => item.Kind == AuthEventKind.DeviceCode).VerificationUri, "normalized verification_uri");
    }

    // identity.ts on Bedrock: bedrockUserAgent's customUserAgent pair pi/<version>, ["pi"] (short) or the custom value. PiSharp has no
    // AWS SDK user agent for the pair to extend, so it is the whole User-Agent; like the SDK's it is outside the SigV4 signed headers.
    // Codex sends getIdentityHeaders' User-Agent with originator "pi"; a request identity merges over the configured one.
    private static async Task Sync150IdentityUserAgents()
    {
        var ask = ImmutableArray.Create(Entry("""{"role":"user","content":"Hi","timestamp":1}"""));
        async Task<string> BedrockAgent(BedrockConverseOptions? options, ClientIdentity? requested = null)
        {
            var fixture = Bedrock(SonnetRow, options: (options ?? new()) with { CacheRetention = "none" });
            using var request = await fixture.Transport.CreateRequestAsync(new(fixture.Model, ask, 1) { Identity = requested });
            Check(request.Headers.GetValues("Authorization").Single().Contains("SignedHeaders=content-type;host;x-amz-content-sha256;x-amz-date,", StringComparison.Ordinal),
                "bedrock user agent unsigned");
            return string.Join(" ", request.Headers.GetValues("User-Agent"));
        }
        Equal("pi/1.5.0", await BedrockAgent(null), "bedrock detailed");
        Equal("pi/2.0.0", await BedrockAgent(new() { Identity = new() { Version = "2.0.0" } }), "bedrock version");
        Equal("pi", await BedrockAgent(new() { Identity = new() { Version = "2.0.0" } }, new() { UserAgent = ClientUserAgent.Short }), "bedrock short");
        Equal("acme/1.0", await BedrockAgent(new() { Identity = new() { UserAgent = ClientUserAgent.Custom("acme/1.0") } }), "bedrock custom");

        var (transport, http, model, _) = Codex(new() { Identity = new() { UserAgent = ClientUserAgent.Short } });
        http.OnUrl("https://chatgpt.com/", _ => Sse("""{"type":"response.completed","response":{"id":"r","status":"completed","output":[]}}"""));
        Check((await Collect(transport, new(model, ask, 2)))[^1] is StreamDone, "codex configured identity done");
        Check((await Collect(transport, new(model, ask, 2) { Identity = new() { UserAgent = ClientUserAgent.Custom("acme/1.0") } }))[^1] is StreamDone, "codex request identity done");
        Equal("pi|acme/1.0", string.Join("|", http.All.Select(request => request.Header("user-agent"))), "codex user agents");
        Check(http.All.All(request => request.Header("originator") == "pi"), "codex originator");
        var (explicitTransport, explicitHttp, explicitModel, _) = Codex(new() { UserAgent = "explicit-agent" });
        explicitHttp.OnUrl("https://chatgpt.com/", _ => Sse("""{"type":"response.completed","response":{"id":"r","status":"completed","output":[]}}"""));
        Check((await Collect(explicitTransport, new(explicitModel, ask, 2) { Identity = new() { UserAgent = ClientUserAgent.Short } }))[^1] is StreamDone, "codex explicit done");
        Equal("explicit-agent", explicitHttp.All.Single().Header("user-agent"), "codex explicit user agent");
    }
}
