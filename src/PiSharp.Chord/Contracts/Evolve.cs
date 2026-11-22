// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/contracts/evolve.ts
//
// Evolution: Contract.Evolve(previous, e => e...) appends the next version of a history with structured edits. Upstream hands a remote
// contract's evolve a RemoteEditor (the draft itself) and a local one's a LocalEditor (the same edits with the variant filled in), and
// its types track every edit (MethodEditor, Put, Drop, PutKeyed, DropKeyed, Evolved, Increment), so an implementation and an accepts
// table see each version's members. C# has no literal or mapped types and a Contract is not generic, so there is one ContractEditor with
// both kinds' edits: the remote forms name a variant, the local ones do not, and the keyed edits are local only. An edit of the other
// kind's form, which upstream's types rule out, is refused when it runs (a PiSharp-only refusal, as declarations check kinds when they
// run). Every other check and message is upstream's. 1.6.0: event edits (Event, AddEvent, RemoveEvent; both forms), queries members
// carried over as they are, and Admission(schema) for a remote contract. Upstream makes admission(schema) the last edit of a version
// through its return type (it returns no editor); here it returns the editor, as evolve must get its editor back, and an edit made after
// it is refused when it runs (a PiSharp-only refusal standing in for the type).
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Contracts;

public sealed partial class Contract
{
    /// <summary>Appends the next version (<c>previous.Version + 1</c>) with the edits <paramref name="edit"/> makes; it must return the
    /// editor it was given. Params and the variant set never change: a change to either is a new history base (a new declaration).
    /// <paramref name="previous"/> is left untouched.</summary>
    public static Contract Evolve(Contract previous, Func<ContractEditor, ContractEditor> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var editor = new ContractEditor(ContractsInternal.DataOf(previous));
        ContractsInternal.EditWith(editor, edit, $"{editor.Path}: evolve");
        return Create(editor.Next());
    }
}

/// <summary>The edits of one <see cref="Contract.Evolve"/> call: the next version under construction (upstream's <c>Draft</c>, the
/// <c>RemoteEditor</c> and <c>LocalEditor</c>). Every edit is checked as it is made and returns this editor. A remote contract's edits name
/// the variant; a local contract's do not, and only a local contract has keyed members.</summary>
public sealed class ContractEditor
{
    private readonly ContractData _data;
    private readonly long _version;
    private readonly OrderedDictionary<string, DraftVariant> _variants = new(StringComparer.Ordinal);
    private readonly OrderedDictionary<string, VariantData> _views = new(StringComparer.Ordinal);
    private readonly OrderedDictionary<string, MemberKind> _kinds;
    private readonly List<Change> _changes = [];
    // The admission of the next version: the previous one's unless Admission(schema) replaced it.
    private AdmissionData? _admission;
    // Set by Admission(schema): the last edit of a version.
    private bool _sealed;

    internal ContractEditor(ContractData data)
    {
        var current = data.Versions[^1];
        _data = data;
        _version = current.Version + 1;
        Path = $"{data.Name} v{_version}";
        foreach (var (name, variant) in current.Variants)
        {
            // `queries` members are carried over as they are: no edit changes one yet (slice 14, carry-over).
            var draft = new DraftVariant(Copy(variant.State), Copy(variant.Methods), Copy(variant.Keyed), Copy(variant.Events), variant.Queries);
            _variants[name] = draft;
            // A view over the draft's maps, for the member checks.
            _views[name] = new VariantData(draft.State, draft.Methods, draft.Keyed, draft.Events, draft.Queries);
        }
        _kinds = Copy(data.Kinds);
        _admission = data.Admission;
    }

    /// <summary>The next version's name in errors: <c>notes v2</c>.</summary>
    internal string Path { get; }

    private bool IsLocal => _data.Kind == ContractKind.Local;

    /// <summary>Changes state member <paramref name="member"/> of <paramref name="variant"/> (a remote contract).</summary>
    public ContractEditor State(string variant, string member, JsonNode? schema) => ChangeState(RemoteVariant(variant), member, schema);

    /// <summary>Replaces the schema of the admission's value (slice 14 §2.5), or gives the contract one: the last edit of a version, and a
    /// remote contract's only. The wire fingerprint is untouched; a reload rehearses the new factory only with a value of an equal
    /// admission schema.</summary>
    public ContractEditor Admission(JsonNode? schema)
    {
        Open();
        if (IsLocal) throw new ArgumentException($"{Path}: an admission belongs to a remote contract");
        _admission = ContractsInternal.AdmissionDataOf(schema, $"{Path}.admission");
        _sealed = true;
        return this;
    }

