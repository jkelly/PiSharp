// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/ai/src/models.ts (createProvider: one API implementation streams every
// catalog model of that API at the model's own baseUrl), packages/ai/src/api/simple-options.ts (buildBaseOptions: maxTokens defaults to
// model.maxTokens; adjustMaxTokensForThinking) and packages/ai/src/api/anthropic-messages.ts (streamSimple budget thinking, buildParams
// compat.supportsMidConvoEffort and thinkingLevelMap.off, metadata.user_id). The per-API bindings mirror PiSharp.Cli's catalog routes
// (LiveSessionSelection.CreateCatalogProvider), which the durable core cannot reference.
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.AI.Protocols.GoogleGenerativeAI;
using PiSharp.AI.Protocols.MistralConversations;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.AI.Protocols.PiMessages;
using PiSharp.AI.Providers;
using PiSharp.Contracts;

namespace PiSharp.Durable;

/// <summary>The request-shaping settings of one durable request, resolved from <see cref="DurableStreamOptions"/>.</summary>
internal sealed record NativeRouteSettings
{
    /// <summary>The output cap (pi-ai <c>maxTokens</c>, default the model's).</summary>
    public required int MaxTokens { get; init; }
    /// <summary><c>none</c>, <c>short</c> or <c>long</c>.</summary>
    public required string CacheRetention { get; init; }
    /// <summary>Caller headers (pi-ai <c>headers</c>), applied after model headers.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    /// <summary>Anthropic <c>metadata.user_id</c>.</summary>
    public string? UserId { get; init; }
    /// <summary>Provider retries (google-generative-ai only).</summary>
    public int? MaxRetries { get; init; }
    /// <summary>Longest server-requested retry delay (google-generative-ai only).</summary>
    public double? MaxRetryDelayMs { get; init; }
}

/// <summary>Binds one catalog model to its native PiSharp.AI protocol with an explicit key.</summary>
internal static class NativeModelRoutes
{
    /// <summary>The pi-ai APIs this adapter streams.</summary>
    internal static ImmutableArray<string> Apis { get; } =
        ["openai-completions", "openai-responses", "anthropic-messages", "google-generative-ai", "mistral-conversations", "pi-messages"];

    /// <summary>Providers whose auth resolves per request (token exchange, ambient credentials, account placeholders or OAuth) and so
    /// need the host's auth (P12); this adapter refuses them even for a routable API.</summary>
    internal static ImmutableArray<string> HostAuthProviders { get; } =
        ["github-copilot", "openai-codex", "amazon-bedrock", "google-vertex", "cloudflare-workers-ai", "cloudflare-ai-gateway", "azure"];

    /// <summary>Whether this adapter can stream <paramref name="api"/> for <paramref name="provider"/>.</summary>
    internal static bool Supports(string provider, string api) => Apis.Contains(api) && !HostAuthProviders.Contains(provider);

    private static readonly IReadOnlyDictionary<string, double> AnthropicBudgets = new Dictionary<string, double>(StringComparer.Ordinal)
    { ["minimal"] = 1024, ["low"] = 2048, ["medium"] = 8192, ["high"] = 16384, ["xhigh"] = 16384, ["max"] = 16384 };

    /// <summary>
    /// anthropic-messages streamSimple budget thinking (adjustMaxTokensForThinking): a reasoning level without
    /// <c>compat.forceAdaptiveThinking</c> raises the cap by the level's budget, up to the model's cap. Other APIs keep the cap.
    /// </summary>
    internal static int ThinkingCap(JsonElement raw, string api, string level, int maximum)
    {
        if (api != "anthropic-messages" || level == "off" || !AnthropicBudgets.TryGetValue(level, out var budget)) return maximum;
        if (raw.TryGetProperty("compat", out var compat) && compat.ValueKind == JsonValueKind.Object &&
            compat.TryGetProperty("forceAdaptiveThinking", out var adaptive) && adaptive.ValueKind == JsonValueKind.True) return maximum;
        if (!raw.TryGetProperty("maxTokens", out var cap) || !cap.TryGetDouble(out var ceiling) || ceiling <= 0) return maximum;
        return (int)Math.Max(maximum, Math.Min(Math.Min(maximum + budget, ceiling), int.MaxValue));
    }

