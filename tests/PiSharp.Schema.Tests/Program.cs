using System.Text;
using System.Text.Json;

// Authored offline expectations for the pi-schema port of Pi v1.5.0 (packages/schema, PiSharp.Contracts.Schema) and for tool argument
// validation on it (packages/ai/src/utils/validation.ts, PiSharp.Agent.ToolArgumentValidation). They are derived from the pinned upstream
// source and its tests (packages/schema/test/*.test.ts) by reading, never captured from an upstream run. The conformance cases run the
// official JSON-Schema-Test-Suite that pi-schema vendors (fixtures/, MIT): its expectations are the suite's, not Pi's.
internal static partial class Program
{
    private const string Upstream = "f9a26365568127461c47a07a37dcb42c140a3ac5";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--report")) throw new ArgumentException("Use [--report <fresh path>].");
        var cases = new List<(string Id, Func<Task> Run)>();
        cases.AddRange(Sync150Cases);
        cases.AddRange(GapCases);
        cases.AddRange(Sync152Cases);
        var results = new List<object>(); var failures = 0;
        foreach (var test in cases)
        {
            try { await test.Run().WaitAsync(TimeSpan.FromSeconds(300)); results.Add(new { test.Id, status = "PASS_AUTHORED_NATIVE_ONLY" }); }
            catch (Exception error) { failures++; results.Add(new { test.Id, status = "FAIL", failure = error.ToString() }); }
        }
        var report = new { sourceSha = Upstream, gapRegressionSourceSha = GapRegressionUpstream, status = "AUTHORED NATIVE; NO UPSTREAM CAPTURE", cases = cases.Count, failures,
            genuineSourceCasesCaptured = 0, results };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        if (args.Length == 2)
        {
            await using var file = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await file.WriteAsync(Encoding.UTF8.GetBytes(json));
        }
        Console.WriteLine(json);
        return failures == 0 ? 0 : 1;
    }

    private static Func<Task> Sync(Action run) => () => { run(); return Task.CompletedTask; };
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static void Equal<T>(T expected, T actual, string what)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{what}: expected <{expected}>, actual <{actual}>."); }
    private static T Throws<T>(Action run, string what) where T : Exception
    {
        try { run(); } catch (T error) { return error; }
        throw new InvalidOperationException(what + ": expected " + typeof(T).Name + ".");
    }
}
