// Decision 0011 (run W2, WU5): "Test connection", which runs only when the user clicks it, and only against a free endpoint: the provider's
// models list (GET), never a completion, so a test costs nothing. A stored sign-in is checked by resolving it (a refresh, at the auth server,
// only when it is due), and a GitHub Copilot sign-in also lists its models. A key can be tested before it is saved (the API key modal) and
// is then forgotten. Answers carry the status, the latency and the number of models; a provider's error text is scrubbed, because an error
// can echo the key. Offline (--faux) nothing is sent. Providers without a known models endpoint answer "not available" instead of guessing.
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.AI.Catalogs;

namespace PiSharp.WebAgent.Credentials;

/// <summary>The result of a connection test. It never holds a secret.</summary>
/// <param name="Ok">Whether the provider accepted the credential.</param>
/// <param name="Message">What to show, scrubbed.</param>
public sealed record ConnectionTestResult(bool Ok, string Message)
{
    /// <summary>False when no test exists for the account (offline, ambient credentials, an unknown endpoint); nothing was sent.</summary>
    public bool Supported { get; init; } = true;

    /// <summary>The provider's HTTP status, when a request was sent.</summary>
    public int? StatusCode { get; init; }

    /// <summary>The request's duration in milliseconds.</summary>
    public long? LatencyMs { get; init; }

    /// <summary>How many models the provider listed, when it said.</summary>
    public int? ModelCount { get; init; }

    /// <summary>The request, such as <c>GET https://api.openai.com/v1/models</c> (never with a credential).</summary>
    public string? Endpoint { get; init; }

    /// <summary>When the test ran.</summary>
    public DateTimeOffset TestedAt { get; init; }
}

/// <summary>Connection tests, on the user's click only.</summary>
public interface IConnectionTester
{
    /// <summary>Tests a stored account and records the result on it (<see cref="AccountInfo.LastTest"/>).</summary>
    /// <exception cref="KeyNotFoundException">No such account.</exception>
    Task<ConnectionTestResult> TestAccountAsync(string accountId, CancellationToken cancellationToken = default);

    /// <summary>Tests a key that is not stored (the API key modal before Save); the key is used for this one request and forgotten.</summary>
    /// <exception cref="ArgumentException">An invalid provider id or key.</exception>
    Task<ConnectionTestResult> TestKeyAsync(string provider, string apiKey, IReadOnlyDictionary<string, string>? settings = null,
        CancellationToken cancellationToken = default);
}

/// <summary>The models-list connection tests.</summary>
public sealed class ConnectionTester(CredentialVault vault, CredentialsOptions options) : IConnectionTester
{
    /// <summary>The <c>anthropic-version</c> header Anthropic's API requires.</summary>
    public const string AnthropicVersion = "2023-06-01";

    /// <summary>The models-list request of <paramref name="provider"/> with <paramref name="key"/>, or null when none is known.
    /// <paramref name="variable"/> is the environment variable the key came from (an <c>ANTHROPIC_AUTH_TOKEN</c> is a bearer token);
    /// a <c>baseUrl</c> setting replaces the provider's.</summary>
    public static HttpRequestMessage? ModelsRequest(string provider, string key, IReadOnlyDictionary<string, string>? settings, string? variable = null)
    {
        var baseUrl = settings is not null && settings.TryGetValue("baseUrl", out var configured) && Uri.TryCreate(configured, UriKind.Absolute, out var uri) &&
            uri.Scheme is "https" or "http" ? configured.TrimEnd('/') : null;
        var definition = BuiltinProviders.All.FirstOrDefault(item => item.Id == provider);
        string? url;
        var bearer = true;
        var headers = new List<(string, string)>();
        switch (provider)
        {
            case "anthropic":
                url = (baseUrl ?? "https://api.anthropic.com") + "/v1/models";
                bearer = variable == ProviderEnvironmentKeys.AnthropicAuthToken;
                if (!bearer) headers.Add(("x-api-key", key));
                headers.Add(("anthropic-version", AnthropicVersion));
                break;
            case "google":
                url = (baseUrl ?? "https://generativelanguage.googleapis.com/v1beta") + "/models";
                bearer = false;
                headers.Add(("x-goog-api-key", key));
                break;
            case "mistral":
                url = (baseUrl ?? "https://api.mistral.ai") + "/v1/models";
                break;
            case "deepseek":
                url = (baseUrl ?? "https://api.deepseek.com") + "/models";
                break;
            case "fireworks":
                url = (baseUrl ?? "https://api.fireworks.ai/inference") + "/v1/models";
                break;
            case "openrouter":
                // OpenRouter lists models without a key; its key endpoint is the free check that the key works.
                url = (baseUrl ?? "https://openrouter.ai/api/v1") + "/key";
                break;
            default:
                var root = baseUrl ?? definition?.BaseUrl;
                url = root is not null && (baseUrl is not null || root.EndsWith("/v1", StringComparison.Ordinal)) &&
                    (baseUrl is not null || definition!.Apis.Any(api => api is "openai-completions" or "openai-responses"))
                    ? root.TrimEnd('/') + "/models" : null;
                break;
        }
        if (url is null) return null;
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (bearer) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        return request;
    }

