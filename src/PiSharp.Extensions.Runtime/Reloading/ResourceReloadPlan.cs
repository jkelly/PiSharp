using System.Collections.Immutable;

namespace PiSharp.Extensions.Runtime.Reloading;

/// <summary>Host-approved catalog identity and content fingerprint. This is not a path, trust decision or payload.</summary>
public sealed record ResourceReloadEntry(string Kind, string Key, string Fingerprint);
public enum ResourceReloadChangeKind { Added, Changed, Removed }
public sealed record ResourceReloadChange(ResourceReloadChangeKind Change, ResourceReloadEntry? Previous,
    ResourceReloadEntry? Replacement);

/// <summary>Bounded, immutable planning input. Resource kinds are extensible (settings, skills, prompts, extensions,
/// MCP or host-specific catalogs); their discovery, approval and typed payloads remain with the host.</summary>
public sealed class ResourceReloadPlan
{
    public ImmutableArray<ResourceReloadEntry> Current { get; }
    public ImmutableArray<ResourceReloadEntry> Replacement { get; }
    public ImmutableArray<ResourceReloadChange> Changes { get; }

    public ResourceReloadPlan(IEnumerable<ResourceReloadEntry> current, IEnumerable<ResourceReloadEntry> replacement,
        int maximumEntriesPerCatalog = 4096)
    {
        if (maximumEntriesPerCatalog is < 1 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(maximumEntriesPerCatalog));
        Current = Capture(current, maximumEntriesPerCatalog, nameof(current));
        Replacement = Capture(replacement, maximumEntriesPerCatalog, nameof(replacement));
        var old = Current.ToDictionary(entry => (entry.Kind, entry.Key));
        var next = Replacement.ToDictionary(entry => (entry.Kind, entry.Key));
        var changes = ImmutableArray.CreateBuilder<ResourceReloadChange>();
        foreach (var entry in Replacement)
        {
            if (!old.TryGetValue((entry.Kind, entry.Key), out var previous))
                changes.Add(new(ResourceReloadChangeKind.Added, null, entry));
            else if (!StringComparer.Ordinal.Equals(previous.Fingerprint, entry.Fingerprint))
                changes.Add(new(ResourceReloadChangeKind.Changed, previous, entry));
        }
        foreach (var entry in Current)
            if (!next.ContainsKey((entry.Kind, entry.Key)))
                changes.Add(new(ResourceReloadChangeKind.Removed, entry, null));
        Changes = changes.ToImmutable();
    }

    private static ImmutableArray<ResourceReloadEntry> Capture(IEnumerable<ResourceReloadEntry> source, int maximum,
        string parameter)
    {
        ArgumentNullException.ThrowIfNull(source, parameter);
        var captured = ImmutableArray.CreateBuilder<ResourceReloadEntry>();
        var identities = new HashSet<(string Kind, string Key)>();
        foreach (var entry in source)
        {
            if (captured.Count == maximum) throw new ArgumentException("Catalog entry limit exceeded.", parameter);
            if (entry is null || !Valid(entry.Kind) || !Valid(entry.Key) || !Valid(entry.Fingerprint))
                throw new ArgumentException("Catalog fields must contain 1 to 2048 characters.", parameter);
            if (!identities.Add((entry.Kind, entry.Key)))
                throw new ArgumentException("Duplicate catalog identity.", parameter);
            captured.Add(entry);
        }
        return captured.ToImmutable();
    }

    private static bool Valid(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 2048;
}