    /// <summary>Changes event member <paramref name="member"/>'s schema in <paramref name="variant"/> (a remote contract): an older
    /// consumer gets each value mapped, or is refused.</summary>
    public ContractEditor Event(string variant, string member, JsonNode? schema) => ChangeEvent(RemoteVariant(variant), member, schema);

    /// <summary>Changes event member <paramref name="member"/>'s schema (a local contract).</summary>
    public ContractEditor Event(string member, JsonNode? schema) => ChangeEvent(LocalVariant(), member, schema);

    /// <summary>Adds event member <paramref name="member"/> to <paramref name="variant"/> (a remote contract).</summary>
    public ContractEditor AddEvent(string variant, string member, JsonNode? schema) => AddEventTo(RemoteVariant(variant), member, schema);

    /// <summary>Adds event member <paramref name="member"/> (a local contract).</summary>
    public ContractEditor AddEvent(string member, JsonNode? schema) => AddEventTo(LocalVariant(), member, schema);

    /// <summary>Removes event member <paramref name="member"/> from <paramref name="variant"/> (a remote contract).</summary>
    public ContractEditor RemoveEvent(string variant, string member) => RemoveEventFrom(RemoteVariant(variant), member);

    /// <summary>Removes event member <paramref name="member"/> (a local contract).</summary>
    public ContractEditor RemoveEvent(string member) => RemoveEventFrom(LocalVariant(), member);

    /// <summary>Changes state member <paramref name="member"/> (a local contract).</summary>
    public ContractEditor State(string member, JsonNode? schema) => ChangeState(LocalVariant(), member, schema);

    /// <summary>Adds state member <paramref name="member"/> to <paramref name="variant"/> (a remote contract).</summary>
    public ContractEditor AddState(string variant, string member, JsonNode? schema) => AddStateTo(RemoteVariant(variant), member, schema);

    /// <summary>Adds state member <paramref name="member"/> (a local contract).</summary>
    public ContractEditor AddState(string member, JsonNode? schema) => AddStateTo(LocalVariant(), member, schema);

    /// <summary>Removes state member <paramref name="member"/> from <paramref name="variant"/> (a remote contract).</summary>
    public ContractEditor RemoveState(string variant, string member) => RemoveStateFrom(RemoteVariant(variant), member);

    /// <summary>Removes state member <paramref name="member"/> (a local contract).</summary>
    public ContractEditor RemoveState(string member) => RemoveStateFrom(LocalVariant(), member);

    /// <summary>Adds method <paramref name="member"/> to <paramref name="variant"/> (a remote contract); a method another variant has
    /// takes the same signature.</summary>
    public ContractEditor AddMethod(string variant, string member, JsonNode? args, JsonNode? result) =>
        AddMethodTo(RemoteVariant(variant), member, args, result);

    /// <summary>Adds method <paramref name="member"/> (a local contract).</summary>
    public ContractEditor AddMethod(string member, JsonNode? args, JsonNode? result) => AddMethodTo(LocalVariant(), member, args, result);

    /// <summary>Changes keyed member <paramref name="member"/>'s schema (slice 7b: an older consumer reads each key mapped, or is refused).</summary>
    public ContractEditor Keyed(string member, JsonNode? schema)
    {
        var variant = KeyedVariant();
        if (!Variant(variant).Keyed.ContainsKey(member)) throw new ArgumentException($"{MemberPath(variant, member, MemberKind.Keyed)}: no such keyed member");
        return PutKeyed(variant, member, schema);
    }

    /// <summary>Changes a keyed member's schema and address kind.</summary>
    public ContractEditor Keyed(string member, HostKind host, JsonNode? schema)
    {
        var variant = KeyedVariant();
        if (!Variant(variant).Keyed.ContainsKey(member)) throw new ArgumentException($"{MemberPath(variant, member, MemberKind.Keyed)}: no such keyed member");
        return PutKeyed(variant, member, schema, host);
    }

    /// <summary>Adds keyed member <paramref name="member"/>.</summary>
    public ContractEditor AddKeyed(string member, JsonNode? schema)
    {
        var variant = KeyedVariant();
        RefuseDeclared(variant, member, MemberKind.Keyed);
        return PutKeyed(variant, member, schema);
    }

    /// <summary>Adds a keyed member whose keys are addresses of <paramref name="host"/>.</summary>
    public ContractEditor AddKeyed(string member, HostKind host, JsonNode? schema)
    {
        var variant = KeyedVariant();
        RefuseDeclared(variant, member, MemberKind.Keyed);
        return PutKeyed(variant, member, schema, host);
    }

    /// <summary>Removes keyed member <paramref name="member"/>.</summary>
    public ContractEditor RemoveKeyed(string member)
    {
        var variant = KeyedVariant();
        Open();
        if (!Variant(variant).Keyed.Remove(member)) throw new ArgumentException($"{MemberPath(variant, member, MemberKind.Keyed)}: no such keyed member");
        return this;
    }

