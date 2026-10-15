// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/nested-tools-bench.ts. Per run, one model-issued tool makes `calls`
// parallel nested calls of `blob`, each streaming `bytes` of output in 8 chunks `gapMs` apart; at depth 2 it makes them through `calls / 10`
// parallel `fan` calls of 10 each. With progress off the nested calls commit no running output. Reports commits, logical bytes written (JSON of
// the storage writes) by write type, the stored size after each run, and the heap: its peak during a run, sampled every 5 ms, and what stays
// after a run and a collection, both over the heap after a collection before the first run.
// Mappings: the heap is the managed heap (GC.GetTotalMemory for samples, HeapRetained after a compacting collection), not V8's heapUsed; the
// gaps are Task.Delay on the system clock; the faux model is FauxModels.cs's.
using System.Diagnostics;
using System.Globalization;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage.Jsonl;

namespace PiSharp.Durable.Benchmarks;

/// <summary>Upstream <c>nested-tools-bench.ts</c>.</summary>
public static class NestedToolsBench
{
    /// <summary>One scenario (upstream <c>Scenario</c>).</summary>
    public sealed record Scenario(string Backend, int Calls, int Depth, int Bytes, int GapMs, bool Progress, int Runs);

    /// <summary>One run's measurements (upstream <c>Run</c>).</summary>
    public sealed record Run(int Commits, IReadOnlyDictionary<string, long> Written, long StoredBytes, double Ms, long PeakHeap, long HeldHeap);

    private const int Chunks = 8;
    /// <summary>Calls each inner <c>fan</c> makes at depth 2.</summary>
    private const int FanOut = 10;
    private static readonly DurableContext Context = DurableContext.Background;

    /// <summary>The streaming section's scenarios (upstream's defaults), with <paramref name="calls"/> and <paramref name="runs"/>.</summary>
    public static IEnumerable<Scenario> Streaming(int calls = 200, int runs = 3) =>
        from backend in new[] { "sqlite", "jsonl" }
        from bytes in new[] { 1024, 16 * 1024 }
        from gap in new[] { 2, 50 }
        from progress in new[] { true, false }
        select new Scenario(backend, calls, 1, bytes, gap, progress, runs);

    /// <summary>The nesting section's scenarios (upstream's defaults).</summary>
    public static IEnumerable<Scenario> Nesting(IReadOnlyList<int>? calls = null, int runs = 3) =>
        from backend in new[] { "sqlite", "jsonl" }
        from count in calls ?? [200, 2000]
        from depth in new[] { 1, 2 }
        select new Scenario(backend, count, depth, 1024, 2, false, runs);

    /// <summary>Runs <paramref name="section"/> (streaming, nesting, or both when null) and writes upstream's tables.</summary>
    public static async Task<IReadOnlyList<(Scenario Scenario, IReadOnlyList<Run> Runs, long ClosedBytes)>> RunAsync(string? section, TextWriter output,
        string? tempRoot = null, IReadOnlyList<Scenario>? only = null)
    {
        var results = new List<(Scenario, IReadOnlyList<Run>, long)>();
        if (only is not null)
        {
            await ReportAsync("Selected scenarios", only, output, tempRoot, results);
            return results;
        }
        if (section is null or "streaming") await ReportAsync("Streaming: 200 calls, with and without progress", [.. Streaming()], output, tempRoot, results);
        if (section is null or "nesting") await ReportAsync("Nesting: fast calls without progress, at depth 1 and 2", [.. Nesting()], output, tempRoot, results);
        return results;
    }

    private static string Mib(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024.0:F2} MiB");

    private static async Task ReportAsync(string title, IReadOnlyList<Scenario> scenarios, TextWriter output, string? tempRoot,
        List<(Scenario, IReadOnlyList<Run>, long)> results)
    {
        output.WriteLine();
        output.WriteLine(title);
        output.WriteLine("backend | calls | depth | output | gap   | stream | run 1: commits, ms, written (all; tasks / doc changes / doc creates) | stored after run 1 / 2 / 3 | after close | heap: run 1 peak; held after run 1 / 2 / 3");
        foreach (var scenario in scenarios)
        {
            var (runs, closedBytes) = await RunScenarioAsync(scenario, tempRoot);
            results.Add((scenario, runs, closedBytes));
            var first = runs[0];
            var all = first.Written.Values.Sum();
            output.WriteLine(string.Join(" | ",
                scenario.Backend.PadRight(6),
                scenario.Calls.ToString(CultureInfo.InvariantCulture).PadRight(5),
                scenario.Depth.ToString(CultureInfo.InvariantCulture).PadRight(5),
                $"{scenario.Bytes / 1024} KiB".PadRight(6),
                $"{scenario.GapMs} ms".PadRight(5),
                (scenario.Progress ? "yes" : "no").PadRight(6),
                string.Create(CultureInfo.InvariantCulture,
                    $"{first.Commits}, {Math.Round(first.Ms)} ms, {Mib(all)} ({Mib(first.Written["task"])} / {Mib(first.Written["document.change"])} / {Mib(first.Written["document.create"])})"),
                string.Join(" / ", runs.Select(run => Mib(run.StoredBytes))),
                Mib(closedBytes),
                $"{Mib(first.PeakHeap)}; {string.Join(" / ", runs.Select(run => Mib(run.HeldHeap)))}"));
        }
    }

