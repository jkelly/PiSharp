using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/contracts/accepts.test.ts. Every run-time assertion a C# table
// can carry is ported with upstream's message. The @ts-expect-error lines are type-level: where they also call checkAccepts, the call and
// its refusal are ported; the three that only check a mapping's value type (a state mapping returning the current shape, args returning
// the old shape, call returning the old result type) are not, as C# mappings take and return untyped values (Accepts.cs). Upstream's
// untyped door (checkUntyped) reaches refusals a C# table cannot carry, which are not ported: a method side that is no function
// (`args: 1`), an unknown key in a version's mappings or a method mapping (`extra`), and a version key that is no number (`other`; a C#
// table's keys are versions). Its other untyped values are null here, which upstream refuses the same way: a state mapping that is
// neither a function nor "refuse" (`count: 1`), a function where a keyed member needs "refuse", and a null table. A PiSharp case adds the
// refusals of null entries and of a remote variant given a mapping, with upstream's messages, and the order Object.entries reads.
// Mappings are never called (the counted case checks it), so their bodies do not matter.
internal static partial class Program
{
    private static readonly HostKind AcceptsServer = new("server");

    private static readonly JsonObject AcceptsNote = Obj(("text", Str()));

    private static readonly JsonObject AcceptsNoteV2 = Obj(("text", Str()), ("tag", Str()));

    private static readonly Contract AcceptsNotesV1 = Contract.Remote("notes", new()
    {
        Version = 1,
        Params = new() { ["session"] = Str() },
        HostAddress = _ => AcceptsServer.Address(),
        Variants = new()
        {
            ["editor"] = new()
            {
                State = { ["notes"] = Arr(AcceptsNote), ["count"] = Int() },
                Methods = { ["add"] = Method(AcceptsNote, Str()), ["clear"] = Method() },
            },
            ["reader"] = new() { State = { ["notes"] = Arr(AcceptsNote) }, Methods = { ["add"] = Method(AcceptsNote, Str()) } },
        },
    });

    /// <summary>Upstream's <c>toV1</c> (a note of the current version to one of v1), and a mapping that keeps its value.</summary>
    private static readonly Func<object?, object?> AcceptsToV1 = note => note, AcceptsSame = value => value;

    /// <summary>A replacement call.</summary>
    private static readonly Func<object?, object, Task<object?>> AcceptsCall = (args, _) => Task.FromResult(args);

    /// <summary>checkAccepts.</summary>
    private static AcceptedVersions AcceptsCheck(Contract contract, AcceptsTable? accepts) => ContractsInternal.CheckAccepts(contract, accepts);

    /// <summary>The versions checkAccepts accepts: <c>1:fingerprint</c>.</summary>
    private static IEnumerable<string> AcceptsVersions(Contract contract, AcceptsTable? accepts) =>
        AcceptsCheck(contract, accepts).Versions.Select(version => $"{version.Version}:{version.Fingerprint}");

    private static Contract AcceptsStateV2() => Contract.Evolve(AcceptsNotesV1, e => e.State("editor", "notes", Arr(AcceptsNoteV2)));

