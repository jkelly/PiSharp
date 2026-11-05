using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Store;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/store/jsonl.test.ts. Every case is ported. Upstream's chunks
// are Uint8Array subarrays and its parts typed ArchivePart literals; here byte arrays (ReadOnlyMemory<byte>) and the parts' JSON, compared
// as JSON (toEqual). JSON.stringify of a part is ChordJson.Stringify.
internal static partial class Program
{
    /// <summary>upstream <c>PARTS</c>: a manifest, a put with text of 2, 3 and 4 UTF-8 bytes per character, the end.</summary>
    private static JsonObject[] StoreJsonlParts() =>
    [
        (JsonObject)StoreValue("""{ "kind": "manifest", "format": 1, "schema": 1, "lastSeq": 3, "nextId": 2 }"""),
        (JsonObject)StoreValue("""{ "kind": "put", "table": "app.store:edits", "row": { "id": "ü€😀", "n": 1 }, "version": 1, "columns": {} }"""),
        (JsonObject)StoreValue("""{ "kind": "end", "writes": 1 }"""),
    ];

    /// <summary>upstream <c>from</c>: <paramref name="values"/> as an async iterable.</summary>
    private static async IAsyncEnumerable<T> StoreJsonlFrom<T>(IEnumerable<T> values)
    {
        foreach (var value in values)
        {
            await Task.Yield();
            yield return value;
        }
    }

    /// <summary>upstream <c>collect</c>.</summary>
    private static async Task<List<T>> StoreJsonlCollect<T>(IAsyncEnumerable<T> values)
    {
        var all = new List<T>();
        await foreach (var value in values) all.Add(value);
        return all;
    }

    /// <summary><paramref name="text"/> as one chunk of UTF-8 (upstream's <c>new TextEncoder().encode(text)</c>).</summary>
    private static IAsyncEnumerable<ReadOnlyMemory<byte>> StoreJsonlBytes(string text) => StoreJsonlFrom<ReadOnlyMemory<byte>>([Encoding.UTF8.GetBytes(text)]);

    private static IEnumerable<(string, Func<Task>)> StoreJsonlCases() =>
    [
        Case("store.jsonl.jsonl-encodes-one-part-per-line-and-decodes-it-back-from-chunks-split-anywhere", async () =>
        {
            var parts = StoreJsonlParts();
            var bytes = (await StoreJsonlCollect(Tables.EncodeJsonl(StoreJsonlFrom(parts)))).SelectMany(chunk => chunk.ToArray()).ToArray();
            Equal(parts.Length + 1, Encoding.UTF8.GetString(bytes).Split('\n').Length, "the lines");
            foreach (var size in new[] { 1, 2, 3, 7, bytes.Length })
            {
                var chunks = new List<ReadOnlyMemory<byte>>();
                for (var at = 0; at < bytes.Length; at += size) chunks.Add(bytes.AsMemory(at, Math.Min(size, bytes.Length - at)));
                SameParts(parts, await StoreJsonlCollect(Tables.DecodeJsonl(StoreJsonlFrom(chunks))), $"chunks of {size}");
            }
        }),
        Case("store.jsonl.jsonl-skips-blank-lines-and-takes-a-last-line-without-a-newline", async () =>
        {
            var parts = StoreJsonlParts();
            var text = $"\n{ChordJson.Stringify(parts[0])}\n\n{ChordJson.Stringify(parts[2])}";
            SameParts([parts[0], parts[2]], await StoreJsonlCollect(Tables.DecodeJsonl(StoreJsonlBytes(text))), "the parts");
        }),
        Case("store.jsonl.jsonl-refuses-a-line-that-is-no-json-naming-it", async () =>
        {
            var text = $"{ChordJson.Stringify(StoreJsonlParts()[0])}\n{{nope\n";
            var rejection = await StoreRejection(StoreJsonlCollect(Tables.DecodeJsonl(StoreJsonlBytes(text))));
            Rejected(rejection, "invalid", "args", "a line that is no JSON");
            Equal("jsonl: line 2 is not JSON", StoreText(rejection), "the message");
        }),
        Case("store.jsonl.jsonl-refuses-bytes-that-are-no-utf-8", async () =>
        {
            var rejection = await StoreRejection(StoreJsonlCollect(Tables.DecodeJsonl(StoreJsonlFrom<ReadOnlyMemory<byte>>([new byte[] { 0x7b, 0xff, 0x0a }]))));
            Rejected(rejection, "invalid", null, "bytes that are no UTF-8");
            Equal("jsonl: line 1 is not UTF-8", StoreText(rejection), "the message");
        }),
    ];
}
