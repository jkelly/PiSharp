// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/auth/oauth/openai-codex.ts (ChatGPT Plus/Pro OAuth for the
// Codex backend: browser and device-code login, token exchange and refresh, account id claim) and auth/oauth/openai-chatgpt.ts
// (Sign in with ChatGPT for the OpenAI Responses API: dynamic client registration, agent host id, direct-token scope).
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Contracts;

namespace PiSharp.AI.Authentication.OAuth;

/// <summary>OpenAI (ChatGPT Plus/Pro) OAuth for provider openai-codex.</summary>
public sealed class OpenAICodexOAuth(HttpMessageInvoker http, Func<string, string?> environment, TimeProvider? time = null,
    string authBaseUrl = "https://auth.openai.com", int callbackPort = 1455) : IProviderOAuth
{
    public const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    public const string RedirectUri = "http://localhost:1455/auth/callback";
    public const string Scope = "openid profile email offline_access";
    private const int DeviceCodeTimeoutSeconds = 15 * 60;
    private const string JwtClaimPath = "https://api.openai.com/auth";
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private string AuthorizeUrl => authBaseUrl + "/oauth/authorize";
    private string TokenUrl => authBaseUrl + "/oauth/token";
    private string DeviceUserCodeUrl => authBaseUrl + "/api/accounts/deviceauth/usercode";
    private string DeviceTokenUrl => authBaseUrl + "/api/accounts/deviceauth/token";
    private string DeviceVerificationUri => authBaseUrl + "/codex/device";
    private string DeviceRedirectUri => authBaseUrl + "/deviceauth/callback";

    public string Name => "OpenAI (ChatGPT Plus/Pro)";
    public bool IsSubscription => true;
    public string? LoginLabel => null;

    public async Task<OAuthCredentialSnapshot> LoginAsync(IProviderAuthInteraction interaction, ProviderLoginOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        var method = await interaction.PromptAsync(new(AuthPromptKind.Select, "Select OpenAI Codex login method:", Options:
            [new("browser", "Browser login (default)"), new("device_code", "Device code login (headless)")]), cancellationToken).ConfigureAwait(false);
        if (method == "device_code") return await LoginDeviceCodeAsync(interaction, cancellationToken).ConfigureAwait(false);
        if (method != "browser") throw new InvalidOperationException($"Unknown OpenAI Codex login method: {method}");
        return await LoginBrowserAsync(interaction, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>createAuthorizationFlow: the authorize URL with PKCE, a random hex state and the originator.</summary>
    public string BuildAuthorizationUrl(string challenge, string state, string originator = "pi") => AuthorizeUrl + "?" + OAuthFlows.Form(
        ("response_type", "code"), ("client_id", ClientId), ("redirect_uri", RedirectUri), ("scope", Scope), ("code_challenge", challenge),
        ("code_challenge_method", "S256"), ("state", state), ("id_token_add_organizations", "true"), ("codex_cli_simplified_flow", "true"),
        ("originator", originator));

    private async Task<OAuthCredentialSnapshot> LoginBrowserAsync(IProviderAuthInteraction interaction, ProviderLoginOptions? options, CancellationToken token)
    {
        var (verifier, challenge) = OAuthFlows.GeneratePkce();
        var state = OAuthFlows.RandomHex(16);
        var url = BuildAuthorizationUrl(challenge, state, options?.AgentName ?? "pi");
        // Port 1455 is shared with the Codex CLI; when it is taken, the pasted redirect URL completes the login.
        OAuthLoopbackServer<string>? callback = null;
        try { callback = OAuthLoopbackServer<string>.Start("OpenAI", OAuthFlows.CallbackHost(environment), callbackPort, "/auth/callback", state, (code, _) => Task.FromResult(code), token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { callback = null; }
        interaction.Notify(new(AuthEventKind.AuthUrl) { Url = url, Instructions = "A browser window should open. Complete login to finish." });
        try
        {
            var result = await OAuthFlows.WaitForCallbackOrManualInputAsync(interaction, callback,
                "Complete login in your browser, or paste the authorization code / redirect URL here:", RedirectUri, token).ConfigureAwait(false);
            string? code;
            if (result.Callback) code = result.Value;
            else
            {
                var (parsedCode, parsedState) = ParseAuthorizationInput(result.Input!);
                if (!string.IsNullOrEmpty(parsedState) && parsedState != state) throw new InvalidOperationException("State mismatch");
                code = parsedCode;
            }
            if (string.IsNullOrEmpty(code)) throw new InvalidOperationException("Missing authorization code");
            return await ExchangeAsync(code, verifier, RedirectUri, token).ConfigureAwait(false);
        }
        finally { callback?.Close(); }
    }

    /// <summary>parseAuthorizationInput: a URL, "code#state", a query string or a bare code.</summary>
    public static (string? Code, string? State) ParseAuthorizationInput(string input)
    {
        var value = (input ?? "").Trim();
        if (value.Length == 0) return (null, null);
        if (OAuthFlows.TryUrlQuery(value, out var query)) return (OAuthFlows.Query(query, "code"), OAuthFlows.Query(query, "state"));
        if (value.Contains('#')) { var parts = value.Split('#', 3); return (parts[0], parts[1]); }
        if (value.Contains("code=", StringComparison.Ordinal)) return (OAuthFlows.Query(value, "code"), OAuthFlows.Query(value, "state"));
        return (value, null);
    }

    private async Task<OAuthCredentialSnapshot> LoginDeviceCodeAsync(IProviderAuthInteraction interaction, CancellationToken token)
    {
        var started = await OAuthFlows.SendAsync(http, HttpMethod.Post, DeviceUserCodeUrl, token, new JsonObject { ["client_id"] = ClientId }.ToJsonString(), time: _time).ConfigureAwait(false);
        if (!started.Ok)
        {
            if (started.Status == 404) throw new InvalidOperationException("OpenAI Codex device code login is not enabled for this server. Use browser login or verify the server URL.");
            throw new InvalidOperationException($"OpenAI Codex device code request failed with status {started.Status}{(started.Body.Length != 0 ? ": " + started.Body : "")}");
        }
        var json = started.Json();
        double? interval = json?["interval"] is JsonValue raw ? raw.TryGetValue<double>(out var number) ? number
            : raw.TryGetValue<string>(out var text) && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null : null;
        var deviceId = OAuthFlows.OptionalText(json, "device_auth_id"); var userCode = OAuthFlows.OptionalText(json, "user_code");
        if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(userCode) || interval is not { } seconds || !double.IsFinite(seconds) || seconds < 0)
            throw new InvalidOperationException("Invalid OpenAI Codex device code response: " + OAuthFlows.ProjectJson(started.Body));
        interaction.Notify(new(AuthEventKind.DeviceCode) { UserCode = userCode, VerificationUri = DeviceVerificationUri, IntervalSeconds = seconds, ExpiresInSeconds = DeviceCodeTimeoutSeconds });
        var (authorizationCode, codeVerifier) = await OAuthFlows.PollDeviceCodeAsync<(string, string)>(async pollToken =>
        {
            var response = await OAuthFlows.SendAsync(http, HttpMethod.Post, DeviceTokenUrl, pollToken,
                new JsonObject { ["device_auth_id"] = deviceId, ["user_code"] = userCode }.ToJsonString(), time: _time).ConfigureAwait(false);
            if (response.Ok)
            {
                var data = response.Json();
                var code = OAuthFlows.OptionalText(data, "authorization_code"); var verifier = OAuthFlows.OptionalText(data, "code_verifier");
                return string.IsNullOrEmpty(code) || string.IsNullOrEmpty(verifier)
                    ? new(OAuthFlows.DevicePollStatus.Failed, Message: "Invalid OpenAI Codex device auth token response: " + OAuthFlows.ProjectJson(response.Body))
                    : new(OAuthFlows.DevicePollStatus.Complete, (code, verifier));
            }
            if (response.Status is 403 or 404) return new(OAuthFlows.DevicePollStatus.Pending);
            string? errorCode = null;
            if (response.Json()?["error"] is { } error)
                errorCode = error is JsonObject nested ? OAuthFlows.OptionalText(nested, "code") : error is JsonValue value && value.TryGetValue<string>(out var flat) ? flat : null;
            if (errorCode == "deviceauth_authorization_pending") return new(OAuthFlows.DevicePollStatus.Pending);
            if (errorCode == "slow_down") return new(OAuthFlows.DevicePollStatus.SlowDown);
            return new(OAuthFlows.DevicePollStatus.Failed, Message: $"OpenAI Codex device auth failed with status {response.Status}{(response.Body.Length != 0 ? ": " + response.Body : "")}");
        }, seconds, DeviceCodeTimeoutSeconds, false, token, _time).ConfigureAwait(false);
        return await ExchangeAsync(authorizationCode, codeVerifier, DeviceRedirectUri, token).ConfigureAwait(false);
    }

    private async Task<OAuthCredentialSnapshot> ExchangeAsync(string code, string verifier, string redirectUri, CancellationToken token)
    {
        var response = await OAuthFlows.SendAsync(http, HttpMethod.Post, TokenUrl, token, OAuthFlows.Form(("grant_type", "authorization_code"),
            ("client_id", ClientId), ("code", code), ("code_verifier", verifier), ("redirect_uri", redirectUri)), "application/x-www-form-urlencoded", time: _time).ConfigureAwait(false);
        return Credential(ReadToken(response, "exchange"));
    }

    public async Task<OAuthCredentialSnapshot> RefreshAsync(string provider, OAuthCredentialSnapshot current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        OAuthFlows.HttpResult response;
        try
        {
            response = await OAuthFlows.SendAsync(http, HttpMethod.Post, TokenUrl, cancellationToken, OAuthFlows.Form(("grant_type", "refresh_token"),
                ("refresh_token", current.Refresh), ("client_id", ClientId)), "application/x-www-form-urlencoded", time: _time, loginCancellation: false).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or TimeoutException or OperationCanceledException)
        { throw new InvalidOperationException("OpenAI Codex token refresh error: " + error.Message, error); }
        return Credential(ReadToken(response, "refresh"));
    }

    private (string Access, string Refresh, long Expires) ReadToken(OAuthFlows.HttpResult response, string operation)
    {
        if (!response.Ok)
            throw new InvalidOperationException($"OpenAI Codex token {operation} failed ({response.Status}): {(response.Body.Length != 0 ? response.Body : response.StatusText)}");
        var json = response.Json();
        if (OAuthFlows.OptionalText(json, "access_token") is not { Length: > 0 } access || OAuthFlows.OptionalText(json, "refresh_token") is not { Length: > 0 } refresh ||
            OAuthFlows.Number(json, "expires_in") is not { } expiresIn)
            throw new InvalidOperationException($"OpenAI Codex token {operation} response missing fields: {OAuthFlows.ProjectJson(response.Body)}");
        return (access, refresh, (long)(_time.GetUtcNow().ToUnixTimeMilliseconds() + expiresIn * 1000));
    }

    /// <summary>credentialsFromToken: the account id claim is required and stored as accountId.</summary>
    private static OAuthCredentialSnapshot Credential((string Access, string Refresh, long Expires) token)
    {
        var accountId = OAuthFlows.JwtPayload(token.Access)?[JwtClaimPath]?["chatgpt_account_id"] is JsonValue value && value.TryGetValue<string>(out var id) && id.Length > 0 ? id : null;
        if (accountId is null) throw new InvalidOperationException("Failed to extract accountId from token");
        return new(token.Access, token.Refresh, token.Expires, new Dictionary<string, string> { ["accountId"] = accountId });
    }

    public ProviderModelAuth ToAuth(OAuthCredentialSnapshot credential) => new(credential.Access);
}

/// <summary>Sign in with ChatGPT (OpenAI Responses token sharing) for provider openai.</summary>
public sealed partial class OpenAIChatGPTOAuth(HttpMessageInvoker http, Func<string, string?> environment, TimeProvider? time = null,
    string authBaseUrl = "https://auth.openai.com", int callbackPort = 1455) : IProviderOAuth
{
    public const string DynamicClientId = "dynamic_agent_client";
    public const string Resource = "https://api.openai.com/v1";
    public const string DirectTokenScope = "chatgpt.tokens.use.direct";
    public const string Scope = "openid profile email offline_access resource.invoke " + DirectTokenScope;
    private const long ExpiryMarginMilliseconds = 3 * 60 * 1000;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private string RedirectUri => $"http://127.0.0.1:{callbackPort}/auth/callback";
    private string TokenUrl => authBaseUrl + "/api/accounts/oauth/token";

    public string Name => "OpenAI (ChatGPT subscription)";
    public bool IsSubscription => true;
    public string? LoginLabel => "Sign in with ChatGPT";

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase)] private static partial Regex Uuid();

    /// <summary>agentHostId: urn:uuid of the installation's device id.</summary>
    public static string AgentHostId(string? deviceId) => deviceId is not null && Uuid().IsMatch(deviceId)
        ? "urn:uuid:" + deviceId.ToLowerInvariant() : throw new InvalidOperationException("Sign in with ChatGPT requires a device ID (UUID) for this installation");

    public string BuildAuthorizationUrl(string hostId, string agentName, string state, string challenge, string nonce) =>
        authBaseUrl + "/api/accounts/authorize?" + OAuthFlows.Form(("client_id", DynamicClientId), ("agent_name_hint", agentName), ("ext_agent_host_id", hostId),
            ("response_type", "code"), ("redirect_uri", RedirectUri), ("resource", Resource), ("scope", Scope), ("state", state),
            ("code_challenge", challenge), ("code_challenge_method", "S256"), ("nonce", nonce));

    public async Task<OAuthCredentialSnapshot> LoginAsync(IProviderAuthInteraction interaction, ProviderLoginOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        var hostId = AgentHostId(options?.GetDeviceId?.Invoke());
        var (verifier, challenge) = OAuthFlows.GeneratePkce();
        var state = OAuthFlows.RandomBase64Url(32); var nonce = OAuthFlows.RandomBase64Url(32);
        // Without this server the browser's callback would reach whatever else holds the port, so a busy port is an error.
        ChatGPTCallback server;
        try { server = ChatGPTCallback.Start(OAuthFlows.CallbackHost(environment), callbackPort, state, cancellationToken); }
        catch (System.Net.Sockets.SocketException error) when (error.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse)
        { throw new InvalidOperationException($"Port {callbackPort} is in use, probably by an unfinished login in another pi session or by the Codex CLI. Cancel that login and try again."); }
        using var ownedServer = server;
        interaction.Notify(new(AuthEventKind.AuthUrl) { Url = BuildAuthorizationUrl(hostId, options?.AgentName ?? "Pi", state, challenge, nonce),
            Instructions = "Complete sign-in in your browser. If the callback does not complete, paste the final redirect URL here." });
        using var manualAbort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var manual = ManualAsync();
        try
        {
            var winner = await Task.WhenAny(server.Result, manual).ConfigureAwait(false);
            var (code, clientId) = await winner.ConfigureAwait(false);
            interaction.Notify(new(AuthEventKind.Progress) { Message = "Exchanging authorization code for tokens..." });
            var token = await RequestTokenAsync(OAuthFlows.Form(("grant_type", "authorization_code"), ("client_id", clientId), ("code", code),
                ("code_verifier", verifier), ("redirect_uri", RedirectUri), ("resource", Resource)), cancellationToken).ConfigureAwait(false);
            // Pi does not use the ID token; its presence is part of the token-response contract.
            if (OAuthFlows.OptionalText(token, "id_token") is not { } id || id.Trim().Length == 0)
                throw new InvalidOperationException("OpenAI OAuth token response did not contain an ID token");
            return Credential(token, clientId);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { throw OAuthFlows.Cancelled(cancellationToken); }
        finally { manualAbort.Cancel(); }

        async Task<(string, string)> ManualAsync()
        {
            var input = await interaction.PromptAsync(new(AuthPromptKind.ManualCode, "Complete login in your browser, or paste the final redirect URL here:", RedirectUri),
                manualAbort.Token).ConfigureAwait(false);
            return FromManualInput(input, state);
        }
    }

    private (string Code, string ClientId) FromManualInput(string input, string expectedState)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var url)) throw new InvalidOperationException("Paste the full callback URL from the browser");
        var expected = new Uri(RedirectUri);
        if (url.GetLeftPart(UriPartial.Authority) != expected.GetLeftPart(UriPartial.Authority) || url.AbsolutePath != expected.AbsolutePath)
            throw new InvalidOperationException($"The pasted callback URL must start with {RedirectUri}");
        var query = url.Query.TrimStart('?');
        if (OAuthFlows.Query(query, "error") is { Length: > 0 } error) throw new InvalidOperationException($"ChatGPT authorization failed: {error}");
        return FromCallback(query, expectedState);
    }

    internal static (string Code, string ClientId) FromCallback(string query, string expectedState)
    {
        var code = OAuthFlows.Query(query, "code");
        if (string.IsNullOrEmpty(code)) throw new InvalidOperationException("Missing authorization code");
        var state = OAuthFlows.Query(query, "state");
        if (string.IsNullOrEmpty(state)) throw new InvalidOperationException("Missing OAuth state");
        if (state != expectedState) throw new InvalidOperationException("OAuth state mismatch");
        var clientId = OAuthFlows.Query(query, "client_id")?.Trim();
        if (string.IsNullOrEmpty(clientId)) throw new InvalidOperationException("OpenAI OAuth registration callback did not contain an issued client ID");
        return (code, clientId);
    }

    private async Task<JsonObject> RequestTokenAsync(string body, CancellationToken token, bool login = true)
    {
        var response = await OAuthFlows.SendAsync(http, HttpMethod.Post, TokenUrl, token, body, "application/x-www-form-urlencoded",
            [new("accept", "application/json")], time: _time, loginCancellation: login).ConfigureAwait(false);
        if (!response.Ok)
            throw new InvalidOperationException($"OpenAI OAuth token request failed ({response.Status}): {(response.Body.Length != 0 ? response.Body : response.StatusText)}");
        return response.Json() ?? throw new InvalidOperationException("OpenAI OAuth token response must be an object");
    }

    private OAuthCredentialSnapshot Credential(JsonObject token, string clientId)
    {
        string Required(string field) => OAuthFlows.OptionalText(token, field) is { } value && value.Trim().Length != 0 ? value
            : throw new InvalidOperationException($"OpenAI OAuth token response has invalid {field}");
        var access = Required("access_token"); var refresh = Required("refresh_token"); var scope = Required("scope");
        if (OAuthFlows.Number(token, "expires_in") is not { } expiresIn || expiresIn <= 0) throw new InvalidOperationException("OpenAI OAuth token response has invalid expires_in");
        var scopes = scope.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (!scopes.Contains(DirectTokenScope)) throw new InvalidOperationException($"OpenAI OAuth grant did not include {DirectTokenScope}");
        return new(access, refresh, (long)(_time.GetUtcNow().ToUnixTimeMilliseconds() + expiresIn * 1000 - ExpiryMarginMilliseconds),
            new Dictionary<string, string> { ["clientId"] = clientId },
            new Dictionary<string, JsonData> { ["scopes"] = JsonData.Parse(JsonSerializer.Serialize(scopes)) });
    }

    public async Task<OAuthCredentialSnapshot> RefreshAsync(string provider, OAuthCredentialSnapshot current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!current.ProviderData.TryGetValue("clientId", out var clientId) || clientId.Trim().Length == 0)
            throw new InvalidOperationException("Stored OpenAI OAuth credential does not contain an issued client ID; reconnect ChatGPT");
        var token = await RequestTokenAsync(OAuthFlows.Form(("grant_type", "refresh_token"), ("client_id", clientId), ("refresh_token", current.Refresh),
            ("resource", Resource)), cancellationToken, login: false).ConfigureAwait(false);
        return Credential(token, clientId);
    }

    public ProviderModelAuth ToAuth(OAuthCredentialSnapshot credential) => new(credential.Access);

    /// <summary>The ChatGPT callback server: its own state, client id and error pages (openai-chatgpt.ts startCallbackServer).</summary>
    private sealed class ChatGPTCallback : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener;
        private readonly TaskCompletionSource<(string, string)> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _stop = new();
        private readonly string _state;
        private CancellationTokenRegistration _abort;
        private ChatGPTCallback(System.Net.Sockets.TcpListener listener, string state) { _listener = listener; _state = state; _ = _result.Task.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted); }
        internal Task<(string, string)> Result => _result.Task;
        internal static ChatGPTCallback Start(string host, int port, string state, CancellationToken token)
        {
            var address = System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var parsed) ? parsed : System.Net.IPAddress.Loopback;
            var listener = new System.Net.Sockets.TcpListener(address, port);
            listener.Start();
            var server = new ChatGPTCallback(listener, state);
            server._abort = token.Register(() => server._result.TrySetException(OAuthFlows.Cancelled(token)));
            _ = server.AcceptAsync();
            return server;
        }
        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false); }
                catch (Exception error) { if (!_stop.IsCancellationRequested) _result.TrySetException(error); return; }
                _ = HandleAsync(client);
            }
        }
        private async Task HandleAsync(System.Net.Sockets.TcpClient client)
        {
            using var _ = client;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var stream = client.GetStream(); var buffer = new byte[16 * 1024]; var length = 0;
                while (length < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token).ConfigureAwait(false);
                    if (read == 0) break; length += read;
                    if (System.Text.Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                }
                var target = System.Text.Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n")[0].Split(' ') is { Length: >= 2 } line ? line[1] : "/";
                var question = target.IndexOf('?');
                var path = question < 0 ? target : target[..question]; var query = question < 0 ? "" : target[(question + 1)..];
                int status; string html; Action? after = null;
                if (path != "/auth/callback") (status, html) = (404, OAuthPages.Error("Callback route not found."));
                else if (OAuthFlows.Query(query, "error") is { Length: > 0 } error)
                {
                    (status, html) = (400, OAuthPages.Error("ChatGPT was not connected.", $"Error: {error}"));
                    after = () => _result.TrySetException(new InvalidOperationException($"ChatGPT authorization failed: {error}"));
                }
                else
                {
                    try
                    {
                        var result = FromCallback(query, _state);
                        (status, html) = (200, OAuthPages.Success("ChatGPT authentication completed. You can close this window."));
                        after = () => _result.TrySetResult(result);
                    }
                    catch (InvalidOperationException invalid) { (status, html) = (400, OAuthPages.Error(invalid.Message)); }
                }
                var body = System.Text.Encoding.UTF8.GetBytes(html);
                var head = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: text/html; charset=utf-8\r\ncontent-length: {body.Length}\r\nconnection: close\r\n\r\n");
                await stream.WriteAsync(head, timeout.Token).ConfigureAwait(false); await stream.WriteAsync(body, timeout.Token).ConfigureAwait(false);
                after?.Invoke();
            }
            catch (Exception) { }
        }
        public void Dispose() { _abort.Dispose(); _stop.Cancel(); _listener.Stop(); }
    }
}
