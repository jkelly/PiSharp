using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using PiSharp.Chord.Host;
using PiSharp.Chord.Registries;

namespace PiSharp.Chord.Hosting.Http;

public sealed class HttpFailure(int status, string? description = null) : Exception(description ?? $"HTTP {status}")
{ public int Status { get; } = status; }
public sealed record HttpError(string Kind, Exception Error, string? Plugin = null, string? Pattern = null);
public sealed record HttpProvenance(string Kind, string? Relay = null);
public sealed record RelayAdmission(string Origin, string? Framer = null);
public delegate Task<HttpResult> HttpHandler(HttpRequestContext request, CallContext context);
public delegate Task<HttpResult> HttpWrap(HttpRequestContext request, Func<Task<HttpResult>> next, CallContext context);
public delegate Task HttpSocket(WebSocket socket, HttpRequestContext request, CallContext context);
public delegate Task<JsonObject?> HttpAuthenticator(HttpRequestContext request, ChordContext context);
public sealed record HttpRoute(HttpHandler Handler, bool Page = false, bool CrossSite = false);
public sealed record HttpWrapper(string Name, HttpWrap Wrap, string Phase = "observe", bool All = false, string? Route = null);
public sealed record HttpRelay(string Name, Func<HttpRequest, ChordContext, Task<RelayAdmission?>> Verify);
public sealed class HttpContribution
{
    public IReadOnlyDictionary<string, HttpRoute> Routes { get; init; } = new Dictionary<string, HttpRoute>();
    public IReadOnlyDictionary<string, HttpSocket> Sockets { get; init; } = new Dictionary<string, HttpSocket>();
    public IReadOnlyList<HttpWrapper> Wraps { get; init; } = [];
    public IReadOnlyList<string> Hosts { get; init; } = [];
    public IReadOnlyList<string> Framers { get; init; } = [];
    public IReadOnlyList<HttpRelay> Relays { get; init; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Csp { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
    public int MaxPayload { get; init; } = 1024 * 1024;
}
public static class HttpRegistries
{
    public static Registry<HttpContribution, HttpRouter> Http { get; } = Registry.Define<HttpContribution, HttpRouter>("chord.http", new() { Version = 1 });
    public static Registry<HttpAuthenticator, IReadOnlyList<HttpAuthenticator>> Authenticators { get; } = Registry.Define<HttpAuthenticator, IReadOnlyList<HttpAuthenticator>>("chord.http.authenticators", new() { Version = 1 });
}
public sealed class ChordHttpOptions
{
    /// <summary>Core application configuration, fixed for this listener's lifetime.</summary>
    public Action<WebApplicationBuilder>? Configure { get; init; }
    public IPAddress Address { get; init; } = IPAddress.Loopback;
    public int? Port { get; init; }
    public string? LinksPath { get; init; }
    public Func<HttpRequestContext, JsonObject, LinkOptions>? LinkOptions { get; init; }
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public required Action<HttpError> OnError { get; init; }
}

public sealed class HttpRequestContext
{
    internal HttpContext Native { get; }
    internal HttpRequestContext(HttpContext native, string origin, HttpProvenance provenance, IReadOnlyDictionary<string, string> parameters, string? framer = null)
    { Native = native; Origin = origin; Provenance = provenance; Params = parameters; Framer = framer; }
    public string Method => Native.Request.Method;
    public string Path => Native.Request.Path.Value ?? "/";
    public IReadOnlyDictionary<string, string> Params { get; }
    public IQueryCollection Query => Native.Request.Query;
    public IHeaderDictionary Headers => Native.Request.Headers;
    public string Origin { get; }
    public HttpProvenance Provenance { get; }
    public string? Framer { get; }
    public string Nonce { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
    public async Task<byte[]> BodyAsync(long limit = 1024 * 1024, CancellationToken cancellationToken = default)
    {
        if (limit < 0) throw new ArgumentOutOfRangeException(nameof(limit));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Native.RequestAborted);
        if (Native.Request.ContentLength > limit) throw new HttpFailure(413);
        using var output = new MemoryStream(); var buffer = new byte[8192];
        try
        {
            int count;
            while ((count = await Native.Request.Body.ReadAsync(buffer, linked.Token).ConfigureAwait(false)) != 0)
            { if (output.Length > limit - count) throw new HttpFailure(413); output.Write(buffer, 0, count); }
            if (Native.Request.ContentLength is { } expected && output.Length != expected) throw new HttpFailure(400);
        }
        catch (IOException) { throw new HttpFailure(400); }
        return output.ToArray();
    }
    public async Task<JsonNode?> JsonAsync(long limit = 1024 * 1024, CancellationToken cancellationToken = default)
    {
        try { return JsonNode.Parse(await BodyAsync(limit, cancellationToken).ConfigureAwait(false)); }
        catch (System.Text.Json.JsonException) { throw new HttpFailure(400); }
    }
}

/// <summary>Typed native response descriptions; body readers run while the request's Chord root is alive.</summary>
public abstract record HttpResult
{
    public int Status { get; init; } = 200;
    public IReadOnlyDictionary<string, string[]> Headers { get; init; } = new Dictionary<string, string[]>();
    public sealed record Json(JsonNode? Value) : HttpResult;
    public sealed record Html(string Body) : HttpResult;
    public sealed record Text(string Body, string Mime = "text/plain; charset=utf-8") : HttpResult;
    public sealed record Redirect(string Location) : HttpResult;
    public sealed record Empty : HttpResult;
    public sealed record Bytes(Func<long, int, ChordContext, Task<ReadOnlyMemory<byte>>> Read, long? Size = null, string Mime = "application/octet-stream", string? Download = null, bool Inline = false, bool Ranges = false) : HttpResult;
    public sealed record File(string Path, string? Root = null, string? Download = null, bool Inline = false, bool Ranges = false, string? Mime = null) : HttpResult;
}
