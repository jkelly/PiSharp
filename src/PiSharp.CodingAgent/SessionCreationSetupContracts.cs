using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

namespace PiSharp.CodingAgent;

public delegate ValueTask SessionCreationSetupCallback(SessionCreationSetupWriter manager, CancellationToken token);
public sealed record SessionSetupAppendReceipt(SessionEntry Entry, SessionLogAppendResult Append, SessionContextProjection Context);
public sealed record SessionSetupEntryDraft(string Type, JsonData Fields);
public sealed class SessionReplacementRollbackException(Exception? primary,
    System.Collections.Immutable.ImmutableArray<SessionBoundaryOriginalEvidence> originals, IEnumerable<Exception> failures)
    : AggregateException("Replacement primary and every actual rollback cleanup original failed.", failures)
{
    public Exception? Primary { get; } = primary;
    public System.Collections.Immutable.ImmutableArray<SessionBoundaryOriginalEvidence> Originals { get; } = originals;
}
