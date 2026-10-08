using System.Text.Json;

var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var index = 0; index < args.Length; index += 2)
{
    if (index + 1 >= args.Length || !Path.IsPathFullyQualified(args[index + 1]) || !args[index].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(args[index], Path.GetFullPath(args[index + 1])))
        throw new ArgumentException("Explicit unique absolute invocation options required.");
}
string[] required = ["--dotnet-host", "--cli", "--published", "--node", "--repo", "--oracle", "--jiti", "--reference", "--run-parent", "--report"];
if (options.Count != required.Length || required.Any(key => !options.ContainsKey(key))) throw new ArgumentException("Complete explicit Node package options required.");
var tests = NodePackageActivationTests.Cases(options["--dotnet-host"], options["--cli"], options["--published"], options["--node"],
    options["--repo"], options["--oracle"], options["--jiti"], options["--reference"], options["--run-parent"]).ToArray();
var evidence = new List<object>(); var failed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run().WaitAsync(TimeSpan.FromSeconds(240));
        Console.WriteLine("PASS " + test.Name); evidence.Add(new { testId = test.Name, status = "passed" });
    }
    catch (Exception error)
    {
        failed++; Console.Error.WriteLine($"FAIL {test.Name}: {error}");
        evidence.Add(new { testId = test.Name, status = "failed", error = error.Message });
    }
}
var report = options["--report"];
Directory.CreateDirectory(Path.GetDirectoryName(report)!);
await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
{
    schemaVersion = 1, sourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f", scope = "explicit-opt-in-native-package-and-real-node-cli-rpc-processes",
    tests = evidence, passed = tests.Length - failed, failed, nodeAbsentClaimed = false, fullPhaseAcceptanceClaimed = false
}, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Console.WriteLine($"Node package activation: {tests.Length - failed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;
