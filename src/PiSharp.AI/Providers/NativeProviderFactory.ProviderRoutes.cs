// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/providers/github-copilot.ts, api/github-copilot-headers.ts,
// auth/oauth/github-copilot.ts (toAuth: per-credential baseUrl), api/anthropic-messages.ts (createClient: Copilot Bearer auth),
// api/openai-completions.ts and api/openai-responses.ts (Copilot dynamic headers), providers/cloudflare-workers-ai.ts,
// providers/cloudflare-ai-gateway.ts, providers/cloudflare-stream.ts (resolveCloudflareModel), providers/cloudflare-auth.ts and
// providers/opencode-headers.ts. Routes over the existing native Anthropic, Completions and Responses protocols whose auth, base
// URL or headers are resolved for every request.
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.AI.Protocols.ProviderShared;
using PiSharp.Contracts;

namespace PiSharp.AI.Providers;

/// <summary>Resolved request auth (types.ts ModelAuth): an apiKey, a per-credential base URL and headers (a null value removes the
/// header, as cf-aig-authorization auth removes Authorization and x-api-key).</summary>
public sealed record ProviderRequestAuth(string? ApiKey, string? BaseUrl = null, ImmutableDictionary<string, string?>? Headers = null)
{
    public override string ToString() => "ProviderRequestAuth [redacted]";
}

/// <summary>A provider route over an existing API whose auth is resolved for every request.</summary>
public sealed record ProviderRouteOptions(Func<CancellationToken, ValueTask<ProviderRequestAuth>> Auth)
{
    /// <summary>The output cap (maxTokens).</summary>
    public int MaxTokens { get; init; } = 1024;
    /// <summary>A summary route: no prompt caching and thinking off.</summary>
    public bool Summary { get; init; }
    public string? SessionId { get; init; }
    /// <summary>Caller request headers (options.headers), applied after model and provider headers; null removes.</summary>
    public ImmutableDictionary<string, string?>? Headers { get; init; }
    /// <summary>Provider env values for base URL placeholders such as {CLOUDFLARE_ACCOUNT_ID}.</summary>
    public ImmutableDictionary<string, string>? Environment { get; init; }
    public int MaximumMessages { get; init; } = 1024;
    public int MaximumEntryCharacters { get; init; } = 64 * 1_048_576;
    public int MaximumPayloadBytes { get; init; } = 64 * 1_048_576;
}

/// <summary>Provider-specific request header policies.</summary>
public static class ProviderHeaderPolicies
{
    /// <summary>inferCopilotInitiator: "agent" when the last message is not a user message.</summary>
    public static string CopilotInitiator(ImmutableArray<TranscriptEntry> messages) =>
        messages.IsDefaultOrEmpty || messages[^1].Role == "user" ? "user" : "agent";

    /// <summary>hasCopilotVisionInput: an image in a user or tool-result content array.</summary>
    public static bool CopilotVisionInput(ImmutableArray<TranscriptEntry> messages) => !messages.IsDefault && messages.Any(message =>
        message.Role is "user" or "toolResult" && message.WireBody.Value.TryGetProperty("content", out var content) &&
        content.ValueKind == JsonValueKind.Array && content.EnumerateArray().Any(block => block.ValueKind == JsonValueKind.Object &&
            block.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "image"));

    /// <summary>buildCopilotDynamicHeaders: X-Initiator, Openai-Intent and, with images, Copilot-Vision-Request.</summary>
    public static ImmutableDictionary<string, string?> CopilotDynamicHeaders(ImmutableArray<TranscriptEntry> messages)
    {
        var headers = ImmutableDictionary.CreateBuilder<string, string?>(StringComparer.Ordinal);
        headers["X-Initiator"] = CopilotInitiator(messages); headers["Openai-Intent"] = "conversation-edits";
        if (CopilotVisionInput(messages)) headers["Copilot-Vision-Request"] = "true";
        return headers.ToImmutable();
    }

    /// <summary>getGitHubCopilotBaseUrl: the token's proxy-ep with "proxy." replaced by "api.", else copilot-api.{enterprise}, else
    /// the individual endpoint.</summary>
    public static string CopilotBaseUrl(string? token, string? enterpriseDomain)
    {
        if (token is not null && System.Text.RegularExpressions.Regex.Match(token, "proxy-ep=([^;]+)") is { Success: true } match)
        {
            var host = match.Groups[1].Value;
            return "https://" + (host.StartsWith("proxy.", StringComparison.Ordinal) ? "api." + host[6..] : host);
        }
        return enterpriseDomain is { Length: > 0 } ? "https://copilot-api." + enterpriseDomain : "https://api.individual.githubcopilot.com";
    }

