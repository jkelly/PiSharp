// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/contracts/contract.ts
//
// Contract handles: the branded, immutable value an app declares and the Chord-owned data behind it. A handle carries kind, name, version
// and fingerprint only; its data is held per handle in a weak table, as upstream's WeakMap, so it lives while the handle is referenced and
// a copy of a handle (upstream's spread or hand-built copy; here a MemberwiseClone) is refused everywhere. The type-level parts of
// contract.ts (ContractShape, ShapeOf, CurrentVariants, ParamsOf, Implementation, VariantImplementation, MethodFunction) have no C#
// counterpart (see Index.cs). KeyedWatchable, the interface a keyed document handle provides, needs the state layer's Watchable and the
// context, and comes with the documents areas.
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Contracts;

/// <summary>Whether a contract is remote (per-principal variants, served at a host address) or local (one flat map of members).</summary>
public enum ContractKind
{
    /// <summary><c>remote(...)</c>.</summary>
    Remote,

    /// <summary><c>local(...)</c>.</summary>
    Local,
}

/// <summary>A declared contract (upstream <c>Contract</c>): a branded handle, immutable by convention, made by <see cref="Remote"/> or
/// <see cref="Local"/>. Copies of a handle are refused.</summary>
public sealed partial class Contract
{
    private Contract(ContractKind kind, string name, long version, string fingerprint)
    {
        Kind = kind;
        Name = name;
        Version = version;
        Fingerprint = fingerprint;
    }

    /// <summary>Remote or local.</summary>
    public ContractKind Kind { get; }

    /// <summary>The contract's name (its object type).</summary>
    public string Name { get; }

    /// <summary>The current version.</summary>
    public long Version { get; }

    /// <summary>The current version's fingerprint: 16 hex characters, the same a TypeScript declaration of the contract has.</summary>
    public string Fingerprint { get; }

    /// <summary>A new contract handle for <paramref name="data"/>.</summary>
    internal static Contract Create(ContractData data)
    {
        var current = data.Versions[^1];
        var contract = new Contract(data.Kind, data.Name, current.Version, current.Fingerprint);
        ContractsInternal.Contracts.Add(contract, data);
        return contract;
    }
}

/// <summary>The kind of a member name: state, a method, or a local contract's keyed state.</summary>
internal enum MemberKind
{
    State,
    Method,
    Keyed,
}

/// <summary>The Chord-owned data behind a contract handle.</summary>
/// <param name="HostAddress">A remote contract's host address: the address of the host serving an object for its params; none for local.
/// Plugin code: what it returns is checked where routed.</param>
/// <param name="Versions">Every version of the history, oldest first; the last is current.</param>
/// <param name="Kinds">The kind of every member name ever declared in the history.</param>
internal sealed record ContractData(
    ContractKind Kind,
    string Name,
    ParamsData Params,
    Func<JsonObject, Address>? HostAddress,
    IReadOnlyList<VersionData> Versions,
    IReadOnlyDictionary<string, MemberKind> Kinds);

/// <summary>One version of a contract's history.</summary>
/// <param name="Variants">The version's variants by name, in declaration order.</param>
/// <param name="Changes">The changes that produced this version; empty for a base.</param>
internal sealed record VersionData(long Version, string Fingerprint, IReadOnlyDictionary<string, VariantData> Variants, IReadOnlyList<Change> Changes);

/// <summary>One variant's members, each map in declaration order.</summary>
/// <param name="Keyed">Keyed state members (slice 6a §12): only a local contract has any.</param>
internal sealed record VariantData(
    IReadOnlyDictionary<string, Snapshot> State,
    IReadOnlyDictionary<string, MethodData> Methods,
    IReadOnlyDictionary<string, Snapshot> Keyed);

/// <summary>A method's argument and result snapshots.</summary>
internal sealed record MethodData(Snapshot Args, Snapshot Result);

/// <summary>A variant's names as links index them (2b): its state members and its methods, each sorted.</summary>
internal sealed record VariantNames(IReadOnlyList<string> State, IReadOnlyList<string> Methods);

internal static partial class ContractsInternal
{
    /// <summary>The variant name of a local contract's members.</summary>
    internal const string Local = "members";

    internal static readonly ConditionalWeakTable<Contract, ContractData> Contracts = new();

    /// <summary>The data behind a contract handle (upstream <c>contractData</c>); refuses copies of a handle.</summary>
    internal static ContractData DataOf(Contract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return Contracts.TryGetValue(contract, out var data) ? data
            : throw new ArgumentException($"{contract.Name}: not a declared contract (spread and hand-built copies are refused)");
    }

    /// <summary>Whether a method takes no arguments: its argument schema is <c>null</c>, so its functions omit the argument.</summary>
    internal static bool TakesNoArgs(MethodData method) =>
        // A schema's normal form is an object of keywords.
        method.Args.Normal is JsonObject normal && ChordJson.AsString(normal["type"]) == "null" && normal.Count == 1;

    /// <summary>The variants of a contract's current version; a local contract has the one variant <see cref="Local"/>.</summary>
    internal static IReadOnlyDictionary<string, VariantData> CurrentVariants(Contract contract) => DataOf(contract).Versions[^1].Variants;

    /// <summary>The variants of version <paramref name="version"/> of a contract's history (an older consumer's binding, Q10).</summary>
    internal static IReadOnlyDictionary<string, VariantData> VariantsAt(Contract contract, long version) =>
        // Callers name a version the provider binds (`binds`): its history has it.
        DataOf(contract).Versions.First(data => data.Version == version).Variants;

    /// <summary>The current version's variant names in the order links index them: sorted.</summary>
    internal static IReadOnlyList<string> VariantOrder(Contract contract) => [.. CurrentVariants(contract).Keys.Order(StringComparer.Ordinal)];

    /// <summary>A variant's state members and methods in the order links index them: each sorted (upstream <c>variantNames</c>).</summary>
    internal static VariantNames VariantNamesOf(VariantData variant) =>
        new([.. variant.State.Keys.Order(StringComparer.Ordinal)], [.. variant.Methods.Keys.Order(StringComparer.Ordinal)]);

    /// <summary>The name a contract kind has in the normal form.</summary>
    internal static string WireName(ContractKind kind) => kind == ContractKind.Local ? "local" : "remote";
}
