using System.Collections.Immutable;

namespace PiSharp.CodingAgent;

public sealed partial class ReplaceableAgentSession
{
    private readonly Dictionary<AgentSessionAttachment, Dictionary<object, Task>> reloadAttempts = new(ReferenceEqualityComparer.Instance);
    private readonly object defaultReloadAttempt = new();
    private int reloadAttemptCount;
    private readonly AsyncLocal<ReloadReservation?> reloadCallback = new();
    private AgentSessionAttachment? reloadInvalidatedAttachment;

    /// <summary>Concurrent reloads of one exact attachment join one original. A new attempt identity
    /// may retry a completed pre-invalidation rejection. The original task includes staging,
    /// publication, and cleanup; close joins it through the existing retirement list.
    /// Caller cancellation controls admission only. All later work borrows the host lifetime.</summary>
    public Task<T> RunReloadAsync<T>(AgentSessionAttachment expected,
        Func<ReloadReservation, CancellationToken, Task<T>> operation, CancellationToken admissionToken = default,
        object? attemptIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(operation);
        if (operation.GetInvocationList().Length != 1) throw new ArgumentException("Reload requires one joined operation.", nameof(operation));
        var changed = AttachmentChanged;
        RejectTransitionReentrancy(); admissionToken.ThrowIfCancellationRequested();
        TaskCompletionSource<T> completion;
        lock (gate)
        {
            var identity = attemptIdentity ?? defaultReloadAttempt;
            reloadAttempts.TryGetValue(expected, out var attempts);
            if (attempts is not null && attempts.TryGetValue(identity, out var existing))
                return existing as Task<T> ?? throw new InvalidOperationException("This attachment already has a different reload operation.");
            if (reloadAttemptCount >= attachmentLimit) throw new InvalidOperationException("Reload attempt limit reached; restart the host.");
            var pending = attempts?.Values.FirstOrDefault(task => !task.IsCompleted);
            if (pending is not null)
            {
                var joined = pending as Task<T> ?? throw new InvalidOperationException("This attachment already has a different reload operation.");
                attempts!.Add(identity, joined); reloadAttemptCount++; return joined;
            }
            ValidateAttachment(expected);
            if (changed?.GetInvocationList().Length > 1) throw new ArgumentException("Reload attachment notification requires one joined callback.");
            if (lifetimes.Count >= attachmentLimit) throw new InvalidOperationException("Session attachment limit reached; restart the host.");
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (attempts is null)
            { attempts = new(ReferenceEqualityComparer.Instance); reloadAttempts.Add(expected, attempts); }
            attempts.Add(identity, completion.Task); reloadAttemptCount++;
            retirements.Add(completion.Task);
        }
        _ = CompleteReloadAsync(expected, operation, changed, admissionToken, completion);
        return completion.Task;
    }

    private async Task CompleteReloadAsync<T>(AgentSessionAttachment expected,
        Func<ReloadReservation, CancellationToken, Task<T>> operation,
        Func<AgentSessionReplacement, ValueTask>? changed, CancellationToken admissionToken,
        TaskCompletionSource<T> completion)
    {
        var held = false; ReloadReservation? transaction = null;
        PersistentAgentSession.ReplacementReservation? reservation = null;
        var previousTransition = inTransition.Value; var previousCallback = reloadCallback.Value;
        var failures = new List<Exception>(); T result = default!;
        try
        {
            using var admission = CancellationTokenSource.CreateLinkedTokenSource(admissionToken, closing.Token);
            await mutations.WaitAsync(admission.Token).ConfigureAwait(false); held = true;
            inTransition.Value = true;
            ValidateAttachment(expected); admission.Token.ThrowIfCancellationRequested();
            // Idle admission is intentional: a command can reload its own input callback, but
            // must not wait for that callback to finish. Busy execution is rejected before effects.
            reservation = expected.Session.ReserveReplacement();
            CancellationTokenSource oldLifetime;
            lock (gate) oldLifetime = lifetimes[^1];
            transaction = new(this, expected, reservation, oldLifetime, changed);
            reloadCallback.Value = transaction;
            var original = operation(transaction, closing.Token)
                ?? throw new InvalidOperationException("Reload must return its original operation task.");
            result = await AgentSessionReloadTask.Join(original).ConfigureAwait(false);
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            if (transaction is not null)
            {
                try { await transaction.SettleAsync().ConfigureAwait(false); }
                catch (Exception error) { failures.Add(error); }
            }
            reservation?.Dispose();
            reloadCallback.Value = previousCallback; inTransition.Value = previousTransition;
            if (held) mutations.Release();
        }
        if (failures.Count == 0) completion.TrySetResult(result);
        // Existing close awaits each retirement once; make every reload failure reachable
        // through that one awaited exception as well as through Task.Exception.
        else completion.TrySetException(new AggregateException("Reload and original settlement failed.", failures));
    }

