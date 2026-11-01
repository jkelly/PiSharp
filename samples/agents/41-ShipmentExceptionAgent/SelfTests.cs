// PiSharp-only (no upstream Pi source): event ordering, timestamp exceptions and local draft checks.
using System.Text.Json.Nodes;
using PiSharp.Samples;
namespace PiSharp.Samples.ShipmentExceptionAgent;

public static class SelfTests
{
    public static async Task<int> RunAsync(string fixture)
    {
        var root = Path.Combine(Path.GetTempPath(),"pisharp-41-" + Guid.NewGuid().ToString("N"));
        try
        {
            var input = JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject();
            var c = new SampleContext(root,input);
            var w = Workflow.Create(c);
            async Task<JsonNode> Call(int i,string id) => (await w.Tools[i].Execute(new() { ["shipmentId"] = id },default))!;
            var inspected = await Call(0,"SHIP-501");
            Check(inspected["events"]![0]!["id"]!.GetValue<string>() == "EV-501","events sorted by occurrence");
            var exception = await Call(1,"SHIP-501");
            Check(exception["overdueHours"]!.GetValue<double>() == 18 && exception["hoursSinceLastEvent"]!.GetValue<double>() == 52,"event-time arithmetic");
            Check(exception["stalled"]!.GetValue<bool>() && exception["needsAttention"]!.GetValue<bool>() && Data.Text(exception,"latestEventId") == "EV-502","delay and stall");
            var delivered = await Call(1,"SHIP-600");
            Check(delivered["delivered"]!.GetValue<bool>() && !delivered["needsAttention"]!.GetValue<bool>(),"delivered shipment is not an active stall");
            Check(!(await Call(1,"UNKNOWN"))["found"]!.GetValue<bool>(),"missing ID");
            Check(JsonNode.DeepEquals(exception,await Call(1,"SHIP-501")),"multiple IDs do not share state");
            var saved = await Call(2,"SHIP-501");
            var draft = saved["draft"]!;
            Check(!draft["sent"]!.GetValue<bool>() && Data.Text(draft,"customerDraft").Contains("EV-502",StringComparison.Ordinal) && Data.Text(draft,"customerDraft").Contains("no confirmed revised ETA",StringComparison.Ordinal),"grounded unsent draft");
            var first = await File.ReadAllTextAsync(c.ResolvePath("shipments/SHIP-501.json"));
            await Call(2,"SHIP-501");
            Check(first == await File.ReadAllTextAsync(c.ResolvePath("shipments/SHIP-501.json")),"deterministic repeated report");
            input["shipments"]![0]!["events"]!.AsArray().Clear();
            var absent = await Call(1,"SHIP-501");
            Check(absent["trackingMissing"]!.GetValue<bool>() && absent["needsAttention"]!.GetValue<bool>() && absent["hoursSinceLastEvent"] is null,"missing tracking stays unknown");
            input["shipments"]![0]!["events"]!.AsArray().Add(SampleJson.Object("""{"id":"EV-FUTURE","occurredAt":"2026-11-02T12:00:00Z","status":"delivered","location":"Depot"}"""));
            await Throws(() => Call(1,"SHIP-501"),"future event rejected");
            input["shipments"]![0]!["events"]![0]!["occurredAt"] = "2026-11-01T11:00:00Z";
            input["shipments"]![0]!["events"]!.AsArray().Add(SampleJson.Object("""{"id":"EV-CONFLICT","occurredAt":"2026-11-01T11:00:00Z","status":"in_transit","location":"Depot"}"""));
            await Throws(() => Call(1,"SHIP-501"),"conflicting simultaneous statuses");
            Console.WriteLine("PASS 41-ShipmentExceptionAgent (11 focused checks)");
            return 0;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root,true); }
    }
    static void Check(bool value,string name) { if (!value) throw new Exception(name); }
    static async Task Throws(Func<Task> action,string name)
    {
        try { await action(); } catch (InvalidOperationException) { return; }
        throw new Exception("Expected rejection: " + name);
    }
}