    /// <summary>Binds <paramref name="model"/> (its catalog row is <paramref name="metadata"/>) over <paramref name="handler"/>, which stays
    /// caller-owned.</summary>
    internal static NativeHttpModelProvider Bind(ModelDescriptor model, JsonData metadata, string key, NativeRouteSettings settings,
        HttpMessageHandler handler)
    {
        var raw = metadata.Value;
        var baseUrl = raw.TryGetProperty("baseUrl", out var url) && url.ValueKind == JsonValueKind.String ? url.GetString()! : "";
        if (baseUrl.Length == 0 || baseUrl.Contains('{'))
            throw new InvalidOperationException($"Model {model.Provider}/{model.Id} has no usable baseUrl");
        var reasoning = raw.TryGetProperty("reasoning", out var flag) && flag.ValueKind == JsonValueKind.True;
        var images = raw.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Array &&
            input.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == "image");
        var modelHeaders = raw.TryGetProperty("headers", out var rowHeaders) && rowHeaders.ValueKind == JsonValueKind.Object
            ? JsonData.FromElement(rowHeaders) : null;
        var headers = settings.Headers is { Count: > 0 } given ? JsonData.Parse(JsonSerializer.Serialize(given)) : null;
        var maximum = settings.MaxTokens;
        var retention = settings.CacheRetention;
        switch (model.Api)
        {
            case "openai-completions":
                return NativeProviderFactory.CreateCatalogCompletions(model, baseUrl, key,
                    new CompletionsTranscriptProjectionOptions(Reasoning: reasoning, ToolDeclarations: new()) { ModelSupportsImages = images },
                    new CompletionsKeyAuthRequestOptions(MaxTokens: maximum, CacheRetention: retention switch
                    {
                        "none" => CompletionsCacheRetention.None, "long" => CompletionsCacheRetention.Long, _ => CompletionsCacheRetention.Short
                    }, ModelHeaders: modelHeaders, Headers: headers), metadata, handler);
            case "openai-responses":
                return NativeProviderFactory.CreateCatalogResponses(model, baseUrl, key,
                    new ResponsesTranscriptProjectionOptions(Reasoning: reasoning, ToolDeclarations: new()) { ModelSupportsImages = images },
                    new ResponsesKeyAuthRequestOptions(SupportsMaxOutputTokens: true, MaxOutputTokens: maximum, CacheRetention: retention)
                    { ModelHeaders = modelHeaders, Headers = headers }, metadata, handler);
            case "anthropic-messages":
            {
                var projection = AnthropicCompat(raw, new AnthropicMessagesRequestOptions(MaximumTokens: maximum, ModelReasoning: reasoning,
                    ModelSupportsImages: images, ThinkingEnabled: false, UserId: settings.UserId, CacheRetention: retention switch
                    {
                        "none" => AnthropicCacheRetention.None, "long" => AnthropicCacheRetention.Long, _ => AnthropicCacheRetention.Short
                    }));
                var request = new AnthropicMessagesKeyAuthRequestOptions(MaxTokens: maximum, ModelHeaders: modelHeaders, Headers: headers);
                // The fixed Anthropic factory owns provider "anthropic"; every other anthropic-messages provider binds by catalog.
                return model.Provider == "anthropic"
                    ? NativeProviderFactory.CreateAnthropic(model, new Uri(baseUrl), key, projection, request, handler, metadata)
                    : NativeProviderFactory.CreateCatalogAnthropic(model, baseUrl, key, projection, request, metadata, handler);
            }
            case "google-generative-ai":
                return NativeProviderFactory.CreateCatalogGoogle(model, key, metadata, new GoogleGenerativeAIOptions(metadata)
                {
                    MaxTokens = maximum, Headers = headers, MaxRetries = Math.Clamp(settings.MaxRetries ?? 0, 0, 32),
                    MaxRetryDelayMilliseconds = settings.MaxRetryDelayMs is { } delay ? (int)Math.Clamp(delay, 0, int.MaxValue) : 60_000,
                }, handler);
            case "pi-messages":
                return NativeProviderFactory.CreateCatalogPiMessages(model, key, metadata, new PiMessagesOptions(metadata)
                { MaxTokens = maximum, Headers = headers, CacheRetention = retention }, handler);
            case "mistral-conversations":
            {
                var endpoint = new Uri(baseUrl);
                var costs = raw.GetProperty("cost");
                var options = new MistralTextOptions(endpoint, SupportsText: true, new(costs.GetProperty("input").GetDouble(),
                    costs.GetProperty("output").GetDouble(), costs.GetProperty("cacheRead").GetDouble(), costs.GetProperty("cacheWrite").GetDouble()), "PiSharp")
                {
                    MaxTokens = maximum, Reasoning = reasoning, CachePrompt = retention != "none", SupportsImages = images,
                    Headers = settings.Headers?.ToImmutableDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal),
                };
                return NativeProviderFactory.CreateCatalogMistral(model, baseUrl, key, metadata, options, simple: true, handler);
            }
            default:
                throw new NotSupportedException($"The {model.Api} API has no durable route in PiSharp yet");
        }
    }

    /// <summary>anthropic-messages buildParams: <c>compat.supportsMidConvoEffort</c> selects managed effort, <c>thinkingLevelMap.off === null</c>
    /// forbids disabled thinking, and fallback models come from the catalog row.</summary>
    private static AnthropicMessagesRequestOptions AnthropicCompat(JsonElement raw, AnthropicMessagesRequestOptions projection)
    {
        var supportsOff = !(raw.TryGetProperty("thinkingLevelMap", out var map) && map.ValueKind == JsonValueKind.Object &&
            map.TryGetProperty("off", out var off) && off.ValueKind == JsonValueKind.Null);
        return projection with
        {
            SupportsMidConversationEffort = raw.TryGetProperty("compat", out var compat) && compat.ValueKind == JsonValueKind.Object &&
                compat.TryGetProperty("supportsMidConvoEffort", out var mid) && mid.ValueKind == JsonValueKind.True,
            SupportsThinkingOff = supportsOff,
            AllowedFallbackModels = NativeProviderFactory.AnthropicFallbackModels(raw),
        };
    }
}
