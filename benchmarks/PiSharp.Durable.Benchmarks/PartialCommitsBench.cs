// PiSharp-authored, for Pi v1.5.0's partial commits (durable work list area partial-commits; packages/durable/CHANGELOG.md 1.5.0 Fixed:
// "partial commits of a streamed answer copying every growing text, thinking and tool-call argument string in full on each commit"). Not an
// upstream file: upstream measures the same path in a test (harness-view-run.test.ts, "commits a growing text and tool call at a cost that
// follows the new text, not the string lengths"; harness.view-run.streamed-partials.* here), which bounds a ratio and prints nothing.
// One answer streams through the real generation task on MemoryStorage: a text of about `size` characters, then a tool call whose
// `content` argument is the same text, both in 256-character pieces (pi-ai's faux provider at token size 64). The partial throttle's interval
// is 0, and after each piece the model waits for the next commit, so a piece that changes the partial gets exactly one partial commit
// whatever the timer resolution. Before this area a tool call's partial arguments stayed `{}` until the call ended, so its pieces changed
// nothing and committed nothing: once 200 ms pass without a commit, the phase streams its other pieces without waiting. Each phase reports its
// commits, the bytes allocated on every thread meanwhile (GC.GetTotalAllocatedBytes) and the time. A per-commit cost that grows with the
// size is an O(length) copy per commit; a flat one is the cost of the new text.
// Pi v1.5.0 sync (area deferred-commits): partial commits are deferred, and ObserveCommits sees only what was stored; the run sets
// progress.persistWithinMs to 0, so each partial is still stored on its own right after its commit and every phase measures a commit and
// its store, as before.
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using PiSharp.Contracts;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;

namespace PiSharp.Durable.Benchmarks;

/// <summary>Partial commits of a long streamed answer (PiSharp-authored; see the file header).</summary>
public static class PartialCommitsBench
{
    /// <summary>The default sizes: upstream's test's two, and the 500 KB tool call of its changelog.</summary>
    public static IReadOnlyList<int> DefaultSizes { get; } = [40_000, 400_000, 500_000];

    /// <summary>One phase of a run: the pieces streamed, the commits they made, the bytes allocated and the time.</summary>
    public sealed record Phase(int Pieces, int Commits, long Bytes, double Ms)
    {
        /// <summary>Bytes allocated per commit; 0 without commits.</summary>
        public double BytesPerCommit => Commits == 0 ? 0 : (double)Bytes / Commits;
    }

    /// <summary>One run: the answer's size, and its text and tool-call phases.</summary>
    public sealed record Row(int Size, Phase Text, Phase ToolCall);

    private const int Piece = 256;
    private static readonly DurableContext Context = DurableContext.Background;

