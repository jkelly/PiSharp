using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Storage;

namespace PiSharp.CodingAgent;

/// <summary>Logical selection only, not a durable checkpoint acknowledgment.</summary>
public sealed record SessionToolActivationSelection(long Revision, ImmutableArray<string> Names);

public sealed partial class PersistentAgentSession
{
    private ImmutableArray<string> RecordedActiveToolNames(SessionContextProjection context, CancellationToken token)
        => new SessionSystemReplay().Replay(context.Messages, token).Tools
            .Select(tool => tool.Value.GetProperty("name").GetString()!).ToImmutableArray();
    internal PiSharp.CodingAgent.ToolSelection.AllowedToolSelection? LifetimeToolSelection => _registry?.LifetimeToolSelection;
    private long _activationEpoch;
    private readonly AsyncLocal<bool> _activationPreparation = new();
    private PendingActivation? _pendingActivation;
    private bool _activationPublishing;
    private sealed record PendingActivation(long Epoch, ImmutableArray<string> Names, ToolLoadoutPresentation? Presentation);

    public SessionToolActivationSelection GetToolActivationSelection()
    {
        lock (_gate) { ThrowAvailable(); return new(_activationEpoch, _pendingActivation?.Names ??
            _configuration.Tools.Select(tool => tool.Name).ToImmutableArray()); }
    }

