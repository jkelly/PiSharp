using System.Collections.Immutable;

namespace PiSharp.CodingAgent;

public sealed partial class ReplaceableAgentSession
{
    private readonly List<OwnedResourceLease> ownedResources = [];
    private readonly AsyncLocal<OwnedResourceRetirementTransaction?> resourceTransaction = new();
    private readonly AsyncLocal<bool> inResourcePublication = new();
    private readonly AsyncLocal<AgentSessionAttachment?> resourceRegistrationAttachment = new();
    private readonly AsyncLocal<OwnedResourceLease?> resourceStopCallback = new();
    public const int MaximumOwnedResources = 128;

    public OwnedResourceLease RegisterOwnedResource(AgentSessionAttachment attachment,
        Func<OwnedResourceRetirementTransaction, Task> closeBody, Func<Task>? stopBody = null)
    {
        ArgumentNullException.ThrowIfNull(closeBody);
        if (closeBody.GetInvocationList().Length != 1) throw new ArgumentException("An owned resource requires one joined body.", nameof(closeBody));
        if (stopBody?.GetInvocationList().Length > 1) throw new ArgumentException("An owned resource requires one joined stop.", nameof(stopBody));
        if (!inTransition.Value || !ReferenceEquals(resourceRegistrationAttachment.Value, attachment)) RejectTransitionReentrancy();
        if (resourceTransaction.Value is not null || resourceStopCallback.Value is not null || inShutdown.Value || inMutation.Value || inDiscovery.Value)
            throw new InvalidOperationException("Resource registration is unavailable from this owning callback.");
        ValidateAttachment(attachment);
        lock (gate)
        {
            ValidateAttachment(attachment);
            if (ownedResources.Count >= ownedResourceLimit) throw new InvalidOperationException("Owned resource bound reached.");
            var lease = new OwnedResourceLease(this, attachment, closeBody, stopBody); ownedResources.Add(lease); return lease;
        }
    }

    public sealed class OwnedResourceLease
    {
        internal readonly ReplaceableAgentSession Owner;
        internal readonly AgentSessionAttachment Attachment;
        internal readonly Func<OwnedResourceRetirementTransaction, Task> Body;
        internal readonly Func<Task>? StopBody;
        internal readonly TaskCompletionSource StopInitiated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? StopOriginal;
        internal Task? StopCallbackOriginal;
        internal TaskCompletionSource? Completion;
        internal CancellationTokenSource? AdmissionCancellation;
        internal Task? Admission;
        internal Task? Settlement;
        internal bool AdmissionClaimed;
        internal Exception? AdmissionFailure;
        internal bool BodyStarted;
        internal readonly TaskCompletionSource BodyCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal OwnedResourceLease(ReplaceableAgentSession owner, AgentSessionAttachment attachment,
            Func<OwnedResourceRetirementTransaction, Task> body, Func<Task>? stopBody)
        { Owner = owner; Attachment = attachment; Body = body; StopBody = stopBody; }
        /// <summary>The exact task returned once by the physical stop callback, including success/canceled state.</summary>
        public Task? PhysicalStopOriginal { get { lock (Owner.gate) return StopCallbackOriginal; } }
        public Task CloseAsync()
        {
            Owner.RejectTransitionReentrancy();
            Attachment.Session.RejectOwnedResourceSelfWait();
            lock (Owner.gate)
            {
                if (Completion is not null) return Completion.Task;
                Owner.ValidateAttachment(Attachment);
                Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Owner.BeginOwnedResourceStop(this);
                AdmissionCancellation = CancellationTokenSource.CreateLinkedTokenSource(Owner.closing.Token);
                Admission = Task.Run(() => Owner.CloseResourceNormallyAsync(this));
                Settlement = Owner.CompleteNormalResourceCloseAsync(this, Admission);
                return Completion.Task;
            }
        }
    }

