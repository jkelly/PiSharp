// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session.ts (_installAgentBoundaryHooks,
// _dispatchTurnEndBoundary, _runBeforeSettleBoundary, _applyBoundaryDrafts, _createBoundaryPreviewManager, _buildBoundaryContext,
// _commitBoundaryDrafts, _reportInvalidBoundaryContinuation, and the message_end replacement in _handleAgentEvent) and
// core/extensions/types.ts (SessionBoundaryDraft, BoundaryContextPreview, BoundaryResult, MessageEndEventResult).
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Sessions.Compaction;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;
using NativeAgent = PiSharp.Agent.Agent;

namespace PiSharp.CodingAgent;

public enum SessionBoundaryKind { TurnEnd, AgentBeforeSettle }

/// <summary>Source BoundaryContextPreview: the context the selected branch would project with the drafted entries appended.</summary>
public sealed record SessionBoundaryPreview(SessionContextProjection Context, ImmutableArray<TranscriptEntry> PendingMessages, bool CanContinue);

/// <summary>One turn_end or agent_before_settle dispatch. <see cref="Preview"/> projects drafted entries (source
/// SessionBoundaryDraft objects) without writing them; it throws for drafts the session would refuse.</summary>
public sealed class SessionBoundaryRequest
{
    private readonly Func<ImmutableArray<JsonData>, CancellationToken, SessionBoundaryPreview> _preview;
    internal SessionBoundaryRequest(SessionBoundaryKind kind, string outcome, AgentLoopTurn? turn,
        Func<ImmutableArray<JsonData>, CancellationToken, SessionBoundaryPreview> preview)
    { Kind = kind; Outcome = outcome; Turn = turn; _preview = preview; }
    public SessionBoundaryKind Kind { get; }
    /// <summary>Source AgentActivityOutcome: completed, aborted or error.</summary>
    public string Outcome { get; }
    /// <summary>The finished turn (turn_end only).</summary>
    public AgentLoopTurn? Turn { get; }
    public SessionBoundaryPreview Preview(ImmutableArray<JsonData> drafts, CancellationToken cancellationToken = default) => _preview(drafts, cancellationToken);
}

/// <summary>Source BoundaryDispatchResult: the drafted entries to commit and whether one more provider request is ensured.</summary>
public sealed record SessionBoundaryDecision(ImmutableArray<JsonData> Entries, bool Continue);
public delegate ValueTask<SessionBoundaryDecision?> SessionBoundaryHandler(SessionBoundaryRequest request, CancellationToken cancellationToken);
/// <summary>Source emitMessageEnd: a replacement with the same role, or null to keep the message.</summary>
public delegate ValueTask<TranscriptEntry?> SessionMessageEndHandler(TranscriptEntry message, CancellationToken cancellationToken);

/// <summary>Reads a Pi Usage object.</summary>
public static class SessionWireUsage
{
    public static TokenUsage Read(JsonElement usage)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject(); writer.WriteString("role", "assistant"); writer.WriteStartArray("content"); writer.WriteEndArray();
            writer.WriteString("api", "usage"); writer.WriteString("provider", "usage"); writer.WriteString("model", "usage");
            writer.WritePropertyName("usage"); writer.WriteRawValue(usage.GetRawText());
            writer.WriteString("stopReason", "stop"); writer.WriteNumber("timestamp", 0); writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(bytes.ToArray());
        return PiWireJson.ReadMessage(document.RootElement).Usage;
    }
}

public sealed partial class PersistentAgentSession
{
    private SessionBoundaryHandler? _turnBoundary, _settleBoundary;
    private Func<SessionBoundaryKind, ValueTask>? _invalidBoundaryContinuation;
    private SessionMessageEndHandler? _messageEnd;
    private Func<Exception, ValueTask>? _messageEndRejected;
    private string _lastActivityOutcome = "completed";
    private readonly Dictionary<string, JsonData> _replacedMessages = new(StringComparer.Ordinal);

    /// <summary>Trusted host binding of the turn_end and agent_before_settle handlers. <paramref name="invalidContinuation"/> reports a
    /// continuation requested without runnable model context (source _reportInvalidBoundaryContinuation).</summary>
    public void ConfigureBoundaryHandlers(SessionBoundaryHandler? turnEnd, SessionBoundaryHandler? beforeSettle,
        Func<SessionBoundaryKind, ValueTask>? invalidContinuation = null)
    {
        lock (_gate) { ThrowBindable(); _turnBoundary = turnEnd; _settleBoundary = beforeSettle; _invalidBoundaryContinuation = invalidContinuation; }
    }

