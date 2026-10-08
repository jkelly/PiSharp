using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Cli.Interactive;
using PiSharp.Tui;
using PiSharp.Tui.Input;
using PiSharp.Tui.Rendering;

if (args.Length != 2 || File.Exists(args[1])) throw new ArgumentException("Frozen Source observations and fresh report path required");
using var source = JsonDocument.Parse(File.ReadAllText(args[0]));
var sourceCases = source.RootElement.GetProperty("cases").EnumerateArray().ToArray();
if (sourceCases.Length != 8 || sourceCases.Sum(c => c.GetProperty("steps").GetArrayLength()) != 75)
    throw new InvalidOperationException("Original corrected eight schedules/75 checkpoints differ");
var rows = new List<object>(); var joins = new List<object>(); var runtimeSegments = new List<object>(); var errors = new List<object>();
var failed = 0;
foreach (var schedule in sourceCases)
{
    var cluster = schedule.GetProperty("cluster").GetString()!;
    var offsets = StringInfo.ParseCombiningCharacters(cluster);
    runtimeSegments.Add(new { id = schedule.GetProperty("id").GetString(), cluster,
        runtime = "StringInfo.ParseCombiningCharacters", offsets,
        graphemes = offsets.Select((start, index) => cluster[start..(index + 1 < offsets.Length ? offsets[index + 1] : cluster.Length)]).ToArray(),
        sourceOffsets = schedule.GetProperty("clusterSegments").EnumerateArray().Select(s => s.GetProperty("index").GetInt32()).ToArray() });
}
foreach (var acknowledged in new[] { false, true })
foreach (var schedule in sourceCases)
{
    var owner = new TerminalEditorFocusOwner();
    ReplayHarness? host = null;
    var id = schedule.GetProperty("id").GetString();
    var route = acknowledged ? "acknowledged" : "legacy";
    try
    {
        host = await ReplayHarness.Start(acknowledged, owner);
        var index = 0;
        foreach (var expected in schedule.GetProperty("steps").EnumerateArray())
        {
            var operation = expected.GetProperty("operation");
            if (operation.GetProperty("kind").GetString() == "input")
                await host.Sink.Send(operation.GetProperty("raw").GetString()!);
            else if (operation.GetProperty("kind").GetString() == "resize")
            {
                host.Sink.Columns = operation.GetProperty("columns").GetInt32();
                await host.View.RefreshViewportAsync(CancellationToken.None);
            }
            else throw new InvalidOperationException("Unknown frozen Source operation");
            // Same-value focus is the accepted input-consumer observation barrier. No editor state is seeded.
            await owner.SetEditorFocusAsync(true).WaitAsync(TimeSpan.FromSeconds(10));
            var draft = host.Last ?? throw new InvalidOperationException("No actual native draft");
            var frame = host.Frame ?? throw new InvalidOperationException("No actual native view frame");
            var expectedText = expected.GetProperty("text").GetString()!;
            var expectedExpandedText = expected.GetProperty("expandedText").GetString()!;
            var expectedCursor = expected.GetProperty("cursorUtf16Offset").GetInt32();
            var expectedRows = expected.GetProperty("rows").EnumerateArray().Select(s => s.GetString()!).ToArray();
            var expectedChanges = expected.GetProperty("changes").EnumerateArray().Select(s => s.GetString()!).ToArray();
            var expectedSubmits = expected.GetProperty("submits").EnumerateArray().Select(s => s.GetString()!).ToArray();
            var textPass = draft.Text == expectedText;
            var expandedTextPass = draft.Text == expectedExpandedText;
            var cursorPass = draft.CursorUtf16Offset == expectedCursor;
            var rowsPass = frame.SourceComponent.Rows.SequenceEqual(expectedRows);
            var changesPass = host.Changes.SequenceEqual(expectedChanges);
            var submitsPass = host.Submits.SequenceEqual(expectedSubmits);
            var focusPass = draft.EditorOwnsFocus == expected.GetProperty("focused").GetBoolean() &&
                draft.EditorFocus is { } applied && owner.IsCurrent(applied) && draft.Layout!.Identity.EditorLifetimeId == applied.EditorLifetimeId;
            var hardwarePass = !frame.Frame.Cursor.Visible && ReplayHarness.Position(frame.Frame);
            var pass = textPass && expandedTextPass && cursorPass && rowsPass && changesPass && submitsPass && focusPass && hardwarePass;
            if (!pass) failed++;
            rows.Add(new { id, route, step = index++, operation = operation.Clone(), columns = host.Sink.Columns,
                expectedText, actualText = draft.Text, expectedExpandedText, actualExpandedText = draft.Text,
                expectedCursor, actualCursor = draft.CursorUtf16Offset, expectedRows, actualRows = frame.SourceComponent.Rows,
                expectedChanges, actualChanges = host.Changes.ToArray(), expectedSubmits, actualSubmits = host.Submits.ToArray(),
                expectedFocus = expected.GetProperty("focused").GetBoolean(), actualFocus = draft.EditorOwnsFocus,
                textPass, expandedTextPass, cursorPass, rowsPass, changesPass, submitsPass, focusPass, hardwarePass, pass });
        }
    }
    catch (Exception error) { errors.Add(new { id, route, error = error.ToString() }); }
    finally { if (host is not null) joins.Add(new { id, route, receipt = await host.Close() }); }
}
IndicGraphemeControls.Result supplemental;
try { supplemental = IndicGraphemeControls.Run(); }
catch (Exception error) { supplemental = new(1, 0, [], [new { id = "supplemental-harness-error", pass = false, error = error.ToString() }]); }
failed += supplemental.Failed;
var versions = new[] { typeof(TerminalInputDecoder).Assembly, typeof(TerminalChatInput).Assembly, Assembly.GetExecutingAssembly() }
    .Select(a => new { name = a.GetName().Name, path = a.Location, version = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion }).ToArray();
