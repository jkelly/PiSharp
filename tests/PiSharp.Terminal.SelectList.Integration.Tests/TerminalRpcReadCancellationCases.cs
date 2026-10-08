using PiSharp.Cli.Commands;
using PiSharp.Cli.Interactive;
using PiSharp.Rpc;
using PiSharp.Rpc.Ui;

// AUTHORED ONLY / UNCOMPILED / UNEXECUTED. Every original read/iterator/host/disposal
// is joined directly. Deadlines guard only entered/canceled observation milestones.
internal static class TerminalRpcReadCancellationCases
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    internal static IEnumerable<(string Id, Func<ConsumerEvidence, Task> Run)> Cases(string[] args) =>
    [
        ("rpc-read-pending-caller-stop-retains-parent-and-original-linked-cause", e => Pending(e, false)),
        ("rpc-read-pending-connection-stop-is-distinct-from-caller-and-cleanup-owned", e => Pending(e, true)),
        ("rpc-read-jsonl-known-read-token-recognizes-local-caller-cancellation", Jsonl),
        ("rpc-read-cleanup-joins-held-original-before-owned-stop-suppression", e => Cleanup(e, "owned")),
        ("rpc-read-cleanup-private-linked-callback-oce-remains-fatal", e => Cleanup(e, "private")),
        ("rpc-read-cleanup-caller-token-callback-oce-remains-fatal", e => Cleanup(e, "caller-token")),
        ("rpc-read-cleanup-foreign-oce-remains-fatal-during-user-and-connection-stop", e => Cleanup(e, "foreign")),
        ("rpc-read-cleanup-retains-original-io-and-cancel-callback-causes", e => Cleanup(e, "two-errors")),
        ("rpc-read-actual-host-owned-user-stop-succeeds-after-original-read-join", e => Host(args[1], e, "user")),
        ("rpc-read-actual-host-external-stop-remains-canceled-failure", e => Host(args[1], e, "external")),
        ("rpc-read-actual-host-private-linked-callback-oce-is-fatal-under-user-stop", e => Host(args[1], e, "private")),
        ("rpc-read-actual-host-foreign-callback-oce-is-fatal-under-user-stop", e => Host(args[1], e, "foreign")),
        ("rpc-read-actual-host-io-fault-is-fatal-under-user-stop", e => Host(args[1], e, "io")),
        ("rpc-read-actual-host-connection-stop-before-late-user-stop-is-fatal", e => Host(args[1], e, "connection"))
    ];

    private static async Task Pending(ConsumerEvidence e, bool closeConnection)
    {
        using var caller = new CancellationTokenSource(); var connection = new BoundedRpcConnection((_, _) => ValueTask.CompletedTask);
        Task<int>? originalRead = null; Task? originalClose = null;
        try
        {
            originalRead = connection.Input.ReadAsync(new byte[64].AsMemory(), caller.Token).AsTask();
            Check(!originalRead.IsCompleted, "The actual empty channel did not hold its original read.");
            if (closeConnection) originalClose = connection.DisposeAsync().AsTask(); else caller.Cancel();
            var failure = await Failure(originalRead);
            Check(failure is RpcCommandReadCanceledException, "Actual private read cancellation lost local provenance.");
            var read = (RpcCommandReadCanceledException)failure!;
            Check(ReferenceEquals(read.Owner, connection) && ReferenceEquals(read.Original, read.InnerException) &&
                read.Original.CancellationToken == read.LinkedToken && read.LinkedToken != caller.Token,
                "Original private linked exception or actual read owner was replaced.");
            if (closeConnection)
                Check(read.ConnectionRequested && read.CancellationToken == read.ConnectionToken && !caller.IsCancellationRequested,
                    "Abrupt connection closure was mislabeled as a caller cancellation or normal EOF.");
            else Check(read.CallerRequested && !read.ConnectionRequested && read.CancellationToken == caller.Token,
                "Actual local read failed to map to the token supplied by its caller.");
            originalClose ??= connection.DisposeAsync().AsTask(); await originalClose;
            Check(ReferenceEquals(originalClose, connection.DisposeAsync().AsTask()), "Repeat disposal replaced the original close task.");
            e.Observe("actual-bounded-pending-read-local-proof", new { closeConnection, read.CallerRequested, read.ConnectionRequested,
                originalRead = originalRead.Status, originalClose = originalClose.Status, originalCauseRetained = true });
        }
        finally
        {
            caller.Cancel(); if (originalRead is not null) await Failure(originalRead);
            if (originalClose is not null) await Failure(originalClose); await Failure(connection.DisposeAsync().AsTask());
        }
    }

    private static async Task Jsonl(ConsumerEvidence e)
    {
        using var caller = new CancellationTokenSource(); var entered = SelectorFixture.Signal();
        var connection = new BoundedRpcConnection((_, _) => ValueTask.CompletedTask,
            afterReadStarted: _ => { entered.TrySetResult(); return ValueTask.CompletedTask; });
        var reader = new JsonlReader(connection.Input); var original = Consume();
        try
        {
            await entered.Task.WaitAsync(Deadline); Check(!original.IsCompleted, "JsonlReader did not retain its original empty channel read.");
            caller.Cancel(); var failure = await Failure(original);
            Check(failure is RpcCommandReadCanceledException read && reader.OwnsCancellation(read) &&
                read.CallerToken != caller.Token && read.CancellationToken == read.CallerToken && !read.ConnectionRequested,
                "The restored parent was not JsonlReader's actual private _readToken.");
            e.Observe("real-jsonl-reader-owner-recognizes-translated-local-read", new { original = original.Status, knownOwnerRecognized = true });
        }
        finally { caller.Cancel(); await Failure(original); await Failure(reader.DisposeAsync().AsTask()); await Failure(connection.DisposeAsync().AsTask()); }
        async Task Consume() { await foreach (var _ in reader.ReadAdmissionsAsync(caller.Token)) { } }
    }

    private static async Task Cleanup(ConsumerEvidence e, string mode)
    {
        using var caller = new CancellationTokenSource(); using var foreign = new CancellationTokenSource(); foreign.Cancel();
        var held = new SelectorHold(); var callbackError = new IOException("AUTHORED_READ_CANCEL_CALLBACK");
        Exception? marker = mode == "two-errors" ? new IOException("AUTHORED_READ_ORIGINAL_IO") : null;
        var connection = new BoundedRpcConnection((_, _) => ValueTask.CompletedTask, afterReadStarted: async linked =>
        {
            if (mode == "private") marker = new OperationCanceledException("AUTHORED_READ_PRIVATE_CALLBACK", linked);
            if (mode == "caller-token") marker = new OperationCanceledException("AUTHORED_READ_CALLER_CALLBACK", caller.Token);
            if (mode == "foreign") marker = new OperationCanceledException("AUTHORED_READ_FOREIGN_CALLBACK", foreign.Token);
            using var registration = linked.Register(() =>
            { held.Canceled.TrySetResult(); if (mode == "two-errors") throw callbackError; });
            held.Entered.TrySetResult(); await held.Release.Task; if (marker is not null) throw marker;
        });
        Task<int>? originalRead = null; Task? originalClose = null;
        try
        {
            originalRead = connection.Input.ReadAsync(new byte[64].AsMemory(), caller.Token).AsTask();
            await held.Entered.Task.WaitAsync(Deadline);
            // Close owns the original connection stop, but does not own callback faults.
            originalClose = connection.DisposeAsync().AsTask(); await held.Canceled.Task.WaitAsync(Deadline);
            if (mode is "caller-token" or "foreign") caller.Cancel();
            Check(!originalRead.IsCompleted && !originalClose.IsCompleted, "Cleanup detached the original held read callback.");
            Check(ReferenceEquals(originalClose, connection.DisposeAsync().AsTask()), "Repeated cleanup lost its original task.");
            held.Release.TrySetResult(); var readFailure = await Failure(originalRead); var closeFailure = await Failure(originalClose);
            if (mode == "owned")
                Check(readFailure is RpcCommandReadCanceledException ownedRead && ownedRead.ConnectionRequested && closeFailure is null,
                    "Cleanup did not suppress exactly its own proven local read stop.");
            else
            {
                Check(readFailure is not RpcCommandReadCanceledException && Contains(readFailure, marker!) && Contains(closeFailure, marker!),
                    "Broad connection-stop suppression discarded an original arbitrary read failure.");
                if (mode != "two-errors")
                    Check(readFailure is RpcCommandReadFailedException foreignRead && ReferenceEquals(foreignRead.Original, marker) &&
                        ReferenceEquals(foreignRead.InnerException, marker) && ReferenceEquals(closeFailure, readFailure),
                        "Foreign cancellation acquired owned authority or its original cause was replaced during cleanup.");
                else Check(Contains(closeFailure, callbackError) && Contains(closeFailure, marker!),
                    "Cancellation callback failure hid the later original read IO failure after joins.");
            }
            e.Observe("actual-read-cleanup-local-ownership-and-original-causes", new { mode, originalRead = originalRead.Status,
                originalClose = originalClose.Status, readFailure = readFailure?.GetType().Name, closeFailure = closeFailure?.GetType().Name });
        }
        finally
        {
            try { caller.Cancel(); } catch (AggregateException error) when (mode == "two-errors" && Contains(error, callbackError)) { }
            held.Release.TrySetResult(); if (originalRead is not null) await Failure(originalRead);
            if (originalClose is not null) await Failure(originalClose); await Failure(connection.DisposeAsync().AsTask());
        }
    }

    private static async Task Host(string root, ConsumerEvidence e, string mode)
    {
        var files = await StartupOwnedFiles.Create(root, plugin: false, e);
        var rpcArgs = files.Args().Where(value => value != "--terminal-preview").ToArray(); rpcArgs[1] = "rpc";
        using var caller = new CancellationTokenSource(); using var foreign = new CancellationTokenSource(); foreign.Cancel();
        var held = new SelectorHold(); var userIntent = 0;
        Exception? marker = mode == "io" ? new IOException("AUTHORED_ACTUAL_HOST_READ_IO") : null;
        var connection = new BoundedRpcConnection((_, _) => ValueTask.CompletedTask, afterReadStarted: async linked =>
        {
            if (mode == "private") marker = new OperationCanceledException("AUTHORED_ACTUAL_HOST_PRIVATE_READ_OCE", linked);
            if (mode == "foreign") marker = new OperationCanceledException("AUTHORED_ACTUAL_HOST_FOREIGN_READ_OCE", foreign.Token);
            using var registration = linked.Register(() => held.Canceled.TrySetResult());
            held.Entered.TrySetResult(); await held.Release.Task; if (marker is not null) throw marker;
        });
        using var errors = new StringWriter(); RpcSessionShutdownSettlement? captured = null;
        var originalHost = RpcSessionCommand.RunWithPresentationAsync(rpcArgs, connection.Input, connection.Output, errors,
            new Presentation(), caller.Token, userShutdown: () => Volatile.Read(ref userIntent) != 0,
            stopTerminalAndJoin: settlement => { captured = settlement; return ValueTask.FromResult(settlement.AcknowledgeTerminalStopped()); });
        Task? originalClose = null;
        try
        {
            await held.Entered.Task.WaitAsync(Deadline); await files.AssertWriterOwned();
            if (mode != "external") Volatile.Write(ref userIntent, 1);
            if (mode == "connection") originalClose = connection.DisposeAsync().AsTask();
            caller.Cancel(); await held.Canceled.Task.WaitAsync(Deadline);
            Check(!originalHost.IsCompleted && (originalClose is null || !originalClose.IsCompleted),
                "Actual host or connection detached the held original read during user/caller cancellation.");
            held.Release.TrySetResult(); var result = await originalHost;
            Check(captured is not null, "Actual host did not publish its joined shutdown settlement.");
            var finalFacts = await captured!.Completion; await captured.RuntimeCleanup;
            Check(result == (mode == "user" ? 0 : 1), "Generic dispatcher checks converted a foreign/connection fault or external cancel to user success.");
            if (marker is not null)
                Check(finalFacts.Any(error => Contains(error, marker)) && errors.ToString().Contains("RpcHostFailed", StringComparison.Ordinal),
                    "Cleared RPC ownership chain lost the original read cause or recategorized operation failure as success.");
            else if (mode == "connection")
                Check(finalFacts.Any(error => Causes(error).Any(cause => cause is RpcCommandReadCanceledException closedRead && closedRead.ConnectionRequested)) &&
                    errors.ToString().Contains("RpcHostFailed", StringComparison.Ordinal), "Abrupt connection stop became ordinary EOF/owned caller cancellation.");
            else if (mode == "user") Check(finalFacts.IsEmpty && errors.ToString().Length == 0, "Clean original read stop retained a spurious fatal cause.");
            if (originalClose is not null) await originalClose;
            await connection.DisposeAsync(); await files.Complete(e);
            e.Observe("actual-host-through-connection-jsonl-dispatcher-cleared-rpc-chain", new { mode, result,
                originalHost = originalHost.Status, originalClose = originalClose?.Status, originalCauseRetained = marker is not null });
        }
        finally
        {
            caller.Cancel(); held.Release.TrySetResult(); await Failure(originalHost);
            if (captured is not null) { await Failure(captured.RuntimeCleanup); await Failure(captured.Completion); }
            if (originalClose is not null) await Failure(originalClose); await Failure(connection.DisposeAsync().AsTask());
        }
    }

    private static bool Contains(Exception? root, Exception original) => root is not null && Causes(root).Any(cause => ReferenceEquals(cause, original));
    private static IEnumerable<Exception> Causes(Exception root)
    {
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance); var pending = new Stack<Exception>(); pending.Push(root);
        while (pending.TryPop(out var current))
        {
            if (!seen.Add(current)) continue; yield return current;
            if (current is AggregateException aggregate) foreach (var cause in aggregate.InnerExceptions) pending.Push(cause);
            else if (current.InnerException is { } inner) pending.Push(inner);
        }
    }
    private static async Task<Exception?> Failure(Task original)
    { try { await original; return null; } catch (Exception error) { return error; } }
    private sealed class Presentation : IRpcExtensionUiPresentationObserver
    {
        public ValueTask PublishedAsync(RpcExtensionUiPresentation value, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask RetiredAsync(RpcExtensionUiRetirement value, CancellationToken token) => ValueTask.CompletedTask;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