    /// <summary>Trusted host binding of the message_end handlers. A replacement is persisted in place of the finalized message;
    /// one the session cannot record is passed to <paramref name="rejected"/> and the original is kept.</summary>
    public void ConfigureMessageEndHandler(SessionMessageEndHandler? handler, Func<Exception, ValueTask>? rejected = null)
    {
        lock (_gate) { ThrowBindable(); _messageEnd = handler; _messageEndRejected = rejected; }
    }

    /// <summary>The persisted form of a finalized message: its message_end replacement, if a handler replaced it.</summary>
    public JsonData PersistedWire(JsonData message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_gate) return _replacedMessages.TryGetValue(message.Value.GetRawText(), out var replacement) ? replacement : message;
    }

    /// <summary>Source finishTurn with _dispatchTurnEndBoundary: turn_end handlers run once the turn's messages are persisted; their
    /// drafted entries are committed at once and the running loop continues from the refreshed context (no new agent run). A
    /// requested continuation becomes the loop's continue decision when the context can run; an earlier end decision wins.</summary>
    private async ValueTask<AgentLoopFinishAction> TurnBoundaryAsync(AgentLoopTurn turn, AgentLoopFinishAction decision, CancellationToken token)
    {
        var stop = turn.Result.Chat.Message.StopReason;
        var outcome = stop == StopReason.Aborted ? "aborted" : stop == StopReason.Error ? "error" : "completed";
        SessionBoundaryHandler? handler;
        lock (_gate) { _lastActivityOutcome = outcome; handler = _turnBoundary; }
        if (handler is null) return decision;
        var result = await handler(new(SessionBoundaryKind.TurnEnd, outcome, turn, Preview(SessionBoundaryKind.TurnEnd)), token).ConfigureAwait(false);
        if (result is null) return decision;
        if (!result.Entries.IsDefaultOrEmpty) await CommitBoundaryDraftsAsync(result.Entries, null).ConfigureAwait(false);
        var extensionContinue = result.Continue;
        if (extensionContinue && !PreviewCore(SessionBoundaryKind.TurnEnd, [], CancellationToken.None).CanContinue)
        { await ReportInvalidContinuationAsync(SessionBoundaryKind.TurnEnd).ConfigureAwait(false); extensionContinue = false; }
        if (decision == AgentLoopFinishAction.End) return decision;
        return extensionContinue || decision == AgentLoopFinishAction.Continue ? AgentLoopFinishAction.Continue : AgentLoopFinishAction.Default;
    }

    /// <summary>Source _runBeforeSettleBoundary. Null when no handler is bound; otherwise whether the run continues.</summary>
    private async Task<bool?> RunBeforeSettleBoundaryAsync(TaskCompletionSource idle, CancellationToken token)
    {
        SessionBoundaryHandler? handler; string outcome;
        lock (_gate) { handler = _settleBoundary; outcome = _lastActivityOutcome; }
        if (handler is null) return null;
        var result = await handler(new(SessionBoundaryKind.AgentBeforeSettle, outcome, null, Preview(SessionBoundaryKind.AgentBeforeSettle)), token).ConfigureAwait(false);
        if (result is { Entries.IsDefaultOrEmpty: false }) await CommitBoundaryDraftsAsync(result.Entries, idle).ConfigureAwait(false);
        var final = PreviewCore(SessionBoundaryKind.AgentBeforeSettle, [], CancellationToken.None);
        if (token.IsCancellationRequested) return false;
        var shouldContinue = result?.Continue == true || HasQueuedInput();
        if (shouldContinue && !final.CanContinue)
        {
            if (result?.Continue == true) await ReportInvalidContinuationAsync(SessionBoundaryKind.AgentBeforeSettle).ConfigureAwait(false);
            return false;
        }
        return shouldContinue;
    }

    private bool HasQueuedInput()
    {
        lock (_gate)
        {
            var queued = _agent.GetPendingInputQueueSnapshot();
            return !queued.SteeringMessages.IsEmpty || !queued.FollowUpMessages.IsEmpty;
        }
    }
    private async ValueTask ReportInvalidContinuationAsync(SessionBoundaryKind kind)
    {
        Func<SessionBoundaryKind, ValueTask>? report; lock (_gate) report = _invalidBoundaryContinuation;
        if (report is not null) try { await report(kind).ConfigureAwait(false); } catch (Exception) { }
    }

    private Func<ImmutableArray<JsonData>, CancellationToken, SessionBoundaryPreview> Preview(SessionBoundaryKind kind) =>
        (drafts, token) => PreviewCore(kind, drafts, token);

    /// <summary>Source _buildBoundaryContext over the preview manager (the branch with the drafts applied).</summary>
    private SessionBoundaryPreview PreviewCore(SessionBoundaryKind kind, ImmutableArray<JsonData> drafts, CancellationToken token)
    {
        SessionContextProjection previous; SessionLogStoreSnapshot log; AgentPendingInputQueueSnapshot queue; bool pendingCustom;
        lock (_gate)
        {
            previous = _context; log = _acknowledgedLog; queue = _agent.GetPendingInputQueueSnapshot();
            pendingCustom = _agent.ContextOnlyPendingCount != 0 || !_nextTurnCustomMessages.IsEmpty;
        }
        var index = 0;
        var (_, context) = BoundaryEntries(previous, log, drafts, _ => "boundary-preview-" + ++index, _clock, token);
        var pending = queue.SteeringMessages.AddRange(queue.FollowUpMessages);
        var llm = context.LlmMessages;
        var finalRole = llm.IsEmpty ? null : llm[^1].Role;
        var contextCanContinue = llm.Any(message => message.Role != "system") && finalRole != "assistant";
        var queued = !pending.IsEmpty;
        return new(context, pending, contextCanContinue || pendingCustom ||
            (kind == SessionBoundaryKind.TurnEnd ? queued : finalRole == "assistant" && queued));
    }

    /// <summary>Source _commitBoundaryDrafts: append the drafts in order, refresh the agent context, and emit entry_appended for each.
    /// With <paramref name="idle"/> null it runs inside the active run's turn boundary, and the loop continues from the refreshed context.</summary>
    private async Task CommitBoundaryDraftsAsync(ImmutableArray<JsonData> drafts, TaskCompletionSource? idle)
    {
        await _commits.WaitAsync().ConfigureAwait(false); var admitted = false;
        try
        {
            SessionContextProjection previous; SessionLogStoreSnapshot log;
            lock (_gate)
            {
                ThrowAvailable();
                if (idle is null ? _active is null : !ReferenceEquals(_active, idle)) throw Error(PersistentAgentSessionFailure.StaleSession);
                previous = _context; log = _acknowledgedLog;
            }
            var (entries, prospective) = BoundaryEntries(previous, log, drafts, existing => Identity(_nextEntryId, log.Header.Id, existing), _clock, CancellationToken.None);
            ValidateRuntimeContext(prospective, _configuration, _toleratedSelection);
            if (_registry is not null) ValidateLoadout(prospective.LlmMessages);
            await using (var probe = new NativeAgent(_configuration, _clock, new NoopSink(), _agentOptions))
                probe.ConfigureAndReplaceMessages(_configuration, SessionContextProjector.AgentMessages(prospective));
            admitted = true;
            var acknowledged = await _store.AppendAsync(entries, CancellationToken.None).ConfigureAwait(false);
            if (!acknowledged.CheckpointAcknowledged) throw Error(PersistentAgentSessionFailure.InvalidCommit);
            lock (_gate)
            {
                if (idle is null) _agent.ReplaceRunMessages(SessionContextProjector.AgentMessages(prospective));
                else _agent.ConfigureAndReplaceMessages(RecoveryConfiguration(_configuration), SessionContextProjector.AgentMessages(prospective));
                _acknowledgedLog = acknowledged.Snapshot; _context = prospective;
            }
            admitted = false; await PublishAppendedAsync(acknowledged.Entries).ConfigureAwait(false);
        }
        catch (SessionLogStoreException storage)
        {
            if (storage.MayHaveWritten || _store.IsPoisoned)
                lock (_gate) _fault ??= new(PersistentAgentSessionFailure.AppendFailed, storage.Failure, storage.MayHaveWritten, storage.DurableFlushCompleted);
            throw;
        }
        catch { if (admitted) lock (_gate) _fault ??= new(PersistentAgentSessionFailure.InvalidCommit); throw; }
        finally { _commits.Release(); }
    }

    /// <summary>Source _applyBoundaryDrafts: one entry per draft, each a child of the previous one.</summary>
    private (ImmutableArray<SessionEntry> Entries, SessionContextProjection Context) BoundaryEntries(SessionContextProjection previous,
        SessionLogStoreSnapshot log, ImmutableArray<JsonData> drafts, Func<ImmutableArray<SessionEntry>, string> nextId, Func<long> clock,
        CancellationToken token)
    {
        var all = log.Entries; var added = ImmutableArray.CreateBuilder<SessionEntry>(); var context = previous; var parent = previous.LeafId;
        foreach (var draft in drafts.IsDefault ? [] : drafts)
        {
            token.ThrowIfCancellationRequested();
            var value = draft?.Value ?? default;
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("A boundary entry needs a type.");
            var id = nextId(all);
            var current = context; var entryParent = parent;
            var entry = type.GetString() switch
            {
                "custom" => Record(_codec, "custom", id, entryParent, clock, writer =>
                {
                    writer.WriteString("customType", Text(value, "customType"));
                    if (value.TryGetProperty("data", out var data)) { writer.WritePropertyName("data"); writer.WriteRawValue(data.GetRawText()); }
                }),
                "custom_message" => Record(_codec, "custom_message", id, entryParent, clock, writer =>
                {
                    writer.WriteString("customType", Text(value, "customType"));
                    if (!value.TryGetProperty("content", out var content) || content.ValueKind is not (JsonValueKind.String or JsonValueKind.Array))
                        throw new InvalidDataException("A custom_message entry needs content.");
                    writer.WritePropertyName("content"); writer.WriteRawValue(content.GetRawText());
                    writer.WriteBoolean("display", value.TryGetProperty("display", out var display) && display.ValueKind == JsonValueKind.True);
                    if (value.TryGetProperty("details", out var details)) { writer.WritePropertyName("details"); writer.WriteRawValue(details.GetRawText()); }
                }),
                "context_edit" => ContextEditRecord(Text(value, "targetId"), SessionContextEditValidator.Normalize(current,
                    new(Text(value, "targetId"), value.TryGetProperty("replacement", out var replacement) ? JsonData.Parse(replacement.GetRawText()) : null), token),
                    id, entryParent, clock),
                "compaction" => SummaryRecord(true, id, entryParent,
                    value.TryGetProperty("firstKeptEntryId", out var kept) && kept.ValueKind == JsonValueKind.String ? kept.GetString() :
                        kept.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : throw new InvalidDataException("A compaction entry needs firstKeptEntryId."),
                    SessionCompactionTokenEstimator.EstimateProjectedContextTokens(current).Tokens, Text(value, "summary"),
                    value.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object ? SessionWireUsage.Read(usage) : null,
                    value.TryGetProperty("details", out var compactionDetails) ? JsonData.Parse(compactionDetails.GetRawText()) : null, true, current, clock),
                var other => throw new InvalidDataException($"Unknown boundary entry type: {other}")
            };
            all = all.Add(entry); added.Add(entry); parent = entry.Id;
            context = _projector.Project(all, entry.Id, token);
        }
        return (added.ToImmutable(), context);

        static string Text(JsonElement value, string name) =>
            value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString()! :
                throw new InvalidDataException($"A boundary entry needs {name}.");
    }

    /// <summary>Source emitMessageEnd before the message is persisted. Runs outside the commit lock: handlers may append entries.</summary>
    private async ValueTask<TranscriptEntry?> MessageEndReplacementAsync()
    {
        SessionMessageEndHandler? handler; lock (_gate) handler = _messageEnd;
        if (handler is null) return null;
        var messages = _agent.Snapshot.Messages;
        if (messages.IsEmpty) return null;
        var original = messages[^1];
        var replacement = await handler(original, CancellationToken.None).ConfigureAwait(false);
        return replacement is not null && replacement.Role == original.Role && replacement.WireBody.Value.ValueKind == JsonValueKind.Object ? replacement : null;
    }
    private async ValueTask RejectMessageEndAsync(Exception error)
    {
        Func<Exception, ValueTask>? rejected; lock (_gate) rejected = _messageEndRejected;
        if (rejected is not null) try { await rejected(error).ConfigureAwait(false); } catch (Exception) { }
    }
}
