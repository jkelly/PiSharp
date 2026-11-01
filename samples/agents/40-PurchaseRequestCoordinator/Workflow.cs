// PiSharp-only (no upstream Pi source): budget-aware approval routing and local submission receipts.
using System.Text.Json.Nodes;
using PiSharp.Samples;
namespace PiSharp.Samples.PurchaseRequestCoordinator;

public static class Workflow
{
    // The sample host executes sequentially; this also protects direct concurrent callbacks in one process.
    static readonly SemaphoreSlim MutationGate = new(1,1);
    public static WorkflowSpec Create(SampleContext c) => new("40-PurchaseRequestCoordinator", "Purchase Request Coordinator",
        "Inspect the requested purchase, evaluate its integer-cent total against the department budget and approval rules, then submit the eligible local request using the supplied operationId and exact expectedTotalCents. Host approval permits only this workspace submission; it does not approve expenditure or contact procurement.",
        [new("inspect_purchase", "Inspect an exact request ID and its department budget.", Schema(), (a,t) => Task.FromResult<JsonNode?>(Inspect(c,a))),
         new("evaluate_purchase", "Calculate line totals, remaining budget and the configured approver route.", Schema(), (a,t) => Task.FromResult<JsonNode?>(Evaluate(c,a))),
         new("submit_purchase", "Create an idempotent local pending-approval request after checking budget and total again.",
             SampleJson.Schema("""{"requestId":{"type":"string"},"operationId":{"type":"string"},"expectedTotalCents":{"type":"integer"}}""","requestId","operationId","expectedTotalCents"), SubmitAsync(c), RequiresApproval: true)],
        [new("inspect_purchase",Args("PR-100")),new("evaluate_purchase",Args("PR-100")),new("submit_purchase",new() { ["requestId"] = "PR-100", ["operationId"] = "purchase-pr100-v1", ["expectedTotalCents"] = 75000 })]);
    static JsonObject Schema() => SampleJson.Schema("""{"requestId":{"type":"string"}}""","requestId");
    static JsonObject Args(string id) => new() { ["requestId"] = id };
    static JsonObject? Request(SampleContext c, JsonObject a) => Data.Find(c.Input["requests"],Data.Id(a,"requestId"));
    static JsonObject Budget(SampleContext c, JsonObject r) => Data.Find(c.Input["budgets"],Data.Text(r,"department")) ?? throw new InvalidOperationException("Department budget missing.");
    static JsonObject[] Receipts(SampleContext c)
    {
        var dir = Path.GetDirectoryName(c.ResolvePath("purchases/probe.json"))!;
        return !Directory.Exists(dir) ? [] : Directory.GetFiles(dir,"*.json").Select(p =>
            JsonNode.Parse(File.ReadAllText(c.ResolvePath("purchases/" + Path.GetFileName(p))))!.AsObject()).ToArray();
    }
    static JsonNode Inspect(SampleContext c, JsonObject a)
    {
        var r = Request(c,a);
        return r is null ? Data.Missing(Data.Id(a,"requestId")) : Data.Json(new { found = true, request = r.DeepClone(), budget = Budget(c,r).DeepClone() });
    }
    static JsonNode Evaluate(SampleContext c, JsonObject a)
    {
        var r = Request(c,a);
        if (r is null) return Data.Missing(Data.Id(a,"requestId"));
        var b = Budget(c,r);
        var lines = Data.Rows(r["lines"]).ToArray();
        if (lines.Length == 0) throw new InvalidOperationException("Purchase requires at least one line.");
        var total = lines.Aggregate(0L,(sum,l) => checked(sum + checked(Data.Number(l,"quantity",1) * Data.Number(l,"unitPriceCents",1))));
        var department = Data.Text(r,"department");
        var receipts = Receipts(c);
        var committed = receipts.Where(x => Data.Text(x["result"]!,"department") == department).Sum(x => Data.Number(x["result"]!,"totalCents"));
        var available = checked(Data.Number(b,"allocatedCents") - Data.Number(b,"spentCents") - committed);
        var rules = Data.Rows(c.Input["approvalRules"]).OrderBy(x => Data.Number(x,"maximumCents")).ToArray();
        var route = rules.FirstOrDefault(x => total <= Data.Number(x,"maximumCents"));
        var approvers = route is null ? [] : Data.Strings(route["approvers"]);
        if (approvers.Any(string.IsNullOrWhiteSpace)) throw new InvalidOperationException("Approval route requires nonempty approver IDs.");
        var alreadySubmitted = receipts.Any(x => Data.Text(x["result"]!,"requestId") == Data.Text(r,"id"));
        return Data.Json(new { found = true, requestId = Data.Text(r,"id"), department, totalCents = total,
            availableCents = available, remainingCents = available-total, approvers,
            eligible = total <= available && approvers.Length > 0 && !alreadySubmitted, alreadySubmitted,
            reason = alreadySubmitted ? "already_submitted" : total > available ? "budget_exceeded" : approvers.Length == 0 ? "no_approval_route" : "ready_for_local_submission" });
    }
    static Func<JsonObject,CancellationToken,Task<JsonNode?>> SubmitAsync(SampleContext c) => async (a,token) =>
    {
        Data.Approved(c);
        await MutationGate.WaitAsync(token);
        try
        {
            var requestId = Data.Id(a,"requestId");
            var operation = Data.Id(a,"operationId");
            var expected = Data.Number(a,"expectedTotalCents",1);
            // Bind the operation to the full source request, not only a stale total.
            var source = Request(c,a) ?? throw new InvalidOperationException("Request not found.");
            var payload = new JsonObject { ["requestId"] = requestId, ["expectedTotalCents"] = expected, ["source"] = source.DeepClone() };
            var path = "purchases/" + operation + ".json";
            var existing = await Data.ExistingAsync(c,path,payload,token);
            if (existing is not null) return existing;
            var evaluation = Evaluate(c,a);
            if (Data.Number(evaluation,"totalCents",1) != expected) throw new InvalidOperationException("Expected total is stale.");
            if (evaluation["eligible"]?.GetValue<bool>() != true) throw new InvalidOperationException("Submission blocked: " + Data.Text(evaluation,"reason"));
            var result = new JsonObject { ["requestId"] = requestId, ["department"] = Data.Text(source,"department"),
                ["totalCents"] = expected, ["approvers"] = evaluation["approvers"]!.DeepClone(), ["status"] = "pending_human_approval", ["path"] = path };
            return await Data.CreateReceiptAsync(c,path,payload,result,token);
        }
        finally { MutationGate.Release(); }
    };
}
