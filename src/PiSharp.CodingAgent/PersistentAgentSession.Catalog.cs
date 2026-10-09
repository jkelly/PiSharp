using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Storage;
using NativeAgent = PiSharp.Agent.Agent;

namespace PiSharp.CodingAgent;

public sealed record SessionToolCatalogReceipt(PersistentAgentSessionSnapshot Snapshot, SessionRuntimeRegistry Registry);

public sealed partial class PersistentAgentSession
{
    public SessionRuntimeRegistry CaptureToolCatalogRegistry()
    {
        lock (_gate) { ThrowAvailable(); return _registry ?? throw Error(PersistentAgentSessionFailure.InvalidConfiguration); }
    }

    /// <summary>Reserves this idle session, validates the complete prepared pipeline and acknowledges
    /// its durable declaration replacement before the supplied synchronous registry publication.
    /// The callback must only commit an already prepared registry plan; it must not perform I/O or
    /// invoke user callbacks. A post-acknowledgment failure faults the session and is not a rollback.</summary>
    public Task<SessionToolCatalogReceipt> PublishToolCatalogAsync(SessionRuntimeRegistry expected,
        SessionRuntimeRegistry replacement, ImmutableArray<string> activeNames, Action publishPreparedRegistry,
        CancellationToken cancellationToken = default)
        => AdmitToolCatalogPublication(expected, replacement, activeNames, publishPreparedRegistry, cancellationToken, null);

    /// <summary>A catalog replacement that keeps the previously active names pending, as a source reload does.</summary>
    internal Task<SessionToolCatalogReceipt> PublishRestoringToolCatalogAsync(SessionRuntimeRegistry expected,
        SessionRuntimeRegistry replacement, ImmutableArray<string> activeNames, Action publishPreparedRegistry,
        CancellationToken cancellationToken = default)
        => AdmitToolCatalogPublication(expected, replacement, activeNames, publishPreparedRegistry, cancellationToken, null, restorePrevious: true);

