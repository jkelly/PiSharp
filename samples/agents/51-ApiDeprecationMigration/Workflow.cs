// PiSharp-only (no upstream Pi source): bounded approved local source migration.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
namespace PiSharp.Samples.ApiDeprecationMigration;

public static class Workflow
{
    public static WorkflowSpec Create(SampleContext c)
    {
        var schema = SampleJson.Schema("{\"migrationId\":{\"type\":\"string\"}}", "migrationId");
        JsonObject Migration(JsonObject a)
        {
            var id = a.Text("migrationId");
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-zA-Z0-9_-]{1,60}$")) throw new ArgumentException("Invalid migration ID.");
            var found = (c.Input["migrations"] as JsonArray ?? throw new ArgumentException("migrations required.")).OfType<JsonObject>().Where(m => m.Text("id") == id).ToArray();
            return found.Length == 1 ? found[0] : throw new ArgumentException("Migration must exist uniquely.");
        }
        string PathFor(string path)
        {
            if (!path.StartsWith("repo/", StringComparison.Ordinal) || path.Split('/').Any(p => p is ".." or "." or "" ) || path.Contains('\\') || Path.GetExtension(path) != ".cs")
                throw new ArgumentException("Only configured repo/*.cs paths are supported.");
            return c.ResolvePath(path);
        }
        string Source(string path)
        {
            var full = PathFor(path);
            if (File.Exists(full))
            {
                if (new FileInfo(full).Length > 262144) throw new ArgumentException("Source exceeds 256 KiB.");
                return File.ReadAllText(full);
            }
            var text = c.Input["files"]?[path]?.GetValue<string>() ?? throw new ArgumentException("Source file is not configured.");
            if (Encoding.UTF8.GetByteCount(text) > 262144) throw new ArgumentException("Source exceeds 256 KiB.");
            return text;
        }
        return new("api-deprecation-migration", "API deprecation migration",
            "Call inspect_migration, apply_migration, verify_migration for the requested ID. Apply requires host approval and edits only configured local fixture sources. Exact bounded text replacements are not a general syntax-aware migration or code execution.",
            [new("inspect_migration", "Locate exact configured API usage strings and prepare bounded edits, preserving all surrounding source.", schema, (a, ct) =>
            {
                var m = Migration(a); var edits = m["edits"] as JsonArray ?? throw new ArgumentException("edits required.");
                if (edits.Count is < 1 or > 20) throw new ArgumentException("Require 1 to 20 edits.");
                var files = new JsonObject(); var matches = new JsonArray();
                foreach (var edit in edits.OfType<JsonObject>())
                {
                    var path = edit.Text("path"); PathFor(path);
                    if (c.Input["files"]?[path] is null) throw new ArgumentException("Path is not in fixture allowlist.");
                    var before = files[path]?["after"]?.GetValue<string>() ?? Source(path);
                    var find = edit.Text("find"); var replace = edit.Text("replace"); var expected = edit["expectedOccurrences"]?.GetValue<int>() ?? 0;
                    if (find.Length == 0 || replace.Length == 0 || find == replace || replace.Contains(find, StringComparison.Ordinal) || expected is < 1 or > 100)
                        throw new ArgumentException("Require distinct nonempty usage strings and 1 to 100 occurrences.");
                    var count = Count(before, find);
                    if (count != expected) throw new InvalidOperationException($"Expected {expected} occurrences in {path}, found {count}.");
                    if (files[path] is null) files[path] = new JsonObject { ["before"] = before, ["beforeHash"] = Hash(before) };
                    var after = before.Replace(find, replace, StringComparison.Ordinal);
                    if (Encoding.UTF8.GetByteCount(after) > 262144) throw new ArgumentException("Edited source exceeds 256 KiB.");
                    files[path]!["after"] = after;
                    matches.Add(new JsonObject { ["path"] = path, ["oldUsage"] = find, ["newUsage"] = replace, ["occurrences"] = count });
                }
                if (matches.Count != edits.Count) throw new ArgumentException("Every edit must be an object.");
                var state = new JsonObject { ["migrationId"] = a.Text("migrationId"), ["files"] = files, ["matches"] = matches, ["applied"] = false };
                c.State[a.Text("migrationId") + "-source"] = c.Input.ToJsonString();
                c.State[a.Text("migrationId")] = state;
                return Task.FromResult<JsonNode?>(new JsonObject { ["migrationId"] = a.Text("migrationId"), ["matches"] = matches.DeepClone(), ["fileCount"] = files.Count });
            }), new("apply_migration", "Apply the reviewed local edits only with host approval and unchanged source; repeated calls verify the original result.", schema, async (a, ct) =>
            {
                if (!c.Approved) throw new InvalidOperationException("Host approval is required to edit source.");
                Migration(a);
                if (c.State[a.Text("migrationId") + "-source"]?.GetValue<string>() != c.Input.ToJsonString()) throw new InvalidOperationException("Migration configuration changed; inspect again.");
                var state = c.State[a.Text("migrationId")]?.AsObject() ?? throw new InvalidOperationException("Inspect this migration first.");
                var files = state["files"]!.AsObject(); var applied = state["applied"]!.GetValue<bool>();
                foreach (var file in files)
                {
                    var expected = file.Value![applied ? "after" : "before"]!.GetValue<string>();
                    if (Source(file.Key) != expected) throw new InvalidOperationException("Source changed after review: " + file.Key);
                }
                if (!applied) foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested(); var full = PathFor(file.Key);
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    await File.WriteAllTextAsync(full, file.Value!["after"]!.GetValue<string>(), new UTF8Encoding(false), ct);
                }
                state["applied"] = true;
                return new JsonObject { ["migrationId"] = a.Text("migrationId"), ["applied"] = true, ["fileCount"] = files.Count };
            }, RequiresApproval: true), new("verify_migration", "Verify full final contents after all configured edits; persist local verification.", schema, async (a, ct) =>
            {
                Migration(a);
                if (c.State[a.Text("migrationId") + "-source"]?.GetValue<string>() != c.Input.ToJsonString()) throw new InvalidOperationException("Migration configuration changed; inspect again.");
                var state = c.State[a.Text("migrationId")]?.AsObject();
                if (state?["applied"]?.GetValue<bool>() != true) throw new InvalidOperationException("Apply this migration first.");
                var files = state["files"]!.AsObject(); var verified = true;
                foreach (var file in files) verified &= Source(file.Key) == file.Value!["after"]!.GetValue<string>();
                var result = new JsonObject { ["migrationId"] = a.Text("migrationId"), ["verified"] = verified, ["fileCount"] = files.Count, ["executedCode"] = false };
                var artifact = "verification-" + a.Text("migrationId") + ".json"; await c.WriteJsonAsync(artifact, result, ct);
                result["artifact"] = artifact; return result;
            })], [new("inspect_migration", new() { ["migrationId"] = "client-v2" }), new("apply_migration", new() { ["migrationId"] = "client-v2" }), new("verify_migration", new() { ["migrationId"] = "client-v2" })]);
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static int Count(string text, string find)
    {
        var count = 0; var start = 0;
        while ((start = text.IndexOf(find, start, StringComparison.Ordinal)) >= 0) { count++; start += find.Length; }
        return count;
    }
}
