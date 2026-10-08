using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using PiSharp.Tui.Input;
using PiSharp.Tui.Rendering;

internal static class IndicGraphemeControls
{
    private static readonly Func<string, int[]> Offsets = typeof(TerminalInputDecoder).Assembly
        .GetType("PiSharp.Tui.Input.TerminalSourceGraphemeSegmenter", true)!
        .GetMethod("GetOffsets", BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate<Func<string, int[]>>();
    internal sealed record Result(int Failed, int ProbeCount, List<object> Probes, List<object> Controls);
    internal static Result Run()
    {
        using var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "indic-source-grapheme-probes.json")));
        var probes = new List<object>(); var controls = new List<object>(); var failed = 0;
        foreach (var probe in source.RootElement.GetProperty("probes").EnumerateArray())
        {
            var utf16Units = probe.GetProperty("textUtf16Units").EnumerateArray().Select(v => checked((char)v.GetInt32())).ToArray();
            var text = new string(utf16Units);
            var expected = probe.GetProperty("sourceOffsets").EnumerateArray().Select(o => o.GetInt32()).ToArray();
            var actual = Offsets(text);
            var pass = actual.SequenceEqual(expected);
            probes.Add(new { id = probe.GetProperty("id").GetString(), text, inputUtf16Units = utf16Units.Select(c => (int)c).ToArray(), expected, actual, pass }); if (!pass) failed++;
        }
        var cluster = "\u0915\u094d\u0937";
        Add("source-helper-keeps-literal-runtime-policy", () =>
        {
            var literal = new TerminalTextEditor(); literal.SetText(cluster); literal.MoveLeft();
            return StringInfo.ParseCombiningCharacters(cluster).Length == 2 && literal.Snapshot.CursorUtf16Offset == 2 &&
                TerminalTextEditor.SegmentationPolicyId == "dotnet-stringinfo-runtime-v1";
        });
        Add("source-caret-prefix-suffix-remain-logical-utf16", () =>
            Offsets(cluster[..2]).SequenceEqual(new[] { 0 }) &&
            Offsets(cluster[2..]).SequenceEqual(new[] { 0 }));
        Add("source-frame-enforces-entire-conjunct-grapheme-bound", () =>
        {
            var editor = new TerminalTextEditorPasteController(); editor.SetText(cluster); editor.SetCursor(0);
            var geometry = new TerminalEditorGeometrySnapshot(new(Guid.NewGuid(), 1, TerminalEditorVisualMapBuilder.SourcePolicyId), 20, 24);
            var map = new TerminalEditorVisualMapBuilder().BuildForTerminal(editor.CaptureLayoutInput(), geometry);
            try { TerminalEditorFrameFactory.Create(map, 0, ImmutableArray<string>.Empty, limits: new(MaximumGraphemeCharacters: 2)); return false; }
            catch (TerminalRenderException e) { return e.Failure == TerminalRenderFailure.ResourceLimit && editor.Snapshot.Text == cluster && editor.Snapshot.CursorUtf16Offset == 0; }
        });
        Add("source-frame-selected-style-covers-whole-conjunct", () =>
        {
            var editor = new TerminalTextEditorPasteController(); editor.SetText(cluster + "Z"); editor.SetCursor(0);
            var geometry = new TerminalEditorGeometrySnapshot(new(Guid.NewGuid(), 1, TerminalEditorVisualMapBuilder.SourcePolicyId), 20, 24);
            var map = new TerminalEditorVisualMapBuilder().BuildForTerminal(editor.CaptureLayoutInput(), geometry);
            var frame = TerminalEditorFrameFactory.Create(map, 0, ImmutableArray<string>.Empty);
            return frame.SourceComponent.Rows.Any(row => row.Contains("\u001b[7m" + cluster + "\u001b[0m", StringComparison.Ordinal)) && (int)typeof(TerminalFrame).GetProperty("LargestGraphemeCharacters", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(frame.Frame)! == 3;
        });
        Add("bounded-long-conjunct-frame-admission", () =>
        {
            var text = string.Concat(Enumerable.Repeat("\u0915\u094d", 200)) + "\u0937";
            var editor = new TerminalTextEditorPasteController(); editor.SetText(text);
            var geometry = new TerminalEditorGeometrySnapshot(new(Guid.NewGuid(), 1, TerminalEditorVisualMapBuilder.SourcePolicyId), 256, 24);
            try
            {
                var map = new TerminalEditorVisualMapBuilder().BuildForTerminal(editor.CaptureLayoutInput(), geometry);
                TerminalEditorFrameFactory.Create(map, 0, ImmutableArray<string>.Empty); return false;
            }
            catch (TerminalRenderException e) { return e.Failure == TerminalRenderFailure.ResourceLimit && editor.Snapshot.Text == text; }
            catch (TerminalEditorLayoutException e) { return e.Failure == TerminalEditorLayoutFailure.ResourceLimit && editor.Snapshot.Text == text; }
        });
        return new(failed, probes.Count, probes, controls);
        void Add(string id, Func<bool> action)
        {
            try { var pass = action(); controls.Add(new { id, pass }); if (!pass) failed++; }
            catch (Exception e) { controls.Add(new { id, pass = false, error = e.ToString() }); failed++; }
        }
    }
}
