// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/anthropic-messages.ts.
// Pi delegates the exchange to @anthropic-ai/sdk 0.129.0 (MIT); its behaviour is ported from
// src/lib/credentials/{oidc-federation,token-cache,identity-token,types}.ts and client.ts token auth.
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PiSharp.AI.Authentication;

/// <summary>The SDK's oidc_federation config. Identifiers and a file path only; no secret material.</summary>
public sealed record AnthropicFederationConfiguration(string OrganizationId, string? WorkspaceId, string FederationRuleId,
    string? ServiceAccountId, string IdentityTokenFile);

/// <summary>A minted federation access token. ExpiresAt is Unix epoch seconds.</summary>
public sealed class AnthropicFederationAccessToken(string token, double expiresAtUnixSeconds)
{
    [JsonIgnore] public string Token { get; } = token;
    public double ExpiresAtUnixSeconds { get; } = expiresAtUnixSeconds;
    public override string ToString() => "AnthropicFederationAccessToken [redacted]";
}

/// <summary>SDK WorkloadIdentityError. Body keeps only RFC 6749 error fields (or a truncated non-JSON body).</summary>
public sealed class AnthropicWorkloadIdentityException(string message, int? statusCode = null, string? body = null,
    string? requestId = null, Exception? inner = null) : Exception(message, inner)
{
    public int? StatusCode { get; } = statusCode;
    public string? Body { get; } = body;
    public string? RequestId { get; } = requestId;
}

/// <summary>Anthropic workload identity federation: selection, jwt-bearer exchange and request token application.</summary>
public static class AnthropicWorkloadIdentityFederation
{
    public const string TokenEndpointPath = "/v1/oauth/token";
    public const string GrantTypeJwtBearer = "urn:ietf:params:oauth:grant-type:jwt-bearer";
    public const string OAuthApiBeta = "oauth-2025-04-20";
    public const string FederationBeta = "oidc-federation-2026-04-01";
    public const string DefaultUserAgent = "PiSharp oidcFederationProvider";
    public const int MaximumAssertionCharacters = 16 * 1024;
    public const int MaximumTokenResponseBytes = 1 << 20;
    private const int MaximumErrorBodyCharacters = 2000;

    /// <summary>
    /// getAnthropicFederation: only for the anthropic provider (the exchange is an Anthropic endpoint) and
    /// only when no key or auth header was resolved. Rule, organization and identity token file are required.
    /// </summary>
    public static AnthropicFederationConfiguration? Configure(string? provider, string? apiKey,
        IEnumerable<KeyValuePair<string, string?>>? headers, ProviderEnvironmentSnapshot environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (provider != "anthropic" || HasRequestAuth(apiKey, headers)) return null;
        var rule = environment.GetValue(InjectedAuthenticationResolver.AnthropicFederationRuleId);
        var organization = environment.GetValue(InjectedAuthenticationResolver.AnthropicOrganizationId);
        var file = environment.GetValue(InjectedAuthenticationResolver.AnthropicIdentityTokenFile);
        if (rule is null || organization is null || file is null) return null;
        return new(organization, environment.GetValue(InjectedAuthenticationResolver.AnthropicWorkspaceId), rule,
            environment.GetValue(InjectedAuthenticationResolver.AnthropicServiceAccountId), file);
    }

