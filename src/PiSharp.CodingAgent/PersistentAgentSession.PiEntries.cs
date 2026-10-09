// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/session-manager.ts (appendCustomEntry,
// appendLabelChange, appendSessionInfo) as the extension API reaches them (pi.appendEntry, pi.setLabel, pi.setSessionName): an entry
// at the current leaf, at any time, idle or while a run is processing.
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Sessions.Storage;

namespace PiSharp.CodingAgent;

public sealed partial class PersistentAgentSession
{
    /// <summary>Source appendCustomEntry: a raw <c>custom</c> entry (<c>{ customType, data }</c>; undefined data is omitted).</summary>
    public Task<SessionEntryAppendReceipt> AppendCustomEntryAsync(string customType, JsonData? data, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(customType);
        return AppendLeafEntryAsync("custom", writer =>
        {
            writer.WriteString("customType", customType);
            if (data is not null) { writer.WritePropertyName("data"); writer.WriteRawValue(data.ToString()); }
        }, cancellationToken);
    }

    /// <summary>Source appendLabelChange: a <c>label</c> entry for an existing entry; a null or empty label clears it.</summary>
    public Task<SessionEntryAppendReceipt> AppendLabelChangeAsync(string targetId, string? label, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetId);
        lock (_gate)
        {
            ThrowAvailable();
            if (!_acknowledgedLog.Entries.Any(entry => entry.Id == targetId)) throw new InvalidOperationException($"Entry {targetId} not found");
        }
        return AppendLeafEntryAsync("label", writer =>
        {
            writer.WriteString("targetId", targetId);
            if (label is not null) writer.WriteString("label", label);
        }, cancellationToken);
    }

    /// <summary>Source appendSessionInfo while a run is processing (the idle form is <see cref="SetSessionNameAsync"/>).</summary>
    public Task<SessionEntryAppendReceipt> AppendSessionInfoAsync(string name, CancellationToken cancellationToken = default) =>
        AppendLeafEntryAsync("session_info", writer => writer.WriteString("name", name), cancellationToken);

    private async Task<SessionEntryAppendReceipt> AppendLeafEntryAsync(string type, Action<Utf8JsonWriter> write, CancellationToken cancellationToken)
    {
        lock (_gate) ThrowAvailable();
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
            var entry = Record(_codec, type, Identity(_nextEntryId, log.Header.Id, log.Entries), previous.LeafId, _clock, write);
            var next = _projector.Project(log.Entries.Add(entry), entry.Id);
            writeAdmitted = true;
            var acknowledged = await _store.AppendAsync([entry], CancellationToken.None).ConfigureAwait(false);
            if (!acknowledged.CheckpointAcknowledged) throw Error(PersistentAgentSessionFailure.InvalidCommit);
            lock (_gate) { _acknowledgedLog = acknowledged.Snapshot; _context = next; }
            writeAdmitted = false; await PublishAppendedAsync(acknowledged.Entries).ConfigureAwait(false);
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
