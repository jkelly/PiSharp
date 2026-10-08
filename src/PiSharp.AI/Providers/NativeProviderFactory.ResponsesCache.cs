using System.Text.Json;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Contracts;

namespace PiSharp.AI.Providers;

public static partial class NativeProviderFactory
{
    // Full model metadata is authoritative; standalone explicit profiles remain caller owned.
    private static ResponsesTranscriptProjectionOptions ResponsesProjectionOptionsForModel(
        ResponsesTranscriptProjectionOptions options, JsonData? metadata)
    {
        if (metadata is null) return options;
        try
        {
            var compat = metadata.Value.TryGetProperty("compat", out var value) ? value : default;
            if (compat.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object))
                throw new InvalidOperationException();
            var supported = false;
            if (compat.ValueKind == JsonValueKind.Object && compat.TryGetProperty("supportsStrictMode", out var flag) && flag.ValueKind != JsonValueKind.Null)
            {
                if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidOperationException();
                supported = flag.GetBoolean();
            }
            return options with { ToolDeclarations = (options.ToolDeclarations ?? new()) with { SupportsStrictMode = supported } };
        }
        catch (InvalidOperationException) { throw new ArgumentException("Unsupported native Responses strict compatibility metadata."); }
    }

    // Pi d86654abb8862e201933517d6f1fce9f88dd117f openai-responses.ts getCompat.
    // Caller-supplied full model metadata is authoritative when available. Without
    // metadata, retain the existing explicit native compatibility option profile.
    private static ResponsesKeyAuthRequestOptions ResponsesCacheOptionsForModel(
        ResponsesKeyAuthRequestOptions? options, JsonData? metadata)
    {
        var result = options ?? new();
        if (metadata is null) return result;
        try
        {
            var compat = metadata.Value.TryGetProperty("compat", out var value) ? value : default;
            if (compat.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object))
                throw new InvalidOperationException();
            bool Flag(string name, bool fallback)
            {
                if (compat.ValueKind != JsonValueKind.Object || !compat.TryGetProperty(name, out var flag) || flag.ValueKind == JsonValueKind.Null)
                    return fallback;
                if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidOperationException();
                return flag.GetBoolean();
            }
            return result with
            {
                ModelHeaders = metadata.Value.TryGetProperty("headers", out var headers) ? JsonData.Parse(headers.GetRawText()) : null,
                ModelSamplingParams = metadata.Value.TryGetProperty("samplingParams", out var sampling) ? JsonData.Parse(sampling.GetRawText()) : null,
                SupportsMaxOutputTokens = Flag("supportsMaxOutputTokens", true),
                SessionAffinityFormat = compat.ValueKind == JsonValueKind.Object && compat.TryGetProperty("sessionAffinityFormat", out var affinity) && affinity.ValueKind != JsonValueKind.Null
                    ? affinity.GetString() ?? "openai" : "openai",
                SupportsLongCacheRetention = Flag("supportsLongCacheRetention", true),
                SupportsExplicitPromptCacheMode = Flag("supportsExplicitPromptCacheMode", false)
            };
        }
        catch (InvalidOperationException)
        {
            throw new ArgumentException("Unsupported native Responses cache compatibility metadata.");
        }
    }
}
