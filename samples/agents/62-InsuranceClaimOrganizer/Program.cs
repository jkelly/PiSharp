// PiSharp-only (no upstream Pi source): runnable insurance claim organizer sample.
using PiSharp.Samples;
using PiSharp.Samples.InsuranceClaimOrganizer;

var fixture = Path.Combine(AppContext.BaseDirectory, "fixture.json");
if (args.Contains("--self-test", StringComparer.Ordinal))
    return await SelfTests.RunAsync(fixture);
return await SampleHost.RunAsync(args, Workflow.Create, fixture);
