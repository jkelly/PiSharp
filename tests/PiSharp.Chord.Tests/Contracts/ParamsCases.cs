using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using static ChordSupport;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/contracts/params.test.ts. Upstream refuses each field "in the
// types and at declaration"; the type-level half (@ts-expect-error) has no C# counterpart, because a C# params field is a Property, so
// each refusal runs when the contract is declared, with upstream's message.
internal static partial class Program
{
    private static Contract ParamsRemote(OrderedDictionary<string, Property> @params) => Contract.Remote("n", new()
    {
        Version = 1,
        Params = @params,
        HostAddress = _ => Server.Address(),
        Variants = new() { ["a"] = new() { State = { ["s"] = Str() } } },
    });

    private static IEnumerable<(string, Func<Task>)> ContractsParamsCases() =>
    [
        Case("contracts.params.accepts-required-str-int-bool-and-one-of-of-strings-with-constraints", () =>
            Accepts(() => ParamsRemote(new()
            {
                ["a"] = Str(min: 1, max: 9, pattern: "^x"),
                ["b"] = Int(min: 0),
                ["c"] = Bool(),
                ["d"] = OneOf("chat", "agent"),
            }), "the params policy's fields")),
        Case("contracts.params.refuses-optional-fields-at-declaration", () =>
            Refuses(() => ParamsRemote(new() { ["a"] = Opt(Str()) }), "n.params.a: a param is a required str(), int(), bool() or oneOf(...) of strings")),
        .. new (string Case, JsonObject Schema)[]
        {
            ("a-number", Num()),
            ("nested-objects", Obj(("a", Str()))),
            ("arrays", Arr(Str())),
            ("bytes", Bytes()),
            ("numeric-literals", OneOf(1, 2)),
            ("defaults", Str(@default: "x")),
        }.Select(row => Case($"contracts.params.refuses-{row.Case}-at-declaration", () => Refuses(() => ParamsRemote(new() { ["p"] = row.Schema }), "n.params.p: a param is"))),
        Case("contracts.params.keep-identity-order-and-a-closed-object-schema", () =>
        {
            // Authored from params.ts: the fields in declared order, the closed object schema checked on every get, and the
            // [field, normal form] pairs the fingerprint hashes.
            var data = ContractsInternal.DataOf(ParamsRemote(new() { ["b"] = Int(min: 0), ["a"] = Str() })).Params;
            Sequence(["b", "a"], data.Fields, "fields in identity order");
            Json("""{"type":"object","properties":{"b":{"minimum":0,"type":"integer"},"a":{"type":"string"}},"required":["b","a"],"additionalProperties":false}""",
                data.Schema, "the closed object schema");
            Equal("""[["b",{"minimum":0,"type":"integer"}],["a",{"type":"string"}]]""", PiSharp.Chord.ChordJson.Stringify(data.Normal), "the normal pairs");
            Equal("a|b\\\\c|d\\|e", ContractsInternal.JoinKey(["a", "b\\c", "d|e"]), "joinKey escapes \\ and |");
        }),
    ];
}
