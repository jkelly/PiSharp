using System.Collections.Immutable;

namespace PiSharp.CodingAgent;

public sealed record PreparedSessionToolCatalog(SessionRuntimeRegistry Registry, ImmutableArray<string> ActiveNames,
    Action CommitPreparedRegistry);

public sealed partial class ReplaceableAgentSession
{
    /// <summary>Serializes catalog preparation across every server attached to this actual owner.
    /// A run in its provider phase takes the catalog at once (<see cref="PersistentAgentSession.TryPublishToolCatalogDuringRunAsync"/>);
    /// otherwise this joins admitted session work before capturing its current registry; no canceled-await wrapper
    /// can leave that run or the preparation behind. The caller cannot recursively mutate this owner.</summary>
    public async Task<SessionToolCatalogReceipt> PrepareAndPublishToolCatalogAsync(AgentSessionAttachment attachment,
        Func<SessionRuntimeRegistry, CancellationToken, ValueTask<PreparedSessionToolCatalog>> prepare,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(prepare); RejectTransitionReentrancy(); ValidateAttachment(attachment);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, closing.Token, attachment.LifetimeToken);
        await mutations.WaitAsync(linked.Token).ConfigureAwait(false);
        var prior = inMutation.Value; inMutation.Value = true;
        try
        {
            ValidateAttachment(attachment);
            // Source _refreshToolRegistry: a catalog change during a run reaches the run at its next request boundary.
            if (await attachment.Session.TryPublishToolCatalogDuringRunAsync((expected, _, cancellation) => prepare(expected, cancellation), linked.Token)
                    .ConfigureAwait(false) is { } during) return during;
            ValidateAttachment(attachment);
            await attachment.Session.WaitForIdleAsync().ConfigureAwait(false);
            ValidateAttachment(attachment); linked.Token.ThrowIfCancellationRequested();
            var expected = attachment.Session.CaptureToolCatalogRegistry();
            var prepared = await prepare(expected, linked.Token).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(prepared);
            return await attachment.Session.PublishToolCatalogAsync(expected, prepared.Registry, prepared.ActiveNames,
                prepared.CommitPreparedRegistry, linked.Token).ConfigureAwait(false);
        }
        finally { inMutation.Value = prior; mutations.Release(); }
    }

    /// <summary>The attachment's catalog registry, captured between owner mutations. A resource close, reload or switch holds the
    /// session's replacement reservation only inside one, and the session refuses a capture meanwhile ("Session replacement is in
    /// progress"); this joins them instead, so binding a resource is not refused because another one is closing.</summary>
    public async Task<SessionRuntimeRegistry> CaptureToolCatalogRegistryAsync(AgentSessionAttachment attachment, CancellationToken token = default)
    {
        RejectTransitionReentrancy(); ValidateAttachment(attachment);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, closing.Token, attachment.LifetimeToken);
        await mutations.WaitAsync(linked.Token).ConfigureAwait(false);
        try { ValidateAttachment(attachment); return attachment.Session.CaptureToolCatalogRegistry(); }
        finally { mutations.Release(); }
    }

    /// <summary>Runs <paramref name="access"/> on the attachment's session at once, or, when the session refuses it because an owner
    /// operation holds it (a resource close, reload or switch holds its replacement reservation only inside an owner mutation), again
    /// between owner mutations once that operation ended. Access the session admits never waits, so a tool call or an input callback
    /// (which a pending catalog publication may be waiting for) is not held here; within this owner's own operations the access gets
    /// the session's refusal.</summary>
    public async Task AccessSessionAsync(AgentSessionAttachment attachment, Action<PersistentAgentSession> access, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(access); ValidateAttachment(attachment);
        try { access(attachment.Session); return; }
        catch (SessionReplacementInProgressException) when (MayAwaitMutations()) { }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, closing.Token, attachment.LifetimeToken);
        await mutations.WaitAsync(linked.Token).ConfigureAwait(false);
        try { ValidateAttachment(attachment); access(attachment.Session); }
        finally { mutations.Release(); }
    }

    /// <summary>Joins the owner operation that holds the attachment's session, if one does, so input the session refuses meanwhile
    /// ("Session replacement is in progress") is admitted once it ended. Returns at once when none does, when the attachment is no
    /// longer current, and within this owner's own operations.</summary>
    public async Task JoinReservationAsync(AgentSessionAttachment attachment, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        lock (gate) if (closed || !ReferenceEquals(current, attachment)) return;
        if (!attachment.Session.ReservedForReplacement || !MayAwaitMutations()) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, closing.Token, attachment.LifetimeToken);
        await mutations.WaitAsync(linked.Token).ConfigureAwait(false);
        mutations.Release();
    }

    /// <summary>Whether this flow may wait for owner mutations: it runs in none of this owner's operations or callbacks, which would
    /// wait for themselves (<see cref="RejectTransitionReentrancy"/>).</summary>
    private bool MayAwaitMutations()
    {
        try { RejectTransitionReentrancy(); return true; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>The captured attachment remains reserved through durable publication; close and
    /// replacement join this actual mutation rather than a caller-cancelled waiting wrapper.</summary>
    public async Task<SessionToolCatalogReceipt> PublishToolCatalogAsync(AgentSessionAttachment attachment,
        SessionRuntimeRegistry expected, SessionRuntimeRegistry replacement, ImmutableArray<string> activeNames,
        Action publishPreparedRegistry, CancellationToken token = default)
    {
        RejectTransitionReentrancy(); ValidateAttachment(attachment);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, closing.Token, attachment.LifetimeToken);
        await mutations.WaitAsync(linked.Token).ConfigureAwait(false);
        var prior = inMutation.Value; inMutation.Value = true;
        try
        {
            ValidateAttachment(attachment);
            return await attachment.Session.PublishToolCatalogAsync(expected, replacement, activeNames, publishPreparedRegistry, linked.Token)
                .ConfigureAwait(false);
        }
        finally { inMutation.Value = prior; mutations.Release(); }
    }
}
