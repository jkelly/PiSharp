using System.Text.Json;
using PiSharp.Tui;
using PiSharp.Tui.Input;

internal static class TerminalHistoryLayoutSourceReplay
{
    internal static readonly List<object> NativeDecodedJumpEvents = [];
    internal static readonly List<object> NativeDecodedWordEvents = [];
    internal static object Compare(JsonElement source, out int differentCases, bool sourceProfile = false, bool captureEvents = false, bool captureWordEvents = false)
    {
        var cases = new List<object>(); differentCases = 0; var checkpoints = 0; var differentCheckpoints = 0;
        foreach (var fixture in source.GetProperty("cases").EnumerateArray())
        {
            var editor = new TerminalTextEditorPasteController(); var changes = new List<string>(); var submits = new List<string>();
            var steps = new List<object>(); var operationErrors = new List<object>(); editor.TextChanged += changes.Add;
            var renderCalls = 0; int? lastRenderColumns = null; string[]? lastRenderedRows = null; var firstVisibleSourceRow = 0;
            var policy = sourceProfile ? TerminalEditorVisualMapBuilder.SourcePolicyId : TerminalEditorVisualMapBuilder.PolicyId;
            var geometry = new TerminalEditorGeometrySnapshot(new(Guid.NewGuid(), 0, policy), sourceProfile ? 81 : 80, 24);
            var builder = new TerminalEditorVisualMapBuilder(); var stepIndex = 0;
            var decodedEvents = new List<object>();
            foreach (var step in fixture.GetProperty("input").GetProperty("steps").EnumerateArray())
            {
                try
                {
                    switch (step.GetProperty("kind").GetString())
                    {
                        case "history": editor.AddToHistory(step.GetProperty("data").GetString()!); break;
                        case "set": editor.SetText(step.GetProperty("data").GetString()!); firstVisibleSourceRow = 0; break;
                        case "insert": editor.InsertText(step.GetProperty("data").GetString()!); break;
                        case "render":
                            var columns = step.GetProperty("columns").GetInt32(); var rows = step.GetProperty("terminalRows").GetInt32();
                            if (columns != geometry.ContentColumns || rows != geometry.ViewportRows)
                                geometry = new(geometry.Identity with { GeometryRevision = geometry.Identity.GeometryRevision + 1 }, columns, rows);
                            lastRenderColumns = columns;
                            var renderMap = builder.Build(editor.CaptureLayoutInput(), geometry);
                            if (sourceProfile)
                            { var frame = TerminalEditorSourceFrameProjector.Project(renderMap, firstVisibleSourceRow); lastRenderedRows = frame.Rows.ToArray(); firstVisibleSourceRow = frame.FirstVisibleSourceRow; }
                            else lastRenderedRows = renderMap.RenderedRows.ToArray();
                            renderCalls++; break;
                        case "input":
                            var decoder = new TerminalInputDecoder(timeProvider: new FixedClock());
                            foreach (var input in decoder.Feed(step.GetProperty("data").GetString()!).AddRange(decoder.Complete()))
                            {
                                if (captureEvents || captureWordEvents) decodedEvents.Add(new { stepIndex, raw = step.GetProperty("data").GetString(), kind = input.GetType().Name, decoded = JsonSerializer.SerializeToElement(input, input.GetType()) });
                                if (input is TerminalKey { Key: "Enter", Modifiers: TerminalModifiers.None, Action: not TerminalKeyAction.Release })
                                {
                                    // Explicit fixture host submission boundary. This is NOT production acceptance wiring.
                                    submits.Add(TrimExpandedSubmission(editor.GetExpandedText())); editor.Reset(); firstVisibleSourceRow = 0;
                                }
                                else if (TerminalTextEditorPasteController.RequiresLayout(input))
                                {
                                    var map = builder.Build(editor.CaptureLayoutInput(), geometry);
                                    var previousBrowseIndex = editor.HistoryIndex;
                                    editor.HandleInput(input, map, geometry.Identity);
                                    if (editor.HistoryIndex != previousBrowseIndex) firstVisibleSourceRow = 0;
                                }
                                else editor.HandleInput(input);
                            }
                            break;
                        default: throw new InvalidOperationException("Unknown frozen operation.");
                    }
                }
                catch (Exception error) { operationErrors.Add(new { stepIndex, error = error.ToString() }); }
                var snapshot = editor.Snapshot; var text = snapshot.Text; var offset = snapshot.CursorUtf16Offset; var line = 0; var col = 0;
                // Pinned Editor splits logical lines on LF only; history retains raw internal CR/tab.
                for (var at = 0; at < offset; at++) { if (text[at] == '\n') { line++; col = 0; } else col++; }
                steps.Add(new { text, expandedText = editor.GetExpandedText(), cursor = new { line, col }, cursorUtf16Offset = offset,
                    changes = changes.ToArray(), submits = submits.ToArray(), renderRequests = 0, renderCalls, lastRenderColumns,
                    terminalRows = geometry.ViewportRows, lastRenderedRows });
                stepIndex++;
            }
            var expected = fixture.GetProperty("observation").Clone(); var actual = JsonSerializer.SerializeToElement(new { steps });
            var differences = new List<object>(); EditorTests.Difference(expected, actual, "$", differences);
            for (var at = 0; at < steps.Count; at++)
            {
                var checkpointDifferences = new List<object>(); EditorTests.Difference(expected.GetProperty("steps")[at], actual.GetProperty("steps")[at], "$", checkpointDifferences);
                if (checkpointDifferences.Count != 0) differentCheckpoints++;
            }
            checkpoints += steps.Count;
            if (differences.Count != 0 || operationErrors.Count != 0) differentCases++;
            cases.Add(new { id = fixture.GetProperty("id").GetString(), input = fixture.GetProperty("input").Clone(), expected, actual,
                matches = differences.Count == 0 && operationErrors.Count == 0, differences, operationErrors });
            if (captureEvents) NativeDecodedJumpEvents.Add(new { id = fixture.GetProperty("id").GetString(), events = decodedEvents });
            if (captureWordEvents) NativeDecodedWordEvents.Add(new { id = fixture.GetProperty("id").GetString(), events = decodedEvents });
        }
        return new { sourceBoundary = sourceProfile ? "Frozen unchanged public Editor complete observations; native actual controller and shared experimental source map/component projection; fixture host alone owns scroll and Enter reset" : "Frozen unchanged public Editor history/operations/render/callback observations; native actual controller/shared escaped map; explicit fixture host Enter reset only",
            policyId = policyId(), normalization = "none", filtering = "none", checkpoints, differentCheckpoints,
            matchedCases = cases.Count - differentCases, differentCases, sourceRenderParityQualified = false, productionAcceptanceWiringQualified = false, cases };
        string policyId() => sourceProfile ? TerminalEditorVisualMapBuilder.SourcePolicyId : TerminalEditorVisualMapBuilder.PolicyId;
    }
    // Fixture host contract copied exactly from independently accepted trim6047151.
    // Expansion occurs first; frozen expected source objects remain unchanged.
    // This does not qualify either actual host or accepted receipt ownership.
    private static string TrimExpandedSubmission(string text)
    {
        var first = 0; var end = text.Length;
        while (first < end && IsSubmissionWhitespace(text[first])) first++;
        while (end > first && IsSubmissionWhitespace(text[end - 1])) end--;
        return first == 0 && end == text.Length ? text : text[first..end];
    }

    private static bool IsSubmissionWhitespace(char value) => value is
        >= '\u0009' and <= '\u000D' or '\u0020' or '\u00A0' or '\u1680' or
        >= '\u2000' and <= '\u200A' or '\u2028' or '\u2029' or '\u202F' or
        '\u205F' or '\u3000' or '\uFEFF';

    private sealed class FixedClock : TimeProvider { public override long TimestampFrequency => 1000; public override long GetTimestamp() => 0; }
}
