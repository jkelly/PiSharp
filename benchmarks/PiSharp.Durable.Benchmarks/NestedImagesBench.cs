// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/nested-images-bench.ts. One model-issued `script` call, as a code
// mode call would, loops `count` times: a nested `read_image` call returns a unique base64 image of `bytes` characters, then a nested
// `classify` call reads it. The `inline` variant passes the image data in `classify`'s arguments; the `reference` variant passes the `taskId`
// of the `read_image` result, whose value is the image, and `classify` resolves it from its caller's NestedCallDoc. Reports commits, wall time,
// logical bytes written (JSON of the storage writes) by write type, the stored size before `script` returns, after the run settles, after
// close and after reopening, the heap (peak during the run, sampled every 5 ms and at each commit; after a collection once the run settles,
// after waitForIdle, and after reopening; all over the heap after a collection before opening), the number of nested task records, and the
// size of the largest one. Mappings as in NestedToolsBench.cs; random bytes come from RandomNumberGenerator.
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage.Jsonl;

namespace PiSharp.Durable.Benchmarks;

/// <summary>Upstream <c>nested-images-bench.ts</c>.</summary>
public static class NestedImagesBench
{
    /// <summary>One scenario (upstream <c>Scenario</c>).</summary>
    public sealed record Scenario(string Backend, string Variant, int Count, int Bytes);

    /// <summary>One scenario's measurements (upstream <c>Result</c>).</summary>
    public sealed record Result(int Commits, double Ms, IReadOnlyDictionary<string, long> Written, long StoredLive, long StoredRun, long StoredClosed,
        long StoredReopened, long PeakHeap, long SettledHeap, long IdleHeap, long ReopenedHeap, int NestedTasks, long LargestTask);

    private static readonly DurableContext Context = DurableContext.Background;

    /// <summary>Upstream's default counts.</summary>
    public static IReadOnlyList<int> DefaultCounts { get; } = [50, 200];

    private static readonly JsonObjectValue ImageArgument = (JsonObjectValue)JsonValue.Parse(
        """{"type":"object","required":["type","mimeType"],"properties":{"type":{"const":"image","type":"string"},"mimeType":{"type":"string"},"data":{"type":"string"},"source":{"type":"object","required":["taskId"],"properties":{"taskId":{"type":"number"}}}}}""");

    /// <summary>Runs every backend and variant for each count; writes upstream's table and returns the rows.</summary>
    public static async Task<IReadOnlyList<(Scenario Scenario, Result Result)>> RunAsync(IReadOnlyList<int> counts, TextWriter output, int bytes = 1024 * 1024,
        string? tempRoot = null)
    {
        var rows = new List<(Scenario, Result)>();
        output.WriteLine($"Images: {bytes / 1024} KiB base64 each, read then classified sequentially in one script call");
        output.WriteLine("backend | variant   | count | commits, ms | written: all (task / doc create / doc change / entry) | stored: live / run / closed / reopened | heap: peak; settled / idle / reopened | nested tasks, largest task");
        foreach (var count in counts)
        foreach (var backend in new[] { "sqlite", "jsonl" })
        foreach (var variant in new[] { "inline", "reference" })
        {
            var scenario = new Scenario(backend, variant, count, bytes);
            var result = await RunScenarioAsync(scenario, tempRoot);
            rows.Add((scenario, result));
            var written = result.Written;
            output.WriteLine(string.Join(" | ",
                backend.PadRight(7), variant.PadRight(9), count.ToString(CultureInfo.InvariantCulture).PadRight(5),
                string.Create(CultureInfo.InvariantCulture, $"{result.Commits}, {Math.Round(result.Ms)} ms"),
                $"{Mib(written.Values.Sum())} ({Mib(written["task"])} / {Mib(written["document.create"])} / {Mib(written["document.change"])} / {Mib(written["entry"])})",
                $"{Mib(result.StoredLive)} / {Mib(result.StoredRun)} / {Mib(result.StoredClosed)} / {Mib(result.StoredReopened)}",
                $"{Mib(result.PeakHeap)}; {Mib(result.SettledHeap)} / {Mib(result.IdleHeap)} / {Mib(result.ReopenedHeap)}",
                string.Create(CultureInfo.InvariantCulture, $"{result.NestedTasks}, {result.LargestTask / 1024.0:F1} KiB")));
        }
        return rows;
    }

    /// <summary>A count, or the defaults.</summary>
    public static IReadOnlyList<int> ParseCounts(string? argument) =>
        argument is null ? DefaultCounts : [int.Parse(argument, CultureInfo.InvariantCulture)];

    private static string Mib(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024.0:F1} MiB");

