using System.Collections.Immutable;
using System.Text.Json;
using System.Runtime.ExceptionServices;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;
using NativeAgent = PiSharp.Agent.Agent;

namespace PiSharp.CodingAgent;

public sealed partial class PersistentAgentSession
{
    private ImmutableArray<TranscriptEntry> _nextTurnCustomMessages = [];
    public bool HasPendingCustomMessages { get { lock (_gate) return !_nextTurnCustomMessages.IsEmpty || _agent.ContextOnlyPendingCount != 0; } }

    /// <summary>Actual custom_message delivery. Queued admission is not a durable commit receipt.</summary>
    public Task<SessionCustomMessageReceipt> SendCustomMessageAsync(string expectedSessionId, SessionCustomMessageDraft draft,
        SessionCustomMessageDelivery delivery = SessionCustomMessageDelivery.Steer, bool? triggerTurn = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.CustomType) || !Enum.IsDefined(delivery)) throw new ArgumentException("Invalid custom delivery.");
        cancellationToken.ThrowIfCancellationRequested();
        // Record codec owns shape/scalar/depth/byte limits before queue, clock, ID or durable effects.
        _ = Record(_codec, "custom_message", "custom-validation", null, () => 0, writer => WriteCustom(writer, draft));
        var timestamp = _clock();
        TranscriptEntry message;
        // JsonData must be embedded as JSON, not reflection-serialized wrapper properties.
        using (var bytes = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(bytes))
            { writer.WriteStartObject(); writer.WriteString("role", "custom"); WriteCustom(writer, draft); writer.WriteNumber("timestamp", timestamp); writer.WriteEndObject(); }
            message = new("custom", JsonData.Parse(System.Text.Encoding.UTF8.GetString(bytes.ToArray())));
        }
        TaskCompletionSource? idle = null;
        lock (_gate)
        {
            ThrowAvailable(); ThrowInputMutation(); cancellationToken.ThrowIfCancellationRequested();
            if (expectedSessionId != _acknowledgedLog.Header.Id) throw Error(PersistentAgentSessionFailure.StaleSession);
            if (delivery == SessionCustomMessageDelivery.NextTurn)
            {
                var limits = _agentOptions?.Queue ?? new AgentPendingInputQueueOptions();
                var length = message.WireBody.ToString().Length;
                if (length > limits.MaximumMessageCharacters || _nextTurnCustomMessages.Length >= limits.MaximumMessagesPerQueue ||
                    _nextTurnCustomMessages.Sum(value => (long)value.WireBody.ToString().Length) + length > limits.MaximumCharactersPerQueue)
                    throw new ArgumentException("Next-turn custom queue exceeds admitted pending-input limits.");
                _nextTurnCustomMessages = _nextTurnCustomMessages.Add(message);
                return Task.FromResult(new SessionCustomMessageReceipt(SessionCustomMessageDisposition.Queued));
            }
            if (_active is not null)
            {
                RejectSettlementQueue(); RejectConfigurationQueue();
                if (triggerTurn == false) _agent.QueueContextOnly(message, cancellationToken);
                else if (delivery == SessionCustomMessageDelivery.FollowUp) _agent.FollowUp(message, cancellationToken);
                else _agent.Steer(message, cancellationToken);
                return Task.FromResult(new SessionCustomMessageReceipt(SessionCustomMessageDisposition.Queued));
            }
            if (_inputSubmission is not null) throw new InvalidOperationException("Input admission is already processing.");
            if (triggerTurn != true)
            {
                if (!_agent.Snapshot.PendingInputs.IsEmpty) throw Error(PersistentAgentSessionFailure.InvalidConfiguration);
                idle = new(TaskCreationOptions.RunContinuationsAsynchronously); _active = idle; _appendingExtensionEntry = true;
            }
        }
        return idle is not null ? AppendCustomMessageCoreAsync(draft, cancellationToken, idle)
            : TriggerCustomMessageCoreAsync(message, cancellationToken);
    }
    private async Task<SessionCustomMessageReceipt> TriggerCustomMessageCoreAsync(TranscriptEntry message, CancellationToken token)
    {
        var original = PromptAsync(message, token);
        try { return new(SessionCustomMessageDisposition.Started, Run: await original.ConfigureAwait(false)); }
        catch (Exception error) when (original.IsFaulted) { throw new AggregateException("Custom prompt original fault.", original.Exception!, error); }
    }
    private async Task<SessionCustomMessageReceipt> AppendCustomMessageCoreAsync(SessionCustomMessageDraft draft, CancellationToken token, TaskCompletionSource idle)
    {
        var held = false; var writeAdmitted = false; var prior = _configurationCallback.Value; _configurationCallback.Value = idle;
        CancellationTokenSource? linked = null; SessionCustomMessageReceipt? receipt = null; var failures = new List<Exception>();
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(token, _closing.Token);
            var work = linked.Token; await _commits.WaitAsync(work).ConfigureAwait(false); held = true;
            SessionContextProjection previous; SessionLogStoreSnapshot log; AgentConfiguration configuration;
            lock (_gate) { previous = _context; log = _acknowledgedLog; configuration = _configuration; }
            work.ThrowIfCancellationRequested();
            var entry = Record(_codec, "custom_message", Identity(_nextEntryId, log.Header.Id, log.Entries), previous.LeafId, _clock, writer => WriteCustom(writer, draft));
            var projected = _projector.Project(log.Entries.Add(entry), entry.Id, work);
            ValidateRuntimeContext(projected, configuration, _toleratedSelection); if (_registry is not null) ValidateLoadout(projected.LlmMessages, work);
            await ValidateCustomConfigurationAsync(configuration, projected).ConfigureAwait(false);
            work.ThrowIfCancellationRequested(); writeAdmitted = true;
            var original = _store.AppendAsync([entry], work);
            SessionLogAppendResult acknowledged;
            try { acknowledged = await original.ConfigureAwait(false); }
            catch (Exception error) when (original.IsFaulted) { throw new AggregateException("Custom durable append original fault.", original.Exception!, error); }
            if (!acknowledged.CheckpointAcknowledged) throw Error(PersistentAgentSessionFailure.InvalidCommit);
            lock (_gate)
            {
                _agent.ConfigureAndReplaceMessages(RecoveryConfiguration(configuration), SessionContextProjector.AgentMessages(projected));
                _acknowledgedLog = acknowledged.Snapshot; _context = projected;
            }
            var notificationOriginal = _agent.NotifyIdleCustomCheckpointAsync(_agent.Snapshot.Messages[^1], work);
            try { await notificationOriginal.ConfigureAwait(false); }
            catch (Exception error) when (notificationOriginal.IsFaulted)
            { throw new AggregateException("Already acknowledged custom checkpoint notification original.", notificationOriginal.Exception!, error); }
            receipt = new(SessionCustomMessageDisposition.Committed, entry);
        }
        catch (Exception error)
        {
            failures.Add(error);
            if (writeAdmitted && error is not OperationCanceledException)
                lock (_gate) _fault ??= new(PersistentAgentSessionFailure.AppendFailed);
        }
        finally
        {
            try { linked?.Dispose(); } catch (Exception error) { failures.Add(error); }
            try { if (held) _commits.Release(); } catch (Exception error) { failures.Add(error); }
            lock (_gate) if (ReferenceEquals(_active, idle)) { _active = null; _appendingExtensionEntry = false; }
            idle.TrySetResult(); _configurationCallback.Value = prior;
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count != 0) throw new AggregateException("Custom checkpoint operation and cleanup originals.", failures);
        return receipt!;
    }
    private async Task ValidateCustomConfigurationAsync(AgentConfiguration configuration, SessionContextProjection projected)
    {
        NativeAgent? probe = null; var failures = new List<Exception>();
        try { probe = new NativeAgent(configuration, _clock, new NoopSink(), _agentOptions); probe.ConfigureAndReplaceMessages(configuration, SessionContextProjector.AgentMessages(projected)); }
        catch (Exception error) { failures.Add(error); }
        if (probe is not null)
        {
            Task? original = null;
            try { original = probe.DisposeAsync().AsTask(); await original.ConfigureAwait(false); }
            catch (Exception error) { if (original?.Exception is { } aggregate) failures.Add(aggregate); failures.Add(error); }
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count != 0) throw new AggregateException("Custom configuration validation and probe cleanup originals.", failures);
    }
    private static void WriteCustom(Utf8JsonWriter writer, SessionCustomMessageDraft draft)
    {
        writer.WriteString("customType", draft.CustomType); writer.WritePropertyName("content");
        writer.WriteRawValue(draft.Content?.ToString() ?? "[]"); writer.WriteBoolean("display", draft.Display);
        if (draft.Details is not null) { writer.WritePropertyName("details"); writer.WriteRawValue(draft.Details.ToString()); }
    }
    // Called only under the actual session reservation gate; nextTurn is injected after the next USER.
    private ImmutableArray<TranscriptEntry> InjectNextTurnCustomLocked(ImmutableArray<TranscriptEntry> inputs)
    {
        if (inputs.IsDefault || _nextTurnCustomMessages.IsEmpty || !inputs.Any(value => value.Role == "user")) return inputs;
        var combined = inputs.AddRange(_nextTurnCustomMessages); _nextTurnCustomMessages = [];
        return combined;
    }
}
