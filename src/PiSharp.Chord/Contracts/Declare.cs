// Pi fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT): packages/chord/src/contracts/declare.ts (v1.7.0 updates).
// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/contracts/declare.ts
//
// Declarations: Contract.Remote and Contract.Local, the checks every declared version passes, and the reading of declaration input.
// Upstream reads untyped input because JavaScript callers reach every check; the C# declarations are typed, so a declaration cannot carry
// an unknown key, a version that is no integer or a host address that is no function (checkKeys has nothing to refuse). The rest of
// upstream's reading is kept: a member is a Member (a schema, a method or a keyed member, upstream's LocalMember) in remote variants too,
// so a method under state, a schema under methods and a keyed member in a remote contract reach their refusals, and a missing (null) name,
// params, variants, members or host address is refused with upstream's message. The required members are TypeScript's required keys; D11
// (SameMembers), RemoteVariants and LocalVariants are type-level and are checked here when the declaration runs. 1.6.0: a member is also
// an event (Schemas.Event in a local contract, a variant's Events in a remote one) or a local contract's queries member
// (Schemas.Queries), and a remote contract may declare its admission's schema. Upstream's `doc: unknown key "admission"` for a local
// declaration cannot be written: LocalDeclaration has no such property.
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Contracts;

/// <summary>A member of a local contract, or of a remote contract's variant: a schema (state, or under a variant's events an event's
/// values), a <see cref="MethodDeclaration"/>, a <see cref="KeyedMember"/>, an <see cref="EventDeclaration"/> or a
/// <see cref="QueriesMember"/> (upstream <c>Schema | Method | KeyedMember | EventDeclaration | QueriesMember</c>).</summary>
public readonly struct Member
{
    private Member(object? value) => Value = value;

    internal object? Value { get; }

    /// <summary>A state member of this schema.</summary>
    public static implicit operator Member(JsonNode? schema) => new(schema);

    /// <summary>A method.</summary>
    public static implicit operator Member(MethodDeclaration method) => new(method);

    /// <summary>A keyed state member (local contracts only).</summary>
    public static implicit operator Member(KeyedMember keyed) => new(keyed);

    /// <summary>An event member of a local contract.</summary>
    public static implicit operator Member(EventDeclaration @event) => new(@event);

    /// <summary>A <c>queries</c> member (local contracts only).</summary>
    public static implicit operator Member(QueriesMember queries) => new(queries);
}

/// <summary>A remote contract's declaration: per-principal variants of state, methods and events, and optionally its admission.</summary>
public sealed class RemoteDeclaration
{
    /// <summary>The version: a positive integer.</summary>
    public required long Version { get; init; }

    /// <summary>The params, in identity order: required <c>Str()</c>, <c>Int()</c>, <c>Bool()</c> or <c>OneOf(...)</c> of strings.</summary>
    public required OrderedDictionary<string, Property> Params { get; init; }

    /// <summary>The address of the host serving an object for these params; never part of the fingerprint.</summary>
    public required Func<JsonObject, Address> HostAddress { get; init; }

    /// <summary>The schema of the value the type's admission hands its factory (slice 14 §2.1): small, about the caller (a mode, a role),
    /// JSON only. It selects the variant and the members. Never part of the fingerprint, never validated at run time; null for a contract
    /// without an admission.</summary>
    public JsonNode? Admission { get; init; }

    /// <summary>The variants by name; at least one.</summary>
    public required OrderedDictionary<string, VariantDeclaration> Variants { get; init; }
}

/// <summary>One variant of a remote contract: its state schemas, its methods and its event members.</summary>
public sealed class VariantDeclaration
{
    /// <summary>The state members: a schema each.</summary>
    public OrderedDictionary<string, Member> State { get; init; } = new();

    /// <summary>The methods: a <see cref="Schemas.Method()"/> each.</summary>
    public OrderedDictionary<string, Member> Methods { get; init; } = new();

    /// <summary>The event members: the schema of each one's values (JSON only).</summary>
    public OrderedDictionary<string, Member> Events { get; init; } = new();
}

/// <summary>A local contract's declaration: one flat map of members.</summary>
public sealed class LocalDeclaration
{
    /// <summary>The version: a positive integer.</summary>
    public required long Version { get; init; }

