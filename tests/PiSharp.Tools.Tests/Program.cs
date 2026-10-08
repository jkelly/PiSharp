using System.Text.Json;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--native-process-test-child")
            return await NativeProcessRunnerTests.RunChildAsync(args[1..]);
        string? report = null, filter = null;
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length) { Console.Error.WriteLine("Tool test option requires a value."); return 2; }
            if (args[index] == "--report" && report is null) report = Path.GetFullPath(args[index + 1]);
            else if (args[index] == "--filter" && filter is null && !string.IsNullOrWhiteSpace(args[index + 1])) filter = args[index + 1];
            else { Console.Error.WriteLine("Usage: PiSharp.Tools.Tests [--report <path>] [--filter <name substring>]"); return 2; }
        }
        var evidence = new List<object>(); var failures = 0;
        EditDifferentialTests.Configure(Path.GetFullPath(Environment.CurrentDirectory));
        EditPreviewTests.Configure(Path.GetFullPath(Environment.CurrentDirectory));
        var tests = ReadWriteToolsTests.Cases().Concat(EditToolTests.Cases()).Concat(EditDifferentialTests.Cases()).Concat(EditPreviewTests.Cases())
            .Concat(NativeProcessRunnerTests.Cases()).Concat(BashToolTests.Cases())
            .Concat(BashProgressDeliveryTests.Cases()).Concat(LsToolTests.Cases()).Concat(FindToolTests.Cases()).Concat(FdFindExecutorTests.Cases()).Concat(GrepToolTests.Cases())
            .Concat(GrepContextHostAdapterTests.Cases())
            .Concat(UnixProcessRunnerTests.Cases())
            .Where(test => filter is null || test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (tests.Length == 0) { Console.Error.WriteLine("No matching tool test cases."); return 2; }
        foreach (var test in tests)
        {
            try
            {
                // These groups own filesystem work and a cancellation fixture with an original cleanup join.
                // A timeout wrapper must not let the runner abandon them before their finally blocks settle.
                if (test.Run.Method.DeclaringType == typeof(LsToolTests) || test.Run.Method.DeclaringType == typeof(FindToolTests) ||
                    test.Run.Method.DeclaringType == typeof(FdFindExecutorTests) || test.Run.Method.DeclaringType == typeof(GrepToolTests) ||
                    test.Run.Method.DeclaringType == typeof(GrepContextHostAdapterTests) ||
                    test.Name.StartsWith("Unix ", StringComparison.Ordinal) ||
                    test.Name == "Bash abort timeout spawn capture cleanup and foreign failures retain bounded known output") await test.Run();
                else await test.Run().WaitAsync(TimeSpan.FromSeconds(20));
                Console.WriteLine("PASS " + test.Name);
                evidence.Add(new { testId = "tools." + test.Run.Method.Name, name = test.Name, status = "passed" });
            }
            catch (Exception error)
            {
                failures++; Console.Error.WriteLine($"FAIL {test.Name}: {error}");
                evidence.Add(new { testId = "tools." + test.Run.Method.Name, name = test.Name, status = "failed", error = error.Message });
            }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} native filesystem tool groups passed");
        if (report is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
            {
                schemaVersion = 1, sourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f",
                scope = "authored-native-bounded-file-effects-and-windows-process-backend", tests = evidence,
                platformEvidence = ReadWriteToolsTests.PlatformEvidence,
                editDifferentialEvidence = EditDifferentialTests.Evidence,
                editAccessEvidence = EditDifferentialTests.AccessEvidence,
                editPreviewEvidence = EditPreviewTests.Evidence,
                editPreviewPlatformEvidence = EditPreviewTests.PlatformEvidence,
                processPlatformEvidence = NativeProcessRunnerTests.PlatformEvidence,
                passed = tests.Length - failures, failed = failures, upstreamDifferential = false, phaseAcceptanceClaimed = false
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return failures == 0 ? 0 : 1;
    }
}
