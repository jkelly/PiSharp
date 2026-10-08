using PiSharp.Contracts;

namespace PiSharp.Extensions.Mcp.Roots;

/// <summary>One host-admitted metadata provider, evaluated separately for each roots/list request.
/// Returned root URIs neither grant filesystem permissions nor cause filesystem access.</summary>
public delegate ValueTask<JsonData> McpAdmittedDynamicRootsProvider(CancellationToken cancellationToken);

/// <summary>Request-handler seam. The owning admitted channel must advertise roots capability,
/// route roots/list to this handler, serialize its result and own the physical response send.</summary>
public interface IMcpDynamicRootsHandler
{
    Task<JsonData> HandleRootsListAsync(CancellationToken cancellationToken = default);
    Task CloseAsync();
}

/// <summary>Retains the exact captured callback Task and its complete unflattened original aggregate.
/// A synchronous callback throw has no Task original.</summary>
public sealed class McpRootsCallbackException(Task? original, Exception evidence)
    : IOException("MCP dynamic roots callback failed.", evidence)
{
    public Task? Original { get; } = original;
    public Exception Evidence { get; } = evidence;
}

public sealed class McpRootsCallbackCancellation(Task original, OperationCanceledException evidence)
    : OperationCanceledException("MCP dynamic roots original callback canceled.", evidence, evidence.CancellationToken)
{
    public Task Original { get; } = original;
}
