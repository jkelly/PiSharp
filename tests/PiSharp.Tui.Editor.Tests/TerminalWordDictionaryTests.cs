using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text;
using System.Globalization;
using System.Diagnostics;
using PiSharp.Tui;
using PiSharp.Tui.Input;

internal static class TerminalWordDictionaryTests
{
    internal static readonly List<object> SegmentObservations = [];
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("word-dictionary.complete-pinned-runtime-prefix-suffix-segments", Segments);
        yield return ("word-dictionary.all-official-cjk-thai-entries-and-costs", DictionaryEntries);
        yield return ("word-dictionary.unicode17-official-nfkc-conformance", Normalization);
        yield return ("word-dictionary.maximum-cjk-thai-alternating-runs-and-atomic-admission", Bounds);
    }
    private static readonly Type Dictionary = typeof(TerminalTextEditorPasteController).GetNestedType("WordDictionary", BindingFlags.NonPublic)!;
    internal static readonly List<object> DataObservations = [];
    private static void DictionaryEntries()
    {
        var method = Dictionary.GetMethod("ExactValue", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var (name, thai) in new[] { ("icu78-cjdict.txt", false), ("icu78-thaidict.txt", true) })
        {
            var count = 0; var mismatches = 0; var first = new List<object>();
            foreach (var raw in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "word-data/icu78", name)))
            {
                var line = raw.Trim(); if (line.Length == 0 || line.StartsWith('#')) continue;
                var fields = line.Split('\t'); var expected = thai ? 0 : int.Parse(fields[1], CultureInfo.InvariantCulture);
                var actual = (int?)method.Invoke(null, [fields[0], thai]); count++;
                if (actual != expected) { mismatches++; if (first.Count < 32) first.Add(new { word = fields[0], expected, actual }); }
            }
            DataObservations.Add(new { kind = "official-compiled-dictionary-membership-costs", name, count, mismatches, first });
            if (mismatches != 0) throw new InvalidOperationException($"{name}: {mismatches} of {count} entry/cost mismatches.");
        }
    }
    private static void Normalization()
    {
        var method = Dictionary.GetMethod("Normalize", BindingFlags.Static | BindingFlags.NonPublic)!; var rows = 0; var checks = 0; var mismatches = 0; var first = new List<object>();
        foreach (var raw in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "word-data/icu78/NormalizationTest-17.0.0.txt")))
        {
            var line = raw.Split('#')[0].Trim(); if (line.Length == 0 || line.StartsWith('@')) continue;
            var columns = line.Split(';').Take(5).Select(value => string.Concat(value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => new Rune(int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString()))).ToArray(); rows++;
            for (var i = 0; i < columns.Length; i++)
            {
                var actual = (string)method.Invoke(null, [columns[i]])!; checks++;
                if (actual != columns[3]) { mismatches++; if (first.Count < 32) first.Add(new { row = rows, column = i, input = columns[i], expected = columns[3], actual }); }
            }
        }
        DataObservations.Add(new { kind = "official-Unicode17-NFKC-conformance", rows, checks, mismatches, first });
        if (mismatches != 0) throw new InvalidOperationException($"Normalization: {mismatches} of {checks} conformance mismatches.");
    }
    private static void Bounds()
    {
        foreach (var (name, text) in new[] {
            ("maximum-cjk", string.Concat(Enumerable.Repeat("你好世界", 16_384))),
            ("maximum-thai", string.Concat(Enumerable.Repeat("ภาษาไทย", 9_362)) + "ภา"),
            ("maximum-alternating-ranges", string.Concat(Enumerable.Repeat("你a", 32_768))) })
        {
            var editor = new TerminalTextEditorPasteController(); editor.SetText(text); editor.SetCursor(0);
            var stopwatch = Stopwatch.StartNew(); editor.HandleInput(new TerminalKey("f", TerminalModifiers.Alt));
            var forward = editor.Snapshot.CursorUtf16Offset; editor.SetCursor(text.Length); editor.HandleInput(new TerminalKey("b", TerminalModifiers.Alt));
            var backward = editor.Snapshot.CursorUtf16Offset;
            if (forward <= 0 || forward > text.Length || backward < 0 || backward >= text.Length || editor.Snapshot.Text != text)
                throw new InvalidOperationException("Maximum dictionary movement escaped draft or changed text.");
            var before = editor.Snapshot; var revision = editor.LayoutIdentity; var ring = editor.KillRingCount; var undo = editor.UndoDepth; var calls = 0; editor.TextChanged += _ => calls++;
            try { editor.HandleInput(new TerminalText("x")); throw new InvalidOperationException("Missing oversized rejection."); }
            catch (TerminalTextEditorException error) when (error.Failure == TerminalTextEditorFailure.ResourceLimit) { }
            if (editor.Snapshot != before || editor.LayoutIdentity != revision || editor.KillRingCount != ring || editor.UndoDepth != undo || calls != 0)
                throw new InvalidOperationException("Maximum dictionary admission changed state before rejection.");
            DataObservations.Add(new { kind = "maximum-dictionary-draft-bound", name, utf16 = text.Length, forward, backward, elapsedMilliseconds = stopwatch.ElapsedMilliseconds, atomicRejection = true });
        }
        var normalization = Dictionary.GetMethod("Normalize", BindingFlags.Static | BindingFlags.NonPublic)!;
        var marks = string.Concat(Enumerable.Repeat("\u0315\u0300", 32_768)); var normalized = (string)normalization.Invoke(null, [marks])!;
        var expected = new string('\u0300', 32_768) + new string('\u0315', 32_768);
        if (normalized != expected) throw new InvalidOperationException("Maximum canonical ordering differs.");
        DataObservations.Add(new { kind = "maximum-linear-canonical-ordering", utf16 = marks.Length, matches = true });
    }
    private static void Segments()
    {
        using var reference = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "word-data/dictionary-runtime-observations.json")));
        var method = typeof(TerminalTextEditorPasteController).GetMethod("WordSegments", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var mismatches = 0;
        foreach (var observation in reference.RootElement.GetProperty("observations").EnumerateArray())
        {
            var fragment = observation.GetProperty("fragment").GetString()!; var editor = new TerminalTextEditorPasteController();
            var result = (IEnumerable)method.Invoke(editor, [fragment])!; var segments = new List<object>();
            foreach (var item in result)
            {
                var type = item!.GetType(); var start = (int)type.GetProperty("Start")!.GetValue(item)!; var length = (int)type.GetProperty("Length")!.GetValue(item)!;
                segments.Add(new { segment = fragment.Substring(start, length), index = start, isWordLike = (bool)type.GetProperty("Wordlike")!.GetValue(item)! });
            }
            var actual = JsonSerializer.SerializeToElement(segments); var expected = observation.GetProperty("segments").Clone(); var differences = new List<object>();
            EditorTests.Difference(expected, actual, "$", differences); var matches = differences.Count == 0; if (!matches) mismatches++;
            SegmentObservations.Add(new { input = observation.Clone(), expected, actual, differences, matches });
        }
        if (mismatches != 0) throw new InvalidOperationException($"Complete dictionary runtime segment mismatches: {mismatches} of {SegmentObservations.Count}.");
    }
}
