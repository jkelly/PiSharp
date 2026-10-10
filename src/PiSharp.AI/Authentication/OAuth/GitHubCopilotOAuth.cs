// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/auth/oauth/github-copilot.ts (device-code login for github.com
// or a GitHub Enterprise domain, Copilot token exchange, model catalog and policy enabling, refresh and toAuth's per-credential base
// URL) and providers/github-copilot.ts (filterModels by availableModelIds).
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI.Providers;
using PiSharp.Contracts;

namespace PiSharp.AI.Authentication.OAuth;

public sealed class GitHubCopilotOAuth(HttpMessageInvoker http, Func<string, bool>? isCatalogModel = null, TimeProvider? time = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IProviderOAuth
{
    public static readonly string ClientId = Encoding.UTF8.GetString(Convert.FromBase64String("SXYxLmI1MDdhMDhjODdlY2ZlOTg="));
    public static readonly IReadOnlyList<KeyValuePair<string, string>> CopilotHeaders =
    [
        new("User-Agent", "GitHubCopilotChat/0.35.0"), new("Editor-Version", "vscode/1.107.0"),
        new("Editor-Plugin-Version", "copilot-chat/0.35.0"), new("Copilot-Integration-Id", "vscode-chat")
    ];
    public const string ApiVersion = "2026-06-01";
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((wait, token) => Task.Delay(wait, time ?? TimeProvider.System, token));

    public string Name => "GitHub Copilot";
    public bool IsSubscription => true;
    public string? LoginLabel => null;

    /// <summary>normalizeDomain: the hostname of a URL or bare domain; null when blank or invalid.</summary>
    public static string? NormalizeDomain(string input)
    {
        var trimmed = (input ?? "").Trim();
        if (trimmed.Length == 0) return null;
        return Uri.TryCreate(trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : "https://" + trimmed, UriKind.Absolute, out var url) && url.Host.Length != 0
            ? url.Host : null;
    }

    private static (string DeviceCode, string AccessToken, string CopilotToken) Urls(string domain) =>
        ($"https://{domain}/login/device/code", $"https://{domain}/login/oauth/access_token", $"https://api.{domain}/copilot_internal/v2/token");

    public async Task<OAuthCredentialSnapshot> LoginAsync(IProviderAuthInteraction interaction, ProviderLoginOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        var input = await interaction.PromptAsync(new(AuthPromptKind.Text, "GitHub Enterprise URL/domain (blank for github.com)", "company.ghe.com"), cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested) throw OAuthFlows.Cancelled(cancellationToken);
        var enterprise = NormalizeDomain(input);
        if (input.Trim().Length != 0 && enterprise is null) throw new InvalidOperationException("Invalid GitHub Enterprise URL/domain");
        var domain = enterprise ?? "github.com";
        var urls = Urls(domain);
        var started = await FetchJsonAsync(HttpMethod.Post, urls.DeviceCode, cancellationToken, OAuthFlows.Form(("client_id", ClientId), ("scope", "read:user")),
            [new("Accept", "application/json"), new("User-Agent", "GitHubCopilotChat/0.35.0")]).ConfigureAwait(false) as JsonObject
            ?? throw new InvalidOperationException("Invalid device code response");
        if (started["device_code"] is not JsonValue code || !code.TryGetValue<string>(out var deviceCode) ||
            started["user_code"] is not JsonValue user || !user.TryGetValue<string>(out var userCode) ||
            started["verification_uri"] is not JsonValue uri || !uri.TryGetValue<string>(out var verificationUri) ||
            started["interval"] is { } intervalNode && (intervalNode is not JsonValue intervalValue || !intervalValue.TryGetValue<double>(out _)) ||
            started["expires_in"] is not JsonValue expires || !expires.TryGetValue<double>(out var expiresIn))
            throw new InvalidOperationException("Invalid device code response fields");
        // The verification URI is opened in the user's browser, so it must be an http(s) URL.
        var trusted = OAuthFlows.TrustedHttpUrl(verificationUri) ?? throw new InvalidOperationException("Untrusted verification_uri in device code response");
        double? interval = started["interval"] is JsonValue providedInterval && providedInterval.TryGetValue<double>(out var parsedInterval) ? parsedInterval : null;
        interaction.Notify(new(AuthEventKind.DeviceCode) { UserCode = userCode, VerificationUri = trusted, IntervalSeconds = interval, ExpiresInSeconds = expiresIn });
        var githubToken = await OAuthFlows.PollDeviceCodeAsync<string>(async token =>
        {
            var raw = await FetchJsonAsync(HttpMethod.Post, urls.AccessToken, token, OAuthFlows.Form(("client_id", ClientId), ("device_code", deviceCode),
                ("grant_type", "urn:ietf:params:oauth:grant-type:device_code")), [new("Accept", "application/json"), new("User-Agent", "GitHubCopilotChat/0.35.0")]).ConfigureAwait(false) as JsonObject;
            if (raw?["access_token"] is JsonValue access && access.TryGetValue<string>(out var accessToken)) return new(OAuthFlows.DevicePollStatus.Complete, accessToken);
            if (raw?["error"] is JsonValue errorValue && errorValue.TryGetValue<string>(out var error))
            {
                if (error == "authorization_pending") return new(OAuthFlows.DevicePollStatus.Pending);
                if (error == "slow_down") return new(OAuthFlows.DevicePollStatus.SlowDown, IntervalSeconds: OAuthFlows.Number(raw, "interval"));
                var description = OAuthFlows.OptionalText(raw, "error_description") is { Length: > 0 } text ? ": " + text : "";
                return new(OAuthFlows.DevicePollStatus.Failed, Message: $"Device flow failed: {error}{description}");
            }
            return new(OAuthFlows.DevicePollStatus.Failed, Message: "Invalid device token response");
        }, interval, expiresIn, true, cancellationToken, _time).ConfigureAwait(false);
        var credential = await CopilotTokenAsync(githubToken, enterprise, cancellationToken).ConfigureAwait(false);
        var (available, policy) = await FetchModelsAsync(credential.Access, enterprise, cancellationToken, 2, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var enabled = new List<string>();
        if (policy.Count > 0)
        {
            interaction.Notify(new(AuthEventKind.Progress) { Message = "Enabling models..." });
            enabled = await EnableModelsAsync(credential.Access, policy, enterprise, cancellationToken).ConfigureAwait(false);
        }
        return WithModels(credential, available.Concat(enabled).Distinct(StringComparer.Ordinal).ToList());
    }

    private static OAuthCredentialSnapshot WithModels(OAuthCredentialSnapshot credential, List<string> ids) => new(credential.Access, credential.Refresh,
        credential.ExpiresUnixMilliseconds, credential.ProviderData, new Dictionary<string, JsonData> { ["availableModelIds"] = JsonData.Parse(JsonSerializer.Serialize(ids)) });

    /// <summary>refreshGitHubCopilotAccessToken: the GitHub token (stored as refresh) traded for a Copilot token; expires 5 min early.</summary>
    private async Task<OAuthCredentialSnapshot> CopilotTokenAsync(string githubToken, string? enterprise, CancellationToken token)
    {
        var headers = new List<KeyValuePair<string, string>> { new("Accept", "application/json"), new("Authorization", "Bearer " + githubToken) };
        headers.AddRange(CopilotHeaders);
        var raw = await FetchJsonAsync(HttpMethod.Get, Urls(enterprise ?? "github.com").CopilotToken, token, null, headers).ConfigureAwait(false) as JsonObject
            ?? throw new InvalidOperationException("Invalid Copilot token response");
        if (raw["token"] is not JsonValue value || !value.TryGetValue<string>(out var copilotToken) || raw["expires_at"] is not JsonValue at || !at.TryGetValue<double>(out var expiresAt))
            throw new InvalidOperationException("Invalid Copilot token response fields");
        var data = new Dictionary<string, string>();
        if (enterprise is not null) data["enterpriseUrl"] = enterprise;
        return new(copilotToken, githubToken, (long)(expiresAt * 1000 - 5 * 60 * 1000), data);
    }

    public async Task<OAuthCredentialSnapshot> RefreshAsync(string provider, OAuthCredentialSnapshot current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        var enterprise = EnterpriseDomain(current);
        var credential = await CopilotTokenAsync(current.Refresh, enterprise, cancellationToken).ConfigureAwait(false);
        var (available, _) = await FetchModelsAsync(credential.Access, enterprise, cancellationToken, 0, TimeSpan.Zero).ConfigureAwait(false);
        return WithModels(credential, available);
    }

    private static string? EnterpriseDomain(OAuthCredentialSnapshot credential) =>
        credential.ProviderData.TryGetValue("enterpriseUrl", out var url) && url.Length != 0 ? NormalizeDomain(url) : null;

    /// <summary>toAuth: the Copilot token as apiKey and the proxy endpoint the token names.</summary>
    public ProviderModelAuth ToAuth(OAuthCredentialSnapshot credential) =>
        new(credential.Access, BaseUrl: ProviderHeaderPolicies.CopilotBaseUrl(credential.Access, EnterpriseDomain(credential)));

    /// <summary>providers/github-copilot.ts filterModels: an OAuth credential's availableModelIds restrict the catalog.</summary>
    public static IReadOnlySet<string>? AvailableModels(OAuthCredentialSnapshot? credential) =>
        credential is not null && credential.ProviderJson.TryGetValue("availableModelIds", out var ids) && ids.Value.ValueKind == JsonValueKind.Array &&
        ids.Value.EnumerateArray().All(id => id.ValueKind == JsonValueKind.String)
            ? ids.Value.EnumerateArray().Select(id => id.GetString()!).ToHashSet(StringComparer.Ordinal) : null;

    private async Task<(List<string> Available, List<string> Policy)> FetchModelsAsync(string copilotToken, string? enterprise, CancellationToken token,
        int maxRetries, TimeSpan maxElapsed)
    {
        var baseUrl = ProviderHeaderPolicies.CopilotBaseUrl(copilotToken, enterprise);
        // Some Individual accounts report false for every picker flag despite enabled policies; only that endpoint falls back.
        var allowPolicyFallback = baseUrl == "https://api.individual.githubcopilot.com";
        var headers = new List<KeyValuePair<string, string>> { new("Accept", "application/json"), new("Authorization", "Bearer " + copilotToken) };
        headers.AddRange(CopilotHeaders); headers.Add(new("X-GitHub-Api-Version", ApiVersion));
        var response = await FetchWithRateLimitRetryAsync(HttpMethod.Get, baseUrl + "/models", null, headers, token, maxRetries, maxElapsed).ConfigureAwait(false);
        if (!response.Ok) throw new InvalidOperationException($"{response.Status} {response.StatusText}: {response.Body}");
        if (JsonNode.Parse(response.Body)?["data"] is not JsonArray data) throw new InvalidOperationException("Invalid Copilot models response");
        var models = new List<(string Id, bool Picker, string? Policy)>();
        foreach (var item in data.OfType<JsonObject>())
        {
            if (item["id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var id)) continue;
            if (item["capabilities"]?["supports"]?["tool_calls"] is JsonValue tools && tools.GetValueKind() == JsonValueKind.False) continue;
            models.Add((id, item["model_picker_enabled"]?.GetValueKind() == JsonValueKind.True, (item["policy"] as JsonObject)?["state"] is JsonValue state && state.TryGetValue<string>(out var policyState) ? policyState : null));
        }
        var picker = models.Where(model => model.Picker && model.Policy != "disabled").Select(model => model.Id).ToList();
        var usePolicyFallback = allowPolicyFallback && picker.Count == 0;
        var available = picker.Count > 0 || !allowPolicyFallback ? picker : models.Where(model => model.Policy == "enabled").Select(model => model.Id).ToList();
        var policyIds = models.Where(model => model.Policy == "unconfigured" && (isCatalogModel?.Invoke(model.Id) ?? false) && (model.Picker || usePolicyFallback))
            .Select(model => model.Id).ToList();
        return (available, policyIds);
    }

    /// <summary>enableGitHubCopilotModels: best-effort policy updates; exhausted rate limiting stops the batch.</summary>
    private async Task<List<string>> EnableModelsAsync(string copilotToken, List<string> ids, string? enterprise, CancellationToken token)
    {
        var enabled = new List<string>();
        var baseUrl = ProviderHeaderPolicies.CopilotBaseUrl(copilotToken, enterprise);
        foreach (var id in ids)
        {
            OAuthFlows.HttpResult response;
            try
            {
                var headers = new List<KeyValuePair<string, string>> { new("Authorization", "Bearer " + copilotToken) };
                headers.AddRange(CopilotHeaders); headers.Add(new("openai-intent", "chat-policy")); headers.Add(new("x-interaction-type", "chat-policy"));
                response = await FetchWithRateLimitRetryAsync(HttpMethod.Post, $"{baseUrl}/models/{id}/policy", "{\"state\":\"enabled\"}", headers, token, 2, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception) when (!token.IsCancellationRequested) { continue; }
            if (response.Status == 429) break;
            if (response.Ok) enabled.Add(id);
        }
        return enabled;
    }

    /// <summary>fetchWithRateLimitRetry: 429 is retried (retry-after, else 500·2^n ms) within the elapsed budget; 5 s per attempt.</summary>
    private async Task<OAuthFlows.HttpResult> FetchWithRateLimitRetryAsync(HttpMethod method, string url, string? body, List<KeyValuePair<string, string>> headers,
        CancellationToken token, int maxRetries, TimeSpan maxElapsed)
    {
        var deadline = maxRetries > 0 && maxElapsed > TimeSpan.Zero ? _time.GetUtcNow() + maxElapsed : (DateTimeOffset?)null;
        for (var retry = 0; ; retry++)
        {
            var response = await OAuthFlows.SendAsync(http, method, url, token, body, body is null ? null : "application/json", headers, TimeSpan.FromSeconds(5), _time).ConfigureAwait(false);
            if (response.Status != 429 || retry == maxRetries) return response;
            var wait = 500 * Math.Pow(2, retry);
            if (response.Header("retry-after") is { } retryAfter)
            {
                if (double.TryParse(retryAfter, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) wait = seconds * 1000;
                else if (DateTimeOffset.TryParse(retryAfter, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) wait = (at - _time.GetUtcNow()).TotalMilliseconds;
                else return response;
            }
            wait = Math.Max(0, wait);
            if (deadline is { } end && wait >= (end - _time.GetUtcNow()).TotalMilliseconds) return response;
            await _delay(TimeSpan.FromMilliseconds(Math.Max(0, wait)), token).ConfigureAwait(false);
        }
    }

    private async Task<JsonNode?> FetchJsonAsync(HttpMethod method, string url, CancellationToken token, string? form, List<KeyValuePair<string, string>> headers)
    {
        var response = await OAuthFlows.SendAsync(http, method, url, token, form, form is null ? null : "application/x-www-form-urlencoded", headers, time: _time).ConfigureAwait(false);
        if (!response.Ok) throw new InvalidOperationException($"{response.Status} {response.StatusText}: {response.Body}");
        try { return JsonNode.Parse(response.Body); } catch (JsonException) { throw new InvalidOperationException("Invalid JSON response"); }
    }
}
