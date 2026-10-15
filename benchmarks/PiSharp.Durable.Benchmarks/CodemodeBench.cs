// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/codemode-bench.ts. Durable code mode overhead: one model-issued
// call makes N calls of a trivial `echo` tool, either sequentially or all at once, in two ways: a code mode script (`tools.echo()`, journaled
// and replayable) and a plain tool calling ExecuteToolAsync the same way (nested calls, no journal). Reports wall time and commits per call on
// SQLite.
// Mappings: performance.now() is a Stopwatch; the sandbox is PiSharp.Codemode's CodemodeSandbox (256 MiB, engines on threads unless the host
// sets a worker launcher, as upstream's run on worker threads); the faux model is FauxModels.cs's; commits are counted with
// SubscribeCommits, as upstream's harness.subscribeCommits.
using System.Diagnostics;
using System.Globalization;
using PiSharp.Codemode;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Tools;

namespace PiSharp.Durable.Benchmarks;

/// <summary>Upstream <c>codemode-bench.ts</c>.</summary>
public static class CodemodeBench
{
    /// <summary>One run: its wall time and the commits it took.</summary>
    public sealed record Run(double Ms, int Commits);

    /// <summary>One row: a shape and size, run plain and in code mode.</summary>
    public sealed record Row(string Shape, int N, Run Plain, Run Codemode);

    private static readonly DurableContext Context = DurableContext.Background;

    /// <summary>Upstream's default sizes, <c>100,1000</c>, or the comma-separated list given.</summary>
    public static IReadOnlyList<int> ParseSizes(string? text) =>
        text is null ? [100, 1000] : [.. text.Split(',').Select(size => int.Parse(size, CultureInfo.InvariantCulture))];

    private static readonly JsonObjectValue EchoParameters =
        (JsonObjectValue)JsonValue.Parse("""{"type":"object","required":["i"],"properties":{"i":{"type":"number"}}}""");

    private static readonly JsonObjectValue PlainParameters =
        (JsonObjectValue)JsonValue.Parse("""{"type":"object","required":["n","shape"],"properties":{"n":{"type":"number"},"shape":{"type":"string"}}}""");

    private static JsonArrayValue Text(string text) => JsonValue.Array(JsonValue.Object(
        KeyValuePair.Create("type", (JsonValue)"text"), KeyValuePair.Create("text", (JsonValue)text)));

    private static readonly ToolRegistration Echo = Define.Tool(new ToolRegistration
    {
        Name = "echo",
        Description = "echo",
        Parameters = EchoParameters,
        Execute = (args, _, _) => Task.FromResult(new ToolExecutionResult { Output = Text(((JsonObjectValue)args)["i"].ToJson()) }),
    });

    private static readonly ToolRegistration Plain = Define.Tool(new ToolRegistration
    {
        Name = "plain",
        Description = "plain",
        Parameters = PlainParameters,
        Replay = ToolReplay.Safe,
        Execute = async (args, api, callContext) =>
        {
            var arguments = (JsonObjectValue)args;
            var n = (int)((JsonNumberValue)arguments["n"]).Value;
            JsonObjectValue Echoed(int i) => JsonValue.Object(KeyValuePair.Create("i", (JsonValue)i));
            if (((JsonStringValue)arguments["shape"]).Value == "sequential")
            {
                for (var i = 0; i < n; i++) await api.ExecuteToolAsync("echo", Echoed(i), callContext);
            }
            else
            {
                await Task.WhenAll(Enumerable.Range(0, n).Select(i => api.ExecuteToolAsync("echo", Echoed(i), callContext)));
            }
            return new ToolExecutionResult { Output = Text("done") };
        },
    });

    /// <summary>Runs every shape and size and writes upstream's lines.</summary>
    public static async Task<IReadOnlyList<Row>> RunAsync(IReadOnlyList<int> sizes, TextWriter output, string? tempRoot = null)
    {
        var rows = new List<Row>();
        foreach (var shape in new[] { "sequential", "parallel" })
        {
            foreach (var n in sizes)
            {
                var plain = await RunOnceAsync("plain", shape, n, tempRoot);
                var codemode = await RunOnceAsync("codemode", shape, n, tempRoot);
                rows.Add(new Row(shape, n, plain, codemode));
                string Per(double value) => (value / n).ToString("F2", CultureInfo.InvariantCulture);
                string Ms(double value) => Math.Round(value).ToString(CultureInfo.InvariantCulture);
                output.WriteLine($"{shape} n={n}: plain {Ms(plain.Ms)} ms ({Per(plain.Ms)} ms, {Per(plain.Commits)} commits/call), " +
                    $"codemode {Ms(codemode.Ms)} ms ({Per(codemode.Ms)} ms, {Per(codemode.Commits)} commits/call)");
            }
        }
        return rows;
    }

    private static async Task<Run> RunOnceAsync(string mode, string shape, int n, string? tempRoot)
    {
        var directory = Path.Combine(tempRoot ?? Path.GetTempPath(), "pi-codemode-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var sandbox = new CodemodeSandbox(new CodemodeSandboxOptions { MemoryLimitBytes = 256 * 1024 * 1024 });
            var registry = Registry.Create();
            registry.Install(Define.Extension(new Extension
            {
                Name = "bench",
                Tools = [Echo, Plain, DurableCodemode.CreateTool(new CodemodeToolOptions { Sandbox = sandbox })],
            }));
            var code = shape == "sequential"
                ? $"for (let i = 0; i < {n}; i++) await tools.echo({{ i }}); return \"done\";"
                : $"await Promise.all(Array.from({{ length: {n} }}, (_, i) => tools.echo({{ i }}))); return \"done\";";
            var call = mode == "codemode"
                ? FauxModels.Calls("codemode", JsonValue.Object(KeyValuePair.Create("code", (JsonValue)code)), "c1")
                : FauxModels.Calls("plain", JsonValue.Object(KeyValuePair.Create("n", (JsonValue)n), KeyValuePair.Create("shape", (JsonValue)shape)), "c1");
            var storage = await SqliteStorage.OpenAsync(Path.Combine(directory, "session.sqlite"));
            var harness = await Harness.OpenAsync(storage, new HarnessOptions { Models = new FauxModels(call, FauxModels.Text("ok")), Registry = registry }, Context);
            var root = await harness.RootAsync(Context, new AgentChange { Model = FauxModels.Ref });
            var commits = 0;
            using var subscription = harness.SubscribeCommits((_, _) => Interlocked.Increment(ref commits));
            var started = Stopwatch.GetTimestamp();
            var settled = await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (settled.Status != SubmissionStatus.Done) throw new InvalidOperationException($"{mode} {shape} n={n}: {settled.Status}");
            await AssertSucceededAsync(root, $"{mode} {shape} n={n}");
            await harness.CloseAsync(Context);
            return new Run(ms, Volatile.Read(ref commits));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>A run that answers <c>ok</c> may still have failed its tool; the model-issued call's result says whether it did.</summary>
    private static async Task AssertSucceededAsync(IConversation root, string what)
    {
        var page = await root.EntriesAsync(new EntryQueryBounds(), 10, null, Context);
        var result = page.Items.FirstOrDefault(entry => entry.Kind == "pi.tool-result")?.Model;
        if (result is not { Count: > 0 } || result[0] is not JsonObjectValue message || message["isError"] is JsonBooleanValue { Value: true })
            throw new InvalidOperationException($"{what}: the call failed: {result?[0].ToJson()}");
    }
}
