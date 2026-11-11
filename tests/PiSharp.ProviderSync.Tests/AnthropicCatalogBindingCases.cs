// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/ai/src/api/anthropic-messages.ts, packages/ai/src/providers/anthropic.ts.
// Authored native regression; not an upstream execution capture.
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Authentication;
using PiSharp.AI.Catalogs;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.AI.Providers;
using PiSharp.Contracts;

internal static partial class Program
{
    private static FrozenCatalogModel ReleasedAnthropic(string provider, string id) =>
        CurrentModelCatalog.Get(provider)!.Models.Single(model => model.Id == id && model.DeclaredApi == "anthropic-messages");

    private static ImmutableArray<TranscriptEntry> CatalogBindingTranscript(ModelDescriptor model, bool required = true) =>
    [
        Entry("{\"role\":\"system\",\"content\":\"initial instructions\",\"toolsAdded\":[" + StrictTool("preferred", "prefer") + "," +
            StrictTool("required", required ? "require" : null) + "," + StrictTool("retired", null) + "],\"timestamp\":0}"),
        UserText("one", 1),
        new("assistant", PiWireJson.WriteMessage(new(model.Api, model.Provider, model.Id, 2,
            [new TextContent("before")], TokenUsage.Zero, StopReason.Stop))),
        Entry("{\"role\":\"system\",\"content\":\"changed instructions\",\"toolsAdded\":[" + StrictTool("added", "prefer") +
            "],\"toolsRemoved\":[{\"name\":\"retired\"}],\"timestamp\":3}"),
        UserText("two", 4)
    ];

    private static async Task AnthropicReleasedCatalogBindings()
    {
        var row = ReleasedAnthropic("anthropic", "claude-fable-5-2");
        var model = new ModelDescriptor(row.Id, row.DeclaredApi, row.Provider);
        foreach (var route in new[] { "direct", "resolved", "simple", "dynamic" })
        {
            using var endpoint = new MessagesEndpoint(_ => MessagesStream(model.Id, "ok"));
            var projection = new AnthropicMessagesRequestOptions(4096, ModelReasoning: true, ThinkingEnabled: true);
            using var client = new HttpClient(endpoint, disposeHandler: false);
            using var provider = route switch
            {
                "direct" => NativeProviderFactory.CreateAnthropic(model, new(row.BaseUrl), Key, projection,
                    handler: endpoint, modelMetadata: row.Raw),
                "resolved" => AnthropicResolvedProviderFactory.Create(model, new(row.BaseUrl),
                    new AnthropicInjectedAuthenticationBinding(new ResolvedAuthentication(AuthenticationKind.ApiKey,
                        AuthenticationOrigin.EnvironmentApiKey, Key)), projection, handler: endpoint, modelMetadata: row.Raw),
                "dynamic" => NativeProviderFactory.CreateProviderRoute(model, row.Raw,
                    new(_ => ValueTask.FromResult(new ProviderRequestAuth(Key))) { MaxTokens = 4096 }, endpoint),
                _ => null
            };
            IChatTransport transport = provider is not null ? provider.Transport : new AnthropicMessagesSimpleRequestFactory(client,
                new(row.BaseUrl), model, new(row.Raw, projection, "high") { ApiKey = Key, MaxTokens = 4096 });
            var result = await new ChatClient(transport).CompleteAsync(new(model, CatalogBindingTranscript(model), 5)
                { ThinkingLevel = "high", DisableContinuation = true }).WaitAsync(Deadline);
            Equal(StopReason.Stop, result.Message.StopReason);
            var sent = endpoint.Requests.Single(); var body = JsonNode.Parse(sent.Body)!.AsObject();
            Equal("true", Strictness(body["tools"]![0]));
            Equal("true", Strictness(body["tools"]![1]));
            Equal("__pi_deferred_placeholder__", (string?)body["tools"]!.AsArray()[^1]!["name"]);
            var updates = body["messages"]!.AsArray().Where(message => (string?)message!["role"] == "system")
                .SelectMany(message => message!["content"]!.AsArray()).OfType<JsonObject>().ToArray();
            Check(updates.Any(block => (string?)block["text"] == "changed instructions"), route + " lost mid-conversation system instructions.");
            var addition = updates.Single(block => (string?)block["type"] == "tool_addition");
            Equal("true", Strictness(addition["tool"]!["definition"]));
            Check(updates.Any(block => (string?)block["type"] == "tool_removal"), route + " lost tool removal.");
            Equal("adaptive", (string?)body["thinking"]!["type"]);
            Check(sent.Headers.Contains("inline-tools-2026-09-15", StringComparison.Ordinal), route + " omitted inline-tools beta.");
            Check(body.ContainsKey("fallbacks"), route + " omitted released fallback models.");
        }
    }

