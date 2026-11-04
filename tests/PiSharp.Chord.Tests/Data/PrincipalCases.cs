using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Data;
using static ChordSupport;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/data/principal.test.ts. A principal is a JsonObject; a custom
// key function is a PrincipalKeyFunction. The GC cases make their functions capture a local, so each is a fresh delegate the compiler does
// not cache, like the arrow function upstream makes per call.
internal static partial class Program
{
    private static JsonObject PrincipalUser(string id = "u1") => new() { ["kind"] = "user", ["id"] = id };

    private static IEnumerable<(string, Func<Task>)> DataPrincipalCases() =>
    [
        Case("data.principal.canonical-json-sorts-object-keys-and-writes-minus-zero-as-zero", () =>
        {
            var value = new JsonObject { ["b"] = 1, ["a"] = new JsonArray(new JsonObject { ["d"] = -0.0, ["c"] = null }) };
            Equal("""{"a":[{"c":null,"d":0}],"b":1}""", DataInternal.CanonicalJson(value, ChordContext.Background), "canonicalJson of an object");
            Equal("\"x\"", DataInternal.CanonicalJson("x", ChordContext.Background), "canonicalJson of a string");
        }),
        Case("data.principal.canonical-json-counts-one-walk", async () =>
            Equal(new Counts(1, 0, 0), await Counted(context => DataInternal.CanonicalJson(Parse("""{"a":[1,2]}"""), context)), "the counts")),
        Case("data.principal.principal-key-defaults-to-the-canonical-json-whatever-the-property-order", () =>
        {
            Equal("""{"id":"u1","kind":"user"}""", DataInternal.PrincipalKey(PrincipalUser(), ChordContext.Background), "the default key");
            Equal(DataInternal.PrincipalKey(new JsonObject { ["id"] = "u1", ["kind"] = "user" }, ChordContext.Background),
                DataInternal.PrincipalKey(PrincipalUser(), ChordContext.Background), "whatever the property order");
        }),
        Case("data.principal.principal-key-uses-a-custom-key-function-when-given", () =>
        {
            PrincipalKeyFunction byId = principal => $"{principal["kind"]!.GetValue<string>()}:{principal["id"]!.GetValue<string>()}";
            Equal("user:u1", DataInternal.PrincipalKey(PrincipalUser(), ChordContext.Background, byId), "the custom key");
        }),
        Case("data.principal.principal-key-caches-per-principal-object-no-second-walk", async () =>
        {
            var principal = PrincipalUser();
            var first = await Counted(context => DataInternal.PrincipalKey(principal, context));
            var second = await Counted(context => DataInternal.PrincipalKey(principal, context));
            Sequence([1.0, 0.0], [first.Walks, second.Walks], "the walks");
        }),
        Case("data.principal.principal-key-caches-custom-keys-per-function", () =>
        {
            var principal = PrincipalUser();
            var calls = 0;
            PrincipalKeyFunction custom = _ => $"key{++calls}";
            Sequence(["key1", "key1"], [DataInternal.PrincipalKey(principal, ChordContext.Background, custom),
                DataInternal.PrincipalKey(principal, ChordContext.Background, custom)], "the custom key, once");
            Equal("other", DataInternal.PrincipalKey(principal, ChordContext.Background, _ => "other"), "another function");
            Equal("""{"id":"u1","kind":"user"}""", DataInternal.PrincipalKey(principal, ChordContext.Background), "the default key");
        }),
        Case("data.principal.principal-key-never-retains-a-dropped-custom-key-function-while-its-principal-lives", async () =>
        {
            var principal = PrincipalUser();
            var references = PrincipalDroppedFunction(principal);
            await ExpectCollected(references);
            Check(DataInternal.PrincipalKey(principal, ChordContext.Background) is not null, "the principal still has its key");
            GC.KeepAlive(principal);
        }),
        Case("data.principal.principal-key-does-not-retain-dropped-principals", async () => await ExpectCollected(Weakly(() =>
        {
            var principal = PrincipalUser("gone");
            var key = "k";
            DataInternal.PrincipalKey(principal, ChordContext.Background);
            DataInternal.PrincipalKey(principal, ChordContext.Background, _ => key);
            return [principal];
        }))),
    ];

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] PrincipalDroppedFunction(JsonObject principal)
    {
        var key = "k";
        PrincipalKeyFunction custom = _ => key;
        DataInternal.PrincipalKey(principal, ChordContext.Background, custom);
        return [new WeakReference(custom)];
    }
}
