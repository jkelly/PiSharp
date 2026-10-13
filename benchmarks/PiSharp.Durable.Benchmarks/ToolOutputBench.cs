// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/tool-output-bench.ts
// Tool output benchmark: drives the real tool task, adaptive progress throttle and pi.live commits. Each scenario runs in its own child
// process so its peak working set is its own. Rate scenarios write output for a fixed time, then close the Harness mid-round (persistent
// backends) to measure replay of the round's delta chain on reopen, and finish the run to measure the stored size after the round's base and
// reclamation. Throughput scenarios push 1 GiB. Node's setImmediate is Task.Yield, its setTimeout Task.Delay; maxRSS is the peak working
// set and the heap after gc the GC heap after a forced collection. The `cat` command names the file with forward slashes, which Git Bash
// needs on Windows (upstream joins the path with backslashes there, so its cat fails at once on Windows; see docs/durable-benchmarks.md),
// and the scenario deletes the env's full-output spill of the cat afterwards.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Env;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Storage.Jsonl;
using PiSharp.Durable.Tools;
using PiSharp.Durable.Tools.Env;

namespace PiSharp.Durable.Benchmarks;

/// <summary>One tool output scenario (upstream <c>Scenario</c>): <c>rate</c>, <c>firehose</c> or <c>cat</c>.</summary>
public sealed record ToolOutputScenario
{
    /// <summary><c>rate</c>, <c>firehose</c> or <c>cat</c>.</summary>
    public required string Kind { get; init; }
    /// <summary><c>memory</c>, <c>sqlite</c> or <c>jsonl</c>.</summary>
    public required string Backend { get; init; }
    /// <summary>A rate scenario's <c>low</c>, <c>normal</c> or <c>high</c>.</summary>
    public string? Rate { get; init; }
    /// <summary><c>head</c> or <c>tail</c>; not for cat.</summary>
    public string? Retain { get; init; }
    /// <summary>A rate scenario's parallel tool calls.</summary>
    public int? Tools { get; init; }
    /// <summary>A rate scenario's duration in seconds; default 3.</summary>
    public double? Seconds { get; init; }
    /// <summary>A firehose of identical lines.</summary>
    public bool? Repetitive { get; init; }

    /// <summary>Upstream's <c>label()</c>.</summary>
    public string Label => Kind switch
    {
        "rate" => $"{Rate} {Retain} x{Tools}{(Seconds is { } seconds ? $" {seconds.ToString(CultureInfo.InvariantCulture)}s" : "")}",
        "cat" => "bash cat 1 GiB",
        _ => $"api.output 1 GiB {Retain}{(Repetitive == true ? " repetitive" : "")}",
    };
}

/// <summary>What one scenario measured (upstream <c>Metrics</c>).</summary>
public sealed record ToolOutputMetrics
{
    /// <summary>Submit to settled.</summary>
    public long Ms { get; init; }
    /// <summary><c>pi.live</c> document changes committed.</summary>
    public int LiveCommits { get; init; }
    /// <summary>Their operations' JSON size.</summary>
    public long LiveOpKiB { get; init; }
    /// <summary>Output writes recorded as a whole-string set, including each slot's first write; the rest are appends and trims.</summary>
    public int OutputSets { get; init; }
    /// <summary>Median storage commit latency.</summary>
    public double CommitP50Ms { get; init; }
    /// <summary>99th percentile storage commit latency.</summary>
    public double CommitP99Ms { get; init; }
    /// <summary>Stored size when the Harness closes mid-round.</summary>
    public long? MidRoundKiB { get; init; }
    /// <summary>Reopen plus the first live snapshot.</summary>
    public long? ReplayMs { get; init; }
    /// <summary>Stored size after the run.</summary>
    public long? FinalKiB { get; init; }
    /// <summary>Peak working set of the process.</summary>
    public long PeakRssMiB { get; init; }
    /// <summary>GC heap after a forced collection.</summary>
    public long? HeapAfterGcMiB { get; init; }
}

