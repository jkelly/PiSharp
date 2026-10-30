using PiSharp.Samples;
using PiSharp.Samples.ModelCatalogMaintainer;

if (args.Contains("--self-test", StringComparer.Ordinal)) return await SelfTests.RunAsync();
if (args.Contains("--maintain", StringComparer.Ordinal)) return await MaintenanceCli.RunAsync(args);
return await SampleHost.RunAsync(args, Workflow.Create, Path.Combine(AppContext.BaseDirectory, "fixture.json"));