    private Task<SessionToolCatalogReceipt> AdmitToolCatalogPublication(SessionRuntimeRegistry expected,
        SessionRuntimeRegistry replacement, ImmutableArray<string> activeNames, Action publishPreparedRegistry,
        CancellationToken cancellationToken, ReplacementReservation? reservation, bool restorePrevious = false)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(publishPreparedRegistry);
        if (reservation is null) ThrowConfigurationSelfWait();
        else if (_inLoadoutDiagnosticDrain.Value)
            throw new InvalidOperationException("A loadout diagnostic cannot publish its own retirement catalog.");
        if (!replacement.ReplacesCatalogOf(expected)) throw new ArgumentException("Catalog must derive from the exact captured session registry.");
        TaskCompletionSource idle;
        lock (_gate)
        {
            if (reservation is null) ThrowAvailable(); else reservation.ValidateCatalogAuthority(this);
            ThrowUserBashMutationLocked();
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_registry, expected) || _active is not null ||
                _inputSubmission is not null && (reservation is null || !IsExecutingInputCallback))
                throw new InvalidOperationException("Catalog publication requires the captured idle session registry.");
            var state = _agent.Snapshot;
            if (!state.PendingInputs.IsEmpty || state.SteeringCount != 0 || state.FollowUpCount != 0)
                throw new InvalidOperationException("Catalog publication cannot replace queued input declarations.");
            idle = new(TaskCreationOptions.RunContinuationsAsynchronously); _active = idle; _configuring = true; _catalogPublication = idle;
        }
        // Owned retirement publications (reload, resource withdrawal) keep the previous active names pending.
        return PublishToolCatalogCoreAsync(expected, replacement, activeNames, publishPreparedRegistry, cancellationToken, idle, restorePrevious || reservation is not null);
    }

    private async Task<SessionToolCatalogReceipt> PublishToolCatalogCoreAsync(SessionRuntimeRegistry expected,
        SessionRuntimeRegistry replacement, ImmutableArray<string> activeNames, Action publishPreparedRegistry,
        CancellationToken token, TaskCompletionSource idle, bool restorePrevious)
    {
        var writeAdmitted = false; var commitHeld = false;
        var prior = _configurationCallback.Value; _configurationCallback.Value = idle;
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _closing.Token);
            var work = cancellation.Token;
            await _commits.WaitAsync(work).ConfigureAwait(false); commitHeld = true;
            SessionContextProjection context; SessionLogStoreSnapshot log; AgentConfiguration configuration; long nextActivation;
            ImmutableArray<string> requested, pendingCandidates;
            lock (_gate)
            {
                if (!ReferenceEquals(_registry, expected)) throw new InvalidOperationException("Captured registry changed.");
                context = _context; log = _acknowledgedLog; configuration = _configuration;
                nextActivation = checked(_activationEpoch + 1);
                // Source _refreshToolRegistry: pending tools that are registered now become active.
                (requested, pendingCandidates) = PendingToolRequestLocked(replacement, activeNames, restorePrevious);
            }
            var selected = replacement.NormalizeActiveTools(requested, work);
            // Source declareToolChanges: the loadout's difference from the declared tools, a changed schema under an unchanged name
            // included; nothing when the declared tools are unchanged.
            var message = replacement.CreateToolChangeMessage(context.LlmMessages, selected, _clock(), work);
            var entry = message is null ? null : Record(_codec, "message", Identity(_nextEntryId, log.Header.Id, log.Entries), context.LeafId, _clock,
                writer => { writer.WritePropertyName("message"); writer.WriteRawValue(message.WireBody.Value.GetRawText()); });
            var prospective = entry is null ? context : _projector.Project(log.Entries.Add(entry), entry.Id, work);
            var selection = await replacement.PrepareAndDrainAsync(() => replacement.Resolve(prospective, configuration.Model, work, activeOrder: selected), work)
                .ConfigureAwait(false);
            ValidateRuntimeContext(prospective, selection.Configuration, _toleratedSelection, _toleratedThinking);
            await using (var probe = new NativeAgent(selection.Configuration, _clock, new NoopSink(), _agentOptions))
                probe.ConfigureAndReplaceMessages(selection.Configuration, SessionContextProjector.AgentMessages(prospective));
            work.ThrowIfCancellationRequested(); writeAdmitted = entry is not null;
            // Once admitted, finish the original append and publish its acknowledgment even if the owner closes meanwhile (an MCP
            // server publishing its catalog while the session shuts down): a cancelled append would fault the session.
            var acknowledged = entry is null ? null : await _store.AppendAsync([entry], CancellationToken.None).ConfigureAwait(false);
            if (acknowledged is { CheckpointAcknowledged: false }) throw Error(PersistentAgentSessionFailure.InvalidCommit);
            lock (_gate)
            {
                // No caller-cancellation check after durable acknowledgment: join the actual publication.
                publishPreparedRegistry();
                _agent.ConfigureAndReplaceMessages(RecoveryConfiguration(selection.Configuration), SessionContextProjector.AgentMessages(prospective));
                _registry = replacement; _configuration = selection.Configuration;
                if (acknowledged is not null) { _acknowledgedLog = acknowledged.Snapshot; _context = prospective; }
                _activationEpoch = nextActivation; _pendingActivation = null;
                // The publication recorded the whole loadout, replacing the recorded names: a restored loadout is recorded too.
                _unrecordedLoadout = false;
                RetirePendingToolsLocked(pendingCandidates, selected);
            }
            return new(Snapshot with { IsConfiguring = false }, replacement);
        }
        catch (SessionLogStoreException storage)
        {
            var fault = new PersistentAgentSessionFault(PersistentAgentSessionFailure.AppendFailed, storage.Failure,
                storage.MayHaveWritten, storage.DurableFlushCompleted);
            if (storage.MayHaveWritten || _store.IsPoisoned) lock (_gate) _fault ??= fault;
            throw new PersistentAgentSessionException(fault);
        }
        catch
        {
            if (writeAdmitted) lock (_gate) _fault ??= new(PersistentAgentSessionFailure.InvalidCommit);
            throw;
        }
        finally
        {
            if (commitHeld) _commits.Release();
            lock (_gate) { if (ReferenceEquals(_active, idle)) { _active = null; _configuring = false; } if (ReferenceEquals(_catalogPublication, idle)) _catalogPublication = null; }
            idle.TrySetResult(); _configurationCallback.Value = prior;
        }
    }
}
