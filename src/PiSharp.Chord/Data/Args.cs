// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/data/args.ts
//
// Arguments: a provider's binding (its dispatch table per accepted version) and the per-call argument check. A value (upstream Value: JSON
// plus byte leaves) is a JsonNode tree whose byte leaves are JsonValues holding a byte[] (JsonValue.Create(bytes)); same-host values are
// plain data and immutable once handed over, by convention. Arguments are the caller's own nodes, never copied: a current-version call gets
// back the very node it passed. The dispatch table is held per provider in a weak table, as upstream's WeakMap, so a dropped binding
// releases its table, mappings, schemas and validators. ArgsOf and the typed CheckedArgs<C, M> are TypeScript types.
//
// Upstream's bindProvider(contract, accepts) first runs checkAccepts (contracts/accepts.ts, area cv2-evolve, which also owns the
// AcceptsTable type and evolve). Until it lands, BindProvider takes the accepts table as checkAccepts leaves it, upstream's own
// CheckedAccepts view of it: per accepted older version, its method mappings ("refuse", { call } or { args?, result? }). Joining the two
// is BindProvider(contract, accepts) = CheckAccepts, then this BindProvider; the provider then keeps the app's table in Accepts, as upstream
// does for the state mappings of an older consumer's binding.
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;

namespace PiSharp.Chord.Data;

/// <summary>A method mapping of an accepted version after <c>checkAccepts</c> (upstream <c>CheckedMethodMapping</c>): refused, served by
/// the provider's own replacement (<c>{ call }</c>), or mapped (<c>{ args?, result? }</c>: old arguments to current, the current result
/// back to the old).</summary>
internal sealed class CheckedMethodMapping
{
    private CheckedMethodMapping(bool refuses, Func<JsonNode?, ChordContext, Task<JsonNode?>>? call, Func<JsonNode?, JsonNode?>? args,
        Func<JsonNode?, JsonNode?>? result)
    {
        Refuses = refuses;
        Call = call;
        Args = args;
        Result = result;
    }

    /// <summary><c>"refuse"</c>.</summary>
    internal static CheckedMethodMapping Refuse { get; } = new(true, null, null, null);

    /// <summary><c>{ call }</c>: the provider's own function serves old callers with their old arguments.</summary>
    internal static CheckedMethodMapping Replace(Func<JsonNode?, ChordContext, Task<JsonNode?>> call) =>
        new(false, call ?? throw new ArgumentNullException(nameof(call)), null, null);

    /// <summary><c>{ args?, result? }</c>: the direct mappings of the method's changed sides.</summary>
    internal static CheckedMethodMapping Map(Func<JsonNode?, JsonNode?>? args = null, Func<JsonNode?, JsonNode?>? result = null) => new(false, null, args, result);

    internal bool Refuses { get; }

    internal Func<JsonNode?, ChordContext, Task<JsonNode?>>? Call { get; }

    internal Func<JsonNode?, JsonNode?>? Args { get; }

    internal Func<JsonNode?, JsonNode?>? Result { get; }
}

/// <summary>An accepts table after <c>checkAccepts</c>, as the binding reads it (upstream <c>CheckedAccepts</c>): per accepted older
/// version, the method mappings its later edits require.</summary>
internal sealed class CheckedAccepts(IReadOnlyDictionary<long, IReadOnlyDictionary<string, CheckedMethodMapping>> versions)
{
    /// <summary>An empty table (<c>{}</c>): no older version is accepted.</summary>
    internal static CheckedAccepts None { get; } = new(new Dictionary<long, IReadOnlyDictionary<string, CheckedMethodMapping>>());

    /// <summary>The accepted older versions and their method mappings.</summary>
    internal IReadOnlyDictionary<long, IReadOnlyDictionary<string, CheckedMethodMapping>> Versions { get; } = versions;
}

/// <summary>A provider's binding of a contract with the older versions it accepts and their mappings (upstream <c>Provider</c>).</summary>
internal sealed class Provider
{
    internal Provider(Contract contract, IReadOnlyList<(long Version, string Fingerprint)> accepted, CheckedAccepts accepts)
    {
        Contract = contract;
        Accepted = accepted;
        Accepts = accepts;
    }

    /// <summary>The bound contract.</summary>
    internal Contract Contract { get; }

    /// <summary>The accepted older versions with their fingerprints, oldest first (upstream's <c>AcceptedVersion[]</c>).</summary>
    internal IReadOnlyList<(long Version, string Fingerprint)> Accepted { get; }

    /// <summary>The accepts table as checked.</summary>
    internal CheckedAccepts Accepts { get; }
}

/// <summary>Checked arguments (upstream <c>CheckedArgs</c>): a method's arguments in the current shape with the result mapping back to the
/// caller's version, or the old arguments for the provider's own replacement (the host dispatches it).</summary>
internal abstract record CheckedArgs(JsonNode? Args)
{
    /// <summary><c>{ kind: "method", args, result? }</c>.</summary>
    internal sealed record Method(JsonNode? Args, Func<JsonNode?, JsonNode?>? Result) : CheckedArgs(Args);

    /// <summary><c>{ kind: "call", args, call }</c>: <paramref name="Replacement"/> is the provider's <c>call</c>.</summary>
    internal sealed record Call(JsonNode? Args, Func<JsonNode?, ChordContext, Task<JsonNode?>> Replacement) : CheckedArgs(Args);
}

