using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/contracts/evolve.test.ts. Every run-time assertion is ported
// with upstream's message. The type-level halves are not: expectTypeOf of an implementation's members (Members, Implementation,
// Parameters, ReturnType), of a version's literal type (VersionOf: "10", "100", "101") and of the history's steps and changes
// (ShapeOf<...>["history"]), and the @ts-expect-error lines, which C# has no counterpart for: a Contract is not generic, so an
// implementation's members are bound by name where a host serves it (phase 6). Where a type-level line has a run-time counterpart, the
// case checks that instead: the members and changes the version holds, its number, and TakesNoArgs for a method whose arguments became
// null. Upstream's untyped casts (`as never`, `"missing" as "notes"`) are ordinary calls here. One editor serves both kinds (Evolve.cs):
// upstream's local editor is a different object from the remote one, and the foreign-editor case returns a local contract's editor from a
// remote contract's evolve, as upstream does.
internal static partial class Program
{
    private static readonly HostKind EvolveServer = new("server");

    private static readonly JsonObject EvolveNote = Obj(("text", Str()));

    private static readonly Contract EvolveNotesV1 = Contract.Remote("notes", new()
    {
        Version = 1,
        Params = new() { ["session"] = Str() },
        HostAddress = _ => EvolveServer.Address(),
        Variants = new()
        {
            ["editor"] = new()
            {
                State = { ["notes"] = Arr(EvolveNote), ["count"] = Int() },
                Methods = { ["add"] = Method(EvolveNote, Str()), ["clear"] = Method() },
            },
            ["reader"] = new() { State = { ["notes"] = Arr(EvolveNote) }, Methods = { ["add"] = Method(EvolveNote, Str()) } },
        },
    });

    private static readonly Contract EvolveInfoV1 = Contract.Local("info", new()
    {
        Version = 1,
        Params = new(),
        Members = new() { ["title"] = Str(), ["clear"] = Method() },
    });

    /// <summary>The current members of a contract's variant, as Chord holds them (upstream's <c>current</c>).</summary>
    private static (List<string> State, List<string> Methods, ContractData Data) EvolveCurrent(Contract contract, string variant)
    {
        var data = ContractsInternal.DataOf(contract);
        var members = data.Versions[^1].Variants[variant];
        return ([.. members.State.Keys], [.. members.Methods.Keys], data);
    }

    /// <summary>A version's changes as upstream's tuples print: <c>editor,add,args</c>.</summary>
    private static IEnumerable<string> EvolveChanges(VersionData version) =>
        version.Changes.Select(change => $"{change.Variant},{change.Member},{change.Side.ToString().ToLowerInvariant()}");

    /// <summary>toEqual of two method snapshots: the same copies, normal forms and bytes flags.</summary>
    private static string EvolveMethodText(MethodData method) =>
        string.Join("|", ChordJson.Stringify(method.Args.Schema), ChordJson.Stringify(method.Args.Normal), method.Args.Bytes,
            ChordJson.Stringify(method.Result.Schema), ChordJson.Stringify(method.Result.Normal), method.Result.Bytes);

