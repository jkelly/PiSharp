using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// The experimental Chord v2 port (decision 0013, phase 4; PiSharp.Chord). Cases are ported by reading from Pi packages/chord/test/next at the
// pinned commit (ChordNext.Commit, v1.5.0), one file per upstream test file in a folder named after test/next's (Contracts/DeclareCases.cs
// ports test/next/contracts/declare.test.ts); ChordSupport.cs ports test/next/support.ts. Expected values are copied from, or derived by
// reading, the upstream tests; nothing is captured from an upstream run. Every static method of Program whose name ends in "Cases" and
// returns IEnumerable<(string, Func<Task>)> is a case group; a group is named after its layer and file (ContractsDeclareCases), so the
// layers' files of one name (contracts/params.test.ts, data/params.test.ts) stay apart. Case ids are <layer>.<file>.<case>.
internal static partial class Program
{
    private static readonly HashSet<string> AuthoredSchema3Cases = new(StringComparer.Ordinal)
    {
        "schema.corpus.first.sqlite-stopped-export",
        "schema.corpus.second.sqlite-stopped-export",
    };
    private static bool IsUpstreamCapture(string id) => id.StartsWith("schema.corpus.", StringComparison.Ordinal) && !AuthoredSchema3Cases.Contains(id);

    private static async Task<int> Main(string[] args)
    {
        string? reportPath = null, only = null;
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || args[index] is not ("--report" or "--only"))
                throw new ArgumentException("Use [--report <fresh path>] [--only <case prefix>[,<case prefix>...]].");
            if (args[index] == "--report") reportPath = args[index + 1]; else only = args[index + 1];
        }
        var cases = new List<(string Id, Func<Task> Run)>();
        foreach (var method in typeof(Program).GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(method => method.Name.EndsWith("Cases", StringComparison.Ordinal) && method.GetParameters().Length == 0 &&
                method.ReturnType == typeof(IEnumerable<(string, Func<Task>)>)).OrderBy(method => method.Name, StringComparer.Ordinal))
            cases.AddRange((IEnumerable<(string, Func<Task>)>)method.Invoke(null, null)!);
        if (cases.GroupBy(test => test.Id, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key).ToList() is { Count: > 0 } duplicates)
            throw new InvalidOperationException("Duplicate case ids: " + string.Join(", ", duplicates));
        if (only?.Split(',') is { } prefixes) cases = [.. cases.Where(test => prefixes.Any(prefix => test.Id.StartsWith(prefix, StringComparison.Ordinal)))];
        var results = new List<object>(); var failures = 0;
        foreach (var test in cases)
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            Console.Error.WriteLine("start " + test.Id);
            try
            {
                await test.Run().WaitAsync(TimeSpan.FromSeconds(120));
                results.Add(new { test.Id, status = IsUpstreamCapture(test.Id) ? "PASS_UPSTREAM_CAPTURE" : "PASS_AUTHORED_NATIVE_ONLY", ms = started.ElapsedMilliseconds });
            }
            catch (Exception error) { failures++; results.Add(new { test.Id, status = "FAIL", failure = error.ToString() }); Console.Error.WriteLine($"FAIL {test.Id}: {error}"); }
        }
        var capturedCases = cases.Count(test => IsUpstreamCapture(test.Id));
        var report = new { sourceSha = PiSharp.Chord.ChordNext.Commit, status = capturedCases == 0 ? "AUTHORED NATIVE; NO UPSTREAM CAPTURE" : "MIXED: SEE INDIVIDUAL CASE STATUS", cases = cases.Count, failures,
            genuineSourceCasesCaptured = capturedCases, results };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        if (reportPath is not null)
        {
            await using var file = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
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
    internal static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual, string what)
    {
        var left = expected.ToList(); var right = actual.ToList();
        if (!left.SequenceEqual(right)) throw new InvalidOperationException($"{what}: expected [{string.Join(", ", left)}], actual [{string.Join(", ", right)}].");
    }
    internal static T Throws<T>(Action run, string what) where T : Exception
    {
        try { run(); } catch (T error) { return error; }
        throw new InvalidOperationException(what + ": expected " + typeof(T).Name + ".");
    }

    /// <summary>expect(run).toThrow(message): an <see cref="ArgumentException"/> (upstream's Error) whose message contains
    /// <paramref name="message"/>.</summary>
    internal static void Refuses(Action run, string message, string? what = null)
    {
        var error = Throws<ArgumentException>(run, what ?? message);
        Check(error.Message.Contains(message, StringComparison.Ordinal), $"{what ?? message}: expected a message containing <{message}>, actual <{error.Message}>.");
    }

    /// <summary>expect(run).not.toThrow().</summary>
    internal static void Accepts(Action run, string what)
    {
        try { run(); }
        catch (Exception error) { throw new InvalidOperationException($"{what}: expected no error, actual {error.GetType().Name}: {error.Message}"); }
    }

    /// <summary>toEqual on JSON: the same values, ignoring object key order (numbers compared as JavaScript numbers).</summary>
    internal static void Json(string expected, JsonNode? actual, string what)
    {
        var left = JsonNode.Parse(expected);
        if (!Same(left, actual)) throw new InvalidOperationException($"{what}: expected {left?.ToJsonString() ?? "null"}, actual {actual?.ToJsonString() ?? "null"}.");
    }

    private static bool Same(JsonNode? left, JsonNode? right)
    {
        if (left is null || right is null) return left is null && right is null;
        var kind = left.GetValueKind();
        if (kind != right.GetValueKind()) return false;
        switch (kind)
        {
            case JsonValueKind.Object:
                var a = left.AsObject(); var b = right.AsObject();
                return a.Count == b.Count && a.All(entry => b.TryGetPropertyValue(entry.Key, out var other) && Same(entry.Value, other));
            case JsonValueKind.Array:
                var x = left.AsArray(); var y = right.AsArray();
                return x.Count == y.Count && x.Zip(y).All(pair => Same(pair.First, pair.Second));
            case JsonValueKind.Number:
                return double.Parse(left.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture) ==
                    double.Parse(right.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
            case JsonValueKind.String:
                return left.GetValue<string>() == right.GetValue<string>();
            default:
                return true;
        }
    }

    /// <summary>An object's own keys in order (Object.keys, Reflect.ownKeys of a plain JSON object).</summary>
    internal static IEnumerable<string> Keys(JsonNode? node) => node is JsonObject value ? value.Select(entry => entry.Key) : [];
}
