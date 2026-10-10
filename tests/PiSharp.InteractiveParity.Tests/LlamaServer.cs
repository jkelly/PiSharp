using System.Net;
using System.Net.Sockets;
using System.Text;

/// <summary>A fake llama.cpp router (or Hugging Face API) on 127.0.0.1: an HTTP/1.1 server answering each request from <see cref="Handle"/>,
/// one request per connection, recording every request with its body; <c>GET /models/sse</c> stays open as an event stream that
/// <see cref="Send"/> writes to. Authored for these cases; nothing is captured from a real server.</summary>
internal sealed class LlamaServer : IDisposable
{
    internal sealed record Request(string Method, string Target, string Path, IReadOnlyDictionary<string, string> Headers, string Body)
    {
        public string? Header(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out var value) ? value : null;
        public override string ToString() => $"{Method} {Target}" + (Body.Length > 0 ? " " + Body : "");
    }
    internal sealed record Response(int Status, string Body, string ContentType = "application/json");

    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly List<Stream> streams = [];
    private readonly object gate = new();
    public List<Request> Requests { get; } = [];
    public Func<Request, Response> Handle { get; set; }
    /// <summary>Whether <c>GET /models/sse</c> opens an event stream (otherwise it goes to <see cref="Handle"/>).</summary>
    public bool Events { get; set; } = true;
    public string Url { get; }

    public LlamaServer(Func<Request, Response>? handle = null)
    {
        Handle = handle ?? (_ => new(404, ""));
        listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        _ = Task.Run(Loop);
    }

    public static Response Json(string json) => new(200, json);
    public static Response NotFound() => new(404, "");

    /// <summary>The recorded requests other than the event stream, as "METHOD target[ body]".</summary>
    public string[] Seen() { lock (gate) return [.. Requests.Where(request => request.Path != "/models/sse").Select(request => request.ToString())]; }

    public int OpenStreams { get { lock (gate) return streams.Count; } }

    /// <summary>Writes one <c>data:</c> event to every open event stream.</summary>
    public void Send(string json)
    {
        Stream[] open; lock (gate) open = [.. streams];
        var frame = Encoding.UTF8.GetBytes("data: " + json + "\n\n");
        foreach (var stream in open)
        {
            try { lock (stream) { stream.Write(frame); stream.Flush(); } } catch (Exception) { }
        }
    }

    private async Task Loop()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token); } catch (Exception) { return; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var head = new List<byte>(); var one = new byte[1];
                while (!(head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
                {
                    if (await stream.ReadAsync(one, stop.Token) != 1) return;
                    head.Add(one[0]);
                }
                var lines = Encoding.ASCII.GetString([.. head]).Split("\r\n");
                var parts = lines[0].Split(' ');
                var headers = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var line in lines.Skip(1).Where(line => line.Contains(':', StringComparison.Ordinal)))
                    headers[line[..line.IndexOf(':', StringComparison.Ordinal)].Trim().ToLowerInvariant()] = line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
                var length = headers.TryGetValue("content-length", out var declared) ? int.Parse(declared, System.Globalization.CultureInfo.InvariantCulture) : 0;
                var body = new byte[length]; var read = 0;
                while (read < length) { var count = await stream.ReadAsync(body.AsMemory(read), stop.Token); if (count == 0) break; read += count; }
                var target = parts[1];
                var request = new Request(parts[0], target, target.Split('?')[0], headers, Encoding.UTF8.GetString(body, 0, read));
                lock (gate) Requests.Add(request);
                if (Events && request.Path == "/models/sse")
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n"), stop.Token);
                    await stream.FlushAsync(stop.Token);
                    lock (gate) streams.Add(stream);
                    try
                    {
                        // The stream stays open until the client goes away or the server stops.
                        while (!stop.IsCancellationRequested && await stream.ReadAsync(one, stop.Token) > 0) { }
                    }
                    catch (Exception) { }
                    finally { lock (gate) streams.Remove(stream); }
                    return;
                }
                Response response;
                try { response = Handle(request); } catch (Exception error) { response = new(500, "{\"error\":{\"message\":\"" + error.Message.Replace("\"", "'", StringComparison.Ordinal) + "\"}}"); }
                var payload = Encoding.UTF8.GetBytes(response.Body);
                var reason = response.Status switch { 200 => "OK", 404 => "Not Found", 429 => "Too Many Requests", 500 => "Internal Server Error", _ => "Status" };
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {response.Status} {reason}\r\nContent-Type: {response.ContentType}\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n"), stop.Token);
                await stream.WriteAsync(payload, stop.Token);
                await stream.FlushAsync(stop.Token);
            }
            catch (Exception) { }
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
        lock (gate) { foreach (var stream in streams) try { stream.Dispose(); } catch (Exception) { } streams.Clear(); }
    }
}