    private static IEnumerable<(string, Func<Task>)> ContractsEvolveCases() =>
    [
        // describe("edits")
        Case("contracts.evolve.edits-state-changes-one-state-member-of-one-variant", () =>
        {
            var v2 = Contract.Evolve(EvolveNotesV1, e => e.State("editor", "count", Str()));
            Equal(2L, v2.Version, "version");
            var data = ContractsInternal.DataOf(v2);
            Equal("""{"type":"string"}""", ChordJson.Stringify(data.Versions[^1].Variants["editor"].State["count"].Schema), "editor's count");
            Equal("""{"items":{"additionalProperties":false,"properties":{"text":{"type":"string"}},"required":["text"],"type":"object"},"type":"array"}""",
                ChordJson.Stringify(data.Versions[^1].Variants["reader"].State["notes"].Normal), "reader's notes stay");
            Refuses(() => Contract.Evolve(EvolveNotesV1, e => e.State("editor", "missing", Str())), "notes v2.editor.state.missing: no such state member");
        }),
        Case("contracts.evolve.edits-add-state-adds-a-state-member", () =>
        {
            var v2 = Contract.Evolve(EvolveNotesV1, e => e.AddState("reader", "count", Int()));
            Sequence(["notes", "count"], EvolveCurrent(v2, "reader").State, "reader's state");
            Refuses(() => Contract.Evolve(EvolveNotesV1, e => e.AddState("reader", "notes", Int())), "notes v2.reader.state.notes: already declared");
        }),
        Case("contracts.evolve.edits-remove-state-removes-a-state-member", () =>
        {
            var v2 = Contract.Evolve(EvolveNotesV1, e => e.RemoveState("editor", "count"));
            Sequence(["notes"], EvolveCurrent(v2, "editor").State, "editor's state");
            Sequence(["add", "clear"], EvolveCurrent(v2, "editor").Methods, "editor's methods");
        }),
        Case("contracts.evolve.edits-args-and-result-change-one-side-of-a-method-in-every-variant-that-has-it", () =>
        {
            var v2 = Contract.Evolve(EvolveNotesV1, e => e.Args("add", Obj(("text", Str()), ("tag", Str()))).Result("add", Int()));
            var data = EvolveCurrent(v2, "editor").Data;
            var editor = data.Versions[1].Variants["editor"].Methods["add"];
            var reader = data.Versions[1].Variants["reader"].Methods["add"];
            Equal(EvolveMethodText(editor), EvolveMethodText(reader), "editor's add equals reader's");
            Sequence(["editor,add,args", "reader,add,args", "editor,add,result", "reader,add,result"], EvolveChanges(data.Versions[1]), "changes");
            Equal("""{"additionalProperties":false,"properties":{"tag":{"type":"string"},"text":{"type":"string"}},"required":["tag","text"],"type":"object"}""",
                ChordJson.Stringify(reader.Args.Normal), "reader's add arguments");
            Equal("""{"type":"integer"}""", ChordJson.Stringify(reader.Result.Normal), "reader's add result");
        }),
        Case("contracts.evolve.edits-method-replaces-both-sides", () =>
        {
            var v2 = Contract.Evolve(EvolveNotesV1, e => e.Method("clear", Obj(("all", Bool())), Bool()));
            var clear = ContractsInternal.DataOf(v2).Versions[^1].Variants["editor"].Methods["clear"];
            Equal("""{"additionalProperties":false,"properties":{"all":{"type":"boolean"}},"required":["all"],"type":"object"}""", ChordJson.Stringify(clear.Args.Normal), "clear's arguments");
            Equal("""{"type":"boolean"}""", ChordJson.Stringify(clear.Result.Normal), "clear's result");
            Refuses(() => Contract.Evolve(EvolveNotesV1, e => e.Method("missing", Nul(), Nul())), "notes v2: no method \"missing\"");
        }),
        Case("contracts.evolve.edits-add-method-adds-a-method-to-another-variant-only-with-the-same-signature", () =>
        {
            var v2 = Contract.Evolve(EvolveNotesV1, e => e.AddMethod("reader", "clear", Nul(), Nul()));
            Sequence(["add", "clear"], EvolveCurrent(v2, "reader").Methods, "reader's methods");
            Refuses(() => Contract.Evolve(EvolveNotesV1, e => e.AddMethod("reader", "clear", Str(), Nul())),
                "notes v2.reader.methods.clear: a method has the same signature in every variant");
            Refuses(() => Contract.Evolve(EvolveNotesV1, e => e.AddMethod("reader", "add", Nul(), Nul())), "notes v2.reader.methods.add: already declared");
        }),
        Case("contracts.evolve.edits-remove-method-removes-a-method-from-every-variant", () =>
        {
            var v2 = Contract.Evolve(EvolveNotesV1, e => e.RemoveMethod("add"));
            Sequence(["clear"], EvolveCurrent(v2, "editor").Methods, "editor's methods");
            Sequence([], EvolveCurrent(v2, "reader").Methods, "reader's methods");
            Sequence(["notes"], EvolveCurrent(v2, "reader").State, "reader keeps its state");
        }),
        Case("contracts.evolve.edits-local-edits-take-no-variant", () =>
        {
            var v2 = Contract.Evolve(EvolveInfoV1, e => e.State("title", Int()).AddState("body", Str()).AddMethod("save", Obj(("force", Bool())), Nul()).RemoveMethod("clear"));
            var (state, methods, _) = EvolveCurrent(v2, ContractsInternal.Local);
            Sequence(["title", "body"], state, "state");
            Sequence(["save"], methods, "methods");
            var v3 = Contract.Evolve(v2, e => e.RemoveState("body").Args("save", Nul()));
            // Arguments changed to `null`: the method omits them now (upstream checks the implementation's type; here the run-time rule).
            Check(ContractsInternal.TakesNoArgs(ContractsInternal.CurrentVariants(v3)[ContractsInternal.Local].Methods["save"]), "save takes no arguments");
            Check(!ContractsInternal.TakesNoArgs(ContractsInternal.CurrentVariants(v2)[ContractsInternal.Local].Methods["save"]), "v2's save takes arguments");
        }),
        Case("contracts.evolve.edits-checks-every-edit-closed-arguments-and-the-vocabulary", () =>
        {
            Refuses(() => Contract.Evolve(EvolveNotesV1, e => e.Args("add", new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["text"] = Str() } })),
                "notes v2.editor.methods.add.args: argument objects must be closed");
            Refuses(() => Contract.Evolve(EvolveInfoV1, e => e.AddMethod("m", new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }, Nul())),
                "info v2.members.m.args: argument objects must be closed");
            Refuses(() => Contract.Evolve(EvolveNotesV1, e => e.State("editor", "count", new JsonObject { ["not"] = new JsonObject() })),
                "notes v2.editor.state.count: unsupported keyword \"not\"");
            Accepts(() => Contract.Evolve(EvolveNotesV1, e => e.State("editor", "count", new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() })),
                "an open state object");
        }),
        // describe("keyed edits (slice 7b)")
        Case("contracts.evolve.keyed-edits-change-add-and-remove-a-local-contracts-keyed-members-each-version-fingerprinted", () =>
        {
            var store = EvolveStore();
            var v2 = Contract.Evolve(store, e => e.Keyed("notes", Obj(("text", Str()), ("tag", Str()))).AddKeyed("drafts", EvolveNote));
            var v3 = Contract.Evolve(v2, e => e.RemoveKeyed("drafts"));
            var versions = ContractsInternal.DataOf(v3).Versions;
            Sequence(["notes", "notes,drafts", "notes"], versions.Select(version => string.Join(",", version.Variants[ContractsInternal.Local].Keyed.Keys)), "keyed members per version");
            Sequence(["members,notes,keyed", "members,drafts,keyed"], EvolveChanges(versions[1]), "v2's changes");
            Equal(3, versions.Select(version => version.Fingerprint).Distinct().Count(), "distinct fingerprints");
            // The same edits elsewhere give the same fingerprint (an old plugin's copy).
            var again = Contract.Evolve(store, e => e.Keyed("notes", Obj(("text", Str()), ("tag", Str()))).AddKeyed("drafts", EvolveNote));
            Equal(versions[1].Fingerprint, ContractsInternal.DataOf(again).Versions[1].Fingerprint, "the same edits' fingerprint");
        }),
        Case("contracts.evolve.keyed-edits-refuse-an-unknown-keyed-member-one-declared-already-and-a-kind-change", () =>
        {
            var store = EvolveStore();
            Refuses(() => Contract.Evolve(store, e => e.Keyed("missing", EvolveNote)), "no such keyed member");
            Refuses(() => Contract.Evolve(store, e => e.AddKeyed("n", EvolveNote)), "already declared");
            Refuses(() => Contract.Evolve(store, e => e.RemoveKeyed("missing")), "no such keyed member");
        }),
        // describe("kinds and fixed parts")
        Case("contracts.evolve.kinds-a-members-kind-never-changes-also-not-after-a-removal-and-re-add", () =>
        {
            Refuses(() => Contract.Evolve(EvolveNotesV1, e => e.RemoveState("editor", "count").AddMethod("editor", "count", Nul(), Nul())),
                "notes v2.editor.methods.count: \"count\" is a state member; its kind never changes");
            var v2 = Contract.Evolve(EvolveNotesV1, e => e.RemoveMethod("clear"));
            var v3 = Contract.Evolve(v2, e => e.State("editor", "count", Str()));
            Refuses(() => Contract.Evolve(v3, e => e.AddState("reader", "clear", Str())), "notes v4.reader.state.clear: \"clear\" is a method member; its kind never changes");
        }),
        Case("contracts.evolve.kinds-the-variant-set-never-changes", () =>
            Refuses(() => Contract.Evolve(EvolveNotesV1, e => e.AddState("admin", "x", Str())), "notes v2.admin: unknown variant (the variant set never changes)")),
        Case("contracts.evolve.kinds-a-new-base-is-a-full-declaration-with-an-explicit-version-and-no-older-versions", () =>
        {
            var @base = Contract.Remote("notes", new()
            {
                Version = 5,
                Params = new() { ["session"] = Str(), ["tenant"] = Str() },
                HostAddress = _ => EvolveServer.Address(),
                Variants = new() { ["editor"] = new() },
            });
            Sequence([5L], ContractsInternal.DataOf(@base).Versions.Select(version => version.Version), "versions");
            Equal(6L, Contract.Evolve(@base, e => e).Version, "the next version");
        }),
        Case("contracts.evolve.kinds-evolve-must-return-the-editor-it-was-given", () =>
        {
            var other = Contract.Evolve(EvolveNotesV1, e => e);
            Equal(2L, other.Version, "version");
            ContractEditor? foreign = null;
            Contract.Evolve(EvolveInfoV1, e =>
            {
                foreign = e;
                return e;
            });
            Refuses(() => Contract.Evolve(EvolveNotesV1, _ => foreign!), "notes v2: evolve must return the editor it was given");
            Refuses(() => Contract.Evolve(EvolveNotesV1, _ => null!), "notes v2: evolve must return the editor it was given");
        }),
        Case("contracts.evolve.kinds-leaves-the-previous-contract-untouched", () =>
        {
            Contract.Evolve(EvolveNotesV1, e => e.RemoveState("editor", "count").RemoveMethod("add"));
            var (state, methods, data) = EvolveCurrent(EvolveNotesV1, "editor");
            Sequence(["notes", "count"], state, "state");
            Sequence(["add", "clear"], methods, "methods");
            Equal(1, data.Versions.Count, "versions");
        }),
        // describe("long histories")
        Case("contracts.evolve.long-histories-count-versions-beyond-9-and-99", () =>
        {
            var contract = EvolveInfoV1;
            for (var step = 0; step < 120; step++) contract = Contract.Evolve(contract, e => e);
            Equal(121L, contract.Version, "version");
            Equal(121, ContractsInternal.DataOf(contract).Versions.Count, "versions");
            var v9 = Contract.Local("i", new() { Version = 9, Params = new(), Members = new() { ["s"] = Str() } });
            var v10 = Contract.Evolve(v9, e => e);
            var v99 = Contract.Local("i", new() { Version = 99, Params = new(), Members = new() { ["s"] = Str() } });
            var v100 = Contract.Evolve(v99, e => e);
            var v101 = Contract.Evolve(v100, e => e.State("s", Int()));
            // Upstream checks the literal version types ("10", "100", "101"); C# versions are numbers.
            Sequence([10L, 100L, 101L], new[] { v10, v100, v101 }.Select(version => version.Version), "versions");
        }),
        Case("contracts.evolve.long-histories-track-every-step-with-its-changes", () =>
        {
            var v2 = Contract.Evolve(EvolveNotesV1, e => e.State("editor", "count", Str()).AddMethod("reader", "clear", Nul(), Nul()));
            // Upstream checks the type-level history; here the run-time one.
            var history = ContractsInternal.DataOf(v2).Versions;
            Equal(2, history.Count, "steps");
            Sequence(["editor,count,state", "reader,clear,args", "reader,clear,result"], EvolveChanges(history[1]), "the second step's changes");
            Equal("""{"type":"integer"}""", ChordJson.Stringify(history[0].Variants["editor"].State["count"].Normal), "the first step's count");
        }),
        // PiSharp: one editor serves both kinds, so the other kind's edits are refused when they run (upstream's types rule them out).
        Case("contracts.evolve.pisharp-refuses-the-other-kinds-edits-when-they-run", () =>
        {
            Refuses(() => Contract.Evolve(EvolveInfoV1, e => e.State("members", "title", Int())), "info v2: a local contract's edits name no variant");
            Refuses(() => Contract.Evolve(EvolveInfoV1, e => e.AddMethod("members", "save", Nul(), Nul())), "info v2: a local contract's edits name no variant");
            Refuses(() => Contract.Evolve(EvolveNotesV1, e => e.State("count", Str())), "notes v2: a remote contract's edits name a variant");
            Refuses(() => Contract.Evolve(EvolveNotesV1, e => e.RemoveState("count")), "notes v2: a remote contract's edits name a variant");
            Refuses(() => Contract.Evolve(EvolveNotesV1, e => e.AddKeyed("drafts", EvolveNote)), "notes v2: keyed members are only in local contracts");
            Refuses(() => Contract.Evolve(EvolveNotesV1, e => e.AddState("editor", "draft", null)), "notes v2.editor.state.draft: expected a schema");
        }),
        Case("contracts.evolve.pisharp-an-editor-kept-past-its-call-cannot-change-the-version-it-produced", () =>
        {
            ContractEditor? kept = null;
            var v2 = Contract.Evolve(EvolveNotesV1, e => kept = e);
            kept!.RemoveState("editor", "count").AddState("editor", "extra", Str());
            Sequence(["notes", "count"], EvolveCurrent(v2, "editor").State, "v2's editor state");
            Equal(v2.Fingerprint, ContractsInternal.DataOf(v2).Versions[^1].Fingerprint, "v2's fingerprint");
        }),
    ];

    private static Contract EvolveStore() => Contract.Local("store", new()
    {
        Version = 1,
        Params = new(),
        Members = new() { ["notes"] = Keyed(EvolveNote), ["n"] = Int() },
    });
}
