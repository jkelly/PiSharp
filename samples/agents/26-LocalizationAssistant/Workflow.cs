// PiSharp-only (no upstream Pi source): agent sample implementation and authored offline verification.
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
namespace PiSharp.Samples.LocalizationAssistant;
public static class Workflow {
  static readonly Regex Placeholders = new(@"\{[^{}]+\}", RegexOptions.CultureInvariant);
  public static WorkflowSpec Create(SampleContext c) => new("localization-assistant", "Localization assistant", "Compare translation keys and placeholders.", [
    new("validate_strings", "Find missing keys and placeholder mismatches", SampleJson.Schema(), async (_, ct) => {
      var source = c.Input["source"]!.AsObject(); var target = c.Input["target"]!.AsObject(); var issues = new JsonArray();
      foreach (var (key,value) in source) { ct.ThrowIfCancellationRequested(); if (!target.TryGetPropertyValue(key, out var translation)) { issues.Add(new JsonObject { ["key"] = key, ["issue"] = "missing" }); continue; }
        var src = Placeholders.Matches(value!.GetValue<string>()).Select(m => m.Value).Order().ToArray(); var dst = Placeholders.Matches(translation!.GetValue<string>()).Select(m => m.Value).Order().ToArray();
        if (!src.SequenceEqual(dst)) issues.Add(new JsonObject { ["key"] = key, ["issue"] = "placeholders", ["expected"] = string.Join(",", src), ["actual"] = string.Join(",", dst) });
      }
      foreach (var key in target.Select(x => x.Key).Except(source.Select(x => x.Key))) issues.Add(new JsonObject { ["key"] = key, ["issue"] = "extra" });
      c.State["issues"] = issues.DeepClone(); return new JsonObject { ["issues"] = issues, ["count"] = issues.Count };
    }),
    new("write_review", "Write machine-readable translation review", SampleJson.Schema(), async (_, ct) => { var issues = c.State["issues"]?.DeepClone() ?? throw new InvalidOperationException("Run validate_strings first."); await c.WriteJsonAsync("localization-review.json", issues, ct); return new JsonObject { ["issues"] = issues }; })
  ], [new("validate_strings", new()), new("write_review", new())]);
}
