using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/schema/schema-switch.test.ts, the tests of the schema switch
// from TypeBox to pi-schema: the same declarations give byte-identical fingerprints and normal forms, and data stored by the code before
// the switch loads after it. recorded.json and stored/ (store.db, archive.jsonl) are upstream's files, read as test input, not captures:
// Fixtures/schema holds them byte for byte (their own .gitattributes keeps git from touching their line endings), and the first case pins
// their git blob ids at the pinned commit. The fingerprint case compares the contracts and the documents (the profile's fingerprint and
// normal form, the notes' and importState's fingerprints); tables' fingerprints, the job's and "stored data" (storedReads: a host reading
// stored/store.db and an import of stored/archive.jsonl) wait for the layers they need (Proofs.cs). write-stored.ts, which wrote stored/ with the TypeBox helpers, is not ported: the fixtures
// are kept as upstream wrote them and never rewritten (decision 0013).
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
    ];
}
