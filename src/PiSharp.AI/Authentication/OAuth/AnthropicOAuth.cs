// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/auth/oauth/anthropic.ts.
// Also ports packages/ai/src/auth/oauth/callback-server.ts, pkce.ts and utils/oauth-page.ts (text only).
using System.Buffers.Text;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PiSharp.AI.Authentication.OAuth;

public sealed record AnthropicOAuthLoginOption(string Id, string Label);
public enum AnthropicOAuthLoginEventKind { AuthUrl, Progress }
public sealed record AnthropicOAuthLoginEvent(AnthropicOAuthLoginEventKind Kind, string? Url = null, string? Instructions = null, string? Message = null);

/// <summary>The ProviderAuthInteraction subset Anthropic login uses. A prompt token is cancelled when the prompt is no longer needed.</summary>
public interface IAnthropicOAuthLoginInteraction
{
    void Notify(AnthropicOAuthLoginEvent loginEvent);
    Task<string> SelectAsync(string message, IReadOnlyList<AnthropicOAuthLoginOption> options, CancellationToken cancellationToken);
    Task<string> PromptManualCodeAsync(string message, string placeholder, CancellationToken cancellationToken);
}

/// <summary>
/// Anthropic (Claude Pro/Max) OAuth: browser login with a loopback callback (53692, else a free port),
/// copy-code login through platform.claude.com, authorization-code exchange and refresh.
/// </summary>
public sealed partial class AnthropicOAuth : IAdmittedOAuthRefresh
{
    public static readonly string ClientId = Encoding.UTF8.GetString(Convert.FromBase64String("OWQxYzI1MGEtZTYxYi00NGQ5LTg4ZWQtNTk0NGQxOTYyZjVl"));
    public const string AuthorizeUrl = "https://claude.ai/oauth/authorize";
    public const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    public const string CallbackHostEnvironment = "PI_OAUTH_CALLBACK_HOST";
    public const string DefaultCallbackHost = "127.0.0.1";
    // Preferred so the port can be forwarded into containers or over SSH. Anthropic accepts any loopback
    // port, so login falls back to a free port when this one cannot be bound (Pi 1.1.0, #10571).
    public const int CallbackPort = 53692;
    public const string CallbackPath = "/callback";
    public const string RedirectUri = "http://localhost:53692/callback";
    public const string CopyCodeRedirectUri = "https://platform.claude.com/oauth/code/callback";
    public const string BrowserLoginMethod = "browser";
    public const string CopyCodeLoginMethod = "copy_code";
    public const string Scopes = "org:create_api_key user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload";
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    public static readonly IReadOnlyList<AnthropicOAuthLoginOption> LoginOptions =
        [new(BrowserLoginMethod, "Browser login (default)"), new(CopyCodeLoginMethod, "Copy code login (headless)")];
    private readonly HttpMessageInvoker http;
    private readonly TimeProvider time;
    private readonly string callbackHost;
    private readonly int callbackPort;

    public AnthropicOAuth(HttpMessageInvoker http, TimeProvider? timeProvider = null, string? callbackHost = null, int callbackPort = CallbackPort)
    {
        ArgumentNullException.ThrowIfNull(http);
        this.http = http; time = timeProvider ?? TimeProvider.System;
        this.callbackHost = string.IsNullOrEmpty(callbackHost) ? DefaultCallbackHost : callbackHost; this.callbackPort = callbackPort;
    }

    /// <summary>CALLBACK_HOST: PI_OAUTH_CALLBACK_HOST when truthy, else 127.0.0.1.</summary>
    public static string CallbackHostFrom(ProviderEnvironmentSnapshot environment) =>
        environment?.GetValue(CallbackHostEnvironment) ?? DefaultCallbackHost;

