using PiSharp.Cli.Commands;
using PiSharp.Tui;

// AUTHORED ONLY / UNEXECUTED. Actual terminal command and Program entrypoint.
// Deadlines guard milestones only; every original command/read/write/restore is joined directly.
internal static class TerminalTwoPhaseShutdownCases
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    internal static IEnumerable<(string Id, Func<ConsumerEvidence, Task> Run)> Cases(string[] args) =>
    [
        ("two-phase-terminal-ctrl-c-drain-leave-restore-before-runtime-release", e => Configured(args[1], e)),
        ("two-phase-terminal-ctrl-d-receipts-complete-before-input-join", e => Configured(args[1], e, quit: "\u0004")),
        ("two-phase-terminal-leave-and-restore-exact-errors-retained", e => Configured(args[1], e, failures: true)),
        ("two-phase-entrypoint-eof-held-leave-restore-retains-writer", e => EntryPoint(args[1], e, external: false)),
        ("two-phase-entrypoint-repeated-cancel-joins-held-read-and-one-restore", e => EntryPoint(args[1], e, external: true))
    ];

    private static async Task Configured(string root, ConsumerEvidence e, string quit = "\u0003\u0003", bool failures = false)
    {
        var files = await StartupOwnedFiles.Create(root, plugin: false, e);
        var leaveError = failures ? new IOException("AUTHORED_TWO_PHASE_LEAVE") : null;
        var restoreError = failures ? new IOException("AUTHORED_TWO_PHASE_RESTORE") : null;
        var trace = new StartupTrace(); var terminal = new StartupControlledTerminal(trace, leaveFailure: leaveError);
        SelectorHold? drain = null; var clock = new TerminalInputDrainCases.Clock(() => drain = terminal.HoldNextRead());
        var restore = new SelectorHold(); var restoreCalls = 0;
        var phaseOne = new TaskCompletionSource<RpcSessionShutdownSettlement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(); using var errors = new StringWriter();
        async ValueTask Restore()
        {
            Interlocked.Increment(ref restoreCalls); restore.Entered.TrySetResult(); await restore.Release.Task;
            if (restoreError is not null) throw restoreError;
        }
        var original = TerminalSessionCommand.RunObservedConfiguredAsync(files.Args(), terminal, terminal, errors,
            trace.Observe, files.Configuration, cancellation.Token, shutdownTimeProvider: clock,
            restoreTerminalAndJoin: Restore, shutdownObserver: settlement => phaseOne.TrySetResult(settlement));
        try
        {
            await terminal.WaitWrite("[history]"); await terminal.WaitReads(1);
            var leave = terminal.HoldWrite("\u001b[?1049l"); await terminal.Feed(quit);
            var settlement = await phaseOne.Task.WaitAsync(Deadline);
            await clock.Started.Task.WaitAsync(Deadline); await drain!.Entered.Task.WaitAsync(Deadline);
            clock.Advance(50); await drain.Canceled.Task.WaitAsync(Deadline);
            Check(!original.IsCompleted && !settlement.RuntimeCleanup.IsCompleted && !leave.Entered.Task.IsCompleted,
                "Drain cancellation detached its original read or released runtime before terminal acknowledgment.");
            await files.AssertWriterOwned(); drain.Release.TrySetResult(); await leave.Entered.Task.WaitAsync(Deadline);
            Check(!restore.Entered.Task.IsCompleted && !settlement.RuntimeCleanup.IsCompleted,
                "Physical leave write was detached or restore/runtime release began early.");
            leave.Release.TrySetResult(); await restore.Entered.Task.WaitAsync(Deadline);
            Check(restoreCalls == 1 && !original.IsCompleted && !settlement.RuntimeCleanup.IsCompleted &&
                settlement.Session is { IsDisposed: false } && errors.ToString().Length == 0,
                "Acknowledgment/diagnostics preceded the original owned restore.");
            await files.AssertWriterOwned(); restore.Release.TrySetResult(); var result = await original;
            var all = await settlement.Completion; await settlement.RuntimeCleanup;
            Check(result == (failures ? 1 : 0) && restoreCalls == 1, "Terminal restoration was repeated or failure became success.");
            if (failures)
                Check(all.Any(error => ReferenceEquals(error, leaveError)) && all.Any(error => ReferenceEquals(error, restoreError)) &&
                    errors.ToString().Contains("CleanupFailed", StringComparison.Ordinal),
                    "Acknowledgment discarded an original error or post-restore diagnostics attempted a repaint.");
            else Check(errors.ToString().Length == 0 && all.IsEmpty, "Orderly terminal quit retained a synthetic failure.");
            terminal.AssertJoined(); await files.Complete(e);
            e.Observe("actual-terminal-two-phase-original-joins", new { quit, failures, result, restoreCalls,
                cleanupFacts = all.Select(error => error.GetType().Name).ToArray(), terminal = terminal.Evidence });
        }
        finally
        {
            cancellation.Cancel(); terminal.End(); terminal.Release(); restore.Release.TrySetResult();
            await original; terminal.AssertJoined();
        }
    }

    private static async Task EntryPoint(string root, ConsumerEvidence e, bool external)
    {
        var files = await StartupOwnedFiles.Create(root, plugin: false, e);
        var trace = new StartupTrace(); var heldRead = external ? new SelectorHold() : null;
        var inner = new StartupControlledTerminal(trace, readHold: heldRead);
        var terminal = new OwnedTerminal(inner); using var cancellation = new CancellationTokenSource(); using var errors = new StringWriter();
        var opens = 0;
        ValueTask<IConsoleTerminal> Open(CancellationToken token)
        { opens++; return ValueTask.FromResult<IConsoleTerminal>(terminal); }
        var original = PiSharp.Cli.Program.RunTerminalHostAsync(files.Args(), cancellation.Token,
            errorOutput: errors, openOwnedTestTerminal: Open, ownedTestConfiguration: files.Configuration);
        try
        {
            await inner.WaitWrite("[history]"); await inner.WaitReads(1);
            var leave = inner.HoldWrite("\u001b[?1049l");
            if (external)
            {
                await heldRead!.Entered.Task.WaitAsync(Deadline); cancellation.Cancel(); cancellation.Cancel();
                await heldRead.Canceled.Task.WaitAsync(Deadline);
                Check(!original.IsCompleted && !leave.Entered.Task.IsCompleted && terminal.Restores == 0,
                    "Repeated shutdown detached the original held read or began restore early.");
                await files.AssertWriterOwned(); heldRead.Release.TrySetResult();
            }
            else inner.End();
            await leave.Entered.Task.WaitAsync(Deadline);
            Check(!original.IsCompleted && terminal.Restores == 0, "Entrypoint did not join its original leave write.");
            await files.AssertWriterOwned(); leave.Release.TrySetResult(); await terminal.Restore.Entered.Task.WaitAsync(Deadline);
            Check(!original.IsCompleted && terminal.Restores == 1 && errors.ToString().Length == 0,
                "Entrypoint returned/emitted diagnostics before its original restore.");
            inner.AssertJoined(); await files.AssertWriterOwned(); terminal.Restore.Release.TrySetResult();
            var result = await original;
            Check(result == (external ? 1 : 0) && opens == 1 && terminal.Restores == 1 && inner.Disposals == 1,
                "Actual entrypoint lost cancellation status or repeated terminal restoration in fallback.");
            var snapshot = terminal.Snapshot;
            Check(snapshot.ActiveReads == 0 && snapshot.ActiveWrites == 0 &&
                snapshot.ReadWorkersStarted == snapshot.ReadWorkersSettled && snapshot.WriteWorkersStarted == snapshot.WriteWorkersSettled,
                "Entrypoint returned while an original physical operation remained unjoined.");
            await files.Complete(e);
            e.Observe("actual-program-terminal-owned-restore-before-writer-release", new { external, result, opens, terminal.Restores, snapshot });
        }
        finally
        {
            cancellation.Cancel(); inner.End(); inner.Release(); terminal.Restore.Release.TrySetResult(); await original;
        }
    }

    private sealed class OwnedTerminal(StartupControlledTerminal inner) : IConsoleTerminal, ITerminalViewportSource
    {
        private Task? disposal;
        internal readonly SelectorHold Restore = new(); internal int Restores;
        public TerminalLeaseSnapshot Snapshot => inner.Snapshot;
        public TerminalViewport ReadViewport() => inner.ReadViewport();
        public ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default) => inner.ReadAsync(destination, token);
        public ValueTask WriteAsync(ReadOnlyMemory<char> frame, CancellationToken token = default) => inner.WriteAsync(frame, token);
        public ValueTask DisposeAsync() => new(disposal ??= Close());
        private async Task Close()
        {
            Restores++; Restore.Entered.TrySetResult(); await Restore.Release.Task; await inner.DisposeAsync();
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
