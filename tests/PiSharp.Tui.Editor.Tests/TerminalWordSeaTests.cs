using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Diagnostics;
using System.Globalization;
using PiSharp.Tui;
using PiSharp.Tui.Input;

internal static class TerminalWordSeaTests
{
    internal static readonly List<object> SegmentObservations = [], ResourceObservations = [], DataObservations = [];
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("word-sea.complete-pinned-runtime-prefix-suffix-segments", Segments);
        yield return ("word-sea.all-official-lao-khmer-burmese-entries", Entries);
        yield return ("word-resource.identical-six-move-maximum-workloads", Resources);
    }
    private static void Entries()
    {
        var dictionary = typeof(TerminalTextEditorPasteController).GetNestedType("WordDictionary", BindingFlags.NonPublic)!;
        var method = dictionary.GetMethod("ExactSeaValue", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var (name, language, expectedCount) in new[] { ("icu78-laodict.txt", 128, 30_550), ("icu78-khmerdict.txt", 256, 81_028), ("icu78-burmesedict.txt", 512, 41_120) })
        {
            var count = 0; var mismatches = 0; var differences = new List<object>();
            foreach (var raw in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "word-data/icu78-sea", name)))
            {
                var line = raw.Trim(); if (line.Length == 0 || line.StartsWith('#')) continue;
                var fields = line.Split('\t'); var expected = fields.Length > 1 ? int.Parse(fields[1], CultureInfo.InvariantCulture) : 0;
                var actual = (int?)method.Invoke(null, [fields[0], language]); count++;
                if (actual != expected) { mismatches++; differences.Add(new { word = fields[0], expected, actual }); }
            }
            DataObservations.Add(new { name, language, count, expectedCount, mismatches, differences });
            if (count != expectedCount || mismatches != 0) throw new InvalidOperationException($"{name}: {mismatches} mismatches; {count} entries, expected {expectedCount}.");
        }
    }
    private static void Segments()
    {
        using var reference = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "word-data/sea-runtime-observations.json")));
        var method = typeof(TerminalTextEditorPasteController).GetMethod("WordSegments", BindingFlags.Instance | BindingFlags.NonPublic)!; var mismatches = 0;
        foreach (var observation in reference.RootElement.GetProperty("observations").EnumerateArray())
        {
            var fragment = observation.GetProperty("fragment").GetString()!; var editor = new TerminalTextEditorPasteController(); var result = (IEnumerable)method.Invoke(editor, [fragment])!; var segments = new List<object>();
            foreach (var item in result)
            {
                var type = item!.GetType(); var start = (int)type.GetProperty("Start")!.GetValue(item)!; var length = (int)type.GetProperty("Length")!.GetValue(item)!;
                segments.Add(new { segment = fragment.Substring(start, length), index = start, isWordLike = (bool)type.GetProperty("Wordlike")!.GetValue(item)! });
            }
            var actual = JsonSerializer.SerializeToElement(segments); var expected = observation.GetProperty("segments").Clone(); var differences = new List<object>(); EditorTests.Difference(expected, actual, "$", differences);
            var matches = differences.Count == 0; if (!matches) mismatches++; SegmentObservations.Add(new { input = observation.Clone(), expected, actual, differences, matches });
        }
        if (mismatches != 0) throw new InvalidOperationException($"Complete SEA runtime mismatches: {mismatches} of {SegmentObservations.Count}.");
    }
    private static void Resources()
    {
        foreach (var (name, text, expectedForward, expectedBackward) in new[] {
            ("warm-max-cjk", string.Concat(Enumerable.Repeat("\u4f60\u597d\u4e16\u754c", 16_384)), 2, 65_534),
            ("warm-max-thai", string.Concat(Enumerable.Repeat("\u0e20\u0e32\u0e29\u0e32\u0e44\u0e17\u0e22", 9_362)) + "\u0e20\u0e32", 4, 65_534),
            ("warm-max-alternating", string.Concat(Enumerable.Repeat("\u4f60a", 32_768)), 1, 65_535) })
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); var before = GC.GetTotalMemory(false); var allocated = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew(); var forward = 0; var backward = 0;
            for (var iteration = 0; iteration < 3; iteration++)
            {
                var editor = new TerminalTextEditorPasteController(); editor.SetText(text); editor.SetCursor(0); editor.HandleInput(new TerminalKey("f", TerminalModifiers.Alt)); forward = editor.Snapshot.CursorUtf16Offset;
                editor.SetCursor(text.Length); editor.HandleInput(new TerminalKey("b", TerminalModifiers.Alt)); backward = editor.Snapshot.CursorUtf16Offset;
                if (forward != expectedForward || backward != expectedBackward || editor.Snapshot.Text != text) throw new InvalidOperationException("Resource workload cursor/text mismatch.");
            }
            watch.Stop(); var total = GC.GetTotalAllocatedBytes(true) - allocated; GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            ResourceObservations.Add(new { name, utf16 = text.Length, iterations = 3, operations = 6, milliseconds = watch.Elapsed.TotalMilliseconds, allocatedBytes = total,
                retainedHeapDelta = GC.GetTotalMemory(false) - before, workingSetBytes = Process.GetCurrentProcess().WorkingSet64, forward, backward, expectedForward, expectedBackward,
                assemblyBytes = new FileInfo(typeof(TerminalTextEditorPasteController).Assembly.Location).Length, performanceGate = false, representativeInteractiveLatency = false });
        }
    }
}