    /// <summary>Accept a bounded logical selection. The next request publishes its matching durable loadout and scheduler.</summary>
    public SessionToolActivationSelection ScheduleToolActivation(ImmutableArray<string> names, CancellationToken cancellationToken = default)
    {
        if (_activationPreparation.Value || _inLoadoutDiagnosticDrain.Value) throw new InvalidOperationException("Loadout preparation or diagnostic reporting cannot reenter activation.");
        using var preparation = ReserveSynchronousLoadoutWork();
        SessionRuntimeRegistry registry; AgentConfiguration configuration; SessionContextProjection context;
        long epoch; ImmutableArray<string> previous;
        lock (_gate)
        {
            ThrowActivationAvailable(); cancellationToken.ThrowIfCancellationRequested();
            registry = _registry ?? throw Error(PersistentAgentSessionFailure.InvalidConfiguration);
            configuration = _configuration; context = _context; epoch = _activationEpoch;
            previous = _pendingActivation?.Names ?? configuration.Tools.Select(tool => tool.Name).ToImmutableArray();
        }
        var normalized = registry.NormalizeActiveTools(names, cancellationToken);
        if (previous.SequenceEqual(normalized, StringComparer.Ordinal))
        { lock (_gate) { ThrowActivationAvailable(); cancellationToken.ThrowIfCancellationRequested();
            if (_activationEpoch != epoch) throw new InvalidOperationException("Activation selection changed during preparation.");
            SelectPendingToolsLocked(previous, normalized); return new(epoch, normalized); } }
        if (normalized.Length > (_agentOptions?.MaximumTools ?? 128)) throw Error(PersistentAgentSessionFailure.InvalidConfiguration);
        var nextEpoch = checked(epoch + 1);
        var delta = registry.CreateActivationMessage(normalized, configuration.Tools.Select(tool => tool.Name).ToImmutableArray(), 0, cancellationToken);
        ToolLoadoutPresentation? presentation;
        _activationPreparation.Value = true;
        try
        {
            presentation = delta is null ? null : registry.PrepareActiveLoadout(normalized, cancellationToken);
            if (delta is not null) _ = registry.Resolve(configuration.Model, context.LlmMessages.Add(delta), configuration.ThinkingLevel, cancellationToken: cancellationToken,
                prepareLoadout: false, preparedLoadout: presentation);
        }
        finally { _activationPreparation.Value = false; }
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowActivationAvailable(); cancellationToken.ThrowIfCancellationRequested();
            if (_activationEpoch != epoch || !ReferenceEquals(_configuration, configuration))
                throw new InvalidOperationException("Activation selection changed during preparation.");
            _activationEpoch = nextEpoch;
            _pendingActivation = delta is null ? null : new(nextEpoch, normalized, presentation);
            SelectPendingToolsLocked(previous, normalized);
            return new(nextEpoch, normalized);
        }
    }

    private void ThrowActivationAvailable()
    {
        ThrowAvailable(); if (_active is null) ThrowUserBashMutationLocked(); _closing.Token.ThrowIfCancellationRequested();
        if (_configuring || _compacting || _editingContext || _appendingExtensionEntry || _activationPublishing)
            throw new InvalidOperationException("Activation cannot race a selected-state transaction.");
        if (_registry?.RequiresInvocationOwner == true && _invocationLifetime is null)
            throw new InvalidOperationException("Callback activation requires the actual invocation owner.");
    }

    // Prevalidate before an external state publication. The returned action has no callbacks, allocation or checked arithmetic.
    private Action PrepareActivationRestoration(AgentConfiguration configuration)
    {
        var next = checked(_activationEpoch + 1);
        var names = configuration.Tools.Select(tool => tool.Name).ToImmutableArray();
        if (_registry is { } registry) _ = registry.NormalizeActiveTools(names, default);
        return () => { _activationEpoch = next; _pendingActivation = null; };
    }

    private async ValueTask<AgentRequestPreparation?> PrepareActivationRequestAsync(AgentRequestBoundary boundary, CancellationToken token)
    {
        await DrainLoadoutDiagnosticsAsync(token).ConfigureAwait(false);
        PendingActivation? pending; SessionLogStoreSnapshot log; SessionContextProjection context;
        TaskCompletionSource operation; long epoch; SessionRuntimeRegistry registry; object? priorPromptRevision;
        lock (_gate)
        {
            ThrowAvailable(); token.ThrowIfCancellationRequested();
            pending = _pendingActivation;
            if (pending is null && _registry?.HasPromptSectionPreparation != true) return null;
            operation = _active ?? throw new InvalidOperationException("Activation publication requires an owned run.");
            registry = _registry!; log = _acknowledgedLog; context = _context; epoch = _activationEpoch;
            priorPromptRevision = _acknowledgedPromptRevision;
        }
        var names = pending?.Names ?? _configuration.Tools.Select(tool => tool.Name).ToImmutableArray();
        var delta = pending is null ? null : registry.CreateActivationMessage(names,
            _configuration.Tools.Select(tool => tool.Name).ToImmutableArray(), _clock(), token);
        SessionPromptSectionPreparation? promptPreparation;
        _activationPreparation.Value = true;
        try { (delta, promptPreparation) = registry.PreparePromptSectionMessage(names, context.Messages, delta, _clock(), token, pending?.Presentation); }
        finally { _activationPreparation.Value = false; }
        if (delta is null) { promptPreparation?.ValidateSource(); token.ThrowIfCancellationRequested(); return null; }
        var entry = Record(_codec, "message", Identity(_nextEntryId, log.Header.Id, log.Entries), context.LeafId, _clock,
            writer => { writer.WritePropertyName("message"); writer.WriteRawValue(delta.WireBody.Value.GetRawText()); });
        var prospective = _projector.Project(log.Entries.Add(entry), entry.Id, token);
        var verified = registry.Resolve(_configuration.Model, prospective.LlmMessages, _configuration.ThinkingLevel, cancellationToken: token, prepareLoadout: false,
            preparedLoadout: pending?.Presentation);
        ValidateRuntimeContext(prospective, verified.Configuration);
        if (!verified.Configuration.Tools.Select(tool => tool.Name).SequenceEqual(names, StringComparer.Ordinal))
            throw Error(PersistentAgentSessionFailure.InvalidConfiguration);
        var projectedInputs = boundary.PendingInputs.Select(message =>
        {
            if (message.Role != "system" || !message.WireBody.Value.TryGetProperty("toolsAdded", out _) &&
                !message.WireBody.Value.TryGetProperty("toolsRemoved", out _)) return message;
            var fields = message.WireBody.Value.EnumerateObject().Where(property => property.Name is not ("toolsAdded" or "toolsRemoved"))
                .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            return new TranscriptEntry("system", JsonData.Parse(JsonSerializer.Serialize(fields)));
        }).ToImmutableArray();
        return new(RecoveryConfiguration(verified.Configuration), [delta], Publish)
            { ProjectedPendingInputs = projectedInputs };

        async ValueTask Publish(Action publishAgent, CancellationToken cancellation)
        {
            await _commits.WaitAsync(cancellation).ConfigureAwait(false);
            var writeAdmitted = false;
            try
            {
                promptPreparation?.ValidateSource();
                lock (_gate)
                {
                    ThrowAvailable(); cancellation.ThrowIfCancellationRequested();
                    if (_activationEpoch != epoch || !ReferenceEquals(_pendingActivation, pending) || !ReferenceEquals(_active, operation) ||
                        !ReferenceEquals(_context, context) || !ReferenceEquals(_acknowledgedLog, log) ||
                        !ReferenceEquals(_registry, registry) || !ReferenceEquals(_acknowledgedPromptRevision, priorPromptRevision))
                        throw new AgentRequestBoundaryStaleException();
                    _activationPublishing = true; writeAdmitted = true;
                }
                // Once admitted, finish the original append and retain its actual acknowledgment even if the run aborts.
                var acknowledged = await _store.AppendAsync([entry], CancellationToken.None).ConfigureAwait(false);
                if (!acknowledged.CheckpointAcknowledged) throw Error(PersistentAgentSessionFailure.InvalidCommit);
                lock (_gate)
                {
                    // No trusted callbacks/output/cancellation checks between acknowledgment and matching publication.
                    _configuration = verified.Configuration; _context = prospective; _acknowledgedLog = acknowledged.Snapshot;
                    _acknowledgedPromptRevision = promptPreparation?.Revision ?? _acknowledgedPromptRevision;
                    if (_activationEpoch == epoch && ReferenceEquals(_pendingActivation, pending)) _pendingActivation = null;
                    publishAgent();
                }
            }
            catch (SessionLogStoreException storage)
            {
                var fault = new PersistentAgentSessionFault(PersistentAgentSessionFailure.AppendFailed, storage.Failure,
                    storage.MayHaveWritten, storage.DurableFlushCompleted);
                if (storage.MayHaveWritten || _store.IsPoisoned) lock (_gate) _fault ??= fault;
                throw new PersistentAgentSessionException(fault);
            }
            catch { if (writeAdmitted) lock (_gate) _fault ??= new(PersistentAgentSessionFailure.InvalidCommit); throw; }
            finally { if (writeAdmitted) lock (_gate) _activationPublishing = false; _commits.Release(); }
        }
    }
}
