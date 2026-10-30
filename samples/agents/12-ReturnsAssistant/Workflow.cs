using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Samples;

namespace PiSharp.Samples.ReturnsAssistant;

public static class Workflow
{
    public static WorkflowSpec Create(SampleContext c) => new("12-ReturnsAssistant", "Returns Assistant", "Use the tools to derive facts from the supplied business records. Report missing records and limitations honestly.",
        new ToolSpec[] {
            new("check_eligibility", "Inspect source records.", SampleJson.Schema("{\"orderId\":{\"type\":\"string\"}}", "orderId"), (a,t) => FirstAsync(c,a,t)),
            new("calculate_refund", "Compute the business result.", SampleJson.Schema("{\"orderId\":{\"type\":\"string\"}}", "orderId"), (a,t) => Task.FromResult<JsonNode?>(Second(c,a))),

        },
        new PlannedCall[] { new("check_eligibility", SampleJson.Object("""{"orderId":"ORD-220"}""")), new("calculate_refund", SampleJson.Object("""{"orderId":"ORD-220"}""")) });

    static Task<JsonNode?> FirstAsync(SampleContext c, JsonObject a, CancellationToken t) => Task.FromResult<JsonNode?>(First(c,a));
static JsonNode? First(SampleContext c, JsonObject a)
{
var order = J.Items(c.Input["orders"]).FirstOrDefault(x => J.Str(x,"id") == J.Str(a,"orderId"));
if (order is null) return J.Json(new { found = false, eligible = false });
var days = DateOnly.Parse(J.Str(c.Input,"as_of")).DayNumber - DateOnly.Parse(J.Str(order,"delivered")).DayNumber;
var excluded = J.Items(c.Input["policy"]?["excluded_categories"]).Any(x => x?.ToString() == J.Str(order,"category"));
return J.Json(new { found = true, eligible = days >= 0 && days <= J.Int(c.Input["policy"],"window_days") && !excluded, daysSinceDelivery = days, excludedCategory = excluded });
}
    static JsonNode? Second(SampleContext c, JsonObject a)
    {
var order = J.Items(c.Input["orders"]).FirstOrDefault(x => J.Str(x,"id") == J.Str(a,"orderId"));
if (order is null) return J.Json(new { found = false });
var eligible = J.Obj(First(c,a))["eligible"]?.GetValue<bool>() == true;
var restock = J.Str(order,"opened") == "true" ? Math.Round(J.Num(order,"price") * J.Num(c.Input["policy"],"restocking_percent") / 100m,2) : 0m;
var amount = eligible ? J.Num(order,"price") + J.Num(order,"tax") - restock : 0m;
return J.Json(new { found = true, eligible, merchandise = J.Num(order,"price"), tax = J.Num(order,"tax"), nonRefundableShipping = J.Num(order,"shipping"), restockingFee = restock, refund = Math.Round(amount,2) });
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
        var c = new SampleContext(Path.Combine(Path.GetTempPath(), "pisharp-sample-selftest-12"), JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject());
        var w = Workflow.Create(c);
        var first = await w.Tools[0].Execute(w.DemoCalls[0].Arguments, CancellationToken.None);
        var second = await w.Tools[1].Execute(w.DemoCalls[1].Arguments, CancellationToken.None);
        if (first is null || second is null) throw new Exception("Tool returned null.");
        if (J.Num(second,"refund") != 127.39m) throw new Exception("Refund calculation failed.");
        Console.WriteLine("PASS 12-ReturnsAssistant");
        return 0;
    }
}
