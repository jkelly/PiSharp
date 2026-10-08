using System.Runtime.ExceptionServices;
using PiSharp.Agent;

namespace PiSharp.CodingAgent;

public sealed partial class PersistentAgentSession
{
    // This admission lease is independent of SummaryCore's compaction reservation. A summary can release
    // its own reservation while its operation event callbacks still need the stable Bash boundary.
    private TaskCompletionSource? _automaticBashBoundaryOwner;

    private void ThrowAutomaticBashBoundaryLocked()
    {
        if (_automaticBashBoundaryOwner is not null)
            throw new InvalidOperationException("Automatic session work has fenced user Bash admission.");
    }

    private sealed class UserBashBoundaryLease(PersistentAgentSession session, TaskCompletionSource owner) : IDisposable
    {
        private bool released;
        public void Dispose()
        {
            lock (session._gate)
            {
                if (released) return;
                released = true;
                if (ReferenceEquals(session._automaticBashBoundaryOwner, owner)) session._automaticBashBoundaryOwner = null;
            }
        }
    }

    /// <summary>Owns actual Bash originals and acknowledged message flush at the joined provider boundary.
    /// No cancellation token is accepted: cancellation must not replace the original settlement.</summary>
    private async Task<IDisposable> BeginUserBashBoundaryAsync(TaskCompletionSource providerOwner)
    {
        Task[] originals;
        UserBashBoundaryLease lease;
        lock (_gate)
        {
            if (!ReferenceEquals(_active, providerOwner) || _agent.Snapshot.IsRunning)
                throw new InvalidOperationException("Bash synchronization requires the original joined provider owner.");
            ThrowAutomaticBashBoundaryLocked();
            _automaticBashBoundaryOwner = providerOwner;
            lease = new(this, providerOwner);
            try { originals = CaptureUserBashCompletionsLocked(); }
            catch { lease.Dispose(); throw; }
        }
        try
        {
            var failures = new List<Exception>();
            foreach (var original in originals)
                try { await original.ConfigureAwait(false); }
                catch (Exception error) { AddDistinctFailure(failures, error); }
            // Preserve every original failure, and leave queued messages for their acknowledged owner.
            // A failed executor/progress/persistence join must never initiate a summary from unseen state.
            if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1) throw new AggregateException("User Bash boundary originals failed.", failures);
            await FlushPendingUserBashMessagesAsync().ConfigureAwait(false);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private async Task<RecoveryDecision> RunAutomaticBoundaryAsync(AgentLoopResult result, TaskCompletionSource idle,
        CancellationToken token, long operation, bool attempted)
    {
        bool configured;
        lock (_gate) configured = _automaticCompaction is not null;
        IDisposable? bashBoundary = null;
        try
        {
            if (configured)
            {
                SetOperationPhase(SessionOperationPhase.Compaction);
                bashBoundary = await BeginUserBashBoundaryAsync(idle).ConfigureAwait(false);
            }
            var recovery = token.IsCancellationRequested ? RecoveryDecision.None :
                await TryRecoveryAsync(result, idle, token, operation, attempted).ConfigureAwait(false);
            if (!recovery.Handled && !token.IsCancellationRequested)
            {
                SetOperationPhase(SessionOperationPhase.Compaction);
                await RunConfiguredAutomaticCompactionAsync(idle, token).ConfigureAwait(false);
            }
            return recovery;
        }
        finally { bashBoundary?.Dispose(); }
    }
}
