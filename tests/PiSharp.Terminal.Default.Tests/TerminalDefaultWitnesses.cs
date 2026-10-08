using System.Text.Json;
using PiSharp.Cli.Interactive;
using PiSharp.Tui;
using PiSharp.Tui.Input;
using PiSharp.Tui.Rendering;

internal static class TerminalDefaultWitnesses
{
    internal sealed record Result(int Cases, int ComponentMatches, int PhysicalRowMatches, int RetainedSourceFailures, int Failed, List<object> Observations);
    internal static async Task<Result> Run()
    {
        using var approved = JsonDocument.Parse(await File.ReadAllTextAsync("artifacts/default-approved-control-picture-source-observations-r2.json"));
        var expected = approved.RootElement.GetProperty("observations").EnumerateArray().ToDictionary(o => o.GetProperty("input").GetProperty("id").GetString()!, o => o.Clone());
        var observations = new List<object>(); var count = 0; var components = 0; var physical = 0; var rejected = 0; var failed = 0;
        foreach (var file in new[] { "renderer-contract-source-observations.json", "renderer-contract-supplemental-source-observations.json" })
        {
            using var original = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine("artifacts", file)));
            foreach (var source in original.RootElement.GetProperty("observations").EnumerateArray())
            {
                count++; var fixture = source.GetProperty("input"); var id = fixture.GetProperty("id").GetString()!; var oracle = expected[id].GetProperty("observation");
                var columns = fixture.GetProperty("columns").GetInt32(); var rows = fixture.GetProperty("terminalRows").GetInt32(); var e = new TerminalTextEditorPasteController();
                var sink = new TerminalDefaultQualificationTests.ConsoleSink(columns, rows); TerminalEditorRenderedFrame? current = null;
                var presents = new List<object>(); await using var view = new TerminalSessionView(sink, sink, (_, f) => { current = f; presents.Add(f); }); await view.StartAsync(default);
                string? error = null; string[]? actualRows = null; string[]? physicalRows = null; string[]? expectedPhysicalRows = null;
                try
                {
                    var text = fixture.GetProperty("text").GetString()!;
                    switch (fixture.GetProperty("admission").GetString())
                    {
                        case "set": e.SetText(text); break;
                        case "insert": e.InsertText(text); break;
                        case "history":
                            e.AddToHistory(text); var recallGeometry = view.CaptureEditorGeometry();
                            e.HandleInput(new TerminalKey("Up"), new TerminalEditorVisualMapBuilder().BuildForTerminal(e.CaptureLayoutInput(), recallGeometry), recallGeometry.Identity); break;
                        default: e.HandleInput(new TerminalPaste(text)); break;
                    }
                    if (fixture.TryGetProperty("initialRender", out var initial) && initial.GetBoolean()) await Render();
                    if (fixture.TryGetProperty("keys", out var keys))
                        foreach (var key in keys.EnumerateArray())
                        {
                            var decoder = new TerminalInputDecoder();
                            foreach (var input in decoder.Feed(key.GetString()!.AsSpan()).AddRange(decoder.Complete()))
                                if (TerminalTextEditorPasteController.RequiresLayout(input))
                                { var g = view.CaptureEditorGeometry(); e.HandleInput(input, new TerminalEditorVisualMapBuilder().BuildForTerminal(e.CaptureLayoutInput(), g), g.Identity); }
                                else e.HandleInput(input);
                        }
                    if (fixture.TryGetProperty("afterInsert", out var inserted)) { await Render(); e.InsertText(inserted.GetString()!); }
                    await Render(); var captured = e.CaptureLayoutInput(); var snapshot = e.Snapshot;
                    TerminalDefaultQualificationTests.Check(snapshot.Text == source.GetProperty("observation").GetProperty("text").GetString(), "Canonical original witness text changed");
                    TerminalDefaultQualificationTests.Check(e.GetExpandedText() == source.GetProperty("observation").GetProperty("expandedText").GetString(), "Canonical original witness expansion changed");
                    var sourceCursor = source.GetProperty("observation").GetProperty("cursor"); var offset = sourceCursor.GetProperty("col").GetInt32();
                    for (var line = 0; line < sourceCursor.GetProperty("line").GetInt32(); line++) offset += snapshot.Text.Split('\n')[line].Length + 1;
                    TerminalDefaultQualificationTests.Check(snapshot.CursorUtf16Offset == offset, "Canonical original witness cursor changed");
                    if (oracle.GetProperty("error").ValueKind != JsonValueKind.Null)
                    {
                        TerminalDefaultQualificationTests.Check(current is null && columns <= 2 && captured == e.CaptureLayoutInput(), "Source failure did not enter nonmutating host fallback"); rejected++;
                    }
                    else
                    {
                        TerminalDefaultQualificationTests.Check(current is not null, "Actual default frame missing"); actualRows = current!.SourceComponent.Rows.ToArray();
                        var rowsExpected = oracle.GetProperty("componentRows").EnumerateArray().Select(v => v.GetString()!).ToArray();
                        TerminalDefaultQualificationTests.Check(actualRows.SequenceEqual(rowsExpected), "Actual safe Source component differs from independently captured approved profile"); components++;
                        var normalized = oracle.GetProperty("normalizedComponentRows").EnumerateArray().Select(v => StripOwned(v.GetString()!)).ToArray();
                        expectedPhysicalRows = normalized.Skip(current.ComponentWindowOffset).Take(current.Frame.Rows.Length - current.EditorRowOrigin).ToArray();
                        physicalRows = current.Frame.Rows.Skip(current.EditorRowOrigin).Select(r => r.Text).ToArray();
                        TerminalDefaultQualificationTests.Check(physicalRows.SequenceEqual(expectedPhysicalRows), "Actual default physical editor rows differ from approved Source crop/normalization"); physical++;
                    }
                    WireGrammar.Check(string.Concat(sink.Writes), columns, rows);
                }
                catch (Exception ex) { failed++; error = ex.ToString(); }
                observations.Add(new { id, original = source.Clone(), approvedProfileSource = expected[id], canonical = e.CaptureLayoutInput(), actualRows, physicalRows, expectedPhysicalRows, actualDefaultFrames = presents, writes = sink.Writes.ToArray(), error, originalExpectedObjectsChanged = false });
                async ValueTask Render() { var capture = e.CaptureLayoutInput(); await view.SetDraftAsync(new(capture.Snapshot.Text, capture.Snapshot.CursorUtf16Offset) { Layout = capture }, default); }
            }
        }
        TerminalDefaultQualificationTests.Check(count == 63, "Witness set truncated"); return new(count, components, physical, rejected, failed, observations);
    }
    private static string StripOwned(string value) => value.Replace("\u001b_pi:c\a", "").Replace("\u001b[7m", "").Replace("\u001b[0m", "");
}
