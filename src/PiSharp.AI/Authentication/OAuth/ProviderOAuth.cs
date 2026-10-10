// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/auth/types.ts (AuthPrompt, AuthEvent, AuthInteraction,
// LoginOptions, OAuthAuth, ModelAuth), auth/oauth/device-code.ts (pollOAuthDeviceCodeFlow), auth/oauth/callback-server.ts
// (startOAuthCallbackServer with complete/timeout, waitForCallbackOrManualInput), auth/oauth/pkce.ts and utils/oauth-page.ts.
using System.Buffers.Text;
using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;

namespace PiSharp.AI.Authentication.OAuth;

public enum AuthPromptKind { Text, Secret, Select, ManualCode }
public sealed record AuthPromptOption(string Id, string Label, string? Description = null);
/// <summary>A login prompt; select prompts answer with the option id.</summary>
public sealed record AuthPrompt(AuthPromptKind Kind, string Message, string? Placeholder = null, IReadOnlyList<AuthPromptOption>? Options = null);

public enum AuthEventKind { Info, AuthUrl, DeviceCode, Progress }
public sealed record AuthInfoLink(string Url, string? Label = null);
public sealed record AuthEvent(AuthEventKind Kind)
{
    public string? Message { get; init; }
    public IReadOnlyList<AuthInfoLink>? Links { get; init; }
    public string? Url { get; init; }
    public string? Instructions { get; init; }
    public string? UserCode { get; init; }
    public string? VerificationUri { get; init; }
    public double? IntervalSeconds { get; init; }
    public double? ExpiresInSeconds { get; init; }
}

/// <summary>ProviderAuthInteraction: prompts reject on cancel; the prompt token withdraws one prompt (a raced manual code).</summary>
public interface IProviderAuthInteraction
{
    Task<string> PromptAsync(AuthPrompt prompt, CancellationToken cancellationToken);
    void Notify(AuthEvent authEvent);
}

/// <summary>LoginOptions: the installation's stable device id (OpenAI agent host id) and the agent name (default pi).</summary>
public sealed record ProviderLoginOptions(Func<string>? GetDeviceId = null, string? AgentName = null);

/// <summary>ModelAuth derived from a credential: apiKey, headers (null removes) and a per-credential base URL.</summary>
public sealed record ProviderModelAuth(string? ApiKey, ImmutableDictionary<string, string?>? Headers = null, string? BaseUrl = null)
{
    public override string ToString() => "ProviderModelAuth [redacted]";
}

/// <summary>OAuthAuth: login, refresh (run under the store lock by <see cref="StoredOAuthLifecycle"/>) and toAuth.</summary>
public interface IProviderOAuth : IAdmittedOAuthRefresh
{
    string Name { get; }
    bool IsSubscription { get; }
    string? LoginLabel { get; }
    Task<OAuthCredentialSnapshot> LoginAsync(IProviderAuthInteraction interaction, ProviderLoginOptions? options, CancellationToken cancellationToken);
    ProviderModelAuth ToAuth(OAuthCredentialSnapshot credential);
}

/// <summary>Shared OAuth helpers: cancellation text, PKCE, form encoding, HTTP with bounded bodies and the device-code poller.</summary>
public static class OAuthFlows
{
    public const string CancelMessage = "Login cancelled";
    public const string CallbackHostEnvironment = "PI_OAUTH_CALLBACK_HOST";

    public static OperationCanceledException Cancelled(CancellationToken token = default) => new(CancelMessage, token);

    /// <summary>generatePKCE: a base64url 32-byte verifier and its S256 challenge.</summary>
    public static (string Verifier, string Challenge) GeneratePkce() => AnthropicOAuth.GeneratePkce();

    /// <summary>URLSearchParams serialization.</summary>
    public static string Form(IEnumerable<KeyValuePair<string, string>> fields) =>
        string.Join('&', fields.Select(pair => AnthropicOAuth.FormEncode(pair.Key) + "=" + AnthropicOAuth.FormEncode(pair.Value)));
    public static string Form(params (string Key, string Value)[] fields) => Form(fields.Select(pair => KeyValuePair.Create(pair.Key, pair.Value)));

