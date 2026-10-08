using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using PiSharp.Tui;
using PiSharp.Tui.Input;
using PiSharp.Tui.Rendering;

internal static class TerminalTrustedSourceFrameTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("trusted-source.sealed-map-policy-clone-and-public-record-rejection", Provenance);
        yield return ("trusted-source.exact-benign-unicode-styles-and-hidden-ime-anchor", Unicode);
        yield return ("trusted-source.control-packets-and-forged-caret-are-inert-data", Controls);
        yield return ("trusted-source.exhaustive-control-scalars-and-hostile-transcript", Transcript);
        yield return ("trusted-source.legal-scalar-seams-and-registered-atomic-suffix", Seams);
        yield return ("trusted-source.focus-crop-at-heights-one-two-three-and-transcript-origin", Tiny);
        yield return ("trusted-source.clipped-caret-preserves-draft-and-recovers-on-resize", ClipRecovery);
        yield return ("trusted-source.style-only-and-focus-only-cache-invalidation", Cache);
        yield return ("trusted-source.held-write-geometry-cancellation-epoch-and-joined-disposal", Lifetime);
        yield return ("trusted-source.input-viewport-grapheme-and-exact-encoded-bounds", Bounds);
        yield return ("trusted-source.source-scroll-preserved-after-insert", Scroll);
    }

    internal static TerminalEditorVisualMap Map(TerminalTextEditorPasteController editor, int columns = 80, int rows = 24) =>
        new TerminalEditorVisualMapBuilder().Build(editor.CaptureLayoutInput(),
            new(new(Guid.NewGuid(), 0, TerminalEditorVisualMapBuilder.SourcePolicyId), columns, rows));
    internal static TerminalEditorRenderedFrame Make(TerminalEditorVisualMap map, int scroll = 0, bool focused = true,
        ImmutableArray<string>? transcript = null, TerminalRenderLimits? limits = null) =>
        TerminalEditorFrameFactory.Create(map, scroll, transcript ?? [], focused, limits);
    internal static bool Anchor(TerminalFrame frame) => (bool)typeof(TerminalFrame).GetProperty("PositionCursor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(frame)!;
    internal static object? Style(TerminalFrame frame) => typeof(TerminalFrame).GetProperty("InverseSpan", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(frame);
    internal static string StyledRow(TerminalFrame frame, int row)
    {
        var style = Style(frame); var text = frame.Rows[row].Text;
        if (style is null || (int)style.GetType().GetProperty("Row")!.GetValue(style)! != row) return text;
        var start = (int)style.GetType().GetProperty("StartUtf16")!.GetValue(style)!;
        var length = (int)style.GetType().GetProperty("LengthUtf16")!.GetValue(style)!;
        return text[..start] + "\u001b[7m" + text.Substring(start, length) + "\u001b[0m" + text[(start + length)..];
    }
    internal static object CompareBenignPresentation(JsonElement expected, JsonElement input, TerminalEditorRenderedFrame result,
        out bool benign, out int differenceCount)
    {
        var text = expected.GetProperty("text").GetString()!;
        benign = !text.Any(c => (c < 32 && c is not ('\n' or '\t')) || c is >= '\u007f' and <= '\u009f');
        var differences = new List<object>(); differenceCount = 0;
        if (!benign) return new { boundary = "Approved inert-control expectations; unchanged source output remains the retained compatibility difference", sourceExpectedChanged = false };
        // This is a fixture-oracle interpretation only. Production never parses these strings for authority.
        var sourceRows = expected.GetProperty("normalizedComponentRows").EnumerateArray().Select(v => v.GetString()!).ToArray();
        var markerRow = Array.FindIndex(sourceRows, r => r.Contains("\u001b_pi:c\a", StringComparison.Ordinal));
        var height = input.GetProperty("terminalRows").GetInt32(); var allocated = Math.Min(height, sourceRows.Length);
        var origin = height - allocated; var offset = markerRow >= allocated ? markerRow - allocated + 1 : 0;
        var expectedRows = Enumerable.Repeat("", origin).Concat(sourceRows.Skip(offset).Take(allocated)
            .Select(r => r.Replace("\u001b_pi:c\a", ""))).ToArray();
        var actualRows = Enumerable.Range(0, result.Frame.Rows.Length).Select(r => StyledRow(result.Frame, r)).ToArray();
        EditorTests.Difference(JsonSerializer.SerializeToElement(expectedRows), JsonSerializer.SerializeToElement(actualRows), "$.styledRows", differences);
        var expectedAnchor = markerRow >= 0;
        if (Anchor(result.Frame) != expectedAnchor) differences.Add(new { path = "$.positionCursor", expected = expectedAnchor, actual = Anchor(result.Frame) });
        if (expectedAnchor)
        {
            // The unchanged actual TuiAltScreen's final CUP supplies the independent hardware anchor.
            var wire = string.Join("", expected.GetProperty("renderWrites").EnumerateArray().Select(v => v.GetString()));
            var moves = System.Text.RegularExpressions.Regex.Matches(wire, "\\x1b\\[([0-9]+);([0-9]+)H");
            var last = moves[^1]; var row = int.Parse(last.Groups[1].Value) - 1 + origin; var col = int.Parse(last.Groups[2].Value) - 1;
            if (row != result.Frame.Cursor.Row || col != result.Frame.Cursor.Column)
                differences.Add(new { path = "$.typedAnchor", expected = new { row, col }, actual = result.Frame.Cursor });
        }
        differenceCount = differences.Count;
        return new { boundary = "Unchanged source normalized component and actual TuiAltScreen focus crop/anchor, translated by explicit transcript origin", expectedRows, actualRows, differences, sourceExpectedChanged = false };
    }
    internal static object CompareIntegrationRows(JsonElement expectedRows, string sourceText, TerminalEditorRenderedFrame result,
        out int differenceCount, out bool approvedSafety)
    {
        var unsafeControls = sourceText.Any(c => (c < 32 && c is not ('\n' or '\t')) || c is >= '\u007f' and <= '\u009f');
        var source = expectedRows.EnumerateArray().Select(v => v.GetString()!).ToArray();
        var marker = Array.FindIndex(source, r => r.Contains("\u001b_pi:c\a", StringComparison.Ordinal));
        var allocated = result.Frame.Rows.Length - result.EditorRowOrigin; var offset = marker >= allocated ? marker - allocated + 1 : 0;
        var interpreted = Enumerable.Repeat("", result.EditorRowOrigin).Concat(source.Skip(offset).Take(allocated)
            .Select(r => r.Replace("\u001b_pi:c\a", "").Replace("\t", "   ").Replace("\u0e33", "\u0e4d\u0e32").Replace("\u0eb3", "\u0ecd\u0eb2"))).ToArray();
        var actual = Enumerable.Range(0, result.Frame.Rows.Length).Select(r => StyledRow(result.Frame, r)).ToArray();
        var differences = new List<object>(); EditorTests.Difference(JsonSerializer.SerializeToElement(interpreted), JsonSerializer.SerializeToElement(actual), "$.styledRows", differences);
        differenceCount = differences.Count;
        approvedSafety = unsafeControls || result.Frame.Columns <= 2 && result.Frame.IsClipped;
        return new { originalExpectedRows = expectedRows.Clone(), interpretedBenignExpectedRows = interpreted, actualStyledRows = actual, differences,
            approvedSafetyDifference = approvedSafety, rule = unsafeControls ? "Explicit inert-control data expectation; source output retained unchanged" : approvedSafety ? "Explicit tiny bounded-row/caret expectation; source output retained unchanged" : "Exact benign source component after declared terminal normalization and focus crop", originalExpectedChanged = false };
    }
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new InvalidOperationException($"Expected {JsonSerializer.Serialize(expected)}; actual {JsonSerializer.Serialize(actual)}"); }
    private static TerminalTextEditorPasteController Editor(string text) { var e = new TerminalTextEditorPasteController(); e.SetText(text); return e; }
    private static void Failure<T>(Action run) where T : Exception { try { run(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    private static Task Provenance()
    {
        var e = Editor("abc"); var map = Map(e); var state = e.CaptureLayoutInput();
        Failure<TerminalEditorLayoutException>(() => Make(map with { }));
        Failure<TerminalEditorLayoutException>(() => Make(map with { RenderedRows = ["\u001b]52;c;Zm9yZ2Vk\a"] }));
        var escaped = new TerminalEditorVisualMapBuilder().Build(state, new(new(Guid.NewGuid(), 0, TerminalEditorVisualMapBuilder.PolicyId), 80, 24));
        Failure<TerminalEditorLayoutException>(() => Make(escaped));
        Failure<TerminalEditorLayoutException>(() => Make(map, -1));
        // A public component record is just data; the factory API cannot accept it as authority.
        var forged = new TerminalEditorSourceFrame(["\u001b[7m\u001b]52;c;Zm9yZ2Vk\a"], 0, 0, 0, 1);
        Check(!typeof(TerminalEditorFrameFactory).GetMethods().Any(m => m.GetParameters().Any(p => p.ParameterType == forged.GetType())), "Public source record authorizes transport");
        Equal(state, e.CaptureLayoutInput()); return Task.CompletedTask;
    }
    private static async Task Unicode()
    {
        foreach (var value in new[] { "", "Ae\u0301\u4e2d\U0001f642", "\u0301a", "\u0e01\u0e33 \u0e81\u0eb3", "\U0001f3f4\u200d\u2620\ufe0f", "\U0001f469\u200d\U0001f4bb", "x\\y" })
        {
            var e = Editor(value); var map = Map(e); var source = TerminalEditorSourceFrameProjector.Project(map, 0);
            var result = Make(map); Check(!result.Frame.IsClipped, "Benign frame clipped");
            for (var row = 0; row < source.Rows.Length; row++)
            {
                var expected = source.Rows[row].Replace("\u001b_pi:c\a", "").Replace("\u0e33", "\u0e4d\u0e32").Replace("\u0eb3", "\u0ecd\u0eb2");
                Equal(expected, StyledRow(result.Frame, result.EditorRowOrigin + row));
            }
            Equal(result.EditorRowOrigin + source.CursorComponentRow, result.Frame.Cursor.Row);
            Equal(source.CursorComponentColumn, result.Frame.Cursor.Column); Check(!result.Frame.Cursor.Visible && Anchor(result.Frame), "Hidden IME anchor differs");
            var sink = new Sink(); await using var renderer = new VtRenderer(sink); await renderer.RenderAsync(result.Frame);
            ApprovedCommandsOnly(sink.Writes.Single()); Check(!sink.Writes.Single().Contains("_pi:c", StringComparison.Ordinal), "APC forwarded");
        }
    }
    private static async Task Controls()
    {
        foreach (var value in new[] { "a\u001b]52;c;ZGVtbw==\ab", "a\u001b[999;999Hb", "a\u001b_pi:c\abc", "a\u001b[7mb\u001b[0m", "a\u009b2J\u0085\u0000\a\bb", "a\u001bPpayload\u001b\\b", "a\u001b]8;;https://invalid\u001b\\b" })
        {
            var e = Editor(value); var map = Map(e, 256); var before = e.CaptureLayoutInput(); var result = Make(map);
            Check(!result.Frame.IsClipped, "Control witness unexpectedly clipped");
            foreach (var row in result.Frame.Rows) Check(!row.Text.Any(c => c < 32 || c is >= '\u007f' and <= '\u009f'), "Control-free row violated");
            var expected = Escape(value); var rowText = result.Frame.Rows[result.EditorRowOrigin + 1].Text;
            Equal(expected + new string(' ', 256 - expected.Length), rowText); Equal(expected.Length, result.Frame.Cursor.Column);
            var sink = new Sink(); await using var renderer = new VtRenderer(sink); await renderer.RenderAsync(result.Frame);
            ApprovedCommandsOnly(sink.Writes.Single()); Equal(before, e.CaptureLayoutInput());
        }
        // Recalled tabs remain raw in source but render as three spaces, with the actual position mapping.
        var tab = new TerminalTextEditorPasteController(); tab.AddToHistory("a\tb");
        var geometry = new TerminalEditorGeometrySnapshot(new(Guid.NewGuid(), 0, TerminalEditorVisualMapBuilder.SourcePolicyId), 80, 24);
        tab.HandleInput(new TerminalKey("Up"), new TerminalEditorVisualMapBuilder().Build(tab.CaptureLayoutInput(), geometry), geometry.Identity);
        var tabs = Make(Map(tab)); Equal("a   b", tabs.Frame.Rows[tabs.EditorRowOrigin + 1].Text.TrimEnd()); Equal(0, tabs.Frame.Cursor.Column);
        tab.SetCursor(tab.Snapshot.Text.Length); tabs = Make(Map(tab)); Equal(5, tabs.Frame.Cursor.Column);
    }
    private static async Task Seams()
    {
        foreach (var value in new[] { "Ae\u0301B", "A\U0001f469\u200d\U0001f4bbB", "A1\ufe0f\u20e3B", "A\U0001f3f4\u200d\u2620\ufe0fB" })
        {
            var e = Editor(value);
            for (var at = 0; at <= value.Length; at++)
            {
                if (at > 0 && at < value.Length && char.IsHighSurrogate(value[at - 1]) && char.IsLowSurrogate(value[at])) continue;
                e.SetCursor(at); var map = Map(e); var source = TerminalEditorSourceFrameProjector.Project(map, 0); var result = Make(map);
                Equal(source.Rows[source.CursorComponentRow].Replace("\u001b_pi:c\a", ""), StyledRow(result.Frame, result.Frame.Cursor.Row));
                Equal(source.CursorComponentColumn, result.Frame.Cursor.Column);
                var sink = new Sink(); await using var renderer = new VtRenderer(sink); await renderer.RenderAsync(result.Frame); ApprovedCommandsOnly(sink.Writes.Single());
            }
        }
        var marker = new TerminalTextEditorPasteController(); marker.HandleInput(new TerminalPaste(new string('p', 1001))); marker.SetCursor(0);
        var registered = Map(marker); Check(registered.AtomicRanges.Length == 1, "Atomic registration lost");
        var sourceMarker = TerminalEditorSourceFrameProjector.Project(registered, 0); var frame = Make(registered);
        Equal(sourceMarker.Rows[1].Replace("\u001b_pi:c\a", ""), StyledRow(frame.Frame, frame.EditorRowOrigin + 1));
        marker.SetCursor(2); registered = Map(marker); sourceMarker = TerminalEditorSourceFrameProjector.Project(registered, 0); frame = Make(registered);
        Equal(sourceMarker.Rows[1].Replace("\u001b_pi:c\a", ""), StyledRow(frame.Frame, frame.EditorRowOrigin + 1));
        // Ordinary unstyled continuation rejection is unchanged.
        Failure<TerminalRenderException>(() => new TerminalTextLayout(new Wide()).CreateFrame("\u4e2d", 1, 4, new(0, 1)));
    }
    private static async Task Transcript()
    {
        var data = new string(Enumerable.Range(0, 160).Where(c => c < 32 || c >= 127).Select(c => (char)c).ToArray());
        var editor = Editor(data); var draft = editor.CaptureLayoutInput(); var result = Make(Map(editor, 256, 24));
        var sink = new Sink(); await using var renderer = new VtRenderer(sink); await renderer.RenderAsync(result.Frame);
        ApprovedCommandsOnly(sink.Writes.Single()); Equal(draft, editor.CaptureLayoutInput());
        foreach (var value in new[] { "\u001b]52;c;ZGVtbw==\a", "\u001b_pi:c\a\u001b[7m", "\u009b999;999H", "x\\y\u4e2d\U0001f642\t\r\n" })
        {
            var rendered = Make(Map(Editor(""), 256, 8), transcript: [value]);
            var expected = string.Concat(value.EnumerateRunes().Select(r => r.Value == '\\' ? "\\\\" : r.Value is >= 32 and <= 126 ? r.ToString() :
                (r.Value <= 0xffff ? "\\u" : "\\U") + r.Value.ToString(r.Value <= 0xffff ? "x4" : "x8")));
            Equal(expected, rendered.Frame.Rows[rendered.EditorRowOrigin - 1].Text);
            var writer = new Sink(); await using var vt = new VtRenderer(writer); await vt.RenderAsync(rendered.Frame); ApprovedCommandsOnly(writer.Writes.Single());
            Equal(rendered.EditorRowOrigin + 1, rendered.Frame.Cursor.Row); Equal(0, rendered.Frame.Cursor.Column);
        }
        Failure<TerminalRenderException>(() => Make(Map(Editor("")), transcript: ["\ud800"]));
    }
    private static Task Tiny()
    {
        var e = Editor("a\nb\nc\nd\ne\nf\ng");
        foreach (var height in new[] { 1, 2, 3 })
        {
            var result = Make(Map(e, 8, height)); Equal(height, result.Frame.Rows.Length); Equal(0, result.EditorRowOrigin);
            Equal(height - 1, result.Frame.Cursor.Row); Check(Anchor(result.Frame), "Tiny caret lost");
            for (var row = 0; row < height; row++) Equal(((char)('g' - height + row + 1)).ToString(), result.Frame.Rows[row].Text.TrimEnd());
            var inactive = Make(Map(e, 8, height), focused: false); Equal("\u2500\u2500\u2500 \u2191...", inactive.Frame.Rows[0].Text); Check(!Anchor(inactive.Frame), "Unfocused anchor invented");
        }
        var placed = Make(Map(Editor("abc"), 20, 8), transcript: ["older", "current"]);
        Equal(5, placed.EditorRowOrigin); Equal("older", placed.Frame.Rows[3].Text); Equal("current", placed.Frame.Rows[4].Text); Equal(6, placed.Frame.Cursor.Row);
        return Task.CompletedTask;
    }
    private static async Task ClipRecovery()
    {
        var e = Editor("a"); var before = e.CaptureLayoutInput(); var tiny = Make(Map(e, 1, 1));
        Equal("a", tiny.Frame.Rows.Single().Text); Check(tiny.Frame.IsClipped && !Anchor(tiny.Frame) && !tiny.Frame.Cursor.Visible, "Unplaceable end caret admitted");
        var sink = new Sink(); await using var renderer = new VtRenderer(sink); await renderer.RenderAsync(tiny.Frame); ApprovedCommandsOnly(sink.Writes.Single());
        var recovered = Make(Map(e, 4, 8)); Check(Anchor(recovered.Frame) && !recovered.Frame.IsClipped, "Resize failed to recover");
        await renderer.RenderAsync(recovered.Frame); Equal(before, e.CaptureLayoutInput());
        var hostile = Editor("\u001b]52;c;ZGVtbw==\a"); var clipped = Make(Map(hostile, 8, 4));
        Check(clipped.Frame.IsClipped && !Anchor(clipped.Frame), "Escaped overflow caret not hidden"); ApprovedCommandsOnly(string.Join("", clipped.Frame.Rows.Select(r => r.Text)));
        Check(!Make(Map(hostile, 80, 8)).Frame.IsClipped, "Safe overflow failed to recover after resize");
    }
    private static async Task Cache()
    {
        var e = Editor("\u0301a"); e.SetCursor(0); var first = Make(Map(e));
        e.SetCursor(1); var second = Make(Map(e)); Check(first.Frame.Rows.SequenceEqual(second.Frame.Rows), "Style-only fixture text differs");
        Equal(first.Frame.Cursor, second.Frame.Cursor); Check(!Equals(Style(first.Frame), Style(second.Frame)), "Style-only fixture style equal");
        var sink = new Sink(); await using var renderer = new VtRenderer(sink);
        Equal(TerminalRenderKind.Full, (await renderer.RenderAsync(first.Frame)).Kind);
        Equal(TerminalRenderKind.Diff, (await renderer.RenderAsync(second.Frame)).Kind);
        Equal(TerminalRenderKind.Unchanged, (await renderer.RenderAsync(Make(Map(e)).Frame)).Kind);
        var inactive = Make(Map(e), focused: false); Equal(TerminalRenderKind.Diff, (await renderer.RenderAsync(inactive.Frame)).Kind);
        Check(!Anchor(inactive.Frame) && Equals(Style(second.Frame), Style(inactive.Frame)), "Focus metadata differs");
        renderer.Invalidate(); Equal(TerminalRenderKind.Full, (await renderer.RenderAsync(inactive.Frame)).Kind);
    }
    private static async Task Lifetime()
    {
        var e = Editor("same"); var sink = new Sink { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var renderer = new VtRenderer(sink); var frame = Make(Map(e)).Frame; using var cancel = new CancellationTokenSource();
        var active = renderer.RenderAsync(frame, cancel.Token).AsTask(); await sink.Entered.Task;
        Check(!active.IsCompleted && !renderer.Snapshot.CacheKnown, "Cache committed before actual completion");
        var resized = Make(Map(e, 40, 8)).Frame; Check(resized.Columns == 40 && !active.IsCompleted, "Pure geometry blocked by writer");
        var waiting = renderer.RenderAsync(frame).AsTask(); cancel.Cancel(); renderer.Invalidate(); var dispose = renderer.DisposeAsync().AsTask();
        Check(!active.IsCompleted && !waiting.IsCompleted && !dispose.IsCompleted, "Owned write detached");
        sink.Hold.SetResult(); await Expect<OperationCanceledException>(active); await Expect<ObjectDisposedException>(waiting); await dispose;
        Equal(0, renderer.Snapshot.PendingRenders); Equal(1L, renderer.Snapshot.PhysicalWritesSettled); Equal(0, sink.Disposals);
    }
    private static async Task Bounds()
    {
        var e = Editor("abc"); var map = Map(e, 8, 3); var frame = Make(map).Frame;
        Failure<TerminalRenderException>(() => Make(map, limits: new(MaximumRows: 2)));
        Failure<TerminalRenderException>(() => Make(map, limits: new(MaximumInputCharacters: 2)));
        Failure<TerminalRenderException>(() => Make(map, transcript: ["0123456789"], limits: new(MaximumInputCharacters: 12)));
        Failure<ArgumentException>(() => Make(map, transcript: default(ImmutableArray<string>)));
        Failure<TerminalRenderException>(() => Make(Map(Editor("a\u0301\u0302")), limits: new(MaximumGraphemeCharacters: 2)));
        Failure<TerminalRenderException>(() => Make(Map(Editor(""), 80, 257)));
        var exact = Convert.ToInt32(typeof(VtRenderer).GetMethod("FullFrameCharacterCount", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [frame]));
        Failure<TerminalRenderException>(() => Make(map, limits: new(MaximumFrameCharacters: exact - 1)));
        var admitted = Make(map, limits: new(MaximumFrameCharacters: exact)); var sink = new Sink();
        await using var renderer = new VtRenderer(sink, new(MaximumFrameCharacters: exact));
        Equal(exact, (await renderer.RenderAsync(admitted.Frame)).WrittenCharacters);
        var blocked = new Sink(); await using var shortRenderer = new VtRenderer(blocked, new(MaximumFrameCharacters: exact - 1));
        Failure<TerminalRenderException>(() => shortRenderer.RenderAsync(frame)); Equal(0, blocked.Writes.Count); Equal(0, shortRenderer.Snapshot.PendingRenders);
    }
    private static Task Scroll()
    {
        var e = Editor("0\n1\n2\n3\n4\n5\n6\n7\n8\n9"); var g = new TerminalEditorGeometrySnapshot(new(Guid.NewGuid(), 0, TerminalEditorVisualMapBuilder.SourcePolicyId), 20, 24);
        var builder = new TerminalEditorVisualMapBuilder(); var first = Make(builder.Build(e.CaptureLayoutInput(), g)); Equal(3, first.FirstVisibleSourceRow);
        for (var at = 0; at < 6; at++) e.HandleInput(new TerminalKey("Up"), builder.Build(e.CaptureLayoutInput(), g), g.Identity);
        e.InsertText("x"); var second = Make(builder.Build(e.CaptureLayoutInput(), g), first.FirstVisibleSourceRow); Equal(3, second.FirstVisibleSourceRow);
        return Task.CompletedTask;
    }
    private static async Task Expect<T>(Task task) where T : Exception { try { await task; } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static string Escape(string text) => string.Concat(text.Select(c => c < 32 || c is >= '\u007f' and <= '\u009f' ? "\\u" + ((int)c).ToString("x4") : c.ToString()));
    internal static void ApprovedCommandsOnly(string wire)
    {
        for (var at = 0; at < wire.Length; at++)
        {
            var c = wire[at]; if (c != '\u001b') { Check(c >= 32 && c is not (>= '\u007f' and <= '\u009f'), "Untrusted control on wire"); continue; }
            var end = wire.IndexOfAny(['m', 'J', 'K', 'H', 'h', 'l'], at + 1); Check(end >= 0, "Unfinished control"); var packet = wire[at..(end + 1)];
            Check(packet is "\u001b[0m" or "\u001b[7m" or "\u001b[2J" or "\u001b[2K" or "\u001b[?25h" or "\u001b[?25l" ||
                System.Text.RegularExpressions.Regex.IsMatch(packet, "^\\x1b\\[[1-9][0-9]*;[1-9][0-9]*H$"), "Unauthorized packet " + JsonSerializer.Serialize(packet)); at = end;
        }
    }
    private sealed class Wide : ITerminalWidthPolicy { public string Id => "fixture-wide"; public int GetWidth(string text) => 2; }
    internal sealed class Sink : IConsoleTerminal
    {
        internal List<string> Writes { get; } = [];
        internal TaskCompletionSource? Hold { get; init; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Disposals { get; private set; }
        public TerminalLeaseSnapshot Snapshot { get { var s = new TerminalConsoleState(0, 0, 65001, 65001, 25, true, 0, 0); return new(s, s, null, false, false, 0, 0, 0, 0, 0, 0); } }
        public ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default) => throw new InvalidOperationException("No real input allowed");
        public async ValueTask WriteAsync(ReadOnlyMemory<char> frame, CancellationToken token = default)
        { Writes.Add(frame.ToString()); Entered.TrySetResult(); if (Hold is not null) await Hold.Task; token.ThrowIfCancellationRequested(); }
        public ValueTask DisposeAsync() { Disposals++; throw new InvalidOperationException("Borrowed terminal disposed"); }
    }
}
