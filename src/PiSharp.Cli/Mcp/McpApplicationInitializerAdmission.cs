using PiSharp.Cli.Extensions;
using PiSharp.Extensions.Mcp.Registration;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Mcp;

/// <summary>Explicit application installer factory on the actual loaded plugin registry.
/// The native initializer host activates its single original plugin and commits this bridge;
/// only after that activation settles may the profile retrieve this exact installation.</summary>
public sealed record McpApplicationInitializerAdmission(
    Func<ExtensionRegistry, McpApplicationHostInstallation> CreateInstallation,
    Action<IMcpServerRegistrationFacade> BindServers)
{
    internal NativeExtensionInitializerInstallation Bind(NativeExtensionInitializerInstallation native,
        out Func<McpApplicationHostInstallation> readInstalled)
    {
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(CreateInstallation); ArgumentNullException.ThrowIfNull(BindServers);
        if (CreateInstallation.GetInvocationList().Length != 1 || BindServers.GetInvocationList().Length != 1 ||
            native.CreateMcpBridge is not null || native.BindMcp is not null)
            throw new ArgumentException("One new application initializer owner; no competing MCP initializer route.");
        native.Validate();
        McpApplicationHostInstallation? installed = null; var attempts = 0;
        readInstalled = () => installed ?? throw new InvalidOperationException("Actual native MCP installation has not completed.");
        return native with
        {
            CreateMcpBridge = actualRegistry =>
            {
                if (Interlocked.Increment(ref attempts) != 1)
                    throw new InvalidOperationException("Application installation belongs to one actual native activation.");
                McpApplicationHostInstallation result;
                try { result = CreateInstallation(actualRegistry)
                    ?? throw new InvalidOperationException("Application installer returned no installation."); }
                catch (OperationCanceledException error)
                { throw new McpProfileResourceAcquisitionException("application initializer factory", null, error); }
                if (!result.Bridge.IsBoundTo(actualRegistry))
                    throw new InvalidOperationException("Application installer substituted the actual loaded plugin registry.");
                installed = result;
                return result.Bridge;
            },
            BindMcp = BindServers
        };
    }
}
