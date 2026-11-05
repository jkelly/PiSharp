// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/conversation-view-bench.ts (added in f3255de76, area view).
// Conversation view benchmark: a long conversation (thousands of entries) and the watches of its view. Each scenario runs in its own child
// process (`conversation-view --worker <scenario>`), so heap numbers are its own, on SQLite so history stays out of the heap (SqliteStorage;
// with PI_DURABLE_BENCH_STORAGE=store, or the `store` argument, Durable's storage on Chord's store, StoreStorage):
// - start: acquire a watch of a conversation of N entries (the mount is built every time; the watch is stopped).
// - append: K started watches; commit entries one per commit, or in bursts of 50 per commit, and wait for delivery.
// - resume: a watch with `from` 20 entries back, as a client that reconnects after a short drop.
// - stream: K started watches; commits that change only pi.live (a streamed answer), no entries.
// - long-lived: one started watch while 20,000 entries are appended; heap after collection, and whether the first entry delivered is
//   still reachable.
// Adapted: upstream's heapUsed after two gc() calls is the GC heap after two blocking full collections (GC.GetTotalMemory), its
// process.cpuUsage() the process's total processor time, its WeakRef a WeakReference, and its setTimeout(1) polls Task.Delay(1) (which ticks
// at the timer resolution, 15.6 ms on Windows, so a poll ends later than under Node). The faux models answer nothing: no scenario asks a
// model. Report only: nothing here passes or fails on a timing.
// Pi v1.5.0 sync (area vocabulary, 5f312dac4): the long-lived listener skips a call that is not a value, and the stream scenario writes
// pi.live compactionFailure with its trigger, as upstream does (the bench writes that JSON value; the rename is area live-turns's).
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;

namespace PiSharp.Durable.Benchmarks;

/// <summary>One conversation view scenario (upstream <c>Scenario</c>): <c>start</c>, <c>append</c>, <c>resume</c>, <c>stream</c> or
/// <c>long-lived</c>, each over a conversation of <see cref="Entries"/> entries.</summary>
public sealed record ConversationViewScenario
{
    /// <summary><c>start</c>, <c>append</c>, <c>resume</c>, <c>stream</c> or <c>long-lived</c>.</summary>
    public required string Kind { get; init; }
    /// <summary>The conversation's entries before the scenario; long-lived appends as many more.</summary>
    public required int Entries { get; init; }
    /// <summary>The started watches of append and stream.</summary>
    public int Watches { get; init; }
    /// <summary>The entries each append commit appends.</summary>
    public int Burst { get; init; }
    /// <summary>The commits of append and stream.</summary>
    public int Commits { get; init; }
    /// <summary><c>sqlite</c> or <c>store</c>.</summary>
    public string Storage { get; init; } = "sqlite";

    /// <summary>Upstream's <c>label()</c>.</summary>
    public string Label => Kind switch
    {
        "start" => $"watch start, {Entries} entries",
        "resume" => $"resume 20 back, {Entries} entries",
        "append" => $"append x{Burst} per commit, {Watches} watches",
        "stream" => $"pi.live only, {Watches} watches",
        _ => $"long-lived watch, {Entries} appends",
    };
}

