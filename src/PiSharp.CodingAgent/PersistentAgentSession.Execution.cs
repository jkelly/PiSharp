using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;
using NativeAgent = PiSharp.Agent.Agent;

namespace PiSharp.CodingAgent;

public sealed partial class PersistentAgentSession
{
    public Task<AgentLoopResult> PromptAsync(TranscriptEntry message, CancellationToken cancellationToken = default) =>
        PromptAsync([message], cancellationToken);
    public Task<AgentLoopResult> PromptAsync(ImmutableArray<TranscriptEntry> messages, CancellationToken cancellationToken = default) =>
        Start(token => _agent.PromptAsync(messages, token), cancellationToken, messages, injectNextTurnCustom: true);
    public Task<AgentLoopResult> ContinueAsync(CancellationToken cancellationToken = default) =>
        Start(_agent.ContinueAsync, cancellationToken);

    /// <summary>Explicit source-input admission. Handled input starts no run; active delivery uses actual existing queues.</summary>
    public Task<SubmittedInputResult> SubmitInputAsync(PromptInput input, IPromptInputAdmission? admission = null,
        PromptInputAdmissionOptions? options = null, CancellationToken cancellationToken = default)
    {
        input = PromptInputValue.Own(input, options);
        if (options?.QueueOnly == true && input.StreamingBehavior is null)
            throw new PromptInputAdmissionException(PromptInputAdmissionFailure.InvalidInput);
        // Pi applies a background MCP server's tools between prompts without refusing input: input that arrives while such a
        // catalog publication is committing waits for it, then is admitted as usual.
        // A host gate (MCP servers a codemode script may need) runs before idle input is admitted.
        if (BeforeInputAdmission is { } before && !_gatedInput.Value) { bool idle; lock (_gate) idle = _active is null && _inputSubmission is null; if (idle) return GatedAsync(before, input, admission, options, cancellationToken); }
        TaskCompletionSource? publication;
        lock (_gate) publication = _configuring && _catalogPublication is { } pending && ReferenceEquals(_active, pending) ? pending : null;
        if (publication is not null) return AfterPublicationAsync(publication.Task, input, admission, options, cancellationToken);
        var reservation = new InputSubmission();
        try
        {
            lock (_gate)
            {
                ThrowAvailable();
                if (_active is null) ThrowUserBashMutationLocked();
                // Pi 1.2.0 _startRun: once an input has become a run the session is busy, so input sent while that run starts (from its
                // before_agent_start handlers) is admitted as busy input and must say how it is delivered.
                if (_configuring || _inputSubmission is { RunStarted: false }) throw new InvalidOperationException("Session input admission is already processing.");
                cancellationToken.ThrowIfCancellationRequested();
                _inputSubmission = reservation; _inputTriggeredRun = null;
                // Like source prompt(), idle handlers see no streaming behavior.
                if (_active is null && options?.QueueOnly != true) input = input with { StreamingBehavior = null };
            }
        }
        catch { reservation.Abort.Dispose(); throw; }
        return SubmitInputCoreAsync(input, admission, options, cancellationToken, reservation);
    }

    /// <summary>Source prompt(): busy input without a delivery mode fails with this message.</summary>
    public const string BusyInputMessage = "Agent is already processing. Specify streamingBehavior ('steer' or 'followUp') to queue the message.";

    /// <summary>The host's prompt validation (<see cref="PersistentAgentSessionOptions.PromptPreflight"/>).</summary>
    public Func<ModelDescriptor, CancellationToken, ValueTask>? PromptPreflight { get; private init; }

    /// <summary>Awaited before idle input is admitted, with the input's cancellation; set by the session host.</summary>
    public Func<CancellationToken, Task>? BeforeInputAdmission { get; set; }
    private readonly AsyncLocal<bool> _gatedInput = new();

    private async Task<SubmittedInputResult> GatedAsync(Func<CancellationToken, Task> before, PromptInput input, IPromptInputAdmission? admission,
        PromptInputAdmissionOptions? options, CancellationToken cancellationToken)
    {
        await before(cancellationToken).ConfigureAwait(false);
        _gatedInput.Value = true;
        try { return await SubmitInputAsync(input, admission, options, cancellationToken).ConfigureAwait(false); }
        finally { _gatedInput.Value = false; }
    }