    private static long DirectorySize(string path) => new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);

    private static JsonArrayValue Text(string text) => JsonValue.Array(JsonValue.Object(
        KeyValuePair.Create("type", (JsonValue)"text"), KeyValuePair.Create("text", (JsonValue)text)));

    private static JsonObjectValue Object(params (string Key, JsonValue Value)[] properties) =>
        JsonValue.Object(properties.Select(property => KeyValuePair.Create(property.Key, property.Value)));

    private static IRegistry RegistryFor(Scenario scenario, Action sample, Func<Task> beforeReturn)
    {
        var registry = Registry.Create();
        var readImage = Define.Tool(new ToolRegistration
        {
            Name = "read_image",
            Description = "Read an image",
            Parameters = (JsonObjectValue)JsonValue.Parse("""{"type":"object","required":["n"],"properties":{"n":{"type":"number"}}}"""),
            // One image, so programs get it as an ImageContent. Unique per call: base64 of random bytes.
            Execute = (_, _, _) => Task.FromResult(new ToolExecutionResult
            {
                Output = JsonValue.Array(Object(("type", "image"), ("data", Convert.ToBase64String(RandomNumberGenerator.GetBytes(scenario.Bytes / 4 * 3))),
                    ("mimeType", "image/png"))),
            }),
        });
        var classify = Define.Tool(new ToolRegistration
        {
            Name = "classify",
            Description = "Classify images",
            Parameters = Object(("type", "object"), ("required", JsonValue.Array((JsonValue)"images")),
                ("properties", Object(("images", Object(("type", "array"), ("items", ImageArgument)))))),
            Execute = async (args, api, callContext) =>
            {
                foreach (var image in ((JsonArrayValue)((JsonObjectValue)args)["images"]).Cast<JsonObjectValue>())
                {
                    var data = image.TryGetValue("data", out var inline) ? ((JsonStringValue)inline).Value : null;
                    if (image.TryGetValue("source", out var source))
                    {
                        // The caller is the nested call's `parent`; its NestedCallDoc holds the referenced result.
                        var input = (await api.GetTaskAsync(api.TaskId, callContext))?.Input as JsonObjectValue;
                        if (input?["kind"] is not JsonStringValue { Value: "nested" })
                            throw new InvalidOperationException("classify resolves references only as a nested call");
                        // Results are keyed by call key, which the referenced call's input carries.
                        var sourceId = new TaskId((long)((JsonNumberValue)((JsonObjectValue)source)["taskId"]).Value);
                        var sourceInput = (JsonObjectValue)(await api.GetTaskAsync(sourceId, callContext))!.Input;
                        if (sourceInput["kind"] is not JsonStringValue { Value: "nested" }) throw new InvalidOperationException("A reference names a nested call");
                        var stored = await api.SnapshotAsync(HarnessTasks.NestedCallDoc, new TaskId((long)((JsonNumberValue)input["parent"]).Value),
                            ((JsonStringValue)sourceInput["key"]).Value, callContext);
                        var block = stored?["result"] is JsonObjectValue result && result.TryGetValue("structuredOutput", out var value) ? value as JsonObjectValue : null;
                        data = block?["type"] is JsonStringValue { Value: "image" } ? ((JsonStringValue)block["data"]).Value : null;
                    }
                    if (data?.Length != scenario.Bytes) throw new InvalidOperationException($"Image has {data?.Length} characters");
                }
                return new ToolExecutionResult { Output = Text("ok") };
            },
        });
        var script = Define.Tool(new ToolRegistration
        {
            Name = "script",
            Description = "Read and classify images",
            Parameters = (JsonObjectValue)JsonValue.Parse("""{"type":"object","required":["count"],"properties":{"count":{"type":"number"}}}"""),
            Callers = [ToolCaller.Model],
            Execute = async (args, api, callContext) =>
            {
                var count = (int)((JsonNumberValue)((JsonObjectValue)args)["count"]).Value;
                for (var n = 0; n < count; n++)
                {
                    var read = await api.ExecuteToolAsync("read_image", Object(("n", n)), callContext, new NestedCallOptions { Key = $"read{n}" });
                    var image = (JsonObjectValue)read.StructuredOutput!;
                    var argument = scenario.Variant == "inline"
                        ? Object(("type", "image"), ("data", image["data"]), ("mimeType", image["mimeType"]))
                        : Object(("type", "image"), ("source", Object(("taskId", read.TaskId.ToJson()))), ("mimeType", image["mimeType"]));
                    var classified = await api.ExecuteToolAsync("classify", Object(("images", JsonValue.Array(argument))), callContext,
                        new NestedCallOptions { Key = $"classify{n}" });
                    if (classified.IsError)
                        throw new InvalidOperationException($"classify {n} failed: {string.Join("; ", classified.Diagnostics.Select(diagnostic => diagnostic.Message))}");
                    sample();
                }
                await beforeReturn();
                return new ToolExecutionResult { Output = Text($"classified {count}") };
            },
        });
        registry.Install(Define.Extension(new Extension { Name = "bench", Tools = [readImage, classify, script] }));
        return registry;
    }

    private static async Task<IStorage> OpenAsync(string backend, string directory) => backend == "sqlite"
        ? await SqliteStorage.OpenAsync(Path.Combine(directory, "session.sqlite"))
        : await JsonlStorage.OpenAsync(directory, Context);

    private static async Task<Result> RunScenarioAsync(Scenario scenario, string? tempRoot)
    {
        var directory = Path.Combine(tempRoot ?? Path.GetTempPath(), "pi-durable-images-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            long peakHeap = 0;
            long storedLive = 0;
            void Sample()
            {
                var heap = GC.GetTotalMemory(false);
                var current = Interlocked.Read(ref peakHeap);
                while (heap > current)
                {
                    var seen = Interlocked.CompareExchange(ref peakHeap, heap, current);
                    if (seen == current) break;
                    current = seen;
                }
            }
            var registry = RegistryFor(scenario, Sample, () =>
            {
                storedLive = DirectorySize(directory);
                return Task.CompletedTask;
            });
            var baseHeap = await HeapRetained.RetainedBytesAsync();
            var storage = new CountingStorage(await OpenAsync(scenario.Backend, directory), Sample);
            var models = new FauxModels(FauxModels.Calls("script", Object(("count", scenario.Count)), "s"), FauxModels.Text("done"));
            var harness = await Harness.OpenAsync(storage, new HarnessOptions { Models = models, Registry = registry }, Context);
            var root = await harness.RootAsync(Context, new AgentChange { Model = FauxModels.Ref });
            var before = (storage.Commits, Written: storage.Written());
            Interlocked.Exchange(ref peakHeap, 0);
            var started = Stopwatch.GetTimestamp();
            double ms;
            using (new Timer(_ => Sample(), null, 0, 5))
            {
                var settled = await (await root.SubmitAsync(SubmissionDraft.Input("run"), Context)).WaitAsync(Context);
                if (settled.Status != SubmissionStatus.Done) throw new InvalidOperationException($"run: {settled.Status}");
                // `done` only says the model answered; the script's result says whether it ran through.
                var latest = (await root.EntriesAsync(new EntryQueryBounds(), 10, null, Context)).Items;
                var result = latest.FirstOrDefault(entry => entry.Kind == "pi.tool-result")?.Model;
                if (result is not { Count: > 0 } || result[0] is not JsonObjectValue message || message["isError"] is JsonBooleanValue { Value: true })
                    throw new InvalidOperationException("run: the script failed");
                ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            var settledHeap = await HeapRetained.RetainedBytesAsync() - baseHeap;
            await harness.WaitForIdleAsync(Context);
            var idleHeap = await HeapRetained.RetainedBytesAsync() - baseHeap;
            var after = storage.Written();
            var written = after.ToDictionary(pair => pair.Key, pair => pair.Value - before.Written[pair.Key]);
            var storedRun = DirectorySize(directory);
            await harness.CloseAsync(Context);
            var storedClosed = DirectorySize(directory);

            var reopenedStorage = await OpenAsync(scenario.Backend, directory);
            var reopened = await Harness.OpenAsync(reopenedStorage, new HarnessOptions { Models = models, Registry = registry }, Context);
            var reopenedHeap = await HeapRetained.RetainedBytesAsync() - baseHeap;
            var storedReopened = DirectorySize(directory);
            var nestedTasks = 0;
            long largestTask = 0;
            JsonObjectValue? cursor = null;
            do
            {
                var page = await reopenedStorage.ScanTasksAsync(new TaskQuery { Kind = "pi.tool" }, 50, cursor, Context);
                foreach (var record in page.Items)
                {
                    if (record.Input is JsonObjectValue input && input["kind"] is JsonStringValue { Value: "nested" }) nestedTasks++;
                    largestTask = Math.Max(largestTask, record.ToJson().ToJson().Length);
                }
                cursor = page.Next;
            } while (cursor is not null);
            await reopened.CloseAsync(Context);
            return new Result(storage.Commits - before.Commits, ms, written, storedLive, storedRun, storedClosed, storedReopened,
                Interlocked.Read(ref peakHeap) - baseHeap, settledHeap, idleHeap, reopenedHeap, nestedTasks, largestTask);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
