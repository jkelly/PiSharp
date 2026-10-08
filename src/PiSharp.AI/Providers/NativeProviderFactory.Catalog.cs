// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/models.ts (createProvider: one API implementation streams every
// catalog model of that API, at the model's own baseUrl), packages/ai/src/api/{openai-completions,openai-responses,anthropic-messages,
// google-generative-ai,mistral-conversations,pi-messages}.ts (client base URLs) and packages/ai/src/models.ts (getSupportedThinkingLevels).
using System.Collections.Immutable;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.AI.Protocols.GoogleGenerativeAI;
using PiSharp.AI.Protocols.MistralConversations;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.AI.Protocols.PiMessages;
using PiSharp.Contracts;

namespace PiSharp.AI.Providers;

/// <summary>
/// Catalog-addressed key-auth bindings: the same native protocols as the fixed OpenAI/OpenRouter/Anthropic/Mistral factories, for any
/// provider whose catalog (built-in shard, models.json or a remote catalog) declares the API. The endpoint is derived from the model's
/// <c>baseUrl</c> exactly as the upstream client does, and the complete model row is the metadata. Never discovers credentials.
/// </summary>
public static partial class NativeProviderFactory
{
    private static void ValidateCatalog(ModelDescriptor model, Uri endpoint, string key, string api, JsonData metadata)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(metadata);
        var raw = metadata.Value;
        if (model.Api != api || string.IsNullOrWhiteSpace(model.Id) || model.Id.Length > 1024 || model.Id.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(model.Provider) || model.Provider.Any(char.IsControl) || endpoint is null || !endpoint.IsAbsoluteUri ||
            endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0 ||
            raw.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !raw.TryGetProperty("id", out var id) || id.GetString() != model.Id || !raw.TryGetProperty("api", out var declared) || declared.GetString() != api ||
            !raw.TryGetProperty("provider", out var provider) || provider.GetString() != model.Provider)
            throw new ArgumentException("Unsupported native model or endpoint selection.");
        if (string.IsNullOrEmpty(key) || key.Length > 4096 || key.Any(value => value is < '!' or > '~'))
            throw new ArgumentException("Invalid explicit native API key.");
    }

    /// <summary>The upstream client endpoint for a catalog base URL: <c>{baseUrl}{suffix}</c> without a doubled slash.</summary>
    public static Uri CatalogEndpoint(string baseUrl, string suffix) => new(baseUrl.TrimEnd('/') + suffix);

    /// <summary>openai-completions for any catalog provider: <c>{baseUrl}/chat/completions</c>, compatibility from the model row.</summary>
    /// <param name="thinkingProfile">False for the metadata-free summary binding (the fixed factories' <c>modelMetadata: null</c>).</param>
    public static NativeHttpModelProvider CreateCatalogCompletions(ModelDescriptor model, string baseUrl, string explicitApiKey,
        CompletionsTranscriptProjectionOptions projectionOptions, CompletionsKeyAuthRequestOptions requestOptions, JsonData modelMetadata,
        HttpMessageHandler? handler = null, bool thinkingProfile = true)
    {
        var endpoint = CatalogEndpoint(baseUrl, "/chat/completions");
        ValidateCatalog(model, endpoint, explicitApiKey, "openai-completions", modelMetadata);
        return BindCompletions(model, endpoint, explicitApiKey, projectionOptions, requestOptions with { ModelMetadata = modelMetadata }, handler,
            thinkingProfile ? modelMetadata : null);
    }

    /// <summary>openai-responses for any catalog provider: <c>{baseUrl}/responses</c>.</summary>
    public static NativeHttpModelProvider CreateCatalogResponses(ModelDescriptor model, string baseUrl, string explicitApiKey,
        ResponsesTranscriptProjectionOptions projectionOptions, ResponsesKeyAuthRequestOptions requestOptions, JsonData modelMetadata,
        HttpMessageHandler? handler = null, bool thinkingProfile = true)
    {
        var endpoint = CatalogEndpoint(baseUrl, "/responses");
        ValidateCatalog(model, endpoint, explicitApiKey, "openai-responses", modelMetadata);
        var metadata = thinkingProfile ? modelMetadata : null;
        var effective = ResponsesCacheOptionsForModel(requestOptions, metadata);
        projectionOptions = ResponsesProjectionOptionsForModel(projectionOptions, metadata);
        var profile = metadata is null ? null : new NativeThinkingProfile(model, metadata, projectionOptions.Reasoning);
        effective = effective with { ThinkingLevelMap = profile?.Map ?? effective.ThinkingLevelMap };
        var responsesOptions = new ResponsesTextToolOptions(Rates: ResponsesRatesForModel(metadata))
        { SupportsOpenAIGrammarTools = projectionOptions.ToolDeclarations?.SupportsOpenAIGrammarTools == true };
        return Bind(model, handler, client => new NativeThinkingTransport(model, profile?.Levels ?? ["off"], level => new ResponsesHttpSseTransport(client,
            level is null ? new ResponsesKeyAuthRequestFactory(endpoint, model, projectionOptions, effective) :
            new ResponsesKeyAuthRequestFactory(endpoint, model, projectionOptions, effective with
            {
                ReasoningEffort = level == "off" ? null : level, ReasoningSummary = level == "off" ? null : effective.ReasoningSummary,
                ThinkingLevelMap = effective.ThinkingLevelMap
            }), explicitApiKey, responsesOptions: responsesOptions)));
    }

    /// <summary>anthropic-messages with an <c>x-api-key</c> for any catalog provider other than anthropic and github-copilot (which have
    /// their own auth): the SDK appends <c>/v1/messages</c> to the base URL.</summary>
    public static NativeHttpModelProvider CreateCatalogAnthropic(ModelDescriptor model, string baseUrl, string explicitApiKey,
        AnthropicMessagesRequestOptions projectionOptions, AnthropicMessagesKeyAuthRequestOptions requestOptions, JsonData modelMetadata,
        HttpMessageHandler? handler = null, bool thinkingProfile = true)
    {
        var endpoint = new Uri(baseUrl);
        ValidateCatalog(model, endpoint, explicitApiKey, "anthropic-messages", modelMetadata);
        if (model.Provider is "anthropic" or "github-copilot") throw new ArgumentException("Unsupported native model or endpoint selection.");
        var maximum = requestOptions.MaxTokens ?? projectionOptions.MaximumTokens;
        if (maximum != Math.Truncate(maximum) || maximum is <= 0 or > int.MaxValue) throw new ArgumentException("Unsupported native thinking token cap.");
        var metadata = thinkingProfile ? modelMetadata : null;
        var profile = metadata is null ? null : new NativeThinkingProfile(model, metadata, projectionOptions.ModelReasoning, (int)maximum);
        if (profile is not null) projectionOptions = projectionOptions with { SupportsMidConversationEffort = profile.MidConversationEffort };
        if (profile is null && projectionOptions.ModelReasoning && !projectionOptions.SupportsThinkingOff)
            throw new ArgumentException("Native thinking off is unsupported by the configured profile.");
        var factory = new AnthropicMessagesKeyAuthRequestFactory(endpoint, model, projectionOptions, requestOptions);
        var messagesOptions = AnthropicMessagesOptionsForModel(metadata);
        return Bind(model, handler, client =>
        {
            IChatTransport Create(AnthropicMessagesKeyAuthRequestFactory bound, AnthropicMessagesRequestOptions projection) => new AnthropicMessagesHttpSseTransport(client,
                (request, token) => bound.Create(request, explicitApiKey, token), messagesOptions: WithThinkingLevel(messagesOptions, projection));
            return new NativeThinkingTransport(model, profile?.Levels ?? ["off"], level =>
            {
                if (level is null) return Create(factory, projectionOptions);
                var selected = AnthropicLevel(projectionOptions, profile, level, maximum);
                return Create(new(endpoint, model, selected, requestOptions with { MaxTokens = maximum }), selected);
            });
        });
    }

    /// <summary>mistral-conversations at a catalog base URL (the fixed factory admits only the Mistral endpoint).</summary>
    public static NativeHttpModelProvider CreateCatalogMistral(ModelDescriptor model, string baseUrl, string explicitApiKey, JsonData modelMetadata,
        MistralTextOptions options, bool simple, HttpMessageHandler? handler = null)
    {
        var endpoint = new Uri(baseUrl);
        ValidateCatalog(model, endpoint, explicitApiKey, "mistral-conversations", modelMetadata);
        ArgumentNullException.ThrowIfNull(options);
        if (options.BaseUrl != endpoint) throw new ArgumentException("Mistral options must retain the selected endpoint.");
        options.Validate();
        return Bind(model, handler, client => simple
            ? new MistralSimpleHttpSseTransport(client, model, modelMetadata, options with { ApiKey = explicitApiKey })
            : new MistralTextHttpSseTransport(client, model, options with { ApiKey = explicitApiKey }));
    }

    /// <summary>google-generative-ai streamSimple for a catalog model; the thinking level selects the Simple reasoning option.</summary>
    public static NativeHttpModelProvider CreateCatalogGoogle(ModelDescriptor model, string explicitApiKey, JsonData modelMetadata,
        GoogleGenerativeAIOptions directOptions, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(directOptions);
        var baseUrl = modelMetadata.Value.TryGetProperty("baseUrl", out var value) ? value.GetString() ?? "" : "";
        ValidateCatalog(model, new Uri(baseUrl), explicitApiKey, "google-generative-ai", modelMetadata);
        var direct = directOptions with { ApiKey = explicitApiKey };
        var levels = CatalogThinkingLevels(modelMetadata);
        return Bind(model, handler, client => new NativeThinkingTransport(model, levels, level =>
            new GoogleSimpleRequestFactory(client, model, new GoogleSimpleOptions(direct, level is null or "off" ? null : level))));
    }

    /// <summary>pi-messages streamSimple for a catalog model (Radius and models.json pi-messages providers).</summary>
    public static NativeHttpModelProvider CreateCatalogPiMessages(ModelDescriptor model, string explicitApiKey, JsonData modelMetadata,
        PiMessagesOptions options, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var baseUrl = modelMetadata.Value.TryGetProperty("baseUrl", out var value) ? value.GetString() ?? "" : "";
        ValidateCatalog(model, new Uri(baseUrl), explicitApiKey, "pi-messages", modelMetadata);
        var bound = options with { ApiKey = explicitApiKey, EnvironmentLookup = options.EnvironmentLookup ?? (_ => null) };
        var levels = CatalogThinkingLevels(modelMetadata);
        return Bind(model, handler, client => new NativeThinkingTransport(model, levels, level =>
            new PiMessagesHttpSseTransport(client, model, bound with { Reasoning = level is null or "off" ? null : level })));
    }

    /// <summary>Source getSupportedThinkingLevels from a catalog row.</summary>
    public static ImmutableArray<string> CatalogThinkingLevels(JsonData modelMetadata)
    {
        var raw = modelMetadata.Value;
        if (!raw.TryGetProperty("reasoning", out var reasoning) || reasoning.ValueKind != System.Text.Json.JsonValueKind.True) return ["off"];
        var hasMap = raw.TryGetProperty("thinkingLevelMap", out var map) && map.ValueKind == System.Text.Json.JsonValueKind.Object;
        return [.. ThinkingLevels.Ordered.Where(level =>
        {
            var present = hasMap && map.TryGetProperty(level, out _);
            if (present && map.GetProperty(level).ValueKind == System.Text.Json.JsonValueKind.Null) return false;
            return level is not ("xhigh" or "max") || present;
        })];
    }
}
