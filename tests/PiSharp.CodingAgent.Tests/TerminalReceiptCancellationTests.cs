using System.Threading.Channels;
using System.Collections.Immutable;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Interactive;
using PiSharp.Tui;

// Authored deterministic controls; original waits/input callbacks are joined in finally.
internal static class TerminalReceiptCancellationTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("terminal-session.receipt-wait-owned-parent-original-cause-and-late-parent-control", Ownership),
        ("terminal-session.receipt-wait-foreign-callback-original-join", () => Callback(false)),
        ("terminal-session.receipt-wait-late-io-original-join", () => Callback(true)),
        ("terminal-session.receipt-wait-cancel-callback-cause-remains-failure", CancellationCallback),
        ("terminal-session.receipt-primary-held-physical-late-io-original-join", () => Physical("io")),
        ("terminal-session.receipt-primary-held-physical-foreign-oce-original-join", () => Physical("foreign")),
        ("terminal-session.receipt-primary-held-physical-cancel-callback-original-join", () => Physical("callback")),
        ("terminal-session.physical-primary-producer-notification-stop-callback-causes", ProducerNotification)
    ];

    private static async Task Ownership()
    {
        using var parent = new CancellationTokenSource(); using var wait = CancellationTokenSource.CreateLinkedTokenSource(parent.Token);
        var receipts = new TerminalSubmissionReceipts(); var original = receipts.WaitForChangeAsync(wait.Token, parent.Token).AsTask();
        try
        {
            Check(!original.IsCompleted, "Receipt wait did not enter its actual channel."); parent.Cancel();
            var error = await Failure(original);
            Check(error is TerminalReceiptWaitCanceledException, "Local channel cancellation lost its proof.");
            var canceled = (TerminalReceiptWaitCanceledException)error!;
            Check(ReferenceEquals(canceled.Original, canceled.InnerException) && canceled.Original.CancellationToken == wait.Token &&
                canceled.InputOwnerRequested && TerminalSessionCommand.IsOwnedReceiptWaitCancellation(canceled, receipts, parent.Token),
                "Receipt contributor identity or exact original cause was lost.");
            Check(!TerminalSessionCommand.IsOwnedReceiptWaitCancellation(canceled, new TerminalSubmissionReceipts(), parent.Token) &&
                !TerminalSessionCommand.IsOwnedReceiptWaitCancellation(new OperationCanceledException(parent.Token), receipts, parent.Token),
                "Foreign receipt/callback cancellation acquired local wait authority.");
        }
        finally { parent.Cancel(); receipts.Complete(); await Failure(original); }

        using var lateParent = new CancellationTokenSource(); using var local = new CancellationTokenSource();
        var late = new TerminalSubmissionReceipts(); var localOriginal = late.WaitForChangeAsync(local.Token, lateParent.Token).AsTask();
        try
        {
            local.Cancel(); var error = await Failure(localOriginal); lateParent.Cancel();
            Check(error is TerminalReceiptWaitCanceledException canceled && !canceled.InputOwnerRequested &&
                !TerminalSessionCommand.IsOwnedReceiptWaitCancellation(error, late, lateParent.Token),
                "Late parent cancellation rewrote recorded contributors.");
        }
        finally { local.Cancel(); late.Complete(); await Failure(localOriginal); }
    }

    private static async Task Callback(bool io)
    {
        using var caller = new CancellationTokenSource(); using var foreign = new CancellationTokenSource(); foreign.Cancel();
        var marker = io ? (Exception)new IOException("authored late receipt callback IO") : new OperationCanceledException(foreign.Token);
        var entered = Gate(); var release = Gate(); var receipts = new TerminalSubmissionReceipts(); var terminal = new Console();
        var original = new TerminalChatInput(terminal).RunAcknowledgedAsync(async (_, _) =>
        { entered.TrySetResult(); await release.Task; throw marker; }, _ => Task.CompletedTask,
            (_, _) => ValueTask.CompletedTask, receipts, (_, _) => { }, _ => receipts.Complete(), () => { }, caller.Token);
        try
        {
            await terminal.Input.Writer.WriteAsync("x\r");
            if (await Task.WhenAny(entered.Task, original) == original) { await original; throw new InvalidOperationException("Callback was not reached."); }
            await entered.Task; caller.Cancel();
            Check(!original.IsCompleted, "Receipt cancellation detached the admitted original callback.");
            release.TrySetResult(); var error = await Failure(original);
            Check(ReferenceEquals(error, marker), "Receipt cancellation replaced a foreign callback or late IO cause.");
            Check(!TerminalSessionCommand.IsOwnedReceiptWaitCancellation(error!, receipts, caller.Token), "Callback failure acquired wait authority.");
        }
        finally
        {
            release.TrySetResult(); caller.Cancel(); receipts.Complete(); terminal.Input.Writer.TryComplete();
            await Failure(original); Check(terminal.ActiveReads == 0, "Original physical read was not joined.");
        }
    }

    private static async Task CancellationCallback()
    {
        using var parent = new CancellationTokenSource(); using var wait = CancellationTokenSource.CreateLinkedTokenSource(parent.Token);
        var marker = new IOException("authored cancellation callback failure");
        using var registration = wait.Token.Register(() => throw marker);
        var receipts = new TerminalSubmissionReceipts(); var original = receipts.WaitForChangeAsync(wait.Token, parent.Token).AsTask();
        Exception? callbackFailure = null;
        try
        {
            try { parent.Cancel(); } catch (Exception error) { callbackFailure = error; }
            var canceled = await Failure(original);
            Check(canceled is TerminalReceiptWaitCanceledException && callbackFailure is AggregateException aggregate &&
                aggregate.Flatten().InnerExceptions.Any(error => ReferenceEquals(error, marker)), "Cancellation callback original cause was lost.");
            Check(!TerminalSessionCommand.IsOwnedReceiptWaitCancellation(callbackFailure!, receipts, parent.Token),
                "Cancellation callback failure was suppressed as owned receipt cancellation.");
        }
        finally { receipts.Complete(); await Failure(original); }
    }

    private static async Task Physical(string mode)
    {
        using var caller = new CancellationTokenSource(); using var foreign = new CancellationTokenSource(); foreign.Cancel();
        var marker = mode == "foreign" ? (Exception)new OperationCanceledException(foreign.Token) : new IOException("authored physical cleanup failure");
        var terminal = new HeldPhysicalConsole(marker, mode == "callback"); var receipts = new TerminalSubmissionReceipts();
        var submitted = Gate(); var releaseSubmission = Gate(); var inputEnded = Gate();
        Exception? primaryAtEnd = null; TerminalInputFailureFacts? facts = null;
        var original = new TerminalChatInput(terminal).RunAcknowledgedAsync(async (_, _) =>
        { submitted.TrySetResult(); await releaseSubmission.Task; return true; }, _ => Task.CompletedTask,
            (_, _) => ValueTask.CompletedTask, receipts, (_, _) => { }, error =>
            {
                primaryAtEnd = error; receipts.Complete(); inputEnded.TrySetResult();
            }, () => { }, caller.Token, observeFailures: value => facts = value);
        try
        {
            await Reach(submitted.Task, original); await Reach(terminal.Entered.Task, original);
            caller.Cancel(); releaseSubmission.TrySetResult(); await Reach(inputEnded.Task, original);
            Check(primaryAtEnd is TerminalReceiptWaitCanceledException && !original.IsCompleted && terminal.ActiveReads == 1,
                "Receipt cancellation did not win before the held physical original joined.");
            terminal.Release.TrySetResult(); var observed = await Failure(original);
            Check(ReferenceEquals(observed, primaryAtEnd) && facts is not null && ReferenceEquals(facts.Primary, primaryAtEnd),
                "Late physical failure replaced caller cancellation primary.");
            var physicalFailure = terminal.Thrown ?? throw new InvalidOperationException("Physical original cause was not retained.");
            Check(facts!.Secondary.Any(error => ReferenceEquals(error, physicalFailure)) &&
                (mode != "callback" || physicalFailure is AggregateException aggregate &&
                    aggregate.InnerExceptions.Any(error => ReferenceEquals(error, marker))), "Physical cleanup original cause was discarded.");
            Check(facts.Secondary.Length == 1 && ReferenceEquals(facts.Secondary[0], physicalFailure),
                "Repeated physical failure observation duplicated or replaced the original secondary cause.");
            Check(!TerminalSessionCommand.IsOwnedReceiptWaitCancellation(physicalFailure, receipts, caller.Token),
                "Physical secondary failure acquired receipt cancellation authority.");
        }
        finally
        {
            releaseSubmission.TrySetResult(); terminal.Release.TrySetResult(); caller.Cancel(); receipts.Complete();
            await Failure(original); Check(terminal.ActiveReads == 0 && terminal.Started == terminal.Settled,
                "Physical read/cancellation cleanup originals did not join.");
        }
    }

    private static async Task ProducerNotification()
    {
        var physicalFailure = new IOException("authored distinct physical-read failure");
        var callbackFailure = new IOException("authored distinct input-stop callback failure");
        var terminal = new HeldPhysicalConsole(physicalFailure, false); var receipts = new TerminalSubmissionReceipts();
        var submitted = Gate(); var callbackEntered = Gate(); var releaseSubmission = Gate();
        TerminalInputFailureFacts? facts = null;
        var original = new TerminalChatInput(terminal).RunAcknowledgedAsync(async (_, token) =>
        {
            using var registration = token.Register(() => { callbackEntered.TrySetResult(); throw callbackFailure; });
            submitted.TrySetResult(); await releaseSubmission.Task; return true;
        }, _ => Task.CompletedTask, (_, _) => ValueTask.CompletedTask, receipts, (_, _) => { },
            _ => receipts.Complete(), () => { }, default, observeFailures: value => facts = value);
        try
        {
            await Reach(submitted.Task, original); await Reach(terminal.Entered.Task, original);
            terminal.Release.TrySetResult(); await Reach(callbackEntered.Task, original);
            Check(!original.IsCompleted && facts is null, "Producer notification detached its admitted submission callback.");
            releaseSubmission.TrySetResult(); var observed = await Failure(original);
            Check(ReferenceEquals(observed, physicalFailure) && facts is not null && ReferenceEquals(facts.Primary, physicalFailure),
                "Producer cancellation callback replaced physical failure primary.");
            Check(facts!.Secondary.Length == 1 && facts.Secondary[0] is AggregateException aggregate &&
                aggregate.Flatten().InnerExceptions.Any(error => ReferenceEquals(error, callbackFailure)),
                "Actual producerFailed-stop.Cancel aggregate cause was discarded or duplicated.");
            var recorded = new List<Exception>(); TerminalSessionCommand.ObserveInputFailureFacts(facts, recorded.Add);
            Check(recorded.Count == 2 && ReferenceEquals(recorded[0], physicalFailure) && ReferenceEquals(recorded[1], facts.Secondary[0]),
                "Command facts adapter lost original cause order or references.");
            var settlement = new RpcSessionShutdownSettlement([]);
            _ = settlement.AcknowledgeTerminalStopped(recorded.ToImmutableArray());
            var accepted = settlement.CaptureAcceptedTerminalFailures();
            Check(accepted.Length == 2 && ReferenceEquals(accepted[0], physicalFailure) && ReferenceEquals(accepted[1], facts.Secondary[0]),
                "Cleanup acknowledgment replaced or omitted joined producer-notification causes.");
        }
        finally
        {
            terminal.Release.TrySetResult(); releaseSubmission.TrySetResult(); receipts.Complete(); await Failure(original);
            Check(terminal.ActiveReads == 0 && terminal.Started == terminal.Settled, "Producer notification failed to join original physical reads.");
        }
    }

    private static async Task Reach(Task milestone, Task original)
    { if (await Task.WhenAny(milestone, original) == original) { await original; throw new InvalidOperationException("Original ended before milestone."); } await milestone; }

    private sealed class HeldPhysicalConsole(Exception marker, bool callbackFailure) : IConsoleTerminal
    {
        internal readonly TaskCompletionSource Entered = Gate(), Release = Gate();
        internal int ActiveReads, Started, Settled; internal Exception? Thrown;
        private static readonly TerminalConsoleState State = new(0, 0, 65001, 65001, 25, true, 0, 0);
        public TerminalLeaseSnapshot Snapshot => new(State, State, null, false, false, ActiveReads, 0, Started, Settled, 0, 0);
        public async ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default)
        {
            Interlocked.Increment(ref ActiveReads); var number = Interlocked.Increment(ref Started);
            try
            {
                if (number == 1) { "x\r".AsMemory().CopyTo(destination); return 2; }
                Entered.TrySetResult(); await Release.Task;
                if (callbackFailure)
                {
                    using var cleanup = new CancellationTokenSource(); using var registration = cleanup.Token.Register(() => throw marker);
                    try { cleanup.Cancel(); } catch (Exception error) { Thrown = error; throw; }
                }
                Thrown = marker; throw marker;
            }
            finally { Interlocked.Decrement(ref ActiveReads); Interlocked.Increment(ref Settled); }
        }
        public ValueTask WriteAsync(ReadOnlyMemory<char> frame, CancellationToken token = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<Exception?> Failure(Task original)
    { try { await original; return null; } catch (Exception error) { return error; } }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Console : IConsoleTerminal
    {
        internal Channel<string> Input { get; } = Channel.CreateBounded<string>(8);
        internal int ActiveReads;
        private static readonly TerminalConsoleState State = new(0, 0, 65001, 65001, 25, true, 0, 0);
        public TerminalLeaseSnapshot Snapshot => new(State, State, null, false, false, ActiveReads, 0, 0, 0, 0, 0);
        public async ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default)
        {
            Interlocked.Increment(ref ActiveReads);
            try { var text = await Input.Reader.ReadAsync(token); text.AsMemory().CopyTo(destination); return text.Length; }
            finally { Interlocked.Decrement(ref ActiveReads); }
        }
        public ValueTask WriteAsync(ReadOnlyMemory<char> frame, CancellationToken token = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
