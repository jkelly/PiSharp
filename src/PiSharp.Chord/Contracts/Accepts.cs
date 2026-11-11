// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/contracts/accepts.ts
//
// Accepted versions: the older versions a provider still serves, with the direct mappings their edits require. Upstream's AcceptsTable
// is a type computed from the contract's history (AcceptsOf, VersionMappings, Section, OmitNever, StateMappings, KeyedMappings,
// MethodMappings, ChangedMethod, Replacement), so the TypeScript compiler requires exactly the mappings each version needs, with their
// value types; checkAccepts checks the same at run time for JavaScript callers. C# has no mapped types and a Contract is not generic, so
// the C# table has one shape for every contract (AcceptsTable, VersionMappings, StateEntry, StateSection, StateMapping, MethodMapping),
// its values are untyped (object?: JSON plus byte leaves, the data layer's values), and CheckAccepts is where a table is checked. Its
// keys are versions (long) and its sections typed, so three of upstream's run-time refusals cannot be written: a version key that is no
// number (`accepts[other]`), an unknown key in a version's mappings or a method mapping, and a method side that is no function. A null
// section, like an absent one, maps nothing; a null mapping or version entry is a value that is neither a function nor "refuse" nor an
// object, as upstream's undefined is. The table's entries are read in JavaScript's own-key order, as Object.entries reads them: array
// indexes ascending, then the other keys in insertion order. checkAccepts never calls a mapping.
using System.Globalization;

namespace PiSharp.Chord.Contracts;

/// <summary>The older versions of a contract a provider still serves, by version, with exactly the mappings the edits after each version
/// require (upstream <c>AcceptsTable</c>). Mappings are direct, old version to current: when a version is added, every accepted version's
/// mappings are updated. A mapping is required per edit after the accepted version, even for a no-op edit.</summary>
public sealed class AcceptsTable : OrderedDictionary<long, VersionMappings?>;

