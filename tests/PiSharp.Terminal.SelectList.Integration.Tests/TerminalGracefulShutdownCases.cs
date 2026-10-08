using PiSharp.Cli.Commands;

// Authored only: the public configured entry point owns every original I/O task.
internal static class TerminalGracefulShutdownCases
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    internal static IEnumerable<(string Id, Func<ConsumerEvidence, Task> Run)> Cases(string[] args) =>
    [
        ("shutdown-public-ctrl-c-success-only-after-terminal-leave", e => Run(args[1], e)),
        ("shutdown-public-cleanup-io-failure-retains-failure", e => Run(args[1], e, leaveFailure: new IOException("AUTHORED_LEAVE_FAILURE"))),
        ("shutdown-public-unrelated-cleanup-cancellation-is-failure", e => Run(args[1], e, leaveFailure: new OperationCanceledException())),
        ("shutdown-public-external-cancellation-is-not-user-success", e => Run(args[1], e, external: true)),
        ("shutdown-public-external-cancellation-during-user-cleanup-is-failure", e => Run(args[1], e, externalDuringLeave: true)),
        ("shutdown-public-late-held-paint-failure-is-not-hidden", e => LatePaint(args[1], e, new IOException("AUTHORED_LATE_PAINT_FAILURE"))),
        ("shutdown-public-unrelated-paint-cancellation-is-not-hidden", e => LatePaint(args[1], e, new OperationCanceledException(new CancellationToken(true))))
    ];

    private static async Task Run(string root, ConsumerEvidence e, Exception? leaveFailure = null,
        bool external = false, bool externalDuringLeave = false)
    {
        var files = await StartupOwnedFiles.Create(root, plugin: false, e); var before = StartupOwnedFiles.Hash(files.Session);
        var trace = new StartupTrace(); var terminal = new StartupControlledTerminal(trace, leaveFailure: leaveFailure);
        using var errors = new StringWriter(); using var cancellation = new CancellationTokenSource();
        var original = TerminalSessionCommand.RunConfiguredAsync(files.Args(), terminal, terminal, errors, files.Configuration, cancellation.Token);
        try
        {
            await terminal.WaitWrite("[history]"); await terminal.WaitReads(1);
            var leaving = terminal.HoldWrite("\u001b[?1049l");
            if (external) cancellation.Cancel(); else await terminal.Feed("\u0003\u0003");
            await leaving.Entered.Task.WaitAsync(Deadline);
            if (externalDuringLeave) cancellation.Cancel();
            Check(!original.IsCompleted && terminal.Snapshot.ActiveWrites == 1 && errors.ToString().Length == 0,
                "Exit result or final diagnostics preceded the original terminal leave.");
            leaving.Release.TrySetResult(); var result = await original;
            var success = leaveFailure is null && !external && !externalDuringLeave;
            Check(result == (success ? 0 : 1), "User cancellation classification lost an unrelated failure.");
            Check((errors.ToString().Length == 0) == success, "Success produced diagnostics or a failure disappeared.");
            terminal.AssertJoined(); await files.Complete(e);
            Check(before == StartupOwnedFiles.Hash(files.Session) && !File.Exists(files.Target), "Shutdown changed idle durable data/effects.");
            e.Observe("public-entry-point-after-owned-terminal-leave", new { result, external, externalDuringLeave,
                failure = leaveFailure?.GetType().Name, errors = errors.ToString(), terminal = terminal.Evidence });
        }
        finally { cancellation.Cancel(); terminal.End(); terminal.Release(); await original; terminal.AssertJoined(); }
    }

    private static async Task LatePaint(string root, ConsumerEvidence e, Exception fault)
    {
        var files = await StartupOwnedFiles.Create(root, plugin: false, e);
        var terminal = new StartupControlledTerminal(new StartupTrace(), heldWriteFailure: fault);
        using var errors = new StringWriter(); using var cancellation = new CancellationTokenSource();
        var original = TerminalSessionCommand.RunConfiguredAsync(files.Args(), terminal, terminal, errors, files.Configuration, cancellation.Token);
        try
        {
            await terminal.WaitWrite("[history]"); await terminal.WaitReads(1);
            var paint = terminal.HoldWrite("BLOCKED"); await terminal.Feed("BLOCKED");
            await paint.Entered.Task.WaitAsync(Deadline); await terminal.Feed("\u0003\u0003");
            await paint.Canceled.Task.WaitAsync(Deadline);
            Check(!original.IsCompleted, "Cancellation detached the original paint before its late fault.");
            paint.Release.TrySetResult(); var result = await original;
            Check(result == 1 && errors.ToString().Length != 0, "Late paint fault was converted into graceful success.");
            terminal.AssertJoined(); await files.Complete(e);
            e.Observe("late-paint-fault-after-user-shutdown", new { result, errors = errors.ToString(), terminal = terminal.Evidence });
        }
        finally { cancellation.Cancel(); terminal.End(); terminal.Release(); await original; terminal.AssertJoined(); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
