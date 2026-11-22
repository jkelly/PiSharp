using System.Reflection;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using static ChordSupport;
using static PiSharp.Chord.Contracts.Schemas;

// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): the cases 1.6.0 added to packages/chord/test/contracts/{accepts,contract,declare,
// evolve,fingerprint,schema}.test.ts: event members (decision "Events: fire-and-forget members"), `queries` members (slice 14 §3.1), a
// remote contract's admission (slice 14 §2.1) and memberNames. Case ids keep the layer and file of the upstream test (contracts.accepts.*).
// Adaptations, as in the 1.5.0 files beside this one: an untyped value that is neither a function nor "refuse" (`paddle: 1`) is
// default(StateMapping); expectTypeOf lines are type-level and have no C# counterpart (AdmittedOf, EventMember<T>, NamedQuery<T, A>,
// Implementation), so "type the implementation as one named query per listed name", which asserts nothing else, is not ported; and
// `doc: unknown key "admission"` for a local declaration cannot be written (LocalDeclaration has no such property). schema.test.ts's new
// case compiles a contract with tsc and checks that its emitted declaration names every member type through Chord's entry point; its
// native counterpart is authored (contracts.schema.160-*): the same two contracts declared through public types only, with every type a
// declaration or an implementation names checked to be public. Four more cases are authored for what only the native port has: the
// run-time checks standing in for upstream's types on admission(schema), the other kind's edit forms for events, and the link order of
// event members (variantNames).
internal static partial class Program
{
    private static readonly JsonObject Sync160Sample = Obj(("y", Int()));

    private static readonly JsonObject Sync160Timed = Obj(("y", Int()), ("t", Int()));

    private static readonly JsonObject Sync160Paddle = Obj(("peer", Str()), ("y", Int()));

    private static readonly JsonObject Sync160Note = Obj(("text", Str()));

    private static readonly Func<object?, object?> Sync160Same = value => value;

    private static readonly Contract Sync160MatchV1 = Contract.Remote("match", new()
    {
        Version = 1,
        Params = new(),
        HostAddress = _ => Server.Address(),
        Variants = new()
        {
            ["player"] = new() { Events = { ["paddle"] = Sync160Sample, ["ball"] = Sync160Sample } },
            ["watcher"] = new() { Events = { ["paddle"] = Sync160Sample } },
        },
    });

    /// <summary>The evolve tests' Match: one variant with an event member, one with state.</summary>
    private static readonly Contract Sync160EvolveMatch = Contract.Remote("match", new()
    {
        Version = 1,
        Params = new(),
        HostAddress = _ => Server.Address(),
        Variants = new()
        {
            ["player"] = new() { Events = { ["paddle"] = Sync160Sample } },
            ["watcher"] = new() { State = { ["score"] = Int() } },
        },
    });

    private static OrderedDictionary<string, VariantDeclaration> Sync160OneVariant() => new() { ["a"] = new() { State = { ["n"] = Int() } } };

    private static Contract Sync160Doc(JsonNode? admission = null) => Contract.Remote("doc", new()
    {
        Version = 1,
        Params = new(),
        HostAddress = _ => Server.Address(),
        Admission = admission,
        Variants = Sync160OneVariant(),
    });

    private static Contract Sync160Remote(string name, OrderedDictionary<string, VariantDeclaration> variants) =>
        Contract.Remote(name, new() { Version = 1, Params = new(), HostAddress = _ => Server.Address(), Variants = variants });

    private static Contract Sync160Local(string name, OrderedDictionary<string, Member> members) =>
        Contract.Local(name, new() { Version = 1, Params = new(), Members = members });

    private static int Sync160Accepted(Contract contract, AcceptsTable accepts) => ContractsInternal.CheckAccepts(contract, accepts).Versions.Count;

    private static IEnumerable<string> Sync160EventNames(VersionData version, string variant) => version.Variants[variant].Events.Keys;

    private static string Sync160Names(MemberNames names) =>
        $"state:[{string.Join(",", names.State)}] methods:[{string.Join(",", names.Methods)}] events:[{string.Join(",", names.Events)}]";

