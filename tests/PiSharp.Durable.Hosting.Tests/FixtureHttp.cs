using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

/// <summary>A request a <see cref="FixtureServer"/> received.</summary>
internal sealed record FixtureRequest(string Path, IReadOnlyDictionary<string, string> Headers)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>A fixture response; <see cref="Headers"/> are sent as given, plus <c>Content-Length</c> unless one is there.</summary>
internal sealed record FixtureResponse(int Status, byte[] Body, IReadOnlyDictionary<string, string>? Headers = null)
{
    public static FixtureResponse Text(string body, string contentType = "application/xml", IReadOnlyDictionary<string, string>? headers = null) =>
        new(200, Encoding.UTF8.GetBytes(body), new Dictionary<string, string>(headers ?? new Dictionary<string, string>()) { ["Content-Type"] = contentType });

    public static FixtureResponse Code(int status, IReadOnlyDictionary<string, string>? headers = null) => new(status, [], headers);
}

/// <summary>An HTTP/1.1 server on 127.0.0.1 for fixture responses: one request per connection, answered by <see cref="Handler"/>, which
/// may wait (for example until a test closes the device).</summary>
internal sealed class FixtureServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;

    public FixtureServer(Func<FixtureRequest, Task<FixtureResponse>> handler)
    {
        Handler = handler;
        listener.Start();
        loop = Task.Run(AcceptAsync);
    }

    public FixtureServer(Func<FixtureRequest, FixtureResponse> handler) : this(request => Task.FromResult(handler(request))) { }

    public Func<FixtureRequest, Task<FixtureResponse>> Handler { get; set; }

    public ConcurrentQueue<FixtureRequest> Requests { get; } = new();

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    /// <summary><c>http://127.0.0.1:port/</c>.</summary>
    public Uri Base => new($"http://127.0.0.1:{Port}/");

    public string Url(string path) => new Uri(Base, path).ToString();

    public int Count(string pathPrefix) => Requests.Count(request => request.Path.StartsWith(pathPrefix, StringComparison.Ordinal));

    private async Task AcceptAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token); }
            catch (Exception) when (stop.IsCancellationRequested) { return; }
            catch (SocketException) { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new List<byte>();
                var chunk = new byte[4096];
                while (!EndsHeaders(buffer))
                {
                    var read = await stream.ReadAsync(chunk, stop.Token);
                    if (read == 0) return;
                    buffer.AddRange(chunk.AsSpan(0, read).ToArray());
                    if (buffer.Count > 64 * 1024) return;
                }
                var lines = Encoding.ASCII.GetString([.. buffer]).Split("\r\n");
                var path = lines[0].Split(' ')[1];
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1).TakeWhile(line => line.Length > 0))
                {
                    var colon = line.IndexOf(':');
                    if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
                var request = new FixtureRequest(path, headers);
                Requests.Enqueue(request);
                var response = await Handler(request);
                var head = new StringBuilder($"HTTP/1.1 {response.Status} {Reason(response.Status)}\r\nConnection: close\r\n");
                var sent = response.Headers ?? new Dictionary<string, string>();
                foreach (var (name, value) in sent) head.Append(name).Append(": ").Append(value).Append("\r\n");
                if (!sent.ContainsKey("Content-Length")) head.Append("Content-Length: ").Append(response.Body.Length).Append("\r\n");
                head.Append("\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), stop.Token);
                await stream.WriteAsync(response.Body, stop.Token);
                await stream.FlushAsync(stop.Token);
            }
            catch (Exception) when (stop.IsCancellationRequested) { }
            catch (IOException) { }
        }
    }

    private static bool EndsHeaders(List<byte> buffer) =>
        buffer.Count >= 4 && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n';

    private static string Reason(int status) => status switch
    {
        200 => "OK", 301 => "Moved Permanently", 302 => "Found", 304 => "Not Modified", 403 => "Forbidden", 404 => "Not Found",
        429 => "Too Many Requests", 503 => "Service Unavailable", _ => "Status",
    };

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Stop();
        try { await loop; } catch (Exception) when (stop.IsCancellationRequested) { }
        stop.Dispose();
    }
}
