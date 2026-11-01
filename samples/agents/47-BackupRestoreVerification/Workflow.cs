// PiSharp-only (no upstream Pi source): fixture file snapshots and isolated SHA-256 restore verification.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
namespace PiSharp.Samples.BackupRestoreVerification;

public static class Workflow
{
    static JsonObject Schema() => SampleJson.Schema("""{"datasetId":{"type":"string"}}""", "datasetId");
    public static WorkflowSpec Create(SampleContext c) => new("47-BackupRestoreVerification", "Backup restore verification",
        "Call plan_backup, create_snapshot, and verify_restore for each requested dataset. Approval is required to create fixture files, snapshots and isolated restores. These are local test files only. Report actual SHA-256 comparisons, missing files and corruption; never claim production access or recovery.",
        [new("plan_backup", "Validate fixture file inventory and plan isolated local paths", Schema(), (a,t) => Plan(c,a,t)),
         new("create_snapshot", "Create fixture source files and copy a verified local snapshot", Schema(), (a,t) => Snapshot(c,a,t), true),
         new("verify_restore", "Restore snapshot into a new local directory and compare file hashes", Schema(), (a,t) => Restore(c,a,t), true)],
        [new("plan_backup",new() { ["datasetId"] = "orders" }),new("create_snapshot",new() { ["datasetId"] = "orders" }),new("verify_restore",new() { ["datasetId"] = "orders" })]);
    static JsonArray Rows(JsonObject o,string key) => o[key] as JsonArray ?? throw new FormatException("Missing " + key);
    static JsonObject Dataset(SampleContext c,string id)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(id,"^[a-zA-Z0-9_-]+$")) throw new ArgumentException("Invalid dataset ID.");
        return Rows(c.Input,"datasets").OfType<JsonObject>().SingleOrDefault(d => d.Text("id") == id) ?? throw new ArgumentException("Unknown dataset: " + id);
    }
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    static JsonArray Inventory(SampleContext c,string id)
    {
        var files = Rows(Dataset(c,id),"files"); if (files.Count is < 1 or > 50) throw new FormatException("Use 1–50 fixture files."); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var inventory = new JsonArray();
        foreach (var file in files.OfType<JsonObject>())
        {
            var path = file.Text("path").Replace('\\','/');
            if (path.Length == 0 || path.Split('/').Any(p => !System.Text.RegularExpressions.Regex.IsMatch(p,"^[a-zA-Z0-9_-][a-zA-Z0-9_.-]*$") || p is "." or "..") || !names.Add(path)) throw new ArgumentException("Invalid or duplicate fixture file path.");
            if (file["content"] is null || file.Text("content").Length > 100_000) throw new FormatException("Missing or oversized fixture content.");
            _ = c.ResolvePath("sources/" + id + "/" + path); var bytes = Encoding.UTF8.GetBytes(file.Text("content"));
            inventory.Add(new JsonObject { ["path"] = path, ["bytes"] = bytes.Length, ["sha256"] = Hash(bytes) });
        }
        if (inventory.Count != files.Count) throw new FormatException("File entries must be objects."); return inventory;
    }
    static Task<JsonNode?> Plan(SampleContext c,JsonObject a,CancellationToken t)
    {
        t.ThrowIfCancellationRequested(); var id = a.Text("datasetId"); var files = Inventory(c,id); c.State["plan:" + id] = Dataset(c,id).ToJsonString();
        return Task.FromResult<JsonNode?>(new JsonObject { ["datasetId"] = id, ["sourceDirectory"] = "sources/" + id, ["snapshotDirectory"] = "snapshots/" + id, ["files"] = files, ["scope"] = "local fixture only" });
    }
    static async Task<JsonNode?> Snapshot(SampleContext c,JsonObject a,CancellationToken t)
    {
        if (!c.Approved) throw new InvalidOperationException("Approval required to create fixture files and backups."); var id = a.Text("datasetId"); var dataset = Dataset(c,id);
        if (c.State["plan:" + id]?.GetValue<string>() != dataset.ToJsonString()) throw new InvalidOperationException("Plan the current dataset first.");
        var fingerprint = Hash(Encoding.UTF8.GetBytes(dataset.ToJsonString()));
        if (c.State["snapshot:" + id] is JsonObject existing)
        {
            if (existing.Text("fixtureFingerprint") != fingerprint) throw new InvalidOperationException("Dataset changed after snapshot; use a fresh workspace.");
            return existing.DeepClone();
        }
        var inventory = Inventory(c,id);
        foreach (var file in Rows(dataset,"files").OfType<JsonObject>())
        {
            t.ThrowIfCancellationRequested(); var relative = file.Text("path").Replace('\\','/'); var source = c.ResolvePath("sources/" + id + "/" + relative); var snapshot = c.ResolvePath("snapshots/" + id + "/" + relative);
            if (File.Exists(source) || File.Exists(snapshot)) throw new InvalidOperationException("Existing fixture files will not be overwritten; use a fresh workspace.");
        }
        foreach (var file in Rows(dataset,"files").OfType<JsonObject>())
        {
            t.ThrowIfCancellationRequested(); var relative = file.Text("path").Replace('\\','/'); var source = c.ResolvePath("sources/" + id + "/" + relative); var snapshot = c.ResolvePath("snapshots/" + id + "/" + relative);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!); Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!); await File.WriteAllBytesAsync(source,Encoding.UTF8.GetBytes(file.Text("content")),t); File.Copy(source,snapshot,false);
            var expected = inventory.OfType<JsonObject>().Single(f => f.Text("path") == relative).Text("sha256"); if (Hash(await File.ReadAllBytesAsync(snapshot,t)) != expected) throw new IOException("Snapshot hash mismatch.");
        }
        var result = new JsonObject { ["datasetId"] = id, ["fixtureFingerprint"] = fingerprint, ["snapshotDirectory"] = "snapshots/" + id, ["files"] = inventory, ["created"] = true }; c.State["snapshot:" + id] = result.DeepClone(); await c.WriteJsonAsync("backup-manifest-" + id + ".json",result,t); return result;
    }
    static async Task<JsonNode?> Restore(SampleContext c,JsonObject a,CancellationToken t)
    {
        if (!c.Approved) throw new InvalidOperationException("Approval required for isolated restore."); var id = a.Text("datasetId"); _ = Dataset(c,id);
        var manifest = c.State["snapshot:" + id] as JsonObject ?? throw new InvalidOperationException("Create this dataset snapshot first.");
        var attempt = (c.State["restoreCount:" + id]?.GetValue<int>() ?? 0) + 1; c.State["restoreCount:" + id] = attempt;
        var root = "restores/" + id + "/attempt-" + attempt; var results = new JsonArray();
        foreach (var file in Rows(manifest,"files").OfType<JsonObject>())
        {
            t.ThrowIfCancellationRequested(); var relative = file.Text("path"); var source = c.ResolvePath("snapshots/" + id + "/" + relative); var dest = c.ResolvePath(root + "/" + relative); string? actual = null;
            if (File.Exists(source)) { Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(source,dest,false); actual = Hash(await File.ReadAllBytesAsync(dest,t)); }
            results.Add(new JsonObject { ["path"] = relative, ["expectedSha256"] = file.Text("sha256"), ["actualSha256"] = actual, ["status"] = actual is null ? "missing" : actual == file.Text("sha256") ? "verified" : "corrupt" });
        }
        var result = new JsonObject { ["datasetId"] = id, ["restoreDirectory"] = root, ["verified"] = results.OfType<JsonObject>().All(f => f.Text("status") == "verified"), ["files"] = results }; await c.WriteJsonAsync("restore-verification-" + id + ".json",result,t); return result;
    }
}