    private static async Task AnthropicCatalogOverridesAndDefaults()
    {
        var row = ReleasedAnthropic("anthropic", "claude-fable-5-2");
        var model = new ModelDescriptor(row.Id, row.DeclaredApi, row.Provider);
        var metadata = JsonNode.Parse(row.Raw.ToString())!.AsObject();
        metadata["compat"]!["supportsTemperature"] = false;
        metadata["compat"]!["supportsEagerToolInputStreaming"] = true;
        metadata["compat"]!["supportsLongCacheRetention"] = true;
        metadata["compat"]!["supportsCacheControlOnTools"] = true;
        metadata["compat"]!["allowEmptySignature"] = true;
        var explicitOptions = new AnthropicMessagesRequestOptions(100, Temperature: 0.2m, CacheRetention: AnthropicCacheRetention.Long,
            SupportsTemperature: true, SupportsEagerToolInputStreaming: false, SupportsStrictTools: false,
            SupportsLongCacheRetention: false, SupportsCacheControlOnTools: false, AllowEmptyThinkingSignatures: false,
            SupportsMidConversationSystemMessages: false, SupportsMidConversationToolChanges: false,
            SupportsMidConversationEffort: false, ForceAdaptiveThinking: false, AllowedFallbackModels: []);
        using var client = new HttpClient(new StrictEndpoint(_ => StrictSuccess()));
        var simple = new AnthropicMessagesSimpleRequestFactory(client, new(row.BaseUrl), model,
            new(JsonData.Parse(metadata.ToJsonString()), explicitOptions) { ApiKey = Key });
        using var sent = simple.Create(new(model, CatalogBindingTranscript(model, required: false), 5), Key);
        var body = JsonNode.Parse(await sent.Content!.ReadAsStringAsync())!.AsObject();
        Equal("0.2", body["temperature"]!.ToJsonString());
        Check(!body.ContainsKey("fallbacks"), "Explicit empty fallback list must win.");
        Check(!body["messages"]!.AsArray().Any(message => (string?)message!["role"] == "system"), "Explicit false system compatibility must win.");
        Check(body["system"]!.ToJsonString().Contains("changed instructions", StringComparison.Ordinal), "Disabled native updates must still retain folded instructions.");
        Check(body["tools"]!.AsArray().All(tool => tool!["strict"] is null && tool["cache_control"] is null && tool["eager_input_streaming"] is null),
            "Explicit false tool compatibility must win.");
        Check(!body.ToJsonString().Contains("\"ttl\":\"1h\"", StringComparison.Ordinal), "Explicit short-only capability must win.");

        // Without metadata the low-level projector retains its established defaults.
        var raw = new AnthropicMessagesRequestProjector(new(100, Temperature: 0.2m, CacheRetention: AnthropicCacheRetention.Long))
            .Project(new(model, StrictTranscript(StrictTool("preferred", "prefer"))));
        var defaults = JsonNode.Parse(raw.ToString())!.AsObject();
        Equal("0.2", defaults["temperature"]!.ToJsonString());
        Equal(null, defaults["tools"]![0]!["strict"]);
        Equal(true, (bool?)defaults["tools"]![0]!["eager_input_streaming"]);
        Equal("1h", (string?)defaults["tools"]![0]!["cache_control"]!["ttl"]);
    }

