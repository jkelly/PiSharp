using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.Extensions.Mcp.Transport;

/// <summary>Host-admitted independent process lease. Input writes stdin; Output reads stdout. Streams remain borrowed.</summary>
public interface IMcpAdmittedDuplexLease
{
    Stream Input { get; }
    Stream Output { get; }
    Task StartAsync(CancellationToken cancellationToken);
    /// <summary>Request stop/unblock pipes, then join owned process/pipe/stderr originals. Stable Task/fault.</summary>
    Task CloseAsync();
}
/// <summary>Explicit admitted send. Return/failure joins the original send; caller transfers response ownership.</summary>
public delegate ValueTask<HttpResponseMessage> McpHttpExchange(HttpRequestMessage request, CancellationToken cancellationToken);

/// <summary>A fresh request-specific lease. Creation has no effects. Stop initiates physical abort
/// before joining the actual SendAsync/header original, and fences any later send. It must not dispose
/// a borrowed global client. Repeated StopAsync calls return the same original Task.</summary>
public interface IMcpAdmittedHttpRequestOperation
{
    ValueTask<HttpResponseMessage> SendAsync(CancellationToken cancellationToken);
    Task StopAsync();
}
public delegate IMcpAdmittedHttpRequestOperation McpAdmittedHttpRequestFactory(HttpRequestMessage request);
public sealed record McpTransportLimits(int MaximumFrameBytes = 16 * 1024 * 1024, int ReadBufferBytes = 4096,
    int MaximumInflight = 64, int MaximumCallbacks = 64, long MaximumRequestId = 9_007_199_254_740_991);
/// <summary>All HTTP stream/session effects opt in explicitly. Resumption reopens GET only; never replays POST.</summary>
public sealed record McpHttpProfile(bool OpenGetStream, bool DeleteSessionOnClose, int MaximumReconnects = 0,
    int InitialReconnectMilliseconds = 1000, int MaximumReconnectMilliseconds = 30000, int DeleteTimeoutMilliseconds = 1000);
public sealed record McpHttpBinding(Uri Endpoint, ImmutableDictionary<string, string> Headers, McpHttpProfile Profile);
/// <summary>Receiver is a bounded quick dispatch, not an awaited lifecycle callback. Owner retains dispatched originals.</summary>
public sealed record McpWireCallbacks(Func<JsonData, ValueTask> Receive, Action<Exception> Disconnected);
public interface IMcpWireTransport
{
    Task StartAsync(McpWireCallbacks callbacks, CancellationToken cancellationToken);
    Task SendAsync(JsonData message, CancellationToken cancellationToken);
    Task SetProtocolVersionAsync(string version, CancellationToken cancellationToken);
    Task CloseAsync();
}
public delegate ValueTask McpNotificationHandler(string method, JsonData? parameters, CancellationToken cancellationToken);
public sealed class McpRemoteErrorException(double code, string message, JsonData? data = null) : IOException(message)
{
    public double Code { get; } = code;
    public new JsonData? Data { get; } = data;
}
public sealed class McpRequestTimeoutException(double milliseconds) : TimeoutException("MCP request timed out")
{
    public double TimeoutMilliseconds { get; } = milliseconds;
}
public sealed class McpHttpStatusException(int statusCode, bool sessionExpired = false) : IOException("MCP HTTP status " + statusCode)
{
    public int StatusCode { get; } = statusCode;
    public bool SessionExpired { get; } = sessionExpired;
}
