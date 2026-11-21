using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Data;
using static ChordSupport;

// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/test/data/principal.test.ts, the principalJson cases 1.6.0 added
// (decision "References share one wire hold": the caller part of the sharing key). data/failure.ts gained the cause kind `denied` with no
// test of its own in test/data; the authored case (data.failure.160-*) pins it where the data layer compares and names causes.
internal static partial class Program
{
    private static JsonObject Sync160User(string id = "u1") => new() { ["kind"] = "user", ["id"] = id };

    private static IEnumerable<(string, Func<Task>)> Sync160DataCases() =>
    [
        Case("data.principal.principal-json-is-the-principals-own-json-its-fields-in-insertion-order-equal-only-when-they-serialize-equally", () =>
        {
            Equal("""{"kind":"user","id":"u1"}""", DataInternal.PrincipalJson(Sync160User(), ChordContext.Background), "the principal's own JSON");
            Check(DataInternal.PrincipalJson(new JsonObject { ["id"] = "u1", ["kind"] = "user" }, ChordContext.Background) !=
                DataInternal.PrincipalJson(Sync160User(), ChordContext.Background), "another field order is another text");
            Equal(DataInternal.PrincipalJson(Sync160User(), ChordContext.Background), DataInternal.PrincipalJson(Sync160User(), ChordContext.Background),
                "equal principals give the same text");
            // JSON.stringify's text, as a TypeScript host writes it: no spaces, -0 as 0, non-ASCII as it is.
            Equal("""{"kind":"user","id":"ü","n":0,"tags":["a",null,true]}""", DataInternal.PrincipalJson(
                new JsonObject { ["kind"] = "user", ["id"] = "ü", ["n"] = -0.0, ["tags"] = new JsonArray("a", null, true) }, ChordContext.Background), "JSON.stringify's text");
        }),
        Case("data.principal.principal-json-caches-per-principal-object-one-walk-per-object-cost-guard", async () =>
        {
            var principal = Sync160User();
            var first = await Counted(context => DataInternal.PrincipalJson(principal, context));
            var second = await Counted(context => DataInternal.PrincipalJson(principal, context));
            var other = await Counted(context => DataInternal.PrincipalJson(Sync160User(), context));
            Sequence([1.0, 0.0, 1.0], [first.Walks, second.Walks, other.Walks], "the walks");
            // Its cache is its own: the principal's key still walks once.
            var key = await Counted(context => DataInternal.PrincipalKey(principal, context));
            Equal(1.0, key.Walks, "the key's walk");
        }),
        Case("data.principal.principal-json-does-not-retain-dropped-principals", async () => await ExpectCollected(Weakly(() =>
        {
            var principal = Sync160User("dropped");
            DataInternal.PrincipalJson(principal, ChordContext.Background);
            return [principal];
        }))),
        Case("data.failure.160-denied-is-a-cause-kind-of-its-own", () =>
        {
            Sequence(["Transient", "Broken", "Waiting", "Denied"], Enum.GetNames<FailureKind>(), "the kinds, in their wire codes' order");
            var error = new ChordError("forbidden", "no access");
            var denied = new FailureCause(FailureKind.Denied, error);
            Check(DataInternal.SameCause(denied, new FailureCause(FailureKind.Denied, new ChordError("forbidden", "no access"))), "the same cause");
            Check(!DataInternal.SameCause(denied, new FailureCause(FailureKind.Waiting, error)), "a waiting cause is another");
            Check(!DataInternal.SameCause(denied, new FailureCause(FailureKind.Broken, error)), "a broken cause is another");
            // An open's failure is never denied: the failing stage decides, and an open is transient or broken.
            Equal(FailureKind.Transient, DataInternal.OpenFailure(error, "open").Kind, "an open's failure");
        }),
    ];
}
