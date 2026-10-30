using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Samples;

namespace PiSharp.Samples.ClientMeetingBriefing;

public static class Workflow
{
    public static WorkflowSpec Create(SampleContext c) => new("14-ClientMeetingBriefing", "Client Meeting Briefing", "Use the tools to derive facts from the supplied business records. Report missing records and limitations honestly.",
        new ToolSpec[] {
            new("fetch_meeting_context", "Inspect source records.", SampleJson.Schema("{\"accountId\":{\"type\":\"string\"}}", "accountId"), (a,t) => FirstAsync(c,a,t)),
            new("build_meeting_brief", "Compute the business result.", SampleJson.Schema("{\"accountId\":{\"type\":\"string\"}}", "accountId"), (a,t) => Task.FromResult<JsonNode?>(Second(c,a))),

        },
        new PlannedCall[] { new("fetch_meeting_context", SampleJson.Object("""{"accountId":"A-4"}""")), new("build_meeting_brief", SampleJson.Object("""{"accountId":"A-4"}""")) });

    static async Task<JsonNode?> FirstAsync(SampleContext c, JsonObject a, CancellationToken t)
{
    var id = J.Str(a,"accountId");
    var accountTask = Task.Run(() => J.Items(c.Input["accounts"]).FirstOrDefault(x => J.Str(x,"id") == id)?.DeepClone(),t);
    var opportunityTask = Task.Run(() => J.Items(c.Input["opportunities"]).Where(x => J.Str(x,"account_id") == id).Select(x => x!.DeepClone()).ToArray(),t);
    var activityTask = Task.Run(() => J.Items(c.Input["activities"]).Where(x => J.Str(x,"account_id") == id).Select(x => x!.DeepClone()).ToArray(),t);
    await Task.WhenAll(accountTask,opportunityTask,activityTask);
    var result = J.Json(new { found = accountTask.Result is not null, account = accountTask.Result, opportunities = opportunityTask.Result, activities = activityTask.Result });
    c.State["meeting_context"] = result?.DeepClone();
    return result;
}
    static JsonNode? Second(SampleContext c, JsonObject a)
    {
var source = c.State["meeting_context"] ?? J.Json(new { found = false });
var account = source?["account"];
if (account is null) return J.Json(new { found = false });
var opportunities = J.Items(source?["opportunities"]);
var activities = J.Items(source?["activities"]);
return J.Json(new { found = true, account = J.Str(account,"name"), owner = J.Str(account,"owner"), pipelineValue = opportunities.Sum(x => J.Num(x,"amount")), openOpportunities = opportunities.Count, latestActivity = activities.OrderBy(x => J.Str(x,"date")).LastOrDefault()?["note"]?.ToString(), discussionTopics = opportunities.Select(x => J.Str(x,"name")).ToArray() });
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
        var c = new SampleContext(Path.Combine(Path.GetTempPath(), "pisharp-sample-selftest-14"), JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject());
        var w = Workflow.Create(c);
        var first = await w.Tools[0].Execute(w.DemoCalls[0].Arguments, CancellationToken.None);
        var second = await w.Tools[1].Execute(w.DemoCalls[1].Arguments, CancellationToken.None);
        if (first is null || second is null) throw new Exception("Tool returned null.");
        if (J.Num(second,"pipelineValue") != 57000m || J.Str(second,"latestActivity") != "Demo completed") throw new Exception("Meeting aggregation failed.");
        Console.WriteLine("PASS 14-ClientMeetingBriefing");
        return 0;
    }
}
