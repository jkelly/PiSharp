using System.Collections.Immutable;
using PiSharp.Agent;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;
using PiSharp.Sessions.Tree;

namespace PiSharp.CodingAgent;

/// <summary>Opaque authority for an acknowledged log, selected context and runtime configuration.
/// Retain the actual object from CaptureTree; an ID, sequence number or deserialized copy is insufficient.</summary>
public sealed class SessionTreeNavigationRevision
{
    internal AgentSessionAttachment Attachment { get; }
    internal PersistentAgentSession Session { get; }
    internal SessionLogStoreSnapshot Log { get; }
    internal SessionContextProjection Context { get; }
    internal AgentConfiguration Configuration { get; }
    internal SessionTreeSnapshot Tree { get; }
    internal long ActivationEpoch { get; }
    internal SessionTreeNavigationRevision(AgentSessionAttachment attachment, PersistentAgentSession session, SessionLogStoreSnapshot log,
        SessionContextProjection context, AgentConfiguration configuration, SessionTreeSnapshot tree, long activationEpoch)
        => (Attachment, Session, Log, Context, Configuration, Tree, ActivationEpoch) = (attachment, session, log, context, configuration, tree, activationEpoch);
}

/// <summary>One immutable tree bound to the actual attachment and selected-state revision.</summary>
public sealed class SessionTreeNavigationView
{
    public AgentSessionAttachment Attachment { get; }
    public SessionTreeNavigationRevision Revision { get; }
    public SessionTreeSnapshot Tree => Revision.Tree;
    public SessionContextProjection Context => Revision.Context;
    public string SessionId => Revision.Log.Header.Id;
    public string Path => Attachment.Session.Path;
    public string? LeafId => Context.LeafId;
    public long Generation => Attachment.Generation;
    internal SessionTreeNavigationView(AgentSessionAttachment attachment, SessionTreeNavigationRevision revision)
        => (Attachment, Revision) = (attachment, revision);
}

/// <summary>No-summary same-file selection. A null target explicitly selects the empty root.
/// User/custom-message targets resolve to their parent and canonical editor text.</summary>
public sealed record SessionTreeNavigationRequest(string? TargetId, SessionTreeNavigationRevision ExpectedRevision)
{
    public SessionTreeNavigationOptions Options { get; init; } = new();
    public SessionTreeNavigationExecution? Execution { get; init; }
}
public enum SessionTreeNavigationDisposition { Selected, NoOp, Vetoed, Aborted }
public enum SessionTreeNavigationFailure { StaleSelection, UnknownTarget, InvalidRequest }
/// <param name="targetId">The unknown target: agent-session.ts navigateTree() throws "Entry &lt;id&gt; not found".</param>
public sealed class SessionTreeNavigationException(SessionTreeNavigationFailure failure, string? targetId = null)
    : InvalidOperationException(failure switch
    {
        SessionTreeNavigationFailure.StaleSelection => "Session tree selection is stale.",
        SessionTreeNavigationFailure.UnknownTarget => $"Entry {targetId} not found",
        _ => "Session tree navigation request is invalid."
    })
{
    public SessionTreeNavigationFailure Failure { get; } = failure;
}

/// <summary>Pre-effect policy data. No write or selection has been acknowledged.</summary>
public sealed record SessionTreeNavigationPreview(string SessionId, string? TargetId, string? OldLeafId,
    string? NewLeafId, string? CommonAncestorId, ImmutableArray<SessionEntry> AbandonedEntries,
    SessionContextProjection Context, AgentConfiguration Configuration, string? EditorText);

/// <summary>Actual selected context and native Agent state on the unchanged attachment.
/// This is an in-memory selection receipt, never an append/durable checkpoint receipt.</summary>
public sealed record SessionTreeNavigationReceipt(SessionTreeNavigationDisposition Disposition,
    SessionTreeNavigationView View, AgentSnapshot Agent, string? EditorText)
{
    public bool CancellationCallbackFailed { get; init; }
    public SessionTreeCheckpoint? Checkpoint { get; init; }
    public ImmutableArray<SessionBoundaryOriginalEvidence> Originals { get; init; } = [];
    public bool Cancelled => Disposition is SessionTreeNavigationDisposition.Vetoed or SessionTreeNavigationDisposition.Aborted;
    public bool Aborted => Disposition == SessionTreeNavigationDisposition.Aborted;
    public bool NoOp => Disposition == SessionTreeNavigationDisposition.NoOp;
    public string SessionId => View.SessionId;
    public string? LeafId => View.LeafId;
    public SessionContextProjection Context => View.Context;
}
