using System.Threading.Channels;
using PiSharp.Cli.Interactive;
using PiSharp.Tui;

// Compiled by the isolated friend-assembly probe against the actual CLI project.
// Shared CodingAgent test registration and production host files remain untouched.
internal static class TerminalCharacterJumpProductionTests
{
    internal static readonly List<object> Observations = [];
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("production-jump.raw-forward-backward-and-logical-lines", Search);
        yield return ("production-jump.supplementary-and-compound-packet-targets", Unicode);
        yield return ("production-jump.every-trigger-target-two-chunk-split", Splits);
        yield return ("production-jump.enter-escape-eof-and-empty-ctrl-d-host-reset", Reset);
        yield return ("production-jump.pending-target-interrupt-joins-read", Interrupt);
        yield return ("production-jump.pending-mode-render-fault-joins-without-replay", RenderFailure);
        yield return ("production-jump.pending-mode-submit-fault-joins-without-replay", SubmitFailure);
        yield return ("production-jump.HOLD-printable-kitty-release-routing", TargetRelease);
        yield return ("production-jump.HOLD-released-jump-hotkey-routing", HotkeyRelease);
        yield return ("production-jump.HOLD-focus-protocol-cancellation-routing", Focus);
        yield return ("production-jump.release-and-unknown-cancellation-every-two-chunk-split", RoutingSplits);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static TerminalDraftSnapshot Draft(string text, int cursor) => new(text, cursor);
    private static async Task Observe(string id, string wire, TerminalDraftSnapshot[] expected, string[]? submissions = null)
    {
        await using var run = new Run(); await run.Console.Feed(wire); run.Console.Complete();
        Check(await run.Task.WaitAsync(Bound) == TerminalInputExit.Eof, "Production fixture failed to finish EOF."); run.Console.AssertJoined();
        var actual = run.Drafts.ToArray(); var submitted = run.Submitted.ToArray();
        var matches = expected.SequenceEqual(actual) && (submissions ?? []).SequenceEqual(submitted);
        Observations.Add(new { id, wire, expected = new { drafts = expected, submissions = submissions ?? [] }, actual = new { drafts = actual, submissions = submitted },
            matches, lease = run.Console.Snapshot, run.Interrupts, childInputTaskJoined = run.Task.IsCompleted, borrowedLeaseDisposed = run.Console.Disposed });
        Check(matches, "Complete actual production draft/submission observations differ: " + id);
    }
    private static async Task Search()
    {
        await Observe("raw-forward", "a x a\u0001\u001da", [Draft("a x a", 5), Draft("a x a", 0), Draft("a x a", 0), Draft("a x a", 4)]);
        await Observe("raw-backward", "a x a\u001b\u001da", [Draft("a x a", 5), Draft("a x a", 5), Draft("a x a", 4)]);
        await Observe("logical-lines", "\u001b[200~a\nb\na\u001b[201~\u0001\u001b\u001db\u001b\u001da", [Draft("a\nb\na", 5), Draft("a\nb\na", 4), Draft("a\nb\na", 4), Draft("a\nb\na", 2), Draft("a\nb\na", 2), Draft("a\nb\na", 0)]);
    }
    private static async Task Unicode()
    {
        await Observe("genuine-smile", "A\U0001f642B\U0001f642C\u0001\u001d\U0001f642", [Draft("A\U0001f642B\U0001f642C", 7), Draft("A\U0001f642B\U0001f642C", 0), Draft("A\U0001f642B\U0001f642C", 0), Draft("A\U0001f642B\U0001f642C", 1)]);
        await Observe("compound-packet", "zabaaba\u0001\u001daba", [Draft("zabaaba", 7), Draft("zabaaba", 0), Draft("zabaaba", 0), Draft("zabaaba", 1)]);
    }
    private static async Task Splits()
    {
        foreach (var trigger in new[] { "\u001d", "\u001b\u001d", "\u001b[93;5u", "\u001b[93;7u", "\u001b[93;5:2u", "\u001b[93;7:2u" })
        foreach (var target in new[] { "\U0001f642", "\u001b[128578;1u", "\u001b[128578;1:2u" })
        {
            var wire = trigger + target; var backward = trigger.Contains(";7", StringComparison.Ordinal) || trigger == "\u001b\u001d";
            for (var split = 0; split <= wire.Length; split++)
            {
                await using var run = new Run(); await run.Expect("A\U0001f642B\U0001f642C", "A\U0001f642B\U0001f642C", 7);
                if (!backward) await run.Expect("\u0001", "A\U0001f642B\U0001f642C", 0);
                if (split > 0) await run.Console.Feed(wire[..split]); if (split < wire.Length) await run.Console.Feed(wire[split..]);
                run.Console.Complete(); Check(await run.Task.WaitAsync(Bound) == TerminalInputExit.Eof, "Split fixture failed EOF."); run.Console.AssertJoined();
                var expected = new List<TerminalDraftSnapshot> { Draft("A\U0001f642B\U0001f642C", 7) };
                if (!backward) expected.Add(Draft("A\U0001f642B\U0001f642C", 0));
                expected.Add(Draft("A\U0001f642B\U0001f642C", backward ? 7 : 0)); expected.Add(Draft("A\U0001f642B\U0001f642C", backward ? 4 : 1));
                var matches = expected.SequenceEqual(run.Drafts) && run.Submitted.Count == 0;
                Observations.Add(new { id = "actual-host-two-chunk", wire, split, expected, actual = run.Drafts.ToArray(), matches, lease = run.Console.Snapshot, childInputTaskJoined = run.Task.IsCompleted });
                Check(matches, "Actual host split draft sequence differs.");
            }
        }
    }
    private static async Task Reset()
    {
        await Observe("enter-reset", "abc\u001d\rx", [Draft("abc", 3), Draft("abc", 3), Draft("", 0), Draft("x", 1)], ["abc"]);
        await Observe("escape-reset", "abc\u001d\u001b[27ux", [Draft("abc", 3), Draft("abc", 3), Draft("", 0), Draft("x", 1)], ["/cancel"]);
        await Observe("pending-eof", "abc\u001d", [Draft("abc", 3), Draft("abc", 3)]);
        await using var run = new Run(); await run.Expect("\u001d", "", 0); await run.Console.Feed("\u0004");
        Check(await run.Task.WaitAsync(Bound) == TerminalInputExit.Quit && run.Submitted.Count == 0 && run.Drafts.SequenceEqual([Draft("", 0)]), "Empty Ctrl-D host precedence changed while pending.");
        run.Console.AssertJoined(); Observations.Add(new { id = "pending-empty-ctrl-d", exit = "Quit", drafts = run.Drafts.ToArray(), lease = run.Console.Snapshot, childInputTaskJoined = run.Task.IsCompleted });
    }
    private static async Task Interrupt()
    {
        await using var run = new Run(cancelOnInterrupt: true); await run.Expect("\u001d", "", 0); await run.Console.Feed("\u0003");
        await Failure<OperationCanceledException>(run.Task); run.Console.AssertJoined();
        Check(run.Interrupts == 1 && run.Drafts.SequenceEqual([Draft("", 0)]) && run.Submitted.Count == 0, "Pending jump changed interrupt routing.");
        Observations.Add(new { id = "pending-interrupt", drafts = run.Drafts.ToArray(), submissions = run.Submitted.ToArray(), run.Interrupts, lease = run.Console.Snapshot, childInputTaskJoined = run.Task.IsCompleted });
    }
    private static async Task RenderFailure()
    {
        var fault = new IOException("jump render failure");
        await using var run = new Run(render: async (owner, _, _) => { await owner.Console.WaitForReads(2); throw fault; });
        await run.Console.Feed("\u001d"); Check(ReferenceEquals(await Failure<IOException>(run.Task), fault), "Jump render error identity changed."); run.Console.AssertJoined();
        Check(run.Console.CanceledReads == 1 && run.Drafts.Count == 0 && run.Submitted.Count == 0, "Fault retried pending jump or left physical read.");
        Observations.Add(new { id = "pending-render-fault", error = fault.Message, lease = run.Console.Snapshot, childInputTaskJoined = run.Task.IsCompleted });
    }
    private static async Task SubmitFailure()
    {
        var fault = new IOException("jump submit failure"); Run? owner = null;
        await using var run = new Run(submit: async (_, _) => { await owner!.Console.WaitForReads(3); throw fault; }); owner = run;
        await run.Expect("\u001d", "", 0); await run.Console.Feed("\r");
        Check(ReferenceEquals(await Failure<IOException>(run.Task), fault), "Jump submit error identity changed."); run.Console.AssertJoined();
        Check(run.Console.CanceledReads == 1 && run.Drafts.SequenceEqual([Draft("", 0)]) && run.Submitted.SequenceEqual([""]), "Pending mode submit replayed or failed to join.");
        Observations.Add(new { id = "pending-submit-fault", error = fault.Message, drafts = run.Drafts.ToArray(), submissions = run.Submitted.ToArray(), lease = run.Console.Snapshot, childInputTaskJoined = run.Task.IsCompleted });
    }
    private static Task TargetRelease() => Observe("source-pending-printable-release", "axxx\u0001\u001d\u001b[120;1:3ux",
        [Draft("axxx", 4), Draft("axxx", 0), Draft("axxx", 0), Draft("axxx", 1), Draft("axxxx", 2)]);
    private static Task HotkeyRelease() => Observe("source-pending-released-hotkey", "axxx\u0001\u001d\u001b[93;7:3ux",
        [Draft("axxx", 4), Draft("axxx", 0), Draft("axxx", 0), Draft("axxx", 0), Draft("xaxxx", 1)]);
    private static Task Focus() => Observe("source-pending-focus-protocol", "axxx\u0001\u001d\u001b[Ix",
        [Draft("axxx", 4), Draft("axxx", 0), Draft("axxx", 0), Draft("axxx", 0), Draft("xaxxx", 1)]);
    private static async Task RoutingSplits()
    {
        // These are the dropped event families; each exact historical assertion above remains intact.
        foreach (var (id, wire, first, final) in new[]
        {
            ("pending-printable-release", "\u001b[120;1:3ux", Draft("axxx", 1), Draft("axxxx", 2)),
            ("pending-released-jump-chord", "\u001b[93;7:3ux", Draft("axxx", 0), Draft("xaxxx", 1)),
            ("pending-focus-protocol", "\u001b[Ix", Draft("axxx", 0), Draft("xaxxx", 1)),
            ("pending-unknown-sequence", "\u001b[?999zx", Draft("axxx", 0), Draft("xaxxx", 1))
        })
        for (var split = 0; split <= wire.Length; split++)
        {
            await using var run = new Run(); await run.Expect("axxx", "axxx", 4); await run.Expect("\u0001", "axxx", 0); await run.Expect("\u001d", "axxx", 0);
            if (split > 0) await run.Console.Feed(wire[..split]); if (split < wire.Length) await run.Console.Feed(wire[split..]);
            run.Console.Complete(); Check(await run.Task.WaitAsync(Bound) == TerminalInputExit.Eof, "Routing split did not reach EOF."); run.Console.AssertJoined();
            var expected = new[] { Draft("axxx", 4), Draft("axxx", 0), Draft("axxx", 0), first, final };
            var actual = run.Drafts.ToArray(); var matches = expected.SequenceEqual(actual) && run.Submitted.Count == 0;
            Observations.Add(new { id, wire, split, expected = new { drafts = expected, submissions = Array.Empty<string>() },
                actual = new { drafts = actual, submissions = run.Submitted.ToArray() }, matches, lease = run.Console.Snapshot, run.Interrupts,
                childInputTaskJoined = run.Task.IsCompleted, borrowedLeaseDisposed = run.Console.Disposed });
            Check(matches, "Complete released/protocol input observations differ: " + id);
        }
    }
    private static async Task<T> Failure<T>(Task<TerminalInputExit> task) where T : Exception
    { try { await task.WaitAsync(Bound); } catch (T error) { return error; } throw new InvalidOperationException("Expected joined " + typeof(T).Name); }
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
