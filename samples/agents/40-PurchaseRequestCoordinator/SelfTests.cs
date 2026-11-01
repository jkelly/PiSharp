// PiSharp-only (no upstream Pi source): budget routing, approval, stale payload and local replay checks.
using System.Text.Json.Nodes;
using PiSharp.Samples;
namespace PiSharp.Samples.PurchaseRequestCoordinator;

public static class SelfTests
{
    public static async Task<int> RunAsync(string fixture)
    {
        var root = Path.Combine(Path.GetTempPath(),"pisharp-40-" + Guid.NewGuid().ToString("N"));
        try
        {
            var input = JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject();
            var c = new SampleContext(root,input,approved:true);
            var w = Workflow.Create(c);
            async Task<JsonNode> Call(int i,string id) => (await w.Tools[i].Execute(new() { ["requestId"] = id },default))!;
            JsonObject Submit(string op,long cents = 75000) => new() { ["requestId"] = "PR-100", ["operationId"] = op, ["expectedTotalCents"] = cents };
            Check((await Call(0,"PR-100"))["found"]!.GetValue<bool>(),"inspect");
            var plan = await Call(1,"PR-100");
            Check(Data.Number(plan,"totalCents") == 75000 && Data.Number(plan,"availableCents") == 150000 && plan["approvers"]!.AsArray().Count == 2,"budget and route");
            Check(Data.Text(await Call(1,"PR-200"),"reason") == "budget_exceeded","over-budget second request");
            Check(!(await Call(1,"UNKNOWN"))["found"]!.GetValue<bool>(),"missing request");
            Check(JsonNode.DeepEquals(plan,await Call(1,"PR-100")),"no stale cache");
            var denied = Workflow.Create(new SampleContext(Path.Combine(root,"denied"),input));
            Check(w.Tools[2].RequiresApproval,"approval metadata");
            await Throws(() => denied.Tools[2].Execute(Submit("deny"),default),"direct approval guard");
            Check(!Directory.Exists(Path.Combine(root,"denied","purchases")),"denied writes nothing");
            await Throws(() => w.Tools[2].Execute(Submit("wrong-total",1),default),"stale total");
            var receipt = (await w.Tools[2].Execute(Submit("once"),default))!;
            Check(Data.Text(receipt["result"]!,"status") == "pending_human_approval","submission remains pending");
            var restarted = Workflow.Create(new SampleContext(root,input,approved:true));
            var replay = (await restarted.Tools[2].Execute(Submit("once"),default))!;
            Check(replay["replayed"]!.GetValue<bool>() && Directory.GetFiles(c.ResolvePath("purchases"),"*.json").Length == 1,"persistent idempotency");
            Check(Data.Number(await Call(1,"PR-100"),"availableCents") == 75000,"local commitments reduce budget");
            await Throws(() => w.Tools[2].Execute(Submit("once",75001),default),"same key changed payload");
            await Throws(() => w.Tools[2].Execute(Submit("another"),default),"duplicate request new key");
            foreach (var approver in new[] { "", " \t " })
            {
                var malformed = (JsonObject)input.DeepClone();
                malformed["approvalRules"]![1]!["approvers"] = new JsonArray(JsonValue.Create(approver));
                var malformedContext = new SampleContext(Path.Combine(root,"malformed-route-" + approver.Length),malformed,approved:true);
                var malformedWorkflow = Workflow.Create(malformedContext);
                await Throws(() => malformedWorkflow.Tools[1].Execute(new() { ["requestId"] = "PR-100" },default),"blank approver route rejected");
                await Throws(() => malformedWorkflow.Tools[2].Execute(Submit("blank-approver"),default),"blank approver cannot submit");
                Check(!Directory.Exists(malformedContext.ResolvePath("purchases")),"invalid approver creates no receipt or reservation");
            }
            input["requests"]![0]!["lines"]![0]!["quantity"] = 0L;
            await Throws(() => Call(1,"PR-100"),"invalid line quantity");
            Console.WriteLine("PASS 40-PurchaseRequestCoordinator (21 focused checks)");
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