    /// <summary>resolveCloudflareModel: {CLOUDFLARE_ACCOUNT_ID} and {CLOUDFLARE_GATEWAY_ID} placeholders from the provider env.</summary>
    public static string ResolveCloudflareBaseUrl(string baseUrl, IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is null) return baseUrl;
        foreach (var name in new[] { "CLOUDFLARE_ACCOUNT_ID", "CLOUDFLARE_GATEWAY_ID" })
            if (environment.TryGetValue(name, out var value)) baseUrl = baseUrl.Replace("{" + name + "}", value, StringComparison.Ordinal);
        return baseUrl;
    }

    /// <summary>cloudflareAIGatewayAuth: the token in cf-aig-authorization, with Authorization and x-api-key removed.</summary>
    public static ImmutableDictionary<string, string?> CloudflareGatewayHeaders(string apiKey) => ImmutableDictionary.CreateRange(StringComparer.Ordinal,
        new KeyValuePair<string, string?>[] { new("cf-aig-authorization", "Bearer " + apiKey), new("Authorization", null), new("x-api-key", null) });

    /// <summary>withOpenCodeSessionHeader: x-opencode-session carries the session id unless the caller already sets it.</summary>
    public static ImmutableDictionary<string, string?>? WithOpenCodeSessionHeader(string? sessionId, ImmutableDictionary<string, string?>? headers)
    {
        if (string.IsNullOrEmpty(sessionId) || headers?.Keys.Any(key => key.Equals("x-opencode-session", StringComparison.OrdinalIgnoreCase)) == true) return headers;
        return (headers ?? ImmutableDictionary.Create<string, string?>(StringComparer.Ordinal)).SetItem("x-opencode-session", sessionId);
    }
}

