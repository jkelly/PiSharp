using System.Reflection;
using System.Text;
using System.Text.Json;
using PiSharp.Tui;
using PiSharp.Tui.Input;
using PiSharp.Tui.Rendering;

internal static class TerminalRendererContractProbe
{
    internal static async Task<int> Run(string sourcePath, string supplementalPath, string reportPath)
    {
        var results = new List<object>(); var componentMatches = 0; var componentDifferences = 0;
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
                string[]? componentRows = null; object? componentError = null; TerminalEditorSourceFrame? component = null;
                object? unstyledFrame = null; object? unstyledError = null; var writes = new List<string>();
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
                    component = Project(); componentRows = component.Rows.ToArray();
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
                        // Diagnostic only: this is the current unstyled converter, not an adoption implementation.
                        var layout = new TerminalTextLayout(new ExistingSourceWidth());
                        var frame = layout.CreateFrame(string.Join('\n', component.Rows), geometry.ViewportRows, geometry.ContentColumns,
                            new(component.CursorComponentRow, component.CursorComponentColumn, input.GetProperty("focused").GetBoolean()));
                        unstyledFrame = new { frame.Columns, frame.Rows, frame.Cursor, frame.WidthPolicyId, frame.IsClipped };
                        var terminal = new BorrowedWriter(writes); await using var renderer = new VtRenderer(terminal);
                        await renderer.RenderAsync(frame); // Actual renderer writes only to the private inert borrowed writer.
                    }
                    catch (Exception error) { unstyledError = new { type = error.GetType().FullName, error.Message, details = error.ToString() }; }
                }
                results.Add(new { input, sourceExpected = expected, actualCommon, componentError, differences,
                    nativeSourceFirstVisibleRow = component?.FirstVisibleSourceRow,
                    currentUnstyledConversion = new { frame = unstyledFrame, error = unstyledError, writes,
                        boundary = "Existing TerminalTextLayout plus the actual compiled source width; actual VtRenderer over inert borrowed writer. CLI default view is not invoked and adoption is not implemented." } });
            }
        }
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { schemaVersion = 1, sourceCases = results.Count,
            componentMatches, componentDifferences, observations = results, purpose = "Baseline contract witnesses and test inputs; no transport/source acceptance inferred from capture success",
            actualDefaultViewQualified = false, productionChanges = 0, fullNativeGate = false, physicalTerminal = false }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Console.WriteLine($"Renderer contract capture: {results.Count} cases, {componentMatches} component matches, {componentDifferences} complete differences retained.");
        return 0;
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
