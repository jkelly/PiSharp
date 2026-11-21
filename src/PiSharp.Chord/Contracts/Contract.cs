// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/contracts/contract.ts
//
// Contract handles: the branded, immutable value an app declares and the Chord-owned data behind it. A handle carries kind, name, version
// and fingerprint only; its data is held per handle in a weak table, as upstream's WeakMap, so it lives while the handle is referenced and
// a copy of a handle (upstream's spread or hand-built copy; here a MemberwiseClone) is refused everywhere. The type-level parts of
// contract.ts (ContractShape, ShapeOf, CurrentVariants, ParamsOf, Implementation, VariantImplementation, MethodFunction) have no C#
// counterpart (see Index.cs). KeyedWatchable, the interface a keyed document handle provides, needs the state layer's Watchable and the
// context, and comes with the documents areas. The 1.6.0 additions: MemberNames (upstream memberNames), the named query, query page and
// event member interfaces that implementations hand out (NamedQuery, QueryPage, EventMember, EventSource, EventSubscription), a remote
// contract's admission data, and the events and queries of a variant. AdmittedOf and NamedQueriesOf are type-level: an admitted value
// and a query's arguments and items are JsonNode here, checked where a host reads them.
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Chord.Store;

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

    /// <summary>The names of the state members, methods and event members of <paramref name="variant"/> of this contract's current version
    /// (a local contract has one variant: leave <paramref name="variant"/> out), in the order declared: for code that fills an
    /// implementation from another object's members. A local contract's keyed and <c>queries</c> members are not among them (upstream
    /// <c>memberNames(contract, variant)</c>).</summary>
    public MemberNames MemberNames(string? variant = null) => ContractsInternal.MemberNamesOf(this, variant ?? ContractsInternal.Local);

    /// <summary>A new contract handle for <paramref name="data"/>.</summary>
    internal static Contract Create(ContractData data)
    {
        var current = data.Versions[^1];
        var contract = new Contract(data.Kind, data.Name, current.Version, current.Fingerprint);
        ContractsInternal.Contracts.Add(contract, data);
        return contract;
    }
}

/// <summary>The kind of a member name: state, a method, a local contract's keyed state, an event member, or a local contract's
/// <c>queries</c> member (slice 14 §3.1).</summary>
internal enum MemberKind
{
    State,
    Method,
    Keyed,
    Event,
    Queries,
}

/// <summary>The member names of one variant of a contract, by kind, in the order declared (upstream <c>MemberNames</c>).</summary>
/// <param name="State">The state members.</param>
/// <param name="Methods">The methods.</param>
/// <param name="Events">The event members.</param>
public sealed record MemberNames(IReadOnlyList<string> State, IReadOnlyList<string> Methods, IReadOnlyList<string> Events);

/// <summary>A page of a named query's items and, when more follow, the cursor to continue after it (upstream <c>QueryPage</c>).</summary>
/// <param name="Items">The items: values of the <c>queries</c> member's item schema.</param>
/// <param name="Next">The cursor of the next page; null when this is the last.</param>
public sealed record QueryPage(IReadOnlyList<JsonNode?> Items, string? Next = null);

/// <summary>A named query (upstream <c>NamedQuery</c>, slice 14 §3.1): a query a store offers under a name, with arguments and items of the
/// schemas its <c>queries</c> member declares. Read live with <c>self.LiveQuery</c>, counted with <c>self.Count</c>, shown as a list with
/// <c>self.List</c>, or read a page at a time.</summary>
public interface INamedQuery
{
    /// <summary>Up to <paramref name="limit"/> items for <paramref name="args"/> (after <c>options.After</c>), and the cursor of the next
    /// page when there is one.</summary>
    Task<QueryPage> PageAsync(JsonNode? args, long limit, ChordContext context, PageOptions? options = null);
}

/// <summary>One event as a subscriber gets it: its value, and the emitting host's Unix time in milliseconds at the emit (upstream's
/// <c>{ value, at }</c>).</summary>
public sealed record ChordEvent(JsonNode? Value, long At);

/// <summary>A subscription to an event member (upstream <c>EventSubscription</c>): it lives until <see cref="Stop"/>, its context's abort
/// or its reference's release.</summary>
public interface IEventSubscription
{
    /// <summary>Ends the subscription; its listener is not called again, a call already due included. Idempotent.</summary>
    void Stop();
}

/// <summary>An event member as its readers have it (upstream <c>EventMember</c>, decision "Events: fire-and-forget members"): a
/// reference's, which receives the values its object emits while it is bound to an instance whose variant has the member, never one
/// emitted before it bound, never replayed. Not a watchable: no value, no derive or routine source.</summary>
public interface IEventMember
{
    /// <summary>Subscribes <paramref name="listener"/> at once, bound or not: it gets every event delivered to this member from now on.
    /// Fails <c>released</c> for a released reference and with the abort reason of an aborted <paramref name="context"/>.</summary>
    Task<IEventSubscription> OnAsync(Action<ChordEvent> listener, ChordContext context);
}

