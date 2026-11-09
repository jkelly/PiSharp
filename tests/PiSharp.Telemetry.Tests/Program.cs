using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Telemetry;

// Authored offline expectations for the pi-telemetry port of Pi v1.5.0 (packages/telemetry, PiSharp.Telemetry) and for the request tracing
// of packages/ai/src/telemetry.ts (PiSharp.AI.AiTelemetry). Expected values are copied from, or derived by reading, the pinned upstream
// tests (packages/telemetry/test/*.ts and packages/ai/test/telemetry.test.ts, cited per group); nothing is captured from an upstream run.
// TypeScript's compile-time rejections (@ts-expect-error) are checked as the ArgumentException the C# typed schemas throw instead. Every
// case runs in process, offline and faux, except the propagation case that re-launches this suite with a hidden flag, as upstream runs
// propagation-worker.ts in a second Node process.
internal static partial class Program
{
    private const string Upstream = "f9a26365568127461c47a07a37dcb42c140a3ac5";
    internal const string WorkerFlag = "--propagation-worker";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == WorkerFlag) return await PropagationWorker(args[1]);
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--report")) throw new ArgumentException("Use [--report <fresh path>].");
        var cases = new List<(string Id, Func<Task> Run)>();
        cases.AddRange(PackageCases);
        cases.AddRange(ConformanceCases);
        cases.AddRange(AiCases);
        cases.AddRange(AotJsonCases);
        var results = new List<object>(); var failures = 0;
        foreach (var test in cases)
        {
            try { await test.Run().WaitAsync(TimeSpan.FromSeconds(120)); results.Add(new { test.Id, status = "PASS_AUTHORED_NATIVE_ONLY" }); }
            catch (Exception error) { failures++; results.Add(new { test.Id, status = "FAIL", failure = error.ToString() }); }
        }
        var report = new { sourceSha = Upstream, status = "AUTHORED NATIVE; NO UPSTREAM CAPTURE", cases = cases.Count, failures,
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

    /// <summary>propagation-worker.ts: a fresh recorder in this second process continues the carrier's parent and prints its spans.</summary>
    private static async Task<int> PropagationWorker(string carrierJson)
    {
        var telemetry = new InMemoryTelemetryContext();
        var carrier = JsonSerializer.Deserialize<Dictionary<string, string>>(carrierJson);
        await telemetry.Extract(carrier).StartSpanAsync("remote", _ => { });
        Console.Out.Write(JsonSerializer.Serialize(telemetry.GetSpans()));
        return 0;
    }

    /// <summary>Re-launches this suite (its apphost, or <c>dotnet</c> with its assembly) as the propagation worker.</summary>
    private static async Task<string> RunWorker(IReadOnlyDictionary<string, string> carrier)
    {
        var host = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
        var start = new ProcessStartInfo(host) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add(WorkerFlag);
        start.ArgumentList.Add(JsonSerializer.Serialize(carrier));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The worker did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(deadline.Token);
        Check(process.ExitCode == 0, "worker exit " + process.ExitCode + ": " + await error);
        return await output;
    }

    private static Func<Task> Sync(Action run) => () => { run(); return Task.CompletedTask; };
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static void Equal<T>(T expected, T actual, string what)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{what}: expected <{expected}>, actual <{actual}>."); }
    private static void Same(object? expected, object? actual, string what)
    { if (!ReferenceEquals(expected, actual)) throw new InvalidOperationException(what + ": expected the same instance."); }

    /// <summary>deepStrictEqual / toEqual through the values' JSON forms, ignoring object key order.</summary>
    private static void Json(object? expected, object? actual, string what)
    {
        var left = Node(expected);
        var right = Node(actual);
        if (!JsonNode.DeepEquals(left, right))
            throw new InvalidOperationException($"{what}: expected {left?.ToJsonString() ?? "null"}, actual {right?.ToJsonString() ?? "null"}.");
    }

    /// <summary>toMatchObject: every expected top-level property equals the actual one's JSON form.</summary>
    private static void Subset(object expected, object? actual, string what)
    {
        var left = Node(expected)?.AsObject() ?? throw new InvalidOperationException(what + ": no expected object.");
        if (Node(actual) is not JsonObject right) throw new InvalidOperationException(what + ": actual is not an object.");
        foreach (var (name, value) in left)
            if (!JsonNode.DeepEquals(value, right[name]))
                throw new InvalidOperationException($"{what}.{name}: expected {value?.ToJsonString() ?? "null"}, actual {right[name]?.ToJsonString() ?? "null"}.");
    }

    private static JsonNode? Node(object? value) => JsonNode.Parse(JsonSerializer.Serialize(value));

    private static string Text(object? value) => JsonSerializer.Serialize(value);

    private static T Throws<T>(Action run, string what) where T : Exception
    {
        try { run(); } catch (T error) { return error; }
        throw new InvalidOperationException(what + ": expected " + typeof(T).Name + ".");
    }

    private static async Task Rejects(Task operation, Exception expected, string what)
    {
        try { await operation; }
        catch (Exception error) { Same(expected, error, what); return; }
        throw new InvalidOperationException(what + ": expected a rejection.");
    }

    private static Dictionary<string, string> Roundtrip(IReadOnlyDictionary<string, string> carrier) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(JsonSerializer.Serialize(carrier))!;

    /// <summary>vi.waitFor(every span settled).</summary>
    private static async Task Settled(InMemoryTelemetryContext telemetry)
    {
        for (var attempt = 0; attempt < 400 && !telemetry.GetSpans().All(span => span.Settled); attempt++) await Task.Delay(5);
        Check(telemetry.GetSpans().All(span => span.Settled), "every span settles");
    }

    /// <summary>vi.spyOn(Date, "now") and vi.spyOn(performance, "now"): <see cref="NowMs"/> is the wall clock, <see cref="ClockMs"/>
    /// the monotonic timestamp clock.</summary>
    internal sealed class ManualTime(double nowMs = 0, double clockMs = 0) : TimeProvider
    {
        public double NowMs { get; set; } = nowMs;
        public double ClockMs { get; set; } = clockMs;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds((long)NowMs);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => (long)Math.Round(ClockMs * TimeSpan.TicksPerMillisecond);
    }
}
