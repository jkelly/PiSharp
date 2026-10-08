using System.Text.Json;

string? report = null; string? filter = null;
for (var index = 0; index < args.Length; index += 2)
{
    if (index + 1 >= args.Length) throw new ArgumentException("Explicit paired report/filter options required.");
    if (args[index] == "--report" && report is null) report = Path.GetFullPath(args[index + 1]);
    else if (args[index] == "--filter" && filter is null && !string.IsNullOrWhiteSpace(args[index + 1])) filter = args[index + 1];
    else throw new ArgumentException("Usage: PiSharp.ExtensionHost.Tests [--report <path>] [--filter <name>]");
}
var tests = WorkerProtocolTests.Cases().Concat(WorkerCancellationWriteFenceTests.Cases())
    .Concat(WorkerCallbackRetirementRaceTests.Cases()).ToArray();
var availableTests = tests.Length;
if (filter is not null)
{
    tests = tests.Where(test => test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
    if (tests.Length == 0) throw new ArgumentException("The explicit protocol filter selected no cases.");
}
var evidence = new List<object>();
var failed = 0;
foreach (var test in tests)
{
    try
    {
        if (test.Run.Method.DeclaringType == typeof(WorkerCancellationWriteFenceTests) ||
            test.Name.StartsWith("worker-callback-retirement.", StringComparison.Ordinal)) await test.Run();
        else await test.Run().WaitAsync(TimeSpan.FromSeconds(30));
        Console.WriteLine("PASS " + test.Name);
        evidence.Add(new { testId = test.Name, status = "passed" });
    }
    catch (Exception error)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {error}");
        evidence.Add(new { testId = test.Name, status = "failed", error = error.Message });
    }
}
Console.WriteLine($"Worker protocol tests: {tests.Length - failed} passed, {failed} failed.");
if (report is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(report)!);
    await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
    {
        schemaVersion = 1, sourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f",
        scope = "authored-native-worker-protocol-with-injected-io",
        tests = evidence, passed = tests.Length - failed, failed, filter, availableTests, completeSuite = filter is null,
        nodeWorkerExecuted = false, upstreamExtensionsExecuted = false,
        upstreamDifferential = false, phaseAcceptanceClaimed = false
    }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
}
return failed == 0 ? 0 : 1;