    /// <summary>URLSearchParams.get over a query string.</summary>
    public static string? Query(string query, string name) => AnthropicOAuth.QueryValue(query.StartsWith('?') ? query[1..] : query, name);

    /// <summary>A URL's query value, or null when the input is not an absolute URL.</summary>
    public static bool TryUrlQuery(string input, out string query)
    {
        query = "";
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || uri.Scheme.Length == 0 || input.Trim().IndexOf(':') < 1) return false;
        query = uri.Query.TrimStart('?'); return true;
    }

    public sealed record HttpResult(int Status, string StatusText, string Body, IReadOnlyDictionary<string, string>? Headers = null)
    {
        public bool Ok => Status is >= 200 and < 300;
        public string? Header(string name) => Headers is not null && Headers.TryGetValue(name.ToLowerInvariant(), out var value) ? value : null;
        public JsonObject? Json() { try { return JsonNode.Parse(Body) as JsonObject; } catch (JsonException) { return null; } }
    }

    /// <summary>Sends a request and reads its body. A cancelled caller becomes "Login cancelled" when <paramref name="loginCancellation"/>.</summary>
    public static async Task<HttpResult> SendAsync(HttpMessageInvoker http, HttpMethod method, string url, CancellationToken token,
        string? body = null, string? contentType = null, IEnumerable<KeyValuePair<string, string>>? headers = null,
        TimeSpan? timeout = null, TimeProvider? time = null, bool loginCancellation = true)
    {
        using var deadline = timeout is { } limit ? new CancellationTokenSource(limit, time ?? TimeProvider.System) : null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline?.Token ?? CancellationToken.None);
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) { request.Content = new StringContent(body, Encoding.UTF8); request.Content.Headers.ContentType = new(contentType ?? "application/json"); }
        foreach (var (name, value) in headers ?? []) request.Headers.TryAddWithoutValidation(name, value);
        try
        {
            using var response = await http.SendAsync(request, linked.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var field in response.Headers.Concat(response.Content.Headers)) fields[field.Key.ToLowerInvariant()] = string.Join(", ", field.Value);
            return new((int)response.StatusCode, response.ReasonPhrase ?? "", text, fields);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested && loginCancellation) { throw Cancelled(token); }
        catch (OperationCanceledException) when (deadline?.IsCancellationRequested == true) { throw new TimeoutException("The operation was aborted due to timeout"); }
    }

    public enum DevicePollStatus { Pending, SlowDown, Failed, Complete }
    public sealed record DevicePollResult<T>(DevicePollStatus Status, T? Value = default, string? Message = null, double? IntervalSeconds = null);

    /// <summary>pollOAuthDeviceCodeFlow: RFC 8628 polling with a 1 s floor, 5 s default, slow_down backoff and the expiry deadline.</summary>
    public static async Task<T> PollDeviceCodeAsync<T>(Func<CancellationToken, Task<DevicePollResult<T>>> poll, double? intervalSeconds,
        double? expiresInSeconds, bool waitBeforeFirstPoll, CancellationToken token, TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        double Now() => time.GetUtcNow().ToUnixTimeMilliseconds();
        var deadline = expiresInSeconds is { } expires ? Now() + expires * 1000 : double.PositiveInfinity;
        var interval = Math.Max(1000, Math.Floor((intervalSeconds ?? 5) * 1000));
        var slowDowns = 0;
        async Task Sleep(double milliseconds)
        {
            if (token.IsCancellationRequested) throw Cancelled(token);
            try { await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), time, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw Cancelled(token); }
        }
        if (waitBeforeFirstPoll && deadline - Now() > 0) await Sleep(Math.Min(interval, deadline - Now())).ConfigureAwait(false);
        while (Now() < deadline)
        {
            if (token.IsCancellationRequested) throw Cancelled(token);
            var result = await poll(token).ConfigureAwait(false);
            if (result.Status == DevicePollStatus.Complete) return result.Value!;
            if (result.Status == DevicePollStatus.Failed) throw new InvalidOperationException(result.Message);
            if (result.Status == DevicePollStatus.SlowDown)
            {
                slowDowns++;
                interval = result.IntervalSeconds is { } next && double.IsFinite(next) && next > 0
                    ? Math.Max(1000, Math.Floor(next * 1000)) : Math.Max(1000, interval + 5000);
            }
            var remaining = deadline - Now();
            if (remaining <= 0) break;
            await Sleep(Math.Min(interval, remaining)).ConfigureAwait(false);
        }
        throw new InvalidOperationException(slowDowns > 0
            ? "Device flow timed out after one or more slow_down responses. This is often caused by clock drift in WSL or VM environments. Please sync or restart the VM clock and try again."
            : "Device flow timed out");
    }

    /// <summary>waitForCallbackOrManualInput: the browser callback or a pasted code, whichever settles first.</summary>
    public static async Task<(bool Callback, T? Value, string? Input)> WaitForCallbackOrManualInputAsync<T>(IProviderAuthInteraction interaction,
        OAuthLoopbackServer<T>? callback, string message, string placeholder, CancellationToken loginToken)
    {
        using var manualAbort = CancellationTokenSource.CreateLinkedTokenSource(loginToken);
        ExceptionDispatchInfo? manualError = null;
        var manual = RunManualAsync();
        try
        {
            var (settled, value) = callback is null ? (false, default) : await callback.WaitAsync().ConfigureAwait(false);
            manualError?.Throw();
            if (settled) return (true, value, null);
            var input = await manual.ConfigureAwait(false);
            manualError?.Throw();
            return (false, default, input ?? "");
        }
        finally { manualAbort.Cancel(); }

        async Task<string?> RunManualAsync()
        {
            try
            {
                var input = await interaction.PromptAsync(new(AuthPromptKind.ManualCode, message, placeholder), manualAbort.Token).ConfigureAwait(false);
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

    /// <summary>The verification URI is opened in a browser; only http(s) URLs are trusted.</summary>
    public static string? TrustedHttpUrl(string? value, bool httpsOnly = false) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || !httpsOnly && uri.Scheme == Uri.UriSchemeHttp)
            ? uri.AbsoluteUri : null;

    /// <summary>A JWT payload (base64url segment two).</summary>
    public static JsonObject? JwtPayload(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return null;
            return JsonNode.Parse(Protocols.OpenAICodexResponses.OpenAICodexResponsesTransport.Base64UrlText(parts[1])) as JsonObject;
        }
        catch (Exception error) when (error is FormatException or JsonException) { return null; }
    }

    internal static string Text(JsonObject? json, string name) => json?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    internal static string? OptionalText(JsonObject? json, string name) => json?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    internal static double? Number(JsonObject? json, string name) => json?[name] is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number) ? number : null;
    internal static string Json(JsonNode? node) => node is null ? "null" : Contracts.Compatibility.EcmaScriptJsonProjection.Project(JsonData.Parse(node.ToJsonString()));
    internal static string ProjectJson(string body) { try { return Json(JsonNode.Parse(body)); } catch (JsonException) { return "null"; } }

    public static string CallbackHost(Func<string, string?> environment) =>
        environment(CallbackHostEnvironment) is { Length: > 0 } host ? host : "127.0.0.1";

    internal static string RandomHex(int bytes) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));
    internal static string RandomBase64Url(int bytes) => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));
}

