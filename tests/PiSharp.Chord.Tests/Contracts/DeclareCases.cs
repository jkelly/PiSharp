using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using static ChordSupport;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/contracts/declare.test.ts. Upstream declares through an
// untyped door, as a JavaScript caller would; the C# declarations are typed, so these refusals cannot be written and C# rules them out when
// it compiles: a name that is no string (here a null name, the one non-string a string parameter carries, is refused with upstream's
// message), a fractional version (Version is a long), an unknown declaration key, a host address that is no function, an unknown variant
// key, and a local declaration with `variants` or `hostAddress`. Every other refusal runs as upstream's does, with its message. The
// @ts-expect-error lines are type-level: the C# declarations' required members refuse a remote declaration without a host address when it
// compiles, and the cases check the run-time refusal of a null one; a member declaration is a Member in every position, so the D11 and
// keyed refusals happen when the declaration runs. The evolve halves of the reserved-names and keyed cases came with area cv2-evolve.
//
// The pins are authored expectations taken from upstream v1.5.0's tests: the fixed fingerprint of fingerprint.test.ts ("pin the normal
// form"), and the fingerprints, params and normal form that test/next/schema/recorded.json records for the contracts of
// test/next/schema/declarations.ts (schema-switch.test.ts checks the same values in TypeScript). A C# host that computes them serves
// TypeScript peers the same contracts. The rest of fingerprint.test.ts and schema-switch.test.ts are areas cv2-evolve's and cv2-data's.
internal static partial class Program
{
    private static readonly JsonObject DeclareNote = Obj(("text", Str()));

    private static Address DeclareHostAddress(JsonObject _) => Server.Address();

