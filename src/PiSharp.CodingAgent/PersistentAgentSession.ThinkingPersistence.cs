namespace PiSharp.CodingAgent;

public sealed partial class PersistentAgentSession
{
    // Upstream 7860ff52: tree navigation preserves live thinking. Record that effective
    // (already model-clamped) selection on the destination branch before its next prompt.
    private async Task RecordLiveThinkingAsync(CancellationToken token)
    {
        await _commits.WaitAsync(token).ConfigureAwait(false);
        var writeAdmitted = false;
        try
        {
            Sessions.Context.SessionContextProjection context;
            string level;
            lock (_gate) { context = _context; level = _configuration.ThinkingLevel; }
            if (PiSharp.AI.ThinkingLevels.Clamp(GetSupportedThinkingLevels(), context.ThinkingLevel) == level) return;
            var log = _store.Snapshot;
            var entry = Record(_codec, "thinking_level_change", Identity(_nextEntryId, log.Header.Id, log.Entries),
                context.LeafId, _clock, writer => writer.WriteString("thinkingLevel", level));
            var next = _projector.Project(log.Entries.Add(entry), entry.Id);
            ValidateRuntimeContext(next, _configuration, _toleratedSelection);
            token.ThrowIfCancellationRequested();
            writeAdmitted = true;
            var acknowledged = await _store.AppendAsync([entry], CancellationToken.None).ConfigureAwait(false);
            if (!acknowledged.CheckpointAcknowledged) throw Error(PersistentAgentSessionFailure.InvalidCommit);
            lock (_gate)
            {
                _acknowledgedLog = acknowledged.Snapshot;
                _context = next;
                _toleratedThinking = null;
            }
            writeAdmitted = false;
            await PublishAppendedAsync(acknowledged.Entries).ConfigureAwait(false);
        }
        catch (Sessions.Storage.SessionLogStoreException storage)
        {
            var fault = new PersistentAgentSessionFault(PersistentAgentSessionFailure.AppendFailed,
                storage.Failure, storage.MayHaveWritten, storage.DurableFlushCompleted);
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
