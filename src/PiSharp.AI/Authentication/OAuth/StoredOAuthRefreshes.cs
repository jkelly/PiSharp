// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/auth/oauth/openai-chatgpt.ts (refreshAccessToken,
// requestToken, credentialFromTokenResponse) and packages/ai/src/auth/oauth/openrouter.ts (refresh).
using System.Globalization;
using System.Text.Json;

namespace PiSharp.AI.Authentication.OAuth;

/// <summary>
/// Sign in with ChatGPT token refresh (provider <c>openai</c>): <c>POST https://auth.openai.com/api/accounts/oauth/token</c>
/// with the form <c>grant_type=refresh_token</c>, the issued <c>client_id</c> stored with the credential, the refresh token and
/// <c>resource=https://api.openai.com/v1</c>. The new token must carry the <c>chatgpt.tokens.use.direct</c> scope; it expires
/// three minutes before the reported lifetime ends. The credential keeps its <c>clientId</c> and the granted <c>scopes</c>
/// array (credentialFromTokenResponse). This is the single ChatGPT token refresh; <see cref="OpenAIChatGPTOAuth"/> (login)
/// refreshes through it and builds its login credential with <see cref="CredentialFromTokenResponse"/>.
/// </summary>
public sealed class OpenAIChatGPTOAuthRefresh(HttpMessageInvoker http, TimeProvider? timeProvider = null, string tokenUrl = OpenAIChatGPTOAuthRefresh.TokenUrl)
    : IAdmittedOAuthRefresh
{
    public const string TokenUrl = "https://auth.openai.com/api/accounts/oauth/token";
    private const string Resource = "https://api.openai.com/v1";
    private const string DirectTokenScope = "chatgpt.tokens.use.direct";
    private const long ExpiryMarginMilliseconds = 3 * 60 * 1000;
    private readonly HttpMessageInvoker http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public async Task<OAuthCredentialSnapshot> RefreshAsync(string provider, OAuthCredentialSnapshot current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!current.ProviderData.TryGetValue("clientId", out var clientId) || string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("Stored OpenAI OAuth credential does not contain an issued client ID; reconnect ChatGPT");
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent([new("grant_type", "refresh_token"), new("client_id", clientId),
                new("refresh_token", current.Refresh), new("resource", Resource)])
        };
        request.Headers.TryAddWithoutValidation("accept", "application/json");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAI OAuth token request failed ({(int)response.StatusCode}): {(text.Length != 0 ? text : response.ReasonPhrase)}");
        JsonElement token;
        try { using var document = JsonDocument.Parse(text); token = document.RootElement.Clone(); }
        catch (JsonException) { throw new InvalidOperationException("OpenAI OAuth token response must be an object"); }
        return CredentialFromTokenResponse(token, clientId, time.GetUtcNow().ToUnixTimeMilliseconds());
    }

    /// <summary>credentialFromTokenResponse: validates the token response and builds the stored credential.</summary>
    public static OAuthCredentialSnapshot CredentialFromTokenResponse(JsonElement token, string clientId, long nowUnixMilliseconds)
    {
        if (token.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("OpenAI OAuth token response must be an object");
        string Required(string field) => token.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString()!.Trim().Length != 0 ? value.GetString()! : throw new InvalidOperationException($"OpenAI OAuth token response has invalid {field}");
        var access = Required("access_token"); var refresh = Required("refresh_token"); var scope = Required("scope");
        if (!token.TryGetProperty("expires_in", out var expiresIn) || expiresIn.ValueKind != JsonValueKind.Number ||
            !expiresIn.TryGetDouble(out var seconds) || !double.IsFinite(seconds) || seconds <= 0)
            throw new InvalidOperationException("OpenAI OAuth token response has invalid expires_in");
        var scopes = scope.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (!scopes.Contains(DirectTokenScope, StringComparer.Ordinal))
            throw new InvalidOperationException($"OpenAI OAuth grant did not include {DirectTokenScope}");
        var expires = nowUnixMilliseconds + (long)(seconds * 1000) - ExpiryMarginMilliseconds;
        return new(access, refresh, expires, new Dictionary<string, string> { ["clientId"] = clientId },
            new Dictionary<string, PiSharp.Contracts.JsonData> { ["scopes"] = PiSharp.Contracts.JsonData.Parse(JsonSerializer.Serialize(scopes)) });
    }

    public override string ToString() => "OpenAIChatGPTOAuthRefresh";
}

/// <summary>OpenRouter OAuth: the login exchanges the code for a permanent API key, so refresh returns the credential
/// unchanged.</summary>
public sealed class OpenRouterOAuthRefresh : IAdmittedOAuthRefresh
{
    public static OpenRouterOAuthRefresh Instance { get; } = new();
    public Task<OAuthCredentialSnapshot> RefreshAsync(string provider, OAuthCredentialSnapshot current, CancellationToken cancellationToken) =>
        Task.FromResult(current ?? throw new ArgumentNullException(nameof(current)));
}

/// <summary>Dispatches a stored OAuth refresh to the provider's implementation (auth.oauth.refresh per provider).</summary>
public sealed class ProviderOAuthRefreshes(IReadOnlyDictionary<string, IAdmittedOAuthRefresh> byProvider) : IAdmittedOAuthRefresh
{
    public bool Supports(string provider) => byProvider.ContainsKey(provider);
    public Task<OAuthCredentialSnapshot> RefreshAsync(string provider, OAuthCredentialSnapshot current, CancellationToken cancellationToken) =>
        byProvider.TryGetValue(provider, out var refresh) ? refresh.RefreshAsync(provider, current, cancellationToken)
            : throw new NotSupportedException(string.Create(CultureInfo.InvariantCulture, $"No OAuth refresh for provider {provider}"));
}