    public async Task<OAuthCredentialSnapshot> LoginAsync(IAnthropicOAuthLoginInteraction interaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        var method = await interaction.SelectAsync("Select Anthropic login method:", LoginOptions, cancellationToken).ConfigureAwait(false);
        if (method == CopyCodeLoginMethod) return await LoginCopyCodeAsync(interaction, cancellationToken).ConfigureAwait(false);
        if (method != BrowserLoginMethod) throw new InvalidOperationException($"Unknown Anthropic login method: {method}");
        return await LoginBrowserAsync(interaction, cancellationToken).ConfigureAwait(false);
    }

    public Task<OAuthCredentialSnapshot> RefreshAsync(string provider, OAuthCredentialSnapshot current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        return RefreshTokenAsync(current.Refresh, cancellationToken);
    }

    public static string BuildAuthorizationUrl(string redirectUri, string challenge, string state) => AuthorizeUrl + "?" + string.Join('&',
        new (string, string)[] { ("code", "true"), ("client_id", ClientId), ("response_type", "code"), ("redirect_uri", redirectUri),
            ("scope", Scopes), ("code_challenge", challenge), ("code_challenge_method", "S256"), ("state", state) }
        .Select(pair => FormEncode(pair.Item1) + "=" + FormEncode(pair.Item2)));

    /// <summary>parseAuthorizationInput: a redirect URL, "code#state", a query string, or a bare code. Empty strings stay distinct from absent.</summary>
    public static (string? Code, string? State) ParseAuthorizationInput(string input)
    {
        var value = (input ?? "").Trim();
        if (value.Length == 0) return (null, null);
        if (UrlScheme().IsMatch(value) && Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            var query = value.IndexOf('?') is var start and >= 0 ? value[(start + 1)..] : "";
            if (query.IndexOf('#') is var hash and >= 0) query = query[..hash];
            return (QueryValue(query, "code"), QueryValue(query, "state"));
        }
        if (value.Contains('#'))
        {
            var parts = value.Split('#');
            return (parts[0], parts[1]);
        }
        if (value.Contains("code=", StringComparison.Ordinal))
        {
            var query = value.StartsWith('?') ? value[1..] : value;
            return (QueryValue(query, "code"), QueryValue(query, "state"));
        }
        return (value, null);
    }

