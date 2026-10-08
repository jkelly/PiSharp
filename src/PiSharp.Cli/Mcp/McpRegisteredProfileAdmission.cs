using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.IncomingRequests;
using PiSharp.Extensions.Mcp.Roots;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Mcp.Transport;
using PiSharp.Extensions.Runtime.Mcp.Transport;

namespace PiSharp.Cli.Mcp;

/// <summary>Host-owned registration admission for the ordinary profile/command acquisition path.
/// Captured metadata never creates a connection, client, credential or process admission.</summary>
public sealed class McpRegisteredProfileAdmission
{
    public McpExtensionRegistrationBridge Bridge { get; }
    public McpRegisteredProfileRuntimeAdmission AcquireRuntime { get; }
    public McpRegisteredProfileAdmission(McpExtensionRegistrationBridge bridge,
        McpRegisteredProfileRuntimeAdmission acquireRuntime)
    {
        ArgumentNullException.ThrowIfNull(bridge); ArgumentNullException.ThrowIfNull(acquireRuntime);
        if (acquireRuntime.GetInvocationList().Length != 1)
            throw new ArgumentException("One admitted profile runtime owner is required.", nameof(acquireRuntime));
        Bridge = bridge; AcquireRuntime = acquireRuntime;
    }

    /// <summary>Use directly with existing SessionCommands/RpcSessionCommand mcpAdmission input.
    /// The ordinary profile retains its exact native registry/policy/resource/view-owner guards.</summary>
    public McpProfileRuntimeAdmission CreateProfileAdmission() => (cwd, generation, native, policy, token) =>
        Bridge.CreateRuntimeAcquisition((currentCwd, currentGeneration, catalog, currentToken) =>
            AcquireRuntime(currentCwd, currentGeneration, native, policy, catalog, currentToken))(cwd, generation, token);
}

public sealed record McpRegisteredIncomingRequest(string Method, McpIncomingRequestHandler Handler);
public delegate ValueTask<IMcpWireTransport> McpRegisteredWireAcquisition(McpServerEntry exactEntry, CancellationToken token);

/// <summary>One explicit host wire dependency plus callback capabilities, installed on the actual
/// JSON-RPC channel before start. Roots are metadata only; handler registration grants no authority.</summary>
public sealed class McpRegisteredServerWireAdmission
{
    private readonly McpRegisteredWireAcquisition acquire;
    private readonly McpAdmittedDynamicRootsProvider? roots;
    private readonly ImmutableArray<McpRegisteredIncomingRequest> handlers;
    private readonly McpTransportLimits limits;
    public McpRegisteredServerWireAdmission(McpRegisteredWireAcquisition acquire,
        McpAdmittedDynamicRootsProvider? dynamicRoots = null,
        ImmutableArray<McpRegisteredIncomingRequest> incomingRequests = default,
        McpTransportLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(acquire);
        if (acquire.GetInvocationList().Length != 1 || dynamicRoots?.GetInvocationList().Length > 1)
            throw new ArgumentException("One owning wire and roots callback required.");
        var copied = incomingRequests.IsDefault ? ImmutableArray<McpRegisteredIncomingRequest>.Empty : incomingRequests.ToImmutableArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (copied.Length > 128) throw new ArgumentException("Bounded incoming handlers required.");
        foreach (var handler in copied)
        {
            ArgumentNullException.ThrowIfNull(handler); ArgumentNullException.ThrowIfNull(handler.Handler);
            if (string.IsNullOrWhiteSpace(handler.Method) || handler.Method.Length > 256 || handler.Method.Any(char.IsControl) ||
                !names.Add(handler.Method) || handler.Handler.GetInvocationList().Length != 1 ||
                dynamicRoots is not null && handler.Method == "roots/list")
                throw new ArgumentException("Unique single admitted incoming handler required.");
        }
        this.acquire = acquire; roots = dynamicRoots; handlers = copied; this.limits = limits ?? new();
    }

    public McpAdmittedChannelFactory CreateChannelFactory(McpServerEntry exactEntry)
    {
        ArgumentNullException.ThrowIfNull(exactEntry);
        return async (entry, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(entry, exactEntry)) throw new InvalidOperationException("Wire admission belongs to the exact committed server entry.");
            Task<IMcpWireTransport>? original = null; IMcpWireTransport wire;
            try { original = acquire(entry, token).AsTask(); wire = await original.ConfigureAwait(false); }
            catch (Exception error)
            {
                if (original is { IsFaulted: true }) throw new McpFactoryDisposalException("wire acquisition", original, original.Exception!);
                if (error is OperationCanceledException canceled &&
                    !(original?.IsCanceled == true && token.IsCancellationRequested && canceled.CancellationToken == token))
                    throw new McpFactoryDisposalException("unowned wire acquisition cancellation", original, error);
                throw;
            }
            ArgumentNullException.ThrowIfNull(wire);
            McpJsonRpcRequestChannel? channel = null;
            try
            {
                channel = new(wire, limits);
                if (roots is not null) channel.ConfigureDynamicRoots(roots);
                foreach (var handler in handlers) channel.SetRequestHandler(handler.Method, handler.Handler);
                return channel;
            }
            catch (Exception setup)
            {
                Task? close = null;
                try { close = channel is null ? wire.CloseAsync() : channel.CloseAsync(); await close.ConfigureAwait(false); }
                catch (Exception cleanup)
                {
                    throw new AggregateException(setup, new McpFactoryDisposalException("registered wire setup", close,
                        close?.IsFaulted == true ? close.Exception! : cleanup));
                }
                throw;
            }
        };
    }
}
