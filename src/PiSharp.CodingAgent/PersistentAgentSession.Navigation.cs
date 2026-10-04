using System.Text;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Sessions.Compaction;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Tree;
using NativeAgent = PiSharp.Agent.Agent;

namespace PiSharp.CodingAgent;

public sealed partial class PersistentAgentSession
{
    internal SessionTreeNavigationRevision CaptureTreeRevision(AgentSessionAttachment attachment, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowAvailable();
            return new(attachment, this, _acknowledgedLog, _context, _configuration,
                new SessionTreeQueries().Build(_acknowledgedLog.Entries, token), _activationEpoch);
        }
    }

    private void ValidateTreeRevision(SessionTreeNavigationRevision revision)
    {
        if (!ReferenceEquals(revision.Session, this) || !ReferenceEquals(revision.Log, _acknowledgedLog) ||
            !ReferenceEquals(revision.Context, _context) || !ReferenceEquals(revision.Configuration, _configuration) || revision.ActivationEpoch != _activationEpoch)
            throw new SessionTreeNavigationException(SessionTreeNavigationFailure.StaleSelection);
    }

    internal sealed record TreeSelection(SessionTreeNavigationDisposition Disposition,
        SessionTreeNavigationRevision Revision, AgentSnapshot Agent, string? EditorText);

    internal Task<TreeSelection> NavigateTreeAsync(SessionTreeNavigationRequest request, CancellationToken token,
        Action<Action> publish, Func<SessionTreeNavigationPreview, CancellationToken, ValueTask<bool>>? beforeTree)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExpectedRevision is null || request.TargetId is { Length: 0 })
            throw new SessionTreeNavigationException(SessionTreeNavigationFailure.InvalidRequest);
        // Validate and reserve in the same state boundary. ReserveSummary already owns idle admission,
        // Abort and the original cancellation-user/idle joins; IsCompacting covers navigation as in Pi.
        (TaskCompletionSource Idle, ContextEditCancellation Abort, CancellationToken InputAbort) reservation;
        lock (_gate)
        {
            ThrowAvailable(); ValidateTreeRevision(request.ExpectedRevision);
            reservation = ReserveSummary(request.ExpectedRevision.Log.Header.Id, token);
        }
        return NavigateTreeCoreAsync(request, token, reservation.Idle, reservation.Abort,
            reservation.InputAbort, publish, beforeTree);
    }

    private async Task<TreeSelection> NavigateTreeCoreAsync(SessionTreeNavigationRequest request,
        CancellationToken token, TaskCompletionSource idle, ContextEditCancellation abort, CancellationToken inputAbort,
        Action<Action> publish, Func<SessionTreeNavigationPreview, CancellationToken, ValueTask<bool>>? beforeTree)
    {
        var previousCallback = _configurationCallback.Value; _configurationCallback.Value = idle;
        var revision = request.ExpectedRevision;
        CancellationToken work = default;
        TreeSelection Unchanged(SessionTreeNavigationDisposition disposition)
        {
            lock (_gate) return new(disposition, revision, _agent.Snapshot, null);
        }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _closing.Token, inputAbort, abort.Abort.Token);
            work = linked.Token; work.ThrowIfCancellationRequested();
            // Source short-circuits the selected ID before user/custom-message parent resolution or veto.
            if (request.TargetId == revision.Context.LeafId)
            {
                TreeSelection? noOp = null;
                publish(() => { lock (_gate) { ThrowAvailable(); ValidateTreeRevision(revision); work.ThrowIfCancellationRequested();
                    ValidateTreeQueuePublication(idle);
                    noOp = Unchanged(SessionTreeNavigationDisposition.NoOp); } });
                return noOp!;
            }
            SessionEntry? target = null;
            if (request.TargetId is { } targetId && !revision.Log.ById.TryGetValue(targetId, out target))
                throw new SessionTreeNavigationException(SessionTreeNavigationFailure.UnknownTarget);
            var newLeaf = request.TargetId; string? editorText = null;
            if (target?.Kind == SessionEntryKind.Message &&
                target.WireBody.Value.GetProperty("message").GetProperty("role").GetString() == "user")
            {
                newLeaf = target.ParentId;
                editorText = TreeContentText(target.WireBody.Value.GetProperty("message").GetProperty("content"), work);
            }
            else if (target?.Kind == SessionEntryKind.CustomMessage)
            {
                newLeaf = target.ParentId;
                editorText = TreeContentText(target.WireBody.Value.GetProperty("content"), work);
            }
            var prospective = _projector.Project(revision.Log.Entries, newLeaf, work);
            var configuration = _registry is { } registry
                ? registry.Resolve(prospective, revision.Configuration.Model, work).Configuration : revision.Configuration;
            ValidateRuntimeContext(prospective, configuration);
            var messages = SessionContextProjector.AgentMessages(prospective);
            await using (var probe = new NativeAgent(configuration, _clock, new NoopSink(), _agentOptions))
                probe.ConfigureAndReplaceMessages(configuration, messages);
            var abandoned = request.TargetId is null
                ? new SessionBranchSummaryCollection(revision.Context.Ancestry, null)
                : new SessionBranchSummaryPlanner().Collect(revision.Log.Entries, revision.Context.LeafId, request.TargetId, work);
            var preview = new SessionTreeNavigationPreview(revision.Log.Header.Id, request.TargetId, revision.Context.LeafId,
                newLeaf, abandoned.CommonAncestorId, abandoned.Entries, prospective, configuration, editorText);
            if (beforeTree is not null && !await beforeTree(preview, work).ConfigureAwait(false))
            {
                work.ThrowIfCancellationRequested();
                return Unchanged(SessionTreeNavigationDisposition.Vetoed);
            }
            TreeSelection? selected = null;
            // The host validates attachment under its publication gate. No callback/output runs here.
            // Cancellation before this boundary preserves both states; after it the receipt is retained.
            publish(() =>
            {
                lock (_gate)
                {
                    ThrowAvailable(); ValidateTreeRevision(revision); work.ThrowIfCancellationRequested();
                    ValidateTreeQueuePublication(idle);
                    var restoreActivation = PrepareActivationRestoration(configuration);
                    _agent.ConfigureAndReplaceMessages(RecoveryConfiguration(configuration), messages);
                    _configuration = configuration; _context = prospective;
                    restoreActivation();
                    selected = new(SessionTreeNavigationDisposition.Selected,
                        new(revision.Attachment, this, revision.Log, prospective, configuration, revision.Tree, _activationEpoch), _agent.Snapshot, editorText);
                }
            });
            return selected!;
        }
        catch (OperationCanceledException error) when (abort.Abort.IsCancellationRequested &&
            !token.IsCancellationRequested && !_closing.IsCancellationRequested && !inputAbort.IsCancellationRequested &&
            error.CancellationToken == work)
        {
            // A foreign OCE must never become a successful abort receipt. Only the linked work token
            // can identify our Abort. Caller/lifetime/closing cancellation remains exceptional.
            return Unchanged(SessionTreeNavigationDisposition.Aborted);
        }
        finally
        {
            Task cancelIdle;
            lock (_gate)
            {
                if (ReferenceEquals(_contextEditCancellation, abort)) _contextEditCancellation = null;
                cancelIdle = abort.CancelUsers == 0 ? Task.CompletedTask : abort.CancelIdle!.Task;
            }
            await cancelIdle.ConfigureAwait(false); abort.Abort.Dispose();
            lock (_gate) if (ReferenceEquals(_active, idle)) { _active = null; _compacting = false; }
            idle.TrySetResult(); _configurationCallback.Value = previousCallback;
        }
    }

    private void ValidateTreeQueuePublication(TaskCompletionSource idle)
    {
        var snapshot = _agent.Snapshot;
        if (!ReferenceEquals(_active, idle) || !snapshot.PendingInputs.IsEmpty || snapshot.SteeringCount != 0 || snapshot.FollowUpCount != 0)
            throw new InvalidOperationException("Session tree navigation requires idle context and empty pending queues.");
    }

    private static string TreeContentText(JsonElement content, CancellationToken token)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString()!;
        var text = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            if (block.GetProperty("type").GetString() == "text") text.Append(block.GetProperty("text").GetString());
        }
        return text.ToString();
    }
}
