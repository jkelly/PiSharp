namespace PiSharp.CodingAgent;

public sealed partial class ReplaceableAgentSession
{
    public Task<AgentSessionReplacement?> CreateWithSetupAsync(AgentSessionAttachment expected,
        AgentSessionCreationRequest request, SessionCreationSetupCallback setup, CancellationToken token = default,
        Func<PersistentAgentSession, CancellationToken, ValueTask>? validateAfterSetup = null)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(setup);
        RejectTransitionReentrancy(); ValidateAttachment(expected);
        if (creationServices is null) throw new InvalidOperationException("Session creation services are unavailable.");
        if (request.Kind != AgentSessionCreationKind.New || request.EntryId is not null ||
            request.ParentSession is { } parent && (string.IsNullOrWhiteSpace(parent) || parent.Length > 4096 || !ValidUnicode(parent)))
            throw new ArgumentException("Setup is admitted only for a new session.");
        if (setup.GetInvocationList().Length != 1) throw new ArgumentException("One setup callback required.");
        if (validateAfterSetup?.GetInvocationList().Length > 1) throw new ArgumentException("One post-setup validator required.");
        return ReplaceCoreAsync(expected, null, request, null, null, null, token, setup: setup, validateAfterSetup: validateAfterSetup);
    }
}