    /// <summary>Non-transferable capability held only by the original reload callback.
    /// It uses this owner's actual persistent reservation and never creates another writer.</summary>
    public sealed class ReloadReservation
    {
        private readonly ReplaceableAgentSession owner;
        private readonly PersistentAgentSession.ReplacementReservation reservation;
        private readonly CancellationTokenSource oldLifetime;
        private readonly CancellationTokenSource nextLifetime;
        private readonly SessionRuntimeLease? oldRuntime;
        private readonly Func<AgentSessionReplacement, ValueTask>? changed;
        private readonly AsyncLocal<bool> insidePhase = new();
        private Task? invalidation, oldCleanup;
        private Task<SessionToolCatalogReceipt>? publication;
        private SessionRuntimeLease? preparedRuntime;
        private bool ended, invalidated, transferred, published, terminal;
        public AgentSessionAttachment Previous { get; }
        public AgentSessionAttachment Candidate { get; }
        public SessionRuntimeRegistry CapturedRegistry { get; }
        public ImmutableArray<string> ActiveTools { get; }
        public bool IsPublished { get { lock (owner.gate) return published; } }

        internal ReloadReservation(ReplaceableAgentSession owner, AgentSessionAttachment previous,
            PersistentAgentSession.ReplacementReservation reservation, CancellationTokenSource oldLifetime,
            Func<AgentSessionReplacement, ValueTask>? changed)
        {
            this.owner = owner; Previous = previous; this.reservation = reservation; this.oldLifetime = oldLifetime;
            CapturedRegistry = reservation.CaptureToolCatalogRegistry(); ActiveTools = reservation.CaptureActiveToolNames();
            oldRuntime = previous.Session.CaptureReloadRuntime(reservation);
            this.changed = changed; nextLifetime = new();
            Candidate = new(previous.Session, checked(previous.Generation + 1), nextLifetime.Token);
        }

        private void ValidateCallback()
        {
            if (ended || insidePhase.Value || !ReferenceEquals(owner.reloadCallback.Value, this) || owner.resourceTransaction.Value is not null ||
                owner.resourceStopCallback.Value is not null ||
                Volatile.Read(ref owner.shutdownCancellationThread) == Environment.CurrentManagedThreadId)
                throw new InvalidOperationException("Reload reservation is stale, foreign or reentrant.");
        }

        /// <summary>Claim a fresh candidate during staging, before old authority changes.
        /// The owner settles this lease on every non-publication path, including malformed staging.</summary>
        public void AdmitRuntime(SessionRuntimeLease runtime)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            lock (owner.gate)
            {
                ValidateCallback();
                if (invalidation is not null || preparedRuntime is not null || ReferenceEquals(runtime, oldRuntime))
                    throw new InvalidOperationException("Reload requires one fresh staged runtime before invalidation.");
                runtime.Claim(); preparedRuntime = runtime;
            }
        }

        public Task InvalidateAndDrainAsync()
        {
            TaskCompletionSource completion;
            lock (owner.gate)
            {
                ValidateCallback();
                // Store a promise before invoking any callback, including synchronous cancellation.
                if (invalidation is not null) return invalidation;
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                invalidation = completion.Task;
            }
            _ = InvalidateCoreAsync(completion);
            return completion.Task;
        }