    private async Task<SubmittedInputResult> AfterPublicationAsync(Task publication, PromptInput input, IPromptInputAdmission? admission,
        PromptInputAdmissionOptions? options, CancellationToken cancellationToken)
    {
        await publication.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await SubmitInputAsync(input, admission, options, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SubmittedInputResult> SubmitInputCoreAsync(PromptInput input, IPromptInputAdmission? admission,
        PromptInputAdmissionOptions? options, CancellationToken token, InputSubmission reservation)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _closing.Token, reservation.Abort.Token);
        var work = cancellation.Token;
        try
        {
            PromptInputDecision decision;
            var previous = _inputCallback.Value;
            _inputCallback.Value = reservation;
            try
            {
                decision = admission is null ? new(PromptInputAction.Continue) :
                    await admission.ReduceAsync(input, work).ConfigureAwait(false);
                work.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException error) when (work.IsCancellationRequested || error.CancellationToken.IsCancellationRequested)
            { throw new OperationCanceledException("Input submission was cancelled.", error.CancellationToken.IsCancellationRequested ? error.CancellationToken : work); }
            catch (PromptInputAdmissionException) { throw; }
            catch (Exception) { throw new PromptInputAdmissionException(PromptInputAdmissionFailure.HandlerFailed); }
            finally { _inputCallback.Value = previous; }

            var effective = PromptInputValue.Apply(input, decision, options);
            work.ThrowIfCancellationRequested();
            if (decision.Action == PromptInputAction.Handled) return new(SubmittedInputDisposition.Handled);
            lock (_gate)
            {
                ThrowAvailable(); work.ThrowIfCancellationRequested();
                if (_active is not null && input.StreamingBehavior is null)
                    throw new InvalidOperationException(BusyInputMessage);
            }
            // agent-session.ts prompt: an idle prompt validates the model and its provider's auth before anything is persisted.
            bool idlePrompt; lock (_gate) idlePrompt = _active is null && options?.QueueOnly != true;
            if (idlePrompt && PromptPreflight is { } preflight)
            {
                ModelDescriptor model; lock (_gate) model = _agent.Snapshot.Model;
                await preflight(model, work).ConfigureAwait(false);
                work.ThrowIfCancellationRequested();
            }
            long timestamp;
            previous = _inputCallback.Value; _inputCallback.Value = reservation;
            try { timestamp = _clock(); }
            catch (Exception) { throw new PromptInputAdmissionException(PromptInputAdmissionFailure.InvalidInput); }
            finally { _inputCallback.Value = previous; }
            var message = PromptInputValue.Message(effective, timestamp, options);
            // Pure schema/depth admission including the durable record's outer envelope. This
            // inert probe consumes no trusted ID or clock and is never appended or projected.
            try { _ = _codec.Parse("{\"type\":\"message\",\"id\":\"input-admission\",\"parentId\":null,\"timestamp\":\"1970-01-01T00:00:00.000Z\",\"message\":" + message.WireBody + "}"); }
            catch (SessionEntryCodecException error)
            {
                throw new PromptInputAdmissionException(error.Failure is SessionEntryCodecFailure.CharacterLimit or
                    SessionEntryCodecFailure.Utf8ByteLimit or SessionEntryCodecFailure.DepthLimit
                    ? PromptInputAdmissionFailure.ResourceLimit : PromptInputAdmissionFailure.InvalidInput);
            }
            TaskCompletionSource? generation = null;
            for (var attempt = 0; ; attempt++)
            {
                AgentPendingInputQueueSnapshot? captured = null;
                lock (_gate)
                {
                    ThrowAvailable(); work.ThrowIfCancellationRequested();
                    if (_active is not null || options?.QueueOnly == true)
                    {
                        RejectSettlementQueue();
                        if (input.StreamingBehavior is null)
                            throw new InvalidOperationException(BusyInputMessage);
                        captured = _agent.GetPendingInputQueueSnapshot(work);
                    }
                }
                if (captured is not null && options?.BeforeQueueCommit is { } validate)
                {
                    // Host receipt validation is pure synchronous work, never a callback under a state lock.
                    previous = _inputCallback.Value; _inputCallback.Value = reservation;
                    var priorValidation = _queueValidationCallback.Value; _queueValidationCallback.Value = true;
                    try { validate(message, captured, input.StreamingBehavior!.Value); }
                    finally { _queueValidationCallback.Value = priorValidation; _inputCallback.Value = previous; }
                }
                lock (_gate)
                {
                    ThrowAvailable(); work.ThrowIfCancellationRequested();
                    if (_active is not null || options?.QueueOnly == true)
                    {
                        if (input.StreamingBehavior is null)
                            throw new InvalidOperationException(BusyInputMessage);
                        var current = _agent.GetPendingInputQueueSnapshot(work);
                        RejectSettlementQueue();
                        if (captured is null || !QueueCanOnlyShrink(captured, current))
                        {
                            if (attempt >= 7) throw new PromptInputAdmissionException(PromptInputAdmissionFailure.ResourceLimit);
                            continue;
                        }
                        if (input.StreamingBehavior == PromptInputStreamingBehavior.Steer) _agent.Steer(message, work);
                        else _agent.FollowUp(message, work);
                        return new(SubmittedInputDisposition.Queued);
                    }
                    var operation = checked(_operationGeneration + 1);
                    var runCancellation = new ContextEditCancellation();
                    generation = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _operationGeneration = operation; _runCancellation = runCancellation;
                    _operationPhase = SessionOperationPhase.Provider; _active = generation; reservation.RunStarted = true;
                    RunCatalogOpenedLocked();
                    break;
                }
            }
            // ExecuteAsync keeps the accepted loadout validation, durable event path and run fault policy.
            // Keep the input abort reservation until Agent admission has begun, closing the pre-start race.
            ImmutableArray<TranscriptEntry> promptMessages;
            lock (_gate) promptMessages = InjectNextTurnCustomLocked([message]);
            var run = ExecuteAsync(current => _agent.PromptAsync(promptMessages, current), work, generation, promptMessages);
            await ReleaseInputAsync(reservation).ConfigureAwait(false);
            return new(SubmittedInputDisposition.Started, await run.ConfigureAwait(false));
        }
        finally { await ReleaseInputAsync(reservation).ConfigureAwait(false); }
    }

