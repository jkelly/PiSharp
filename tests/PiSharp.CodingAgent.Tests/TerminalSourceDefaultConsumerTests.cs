using System.Threading.Channels;
using PiSharp.Cli.Interactive;
using PiSharp.Tui;
using PiSharp.Tui.Input;
using PiSharp.Tui.Rendering;

internal static class TerminalSourceDefaultConsumerTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(8);
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("terminal source default first controller capture precedes input and held writes leave geometry synchronous", InitialCapture),
        ("terminal source default initial captured-render failure joins reader and receipt ownership", InitialFailure)
    ];

    private static async Task InitialCapture()
    {
        var console = new ConsoleFixture(holdFirstEditorWrite: true);
        var frames = Channel.CreateBounded<(TerminalEditorVisualMap Map, TerminalEditorRenderedFrame Frame)>(4);
        var drafts = Channel.CreateBounded<TerminalDraftSnapshot>(4);
        var receipts = new TerminalSubmissionReceipts();
        var view = new TerminalSessionView(console, console, (map, frame) =>
            Check(frames.Writer.TryWrite((map, frame)), "Actual default frame observation exceeded its bound."));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<TerminalInputExit>? input = null; var ended = 0;
        try
        {
            await view.StartAsync(stop.Token);
            Check(console.Writes.Count == 1 && console.Writes[0] == "\u001b[?1049h\u001b[?2004h",
                "View painted an editor before the authoritative controller existed.");
            input = new TerminalChatInput(console).RunAcknowledgedAsync((_, _) => throw new InvalidOperationException("Unexpected submit."),
                _ => Task.CompletedTask, Render, receipts, (_, _) => throw new InvalidOperationException("Unexpected acknowledgment."),
                _ => { Interlocked.Increment(ref ended); }, stop.Cancel, stop.Token, view.CaptureEditorGeometry);
            await console.EditorWriteEntered.Task.WaitAsync(Bound);
            var first = await drafts.Reader.ReadAsync(stop.Token).AsTask().WaitAsync(Bound);
            var selected = await frames.Reader.ReadAsync(stop.Token).AsTask().WaitAsync(Bound);
            Check(first.Text == "" && first.CursorUtf16Offset == 0 && first.Layout is not null,
                "Initial callback was not the input controller's actual empty capture.");
            var firstLayout = first.Layout ?? throw new InvalidOperationException("Missing authoritative initial layout.");
            Check(selected.Map.EditorIdentity == firstLayout.Identity && selected.Map.SourceText == "" &&
                selected.Map.PolicyId == TerminalEditorVisualMapBuilder.SourcePolicyId &&
                selected.Map.ContentColumns == 12 && selected.Map.ViewportRows == 2 &&
                selected.Map.ScrollResetRevision == firstLayout.ScrollResetRevision,
                "Actual first paint did not use the captured controller, Source policy and physical dimensions.");
            Check(selected.Frame.SourceComponent.Rows.Length == 3 && selected.Frame.Frame.Rows.Length == 2 &&
                !selected.Frame.Frame.Cursor.Visible, "Source component crop or default hidden hardware cursor changed.");

            // These calls are intentionally synchronous while the actual renderer write is held.
            var original = view.CaptureEditorGeometry();
            console.Viewport = new(7, 3, 0, 0, 7, 3); var resized = view.CaptureEditorGeometry();
            console.Viewport = new(12, 2, 0, 0, 12, 2); var returned = view.CaptureEditorGeometry();
            Check(original.Identity.ViewLifetimeId == resized.Identity.ViewLifetimeId &&
                resized.Identity.GeometryRevision > original.Identity.GeometryRevision &&
                returned.Identity.GeometryRevision > resized.Identity.GeometryRevision &&
                returned.Identity != original.Identity, "Held write lost synchronous resize/ABA geometry identity.");
            await console.Feed("x"); await console.ChunkRead.Task.WaitAsync(Bound);
            Check(!drafts.Reader.TryRead(out _) && console.Snapshot.ActiveWrites == 1,
                "Decoded input was consumed before the authoritative first render settled.");
            console.ReleaseEditorWrite.TrySetResult();
            var typed = await drafts.Reader.ReadAsync(stop.Token).AsTask().WaitAsync(Bound);
            var next = await frames.Reader.ReadAsync(stop.Token).AsTask().WaitAsync(Bound);
            var typedLayout = typed.Layout ?? throw new InvalidOperationException("Missing authoritative typed layout.");
            Check(typed.Text == "x" && typed.Layout is not null &&
                typedLayout.Identity.EditorLifetimeId == firstLayout.Identity.EditorLifetimeId &&
                typedLayout.ScrollResetRevision == firstLayout.ScrollResetRevision,
                "Ordinary insertion recreated the controller or invented a scroll reset.");
            Check(next.Map.EditorIdentity == typedLayout.Identity && next.Map.SourceText == "x" &&
                next.Map.GeometryIdentity == returned.Identity,
                "Next actual default paint did not use the same controller and fresh geometry capture.");
            receipts.Complete(); console.Complete(); await input.WaitAsync(Bound);
            await view.DisposeAsync(); console.AssertJoined();
            Check(ended == 1 && console.Writes[^1] == "\u001b[?2004l\u001b[?1049l",
                "Initial-frame workflow did not join input and restore borrowed terminal modes.");
        }
        finally
        {
            console.ReleaseEditorWrite.TrySetResult(); receipts.Complete(); stop.Cancel(); console.Complete();
            if (input is not null) { try { await input; } catch (OperationCanceledException) { } }
            await view.DisposeAsync(); console.AssertJoined();
        }
        async ValueTask Render(TerminalDraftSnapshot captured, CancellationToken token)
        {
            Check(drafts.Writer.TryWrite(captured), "Captured draft observations exceeded their bound.");
            await view.SetDraftAsync(captured, token);
        }
    }

    private static async Task InitialFailure()
    {
        var console = new ConsoleFixture(holdFirstEditorWrite: false); var receipts = new TerminalSubmissionReceipts();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var renderCalls = 0; var ended = 0; Exception? notified = null;
        var running = new TerminalChatInput(console).RunAcknowledgedAsync((_, _) => throw new InvalidOperationException("Unexpected submit."),
            _ => Task.CompletedTask, (capture, _) =>
            {
                Interlocked.Increment(ref renderCalls);
                Check(capture.Text == "" && capture.Layout is not null, "Startup failure did not enter the real initial capture.");
                return ValueTask.FromException(new IOException("authored initial captured-render fault"));
            }, receipts, (_, _) => throw new InvalidOperationException("Unexpected acknowledgment."),
            error => { notified = error; Interlocked.Increment(ref ended); receipts.Complete(); }, stop.Cancel, stop.Token,
            () => new(new(Guid.Parse("71062e7f-9b4d-4d21-9a97-cb836736ea88"), 1, TerminalEditorVisualMapBuilder.SourcePolicyId), 12, 2));
        try
        {
            try { await running.WaitAsync(Bound); throw new InvalidOperationException("Initial render fault was swallowed."); }
            catch (IOException error) { Check(error.Message == "authored initial captured-render fault", "Startup replaced the original owned failure."); }
            Check(renderCalls == 1 && ended == 1 && notified is IOException,
                "Initial capture failure replayed render or lost its input-ended notification.");
            Check(console.Writes.Count == 0, "Failed callback emitted an unowned terminal frame.");
            console.AssertJoined();
        }
        finally { receipts.Complete(); stop.Cancel(); console.Complete(); try { await running; } catch (IOException) { } console.AssertJoined(); }
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class ConsoleFixture(bool holdFirstEditorWrite) : IConsoleTerminal, ITerminalViewportSource
    {
        private readonly Channel<string> input = Channel.CreateBounded<string>(2);
        private readonly object gate = new(); private int reads, writes;
        private long readStarted, readSettled, writeStarted, writeSettled; private bool editorWriteSeen, disposed;
        private static readonly TerminalConsoleState State = new(0, 0, 65001, 65001, 25, true, 0, 0);
        internal TerminalViewport Viewport = new(12, 2, 0, 0, 12, 2);
        internal readonly List<string> Writes = [];
        internal readonly TaskCompletionSource EditorWriteEntered = NewGate(), ReleaseEditorWrite = NewGate(), ChunkRead = NewGate();
        public TerminalLeaseSnapshot Snapshot { get { lock (gate) return new(State, State, null, false, false, reads, writes, readStarted, readSettled, writeStarted, writeSettled); } }
        public TerminalViewport ReadViewport() => Viewport;
        internal ValueTask Feed(string value) => input.Writer.WriteAsync(value);
        internal void Complete() => input.Writer.TryComplete();
        public async ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default)
        {
            lock (gate) { Check(reads == 0, "Concurrent physical reads."); reads++; readStarted++; }
            try
            {
                if (!await input.Reader.WaitToReadAsync(token)) return 0;
                Check(input.Reader.TryRead(out var value) && value.Length <= destination.Length, "Physical chunk exceeded its bound.");
                var text = value ?? throw new IOException("Missing owned physical chunk."); text.AsMemory().CopyTo(destination); ChunkRead.TrySetResult(); return text.Length;
            }
            finally { lock (gate) { reads--; readSettled++; } }
        }
        public async ValueTask WriteAsync(ReadOnlyMemory<char> value, CancellationToken token = default)
        {
            bool hold;
            lock (gate)
            {
                Check(writes == 0, "Concurrent physical writes."); writes++; writeStarted++;
                var text = value.ToString(); Writes.Add(text);
                hold = holdFirstEditorWrite && !editorWriteSeen && text != "\u001b[?1049h\u001b[?2004h" && text != "\u001b[?2004l\u001b[?1049l";
                if (hold) editorWriteSeen = true;
            }
            try { if (hold) { EditorWriteEntered.TrySetResult(); await ReleaseEditorWrite.Task.WaitAsync(token); } token.ThrowIfCancellationRequested(); }
            finally { lock (gate) { writes--; writeSettled++; } }
        }
        internal void AssertJoined() { lock (gate) Check(reads == 0 && writes == 0 && readStarted == readSettled && writeStarted == writeSettled && !disposed,
            "Physical work remained active or the borrowed terminal was disposed."); }
        public ValueTask DisposeAsync() { disposed = true; return ValueTask.CompletedTask; }
        private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
