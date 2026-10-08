using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Interactive;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Tui.Components.Text;
using PiSharp.Tui.Input;
using PiSharp.Tui.Rendering;

internal static class TerminalQueueAdoptionCases
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    internal static IEnumerable<(string Id, Func<ConsumerEvidence, Task> Run)> Cases(string[] args) =>
    [
        ("queue-adoption-authored-source-children-through-real-frontend-view", e => SourceFixtures(args[1], e)),
        ("queue-adoption-authoritative-replace-drain-clear-preserves-editor", ReplaceClear),
        ("queue-adoption-session-switch-clears-and-stale-switch-is-inert", SessionSwitch),
        ("queue-adoption-held-old-paint-switch-and-fresh-preview-remain-ordered", HeldSwitch),
        ("queue-adoption-dialog-stores-latest-preview-without-reply-authority", Dialog),
        ("queue-adoption-small-viewport-crop-restores-complete-snapshot", Crop),
        ("queue-adoption-original-held-paint-and-next-update-join", HeldPaint),
        ("queue-adoption-bounds-cooked-and-count-only-state", Bounds),
        ("queue-adoption-actual-configured-command-rpc-steer-followup-clear", e => NativeCommand(args[1], e))
    ];

    private static async Task SourceFixtures(string reviewRoot, ConsumerEvidence e)
    {
        const string relative = "tests/PiSharp.Terminal.SelectList.Integration.Tests/fixtures/queue-adoption-source.json";
        var source = Path.Combine(reviewRoot, relative); var output = Path.Combine(AppContext.BaseDirectory, "fixtures/queue-adoption-source.json");
        var bytes = await File.ReadAllBytesAsync(source); var copiedBytes = await File.ReadAllBytesAsync(output);
        Check(bytes.AsSpan().SequenceEqual(copiedBytes), "Queue fixture differs from admitted source.");
        using var fixture = JsonDocument.Parse(bytes); var root = fixture.RootElement;
        Equal("d86654abb8862e201933517d6f1fce9f88dd117f", root.GetProperty("upstreamCommit").GetString());
        Equal("authored-from-static-pinned-source-not-captured", root.GetProperty("expectationKind").GetString());
        Equal(4, root.GetProperty("vectors").GetArrayLength());
        await using var f = await SelectorFixture.Create(); var number = 0;
        foreach (var vector in root.GetProperty("vectors").EnumerateArray())
        {
            var steering = Strings(vector.GetProperty("steering")); var followUp = Strings(vector.GetProperty("followUp"));
            var columns = vector.GetProperty("columns").GetInt32(); var height = 10 + number++;
            var children = steering.Select(text => "Steering: " + text).Concat(followUp.Select(text => "Follow-up: " + text)).ToArray();
            for (var at = 0; at < children.Length; at++)
                Same(Strings(vector.GetProperty("sourceRows")[at]), new TerminalTruncatedText(children[at], 1, 0).Render(columns));
            f.Viewport.Value = new(columns, height, 0, 0, 80, 25); await f.View.RefreshViewportAsync(default);
            await f.Frontend.ObserveAsync(Packet(steering, followUp), default);
            var actual = PreviewRows(f, height);
            Same(Strings(vector.GetProperty("nativeRows")), actual);
            Check(actual.All(row => !row.Any(c => c < 32 || c is >= '\u007f' and <= '\u009f')), "Queue text gained VT authority.");
            Equal(0, f.Replies().Length); Equal("", f.LastDraft!.Text);
            e.Observe("authored-truncated-children-and-connected-physical-rows", new { id = vector.GetProperty("id").GetString(),
                steering, followUp, columns, actual, sourceFixtureHash = EvidenceAdmission.Hash(source), sourceObserved = false });
        }
    }

    private static async Task ReplaceClear(ConsumerEvidence e)
    {
        await using var f = await SelectorFixture.Create(); await f.TypeDraft("owned \u6587");
        var draft = f.LastDraft!; var originalEditor = Editor(f);
        await f.Frontend.ObserveAsync(Packet(["first", "first", "third\nfull canonical second line"], ["after"]), default);
        var first = PreviewRows(f, 25);
        Same([" Steering: first", " Steering: first", " Steering: third", " Follow-up: after"], first.Select(row => row.TrimEnd()));
        var published = f.Terminal.Writes().Length;
        await f.Frontend.ObserveAsync(Packet(["first", "first", "third\nfull canonical second line"], ["after"]), default);
        Equal(published, f.Terminal.Writes().Length);
        await f.Frontend.ObserveAsync(Packet(["third\nfull canonical second line"], ["after"]), default);
        Same([" Steering: third", " Follow-up: after"], PreviewRows(f, 25).Select(row => row.TrimEnd()));
        await f.Frontend.ObserveAsync(Packet([], []), default); Equal(0, PreviewRows(f, 25).Length);
        Equal(draft, f.LastDraft); var final = Editor(f);
        Equal(originalEditor.Frame.Cursor, final.Frame.Cursor); Equal(originalEditor.FirstVisibleSourceRow, final.FirstVisibleSourceRow);
        Same(originalEditor.Frame.Rows.Skip(originalEditor.EditorRowOrigin).Select(row => row.Text), Screen(f.Terminal.Writes(), 25).Skip(final.EditorRowOrigin));
        await f.Key("!"); Equal("owned \u6587!", f.LastDraft!.Text); Equal(0, f.Replies().Length);
        e.Observe("real-event-fifo-duplicates-drain-clear-preserve-canonical-editor", new { first, draft, originalEditor.Frame.Cursor, writes = f.Terminal.Writes() });
    }

    private static async Task SessionSwitch(ConsumerEvidence e)
    {
        await using var f = await SelectorFixture.Create(); await f.TypeDraft("draft stays with input owner");
        await f.Frontend.ObserveAsync(Packet(["old"], []), default);
        await f.Frontend.ObserveAsync(JsonData.Parse("{\"type\":\"session_switched\",\"generation\":2,\"sessionId\":\"replacement\"}"), default);
        Equal(0, PreviewRows(f, 25).Length);
        await f.Frontend.ObserveAsync(Packet([], ["new"]), default);
        var before = f.Terminal.Writes().Length;
        foreach (var generation in new[] { 1, 2 })
            await f.Frontend.ObserveAsync(JsonData.Parse(JsonSerializer.Serialize(new { type = "session_switched", generation, sessionId = "stale" })), default);
        Equal(before, f.Terminal.Writes().Length);
        await f.View.PresentPendingAsync(new(1, ["stale view turn"], []), default);
        Same([" Follow-up: new"], PreviewRows(f, 25).Select(row => row.TrimEnd()));
        Equal("draft stays with input owner", f.LastDraft!.Text); Equal(0, f.Replies().Length);
        e.Observe("ordered-rpc-stream-generation-bound-preview", new { rows = PreviewRows(f, 25), f.LastDraft, inventedWireGenerations = false });
    }

    // Authored only: pairs with the RPC authority/publication interleaving regression.
    private static async Task HeldSwitch(ConsumerEvidence e)
    {
        await using var f = await SelectorFixture.Create();
        var hold = f.Terminal.HoldWrite("Steering: old held preview");
        var old = f.Frontend.ObserveAsync(Packet(["old held preview"], []), default).AsTask();
        Task? switched = null, fresh = null;
        try
        {
            await hold.Entered.Task.WaitAsync(Deadline);
            switched = f.Frontend.ObserveAsync(JsonData.Parse("""{"type":"session_switched","generation":2,"sessionId":"replacement"}"""), default).AsTask();
            fresh = f.Frontend.ObserveAsync(Packet([], ["fresh preview"]), default).AsTask();
            Check(!old.IsCompleted && !switched.IsCompleted && !fresh.IsCompleted && f.Terminal.ActiveWrites == 1,
                "Switch or fresh preview detached the held original paint.");
        }
        finally
        {
            hold.Release.TrySetResult();
            await Task.WhenAll(new[] { old, switched, fresh }.OfType<Task>());
        }
        Same([" Follow-up: fresh preview"], PreviewRows(f, 25).Select(row => row.TrimEnd()));
        e.Observe("ordered-switch-retains-new-generation-preview-after-held-paint", new { rows = PreviewRows(f, 25) });
    }

    private static async Task Dialog(ConsumerEvidence e)
    {
        await using var f = await SelectorFixture.Create(); await f.TypeDraft("kept");
        await f.Frontend.ObserveAsync(Packet(["before"], []), default);
        await using var scope = f.Ui.OpenScope(new SelectorContext());
        const string exact = "chosen\nfull reply \u6587\u001b[2J";
        var answer = scope.SelectAsync("dialog", [exact]).AsTask(); await f.Observer.WaitPublished(1);
        var identity = f.Frontend.CaptureSelectDialog()!.Identity; var before = f.Terminal.Writes().Length;
        await f.Frontend.ObserveAsync(Packet(["latest"], ["pending later"]), default);
        Equal(before, f.Terminal.Writes().Length); Equal(identity, f.Frontend.CaptureSelectDialog()!.Identity);
        await f.Key("\r"); Equal(exact, (await answer.WaitAsync(Deadline)).Value);
        Equal(exact, f.Replies().Single().Value.GetProperty("value").GetString());
        Same([" Steering: latest", " Follow-up: pending later"], PreviewRows(f, 25).Select(row => row.TrimEnd()));
        Equal("kept", f.LastDraft!.Text); Check(f.LastDraft.EditorOwnsFocus, "Dialog failed to restore draft focus.");
        e.Observe("real-overlay-keeps-latest-queue-and-canonical-reply", new { identity, replies = f.Replies(), rows = PreviewRows(f, 25), f.LastDraft });
    }

    private static async Task Crop(ConsumerEvidence e)
    {
        await using var f = await SelectorFixture.Create(); await f.TypeDraft("kept");
        var steering = Enumerable.Range(0, 6).Select(i => "item " + i).ToArray();
        await f.Frontend.ObserveAsync(Packet(steering, ["follow"]), default);
        f.Viewport.Value = new(20, 4, 0, 0, 80, 25); await f.View.RefreshViewportAsync(default);
        Same([" Steering: item 0"], PreviewRows(f, 4).Select(row => row.TrimEnd()));
        var narrow = TerminalPendingQueueFrameFactory.Create(Editor(f), steering.ToImmutableArray(), ["follow"]);
        Check(narrow.Frame.IsClipped, "Viewport crop failed to report clipping.");
        f.Viewport.Value = new(20, 3, 0, 0, 80, 25); await f.View.RefreshViewportAsync(default); Equal(0, PreviewRows(f, 3).Length);
        f.Viewport.Value = new(80, 25, 0, 0, 80, 25); await f.View.RefreshViewportAsync(default);
        Same(steering.Select(text => " Steering: " + text).Append(" Follow-up: follow"), PreviewRows(f, 25).Select(row => row.TrimEnd()));
        Equal("kept", f.LastDraft!.Text);
        e.Observe("bounded-preview-crop-does-not-drain-canonical-snapshot", new { narrow.Frame.IsClipped, restored = PreviewRows(f, 25), f.LastDraft });
    }

    private static async Task HeldPaint(ConsumerEvidence e)
    {
        await using var f = await SelectorFixture.Create(); await f.TypeDraft("owned");
        var hold = f.Terminal.HoldWrite("Steering: physically held"); using var caller = new CancellationTokenSource();
        var original = f.Frontend.ObserveAsync(Packet(["physically held"], []), caller.Token).AsTask(); Task? clear = null;
        try
        {
            await hold.Entered.Task.WaitAsync(Deadline); caller.Cancel(); await hold.Canceled.Task.WaitAsync(Deadline);
            clear = f.Frontend.ObserveAsync(Packet([], []), default).AsTask();
            Check(!original.IsCompleted && !clear.IsCompleted && f.Terminal.ActiveWrites == 1, "Queue paint detached its original physical output.");
            Equal("owned", f.LastDraft!.Text); Equal(0, f.Terminal.Disposals);
            e.Observe("queue-original-paint-held-and-clear-waits", new { original.IsCompleted, clearComplete = clear.IsCompleted, f.Terminal.ActiveWrites });
        }
        finally
        {
            hold.Release.TrySetResult();
            try { await original; } catch (OperationCanceledException) when (caller.IsCancellationRequested) { }
            if (clear is not null) await clear;
        }
        Equal(0, PreviewRows(f, 25).Length); await f.Key("!"); Equal("owned!", f.LastDraft!.Text);
        await f.StopAsync(); Equal(0, f.Terminal.ActiveReads); Equal(0, f.Terminal.ActiveWrites); Equal(1, f.Terminal.MaximumReaders);
        e.Observe("queue-original-physical-write-and-input-owner-joined", f.Snapshot());
    }

    private static async Task Bounds(ConsumerEvidence e)
    {
        await using var f = await SelectorFixture.Create(); await f.Frontend.ObserveAsync(Packet(["old"], []), default);
        var before = f.Terminal.Writes().Length;
        foreach (var invalid in new[] { Packet(Enumerable.Repeat("x", 257).ToArray(), []), Packet([new string('x', 65_527)], []),
            Packet(Enumerable.Repeat(new string('x', 32_768), 33).ToArray(), []),
            JsonData.Parse("{\"type\":\"queue_update\",\"steering\":[null],\"followUp\":[]}"),
            JsonData.Parse("{\"type\":\"queue_update\",\"steering\":[],\"followUp\":false}") })
        { try { await f.Frontend.ObserveAsync(invalid, default); throw new Exception("Missing bounded queue rejection."); } catch (InvalidOperationException) { } }
        Equal(before, f.Terminal.Writes().Length); Same([" Steering: old"], PreviewRows(f, 25).Select(row => row.TrimEnd()));
        await f.Frontend.ObserveAsync(JsonData.Parse("{\"type\":\"response\",\"id\":\"state-only\",\"success\":true,\"command\":\"get_state\",\"data\":{\"isStreaming\":false,\"isCompacting\":false,\"pendingMessageCount\":7}}"), default);
        Same([" Steering: old"], PreviewRows(f, 25).Select(row => row.TrimEnd()));
        var editor = Editor(f);
        Failure(TerminalRenderFailure.InvalidUnicode, () => TerminalPendingQueueFrameFactory.Create(editor, ["\ud800"], []));
        Failure(TerminalRenderFailure.ResourceLimit, () => TerminalPendingQueueFrameFactory.Create(editor, Enumerable.Repeat("x", 257).ToImmutableArray(), []));
        Failure(TerminalRenderFailure.ResourceLimit, () => TerminalPendingQueueFrameFactory.Create(editor, ["x"], [], new(MaximumFrameCharacters: 32)));
        var safe = TerminalPendingQueueFrameFactory.Create(editor, ["\u001b]0;bad\u0007\r\t\0\u6587"], []);
        Check(safe.Frame.Rows.All(row => !row.Text.Any(c => c < 32 || c is >= '\u007f' and <= '\u009f')), "Composition granted canonical controls authority.");
        using var output = new StringWriter(); using var cooked = new InteractiveSessionFrontend(output);
        await cooked.ObserveAsync(Packet(["silent"], []), default); Equal("", output.ToString());
        using var probe = new InteractiveSessionFrontend(output, nativePresentation: true);
        probe.Bind((_, _) => Task.CompletedTask);
        await probe.ObserveAsync(JsonData.Parse("{\"type\":\"response\",\"id\":\"other\",\"success\":true,\"command\":\"get_state\",\"data\":{\"isStreaming\":false,\"isCompacting\":false,\"pendingMessageCount\":7}}"), default);
        Check(!output.ToString().Contains("Steering:", StringComparison.Ordinal) && !output.ToString().Contains("Follow-up:", StringComparison.Ordinal), "Count-only state manufactured queue text.");
        e.Observe("queue-admission-bounds-inert-controls-cooked-count-only", new { safe.Frame.Rows, current = PreviewRows(f, 25), output = output.ToString() });
    }

    private static async Task NativeCommand(string reviewRoot, ConsumerEvidence e)
    {
        var files = await StartupOwnedFiles.Create(reviewRoot, plugin: false, e); var before = StartupOwnedFiles.Hash(files.Session);
        var trace = new StartupTrace(); var terminal = new StartupControlledTerminal(trace);
        using var errors = new StringWriter(); using var cancellation = new CancellationTokenSource();
        var original = TerminalSessionCommand.RunObservedConfiguredAsync(files.Args(), terminal, terminal, errors, trace.Observe, files.Configuration, cancellation.Token);
        try
        {
            await terminal.WaitWrite("[history]"); await terminal.WaitReads(1); await files.AssertWriterOwned();
            await terminal.Feed("/steer canonical \u6587\r");
            var first = await trace.WaitRecord(record => IsQueue(record, 1, 0));
            Equal("canonical \u6587", first.Value.GetProperty("steering")[0].GetString()); await terminal.WaitWrite("Steering: canonical \u6587");
            await terminal.Feed("/follow-up next \u6587\r");
            var mixed = await trace.WaitRecord(record => IsQueue(record, 1, 1));
            Equal("next \u6587", mixed.Value.GetProperty("followUp")[0].GetString()); await terminal.WaitWrite("Follow-up: next \u6587");
            await terminal.Feed("/clear-queue\r"); await trace.WaitRecord(record => IsQueue(record, 0, 0));
            await terminal.WaitWrite("[cleared]");
            await terminal.Feed("/quit\r"); Equal(0, await original.WaitAsync(TimeSpan.FromSeconds(15)));
            Equal("", errors.ToString()); terminal.AssertJoined(); await files.Complete(e);
            Equal(before, StartupOwnedFiles.Hash(files.Session)); Check(!File.Exists(files.Target), "Idle queue presentation performed an effect.");
            Check(!trace.Records().Any(record => record.Value.GetProperty("type").GetString() == "agent_start"), "Idle queue preview started an agent operation.");
            e.Observe("actual-public-command-host-queue-events-and-native-preview", new { first, mixed, terminal = terminal.Evidence, trace = trace.Rows(), sourceObserved = false });
        }
        finally { cancellation.Cancel(); terminal.End(); terminal.Release(); await original; terminal.AssertJoined(); }
    }

    private static bool IsQueue(JsonData record, int steering, int followUp) => record.Value.GetProperty("type").GetString() == "queue_update" &&
        record.Value.GetProperty("steering").GetArrayLength() == steering && record.Value.GetProperty("followUp").GetArrayLength() == followUp;
    private static JsonData Packet(string[] steering, string[] followUp) => JsonData.Parse(JsonSerializer.Serialize(new { type = "queue_update", steering, followUp }));
    private static TerminalEditorRenderedFrame Editor(SelectorFixture f)
    {
        var map = new TerminalEditorVisualMapBuilder().BuildForTerminal(f.LastDraft!.Layout!, f.View.CaptureEditorGeometry());
        return TerminalEditorFrameFactory.Create(map, 0, [], f.LastDraft.EditorOwnsFocus);
    }
    private static string[] PreviewRows(SelectorFixture f, int height) => Screen(f.Terminal.Writes(), height)
        .Where(row => row.StartsWith(" Steering:", StringComparison.Ordinal) || row.StartsWith(" Follow-up:", StringComparison.Ordinal)).ToArray();
    // Read real full/diff writes. Every renderer row writes from column one after clear-line;
    // final caret CUP carries no row text. Unicode stays data; this observer does not emulate width.
    private static string[] Screen(IEnumerable<string> writes, int rows)
    {
        var result = Enumerable.Repeat("", rows).ToArray(); var row = 0;
        foreach (var output in writes)
            for (var at = 0; at < output.Length;)
            {
                if (output[at] != '\u001b')
                {
                    var end = output.IndexOf('\u001b', at); if (end < 0) end = output.Length;
                    if ((uint)row < (uint)rows) result[row] += output[at..end]; at = end; continue;
                }
                Check(at + 1 < output.Length && output[at + 1] == '[', "Unexpected native control introducer.");
                var start = at + 2; var endCode = start;
                while (endCode < output.Length && output[endCode] is not (>= '@' and <= '~')) endCode++;
                Check(endCode < output.Length, "Incomplete native control sequence.");
                var parameter = output[start..endCode]; var final = output[endCode]; at = endCode + 1;
                if (final == 'H') row = int.Parse(parameter.Split(';')[0], System.Globalization.CultureInfo.InvariantCulture) - 1;
                else if (final == 'J' && parameter == "2") Array.Fill(result, "");
                else if (final == 'K' && parameter == "2") { if ((uint)row < (uint)rows) result[row] = ""; }
                else if (final == 'm' && parameter is "0" or "7") { }
                else if (final is 'h' or 'l' && parameter is "?25" or "?1049" or "?2004") { }
                else throw new InvalidOperationException("Unexpected native queue VT command.");
            }
        return result;
    }
    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(value => value.GetString()!).ToArray();
    private static void Same(IEnumerable<string> expected, IEnumerable<string> actual) => Check(expected.SequenceEqual(actual), "Queue rows differ: " + string.Join("|", actual));
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => TerminalSelectDialogCases.Equal(expected, actual);
    private static void Failure(TerminalRenderFailure expected, Action action)
    { try { action(); } catch (TerminalRenderException error) { Equal(expected, error.Failure); return; } throw new InvalidOperationException("Expected queue render failure."); }
}
