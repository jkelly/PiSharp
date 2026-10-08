using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Mcp;

public sealed partial class McpExtensionRegistrationBridge
{
    // The admitted loader validates identity before any decoration, binder or activation effect.
    internal bool IsBoundTo(ExtensionRegistry candidate) => ReferenceEquals(registry, candidate);
}
