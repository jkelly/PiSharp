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

    private Task<SessionToolCatalogReceipt> AdmitToolCatalogPublication(SessionRuntimeRegistry expected,
        SessionRuntimeRegistry replacement, ImmutableArray<string> activeNames, Action publishPreparedRegistry,
        CancellationToken cancellationToken, ReplacementReservation? reservation)
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
                _inputSubmission is not null && (reservation is null || !IsExecutingInputCallback) ||
                reservation is null && _pendingActivation is not null)
                throw new InvalidOperationException("Catalog publication requires the captured idle session registry.");
            var state = _agent.Snapshot;
            if (!state.PendingInputs.IsEmpty || state.SteeringCount != 0 || state.FollowUpCount != 0)
                throw new InvalidOperationException("Catalog publication cannot replace queued input declarations.");
            idle = new(TaskCreationOptions.RunContinuationsAsynchronously); _active = idle; _configuring = true;
        }
        return PublishToolCatalogCoreAsync(expected, replacement, activeNames, publishPreparedRegistry, cancellationToken, idle);
    }

    private async Task<SessionToolCatalogReceipt> PublishToolCatalogCoreAsync(SessionRuntimeRegistry expected,
        SessionRuntimeRegistry replacement, ImmutableArray<string> activeNames, Action publishPreparedRegistry,
        CancellationToken token, TaskCompletionSource idle)
    {
        var writeAdmitted = false; var commitHeld = false;
        var prior = _configurationCallback.Value; _configurationCallback.Value = idle;
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _closing.Token);
            var work = cancellation.Token;
            await _commits.WaitAsync(work).ConfigureAwait(false); commitHeld = true;
            SessionContextProjection context; SessionLogStoreSnapshot log; AgentConfiguration configuration; long nextActivation;
            lock (_gate)
            {
                if (!ReferenceEquals(_registry, expected)) throw new InvalidOperationException("Captured registry changed.");
                context = _context; log = _acknowledgedLog; configuration = _configuration;
                nextActivation = checked(_activationEpoch + 1);
            }
            var selected = replacement.NormalizeActiveTools(activeNames, work);
            // Always replace declarations, including a changed schema under an unchanged name.
            var byName = replacement.RegisteredTools.ToDictionary(tool => tool.Adapter.Name, StringComparer.Ordinal);
            var message = JsonData.Parse(JsonSerializer.Serialize(new { role = "system", content = "", timestamp = _clock(),
                toolsRemoved = RecordedActiveToolNames(context, work).Select(name => new { name }),
                toolsAdded = selected.Select(name => byName[name].Declaration.Value) }));
            var entry = Record(_codec, "message", Identity(_nextEntryId, log.Header.Id, log.Entries), context.LeafId, _clock,
                writer => { writer.WritePropertyName("message"); writer.WriteRawValue(message.Value.GetRawText()); });
            var prospective = _projector.Project(log.Entries.Add(entry), entry.Id, work);
            var selection = await replacement.PrepareAndDrainAsync(() => replacement.Resolve(prospective, configuration.Model, work), work)
                .ConfigureAwait(false);
            ValidateRuntimeContext(prospective, selection.Configuration);
            await using (var probe = new NativeAgent(selection.Configuration, _clock, new NoopSink(), _agentOptions))
                probe.ConfigureAndReplaceMessages(selection.Configuration, SessionContextProjector.AgentMessages(prospective));
            work.ThrowIfCancellationRequested(); writeAdmitted = true;
            var acknowledged = await _store.AppendAsync([entry], work).ConfigureAwait(false);
            if (!acknowledged.CheckpointAcknowledged) throw Error(PersistentAgentSessionFailure.InvalidCommit);
            lock (_gate)
            {
                // No caller-cancellation check after durable acknowledgment: join the actual publication.
                publishPreparedRegistry();
                _agent.ConfigureAndReplaceMessages(RecoveryConfiguration(selection.Configuration), SessionContextProjector.AgentMessages(prospective));
                _registry = replacement; _configuration = selection.Configuration;
                _acknowledgedLog = acknowledged.Snapshot; _context = prospective;
                _activationEpoch = nextActivation; _pendingActivation = null;
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
            lock (_gate) if (ReferenceEquals(_active, idle)) { _active = null; _configuring = false; }
            idle.TrySetResult(); _configurationCallback.Value = prior;
        }
    }
}
