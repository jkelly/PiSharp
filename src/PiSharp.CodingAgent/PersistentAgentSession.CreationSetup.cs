using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

namespace PiSharp.CodingAgent;

public sealed partial class PersistentAgentSession
{
    private SessionCreationSetupWriter CreateSetupWriter(ReplacementReservation reservation, CancellationToken token)
    {
        var originals = new SessionBoundaryOriginals();
        (SessionLogStoreSnapshot, SessionContextProjection) Read()
        { lock (_gate) { reservation.ValidateCatalogAuthority(this); return (_acknowledgedLog, _context); } }
        void Select(string? leaf)
        {
            SessionLogStoreSnapshot log; SessionContextProjection previous;
            lock (_gate) { reservation.ValidateCatalogAuthority(this); if (_setupAppending) throw new InvalidOperationException("Setup selection cannot overlap an append."); log = _acknowledgedLog; previous = _context; }
            var projected = _projector.Project(log.Entries, leaf, token);
            var configuration = _registry?.Resolve(WithUnrecordedLoadout(projected, _configuration.Tools.Select(tool => tool.Name).ToImmutableArray(), token),
                _configuration.Model, token).Configuration ?? _configuration;
            ValidateRuntimeContext(projected, configuration, _toleratedSelection, _toleratedThinking);
            lock (_gate)
            {
                reservation.ValidateCatalogAuthority(this);
                if (_setupAppending || !ReferenceEquals(log, _acknowledgedLog) || !ReferenceEquals(previous, _context))
                    throw new InvalidOperationException("Setup selection changed while preparing.");
                _agent.ConfigureAndReplaceMessages(RecoveryConfiguration(configuration), SessionContextProjector.AgentMessages(projected));
                _context = projected; _configuration = configuration;
            }
        }
        return new((draft, supplied) => AppendSetupEntryAsync(reservation, draft, token, supplied, originals), Read, Select, originals);
    }
    private bool _setupAppending;
    private async Task<SessionSetupAppendReceipt> AppendSetupEntryAsync(ReplacementReservation reservation,
        SessionSetupEntryDraft draft, CancellationToken parentToken, CancellationToken supplied, SessionBoundaryOriginals originals)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(parentToken, supplied, _closing.Token);
        var token = linked.Token; await _commits.WaitAsync(token).ConfigureAwait(false);
        var writing = false;
        try
        {
            SessionLogStoreSnapshot log; SessionContextProjection previous;
            lock (_gate) { reservation.ValidateCatalogAuthority(this); _setupAppending = true; log = _acknowledgedLog; previous = _context; }
            if (draft.Type is not ("message" or "thinking_level_change" or "model_change" or "usage" or "custom" or "custom_message" or "session_info" or "label" or "context_edit" or "compaction" or "branch_summary"))
                throw new ArgumentException("Unsupported setup record kind.");
            if (draft.Fields.Value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Setup record fields must be an object.");
            if (draft.Type == "label" && (!draft.Fields.Value.TryGetProperty("targetId", out var targetId) ||
                targetId.ValueKind != JsonValueKind.String || !log.ById.ContainsKey(targetId.GetString()!)))
                throw new ArgumentException("Setup label target must exist in the actual staged log.");
            var entry = Record(_codec, draft.Type, Identity(_nextEntryId, log.Header.Id, log.Entries), previous.LeafId, _clock, writer =>
            {
                foreach (var property in draft.Fields.Value.EnumerateObject())
                {
                    if (property.Name is "type" or "id" or "parentId" or "timestamp") throw new ArgumentException("Setup cannot supply record identity.");
                    writer.WritePropertyName(property.Name); writer.WriteRawValue(property.Value.GetRawText());
                }
            });
            var prospective = _projector.Project(log.Entries.Add(entry), entry.Id, token);
            // A restored loadout that awaits its record (the next request writes it) precedes the setup record.
            var resolved = prospective with { LlmMessages = WithUnrecordedLoadout(prospective.LlmMessages,
                _configuration.Tools.Select(tool => tool.Name).ToImmutableArray(), token, previous.LlmMessages.Length) };
            var configuration = _registry is { } registry
                ? (await SessionLoadoutDiagnosticBoundary.RunAsync(() => registry.Resolve(resolved, _configuration.Model, token, tolerated: _toleratedSelection, thinkingLevel: KeptThinking(_configuration)),
                    () => new ValueTask(reservation.DrainLoadoutDiagnosticsAsync(token))).ConfigureAwait(false)).Configuration
                : _configuration;
            ValidateRuntimeContext(prospective, configuration, _toleratedSelection, _toleratedThinking);
            await using (var probe = new PiSharp.Agent.Agent(configuration, _clock, new NoopSink(), _agentOptions))
                probe.ConfigureAndReplaceMessages(configuration, SessionContextProjector.AgentMessages(prospective));
            token.ThrowIfCancellationRequested(); writing = true;
            var appendOriginal = _store.AppendAsync([entry], token);
            var acknowledged = await originals.Join(appendOriginal, "setup-storage-checkpoint").ConfigureAwait(false);
            if (!acknowledged.CheckpointAcknowledged) throw Error(PersistentAgentSessionFailure.InvalidCommit);
            lock (_gate)
            {
                reservation.ValidateCatalogAuthority(this);
                _agent.ConfigureAndReplaceMessages(RecoveryConfiguration(configuration), SessionContextProjector.AgentMessages(prospective));
                _acknowledgedLog = acknowledged.Snapshot; _context = prospective; _configuration = configuration;
            }
            return new(acknowledged.Entries.Single(), acknowledged, prospective);
        }
        catch { if (writing) lock (_gate) _fault ??= new(PersistentAgentSessionFailure.AppendFailed, MayHaveWritten: true); throw; }
        finally { lock (_gate) _setupAppending = false; _commits.Release(); }
    }
}
