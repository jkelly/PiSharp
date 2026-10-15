// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/scheduler-fanout-bench.ts. One model-issued tool makes `calls`
// parallel nested calls (IToolExecutionApi.ExecuteToolAsync, progress off) of a tool that returns at once, on in-memory storage, so the time
// is the Harness's and the scheduler's own. Quadratic work shows as four times the time per doubling of `calls`. The faux model is
// FauxModels.cs's; time is a Stopwatch around the run and waitForIdle, as upstream's performance.now() is.
using System.Diagnostics;
using System.Globalization;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;

namespace PiSharp.Durable.Benchmarks;

/// <summary>Upstream <c>scheduler-fanout-bench.ts</c>.</summary>
public static class SchedulerFanoutBench
{
    /// <summary>Upstream's default sizes.</summary>
    public static IReadOnlyList<int> DefaultSizes { get; } = [250, 500, 1000, 2000, 4000];

    /// <summary>One row: the calls, the time and the commits.</summary>
    public sealed record Row(int Calls, double Ms, int Commits);

    private static readonly DurableContext Context = DurableContext.Background;

    private static readonly JsonObjectValue LeafParameters =
        (JsonObjectValue)JsonValue.Parse("""{"type":"object","required":["n"],"properties":{"n":{"type":"number"}}}""");

    private static readonly JsonObjectValue FanParameters =
        (JsonObjectValue)JsonValue.Parse("""{"type":"object","required":["count"],"properties":{"count":{"type":"number"}}}""");

    /// <summary>Runs each size; writes upstream's table and returns the rows.</summary>
    public static async Task<IReadOnlyList<Row>> RunAsync(IReadOnlyList<int> sizes, TextWriter output)
    {
        var rows = new List<Row>();
        output.WriteLine("calls | ms     | commits | ms per call");
        foreach (var calls in sizes)
        {
            var row = await RunOneAsync(calls);
            rows.Add(row);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{calls,-5} | {Math.Round(row.Ms),-6} | {row.Commits,-7} | {row.Ms / calls:F2}"));
        }
        return rows;
    }

    /// <summary>Comma-separated sizes, or the defaults.</summary>
    public static IReadOnlyList<int> ParseSizes(string? argument) =>
        argument is null ? DefaultSizes : [.. argument.Split(',').Select(size => int.Parse(size, CultureInfo.InvariantCulture))];

    private static JsonArrayValue Text(string text) => JsonValue.Array(JsonValue.Object(
        KeyValuePair.Create("type", (JsonValue)"text"), KeyValuePair.Create("text", (JsonValue)text)));

    private static async Task<Row> RunOneAsync(int calls)
    {
        var registry = Registry.Create();
        var leaf = Define.Tool(new ToolRegistration
        {
            Name = "leaf",
            Description = "Return at once",
            Parameters = LeafParameters,
            Execute = (_, _, _) => Task.FromResult(new ToolExecutionResult { Output = Text("ok") }),
        });
        var fan = Define.Tool(new ToolRegistration
        {
            Name = "fan",
            Description = "Call leaf in parallel",
            Parameters = FanParameters,
            Execute = async (args, api, callContext) =>
            {
                var count = (int)((JsonNumberValue)((JsonObjectValue)args)["count"]).Value;
                var results = await Task.WhenAll(Enumerable.Range(0, count).Select(n => api.ExecuteToolAsync("leaf",
                    JsonValue.Object(KeyValuePair.Create("n", (JsonValue)n)), callContext, new NestedCallOptions { Progress = false })));
                var failed = results.Count(result => result.IsError);
                return new ToolExecutionResult { Output = Text($"{failed} failed"), IsError = failed > 0 };
            },
        });
        registry.Install(Define.Extension(new Extension { Name = "bench", Tools = [leaf, fan] }));
        var storage = new CountingStorage(new MemoryStorage());
        var models = new FauxModels(FauxModels.Calls("fan", JsonValue.Object(KeyValuePair.Create("count", (JsonValue)calls)), "fan"),
            FauxModels.Text("done"));
        var harness = await Harness.OpenAsync(storage, new HarnessOptions { Models = models, Registry = registry }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = FauxModels.Ref });
        var started = Stopwatch.GetTimestamp();
        var settled = await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
        if (settled.Status != SubmissionStatus.Done) throw new InvalidOperationException($"run: {settled.Status.ToString().ToLowerInvariant()}");
        await harness.WaitForIdleAsync(Context);
        var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        // A run whose nested calls failed measures nothing.
        var page = await root.EntriesAsync(new EntryQueryBounds(), 10, null, Context);
        var result = page.Items.FirstOrDefault(entry => entry.Kind == "pi.tool-result")?.Model;
        if (result is not { Count: > 0 } || result[0] is not JsonObjectValue message || message["isError"] is JsonBooleanValue { Value: true })
            throw new InvalidOperationException("run: nested calls failed");
        await harness.CloseAsync(Context);
        return new Row(calls, ms, storage.Commits);
    }
}
