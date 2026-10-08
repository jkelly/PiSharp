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

/// <summary>The admitted host publishes this composed catalog through its existing registration pipeline.
/// It must commit atomically before returning a matching receipt. Neither metadata nor this delegate
/// grants a process, HTTP client, credential, filesystem, or transport acquisition capability.</summary>
public sealed record McpRegistrationPublication(IExtensionRegistry Owner, long Revision,
    ImmutableArray<McpRegisteredServer> Previous, ImmutableArray<McpRegisteredServer> Current,
    McpServerCatalog Catalog);

public sealed record McpRegistrationPublicationReceipt(IExtensionRegistry Owner, long Revision, bool Published);
public delegate McpRegistrationPublicationReceipt McpAdmittedRegistrationPublisher(McpRegistrationPublication publication);
