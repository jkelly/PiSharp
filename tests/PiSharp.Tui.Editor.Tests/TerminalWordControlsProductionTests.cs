using System.Threading.Channels;
using System.Text.Json;
using PiSharp.Cli.Interactive;
using PiSharp.Tui;

// Private borrowed-console probe against the actual unchanged CLI project.
internal static class TerminalWordControlsProductionTests
{
    internal static readonly List<object> Observations = [];
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("production-word.original-aliases-and-kitty-actions-every-split", Splits);
        yield return ("production-word.logical-line-marker-kill-yank-and-undo", LinesAndMarkers);
        yield return ("production-word.pending-jump-release-and-unbound-key-precedence", PendingAndUnbound);
        yield return ("production-word.HOLD-source-submit-trims-after-word-kill", SubmissionTrim);
        yield return ("production-word.complete-original-chinese-japanese-thai-source-drafts", DictionarySource);
        yield return ("production-word.complete-sea-original-and-additive-source-drafts", SeaSource);
    }
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static TerminalDraftSnapshot Draft(string text, int cursor) => new(text, cursor);
    private static async Task Observe(string id, string wire, TerminalDraftSnapshot[] expected, string[]? submissions = null)
    {
        await using var run = new Run(); await run.Console.Feed(wire); run.Console.Complete();
        Check(await run.Task.WaitAsync(Bound) == TerminalInputExit.Eof, "Word production fixture failed EOF."); run.Console.AssertJoined();
        var actual = new { drafts = run.Drafts.ToArray(), submissions = run.Submitted.ToArray() };
        var matches = expected.SequenceEqual(actual.drafts) && (submissions ?? []).SequenceEqual(actual.submissions);
        Observations.Add(new { id, wire, expected = new { drafts = expected, submissions = submissions ?? [] }, actual, matches,
            lease = run.Console.Snapshot, childInputTaskJoined = run.Task.IsCompleted, borrowedLeaseDisposed = run.Console.Disposed });
        Check(matches, "Complete actual word production observations differ: " + id);
    }
    private static async Task Splits()
    {
        var bindings = new (string Wire, int Direction, bool Kill)[] {
            ("\u001bb", -1, false), ("\u001b[1;3D", -1, false), ("\u001b[1;5D", -1, false),
            ("\u001bf", 1, false), ("\u001b[1;3C", 1, false), ("\u001b[1;5C", 1, false),
            ("\u0017", -1, true), ("\u001b\u007f", -1, true), ("\u001bd", 1, true), ("\u001b[3;3~", 1, true),
            ("\u001b[98;3u", -1, false), ("\u001b[102;3u", 1, false), ("\u001b[119;5u", -1, true), ("\u001b[127;3u", -1, true), ("\u001b[100;3u", 1, true),
            ("\u001b[98;3:2u", -1, false), ("\u001b[102;3:2u", 1, false), ("\u001b[119;5:2u", -1, true), ("\u001b[127;3:2u", -1, true), ("\u001b[100;3:2u", 1, true),
            ("\u001b[98;3:3u", -1, false), ("\u001b[102;3:3u", 1, false), ("\u001b[119;5:3u", -1, true), ("\u001b[127;3:3u", -1, true), ("\u001b[100;3:3u", 1, true) };
        foreach (var (wire, direction, kill) in bindings)
        for (var split = 0; split <= wire.Length; split++)
        {
            await using var run = new Run(); await run.Expect("one two three", "one two three", 13);
            if (direction > 0) await run.Expect("\u0001", "one two three", 0);
            if (split > 0) await run.Console.Feed(wire[..split]); if (split < wire.Length) await run.Console.Feed(wire[split..]);
            run.Console.Complete(); Check(await run.Task.WaitAsync(Bound) == TerminalInputExit.Eof, "Word split failed EOF."); run.Console.AssertJoined();
            var expected = new List<TerminalDraftSnapshot> { Draft("one two three", 13) }; if (direction > 0) expected.Add(Draft("one two three", 0));
            expected.Add(Draft(kill ? direction < 0 ? "one two " : " two three" : "one two three", kill ? direction < 0 ? 8 : 0 : direction < 0 ? 8 : 3));
            var matches = expected.SequenceEqual(run.Drafts) && run.Submitted.Count == 0;
            Observations.Add(new { id = "word-actual-host-two-chunk", wire, split, expected = new { drafts = expected, submissions = Array.Empty<string>() },
                actual = new { drafts = run.Drafts.ToArray(), submissions = run.Submitted.ToArray() }, matches, lease = run.Console.Snapshot,
                childInputTaskJoined = run.Task.IsCompleted, borrowedLeaseDisposed = run.Console.Disposed }); Check(matches, "Actual host word split differs.");
        }
    }
    private static async Task LinesAndMarkers()
    {
        await Observe("logical-LF-kill-join-yank", "\u001b[200~one\ntwo\u001b[201~\u0017\u0017\u0019\u001f",
            [Draft("one\ntwo", 7), Draft("one\n", 4), Draft("one", 3), Draft("one\ntwo", 7), Draft("one", 3)]);
        var payload = new string('p', 1001); var marker = "[paste #1 1001 chars]";
        await Observe("registered-marker-word-kill-yank-undo", "\u001b[200~" + payload + "\u001b[201~\u0017\u0019\u001f\u001f",
            [Draft(marker, marker.Length), Draft("", 0), Draft(marker, marker.Length), Draft("", 0), Draft(marker, marker.Length)]);
    }
    private static async Task PendingAndUnbound()
    {
        await Observe("pending-jump-word-release-fallthrough", "one two\u001d\u001b[98;3:3ux",
            [Draft("one two", 7), Draft("one two", 7), Draft("one two", 4), Draft("one xtwo", 5)]);
        await Observe("unbound-functional-delete-and-control-backspace", "one two\u001b[57349;3u\u001b[127;5u\u001b[98;4u\u001b[120;1:3u",
            [Draft("one two", 7), Draft("one two", 7), Draft("one two", 7), Draft("one two", 7), Draft("one two", 7)]);
    }
    // Expected is frozen public Editor submitValue(), which trims the expanded result.
    // Lead owns the host's accepted-text/receipt policy; retain the complete mismatch.
    private static Task SubmissionTrim() => Observe("source-submit-trim-after-word-kill", "one two\u0017\r\u0019",
        [Draft("one two", 7), Draft("one ", 4), Draft("", 0), Draft("two", 3)], ["one"]);
    private static async Task DictionarySource()
    {
        using var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "word-controls-source-observations.json")));
        foreach (var id in new[] { "word-cjk-dictionary-source-witness", "word-japanese-dictionary-source-witness", "word-thai-dictionary-source-witness" })
        {
            var example = source.RootElement.GetProperty("cases").EnumerateArray().Single(value => value.GetProperty("id").GetString() == id);
            var wire = string.Concat(example.GetProperty("input").GetProperty("steps").EnumerateArray().Select(value => value.GetProperty("data").GetString()));
            var drafts = example.GetProperty("observation").GetProperty("steps").EnumerateArray().Select(value => Draft(value.GetProperty("text").GetString()!, value.GetProperty("cursorUtf16Offset").GetInt32())).ToArray();
            await Observe("complete-source-dictionary-drafts-" + id, wire, drafts);
        }
    }
    private static async Task SeaSource()
    {
        foreach (var (file, selectOriginal) in new[] { ("word-dictionary-source-observations.json", true), ("word-sea-source-observations.json", false) })
        {
            using var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file)));
            foreach (var example in source.RootElement.GetProperty("cases").EnumerateArray().Where(row => !selectOriginal || row.GetProperty("id").GetString() is "dictionary-full-public-editor-22" or "dictionary-full-public-editor-23" or "dictionary-full-public-editor-24"))
            {
                var packets = example.GetProperty("input").GetProperty("steps").EnumerateArray().Select(value => value.GetProperty("data").GetString()!).ToArray();
                var drafts = example.GetProperty("observation").GetProperty("steps").EnumerateArray().Select(value => Draft(value.GetProperty("text").GetString()!, value.GetProperty("cursorUtf16Offset").GetInt32())).ToArray();
                await using var run = new Run();
                for (var index = 0; index < packets.Length; index++) await run.Expect(packets[index], drafts[index].Text, drafts[index].CursorUtf16Offset);
                run.Console.Complete(); Check(await run.Task.WaitAsync(Bound) == TerminalInputExit.Eof, "SEA schedule failed EOF."); run.Console.AssertJoined();
                var actual = new { drafts = run.Drafts.ToArray(), submissions = run.Submitted.ToArray() }; var matches = drafts.SequenceEqual(actual.drafts) && actual.submissions.Length == 0;
                Observations.Add(new { id = "complete-source-sea-drafts-" + example.GetProperty("id").GetString(), packets, expected = new { drafts, submissions = Array.Empty<string>() }, actual, matches,
                    lease = run.Console.Snapshot, childInputTaskJoined = run.Task.IsCompleted, borrowedLeaseDisposed = run.Console.Disposed });
                Check(matches, "Complete actual SEA production observations differ.");
            }
        }
    }
    private sealed class Run : IAsyncDisposable
    {
        internal readonly Fixture Console = new(); internal readonly CancellationTokenSource Cancel = new(TimeSpan.FromSeconds(10));
        internal readonly List<TerminalDraftSnapshot> Drafts = []; internal readonly List<string> Submitted = [];
        private readonly Channel<TerminalDraftSnapshot> snapshots = Channel.CreateBounded<TerminalDraftSnapshot>(64);
        internal int Interrupts; internal Task<TerminalInputExit> Task { get; }
        internal Run(Func<string, CancellationToken, Task<bool>>? submit = null,
            Func<Run, TerminalDraftSnapshot, CancellationToken, ValueTask>? render = null, bool cancelOnInterrupt = false)
        {
            Task = new TerminalChatInput(Console).RunAsync((line, token) => { Submitted.Add(line); return submit?.Invoke(line, token) ?? System.Threading.Tasks.Task.FromResult(true); },
                async (draft, token) => { if (render is not null) await render(this, draft, token); Drafts.Add(draft); Check(snapshots.Writer.TryWrite(draft), "Fixture snapshot bound exceeded."); },
                () => { Interrupts++; if (cancelOnInterrupt) Cancel.Cancel(); }, Cancel.Token);
        }
        internal async Task Expect(string wire, string text, int cursor)
        { await Console.Feed(wire); Check(await snapshots.Reader.ReadAsync(Cancel.Token).AsTask().WaitAsync(Bound) == Draft(text, cursor), "Production setup snapshot differs."); }
        public async ValueTask DisposeAsync()
        { Cancel.Cancel(); Console.Complete(); try { await Task.WaitAsync(Bound); } catch (Exception) when (Task.IsCompleted) { } Console.AssertJoined(); Cancel.Dispose(); }
    }
    private sealed class Fixture : IConsoleTerminal
    {
        private readonly Channel<string> input = Channel.CreateBounded<string>(8); private readonly object gate = new();
        private readonly SemaphoreSlim startedSignal = new(0); private long started, settled; private int reads, canceled; internal bool Disposed;
        private static readonly TerminalConsoleState State = new(0, 0, 65001, 65001, 25, true, 0, 0);
        public TerminalLeaseSnapshot Snapshot { get { lock (gate) return new(State, State, null, false, false, reads, 0, started, settled, 0, 0); } }
        internal int CanceledReads { get { lock (gate) return canceled; } }
        internal ValueTask Feed(string wire) => input.Writer.WriteAsync(wire);
        internal void Complete() => input.Writer.TryComplete();
        internal async Task WaitForReads(long count)
        { using var bound = new CancellationTokenSource(Bound); while (true) { lock (gate) if (started >= count) return; await startedSignal.WaitAsync(bound.Token); } }
        public async ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default)
        {
            lock (gate) { Check(reads == 0 && !Disposed, "Concurrent or disposed borrowed read."); reads++; started++; } startedSignal.Release();
            try
            {
                if (!await input.Reader.WaitToReadAsync(token)) return 0;
                Check(input.Reader.TryRead(out var wire) && wire is not null && wire.Length > 0 && wire.Length <= destination.Length, "Invalid fake read packet.");
                wire!.AsMemory().CopyTo(destination); return wire!.Length;
            }
            catch (OperationCanceledException) { lock (gate) canceled++; throw; }
            finally { lock (gate) { reads--; settled++; } }
        }
        public ValueTask WriteAsync(ReadOnlyMemory<char> frame, CancellationToken token = default) => throw new InvalidOperationException("Input adapter wrote to borrowed lease.");
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        internal void AssertJoined() { lock (gate) Check(reads == 0 && started == settled && !Disposed, "Adapter left active reads or disposed borrowed lease."); }
    }
}
