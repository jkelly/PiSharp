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
            bool busy;
            lock (_gate)
            {
                busy = RunCatalogBusyLocked();
                if (!busy && !RunCatalogAdmissibleLocked()) return null;
                expected = _registry!; epoch = _activationEpoch; configuration = _configuration; context = _context;
                names = _pendingActivation?.Names ?? configuration.Tools.Select(tool => tool.Name).ToImmutableArray();
            }
            if (busy) { await WaitForRunCatalogAsync(cancellationToken).ConfigureAwait(false); continue; }
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
                busy = RunCatalogBusyLocked();
                if (!busy && !RunCatalogAdmissibleLocked()) return null;
            }
            if (busy) { await WaitForRunCatalogAsync(cancellationToken).ConfigureAwait(false); continue; }
            lock (_gate)
            {
                if (!RunCatalogAdmissibleLocked()) continue;
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

    /// <summary>A run that settles without another request keeps the catalog it took during the run as the session's loadout, in
    /// memory like an idle catalog change: the next request records it (source _refreshToolRegistry; declareToolChanges). The
    /// transcript keeps the previous registry's declarations meanwhile (<see cref="_unrecordedLoadout"/>). Skipped (the next request
    /// records it) when input is queued.</summary>
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
        try
        {
            SessionRuntimeRegistry registry; Sessions.Context.SessionContextProjection context; AgentConfiguration configuration; long epoch;
            lock (_gate)
            {
                if (!ReferenceEquals(_pendingActivation, pending) || !ReferenceEquals(_active, operation) || _fault is not null || _disposed) return;
                registry = _registry!; context = _context; configuration = _configuration; epoch = _activationEpoch;
            }
            AgentConfiguration verified;
            _activationPreparation.Value = true;
            try
            {
                verified = registry.Resolve(configuration.Model, WithLoadoutRecord(registry, context.LlmMessages, pending.Names, default),
                    configuration.ThinkingLevel, prepareLoadout: false, preparedLoadout: pending.Presentation, activeOrder: pending.Names).Configuration;
            }
            finally { _activationPreparation.Value = false; }
            ValidateRuntimeContext(context, verified, _toleratedSelection, _toleratedThinking);
            lock (_gate)
            {
                if (!ReferenceEquals(_pendingActivation, pending) || _activationEpoch != epoch || !ReferenceEquals(_context, context) ||
                    !ReferenceEquals(_registry, registry) || _fault is not null || _disposed) return;
                _agent.ConfigureAndReplaceMessages(RecoveryConfiguration(verified), SessionContextProjector.AgentMessages(context));
                _configuration = verified;
                _pendingActivation = null; _lateNestedInvoker = null; _recordedRegistry = null; _unrecordedLoadout = true;
            }
        }
        finally { _commits.Release(); }
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


    /// <summary>A run in its provider phase while another selected-state transaction is in flight (the run's request boundary
    /// recording its loadout, a compaction or an appended entry): the catalog waits for it instead of falling back to the idle
    /// boundary, which a tool of the same run (tool_search waiting for its servers) may be waiting on.</summary>
    private bool RunCatalogBusyLocked() =>
        !_disposed && !_admissionStopped && _fault is null && !_retired && !_replacing && _registry is not null &&
        _active is not null && _operationPhase == SessionOperationPhase.Provider && !_closing.IsCancellationRequested &&
        (_configuring || _compacting || _editingContext || _appendingExtensionEntry || _activationPublishing || _catalogPublication is not null);

    /// <summary>Let the in-flight transaction finish: they hold the commit gate while they write.</summary>
    private async Task WaitForRunCatalogAsync(CancellationToken token)
    {
        await _commits.WaitAsync(token).ConfigureAwait(false);
        _commits.Release();
        await Task.Delay(1, token).ConfigureAwait(false);
    }

    /// <summary>A run in its provider phase, with no other selected-state transaction in flight.</summary>
    private bool RunCatalogAdmissibleLocked() =>
        !_disposed && !_admissionStopped && _fault is null && !_retired && !_replacing && _registry is not null &&
        _active is not null && _operationPhase == SessionOperationPhase.Provider && !_closing.IsCancellationRequested &&
        !_configuring && !_compacting && !_editingContext && !_appendingExtensionEntry && !_activationPublishing && _catalogPublication is null;
}
