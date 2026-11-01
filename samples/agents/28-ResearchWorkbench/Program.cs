// PiSharp-only (no upstream Pi source): agent sample implementation and authored offline verification.
using System.Text.Json.Nodes;
using PiSharp.Samples;
using PiSharp.Samples.ResearchWorkbench;
if (args.Contains("--self-test", StringComparer.Ordinal)) {
    var fixture = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixture.json")))!.AsObject();
    var workspace = Path.Combine(Path.GetTempPath(), "pisharp-research-" + Guid.NewGuid().ToString("N"));
    try { var c = new SampleContext(workspace, fixture); var spec = Workflow.Create(c);
        foreach (var call in spec.DemoCalls) await spec.Tools.Single(t => t.Name == call.Tool).Execute(call.Arguments, CancellationToken.None);
        if (!File.Exists(c.ResolvePath("source-session.jsonl")) || !File.Exists(c.ResolvePath("branch-session.jsonl"))) throw new Exception("Session artifacts missing.");
        Console.WriteLine("PASS ResearchWorkbench: persisted source and branch sessions"); return 0;
    } finally { Directory.Delete(workspace, true); }
}
return await SampleHost.RunAsync(args, Workflow.Create, Path.Combine(AppContext.BaseDirectory, "fixture.json"));
