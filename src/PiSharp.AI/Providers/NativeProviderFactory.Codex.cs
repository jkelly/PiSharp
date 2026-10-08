// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/providers/openai-codex.ts (api "openai-codex-responses",
// ChatGPT OAuth) and packages/ai/src/api/openai-codex-responses.ts (streamSimple).
using PiSharp.AI.Protocols.OpenAICodexResponses;
using PiSharp.Contracts;

namespace PiSharp.AI.Providers;

public static partial class NativeProviderFactory
{
    /// <summary>OpenAI Codex Responses for provider "openai-codex". <paramref name="accessToken"/> supplies the (refreshed) ChatGPT
    /// OAuth access token for every request; each request's thinking level selects the streamSimple resolution.</summary>
    public static NativeHttpModelProvider CreateCodexResponses(ModelDescriptor model, JsonData modelMetadata, OpenAICodexResponsesOptions options,
        Func<CancellationToken, ValueTask<string>> accessToken, HttpMessageHandler? handler = null, string? defaultThinkingLevel = null)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(modelMetadata);
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(accessToken);
        if (model.Provider != "openai-codex" || model.Api != "openai-codex-responses" || string.IsNullOrWhiteSpace(model.Id) ||
            model.Id.Length > 1024 || model.Id.Any(char.IsControl)) throw new ArgumentException("Unsupported native model or endpoint selection.");
        return Bind(model, handler, client => new OpenAICodexResponsesTransport(client, model, modelMetadata, options, accessToken, defaultThinkingLevel));
    }
}