    private static async Task AnthropicCatalogGatewayIsolation()
    {
        var row = ReleasedAnthropic("openrouter", "anthropic/claude-fable-5.2");
        var model = new ModelDescriptor(row.Id, row.DeclaredApi, row.Provider);
        using var endpoint = new MessagesEndpoint(_ => MessagesStream(model.Id, "ok"));
        using var provider = NativeProviderFactory.CreateCatalogAnthropic(model, row.BaseUrl, Key,
            new(4096, ModelReasoning: true, ThinkingEnabled: true), new(SessionId: "gateway-session"), row.Raw, endpoint);
        await new ChatClient(provider.Transport).CompleteAsync(new(model, CatalogBindingTranscript(model, required: false), 5)
            { ThinkingLevel = "high" }).WaitAsync(Deadline);
        var sent = endpoint.Requests.Single(); var body = JsonNode.Parse(sent.Body)!.AsObject();
        Check(body["tools"]!.AsArray().All(tool => tool!["strict"] is null), "Gateway must not inherit direct Anthropic strict capability.");
        Check(!body["messages"]!.AsArray().Any(message => (string?)message!["role"] == "system" && message["content"] is JsonArray { Count: > 0 }),
            "Gateway must not inherit direct Anthropic native system/tool changes.");
        Check(sent.Headers.Contains("x-session-id: gateway-session", StringComparison.Ordinal), "OpenRouter affinity default was not bound.");
        Check(!body.ContainsKey("thread"), "Gateway must not inherit direct Anthropic message threads.");
    }

    private static async Task AnthropicCatalogOptionalCapabilities()
    {
        var row = ReleasedAnthropic("anthropic", "claude-fable-5-2");
        var model = new ModelDescriptor(row.Id, row.DeclaredApi, row.Provider);
        var metadata = JsonNode.Parse(row.Raw.ToString())!.AsObject();
        metadata["reasoning"] = false;
        metadata["compat"] = new JsonObject
        {
            ["supportsTemperature"] = false, ["supportsLongCacheRetention"] = false,
            ["supportsEagerToolInputStreaming"] = false, ["supportsCacheControlOnTools"] = false,
            ["allowEmptySignature"] = true, ["sendSessionAffinityHeaders"] = true, ["sessionAffinityFormat"] = "openrouter"
        };
        var replay = new TranscriptEntry("assistant", PiWireJson.WriteMessage(new(model.Api, model.Provider, model.Id, 2,
            [new ThinkingContent("unsigned thinking")], TokenUsage.Zero, StopReason.Stop)));
        var transcript = StrictTranscript(StrictTool("tool", "prefer")).Add(replay).Add(UserText("after", 3));
        foreach (var overridden in new[] { false, true })
        {
            var projection = new AnthropicMessagesRequestOptions(100, Temperature: 0.3m, CacheRetention: AnthropicCacheRetention.Long);
            var headers = new AnthropicMessagesKeyAuthRequestOptions(SessionId: "capability-session");
            if (overridden)
            {
                projection = projection with { SupportsTemperature = true, SupportsLongCacheRetention = true,
                    SupportsEagerToolInputStreaming = true, SupportsCacheControlOnTools = true, AllowEmptyThinkingSignatures = false };
                headers = headers with { SendSessionAffinityHeaders = false };
            }
            using var client = new HttpClient(new StrictEndpoint(_ => StrictSuccess()));
            var simple = new AnthropicMessagesSimpleRequestFactory(client, new(row.BaseUrl), model,
                new(JsonData.Parse(metadata.ToJsonString()), projection) { ApiKey = Key, KeyAuthOptions = headers });
            using var request = simple.Create(new(model, transcript), Key);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
            Equal(overridden, body.ContainsKey("temperature"));
            Equal(overridden, body["tools"]![0]!["eager_input_streaming"] is not null);
            Equal(overridden, body["tools"]![0]!["cache_control"] is not null);
            Equal(overridden, body.ToJsonString().Contains("\"ttl\":\"1h\"", StringComparison.Ordinal));
            var assistant = body["messages"]!.AsArray().Single(message => (string?)message!["role"] == "assistant");
            Equal(overridden ? "text" : "thinking", (string?)assistant!["content"]![0]!["type"]);
            Equal(!overridden, request.Headers.Contains("x-session-id"));
        }
    }
}
