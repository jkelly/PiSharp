using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using static ChordSupport;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/contracts/fingerprint.test.ts. Every assertion is ported. The
// pinned fingerprint b3eda86070a7e319 is an authored expectation taken from upstream's test, not a capture: a C# declaration of the
// contract hashes to the value a TypeScript declaration of it does (DeclareCases.cs pins it too, with the schema-switch contracts). A
// schema is a JsonNode, so the raw schemas of the cases are JsonObjects with the same keys in the same order.
internal static partial class Program
{
    private static readonly HostKind FingerprintServer = new("server");

    /// <summary>Fingerprint of a one-variant remote contract with state <c>s</c> and params <paramref name="params"/>.</summary>
    private static string FingerprintOf(JsonNode state, OrderedDictionary<string, Property>? @params = null) =>
        Contract.Remote("n", new()
        {
            Version = 1,
            Params = @params ?? new(),
            HostAddress = _ => FingerprintServer.Address(),
            Variants = new() { ["a"] = new() { State = { ["s"] = state } } },
        }).Fingerprint;

    private static IEnumerable<(string, Func<Task>)> ContractsFingerprintCases() =>
    [
        Case("contracts.fingerprint.pin-the-normal-form-a-fixed-contract-has-a-fixed-hash", () =>
        {
            Equal(2, ContractsInternal.NormalFormVersion, "NORMAL_FORM_VERSION");
            var notes = Contract.Remote("notes", new()
            {
                Version = 1,
                Params = new() { ["session"] = Str() },
                HostAddress = _ => FingerprintServer.Address(),
                Variants = new()
                {
                    ["editor"] = new() { State = { ["notes"] = Obj(("text", Str())) }, Methods = { ["add"] = Method(Obj(("text", Str())), Str()) } },
                    ["reader"] = new() { State = { ["notes"] = Obj(("text", Str())) } },
                },
            });
            Equal("b3eda86070a7e319", notes.Fingerprint, "fingerprint");
        }),
        Case("contracts.fingerprint.are-stable-across-key-order-and-annotations", () =>
        {
            var plain = Parse("""{"type":"object","properties":{"a":{"type":"string"},"b":{"type":"integer"}},"required":["a","b"]}""");
            var reordered = Parse("""
                {"required":["b","a"],"title":"T","properties":{"b":{"type":"integer","description":"d"},"a":{"$comment":"c","type":"string","examples":["x"]}},
                 "type":"object","deprecated":true}
                """);
            Equal(FingerprintOf(plain), FingerprintOf(reordered), "the reordered, annotated schema's fingerprint");
        }),
        .. new (string Name, Func<JsonNode> A, Func<JsonNode> B)[]
        {
            ("defaults", () => Str(), () => Str(@default: "x")),
            ("bounds", () => Str(), () => Str(max: 3)),
            ("formats", () => Str(), () => Parse("""{"type":"string","format":"email"}""")),
            ("union-order", () => Union(Str(), Int()), () => Union(Int(), Str())),
            ("additional-properties", () => Parse("""{"type":"object","properties":{}}"""), () => Obj()),
            ("read-only", () => Str(), () => Parse("""{"type":"string","readOnly":true}""")),
        }.Select(test => Case($"contracts.fingerprint.are-sensitive-to-{test.Name}", () =>
            Check(FingerprintOf(test.A()) != FingerprintOf(test.B()), $"{test.Name}: the fingerprints differ"))),
        Case("contracts.fingerprint.are-sensitive-to-params-order", () =>
            Check(FingerprintOf(Str(), new() { ["a"] = Str(), ["b"] = Int() }) != FingerprintOf(Str(), new() { ["b"] = Int(), ["a"] = Str() }),
                "the params orders' fingerprints differ")),
        Case("contracts.fingerprint.give-the-same-v1-from-a-separate-declaration-and-evolve-keeps-v1s", () =>
        {
            static Contract Declare() => Contract.Remote("n", new()
            {
                Version = 1,
                Params = new(),
                HostAddress = _ => FingerprintServer.Address(),
                Variants = new() { ["a"] = new() { State = { ["s"] = Str() } } },
            });
            var v1 = Declare();
            var v2 = Contract.Evolve(v1, e => e.State("a", "s", Int()));
            Equal(v1.Fingerprint, Declare().Fingerprint, "a separate declaration's fingerprint");
            Check(v2.Fingerprint != v1.Fingerprint, "v2's fingerprint differs from v1's");
            Equal(v2.Fingerprint, Contract.Evolve(Declare(), e => e.State("a", "s", Int())).Fingerprint, "the same evolution's fingerprint");
            Equal(v1.Fingerprint, ContractsInternal.DataOf(v2).Versions[0].Fingerprint, "evolve keeps v1's fingerprint");
        }),
        Case("contracts.fingerprint.differ-between-local-and-remote", () =>
        {
            var asRemote = Contract.Remote("n", new()
            {
                Version = 1,
                Params = new(),
                HostAddress = _ => FingerprintServer.Address(),
                Variants = new() { ["members"] = new() { State = { ["s"] = Str() } } },
            });
            var asLocal = Contract.Local("n", new() { Version = 1, Params = new(), Members = new() { ["s"] = Str() } });
            Check(asLocal.Fingerprint != asRemote.Fingerprint, "local and remote fingerprints differ");
        }),
        Case("contracts.fingerprint.do-not-depend-on-the-name-or-version", () =>
        {
            var a = Contract.Remote("a", new() { Version = 1, Params = new(), HostAddress = _ => FingerprintServer.Address(), Variants = new() { ["v"] = new() } });
            var b = Contract.Remote("b", new() { Version = 7, Params = new(), HostAddress = _ => FingerprintServer.Address(), Variants = new() { ["v"] = new() } });
            Equal(a.Fingerprint, b.Fingerprint, "fingerprints");
        }),
    ];
}
