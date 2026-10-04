namespace PiSharp.CodingAgent;

public sealed partial class ReplaceableAgentSession
{
    private Task? admissionStop;
    private readonly AsyncLocal<bool> inShutdown = new();
    private int shutdownCancellationThread;

    /// <summary>Close host/attachment admission, cancel and join original discovery/mutation/run work.
    /// Current session/runtime resources remain owned until DisposeAsync.</summary>
    public Task StopAdmissionAndJoinAsync()
    {
        RejectTransitionReentrancy();
        PersistentAgentSession[] attached;
        lock (gate) attached = sessions.ToArray();
        foreach (var session in attached) _ = session.WaitForIdleAsync();
        TaskCompletionSource? completion = null; Task settled;
        lock (gate)
        {
            if (admissionStop is null)
            { closed = true; completion = new(TaskCreationOptions.RunContinuationsAsynchronously); admissionStop = completion.Task; }
            settled = admissionStop;
        }
        if (completion is not null) _ = StopAdmissionCoreAsync(completion);
        return settled;
    }

    private async Task StopAdmissionCoreAsync(TaskCompletionSource completion)
    {
        var prior = inShutdown.Value; inShutdown.Value = true;
        var failures = new List<Exception>();
        try
        {
            try { InvokeShutdownCancellation(() => closing.Cancel()); } catch (Exception error) { failures.Add(error); }
            try { InvokeShutdownCancellation(Abort); } catch (Exception error) { failures.Add(error); }
            Task[] discovery; CancellationTokenSource[] attachedLifetimes;
            lock (gate) { discovery = discoveryOperations.Keys.ToArray(); attachedLifetimes = lifetimes.ToArray(); }
            foreach (var read in discovery) try { await read.ConfigureAwait(false); } catch (Exception) { /* Caller owns read failure; read task includes cleanup. */ }
            foreach (var lifetime in attachedLifetimes)
                try { InvokeShutdownCancellation(() => lifetime.Cancel()); } catch (Exception error) { failures.Add(error); }
            await mutations.WaitAsync().ConfigureAwait(false);
            Task[] retired; PersistentAgentSession[] owned; CancellationTokenSource[] finalLifetimes;
            try
            {
                lock (gate) { retired = retirements.ToArray(); owned = sessions.ToArray(); finalLifetimes = lifetimes.ToArray(); }
            }
            finally { mutations.Release(); }
            // A previously admitted transition may have attached a final lifetime before its original joined.
            foreach (var lifetime in finalLifetimes)
                try { InvokeShutdownCancellation(() => lifetime.Cancel()); } catch (Exception error) { failures.Add(error); }
            foreach (var session in owned)
                try { await session.StopAdmissionAndJoinAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            // Retirement may already have begun before stop; join its original resource cleanup rather than detach it.
            foreach (var retirement in retired) try { await retirement.ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            if (CancellationCallbackFailed) failures.Add(new InvalidOperationException("An attached lifetime cancellation callback failed."));
        }
        catch (Exception error) { failures.Add(error); }
        finally { inShutdown.Value = prior; }
        if (failures.Count == 0) completion.TrySetResult(); else completion.TrySetException(new AggregateException(failures));
    }
    private void InvokeShutdownCancellation(Action action)
    {
        var prior = Interlocked.Exchange(ref shutdownCancellationThread, Environment.CurrentManagedThreadId);
        try { action(); } finally { Volatile.Write(ref shutdownCancellationThread, prior); }
    }
}
