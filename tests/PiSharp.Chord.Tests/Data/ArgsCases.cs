using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using static ChordSupport;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/data/args.test.ts. The contracts evolve with Contract.Evolve
// and bind with an AcceptsTable, which BindProvider checks with checkAccepts. A table's mappings are untyped (object?): an args mapping
// gets the caller's JsonNode and returns one. The type-level checks (expectTypeOf of CheckedArgs and of a call replacement's arguments)
// have no C# counterpart. A Uint8Array is a JsonValue holding a byte[]; vi.fn is a call counter; `as never` (a table missing a mapping) needs
// no cast, since a table's sections are optional in C#. The memory case's mapping captures a local, so it is a fresh delegate the compiler
// does not cache, like the arrow function upstream makes.
internal static partial class Program
{
    private static readonly Contract ArgsNotesV1 = Contract.Remote("notes", new()
    {
        Version = 1,
        Params = new() { ["session"] = Str() },
        HostAddress = _ => Server.Address(),
        Variants = new()
        {
            ["editor"] = new()
            {
                State = { ["notes"] = Arr(Obj(("text", Str()))) },
                Methods = { ["add"] = Method(Obj(("text", Str())), Str()), ["clear"] = Method(), ["attach"] = Method(Obj(("data", Bytes(max: 4)))) },
            },
            ["reader"] = new() { State = { ["notes"] = Arr(Obj(("text", Str()))) } },
        },
    });

    private static readonly Contract ArgsNotesV2 = Contract.Evolve(ArgsNotesV1, e => e.Args("add", Obj(("text", Str()), ("tag", Str()))).Result("add", Int()));

    private static readonly Contract ArgsNotesV3 = Contract.Evolve(ArgsNotesV2, e => e.RemoveMethod("attach"));

    /// <summary>vi.fn's call count of <see cref="ArgsToV2"/>.</summary>
    private static int argsToV2Calls;

    /// <summary><c>(old) => ({ ...old, tag: "v1" })</c>, counted.</summary>
    private static readonly Func<object?, object?> ArgsToV2 = old =>
    {
        argsToV2Calls++;
        var current = ((JsonNode)old!).DeepClone().AsObject();
        current["tag"] = "v1";
        return current;
    };

    /// <summary><c>(current) => String(current)</c>.</summary>
    private static readonly Func<object?, object?> ArgsResultToOld = current => (JsonNode)ChordJson.ToJavaScriptString((JsonNode?)current);

    /// <summary><c>async () => null</c>.</summary>
    private static readonly Func<object?, object, Task<object?>> ArgsAttach = (_, _) => Task.FromResult<object?>(null);

    /// <summary><c>{ 1: { methods: { add: { args: toV2, result: resultToOld }, attach: { call: attach } } }, 2: { methods: { attach: "refuse" } } }</c>.</summary>
    private static readonly AcceptsTable ArgsAccepts = new()
    {
        [1] = new() { Methods = { ["add"] = new MethodMapping { Args = ArgsToV2, Result = ArgsResultToOld }, ["attach"] = new MethodMapping { Call = ArgsAttach } } },
        [2] = new() { Methods = { ["attach"] = MethodMapping.Refuse } },
    };

    private static readonly Provider ArgsProvider = DataInternal.BindProvider(ArgsNotesV3, ArgsAccepts);

    /// <summary>The error a refused check throws, as JSON.</summary>
    private static JsonObject ArgsRefusal(Action run)
    {
        try
        {
            run();
        }
        catch (ChordError error)
        {
            return error.ToJson();
        }
        throw new InvalidOperationException("expected a refusal");
    }

    /// <summary>toMatchObject on a refusal: each given field of the error's JSON.</summary>
    private static void ArgsMatches(string expected, JsonObject actual, string what)
    {
        foreach (var (key, value) in JsonNode.Parse(expected)!.AsObject()) Json(value?.ToJsonString() ?? "null", actual[key], $"{what}: {key}");
    }

