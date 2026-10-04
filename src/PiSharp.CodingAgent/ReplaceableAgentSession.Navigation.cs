namespace PiSharp.CodingAgent;

public sealed partial class ReplaceableAgentSession
{
    /// <summary>Captures actual acknowledged entries and context on the expected attachment.
    /// Keep this view in the host until selection; IDs/generation numbers alone confer no authority.</summary>
    public SessionTreeNavigationView CaptureTree(AgentSessionAttachment expected, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(expected); ValidateAttachment(expected);
        var revision = expected.Session.CaptureTreeRevision(expected, token);
        ValidateAttachment(expected); token.ThrowIfCancellationRequested();
        return new(expected, revision);
    }

    /// <summary>Same-file no-summary navigation. BeforeTree is awaited once outside state monitors;
    /// false vetoes publication. Caller/lifetime cancellation throws; owned Abort returns Aborted.
    /// The returned view/context/editor text are actual state, with no new attachment or checkpoint.</summary>
    public async Task<SessionTreeNavigationReceipt> NavigateTreeAsync(AgentSessionAttachment expected,
        SessionTreeNavigationRequest request, CancellationToken token = default,
        Func<SessionTreeNavigationPreview, CancellationToken, ValueTask<bool>>? beforeTree = null)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(request);
        RejectTransitionReentrancy(); ValidateAttachment(expected);
        if (request.ExpectedRevision is null)
            throw new SessionTreeNavigationException(SessionTreeNavigationFailure.InvalidRequest);
        if (!ReferenceEquals(request.ExpectedRevision.Attachment, expected))
            throw new SessionTreeNavigationException(SessionTreeNavigationFailure.StaleSelection);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, closing.Token, expected.LifetimeToken);
        await mutations.WaitAsync(linked.Token).ConfigureAwait(false);
        var previous = inMutation.Value; inMutation.Value = true;
        try
        {
            ValidateAttachment(expected);
            var selection = await expected.Session.NavigateTreeAsync(request, linked.Token, publish =>
            {
                lock (gate)
                {
                    ValidateAttachment(expected); linked.Token.ThrowIfCancellationRequested(); publish();
                }
            }, beforeTree).ConfigureAwait(false);
            // Publication already succeeded, or the reserved old state was retained. Never replace an
            // acknowledged receipt with a late caller cancellation check or read owner.Current here.
            return new(selection.Disposition, new(expected, selection.Revision), selection.Agent, selection.EditorText)
            { CancellationCallbackFailed = expected.Session.Snapshot.InputCancellationCallbackFailed };
        }
        finally { inMutation.Value = previous; mutations.Release(); }
    }
}
