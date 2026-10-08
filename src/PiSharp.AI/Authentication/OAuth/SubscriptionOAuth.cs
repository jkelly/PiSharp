// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/auth/oauth/openrouter.ts, kimi-coding.ts, meta.ts, xai.ts,
// radius.ts (with providers/radius-config.ts normalizeRadiusGatewayUrl) and anthropic.ts (adapted to the shared interaction).
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;

namespace PiSharp.AI.Authentication.OAuth;

/// <summary>OpenRouter OAuth PKCE: the code is exchanged for a permanent API key (refresh is the identity).</summary>
public sealed class OpenRouterOAuth(HttpMessageInvoker http, Func<string, string?> environment, TimeProvider? time = null,
    string authorizeUrl = "https://openrouter.ai/auth", string tokenUrl = "https://openrouter.ai/api/v1/auth/keys") : IProviderOAuth
{
    public string Name => "OpenRouter OAuth";
    public bool IsSubscription => false;
    public string? LoginLabel => "Sign in with OpenRouter";

    public async Task<OAuthCredentialSnapshot> LoginAsync(IProviderAuthInteraction interaction, ProviderLoginOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        var (verifier, challenge) = OAuthFlows.GeneratePkce();
        // OpenRouter sends no state; the random path keeps stray requests from completing the sign-in.
        var callback = OAuthLoopbackServer<OAuthCredentialSnapshot>.Start("OpenRouter", OAuthFlows.CallbackHost(environment), 0, "/oauth/callback/" + Guid.NewGuid(), null,
            (code, _) => ExchangeAsync(code, verifier, cancellationToken), cancellationToken, timeout: TimeSpan.FromMinutes(5), time: time);
        try
        {
            var url = authorizeUrl + "?" + OAuthFlows.Form(("callback_url", callback.RedirectUri), ("code_challenge", challenge), ("code_challenge_method", "S256"));
            interaction.Notify(new(AuthEventKind.Progress) { Message = $"Listening for OpenRouter OAuth callback on {callback.RedirectUri}" });
            interaction.Notify(new(AuthEventKind.AuthUrl) { Url = url, Instructions = "Complete sign-in in your browser. If the browser is on another machine, paste the final redirect URL here." });
            var result = await OAuthFlows.WaitForCallbackOrManualInputAsync(interaction, callback,
                "Complete sign-in in your browser, or paste the authorization code / redirect URL here:", callback.RedirectUri, cancellationToken).ConfigureAwait(false);
            if (result.Callback) return result.Value!;
            var code = ParseAuthorizationInput(result.Input!);
            if (string.IsNullOrEmpty(code)) throw new InvalidOperationException("Missing authorization code");
            interaction.Notify(new(AuthEventKind.Progress) { Message = "Exchanging authorization code for an API key..." });
            return await ExchangeAsync(code, verifier, cancellationToken).ConfigureAwait(false);
        }
        finally { callback.Close(); }
    }

    public static string? ParseAuthorizationInput(string input)
    {
        var value = (input ?? "").Trim();
        if (value.Length == 0) return null;
        if (OAuthFlows.TryUrlQuery(value, out var query)) return OAuthFlows.Query(query, "code");
        if (value.Contains("code=", StringComparison.Ordinal)) return OAuthFlows.Query(value, "code");
        return value;
    }

    private async Task<OAuthCredentialSnapshot> ExchangeAsync(string code, string verifier, CancellationToken token)
    {
        if (token.IsCancellationRequested) throw OAuthFlows.Cancelled(token);
        OAuthFlows.HttpResult response;
        try
        {
            response = await OAuthFlows.SendAsync(http, HttpMethod.Post, tokenUrl, token,
                new JsonObject { ["code"] = code, ["code_verifier"] = verifier, ["code_challenge_method"] = "S256" }.ToJsonString(), "application/json",
                [new("accept", "application/json")], TimeSpan.FromSeconds(30), time).ConfigureAwait(false);
        }
        catch (TimeoutException) { throw new InvalidOperationException("OpenRouter OAuth token exchange timed out"); }
        JsonObject body;
        try { body = JsonNode.Parse(response.Body) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { if (response.Ok) throw new InvalidOperationException("OpenRouter OAuth returned invalid JSON"); body = new JsonObject(); }
        if (!response.Ok)
        {
            var detail = OAuthFlows.OptionalText(body, "error_description") ?? OAuthFlows.OptionalText(body, "message") ?? OAuthFlows.OptionalText(body, "error")
                ?? (body["error"] as JsonObject)?["message"]?.GetValue<string>();
            throw new InvalidOperationException($"OpenRouter OAuth key exchange failed (HTTP {response.Status}){(detail is null ? "" : ": " + detail)}");
        }
        if (OAuthFlows.OptionalText(body, "key") is not { Length: > 0 } key) throw new InvalidOperationException("OpenRouter OAuth response carries no \"key\"");
        return new(key, "", 9007199254740991L);
    }

    public Task<OAuthCredentialSnapshot> RefreshAsync(string provider, OAuthCredentialSnapshot current, CancellationToken cancellationToken) => Task.FromResult(current);
    public ProviderModelAuth ToAuth(OAuthCredentialSnapshot credential) => new(credential.Access);
}

/// <summary>Kimi Code (subscription): RFC 8628 device grant against auth.kimi.com; Bearer header auth.</summary>
public sealed class KimiCodingOAuth(HttpMessageInvoker http, Func<string, string?> environment, TimeProvider? time = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IProviderOAuth
{
    public const string ClientId = "17e5f671-d194-4dfb-9706-5516cb48c098";
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    public string Name => "Kimi Code (subscription)";
    public bool IsSubscription => true;
    public string? LoginLabel => "Sign in with Kimi Code";
    private string Host => ((environment("KIMI_CODE_OAUTH_HOST") is { Length: > 0 } code ? code : environment("KIMI_OAUTH_HOST") is { Length: > 0 } kimi ? kimi : null) ?? "https://auth.kimi.com").TrimEnd('/');

    private Task<OAuthFlows.HttpResult> PostAsync(string path, string form, CancellationToken token, bool login = true) => OAuthFlows.SendAsync(http, HttpMethod.Post,
        Host + path, token, form, "application/x-www-form-urlencoded", [new("Accept", "application/json")], TimeSpan.FromSeconds(30), _time, login);

    public async Task<OAuthCredentialSnapshot> LoginAsync(IProviderAuthInteraction interaction, ProviderLoginOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        var started = await PostAsync("/api/oauth/device_authorization", OAuthFlows.Form(("client_id", ClientId)), cancellationToken).ConfigureAwait(false);
        if (!started.Ok) throw new InvalidOperationException($"Kimi Code device authorization failed with status {started.Status}{(started.Body.Length != 0 ? ": " + started.Body : "")}");
        var json = started.Json();
        var deviceCode = OAuthFlows.OptionalText(json, "device_code"); var userCode = OAuthFlows.OptionalText(json, "user_code");
        var verification = OAuthFlows.OptionalText(json, "verification_uri"); var complete = OAuthFlows.OptionalText(json, "verification_uri_complete");
        if (deviceCode is null || userCode is null || verification is null || complete is null || OAuthFlows.TrustedHttpUrl(complete) is not { } trusted || OAuthFlows.TrustedHttpUrl(verification) is null)
            throw new InvalidOperationException("Invalid Kimi Code device authorization response: " + OAuthFlows.ProjectJson(started.Body));
        var interval = OAuthFlows.Number(json, "interval") is { } i && i > 0 ? i : 5;
        var expires = OAuthFlows.Number(json, "expires_in") is { } e && e > 0 ? e : 15 * 60;
        interaction.Notify(new(AuthEventKind.DeviceCode) { UserCode = userCode, VerificationUri = trusted, IntervalSeconds = interval, ExpiresInSeconds = expires });
        return await OAuthFlows.PollDeviceCodeAsync<OAuthCredentialSnapshot>(async token =>
        {
            var response = await PostAsync("/api/oauth/token", OAuthFlows.Form(("client_id", ClientId), ("device_code", deviceCode),
                ("grant_type", "urn:ietf:params:oauth:grant-type:device_code")), token).ConfigureAwait(false);
            if (response.Status >= 500)
                return new(OAuthFlows.DevicePollStatus.Failed, Message: $"Kimi Code device token request failed with status {response.Status}{(response.Body.Length != 0 ? ": " + response.Body : "")}");
            var data = response.Json();
            if (response.Ok && data?["access_token"] is JsonValue access && access.TryGetValue<string>(out _))
            {
                try { return new(OAuthFlows.DevicePollStatus.Complete, Token(data, response.Body, "poll")); }
                catch (InvalidOperationException invalid) { return new(OAuthFlows.DevicePollStatus.Failed, Message: invalid.Message); }
            }
            var error = OAuthFlows.OptionalText(data, "error");
            var description = OAuthFlows.OptionalText(data, "error_description") is { } text ? ": " + text : "";
            return error switch
            {
                "authorization_pending" => new(OAuthFlows.DevicePollStatus.Pending),
                "slow_down" => new(OAuthFlows.DevicePollStatus.SlowDown, IntervalSeconds: OAuthFlows.Number(data, "interval") is { } slow && slow > 0 ? slow : null),
                "expired_token" => new(OAuthFlows.DevicePollStatus.Failed, Message: "Kimi Code device authorization expired. Please restart login."),
                "access_denied" => new(OAuthFlows.DevicePollStatus.Failed, Message: "Kimi Code login was denied."),
                _ => new(OAuthFlows.DevicePollStatus.Failed, Message: $"Kimi Code device token request failed (status {response.Status}){(error is null ? "" : $": {error}{description}")}")
            };
        }, interval, expires, true, cancellationToken, _time).ConfigureAwait(false);
    }

    private OAuthCredentialSnapshot Token(JsonObject? json, string raw, string operation)
    {
        if (OAuthFlows.OptionalText(json, "access_token") is not { Length: > 0 } access || OAuthFlows.OptionalText(json, "refresh_token") is not { Length: > 0 } refresh ||
            OAuthFlows.Number(json, "expires_in") is not { } expiresIn || expiresIn <= 0)
            throw new InvalidOperationException($"Kimi Code token {operation} response missing fields: {OAuthFlows.ProjectJson(raw)}");
        return new(access, refresh, (long)(_time.GetUtcNow().ToUnixTimeMilliseconds() + expiresIn * 1000));
    }

    public async Task<OAuthCredentialSnapshot> RefreshAsync(string provider, OAuthCredentialSnapshot current, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 0; attempt <= 3; attempt++)
        {
            if (attempt > 0) await (delay ?? ((wait, token) => Task.Delay(wait, _time, token)))(TimeSpan.FromMilliseconds(1000 * Math.Pow(2, attempt - 1)), cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) throw new InvalidOperationException("Kimi Code token refresh aborted");
            OAuthFlows.HttpResult response;
            try
            {
                response = await PostAsync("/api/oauth/token", OAuthFlows.Form(("client_id", ClientId), ("grant_type", "refresh_token"), ("refresh_token", current.Refresh)),
                    cancellationToken, login: false).ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException or IOException or TimeoutException) { last = error; continue; }
            var json = response.Json();
            if (response.Ok) return Token(json, response.Body, "refresh");
            if (response.Status is 401 or 403 || OAuthFlows.OptionalText(json, "error") == "invalid_grant")
                throw new InvalidOperationException($"Kimi Code token refresh unauthorized (status {response.Status}){(OAuthFlows.OptionalText(json, "error_description") is { } d ? ": " + d : "")}");
            if ((response.Status == 429 || response.Status >= 500) && attempt < 3) { last = new InvalidOperationException($"Kimi Code token refresh failed with status {response.Status}"); continue; }
            throw new InvalidOperationException($"Kimi Code token refresh failed with status {response.Status}: {(json is null ? "null" : OAuthFlows.ProjectJson(response.Body))}");
        }
        throw last ?? new InvalidOperationException("Kimi Code token refresh failed");
    }

    /// <summary>toAuth: Authorization: Bearer header auth (not an apiKey).</summary>
    public ProviderModelAuth ToAuth(OAuthCredentialSnapshot credential) =>
        new(null, ImmutableDictionary<string, string?>.Empty.Add("Authorization", "Bearer " + credential.Access));
}

/// <summary>Meta (Muse subscription): device grant for an identity token (stored as refresh) minted into a day-long API key.</summary>
public sealed class MetaOAuth(HttpMessageInvoker http, TimeProvider? time = null) : IProviderOAuth
{
    public const string ClientId = "1031625952748946";
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    public string Name => "Meta (Muse subscription)";
    public bool IsSubscription => true;
    public string? LoginLabel => "Sign in with Meta";

    private static string Detail(JsonObject? json)
    {
        foreach (var key in new[] { "error_description", "detail", "message", "error" })
            if (OAuthFlows.OptionalText(json, key) is { } value && value.Trim().Length != 0) return ": " + value.Trim();
        return "";
    }

    public async Task<OAuthCredentialSnapshot> LoginAsync(IProviderAuthInteraction interaction, ProviderLoginOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        try
        {
            var started = await OAuthFlows.SendAsync(http, HttpMethod.Post, "https://auth.meta.com/oidc/device/authorization/", cancellationToken,
                OAuthFlows.Form(("client_id", ClientId)), "application/x-www-form-urlencoded", [new("Accept", "application/json")], TimeSpan.FromSeconds(30), _time).ConfigureAwait(false);
            var json = started.Json();
            if (!started.Ok) throw new InvalidOperationException($"Meta device authorization failed with status {started.Status}{Detail(json)}");
            var deviceCode = OAuthFlows.OptionalText(json, "device_code"); var userCode = OAuthFlows.OptionalText(json, "user_code");
            var verification = OAuthFlows.TrustedHttpUrl(OAuthFlows.OptionalText(json, "verification_uri_complete")) ?? OAuthFlows.TrustedHttpUrl(OAuthFlows.OptionalText(json, "verification_uri"));
            if (string.IsNullOrEmpty(deviceCode) || string.IsNullOrEmpty(userCode) || verification is null)
                throw new InvalidOperationException("Invalid Meta device authorization response: " + OAuthFlows.ProjectJson(started.Body));
            var interval = OAuthFlows.Number(json, "interval") is { } i && i > 0 ? i : (double?)null;
            var expires = OAuthFlows.Number(json, "expires_in") is { } e && e > 0 ? e : (double?)null;
            interaction.Notify(new(AuthEventKind.DeviceCode) { UserCode = userCode, VerificationUri = verification, IntervalSeconds = interval, ExpiresInSeconds = expires });
            var identity = await OAuthFlows.PollDeviceCodeAsync<string>(async token =>
            {
                var response = await OAuthFlows.SendAsync(http, HttpMethod.Post, "https://auth.meta.com/oidc/device/token/", token,
                    OAuthFlows.Form(("grant_type", "urn:ietf:params:oauth:grant-type:device_code"), ("device_code", deviceCode), ("client_id", ClientId)),
                    "application/x-www-form-urlencoded", [new("Accept", "application/json")], TimeSpan.FromSeconds(30), _time).ConfigureAwait(false);
                var data = response.Json();
                if (response.Ok && OAuthFlows.OptionalText(data, "access_token") is { Length: > 0 } accessToken) return new(OAuthFlows.DevicePollStatus.Complete, accessToken);
                return OAuthFlows.OptionalText(data, "error") switch
                {
                    "authorization_pending" => new(OAuthFlows.DevicePollStatus.Pending),
                    "slow_down" => new(OAuthFlows.DevicePollStatus.SlowDown, IntervalSeconds: OAuthFlows.Number(data, "interval") is { } slow && slow > 0 ? slow : null),
                    "access_denied" => new(OAuthFlows.DevicePollStatus.Failed, Message: "Meta login was denied."),
                    "expired_token" => new(OAuthFlows.DevicePollStatus.Failed, Message: "Meta device authorization expired. Please restart login."),
                    _ => new(OAuthFlows.DevicePollStatus.Failed, Message: $"Meta device token request failed with status {response.Status}{Detail(data)}")
                };
            }, interval, expires, true, cancellationToken, _time).ConfigureAwait(false);
            interaction.Notify(new(AuthEventKind.Progress) { Message = "Enabling Meta Model API access..." });
            return await MintAsync(identity, cancellationToken, login: true).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { throw OAuthFlows.Cancelled(cancellationToken); }
    }

    /// <summary>mintApiKey: the identity token traded for a Model API key living about a day.</summary>
    private async Task<OAuthCredentialSnapshot> MintAsync(string identity, CancellationToken token, bool login)
    {
        var response = await OAuthFlows.SendAsync(http, HttpMethod.Post, "https://api.meta.ai/muse-code/key", token, "{}", "application/json",
            [new("Accept", "application/json"), new("Authorization", "Bearer " + identity), new("x-api-version", "1.0.0")], TimeSpan.FromSeconds(30), _time, login).ConfigureAwait(false);
        var json = response.Json();
        if (response.Status is 401 or 403)
            throw new InvalidOperationException($"Meta session expired (status {response.Status}). Run `/login meta` to sign in again.{Detail(json)}");
        if (!response.Ok) throw new InvalidOperationException($"Meta API key mint failed with status {response.Status}{Detail(json)}");
        if (OAuthFlows.OptionalText(json, "api_key") is not { Length: > 0 } key)
        {
            var action = OAuthFlows.TrustedHttpUrl(OAuthFlows.OptionalText(json, "action_url"));
            throw new InvalidOperationException($"Meta did not issue an API key.{(action is null ? "" : $" Complete setup at {action}")}");
        }
        return new(key, identity, _time.GetUtcNow().ToUnixTimeMilliseconds() + 24L * 60 * 60 * 1000);
    }

    public Task<OAuthCredentialSnapshot> RefreshAsync(string provider, OAuthCredentialSnapshot current, CancellationToken cancellationToken) =>
        MintAsync(current.Refresh, cancellationToken, login: false);
    public ProviderModelAuth ToAuth(OAuthCredentialSnapshot credential) => new(credential.Access);
}

/// <summary>xAI (Grok/X subscription): device grant against auth.x.ai.</summary>
public sealed class XaiOAuth(HttpMessageInvoker http, TimeProvider? time = null) : IProviderOAuth
{
    public const string ClientId = "b1a00492-073a-47ea-816f-4c329264a828";
    public const string Scope = "openid profile email offline_access grok-cli:access api:access";
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    public string Name => "xAI (Grok/X subscription)";
    public bool IsSubscription => true;
    public string? LoginLabel => "Sign in with SuperGrok or X Premium";

    private async Task<(OAuthFlows.HttpResult Response, JsonObject Body)> PostAsync(string url, string form, CancellationToken token, bool login = true)
    {
        var response = await OAuthFlows.SendAsync(http, HttpMethod.Post, url, token, form, "application/x-www-form-urlencoded", [new("Accept", "application/json")], time: _time, loginCancellation: login).ConfigureAwait(false);
        JsonObject body;
        try { body = JsonNode.Parse(response.Body) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { throw new InvalidOperationException($"xAI OAuth returned invalid JSON (HTTP {response.Status})"); }
        return (response, body);
    }

    private static Exception Failure(string action, OAuthFlows.HttpResult response, JsonObject body)
    {
        var detail = string.Join(": ", new[] { OAuthFlows.OptionalText(body, "error"), OAuthFlows.OptionalText(body, "error_description") }.Where(value => !string.IsNullOrEmpty(value)));
        return new InvalidOperationException($"xAI OAuth {action} failed (HTTP {response.Status}){(detail.Length != 0 ? ": " + detail : "")}");
    }

    private static string Required(JsonObject body, string field) =>
        OAuthFlows.OptionalText(body, field) is { Length: > 0 } value ? value : throw new InvalidOperationException($"Invalid xAI OAuth response field: {field}");
    private static double Positive(JsonObject body, string field) =>
        OAuthFlows.Number(body, field) is { } value && value > 0 ? value : throw new InvalidOperationException($"Invalid xAI OAuth response field: {field}");
    private static string Verification(string raw) =>
        OAuthFlows.TrustedHttpUrl(raw, httpsOnly: true) ?? throw new InvalidOperationException("Untrusted verification URI in xAI OAuth response");

    public async Task<OAuthCredentialSnapshot> LoginAsync(IProviderAuthInteraction interaction, ProviderLoginOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        var (started, body) = await PostAsync("https://auth.x.ai/oauth2/device/code", OAuthFlows.Form(("client_id", ClientId), ("scope", Scope), ("referrer", "pi")), cancellationToken).ConfigureAwait(false);
        if (!started.Ok) throw Failure("device authorization", started, body);
        var interval = OAuthFlows.Number(body, "interval") is { } value && value > 0 ? value : (double?)null;
        var complete = OAuthFlows.OptionalText(body, "verification_uri_complete") is { Length: > 0 } full ? Verification(full) : null;
        var deviceCode = Required(body, "device_code"); var userCode = Required(body, "user_code");
        var verification = Verification(Required(body, "verification_uri")); var expires = Positive(body, "expires_in");
        interaction.Notify(new(AuthEventKind.DeviceCode) { UserCode = userCode, VerificationUri = complete ?? verification, IntervalSeconds = interval, ExpiresInSeconds = expires });
        return await OAuthFlows.PollDeviceCodeAsync<OAuthCredentialSnapshot>(async token =>
        {
            var (response, data) = await PostAsync("https://auth.x.ai/oauth2/token", OAuthFlows.Form(("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
                ("client_id", ClientId), ("device_code", deviceCode)), token).ConfigureAwait(false);
            if (response.Ok) return new(OAuthFlows.DevicePollStatus.Complete, Credential(data, null));
            return OAuthFlows.OptionalText(data, "error") switch
            {
                "authorization_pending" => new(OAuthFlows.DevicePollStatus.Pending),
                "slow_down" => new(OAuthFlows.DevicePollStatus.SlowDown, IntervalSeconds: OAuthFlows.Number(data, "interval")),
                "access_denied" or "authorization_denied" => new(OAuthFlows.DevicePollStatus.Failed, Message: "xAI device authorization was denied"),
                "expired_token" => new(OAuthFlows.DevicePollStatus.Failed, Message: "xAI device code expired"),
                _ => new(OAuthFlows.DevicePollStatus.Failed, Message: Failure("device token polling", response, data).Message)
            };
        }, interval, expires, true, cancellationToken, _time).ConfigureAwait(false);
    }

    private OAuthCredentialSnapshot Credential(JsonObject body, string? previousRefresh)
    {
        var access = Required(body, "access_token");
        // xAI may omit refresh_token on refresh when it does not rotate the token.
        var refresh = !body.ContainsKey("refresh_token") && previousRefresh is { Length: > 0 } ? previousRefresh : Required(body, "refresh_token");
        var expiresIn = !body.ContainsKey("expires_in") ? 3600 : Positive(body, "expires_in");
        return new(access, refresh, (long)(_time.GetUtcNow().ToUnixTimeMilliseconds() + expiresIn * 1000 - 5 * 60 * 1000));
    }

    public async Task<OAuthCredentialSnapshot> RefreshAsync(string provider, OAuthCredentialSnapshot current, CancellationToken cancellationToken)
    {
        var (response, body) = await PostAsync("https://auth.x.ai/oauth2/token", OAuthFlows.Form(("grant_type", "refresh_token"), ("client_id", ClientId),
            ("refresh_token", current.Refresh)), cancellationToken, login: false).ConfigureAwait(false);
        if (!response.Ok) throw Failure("token refresh", response, body);
        return Credential(body, current.Refresh);
    }
    public ProviderModelAuth ToAuth(OAuthCredentialSnapshot credential) => new(credential.Access);
}

/// <summary>Radius gateway OAuth: browser (discovered authorization endpoint, fixed loopback port 1456) or device code.</summary>
public sealed class RadiusOAuth(HttpMessageInvoker http, string name, string gateway, TimeProvider? time = null, int callbackPort = 1456) : IProviderOAuth
{
    public const string DefaultGateway = "https://radius.pi.dev";
    public const string ClientId = "pi-gateway";
    public const string Scope = "gateway offline_access";
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly string _gateway = NormalizeGateway(gateway);
    private string RedirectUri => $"http://127.0.0.1:{callbackPort}/oauth/callback";
    public string Name => name;
    public bool IsSubscription => false;
    public string? LoginLabel => null;

    /// <summary>normalizeRadiusGatewayUrl: https:// unless a scheme is present, trailing slashes removed.</summary>
    public static string NormalizeGateway(string value)
    {
        var withScheme = System.Text.RegularExpressions.Regex.IsMatch(value, "^https?://", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? value : "https://" + value;
        return withScheme.TrimEnd('/');
    }

    private string Url(string path) => new Uri(new Uri(_gateway + "/"), path).AbsoluteUri;

    public async Task<OAuthCredentialSnapshot> LoginAsync(IProviderAuthInteraction interaction, ProviderLoginOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        var method = await interaction.PromptAsync(new(AuthPromptKind.Select, $"Sign in to {name}:", Options:
            [new("browser", "Sign in with browser (recommended)"), new("device-code", "Sign in with device code (when signing in from another device)")]), cancellationToken).ConfigureAwait(false);
        if (method == "device-code") return await LoginDeviceAsync(interaction, cancellationToken).ConfigureAwait(false);
        if (method != "browser") throw new InvalidOperationException($"Unknown {name} sign-in method: {method}");
        var discovery = await OAuthFlows.SendAsync(http, HttpMethod.Get, Url("/v1/oauth"), cancellationToken, headers: [new("accept", "application/json")], time: _time).ConfigureAwait(false);
        if (!discovery.Ok) throw new InvalidOperationException($"Could not load Radius OAuth config from {_gateway}: {discovery.Status} {discovery.Body}");
        if (OAuthFlows.OptionalText(discovery.Json(), "authorizationEndpoint") is not { } endpoint) throw new InvalidOperationException($"Invalid Radius OAuth config from {_gateway}");
        var (verifier, challenge) = OAuthFlows.GeneratePkce();
        var state = Guid.NewGuid().ToString();
        var url = new UriBuilder(endpoint) { Query = OAuthFlows.Form(("response_type", "code"), ("client_id", ClientId), ("redirect_uri", RedirectUri),
            ("scope", Scope), ("code_challenge", challenge), ("code_challenge_method", "S256"), ("handoff", "url"), ("state", state)) }.Uri.AbsoluteUri;
        var callback = OAuthLoopbackServer<OAuthCredentialSnapshot>.Start("Radius", "127.0.0.1", callbackPort, "/oauth/callback", state,
            (code, _) => TokenAsync(OAuthFlows.Form(("grant_type", "authorization_code"), ("client_id", ClientId), ("redirect_uri", RedirectUri),
                ("code", code), ("code_verifier", verifier)), cancellationToken), cancellationToken);
        interaction.Notify(new(AuthEventKind.Progress) { Message = $"Listening for OAuth callback on {RedirectUri}" });
        interaction.Notify(new(AuthEventKind.AuthUrl) { Url = url, Instructions = "Continue in your browser." });
        try
        {
            var (settled, credential) = await callback.WaitAsync().ConfigureAwait(false);
            return settled && credential is not null ? credential : throw new InvalidOperationException("OAuth callback did not complete.");
        }
        finally { callback.Close(); }
    }

    private sealed class OAuthResponseException(int status, string? error, string? description, string message)
        : Exception($"{message}: {(error is not null ? description is not null ? $"{error}: {description}" : error : description ?? status.ToString(System.Globalization.CultureInfo.InvariantCulture))}")
    { public string? OAuthError { get; } = error; }

    private static OAuthResponseException ResponseError(OAuthFlows.HttpResult response, string message)
    {
        string? error = null, description = null;
        if (response.Body.Length != 0)
        {
            try { var data = JsonNode.Parse(response.Body) as JsonObject; error = OAuthFlows.OptionalText(data, "error"); description = OAuthFlows.OptionalText(data, "error_description"); }
            catch (JsonException) { description = response.Body; }
        }
        return new(response.Status, error, description, message);
    }

    private async Task<OAuthCredentialSnapshot> TokenAsync(string form, CancellationToken token, bool login = true)
    {
        var response = await OAuthFlows.SendAsync(http, HttpMethod.Post, Url("/v1/oauth/token"), token, form, "application/x-www-form-urlencoded",
            [new("accept", "application/json")], time: _time, loginCancellation: login).ConfigureAwait(false);
        if (!response.Ok) throw ResponseError(response, "Radius OAuth token request failed");
        var data = response.Json() ?? new JsonObject();
        var extra = new Dictionary<string, string>();
        if (OAuthFlows.OptionalText(data, "scope") is { } scope) extra["scope"] = scope;
        return new(OAuthFlows.Text(data, "access_token"), OAuthFlows.Text(data, "refresh_token"),
            (long)(_time.GetUtcNow().ToUnixTimeMilliseconds() + (OAuthFlows.Number(data, "expires_in") ?? 0) * 1000 - 60_000), extra);
    }

    private async Task<OAuthCredentialSnapshot> LoginDeviceAsync(IProviderAuthInteraction interaction, CancellationToken token)
    {
        var started = await OAuthFlows.SendAsync(http, HttpMethod.Post, Url("/v1/oauth/device"), token, OAuthFlows.Form(("client_id", ClientId), ("scope", Scope)),
            "application/x-www-form-urlencoded", [new("accept", "application/json")], time: _time).ConfigureAwait(false);
        if (!started.Ok) throw ResponseError(started, "Radius OAuth device authorization failed");
        var data = started.Json();
        var deviceCode = OAuthFlows.OptionalText(data, "device_code"); var userCode = OAuthFlows.OptionalText(data, "user_code");
        var verification = OAuthFlows.OptionalText(data, "verification_uri"); var expires = OAuthFlows.Number(data, "expires_in");
        if (string.IsNullOrEmpty(deviceCode) || string.IsNullOrEmpty(userCode) || string.IsNullOrEmpty(verification) || expires is null or 0)
            throw new InvalidOperationException("Radius OAuth device authorization response is missing required fields");
        var interval = OAuthFlows.Number(data, "interval");
        interaction.Notify(new(AuthEventKind.DeviceCode) { UserCode = userCode, VerificationUri = verification, IntervalSeconds = interval, ExpiresInSeconds = expires });
        return await OAuthFlows.PollDeviceCodeAsync<OAuthCredentialSnapshot>(async pollToken =>
        {
            try
            {
                return new(OAuthFlows.DevicePollStatus.Complete, await TokenAsync(OAuthFlows.Form(("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
                    ("client_id", ClientId), ("device_code", deviceCode)), pollToken).ConfigureAwait(false));
            }
            catch (OAuthResponseException error) when (error.OAuthError is "authorization_pending" or "slow_down" or "expired_token" or "access_denied")
            {
                return error.OAuthError switch
                {
                    "authorization_pending" => new(OAuthFlows.DevicePollStatus.Pending),
                    "slow_down" => new(OAuthFlows.DevicePollStatus.SlowDown),
                    "expired_token" => new(OAuthFlows.DevicePollStatus.Failed, Message: "Device authorization expired."),
                    _ => new(OAuthFlows.DevicePollStatus.Failed, Message: "Device authorization was denied.")
                };
            }
        }, interval, expires, false, token, _time).ConfigureAwait(false);
    }

    public Task<OAuthCredentialSnapshot> RefreshAsync(string provider, OAuthCredentialSnapshot current, CancellationToken cancellationToken) =>
        TokenAsync(OAuthFlows.Form(("grant_type", "refresh_token"), ("client_id", ClientId), ("refresh_token", current.Refresh)), cancellationToken, login: false);
    public ProviderModelAuth ToAuth(OAuthCredentialSnapshot credential) => new(credential.Access);
}

/// <summary>Anthropic (Claude Pro/Max) OAuth over the shared interaction: select, manual-code prompts and auth-url/progress events.</summary>
public sealed class AnthropicProviderOAuth(Func<AnthropicOAuth> create) : IProviderOAuth
{
    public string Name => "Anthropic (Claude Pro/Max)";
    public bool IsSubscription => true;
    public string? LoginLabel => null;

    public Task<OAuthCredentialSnapshot> LoginAsync(IProviderAuthInteraction interaction, ProviderLoginOptions? options, CancellationToken cancellationToken) =>
        create().LoginAsync(new Adapter(interaction), cancellationToken);
    public Task<OAuthCredentialSnapshot> RefreshAsync(string provider, OAuthCredentialSnapshot current, CancellationToken cancellationToken) =>
        create().RefreshAsync(provider, current, cancellationToken);
    public ProviderModelAuth ToAuth(OAuthCredentialSnapshot credential) => new(credential.Access);

    private sealed class Adapter(IProviderAuthInteraction interaction) : IAnthropicOAuthLoginInteraction
    {
        public void Notify(AnthropicOAuthLoginEvent loginEvent) => interaction.Notify(loginEvent.Kind == AnthropicOAuthLoginEventKind.AuthUrl
            ? new AuthEvent(AuthEventKind.AuthUrl) { Url = loginEvent.Url, Instructions = loginEvent.Instructions }
            : new AuthEvent(AuthEventKind.Progress) { Message = loginEvent.Message });
        public Task<string> SelectAsync(string message, IReadOnlyList<AnthropicOAuthLoginOption> options, CancellationToken cancellationToken) =>
            interaction.PromptAsync(new(AuthPromptKind.Select, message, Options: [.. options.Select(option => new AuthPromptOption(option.Id, option.Label))]), cancellationToken);
        public Task<string> PromptManualCodeAsync(string message, string placeholder, CancellationToken cancellationToken) =>
            interaction.PromptAsync(new(AuthPromptKind.ManualCode, message, placeholder), cancellationToken);
    }
}