    /// <summary>hasRequestAuth: a truthy key, or a non-blank authorization, x-api-key or cf-aig-authorization header.</summary>
    public static bool HasRequestAuth(string? apiKey, IEnumerable<KeyValuePair<string, string?>>? headers) =>
        !string.IsNullOrEmpty(apiKey) || headers is not null && headers.Any(header => header.Value is { } value && value.Trim().Length > 0 &&
            (header.Key.Equals("authorization", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("x-api-key", StringComparison.OrdinalIgnoreCase) ||
             header.Key.Equals("cf-aig-authorization", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Rejects endpoints that would send the assertion over cleartext; loopback HTTP stays allowed.</summary>
    public static void RequireSecureTokenEndpoint(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri) throw new AnthropicWorkloadIdentityException($"Invalid token endpoint base URL \"{baseUri}\"");
        if (baseUri.Scheme == Uri.UriSchemeHttps) return;
        var host = baseUri.Host.Trim('[', ']').ToLowerInvariant();
        if (baseUri.Scheme == Uri.UriSchemeHttp && host is "localhost" or "127.0.0.1" or "::1") return;
        throw new AnthropicWorkloadIdentityException($"Refusing to send credential over non-https token endpoint \"{baseUri}\"");
    }

    /// <summary>The SDK's identityTokenFromFile: re-read on every exchange so rotated projected tokens are picked up.</summary>
    public static async Task<string> ReadIdentityTokenAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path)) throw new AnthropicWorkloadIdentityException("Identity token file path is empty");
        string content;
        try { content = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { throw new AnthropicWorkloadIdentityException($"Failed to read identity token file at {path}: {error.Message}", inner: error); }
        var token = content.Trim();
        return token.Length == 0 ? throw new AnthropicWorkloadIdentityException($"Identity token file at {path} is empty") : token;
    }

    /// <summary>The jwt-bearer exchange request (oidcFederationProvider), with the exact body field order.</summary>
    public static HttpRequestMessage CreateExchangeRequest(Uri baseUri, AnthropicFederationConfiguration configuration,
        string identityToken, string? userAgent = null)
    {
        ArgumentNullException.ThrowIfNull(configuration); ArgumentNullException.ThrowIfNull(identityToken);
        RequireSecureTokenEndpoint(baseUri);
        if (identityToken.Length > MaximumAssertionCharacters)
            throw new AnthropicWorkloadIdentityException($"Identity token is {(identityToken.Length + 1023) / 1024} KiB, exceeds the 16 KiB assertion limit");
        var body = new JsonObject
        {
            ["grant_type"] = GrantTypeJwtBearer, ["assertion"] = identityToken,
            ["federation_rule_id"] = configuration.FederationRuleId, ["organization_id"] = configuration.OrganizationId,
        };
        if (!string.IsNullOrEmpty(configuration.ServiceAccountId)) body["service_account_id"] = configuration.ServiceAccountId;
        if (!string.IsNullOrEmpty(configuration.WorkspaceId)) body["workspace_id"] = configuration.WorkspaceId;
        var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint(baseUri))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("anthropic-beta", OAuthApiBeta + "," + FederationBeta);
        request.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrEmpty(userAgent) ? DefaultUserAgent : userAgent);
        return request;
    }

    /// <summary>One fresh exchange. Wrap in <see cref="AnthropicFederationTokenCache"/> to avoid exchanging per request.</summary>
    public static async Task<AnthropicFederationAccessToken> ExchangeAsync(HttpMessageInvoker http, Uri baseUri,
        AnthropicFederationConfiguration configuration, TimeProvider? timeProvider = null, string? userAgent = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http); ArgumentNullException.ThrowIfNull(configuration);
        RequireSecureTokenEndpoint(baseUri);
        var jwt = await ReadIdentityTokenAsync(configuration.IdentityTokenFile, cancellationToken).ConfigureAwait(false);
        using var request = CreateExchangeRequest(baseUri, configuration, jwt, userAgent);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { throw new AnthropicWorkloadIdentityException($"Failed to reach token endpoint {request.RequestUri}: {error.Message}", inner: error); }
        using (response)
        {
            var requestId = response.Headers.TryGetValues("Request-Id", out var ids) ? string.Join(", ", ids) : null;
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                string text;
                try { text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception) { text = ""; }
                var redacted = RedactSensitive(text);
                var hint = status != 401 ? "" : " Ensure your federation rule matches your identity token. " + (configuration.WorkspaceId is { Length: > 0 } ? ""
                    : "If your federation rule is scoped to multiple workspaces, set the ANTHROPIC_WORKSPACE_ID environment variable, the 'workspace_id' config key, or the `workspaceId` option. ")
                    + "View your authentication events in the Workload identity page of Claude Console for more details.";
                throw new AnthropicWorkloadIdentityException($"Token exchange failed with status {status}{(requestId is null ? "" : $" (request-id {requestId})")}: {redacted}{hint}",
                    status, redacted, requestId);
            }
            var limited = await ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            JsonNode? data;
            try { data = JsonNode.Parse(limited); }
            catch (JsonException) { throw new AnthropicWorkloadIdentityException($"Token endpoint returned non-JSON response (status {status})", status, RedactSensitive(limited), requestId); }
            var fields = data as JsonObject;
            var safe = fields is null ? "null" : RedactObject(fields).ToJsonString();
            if (fields?["access_token"] is not JsonValue accessValue || !accessValue.TryGetValue<string>(out var access) || access.Length == 0)
                throw new AnthropicWorkloadIdentityException($"Token endpoint response missing access_token: {safe}", status, safe, requestId);
            if (fields["token_type"] is { } tokenType && Truthy(tokenType) &&
                !(tokenType is JsonValue typeValue && typeValue.TryGetValue<string>(out var type) && type.Equals("bearer", StringComparison.OrdinalIgnoreCase)))
                throw new AnthropicWorkloadIdentityException($"Token endpoint response: unsupported token_type \"{tokenType.ToJsonString().Trim('"')}\" (want Bearer)", status, safe, requestId);
            var expiresIn = JsNumber(fields["expires_in"], fields.ContainsKey("expires_in"));
            if (!double.IsFinite(expiresIn))
                throw new AnthropicWorkloadIdentityException($"Token endpoint response missing required fields: {safe}", status, safe, requestId);
            return new(access, NowSeconds(timeProvider ?? TimeProvider.System) + expiresIn);
        }
    }

    /// <summary>client.ts token auth: Bearer token unless the request already owns Authorization, and the OAuth API beta appended.</summary>
    public static void ApplyAccessToken(HttpRequestMessage request, string accessToken)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrEmpty(accessToken);
        if (!request.Headers.Contains("Authorization")) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
        if (request.Headers.TryGetValues("anthropic-beta", out var values))
        {
            var existing = string.Join(", ", values);
            if (existing.Split(',').Select(value => value.Trim()).Contains(OAuthApiBeta, StringComparer.Ordinal)) return;
            request.Headers.Remove("anthropic-beta");
            request.Headers.TryAddWithoutValidation("anthropic-beta", existing + ", " + OAuthApiBeta);
        }
        else request.Headers.TryAddWithoutValidation("anthropic-beta", OAuthApiBeta);
    }

    /// <summary>redactSensitive for a token-endpoint body: JSON keeps only error, error_description and error_uri.</summary>
    public static string RedactSensitive(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(body); }
        catch (JsonException)
        {
            return body.Length <= MaximumErrorBodyCharacters ? body
                : body[..MaximumErrorBodyCharacters] + $"... <{body.Length - MaximumErrorBodyCharacters} more chars>";
        }
        return parsed switch
        {
            null => "null", JsonObject fields => RedactObject(fields).ToJsonString(), JsonValue value => value.ToJsonString(), _ => "null",
        };
    }

    internal static long NowSeconds(TimeProvider time) => (long)Math.Floor(time.GetUtcNow().ToUnixTimeMilliseconds() / 1000d);
    private static Uri TokenEndpoint(Uri baseUri) => new(baseUri.AbsoluteUri.TrimEnd('/') + TokenEndpointPath);
    private static JsonObject RedactObject(JsonObject fields)
    {
        var safe = new JsonObject();
        foreach (var (key, value) in fields)
            if (key is "error" or "error_description" or "error_uri") safe[key] = value?.DeepClone();
        return safe;
    }
    private static bool Truthy(JsonNode node) => node is not JsonValue value || value.GetValueKind() switch
    {
        JsonValueKind.String => value.GetValue<string>().Length != 0, JsonValueKind.False => false,
        JsonValueKind.Number => value.GetValue<double>() is var number && number != 0 && !double.IsNaN(number), _ => true,
    };
    // ECMAScript Number(): absent is NaN, null is 0, booleans are 0/1, strings parse after trimming.
    private static double JsNumber(JsonNode? node, bool present)
    {
        if (!present) return double.NaN;
        if (node is null) return 0;
        if (node is not JsonValue value) return double.NaN;
        return value.GetValueKind() switch
        {
            JsonValueKind.Number => value.GetValue<double>(), JsonValueKind.True => 1, JsonValueKind.False => 0,
            JsonValueKind.String => ParseNumber(value.GetValue<string>().Trim()), _ => double.NaN,
        };
    }
    private static double ParseNumber(string text) => text.Length == 0 ? 0
        : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN;
    private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[MaximumTokenResponseBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return Encoding.UTF8.GetString(buffer, 0, total);
    }
}