    private static IEnumerable<(string, Func<Task>)> DataArgsCases() =>
    [
        Case("data.args.bind-provider-checks-the-accepts-table-once-and-lists-the-accepted-versions", () =>
        {
            Check(ReferenceEquals(ArgsNotesV3, ArgsProvider.Contract), "the provider's contract");
            Sequence([new AcceptedVersion(1, ArgsNotesV1.Fingerprint), new AcceptedVersion(2, ArgsNotesV2.Fingerprint)], ArgsProvider.Accepted,
                "the accepted versions");
            Refuses(() => DataInternal.BindProvider(ArgsNotesV3, new() { [1] = new() }), "notes accepts[1].methods.add: missing mapping");
        }),
        Case("data.args.bind-provider-compiles-nothing-and-calls-no-mapping", async () =>
        {
            var calls = argsToV2Calls;
            Equal(new Counts(0, 0, 0), await Counted(_ => DataInternal.BindProvider(ArgsNotesV3, ArgsAccepts)), "the counts");
            Equal(calls, argsToV2Calls, "toV2 is not called");
        }),
        Case("data.args.check-args-returns-current-version-arguments-as-passed-no-copy-never-mapped", () =>
        {
            var calls = argsToV2Calls;
            var raw = new JsonObject { ["text"] = "t", ["tag"] = "x" };
            var checkedArgs = DataInternal.CheckArgs(ArgsProvider, "add", raw, 3, ChordContext.Background);
            Equal(new CheckedArgs.Method(raw, null), checkedArgs, "{ kind: \"method\", args: raw, result: undefined }");
            Check(ReferenceEquals(raw, checkedArgs.Args), "the caller's own object");
            Equal(calls, argsToV2Calls, "toV2 is not called");
        }),
        Case("data.args.check-args-checks-an-old-version-against-the-old-schema-then-maps-directly-to-the-current-shape", () =>
        {
            var mapped = (CheckedArgs.Method)DataInternal.CheckArgs(ArgsProvider, "add", new JsonObject { ["text"] = "t" }, 1, ChordContext.Background);
            Json("""{"text":"t","tag":"v1"}""", mapped.Args, "the mapped arguments");
            Check(ReferenceEquals(ArgsResultToOld, mapped.Result), "the result mapping");
            ArgsMatches("""{"code":"invalid","reason":"args","details":{"path":"/tag"}}""",
                ArgsRefusal(() => DataInternal.CheckArgs(ArgsProvider, "add", new JsonObject { ["text"] = "t", ["tag"] = "x" }, 1, ChordContext.Background)), "v1 with v2 arguments");
            // Version 2 already has the current args: checked with that schema, not mapped.
            var raw = new JsonObject { ["text"] = "t", ["tag"] = "x" };
            Equal(new CheckedArgs.Method(raw, null), DataInternal.CheckArgs(ArgsProvider, "add", raw, 2, ChordContext.Background), "version 2");
        }),
        Case("data.args.check-args-returns-the-old-arguments-with-the-call-replacement-never-invokes-it", () =>
        {
            var raw = new JsonObject { ["data"] = JsonValue.Create(new byte[2]) };
            Equal(new CheckedArgs.Call(raw, ArgsAttach), DataInternal.CheckArgs(ArgsProvider, "attach", raw, 1, ChordContext.Background), "{ kind: \"call\", args: raw, call: attach }");
        }),
        Case("data.args.check-args-refuses-unknown-fields-nested-ones-too-and-checks-bytes-by-instanceof-and-length", () =>
        {
            ArgsMatches("""{"details":{"path":"/extra"}}""", ArgsRefusal(() =>
                DataInternal.CheckArgs(ArgsProvider, "add", new JsonObject { ["text"] = "t", ["tag"] = "x", ["extra"] = 1 }, 3, ChordContext.Background)), "an unknown field");
            ArgsMatches("""{"details":{"path":"/data"}}""", ArgsRefusal(() =>
                DataInternal.CheckArgs(ArgsProvider, "attach", new JsonObject { ["data"] = new JsonArray(1, 2) }, 1, ChordContext.Background)), "an array for bytes");
            ArgsMatches("""{"details":{"path":"/data"}}""", ArgsRefusal(() =>
                DataInternal.CheckArgs(ArgsProvider, "attach", new JsonObject { ["data"] = JsonValue.Create(new byte[5]) }, 1, ChordContext.Background)), "5 bytes");
            var nested = DataInternal.BindProvider(Contract.Local("nested", new()
            {
                Version = 1,
                Params = new(),
                Members = new() { ["m"] = Method(Obj(("a", Obj(("b", Str()))))) },
            }), new AcceptsTable());
            ArgsMatches("""{"details":{"path":"/a/c"}}""", ArgsRefusal(() =>
                DataInternal.CheckArgs(nested, "m", Parse("""{"a":{"b":"x","c":1}}"""), 1, ChordContext.Background)), "a nested unknown field");
        }),
        Case("data.args.check-args-takes-null-for-no-argument-methods", () =>
        {
            Equal(new CheckedArgs.Method(null, null), DataInternal.CheckArgs(ArgsProvider, "clear", null, 3, ChordContext.Background), "null arguments");
            ArgsMatches("""{"code":"invalid","reason":"args","details":{"path":"/"}}""",
                ArgsRefusal(() => DataInternal.CheckArgs(ArgsProvider, "clear", new JsonObject(), 3, ChordContext.Background)), "an object for no arguments");
        }),
        Case("data.args.check-args-checks-local-callers-too", () =>
        {
            var info = DataInternal.BindProvider(Contract.Local("info", new()
            {
                Version = 1,
                Params = new(),
                Members = new() { ["save"] = Method(Obj(("title", Str())), Nul()) },
            }), new AcceptsTable());
            Json("""{"title":"t"}""", DataInternal.CheckArgs(info, "save", Parse("""{"title":"t"}"""), 1, ChordContext.Background).Args, "the arguments");
            ArgsMatches("""{"message":"args/title: expected string, got integer"}""",
                ArgsRefusal(() => DataInternal.CheckArgs(info, "save", Parse("""{"title":1}"""), 1, ChordContext.Background)), "a wrong field");
        }),
        Case("data.args.check-args-treats-format-as-an-annotation-a-contract-with-format-email-accepts-any-string", () =>
        {
            var mail = DataInternal.BindProvider(Contract.Local("mail", new()
            {
                Version = 1,
                Params = new(),
                Members = new() { ["send"] = Method(Obj(("to", Parse("""{"type":"string","format":"email"}"""))), Nul()) },
            }), new AcceptsTable());
            Json("""{"to":"nope"}""", DataInternal.CheckArgs(mail, "send", Parse("""{"to":"nope"}"""), 1, ChordContext.Background).Args, "no email");
        }),
        Case("data.args.check-args-refuses-an-unknown-method-an-unaccepted-version-and-a-refused-method", () =>
        {
            Json("""{"code":"invalid","reason":"member","message":"notes v3 has no method \"missing\""}""",
                ArgsRefusal(() => DataInternal.CheckArgs(ArgsProvider, "missing", null, 3, ChordContext.Background)), "an unknown method");
            ArgsMatches("""{"code":"invalid","reason":"member"}""",
                ArgsRefusal(() => DataInternal.CheckArgs(ArgsProvider, "attach", null, 3, ChordContext.Background)), "a removed method");
            Json("""{"code":"contract_mismatch","message":"notes v4 is not accepted"}""",
                ArgsRefusal(() => DataInternal.CheckArgs(ArgsProvider, "add", Parse("""{"text":"t"}"""), 4, ChordContext.Background)), "an unknown version");
            ArgsMatches("""{"code":"contract_mismatch"}""", ArgsRefusal(() =>
                DataInternal.CheckArgs(DataInternal.BindProvider(ArgsNotesV3, new AcceptsTable()), "add", Parse("""{"text":"t"}"""), 1, ChordContext.Background)),
                "a version not accepted");
            Json("""{"code":"contract_mismatch","message":"notes v2 refuses \"attach\""}""", ArgsRefusal(() =>
                DataInternal.CheckArgs(ArgsProvider, "attach", new JsonObject { ["data"] = JsonValue.Create(Array.Empty<byte>()) }, 2, ChordContext.Background)),
                "a refused method");
        }),
        Case("data.args.check-args-compiles-lazily-once-per-accepted-version-and-method-equal-schema-objects-share-a-validator", async () =>
        {
            var v1 = Contract.Local("lazy", new()
            {
                Version = 1,
                Params = new(),
                Members = new() { ["m"] = Method(Obj(("a", Str()))), ["clear"] = Method() },
            });
            var v2 = Contract.Evolve(v1, e => e.Args("m", Obj(("a", Str()), ("b", Str()))));
            var fresh = DataInternal.BindProvider(v2, new()
            {
                [1] = new()
                {
                    Methods =
                    {
                        ["m"] = new MethodMapping
                        {
                            Args = old =>
                            {
                                var current = ((JsonNode)old!).DeepClone().AsObject();
                                current["b"] = "";
                                return current;
                            },
                        },
                    },
                },
            });
            Action<ChordContext>[] calls =
            [
                context => DataInternal.CheckArgs(fresh, "clear", null, 2, context),
                context => DataInternal.CheckArgs(fresh, "clear", null, 2, context),
                // Version 1 shares the unchanged `clear` schema object with version 2.
                context => DataInternal.CheckArgs(fresh, "clear", null, 1, context),
                context => DataInternal.CheckArgs(fresh, "m", Parse("""{"a":"x"}"""), 1, context),
                context => DataInternal.CheckArgs(fresh, "m", Parse("""{"a":"x"}"""), 1, context),
                context => DataInternal.CheckArgs(fresh, "m", Parse("""{"a":"x","b":"y"}"""), 2, context),
            ];
            var compilations = new List<double>();
            foreach (var call in calls) compilations.Add((await Counted(call)).Compilations);
            Sequence([1.0, 0, 0, 1, 0, 1], compilations, "the compilations");
        }),
        Case("data.args.check-args-counts-one-validation-per-call-and-no-walk", async () =>
            Equal(new Counts(0, 1, 0), await Counted(context => DataInternal.CheckArgs(ArgsProvider, "add", Parse("""{"text":"t"}"""), 1, context)), "the counts")),
        Case("data.args.check-args-refuses-an-args-mapping-output-that-is-no-json-node", () =>
        {
            // Authored, PiSharp-only: a table's mappings are object?, so CheckArgs refuses what upstream's types rule out.
            var v1 = Contract.Local("typed", new() { Version = 1, Params = new(), Members = new() { ["m"] = Method(Obj(("a", Str()))) } });
            var v2 = Contract.Evolve(v1, e => e.Args("m", Obj(("a", Str()), ("b", Str()))));
            var bound = DataInternal.BindProvider(v2, new() { [1] = new() { Methods = { ["m"] = new MethodMapping { Args = _ => "not a node" } } } });
            var error = Throws<InvalidOperationException>(() => DataInternal.CheckArgs(bound, "m", Parse("""{"a":"x"}"""), 1, ChordContext.Background), "a string");
            Equal("typed accepts[1].methods.m.args: returned a String, not a JsonNode", error.Message, "the message");
            var nulled = DataInternal.BindProvider(v2, new() { [1] = new() { Methods = { ["m"] = new MethodMapping { Args = _ => null } } } });
            Equal(new CheckedArgs.Method(null, null), DataInternal.CheckArgs(nulled, "m", Parse("""{"a":"x"}"""), 1, ChordContext.Background), "null is a value");
        }),
        Case("data.args.binds-the-current-version-and-the-accepted-ones-with-their-fingerprints", () =>
        {
            // Authored from args.ts's binds (R6, Q10): upstream's args.test.ts does not call it.
            Check(DataInternal.Binds(ArgsProvider, 3, ArgsNotesV3.Fingerprint), "the current version");
            Check(DataInternal.Binds(ArgsProvider, 1, ArgsNotesV1.Fingerprint) && DataInternal.Binds(ArgsProvider, 2, ArgsNotesV2.Fingerprint), "the accepted versions");
            Check(!DataInternal.Binds(ArgsProvider, 3, ArgsNotesV2.Fingerprint), "the current version with another fingerprint");
            Check(!DataInternal.Binds(ArgsProvider, 1, ArgsNotesV2.Fingerprint), "an accepted version with another fingerprint");
            Check(!DataInternal.Binds(DataInternal.BindProvider(ArgsNotesV3, new AcceptsTable()), 1, ArgsNotesV1.Fingerprint), "a version not accepted");
        }),
        Case("data.args.memory-a-dropped-binding-releases-its-dispatch-table-mappings-schemas-and-validators-after-the-last-call", async () =>
            await ExpectCollected(await ArgsDroppedBinding())),
    ];

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> ArgsDroppedBinding()
    {
        var contract = Contract.Evolve(Contract.Local("gc", new()
        {
            Version = 1,
            Params = new(),
            Members = new() { ["m"] = Method(Obj(("a", Str()))) },
        }), e => e.Args("m", Obj(("a", Str()), ("b", Str()))));
        var filler = "";
        Func<object?, object?> mapping = old =>
        {
            var current = ((JsonNode)old!).DeepClone().AsObject();
            current["b"] = filler;
            return current;
        };
        var bound = DataInternal.BindProvider(contract, new() { [1] = new() { Methods = { ["m"] = new MethodMapping { Args = mapping } } } });
        await Task.FromResult(DataInternal.CheckArgs(bound, "m", Parse("""{"a":"x"}"""), 1, ChordContext.Background));
        return [new WeakReference(contract), new WeakReference(bound), new WeakReference(mapping)];
    }
}
