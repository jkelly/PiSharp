using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using PiSharp.CodingAgent.Execution;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

namespace PiSharp.CodingAgent;

public sealed partial class PersistentAgentSession
{
    private IUserBashExecutor? _bashCapability;
    private Func<UserBashExecutionUpdate, Task>? _bashObservation;
    private readonly List<BashWork> _bashWork = [];
    private readonly Queue<JsonData> _bashRecords = new();
    private readonly AsyncLocal<BashWork?> _insideBash = new();
    [ThreadStatic] private static Stack<PersistentAgentSession>? t_bashCancellation;
    private Task? _bashAbort;
    private bool _bashAborting;
    private sealed class BashWork
    {
        internal readonly CancellationTokenSource Stop = new();
        internal readonly TaskCompletionSource<UserBashResult> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<BashProgressWork> Progress = [];
        internal readonly List<Exception> CancellationErrors = [];
        internal Task<UserBashResult>? ExecutorOriginal;
        internal TaskCompletionSource? CancellationIdle;
        internal int CancellationUsers;
        internal bool ReceivingProgress = true, Retiring;
    }
    private sealed class BashProgressWork
    {
        internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? ObserverOriginal;
    }
    public bool IsUserBashRunning { get { lock (_gate) return _bashWork.Count != 0; } }
    public bool HasPendingUserBashMessages { get { lock (_gate) return _bashRecords.Count != 0; } }

