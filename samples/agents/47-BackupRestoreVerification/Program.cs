// PiSharp-only (no upstream Pi source): runnable backup restore verification agent sample.
using PiSharp.Samples;
using PiSharp.Samples.BackupRestoreVerification;

var fixture = Path.Combine(AppContext.BaseDirectory, "fixture.json");
if (args.Contains("--self-test", StringComparer.Ordinal))
    return await SelfTests.RunAsync(fixture);
return await SampleHost.RunAsync(args, Workflow.Create, fixture);
