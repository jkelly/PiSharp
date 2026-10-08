// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/mcp/src/oauth/callback.ts OAuthCallbackServer,
// packages/coding-agent/src/extensions/mcp/oauth.ts listenForCallback (pages from packages/ai/src/utils/oauth-page.ts, text only).
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PiSharp.Cli.Mcp.Authentication;

/// <summary>The authorization response the browser delivered.</summary>
public sealed record McpOAuthCallback(string Code, string State, string? Iss);

/// <summary>Loopback HTTP server receiving OAuth authorization responses. Port 0 picks a free port. Each wait is for
/// one `state`; with a path, a response on another of the server's paths fails, so a server-specific redirect URI
/// tells authorization servers apart (RFC 9700 section 4.4.2.2).</summary>
public sealed class McpOAuthCallbackServer : IAsyncDisposable
{
    private const int MaximumRequestBytes = 16 * 1024;
    private static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(10);
    /// <summary>The original's default: a wait fails after five minutes.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    private readonly TcpListener listener;
    private readonly string[] paths;
    private readonly TimeSpan timeout;
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private readonly Dictionary<string, (TaskCompletionSource<McpOAuthCallback> Result, string? Path, CancellationTokenSource Timer)> pending = new(StringComparer.Ordinal);
    private Task? accepting;

    private McpOAuthCallbackServer(TcpListener listener, string redirectUrl, string[] paths, TimeSpan timeout)
    { this.listener = listener; RedirectUrl = redirectUrl; this.paths = paths; this.timeout = timeout; }

    public string RedirectUrl { get; }
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    /// <summary>Listen on <paramref name="host"/> (default 127.0.0.1); <paramref name="redirectHost"/> names it in the redirect URL.</summary>
    public static McpOAuthCallbackServer Listen(string host = "127.0.0.1", int port = 0, string path = "/callback",
        string? redirectHost = null, IEnumerable<string>? extraPaths = null, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(host); ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfNegative(port); ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        var address = IPAddress.TryParse(host.Trim('[', ']'), out var parsed) ? parsed
            : host == "localhost" ? IPAddress.Loopback : throw new ArgumentException("The OAuth callback listens on a loopback address.", nameof(host));
        var listener = new TcpListener(address, port);
        listener.Start();
        var name = redirectHost ?? host;
        var server = new McpOAuthCallbackServer(listener,
            $"http://{(name.Contains(':') && !name.StartsWith('[') ? "[" + name + "]" : name)}:{((IPEndPoint)listener.LocalEndpoint).Port}{path}",
            [path, .. extraPaths ?? []], timeout ?? DefaultTimeout);
        server.accepting = server.AcceptAsync();
        return server;
    }

    /// <summary>Wait for the authorization response with <paramref name="state"/>.</summary>
    public Task<McpOAuthCallback> WaitForCallbackAsync(string state, string? path = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(state);
        lock (gate)
        {
            if (pending.ContainsKey(state)) throw new InvalidOperationException("OAuth state is already pending");
            if (stop.IsCancellationRequested) throw new InvalidOperationException("OAuth callback server closed");
            var result = new TaskCompletionSource<McpOAuthCallback>(TaskCreationOptions.RunContinuationsAsynchronously);
            var timer = new CancellationTokenSource(timeout);
            timer.Token.Register(() => { if (Take(state) is { } entry) entry.Result.TrySetException(new TimeoutException("OAuth callback timed out")); });
            pending.Add(state, (result, path, timer));
            return result.Task;
        }
    }

    public async ValueTask DisposeAsync()
    {
        (TaskCompletionSource<McpOAuthCallback> Result, string? Path, CancellationTokenSource Timer)[] waiting;
        lock (gate)
        {
            if (stop.IsCancellationRequested) return;
            stop.Cancel(); waiting = [.. pending.Values]; pending.Clear();
        }
        foreach (var entry in waiting) { entry.Timer.Dispose(); entry.Result.TrySetException(new InvalidOperationException("OAuth callback server closed")); }
        listener.Stop();
        if (accepting is { } running) await running.ConfigureAwait(false);
    }

    private (TaskCompletionSource<McpOAuthCallback> Result, string? Path, CancellationTokenSource Timer)? Take(string state)
    {
        lock (gate)
        {
            if (!pending.Remove(state, out var entry)) return null;
            entry.Timer.Dispose(); return entry;
        }
    }