    private static long DirectorySize(string path) => new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);

    private static JsonArrayValue Text(string text) => JsonValue.Array(JsonValue.Object(
        KeyValuePair.Create("type", (JsonValue)"text"), KeyValuePair.Create("text", (JsonValue)text)));

    private static readonly JsonObjectValue BlobParameters =
        (JsonObjectValue)JsonValue.Parse("""{"type":"object","required":["n"],"properties":{"n":{"type":"number"}}}""");

    private static readonly JsonObjectValue FanParameters =
        (JsonObjectValue)JsonValue.Parse("""{"type":"object","required":["count"],"properties":{"count":{"type":"number"}}}""");

    private static async Task<(IReadOnlyList<Run> Runs, long ClosedBytes)> RunScenarioAsync(Scenario scenario, string? tempRoot)
    {
        var directory = Path.Combine(tempRoot ?? Path.GetTempPath(), "pi-durable-nested-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var registry = Registry.Create();
            var chunk = new string('x', scenario.Bytes / Chunks - 1) + "\n";
            // Streams its output and returns none, so its result is the retained output, as bash's is.
            var blob = Define.Tool(new ToolRegistration
            {
                Name = "blob",
                Description = "Stream a blob",
                Parameters = BlobParameters,
                Execute = async (_, api, _) =>
                {
                    for (var n = 0; n < Chunks; n++)
                    {
                        api.Output(chunk);
                        await Task.Delay(scenario.GapMs);
                    }
                    return new ToolExecutionResult();
                },
            });
            var options = scenario.Progress ? null : new NestedCallOptions { Progress = false };
            ToolRegistration FanOutTool(string name, string target) => Define.Tool(new ToolRegistration
            {
                Name = name,
                Description = $"Call {target} in parallel",
                Parameters = FanParameters,
                Execute = async (args, api, callContext) =>
                {
                    var count = (int)((JsonNumberValue)((JsonObjectValue)args)["count"]).Value;
                    var results = await Task.WhenAll(Enumerable.Range(0, count).Select(n => api.ExecuteToolAsync(target,
                        JsonValue.Object(KeyValuePair.Create("n", (JsonValue)n), KeyValuePair.Create("count", (JsonValue)FanOut)), callContext, options)));
                    var failed = results.Count(result => result.IsError);
                    return new ToolExecutionResult { Output = Text($"{failed} failed"), IsError = failed > 0 };
                },
            });
            registry.Install(Define.Extension(new Extension { Name = "bench", Tools = [blob, FanOutTool("fan", "blob"), FanOutTool("fan2", "fan")] }));

            IStorage inner = scenario.Backend == "sqlite"
                ? await SqliteStorage.OpenAsync(Path.Combine(directory, "session.sqlite"))
                : await JsonlStorage.OpenAsync(directory, Context);
            var storage = new CountingStorage(inner);
            var top = scenario.Depth == 1 ? scenario.Calls : scenario.Calls / FanOut;
            var replies = Enumerable.Range(0, scenario.Runs).SelectMany(run => new[]
            {
                FauxModels.Calls(scenario.Depth == 1 ? "fan" : "fan2", JsonValue.Object(KeyValuePair.Create("count", (JsonValue)top)), $"r{run}"),
                FauxModels.Text("done"),
            }).ToArray();
            var harness = await Harness.OpenAsync(storage, new HarnessOptions { Models = new FauxModels(replies), Registry = registry }, Context);
            var root = await harness.RootAsync(Context, new AgentChange { Model = FauxModels.Ref });
            var runs = new List<Run>();
            var baseHeap = await HeapRetained.RetainedBytesAsync();
            for (var run = 0; run < scenario.Runs; run++)
            {
                var before = (storage.Commits, Written: storage.Written());
                long peakHeap = 0;
                using var sampler = new Timer(_ => InterlockedMax(ref peakHeap, GC.GetTotalMemory(false)), null, 0, 5);
                var started = Stopwatch.GetTimestamp();
                var settled = await (await root.SubmitAsync(SubmissionDraft.Input($"run {run}"), Context)).WaitAsync(Context);
                if (settled.Status != SubmissionStatus.Done) throw new InvalidOperationException($"run {run}: {settled.Status}");
                await AssertToolsSucceededAsync(root, run);
                await harness.WaitForIdleAsync(Context);
                var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                await sampler.DisposeAsync();
                var after = storage.Written();
                runs.Add(new Run(storage.Commits - before.Commits, after.ToDictionary(pair => pair.Key, pair => pair.Value - before.Written[pair.Key]),
                    DirectorySize(directory), ms, Interlocked.Read(ref peakHeap) - baseHeap, await HeapRetained.RetainedBytesAsync() - baseHeap));
            }
            await harness.CloseAsync(Context);
            return (runs, DirectorySize(directory));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void InterlockedMax(ref long target, long value)
    {
        var current = Interlocked.Read(ref target);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current) return;
            current = seen;
        }
    }

    /// <summary>A run that answers <c>done</c> may still have failed tools; the model-issued call's result says whether any did.</summary>
    private static async Task AssertToolsSucceededAsync(IConversation root, int run)
    {
        var page = await root.EntriesAsync(new EntryQueryBounds(), 10, null, Context);
        var result = page.Items.FirstOrDefault(entry => entry.Kind == "pi.tool-result")?.Model;
        if (result is not { Count: > 0 } || result[0] is not JsonObjectValue message || message["isError"] is JsonBooleanValue { Value: true })
            throw new InvalidOperationException($"run {run}: a nested call failed");
    }
}
