// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/loader.ts (registerMcpServer,
// unregisterMcpServer, getMcpServers: assertActive, then the runtime's McpServerRegistry).
using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Registration;

namespace PiSharp.Extensions.Runtime;

public sealed partial class RegistrationScope : IExtensionMcpServerRegistry
{
    public void RegisterMcpServer(string name, JsonData configuration)
    {
        ArgumentNullException.ThrowIfNull(name); ArgumentNullException.ThrowIfNull(configuration);
        McpHost().Register(OwnerId, name, configuration);
    }

    public void UnregisterMcpServer(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        McpHost().Unregister(OwnerId, name);
    }

    public ImmutableArray<McpRegisteredServer> GetMcpServers() => McpHost().List();

    private IExtensionMcpServerHost McpHost()
    {
        if (ExtensionLifetimeCancellationToken.IsCancellationRequested) throw new ObjectDisposedException(nameof(RegistrationScope), "The extension is no longer active.");
        return Registry.McpServerHost ?? throw new NotSupportedException("MCP server registration is unavailable: this host connects no MCP servers.");
    }
}

public sealed partial class ExtensionRegistry
{
    /// <summary>The host's registered MCP servers, which <see cref="IExtensionMcpServerRegistry"/> reaches; null when the host connects
    /// none. Set before the extensions initialize, so registrations made while they load are seen at session start.</summary>
    public IExtensionMcpServerHost? McpServerHost { get; set; }
}
