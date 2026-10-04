using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Contracts;

namespace PiSharp.Agent;

public sealed record AgentHooks(
    Func<AgentLoopSnapshot, CancellationToken, ValueTask<ChatRequest>>? PrepareRequest = null,
    Func<AgentLoopTurn, CancellationToken, ValueTask<ImmutableArray<TranscriptEntry>>>? PrepareNextTurn = null,
    Func<AgentLoopTurn, CancellationToken, ValueTask>? FinishTurn = null,
    Func<AgentLoopTurn, CancellationToken, ValueTask<AgentLoopFinishAction>>? FinishTurnDecision = null)
{
    public Func<AgentPromptStart, CancellationToken, ValueTask<AgentPromptPreparation>>? BeforePrompt { get; init; }
    /// <summary>Explicit request-only context projection after canonical request preparation. Never edits history.</summary>
    public Func<ImmutableArray<TranscriptEntry>, CancellationToken, ValueTask<ImmutableArray<TranscriptEntry>>>? TransformRequestMessages { get; init; }
    /// <summary>Final request-only projection after context and forced-system transformations.</summary>
    public Func<ImmutableArray<TranscriptEntry>, CancellationToken, ValueTask<ImmutableArray<TranscriptEntry>>>? FinalTransformRequestMessages { get; init; }
    public Func<AgentRequestBoundary, CancellationToken, ValueTask<AgentRequestPreparation?>>? PrepareRequestBoundary { get; init; }
}
public sealed record AgentConfiguration(ModelDescriptor Model, IChatTransport Transport, ImmutableArray<ToolDefinition> Tools,
    IToolHooks? ToolHooks = null, ToolExecutionMode ExecutionMode = ToolExecutionMode.Parallel, AgentHooks? Hooks = null);
public enum AgentCancellationBehavior { Propagate, SettleAborted }
public sealed record AgentOptions(AgentLoopOptions? Loop = null, AgentPendingInputQueueOptions? Queue = null,
    int MaximumTools = 128, int StreamCapacity = 32, int MaximumSubscribers = 128,
    AgentCancellationBehavior CancellationBehavior = AgentCancellationBehavior.SettleAborted,
    ToolProgressDeliveryOptions? ProgressDelivery = null)
{
    public ToolResultValueOptions ResultValues { get; init; } = ToolResultValueOptions.ExecutionBoundary;
}
public enum AgentFailureKind { RunFault, Canceled, ChatFailure }
public sealed record AgentSnapshot(long Generation, ModelDescriptor Model, ImmutableArray<ToolDefinition> Tools,
    ImmutableArray<TranscriptEntry> Messages, ImmutableArray<TranscriptEntry> PendingInputs,
    ImmutableArray<ToolOutcome> CompletedToolOutcomes, StreamEvent? LatestStreamObservation,
    int SteeringCount, int FollowUpCount, bool IsRunning, bool IsDisposed,
    AgentFailureKind? Failure, bool CancellationCallbackFailed, bool CancellationRequested = false);

