using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Cli.Interactive;
using PiSharp.Tui;

// Compile only in a private diagnostic composition with accepted lead trim6047151.
internal static class TerminalWordSeaAcknowledgedProductionTests
{
    internal static readonly List<object> Observations = [];
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases()
    { yield return ("production-word-sea.acknowledged-complete-source-drafts-and-three-accepted-claims", Source); }
    private static async Task Source()
    {
        foreach (var (file, original) in new[] { ("word-dictionary-source-observations.json", true), ("word-sea-source-observations.json", false) })
        {
            using var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file)));
            foreach (var example in source.RootElement.GetProperty("cases").EnumerateArray().Where(row => !original || row.GetProperty("id").GetString() is "dictionary-full-public-editor-22" or "dictionary-full-public-editor-23" or "dictionary-full-public-editor-24"))
            {
                var packets = example.GetProperty("input").GetProperty("steps").EnumerateArray().Select(row => row.GetProperty("data").GetString()!).ToList();
                var expected = example.GetProperty("observation").GetProperty("steps").EnumerateArray().Select(row => new TerminalDraftSnapshot(row.GetProperty("text").GetString()!, row.GetProperty("cursorUtf16Offset").GetInt32())).ToList();
                var expectedAccepted = original ? example.GetProperty("observation").GetProperty("steps").EnumerateArray().Last().GetProperty("expandedText").GetString() : null;
                if (original) { packets.Add("\r"); expected.Add(new("", 0)); }
                await Observe(example.GetProperty("id").GetString()!, packets.ToArray(), expected.ToArray(), expectedAccepted);
            }
        }
    }
    private static async Task Observe(string id, string[] packets, TerminalDraftSnapshot[] expected, string? expectedAccepted)
    {
        var console = new Fixture(); var receipts = new TerminalSubmissionReceipts(); var drafts = new List<TerminalDraftSnapshot>(); var accepted = new List<TerminalAcceptedLine>();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var ended = 0; var rendersAfterClaim = 0; var rendered = Channel.CreateBounded<TerminalDraftSnapshot>(64);
        var run = new TerminalChatInput(console).RunAcknowledgedAsync((submission, _) => { receipts.FinishLocal(submission, true, "sea-local-fixture"); return Task.FromResult(true); },
            _ => Task.CompletedTask, (draft, _) => { drafts.Add(draft); Check(rendered.Writer.TryWrite(draft), "SEA render fixture capacity exceeded."); if (accepted.Count > 0) rendersAfterClaim++; return ValueTask.CompletedTask; }, receipts,
            (_, line) => accepted.Add(line), error => { Check(error is null, "Acknowledged SEA failure."); ended++; receipts.Complete(); },
            () => throw new InvalidOperationException("Unexpected SEA interrupt."), cancel.Token);
        try
        {
            foreach (var packet in packets) { await console.Feed(packet); await rendered.Reader.ReadAsync(cancel.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            console.Complete(); Check(await run.WaitAsync(TimeSpan.FromSeconds(5)) == TerminalInputExit.Eof, "SEA EOF differs."); console.AssertJoined();
            var matches = expected.SequenceEqual(drafts) && ended == 1 && accepted.Count == (expectedAccepted is null ? 0 : 1) &&
                (expectedAccepted is null || accepted.Single().Text == expectedAccepted && accepted.Single().Applied.IsCompletedSuccessfully && rendersAfterClaim > 0) && receipts.Snapshot == (0, 0, 0) && receipts.IsCompleted;
            Observations.Add(new { id, packets, expected = new { drafts = expected, acceptedText = expectedAccepted }, actual = new { drafts, accepted = accepted.Select(row => new { row.Text, row.Command, row.SessionReplaced, applied = row.Applied.IsCompletedSuccessfully }).ToArray() },
                matches, ended, rendersAfterClaim, receipts = receipts.Snapshot, receiptsCompleted = receipts.IsCompleted, childTaskJoined = run.IsCompleted, borrowedLeaseDisposed = console.Disposed, lease = console.Snapshot });
            Check(matches, "Complete acknowledged SEA observations differ: " + id);
        }
        finally { cancel.Cancel(); console.Complete(); receipts.Complete(); try { await run.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) when (run.IsCompleted) { } console.AssertJoined(); }
    }
    private sealed class Fixture : IConsoleTerminal
    {
        private readonly Channel<string> input = Channel.CreateBounded<string>(8); private readonly object gate = new(); private long started, settled; private int active;
        internal bool Disposed; private static readonly TerminalConsoleState State = new(0, 0, 65001, 65001, 25, true, 0, 0);
        public TerminalLeaseSnapshot Snapshot { get { lock (gate) return new(State, State, null, false, false, active, 0, started, settled, 0, 0); } }
        internal ValueTask Feed(string wire) => input.Writer.WriteAsync(wire); internal void Complete() => input.Writer.TryComplete();
        public async ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default)
        {
            lock (gate) { Check(active == 0 && !Disposed, "Concurrent/disposed SEA read."); active++; started++; }
            try { if (!await input.Reader.WaitToReadAsync(token)) return 0; Check(input.Reader.TryRead(out var wire) && wire is not null && wire.Length <= destination.Length, "SEA packet exceeded private read."); wire!.AsMemory().CopyTo(destination); return wire!.Length; }
            finally { lock (gate) { active--; settled++; } }
        }
        public ValueTask WriteAsync(ReadOnlyMemory<char> frame, CancellationToken token = default) => throw new InvalidOperationException("SEA adapter wrote to borrowed console.");
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        internal void AssertJoined() { lock (gate) Check(active == 0 && started == settled && !Disposed, "SEA borrowed read not joined."); }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
