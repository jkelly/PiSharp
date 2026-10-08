using System.Text.Json;

var rendererWorkerResult = await WindowsConPtyRendererFixture.TryRunWorkerAsync(args);
if (rendererWorkerResult is not null) return rendererWorkerResult.Value;
var workerResult = await WindowsConPtyFixture.TryRunWorkerAsync(args);
if (workerResult is not null) return workerResult.Value;
string? report = null, dotnetHost = null, filter = null;
for (var index = 0; index < args.Length; index++)
{
    if (args[index] == "--report" && report is null && index + 1 < args.Length) report = Path.GetFullPath(args[++index]);
    else if (args[index] == "--dotnet-host" && dotnetHost is null && index + 1 < args.Length) dotnetHost = Path.GetFullPath(args[++index]);
    else if (args[index] == "--filter" && filter is null && index + 1 < args.Length && !string.IsNullOrWhiteSpace(args[index + 1])) filter = args[++index];
    else throw new ArgumentException("Usage: PiSharp.Tui.Tests --dotnet-host <path> [--report <path>] [--filter <ordinal-ignore-case name substring>]");
}
if (dotnetHost is null) throw new ArgumentException("Explicit .NET host path is required.");
var tests = TerminalInputTests.Cases().Concat(TerminalLeaseTests.Cases())
    .Concat(TerminalRendererTests.Cases())
    .Concat(TerminalTextComponentTests.Cases())
    .Concat(WindowsConPtyFixture.Cases(dotnetHost, typeof(TerminalInputTests).Assembly.Location))
    .Concat(WindowsConPtyRendererFixture.Cases(dotnetHost, typeof(TerminalInputTests).Assembly.Location)).ToArray();
var availableTests = tests.Length;
if (filter is not null)
{
    tests = tests.Where(test => test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
    if (tests.Length == 0) throw new ArgumentException("The explicit test filter selected no cases.");
}
var evidence = new List<object>(); var failed = 0;
foreach (var test in tests)
{
    try
    {
        // These fixtures retain native child/pipe ownership through their own cancellation and cleanup.
        // An outer WaitAsync must not detach their join when a physical cleanup is still held.
        if (test.Name.StartsWith("terminal-renderer-conpty.", StringComparison.Ordinal)) await test.Run();
        else await test.Run().WaitAsync(TimeSpan.FromSeconds(60));
        Console.WriteLine("PASS " + test.Name);
        evidence.Add(new { testId = test.Name, status = "passed" });
    }
    catch (Exception error)
    {
        failed++; Console.Error.WriteLine($"FAIL {test.Name}: {error}");
        evidence.Add(new { testId = test.Name, status = "failed", error = error.Message });
    }
}
Console.WriteLine($"Native terminal input tests: {tests.Length - failed} passed, {failed} failed.");
if (report is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(report)!);
    await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
    {
        schemaVersion = 1, sourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f",
        scope = "authored-native-bounded-input-and-actual-windows-console-ownership", tests = evidence,
        filter, availableTests, completeSuite = filter is null,
        passed = tests.Length - failed, failed, upstreamDifferential = false, phaseAcceptanceClaimed = false,
        workerReceipts = WindowsConPtyFixture.WorkerReceipts,
        rendererWorkerReceipts = WindowsConPtyRendererFixture.WorkerReceipts,
        rendererParentWitnesses = WindowsConPtyRendererFixture.ParentWitnesses,
        rendererPlatformEvidence = WindowsConPtyRendererFixture.PlatformEvidence
    }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
}
return failed == 0 ? 0 : 1;
