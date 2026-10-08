// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/providers/azure.ts (api "azure-openai-responses")
// and packages/ai/src/api/azure-openai-responses.ts (streamSimple).
using System.Collections.Immutable;
using PiSharp.AI.Protocols.AzureResponses;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Contracts;

namespace PiSharp.AI.Providers;

public static partial class NativeProviderFactory
{
    private static readonly string[] AzureResponsesVariables =
        ["AZURE_OPENAI_BASE_URL", "AZURE_OPENAI_RESOURCE_NAME", "AZURE_OPENAI_API_VERSION", "AZURE_OPENAI_DEPLOYMENT_NAME_MAP"];

    /// <summary>
    /// Azure Responses under provider "azure" for the CLI's streamSimple route: each request's thinking level selects the
    /// Simple resolution (reasoning effort, context-clamped output cap) over the Azure Responses transport. The endpoint,
    /// API version and deployment come from the explicit options, then the copied AZURE_OPENAI_* values, then model.baseUrl.
    /// </summary>
    public static NativeHttpModelProvider CreateAzureResponses(ModelDescriptor model, string explicitApiKey, JsonData modelMetadata,
        ResponsesTranscriptProjectionOptions projection, double? maxTokens = null, AzureEndpointOptions? azure = null,
        HttpMessageHandler? handler = null, bool fixedReasoningOff = false)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(modelMetadata); ArgumentNullException.ThrowIfNull(projection);
        if (model.Provider != "azure" || model.Api != "azure-openai-responses" || string.IsNullOrWhiteSpace(model.Id) ||
            model.Id.Length > 1024 || model.Id.Any(char.IsControl)) throw new ArgumentException("Unsupported native model or endpoint selection.");
        if (string.IsNullOrEmpty(explicitApiKey) || explicitApiKey.Length > 4096 || explicitApiKey.Any(value => value is < '!' or > '~'))
            throw new ArgumentException("Invalid explicit native API key.");
        var values = ImmutableDictionary.CreateBuilder<string, string?>(StringComparer.Ordinal);
        foreach (var name in AzureResponsesVariables)
            if (azure?.Environment?.GetValue(name) is { } value) values[name] = value;
        // The Responses tool projection has no custom grammar tools: like the OpenAI Responses route, grammar-constrained tools
        // are declared as function tools, so the catalog's supportsOpenAIGrammarTools opt-in is not forwarded to the direct factory.
        if (System.Text.Json.Nodes.JsonNode.Parse(modelMetadata.ToString()) is System.Text.Json.Nodes.JsonObject metadata &&
            metadata["compat"] is System.Text.Json.Nodes.JsonObject compat && compat.Remove("supportsOpenAIGrammarTools"))
            modelMetadata = JsonData.Parse(metadata.ToJsonString());
        var direct = new AzureResponsesOptions(modelMetadata, projection)
        {
            AzureBaseUrl = azure?.AzureBaseUrl, AzureResourceName = azure?.AzureResourceName, AzureDeploymentName = azure?.AzureDeploymentName,
            ConfigurationValues = values.ToImmutable(), MaxTokens = maxTokens
        };
        return Bind(model, handler, client => new AzureResponsesThinkingTransport(model, client, direct, explicitApiKey, fixedReasoningOff));
    }

    /// <summary>Selects the Simple transport for each request's thinking level; levels come from the model metadata.</summary>
    private sealed class AzureResponsesThinkingTransport : IChatTransport, IThinkingLevelTransport
    {
        private readonly ModelDescriptor model;
        private readonly AzureResponsesSimpleTransport fallback;
        private readonly ImmutableDictionary<string, AzureResponsesSimpleTransport> levels;
        internal AzureResponsesThinkingTransport(ModelDescriptor model, HttpClient client, AzureResponsesOptions direct, string key, bool fixedOff)
        {
            this.model = model;
            fallback = new(client, model, new(direct, key));
            var supported = fixedOff ? ["off"] : fallback.GetSupportedThinkingLevels();
            levels = supported.ToImmutableDictionary(level => level, level => new AzureResponsesSimpleTransport(client, model,
                new(direct, key, fixedOff ? null : level)), StringComparer.Ordinal);
        }
        public ImmutableArray<string> GetSupportedThinkingLevels(ModelDescriptor selected) => selected == model
            ? [.. levels.Keys.OrderBy(level => ThinkingLevels.Ordered.IndexOf(level))] : throw new ArgumentException("Unknown native thinking model.");
        public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(request);
            if (request.Model != model) throw new ArgumentException("Unknown native thinking model.");
            if (request.ThinkingLevel is { } level) ThinkingLevels.Validate(this, model, level);
            return (request.ThinkingLevel is null ? fallback : levels[request.ThinkingLevel]).StreamAsync(request, cancellationToken);
        }
    }
}
