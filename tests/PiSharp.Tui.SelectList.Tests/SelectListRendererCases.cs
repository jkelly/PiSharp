using PiSharp.Tui;
using PiSharp.Tui.Components.SelectList;
using PiSharp.Tui.Input;
using PiSharp.Tui.Rendering;
using static SelectListCases;

internal static class SelectListRendererCases
{
    internal static IEnumerable<SelectListCase> Cases()
    {
        yield return new("input-frame-vt-callback-pipeline", "Decoded input through real component, frame and borrowed VtRenderer", Pipeline);
        yield return new("resize-and-host-cache-invalidation", "New dimensions and same-size host invalidation force full redraw", Resize);
        yield return new("short-window-crop-pointer-translation", "Selected item remains visible and frame Y maps back through crop", Crop);
        yield return new("unicode-and-control-safe-frames", "Canonical data preserved; controls inert, graphemes whole and native style private", SafeFrames);
        yield return new("unfocused-frame-selection-style", "Host focus controls trusted inverse styling while component selection stays intact", Unfocused);
        yield return new("frame-hard-resource-boundaries", "Viewport/input/frame/grapheme rejection occurs before terminal I/O", Bounds);
        yield return new("held-write-disposal-original-join", "Disposal retains original held write and borrowed terminal ownership", HeldWrite);
        yield return new("write-fault-and-full-recovery", "Physical fault leaves cache unknown; recovery is full and borrowed terminal survives", Fault);
        yield return new("ansi-regression-safe-frame-separation", "Reviewed canonical RI truncation versus approved inert native display, both preserving canonical item data", AnsiSafeSeparation);
    }

