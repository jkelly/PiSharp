// PiSharp-only (no upstream Pi source): offline grant requirement regression tests.
using System.Text.Json.Nodes;

namespace PiSharp.Samples.GrantApplicationOrganizer;
public static class SelfTests
{
    public static async Task<int> RunAsync(string fixture)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-59-" + Guid.NewGuid().ToString("N"));
        try
        {
            var input = JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject(); var c = new SampleContext(root, input);
            var result = Workflow.Validate(c, "arts-2026");
            Check(result["gaps"]!.AsArray().Count == 2 && result.Text("status") == "incomplete", "expired certificate and missing letter");
            Check(result["requirementMap"]![0]!["evidence"]![0]!["documentId"]!.GetValue<string>() == "d-budget", "source evidence mapped");
            Check(result["requirementMap"]![1]!["rejected"]![0]!["reason"]!.GetValue<string>() == "expires-before-deadline", "expiry reason retained");
            Check(Workflow.Validate(c, "mini-2026").Text("status") == "ready-for-human-review", "second application isolated");
            Check(JsonNode.DeepEquals(result, Workflow.Validate(c, "arts-2026")), "repeated validation unchanged");
            Throws(() => Workflow.Validate(c, "absent"), "unknown ID rejected");
            var malformed = (JsonObject)input.DeepClone(); malformed["applications"]![0]!["requirements"] = new JsonArray();
            Throws(() => Workflow.Validate(new SampleContext(Path.Combine(root, "empty"), malformed), "arts-2026"), "empty requirements rejected");
            malformed = (JsonObject)input.DeepClone(); malformed["applications"]![0]!["documents"]!.AsArray().Add("not-a-document");
            Throws(() => Workflow.Validate(new SampleContext(Path.Combine(root, "malformed"), malformed), "arts-2026"), "malformed document not silently dropped");
            input["applications"]![0]!["documents"]![0]!["evidence"] = "";
            Check(Workflow.Validate(c, "arts-2026")["gaps"]!.AsArray().Count == 3, "missing evidence cannot satisfy requirement");
            input["applications"]![0]!["documents"]![0]!["evidence"] = "Restored budget";
            input["applications"]![0]!["documents"]![1]!["expires"] = "2026-12-01";
            Check(Workflow.Validate(c, "arts-2026")["gaps"]!.AsArray().Count == 1, "valid through deadline accepted");
            input["applications"]![0]!["documents"]![0]!["requirementIds"]!.AsArray().Add("unknown-requirement");
            Check(Workflow.Validate(c, "arts-2026")["gaps"]!.AsArray().Count == 2, "unknown reference surfaced");
            input["applications"]![0]!["documents"]!.AsArray().Add(input["applications"]![0]!["documents"]![0]!.DeepClone());
            Throws(() => Workflow.Validate(c, "arts-2026"), "conflicting document identity rejected");
            var native = new SampleContext(Path.Combine(root, "native"), JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject());
            Check((await SampleHost.ExecuteAsync(Workflow.Create(native), native)).Success, "native three-stage workflow");
            Check(File.Exists(native.ResolvePath("grant-arts-2026.json")), "review package persisted");
            Console.WriteLine("PASS 59: mapping, expiry, missing evidence, references, IDs, repeats and native package."); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL 59: " + e.Message); return 1; }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws(Action action, string message) { try { action(); } catch (ArgumentException) { return; } throw new InvalidOperationException(message); }
}
