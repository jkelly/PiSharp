using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using PiSharp.Cli.Interactive;
using PiSharp.Tui;
using PiSharp.Tui.Input;
using PiSharp.Tui.Rendering;

if (args.Length != 2 || File.Exists(args[1])) throw new ArgumentException("Pinned Source focus observations and a fresh report path are required");
var failureFacts = new List<object>();
try
{
using var source = JsonDocument.Parse(File.ReadAllText(args[0]));
var primitiveRows = new List<object>(); var actualRows = new List<object>(); var joinedViews = new List<object>();
var primitiveFailed = 0; var actualFailed = 0; var ownershipFailed = 0;
var focusProperty = typeof(TerminalDraftSnapshot).GetProperty("EditorOwnsFocus", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
var positionProperty = typeof(TerminalFrame).GetProperty("PositionCursor", BindingFlags.Instance | BindingFlags.NonPublic)!;
var marker = "\u001b_pi:c\u0007";
var sourceCases = source.RootElement.GetProperty("cases").EnumerateArray().ToArray();
if (sourceCases.Length != 5) throw new InvalidOperationException("The original five public Source focus schedules changed");
foreach (var schedule in sourceCases)
{
    foreach (var editorName in new[] { "first", "second" })
    {
        var sink = new ProjectionTerminal();
        TerminalEditorRenderedFrame? observed = null;
        var view = new TerminalSessionView(sink, sink, (_, frame) => observed = frame);
        var editor = new TerminalTextEditorPasteController();
        var index = 0;
        try
        {
            await view.StartAsync(CancellationToken.None);
            foreach (var step in schedule.GetProperty("steps").EnumerateArray())
            {
                var operation = step.GetProperty("operation");
                if (operation.GetProperty("kind").GetString() == "recreate" &&
                    operation.GetProperty("target").GetString() == editorName)
                    editor = new TerminalTextEditorPasteController();
                var expected = step.GetProperty("editors").GetProperty(editorName);
                var text = expected.GetProperty("text").GetString()!;
                var expectedRows = expected.GetProperty("rows").EnumerateArray().Select(r => r.GetString()!).ToArray();
                var cursor = expected.GetProperty("cursor");
                var offset = cursor.GetProperty("col").GetInt32() +
                    text.Split('\n').Take(cursor.GetProperty("line").GetInt32()).Sum(line => line.Length + 1);
                var focused = expected.GetProperty("focused").GetBoolean();
                editor.SetText(text); editor.SetCursor(offset);
                var captured = editor.CaptureLayoutInput();
                var geometry = new TerminalEditorGeometrySnapshot(new(Guid.NewGuid(), 1,
                    TerminalEditorVisualMapBuilder.SourcePolicyId), 20, 24);
                var map = new TerminalEditorVisualMapBuilder().BuildForTerminal(captured, geometry);
                var primitive = TerminalEditorFrameFactory.Create(map, 0, ImmutableArray<string>.Empty, focused);
                var primitivePositions = (bool)positionProperty.GetValue(primitive.Frame)!;
                var primitivePass = primitive.SourceComponent.Rows.SequenceEqual(expectedRows) &&
                    primitivePositions == focused && !primitive.Frame.Cursor.Visible;
                if (!primitivePass) primitiveFailed++;
                primitiveRows.Add(new { id = schedule.GetProperty("id").GetString(), editorName, step = index,
                    focused, seededCanonicalSourceState = true, expectedRows, actualRows = primitive.SourceComponent.Rows,
                    positionsTrustedHiddenCursor = primitivePositions, hiddenCursorPolicyPreserved = !primitive.Frame.Cursor.Visible,
                    pass = primitivePass });

                var snapshot = new TerminalDraftSnapshot(text, offset) { Layout = captured };
                var focusRequestRepresentable = focusProperty is not null;
                if (focusProperty is not null) focusProperty.SetValue(snapshot, focused);
                observed = null;
                await view.SetDraftAsync(snapshot, CancellationToken.None);
                var actual = observed ?? throw new InvalidOperationException("Actual view did not invoke its source-frame observer");
                var positions = (bool)positionProperty.GetValue(actual.Frame)!;
                var actualPass = actual.SourceComponent.Rows.SequenceEqual(expectedRows) && positions == focused && !actual.Frame.Cursor.Visible;
                if (!actualPass) actualFailed++;
                actualRows.Add(new { id = schedule.GetProperty("id").GetString(), editorName, step = index++,
                    operation = operation.Clone(), expectedEditorOwnsFocus = focused, focusRequestRepresentable,
                    seededCanonicalSourceState = true, expectedRows, actualRows = actual.SourceComponent.Rows,
                    expectedMarkerPresent = focused, actualMarkerPresent = actual.SourceComponent.Rows.Any(r => r.Contains(marker, StringComparison.Ordinal)),
                    positionsTrustedHiddenCursor = positions, hiddenCursorPolicyPreserved = !actual.Frame.Cursor.Visible,
                    pass = actualPass });
            }
        }
        finally { await view.DisposeAsync(); }
        var joined = sink.ActiveWrites == 0 && sink.StartedWrites == sink.SettledWrites && sink.Disposals == 0;
        if (!joined) ownershipFailed++;
        joinedViews.Add(new { id = schedule.GetProperty("id").GetString(), editorName, sink.StartedWrites,
            sink.SettledWrites, sink.ActiveWrites, sink.Disposals, joined });
    }
}
var windows = sourceCases.Single(c => c.GetProperty("id").GetString() == "terminal-window-reports-are-not-component-focus");
var windowChecks = new List<object>();
foreach (var step in windows.GetProperty("steps").EnumerateArray())
{
    var operation = step.GetProperty("operation");
    if (!operation.TryGetProperty("raw", out var rawElement) || rawElement.GetString() is not ("\u001b[I" or "\u001b[O")) continue;
    var raw = rawElement.GetString()!; var decoder = new TerminalInputDecoder();
    var events = decoder.Feed(raw.AsSpan()).Concat(decoder.Complete()).ToArray();
    var pass = events.Length == 1 && events[0] is TerminalProtocol protocol && protocol.Sequence == raw &&
        step.GetProperty("editors").GetProperty("first").GetProperty("focused").GetBoolean();
    windowChecks.Add(new { raw, sourceKeepsComponentOwner = true, decoded = events[0].GetType().Name, pass });
    if (!pass) ownershipFailed++;
}
var actualOwnership = await FocusOwnershipTests.Run(source.RootElement, facts => failureFacts.Add(facts));
var actualCaller = await FocusCallerTests.Run(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
var actualCallerFailed = JsonSerializer.SerializeToElement(actualCaller).GetProperty("pass").GetBoolean() ? 0 : 1;
var actualOwnershipFailed = (int)actualOwnership.GetType().GetProperty("failed")!.GetValue(actualOwnership)!;
var failed = primitiveFailed + actualFailed + ownershipFailed + actualOwnershipFailed + actualCallerFailed;
var versions = new[] { typeof(TerminalInputDecoder).Assembly, typeof(TerminalChatInput).Assembly }
    .Select(a => new { name = a.GetName().Name, version = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion }).ToArray();
File.WriteAllText(args[1], JsonSerializer.Serialize(new { sourceCases = sourceCases.Length, sourceCheckpoints = 37,
    sourceEditorStateProjections = primitiveRows.Count, primitiveFailed, actualViewFailed = actualFailed,
    ownershipFailed, focusCarrierAvailable = focusProperty is not null, primitiveRows, actualRows, joinedViews, windowChecks,
    versions, actualOwnership, actualCaller, actualCallerFailed, actualOwnershipFailed, failed, allOwnedExecutionsJoined = true, productionEdits = true,
    boundary = "Original74canonical state projections retain public SetText/SetCursor seeding and unchanged Source expected objects; actual input ownership is separately replayed through both input routes with the same five Source schedules. Physical focus/IME and whole-package gates remain open.",
    fullNativeGate = false, physicalTerminal = false, originalPackageAcceptance = false }, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
Console.WriteLine(JsonSerializer.Serialize(new { sourceCases = sourceCases.Length, sourceCheckpoints = 37,
    sourceEditorStateProjections = primitiveRows.Count, primitiveFailed, actualViewFailed = actualFailed, ownershipFailed,
    focusCarrierAvailable = focusProperty is not null, joinedViews = joinedViews.Count, versions, actualCallerFailed, actualOwnershipFailed, failed }));
return failed == 0 ? 0 : 1;
}
catch (Exception error)
{
    // Fixed scalar facts only: no exception message, stack, Data, paths or source payloads.
    var report = JsonSerializer.Serialize(new { status = "incomplete", passed = false, failed = 1,
        failureKind = error is OperationCanceledException ? "canceled" : error is InvalidOperationException ? "invalid-operation" : "other",
        failureFacts = failureFacts.Take(1).ToArray(), fullNativeGate = false, physicalTerminal = false,
        originalPackageAcceptance = false });
    using var output = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    using var writer = new StreamWriter(output); writer.WriteLine(report); writer.Flush(); output.Flush(flushToDisk: true);
    Console.Error.WriteLine("Focus consumer incomplete; bounded failure report written after owned cleanup.");
    return 1;
}

internal sealed class ProjectionTerminal : IConsoleTerminal, ITerminalViewportSource
{
    internal int StartedWrites, SettledWrites, ActiveWrites, Disposals;
    public TerminalLeaseSnapshot Snapshot
    {
        get { var s = new TerminalConsoleState(0, 0, 65001, 65001, 25, true, 0, 0);
            return new(s, s, null, false, false, 0, ActiveWrites, 0, 0, StartedWrites, SettledWrites); }
    }
    public TerminalViewport ReadViewport() => new(20, 24, 0, 0, 20, 24);
    public ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default) =>
        throw new InvalidOperationException("Projection-only witness performs no terminal reads");
    public ValueTask WriteAsync(ReadOnlyMemory<char> frame, CancellationToken token = default)
    {
        StartedWrites++; ActiveWrites++;
        try { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
        finally { ActiveWrites--; SettledWrites++; }
    }
    public ValueTask DisposeAsync()
    { Disposals++; throw new InvalidOperationException("Borrowed projection terminal was disposed"); }
}
