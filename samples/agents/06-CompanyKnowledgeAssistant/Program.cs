using PiSharp.Samples;
using PiSharp.Samples.CompanyKnowledgeAssistant;

if (args.Contains("--self-test")) return await SelfTests.RunAsync();
return await SampleHost.RunAsync(args, Workflow.Create, Path.Combine(AppContext.BaseDirectory, "fixture.json"));
