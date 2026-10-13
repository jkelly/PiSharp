using System.Reflection;
using System.Text;
using System.Text.Json;
using PiSharp.Durable;

// The experimental `pisharp durable` mode (decision 0005, phase P12): Pi v1.1.0 packages/coding-agent/src/experimental/durable has no
// upstream tests, so these cases are authored. Every static method of Program whose name ends in "Cases" and returns
// IEnumerable<(string, Func<Task>)> is a case group; the runner is the same as tests/PiSharp.Durable.Tests'.
internal static partial class Program
{
    internal const string Upstream = DurableUpstream.Commit;

    private static async Task<int> Main(string[] args)
    {
        // The child processes of the runtime.kill-resume and sessions.lock.cross-process cases (RuntimeCases.cs, SessionsCases.cs).
        if (args is ["--durable-runtime-child", var root]) return await RunRuntimeChildAsync(root);
        if (args is ["--durable-lock-child", var agentDir, var cwd]) return await RunLockChildAsync(agentDir, cwd);
        if (args.Length != 0 && (args.Length != 2 || args[0] is not ("--report" or "--only")))
            throw new ArgumentException("Use [--report <fresh path>] or [--only <case prefix>].");
        var cases = new List<(string Id, Func<Task> Run)>();
        foreach (var method in typeof(Program).GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(method => method.Name.EndsWith("Cases", StringComparison.Ordinal) && method.GetParameters().Length == 0 &&
                method.ReturnType == typeof(IEnumerable<(string, Func<Task>)>)).OrderBy(method => method.Name, StringComparer.Ordinal))
            cases.AddRange((IEnumerable<(string, Func<Task>)>)method.Invoke(null, null)!);
        if (cases.GroupBy(test => test.Id, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key).ToList() is { Count: > 0 } duplicates)
            throw new InvalidOperationException("Duplicate case ids: " + string.Join(", ", duplicates));
        if (args is ["--only", var prefix]) cases = [.. cases.Where(test => test.Id.StartsWith(prefix, StringComparison.Ordinal))];
        var results = new List<object>(); var failures = 0;
        foreach (var test in cases)
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            Console.Error.WriteLine("start " + test.Id);
            try { await test.Run().WaitAsync(TimeSpan.FromSeconds(120)); results.Add(new { test.Id, status = "PASS_AUTHORED_NATIVE_ONLY", ms = started.ElapsedMilliseconds }); }
            catch (Exception error) { failures++; results.Add(new { test.Id, status = "FAIL", failure = error.ToString() }); }
        }
        var report = new { sourceSha = Upstream, status = "AUTHORED NATIVE; NO UPSTREAM CAPTURE", cases = cases.Count, failures,
            genuineSourceCasesCaptured = 0, results };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        if (args is ["--report", var path])
        {
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await file.WriteAsync(Encoding.UTF8.GetBytes(json));
        }
        Console.WriteLine(json);
        return failures == 0 ? 0 : 1;
    }

    internal static (string, Func<Task>) Case(string id, Action run) => (id, () => { run(); return Task.CompletedTask; });
    internal static (string, Func<Task>) Case(string id, Func<Task> run) => (id, run);
    internal static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    internal static void Equal<T>(T expected, T actual, string what)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{what}: expected <{expected}>, actual <{actual}>."); }
    internal static T Throws<T>(Action run, string what) where T : Exception
    {
        try { run(); } catch (T error) { return error; }
        throw new InvalidOperationException(what + ": expected " + typeof(T).Name + ".");
    }
    internal static async Task<T> ThrowsAsync<T>(Func<Task> run, string what) where T : Exception
    {
        try { await run(); } catch (T error) { return error; }
        throw new InvalidOperationException(what + ": expected " + typeof(T).Name + ".");
    }
    internal static string Temp(string name)
    {
        var path = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "pisharp-durable", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    /// <summary>The repository root, found from the test binary (tests/PiSharp.Durable.Tests/bin/&lt;configuration&gt;/net10.0).</summary>
    internal static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx"))) return directory.FullName;
        throw new InvalidOperationException("PiSharp.slnx not found above " + AppContext.BaseDirectory);
    }
}
