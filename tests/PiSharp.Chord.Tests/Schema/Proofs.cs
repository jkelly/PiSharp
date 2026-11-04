using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/schema/proofs.ts, the values the schema switch must keep
// (recorded.json, made with the TypeBox helpers). Ported here: fingerprints()'s contracts and documents (the profile's fingerprint and
// normal form, snapshotSchema of `everything`, and the notes' and importState's fingerprints; the documents from area cv2-documents-core).
// The rest needs layers later areas bring: tables (TableDefinition fingerprints and records; the store-tables area), job (JobDefinition's
// fingerprint; jobs, phase 6), and storedReads, which opens stored/store.db and imports stored/archive.jsonl into a host serving the owner
// (owner.ts: createHost, defineFacet, the store engine and its archive; phase 6 and the store areas).
internal static class SchemaProofs
{
    /// <summary>The fingerprints and normal forms of the declarations ported so far, in recorded.json's shape.</summary>
    internal static JsonObject Fingerprints() => new()
    {
        ["contracts"] = new JsonObject
        {
            ["everything"] = ContractFingerprints(SchemaDeclarations.Everything),
            ["account"] = ContractFingerprints(SchemaDeclarations.Account),
            ["owner"] = ContractFingerprints(SchemaDeclarations.Owner),
        },
        ["documents"] = new JsonObject
        {
            ["profile"] = new JsonObject
            {
                ["fingerprint"] = SchemaDeclarations.Profile.Fingerprint,
                ["normal"] = ContractsInternal.SnapshotSchema(SchemaDeclarations.EverythingSchema(), "p").Normal.DeepClone(),
            },
            ["notes"] = SchemaDeclarations.Notes.Fingerprint,
            ["importState"] = SchemaDeclarations.ImportState.Fingerprint,
        },
    };

    /// <summary>A contract's params normal form and, per version, its fingerprint and each variant's state, keyed and method normal forms.</summary>
    private static JsonObject ContractFingerprints(Contract contract)
    {
        var data = ContractsInternal.DataOf(contract);
        var versions = new JsonArray();
        foreach (var version in data.Versions)
        {
            var variants = new JsonObject();
            foreach (var (name, variant) in version.Variants)
            {
                variants[name] = new JsonObject
                {
                    ["state"] = Normals(variant.State),
                    ["keyed"] = Normals(variant.Keyed),
                    ["methods"] = Entries(variant.Methods.Select(entry => (entry.Key, (JsonNode)new JsonObject
                    {
                        ["args"] = entry.Value.Args.Normal.DeepClone(),
                        ["result"] = entry.Value.Result.Normal.DeepClone(),
                    }))),
                };
            }
            versions.Add(new JsonObject { ["fingerprint"] = version.Fingerprint, ["variants"] = variants });
        }
        return new JsonObject { ["params"] = data.Params.Normal.DeepClone(), ["versions"] = versions };
    }

    private static JsonObject Normals(IReadOnlyDictionary<string, Snapshot> members) =>
        Entries(members.Select(entry => (entry.Key, entry.Value.Normal.DeepClone())));

    private static JsonObject Entries(IEnumerable<(string Key, JsonNode Value)> entries)
    {
        var result = new JsonObject();
        foreach (var (key, value) in entries) result[key] = value;
        return result;
    }
}
