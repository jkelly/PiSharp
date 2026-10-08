namespace PiSharp.Extensions.Runtime;

/// <summary>Synchronous cancellation ancestry, independent of a registration's captured ExecutionContext.</summary>
internal sealed class TerminalInputRegistryCleanupFrame : IDisposable
{
    [ThreadStatic] private static TerminalInputRegistryCleanupFrame? current;
    private readonly TerminalInputRegistryCleanupFrame? parent;
    private readonly RegistrationScope owner;
    private bool active = true;
    internal TerminalInputRegistryCleanupFrame(RegistrationScope owner)
    {
        this.owner = owner; parent = current; current = this;
    }
    internal static bool IsExecuting(RegistrationScope scope)
    {
        for (var frame = current; frame is not null; frame = frame.parent)
            if (frame.active && ReferenceEquals(frame.owner, scope)) return true;
        return false;
    }
    internal static bool IsExecuting(ExtensionRegistry registry)
    {
        for (var frame = current; frame is not null; frame = frame.parent)
            if (frame.active && ReferenceEquals(frame.owner.Registry, registry)) return true;
        return false;
    }
    public void Dispose() { active = false; current = parent; }
}
