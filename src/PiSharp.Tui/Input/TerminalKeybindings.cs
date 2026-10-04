using System.Collections.ObjectModel;

namespace PiSharp.Tui.Input;

/// <summary>A scalar key or an ordered array of keys, preserving the configuration's shape.</summary>
public sealed class TerminalKeybindingValue
{
    private readonly ReadOnlyCollection<string> keys;

    public TerminalKeybindingValue(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        keys = Array.AsReadOnly(new[] { key });
        IsScalar = true;
    }

    public TerminalKeybindingValue(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var values = keys.ToArray();
        foreach (var key in values) ArgumentNullException.ThrowIfNull(key);
        this.keys = Array.AsReadOnly(values);
    }

    public bool IsScalar { get; }
    public string? Scalar => IsScalar ? keys[0] : null;
    public IReadOnlyList<string> Keys => keys;

    public static implicit operator TerminalKeybindingValue(string key) => new(key);
    public static implicit operator TerminalKeybindingValue(string[] keys) => new(keys);
}

public sealed record TerminalKeybindingDefinition(TerminalKeybindingValue DefaultKeys, string? Description = null);
public sealed record TerminalKeybindingConflict(string Key, string[] Keybindings);

/// <summary>
/// Ordered, per-owner keybinding registry matching Pi v0.99.1's TUI configuration rules.
/// The host supplies raw-data matching; this class does not reinterpret decoder events or dispatch actions.
/// Use from one UI owner. Configuration values and definitions are immutable snapshots.
/// </summary>
public sealed class TerminalKeybindings
{
    private readonly KeyValuePair<string, TerminalKeybindingDefinition>[] definitions;
    private readonly Dictionary<string, TerminalKeybindingDefinition> definitionsById;
    private readonly Func<string, string, bool> matchesKey;
    private KeyValuePair<string, TerminalKeybindingValue?>[] userBindings = [];
    private Dictionary<string, string[]> keysById = new(StringComparer.Ordinal);
    private TerminalKeybindingConflict[] conflicts = [];

    public TerminalKeybindings(Func<string, string, bool> matchesKey,
        IEnumerable<KeyValuePair<string, TerminalKeybindingValue?>>? userBindings = null)
        : this(TerminalKeybindingDefinitions.Tui, matchesKey, userBindings) { }

    public TerminalKeybindings(IEnumerable<KeyValuePair<string, TerminalKeybindingDefinition>> definitions,
        Func<string, string, bool> matchesKey,
        IEnumerable<KeyValuePair<string, TerminalKeybindingValue?>>? userBindings = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(matchesKey);
        this.definitions = definitions.ToArray();
        definitionsById = new(StringComparer.Ordinal);
        foreach (var (id, definition) in this.definitions)
        {
            ArgumentNullException.ThrowIfNull(id);
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentNullException.ThrowIfNull(definition.DefaultKeys);
            if (!definitionsById.TryAdd(id, definition))
                throw new ArgumentException("Definition IDs must be unique", nameof(definitions));
        }
        this.matchesKey = matchesKey;
        SetUserBindings(userBindings ?? []);
    }

    /// <summary>Replace the entire configuration. Null is Source undefined; an empty array disables.</summary>
    public void SetUserBindings(IEnumerable<KeyValuePair<string, TerminalKeybindingValue?>> userBindings)
    {
        ArgumentNullException.ThrowIfNull(userBindings);
        var next = userBindings.ToArray();
        var userById = new Dictionary<string, TerminalKeybindingValue?>(StringComparer.Ordinal);
        var claimsByKey = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var claimOrder = new List<string>();
        foreach (var (id, value) in next)
        {
            ArgumentNullException.ThrowIfNull(id);
            if (!userById.TryAdd(id, value))
                throw new ArgumentException("Configuration IDs must be unique", nameof(userBindings));
            if (!definitionsById.ContainsKey(id)) continue;
            foreach (var key in Normalize(value))
            {
                if (!claimsByKey.TryGetValue(key, out var claimants))
                {
                    claimants = [];
                    claimsByKey.Add(key, claimants);
                    claimOrder.Add(key);
                }
                claimants.Add(id);
            }
        }

        var resolved = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (id, definition) in definitions)
        {
            userById.TryGetValue(id, out var userValue);
            resolved.Add(id, Normalize(userValue ?? definition.DefaultKeys));
        }
        var nextConflicts = claimOrder.Where(key => claimsByKey[key].Count > 1)
            .Select(key => new TerminalKeybindingConflict(key, claimsByKey[key].ToArray())).ToArray();
        // Publish only after complete validation; failed replacement leaves the old registry intact.
        this.userBindings = next;
        keysById = resolved;
        conflicts = nextConflicts;
    }

    public bool Matches(string data, string keybinding)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(keybinding);
        if (!keysById.TryGetValue(keybinding, out var keys)) return false;
        foreach (var key in keys)
            if (matchesKey(data, key)) return true;
        return false;
    }

    public string[] GetKeys(string keybinding)
    {
        ArgumentNullException.ThrowIfNull(keybinding);
        return keysById.TryGetValue(keybinding, out var keys) ? (string[])keys.Clone() : [];
    }

    public TerminalKeybindingDefinition? GetDefinition(string keybinding)
    {
        ArgumentNullException.ThrowIfNull(keybinding);
        return definitionsById.GetValueOrDefault(keybinding);
    }

    public TerminalKeybindingConflict[] GetConflicts() => conflicts
        .Select(conflict => new TerminalKeybindingConflict(conflict.Key, (string[])conflict.Keybindings.Clone()))
        .ToArray();

    /// <summary>Copy the complete configuration for an input owner's lifetime. Later replacements are independent.</summary>
    public TerminalKeybindings CreateSnapshot() => new(definitions, matchesKey, userBindings);

    /// <summary>Return a new map. Values are immutable; unknown IDs and null values are retained.</summary>
    public Dictionary<string, TerminalKeybindingValue?> GetUserBindings() =>
        userBindings.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    /// <summary>Definition order, with one key as a scalar and zero or multiple keys as arrays.</summary>
    public Dictionary<string, TerminalKeybindingValue> GetResolvedBindings()
    {
        var resolved = new Dictionary<string, TerminalKeybindingValue>(StringComparer.Ordinal);
        foreach (var (id, _) in definitions)
        {
            var keys = keysById[id];
            resolved.Add(id, keys.Length == 1 ? new TerminalKeybindingValue(keys[0]) : new TerminalKeybindingValue(keys));
        }
        return resolved;
    }

    private static string[] Normalize(TerminalKeybindingValue? value)
    {
        if (value is null) return [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var keys = new List<string>();
        foreach (var key in value.Keys)
            if (seen.Add(key)) keys.Add(key);
        return keys.ToArray();
    }
}
