using System.Text.Json;

var options = new Dictionary<string, string>(StringComparer.Ordinal);
string? filter = null;
for (var index = 0; index < args.Length; index += 2)
{
    if (index + 1 >= args.Length) throw new ArgumentException("Explicit unique invocation options required.");
    if (args[index] == "--filter" && filter is null && !string.IsNullOrWhiteSpace(args[index + 1]))
    { filter = args[index + 1]; continue; }
    if (!Path.IsPathFullyQualified(args[index + 1]) || !args[index].StartsWith("--", StringComparison.Ordinal) ||
        !options.TryAdd(args[index], Path.GetFullPath(args[index + 1]))) throw new ArgumentException("Explicit unique absolute paths required.");
}
string[] required = ["--dotnet-host", "--cli", "--published", "--node", "--repo", "--oracle", "--jiti", "--reference", "--command-input-reference", "--run-parent", "--report"];
if (options.Count != required.Length || required.Any(key => !options.ContainsKey(key)))
    throw new ArgumentException("Complete explicit Commands/Input package options required.");
var tests = NodeCommandInputPackageTests.Cases(options["--dotnet-host"], options["--cli"], options["--published"], options["--node"],
    options["--repo"], options["--oracle"], options["--jiti"], options["--reference"], options["--command-input-reference"], options["--run-parent"]).ToArray();
var availableTests = tests.Length;
if (filter is not null)
{
    tests = tests.Where(test => test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
    if (tests.Length == 0) throw new ArgumentException("The explicit Commands/Input filter selected no cases.");
}
var evidence = new List<object>(); var failed = 0;
foreach (var test in tests)
{
    try
    {
        // Child owners retain their deadlines and physically join cleanup. No detached timeout wrapper.
        await test.Run();
        Console.WriteLine("PASS " + test.Name); evidence.Add(new { testId = test.Name, status = "passed" });
    }
    catch (Exception error)
    {
        failed++; Console.Error.WriteLine($"FAIL {test.Name}: {error}");
        evidence.Add(new { testId = test.Name, status = "failed", error = error.Message });
    }
}
var report = options["--report"];
if (File.Exists(report)) throw new IOException("Commands/Input report must be fresh.");
Directory.CreateDirectory(Path.GetDirectoryName(report)!);
await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
{
    schemaVersion = 1, sourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f",
    scope = "explicit-opt-in-original-commands-input-provider-cli-rpc-durable-session-processes", filter, availableTests, completeSuite = filter is null,
    tests = evidence, passed = tests.Length - failed, failed, nodeAbsentClaimed = false, fullPhaseAcceptanceClaimed = false
}, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Console.WriteLine($"Commands/Input package integration: {tests.Length - failed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;
