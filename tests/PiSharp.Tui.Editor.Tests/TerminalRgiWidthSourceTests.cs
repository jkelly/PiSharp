using System.Reflection;
using System.Text.Json;
using PiSharp.Tui.Input;

internal static class TerminalRgiWidthSourceTests
{
    internal static object Compare(JsonElement source, out int differentObservations)
    {
        var type = typeof(TerminalEditorVisualMapBuilder).Assembly.GetType("PiSharp.Tui.Input.TerminalEditorSourceWidth", throwOnError: true)!;
        var visible = type.GetMethod("Visible", BindingFlags.Static | BindingFlags.NonPublic)!;
        var fullRgi = type.GetMethod("IsRgiEmoji", BindingFlags.Static | BindingFlags.NonPublic);
        var single = (int[])type.GetField("RgiSingleRanges", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var vs16 = (HashSet<int>)type.GetField("RgiVs16", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        bool LegacyRgi(string text)
        {
            var chars = text.EnumerateRunes().Select(r => r.Value).ToArray();
            if (chars.Length == 2 && chars[1] == 0xfe0f) return vs16.Contains(chars[0]);
            if (chars.Length != 1) return false;
            for (var at = 0; at < single.Length; at += 2) if (chars[0] >= single[at] && chars[0] <= single[at + 1]) return true;
            return false;
        }
        var observations = new List<object>(); differentObservations = 0; var widthDifferences = 0; var membershipDifferences = 0;
        foreach (var row in source.GetProperty("observations").EnumerateArray())
        {
            var input = row.GetProperty("input").Clone(); var text = input.GetProperty("text").GetString()!;
            var expected = row.GetProperty("observation").Clone();
            var actual = new { width = (int)visible.Invoke(null, [text])!, isRgiEmoji = fullRgi is null ? LegacyRgi(text) : (bool)fullRgi.Invoke(null, [text])! };
            var differences = new List<string>();
            if (expected.GetProperty("width").GetInt32() != actual.width) { widthDifferences++; differences.Add("width"); }
            if (expected.GetProperty("isRgiEmoji").GetBoolean() != actual.isRgiEmoji) { membershipDifferences++; differences.Add("isRgiEmoji"); }
            if (differences.Count > 0) differentObservations++;
            observations.Add(new { input, expected, actual, differences });
        }
        return new { comparisonBoundary = "Actual compiled private source width; complete expected and actual width/property observations retained", matcher = fullRgi is null ? "Existing scalar/VS16 fields; no multi-sequence implementation" : "Actual compiled private complete IsRgiEmoji method", count = observations.Count, differentObservations, widthDifferences, membershipDifferences, observations };
    }
}
