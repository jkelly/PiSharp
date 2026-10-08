using System.Text.Json;
using PiSharp.Cli.Interactive;
using PiSharp.Tui;
using PiSharp.Tui.Input;
using PiSharp.Tui.Rendering;

internal static class TerminalDefaultCorpus
{
    internal sealed record Result(int Cases, int Checkpoints, int Renders, int Failed, int ApprovedControlRowDifferences, List<object> Observations);
    internal static async Task<Result> Run()
    {
        var observations = new List<object>(); var cases = 0; var checkpoints = 0; var renders = 0; var failed = 0; var approved = 0;
        using var safeSource = JsonDocument.Parse(await File.ReadAllTextAsync("artifacts/default-approved-history-profile-observations.json"));
        var safeCase = safeSource.RootElement.GetProperty("cases")[0];
        foreach (var name in new[] { "frozen-history-layout-source.json", "supplementary-history-layout-source-observations.json", "rgi-layout-source-observations.json" })
        {
            using var source = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine("artifacts", "default-view-integration-vectors", name)));
            foreach (var fixture in source.RootElement.GetProperty("cases").EnumerateArray())
            {
                cases++; var e = new TerminalTextEditorPasteController(); var changes = new List<string>(); var submits = new List<string>();
                e.TextChanged += changes.Add; var sink = new TerminalDefaultQualificationTests.ConsoleSink(81, 24);
                var presented = new List<object>(); TerminalEditorRenderedFrame? current = null; TerminalEditorVisualMap? map = null;
                await using var view = new TerminalSessionView(sink, sink, (m, f) => { map = m; current = f; });
                await view.StartAsync(default); var actualSteps = new List<JsonElement>(); var differences = new List<object>(); var errors = new List<object>();
                string[]? lastRows = null; int? lastColumns = null; var renderCalls = 0; var stepIndex = 0; var unsafeRenderedRows = false;
                foreach (var step in fixture.GetProperty("input").GetProperty("steps").EnumerateArray())
                {
                    try
                    {
                        switch (step.GetProperty("kind").GetString())
                        {
                            case "history": e.AddToHistory(step.GetProperty("data").GetString()!); break;
                            case "set": e.SetText(step.GetProperty("data").GetString()!); break;
                            case "insert": e.InsertText(step.GetProperty("data").GetString()!); break;
                            case "render":
                                var columns = step.GetProperty("columns").GetInt32(); var rows = step.GetProperty("terminalRows").GetInt32();
                                sink.Viewport = new(columns, rows, 0, 0, columns, rows); lastColumns = columns;
                                var capture = e.CaptureLayoutInput();
                                await view.SetDraftAsync(new(e.Snapshot.Text, e.Snapshot.CursorUtf16Offset) { Layout = capture }, default);
                                TerminalDefaultQualificationTests.Check(map is not null && current is not null && map.EditorIdentity == capture.Identity &&
                                    map.ScrollResetRevision == capture.ScrollResetRevision && map.SourceText == capture.Snapshot.Text &&
                                    map.ContentColumns == columns && map.ViewportRows == rows && map.PolicyId == TerminalEditorVisualMapBuilder.SourcePolicyId,
                                    "Actual default frame lost canonical capture, safe policy or geometry");
                                lastRows = current!.SourceComponent.Rows.ToArray();
                                unsafeRenderedRows = e.Snapshot.Text.Any(c => c < 32 && c is not ('\n' or '\t') || c is >= '\u007f' and <= '\u009f'); renderCalls++; renders++;
                                presented.Add(new { stepIndex, capture.Identity, capture.ScrollResetRevision, map!.SourceText,
                                    current.SourceComponent, current.FirstVisibleSourceRow, current.EditorRowOrigin, current.ComponentWindowOffset,
                                    frame = new { current.Frame.Rows, current.Frame.Cursor, current.Frame.IsClipped }, writes = sink.Writes.ToArray() });
                                break;
                            case "input":
                                var decoder = new TerminalInputDecoder();
                                foreach (var input in decoder.Feed(step.GetProperty("data").GetString()!.AsSpan()).AddRange(decoder.Complete()))
                                {
                                    if (input is TerminalKey { Key: "Enter", Modifiers: TerminalModifiers.None, Action: not TerminalKeyAction.Release })
                                    { submits.Add(Trim(e.GetExpandedText())); e.Reset(); }
                                    else if (TerminalTextEditorPasteController.RequiresLayout(input))
                                    { var g = view.CaptureEditorGeometry(); e.HandleInput(input, new TerminalEditorVisualMapBuilder().BuildForTerminal(e.CaptureLayoutInput(), g), g.Identity); }
                                    else e.HandleInput(input);
                                }
                                break;
                            default: throw new InvalidOperationException("Unknown frozen operation");
                        }
                    }
                    catch (Exception error) { errors.Add(new { stepIndex, error = error.ToString() }); }
                    var text = e.Snapshot.Text; var offset = e.Snapshot.CursorUtf16Offset; var line = 0; var col = 0;
                    for (var at = 0; at < offset; at++) { if (text[at] == '\n') { line++; col = 0; } else col++; }
                    var actual = JsonSerializer.SerializeToElement(new { text, expandedText = e.GetExpandedText(), cursor = new { line, col }, cursorUtf16Offset = offset,
                        changes = changes.ToArray(), submits = submits.ToArray(), renderRequests = 0, renderCalls, lastRenderColumns = lastColumns,
                        terminalRows = sink.Viewport.Rows, lastRenderedRows = lastRows }); actualSteps.Add(actual);
                    var expected = fixture.GetProperty("observation").GetProperty("steps")[stepIndex];
                    foreach (var property in expected.EnumerateObject())
                    {
                        if (!actual.TryGetProperty(property.Name, out var value) || !JsonElement.DeepEquals(property.Value, value))
                        {
                            var approvedControlRow = property.Name == "lastRenderedRows" && unsafeRenderedRows;
                            JsonElement? approvedProfileExpected = null;
                            if (approvedControlRow && JsonElement.DeepEquals(fixture.GetProperty("id"), safeCase.GetProperty("id")))
                                approvedProfileExpected = safeCase.GetProperty("observation").GetProperty("steps")[stepIndex].GetProperty("lastRenderedRows").Clone();
                            var approvedExactMatch = approvedProfileExpected is { } approvedRows && JsonElement.DeepEquals(approvedRows, value);
                            if (approvedControlRow && approvedExactMatch) approved++; else failed++;
                            differences.Add(new { stepIndex, path = property.Name, expected = property.Value.Clone(), actual = value.Clone(), approvedControlRowDifference = approvedControlRow,
                                approvedProfileExpected, approvedExactMatch });
                        }
                    }
                    stepIndex++; checkpoints++;
                }
                if (errors.Count > 0) failed += errors.Count;
                WireGrammar.Check(string.Concat(sink.Writes), 256, 256);
                observations.Add(new { id = fixture.GetProperty("id").Clone(), input = fixture.GetProperty("input").Clone(), sourceExpected = fixture.GetProperty("observation").Clone(),
                    actual = actualSteps, differences, errors, actualDefaultPresentations = presented, originalSourceExpectedChanged = false });
            }
        }
        TerminalDefaultQualificationTests.Check(cases == 40 && checkpoints == 745 && renders == 66, "Complete source corpus was truncated");
        return new(cases, checkpoints, renders, failed, approved, observations);
    }
    private static string Trim(string text)
    {
        var first = 0; var end = text.Length; while (first < end && Space(text[first])) first++; while (end > first && Space(text[end - 1])) end--;
        return text[first..end];
    }
    private static bool Space(char c) => c is >= '\u0009' and <= '\u000d' or '\u0020' or '\u00a0' or '\u1680' or >= '\u2000' and <= '\u200a' or '\u2028' or '\u2029' or '\u202f' or '\u205f' or '\u3000' or '\ufeff';
}
