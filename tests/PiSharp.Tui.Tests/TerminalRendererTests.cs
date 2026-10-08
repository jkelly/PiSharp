using PiSharp.Tui;
using PiSharp.Tui.Rendering;

internal static class TerminalRendererTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("terminal-renderer.exact-full-diff-equality-cursor-and-virtual-screen", Frames);
        yield return ("terminal-renderer.actual-write-hold-delays-cache-and-equal-followup", CommitBoundary);
        yield return ("terminal-renderer.bounded-concurrent-admission-preserves-physical-order", Ordering);
        yield return ("terminal-renderer.partial-physical-fault-invalidates-and-full-redraw-recovers", FaultRecovery);
        yield return ("terminal-renderer.active-and-waiting-cancellation-settle-before-recovery", Cancellation);
        yield return ("terminal-renderer.resize-during-write-and-same-size-invalidation", Resize);
        yield return ("terminal-renderer.untrusted-controls-newlines-tabs-and-whole-graphemes", Text);
        yield return ("terminal-renderer.hard-input-cell-row-column-grapheme-and-frame-bounds", Bounds);
        yield return ("terminal-renderer.disposal-drains-physical-work-and-keeps-borrowed-lease", Disposal);
    }

    private static TerminalTextLayout Layout(TerminalRenderLimits? limits = null) => new(new FixtureWidth(), limits);

    private static async Task Frames()
    {
        var screen = new VirtualScreen(2, 4); var sink = new PhysicalSink(screen);
        await using var renderer = new VtRenderer(sink); var layout = Layout();
        var first = layout.CreateFrame("abcd\nxy", 2, 4);
        var full = await renderer.RenderAsync(first);
        const string firstBytes = "\u001b[0m\u001b[2J\u001b[1;1H\u001b[2Kabcd\u001b[2;1H\u001b[2Kxy\u001b[1;1H\u001b[?25h";
        Equal(firstBytes, sink.Attempts.Single()); Equal(firstBytes.Length, full.WrittenCharacters);
        Equal(TerminalRenderKind.Full, full.Kind); True(full.CacheCommitted); Equal("abcd\nxy  ", screen.Text);
        var unchanged = await renderer.RenderAsync(layout.CreateFrame("abcd\nxy", 2, 4));
        Equal(TerminalRenderKind.Unchanged, unchanged.Kind); Equal(0, unchanged.WrittenCharacters); Equal(1, sink.Attempts.Length);
        var second = layout.CreateFrame("a\nxy", 2, 4, new(1, 2, false));
        var diff = await renderer.RenderAsync(second);
        Equal("\u001b[0m\u001b[1;1H\u001b[2Ka\u001b[2;3H\u001b[?25l", sink.Attempts[1]);
        Equal(TerminalRenderKind.Diff, diff.Kind); Equal("a   \nxy  ", screen.Text);
        Equal(1, screen.Row); Equal(2, screen.Column); True(!screen.CursorVisible);
        await renderer.RenderAsync(layout.CreateFrame("a\nxy", 2, 4, new(0, 3)));
        Equal("\u001b[0m\u001b[1;4H\u001b[?25h", sink.Attempts[2]);
        Equal("a   \nxy  ", screen.Text); Equal(1, sink.MaximumActiveWrites);
    }

    private static async Task CommitBoundary()
    {
        var sink = new PhysicalSink(); var hold = sink.HoldNext();
        await using var renderer = new VtRenderer(sink); var frame = Layout().CreateFrame("same", 1, 4);
        var first = renderer.RenderAsync(frame).AsTask(); await hold.Entered.Task;
        True(!first.IsCompleted); True(!renderer.Snapshot.CacheKnown);
        Equal(1L, renderer.Snapshot.PhysicalWritesStarted); Equal(0L, renderer.Snapshot.PhysicalWritesSettled);
        var equal = renderer.RenderAsync(frame).AsTask(); True(!equal.IsCompleted); Equal(1, sink.Attempts.Length);
        hold.Complete(); True((await first).CacheCommitted);
        Equal(TerminalRenderKind.Unchanged, (await equal).Kind); Equal(1, sink.Attempts.Length);
        True(renderer.Snapshot.CacheKnown); Equal(0, renderer.Snapshot.PendingRenders);
        Equal(1L, renderer.Snapshot.PhysicalWritesSettled);
    }

    private static async Task Ordering()
    {
        var limits = new TerminalRenderLimits(MaximumPendingRenders: 3); var sink = new PhysicalSink(); var hold = sink.HoldNext();
        await using var renderer = new VtRenderer(sink, limits); var layout = Layout(limits);
        var first = renderer.RenderAsync(layout.CreateFrame("a", 1, 4)).AsTask(); await hold.Entered.Task;
        var second = renderer.RenderAsync(layout.CreateFrame("b", 1, 4)).AsTask();
        var third = renderer.RenderAsync(layout.CreateFrame("c", 1, 4)).AsTask();
        Failure(TerminalRenderFailure.ResourceLimit, () => renderer.RenderAsync(layout.CreateFrame("d", 1, 4)));
        Equal(3, renderer.Snapshot.PendingRenders); True(!second.IsCompleted && !third.IsCompleted); Equal(1, sink.Attempts.Length);
        hold.Complete(); var results = await Task.WhenAll(first, second, third);
        Equal(TerminalRenderKind.Full, results[0].Kind); Equal(TerminalRenderKind.Diff, results[1].Kind); Equal(TerminalRenderKind.Diff, results[2].Kind);
        Equal("\u001b[0m\u001b[2J\u001b[1;1H\u001b[2Ka\u001b[1;1H\u001b[?25h", sink.Attempts[0]);
        Equal("\u001b[0m\u001b[1;1H\u001b[2Kb\u001b[1;1H\u001b[?25h", sink.Attempts[1]);
        Equal("\u001b[0m\u001b[1;1H\u001b[2Kc\u001b[1;1H\u001b[?25h", sink.Attempts[2]);
        Equal(1, sink.MaximumActiveWrites); Equal(3L, renderer.Snapshot.PhysicalWritesStarted);
        Equal(3L, renderer.Snapshot.PhysicalWritesSettled); Equal(0, renderer.Snapshot.PendingRenders);
    }

    private static async Task FaultRecovery()
    {
        var screen = new VirtualScreen(1, 4); var sink = new PhysicalSink(screen);
        await using var renderer = new VtRenderer(sink); var layout = Layout();
        await renderer.RenderAsync(layout.CreateFrame("abcd", 1, 4));
        var failing = sink.HoldNext();
        var write = renderer.RenderAsync(layout.CreateFrame("z", 1, 4)).AsTask(); await failing.Entered.Task;
        // Physical mutation is visible before failure, so recovery must not trust the last successful image.
        failing.Complete(new IOException("Injected partial write."), "\u001b[0m\u001b[1;1H\u001b[2Kz".Length);
        await Throws<IOException>(() => write); Equal("z   ", screen.Text); True(!renderer.Snapshot.CacheKnown);
        Equal(TerminalRenderKind.Full, (await renderer.RenderAsync(layout.CreateFrame("abcd", 1, 4))).Kind);
        True(sink.Attempts[2].Contains("\u001b[2J", StringComparison.Ordinal)); Equal("abcd", screen.Text);
        Equal(3L, renderer.Snapshot.PhysicalWritesSettled); Equal(1, sink.MaximumActiveWrites);
    }

    private static async Task Cancellation()
    {
        var sink = new PhysicalSink(); await using var renderer = new VtRenderer(sink); var layout = Layout();
        await renderer.RenderAsync(layout.CreateFrame("a", 1, 4)); var hold = sink.HoldNext();
        using var activeCancel = new CancellationTokenSource(); using var waitingCancel = new CancellationTokenSource();
        var active = renderer.RenderAsync(layout.CreateFrame("b", 1, 4), activeCancel.Token).AsTask(); await hold.Entered.Task;
        True(!renderer.Snapshot.CacheKnown);
        var waiting = renderer.RenderAsync(layout.CreateFrame("c", 1, 4), waitingCancel.Token).AsTask();
        activeCancel.Cancel(); waitingCancel.Cancel();
        True(!active.IsCompleted && !waiting.IsCompleted); Equal(1, sink.ActiveWrites); Equal(2, sink.Attempts.Length);
        hold.Complete(); await Cancelled(active, activeCancel.Token); await Cancelled(waiting, waitingCancel.Token);
        True(!renderer.Snapshot.CacheKnown); Equal(0, sink.ActiveWrites); Equal(0, renderer.Snapshot.PendingRenders);
        Equal(TerminalRenderKind.Full, (await renderer.RenderAsync(layout.CreateFrame("c", 1, 4))).Kind);
        using var before = new CancellationTokenSource(); before.Cancel();
        await CancelledCall(() => renderer.RenderAsync(layout.CreateFrame("c", 1, 4), before.Token).AsTask(), before.Token);
        True(!renderer.Snapshot.CacheKnown); Equal(3, sink.Attempts.Length);
        Equal(TerminalRenderKind.Full, (await renderer.RenderAsync(layout.CreateFrame("c", 1, 4))).Kind);
    }

    private static async Task Resize()
    {
        var sink = new PhysicalSink(); await using var renderer = new VtRenderer(sink); var layout = Layout();
        var original = layout.CreateFrame("old", 1, 4); await renderer.RenderAsync(original);
        var hold = sink.HoldNext(); var active = renderer.RenderAsync(layout.CreateFrame("new", 1, 4)).AsTask(); await hold.Entered.Task;
        renderer.Invalidate(); True(!renderer.Snapshot.CacheKnown); hold.Complete(); True(!(await active).CacheCommitted);
        var resized = layout.CreateFrame("new", 2, 3);
        Equal(TerminalRenderKind.Full, (await renderer.RenderAsync(resized)).Kind);
        Equal(TerminalRenderKind.Unchanged, (await renderer.RenderAsync(resized)).Kind);
        renderer.Invalidate(); Equal(TerminalRenderKind.Full, (await renderer.RenderAsync(resized)).Kind);
        Equal(TerminalRenderKind.Full, (await renderer.RenderAsync(original)).Kind);
        Equal(5, sink.Attempts.Length);
    }

    private static async Task Text()
    {
        var layout = Layout(); var controls = layout.CreateFrame("\u001b[31mX\u009b2J\u0000\u0007\r\n\tY", 2, 20);
        Equal("?[31mX?2J??", controls.Rows[0].Text); Equal("   Y", controls.Rows[1].Text);
        var sink = new PhysicalSink(); await using var renderer = new VtRenderer(sink); await renderer.RenderAsync(controls);
        var output = sink.Attempts.Single(); True(!output.Contains("\u001b[31m", StringComparison.Ordinal));
        True(!output.Contains('\r') && !output.Contains('\n') && !output.Contains('\t'));
        True(!output.Any(value => value is >= '\u007f' and <= '\u009f' || value < 0x20 && value != '\u001b'));
        var allControls = layout.CreateFrame(new string(Enumerable.Range(0, 160).Where(value => value < 32 || value >= 127).Select(value => (char)value).ToArray()), 40, 8);
        True(allControls.Rows.All(value => !value.Text.Any(character => character < 32 || character is >= '\u007f' and <= '\u009f')));
        var graphemes = layout.CreateFrame("Ae\u0301\u4e2d\ud83d\ude42B", 2, 4);
        Equal("Ae\u0301\u4e2d", graphemes.Rows[0].Text); Equal(4, graphemes.Rows[0].CellWidth);
        Equal("\ud83d\ude42B", graphemes.Rows[1].Text); Equal(3, graphemes.Rows[1].CellWidth); True(!graphemes.IsClipped);
        const string family = "\ud83d\udc69\u200d\ud83d\udc69\u200d\ud83d\udc67\u200d\ud83d\udc66";
        var zwj = layout.CreateFrame("A" + family + "B", 3, 2);
        Equal("A", zwj.Rows[0].Text); Equal(family, zwj.Rows[1].Text); Equal("B", zwj.Rows[2].Text);
        Failure(TerminalRenderFailure.InvalidCursor, () => layout.CreateFrame("A" + family + "B", 3, 2, new(1, 1)));
        var narrow = layout.CreateFrame(family + "B", 1, 1); Equal("B", narrow.Rows.Single().Text); True(narrow.IsClipped);
        var clipped = layout.CreateFrame("e\u0301\ud83d\ude42", 1, 1); Equal("e\u0301", clipped.Rows.Single().Text); True(clipped.IsClipped);
        Equal("a", layout.CreateFrame("\u0301a", 1, 2).Rows.Single().Text);
        foreach (var invalid in new[] { "\ud800", "\udc00", "x\ud800y", "abcd\udc00" })
            Failure(TerminalRenderFailure.InvalidUnicode, () => layout.CreateFrame(invalid, 1, 1));
    }

    private static async Task Bounds()
    {
        var sink = new PhysicalSink();
        foreach (var limits in new TerminalRenderLimits[] { new(MaximumRows: 0), new(MaximumRows: 1025), new(MaximumColumns: 0), new(MaximumColumns: 4097),
            new(MaximumCells: 0), new(MaximumCells: 262_145), new(MaximumInputCharacters: 0), new(MaximumInputCharacters: 1_048_577),
            new(MaximumFrameCharacters: 31), new(MaximumFrameCharacters: 1_048_577), new(MaximumGraphemeCharacters: 0), new(MaximumGraphemeCharacters: 4097),
            new(MaximumPendingRenders: 0), new(MaximumPendingRenders: 65) })
        {
            Failure(TerminalRenderFailure.InvalidOptions, () => Layout(limits));
            Failure(TerminalRenderFailure.InvalidOptions, () => new VtRenderer(sink, limits));
        }
        var exact = Layout(new(MaximumRows: 2, MaximumColumns: 4, MaximumCells: 8, MaximumInputCharacters: 8, MaximumGraphemeCharacters: 4));
        Equal("aaaa", exact.CreateFrame("aaaaaaaa", 2, 4).Rows[1].Text);
        Failure(TerminalRenderFailure.ResourceLimit, () => exact.CreateFrame("aaaaaaaaa", 2, 4));
        foreach (var size in new[] { (0, 1), (3, 1), (1, 0), (1, 5) })
            Failure(TerminalRenderFailure.ResourceLimit, () => exact.CreateFrame("", size.Item1, size.Item2));
        Failure(TerminalRenderFailure.ResourceLimit, () => Layout(new(MaximumCells: 5)).CreateFrame("", 2, 3));
        Equal(2, Layout(new(MaximumCells: 6)).CreateFrame("", 2, 3).Rows.Length);
        Equal("a\u0301\u0302\u0303", exact.CreateFrame("a\u0301\u0302\u0303", 1, 4).Rows.Single().Text);
        Failure(TerminalRenderFailure.ResourceLimit, () => exact.CreateFrame("a\u0301\u0302\u0303\u0304", 1, 4));
        Failure(TerminalRenderFailure.ResourceLimit, () => Layout(new(MaximumFrameCharacters: 32)).CreateFrame(new string('a', 33), 1, 64));
        foreach (var cursor in new TerminalCursor[] { new(-1, 0), new(1, 0), new(0, -1), new(0, 4) })
            Failure(TerminalRenderFailure.InvalidCursor, () => exact.CreateFrame("", 1, 4, cursor));
        Failure(TerminalRenderFailure.InvalidWidth, () => new TerminalTextLayout(new ConstantWidth(-1)).CreateFrame("a", 1, 4));
        Failure(TerminalRenderFailure.InvalidWidth, () => new TerminalTextLayout(new ConstantWidth(513)).CreateFrame("a", 1, 4));
        Failure(TerminalRenderFailure.InvalidOptions, () => new TerminalTextLayout(new ConstantWidth(1, "")));
        await using var budgeted = new VtRenderer(sink, new(MaximumFrameCharacters: 32));
        await RenderFailure(TerminalRenderFailure.ResourceLimit, () => budgeted.RenderAsync(Layout().CreateFrame("abcd", 1, 4)).AsTask());
        Equal(0, sink.Attempts.Length); Equal(0L, budgeted.Snapshot.PhysicalWritesStarted); Equal(0, budgeted.Snapshot.PendingRenders);
        await using var small = new VtRenderer(sink, new(MaximumRows: 1));
        Failure(TerminalRenderFailure.ResourceLimit, () => small.RenderAsync(Layout().CreateFrame("", 2, 4)));
        await using var shortGrapheme = new VtRenderer(sink, new(MaximumGraphemeCharacters: 1));
        Failure(TerminalRenderFailure.ResourceLimit, () => shortGrapheme.RenderAsync(Layout().CreateFrame("e\u0301", 1, 4)));
    }

    private static async Task Disposal()
    {
        var sink = new PhysicalSink(); var hold = sink.HoldNext(); var renderer = new VtRenderer(sink); var frame = Layout().CreateFrame("x", 1, 4);
        var active = renderer.RenderAsync(frame).AsTask(); await hold.Entered.Task;
        var waiting = renderer.RenderAsync(frame).AsTask(); var close = renderer.DisposeAsync().AsTask();
        True(!close.IsCompleted && !active.IsCompleted && !waiting.IsCompleted); Equal(0, sink.DisposeCalls);
        hold.Complete(); await Throws<ObjectDisposedException>(() => active); await Throws<ObjectDisposedException>(() => waiting); await close;
        await renderer.DisposeAsync(); Equal(0, sink.DisposeCalls); Equal(0, sink.ActiveWrites);
        Equal(0, renderer.Snapshot.PendingRenders); True(!renderer.Snapshot.CacheKnown);
        await Throws<ObjectDisposedException>(() => renderer.RenderAsync(frame).AsTask());
    }

    private static void Failure(TerminalRenderFailure expected, Action action)
    { try { action(); } catch (TerminalRenderException error) { Equal(expected, error.Failure); return; } throw new Exception("Expected render failure."); }
    private static async Task RenderFailure(TerminalRenderFailure expected, Func<Task> action)
    { try { await action(); } catch (TerminalRenderException error) { Equal(expected, error.Failure); return; } throw new Exception("Expected render failure."); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name + "."); }
    private static Task Cancelled(Task operation, CancellationToken token) => CancelledCall(() => operation, token);
    private static async Task CancelledCall(Func<Task> action, CancellationToken token)
    { try { await action(); } catch (OperationCanceledException error) { Equal(token, error.CancellationToken); return; } throw new Exception("Expected caller cancellation."); }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}."); }
    private static void True(bool value) { if (!value) throw new Exception("Terminal renderer invariant differs."); }

    /// <summary>Exact fixture widths, intentionally neither Unicode tables nor an estimated production policy.</summary>
    private sealed class FixtureWidth : ITerminalWidthPolicy
    {
        public string Id => "authored-fixture-width-v1";
        public int GetWidth(string grapheme)
        {
            if (grapheme is "\u4e2d" or "\ud83d\ude42" or "\ud83d\udc69\u200d\ud83d\udc69\u200d\ud83d\udc67\u200d\ud83d\udc66") return 2;
            if (grapheme.All(value => value is >= '\u0300' and <= '\u036f')) return 0;
            if (grapheme[0] is >= ' ' and <= '~' && grapheme.Skip(1).All(value => value is >= '\u0300' and <= '\u036f')) return 1;
            throw new Exception("Fixture width received unqualified text.");
        }
    }
    private sealed class ConstantWidth(int width, string id = "authored-constant-width") : ITerminalWidthPolicy
    { public string Id => id; public int GetWidth(string grapheme) => width; }

    private sealed class WriteHold
    {
        public TaskCompletionSource Entered { get; } = NewGate();
        public TaskCompletionSource Release { get; } = NewGate();
        public Exception? Error { get; private set; }
        public int PrefixCharacters { get; private set; }
        public void Complete(Exception? error = null, int prefixCharacters = 0)
        { Error = error; PrefixCharacters = prefixCharacters; Release.TrySetResult(); }
    }

    /// <summary>A physical completion boundary: cancellation alone cannot release a held transport write.</summary>
    private sealed class PhysicalSink(VirtualScreen? screen = null) : IConsoleTerminal
    {
        private readonly object gate = new(); private readonly Queue<WriteHold> holds = new(); private readonly List<string> attempts = new();
        private int active, maximumActive, disposed; private long started, settled;
        public int ActiveWrites { get { lock (gate) return active; } }
        public int MaximumActiveWrites { get { lock (gate) return maximumActive; } }
        public int DisposeCalls { get { lock (gate) return disposed; } }
        public string[] Attempts { get { lock (gate) return attempts.ToArray(); } }
        public WriteHold HoldNext() { var hold = new WriteHold(); lock (gate) holds.Enqueue(hold); return hold; }
        public TerminalLeaseSnapshot Snapshot
        {
            get { lock (gate) { var state = new TerminalConsoleState(0, 0, 0, 0, 1, true, 0, 0); return new(state, state, null, false, false, 0, active, 0, 0, started, settled); } }
        }
        public ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask WriteAsync(ReadOnlyMemory<char> frame, CancellationToken token = default) => new(Write(frame.ToString(), token));
        private async Task Write(string frame, CancellationToken token)
        {
            WriteHold? hold;
            lock (gate)
            {
                if (active != 0) throw new Exception("Overlapping actual physical writes.");
                active++; started++; maximumActive = Math.Max(maximumActive, active); attempts.Add(frame);
                hold = holds.Count == 0 ? null : holds.Dequeue();
            }
            try
            {
                if (hold is not null)
                {
                    hold.Entered.TrySetResult(); await hold.Release.Task.ConfigureAwait(false);
                    if (hold.Error is not null) { if (hold.PrefixCharacters > 0) screen?.Apply(frame[..hold.PrefixCharacters]); throw hold.Error; }
                }
                token.ThrowIfCancellationRequested(); screen?.Apply(frame);
            }
            finally { lock (gate) { active--; settled++; } }
        }
        public ValueTask DisposeAsync() { lock (gate) disposed++; return ValueTask.CompletedTask; }
    }

    /// <summary>Independent ASCII VT viewport oracle for only the renderer's declared command vocabulary.</summary>
    private sealed class VirtualScreen
    {
        private readonly char[][] cells; private bool delayedWrap;
        public int Row { get; private set; }
        public int Column { get; private set; }
        public bool CursorVisible { get; private set; } = true;
        public string Text => string.Join("\n", cells.Select(value => new string(value)));
        public VirtualScreen(int rows, int columns) => cells = Enumerable.Range(0, rows).Select(_ => Enumerable.Repeat(' ', columns).ToArray()).ToArray();
        public void Apply(string frame)
        {
            for (var index = 0; index < frame.Length; index++)
            {
                if (frame[index] != '\u001b')
                {
                    if (frame[index] is < ' ' or > '~') throw new Exception("Oracle received non-ASCII data or a data control.");
                    if (delayedWrap) { Row++; Column = 0; delayedWrap = false; }
                    if (Row >= cells.Length) throw new Exception("Frame scrolled past the viewport.");
                    cells[Row][Column] = frame[index]; if (Column == cells[Row].Length - 1) delayedWrap = true; else Column++;
                    continue;
                }
                if (++index >= frame.Length || frame[index] != '[') throw new Exception("Unexpected VT introducer.");
                var start = ++index;
                while (index < frame.Length && frame[index] is not (>= '@' and <= '~')) index++;
                if (index >= frame.Length) throw new Exception("Incomplete physical command.");
                var arguments = frame[start..index]; var command = frame[index];
                if (command == 'm' && arguments == "0") continue;
                if (command == 'J' && arguments == "2") { foreach (var row in cells) Array.Fill(row, ' '); continue; }
                if (command == 'K' && arguments == "2") { Array.Fill(cells[Row], ' '); continue; }
                if (command == 'H')
                {
                    var coordinates = arguments.Split(';'); Row = int.Parse(coordinates[0]) - 1; Column = int.Parse(coordinates[1]) - 1;
                    if (Row < 0 || Row >= cells.Length || Column < 0 || Column >= cells[Row].Length) throw new Exception("Physical cursor escaped viewport.");
                    delayedWrap = false; continue;
                }
                if (arguments == "?25" && command is 'h' or 'l') { CursorVisible = command == 'h'; continue; }
                throw new Exception("Unexpected renderer command.");
            }
        }
    }
    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
