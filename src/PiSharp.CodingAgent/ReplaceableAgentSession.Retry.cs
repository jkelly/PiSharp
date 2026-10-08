namespace PiSharp.CodingAgent;

public sealed partial class ReplaceableAgentSession
{
    public async Task SetAutoRetryEnabledAsync(AgentSessionAttachment attachment, bool enabled, CancellationToken token = default)
    {
        attachment.Session.RejectRetryOwnedSelfWait(); RejectTransitionReentrancy(); ValidateAttachment(attachment);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, attachment.LifetimeToken, closing.Token);
        await mutations.WaitAsync(linked.Token).ConfigureAwait(false);
        var previous = inMutation.Value; inMutation.Value = true;
        try { ValidateAttachment(attachment); await attachment.Session.SetAutoRetryEnabledAsync(enabled, linked.Token).ConfigureAwait(false); }
        finally { inMutation.Value = previous; mutations.Release(); }
    }
}
