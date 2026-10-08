using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Configuration;

namespace PiSharp.Extensions.Mcp.Runtime;

public sealed record McpProgress(double Progress, double? Total = null, string? Message = null);
public delegate ValueTask McpProgressCallback(McpProgress progress, CancellationToken cancellationToken);
public sealed record McpInvocationIdentity(string OwnerId, long OwnerGeneration, long SessionGeneration,
    string ToolCallId, string? ParentToolCallId);
public sealed record McpRequestOptions(double TimeoutMilliseconds, McpProgressCallback? OnProgress = null)
{
    public int MaximumResponseBytes { get; init; } = 1_048_576;
    /// <summary>Native captured identity, not an MCP wire parameter or remote permission grant.</summary>
    public McpInvocationIdentity? InvocationIdentity { get; init; }
}

/// <summary>
/// One explicitly admitted MCP request channel lease. Its adapter owns JSON-RPC framing, correlation,
/// physical sends/cancellation writes, progress-reset timeout and incoming requests. Returned operations
/// must join those originals and admitted progress callbacks, including cancellation/failure cleanup.
/// No implementation may abandon a physical request merely because an await was canceled.
/// </summary>
public interface IMcpAdmittedRequestChannel
{
    ValueTask StartAsync(CancellationToken cancellationToken);
    ValueTask ConfigureRootsAsync(JsonData roots, CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException("This admitted channel has no roots/list handler."));
    ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken cancellationToken);
    ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken cancellationToken);
    ValueTask SetProtocolVersionAsync(string protocolVersion, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    /// <summary>Closes only this lease; joins its physical originals. Repeated calls return the same Task.</summary>
    Task CloseAsync();
}

/// <summary>Opaque retained retirement original. Joining from its borrowed callback must reject
/// before effects; outside that callback repeated joins return the same original Task.</summary>
public interface IMcpAdmittedChannelRetirement { Task JoinAsync(); }
/// <summary>Optional channel-owned notification retirement. Queueing is synchronous and effect-free
/// with respect to physical close; the owner starts close only after the exact notification original
/// returns. External CloseAsync must join the queued retirement and all cleanup originals.</summary>
public interface IMcpNotificationRetirementChannel
{
    bool IsInNotificationCallback { get; }
    IMcpAdmittedChannelRetirement RetireAfterNotification();
}

/// <summary>Transfers a fresh independently closeable admitted lease, never ownership of a global client/process.</summary>
public delegate ValueTask<IMcpAdmittedRequestChannel> McpAdmittedChannelFactory(McpServerEntry entry, CancellationToken cancellationToken);
public sealed record McpRuntimeLimits(int MaximumPages = 1000, int MaximumTools = 4096,
    int MaximumSchemaBytes = 262_144, int MaximumResponseBytes = 1_048_576, int MaximumCatalogBytes = 8_388_608);
public sealed record McpRuntimeOptions(long Generation, string ClientVersion, JsonData? Roots = null)
{
    public McpRuntimeLimits Limits { get; init; } = new();
}
public sealed record McpRuntimeSnapshot(long Generation, long Revision, McpServerToolSnapshot Catalog,
    JsonData? InitializeResult, ImmutableArray<JsonData> OriginalTools);
public sealed record McpCatalogPublication(McpRuntimeSnapshot Current, ImmutableArray<McpPlannedTool> Tools,
    ImmutableArray<McpPlannedTool> WithdrawnTools);
public sealed record McpCatalogPublicationReceipt(long Generation, long Revision, bool Published);
/// <summary>
/// The host validates generation/admission and atomically commits through its existing registry/tool pipeline.
/// Return a matching receipt only after original publication/reporting work joins. Failed/uncertain effects
/// are not automatically retried by the bridge. Host lifecycle/publication approval remains external.
/// </summary>
public delegate ValueTask<McpCatalogPublicationReceipt> McpCatalogPublisher(McpCatalogPublication publication, CancellationToken cancellationToken);

public sealed class McpRuntimeProtocolException(string message) : IOException(message);
/// <summary>Definite disconnected channel. Tool calls that may have executed are never replayed by this bridge.</summary>
public sealed class McpRuntimeDisconnectedException(string message) : IOException(message);
