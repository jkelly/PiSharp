using System.Text.Json.Nodes;
using PiSharp.Chord.Documents;
using PiSharp.Chord.State;
using static ChordSupport;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/documents/handles.test.ts
//
// Document handles (slice 6a §4): keyed handles list keys in code point order, and sealed handles still read but refuse every change and
// retirement released. Upstream serves app.restartable through createHost (restartable, instancesOf; phase 6); here the object is the
// hand-built host's (Documents/DocumentsHost.cs), and fail() is its FailAttempt.
internal static partial class Program
{
    private static readonly StateDefinition HandlesBody = DocumentsDefine("body", Obj(("text", Str())), 1, () => new JsonObject { ["text"] = "" });

    private static readonly StateDefinition HandlesNotes = DocumentsDefine("notes", Obj(("text", Str())), 1, () => new JsonObject { ["text"] = "" }, keyed: true);

    private static IEnumerable<(string, Func<Task>)> DocumentsHandlesCases() =>
    [
        Case("documents.handles.keyed-handles-list-keys-in-code-point-order-not-utf-16-order", async () =>
        {
            IKeyedHandle? notes = null;
            await DocumentsHost.Restartable((_, self) => notes = self.KeyedState(HandlesNotes));
            // U+FF5E sorts before U+1F600 by code point; as UTF-16 its unit 0xFF5E sorts after the surrogate 0xD83D.
            foreach (var key in new[] { "\U0001F600", "～", "b", "a" })
                await notes!.ChangeAsync(key, draft => { draft["text"] = key; }, DocumentsBg);
            Sequence(["a", "b", "～", "\U0001F600"], await DocumentsKeysOf(notes!), "the keys");
            Json("""{"text":"～"}""", await notes!.GetAsync("～", DocumentsBg), "a key's document");
        }),
        Case("documents.handles.sealed-handles-still-read-synchronously-and-from-memory-changes-and-retirements-are-refused-released", async () =>
        {
            ISingletonHandle? body = null;
            IKeyedHandle? notes = null;
            var made = await DocumentsHost.Restartable((_, self) =>
            {
                body = self.State(HandlesBody);
                notes = self.KeyedState(HandlesNotes);
            });
            await body!.ChangeAsync(draft => { draft["text"] = "x"; }, DocumentsBg);
            await notes!.ChangeAsync("k", draft => { draft["text"] = "y"; }, DocumentsBg);
            made.FailAttempt();
            Json("""{"text":"x"}""", body.Value, "the singleton");
            Json("""{"text":"y"}""", await notes.GetAsync("k", DocumentsBg), "the keyed document");
            Sequence(["k"], await DocumentsKeysOf(notes), "the keys");
            const string released = """{"code":"released","reason":"ended","message":"app.restartable(1): ended"}""";
            Json(released, (JsonNode)await Rejection(body.ChangeAsync(_ => { }, DocumentsBg)), "a change");
            Json(released, (JsonNode)await Rejection(body.RetireAsync(DocumentsBg)), "a retirement");
            Json(released, (JsonNode)await Rejection(notes.ChangeAsync("k", _ => { }, DocumentsBg)), "a keyed change");
            Json(released, (JsonNode)await Rejection(notes.RetireAsync("k", DocumentsBg)), "a keyed retirement");
        }),
    ];
}
