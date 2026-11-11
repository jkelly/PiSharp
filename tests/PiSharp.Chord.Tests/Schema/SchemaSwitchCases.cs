using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Store;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/schema/schema-switch.test.ts, the tests of the schema switch
// from TypeBox to pi-schema: the same declarations give byte-identical fingerprints and normal forms, and data stored by the code before
// the switch loads after it. recorded.json and stored/ (store.db, archive.jsonl) are upstream's files, read as test input, not captures:
// Fixtures/schema holds them byte for byte (their own .gitattributes keeps git from touching their line endings), and the first case pins
// their git blob ids at the pinned commit. The fingerprint case compares the contracts and the documents (the profile's fingerprint and
// normal form, the notes' and importState's fingerprints); tables' fingerprints, the job's and "stored data" (storedReads: a host
// reading stored/store.db and an import of stored/archive.jsonl) wait for the layers they need (Proofs.cs). write-stored.ts, which wrote
// stored/ with the TypeBox helpers, is not ported: the fixtures are kept as upstream wrote them and never rewritten (decision 0013). Two
// PiSharp cases (area cv2-store-archive) read stored/ without createHost, through the store alone: archive.jsonl decodes, imports and
// exports its payload byte for byte with the authored schema-3 manifest overlay below; a copy of store.db opens and exports what archive.jsonl holds, but for the host's close that
// write-stored.ts ran after the export (an authored expectation, read off the fixtures: one commit at seq 14 stores each job run stopped,
// a new base at revision 3).
internal static partial class Program
{
    /// <summary>A file of the schema switch's fixtures.</summary>
    private static string SchemaFixture(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx")))
                return Path.Combine([directory.FullName, "tests", "PiSharp.Chord.Tests", "Fixtures", "schema", .. parts]);
        throw new InvalidOperationException("PiSharp.slnx not found above " + AppContext.BaseDirectory);
    }

    /// <summary>git's blob id of a file's bytes: SHA-1 of <c>blob &lt;length&gt;\0</c> and the bytes.</summary>
    private static string SchemaBlobId(byte[] bytes) =>
        Convert.ToHexStringLower(SHA1.HashData([.. Encoding.ASCII.GetBytes($"blob {bytes.Length}\0"), .. bytes]));

    private static JsonNode SchemaRecorded() => JsonNode.Parse(File.ReadAllText(SchemaFixture("recorded.json")))!;

    /// <summary>A fixture's bytes in chunks, as a file stream reads them (upstream's createReadStream).</summary>
    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> SchemaFileChunks(string path)
    {
        await using var file = File.OpenRead(path);
        var buffer = new byte[4096];
        int read;
        while ((read = await file.ReadAsync(buffer)) > 0) yield return buffer.AsMemory(0, read).ToArray();
    }

    /// <summary>stored/archive.jsonl's parts.</summary>
    private static async Task<List<JsonNode?>> SchemaStoredArchive()
    {
        var parts = new List<JsonNode?>();
        await foreach (var part in Tables.DecodeJsonl(SchemaFileChunks(SchemaFixture("stored", "archive.jsonl")))) parts.Add(part);
        return parts;
    }

    /// <summary>The sole archive change caused by opening historical schema-2 data with the schema-3 store. The captures stay
    /// untouched: old records have no host ownership to infer, and every payload part must remain exact.</summary>
    private static List<JsonNode?> Schema3ArchiveExpectation(IEnumerable<JsonNode?> historical)
    {
        Equal(3L, StoreInternal.StoreSchema, "authored schema-3 archive overlay target");
        var expected = historical.Select(part => part?.DeepClone()).ToList();
        Check(expected.Count > 0 && (string?)expected[0]?["kind"] == "manifest", "historical archive starts with its manifest");
        Equal(2L, (long)expected[0]!["schema"]!, "captured historical schema remains 2");
        Check(expected.Where(part => (string?)part?["kind"] == "document.create")
            .All(part => !part!["record"]!.AsObject().ContainsKey("host")), "historical documents have no host assignment");
        expected[0]!["schema"] = 3;
        return expected;
    }