    /// <inheritdoc/>
    public async Task<ConnectionTestResult> TestAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var account = vault.Get(accountId) ?? throw new KeyNotFoundException($"No account {accountId}.");
        var result = await TestStoredAsync(account, cancellationToken).ConfigureAwait(false);
        vault.RecordTest(accountId, result);
        return result;
    }

    /// <inheritdoc/>
    public Task<ConnectionTestResult> TestKeyAsync(string provider, string apiKey, IReadOnlyDictionary<string, string>? settings = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(provider)) throw new ArgumentException("Choose a provider.");
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("Enter an API key to test.");
        var key = apiKey.Trim();
        if (key.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            throw new ArgumentException("The API key contains spaces or line breaks; paste the key on its own.");
        return TestKeyCoreAsync(provider.Trim(), key, settings, null, cancellationToken);
    }

    private async Task<ConnectionTestResult> TestStoredAsync(AccountInfo account, CancellationToken token)
    {
        if (options.Offline) return Unsupported(CredentialsOptions.OfflineMessage);
        if (account.Kind == AccountKind.Ambient)
            return Unsupported("Ambient credentials are checked by the provider's own SDK when a request uses them.");
        if (account.Kind == AccountKind.OAuth) return await TestSignInAsync(account, token).ConfigureAwait(false);
        AccountCredential? credential;
        try { credential = await vault.ReadAsync(account.Id, token).ConfigureAwait(false); }
        catch (CredentialUnavailableException error) { return Failed(error.Message, null); }
        if (credential?.ApiKey is not { } key) return Unsupported("This account has settings but no key, so there is nothing to test here.");
        return await TestKeyCoreAsync(account.Provider, key, account.Settings, account.Kind == AccountKind.Environment ? account.Source : null, token)
            .ConfigureAwait(false);
    }

    private async Task<ConnectionTestResult> TestKeyCoreAsync(string provider, string key, IReadOnlyDictionary<string, string>? settings, string? variable,
        CancellationToken token)
    {
        if (options.Offline) return Unsupported(CredentialsOptions.OfflineMessage);
        using var request = ModelsRequest(provider, key, settings, variable);
        if (request is null) return Unsupported($"A connection test for {provider} is not available yet.");
        using var http = options.CreateHttp();
        return await SendAsync(http, request, key, token).ConfigureAwait(false);
    }

    private async Task<ConnectionTestResult> TestSignInAsync(AccountInfo account, CancellationToken token)
    {
        using var http = options.CreateHttp();
        IProviderOAuth flow;
        try { flow = SignInFlows.CreateFlow(account.Provider, http, options); }
        catch (InvalidOperationException) { return Unsupported($"A connection test for {account.Provider} sign-ins is not available yet."); }
        OAuthCredentialSnapshot? credential;
        try
        {
            credential = await new StoredOAuthLifecycle(vault.OAuthSource(account.Id), flow, options.Time)
                .ResolveAsync(account.Provider, cancellationToken: token).ConfigureAwait(false);
        }
        catch (OAuthLifecycleException error) when (error.Failure == OAuthLifecycleFailure.Refresh)
        {
            const string reason = "The sign-in could not be refreshed (it was revoked or has expired); sign in again.";
            await vault.MarkExpiredAsync(account.Id, reason, CancellationToken.None).ConfigureAwait(false);
            return Failed(reason, null);
        }
        catch (OAuthLifecycleException error)
        {
            return Failed(error.OriginalException is CredentialUnavailableException unavailable
                ? unavailable.Message : "The stored sign-in could not be read; sign in again.", null);
        }
        catch (CredentialUnavailableException error) { return Failed(error.Message, null); }
        if (credential is null) return Failed("No sign-in is stored for this account; sign in again.", null);
        if (account.Provider != SignInFlows.CopilotProvider)
            return new(true, $"Signed in. The access token is valid until {DateTimeOffset.FromUnixTimeMilliseconds(credential.ExpiresUnixMilliseconds):yyyy-MM-dd HH:mm} UTC.")
            {
                TestedAt = options.Time.GetUtcNow(),
            };
        // Copilot: the token is exchanged through the GitHub token above; listing its models is free and proves the Copilot token works.
        var auth = flow.ToAuth(credential);
        using var request = new HttpRequestMessage(HttpMethod.Get, auth.BaseUrl!.TrimEnd('/') + "/models");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + credential.Access);
        foreach (var (name, value) in GitHubCopilotOAuth.CopilotHeaders) request.Headers.TryAddWithoutValidation(name, value);
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", GitHubCopilotOAuth.ApiVersion);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        return await SendAsync(http, request, credential.Access, token, credential.Refresh).ConfigureAwait(false);
    }

    private async Task<ConnectionTestResult> SendAsync(HttpMessageInvoker http, HttpRequestMessage request, string secret, CancellationToken token,
        string? otherSecret = null)
    {
        var endpoint = $"GET {request.RequestUri!.GetLeftPart(UriPartial.Path)}";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(options.TestTimeout);
        var clock = Stopwatch.StartNew();
        try
        {
            using var response = await http.SendAsync(request, deadline.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            var elapsed = clock.ElapsedMilliseconds;
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
                return new(true, "Connected.") { StatusCode = status, LatencyMs = elapsed, ModelCount = CountModels(body), Endpoint = endpoint, TestedAt = options.Time.GetUtcNow() };
            var reason = ErrorText(body) ?? response.ReasonPhrase ?? "";
            var message = SecretText.Scrub($"{status} {reason}".Trim(), secret, otherSecret);
            if (status is 401 or 403) message += " The provider rejected the credential.";
            return new(false, message) { StatusCode = status, LatencyMs = elapsed, Endpoint = endpoint, TestedAt = options.Time.GetUtcNow() };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return Failed($"No answer within {options.TestTimeout.TotalSeconds:0} seconds.", endpoint);
        }
        catch (HttpRequestException error)
        {
            return Failed(SecretText.Scrub("Could not reach the provider: " + error.Message, secret, otherSecret), endpoint);
        }
    }

    private static int? CountModels(string body)
    {
        try
        {
            return JsonNode.Parse(body) switch
            {
                JsonArray array => array.Count,
                JsonObject { } json when json["data"] is JsonArray data => data.Count,
                JsonObject { } json when json["models"] is JsonArray models => models.Count,
                _ => null,
            };
        }
        catch (JsonException) { return null; }
    }

    private static string? ErrorText(string body)
    {
        try
        {
            var json = JsonNode.Parse(body) as JsonObject;
            return json?["error"] switch
            {
                JsonObject error => error["message"]?.GetValue<string>() ?? error["code"]?.ToString(),
                JsonValue value when value.TryGetValue<string>(out var text) => text,
                _ => json?["message"] is JsonValue message && message.TryGetValue<string>(out var text) ? text : null,
            };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return null; }
    }

    private ConnectionTestResult Unsupported(string message) => new(false, message) { Supported = false, TestedAt = options.Time.GetUtcNow() };

    private ConnectionTestResult Failed(string message, string? endpoint) => new(false, message) { Endpoint = endpoint, TestedAt = options.Time.GetUtcNow() };
}
