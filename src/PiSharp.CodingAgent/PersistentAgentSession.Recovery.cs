using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Sessions.Compaction;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;
using NativeAgent = PiSharp.Agent.Agent;

namespace PiSharp.CodingAgent;

public enum SessionOperationPhase { Idle, Provider, RecoveryOmission, Compaction, BeforeSettlement, Settlement }
public abstract record SessionOperationEvent(long OperationGeneration);
public sealed record SessionRecoveryStarted(long OperationGeneration, string Reason, bool WillRetry) : SessionOperationEvent(OperationGeneration);
public sealed record SessionRecoveryEnded(long OperationGeneration, string Status, bool WillRetry, string? CheckpointId = null) : SessionOperationEvent(OperationGeneration);
public sealed record SessionOperationSettled(long OperationGeneration, string Status, AgentLoopResult? Result) : SessionOperationEvent(OperationGeneration)
{
    /// <summary>Source agent_settled.aborted: the operation ended because an abort was requested while it ran.</summary>
    public bool Aborted { get; init; }
}
public interface ISessionOperationEventSink
{ ValueTask EmitAsync(SessionOperationEvent observation, CancellationToken cancellationToken); }

public sealed partial class PersistentAgentSession
{
    private double? _recoveryDesiredOutput;
    private SessionOperationPhase _operationPhase;
    private long _operationGeneration;
    private ContextEditCancellation? _runCancellation;
    private Func<CancellationToken, ValueTask>? _beforeSettlement;
    private ImmutableArray<OperationSubscription> _operationSubscriptions = [];
    private sealed record RecoveryDecision(bool Handled, bool Retry, bool Attempted)
    { internal static readonly RecoveryDecision None = new(false,false,false); }
    private sealed record OperationSubscription(ISessionOperationEventSink Sink);
    private sealed class OperationLease(PersistentAgentSession owner, OperationSubscription subscription) : IDisposable
    {
        private PersistentAgentSession? _owner = owner;
        public void Dispose()
        { var current=Interlocked.Exchange(ref _owner,null); if(current is not null)lock(current._gate)current._operationSubscriptions=current._operationSubscriptions.Remove(subscription); }
    }
    /// <summary>Enables one compact-and-retry attempt per public run. The original model output limit
    /// must be supplied before context clamping; compact configuration supplies the context window.</summary>
    public void ConfigureAutomaticRecovery(double? originalDesiredMaxOutput,
        Func<CancellationToken, ValueTask>? beforeSettlement = null)
    {
        if(originalDesiredMaxOutput is { } output&&(!double.IsFinite(output)||output<=0))throw new ArgumentOutOfRangeException(nameof(originalDesiredMaxOutput));
        lock(_gate)
        {
            ThrowAvailable();ThrowInputMutation();
            if(_active is not null||_inputSubmission is not null)throw new InvalidOperationException("Recovery configuration requires an idle session.");
            if(originalDesiredMaxOutput is not null&&_automaticCompaction is null)throw new InvalidOperationException("Recovery requires configured automatic compaction.");
            _recoveryDesiredOutput=originalDesiredMaxOutput;_beforeSettlement=beforeSettlement;
            _agent.ConfigureAndReplaceMessages(RecoveryConfiguration(_configuration),SessionContextProjector.AgentMessages(_context));
        }
    }
    /// <summary>Registers awaited coordinator events. AgentLoopEnded remains a lower-level event;
    /// SessionOperationSettled is emitted once after automatic work and boundary listeners finish.</summary>
    public IDisposable SubscribeOperationEvents(ISessionOperationEventSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        // A runtime binding under a replacement reservation subscribes its observers (extension activations bind there too).
        lock(_gate){ThrowBindable();if(_operationSubscriptions.Length>=(_agentOptions?.MaximumSubscribers??128))throw new InvalidOperationException("Session subscriber limit reached.");var item=new OperationSubscription(sink);_operationSubscriptions=_operationSubscriptions.Add(item);return new OperationLease(this,item);}
    }
    private AgentConfiguration RecoveryConfiguration(AgentConfiguration configuration)
    {
        var original=configuration.Hooks??new();
        return configuration with { Hooks=original with { PrepareRequestBoundary=PrepareActivationRequestAsync, FinishTurnDecision=async (turn,token)=>
        {
            var decision=original.FinishTurnDecision is null?AgentLoopFinishAction.Default:await original.FinishTurnDecision(turn,token).ConfigureAwait(false);
            double? desired;lock(_gate)desired=_automaticCompaction is null?null:_recoveryDesiredOutput;
            decision=desired is { } max&&SessionRecoveryClassifier.IsRecoverableLength(turn.Result.Chat.Message,max)?AgentLoopFinishAction.End:decision;
            return await TurnBoundaryAsync(turn,decision,token).ConfigureAwait(false);
        } } };
    }
    private async ValueTask EmitOperationAsync(SessionOperationEvent observation)
    {
        ImmutableArray<OperationSubscription> subscriptions;lock(_gate)subscriptions=_operationSubscriptions;
        Exception? failure = null;
        foreach(var item in subscriptions)
            try { await item.Sink.EmitAsync(observation,CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) { failure ??= error; }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private async Task<AgentLoopResult> RunUntilSettlementAsync(Func<CancellationToken,Task<AgentLoopResult>> start,
        TaskCompletionSource idle,CancellationToken token,long operation)
    {
        var allTurns=ImmutableArray.CreateBuilder<AgentLoopTurn>();var attempted=false;
        var result=await start(token).ConfigureAwait(false);var runs=1;
        while(true)
        {
            allTurns.AddRange(result.Turns);
            if (await SettleTurnBoundaryAsync(result, idle, token).ConfigureAwait(false))
            {
                if(runs>=(_agentOptions?.Loop?.MaximumTurns??16)){result=result with { Reason=AgentLoopStopReason.TurnLimit };break;}
                SetOperationPhase(SessionOperationPhase.Provider);result=await _agent.ContinueAsync(token).ConfigureAwait(false);runs++;continue;
            }
            if (!token.IsCancellationRequested && await TryAutomaticRetryAsync(result, idle, token).ConfigureAwait(false))
            { SetOperationPhase(SessionOperationPhase.Provider); result = await _agent.ContinueAsync(token).ConfigureAwait(false); runs++; continue; }
            var recovery=token.IsCancellationRequested?RecoveryDecision.None:await RunAutomaticBoundaryAsync(result,idle,token,operation,attempted).ConfigureAwait(false);
            attempted|=recovery.Attempted;
            if(recovery.Retry)
            { SetOperationPhase(SessionOperationPhase.Provider);result=await _agent.ContinueAsync(token).ConfigureAwait(false);runs++;continue; }
            Func<CancellationToken,ValueTask>? boundary;lock(_gate)boundary=_beforeSettlement;
            SetOperationPhase(SessionOperationPhase.BeforeSettlement);
            if(boundary is not null&&!token.IsCancellationRequested)await boundary(token).ConfigureAwait(false);
            // Source _runBeforeSettleBoundary: agent_before_settle may append entries and ensure one more provider request.
            var settle=token.IsCancellationRequested?null:await RunBeforeSettleBoundaryAsync(idle,token).ConfigureAwait(false);
            if(settle==true)
            {
                if(runs>=(_agentOptions?.Loop?.MaximumTurns??16)){result=result with { Reason=AgentLoopStopReason.TurnLimit };break;}
                SetOperationPhase(SessionOperationPhase.Provider);result=await _agent.ContinueAsync(token).ConfigureAwait(false);runs++;continue;
            }
            // Default sessions retain the accepted explicit-Continue contract for input admitted
            // during low-level end delivery. Recovery and explicit boundary hooks own the wider settlement loop.
            bool ownsContinuation;lock(_gate)ownsContinuation=boundary is not null||_automaticCompaction is not null&&_recoveryDesiredOutput is not null;
            var canContinue=settle is null&&ownsContinuation&&result.Reason==AgentLoopStopReason.Completed&&!token.IsCancellationRequested;
            AgentPendingInputQueueSnapshot queued;
            lock (_gate)
            {
                queued=_agent.GetPendingInputQueueSnapshot();
                if (!canContinue || queued.SteeringMessages.IsEmpty && queued.FollowUpMessages.IsEmpty)
                    _operationPhase=SessionOperationPhase.Settlement;
            }
            if(canContinue&&(!queued.SteeringMessages.IsEmpty||!queued.FollowUpMessages.IsEmpty))
            {
                if(runs>=(_agentOptions?.Loop?.MaximumTurns??16)){result=result with { Reason=AgentLoopStopReason.TurnLimit };break;}
                SetOperationPhase(SessionOperationPhase.Provider);result=await _agent.ContinueAsync(token).ConfigureAwait(false);runs++;continue;
            }
            break;
        }
        return result with { Turns=allTurns.ToImmutable(),Transcript=Snapshot.Context.LlmMessages };
    }
    private void RejectSettlementQueue()
    {
        if (_active is not null && _operationPhase == SessionOperationPhase.Settlement)
            throw new InvalidOperationException("The session operation has closed input admission for final settlement.");
    }
    private void SetOperationPhase(SessionOperationPhase phase){lock(_gate)_operationPhase=phase;}
    private async Task<RecoveryDecision> TryRecoveryAsync(AgentLoopResult result,TaskCompletionSource idle,CancellationToken token,long operation,bool attempted)
    {
        AutomaticCompaction? configured;double? desired;SessionContextProjection context;
        lock(_gate){configured=_automaticCompaction;desired=_recoveryDesiredOutput;context=_context;}
        if(configured is null||desired is null||result.Turns.IsEmpty)return RecoveryDecision.None;
        var turn=result.Turns[^1];var assistant=turn.Result.Chat.Message;var model=_configuration.Model;
        if(assistant.StopReason==StopReason.Aborted||assistant.Model!=model.Id||assistant.Provider!=model.Provider||assistant.Api!=model.Api)return RecoveryDecision.None;
        var wire=PersistedWire(PiWireJson.WriteMessage(assistant)).Value;
        var selected=context.ContextEntries.LastOrDefault(e=>e.Messages.Any(m=>m.Role=="assistant"&&JsonElement.DeepEquals(m.WireBody.Value,wire)));
        if(selected is null)return RecoveryDecision.None;
        var index=context.Ancestry.IndexOf(selected.SourceEntry);
        var later=context.Ancestry.Skip(index+1).ToArray();
        if(later.Any(e=>e.Kind==SessionEntryKind.Compaction))return RecoveryDecision.None;
        var latestCompaction=context.Ancestry.LastOrDefault(e=>e.Kind==SessionEntryKind.Compaction);
        if(latestCompaction is not null&&assistant.Timestamp<=DateTimeOffset.Parse(latestCompaction.WireBody.Value.GetProperty("timestamp").GetString()!,System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds())return RecoveryDecision.None;
        var explicitOverflow=assistant.StopReason==StopReason.Error&&SessionRecoveryClassifier.IsContextOverflow(assistant);
        var overflow=explicitOverflow||!later.Any(e=>e.Kind==SessionEntryKind.ContextEdit)&&SessionRecoveryClassifier.IsContextOverflow(assistant,configured.Request.ContextWindow);
        var length=SessionRecoveryClassifier.IsRecoverableLength(assistant,desired.Value);
        if(!overflow&&!length)return RecoveryDecision.None;
        var retry=assistant.StopReason!=StopReason.Stop;
        if(retry&&attempted){await EmitOperationAsync(new SessionRecoveryEnded(operation,"retry-exhausted",false)).ConfigureAwait(false);return new(true,false,false);}
        await EmitOperationAsync(new SessionRecoveryStarted(operation,overflow?"overflow":"length",retry)).ConfigureAwait(false);
        // Only synthetic truncated results from this final attempt are omitted. Completed effects
        // from earlier turns remain in raw history and canonical context, and are never replayed.
        if(retry)
        {
            var syntheticIds=turn.Result.Tools.Outcomes.Where(o=>o.Result.Failure?.Kind==ToolFailureKind.Truncated).Select(o=>o.Invocation.Call.Id).ToHashSet(StringComparer.Ordinal);
            var targets=new List<string>{selected.SourceEntry.Id};
            foreach(var tool in turn.ToolResults.Where(m=>syntheticIds.Contains(m.WireBody.Value.GetProperty("toolCallId").GetString()!)))
            {
                var target=context.ContextEntries.LastOrDefault(e=>e.Messages.Any(m=>m.Role=="toolResult"&&JsonElement.DeepEquals(m.WireBody.Value,PersistedWire(tool.WireBody).Value)));
                if(target is null)throw Error(PersistentAgentSessionFailure.InvalidCommit);targets.Add(target.SourceEntry.Id);
            }
            SetOperationPhase(SessionOperationPhase.RecoveryOmission);await OmitRecoveryAttemptAsync(targets,token,idle).ConfigureAwait(false);
        }
        ContextEditCancellation abort;lock(_gate){abort=new();_contextEditCancellation=abort;_compacting=true;}
        SetOperationPhase(SessionOperationPhase.Compaction);
        try
        {
            var receipt=await SummaryCoreAsync(configured.Request with { Automatic=false, Reason=SessionCompactionReason.Overflow, WillRetry=retry },null,configured.Generator,token,idle,abort,default,null,releaseReservation:false).ConfigureAwait(false);
            var status=receipt is null?"skipped":"committed";lock(_gate)_lastAutomaticCompaction=new(status,receipt?.Entry.Id);
            await EmitOperationAsync(new SessionRecoveryEnded(operation,status,retry&&receipt is not null,receipt?.Entry.Id)).ConfigureAwait(false);
            return new(true,retry&&receipt is not null&&!token.IsCancellationRequested,retry);
        }
        catch(SessionCompactionException error)
        {lock(_gate)_lastAutomaticCompaction=new("failed",Failure:error.Failure);await EmitOperationAsync(new SessionRecoveryEnded(operation,"failed",false)).ConfigureAwait(false);return new(true,false,retry);}
        catch(OperationCanceledException)when(token.IsCancellationRequested||abort.Abort.IsCancellationRequested)
        {lock(_gate)_lastAutomaticCompaction=new("cancelled");await EmitOperationAsync(new SessionRecoveryEnded(operation,"cancelled",false)).ConfigureAwait(false);return new(true,false,retry);}
    }
    private async Task OmitRecoveryAttemptAsync(List<string> targets,CancellationToken token,TaskCompletionSource idle)
    {
        await _commits.WaitAsync(token).ConfigureAwait(false);var admitted=false;
        try
        {
            SessionContextProjection previous;SessionLogStoreSnapshot log;lock(_gate){ThrowAvailable();if(!ReferenceEquals(_active,idle))throw Error(PersistentAgentSessionFailure.StaleSession);previous=_context;log=_acknowledgedLog;}
            var entries=log.Entries;var edits=ImmutableArray.CreateBuilder<SessionEntry>();var parent=previous.LeafId;
            foreach(var target in targets)
            {
                var replacement=SessionContextEditValidator.Normalize(previous,new(target,null),token);
                _=ContextEditRecord(target,replacement,"recovery-validation",parent,()=>0);
                token.ThrowIfCancellationRequested();var id=Identity(_nextEntryId,log.Header.Id,entries);token.ThrowIfCancellationRequested();
                var entry=ContextEditRecord(target,replacement,id,parent,_clock);edits.Add(entry);entries=entries.Add(entry);parent=id;
            }
            var prospective=_projector.Project(entries,parent,token);ValidateRuntimeContext(prospective,_configuration);
            await using(var probe=new NativeAgent(_configuration,_clock,new NoopSink(),_agentOptions))probe.ConfigureAndReplaceMessages(_configuration,SessionContextProjector.AgentMessages(prospective));
            token.ThrowIfCancellationRequested();admitted=true;var acknowledgment=await _store.AppendAsync(edits.ToImmutable(),token).ConfigureAwait(false);
            if(!acknowledgment.CheckpointAcknowledged)throw Error(PersistentAgentSessionFailure.InvalidCommit);
            lock(_gate){_agent.ConfigureAndReplaceMessages(RecoveryConfiguration(_configuration),SessionContextProjector.AgentMessages(prospective));_acknowledgedLog=acknowledgment.Snapshot;_context=prospective;}
            // Source _omitRecoveryAttempt emits entry_appended for each context edit it appends.
            admitted=false;await PublishAppendedAsync(acknowledgment.Entries).ConfigureAwait(false);
        }
        catch(SessionLogStoreException storage)
        {if(storage.MayHaveWritten||_store.IsPoisoned)lock(_gate)_fault??=new(PersistentAgentSessionFailure.AppendFailed,storage.Failure,storage.MayHaveWritten,storage.DurableFlushCompleted);throw;}
        catch{if(admitted)lock(_gate)_fault??=new(PersistentAgentSessionFailure.InvalidCommit);throw;}
        finally{_commits.Release();}
    }
}