    private static IEnumerable<(string, Func<Task>)> SchemaSchemaSwitchCases() =>
    [
        Case("schema.schema-switch.fixtures-are-upstreams-files-byte-for-byte", () =>
        {
            // The blob ids of packages/chord/test/next/schema/{recorded.json,stored/archive.jsonl,stored/store.db} at the pinned commit.
            foreach (var (file, blob) in new[]
            {
                (new[] { "recorded.json" }, "00c0a3588fb5d21085af0c25c6147a08251150fe"),
                (new[] { "stored", "archive.jsonl" }, "7fcbd5972a9804f4852d178b8f133ecf57194670"),
                (new[] { "stored", "store.db" }, "f9b1e70836f843f0967cdb76f7890de36f441247"),
            })
                Equal(blob, SchemaBlobId(File.ReadAllBytes(SchemaFixture(file))), string.Join("/", file));
        }),
        Case("schema.schema-switch.keeps-every-fingerprint-and-normal-form-of-contracts", () =>
        {
            // fingerprints() toEqual recorded.fingerprints, for the parts ported so far: the contracts and the documents.
            var recorded = SchemaRecorded()["fingerprints"]!;
            var fingerprints = SchemaProofs.Fingerprints();
            Json(recorded["contracts"]!.ToJsonString(), fingerprints["contracts"], "the contracts");
            Json(recorded["documents"]!.ToJsonString(), fingerprints["documents"], "the documents");
            Sequence(["everything", "account", "owner"], Keys(recorded["contracts"]), "recorded.json's contracts");
            Sequence(["profile", "notes", "importState"], Keys(recorded["documents"]), "recorded.json's documents");
        }),
        Case("schema.schema-switch.stored-data-pisharp-the-stored-archive-imports-and-exports-back-byte-for-byte", async () =>
        {
            var context = ChordContext.Background;
            var bytes = await File.ReadAllBytesAsync(SchemaFixture("stored", "archive.jsonl"));
            var historical = await SchemaStoredArchive();
            var canonical = Encoding.UTF8.GetBytes(string.Concat(historical.Select(part => ChordJson.Stringify(part) + "\n")));
            Check(bytes.AsSpan().SequenceEqual(canonical), "historical capture remains canonical, unchanged JSONL bytes");
            var expected = Encoding.UTF8.GetBytes(string.Concat(Schema3ArchiveExpectation(historical).Select(part => ChordJson.Stringify(part) + "\n")));
            await using var store = (ChordStore)await Tables.ImportStoreAsync(Tables.SqliteBackend(new TestSqliteDatabase(":memory:")),
                Tables.DecodeJsonl(SchemaFileChunks(SchemaFixture("stored", "archive.jsonl"))), context);
            Sequence(new[] { 13L, 12L }, new[] { store.LastSeq, store.NextId }, "the manifest's lastSeq and nextId");
            var written = new List<byte>();
            await foreach (var chunk in Tables.EncodeJsonl(store.Export(context))) written.AddRange(chunk.ToArray());
            Check(expected.AsSpan().SequenceEqual(written.ToArray()), $"the export changes only archive.jsonl's schema to 3: {expected.Length} expected bytes, {written.Count} actual bytes, " +
                $"first difference at {expected.Zip(written).TakeWhile(pair => pair.First == pair.Second).Count()}");
        }),
        Case("schema.schema-switch.stored-data-pisharp-a-copy-of-the-stored-store-opens-and-exports-the-archive-after-the-host-s-close", async () =>
        {
            var context = ChordContext.Background;
            var directory = Directory.CreateTempSubdirectory("chord-schema-switch-");
            try
            {
                var path = Path.Combine(directory.FullName, "store.db");
                File.Copy(SchemaFixture("stored", "store.db"), path);
                List<JsonObject> parts;
                await using (var store = await StoreOver(new TestSqliteDatabase(path))) parts = await Exported(store);
                // The archive, then the host's close (authored, read off the fixtures): one more commit, at seq 14, and each job run's
                // record at revision 3 with a new base there, its phase stopped.
                var expected = Schema3ArchiveExpectation(await SchemaStoredArchive());
                expected[0]!["lastSeq"] = 14;
                var runs = new HashSet<long>();
                foreach (var part in expected)
                {
                    if ((string)part!["kind"]! == "document.create" && (string)part["record"]!["namespace"]! == "chord.jobs")
                    {
                        part["record"]!["revision"] = 3;
                        runs.Add((long)part["record"]!["id"]!);
                    }
                    else if ((string)part["kind"]! == "document.revision" && runs.Contains((long)part["id"]!))
                    {
                        part["seq"] = 14;
                        part["content"]!["value"]!["phase"] = "stopped";
                    }
                }
                Sequence(new[] { 4L, 7L, 10L }, runs.Order(), "the job runs");
                SameParts(expected, parts, "store.db's export");
            }
            finally
            {
                directory.Delete(recursive: true);
            }
        }),
    ];
}
