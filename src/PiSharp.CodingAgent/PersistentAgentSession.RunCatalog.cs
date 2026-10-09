// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session.ts (_refreshToolRegistry during a run:
// a tool registered while the agent runs updates the registry and the active set at once, so the next model call of the same run
// declares it, and the change is recorded with the next request).
using System.Collections.Immutable;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;

namespace PiSharp.CodingAgent;

public sealed partial class PersistentAgentSession
{
    /// <summary>
    /// A catalog replacement while a run is in its provider phase (an MCP server connected or changed its tools mid-run): the
    /// registry is swapped at once, so tool discovery and nested calls of the running turn see the new tools, and the loadout is
    /// reconfigured and recorded (with replaced declarations) at the next request boundary of the run, or when the run settles if
    /// it makes no further request. Until it is recorded, the transcript and the agent
    /// keep the loadout of the previous registry, which validates them. Returns null when no run is in that phase; the caller
    /// then publishes at the idle boundary.
    /// <paramref name="prepare"/> receives the current registry and the current logical selection (a pending activation's names
    /// included) and must only prepare: its commit runs under the session gate once the swap is admitted.
    /// </summary>
    public async Task<SessionToolCatalogReceipt?> TryPublishToolCatalogDuringRunAsync(
        Func<SessionRuntimeRegistry, ImmutableArray<string>, CancellationToken, ValueTask<PreparedSessionToolCatalog>> prepare,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        if (_activationPreparation.Value || _inLoadoutDiagnosticDrain.Value) return null;
        // Source _refreshToolRegistry applies at once; a concurrent change that came first is prepared on again (each retry follows
        // another committed change), with no fallback to the idle boundary while the run is in its provider phase.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SessionRuntimeRegistry expected; ImmutableArray<string> names; long epoch; AgentConfiguration configuration;
            Sessions.Context.SessionContextProjection context;
            lock (_gate)
            {
                if (!RunCatalogAdmissibleLocked()) return null;
                expected = _registry!; epoch = _activationEpoch; configuration = _configuration; context = _context;
                names = _pendingActivation?.Names ?? configuration.Tools.Select(tool => tool.Name).ToImmutableArray();
            }
            var prepared = await prepare(expected, names, cancellationToken).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(prepared);
            if (!prepared.Registry.ReplacesCatalogOf(expected)) throw new ArgumentException("Catalog must derive from the exact captured session registry.");
            var replacement = prepared.Registry;
            var normalized = replacement.NormalizeActiveTools(prepared.ActiveNames, cancellationToken);
            if (normalized.Length > (_agentOptions?.MaximumTools ?? 128)) throw Error(PersistentAgentSessionFailure.InvalidConfiguration);
            // Validate the loadout the next boundary records, as an activation does before it is accepted.
            var delta = replacement.CreateActivationMessage(normalized, configuration.Tools.Select(tool => tool.Name).ToImmutableArray(), 0,
                cancellationToken, replaceDeclarations: true)!;
            ToolLoadoutPresentation? presentation; ToolInvoker? late;
            _activationPreparation.Value = true;
            try
            {
                presentation = replacement.PrepareActiveLoadout(normalized, cancellationToken);
                late = replacement.Resolve(configuration.Model, WithUnrecordedLoadout(context.LlmMessages,
                    configuration.Tools.Select(tool => tool.Name).ToImmutableArray(), cancellationToken).Add(delta), configuration.ThinkingLevel, cancellationToken: cancellationToken,
                    prepareLoadout: false, preparedLoadout: presentation).Invoker;
            }
            finally { _activationPreparation.Value = false; }
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!RunCatalogAdmissibleLocked()) return null;
                // Another catalog change or activation came first: prepare again on top of it.
                if (!ReferenceEquals(_registry, expected) || _activationEpoch != epoch || !ReferenceEquals(_configuration, configuration)) continue;
                prepared.CommitPreparedRegistry();
                _registry = replacement;
                _activationEpoch = checked(epoch + 1);
                // The transcript and the agent keep the loadout the previous registry resolved until the replacement is recorded.
                if (_pendingActivation is not { ReplaceDeclarations: true }) _recordedRegistry = expected;
                _pendingActivation = new(_activationEpoch, normalized, presentation, ReplaceDeclarations: true);
                _lateNestedInvoker = late;
            }
            return new(Snapshot, replacement);
        }
    }

    private ToolInvoker? _lateNestedInvoker;
    /// <summary>The registry the recorded loadout and the agent's configuration were resolved with, while a catalog published
    /// during the run awaits its record.</summary>
    private SessionRuntimeRegistry? _recordedRegistry;

    /// <summary>The registry that validates the recorded loadout: the previous one while a run's catalog change awaits its record.</summary>
    private SessionRuntimeRegistry LoadoutRegistry() =>
        _pendingActivation is { ReplaceDeclarations: true } && _recordedRegistry is { } recorded ? recorded : _registry!;

    /// <summary>A run that settles without another request records the catalog it took during the run, so the session is idle
    /// with a transcript its registry resolves. Skipped (the next request records it) when input is queued.</summary>
    private async Task RecordRunCatalogAsync(TaskCompletionSource operation)
    {
        PendingActivation? pending;
        lock (_gate)
        {
            pending = _pendingActivation;
            if (pending is not { ReplaceDeclarations: true } || !ReferenceEquals(_active, operation) || _fault is not null || _disposed) return;
            var state = _agent.Snapshot;
            if (state.IsRunning || !state.PendingInputs.IsEmpty || state.SteeringCount != 0 || state.FollowUpCount != 0) return;
        }
        await _commits.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        var writeAdmitted = false;
        try
        {
            SessionRuntimeRegistry registry; Sessions.Context.SessionContextProjection context; Sessions.Storage.SessionLogStoreSnapshot log;
            AgentConfiguration configuration; long epoch;
            lock (_gate)
            {
                if (!ReferenceEquals(_pendingActivation, pending) || !ReferenceEquals(_active, operation) || _fault is not null || _disposed) return;
                registry = _registry!; context = _context; log = _acknowledgedLog; configuration = _configuration; epoch = _activationEpoch;
            }
            var delta = registry.CreateActivationMessage(pending.Names, RecordedActiveToolNames(context, default), _clock(), default,
                replaceDeclarations: true)!;
            var entry = Record(_codec, "message", Identity(_nextEntryId, log.Header.Id, log.Entries), context.LeafId, _clock,
                writer => { writer.WritePropertyName("message"); writer.WriteRawValue(delta.WireBody!.Value.GetRawText()); });
            var prospective = _projector.Project(log.Entries.Add(entry), entry.Id);
            AgentConfiguration verified;
            _activationPreparation.Value = true;
            try
            {
                verified = registry.Resolve(configuration.Model, prospective.LlmMessages, configuration.ThinkingLevel, prepareLoadout: false,
                    preparedLoadout: pending.Presentation).Configuration;
            }
            finally { _activationPreparation.Value = false; }
            ValidateRuntimeContext(prospective, verified, _toleratedSelection);
            lock (_gate)
            {
                if (!ReferenceEquals(_pendingActivation, pending) || _activationEpoch != epoch || !ReferenceEquals(_context, context) ||
                    !ReferenceEquals(_acknowledgedLog, log) || !ReferenceEquals(_registry, registry) || _fault is not null || _disposed) return;
                _activationPublishing = true; writeAdmitted = true;
            }
            var acknowledged = await _store.AppendAsync([entry], CancellationToken.None).ConfigureAwait(false);
            if (!acknowledged.CheckpointAcknowledged) throw Error(PersistentAgentSessionFailure.InvalidCommit);
            lock (_gate)
            {
                _agent.ConfigureAndReplaceMessages(RecoveryConfiguration(verified), SessionContextProjector.AgentMessages(prospective));
                _configuration = verified; _context = prospective; _acknowledgedLog = acknowledged.Snapshot;
                _pendingActivation = null; _lateNestedInvoker = null; _recordedRegistry = null; _unrecordedLoadout = false;
            }
        }
        catch (Sessions.Storage.SessionLogStoreException storage)
        {
            var fault = new PersistentAgentSessionFault(PersistentAgentSessionFailure.AppendFailed, storage.Failure,
                storage.MayHaveWritten, storage.DurableFlushCompleted);
            if (storage.MayHaveWritten || _store.IsPoisoned) lock (_gate) _fault ??= fault;
            throw new PersistentAgentSessionException(fault);
        }
        catch { if (writeAdmitted) lock (_gate) _fault ??= new(PersistentAgentSessionFailure.InvalidCommit); throw; }
        finally { if (writeAdmitted) lock (_gate) _activationPublishing = false; _commits.Release(); }
    }

    /// <summary>While a catalog published during the run awaits its boundary, nested calls (codemode scripts) reach its tools
    /// through its invoker, as the original's tools are callable once registered.</summary>
    private ToolInvoker? LateNestedInvoker()
    {
        lock (_gate) return !_disposed && !_retired && _pendingActivation is { ReplaceDeclarations: true } ? _lateNestedInvoker : null;
    }

    /// <summary>Tools a nested call reaches that the running request's loadout does not know yet: those of a catalog
    /// published during the run (see <see cref="TryPublishToolCatalogDuringRunAsync"/>). Empty otherwise.</summary>
    public ImmutableArray<string> GetLateNestedToolNames() => LateNestedInvoker()?.CallableToolNames ?? [];


    /// <summary>A run in its provider phase, with no other selected-state transaction in flight.</summary>
    private bool RunCatalogAdmissibleLocked() =>
        !_disposed && !_admissionStopped && _fault is null && !_retired && !_replacing && _registry is not null &&
        _active is not null && _operationPhase == SessionOperationPhase.Provider && !_closing.IsCancellationRequested &&
        !_configuring && !_compacting && !_editingContext && !_appendingExtensionEntry && !_activationPublishing && _catalogPublication is null;
}
