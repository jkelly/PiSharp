using System.Text.Json;

if (args.Length != 2 || args[0] != "--report") throw new ArgumentException("Explicit report required");
var tests = new List<object>(); var failed = 0;
foreach (var test in TerminalSourceDefaultConsumerTests.Cases().Concat(TerminalDefaultQualificationTests.Cases()))
{
    try { await test.Run(); Console.WriteLine("PASS " + test.Name); tests.Add(new { test.Name, status = "passed" }); }
    catch (Exception e) { failed++; Console.WriteLine("FAIL " + test.Name + " " + e); tests.Add(new { test.Name, status = "failed", error = e.ToString() }); }
}
var corpus = await TerminalDefaultCorpus.Run(); failed += corpus.Failed;
var witnesses = await TerminalDefaultWitnesses.Run(); failed += witnesses.Failed;
await File.WriteAllTextAsync(Path.GetFullPath(args[1]), JsonSerializer.Serialize(new { tests, failed, corpus, witnesses,
    actualProductionDefaultViewExecuted = true, actualAcknowledgedInputStartupExecuted = true,
    fullNativeGate = false, physicalTerminal = false, completePackageAcceptance = false }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
return failed == 0 ? 0 : 1;