    private async Task CloseResourceNormallyAsync(OwnedResourceLease lease)
    {
        var held = false; var claimed = false; Exception? failure = null;
        try
        {
            await lease.StopInitiated.Task.ConfigureAwait(false);
            await mutations.WaitAsync(lease.AdmissionCancellation!.Token).ConfigureAwait(false); held = true;
            lock (gate) if (closed) return;
            ValidateAttachment(lease.Attachment);
            await lease.Attachment.Session.WaitForIdleAsync().ConfigureAwait(false);
            using var reservation = lease.Attachment.Session.ReserveReplacement();
            claimed = true;
            await ExecuteOwnedResourceAsync(lease, reservation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!held && lease.AdmissionCancellation!.IsCancellationRequested)
        { /* Retirement claims this pending, effect-free body after joining this admission original. */ }
        catch (Exception error)
        {
            failure = error; claimed = true;
            // A failed idle/reservation preflight still owes the already initiated stop and owned
            // cleanup. No durable publication authority is fabricated for that fallback body.
            if (!lease.BodyStarted)
                try { await ExecuteOwnedResourceAsync(lease, null).ConfigureAwait(false); }
                catch (Exception cleanup) { failure = new AggregateException(error, cleanup); }
        }
        finally { if (held) mutations.Release(); }
        lock (gate) { lease.AdmissionClaimed = claimed; lease.AdmissionFailure = failure; }
    }

    private void BeginOwnedResourceStop(OwnedResourceLease lease)
    {
        lock (gate)
        {
            // Automatic retirement publishes the same close receipt before starting effects, so
            // a concurrent explicit close can join it even after host admission has been closed.
            lease.Completion ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            lease.StopOriginal ??= Task.Run(() => StopOwnedResourceAsync(lease));
        }
    }
    private async Task StopOwnedResourceAsync(OwnedResourceLease lease)
    {
        var prior = resourceStopCallback.Value; resourceStopCallback.Value = lease;
        Task? original = null;
        try
        {
            // Invoke outside registry/owner state locks, and signal only after the actual stop callback
            // has returned its original. Its settlement may depend on idle work; initiation must not.
            original = lease.StopBody is null ? Task.CompletedTask :
                lease.StopBody() ?? throw new InvalidOperationException("An owned resource stop must return its actual task.");
            lock (gate) lease.StopCallbackOriginal = original;
            lease.StopInitiated.TrySetResult();
            await original.ConfigureAwait(false);
        }
        catch (Exception error) { throw new OwnedResourceStopException(original, error); }
        finally { lease.StopInitiated.TrySetResult(); resourceStopCallback.Value = prior; }
    }

    /// <summary>Retains the exact original physical stop and complete task fault inventory.
    /// Aggregate inheritance preserves existing leaf/identity comparisons and cleanup fault composition.
    /// A canceled callback task is recorded explicitly; no caller token is available to claim owned cancellation.</summary>
    public sealed class OwnedResourceStopException : AggregateException
    {
        internal OwnedResourceStopException(Task? original, Exception observed)
            : base("An owned resource physical stop failed.", (IEnumerable<Exception>?)original?.Exception?.InnerExceptions ??
                new[] { observed })
        { OriginalTask = original; ObservedException = observed; OriginalTaskException = original?.Exception; }
        public Task? OriginalTask { get; }
        public Exception ObservedException { get; }
        public AggregateException? OriginalTaskException { get; }
    }
    private async Task InitiateOwnedResourceStopsAsync(AgentSessionAttachment? attachment = null)
    {
        OwnedResourceLease[] selected;
        lock (gate) selected = ownedResources.Where(resource => attachment is null || ReferenceEquals(resource.Attachment, attachment)).ToArray();
        foreach (var resource in selected) BeginOwnedResourceStop(resource);
        foreach (var resource in selected) await resource.StopInitiated.Task.ConfigureAwait(false);
    }

    private async Task CompleteNormalResourceCloseAsync(OwnedResourceLease lease, Task admissionOriginal)
    {
        await admissionOriginal.ConfigureAwait(false);
        bool claimed; Exception? failure;
        lock (gate) { claimed = lease.AdmissionClaimed; failure = lease.AdmissionFailure; }
        if (!claimed) return;
        if (failure is not null) { lease.Completion!.TrySetException(failure); return; }
        // A resource closed while its attachment continues (an MCP server removed or rebound during the session) is done:
        // it leaves the owner's set, so it no longer counts toward MaximumOwnedResources and is not retired again.
        lock (gate) ownedResources.Remove(lease);
        lease.Completion!.TrySetResult();
    }

    // Caller holds the actual owner mutation semaphore and exact persistent reservation.
    private async Task RetireOwnedResourcesAsync(AgentSessionAttachment attachment,
        PersistentAgentSession.ReplacementReservation? reservation, bool joinAll = false)
    {
        OwnedResourceLease[] selected;
        lock (gate) selected = ownedResources.Where(lease => ReferenceEquals(lease.Attachment, attachment)).ToArray();
        var faults = new List<Exception>();
        foreach (var lease in selected)
        {
            try { await RetireOneAsync(lease).ConfigureAwait(false); }
            catch (Exception error) { if (!joinAll) throw; faults.Add(error); }
        }
        if (faults.Count != 0) throw new AggregateException(faults);
        async Task RetireOneAsync(OwnedResourceLease lease)
        {
            Task? admission, settlement; CancellationTokenSource? cancellation;
            lock (gate)
            {
                lease.Completion ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                admission = lease.Admission; settlement = lease.Settlement; cancellation = lease.AdmissionCancellation;
            }
            // Only semaphore admission is canceled. No resource body effects run under that token.
            if (cancellation is not null) await cancellation.CancelAsync().ConfigureAwait(false);
            if (admission is not null) await admission.ConfigureAwait(false);
            if (settlement is not null) await settlement.ConfigureAwait(false);
            try { await ExecuteOwnedResourceAsync(lease, reservation).ConfigureAwait(false); lease.Completion!.TrySetResult(); }
            catch (Exception error) { lease.Completion!.TrySetException(error); throw; }
            await lease.Completion!.Task.ConfigureAwait(false);
        }
    }

    private async Task ExecuteOwnedResourceAsync(OwnedResourceLease lease,
        PersistentAgentSession.ReplacementReservation? reservation)
    {
        lock (gate) { if (lease.BodyStarted) return; lease.BodyStarted = true; }
        var tx = new OwnedResourceRetirementTransaction(this, lease.Attachment, reservation) { IsSessionShutdown = inShutdown.Value };
        var prior = resourceTransaction.Value; resourceTransaction.Value = tx;
        var faults = new List<Exception>();
        BeginOwnedResourceStop(lease);
        try { await AgentSessionReloadTask.JoinOwned(lease.StopOriginal!).ConfigureAwait(false); } catch (Exception error) { faults.Add(error); }
        try { await AgentSessionReloadTask.JoinOwned(lease.Body(tx)).ConfigureAwait(false); } catch (Exception error) { faults.Add(error); }
        finally
        {
            Task? publication; lock (gate) { tx.BodyEnded = true; publication = tx.Publication; }
            if (publication is not null)
                try { await AgentSessionReloadTask.JoinOwned(publication).ConfigureAwait(false); } catch (Exception error) { if (!faults.Contains(error)) faults.Add(error); }
            lock (gate) tx.Alive = false;
            resourceTransaction.Value = prior;
        }
        if (faults.Count == 0) lease.BodyCompletion.TrySetResult();
        else if (faults.Count == 1) lease.BodyCompletion.TrySetException(faults[0]);
        else lease.BodyCompletion.TrySetException(new AggregateException(faults));
        // Completion is the stable public original; its failure is joined by both normal/retirement paths.
        await lease.BodyCompletion.Task.ConfigureAwait(false);
    }

    public sealed class OwnedResourceRetirementTransaction
    {
        private readonly ReplaceableAgentSession owner;
        private readonly PersistentAgentSession.ReplacementReservation? reservation;
        internal bool Alive = true, BodyEnded;
        internal Task? Publication;
        public AgentSessionAttachment Attachment { get; }
        /// <summary>Whether the owner retires this resource because it shuts down, rather than for a replacement or an explicit close.</summary>
        public bool IsSessionShutdown { get; internal init; }
        internal OwnedResourceRetirementTransaction(ReplaceableAgentSession owner, AgentSessionAttachment attachment,
            PersistentAgentSession.ReplacementReservation? reservation)
        { this.owner = owner; Attachment = attachment; this.reservation = reservation; }
        public Task<SessionToolCatalogReceipt> PrepareAndPublishCatalogAsync(
            Func<SessionRuntimeRegistry, ImmutableArray<string>, CancellationToken, ValueTask<PreparedSessionToolCatalog>> prepare)
        {
            ArgumentNullException.ThrowIfNull(prepare);
            if (prepare.GetInvocationList().Length != 1) throw new ArgumentException("Retirement requires one joined preparation.", nameof(prepare));
            lock (owner.gate)
            {
                if (!Alive || BodyEnded || !ReferenceEquals(owner.resourceTransaction.Value, this) || owner.inResourcePublication.Value)
                    throw new InvalidOperationException("Resource retirement transaction is stale, foreign or reentrant.");
                if (Publication is not null) throw new InvalidOperationException("Resource retirement admits one catalog publication.");
                var original = Task.Run(() => PublishAsync(prepare)); Publication = original; return original;
            }
        }
        private async Task<SessionToolCatalogReceipt> PublishAsync(
            Func<SessionRuntimeRegistry, ImmutableArray<string>, CancellationToken, ValueTask<PreparedSessionToolCatalog>> prepare)
        {
            var prior = owner.inResourcePublication.Value; owner.inResourcePublication.Value = true;
            try
            {
                if (reservation is null) throw new InvalidOperationException("Faulted shutdown permits resource cleanup but cannot acknowledge catalog publication.");
                var current = reservation.CaptureToolCatalogRegistry();
                var activeNames = reservation.CaptureActiveToolNames();
                var prepared = await AgentSessionReloadTask.JoinOwned(prepare(current, activeNames, CancellationToken.None).AsTask()).ConfigureAwait(false);
                ArgumentNullException.ThrowIfNull(prepared);
                ArgumentNullException.ThrowIfNull(prepared.CommitPreparedRegistry);
                if (prepared.CommitPreparedRegistry.GetInvocationList().Length != 1)
                    throw new ArgumentException("Retirement requires one prepared registry commit.");
                return await reservation.PublishToolCatalogAsync(current, prepared.Registry, prepared.ActiveNames,
                    prepared.CommitPreparedRegistry).ConfigureAwait(false);
            }
            finally { owner.inResourcePublication.Value = prior; }
        }
    }
}