    private static async Task Pipeline(ConsumerEvidence e)
    {
        var list = List(2, 2); var decoder = new TerminalInputDecoder(); var selected = ""; var cancelled = 0;
        list.OnSelect = item => selected = item.Value; list.OnCancel = () => cancelled++;
        var down = decoder.Feed("\u001b[B").Single(); list.HandleInput(down);
        var rendered = TerminalSelectListFrameFactory.Create(list, 4, 12);
        Rows(["  Item 0", "\u2192 Item 1"], rendered.Frame.Rows.Select(row => row.Text));
        var terminal = new BorrowedSink(); var renderer = new VtRenderer(terminal);
        try
        {
            var first = await renderer.RenderAsync(rendered.Frame); Equal(TerminalRenderKind.Full, first.Kind); True(first.CacheCommitted);
            True(terminal.Attempts.Single().Contains("\u001b[7m\u2192 Item 1\u001b[0m", StringComparison.Ordinal));
            var equal = await renderer.RenderAsync(TerminalSelectListFrameFactory.Create(list, 4, 12).Frame);
            Equal(TerminalRenderKind.Unchanged, equal.Kind); Equal(1, terminal.Attempts.Count);
            list.HandleInput(decoder.Feed("\r").Single()); Equal("item1", selected);
            list.HandleInput(new TerminalKey("escape")); Equal(1, cancelled);
            e.Observe("actual-pipeline", new { down, selected, cancelled, first, equal, terminal.Attempts, snapshot = renderer.Snapshot });
        }
        finally { await renderer.DisposeAsync(); }
        Equal(0, terminal.Disposals); Equal(0, terminal.ActiveWrites); Equal(terminal.Started, terminal.Settled);
    }
    private static async Task Resize(ConsumerEvidence e)
    {
        var list = List(2, 2); var terminal = new BorrowedSink(); var renderer = new VtRenderer(terminal);
        try
        {
            Equal(TerminalRenderKind.Full, (await renderer.RenderAsync(TerminalSelectListFrameFactory.Create(list, 3, 12).Frame)).Kind);
            list.Invalidate(); Equal(TerminalRenderKind.Unchanged, (await renderer.RenderAsync(TerminalSelectListFrameFactory.Create(list, 3, 12).Frame)).Kind);
            Equal(TerminalRenderKind.Full, (await renderer.RenderAsync(TerminalSelectListFrameFactory.Create(list, 3, 8).Frame)).Kind);
            renderer.Invalidate(); Equal(TerminalRenderKind.Full, (await renderer.RenderAsync(TerminalSelectListFrameFactory.Create(list, 3, 8).Frame)).Kind);
            list.HandleInput("\u001b[B"); Equal(TerminalRenderKind.Diff, (await renderer.RenderAsync(TerminalSelectListFrameFactory.Create(list, 3, 8).Frame)).Kind);
            e.Observe("actual-resize-vt-writes", new { terminal.Attempts, snapshot = renderer.Snapshot });
        }
        finally { await renderer.DisposeAsync(); }
        Equal(0, terminal.Disposals);
    }
    private static Task Crop(ConsumerEvidence e)
    {
        var list = List(12, 5); list.SetSelectedIndex(8); var submitted = ""; list.OnSelect = item => submitted = item.Value;
        var frame = TerminalSelectListFrameFactory.Create(list, 1, 20);
        Equal(2, frame.ComponentRowOffset); Equal(new(6, 11), frame.SourceVisibleRange); True(frame.Frame.IsClipped);
        Rows(["\u2192 Item 8"], frame.Frame.Rows.Select(row => row.Text));
        var local = new TerminalSelectListPointer(TerminalSelectListPointerType.Click, 0, 0, TerminalSelectListPointerButton.Left);
        var pointer = frame.ToComponentPointer(local); True(pointer is { Y: 2 }); list.HandleMouse(pointer!); Equal("item8", submitted);
        Equal<TerminalSelectListPointer?>(null, frame.ToComponentPointer(local with { Y = -1 }));
        Equal<TerminalSelectListPointer?>(null, frame.ToComponentPointer(local with { Y = 1 }));
        var empty = List(0); var tiny = TerminalSelectListFrameFactory.Create(empty, 1, 1);
        Rows([" "], tiny.Frame.Rows.Select(row => row.Text)); True(tiny.Frame.IsClipped);
        Rows(["  No matching commands"], empty.Render(1));
        var selected = TerminalSelectListFrameFactory.Create(List(1, 1), 1, 1); Rows(["\u2192"], selected.Frame.Rows.Select(row => row.Text));
        True(selected.Frame.IsClipped); e.Observe("actual-cropped-frame-and-pointer", new { frame, pointer, submitted, tiny, selected });
        return Task.CompletedTask;
    }
    private static async Task SafeFrames(ConsumerEvidence e)
    {
        const string original = "\u732be\u0301\U0001f469\u200d\U0001f4bb";
        const string malicious = "\u001b]52;c;private\a\u001b[2J\r\n\u009bA";
        var list = new TerminalSelectList([new("unicode", original), new("controls", malicious)], 2, Bindings());
        var frame = TerminalSelectListFrameFactory.Create(list, 3, 80);
        Equal(original, list.FilteredItems[0].Label); Equal(malicious, list.FilteredItems[1].Label);
        Rows(["\u2192 " + original, "  \\u001b]52;c;private\\u0007\\u001b[2J\\u000d\\u000a\\u009bA"], frame.Frame.Rows.Select(row => row.Text));
        True(frame.Frame.Rows.All(row => row.Text.All(c => c >= 0x20 && c is not (>= '\u007f' and <= '\u009f'))));
        True(frame.Frame.Rows.All(row => row.CellWidth <= frame.Frame.Columns));
        var shortList = new TerminalSelectList([new("u", "e\u0301\U0001f469\u200d\U0001f4bbz")], 1, Bindings());
        var shortFrame = TerminalSelectListFrameFactory.Create(shortList, 1, 7);
        Rows(["\u2192 e\u0301\U0001f469\u200d\U0001f4bb"], shortFrame.Frame.Rows.Select(row => row.Text));
        Equal(5, shortFrame.Frame.Rows[0].CellWidth);
        var theme = new TerminalSelectListTheme { SelectedText = text => "\u001b[31m" + text + "\u001b[0m" };
        var styled = List(1, 1, theme: theme); var styleFrame = TerminalSelectListFrameFactory.Create(styled, 1, 80);
        True(styleFrame.Frame.Rows[0].Text.StartsWith("\\u001b[31m\u2192 ", StringComparison.Ordinal));
        var terminal = new BorrowedSink(); var renderer = new VtRenderer(terminal);
        try
        {
            await renderer.RenderAsync(frame.Frame);
            True(!terminal.Attempts.Single().Contains("\u001b]52;", StringComparison.Ordinal));
            True(!terminal.Attempts.Single().Contains(malicious, StringComparison.Ordinal));
            e.Observe("actual-safe-unicode-frame-and-vt", new { frame, shortFrame, styleFrame, terminal.Attempts });
        }
        finally { await renderer.DisposeAsync(); }
        Equal(0, terminal.Disposals);
    }
    private static Task Bounds(ConsumerEvidence e)
    {
        var list = List(1, 1);
        Equal(TerminalRenderFailure.ResourceLimit, Throws<TerminalRenderException>(() => TerminalSelectListFrameFactory.Create(list, 0, 4)).Failure);
        Equal(TerminalRenderFailure.ResourceLimit, Throws<TerminalRenderException>(() => TerminalSelectListFrameFactory.Create(list, 1, 513)).Failure);
        Equal(TerminalRenderFailure.ResourceLimit, Throws<TerminalRenderException>(() =>
            TerminalSelectListFrameFactory.Create(list, 1, 24, limits: new(MaximumInputCharacters: 1))).Failure);
        var marks = new TerminalSelectList([new("a", "e" + new string('\u0301', 300))], 1, Bindings());
        Equal(TerminalRenderFailure.ResourceLimit, Throws<TerminalRenderException>(() => TerminalSelectListFrameFactory.Create(marks, 1, 24)).Failure);
        var output = new TerminalSelectList([new("a", new string('a', 32))], 1, Bindings());
        Equal(TerminalRenderFailure.ResourceLimit, Throws<TerminalRenderException>(() =>
            TerminalSelectListFrameFactory.Create(output, 1, 40, limits: new(MaximumFrameCharacters: 32))).Failure);
        Observe(e, list); return Task.CompletedTask;
    }
    private static async Task Unfocused(ConsumerEvidence e)
    {
        var list = List(1, 1); var terminal = new BorrowedSink(); var renderer = new VtRenderer(terminal);
        try
        {
            var focused = TerminalSelectListFrameFactory.Create(list, 1, 20);
            var unfocused = TerminalSelectListFrameFactory.Create(list, 1, 20, focused: false);
            Rows(focused.Frame.Rows.Select(row => row.Text).ToArray(), unfocused.Frame.Rows.Select(row => row.Text));
            await renderer.RenderAsync(focused.Frame); True(terminal.Attempts[0].Contains("\u001b[7m", StringComparison.Ordinal));
            Equal(TerminalRenderKind.Diff, (await renderer.RenderAsync(unfocused.Frame)).Kind);
            True(!terminal.Attempts[1].Contains("\u001b[7m", StringComparison.Ordinal)); Equal("item0", list.GetSelectedItem()!.Value);
            e.Observe("actual-host-focus-style-transition", new { focused, unfocused, terminal.Attempts });
        }
        finally { await renderer.DisposeAsync(); }
        Equal(0, terminal.Disposals);
    }
    private static async Task HeldWrite(ConsumerEvidence e)
    {
        var list = List(1, 1); var terminal = new BorrowedSink { HoldNext = true }; var renderer = new VtRenderer(terminal);
        var original = renderer.RenderAsync(TerminalSelectListFrameFactory.Create(list, 1, 24).Frame).AsTask(); Task? disposal = null;
        ObjectDisposedException? shutdownFailure = null;
        try
        {
            await terminal.Entered.Task; disposal = renderer.DisposeAsync().AsTask();
            e.Observe("held-original-write-before-release", new { original.IsCompleted, disposalComplete = disposal.IsCompleted,
                terminal.ActiveWrites, terminal.Started, terminal.Settled, snapshot = renderer.Snapshot });
            True(!original.IsCompleted); True(!disposal.IsCompleted); Equal(1, terminal.ActiveWrites); Equal(0, terminal.Disposals);
        }
        finally
        {
            // A diagnostic failure cannot detach the original write or disposal. The fake hold is always released.
            terminal.Release.TrySetResult();
            try { await original; } catch (ObjectDisposedException error) { shutdownFailure = error; }
            finally { await (disposal ?? renderer.DisposeAsync().AsTask()); }
        }
        True(shutdownFailure?.GetType() == typeof(ObjectDisposedException)); Equal(nameof(VtRenderer), shutdownFailure?.ObjectName);
        True(original.IsCompleted); Equal(0, terminal.ActiveWrites); Equal(1L, terminal.Started); Equal(terminal.Started, terminal.Settled);
        Equal(0, terminal.Disposals); e.Observe("joined-original-write-and-disposal", new { original.Status, terminal.Started,
            terminal.Settled, terminal.Disposals, failure = shutdownFailure?.GetType().FullName, snapshot = renderer.Snapshot });
    }
    private static async Task Fault(ConsumerEvidence e)
    {
        var terminal = new BorrowedSink { FaultNext = true }; var renderer = new VtRenderer(terminal);
        var frame = TerminalSelectListFrameFactory.Create(List(1, 1), 1, 24).Frame;
        try
        {
            IOException? failure = null; try { await renderer.RenderAsync(frame); } catch (IOException error) { failure = error; }
            True(failure is { Message: "offline physical write marker" }); True(!renderer.Snapshot.CacheKnown);
            Equal(1L, terminal.Started); Equal(1L, terminal.Settled);
            var recovered = await renderer.RenderAsync(frame); Equal(TerminalRenderKind.Full, recovered.Kind); True(recovered.CacheCommitted);
            e.Observe("actual-write-fault-and-recovery", new { error = failure is null ? null : ConsumerException.From(failure), recovered,
                terminal.Attempts, snapshot = renderer.Snapshot });
        }
        finally { await renderer.DisposeAsync(); }
        Equal(0, terminal.Disposals); Equal(0, terminal.ActiveWrites);
    }

