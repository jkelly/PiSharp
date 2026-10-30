using System.Text.Json.Nodes;
using PiSharp.Samples;
using PiSharp.Samples.LocalizationAssistant;
if (args.Contains("--self-test", StringComparer.Ordinal)) {
    var fixture = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixture.json")))!.AsObject();
    var workspace = Path.Combine(Path.GetTempPath(), "pisharp-localizationassistant-" + Guid.NewGuid().ToString("N"));
    try { var context = new SampleContext(workspace, fixture); var spec = Workflow.Create(context);
        foreach (var call in spec.DemoCalls) { var tool = spec.Tools.Single(t => t.Name == call.Tool); await tool.Execute(call.Arguments, CancellationToken.None); }
        if (!Directory.EnumerateFiles(workspace).Any()) throw new Exception("No artifact was written.");
        Console.WriteLine("PASS LocalizationAssistant: " + spec.Tools.Count + " tools, " + spec.DemoCalls.Count + " calls"); return 0;
    } finally { Directory.Delete(workspace, true); }
}
return await SampleHost.RunAsync(args, Workflow.Create, Path.Combine(AppContext.BaseDirectory, "fixture.json"));
