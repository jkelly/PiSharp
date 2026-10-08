using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Extensions;

public sealed partial class NativeExtensionRegistrationBridge
{
    // Validation only: the loader rejects a foreign configured bridge before any initializer effects.
    internal bool IsBoundTo(ExtensionRegistry candidate) => ReferenceEquals(registry, candidate);
}