/// <summary>
/// The loopback OAuth redirect handler (callback-server.ts). <c>complete</c> finishes the sign-in with the received code before the
/// browser page is sent, so the page can show an exchange failure. WaitAsync yields (true, value), or (false, default) after Cancel;
/// it faults on a provider error, a complete failure, cancellation, the timeout or Close.
/// </summary>
public sealed class OAuthLoopbackServer<T>
{
    private readonly TcpListener _listener;
    private readonly string _provider, _path;
    private readonly string? _state;
    private readonly Func<string, CancellationToken, Task<T>> _complete;
    private readonly TaskCompletionSource<(bool, T?)> _wait = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private CancellationTokenRegistration _abort;
    private ITimer? _timer;
    private bool _claimed, _settled;

    private OAuthLoopbackServer(TcpListener listener, string provider, string path, string? state, string redirectUri, Func<string, CancellationToken, Task<T>> complete)
    {
        _listener = listener; _provider = provider; _path = path; _state = state; RedirectUri = redirectUri; _complete = complete;
        _ = _wait.Task.ContinueWith(task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    public string RedirectUri { get; }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static OAuthLoopbackServer<T> Start(string providerName, string host, int port, string path, string? state,
        Func<string, CancellationToken, Task<T>> complete, CancellationToken cancellationToken, string? redirectHost = null,
        TimeSpan? timeout = null, TimeProvider? time = null)
    {
        if (cancellationToken.IsCancellationRequested) throw OAuthFlows.Cancelled(cancellationToken);
        var address = IPAddress.TryParse(host.Trim('[', ']'), out var parsed) ? parsed : host == "localhost" ? IPAddress.Loopback : Dns.GetHostAddresses(host)[0];
        var listener = new TcpListener(address, port);
        listener.Start();
        var bound = ((IPEndPoint)listener.LocalEndpoint).Port;
        var name = redirectHost ?? host;
        var server = new OAuthLoopbackServer<T>(listener, providerName, path, state, $"http://{(name.Contains(':') ? "[" + name + "]" : name)}:{bound}{path}", complete);
        server._abort = cancellationToken.Register(() => server.Finish(false, default, OAuthFlows.Cancelled(cancellationToken)));
        if (timeout is { } limit)
            server._timer = (time ?? TimeProvider.System).CreateTimer(_ => server.Finish(false, default, new TimeoutException($"{providerName} sign-in timed out")), null, limit, Timeout.InfiniteTimeSpan);
        _ = server.AcceptAsync();
        return server;
    }

    public Task<(bool Settled, T? Value)> WaitAsync() => _wait.Task;

    /// <summary>Stops waiting for the browser unless a callback is already being completed.</summary>
    public void Cancel() { lock (_gate) if (_claimed) return; Finish(false, default, null); }

    public void Close()
    {
        Finish(false, default, new InvalidOperationException("OAuth callback server closed"));
        _stop.Cancel(); _listener.Stop();
    }

    private void Finish(bool settled, T? value, Exception? error)
    {
        lock (_gate) { if (_settled) return; _settled = true; }
        _abort.Dispose(); _timer?.Dispose();
        if (error is not null) _wait.TrySetException(error); else _wait.TrySetResult((settled, value));
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false); }
            catch (Exception error) { if (!_stop.IsCancellationRequested) Finish(false, default, error); return; }
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var stream = client.GetStream();
            var buffer = new byte[16 * 1024]; var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
                if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
            }
            var line = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n")[0].Split(' ');
            var (status, html, after) = await RouteAsync(line.Length >= 2 ? line[0] : "", line.Length >= 2 ? line[1] : "/").ConfigureAwait(false);
            var body = Encoding.UTF8.GetBytes(html);
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\ncontent-type: text/html; charset=utf-8\r\n" +
                $"cache-control: no-store\r\ncontent-length: {body.Length}\r\nconnection: close\r\n\r\n");
            try
            {
                await stream.WriteAsync(head, timeout.Token).ConfigureAwait(false);
                await stream.WriteAsync(body, timeout.Token).ConfigureAwait(false);
                await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception) { /* A broken browser connection only loses its page. */ }
            after?.Invoke();
        }
        catch (Exception) { }
    }

    private async Task<(int, string, Action?)> RouteAsync(string method, string target)
    {
        var question = target.IndexOf('?');
        var pathname = question < 0 ? target : target[..question];
        var query = question < 0 ? "" : target[(question + 1)..];
        if (query.IndexOf('#') is var hash and >= 0) query = query[..hash];
        if (method != "GET" || pathname != _path) return (404, OAuthPages.Error("Callback route not found."), null);
        if (_state is not null && OAuthFlows.Query(query, "state") != _state) return (400, OAuthPages.Error("State mismatch."), null);
        string code;
        lock (_gate)
        {
            if (_claimed || _settled) return (409, OAuthPages.Error("This sign-in has already been handled."), null);
            if (OAuthFlows.Query(query, "error") is { Length: > 0 } providerError)
            {
                var description = OAuthFlows.Query(query, "error_description") ?? providerError;
                return (400, OAuthPages.Error($"{_provider} authorization failed.", description),
                    () => Finish(false, default, new InvalidOperationException($"{_provider} authorization failed: {description}")));
            }
            if (OAuthFlows.Query(query, "code") is not { Length: > 0 } received) return (400, OAuthPages.Error("Missing authorization code."), null);
            _claimed = true; code = received;
        }
        try
        {
            var value = await _complete(code, CancellationToken.None).ConfigureAwait(false);
            return (200, OAuthPages.Success($"Signed in to {_provider}. You may now close this page."), () => Finish(true, value, null));
        }
        catch (Exception error)
        {
            return (502, OAuthPages.Error($"{_provider} sign-in failed.", error.Message), () => Finish(false, default, error));
        }
    }
}

