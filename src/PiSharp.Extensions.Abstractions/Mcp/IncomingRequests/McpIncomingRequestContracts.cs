using PiSharp.Contracts;

namespace PiSharp.Extensions.Mcp.IncomingRequests;

public delegate ValueTask<JsonData?> McpIncomingRequestHandler(JsonData? parameters, CancellationToken token);
public delegate Task McpAdmittedIncomingResponseSender(JsonData response, CancellationToken token);

/// <summary>Transferred channel dependency; registration conveys no new host authority.</summary>
public interface IMcpIncomingRequestRegistry
{
    Action SetRequestHandler(string method, McpIncomingRequestHandler handler);
    Task DispatchAsync(JsonData request, CancellationToken token = default);
    bool CancelIncoming(JsonData id);
    Task CloseAsync();
}

/// <summary>Original callback/send evidence is retained in full, without aggregate flattening.</summary>
public sealed class McpIncomingOriginalException(string phase, Task? original, Exception evidence)
    : IOException("MCP incoming " + phase + " failed.", evidence)
{
    public string Phase { get; } = phase;
    public Task? Original { get; } = original;
    public Exception Evidence { get; } = evidence;
}