    /// <summary>The params, in identity order: required <c>Str()</c>, <c>Int()</c>, <c>Bool()</c> or <c>OneOf(...)</c> of strings.</summary>
    public required OrderedDictionary<string, Property> Params { get; init; }

    /// <summary>The members: a schema is state, <see cref="Schemas.Method()"/> a method, <see cref="Schemas.Keyed(JsonNode)"/> a keyed state
    /// member, <see cref="Schemas.Event"/> an event member and <see cref="Schemas.Queries"/> a <c>queries</c> member.</summary>
    public required OrderedDictionary<string, Member> Members { get; init; }
}

public sealed partial class Contract
{
    /// <summary>Declares a remote contract: per-principal variants of state, methods and events, and optionally its admission, the JSON
    /// value its provider reads about a caller before the factory runs (slice 14 §2.1).</summary>
    public static Contract Remote(string name, RemoteDeclaration declaration)
    {
        ContractsInternal.CheckName(name, "A contract name");
        if (declaration is null) throw new ArgumentException($"{name}: expected an object");
        var variants = ContractsInternal.EntriesOf(declaration.Variants, $"{name}.variants");
        if (variants.Count == 0) throw new ArgumentException($"{name}.variants: at least one variant is required");
        var hostAddress = declaration.HostAddress ?? throw new ArgumentException($"{name}.hostAddress: expected a function of the params");
        var read = new OrderedDictionary<string, VariantData>(StringComparer.Ordinal);
        foreach (var (variant, members) in variants) read[variant] = ReadVariant(members, $"{name}.{variant}");
        var admitted = declaration.Admission is null ? null : ContractsInternal.AdmissionDataOf(declaration.Admission, $"{name}.admission");
        // The host address gets the checked params of this contract.
        return Declare(ContractKind.Remote, name, declaration.Version, declaration.Params, read, hostAddress, admitted);
    }

    /// <summary>Declares a local contract: one flat map of members, where a schema is state, a method declaration a method, a keyed
    /// member a keyed state member, an event declaration an event member and a queries member its named queries.</summary>
    public static Contract Local(string name, LocalDeclaration declaration)
    {
        ContractsInternal.CheckName(name, "A contract name");
        if (declaration is null) throw new ArgumentException($"{name}: expected an object");
        var state = new OrderedDictionary<string, Snapshot>(StringComparer.Ordinal);
        var methods = new OrderedDictionary<string, MethodData>(StringComparer.Ordinal);
        var keyed = new OrderedDictionary<string, Snapshot>(StringComparer.Ordinal);
        var events = new OrderedDictionary<string, Snapshot>(StringComparer.Ordinal);
        var queries = new OrderedDictionary<string, QueriesData>(StringComparer.Ordinal);
        foreach (var (member, value) in ContractsInternal.EntriesOf(declaration.Members, $"{name}.{ContractsInternal.Local}"))
        {
            var path = $"{name}.{ContractsInternal.Local}.{member}";
            if (value.Value is MethodDeclaration method) methods[member] = ContractsInternal.ReadMethod(method, path);
            else if (value.Value is QueriesMember queriesMember) queries[member] = ContractsInternal.ReadQueries(queriesMember, path);
            else if (value.Value is KeyedMember keyedMember) keyed[member] = ContractsInternal.SnapshotSchema(keyedMember.Schema, path) with { KeyHost = keyedMember.KeyHost };
            else if (value.Value is EventDeclaration eventMember) events[member] = ContractsInternal.EventSnapshot(eventMember.Schema, path);
            else state[member] = ContractsInternal.SnapshotSchema(value.Value, path);
        }
        var variants = new OrderedDictionary<string, VariantData>(StringComparer.Ordinal)
        {
            [ContractsInternal.Local] = new VariantData(state, methods, keyed, events, queries),
        };
        return Declare(ContractKind.Local, name, declaration.Version, declaration.Params, variants);
    }

