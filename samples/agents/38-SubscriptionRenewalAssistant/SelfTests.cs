// PiSharp-only (no upstream Pi source): deterministic renewal calculations and review-gate checks.
using System.Text.Json.Nodes;
using PiSharp.Samples;
namespace PiSharp.Samples.SubscriptionRenewalAssistant;

public static class SelfTests
{
    public static async Task<int> RunAsync(string fixture)
    {
        var root = Path.Combine(Path.GetTempPath(),"pisharp-38-" + Guid.NewGuid().ToString("N"));
        try
        {
            var input = JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject();
            var c = new SampleContext(root,input,approved:true);
            var w = Workflow.Create(c);
            async Task<JsonNode> Call(int i,string id) => (await w.Tools[i].Execute(new() { ["subscriptionId"] = id },default))!;
            var window = await Call(0,"SUB-CRM");
            Check(window["daysUntilRenewal"]!.GetValue<double>() == 44 && window["noticeWindowOpen"]!.GetValue<bool>(),"renewal window");
            var cost = await Call(1,"SUB-CRM");
            Check(Data.Number(cost,"quotedAnnualCents") == 264000 && Data.Number(cost,"proposedAnnualCents") == 158400 && Data.Number(cost,"savingsAgainstQuoteCents") == 105600,"integer-cent comparison");
            Check(cost["utilizationPercent"]!.GetValue<decimal>() == 60 && Data.Text(cost,"recommendation") == "request_seat_reduction","seat utilization");
            var late = await Call(1,"SUB-DESIGN");
            Check(!late["canChangeWithinNotice"]!.GetValue<bool>() && Data.Text(late,"recommendation") == "negotiate_late_change_with_vendor","late notice");
            Check(!(await Call(1,"UNKNOWN"))["found"]!.GetValue<bool>(),"missing ID");
            Check(JsonNode.DeepEquals(cost,await Call(1,"SUB-CRM")),"multi-ID calls have no stale cache");
            Check(w.Tools[2].RequiresApproval,"approval metadata");
            var denied = Workflow.Create(new SampleContext(Path.Combine(root,"denied"),input));
            await Throws(() => denied.Tools[2].Execute(new() { ["subscriptionId"] = "SUB-CRM" },default),"callback approval guard");
            Check(!File.Exists(Path.Combine(root,"denied","renewals","SUB-CRM.json")),"denied writes nothing");
            await Call(2,"SUB-CRM");
            var path = c.ResolvePath("renewals/SUB-CRM.json");
            var first = await File.ReadAllTextAsync(path);
            await Call(2,"SUB-CRM");
            Check(first == await File.ReadAllTextAsync(path),"repeat report is deterministic");
            var saved = JsonNode.Parse(first)!;
            Check(saved["hostReviewed"]!.GetValue<bool>() && !saved["externalActionTaken"]!.GetValue<bool>(),"review scope");
            input["subscriptions"]![0]!["activeSeats"] = 21L;
            await Throws(() => Call(1,"SUB-CRM"),"inconsistent usage rejected");
            input["subscriptions"]![0]!["activeSeats"] = 12L;
            input["subscriptions"]!.AsArray().Add(input["subscriptions"]![0]!.DeepClone());
            await Throws(() => Call(1,"SUB-CRM"),"duplicate IDs rejected");
            Console.WriteLine("PASS 38-SubscriptionRenewalAssistant (13 focused checks)");
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