    private static bool QueueCanOnlyShrink(AgentPendingInputQueueSnapshot captured, AgentPendingInputQueueSnapshot current) =>
        Suffix(captured.SteeringMessages, current.SteeringMessages) && Suffix(captured.FollowUpMessages, current.FollowUpMessages);
    private static bool Suffix(ImmutableArray<TranscriptEntry> before, ImmutableArray<TranscriptEntry> after)
    {
        if (after.Length > before.Length) return false;
        for (var index = 0; index < after.Length; index++)
            if (!ReferenceEquals(before[before.Length - after.Length + index], after[index])) return false;
        return true;
    }

    private async Task ReleaseInputAsync(InputSubmission reservation)
    {
        Task? wait = null; var release = false;
        lock (_gate)
        {
            if (!reservation.Releasing)
            {
                reservation.Releasing = true; release = true;
                wait = reservation.CancelUsers == 0 ? Task.CompletedTask : reservation.CancelIdle!.Task;
            }
        }
        if (!release) { await reservation.Idle.Task.ConfigureAwait(false); return; }
        try { await wait!.ConfigureAwait(false); reservation.Abort.Dispose(); }
        finally
        {
            lock (_gate) if (ReferenceEquals(_inputSubmission, reservation)) _inputSubmission = null;
            reservation.Idle.TrySetResult();
        }
    }

