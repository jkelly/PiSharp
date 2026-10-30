// PiSharp-only (no upstream Pi source): agent sample implementation and authored offline verification.
using PiSharp.Samples;
using PiSharp.Samples.PersistentTaskManager;
using System.Text.Json.Nodes;

if (args.Contains("--self-test")) return await SelfTests.RunAsync();
if (args.Contains("--persist-demo"))
{
    if (!args.Contains("--approve")) { Console.Error.WriteLine("--persist-demo requires --approve"); return 2; }
    var index = Array.IndexOf(args, "--workspace");
    var workspace = index >= 0 && index + 1 < args.Length ? args[index + 1] : Path.Combine(Path.GetTempPath(), "pisharp-task-demo");
    var input = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixture.json")))!.AsObject();
    var context = new SampleContext(workspace, input, approved: true);
    var spec = Workflow.Create(context);
    await spec.Tools.Single(t => t.Name == "complete_task").Execute(SampleJson.Object("{\"id\":\"task-501\"}"), default);
    Console.WriteLine((await spec.Tools.Single(t => t.Name == "list_tasks").Execute(new JsonObject(), default))!.ToJsonString(SampleContext.JsonOptions));
    return 0;
}
return await SampleHost.RunAsync(args, Workflow.Create, Path.Combine(AppContext.BaseDirectory, "fixture.json"));
