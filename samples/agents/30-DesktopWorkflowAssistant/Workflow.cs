using System.Text.Json.Nodes;
namespace PiSharp.Samples.DesktopWorkflowAssistant;
public static class Workflow {
  public static WorkflowSpec Create(SampleContext c) => new("desktop-workflow-assistant", "Desktop workflow assistant", "Validate a desktop request and prepare a local meeting brief with a receipt.", [
    new("validate_request", "Validate supported desktop intent and required fields", SampleJson.Schema(), async (_, ct) => {
      if (c.Input.Text("intent") != "prepare-meeting") throw new ArgumentException("Only prepare-meeting is supported.");
      if (string.IsNullOrWhiteSpace(c.Input.Text("requestId")) || string.IsNullOrWhiteSpace(c.Input.Text("title"))) throw new ArgumentException("requestId and title are required.");
      var people = c.Input["participants"] as JsonArray ?? throw new ArgumentException("participants array is required.");
      var result = new JsonObject { ["valid"] = true, ["participantCount"] = people.Count, ["requestId"] = c.Input.Text("requestId") };
      c.State["validation"] = result.DeepClone(); await c.WriteJsonAsync("validation.json", result, ct); return result;
    }),
    new("prepare_brief", "Write local brief and request receipt", SampleJson.Schema(), async (_, ct) => {
      if (c.State["validation"] is null) throw new InvalidOperationException("Run validate_request first.");
      var participants = string.Join(", ", c.Input["participants"]!.AsArray().Select(x => x!.GetValue<string>()));
      var notes = string.Join("\n", c.Input["notes"]!.AsArray().Select(x => "- " + x!.GetValue<string>()));
      var brief = $"# {c.Input.Text("title")}\n\nParticipants: {participants}\n\n## Agenda\n\n{notes}\n";
      await File.WriteAllTextAsync(c.ResolvePath("meeting-brief.md"), brief, ct);
      var receipt = new JsonObject { ["requestId"] = c.Input.Text("requestId"), ["status"] = "prepared", ["artifact"] = "meeting-brief.md" };
      await c.WriteJsonAsync("receipt.json", receipt, ct); return receipt;
    })
  ], [new("validate_request", new()), new("prepare_brief", new())]);
}

public static class StdioAdapter {
  public static async Task<int> RunAsync() {
    string? line; while ((line = await Console.In.ReadLineAsync()) is not null) {
      try {
        var request = JsonNode.Parse(line)?.AsObject() ?? throw new ArgumentException("Expected JSON object."); var action = request.Text("action");
        if (action == "ping") { Console.WriteLine(new JsonObject { ["ok"] = true, ["protocol"] = "pisharp-desktop-stdio-v1" }.ToJsonString()); continue; }
        if (action != "run") throw new ArgumentException("Only ping and run actions are supported.");
        var input = request["input"] as JsonObject ?? throw new ArgumentException("run requires input object.");
        var workspace = Path.Combine(Path.GetTempPath(), "pisharp-desktop-stdio", Guid.NewGuid().ToString("N"));
        var context = new SampleContext(workspace, (JsonObject)input.DeepClone()); var result = await SampleHost.ExecuteAsync(Workflow.Create(context), context);
        Console.WriteLine(new JsonObject { ["ok"] = result.Success, ["workspace"] = workspace, ["report"] = result.Report.DeepClone() }.ToJsonString());
      } catch (Exception ex) { Console.WriteLine(new JsonObject { ["ok"] = false, ["error"] = SecretRedactor.Redact(ex.Message) }.ToJsonString()); }
    }
    return 0;
  }
}