        private async Task InvalidateCoreAsync(TaskCompletionSource completion)
        {
            insidePhase.Value = true;
            var failures = new List<Exception>();
            lock (owner.gate)
            {
                // Close may have stopped admission; an already admitted reload still owns drain.
                if (!ReferenceEquals(owner.current, Previous)) failures.Add(new InvalidOperationException("Reload attachment changed."));
                else { invalidated = true; owner.reloadInvalidatedAttachment = Previous; }
            }
            if (invalidated)
            {
                // A lifetime callback or admitted discovery read may depend on physical
                // stop. Start every owned stop before joining either dependency, just as
                // the owner's normal shutdown path does. Retirement below still joins
                // the original stop/body tasks and retains their complete failures.
                try { await AgentSessionReloadTask.Join(owner.InitiateOwnedResourceStopsAsync(Previous)).ConfigureAwait(false); }
                catch (Exception error) { failures.Add(error); }
                try { owner.InvokeShutdownCancellation(() => oldLifetime.Cancel()); } catch (Exception error) { failures.Add(error); }
                try { owner.InvokeShutdownCancellation(Previous.Session.RetireInvocationOwner); } catch (Exception error) { failures.Add(error); }
                Task[] discovery;
                lock (owner.gate) discovery = owner.discoveryOperations.Keys.ToArray();
                foreach (var read in discovery)
                {
                    try { await AgentSessionReloadTask.Join(read).ConfigureAwait(false); }
                    catch (Exception) when (read.IsCanceled) { /* Old attachment cancellation is the requested invalidation. */ }
                    catch (Exception error) { failures.Add(error); }
                }
                try { await AgentSessionReloadTask.Join(owner.RetireOwnedResourcesAsync(Previous, reservation, joinAll: true)).ConfigureAwait(false); }
                catch (Exception error) { failures.Add(error); }
                try { await AgentSessionReloadTask.Join(reservation.DrainLoadoutDiagnosticsAsync(CancellationToken.None)).ConfigureAwait(false); }
                catch (Exception error) { failures.Add(error); }
            }
            if (failures.Count == 0) completion.TrySetResult(); else completion.TrySetException(failures);
        }

        /// <summary>Install the staged runtime using the existing durable catalog transaction.
        /// The synchronous commit must only publish an already prepared host registry.</summary>
        public Task<SessionToolCatalogReceipt> PublishAsync(SessionRuntimeLease runtime,
            ImmutableArray<string> activeNames, Action commitPreparedRegistry)
        {
            ArgumentNullException.ThrowIfNull(runtime); ArgumentNullException.ThrowIfNull(commitPreparedRegistry);
            if (commitPreparedRegistry.GetInvocationList().Length != 1)
                throw new ArgumentException("Registry publication requires one prepared commit.", nameof(commitPreparedRegistry));
            TaskCompletionSource<SessionToolCatalogReceipt> completion;
            lock (owner.gate)
            {
                ValidateCallback();
                if (invalidation is not { IsCompletedSuccessfully: true } || !invalidated || publication is not null)
                    throw new InvalidOperationException("Reload publication requires the original successful drain and one candidate.");
                if (!ReferenceEquals(runtime, preparedRuntime)) throw new InvalidOperationException("Runtime was not admitted by this reload owner.");
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                publication = completion.Task;
            }
            _ = PublishCoreAsync(runtime, activeNames, commitPreparedRegistry, completion);
            return completion.Task;
        }

