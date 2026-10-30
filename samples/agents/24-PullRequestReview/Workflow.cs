using System.Text.Json.Nodes;
namespace PiSharp.Samples.PullRequestReview;
public static class Workflow {
  public static WorkflowSpec Create(SampleContext c) => new("pull-request-review", "Pull request review", "Inspect added lines and produce evidence-linked findings.", [
    new("scan_diff", "Identify secret-shaped literals and unchecked dereferences", SampleJson.Schema(), async (_, ct) => {
      var findings = new JsonArray(); var lines = c.Input.Text("diff").Split('\n');
      for (int i = 0; i < lines.Length; i++) { ct.ThrowIfCancellationRequested(); var line = lines[i]; if (!line.StartsWith('+')) continue;
        if (line.Contains("sk_live_", StringComparison.Ordinal)) findings.Add(new JsonObject { ["line"] = i+1, ["severity"] = "high", ["issue"] = "Secret-shaped literal added" });
        if (line.Contains("user.Name.", StringComparison.Ordinal)) findings.Add(new JsonObject { ["line"] = i+1, ["severity"] = "medium", ["issue"] = "Unchecked user/name dereference" });
      }
      c.State["findings"] = findings.DeepClone(); return new JsonObject { ["findingCount"] = findings.Count, ["findings"] = findings };
    }),
    new("write_review", "Write structured review with file and line evidence", SampleJson.Schema(), async (_, ct) => {
      var findings = c.State["findings"]?.DeepClone() ?? throw new InvalidOperationException("Run scan_diff first."); var result = new JsonObject { ["file"] = c.Input.Text("changedFile"), ["findings"] = findings }; await c.WriteJsonAsync("review.json", result, ct); return result;
    })
  ], [new("scan_diff", new()), new("write_review", new())]);
}
