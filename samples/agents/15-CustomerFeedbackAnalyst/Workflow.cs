// PiSharp-only (no upstream Pi source): agent sample implementation and authored offline verification.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Samples;

namespace PiSharp.Samples.CustomerFeedbackAnalyst;

public static class Workflow
{
    public static WorkflowSpec Create(SampleContext c) => new("15-CustomerFeedbackAnalyst", "Customer Feedback Analyst", "Use the tools to derive facts from the supplied business records. Report missing records and limitations honestly.",
        new ToolSpec[] {
            new("analyze_themes", "Inspect source records.", SampleJson.Schema(), (a,t) => FirstAsync(c,a,t)),
            new("rank_themes", "Compute the business result.", SampleJson.Schema(), (a,t) => Task.FromResult<JsonNode?>(Second(c,a))),

        },
        new PlannedCall[] { new("analyze_themes", SampleJson.Object("""{}""")), new("rank_themes", SampleJson.Object("""{}""")) });

    static Task<JsonNode?> FirstAsync(SampleContext c, JsonObject a, CancellationToken t) => Task.FromResult<JsonNode?>(First(c,a));
static JsonNode? First(SampleContext c, JsonObject a)
{
var themes = J.Obj(c.Input["themes"]);
var feedback = J.Items(c.Input["feedback"]);
var rows = themes.Select(kv => new { theme = kv.Key, matches = feedback.Where(f => J.Items(kv.Value).Any(word => J.Str(f,"text").Contains(word!.ToString(),StringComparison.OrdinalIgnoreCase))).Select(f => new { id = J.Str(f,"id"), severity = J.Int(f,"severity") }).ToArray() }).Select(x => new { x.theme, count = x.matches.Length, severityTotal = x.matches.Sum(m => m.severity), ids = x.matches.Select(m => m.id).ToArray() }).ToArray();
c.State["theme_analysis"] = J.Json(new { themes = rows });
return c.State["theme_analysis"]?.DeepClone();
}
    static JsonNode? Second(SampleContext c, JsonObject a)
    {
var analysis = c.State["theme_analysis"] ?? First(c,a);
var ranked = J.Items(analysis?["themes"]).Select(x => new { theme = J.Str(x,"theme"), count = J.Int(x,"count"), score = J.Int(x,"severityTotal") }).OrderByDescending(x => x.score).ThenByDescending(x => x.count).ToArray();
return J.Json(new { ranked, topTheme = ranked.FirstOrDefault()?.theme, reviewed = J.Items(c.Input["feedback"]).Count });
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
        var c = new SampleContext(Path.Combine(Path.GetTempPath(), "pisharp-sample-selftest-15"), JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject());
        var w = Workflow.Create(c);
        var first = await w.Tools[0].Execute(w.DemoCalls[0].Arguments, CancellationToken.None);
        var second = await w.Tools[1].Execute(w.DemoCalls[1].Arguments, CancellationToken.None);
        if (first is null || second is null) throw new Exception("Tool returned null.");
        if (J.Str(second,"topTheme") != "checkout" || J.Int(second,"reviewed") != 5) throw new Exception("Feedback ranking failed.");
        Console.WriteLine("PASS 15-CustomerFeedbackAnalyst");
        return 0;
    }
}
