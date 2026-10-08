using PiSharp.Cli.Commands;
using PiSharp.Cli.Interactive;
using PiSharp.Contracts;

// AUTHORED ONLY / UNCOMPILED / UNEXECUTED. Real bounded connection and input owner.
// Milestone deadlines never substitute for the original sender/input/disposal joins.
internal static class TerminalRpcAdmissionCancellationCases
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly JsonData Command = JsonData.Parse("""{"type":"get_state","id":"slot-fixture"}""");
    internal static IEnumerable<(string Id, Func<ConsumerEvidence, Task> Run)> Cases(string[] args) =>
    [
        ("rpc-slot-full-caller-cancellation-retains-actual-linked-cause-and-capacity", e => Connection(e, "caller")),
        ("rpc-slot-full-connection-close-is-fatal", e => Connection(e, "connection")),
        ("rpc-slot-connection-close-before-late-caller-cancel-remains-fatal", e => Connection(e, "late-caller")),
        ("rpc-slot-already-canceled-caller-with-free-slot-does-not-leak-capacity", e => Connection(e, "early")),
        ("rpc-slot-late-cancel-after-admission-does-not-retroactively-fail-original", LateCompleted),
        ("rpc-slot-cancel-after-acquisition-before-channel-write-retains-parent-proof", e => AfterSlot(e, false)),
        ("rpc-slot-callback-private-linked-token-oce-is-never-reclassified", e => AfterSlot(e, true)),
        ("rpc-slot-native-input-clean-user-stop-joins-and-returns-quit", e => Input(e, "user")),
        ("rpc-slot-native-input-caller-stop-is-not-graceful-user-stop", e => Input(e, "caller")),
        ("rpc-slot-native-input-local-wait-connection-failure-after-user-intent", e => Input(e, "connection-wait")),
        ("rpc-slot-native-input-connection-close-during-user-stop-is-fatal", e => Input(e, "connection")),
        ("rpc-slot-native-input-late-private-linked-callback-oce-retains-original", e => Input(e, "private")),
        ("rpc-slot-native-input-late-foreign-callback-oce-retains-original", e => Input(e, "foreign")),
        ("rpc-slot-native-input-late-callback-io-retains-original", e => Input(e, "io")),
        ("rpc-slot-actual-configured-command-clean-control-c-exit-zero-after-leave", e => ActualCommand(args[1], e))
    ];

    private static async Task Connection(ConsumerEvidence e, string mode)
    {
        var connection = new BoundedRpcConnection((_, _) => ValueTask.CompletedTask);
        using var caller = new CancellationTokenSource(); Task? send = null, disposal = null;
        try
        {
            if (mode != "early") await Fill(connection);
            else caller.Cancel();
            send = connection.SendAsync(Command, caller.Token);
            if (mode != "early") Check(!send.IsCompleted, "Full real slots did not hold the original sender.");
            if (mode == "caller") caller.Cancel();
            else if (mode is "connection" or "late-caller")
            {
                disposal = connection.DisposeAsync().AsTask();
                if (mode == "late-caller") caller.Cancel();
            }
            var failure = await Failure(send);
            Check(failure is RpcCommandAdmissionCanceledException, "Actual local linked wait lost its cancellation proof.");
            var admission = (RpcCommandAdmissionCanceledException)failure!;
            Check(ReferenceEquals(admission.Original, admission.InnerException) && admission.Original.CancellationToken != caller.Token &&
                admission.Original.CancellationToken.IsCancellationRequested, "Original private linked-token exception was replaced/discarded.");
            if (mode is "caller" or "early")
            {
                Check(admission.CallerRequested && !admission.ConnectionRequested && admission.CallerToken == caller.Token &&
                    admission.CancellationToken == caller.Token, "Known local caller cancellation did not preserve its actual parent.");
                if (mode == "caller") await Drain(connection, 8);
                await Fill(connection); await Drain(connection, 8); // Same live semaphore/channel capacity.
            }
            else
            {
                Check(admission.ConnectionRequested && admission.CancellationToken == admission.ConnectionToken,
                    "Connection closure became caller/user cancellation after a late caller cancel.");
                Check(ReferenceEquals(disposal, connection.DisposeAsync().AsTask()), "Repeated connection disposal replaced its original task.");
                await disposal!;
            }
            e.Observe("actual-local-slot-cancellation-provenance", new { mode, admission.CallerRequested, admission.ConnectionRequested,
                originalLinkedCauseRetained = true, send = send.Status, disposal = disposal?.Status });
        }
        finally
        {
            caller.Cancel(); if (send is not null) await Failure(send);
            if (disposal is not null) await disposal; await connection.DisposeAsync();
        }
    }

    private static async Task LateCompleted(ConsumerEvidence e)
    {
        await using var connection = new BoundedRpcConnection((_, _) => ValueTask.CompletedTask);
        using var caller = new CancellationTokenSource();
        var original = connection.SendAsync(Command, caller.Token); await original; caller.Cancel(); await original;
        Check(original.IsCompletedSuccessfully, "Late cancellation rewrote an already admitted original command.");
        await Drain(connection, 1);
        e.Observe("admitted-original-stays-successful-after-late-cancel", new { original = original.Status });
    }

    private static async Task AfterSlot(ConsumerEvidence e, bool foreignCallback)
    {
        using var caller = new CancellationTokenSource(); OperationCanceledException? marker = null; var calls = 0;
        var connection = new BoundedRpcConnection((_, _) => ValueTask.CompletedTask, linked =>
        {
            if (Interlocked.Increment(ref calls) != 1) return ValueTask.CompletedTask;
            marker = new OperationCanceledException("AUTHORED_CALLBACK_PRIVATE_LINKED_TOKEN", linked);
            caller.Cancel();
            if (foreignCallback) throw marker;
            return ValueTask.CompletedTask;
        });
        Task? original = null;
        try
        {
            original = connection.SendAsync(Command, caller.Token); var failure = await Failure(original);
            if (foreignCallback)
                Check(ReferenceEquals(failure, marker) && failure is not RpcCommandAdmissionCanceledException,
                    "Arbitrary callback OCE acquired local-wait proof merely because it carried the same canceled linked token.");
            else
                Check(failure is RpcCommandAdmissionCanceledException admission && admission.CallerToken == caller.Token &&
                    admission.CallerRequested && !admission.ConnectionRequested && admission.Original.CancellationToken == marker!.CancellationToken,
                    "Cancellation after slot acquisition lost provenance at the real channel write.");
            await Fill(connection); await Drain(connection, 8);
            e.Observe("slot-acquired-original-channel-callback-races", new { foreignCallback, original = original.Status,
                actualPrivateCallbackErrorPreserved = foreignCallback, sameCapacityRecovered = true });
        }
        finally { caller.Cancel(); if (original is not null) await Failure(original); await connection.DisposeAsync(); }
    }

    private static async Task Input(ConsumerEvidence e, string mode)
    {
        using var caller = new CancellationTokenSource(); using var foreign = new CancellationTokenSource(); foreign.Cancel();
        var receipts = new TerminalSubmissionReceipts(); var terminal = new StartupControlledTerminal(new StartupTrace());
        var held = new SelectorHold(); var sending = SelectorFixture.Signal();
        var interrupted = SelectorFixture.Signal(); var hook = mode is "connection" or "private" or "foreign" or "io";
        Exception? marker = null; Task? originalSend = null, disposal = null; CancellationToken actualInputStop = default;
        var connection = new BoundedRpcConnection((_, _) => ValueTask.CompletedTask, hook ? async linked =>
        {
            if (mode == "private") marker = new OperationCanceledException("AUTHORED_PRIVATE_CALLBACK_CANCELLATION", linked);
            else if (mode == "foreign") marker = new OperationCanceledException("AUTHORED_FOREIGN_CALLBACK_CANCELLATION", foreign.Token);
            else if (mode == "io") marker = new IOException("AUTHORED_LATE_CALLBACK_IO");
            held.Entered.TrySetResult(); using var registration = linked.Register(() => held.Canceled.TrySetResult());
            await held.Release.Task; if (marker is not null) throw marker;
        } : null);
        Task<TerminalInputExit>? originalInput = null;
        try
        {
            if (!hook) await Fill(connection);
            originalInput = new TerminalChatInput(terminal).RunAcknowledgedAsync(async (_, stop) =>
            {
                actualInputStop = stop; originalSend = connection.SendAsync(Command, stop); sending.TrySetResult();
                await originalSend; return true;
            }, _ => Task.CompletedTask, (_, _) => ValueTask.CompletedTask, receipts, (_, _) => { },
                _ => receipts.Complete(), () => { interrupted.TrySetResult(); if (mode != "connection-wait") caller.Cancel(); }, caller.Token, gracefulInterrupt: true);
            await terminal.WaitReads(1); await terminal.Feed("prompt\r"); await sending.Task.WaitAsync(Deadline);
            Check(originalSend is not null && !originalSend.IsCompleted, "Original input did not retain its entered bounded sender.");
            if (hook) await held.Entered.Task.WaitAsync(Deadline);
            if (mode == "connection")
            {
                disposal = connection.DisposeAsync().AsTask();
                Check(!disposal.IsCompleted, "Connection disposal detached its original held admission callback.");
            }
            if (mode == "connection-wait")
            {
                // User intent alone is not provenance: only closing this connection
                // cancels the actual blocked slot wait in this interleaving.
                await terminal.Feed("\u0003"); await interrupted.Task.WaitAsync(Deadline);
                disposal = connection.DisposeAsync().AsTask();
            }
            else if (mode == "caller") caller.Cancel();
            else { await terminal.Feed("\u0003"); await interrupted.Task.WaitAsync(Deadline); }
            if (hook)
            {
                await held.Canceled.Task.WaitAsync(Deadline);
                Check(!originalInput.IsCompleted && !originalSend!.IsCompleted && (disposal is null || !disposal.IsCompleted),
                    "User shutdown detached the original sender/callback/input before its late outcome.");
                held.Release.TrySetResult();
            }
            var sendFailure = await Failure(originalSend!);
            Exception? inputFailure = null; TerminalInputExit? exit = null;
            try { exit = await originalInput; } catch (Exception error) { inputFailure = error; }
            if (mode == "user")
                Check(exit == TerminalInputExit.Quit && inputFailure is null && sendFailure is RpcCommandAdmissionCanceledException userAdmission &&
                    userAdmission.CallerToken == actualInputStop && userAdmission.CancellationToken == actualInputStop && !userAdmission.ConnectionRequested,
                    "Clean user interruption did not preserve actual input-owner cancellation and graceful Quit.");
            else if (mode == "caller")
                Check(inputFailure is RpcCommandAdmissionCanceledException callerAdmission && ReferenceEquals(inputFailure, sendFailure) &&
                    callerAdmission.CallerToken == actualInputStop && !callerAdmission.ConnectionRequested && exit is null,
                    "Ordinary caller cancellation became user success or lost the original admission proof.");
            else if (mode == "connection")
                Check(inputFailure is InvalidOperationException && ReferenceEquals(inputFailure, sendFailure) && exit is null,
                    "Closed connection admission became user success after shutdown flag was set.");
            else if (mode == "connection-wait")
                Check(inputFailure is RpcCommandAdmissionCanceledException connectionAdmission && connectionAdmission.ConnectionRequested &&
                    connectionAdmission.CancellationToken == connectionAdmission.ConnectionToken && ReferenceEquals(inputFailure, sendFailure) && exit is null,
                    "Actual connection-canceled slot wait was swallowed merely because user intent was already recorded.");
            else
                Check(ReferenceEquals(sendFailure, marker) && ReferenceEquals(inputFailure, marker) && exit is null &&
                    inputFailure is not RpcCommandAdmissionCanceledException,
                    "Late arbitrary callback/foreign error was wrapped or swallowed under user shutdown.");
            if (disposal is not null) await disposal;
            terminal.AssertJoined();
            e.Observe("actual-input-owner-bounded-send-original-joins", new { mode, exit = exit?.ToString(),
                failure = inputFailure?.GetType().Name, originalInstanceRetained = mode != "user", sender = originalSend!.Status,
                input = originalInput.Status, disposal = disposal?.Status, terminal = terminal.Evidence });
        }
        finally
        {
            caller.Cancel(); terminal.End(); terminal.Release(); held.Release.TrySetResult(); receipts.Complete();
            if (originalSend is not null) await Failure(originalSend);
            if (originalInput is not null) await Failure(originalInput);
            if (disposal is not null) await disposal;
            await connection.DisposeAsync(); terminal.AssertJoined();
        }
    }

    private static async Task ActualCommand(string root, ConsumerEvidence e)
    {
        var files = await StartupOwnedFiles.Create(root, plugin: false, e); var terminal = new StartupControlledTerminal(new StartupTrace());
        using var errors = new StringWriter(); using var cancellation = new CancellationTokenSource();
        var original = TerminalSessionCommand.RunConfiguredAsync(files.Args(), terminal, terminal, errors, files.Configuration, cancellation.Token);
        try
        {
            await terminal.WaitWrite("[history]"); await terminal.WaitReads(1);
            var leave = terminal.HoldWrite("\u001b[?1049l"); await terminal.Feed("\u0003\u0003"); await leave.Entered.Task.WaitAsync(Deadline);
            Check(!original.IsCompleted && errors.ToString().Length == 0, "Exit/diagnostics preceded the original terminal leave.");
            leave.Release.TrySetResult(); var result = await original;
            Check(result == 0 && errors.ToString().Length == 0, "Clean actual configured Control-C stopped with cancellation failure.");
            terminal.AssertJoined(); await files.Complete(e);
            e.Observe("actual-configured-control-c-original-joins-and-exit-zero", new { result, terminal = terminal.Evidence });
        }
        finally { cancellation.Cancel(); terminal.End(); terminal.Release(); await original; terminal.AssertJoined(); }
    }

    private static async Task Fill(BoundedRpcConnection connection)
    { for (var index = 0; index < 8; index++) { var original = connection.SendAsync(Command, default); await original; } }
    private static async Task Drain(BoundedRpcConnection connection, int count)
    {
        var bytes = new byte[4096];
        for (var index = 0; index < count; index++)
        { var original = connection.Input.ReadAsync(bytes.AsMemory()).AsTask(); Check(await original > 0, "Original admitted frame disappeared."); }
    }
    private static async Task<Exception?> Failure(Task original)
    { try { await original; return null; } catch (Exception error) { return error; } }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
