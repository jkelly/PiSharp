// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/runner.ts (bind: the MCP server registry's
// change listener emits mcp_servers_change with every registered server) and core/extensions/types.ts (McpServersChangeEvent).
namespace PiSharp.Cli.Extensions;

internal sealed partial class NativeExtensionActivation
{
    /// <summary>The servers this generation's extensions register with <c>pi.registerMcpServer()</c>, when the host connects MCP servers.</summary>
    internal PiSharp.Cli.Mcp.McpRegisteredServers? McpServers { get; private init; }

    /// <summary>From the binding on, every registration change reaches the extensions' <c>mcp_servers_change</c> observers of the bound
    /// revision; handler failures are reported and the remaining handlers still run.</summary>
    private void BindMcpServersChange()
    {
        if (McpServers is not { } servers || Binding is null) return;
        var snapshot = Binding.Snapshot;
        if (!_registry.HasObservers(snapshot, "mcp_servers_change")) return;
        servers.BindDispatch(value => _registry.DispatchObservationsReportingAsync(snapshot, "mcp_servers_change", value, _reportInputDiagnostic,
            System.Threading.CancellationToken.None, _closing.Token).AsTask());
    }
}
