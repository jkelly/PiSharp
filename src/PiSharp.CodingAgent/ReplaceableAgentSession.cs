using System.Collections.Immutable;
using PiSharp.Sessions.Lifecycle;

namespace PiSharp.CodingAgent;

/// <summary>Identity of one actual host attachment. Returning to the same file creates a new generation.</summary>
public sealed record AgentSessionAttachment(PersistentAgentSession Session, long Generation, CancellationToken LifetimeToken);
public sealed record AgentSessionSwitchRequest(string Path, bool UseLatestLeaf = true, string? SelectedLeafId = null);
public sealed record AgentSessionResumeRequest(string CatalogKey, bool UseLatestLeaf = true, string? SelectedLeafId = null);
public sealed record AgentSessionReplacement(AgentSessionAttachment Previous, AgentSessionAttachment Current)
{
    public string Reason { get; init; } = "switch";
    public string? SelectedText { get; init; }
}
public sealed class AgentSessionReplacementNotificationException(AgentSessionReplacement replacement, Exception inner)
    : Exception("Session replacement committed, but its host notification failed; inspect the current session before retrying.", inner)
{
    public AgentSessionReplacement Replacement { get; } = replacement;
}

/// <summary>Owns staged disk sessions, serialized host writes, attachment authority and physical retirement.</summary>
public sealed partial class ReplaceableAgentSession : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim mutations = new(1, 1);
    private readonly CancellationTokenSource closing = new();
    private readonly AsyncLocal<bool> inTransition = new();
    private readonly AsyncLocal<bool> inDiscovery = new();
    private readonly AsyncLocal<bool> inMutation = new();
    private readonly Dictionary<Task, CancellationTokenSource> discoveryOperations = [];
    private readonly Func<AgentSessionSwitchRequest, CancellationToken, Task<PersistentAgentSession>> open;
    private readonly PersistentSessionLifecycle? creationServices;
    private readonly List<Task> retirements = [];
    private readonly List<CancellationTokenSource> lifetimes = [];
    private readonly List<PersistentAgentSession> sessions = [];
    private CancellationTokenSource? transitionCancellation;
    private AgentSessionAttachment current;
    private Task? disposal;
    private bool closed, cancellationCallbackFailed;

    public ReplaceableAgentSession(PersistentAgentSession initial,
        Func<AgentSessionSwitchRequest, CancellationToken, Task<PersistentAgentSession>> openReplacement)
        : this(initial, openReplacement, null) { }
    public ReplaceableAgentSession(PersistentAgentSession initial,
        Func<AgentSessionSwitchRequest, CancellationToken, Task<PersistentAgentSession>> openReplacement,
        PersistentSessionLifecycle? creationServices)
    {
        ArgumentNullException.ThrowIfNull(initial); ArgumentNullException.ThrowIfNull(openReplacement);
        var state = initial.Snapshot;
        if (state.IsDisposed || state.IsRetired || state.IsAdmittingInput || state.IsConfiguring || state.IsAppendingExtensionEntry || state.IsEditingContext ||
            state.Agent.IsRunning || state.IsProcessingOperation || state.IsCompacting || state.Fault is not null)
            throw new ArgumentException("Replacement owner requires an available idle initial session.", nameof(initial));
        open = openReplacement; this.creationServices = creationServices;
        var lifetime = new CancellationTokenSource();
        try
        {
            using var reservation = initial.ReserveReplacement();
            initial.BindInvocationOwner(1, lifetime.Token);
        }
        catch { lifetime.Dispose(); throw; }
        lifetimes.Add(lifetime);
        current = new(initial, 1, lifetime.Token);
        sessions.Add(initial);
    }

    public AgentSessionAttachment Current { get { lock (gate) return current; } }
    public bool CancellationCallbackFailed { get { lock (gate) return cancellationCallbackFailed; } }
    public const int MaximumAttachments = 128;
    public bool CanCreateSessions => creationServices is not null;
    public bool CanDiscoverSessions => creationServices?.Catalog is not null;
    /// <summary>One lifecycle service supplies existing opens, catalog resume and durable creation.
    /// The fallback model is captured from the actual current attachment at each admitted open.</summary>
    public static ReplaceableAgentSession WithLifecycle(PersistentAgentSession initial, PersistentSessionLifecycle services)
    {
        ArgumentNullException.ThrowIfNull(initial); ArgumentNullException.ThrowIfNull(services);
        ReplaceableAgentSession? result = null;
        result = new(initial, (request, token) => services.OpenAsync(request, result!.Current.Session.Snapshot.Agent.Model, token), services);
        return result;
    }
    public Task<SessionCatalogPage> ListSessionsAsync(AgentSessionAttachment expected, SessionCatalogQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return ReadCatalogAsync(expected, (catalog, token) => catalog.ListAsync(query, token), cancellationToken);
    }
    private Task<T> ReadCatalogAsync<T>(AgentSessionAttachment expected, Func<SessionCatalog, CancellationToken, Task<T>> read,
        CancellationToken cancellationToken)
    {
        if (inDiscovery.Value) throw new InvalidOperationException("A catalog reader cannot recursively await its own host operations.");
        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource linked;
        SessionCatalog catalog;
        lock (gate)
        {
            ValidateAttachment(expected);
            catalog = creationServices?.Catalog ?? throw new InvalidOperationException("Session discovery is unavailable from this host.");
            if (discoveryOperations.Count >= 128) throw new InvalidOperationException("Session discovery operation limit reached.");
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closing.Token, expected.LifetimeToken);
            try { linked.Token.ThrowIfCancellationRequested(); }
            catch { linked.Dispose(); throw; }
            discoveryOperations.Add(completion.Task, linked);
        }
        _ = CompleteCatalogReadAsync(expected, catalog, read, linked, completion);
        return completion.Task;
    }
    private async Task CompleteCatalogReadAsync<T>(AgentSessionAttachment expected, SessionCatalog catalog,
        Func<SessionCatalog, CancellationToken, Task<T>> read, CancellationTokenSource linked, TaskCompletionSource<T> completion)
    {
        var previous = inDiscovery.Value; inDiscovery.Value = true;
        T result = default!; Exception? failure = null;
        try
        {
            result = await read(catalog, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested(); ValidateAttachment(expected);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            // The catalog read has already joined its stream/enumerator close before completing below.
            lock (gate) discoveryOperations.Remove(completion.Task);
            linked.Dispose(); inDiscovery.Value = previous;
        }
        if (failure is OperationCanceledException cancelled) completion.TrySetCanceled(cancelled.CancellationToken);
        else if (failure is not null) completion.TrySetException(failure);
        else completion.TrySetResult(result);
    }
    public async Task<AgentSessionReplacement?> ResumeAsync(AgentSessionAttachment expected, AgentSessionResumeRequest request,
        Func<PersistentAgentSession, CancellationToken, ValueTask>? beforeAttach = null, CancellationToken cancellationToken = default,
        Func<AgentSessionAttachment, PersistentAgentSession, CancellationToken, ValueTask<bool>>? beforeResume = null,
        Func<AgentSessionReplacement, ValueTask>? afterResume = null)
    {
        ArgumentNullException.ThrowIfNull(request); RejectTransitionReentrancy(); ValidateAttachment(expected);
        if (request.UseLatestLeaf && request.SelectedLeafId is not null) throw new ArgumentException("Resume branch selection is invalid.", nameof(request));
        var catalog = creationServices?.Catalog ?? throw new InvalidOperationException("Session discovery is unavailable from this host.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closing.Token, expected.LifetimeToken);
        var selected = await ReadCatalogAsync(expected, (value, token) => value.FindAsync(request.CatalogKey, token), linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested(); ValidateAttachment(expected);
        var switching = new AgentSessionSwitchRequest(selected.Path, request.UseLatestLeaf, request.SelectedLeafId);
        ValidateSwitchRequest(expected, switching);
        return await ReplaceCoreAsync(expected, switching, null, async (previous, target, token) =>
        {
            catalog.ValidateOpenedHeader(selected, target.Snapshot.Log.Header);
            if (beforeAttach is not null) await beforeAttach(target, token).ConfigureAwait(false);
            var veto = beforeResume ?? BeforeReplacement;
            return veto is null || await veto(previous, target, token).ConfigureAwait(false);
        }, afterResume, null, linked.Token, "resume").ConfigureAwait(false);
    }
    public Func<AgentSessionAttachment, AgentSessionCreationRequest, CancellationToken, ValueTask<bool>>? BeforeCreation { get; set; }
    public Func<AgentSessionAttachment, PersistentAgentSession, CancellationToken, ValueTask<bool>>? BeforeReplacement { get; set; }
    public Func<AgentSessionReplacement, ValueTask>? AfterReplacement { get; set; }
    public Func<AgentSessionReplacement, ValueTask>? AttachmentChanged { get; set; }
    /// <summary>Host policy for every actual reserved target, including contexts delivered by lifecycle
    /// notifications. A failure precedes physical source retirement and publication.</summary>
    public Func<AgentSessionAttachment, PersistentAgentSession, CancellationToken, ValueTask>? ValidateTargetAttachment { get; set; }

    public void ValidateAttachment(AgentSessionAttachment attachment)
    {
        lock (gate)
        {
            if (closed) throw new ObjectDisposedException(nameof(ReplaceableAgentSession));
            if (!ReferenceEquals(current, attachment)) throw new InvalidOperationException("Session context is stale.");
        }
    }

    public async Task<SessionExtensionEntryReceipt> AppendExtensionEntryAsync(AgentSessionAttachment attachment,
        SessionExtensionEntryDraft draft, CancellationToken token = default)
    {
        RejectTransitionReentrancy(); ValidateAttachment(attachment);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, closing.Token, attachment.LifetimeToken);
        await mutations.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ValidateAttachment(attachment);
            return await attachment.Session.AppendExtensionEntryAsync(attachment.Session.Snapshot.Log.Header.Id,
                draft, linked.Token).ConfigureAwait(false);
        }
        finally { mutations.Release(); }
    }

    public async Task<SessionContextEditReceipt> AppendContextEditAsync(AgentSessionAttachment attachment,
        PiSharp.Sessions.Context.SessionContextEditDraft draft, CancellationToken token = default,
        Func<SessionContextEditPreview, CancellationToken, ValueTask>? preflight = null)
    {
        RejectTransitionReentrancy(); ValidateAttachment(attachment);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, closing.Token, attachment.LifetimeToken);
        await mutations.WaitAsync(linked.Token).ConfigureAwait(false);
        var priorMutation = inMutation.Value; inMutation.Value = true;
        try
        {
            ValidateAttachment(attachment);
            return await attachment.Session.AppendContextEditAsync(attachment.Session.Snapshot.Log.Header.Id,
                draft, linked.Token, preflight).ConfigureAwait(false);
        }
        finally { inMutation.Value = priorMutation; mutations.Release(); }
    }

    public async Task<SessionSummaryCheckpointReceipt?> CompactAsync(AgentSessionAttachment attachment,
        SessionCompactionRequest request, ISessionSummaryGenerator generator, CancellationToken token = default,
        Func<SessionSummaryCheckpointPreview, CancellationToken, ValueTask>? preflight = null)
    {
        RejectTransitionReentrancy(); ValidateAttachment(attachment);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, closing.Token, attachment.LifetimeToken);
        await mutations.WaitAsync(linked.Token).ConfigureAwait(false);
        var priorMutation = inMutation.Value; inMutation.Value = true;
        try { ValidateAttachment(attachment); return await attachment.Session.CompactAsync(attachment.Session.Snapshot.Log.Header.Id,
            request, generator, linked.Token, preflight).ConfigureAwait(false); }
        finally { inMutation.Value = priorMutation; mutations.Release(); }
    }
    public async Task ConfigureAutomaticCompactionAsync(AgentSessionAttachment attachment, ISessionSummaryGenerator? generator,
        SessionCompactionRequest request, CancellationToken token = default, double? recoveryDesiredMaxOutput = null)
    {
        ArgumentNullException.ThrowIfNull(request); RejectTransitionReentrancy(); ValidateAttachment(attachment);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, closing.Token, attachment.LifetimeToken);
        await mutations.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ValidateAttachment(attachment); linked.Token.ThrowIfCancellationRequested();
            attachment.Session.ConfigureAutomaticCompaction(generator, request.Settings, request.ContextWindow, request.SummaryOptions,
                recoveryDesiredMaxOutput);
        }
        finally { mutations.Release(); }
    }

    public async Task<SessionSummaryCheckpointReceipt> SummarizeBranchAsync(AgentSessionAttachment attachment,
        SessionBranchSummaryRequest request, ISessionSummaryGenerator generator, CancellationToken token = default,
        Func<SessionSummaryCheckpointPreview, CancellationToken, ValueTask>? preflight = null)
    {
        RejectTransitionReentrancy(); ValidateAttachment(attachment);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, closing.Token, attachment.LifetimeToken);
        await mutations.WaitAsync(linked.Token).ConfigureAwait(false);
        var priorMutation = inMutation.Value; inMutation.Value = true;
        try { ValidateAttachment(attachment); return await attachment.Session.SummarizeBranchAsync(attachment.Session.Snapshot.Log.Header.Id,
            request, generator, linked.Token, preflight).ConfigureAwait(false); }
        finally { inMutation.Value = priorMutation; mutations.Release(); }
    }

    /// <summary>Staging/veto/cancellation leaves the previous writer usable. After writer retirement admission,
    /// caller cancellation is shielded through publication; lifecycle notification failures retain the committed attachment.</summary>
    public Task<AgentSessionReplacement?> SwitchAsync(AgentSessionAttachment expected, AgentSessionSwitchRequest request,
        Func<AgentSessionAttachment, PersistentAgentSession, CancellationToken, ValueTask<bool>>? beforeSwitch = null,
        Func<AgentSessionReplacement, ValueTask>? afterSwitch = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); RejectTransitionReentrancy(); ValidateAttachment(expected);
        ValidateSwitchRequest(expected, request);
        return ReplaceCoreAsync(expected, request, null, beforeSwitch, afterSwitch, null, cancellationToken);
    }
    private static void ValidateSwitchRequest(AgentSessionAttachment expected, AgentSessionSwitchRequest request)
    {
        if (!System.IO.Path.IsPathFullyQualified(request.Path) || string.IsNullOrWhiteSpace(request.Path) ||
            request.UseLatestLeaf && request.SelectedLeafId is not null)
            throw new ArgumentException("Session switch requires an absolute path and an explicit valid branch selection.", nameof(request));
        if (string.Equals(System.IO.Path.GetFullPath(request.Path), System.IO.Path.GetFullPath(expected.Session.Path),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("The requested session is already attached.");
    }

    /// <summary>Creates and attaches a fresh durable sibling, with source-aware fork/clone and pre-effect veto.
    /// Known unattached files are closed/deleted on precommit rollback; uncertain publication remains explicit.</summary>
    public Task<AgentSessionReplacement?> CreateAsync(AgentSessionAttachment expected, AgentSessionCreationRequest request,
        CancellationToken cancellationToken = default)
        => CreateAsync(expected, request, null, cancellationToken);

    /// <summary>Host preflight validates the actual staged file/context/output budget while target and source
    /// are reserved. Throwing here rolls back the known unattached file and leaves the source usable.</summary>
    public Task<AgentSessionReplacement?> CreateAsync(AgentSessionAttachment expected, AgentSessionCreationRequest request,
        Func<PersistentAgentSession, string?, CancellationToken, ValueTask>? beforeAttach,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); RejectTransitionReentrancy(); ValidateAttachment(expected);
        if (creationServices is null) throw new InvalidOperationException("Session creation services are unavailable from this host.");
        if (!Enum.IsDefined(request.Kind) || request.Kind == AgentSessionCreationKind.New && request.EntryId is not null ||
            request.Kind != AgentSessionCreationKind.New && request.ParentSession is not null ||
            request.Kind == AgentSessionCreationKind.Clone && request.EntryId is not null ||
            request.Kind is AgentSessionCreationKind.ForkBefore or AgentSessionCreationKind.ForkAt && string.IsNullOrEmpty(request.EntryId) ||
            request.ParentSession is { } parent && (string.IsNullOrWhiteSpace(parent) || parent.Length > 4096 || !ValidUnicode(parent)))
            throw new ArgumentException("Session creation request is invalid.", nameof(request));
        return ReplaceCoreAsync(expected, null, request, null, null, beforeAttach, cancellationToken);
    }

    private async Task<AgentSessionReplacement?> ReplaceCoreAsync(AgentSessionAttachment expected, AgentSessionSwitchRequest? request,
        AgentSessionCreationRequest? creation,
        Func<AgentSessionAttachment, PersistentAgentSession, CancellationToken, ValueTask<bool>>? beforeSwitch,
        Func<AgentSessionReplacement, ValueTask>? afterSwitch,
        Func<PersistentAgentSession, string?, CancellationToken, ValueTask>? beforeAttach, CancellationToken cancellationToken,
        string replacementReason = "switch")
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closing.Token, expected.LifetimeToken);
        await mutations.WaitAsync(linked.Token).ConfigureAwait(false);
        PersistentAgentSession? staged = null;
        CancellationTokenSource? stagedLifetime = null;
        PersistentSessionLifecycle.PreparedCreation? created = null;
        var priorTransition = inTransition.Value; inTransition.Value = true;
        try
        {
            ValidateAttachment(expected);
            lock (gate) transitionCancellation = linked;
            lock (gate) if (lifetimes.Count >= MaximumAttachments) throw new InvalidOperationException("Session attachment limit reached; restart the host.");
            using var reservation = expected.Session.ReserveReplacement();
            if (creation is not null)
            {
                if (BeforeCreation is { } veto && !await veto(expected, creation, linked.Token).ConfigureAwait(false)) return null;
                linked.Token.ThrowIfCancellationRequested();
                created = await creationServices!.PrepareAsync(expected, creation, linked.Token).ConfigureAwait(false);
                staged = created.Session;
            }
            else staged = await open(request!, linked.Token).ConfigureAwait(false);
            if (ReferenceEquals(staged, expected.Session)) { staged = null; throw new InvalidOperationException("Replacement factory returned the current coordinator."); }
            var state = staged.Snapshot;
            if (state.IsDisposed || state.IsRetired || state.IsAdmittingInput || state.IsConfiguring || state.IsAppendingExtensionEntry || state.IsEditingContext || state.IsCompacting || state.IsProcessingOperation || state.Agent.IsRunning || state.Fault is not null ||
                request is not null && !string.Equals(System.IO.Path.GetFullPath(staged.Path), System.IO.Path.GetFullPath(request.Path),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                creationServices?.RebindsWorkingDirectory != true && !string.Equals(System.IO.Path.GetFullPath(staged.WorkingDirectory), System.IO.Path.GetFullPath(expected.Session.WorkingDirectory),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Replacement factory did not stage an available target in this workspace.");
            // A factory or preflight callback may retain the actual target coordinator. Its idle snapshot is
            // only an observation; reserve availability through publication so those handles cannot start
            // a checkpoint, input, configuration, queue mutation or disposal in the intervening interval.
            using var targetReservation = staged.ReserveReplacement();
            var preflight = creation is null ? beforeSwitch ?? BeforeReplacement : null;
            if (preflight is not null && !await preflight(expected, staged, linked.Token).ConfigureAwait(false)) return null;
            if (beforeAttach is not null) await beforeAttach(staged, created?.SelectedText, linked.Token).ConfigureAwait(false);
            if (ValidateTargetAttachment is { } validateTarget) await validateTarget(expected, staged, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            stagedLifetime = new CancellationTokenSource();
            staged.BindInvocationOwner(checked(expected.Generation + 1), stagedLifetime.Token);
            // The owner serializes all its writes and the coordinator reservation prevents direct mutations.
            await reservation.RetireWriterAsync().ConfigureAwait(false);
            // Old execution is idle under its reservation; retire its capability before publication.
            try { expected.Session.RetireInvocationOwner(); } catch (Exception) { lock (gate) cancellationCallbackFailed = true; }
            AgentSessionAttachment attached; CancellationTokenSource previousLifetime, attachedLifetime; bool cancelAttached;
            lock (gate)
            {
                previousLifetime = lifetimes[^1];
                var lifetime = stagedLifetime!; stagedLifetime = null; lifetimes.Add(lifetime);
                attachedLifetime = lifetime; cancelAttached = closed;
                attached = new(staged, checked(expected.Generation + 1), lifetime.Token);
                current = attached; sessions.Add(staged); staged = null;
            }
            // The new attachment is now committed. Release its staging reservation before any host/lifetime
            // callback receives the fresh attachment; lifecycle handlers can use its actual durable writer.
            targetReservation.Dispose();
            created?.CommitAttachment();
            // Cancel outside the state lock: trusted registrations may execute arbitrary callbacks.
            try { previousLifetime.Cancel(); } catch (Exception) { lock (gate) cancellationCallbackFailed = true; }
            if (cancelAttached)
                try { attachedLifetime.Cancel(); } catch (Exception) { lock (gate) cancellationCallbackFailed = true; }
            Task retirement;
            using (ExecutionContext.SuppressFlow()) retirement = Task.Run(async () =>
            {
                await expected.Session.WaitForIdleAsync().ConfigureAwait(false);
                await expected.Session.DisposeAsync().ConfigureAwait(false);
            });
            lock (gate) retirements.Add(retirement);
            var result = new AgentSessionReplacement(expected, attached)
            {
                Reason = creation?.Kind == AgentSessionCreationKind.New ? "new" : creation is not null ? "fork" : replacementReason,
                SelectedText = created?.SelectedText
            };
            try
            {
                if (AttachmentChanged is { } changed) await changed(result).ConfigureAwait(false);
                var notification = afterSwitch ?? AfterReplacement;
                if (notification is not null) await notification(result).ConfigureAwait(false);
            }
            catch (Exception error) { throw new AgentSessionReplacementNotificationException(result, error); }
            return result;
        }
        finally
        {
            try
            {
                if (staged is not null) await staged.DisposeAsync().ConfigureAwait(false);
                if (created is not null) await created.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                stagedLifetime?.Dispose();
                lock (gate) if (ReferenceEquals(transitionCancellation, linked)) transitionCancellation = null;
                inTransition.Value = priorTransition; mutations.Release();
            }
        }
    }

    /// <summary>Cancel staged replacement and cooperative input/run work, including the originating retired callback.</summary>
    public void Abort()
    {
        PersistentAgentSession[] attached; CancellationTokenSource? transition; CancellationTokenSource[] discovery;
        lock (gate) { attached = sessions.ToArray(); transition = transitionCancellation; discovery = discoveryOperations.Values.ToArray(); }
        if (transition is not null)
            try { transition.Cancel(); }
            catch (ObjectDisposedException) { /* The completed transition already released its cancellation source. */ }
            catch (Exception) { lock (gate) cancellationCallbackFailed = true; }
        foreach (var cancellation in discovery)
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { /* Completed read has already joined cleanup. */ }
            catch (Exception) { lock (gate) cancellationCallbackFailed = true; }
        foreach (var session in attached) session.Abort();
    }

    private void RejectTransitionReentrancy()
    {
        if (inShutdown.Value || Volatile.Read(ref shutdownCancellationThread) == Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Shutdown callbacks cannot await their own host settlement or disposal.");
        if (inTransition.Value) throw new InvalidOperationException("A replacement lifecycle callback cannot await the transition it is running in.");
        if (inDiscovery.Value) throw new InvalidOperationException("A catalog reader cannot await its own host cleanup or replacement.");
        if (inMutation.Value) throw new InvalidOperationException("A session mutation preflight cannot await its own host mutation or cleanup.");
    }

    private static bool ValidUnicode(string text)
    {
        for (var index = 0; index < text.Length; index++)
            if (char.IsSurrogate(text[index]) && (!char.IsHighSurrogate(text[index]) || ++index >= text.Length || !char.IsLowSurrogate(text[index]))) return false;
        return true;
    }

    public ValueTask DisposeAsync()
    {
        RejectTransitionReentrancy();
        PersistentAgentSession[] attached;
        lock (gate) attached = sessions.ToArray();
        foreach (var session in attached) _ = session.WaitForIdleAsync(); // Reject self-disposal even after A was replaced by B.
        TaskCompletionSource? completion = null;
        lock (gate)
        {
            if (disposal is null)
            { closed = true; completion = new(TaskCreationOptions.RunContinuationsAsynchronously); disposal = completion.Task; }
        }
        if (completion is not null) _ = CloseAsync(completion);
        return new(disposal);
    }
    private async Task CloseAsync(TaskCompletionSource completion)
    {
        var failures = new List<Exception>();
        try { await StopAdmissionAndJoinAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        var priorShutdown = inShutdown.Value; inShutdown.Value = true;
        try
        {
            PersistentAgentSession[] owned; CancellationTokenSource[] attachedLifetimes;
            lock (gate) { owned = sessions.ToArray(); attachedLifetimes = lifetimes.ToArray(); }
            foreach (var session in owned)
                try { await session.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            try { closing.Dispose(); } catch (Exception error) { failures.Add(error); }
            foreach (var lifetime in attachedLifetimes)
                try { lifetime.Dispose(); } catch (Exception error) { failures.Add(error); }
        }
        catch (Exception error) { failures.Add(error); }
        finally { inShutdown.Value = priorShutdown; }
        if (failures.Count == 0) completion.TrySetResult(); else completion.TrySetException(new AggregateException(failures));
    }
}
