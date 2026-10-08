using PiSharp.AI;
using PiSharp.AI.Authentication;
using PiSharp.AI.Providers;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.AnthropicMessages;

/// <summary>Explicit resolved-auth protocol binding; no credential lookup or shared registry mutation.</summary>
public static class AnthropicResolvedProviderFactory
{
    public static NativeHttpModelProvider Create(ModelDescriptor model, Uri endpoint,
        AnthropicInjectedAuthenticationBinding authentication, AnthropicMessagesRequestOptions projectionOptions,
        AnthropicMessagesKeyAuthRequestOptions? requestOptions = null, HttpMessageHandler? handler = null,
        JsonData? modelMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(authentication);
        // Existing fixed endpoint/model/key admission remains authoritative and unchanged.
        Validate(model, endpoint, authentication.Authentication.Secret, "anthropic", "anthropic-messages", "https://api.anthropic.com/");
        var factory = new AnthropicMessagesAuthenticatedRequestFactory(endpoint, model, projectionOptions, authentication, requestOptions);
        var maximum = requestOptions?.MaxTokens ?? projectionOptions.MaximumTokens;
        if (modelMetadata is not null && (maximum != Math.Truncate(maximum) || maximum is <= 0 or > int.MaxValue))
            throw new ArgumentException("Unsupported native thinking token cap.");
        var profile = modelMetadata is null ? null : new NativeThinkingProfile(model, modelMetadata, projectionOptions.ModelReasoning, (int)maximum);
        var responseOptions = new AnthropicMessagesOptions(OAuthToolNames: authentication.UseOAuthProjection,
            MaximumToolDeclarations: projectionOptions.MaximumDeclarations, MaximumActiveTools: projectionOptions.MaximumActiveTools,
            MaximumInputCharacters: projectionOptions.MaximumInputCharacters);
        return Bind(model, handler, client =>
        {
            IChatTransport Create(AnthropicMessagesAuthenticatedRequestFactory bound) => new AnthropicMessagesHttpSseTransport(client,
                (request, token) => bound.Create(request, token), messagesOptions: responseOptions);
            if (profile is null && projectionOptions.ModelReasoning && !projectionOptions.SupportsThinkingOff)
                throw new ArgumentException("Native thinking off is unsupported by the configured profile.");
            return new NativeThinkingTransport(model, profile?.Levels ?? ["off"], level => level is null
                ? Create(factory) : Create(new(endpoint, model, projectionOptions with
                { MaximumTokens = profile is null ? projectionOptions.MaximumTokens : (int)maximum, ThinkingEnabled = level != "off", ForceAdaptiveThinking = profile?.Adaptive ?? false,
                    SupportsThinkingOff = profile?.SupportsOff ?? projectionOptions.SupportsThinkingOff,
                    Effort = level != "off" && profile?.Adaptive == true ? profile.AnthropicEffort(level) : null,
                    ThinkingBudgetTokens = level == "off" || profile?.Adaptive == true ? 0 : NativeThinkingProfile.AnthropicBudget(level, (int)maximum) },
                    authentication, (requestOptions ?? new()) with { MaxTokens = maximum })));
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
