using System.Text.Json;

string? report = null, filter = null;
for (var index = 0; index < args.Length; index++)
{
    if (args[index] == "--report" && report is null && index + 1 < args.Length) report = Path.GetFullPath(args[++index]);
    else if (args[index] == "--filter" && filter is null && index + 1 < args.Length && !string.IsNullOrWhiteSpace(args[index + 1])) filter = args[++index];
    else throw new ArgumentException("Usage: PiSharp.Rpc.Tests [--report <path>] [--filter <name substring>]");
}
var tests = JsonlTransportTests.Cases().Concat(RecoverableJsonlReaderTests.Cases())
    .Concat(RpcSessionDispatcherTests.Cases()).Concat(RpcSessionCreationTests.Cases()).Concat(SourceProgressRpcTests.Cases())
    .Concat(RpcSessionCatalogTests.Cases())
    .Concat(RpcQueueRestorationTests.Cases())
    .Concat(RpcTerminalInterruptTests.Cases())
    .Concat(RpcContextEditTests.Cases())
    .Concat(RpcCompactionTests.Cases())
    .Concat(RpcUpstreamCompactionTests.Cases())
    .Concat(OriginalCompactionWireTests.Cases())
    .Concat(OriginalCompactionLifecycleTests.Cases())
    .Concat(RpcHtmlExportTests.Cases())
    .Concat(RpcModelThinkingTests.Cases())
    .Concat(RpcNativeThinkingIntegrationTests.Cases())
    .Concat(RpcSessionMetadataTests.Cases())
    .Concat(SourceToolResultPersistenceTests.Cases())
    .Concat(CompletionsSourceEventProjectionTests.Cases())
    .Concat(ResponsesCheckpointProjectionTests.Cases())
    .Concat(RegisteredInputRpcTests.Cases())
    .Concat(RpcExtensionUiTests.Cases()).Concat(RpcUiPresentationRetirementTests.Cases()).ToArray();
var availableTests = tests.Length;
if (filter is not null)
{
    tests = tests.Where(test => test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
    if (tests.Length == 0) throw new ArgumentException("The explicit filter selected no RPC groups.");
}
var evidence = new List<object>(); var failed = 0;
foreach (var test in tests)
{
    try
    {
        // Ownership-sensitive cases must join originals after releasing observations.
        if (test.Name == "rpc.lifecycle-queue-publication-orders-authority-switch-and-joins-held-writes" ||
            test.Name.StartsWith(RpcQueueRestorationTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith(RpcTerminalInterruptTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith(RpcModelThinkingTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith(RpcSessionMetadataTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith(RpcUpstreamCompactionTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith(OriginalCompactionLifecycleTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith(RpcHtmlExportTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith("rpc.shutdown-settlement-", StringComparison.Ordinal) ||
            test.Name == "rpc.responses-checkpoint-public-projection-and-final-state" ||
            test.Name == "rpc.native-checkpoint-suppression-keeps-existing-event-policy")
            await test.Run();
        else
            await test.Run().WaitAsync(TimeSpan.FromSeconds(30));
        Console.WriteLine("PASS " + test.Name);
        evidence.Add(new { testId = test.Name, status = "passed" });
    }
    catch (Exception error)
    {
        failed++; Console.Error.WriteLine($"FAIL {test.Name}: {error}");
        evidence.Add(new { testId = test.Name, status = "failed", error = error.Message, diagnostics = TestFailureDiagnostics.Capture(error) });
    }
}
Console.WriteLine($"RPC transport tests: {tests.Length - failed} passed, {failed} failed.");
if (report is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(report)!);
    await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
    {
        schemaVersion = 1, sourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f",
        scope = "authored-native-jsonl-framing-and-durable-session-dispatch", tests = evidence,
        passed = tests.Length - failed, failed, filter, availableTests, completeSuite = filter is null, upstreamDifferential = false, phaseAcceptanceClaimed = false
    }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
}
return failed == 0 ? 0 : 1;
