using System.Text.Json;

if (args.Length == 2 && args[0] == "--session-store-lease-probe")
    return await SessionLogStoreTests.RunLeaseProbeAsync(args[1]);
if (args is ["--capture-native-copy-reader", var outputRoot, "--candidate", var executionCandidate])
    return await SessionNativeCopyReaderReferenceTests.CaptureNativeExportsAsync(outputRoot, executionCandidate);
string? report = null, filter = null;
for (var index = 0; index < args.Length; index += 2)
{
    if (index + 1 >= args.Length) throw new ArgumentException("Session test option requires a value.");
    if (args[index] == "--report" && report is null) report = Path.GetFullPath(args[index + 1]);
    else if (args[index] == "--filter" && filter is null && !string.IsNullOrWhiteSpace(args[index + 1])) filter = args[index + 1];
    else throw new ArgumentException("Usage: PiSharp.Sessions.Tests [--report <path>] [--filter <name substring>]");
}
var evidence = new List<object>();
var tests = SessionEntryCodecTests.Cases().Concat(SessionLogReaderTests.Cases()).Concat(SessionLogStoreTests.Cases()).Concat(SessionContextProjectorTests.Cases()).Concat(SessionContextInfluenceTests.Cases()).Concat(SessionEntryMigrationTests.Cases()).Concat(SessionTreeQueriesTests.Cases()).Concat(SessionCopyServiceTests.Cases()).Concat(SessionBranchPlannerTests.Cases()).Concat(SessionBranchPublisherTests.Cases()).Concat(SessionCatalogTests.Cases()).Concat(SessionStorageBackendTests.Cases())
    .Concat(SessionHistoryProjectorTests.Cases())
    .Concat(SessionArchiveCopyTests.Cases())
    .Concat(SessionNativeCopyReaderReferenceTests.Cases())
    .Concat(SessionBranchReferenceTests.Cases())
    .Concat(NativeSessionDiagnosticViewTests.Cases())
    .Where(test => filter is null || test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
if (tests.Length == 0) throw new ArgumentException("No matching session cases.");
var failed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run().WaitAsync(TimeSpan.FromSeconds(20));
        Console.WriteLine($"PASS {test.Name}");
        evidence.Add(new { testId = $"session.{test.Run.Method.DeclaringType?.Name}.{test.Run.Method.Name}", name = test.Name, status = "passed" });
    }
    catch (Exception error)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {error}");
        evidence.Add(new { testId = $"session.{test.Run.Method.DeclaringType?.Name}.{test.Run.Method.Name}", name = test.Name, status = "failed", error = error.Message });
    }
}
Console.WriteLine($"Session tests: {tests.Length - failed} passed, {failed} failed.");
if (report is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(report)!);
    await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        sourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f",
        scope = "native-session-records-storage-migration-archives-tree-context-history-and-pinned-reader-comparisons",
        referenceEvidence = "Authored native controls plus frozen unchanged whole-Pi SessionManager observations of actual native exports and branch/history fixtures; no complete phase acceptance",
        tests = evidence,
        passed = tests.Length - failed,
        failed
    }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
}
return failed == 0 ? 0 : 1;
