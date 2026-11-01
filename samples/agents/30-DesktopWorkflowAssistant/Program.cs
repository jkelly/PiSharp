// PiSharp-only (no upstream Pi source): agent sample implementation and authored offline verification.
using System.Text.Json.Nodes;
using PiSharp.Samples;
using PiSharp.Samples.DesktopWorkflowAssistant;
if (args.Contains("--self-test", StringComparer.Ordinal)) {
    var fixture = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixture.json")))!.AsObject();
    var workspace = Path.Combine(Path.GetTempPath(), "pisharp-desktop-" + Guid.NewGuid().ToString("N"));
    try { var c = new SampleContext(workspace, fixture); var spec = Workflow.Create(c);
        foreach (var call in spec.DemoCalls) await spec.Tools.Single(t => t.Name == call.Tool).Execute(call.Arguments, CancellationToken.None);
        if (!File.Exists(c.ResolvePath("meeting-brief.md")) || !File.Exists(c.ResolvePath("receipt.json"))) throw new Exception("Desktop artifacts missing.");
        Console.WriteLine("PASS DesktopWorkflowAssistant: brief and receipt"); return 0;
    } finally { Directory.Delete(workspace, true); }
}
if (args.Contains("--stdio", StringComparer.Ordinal)) return await StdioAdapter.RunAsync();
return await SampleHost.RunAsync(args, Workflow.Create, Path.Combine(AppContext.BaseDirectory, "fixture.json"));
