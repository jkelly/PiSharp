// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/ai/src/utils/retry.ts, api/constrained-sampling.ts, api/mistral-conversations.ts,
// api/anthropic-messages.ts, session-resources.ts, api/openai-completions.ts, api/openai-responses-shared.ts, api/google-shared.ts,
// api/google-generative-ai.ts and api/google-vertex.ts.
using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.AI.Protocols.AzureResponses;
using PiSharp.AI.Protocols.GoogleGenerativeAI;
using PiSharp.AI.Protocols.GoogleVertex;
using PiSharp.AI.Protocols.MistralConversations;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.AI.Protocols.ProviderShared;
using PiSharp.AI.Providers;
using PiSharp.CodingAgent.Configuration;
using PiSharp.Contracts;

// Pi 1.6.0 sync (ai-providers). Authored expectations derived by reading Pi v1.6.0 source and test text (packages/ai/test/retry.test.ts,
// constrained-sampling.test.ts, mistral-tool-schema.test.ts, anthropic-strict-tool-schema.test.ts, anthropic-message-threads.test.ts,
// openai-completions-tool-choice.test.ts, openai-responses-compat.test.ts, providers.test.ts, openai-responses-input-transformations.test.ts,
// google-stop-reason-continuation.test.ts and fetch-option.test.ts). Nothing here executes upstream code or is an upstream capture; every
// HTTP peer is an in-process fake. The catalog flags of Pi 1.6.0 (GitHub Copilot's compat) are not in the 1.5.2 catalog data this branch
// carries, so those cases write the compat field into an authored model row.
internal static partial class Program
{
    private static readonly (string Id, Func<Task> Run)[] Sync160ProviderCases =
    [
        ("sync160.retry-node-socket-codes-connection-closed-and-codex-connection-limit", Sync160RetrySocketCodes),
        ("sync160.retry-dotnet-socket-and-connection-failure-texts", Sync160RetryDotNetSocketTexts),
        ("sync160.strict-untyped-schema-nodes-fall-back-or-fail-on-every-api", Sync160StrictUntypedNodes),
        ("sync160.mistral-strict-tools-are-opt-in", Sync160MistralStrictOptIn),
        ("sync160.anthropic-non-strict-schema-keeps-root-additional-properties", Sync160AnthropicAdditionalProperties),
        ("sync160.anthropic-message-threads-default-by-endpoint-not-provider", Sync160AnthropicThreadsByEndpoint),
        ("sync160.session-resources-release-anthropic-thread-state", Sync160SessionResourcesThreads),
        ("sync160.completions-developer-role-only-for-openai", Sync160CompletionsDeveloperRole),
        ("sync160.completions-top-level-reasoning-tokens-and-copilot-effort", Sync160CompletionsReasoningTokens),
        ("sync160.copilot-responses-explicit-strict-and-claude-strict-tools", Sync160CopilotStrict),
        ("sync160.responses-openai-input-transformations-diagnostic", Sync160ResponsesInputTransformations),
        ("sync160.responses-input-transformations-over-openai-and-azure-http", Sync160ResponsesInputTransformationsHttp),
        ("sync160.google-continuation-finish-ends-the-turn-as-length", Sync160GoogleContinuation),
        ("sync160.google-injected-http-handler-and-continuation-switch", Sync160GoogleInjectedHandler),
        ("sync160.completions-developer-role-default-on-the-route-and-simple-paths", Sync160DeveloperRoleRoutes),
        ("sync160.max-tokens-above-one-million-builds-on-every-request-path", Sync160LargeMaxTokens),
    ];

    // ---------------------------------------------------------------- retry.ts

    private static Task Sync160RetrySocketCodes()
    {
        foreach (var message in new[]
        {
            // #12124: the Codex WebSocket lifetime limit, as text and as code, is recoverable on a fresh connection.
            "Codex error: Responses websocket connection limit reached (60 minutes). Create a new websocket connection to continue.",
            "Codex error: websocket_connection_limit_reached",
            // #12128: bare Node socket error codes.
            "read ECONNRESET", "connect ETIMEDOUT 140.82.113.3:443", "write EPIPE", "connect ECONNREFUSED 127.0.0.1:443", "ECONNABORTED",
            "Connection reset by peer", "econnreset",
            // #12064: a gateway's mid-stream drop relayed as an Anthropic-style api_error event.
            """{"type":"error","error":{"type":"api_error","message":"Connection closed."}}""", "connection-closed", "ConnectionClosed",
        })
            Check(AgentRetryPolicy.IsRetryableError(StopReason.Error, message), "Not retried: " + message);
        foreach (var message in new[]
        {
            "connection limit reached",
            // The codes must not be part of a longer identifier.
            "Invalid value EXECONNRESET", "Unknown field ETIMEDOUTX", "Invalid key ERR_EPIPE_ID", "EPIPE_", "9ECONNRESET",
        })
            Check(!AgentRetryPolicy.IsRetryableError(StopReason.Error, message), "Unexpectedly retried: " + message);
        // Limits stay permanent, and only an error stop is classified.
        Check(!AgentRetryPolicy.IsRetryableError(StopReason.Error, "read ECONNRESET while billing"), "A billing limit was retried.");
        Check(!AgentRetryPolicy.IsRetryableError(StopReason.Stop, "read ECONNRESET") && !AgentRetryPolicy.IsRetryableError(StopReason.Aborted, "Connection closed."),
            "A stop reason other than error was retried.");
        return Task.CompletedTask;
    }

