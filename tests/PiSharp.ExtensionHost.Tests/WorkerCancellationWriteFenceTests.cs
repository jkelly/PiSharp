using PiSharp.ExtensionHost.Protocol;
using Pair = WorkerProtocolTests.Pair;

internal static class WorkerCancellationWriteFenceTests
{
    private static readonly WorkerFrameCodec Codec = new();

    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("worker-protocol.cancel-fence-orders-nested-callback-error-after-actual-flush", NestedCancellationFlush),
        ("worker-protocol.queued-cancel-fence-waits-for-actual-request-skip", QueuedCancellationSkip),
        ("worker-protocol.normal-and-late-cancellation-fences-remain-not-requested", NormalAndLateCancellation),
        ("worker-protocol.failed-cancel-write-fence-joins-held-shared-cleanup", CancelWriteFault),
        ("worker-protocol.stopped-cancel-write-fence-joins-held-shared-disposal", CancelWriteStopped),
        ("worker-protocol.stopped-queued-call-fence-does-not-invent-a-writer-skip", StoppedQueuedCancellation)
    ];

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<T> Gate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task NestedCancellationFlush()
    {
        var childRequestFlushed = Gate();
        var childReady = Gate();
        var childStarted = Gate<WorkerCall>();
        var childReceived = Gate<WorkerRequestContext>();
        var hostReceived = Gate<WorkerRequestContext>();
        var childCallerCanceled = Gate();
        var childPeerCanceled = Gate();
        var childPeerRelease = Gate();
        var cancelFlushEntered = Gate();
        var cancelFlushRelease = Gate();
        var hostReturned = Gate();
        var hostErrorWritten = Gate();
        using var caller = new CancellationTokenSource();
        Pair? pair = null;
        pair = new Pair(handlerA: async (host, token) =>
        {
            hostReceived.TrySetResult(host);
            var active = pair ?? throw new InvalidOperationException("Pair was not assigned.");
            active.WriterA.FlushFinished = childRequestFlushed;
            var child = active.A.StartRequest("child", WorkerValue.Undefined, cancellationToken: token);
            childStarted.TrySetResult(child);
            await childRequestFlushed.Task;
            active.WriterA.FlushFinished = null;
            active.WriterA.FlushEntered = cancelFlushEntered;
            active.WriterA.FlushGate = cancelFlushRelease;
            childReady.TrySetResult();
            try
            {
                _ = await child.Result;
                throw new InvalidOperationException("The nested child unexpectedly completed normally.");
            }
            catch (WorkerProtocolException error) when (error.Failure == WorkerProtocolFailure.Cancelled)
            {
                childCallerCanceled.TrySetResult();
                // This is the host callback's real dependency, not a test-created ordering task.
                Equal(WorkerCancellationWriteDisposition.Written, await child.CancellationWrite);
                hostReturned.TrySetResult();
                throw new OperationCanceledException(token);
            }
        }, handlerB: async (child, token) =>
        {
            using var listener = token.Register(() => childPeerCanceled.TrySetResult());
            childReceived.TrySetResult(child);
            await childPeerRelease.Task;
            return WorkerValue.Undefined;
        });
        try
        {
            await Start(pair);
            var hostCall = pair.B.StartRequest("host", WorkerValue.Absent, cancellationToken: caller.Token);
            var host = await hostReceived.Task;
            var child = await childStarted.Task;
            var childRequest = await childReceived.Task;
            await childReady.Task;
            False(child.Result.IsCompleted);
            False(child.CancellationWrite.IsCompleted);
            caller.Cancel();
            var canceledHost = await Failure(WorkerProtocolFailure.Cancelled, () => hostCall.Result);
            Equal(WorkerOutcome.Unknown, canceledHost.Outcome);
            var canceledChild = await Failure(WorkerProtocolFailure.Cancelled, () => child.Result);
            Equal(WorkerOutcome.Unknown, canceledChild.Outcome);
            await childCallerCanceled.Task;
            await cancelFlushEntered.Task;
            await childPeerCanceled.Task;
            Equal(WorkerCancellationWriteDisposition.Written, await hostCall.CancellationWrite);
            False(child.CancellationWrite.IsCompleted, "Physical cancel delivery does not settle its held flush.");
            False(hostReturned.Task.IsCompleted, "The host callback must still own its child cancellation fence.");
            Equal(1, pair.A.Snapshot.ActiveCallbacks);
            Equal(1, pair.A.Snapshot.PendingCalls);
            Equal(1, pair.A.Snapshot.PendingWrites);
            True(pair.A.Snapshot.BufferedBytes > 0);
            var heldFrames = Frames(pair.WriterA);
            True(heldFrames.Any(frame => frame.Kind == WorkerMessageKind.Cancel && frame.Id == childRequest.Id));
            False(heldFrames.Any(frame => frame.Kind == WorkerMessageKind.Error && frame.Id == host.Id));

            // The cancel write has already completed and entered its held flush. The next A write is the host error.
            pair.WriterA.WriteFinished = hostErrorWritten;
            cancelFlushRelease.TrySetResult();
            Equal(WorkerCancellationWriteDisposition.Written, await child.CancellationWrite);
            await hostReturned.Task;
            await hostErrorWritten.Task;
            await Drain(hostCall);
            var settledFrames = Frames(pair.WriterA);
            var cancelIndex = Array.FindIndex(settledFrames,
                frame => frame.Kind == WorkerMessageKind.Cancel && frame.Id == childRequest.Id);
            var errorIndex = Array.FindIndex(settledFrames,
                frame => frame.Kind == WorkerMessageKind.Error && frame.Id == host.Id);
            True(cancelIndex >= 0 && errorIndex > cancelIndex, "The physical host error overtook the child's cancel frame.");
            Equal("Cancelled", settledFrames[errorIndex].ErrorCode);
            False(pair.B.Completion.IsCompleted, "A real child cancel crossing the host reply must preserve the peer.");

            childPeerRelease.TrySetResult();
            await Drain(child);
            Equal(0, pair.A.Snapshot.PendingCalls);
        }
        finally
        {
            caller.Cancel();
            cancelFlushRelease.TrySetResult();
            childPeerRelease.TrySetResult();
            await Close(pair);
        }
    }

    private static async Task QueuedCancellationSkip()
    {
        var writeEntered = Gate();
        var writeRelease = Gate();
        var firstReceived = Gate();
        using var caller = new CancellationTokenSource();
        var pair = new Pair(optionsA: new(MaximumPendingCalls: 2), handlerB: (request, _) =>
        {
            Equal("first", request.Method);
            firstReceived.TrySetResult();
            return ValueTask.FromResult(WorkerValue.Undefined);
        });
        try
        {
            await Start(pair);
            pair.WriterA.WriteEntered = writeEntered;
            pair.WriterA.WriteGate = writeRelease;
            var first = pair.A.StartRequest("first", WorkerValue.Absent);
            await writeEntered.Task;
            var queued = pair.A.StartRequest("queued", WorkerValue.Undefined, cancellationToken: caller.Token);
            False(queued.CancellationWrite.IsCompleted);
            caller.Cancel();
            var error = await Failure(WorkerProtocolFailure.Cancelled, () => queued.Result);
            Equal(WorkerOutcome.NotSent, error.Outcome);
            False(queued.CancellationWrite.IsCompleted, "The writer has not yet reached and skipped the queued request.");
            Equal(2, pair.A.Snapshot.PendingCalls);
            Equal(2, pair.A.Snapshot.PendingWrites);
            False(firstReceived.Task.IsCompleted);
            Throws(WorkerProtocolFailure.PendingLimit, () => pair.A.StartRequest("overbound", WorkerValue.Absent));
            False(Frames(pair.WriterA).Any(frame => frame.Kind is WorkerMessageKind.Request or WorkerMessageKind.Cancel));

            pair.WriterA.WriteGate = null;
            writeRelease.TrySetResult();
            Equal(WorkerCancellationWriteDisposition.NotSent, await queued.CancellationWrite);
            Equal(WorkerValuePresence.Undefined, (await first.Result).Presence);
            Equal(WorkerCancellationWriteDisposition.NotRequested, await first.CancellationWrite);
            await Task.WhenAll(Drain(first), Drain(queued));
            Equal(0, pair.A.Snapshot.PendingCalls);
            Equal(0, pair.A.Snapshot.PendingWrites);
            var frames = Frames(pair.WriterA);
            Equal(1, frames.Count(frame => frame.Kind == WorkerMessageKind.Request));
            False(frames.Any(frame => frame.Method == "queued" || frame.Kind == WorkerMessageKind.Cancel));
        }
        finally
        {
            writeRelease.TrySetResult();
            await Close(pair);
        }
    }

    private static async Task NormalAndLateCancellation()
    {
        var entered = Gate();
        var release = Gate();
        using var caller = new CancellationTokenSource();
        var pair = new Pair(handlerB: async (request, _) =>
        {
            if (request.Method == "normal") { entered.TrySetResult(); await release.Task; }
            return request.Value;
        });
        try
        {
            await Start(pair);
            var normal = pair.A.StartRequest("normal", WorkerValue.Undefined, cancellationToken: caller.Token);
            await entered.Task;
            False(normal.Result.IsCompleted);
            False(normal.CancellationWrite.IsCompleted, "A live uncancelled call has not yet settled its disposition.");
            release.TrySetResult();
            Equal(WorkerValuePresence.Undefined, (await normal.Result).Presence);
            Equal(WorkerCancellationWriteDisposition.NotRequested, await normal.CancellationWrite);
            await Drain(normal);
            caller.Cancel();
            Equal(WorkerCancellationWriteDisposition.NotRequested, await normal.CancellationWrite);

            // A complete later round trip crosses the same ordered writer and peer reader.
            var barrier = pair.A.StartRequest("barrier", WorkerValue.Absent);
            Equal(WorkerValuePresence.Absent, (await barrier.Result).Presence);
            Equal(WorkerCancellationWriteDisposition.NotRequested, await barrier.CancellationWrite);
            await Drain(barrier);
            False(Frames(pair.WriterA).Any(frame => frame.Kind == WorkerMessageKind.Cancel));
            False(pair.A.Completion.IsCompleted);
            False(pair.B.Completion.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await Close(pair);
        }
    }

    private static Task CancelWriteFault() => CancelWriteCleanup(faultWrite: true);
    private static Task CancelWriteStopped() => CancelWriteCleanup(faultWrite: false);

    private static async Task CancelWriteCleanup(bool faultWrite)
    {
        var localEntered = Gate();
        var localCanceled = Gate();
        var localRelease = Gate();
        var localRequestFlushed = Gate();
        var remoteEntered = Gate();
        var remoteRelease = Gate();
        var remoteReplyFlushed = Gate();
        var requestFlushed = Gate();
        var cancelWriteEntered = Gate();
        var cancelWriteRelease = Gate();
        using var caller = new CancellationTokenSource();
        var pair = new Pair(handlerA: async (_, token) =>
        {
            using var listener = token.Register(() => localCanceled.TrySetResult());
            localEntered.TrySetResult();
            await localRelease.Task;
            return WorkerValue.Absent;
        }, handlerB: async (_, _) =>
        {
            remoteEntered.TrySetResult();
            await remoteRelease.Task;
            return WorkerValue.Undefined;
        });
        WorkerProtocolFailure? expectedConnectionFailure = null;
        WorkerCall? local = null;
        var remoteAdmitted = false;
        try
        {
            await Start(pair);
            pair.WriterB.FlushFinished = localRequestFlushed;
            local = pair.B.StartRequest("held-local", WorkerValue.Absent);
            await localEntered.Task;
            await localRequestFlushed.Task;
            pair.WriterB.FlushFinished = null;
            pair.WriterA.FlushFinished = requestFlushed;
            var remote = pair.A.StartRequest("held-remote", WorkerValue.Absent, cancellationToken: caller.Token);
            await remoteEntered.Task;
            remoteAdmitted = true;
            await requestFlushed.Task;
            pair.WriterA.FlushFinished = null;
            pair.WriterA.WriteEntered = cancelWriteEntered;
            if (faultWrite)
            {
                expectedConnectionFailure = WorkerProtocolFailure.Transport;
                pair.WriterA.ThrowWrites = true;
            }
            else pair.WriterA.WriteGate = cancelWriteRelease;

            caller.Cancel();
            var canceled = await Failure(WorkerProtocolFailure.Cancelled, () => remote.Result);
            Equal(WorkerOutcome.Unknown, canceled.Outcome);
            await cancelWriteEntered.Task;
            Task firstDisposal;
            Task secondDisposal;
            if (faultWrite)
            {
                await Failure(WorkerProtocolFailure.Transport, () => remote.CancellationWrite);
                firstDisposal = pair.A.DisposeAsync().AsTask();
                secondDisposal = pair.A.DisposeAsync().AsTask();
            }
            else
            {
                False(remote.CancellationWrite.IsCompleted, "The actual cancel write is still held before delivery.");
                firstDisposal = pair.A.DisposeAsync().AsTask();
                secondDisposal = pair.A.DisposeAsync().AsTask();
                await Failure(WorkerProtocolFailure.Closed, () => remote.CancellationWrite);
            }
            await localCanceled.Task;
            True(ReferenceEquals(firstDisposal, secondDisposal));
            True(ReferenceEquals(firstDisposal, pair.A.Completion));
            False(firstDisposal.IsCompleted, "A fence fault cannot abandon an actual held local callback.");
            True(pair.A.Snapshot.Stopped);
            Equal(1, pair.A.Snapshot.ActiveCallbacks);
            Equal(1, pair.A.Snapshot.PendingCalls);
            True(pair.A.Snapshot.BufferedBytes > 0);
            False(Frames(pair.WriterA).Any(frame => frame.Kind == WorkerMessageKind.Cancel));

            localRelease.TrySetResult();
            await CleanupResult(firstDisposal, expectedConnectionFailure);
            await CleanupResult(secondDisposal, expectedConnectionFailure);
            Equal(0, pair.A.Snapshot.ActiveCallbacks);
            Equal(0, pair.A.Snapshot.PendingCalls);
            Equal(0, pair.A.Snapshot.PendingWrites);
            Equal(0L, pair.A.Snapshot.BufferedBytes);
            await Drain(remote);
        }
        finally
        {
            cancelWriteRelease.TrySetResult();
            localRelease.TrySetResult();
            if (remoteAdmitted) pair.WriterB.FlushFinished = remoteReplyFlushed;
            remoteRelease.TrySetResult();
            // B remains open: join its actual reply flush before asking its connection to stop.
            if (remoteAdmitted) await remoteReplyFlushed.Task;
            await Close(pair, expectedConnectionFailure);
        }
        // A's stopped callback could not emit a reply, so B's real cleanup settles this retained call.
        if (local is not null)
        {
            await Failure(WorkerProtocolFailure.Closed, () => local.Result);
            await Failure(WorkerProtocolFailure.Closed, () => local.CancellationWrite);
            await Drain(local);
        }
    }

    private static async Task StoppedQueuedCancellation()
    {
        var writeEntered = Gate();
        var writeRelease = Gate();
        using var caller = new CancellationTokenSource();
        var pair = new Pair(optionsA: new(MaximumPendingCalls: 2));
        try
        {
            await Start(pair);
            pair.WriterA.WriteEntered = writeEntered;
            pair.WriterA.WriteGate = writeRelease;
            var first = pair.A.StartRequest("held-write", WorkerValue.Absent);
            await writeEntered.Task;
            var queued = pair.A.StartRequest("queued", WorkerValue.Absent, cancellationToken: caller.Token);
            caller.Cancel();
            Equal(WorkerOutcome.NotSent,
                (await Failure(WorkerProtocolFailure.Cancelled, () => queued.Result)).Outcome);
            False(queued.CancellationWrite.IsCompleted);
            var firstDisposal = pair.A.DisposeAsync().AsTask();
            var secondDisposal = pair.A.DisposeAsync().AsTask();
            True(ReferenceEquals(firstDisposal, secondDisposal));
            await Failure(WorkerProtocolFailure.Closed, () => queued.CancellationWrite);
            await Failure(WorkerProtocolFailure.Closed, () => first.Result);
            await Failure(WorkerProtocolFailure.Closed, () => first.CancellationWrite);
            await Task.WhenAll(firstDisposal, secondDisposal);
            await Task.WhenAll(Drain(first), Drain(queued));
            Equal(0, pair.A.Snapshot.PendingCalls);
            Equal(0, pair.A.Snapshot.PendingWrites);
            Equal(0L, pair.A.Snapshot.BufferedBytes);
            False(Frames(pair.WriterA).Any(frame => frame.Kind is WorkerMessageKind.Request or WorkerMessageKind.Cancel));
        }
        finally
        {
            writeRelease.TrySetResult();
            await Close(pair);
        }
    }

    // Reuse the real pair, but await its actual owners directly rather than the older helpers' deadline wrappers.
    private static Task Start(Pair pair) => Task.WhenAll(pair.A.StartAsync(), pair.B.StartAsync());

    private static async Task Close(Pair pair, WorkerProtocolFailure? expectedA = null, WorkerProtocolFailure? expectedB = null)
    {
        var a = pair.A.DisposeAsync().AsTask();
        var b = pair.B.DisposeAsync().AsTask();
        try { await Task.WhenAll(a, b); }
        catch { /* Both actual owners are joined before interpreting either failure. */ }
        await CleanupResult(a, expectedA);
        await CleanupResult(b, expectedB);
        Equal(0, pair.ReaderA.DisposeCalls);
        Equal(0, pair.ReaderB.DisposeCalls);
        Equal(0, pair.WriterA.DisposeCalls);
        Equal(0, pair.WriterB.DisposeCalls);
        Equal(0, pair.A.Snapshot.ActiveCallbacks);
        Equal(0, pair.B.Snapshot.ActiveCallbacks);
        Equal(0, pair.A.Snapshot.PendingCalls);
        Equal(0, pair.B.Snapshot.PendingCalls);
        Equal(0, pair.A.Snapshot.PendingWrites);
        Equal(0, pair.B.Snapshot.PendingWrites);
        Equal(0L, pair.A.Snapshot.BufferedBytes);
        Equal(0L, pair.B.Snapshot.BufferedBytes);
    }

    private static async Task CleanupResult(Task task, WorkerProtocolFailure? expected)
    {
        if (expected is { } failure) await Failure(failure, () => task);
        else await task;
    }
    private static async Task Drain(WorkerCall call)
    {
        await foreach (var _ in call.Progress)
            throw new InvalidOperationException("This fixture does not produce progress frames.");
    }
    private static WorkerMessage[] Frames(WorkerProtocolTests.PipeWriter writer) =>
        writer.Frames.Select(frame => Codec.Decode(frame.TrimEnd('\n'))).ToArray();
    private static void Throws(WorkerProtocolFailure expected, Action action)
    {
        try { action(); }
        catch (WorkerProtocolException error) { Equal(expected, error.Failure); return; }
        throw new InvalidOperationException("Expected protocol failure: " + expected);
    }
    private static async Task<WorkerProtocolException> Failure(WorkerProtocolFailure expected, Func<Task> action)
    {
        try { await action(); }
        catch (WorkerProtocolException error) { Equal(expected, error.Failure); return error; }
        throw new InvalidOperationException("Expected protocol failure: " + expected);
    }
    private static void True(bool condition, string message = "Expected true.")
    { if (!condition) throw new InvalidOperationException(message); }
    private static void False(bool condition, string message = "Expected false.") => True(!condition, message);
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
}
