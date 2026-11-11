// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): anthropic-messages.ts getAnthropicCompat/buildParams.
using System.Text.Json;
using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.AnthropicMessages;

internal static class AnthropicCatalogCompatibility
{
    internal static AnthropicMessagesRequestOptions Apply(AnthropicMessagesRequestOptions options, JsonData? metadata)
    {
        var compat = metadata is not null && metadata.Value.TryGetUtf16Property("compat", out var value) ? value : default;
        return options with
        {
            ForceAdaptiveThinking = options.ForceAdaptiveThinking ?? Flag(compat, "forceAdaptiveThinking") ?? false,
            SupportsMidConversationEffort = options.SupportsMidConversationEffort ?? Flag(compat, "supportsMidConvoEffort") ?? false,
            SupportsTemperature = options.SupportsTemperature ?? Flag(compat, "supportsTemperature") ?? true,
            AllowEmptyThinkingSignatures = options.AllowEmptyThinkingSignatures ?? Flag(compat, "allowEmptySignature") ?? false,
            SupportsLongCacheRetention = options.SupportsLongCacheRetention ?? Flag(compat, "supportsLongCacheRetention") ?? true,
            SupportsEagerToolInputStreaming = options.SupportsEagerToolInputStreaming ?? Flag(compat, "supportsEagerToolInputStreaming") ?? true,
            SupportsStrictTools = options.SupportsStrictTools ?? Flag(compat, "supportsStrictTools") ?? false,
            SupportsCacheControlOnTools = options.SupportsCacheControlOnTools ?? Flag(compat, "supportsCacheControlOnTools") ?? true,
            SupportsMidConversationSystemMessages = options.SupportsMidConversationSystemMessages ?? Flag(compat, "supportsMidConvoSystemMessages") ?? false,
            SupportsMidConversationToolChanges = options.SupportsMidConversationToolChanges ?? Flag(compat, "supportsMidConvoToolChanges") ?? false,
            SupportsMessageThreads = options.SupportsMessageThreads ?? Flag(compat, "supportsMessageThreads"),
            AllowedFallbackModels = options.AllowedFallbackModels.IsDefault ? FallbackModels(compat) : options.AllowedFallbackModels
        };
    }

    private static bool? Flag(JsonElement compat, string name) =>
        compat.ValueKind == JsonValueKind.Object && compat.TryGetUtf16Property(name, out var flag) &&
        flag.ValueKind is JsonValueKind.True or JsonValueKind.False ? flag.GetBoolean() : null;

    private static ImmutableArray<string> FallbackModels(JsonElement compat) =>
        compat.ValueKind == JsonValueKind.Object && compat.TryGetUtf16Property("allowedFallbackModels", out var fallbacks) &&
        fallbacks.ValueKind == JsonValueKind.Array
            ? fallbacks.EnumerateArray().Where(model => model.ValueKind == JsonValueKind.Object &&
                model.TryGetUtf16Property("model", out var id) && id.ValueKind == JsonValueKind.String)
                .Select(model => model.GetUtf16Property("model").GetString()!).ToImmutableArray()
            : default;

    internal static AnthropicMessagesKeyAuthRequestOptions Apply(AnthropicMessagesKeyAuthRequestOptions? options,
        JsonData? metadata, ModelDescriptor model, Uri endpoint)
    {
        var result = options ?? new();
        var compat = metadata is not null && metadata.Value.TryGetUtf16Property("compat", out var value) ? value : default;
        var openRouter = model.Provider == "openrouter" || endpoint.AbsoluteUri.Contains("openrouter.ai", StringComparison.Ordinal);
        var format = compat.ValueKind == JsonValueKind.Object && compat.TryGetUtf16Property("sessionAffinityFormat", out var affinity) &&
            affinity.ValueKind == JsonValueKind.String ? affinity.GetString() : null;
        return result with
        {
            SendSessionAffinityHeaders = result.SendSessionAffinityHeaders ?? Flag(compat, "sendSessionAffinityHeaders") ?? openRouter,
            SessionAffinityHeader = result.SessionAffinityHeader ?? (format == "openrouter" || format is null && openRouter ? "x-session-id" : "x-session-affinity")
        };
    }
}
