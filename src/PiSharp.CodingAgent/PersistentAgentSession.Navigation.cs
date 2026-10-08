using System.Collections.Immutable;
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
        SessionTreeNavigationRevision Revision, AgentSnapshot Agent, string? EditorText)
    {
        internal SessionTreeCheckpoint? Checkpoint { get; init; }
        internal SessionBoundaryOriginals? OriginalOwner { get; init; }
        internal System.Collections.Immutable.ImmutableArray<SessionBoundaryOriginalEvidence> Originals => OriginalOwner?.Snapshot() ?? [];
    }

    internal Task<TreeSelection> NavigateTreeAsync(SessionTreeNavigationRequest request, CancellationToken token,
        Action<Action> publish, Func<SessionTreeNavigationPreview, CancellationToken, ValueTask<bool>>? beforeTree)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExpectedRevision is null || request.TargetId is { Length: 0 })
            throw new SessionTreeNavigationException(SessionTreeNavigationFailure.InvalidRequest);
        if (request.Execution?.BeforeTree?.GetInvocationList().Length > 1 || request.Execution?.AfterTree?.GetInvocationList().Length > 1 ||
            request.Execution?.ValidateProspective?.GetInvocationList().Length > 1)
            throw new ArgumentException("Tree callbacks must each return one captured original.");
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
        var originals = new SessionBoundaryOriginals();
        var commitHeld = false; var writeAdmitted = false;
        SessionTreeCheckpoint? checkpoint = null;
        Exception? bodyFailure = null;
        CancellationToken work = default;
        TreeSelection Unchanged(SessionTreeNavigationDisposition disposition)
        {
            lock (_gate) return new(disposition, revision, _agent.Snapshot, null) { OriginalOwner = originals };
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
            // Source navigateTree -> _restoreToolsFromTranscript: the target's loadout is restored by name with the current
            // bindings, and left-out tools stay pending (when allowed). A restored loadout that differs from the record is
            // recorded with the navigation. A target with no system message keeps the current tools (`if (!current) return`):
            // nothing is written; they stay the logical selection and are recorded at the next request, as the source does.
            ImmutableArray<string> pendingTools = []; PiSharp.Contracts.TranscriptEntry? restoredRecord = null;
            PendingActivation? keptTools = null;
            var configuration = revision.Configuration;
            if (_registry is { } registry)
            {
                var current = GetToolActivationSelection().Names;
                var keepCurrent = !current.IsEmpty && !prospective.LlmMessages.Any(message => message.Role == "system");
                var (restoredLoadout, keptPresentation) = await PrepareAndDrainLoadoutAsync(() => (registry.ResolveRestored(prospective, revision.Configuration.Model, work),
                    keepCurrent ? registry.PrepareActiveLoadout(registry.NormalizeActiveTools(current, work), work) : null), work).ConfigureAwait(false);
                configuration = restoredLoadout.Selection.Configuration;
                if (keepCurrent) keptTools = new(0, registry.NormalizeActiveTools(current, work), keptPresentation);
                else
                {
                    pendingTools = restoredLoadout.Pending;
                    if (restoredLoadout.RequiresRecord) restoredRecord = registry.CreateActivationMessage(configuration.Tools.Select(tool => tool.Name).ToImmutableArray(),
                        RecordedActiveToolNames(prospective, work), _clock(), work, replaceDeclarations: true);
                }
            }
            ValidateRuntimeContext(prospective, configuration);
            var messages = SessionContextProjector.AgentMessages(prospective);
            await using (var probe = new NativeAgent(configuration, _clock, new NoopSink(), _agentOptions))
                probe.ConfigureAndReplaceMessages(configuration, messages);
            var abandoned = request.TargetId is null
                ? new SessionBranchSummaryCollection(revision.Context.Ancestry, null)
                : new SessionBranchSummaryPlanner().Collect(revision.Log.Entries, revision.Context.LeafId, request.TargetId, work);
            var preview = new SessionTreeNavigationPreview(revision.Log.Header.Id, request.TargetId, revision.Context.LeafId,
                newLeaf, abandoned.CommonAncestorId, abandoned.Entries, prospective, configuration, editorText);
            if (beforeTree is not null && !await originals.Join(beforeTree(preview, work).AsTask(), "tree-veto").ConfigureAwait(false))
            {
                work.ThrowIfCancellationRequested();
                return Unchanged(SessionTreeNavigationDisposition.Vetoed);
            }
            var options = request.Options;
            SessionProvidedSummary? provided = null;
            if (request.Execution?.BeforeTree is { } prepare)
            {
                var result = await originals.Join(prepare(preview, options, work).AsTask(), "tree-preparation").ConfigureAwait(false);
                if(result is not null)foreach(var original in result.Originals)originals.Retain(original);
                if (result?.Cancel == true) return Unchanged(SessionTreeNavigationDisposition.Vetoed);
                if (result is not null)
                {
                    provided = result.Summary;
                    options = options with { CustomInstructions = result.CustomInstructions is { } instructions ? instructions.Value : options.CustomInstructions,
                        ReplaceInstructions = result.ReplaceInstructions?.Value ?? options.ReplaceInstructions,
                        Label = result.Label is { } label ? label.Value : options.Label };
                }
            }
            var records = await PrepareTreeRecordsAsync(revision, preview, options, provided, request.Execution, work, originals).ConfigureAwait(false);
            if (restoredRecord is { } loadout)
                records = records.Add(Record(_codec, "message", Identity(_nextEntryId, revision.Log.Header.Id, revision.Log.Entries.AddRange(records)),
                    records.IsEmpty ? newLeaf : records[^1].Id, _clock, writer => { writer.WritePropertyName("message"); writer.WriteRawValue(loadout.WireBody.Value.GetRawText()); }));
            var publishedLog = revision.Log;
            if (!records.IsEmpty)
            {
                await _commits.WaitAsync(work).ConfigureAwait(false); commitHeld = true;
                lock (_gate) { ThrowAvailable(); ValidateTreeRevision(revision); ValidateTreeQueuePublication(idle); }
                var summary = records.FirstOrDefault(entry => entry.Kind == SessionEntryKind.BranchSummary);
                var projectedLeaf = records[^1].Id;
                prospective = _projector.Project(revision.Log.Entries.AddRange(records), projectedLeaf, work);
                configuration = _registry is { } changedRegistry
                    ? (await PrepareAndDrainLoadoutAsync(() => changedRegistry.Resolve(prospective, revision.Configuration.Model, work), work).ConfigureAwait(false)).Configuration
                    : revision.Configuration;
                ValidateRuntimeContext(prospective, configuration);
                messages = SessionContextProjector.AgentMessages(prospective);
                await using (var probe = new NativeAgent(configuration, _clock, new NoopSink(), _agentOptions))
                    probe.ConfigureAndReplaceMessages(configuration, messages);
                if(request.Execution?.ValidateProspective is { } validate)
                    await originals.Join(validate(prospective,work).AsTask(),"tree-prospective-validator").ConfigureAwait(false);
                work.ThrowIfCancellationRequested(); writeAdmitted = true;
                var append = await originals.Join(_store.AppendAsync(records, work), "tree-storage-checkpoint").ConfigureAwait(false);
                if (!append.CheckpointAcknowledged) throw Error(PersistentAgentSessionFailure.InvalidCommit);
                publishedLog = append.Snapshot;
                checkpoint = new(append, summary?.WireBody, records.FirstOrDefault(entry => entry.Kind == SessionEntryKind.Label)?.WireBody);
            }
            TreeSelection? selected = null;
            // The host validates attachment under its publication gate. No callback/output runs here.
            // Cancellation before this boundary preserves both states; after it the receipt is retained.
            publish(() =>
            {
                lock (_gate)
                {
                    ThrowAvailable(); ValidateTreeRevision(revision); if (checkpoint is null) work.ThrowIfCancellationRequested();
                    ValidateTreeQueuePublication(idle);
                    var restoreActivation = PrepareActivationRestoration(configuration);
                    _agent.ConfigureAndReplaceMessages(RecoveryConfiguration(configuration), messages);
                    _configuration = configuration; _context = prospective; _acknowledgedLog = publishedLog;
                    restoreActivation();
                    // The kept tools remain the logical selection; the next request boundary records them.
                    if (keptTools is not null) _pendingActivation = keptTools with { Epoch = _activationEpoch };
                    // Source _restoreToolsFromTranscript replaces the pending set with the target's unregistered tools.
                    _pendingToolNames = pendingTools;
                    selected = new(SessionTreeNavigationDisposition.Selected,
                        new(revision.Attachment, this, publishedLog, prospective, configuration,
                            new SessionTreeQueries().Build(publishedLog.Entries), _activationEpoch), _agent.Snapshot, editorText)
                        { Checkpoint = checkpoint, OriginalOwner = originals };
                }
            });
            return selected!;
        }
        catch (OperationCanceledException error) when (abort.Abort.IsCancellationRequested &&
            !token.IsCancellationRequested && !_closing.IsCancellationRequested && !inputAbort.IsCancellationRequested &&
            error.CancellationToken == work && checkpoint is null)
        {
            // A foreign OCE must never become a successful abort receipt. Only the linked work token
            // can identify our Abort. Caller/lifetime/closing cancellation remains exceptional.
            return Unchanged(SessionTreeNavigationDisposition.Aborted);
        }
        catch (Exception error)
        {
            bodyFailure = checkpoint is not null ? new SessionTreeCheckpointPublicationException(checkpoint, error) : error;
            if (writeAdmitted) lock (_gate) _fault ??= new(PersistentAgentSessionFailure.InvalidCommit, MayHaveWritten: true);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(bodyFailure).Throw(); throw;
        }
        finally
        {
            var failures = new List<Exception>();
            try { await originals.JoinAll(row => row.Original.IsCanceled && row.Observed is OperationCanceledException canceled &&
                canceled.CancellationToken == work && work.IsCancellationRequested).ConfigureAwait(false); }
            catch (Exception error) { SessionBoundaryOriginals.Add(failures, error); }
            if (commitHeld) _commits.Release();
            Task cancelIdle;
            lock (_gate)
            {
                if (ReferenceEquals(_contextEditCancellation, abort)) _contextEditCancellation = null;
                cancelIdle = abort.CancelUsers == 0 ? Task.CompletedTask : abort.CancelIdle!.Task;
            }
            try { await originals.Join(cancelIdle, "tree-cancellation-users").ConfigureAwait(false); }
            catch (Exception error) { SessionBoundaryOriginals.Add(failures, error); }
            try { abort.Abort.Dispose(); } catch (Exception error) { SessionBoundaryOriginals.Add(failures, error); }
            finally
            {
                lock (_gate) if (ReferenceEquals(_active, idle)) { _active = null; _compacting = false; }
                idle.TrySetResult(); _configurationCallback.Value = previousCallback;
            }
            if (failures.Count != 0)
            { if (bodyFailure is not null) SessionBoundaryOriginals.Add(failures, bodyFailure); SessionBoundaryOriginals.Throw(failures, originals.Snapshot()); }
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
