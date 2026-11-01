// PiSharp-only (no upstream Pi source): offline regression tests for bounded accessibility edits.
using System.Text.Json.Nodes;

namespace PiSharp.Samples.AccessibilityAuditAssistant;

public static class SelfTests
{
    public static async Task<int> RunAsync(string fixture)
    {
        var tests = new (string, Func<SampleContext, WorkflowSpec, Task>)[]
        {
            ("two documents, supported fixes and rescan", async (c, w) =>
            {
                var findings = await Call(w, "inspect_accessibility", Workflow.Args("checkout", "contact"));
                Check(findings!.AsArray().Count == 4, "Expected four concrete findings.");
                await Call(w, "propose_accessibility_fixes", Workflow.Args("checkout", "contact"));
                var result = await Call(w, "apply_accessibility_fixes", Workflow.Args("checkout", "contact"));
                Check(result!["fixedCount"]!.GetValue<int>() == 4 && result["remaining"]!.AsArray().Count == 0, "Fix/rescan failed.");
                Check((await Call(w, "inspect_accessibility", Workflow.Args("contact")))!.AsArray().Count == 0, "Second document retained stale findings.");
                Check(File.Exists(c.ResolvePath("accessibility-rescan.json")), "No persisted rescan.");
            }),
            ("missing and duplicate selections rejected", async (_, w) =>
            {
                await Throws(() => Call(w, "inspect_accessibility", Workflow.Args("missing")));
                await Throws(() => Call(w, "inspect_accessibility", Workflow.Args("checkout", "checkout")));
            }),
            ("missing fix stays unresolved", async (c, w) =>
            {
                c.Input["fixes"]!.AsArray().RemoveAt(3);
                await Call(w, "propose_accessibility_fixes", Workflow.Args("contact"));
                var result = await Call(w, "apply_accessibility_fixes", Workflow.Args("contact"));
                Check(result!["remaining"]!.AsArray().Count == 1, "Missing fix was invented.");
            }),
            ("malformed HTML never reports clean", async (c, w) =>
            {
                c.Input["documents"]![0]!["html"] = "<html><input></html>";
                var result = await Call(w, "inspect_accessibility", Workflow.Args("checkout"));
                Check(result![0]!["Rule"]!.GetValue<string>() == "malformed-xhtml", "Malformed input hidden.");
            }),
            ("idempotent apply and restart", async (c, w) =>
            {
                var args = Workflow.Args("checkout", "contact"); await Call(w, "propose_accessibility_fixes", args);
                var first = await Call(w, "apply_accessibility_fixes", args); var second = await Call(w, "apply_accessibility_fixes", args);
                Check(JsonNode.DeepEquals(first, second), "Repeat changed the receipt.");
                var restarted = Workflow.Create(new SampleContext(c.Workspace, c.Input, true));
                Check((await Call(restarted, "inspect_accessibility", args))!.AsArray().Count == 0, "Restart lost fixed documents.");
            }),
            ("stale source and approval guards", async (c, w) =>
            {
                var args = Workflow.Args("checkout"); await Call(w, "propose_accessibility_fixes", args);
                c.Input["documents"]![0]!["html"] = "<html lang='en'><body /></html>";
                await Throws(() => Call(w, "apply_accessibility_fixes", args));
                var unapproved = Workflow.Create(new SampleContext(c.Workspace, c.Input, false));
                Check(unapproved.Tools.Single(t => t.Name == "apply_accessibility_fixes").RequiresApproval, "Missing host approval flag.");
                await Throws(() => Call(unapproved, "apply_accessibility_fixes", args));
                Check(!File.Exists(c.ResolvePath("accessibility-documents.json")), "Guard wrote data.");
            }),
            ("labels, decorative alt and duplicate element IDs", async (c, w) =>
            {
                c.Input["documents"]![0]!["html"] = "<html lang='en'><body><img alt=''/><label for='x'>Name</label><input id='x'/><button aria-label='Save'/></body></html>";
                Check((await Call(w, "inspect_accessibility", Workflow.Args("checkout")))!.AsArray().Count == 0, "Valid supported names rejected.");
                c.Input["documents"]![0]!["html"] = "<html lang='en'><input id='x'/><input id='x'/></html>";
                Check((await Call(w, "inspect_accessibility", Workflow.Args("checkout")))![0]!["Rule"]!.GetValue<string>() == "malformed-xhtml", "Ambiguous target IDs allowed.");
            })
        };
        var failures = 0;
        foreach (var (name, test) in tests)
        {
            var root = Path.Combine(Path.GetTempPath(), "pisharp-53-" + Guid.NewGuid().ToString("N"));
            try { var c = new SampleContext(root, JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject(), true); await test(c, Workflow.Create(c)); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} accessibility checks passed."); return failures == 0 ? 0 : 1;
    }
    private static Task<JsonNode?> Call(WorkflowSpec w, string name, JsonObject args) => w.Tools.Single(t => t.Name == name).Execute(args, default);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Throws(Func<Task<JsonNode?>> action) { try { await action(); } catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or UnauthorizedAccessException) { return; } throw new InvalidOperationException("Expected rejection."); }
}