    private Task<AgentLoopResult> Start(Func<CancellationToken, Task<AgentLoopResult>> start, CancellationToken token,
        ImmutableArray<TranscriptEntry> inputs = default, bool injectNextTurnCustom = false, bool duringInput = false)
    {
        TaskCompletionSource idle;
        lock (_gate)
        {
            ThrowAvailable();
            ThrowUserBashMutationLocked();
            // A custom message's turn (sendCustomMessage triggerTurn) may start while a prompt's input is admitted (a command's handler).
            if (_active is not null || _inputSubmission is not null && !duringInput) throw new InvalidOperationException("Session is already processing.");
            token.ThrowIfCancellationRequested();
            if (injectNextTurnCustom) inputs = InjectNextTurnCustomLocked(inputs);
            _operationGeneration = checked(_operationGeneration + 1);
            // Source _startRun: the run records the loadout; restored tools that did not register by now are dropped.
            _pendingToolNames = [];
            _runCancellation = new(); _operationPhase = SessionOperationPhase.Provider;
            idle = new(TaskCreationOptions.RunContinuationsAsynchronously); _active = idle;
            RunCatalogOpenedLocked();
        }
        return ExecuteAsync(injectNextTurnCustom ? current => _agent.PromptAsync(inputs, current) : start, token, idle, inputs);
    }
    private async Task<AgentLoopResult> ExecuteAsync(Func<CancellationToken, Task<AgentLoopResult>> start,
        CancellationToken token, TaskCompletionSource idle, ImmutableArray<TranscriptEntry> inputs)
    {
        var generation = _agent.Snapshot.Generation;
        ContextEditCancellation runAbort; long operation;
        lock (_gate) { runAbort = _runCancellation!; operation = _operationGeneration; }
        var retry = BeginRetryOperation(operation);
        AgentLoopResult? settledResult = null; var status = "failed";
        var failures = new List<Exception>();
        var priorCallback = _configurationCallback.Value; _configurationCallback.Value = idle;
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _closing.Token, runAbort.Abort.Token);
            await DrainLoadoutDiagnosticsAsync(cancellation.Token).ConfigureAwait(false);
            if (_registry is not null && !inputs.IsDefault)
                ValidateLoadout(_context.LlmMessages.AddRange(inputs), cancellation.Token);
            if (!inputs.IsDefault) await RecordLiveThinkingAsync(cancellation.Token).ConfigureAwait(false);
            settledResult = await RunUntilSettlementAsync(start, idle, cancellation.Token, operation).ConfigureAwait(false);
            status = cancellation.IsCancellationRequested ? "cancelled" :
                settledResult.Reason == AgentLoopStopReason.Completed ? "completed" :
                settledResult.Reason == AgentLoopStopReason.TurnLimit ? "turn-limit" : "failed";
        }
        catch (OperationCanceledException error) when (token.IsCancellationRequested || _closing.IsCancellationRequested || runAbort.Abort.IsCancellationRequested)
        { status = "cancelled"; AddDistinctFailure(failures, error); }
        catch (Exception error)
        {
            AddDistinctFailure(failures, error);
            if (_agent.Snapshot.Generation > generation)
                lock (_gate) _fault ??= new(PersistentAgentSessionFailure.RunFailed);
        }
        finally
        {
            IDisposable? bashBoundary = null;
            try
            {
                SetOperationPhase(SessionOperationPhase.Settlement);
                // Even without automatic compaction, final settlement joins Bash originals and the
                // acknowledged transcript flush before notifying observers or releasing provider ownership.
                try
                {
                    bashBoundary = await BeginUserBashBoundaryAsync(idle).ConfigureAwait(false);
                    // A catalog the run took after its last request is recorded before the session is idle again.
                    await RecordRunCatalogAsync(idle).ConfigureAwait(false);
                    if (settledResult is not null) settledResult = settledResult with { Transcript = Snapshot.Context.LlmMessages };
                }
                catch (Exception error) { AddDistinctFailure(failures, error); status = "failed"; }
                if (retry is not null)
                {
                    try
                    {
                        if (status == "cancelled" || token.IsCancellationRequested || _closing.IsCancellationRequested || runAbort.Abort.IsCancellationRequested)
                            await retry.FinishCancelledAsync().ConfigureAwait(false);
                        else
                        {
                            var last = settledResult?.Turns.LastOrDefault()?.Result.Chat.Message;
                            await retry.FinishAsync(last?.StopReason ?? StopReason.Error, last is null ? null : RetryErrorMessage(last)).ConfigureAwait(false);
                        }
                    }
                    catch (Exception error) { AddDistinctFailure(failures, error); status = "failed"; }
                    try { await retry.JoinAsync().ConfigureAwait(false); }
                    catch (Exception error) { AddDistinctFailure(failures, error); status = "failed"; }
                }
                Task settingsIdle; lock (_gate) settingsIdle = RetrySettingsIdleLocked();
                try { await settingsIdle.ConfigureAwait(false); }
                catch (Exception error) { AddDistinctFailure(failures, error); status = "failed"; }
                // The source flag is set by abort() during an active run; the caller's token is this host's abort path.
                var aborted = runAbort.Abort.IsCancellationRequested || token.IsCancellationRequested;
                try { await EmitOperationAsync(new SessionOperationSettled(operation, status, settledResult) { Aborted = aborted }).ConfigureAwait(false); }
                catch (Exception error)
                { AddDistinctFailure(failures, error); lock (_gate) _fault ??= new(PersistentAgentSessionFailure.RunFailed); }
            }
            finally
            {
                try { bashBoundary?.Dispose(); } catch (Exception error) { AddDistinctFailure(failures, error); }
                Task cancelIdle;
                lock (_gate) { if (ReferenceEquals(_runCancellation, runAbort)) _runCancellation = null; cancelIdle = runAbort.CancelUsers == 0 ? Task.CompletedTask : runAbort.CancelIdle!.Task; }
                try { await cancelIdle.ConfigureAwait(false); } catch (Exception error) { AddDistinctFailure(failures, error); }
                try { runAbort.Abort.Dispose(); } catch (Exception error) { AddDistinctFailure(failures, error); }
                lock (_gate) if (ReferenceEquals(_active, idle)) { _active = null; _operationPhase = SessionOperationPhase.Idle; }
                lock (_gate) if (ReferenceEquals(_retryCoordinator, retry)) _retryCoordinator = null;
                idle.TrySetResult(); _configurationCallback.Value = priorCallback;
            }
        }
        if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Session body and owned settlement failed.", failures);
        return settledResult!;
    }
    public void Steer(TranscriptEntry message, CancellationToken cancellationToken = default)
    { lock (_gate) { ThrowAvailable(); ThrowInputMutation(); RejectSettlementQueue(); RejectConfigurationQueue(); RejectQueuedLoadoutChange(message); _agent.Steer(message, cancellationToken); } }
    public void FollowUp(TranscriptEntry message, CancellationToken cancellationToken = default)
    { lock (_gate) { ThrowAvailable(); ThrowInputMutation(); RejectSettlementQueue(); RejectConfigurationQueue(); RejectQueuedLoadoutChange(message); _agent.FollowUp(message, cancellationToken); } }
    /// <summary>Reads full independent pending queues; this does not read or change durable history.</summary>
    public AgentPendingInputQueueSnapshot GetPendingInputQueueSnapshot(CancellationToken cancellationToken = default)
    { lock (_gate) return _agent.GetPendingInputQueueSnapshot(cancellationToken); }
    /// <summary>Returns and removes all queued steering/follow-up values, while preserving admitted work and history.</summary>
    public AgentPendingInputQueueSnapshot ClearPendingInputQueues(CancellationToken cancellationToken = default)
    { lock (_gate) { ThrowAvailable(); ThrowInputMutation(); return _agent.ClearPendingInputQueues(cancellationToken); } }
    /// <summary>Compare-and-clear for preflighted queue restoration; preserves admitted work and durable history.</summary>
    public bool TryClearPendingInputQueues(AgentPendingInputQueueSnapshot expected,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AgentPendingInputQueueSnapshot? removed,
        CancellationToken cancellationToken = default)
    { lock (_gate) { ThrowAvailable(); ThrowInputMutation(); return _agent.TryClearPendingInputQueues(expected, out removed, cancellationToken); } }
    private void RejectConfigurationQueue()
    {
        if (_configuring) throw new InvalidOperationException("Session configuration is already processing.");
    }
    private void RejectQueuedLoadoutChange(TranscriptEntry message)
    {
        if (_registry is not null && message?.Role == "system" && message.WireBody.Value.ValueKind == JsonValueKind.Object &&
            (message.WireBody.Value.TryGetProperty("toolsAdded", out _) || message.WireBody.Value.TryGetProperty("toolsRemoved", out _)))
            throw Error(PersistentAgentSessionFailure.InvalidConfiguration);
    }
    private void ValidateLoadout(ImmutableArray<TranscriptEntry> messages, CancellationToken token = default)
    {
        var selected = LoadoutRegistry().Resolve(_configuration.Model, WithUnrecordedLoadout(messages, _configuration.Tools.Select(tool => tool.Name).ToImmutableArray(), token),
            _configuration.ThinkingLevel, cancellationToken: token, prepareLoadout: false, activeOrder: _configuration.Tools.Select(tool => tool.Name).ToImmutableArray());
        if (!selected.Configuration.Tools.Select(tool => (tool.Name, tool.ExecutionMode))
            .SequenceEqual(_configuration.Tools.Select(tool => (tool.Name, tool.ExecutionMode))))
            throw Error(PersistentAgentSessionFailure.InvalidConfiguration);
    }
    public AgentPendingInputMode SteeringMode
    {
        get => _agent.SteeringMode;
        set { lock (_gate) { ThrowAvailable(); ThrowInputMutation(); _agent.SteeringMode = value; } }
    }
    public AgentPendingInputMode FollowUpMode
    {
        get => _agent.FollowUpMode;
        set { lock (_gate) { ThrowAvailable(); ThrowInputMutation(); _agent.FollowUpMode = value; } }
    }
    public IDisposable Subscribe(IAgentEventSink sink)
    { lock (_gate) { ThrowBindable(); return _agent.Subscribe(sink); } }
    public bool Abort()
    {
        InputSubmission? input; ContextEditCancellation? edit; ContextEditCancellation? run;
        lock (_gate)
        {
            input = _inputSubmission is { Releasing: false } current ? current : null;
            if (input is not null && input.CancelUsers++ == 0)
                input.CancelIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            edit = _contextEditCancellation;
            if (edit is not null && edit.CancelUsers++ == 0)
                edit.CancelIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            run = _runCancellation;
            if (run is not null && run.CancelUsers++ == 0)
                run.CancelIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        if (input is not null)
            try { input.Abort.Cancel(); }
            catch (Exception error) { RetainInputCancellationFailure(error); }
            finally
            {
                lock (_gate)
                {
                    if (--input.CancelUsers == 0) input.CancelIdle!.TrySetResult();
                }
            }
        if (edit is not null)
            try { edit.Abort.Cancel(); }
            catch (Exception error) { RetainInputCancellationFailure(error); }
            finally { lock (_gate) if (--edit.CancelUsers == 0) edit.CancelIdle!.TrySetResult(); }
        if (run is not null)
            try { run.Abort.Cancel(); }
            catch (Exception error) { RetainInputCancellationFailure(error); }
            finally { lock (_gate) if (--run.CancelUsers == 0) run.CancelIdle!.TrySetResult(); }
        var agent = _agent.Abort();
        return input is not null || edit is not null || run is not null || agent;
    }
    public Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        ThrowConfigurationSelfWait();
        // Agent detects callback self-waits before this coordinator's additional admission lease is awaited.
        var agentIdle = _agent.WaitForIdleAsync();
        Task[] pending; lock (_gate) pending = IdleTasksLocked(agentIdle);
        var settled = Task.WhenAll(pending);
        return cancellationToken.CanBeCanceled ? settled.WaitAsync(cancellationToken) : settled;
    }
    private Task[] IdleTasksLocked(Task agentIdle) =>
        [agentIdle, _active?.Task ?? Task.CompletedTask, _inputSubmission?.Idle.Task ?? Task.CompletedTask, LoadoutDiagnosticIdleLocked(),
            RetrySettingsIdleLocked(), .. CaptureUserBashCompletionsLocked()];

    /// <summary>Source <c>ctx.waitForIdle()</c> (agent-session.ts <c>waitForIdle</c>: no agent run and no compaction): called from the
    /// admitted input's own callback (an extension command's handler), the command's input does not count, so the handler waits for the
    /// turn its <c>sendMessage</c> started. Elsewhere it is <see cref="WaitForIdleAsync"/>.</summary>
    public Task WaitForCommandIdleAsync(CancellationToken cancellationToken = default)
    {
        bool own; lock (_gate) own = _inputSubmission is { Releasing: false } current && ReferenceEquals(_inputCallback.Value, current);
        if (!own) return WaitForIdleAsync(cancellationToken);
        ThrowRetrySelfWait(); ThrowUserBashSelfWait();
        var agentIdle = _agent.WaitForIdleAsync();
        Task idle; Task diagnostics; Task settings; Task[] bash;
        lock (_gate) { idle = _active?.Task ?? Task.CompletedTask; diagnostics = LoadoutDiagnosticIdleLocked(); settings = RetrySettingsIdleLocked(); bash = CaptureUserBashCompletionsLocked(); }
        var settled = AfterPublishedAsync(Task.WhenAll(new[] { agentIdle, idle, diagnostics, settings }.Concat(bash)));
        return cancellationToken.CanBeCanceled ? settled.WaitAsync(cancellationToken) : settled;
        // _emitAgentSettled: agent_settled is published before the waiter resumes (the host adds its barrier when the run starts).
        async Task AfterPublishedAsync(Task run)
        {
            await run.ConfigureAwait(false);
            ImmutableList<Task> barriers; lock (_gate) barriers = _commandIdleBarriers;
            if (!barriers.IsEmpty) await Task.WhenAll(barriers).ConfigureAwait(false);
        }
    }

    // Same pre-mutation callback checks as WaitForIdle, without creating an aggregate idle join.
    internal void RejectOwnedResourceSelfWait()
    {
        ThrowConfigurationSelfWait();
        _ = _agent.WaitForIdleAsync(); // Agent returns its existing task after its own synchronous guard.
    }

    private async ValueTask CommitAsync(AgentEvent observation)
    {
        if (observation is AgentLoopTurnEnded turn) { await CommitNativeDiagnosticAsync(turn).ConfigureAwait(false); return; }
        if (observation is not (AgentLoopInputMessageEnded or AssistantMessageEnded or ToolResultMessageEnded)) return;
        // Source message_end handlers run before the message is persisted; a replacement is what the session records.
        var replacement = await MessageEndReplacementAsync().ConfigureAwait(false); Exception? rejected = null;
        await _commits.WaitAsync().ConfigureAwait(false);
        try
        {
            // Agent publishes this exact owned record before invoking its primary sink.
            var messages = _agent.Snapshot.Messages;
            if (messages.IsEmpty) throw Error(PersistentAgentSessionFailure.InvalidCommit);
            var original = messages[^1]; var message = replacement ?? original;
            var role = observation switch
            {
                AgentLoopInputMessageEnded input => input.Message.Role,
                AssistantMessageEnded => "assistant",
                _ => "toolResult"
            };
            if (original.Role != role || message.Role != role) throw Error(PersistentAgentSessionFailure.InvalidCommit);
            SessionContextProjection previous;
            lock (_gate)
            {
                if (_fault is not null) throw Error(PersistentAgentSessionFailure.Faulted);
                previous = _context;
            }
            var log = _store.Snapshot;
            SessionEntry entry; SessionContextProjection nextContext;
            try { (entry, nextContext) = Prepare(message); }
            catch (Exception error) when (replacement is not null && error is not SessionLogStoreException)
            { rejected = error; replacement = null; message = original; (entry, nextContext) = Prepare(message); }
            (SessionEntry, SessionContextProjection) Prepare(TranscriptEntry message)
            {
            var entry = Record(_codec, role == "custom" ? "custom_message" : "message", Identity(_nextEntryId, log.Header.Id, log.Entries), previous.LeafId,
                _clock, writer =>
                {
                    if (role == "custom")
                    {
                        foreach (var property in message.WireBody.Value.EnumerateObject())
                            if (property.Name is "customType" or "content" or "display" or "details") property.WriteTo(writer);
                    }
                    else
                    {
                        writer.WritePropertyName("message");
                        writer.WriteRawValue(message.WireBody.Value.GetRawText(), skipInputValidation: true);
                    }
                });
            // Reject unsupported selected influences/bounds before writing, without rewriting history.
            var nextContext = _projector.Project(log.Entries.Add(entry), entry.Id);
            ValidateRuntimeContext(nextContext, _configuration, _toleratedSelection, _toleratedThinking);
            if (_registry is not null) ValidateLoadout(nextContext.LlmMessages);
            // A replacement enters the running loop (source in-place mutation), so it must be a message the loop can send.
            if (!ReferenceEquals(message, original) && role != "custom") AgentLoopRunner.ValidateRequestMessages([message]);
            return (entry, nextContext);
            }
            // The loop holds a custom replacement in its persisted form (the projection adds the entry timestamp).
            if (replacement is not null && role == "custom")
                replacement = SessionContextProjector.AgentMessages(nextContext) is { IsEmpty: false } projected && projected[^1].Role == "custom" ? projected[^1] : null;
            var acknowledged = await _store.AppendAsync([entry], CancellationToken.None).ConfigureAwait(false);
            if (!acknowledged.CheckpointAcknowledged) throw Error(PersistentAgentSessionFailure.InvalidCommit);
            lock (_gate)
            {
                _acknowledgedLog = acknowledged.Snapshot; _context = nextContext;
                if (role == "assistant") _lastAcknowledgedAssistantId = entry.Id;
                if (replacement is not null)
                {
                    if (_replacedMessages.Count >= 1024) _replacedMessages.Clear();
                    _replacedMessages[original.WireBody.Value.GetRawText()] = replacement.WireBody;
                    // The loop's assistant is the parsed replacement; its wire form maps to the persisted body too.
                    if (role == "assistant") _replacedMessages[PiWireJson.WriteMessage(PiWireJson.ReadMessage(replacement.WireBody.Value)).Value.GetRawText()] = replacement.WireBody;
                }
            }
            // Source _replaceMessageInPlace: the running loop, the agent's history and later events use the replacement immediately.
            if (replacement is not null) AgentMessageReplacement.Set(observation, replacement);
        }
        catch (Exception error)
        {
            var fault = error is SessionLogStoreException storage ?
                new PersistentAgentSessionFault(PersistentAgentSessionFailure.AppendFailed, storage.Failure,
                    storage.MayHaveWritten, storage.DurableFlushCompleted) :
                new PersistentAgentSessionFault(PersistentAgentSessionFailure.InvalidCommit);
            lock (_gate) _fault ??= fault;
            throw new PersistentAgentSessionException(fault);
        }
        finally { _commits.Release(); }
        if (rejected is not null) await RejectMessageEndAsync(rejected).ConfigureAwait(false);
        if (observation is AssistantMessageEnded assistant && assistant.Message.StopReason != StopReason.Error)
        {
            SessionRetryCoordinator? retry; lock (_gate) retry = _retryCoordinator;
            if (retry is not null) await retry.CompleteAssistantAsync(assistant.Message.StopReason, RetryErrorMessage(assistant.Message)).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (IsShutdownCallback) throw new InvalidOperationException("Shutdown callbacks cannot await their own session disposal.");
        ThrowConfigurationSelfWait();
        _ = _agent.WaitForIdleAsync(); // Reject disposal from an active Agent callback, without changing lifecycle state.
        TaskCompletionSource completion; Task idle;
        lock (_gate)
        {
            if (_disposal is not null) return new(_disposal);
            if (_replacing && !_retired) throw new InvalidOperationException("Session replacement is in progress.");
            _disposed = true; idle = Task.WhenAll(_active?.Task ?? Task.CompletedTask, _inputSubmission?.Idle.Task ?? Task.CompletedTask);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposal = completion.Task;
        }
        _ = DisposeCoreAsync(idle, completion);
        return new(completion.Task);
    }
    private async Task DisposeCoreAsync(Task idle, TaskCompletionSource completion)
    {
        var failures = new List<Exception>();
        try { await StopAdmissionAndJoinAsync().ConfigureAwait(false); } catch (Exception error) { AddDistinctFailure(failures, error); }
        var priorShutdown = _inShutdown.Value; _inShutdown.Value = true;
        try
        {
            try { await idle.ConfigureAwait(false); await _agent.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { AddDistinctFailure(failures, error); }
            // agent-session.ts dispose: cleanupSessionResources(this.sessionId) releases the session's provider resources (the cached
            // Codex WebSockets) under the id its requests carried.
            try { string sessionId; lock (_gate) sessionId = _configuration.SessionId ?? _store.Snapshot.Header.Id; SessionResources.Cleanup(sessionId); }
            catch (Exception error) { AddDistinctFailure(failures, error); }
            try { await _store.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { AddDistinctFailure(failures, error); }
            try { if (_runtimeLease is not null) await _runtimeLease.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { AddDistinctFailure(failures, error); }
            foreach (var resource in new IDisposable?[] { _invocationLifetime, _closing, _commits })
                try { resource?.Dispose(); } catch (Exception error) { AddDistinctFailure(failures, error); }
        }
        catch (Exception error) { AddDistinctFailure(failures, error); }
        finally { _inShutdown.Value = priorShutdown; }
        if (failures.Count > 0)
        {
            var fault = new PersistentAgentSessionFault(PersistentAgentSessionFailure.CleanupFailed);
            lock (_gate) _fault ??= fault;
            completion.TrySetException(new PersistentAgentSessionException(fault, new AggregateException(failures)));
        }
        else completion.TrySetResult();
    }

    private void ThrowAvailable()
    {
        if (_disposed) throw Error(PersistentAgentSessionFailure.Disposed);
        if (_admissionStopped) throw Error(PersistentAgentSessionFailure.Disposed);
        if (_fault is not null) throw Error(PersistentAgentSessionFailure.Faulted);
        if (_retired) throw Error(PersistentAgentSessionFailure.SessionReplaced);
        if (_replacing) throw new SessionReplacementInProgressException();
    }

    /// <summary>Whether an owner operation holds this session's replacement reservation, which most operations refuse meanwhile.</summary>
    internal bool ReservedForReplacement { get { lock (_gate) return _replacing && !_retired; } }
    private void ThrowConfigurationSelfWait()
    {
        ThrowRetrySelfWait();
        ThrowUserBashSelfWait();
        if (_inLoadoutDiagnosticDrain.Value) throw new InvalidOperationException("A diagnostic reporter cannot await its own session settlement.");
        lock (_gate)
        {
            if (_active is not null && ReferenceEquals(_configurationCallback.Value, _active))
                throw new InvalidOperationException("An active configuration callback cannot await its own settlement.");
            if (_inputSubmission is not null && ReferenceEquals(_inputCallback.Value, _inputSubmission))
                throw new InvalidOperationException("An active input callback cannot await its own settlement.");
        }
    }
    private void ThrowInputMutation()
    {
        if (_active is null) ThrowUserBashMutationLocked();
        if (_queueValidationCallback.Value)
            throw new InvalidOperationException("Queue preflight cannot mutate pending queues.");
    }
    private static string Identity(Func<string> nextEntryId, string headerId, ImmutableArray<SessionEntry> entries)
    {
        // session-manager.ts generateId: up to 100 draws of a short id that is not in use yet, then a full UUID.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            string id;
            try { id = nextEntryId(); } catch (Exception) { throw Error(PersistentAgentSessionFailure.InvalidCommit); }
            if (string.IsNullOrWhiteSpace(id)) throw Error(PersistentAgentSessionFailure.InvalidCommit);
            if (id != headerId && !entries.Any(entry => entry.Id == id)) return id;
        }
        return Guid.NewGuid().ToString("D");
    }
    /// <summary>session-manager.ts entry timestamps: <c>new Date().toISOString()</c> (UTC, milliseconds, Z).</summary>
    internal const string IsoTimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    private static SessionEntry Record(SessionEntryCodec codec, string type, string id, string? parentId,
        Func<long> clock, Action<Utf8JsonWriter> fields, string timestampFormat = IsoTimestampFormat)
    {
        string timestamp;
        try { timestamp = DateTimeOffset.FromUnixTimeMilliseconds(clock()).ToString(timestampFormat, CultureInfo.InvariantCulture); }
        catch (Exception) { throw Error(PersistentAgentSessionFailure.InvalidCommit); }
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject(); writer.WriteString("type", type);
            // session-manager.ts appendCustomEntry and appendCustomMessageEntry build { type, customType, ..., id, parentId, timestamp };
            // every other entry starts with its envelope.
            var envelopeLast = type is "custom" or "custom_message";
            if (!envelopeLast) { writer.WriteString("id", id); writer.WriteString("parentId", parentId); writer.WriteString("timestamp", timestamp); }
            fields(writer);
            if (envelopeLast) { writer.WriteString("id", id); writer.WriteString("parentId", parentId); writer.WriteString("timestamp", timestamp); }
            writer.WriteEndObject();
        }
        return codec.Parse(Encoding.UTF8.GetString(bytes.ToArray()));
    }
    private static async Task CloseAfterAdmissionFailureAsync(NativeAgent agent, SessionLogStore? store)
    {
        try { await agent.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
        if (store is not null) try { await store.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
    }
    private static PersistentAgentSessionException Error(PersistentAgentSessionFailure failure) => new(new(failure));
}
