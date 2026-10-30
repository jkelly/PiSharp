using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Samples;

namespace PiSharp.Samples.InventoryReorder;

public static class Workflow
{
    public static WorkflowSpec Create(SampleContext c) => new("19-InventoryReorder", "Inventory Reorder", "Use the tools to derive facts from the supplied business records. Report missing records and limitations honestly.",
        new ToolSpec[] {
            new("calculate_stock_gaps", "Inspect source records.", SampleJson.Schema(), (a,t) => FirstAsync(c,a,t)),
            new("build_reorder_plan", "Compute the business result.", SampleJson.Schema(), (a,t) => Task.FromResult<JsonNode?>(Second(c,a))),

        },
        new PlannedCall[] { new("calculate_stock_gaps", SampleJson.Object("""{}""")), new("build_reorder_plan", SampleJson.Object("""{}""")) });

    static Task<JsonNode?> FirstAsync(SampleContext c, JsonObject a, CancellationToken t) => Task.FromResult<JsonNode?>(First(c,a));
static JsonNode? First(SampleContext c, JsonObject a)
{
var gaps = J.Items(c.Input["products"]).Select(x => { var point = J.Int(x,"daily_demand") * J.Int(x,"lead_days") + J.Int(x,"safety_stock"); var available = J.Int(x,"on_hand") + J.Int(x,"on_order"); return new { sku = J.Str(x,"sku"), reorderPoint = point, available, gap = Math.Max(0,point-available), unitCost = J.Num(x,"unit_cost") }; }).ToArray();
var result = J.Json(new { gaps }); c.State["stock_gaps"] = result?.DeepClone(); return result;
}
    static JsonNode? Second(SampleContext c, JsonObject a)
    {
var gaps = J.Items((c.State["stock_gaps"] ?? First(c,a))?["gaps"]);
var budget = J.Num(c.Input,"budget"); var remaining = budget;
var lines = new List<object>();
var ordered = new Dictionary<string,int>(StringComparer.Ordinal);
foreach (var gap in gaps.OrderByDescending(x => J.Int(x,"gap"))) { var unit = J.Num(gap,"unitCost"); var quantity = unit > 0 ? Math.Min(J.Int(gap,"gap"),(int)Math.Floor(remaining/unit)) : 0; if (quantity <= 0) continue; var cost = quantity*unit; remaining -= cost; var sku = J.Str(gap,"sku"); ordered[sku] = quantity; lines.Add(new { sku, quantity, cost }); }
return J.Json(new { budget, spent = budget-remaining, remaining, lines, unfilledSkus = gaps.Where(x => J.Int(x,"gap") > ordered.GetValueOrDefault(J.Str(x,"sku"))).Select(x => J.Str(x,"sku")).ToArray() });
    }
}

internal static class J
{
    public static JsonArray Items(JsonNode? n) => n as JsonArray ?? new JsonArray();
    public static JsonObject Obj(JsonNode? n) => n as JsonObject ?? new JsonObject();
    public static string Str(JsonNode? n,string key) => n?[key]?.ToString() ?? "";
    public static decimal Num(JsonNode? n,string key) => decimal.TryParse(Str(n,key),NumberStyles.Number,CultureInfo.InvariantCulture,out var x) ? x : 0m;
    public static int Int(JsonNode? n,string key) => int.TryParse(Str(n,key),NumberStyles.Integer,CultureInfo.InvariantCulture,out var x) ? x : 0;
    public static JsonNode? Json(object o) => JsonSerializer.SerializeToNode(o);
}

public static class SelfTests
{
    public static async Task<int> RunAsync(string fixture)
    {
        var c = new SampleContext(Path.Combine(Path.GetTempPath(), "pisharp-sample-selftest-19"), JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject());
        var w = Workflow.Create(c);
        var first = await w.Tools[0].Execute(w.DemoCalls[0].Arguments, CancellationToken.None);
        var second = await w.Tools[1].Execute(w.DemoCalls[1].Arguments, CancellationToken.None);
        if (first is null || second is null) throw new Exception("Tool returned null.");
        if (J.Num(second,"spent") != 217m || J.Items(second?["lines"]).Count != 2) throw new Exception("Reorder plan failed.");
        Console.WriteLine("PASS 19-InventoryReorder");
        return 0;
    }
}
