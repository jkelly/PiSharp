using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Interactive;
using PiSharp.Contracts;
using PiSharp.Tui;
using PiSharp.Tui.Input;

internal static class TerminalAcknowledgedCallerTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(12);
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases(string host, string cli) =>
    [
        ("terminal-caller actual RPC acknowledgment enables recall and restores the unsent draft", () => Recall(host, cli)),
        ("terminal-caller actual rejected switch never enters recall", () => Rejected(host, cli)),
        ("terminal-caller EOF closes admission while actual output callback and receipt owner are joined", () => LateEof(host, cli)),
        ("terminal-caller actual durable replacement acknowledges its command before recall", () => Replacement(host, cli)),
        ("terminal-caller actual view geometry rejects stale and foreign maps before editor effects", Geometry),
        ("terminal-caller actual immutable draft capture drives rendering and wrapped navigation", CapturedLayout)
    ];

    private static async Task Recall(string host, string cli)
    {
        var files = await TerminalSessionCommandTests.WorkflowFiles.Create(host, cli, plugin: false);
        var terminal = new ScreenConsole(); using var error = new StringWriter(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var acknowledged = NewGate(); var history = NewGate(); var line = "accepted:" + files.Nonce;
        var running = TerminalSessionCommand.RunObservedAsync(files.Args(), terminal, terminal, error, Observe, stop.Token, observeInputCompletion: terminal.ObserveInputCompletion);
        try
        {
            await history.Task.WaitAsync(Bound); await terminal.Feed(line + "\r"); await acknowledged.Task.WaitAsync(Bound);
            await terminal.WaitScreen(rows => TerminalSourceAsciiExpectations.RowsMatch(rows, 96, ""), stop.Token);
            await terminal.Feed("unsent\u001b[H\u001b[A");
            await terminal.WaitScreen(rows => TerminalSourceAsciiExpectations.RowsMatch(rows, 96, line), stop.Token);
            await terminal.Feed("\u001b[B"); await terminal.WaitScreen(rows => TerminalSourceAsciiExpectations.RowsMatch(rows, 96, "unsent"), stop.Token);
            await terminal.Feed("\u001b"); await terminal.EscapeConsumed.Task.WaitAsync(Bound); await terminal.WaitScreen(rows => TerminalSourceAsciiExpectations.RowsMatch(rows, 96, "unsent"), stop.Token);
            await terminal.Feed("\u0003"); await terminal.WaitScreen(rows => TerminalSourceAsciiExpectations.RowsMatch(rows, 96, ""), stop.Token);
            await terminal.Feed("\u0004"); Check(await running.WaitAsync(Bound) == 0 && error.ToString() == "", "Acknowledged recall command failed.");
            var log = await TerminalSessionCommandTests.Complete(files.Session);
            var context = TerminalSessionCommandTests.Context(log);
            Check(context.Messages.Count(message => message.Role == "user") == 1 &&
                context.Messages.Any(message => message.Role == "user" && UserText(message.WireBody.Value) == line), "Recall or restored draft was submitted without Enter.");
            terminal.AssertJoined(); await files.Retain(new { scenario = "actual-acknowledged-recall", line, restoredUnsentDraft = true, idleEscapeConsumed = terminal.EscapeConsumed.Task.IsCompletedSuccessfully, terminal.Snapshot });
        }
        finally { stop.Cancel(); terminal.Complete(); await running; terminal.AssertJoined(); }
        ValueTask Observe(JsonData record, CancellationToken token)
        { var body = record.Value; if (IsResponse(body, "get_messages")) history.TrySetResult(); if (IsResponse(body, "prompt")) { Check(body.GetProperty("success").GetBoolean(), "Prompt was rejected."); acknowledged.TrySetResult(); } return ValueTask.CompletedTask; }
    }

    private static async Task Rejected(string host, string cli)
    {
        var files = await TerminalSessionCommandTests.WorkflowFiles.Create(host, cli, plugin: false);
        var terminal = new ScreenConsole(); using var error = new StringWriter(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var rejected = NewGate(); var history = NewGate(); var running = TerminalSessionCommand.RunObservedAsync(files.Args(), terminal, terminal, error, Observe, stop.Token, observeInputCompletion: terminal.ObserveInputCompletion);
        try
        {
            await history.Task.WaitAsync(Bound); await terminal.Feed("/switch " + Path.Combine(files.Root, "absent.jsonl") + "\r");
            await rejected.Task.WaitAsync(Bound); await terminal.WaitScreen(rows => TerminalSourceAsciiExpectations.RowsMatch(rows, 96, ""), stop.Token);
            await terminal.Feed("\u001b[Az"); await terminal.WaitScreen(rows => TerminalSourceAsciiExpectations.RowsMatch(rows, 96, "z"), stop.Token);
            await terminal.Feed("\u001b"); await terminal.EscapeConsumed.Task.WaitAsync(Bound); await terminal.WaitScreen(rows => TerminalSourceAsciiExpectations.RowsMatch(rows, 96, "z"), stop.Token);
            await terminal.Feed("\u0003"); await terminal.WaitScreen(rows => TerminalSourceAsciiExpectations.RowsMatch(rows, 96, ""), stop.Token);
            await terminal.Feed("\u0004"); Check(await running.WaitAsync(Bound) == 0 && error.ToString() == "", "Rejected switch did not permit clean input shutdown.");
            await TerminalSessionCommandTests.Complete(files.Session); terminal.AssertJoined();
            await files.Retain(new { scenario = "actual-rejected-switch-no-recall", idleEscapeConsumed = terminal.EscapeConsumed.Task.IsCompletedSuccessfully, terminal.Snapshot });
        }
        finally { stop.Cancel(); terminal.Complete(); await running; terminal.AssertJoined(); }
        ValueTask Observe(JsonData record, CancellationToken token)
        { var body = record.Value; if (IsResponse(body, "get_messages")) history.TrySetResult(); if (IsResponse(body, "switch_session")) { Check(!body.GetProperty("success").GetBoolean(), "Absent switch unexpectedly succeeded."); rejected.TrySetResult(); } return ValueTask.CompletedTask; }
    }

    private static async Task LateEof(string host, string cli)
    {
        var files = await TerminalSessionCommandTests.WorkflowFiles.Create(host, cli, plugin: false);
        var terminal = new ScreenConsole(); using var error = new StringWriter(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var held = NewGate(); var release = NewGate(); var history = NewGate(); var line = "late:" + files.Nonce;
        var running = TerminalSessionCommand.RunObservedAsync(files.Args(), terminal, terminal, error, Observe, stop.Token, observeInputCompletion: terminal.ObserveInputCompletion);
        try
        {
            await history.Task.WaitAsync(Bound); await terminal.Feed(line + "\r"); await held.Task.WaitAsync(Bound);
            terminal.Complete(); await terminal.Eof.Task.WaitAsync(Bound);
            Check(!running.IsCompleted && terminal.Snapshot.ActiveReads == 0, "EOF bypassed held output or detached physical input.");
            release.TrySetResult(); Check(await running.WaitAsync(Bound) == 0 && error.ToString() == "", "Late-response EOF failed or deadlocked receipt completion.");
            var log = await TerminalSessionCommandTests.Complete(files.Session);
            Check(TerminalSessionCommandTests.Context(log).Messages.Any(message => message.Role == "user" && UserText(message.WireBody.Value) == line), "Actual acknowledged prompt was lost at EOF.");
            terminal.AssertJoined(); await files.Retain(new { scenario = "actual-response-held-EOF", heldAtEof = true, terminal.Snapshot });
        }
        finally { release.TrySetResult(); stop.Cancel(); terminal.Complete(); await running; terminal.AssertJoined(); }
        async ValueTask Observe(JsonData record, CancellationToken token)
        { var body = record.Value; if (IsResponse(body, "get_messages")) history.TrySetResult(); if (IsResponse(body, "prompt")) { Check(body.GetProperty("success").GetBoolean(), "Prompt was rejected."); held.TrySetResult(); await release.Task; } }
    }

    private static async Task Replacement(string host, string cli)
    {
        var files = await TerminalSessionCommandTests.WorkflowFiles.Create(host, cli, plugin: false);
        var terminal = new ScreenConsole(); using var error = new StringWriter(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var replaced = NewGate(); var history = NewGate(); string? replacementFile = null; long generation = 0;
        var running = TerminalSessionCommand.RunObservedAsync(files.Args(), terminal, terminal, error, Observe, stop.Token, observeInputCompletion: terminal.ObserveInputCompletion);
        try
        {
            await history.Task.WaitAsync(Bound); await terminal.Feed("/new\r"); await replaced.Task.WaitAsync(Bound);
            await terminal.WaitScreen(rows => TerminalSourceAsciiExpectations.RowsMatch(rows, 96, ""), stop.Token); await terminal.Feed("\u001b[A");
            await terminal.WaitScreen(rows => TerminalSourceAsciiExpectations.RowsMatch(rows, 96, "/new"), stop.Token);
            await terminal.Feed("\u001b"); await terminal.EscapeConsumed.Task.WaitAsync(Bound); await terminal.WaitScreen(rows => TerminalSourceAsciiExpectations.RowsMatch(rows, 96, "/new"), stop.Token);
            await terminal.Feed("\u0003"); await terminal.WaitScreen(rows => TerminalSourceAsciiExpectations.RowsMatch(rows, 96, ""), stop.Token);
            await terminal.Feed("\u0004"); Check(await running.WaitAsync(Bound) == 0 && error.ToString() == "", "Durable replacement command failed.");
            Check(generation > 1 && replacementFile is not null && Path.IsPathFullyQualified(replacementFile) && replacementFile != files.Session,
                "Actual replacement omitted its fresh durable session and generation.");
            await TerminalSessionCommandTests.Complete(files.Session); await TerminalSessionCommandTests.Complete(replacementFile!);
            terminal.AssertJoined(); await files.Retain(new { scenario = "actual-durable-replacement-recall", generation, replacementFile, idleEscapeConsumed = terminal.EscapeConsumed.Task.IsCompletedSuccessfully, terminal.Snapshot });
        }
        finally { stop.Cancel(); terminal.Complete(); await running; terminal.AssertJoined(); }
        ValueTask Observe(JsonData record, CancellationToken token)
        {
            var body = record.Value; if (IsResponse(body, "get_messages")) history.TrySetResult();
            if (body.GetProperty("type").GetString() == "session_switched")
            { replacementFile = body.GetProperty("sessionFile").GetString(); generation = body.GetProperty("generation").GetInt64(); }
            if (IsResponse(body, "new_session")) { Check(body.GetProperty("success").GetBoolean() && !body.GetProperty("data").GetProperty("cancelled").GetBoolean(), "New session was rejected or canceled."); replaced.TrySetResult(); }
            return ValueTask.CompletedTask;
        }
    }

    private static async Task Geometry()
    {
        var terminal = new ScreenConsole(); var first = new TerminalSessionView(terminal, terminal); var secondTerminal = new ScreenConsole(); var second = new TerminalSessionView(secondTerminal, secondTerminal);
        try
        {
            await first.StartAsync(CancellationToken.None); await second.StartAsync(CancellationToken.None);
            var editor = new TerminalTextEditorPasteController(); editor.SetText("abcd"); var original = first.CaptureEditorGeometry();
            var map = new TerminalEditorVisualMapBuilder().BuildForTerminal(editor.CaptureLayoutInput(), original); var before = editor.CaptureLayoutInput(); var beforeUndo = editor.UndoDepth;
            terminal.SetViewport(new(24, 12, 0, 0, 24, 12)); var changed = first.CaptureEditorGeometry();
            terminal.SetViewport(new(96, 12, 0, 0, 96, 12)); var returned = first.CaptureEditorGeometry();
            Check(original.Identity.ViewLifetimeId == returned.Identity.ViewLifetimeId && changed.Identity.GeometryRevision > original.Identity.GeometryRevision &&
                returned.Identity.GeometryRevision > changed.Identity.GeometryRevision && returned.Identity.PolicyId == TerminalEditorVisualMapBuilder.SourcePolicyId,
                "Actual view lost resize ABA or the admitted Source default policy.");
            Stale(returned.Identity); Stale(second.CaptureEditorGeometry().Identity);
            Check(editor.CaptureLayoutInput() == before && editor.HistoryCount == 0 && editor.UndoDepth == beforeUndo && editor.RegisteredPasteCount == 0,
                "Stale or foreign actual geometry changed editor ownership.");
            void Stale(TerminalEditorGeometryIdentity geometry)
            { try { editor.HandleInput(new TerminalKey("Up", TerminalModifiers.None, TerminalKeyAction.Press), map, geometry); throw new InvalidOperationException("Stale actual geometry was admitted."); } catch (TerminalEditorLayoutException error) when (error.Failure == TerminalEditorLayoutFailure.StaleGeometry) { } }
        }
        finally { await first.DisposeAsync(); await second.DisposeAsync(); terminal.AssertJoined(); secondTerminal.AssertJoined(); }
    }

    private static async Task CapturedLayout()
    {
        var terminal = new ScreenConsole(); terminal.SetViewport(new(8, 4, 0, 0, 8, 4)); var view = new TerminalSessionView(terminal, terminal);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var receipts = new TerminalSubmissionReceipts(); var snapshots = Channel.CreateBounded<TerminalDraftSnapshot>(32);
        await view.StartAsync(stop.Token);
        var input = new TerminalChatInput(terminal).RunAcknowledgedAsync((_, _) => throw new InvalidOperationException("This layout fixture never submits."),
            _ => Task.CompletedTask, async (snapshot, token) => { await view.SetDraftAsync(snapshot, token); Check(snapshots.Writer.TryWrite(snapshot), "Layout observation exceeds fixture bound."); },
            receipts, (_, _) => throw new InvalidOperationException("Unexpected acknowledgment."), _ => receipts.Complete(), stop.Cancel, stop.Token, view.CaptureEditorGeometry);
        try
        {
            var initial = await Next(); Check(initial.Text == "" && initial.Layout is not null, "Actual input owner omitted its initial empty Source layout.");
            await terminal.Feed("abcdefghijk"); var typed = await Next(); Check(typed.Layout is not null && typed.Layout.Identity.EditorLifetimeId != Guid.Empty, "Actual owner omitted immutable layout identity.");
            await terminal.Feed("\u001b[A"); var moved = await Next(); Check(moved.Text == typed.Text && moved.CursorUtf16Offset < typed.CursorUtf16Offset &&
                moved.Layout!.Identity.EditorLifetimeId == typed.Layout!.Identity.EditorLifetimeId && moved.Layout.Identity.EditorRevision > typed.Layout.Identity.EditorRevision,
                "Wrapped Up did not use actual full layout before clipping.");
            var paste = new string('p', 1001); await terminal.Feed("\u001b[200~" + paste + "\u001b[201~"); var marked = await Next();
            Check(marked.Layout is { AtomicRanges.Length: 1 } && marked.Text.Contains("[paste #1", StringComparison.Ordinal), "Actual view lost registered marker atomic provenance.");
            var retained = typed.Layout; terminal.Complete(); Check(await input.WaitAsync(Bound) == TerminalInputExit.Eof, "Captured layout owner failed EOF.");
            Check(retained!.Snapshot.Text == "abcdefghijk" && retained.Snapshot.CursorUtf16Offset == 11 && retained.AtomicRanges.IsEmpty,
                "View capture retained mutable editor state.");
            await view.DisposeAsync(); terminal.AssertJoined();
            Check(terminal.Frames[^1].Contains("\u001b[?2004l\u001b[?1049l", StringComparison.Ordinal), "Actual view restoration was not joined.");
        }
        finally { stop.Cancel(); receipts.Complete(); terminal.Complete(); await input; await view.DisposeAsync(); terminal.AssertJoined(); }
        async Task<TerminalDraftSnapshot> Next() => await snapshots.Reader.ReadAsync(stop.Token).AsTask().WaitAsync(Bound);
    }

    private static bool IsResponse(JsonElement body, string command) => body.GetProperty("type").GetString() == "response" && body.GetProperty("command").GetString() == command;
    private static string UserText(JsonElement body) { var value = body.GetProperty("content"); return value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Concat(value.EnumerateArray().Where(block => block.GetProperty("type").GetString() == "text").Select(block => block.GetProperty("text").GetString())); }
    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class ScreenConsole : IConsoleTerminal, ITerminalViewportSource
    {
        private readonly object gate = new(); private readonly Channel<string> input = Channel.CreateBounded<string>(8); private readonly Channel<bool> changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        private TerminalViewport viewport = new(96, 12, 0, 0, 96, 12); private readonly char[,] screen = new char[64, 256]; private readonly int[] lengths = new int[64]; private int row, column, reads, writes; private long readStarted, readSettled, writeStarted, writeSettled; private bool disposed;
        internal readonly TaskCompletionSource Eof = NewGate(); internal readonly List<string> Frames = [];
        internal readonly TaskCompletionSource EscapeConsumed = NewGate();
        internal void ObserveInputCompletion(TerminalInputEvent input)
        { if (input is TerminalKey { Key: "Escape", Modifiers: TerminalModifiers.None, Action: TerminalKeyAction.Press }) EscapeConsumed.TrySetResult(); }
        private static readonly TerminalConsoleState State = new(0, 0, 65001, 65001, 25, true, 0, 0);
        public TerminalLeaseSnapshot Snapshot { get { lock (gate) return new(State, State, null, false, false, reads, writes, readStarted, readSettled, writeStarted, writeSettled); } }
        public TerminalViewport ReadViewport() { lock (gate) return viewport; }
        internal void SetViewport(TerminalViewport value) { lock (gate) viewport = value; }
        internal ValueTask Feed(string text) => input.Writer.WriteAsync(text);
        internal void Complete() => input.Writer.TryComplete();
        public async ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default)
        {
            lock (gate) { Check(reads == 0, "Concurrent physical reads."); reads++; readStarted++; }
            var ended = false;
            try { if (!await input.Reader.WaitToReadAsync(token)) { ended = true; return 0; } Check(input.Reader.TryRead(out var text) && text.Length <= destination.Length, "Fixture read exceeds bounded decoder input."); text!.AsMemory().CopyTo(destination); return text!.Length; }
            finally { lock (gate) { reads--; readSettled++; } if (ended) Eof.TrySetResult(); }
        }
        public ValueTask WriteAsync(ReadOnlyMemory<char> data, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); lock (gate)
            {
                Check(writes == 0, "Concurrent physical writes."); writes++; writeStarted++;
                try
                {
                    var text = data.ToString(); Frames.Add(text); Check(Frames.Count <= 1024, "Frame retention exceeds fixture bound.");
                    for (var at = 0; at < text.Length;)
                    {
                        if (text[at] == '\u001b')
                        {
                            Check(at + 1 < text.Length && text[++at] == '[', "Unexpected VT escape."); var start = ++at;
                            while (at < text.Length && text[at] is not (>= '@' and <= '~')) at++;
                            Check(at < text.Length, "Incomplete VT operation."); var parameters = text[start..at]; var operation = text[at++];
                            if (operation == 'H') { var values = parameters.Split(';'); row = int.Parse(values[0]) - 1; column = int.Parse(values[1]) - 1; }
                            else if (operation == 'J') { Check(parameters == "2", "Unsupported clear screen."); Array.Clear(screen); Array.Clear(lengths); }
                            else if (operation == 'K') { Check(parameters == "2", "Unsupported clear row."); for (var x = 0; x < 256; x++) screen[row, x] = '\0'; lengths[row] = 0; }
                            else Check(operation is 'm' or 'h' or 'l', "Unexpected terminal control.");
                        }
                        else { Check(row is >= 0 and < 64 && column is >= 0 and < 256 && (text[at] is >= ' ' and <= '~' or '\u2500' or '\u2191' or '\u2193'), "Unprojected terminal data or invalid screen coordinate."); screen[row, column++] = text[at++]; lengths[row] = Math.Max(lengths[row], column); }
                    }
                    changed.Writer.TryWrite(true);
                }
                finally { writes--; writeSettled++; }
            }
            return ValueTask.CompletedTask;
        }
        internal async Task WaitScreen(Func<string[], bool> predicate, CancellationToken token)
        {
            while (true)
            {
                lock (gate) { var rows = Enumerable.Range(0, Math.Min(viewport.Rows, 64)).Select(y => new string(Enumerable.Range(0, lengths[y]).Select(x => screen[y, x] == '\0' ? ' ' : screen[y, x]).ToArray())).ToArray(); if (predicate(rows)) return; }
                await changed.Reader.ReadAsync(token).AsTask().WaitAsync(Bound);
            }
        }
        internal void AssertJoined() { var value = Snapshot; Check(value.ActiveReads == 0 && value.ActiveWrites == 0 && value.ReadWorkersStarted == value.ReadWorkersSettled && value.WriteWorkersStarted == value.WriteWorkersSettled && !disposed, "Owned physical work was detached or borrowed console disposed."); }
        public ValueTask DisposeAsync() { disposed = true; return ValueTask.CompletedTask; }
    }
}
