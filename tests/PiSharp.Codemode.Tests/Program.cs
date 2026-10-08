using System.Text;
using System.Text.Json;
using PiSharp.Codemode;
using PiSharp.Contracts;

// Authored offline expectations for PiSharp 1.1.0.3: codemode on Jint (decision 0003), ported from Pi v1.1.0
// packages/codemode/test/{sandbox,source,declarations}.test.ts, packages/coding-agent/test/suite/agent-session-codemode.test.ts
// and codemode-renderer.test.ts by reading; never captured from an upstream run. Fake MCP servers, a fake model registry and a
// fake Anthropic endpoint answer in process; no network or live credentials are used.
internal static partial class Program
{
    private const string Upstream = "abe508e1b89912adde45528136c3221eb69acdd7";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--report") && (args.Length != 2 || args[0] != "--only"))
            throw new ArgumentException("Use [--report <fresh path>] or [--only <case prefix>].");
        var cases = new List<(string Id, Func<Task> Run)>();
        cases.AddRange(PureCases());
        cases.AddRange(SandboxCases());
        cases.AddRange(LimitCases());
        cases.AddRange(ExecutorCases());
        cases.AddRange(SessionCases());
        if (args.Length == 2 && args[0] == "--only") cases = [.. cases.Where(test => test.Id.StartsWith(args[1], StringComparison.Ordinal))];
        var results = new List<object>(); var failures = 0;
        foreach (var test in cases)
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            try { await test.Run().WaitAsync(TimeSpan.FromSeconds(180)); results.Add(new { test.Id, status = "PASS_AUTHORED_NATIVE_ONLY", ms = started.ElapsedMilliseconds }); }
            catch (Exception error) { failures++; results.Add(new { test.Id, status = "FAIL", failure = error.ToString() }); }
        }
        var report = new { sourceSha = Upstream, status = "AUTHORED NATIVE; NO UPSTREAM CAPTURE", engine = "Jint 4.16.3", cases = cases.Count, failures,
            genuineSourceCasesCaptured = 0, results };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        if (args.Length == 2 && args[0] == "--report")
        {
            await using var file = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await file.WriteAsync(Encoding.UTF8.GetBytes(json));
        }
        Console.WriteLine(json);
        return failures == 0 ? 0 : 1;
    }

    private static (string, Func<Task>) Case(string id, Action run) => (id, () => { run(); return Task.CompletedTask; });
    private static (string, Func<Task>) Case(string id, Func<Task> run) => (id, run);
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static void Equal<T>(T expected, T actual, string what)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{what}: expected <{expected}>, actual <{actual}>."); }
    private static void Names(IEnumerable<string> expected, IEnumerable<string> actual, string what) =>
        Check(expected.SequenceEqual(actual, StringComparer.Ordinal), $"{what}: expected [{string.Join(" | ", expected)}], actual [{string.Join(" | ", actual)}].");
    private static T Throws<T>(Action run, string what) where T : Exception
    {
        try { run(); } catch (T error) { return error; }
        throw new InvalidOperationException(what + ": expected " + typeof(T).Name + ".");
    }
    /// <summary>Compact JSON of a value, for comparisons with the expected script values.</summary>
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static string Json(JsonData? value) => value is null ? "undefined" : JsonSerializer.Serialize(value.Value, Relaxed);
    private static JsonData Parse(string json) => JsonData.Parse(json);
    private static string Temp(string name) => Path.Combine(Path.GetFullPath(Path.GetTempPath()), "pisharp-codemode", name + "-" + Guid.NewGuid().ToString("N"));
}
