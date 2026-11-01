// PiSharp-only (no upstream Pi source): offline regression tests for normalized page monitoring.
using System.Text.Json.Nodes;

namespace PiSharp.Samples.WebsiteChangeMonitor;

public static class SelfTests
{
    public static async Task<int> RunAsync(string fixture)
    {
        var tests = new (string, Func<SampleContext, WorkflowSpec, Task>)[]
        {
            ("content change alerts; markup-only change suppressed", async (c, w) =>
            {
                var args = Workflow.Args("pricing", "status"); await Call(w, "inspect_recorded_pages", args);
                var plan = await Call(w, "compare_page_content", args);
                Check(plan!["Changes"]![0]!["Alert"]!.GetValue<bool>() && !plan["Changes"]![1]!["Alert"]!.GetValue<bool>(), "Wrong content conditions.");
                var result = await Call(w, "commit_monitor_results", args);
                Check(result!["newLocalAlerts"]!.GetValue<int>() == 1 && result["successfulPageIds"]!.AsArray().Count == 2, "Expected one local alert and two baselines.");
                Check(File.Exists(c.ResolvePath("monitor-state.json")), "Persistent state missing.");
            }),
            ("repeat and restart suppress duplicate alerts", async (c, w) =>
            {
                var args = Workflow.Args("pricing", "status"); await Call(w, "compare_page_content", args); await Call(w, "commit_monitor_results", args);
                Check((await Call(w, "commit_monitor_results", args))!["newLocalAlerts"]!.GetValue<int>() == 0, "Duplicate alert created.");
                var restart = Workflow.Create(new SampleContext(c.Workspace, c.Input, true)); await Call(restart, "compare_page_content", args);
                var result = await Call(restart, "commit_monitor_results", args);
                Check(result!["newLocalAlerts"]!.GetValue<int>() == 0 && result["totalLocalAlerts"]!.GetValue<int>() == 1, "Restart lost deduplication.");
            }),
            ("unknown and duplicate IDs rejected", async (_, w) =>
            {
                await Throws(() => Call(w, "compare_page_content", Workflow.Args("absent")));
                await Throws(() => Call(w, "compare_page_content", Workflow.Args("status", "status")));
            }),
            ("failed fetch preserves old baseline", async (c, w) =>
            {
                c.Input["pages"]![0]!["status"] = 503; var args = Workflow.Args("pricing", "status"); await Call(w, "compare_page_content", args);
                var result = await Call(w, "commit_monitor_results", args);
                var store = JsonNode.Parse(await File.ReadAllTextAsync(c.ResolvePath("monitor-state.json")))!;
                Check(result!["newLocalAlerts"]!.GetValue<int>() == 0 && store["baselines"]!["pricing"]!.GetValue<string>().Contains("20"), "Failure advanced baseline.");
            }),
            ("new page seeds without fabricated change", async (c, w) =>
            {
                c.Input["baselines"]!.AsObject().Remove("pricing"); var args = Workflow.Args("pricing");
                var plan = await Call(w, "compare_page_content", args);
                Check(!plan!["Changes"]![0]!["Alert"]!.GetValue<bool>(), "New baseline emitted change.");
                await Call(w, "commit_monitor_results", args);
            }),
            ("configured text and thresholds apply", async (c, w) =>
            {
                c.Input["pages"]![0]!["requiredText"] = "Enterprise";
                var plan = await Call(w, "compare_page_content", Workflow.Args("pricing"));
                Check(!plan!["Changes"]![0]!["Alert"]!.GetValue<bool>(), "Text condition ignored.");
                c.Input["pages"]![0]!["requiredText"] = "Standard"; c.Input["pages"]![0]!["minimumChangedWords"] = 9;
                plan = await Call(w, "compare_page_content", Workflow.Args("pricing"));
                Check(!plan!["Changes"]![0]!["Alert"]!.GetValue<bool>(), "Threshold ignored.");
            }),
            ("external baseline replacement invalidates an existing plan", async (c, w) =>
            {
                var args = Workflow.Args("pricing"); await Call(w, "compare_page_content", args);
                var replacement = new JsonObject { ["baselines"] = new JsonObject { ["pricing"] = Workflow.Normalize(c.Input["pages"]![0]!["html"]!.GetValue<string>()) }, ["alerts"] = new JsonObject() };
                await c.WriteJsonAsync("monitor-state.json", replacement);
                await Throws(() => Call(w, "commit_monitor_results", args));
                var store = JsonNode.Parse(await File.ReadAllTextAsync(c.ResolvePath("monitor-state.json")))!;
                Check(store["alerts"]!.AsObject().Count == 0, "Stale comparison emitted a notification.");
            }),
            ("stale page and unapproved callbacks cannot commit", async (c, w) =>
            {
                var args = Workflow.Args("pricing"); await Call(w, "compare_page_content", args); c.Input["pages"]![0]!["html"] = "changed after review";
                await Throws(() => Call(w, "commit_monitor_results", args));
                var unapproved = Workflow.Create(new SampleContext(c.Workspace, c.Input));
                Check(unapproved.Tools.Single(t => t.Name == "commit_monitor_results").RequiresApproval, "Missing approval flag.");
                await Throws(() => Call(unapproved, "commit_monitor_results", args)); Check(!File.Exists(c.ResolvePath("monitor-state.json")), "Guard wrote state.");
            })
        };
        var failures = 0;
        foreach (var (name, test) in tests)
        {
            var root = Path.Combine(Path.GetTempPath(), "pisharp-54-" + Guid.NewGuid().ToString("N"));
            try { var c = new SampleContext(root, JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject(), true); await test(c, Workflow.Create(c)); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} monitor checks passed."); return failures == 0 ? 0 : 1;
    }
    private static Task<JsonNode?> Call(WorkflowSpec w, string name, JsonObject args) => w.Tools.Single(t => t.Name == name).Execute(args, default);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Throws(Func<Task<JsonNode?>> action) { try { await action(); } catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or UnauthorizedAccessException) { return; } throw new InvalidOperationException("Expected rejection."); }
}