/// <summary>Sizes the smoke test shrinks; the defaults are upstream's.</summary>
public sealed record ToolOutputOptions
{
    /// <summary>Upstream <c>RATE_SECONDS</c>.</summary>
    public double RateSeconds { get; init; } = 3;
    /// <summary>Upstream <c>GIB</c>: what a firehose writes and the size of the cat file.</summary>
    public long Bytes { get; init; } = 1L << 30;
    /// <summary>The wait for the last progress commit before a mid-round close (upstream 600 ms).</summary>
    public int SettleMs { get; init; } = 600;
    /// <summary>Upstream <c>BIG_FILE</c>.</summary>
    public string BigFile { get; init; } = Path.Combine(Path.GetTempPath(), "pi-durable-bench-1gib.txt");
}

/// <summary>The tool output benchmark (upstream <c>test/tool-output-bench.ts</c>).</summary>
public static class ToolOutputBench
{
    private static readonly DurableContext Context = DurableContext.Background;
    private const string Line = "The quick brown fox jumps over the lazy dog. Unique identifier";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Output one tool writes per tick, and the pause between ticks (upstream <c>RATES</c>).</summary>
    private static (int Lines, int PauseMs) Rates(string rate) => rate switch
    {
        // A line every 200 ms, like a slow build step.
        "low" => (1, 200),
        // Bursts of 25 lines every 50 ms (~40 KB/s), like a compiler or test run.
        "normal" => (25, 50),
        // Continuous 64 KiB chunks, yielding between them.
        _ => (800, 0),
    };

    /// <summary>Upstream's scenario list, in order.</summary>
    public static IReadOnlyList<ToolOutputScenario> Scenarios()
    {
        var scenarios = new List<ToolOutputScenario>();
        foreach (var backend in new[] { "memory", "sqlite", "jsonl" })
        {
            foreach (var rate in new[] { "low", "normal", "high" })
                foreach (var retain in new[] { "head", "tail" })
                    foreach (var tools in new[] { 1, 4 })
                        scenarios.Add(new() { Kind = "rate", Backend = backend, Rate = rate, Retain = retain, Tools = tools });
            foreach (var retain in new[] { "head", "tail" }) scenarios.Add(new() { Kind = "firehose", Backend = backend, Retain = retain });
            // Identical lines: how often Chord's bounded overlap search falls back to writing the whole tail window.
            scenarios.Add(new() { Kind = "firehose", Backend = backend, Retain = "tail", Repetitive = true });
            // One long round: the delta chain a chatty tail tool builds before the round's base.
            if (backend != "memory") scenarios.Add(new() { Kind = "rate", Backend = backend, Rate = "high", Retain = "tail", Tools = 1, Seconds = 30 });
            scenarios.Add(new() { Kind = "cat", Backend = backend });
        }
        return scenarios;
    }

    /// <summary>Runs every scenario in its own child process (upstream <c>main</c>) and prints the table.</summary>
    public static async Task<IReadOnlyList<(ToolOutputScenario Scenario, ToolOutputMetrics Metrics)>> RunDriverAsync(TextWriter output,
        Func<ToolOutputScenario, bool>? filter = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        var options = new ToolOutputOptions();
        await EnsureBigFileAsync(options.BigFile, options.Bytes);
        var rows = new List<(ToolOutputScenario, ToolOutputMetrics)>();
        await output.WriteLineAsync(TableHeader);
        foreach (var scenario in Scenarios().Where(filter ?? (_ => true)))
        {
            var stdout = await ChildProcess.RunAsync(["tool-output", "--worker", JsonSerializer.Serialize(scenario, Json)]);
            var metrics = JsonSerializer.Deserialize<ToolOutputMetrics>(stdout.Trim().Split('\n')[^1], Json)!;
            rows.Add((scenario, metrics));
            await output.WriteLineAsync(Row(scenario, metrics));
        }
        return rows;
    }

    /// <summary>The worker entry point: runs one scenario and prints its metrics as a JSON line.</summary>
    public static async Task<int> WorkerMainAsync(string encoded, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var scenario = JsonSerializer.Deserialize<ToolOutputScenario>(encoded, Json) ?? throw new ArgumentException("No scenario");
        await output.WriteLineAsync(JsonSerializer.Serialize(await RunScenarioAsync(scenario, new ToolOutputOptions()), Json));
        return 0;
    }