        private async Task PublishCoreAsync(SessionRuntimeLease runtime, ImmutableArray<string> activeNames,
            Action commitPreparedRegistry, TaskCompletionSource<SessionToolCatalogReceipt> completion)
        {
            insidePhase.Value = true;
            try
            {
                var receipt = await AgentSessionReloadTask.Join(Previous.Session.PublishReloadAsync(reservation,
                    CapturedRegistry, runtime, activeNames, Candidate.Generation, nextLifetime.Token, () =>
                    {
                        // Called by the persistent catalog transaction under its session gate.
                        // No other owner path enters that gate while holding owner.gate (the
                        // navigation publisher is excluded by this same mutation reservation).
                        lock (owner.gate)
                        {
                            if (!ReferenceEquals(owner.current, Previous) || !ReferenceEquals(owner.reloadInvalidatedAttachment, Previous))
                                throw new InvalidOperationException("Reload compare-and-publish failed.");
                            commitPreparedRegistry();
                            owner.current = Candidate; owner.lifetimes.Add(nextLifetime);
                            owner.reloadInvalidatedAttachment = null; transferred = true;
                        }
                    })).ConfigureAwait(false);
                owner.BindRuntimeUnderReservation(Candidate, reservation);
                bool closing; lock (owner.gate) closing = owner.closed;
                if (closing)
                {
                    await AgentSessionReloadTask.Join(owner.InitiateOwnedResourceStopsAsync(Candidate)).ConfigureAwait(false);
                    owner.InvokeShutdownCancellation(() => nextLifetime.Cancel());
                }
                lock (owner.gate) published = true;
                reservation.Dispose();
                if (changed is not null)
                {
                    var prior = owner.resourceRegistrationAttachment.Value;
                    owner.resourceRegistrationAttachment.Value = Candidate;
                    try { await AgentSessionReloadTask.Join(changed(new(Previous, Candidate) { Reason = "reload" }).AsTask()).ConfigureAwait(false); }
                    finally { owner.resourceRegistrationAttachment.Value = prior; }
                }
                completion.TrySetResult(receipt);
            }
            catch (Exception error)
            {
                terminal = !published;
                completion.TrySetException(error);
            }
        }

        public Task CleanupPreviousRuntimeAsync()
        {
            lock (owner.gate)
            {
                ValidateCallback();
                if (!invalidated) throw new InvalidOperationException("Current runtime is still authoritative.");
            }
            return CleanupOldRuntime();
        }

        private Task CleanupOldRuntime()
        {
            TaskCompletionSource completion;
            lock (owner.gate)
            {
                if (oldCleanup is not null) return oldCleanup;
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously); oldCleanup = completion.Task;
            }
            _ = CleanupOldRuntimeCoreAsync(completion);
            return completion.Task;
        }
        private async Task CleanupOldRuntimeCoreAsync(TaskCompletionSource completion)
        {
            insidePhase.Value = true;
            try
            {
                if (oldRuntime is not null) await AgentSessionReloadTask.Join(oldRuntime.DisposeAsync().AsTask()).ConfigureAwait(false);
                completion.TrySetResult();
            }
            catch (Exception error) { completion.TrySetException(error); }
        }

        internal async Task SettleAsync()
        {
            lock (owner.gate) ended = true;
            var failures = new List<Exception>();
            if (invalidation is not null)
                try { await AgentSessionReloadTask.Join(invalidation).ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            if (publication is not null)
                try { await AgentSessionReloadTask.Join(publication).ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            if (invalidated)
            {
                try { await AgentSessionReloadTask.Join(CleanupOldRuntime()).ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
                if (!published || terminal)
                {
                    // There is no rollback after old authority was invalidated. A failed or
                    // unacknowledged publication leaves a permanently unavailable writer.
                    lock (owner.gate) owner.closed = true;
                    // Binding can register candidate resources before it fails. Start every
                    // physical stop before cancellation callbacks can synchronously join them.
                    if (transferred)
                        try { await AgentSessionReloadTask.Join(owner.InitiateOwnedResourceStopsAsync(Candidate)).ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
                    try { owner.InvokeShutdownCancellation(() => nextLifetime.Cancel()); } catch (Exception error) { failures.Add(error); }
                    if (transferred)
                    {
                        try { await AgentSessionReloadTask.Join(owner.RetireOwnedResourcesAsync(Candidate, reservation, joinAll: true)).ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
                        try { await AgentSessionReloadTask.Join(Previous.Session.ReleaseRuntimeAfterBindingFailureAsync()).ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
                    }
                    try { await AgentSessionReloadTask.Join(reservation.RetireWriterAsync()).ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
                }
            }
            if (!transferred && preparedRuntime is not null)
                try { await AgentSessionReloadTask.Join(preparedRuntime.DisposeAsync().AsTask()).ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            if (!transferred) nextLifetime.Dispose();
            if (failures.Count != 0) throw new AggregateException("Reload settlement failed.", failures);
        }
    }
}
