using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

// Web Agent W2 (WU2, plan "Architecture" item 2): PiSharp.ModelRuntime, the registry, catalog, models.json, provider auth catalog and
// live provider routes moved out of PiSharp.Cli. PiSharp-only, so every case is authored; the runner is PiSharp.Durable.Hosting.Tests'.
// Every static method of Program whose name ends in "Cases" and returns IEnumerable<(string, Func<Task>)> is a case group. No case
// reads the user's ~/.pi, auth.json or environment: environments are explicit maps, credentials are fake sources and every HTTP
// client answers in process (a refusing handler fails any request a case did not expect).
internal static partial class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 0 && (args.Length != 2 || args[0] is not ("--report" or "--only")) && (args.Length != 4 || args[0] != "--only" || args[2] != "--report"))
            throw new ArgumentException("Use [--only <case prefix>] [--report <fresh path>].");
        var cases = new List<(string Id, Func<Task> Run)>();
        foreach (var method in typeof(Program).GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(method => method.Name.EndsWith("Cases", StringComparison.Ordinal) && method.GetParameters().Length == 0 &&
                method.ReturnType == typeof(IEnumerable<(string, Func<Task>)>)).OrderBy(method => method.Name, StringComparer.Ordinal))
            cases.AddRange((IEnumerable<(string, Func<Task>)>)method.Invoke(null, null)!);
        if (cases.GroupBy(test => test.Id, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key).ToList() is { Count: > 0 } duplicates)
            throw new InvalidOperationException("Duplicate case ids: " + string.Join(", ", duplicates));
        string? reportPath = null;
        for (var index = 0; index < args.Length; index += 2)
        {
            if (args[index] == "--only") { var prefix = args[index + 1]; cases = [.. cases.Where(test => test.Id.StartsWith(prefix, StringComparison.Ordinal))]; }
            else reportPath = Path.GetFullPath(args[index + 1]);
        }
        var results = new List<object>();
        var failures = 0;
        foreach (var test in cases)
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            Console.Error.WriteLine("start " + test.Id);
            try
            {
                await test.Run().WaitAsync(TimeSpan.FromSeconds(120));
                results.Add(new { test.Id, status = "PASS_AUTHORED_NATIVE_ONLY", ms = started.ElapsedMilliseconds });
            }
            catch (Exception error)
            {
                failures++;
                results.Add(new { test.Id, status = "FAIL", failure = error.ToString() });
            }
        }
        var report = new { status = "AUTHORED NATIVE; PISHARP-ONLY (Web Agent W2, WU2)", cases = cases.Count, failures, results };
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
    internal static T Throws<T>(Action run, string what) where T : Exception
    {
        try { run(); } catch (T error) { return error; }
        throw new InvalidOperationException(what + ": expected " + typeof(T).Name + ".");
    }

    /// <summary>An explicit environment: only the given names are set.</summary>
    internal static Func<string, string?> Env(params (string Name, string Value)[] values) => name =>
    {
        foreach (var (key, value) in values) if (key == name) return value;
        return null;
    };

    /// <summary>Fails every request: a case that must not reach the network uses it for every client it does not script.</summary>
    internal sealed class RefusingHandler : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            throw new InvalidOperationException("Unexpected request to " + request.RequestUri);
        }
    }

    /// <summary>One request a fake endpoint saw.</summary>
    internal sealed record Seen(string Url, IReadOnlyDictionary<string, string> Headers, string? Body);

    /// <summary>An in-process provider endpoint: records each request and answers it.</summary>
    internal sealed class FakeEndpoint(Func<Seen, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly List<Seen> requests = [];
        internal Seen[] Snapshot() { lock (requests) return [.. requests]; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .ToDictionary(pair => pair.Key, pair => string.Join(",", pair.Value), StringComparer.OrdinalIgnoreCase);
            var seen = new Seen(request.RequestUri!.ToString(), headers, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            lock (requests) requests.Add(seen);
            return respond(seen);
        }
    }

    /// <summary>One Chat Completions stream that completes with "ok".</summary>
    internal const string CompletionsSse = "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";

    internal static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
}
