// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/data/args.ts
//
// Arguments: a provider's binding (its dispatch table per accepted version) and the per-call argument check. A value (upstream Value: JSON
// plus byte leaves) is a JsonNode tree whose byte leaves are JsonValues holding a byte[] (JsonValue.Create(bytes)); same-host values are
// plain data and immutable once handed over, by convention. Arguments are the caller's own nodes, never copied: a current-version call gets
// back the very node it passed. The dispatch table is held per provider in a weak table, as upstream's WeakMap, so a dropped binding
// releases its table, mappings, schemas and validators. ArgsOf and the typed CheckedArgs<C, M> are TypeScript types.
//
// bindProvider checks the app's AcceptsTable with ContractsInternal.CheckAccepts (contracts/accepts.ts), then reads it as upstream's
// private CheckedAccepts view does: per accepted version, its MethodMapping by method. A table's mappings are untyped (object?, the data
// layer's values), so an args mapping gets the caller's JsonNode and must return a value: a JsonNode or null. Upstream trusts a mapping's
// output, which its types establish; here CheckArgs refuses an output that is no JsonNode (a PiSharp-only refusal, the run-time half of
// those types). The result mapping and a call replacement are handed back as the table holds them, never called here.
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;

namespace PiSharp.Chord.Data;

/// <summary>A provider's binding of a contract with the older versions it accepts and their mappings (upstream <c>Provider</c>): the
/// table as checked, kept as it is, for the state mappings of an older consumer's binding.</summary>
internal sealed class Provider
{
    internal Provider(Contract contract, IReadOnlyList<AcceptedVersion> accepted, AcceptsTable accepts)
    {
        Contract = contract;
        Accepted = accepted;
        Accepts = accepts;
    }

    /// <summary>The bound contract.</summary>
    internal Contract Contract { get; }

    /// <summary>The accepted older versions with their fingerprints, in the table's order (<c>checkAccepts</c>'s).</summary>
    internal IReadOnlyList<AcceptedVersion> Accepted { get; }

    /// <summary>The app's accepts table, as checked.</summary>
    internal AcceptsTable Accepts { get; }
}

/// <summary>Checked arguments (upstream <c>CheckedArgs</c>): a method's arguments in the current shape with the result mapping back to the
/// caller's version, or the old arguments for the provider's own replacement (the host dispatches it).</summary>
internal abstract record CheckedArgs(JsonNode? Args)
{
    /// <summary><c>{ kind: "method", args, result? }</c>: <paramref name="Result"/> is the table's own result mapping.</summary>
    internal sealed record Method(JsonNode? Args, Func<object?, object?>? Result) : CheckedArgs(Args);

    /// <summary><c>{ kind: "call", args, call }</c>: <paramref name="Replacement"/> is the table's own <c>call</c>, taking the old
    /// arguments and the call's context.</summary>
    internal sealed record Call(JsonNode? Args, Func<object?, object, Task<object?>> Replacement) : CheckedArgs(Args);
}

internal static partial class DataInternal
{
    private enum DispatchKind { Refuse, Call, Method }

    /// <summary>A method of an accepted version: refused, a replacement, or a method with its mappings; its validator compiles on first use.</summary>
    private sealed class DispatchEntry(DispatchKind kind, Snapshot args, MethodMapping? mapping)
    {
        internal DispatchKind Kind { get; } = kind;
        internal Snapshot Args { get; } = args;
        internal MethodMapping? Mapping { get; } = mapping;
        internal Validator? Validator { get; set; }
    }

    /// <summary>Accepted version -> method -> entry.</summary>
    private sealed class Dispatch : Dictionary<long, Dictionary<string, DispatchEntry>>;

    private static readonly ConditionalWeakTable<Provider, Dispatch> DispatchTables = new();

    /// <summary>Binds a contract and the app's accepts table (validated once by <c>checkAccepts</c>, kept as it is) into a provider
    /// (upstream <c>bindProvider</c>). Builds the dispatch table: accepted version -> method -> schema and mapping; nothing is compiled
    /// and no mapping is called. A table <c>checkAccepts</c> refuses throws its <see cref="ArgumentException"/>.</summary>
    internal static Provider BindProvider(Contract contract, AcceptsTable accepts)
    {
        var accepted = ContractsInternal.CheckAccepts(contract, accepts).Versions;
        var versions = ContractsInternal.DataOf(contract).Versions;
        var current = versions[^1];
        var table = new Dispatch { [current.Version] = DispatchEntries(current, null) };
        foreach (var (version, _) in accepted)
        {
            var data = versions.First(candidate => candidate.Version == version);
            // checkAccepts established this shape: an accepted version's mappings are an object.
            table[version] = DispatchEntries(data, accepts[version]!.Methods);
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

    private static Dictionary<string, DispatchEntry> DispatchEntries(VersionData version, OrderedDictionary<string, MethodMapping?>? mappings)
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
        // The check above establishes the argument type; mapping output is trusted (once it is a value).
        if (entry.Kind == DispatchKind.Call) return new CheckedArgs.Call(raw, entry.Mapping!.Call!);
        var mapArgs = entry.Mapping?.Args;
        return new CheckedArgs.Method(mapArgs is null ? raw : MappedArgs(mapArgs(raw), name, boundVersion, method), entry.Mapping?.Result);
    }

    /// <summary>An args mapping's output as a value: a JsonNode or null. Anything else is refused, as upstream's types refuse it.</summary>
    private static JsonNode? MappedArgs(object? mapped, string name, long version, string method) => mapped switch
    {
        null => null,
        JsonNode node => node,
        _ => throw new InvalidOperationException($"{name} accepts[{version}].methods.{method}.args: returned a {mapped.GetType().Name}, not a JsonNode"),
    };
}
