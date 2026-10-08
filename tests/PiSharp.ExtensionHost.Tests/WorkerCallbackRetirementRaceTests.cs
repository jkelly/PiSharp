using System.Threading.Tasks.Sources;
using PiSharp.ExtensionHost.Protocol;
using Pair = WorkerProtocolTests.Pair;

internal static class WorkerCallbackRetirementRaceTests
{
    private static readonly WorkerFrameCodec Codec = new();

    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("worker-callback-retirement.clean-disposal-survives-callback-before-cleanup-cancellation", HealthyRetirement),
        ("worker-callback-retirement.actual-response-write-fault-remains-visible-while-open", ResponseTransportFault)
    ];

    private static Task HealthyRetirement() => RetirementScenario(faultResponseWrite: false);
    private static Task ResponseTransportFault() => RetirementScenario(faultResponseWrite: true);

    private static async Task RetirementScenario(bool faultResponseWrite)
    {
        var source = new ControlledHandlerResult();
        var heldCleanup = new HeldCleanupContext();
        var peerRequestFlushed = Gate();
        var responseWriteEntered = Gate();
        CancellationToken handlerToken = default;
        var pair = new Pair(handlerA: (_, token) =>
        {
            handlerToken = token;
            return source.ValueTask;
        });
        WorkerCall? peerCall = null;
        var bodyCompleted = false;
        try
        {
            // The pumps and actual callback start outside the context used later to hold cleanup.
            await Task.WhenAll(pair.A.StartAsync(), pair.B.StartAsync());
            pair.WriterB.FlushFinished = peerRequestFlushed;
            peerCall = pair.B.StartRequest("retire-handler", WorkerValue.Absent);
            await source.ContinuationAttached.Task;
            await peerRequestFlushed.Task;
            Equal(ValueTaskSourceOnCompletedFlags.None,
                source.Flags & ValueTaskSourceOnCompletedFlags.UseSchedulingContext);
            True(handlerToken.CanBeCanceled);
            False(handlerToken.IsCancellationRequested);
            False(pair.A.Snapshot.Stopped);
            Equal(1, pair.A.Snapshot.ActiveCallbacks);
            True(pair.A.Snapshot.BufferedBytes > 0);
            False(peerCall.Result.IsCompleted);

            if (faultResponseWrite)
            {
                // Companion control: an actual callback response WriteAsync fault while A is open.
                // This control makes no claim of forcing a fault/disposal overlap.
                pair.WriterA.WriteEntered = responseWriteEntered;
                pair.WriterA.ThrowWrites = true;
                True(source.TryComplete(WorkerValue.Undefined));
                await source.ContinuationReturned.Task;
                await responseWriteEntered.Task;
                await Failure(WorkerProtocolFailure.Transport, () => pair.A.Completion);
                var first = pair.A.DisposeAsync().AsTask();
                var second = pair.A.DisposeAsync().AsTask();
                SharedDisposal(pair, first, second);
                await Failure(WorkerProtocolFailure.Transport, () => first);
                await Failure(WorkerProtocolFailure.Transport, () => second);
                True(pair.A.Snapshot.Stopped);
                SettledConnection(pair.A);
                NoPhysicalReply(pair);
            }
            else
            {
                var first = DisposeWithCleanupHeld(pair.A, heldCleanup);
                await heldCleanup.Posted.Task;
                var second = pair.A.DisposeAsync().AsTask();
                SharedDisposal(pair, first, second);
                Equal(1, heldCleanup.PendingPosts);
                True(pair.A.Snapshot.Stopped);
                Equal(1, pair.A.Snapshot.ActiveCallbacks);
                False(handlerToken.IsCancellationRequested,
                    "Stop publication must precede the real cleanup continuation's cancellation.");
                False(first.IsCompleted);

                True(source.TryComplete(WorkerValue.Undefined));
                await source.ContinuationReturned.Task;
                // Queue rejects synchronously after stop. The acknowledged real continuation
                // therefore includes the actual RunCallback catch and callback finalization.
                Equal(0, pair.A.Snapshot.ActiveCallbacks);
                Equal(0, pair.A.Snapshot.PendingWrites);
                Equal(0L, pair.A.Snapshot.BufferedBytes);
                False(handlerToken.IsCancellationRequested);
                Equal(1, heldCleanup.PendingPosts);
                False(first.IsCompleted, "The actual cleanup continuation remains held after callback retirement.");
                Equal(1, pair.B.Snapshot.PendingCalls);
                False(peerCall.Result.IsCompleted);
                NoPhysicalReply(pair);

                heldCleanup.ReleasePostedContinuations();
                // The unchanged baseline is expected to fail here with Closed. The regression
                // requires healthy shared cleanup, rather than accepting that baseline failure.
                await Task.WhenAll(first, second, pair.A.Completion);
                SettledConnection(pair.A);
                Equal(0, heldCleanup.PendingPosts);
                Equal(1, pair.B.Snapshot.PendingCalls);
                False(peerCall.Result.IsCompleted);
            }
            bodyCompleted = true;
        }
        finally
        {
            // Release the actual handler and captured cleanup work even if an assertion fails.
            source.TryComplete(WorkerValue.Undefined);
            heldCleanup.ReleasePostedContinuations();
            var owners = await JoinOwners(pair);
            Exception? resultError = null;
            Exception? fenceError = null;
            var progress = 0;
            if (peerCall is not null)
            {
                resultError = await ObserveFailure(peerCall.Result);
                fenceError = await ObserveFailure(peerCall.CancellationWrite);
                await foreach (var _ in peerCall.Progress) progress++;
            }
            BorrowedIoAndSettledOwners(pair);
            if (bodyCompleted)
            {
                if (faultResponseWrite) await Failure(WorkerProtocolFailure.Transport, () => owners.A);
                else await owners.A;
                await owners.B;
                var closedResult = ProtocolFailure(WorkerProtocolFailure.Closed, resultError);
                Equal(WorkerOutcome.Unknown, closedResult.Outcome);
                ProtocolFailure(WorkerProtocolFailure.Closed, fenceError);
                Equal(0, progress);
            }
            // If the body failed, both actual owners and retained call tasks have still
            // been joined/observed. Preserve the original failure instead of replacing it.
        }
    }

    private static Task DisposeWithCleanupHeld(WorkerProtocolConnection connection, HeldCleanupContext context)
    {
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            return connection.DisposeAsync().AsTask();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void SharedDisposal(WorkerProtocolTests.Pair pair, Task first, Task second)
    {
        True(ReferenceEquals(first, second), "Both disposal calls must share the actual settlement task.");
        True(ReferenceEquals(first, pair.A.Completion));
    }

    private static void NoPhysicalReply(Pair pair)
    {
        var frames = pair.WriterA.Frames.Select(frame => Codec.Decode(frame.TrimEnd('\n'))).ToArray();
        False(frames.Any(frame => frame.Kind is WorkerMessageKind.Response or WorkerMessageKind.Error),
            "The retired or physically failed response must not reach the peer.");
    }

    private static async Task<(Task A, Task B)> JoinOwners(Pair pair)
    {
        var a = pair.A.DisposeAsync().AsTask();
        var b = pair.B.DisposeAsync().AsTask();
        try { await Task.WhenAll(a, b); }
        catch { /* Both actual owners are joined before their outcomes are interpreted. */ }
        return (a, b);
    }

    private static async Task<Exception?> ObserveFailure(Task task)
    {
        try { await task; return null; }
        catch (Exception error) { return error; }
    }

    private static void BorrowedIoAndSettledOwners(Pair pair)
    {
        Equal(0, pair.ReaderA.DisposeCalls);
        Equal(0, pair.ReaderB.DisposeCalls);
        Equal(0, pair.WriterA.DisposeCalls);
        Equal(0, pair.WriterB.DisposeCalls);
        SettledConnection(pair.A);
        SettledConnection(pair.B);
    }

    private static void SettledConnection(WorkerProtocolConnection connection)
    {
        var snapshot = connection.Snapshot;
        True(snapshot.Stopped);
        Equal(0, snapshot.ActiveCallbacks);
        Equal(0, snapshot.PendingCalls);
        Equal(0, snapshot.PendingWrites);
        Equal(0L, snapshot.BufferedBytes);
    }

    // Controls only the real handler await. It never changes coordinator state.
    private sealed class ControlledHandlerResult : IValueTaskSource<WorkerValue>
    {
        private ManualResetValueTaskSourceCore<WorkerValue> core = new() { RunContinuationsAsynchronously = false };
        private int completed;
        internal TaskCompletionSource ContinuationAttached { get; } = Gate();
        internal TaskCompletionSource ContinuationReturned { get; } = Gate();
        internal ValueTaskSourceOnCompletedFlags Flags { get; private set; }
        internal ValueTask<WorkerValue> ValueTask => new(this, core.Version);

        internal bool TryComplete(WorkerValue value)
        {
            if (Interlocked.Exchange(ref completed, 1) != 0) return false;
            core.SetResult(value);
            return true;
        }
        public WorkerValue GetResult(short token) => core.GetResult(token);
        public ValueTaskSourceStatus GetStatus(short token) => core.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token,
            ValueTaskSourceOnCompletedFlags flags)
        {
            Flags = flags;
            core.OnCompleted(_ =>
            {
                try { continuation(state); }
                finally { ContinuationReturned.TrySetResult(); }
            }, null, token, flags);
            ContinuationAttached.TrySetResult();
        }
    }

    // Only the synchronous DisposeAsync call installs this context. Its queued work
    // is the real Cleanup initial yield continuation, released explicitly by the owner.
    private sealed class HeldCleanupContext : SynchronizationContext
    {
        private readonly object gate = new();
        private readonly Queue<(SendOrPostCallback Callback, object? State)> posts = new();
        private bool released;
        internal TaskCompletionSource Posted { get; } = Gate();
        internal int PendingPosts { get { lock (gate) return posts.Count; } }

        public override void Post(SendOrPostCallback callback, object? state)
        {
            bool dispatch;
            lock (gate)
            {
                dispatch = released;
                if (!dispatch) posts.Enqueue((callback, state));
            }
            Posted.TrySetResult();
            if (dispatch) ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
        internal void ReleasePostedContinuations()
        {
            (SendOrPostCallback Callback, object? State)[] pending;
            lock (gate)
            {
                released = true;
                pending = posts.ToArray();
                posts.Clear();
            }
            foreach (var work in pending) work.Callback(work.State);
        }
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<WorkerProtocolException> Failure(WorkerProtocolFailure expected, Func<Task> action)
    {
        try { await action(); }
        catch (WorkerProtocolException error) { Equal(expected, error.Failure); return error; }
        throw new InvalidOperationException("Expected protocol failure: " + expected);
    }
    private static WorkerProtocolException ProtocolFailure(WorkerProtocolFailure expected, Exception? error)
    {
        if (error is not WorkerProtocolException protocol)
            throw new InvalidOperationException("Expected protocol failure: " + expected, error);
        Equal(expected, protocol.Failure);
        return protocol;
    }
    private static void True(bool condition, string message = "Expected true.")
    { if (!condition) throw new InvalidOperationException(message); }
    private static void False(bool condition, string message = "Expected false.") => True(!condition, message);
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
}
