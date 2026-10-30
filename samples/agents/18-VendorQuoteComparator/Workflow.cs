using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Samples;

namespace PiSharp.Samples.VendorQuoteComparator;

public static class Workflow
{
    public static WorkflowSpec Create(SampleContext c) => new("18-VendorQuoteComparator", "Vendor Quote Comparator", "Use the tools to derive facts from the supplied business records. Report missing records and limitations honestly.",
        new ToolSpec[] {
            new("normalize_quotes", "Inspect source records.", SampleJson.Schema(), (a,t) => FirstAsync(c,a,t)),
            new("rank_quotes", "Compute the business result.", SampleJson.Schema(), (a,t) => Task.FromResult<JsonNode?>(Second(c,a))),

        },
        new PlannedCall[] { new("normalize_quotes", SampleJson.Object("""{}""")), new("rank_quotes", SampleJson.Object("""{}""")) });

    static Task<JsonNode?> FirstAsync(SampleContext c, JsonObject a, CancellationToken t) => Task.FromResult<JsonNode?>(First(c,a));
static JsonNode? First(SampleContext c, JsonObject a)
{
var quantity = J.Int(c.Input["request"],"quantity");
var rows = J.Items(c.Input["quotes"]).Select(x => new { vendor = J.Str(x,"vendor"), unitPrice = J.Num(x,"unit_price"), shipping = J.Num(x,"shipping"), total = J.Num(x,"unit_price")*quantity+J.Num(x,"shipping"), leadDays = J.Int(x,"lead_days") }).ToArray();
var result = J.Json(new { quantity, quotes = rows }); c.State["normalized_quotes"] = result?.DeepClone(); return result;
}
    static JsonNode? Second(SampleContext c, JsonObject a)
    {
var normalized = c.State["normalized_quotes"] ?? First(c,a);
var maxLead = J.Int(c.Input["request"],"max_lead_days");
var quotes = J.Items(normalized?["quotes"]).Select(x => new { vendor = J.Str(x,"vendor"), total = J.Num(x,"total"), leadDays = J.Int(x,"leadDays"), eligible = J.Int(x,"leadDays") <= maxLead }).ToArray();
var eligible = quotes.Where(x => x.eligible).OrderBy(x => x.total).ThenBy(x => x.leadDays).ToArray();
return J.Json(new { ranked = eligible, excluded = quotes.Where(x => !x.eligible).Select(x => x.vendor).ToArray(), recommendedVendor = eligible.FirstOrDefault()?.vendor, recommendedTotal = eligible.FirstOrDefault()?.total });
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
        var c = new SampleContext(Path.Combine(Path.GetTempPath(), "pisharp-sample-selftest-18"), JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject());
        var w = Workflow.Create(c);
        var first = await w.Tools[0].Execute(w.DemoCalls[0].Arguments, CancellationToken.None);
        var second = await w.Tools[1].Execute(w.DemoCalls[1].Arguments, CancellationToken.None);
        if (first is null || second is null) throw new Exception("Tool returned null.");
        if (J.Str(second,"recommendedVendor") != "Deskly" || J.Num(second,"recommendedTotal") != 1720m) throw new Exception("Quote ranking failed.");
        Console.WriteLine("PASS 18-VendorQuoteComparator");
        return 0;
    }
}