    private static VariantData ReadVariant(VariantDeclaration? source, string path)
    {
        if (source is null) throw new ArgumentException($"{path}: expected an object");
        var state = new OrderedDictionary<string, Snapshot>(StringComparer.Ordinal);
        foreach (var (member, schema) in source.State ?? new())
        {
            if (schema.Value is MethodDeclaration) throw new ArgumentException($"{path}.state.{member}: a method belongs in methods");
            if (schema.Value is KeyedMember) throw new ArgumentException($"{path}.state.{member}: keyed members are only in local contracts");
            if (schema.Value is EventDeclaration) throw new ArgumentException($"{path}.state.{member}: an event belongs in events");
            if (schema.Value is QueriesMember) throw new ArgumentException($"{path}.state.{member}: queries members are only in local contracts");
            state[member] = ContractsInternal.SnapshotSchema(schema.Value, $"{path}.state.{member}");
        }
        var methods = new OrderedDictionary<string, MethodData>(StringComparer.Ordinal);
        foreach (var (member, method) in source.Methods ?? new())
        {
            if (method.Value is not MethodDeclaration declared) throw new ArgumentException($"{path}.methods.{member}: expected method(...)");
            methods[member] = ContractsInternal.ReadMethod(declared, $"{path}.methods.{member}");
        }
        var events = new OrderedDictionary<string, Snapshot>(StringComparer.Ordinal);
        foreach (var (member, schema) in source.Events ?? new())
        {
            if (schema.Value is MethodDeclaration or KeyedMember or EventDeclaration)
                throw new ArgumentException($"{path}.events.{member}: expected the schema of the event's values");
            events[member] = ContractsInternal.EventSnapshot(schema.Value, $"{path}.events.{member}");
        }
        return new VariantData(state, methods, new OrderedDictionary<string, Snapshot>(StringComparer.Ordinal), events,
            new OrderedDictionary<string, QueriesData>(StringComparer.Ordinal));
    }

    private static Contract Declare(ContractKind kind, string name, long version, OrderedDictionary<string, Property>? @params,
        IReadOnlyDictionary<string, VariantData> variants, Func<JsonObject, Address>? hostAddress = null, AdmissionData? admission = null)
    {
        // Number.isSafeInteger(version) && version >= 1.
        if (version < 1 || version > 9007199254740991) throw new ArgumentException($"{name}.version: expected a positive integer");
        var kinds = new OrderedDictionary<string, MemberKind>(StringComparer.Ordinal);
        ContractsInternal.CheckMembers(variants, kinds, (variant, member, memberKind) => ContractsInternal.MemberPath(name, kind, variant, member, memberKind));
        var data = ContractsInternal.ReadParams(ContractsInternal.EntriesOf(@params, $"{name}.params"), $"{name}.params");
        var versions = new[] { ContractsInternal.Materialize(kind, data, version, variants, []) };
        return Create(new ContractData(kind, name, data, hostAddress, versions, kinds, admission));
    }
}

internal static partial class ContractsInternal
{
    /// <summary>Names a reference has itself (<c>release</c>, <c>variant</c>, and <c>state</c> and <c>retry</c> with failures), and
    /// <c>then</c>, which would make an awaited reference thenable.</summary>
    private static readonly HashSet<string> ReservedMembers = new(["then", "release", "variant", "state", "retry"], StringComparer.Ordinal);

    /// <summary>One method declaration as Chord-owned snapshots; its arguments must be closed.</summary>
    internal static MethodData ReadMethod(MethodDeclaration method, string path) =>
        new(SnapshotArguments(method.Args, $"{path}.args"), SnapshotSchema(method.Result, $"{path}.result"));

    /// <summary>An admission schema as Chord-owned data, with its fingerprint (upstream <c>admissionData</c>): JSON only (declaration
    /// policy: no <c>bytes()</c> leaf).</summary>
    internal static AdmissionData AdmissionDataOf(object? schema, string path)
    {
        var snapshot = SnapshotSchema(schema, path);
        if (snapshot.Bytes) throw new ArgumentException($"{path}: an admission's value is JSON only (no bytes())");
        return new AdmissionData(snapshot, FingerprintOf(snapshot.Normal));
    }

    /// <summary>An event member's snapshot (upstream <c>eventSnapshot</c>): JSON only (declaration policy: no <c>bytes()</c> leaf, events
    /// travel as JSON parts).</summary>
    internal static Snapshot EventSnapshot(object? schema, string path)
    {
        var snapshot = SnapshotSchema(schema, path);
        if (snapshot.Bytes) throw new ArgumentException($"{path}: an event's values are JSON only (no bytes())");
        return snapshot;
    }

