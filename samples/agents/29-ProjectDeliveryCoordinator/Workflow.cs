using System.Text.Json.Nodes;
namespace PiSharp.Samples.ProjectDeliveryCoordinator;
public static class Workflow {
  static WorkflowSpec Status(SampleContext c) => new("delivery-status-child", "Status analyst", "Calculate task completion from fixture.", [
    new("count_tasks", "Count completed and pending tasks", SampleJson.Schema(), async (_, ct) => {
      var tasks = c.Input["tasks"]!.AsArray(); var completed = tasks.Count(x => x!["done"]!.GetValue<bool>()); var result = new JsonObject { ["completed"] = completed, ["pending"] = tasks.Count - completed, ["total"] = tasks.Count };
      await c.WriteJsonAsync("status.json", result, ct); return result;
    })
  ], [new("count_tasks", new())]);
  static WorkflowSpec Risk(SampleContext c) => new("delivery-risk-child", "Risk analyst", "Inspect declared risks and blockers.", [
    new("rank_risks", "Extract risk and dependency counts", SampleJson.Schema(), async (_, ct) => {
      var risks = c.Input["risks"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray(); var dependencies = risks.Count(x => x.Contains("until", StringComparison.OrdinalIgnoreCase) || x.Contains("depends", StringComparison.OrdinalIgnoreCase));
      var result = new JsonObject { ["riskCount"] = risks.Length, ["dependencyCount"] = dependencies, ["risks"] = new JsonArray(risks.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()) };
      await c.WriteJsonAsync("risks.json", result, ct); return result;
    })
  ], [new("rank_risks", new())]);
  public static WorkflowSpec Create(SampleContext c) => new("project-delivery-coordinator", "Project delivery coordinator", "Run status and risk analysts as separate PiSharp Agent instances concurrently, then combine their reports.", [
    new("dispatch_analysts", "Run independent status and risk Agents concurrently", SampleJson.Schema(), async (_, ct) => {
      var statusContext = new SampleContext(c.ResolvePath("status"), (JsonObject)c.Input.DeepClone(), c.Approved, c.Live) { Model = c.Model, Provider = c.Provider, Api = c.Api, BaseUrl = c.BaseUrl, KeyEnvironment = c.KeyEnvironment };
      var riskContext = new SampleContext(c.ResolvePath("risk"), (JsonObject)c.Input.DeepClone(), c.Approved, c.Live) { Model = c.Model, Provider = c.Provider, Api = c.Api, BaseUrl = c.BaseUrl, KeyEnvironment = c.KeyEnvironment };
      var statusTask = SampleHost.ExecuteAsync(Status(statusContext), statusContext, ct); var riskTask = SampleHost.ExecuteAsync(Risk(riskContext), riskContext, ct);
      await Task.WhenAll(statusTask, riskTask); var status = await statusTask; var risk = await riskTask;
      if (!status.Success || !risk.Success) throw new InvalidOperationException("One child Agent failed; inspect status/report.json and risk/report.json.");
      c.State["status"] = status.Report.DeepClone(); c.State["risk"] = risk.Report.DeepClone();
      return new JsonObject { ["statusAgent"] = status.Success, ["riskAgent"] = risk.Success, ["parallel"] = true };
    }),
    new("synthesize_delivery", "Combine independent analyst outputs", SampleJson.Schema(), async (_, ct) => {
      var status = c.State["status"] as JsonObject ?? throw new InvalidOperationException("Run dispatch_analysts first."); var risk = c.State["risk"] as JsonObject ?? throw new InvalidOperationException("Run dispatch_analysts first.");
      var statusData = status["outputs"]!.AsArray()[0]!["result"]!.AsObject(); var riskData = risk["outputs"]!.AsArray()[0]!["result"]!.AsObject();
      var result = new JsonObject { ["project"] = c.Input.Text("project"), ["completed"] = statusData["completed"]!.DeepClone(), ["pending"] = statusData["pending"]!.DeepClone(), ["riskCount"] = riskData["riskCount"]!.DeepClone(), ["dependencyCount"] = riskData["dependencyCount"]!.DeepClone() };
      await c.WriteJsonAsync("delivery.json", result, ct); return result;
    })
  ], [new("dispatch_analysts", new()), new("synthesize_delivery", new())]);
}
