using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Samples;

namespace PiSharp.Samples.OrderStatusConcierge;

public static class Workflow
{
    public static WorkflowSpec Create(SampleContext c) => new("11-OrderStatusConcierge", "Order Status Concierge", "Use the tools to derive facts from the supplied business records. Report missing records and limitations honestly.",
        new ToolSpec[] {
            new("lookup_order", "Inspect source records.", SampleJson.Schema("{\"orderId\":{\"type\":\"string\"}}", "orderId"), (a,t) => FirstAsync(c,a,t)),
            new("estimate_delivery", "Compute the business result.", SampleJson.Schema("{\"orderId\":{\"type\":\"string\"}}", "orderId"), (a,t) => Task.FromResult<JsonNode?>(Second(c,a))),

        },
        new PlannedCall[] { new("lookup_order", SampleJson.Object("""{"orderId":"ORD-1042"}""")), new("estimate_delivery", SampleJson.Object("""{"orderId":"ORD-1042"}""")) });

    static Task<JsonNode?> FirstAsync(SampleContext c, JsonObject a, CancellationToken t) => Task.FromResult<JsonNode?>(First(c,a));
static JsonNode? First(SampleContext c, JsonObject a)
{
var order = J.Items(c.Input["orders"]).FirstOrDefault(x => J.Str(x,"id") == J.Str(a,"orderId"));
if (order is null) return J.Json(new { found = false, orderId = J.Str(a,"orderId") });
return J.Json(new { found = true, id = J.Str(order,"id"), customer = J.Str(order,"customer"), status = J.Str(order,"status"), carrier = J.Str(order,"carrier"), items = J.Items(order["items"]).Select(x => x!.ToString()).ToArray(), lastScan = J.Items(order["events"]).Select(x => J.Str(x,"date")).OrderBy(x => x).LastOrDefault() });
}
    static JsonNode? Second(SampleContext c, JsonObject a)
    {
var order = J.Items(c.Input["orders"]).FirstOrDefault(x => J.Str(x,"id") == J.Str(a,"orderId"));
if (order is null) return J.Json(new { found = false });
var shipped = J.Str(order,"shipped");
if (!DateOnly.TryParse(shipped, out var date)) return J.Json(new { found = true, estimateAvailable = false, reason = "Order has not shipped" });
var eta = date.AddDays(J.Int(order,"transit_days"));
var latest = J.Items(order["events"]).Select(x => J.Str(x,"date")).OrderBy(x => x).LastOrDefault();
var asOf = DateOnly.Parse(J.Str(c.Input,"as_of"));
return J.Json(new { found = true, estimateAvailable = true, estimatedDelivery = eta.ToString("yyyy-MM-dd"), lastScan = latest, overdueAsOfFixture = eta < asOf });
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
        var c = new SampleContext(Path.Combine(Path.GetTempPath(), "pisharp-sample-selftest-11"), JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject());
        var w = Workflow.Create(c);
        var first = await w.Tools[0].Execute(w.DemoCalls[0].Arguments, CancellationToken.None);
        var second = await w.Tools[1].Execute(w.DemoCalls[1].Arguments, CancellationToken.None);
        if (first is null || second is null) throw new Exception("Tool returned null.");
        if (J.Str(second,"estimatedDelivery") != "2026-10-31") throw new Exception("Delivery estimate was not calculated from transit days.");
        Console.WriteLine("PASS 11-OrderStatusConcierge");
        return 0;
    }
}
