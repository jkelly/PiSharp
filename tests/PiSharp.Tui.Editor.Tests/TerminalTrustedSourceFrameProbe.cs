using System.Reflection;
using System.Text;
using System.Text.Json;
using PiSharp.Tui;
using PiSharp.Tui.Input;
using PiSharp.Tui.Rendering;

internal static class TerminalTrustedSourceFrameProbe
{
    internal static async Task<int> Run(string sourcePath, string supplementalPath, string reportPath)
    {
        var results = new List<object>(); var componentMatches = 0; var componentDifferences = 0; var tests = new List<object>(); var failed = 0;
        var benignTransportCases = 0; var benignTransportDifferences = 0; var safetyTransportCases = 0;
        foreach (var test in TerminalTrustedSourceFrameTests.Cases())
        { try { await test.Run(); tests.Add(new { test.Name, status = "passed" }); Console.WriteLine("PASS " + test.Name); } catch (Exception e) { failed++; tests.Add(new { test.Name, status = "failed", error = e.ToString() }); Console.WriteLine("FAIL " + test.Name + " " + e); } }
        foreach (var filename in new[] { sourcePath, supplementalPath })
        {
            using var source = JsonDocument.Parse(await File.ReadAllTextAsync(filename));
            foreach (var row in source.RootElement.GetProperty("observations").EnumerateArray())
            {
                var input = row.GetProperty("input").Clone(); var expected = row.GetProperty("observation").Clone();
                var editor = new TerminalTextEditorPasteController(); var changes = new List<string>(); editor.TextChanged += changes.Add;
                var geometry = new TerminalEditorGeometrySnapshot(new(Guid.NewGuid(), 0, TerminalEditorVisualMapBuilder.SourcePolicyId),
                    input.GetProperty("columns").GetInt32(), input.GetProperty("terminalRows").GetInt32());
                var builder = new TerminalEditorVisualMapBuilder(); var scroll = 0;
                void Key(string data)
                {
                    foreach (var key in new TerminalInputDecoder().Feed(data.AsSpan()))
                    {
                        if (key is TerminalKey terminalKey && TerminalTextEditorPasteController.RequiresLayout(terminalKey))
                            editor.HandleInput(key, builder.Build(editor.CaptureLayoutInput(), geometry), geometry.Identity);
                        else editor.HandleInput(key);
                    }
                }
                TerminalEditorSourceFrame Project() => TerminalEditorSourceFrameProjector.Project(
                    builder.Build(editor.CaptureLayoutInput(), geometry), scroll, input.GetProperty("focused").GetBoolean());
                string[]? componentRows = null; object? componentError = null; TerminalEditorSourceFrame? component = null; TerminalEditorVisualMap? capturedMap = null;
                object? unstyledFrame = null; object? unstyledError = null; object? transportComparison = null; var writes = new List<string>();
                try
                {
                    var text = input.GetProperty("text").GetString()!;
                    switch (input.GetProperty("admission").GetString())
                    {
                        case "set": editor.SetText(text); break;
                        case "insert": editor.InsertText(text); break;
                        case "history": editor.AddToHistory(text); Key("\u001b[A"); break;
                        case "paste": editor.HandleInput(new TerminalPaste(text)); break;
                        default: throw new InvalidOperationException("Unknown frozen admission.");
                    }
                    if (input.TryGetProperty("initialRender", out var initial) && initial.GetBoolean()) scroll = Project().FirstVisibleSourceRow;
                    if (input.TryGetProperty("keys", out var keys)) foreach (var key in keys.EnumerateArray()) Key(key.GetString()!);
                    if (input.TryGetProperty("afterInsert", out var insertion)) { scroll = Project().FirstVisibleSourceRow; editor.InsertText(insertion.GetString()!); }
                    capturedMap = builder.Build(editor.CaptureLayoutInput(), geometry); component = TerminalEditorSourceFrameProjector.Project(capturedMap, scroll, input.GetProperty("focused").GetBoolean()); componentRows = component.Rows.ToArray();
                }
                catch (Exception error) { componentError = new { type = error.GetType().FullName, error.Message, details = error.ToString() }; }
                var snapshot = editor.Snapshot; var line = snapshot.Text[..snapshot.CursorUtf16Offset].Count(c => c == '\n');
                var lastLf = snapshot.Text.LastIndexOf('\n', Math.Max(0, snapshot.CursorUtf16Offset - 1), Math.Max(0, snapshot.CursorUtf16Offset));
                var col = snapshot.CursorUtf16Offset - lastLf - 1;
                var actualCommon = new { text = snapshot.Text, expandedText = editor.GetExpandedText(), cursor = new { line, col },
                    focused = input.GetProperty("focused").GetBoolean(), changes, componentRows };
                var expectedCommon = JsonSerializer.SerializeToElement(new {
                    text = expected.GetProperty("text"), expandedText = expected.GetProperty("expandedText"), cursor = expected.GetProperty("cursor"),
                    focused = expected.GetProperty("focused"), changes = expected.GetProperty("changes"), componentRows = expected.GetProperty("componentRows") });
                var differences = new List<object>(); EditorTests.Difference(expectedCommon, JsonSerializer.SerializeToElement(actualCommon), "$", differences);
                if (differences.Count == 0 && componentError is null && expected.GetProperty("error").ValueKind == JsonValueKind.Null) componentMatches++; else componentDifferences++;
                if (component is not null)
                {
                    try
                    {
                        var result = TerminalEditorFrameFactory.Create(capturedMap!, scroll, [], input.GetProperty("focused").GetBoolean());
                        var frame = result.Frame;
                        transportComparison = TerminalTrustedSourceFrameTests.CompareBenignPresentation(expected, input, result, out var benign, out var transportDifferences);
                        if (benign) { benignTransportCases++; if (transportDifferences > 0) { benignTransportDifferences++; failed++; } }
                        else safetyTransportCases++;
                        unstyledFrame = new { frame.Columns, frame.Rows, frame.Cursor, frame.WidthPolicyId, frame.IsClipped, result.FirstVisibleSourceRow, result.EditorRowOrigin, result.TotalSourceRows, positionCursor = TerminalTrustedSourceFrameTests.Anchor(frame), inverseSpan = TerminalTrustedSourceFrameTests.Style(frame), styledRows = Enumerable.Range(0, frame.Rows.Length).Select(r => TerminalTrustedSourceFrameTests.StyledRow(frame, r)).ToArray() };
                        var terminal = new BorrowedWriter(writes); await using var renderer = new VtRenderer(terminal);
                        await renderer.RenderAsync(frame); TerminalTrustedSourceFrameTests.ApprovedCommandsOnly(writes.Single());
                    }
                    catch (Exception error) { failed++; unstyledError = new { type = error.GetType().FullName, error.Message, details = error.ToString() }; }
                }
                results.Add(new { input, sourceExpected = expected, actualCommon, componentError, differences,
                    nativeSourceFirstVisibleRow = component?.FirstVisibleSourceRow,
                    trustedFactoryConversion = new { frame = unstyledFrame, error = unstyledError, writes, transportComparison,
                        boundary = "Released trusted factory and actual VtRenderer over inert borrowed writer. Full source observations retained; actual CLI adoption and qualification are lead-owned." } });
            }
        }
        var integration = new List<object>();
        foreach (var name in new[] { "frozen-history-layout-source.json", "supplementary-history-layout-source-observations.json", "rgi-layout-source-observations.json" })
        {
            using var source = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(sourcePath)!, "default-view-integration-vectors", name)));
            var comparison = TerminalTrustedFrameIntegrationReplay.Compare(source.RootElement, out var differences, sourceProfile: true);
            integration.Add(new { name, comparison }); failed += differences;
        }
        if (results.Count != 63 || componentMatches != 61 || componentDifferences != 2) failed++;
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { schemaVersion = 1, sourceCases = results.Count,
            componentMatches, componentDifferences, benignTransportCases, benignTransportDifferences, safetyTransportCases, tests, integration, failed, observations = results, purpose = "Trusted producer factory and retained unchanged source witnesses; actual default qualification remains lead-owned",
            actualDefaultViewQualified = false, productionChanges = 5, fullNativeGate = false, physicalTerminal = false }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Console.WriteLine($"Renderer contract capture: {results.Count} cases, {componentMatches} component matches, {componentDifferences} complete differences retained.");
        return failed == 0 ? 0 : 1;
    }

    private sealed class ExistingSourceWidth : ITerminalWidthPolicy
    {
        private static readonly Func<string, int> Visible = typeof(TerminalEditorVisualMapBuilder).Assembly
            .GetType("PiSharp.Tui.Input.TerminalEditorSourceWidth", throwOnError: true)!
            .GetMethod("Visible", BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<Func<string, int>>();
        public string Id => TerminalEditorVisualMapBuilder.SourcePolicyId;
        public int GetWidth(string grapheme) => Visible(grapheme);
    }
    private sealed class BorrowedWriter(List<string> writes) : IConsoleTerminal
    {
        private static readonly TerminalConsoleState State = new(0, 0, 65001, 65001, 25, true, 0, 0);
        public TerminalLeaseSnapshot Snapshot => new(State, State, null, false, false, 0, 0, 0, 0, 0, 0);
        public ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default) => ValueTask.FromResult(0);
        public ValueTask WriteAsync(ReadOnlyMemory<char> frame, CancellationToken token = default) { token.ThrowIfCancellationRequested(); writes.Add(frame.ToString()); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => throw new InvalidOperationException("The borrowed writer must never be disposed by VtRenderer.");
    }
}