    private static IEnumerable<(string, Func<Task>)> ContractsDeclareCases() =>
    [
        Case("contracts.declare.accept-a-remote-and-a-local-declaration", () =>
        {
            var notes = Contract.Remote("notes", new()
            {
                Version = 1,
                Params = new() { ["session"] = Str() },
                HostAddress = DeclareHostAddress,
                Variants = new()
                {
                    ["editor"] = new() { State = { ["notes"] = Arr(DeclareNote) }, Methods = { ["add"] = Method(DeclareNote, Str()), ["clear"] = Method() } },
                    ["reader"] = new() { State = { ["notes"] = Arr(DeclareNote) } },
                },
            });
            Equal(ContractKind.Remote, notes.Kind, "kind");
            Equal("notes", notes.Name, "name");
            Equal(1L, notes.Version, "version");
            Check(Regex.IsMatch(notes.Fingerprint, "^[0-9a-f]{16}\\z"), "fingerprint: " + notes.Fingerprint);
            var info = Contract.Local("app.session-info", new()
            {
                Version = 3,
                Params = new(),
                Members = new() { ["init"] = Obj(("title", Str())), ["clear"] = Method() },
            });
            Equal(ContractKind.Local, info.Kind, "local kind");
            Equal("app.session-info", info.Name, "local name");
            Equal(3L, info.Version, "local version");
        }),
        .. RemoteRefusals(),
        .. LocalRefusals(),
        Case("contracts.declare.refuse-a-remote-declaration-without-a-host-address", () =>
            Refuses(() => Contract.Remote("n", new() { Version = 1, Params = new(), HostAddress = null!, Variants = new() { ["a"] = new() } }),
                "n.hostAddress: expected a function of the params")),
        Case("contracts.declare.does-not-check-state-for-closed-objects", () =>
            Accepts(() => Contract.Local("i", new()
            {
                Version = 1,
                Params = new(),
                Members = new() { ["s"] = Parse("""{"type":"object","properties":{"a":{"type":"string"}},"required":["a"]}""") },
            }), "an open state object")),
        Case("contracts.declare.d11-refuses-a-name-that-is-state-in-one-variant-and-a-method-in-another", () =>
            Refuses(() => Contract.Remote("n", new()
            {
                Version = 1,
                Params = new(),
                HostAddress = DeclareHostAddress,
                Variants = new() { ["a"] = new() { State = { ["x"] = Str() } }, ["b"] = new() { Methods = { ["x"] = Method() } } },
            }), "n.b.methods.x: \"x\" is a state member; its kind never changes")),
        Case("contracts.declare.d11-refuses-one-name-as-state-and-method-within-a-variant", () =>
            Refuses(() => Contract.Remote("n", new()
            {
                Version = 1,
                Params = new(),
                HostAddress = DeclareHostAddress,
                Variants = new() { ["a"] = new() { State = { ["x"] = Str() }, Methods = { ["x"] = Method() } } },
            }), "n.a.methods.x: \"x\" is a state member")),
        Case("contracts.declare.d11-refuses-a-method-with-different-signatures-in-two-variants", () =>
        {
            Refuses(() => Contract.Remote("n", new()
            {
                Version = 1,
                Params = new(),
                HostAddress = DeclareHostAddress,
                Variants = new() { ["a"] = new() { Methods = { ["m"] = Method(Str()) } }, ["b"] = new() { Methods = { ["m"] = Method(Int()) } } },
            }), "n.b.methods.m: a method has the same signature in every variant");
            Refuses(() => Contract.Remote("n", new()
            {
                Version = 1,
                Params = new(),
                HostAddress = DeclareHostAddress,
                Variants = new()
                {
                    ["a"] = new() { Methods = { ["m"] = Method(Str(), Str()) } },
                    ["b"] = new() { Methods = { ["m"] = Method(Str(max: 3), Str()) } },
                },
            }), "n.b.methods.m: a method has the same signature in every variant");
        }),
        Case("contracts.declare.d11-allows-state-schemas-to-differ-per-variant-redaction", () =>
            Accepts(() => Contract.Remote("n", new()
            {
                Version = 1,
                Params = new(),
                HostAddress = DeclareHostAddress,
                Variants = new()
                {
                    ["editor"] = new() { State = { ["notes"] = Arr(Obj(("text", Str()), ("author", Str()))) }, Methods = { ["m"] = Method(Str()) } },
                    ["reader"] = new() { State = { ["notes"] = Arr(Obj(("text", Str()))) }, Methods = { ["m"] = Method(Str()) } },
                },
            }), "redacted state per variant")),
        .. new[] { "then", "release", "variant", "state", "retry" }.Select(name => Case($"contracts.declare.reserved-member-names-refuses-{name}-in-remote-local-and-evolve", () =>
        {
            Refuses(() => Contract.Remote("n", new()
            {
                Version = 1,
                Params = new(),
                HostAddress = DeclareHostAddress,
                Variants = new() { ["v"] = new() { State = { [name] = Str() } } },
            }), $"n.v.state.{name}: \"{name}\" is reserved: a reference has it");
            Refuses(() => Contract.Local("n", new() { Version = 1, Params = new(), Members = new() { [name] = Method() } }),
                $"n.members.{name}: \"{name}\" is reserved: a reference has it");
            var @base = Contract.Local("n", new() { Version = 1, Params = new(), Members = new() });
            Refuses(() => Contract.Evolve(@base, e => e.AddState(name, Str())), $"n v2.members.{name}: \"{name}\" is reserved: a reference has it");
        })),
        Case("contracts.declare.keyed-members-declare-a-keyed-member-in-a-local-contract", () =>
        {
            var keyedStore = Contract.Local("app.store", new() { Version = 1, Params = new(), Members = new() { ["notes"] = Keyed(DeclareNote), ["add"] = Method() } });
            var stateStore = Contract.Local("app.store", new() { Version = 1, Params = new(), Members = new() { ["notes"] = DeclareNote, ["add"] = Method() } });
            var variant = ContractsInternal.CurrentVariants(keyedStore)[ContractsInternal.Local];
            Sequence(["notes"], variant.Keyed.Keys, "keyed members");
            Sequence([], variant.State.Keys, "state members");
            Check(keyedStore.Fingerprint != stateStore.Fingerprint, "a keyed member is a node of its own in the fingerprint");
            // Evolving the contract carries its keyed members over unchanged (their versions are slice 7's).
            var next = Contract.Evolve(keyedStore, e => e.AddMethod("clear", Str(), Str()));
            Check(ContractsInternal.CurrentVariants(next)[ContractsInternal.Local].Keyed.ContainsKey("notes"), "the keyed member is carried over");
        }),
        Case("contracts.declare.keyed-members-refuse-a-keyed-member-in-a-remote-contract", () =>
            Refuses(() => Contract.Remote("app.notes", new()
            {
                Version = 1,
                Params = new(),
                HostAddress = DeclareHostAddress,
                Variants = new() { ["editor"] = new() { State = { ["notes"] = Keyed(DeclareNote) } } },
            }), "app.notes.editor.state.notes: keyed members are only in local contracts")),
        Case("contracts.declare.pins-the-fingerprint-of-upstream-fingerprint-test-notes", () =>
        {
            // fingerprint.test.ts, "pin the normal form: a fixed contract has a fixed hash".
            Equal(2, ContractsInternal.NormalFormVersion, "NORMAL_FORM_VERSION");
            var notes = Contract.Remote("notes", new()
            {
                Version = 1,
                Params = new() { ["session"] = Str() },
                HostAddress = DeclareHostAddress,
                Variants = new()
                {
                    ["editor"] = new() { State = { ["notes"] = Obj(("text", Str())) }, Methods = { ["add"] = Method(Obj(("text", Str())), Str()) } },
                    ["reader"] = new() { State = { ["notes"] = Obj(("text", Str())) } },
                },
            });
            Equal("b3eda86070a7e319", notes.Fingerprint, "the pinned fingerprint");
        }),
        Case("contracts.declare.fingerprints-hash-javascripts-json-text", () =>
        {
            // Authored from fingerprint.ts and JSON.stringify's rules: the hash input is the text JavaScript writes, so array-index keys
            // come first in ascending order, numbers are written as Number::toString writes them and lone surrogates are escaped.
            Equal("""{"9":2,"10":1,"a":3,"01":4}""", ChordJson.Stringify(Parse("""{"10":1,"9":2,"a":3,"01":4}""")), "array-index keys first");
            Equal("[1e+21,0.1,0,1.5e-7,100]", ChordJson.Stringify(Parse("[1e21,0.1,-0,1.5e-7,1e2]")), "Number::toString");
            Equal("\"\\ud800\\n\"", ChordJson.Stringify(JsonValue.Create("\ud800\n")), "a lone surrogate and a control character");
            // FNV-1a's offset bases, high lane first, for empty input.
            Equal("cbf29ce4811c9dc5", ContractsInternal.Hash64(""), "hash64 of no bytes");
            var normal = Parse("""{"kind":"local","params":[],"members":{}}""");
            Equal(ContractsInternal.Hash64("""{"normalFormVersion":2,"value":{"kind":"local","params":[],"members":{}}}"""), ContractsInternal.FingerprintOf(normal),
                "fingerprintOf hashes {normalFormVersion, value}");
            Equal(ContractsInternal.FingerprintOf(normal), Contract.Local("x", new() { Version = 1, Params = new(), Members = new() }).Fingerprint,
                "an empty local contract's fingerprint");
        }),
        Case("contracts.declare.pins-the-schema-switch-contract-fingerprints-and-normal-forms", () =>
        {
            // test/next/schema/declarations.ts's contracts and recorded.json's fingerprints.contracts and documents.profile.normal.
            var (everythingSchema, everything, account, owner) = SchemaSwitchDeclarations();
            Equal("3861c5a457ef4aa3", everything.Fingerprint, "Everything");
            Equal("821be6a79fbe350a", account.Fingerprint, "Account");
            Equal("b01eec2d4e2311b0", owner.Fingerprint, "Owner");
            Equal("""[["id",{"type":"string"}],["shard",{"type":"integer"}]]""", ChordJson.Stringify(ContractsInternal.DataOf(everything).Params.Normal), "Everything's params");
            Equal("""[["user",{"minLength":1,"type":"string"}]]""", ChordJson.Stringify(ContractsInternal.DataOf(account).Params.Normal), "Account's params");
            Equal("[]", ChordJson.Stringify(ContractsInternal.DataOf(owner).Params.Normal), "Owner's params");
            Sequence(["owner", "guest"], ContractsInternal.CurrentVariants(account).Keys, "Account's variants in declaration order");
            // JSON.stringify of the snapshot's normal form, byte for byte (keys sorted, annotations dropped, `required` sorted).
            Equal(SchemaSwitchProfileNormal, ChordJson.Stringify(ContractsInternal.SnapshotSchema(everythingSchema, "p").Normal), "everything's normal form");
            var members = ContractsInternal.CurrentVariants(everything)[ContractsInternal.Local];
            Equal("""{"additionalProperties":false,"properties":{"body":{"type":"string"}},"required":["body"],"type":"object"}""",
                ChordJson.Stringify(members.Keyed["notes"].Normal), "Everything's keyed notes");
            Equal("""{"additionalProperties":false,"properties":{"data":{"maxLength":1024,"type":"bytes"}},"required":["data"],"type":"object"}""",
                ChordJson.Stringify(members.Methods["blob"].Args.Normal), "Everything's blob arguments");
            Equal("""{"anyOf":[{"const":"ok","type":"string"},{"additionalProperties":false,"properties":{"error":{"type":"string"}},"required":["error"],"type":"object"}]}""",
                ChordJson.Stringify(members.Methods["both"].Result.Normal), "Everything's both result");
        }),
    ];