    public void ConfigureUserBashExecution(IUserBashExecutor? executor,
        Func<UserBashExecutionUpdate, Task>? observer = null)
    {
        ThrowUserBashSelfWait();
        lock (_gate)
        {
            ThrowAvailable(); ThrowInputMutation(); ThrowUserBashMutationLocked();
            if (_active is not null || _inputSubmission is not null) throw new InvalidOperationException("Bash capability capture requires idle session ownership.");
            _bashCapability = executor; _bashObservation = observer;
        }
    }
    public Task<UserBashResult> ExecuteUserBashAsync(string command, bool? excludeFromContext = null,
        string? id = null, CancellationToken cancellationToken = default)
    {
        var request = new UserBashExecutionRequest(command, WorkingDirectory, excludeFromContext, id);
        UserBash.Validate(request); cancellationToken.ThrowIfCancellationRequested(); ThrowUserBashSelfWait();
        BashWork work; IUserBashExecutor capability; Func<UserBashExecutionUpdate, Task>? observer;
        lock (_gate)
        {
            ThrowAvailable(); ThrowAutomaticBashBoundaryLocked();
            if (_bashAborting || _configuring || _editingContext || _compacting || _appendingExtensionEntry || _inputSubmission is not null)
                throw new InvalidOperationException("Bash admission is fenced by an owned session mutation or abort.");
            if (_bashWork.Count + _bashRecords.Count >= 128) throw new InvalidOperationException("Session Bash ownership capacity reached.");
            capability = _bashCapability ?? throw new InvalidOperationException("No explicit user Bash capability is captured.");
            observer = _bashObservation;
            _bashAbort = null; work = new(); _bashWork.Add(work);
        }
        _ = RunUserBashAsync(work, request, capability, observer, cancellationToken);
        return work.Done.Task;
    }
    private async Task RunUserBashAsync(BashWork work, UserBashExecutionRequest request, IUserBashExecutor capability,
        Func<UserBashExecutionUpdate, Task>? observer, CancellationToken callerToken)
    {
        var prior = _insideBash.Value; _insideBash.Value = work;
        var errors = new List<Exception>(); UserBashResult? result = null;
        CancellationTokenRegistration registration = default;
        try
        {
            registration = callerToken.UnsafeRegister(_ => CancelBashWork(work), null);
            work.Stop.Token.ThrowIfCancellationRequested();
            work.ExecutorOriginal = capability.ExecuteAsync(request,
                delta => AcceptBashProgress(work, request.Id, delta, observer), work.Stop.Token)
                ?? throw new InvalidOperationException("Bash capability returned no original operation.");
            try { result = await work.ExecutorOriginal.ConfigureAwait(false); }
            catch (Exception error) { KeepBashFailure(errors, error); }
        }
        catch (Exception error) { KeepBashFailure(errors, error); }
        finally
        {
            try
            {
                Task[] progress;
                lock (_gate) { work.ReceivingProgress = false; progress = work.Progress.Select(value => value.Done.Task).ToArray(); }
                // Each original is joined even if an earlier executor/progress operation failed.
                foreach (var original in progress)
                    try { await original.ConfigureAwait(false); } catch (Exception error) { KeepBashFailure(errors, error); }
                if (errors.Count == 0)
                {
                    UserBash.Validate(result!);
                    var message = UserBash.Message(request, result!, _clock()); bool idle;
                    lock (_gate) { _bashRecords.Enqueue(message); idle = _active is null; }
                    if (idle) await FlushPendingUserBashMessagesAsync().ConfigureAwait(false);
                }
            }
            catch (Exception error) { KeepBashFailure(errors, error); }
            Task cancellations;
            lock (_gate)
            {
                work.ReceivingProgress = false; work.Retiring = true;
                cancellations = work.CancellationUsers == 0 ? Task.CompletedTask : work.CancellationIdle!.Task;
            }
            try { await registration.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { KeepBashFailure(errors, error); }
            try { await cancellations.ConfigureAwait(false); } catch (Exception error) { KeepBashFailure(errors, error); }
            lock (_gate) foreach (var error in work.CancellationErrors) KeepBashFailure(errors, error);
            try { work.Stop.Dispose(); } catch (Exception error) { KeepBashFailure(errors, error); }
            _insideBash.Value = prior;
            lock (_gate)
            {
                if (errors.Count == 0) work.Done.TrySetResult(result!);
                else work.Done.TrySetException(errors.Count == 1 ? errors[0] : new AggregateException(errors));
                _bashWork.Remove(work);
            }
        }
    }
    private Task AcceptBashProgress(BashWork work, string? id, string delta, Func<UserBashExecutionUpdate, Task>? observer)
    {
        UserBash.ValidateDelta(delta); BashProgressWork progress;
        lock (_gate)
        {
            if (!work.ReceivingProgress || work.Retiring) throw new InvalidOperationException("Bash progress arrived after original execution settlement.");
            if (work.Progress.Count >= 1024) throw new InvalidOperationException("Bash original progress ownership capacity reached.");
            progress = new(); work.Progress.Add(progress);
        }
        _ = ObserveBashProgressAsync(work, progress, new(id, delta), observer);
        return progress.Done.Task;
    }
    private async Task ObserveBashProgressAsync(BashWork work, BashProgressWork progress, UserBashExecutionUpdate update,
        Func<UserBashExecutionUpdate, Task>? observer)
    {
        var prior = _insideBash.Value; _insideBash.Value = work;
        try
        {
            progress.ObserverOriginal = observer is null ? Task.CompletedTask : observer(update)
                ?? throw new InvalidOperationException("Bash observer returned no original progress task.");
            await progress.ObserverOriginal.ConfigureAwait(false); progress.Done.TrySetResult();
        }
        catch (Exception error) { progress.Done.TrySetException(error); }
        finally { _insideBash.Value = prior; }
    }
    private void CancelBashWork(BashWork work)
    {
        lock (_gate)
        {
            if (work.Retiring) return;
            if (work.CancellationUsers++ == 0) work.CancellationIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        var owners = t_bashCancellation ??= new(); owners.Push(this);
        try { work.Stop.Cancel(); }
        catch (Exception error)
        {
            lock (_gate) KeepBashFailure(work.CancellationErrors, error);
            RetainOwnedCancellationFailure(error, input: false);
        }
        finally
        {
            owners.Pop();
            lock (_gate) if (--work.CancellationUsers == 0) work.CancellationIdle!.TrySetResult();
        }
    }
    /// <summary>Repeated calls return the same abort task/fault until a fresh invocation is admitted.
    /// Initiate shutdown of every captured physical operation before joining any operation.</summary>
    public Task AbortUserBashAsync()
    {
        ThrowUserBashSelfWait(); BashWork[] captured; TaskCompletionSource completion;
        lock (_gate)
        {
            if (_bashAbort is not null) return _bashAbort;
            _bashAborting = true; captured = _bashWork.ToArray();
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _bashAbort = completion.Task;
        }
        _ = AbortBashOwnedAsync(captured, completion); return completion.Task;
    }
    private async Task AbortBashOwnedAsync(BashWork[] captured, TaskCompletionSource completion)
    {
        var errors = new List<Exception>();
        foreach (var work in captured)
            try { CancelBashWork(work); } catch (Exception error) { KeepBashFailure(errors, error); }
        foreach (var work in captured)
            try { await work.Done.Task.ConfigureAwait(false); } catch (Exception error) { KeepBashFailure(errors, error); }
        lock (_gate)
        {
            _bashAborting = false;
            if (errors.Count == 0) completion.TrySetResult();
            else completion.TrySetException(errors.Count == 1 ? errors[0] : new AggregateException(errors));
        }
    }
    private Task[] CaptureUserBashCompletionsLocked()
    {
        var originals = _bashWork.Select(value => (Task)value.Done.Task).ToList();
        if (_bashAborting && _bashAbort is { } abort) originals.Add(abort);
        return originals.ToArray();
    }
    private void ThrowUserBashSelfWait()
    {
        if (_insideBash.Value is not null || t_bashCancellation?.Contains(this) == true)
            throw new InvalidOperationException("Bash-owned callbacks cannot await their own session settlement.");
    }
    private void ThrowUserBashMutationLocked()
    {
        if (_bashWork.Count != 0 || _bashRecords.Count != 0 || _bashAborting)
            throw new InvalidOperationException("Bash originals or deferred records still own session state.");
    }
    private static void KeepBashFailure(List<Exception> errors, Exception error)
    {
        bool Contains(Exception outer, Exception inner) => ReferenceEquals(outer, inner) ||
            outer is AggregateException aggregate && aggregate.InnerExceptions.Any(value => Contains(value, inner));
        if (!errors.Any(value => Contains(value, error))) errors.Add(error);
    }
    private async Task FlushPendingUserBashMessagesAsync()
    {
        await _commits.WaitAsync().ConfigureAwait(false);
        try
        {
            JsonData[] records; SessionContextProjection previous;
            lock (_gate)
            {
                if (_bashRecords.Count == 0) return;
                if (_agent.Snapshot.IsRunning || _active is not null && !ReferenceEquals(_automaticBashBoundaryOwner, _active))
                    throw new InvalidOperationException("Bash records require idle ownership or the fenced provider boundary.");
                if (_fault is not null) throw Error(PersistentAgentSessionFailure.Faulted);
                records = _bashRecords.ToArray(); previous = _context;
            }
            var log = _store.Snapshot; var entries = log.Entries; var leaf = previous.LeafId;
            var pending = ImmutableArray.CreateBuilder<SessionEntry>(records.Length);
            foreach (var message in records)
            {
                var entry = Record(_codec, "message", Identity(_nextEntryId, log.Header.Id, entries), leaf, _clock,
                    writer => { writer.WritePropertyName("message"); message.Value.WriteTo(writer); });
                pending.Add(entry); entries = entries.Add(entry); leaf = entry.Id;
            }
            var next = _projector.Project(entries, leaf); ValidateRuntimeContext(next, _configuration);
            if (_registry is not null) ValidateLoadout(next.LlmMessages);
            // Cancellation can initiate physical shutdown but cannot detach an admitted durable checkpoint.
            var originalAppend = _store.AppendAsync(pending.ToImmutable(), CancellationToken.None);
            var receipt = await originalAppend.ConfigureAwait(false);
            if (!receipt.CheckpointAcknowledged) throw Error(PersistentAgentSessionFailure.InvalidCommit);
            lock (_gate)
            {
                _acknowledgedLog = receipt.Snapshot; _context = next;
                for (var index = 0; index < records.Length; index++) _bashRecords.Dequeue();
                _agent.ConfigureAndReplaceMessages(RecoveryConfiguration(_configuration), SessionContextProjector.AgentMessages(next));
            }
        }
        catch (Exception error)
        {
            lock (_gate) _fault ??= error is SessionLogStoreException storage
                ? new(PersistentAgentSessionFailure.AppendFailed, storage.Failure, storage.MayHaveWritten, storage.DurableFlushCompleted)
                : new(PersistentAgentSessionFailure.InvalidCommit);
            throw;
        }
        finally { _commits.Release(); }
    }
}
