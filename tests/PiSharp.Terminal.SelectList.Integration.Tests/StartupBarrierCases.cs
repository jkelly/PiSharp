using PiSharp.Contracts;
using PiSharp.Extensions;

internal static class StartupBarrierCases
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    internal static IEnumerable<(string Id, Func<ConsumerEvidence, Task> Run)> Cases() =>
    [
        ("startup-barrier-focus-and-select-paint-before-physical-read", PaintBeforeRead),
        ("startup-barrier-cancel-closed-gate-without-physical-read", CancelClosedGate),
        ("startup-barrier-cancel-held-original-read-and-join", CancelHeldRead),
        ("startup-barrier-retired-dialog-and-replaced-session-before-release", ReplaceBeforeRelease)
    ];
    private static async Task PaintBeforeRead(ConsumerEvidence e)
    {
        var begin = SelectorFixture.Signal();
        await using var f = await StartupInputFixture.Create(begin.Task);
        await using var scope = f.Ui.OpenScope(new SelectorContext());
        var answer = scope.SelectAsync("startup before physical read", ["first", "second"]).AsTask();
        await f.Observer.WaitPublished(1);
        e.Observe("closed-startup-barrier-after-actual-focus-paint", f.Snapshot());
        Check(f.Terminal.ReadsStarted == 0 && f.Terminal.ActiveReads == 0, "Closed startup gate admitted physical input.");
        Check(!f.LastDraft!.EditorOwnsFocus && f.Terminal.Writes().Any(x => x.Contains("startup before physical read")), "Startup dialog did not paint and acquire applied focus before reading.");
        Check(!answer.IsCompleted && f.Replies().Length == 0, "Startup publication supplied an answer.");
        begin.TrySetResult(); await f.Key("j"); await f.Key("\r");
        var result = await answer.WaitAsync(Deadline);
        await f.StopAsync(); e.Observe("released-startup-barrier-original-owners-joined", f.Snapshot());
        Check(result.Kind == ExtensionUiOutcomeKind.Value && result.Value == "second", "Real decoder did not answer the startup selector.");
        Check(f.Replies().Length == 1 && f.Terminal.MaximumReaders == 1, "Startup selected more than once or introduced another reader.");
        Joined(f);
    }
    private static async Task CancelClosedGate(ConsumerEvidence e)
    {
        var begin = SelectorFixture.Signal();
        await using var f = await StartupInputFixture.Create(begin.Task);
        await using var scope = f.Ui.OpenScope(new SelectorContext());
        var answer = scope.SelectAsync("cancel closed startup", ["must not choose"]).AsTask(); await f.Observer.WaitPublished(1);
        f.Cancellation.Cancel(); await f.StopAsync().WaitAsync(Deadline);
        var result = await answer.WaitAsync(Deadline);
        e.Observe("cancellation-joined-with-physical-gate-still-closed", new { barrierReleased = begin.Task.IsCompleted, outcome = result.Kind, snapshot = f.Snapshot() });
        Check(!begin.Task.IsCompleted && f.Terminal.ReadsStarted == 0, "Cancel-before-start waited for or crossed the physical gate.");
        Check(result.Kind != ExtensionUiOutcomeKind.Value && f.Replies().Length == 0, "Canceled startup selected a default value."); Joined(f);
    }
    private static async Task CancelHeldRead(ConsumerEvidence e)
    {
        var begin = SelectorFixture.Signal(); var held = new SelectorHold();
        await using var f = await StartupInputFixture.Create(begin.Task, heldRead: held);
        await using var scope = f.Ui.OpenScope(new SelectorContext());
        var answer = scope.SelectAsync("held startup reader", ["no late answer"]).AsTask(); await f.Observer.WaitPublished(1);
        begin.TrySetResult(); Task? stopping = null;
        try
        {
            await held.Entered.Task.WaitAsync(Deadline); f.Cancellation.Cancel(); stopping = f.StopAsync();
            await held.Canceled.Task.WaitAsync(Deadline);
            e.Observe("canceled-original-read-before-release", new { stopComplete = stopping.IsCompleted, snapshot = f.Snapshot() });
            Check(!stopping.IsCompleted && f.Terminal.ActiveReads == 1 && f.Terminal.ReadsSettled == 0 && f.Terminal.Disposals == 0, "Startup shutdown detached or disposed the held physical reader.");
        }
        finally { held.Release.TrySetResult(); }
        await stopping!.WaitAsync(Deadline); var result = await answer.WaitAsync(Deadline);
        e.Observe("canceled-original-read-after-joined-release", f.Snapshot());
        Check(result.Kind != ExtensionUiOutcomeKind.Value && f.Replies().Length == 0, "Canceled held read answered a selector."); Joined(f);
    }
    private static async Task ReplaceBeforeRelease(ConsumerEvidence e)
    {
        var begin = SelectorFixture.Signal();
        await using var f = await StartupInputFixture.Create(begin.Task);
        await using var old = f.Ui.OpenScope(new SelectorContext());
        var retired = old.SelectAsync("old startup identity", ["obsolete"]).AsTask(); await f.Observer.WaitPublished(1);
        var snapshot = f.Frontend.CaptureSelectDialog()!;
        await f.Frontend.ObserveAsync(JsonData.Parse("{\"type\":\"session_switched\",\"generation\":2,\"sessionId\":\"startup-replacement\"}"), default);
        await old.DisposeAsync();
        await using var fresh = f.Ui.OpenScope(new SelectorContext(SessionGeneration: 2, OwnerGeneration: 2));
        var current = fresh.SelectAsync("fresh startup identity", ["first", "current"]).AsTask(); await f.Observer.WaitPublished(2);
        // Exercise a stale full identity while the real input consumer is attached and the physical gate remains closed.
        var staleAccepted = await f.Frontend.RespondSelectAsync(snapshot, 0, false, default);
        await f.Frontend.PublishedAsync(f.Observer.Published()[0], default);
        e.Observe("replacement-before-physical-admission", new { snapshot.Identity, staleAccepted, physicalReads = f.Terminal.ReadsStarted, retired = f.Observer.Retired(), active = f.Frontend.CaptureSelectDialog() });
        Check(!staleAccepted && f.Terminal.ReadsStarted == 0 && f.Replies().Length == 0, "Closed-barrier stale identity escaped retirement.");
        begin.TrySetResult(); await f.Key("j"); await f.Key("\r");
        var oldResult = await retired.WaitAsync(Deadline); var result = await current.WaitAsync(Deadline);
        await f.StopAsync(); e.Observe("current-identity-only-after-physical-admission", f.Snapshot());
        Check(oldResult.Kind == ExtensionUiOutcomeKind.Cancelled && result.Kind == ExtensionUiOutcomeKind.Value && result.Value == "current", "Physical gate release answered an obsolete session.");
        var reply = f.Replies().Single().Value;
        Check(reply.GetProperty("id").GetString() == f.Observer.Published()[1].Identity.RequestId, "Reply belongs to retired startup request."); Joined(f);
    }
    private static void Joined(StartupInputFixture f) => Check(f.Terminal.ActiveReads == 0 && f.Terminal.ActiveWrites == 0 &&
        f.Terminal.ReadsStarted == f.Terminal.ReadsSettled && f.Terminal.WritesStarted == f.Terminal.WritesSettled && f.Terminal.Disposals == 0,
        "Startup consumer returned before original I/O joined or disposed its borrowed terminal.");
    internal static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
