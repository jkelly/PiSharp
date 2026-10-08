namespace PiSharp.CodingAgent;

public sealed partial class PersistentAgentSession
{
    private Task? _admissionStop;
    private bool _admissionStopped;
    private readonly List<Exception> _ownedCancellationFailures = [];
    private readonly AsyncLocal<bool> _inShutdown = new();
    private int _shutdownCancellationThread;
    private bool IsShutdownCallback => _inShutdown.Value || Volatile.Read(ref _shutdownCancellationThread) == Environment.CurrentManagedThreadId;

    /// <summary>Irreversibly stop admission and join original work without releasing Agent, store or runtime resources.
    /// No caller cancellation may detach this owned settlement. DisposeAsync remains the resource-release phase.</summary>
    public Task StopAdmissionAndJoinAsync()
    {
        if (IsShutdownCallback) throw new InvalidOperationException("Shutdown callbacks cannot await their own session settlement.");
        ThrowConfigurationSelfWait();
        var agentIdle = _agent.WaitForIdleAsync();
        TaskCompletionSource? completion = null; Task settled; Task idle = Task.CompletedTask;
        lock (_gate)
        {
            if (_admissionStop is null)
            {
                if (_replacing && !_retired) throw new InvalidOperationException("Session replacement is in progress.");
                _admissionStopped = true;
                idle = Task.WhenAll(new[] { agentIdle, _active?.Task ?? Task.CompletedTask, _inputSubmission?.Idle.Task ?? Task.CompletedTask, LoadoutDiagnosticIdleLocked(), RetrySettingsIdleLocked() }.Concat(CaptureUserBashCompletionsLocked()));
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _admissionStop = completion.Task;
            }
            settled = _admissionStop;
        }
        if (completion is not null) _ = StopAdmissionCoreAsync(idle, completion);
        return settled;
    }

    private async Task StopAdmissionCoreAsync(Task idle, TaskCompletionSource completion)
    {
        var prior = _inShutdown.Value; _inShutdown.Value = true;
        var failures = new List<Exception>();
        try
        {
            Task bashAbort;
            try { bashAbort = AbortUserBashAsync(); } catch (Exception error) { AddDistinctFailure(failures, error); bashAbort = Task.CompletedTask; }
            try { InvokeShutdownCancellation(() => _closing.Cancel()); } catch (Exception error) { RetainOwnedCancellationFailure(error, input: false); }
            try { InvokeShutdownCancellation(() => Abort()); } catch (Exception error) { AddDistinctFailure(failures, error); }
            try { await idle.ConfigureAwait(false); } catch (Exception error) { AddDistinctFailure(failures, error); }
            try { await bashAbort.ConfigureAwait(false); } catch (Exception error) { AddDistinctFailure(failures, error); }
            try { await FlushPendingUserBashMessagesAsync().ConfigureAwait(false); } catch (Exception error) { AddDistinctFailure(failures, error); }
            try { await DrainLoadoutAtCloseAsync().ConfigureAwait(false); } catch (Exception error) { AddDistinctFailure(failures, error); }
            lock (_gate)
            {
                var ordered = new List<Exception>(_ownedCancellationFailures);
                foreach (var error in failures) AddDistinctFailure(ordered, error);
                failures = ordered;
            }
            // Agent currently exposes only its boolean failure receipt; its source is a separately owned contract.
            if (_agent.Snapshot.CancellationCallbackFailed)
                failures.Add(new InvalidOperationException("An owned Agent run cancellation callback failed."));
        }
        catch (Exception error) { AddDistinctFailure(failures, error); }
        finally { _inShutdown.Value = prior; }
        if (failures.Count == 0) completion.TrySetResult(); else completion.TrySetException(new AggregateException(failures));
    }
    private void RetainInputCancellationFailure(Exception error)
        => RetainOwnedCancellationFailure(error, input: true);
    private void RetainOwnedCancellationFailure(Exception error, bool input)
    {
        lock (_gate)
        {
            _inputCancellationCallbackFailed |= input;
            AddDistinctFailure(_ownedCancellationFailures, error);
        }
    }
    private static void AddDistinctFailure(List<Exception> failures, Exception error)
    {
        // Cancel(false) aggregates every callback in execution order. Preserve original leaves, including
        // nested linked-token aggregates, without recording the same instance again on repeated Abort/close.
        if (error is AggregateException { InnerExceptions.Count: > 0 } aggregate)
        { foreach (var inner in aggregate.InnerExceptions) AddDistinctFailure(failures, inner); }
        else if (!failures.Any(existing => ReferenceEquals(existing, error))) failures.Add(error);
    }
    private void InvokeShutdownCancellation(Action action)
    {
        // Register callbacks may restore an older ExecutionContext. Their synchronous thread still belongs to this cancel join.
        var prior = Interlocked.Exchange(ref _shutdownCancellationThread, Environment.CurrentManagedThreadId);
        try { action(); } finally { Volatile.Write(ref _shutdownCancellationThread, prior); }
    }
}
