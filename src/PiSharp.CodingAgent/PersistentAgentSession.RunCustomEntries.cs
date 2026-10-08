// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/session-manager.ts (appendCustomEntry) as
// extensions call it through pi.appendEntry while a tool runs (extensions/codemode/execute.ts appends codemode-store entries).
using PiSharp.Contracts;
using PiSharp.Sessions.Storage;

namespace PiSharp.CodingAgent;

public sealed partial class PersistentAgentSession
{
    /// <summary>Appends a raw <c>custom</c> entry (<c>{ customType, data }</c>) at the current leaf while a run is processing,
    /// through the same commit order as the run's messages, so it lands between the assistant message that called the tool
    /// and the tool's result and is on every branch that continues from there. Custom entries do not reach the model.</summary>
    public async Task<SessionEntryAppendReceipt> AppendRunCustomEntryAsync(string customType, JsonData data, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(customType); ArgumentNullException.ThrowIfNull(data);
        lock (_gate)
        {
            ThrowAvailable();
            if (_active is null) throw new InvalidOperationException("Run custom entries require a processing run.");
        }
        await _commits.WaitAsync(cancellationToken).ConfigureAwait(false);
        var writeAdmitted = false;
        try
        {
            Sessions.Context.SessionContextProjection previous;
            lock (_gate)
            {
                if (_fault is not null) throw Error(PersistentAgentSessionFailure.Faulted);
                previous = _context;
            }
            var log = _store.Snapshot;
            var entry = Record(_codec, "custom", Identity(_nextEntryId, log.Header.Id, log.Entries), previous.LeafId, _clock, writer =>
            {
                writer.WriteString("customType", customType);
                writer.WritePropertyName("data"); writer.WriteRawValue(data.ToString());
            });
            var next = _projector.Project(log.Entries.Add(entry), entry.Id);
            writeAdmitted = true;
            var acknowledged = await _store.AppendAsync([entry], CancellationToken.None).ConfigureAwait(false);
            if (!acknowledged.CheckpointAcknowledged) throw Error(PersistentAgentSessionFailure.InvalidCommit);
            lock (_gate) { _acknowledgedLog = acknowledged.Snapshot; _context = next; }
            return new(acknowledged.Entries.Single());
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
    }
}

/// <summary>The acknowledged custom entry.</summary>
public sealed record SessionEntryAppendReceipt(PiSharp.Sessions.Serialization.SessionEntry Entry);
