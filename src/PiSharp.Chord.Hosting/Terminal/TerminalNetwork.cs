// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/tui/credentials.ts, packages/chord/src/node/tui/launcher.ts.
// Native adapters retain explicit trusted-code and terminal ownership boundaries.
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Chord.Hosting.App;

namespace PiSharp.Chord.Hosting.Terminal;

public abstract record TerminalCredential
{
    public sealed record Token(string Value) : TerminalCredential;
    public sealed record File(string Path) : TerminalCredential;
    public sealed record Pairing(string Label, string? Save = null) : TerminalCredential;
}

/// <summary>Launcher-side credential acquisition. Neither bearer nor pairing poll tokens enter a bridge.</summary>
public static partial class TerminalCredentials
{
    public static async Task<string> ResolveAsync(TerminalCredential credentials, Uri server, Action<string> say,
        HttpClient http, CancellationToken cancellationToken = default)
    {
        string token;
        if (credentials is TerminalCredential.Token direct) token = direct.Value;
        else if (credentials is TerminalCredential.File file)
            token = await Secrets.ReadSecretAsync(file.Path).ConfigureAwait(false) ?? throw new IOException("terminal: token file is missing");
        else if (credentials is TerminalCredential.Pairing pairing)
        {
            var answer = await PostAsync(http, new(server, "/_chord/pair"), new() { ["label"] = pairing.Label }, cancellationToken).ConfigureAwait(false);
            if (answer?["code"] is not JsonValue c || !c.TryGetValue<string>(out var code) || !PairingCode().IsMatch(code) ||
                answer["poll"] is not JsonValue p || !p.TryGetValue<string>(out var poll)) throw new IOException("terminal: malformed pairing answer");
            say($"Pairing code {code}: approve it at {new Uri(server, "/_chord/pair/" + code)} (signed in as the owner).");
            for (;;)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                var polled = await PostAsync(http, new(server, "/_chord/pair/poll"), new() { ["poll"] = poll }, cancellationToken).ConfigureAwait(false);
                if (polled?["status"]?.GetValue<string>() != "approved") continue;
                token = polled["token"]?.GetValue<string>() ?? throw new IOException("terminal: malformed approved pairing");
                Validate(token);
                if (pairing.Save is not null && !await Secrets.WriteSecretAsync(pairing.Save, token, replace: false).ConfigureAwait(false))
                    throw new IOException("terminal: token destination already exists");
                break;
            }
        }
        else throw new ArgumentException("Unknown terminal credentials.", nameof(credentials));
        Validate(token); cancellationToken.ThrowIfCancellationRequested(); return token;
    }
    private static void Validate(string token)
    {
        if (token.Length is 0 or > 16384 || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl)) throw new IOException("terminal: invalid bearer token");
    }
    private static async Task<JsonNode?> PostAsync(HttpClient http, Uri uri, JsonObject body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new IOException("terminal: the server refused pairing");
        return JsonNode.Parse(await HttpTerminalServices.ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false));
    }
    [GeneratedRegex("^[A-Za-z0-9-]{4,32}$", RegexOptions.CultureInvariant)] private static partial Regex PairingCode();
}

/// <summary>Same-origin authenticated HTTP and sockets, with no redirects or client access to credentials.</summary>
public sealed partial class HttpTerminalServices : ITerminalServices
{
    public const int MaximumDownloadBytes = 8 * 1024 * 1024;
    private readonly Uri _server;
    private readonly string _token;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly object _gate = new();
    private readonly HashSet<WebSocketConnection> _sockets = [];

    public HttpTerminalServices(Uri server, string token, HttpClient? http = null)
    {
        if (!server.IsAbsoluteUri || server.Scheme is not ("http" or "https") || server.UserInfo.Length != 0) throw new ArgumentException("A server must be an HTTP(S) origin without user info.", nameof(server));
        _server = server; _token = token; _ownsHttp = http is null;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    }

    public static async Task<HttpTerminalServices> ConnectAsync(Uri server, TerminalCredential credentials,
        Action<string> say, CancellationToken cancellationToken = default)
    {
        if (!server.IsAbsoluteUri || server.Scheme is not ("http" or "https") || server.UserInfo.Length != 0)
            throw new ArgumentException("A server must be an HTTP(S) origin without user info.", nameof(server));
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        try
        {
            var token = await TerminalCredentials.ResolveAsync(credentials, server, text => say(TerminalOutput.Filter(text)), http, cancellationToken).ConfigureAwait(false);
            return new HttpTerminalServices(server, token, http, ownsHttp: true);
        }
        catch { http.Dispose(); throw; }
    }
    private HttpTerminalServices(Uri server, string token, HttpClient http, bool ownsHttp) : this(server, token, http) { _ownsHttp = ownsHttp; }

