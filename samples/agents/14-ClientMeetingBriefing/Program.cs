using System.Text.Json.Nodes;
using PiSharp.Samples;
using PiSharp.Samples.ClientMeetingBriefing;

var fixture = Path.Combine(AppContext.BaseDirectory, "fixture.json");
if (args.Contains("--self-test", StringComparer.Ordinal))
    return await SelfTests.RunAsync(fixture);
return await SampleHost.RunAsync(args, Workflow.Create, fixture);