    /// <summary>Changes method <paramref name="member"/>'s arguments in every variant that has it.</summary>
    public ContractEditor Args(string member, JsonNode? args) => ChangeMethod(member, (args, true), (null, false));

    /// <summary>Changes method <paramref name="member"/>'s result in every variant that has it.</summary>
    public ContractEditor Result(string member, JsonNode? result) => ChangeMethod(member, (null, false), (result, true));

    /// <summary>Replaces both sides of method <paramref name="member"/> in every variant that has it.</summary>
    public ContractEditor Method(string member, JsonNode? args, JsonNode? result) => ChangeMethod(member, (args, true), (result, true));

    /// <summary>Removes method <paramref name="member"/> from every variant that has it.</summary>
    public ContractEditor RemoveMethod(string member)
    {
        Open();
        foreach (var variant in VariantsWith(member)) Variant(variant).Methods.Remove(member);
        return this;
    }

    /// <summary>The contract data with this version appended.</summary>
    internal ContractData Next()
    {
        // Copies: an editor kept past its evolve call cannot change the version it produced.
        var variants = new OrderedDictionary<string, VariantData>(StringComparer.Ordinal);
        foreach (var (name, draft) in _variants)
            variants[name] = new VariantData(Copy(draft.State), Copy(draft.Methods), Copy(draft.Keyed), Copy(draft.Events), draft.Queries);
        var version = ContractsInternal.Materialize(_data.Kind, _data.Params, _version, variants, [.. _changes]);
        return _data with { Versions = [.. _data.Versions, version], Kinds = Copy(_kinds), Admission = _admission };
    }

    private ContractEditor ChangeEvent(string variant, string member, JsonNode? schema)
    {
        Open();
        if (!Variant(variant).Events.ContainsKey(member)) throw new ArgumentException($"{MemberPath(variant, member, MemberKind.Event)}: no such event member");
        return PutEvent(variant, member, schema);
    }

    private ContractEditor AddEventTo(string variant, string member, JsonNode? schema)
    {
        Open();
        RefuseDeclared(variant, member, MemberKind.Event);
        return PutEvent(variant, member, schema);
    }

    private ContractEditor RemoveEventFrom(string variant, string member)
    {
        Open();
        if (!Variant(variant).Events.Remove(member)) throw new ArgumentException($"{MemberPath(variant, member, MemberKind.Event)}: no such event member");
        return this;
    }

    private ContractEditor PutEvent(string variant, string member, JsonNode? schema)
    {
        Variant(variant).Events[member] = ContractsInternal.EventSnapshot(schema, MemberPath(variant, member, MemberKind.Event));
        _changes.Add(new Change(variant, member, ChangeSide.Event));
        return Checked();
    }

    private ContractEditor ChangeState(string variant, string member, JsonNode? schema)
    {
        if (!Variant(variant).State.ContainsKey(member)) throw new ArgumentException($"{MemberPath(variant, member, MemberKind.State)}: no such state member");
        return PutState(variant, member, schema);
    }

    private ContractEditor AddStateTo(string variant, string member, JsonNode? schema)
    {
        RefuseDeclared(variant, member, MemberKind.State);
        return PutState(variant, member, schema);
    }

    private ContractEditor RemoveStateFrom(string variant, string member)
    {
        Open();
        if (!Variant(variant).State.Remove(member)) throw new ArgumentException($"{MemberPath(variant, member, MemberKind.State)}: no such state member");
        return this;
    }

    private ContractEditor AddMethodTo(string variant, string member, JsonNode? args, JsonNode? result)
    {
        Open();
        RefuseDeclared(variant, member, MemberKind.Method);
        Variant(variant).Methods[member] = ContractsInternal.ReadMethod(new MethodDeclaration(args!, result!), MemberPath(variant, member, MemberKind.Method));
        _changes.Add(new Change(variant, member, ChangeSide.Args));
        _changes.Add(new Change(variant, member, ChangeSide.Result));
        return Checked();
    }

    private ContractEditor PutState(string variant, string member, JsonNode? schema)
    {
        Open();
        Variant(variant).State[member] = ContractsInternal.SnapshotSchema(schema, MemberPath(variant, member, MemberKind.State));
        _changes.Add(new Change(variant, member, ChangeSide.State));
        return Checked();
    }

    private ContractEditor PutKeyed(string variant, string member, JsonNode? schema, HostKind? host = null)
    {
        Open();
        if (host is not null) ContractsInternal.KindParams(host);
        Variant(variant).Keyed[member] = ContractsInternal.SnapshotSchema(schema, MemberPath(variant, member, MemberKind.Keyed)) with { KeyHost = host };
        _changes.Add(new Change(variant, member, ChangeSide.Keyed));
        return Checked();
    }

