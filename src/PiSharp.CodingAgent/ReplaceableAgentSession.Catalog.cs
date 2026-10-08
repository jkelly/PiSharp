using System.Collections.Immutable;

namespace PiSharp.CodingAgent;

public sealed record PreparedSessionToolCatalog(SessionRuntimeRegistry Registry, ImmutableArray<string> ActiveNames,
    Action CommitPreparedRegistry);

public sealed partial class ReplaceableAgentSession
{
    /// <summary>Serializes catalog preparation across every server attached to this actual owner.
    /// Joins admitted session work before capturing its current registry; no canceled-await wrapper
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