    /// <summary>Streams one answer of each size, writes a line per size and the per-commit ratios of the last size over the first.</summary>
    public static async Task<IReadOnlyList<Row>> RunAsync(IReadOnlyList<int> sizes, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        ArgumentNullException.ThrowIfNull(output);
        var rows = new List<Row>();
        foreach (var size in sizes)
        {
            var row = await RunAsync(size);
            rows.Add(row);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"size={size}: text {Describe(row.Text)}; tool call {Describe(row.ToolCall)}"));
        }
        if (rows.Count > 1)
        {
            static string Ratio(Phase last, Phase first) => first.BytesPerCommit > 0 && last.BytesPerCommit > 0
                ? (last.BytesPerCommit / first.BytesPerCommit).ToString("F2", CultureInfo.InvariantCulture) : "-";
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"per-commit bytes, size {rows[^1].Size} over size {rows[0].Size}: text {Ratio(rows[^1].Text, rows[0].Text)}, tool call {Ratio(rows[^1].ToolCall, rows[0].ToolCall)}"));
        }
        return rows;
    }

    private static string Describe(Phase phase) => string.Create(CultureInfo.InvariantCulture,
        $"{phase.Pieces} pieces, {phase.Commits} commits, {phase.Bytes / 1024.0 / 1024.0:F1} MiB ({phase.BytesPerCommit / 1024.0:F1} KiB/commit), {phase.Ms:F0} ms");

    /// <summary>One run on a fresh Harness: streams the answer, then a short final one.</summary>
    public static async Task<Row> RunAsync(int size)
    {
        var text = string.Concat(Enumerable.Repeat("streamed text with \"quotes\" and \\ escapes\n", Math.Max(1, size / 40)));
        var gate = new CommitGate();
        var models = new StreamingModels(text, gate);
        // Each partial stored on its own, right after its commit (see the file header).
        var settings = new HarnessSettings { Progress = new ProgressSettings { PartialIntervalMs = 0, OutputIntervalMs = 100, PersistWithinMs = 0 },
            // Keeps `pi.live` cached between commits whatever the run's default (upstream 1e42501bb).
            DocumentRetentionMs = 60_000 };
        var harness = await Harness.OpenAsync(new MemoryStorage(), new HarnessOptions { Models = models, Registry = Registry.Create(), Settings = settings },
            Context);
        try
        {
            harness.ObserveCommits(new CommitObserver((_, _) => gate.Committed(), _ => { }));
            var root = await harness.RootAsync(Context, new AgentChange { Model = StreamingModels.Ref });
            var settled = await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            if (settled.Status != SubmissionStatus.Done) throw new InvalidOperationException($"size {size}: {settled}");
            return new Row(size, models.Phases[0], models.Phases[1]);
        }
        finally
        {
            await harness.CloseAsync(Context);
        }
    }

    /// <summary>Comma-separated sizes, or the defaults.</summary>
    public static IReadOnlyList<int> ParseSizes(string? argument) =>
        argument is null ? DefaultSizes : [.. argument.Split(',').Select(size => int.Parse(size, CultureInfo.InvariantCulture))];

    /// <summary>Counts commits and lets the model wait for the next one.</summary>
    private sealed class CommitGate
    {
        private long count;
        private TaskCompletionSource next = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long Count => Interlocked.Read(ref count);

        public void Committed()
        {
            Interlocked.Increment(ref count);
            Interlocked.Exchange(ref next, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        }

        /// <summary>Waits until a commit lands after <paramref name="seen"/> commits, for at most 200 ms; false when none did (a piece
        /// that changes nothing commits nothing).</summary>
        public async Task<bool> AfterAsync(long seen)
        {
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 5;
            while (Count <= seen)
            {
                var waiting = Volatile.Read(ref next).Task;
                if (Count > seen) return true;
                var left = deadline - Stopwatch.GetTimestamp();
                if (left <= 0) return false;
                try { await waiting.WaitAsync(TimeSpan.FromSeconds((double)left / Stopwatch.Frequency)); }
                catch (TimeoutException) { return false; }
            }
            return true;
        }
    }

    /// <summary>faux/faux-1: the long text and tool call, piece by piece, then "done".</summary>
    private sealed class StreamingModels(string text, CommitGate gate) : IDurableModels
    {
        private int requests;

        public static ModelRef Ref { get; } = new("faux", "faux-1");

        /// <summary>The text phase and the tool-call phase of the first answer.</summary>
        public List<Phase> Phases { get; } = [];

        private DurableModel Model { get; } = new()
        {
            Id = "faux-1", Name = "Faux Model", Api = "faux", Provider = "faux", BaseUrl = "http://localhost:0", Input = ["text"],
            ContextWindow = 10_000_000, MaxTokens = 16384,
        };

        public DurableModel? GetModel(string provider, string modelId) => provider == Model.Provider && modelId == Model.Id ? Model : null;

        public async IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            var partial = new AssistantMessage(model.Api, model.Provider, model.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), [],
                TokenUsage.Zero, StopReason.Pending);
            yield return new StreamStarted(partial);
            if (Interlocked.Increment(ref requests) > 1)
            {
                yield return new TextStarted(0, new TextContent(""));
                yield return new TextDelta(0, "done");
                yield return new TextEnded(0, "done");
                yield return new StreamDone(StopReason.Stop, partial with { Content = [new TextContent("done")], StopReason = StopReason.Stop });
                yield break;
            }
            var arguments = JsonValue.Object(KeyValuePair.Create("path", (JsonValue)"a.ts"), KeyValuePair.Create("content", (JsonValue)text)).ToJson();
            var call = new ToolCallContent("c1", "missing", JsonData.Parse(arguments));
            yield return new TextStarted(0, new TextContent(""));
            await foreach (var item in Pieces(text, chunk => new TextDelta(0, chunk))) yield return item;
            yield return new TextEnded(0, text);
            yield return new ToolCallStarted(1, call with { Arguments = JsonData.Parse("{}") });
            await foreach (var item in Pieces(arguments, chunk => new ToolCallDelta(1, chunk))) yield return item;
            yield return new ToolCallEnded(1, call);
            yield return new StreamDone(StopReason.ToolUse, partial with { Content = [new TextContent(text), call], StopReason = StopReason.ToolUse });
        }

        // Streams `source` piece by piece, waiting for each piece's commit, and records the phase.
        private async IAsyncEnumerable<StreamEvent> Pieces(string source, Func<string, StreamEvent> piece)
        {
            // A collection first, so the phase starts with no garbage of the one before it.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var (commits, bytes, started) = (gate.Count, GC.GetTotalAllocatedBytes(precise: true), Stopwatch.GetTimestamp());
            var pieces = 0;
            var wait = true;
            for (var index = 0; index < source.Length; index += Piece)
            {
                var seen = gate.Count;
                yield return piece(source.Substring(index, Math.Min(Piece, source.Length - index)));
                pieces++;
                if (wait) wait = await gate.AfterAsync(seen);
                else await Task.Yield();
            }
            Phases.Add(new Phase(pieces, (int)(gate.Count - commits), GC.GetTotalAllocatedBytes(precise: true) - bytes,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        }

        public async Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
            CancellationToken cancellationToken)
        {
            StreamTerminalEvent? terminal = null;
            await foreach (var item in StreamSimple(model, messages, options, cancellationToken)) if (item is StreamTerminalEvent end) terminal = end;
            return terminal!.Message;
        }

        public IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The faux model does not defer responses.");

        public Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            Task.FromException<AssistantMessage>(new NotSupportedException("The faux model does not defer responses."));

        public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
