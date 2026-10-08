using System.Threading.Channels;
using PiSharp.Cli.Interactive;
using PiSharp.Tui;

/// <summary>Actual production adapter, borrowed virtual lease, and joined asynchronous boundaries.</summary>
internal static class TerminalInputAdapterTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("terminal-input.production-every-two-chunk-split", ChunkSplits),
        ("terminal-input.production-key-aliases-and-scalar-caret", Keys),
        ("terminal-input.production-kitty-printable-release-and-protocol", PrintableAndProtocol),
        ("terminal-input.production-expanded-paste-submit-reset", PasteReset),
        ("terminal-input.production-cancel-resets-paste-and-undo", CancelReset),
        ("terminal-input.production-kill-ring-survives-prompt-reset", KillRing),
        ("terminal-input.production-empty-control-d-and-nonempty-delete", ControlD),
        ("terminal-input.production-escape-timer-and-eof-completion", EscapeAndEof),
        ("terminal-input.production-held-read-cancellation", CancelRead),
        ("terminal-input.production-control-c-interrupt-joins", Interrupt),
        ("terminal-input.production-render-failure-joins-held-read", RenderFailure),
        ("terminal-input.production-submit-failure-joins-held-read", SubmitFailure),
        ("terminal-input.production-producer-failure-precedence", ProducerFailure),
        ("terminal-input.production-existing-event-admission-bound", EventAdmission)
    ];

    private static async Task ChunkSplits()
    {
        (string Wire, string Text, int Cursor)[] cases =
        [
            ("A\U0001f642B", "A\U0001f642B", 4),
            ("a\u001b[DZ", "Za", 1),
            ("A\U0001f642B\u001b[D\u007f", "AB", 1),
            ("\u001b[200~ a\r\nb\t\U0001f642 \u001b[201~", " a\nb    \U0001f642 ", 11),
            ("\u001b[128578u\u001b[57350uX", "X\U0001f642", 1)
        ];
        foreach (var item in cases)
            for (var split = 1; split < item.Wire.Length; split++)
            {
                await using var run = new Run();
                await run.Console.Feed(item.Wire[..split]);
                await run.Console.Feed(item.Wire[split..]);
                run.Console.Complete();
                Check(await run.Task.WaitAsync(Bound) == TerminalInputExit.Eof, "Split input did not reach EOF.");
                var last = run.AllDrafts[^1];
                Check(last == new TerminalDraftSnapshot(item.Text, item.Cursor), "Complete production snapshot changed across input split " + split);
                Check(run.Submitted.Count == 0 && run.Interrupts == 0, "Split input invoked host submission/interrupt.");
                run.Console.AssertJoined();
            }
    }

    private static async Task Keys()
    {
        await using var run = new Run();
        await run.Expect("abc", "abc", 3);
        await run.Expect("\u0001", "abc", 0); // Ctrl-A
        await run.Expect("\u0006", "abc", 1); // Ctrl-F
        await run.Expect("\u0004", "ac", 1); // Ctrl-D deletes inside a draft
        await run.Expect("\u001f", "abc", 1); // Undo uses actual decoded Ctrl-minus
        await run.Expect("\u0005", "abc", 3); // Ctrl-E
        await run.Expect("\u0002", "abc", 2); // Ctrl-B
        await run.Expect("\u001b[127;2u", "ac", 1); // Shift-Backspace
        await run.Expect("\u001f", "abc", 2);
        await run.Expect("\u001b[3;2~", "ab", 2); // Shift-Delete
        await run.Expect("\u001f", "abc", 2);
        await run.Expect("\u001b[1;5H", "abc", 0);
        await run.Expect("\u001b[1;5F", "abc", 3);
        await run.Expect("\n", "abc\n", 4); // Raw LF edits rather than submitting
        await run.Expect("\u001b[13;2u", "abc\n\n", 5);
        await run.Expect("\u001b[13;3u", "abc\n\n\n", 6);
        await run.Expect("\u001b[106;5u", "abc\n\n\n\n", 7);
        await run.Expect("\r", "", 0);
        Check(run.Submitted.SequenceEqual(["abc"]), "Host Enter did not submit the expanded draft with Source ECMAScript trimming.");
        await run.Expect("\u001f", "", 0);
        run.Console.Complete(); Check(await run.Task.WaitAsync(Bound) == TerminalInputExit.Eof, "Keys failed EOF.");
    }

    private static async Task PrintableAndProtocol()
    {
        await using var run = new Run();
        await run.Expect("\u001b[128578u", "\U0001f642", 2);
        await run.Expect("\u001b[90;2u", "\U0001f642Z", 3);
        await run.Expect("\u001b[57349u", "\U0001f642Z\ue005", 4);
        await run.Expect("\u001b[57350u", "\U0001f642Z\ue005", 3);
        await run.Expect("\u001b[57426u", "\U0001f642Z", 3);
        await run.Expect("\u001b[120;1:3u", "\U0001f642Z", 3);
        await run.Expect("\u001b[13;1:3u", "\U0001f642Z", 3);
        await run.Expect("\u001b[99;5:3u", "\U0001f642Z", 3);
        await run.Expect("\u001b[120;1:2u", "\U0001f642Zx", 4);
        await run.Expect("\u001b]0;unsafe title\a", "\U0001f642Zx", 4);
        await run.Expect("\u001b[?1;2c", "\U0001f642Zx", 4);
        await run.Expect("\u001b[999~", "\U0001f642Zx", 4);
        Check(run.Submitted.Count == 0 && run.Interrupts == 0, "Release/protocol key invoked a host action.");
        run.Console.Complete(); Check(await run.Task.WaitAsync(Bound) == TerminalInputExit.Eof, "Printable input failed EOF.");
    }

    private static async Task PasteReset()
    {
        await using var run = new Run();
        var payload = new string('p', 1001) + "\n\u6587\U0001f642";
        await run.Console.Feed("\u001b[200~" + payload[..500]);
        await run.Console.Feed(payload[500..] + "\u001b[201~");
        var marker = await run.Next(); Check(marker.Text == "[paste #1 1005 chars]" && marker.CursorUtf16Offset == marker.Text.Length, "Actual large-paste marker differs.");
        await run.Expect("\u0001", marker.Text, 0);
        await run.Expect("before ", "before " + marker.Text, 7);
        await run.Expect("\r", "", 0);
        Check(run.Submitted.SequenceEqual(["before " + payload]), "Production host submitted marker text instead of expanded data.");
        await run.Expect("\u001f", "", 0); // Accepted submission must release its undo/paste frames
        await run.Expect("\u001b[200~" + new string('q', 1001) + "\u001b[201~", "[paste #1 1001 chars]", 21);
        await run.Expect("\r", "", 0);
        Check(run.Submitted[^1] == new string('q', 1001), "Prompt reset failed to restart paste registry.");
        run.Console.Complete(); Check(await run.Task.WaitAsync(Bound) == TerminalInputExit.Eof, "Paste submission failed EOF.");
    }

    private static async Task CancelReset()
    {
        await using var run = new Run();
        await run.Expect("\u001b[200~" + new string('p', 1001) + "\u001b[201~", "[paste #1 1001 chars]", 21);
        await run.Expect("\u001b[27u", "", 0);
        Check(run.Submitted.SequenceEqual(["/cancel"]), "Cancel action changed host submission.");
        await run.Expect("\u001f", "", 0);
        await run.Expect("\u001b[200~" + new string('q', 1001) + "\u001b[201~", "[paste #1 1001 chars]", 21);
        run.Console.Complete(); Check(await run.Task.WaitAsync(Bound) == TerminalInputExit.Eof, "Cancel reset failed EOF.");
    }

    private static async Task KillRing()
    {
        await using var run = new Run();
        await run.Expect("first", "first", 5);
        await run.Expect("\u0015", "", 0); // Ctrl-U
        await run.Expect("\r", "", 0);
        await run.Expect("\u0019", "first", 5); // Ring intentionally survives Reset
        await run.Expect("\u001b[27u", "", 0);
        await run.Expect("second", "second", 6);
        await run.Expect("\u0001", "second", 0);
        await run.Expect("\u000b", "", 0); // Ctrl-K
        await run.Expect("\u0019", "second", 6);
        await run.Expect("\u001by", "first", 5);
        await run.Expect("\u001f", "second", 6);
        Check(run.Submitted.SequenceEqual(["", "/cancel"]), "Kill/yank changed host actions.");
        run.Console.Complete(); Check(await run.Task.WaitAsync(Bound) == TerminalInputExit.Eof, "Kill ring failed EOF.");
    }

    private static async Task ControlD()
    {
        await using (var run = new Run())
        {
            await run.Expect("x", "x", 1);
            await run.Expect("\u0004", "x", 1); // Nonempty draft at end does not exit
            await run.Expect("\u0001", "x", 0);
            await run.Expect("\u0004", "", 0);
            await run.Console.Feed("\u0004");
            Check(await run.Task.WaitAsync(Bound) == TerminalInputExit.Quit && run.Submitted.Count == 0, "Empty/nonempty Ctrl-D host precedence changed.");
        }
        await using (var run = new Run(submit: (_, _) => Task.FromResult(false)))
        {
            await run.Expect("quit", "quit", 4);
            await run.Console.Feed("\r");
            Check(await run.Task.WaitAsync(Bound) == TerminalInputExit.Quit && run.AllDrafts.Count == 1, "Rejected host continuation rendered a new prompt.");
            Check(run.Submitted.SequenceEqual(["quit"]), "Quit submission changed.");
        }
    }

    private static async Task EscapeAndEof()
    {
        await using (var run = new Run())
        {
            await run.Expect("abc", "abc", 3);
            await run.Expect("\u001b", "", 0); // Actual PeriodicTimer flush
            Check(run.Submitted.SequenceEqual(["/cancel"]), "Pending raw Escape did not invoke timed cancellation.");
            run.Console.Complete(); Check(await run.Task.WaitAsync(Bound) == TerminalInputExit.Eof, "Timed escape failed EOF.");
        }
        foreach (var wire in new[] { "\u001b", "\u001b[", "\u001b]unfinished" })
        {
            await using var run = new Run(); await run.Console.Feed(wire); run.Console.Complete();
            Check(await run.Task.WaitAsync(Bound) == TerminalInputExit.Eof, "Decoder completion did not settle EOF.");
            Check(run.AllDrafts.SequenceEqual([new TerminalDraftSnapshot("", 0)]), "EOF protocol became literal draft data.");
            Check(run.Submitted.SequenceEqual(wire == "\u001b" ? new[] { "/cancel" } : []), "EOF Escape/protocol host action differs.");
        }
    }

    private static async Task CancelRead()
    {
        await using var run = new Run(); await run.Console.WaitForReads(1); run.Cancel.Cancel();
        await ExpectFailure<OperationCanceledException>(run.Task);
        Check(run.Console.CanceledReads == 1 && run.AllDrafts.Count == 0, "Caller cancellation did not cancel exactly the held read.");
    }

    private static async Task Interrupt()
    {
        await using var run = new Run(cancelOnInterrupt: true); await run.Console.Feed("\u0003");
        await ExpectFailure<OperationCanceledException>(run.Task);
        Check(run.Interrupts == 1 && run.AllDrafts.Count == 0 && run.Submitted.Count == 0, "Ctrl-C was rendered/submitted or not delivered exactly once.");
    }

    private static async Task RenderFailure()
    {
        var expected = new IOException("owned render failure");
        await using var run = new Run(render: async (owner, _, _) => { await owner.Console.WaitForReads(2); throw expected; });
        await run.Console.Feed("x"); Check(ReferenceEquals(await ExpectFailure<IOException>(run.Task), expected), "Render failure identity was replaced.");
        Check(run.Console.CanceledReads == 1, "Render failure did not join its outstanding physical read.");
    }

    private static async Task SubmitFailure()
    {
        var expected = new IOException("owned submit failure");
        Run? owner = null;
        await using var run = new Run(submit: async (_, _) => { await owner!.Console.WaitForReads(2); throw expected; }); owner = run;
        await run.Console.Feed("\r"); Check(ReferenceEquals(await ExpectFailure<IOException>(run.Task), expected), "Submit failure identity was replaced.");
        Check(run.Console.CanceledReads == 1 && run.AllDrafts.Count == 0, "Failed submission returned before its physical read settled.");
    }

    private static async Task ProducerFailure()
    {
        foreach (var item in new[] { (Wire: "\ud800x", Failure: TerminalInputFailure.InvalidUnicode), (Wire: "\ud800", Failure: TerminalInputFailure.InvalidUnicode),
            (Wire: "\u001b[200~unfinished", Failure: TerminalInputFailure.IncompleteInput) })
        {
            await using var run = new Run(); await run.Console.Feed(item.Wire); run.Console.Complete();
            Check((await ExpectFailure<TerminalInputException>(run.Task)).Failure == item.Failure, "Producer's decoder failure was replaced by cancellation.");
        }
        foreach (var count in new[] { -1, 4097 })
        {
            await using var run = new Run(); await run.Console.Input.Writer.WriteAsync(new(null, count, null));
            Check((await ExpectFailure<IOException>(run.Task)).Message == "Terminal read returned an invalid character count.", "Invalid read count was not rejected.");
        }
        var expected = new IOException("owned physical producer failure");
        await using var held = new Run(render: async (owner, _, token) =>
        {
            owner.RenderHeld.TrySetResult();
            using var registration = token.Register(() => owner.RenderCanceled.TrySetResult());
            await owner.ReleaseRender.Task; token.ThrowIfCancellationRequested();
        });
        await held.Console.Feed("x"); await held.RenderHeld.Task.WaitAsync(Bound);
        await held.Console.Input.Writer.WriteAsync(new(null, null, expected)); await held.RenderCanceled.Task.WaitAsync(Bound);
        Check(!held.Task.IsCompleted, "Producer failure detached a pending render callback.");
        held.ReleaseRender.TrySetResult();
        Check(ReferenceEquals(await ExpectFailure<IOException>(held.Task), expected), "Producer failure lost precedence to render cancellation.");
    }

    private static async Task EventAdmission()
    {
        await using var run = new Run(render: async (owner, _, token) =>
        {
            owner.RenderHeld.TrySetResult(); using var registration = token.Register(() => owner.RenderCanceled.TrySetResult());
            await owner.ReleaseRender.Task; token.ThrowIfCancellationRequested();
        });
        await run.Console.Feed("x"); await run.RenderHeld.Task.WaitAsync(Bound);
        await run.Console.Feed(string.Concat(Enumerable.Repeat("\u001b[D", 33)));
        await run.RenderCanceled.Task.WaitAsync(Bound); Check(!run.Task.IsCompleted, "Admission failure detached the held render.");
        run.ReleaseRender.TrySetResult();
        Check((await ExpectFailure<InvalidOperationException>(run.Task)).Message == "Terminal input event admission exceeds its bounded profile.", "Existing event admission contract changed.");
    }

    private static async Task<T> ExpectFailure<T>(Task<TerminalInputExit> task) where T : Exception
    {
        try { await task.WaitAsync(Bound); } catch (T error) { return error; }
        throw new InvalidOperationException("Expected joined " + typeof(T).Name);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class Run : IAsyncDisposable
    {
        internal readonly ConsoleFixture Console = new();
        internal readonly CancellationTokenSource Cancel = new(TimeSpan.FromSeconds(15));
        internal readonly List<TerminalDraftSnapshot> AllDrafts = [];
        internal readonly List<string> Submitted = [];
        internal readonly TaskCompletionSource RenderHeld = new(TaskCreationOptions.RunContinuationsAsynchronously),
            RenderCanceled = new(TaskCreationOptions.RunContinuationsAsynchronously), ReleaseRender = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Channel<TerminalDraftSnapshot> snapshots = Channel.CreateBounded<TerminalDraftSnapshot>(64);
        internal int Interrupts;
        internal Task<TerminalInputExit> Task { get; }
        internal Run(Func<string, CancellationToken, Task<bool>>? submit = null,
            Func<Run, TerminalDraftSnapshot, CancellationToken, ValueTask>? render = null, bool cancelOnInterrupt = false)
        {
            Task = new TerminalChatInput(Console).RunAsync((line, token) => { Submitted.Add(line); return submit?.Invoke(line, token) ?? System.Threading.Tasks.Task.FromResult(true); },
                async (draft, token) =>
                {
                    if (render is not null) await render(this, draft, token);
                    AllDrafts.Add(draft); Check(snapshots.Writer.TryWrite(draft), "Fixture retained too many snapshots.");
                }, () => { Interrupts++; if (cancelOnInterrupt) Cancel.Cancel(); }, Cancel.Token);
        }
        internal async Task Expect(string wire, string text, int cursor)
        { await Console.Feed(wire); Check(await Next() == new TerminalDraftSnapshot(text, cursor), "Production draft/caret differs for " + System.Text.Json.JsonSerializer.Serialize(wire)); }
        internal async Task<TerminalDraftSnapshot> Next() => await snapshots.Reader.ReadAsync(Cancel.Token).AsTask().WaitAsync(Bound);
        public async ValueTask DisposeAsync()
        {
            ReleaseRender.TrySetResult(); Cancel.Cancel(); Console.Complete();
            try { await Task.WaitAsync(Bound); } catch (Exception) when (Task.IsCompleted) { }
            Console.AssertJoined(); Cancel.Dispose();
        }
    }

    private sealed record ReadStep(string? Text, int? Count, Exception? Error);
    private sealed class ConsoleFixture : IConsoleTerminal
    {
        internal readonly Channel<ReadStep> Input = Channel.CreateBounded<ReadStep>(8);
        private readonly object gate = new(); private readonly SemaphoreSlim readStartedSignal = new(0);
        private int reads, canceledReads; private long started, settled; private bool disposed;
        private static readonly TerminalConsoleState State = new(0, 0, 65001, 65001, 25, true, 0, 0);
        public TerminalLeaseSnapshot Snapshot { get { lock (gate) return new(State, State, null, false, false, reads, 0, started, settled, 0, 0); } }
        internal int CanceledReads { get { lock (gate) return canceledReads; } }
        internal ValueTask Feed(string text) => Input.Writer.WriteAsync(new(text, null, null));
        internal void Complete() => Input.Writer.TryComplete();
        internal async Task WaitForReads(long count)
        {
            using var bound = new CancellationTokenSource(Bound);
            while (true) { lock (gate) if (started >= count) return; await readStartedSignal.WaitAsync(bound.Token); }
        }
        public async ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default)
        {
            lock (gate) { Check(reads == 0 && !disposed, "Concurrent or disposed borrowed physical read."); reads++; started++; }
            readStartedSignal.Release();
            try
            {
                if (!await Input.Reader.WaitToReadAsync(token)) return 0;
                Check(Input.Reader.TryRead(out var next) && next is not null, "Read fixture lost its admitted item.");
                var step = next ?? throw new IOException("Read fixture returned no item.");
                if (step.Error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(step.Error).Throw();
                if (step.Count is { } count) return count;
                var text = step.Text ?? throw new IOException("Read fixture returned no text.");
                Check(text.Length <= destination.Length, "Fixture exceeded the actual read buffer."); text.AsMemory().CopyTo(destination); return text.Length;
            }
            catch (OperationCanceledException) { lock (gate) canceledReads++; throw; }
            finally { lock (gate) { reads--; settled++; } }
        }
        public ValueTask WriteAsync(ReadOnlyMemory<char> frame, CancellationToken token = default) => throw new InvalidOperationException("Input adapter wrote directly to its lease.");
        public ValueTask DisposeAsync() { lock (gate) disposed = true; return ValueTask.CompletedTask; }
        internal void AssertJoined() { lock (gate) Check(reads == 0 && started == settled && !disposed, "Input returned with active reads or disposed its borrowed lease."); }
    }
}