    /// <summary>"remote refuses %s": each case declares a valid host address unless it checks that one, so the check it names is the one
    /// reached.</summary>
    private static IEnumerable<(string, Func<Task>)> RemoteRefusals()
    {
        RemoteDeclaration Valid(long version = 1) => new() { Version = version, Params = new(), HostAddress = DeclareHostAddress, Variants = new() { ["a"] = new() } };
        (string Case, string? Name, Func<RemoteDeclaration> Declaration, string Message)[] rows =
        [
            ("empty-name", "", () => Valid(), "A contract name \"\": expected lowercase"),
            ("a-null-name", null, () => Valid(), "A contract name null: expected lowercase"),
            ("an-uppercase-name", "Notes", () => Valid(), "expected lowercase"),
            ("a-name-with-a-colon", "a:b", () => Valid(), "expected lowercase"),
            ("version-zero", "n", () => Valid(0), "n.version: expected a positive integer"),
            ("a-negative-version", "n", () => Valid(-1), "n.version: expected a positive integer"),
            ("unsafe-version", "n", () => Valid(9007199254740992), "n.version: expected a positive integer"),
            ("no-host-address", "n", () => new() { Version = 1, Params = new(), HostAddress = null!, Variants = new() { ["a"] = new() } },
                "n.hostAddress: expected a function of the params"),
            ("no-variants", "n", () => new() { Version = 1, Params = new(), HostAddress = DeclareHostAddress, Variants = new() },
                "n.variants: at least one variant is required"),
            ("missing-variants", "n", () => new() { Version = 1, Params = new(), HostAddress = DeclareHostAddress, Variants = null! },
                "n.variants: expected an object"),
            ("missing-params", "n", () => new() { Version = 1, Params = null!, HostAddress = DeclareHostAddress, Variants = new() { ["a"] = new() } },
                "n.params: expected an object"),
            ("a-schema-under-methods", "n", () => new() { Version = 1, Params = new(), HostAddress = DeclareHostAddress,
                Variants = new() { ["a"] = new() { Methods = { ["m"] = Str() } } } }, "n.a.methods.m: expected method(...)"),
            ("a-method-under-state", "n", () => new() { Version = 1, Params = new(), HostAddress = DeclareHostAddress,
                Variants = new() { ["a"] = new() { State = { ["s"] = Method() } } } }, "n.a.state.s: a method belongs in methods"),
            ("bad-state-schema", "n", () => new() { Version = 1, Params = new(), HostAddress = DeclareHostAddress,
                Variants = new() { ["a"] = new() { State = { ["s"] = Parse("""{"not":{}}""") } } } }, "n.a.state.s: unsupported keyword \"not\""),
            ("open-arguments", "n", () => new() { Version = 1, Params = new(), HostAddress = DeclareHostAddress,
                Variants = new() { ["a"] = new() { Methods = { ["m"] = Method(Parse("""{"type":"object","properties":{}}""")) } } } },
                "n.a.methods.m.args: argument objects must be closed"),
            ("bad-result", "n", () => new() { Version = 1, Params = new(), HostAddress = DeclareHostAddress,
                Variants = new() { ["a"] = new() { Methods = { ["m"] = Method(null, Parse("""{"not":{}}""")) } } } },
                "n.a.methods.m.result: unsupported keyword \"not\""),
        ];
        return rows.Select(row => Case($"contracts.declare.remote-refuses-{row.Case}", () => Refuses(() => Contract.Remote(row.Name!, row.Declaration()), row.Message)));
    }

