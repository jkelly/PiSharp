// PiSharp-only (no upstream Pi source): shared catalog capability binding for native Anthropic entry points.
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.AnthropicMessages;

internal static class AnthropicCatalogCompatibility
{
    internal static AnthropicMessagesRequestOptions Apply(AnthropicMessagesRequestOptions options, JsonData? metadata)
    {
        if (metadata is null || !metadata.Value.TryGetUtf16Property("compat", out var compat) || compat.ValueKind != JsonValueKind.Object) return options;
        return options with
        {
            SupportsTemperature = options.SupportsTemperature && !(compat.TryGetUtf16Property("supportsTemperature", out var temperature) && temperature.ValueKind == JsonValueKind.False),
            SupportsMessageThreads = options.SupportsMessageThreads ?? (compat.TryGetUtf16Property("supportsMessageThreads", out var threads) &&
                threads.ValueKind is JsonValueKind.True or JsonValueKind.False ? threads.GetBoolean() : null)
        };
    }
}