    public static (string Verifier, string Challenge) GeneratePkce()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (verifier, Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(verifier))));
    }

    public async Task<OAuthCredentialSnapshot> ExchangeAuthorizationCodeAsync(string code, string state, string verifier, string redirectUri,
        CancellationToken cancellationToken = default)
    {
        string body;
        try
        {
            body = await PostJsonAsync(new JsonObject
            {
                ["grant_type"] = "authorization_code", ["client_id"] = ClientId, ["code"] = code, ["state"] = state,
                ["redirect_uri"] = redirectUri, ["code_verifier"] = verifier,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            throw new InvalidOperationException($"Token exchange request failed. url={TokenUrl}; redirect_uri={redirectUri}; response_type=authorization_code; details={Details(error)}", error);
        }
        return Credential(body, "Token exchange returned invalid JSON. url=" + TokenUrl);
    }

    public async Task<OAuthCredentialSnapshot> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        string body;
        try
        {
            body = await PostJsonAsync(new JsonObject
            {
                ["grant_type"] = "refresh_token", ["client_id"] = ClientId, ["refresh_token"] = refreshToken,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { throw new InvalidOperationException($"Anthropic token refresh request failed. url={TokenUrl}; details={Details(error)}", error); }
        return Credential(body, "Anthropic token refresh returned invalid JSON. url=" + TokenUrl);
    }

    private async Task<OAuthCredentialSnapshot> LoginBrowserAsync(IAnthropicOAuthLoginInteraction interaction, CancellationToken cancellationToken)
    {
        var (verifier, challenge) = GeneratePkce();
        // Without a callback server, login continues with the pasted redirect URL.
        var callback = TryStart(callbackPort) ?? TryStart(0);
        var redirectUri = callback?.RedirectUri ?? RedirectUri;
        try
        {
            interaction.Notify(new(AnthropicOAuthLoginEventKind.AuthUrl, BuildAuthorizationUrl(redirectUri, challenge, verifier),
                "Complete login in your browser. If the browser is on another machine, paste the final redirect URL here."));
            var (callbackCode, input) = await WaitForCallbackOrManualInputAsync(interaction, callback,
                "Complete login in your browser, or paste the authorization code / redirect URL here:", redirectUri).ConfigureAwait(false);
            string? code; var state = verifier;
            if (callbackCode is not null) code = callbackCode;
            else
            {
                var parsed = ParseAuthorizationInput(input!);
                if (!string.IsNullOrEmpty(parsed.State) && parsed.State != verifier) throw new InvalidOperationException("OAuth state mismatch");
                code = parsed.Code; state = parsed.State ?? verifier;
            }
            if (string.IsNullOrEmpty(code)) throw new InvalidOperationException("Missing authorization code");
            interaction.Notify(new(AnthropicOAuthLoginEventKind.Progress, Message: "Exchanging authorization code for tokens..."));
            return await ExchangeAuthorizationCodeAsync(code, state, verifier, redirectUri, cancellationToken).ConfigureAwait(false);
        }
        finally { callback?.Close(); }

        AnthropicOAuthCallbackServer? TryStart(int port)
        {
            try { return AnthropicOAuthCallbackServer.Start("Anthropic", callbackHost, port, CallbackPath, "localhost", verifier, cancellationToken); }
            catch (Exception) { return null; }
        }
    }

    private async Task<OAuthCredentialSnapshot> LoginCopyCodeAsync(IAnthropicOAuthLoginInteraction interaction, CancellationToken cancellationToken)
    {
        var (verifier, challenge) = GeneratePkce();
        interaction.Notify(new(AnthropicOAuthLoginEventKind.AuthUrl, BuildAuthorizationUrl(CopyCodeRedirectUri, challenge, verifier),
            "Complete login in your browser, then copy the code Anthropic shows and paste it here."));
        var input = await interaction.PromptManualCodeAsync("Paste the code Anthropic shows after you sign in:", "code#state", cancellationToken).ConfigureAwait(false);
        var parsed = ParseAuthorizationInput(input);
        if (!string.IsNullOrEmpty(parsed.State) && parsed.State != verifier) throw new InvalidOperationException("OAuth state mismatch");
        if (string.IsNullOrEmpty(parsed.Code)) throw new InvalidOperationException("Missing authorization code");
        interaction.Notify(new(AnthropicOAuthLoginEventKind.Progress, Message: "Exchanging authorization code for tokens..."));
        return await ExchangeAuthorizationCodeAsync(parsed.Code, parsed.State ?? verifier, verifier, CopyCodeRedirectUri, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>waitForCallbackOrManualInput: the browser callback or a pasted code, whichever comes first. The manual prompt is cancelled once this settles.</summary>
    public static async Task<(string? Callback, string? Input)> WaitForCallbackOrManualInputAsync(IAnthropicOAuthLoginInteraction interaction,
        AnthropicOAuthCallbackServer? callback, string message, string placeholder)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        using var manualAbort = new CancellationTokenSource();
        ExceptionDispatchInfo? manualError = null;
        var manual = RunManualAsync();
        try
        {
            var value = callback is null ? null : await callback.WaitAsync().ConfigureAwait(false);
            manualError?.Throw();
            if (value is not null) return (value, null);
            var input = await manual.ConfigureAwait(false);
            manualError?.Throw();
            return (null, input ?? "");
        }
        finally { manualAbort.Cancel(); }

        async Task<string?> RunManualAsync()
        {
            try
            {
                var input = await interaction.PromptManualCodeAsync(message, placeholder, manualAbort.Token).ConfigureAwait(false);
                callback?.Cancel();
                return input;
            }
            catch (Exception error)
            {
                manualError = ExceptionDispatchInfo.Capture(error);
                callback?.Cancel();
                return null;
            }
        }
    }

    private async Task<string> PostJsonAsync(JsonObject body, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(RequestTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await http.SendAsync(request, linked.Token).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
        // Response bodies can echo credential material; diagnostics keep only status and URL.
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP request failed. status={(int)response.StatusCode}; url={TokenUrl}", null, response.StatusCode);
        return text;
    }

    private OAuthCredentialSnapshot Credential(string body, string invalid)
    {
        JsonObject? data;
        try { data = JsonNode.Parse(body) as JsonObject; }
        catch (JsonException error) { throw new InvalidOperationException(invalid, error); }
        if (data?["access_token"] is not JsonValue access || !access.TryGetValue<string>(out var accessToken) ||
            data["refresh_token"] is not JsonValue refresh || !refresh.TryGetValue<string>(out var refreshToken) ||
            data["expires_in"] is not JsonValue expires || !expires.TryGetValue<double>(out var expiresIn) || !double.IsFinite(expiresIn))
            throw new InvalidOperationException(invalid);
        return new(accessToken, refreshToken, (long)(time.GetUtcNow().ToUnixTimeMilliseconds() + expiresIn * 1000 - 5 * 60 * 1000));
    }

    private static string Details(Exception error)
    {
        var details = error.GetType().Name + ": " + error.Message;
        return error.InnerException is { } inner ? details + "; cause=" + Details(inner) : details;
    }

    internal static string FormEncode(string value)
    {
        var builder = new StringBuilder();
        foreach (var unit in Encoding.UTF8.GetBytes(value))
            if (unit is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'*' or (byte)'-' or (byte)'.' or (byte)'_')
                builder.Append((char)unit);
            else if (unit == (byte)' ') builder.Append('+');
            else builder.Append('%').Append(unit.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    /// <summary>URLSearchParams.get: the first value, "" for a bare name, null when absent.</summary>
    internal static string? QueryValue(string query, string name)
    {
        foreach (var pair in query.Split('&'))
        {
            if (pair.Length == 0) continue;
            var equals = pair.IndexOf('=');
            var key = FormDecode(equals < 0 ? pair : pair[..equals]);
            if (key == name) return equals < 0 ? "" : FormDecode(pair[(equals + 1)..]);
        }
        return null;
    }

    private static string FormDecode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9+.-]*:")] private static partial Regex UrlScheme();
}

/// <summary>
/// Loopback OAuth redirect handler (callback-server.ts). Port 0 picks a free port. WaitAsync resolves with
/// the code, or null after Cancel; it faults on a provider error, cancellation, or Close.
/// </summary>
public sealed class AnthropicOAuthCallbackServer
{
    private const int MaximumRequestBytes = 16 * 1024;
    private static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(10);
    private readonly TcpListener listener;
    private readonly string providerName;
    private readonly string path;
    private readonly string? state;
    private readonly TaskCompletionSource<string?> wait = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private CancellationTokenRegistration abort;
    private bool claimed;
    private bool settled;

    private AnthropicOAuthCallbackServer(TcpListener listener, string providerName, string path, string? state, string redirectUri)
    {
        this.listener = listener; this.providerName = providerName; this.path = path; this.state = state; RedirectUri = redirectUri;
        // A cancelled or closed wait may never be observed.
        _ = wait.Task.ContinueWith(task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    public string RedirectUri { get; }
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public static AnthropicOAuthCallbackServer Start(string providerName, string host, int port, string path, string? redirectHost,
        string? state, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException("Login cancelled", cancellationToken);
        var address = IPAddress.TryParse(host.Trim('[', ']'), out var parsed) ? parsed
            : host == "localhost" ? IPAddress.Loopback : Dns.GetHostAddresses(host)[0];
        var listener = new TcpListener(address, port);
        listener.Start();
        var bound = ((IPEndPoint)listener.LocalEndpoint).Port;
        var name = redirectHost ?? host;
        var server = new AnthropicOAuthCallbackServer(listener, providerName, path, state,
            $"http://{(name.Contains(':') ? "[" + name + "]" : name)}:{bound}{path}");
        server.abort = cancellationToken.Register(() => server.Finish(null, new OperationCanceledException("Login cancelled", cancellationToken)));
        _ = server.AcceptAsync();
        return server;
    }

    public Task<string?> WaitAsync() => wait.Task;

    /// <summary>Stop waiting for the browser unless a callback is already being completed.</summary>
    public void Cancel()
    {
        lock (gate) if (claimed) return;
        Finish(null, null);
    }

    public void Close()
    {
        Finish(null, new InvalidOperationException("OAuth callback server closed"));
        stop.Cancel();
        listener.Stop();
    }

    private void Finish(string? value, Exception? error)
    {
        lock (gate) { if (settled) return; settled = true; }
        abort.Dispose();
        if (error is not null) wait.SetException(error); else wait.SetResult(value);
    }

    private async Task AcceptAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false); }
            catch (Exception error)
            {
                if (!stop.IsCancellationRequested) Finish(null, error);
                return;
            }
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        string? pendingCode = null; Exception? pendingError = null;
        try
        {
            using var timeout = new CancellationTokenSource(RequestReadTimeout);
            var stream = client.GetStream();
            var buffer = new byte[MaximumRequestBytes];
            var length = 0; var end = -1;
            while (end < 0 && length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
                end = Encoding.ASCII.GetString(buffer, 0, length).IndexOf("\r\n\r\n", StringComparison.Ordinal);
            }
            var line = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n")[0].Split(' ');
            var (status, html) = Route(line.Length >= 2 ? line[0] : "", line.Length >= 2 ? line[1] : "/");
            var body = Encoding.UTF8.GetBytes(html);
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\ncontent-type: text/html; charset=utf-8\r\n" +
                $"cache-control: no-store\r\ncontent-length: {body.Length}\r\nconnection: close\r\n\r\n");
            await stream.WriteAsync(head, timeout.Token).ConfigureAwait(false);
            await stream.WriteAsync(body, timeout.Token).ConfigureAwait(false);
            await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception) { /* A broken browser connection only loses its page. */ }
        // Settle after the page is written, as the original sends the page before finishing.
        if (pendingCode is not null) Finish(pendingCode, null);
        else if (pendingError is not null) Finish(null, pendingError);

        (int, string) Route(string method, string target)
        {
            var question = target.IndexOf('?');
            var pathname = question < 0 ? target : target[..question];
            var query = question < 0 ? "" : target[(question + 1)..];
            if (query.IndexOf('#') is var hash and >= 0) query = query[..hash];
            if (method != "GET" || pathname != path) return (404, ErrorHtml("Callback route not found."));
            if (state is not null && AnthropicOAuth.QueryValue(query, "state") != state) return (400, ErrorHtml("State mismatch."));
            lock (gate)
            {
                if (claimed || settled) return (409, ErrorHtml("This sign-in has already been handled."));
                if (AnthropicOAuth.QueryValue(query, "error") is { Length: > 0 } providerError)
                {
                    var description = AnthropicOAuth.QueryValue(query, "error_description") ?? providerError;
                    pendingError = new InvalidOperationException($"{providerName} authorization failed: {description}");
                    return (400, ErrorHtml($"{providerName} authorization failed.", description));
                }
                if (AnthropicOAuth.QueryValue(query, "code") is not { Length: > 0 } code) return (400, ErrorHtml("Missing authorization code."));
                claimed = true; pendingCode = code;
            }
            return (200, Page("Authentication successful", $"Signed in to {providerName}. You may now close this page.", null));
        }
    }

    private static string ErrorHtml(string message, string? details = null) => Page("Authentication failed", message, details);
    private static string Page(string heading, string message, string? details) =>
        $"<!doctype html>\n<html lang=\"en\">\n<head>\n  <meta charset=\"utf-8\" />\n  <title>{WebUtility.HtmlEncode(heading)}</title>\n</head>\n" +
        $"<body>\n  <main>\n    <h1>{WebUtility.HtmlEncode(heading)}</h1>\n    <p>{WebUtility.HtmlEncode(message)}</p>\n" +
        (string.IsNullOrEmpty(details) ? "" : $"    <pre>{WebUtility.HtmlEncode(details)}</pre>\n") + "  </main>\n</body>\n</html>\n";
}