    /// <summary>"local refuses %s" (its unknown-key and host-address rows are ruled out by the C# declaration type).</summary>
    private static IEnumerable<(string, Func<Task>)> LocalRefusals()
    {
        (string Case, Func<LocalDeclaration> Declaration, string Message)[] rows =
        [
            ("bad-member-schema", () => new() { Version = 1, Params = new(), Members = new() { ["s"] = Parse("""{"not":{}}""") } },
                "i.members.s: unsupported keyword \"not\""),
            ("open-arguments", () => new() { Version = 1, Params = new(), Members = new() { ["m"] = Method(Parse("""{"type":"object","properties":{}}""")) } },
                "i.members.m.args: argument objects must be closed"),
            ("missing-members", () => new() { Version = 1, Params = new(), Members = null! }, "i.members: expected an object"),
        ];
        return rows.Select(row => Case($"contracts.declare.local-refuses-{row.Case}", () => Refuses(() => Contract.Local("i", row.Declaration()), row.Message)));
    }

    /// <summary>test/next/schema/declarations.ts: `everything`, and the contracts Everything, Account and Owner.</summary>
    private static (JsonObject Schema, Contract Everything, Contract Account, Contract Owner) SchemaSwitchDeclarations()
    {
        // A schema with every helper and option a declaration can use.
        var everything = Obj(
            ("name", Str(min: 1, max: 40, pattern: "^[a-z ]+$")),
            ("nick", Str(@default: "x")),
            ("age", Int(min: 0, max: 150)),
            ("count", Int(@default: 0)),
            ("score", Num(min: 0, max: 100)),
            ("ratio", Num(@default: 0.5)),
            ("active", Bool()),
            ("verified", Bool(@default: false)),
            ("role", OneOf("admin", "user", "guest")),
            ("level", OneOf(1, 2, 3)),
            ("kind", Lit("person")),
            ("one", Lit(1)),
            ("yes", Lit(true)),
            ("tags", Arr(Str(), min: 0, max: 5)),
            ("points", Arr(Int())),
            ("extra", Map(Int())),
            ("note", Opt(Str())),
            ("nothing", Nul()),
            ("choice", Union(Str(), Int(), Obj(("deep", Arr(Obj(("x", Num()))))))),
            ("nested", Obj(("inner", Opt(Obj(("v", Str())))))));
        var everythingContract = Contract.Local("fixture.switch.everything", new()
        {
            Version = 1,
            Params = new() { ["id"] = Str(), ["shard"] = Int() },
            Members = new()
            {
                ["profile"] = everything,
                ["notes"] = Keyed(Obj(("body", Str()))),
                ["plain"] = Method(),
                ["args"] = Method(Obj(("a", Str()), ("b", Opt(Int())), ("any", Unknown()))),
                ["result"] = Method(null, Arr(Str())),
                ["both"] = Method(Obj(("when", Int(min: 0))), Union(Lit("ok"), Obj(("error", Str())))),
                ["blob"] = Method(Obj(("data", Bytes(max: 1024))), Bytes()),
            },
        });
        var server = new HostKind("fixture.switch.server", new() { ["region"] = Str() });
        var account = Contract.Remote("fixture.switch.account", new()
        {
            Version = 1,
            Params = new() { ["user"] = Str(min: 1) },
            HostAddress = _ => server.Address(("region", "eu")),
            Variants = new()
            {
                ["owner"] = new() { State = { ["balance"] = Num() }, Methods = { ["pay"] = Method(Obj(("amount", Num(min: 0))), Bool()) } },
                ["guest"] = new() { State = { ["balance"] = Num() } },
            },
        });
        var owner = Contract.Local("fixture.switch.owner", new()
        {
            Version = 1,
            Params = new(),
            Members = new()
            {
                ["profile"] = everything,
                ["write"] = Method(Obj(("id", Str()), ("body", Str()))),
                ["read"] = Method(Str(), Str()),
                ["dump"] = Method(Obj(), Unknown()),
            },
        });
        return (everything, everythingContract, account, owner);
    }

