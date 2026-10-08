using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Contracts;

namespace PiSharp.AI.Providers;

/// <summary>Explicit key-auth composition over existing native protocols. Never discovers credentials or models.</summary>
public static partial class NativeProviderFactory
{
    public static NativeHttpModelProvider CreateResponses(ModelDescriptor model, Uri endpoint, string explicitApiKey,
        ResponsesTranscriptProjectionOptions projectionOptions, ResponsesKeyAuthRequestOptions? requestOptions = null,
        HttpMessageHandler? handler = null, JsonData? modelMetadata = null)
    {
        Validate(model, endpoint, explicitApiKey, "openai", "openai-responses", "https://api.openai.com/v1/responses");
        var effectiveRequestOptions = ResponsesCacheOptionsForModel(requestOptions, modelMetadata);
        projectionOptions = ResponsesProjectionOptionsForModel(projectionOptions, modelMetadata);
        var profile = modelMetadata is null ? null : new NativeThinkingProfile(model, modelMetadata, projectionOptions.Reasoning);
        effectiveRequestOptions = effectiveRequestOptions with { ThinkingLevelMap = modelMetadata is null ? effectiveRequestOptions.ThinkingLevelMap : profile?.Map };
        var factory = new ResponsesKeyAuthRequestFactory(endpoint, model, projectionOptions, effectiveRequestOptions);
        var responsesOptions = new ResponsesTextToolOptions(Rates: ResponsesRatesForModel(modelMetadata))
        { SupportsOpenAIGrammarTools = projectionOptions.ToolDeclarations?.SupportsOpenAIGrammarTools == true };
        return Bind(model, handler, client => new NativeThinkingTransport(model, profile?.Levels ?? ["off"], level => level is null
                ? new ResponsesHttpSseTransport(client, factory, explicitApiKey, responsesOptions: responsesOptions)
                : new ResponsesHttpSseTransport(client, new ResponsesKeyAuthRequestFactory(endpoint, model, projectionOptions,
                    effectiveRequestOptions with { ReasoningEffort = level == "off" ? null : level,
                        ReasoningSummary = level == "off" ? null : effectiveRequestOptions.ReasoningSummary,
                        ThinkingLevelMap = effectiveRequestOptions.ThinkingLevelMap }), explicitApiKey, responsesOptions: responsesOptions)));
    }

    /// <summary>OpenAI or OpenRouter Completions, selected by the exact provider identity and endpoint.</summary>
    public static NativeHttpModelProvider CreateCompletions(ModelDescriptor model, Uri endpoint, string explicitApiKey,
        CompletionsTranscriptProjectionOptions? projectionOptions = null, CompletionsKeyAuthRequestOptions? requestOptions = null,
        HttpMessageHandler? handler = null, JsonData? modelMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        var openRouter = model.Provider == "openrouter";
        Validate(model, endpoint, explicitApiKey, openRouter ? "openrouter" : "openai", "openai-completions",
            openRouter ? "https://openrouter.ai/api/v1/chat/completions" : "https://api.openai.com/v1/chat/completions");
        projectionOptions ??= new(SupportsDeveloperRole: !openRouter);
        requestOptions ??= openRouter
            ? new(MaxTokensField: "max_tokens", SupportsStore: false, SupportsLongCacheRetention: false)
            : new();
        return BindCompletions(model, endpoint, explicitApiKey, projectionOptions, requestOptions, handler, modelMetadata);
    }

    private static NativeHttpModelProvider BindCompletions(ModelDescriptor model, Uri endpoint, string explicitApiKey,
        CompletionsTranscriptProjectionOptions projectionOptions, CompletionsKeyAuthRequestOptions requestOptions,
        HttpMessageHandler? handler, JsonData? modelMetadata)
    {
        var factory = new CompletionsKeyAuthRequestFactory(endpoint, model, projectionOptions, requestOptions);
        var profile = modelMetadata is null ? null : new NativeThinkingProfile(model, modelMetadata, projectionOptions.Reasoning);
        if (profile is not null) profile.ConstrainCompletions(new(endpoint, model, projectionOptions,
            requestOptions with { ModelMetadata = modelMetadata, ThinkingLevelMap = profile.Map }));
        return Bind(model, handler, client =>
        {
            IChatTransport Create(CompletionsKeyAuthRequestFactory bound) => new CompletionsHttpSseTransport(client,
                (request, token) => bound.Create(request, explicitApiKey, token), completionsOptions: bound.ResolvedWireOptions);
            IChatTransport Selected(string? level)
            {
                if (level is null) return Create(factory);
                var selected = new CompletionsKeyAuthRequestFactory(endpoint, model, projectionOptions, requestOptions with
                { ReasoningEffort = level == "off" ? null : level, ThinkingLevelMap = profile?.Map,
                    ModelMetadata = modelMetadata ?? requestOptions.ModelMetadata });
                // Embedded model metadata is still authoritative even without capability opt-in.
                if (profile is null) selected.AdmitNativeThinkingOff();
                return Create(selected);
            }
            return new NativeThinkingTransport(model, profile?.Levels ?? ["off"], Selected);
        });
    }

