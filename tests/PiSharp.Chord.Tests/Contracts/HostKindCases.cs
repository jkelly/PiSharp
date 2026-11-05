using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using static ChordSupport;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/contracts/host-kind.test.ts. Adaptations: a kind's call
// (`Session({ id: "S1" })`) is its Address method, and toEqual on an address compares its kind, params and key. Upstream's opt() params
// field is a @ts-expect-error and a declaration-time refusal; a C# params field is a Property, so the refusal runs when the kind is
// declared. "check an address's params where it is routed" (checkAddress, isChordError) comes with area cv2-data; the evolve half of
// "hostAddress is not part of the fingerprint" with area cv2-evolve; "hostAddress is typed by the contract's params" is a type-level check
// with no C# counterpart (a host address gets the params as a JsonObject).
internal static partial class Program
{
    private static readonly HostKind HostKindShard = new("shard", new() { ["region"] = OneOf("eu", "us"), ["n"] = Int(min: 0) });

    private static IEnumerable<(string, Func<Task>)> ContractsHostKindCases() =>
    [
        Case("contracts.host-kind.make-addresses-the-kind-the-params-and-a-key-built-like-an-identity-key", () =>
        {
            var server = Server.Address();
            Equal("server", server.Kind, "server kind");
            Json("{}", server.Params, "server params");
            Equal("server", server.Key, "server key");
            var session = Session.Address(new JsonObject { ["id"] = "S1" });
            Equal("session", session.Kind, "session kind");
            Json("""{"id":"S1"}""", session.Params, "session params");
            Equal("session|S1", session.Key, "session key");
            Equal("shard|eu|3", HostKindShard.Address(("region", "eu"), ("n", 3)).Key, "shard key");
            // Escaped like identity keys: two different param lists never make one key.
            Check(Session.Address(("id", "a|b")).Key != HostKindShard.Address(("region", "a"), ("n", "b")).Key, "escaped keys stay apart");
            Equal("session|a\\|b", Session.Address(("id", "a|b")).Key, "an escaped key");
            // Two addresses are equal when their keys are.
            Check(Session.Address(("id", "S1")).Equals(session) && !Session.Address(("id", "S2")).Equals(session), "equality by key");
        }),
        Case("contracts.host-kind.follow-the-params-policy-of-contracts-at-declaration", () =>
        {
            Refuses(() => _ = new HostKind("k", new() { ["id"] = Opt(Str()) }), "k.params.id: a param is");
            Refuses(() => _ = new HostKind(""), "A host kind's name \"\": expected lowercase [a-z0-9@/._~-]");
            Refuses(() => _ = new HostKind("Session"), "expected lowercase");
            Refuses(() => _ = new HostKind("a:b"), "expected lowercase");
            Refuses(() => _ = new HostKind("client"), "\"client\" is reserved for hosts without an address");
            Equal("@pi/session.v2_~-", new HostKind("@pi/session.v2_~-").Kind, "every name character");
        }),
        Case("contracts.host-kind.keep-their-params-data-only-while-the-kind-is-referenced", async () =>
        {
            var references = Weakly(() =>
            {
                var temp = new HostKind("temp", new() { ["id"] = Str() });
                var data = ContractsInternal.KindParams(temp);
                return [temp, data];
            });
            await ExpectCollected(references);
        }),
        Case("contracts.host-kind.refuse-a-copy-of-a-kind", () =>
        {
            // Authored: upstream's kindParams refuses an object that is no declared kind (its WeakMap has none); here, a copy of one.
            var copy = (HostKind)typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(Session, null)!;
            Refuses(() => ContractsInternal.KindParams(copy), "session: not a declared host kind");
            Refuses(() => copy.Address(("id", "S1")), "session: not a declared host kind", "a copy makes no address");
            Equal("session|S1", Session.Address(("id", "S1")).Key, "the declared kind still does");
        }),
        Case("contracts.host-kind.host-address-is-not-part-of-the-fingerprint", () =>
        {
            Func<JsonObject, Address> hostAddress = values => Session.Address(("id", values["session"]));
            OrderedDictionary<string, VariantDeclaration> Variants() => new() { ["v"] = new() { State = { ["n"] = Int() } } };
            var onSession = Contract.Remote("app.placed", new() { Version = 1, Params = new() { ["session"] = Str() }, HostAddress = hostAddress, Variants = Variants() });
            var onServer = Contract.Remote("app.placed", new() { Version = 1, Params = new() { ["session"] = Str() }, HostAddress = _ => Server.Address(), Variants = Variants() });
            Equal(onSession.Fingerprint, onServer.Fingerprint, "the host address is not in the fingerprint");
            Check(ReferenceEquals(hostAddress, ContractsInternal.DataOf(onSession).HostAddress), "the contract keeps its host address");
            // The placed notes of support.ts compute their worker's address from their params.
            Equal("session|S9", ContractsInternal.DataOf(PlacedNotes).HostAddress!(new JsonObject { ["session"] = "S9" }).Key, "PlacedNotes' host address");
        }),
    ];
}
