using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Configuration;

namespace PiSharp.Extensions.Mcp.Registration;

/// <summary>Owner-bound inert server metadata. Registration itself grants no connection authority.</summary>
public interface IMcpServerRegistrationFacade
{
    string OwnerId { get; }
    long OwnerGeneration { get; }
    void RegisterMcpServer(string name, JsonData configuration);
    void UnregisterMcpServer(string name);
    ImmutableArray<McpRegisteredServer> GetMcpServers();
}

/// <summary>Pi 1.1.0 core/extensions/loader.ts <c>pi.registerMcpServer(name, config)</c>, <c>pi.unregisterMcpServer(name)</c> and
/// <c>pi.getMcpServers()</c> on the registry a native extension receives. <c>config</c> has the shape of an <c>mcpServers</c> entry.
/// Registrations are not saved; a server in <c>mcp.json</c> with the same name takes precedence. Registering again replaces the
/// extension's earlier registration; names another extension registered, invalid names and invalid configs throw. Changes after the
/// session started reach extensions as the <c>mcp_servers_change</c> observation (<c>{ type, servers: [{ name, config, extensionPath }] }</c>).
/// Unavailable (<see cref="NotSupportedException"/>) when the host connects no MCP servers.</summary>
public interface IExtensionMcpServerRegistry : IExtensionRegistry
{
    void RegisterMcpServer(string name, JsonData configuration);
    void UnregisterMcpServer(string name);
    ImmutableArray<McpRegisteredServer> GetMcpServers();
}

/// <summary>The host's registered MCP servers (core/mcp-servers.ts McpServerRegistry) as extension owners reach them. The host
/// validates, checks ownership and connects the servers.</summary>
public interface IExtensionMcpServerHost
{
    void Register(string ownerId, string name, JsonData configuration);
    void Unregister(string ownerId, string name);
    ImmutableArray<McpRegisteredServer> List();
}

/// <summary>The admitted host publishes this composed catalog through its existing registration pipeline.
/// It must commit atomically before returning a matching receipt. Neither metadata nor this delegate
/// grants a process, HTTP client, credential, filesystem, or transport acquisition capability.</summary>
public sealed record McpRegistrationPublication(IExtensionRegistry Owner, long Revision,
    ImmutableArray<McpRegisteredServer> Previous, ImmutableArray<McpRegisteredServer> Current,
    McpServerCatalog Catalog);

public sealed record McpRegistrationPublicationReceipt(IExtensionRegistry Owner, long Revision, bool Published);
public delegate McpRegistrationPublicationReceipt McpAdmittedRegistrationPublisher(McpRegistrationPublication publication);