    /// <summary>recorded.json's documents.profile.normal: snapshotSchema(everything).normal.</summary>
    private const string SchemaSwitchProfileNormal =
        """{"additionalProperties":false,"properties":{"active":{"type":"boolean"},"age":{"maximum":150,"minimum":0,"type":"integer"},"choice":{"anyOf":[{"type":"string"},{"type":"integer"},{"additionalProperties":false,"properties":{"deep":{"items":{"additionalProperties":false,"properties":{"x":{"type":"number"}},"required":["x"],"type":"object"},"type":"array"}},"required":["deep"],"type":"object"}]},"count":{"default":0,"type":"integer"},"extra":{"patternProperties":{"^.*$":{"type":"integer"}},"type":"object"},"kind":{"const":"person","type":"string"},"level":{"anyOf":[{"const":1,"type":"number"},{"const":2,"type":"number"},{"const":3,"type":"number"}]},"name":{"maxLength":40,"minLength":1,"pattern":"^[a-z ]+$","type":"string"},"nested":{"additionalProperties":false,"properties":{"inner":{"additionalProperties":false,"properties":{"v":{"type":"string"}},"required":["v"],"type":"object"}},"type":"object"},"nick":{"default":"x","type":"string"},"note":{"type":"string"},"nothing":{"type":"null"},"one":{"const":1,"type":"number"},"points":{"items":{"type":"integer"},"type":"array"},"ratio":{"default":0.5,"type":"number"},"role":{"anyOf":[{"const":"admin","type":"string"},{"const":"user","type":"string"},{"const":"guest","type":"string"}]},"score":{"maximum":100,"minimum":0,"type":"number"},"tags":{"items":{"type":"string"},"maxItems":5,"minItems":0,"type":"array"},"verified":{"default":false,"type":"boolean"},"yes":{"const":true,"type":"boolean"}},"required":["active","age","choice","count","extra","kind","level","name","nested","nick","nothing","one","points","ratio","role","score","tags","verified","yes"],"type":"object"}""";
}
