using System.Reflection;
using System.Text;
using System.Text.Json;

// PiSharp Durable Web in a browser (decision 0008, Phase 8; PLAN.md Appendix C). PiSharp-only, so every case is authored; the runner is
// PiSharp.Durable.Web.Tests'. Every static method of Program whose name ends in "Cases" and returns IEnumerable<(string, Func<Task>)> is a case
// group. --artifacts saves a screenshot and the page's HTML of each failed case there.
internal static partial class Program
{
    private static async Task<int> Main(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || args[i] is not ("--report" or "--only" or "--artifacts") || !options.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Use [--report <fresh path>] [--only <case prefix>] [--artifacts <directory>].");
        }
        Artifacts = options.GetValueOrDefault("--artifacts");
        var cases = new List<(string Id, Func<Task> Run)>();
        foreach (var method in typeof(Program).GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(method => method.Name.EndsWith("Cases", StringComparison.Ordinal) && method.GetParameters().Length == 0 &&
                method.ReturnType == typeof(IEnumerable<(string, Func<Task>)>)).OrderBy(method => method.Name, StringComparer.Ordinal))
            cases.AddRange((IEnumerable<(string, Func<Task>)>)method.Invoke(null, null)!);
        if (cases.GroupBy(test => test.Id, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key).ToList() is { Count: > 0 } duplicates)
            throw new InvalidOperationException("Duplicate case ids: " + string.Join(", ", duplicates));
        if (options.TryGetValue("--only", out var prefix)) cases = [.. cases.Where(test => test.Id.StartsWith(prefix, StringComparison.Ordinal))];
        var results = new List<object>();
        var failures = 0;
        try
        {
            foreach (var test in cases)
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                Console.Error.WriteLine("start " + test.Id);
                try
                {
                    await test.Run().WaitAsync(TimeSpan.FromSeconds(180));
                    results.Add(new { test.Id, status = "PASS_AUTHORED_NATIVE_ONLY", ms = started.ElapsedMilliseconds });
                }
                catch (Exception error)
                {
                    failures++;
                    Console.Error.WriteLine("FAIL " + test.Id + ": " + error.Message);
                    results.Add(new { test.Id, status = "FAIL", failure = error.ToString() });
                }
            }
        }
        finally
        {
            await CloseBrowserAsync();
        }
        var report = new { status = "AUTHORED NATIVE; PISHARP-ONLY (decision 0008, Phase 8, browser)", cases = cases.Count, failures, results };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        if (options.TryGetValue("--report", out var path))
        {
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await file.WriteAsync(Encoding.UTF8.GetBytes(json));
        }
        Console.WriteLine(json);
        return failures == 0 ? 0 : 1;
    }

    internal static (string, Func<Task>) Case(string id, Func<Task> run) => (id, run);
    internal static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    internal static void Equal<T>(T expected, T actual, string what)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{what}: expected <{expected}>, actual <{actual}>."); }
    internal static string Temp(string name)
    {
        var path = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "pisharp-durable-web-browser", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