    private static Task Sync160RetryDotNetSocketTexts()
    {
        // PiSharp-only: System.Net reports these where Node reports a bare errno name. Authored from the Windows (Winsock) and Unix
        // (strerror) texts of ECONNRESET, ECONNABORTED, ECONNREFUSED, ETIMEDOUT and EPIPE, and from the framework's own messages.
        foreach (var message in new[]
        {
            "An existing connection was forcibly closed by the remote host.",
            "An established connection was aborted by the software in your host machine.",
            "Software caused connection abort",
            "No connection could be made because the target machine actively refused it. (api.example.com:443)",
            "Connection refused (api.example.com:443)",
            "A connection attempt failed because the connected party did not properly respond after a period of time, or established connection failed because connected host has failed to respond.",
            "Connection timed out", "Operation timed out",
            "Broken pipe",
            "A request to send or receive data was disallowed because the socket had already been shut down in that direction with a previous shutdown call.",
            "Unable to read data from the transport connection: An existing connection was forcibly closed by the remote host..",
            "Unable to write data to the transport connection: Broken pipe.",
            "An error occurred while sending the request.",
            "The response ended prematurely.",
            "The response ended prematurely, with at least 12 additional bytes expected.",
            "The remote party closed the WebSocket connection without completing the close handshake.",
        })
            Check(AgentRetryPolicy.IsRetryableError(StopReason.Error, message), "Not retried: " + message);
        // What this platform's sockets say for the four codes every platform has.
        foreach (var code in new[] { SocketError.ConnectionReset, SocketError.ConnectionAborted, SocketError.ConnectionRefused, SocketError.TimedOut })
        {
            var message = new SocketException((int)code).Message;
            Check(AgentRetryPolicy.IsRetryableError(StopReason.Error, message), $"Not retried: SocketError.{code}: {message}");
            Check(AgentRetryPolicy.IsRetryableError(StopReason.Error, new IOException("Unable to read data from the transport connection: " + message, new SocketException((int)code)).Message),
                $"Not retried: IOException over SocketError.{code}");
        }
        foreach (var message in new[]
        {
            // Not a connection failure: a rejected request, a TLS failure the next attempt would repeat, and unrelated text.
            "The SSL connection could not be established, see inner exception.", "Access to the path is denied.", "pipe", "transport",
            "Invalid Responses transcript projection input.",
        })
            Check(!AgentRetryPolicy.IsRetryableError(StopReason.Error, message), "Unexpectedly retried: " + message);
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- constrained-sampling.ts

    private static TranscriptEntry Sync160Tool(string parameters, string? strict, string name = "t") => Entry(
        "{\"role\":\"system\",\"content\":\"\",\"toolsAdded\":[{\"name\":\"" + name + "\",\"description\":\"D\",\"parameters\":" + parameters +
        (strict is null ? "" : ",\"constrainedSampling\":{\"type\":\"json_schema\",\"strict\":\"" + strict + "\"}") + "}],\"timestamp\":0}");

    private static Task Sync160StrictUntypedNodes()
    {
        // #12049: OpenAI and Anthropic strict mode accept an enum without a type.
        const string enumOnly = """{"type":"object","properties":{"level":{"enum":["low","high"]}},"required":["level"]}""";
        const string enumStrict = """{"type":"object","properties":{"level":{"enum":["low","high"]}},"required":["level"],"additionalProperties":false}""";
        // Type.Optional(Type.Array(Type.Unknown())), Type.Any({ description }) and a bare const: nodes without a type.
        string[] untyped =
        [
            """{"type":"object","properties":{"commandArguments":{"type":"array","items":{}}}}""",
            """{"type":"object","properties":{"value":{"description":"Any JSON value"}},"required":["value"]}""",
            """{"type":"object","properties":{"mode":{"const":"fast"}},"required":["mode"]}""",
            // The rule holds at every depth: array items of a nested object.
            """{"type":"object","properties":{"rows":{"type":"array","items":{"type":"object","properties":{"cell":{}},"required":["cell"]}}},"required":["rows"]}""",
        ];
        var responsesModel = new ModelDescriptor("strict-model", "openai-responses", "openai");
        var completionsModel = new ModelDescriptor("strict-model", "openai-completions", "openai");

        // openai-responses-shared.ts convertResponsesTools (also Azure Responses and Codex).
        var responses = new ResponsesToolDeclarationProjector(new(SupportsStrictMode: true, Strict: false));
        JsonElement Responses(string parameters, string strict) => responses.Project(new(responsesModel, [Sync160Tool(parameters, strict)], 1)).Value[0];
        Check(Responses(enumOnly, "prefer").GetProperty("strict").GetBoolean(), "Responses: an enum-only node lost strict mode.");
        BodyEqual(enumStrict, Responses(enumOnly, "require").GetProperty("parameters").GetRawText());
        // openai-completions.ts convertTools.
        var completions = new CompletionsToolDeclarationProjector(new(SupportsStrictMode: true));
        JsonElement Completions(string parameters, string strict) => completions.Project(new(completionsModel, [Sync160Tool(parameters, strict)], 1)).Value[0].GetProperty("function");
        Check(Completions(enumOnly, "prefer").GetProperty("strict").GetBoolean(), "Completions: an enum-only node lost strict mode.");
        // anthropic-messages.ts convertTools.
        var anthropic = new AnthropicMessagesRequestProjector(new(128, SupportsStrictTools: true, CacheRetention: AnthropicCacheRetention.None));
        JsonElement Anthropic(string parameters, string strict) => anthropic.Project(new(Haiku, [Sync160Tool(parameters, strict)], 1)).Value.GetProperty("tools")[0];
        Check(Anthropic(enumOnly, "prefer").GetProperty("strict").GetBoolean(), "Anthropic: an enum-only node lost strict mode.");
        // google-shared.ts convertTools: a strict tool selects VALIDATED function calling.
        string? GoogleMode(string parameters, string strict)
        {
            var config = GoogleRequestProjector.Project(new(Gemini, [Sync160Tool(parameters, strict), Ask], 1), new GoogleGenerativeAIOptions(GoogleMetadata(), Key)).Value.GetProperty("config");
            return config.TryGetProperty("toolConfig", out var tool) ? tool.GetProperty("functionCallingConfig").GetProperty("mode").GetString() : null;
        }
        Equal("VALIDATED", GoogleMode(enumOnly, "prefer"));
        // bedrock-converse-stream.ts resolveJsonSchemaStrictSampling.
        static JsonObject Declared(string parameters, string strict) => new()
        {
            ["name"] = "t", ["description"] = "D", ["parameters"] = JsonNode.Parse(parameters),
            ["constrainedSampling"] = new JsonObject { ["type"] = "json_schema", ["strict"] = strict }
        };
        Equal<bool?>(true, ProviderTranscript.ResolveStrict(Declared(enumOnly, "prefer"), true));

        foreach (var parameters in untyped)
        {
            // "prefer" falls back to non-strict with the declared schema.
            var response = Responses(parameters, "prefer");
            Check(!response.GetProperty("strict").GetBoolean(), "Responses sent an untyped node strict: " + parameters);
            BodyEqual(parameters, response.GetProperty("parameters").GetRawText());
            var completion = Completions(parameters, "prefer");
            Check(!completion.GetProperty("strict").GetBoolean(), "Completions sent an untyped node strict: " + parameters);
            BodyEqual(parameters, completion.GetProperty("parameters").GetRawText());
            Check(!Anthropic(parameters, "prefer").TryGetProperty("strict", out _), "Anthropic sent an untyped node strict: " + parameters);
            Equal<string?>(null, GoogleMode(parameters, "prefer"));
            Equal<bool?>(null, ProviderTranscript.ResolveStrict(Declared(parameters, "prefer"), true));
            // "require" fails locally, before any request exists.
            try { _ = Responses(parameters, "require"); throw new InvalidOperationException("Responses admitted a required untyped schema: " + parameters); }
            catch (ResponsesProjectionException error) when (error.Failure == ResponsesProjectionFailure.UnsupportedContent) { }
            try { _ = Completions(parameters, "require"); throw new InvalidOperationException("Completions admitted a required untyped schema: " + parameters); }
            catch (CompletionsRequestException error) when (error.Failure == CompletionsRequestFailure.UnsupportedContent) { }
            ThrowsAnthropic(AnthropicRequestFailure.UnsupportedStrictSchema, () => Anthropic(parameters, "require"));
            try { _ = GoogleMode(parameters, "require"); throw new InvalidOperationException("Google admitted a required untyped schema: " + parameters); }
            catch (GoogleGenerativeAIException error) when (error.Failure == GoogleFailure.UnsupportedValue) { }
            try { _ = ProviderTranscript.ResolveStrict(Declared(parameters, "require"), true); throw new InvalidOperationException("A required untyped schema resolved: " + parameters); }
            catch (ProviderTranscriptException error)
            { Equal("Tool \"t\" requires JSON-schema constrained sampling, but schemas without a type are unsupported.", error.Message); }
        }
        // An anyOf node needs no type of its own, and an unsupported keyword is still reported before the missing type.
        Check(Responses("""{"type":"object","properties":{"v":{"anyOf":[{"type":"string"},{"type":"null"}]}},"required":["v"]}""", "require").GetProperty("strict").GetBoolean(),
            "An anyOf node without a type lost strict mode.");
        try { _ = ProviderTranscript.ResolveStrict(Declared("""{"type":"object","properties":{"v":{"$ref":"#/$defs/a"}},"required":["v"]}""", "require"), true); throw new InvalidOperationException("A $ref schema resolved."); }
        catch (ProviderTranscriptException error) { Equal("Tool \"t\" requires JSON-schema constrained sampling, but $ref schemas are unsupported.", error.Message); }
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- mistral-conversations.ts

    private static async Task Sync160MistralStrictOptIn()
    {
        // #12148: Mistral Large 4 stalls for minutes before strict tool calls, so strict tools need compat.supportsStrictMode.
        var model = new ModelDescriptor("mistral-large-4", "mistral-conversations", "mistral");
        var endpoint = new Uri("https://api.mistral.ai/");
        const string parameters = """{"type":"object","properties":{"path":{"type":"string"},"limit":{"type":"number"}},"required":["path"]}""";
        async Task<(JsonElement? Function, ChatResult Result)> Run(string compat, string strict, bool simple = true)
        {
            var metadata = JsonData.Parse("""{"id":"mistral-large-4","name":"Mistral Large 4","api":"mistral-conversations","provider":"mistral","baseUrl":"https://api.mistral.ai","reasoning":false,"input":["text"],"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0},"contextWindow":100000,"maxTokens":1000""" + compat + "}");
            string? body = null;
            using var handler = new Handler(async request =>
            {
                body = await request.Content!.ReadAsStringAsync();
                return Sse("""{"id":"m","choices":[{"delta":{"content":"ok"},"finish_reason":"stop"}]}""");
            });
            var options = new MistralTextOptions(endpoint, true, new(0, 0, 0, 0), "offline-fixture");
            using var provider = NativeProviderFactory.CreateCatalogMistral(model, "https://api.mistral.ai/", Key, metadata, options, simple, handler);
            var result = await new ChatClient(provider).CompleteAsync(new(model, [Sync160Tool(parameters, strict, "read"), Ask], 1)).WaitAsync(Deadline);
            return (body is null ? null : JsonDocument.Parse(body).RootElement.GetProperty("tools")[0].GetProperty("function").Clone(), result);
        }
        foreach (var simple in new[] { true, false })
        {
            // Without the flag a "prefer" tool keeps its declared schema and strict: false is still written.
            var plain = (await Run("", "prefer", simple)).Function!.Value;
            Check(plain.GetProperty("strict").ValueKind == JsonValueKind.False, "Mistral sent a strict tool without compat.supportsStrictMode.");
            BodyEqual(parameters, plain.GetProperty("parameters").GetRawText());
            BodyEqual("""["path"]""", plain.GetProperty("parameters").GetProperty("required").GetRawText());
            var disabled = (await Run(""","compat":{"supportsStrictMode":false}""", "prefer", simple)).Function!.Value;
            Check(disabled.GetProperty("strict").ValueKind == JsonValueKind.False, "compat.supportsStrictMode false sent a strict tool.");
            // With it the tool is strict: every property required, optional ones nullable.
            var strict = (await Run(""","compat":{"supportsStrictMode":true}""", "prefer", simple)).Function!.Value;
            Check(strict.GetProperty("strict").GetBoolean(), "compat.supportsStrictMode did not enable strict tools.");
            BodyEqual("""{"type":"object","properties":{"path":{"type":"string"},"limit":{"anyOf":[{"type":"number"},{"type":"null"}]}},"required":["path","limit"],"additionalProperties":false}""",
                strict.GetProperty("parameters").GetRawText());
            // strict: "require" fails locally when the model has no strict mode: no request is sent.
            var (function, required) = await Run("", "require", simple);
            Check(function is null && required.Message.StopReason == StopReason.Error, "A required strict tool was sent without strict mode.");
            Check((await Run(""","compat":{"supportsStrictMode":true}""", "require", simple)).Function!.Value.GetProperty("strict").GetBoolean(), "A required strict tool was not strict.");
        }
    }

    // ---------------------------------------------------------------- anthropic-messages.ts

    private static Task Sync160AnthropicAdditionalProperties()
    {
        // #12067: the non-strict schema is { type, properties, required, additionalProperties, $defs, definitions } of the declared one.
        JsonElement Tool(AnthropicMessagesRequestOptions options, string parameters, string? strict = null) =>
            new AnthropicMessagesRequestProjector(options).Project(new(Haiku, [Sync160Tool(parameters, strict)], 1)).Value.GetProperty("tools")[0];
        var plain = new AnthropicMessagesRequestOptions(128, CacheRetention: AnthropicCacheRetention.None);
        var strictTools = plain with { SupportsStrictTools = true };
        const string properties = """{"path":{"type":"string"},"limit":{"type":"number"}}""";
        string Schema(string rest) => """{"type":"object","properties":""" + properties + ""","required":["path"]""" + rest + "}";
        BodyEqual(Schema(""","additionalProperties":false"""), Tool(plain, Schema(""","additionalProperties":false""")).GetProperty("input_schema").GetRawText());
        BodyEqual(Schema(""","additionalProperties":true"""), Tool(plain, Schema(""","additionalProperties":true""")).GetProperty("input_schema").GetRawText());
        BodyEqual(Schema(""","additionalProperties":{"type":"string"}"""), Tool(plain, Schema(""","additionalProperties":{"type":"string"}""")).GetProperty("input_schema").GetRawText());
        // A schema that says nothing gets nothing added.
        BodyEqual(Schema(""), Tool(plain, Schema("")).GetProperty("input_schema").GetRawText());
        // It comes before the shared definitions, whatever the declared order; other root keywords still stay out.
        BodyEqual(Schema(""","additionalProperties":false,"$defs":{"a":{"type":"string"}},"definitions":{"b":{"type":"number"}}"""),
            Tool(plain, """{"$defs":{"a":{"type":"string"}},"title":"Read","type":"object","definitions":{"b":{"type":"number"}},"additionalProperties":false,"anyOf":[{"required":["path"]}],"properties":""" +
                properties + ""","required":["path"]}""").GetProperty("input_schema").GetRawText());
        // The preferred-strict fallbacks use the same conversion: no strict tools, a rejected keyword, and the strict-limit retry.
        const string preferred = """{"type":"object","properties":{"n":{"type":"integer","minimum":1}},"required":["n"],"additionalProperties":false}""";
        BodyEqual(preferred, Tool(plain, preferred, "prefer").GetProperty("input_schema").GetRawText());
        BodyEqual(preferred, Tool(strictTools, preferred, "prefer").GetProperty("input_schema").GetRawText());
        var fallback = Tool(strictTools with { DisablePreferredStrictTools = true }, Schema(""","additionalProperties":false"""), "prefer");
        Check(!fallback.TryGetProperty("strict", out _), "The strict-limit fallback stayed strict.");
        BodyEqual(Schema(""","additionalProperties":false"""), fallback.GetProperty("input_schema").GetRawText());
        // A strict tool is unchanged: the strict schema carries its own additionalProperties: false.
        var strict = Tool(strictTools, Schema(""), "prefer");
        Check(strict.GetProperty("strict").GetBoolean(), "A strict tool lost strict mode.");
        BodyEqual("""{"type":"object","properties":{"path":{"type":"string"},"limit":{"anyOf":[{"type":"number"},{"type":"null"}]}},"required":["path","limit"],"additionalProperties":false}""",
            strict.GetProperty("input_schema").GetRawText());
        return Task.CompletedTask;
    }

    private static HttpResponseMessage Sync160AnthropicReply(int id) => new(HttpStatusCode.OK)
    {
        Content = new StringContent("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_" + id + "\",\"model\":\"claude-thread\",\"usage\":{\"input_tokens\":10,\"output_tokens\":0}}}\n\n" +
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"ok\"}}\n\n" +
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n", Encoding.UTF8, "text/event-stream")
    };

    /// <summary>Two turns of one session on an Anthropic endpoint: each request's thread member and whether it carried the threads beta.</summary>
    private static async Task<List<(string? Thread, string? Previous, bool Beta)>> Sync160ThreadTurns(string provider, string host, string session,
        AnthropicMessageThreadStore? store, Action? betweenTurns = null, bool? supports = null)
    {
        var model = new ModelDescriptor("claude-thread", "anthropic-messages", provider);
        var seen = new List<(string?, string?, bool)>();
        using var handler = new Handler(async request =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            seen.Add(((string?)body["thread"]?["type"], (string?)body["thread"]?["previous_message_id"],
                request.Headers.TryGetValues("anthropic-beta", out var betas) && betas.Any(value => value.Contains("message-threads-2026-08-12", StringComparison.Ordinal))));
            return Sync160AnthropicReply(seen.Count);
        });
        using var client = new HttpClient(handler);
        var factory = new AnthropicMessagesKeyAuthRequestFactory(new("https://" + host), model, new(100) { SupportsMessageThreads = supports }, new() { ThreadStore = store });
        var transport = AnthropicMessagesHttpSseTransport.FromPrepared(client, (request, token) => factory.Prepare(request, Key, token));
        async Task<AssistantMessage> Say(ImmutableArray<TranscriptEntry> messages)
        {
            StreamTerminalEvent? terminal = null;
            await foreach (var frame in transport.StreamAsync(new(model, messages, 1) { SessionId = session }))
                if (frame is StreamTerminalEvent done) terminal = done;
            Check(terminal is StreamDone, "The thread turn failed: " + PiWireJson.WriteMessage(terminal!.Message));
            return terminal.Message;
        }
        var first = await Say([User("first", 1)]);
        betweenTurns?.Invoke();
        _ = await Say([User("first", 1), new("assistant", PiWireJson.WriteMessage(first)), User("second", 3)]);
        return seen;
    }

    private static async Task Sync160AnthropicThreadsByEndpoint()
    {
        // #11987: any provider on api.anthropic.com gets threads, such as a models.json alias of the anthropic provider.
        var alias = await Sync160ThreadTurns("anthropic-1-api", "api.anthropic.com", "sync160-alias", new());
        Check(alias is [("create", null, true), ("continue", "msg_1", true)], "An alias provider on api.anthropic.com did not use threads: " + string.Join(";", alias));
        var direct = await Sync160ThreadTurns("anthropic", "api.anthropic.com", "sync160-direct", new());
        Check(direct is [("create", null, true), ("continue", "msg_1", true)], "The anthropic provider lost threads: " + string.Join(";", direct));
        // Other endpoints stay off, the anthropic provider id included: a gateway that drops unknown fields would lose the conversation.
        foreach (var provider in new[] { "anthropic-1-api", "anthropic" })
        {
            var gateway = await Sync160ThreadTurns(provider, "gateway.example.com", "sync160-gateway-" + provider, new());
            Check(gateway is [(null, null, false), (null, null, false)], "A gateway endpoint used threads: " + string.Join(";", gateway));
        }
        // A host that only contains the name is another endpoint.
        var lookalike = await Sync160ThreadTurns("anthropic", "api.anthropic.com.gateway.example", "sync160-lookalike", new());
        Check(lookalike is [(null, null, false), (null, null, false)], "A lookalike host used threads: " + string.Join(";", lookalike));
        // compat.supportsMessageThreads still overrides in both directions.
        var optedIn = await Sync160ThreadTurns("gateway", "gateway.example.com", "sync160-opted-in", new(), supports: true);
        Check(optedIn is [("create", null, true), ("continue", "msg_1", true)], "compat.supportsMessageThreads true was ignored: " + string.Join(";", optedIn));
        var optedOut = await Sync160ThreadTurns("anthropic-1-api", "api.anthropic.com", "sync160-opted-out", new(), supports: false);
        Check(optedOut is [(null, null, false), (null, null, false)], "compat.supportsMessageThreads false was ignored: " + string.Join(";", optedOut));
    }

    // ---------------------------------------------------------------- session-resources.ts

    private static async Task Sync160SessionResourcesThreads()
    {
        // cleanupSessionResources(sessionId) frees the process-local provider state of that session only: here the shared message
        // thread state (anthropic-messages.ts registers it), so the next request starts a new thread instead of continuing.
        const string session = "sync160-session-resources", other = "sync160-session-resources-other";
        try
        {
            var kept = await Sync160ThreadTurns("anthropic", "api.anthropic.com", session, store: null);
            Check(kept is [("create", null, true), ("continue", "msg_1", true)], "The shared store did not continue: " + string.Join(";", kept));
            var unrelated = await Sync160ThreadTurns("anthropic", "api.anthropic.com", session + "-2", store: null, betweenTurns: () => SessionResources.Cleanup(other));
            Check(unrelated is [("create", null, true), ("continue", "msg_1", true)], "Another session's cleanup dropped this session's thread: " + string.Join(";", unrelated));
            var cleaned = await Sync160ThreadTurns("anthropic", "api.anthropic.com", session + "-3", store: null, betweenTurns: () => SessionResources.Cleanup(session + "-3"));
            Check(cleaned is [("create", null, true), ("create", null, true)], "Session cleanup kept the thread state: " + string.Join(";", cleaned));
            var all = await Sync160ThreadTurns("anthropic", "api.anthropic.com", session + "-4", store: null, betweenTurns: () => SessionResources.Cleanup());
            Check(all is [("create", null, true), ("create", null, true)], "Cleanup of every session kept the thread state: " + string.Join(";", all));
            // A store of the caller's own is not process-wide state and is left alone.
            var own = new AnthropicMessageThreadStore();
            var isolated = await Sync160ThreadTurns("anthropic", "api.anthropic.com", session + "-5", own, betweenTurns: () => SessionResources.Cleanup(session + "-5"));
            Check(isolated is [("create", null, true), ("continue", "msg_1", true)], "Session cleanup reached a caller-owned store: " + string.Join(";", isolated));
        }
        finally { foreach (var suffix in new[] { "", "-2", "-3", "-4", "-5" }) SessionResources.Cleanup(session + suffix); }
    }

    // ---------------------------------------------------------------- openai-completions.ts

    private static string Sync160CompletionsRow(string provider, string id, string baseUrl, string compat = "{}", bool reasoning = true,
        int contextWindow = 100000, int maxTokens = 1000) =>
        "{\"id\":\"" + id + "\",\"name\":\"N\",\"api\":\"openai-completions\",\"provider\":\"" + provider + "\",\"baseUrl\":\"" + baseUrl + "\",\"reasoning\":" +
        (reasoning ? "true" : "false") + ",\"input\":[\"text\"],\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0},\"contextWindow\":" + contextWindow +
        ",\"maxTokens\":" + maxTokens + ",\"compat\":" + compat + "}";

    private const string Sync160CompletionChunk = """{"id":"chatcmpl-sync160","object":"chat.completion.chunk","created":1,"model":"m","choices":""";
    private static HttpResponseMessage Sync160Completion() => Sse(null, Frames(Sync160CompletionChunk + """[{"index":0,"delta":{"role":"assistant","content":"OK"},"finish_reason":null}]}""",
        Sync160CompletionChunk + """[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1}}"""));

    private static async Task Sync160CompletionsDeveloperRole()
    {
        async Task<string[]> Roles(string provider, string id, string baseUrl, string compat = "{}", bool reasoning = true, bool midSystem = false)
        {
            var model = new ModelDescriptor(id, "openai-completions", provider);
            var factory = new CompletionsKeyAuthRequestFactory(new(baseUrl + "/chat/completions"), model, null,
                new CompletionsKeyAuthRequestOptions { ModelMetadata = JsonData.Parse(Sync160CompletionsRow(provider, id, baseUrl, compat, reasoning)) });
            ImmutableArray<TranscriptEntry> messages = midSystem
                ? [Entry("""{"role":"system","content":"Follow instructions.","timestamp":0}"""), Ask, Entry("""{"role":"system","content":"Now answer in German.","timestamp":2}"""), User("again", 3)]
                : [Entry("""{"role":"system","content":"Follow instructions.","timestamp":0}"""), Ask];
            using var http = factory.Create(new(model, messages, 1), Key);
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync());
            return [.. body.RootElement.GetProperty("messages").EnumerateArray().Select(message => message.GetProperty("role").GetString()!)];
        }
        const string azure = "https://my-resource.openai.azure.com/openai/v1", openRouter = "https://openrouter.ai/api/v1";
        // OpenAI's own API keeps developer for a reasoning model: by provider id or by host.
        Equal("developer", (await Roles("openai", "gpt-5.5", "https://api.openai.com/v1"))[0]);
        Equal("developer", (await Roles("my-openai", "gpt-5.5", "https://api.openai.com/v1"))[0]);
        Equal("developer", (await Roles("openai", "gpt-5.5", "https://proxy.example/v1"))[0]);
        // #10741: every other host gets system: Groq (whose Qwen chat template rejects developer), a custom host, OpenRouter for
        // any model (its openai/ and anthropic/ models included), and Azure for a built-in or a custom provider.
        foreach (var (provider, id, baseUrl) in new[]
        {
            ("groq", "qwen/qwen3.8-27b", "https://api.groq.com/openai/v1"), ("local", "qwen3.8-27b", "http://localhost:8080/v1"),
            ("openrouter", "deepseek/deepseek-v4-pro", openRouter), ("openrouter", "openai/gpt-5.2-codex", openRouter),
            ("openrouter", "anthropic/claude-fable-5.1:batch", openRouter), ("azure", "deepseek-v4-pro", azure), ("my-azure", "gpt-5.5", azure),
            ("github-copilot", "gemini-4-argon", "https://api.individual.githubcopilot.com"), ("fireworks", "accounts/fireworks/routers/kimi-k3-fast", "https://api.fireworks.ai/inference/v1"),
        })
            Equal("system", (await Roles(provider, id, baseUrl))[0]);
        // Pi 1.6.0's catalog no longer says supportsDeveloperRole: false on any row (428 rows lost it), so the default alone keeps these
        // providers' reasoning models on system: rows without the flag, as the 1.6.0 catalog has them.
        foreach (var (provider, baseUrl) in new[]
        {
            ("baseten", "https://inference.baseten.co/v1"), ("fireworks", "https://api.fireworks.ai/inference/v1"),
            ("github-copilot", "https://api.individual.githubcopilot.com"), ("huggingface", "https://router.huggingface.co/v1"),
            ("kimi-coding", "https://api.kimi.com/coding/v1"), ("qwen-token-plan", "https://token-plan.maas.qwencloudapi.com/compatible-mode/v1"),
            ("qwen-token-plan-cn", "https://token-plan.maas.qianwenaiapi.com/compatible-mode/v1"),
            ("qwen-token-plan-individual", "https://token-plan.maas.qwencloudapi.com/compatible-mode/v1"),
            ("qwen-token-plan-individual-cn", "https://token-plan.maas.qianwenaiapi.com/compatible-mode/v1"),
            ("deepseek", "https://api.deepseek.com"), ("cerebras", "https://api.cerebras.ai/v1"), ("moonshotai", "https://api.moonshot.ai/v1"),
            ("nvidia", "https://integrate.api.nvidia.com/v1"), ("together", "https://api.together.xyz/v1"), ("zai", "https://api.z.ai/api/coding/paas/v4"),
            ("opencode", "https://opencode.ai/zen/v1"), ("ant-ling", "https://api.ant-ling.com/v1"), ("cloudflare-workers-ai", "https://api.cloudflare.com/client/v4/accounts/a/ai/v1"),
            ("cloudflare-ai-gateway", "https://gateway.ai.cloudflare.com/v1/a/g/compat"), ("xai", "https://api.x.ai/v1"), ("mistral-compat", "https://api.mistral.ai/v1"),
        })
            Equal("system", (await Roles(provider, "reasoning-model", baseUrl))[0]);
        // compat.supportsDeveloperRole overrides in both directions.
        Equal("developer", (await Roles("groq", "qwen/qwen3.8-27b", "https://api.groq.com/openai/v1", """{"supportsDeveloperRole":true}"""))[0]);
        Equal("system", (await Roles("openai", "gpt-5.5", "https://api.openai.com/v1", """{"supportsDeveloperRole":false}"""))[0]);
        // The role is the reasoning models' only; mid-conversation system messages follow the same rule.
        Equal("system", (await Roles("openai", "gpt-4.1", "https://api.openai.com/v1", reasoning: false))[0]);
        BodyEqual("developer,user,developer,user", string.Join(",", await Roles("openai", "gpt-5.5", "https://api.openai.com/v1", """{"supportsMidConvoSystemMessages":true}""", midSystem: true)));
        BodyEqual("system,user,system,user", string.Join(",", await Roles("groq", "qwen/qwen3.8-27b", "https://api.groq.com/openai/v1", """{"supportsMidConvoSystemMessages":true}""", midSystem: true)));
    }

    private static async Task Sync160DeveloperRoleRoutes()
    {
        // The same default through the paths a session uses: the catalog route at each thinking level (the model row is bound on every
        // one, the summary binding included) and the Simple factory. Nothing else decides the role: the fixed OpenAI/OpenRouter factory
        // and the Azure one bind the row too.
        ImmutableArray<TranscriptEntry> messages = [Entry("""{"role":"system","content":"Follow instructions.","timestamp":0}"""), Ask];
        async Task<string?> RouteRole(string provider, string baseUrl, string? level, bool thinkingProfile = true)
        {
            var model = new ModelDescriptor("reasoning-model", "openai-completions", provider);
            string? body = null;
            using var handler = new Handler(async request => { body = await request.Content!.ReadAsStringAsync(); return Sync160Completion(); });
            using var provider0 = NativeProviderFactory.CreateCatalogCompletions(model, baseUrl, Key, new(Reasoning: true), new(),
                JsonData.Parse(Sync160CompletionsRow(provider, "reasoning-model", baseUrl)), handler, thinkingProfile);
            var result = await new ChatClient(provider0).CompleteAsync(new ChatRequest(model, messages, 1) { ThinkingLevel = level }).WaitAsync(Deadline);
            Check(result.Message.StopReason == StopReason.Stop, provider + " route failed: " + PiWireJson.WriteMessage(result.Message));
            return (string?)JsonNode.Parse(body!)!["messages"]![0]!["role"];
        }
        async Task<string?> SimpleRole(string provider, string baseUrl, string? reasoning)
        {
            var model = new ModelDescriptor("reasoning-model", "openai-completions", provider);
            string? body = null;
            using var handler = new Handler(async request => { body = await request.Content!.ReadAsStringAsync(); return Sync160Completion(); });
            using var client = new HttpClient(handler);
            var factory = new CompletionsSimpleRequestFactory(client, new(baseUrl + "/chat/completions"), model,
                new CompletionsSimpleOptions(JsonData.Parse(Sync160CompletionsRow(provider, "reasoning-model", baseUrl)), reasoning) { ApiKey = Key });
            StreamTerminalEvent? terminal = null;
            await foreach (var frame in factory.StreamAsync(new(model, messages, 1))) if (frame is StreamTerminalEvent done) terminal = done;
            Check(terminal is StreamDone, provider + " Simple request failed: " + PiWireJson.WriteMessage(terminal!.Message));
            return (string?)JsonNode.Parse(body!)!["messages"]![0]!["role"];
        }
        foreach (var (provider, baseUrl) in new[]
        {
            ("kimi-coding", "https://api.kimi.com/coding/v1"), ("fireworks", "https://api.fireworks.ai/inference/v1"),
            ("huggingface", "https://router.huggingface.co/v1"), ("baseten", "https://inference.baseten.co/v1"),
        })
        {
            foreach (var level in new[] { null, "off", "high" }) Equal("system", await RouteRole(provider, baseUrl, level));
            Equal("system", await RouteRole(provider, baseUrl, null, thinkingProfile: false));
            foreach (var reasoning in new[] { null, "high" }) Equal("system", await SimpleRole(provider, baseUrl, reasoning));
        }
        foreach (var level in new[] { null, "off", "high" }) Equal("developer", await RouteRole("openai", "https://api.openai.com/v1", level));
        Equal("developer", await SimpleRole("openai", "https://api.openai.com/v1", "high"));
    }

    // PiSharp-only: the request factories bound the output token limit. Pi has no ceiling and sends the catalog value as it is; the
    // bound was 1,000,000 and refused the models whose maxTokens is 1,048,576 before any request was sent (62 rows of the Pi 1.6.0
    // catalog, the Fireworks, OpenCode Go and Together default models among them). Its default is now the 32-bit token range.
    private static async Task Sync160LargeMaxTokens()
    {
        const int large = 1_048_576;
        // 1. The Completions key factory, alone and with the model row bound.
        foreach (var (provider, baseUrl, field) in new[]
        {
            ("fireworks", "https://api.fireworks.ai/inference/v1", "max_completion_tokens"), ("together", "https://api.together.xyz/v1", "max_tokens"),
            ("opencode-go", "https://opencode.ai/zen/go/v1", "max_completion_tokens"),
        })
        {
            var model = new ModelDescriptor("large-output", "openai-completions", provider);
            var row = JsonData.Parse(Sync160CompletionsRow(provider, "large-output", baseUrl, contextWindow: large, maxTokens: large));
            foreach (var metadata in new JsonData?[] { null, row })
            {
                var factory = new CompletionsKeyAuthRequestFactory(new(baseUrl + "/chat/completions"), model, null, new CompletionsKeyAuthRequestOptions(MaxTokens: large) { ModelMetadata = metadata });
                using var http = factory.Create(new(model, [Ask], 1), Key);
                using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync());
                Equal(large, body.RootElement.GetProperty(metadata is null ? "max_completion_tokens" : field).GetInt32());
            }
            // The route a session uses, with the model's limit as its output cap, at a thinking level and without.
            foreach (var level in new[] { null, "high" })
            {
                string? sent = null;
                using var handler = new Handler(async request => { sent = await request.Content!.ReadAsStringAsync(); return Sync160Completion(); });
                using var route = NativeProviderFactory.CreateCatalogCompletions(model, baseUrl, Key, new(Reasoning: true), new(MaxTokens: large), row, handler);
                var result = await new ChatClient(route).CompleteAsync(new ChatRequest(model, [Ask], 1) { ThinkingLevel = level }).WaitAsync(Deadline);
                Check(result.Message.StopReason == StopReason.Stop, provider + " route refused maxTokens " + large + ": " + PiWireJson.WriteMessage(result.Message));
                Equal(large, JsonNode.Parse(sent!)![field]!.GetValue<int>());
            }
            // 2. The Completions Simple factory: the model's limit fitted into its context window (above the old ceiling), and whole
            // when the window leaves room for it.
            foreach (var window in new[] { large, 4 * large })
            {
                string? sent = null;
                using var handler = new Handler(async request => { sent = await request.Content!.ReadAsStringAsync(); return Sync160Completion(); });
                using var client = new HttpClient(handler);
                var simple = new CompletionsSimpleRequestFactory(client, new(baseUrl + "/chat/completions"), model,
                    new CompletionsSimpleOptions(JsonData.Parse(Sync160CompletionsRow(provider, "large-output", baseUrl, contextWindow: window, maxTokens: large))) { ApiKey = Key });
                var resolved = simple.Resolve(new(model, [Ask], 1)).MaxTokens;
                Check(window == large ? resolved is > 1_000_000 and < large : resolved == large, provider + " Simple resolved " + resolved + " for a window of " + window);
                StreamTerminalEvent? terminal = null;
                await foreach (var frame in simple.StreamAsync(new(model, [Ask], 1))) if (frame is StreamTerminalEvent done) terminal = done;
                Check(terminal is StreamDone, provider + " Simple refused maxTokens " + large + ": " + PiWireJson.WriteMessage(terminal!.Message));
                Equal(resolved, JsonNode.Parse(sent!)![field]!.GetValue<double>());
            }
        }
        // 3. The Anthropic key factory: the projection's limit, an explicit one, and the catalog route.
        var claude = new ModelDescriptor("large-output", "anthropic-messages", "opencode-go");
        foreach (var options in new[] { new AnthropicMessagesKeyAuthRequestOptions(), new AnthropicMessagesKeyAuthRequestOptions(MaxTokens: large) })
        {
            var factory = new AnthropicMessagesKeyAuthRequestFactory(new("https://opencode.ai/zen/go"), claude, new(large, CacheRetention: AnthropicCacheRetention.None), options);
            using var http = factory.Create(new(claude, [Ask], 1), Key);
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync());
            Equal(large, body.RootElement.GetProperty("max_tokens").GetInt32());
        }
        string? anthropicBody = null;
        using (var handler = new Handler(async request => { anthropicBody = await request.Content!.ReadAsStringAsync(); return Sync160AnthropicReply(1); }))
        using (var route = NativeProviderFactory.CreateCatalogAnthropic(claude, "https://opencode.ai/zen/go", Key, new(large, CacheRetention: AnthropicCacheRetention.None), new(MaxTokens: large),
            JsonData.Parse("""{"id":"large-output","name":"N","api":"anthropic-messages","provider":"opencode-go","baseUrl":"https://opencode.ai/zen/go","reasoning":false,"input":["text"],"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0},"contextWindow":1048576,"maxTokens":1048576}"""), handler))
        {
            var result = await new ChatClient(route).CompleteAsync(new ChatRequest(claude, [Ask], 1)).WaitAsync(Deadline);
            Check(result.Message.StopReason == StopReason.Stop, "The Anthropic route refused maxTokens " + large + ": " + PiWireJson.WriteMessage(result.Message));
            Equal(large, JsonNode.Parse(anthropicBody!)!["max_tokens"]!.GetValue<int>());
        }
        // The Responses factory had the same ceiling.
        var responsesModel = new ModelDescriptor("large-output", "openai-responses", "fireworks");
        using (var http = new ResponsesKeyAuthRequestFactory(new("https://api.fireworks.ai/inference/v1/responses"), responsesModel, new(false), new(MaxOutputTokens: large)).Create(new(responsesModel, [Ask], 1), Key))
        using (var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync()))
            Equal(large, body.RootElement.GetProperty("max_output_tokens").GetInt32());

        // The bound is still there for a caller that configures one, and the default still stops a count outside the 32-bit range.
        var bounded = new ModelDescriptor("bounded", "openai-completions", "fixture");
        foreach (var options in new[] { new CompletionsKeyAuthRequestOptions(MaxTokens: large, MaximumTokenMagnitude: 1_000_000), new CompletionsKeyAuthRequestOptions(MaxTokens: 2_147_483_648d) })
            try { _ = new CompletionsKeyAuthRequestFactory(new("https://bounded.invalid/v1/chat/completions"), bounded, null, options); throw new InvalidOperationException("A Completions limit above the bound was admitted."); }
            catch (CompletionsRequestException error) when (error.Failure == CompletionsRequestFailure.ResourceLimit) { }
        foreach (var options in new[] { new AnthropicMessagesKeyAuthRequestOptions(MaxTokens: large, MaximumTokenMagnitude: 1_000_000), new AnthropicMessagesKeyAuthRequestOptions(MaxTokens: 2_147_483_648d) })
            try { _ = new AnthropicMessagesKeyAuthRequestFactory(new("https://bounded.invalid"), claude, new(128), options); throw new InvalidOperationException("An Anthropic limit above the bound was admitted."); }
            catch (AnthropicMessagesKeyAuthRequestException error) when (error.Failure == AnthropicMessagesKeyAuthRequestFailure.ResourceLimit) { }
        try { _ = new ResponsesKeyAuthRequestFactory(new("https://bounded.invalid/v1/responses"), responsesModel, new(false), new(MaxOutputTokens: large, MaximumOutputTokens: 1_000_000)); throw new InvalidOperationException("A Responses limit above the bound was admitted."); }
        catch (ResponsesKeyAuthRequestException error) when (error.Failure == ResponsesKeyAuthRequestFailure.ResourceLimit) { }
    }

    private static async Task Sync160CompletionsReasoningTokens()
    {
        // #12110 (#12019): GitHub Copilot's Gemini and Kimi models report reasoning tokens at the top level of usage and take
        // reasoning_effort. Pi 1.6.0's catalog marks them with compat.supportsReasoningEffort; the 1.5.2 rows here say false.
        const string baseUrl = "https://api.individual.githubcopilot.com";
        var model = new ModelDescriptor("gemini-4-argon", "openai-completions", "github-copilot");
        async Task<(JsonNode Body, ChatResult Result)> Run(string compat, string usage, string? level = null)
        {
            string? body = null;
            using var handler = new Handler(async request =>
            {
                body = await request.Content!.ReadAsStringAsync();
                const string chunk = """{"id":"chatcmpl-copilot-reasoning","object":"chat.completion.chunk","created":1,"model":"gemini-4-argon","choices":""";
                return Sse(null, Frames(chunk + """[{"index":0,"delta":{"role":"assistant","content":"OK"},"finish_reason":null}]}""",
                    chunk + """[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":""" + usage + "}"));
            });
            using var provider = NativeProviderFactory.CreateCatalogCompletions(model, baseUrl, Key, new(Reasoning: true), new(), JsonData.Parse(Sync160CompletionsRow("github-copilot", "gemini-4-argon", baseUrl, compat)), handler);
            var result = await new ChatClient(provider).CompleteAsync(new ChatRequest(model, [Ask], 1) { ThinkingLevel = level }).WaitAsync(Deadline);
            Check(result.Message.StopReason == StopReason.Stop, "The Copilot completion failed: " + PiWireJson.WriteMessage(result.Message));
            return (JsonNode.Parse(body!)!, result);
        }
        static double Reasoning(ChatResult result) => result.Message.Usage.ExtraProperties!.TryGet("reasoning", out var value) ? value!.Value.GetDouble() : double.NaN;
        const string copilot = """{"supportsStore":false,"supportsReasoningEffort":true}""";
        var top = await Run(copilot, """{"prompt_tokens":10,"completion_tokens":40,"prompt_tokens_details":{"cached_tokens":0},"reasoning_tokens":30}""");
        Equal(30d, Reasoning(top.Result)); Equal(40d, top.Result.Message.Usage.Output);
        // completion_tokens_details wins when it reports a count; a zero or missing count falls through to the top level.
        Equal(12d, Reasoning((await Run(copilot, """{"prompt_tokens":10,"completion_tokens":40,"completion_tokens_details":{"reasoning_tokens":12},"reasoning_tokens":30}""")).Result));
        Equal(30d, Reasoning((await Run(copilot, """{"prompt_tokens":10,"completion_tokens":40,"completion_tokens_details":{"reasoning_tokens":0},"reasoning_tokens":30}""")).Result));
        Equal(30d, Reasoning((await Run(copilot, """{"prompt_tokens":10,"completion_tokens":40,"completion_tokens_details":{},"reasoning_tokens":30}""")).Result));
        Equal(0d, Reasoning((await Run(copilot, """{"prompt_tokens":10,"completion_tokens":40}""")).Result));
        // The thinking level reaches a Copilot completions model whose compat says it takes an effort, and only then.
        Equal("low", (string?)(await Run(copilot, """{"prompt_tokens":10,"completion_tokens":4}""", "low")).Body["reasoning_effort"]);
        Equal("high", (string?)(await Run(copilot, """{"prompt_tokens":10,"completion_tokens":4}""", "high")).Body["reasoning_effort"]);
        // The 1.5.2 rows say supportsReasoningEffort: false: such a model has no thinking level to select and sends no effort.
        const string before = """{"supportsStore":false,"supportsReasoningEffort":false}""";
        ImmutableArray<string> Levels(string compat)
        {
            using var provider = NativeProviderFactory.CreateCatalogCompletions(model, baseUrl, Key, new(Reasoning: true), new(), JsonData.Parse(Sync160CompletionsRow("github-copilot", "gemini-4-argon", baseUrl, compat)));
            return ThinkingLevels.GetSupported(provider.Transport, model);
        }
        Check(Levels(copilot).Contains("low") && Levels(copilot).Contains("high"), "A Copilot effort model offers no thinking levels: " + string.Join(",", Levels(copilot)));
        Check(Levels(before) is ["off"], "A model without reasoning effort offers thinking levels: " + string.Join(",", Levels(before)));
        Check((await Run(before, """{"prompt_tokens":10,"completion_tokens":4}""")).Body["reasoning_effort"] is null, "reasoning_effort was sent although compat.supportsReasoningEffort is false.");
    }

    // ---------------------------------------------------------------- openai-responses.ts / anthropic-messages.ts on GitHub Copilot

    private static async Task Sync160CopilotStrict()
    {
        // #12286: Copilot's /responses treats a function tool without `strict` as strict and fills omitted optional arguments with ""
        // or invented values. With compat.supportsStrictMode (Pi 1.6.0's catalog sets it for github-copilot's openai-responses models)
        // every tool says so: strict: false, or strict: true for a tool with JSON-schema constrained sampling.
        const string baseUrl = "https://api.individual.githubcopilot.com";
        var tools = Entry("""{"role":"system","content":"","toolsAdded":[{"name":"ordinary","description":"An ordinary tool","parameters":{"type":"object","properties":{"path":{"type":"string"},"offset":{"type":"number"}},"required":["path"]}},{"name":"constrained","description":"A constrained tool","parameters":{"type":"object","properties":{"value":{"type":"string"}},"required":["value"]},"constrainedSampling":{"type":"json_schema","strict":"prefer"}}],"timestamp":0}""");
        foreach (var id in new[] { "gpt-5.3-codex", "mai-code-2", "grok-4.7" })
        {
            var model = new ModelDescriptor(id, "openai-responses", "github-copilot");
            async Task<JsonNode> Tools(string compat)
            {
                string? body = null;
                using var handler = new Handler(async request =>
                {
                    body = await request.Content!.ReadAsStringAsync();
                    return Sse("""{"type":"response.completed","response":{"id":"r","status":"completed","output":[],"usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}}""");
                });
                var metadata = JsonData.Parse("{\"id\":\"" + id + "\",\"name\":\"N\",\"api\":\"openai-responses\",\"provider\":\"github-copilot\",\"baseUrl\":\"" + baseUrl +
                    "\",\"reasoning\":true,\"input\":[\"text\"],\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0},\"contextWindow\":100000,\"maxTokens\":1000,\"compat\":" + compat + "}");
                using var provider = NativeProviderFactory.CreateCatalogResponses(model, baseUrl, Key, new(true), new(), metadata, handler);
                _ = await new ChatClient(provider).CompleteAsync(new ChatRequest(model, [tools, Ask], 1)).WaitAsync(Deadline);
                return JsonNode.Parse(body!)!["tools"]!;
            }
            var explicitStrict = await Tools("""{"supportsStrictMode":true}""");
            BodyEqual("""{"type":"function","name":"ordinary","description":"An ordinary tool","parameters":{"type":"object","properties":{"path":{"type":"string"},"offset":{"type":"number"}},"required":["path"]},"strict":false}""",
                explicitStrict[0]!.ToJsonString());
            BodyEqual("""{"type":"function","name":"constrained","description":"A constrained tool","parameters":{"type":"object","properties":{"value":{"type":"string"}},"required":["value"],"additionalProperties":false},"strict":true}""",
                explicitStrict[1]!.ToJsonString());
            // The 1.5.2 rows carry no flag: the member is left out, which is what Copilot misreads.
            var silent = await Tools("{}");
            Check(silent[0]!["strict"] is null && silent[1]!["strict"] is null && !silent[0]!.AsObject().ContainsKey("strict"), "A row without compat.supportsStrictMode wrote strict.");
        }
        // Copilot's Claude models send strict: true for those tools, as direct Anthropic does (compat.supportsStrictTools on its
        // anthropic-messages models in Pi 1.6.0's catalog); an ordinary tool has no strict member.
        JsonElement Claude(string compat)
        {
            var metadata = JsonData.Parse("""{"id":"claude-sonnet-5.5","api":"anthropic-messages","provider":"github-copilot","compat":""" + compat + "}");
            var options = AnthropicCatalogCompatibility.Apply(new AnthropicMessagesRequestOptions(128, CacheRetention: AnthropicCacheRetention.None), metadata);
            return new AnthropicMessagesRequestProjector(options).Project(new(new("claude-sonnet-5.5", "anthropic-messages", "github-copilot"), [tools, Ask], 1)).Value.GetProperty("tools");
        }
        var claude = Claude("""{"supportsStrictTools":true}""");
        Check(!claude[0].TryGetProperty("strict", out _) && claude[1].GetProperty("strict").GetBoolean(), "Copilot Claude did not send strict: true for the constrained tool.");
        BodyEqual("""{"type":"object","properties":{"value":{"type":"string"}},"required":["value"],"additionalProperties":false}""", claude[1].GetProperty("input_schema").GetRawText());
        var before = Claude("{}");
        Check(!before[0].TryGetProperty("strict", out _) && !before[1].TryGetProperty("strict", out _), "A row without compat.supportsStrictTools sent a strict tool.");
    }

    // ---------------------------------------------------------------- openai-responses-shared.ts appendReasoningRemovals

    private const string PrefixMismatch = """{"type":"reasoning","id":"rs_edited","action":"removed","reason":"prefix_mismatch"}""";
    private const string OrganizationMismatch = """{"type":"reasoning","id":"rs_org","action":"removed","reason":"organization_mismatch"}""";
    private const string TurnBoundary = """{"type":"reasoning","id":"rs_old_turn","action":"removed","reason":"turn_boundary"}""";

    private static string? Sync160Diagnostics(AssistantMessage message) =>
        message.ExtraProperties?.TryGet("diagnostics", out var value) == true ? value!.ToString() : null;

    private static async Task Sync160ResponsesInputTransformations()
    {
        var model = new ModelDescriptor("gpt-6.2-sol", "openai-responses", "openai");
        var clock = new Clock(); var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        // One terminal response event carrying input_transformations through the shared stream processor.
        async Task<AssistantMessage> Terminal(string? transformations, string status = "completed")
        {
            var response = "{\"id\":\"resp_transformations\",\"status\":\"" + status + "\",\"output\":[]" +
                (status == "incomplete" ? ",\"incomplete_details\":{\"reason\":\"max_output_tokens\"}" : "") +
                (transformations is null ? "" : ",\"input_transformations\":" + transformations) + "}";
            var dto = JsonData.Parse("{\"type\":\"response." + status + "\",\"sequence_number\":0,\"response\":" + response + "}");
            async IAsyncEnumerable<JsonData> Events() { await Task.CompletedTask; yield return dto; }
            var transport = new ResponsesTextToolTransport((_, _) => Events(), new ResponsesTextToolOptions { Clock = clock });
            StreamTerminalEvent? terminal = null;
            await foreach (var frame in transport.StreamAsync(new(model, [Ask], 1))) if (frame is StreamTerminalEvent done) terminal = done;
            return terminal!.Message;
        }
        string Diagnostic(params string[] removals) => "[{\"type\":\"openai_input_transformations\",\"timestamp\":" + now + ",\"details\":{\"transformations\":[" + string.Join(",", removals) + "]}}]";

        // Removed reasoning items are recorded with their reasons; turn_boundary removals are not.
        var recorded = await Terminal("[" + PrefixMismatch + "," + TurnBoundary + "," + OrganizationMismatch + "]");
        Equal(StopReason.Stop, recorded.StopReason);
        BodyEqual(Diagnostic(PrefixMismatch, OrganizationMismatch), Sync160Diagnostics(recorded)!);
        // Also on an incomplete response.
        var incomplete = await Terminal("[" + PrefixMismatch + "]", "incomplete");
        Equal(StopReason.Length, incomplete.StopReason);
        BodyEqual(Diagnostic(PrefixMismatch), Sync160Diagnostics(incomplete)!);
        // A reason this port does not know yet is kept.
        const string future = """{"type":"reasoning","id":"rs_future","action":"removed","reason":"future_reason"}""";
        BodyEqual(Diagnostic(future), Sync160Diagnostics(await Terminal("[" + future + "]"))!);
        // ({ type, id, action, reason }): those members in that order, a missing one left out, a null kept, anything else dropped.
        BodyEqual(Diagnostic("""{"type":"reasoning","id":"rs_extra","action":"removed","reason":"prefix_mismatch"}""", """{"type":"reasoning","action":"removed"}""",
                """{"type":"reasoning","id":null,"action":"removed","reason":7}"""),
            Sync160Diagnostics(await Terminal("""[{"reason":"prefix_mismatch","extra":{"a":1},"action":"removed","id":"rs_extra","type":"reasoning"},{"action":"removed","type":"reasoning"},{"type":"reasoning","id":null,"action":"removed","reason":7}]"""))!);
        // Turn boundaries, other transformations and malformed reports leave no diagnostic and do not fail the response. Models
        // before GPT-5.6 report every earlier turn's reasoning as a turn boundary on each request.
        foreach (var report in new string?[]
        {
            null, "null", "[]", "[" + TurnBoundary + "," + TurnBoundary + "]", """{"malformed":true}""", "\"rs_text\"", "7",
            """[null,"rs_text",7,[],{"type":"message","id":"msg_1","action":"removed","reason":"prefix_mismatch"}]""",
            """[{"type":"reasoning","id":"rs_changed","action":"changed","reason":"prefix_mismatch"}]""",
            """[{"type":"reasoning","id":"rs_no_action","reason":"prefix_mismatch"},{"type":["reasoning"],"action":"removed"}]""",
        })
        {
            var message = await Terminal(report);
            Check(message.StopReason == StopReason.Stop && Sync160Diagnostics(message) is null, "A report left a diagnostic or failed: " + (report ?? "(absent)"));
        }
    }

    private static async Task Sync160ResponsesInputTransformationsHttp()
    {
        // The OpenAI Responses and Azure OpenAI Responses providers share the processor (Codex's response.done is covered with the
        // Codex cases of PiSharp.ProviderApis.Tests).
        const string terminal = """{"type":"response.completed","response":{"id":"r1","status":"completed","output":[],"input_transformations":[""" + PrefixMismatch + "," + TurnBoundary +
            """],"usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}}""";
        static void Recorded(AssistantMessage message, string provider)
        {
            Check(message.StopReason == StopReason.Stop, provider + " failed: " + PiWireJson.WriteMessage(message));
            var diagnostics = JsonNode.Parse(Sync160Diagnostics(message) ?? throw new InvalidOperationException(provider + " recorded no diagnostic."))!.AsArray();
            Equal(1, diagnostics.Count);
            Equal("openai_input_transformations", (string?)diagnostics[0]!["type"]);
            Check(diagnostics[0]!["timestamp"]!.GetValue<long>() > 1_700_000_000_000, provider + " diagnostic has no timestamp.");
            BodyEqual("{\"transformations\":[" + PrefixMismatch + "]}", diagnostics[0]!["details"]!.ToJsonString());
            // The diagnostic travels with the stored message.
            Check(PiWireJson.WriteMessage(message).ToString().Contains("\"diagnostics\":[{\"type\":\"openai_input_transformations\"", StringComparison.Ordinal), provider + " wire message lost the diagnostic.");
        }
        var openai = new ModelDescriptor("gpt-6.2-sol", "openai-responses", "openai");
        var metadata = JsonData.Parse("""{"id":"gpt-6.2-sol","name":"GPT-6.2 Sol","api":"openai-responses","provider":"openai","baseUrl":"https://api.openai.com/v1","reasoning":true,"input":["text"],"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0},"contextWindow":272000,"maxTokens":128000}""");
        using (var handler = new Handler(_ => Task.FromResult(Sse(terminal))))
        using (var provider = NativeProviderFactory.CreateResponses(openai, new("https://api.openai.com/v1/responses"), "sk-inert-provider-sync", new(true), null, handler, metadata))
            Recorded((await new ChatClient(provider).CompleteAsync(new ChatRequest(openai, [Ask], 1)).WaitAsync(Deadline)).Message, "OpenAI Responses");

        var azure = new ModelDescriptor("azure-model", "azure-openai-responses", "azure");
        var azureMetadata = JsonData.Parse("""{"id":"azure-model","api":"azure-openai-responses","provider":"azure","baseUrl":"https://r.openai.azure.com/openai/v1","reasoning":true}""");
        using var azureHandler = new Handler(_ => Task.FromResult(Sse(terminal)));
        using var client = new HttpClient(azureHandler);
        var transport = new AzureResponsesTransport(client, new AzureResponsesRequestFactory(azure, new AzureResponsesOptions(azureMetadata, new(true))), Key);
        Recorded((await new ChatClient(transport).CompleteAsync(new ChatRequest(azure, [Ask], 1)).WaitAsync(Deadline)).Message, "Azure OpenAI Responses");
    }

    // ---------------------------------------------------------------- google-shared.ts, google-generative-ai.ts, google-vertex.ts

    private static readonly ModelDescriptor Sync160Google = new("test-model", "google-generative-ai", "test-provider");
    // The native Vertex transport admits the google-vertex provider only.
    private static readonly ModelDescriptor Sync160Vertex = new("test-model", "google-vertex", "google-vertex");
    private static JsonData Sync160GoogleRow(string api) => JsonData.Parse("{\"type\":\"chat\",\"id\":\"test-model\",\"name\":\"Test Model\",\"api\":\"" + api +
        "\",\"provider\":\"" + (api == "google-vertex" ? "google-vertex" : "test-provider") + "\",\"baseUrl\":\"https://upstream.test/v1\",\"reasoning\":false,\"input\":[\"text\"],\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0},\"contextWindow\":10000,\"maxTokens\":1000}");
    private static readonly Uri Sync160VertexEndpoint = new("https://upstream.test/v1/publishers/google/models/test-model:streamGenerateContent?alt=sse");

    /// <summary>One request through each Google adapter over the given handler; the hook receives each adapter's SDK parameters.</summary>
    private static async Task<(AssistantMessage Google, AssistantMessage Vertex)> Sync160GoogleBoth(HttpMessageHandler handler, Action<JsonData>? onPayload = null)
    {
        using var client = new HttpClient(handler, disposeHandler: false);
        var hooks = new GoogleGenerativeAIHooks { OnPayload = (payload, _, _) => { onPayload?.Invoke(payload); return ValueTask.FromResult<JsonData?>(null); } };
        var google = new GoogleGenerativeAIHttpTransport(client, Sync160Google, new GoogleGenerativeAIOptions(Sync160GoogleRow("google-generative-ai"), "test-key") { Hooks = hooks });
        var vertex = new GoogleVertexHttpTransport(client, Sync160Vertex, new GoogleVertexOptions(Sync160VertexEndpoint, "test-key",
            new GoogleGenerativeAIOptions(Sync160GoogleRow("google-vertex")) { Hooks = hooks }) { ApiKeyMode = true });
        return ((await new ChatClient(google).CompleteAsync(new(Sync160Google, [Ask], 1)).WaitAsync(Deadline)).Message,
            (await new ChatClient(vertex).CompleteAsync(new(Sync160Vertex, [Ask], 1)).WaitAsync(Deadline)).Message);
    }

    private static async Task Sync160GoogleContinuation()
    {
        // #12097: @google/genai 2.32.0 adds FinishReason.CONTINUATION (the per-request token limit was reached before generation
        // completed) and continues automatically by default; the adapters turn that off. The turn ends as "length", like MAX_TOKENS,
        // and no continuation request follows the candidate's continuationToken.
        var requests = 0;
        using var handler = new Handler(_ =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"partial\"}]},\"finishReason\":\"CONTINUATION\",\"continuationToken\":\"token-1\"}]}\n\n",
                Encoding.UTF8, "text/event-stream") });
        });
        var (google, vertex) = await Sync160GoogleBoth(handler);
        Equal(2, requests);
        foreach (var message in new[] { google, vertex })
        {
            Equal(StopReason.Length, message.StopReason);
            Check(message.Content.Single() is TextContent { Text: "partial" }, "The partial text was lost.");
            Equal("CONTINUATION", message.ExtraProperties!.TryGet("rawStopReason", out var raw) ? raw!.Value.GetString() : null);
            Check(!message.ExtraProperties.TryGet("errorMessage", out _), "A CONTINUATION finish was an error.");
        }
    }

    private static async Task Sync160GoogleInjectedHandler()
    {
        // #12103: Pi's Google adapters now pass a custom fetch to the SDK instead of rejecting it. The native adapters have always sent
        // through the HttpClient they are given (Vertex ADC token requests use GoogleApplicationDefaultCredentials' own invoker): the
        // injected handler sees both adapters' requests and its rejection is the error shown.
        var urls = new List<string>(); var bodies = new List<JsonNode>(); var parameters = new List<JsonNode>();
        using var handler = new Handler(async request =>
        {
            urls.Add(request.RequestUri!.AbsoluteUri); bodies.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync())!);
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":{"message":"upstream rejected request"}}""", Encoding.UTF8, "application/json") };
        });
        var (google, vertex) = await Sync160GoogleBoth(handler, payload => parameters.Add(JsonNode.Parse(payload.ToString())!));
        Equal(2, urls.Count);
        Check(urls.All(url => url.StartsWith("https://upstream.test/v1", StringComparison.Ordinal)), "A request left the injected handler's endpoint: " + string.Join(" ", urls));
        foreach (var message in new[] { google, vertex })
        {
            Equal(StopReason.Error, message.StopReason);
            Check(message.ExtraProperties!.TryGet("errorMessage", out var error) && error!.Value.GetString()!.Contains("upstream rejected request", StringComparison.Ordinal),
                "The handler's rejection is not the error shown: " + PiWireJson.WriteMessage(message));
        }
        // buildParams: the SDK parameters a payload hook sees switch the SDK's automatic continuation off; the member is an SDK
        // switch and never part of the REST body.
        Equal(2, parameters.Count);
        foreach (var payload in parameters)
        {
            Check(payload["config"]!["automaticContinuation"] is JsonValue flag && flag.GetValueKind() == JsonValueKind.False, "The SDK parameters do not turn automatic continuation off: " + payload.ToJsonString());
            Equal("automaticContinuation", payload["config"]!.AsObject().Last().Key);
        }
        foreach (var body in bodies)
            Check(!body.ToJsonString().Contains("automaticContinuation", StringComparison.Ordinal), "automaticContinuation reached the REST body: " + body.ToJsonString());
        // A hook may hand the member back, whatever its value: it stays out of the body and no continuation request is sent.
        var wire = GoogleRequestProjector.WireBody(JsonData.Parse("""{"model":"test-model","contents":[{"role":"user","parts":[{"text":"hi"}]}],"config":{"maxOutputTokens":5,"automaticContinuation":true}}"""));
        BodyEqual("""{"contents":[{"parts":[{"text":"hi"}],"role":"user"}],"generationConfig":{"maxOutputTokens":5}}""", wire.ToJsonString());
    }
}
