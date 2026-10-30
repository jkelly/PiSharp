using System.Text.Json.Nodes;
namespace PiSharp.Samples.BugInvestigation;
public static class Workflow {
  public static WorkflowSpec Create(SampleContext c) => new("bug-investigation", "Bug investigation", "Find boundary bug, patch a copy, and verify fixture cases.", [
    new("reproduce", "Run boundary cases against supplied source expression", SampleJson.Schema(), async (_, ct) => {
      var source = c.Input.Text("source"); if (!source.Contains("age > 18", StringComparison.Ordinal)) throw new FormatException("Unsupported fixture expression.");
      var failures = new JsonArray(); foreach (var item in c.Input["cases"]!.AsArray()) { var age = (int)item!["age"]!; var expected = (bool)item["expected"]!; if ((age > 18) != expected) failures.Add(age); }
      var result = new JsonObject { ["failingAges"] = failures, ["count"] = failures.Count }; c.State["reproduction"] = result.DeepClone(); await c.WriteJsonAsync("reproduction.json", result, ct); return result;
    }),
    new("patch_and_verify", "Patch expression and rerun all boundary cases", SampleJson.Schema(), async (_, ct) => {
      if (c.State["reproduction"] is null) throw new InvalidOperationException("Run reproduce first.");
      var patched = c.Input.Text("source").Replace("age > 18", "age >= 18", StringComparison.Ordinal);
      var passed = c.Input["cases"]!.AsArray().All(item => ((int)item!["age"]! >= 18) == (bool)item["expected"]!);
      await File.WriteAllTextAsync(c.ResolvePath("Fixed.cs"), patched + "\n", ct); return new JsonObject { ["passed"] = passed, ["patchedSource"] = patched };
    })
  ], [new("reproduce", new()), new("patch_and_verify", new())]);
}
