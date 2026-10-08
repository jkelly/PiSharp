using System.Collections.Immutable;
using PiSharp.Tui;
using PiSharp.Tui.Input;

internal static class TerminalSourceProfileTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("source-profile.one-shared-word-wrap-and-half-open-membership", WordWrap);
        yield return ("source-profile.source-last-row-controls-history-exit", HistoryRows);
        yield return ("source-profile.genuine-supplementary-cells-and-utf16-sticky", Supplementary);
        yield return ("source-profile.marker-continuations-share-navigation-render-ranges", Markers);
        yield return ("source-profile.scroll-is-immutable-frame-state-and-actual-height", Scroll);
        yield return ("source-profile.stale-policy-clone-and-width-fail-before-effects", Rejections);
        yield return ("source-profile.maximum-newline-draft-does-not-pad-full-map", Maximum);
        yield return ("source-profile.default-escaped-output-remains-separate", DefaultPreserved);
    }
    private static TerminalEditorGeometrySnapshot Geometry(int columns, int height = 24) => new(new(Guid.NewGuid(), 0, TerminalEditorVisualMapBuilder.SourcePolicyId), columns, height);
    private static TerminalEditorVisualMap Map(TerminalTextEditorPasteController e, TerminalEditorGeometrySnapshot g) => new TerminalEditorVisualMapBuilder().Build(e.CaptureLayoutInput(), g);
    private static void Check(bool yes, string message) { if (!yes) throw new InvalidOperationException(message); }
    private static void Key(TerminalTextEditorPasteController e, string key, TerminalEditorGeometrySnapshot g) => e.HandleInput(new TerminalKey(key), Map(e, g), g.Identity);
    private static void WordWrap()
    {
        var e = new TerminalTextEditorPasteController(); e.SetText("one two three four"); var g = Geometry(7); var map = Map(e, g);
        Check(map.RenderedRows.SequenceEqual(new[] { "one ", "two ", "three ", "four" }), "Source word chunks differ");
        for (var at = 0; at < map.Rows.Length; at++) Check(map.RenderedRows[at] == map.SourceText.Substring(map.Rows[at].SourceStartUtf16, map.Rows[at].LengthUtf16), "Rendered and navigable chunks differ");
        e.SetCursor(4); map = Map(e, g); Check(map.CursorRenderedRow == 1 && map.CursorRenderedColumn == 0, "Internal wrap seam belongs to following row");
        e.SetText("abcd\n\nxy\n"); map = Map(e, Geometry(5)); Check(map.Rows.Length == 4 && map.CursorRenderedRow == 3, "One row per logical empty line");
    }
    private static void HistoryRows()
    {
        var e = new TerminalTextEditorPasteController(); e.AddToHistory("one two three four"); e.SetText(""); var g = Geometry(7);
        Key(e, "Up", g); for (var at = 0; at < 3; at++) { Key(e, "Down", g); Check(e.HistoryIndex == 0, "Browse exited before source final row"); }
        Check(e.Snapshot.Text == "one two three four", "Recall was lost"); Key(e, "Down", g); Check(e.HistoryIndex == -1 && e.Snapshot.Text == "", "Final row Down restores saved draft");
    }
    private static void Supplementary()
    {
        var e = new TerminalTextEditorPasteController(); e.SetText("a\U0001f642b\n\U0001f469\u200d\U0001f4bbx\nabcdef"); var g = Geometry(12);
        var map = Map(e, g); Check(map.RenderedRows[0].Length == 4 && map.RenderedRows[1].Length == 6, "Genuine surrogate/ZWJ data lost");
        e.SetCursor(3); map = Map(e, g); Check(map.CursorRenderedColumn == 3, "Source cell width differs from UTF16 indexing");
        Key(e, "Down", g); Check(e.Snapshot.CursorUtf16Offset == 5 && e.SnappedUtf16Offset == 8, "Target ZWJ cluster snap/pre-snap differs");
        Key(e, "Down", g); Check(e.Snapshot.CursorUtf16Offset == 15, "Sticky arithmetic must preserve UTF16 offset3");
    }
    private static void Markers()
    {
        var e = new TerminalTextEditorPasteController(); e.HandleInput(new TerminalPaste(string.Join('\n', Enumerable.Repeat("p", 12)))); var g = Geometry(5); var map = Map(e, g);
        Check(map.AtomicRanges.Length == 1 && map.Rows.Length > 1 && string.Concat(map.RenderedRows) == e.Snapshot.Text, "Marker visual splitting differs");
        e.SetCursor(0); Key(e, "Down", g); Check(e.Snapshot.CursorUtf16Offset is 0 || e.Snapshot.CursorUtf16Offset == e.Snapshot.Text.Length, "Marker entered internally");
    }
    private static void Scroll()
    {
        var e = new TerminalTextEditorPasteController(); e.SetText(string.Join('\n', Enumerable.Range(0, 18).Select(i => "line" + i))); var g = Geometry(16, 20); var map = Map(e, g);
        var frame = TerminalEditorSourceFrameProjector.Project(map, 0); Check(frame.FirstVisibleSourceRow == 12 && frame.Rows.Length == 8 && frame.CursorComponentRow == 6, "Source visible scroll height differs");
        var replay = TerminalEditorSourceFrameProjector.Project(map, 0); Check(frame.Rows.SequenceEqual(replay.Rows), "Pure projector retains scroll state");
        Key(e, "PageUp", g); Check(e.Snapshot.Text[..e.Snapshot.CursorUtf16Offset].Count(c => c == '\n') == 11, "Page key uses actual height20");
        Check(TerminalEditorSourceFrameProjector.Project(Map(e, g), frame.FirstVisibleSourceRow).Rows.Length == 8, "Frame lost actual height");
    }
    private static void Rejections()
    {
        var e = new TerminalTextEditorPasteController(); e.SetText("ab\ncd"); var g = Geometry(5); var map = Map(e, g); var before = e.CaptureLayoutInput();
        try { e.HandleInput(new TerminalKey("Up"), map with { }, g.Identity); throw new InvalidOperationException("Clone admitted"); } catch (TerminalEditorLayoutException error) { Check(error.Failure == TerminalEditorLayoutFailure.InvalidMap, "Wrong clone failure"); }
        try { e.HandleInput(new TerminalKey("Up"), map, g.Identity with { PolicyId = TerminalEditorVisualMapBuilder.PolicyId }); throw new InvalidOperationException("Changed policy admitted"); } catch (TerminalEditorLayoutException error) { Check(error.Failure == TerminalEditorLayoutFailure.StaleGeometry, "Wrong stale policy failure"); }
        Check(e.CaptureLayoutInput() == before, "Rejected source map mutated state");
        e.SetText("\u754c"); before = e.CaptureLayoutInput(); try { Map(e, Geometry(1)); throw new InvalidOperationException("Overwide indivisible cluster admitted"); } catch (TerminalEditorLayoutException error) { Check(error.Failure == TerminalEditorLayoutFailure.ResourceLimit, "Wrong width bound failure"); }
        Check(e.CaptureLayoutInput() == before, "Failed pure builder mutated editor");
    }
    private static void Maximum()
    {
        var e = new TerminalTextEditorPasteController(); e.SetText(new string('\n', 65536)); var map = Map(e, Geometry(256, 1024));
        Check(map.Rows.Length == 65537 && map.RenderedRows.Sum(s => s.Length) == 0, "Full map padded logical empty lines");
        var frame = TerminalEditorSourceFrameProjector.Project(map, 0); Check(frame.Rows.Length == 309 && frame.Rows.Sum(s => s.Length) < 80000, "Bounded frame dimensions differ");
    }
    private static void DefaultPreserved()
    {
        var e = new TerminalTextEditorPasteController(); e.SetText("a\U0001f642"); var snapshot = e.Snapshot;
        var actual = TerminalTextEditorProjection.Create(snapshot, 32, 3); Check(actual.Rows.SequenceEqual(new[] { "> a\\U0001f642" }), "Production escaped output changed");
        Check(TerminalEditorVisualMapBuilder.SourcePolicyId != TerminalEditorVisualMapBuilder.PolicyId, "Experimental profile lacks separate identity");
    }
}
