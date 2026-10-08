using PiSharp.Cli.Interactive;
using PiSharp.Tui;
using PiSharp.Tui.Rendering;

internal static class TerminalCustomComponentPresentationTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("custom-native.frame-preserves-sgr-and-admitted-original-cell-width", Frame),
        ("custom-native.unsupported-controls-and-overflow-before-write", Rejection),
        ("custom-native.actual-view-held-render-and-physical-write-close-joins", HeldView)
    ];
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Native custom presentation control failed."); }
    private static Task Frame()
    {
        var frame = TerminalCustomComponentFrameFactory.Create(["\u001b[31m界\u001b[0m"], [2], 2, 4);
        Check(frame.Rows.Length == 2 && frame.Rows[0].CellWidth == 2 && frame.Rows[1].CellWidth == 0);
        Check(frame.Rows[0].Text == "\u001b[31m界\u001b[0m\u001b[0m" && !frame.Cursor.Visible && !frame.IsClipped);
        Check(frame.WidthPolicyId == TerminalCustomComponentFrameFactory.OriginalWidthPolicyId);
        return Task.CompletedTask;
    }
    private static Task Rejection()
    {
        foreach (var source in new[] { "\u001b]0;title\u0007", "\u001b[2J", "\n", "\u009b31m", "\ud800" })
        {
            var refused = false;
            try { _ = TerminalCustomComponentFrameFactory.Create([source], [0], 2, 4); }
            catch (TerminalRenderException) { refused = true; }
            Check(refused);
        }
        var overflow = false;
        try { _ = TerminalCustomComponentFrameFactory.Create(["too wide"], [8], 2, 4); }
        catch (TerminalRenderException error) when (error.Failure == TerminalRenderFailure.ResourceLimit) { overflow = true; }
        Check(overflow); return Task.CompletedTask;
    }
    private static async Task HeldView()
    {
        var console = new ConsoleFixture(); var view = new TerminalSessionView(console, new Viewport());
        var renderEntered = Gate(); var renderOriginal = new TaskCompletionSource<TerminalCustomComponentRows>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new TerminalCustomComponentRows(["safe"], [4]);
        var owner = new TerminalCustomComponentPresentation("owner", 1, 1, "component-1", (width, _) =>
        { Check(width == 8); renderEntered.TrySetResult(); return renderOriginal.Task; });
        var originals = new List<Task> { renderOriginal.Task }; var errors = new List<Exception>();
        Task? close = null;
        try
        {
            var start = view.StartAsync(CancellationToken.None).AsTask(); originals.Add(start); await start;
            console.HoldNext = true;
            var open = view.OpenCustomComponentAsync(owner, CancellationToken.None).AsTask(); originals.Add(open);
            await Task.WhenAny(renderEntered.Task, open); Check(renderEntered.Task.IsCompleted && !open.IsCompleted && console.Started == 1);
            renderOriginal.TrySetResult(source);
            await Task.WhenAny(console.HeldEntered.Task, open); Check(console.HeldEntered.Task.IsCompleted && !open.IsCompleted);
            close = view.DisposeAsync().AsTask(); originals.Add(close); Check(!close.IsCompleted && console.Active == 1);
            console.Release.TrySetResult(); await open; await close;
            Check(console.Active == 0 && console.Started == console.Settled && !console.Disposed);
            Check(console.Writes.Any(text => text.Contains("safe\u001b[0m", StringComparison.Ordinal)));
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            renderOriginal.TrySetResult(source); console.Release.TrySetResult();
            try { close ??= view.DisposeAsync().AsTask(); originals.Add(close); } catch (Exception error) { errors.Add(error); }
            foreach (var original in new HashSet<Task>(originals, ReferenceEqualityComparer.Instance))
                try { await original; } catch (Exception error) { errors.Add(original.Exception ?? error); }
            if (console.Active != 0 || console.Started != console.Settled) errors.Add(new InvalidOperationException("Actual physical original not joined."));
        }
        if (errors.Count != 0) throw new AggregateException(errors);
    }
    private sealed class Viewport : ITerminalViewportSource
    {
        public TerminalViewport ReadViewport() => new(8, 2, 0, 0, 8, 2);
    }
    private sealed class ConsoleFixture : IConsoleTerminal
    {
        internal bool HoldNext, Disposed;
        internal int Active, Started, Settled;
        internal readonly List<string> Writes = [];
        internal TaskCompletionSource HeldEntered { get; } = Gate();
        internal TaskCompletionSource Release { get; } = Gate();
        public TerminalLeaseSnapshot Snapshot => new(new(0, 0, 65001, 65001, 25, true, 0, 0),
            new(0, 0, 65001, 65001, 25, true, 0, 0), null, false, false, 0, Active, 0, 0, Started, Settled);
        public ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default) => throw new InvalidOperationException("Presentation acquired input authority.");
        public async ValueTask WriteAsync(ReadOnlyMemory<char> text, CancellationToken token = default)
        {
            Check(Interlocked.Increment(ref Active) == 1); Interlocked.Increment(ref Started);
            try
            {
                Writes.Add(text.ToString());
                if (HoldNext) { HoldNext = false; HeldEntered.TrySetResult(); await Release.Task; }
            }
            finally { Interlocked.Decrement(ref Active); Interlocked.Increment(ref Settled); }
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