    /// <summary>The table header of <see cref="Row"/>.</summary>
    public const string TableHeader =
        "| scenario | backend | ms | liveCommits | liveOpKiB | outputSets | commitP50Ms | commitP99Ms | peakRssMiB | heapAfterGcMiB | midRoundKiB | replayMs | finalKiB |\n" +
        "|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|";

    /// <summary>One table row.</summary>
    public static string Row(ToolOutputScenario scenario, ToolOutputMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(metrics);
        static string Value(object? value) => value switch { null => "", IFormattable number => number.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString()! };
        return "| " + string.Join(" | ", scenario.Label, scenario.Backend, Value(metrics.Ms), Value(metrics.LiveCommits), Value(metrics.LiveOpKiB),
            Value(metrics.OutputSets), Value(metrics.CommitP50Ms), Value(metrics.CommitP99Ms), Value(metrics.PeakRssMiB), Value(metrics.HeapAfterGcMiB),
            Value(metrics.MidRoundKiB), Value(metrics.ReplayMs), Value(metrics.FinalKiB)) + " |";
    }

    /// <summary>Writes <paramref name="bytes"/> of unique lines to <paramref name="path"/> unless it already has that size (upstream
    /// <c>ensureBigFile</c>: the same lines, written in 64 MiB blocks and cut at exactly the size).</summary>
    public static async Task EnsureBigFileAsync(string path, long bytes)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (File.Exists(path) && new FileInfo(path).Length == bytes) return;
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        long index = 0;
        var block = new StringBuilder();
        for (long written = 0; written < bytes;)
        {
            block.Clear();
            while (block.Length < 64 << 20)
            {
                index++;
                block.Append("Line ").Append(index - 1).Append(": ").Append(Line).Append(": ").Append((index * 7919).ToString("x", CultureInfo.InvariantCulture)).Append('\n');
            }
            var data = Encoding.UTF8.GetBytes(block.ToString());
            var count = (int)Math.Min(data.Length, bytes - written);
            await file.WriteAsync(data.AsMemory(0, count));
            written += count;
        }
    }

    /// <summary>Runs one scenario in this process (upstream <c>runScenario</c>).</summary>
    public static async Task<ToolOutputMetrics> RunScenarioAsync(ToolOutputScenario scenario, ToolOutputOptions options)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(options);
        var directory = Directory.CreateTempSubdirectory("pi-durable-tool-bench-").FullName;
        try
        {
            var path = Path.Combine(directory, scenario.Backend == "sqlite" ? "session.sqlite" : "session");
            var latencies = new List<double>();
            async Task<IStorage> OpenTimed() => new TimedStorage(await OpenStorageAsync(scenario.Backend, path), latencies);
            var stop = false;
            var registry = Registry.Create();
            var toolCount = scenario.Kind == "rate" ? scenario.Tools ?? 1 : 1;
            if (scenario.Kind == "cat")
            {
                registry.Install(new Extension { Name = "tool:bash", Tools = [CodingTools.CreateBashTool()] });
            }
            else
            {
                var rate = Rates(scenario.Kind == "rate" ? scenario.Rate! : "high");
                registry.Install(new Extension
                {
                    Name = "tool:emit",
                    Tools =
                    [
                        new ToolRegistration
                        {
                            Name = "emit",
                            Description = "",
                            OutputLimits = new ToolOutputLimits { Retain = scenario.Retain == "tail" ? ToolOutputRetention.Tail : ToolOutputRetention.Head },
                            Execute = async (_, api, callContext) =>
                            {
                                long counter = 0, written = 0;
                                // The firehose reuses one prebuilt chunk so it measures the Harness, not string building.
                                var prebuilt = scenario.Kind == "firehose" ? Lines(api.CallId, 0, rate.Lines) : null;
                                // Repetitive: identical lines with one numbered line per chunk, so the tail window keeps sliding.
                                var repeated = string.Concat(Enumerable.Repeat("y\n", 32 * 1024 - 8));
                                while (!Volatile.Read(ref stop) && (scenario.Kind == "rate" || written < options.Bytes))
                                {
                                    var chunk = scenario.Kind == "firehose" && scenario.Repetitive == true
                                        ? $"{repeated}#{counter}\n"
                                        : prebuilt ?? Lines(api.CallId, counter, rate.Lines);
                                    counter += rate.Lines;
                                    api.Output(chunk);
                                    written += chunk.Length;
                                    if (rate.PauseMs == 0) await Task.Yield(); else await Task.Delay(rate.PauseMs);
                                }
                                // A persistent rate scenario stays in flight until close, so reopen replays the round.
                                if (scenario.Kind == "rate" && scenario.Backend != "memory")
                                    await Task.Delay(Timeout.Infinite, callContext.CancellationToken);
                                return new ToolExecutionResult();
                            },
                        },
                    ],
                });
            }
            var bigFile = options.BigFile.Replace('\\', '/');
            var calls = scenario.Kind == "cat"
                ? FauxModels.Calls("bash", JsonValue.Object([new("command", "cat " + bigFile)]), "c0")
                : FauxModels.Calls("emit", JsonObjectValue.Empty, [.. Enumerable.Range(0, toolCount).Select(index => $"c{index}")]);
            var models = new FauxModels(calls, FauxModels.Text("done"));
            var env = new LocalExecutionEnv(directory);
            var harnessOptions = new HarnessOptions
            {
                Models = models, Registry = registry, Env = (_, _) => Task.FromResult<IExecutionEnv?>(env),
            };
            var harness = await Harness.OpenAsync(await OpenTimed(), harnessOptions, Context);
            var root = await harness.RootAsync(Context, new AgentChange { Model = FauxModels.Ref });
            int liveCommits = 0, outputSets = 0;
            long liveOpBytes = 0;
            void Observe(IHarness target) => target.SubscribeCommits((publication, _) =>
            {
                foreach (var change in publication.Changes)
                {
                    if (change is not DocumentCommitChange { Record.Kind: "pi.live" } document) continue;
                    Interlocked.Increment(ref liveCommits);
                    Interlocked.Add(ref liveOpBytes, JsonValue.Array(document.Ops.Select(op => (JsonValue)op.ToJson())).ToJson().Length);
                    foreach (var op in document.Ops)
                        if (op is SetOp set && set.Path.Length > 0 && set.Path[^1] == PathSegment.Of("output")) Interlocked.Increment(ref outputSets);
                }
            });
            Observe(harness);
            var started = Stopwatch.GetTimestamp();
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            long? midRoundKiB = null, replayMs = null, finalKiB = null;
            if (scenario.Kind == "rate")
            {
                await Task.Delay(TimeSpan.FromSeconds(scenario.Seconds ?? options.RateSeconds));
                Volatile.Write(ref stop, true);
                if (scenario.Backend != "memory")
                {
                    // Let the last progress commit land, then close mid-round and time replay on reopen.
                    await Task.Delay(options.SettleMs);
                    midRoundKiB = Footprint(scenario.Backend, path);
                    await harness.CloseAsync(Context);
                    var reopened = Stopwatch.GetTimestamp();
                    harness = await Harness.OpenAsync(await OpenTimed(), harnessOptions, Context);
                    await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context);
                    Observe(harness);
                    replayMs = (long)Math.Round(Stopwatch.GetElapsedTime(reopened).TotalMilliseconds);
                    harness.Resume();
                    await (await harness.SubmissionAsync(submission.Id, Context))!.WaitAsync(Context);
                }
                else await submission.WaitAsync(Context);
            }
            else await submission.WaitAsync(Context);
            var ms = (long)Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (scenario.Backend != "memory") finalKiB = Footprint(scenario.Backend, path);
            if (scenario.Kind == "cat") await DeleteSpillAsync(root);
            await harness.CloseAsync(Context);
            double[] sorted;
            lock (latencies) sorted = [.. latencies.Order()];
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            using var process = Process.GetCurrentProcess();
            return new()
            {
                Ms = ms,
                LiveCommits = liveCommits,
                LiveOpKiB = (long)Math.Round(liveOpBytes / 1024.0),
                OutputSets = outputSets,
                CommitP50Ms = Round(sorted.Length == 0 ? 0 : sorted[(int)Math.Floor(sorted.Length * 0.5)]),
                CommitP99Ms = Round(sorted.Length == 0 ? 0 : sorted[(int)Math.Floor(sorted.Length * 0.99)]),
                MidRoundKiB = midRoundKiB,
                ReplayMs = replayMs,
                FinalKiB = finalKiB,
                PeakRssMiB = (long)Math.Round(process.PeakWorkingSet64 / 1024.0 / 1024.0),
                HeapAfterGcMiB = (long)Math.Round(GC.GetTotalMemory(forceFullCollection: false) / (double)(1 << 20)),
            };
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<IStorage> OpenStorageAsync(string backend, string path) => backend switch
    {
        "memory" => new MemoryStorage(),
        "sqlite" => await SqliteStorage.OpenAsync(path),
        _ => await JsonlStorage.OpenAsync(path, Context),
    };

    // The bash tool spills a large output to a temporary file and names it in the result; a 1 GiB spill per run is not left behind.
    private static async Task DeleteSpillAsync(IConversation conversation)
    {
        var page = await conversation.EntriesAsync(new EntryQueryBounds(), 20, null, Context);
        foreach (var entry in page.Items.Where(Entries.ToolResultEntry.Is))
            foreach (Match match in Regex.Matches(entry.Model!.ToJson(), @"Full output: ((?:[^""\\]|\\.)+?)\\n"))
            {
                var spill = JsonValue.Parse("\"" + match.Groups[1].Value + "\"") is JsonStringValue text ? text.Value : "";
                var parent = Path.GetDirectoryName(spill);
                if (parent is not null && Path.GetFileName(parent).StartsWith("tmp-", StringComparison.Ordinal) && Directory.Exists(parent))
                    Directory.Delete(parent, recursive: true);
                else if (File.Exists(spill)) File.Delete(spill);
            }
    }

    private static long Footprint(string backend, string path)
    {
        long bytes = 0;
        if (backend == "sqlite")
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) bytes += new FileInfo(path + suffix).Length;
        }
        else foreach (var file in new DirectoryInfo(path).EnumerateFiles()) bytes += file.Length;
        return (long)Math.Round(bytes / 1024.0);
    }

    private static string Lines(string prefix, long first, int count)
    {
        var text = new StringBuilder();
        for (var index = first; index < first + count; index++) text.Append(prefix).Append(' ').Append(index).Append(": ").Append(Line).Append('\n');
        return text.ToString();
    }

    private static double Round(double value) => Math.Round(value * 100) / 100;

    /// <summary>Measures every storage commit (upstream <c>timed()</c>, a Proxy over <c>commit</c>).</summary>
    private sealed class TimedStorage(IStorage inner, List<double> latencies) : IStorage
    {
        public async Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, DurableContext context)
        {
            var started = Stopwatch.GetTimestamp();
            try { return await inner.CommitAsync(writes, context); }
            finally { var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds; lock (latencies) latencies.Add(elapsed); }
        }
        public Task<TId> MintIdAsync<TId>() where TId : struct, IDurableId<TId> => inner.MintIdAsync<TId>();
        public Task<ConversationRecord?> GetConversationAsync(ConversationId id, DurableContext context) => inner.GetConversationAsync(id, context);
        public Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            inner.ScanConversationsAsync(query, limit, cursor, context);
        public Task<CommittedEntry?> GetEntryAsync(EntryId id, DurableContext context) => inner.GetEntryAsync(id, context);
        public Task<CommittedEntry?> GetEntryAsync(ConversationId conversationId, EntryId id, DurableContext context) =>
            inner.GetEntryAsync(conversationId, id, context);
        public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId, DurableContext context) =>
            inner.FindLatestHeadMarkerAsync(conversationId, atOrBeforeEntryId, context);
        public Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            inner.ScanEntriesAsync(query, limit, cursor, context);
        public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) => inner.GetTaskAsync(id, context);
        public Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            inner.ScanTasksAsync(query, limit, cursor, context);
        public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context) => inner.GetSubmissionAsync(id, context);
        public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            inner.ScanSubmissionsAsync(query, limit, cursor, context);
        public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context) =>
            inner.GetSubmissionByRequestAsync(conversationId, requestId, context);
        public Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at, DurableContext context) =>
            inner.FindDocumentAsync(address, at, context);
        public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at, DurableContext context) => inner.GetDocumentAsync(id, at, context);
        public Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            inner.ScanDocumentsAsync(query, limit, cursor, context);
        public Task CloseAsync(DurableContext context) => inner.CloseAsync(context);
    }
}