    /// <summary>Changes the given sides of a method in every variant that has it.</summary>
    private ContractEditor ChangeMethod(string member, (JsonNode? Schema, bool Changed) args, (JsonNode? Schema, bool Changed) result)
    {
        Open();
        var variants = VariantsWith(member);
        var path = MemberPath(variants[0], member, MemberKind.Method);
        var newArgs = args.Changed ? ContractsInternal.SnapshotArguments(args.Schema, $"{path}.args") : null;
        var newResult = result.Changed ? ContractsInternal.SnapshotSchema(result.Schema, $"{path}.result") : null;
        foreach (var variant in variants)
        {
            var methods = Variant(variant).Methods;
            var old = methods[member];
            methods[member] = new MethodData(newArgs ?? old.Args, newResult ?? old.Result);
            if (newArgs is not null) _changes.Add(new Change(variant, member, ChangeSide.Args));
            if (newResult is not null) _changes.Add(new Change(variant, member, ChangeSide.Result));
        }
        return Checked();
    }

    private DraftVariant Variant(string variant) =>
        _variants.TryGetValue(variant, out var found) ? found
            : throw new ArgumentException($"{Path}.{variant}: unknown variant (the variant set never changes)");

    private List<string> VariantsWith(string member)
    {
        var variants = _variants.Where(entry => entry.Value.Methods.ContainsKey(member)).Select(entry => entry.Key).ToList();
        return variants.Count == 0 ? throw new ArgumentException($"{Path}: no method \"{member}\"") : variants;
    }

    private void RefuseDeclared(string variant, string member, MemberKind kind)
    {
        var (state, methods, keyed, events, queries) = Variant(variant);
        if (state.ContainsKey(member) || methods.ContainsKey(member) || keyed.ContainsKey(member) || events.ContainsKey(member) || queries.ContainsKey(member))
            throw new ArgumentException($"{MemberPath(variant, member, kind)}: already declared");
    }

    /// <summary>Refuses an edit after <see cref="Admission"/>, which upstream's types make the last edit of a version (it returns no
    /// editor).</summary>
    private void Open()
    {
        if (_sealed) throw new ArgumentException($"{Path}: admission(schema) is the last edit of a version");
    }

    /// <summary>The variant of a remote edit; a local contract's edits name none (upstream's LocalEditor has no variant parameter).</summary>
    private string RemoteVariant(string variant) =>
        IsLocal ? throw new ArgumentException($"{Path}: a local contract's edits name no variant") : variant;

    /// <summary>The variant of a local edit; a remote contract's edits name one (upstream's RemoteEditor takes it).</summary>
    private string LocalVariant() =>
        IsLocal ? ContractsInternal.Local : throw new ArgumentException($"{Path}: a remote contract's edits name a variant");

    /// <summary>The variant of a keyed edit: keyed members are only in local contracts.</summary>
    private string KeyedVariant() =>
        IsLocal ? ContractsInternal.Local : throw new ArgumentException($"{Path}: keyed members are only in local contracts");

    private string MemberPath(string variant, string member, MemberKind kind) => ContractsInternal.MemberPath(Path, _data.Kind, variant, member, kind);

    /// <summary>A member's kind never changes, also not after a removal; a method keeps one signature across variants.</summary>
    private ContractEditor Checked()
    {
        ContractsInternal.CheckMembers(_views, _kinds, MemberPath);
        return this;
    }

    private static OrderedDictionary<string, T> Copy<T>(IReadOnlyDictionary<string, T> source)
    {
        var copy = new OrderedDictionary<string, T>(StringComparer.Ordinal);
        foreach (var (key, value) in source) copy[key] = value;
        return copy;
    }

    /// <summary>A variant under construction.</summary>
    private sealed record DraftVariant(
        OrderedDictionary<string, Snapshot> State,
        OrderedDictionary<string, MethodData> Methods,
        OrderedDictionary<string, Snapshot> Keyed,
        OrderedDictionary<string, Snapshot> Events,
        IReadOnlyDictionary<string, QueriesData> Queries);
}

internal static partial class ContractsInternal
{
    /// <summary>Runs <paramref name="edit"/> on <paramref name="editor"/> (contracts' and tables' rule): an edit returning anything but the
    /// editor it was given throws (<paramref name="what"/> names the call).</summary>
    internal static void EditWith<TEditor>(TEditor editor, Func<TEditor, object?> edit, string what)
    {
        if (!ReferenceEquals(edit(editor), editor)) throw new ArgumentException($"{what} must return the editor it was given");
    }
}