/// <summary>One accepted version's mappings. A section is needed when it holds a needed mapping; otherwise it may be left empty.</summary>
public sealed class VersionMappings
{
    /// <summary>State mappings. A remote contract's are by variant, a <see cref="StateSection"/> each; a local contract's table is flat: a
    /// <see cref="StateMapping"/> by member. A changed member maps the current member to the old one; a removed one maps the variant's whole
    /// current state (an object of its members) to the old member, or refuses.</summary>
    public OrderedDictionary<string, StateEntry> State { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Method mappings by name: changed arguments map old to current, a changed result current to old; a removed method takes a
    /// <see cref="MethodMapping.Call"/> replacement; any changed or removed method may refuse.</summary>
    public OrderedDictionary<string, MethodMapping?> Methods { get; init; } = new(StringComparer.Ordinal);

    /// <summary>A local contract's keyed members (slice 7b): a changed one maps each document of the current version back, or refuses; a
    /// removed one refuses (no document maps from the whole state).</summary>
    public OrderedDictionary<string, StateMapping> Keyed { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>One remote variant's state mappings, by member.</summary>
public sealed class StateSection : OrderedDictionary<string, StateMapping>
{
    /// <summary>An empty section.</summary>
    public StateSection() : base(StringComparer.Ordinal) { }
}

/// <summary>An entry of <see cref="VersionMappings.State"/>: a remote variant's <see cref="StateSection"/>, or a local member's
/// <see cref="StateMapping"/>.</summary>
public readonly struct StateEntry
{
    private StateEntry(object? value) => Value = value;

    /// <summary>A boxed <see cref="StateMapping"/>, a <see cref="StateSection"/>, or null (nothing).</summary>
    internal object? Value { get; }

    /// <summary>A local member's mapping.</summary>
    public static implicit operator StateEntry(StateMapping mapping) => new(mapping);

    /// <summary>A local member's mapping function.</summary>
    public static implicit operator StateEntry(Func<object?, object?>? map) => new((StateMapping)map);

    /// <summary>A remote variant's mappings.</summary>
    public static implicit operator StateEntry(StateSection? section) => new(section);
}

/// <summary>A state or keyed member's mapping: a function from the current value to the old one, or <see cref="Refuse"/> (upstream's
/// <c>"refuse"</c>), which leaves the older version's consumers of the member unbound.</summary>
public readonly struct StateMapping
{
    private StateMapping(Func<object?, object?>? map, bool refuses)
    {
        Map = map;
        Refuses = refuses;
    }

    /// <summary>A mapping function.</summary>
    public StateMapping(Func<object?, object?> map) : this(map, false) { }

    /// <summary>The refusal: for a removed state member, and a changed or removed keyed member.</summary>
    public static StateMapping Refuse { get; } = new(null, true);

    /// <summary>The mapping function; null for a refusal.</summary>
    public Func<object?, object?>? Map { get; }

    /// <summary>Whether this is <see cref="Refuse"/>.</summary>
    public bool Refuses { get; }

    /// <summary>A mapping function.</summary>
    public static implicit operator StateMapping(Func<object?, object?>? map) => new(map, false);
}

/// <summary>A changed or removed method's mapping: the changed sides (<see cref="Args"/>, <see cref="Result"/>), a replacement
/// (<see cref="Call"/>, for a removed or changed method), or <see cref="Refuse"/>.</summary>
public sealed class MethodMapping
{
    /// <summary>The refusal (upstream's <c>"refuse"</c>): an older consumer's call fails <c>contract_mismatch</c>.</summary>
    public static MethodMapping Refuse { get; } = new() { Refuses = true };

    /// <summary>For changed arguments: the old version's arguments to the current version's.</summary>
    public Func<object?, object?>? Args { get; init; }

    /// <summary>For a changed result: the current version's result to the old version's.</summary>
    public Func<object?, object?>? Result { get; init; }

    /// <summary>The provider's own function serving the old version's calls: the old arguments and the call's context (Chord's
    /// <c>Context</c>, which comes with the host in phase 6) to the old result. Never combined with <see cref="Args"/> or <see cref="Result"/>.</summary>
    public Func<object?, object, Task<object?>>? Call { get; init; }

    /// <summary>Whether this is <see cref="Refuse"/>.</summary>
    public bool Refuses { get; private init; }
}

/// <summary>An accepted older version and its fingerprint.</summary>
internal readonly record struct AcceptedVersion(long Version, string Fingerprint);

/// <summary>What <see cref="ContractsInternal.CheckAccepts"/> returns: the accepted versions, in the table's order.</summary>
internal sealed record AcceptedVersions(IReadOnlyList<AcceptedVersion> Versions);

internal static partial class ContractsInternal
{
    /// <summary>Validates an accepts table without calling any mapping: known older versions only, exactly the required mappings with the
    /// right shapes. Throws an <see cref="ArgumentException"/> naming the path (<c>notes accepts[1].methods.add</c>).</summary>
    internal static AcceptedVersions CheckAccepts(Contract contract, AcceptsTable? accepts)
    {
        var data = DataOf(contract);
        if (accepts is null) throw new ArgumentException($"{data.Name} accepts: expected an object");
        var versions = new List<AcceptedVersion>();
        var entries = accepts.Select(entry => KeyValuePair.Create(entry.Key.ToString(CultureInfo.InvariantCulture), entry.Value));
        foreach (var (key, mappings) in InJavaScriptOrder(entries))
        {
            var path = $"{data.Name} accepts[{key}]";
            var old = OlderVersions(data).FirstOrDefault(version => version.Version.ToString(CultureInfo.InvariantCulture) == key)
                ?? throw new ArgumentException($"{path}: not an older version");
            CheckMappings(mappings, NeedsOf(data, old), data.Kind, path);
            versions.Add(new AcceptedVersion(old.Version, old.Fingerprint));
        }
        return new AcceptedVersions(versions);
    }

    /// <summary>What an accepted version needs, by edit and never by schema comparison: per variant, per state member; per method; per
    /// keyed member (local contracts). Each map is in the order upstream's Maps fill.</summary>
    private sealed record Needs(
        OrderedDictionary<string, OrderedDictionary<string, StateNeed>> State,
        OrderedDictionary<string, MethodNeed> Methods,
        OrderedDictionary<string, StateNeed> Keyed);

    private enum StateNeed { Changed, Removed, Rekeyed }

    /// <summary>A removed method, or a changed one with the sides that changed.</summary>
    private sealed record MethodNeed(bool Removed, bool Args, bool Result);

    private static Needs NeedsOf(ContractData data, VersionData old)
    {
        var current = data.Versions[^1];
        var changes = ChangesAfter(data, old.Version).ToHashSet();
        bool Changed(string variant, string member, ChangeSide side) => changes.Contains(new Change(variant, member, side));
        var state = new OrderedDictionary<string, OrderedDictionary<string, StateNeed>>(StringComparer.Ordinal);
        var methods = new OrderedDictionary<string, MethodNeed>(StringComparer.Ordinal);
        var keyed = new OrderedDictionary<string, StateNeed>(StringComparer.Ordinal);
        foreach (var (variant, members) in old.Variants)
        {
            var now = current.Variants[variant];
            foreach (var member in members.Keyed.Keys)
            {
                if (!now.Keyed.ContainsKey(member)) keyed[member] = StateNeed.Removed;
                else if (members.Keyed[member].KeyHost?.Kind != now.Keyed[member].KeyHost?.Kind) keyed[member] = StateNeed.Rekeyed;
                else if (Changed(variant, member, ChangeSide.Keyed)) keyed[member] = StateNeed.Changed;
            }
            var variantNeeds = new OrderedDictionary<string, StateNeed>(StringComparer.Ordinal);
            foreach (var member in members.State.Keys)
            {
                if (!now.State.ContainsKey(member)) variantNeeds[member] = StateNeed.Removed;
                else if (Changed(variant, member, ChangeSide.State)) variantNeeds[member] = StateNeed.Changed;
            }
            if (variantNeeds.Count > 0) state[variant] = variantNeeds;
            foreach (var member in members.Methods.Keys)
            {
                var need = methods.GetValueOrDefault(member) ?? new MethodNeed(false, false, false);
                methods[member] = new MethodNeed(need.Removed || !now.Methods.ContainsKey(member), need.Args || Changed(variant, member, ChangeSide.Args),
                    need.Result || Changed(variant, member, ChangeSide.Result));
            }
        }
        var methodNeeds = new OrderedDictionary<string, MethodNeed>(StringComparer.Ordinal);
        foreach (var (member, need) in methods)
        {
            if (need.Removed) methodNeeds[member] = need with { Args = false, Result = false };
            else if (need.Args || need.Result) methodNeeds[member] = need;
        }
        return new Needs(state, methodNeeds, keyed);
    }

    private static void CheckMappings(VersionMappings? mappings, Needs needs, ContractKind kind, string path)
    {
        if (mappings is null) throw new ArgumentException($"{path}: expected an object");
        // A local table is flat: its state section is the one variant's.
        if (kind == ContractKind.Local)
        {
            var localNeeds = needs.State.GetValueOrDefault(Local) ?? new OrderedDictionary<string, StateNeed>(StringComparer.Ordinal);
            CheckSection(mappings.State, localNeeds, $"{path}.state", (entry, need, at) => CheckStateMapping(entry.Value, need, at));
        }
        else CheckSection(mappings.State, needs.State, $"{path}.state", CheckVariant);
        CheckSection(mappings.Methods, needs.Methods, $"{path}.methods", CheckMethodMapping);
        CheckSection(mappings.Keyed, needs.Keyed, $"{path}.keyed", CheckKeyedMapping);
    }

    /// <summary>A remote variant's mappings: a section (nothing is an empty one) with exactly the needed members.</summary>
    private static void CheckVariant(StateEntry entry, OrderedDictionary<string, StateNeed> needs, string path)
    {
        if (entry.Value is not (null or StateSection)) throw new ArgumentException($"{path}: expected an object");
        CheckSection((StateSection?)entry.Value, needs, path, (mapping, need, at) => CheckStateMapping(mapping, need, at));
    }

    /// <summary>Exactly the needed keys: no missing and no unnecessary mapping; absent only when nothing is needed.</summary>
    private static void CheckSection<TMapping, TNeed>(IEnumerable<KeyValuePair<string, TMapping>>? section, OrderedDictionary<string, TNeed> needs, string path,
        Action<TMapping, TNeed, string> check)
    {
        var mappings = new OrderedDictionary<string, TMapping>(StringComparer.Ordinal);
        foreach (var (key, mapping) in InJavaScriptOrder(section ?? [])) mappings[key] = mapping;
        foreach (var key in mappings.Keys)
            if (!needs.ContainsKey(key)) throw new ArgumentException($"{path}.{key}: unnecessary mapping");
        foreach (var (key, need) in needs)
        {
            if (!mappings.TryGetValue(key, out var mapping)) throw new ArgumentException($"{path}.{key}: missing mapping");
            check(mapping, need, $"{path}.{key}");
        }
    }

    /// <summary>A state mapping: a function, or for a removed member also <see cref="StateMapping.Refuse"/>.</summary>
    private static void CheckStateMapping(object? mapping, StateNeed need, string path)
    {
        if (mapping is StateMapping { Map: not null } || (need == StateNeed.Removed && mapping is StateMapping { Refuses: true })) return;
        throw new ArgumentException($"{path}: expected {(need == StateNeed.Removed ? "a function or \"refuse\"" : "a function")}");
    }

    /// <summary>A keyed mapping: <see cref="StateMapping.Refuse"/>, or for a changed member also a function.</summary>
    private static void CheckKeyedMapping(StateMapping mapping, StateNeed need, string path)
    {
        if (mapping.Refuses || (need == StateNeed.Changed && mapping.Map is not null)) return;
        var because = need == StateNeed.Rekeyed ? " (its key kind changed: a value mapping cannot map keys)" : "";
        throw new ArgumentException($"{path}: expected {(need == StateNeed.Changed ? "a function or \"refuse\"" : "\"refuse\"")}{because}");
    }

    private static void CheckMethodMapping(MethodMapping? mapping, MethodNeed need, string path)
    {
        if (mapping is { Refuses: true }) return;
        if (mapping is null) throw new ArgumentException($"{path}: expected an object");
        if (mapping.Call is not null || need.Removed)
        {
            if (mapping.Call is null || mapping.Args is not null || mapping.Result is not null)
                throw new ArgumentException($"{path}: expected {{ call }} or \"refuse\"");
            return;
        }
        CheckSide(mapping.Args, need.Args, $"{path}.args");
        CheckSide(mapping.Result, need.Result, $"{path}.result");
    }

    private static void CheckSide(Delegate? mapping, bool needed, string path)
    {
        if (!needed && mapping is not null) throw new ArgumentException($"{path}: unnecessary mapping");
        if (needed && mapping is null) throw new ArgumentException($"{path}: missing mapping");
    }

    /// <summary>Entries in JavaScript's own-key order, as <c>Object.entries</c> reads an object: array-index keys ascending, then the rest
    /// in insertion order.</summary>
    private static IEnumerable<KeyValuePair<string, T>> InJavaScriptOrder<T>(IEnumerable<KeyValuePair<string, T>> entries)
    {
        var list = entries.ToList();
        var indexes = list.Select(entry => (Entry: entry, Index: ArrayIndex(entry.Key))).Where(entry => entry.Index is not null).OrderBy(entry => entry.Index)
            .Select(entry => entry.Entry);
        return indexes.Concat(list.Where(entry => ArrayIndex(entry.Key) is null));
    }

    /// <summary>A key's array index: the canonical decimal form of an integer from 0 to 2^32 - 2; otherwise null.</summary>
    private static uint? ArrayIndex(string key) =>
        uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index != uint.MaxValue &&
        index.ToString(CultureInfo.InvariantCulture) == key ? index : null;
}
