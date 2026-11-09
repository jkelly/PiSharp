using System.Text.Json;
using PiSharp.Evals;

internal static class Program
{
    private static readonly EvalCase One = new("case-one", "smoke", "Say READY", "READY");
    private static readonly EvalTask[] Plan = EvalPlan.Create([One], "fixture/model", 1).ToArray();
    private static int checks;
    private static async Task<int> Main(string[] args)
    {
        // A real child-process fixture; no provider, network or user credentials.
        if (args is ["--fake-cli", ..]) { Console.WriteLine(Wire("READY")); return 0; }
        if (args is ["--sleep"]) { await Task.Delay(30_000); return 0; }
        if (args.Contains("--print"))
        {
            if (!Directory.Exists(Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR")) || !Directory.Exists(Environment.GetEnvironmentVariable("HOME"))) return 12;
            Console.WriteLine(Wire("READY")); return 0;
        }
        var failures = new List<string>();
        foreach (var (id, run) in Cases())
        {
            try { await run(); Console.WriteLine("PASS " + id); }
            catch (Exception error) { failures.Add(id); Console.WriteLine("FAIL " + id + ": " + error); }
        }
        Console.WriteLine($"{checks} assertions; {failures.Count} failed cases."); return failures.Count == 0 ? 0 : 1;
    }
    private static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return Case("plan.validation-order", () =>
        {
            Throws(() => EvalPlan.Create([One, One], "p/m", 1)); Throws(() => EvalPlan.Create([], "p/m", 1));
            Throws(() => EvalPlan.Create([One], "model", 1)); Throws(() => EvalPlan.Create([One], "p/m", 0));
            var plan = EvalPlan.Create([One], "p/m", 3);
            Check(plan.Select(t => t.Arm).SequenceEqual(new[] { DocumentationArm.without_docs, DocumentationArm.with_docs, DocumentationArm.with_docs, DocumentationArm.without_docs, DocumentationArm.without_docs, DocumentationArm.with_docs }), "alternating arms");
            Throws(() => EvalPlan.RequireEqualCohorts([One], [One with { Prompt = "different" }]));
        });
        yield return Case("comparison.paired-values-and-nulls", () =>
        {
            var report = EvalReport.Create(Plan, [new(Plan[0], EvalStatus.scored, .25, new(TotalTokens: 100)), new(Plan[1], EvalStatus.scored, .75, new(TotalTokens: 150))]);
            Check(report.ExitCode == 0 && report.Comparisons[0].Lift == .5, "lift");
            Check(report.Comparisons[0].Metrics["totalTokens"].MeanDelta == 50, "token delta");
            Check(report.Comparisons[0].Metrics["estimatedCostUsd"].MeanDelta is null, "unknown cost null");
            Check(report.OperationalTotals.All(t => t.Metrics["estimatedCostUsd"].Total is null), "unknown totals null");
            var mixed = EvalReport.Create(Plan, [new(Plan[0], EvalStatus.scored, 1, new(TotalTokens: 3)), new(Plan[1], EvalStatus.scored, 1)]);
            Check(mixed.Comparisons[0].Metrics["totalTokens"].EligiblePairs == 0, "one-sided metrics excluded");
        });
        yield return Case("comparison.invalid-arms-block", () =>
        {
            EvalResult good = new(Plan[0], EvalStatus.scored, 1), other = new(Plan[1], EvalStatus.scored, 1);
            foreach (var bad in new[] {
                new[] { good }, new[] { good, good, other }, new[] { good, other with { Score = double.NaN } },
                new[] { good, other with { Score = 1.1 } }, new[] { good, other with { Metrics = new(TotalMs: -1) } },
                new[] { good, other with { Task = Plan[1] with { Arm = (DocumentationArm)99 } } },
                new[] { good, other with { Task = Plan[1] with { Case = One with { Prompt = "changed" } } } } })
            { var report = EvalReport.Create(Plan, bad); Check(report.ExitCode != 0 && report.Comparisons[0].EligiblePairs == 0, "invalid pair blocked"); }
            foreach (var state in new[] { EvalStatus.unscored, EvalStatus.skipped, EvalStatus.pending, EvalStatus.errored })
                Check(EvalReport.Create(Plan, [good, other with { Status = state, Score = null }]).ExitCode != 0, "state blocks: " + state);
        });
        yield return Case("comparison.digest-and-determinism", () =>
        {
            EvalResult[] results = [new(Plan[0], EvalStatus.scored, 0), new(Plan[1], EvalStatus.scored, 1)];
            var a = EvalReport.Create(Plan, results); var b = EvalReport.Create(Plan.Reverse().ToArray(), results.Reverse());
            Check(EvalJson.Serialize(a) == EvalJson.Serialize(b), "stable ordering");
            Check(a.ProtocolDigest != EvalReport.Create(Plan, results, "live").ProtocolDigest, "mode participates in digest");
            Check(a.ProtocolDigest != EvalReport.Create(Plan.Select(t => t with { Case = One with { Prompt = "change" } }).ToArray(), []).ProtocolDigest, "prompt participates");
        });
        yield return ("harness.failed-arm-continues-and-preflight", async () =>
        {
            var runtime = new FauxRuntime(); var report = await EvalHarness.RunAsync(runtime, "fixture/model", 2);
            Check(runtime.Executed.Count == 4, "all arms continue"); Check(report.BlockedPairs.Length == 1, "one errored pair");
            Check(runtime.Discoveries == 2, "both discovered");
            var mismatch = new FauxRuntime { Mismatch = true };
            try { await EvalHarness.RunAsync(mismatch, "fixture/model", 1); throw new Exception("Expected mismatch"); } catch (ArgumentException) { }
            Check(mismatch.Executed.Count == 0, "no execution before equal cohort");
        });
        yield return ("fixture.offline-repeatable", async () =>
        {
            var a = await EvalHarness.RunAsync(new FixtureRuntime(), "fixture/model", 2); var b = await EvalHarness.RunAsync(new FixtureRuntime(), "fixture/model", 2);
            Check(EvalJson.Serialize(a) == EvalJson.Serialize(b), "repeatable fixture"); Check(a.Results.Length == 16, "built-in cohort");
            Check(a.Results.All(r => r.Metrics is null) && a.Evidence.Contains("synthetic"), "synthetic explicitly has no measured metrics");
        });
        yield return Case("runtime.json-transcript", () =>
        {
            var result = StagedRuntime.Parse(Plan[0], new(0, Wire("READY"), "", 10));
            Check(result.Score == 1 && result.Metrics!.InputTokens == 5 && result.Metrics.OutputTokens == 2 && result.Metrics.EstimatedCostUsd == .01, "usage parsed");
            Check(StagedRuntime.Parse(Plan[0], new(0, "{\"type\":\"session\"}\n", "", 1)).Status == EvalStatus.unscored, "missing answer unscored");
            Check(StagedRuntime.Parse(Plan[0], new(0, "not json", "", 1)).Status == EvalStatus.errored, "malformed transcript");
            Check(StagedRuntime.Parse(Plan[0], new(1, Wire("READY"), "failure", 1)).Status == EvalStatus.errored, "nonzero cannot pass");
            Check(StagedRuntime.Parse(Plan[0], new(0, Wire("READY").Replace("\"stop\"", "\"error\""), "", 1)).Status == EvalStatus.errored, "assistant error cannot pass");
            var partial = StagedRuntime.Parse(Plan[0], new(0, Wire("first") + Wire("READY").Replace("\"input\":5,", ""), "", 1));
            Check(partial.Metrics!.InputTokens is null && partial.Metrics.OutputTokens == 4, "partial usage remains unavailable rather than undercounted");
        });
        yield return ("staging.container-recorder-isolation", async () =>
        {
            using var temp = new Temp(); var recorder = new Recorder(); var opts = Options(temp) with { ContainerImage = "dotnet@sha256:" + new string('a', 64) };
            var runtime = new StagedRuntime(opts, recorder, [One]);
            var report = await EvalHarness.RunAsync(runtime, "fixture/model", 2);
            Check(report.ExitCode == 0 && recorder.Commands.Count == 4, "container commands succeeded via recorder");
            Check(recorder.Commands.Select(c => c.WorkingDirectory).Distinct().Count() == 4, "fresh workspaces");
            foreach (var command in recorder.Commands)
            {
                Check(command.Executable == "docker" && command.Arguments.Contains("--rm") && command.Arguments.Contains("--read-only") && command.Arguments.Contains("--pull=never"), "fresh immutable container");
                Check(command.Arguments.Any(a => a.EndsWith("target=/runtime,readonly", StringComparison.Ordinal)), "read-only same binaries");
                Check(command.Arguments.Any(a => a.EndsWith("target=/sessions", StringComparison.Ordinal)), "session mount");
                Check(command.Arguments.Contains("PI_CODING_AGENT_DIR=/home/eval/agent"), "isolated config");
            }
            Check(Directory.Exists(Path.Combine(recorder.Commands[1].WorkingDirectory, "docs")), "treatment documentation present");
            Check(!Directory.Exists(Path.Combine(recorder.Commands[0].WorkingDirectory, "docs")), "control documentation absent");
            Check(Directory.GetFiles(Path.Combine(opts.ArtifactDirectory, "tasks"), "result.json", SearchOption.AllDirectories).Length == 4, "per-arm artifacts");
            var controlArgs = recorder.Commands[0].Arguments; var treatmentArgs = recorder.Commands[1].Arguments;
            Check(controlArgs[^1] != treatmentArgs[^1], "routing prompt differs");
            Check(report.ProtocolDigest.Length == 64, "digest");
        });
        yield return ("staging.failure-continuation-and-mutation", async () =>
        {
            using var temp = new Temp(); var recorder = new Recorder { FailFirst = true }; var opts = Options(temp);
            var runtime = new StagedRuntime(opts, recorder, [One]); var report = await EvalHarness.RunAsync(runtime, "fixture/model", 1);
            Check(report.ExitCode != 0 && recorder.Commands.Count == 2, "failed runtime continues next arm");
            await File.WriteAllTextAsync(Path.Combine(opts.ArtifactDirectory, "runtime", "PiSharp.Cli.dll"), "tamper");
            try { await runtime.RunAsync(Plan[0] with { Repetition = 3 }, default); throw new Exception("Expected changed bytes rejection"); } catch (IOException) { }
            Check(recorder.Commands.Count == 2, "changed bytes never executed");
        });
        yield return Case("staging.live-opt-in-and-paths", () =>
        {
            using var temp = new Temp(); var opts = Options(temp);
            Throws(() => new StagedRuntime(opts with { Live = false }));
            Throws(() => new StagedRuntime(opts with { EntryPoint = "../outside.dll" }));
            Throws(() => new StagedRuntime(opts with { ContainerImage = "dotnet:latest" }));
            Throws(() => new StagedRuntime(opts with { ArtifactDirectory = Path.Combine(opts.RuntimeDirectory, "out") }));
        });
        yield return ("executor.real-offline-child", async () =>
        {
            var process = Environment.ProcessPath!;
            var arguments = Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? new[] { typeof(Program).Assembly.Location, "--fake-cli" } : new[] { "--fake-cli" };
            var environment = new Dictionary<string, string>();
            foreach (var key in new[] { "PATH", "SystemRoot", "DOTNET_ROOT" }) if (Environment.GetEnvironmentVariable(key) is { } value) environment[key] = value;
            var result = await new ProcessCommandExecutor().ExecuteAsync(new(process, arguments, Environment.CurrentDirectory, environment), default);
            Check(result.ExitCode == 0 && StagedRuntime.Parse(Plan[0], result).Score == 1, "real captured child stdout");
            using var timeout = new CancellationTokenSource(150);
            var sleeper = arguments.Length == 2 ? new[] { arguments[0], "--sleep" } : new[] { "--sleep" };
            try { await new ProcessCommandExecutor().ExecuteAsync(new(process, sleeper, Environment.CurrentDirectory, environment), timeout.Token); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { Check(true, "cancelled child is killed and joined"); }
        });
        yield return ("staging.real-fake-executable", async () =>
        {
            using var temp = new Temp(); var opts = Options(temp);
            foreach (var file in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(file, Path.Combine(opts.RuntimeDirectory, Path.GetFileName(file)), true);
            opts = opts with { EntryPoint = Path.GetFileName(typeof(Program).Assembly.Location) };
            var runtime = new StagedRuntime(opts, cases: [One]);
            var result = await EvalHarness.RunAsync(runtime, "fixture/model", 1);
            Check(result.ExitCode == 0 && result.Results.All(r => r.Score == 1), "actual staged fake executable responds with isolated environment");
        });
        yield return ("cli.negative-input-exit", async () =>
        {
            using var temp = new Temp(); var input = Path.Combine(temp.Path, "input.json");
            await File.WriteAllTextAsync(input, EvalJson.Serialize(new PiSharp.Evals.Program.ComparisonInput(Plan, [new(Plan[0], EvalStatus.scored, 1)])));
            var previousOut = Console.Out; var previousError = Console.Error;
            try
            {
                Console.SetOut(new StringWriter()); Console.SetError(new StringWriter());
                Check(await PiSharp.Evals.Program.Main(["--input", input]) == 1, "CLI missing arm exit");
                Check(await PiSharp.Evals.Program.Main(["--provider", "oops"]) == 2, "CLI opt-in gate");
            }
            finally { Console.SetOut(previousOut); Console.SetError(previousError); }
        });
    }
    private static string Wire(string answer) => JsonSerializer.Serialize(new { type = "message_end", message = new { role = "assistant", stopReason = "stop", content = new[] { new { type = "text", text = answer } }, usage = new { input = 5, output = 2, totalTokens = 7, cost = new { total = .01 } } } }) + "\n";
    private static (string, Func<Task>) Case(string id, Action action) => (id, () => { action(); return Task.CompletedTask; });
    private static void Check(bool condition, string label) { checks++; if (!condition) throw new Exception(label); }
    private static void Throws(Action action) { checks++; try { action(); } catch (ArgumentException) { return; } throw new Exception("Expected ArgumentException"); }
    private sealed class Temp : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pisharp-evals-tests-" + Guid.NewGuid().ToString("N"));
        public Temp() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
    private static StagedRuntimeOptions Options(Temp temp)
    {
        var runtime = Path.Combine(temp.Path, "source"); var docs = Path.Combine(temp.Path, "docs"); Directory.CreateDirectory(runtime); Directory.CreateDirectory(docs);
        File.WriteAllText(Path.Combine(runtime, "PiSharp.Cli.dll"), "fixture binary bytes"); File.WriteAllText(Path.Combine(docs, "api.md"), "fixture docs");
        return new(runtime, "PiSharp.Cli.dll", docs, Path.Combine(temp.Path, "artifacts"), "fixture", "model", true, Environment.ProcessPath);
    }
    private sealed class Recorder : ICommandExecutor
    {
        public List<CommandSpec> Commands { get; } = []; public bool FailFirst { get; init; }
        public Task<CommandResult> ExecuteAsync(CommandSpec command, CancellationToken cancellationToken)
        { Commands.Add(command); return Task.FromResult(new CommandResult(FailFirst && Commands.Count == 1 ? 3 : 0, Wire("READY"), "", 5)); }
    }
    private sealed class FauxRuntime : IEvalRuntime
    {
        public string Evidence => "test"; public bool Mismatch { get; init; } public int Discoveries { get; private set; }
        public List<EvalTask> Executed { get; } = [];
        public Task<IReadOnlyList<EvalCase>> DiscoverAsync(DocumentationArm arm, CancellationToken cancellationToken)
        { Discoveries++; return Task.FromResult<IReadOnlyList<EvalCase>>([Mismatch && arm == DocumentationArm.with_docs ? One with { ExpectedText = "changed" } : One]); }
        public Task<EvalResult> RunAsync(EvalTask task, CancellationToken cancellationToken)
        { Check(Discoveries == 2, "preflight complete"); Executed.Add(task); if (Executed.Count == 1) throw new IOException("fixture failure"); return Task.FromResult(new EvalResult(task, EvalStatus.scored, 1)); }
    }
}