    /// <summary>The Anthropic endpoint is the base URI; the existing factory appends /v1/messages?beta=true.</summary>
    public static NativeHttpModelProvider CreateAnthropic(ModelDescriptor model, Uri endpoint, string explicitApiKey,
        AnthropicMessagesRequestOptions projectionOptions, AnthropicMessagesKeyAuthRequestOptions? requestOptions = null,
        HttpMessageHandler? handler = null, JsonData? modelMetadata = null, AnthropicMessagesHooks? hooks = null)
    {
        Validate(model, endpoint, explicitApiKey, "anthropic", "anthropic-messages", "https://api.anthropic.com/");
        var factory = new AnthropicMessagesKeyAuthRequestFactory(endpoint, model, projectionOptions, requestOptions);
        var maximum = requestOptions?.MaxTokens ?? projectionOptions.MaximumTokens;
        if (modelMetadata is not null && (maximum != Math.Truncate(maximum) || maximum is <= 0 or > int.MaxValue))
            throw new ArgumentException("Unsupported native thinking token cap.");
        var profile = modelMetadata is null ? null : new NativeThinkingProfile(model, modelMetadata, projectionOptions.ModelReasoning, (int)maximum);
        var messagesOptions = AnthropicMessagesOptionsForModel(modelMetadata);
        return Bind(model, handler, client =>
        {
            IChatTransport Create(AnthropicMessagesKeyAuthRequestFactory bound) => new AnthropicMessagesHttpSseTransport(client,
                (request, token) => bound.Create(request, explicitApiKey, token), messagesOptions: messagesOptions, hooks: hooks);
            if (profile is null && projectionOptions.ModelReasoning && !projectionOptions.SupportsThinkingOff)
                throw new ArgumentException("Native thinking off is unsupported by the configured profile.");
            return new NativeThinkingTransport(model, profile?.Levels ?? ["off"], level => level is null
                ? Create(factory) : Create(new(endpoint, model, projectionOptions with
                { MaximumTokens = profile is null ? projectionOptions.MaximumTokens : (int)maximum, ThinkingEnabled = level != "off", ForceAdaptiveThinking = profile?.Adaptive ?? false,
                    SupportsThinkingOff = profile?.SupportsOff ?? projectionOptions.SupportsThinkingOff,
                    Effort = level != "off" && profile?.Adaptive == true ? profile.AnthropicEffort(level) : null,
                    ThinkingBudgetTokens = level == "off" || profile?.Adaptive == true ? 0 : NativeThinkingProfile.AnthropicBudget(level, (int)maximum) },
                    (requestOptions ?? new()) with { MaxTokens = maximum })));
        });
    }

    private static void Validate(ModelDescriptor model, Uri endpoint, string key, string provider, string api, string address)
    {
        ArgumentNullException.ThrowIfNull(model);
        // Fixed diagnostics intentionally exclude keys, supplied identities, endpoints and request content.
        if (model.Provider != provider || model.Api != api || string.IsNullOrWhiteSpace(model.Id) ||
            model.Id.Length > 1024 || model.Id.Any(char.IsControl) || endpoint is null ||
            !endpoint.IsAbsoluteUri || endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0 ||
            endpoint.Query.Length != 0 || endpoint != new Uri(address))
            throw new ArgumentException("Unsupported native model or endpoint selection.");
        if (string.IsNullOrEmpty(key) || key.Length > 4096 || key.Any(value => value is < '!' or > '~'))
            throw new ArgumentException("Invalid explicit native API key.");
    }

    private static NativeHttpModelProvider Bind(ModelDescriptor model, HttpMessageHandler? handler,
        Func<HttpClient, IChatTransport> create)
    {
        // Caller-injected handlers are trusted offline seams and remain caller-owned.
        var client = handler is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            : new HttpClient(handler, disposeHandler: false);
        // Cancellation controls SSE bodies; a fixed client timeout must not truncate a long answer.
        client.Timeout = Timeout.InfiniteTimeSpan;
        try { return new(model, client, create(client)); }
        catch { client.Dispose(); throw; }
    }
}
