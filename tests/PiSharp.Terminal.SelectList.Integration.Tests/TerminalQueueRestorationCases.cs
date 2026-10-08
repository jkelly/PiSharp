using System.Text.Json;
using PiSharp.Cli.Interactive;
using PiSharp.Cli.Commands;
using PiSharp.Contracts;
using PiSharp.Tui.Input;

// Authored controlled-peer integration cases, not executed and not a core queue oracle.
internal static class TerminalQueueRestorationCases
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    internal static IEnumerable<(string Id, Func<ConsumerEvidence, Task> Run)> Cases(string[] args) =>
    [
        ("queue-restore-native-binding-canonical-text-edit-and-undo", RestoreEdit),
        ("queue-restore-held-request-queued-edit-and-duplicate-response", HeldEdit),
        ("queue-restore-switch-discards-stale-generation-response", Switch),
        ("queue-restore-empty-and-rejected-response-preserve-draft", EmptyRejected),
        ("queue-restore-canceled-send-joins-original-without-draft-change", e => Cancel(e, eof: false)),
        ("queue-restore-eof-joins-original-send-before-cleanup", e => Cancel(e, eof: true)),
        ("queue-restore-pending-take-keeps-selector-input-live", Selector),
        ("queue-restore-malformed-and-foreign-response-correlation", ResponseValidation),
        ("queue-restore-configured-press-repeat-release-and-empty-second-action", ConfiguredAction),
        ("queue-restore-actual-configured-command-authoritative-take-and-edit", e => NativeCommand(args[1], e)),
        ("queue-restore-receipt-single-claim-editor-and-generation-guards", ReceiptGuards)
    ];

    private static async Task RestoreEdit(ConsumerEvidence e)
    {
        SelectorFixture? owner = null; var requests = new List<JsonData>();
        const string restored = "first\n\nfirst\n\nlast\nline\n\ndraft";
        await using var f = await SelectorFixture.Create(commandHandler: async (request, token) =>
        {
            requests.Add(request);
            await owner!.Frontend.ObserveAsync(Reply(request, restored, 3), token);
        });
        owner = f; await f.TypeDraft("draft");
        var lifetime = f.LastDraft!.Layout!.Identity.EditorLifetimeId;
        await f.Key("\u001bq");
        Equal(restored, f.LastDraft!.Text);
        Equal(restored.Length, f.LastDraft.CursorUtf16Offset);
        Equal(lifetime, f.LastDraft.Layout!.Identity.EditorLifetimeId);
        Equal("draft", requests.Single().Value.GetProperty("currentText").GetString());
        Equal(1L, requests.Single().Value.GetProperty("generation").GetInt64());
        await f.Key("!"); Equal(restored + "!", f.LastDraft.Text);
        await f.Key("\u001a"); Equal(restored, f.LastDraft.Text);
        await f.Key("\u001a"); Equal("draft", f.LastDraft.Text);
        e.Observe("native-alt-q-original-editor-canonical-duplicates-and-undo", new { requests, restored, lifetime });
    }

    private static async Task HeldEdit(ConsumerEvidence e)
    {
        SelectorFixture? owner = null; JsonData? response = null;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var f = await SelectorFixture.Create(commandHandler: async (request, token) =>
        {
            calls++; entered.TrySetResult(); await release.Task;
            response = Reply(request, "queued\n\ndraft", 1);
            await owner!.Frontend.ObserveAsync(response, token);
        });
        owner = f; await f.TypeDraft("draft");
        var restore = f.Key("\u001bq");
        try
        {
            await entered.Task.WaitAsync(Deadline);
            using var ignored = new CancellationTokenSource();
            var otherEditor = new TerminalTextEditorPasteController(); otherEditor.SetText("foreign");
            Check(await f.Frontend.RestoreQueuedMessagesAsync(otherEditor, ignored.Token) is null,
                "Duplicate action gained a second outstanding removal.");
            f.Terminal.Feed("!");
            Equal("draft", f.LastDraft!.Text); Equal(1, calls);
        }
        finally { release.TrySetResult(); await restore; }
        await f.WaitDraft("queued\n\ndraft!");
        await f.Frontend.ObserveAsync(response!, default);
        Equal("queued\n\ndraft!", f.LastDraft!.Text); Equal(1, calls);
        e.Observe("held-removal-serializes-input-and-replayed-response-is-inert", new { calls, f.LastDraft });
    }

    private static async Task Switch(ConsumerEvidence e)
    {
        SelectorFixture? owner = null;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var f = await SelectorFixture.Create(commandHandler: async (request, token) =>
        {
            entered.TrySetResult(); await release.Task;
            await owner!.Frontend.ObserveAsync(Reply(request, "stale removed text", 1), token);
        });
        owner = f; await f.TypeDraft("kept"); var restore = f.Key("\u001bq");
        try
        {
            await entered.Task.WaitAsync(Deadline);
            var oldRead = checked((int)f.Terminal.ReadsStarted);
            f.Terminal.Feed("!"); await f.Terminal.WaitDecodedReads(oldRead);
            await f.Frontend.ObserveAsync(JsonData.Parse("""{"type":"session_switched","generation":2,"sessionId":"new"}"""), default);
            var newRead = checked((int)f.Terminal.ReadsStarted);
            f.Terminal.Feed("?"); await f.Terminal.WaitDecodedReads(newRead);
        }
        finally { release.TrySetResult(); await restore; }
        await f.WaitDraft("kept?");
        e.Observe("generation-one-reply-cannot-overwrite-generation-two-editor", new { f.LastDraft });
    }

    private static async Task EmptyRejected(ConsumerEvidence e)
    {
        SelectorFixture? owner = null; var calls = 0;
        await using var f = await SelectorFixture.Create(commandHandler: async (request, token) =>
        {
            var response = ++calls == 1 ? Reply(request, "kept", 0) : JsonData.Parse(JsonSerializer.Serialize(new
            {
                type = "response", id = request.Value.GetProperty("id").GetString(),
                command = "pisharp_restore_queue", success = false, error = "queue exceeds editor capacity"
            }));
            await owner!.Frontend.ObserveAsync(response, token);
        });
        owner = f; await f.TypeDraft("kept"); await f.Key("\u001b[D");
        var cursor = f.LastDraft!.CursorUtf16Offset;
        await f.Key("\u001bq"); Equal("kept", f.LastDraft.Text); Equal(cursor, f.LastDraft.CursorUtf16Offset);
        await f.Key("\u001bq"); Equal("kept", f.LastDraft.Text); Equal(cursor, f.LastDraft.CursorUtf16Offset);
        Equal(2, calls);
        e.Observe("empty-or-rejected-take-does-not-reset-editor-cursor", new { calls, f.LastDraft });
    }

    private static async Task Cancel(ConsumerEvidence e, bool eof)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var f = await SelectorFixture.Create(commandHandler: async (_, token) =>
        {
            entered.TrySetResult(); using var registration = token.Register(() => canceled.TrySetResult());
            await release.Task; token.ThrowIfCancellationRequested();
        });
        await f.TypeDraft("kept"); f.Terminal.Feed("\u001bq");
        try
        {
            await entered.Task.WaitAsync(Deadline);
            if (eof) f.Terminal.End(); else f.Cancellation.Cancel();
            await canceled.Task.WaitAsync(Deadline);
            Check(!f.Input.IsCompleted, "Cancellation detached the entered restoration sender.");
            Equal("kept", f.LastDraft!.Text);
            e.Observe("canceled-restoration-retains-original-send-join", new { eof, f.Input.IsCompleted, f.LastDraft });
        }
        finally { release.TrySetResult(); }
        try { await f.Input; } catch (OperationCanceledException) { }
    }

    private static async Task Selector(ConsumerEvidence e)
    {
        SelectorFixture? owner = null;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var f = await SelectorFixture.Create(commandHandler: async (request, token) =>
        {
            entered.TrySetResult(); await release.Task;
            await owner!.Frontend.ObserveAsync(Reply(request, "restored\n\ndraft", 1), token);
        });
        owner = f; await f.TypeDraft("draft"); var restoring = f.Key("\u001bq");
        Task? answerTask = null;
        try
        {
            await entered.Task.WaitAsync(Deadline);
            await using var scope = f.Ui.OpenScope(new SelectorContext());
            var answer = scope.SelectAsync("While queue request is pending", ["chosen"]).AsTask();
            answerTask = answer;
            await f.Observer.WaitPublished(1);
            await f.Key("\r");
            Equal("chosen", (await answer).Value);
            Equal("draft", f.LastDraft!.Text);
        }
        finally
        {
            release.TrySetResult();
            await restoring;
            if (answerTask is not null) await answerTask;
        }
        await f.WaitDraft("restored\n\ndraft");
        e.Observe("pending-queue-restoration-does-not-deadlock-host-selector-publication", new { f.LastDraft });
    }

    private static async Task ResponseValidation(ConsumerEvidence e)
    {
        await using var f = await SelectorFixture.Create();
        var editor = new TerminalTextEditorPasteController(); editor.SetText("draft");
        JsonData? request = null;
        f.Frontend.Bind((record, _) => { request = record; return Task.CompletedTask; });
        var pending = f.Frontend.RestoreQueuedMessagesAsync(editor, default);
        var foreign = JsonData.Parse("""{"id":"foreign","type":"pisharp_restore_queue","generation":1}""");
        await f.Frontend.ObserveAsync(Reply(foreign, "wrong", 1), default);
        Check(!pending.IsCompleted, "Foreign response completed another request.");
        var malformed = Reply(request!, "changed empty response", 0);
        var rejected = false;
        try { await f.Frontend.ObserveAsync(malformed, default); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected && (await pending) is null, "Malformed response stranded its waiter or granted edit authority.");
        Equal("draft", editor.GetExpandedText());

        pending = f.Frontend.RestoreQueuedMessagesAsync(editor, default);
        await f.Frontend.ObserveAsync(Reply(request!, "restored", 1), default);
        var result = await pending;
        Check(result is not null && f.Frontend.ApplyQueueRestoration(result, editor), "Valid correlated response was rejected.");
        await f.Frontend.ObserveAsync(Reply(request!, "malformed duplicate", -1), default);
        Equal("restored", editor.GetExpandedText());
        e.Observe("foreign-malformed-and-duplicate-responses-do-not-grant-edit-authority", new { rejected, editor.Snapshot });
    }

    private static async Task ConfiguredAction(ConsumerEvidence e)
    {
        SelectorFixture? owner = null; var requests = new List<JsonData>();
        await using var f = await SelectorFixture.Create(kitty: true,
            configuration: """{"app.message.dequeue":"ctrl+r"}""", commandHandler: async (request, token) =>
            {
                requests.Add(request);
                var text = requests.Count == 1 ? "restored\n\ndraft" : request.Value.GetProperty("currentText").GetString()!;
                await owner!.Frontend.ObserveAsync(Reply(request, text, requests.Count == 1 ? 1 : 0), token);
            });
        owner = f; await f.TypeDraft("draft");
        await f.Key("\u001b[114;5:2u"); await f.Key("\u001b[114;5:3u");
        Equal(0, requests.Count); Equal("draft", f.LastDraft!.Text);
        await f.Key("\u001b[114;5:1u"); Equal(1, requests.Count);
        Equal("restored\n\ndraft", f.LastDraft.Text);
        await f.Key("\u001b[114;5:1u"); Equal(2, requests.Count);
        Equal("restored\n\ndraft", f.LastDraft.Text);
        Check(requests[0].Value.GetProperty("id").GetString() != requests[1].Value.GetProperty("id").GetString(),
            "Distinct actions reused a restoration correlation identity.");
        e.Observe("configured-native-action-ignores-repeat-release-and-empty-take", new { requests, f.LastDraft });
    }

    private static async Task ReceiptGuards(ConsumerEvidence e)
    {
        await using var f = await SelectorFixture.Create();
        var editor = new TerminalTextEditorPasteController(); editor.SetText("before");
        var receipt = new TerminalQueueRestoration(1, editor.LayoutIdentity.EditorLifetimeId, "before", "restored", 1);
        Check(f.Frontend.ApplyQueueRestoration(receipt, editor), "Current receipt was not applied.");
        Check(!f.Frontend.ApplyQueueRestoration(receipt, editor), "Receipt applied more than once.");
        var foreignEditor = new TerminalTextEditorPasteController(); foreignEditor.SetText("restored");
        var foreignReceipt = new TerminalQueueRestoration(1, editor.LayoutIdentity.EditorLifetimeId, "restored", "wrong", 1);
        Check(!f.Frontend.ApplyQueueRestoration(foreignReceipt, foreignEditor), "Receipt crossed editor lifetimes.");
        var changed = new TerminalQueueRestoration(1, editor.LayoutIdentity.EditorLifetimeId, "restored", "wrong", 1);
        editor.SetText("edited");
        Check(!f.Frontend.ApplyQueueRestoration(changed, editor), "Receipt overwrote intervening editor edits.");
        var stale = new TerminalQueueRestoration(1, editor.LayoutIdentity.EditorLifetimeId, "edited", "wrong", 1);
        await f.Frontend.ObserveAsync(JsonData.Parse("""{"type":"session_switched","generation":2,"sessionId":"new"}"""), default);
        Check(!f.Frontend.ApplyQueueRestoration(stale, editor), "Stale generation receipt applied.");
        Equal("edited", editor.GetExpandedText());
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await f.Frontend.RestoreQueuedMessagesAsync(editor, cancellation.Token); throw new InvalidOperationException("Canceled request sent."); }
        catch (OperationCanceledException) { }
        e.Observe("single-claim-editor-lifetime-text-generation-and-pre-send-cancellation", new { editor.Snapshot });
    }

    private static async Task NativeCommand(string root, ConsumerEvidence e)
    {
        var files = await StartupOwnedFiles.Create(root, plugin: false, e);
        var before = StartupOwnedFiles.Hash(files.Session);
        var trace = new StartupTrace(); var terminal = new StartupControlledTerminal(trace);
        using var errors = new StringWriter(); using var cancellation = new CancellationTokenSource();
        var original = TerminalSessionCommand.RunObservedConfiguredAsync(files.Args(), terminal, terminal, errors,
            trace.Observe, files.Configuration, cancellation.Token);
        try
        {
            await terminal.WaitWrite("[history]"); await terminal.WaitReads(1);
            await terminal.Feed("/steer same\r"); await trace.WaitRecord(r => QueueSize(r, 1, 0));
            await terminal.Feed("/steer same\r"); await trace.WaitRecord(r => QueueSize(r, 2, 0));
            await terminal.Feed("\u001b[200~/follow-up next\nline\u001b[201~\r");
            await trace.WaitRecord(r => QueueSize(r, 2, 1));
            await terminal.Feed("draft\u001bq");
            var first = await trace.WaitRecord(r => RestoreCount(r, 3));
            const string expected = "same\n\nsame\n\nnext\nline\n\ndraft";
            Equal(expected, first.Value.GetProperty("data").GetProperty("text").GetString());
            await terminal.Feed("!\u001bq");
            var second = await trace.WaitRecord(r => RestoreCount(r, 0));
            Equal(expected + "!", second.Value.GetProperty("data").GetProperty("text").GetString());
            await terminal.WaitWrite("draft!");
            await terminal.Feed("\u0003"); await terminal.WaitWrite("[draft discarded]");
            await terminal.Feed("/quit\r"); Equal(0, await original);
            Equal("", errors.ToString()); terminal.AssertJoined(); await files.Complete(e);
            Equal(before, StartupOwnedFiles.Hash(files.Session));
            Check(!trace.Records().Any(r => r.Value.GetProperty("type").GetString() == "agent_start"), "Restoration started provider work.");
            Check(!File.Exists(files.Target), "Restoration performed an unrelated effect.");
            e.Observe("actual-native-key-rpc-atomic-take-draft-edit-and-empty-second-take", new { first, second, terminal = terminal.Evidence, trace = trace.Rows() });
        }
        finally { cancellation.Cancel(); terminal.End(); terminal.Release(); await original; terminal.AssertJoined(); }
    }

    private static bool QueueSize(JsonData r, int steering, int followUp) =>
        r.Value.GetProperty("type").GetString() == "queue_update" &&
        r.Value.GetProperty("steering").GetArrayLength() == steering && r.Value.GetProperty("followUp").GetArrayLength() == followUp;
    private static bool RestoreCount(JsonData r, int count) => r.Value.GetProperty("type").GetString() == "response" &&
        r.Value.GetProperty("command").GetString() == "pisharp_restore_queue" && r.Value.GetProperty("success").GetBoolean() &&
        r.Value.GetProperty("data").GetProperty("count").GetInt32() == count;

    private static JsonData Reply(JsonData request, string text, int count) => JsonData.Parse(JsonSerializer.Serialize(new
    {
        type = "response", id = request.Value.GetProperty("id").GetString(), command = "pisharp_restore_queue", success = true,
        data = new { generation = request.Value.GetProperty("generation").GetInt64(), text, count }
    }));
    private static void Equal<T>(T expected, T actual) => TerminalSelectDialogCases.Equal(expected, actual);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
