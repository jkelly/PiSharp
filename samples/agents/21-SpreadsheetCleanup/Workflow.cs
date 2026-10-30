// PiSharp-only (no upstream Pi source): agent sample implementation and authored offline verification.
using System.Text;
using System.Text.Json.Nodes;
namespace PiSharp.Samples.SpreadsheetCleanup;
public static class Workflow {
  public static WorkflowSpec Create(SampleContext c) => new("spreadsheet-cleanup", "Spreadsheet cleanup", "Clean fixture CSV and report data quality.", [
    new("clean_csv", "Parse and deduplicate contacts by normalized email", SampleJson.Schema(), async (_, ct) => {
      var lines = c.Input.Text("csv").Split('\n', StringSplitOptions.RemoveEmptyEntries);
      if (lines.Length == 0 || lines[0].Trim() != "name,email,region") throw new FormatException("Expected name,email,region header.");
      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var output = new StringBuilder("name,email,region\n");
      int duplicates = 0, invalid = 0;
      foreach (var line in lines.Skip(1)) { ct.ThrowIfCancellationRequested(); var cols = line.Split(','); if (cols.Length != 3) { invalid++; continue; }
        var name = string.Join(' ', cols[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)); var email = cols[1].Trim().ToLowerInvariant(); var region = cols[2].Trim();
        if (!email.Contains('@') || !email.Split('@')[1].Contains('.')) { invalid++; continue; }
        if (!seen.Add(email)) { duplicates++; continue; }
        output.AppendLine($"{name},{email},{region}");
      }
      await File.WriteAllTextAsync(c.ResolvePath("contacts-clean.csv"), output.ToString(), ct);
      c.State["quality"] = new JsonObject { ["kept"] = seen.Count, ["duplicates"] = duplicates, ["invalid"] = invalid };
      return c.State["quality"]?.DeepClone();
    }),
    new("quality_report", "Write count and issue summary", SampleJson.Schema(), async (_, ct) => { var report = c.State["quality"]?.DeepClone() ?? throw new InvalidOperationException("Run clean_csv first."); await c.WriteJsonAsync("quality.json", report, ct); return report; })
  ], [new("clean_csv", new()), new("quality_report", new())]);
}
