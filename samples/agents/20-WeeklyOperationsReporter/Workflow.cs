// PiSharp-only (no upstream Pi source): agent sample implementation and authored offline verification.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Samples;

namespace PiSharp.Samples.WeeklyOperationsReporter;

public static class Workflow
{
    public static WorkflowSpec Create(SampleContext c) => new("20-WeeklyOperationsReporter", "Weekly Operations Reporter", "Use the tools to derive facts from the supplied business records. Report missing records and limitations honestly.",
        new ToolSpec[] {
            new("fetch_week_metrics", "Inspect source records.", SampleJson.Schema("{\"week\":{\"type\":\"string\"}}", "week"), (a,t) => FirstAsync(c,a,t)),
            new("calculate_week_report", "Compute the business result.", SampleJson.Schema("{\"week\":{\"type\":\"string\"}}", "week"), (a,t) => Task.FromResult<JsonNode?>(Second(c,a))),

        },
        new PlannedCall[] { new("fetch_week_metrics", SampleJson.Object("""{"week":"2026-W43"}""")), new("calculate_week_report", SampleJson.Object("""{"week":"2026-W43"}""")) });

    static async Task<JsonNode?> FirstAsync(SampleContext c, JsonObject a, CancellationToken t)
{
    c.State.Remove("weekly_metrics");
    var week = J.Items(c.Input["weeks"]).FirstOrDefault(x => J.Str(x,"label") == J.Str(a,"week"));
    if (week is null) return J.Json(new { found = false });
    var salesTask = Task.Run(() => J.Items(week["sales"]).Sum(x => decimal.Parse(x!.ToString(),CultureInfo.InvariantCulture)),t);
    var supportTask = Task.Run(() => new { tickets = J.Items(week["tickets"]).Sum(x => int.Parse(x!.ToString(),CultureInfo.InvariantCulture)), resolved = J.Items(week["resolved"]).Sum(x => int.Parse(x!.ToString(),CultureInfo.InvariantCulture)) },t);
    var fulfillmentTask = Task.Run(() => new { shipped = J.Items(week["shipped"]).Sum(x => int.Parse(x!.ToString(),CultureInfo.InvariantCulture)), onTime = J.Items(week["on_time"]).Sum(x => int.Parse(x!.ToString(),CultureInfo.InvariantCulture)) },t);
    await Task.WhenAll(salesTask,supportTask,fulfillmentTask);
    var result = J.Json(new { found = true, week = J.Str(week,"label"), sales = salesTask.Result, support = supportTask.Result, fulfillment = fulfillmentTask.Result });
    c.State["weekly_metrics"] = result?.DeepClone(); return result;
}
    static JsonNode? Second(SampleContext c, JsonObject a)
    {
var metrics = c.State["weekly_metrics"];
if (metrics?["found"]?.GetValue<bool>() != true) return J.Json(new { found = false, reason = "Fetch weekly metrics first" });
if (J.Str(metrics,"week") != J.Str(a,"week")) return J.Json(new { found = false, reason = "Fetch metrics for the requested week first" });
var tickets = J.Int(metrics["support"],"tickets"); var resolved = J.Int(metrics["support"],"resolved");
var shipped = J.Int(metrics["fulfillment"],"shipped"); var onTime = J.Int(metrics["fulfillment"],"onTime");
return J.Json(new { found = true, week = J.Str(metrics,"week"), revenue = J.Num(metrics,"sales"), resolutionRate = tickets == 0 ? 0m : Math.Round((decimal)resolved/tickets,4), onTimeRate = shipped == 0 ? 0m : Math.Round((decimal)onTime/shipped,4), unresolvedTickets = tickets-resolved });
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
        var c = new SampleContext(Path.Combine(Path.GetTempPath(), "pisharp-sample-selftest-20"), JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject());
        var w = Workflow.Create(c);
        var first = await w.Tools[0].Execute(w.DemoCalls[0].Arguments, CancellationToken.None);
        var second = await w.Tools[1].Execute(w.DemoCalls[1].Arguments, CancellationToken.None);
        if (first is null || second is null) throw new Exception("Tool returned null.");
        if (J.Num(second,"revenue") != 515m || J.Num(second,"resolutionRate") != 0.9062m) throw new Exception("Weekly metric calculation failed.");
        var other = SampleJson.Object("""{"week":"2026-W42"}""");
        var mismatched = await w.Tools[1].Execute(other, CancellationToken.None);
        if (mismatched?["found"]?.GetValue<bool>() != false) throw new Exception("Report reused another week's metrics.");
        await w.Tools[0].Execute(other, CancellationToken.None);
        var otherReport = await w.Tools[1].Execute(other, CancellationToken.None);
        if (J.Str(otherReport,"week") != "2026-W42" || J.Num(otherReport,"revenue") != 450m) throw new Exception("Second week report was not isolated.");
        var missing = SampleJson.Object("""{"week":"missing"}""");
        await w.Tools[0].Execute(missing, CancellationToken.None);
        var missingReport = await w.Tools[1].Execute(missing, CancellationToken.None);
        var afterMissing = await w.Tools[1].Execute(other, CancellationToken.None);
        if (missingReport?["found"]?.GetValue<bool>() != false || afterMissing?["found"]?.GetValue<bool>() != false) throw new Exception("Missing week retained previous metrics.");
        Console.WriteLine("PASS 20-WeeklyOperationsReporter");
        return 0;
    }
}