    private static async Task AnsiSafeSeparation(ConsumerEvidence e)
    {
        const string first = "\U0001f1fa"; const string second = "\U0001f1f8"; const string reset = "\u001b[0m";
        const string original = first + reset + second;
        var component = new TerminalSelectList([new("flag", original)], 1, Bindings());
        var canonical = component.Render(6); var small = TerminalSelectListFrameFactory.Create(component, 1, 6);
        var wide = TerminalSelectListFrameFactory.Create(component, 1, 20);
        e.Observe("canonical-and-inert-native-boundaries", new { original, canonical, small, wide,
            expectationProvenance = "Authored Source expectation versus separately approved native controls-inert projection" });
        Rows(["\u2192 " + first + reset], canonical);
        Rows(["\u2192 " + first], small.Frame.Rows.Select(row => row.Text));
        Rows(["\u2192 " + first + "\\u001b[0m" + second], wide.Frame.Rows.Select(row => row.Text));
        Equal(4, small.Frame.Rows[0].CellWidth); Equal(15, wide.Frame.Rows[0].CellWidth);
        Equal(original, component.FilteredItems[0].Label);
        True(small.Frame.Rows.Concat(wide.Frame.Rows).All(row => row.Text.All(c => c >= 0x20 && c is not (>= '\u007f' and <= '\u009f'))));
        var terminal = new BorrowedSink(); var renderer = new VtRenderer(terminal);
        try
        {
            await renderer.RenderAsync(wide.Frame);
            // Only TUI-owned inverse/reset/cursor/position controls enter VT output; source ANSI is literal data.
            True(terminal.Attempts.Single().Contains(first + "\\u001b[0m" + second, StringComparison.Ordinal));
            True(!terminal.Attempts.Single().Contains(original, StringComparison.Ordinal));
            e.Observe("actual-inert-regional-indicator-vt-write", new { terminal.Attempts, snapshot = renderer.Snapshot });
        }
        finally { await renderer.DisposeAsync(); }
        Equal(0, terminal.Disposals); Equal(terminal.Started, terminal.Settled);
    }

    private sealed class BorrowedSink : IConsoleTerminal
    {
        internal readonly List<string> Attempts = [];
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool HoldNext, FaultNext; internal int Disposals, ActiveWrites; internal long Started, Settled;
        private static readonly TerminalConsoleState State = new(0, 0, 65001, 65001, 25, true, 0, 0);
        public TerminalLeaseSnapshot Snapshot => new(State, State, null, false, false, 0, ActiveWrites, 0, 0, Started, Settled);
        public ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default) =>
            throw new InvalidOperationException("Selector renderer must not acquire input");
        public async ValueTask WriteAsync(ReadOnlyMemory<char> frame, CancellationToken token = default)
        {
            Attempts.Add(frame.ToString()); Started++; ActiveWrites++;
            try
            {
                if (FaultNext) { FaultNext = false; throw new IOException("offline physical write marker"); }
                if (HoldNext) { HoldNext = false; Entered.TrySetResult(); await Release.Task; }
            }
            finally { ActiveWrites--; Settled++; }
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