    private static IEnumerable<(string, Func<Task>)> ContractsAcceptsCases() =>
    [
        // describe("state mappings")
        Case("contracts.accepts.state-mappings-a-changed-member-maps-the-current-member-to-the-old-one", () =>
        {
            var v2 = AcceptsStateV2();
            var accepts = new AcceptsTable { [1] = new() { State = { ["editor"] = new StateSection { ["notes"] = AcceptsToV1 } } } };
            Sequence([$"1:{AcceptsNotesV1.Fingerprint}"], AcceptsVersions(v2, accepts), "versions");
            Refuses(() => AcceptsCheck(v2, new() { [1] = new() { State = { ["editor"] = new StateSection() } } }),
                "notes accepts[1].state.editor.notes: missing mapping");
            Refuses(() => AcceptsCheck(v2, new() { [1] = new() { State = { ["editor"] = new StateSection { ["notes"] = StateMapping.Refuse } } } }),
                "notes accepts[1].state.editor.notes: expected a function");
        }),
        Case("contracts.accepts.state-mappings-a-removed-member-maps-the-variants-whole-current-state-or-refuses", () =>
        {
            var v3 = Contract.Evolve(AcceptsStateV2(), e => e.RemoveState("editor", "count"));
            var accepts = new AcceptsTable
            {
                [1] = new() { State = { ["editor"] = new StateSection { ["notes"] = AcceptsToV1, ["count"] = AcceptsSame } } },
                [2] = new() { State = { ["editor"] = new StateSection { ["count"] = StateMapping.Refuse } } },
            };
            Sequence([1L, 2L], AcceptsCheck(v3, accepts).Versions.Select(version => version.Version), "versions");
            // Upstream's `count: 1`, a value that is neither a function nor "refuse": here a null function.
            Refuses(() => AcceptsCheck(v3, new() { [2] = new() { State = { ["editor"] = new StateSection { ["count"] = (Func<object?, object?>?)null } } } }),
                "notes accepts[2].state.editor.count: expected a function or \"refuse\"");
        }),
        Case("contracts.accepts.state-mappings-an-absent-section-is-allowed-only-when-nothing-is-needed-there", () =>
        {
            var v2 = AcceptsStateV2();
            Refuses(() => AcceptsCheck(v2, new() { [1] = new() }), "notes accepts[1].state.editor: missing mapping");
            var v3 = Contract.Evolve(v2, e => e.AddState("reader", "count", Int()));
            Accepts(() => AcceptsCheck(v3, new() { [2] = new() }), "no sections");
            Accepts(() => AcceptsCheck(v3, new() { [2] = new() { State = new(), Methods = new() } }), "empty sections");
        }),
        Case("contracts.accepts.state-mappings-unnecessary-mappings-are-refused", () =>
        {
            var v2 = AcceptsStateV2();
            Refuses(() => AcceptsCheck(v2, new()
            {
                [1] = new() { State = { ["editor"] = new StateSection { ["notes"] = AcceptsToV1 }, ["reader"] = new StateSection { ["notes"] = AcceptsSame } } },
            }), "notes accepts[1].state.reader: unnecessary mapping");
            var v3 = Contract.Evolve(v2, e => e.AddState("reader", "count", Int()));
            Refuses(() => AcceptsCheck(v3, new() { [2] = new() { State = { ["reader"] = new StateSection { ["count"] = AcceptsSame } } } }),
                "notes accepts[2].state.reader: unnecessary mapping");
        }),
        Case("contracts.accepts.state-mappings-are-checked-by-edit-not-by-schema-a-no-op-edit-and-a-reverted-change-still-need-mappings", () =>
        {
            var noop = Contract.Evolve(AcceptsNotesV1, e => e.State("reader", "notes", Arr(AcceptsNote)));
            Refuses(() => AcceptsCheck(noop, new() { [1] = new() }), "notes accepts[1].state.reader: missing mapping");
            Accepts(() => AcceptsCheck(noop, new() { [1] = new() { State = { ["reader"] = new StateSection { ["notes"] = AcceptsSame } } } }), "the no-op edit's mapping");
            var reverted = Contract.Evolve(AcceptsStateV2(), e => e.State("editor", "notes", Arr(AcceptsNote)));
            Accepts(() => AcceptsCheck(reverted, new() { [1] = new() { State = { ["editor"] = new StateSection { ["notes"] = AcceptsSame } } } }), "the reverted change's mapping");
            var readded = Contract.Evolve(AcceptsNotesV1, e => e.RemoveState("editor", "count").AddState("editor", "count", Int()));
            Accepts(() => AcceptsCheck(readded, new() { [1] = new() { State = { ["editor"] = new StateSection { ["count"] = AcceptsSame } } } }), "the re-added member's mapping");
        }),
        // describe("method mappings")
        Case("contracts.accepts.method-mappings-changed-args-map-old-to-current-a-changed-result-maps-current-to-old-both-need-both", () =>
        {
            var args = Contract.Evolve(AcceptsNotesV1, e => e.Args("add", AcceptsNoteV2));
            Accepts(() => AcceptsCheck(args, new() { [1] = new() { Methods = { ["add"] = new MethodMapping { Args = AcceptsSame } } } }), "an args mapping");
            Refuses(() => AcceptsCheck(args, new() { [1] = new() { Methods = { ["add"] = new MethodMapping { Args = AcceptsSame, Result = AcceptsSame } } } }),
                "notes accepts[1].methods.add.result: unnecessary mapping");
            var result = Contract.Evolve(AcceptsNotesV1, e => e.Result("add", Int()));
            Accepts(() => AcceptsCheck(result, new() { [1] = new() { Methods = { ["add"] = new MethodMapping { Result = AcceptsSame } } } }), "a result mapping");
            Refuses(() => AcceptsCheck(result, new() { [1] = new() { Methods = { ["add"] = new MethodMapping() } } }),
                "notes accepts[1].methods.add.result: missing mapping");
            var both = Contract.Evolve(AcceptsNotesV1, e => e.Method("add", AcceptsNoteV2, Int()));
            Accepts(() => AcceptsCheck(both, new() { [1] = new() { Methods = { ["add"] = new MethodMapping { Args = AcceptsSame, Result = AcceptsSame } } } }), "both mappings");
            Refuses(() => AcceptsCheck(both, new() { [1] = new() { Methods = { ["add"] = new MethodMapping { Args = AcceptsSame } } } }),
                "notes accepts[1].methods.add.result: missing mapping");
        }),
        Case("contracts.accepts.method-mappings-a-removed-method-takes-a-call-replacement-or-refuses", () =>
        {
            var v2 = Contract.Evolve(AcceptsNotesV1, e => e.RemoveMethod("clear"));
            Accepts(() => AcceptsCheck(v2, new() { [1] = new() { Methods = { ["clear"] = new MethodMapping { Call = AcceptsCall } } } }), "a replacement");
            Accepts(() => AcceptsCheck(v2, new() { [1] = new() { Methods = { ["clear"] = MethodMapping.Refuse } } }), "a refusal");
            Refuses(() => AcceptsCheck(v2, new() { [1] = new() { Methods = { ["clear"] = new MethodMapping { Args = AcceptsSame } } } }),
                "notes accepts[1].methods.clear: expected { call } or \"refuse\"");
            Refuses(() => AcceptsCheck(v2, new() { [1] = new() }), "notes accepts[1].methods.clear: missing mapping");
        }),
        Case("contracts.accepts.method-mappings-a-method-removed-from-one-variant-that-had-it-counts-as-removed", () =>
        {
            var v2 = Contract.Evolve(AcceptsNotesV1, e => e.RemoveMethod("add").AddMethod("editor", "add", AcceptsNote, Str()));
            Refuses(() => AcceptsCheck(v2, new() { [1] = new() { Methods = { ["add"] = new MethodMapping { Args = AcceptsSame, Result = AcceptsSame } } } }),
                "notes accepts[1].methods.add: expected { call } or \"refuse\"");
            Accepts(() => AcceptsCheck(v2, new() { [1] = new() { Methods = { ["add"] = MethodMapping.Refuse } } }), "a refusal");
        }),
        Case("contracts.accepts.method-mappings-call-and-refuse-are-also-allowed-for-changed-methods", () =>
        {
            var v2 = Contract.Evolve(AcceptsNotesV1, e => e.Args("add", AcceptsNoteV2));
            Accepts(() => AcceptsCheck(v2, new() { [1] = new() { Methods = { ["add"] = new MethodMapping { Call = AcceptsCall } } } }), "a replacement");
            Accepts(() => AcceptsCheck(v2, new() { [1] = new() { Methods = { ["add"] = MethodMapping.Refuse } } }), "a refusal");
            Refuses(() => AcceptsCheck(v2, new() { [1] = new() { Methods = { ["add"] = new MethodMapping { Call = AcceptsCall, Args = AcceptsSame } } } }),
                "notes accepts[1].methods.add: expected { call } or \"refuse\"");
        }),
        Case("contracts.accepts.method-mappings-added-methods-and-methods-added-to-a-new-variant-need-nothing", () =>
        {
            var v2 = Contract.Evolve(AcceptsNotesV1, e => e.AddMethod("reader", "clear", Nul(), Nul()));
            Equal(1, AcceptsCheck(v2, new() { [1] = new() }).Versions.Count, "versions");
            Refuses(() => AcceptsCheck(v2, new() { [1] = new() { Methods = { ["clear"] = MethodMapping.Refuse } } }),
                "notes accepts[1].methods.clear: unnecessary mapping");
        }),
        Case("contracts.accepts.method-mappings-refuses-wrong-shapes", () =>
            // Upstream's `args: 1`, `extra` keys and the `other` version cannot be written in C#; a null table is refused as upstream's is.
            Refuses(() => AcceptsCheck(Contract.Evolve(AcceptsNotesV1, e => e.Args("add", AcceptsNoteV2)), null), "notes accepts: expected an object")),
        // describe("keyed mappings (slice 7b)")
        Case("contracts.accepts.keyed-mappings-need-a-function-or-refuse-for-a-changed-keyed-member-refuse-for-a-removed-one", () =>
        {
            var store = Contract.Local("store", new() { Version = 1, Params = new(), Members = new() { ["notes"] = Keyed(AcceptsNote), ["drafts"] = Keyed(AcceptsNote) } });
            var v2 = Contract.Evolve(store, e => e.Keyed("notes", AcceptsNoteV2).RemoveKeyed("drafts"));
            Equal(1, AcceptsCheck(v2, new() { [1] = new() { Keyed = { ["notes"] = AcceptsToV1, ["drafts"] = StateMapping.Refuse } } }).Versions.Count, "a function and a refusal");
            Equal(1, AcceptsCheck(v2, new() { [1] = new() { Keyed = { ["notes"] = StateMapping.Refuse, ["drafts"] = StateMapping.Refuse } } }).Versions.Count, "two refusals");
            Refuses(() => AcceptsCheck(v2, new() { [1] = new() { Keyed = { ["notes"] = AcceptsToV1 } } }), "accepts[1].keyed.drafts: missing mapping");
            Refuses(() => AcceptsCheck(v2, new() { [1] = new() { Keyed = { ["notes"] = AcceptsToV1, ["drafts"] = AcceptsToV1 } } }), "accepts[1].keyed.drafts: expected \"refuse\"");
            Accepts(() => AcceptsCheck(store, new()), "an empty table of a one-version contract");
        }),
        // describe("versions")
        Case("contracts.accepts.versions-refuses-unknown-versions-and-the-current-version", () =>
        {
            var v2 = Contract.Evolve(AcceptsNotesV1, e => e);
            Refuses(() => AcceptsCheck(v2, new() { [3] = new() }), "notes accepts[3]: not an older version");
            Refuses(() => AcceptsCheck(v2, new() { [2] = new() }), "notes accepts[2]: not an older version");
            Sequence([], AcceptsVersions(v2, new()), "versions of an empty table");
        }),
        Case("contracts.accepts.versions-maps-directly-over-several-versions-every-edit-after-the-accepted-version-counts", () =>
        {
            var v2 = Contract.Evolve(AcceptsNotesV1, e => e.Args("add", AcceptsNoteV2));
            var v3 = Contract.Evolve(v2, e => e.Result("add", Int()).RemoveState("editor", "count"));
            var accepts = new AcceptsTable
            {
                [1] = new()
                {
                    State = { ["editor"] = new StateSection { ["count"] = StateMapping.Refuse } },
                    Methods = { ["add"] = new MethodMapping { Args = AcceptsSame, Result = AcceptsSame } },
                },
                [2] = new()
                {
                    State = { ["editor"] = new StateSection { ["count"] = AcceptsSame } },
                    Methods = { ["add"] = new MethodMapping { Result = AcceptsSame } },
                },
            };
            Sequence([$"1:{AcceptsNotesV1.Fingerprint}", $"2:{v2.Fingerprint}"], AcceptsVersions(v3, accepts), "versions");
            var fromV1 = new VersionMappings
            {
                State = { ["editor"] = new StateSection { ["count"] = StateMapping.Refuse } },
                Methods = { ["add"] = new MethodMapping { Result = AcceptsSame } },
            };
            Refuses(() => AcceptsCheck(v3, new() { [1] = fromV1 }), "notes accepts[1].methods.add.args: missing mapping");
        }),
        Case("contracts.accepts.versions-never-calls-a-mapping", () =>
        {
            var v2 = Contract.Evolve(AcceptsNotesV1, e => e.Args("add", AcceptsNoteV2).RemoveMethod("clear").State("editor", "count", Str()));
            int args = 0, call = 0, count = 0;
            AcceptsCheck(v2, new()
            {
                [1] = new()
                {
                    State = { ["editor"] = new StateSection { ["count"] = (Func<object?, object?>)(value => { count++; return value; }) } },
                    Methods =
                    {
                        ["add"] = new MethodMapping { Args = value => { args++; return value; } },
                        ["clear"] = new MethodMapping { Call = (value, _) => { call++; return Task.FromResult(value); } },
                    },
                },
            });
            Sequence([0, 0, 0], [args, call, count], "calls");
        }),
        Case("contracts.accepts.versions-local-tables-are-flat-state-without-a-variant-level", () =>
        {
            var v1 = Contract.Local("info", new() { Version = 1, Params = new(), Members = new() { ["title"] = Str(), ["save"] = Method(Str()) } });
            var v2 = Contract.Evolve(v1, e => e.State("title", Int()).Args("save", Int()));
            Accepts(() => AcceptsCheck(v2, new() { [1] = new() { State = { ["title"] = AcceptsSame }, Methods = { ["save"] = new MethodMapping { Args = AcceptsSame } } } }),
                "a flat table");
            Refuses(() => AcceptsCheck(v2, new()
            {
                [1] = new() { State = { ["members"] = new StateSection { ["title"] = AcceptsSame } }, Methods = { ["save"] = MethodMapping.Refuse } },
            }), "info accepts[1].state.members: unnecessary mapping");
        }),
        // PiSharp: the untyped values a C# table can carry (null entries, a mapping where a variant's section belongs) are refused as upstream
        // refuses the same JavaScript values, and the table is read in Object.entries order.
        Case("contracts.accepts.pisharp-refuses-null-entries-and-a-mapping-in-place-of-a-variant-section", () =>
        {
            var v2 = AcceptsStateV2();
            Refuses(() => AcceptsCheck(v2, new() { [1] = null }), "notes accepts[1]: expected an object");
            Refuses(() => AcceptsCheck(v2, new() { [1] = new() { State = { ["editor"] = AcceptsSame } } }), "notes accepts[1].state.editor: expected an object");
            Refuses(() => AcceptsCheck(v2, new() { [1] = new() { State = { ["editor"] = StateMapping.Refuse } } }), "notes accepts[1].state.editor: expected an object");
            // A null section is an absent one (upstream's undefined): it maps nothing.
            Refuses(() => AcceptsCheck(v2, new() { [1] = new() { State = { ["editor"] = (StateSection?)null } } }), "notes accepts[1].state.editor.notes: missing mapping");
            var removed = Contract.Evolve(AcceptsNotesV1, e => e.RemoveMethod("clear"));
            Refuses(() => AcceptsCheck(removed, new() { [1] = new() { Methods = { ["clear"] = null } } }), "notes accepts[1].methods.clear: expected an object");
            var local = Contract.Evolve(Contract.Local("info", new() { Version = 1, Params = new(), Members = new() { ["title"] = Str() } }), e => e.State("title", Int()));
            Refuses(() => AcceptsCheck(local, new() { [1] = new() { State = { ["title"] = new StateSection() } } }), "info accepts[1].state.title: expected a function");
            Refuses(() => AcceptsCheck(local, new() { [1] = new() { State = { ["title"] = default(StateEntry) } } }), "info accepts[1].state.title: expected a function");
        }),
        Case("contracts.accepts.pisharp-reads-versions-in-object-entries-order", () =>
        {
            var v2 = Contract.Evolve(AcceptsNotesV1, e => e);
            var v3 = Contract.Evolve(v2, e => e);
            Sequence([1L, 2L], AcceptsCheck(v3, new() { [2] = new(), [1] = new() }).Versions.Select(version => version.Version), "versions");
        }),
    ];
}
