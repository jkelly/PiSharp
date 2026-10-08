using System.Collections.Immutable;
using System.Reflection;
using System.Threading.Channels;
using PiSharp.Cli.Interactive;
using PiSharp.Tui;
using PiSharp.Tui.Input;
using PiSharp.Tui.Rendering;

internal static class TerminalDefaultQualificationTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("actual-default.overwide-columns-one-two-preserve-and-recover-on-resize", Overwide);
        yield return ("actual-default.reset-stamp-identical-text-scroll-insert-history-and-lifetime", Reset);
        yield return ("actual-default.safe-map-control-pictures-before-wrap-and-navigation", Mapping);
        yield return ("actual-default.reset-stamp-overflow-rejection-and-observer-commit", AtomicReset);
        yield return ("actual-default.maximum-supported-draft-with-owned-header", Maximum);
        yield return ("actual-default.decoded-overwide-navigation-is-atomic-and-resize-recovers", DecodedOverwide);
        yield return ("actual-default.joined-wire-grammar-rejects-control-injections-and-packet-seams", Grammar);
        yield return ("actual-default.transcript-projection-preserves-existing-escaped-display", Transcript);
    }
    internal static void Check(bool valid, string reason) { if (!valid) throw new InvalidOperationException(reason); }
    private static bool Same(TerminalEditorLayoutInput a, TerminalEditorLayoutInput b) => a.Snapshot == b.Snapshot && a.Identity == b.Identity &&
        a.ScrollResetRevision == b.ScrollResetRevision && a.AtomicRanges.SequenceEqual(b.AtomicRanges);
    private static TerminalDraftSnapshot Capture(TerminalTextEditorPasteController e) => new(e.Snapshot.Text, e.Snapshot.CursorUtf16Offset) { Layout = e.CaptureLayoutInput() };
    private static TerminalEditorVisualMap Build(TerminalTextEditorPasteController e, int columns) => new TerminalEditorVisualMapBuilder().BuildForTerminal(e.CaptureLayoutInput(), new(new(Guid.NewGuid(), 0, TerminalEditorVisualMapBuilder.SourcePolicyId), columns, 24));
    private static async Task Overwide()
    {
        foreach (var columns in new[] { 1, 2 })
        {
            var sink = new ConsoleSink(columns, 3); var observed = new List<(TerminalEditorVisualMap Map, TerminalEditorRenderedFrame Frame)>();
            await using var view = new TerminalSessionView(sink, sink, (m, f) => observed.Add((m, f)));
            await view.StartAsync(default); var editor = new TerminalTextEditorPasteController(); editor.HandleInput(new TerminalPaste(string.Join('\n', Enumerable.Repeat("p", 12))));
            editor.InsertText("\u4e2d"); var input = editor.CaptureLayoutInput(); var expanded = editor.GetExpandedText(); var registry = editor.RegisteredPasteCount;
            await view.SetDraftAsync(Capture(editor), default);
            Check(observed.Count == 0 && sink.Writes.Count == 2 && sink.Writes[1].Contains("\u001b[?25l"), "Overwide source did not enter the bounded owned fallback");
            Check(Same(input, editor.CaptureLayoutInput()) && expanded == editor.GetExpandedText() && registry == editor.RegisteredPasteCount, "Fallback changed draft/paste/identity");
            sink.Viewport = new(40, 8, 0, 0, 40, 8); await view.RefreshViewportAsync(default);
            Check(observed.Count == 1 && observed[0].Map.SourceText == input.Snapshot.Text && observed[0].Map.EditorIdentity == input.Identity && observed[0].Map.AtomicRanges.SequenceEqual(input.AtomicRanges), "Resize did not recover the same captured draft");
            Check(observed[0].Frame.SourceComponent.Rows.Any(r => r.Contains('\u4e2d')), "Recovered real default frame lost the wide glyph");
            Check(Same(input, editor.CaptureLayoutInput()), "Resize mutated controller"); foreach (var wire in sink.Writes.Skip(1)) WireGrammar.Check(wire, 40, 8);
        }
    }
    private static async Task Reset()
    {
        var sink = new ConsoleSink(20, 24); var frames = new List<TerminalEditorRenderedFrame>();
        await using var view = new TerminalSessionView(sink, sink, (_, f) => frames.Add(f)); await view.StartAsync(default);
        var e = new TerminalTextEditorPasteController(); e.SetText("0\n1\n2\n3\n4\n5\n6\n7\n8\n9"); await view.SetDraftAsync(Capture(e), default);
        Check(frames[^1].FirstVisibleSourceRow == 3, "Initial source scroll differs");
        for (var at = 0; at < 6; at++) { var g = view.CaptureEditorGeometry(); e.HandleInput(new TerminalKey("Up"), new TerminalEditorVisualMapBuilder().BuildForTerminal(e.CaptureLayoutInput(), g), g.Identity); }
        var stamp = e.ScrollResetRevision; e.InsertText("x"); await view.SetDraftAsync(Capture(e), default);
        Check(e.ScrollResetRevision == stamp && frames[^1].FirstVisibleSourceRow == 3, "Insertion reset source scroll");
        var identical = e.Snapshot.Text; e.SetText(identical); Check(e.ScrollResetRevision == stamp + 1, "Identical SetText did not reset stamp");
        e.SetCursor(0); await view.SetDraftAsync(Capture(e), default); Check(frames[^1].FirstVisibleSourceRow == 0, "Identical reset stamp did not reset view scroll");
        e.AddToHistory("history"); var beforeHistory = e.ScrollResetRevision; var g2 = view.CaptureEditorGeometry();
        e.HandleInput(new TerminalKey("Up"), new TerminalEditorVisualMapBuilder().BuildForTerminal(e.CaptureLayoutInput(), g2), g2.Identity);
        Check(e.ScrollResetRevision == beforeHistory + 1 && e.HistoryIndex == 0, "History recall stamp differs");
        e.HandleInput(new TerminalKey("Down"), new TerminalEditorVisualMapBuilder().BuildForTerminal(e.CaptureLayoutInput(), g2), g2.Identity);
        Check(e.ScrollResetRevision == beforeHistory + 2 && e.HistoryIndex == -1, "Saved draft restoration stamp differs");
        var sibling = new TerminalTextEditorPasteController(); sibling.SetText(identical); sibling.SetCursor(0); await view.SetDraftAsync(Capture(sibling), default);
        Check(frames[^1].FirstVisibleSourceRow == 0, "New editor lifetime retained old scroll");
    }
    private static Task Mapping()
    {
        var e = new TerminalTextEditorPasteController(); const string text = "a\u001b]52;c;x\ab\u0085\u007f\u009b2J"; e.SetText(text);
        var map = Build(e, 8); Check(map.SourceText == text && map.RenderedRows.All(r => !r.Any(c => c < 32 || c is >= '\u007f' and <= '\u009f')), "Safe builder lost canonical provenance or retained unsafe control");
        var display = string.Concat(map.RenderedRows); Check(display == "a\u241b]52;c;x\u2407b\u2426\u2421\u24262J", "R3 control-picture mapping differs");
        foreach (var row in map.Rows) Check(map.RenderedRows[row.RenderedRowIndex].Length == row.LengthUtf16, "Display/source offset length differs");
        e.SetCursor(1); var g = new TerminalEditorGeometrySnapshot(new(Guid.NewGuid(), 0, TerminalEditorVisualMapBuilder.SourcePolicyId), 8, 24);
        var safe = new TerminalEditorVisualMapBuilder().BuildForTerminal(e.CaptureLayoutInput(), g); e.HandleInput(new TerminalKey("Down"), safe, g.Identity);
        Check(e.Snapshot.Text == text && e.Snapshot.CursorUtf16Offset >= 1, "Safe navigation changed stored control text");
        var raw = new TerminalEditorVisualMapBuilder().Build(e.CaptureLayoutInput(), g);
        try { TerminalEditorFrameFactory.Create(raw, 0, []); throw new InvalidOperationException("Raw unsafe map authorized factory"); } catch (TerminalEditorLayoutException ex) { Check(ex.Failure == TerminalEditorLayoutFailure.InvalidInput, "Wrong raw rejection"); }
        return Task.CompletedTask;
    }
    private static Task AtomicReset()
    {
        var e = new TerminalTextEditorPasteController(); e.SetText("seed"); var field = typeof(TerminalTextEditorPasteController).GetField("scrollResetRevision", BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(e, long.MaxValue); var before = e.CaptureLayoutInput();
        try { e.SetText("other"); throw new InvalidOperationException("Reset stamp overflow admitted"); } catch (TerminalTextEditorException ex) { Check(ex.Failure == TerminalTextEditorFailure.ResourceLimit, "Wrong overflow failure"); }
        Check(e.CaptureLayoutInput() == before, "Reset overflow partially committed");
        field.SetValue(e, 1L); e.TextChanged += _ => { Check(e.ScrollResetRevision == 2 && e.CaptureLayoutInput().ScrollResetRevision == 2, "Observer saw uncommitted reset"); throw new IOException("observer"); };
        try { e.SetText("other"); } catch (IOException) { }
        Check(e.Snapshot.Text == "other" && e.ScrollResetRevision == 2, "Observer failure rolled back reset"); return Task.CompletedTask;
    }
    private static async Task Maximum()
    {
        var sink = new ConsoleSink(80, 24); TerminalEditorVisualMap? map = null;
        await using var view = new TerminalSessionView(sink, sink, (m, _) => map = m); await view.StartAsync(default);
        var e = new TerminalTextEditorPasteController(); e.SetText(new string('a', 65536)); var before = e.CaptureLayoutInput();
        await view.SetDraftAsync(Capture(e), default); Check(map?.SourceText.Length == 65536 && before == e.CaptureLayoutInput(), "Maximum canonical draft was rejected by transcript/header accounting");
    }
    private static async Task Transcript()
    {
        var sink = new ConsoleSink(80, 24); TerminalEditorRenderedFrame? frame = null;
        await using var view = new TerminalSessionView(sink, sink, (_, f) => frame = f); await view.StartAsync(default);
        var e = new TerminalTextEditorPasteController(); await view.SetDraftAsync(Capture(e), default);
        await view.PresentAsync("path\\name \u4e2d\n", default);
        const string expected = "path\\\\name \\u4e2d";
        Check(frame!.Frame.Rows.Take(frame.EditorRowOrigin).Any(r => r.Text == expected),
            "Existing escaped transcript was escaped again or clipped by the source factory. Expected " + System.Text.Json.JsonSerializer.Serialize(expected) +
            "; actual transcript rows " + System.Text.Json.JsonSerializer.Serialize(frame.Frame.Rows.Take(frame.EditorRowOrigin).Select(r => r.Text)));
    }
    private static Task Grammar()
    {
        WireGrammar.Check(string.Concat(new[] { "\u001b[", "1;1H", "safe", "\u001b[?25", "l" }), 8, 3);
        foreach (var attack in new[] { "\u001b]52;c;x\a", "\u001bPfake\u001b\\", "\u001b_pi:c\u001b\\", "\u009b2J", "\u001b[999;999H", "\u001b[7", "\u001b[7m\u0001" })
        {
            try { WireGrammar.Check(attack, 8, 3); throw new InvalidOperationException("Grammar accepted injected or unfinished command"); }
            catch (InvalidOperationException ex) when (ex.Message != "Grammar accepted injected or unfinished command") { }
        }
        return Task.CompletedTask;
    }
    private static async Task DecodedOverwide()
    {
        foreach (var columns in new[] { 1, 2 })
        {
            var sink = new InputSink(columns); var captures = Channel.CreateBounded<TerminalDraftSnapshot>(8); var receipts = new TerminalSubmissionReceipts();
            TerminalEditorRenderedFrame? frame = null; var view = new TerminalSessionView(sink, sink, (_, f) => frame = f);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12)); Task<TerminalInputExit>? run = null; var ended = 0;
            try
            {
                await view.StartAsync(stop.Token);
                run = new TerminalChatInput(sink).RunAcknowledgedAsync((_, _) => throw new InvalidOperationException("Unexpected submit"),
                    _ => Task.CompletedTask, async (capture, token) => { await view.SetDraftAsync(capture, token); Check(captures.Writer.TryWrite(capture), "Capture bound"); },
                    receipts, (_, _) => throw new InvalidOperationException("Unexpected acknowledgment"), _ => Interlocked.Increment(ref ended), stop.Cancel, stop.Token, view.CaptureEditorGeometry);
                var initial = await captures.Reader.ReadAsync(stop.Token);
                await sink.Feed("\u001b[200~" + string.Join('\n', Enumerable.Repeat("p", 12)) + "\u001b[201~"); var paste = await captures.Reader.ReadAsync(stop.Token);
                await sink.Feed("\u4e2d"); var wide = await captures.Reader.ReadAsync(stop.Token);
                Check(wide.Layout!.AtomicRanges.Length == 1 && wide.Text.Contains('\u4e2d'), "Actual decoder lost registered paste/wide text");
                await sink.Feed("\u001b[A"); var rejected = await captures.Reader.ReadAsync(stop.Token);
                Check(Same(wide.Layout!, rejected.Layout!), "Overwide decoded navigation partially changed controller");
                sink.Viewport = new(40, 8, 0, 0, 40, 8); await view.RefreshViewportAsync(stop.Token);
                Check(frame!.SourceComponent.Rows.Any(r => r.Contains('\u4e2d')), "Actual resize did not restore captured editor");
                await sink.Feed("x"); var recovered = await captures.Reader.ReadAsync(stop.Token);
                Check(recovered.Text == wide.Text + "x" && recovered.Layout!.Identity.EditorLifetimeId == initial.Layout!.Identity.EditorLifetimeId &&
                    recovered.Layout.ScrollResetRevision == wide.Layout!.ScrollResetRevision && recovered.Layout.AtomicRanges.SequenceEqual(wide.Layout.AtomicRanges), "Resize recovery changed controller, paste or reset state");
                receipts.Complete(); sink.Complete(); await run; await view.DisposeAsync(); sink.AssertJoined(); Check(ended == 1, "Input-ended ownership differs");
                WireGrammar.Check(string.Concat(sink.Writes), 40, 8);
            }
            finally { receipts.Complete(); stop.Cancel(); sink.Complete(); if (run is not null) { try { await run; } catch (OperationCanceledException) { } } await view.DisposeAsync(); sink.AssertJoined(); }
        }
    }
    private sealed class InputSink(int columns) : IConsoleTerminal, ITerminalViewportSource
    {
        private readonly Channel<string> input = Channel.CreateBounded<string>(2); private int reads; private long started, settled;
        internal readonly List<string> Writes = []; internal TerminalViewport Viewport = new(columns, 3, 0, 0, columns, 3);
        internal ValueTask Feed(string text) => input.Writer.WriteAsync(text); internal void Complete() => input.Writer.TryComplete();
        public TerminalViewport ReadViewport() => Viewport;
        public TerminalLeaseSnapshot Snapshot { get { var s = new TerminalConsoleState(0, 0, 65001, 65001, 25, true, 0, 0); return new(s, s, null, false, false, reads, 0, started, settled, Writes.Count, Writes.Count); } }
        public async ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default)
        {
            Check(Interlocked.Increment(ref reads) == 1, "Concurrent reads"); Interlocked.Increment(ref started);
            try { if (!await input.Reader.WaitToReadAsync(token)) return 0; Check(input.Reader.TryRead(out var value) && value.Length <= destination.Length, "Chunk bound"); var chunk = value ?? throw new IOException("Missing chunk"); chunk.AsMemory().CopyTo(destination); return chunk.Length; }
            finally { Interlocked.Decrement(ref reads); Interlocked.Increment(ref settled); }
        }
        public ValueTask WriteAsync(ReadOnlyMemory<char> value, CancellationToken token = default) { token.ThrowIfCancellationRequested(); Writes.Add(value.ToString()); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => throw new InvalidOperationException("Borrowed console disposed");
        internal void AssertJoined() => Check(reads == 0 && started == settled, "Reader remained active");
    }
    internal sealed class ConsoleSink(int columns, int rows) : IConsoleTerminal, ITerminalViewportSource
    {
        internal List<string> Writes { get; } = [];
        internal TerminalViewport Viewport = new(columns, rows, 0, 0, columns, rows);
        public TerminalViewport ReadViewport() => Viewport;
        public TerminalLeaseSnapshot Snapshot { get { var s = new TerminalConsoleState(0, 0, 65001, 65001, 25, true, 0, 0); return new(s, s, null, false, false, 0, 0, 0, 0, 0, 0); } }
        public ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default) => ValueTask.FromResult(0);
        public ValueTask WriteAsync(ReadOnlyMemory<char> frame, CancellationToken token = default) { token.ThrowIfCancellationRequested(); Writes.Add(frame.ToString()); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => throw new InvalidOperationException("Borrowed console disposed");
    }
}
