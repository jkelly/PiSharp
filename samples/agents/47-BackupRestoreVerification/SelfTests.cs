// PiSharp-only (no upstream Pi source): real file restore, corruption and approval tests.
using System.Text.Json.Nodes;
namespace PiSharp.Samples.BackupRestoreVerification;
public static class SelfTests
{
    public static async Task<int> RunAsync(string fixture)
    {
        var input = JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject(); var count = 0;
        SampleContext Context(bool approved = true) => new(Path.Combine(Path.GetTempPath(),"pisharp-47-" + Guid.NewGuid().ToString("N")),(JsonObject)input.DeepClone(),approved);
        async Task<JsonNode> Call(SampleContext c,string name,string id = "orders") => (await Workflow.Create(c).Tools.Single(t => t.Name == name).Execute(new JsonObject { ["datasetId"] = id },default))!;
        static void Check(bool value) { if (!value) throw new Exception("Backup assertion failed."); }
        static async Task Reject(Func<Task<JsonNode>> action) { try { await action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException) { return; } throw new Exception("Expected rejection."); }
        var c = Context(); await Call(c,"plan_backup"); var snapshot = await Call(c,"create_snapshot"); var result = await Call(c,"verify_restore"); Check(result["verified"]!.GetValue<bool>() && result["files"]!.AsArray().Count == 2 && File.Exists(c.ResolvePath("restores/orders/attempt-1/orders.json"))); count++;
        Check(JsonNode.DeepEquals(snapshot,await Call(c,"create_snapshot"))); var repeat = await Call(c,"verify_restore"); Check(repeat["verified"]!.GetValue<bool>() && repeat["restoreDirectory"]!.GetValue<string>() == "restores/orders/attempt-2"); count++;
        await Call(c,"plan_backup","inventory"); await Call(c,"create_snapshot","inventory"); var second = await Call(c,"verify_restore","inventory"); Check(second["verified"]!.GetValue<bool>() && second["files"]!.AsArray().Count == 1); count++;
        await File.WriteAllTextAsync(c.ResolvePath("snapshots/orders/orders.json"),"corrupt"); var corrupt = await Call(c,"verify_restore"); Check(!corrupt["verified"]!.GetValue<bool>() && corrupt["files"]![0]!["status"]!.GetValue<string>() == "corrupt"); count++;
        File.Delete(c.ResolvePath("snapshots/orders/config/settings.txt")); var missing = await Call(c,"verify_restore"); Check(!missing["verified"]!.GetValue<bool>() && missing["files"]![1]!["status"]!.GetValue<string>() == "missing"); count++;
        var no = Context(false); await Call(no,"plan_backup"); Check(Workflow.Create(no).Tools.Where(t => t.Name != "plan_backup").All(t => t.RequiresApproval)); await Reject(() => Call(no,"create_snapshot")); await Reject(() => Call(no,"verify_restore")); Check(!Directory.Exists(no.ResolvePath("sources/orders"))); count++;
        await Reject(() => Call(Context(),"create_snapshot")); await Reject(() => Call(Context(),"verify_restore")); await Reject(() => Call(Context(),"plan_backup","missing")); count++;
        var escape = Context(); escape.Input["datasets"]![0]!["files"]![0]!["path"] = "../outside"; await Reject(() => Call(escape,"plan_backup")); var absent = Context(); absent.Input["datasets"]![0]!.AsObject().Remove("files"); await Reject(() => Call(absent,"plan_backup")); count++;
        var existing = Context(); await Call(existing,"plan_backup"); var path = existing.ResolvePath("sources/orders/orders.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path,"must preserve"); await Reject(() => Call(existing,"create_snapshot")); Check(await File.ReadAllTextAsync(path) == "must preserve"); count++;
        var changed = Context(); await Call(changed,"plan_backup"); await Call(changed,"create_snapshot"); changed.Input["datasets"]![0]!["files"]![0]!["content"] = "new dataset contents"; await Call(changed,"plan_backup"); await Reject(() => Call(changed,"create_snapshot")); count++;
        Console.WriteLine($"PASS 47-BackupRestoreVerification ({count} focused cases)"); return 0;
    }
}