public static partial class NativeProviderFactory
{
    /// <summary>
    /// A provider route (github-copilot, cloudflare-workers-ai, cloudflare-ai-gateway, opencode, opencode-go or another provider whose
    /// auth resolves per request) over anthropic-messages, openai-completions or openai-responses. Each request resolves its auth,
    /// derives the base URL (auth.BaseUrl, else the model's, with Cloudflare placeholders resolved), adds the provider's dynamic headers
    /// and binds the existing protocol for that request. Auth headers with a null value remove the protocol's own auth header.
    /// </summary>
    public static NativeHttpModelProvider CreateProviderRoute(ModelDescriptor model, JsonData modelMetadata, ProviderRouteOptions options,
        HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(modelMetadata); ArgumentNullException.ThrowIfNull(options);
        if (model.Api is not ("anthropic-messages" or "openai-completions" or "openai-responses") || string.IsNullOrWhiteSpace(model.Id) ||
            string.IsNullOrWhiteSpace(model.Provider) || model.Id.Length > 1024 || model.Id.Any(char.IsControl))
            throw new ArgumentException("Unsupported native model or endpoint selection.");
        var raw = modelMetadata.Value;
        if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty("id", out var id) || id.GetString() != model.Id)
            throw new ArgumentException("Unsupported native model or endpoint selection.");
        // Thinking levels from the bound protocol, measured once with an inert prototype binding.
        using var prototype = BindRouteRequest(model, modelMetadata, options, new("prototype-key", "https://prototype.invalid/"),
            ImmutableDictionary<string, string?>.Empty, null);
        var levels = prototype.GetSupportedThinkingLevels(model);
        // Each request binds through the route's own handler (an HttpClient refuses to resend a request another client sent).
        var root = handler ?? new HttpClientHandler { AllowAutoRedirect = false };
        var invoker = new HttpMessageInvoker(root, disposeHandler: false);
        var client = new HttpClient(root, disposeHandler: handler is null) { Timeout = Timeout.InfiniteTimeSpan };
        return new(model, client, new RouteTransport(model, levels, async (request, token) =>
        {
            var auth = await options.Auth(token).ConfigureAwait(false);
            var dynamic = model.Provider == "github-copilot" ? ProviderHeaderPolicies.CopilotDynamicHeaders(request.Messages) : ImmutableDictionary<string, string?>.Empty;
            return BindRouteRequest(model, modelMetadata, options, auth, dynamic, invoker);
        }));
    }

    private static NativeHttpModelProvider BindRouteRequest(ModelDescriptor model, JsonData metadata, ProviderRouteOptions options,
        ProviderRequestAuth auth, ImmutableDictionary<string, string?> dynamicHeaders, HttpMessageInvoker? client)
    {
        var raw = metadata.Value;
        string Text(string name) => raw.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        var baseUrl = ProviderHeaderPolicies.ResolveCloudflareBaseUrl(auth.BaseUrl ?? Text("baseUrl"), options.Environment);
        if (baseUrl.Contains('{')) throw new ArgumentException("Unresolved provider base URL placeholder.");
        // The bound model is the resolved model (resolveCloudflareModel, Copilot's per-credential baseUrl): its metadata names that URL.
        if (Text("baseUrl") != baseUrl && JsonNode.Parse(metadata.ToString()) is JsonObject resolvedRow)
        {
            resolvedRow["baseUrl"] = baseUrl;
            metadata = JsonData.Parse(resolvedRow.ToJsonString());
            raw = metadata.Value;
        }
        var reasoning = raw.TryGetProperty("reasoning", out var flag) && flag.ValueKind == JsonValueKind.True;
        var images = raw.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Array &&
            input.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == "image");
        var modelHeaders = raw.TryGetProperty("headers", out var headersValue) && headersValue.ValueKind == JsonValueKind.Object ? JsonData.FromElement(headersValue) : null;
        // Request headers in upstream order: provider dynamic headers, then auth headers, then caller headers. Auth headers may remove
        // the protocol's own key header, which the protocol factories reserve, so they are applied to each outgoing request.
        var routeHeaders = ImmutableDictionary.CreateBuilder<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in dynamicHeaders) routeHeaders[name] = value;
        foreach (var (name, value) in auth.Headers ?? ImmutableDictionary<string, string?>.Empty) routeHeaders[name] = value;
        // Header-owned Authorization auth (Kimi Code OAuth) carries no apiKey: the SDK sends no x-api-key.
        if (string.IsNullOrEmpty(auth.ApiKey) && routeHeaders.TryGetValue("Authorization", out var bearer) && bearer is not null && !routeHeaders.ContainsKey("x-api-key"))
            routeHeaders["x-api-key"] = null;
        var callerHeaders = model.Provider is "opencode" or "opencode-go"
            ? ProviderHeaderPolicies.WithOpenCodeSessionHeader(options.SessionId, options.Headers) : options.Headers;
        foreach (var (name, value) in callerHeaders ?? ImmutableDictionary<string, string?>.Empty) routeHeaders[name] = value;
        var rewrites = routeHeaders.ToImmutable();
        // A header-owned auth (Cloudflare AI Gateway) carries no apiKey; the protocols still require one, which the rewrite removes.
        var key = auth.ApiKey is { Length: > 0 } apiKey ? apiKey : rewrites.Values.Any(value => value is not null) ? "header-owned-auth" : null;
        if (key is null) throw new InvalidOperationException($"No API key for provider: {model.Provider}");
        var handler = client is null ? null : new RouteHandler(client, rewrites);
        var maximum = options.MaxTokens; var summary = options.Summary;
        var sessionId = options.Summary ? null : options.SessionId;
        // Pi has no request-size cap: projection budgets follow the configured payload limit (images of ~4.5 MB reach the provider).
        var budget = options.MaximumPayloadBytes;
        switch (model.Api)
        {
            case "openai-completions":
            {
                var endpoint = new Uri(baseUrl.TrimEnd('/') + "/chat/completions");
                var projection = new CompletionsTranscriptProjectionOptions(Reasoning: reasoning, MaximumMessages: options.MaximumMessages,
                    MaximumEntryCharacters: options.MaximumEntryCharacters, MaximumInputCharacters: budget, MaximumOutputCharacters: budget, MaximumOutputBytes: budget,
                    ToolDeclarations: new(MaximumMessages: options.MaximumMessages, MaximumEntryCharacters: options.MaximumEntryCharacters,
                        MaximumInputCharacters: budget, MaximumOutputCharacters: budget, MaximumOutputBytes: budget)) { ModelSupportsImages = images };
                var request = new CompletionsKeyAuthRequestOptions(MaxTokens: maximum, CacheRetention: summary ? CompletionsCacheRetention.None : CompletionsCacheRetention.Short,
                    MaximumPayloadBytes: options.MaximumPayloadBytes, SessionId: sessionId) { ModelMetadata = metadata };
                return BindCompletions(model, endpoint, key, projection, request with { ModelHeaders = modelHeaders }, handler, summary ? null : metadata);
            }
            case "openai-responses":
            {
                var endpoint = new Uri(baseUrl.TrimEnd('/') + "/responses");
                return BindRouteResponses(model, endpoint, key, new(Reasoning: reasoning, MaximumMessages: options.MaximumMessages, MaximumEntryCharacters: options.MaximumEntryCharacters,
                    MaximumInputCharacters: budget, MaximumOutputCharacters: budget,
                    ToolDeclarations: new(MaximumMessages: options.MaximumMessages, MaximumEntryCharacters: options.MaximumEntryCharacters,
                        MaximumInputCharacters: budget, MaximumOutputCharacters: budget, MaximumOutputBytes: budget)) { ModelSupportsImages = images },
                    new(SupportsMaxOutputTokens: true, MaxOutputTokens: maximum, MaximumPayloadBytes: options.MaximumPayloadBytes, SessionId: sessionId) { ModelHeaders = modelHeaders },
                    handler, summary ? null : metadata);
            }
            default:
                return BindRouteAnthropic(model, new Uri(baseUrl), key, RouteAnthropicProjection(raw, new(MaximumTokens: maximum, ModelReasoning: reasoning,
                    ModelSupportsImages: images, ThinkingEnabled: false, MaximumMessages: options.MaximumMessages, MaximumEntryCharacters: options.MaximumEntryCharacters,
                    MaximumInputCharacters: budget, MaximumOutputCharacters: budget, MaximumOutputBytes: budget,
                    CacheRetention: summary ? AnthropicCacheRetention.None : AnthropicCacheRetention.Short), summary),
                    new(MaxTokens: maximum, MaximumPayloadBytes: options.MaximumPayloadBytes, ModelHeaders: modelHeaders, SessionId: sessionId)
                    { BearerAuthorization = model.Provider == "github-copilot" },
                    handler, summary ? null : metadata);
        }
    }

    /// <summary>The live route's Anthropic thinking compatibility (compat.supportsMidConvoEffort and thinkingLevelMap.off).</summary>
    private static AnthropicMessagesRequestOptions RouteAnthropicProjection(JsonElement value, AnthropicMessagesRequestOptions projection, bool summary)
    {
        var supportsOff = !(value.TryGetProperty("thinkingLevelMap", out var map) && map.ValueKind == JsonValueKind.Object &&
            map.TryGetProperty("off", out var off) && off.ValueKind == JsonValueKind.Null);
        return projection with
        {
            SupportsMidConversationEffort = value.TryGetProperty("compat", out var compat) && compat.ValueKind == JsonValueKind.Object &&
                compat.TryGetProperty("supportsMidConvoEffort", out var mid) && mid.ValueKind == JsonValueKind.True,
            SupportsThinkingOff = supportsOff, ModelReasoning = projection.ModelReasoning && (supportsOff || !summary)
        };
    }

    /// <summary>CreateResponses without the OpenAI identity/endpoint restriction.</summary>
    private static NativeHttpModelProvider BindRouteResponses(ModelDescriptor model, Uri endpoint, string key,
        ResponsesTranscriptProjectionOptions projectionOptions, ResponsesKeyAuthRequestOptions requestOptions, HttpMessageHandler? handler, JsonData? modelMetadata)
    {
        var effectiveRequestOptions = ResponsesCacheOptionsForModel(requestOptions, modelMetadata);
        projectionOptions = ResponsesProjectionOptionsForModel(projectionOptions, modelMetadata);
        var profile = modelMetadata is null ? null : new NativeThinkingProfile(model, modelMetadata, projectionOptions.Reasoning);
        effectiveRequestOptions = effectiveRequestOptions with { ThinkingLevelMap = modelMetadata is null ? effectiveRequestOptions.ThinkingLevelMap : profile?.Map };
        var factory = new ResponsesKeyAuthRequestFactory(endpoint, model, projectionOptions, effectiveRequestOptions);
        var responsesOptions = new ResponsesTextToolOptions(Rates: ResponsesRatesForModel(modelMetadata))
        { SupportsOpenAIGrammarTools = projectionOptions.ToolDeclarations?.SupportsOpenAIGrammarTools == true };
        return Bind(model, handler, client => new NativeThinkingTransport(model, profile?.Levels ?? ["off"], level => level is null
                ? new ResponsesHttpSseTransport(client, factory, key, responsesOptions: responsesOptions)
                : new ResponsesHttpSseTransport(client, new ResponsesKeyAuthRequestFactory(endpoint, model, projectionOptions,
                    effectiveRequestOptions with { ReasoningEffort = level == "off" ? null : level,
                        ReasoningSummary = level == "off" ? null : effectiveRequestOptions.ReasoningSummary,
                        ThinkingLevelMap = effectiveRequestOptions.ThinkingLevelMap }), key, responsesOptions: responsesOptions)));
    }

    /// <summary>CreateAnthropic without the Anthropic identity/endpoint restriction.</summary>
    private static NativeHttpModelProvider BindRouteAnthropic(ModelDescriptor model, Uri endpoint, string key,
        AnthropicMessagesRequestOptions projectionOptions, AnthropicMessagesKeyAuthRequestOptions requestOptions, HttpMessageHandler? handler, JsonData? modelMetadata)
    {
        var factory = new AnthropicMessagesKeyAuthRequestFactory(endpoint, model, projectionOptions, requestOptions);
        var maximum = requestOptions.MaxTokens ?? projectionOptions.MaximumTokens;
        var profile = modelMetadata is null ? null : new NativeThinkingProfile(model, modelMetadata, projectionOptions.ModelReasoning, (int)maximum);
        if (profile is not null && profile.MidConversationEffort != projectionOptions.SupportsMidConversationEffort)
            factory = new(endpoint, model, projectionOptions = projectionOptions with { SupportsMidConversationEffort = profile.MidConversationEffort }, requestOptions);
        var messagesOptions = AnthropicMessagesOptionsForModel(modelMetadata);
        return Bind(model, handler, client =>
        {
            IChatTransport Create(AnthropicMessagesKeyAuthRequestFactory bound, AnthropicMessagesRequestOptions projection) => new AnthropicMessagesHttpSseTransport(client,
                (request, token) => bound.Create(request, key, token), messagesOptions: WithThinkingLevel(messagesOptions, projection));
            if (profile is null && projectionOptions.ModelReasoning && !projectionOptions.SupportsThinkingOff)
                throw new ArgumentException("Native thinking off is unsupported by the configured profile.");
            return new NativeThinkingTransport(model, profile?.Levels ?? ["off"], level =>
            {
                if (level is null) return Create(factory, projectionOptions);
                var selected = AnthropicLevel(projectionOptions, profile, level, maximum);
                return Create(new(endpoint, model, selected, requestOptions with { MaxTokens = maximum }), selected);
            });
        });
    }

    /// <summary>Sends through the route's shared client and applies the request's provider headers (null removes).</summary>
    private sealed class RouteHandler(HttpMessageInvoker client, ImmutableDictionary<string, string?> headers) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            foreach (var (name, value) in headers)
            {
                request.Headers.Remove(name);
                if (value is not null && !request.Headers.TryAddWithoutValidation(name, value))
                    request.Content?.Headers.TryAddWithoutValidation(name, value);
            }
            return client.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>Binds the protocol for every request and releases it once that request's stream settles.</summary>
    private sealed class RouteTransport(ModelDescriptor model, ImmutableArray<string> levels,
        Func<ChatRequest, CancellationToken, ValueTask<NativeHttpModelProvider>> create) : IChatTransport, IThinkingLevelTransport
    {
        public ImmutableArray<string> GetSupportedThinkingLevels(ModelDescriptor selected) =>
            selected == model ? levels : throw new ArgumentException("Unknown native route model.");

        public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.Model != model) throw new ArgumentException("Unknown native route model.");
            return Stream(request, cancellationToken);
        }

        private async IAsyncEnumerable<StreamEvent> Stream(ChatRequest request, [EnumeratorCancellation] CancellationToken token)
        {
            NativeHttpModelProvider? provider = null; string? failure = null;
            try { provider = await create(request, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { failure = "Request was aborted"; }
            catch (Exception error) when (error is not OperationCanceledException)
            { failure = error.Message; }
            if (provider is null)
            {
                var reason = token.IsCancellationRequested ? StopReason.Aborted : StopReason.Error;
                yield return new StreamError(reason, new AssistantMessage(model.Api, model.Provider, model.Id, request.Timestamp, [], TokenUsage.Zero, reason,
                    JsonFields.Empty.Set("errorMessage", JsonData.Parse(JsonSerializer.Serialize(failure ?? "Request failed")))));
                yield break;
            }
            using (provider)
                await foreach (var frame in provider.StreamAsync(request, token).ConfigureAwait(false)) yield return frame;
        }
    }
}