/// <summary>An event member as its object owns it (upstream <c>EventSource</c>, <c>self.Events()</c>): it emits.</summary>
public interface IEventSource : IEventMember
{
    /// <summary>Emits <paramref name="value"/> now, outside any transaction: delivered after every state change published before the
    /// event flush, to the references bound before this emit. Throws <c>released</c> after its attempt's seal.</summary>
    void Emit(JsonNode? value);
}

/// <summary>The Chord-owned data behind a contract handle.</summary>
/// <param name="HostAddress">A remote contract's host address: the address of the host serving an object for its params; none for local.
/// Plugin code: what it returns is checked where routed.</param>
/// <param name="Versions">Every version of the history, oldest first; the last is current.</param>
/// <param name="Kinds">The kind of every member name ever declared in the history.</param>
/// <param name="Admission">A remote contract's admission, as its current version declares it; none without one, and for local.</param>
internal sealed record ContractData(
    ContractKind Kind,
    string Name,
    ParamsData Params,
    Func<JsonObject, Address>? HostAddress,
    IReadOnlyList<VersionData> Versions,
    IReadOnlyDictionary<string, MemberKind> Kinds,
    AdmissionData? Admission = null);

/// <summary>A contract's admission (slice 14 §2.1): the schema of the value its factory is handed, and that schema's fingerprint. Never
/// part of the contract's fingerprint; a reload's staging compares it (the value is never checked).</summary>
internal sealed record AdmissionData(Snapshot Schema, string Fingerprint);

/// <summary>One version of a contract's history.</summary>
/// <param name="Variants">The version's variants by name, in declaration order.</param>
/// <param name="Changes">The changes that produced this version; empty for a base.</param>
internal sealed record VersionData(long Version, string Fingerprint, IReadOnlyDictionary<string, VariantData> Variants, IReadOnlyList<Change> Changes);

/// <summary>One variant's members, each map in declaration order.</summary>
/// <param name="Keyed">Keyed state members (slice 6a §12): only a local contract has any.</param>
/// <param name="Events">Event members: their values' schemas (JSON only).</param>
/// <param name="Queries"><c>queries</c> members (slice 14 §3.1): only a local contract has any.</param>
internal sealed record VariantData(
    IReadOnlyDictionary<string, Snapshot> State,
    IReadOnlyDictionary<string, MethodData> Methods,
    IReadOnlyDictionary<string, Snapshot> Keyed,
    IReadOnlyDictionary<string, Snapshot> Events,
    IReadOnlyDictionary<string, QueriesData> Queries);

/// <summary>A <c>queries</c> member's snapshots: its items' schema, and each named query's arguments, in declaration order.</summary>
internal sealed record QueriesData(Snapshot Item, IReadOnlyDictionary<string, Snapshot> Args);

/// <summary>A method's argument and result snapshots.</summary>
internal sealed record MethodData(Snapshot Args, Snapshot Result);

/// <summary>A variant's names as links index them (2b): its state members, its methods and its event members, each sorted.</summary>
internal sealed record VariantNames(IReadOnlyList<string> State, IReadOnlyList<string> Methods, IReadOnlyList<string> Events);

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

    /// <summary>A variant's state members, methods and event members in the order links index them: each sorted (upstream
    /// <c>variantNames</c>); a variant without event members shares one empty list (contracts without events pay nothing for them per
    /// binding).</summary>
    internal static VariantNames VariantNamesOf(VariantData variant) =>
        new([.. variant.State.Keys.Order(StringComparer.Ordinal)], [.. variant.Methods.Keys.Order(StringComparer.Ordinal)],
            variant.Events.Count == 0 ? NoNames : [.. variant.Events.Keys.Order(StringComparer.Ordinal)]);

    private static readonly IReadOnlyList<string> NoNames = [];

    /// <summary>The names of the state members, methods and event members of <paramref name="variant"/> of a contract's current version, in
    /// the order declared (upstream <c>memberNames</c>).</summary>
    internal static MemberNames MemberNamesOf(Contract contract, string variant)
    {
        if (!CurrentVariants(contract).TryGetValue(variant, out var found)) throw new ArgumentException($"{contract.Name}: no variant \"{variant}\"");
        return new MemberNames([.. found.State.Keys], [.. found.Methods.Keys], [.. found.Events.Keys]);
    }

    /// <summary>The name a contract kind has in the normal form.</summary>
    internal static string WireName(ContractKind kind) => kind == ContractKind.Local ? "local" : "remote";
}