/// <summary>
/// The SDK TokenCache: more than 120 s left serves the cache; 30-120 s serves it and refreshes in the background
/// (failures back off 5 s and keep the stale token); under 30 s blocks. Concurrent refreshes coalesce, and a
/// started refresh is not cancelled by the caller that awaited it.
/// </summary>
public sealed class AnthropicFederationTokenCache
{
    public const int AdvisoryRefreshThresholdSeconds = 120;
    public const int MandatoryRefreshThresholdSeconds = 30;
    public const int AdvisoryRefreshBackoffSeconds = 5;
    private readonly Func<CancellationToken, Task<AnthropicFederationAccessToken>> provider;
    private readonly TimeProvider time;
    private readonly Action<Exception>? onAdvisoryRefreshError;
    private readonly object gate = new();
    private AnthropicFederationAccessToken? cached;
    private Task<AnthropicFederationAccessToken>? pending;
    private bool nextForce;
    private long lastAdvisoryError;

    public AnthropicFederationTokenCache(Func<CancellationToken, Task<AnthropicFederationAccessToken>> provider,
        TimeProvider? timeProvider = null, Action<Exception>? onAdvisoryRefreshError = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        this.provider = provider; time = timeProvider ?? TimeProvider.System; this.onAdvisoryRefreshError = onAdvisoryRefreshError;
    }

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        bool force; AnthropicFederationAccessToken? current;
        lock (gate) { force = nextForce; nextForce = false; current = cached; }
        if (force || current is null) return (await Refresh(force).WaitAsync(cancellationToken).ConfigureAwait(false)).Token;
        var remaining = current.ExpiresAtUnixSeconds - AnthropicWorkloadIdentityFederation.NowSeconds(time);
        if (remaining > AdvisoryRefreshThresholdSeconds) return current.Token;
        if (remaining > MandatoryRefreshThresholdSeconds) { BackgroundRefresh(); return current.Token; }
        return (await Refresh(false).WaitAsync(cancellationToken).ConfigureAwait(false)).Token;
    }

    /// <summary>After a 401: drop the cache and force the next acquisition to exchange again.</summary>
    public void Invalidate() { lock (gate) { cached = null; nextForce = true; } }

    private Task<AnthropicFederationAccessToken> Refresh(bool force)
    {
        lock (gate) if (pending is not null && !force) return pending;
        return DoRefresh();
    }

    private void BackgroundRefresh()
    {
        lock (gate)
            if (pending is not null || AnthropicWorkloadIdentityFederation.NowSeconds(time) - lastAdvisoryError < AdvisoryRefreshBackoffSeconds) return;
        _ = DoRefresh().ContinueWith(failed =>
        {
            lock (gate) lastAdvisoryError = AnthropicWorkloadIdentityFederation.NowSeconds(time);
            onAdvisoryRefreshError?.Invoke(failed.Exception!.GetBaseException());
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private Task<AnthropicFederationAccessToken> DoRefresh()
    {
        var completion = new TaskCompletionSource<AnthropicFederationAccessToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) pending = completion.Task;
        _ = RunAsync();
        return completion.Task;

        async Task RunAsync()
        {
            try
            {
                var token = await provider(CancellationToken.None).ConfigureAwait(false);
                // As in the SDK, any settled refresh clears the pending slot.
                lock (gate) { cached = token; pending = null; }
                completion.SetResult(token);
            }
            catch (Exception error)
            {
                lock (gate) pending = null;
                completion.SetException(error);
            }
        }
    }
}

/// <summary>
/// HTTP-level equivalent of Pi's reused federation SDK client: every request carries a cached federation
/// token, and a 401 invalidates the cache so the next request exchanges again. Like Pi (maxRetries 0)
/// the 401 itself is returned, not retried. Reuse one handler per provider to share the token cache.
/// </summary>
public sealed class AnthropicFederationHandler : DelegatingHandler
{
    public AnthropicFederationHandler(Uri baseUri, AnthropicFederationConfiguration configuration, HttpMessageHandler innerHandler,
        TimeProvider? timeProvider = null, string? userAgent = null) : base(innerHandler)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        AnthropicWorkloadIdentityFederation.RequireSecureTokenEndpoint(baseUri);
        var exchange = new HttpMessageInvoker(innerHandler, disposeHandler: false);
        Configuration = configuration;
        Cache = new(token => AnthropicWorkloadIdentityFederation.ExchangeAsync(exchange, baseUri, configuration, timeProvider, userAgent, token), timeProvider);
    }

    public AnthropicFederationConfiguration Configuration { get; }
    public AnthropicFederationTokenCache Cache { get; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        AnthropicWorkloadIdentityFederation.ApplyAccessToken(request, await Cache.GetTokenAsync(cancellationToken).ConfigureAwait(false));
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized) Cache.Invalidate();
        return response;
    }
}
