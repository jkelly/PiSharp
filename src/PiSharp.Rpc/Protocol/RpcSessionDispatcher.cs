// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session.ts (agent_settled).
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Rpc.Ui;
using System.Text;
using PiSharp.Sessions.Tree;
using PiSharp.Sessions.Storage;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Lifecycle;
using PiSharp.Sessions.Context;

namespace PiSharp.Rpc.Protocol;

/// <summary>Exclusive fixed-session RPC admission over actual durable execution and one owned awaited output writer.</summary>
public sealed partial class RpcSessionDispatcher : IAsyncDisposable
{
    private readonly Func<SessionTreeNavigationReceipt, string?, ValueTask>? _selectedTreePublisher;
    private ImmutableArray<RpcSessionTreePublicationException> _sessionTreePublicationFailures = [];
    /// <summary>Original postcommit publication failures, retained after wire error mapping and retirement.</summary>
    public ImmutableArray<RpcSessionTreePublicationException> SessionTreePublicationFailures
    { get { lock (_gate) return _sessionTreePublicationFailures; } }
    private PersistentAgentSession _session;
    private readonly SemaphoreSlim _projectionGate = new(1, 1);
    private readonly ReplaceableAgentSession? _sessionOwner;
    private readonly Func<CancellationToken, ValueTask>? _sessionStartup;
    private readonly Func<PersistentAgentSession, Task>? _postInputSettlement, _postRunSettlement;
    private sealed record PostOriginEvidence(string Phase, Task? Original, AggregateException? Aggregate, Exception? Direct);
    private readonly List<TaskCompletionSource<PostOriginEvidence>> _postOriginSlots = [];
    public (string Phase, Exception Direct)[] CapturedPostOriginSynchronousFailures
    {
        get { lock (_gate) return _postOriginSlots.Where(slot => slot.Task.IsCompletedSuccessfully)
            .Select(slot => slot.Task.Result).Where(row => row.Original is null && row.Direct is not null)
            .Select(row => (row.Phase, row.Direct!)).ToArray(); }
    }
    private readonly Dictionary<Task, (string Phase, AggregateException? Aggregate, Exception? Direct)> _postOriginOriginals = new(ReferenceEqualityComparer.Instance);
    private int _activePostOrigins;
    private readonly HashSet<RpcCommandEnvelope> _postInputMovedToRun = new(ReferenceEqualityComparer.Instance);
    public (string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)[] CapturedPostOriginOriginals
    { get { lock (_gate) return _postOriginOriginals.Select(pair => (pair.Value.Phase, pair.Key, pair.Value.Aggregate, pair.Value.Direct)).ToArray(); } }
    private TaskCompletionSource<PostOriginEvidence>? ReservePostOrigin(Func<PersistentAgentSession, Task>? callback)
    {
        if (callback is null) return null;
        lock (_gate)
        {
            if (_postOriginSlots.Count >= 4096) throw new RpcDispatchException(RpcDispatchFailure.ResourceLimit);
            var slot = new TaskCompletionSource<PostOriginEvidence>(TaskCreationOptions.RunContinuationsAsynchronously);
            _postOriginSlots.Add(slot); return slot;
        }
    }
    private async Task JoinPostOrigin(string phase, Func<PersistentAgentSession, Task>? callback,
        PersistentAgentSession originating, TaskCompletionSource<PostOriginEvidence>? slot)
    {
        if (slot is null) return;
        Task? original = null; Exception? direct = null; AggregateException? aggregate = null;
        lock (_gate) _activePostOrigins++;
        try
        {
            try { original = callback!(originating) ?? throw new InvalidOperationException("Post-origin callback returned no Task."); }
            catch (OperationCanceledException error) { throw new InvalidOperationException("Synchronous post-origin callback failed.", error); }
            await original.ConfigureAwait(false);
        }
        catch (Exception error) { direct = error; }
        finally
        {
            lock (_gate)
            {
                if (original is not null && !_postOriginOriginals.ContainsKey(original))
                {
                    aggregate = original.IsFaulted ? original.Exception : null;
                    _postOriginOriginals.Add(original, (phase, aggregate, direct));
                }
                if (original is not null && _postOriginOriginals.TryGetValue(original, out var cached))
                { aggregate = cached.Aggregate; direct = cached.Direct; }
                slot.TrySetResult(new(phase, original, aggregate, direct)); // Evidence may contain a synchronous failure with no Task.
                _activePostOrigins--;
            }
        }
        if (direct is not null) SignalFatal(RpcDispatchFailure.SessionRunFailed, (Exception?)aggregate ?? direct);
    }
    private async Task JoinPostOriginShutdown(List<Exception> failures)
    {
        TaskCompletionSource<PostOriginEvidence>[] slots;
        lock (_gate) slots = _postOriginSlots.ToArray();
        foreach (var slot in slots)
        {
            var evidence = await slot.Task.ConfigureAwait(false);
            var original = evidence.Original;
            if (original is null)
            {
                if (evidence.Direct is not null) failures.Add(evidence.Direct);
                continue;
            }
            try { await original.ConfigureAwait(false); }
            catch (Exception error)
            {
                lock (_gate)
                    failures.Add(_postOriginOriginals.TryGetValue(original, out var cached)
                        ? (Exception?)cached.Aggregate ?? cached.Direct ?? error : error);
            }
        }
    }
    private readonly TaskCompletionSource _startupReady = NewGate();
    private Task? _startupWork;
    private RpcDispatchException? _startupFailure;
    private CancellationToken _startupToken;
    private readonly JsonlWriter _output;
    private readonly Func<long> _clock;
    private readonly ImmutableDictionary<ModelDescriptor, JsonData> _models;
    private readonly ImmutableArray<ModelDescriptor> _modelOrder;
    private readonly RpcModelRuntime? _modelRuntime;
    /// <summary>The host's SessionManager.open preparation of a session path before switch_session opens it (an empty file gets its
    /// header, a missing one becomes a new session); an InvalidDataException refuses the switch with its message. The returned undo
    /// runs when the switch does not happen (an extension veto), so a cancelled switch leaves the path as it was.</summary>
    private readonly Func<string, CancellationToken, ValueTask<Func<ValueTask>?>>? _prepareSessionPath;
    private readonly Func<ModelDescriptor, PiSharp.Sessions.Compaction.SessionCompactionSettings?>? _compactionSettings;
    /// <summary>The definition of a model: the startup definitions, then the host's current runtime models.</summary>
    private bool TryGetModel(ModelDescriptor model, out JsonData wire)
    {
        if (_models.TryGetValue(model, out wire!)) return true;
        if (_modelRuntime is not null)
            foreach (var definition in _modelRuntime.Available())
                if (definition.Model == model) { wire = definition.WireBody; return true; }
        wire = null!; return false;
    }
    private bool KnowsModel(ModelDescriptor model) => TryGetModel(model, out _);
    private readonly RpcDispatchOptions _options;
    private readonly RpcSessionOwnership _ownership;
    private readonly IPromptInputAdmission? _inputAdmission;
    private readonly Func<string, IPromptInputAdmission>? _inputAdmissionSelector;
    private readonly IRpcExtensionCommandCatalog? _commandCatalog;
    private readonly ISessionSummaryGenerator? _summaryGenerator;
    private readonly ToolInvoker? _exportHtmlWriter;
    private readonly PiSharp.CodingAgent.Export.SessionHtmlExportHost _htmlExport;
    private readonly double? _recoveryDesiredMaxOutput;
    private readonly RpcExtensionUiCoordinator? _ui;
    private readonly Queue<DeferredFrame> _deferred = new();
    private long _retainedCommandBytes;
    private sealed record DeferredFrame(JsonData? Record, string? ParseFailure, int Bytes);
    private IDisposable _subscription;
    private IDisposable? _operationSubscription;
    private readonly RpcAgentEventProjector _events;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _transitions = new(1, 1), _wire = new(1, 1), _queuePublication = new(1, 1);
    private readonly SemaphoreSlim _inputCommands = new(1, 1);
    private readonly CancellationTokenSource _stopInput = new();
    private readonly CancellationToken _stopInputToken;
    private readonly AsyncLocal<bool> _inCallback = new();
    private readonly AsyncLocal<bool> _inInputCallback = new();
    private readonly TaskCompletionSource _completion = NewGate();
    private readonly TaskCompletionSource<ShutdownSettlement> _shutdownSettlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _capacity = NewGate();
    private TaskCompletionSource? _pendingIdle;
    private AgentPendingInputQueueSnapshot _publishedQueue;
    private RunState? _run;
    private RunState? _startingInput;
    private PersistentAgentSession? _admittingSession;
    private JsonlReader? _input;
    private RpcDispatchException? _fatal;
    private int _pending, _readerClaimed;
    private bool _closed;
    private Task? _disposal;
    private readonly HashSet<ContextEditCommand> _contextEdits = [];
    private sealed class ContextEditCommand
    {
        internal readonly CancellationTokenSource Abort = new();
        internal int CancelUsers;
        internal TaskCompletionSource? CancelIdle;
    }
    private sealed class RunState(int historyLength)
    {
        public int HistoryLength = historyLength;
        public bool AbortRequested;
        public TaskCompletionSource Ready = NewGate();
        public readonly TaskCompletionSource Entered = NewGate();
        public readonly TaskCompletionSource Settled = NewGate();
        /// <summary>Set (under the gate) just before agent_settled is written: a client that saw it may prompt at once.</summary>
        public bool SettledPublished;
    }
    private sealed class EventSink(RpcSessionDispatcher owner) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) => owner.ObserveAsync(observation); }
    private sealed class OperationSink(RpcSessionDispatcher owner) : ISessionOperationEventSink
    { public ValueTask EmitAsync(SessionOperationEvent observation, CancellationToken token) => owner.ObserveMetadataOrOperationAsync(observation); }

    public RpcSessionDispatcher(PersistentAgentSession session, JsonlWriter output, Func<long> clock,
        ImmutableArray<RpcModelDefinition> models, RpcDispatchOptions? options = null,
        RpcSessionOwnership sessionOwnership = RpcSessionOwnership.Owned, IPromptInputAdmission? inputAdmission = null,
        RpcExtensionUiCoordinator? extensionUi = null, IRpcExtensionCommandCatalog? extensionCommandCatalog = null,
        ReplaceableAgentSession? sessionOwner = null, Func<CancellationToken, ValueTask>? sessionStartup = null,
        ISessionSummaryGenerator? summaryGenerator = null, double? recoveryDesiredMaxOutput = null,
        Func<string, IPromptInputAdmission>? inputAdmissionSelector = null,
        ToolInvoker? exportHtmlWriter = null, PiSharp.CodingAgent.Export.SessionHtmlExportHost? htmlExport = null,
        Func<SessionTreeNavigationReceipt, string?, ValueTask>? selectedTreePublisher = null,
        Func<PersistentAgentSession, Task>? postInputSettlement = null,
        Func<PersistentAgentSession, Task>? postRunSettlement = null,
        PiSharp.CodingAgent.Execution.IUserBashExecutor? userBash = null, RpcModelRuntime? modelRuntime = null,
        Func<ModelDescriptor, PiSharp.Sessions.Compaction.SessionCompactionSettings?>? compactionSettings = null,
        Func<string, CancellationToken, ValueTask<Func<ValueTask>?>>? prepareSessionPath = null)
    {
        _prepareSessionPath = prepareSessionPath;
        ArgumentNullException.ThrowIfNull(session); ArgumentNullException.ThrowIfNull(output); ArgumentNullException.ThrowIfNull(clock);
        if (selectedTreePublisher is not null && selectedTreePublisher.GetInvocationList().Length != 1)
            throw new ArgumentException("Selected tree publication requires one owned callback.", nameof(selectedTreePublisher));
        _options = options ?? new(); _options.Validate(sessionOwnership);
        if (recoveryDesiredMaxOutput is { } desired && (!double.IsFinite(desired) || desired <= 0))
            throw new ArgumentOutOfRangeException(nameof(recoveryDesiredMaxOutput));
        _session = session; _output = output; _clock = clock; _ownership = sessionOwnership; _inputAdmission = inputAdmission;
        _inputAdmissionSelector = inputAdmissionSelector;
        _ui = extensionUi;
        _commandCatalog = extensionCommandCatalog;
        _summaryGenerator = summaryGenerator;
        _exportHtmlWriter = exportHtmlWriter; _htmlExport = htmlExport ?? PiSharp.CodingAgent.Export.SessionHtmlExportHost.CreateDefault();
        _recoveryDesiredMaxOutput = recoveryDesiredMaxOutput;
        _sessionOwner = sessionOwner; _sessionStartup = sessionStartup;
        _selectedTreePublisher = selectedTreePublisher;
        if (postInputSettlement?.GetInvocationList().Length > 1 || postRunSettlement?.GetInvocationList().Length > 1)
            throw new ArgumentException("Each post-origin hook requires one admitted callback.");
        _postInputSettlement = postInputSettlement; _postRunSettlement = postRunSettlement;
        if (sessionOwner is not null && (!ReferenceEquals(sessionOwner.Current.Session, session) || sessionOwner.AttachmentChanged is not null))
            throw new ArgumentException("RPC requires the exclusive current session attachment.", nameof(sessionOwner));
        if (sessionStartup is null) _startupReady.TrySetResult();
        _modelRuntime = modelRuntime; _compactionSettings = compactionSettings;
        _models = RpcCommandCodec.Models(models, _options);
        _modelOrder = models.Select(value => value.Model).ToImmutableArray();
        if (_modelOrder.Select(value => (value.Provider, value.Id)).Distinct().Count() != _modelOrder.Length)
            throw new RpcDispatchException(RpcDispatchFailure.InvalidModelDefinition);
        if (!_models.ContainsKey(session.Snapshot.Agent.Model)) throw new RpcDispatchException(RpcDispatchFailure.InvalidModelDefinition);
        if (session.Snapshot.Agent.IsRunning || session.Snapshot.IsProcessingOperation || session.Snapshot.IsConfiguring || session.Snapshot.IsAdmittingInput || session.Snapshot.IsEditingContext || session.Snapshot.IsCompacting ||
            session.Snapshot.IsDisposed || session.Snapshot.Fault is not null)
            throw new ArgumentException("RPC requires an available idle session under exclusive host ownership.", nameof(session));
        _userBash = userBash; TryConfigureUserBash(session, required: true);
        _events = new(_options); _publishedQueue = session.GetPendingInputQueueSnapshot();
        _stopInputToken = _stopInput.Token;
        // Ownership transfers only after successful construction; subscription precedes all command acceptance.
        _subscription = session.Subscribe(new EventSink(this));
        try { _operationSubscription = session.SubscribeOperationEvents(new OperationSink(this)); _ui?.Attach(WriteUiAsync, (RpcDispatchFailure failure) => SignalFatal(failure), _options.MaximumOutputBytes); }
        catch { _operationSubscription?.Dispose(); _subscription.Dispose(); throw; }
        if (sessionOwner is not null) sessionOwner.AttachmentChanged = AttachmentChangedAsync;
    }

    private async ValueTask AttachmentChangedAsync(AgentSessionReplacement replacement)
    {
        // Queue records have no wire generation. Drain the old publisher before
        // changing authority, then deliver the switch before sampling the new queue.
        // Do not hold the admission transition across physical output.
        await _queuePublication.WaitAsync().ConfigureAwait(false);
        try
        {
            await _transitions.WaitAsync().ConfigureAwait(false);
            try
            {
                CheckOpen();
                TryConfigureUserBash(replacement.Current.Session, required: false);
                var subscription = replacement.Current.Session.Subscribe(new EventSink(this));
                IDisposable operationSubscription;
                try { operationSubscription = replacement.Current.Session.SubscribeOperationEvents(new OperationSink(this)); }
                catch { subscription.Dispose(); throw; }
                var previous = _subscription;
                var previousOperation = _operationSubscription;
                _session = replacement.Current.Session; _subscription = subscription;
                _navigation = null;
                _operationSubscription = operationSubscription;
                _publishedQueue = _session.GetPendingInputQueueSnapshot();
                previous.Dispose();
                previousOperation?.Dispose();
            }
            finally { _transitions.Release(); }
            await WriteAsync(RpcCommandCodec.Event("session_switched", writer =>
            {
                writer.WriteString("sessionFile", replacement.Current.Session.Path);
                writer.WriteString("sessionId", replacement.Current.Session.Snapshot.Log.Header.Id);
                writer.WriteNumber("generation", replacement.Current.Generation);
            }, _options)).ConfigureAwait(false);
        }
        finally { _queuePublication.Release(); }
    }

    private async Task StartLifecycleAsync(CancellationToken token)
    {
        _startupToken = token;
        try { await _sessionStartup!(token).ConfigureAwait(false); _startupReady.TrySetResult(); }
        catch (OperationCanceledException error) when (token.IsCancellationRequested && error.CancellationToken == token) { _startupReady.TrySetCanceled(token); }
        catch (Exception error)
        {
            _startupFailure = new(RpcDispatchFailure.SessionRunFailed, error);
            _startupReady.TrySetException(_startupFailure);
            SignalFatal(RpcDispatchFailure.SessionRunFailed, error);
        }
    }

    /// <summary>Settles on shared shutdown, including actual run, coordinator, output and stream cleanup.</summary>
    public Task Completion
    { get { lock (_gate) if (_activePostOrigins != 0) throw new InvalidOperationException("Completion cannot join an active post-origin callback."); return _completion.Task; } }
    /// <summary>Original operation failure and newly encountered cleanup failures, in shutdown order.</summary>
    public sealed record ShutdownSettlement(RpcDispatchException? OperationFailure,
        ImmutableArray<Exception> CleanupFailures, RpcDispatchException? CompletionFailure);

    /// <summary>Directly joins the original disposal task and reports its separately retained causes.</summary>
    public async ValueTask<ShutdownSettlement> SettleAsync()
    {
        try { await DisposeAsync().ConfigureAwait(false); }
        catch (RpcDispatchException error)
        {
            var settlement = await _shutdownSettlement.Task.ConfigureAwait(false);
            if (!ReferenceEquals(error, settlement.CompletionFailure)) throw;
            return settlement;
        }
        return await _shutdownSettlement.Task.ConfigureAwait(false);
    }
    /// <summary>Admits at most the configured number of concurrent calls. Prompt completion remains asynchronous.</summary>
    public Task SubmitAsync(JsonData command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command); cancellationToken.ThrowIfCancellationRequested();
        if ((_inCallback.Value || _session.IsRetryOwnedCallback) && command.Value.TryGetProperty("type", out var retryType) &&
            retryType.ValueKind == JsonValueKind.String && retryType.GetString() is "set_auto_retry" or "abort_retry")
            throw new InvalidOperationException("RPC retry event callbacks cannot await their own controls.");
        if (_inInputCallback.Value && command.Value.TryGetProperty("type", out var type) &&
            type.ValueKind == JsonValueKind.String && type.GetString() is "prompt" or "steer" or "follow_up")
            throw new InvalidOperationException("An input callback cannot await its own RPC input admission.");
        if (TryUiResponse(command)) return Task.CompletedTask;
        var bytes = _ui is null ? 0 : Encoding.UTF8.GetByteCount(command.ToString());
        var exhausted = false;
        lock (_gate)
        {
            ThrowOpen();
            if (_pending >= _options.MaximumConcurrentCommands) throw new RpcDispatchException(RpcDispatchFailure.ResourceLimit);
            if (_ui is not null && _retainedCommandBytes + bytes > _ui.Options.MaximumRetainedOrdinaryBytes)
                exhausted = true;
            else AddPending(bytes);
        }
        if (exhausted) { SignalFatal(RpcDispatchFailure.ResourceLimit); throw new RpcDispatchException(RpcDispatchFailure.ResourceLimit); }
        return HandleAsync(command, null, cancellationToken, bytes);
    }
    /// <summary>One input loop; continued reads and bounded command handling proceed independently of prompt settlement.</summary>
    public async Task RunAsync(JsonlReader input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (Interlocked.Exchange(ref _readerClaimed, 1) != 0) throw new InvalidOperationException("RPC dispatcher supports one input loop.");
        lock (_gate) { ThrowOpen(); _input = input; }
        CancellationToken inputToken = default;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopInputToken);
            inputToken = linked.Token;
            if (_sessionStartup is not null) _startupWork = StartLifecycleAsync(linked.Token);
            await foreach (var admission in input.ReadAdmissionsAsync(linked.Token).ConfigureAwait(false))
            {
                if (admission.Record is { } record && TryUiResponse(record)) continue;
                if (_ui is not null)
                {
                    var bytes = admission.Record is null ? Encoding.UTF8.GetByteCount(admission.FailureMessage ?? "") : Encoding.UTF8.GetByteCount(admission.Record.ToString());
                    var frame = new DeferredFrame(admission.Record, admission.FailureMessage, bytes); var start = false;
                    lock (_gate)
                    {
                        ThrowOpen();
                        if (_retainedCommandBytes + bytes > _ui.Options.MaximumRetainedOrdinaryBytes ||
                            _pending >= _options.MaximumConcurrentCommands && _deferred.Count >= _ui.Options.MaximumDeferredOrdinaryFrames)
                            throw new RpcDispatchException(RpcDispatchFailure.ResourceLimit);
                        if (_pending < _options.MaximumConcurrentCommands && _deferred.Count == 0)
                        { AddPending(bytes); start = true; }
                        else { _deferred.Enqueue(frame); _retainedCommandBytes += bytes; }
                    }
                    if (start) _ = ObserveCommandAsync(HandleAsync(frame.Record, frame.ParseFailure, CancellationToken.None, frame.Bytes));
                    continue;
                }
                while (true)
                {
                    Task capacity;
                    lock (_gate)
                    {
                        ThrowOpen();
                        if (_pending < _options.MaximumConcurrentCommands) { AddPending(); break; }
                        capacity = _capacity.Task;
                    }
                    await capacity.WaitAsync(linked.Token).ConfigureAwait(false);
                }
                // HandleAsync is observed separately; the next bounded input read need not wait for this response.
                _ = ObserveCommandAsync(HandleAsync(admission.Record, admission.FailureMessage, CancellationToken.None));
            }
        }
        catch (OperationCanceledException error) when (input.OwnsCancellation(error) ||
            inputToken.IsCancellationRequested && error.CancellationToken == inputToken) { }
        catch (RpcDispatchException) when (_stopInputToken.IsCancellationRequested) { }
        catch (RpcDispatchException error) { SignalFatal(error.Failure, error); }
        catch (Exception error) { SignalFatal(RpcDispatchFailure.SessionRunFailed, error); }
        finally { await BeginShutdown().ConfigureAwait(false); }
    }
    private static async Task ObserveCommandAsync(Task command)
    { try { await command.ConfigureAwait(false); } catch (Exception) { /* HandleAsync already records fatal output failures. */ } }

    private bool TryUiResponse(JsonData raw)
    {
        if (_ui is null || !RpcExtensionUiCodec.IsResponse(raw)) return false;
        lock (_gate) ThrowOpen();
        try { return _ui.AcceptResponse(raw); }
        catch (Exception) { SignalFatal(RpcDispatchFailure.ResourceLimit); throw new RpcDispatchException(RpcDispatchFailure.ResourceLimit); }
    }

    private async Task HandleAsync(JsonData? raw, string? parseFailure, CancellationToken token, int chargedBytes = 0, bool wasDeferred = false)
    {
        RpcCommandEnvelope? command = null;
        AgentSessionAttachment? originatingAttachment = null;
        PersistentAgentSession? originatingInput = null;
        try
        {
            if (raw is null)
            {
                await WriteAsync(RpcCommandCodec.Error(null, "parse", "Failed to parse command: " + parseFailure, _options)).ConfigureAwait(false);
                return;
            }
            if (raw.Value.ValueKind == JsonValueKind.Null) { SignalFatal(RpcDispatchFailure.SessionRunFailed, new RpcInputTypeError()); return; }
            command = RpcCommandCodec.Decode(raw, _options);
            // With no native dialogs registered there is no pending request to resolve. These are never ordinary commands/responses.
            if (command.Type == "extension_ui_response") return;
            bool closed; lock (_gate) closed = _closed;
            if (wasDeferred && closed)
            {
                await WriteAsync(RpcCommandCodec.Error(command.Id, command.Type, "Command canceled before acceptance: RPC input is closing.", _options)).ConfigureAwait(false);
                return;
            }
            originatingAttachment = _sessionOwner?.Current;
            if (command.Type is "prompt" or "steer" or "follow_up") originatingInput = _session;
            await ExecuteAsync(command, token, originatingAttachment).ConfigureAwait(false);
        }
        catch (RpcCommandException error)
        { await WriteAsync(RpcCommandCodec.Error(error.Id, error.Command, error.Message, _options)).ConfigureAwait(false); }
        catch (RpcDispatchException error) when (error.Failure is RpcDispatchFailure.OutputFailed)
        { throw; }
        catch (RpcSessionTreePublicationException error)
        {
            // Selection is already acknowledged. Preserve its identity on the wire and
            // the original task/fault in the host; neither cancellation nor retry undoes it.
            await WriteAsync(TreePublicationFailureResponse(command!, error.CommittedReceipt.SessionId,
                error.CommittedReceipt.View.Generation, error.CommittedReceipt.LeafId)).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            var message = error switch
            {
                RpcDispatchException dispatch => dispatch.Message,
                AgentPendingInputException queue => queue.Message,
                PersistentAgentSessionException session => session.Message,
                AgentSessionReplacementNotificationException replacement => replacement.Message,
                SessionBranchPublishException publication => publication.Message,
                SessionBranchPlanException plan => plan.Message,
                SessionCatalogException catalog => catalog.Message,
                SessionContextEditException edit => edit.Message,
                PromptInputAdmissionException admission => admission.Message,
                // rpc-mode.ts prompt: a prompt refused by its preflight answers with the refusal's message.
                SessionPromptRejectedException rejected => rejected.Message,
                OperationCanceledException when originatingAttachment is not null && !ReferenceEquals(originatingAttachment, _sessionOwner!.Current) =>
                    "Command canceled after session replacement committed; inspect the current session and durable state.",
                OperationCanceledException => "Command canceled before acceptance.",
                _ => "RPC command failed."
            };
            await WriteAsync(RpcCommandCodec.Error(command?.Id, command?.Type ?? "parse", message, _options)).ConfigureAwait(false);
        }
        finally
        {
            DeferredFrame? next = null;
            TaskCompletionSource<PostOriginEvidence>? postSlot = null;
            bool movedToRun;
            lock (_gate) movedToRun = command is not null && _postInputMovedToRun.Remove(command);
            if (originatingInput is not null && !movedToRun)
                try { postSlot = ReservePostOrigin(_postInputSettlement); }
                catch (Exception error) { SignalFatal(RpcDispatchFailure.ResourceLimit, error); }
            lock (_gate)
            {
                _pending--; _retainedCommandBytes -= chargedBytes;
                if (_deferred.Count != 0) { next = _deferred.Dequeue(); _pending++; }
                if (_pending == 0) _pendingIdle!.TrySetResult();
                _capacity.TrySetResult(); _capacity = NewGate();
            }
            // All actual command/input and response writer awaits finished; input/transition/wire gates
            // are released. The reserved slot is included in shutdown even after pending reaches zero.
            if (originatingInput is not null && postSlot is not null)
                await JoinPostOrigin("input-settled", _postInputSettlement, originatingInput, postSlot).ConfigureAwait(false);
            if (next is not null) _ = ObserveCommandAsync(HandleAsync(next.Record, next.ParseFailure, CancellationToken.None, next.Bytes, wasDeferred: true));
        }
    }

    private async Task ExecuteAsync(RpcCommandEnvelope command, CancellationToken token, AgentSessionAttachment? originatingAttachment)
    {
        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _stopInputToken);
        await _startupReady.Task.WaitAsync(startupCancellation.Token).ConfigureAwait(false);
        JsonData? data;
        switch (command.Type)
        {
            case "prompt":
                if (_inputAdmission is not null || _inputAdmissionSelector is not null) await SubmitInputAsync(command, token).ConfigureAwait(false);
                else await PromptAsync(command, token).ConfigureAwait(false);
                return;
            case "steer": case "follow_up":
                if (_inputAdmission is not null || _inputAdmissionSelector is not null) { await SubmitInputAsync(command, token).ConfigureAwait(false); return; }
                await QueueAsync(command, command.Type == "steer", token).ConfigureAwait(false);
                data = Disposition("queued"); break;
            case "abort":
                await AbortAsync(token).ConfigureAwait(false); data = null; break;
            case "pisharp_restore_queue":
            case "pisharp_interrupt":
                await RestoreQueueAsync(command, startupCancellation.Token).ConfigureAwait(false); return;
            case "pisharp_capture_navigation":
                data = await CaptureNavigationAsync(command, startupCancellation.Token).ConfigureAwait(false); break;
            case "pisharp_select_navigation":
                data = await SelectNavigationAsync(command, startupCancellation.Token).ConfigureAwait(false); break;
            case "pisharp_retire_navigation":
                data = await RetireNavigationAsync(command, startupCancellation.Token).ConfigureAwait(false); break;
            case "clear_queue":
            {
                AgentPendingInputQueueSnapshot removed;
                await _transitions.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    CheckOpen();
                    var before = _session.GetPendingInputQueueSnapshot(token);
                    var prospective = RpcCommandCodec.Build(writer => RpcCommandCodec.Queue(writer, before), _options.MaximumOutputBytes);
                    _ = RpcCommandCodec.Success(command, prospective, _options); // Budget returned text before removal; concurrent polling can only shrink it.
                    removed = _session.ClearPendingInputQueues(token);
                }
                finally { _transitions.Release(); }
                await PublishQueueAsync(force: true).ConfigureAwait(false);
                data = RpcCommandCodec.Build(writer => RpcCommandCodec.Queue(writer, removed), _options.MaximumOutputBytes); break;
            }
            case "set_steering_mode": case "set_follow_up_mode":
                await _transitions.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    CheckOpen(); var mode = command.Mode == "all" ? AgentPendingInputMode.All : AgentPendingInputMode.OneAtATime;
                    if (command.Type == "set_steering_mode") _session.SteeringMode = mode; else _session.FollowUpMode = mode;
                }
                finally { _transitions.Release(); }
                data = null; break;
            case "get_state": data = State(); break;
            case "set_auto_retry": case "abort_retry":
                data = await RetryCommandAsync(command, originatingAttachment, startupCancellation.Token).ConfigureAwait(false); break;
            case "export_html":
                data = await ExportHtmlAsync(command, originatingAttachment, startupCancellation.Token).ConfigureAwait(false); break;
            case "get_session_stats": case "set_session_name":
                data = await SessionMetadataAsync(command, originatingAttachment, startupCancellation.Token).ConfigureAwait(false); break;
            case "set_model": case "cycle_model": case "get_available_models":
            case "set_thinking_level": case "cycle_thinking_level": case "get_available_thinking_levels":
                data = await ModelThinkingAsync(command, originatingAttachment, startupCancellation.Token).ConfigureAwait(false); break;
            case "set_auto_compaction": case "pisharp_set_auto_compaction":
            case "compact": case "pisharp_compact": case "pisharp_branch_summary":
                data = await SummaryCommandAsync(command, originatingAttachment, startupCancellation.Token).ConfigureAwait(false); break;
            case "pisharp_context_edit":
            {
                if (_sessionOwner is null || originatingAttachment is null)
                    throw new RpcCommandException(command.Id, command.Type, "Context edits require an owning native session host.");
                if (command.ExpectedGeneration != originatingAttachment.Generation)
                    throw new RpcCommandException(command.Id, command.Type, "Context edit session generation is stale.");
                var operation = new ContextEditCommand();
                lock (_gate) { ThrowOpen(); _contextEdits.Add(operation); }
                try
                {
                    using var editCancellation = CancellationTokenSource.CreateLinkedTokenSource(startupCancellation.Token, operation.Abort.Token);
                    await _transitions.WaitAsync(editCancellation.Token).ConfigureAwait(false);
                    try { CheckOpen(); _sessionOwner.ValidateAttachment(originatingAttachment); }
                    finally { _transitions.Release(); }
                    JsonData EditResponse(SessionEntry entry) => RpcCommandCodec.Build(writer =>
                    {
                        writer.WriteBoolean("checkpointAcknowledged", true);
                        writer.WriteString("sessionId", originatingAttachment.Session.Snapshot.Log.Header.Id);
                        writer.WriteNumber("generation", originatingAttachment.Generation);
                        writer.WriteString("entryId", entry.Id); writer.WriteString("leafId", entry.Id);
                        writer.WriteString("targetId", command.TargetId);
                    }, _options.MaximumOutputBytes);
                    var edited = await _sessionOwner.AppendContextEditAsync(originatingAttachment,
                        new(command.TargetId!, command.Replacement), editCancellation.Token,
                        (prospective, cancellation) =>
                        {
                            cancellation.ThrowIfCancellationRequested();
                            _ = RpcCommandCodec.Success(command, EditResponse(prospective.Entry), _options);
                            return ValueTask.CompletedTask;
                        }).ConfigureAwait(false);
                    // A known append checkpoint survives late cancellation. Delivery remains owned and awaited.
                    data = EditResponse(edited.Entry);
                }
                finally
                {
                    Task cancellationIdle;
                    lock (_gate) { _contextEdits.Remove(operation); cancellationIdle = operation.CancelUsers == 0 ? Task.CompletedTask : operation.CancelIdle!.Task; }
                    await cancellationIdle.ConfigureAwait(false); operation.Abort.Dispose();
                }
                break;
            }
            case "get_messages":
                data = RpcCommandCodec.Build(writer => RpcCommandCodec.Messages(writer, "messages", _session.Snapshot.Context.Messages,
                    _options.MaximumReturnedMessages), _options.MaximumOutputBytes); break;
            case "get_commands":
                data = RpcCommandCodec.Build(writer => RpcCommandCodec.Raw(writer, "commands",
                    _commandCatalog?.CommandCatalog ?? JsonData.Parse("[]")), _options.MaximumOutputBytes); break;
            case "pisharp_complete_extension_command":
                if (_commandCatalog is null) throw new RpcCommandException(command.Id, command.Type, "Extension command completion is unavailable.");
                using (var completionCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _stopInputToken))
                {
                    var completions = await _commandCatalog.CompleteCommandAsync(command.Message!, command.Mode!, completionCancellation.Token).ConfigureAwait(false);
                    completionCancellation.Token.ThrowIfCancellationRequested();
                    data = RpcCommandCodec.Build(writer => RpcCommandCodec.Raw(writer, "completions", completions), _options.MaximumOutputBytes);
                }
                break;
            case "get_entries": data = Entries(command); break;
            case "get_tree": data = Tree(command, startupCancellation.Token); break;
            case "get_fork_messages": data = ForkMessages(startupCancellation.Token); break;
            case "pisharp_list_sessions":
            {
                if (_sessionOwner is null || !_sessionOwner.CanDiscoverSessions)
                    throw new RpcCommandException(command.Id, command.Type, "Session discovery is unavailable from this host.");
                var expected = _sessionOwner.Current;
                var page = await _sessionOwner.ListSessionsAsync(expected, command.CatalogQuery!, startupCancellation.Token).ConfigureAwait(false);
                data = CatalogPage(page, expected);
                break;
            }
            case "pisharp_resume_session":
            {
                if (_sessionOwner is null || !_sessionOwner.CanDiscoverSessions)
                    throw new RpcCommandException(command.Id, command.Type, "Session resume is unavailable from this host.");
                var expected = _sessionOwner.Current;
                var replacement = await _sessionOwner.ResumeAsync(expected, new(command.Message!, command.Mode != "selected", command.Since),
                    (target, cancellation) =>
                    {
                        cancellation.ThrowIfCancellationRequested();
                        PreflightReplacement(command, target, checked(expected.Generation + 1));
                        return ValueTask.CompletedTask;
                    }, startupCancellation.Token).ConfigureAwait(false);
                data = ReplacementResponse(replacement);
                break;
            }
            case "new_session": case "fork": case "clone":
            {
                if (_sessionOwner is null || !_sessionOwner.CanCreateSessions)
                    throw new RpcCommandException(command.Id, command.Type, "Durable session creation is unavailable from this host.");
                var expected = _sessionOwner.Current;
                var kind = command.Type == "new_session" ? AgentSessionCreationKind.New : command.Type == "clone" ?
                    AgentSessionCreationKind.Clone : command.Mode == "at" ? AgentSessionCreationKind.ForkAt : AgentSessionCreationKind.ForkBefore;
                JsonData Response(PersistentAgentSession target, string? text, long generation) => RpcCommandCodec.Build(writer =>
                {
                    writer.WriteBoolean("cancelled", false);
                    if (command.Type == "fork") writer.WriteString("text", text);
                    writer.WriteString("sessionId", target.Snapshot.Log.Header.Id);
                    writer.WriteString("sessionFile", target.Path); writer.WriteNumber("generation", generation);
                }, _options.MaximumOutputBytes);
                var replacement = await _sessionOwner.CreateAsync(expected,
                    new(kind, command.Type == "fork" ? command.Message : null, command.Type == "new_session" ? command.Message : null),
                    (target, text, cancellation) =>
                    {
                        cancellation.ThrowIfCancellationRequested();
                        _ = RpcCommandCodec.Success(command, Response(target, text, checked(expected.Generation + 1)), _options);
                        _ = RpcCommandCodec.Event("session_switched", writer =>
                        {
                            writer.WriteString("sessionFile", target.Path); writer.WriteString("sessionId", target.Snapshot.Log.Header.Id);
                            writer.WriteNumber("generation", checked(expected.Generation + 1));
                        }, _options);
                        return ValueTask.CompletedTask;
                    }, startupCancellation.Token).ConfigureAwait(false);
                data = replacement is null ? RpcCommandCodec.Build(writer => writer.WriteBoolean("cancelled", true), _options.MaximumOutputBytes) :
                    Response(replacement.Current.Session, replacement.SelectedText, replacement.Current.Generation);
                break;
            }
            case "switch_session":
            {
                if (_sessionOwner is null) throw new RpcCommandException(command.Id, command.Type, "Session replacement is unavailable from this host.");
                var owner = _sessionOwner; var expected = owner.Current;
                Func<ValueTask>? undoPreparation = null;
                if (_prepareSessionPath is { } prepare)
                {
                    try { undoPreparation = await prepare(command.Message!, startupCancellation.Token).ConfigureAwait(false); }
                    catch (InvalidDataException error) { throw new RpcCommandException(command.Id, command.Type, error.Message); }
                }
                AgentSessionReplacement? replacement;
                try { replacement = await SwitchWithPreparationAsync().ConfigureAwait(false); }
                catch when (undoPreparation is not null && ReferenceEquals(owner.Current, expected))
                {
                    // agent-session-runtime.ts switchSession: SessionManager.open runs after the session_before_switch veto.
                    await undoPreparation().ConfigureAwait(false); throw;
                }
                if (replacement is null && undoPreparation is not null) await undoPreparation().ConfigureAwait(false);
                Task<AgentSessionReplacement?> SwitchWithPreparationAsync() => owner.SwitchAsync(expected, new(command.Message!, command.Mode != "selected", command.Since),
                    beforeSwitch: async (previous, target, cancellation) =>
                    {
                        cancellation.ThrowIfCancellationRequested(); PreflightReplacement(command, target, checked(expected.Generation + 1));
                        return owner.BeforeReplacement is not { } veto || await veto(previous, target, cancellation).ConfigureAwait(false);
                    },
                    cancellationToken: startupCancellation.Token);
                data = RpcCommandCodec.Build(writer =>
                {
                    writer.WriteBoolean("cancelled", replacement is null);
                    if (replacement is not null)
                    {
                        writer.WriteString("sessionId", replacement.Current.Session.Snapshot.Log.Header.Id);
                        writer.WriteString("sessionFile", replacement.Current.Session.Path);
                        writer.WriteNumber("generation", replacement.Current.Generation);
                    }
                }, _options.MaximumOutputBytes);
                break;
            }
            case "get_last_assistant_text": data = LastAssistantText(); break;
            case "bash": case "abort_bash": data = await UserBashAsync(command, token).ConfigureAwait(false); break;
            default:
                await WriteAsync(RpcCommandCodec.Error(command.Id, command.Type, RpcCommandCodec.IsKnown(command.Type)
                    ? "Command requires unfinished native RPC/session workflow support: " + command.Type
                    : "Unknown command: " + command.Type, _options)).ConfigureAwait(false); return;
        }
        await WriteAsync(RpcCommandCodec.Success(command, data, _options)).ConfigureAwait(false);
    }

    private TranscriptEntry Input(RpcCommandEnvelope command)
    {
        long timestamp;
        try { timestamp = _clock(); }
        catch (Exception) { throw new RpcCommandException(command.Id, command.Type, "Prompt timestamp is invalid."); }
        if (timestamp is < -9_007_199_254_740_991 or > 9_007_199_254_740_991)
            throw new RpcCommandException(command.Id, command.Type, "Prompt timestamp is outside the native integer profile.");
        return RpcCommandCodec.User(command, timestamp, _options);
    }
    private JsonData CatalogPage(SessionCatalogPage page, AgentSessionAttachment attachment) => RpcCommandCodec.Build(writer =>
    {
        writer.WriteNumber("generation", attachment.Generation);
        if (page.Items.Length > _options.MaximumReturnedEntries) throw new RpcDispatchException(RpcDispatchFailure.ResourceLimit);
        writer.WritePropertyName("sessions"); writer.WriteStartArray();
        foreach (var item in page.Items)
        {
            writer.WriteStartObject(); writer.WriteString("key", item.Key); writer.WriteString("storeId", item.StoreId);
            writer.WriteString("fileName", item.FileName); writer.WriteString("sessionFile", item.Path);
            writer.WriteString("sessionId", item.SessionId); writer.WriteString("createdTimestamp", item.CreatedTimestamp);
            writer.WriteString("cwd", item.WorkingDirectory); writer.WriteString("cwdGroup", item.CwdGroup);
            writer.WriteString("parentSession", item.ParentSessionPath); writer.WriteNumber("fileBytes", item.FileBytes);
            writer.WriteString("modifiedUtc", new DateTime(item.ModifiedUtcTicks, DateTimeKind.Utc).ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteBoolean("isCurrent", string.Equals(item.Path, attachment.Session.Path,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WriteString("nextCursor", page.NextCursor);
        writer.WriteNumber("skippedFiles", page.SkippedFiles); writer.WriteNumber("unavailableStores", page.UnavailableStores);
    }, _options.MaximumOutputBytes);
    private JsonData ReplacementResponse(AgentSessionReplacement? replacement) => RpcCommandCodec.Build(writer =>
    {
        writer.WriteBoolean("cancelled", replacement is null);
        if (replacement is null) return;
        writer.WriteString("sessionId", replacement.Current.Session.Snapshot.Log.Header.Id);
        writer.WriteString("sessionFile", replacement.Current.Session.Path); writer.WriteNumber("generation", replacement.Current.Generation);
    }, _options.MaximumOutputBytes);
    private void PreflightReplacement(RpcCommandEnvelope command, PersistentAgentSession target, long generation)
    {
        var data = RpcCommandCodec.Build(writer =>
        {
            writer.WriteBoolean("cancelled", false); writer.WriteString("sessionId", target.Snapshot.Log.Header.Id);
            writer.WriteString("sessionFile", target.Path); writer.WriteNumber("generation", generation);
        }, _options.MaximumOutputBytes);
        _ = RpcCommandCodec.Success(command, data, _options);
        _ = RpcCommandCodec.Event("session_switched", writer =>
        {
            writer.WriteString("sessionFile", target.Path); writer.WriteString("sessionId", target.Snapshot.Log.Header.Id);
            writer.WriteNumber("generation", generation);
        }, _options);
    }
    private JsonData ForkMessages(CancellationToken token)
    {
        var entries = _session.Snapshot.Log.Entries;
        return RpcCommandCodec.Build(writer =>
        {
            writer.WritePropertyName("messages"); writer.WriteStartArray(); var count = 0;
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Kind != SessionEntryKind.Message) continue;
                var message = entry.WireBody.Value.GetProperty("message");
                if (message.GetProperty("role").GetString() != "user") continue;
                var text = RpcCommandCodec.Text(new("user", JsonData.FromElement(message)));
                if (text.Length == 0) continue;
                if (++count > _options.MaximumReturnedMessages) throw new RpcDispatchException(RpcDispatchFailure.ResourceLimit);
                writer.WriteStartObject(); writer.WriteString("entryId", entry.Id); writer.WriteString("text", text); writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }, _options.MaximumOutputBytes);
    }
    private JsonData Tree(RpcCommandEnvelope command, CancellationToken token)
    {
        var state = _session.Snapshot;
        if (state.Log.Entries.Length > _options.MaximumReturnedEntries) throw new RpcDispatchException(RpcDispatchFailure.ResourceLimit);
        // The session's own context bounds (the Pi entry's admit every entry); the returned-entry bound above stays the command's.
        var tree = _session.CreateTreeQueries().Build(state.Log.Entries, token);
        if (!tree.LabelsAvailable) throw new RpcCommandException(command.Id, command.Type, "Session tree metadata is unavailable under the native profile.");
        var data = RpcCommandCodec.Build(writer =>
        {
            writer.WriteString("leafId", state.Context.LeafId);
            writer.WritePropertyName("tree"); writer.WriteStartArray();
            var stack = new Stack<(string? Id, int Depth)>();
            for (var index = tree.RootIds.Length - 1; index >= 0; index--) stack.Push((tree.RootIds[index], 1));
            while (stack.TryPop(out var frame))
            {
                token.ThrowIfCancellationRequested();
                if (frame.Id is null) { writer.WriteEndArray(); writer.WriteEndObject(); continue; }
                if (frame.Depth * 2 + 4 >= _options.MaximumJsonDepth) throw new RpcDispatchException(RpcDispatchFailure.ResourceLimit);
                var node = tree.ById[frame.Id]; writer.WriteStartObject();
                RpcCommandCodec.Raw(writer, "entry", node.Entry.WireBody);
                if (node.ResolvedLabel is { } label) { writer.WriteString("label", label.Label); writer.WriteString("labelTimestamp", label.Timestamp); }
                writer.WritePropertyName("children"); writer.WriteStartArray(); stack.Push((null, frame.Depth));
                var children = tree.GetChronologicalChildren(frame.Id, token);
                if (children.Status != SessionTreeOrderStatus.Completed)
                    throw new RpcCommandException(command.Id, command.Type, "Session tree chronological ordering is unavailable under the native profile.");
                for (var index = children.Entries.Length - 1; index >= 0; index--) stack.Push((children.Entries[index].Id, frame.Depth + 1));
            }
            writer.WriteEndArray();
        }, _options.MaximumOutputBytes);
        try { RpcCommandCodec.Strict(data, _options.MaximumOutputBytes, Math.Max(1, _options.MaximumJsonDepth - 1)); }
        catch (JsonlTransportException) { throw new RpcDispatchException(RpcDispatchFailure.ResourceLimit); }
        return data;
    }
    private async Task RestoreQueueAsync(RpcCommandEnvelope command, CancellationToken token)
    {
        Task interrupted = Task.CompletedTask;
        await _queuePublication.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RpcQueueRestorationPlan plan;
            await _transitions.WaitAsync(token).ConfigureAwait(false);
            try
            {
                CheckOpen();
                var attachment = _sessionOwner?.Current;
                if (attachment is null || attachment.Generation != command.ExpectedGeneration ||
                    !ReferenceEquals(attachment.Session, _session))
                    throw new RpcCommandException(command.Id, command.Type, "Queue restoration session generation is stale or unavailable.");
                var removed = false;
                try
                {
                    plan = RpcQueueRestorationPlan.Capture(_session, command, attachment.Generation, _options, token);
                    if (!plan.TryCommit(_session, out _, token))
                        throw new RpcCommandException(command.Id, command.Type, "Queue changed during restoration; retry against the current queue.");
                    removed = true;
                }
                finally
                {
                    if (command.Type == "pisharp_interrupt" && (removed || !token.IsCancellationRequested))
                    {
                        // Capture only this validated attachment's work. Global owner.Abort()
                        // would also cancel a staged replacement or a newer attachment.
                        RunState? run;
                        lock (_gate) { run = _run; if (run is not null) run.AbortRequested = true; }
                        interrupted = Task.WhenAll(_session.WaitForIdleAsync(), run?.Settled.Task ?? Task.CompletedTask);
                        _session.Abort();
                    }
                }
                // The exact expected snapshot was atomically removed. Both frames were
                // fully budgeted before that commit, including the response envelope.
            }
            finally { _transitions.Release(); }
            // Keep postcommit delivery owned even if the requesting caller cancels.
            // Later queue admissions publish after this clear event; switching also waits.
            await WriteAsync(plan.QueueEvent).ConfigureAwait(false);
            _publishedQueue = plan.Cleared;
            await WriteAsync(plan.Response).ConfigureAwait(false);
        }
        finally
        {
            _queuePublication.Release();
            // Settlement can publish output/queue events. Never join it under either
            // semaphore, and retain the original join after caller cancellation/errors.
            await interrupted.ConfigureAwait(false);
        }
    }

    private async Task QueueAsync(RpcCommandEnvelope command, bool steering, CancellationToken token)
    {
        var input = Input(command);
        _ = RpcCommandCodec.Success(command, Disposition("queued"), _options);
        await _transitions.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckOpen(); CheckQueueBudget(input, steering, token);
            if (steering) _session.Steer(input, token); else _session.FollowUp(input, token);
        }
        finally { _transitions.Release(); }
        await PublishQueueAsync(force: true).ConfigureAwait(false);
    }
    /// <summary>Pi accepts a prompt as soon as agent_settled has been published; the run releases its ownership right after
    /// writing it, so a prompt in that window waits for the release instead of being refused as "settling".</summary>
    private async Task WaitForPublishedSettlementAsync(CancellationToken token)
    {
        RunState? published; lock (_gate) published = _run is { SettledPublished: true } run ? run : null;
        if (published is not null) await published.Settled.Task.WaitAsync(token).ConfigureAwait(false);
    }
    private async Task PromptAsync(RpcCommandEnvelope command, CancellationToken token)
    {
        var input = Input(command); RunState? run = null; Task<AgentLoopResult>? processing = null; var queued = false; PersistentAgentSession? originating = null;
        var startedResponse = RpcCommandCodec.Success(command, Disposition("started"), _options);
        var queuedResponse = RpcCommandCodec.Success(command, Disposition("queued"), _options);
        await WaitForPublishedSettlementAsync(token).ConfigureAwait(false);
        await _transitions.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckOpen(); RunState? active; lock (_gate) active = _run;
            var snapshot = _session.Snapshot;
            if (active is not null)
            {
                if (!snapshot.Agent.IsRunning)
                    throw new RpcCommandException(command.Id, command.Type, "Session is settling. Wait for agent_settled before prompting again.");
                if (command.StreamingBehavior is null)
                    throw new RpcCommandException(command.Id, command.Type,
                        "Agent is already processing. Specify streamingBehavior ('steer' or 'followUp') to queue the message.");
                CheckQueueBudget(input, command.StreamingBehavior == "steer", token);
                if (command.StreamingBehavior == "steer") _session.Steer(input, token); else _session.FollowUp(input, token);
                queued = true;
            }
            else
            {
                if (!KnowsModel(snapshot.Agent.Model)) throw new RpcDispatchException(RpcDispatchFailure.InvalidModelDefinition);
                originating = _session;
                run = new(snapshot.Agent.Messages.Length); lock (_gate) _run = run;
                try { processing = _session.PromptAsync(input); }
                catch { lock (_gate) if (ReferenceEquals(_run, run)) _run = null; run.Ready.TrySetResult(); run.Settled.TrySetResult(); throw; }
                if (_session.Snapshot.Agent.Generation == snapshot.Agent.Generation)
                {
                    lock (_gate) if (ReferenceEquals(_run, run)) _run = null;
                    run.Ready.TrySetResult(); run.Settled.TrySetResult(); run = null;
                }
            }
        }
        finally { _transitions.Release(); }
        if (queued)
        {
            await PublishQueueAsync(force: true).ConfigureAwait(false);
            await WriteAsync(queuedResponse).ConfigureAwait(false); return;
        }
        if (run is null) { await processing!.ConfigureAwait(false); throw new RpcDispatchException(RpcDispatchFailure.SessionRunFailed); }
        // The generation transition is actual admission. Gate observer output until this one authoritative response is written.
        lock (_gate) _postInputMovedToRun.Add(command);
        _ = MonitorAsync(run, processing!, originating!);
        try { await WriteAsync(startedResponse).ConfigureAwait(false); }
        finally { run.Ready.TrySetResult(); }
    }
    private sealed class InputAdmission(RpcSessionDispatcher owner, IPromptInputAdmission selected, CancellationToken caller) : IPromptInputAdmission
    {
        public async ValueTask<PromptInputDecision> ReduceAsync(PromptInput input, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, caller);
            var previous = owner._inInputCallback.Value; owner._inInputCallback.Value = true;
            try
            {
                var result = await selected.ReduceAsync(input, linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested(); return result;
            }
            finally { owner._inInputCallback.Value = previous; }
        }
    }
    private async Task SubmitInputAsync(RpcCommandEnvelope command, CancellationToken token)
    {
        var started = RpcCommandCodec.Success(command, Disposition("started"), _options);
        var queued = RpcCommandCodec.Success(command, Disposition("queued"), _options);
        var handled = RpcCommandCodec.Success(command, Disposition("handled"), _options);
        using var admissionCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _stopInputToken);
        if (command.Type == "prompt") await WaitForPublishedSettlementAsync(admissionCancellation.Token).ConfigureAwait(false);
        await _inputCommands.WaitAsync(admissionCancellation.Token).ConfigureAwait(false);
        var candidate = new RunState(0);
        PersistentAgentSession originating;
        try
        {
            await _transitions.WaitAsync(admissionCancellation.Token).ConfigureAwait(false);
            try
            {
                CheckOpen();
                originating = _session;
                lock (_gate) _admittingSession = originating;
                if (command.Type == "prompt")
                {
                    RunState? active; lock (_gate) active = _run;
                    if (active is not null && !_session.Snapshot.Agent.IsRunning)
                        throw new RpcCommandException(command.Id, command.Type, "Session is settling. Wait for agent_settled before prompting again.");
                    if (!KnowsModel(_session.Snapshot.Agent.Model)) throw new RpcDispatchException(RpcDispatchFailure.InvalidModelDefinition);
                    lock (_gate) _startingInput = candidate;
                }
            }
            finally { _transitions.Release(); }
            var mode = command.Type switch
            {
                "steer" => PromptInputStreamingBehavior.Steer,
                "follow_up" => PromptInputStreamingBehavior.FollowUp,
                _ => command.StreamingBehavior switch
                {
                    "steer" => PromptInputStreamingBehavior.Steer,
                    "followUp" => PromptInputStreamingBehavior.FollowUp,
                    _ => (PromptInputStreamingBehavior?)null
                }
            };
            var limits = new PromptInputAdmissionOptions(_options.MaximumPromptCharacters, _options.MaximumImages,
                _options.MaximumCommandBytes, _options.MaximumCommandBytes, _options.MaximumJsonDepth,
                _options.MaximumCommandBytes, _options.MaximumCommandBytes)
            {
                QueueOnly = command.Type != "prompt",
                BeforeQueueCommit = (message, queue, behavior) =>
                {
                    admissionCancellation.Token.ThrowIfCancellationRequested();
                    CheckQueueBudget(message, behavior == PromptInputStreamingBehavior.Steer, queue);
                }
            };
            var selected = _inputAdmissionSelector?.Invoke(command.Type) ?? _inputAdmission ??
                throw new InvalidOperationException("Input admission is unavailable.");
            var processing = originating.SubmitInputAsync(new(command.Message!, PromptInputSource.Rpc, command.Images, mode),
                new InputAdmission(this, selected, token), limits, _stopInputToken);
            // Started coordinator submissions finish after the run. Its actual first event is the admission witness.
            await Task.WhenAny(candidate.Entered.Task, processing).ConfigureAwait(false);
            if (candidate.Entered.Task.IsCompleted)
            {
                lock (_gate) if (ReferenceEquals(_startingInput, candidate)) _startingInput = null;
                lock (_gate) _postInputMovedToRun.Add(command);
                _ = MonitorAsync(candidate, SubmittedRunAsync(processing), originating);
                try { await WriteAsync(started).ConfigureAwait(false); }
                finally { candidate.Ready.TrySetResult(); }
                return;
            }
            var result = await processing.ConfigureAwait(false);
            if (result.Disposition == SubmittedInputDisposition.Queued)
            {
                await PublishQueueAsync(force: true).ConfigureAwait(false);
                await WriteAsync(queued).ConfigureAwait(false);
            }
            else if (result.Disposition == SubmittedInputDisposition.Handled) await WriteAsync(handled).ConfigureAwait(false);
            else throw new RpcDispatchException(RpcDispatchFailure.SessionRunFailed);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_startingInput, candidate)) _startingInput = null;
                _admittingSession = null;
            }
            _inputCommands.Release();
        }
    }
    private static async Task<AgentLoopResult> SubmittedRunAsync(Task<SubmittedInputResult> processing)
    {
        var result = await processing.ConfigureAwait(false);
        return result.Disposition == SubmittedInputDisposition.Started && result.Run is not null
            ? result.Run : throw new RpcDispatchException(RpcDispatchFailure.SessionRunFailed);
    }
    private async Task MonitorAsync(RunState run, Task<AgentLoopResult> processing, PersistentAgentSession originating)
    {
        try
        {
            await run.Ready.Task.ConfigureAwait(false); var continuations = 0;
            while (true)
            {
                var result = await processing.ConfigureAwait(false);
                await _session.WaitForIdleAsync().ConfigureAwait(false);
                await _inputCommands.WaitAsync().ConfigureAwait(false);
                await _transitions.WaitAsync().ConfigureAwait(false);
                TaskCompletionSource? ready = null;
                try
                {
                    bool stopping;
                    lock (_gate)
                    {
                        if (!ReferenceEquals(_run, run)) return;
                        stopping = _closed || run.AbortRequested;
                    }
                    var queue = _session.GetPendingInputQueueSnapshot();
                    if (stopping || result.Reason != AgentLoopStopReason.Completed || queue.SteeringMessages.IsEmpty && queue.FollowUpMessages.IsEmpty) break;
                    if (++continuations > _options.MaximumContinuationRuns) throw new RpcDispatchException(RpcDispatchFailure.ResourceLimit);
                    run.HistoryLength = _session.Snapshot.Agent.Messages.Length;
                    run.Ready = ready = NewGate(); // Keep callbacks out of this short transition, even for a synchronous continuation.
                    processing = _session.ContinueAsync();
                }
                finally { _transitions.Release(); _inputCommands.Release(); ready?.TrySetResult(); }
            }
            await PublishQueueAsync(force: false).ConfigureAwait(false);
            bool fatal, aborted; lock (_gate) { fatal = _fatal is not null; aborted = run.AbortRequested; }
            // aborted reports whether this session-level run ended because an abort was requested while it ran.
            lock (_gate) run.SettledPublished = true;
            if (!fatal) await WriteAsync(RpcCommandCodec.Event("agent_settled", writer => writer.WriteBoolean("aborted", aborted), _options)).ConfigureAwait(false);
        }
        catch (Exception error) { SignalFatal(error is RpcDispatchException dispatch ? dispatch.Failure : RpcDispatchFailure.SessionRunFailed, error); }
        finally
        {
            TaskCompletionSource<PostOriginEvidence>? postSlot = null;
            try { postSlot = ReservePostOrigin(_postRunSettlement); }
            catch (Exception error) { SignalFatal(RpcDispatchFailure.ResourceLimit, error); }
            lock (_gate) if (ReferenceEquals(_run, run)) _run = null;
            run.Settled.TrySetResult();
            // Processing/idle and agent_settled writer awaits finished and run ownership is released.
            // A reserved post-run slot remains part of actual dispatcher shutdown custody.
            await JoinPostOrigin("run-settled", _postRunSettlement, originating, postSlot).ConfigureAwait(false);
        }
    }
    private async Task AbortAsync(CancellationToken token)
    {
        ContextEditCommand[] edits;
        lock (_gate)
        {
            edits = _contextEdits.ToArray();
            foreach (var edit in edits) if (edit.CancelUsers++ == 0) edit.CancelIdle = NewGate();
        }
        foreach (var edit in edits)
        {
            try { edit.Abort.Cancel(); }
            catch (Exception error) { SignalFatal(RpcDispatchFailure.CleanupFailed, error); }
            finally { lock (_gate) if (--edit.CancelUsers == 0) edit.CancelIdle!.TrySetResult(); }
        }
        RunState? run; PersistentAgentSession? originating;
        await _transitions.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_gate) { run = _run; originating = _admittingSession; if (run is not null) run.AbortRequested = true; }
            if (_sessionOwner is not null) _sessionOwner.Abort(); else _session.Abort();
            originating?.Abort();
        }
        finally { _transitions.Release(); }
        if (run is not null) await run.Settled.Task.ConfigureAwait(false);
        if (originating is not null) await originating.WaitForIdleAsync().ConfigureAwait(false);
        await _session.WaitForIdleAsync().ConfigureAwait(false);
    }

    private async ValueTask ObserveAsync(AgentEvent observation)
    {
        var previous = _inCallback.Value; _inCallback.Value = true;
        try
        {
            RunState? run;
            lock (_gate)
            {
                if (observation is AgentLoopStarted && _startingInput is { } starting)
                {
                    starting.HistoryLength = _session.Snapshot.Agent.Messages.Length;
                    _run = starting; starting.Entered.TrySetResult();
                }
                run = _run;
                if (observation is AgentLoopStarted && run is not null)
                    run.HistoryLength = _session.Snapshot.Agent.Messages.Length;
            }
            if (run is null && observation is AgentLoopInputMessageStarted { Message.Role: "custom" } or AgentLoopInputMessageEnded { Message.Role: "custom" })
            {
                // agent-session.ts _appendCustomMessage: an idle session appends an extension's custom message and emits its
                // message_start/message_end at once, outside any run.
                lock (_gate) if (_fatal is not null) throw _fatal;
                await _projectionGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    foreach (var record in _events.Project(observation, _session.Snapshot, _session.Snapshot.Agent.Messages.Length))
                        await WriteAsync(record).ConfigureAwait(false);
                }
                finally { _projectionGate.Release(); }
                return;
            }
            if (run is null) throw new RpcDispatchException(RpcDispatchFailure.SessionRunFailed);
            await run.Ready.Task.ConfigureAwait(false);
            lock (_gate) if (_fatal is not null) throw _fatal;
            if (observation is AgentLoopInputMessageStarted) await PublishQueueAsync(force: false).ConfigureAwait(false);
            // A parallel tool batch reports its tools' progress and ends concurrently; projection and its records stay in order.
            await _projectionGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var projected = _events.Project(observation, _session.Snapshot, run.HistoryLength,
                    observation is AgentLoopEnded ended && _session.WillRetryAfterAgentEnd(ended.Result));
                foreach (var record in projected) await WriteAsync(record).ConfigureAwait(false);
            }
            finally { _projectionGate.Release(); }
        }
        catch (Exception error) { SignalFatal(error is RpcDispatchException dispatch ? dispatch.Failure : RpcDispatchFailure.SessionRunFailed, error); throw; }
        finally { _inCallback.Value = previous; }
    }
    private async ValueTask ObserveOperationAsync(SessionOperationEvent observation)
    {
        if (observation is SessionCompactionStarted or SessionCompactionPrepared or SessionCompactionEnded)
        {
            var prior = _inCallback.Value; _inCallback.Value = true;
            try
            {
                switch (observation)
                {
                    case SessionCompactionStarted started:
                        await WriteAsync(OriginalCompactionEventProjector.Start(started.Reason, _options)).ConfigureAwait(false);
                        break;
                    case SessionCompactionPrepared prepared:
                        // Validate the complete eventual frame before the original append is admitted.
                        _ = OriginalCompactionEventProjector.End(prepared.Reason, prepared.Result, false, prepared.WillRetry, options: _options);
                        break;
                    case SessionCompactionEnded ended:
                        await WriteAsync(OriginalCompactionEventProjector.End(ended.Reason, ended.Result,
                            ended.Aborted, ended.WillRetry, ended.ErrorMessage, _options)).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception error)
            {
                // A prospective output-budget refusal has no transport effect and must not poison the dispatcher.
                if (observation is not SessionCompactionPrepared)
                    SignalFatal(error is RpcDispatchException dispatch ? dispatch.Failure : RpcDispatchFailure.SessionRunFailed, error);
                throw;
            }
            finally { _inCallback.Value = prior; }
            return;
        }
        if (observation is SessionAutoRetryStarted or SessionAutoRetryEnded)
        { await ObserveRetryAsync(observation).ConfigureAwait(false); return; }
        if (!_session.Snapshot.AutoRecoveryEnabled && !_session.AutomaticRetryConfigured) return;
        var previous = _inCallback.Value; _inCallback.Value = true;
        try
        {
            var kind = observation switch
            {
                SessionRecoveryStarted => "pisharp_recovery_started",
                SessionRecoveryEnded => "pisharp_recovery_ended",
                SessionOperationSettled => "pisharp_operation_settled",
                _ => throw new RpcDispatchException(RpcDispatchFailure.SessionRunFailed)
            };
            await WriteAsync(RpcCommandCodec.Event(kind, writer =>
            {
                writer.WriteNumber("operationGeneration", observation.OperationGeneration);
                if (observation is SessionRecoveryStarted started)
                { writer.WriteString("reason", started.Reason); writer.WriteBoolean("willRetry", started.WillRetry); }
                else if (observation is SessionRecoveryEnded ended)
                { writer.WriteString("status", ended.Status); writer.WriteBoolean("willRetry", ended.WillRetry); if (ended.CheckpointId is not null) writer.WriteString("checkpointId", ended.CheckpointId); }
                else if (observation is SessionOperationSettled settled) writer.WriteString("status", settled.Status);
            }, _options)).ConfigureAwait(false);
        }
        catch (Exception error) { SignalFatal(error is RpcDispatchException dispatch ? dispatch.Failure : RpcDispatchFailure.SessionRunFailed, error); throw; }
        finally { _inCallback.Value = previous; }
    }
    private async Task PublishQueueAsync(bool force)
    {
        await _queuePublication.WaitAsync().ConfigureAwait(false);
        try
        {
            var queue = _session.GetPendingInputQueueSnapshot();
            if (!force && SameQueue(queue, _publishedQueue)) return;
            await WriteAsync(RpcCommandCodec.Event("queue_update", writer => RpcCommandCodec.Queue(writer, queue), _options)).ConfigureAwait(false);
            _publishedQueue = queue;
        }
        finally { _queuePublication.Release(); }
    }
    private static bool SameQueue(AgentPendingInputQueueSnapshot left, AgentPendingInputQueueSnapshot right) =>
        Same(left.SteeringMessages, right.SteeringMessages) && Same(left.FollowUpMessages, right.FollowUpMessages);
    private static bool Same(ImmutableArray<TranscriptEntry> left, ImmutableArray<TranscriptEntry> right) =>
        left.Length == right.Length && left.Zip(right).All(pair => pair.First.WireBody.ToString() == pair.Second.WireBody.ToString());
    private void CheckQueueBudget(TranscriptEntry input, bool steering, CancellationToken token)
    {
        var queue = _session.GetPendingInputQueueSnapshot(token);
        CheckQueueBudget(input, steering, queue);
    }
    private void CheckQueueBudget(TranscriptEntry input, bool steering, AgentPendingInputQueueSnapshot queue)
    {
        var prospective = steering ? queue with { SteeringMessages = queue.SteeringMessages.Add(input) }
            : queue with { FollowUpMessages = queue.FollowUpMessages.Add(input) };
        _ = RpcCommandCodec.Event("queue_update", writer => RpcCommandCodec.Queue(writer, prospective), _options);
    }

    private JsonData State()
    {
        var snapshot = _session.Snapshot; var queue = _session.GetPendingInputQueueSnapshot();
        if (!TryGetModel(snapshot.Agent.Model, out var model)) throw new RpcDispatchException(RpcDispatchFailure.InvalidModelDefinition);
        string? name = null;
        foreach (var entry in snapshot.Log.Entries.Reverse())
            if (entry.Type == "session_info")
            {
                if (entry.WireBody.Value.TryGetProperty("name", out var value) && value.ValueKind == JsonValueKind.String)
                { var text = RpcCommandCodec.TrimSource(value.GetString()!); if (text.Length != 0) name = text; }
                break;
            }
        return RpcCommandCodec.Build(writer =>
        {
            RpcCommandCodec.Raw(writer, "model", model); writer.WriteString("thinkingLevel", snapshot.Context.ThinkingLevel);
            writer.WriteBoolean("isStreaming", snapshot.Agent.IsRunning); writer.WriteBoolean("isCompacting", snapshot.IsCompacting);
            writer.WriteBoolean("autoRetryEnabled", _session.AutoRetryEnabled); writer.WriteBoolean("isRetrying", _session.IsRetrying);
            // agent_settled is published before the monitor clears its original run owner.
            // This is a read-only dispatcher admission fence, not a promise about future commands.
            bool runOwnerSettled; lock (_gate) runOwnerSettled = _run is null;
            writer.WriteBoolean("pisharpRunOwnerSettled", runOwnerSettled);
            if (snapshot.AutoRecoveryEnabled)
            {
                writer.WriteBoolean("pisharpOperationActive", snapshot.IsProcessingOperation);
                writer.WriteString("pisharpOperationPhase", snapshot.OperationPhase.ToString());
                writer.WriteNumber("pisharpOperationGeneration", snapshot.OperationGeneration);
            }
            if (snapshot.IsEditingContext) writer.WriteBoolean("pisharpEditingContext", true);
            writer.WriteString("steeringMode", RpcCommandCodec.Mode(queue.SteeringMode)); writer.WriteString("followUpMode", RpcCommandCodec.Mode(queue.FollowUpMode));
            writer.WriteString("sessionFile", _session.Path); writer.WriteString("sessionId", snapshot.Log.Header.Id);
            if (_sessionOwner is not null) writer.WriteNumber("pisharpGeneration", _sessionOwner.Current.Generation);
            if (snapshot.Log.StorageDurability != PiSharp.Sessions.Storage.SessionLogStorageDurability.LocalFileFlush)
            {
                writer.WriteString("pisharpPersistence", snapshot.Log.StorageDurability.ToString());
                writer.WriteBoolean("pisharpDurableCheckpointAcknowledged", false);
            }
            if (name is not null) writer.WriteString("sessionName", name);
            writer.WriteBoolean("autoCompactionEnabled", snapshot.AutoCompactionEnabled); writer.WriteNumber("messageCount", snapshot.Context.Messages.Length);
            writer.WriteNumber("pendingMessageCount", checked(queue.SteeringMessages.Length + queue.FollowUpMessages.Length));
        }, _options.MaximumOutputBytes);
    }
    private JsonData Entries(RpcCommandEnvelope command)
    {
        var snapshot = _session.Snapshot; var start = 0;
        if (command.Since is not null)
        {
            var found = -1;
            for (var index = 0; index < snapshot.Log.Entries.Length; index++) if (snapshot.Log.Entries[index].Id == command.Since) { found = index; break; }
            if (found < 0) throw new RpcCommandException(command.Id, command.Type, "Entry not found: " + command.Since);
            start = found + 1;
        }
        if (snapshot.Log.Entries.Length - start > _options.MaximumReturnedEntries) throw new RpcDispatchException(RpcDispatchFailure.ResourceLimit);
        return RpcCommandCodec.Build(writer =>
        {
            writer.WritePropertyName("entries"); writer.WriteStartArray();
            for (var index = start; index < snapshot.Log.Entries.Length; index++) writer.WriteRawValue(snapshot.Log.Entries[index].WireBody.ToString());
            writer.WriteEndArray(); writer.WriteString("leafId", snapshot.Context.LeafId);
        }, _options.MaximumOutputBytes);
    }
    private JsonData LastAssistantText()
    {
        string? text = null;
        foreach (var message in _session.Snapshot.Context.Messages.Reverse())
        {
            if (message.Role != "assistant") continue;
            var body = message.WireBody.Value;
            if (body.GetProperty("stopReason").GetString() == "aborted" && body.GetProperty("content").GetArrayLength() == 0) continue;
            var content = RpcCommandCodec.TrimSource(RpcCommandCodec.Text(message)); if (content.Length != 0) text = content;
            break;
        }
        // Pinned runtime returns undefined despite the docs/types saying null; JSON.stringify omits the text property.
        return RpcCommandCodec.Build(writer => { if (text is not null) writer.WriteString("text", text); }, _options.MaximumOutputBytes);
    }
    private JsonData Disposition(string disposition) => RpcCommandCodec.Build(writer => writer.WriteString("disposition", disposition), _options.MaximumOutputBytes);

    private async Task WriteAsync(JsonData record)
    {
        // This gate makes only one call pending in the owned writer. Waiters are bounded by admitted commands plus one sequential agent observer/settlement.
        await _wire.WaitAsync().ConfigureAwait(false);
        try { await _output.WriteAsync(record).ConfigureAwait(false); }
        catch (Exception error) { SignalFatal(RpcDispatchFailure.OutputFailed, error); throw new RpcDispatchException(RpcDispatchFailure.OutputFailed, error); }
        finally { _wire.Release(); }
    }
    private async Task WriteUiAsync(JsonData record, CancellationToken admissionToken, CancellationToken connectionToken)
    {
        using var queued = CancellationTokenSource.CreateLinkedTokenSource(admissionToken, connectionToken);
        await _wire.WaitAsync(queued.Token).ConfigureAwait(false);
        try
        {
            queued.Token.ThrowIfCancellationRequested();
            try { await _output.WriteAsync(record, connectionToken).ConfigureAwait(false); }
            catch (Exception error) { SignalFatal(RpcDispatchFailure.OutputFailed, error); throw new RpcDispatchException(RpcDispatchFailure.OutputFailed, error); }
        }
        finally { _wire.Release(); }
    }
    public Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        if (_inCallback.Value) throw new InvalidOperationException("RPC event callbacks cannot await their own settlement.");
        var nativeIdle = _session.WaitForIdleAsync(); Task settled;
        lock (_gate) settled = _run?.Settled.Task ?? Task.CompletedTask;
        var idle = Task.WhenAll(nativeIdle, settled); return cancellationToken.CanBeCanceled ? idle.WaitAsync(cancellationToken) : idle;
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate) if (_activePostOrigins != 0) throw new InvalidOperationException("Dispatcher disposal cannot join an active post-origin callback.");
        if (_inCallback.Value) throw new InvalidOperationException("RPC event callbacks cannot dispose their own dispatcher.");
        _ = _session.WaitForIdleAsync(); // Includes the coordinator/Agent's callback self-wait guards.
        return new(BeginShutdown(publicRequest: true));
    }
    private void SignalFatal(RpcDispatchFailure failure, Exception? cause = null)
    {
        lock (_gate) _fatal ??= new(failure, cause);
        _ = BeginShutdown();
    }
    private Task BeginShutdown(bool publicRequest = false)
    {
        Task pending; RunState? run; JsonlReader? input;
        lock (_gate)
        {
            if (publicRequest && _activePostOrigins != 0) throw new InvalidOperationException("Dispatcher close cannot join an active post-origin callback.");
            if (_disposal is not null) return _disposal;
            _closed = true; _disposal = _completion.Task;
            pending = _pendingIdle?.Task ?? Task.CompletedTask; run = _run; input = _input;
            if (run is not null) run.AbortRequested = true;
            _capacity.TrySetResult();
        }
        _ = ShutdownAsync(pending, run, input); return _completion.Task;
    }
    private async Task ShutdownAsync(Task pending, RunState? run, JsonlReader? input)
    {
        var failures = new List<Exception>();
        // Resolve dialogs before joining the callback/Agent which may be awaiting them. Cancel cooperative UI writes too.
        Task? uiCleanup = null;
        try { uiCleanup = _ui?.BeginDisconnect(); } catch (Exception error) { failures.Add(error); }
        try { _stopInput.Cancel(); } catch (Exception error) { failures.Add(error); }
        // Join the short admission transition before aborting: shutdown can race a prompt's generation publication.
        try
        {
            await _transitions.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_gate) { run = _run ?? run; if (run is not null) run.AbortRequested = true; }
                if (_sessionOwner is not null) _sessionOwner.Abort(); else _session.Abort();
            }
            finally { _transitions.Release(); }
        }
        catch (Exception error) { failures.Add(error); }
        if (input is not null) try { await input.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        // Internal fatal closure may start inside a callback: wait our run first, before invoking native self-wait guards.
        if (run is not null) try { await run.Settled.Task.ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        try { await pending.ConfigureAwait(false); await _session.WaitForIdleAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        await JoinPostOriginShutdown(failures).ConfigureAwait(false);
        if (uiCleanup is not null) try { await uiCleanup.ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        try { _subscription.Dispose(); } catch (Exception error) { failures.Add(error); }
        try { _operationSubscription?.Dispose(); } catch (Exception error) { failures.Add(error); }
        if (_startupWork is not null) try { await _startupWork.ConfigureAwait(false); await _startupReady.Task.ConfigureAwait(false); }
            catch (OperationCanceledException error) when (_startupToken.IsCancellationRequested && error.CancellationToken == _startupToken) { }
            catch (Exception error) when (ReferenceEquals(error, _startupFailure)) { /* Already retained as the operation failure. */ }
            catch (Exception error) { failures.Add(error); }
        if (_sessionOwner is not null) _sessionOwner.AttachmentChanged = null;
        _navigation = null;
        if (_ownership == RpcSessionOwnership.Owned)
            try
            {
                if (_sessionOwner is not null) await _sessionOwner.DisposeAsync().ConfigureAwait(false);
                else await _session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error) { failures.Add(error); }
        try { await _output.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        foreach (var resource in new IDisposable[] { _stopInput, _transitions, _wire, _queuePublication, _inputCommands })
            try { resource.Dispose(); } catch (Exception error) { failures.Add(error); }
        lock (_gate)
        {
            RpcDispatchException? failure = failures.Count == 0 ? _fatal : new(_fatal?.Failure ?? RpcDispatchFailure.CleanupFailed,
                new AggregateException(_fatal is null ? failures : failures.Prepend(_fatal)));
            _shutdownSettlement.TrySetResult(new(_fatal, failures.ToImmutableArray(), failure));
            if (failure is null) _completion.TrySetResult(); else _completion.TrySetException(failure);
        }
    }
    private JsonData TreePublicationFailureResponse(RpcCommandEnvelope command, string sessionId, long generation, string? leafId) =>
        RpcCommandCodec.Build(writer =>
        {
            if (command.Id is not null) writer.WriteString("id", command.Id);
            writer.WriteString("type", "response"); writer.WriteString("command", command.Type);
            writer.WriteBoolean("success", false);
            writer.WriteString("error", "Session tree selection committed; lifecycle publication failed.");
            writer.WriteBoolean("committed", true);
            writer.WriteStartObject("data"); writer.WriteString("disposition", "Selected");
            writer.WriteString("sessionId", sessionId); writer.WriteNumber("generation", generation);
            writer.WriteString("leafId", leafId); writer.WriteEndObject();
        }, _options.MaximumOutputBytes);
    private void AddPending(int bytes = 0) { if (_pending++ == 0) _pendingIdle = NewGate(); _retainedCommandBytes += bytes; }
    private void CheckOpen() { lock (_gate) ThrowOpen(); }
    private void ThrowOpen() { if (_closed) throw _fatal ?? new RpcDispatchException(RpcDispatchFailure.Disposed); }
    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>Actual postcommit receipt and original publication evidence; selection is never rolled back.</summary>
public sealed class RpcSessionTreePublicationException(SessionTreeNavigationReceipt committedReceipt,
    Task? original, Exception evidence, Exception direct)
    : IOException("Session tree selection committed; lifecycle publication failed.", evidence)
{
    public SessionTreeNavigationReceipt CommittedReceipt { get; } = committedReceipt;
    public Task? Original { get; } = original;
    public Exception Evidence { get; } = evidence;
    public Exception Direct { get; } = direct;
}
