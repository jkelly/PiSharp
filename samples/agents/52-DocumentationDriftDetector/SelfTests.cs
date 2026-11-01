// PiSharp-only (no upstream Pi source): documentation snapshot comparison tests.
using System.Text.Json.Nodes;
namespace PiSharp.Samples.DocumentationDriftDetector;
public static class SelfTests
{
    public static async Task<int> RunAsync(string fixture)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-52-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); var passed = 0;
        var source = JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject();
        SampleContext Context() => new(Path.Combine(root, Guid.NewGuid().ToString("N")), (JsonObject)source.DeepClone());
        async Task Check(string name, Func<Task> test) { await test(); passed++; Console.WriteLine("PASS " + name); }
        try
        {
            await Check("three reference kinds produce precise review artifacts", async () =>
            {
                var c = Context(); var original = c.Input.ToJsonString(); var w = Workflow.Create(c); var r = await Call(w, 0);
                Assert(r["driftCount"]!.GetValue<int>() == 3 && r["checks"]![0]!["line"]!.GetValue<int>() == 2, "Incorrect drift or line.");
                await Call(w, 1); r = await Call(w, 2); Assert(r["correctionCount"]!.GetValue<int>() == 3, "Missing corrections.");
                var text = await File.ReadAllTextAsync(c.ResolvePath("proposed-setup.md"));
                Assert(text.Contains("APP_REGION") && text.Contains("Keep this sentence unchanged.") && c.Input.ToJsonString() == original, "Unrelated text or fixture changed.");
            });
            await Check("unknown replacement remains unresolved", async () =>
            {
                var c = Context(); c.Input["documents"]![0]!["references"]![0]!["replacement"] = "missing.cs"; var w = Workflow.Create(c); await Call(w, 0);
                var r = await Call(w, 1); Assert(r["unresolved"]!.AsArray().Count == 1 && r["corrections"]!.AsArray().Count == 2, "Invented replacement.");
            });
            await Check("missing repository evidence and malformed references reject", async () =>
            {
                var c = Context(); c.Input["repository"]!.AsObject().Remove("commands"); await Reject(() => Call(Workflow.Create(c), 0));
                c = Context(); c.Input["documents"]![0]!["references"]!.AsArray().Add("invalid"); await Reject(() => Call(Workflow.Create(c), 0));
            });
            await Check("ambiguous document snippets reject", async () =>
            {
                var c = Context(); c.Input["documents"]![0]!["text"] = c.Input["documents"]![0]!["text"]!.GetValue<string>() + "APP_ZONE\n";
                await Reject(() => Call(Workflow.Create(c), 0));
            });
            await Check("two documents and repeated inspection have independent results", async () =>
            {
                var c = Context(); var other = (JsonObject)c.Input["documents"]![0]!.DeepClone(); other["id"] = "other"; other["text"] = "Use src/App.cs.\n";
                other["references"] = JsonNode.Parse("[{\"kind\":\"file\",\"target\":\"src/App.cs\"}]"); c.Input["documents"]!.AsArray().Add(other);
                var w = Workflow.Create(c); var first = (await Call(w, 0)).ToJsonString(); Assert((await Call(w, 0, "other"))["driftCount"]!.GetValue<int>() == 0, "Other doc reused stale state.");
                Assert(first == (await Call(w, 0)).ToJsonString(), "Repeated inspection changed result.");
            });
            await Check("stage unknown-id and changed-snapshot guards", async () =>
            {
                var c = Context(); var w = Workflow.Create(c); await Reject(() => Call(w, 2)); await Reject(() => Call(w, 0, "missing")); await Call(w, 0);
                c.Input["repository"]!["settings"]!.AsObject().Remove("APP_REGION"); await Reject(() => Call(w, 1));
            });
            Console.WriteLine($"{passed} tests passed."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(root, true); }
    }
    private static async Task<JsonObject> Call(WorkflowSpec w, int stage, string id = "setup") => (await w.Tools[stage].Execute(new() { ["documentId"] = id }, default))!.AsObject();
    private static void Assert(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task<JsonObject>> action) { try { await action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return; } throw new Exception("Expected rejection."); }
}
