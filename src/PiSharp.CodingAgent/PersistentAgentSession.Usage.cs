// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/session-manager.ts (appendUsage) and
// core/cache-warmer.ts (a successful refresh persists its usage; agent-session.ts emits entry_appended for it).
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

namespace PiSharp.CodingAgent;

public sealed partial class PersistentAgentSession
{
    /// <summary>Source appendUsage: a <c>usage</c> entry (<c>{kind, provider, model, usage, note?}</c>) at the current leaf, outside
    /// the message flow, during a run or while idle. Usage entries never reach the model. Publishes entry_appended.</summary>
    public async Task<SessionEntry> AppendUsageEntryAsync(string kind, string provider, string model, TokenUsage usage, string? note = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind); ArgumentNullException.ThrowIfNull(provider); ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(usage);
        lock (_gate) ThrowAvailable();
        var usageWire = PiWireJson.WriteMessage(new("usage", "usage", "usage", 0, [], usage, StopReason.Stop)).Value.GetProperty("usage").GetRawText();
        await _commits.WaitAsync(cancellationToken).ConfigureAwait(false);
        var writeAdmitted = false; SessionEntry committed;
        try
        {
            Sessions.Context.SessionContextProjection previous;
            lock (_gate) { if (_fault is not null) throw Error(PersistentAgentSessionFailure.Faulted); ThrowAvailable(); previous = _context; }
            var log = _store.Snapshot;
            var entry = Record(_codec, "usage", Identity(_nextEntryId, log.Header.Id, log.Entries), previous.LeafId, _clock, writer =>
            {
                writer.WriteString("kind", kind); writer.WriteString("provider", provider); writer.WriteString("model", model);
                writer.WritePropertyName("usage"); writer.WriteRawValue(usageWire);
                if (!string.IsNullOrEmpty(note)) writer.WriteString("note", note);
            }, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
            var next = _projector.Project(log.Entries.Add(entry), entry.Id);
            writeAdmitted = true;
            var acknowledged = await _store.AppendAsync([entry], CancellationToken.None).ConfigureAwait(false);
            if (!acknowledged.CheckpointAcknowledged) throw Error(PersistentAgentSessionFailure.InvalidCommit);
            lock (_gate) { _acknowledgedLog = acknowledged.Snapshot; _context = next; }
            committed = acknowledged.Entries.Single();
        }
        catch (SessionLogStoreException storage)
        {
            var fault = new PersistentAgentSessionFault(PersistentAgentSessionFailure.AppendFailed, storage.Failure,
                storage.MayHaveWritten, storage.DurableFlushCompleted);
            if (storage.MayHaveWritten || _store.IsPoisoned) lock (_gate) _fault ??= fault;
            throw new PersistentAgentSessionException(fault);
        }
        catch (Exception) when (writeAdmitted)
        {
            lock (_gate) _fault ??= new(PersistentAgentSessionFailure.InvalidCommit);
            throw;
        }
        finally { _commits.Release(); }
        await PublishAppendedAsync([committed]).ConfigureAwait(false);
        return committed;
    }
}
