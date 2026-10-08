using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using PiSharp.Cli.Interactive;
using PiSharp.Tui;
using PiSharp.Tui.Input;
using PiSharp.Tui.Rendering;

internal static partial class TerminalDefaultSourceReplayTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(8);
    internal static readonly List<object> Evidence = [];
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("terminal-session.default-source actual input and view preserve all40 complete745 checkpoints", CompleteSourceReplay),
        ("terminal-session.default-source CJK narrow-window rejection preserves owner and resize recovers", NarrowRecovery),
        ("terminal-session.default-source ordinary scroll survives edit and identical reset reaches view", ScrollReset),
        ("terminal-session.default-source control pictures share wrap navigation and caret with canonical submission", ControlPictureNavigation)
    ];

    private static async Task CompleteSourceReplay()
    {
        var pins = new[]
        {
            ("frozen-history-layout-source.json", 828465L, "7e869755af3163ce02df6643e13c76443db2813eb4c12f9b2e5bf364b2d54d8a"),
            ("supplementary-history-layout-source-observations.json", 101883L, "4a57d0f2421754d01925cfa6529d56abbf765b1c110c0136dad6cc550e7b058a"),
            ("rgi-layout-source-observations.json", 77412L, "d1d623a3be6c26e0b159b52cae8f5ef5b2633dd4a32d36fda090083af74893d9")
        };
        var cases = new List<object>(); var files = new List<object>();
        var checkpoints = 0; var differentCases = 0; var differentCheckpoints = 0; var approvedCases = 0; var approvedCheckpoints = 0;
        foreach (var (name, bytes, sha256) in pins)
        {
            var file = Path.Combine(AppContext.BaseDirectory, "Fixtures", "terminal-default-source", name);
            var raw = await File.ReadAllBytesAsync(file);
            Check(raw.LongLength == bytes && Convert.ToHexStringLower(SHA256.HashData(raw)) == sha256,
                "Original Source fixture bytes changed: " + name);
            files.Add(new { name, bytes, sha256 });
            using var document = JsonDocument.Parse(raw);
            foreach (var fixture in document.RootElement.GetProperty("cases").EnumerateArray())
            {
                var id = fixture.GetProperty("id").GetString()!;
                var expected = fixture.GetProperty("observation").Clone();
                var approvedExpected = ApprovedObservation(id, expected);
                var steps = new List<object>(); var errors = new List<object>();
                var renderCalls = 0; int? lastRenderColumns = null; string[]? lastRenderedRows = null;
                var harness = new Harness(81, 24); var stepIndex = 0;
                try
                {
                    await harness.Start();
                    foreach (var step in fixture.GetProperty("input").GetProperty("steps").EnumerateArray())
                    {
                        var kind = step.GetProperty("kind").GetString();
                        Capture capture;
                        switch (kind)
                        {
                            case "history": capture = await harness.Control(editor => editor.AddToHistory(step.GetProperty("data").GetString()!)); await harness.Repaint(capture.Draft); break;
                            case "set": capture = await harness.Control(editor => editor.SetText(step.GetProperty("data").GetString()!)); await harness.Repaint(capture.Draft); break;
                            case "insert": capture = await harness.Control(editor => editor.InsertText(step.GetProperty("data").GetString()!)); await harness.Repaint(capture.Draft); break;
                            case "input":
                                await harness.Input(step.GetProperty("data").GetString()!);
                                capture = await harness.Control(); break;
                            case "render":
                                var columns = step.GetProperty("columns").GetInt32(); var rows = step.GetProperty("terminalRows").GetInt32();
                                harness.Console.Viewport = new(columns, rows, 0, 0, columns, rows);
                                capture = await harness.Control(); await harness.Repaint(capture.Draft);
                                var presented = harness.LastPresented ?? throw new InvalidOperationException("Source checkpoint did not enter actual default factory.");
                                lastRenderedRows = presented.Frame.SourceComponent.Rows.ToArray(); lastRenderColumns = columns; renderCalls++; break;
                            default: throw new InvalidOperationException("Unknown original Source operation.");
                        }
                        var text = capture.Draft.Text; var offset = capture.Draft.CursorUtf16Offset; var line = 0; var col = 0;
                        for (var at = 0; at < offset; at++) { if (text[at] == '\n') { line++; col = 0; } else col++; }
                        steps.Add(new { text, expandedText = capture.Expanded, cursor = new { line, col }, cursorUtf16Offset = offset,
                            changes = capture.Changes, submits = capture.Submits, renderRequests = 0, renderCalls, lastRenderColumns,
                            terminalRows = harness.Console.Viewport.Rows, lastRenderedRows });
                        stepIndex++;
                    }
                }
                catch (Exception error) { errors.Add(new { stepIndex, error = error.ToString() }); }
                finally { await harness.DisposeAsync(); }
                if (harness.JoinFailure is not null) errors.Add(new { stepIndex, error = harness.JoinFailure });
                var actual = JsonSerializer.SerializeToElement(new { steps });
                var differences = Differences(approvedExpected, actual); var originalDifferences = Differences(expected, actual);
                for (var at = 0; at < expected.GetProperty("steps").GetArrayLength(); at++)
                {
                    if (at >= steps.Count) { differentCheckpoints++; continue; }
                    var projectedDifferences = Differences(approvedExpected.GetProperty("steps")[at], actual.GetProperty("steps")[at]);
                    if (projectedDifferences.Count != 0) differentCheckpoints++;
                    if (Differences(expected.GetProperty("steps")[at], approvedExpected.GetProperty("steps")[at]).Count != 0) approvedCheckpoints++;
                }
                if (Differences(expected, approvedExpected).Count != 0) approvedCases++;
                checkpoints += expected.GetProperty("steps").GetArrayLength();
                var matches = differences.Count == 0 && errors.Count == 0;
                if (!matches) differentCases++;
                cases.Add(new { id, input = fixture.GetProperty("input").Clone(), originalExpected = expected, approvedExpected, actual,
                    matches, differences, originalDifferences, errors, physicalFrames = harness.Frames, writes = harness.Console.Written,
                    joined = harness.JoinEvidence, productionInputDispatch = true, productionViewFactorySelection = true,
                    fixtureHostHistoryAdmission = "Only original explicit history steps; physical Enter callback rejects its local receipt, as the original fixture does not admit Enter to history." });
            }
        }
        var report = new { boundary = "Actual TerminalChatInput.RunAcknowledgedAsync and actual TerminalSessionView default BuildForTerminal/factory/render transport. One input-owned controller, original fixture setup serialized through existing receipt callback.",
            originalSourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f", originalSourceExpectedObjectsChanged = false,
            physicalRenderCountSeparateFromSourceFixtureCounters = true, sourceFiles = files, sourceCases = cases.Count, checkpoints,
            differentCases, differentCheckpoints, explicitApprovedCases = approvedCases, explicitApprovedCheckpoints = approvedCheckpoints,
            safetyApproval = "TERMINAL_SOURCE_VIEW_RENDERER_APPROVED_SAFETY_ADDENDUM_R3.json SHA256 2ec6dd6b67c1aabe459f0007f778fbf8d50a13f6cb528ee5202aa74daeddbd3b",
            completeSessionHostAcceptanceQualified = false, cases };
        Evidence.Add(report);
        var output = Environment.GetEnvironmentVariable("PISHARP_TERMINAL_DEFAULT_REPLAY_REPORT");
        if (output is not null) await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Check(cases.Count == 40 && checkpoints == 745 && approvedCases == 1 && approvedCheckpoints == 3,
            "Default replay did not retain the exact40/745 corpus and one explicit three-checkpoint display decision.");
        Check(differentCases == 0 && differentCheckpoints == 0, $"Actual default Source replay differs: {differentCases} cases, {differentCheckpoints} checkpoints. Full original/approved/actual evidence retained.");
    }

    private static JsonElement ApprovedObservation(string id, JsonElement original)
    {
        if (id != "history-ecmascript-trim-preserves-nel-and-raw-line-controls") return original;
        var originalRows = JsonSerializer.SerializeToElement(new[] { "────────────────", "\u001b_pi:c\a\u001b[7m\u0085\u001b[0mA\u0085               ", "────────────────" });
        var displayRows = new[] { "────────────────", "\u001b_pi:c\a\u001b[7m\u2426\u001b[0mA\u2426             ", "────────────────" };
        var approved = JsonNode.Parse(original.GetRawText())!;
        for (var at = 7; at <= 9; at++)
        {
            Check(Differences(originalRows, original.GetProperty("steps")[at].GetProperty("lastRenderedRows")).Count == 0,
                "Original raw-NEL Source witness changed; approval cannot silently match a new oracle.");
            approved["steps"]![at]!["lastRenderedRows"] = JsonSerializer.SerializeToNode(displayRows);
        }
        return JsonSerializer.SerializeToElement(approved);
    }

    private sealed record Capture(TerminalDraftSnapshot Draft, string Expanded, string[] Changes, string[] Submits, int HistoryIndex, string[] History);
    private sealed record Presented(TerminalEditorVisualMap Map, TerminalEditorRenderedFrame Frame);
    private sealed class Harness : IAsyncDisposable
    {
        internal readonly ConsoleFixture Console;
        private readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(90));
        private readonly TerminalSubmissionReceipts receipts = new();
        private readonly TerminalSessionView view;
        private readonly Channel<TerminalDraftSnapshot> rendered = Channel.CreateBounded<TerminalDraftSnapshot>(32);
        private readonly Dictionary<TerminalSubmissionIdentity, Action<TerminalTextEditorPasteController>> operations = [];
        private readonly object operationGate = new();
        private readonly List<string> changes = [], submits = [];
        private readonly TerminalInputDecoder barrierDecoder = new(timeProvider: new FixedClock());
        private Task<TerminalInputExit>? running;
        private bool subscribed, closed; private int inputEnded;
        internal Presented? LastPresented;
        internal readonly List<object> Frames = [];
        internal string? JoinFailure;
        internal object? JoinEvidence;
        internal Harness(int columns, int rows)
        {
            Console = new(columns, rows);
            view = new(Console, Console, (map, frame) =>
            {
                Check(map.PolicyId == TerminalEditorVisualMapBuilder.SourcePolicyId && map.SourceText is not null,
                    "Actual default selected a different geometry policy.");
                foreach (var row in frame.Frame.Rows) Check(!row.Text.Any(c => c < 32 || c is >= '\u007f' and <= '\u009f'), "Unsafe data control reached actual default physical row.");
                LastPresented = new(map, frame);
                Frames.Add(new { ordinal = Frames.Count, canonicalText = map.SourceText, canonicalCursor = map.SourceCursorUtf16Offset,
                    map.EditorIdentity, map.GeometryIdentity, map.ScrollResetRevision, map.ContentColumns, map.ViewportRows,
                    displayRows = map.RenderedRows, sourceComponent = frame.SourceComponent, frame.ComponentWindowOffset,
                    frame.FirstVisibleSourceRow, frame.EditorRowOrigin, frame.TotalSourceRows,
                    physicalFrame = new { frame.Frame.Columns, frame.Frame.Rows, frame.Frame.Cursor, frame.Frame.IsClipped,
                        positionCursor = Metadata(frame.Frame, "PositionCursor"), inverseSpan = Metadata(frame.Frame, "InverseSpan") } });
            });
        }
        internal async Task Start()
        {
            await view.StartAsync(stop.Token);
            running = new TerminalChatInput(Console).RunAcknowledgedAsync((submitted, _) =>
                { submits.Add(submitted.Text); receipts.FinishLocal(submitted, acknowledged: false); return Task.FromResult(true); },
                _ => Task.CompletedTask, Render, receipts, (editor, accepted) =>
                {
                    Action<TerminalTextEditorPasteController> operation;
                    lock (operationGate) { Check(operations.Remove(accepted.Identity, out var selected), "Unexpected production acknowledgment in Source setup fixture."); operation = selected!; }
                    operation(editor);
                }, _ => { Interlocked.Increment(ref inputEnded); receipts.Complete(); }, stop.Cancel, stop.Token, view.CaptureEditorGeometry);
            await NextRender();
            await Control();
        }
        internal async Task<Capture> Control(Action<TerminalTextEditorPasteController>? change = null)
        {
            var ready = new TaskCompletionSource<Capture>(TaskCreationOptions.RunContinuationsAsynchronously);
            var line = receipts.Reserve("source-fixture-operation");
            lock (operationGate) operations.Add(line.Identity, editor =>
            {
                try
                {
                    if (!subscribed) { editor.TextChanged += changes.Add; subscribed = true; }
                    change?.Invoke(editor);
                    var state = editor.Snapshot;
                    ready.SetResult(new(new(state.Text, state.CursorUtf16Offset) { Layout = editor.CaptureLayoutInput() },
                        editor.GetExpandedText(), changes.ToArray(), submits.ToArray(), editor.HistoryIndex, editor.History.ToArray()));
                }
                catch (Exception error) { ready.TrySetException(error); throw; }
            });
            receipts.FinishLocal(line, acknowledged: true, command: "source-fixture-operation");
            return await ready.Task.WaitAsync(Bound);
        }
        internal async Task Input(string wire)
        {
            Check(wire.Length is > 0 and <= 4096, "Original physical input exceeds its declared chunk bound.");
            var counted = barrierDecoder.Feed(wire);
            var isolated = new TerminalInputDecoder(timeProvider: new FixedClock());
            var fixtureEvents = isolated.Feed(wire).AddRange(isolated.Complete());
            Check(JsonSerializer.Serialize(counted.Select(v => JsonSerializer.SerializeToElement(v, v.GetType()))) ==
                JsonSerializer.Serialize(fixtureEvents.Select(v => JsonSerializer.SerializeToElement(v, v.GetType()))),
                "Original per-step decoder fixture is not a complete physical chunk; no artificial production EOF will be inserted.");
            Check(counted.Length is > 0 and <= 32, "Original physical input exceeds the owned event queue.");
            await Console.Feed(wire, stop.Token);
            for (var at = 0; at < counted.Length; at++) await NextRender();
        }
        private async Task NextRender() { _ = await rendered.Reader.ReadAsync(stop.Token).AsTask().WaitAsync(Bound); }
        private async ValueTask Render(TerminalDraftSnapshot draft, CancellationToken token)
        { await Repaint(draft, token); Check(rendered.Writer.TryWrite(draft), "Actual render-completion observations exceeded their bound."); }
        internal async Task Repaint(TerminalDraftSnapshot draft, CancellationToken token = default)
        { LastPresented = null; await view.SetDraftAsync(draft, token == default ? stop.Token : token); }
        internal async Task Resize(int columns, int rows)
        { Console.Viewport = new(columns, rows, 0, 0, columns, rows); LastPresented = null; await view.RefreshViewportAsync(stop.Token); }
        public async ValueTask DisposeAsync()
        {
            if (closed) return; closed = true;
            receipts.Complete(); Console.Complete(); stop.Cancel();
            try { if (running is not null) await running; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception error) { JoinFailure = error.ToString(); }
            try { await view.DisposeAsync(); Console.AssertJoined(); ApprovedCommandsOnly(string.Join("", Console.Written));
                Check(receipts.Snapshot == (0, 0, 0) && operations.Count == 0 && inputEnded == 1, "Actual input owner retained receipt/setup work or end notification.");
                var writes = Console.Written;
                Check(writes[0] == "\u001b[?1049h\u001b[?2004h" && writes[^1] == "\u001b[?2004l\u001b[?1049l", "Borrowed terminal modes were not joined and restored.");
            } catch (Exception error) { JoinFailure ??= error.ToString(); }
            JoinEvidence = new { Console.Snapshot, receipts = receipts.Snapshot, retainedFixtureOperations = operations.Count, inputEnded, failure = JoinFailure,
                borrowedConsoleDisposed = Console.Disposals != 0 };
            stop.Dispose();
        }
    }

    private sealed class ConsoleFixture(int columns, int rows) : IConsoleTerminal, ITerminalViewportSource
    {
        private readonly Channel<string> input = Channel.CreateBounded<string>(2);
        private readonly object gate = new(); private readonly List<string> writes = [];
        private int readsActive, writesActive; private long readsStarted, readsSettled, writesStarted, writesSettled;
        private static readonly TerminalConsoleState State = new(0, 0, 65001, 65001, 25, true, 0, 0);
        internal TerminalViewport Viewport = new(columns, rows, 0, 0, columns, rows);
        internal int Disposals;
        internal string[] Written { get { lock (gate) return writes.ToArray(); } }
        public TerminalLeaseSnapshot Snapshot { get { lock (gate) return new(State, State, null, false, false, readsActive, writesActive, readsStarted, readsSettled, writesStarted, writesSettled); } }
        public TerminalViewport ReadViewport() => Viewport;
        internal ValueTask Feed(string value, CancellationToken token) => input.Writer.WriteAsync(value, token);
        internal void Complete() => input.Writer.TryComplete();
        public async ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default)
        {
            lock (gate) { Check(readsActive == 0, "Concurrent borrowed-console physical reads."); readsActive++; readsStarted++; }
            try
            {
                if (!await input.Reader.WaitToReadAsync(token)) return 0;
                Check(input.Reader.TryRead(out var value), "Missing physical input chunk.");
                var text = value!; Check(text.Length <= destination.Length, "Physical chunk exceeds input buffer."); text.AsMemory().CopyTo(destination); return text.Length;
            }
            finally { lock (gate) { readsActive--; readsSettled++; } }
        }
        public ValueTask WriteAsync(ReadOnlyMemory<char> value, CancellationToken token = default)
        {
            lock (gate) { Check(writesActive == 0, "Concurrent borrowed-console physical writes."); writesActive++; writesStarted++; }
            try { token.ThrowIfCancellationRequested(); lock (gate) writes.Add(value.ToString()); return ValueTask.CompletedTask; }
            finally { lock (gate) { writesActive--; writesSettled++; } }
        }
        internal void AssertJoined() { lock (gate) Check(readsActive == 0 && writesActive == 0 && readsStarted == readsSettled && writesStarted == writesSettled && Disposals == 0, "Borrowed physical work remained active or was disposed."); }
        public ValueTask DisposeAsync() { Disposals++; throw new InvalidOperationException("Borrowed fixture console must remain owned by its host."); }
    }
    private static object? Metadata(TerminalFrame frame, string name) => typeof(TerminalFrame).GetProperty(name,
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(frame);
    private static void ApprovedCommandsOnly(string wire)
    {
        for (var at = 0; at < wire.Length; at++)
        {
            var value = wire[at];
            if (value != '\u001b') { Check(value >= 32 && value is not (>= '\u007f' and <= '\u009f'), "Untrusted data control on actual default wire."); continue; }
            var end = wire.IndexOfAny(['m', 'J', 'K', 'H', 'h', 'l'], at + 1); Check(end >= 0, "Unfinished actual default command.");
            var packet = wire[at..(end + 1)];
            Check(packet is "\u001b[0m" or "\u001b[7m" or "\u001b[2J" or "\u001b[2K" or "\u001b[?25h" or "\u001b[?25l" or
                "\u001b[?1049h" or "\u001b[?1049l" or "\u001b[?2004h" or "\u001b[?2004l" ||
                Regex.IsMatch(packet, "^\\x1b\\[[1-9][0-9]*;[1-9][0-9]*H$"), "Unauthorized actual default terminal command: " + JsonSerializer.Serialize(packet));
            at = end;
        }
    }
    private static List<object> Differences(JsonElement expected, JsonElement actual)
    {
        var found = new List<object>(); Visit(expected, actual, "$"); return found;
        void Visit(JsonElement left, JsonElement right, string path)
        {
            if (left.ValueKind != right.ValueKind) { Add(); return; }
            if (left.ValueKind == JsonValueKind.Object)
            {
                var names = left.EnumerateObject().Select(v => v.Name).Union(right.EnumerateObject().Select(v => v.Name), StringComparer.Ordinal);
                foreach (var name in names)
                    if (left.TryGetProperty(name, out var l) && right.TryGetProperty(name, out var r)) Visit(l, r, path + "." + name);
                    else found.Add(new { path = path + "." + name, expected = left.TryGetProperty(name, out l) ? l.Clone() : (JsonElement?)null,
                        actual = right.TryGetProperty(name, out r) ? r.Clone() : (JsonElement?)null });
                return;
            }
            if (left.ValueKind == JsonValueKind.Array)
            {
                if (left.GetArrayLength() != right.GetArrayLength()) { Add(); return; }
                for (var at = 0; at < left.GetArrayLength(); at++) Visit(left[at], right[at], path + "[" + at + "]"); return;
            }
            if (left.ValueKind == JsonValueKind.String ? left.GetString() != right.GetString() : left.GetRawText() != right.GetRawText()) Add();
            void Add() => found.Add(new { path, expected = left.Clone(), actual = right.Clone() });
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class FixedClock : TimeProvider { public override long TimestampFrequency => 1000; public override long GetTimestamp() => 0; }
}
