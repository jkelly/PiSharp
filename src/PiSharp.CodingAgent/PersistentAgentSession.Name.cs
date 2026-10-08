using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

namespace PiSharp.CodingAgent;

public sealed partial class PersistentAgentSession
{
    private Func<SessionInfoChanged, ValueTask>? _sessionInfoObservation;
    /// <summary>Trusted extension binding captured per naming reservation, with no veto or event abort signal.</summary>
    public void ConfigureSessionInfoObservation(Func<SessionInfoChanged, ValueTask>? observer)
    {
        lock (_gate)
        {
            ThrowAvailable(); ThrowInputMutation(); ThrowConfigurationSelfWait();
            if (_active is not null || _inputSubmission is not null) throw new InvalidOperationException("Metadata observation binding requires an idle session.");
            _sessionInfoObservation = observer;
        }
    }
    internal void ConfigureSessionInfoObservationForBinding(ReplaceableAgentSession owner, AgentSessionAttachment attachment,
        ReplacementReservation? reservation,
        Func<SessionInfoChanged, ValueTask>? observer)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(attachment.Session, this)) throw new InvalidOperationException("Observation session differs from the attachment.");
            owner.ValidateRuntimeObservationBinding(attachment, reservation);
            if (reservation is null) ThrowAvailable(); else reservation.ValidateCatalogAuthority(this);
            ThrowInputMutation(); ThrowConfigurationSelfWait();
            if (_active is not null || _inputSubmission is not null) throw new InvalidOperationException("Metadata observation binding requires an idle session.");
            _sessionInfoObservation = observer;
        }
    }
    /// <summary>Appends an already sanitized, nonempty title using this coordinator's idle reservation and actual checkpoint.
    /// The owning host must also enforce captured attachment authority. Notifications follow acknowledged publication.</summary>
    public Task<SessionEntry> SetSessionNameAsync(string expectedSessionId, string name, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 65_536 || name.Contains('\r') || name.Contains('\n'))
            throw new ArgumentException("Session name must be a bounded nonempty single line.", nameof(name));
        token.ThrowIfCancellationRequested(); TaskCompletionSource idle; Func<SessionInfoChanged, ValueTask>? observer;
        lock (_gate)
        {
            ThrowAvailable(); ThrowInputMutation();
            if (!string.Equals(expectedSessionId, _acknowledgedLog.Header.Id, StringComparison.Ordinal))
                throw Error(PersistentAgentSessionFailure.StaleSession);
            if (_active is not null || _inputSubmission is not null) throw new InvalidOperationException("Session is already processing.");
            var agent = _agent.Snapshot;
            if (!agent.PendingInputs.IsEmpty || agent.SteeringCount != 0 || agent.FollowUpCount != 0)
                throw Error(PersistentAgentSessionFailure.InvalidConfiguration);
            token.ThrowIfCancellationRequested();
            idle = new(TaskCreationOptions.RunContinuationsAsynchronously); _active = idle; _configuring = true;
            observer = _sessionInfoObservation;
        }
        return SetSessionNameCoreAsync(name, token, idle, observer);
    }

    private async Task<SessionEntry> SetSessionNameCoreAsync(string name, CancellationToken token, TaskCompletionSource idle,
        Func<SessionInfoChanged, ValueTask>? observer)
    {
        var commitHeld = false; var writeAdmitted = false; var published = false;
        var priorCallback = _configurationCallback.Value; _configurationCallback.Value = idle;
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _closing.Token);
            var work = cancellation.Token;
            await _commits.WaitAsync(work).ConfigureAwait(false); commitHeld = true;
            SessionLogStoreSnapshot log; PiSharp.Sessions.Context.SessionContextProjection context;
            lock (_gate) { log = _acknowledgedLog; context = _context; }
            work.ThrowIfCancellationRequested();
            var entry = Record(_codec, "session_info", Identity(_nextEntryId, log.Header.Id, log.Entries), context.LeafId,
                _clock, writer => writer.WriteString("name", name), timestampFormat: "yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
            var prospective = _projector.Project(log.Entries.Add(entry), entry.Id, work);
            work.ThrowIfCancellationRequested(); writeAdmitted = true;
            var acknowledged = await _store.AppendAsync([entry], work).ConfigureAwait(false);
            if (!acknowledged.CheckpointAcknowledged) throw Error(PersistentAgentSessionFailure.InvalidCommit);
            lock (_gate) { _acknowledgedLog = acknowledged.Snapshot; _context = prospective; }
            published = true;
            // Publication is irreversible. Join source-ordered observers then extension notification
            // without a late caller cancellation check, still under original commit/idle ownership.
            var committedEntry = acknowledged.Entries.Single();
            var observation = new SessionInfoChanged(_operationGeneration,
                SessionInfoChanged.Normalize(committedEntry.WireBody.Value.GetProperty("name").GetString()!));
            await EmitOperationAsync(observation).ConfigureAwait(false);
            if (observer is not null) await observer(observation).ConfigureAwait(false);
            return committedEntry;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || _closing.IsCancellationRequested) { throw; }
        catch (SessionLogStoreException storage) when (!published)
        {
            var fault = new PersistentAgentSessionFault(PersistentAgentSessionFailure.AppendFailed, storage.Failure,
                storage.MayHaveWritten, storage.DurableFlushCompleted);
            if (storage.MayHaveWritten || _store.IsPoisoned) lock (_gate) _fault ??= fault;
            throw new PersistentAgentSessionException(fault);
        }
        catch
        {
            if (writeAdmitted && !published) lock (_gate) _fault ??= new(PersistentAgentSessionFailure.InvalidCommit);
            throw;
        }
        finally
        {
            if (commitHeld) _commits.Release();
            lock (_gate) if (ReferenceEquals(_active, idle)) { _active = null; _configuring = false; }
            idle.TrySetResult(); _configurationCallback.Value = priorCallback;
        }
    }
}