/// <summary>utils/oauth-page.ts: the success and failure pages the loopback server sends.</summary>
public static class OAuthPages
{
    private const string Logo = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 800 800\" aria-hidden=\"true\"><path fill=\"#F09082\" d=\"M165.29 165.29H517.36V400H400V282.65H165.29Z\"/><path fill=\"#4D9ABF\" d=\"M165.29 282.65H282.65V400H400V517.36H282.65V634.72H165.29Z\"/><path fill=\"#F1BE58\" d=\"M517.36 400H634.72V634.72H517.36Z\"/></svg>";
    private static string Escape(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
    public static string Success(string message) => Render("Authentication successful", "Authentication successful", message, null);
    public static string Error(string message, string? details = null) => Render("Authentication failed", "Authentication failed", message, details);
    private static string Render(string title, string heading, string message, string? details) =>
        "<!doctype html>\n<html lang=\"en\">\n<head>\n  <meta charset=\"utf-8\" />\n  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />\n" +
        $"  <title>{Escape(title)}</title>\n  <style>\n" +
        "    :root {\n      --text: #fafafa;\n      --text-dim: #a1a1aa;\n      --page-bg: #09090b;\n" +
        "      --font-sans: ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, \"Segoe UI\", Roboto, \"Helvetica Neue\", Arial, \"Noto Sans\", sans-serif, \"Apple Color Emoji\", \"Segoe UI Emoji\", \"Segoe UI Symbol\", \"Noto Color Emoji\";\n" +
        "      --font-mono: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, \"Liberation Mono\", \"Courier New\", monospace;\n    }\n" +
        "    * { box-sizing: border-box; }\n    html { color-scheme: dark; }\n    body {\n      margin: 0;\n      min-height: 100vh;\n      display: flex;\n" +
        "      align-items: center;\n      justify-content: center;\n      padding: 24px;\n      background: var(--page-bg);\n      color: var(--text);\n" +
        "      font-family: var(--font-sans);\n      text-align: center;\n    }\n    main {\n      width: 100%;\n      max-width: 560px;\n      display: flex;\n" +
        "      flex-direction: column;\n      align-items: center;\n      justify-content: center;\n    }\n    .logo {\n      width: 72px;\n      height: 72px;\n" +
        "      display: block;\n      margin-bottom: 24px;\n    }\n    h1 {\n      margin: 0 0 10px;\n      font-size: 28px;\n      line-height: 1.15;\n" +
        "      font-weight: 650;\n      color: var(--text);\n    }\n    p {\n      margin: 0;\n      line-height: 1.7;\n      color: var(--text-dim);\n" +
        "      font-size: 15px;\n    }\n    .details {\n      margin-top: 16px;\n      font-family: var(--font-mono);\n      font-size: 13px;\n" +
        "      color: var(--text-dim);\n      white-space: pre-wrap;\n      word-break: break-word;\n    }\n  </style>\n</head>\n<body>\n  <main>\n" +
        $"    <div class=\"logo\">{Logo}</div>\n    <h1>{Escape(heading)}</h1>\n    <p>{Escape(message)}</p>\n    " +
        (details is { Length: > 0 } ? $"<div class=\"details\">{Escape(details)}</div>" : "") + "\n  </main>\n</body>\n</html>";
}
