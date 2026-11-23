// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/coding-agent/src/core/agent-session.ts (agent_settled).
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
    /// <summary>agent-session.ts setSteeringMode, setFollowUpMode, setAutoCompactionEnabled and setAutoRetryEnabled save the global
    /// setting (<c>steeringMode</c>, <c>followUpMode</c>, <c>compaction.enabled</c>, <c>retry.enabled</c>) through the settings manager;
    /// the host persists each accepted change here.</summary>
    public Action<string, System.Text.Json.Nodes.JsonNode>? GlobalSettingChanged { get; set; }
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
        /// <summary>A run an extension command's handler triggered: the command holds input admission while its handler runs, and the
        /// run settles inside it (agent-session.ts _startRun outlives the sendMessage call).</summary>
        public bool CommandTriggered, InputHeldByCommand, MonitorBorrowingInput, InputReleasePending;
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
        finally { ScheduleIdleShutdownCheck(); }
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
            await foreach (var admission in ReadAttachedInputAsync(input, linked.Token).ConfigureAwait(false))
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
    private static async Task ObserveUnadoptedRunAsync(Task<AgentLoopResult> run)
    { try { await run.ConfigureAwait(false); } catch (Exception) { /* Shutdown joins the session's idle state; the run is not this host's. */ } }
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
            // rpc-mode.ts handleInputLine: a command that throws answers error(id, type, commandError.message) — the thrown error's
            // message (a prompt refused by its preflight answers with the refusal's), never its stack. Cancellation keeps PiSharp's texts.
            var message = error switch
            {
                OperationCanceledException when originatingAttachment is not null && !ReferenceEquals(originatingAttachment, _sessionOwner!.Current) =>
                    "Command canceled after session replacement committed; inspect the current session and durable state.",
                OperationCanceledException => "Command canceled before acceptance.",
                _ => error.Message
            };
            // PISHARP_DEBUG=1 also writes the full exception (with its stack) to stderr for bug reports.
            if (Environment.GetEnvironmentVariable("PISHARP_DEBUG") == "1")
                Console.Error.WriteLine($"pisharp: {command?.Type ?? "parse"} failed: {error}");
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
            ScheduleIdleShutdownCheck();
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
                GlobalSettingChanged?.Invoke(command.Type == "set_steering_mode" ? "steeringMode" : "followUpMode", command.Mode == "all" ? "all" : "one-at-a-time");
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
                // rpc-mode.ts clone: a session without a current entry answers this error instead of forking.
                if (command.Type == "clone" && expected.Session.Snapshot.Context.LeafId is null)
                    throw new RpcCommandException(command.Id, command.Type, "Cannot clone session: no current entry selected");
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
