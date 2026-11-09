using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Store;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/schema/proofs.ts, the values the schema switch must keep
// (recorded.json, made with the TypeBox helpers). Complete fingerprints() includes contracts, documents, tables and jobs.
// CorpusCases.cs completes storedReads, migrating tables and resuming stored jobs through a real native host.
internal static class SchemaProofs
{
    /// <summary>All fingerprints and normal forms of the declarations, in recorded.json's shape.</summary>
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
        ["tables"] = new JsonObject
        {
            ["rows"] = TableFingerprint(SchemaDeclarations.Rows),
            ["editsV1"] = TableFingerprint(SchemaDeclarations.EditsV1),
            ["editsV2"] = TableFingerprint(SchemaDeclarations.EditsV2),
            ["editsV3"] = TableFingerprint(SchemaDeclarations.EditsV3),
        },
        ["job"] = SchemaDeclarations.ImportJob.Fingerprint,
    };

    private static JsonObject TableFingerprint(TableDefinition table) => new()
    {
        ["fingerprint"] = table.Fingerprint,
        ["record"] = Entries(table.Record.Select(entry => (entry.Key, (JsonNode)entry.Value.ToJson()))),
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
