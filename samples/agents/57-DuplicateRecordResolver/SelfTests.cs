// PiSharp-only (no upstream Pi source): identity evidence, conflict and merge replay regressions.
using System.Text.Json.Nodes;

namespace PiSharp.Samples.DuplicateRecordResolver;

public static class SelfTests
{
    public static async Task<int> RunAsync(string fixture)
    {
        var tests = new (string, Func<SampleContext, WorkflowSpec, Task>)[]
        {
            ("two merges preserve survivors, fields, notes and provenance", async (c, w) =>
            {
                var evidence = await Call(w, "inspect_duplicate_evidence", new JsonObject { ["recordIds"] = new JsonArray("c1", "c2", "c3", "c4", "c5", "c6") });
                Check(evidence!.AsArray().Any(e => e!["LeftId"]!.GetValue<string>() == "c5" && !e["Eligible"]!.GetValue<bool>()), "Ambiguous family identity hidden.");
                var args = Workflow.Args("merge-alex", "merge-jordan"); await Call(w, "propose_duplicate_merges", args); var result = await Call(w, "apply_duplicate_merges", args);
                Check(result!["removedIds"]!.AsArray().Count == 2 && result["survivors"]![0]!["Id"]!.GetValue<string>() == "c1", "Wrong survivors.");
                var store = JsonNode.Parse(await File.ReadAllTextAsync(c.ResolvePath("customer-state.json")))!;
                Check(store["records"]!.AsArray().Count == 4 && store["mergeHistory"]!.AsObject().Count == 2, "Source provenance not retained.");
                Check(result["survivors"]![0]!["Address"]!.GetValue<string>() == "20 Pine St" && result["survivors"]![0]!["Notes"]!.AsArray().Count == 2, "Resolution or notes lost.");
                Check(result["survivors"]![1]!["Email"]!.GetValue<string>() == "jordan@example.test", "Missing field not filled from source.");
            }),
            ("idempotent replay and persisted survivors", async (c, w) =>
            {
                var args = Workflow.Args("merge-alex", "merge-jordan"); await Call(w, "propose_duplicate_merges", args);
                var first = await Call(w, "apply_duplicate_merges", args); var second = await Call(w, "apply_duplicate_merges", args);
                Check(JsonNode.DeepEquals(first, second), "Repeated merge changed receipt.");
                var restart = Workflow.Create(new SampleContext(c.Workspace, c.Input, true));
                await Throws(() => Call(restart, "inspect_duplicate_evidence", new JsonObject { ["recordIds"] = new JsonArray("c1", "c2") }));
                await Call(restart, "propose_duplicate_merges", args);
                Check((await Call(restart, "apply_duplicate_merges", args))!["removedIds"]!.AsArray().Count == 0, "Restart removed a duplicate twice.");
                var store = JsonNode.Parse(await File.ReadAllTextAsync(c.ResolvePath("customer-state.json")))!;
                Check(store["mergeHistory"]!["merge-alex"]!["merge"]!["Originals"]!.AsArray().Count == 2, "Replay overwrote source provenance.");
            }),
            ("shared mailbox with contradictory identity is blocked", async (c, w) =>
            {
                c.Input["merges"]!.AsArray().Add(SampleJson.Object("""{"id":"ambiguous","survivorId":"c5","duplicateIds":["c6"],"resolutions":{}}"""));
                var args = Workflow.Args("ambiguous"); var plan = await Call(w, "propose_duplicate_merges", args);
                Check(plan!["Merges"]![0]!["Blockers"]!.AsArray().Count > 0, "Contradictions overmerged.");
                await Throws(() => Call(w, "apply_duplicate_merges", args)); Check(!File.Exists(c.ResolvePath("customer-state.json")), "Ambiguous merge wrote state.");
            }),
            ("duplicate-group phone conflict cannot hide behind blank survivor", (c, w) => GroupConflict(c, w, "phone", "3125550101", "3125550999")),
            ("duplicate-group external ID conflict cannot hide behind blank survivor", (c, w) => GroupConflict(c, w, "externalId", "acct-first", "acct-second")),
            ("unresolved field conflict blocks the complete batch", async (c, w) =>
            {
                c.Input["merges"]![0]!["resolutions"] = new JsonObject(); var args = Workflow.Args("merge-alex", "merge-jordan");
                var plan = await Call(w, "propose_duplicate_merges", args);
                Check(plan!["Merges"]![0]!["Blockers"]!.AsArray().Count == 1, "Missing address choice was invented.");
                await Throws(() => Call(w, "apply_duplicate_merges", args));
            }),
            ("missing identity and overlapping groups rejected", async (c, w) =>
            {
                await Throws(() => Call(w, "propose_duplicate_merges", Workflow.Args("absent")));
                c.Input["merges"]![1]!["duplicateIds"] = new JsonArray("c2");
                await Throws(() => Call(w, "propose_duplicate_merges", Workflow.Args("merge-alex", "merge-jordan")));
            }),
            ("freshness and direct mutation approval", async (c, w) =>
            {
                var args = Workflow.Args("merge-alex"); await Call(w, "propose_duplicate_merges", args);
                c.Input["records"]![0]!["email"] = "different@example.test"; await Throws(() => Call(w, "apply_duplicate_merges", args));
                var no = Workflow.Create(new SampleContext(c.Workspace, c.Input));
                Check(no.Tools.Single(t => t.Name == "apply_duplicate_merges").RequiresApproval, "Missing host gate.");
                await Throws(() => Call(no, "apply_duplicate_merges", args)); Check(!File.Exists(c.ResolvePath("customer-state.json")), "Guard wrote state.");
            })
        };
        var failures = 0;
        foreach (var (name, test) in tests)
        {
            var root = Path.Combine(Path.GetTempPath(), "pisharp-57-" + Guid.NewGuid().ToString("N"));
            try { var c = new SampleContext(root, JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject(), true); await test(c, Workflow.Create(c)); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} duplicate checks passed."); return failures == 0 ? 0 : 1;
    }
    private static Task<JsonNode?> Call(WorkflowSpec w, string name, JsonObject args) => w.Tools.Single(t => t.Name == name).Execute(args, default);
    private static async Task GroupConflict(SampleContext c, WorkflowSpec w, string field, string firstValue, string secondValue)
    {
        var survivor = c.Input["records"]![0]!.AsObject(); survivor[field] = "";
        var first = survivor.DeepClone().AsObject(); first["id"] = "c2"; first[field] = firstValue;
        var second = survivor.DeepClone().AsObject(); second["id"] = "c7"; second[field] = secondValue;
        c.Input["records"]!.AsArray()[1] = first; c.Input["records"]!.AsArray().Add(second);
        c.Input["merges"]![0]!["duplicateIds"] = new JsonArray("c2", "c7");
        c.Input["merges"]![0]!["resolutions"] = new JsonObject();
        var originalRecords = c.Input["records"]!.DeepClone(); var args = Workflow.Args("merge-alex");
        var plan = await Call(w, "propose_duplicate_merges", args);
        Check(plan!["Merges"]![0]!["Survivor"] is null && plan["Merges"]![0]!["Blockers"]!.AsArray().Any(b => b!.GetValue<string>().Contains(field, StringComparison.Ordinal)),
            "Conflicting duplicate " + field + " values were silently reduced to one survivor value.");
        await Throws(() => Call(w, "apply_duplicate_merges", args));
        Check(!File.Exists(c.ResolvePath("customer-state.json")) && !File.Exists(c.ResolvePath("duplicate-merge-receipt.json")), "Rejected group wrote customer data or a success receipt.");
        Check(JsonNode.DeepEquals(originalRecords, c.Input["records"]), "Rejected merge changed source records.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Throws(Func<Task<JsonNode?>> action) { try { await action(); } catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or UnauthorizedAccessException) { return; } throw new InvalidOperationException("Expected rejection."); }
}
