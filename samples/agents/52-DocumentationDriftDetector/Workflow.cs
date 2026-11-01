// PiSharp-only (no upstream Pi source): fixture documentation drift comparison.
using System.Text.Json.Nodes;
namespace PiSharp.Samples.DocumentationDriftDetector;

public static class Workflow
{
    public static WorkflowSpec Create(SampleContext c)
    {
        var schema = SampleJson.Schema("{\"documentId\":{\"type\":\"string\"}}", "documentId");
        JsonObject Document(JsonObject a)
        {
            var id = a.Text("documentId");
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-zA-Z0-9_-]{1,60}$")) throw new ArgumentException("Invalid document ID.");
            var docs = (c.Input["documents"] as JsonArray ?? throw new ArgumentException("documents required.")).OfType<JsonObject>().Where(d => d.Text("id") == id).ToArray();
            return docs.Length == 1 ? docs[0] : throw new ArgumentException("Document must exist uniquely.");
        }
        bool Exists(string kind, string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return false;
            return kind switch
            {
                "file" => c.Input["repository"]?["files"] is JsonObject files && files.ContainsKey(target),
                "command" => c.Input["repository"]?["commands"] is JsonArray commands && commands.Any(x => x?.GetValue<string>() == target),
                "setting" => c.Input["repository"]?["settings"] is JsonObject settings && settings.ContainsKey(target),
                _ => throw new ArgumentException("Reference kind must be file, command or setting.")
            };
        }
        return new("documentation-drift-detector", "Documentation drift detector",
            "Inspect_document references against the fixture repository, propose_corrections, then write_corrections for each requested ID. Report exact source lines and unresolved references. Corrections are local review artifacts; repository documents are not changed and commands are not run.",
            [new("inspect_document", "Compare documented files, exact commands and setting keys with the supplied repository snapshot.", schema, (a, ct) =>
            {
                var d = Document(a); var text = d["text"]?.GetValue<string>() ?? throw new ArgumentException("Document text required.");
                var references = d["references"] as JsonArray ?? throw new ArgumentException("references required.");
                if (references.Count == 0 || c.Input["repository"] is not JsonObject repository || repository["files"] is not JsonObject || repository["commands"] is not JsonArray || repository["settings"] is not JsonObject)
                    throw new ArgumentException("A complete repository snapshot and nonempty references are required.");
                var checks = new JsonArray(); var used = new HashSet<string>(StringComparer.Ordinal);
                foreach (var r in references.OfType<JsonObject>())
                {
                    var snippet = r.Text("target");
                    if (snippet.Length == 0 || !used.Add(snippet)) throw new ArgumentException("Reference targets must be unique and nonempty.");
                    var offset = text.IndexOf(snippet, StringComparison.Ordinal);
                    if (offset < 0 || text.IndexOf(snippet, offset + snippet.Length, StringComparison.Ordinal) >= 0) throw new ArgumentException("Each target must occur exactly once in document text.");
                    checks.Add(new JsonObject { ["kind"] = r.Text("kind"), ["target"] = snippet, ["replacement"] = r.Text("replacement"), ["exists"] = Exists(r.Text("kind"), snippet), ["offset"] = offset, ["line"] = 1 + text[..offset].Count(ch => ch == '\n') });
                }
                if (checks.Count != references.Count) throw new ArgumentException("Every reference must be an object.");
                var state = new JsonObject { ["documentId"] = a.Text("documentId"), ["checks"] = checks, ["original"] = text };
                c.State[a.Text("documentId") + "-source"] = c.Input.ToJsonString();
                c.State[a.Text("documentId")] = state;
                return Task.FromResult<JsonNode?>(new JsonObject { ["documentId"] = a.Text("documentId"), ["checks"] = checks.DeepClone(), ["driftCount"] = checks.OfType<JsonObject>().Count(x => !x["exists"]!.GetValue<bool>()) });
            }), new("propose_corrections", "Generate precise line/offset corrections only when replacement references exist in the same repository snapshot.", schema, (a, ct) =>
            {
                Document(a);
                if (c.State[a.Text("documentId") + "-source"]?.GetValue<string>() != c.Input.ToJsonString()) throw new InvalidOperationException("Document or repository changed; inspect again.");
                var state = c.State[a.Text("documentId")]?.AsObject() ?? throw new InvalidOperationException("Inspect this document first.");
                var corrections = new JsonArray(); var unresolved = new JsonArray(); var text = state.Text("original"); var lastOffset = text.Length;
                foreach (var check in state["checks"]!.AsArray().OfType<JsonObject>().OrderByDescending(x => x["offset"]!.GetValue<int>()))
                {
                    if (check["exists"]!.GetValue<bool>()) continue;
                    var replacement = check.Text("replacement"); var target = check.Text("target"); var offset = check["offset"]!.GetValue<int>();
                    if (!Exists(check.Text("kind"), replacement)) { unresolved.Add(check.DeepClone()); continue; }
                    if (offset + target.Length > lastOffset) throw new ArgumentException("Overlapping reference corrections are not supported.");
                    text = text[..offset] + replacement + text[(offset + target.Length)..]; lastOffset = offset;
                    corrections.Add(new JsonObject { ["line"] = check["line"]!.DeepClone(), ["offset"] = offset, ["before"] = target, ["after"] = replacement, ["kind"] = check.Text("kind") });
                }
                state["corrections"] = corrections; state["unresolved"] = unresolved; state["correctedText"] = text;
                return Task.FromResult<JsonNode?>(new JsonObject { ["documentId"] = a.Text("documentId"), ["corrections"] = corrections.DeepClone(), ["unresolved"] = unresolved.DeepClone() });
            }), new("write_corrections", "Write a correction manifest plus proposed document as local review artifacts without changing repository content.", schema, async (a, ct) =>
            {
                Document(a);
                if (c.State[a.Text("documentId") + "-source"]?.GetValue<string>() != c.Input.ToJsonString()) throw new InvalidOperationException("Document or repository changed; inspect again.");
                var state = c.State[a.Text("documentId")]?.AsObject();
                if (state?["corrections"] is null) throw new InvalidOperationException("Propose corrections first.");
                var id = a.Text("documentId"); var report = "drift-" + id + ".json"; var proposal = "proposed-" + id + ".md";
                await c.WriteJsonAsync(report, state, ct); await File.WriteAllTextAsync(c.ResolvePath(proposal), state.Text("correctedText"), ct);
                return new JsonObject { ["artifact"] = report, ["proposedDocument"] = proposal, ["correctionCount"] = state["corrections"]!.AsArray().Count, ["unresolvedCount"] = state["unresolved"]!.AsArray().Count };
            })], [new("inspect_document", new() { ["documentId"] = "setup" }), new("propose_corrections", new() { ["documentId"] = "setup" }), new("write_corrections", new() { ["documentId"] = "setup" })]);
    }
}
