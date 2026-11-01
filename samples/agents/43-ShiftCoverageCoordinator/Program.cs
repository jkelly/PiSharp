// PiSharp-only (no upstream Pi source): runnable shift coverage coordinator sample.
using PiSharp.Samples;
using PiSharp.Samples.ShiftCoverageCoordinator;

var fixture = Path.Combine(AppContext.BaseDirectory, "fixture.json");
if (args.Contains("--self-test", StringComparer.Ordinal))
    return await SelfTests.RunAsync(fixture);
return await SampleHost.RunAsync(args, Workflow.Create, fixture);
