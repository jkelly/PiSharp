using System.Text.Json.Nodes;
using PiSharp.Samples;
using PiSharp.Samples.ProjectDeliveryCoordinator;
if (args.Contains("--self-test", StringComparer.Ordinal)) {
    var fixture = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixture.json")))!.AsObject();
    var workspace = Path.Combine(Path.GetTempPath(), "pisharp-delivery-" + Guid.NewGuid().ToString("N"));
    try { var c = new SampleContext(workspace, fixture); var spec = Workflow.Create(c);
        foreach (var call in spec.DemoCalls) await spec.Tools.Single(t => t.Name == call.Tool).Execute(call.Arguments, CancellationToken.None);
        if (!File.Exists(c.ResolvePath("status/report.json")) || !File.Exists(c.ResolvePath("risk/report.json")) || !File.Exists(c.ResolvePath("delivery.json"))) throw new Exception("Child Agent reports missing.");
        Console.WriteLine("PASS ProjectDeliveryCoordinator: two child Agent executions and synthesis"); return 0;
    } finally { Directory.Delete(workspace, true); }
}
return await SampleHost.RunAsync(args, Workflow.Create, Path.Combine(AppContext.BaseDirectory, "fixture.json"));