var complete = rows.Count == 150 && joins.Count == 16 && errors.Count == 0;
var report = new { sourceCases = 8, sourceCheckpoints = 75, actualRouteCheckpoints = rows.Count, failed, errors,
    runtimeSegments, rows, joins, supplemental, versions, framework = RuntimeInformation.FrameworkDescription,
    runtimeVersion = Environment.Version.ToString(), architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    coreLibrary = typeof(StringInfo).Assembly.Location, allOwnedExecutionsJoined = complete,
    productionEdits = true, baselineOnly = false, sourceStateSeeded = false,
    boundary = "Frozen corrected Source schedules replayed through actual TerminalChatInput legacy/acknowledged routes and TerminalSessionView; public terminal interface injections only. Same-value focus barriers retain Source component ownership. Callback field records distinct native draft text changes. Initial Home capture is preserved separately. No renderer transport, physical terminal or whole-package qualification.",
    fullNativeGate = false, physicalTerminal = false, originalPackageAcceptance = false };
File.WriteAllText(args[1], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
Console.WriteLine(JsonSerializer.Serialize(new { sourceCases = 8, sourceCheckpoints = 75, actualRouteCheckpoints = rows.Count, failed, errors = errors.Count,
    joinedRuns = joins.Count, allOwnedExecutionsJoined = complete, framework = RuntimeInformation.FrameworkDescription, versions }));
return complete ? failed == 0 ? 0 : 1 : 2;

internal sealed class ReplayHarness
{
    internal readonly ReplayTerminal Sink = new();
    internal readonly TerminalSessionView View;
    internal readonly CancellationTokenSource Stop = new();
    internal readonly TerminalSubmissionReceipts Receipts = new();
    internal TerminalDraftSnapshot? Last;
    internal TerminalEditorRenderedFrame? Frame;
    internal readonly List<string> Changes = [], Submits = [];
    internal Task<TerminalInputExit> Run = null!;
    internal int Ended, Interrupts;
    private ReplayHarness(TerminalEditorFocusOwner owner) => View = new(Sink, Sink, (_, frame) => Frame = frame, owner);
    internal static bool Position(TerminalFrame frame) => (bool)typeof(TerminalFrame).GetProperty("PositionCursor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(frame)!;
    internal static async Task<ReplayHarness> Start(bool acknowledged, TerminalEditorFocusOwner owner)
    {
        var host = new ReplayHarness(owner); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await host.View.StartAsync(CancellationToken.None);
        async ValueTask Paint(TerminalDraftSnapshot draft, CancellationToken token)
        { if (draft.Text != (host.Last?.Text ?? "")) host.Changes.Add(draft.Text); host.Last = draft; await host.View.SetDraftAsync(draft, token); entered.TrySetResult(); }
        Task<bool> Submit(string text, CancellationToken token) { host.Submits.Add(text); return Task.FromResult(true); }
        var input = new TerminalChatInput(host.Sink, null, owner);
        host.Run = acknowledged ? input.RunAcknowledgedAsync(async (line, token) =>
            { var keep = await Submit(line.Text, token); host.Receipts.FinishLocal(line, true); return keep; }, _ => Task.CompletedTask, Paint, host.Receipts,
            (editor, line) => editor.AddToHistory(line.Text), _ => { host.Ended++; host.Receipts.Complete(); }, () => host.Interrupts++, host.Stop.Token, host.View.CaptureEditorGeometry)
            : input.RunAsync(Submit, Paint, () => host.Interrupts++, host.Stop.Token);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch { await host.Close(); throw; }
        return host;
    }
    internal async Task<object> Close()
    {
        Sink.End(); Exception? error = null;
        try { await Run.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception e) { error = e; }
        finally { Receipts.Complete(); Stop.Cancel(); await View.DisposeAsync(); Stop.Dispose(); }
        var pass = error is null && Sink.ActiveReads == 0 && Sink.ActiveWrites == 0 && Sink.ReadsStarted == Sink.ReadsSettled && Sink.WritesStarted == Sink.WritesSettled && Sink.Disposals == 0;
        if (!pass) throw new InvalidOperationException("Native Indic baseline execution did not join: " + error);
        return new { Sink.ReadsStarted, Sink.ReadsSettled, Sink.ActiveReads, Sink.WritesStarted, Sink.WritesSettled, Sink.ActiveWrites, Sink.Disposals, Ended, Interrupts, pass };
    }
}

internal sealed class ReplayTerminal : IConsoleTerminal, ITerminalViewportSource
{
    private sealed record Packet(string? Text) { internal readonly TaskCompletionSource Admitted = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    private readonly Channel<Packet> packets = Channel.CreateUnbounded<Packet>();
    private Packet? previous;
    internal int ReadsStarted, ReadsSettled, ActiveReads, WritesStarted, WritesSettled, ActiveWrites, Disposals;
    internal int Columns = 20;
    public TerminalViewport ReadViewport() => new(Columns, 24, 0, 0, Columns, 24);
    public TerminalLeaseSnapshot Snapshot { get { var state = new TerminalConsoleState(0, 0, 65001, 65001, 25, true, 0, 0); return new(state, state, null, false, false, ActiveReads, ActiveWrites, ReadsStarted, ReadsSettled, WritesStarted, WritesSettled); } }
    internal async Task Send(string text) { var packet = new Packet(text); packets.Writer.TryWrite(packet); await packet.Admitted.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
    internal void End() => packets.Writer.TryWrite(new(null));
    public async ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default)
    {
        ReadsStarted++; ActiveReads++; previous?.Admitted.TrySetResult();
        try { var packet = await packets.Reader.ReadAsync(token); previous = packet; if (packet.Text is null) return 0; packet.Text.AsMemory().CopyTo(destination); return packet.Text.Length; }
        finally { ActiveReads--; ReadsSettled++; }
    }
    public ValueTask WriteAsync(ReadOnlyMemory<char> text, CancellationToken token = default)
    { WritesStarted++; ActiveWrites++; try { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; } finally { ActiveWrites--; WritesSettled++; } }
    public ValueTask DisposeAsync() { Disposals++; throw new InvalidOperationException("Borrowed terminal disposed"); }
}
