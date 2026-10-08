namespace PiSharp.Extensions.Runtime;

internal enum RegistrationKind { Tool, Command, Observation, InputHandler, ToolCallHandler, ToolResultHandler, SessionSwitchHandler, SessionCreationHandler, ContextWithSystemHandler, ContextHandler, BeforeAgentStartHandler, EventBus, SessionBeforeTreeHandler, ToolRenderer, UserBashHandler }

internal sealed class RegistrationEntry
{
    internal required string OwnerId { get; init; }
    internal required long OwnerGeneration { get; init; }
    internal required string RegistrationId { get; init; }
    internal required string Name { get; init; }
    internal required RegistrationKind Kind { get; init; }
    internal required object Descriptor { get; init; }
    internal required int MetadataCharacters { get; init; }
    // All mutable bookkeeping is protected by the registry gate.
    internal bool Registered { get; set; } = true;
    internal bool Charged { get; set; } = true;
    internal int Leases { get; set; }
}

/// <summary>Live entries in deterministic registration order; retired callback leases are charged separately.</summary>
internal sealed class StagedRegistrationSet
{
    private readonly Dictionary<string, RegistrationEntry> byId = new(StringComparer.Ordinal);
    private readonly List<RegistrationEntry> ordered = [];
    internal IReadOnlyList<RegistrationEntry> Entries => ordered;
    internal bool ContainsId(string id) => byId.ContainsKey(id);
    internal bool Contains(RegistrationEntry entry) =>
        byId.TryGetValue(entry.RegistrationId, out var current) && ReferenceEquals(current, entry);
    internal bool ContainsName(RegistrationKind kind, string name) =>
        ordered.Any(entry => entry.Kind == kind && entry.Name == name);

    internal void Add(RegistrationEntry entry)
    {
        byId.Add(entry.RegistrationId, entry);
        ordered.Add(entry);
    }

    internal bool Remove(RegistrationEntry entry)
    {
        if (!Contains(entry)) return false;
        byId.Remove(entry.RegistrationId);
        ordered.Remove(entry);
        return true;
    }
}