    public static string TerminalFile(string path, Uri server)
    {
        if (!Uri.TryCreate(server, path, out var uri) || uri.Scheme != server.Scheme || uri.IdnHost != server.IdnHost || uri.Port != server.Port ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || !TerminalPath().IsMatch(uri.AbsolutePath)) throw new IOException("terminal: no terminal file");
        return uri.AbsolutePath;
    }
    public Task<string> FetchAsync(string path, CancellationToken cancellationToken) => GetAsync(TerminalFile(path, _server), cancellationToken);
    public async Task<JsonNode?> BaseAsync(CancellationToken cancellationToken) => JsonNode.Parse(await GetAsync("/_chord/tui/base", cancellationToken).ConfigureAwait(false));
    private async Task<string> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_server, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new IOException($"terminal: request answered {(int)response.StatusCode}");
        return await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
    }
    internal static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaximumDownloadBytes) throw new IOException("terminal: download exceeds 8 MiB");
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var kept = new MemoryStream(); var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, MaximumDownloadBytes + 1L - kept.Length)), cancellationToken).ConfigureAwait(false);
            if (count == 0) return Encoding.UTF8.GetString(kept.GetBuffer(), 0, (int)kept.Length);
            if (kept.Length + count > MaximumDownloadBytes) throw new IOException("terminal: download exceeds 8 MiB");
            kept.Write(buffer, 0, count);
        }
    }
    public async Task<ITerminalSocket> OpenSocketAsync(CancellationToken cancellationToken)
    {
        var websocket = new ClientWebSocket(); websocket.Options.SetRequestHeader("Authorization", "Bearer " + _token);
        try
        {
            var target = new UriBuilder(new Uri(_server, "/chord")) { Scheme = _server.Scheme == "https" ? "wss" : "ws" };
            await websocket.ConnectAsync(target.Uri, cancellationToken).ConfigureAwait(false);
            var connection = new WebSocketConnection(websocket);
            lock (_gate) _sockets.Add(connection);
            connection.Closed += () => { lock (_gate) _sockets.Remove(connection); }; return connection;
        }
        catch { websocket.Dispose(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        WebSocketConnection[] sockets; lock (_gate) { sockets = _sockets.ToArray(); _sockets.Clear(); }
        foreach (var socket in sockets) await socket.DisposeAsync().ConfigureAwait(false);
        if (_ownsHttp) _http.Dispose();
    }
    [GeneratedRegex("^/_chord/tui/(?:plugin/)?[A-Za-z0-9_.~@-]+$", RegexOptions.CultureInvariant)] private static partial Regex TerminalPath();

    private sealed class WebSocketConnection(ClientWebSocket socket) : ITerminalSocket
    {
        private readonly CancellationTokenSource _stop = new(); private Task? _receiving; private int _closed;
        public event Action<ReadOnlyMemory<byte>>? Message;
        public event Action? Closed;
        public void Start() => _receiving ??= ReceiveAsync();
        public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
            await socket.SendAsync(data, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
        private async Task ReceiveAsync()
        {
            var buffer = new byte[64 * 1024]; using var message = new MemoryStream();
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var frame = await socket.ReceiveAsync(buffer.AsMemory(), _stop.Token).ConfigureAwait(false);
                    if (frame.MessageType == WebSocketMessageType.Close) break;
                    if (message.Length + frame.Count > TerminalPipe.MaximumFrameBytes) throw new IOException("terminal: oversized socket message");
                    message.Write(buffer, 0, frame.Count);
                    if (!frame.EndOfMessage) continue;
                    Message?.Invoke(message.ToArray()); message.SetLength(0);
                }
            }
            catch (Exception) { }
            finally { End(); }
        }
        private void End() { if (Interlocked.Exchange(ref _closed, 1) == 0) { socket.Abort(); Closed?.Invoke(); } }
        public ValueTask DisposeAsync()
        {
            _stop.Cancel(); End(); socket.Dispose();
            // A receive callback can own this disposal; it must never await itself.
            return ValueTask.CompletedTask;
        }
    }
}
