using PiSharp.Chord;
using PiSharp.Chord.Store;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/store/archive.test.ts. Every case is ported; the it.each
// rows of "refuses %s" are a case each. checkPart returns the part it checked (toBe: the same object); its refusal is a ChordError
// invalid/args, matched on its message as toThrow(message) does.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> StoreArchiveCases()
    {
        var context = ChordContext.Background;
        var refusals = new (string Case, string Part)[]
        {
            ("an-unknown-kind", """{ "kind": "blob" }"""),
            ("a-missing-field", """{ "kind": "end" }"""),
            ("an-extra-field", """{ "kind": "end", "writes": 0, "more": 1 }"""),
            ("a-negative-count", """{ "kind": "end", "writes": -1 }"""),
            ("a-column-of-an-unknown-type",
                """{ "kind": "table", "table": "a:b:c", "version": 1, "state": "ready", "columns": { "a": { "type": "date" } }, "key": ["a"], "fingerprint": "f" }"""),
            ("a-put-at-version-0", """{ "kind": "put", "table": "a:b:c", "row": {}, "version": 0, "columns": {} }"""),
            ("a-record-without-an-id",
                """{ "kind": "document.create", "record": { "namespace": "n" }, "base": { "version": 1, "fingerprint": "f", "value": {} } }"""),
        };
        return
        [
            Case("store.archive.the-archive-part-schema-takes-every-part-an-export-yields", async () =>
            {
                await using var store = await ArchiveStore();
                var parts = await Exported(store);
                for (var position = 0; position < parts.Count; position++)
                    Check(ReferenceEquals(parts[position], StoreInternal.CheckPart(parts[position], position, context)), $"part {position} is returned");
            }),
            .. refusals.Select(row => Case($"store.archive.the-archive-part-schema-refuses-{row.Case}", () =>
                StoreArgs(() => StoreInternal.CheckPart(StoreValue(row.Part), 4, context), "import: part 4: not an archive part", row.Case))),
        ];
    }
}
