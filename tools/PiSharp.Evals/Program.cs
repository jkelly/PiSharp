using System.Text.Json;

namespace PiSharp.Evals;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Contains("--help"))
            {
                Console.WriteLine("PiSharp.Evals [--repetitions N] [--cases cases.json] [--out fresh-directory]\n" +
                    "  Default: deterministic synthetic fixtures, no process/provider execution.\n" +
                    "  --live --runtime-dir published-directory --entry PiSharp.Cli.dll --dotnet executable\n" +
                    "    --docs documentation-directory --provider name --model id --out fresh-directory\n" +
                    "    [--container-image image@sha256:digest] [--container-executable docker]\n" +
                    "    [--credential-env NAME,NAME] [--timeout-seconds N]\n" +
                    "  --input observations.json: compare { plan: [...], results: [...] } without execution."); return 0;
            }
            var values = new Dictionary<string, string>(StringComparer.Ordinal); bool live = false;
            var allowed = new HashSet<string>(["--repetitions", "--cases", "--out", "--runtime-dir", "--entry", "--dotnet", "--docs", "--provider", "--model", "--container-image", "--container-executable", "--credential-env", "--timeout-seconds", "--input"]);
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--live" && !live) { live = true; continue; }
                if (!allowed.Contains(args[i]) || i + 1 == args.Length || !values.TryAdd(args[i], args[++i])) throw new ArgumentException("Unknown, duplicate or incomplete option.");
            }
            string Required(string name) => values.GetValueOrDefault(name) ?? throw new ArgumentException(name + " is required.");
            EvalReport report;
            if (values.TryGetValue("--input", out var input))
            {
                if (live || values.Keys.Any(k => k is not ("--input" or "--out"))) throw new ArgumentException("Comparison input cannot be combined with execution options.");
                var data = JsonSerializer.Deserialize<ComparisonInput>(await File.ReadAllTextAsync(input), EvalJson.Options) ?? throw new ArgumentException("Invalid comparison input.");
                report = EvalReport.Create(data.Plan, data.Results, "imported observations; provenance supplied by caller");
            }
            else
            {
                var cases = values.TryGetValue("--cases", out var path) ? EvalPlan.ValidateCases(JsonSerializer.Deserialize<EvalCase[]>(await File.ReadAllTextAsync(path), EvalJson.Options) ?? []) : EvalPlan.BuiltInCases;
                IEvalRuntime runtime;
                if (live)
                    runtime = new StagedRuntime(new(Required("--runtime-dir"), Required("--entry"), Required("--docs"), Required("--out"),
                        Required("--provider"), Required("--model"), true, values.GetValueOrDefault("--dotnet"), values.GetValueOrDefault("--container-image"),
                        values.GetValueOrDefault("--container-executable") ?? "docker", values.GetValueOrDefault("--credential-env")?.Split(','),
                        int.Parse(values.GetValueOrDefault("--timeout-seconds") ?? "120")), cases: cases);
                else
                {
                    if (values.Keys.Any(k => k is not ("--cases" or "--repetitions" or "--out"))) throw new ArgumentException("Runtime/provider options require --live.");
                    runtime = new FixtureRuntime(cases);
                }
                report = await EvalHarness.RunAsync(runtime, live ? Required("--provider") + "/" + Required("--model") : "fixture/synthetic", int.Parse(values.GetValueOrDefault("--repetitions") ?? "2"));
            }
            var json = EvalJson.Serialize(report);
            if (values.TryGetValue("--out", out var output))
            {
                if (!live && (Directory.Exists(output) || File.Exists(output))) throw new ArgumentException("Output directory must be fresh.");
                Directory.CreateDirectory(output); await File.WriteAllTextAsync(Path.Combine(output, "report.json"), json);
            }
            Console.Write(json); return report.ExitCode;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Console.Error.WriteLine(error.Message); return 2; }
    }
    public sealed record ComparisonInput(EvalTask[] Plan, EvalResult[] Results);
}