/// <summary>The conversation view benchmark (upstream <c>test/conversation-view-bench.ts</c>).</summary>
public static class ConversationViewBench
{
    private static readonly DurableContext Context = DurableContext.Background;
    private static readonly string Text = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 10));
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The metrics columns, in the order a row prints them.</summary>
    public static IReadOnlyList<string> Columns { get; } =
        ["p50Ms", "perCommitUs", "windowEntries", "heapWithWatchesMiB", "heapWithWatchMiB", "firstEntryCollected", "cpuMs", "wallMs", "heapBeforeMiB"];

    /// <summary>Upstream's scenario list, in order, on <paramref name="storage"/>.</summary>
    public static IReadOnlyList<ConversationViewScenario> Scenarios(string storage = "sqlite") =>
    [
        new() { Kind = "start", Entries = 5000, Storage = storage },
        new() { Kind = "resume", Entries = 5000, Storage = storage },
        new() { Kind = "append", Entries = 5000, Watches = 1, Burst = 1, Commits = 2000, Storage = storage },
        new() { Kind = "append", Entries = 5000, Watches = 4, Burst = 1, Commits = 2000, Storage = storage },
        new() { Kind = "append", Entries = 5000, Watches = 4, Burst = 50, Commits = 200, Storage = storage },
        new() { Kind = "stream", Entries = 5000, Watches = 4, Commits = 5000, Storage = storage },
        new() { Kind = "long-lived", Entries = 20000, Storage = storage },
    ];

    /// <summary>The storages of a run: <paramref name="argument"/> (<c>sqlite</c>, <c>store</c> or <c>both</c>), else upstream's
    /// PI_DURABLE_BENCH_STORAGE (<c>store</c>), else SQLite.</summary>
    public static IReadOnlyList<string> Storages(string? argument) => argument switch
    {
        "both" => ["sqlite", "store"],
        "sqlite" or "store" => [argument],
        _ => [Environment.GetEnvironmentVariable("PI_DURABLE_BENCH_STORAGE") == "store" ? "store" : "sqlite"],
    };

    /// <summary>Runs every scenario on each storage in its own child process (upstream <c>main</c>) and prints the table.</summary>
    public static async Task<IReadOnlyList<(ConversationViewScenario Scenario, IReadOnlyDictionary<string, double> Metrics)>> RunDriverAsync(
        IReadOnlyList<string> storages, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(storages);
        ArgumentNullException.ThrowIfNull(output);
        var rows = new List<(ConversationViewScenario, IReadOnlyDictionary<string, double>)>();
        await output.WriteLineAsync(TableHeader);
        foreach (var scenario in storages.SelectMany(storage => Scenarios(storage)))
        {
            var stdout = await ChildProcess.RunAsync(["conversation-view", "--worker", JsonSerializer.Serialize(scenario, Json)]);
            var metrics = JsonSerializer.Deserialize<Dictionary<string, double>>(stdout.Trim().Split('\n')[^1], Json)!;
            rows.Add((scenario, metrics));
            await output.WriteLineAsync(Row(scenario, metrics));
        }
        return rows;
    }

    /// <summary>The worker entry point: runs one scenario and prints its metrics as a JSON line.</summary>
    public static async Task<int> WorkerMainAsync(string encoded, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var scenario = JsonSerializer.Deserialize<ConversationViewScenario>(encoded, Json) ?? throw new ArgumentException("No scenario");
        await output.WriteLineAsync(JsonSerializer.Serialize(await RunScenarioAsync(scenario), Json));
        return 0;
    }

    /// <summary>The table header of <see cref="Row"/>.</summary>
    public static string TableHeader =>
        "| scenario | storage | " + string.Join(" | ", Columns) + " |\n|---|---|" + string.Concat(Columns.Select(_ => "---:|"));

    /// <summary>One table row; a metric the scenario does not measure is empty.</summary>
    public static string Row(ConversationViewScenario scenario, IReadOnlyDictionary<string, double> metrics)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(metrics);
        return "| " + string.Join(" | ", [scenario.Label, scenario.Storage,
            .. Columns.Select(column => metrics.TryGetValue(column, out var value) ? value.ToString(CultureInfo.InvariantCulture) : "")]) + " |";
    }

    private static double HeapMiB()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        return Math.Round(GC.GetTotalMemory(forceFullCollection: false) / (double)(1 << 20) * 10) / 10;
    }

    private static double Round(double value) => Math.Round(value * 100) / 100;

    private static async Task AppendEntriesAsync(IConversation conversation, int count, int perCommit)
    {
        for (var done = 0; done < count; done += perCommit)
        {
            var first = done;
            await conversation.CommitAsync(async tx =>
            {
                for (var index = first; index < Math.Min(count, first + perCommit); index++)
                    await tx.AppendEntryAsync(conversation.Id, new EntryDraft { Kind = "note", Data = JsonValue.Object([new("text", $"{index} {Text}")]) });
            }, Context);
        }
    }

    /// <summary>Returns once every watch holds a value whose newest entry is <paramref name="id"/>'s (upstream <c>delivered</c>).</summary>
    private static async Task DeliveredAsync(IReadOnlyList<IWatchHandle<ConversationView>> watches, Func<EntryId?> id)
    {
        while (!watches.All(watch => watch.Value.Entries is [.., var last] && last.Id == id())) await Task.Delay(1);
    }

    private static async Task<IStorage> OpenAsync(string storage, string path) =>
        storage == "store" ? await StorageBench.OpenStoreStorageAsync(path) : await SqliteStorage.OpenAsync(path);

    /// <summary>Runs one scenario in this process (upstream <c>run</c>).</summary>
    public static async Task<IReadOnlyDictionary<string, double>> RunScenarioAsync(ConversationViewScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var directory = Directory.CreateTempSubdirectory("pi-durable-view-bench-").FullName;
        try
        {
            var storage = await OpenAsync(scenario.Storage, Path.Combine(directory, "session.sqlite"));
            var harness = await Harness.OpenAsync(storage, new HarnessOptions { Models = new FauxModels(), Registry = Registry.Create() }, Context);
            var root = await harness.RootAsync(Context);
            await AppendEntriesAsync(root, scenario.Entries, 500);
            async Task<EntryId> Newest() => (await root.EntriesAsync(new EntryQueryBounds(), 1, null, Context)).Items[0].Id;
            var heapBefore = HeapMiB();
            var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var started = Stopwatch.StartNew();
            var result = new Dictionary<string, double>();
            if (scenario.Kind is "start" or "resume")
            {
                ConversationWindow? window = scenario.Kind == "resume" ? new ConversationWindow { From = new EntryId((await Newest()).Value - 19) } : null;
                var times = new List<double>();
                var size = 0;
                for (var round = 0; round < 20; round++)
                {
                    var at = Stopwatch.GetTimestamp();
                    var watch = await root.WatchAsync(Context, window);
                    times.Add(Stopwatch.GetElapsedTime(at).TotalMilliseconds);
                    size = watch.Value.Entries.Length;
                    await watch.StopAsync();
                }
                times.Sort();
                result["p50Ms"] = Round(times[10]);
                result["windowEntries"] = size;
            }
            else if (scenario.Kind == "append")
            {
                var watches = await Task.WhenAll(Enumerable.Range(0, scenario.Watches).Select(_ => root.WatchAsync(Context)));
                foreach (var watch in watches) watch.Start((_, _) => Task.CompletedTask);
                EntryId? last = null;
                var at = Stopwatch.GetTimestamp();
                for (var index = 0; index < scenario.Commits; index++)
                {
                    last = await root.CommitAsync(async tx =>
                    {
                        EntryId? appended = null;
                        for (var n = 0; n < scenario.Burst; n++)
                            appended = (await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note", Data = JsonValue.Object([new("text", Text)]) })).Id;
                        return appended;
                    }, Context);
                }
                await DeliveredAsync(watches, () => last);
                result["perCommitUs"] = Round(Stopwatch.GetElapsedTime(at).TotalMilliseconds * 1000 / scenario.Commits);
                result["windowEntries"] = watches[0].Value.Entries.Length;
                result["heapWithWatchesMiB"] = HeapMiB();
                foreach (var watch in watches) await watch.StopAsync();
            }
            else if (scenario.Kind == "stream")
            {
                var watches = await Task.WhenAll(Enumerable.Range(0, scenario.Watches).Select(_ => root.WatchAsync(Context)));
                var calls = 0;
                foreach (var watch in watches)
                    watch.Start((_, _) =>
                    {
                        Interlocked.Increment(ref calls);
                        return Task.CompletedTask;
                    });
                var at = Stopwatch.GetTimestamp();
                for (var index = 0; index < scenario.Commits; index++)
                {
                    var error = index.ToString(CultureInfo.InvariantCulture);
                    await root.CommitAsync(async tx => (await tx.DocAsync(Live.LiveDoc, root.Id)).Set("compactionFailure",
                        JsonValue.Object([new("taskId", 1), new("trigger", "manual"), new("error", error)])), Context);
                }
                // Every watch delivered the final value, not just one of them.
                var final = (scenario.Commits - 1).ToString(CultureInfo.InvariantCulture);
                bool Done() => watches.All(watch => watch.Value.Doc("pi.live") is JsonObjectValue live &&
                    live.TryGetValue("compactionFailure", out var failure) && ((JsonObjectValue)failure)["error"] is JsonStringValue { Value: var shown } &&
                    shown == final);
                while (Volatile.Read(ref calls) < scenario.Watches || !Done()) await Task.Delay(1);
                result["perCommitUs"] = Round(Stopwatch.GetElapsedTime(at).TotalMilliseconds * 1000 / scenario.Commits);
                foreach (var watch in watches) await watch.StopAsync();
            }
            else
            {
                var watch = await root.WatchAsync(Context);
                WeakReference<EntryRecord>? first = null;
                watch.Start((call, _) =>
                {
                    if (call is WatchValueCall<ConversationView> { Value.Entries: [.., var entry] } && first is null) first = new WeakReference<EntryRecord>(entry);
                    return Task.CompletedTask;
                });
                await AppendEntriesAsync(root, 1, 1);
                var id = await Newest();
                await DeliveredAsync([watch], () => id);
                await AppendEntriesAsync(root, scenario.Entries, 1);
                var last = await Newest();
                await DeliveredAsync([watch], () => last);
                result["windowEntries"] = watch.Value.Entries.Length;
                result["heapWithWatchMiB"] = HeapMiB();
                await Task.Delay(10);
                HeapMiB();
                result["firstEntryCollected"] = first is null || !first.TryGetTarget(out _) ? 1 : 0;
                await watch.StopAsync();
            }
            process.Refresh();
            result["cpuMs"] = Round((process.TotalProcessorTime - cpu).TotalMilliseconds);
            result["wallMs"] = Round(started.Elapsed.TotalMilliseconds);
            result["heapBeforeMiB"] = heapBefore;
            await harness.CloseAsync(Context);
            return result;
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