    private async Task AcceptAsync()
    {
        var handlers = new List<Task>();
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false); }
            catch (Exception) { break; }
            handlers.RemoveAll(task => task.IsCompleted);
            handlers.Add(HandleAsync(client));
        }
        await Task.WhenAll(handlers).ConfigureAwait(false);
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            using var read = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            read.CancelAfter(RequestReadTimeout);
            var stream = client.GetStream();
            var buffer = new byte[MaximumRequestBytes]; var length = 0; var end = -1;
            while (end < 0 && length < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(length), read.Token).ConfigureAwait(false);
                if (count == 0) break;
                length += count;
                end = Encoding.ASCII.GetString(buffer, 0, length).IndexOf("\r\n\r\n", StringComparison.Ordinal);
            }
            var line = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n")[0].Split(' ');
            var (status, ok, message, details) = Handle(line.Length >= 2 ? line[1] : "/");
            var body = Encoding.UTF8.GetBytes(ok ? Page("Authentication successful", "Signed in to the MCP server. You may now close this page.", null)
                : Page("Authentication failed", message!, details));
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : status == 404 ? "Not Found" : "Bad Request")}\r\n" +
                $"content-type: text/html; charset=utf-8\r\ncache-control: no-store\r\ncontent-length: {body.Length}\r\nconnection: close\r\n\r\n");
            await stream.WriteAsync(head, read.Token).ConfigureAwait(false);
            await stream.WriteAsync(body, read.Token).ConfigureAwait(false);
            await stream.FlushAsync(read.Token).ConfigureAwait(false);
        }
        catch (Exception) { /* A broken browser connection only loses its page. */ }
    }

    private (int Status, bool Ok, string? Message, string? Details) Handle(string target)
    {
        var question = target.IndexOf('?');
        var pathname = question < 0 ? target : target[..question];
        var query = question < 0 ? "" : target[(question + 1)..];
        if (query.IndexOf('#') is var hash and >= 0) query = query[..hash];
        if (!paths.Contains(pathname, StringComparer.Ordinal)) return (404, false, "Not found", null);
        var state = Query(query, "state");
        if (string.IsNullOrEmpty(state) || Take(state) is not { } entry) return (400, false, "Invalid or expired OAuth state", null);
        if (entry.Path is not null && pathname != entry.Path)
        {
            entry.Result.TrySetException(new InvalidOperationException("The authorization response arrived on another redirect URI"));
            return (400, false, "Unexpected redirect URI", null);
        }
        if (Query(query, "error") is { Length: > 0 } error)
        {
            var description = Query(query, "error_description") ?? error;
            entry.Result.TrySetException(new InvalidOperationException(description));
            return (200, false, "Authorization failed. You may close this window.", description);
        }
        if (Query(query, "code") is not { Length: > 0 } code)
        {
            entry.Result.TrySetException(new InvalidOperationException("OAuth callback did not include an authorization code"));
            return (400, false, "Missing authorization code", null);
        }
        var iss = Query(query, "iss");
        entry.Result.TrySetResult(new(code, state, string.IsNullOrEmpty(iss) ? null : iss));
        return (200, true, null, null);
    }

    /// <summary>`URLSearchParams.get`: the first value of <paramref name="name"/>, `+` as space, percent-decoded.</summary>
    internal static string? Query(string query, string name)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (Decode(parts[0]) == name) return parts.Length == 2 ? Decode(parts[1]) : "";
        }
        return null;
        static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
    }

    private static string Page(string heading, string message, string? details) =>
        $"<!doctype html>\n<html lang=\"en\">\n<head>\n  <meta charset=\"utf-8\" />\n  <title>{WebUtility.HtmlEncode(heading)}</title>\n</head>\n" +
        $"<body>\n  <main>\n    <h1>{WebUtility.HtmlEncode(heading)}</h1>\n    <p>{WebUtility.HtmlEncode(message)}</p>\n" +
        (string.IsNullOrEmpty(details) ? "" : $"    <pre>{WebUtility.HtmlEncode(details)}</pre>\n") + "  </main>\n</body>\n</html>\n";
}