internal static partial class DataInternal
{
    private enum DispatchKind { Refuse, Call, Method }

    /// <summary>A method of an accepted version: refused, a replacement, or a method with its mappings; its validator compiles on first use.</summary>
    private sealed class DispatchEntry(DispatchKind kind, Snapshot args, CheckedMethodMapping? mapping)
    {
        internal DispatchKind Kind { get; } = kind;
        internal Snapshot Args { get; } = args;
        internal CheckedMethodMapping? Mapping { get; } = mapping;
        internal Validator? Validator { get; set; }
    }

    /// <summary>Accepted version -> method -> entry.</summary>
    private sealed class Dispatch : Dictionary<long, Dictionary<string, DispatchEntry>>;

    private static readonly ConditionalWeakTable<Provider, Dispatch> DispatchTables = new();

    /// <summary>Binds a contract and its checked accepts table (kept as it is) into a provider (upstream <c>bindProvider</c>, after
    /// <c>checkAccepts</c>). Builds the dispatch table: accepted version -> method -> schema and mapping; nothing is compiled yet.</summary>
    internal static Provider BindProvider(Contract contract, CheckedAccepts accepts)
    {
        ArgumentNullException.ThrowIfNull(accepts);
        var versions = ContractsInternal.DataOf(contract).Versions;
        var current = versions[^1];
        var table = new Dispatch { [current.Version] = DispatchEntries(current, null) };
        var accepted = new List<(long Version, string Fingerprint)>();
        // checkAccepts lists the accepted versions in the order of the table's keys: integer keys, ascending.
        foreach (var (version, mappings) in accepts.Versions.OrderBy(entry => entry.Key))
        {
            var data = versions.FirstOrDefault(candidate => candidate.Version == version);
            if (data is null || ReferenceEquals(data, current)) throw new ArgumentException($"{contract.Name} accepts[{version}]: not an older version");
            accepted.Add((version, data.Fingerprint));
            table[version] = DispatchEntries(data, mappings);
        }
        var provider = new Provider(contract, accepted, accepts);
        DispatchTables.Add(provider, table);
        return provider;
    }

    /// <summary>Whether <paramref name="provider"/> binds a consumer of <paramref name="version"/> with <paramref name="fingerprint"/>: its
    /// current version and fingerprint, or a version it accepts with the fingerprint its own history computes (R6, Q10; upstream
    /// <c>binds</c>).</summary>
    internal static bool Binds(Provider provider, long version, string fingerprint)
    {
        var contract = provider.Contract;
        if (version == contract.Version) return fingerprint == contract.Fingerprint;
        return provider.Accepted.Any(accepted => accepted.Version == version && accepted.Fingerprint == fingerprint);
    }

    private static Dictionary<string, DispatchEntry> DispatchEntries(VersionData version, IReadOnlyDictionary<string, CheckedMethodMapping>? mappings)
    {
        var entries = new Dictionary<string, DispatchEntry>(StringComparer.Ordinal);
        // A method has one signature in every variant that has it.
        foreach (var (member, method) in version.Variants.Values.SelectMany(variant => variant.Methods))
        {
            var mapping = mappings is not null && mappings.TryGetValue(member, out var found) ? found : null;
            entries[member] = new DispatchEntry(mapping switch
            {
                { Refuses: true } => DispatchKind.Refuse,
                { Call: not null } => DispatchKind.Call,
                _ => DispatchKind.Method,
            }, method.Args, mapping);
        }
        return entries;
    }

    /// <summary>Checks a call's arguments against the provider's schema for the caller's bound version (upstream <c>checkArgs</c>): three
    /// lookups, one compiled check, at most one direct mapping old -> current (never for current-version calls). A method without arguments
    /// gets <c>null</c> (its internal and wire form). Unknown method: <c>invalid/member</c>; version not accepted or refused:
    /// <c>contract_mismatch</c>. A <c>call</c> replacement is returned, never invoked.</summary>
    internal static CheckedArgs CheckArgs(Provider provider, string method, JsonNode? raw, long boundVersion, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var name = provider.Contract.Name;
        var table = DispatchTables.TryGetValue(provider, out var found) ? found : throw new ArgumentException($"{name}: not a bound provider");
        if (!table.TryGetValue(boundVersion, out var methods))
            throw new ChordError("contract_mismatch", $"{name} v{boundVersion} is not accepted");
        if (!methods.TryGetValue(method, out var entry)) throw Invalid("member", $"{name} v{boundVersion} has no method \"{method}\"");
        if (entry.Kind == DispatchKind.Refuse) throw new ChordError("contract_mismatch", $"{name} v{boundVersion} refuses \"{method}\"");
        entry.Validator ??= ValidatorFor(entry.Args, context);
        Check(entry.Validator, raw, "args", context);
        // The check above establishes the argument type; mapping output is trusted.
        if (entry.Kind == DispatchKind.Call) return new CheckedArgs.Call(raw, entry.Mapping!.Call!);
        var mapArgs = entry.Mapping?.Args;
        return new CheckedArgs.Method(mapArgs is null ? raw : mapArgs(raw), entry.Mapping?.Result);
    }
}
