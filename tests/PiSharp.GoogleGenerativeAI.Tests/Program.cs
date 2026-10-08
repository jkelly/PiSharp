using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

// Source-only until the lead reopens the native window. This is a real serial consumer test runner.
string? filter = null; string? reportPath = null;
for (var i = 0; i < args.Length; i += 2)
{
    if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1])) throw new ArgumentException("Use --case <exact case id> and/or --report <fresh path>, or no arguments.");
    if (args[i] == "--case" && filter is null) filter = args[i + 1];
    else if (args[i] == "--report" && reportPath is null) reportPath = Path.GetFullPath(args[i + 1]);
    else throw new ArgumentException("Unknown or duplicate Google runner argument.");
}
var cases = GoogleCases.Cases().Where(x => filter is null || x.Id == filter).ToArray();
if (cases.Length == 0) throw new ArgumentException("Unknown Google case.");
var results = new List<object>(); var failed = false;
// Create both owned files before any test starts. The flushed journal precedes
// each convenience snapshot, including incomplete evidence before an owned join.
using var report = reportPath is null ? null : new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
using var journal = reportPath is null ? null : new FileStream(reportPath + ".jsonl", FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
var persistenceErrors = new List<string>();
void Emit(object value, bool print = true)
{
    try
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (journal is not null && report is not null)
        {
            journal.Write(bytes); journal.WriteByte((byte)'\n'); journal.Flush(true);
            report.Position = 0; report.SetLength(0); report.Write(bytes); report.WriteByte((byte)'\n'); report.Flush(true);
        }
        if (print) Console.WriteLine(System.Text.Encoding.UTF8.GetString(bytes));
    }
    catch (Exception error) { persistenceErrors.Add(error.ToString()); failed = true; }
}
Emit(new { schemaVersion = 1, status = "STARTED_NONPASSING", packageAcceptance = false, expectedFamilies = cases.Length }, print: false);
foreach (var test in cases)
{
    if (persistenceErrors.Count != 0) break;
    try
    {
        var original = test.Run();
        try
        {
            if (test.Id is "google.history-id-normalization-before-filtering" or "google.simple-model-reasoning-and-budgets")
                await original;
            else await original.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            Emit(new { schemaVersion = 1, caseId = test.Id,
                status = "INCOMPLETE_NONPASSING", originalTaskOwned = true, laterAdmission = "STOPPED" });
            // A diagnostic deadline grants no detach/cleanup/pass receipt authority.
            try { await original; } catch { }
            throw;
        }
        results.Add(new { caseId = test.Id, status = "PASS_AUTHORED_NATIVE_ONLY" });
    }
    catch (Exception error)
    {
        failed = true;
        results.Add(new { caseId = test.Id, status = "FAIL", error = error.Message });
        if (error is TimeoutException) break;
    }
    Emit(new { schemaVersion = 1, status = "RUNNING_NONPASSING", packageAcceptance = false, results }, print: false);
}
var products = new[] { Assembly.GetExecutingAssembly(), typeof(PiSharp.AI.ChatClient).Assembly,
    typeof(PiSharp.Agent.Agent).Assembly, typeof(PiSharp.Contracts.JsonData).Assembly }.Distinct().Select(assembly =>
    new { name = assembly.GetName().Name, path = assembly.Location, bytes = new FileInfo(assembly.Location).Length,
        sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
        informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion }).ToArray();
Emit(new { schemaVersion = 1, qualification = "AUTHORED OFFLINE NATIVE ONLY",
    sourceCaptures = 0, packageAcceptance = false, allEightPhaseGates = "OPEN", products, results,
    expectedFamilies = cases.Length, complete = !failed && results.Count == cases.Length && persistenceErrors.Count == 0, persistenceErrors });
return failed ? 1 : 0;
