// PiSharp-only (no upstream Pi source): bounded allowlisted local runbook execution.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
namespace PiSharp.Samples.RunbookExecutionAssistant;

public static class Workflow
{
    static JsonObject Schema() => SampleJson.Schema("""{"runbookId":{"type":"string"}}""", "runbookId");
    public static WorkflowSpec Create(SampleContext c) => new("46-RunbookExecutionAssistant", "Runbook execution assistant",
        "Call inspect_runbook, execute_runbook, then write_execution_report for every requested runbook ID. Execute only the four allowlisted local actions. Execution requires host approval. Respect dependency failures and report actual file evidence; never run shell commands or claim production changes.",
        [new("inspect_runbook", "Validate action allowlist, safe paths and dependency graph", Schema(), (a,t) => Inspect(c,a,t)),
         new("execute_runbook", "Execute approved local writes and file checks in dependency order", Schema(), (a,t) => Execute(c,a,t), true),
         new("write_execution_report", "Persist actual execution and skipped-step evidence", Schema(), (a,t) => Report(c,a,t))],
        [new("inspect_runbook",new() { ["runbookId"] = "recovery" }),new("execute_runbook",new() { ["runbookId"] = "recovery" }),new("write_execution_report",new() { ["runbookId"] = "recovery" })]);
    static JsonArray Rows(JsonObject o,string key) => o[key] as JsonArray ?? throw new FormatException("Missing " + key);
    static JsonObject Runbook(SampleContext c,string id) => Rows(c.Input,"runbooks").OfType<JsonObject>().SingleOrDefault(x => x.Text("id") == id) ?? throw new ArgumentException("Unknown runbook: " + id);
    static string Fingerprint(SampleContext c,string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Runbook(c,id).ToJsonString())));
    static string SafePath(SampleContext c,string id,JsonObject step)
    {
        var path = step.Text("path"); if (!System.Text.RegularExpressions.Regex.IsMatch(id,"^[a-zA-Z0-9_-]+$") || !path.Replace('\\','/').StartsWith("operations/" + id + "/",StringComparison.Ordinal)) throw new ArgumentException("Runbook actions must stay in their operations directory.");
        var resolved = c.ResolvePath(path); var prefix = c.ResolvePath("operations/" + id + "/anchor"); prefix = Path.GetDirectoryName(prefix)! + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix,OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new ArgumentException("Action path escapes runbook directory."); return resolved;
    }
    static List<JsonObject> Plan(SampleContext c,string id)
    {
        var rows = Rows(Runbook(c,id),"steps");
        if (rows.Count is < 1 or > 20) throw new FormatException("Use 1–20 runbook steps.");
        if (rows.Any(step => step is not JsonObject)) throw new FormatException("Every runbook step must be an object.");
        var steps = rows.Cast<JsonObject>().ToList();
        if (steps.Any(s => s.Text("id").Length == 0) || steps.Select(s => s.Text("id")).Distinct().Count() != steps.Count) throw new FormatException("Unique step IDs required.");
        foreach (var step in steps)
        {
            if (step.Text("action") is not ("write_text" or "check_exists" or "check_contains" or "sha256")) throw new ArgumentException("Unsupported action: " + step.Text("action"));
            _ = SafePath(c,id,step); _ = Rows(step,"dependsOn");
            if (step.Text("action") == "write_text" && step["content"] is null) throw new FormatException("Write content required.");
            if (step.Text("content").Length > 100_000) throw new FormatException("Content limit exceeded.");
            if (step.Text("action") == "check_contains" && step.Text("expected").Length == 0) throw new FormatException("Nonempty expected content required.");
            if (Rows(step,"dependsOn").Any(d => !steps.Any(s => s.Text("id") == d?.GetValue<string>()))) throw new FormatException("Unknown dependency.");
        }
        var sorted = new List<JsonObject>();
        while (sorted.Count < steps.Count)
        {
            var ready = steps.FirstOrDefault(s => !sorted.Contains(s) && Rows(s,"dependsOn").All(d => sorted.Any(x => x.Text("id") == d?.GetValue<string>())));
            if (ready is null) throw new FormatException("Dependency cycle detected."); sorted.Add(ready);
        }
        return sorted;
    }
    static Task<JsonNode?> Inspect(SampleContext c,JsonObject a,CancellationToken t)
    {
        t.ThrowIfCancellationRequested(); var id = a.Text("runbookId"); c.State.Remove("execution:" + id); var steps = Plan(c,id); c.State["plan:" + id] = Fingerprint(c,id);
        return Task.FromResult<JsonNode?>(new JsonObject { ["runbookId"] = id, ["scope"] = "workspace-only", ["steps"] = new JsonArray(steps.Select(s => s.DeepClone()).ToArray()), ["approvalRequired"] = true });
    }
    static async Task<JsonNode?> Execute(SampleContext c,JsonObject a,CancellationToken t)
    {
        if (!c.Approved) throw new InvalidOperationException("Approval required for runbook execution."); var id = a.Text("runbookId");
        c.State.Remove("execution:" + id); var fingerprint = Fingerprint(c,id);
        if (c.State["plan:" + id]?.GetValue<string>() != fingerprint) throw new InvalidOperationException("Inspect the current runbook before executing.");
        var results = new JsonArray(); var passed = new Dictionary<string,bool>();
        foreach (var step in Plan(c,id))
        {
            t.ThrowIfCancellationRequested(); var sid = step.Text("id"); var action = step.Text("action"); var path = SafePath(c,id,step);
            if (Rows(step,"dependsOn").Any(d => !passed[d!.GetValue<string>()])) { passed[sid] = false; results.Add(new JsonObject { ["stepId"] = sid, ["status"] = "skipped", ["reason"] = "dependency failed" }); continue; }
            bool ok; string evidence;
            try
            {
                switch (action)
                {
                    case "write_text":
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var content = step.Text("content");
                        if (!File.Exists(path) || await File.ReadAllTextAsync(path,t) != content) await File.WriteAllTextAsync(path,content,t);
                        ok = await File.ReadAllTextAsync(path,t) == content; evidence = "sha256:" + Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path,t))).ToLowerInvariant(); break;
                    case "check_exists": ok = File.Exists(path); evidence = ok ? "file exists" : "file missing"; break;
                    case "check_contains": ok = File.Exists(path) && (await File.ReadAllTextAsync(path,t)).Contains(step.Text("expected"),StringComparison.Ordinal); evidence = ok ? "expected text found" : "file missing or expected text absent"; break;
                    default: ok = File.Exists(path); evidence = ok ? "sha256:" + Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path,t))).ToLowerInvariant() : "file missing"; break;
                }
            }
            catch (IOException ex) { ok = false; evidence = ex.GetType().Name; }
            passed[sid] = ok; results.Add(new JsonObject { ["stepId"] = sid, ["action"] = action, ["path"] = step.Text("path"), ["status"] = ok ? "passed" : "failed", ["evidence"] = evidence });
        }
        var result = new JsonObject { ["runbookId"] = id, ["planFingerprint"] = fingerprint, ["success"] = passed.Values.All(x => x), ["scope"] = "workspace-only", ["results"] = results }; c.State["execution:" + id] = result.DeepClone(); return result;
    }
    static async Task<JsonNode?> Report(SampleContext c,JsonObject a,CancellationToken t)
    {
        var id = a.Text("runbookId"); var r = c.State["execution:" + id]?.DeepClone() ?? throw new InvalidOperationException("Execute this runbook first.");
        if (r["planFingerprint"]?.GetValue<string>() != Fingerprint(c,id) || r["planFingerprint"]?.GetValue<string>() != c.State["plan:" + id]?.GetValue<string>()) throw new InvalidOperationException("Runbook changed after execution; inspect and execute again.");
        await c.WriteJsonAsync("runbook-execution.json",r,t); return r;
    }
}
