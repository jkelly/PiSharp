using System.Text.Json.Nodes;
namespace PiSharp.Samples.RepositoryOnboarding;
public static class Workflow {
  public static WorkflowSpec Create(SampleContext c) => new("repository-onboarding", "Repository onboarding", "Inspect fixture file inventory and produce a newcomer guide.", [
    new("inspect_layout", "Classify source, tests, docs, and project files", SampleJson.Schema(), async (_, ct) => {
      var files = (c.Input["files"] as JsonArray ?? throw new FormatException("files array missing")).Select(x => x!.GetValue<string>()).ToArray();
      var result = new JsonObject { ["sourceFiles"] = files.Count(x => x.StartsWith("src/", StringComparison.Ordinal)), ["testFiles"] = files.Count(x => x.StartsWith("tests/", StringComparison.Ordinal)), ["projectFiles"] = files.Count(x => x.EndsWith(".csproj", StringComparison.Ordinal)), ["hasReadme"] = files.Contains("README.md"), ["hasArchitectureDoc"] = files.Contains("docs/architecture.md") };
      c.State["layout"] = result.DeepClone(); await c.WriteJsonAsync("layout.json", result, ct); return result;
    }),
    new("write_guide", "Create a guide grounded in inspected files", SampleJson.Schema(), async (_, ct) => {
      var layout = c.State["layout"] as JsonObject ?? throw new InvalidOperationException("Run inspect_layout first.");
      var files = c.Input["files"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
      var source = files.FirstOrDefault(x => x.StartsWith("src/", StringComparison.Ordinal) && x.EndsWith(".cs", StringComparison.Ordinal));
      var test = files.FirstOrDefault(x => x.StartsWith("tests/", StringComparison.Ordinal) && x.EndsWith(".cs", StringComparison.Ordinal));
      var text = $"# New developer guide\n\nSource files: {layout["sourceFiles"]}. Test files: {layout["testFiles"]}.\n\n{c.Input.Text("readme")}\n\nStart in {source ?? "the source directory"}. Review {test ?? "the test directory"} for expected behavior.\n";
      await File.WriteAllTextAsync(c.ResolvePath("ONBOARDING.md"), text, ct); return new JsonObject { ["path"] = "ONBOARDING.md", ["characters"] = text.Length };
    })
  ], [new("inspect_layout", new()), new("write_guide", new())]);
}
