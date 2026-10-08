using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Cli.Interactive;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Rpc.Ui;
using PiSharp.Tui.Input;

internal static class TerminalSelectDialogCases
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    internal static IEnumerable<(string Id, Func<ConsumerEvidence, Task> Run)> Cases() =>
    [
        ("actual-select-decoder-clamp-unicode-inert-controls-reply-and-draft", Selection),
        ("source-literal-j-k-newline-empty-choice-and-paste-isolation", AliasesAndEmpty),
        ("original-kitty-keybinding-release-and-literal-alias-provenance", Kitty),
        ("configured-selector-keys-source-conflict-precedence-and-cancel", Configured),
        ("queued-retirement-full-identities-duplicate-and-stale-response", Queues),
        ("keys-admitted-for-retired-dialog-cannot-answer-successor", StaleAdmittedKeys),
        ("actual-timeout-restores-draft-focus-and-rejects-late-reply", Timeout),
        ("actual-caller-cancellation-restores-draft-and-session-remains-live", CallerCancel),
        ("actual-eof-retires-dialog-and-joins-input-render-owner", Eof),
        ("actual-disconnect-retires-dialog-without-default-approval", Disconnect),
        ("session-generation-replacement-rejects-old-dialog-and-replay", Replacement),
        ("held-original-selector-paint-timeout-cannot-retire-early", HeldPaintTimeout),
        ("held-original-selector-paint-eof-cannot-release-owner-early", HeldPaintEof),
        ("held-original-answer-send-retirement-keeps-key-owner", HeldSend),
        ("rapid-sequential-retirement-never-leaks-draft-or-focus", Rapid),
        ("fresh-short-viewport-controls-remain-inert-and-draft-intact", Resize),
        ("held-original-read-blocks-owned-cleanup-and-borrowed-disposal", HeldRead),
        ("cooked-numeric-select-and-source-no-ui-defaults-retained", Cooked)
    ];

    private static async Task Selection(ConsumerEvidence e)
    {
        await using var f = await SelectorFixture.Create();
        await f.TypeDraft("retained draft");
        await using var scope = f.Ui.OpenScope(new SelectorContext());
        const string exact = "\u6587\U0001f642\0\u001b[31m";
        var answer = scope.SelectAsync("pick \u6587", ["first", exact, "last"]).AsTask();
        await f.Observer.WaitPublished(1); var snapshot = f.Frontend.CaptureSelectDialog()!;
        await f.Key("\u001b[A"); await f.Key("\u001b[B"); await f.Key("\r");
        var result = await answer.WaitAsync(Deadline);
        e.Observe("actual-scope-decoder-frame-reply", new { result.Kind, result.Value, commands = f.Replies(),
            snapshot.Identity, f.LastDraft, writes = f.Terminal.Writes(), f.Terminal.ReadsStarted, f.Terminal.MaximumReaders });
        Equal(exact, result.Value); Equal(1, f.Replies().Length); Baseline(f.Replies()[0], PublishedIdentity(snapshot).RequestId, exact);
        True(f.Terminal.Writes().Any(write => write.Contains("\u6587", StringComparison.Ordinal)));
        True(f.Terminal.Writes().Any(write => write.Contains("\\u0000\\u001b[31m", StringComparison.Ordinal)));
        True(!f.Terminal.Writes().Any(write => write.Contains("\u001b[31m", StringComparison.Ordinal)));
        Equal("retained draft", f.LastDraft!.Text); True(f.LastDraft!.EditorOwnsFocus); Equal(1, f.Terminal.MaximumReaders);
        await f.Key("!"); Equal("retained draft!", f.LastDraft!.Text); Equal(0, f.Terminal.Disposals);
    }

    private static async Task AliasesAndEmpty(ConsumerEvidence e)
    {
        await using var f = await SelectorFixture.Create(); await f.TypeDraft("draft");
        await using var scope = f.Ui.OpenScope(new SelectorContext());
        var answer = scope.SelectAsync("empty choice", ["", "one", "two"]).AsTask(); await f.Observer.WaitPublished(1);
        await f.Key("\n"); True(!answer.IsCompleted); Equal(0, f.Replies().Length);
        await f.Key("\u001b[200~must not become draft\u001b[201~"); Equal("draft", f.LastDraft!.Text);
        await f.Key("j"); await f.Key("j"); await f.Key("j"); await f.Key("k"); await f.Key("\n");
        Equal("one", (await answer.WaitAsync(Deadline)).Value);
        var none = scope.SelectAsync("no options", []).AsTask(); await f.Observer.WaitPublished(2);
        await f.Key("j"); await f.Key("k"); await f.Key("\n"); True(!none.IsCompleted);
        await f.Key("\u001b"); Equal(ExtensionUiOutcomeKind.Cancelled, (await none.WaitAsync(Deadline)).Kind);
        e.Observe("source-alias-empty-and-paste-consumer", new { replies = f.Replies(), f.LastDraft, none.Status });
        Equal("draft", f.LastDraft!.Text); True(f.LastDraft!.EditorOwnsFocus);
    }

    private static async Task Kitty(ConsumerEvidence e)
    {
        SelectorInputProtocolCases.Run(e);
        await using var f = await SelectorFixture.Create(kitty: true);
        await using var scope = f.Ui.OpenScope(new SelectorContext());
        var answer = scope.SelectAsync("protocol origin", ["first", "second"]).AsTask(); await f.Observer.WaitPublished(1);
        // Source's literal j fallback does not treat Kitty-encoded j as the raw string j.
        await f.Key("\u001b[106u"); await f.Key("\r"); Equal("first", (await answer.WaitAsync(Deadline)).Value);
        // This decoded Down alias is not Source's original-event down binding.
        var negative = scope.SelectAsync("source unmatched release alias", ["first", "second"]).AsTask(); await f.Observer.WaitPublished(2);
        await f.Key("\u001b[57353;1:3u"); await f.Key("\r");
        Equal("first", (await negative.WaitAsync(Deadline)).Value);
        var released = scope.SelectAsync("source release matching", ["first", "second"]).AsTask(); await f.Observer.WaitPublished(3);
        await f.Key("\u001b[1;1:3B"); await f.Key("\r");
        e.Observe("actual-kitty-original-events", new { replies = f.Replies(), negativeStatus = negative.Status, released.Status });
        Equal("second", (await released.WaitAsync(Deadline)).Value);
        var cancelled = scope.SelectAsync("release navigation is not approval", ["first", "second"]).AsTask();
        await f.Observer.WaitPublished(4);
        await f.Key("\u001b[1;1:3B"); True(!cancelled.IsCompleted); Equal(3, f.Replies().Length);
        await f.Key("\u001b");
        Equal(ExtensionUiOutcomeKind.Cancelled, (await cancelled.WaitAsync(Deadline)).Kind);
        e.Observe("release-navigation-then-explicit-cancel", new { cancelled.Status, replies = f.Replies(), f.LastDraft });
    }

    private static async Task Queues(ConsumerEvidence e)
    {
        await using var f = await SelectorFixture.Create();
        await using var a = f.Ui.OpenScope(new SelectorContext(OwnerGeneration: 1));
        await using var b = f.Ui.OpenScope(new SelectorContext(OwnerGeneration: 2));
        await using var c = f.Ui.OpenScope(new SelectorContext(OwnerGeneration: 3));
        var first = a.SelectAsync("first", ["first value"]).AsTask(); await f.Observer.WaitPublished(1);
        var firstSnapshot = f.Frontend.CaptureSelectDialog()!;
        await f.Frontend.LineAsync("1", default); await f.Frontend.LineAsync("/cancel", default); Equal(0, f.Replies().Length);
        var second = b.SelectAsync("second", ["second value"]).AsTask(); await f.Observer.WaitPublished(2);
        var removed = c.SelectAsync("removed queue", ["never selected"]).AsTask(); await f.Observer.WaitPublished(3);
        var identity = PublishedIdentity(firstSnapshot);
        foreach (var stale in new[] { identity with { ConnectionGeneration = 8 }, identity with { SessionGeneration = 8 },
            identity with { OwnerId = "other" }, identity with { OwnerGeneration = 9 } })
            await f.Frontend.RetiredAsync(new(stale, ExtensionUiOutcomeKind.Cancelled, null, true, true), default);
        await f.Frontend.PublishedAsync(f.Observer.Published()[0], default); // duplicate does not enqueue another owner
        await c.DisposeAsync(); Equal(ExtensionUiOutcomeKind.Cancelled, (await removed.WaitAsync(Deadline)).Kind);
        await a.DisposeAsync(); Equal(ExtensionUiOutcomeKind.Cancelled, (await first.WaitAsync(Deadline)).Kind);
        True(!await f.Frontend.RespondSelectAsync(firstSnapshot, 0, false, default));
        await f.Frontend.RetiredAsync(f.Observer.Retired().First(row => row.Identity == identity), default);
        await f.Key("\r"); Equal("second value", (await second.WaitAsync(Deadline)).Value);
        e.Observe("actual-queue-and-identity-retirement", new { replies = f.Replies(), retired = f.Observer.Retired(), f.LastDraft });
        Equal(1, f.Replies().Length); True(f.Frontend.CaptureSelectDialog() is null); True(f.LastDraft!.EditorOwnsFocus);
    }

    private static async Task Configured(ConsumerEvidence e)
    {
        const string configuration = "{\"tui.select.up\":\"x\",\"tui.select.down\":\"y\",\"tui.select.confirm\":\"ctrl+j\",\"tui.select.cancel\":\"z\",\"app.tools.expand\":\"y\"}";
        await using var f = await SelectorFixture.Create(configuration: configuration); await using var scope = f.Ui.OpenScope(new SelectorContext());
        var answer = scope.SelectAsync("configured precedence", ["first", "second", "third"]).AsTask(); await f.Observer.WaitPublished(1);
        await f.Key("\u001b[B"); await f.Key("j"); await f.Key("y"); await f.Key("\n");
        Equal("second", (await answer.WaitAsync(Deadline)).Value);
        var canceled = scope.SelectAsync("configured cancel", ["deny"]).AsTask(); await f.Observer.WaitPublished(2); await f.Key("z");
        Equal(ExtensionUiOutcomeKind.Cancelled, (await canceled.WaitAsync(Deadline)).Kind);
        e.Observe("actual-configured-original-key-dispatch", new { configuration, replies = f.Replies(), retired = f.Observer.Retired() });
    }

    private static async Task StaleAdmittedKeys(ConsumerEvidence e)
    {
        await using var f = await SelectorFixture.Create();
        using var cancellation = new CancellationTokenSource();
        await using var old = f.Ui.OpenScope(new SelectorContext()); await using var fresh = f.Ui.OpenScope(new SelectorContext(OwnerGeneration: 2));
        var answer = old.SelectAsync("old", ["old first", "old second"], cancellationToken: cancellation.Token).AsTask(); await f.Observer.WaitPublished(1);
        var next = fresh.SelectAsync("successor", ["fresh value"]).AsTask(); await f.Observer.WaitPublished(2);
        var held = f.Terminal.HoldWrite("old second");
        try
        {
            f.Terminal.Feed("\u001b[B"); await held.Entered.Task.WaitAsync(Deadline);
            f.Terminal.Feed("\r"); await f.Terminal.WaitDecodedReads(2); cancellation.Cancel();
            // Retirement enters frontend state before its queued view turn can pass the hold.
            await f.Observer.WaitRetiredEntered(1);
            e.Observe("old-key-queue-before-physical-release", new { answer.IsCompleted, nextComplete = next.IsCompleted, f.Terminal.ActiveWrites });
            held.Release.TrySetResult(); Equal(ExtensionUiOutcomeKind.Cancelled, (await answer.WaitAsync(Deadline)).Kind);
            True(!next.IsCompleted); Equal(0, f.Replies().Length);
            await f.Key("\r"); Equal("fresh value", (await next.WaitAsync(Deadline)).Value);
        }
        finally { held.Release.TrySetResult(); }
    }

    private static async Task Timeout(ConsumerEvidence e)
    {
        await using var f = await SelectorFixture.Create(); await f.TypeDraft("timeout draft");
        await using var scope = f.Ui.OpenScope(new SelectorContext());
        var answer = scope.SelectAsync("expires", ["must not approve"], new(12.5)).AsTask(); await f.Observer.WaitPublished(1);
        var old = f.Frontend.CaptureSelectDialog()!; f.Clock.Fire();
        Equal(ExtensionUiOutcomeKind.TimedOut, (await answer.WaitAsync(Deadline)).Kind);
        True(!await f.Frontend.RespondSelectAsync(old, 0, false, default)); f.Ui.AcceptResponse(Response(PublishedIdentity(old).RequestId, "late"));
        e.Observe("actual-timeout-and-late-reply", new { replies = f.Replies(), retired = f.Observer.Retired(), f.LastDraft });
        Equal(0, f.Replies().Length); Equal("timeout draft", f.LastDraft!.Text); True(f.LastDraft!.EditorOwnsFocus);
        await f.Key("!"); Equal("timeout draft!", f.LastDraft!.Text);
    }

    private static async Task CallerCancel(ConsumerEvidence e)
    {
        await using var f = await SelectorFixture.Create(); await f.TypeDraft("caller draft"); using var caller = new CancellationTokenSource();
        await using var scope = f.Ui.OpenScope(new SelectorContext());
        var answer = scope.SelectAsync("cancel caller", ["no"], cancellationToken: caller.Token).AsTask(); await f.Observer.WaitPublished(1); caller.Cancel();
        Equal(ExtensionUiOutcomeKind.Cancelled, (await answer.WaitAsync(Deadline)).Kind);
        await f.Key("!"); e.Observe("actual-caller-cancel-input-survives", new { f.LastDraft, replies = f.Replies() });
        Equal("caller draft!", f.LastDraft!.Text); Equal(0, f.Replies().Length);
    }

    private static async Task Eof(ConsumerEvidence e)
    {
        await using var f = await SelectorFixture.Create(); await using var scope = f.Ui.OpenScope(new SelectorContext());
        var answer = scope.SelectAsync("EOF", ["no default"]).AsTask(); await f.Observer.WaitPublished(1); await f.StopAsync();
        e.Observe("actual-eof-owned-cleanup", f.Snapshot());
        True((await answer.WaitAsync(Deadline)).Kind != ExtensionUiOutcomeKind.Value); Equal(0, f.Replies().Length);
        Equal(0, f.Terminal.ActiveWrites); Equal(0, f.Terminal.ActiveReads); Equal(0, f.Terminal.Disposals);
    }

    private static async Task Disconnect(ConsumerEvidence e)
    {
        await using var f = await SelectorFixture.Create(); await using var scope = f.Ui.OpenScope(new SelectorContext());
        var answer = scope.SelectAsync("disconnect", ["no"]).AsTask(); await f.Observer.WaitPublished(1);
        await f.Ui.DisposeAsync(); Equal(ExtensionUiOutcomeKind.Unavailable, (await answer.WaitAsync(Deadline)).Kind);
        e.Observe("actual-disconnect-no-default", new { replies = f.Replies(), retired = f.Observer.Retired(), f.Input.IsCompleted });
        Equal(0, f.Replies().Length); True(f.Frontend.CaptureSelectDialog() is null);
    }

    private static async Task Replacement(ConsumerEvidence e)
    {
        await using var f = await SelectorFixture.Create(); await using var oldScope = f.Ui.OpenScope(new SelectorContext());
        var old = oldScope.SelectAsync("old generation", ["old"]).AsTask(); await f.Observer.WaitPublished(1); var oldSnapshot = f.Frontend.CaptureSelectDialog()!;
        await f.Frontend.ObserveAsync(JsonData.Parse("{\"type\":\"session_switched\",\"generation\":2,\"sessionId\":\"replacement\"}"), default);
        True(f.Frontend.CaptureSelectDialog() is null); True(!await f.Frontend.RespondSelectAsync(oldSnapshot, 0, false, default));
        await oldScope.DisposeAsync(); Equal(ExtensionUiOutcomeKind.Cancelled, (await old.WaitAsync(Deadline)).Kind);
        await using var fresh = f.Ui.OpenScope(new SelectorContext(SessionGeneration: 2, OwnerGeneration: 2));
        var answer = fresh.SelectAsync("new generation", ["fresh"]).AsTask(); await f.Observer.WaitPublished(2);
        f.Ui.AcceptResponse(Response(PublishedIdentity(oldSnapshot).RequestId, "old late"));
        await f.Frontend.PublishedAsync(f.Observer.Published()[0], default); await f.Key("\r");
        Equal("fresh", (await answer.WaitAsync(Deadline)).Value);
        e.Observe("actual-generation-bound-selector", new { oldSnapshot.Identity, replies = f.Replies(), retired = f.Observer.Retired() });
        Equal(1, f.Replies().Length);
    }

    private static Task HeldPaintTimeout(ConsumerEvidence e) => HeldPaint(e, eof: false);
    private static Task HeldPaintEof(ConsumerEvidence e) => HeldPaint(e, eof: true);
    private static async Task HeldPaint(ConsumerEvidence e, bool eof)
    {
        await using var f = await SelectorFixture.Create(); await using var scope = f.Ui.OpenScope(new SelectorContext());
        var held = f.Terminal.HoldWrite("\u2192 hold choice"); Task? stopping = null;
        var answer = scope.SelectAsync("held native paint", ["hold choice"], new(7)).AsTask();
        try
        {
            await held.Entered.Task.WaitAsync(Deadline);
            if (eof) { f.Terminal.End(); stopping = f.StopAsync(); } else f.Clock.Fire();
            // The owned physical write deliberately ignores cancellation until released.
            await held.Canceled.Task.WaitAsync(Deadline);
            e.Observe("held-original-native-write", new { eof, answer.IsCompleted, stopCompleted = stopping?.IsCompleted,
                f.Terminal.ActiveWrites, f.Terminal.WritesStarted, f.Terminal.WritesSettled, f.Terminal.Disposals });
            True(!answer.IsCompleted); True(stopping is null || !stopping.IsCompleted); Equal(1, f.Terminal.ActiveWrites); Equal(0, f.Terminal.Disposals);
        }
        finally { held.Release.TrySetResult(); }
        True((await answer.WaitAsync(Deadline)).Kind != ExtensionUiOutcomeKind.Value);
        if (stopping is not null) await stopping;
        else { await f.Key("!"); Equal("!", f.LastDraft!.Text); }
        e.Observe("original-native-write-joined", f.Snapshot()); Equal(0, f.Replies().Length);
    }

    private static async Task HeldSend(ConsumerEvidence e)
    {
        await using var f = await SelectorFixture.Create(); using var caller = new CancellationTokenSource(); await using var scope = f.Ui.OpenScope(new SelectorContext());
        var answer = scope.SelectAsync("held send", ["no late effect"], cancellationToken: caller.Token).AsTask(); await f.Observer.WaitPublished(1);
        var held = f.HoldReply(); f.Terminal.Feed("\r");
        try
        {
            await held.Entered.Task.WaitAsync(Deadline); caller.Cancel(); await f.Observer.WaitRetiredEntered(1);
            e.Observe("held-original-answer-before-retirement", new { answer.IsCompleted, inputComplete = f.Input.IsCompleted, replies = f.Replies() });
            True(!answer.IsCompleted); True(!f.Input.IsCompleted);
        }
        finally { held.Release.TrySetResult(); }
        Equal(ExtensionUiOutcomeKind.Cancelled, (await answer.WaitAsync(Deadline)).Kind);
        await f.Key("!"); Equal("!", f.LastDraft!.Text); Equal(1, f.Replies().Length);
    }

    private static async Task Rapid(ConsumerEvidence e)
    {
        await using var f = await SelectorFixture.Create(); await f.TypeDraft("kept");
        for (var index = 0; index < 16; index++)
        {
            await using var scope = f.Ui.OpenScope(new SelectorContext(OwnerGeneration: index + 1));
            var answer = scope.SelectAsync("retire " + index, ["deny"]).AsTask(); await f.Observer.WaitPublished(index + 1);
            await f.Key(index % 2 == 0 ? "\u001b" : "\u0003"); Equal(ExtensionUiOutcomeKind.Cancelled, (await answer.WaitAsync(Deadline)).Kind);
            Equal("kept", f.LastDraft!.Text); True(f.LastDraft!.EditorOwnsFocus);
        }
        e.Observe("rapid-actual-scope-lifetimes", new { retired = f.Observer.Retired(), f.LastDraft, replies = f.Replies(), f.Terminal.MaximumReaders });
        Equal(16, f.Replies().Length); Equal(16, f.Observer.Retired().Length);
    }

    private static async Task Resize(ConsumerEvidence e)
    {
        await using var f = await SelectorFixture.Create(); await f.TypeDraft("canonical"); await using var scope = f.Ui.OpenScope(new SelectorContext());
        var answer = scope.SelectAsync("safe title\u001b]0;evil\a", ["\u6587\U0001f642\u001b[31m", "other"]).AsTask(); await f.Observer.WaitPublished(1);
        f.Viewport.Value = new(1, 1, 0, 0, 80, 25); await f.View.RefreshViewportAsync(default);
        f.Viewport.Value = new(80, 25, 0, 0, 80, 25); await f.View.RefreshViewportAsync(default);
        await f.Key("\u001b"); Equal(ExtensionUiOutcomeKind.Cancelled, (await answer.WaitAsync(Deadline)).Kind);
        e.Observe("actual-fresh-geometry-native-list", new { writes = f.Terminal.Writes(), f.LastDraft, viewport = f.Viewport.Value });
        Equal("canonical", f.LastDraft!.Text); True(!f.Terminal.Writes().Any(write => write.Contains("\u001b]0;evil", StringComparison.Ordinal) || write.Contains("\u001b[31m", StringComparison.Ordinal)));
    }

    private static async Task HeldRead(ConsumerEvidence e)
    {
        var held = new SelectorHold(); await using var f = await SelectorFixture.Create(heldRead: held); Task? stopping = null;
        try
        {
            await held.Entered.Task.WaitAsync(Deadline); f.Cancellation.Cancel(); stopping = f.StopAsync(); await held.Canceled.Task.WaitAsync(Deadline);
            e.Observe("held-original-read-before-release", new { stopping.IsCompleted, f.Terminal.ActiveReads, f.Terminal.Disposals,
                leftScreen = f.Terminal.Writes().Any(write => write.Contains("\u001b[?1049l", StringComparison.Ordinal)) });
            True(!stopping.IsCompleted); Equal(1, f.Terminal.ActiveReads); Equal(0, f.Terminal.Disposals);
        }
        finally { held.Release.TrySetResult(); if (stopping is not null) await stopping; }
        e.Observe("held-original-read-joined", f.Snapshot()); Equal(0, f.Terminal.ActiveReads);
    }

    private static async Task Cooked(ConsumerEvidence e)
    {
        using var output = new StringWriter(); using var frontend = new InteractiveSessionFrontend(output);
        var commands = new List<JsonData>(); frontend.Bind((record, _) => { commands.Add(record); return Task.CompletedTask; });
        await frontend.ObserveAsync(JsonData.Parse("{\"type\":\"extension_ui_request\",\"id\":\"cooked\",\"method\":\"select\",\"title\":\"legacy\",\"options\":[\"one\",\"two\"]}"), default);
        await frontend.LineAsync("2", default); await frontend.LineAsync("next prompt", default);
        e.Observe("actual-cooked-baseline-and-default-projection", new { commands, display = output.ToString(),
            canceled = ExtensionUiSourceDefaults.Text(ExtensionUiOutcome<string>.Cancelled()), unavailable = ExtensionUiSourceDefaults.Text(ExtensionUiOutcome<string>.Unavailable(ExtensionUiUnavailableReason.NoUi)) });
        Baseline(commands[0], "cooked", "two"); Equal("prompt", commands[1].Value.GetProperty("type").GetString());
        True(ExtensionUiSourceDefaults.Text(ExtensionUiOutcome<string>.Unavailable(ExtensionUiUnavailableReason.NoUi)) is null);
    }

    private static RpcExtensionUiPresentationIdentity PublishedIdentity(TerminalSelectListDialogSnapshot? snapshot) =>
        snapshot?.Identity ?? throw new InvalidOperationException("Published extension selector has no request identity.");
    private static JsonData Response(string id, string value) => JsonData.Parse(JsonSerializer.Serialize(new { type = "extension_ui_response", id, value }));
    private static void Baseline(JsonData response, string id, string value)
    {
        Equal("extension_ui_response", response.Value.GetProperty("type").GetString()); Equal(id, response.Value.GetProperty("id").GetString());
        Equal(value, response.Value.GetProperty("value").GetString()); Equal("id,type,value", string.Join(',', response.Value.EnumerateObject().Select(row => row.Name).Order()));
    }
    internal static void True(bool value) { if (!value) throw new InvalidOperationException("Selector integration assertion failed"); }
    internal static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; actual {actual}"); }
}

internal sealed record SelectorContext(string OwnerId = "selector-owner", long OwnerGeneration = 1,
    long SessionGeneration = 1, CancellationToken OperationCancellationToken = default,
    CancellationToken SessionCancellationToken = default, CancellationToken ExtensionLifetimeCancellationToken = default) : IExtensionSessionContext
{
    public ExtensionSessionSnapshot? SessionSnapshot => new("test-session", SessionGeneration, null, []);
}
