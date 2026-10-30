using System.Text.Json.Nodes;
namespace PiSharp.Samples.ReleaseNotes;
public static class Workflow {
  public static WorkflowSpec Create(SampleContext c) => new("release-notes", "Release notes", "Classify conventional commits and draft release notes.", [
    new("classify_commits", "Group feat, fix, docs, and other commits", SampleJson.Schema(), async (_, ct) => {
      var groups = new JsonObject { ["features"] = new JsonArray(), ["fixes"] = new JsonArray(), ["docs"] = new JsonArray(), ["internal"] = new JsonArray() };
      foreach (var item in c.Input["commits"]!.AsArray()) { ct.ThrowIfCancellationRequested(); var text = item!.GetValue<string>(); var pair = text.Split(':', 2); var key = pair[0] switch { "feat" => "features", "fix" => "fixes", "docs" => "docs", _ => "internal" }; groups[key]!.AsArray().Add(pair.Length == 2 ? pair[1].Trim() : text); }
      c.State["groups"] = groups.DeepClone(); await c.WriteJsonAsync("groups.json", groups, ct); return groups;
    }),
    new("draft_notes", "Render categorized Markdown notes", SampleJson.Schema(), async (_, ct) => {
      var groups = c.State["groups"] as JsonObject ?? throw new InvalidOperationException("Run classify_commits first."); var sections = new [] { ("Features", "features"), ("Fixes", "fixes"), ("Documentation", "docs") };
      var lines = new List<string> { "# Release notes", "" }; foreach (var (title,key) in sections) { var entries = groups[key]!.AsArray(); if (entries.Count == 0) continue; lines.Add("## " + title); lines.AddRange(entries.Select(x => "- " + x!.GetValue<string>())); lines.Add(""); }
      var result = string.Join("\n", lines); await File.WriteAllTextAsync(c.ResolvePath("RELEASE_NOTES.md"), result, ct); return new JsonObject { ["path"] = "RELEASE_NOTES.md", ["sections"] = sections.Count(x => groups[x.Item2]!.AsArray().Count > 0) };
    })
  ], [new("classify_commits", new()), new("draft_notes", new())]);
}