    private static IEnumerable<(string, Func<Task>)> Sync160ContractsCases() =>
    [
        // accepts.test.ts: event mappings
        Case("contracts.accepts.event-mappings-need-a-function-or-refuse-for-a-changed-event-member-by-variant", () =>
        {
            var v2 = Contract.Evolve(Sync160MatchV1, e => e.Event("player", "paddle", Sync160Timed));
            Equal(1, Sync160Accepted(v2, new() { [1] = new() { Events = { ["player"] = new StateSection { ["paddle"] = Sync160Same } } } }), "a function");
            Equal(1, Sync160Accepted(v2, new() { [1] = new() { Events = { ["player"] = new StateSection { ["paddle"] = StateMapping.Refuse } } } }), "refuse");
            // The changed member needs a mapping.
            Refuses(() => Sync160Accepted(v2, new() { [1] = new() }), "match accepts[1].events.player: missing mapping");
            Refuses(() => Sync160Accepted(v2, new() { [1] = new() { Events = { ["player"] = new StateSection { ["paddle"] = default(StateMapping) } } } }),
                "match accepts[1].events.player.paddle: expected a function or \"refuse\"");
            // One variant's change needs nothing for another (its refusal breaks only that variant's binding).
            Refuses(() => Sync160Accepted(v2, new()
            {
                [1] = new() { Events = { ["player"] = new StateSection { ["paddle"] = StateMapping.Refuse }, ["watcher"] = new StateSection() } },
            }), "match accepts[1].events.watcher: unnecessary mapping");
        }),
        Case("contracts.accepts.event-mappings-need-nothing-for-a-removed-or-an-added-event-member-a-re-added-one-counts-as-changed", () =>
        {
            var removed = Contract.Evolve(Sync160MatchV1, e => e.RemoveEvent("player", "ball").AddEvent("watcher", "ball", Sync160Sample));
            Equal(0, Sync160Accepted(removed, new()), "an empty table");
            Equal(1, Sync160Accepted(removed, new() { [1] = new() }), "nothing is needed");
            Refuses(() => Sync160Accepted(removed, new() { [1] = new() { Events = { ["player"] = new StateSection { ["ball"] = Sync160Same } } } }),
                "match accepts[1].events.player: unnecessary mapping");
            var readded = Contract.Evolve(removed, e => e.AddEvent("player", "ball", Sync160Timed));
            Refuses(() => Sync160Accepted(readded, new() { [1] = new() }), "match accepts[1].events.player: missing mapping");
            Equal(1, Sync160Accepted(readded, new() { [1] = new() { Events = { ["player"] = new StateSection { ["ball"] = Sync160Same } } } }), "the re-added member's mapping");
        }),
        Case("contracts.accepts.event-mappings-need-an-identity-mapping-for-a-no-op-edit-by-edit-never-by-schema", () =>
        {
            var v2 = Contract.Evolve(Sync160MatchV1, e => e.Event("watcher", "paddle", Sync160Sample));
            Refuses(() => Sync160Accepted(v2, new() { [1] = new() }), "match accepts[1].events.watcher: missing mapping");
            Equal(1, Sync160Accepted(v2, new() { [1] = new() { Events = { ["watcher"] = new StateSection { ["paddle"] = Sync160Same } } } }), "the identity mapping");
        }),
        Case("contracts.accepts.event-mappings-are-flat-in-a-local-contract", () =>
        {
            var v1 = Sync160Local("store", new() { ["paddle"] = Event(Sync160Sample) });
            var v2 = Contract.Evolve(v1, e => e.Event("paddle", Sync160Timed));
            Equal(1, Sync160Accepted(v2, new() { [1] = new() { Events = { ["paddle"] = Sync160Same } } }), "a flat mapping");
            Equal(1, Sync160Accepted(v2, new() { [1] = new() { Events = { ["paddle"] = StateMapping.Refuse } } }), "a flat refusal");
            Refuses(() => Sync160Accepted(v2, new() { [1] = new() { Events = { ["members"] = new StateSection { ["paddle"] = Sync160Same } } } }),
                "store accepts[1].events.members: unnecessary mapping");
        }),

        // contract.test.ts: member names
        Case("contracts.contract.member-names-lists-a-remote-variants-state-members-methods-and-event-members-of-the-current-version", () =>
        {
            Equal("state:[notes] methods:[add,clear] events:[]", Sync160Names(ContractNotes.MemberNames("editor")), "the editor");
            Equal("state:[notes] methods:[] events:[]", Sync160Names(ContractNotes.MemberNames("reader")), "the reader");
            var next = Contract.Evolve(ContractNotes, e => e.AddState("editor", "count", Int()).RemoveMethod("clear"));
            Equal("state:[notes,count] methods:[add] events:[]", Sync160Names(next.MemberNames("editor")), "the next version's editor");
            Equal("state:[] methods:[] events:[paddle,ball]", Sync160Names(Sync160MatchV1.MemberNames("player")), "event members, in the order declared");
        }),
        Case("contracts.contract.member-names-lists-a-local-contracts-without-its-keyed-and-queries-members", () =>
        {
            var store = Sync160Local("names.store", new()
            {
                ["count"] = Int(),
                ["notes"] = Keyed(Str()),
                ["rows"] = Queries(Obj(("id", Str())), ("one", Obj(("id", Str())))),
                ["ticked"] = Event(Int()),
                ["add"] = Method(Int()),
            });
            Equal("state:[count] methods:[add] events:[ticked]", Sync160Names(store.MemberNames()), "the local contract's names");
        }),
        Case("contracts.contract.member-names-refuses-a-variant-the-contract-does-not-have-and-a-copy-of-a-contract", () =>
        {
            Refuses(() => ContractNotes.MemberNames("owner"), "notes: no variant \"owner\"");
            Refuses(() => ContractNotes.MemberNames(), "notes: no variant \"members\"");
            var copy = (Contract)typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(ContractNotes, null)!;
            Refuses(() => copy.MemberNames("editor"), "not a declared contract");
        }),

        // declare.test.ts: admission (slice 14 §2.1)
        Case("contracts.declare.admission-is-declared-with-its-schema-and-never-part-of-the-fingerprint", () =>
        {
            var plain = Sync160Doc();
            var admitted = Sync160Doc(Obj(("mode", Str())));
            Equal(plain.Fingerprint, admitted.Fingerprint, "the fingerprint");
            var other = Sync160Doc(Obj(("mode", Int())));
            // Its own fingerprint, which a reload's staging compares.
            Check(ContractsInternal.DataOf(admitted).Admission!.Fingerprint != ContractsInternal.DataOf(other).Admission!.Fingerprint, "the admission's own fingerprint");
            Equal(ContractsInternal.DataOf(admitted).Admission!.Fingerprint, ContractsInternal.DataOf(Sync160Doc(Obj(("mode", Str())))).Admission!.Fingerprint,
                "equal schemas, equal admission fingerprints");
            Check(ContractsInternal.DataOf(plain).Admission is null, "a contract without one has none");
            Json("""{"type":"object","properties":{"mode":{"type":"string"}},"required":["mode"],"additionalProperties":false}""",
                ContractsInternal.DataOf(admitted).Admission!.Schema.Schema, "the schema is kept as declared");
        }),
        Case("contracts.declare.admission-is-json-only-and-belongs-to-remote-contracts", () =>
        {
            Refuses(() => Sync160Doc(Obj(("data", Bytes()))), "doc.admission: an admission's value is JSON only (no bytes())");
            // `doc: unknown key "admission"` for a local declaration: LocalDeclaration has no such property, and a local contract has none.
            Check(typeof(LocalDeclaration).GetProperty("Admission") is null, "a local declaration has no admission");
            Check(ContractsInternal.DataOf(Sync160Local("doc", new())).Admission is null, "a local contract has no admission");
        }),
        Case("contracts.declare.admission-is-replaced-by-evolves-admission-the-last-edit-of-a-version-the-wire-fingerprint-stays-an-edits", () =>
        {
            var v1 = Sync160Doc(Obj(("mode", Str())));
            var v2 = Contract.Evolve(v1, e => e.AddState("a", "m", Int()));
            var v3 = Contract.Evolve(v2, e => e.Admission(Obj(("mode", Str()), ("since", Int()))));
            // Carried over unchanged by an edit that is not its own.
            Check(ReferenceEquals(ContractsInternal.DataOf(v2).Admission, ContractsInternal.DataOf(v1).Admission), "carried over");
            Check(ContractsInternal.DataOf(v3).Admission!.Fingerprint != ContractsInternal.DataOf(v1).Admission!.Fingerprint, "replaced");
            // A version whose only edit is its admission has the previous version's members.
            Equal(v2.Fingerprint, v3.Fingerprint, "the wire fingerprint");
            Equal(3L, v3.Version, "it is a version of its own");
            Equal(0, ContractsInternal.DataOf(v3).Versions[^1].Changes.Count, "no member change");
            var gained = Contract.Evolve(Sync160Doc(), e => e.Admission(Obj(("mode", Str()))));
            Check(ContractsInternal.DataOf(gained).Admission is not null, "a contract gains one");
            // An edit before it in the same version keeps both.
            var both = Contract.Evolve(v1, e => e.AddState("a", "m", Int()).Admission(Obj(("role", Str()))));
            Equal(v2.Fingerprint, both.Fingerprint, "the edit's fingerprint");
            Check(ContractsInternal.DataOf(both).Admission!.Fingerprint != ContractsInternal.DataOf(v1).Admission!.Fingerprint, "and the new admission");
        }),
        Case("contracts.declare.160-admission-edits-are-checked-when-they-run", () =>
        {
            var v1 = Sync160Doc(Obj(("mode", Str())));
            // Upstream's admission(schema) returns no editor, so nothing can follow it; here an edit after it is refused.
            Refuses(() => Contract.Evolve(v1, e => e.Admission(Obj(("mode", Str()))).AddState("a", "m", Int())),
                "doc v2: admission(schema) is the last edit of a version");
            Refuses(() => Contract.Evolve(v1, e => e.Admission(Obj(("mode", Str()))).RemoveState("a", "n")), "admission(schema) is the last edit of a version");
            Refuses(() => Contract.Evolve(v1, e => e.Admission(Obj(("mode", Str()))).Admission(Obj(("mode", Int())))), "admission(schema) is the last edit of a version");
            Refuses(() => Contract.Evolve(v1, e => e.Admission(Obj(("data", Bytes())))), "doc v2.admission: an admission's value is JSON only (no bytes())");
            Refuses(() => Contract.Evolve(v1, e => e.Admission(null)), "doc v2.admission: expected a schema");
            // Upstream's LocalEditor has no admission.
            Refuses(() => Contract.Evolve(Sync160Local("doc", new() { ["n"] = Int() }), e => e.Admission(Obj(("mode", Str())))),
                "doc v2: an admission belongs to a remote contract");
        }),

        // declare.test.ts: queries members (slice 14 §3.1)
        Case("contracts.declare.queries-members-declare-named-queries-in-a-local-contract-their-own-kind-each-querys-argument-schema-in-the-fingerprint", () =>
        {
            Contract Store(JsonNode one) => Sync160Local("app.store", new()
            {
                ["notes"] = Queries(Sync160Note, ("ofBoard", Obj(("board", Str()))), ("one", one)),
                ["add"] = Method(),
            });
            var store = Store(Obj(("id", Int())));
            var variant = ContractsInternal.CurrentVariants(store)[ContractsInternal.Local];
            Sequence(["ofBoard", "one"], variant.Queries["notes"].Args.Keys, "the named queries, in the order declared");
            Sequence([], variant.State.Keys, "no state member");
            Equal(MemberKind.Queries, ContractsInternal.DataOf(store).Kinds["notes"], "its own kind");
            var changed = Store(Obj(("id", Str())));
            Check(changed.Fingerprint != store.Fingerprint, "an argument schema is part of the fingerprint");
            Equal(store.Fingerprint, Store(Obj(("id", Int()))).Fingerprint, "the same declaration, the same fingerprint");
            // Evolving the contract carries its queries members over unchanged (their edits are a later slice's).
            var next = Contract.Evolve(store, e => e.AddMethod("clear", Str(), Str()));
            Check(ContractsInternal.CurrentVariants(next)[ContractsInternal.Local].Queries.ContainsKey("notes"), "carried over");
            Refuses(() => Contract.Evolve(store, e => e.AddState("notes", Str())), "already declared");
            // The public declaration: the item and the arguments as they were given.
            var member = Queries(Sync160Note, ("one", Obj(("id", Int()))));
            Sequence(["one"], member.Args.Keys, "the declaration's queries");
            Json("""{"type":"object","properties":{"text":{"type":"string"}},"required":["text"],"additionalProperties":false}""", member.Item, "the declaration's item");
        }),
        Case("contracts.declare.queries-members-refuse-a-queries-member-in-a-remote-contract-and-argument-schemas-that-are-not-closed", () =>
        {
            Refuses(() => Sync160Remote("app.notes", new() { ["editor"] = new() { State = { ["notes"] = Queries(Sync160Note) } } }),
                "app.notes.editor.state.notes: queries members are only in local contracts");
            Refuses(() => Sync160Local("app.store", new() { ["notes"] = Queries(Sync160Note, ("one", JsonValue.Create(5))) }), "app.store.members.notes.args.one");
            // Arguments are closed, as a method's are.
            Refuses(() => Sync160Local("app.store", new() { ["notes"] = Queries(Sync160Note, ("one", new JsonObject { ["type"] = "object" })) }),
                "app.store.members.notes.args.one");
        }),

        // declare.test.ts: event members
        Case("contracts.declare.event-members-declare-events-in-a-remote-variant-and-with-the-local-marker-their-own-kind", () =>
        {
            var match = Sync160Remote("app.match", new()
            {
                ["player"] = new() { State = { ["score"] = Int() }, Events = { ["paddle"] = Sync160Paddle } },
                ["watcher"] = new() { Events = new() },
            });
            var store = Sync160Local("app.store", new() { ["score"] = Int(), ["paddle"] = Event(Sync160Paddle) });
            Sequence(["paddle"], ContractsInternal.CurrentVariants(match)["player"].Events.Keys, "the remote variant's events");
            var variant = ContractsInternal.CurrentVariants(store)[ContractsInternal.Local];
            Sequence(["paddle"], variant.Events.Keys, "the local contract's events");
            Sequence(["score"], variant.State.Keys, "the local contract's state");
            Equal(MemberKind.Event, ContractsInternal.DataOf(store).Kinds["paddle"], "its own kind");
        }),
        Case("contracts.declare.event-members-keep-one-kind-per-name-across-variants-and-the-history", () =>
        {
            Refuses(() => Sync160Remote("app.match", new()
            {
                ["a"] = new() { State = { ["paddle"] = Sync160Paddle } },
                ["b"] = new() { Events = { ["paddle"] = Sync160Paddle } },
            }), "app.match.b.events.paddle: \"paddle\" is a state member; its kind never changes");
            var store = Sync160Local("app.store", new() { ["paddle"] = Event(Sync160Paddle) });
            var without = Contract.Evolve(store, e => e.RemoveEvent("paddle"));
            Refuses(() => Contract.Evolve(without, e => e.AddState("paddle", Sync160Paddle)), "\"paddle\" is an event member; its kind never changes");
        }),
        Case("contracts.declare.event-members-allow-an-events-schema-to-differ-per-variant-redaction-and-refuse-bytes", () =>
        {
            Accepts(() => Sync160Remote("app.match", new()
            {
                ["a"] = new() { Events = { ["paddle"] = Sync160Paddle } },
                ["b"] = new() { Events = { ["paddle"] = Obj(("y", Int())) } },
            }), "an event's schema may differ per variant");
            Refuses(() => Sync160Local("app.store", new() { ["frame"] = Event(Obj(("data", Bytes()))) }),
                "app.store.members.frame: an event's values are JSON only (no bytes())");
            Refuses(() => Sync160Remote("app.match", new() { ["a"] = new() { Events = { ["x"] = Method() } } }),
                "app.match.a.events.x: expected the schema of the event's values");
            // The other markers upstream refuses there, and an event under state.
            Refuses(() => Sync160Remote("app.match", new() { ["a"] = new() { Events = { ["x"] = Event(Sync160Paddle) } } }),
                "app.match.a.events.x: expected the schema of the event's values");
            Refuses(() => Sync160Remote("app.match", new() { ["a"] = new() { Events = { ["x"] = Keyed(Str()) } } }),
                "app.match.a.events.x: expected the schema of the event's values");
            Refuses(() => Sync160Remote("app.match", new() { ["a"] = new() { State = { ["x"] = Event(Sync160Paddle) } } }),
                "app.match.a.state.x: an event belongs in events");
            Refuses(() => Sync160Remote("app.match", new() { ["a"] = new() { Events = { ["x"] = Obj(("data", Bytes())) } } }),
                "app.match.a.events.x: an event's values are JSON only (no bytes())");
        }),

        // evolve.test.ts: event edits
        Case("contracts.evolve.event-edits-change-add-and-remove-an-event-member-of-a-variant-each-a-change-of-its-own-kind", () =>
        {
            var v2 = Contract.Evolve(Sync160EvolveMatch, e => e.Event("player", "paddle", Sync160Timed).AddEvent("watcher", "ball", Sync160Sample));
            var v3 = Contract.Evolve(v2, e => e.RemoveEvent("player", "paddle"));
            var versions = ContractsInternal.DataOf(v3).Versions;
            Sequence(["paddle", "paddle", ""], versions.Select(version => string.Join(",", Sync160EventNames(version, "player"))), "the player's events by version");
            Sequence([new Change("player", "paddle", ChangeSide.Event), new Change("watcher", "ball", ChangeSide.Event)], versions[1].Changes, "the changes of version 2");
            Equal(0, versions[2].Changes.Count, "a removal records no change");
            Equal(3, versions.Select(version => version.Fingerprint).Distinct().Count(), "three fingerprints");
        }),
        Case("contracts.evolve.event-edits-take-no-variant-in-a-local-contract", () =>
        {
            var store = Sync160Local("store", new() { ["paddle"] = Event(Sync160Sample) });
            var v2 = Contract.Evolve(store, e => e.Event("paddle", Sync160Timed).AddEvent("ball", Sync160Sample));
            var v3 = Contract.Evolve(v2, e => e.RemoveEvent("ball"));
            Sequence(["paddle", "paddle,ball", "paddle"],
                ContractsInternal.DataOf(v3).Versions.Select(version => string.Join(",", Sync160EventNames(version, "members"))), "the events by version");
        }),
        Case("contracts.evolve.event-edits-refuse-an-unknown-event-member-one-declared-already-and-bytes", () =>
        {
            Refuses(() => Contract.Evolve(Sync160EvolveMatch, e => e.Event("player", "missing", Sync160Sample)), "no such event member");
            Refuses(() => Contract.Evolve(Sync160EvolveMatch, e => e.AddEvent("watcher", "score", Sync160Sample)), "already declared");
            Refuses(() => Contract.Evolve(Sync160EvolveMatch, e => e.RemoveEvent("player", "missing")), "no such event member");
            Refuses(() => Contract.Evolve(Sync160EvolveMatch, e => e.AddEvent("player", "frame", Obj(("data", Bytes())))), "an event's values are JSON only");
            // The paths name the events section.
            Refuses(() => Contract.Evolve(Sync160EvolveMatch, e => e.Event("player", "missing", Sync160Sample)), "match v2.player.events.missing: no such event member");
        }),
        Case("contracts.evolve.160-event-edits-of-the-other-kinds-form-are-refused-when-they-run", () =>
        {
            var store = Sync160Local("store", new() { ["paddle"] = Event(Sync160Sample) });
            Refuses(() => Contract.Evolve(store, e => e.Event("members", "paddle", Sync160Timed)), "store v2: a local contract's edits name no variant");
            Refuses(() => Contract.Evolve(store, e => e.AddEvent("members", "ball", Sync160Sample)), "store v2: a local contract's edits name no variant");
            Refuses(() => Contract.Evolve(store, e => e.RemoveEvent("members", "paddle")), "store v2: a local contract's edits name no variant");
            Refuses(() => Contract.Evolve(Sync160EvolveMatch, e => e.Event("paddle", Sync160Timed)), "match v2: a remote contract's edits name a variant");
            Refuses(() => Contract.Evolve(Sync160EvolveMatch, e => e.AddEvent("ball", Sync160Sample)), "match v2: a remote contract's edits name a variant");
            Refuses(() => Contract.Evolve(Sync160EvolveMatch, e => e.RemoveEvent("paddle")), "match v2: a remote contract's edits name a variant");
            Refuses(() => Contract.Evolve(Sync160EvolveMatch, e => e.AddEvent("nobody", "ball", Sync160Sample)), "match v2.nobody: unknown variant (the variant set never changes)");
        }),

        // fingerprint.test.ts: fingerprints of event members
        Case("contracts.fingerprint.event-members-keep-a-contract-without-events-at-its-fingerprint-an-event-member-changes-it-under-its-own-kind", () =>
        {
            var plain = Sync160Remote("n", new() { ["a"] = new() { State = { ["s"] = Int() } } });
            var empty = Sync160Remote("n", new() { ["a"] = new() { State = { ["s"] = Int() }, Events = new() } });
            var withEvent = Sync160Remote("n", new() { ["a"] = new() { State = { ["s"] = Int() }, Events = { ["e"] = Int() } } });
            var asState = Sync160Remote("n", new() { ["a"] = new() { State = { ["s"] = Int(), ["e"] = Int() } } });
            Equal(plain.Fingerprint, empty.Fingerprint, "no events, the same fingerprint");
            Equal(3, new[] { plain.Fingerprint, withEvent.Fingerprint, asState.Fingerprint }.Distinct().Count(), "three fingerprints");
            var store = Sync160Local("n", new() { ["e"] = Event(Int()) });
            Check(store.Fingerprint != Sync160Local("n", new() { ["e"] = Int() }).Fingerprint, "an event is no state member");
            // The node a TypeScript declaration hashes: { event: <normal form> } under the member's name.
            JsonNode Normal(JsonNode schema) => ContractsInternal.SnapshotSchema(schema, "n").Normal.DeepClone();
            Equal(ContractsInternal.FingerprintOf(new JsonObject
            {
                ["kind"] = "local", ["params"] = new JsonArray(), ["members"] = new JsonObject { ["e"] = new JsonObject { ["event"] = Normal(Int()) } },
            }), store.Fingerprint, "the event member's node");
            // A queries member's node: { queries: <item>, args: { <query>: <arguments> } }, its queries sorted.
            var queried = Sync160Local("n", new() { ["q"] = Queries(Int(), ("b", Nul()), ("a", Obj(("id", Str())))) });
            Equal(ContractsInternal.FingerprintOf(new JsonObject
            {
                ["kind"] = "local",
                ["params"] = new JsonArray(),
                ["members"] = new JsonObject
                {
                    ["q"] = new JsonObject { ["queries"] = Normal(Int()), ["args"] = new JsonObject { ["a"] = Normal(Obj(("id", Str()))), ["b"] = Normal(Nul()) } },
                },
            }), queried.Fingerprint, "the queries member's node, its queries sorted");
        }),

        // schema.test.ts: the types of a contract's members (authored counterpart)
        Case("contracts.schema.160-every-member-kind-is-declared-and-named-through-public-types", () =>
        {
            var home = new HostKind("home", new() { ["id"] = Str() });
            var item = Obj(("id", Str()), ("kind", OneOf("a", "b")), ("size", Opt(Int())), ("tags", Arr(Str())));
            OrderedDictionary<string, Member> State() => new()
            {
                ["text"] = Str(@default: ""), ["mode"] = OneOf("on", "off", 1), ["any"] = Unknown(), ["flag"] = Bool(), ["nothing"] = Nul(),
            };
            OrderedDictionary<string, Member> Methods() => new()
            {
                ["plain"] = Method(), ["put"] = Method(Obj(("at", Num()), ("what", Union(Lit("x"), Map(Int())))), item),
            };
            var members = State();
            foreach (var (name, method) in Methods()) members[name] = method;
            members["items"] = Keyed(item);
            members["homes"] = Keyed(home, item);
            members["changed"] = Event(item);
            members["list"] = Queries(item, ("all", Nul()), ("some", Obj(("kind", OneOf("a", "b")))));
            var stored = Contract.Local("fixture.stored", new() { Version = 1, Params = new() { ["part"] = OneOf("left", "right") }, Members = members });
            var served = Contract.Remote("fixture.served", new()
            {
                Version = 1,
                Params = new() { ["id"] = Str() },
                HostAddress = @params => home.Address(@params),
                Admission = Obj(("role", OneOf("editor", "reader"))),
                Variants = new()
                {
                    ["editor"] = new() { State = State(), Methods = Methods(), Events = { ["changed"] = item } },
                    ["reader"] = new() { State = State() },
                },
            });
            Equal("state:[text,mode,any,flag,nothing] methods:[plain,put] events:[changed]", Sync160Names(stored.MemberNames()), "the local contract");
            Equal("state:[text,mode,any,flag,nothing] methods:[plain,put] events:[changed]", Sync160Names(served.MemberNames("editor")), "the remote editor");
            Equal("state:[text,mode,any,flag,nothing] methods:[] events:[]", Sync160Names(served.MemberNames("reader")), "the remote reader");
            // Every type a declaration or an implementation of these contracts names is public (upstream: exported from Chord's entry point).
            foreach (var type in new[]
            {
                typeof(Contract), typeof(ContractEditor), typeof(Schemas), typeof(Member), typeof(Property), typeof(Optional), typeof(Literal),
                typeof(MethodDeclaration), typeof(KeyedMember), typeof(EventDeclaration), typeof(QueriesMember), typeof(HostKind), typeof(Address),
                typeof(RemoteDeclaration), typeof(LocalDeclaration), typeof(VariantDeclaration), typeof(MemberNames), typeof(AcceptsTable),
                typeof(VersionMappings), typeof(StateSection), typeof(StateEntry), typeof(StateMapping), typeof(MethodMapping),
                typeof(INamedQuery), typeof(QueryPage), typeof(ChordEvent), typeof(IEventMember), typeof(IEventSource), typeof(IEventSubscription),
            }) Check(type.IsPublic, $"{type.Name} is public");
            // Upstream's Method, LiteralsSchema and HostKeyed: MethodDeclaration, the schema OneOf builds, and a keyed member with its host kind.
            Check(Method() is MethodDeclaration { Args: not null, Result: not null }, "Method");
            Json("""{"anyOf":[{"type":"string","const":"a"},{"type":"string","const":"b"}]}""", OneOf("a", "b"), "LiteralsSchema");
            Check(ReferenceEquals(home, Keyed(home, item).KeyHost), "HostKeyed");
        }),
        Case("contracts.contract.160-variant-names-index-event-members-sorted-and-share-one-empty-list", () =>
        {
            var match = Sync160Remote("app.match", new()
            {
                ["player"] = new() { State = { ["score"] = Int() }, Methods = { ["serve"] = Method() }, Events = { ["paddle"] = Sync160Paddle, ["ball"] = Sync160Sample } },
                ["watcher"] = new() { State = { ["score"] = Int() } },
                ["referee"] = new() { State = { ["score"] = Int() } },
            });
            var variants = ContractsInternal.CurrentVariants(match);
            var player = ContractsInternal.VariantNamesOf(variants["player"]);
            // Links index a variant's event members as they index its state and methods: sorted.
            Sequence(["ball", "paddle"], player.Events, "the event members, sorted");
            Sequence(["score"], player.State, "the state members");
            Sequence(["serve"], player.Methods, "the methods");
            var watcher = ContractsInternal.VariantNamesOf(variants["watcher"]);
            Equal(0, watcher.Events.Count, "no events");
            Check(ReferenceEquals(watcher.Events, ContractsInternal.VariantNamesOf(variants["referee"]).Events), "contracts without events share one empty list");
            // An older version's names (an older consumer's binding).
            var v2 = Contract.Evolve(match, e => e.RemoveEvent("player", "ball"));
            Sequence(["ball", "paddle"], ContractsInternal.VariantNamesOf(ContractsInternal.VariantsAt(v2, 1)["player"]).Events, "version 1's events");
            Sequence(["paddle"], ContractsInternal.VariantNamesOf(ContractsInternal.CurrentVariants(v2)["player"]).Events, "version 2's events");
        }),
    ];
}
