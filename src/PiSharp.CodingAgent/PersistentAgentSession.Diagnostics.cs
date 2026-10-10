using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

namespace PiSharp.CodingAgent;

public sealed partial class PersistentAgentSession
{
    private string? _lastAcknowledgedAssistantId;

    /// <summary>Explicit selected raw-ancestry diagnostics, retaining omitted assistants and labelled legacy observations.</summary>
    public NativeSessionDiagnosticView GetNativeDiagnostics(CancellationToken cancellationToken=default)
    {
        SessionContextProjection context;lock(_gate)context=_context;
        return NativeSessionDiagnosticProjector.Project(context,cancellationToken);
    }

    private async ValueTask CommitNativeDiagnosticAsync(AgentLoopTurnEnded observation)
    {
        var chat=observation.Turn.Result.Chat;
        var primary=chat.NativeDiagnostic??chat.Failure?.NativeDiagnostic;var cleanup=chat.NativeCleanupDiagnostic;
        if(primary is null&&cleanup is null)return;
        await _commits.WaitAsync().ConfigureAwait(false);
        try
        {
            SessionContextProjection previous;string? assistantId;long generation;
            lock(_gate)
            {
                if(_fault is not null)throw Error(PersistentAgentSessionFailure.Faulted);
                if(_retired)throw Error(PersistentAgentSessionFailure.SessionReplaced);
                previous=_context;assistantId=_lastAcknowledgedAssistantId;generation=_operationGeneration;
            }
            if(chat.NativeDiagnostic is not null&&chat.Failure?.NativeDiagnostic is not null&&chat.NativeDiagnostic!=chat.Failure.NativeDiagnostic)
                throw Error(PersistentAgentSessionFailure.InvalidCommit);
            var assistant=previous.Ancestry.LastOrDefault(e=>e.Id==assistantId);
            if(assistant is null||assistant.Kind!=SessionEntryKind.Message||
                !JsonUtf16.DeepEquals(assistant.WireBody.Value.GetProperty("message"),PersistedWire(PiWireJson.WriteMessage(chat.Message)).Value))
                throw Error(PersistentAgentSessionFailure.InvalidCommit);
            var data=NativeSessionDiagnosticProjector.RecordData(assistant.Id,generation,primary,cleanup);
            var log=_store.Snapshot;
            var entry=Record(_codec,"custom",Identity(_nextEntryId,log.Header.Id,log.Entries),previous.LeafId,_clock,writer=>
            {
                writer.WriteString("customType",NativeSessionDiagnosticProjector.CustomType);
                writer.WritePropertyName("data");writer.WriteRawValue(data.Value.GetRawText());
            });
            var nextContext=_projector.Project(log.Entries.Add(entry),entry.Id);
            if(previous.LlmMessages.Length!=nextContext.LlmMessages.Length||!previous.LlmMessages.Zip(nextContext.LlmMessages).All(pair=>
                pair.First.Role==pair.Second.Role&&pair.First.WireBody.ToString()==pair.Second.WireBody.ToString()))throw Error(PersistentAgentSessionFailure.InvalidCommit);
            var acknowledged=await _store.AppendAsync([entry],CancellationToken.None).ConfigureAwait(false);
            if(!acknowledged.CheckpointAcknowledged)throw Error(PersistentAgentSessionFailure.InvalidCommit);
            lock(_gate){_acknowledgedLog=acknowledged.Snapshot;_context=nextContext;}
        }
        catch(Exception error)
        {
            var fault=error is SessionLogStoreException storage?
                new PersistentAgentSessionFault(PersistentAgentSessionFailure.AppendFailed,storage.Failure,storage.MayHaveWritten,storage.DurableFlushCompleted):
                new PersistentAgentSessionFault(PersistentAgentSessionFailure.InvalidCommit);
            lock(_gate)_fault??=fault;
            throw new PersistentAgentSessionException(fault);
        }
        finally{_commits.Release();}
    }
}