/// <summary>Exclusive, in-memory high-level lifecycle over the accepted loop, turns and pending-input queue.</summary>
public sealed class Agent : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly AsyncLocal<long?> _callbackGeneration = new();
    private readonly Func<long> _clock;
    private readonly IAgentEventSink _sink;
    private readonly AgentOptions _options;
    private readonly AgentLoopOptions _loopOptions;
    private readonly ToolProgressDeliveryOptions _progressOptions;
    private readonly AgentPendingInputQueue _queue;
    private AgentConfiguration _configuration;
    private ImmutableArray<TranscriptEntry> _messages = [];
    private ImmutableArray<TranscriptEntry> _retainedPending = [];
    private ImmutableArray<ToolOutcome> _outcomes = [];
    private StreamEvent? _latest;
    private AgentFailureKind? _failure;
    private bool _cancellationCallbackFailed;
    private bool _cancellationRequested;
    private long _generation;
    private Run? _active;
    private bool _disposed;
    private Task? _disposal;
    private ImmutableArray<Subscription> _subscriptions = [];
    private sealed class Subscription(IAgentEventSink sink) { public readonly IAgentEventSink Sink = sink; }
    private sealed class SubscriptionLease(Agent owner, Subscription subscription) : IDisposable
    {
        private Agent? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Unsubscribe(subscription);
    }
    private sealed class Run(long generation, AgentConfiguration configuration, ImmutableArray<TranscriptEntry> history,
        ImmutableArray<TranscriptEntry> inputs, bool skipInitialSteering, bool continuation)
    {
        public readonly bool Continuation = continuation;
        public readonly long Generation = generation;
        public readonly AgentConfiguration Configuration = configuration;
        public readonly ImmutableArray<TranscriptEntry> History = history;
        public readonly ImmutableArray<TranscriptEntry> Inputs = inputs;
        public readonly List<TranscriptEntry> Pending = [.. inputs];
        public readonly CancellationTokenSource Cancellation = new();
        public readonly TaskCompletionSource<AgentLoopResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration;
        public bool SkipInitialSteering = skipInitialSteering;
        public long Timestamp;
        public int CancelUsers;
        public bool Done, Settling, CallbackFailed;
        public AgentLoopResult? Result;
        public Exception? Error;
    }

    public Agent(AgentConfiguration configuration, Func<long> clock, IAgentEventSink sink, AgentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sink);
        _options = options ?? new();
        ArgumentNullException.ThrowIfNull(_options.ResultValues);
        if (_options.MaximumTools <= 0 || _options.StreamCapacity <= 0 || _options.MaximumSubscribers <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (_options.CancellationBehavior is not (AgentCancellationBehavior.Propagate or AgentCancellationBehavior.SettleAborted))
            throw new ArgumentOutOfRangeException(nameof(options));
        _progressOptions = _options.ProgressDelivery ?? new(Mode: ToolProgressDeliveryMode.SourceCompatible);
        _progressOptions.Validate();
        _loopOptions = _options.Loop ?? new();
        if (_loopOptions.MaximumTurns <= 0 || _loopOptions.MaximumTranscriptMessages <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        _queue = new(_options.Queue);
        _configuration = ValidateConfiguration(configuration);
        _clock = clock;
        _sink = sink;
    }

    public AgentSnapshot Snapshot
    {
        get
        {
            lock (_gate) return new(_generation, _configuration.Model, _configuration.Tools, _messages,
                _active is { } run ? run.Pending.ToImmutableArray() : _retainedPending, _outcomes, _latest,
                _queue.SteeringCount, _queue.FollowUpCount, _active is not null, _disposed, _failure, _cancellationCallbackFailed,
                _active?.Cancellation.IsCancellationRequested ?? _cancellationRequested);
        }
    }
    public AgentPendingInputMode SteeringMode
    {
        get { lock (_gate) return _queue.SteeringMode; }
        set { lock (_gate) { ThrowDisposed(); _queue.SteeringMode = value; } }
    }
    public AgentPendingInputMode FollowUpMode
    {
        get { lock (_gate) return _queue.FollowUpMode; }
        set { lock (_gate) { ThrowDisposed(); _queue.FollowUpMode = value; } }
    }
    public void Configure(AgentConfiguration configuration)
    { lock (_gate) { ThrowIdle(); _configuration = ValidateConfiguration(configuration); } }
    /// <summary>Validates both values, then publishes configuration and owned history under one idle state boundary.</summary>
    public void ConfigureAndReplaceMessages(AgentConfiguration configuration, ImmutableArray<TranscriptEntry> messages)
    {
        lock (_gate)
        {
            ThrowIdle();
            var admitted = ValidateConfiguration(configuration);
            ValidateMessages(messages, inputsOnly: false);
            _configuration = admitted;
            _messages = messages;
            _failure = null;
        }
    }
    public void ReplaceMessages(ImmutableArray<TranscriptEntry> messages)
    {
        lock (_gate)
        {
            ThrowIdle();
            ValidateMessages(messages, inputsOnly: false);
            _messages = messages;
            _failure = null;
        }
    }
    public void Steer(TranscriptEntry message, CancellationToken cancellationToken = default)
    { lock (_gate) { ThrowDisposed(); _queue.EnqueueSteering(message, cancellationToken); } }
    public void FollowUp(TranscriptEntry message, CancellationToken cancellationToken = default)
    { lock (_gate) { ThrowDisposed(); _queue.EnqueueFollowUp(message, cancellationToken); } }
    public ImmutableArray<TranscriptEntry> PeekQueuedMessages()
    { lock (_gate) return _queue.PeekQueuedMessages(); }
    public void ClearQueues()
    { lock (_gate) { ThrowDisposed(); _queue.ClearAll(); } }
    /// <summary>Full independent queues; excludes inputs already drained into the active generation.</summary>
    public AgentPendingInputQueueSnapshot GetPendingInputQueueSnapshot(CancellationToken cancellationToken = default)
    { lock (_gate) return _queue.GetSnapshot(cancellationToken); }
    /// <summary>Atomically returns and removes both full queued FIFOs without canceling active work.</summary>
    public AgentPendingInputQueueSnapshot ClearPendingInputQueues(CancellationToken cancellationToken = default)
    { lock (_gate) { ThrowDisposed(); return _queue.ClearAndSnapshot(cancellationToken); } }
    /// <summary>Atomically removes the exact captured queue state; stale/foreign snapshots remove nothing.</summary>
    public bool TryClearPendingInputQueues(AgentPendingInputQueueSnapshot expected,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AgentPendingInputQueueSnapshot? removed,
        CancellationToken cancellationToken = default)
    { lock (_gate) { ThrowDisposed(); return _queue.TryClearAndSnapshot(expected, out removed, cancellationToken); } }
    /// <summary>Registers an awaited sink; disposing its lease removes it from subsequent event snapshots.</summary>
    public IDisposable Subscribe(IAgentEventSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_gate)
        {
            ThrowDisposed();
            if (_subscriptions.Length >= _options.MaximumSubscribers)
                throw new InvalidOperationException("Agent subscriber limit reached.");
            var subscription = new Subscription(sink);
            var lease = new SubscriptionLease(this, subscription);
            _subscriptions = _subscriptions.Add(subscription);
            return lease;
        }
    }
    private void Unsubscribe(Subscription subscription)
    { lock (_gate) _subscriptions = _subscriptions.Remove(subscription); }
    public ImmutableArray<TranscriptEntry> TakePendingInputs()
    {
        lock (_gate)
        {
            ThrowIdle();
            var retained = _retainedPending;
            _retainedPending = [];
            return retained;
        }
    }
    public Task<AgentLoopResult> PromptAsync(TranscriptEntry message, CancellationToken cancellationToken = default) =>
        PromptAsync([message], cancellationToken);
    public Task<AgentLoopResult> PromptAsync(ImmutableArray<TranscriptEntry> messages, CancellationToken cancellationToken = default) =>
        Start(messages, continuation: false, cancellationToken);
    public Task<AgentLoopResult> ContinueAsync(CancellationToken cancellationToken = default) =>
        Start([], continuation: true, cancellationToken);

    private Task<AgentLoopResult> Start(ImmutableArray<TranscriptEntry> inputs, bool continuation, CancellationToken token)
    {
        Run run;
        lock (_gate)
        {
            ThrowIdle();
            token.ThrowIfCancellationRequested();
            if (!_retainedPending.IsEmpty) throw new InvalidOperationException("Recover uncommitted inputs before starting another run.");
            ValidateMessages(inputs, inputsOnly: true);
            var skipSteering = false;
            if (continuation)
            {
                if (_messages.IsEmpty || _messages.All(message => message.Role == "system"))
                    throw new InvalidOperationException("No messages to continue from.");
                if (_messages[^1].Role == "assistant")
                {
                    inputs = _queue.PeekSteering();
                    if (!inputs.IsEmpty) skipSteering = true;
                    else inputs = _queue.PeekFollowUp();
                    if (inputs.IsEmpty) throw new InvalidOperationException("Assistant-tailed continuation requires queued input.");
                }
                else if (_messages[^1].Role is not ("user" or "toolResult" or "custom"))
                    throw new InvalidOperationException("Unsupported continuation tail.");
            }
            else if (inputs.IsEmpty) throw new ArgumentException("Prompt inputs must not be empty.", nameof(inputs));
            if ((long)_messages.Length + inputs.Length > _loopOptions.MaximumTranscriptMessages)
                throw new ArgumentException("Prompt history exceeds the transcript limit.", nameof(inputs));
            var generation = checked(_generation + 1);
            run = new(generation, _configuration, _messages, inputs, skipSteering, continuation);
            if (continuation && !inputs.IsEmpty)
            {
                try { _ = skipSteering ? _queue.DrainSteering(token) : _queue.DrainFollowUp(token); }
                catch { run.Cancellation.Dispose(); throw; }
            }
            _generation = generation;
            _active = run;
            _failure = null;
            _latest = null;
            _outcomes = [];
            _cancellationCallbackFailed = false;
            _cancellationRequested = false;
        }
        // Registration and all user code run outside the state lock.
        try
        {
            run.Registration = token.UnsafeRegister(_ => RequestAbort(run), null);
            _ = ExecuteAsync(run);
        }
        catch (Exception error)
        {
            run.Error = error;
            bool settle;
            lock (_gate) { run.Done = true; settle = BeginSettlement(run); }
            if (settle) Settle(run);
        }
        return run.Completion.Task;
    }

    private Func<ImmutableArray<TranscriptEntry>, CancellationToken, ValueTask<ImmutableArray<TranscriptEntry>>>? _continuationProjection;

    private async Task ExecuteAsync(Run run)
    {
        var token = run.Cancellation.Token;
        var priorGeneration = _callbackGeneration.Value;
        _callbackGeneration.Value = run.Generation;
        try
        {
            var config = run.Configuration;
            var hooks = config.Hooks ?? new();
            var inputs = run.Inputs;
            var afterContext = run.Continuation ? _continuationProjection : null;
            if (!run.Continuation)
            {
                _continuationProjection = null;
                if (hooks.BeforePrompt is { } before)
                {
                    var prepared = await InCallbackAsync(run, () => before(new(run.History, inputs, _clock()), token)).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    ArgumentNullException.ThrowIfNull(prepared);
                    AgentLoopRunner.ValidateRequestMessages(prepared.AdditionalMessages, _options.ResultValues, _loopOptions.MaximumTranscriptMessages);
                    if (prepared.AdditionalMessages.Any(message => message.Role != "custom"))
                        throw new ArgumentException("Prompt preparation may append only custom messages.");
                    if ((long)run.History.Length + inputs.Length + prepared.AdditionalMessages.Length > _loopOptions.MaximumTranscriptMessages)
                        throw new ArgumentException("Prepared prompt exceeds the transcript limit.");
                    inputs = inputs.AddRange(prepared.AdditionalMessages);
                    lock (_gate) run.Pending.AddRange(prepared.AdditionalMessages);
                    afterContext = prepared.AfterContext;
                }
                _continuationProjection = afterContext;
            }
            async ValueTask<ImmutableArray<TranscriptEntry>> Project(ImmutableArray<TranscriptEntry> messages, CancellationToken cancellation)
            {
                var result = hooks.TransformRequestMessages is { } transform
                    ? await InCallbackAsync(run, () => transform(messages, cancellation)).ConfigureAwait(false) : messages;
                cancellation.ThrowIfCancellationRequested();
                if (afterContext is not null) result = await InCallbackAsync(run, () => afterContext(result, cancellation)).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                return hooks.FinalTransformRequestMessages is not { } final ? result :
                    await InCallbackAsync(run, () => final(result, cancellation)).ConfigureAwait(false);
            }
            var turn = new TurnRunner(new ChatClient(config.Transport, _options.StreamCapacity),
                new ToolBatchScheduler(config.Tools, config.ToolHooks, config.ExecutionMode, _progressOptions, _options.ResultValues));
            async ValueTask<AgentLoopRequestPreparation?> PrepareBoundary(AgentRequestBoundary boundary, CancellationToken cancellation, int attempt = 0)
            {
                if (attempt >= 16) throw new InvalidOperationException("Request boundary revision retry limit exceeded.");
                if (hooks.PrepareRequestBoundary is not { } prepare) return null;
                var update = await InCallbackAsync(run, () => prepare(boundary, cancellation)).ConfigureAwait(false);
                if (update is null) return null;
                var admitted = ValidateConfiguration(update.Configuration);
                if (admitted.Model != config.Model || !ReferenceEquals(admitted.Transport, config.Transport))
                    throw new ArgumentException("Request boundaries retain model and transport.");
                ValidateMessages(update.AdditionalSystemMessages, inputsOnly: true);
                var projectedInputs = update.ProjectedPendingInputs ?? boundary.PendingInputs;
                ValidateMessages(projectedInputs, inputsOnly: false);
                if (projectedInputs.Length != boundary.PendingInputs.Length) throw new ArgumentException("Boundary input count changed.");
                for (var index = 0; index < projectedInputs.Length; index++)
                {
                    var original = boundary.PendingInputs[index]; var projected = projectedInputs[index];
                    if (original.Role != projected.Role || (original.Role != "system" ? !ReferenceEquals(original, projected) :
                        SystemContent(original) != SystemContent(projected)))
                        throw new ArgumentException("Boundary changed an admitted input beyond system tool intent.");
                }
                if (update.AdditionalSystemMessages.Any(message => message.Role != "system") ||
                    (long)boundary.Snapshot.Transcript.Length + boundary.PendingInputs.Length + update.AdditionalSystemMessages.Length + 1 > _loopOptions.MaximumTranscriptMessages)
                    throw new ArgumentException("Invalid boundary system update.");
                var nextRunner = new TurnRunner(new ChatClient(admitted.Transport, _options.StreamCapacity),
                    new ToolBatchScheduler(admitted.Tools, admitted.ToolHooks, admitted.ExecutionMode, _progressOptions, _options.ResultValues));
                cancellation.ThrowIfCancellationRequested();
                var published = false;
                try { await InCallbackAsync(run, () => update.PublishAsync(() =>
                {
                    lock (_gate)
                    {
                        if (published || !ReferenceEquals(_active, run)) throw new InvalidOperationException("Boundary publication lost its run.");
                        _configuration = admitted; _messages = _messages.AddRange(update.AdditionalSystemMessages);
                        for (var index = 0; index < projectedInputs.Length; index++)
                            if (!ReferenceEquals(projectedInputs[index], boundary.PendingInputs[index]))
                            {
                                var slot = run.Pending.IndexOf(boundary.PendingInputs[index]);
                                if (slot >= 0) run.Pending[slot] = projectedInputs[index];
                            }
                        config = admitted; hooks = admitted.Hooks ?? new(); published = true;
                    }
                }, cancellation)).ConfigureAwait(false); }
                catch (AgentRequestBoundaryStaleException) when (!published)
                { return await PrepareBoundary(boundary, cancellation, attempt + 1).ConfigureAwait(false); }
                if (!published) throw new InvalidOperationException("Boundary host returned without publication.");
                return new(nextRunner, update.AdditionalSystemMessages, projectedInputs);
                static string SystemContent(TranscriptEntry message) => JsonSerializer.Serialize(message.WireBody.Value.EnumerateObject()
                    .Where(property => property.Name is not ("toolsAdded" or "toolsRemoved"))
                    .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal));
            }
            var runner = new AgentLoopRunner(turn, config.Model, () =>
            {
                var prior = _callbackGeneration.Value; _callbackGeneration.Value = run.Generation;
                try { run.Timestamp = _clock(); return run.Timestamp; }
                finally { _callbackGeneration.Value = prior; }
            }, _loopOptions);
            var callbacks = new AgentLoopCallbacks(
                (snapshot, cancellation) => hooks.PrepareRequest is { } prepare
                    ? InCallbackAsync(run, () => prepare(snapshot, cancellation))
                    : ValueTask.FromResult(new ChatRequest(snapshot.Model, snapshot.Transcript)),
                hooks.PrepareNextTurn is { } next ? (completed, cancellation) => PrepareNextAsync(run, next, completed, cancellation) : null,
                cancellation => Poll(run, steering: true, cancellation), cancellation => Poll(run, steering: false, cancellation),
                hooks.FinishTurn is { } finish ? (completed, cancellation) => InCallbackAsync(run, () => finish(completed, cancellation)) : null,
                hooks.FinishTurnDecision is { } decision ? (completed, cancellation) => InCallbackAsync(run, () => decision(completed, cancellation)) : null)
            {
                TransformRequestMessages = Project, PrepareRequestBoundary = (boundary, cancellation) => PrepareBoundary(boundary, cancellation)
            };
            var eventSink = new EventSink((observation, cancellation) => CommitAndEmitAsync(run, observation, cancellation));
            run.Result = _options.CancellationBehavior == AgentCancellationBehavior.SettleAborted ?
                await runner.RunWithContextAndAbortSettlementAsync(run.History, inputs, callbacks, eventSink, token).ConfigureAwait(false) :
                await runner.RunWithContextAsync(run.History, inputs, callbacks, eventSink, token).ConfigureAwait(false);
        }
        catch (Exception error) { run.Error = error; }
        finally
        {
            bool settle;
            lock (_gate) { run.Done = true; settle = BeginSettlement(run); }
            if (settle) Settle(run);
            _callbackGeneration.Value = priorGeneration;
        }
    }
    private ValueTask<ImmutableArray<TranscriptEntry>> Poll(Run run, bool steering, CancellationToken token)
    {
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            if (steering && run.SkipInitialSteering) { run.SkipInitialSteering = false; return ValueTask.FromResult(ImmutableArray<TranscriptEntry>.Empty); }
            var inputs = steering ? _queue.DrainSteering(token) : _queue.DrainFollowUp(token);
            run.Pending.AddRange(inputs);
            return ValueTask.FromResult(inputs);
        }
    }
    private async ValueTask CommitAndEmitAsync(Run run, AgentEvent observation, CancellationToken token)
    {
        ImmutableArray<Subscription> subscriptions;
        var entry = observation switch
        {
            AgentLoopInputMessageEnded input => input.Message,
            AssistantMessageEnded assistant => new TranscriptEntry("assistant", PiWireJson.WriteMessage(assistant.Message)),
            ToolResultMessageEnded tool => ToolEntry(tool.Message, run.Timestamp),
            _ => null
        };
        lock (_gate)
        {
            if (!ReferenceEquals(_active, run)) throw new InvalidOperationException("Agent generation is no longer active.");
            if (entry is not null)
            {
                _messages = _messages.Add(entry);
                if (observation is AgentLoopInputMessageEnded) run.Pending.Remove(entry);
            }
            if (observation is ToolExecutionEnded completed) _outcomes = _outcomes.Add(completed.Outcome);
            if (observation is TurnStreamObserved stream) _latest = stream.Event;
            if (observation is AssistantMessageEnded or AgentLoopEnded) _latest = null;
            subscriptions = _subscriptions;
        }
        await InCallbackAsync(run, () => _sink.EmitAsync(observation, token)).ConfigureAwait(false);
        var listenerToken = _progressOptions.Mode == ToolProgressDeliveryMode.SourceCompatible ? run.Cancellation.Token : token;
        foreach (var subscription in subscriptions)
            await InCallbackAsync(run, () => subscription.Sink.EmitAsync(observation, listenerToken)).ConfigureAwait(false);
    }
    private async ValueTask<ImmutableArray<TranscriptEntry>> PrepareNextAsync(Run run,
        Func<AgentLoopTurn, CancellationToken, ValueTask<ImmutableArray<TranscriptEntry>>> prepare,
        AgentLoopTurn completed, CancellationToken token)
    {
        var prepared = await InCallbackAsync(run, () => prepare(completed, token)).ConfigureAwait(false);
        ValidateMessages(prepared, inputsOnly: true);
        lock (_gate) run.Pending.InsertRange(0, prepared);
        return prepared;
    }
    private async ValueTask<T> InCallbackAsync<T>(Run run, Func<ValueTask<T>> callback)
    {
        var prior = _callbackGeneration.Value;
        _callbackGeneration.Value = run.Generation;
        try { return await callback().ConfigureAwait(false); }
        finally { _callbackGeneration.Value = prior; }
    }
    private async ValueTask InCallbackAsync(Run run, Func<ValueTask> callback)
    {
        var prior = _callbackGeneration.Value;
        _callbackGeneration.Value = run.Generation;
        try { await callback().ConfigureAwait(false); }
        finally { _callbackGeneration.Value = prior; }
    }
    public bool Abort()
    {
        Run? run;
        lock (_gate) run = _active;
        return run is not null && RequestAbort(run);
    }
    private bool RequestAbort(Run run)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_active, run) || run.Settling) return false;
            run.CancelUsers++;
        }
        var priorGeneration = _callbackGeneration.Value;
        _callbackGeneration.Value = run.Generation;
        try { run.Cancellation.Cancel(); }
        catch (Exception) { lock (_gate) { run.CallbackFailed = true; _cancellationCallbackFailed = true; } }
        finally
        {
            bool settle;
            lock (_gate) { run.CancelUsers--; settle = BeginSettlement(run); }
            if (settle) Settle(run);
            _callbackGeneration.Value = priorGeneration;
        }
        return true;
    }
    private static bool BeginSettlement(Run run)
    {
        if (!run.Done || run.CancelUsers != 0 || run.Settling) return false;
        run.Settling = true;
        return true;
    }
    private void Settle(Run run)
    {
        var token = run.Cancellation.Token;
        try { run.Registration.Dispose(); }
        catch (Exception error) { run.Error ??= error; }
        finally { run.Cancellation.Dispose(); }
        lock (_gate)
        {
            _retainedPending = run.Pending.ToImmutableArray();
            _failure = run.Error is OperationCanceledException && token.IsCancellationRequested ? AgentFailureKind.Canceled :
                run.Error is not null ? AgentFailureKind.RunFault :
                run.Result?.Reason == AgentLoopStopReason.ChatFailure ?
                    (token.IsCancellationRequested || run.Result.Turns.Any(turn => turn.Result.Chat.Message.StopReason == StopReason.Aborted)
                        ? AgentFailureKind.Canceled : AgentFailureKind.ChatFailure) : null;
            _cancellationCallbackFailed = run.CallbackFailed;
            _cancellationRequested = token.IsCancellationRequested;
            _latest = null;
            _active = null;
        }
        if (run.Error is OperationCanceledException && token.IsCancellationRequested) run.Completion.TrySetCanceled(token);
        else if (run.Error is { } error) run.Completion.TrySetException(error);
        else run.Completion.TrySetResult(run.Result!);
        run.Idle.TrySetResult();
    }
    public Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        Task idle;
        lock (_gate) { ThrowSelfWait(); idle = _active?.Idle.Task ?? Task.CompletedTask; }
        return cancellationToken.CanBeCanceled ? idle.WaitAsync(cancellationToken) : idle;
    }
    public ValueTask DisposeAsync()
    {
        Run? run;
        TaskCompletionSource completion;
        lock (_gate)
        {
            ThrowSelfWait();
            if (_disposal is not null) return new(_disposal);
            _disposed = true;
            run = _active;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposal = completion.Task;
        }
        _ = DisposeCoreAsync(run, completion);
        return new(completion.Task);
    }
    private async Task DisposeCoreAsync(Run? run, TaskCompletionSource completion)
    {
        try
        {
            if (run is not null) { RequestAbort(run); await run.Idle.Task.ConfigureAwait(false); }
            lock (_gate) _subscriptions = [];
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }
    private AgentConfiguration ValidateConfiguration(AgentConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configuration.Model);
        ArgumentNullException.ThrowIfNull(configuration.Transport);
        if (string.IsNullOrWhiteSpace(configuration.Model.Id) || string.IsNullOrWhiteSpace(configuration.Model.Api) ||
            string.IsNullOrWhiteSpace(configuration.Model.Provider) || configuration.Tools.IsDefault ||
            configuration.Tools.Length > _options.MaximumTools) throw new ArgumentException("Invalid Agent configuration.", nameof(configuration));
        _ = new ToolBatchScheduler(configuration.Tools, configuration.ToolHooks, configuration.ExecutionMode, resultOptions: _options.ResultValues);
        return configuration;
    }
    private void ValidateMessages(ImmutableArray<TranscriptEntry> messages, bool inputsOnly)
    {
        if (messages.IsDefault || messages.Length > _loopOptions.MaximumTranscriptMessages) throw new ArgumentException("Invalid Agent messages.", nameof(messages));
        foreach (var message in messages)
            if (message is null || message.WireBody is null || message.WireBody.Value.ValueKind != JsonValueKind.Object ||
                (inputsOnly ? message.Role is not ("system" or "user") : message.Role is not ("system" or "user" or "assistant" or "toolResult" or "custom")) ||
                !message.WireBody.Value.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String || role.GetString() != message.Role)
                throw new ArgumentException("Invalid Agent message role or ownership.", nameof(messages));
    }
    private void ThrowDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(Agent)); }
    private void ThrowIdle() { ThrowDisposed(); if (_active is not null) throw new InvalidOperationException("Agent is already processing."); }
    private void ThrowSelfWait()
    {
        if (_active is { } run && _callbackGeneration.Value == run.Generation)
            throw new InvalidOperationException("An active Agent callback cannot await its own settlement.");
    }
    private static TranscriptEntry ToolEntry(ToolResultMessage message, long timestamp) => ToolResultMessageMaterializer.ToTranscript(message, timestamp);
    private sealed class EventSink(Func<AgentEvent, CancellationToken, ValueTask> emit) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken cancellationToken) => emit(observation, cancellationToken); }
}