    /// <summary>A <c>queries</c> member as Chord-owned snapshots (upstream <c>readQueries</c>): its items' schema, and each named query's
    /// arguments, closed. The arguments are read before the item, as upstream reads them.</summary>
    internal static QueriesData ReadQueries(QueriesMember member, string path)
    {
        var args = new OrderedDictionary<string, Snapshot>(StringComparer.Ordinal);
        foreach (var (query, schema) in member.Declared) args[query] = QueryArguments(schema, $"{path}.args.{query}");
        return new QueriesData(SnapshotSchema(member.Item, $"{path}.item"), args);
    }

    internal static Snapshot QueryArguments(object? schema, string path)
    {
        var snapshot = SnapshotArguments(schema, path);
        if (snapshot.Bytes) throw new ArgumentException($"{path}: a query's arguments are JSON only (no bytes())");
        return snapshot;
    }

    /// <summary>Checks the members of every variant: a name keeps one kind across variants and the whole history (<paramref name="kinds"/>,
    /// updated here), a method one signature across variants.</summary>
    internal static void CheckMembers(IReadOnlyDictionary<string, VariantData> variants, IDictionary<string, MemberKind> kinds,
        Func<string, string, MemberKind, string> path)
    {
        var signatures = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (variant, data) in variants)
        {
            foreach (var member in data.Queries.Keys) RecordKind(kinds, member, MemberKind.Queries, path(variant, member, MemberKind.Queries));
            foreach (var member in data.State.Keys) RecordKind(kinds, member, MemberKind.State, path(variant, member, MemberKind.State));
            foreach (var member in data.Keyed.Keys) RecordKind(kinds, member, MemberKind.Keyed, path(variant, member, MemberKind.Keyed));
            foreach (var member in data.Events.Keys) RecordKind(kinds, member, MemberKind.Event, path(variant, member, MemberKind.Event));
            foreach (var (member, method) in data.Methods)
            {
                RecordKind(kinds, member, MemberKind.Method, path(variant, member, MemberKind.Method));
                var signature = "[" + ChordJson.Stringify(method.Args.Normal) + "," + ChordJson.Stringify(method.Result.Normal) + "]";
                if ((signatures.GetValueOrDefault(member) ?? signature) != signature)
                    throw new ArgumentException($"{path(variant, member, MemberKind.Method)}: a method has the same signature in every variant");
                signatures[member] = signature;
            }
        }
    }

    private static void RecordKind(IDictionary<string, MemberKind> kinds, string member, MemberKind kind, string path)
    {
        if (ReservedMembers.Contains(member)) throw new ArgumentException($"{path}: \"{member}\" is reserved: a reference has it");
        if (kinds.TryGetValue(member, out var known) && known != kind)
            throw new ArgumentException($"{path}: \"{member}\" is {(known == MemberKind.Event ? "an" : "a")} {KindName(known)} member; its kind never changes");
        kinds[member] = kind;
    }

    /// <summary>The path of a member in errors: <c>notes.editor.methods.add</c>, <c>game.player.events.paddle</c>,
    /// <c>info.members.clear</c>.</summary>
    internal static string MemberPath(string @base, ContractKind kind, string variant, string member, MemberKind memberKind) =>
        kind == ContractKind.Local ? $"{@base}.{Local}.{member}" : $"{@base}.{variant}.{Section(memberKind)}.{member}";

    /// <summary>The section of a remote variant a member of each kind is declared in (keyed and queries members: only local contracts).</summary>
    private static string Section(MemberKind kind) => kind switch { MemberKind.Method => "methods", MemberKind.Event => "events", _ => "state" };

    /// <summary>The entries of a declared map, in order; a missing (null) map is refused.</summary>
    internal static List<KeyValuePair<string, T>> EntriesOf<T>(OrderedDictionary<string, T>? source, string path) =>
        source is null ? throw new ArgumentException($"{path}: expected an object") : [.. source];

    private static string KindName(MemberKind kind) => kind switch
    {
        MemberKind.State => "state",
        MemberKind.Method => "method",
        MemberKind.Keyed => "keyed",
        MemberKind.Event => "event",
        _ => "queries",
    };
}
