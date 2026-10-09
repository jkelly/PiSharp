// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session.ts (durationMs, agent_settled.aborted).
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

public sealed record PersistentAgentSessionOptions(bool UseLatestLeaf = true, string? SelectedLeafId = null,
    AgentOptions? AgentOptions = null, SessionLogStoreOptions? SessionLogStoreOptions = null,
    SessionContextProjectionOptions? ContextOptions = null)
{
    public PiSharp.CodingAgent.ToolSelection.AllowedToolSelection? LifetimeToolSelection { get; init; }
}
public enum PersistentAgentSessionFailure
{
    InvalidConfiguration, UnsupportedThinkingLevel, ModelMismatch, InvalidCommit,
    AppendFailed, RunFailed, CleanupFailed, Faulted, Disposed,
    InvalidExtensionEntry, UnsupportedExtensionEntryVersion, ExtensionEntryLimitExceeded, StaleSession, SessionReplaced
}
public sealed record PersistentAgentSessionFault(PersistentAgentSessionFailure Failure,
    SessionLogStoreFailure? StorageFailure = null, bool MayHaveWritten = false, bool DurableFlushCompleted = false);
public sealed class PersistentAgentSessionException : Exception
{
    public PersistentAgentSessionFault Fault { get; }
    internal PersistentAgentSessionException(PersistentAgentSessionFault fault, Exception? inner = null) : base(fault.Failure switch
    {
        PersistentAgentSessionFailure.ModelMismatch => "Restored session model does not match the supplied runtime configuration.",
        PersistentAgentSessionFailure.UnsupportedThinkingLevel => "Restored session thinking level requires unfinished runtime support.",
        PersistentAgentSessionFailure.Faulted => "Session continuation requires close and explicit inspection.",
        PersistentAgentSessionFailure.Disposed => "Session coordinator is closing or disposed.",
        PersistentAgentSessionFailure.InvalidConfiguration => "Session coordinator configuration is invalid.",
        PersistentAgentSessionFailure.InvalidExtensionEntry => "Extension session entry is invalid.",
        PersistentAgentSessionFailure.UnsupportedExtensionEntryVersion => "Extension session entry envelope version is unsupported.",
        PersistentAgentSessionFailure.ExtensionEntryLimitExceeded => "Extension session entry exceeds configured bounds.",
        PersistentAgentSessionFailure.StaleSession => "Extension session entry targets a different coordinator identity.",
        PersistentAgentSessionFailure.SessionReplaced => "Session coordinator has been replaced.",
        _ => "Session coordinator operation failed."
    }, inner) => Fault = fault;
}
public sealed record PersistentAgentSessionSnapshot(AgentSnapshot Agent, SessionLogStoreSnapshot Log,
    SessionContextProjection Context, PersistentAgentSessionFault? Fault, bool IsDisposed, bool IsConfiguring = false)
{
    public bool IsAdmittingInput { get; init; }
    public bool InputCancellationCallbackFailed { get; init; }
    public bool IsAppendingExtensionEntry { get; init; }
    public bool IsEditingContext { get; init; }
    public bool IsCompacting { get; init; }
    public bool AutoCompactionEnabled { get; init; }
    public SessionAutomaticCompactionStatus? LastAutomaticCompaction { get; init; }
    public bool IsRetired { get; init; }
    public bool IsProcessingOperation { get; init; }
    public SessionOperationPhase OperationPhase { get; init; }
    public long OperationGeneration { get; init; }
    public bool AutoRecoveryEnabled { get; init; }
}

/// <summary>Owns one Agent and one explicit-path durable writer, with acknowledged end-event commit barriers.</summary>
public sealed partial class PersistentAgentSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly AsyncLocal<TaskCompletionSource?> _configurationCallback = new();
    private readonly AsyncLocal<InputSubmission?> _inputCallback = new();
    private readonly AsyncLocal<bool> _queueValidationCallback = new();
    private readonly NativeAgent _agent;
    private readonly SessionLogStore _store;
    private readonly SessionContextProjector _projector;
    private readonly SessionEntryCodec _codec;
    private readonly SemaphoreSlim _commits = new(1, 1);
    private AgentConfiguration _configuration;
    private readonly AgentOptions? _agentOptions;
    private SessionRuntimeRegistry? _registry;
    private SessionRuntimeLease? _runtimeLease;
    private CancellationTokenSource? _invocationLifetime;
    private readonly Func<long> _clock;
    private readonly Func<string> _nextEntryId;
    private readonly CancellationTokenSource _closing = new();
    private SessionLogStoreSnapshot _acknowledgedLog;
    private SessionContextProjection _context;
    private PersistentAgentSessionFault? _fault;
    private TaskCompletionSource? _active;
    private Task? _disposal;
    private bool _disposed;
    private bool _configuring;
    /// <summary>The idle signal of a tool catalog publication in progress (an MCP server that connected in the background).</summary>
    private TaskCompletionSource? _catalogPublication;
    private bool _appendingExtensionEntry;
    private bool _editingContext;
    private bool _compacting;
    private ContextEditCancellation? _contextEditCancellation;
    private sealed class ContextEditCancellation
    {
        internal readonly CancellationTokenSource Abort = new();
        internal int CancelUsers;
        internal TaskCompletionSource? CancelIdle;
    }
    private bool _replacing, _retired;
    private InputSubmission? _inputSubmission;
    private bool _inputCancellationCallbackFailed;

    // Lifecycle reservation only. Messages always use the existing Agent/queue and durable sink.
    private sealed class InputSubmission
    {
        public readonly CancellationTokenSource Abort = new();
        public readonly TaskCompletionSource Idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? CancelIdle;
        public int CancelUsers;
        public bool Releasing;
    }

    private sealed class Bridge : IAgentEventSink
    {
        public PersistentAgentSession? Owner;
        public ValueTask EmitAsync(AgentEvent observation, CancellationToken cancellationToken) =>
            Owner!.CommitAsync(observation);
    }

    private PersistentAgentSession(string path, SessionLogStore store, NativeAgent agent, Bridge bridge,
        SessionContextProjector projector, SessionEntryCodec codec, SessionContextProjection context,
        AgentConfiguration configuration, Func<long> clock, Func<string> nextEntryId, AgentOptions? agentOptions = null,
        SessionLogReaderOptions? logBounds = null)
    {
        Path = path; WorkingDirectory = store.Snapshot.Header.WireBody.Value.GetProperty("cwd").GetString()!;
        _store = store; _agent = agent; _projector = projector; _codec = codec; _context = context;
        _configuration = configuration; _clock = clock; _nextEntryId = nextEntryId;
        _agentOptions = agentOptions;
        _acknowledgedLog = store.Snapshot;
        bridge.Owner = this;
        _agent.ConfigureAndReplaceMessages(RecoveryConfiguration(configuration), SessionContextProjector.AgentMessages(context));
    }

    public string Path { get; }
    /// <summary>The configured local session path, including a lazy path not yet materialized.
    /// In-memory namespace identities are not filesystem session paths.</summary>
    public string? SessionFile => Snapshot.Log.StorageDurability == SessionLogStorageDurability.VolatileMemory ? null : Path;
    public string WorkingDirectory { get; }

    /// <summary>Queries admitted native capabilities. A registry is required to query a different model.</summary>
    public ImmutableArray<string> GetSupportedThinkingLevels(ModelDescriptor? model = null)
    {
        SessionRuntimeRegistry? registry; AgentConfiguration configuration;
        lock (_gate) { ThrowAvailable(); registry = _registry; configuration = _configuration; }
        var selected = model ?? configuration.Model;
        if (registry is not null) return registry.GetSupportedThinkingLevels(selected);
        if (selected != configuration.Model) throw new ArgumentException("Unknown session model.", nameof(model));
        return ThinkingLevels.GetSupported(configuration.Transport, selected);
    }
    /// <summary>Read-only self-wait detection for the current host call; this does not transfer input authority.</summary>
    internal bool IsExecutingInputCallback
    {
        get
        {
            lock (_gate) return _inputSubmission is { Releasing: false } current &&
                ReferenceEquals(_inputCallback.Value, current);
        }
    }
    public PersistentAgentSessionSnapshot Snapshot
    {
        get
        {
            lock (_gate) return new(_agent.Snapshot, _acknowledgedLog, _context, _fault, _disposed, _configuring)
            { IsAdmittingInput = _inputSubmission is not null, InputCancellationCallbackFailed = _inputCancellationCallbackFailed,
                IsAppendingExtensionEntry = _appendingExtensionEntry, IsEditingContext = _editingContext, IsCompacting = _compacting,
                AutoCompactionEnabled = _automaticCompaction?.Request.Settings?.Enabled == true,
                LastAutomaticCompaction = _lastAutomaticCompaction, IsRetired = _retired,
                // Cancellation admission closes before existing callback users join. The operation remains owned through that join.
                IsProcessingOperation = _active is not null && _operationPhase != SessionOperationPhase.Idle,
                OperationPhase = _operationPhase, OperationGeneration = _operationGeneration,
                AutoRecoveryEnabled = _automaticCompaction is not null && _recoveryDesiredOutput is not null };
        }
    }

    public static async Task<PersistentAgentSession> CreateAsync(string path, SessionEntry header,
        AgentConfiguration configuration, Func<long> clock, Func<string> nextEntryId,
        PersistentAgentSessionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var (configured, projector, codec, agent, bridge) = Admit(path, configuration, clock, nextEntryId, options);
        SessionLogStore? store = null;
        try
        {
            ArgumentNullException.ThrowIfNull(header);
            header = codec.Read(header.WireBody.Value);
            if (!header.IsHeader || string.IsNullOrWhiteSpace(header.WireBody.Value.GetProperty("cwd").GetString()) ||
                !configured.UseLatestLeaf || configured.SelectedLeafId is not null)
                throw Error(PersistentAgentSessionFailure.InvalidConfiguration);
            cancellationToken.ThrowIfCancellationRequested();
            var model = Record(codec, "model_change", Identity(nextEntryId, header.Id, []), null, clock, writer =>
            {
                writer.WriteString("provider", configuration.Model.Provider);
                writer.WriteString("modelId", configuration.Model.Id);
            });
            var thinking = Record(codec, "thinking_level_change", Identity(nextEntryId, header.Id, [model]), model.Id,
                clock, writer => writer.WriteString("thinkingLevel", configuration.ThinkingLevel));
            ImmutableArray<SessionEntry> initial = [model, thinking];
            var context = projector.Project(initial, thinking.Id, cancellationToken);
            store = await SessionLogStore.CreateNewAsync(path, header, configured.SessionLogStoreOptions, cancellationToken).ConfigureAwait(false);
            // Once the file/header creation is admitted, complete the initial metadata checkpoint.
            await store.AppendAsync(initial, CancellationToken.None).ConfigureAwait(false);
            return new(path, store, agent, bridge, projector, codec, context, configuration, clock, nextEntryId,
                configured.AgentOptions, configured.SessionLogStoreOptions?.ReaderOptions);
        }
        catch
        {
            await CloseAfterAdmissionFailureAsync(agent, store).ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<PersistentAgentSession> OpenAsync(string path, AgentConfiguration configuration,
        Func<long> clock, Func<string> nextEntryId, PersistentAgentSessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (configured, projector, codec, agent, bridge) = Admit(path, configuration, clock, nextEntryId, options);
        SessionLogStore? store = null;
        try
        {
            store = await SessionLogStore.OpenAsync(path, configured.SessionLogStoreOptions, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(store.Snapshot.Header.WireBody.Value.GetProperty("cwd").GetString()))
                throw Error(PersistentAgentSessionFailure.InvalidConfiguration);
            var leaf = configured.UseLatestLeaf ? store.Snapshot.LeafId : configured.SelectedLeafId;
            var context = projector.Project(store.Snapshot.Entries, leaf, cancellationToken);
            ValidateRuntimeContext(context, configuration);
            agent.ReplaceMessages(SessionContextProjector.AgentMessages(context));
            cancellationToken.ThrowIfCancellationRequested();
            return new(path, store, agent, bridge, projector, codec, context, configuration, clock, nextEntryId,
                configured.AgentOptions, configured.SessionLogStoreOptions?.ReaderOptions);
        }
        catch
        {
            await CloseAfterAdmissionFailureAsync(agent, store).ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<PersistentAgentSession> CreateAsync(string path, SessionEntry header,
        SessionRuntimeRegistry registry, ModelDescriptor initialModel, Func<long> clock, Func<string> nextEntryId,
        PersistentAgentSessionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry = registry.RetainToolSelection(options?.LifetimeToolSelection);
        var selection = await registry.PrepareAndDrainAsync(() => registry.Resolve(initialModel, [], registry.GetDefaultThinkingLevel(initialModel), cancellationToken), cancellationToken).ConfigureAwait(false);
        var session = await CreateAsync(path, header, selection.Configuration, clock, nextEntryId, options, cancellationToken).ConfigureAwait(false);
        session._registry = registry;
        return session;
    }

    /// <summary>Restores selected model and declared tool loadout through explicit borrowed runtime bindings.</summary>
    public static async Task<PersistentAgentSession> OpenWithRegistryAsync(string path, SessionRuntimeRegistry registry,
        Func<long> clock, Func<string> nextEntryId, PersistentAgentSessionOptions? options = null,
        ModelDescriptor? fallbackModel = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return await OpenWithRegistryFactoryAsync(path, _ => registry, clock, nextEntryId, options,
            fallbackModel, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds explicit borrowed cwd-dependent bindings from the actual fully validated header
    /// while its store owns the writer lease. The factory runs before source retirement/publication.
    /// Persisted cwd is data: the trusted host factory decides which directories and actions are allowed.</summary>
    public static async Task<PersistentAgentSession> OpenWithRegistryFactoryAsync(string path,
        Func<string, SessionRuntimeRegistry> registryForWorkingDirectory,
        Func<long> clock, Func<string> nextEntryId, PersistentAgentSessionOptions? options = null,
        ModelDescriptor? fallbackModel = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registryForWorkingDirectory);
        return await OpenWithRuntimeFactoryAsync(path,
            (cwd, _) => ValueTask.FromResult(new SessionRuntimeLease(registryForWorkingDirectory(cwd) ??
                throw Error(PersistentAgentSessionFailure.InvalidConfiguration))),
            clock, nextEntryId, options, fallbackModel, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Acquires fresh host-owned services using the validated actual header. Ownership is
    /// transferred only through a fresh lease; all acquired services are joined on failed admission.</summary>
    public static async Task<PersistentAgentSession> OpenWithRuntimeFactoryAsync(string path,
        Func<string, CancellationToken, ValueTask<SessionRuntimeLease>> runtimeForWorkingDirectory,
        Func<long> clock, Func<string> nextEntryId, PersistentAgentSessionOptions? options = null,
        ModelDescriptor? fallbackModel = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeForWorkingDirectory);
        ArgumentNullException.ThrowIfNull(clock); ArgumentNullException.ThrowIfNull(nextEntryId);
        var configured = Timed(options);
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path) ||
            configured.UseLatestLeaf && configured.SelectedLeafId is not null ||
            configured.AgentOptions?.CancellationBehavior == AgentCancellationBehavior.Propagate)
            throw Error(PersistentAgentSessionFailure.InvalidConfiguration);
        var projector = new SessionContextProjector(configured.ContextOptions);
        var codec = new SessionEntryCodec(configured.SessionLogStoreOptions?.ReaderOptions?.CodecOptions);
        var store = await SessionLogStore.OpenAsync(path, configured.SessionLogStoreOptions, cancellationToken).ConfigureAwait(false);
        NativeAgent? agent = null;
        SessionRuntimeLease? runtime = null;
        PersistentAgentSession? opened = null;
        try
        {
            if (string.IsNullOrWhiteSpace(store.Snapshot.Header.WireBody.Value.GetProperty("cwd").GetString()))
                throw Error(PersistentAgentSessionFailure.InvalidConfiguration);
            var leaf = configured.UseLatestLeaf ? store.Snapshot.LeafId : configured.SelectedLeafId;
            var context = projector.Project(store.Snapshot.Entries, leaf, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var acquired = await runtimeForWorkingDirectory(store.Snapshot.Header.WireBody.Value.GetProperty("cwd").GetString()!, cancellationToken).ConfigureAwait(false)
                ?? throw Error(PersistentAgentSessionFailure.InvalidConfiguration);
            acquired.Claim(); runtime = acquired;
            var registry = runtime.Registry.RetainToolSelection(configured.LifetimeToolSelection);
            cancellationToken.ThrowIfCancellationRequested();
            // Without initial names the transcript's loadout is restored by name with the current bindings (Pi 0.99.2).
            var loadout = await registry.PrepareAndDrainAsync(() => registry.ResolveRestored(context, fallbackModel, cancellationToken,
                registry.InitialActiveToolNames), cancellationToken).ConfigureAwait(false);
            var selection = loadout.Selection;
            var bridge = new Bridge();
            agent = new(selection.Configuration, clock, bridge, configured.AgentOptions);
            agent.ReplaceMessages(SessionContextProjector.AgentMessages(context));
            cancellationToken.ThrowIfCancellationRequested();
            opened = new(path, store, agent, bridge, projector, codec, context, selection.Configuration, clock, nextEntryId,
                configured.AgentOptions, configured.SessionLogStoreOptions?.ReaderOptions)
            { _registry = registry, _runtimeLease = runtime };
            var restored = selection.Configuration.Tools.Select(tool => tool.Name).ToImmutableArray();
            if (registry.InitialActiveToolNames is not null)
                await opened.ConfigureAsync(new() { ActiveToolNames = restored, ReplaceDeclarations = loadout.RequiresRecord }, cancellationToken).ConfigureAwait(false);
            // Restored tools that are not registered yet, such as MCP tools whose server is still connecting, stay pending.
            else if (loadout.RequiresRecord)
                await opened.RecordRestoredToolsAsync(restored, loadout.Pending, cancellationToken).ConfigureAwait(false);
            return opened;
        }
        catch (Exception admission)
        {
            var failures = new List<Exception>();
            if (opened is not null)
            { try { await opened.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); } }
            else
            {
                if (agent is not null) try { await agent.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
                try { await store.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
                if (runtime is not null) try { await runtime.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            }
            if (failures.Count != 0) throw new AggregateException("Session admission and owned cleanup failed.", new[] { admission }.Concat(failures));
            throw;
        }
    }

    internal void OwnRuntime(SessionRuntimeLease runtime) => _runtimeLease = runtime;
    internal bool RequiresRuntimeOwnerBinding => _runtimeLease?.RequiresOwnerBinding == true;
    internal void BindRuntimeOwner(ReplaceableAgentSession owner, AgentSessionAttachment attachment)
    { if (_runtimeLease?.RequiresOwnerBinding == true) _runtimeLease.BindOwner(owner, attachment); }
    internal async Task ReleaseRuntimeAfterBindingFailureAsync()
    {
        SessionRuntimeLease? runtime;
        lock (_gate)
        {
            if (!_replacing || _active is not null) throw new InvalidOperationException("Failed runtime release requires reserved idle ownership.");
            runtime = _runtimeLease; _runtimeLease = null;
        }
        if (runtime is not null) await runtime.DisposeAsync().ConfigureAwait(false);
    }

    internal void RetireInvocationOwner() => _invocationLifetime?.Cancel();

    // Called only by the attachment owner under the coordinator replacement reservation.
    internal void BindInvocationOwner(long generation, CancellationToken lifetime)
    {
        if (_registry?.RequiresInvocationOwner != true) return;
        if (!_replacing || _invocationLifetime is not null)
            throw new InvalidOperationException("Nested invocation owner requires a fresh reserved coordinator.");
        var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, _closing.Token);
        try
        {
            // Pi sets no limit on the nested calls of codemode scripts.
            var registry = _registry.BindInvocationOwner(new(generation, linked.Token) { UncountedNestedCallTools = ["codemode"],
                LateNestedTools = LateNestedInvoker });
            var selection = registry.Resolve(_context, _configuration.Model);
            _agent.ConfigureAndReplaceMessages(RecoveryConfiguration(selection.Configuration), SessionContextProjector.AgentMessages(_context));
            _configuration = selection.Configuration;
            _registry = registry;
            _invocationLifetime = linked;
        }
        catch { linked.Dispose(); throw; }
    }

    /// <summary>Current ordered logical model-active names; a pending selection publishes at the next request boundary.</summary>
    public ImmutableArray<string> GetActiveTools()
    {
        return GetToolActivationSelection().Names;
    }

    /// <summary>Host-owned activation at the durable idle boundary. Unknown and hidden names, and names the lifetime
    /// selection keeps out, are ignored (source setActiveToolsByName); they do not become pending.
    /// An in-flight run, pending input, retired owner or cancelled request cannot publish a new loadout.</summary>
    public Task<PersistentAgentSessionSnapshot> SetActiveToolsAsync(ImmutableArray<string> names,
        CancellationToken cancellationToken = default) => ConfigureAsync(new() { ActiveToolNames = names }, cancellationToken);

    /// <summary>Reserves idle admission, validates prospective selected state, and publishes it only after durable acknowledgement.</summary>
    public Task<PersistentAgentSessionSnapshot> ConfigureAsync(SessionRuntimeUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.ActiveToolNames is not null && update.SystemMessage is not null)
            throw new ArgumentException("Supply active names or a system update, not both.", nameof(update));
        TaskCompletionSource idle;
        lock (_gate)
        {
            ThrowAvailable();
            ThrowUserBashMutationLocked();
            if (_registry is null) throw Error(PersistentAgentSessionFailure.InvalidConfiguration);
            ThrowUserBashMutationLocked();
            if (_active is not null || _inputSubmission is not null) throw new InvalidOperationException("Session is already processing.");
            var snapshot = _agent.Snapshot;
            if (!snapshot.PendingInputs.IsEmpty || snapshot.SteeringCount != 0 || snapshot.FollowUpCount != 0)
                throw Error(PersistentAgentSessionFailure.InvalidConfiguration);
            cancellationToken.ThrowIfCancellationRequested();
            idle = new(TaskCreationOptions.RunContinuationsAsynchronously); _active = idle; _configuring = true;
        }
        return ConfigureCoreAsync(update, cancellationToken, idle);
    }
    private async Task<PersistentAgentSessionSnapshot> ConfigureCoreAsync(SessionRuntimeUpdate update,
        CancellationToken token, TaskCompletionSource idle)
    {
        var writeAdmitted = false;
        var commitHeld = false;
        var priorCallback = _configurationCallback.Value;
        _configurationCallback.Value = idle;
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _closing.Token);
            var work = cancellation.Token;
            await _commits.WaitAsync(work).ConfigureAwait(false); commitHeld = true;
            await DrainLoadoutDiagnosticsAsync(work).ConfigureAwait(false);
            SessionContextProjection context; SessionLogStoreSnapshot log; object? priorPromptRevision;
            lock (_gate) { context = _context; log = _acknowledgedLog; priorPromptRevision = _acknowledgedPromptRevision; }
            var entries = ImmutableArray.CreateBuilder<SessionEntry>();
            var parent = context.LeafId;
            if (update.Model is { } model)
                Add("model_change", writer =>
                {
                    writer.WriteString("provider", model.Provider);
                    writer.WriteString("modelId", model.Id);
                });
            if (update.ThinkingLevel is { } level && level != context.ThinkingLevel)
                Add("thinking_level_change", writer => writer.WriteString("thinkingLevel", level));
            var systemUpdate = update.ActiveToolNames is { } activeNames
                ? _registry!.CreateActivationMessage(activeNames, RecordedActiveToolNames(context, work), _clock(), work, update.ReplaceDeclarations)
                : update.SystemMessage;
            SessionPromptSectionPreparation? promptPreparation = null;
            if (update.SystemMessage is null)
            {
                var names = update.ActiveToolNames ?? _configuration.Tools.Select(tool => tool.Name).ToImmutableArray();
                _activationPreparation.Value = true;
                try { (systemUpdate, promptPreparation) = _registry!.PreparePromptSectionMessage(names, context.Messages, systemUpdate, _clock(), work); }
                finally { _activationPreparation.Value = false; }
            }
            if (systemUpdate is { } system)
            {
                if (system.Role != "system" || system.WireBody is null)
                    throw Error(PersistentAgentSessionFailure.InvalidConfiguration);
                Add("message", writer =>
                {
                    writer.WritePropertyName("message"); writer.WriteRawValue(system.WireBody.Value.GetRawText());
                });
            }
            var prospective = _projector.Project(log.Entries.AddRange(entries), parent, work);
            if (entries.Count == 0)
            {
                promptPreparation?.ValidateSource();
                work.ThrowIfCancellationRequested();
                if (update.ActiveToolNames is not null) lock (_gate)
                { work.ThrowIfCancellationRequested(); if (_pendingActivation is not null) PrepareActivationRestoration(_configuration)(); }
                return Snapshot with { IsConfiguring = false };
            }
            var selection = await PrepareAndDrainLoadoutAsync(() => _registry!.Resolve(prospective, update.Model ?? _configuration.Model, work), work).ConfigureAwait(false);
            // A stored selection only carries provider/modelId; preserve exact API matching for an explicitly requested model.
            if (update.Model is { } requested && selection.Configuration.Model != requested)
                throw Error(PersistentAgentSessionFailure.InvalidConfiguration);
            await using (var probe = new NativeAgent(selection.Configuration, _clock, new NoopSink(), _agentOptions))
                probe.ConfigureAndReplaceMessages(selection.Configuration, SessionContextProjector.AgentMessages(prospective));
            work.ThrowIfCancellationRequested();
            Action restoreActivation;
            lock (_gate) restoreActivation = PrepareActivationRestoration(selection.Configuration);
            promptPreparation?.ValidateSource();
            work.ThrowIfCancellationRequested();
            lock (_gate)
                if (!ReferenceEquals(_context, context) || !ReferenceEquals(_acknowledgedLog, log) || !ReferenceEquals(_active, idle) ||
                    !ReferenceEquals(_acknowledgedPromptRevision, priorPromptRevision))
                    throw new InvalidOperationException("Prompt configuration reservation changed.");
            writeAdmitted = true;
            var acknowledged = await _store.AppendAsync(entries.ToImmutable(), work).ConfigureAwait(false);
            if (!acknowledged.CheckpointAcknowledged) throw Error(PersistentAgentSessionFailure.InvalidCommit);
            ModelDescriptor previousModel; long generation;
            lock (_gate)
            {
                previousModel = _configuration.Model; generation = _operationGeneration;
                _agent.ConfigureAndReplaceMessages(RecoveryConfiguration(selection.Configuration), SessionContextProjector.AgentMessages(prospective));
                if (update.ActiveToolNames is not null)
                    SelectPendingToolsLocked(_configuration.Tools.Select(tool => tool.Name).ToImmutableArray(),
                        selection.Configuration.Tools.Select(tool => tool.Name).ToImmutableArray());
                _configuration = selection.Configuration;
                _acknowledgedLog = acknowledged.Snapshot;
                _context = prospective;
                _acknowledgedPromptRevision = promptPreparation?.Revision ?? _acknowledgedPromptRevision;
                restoreActivation();
            }
            // Source setModel/setThinkingLevel: thinking_level_changed (and thinking_level_select) when the level changed,
            // then model_select when the model changed. Listener failures cannot undo the committed configuration.
            writeAdmitted = false;
            if (entries.Any(entry => entry.Type == "thinking_level_change"))
                await EmitOperationAsync(new SessionThinkingLevelChanged(generation, prospective.ThinkingLevel, context.ThinkingLevel)).ConfigureAwait(false);
            if (update.Model is { } selected && selected != previousModel)
                await EmitOperationAsync(new SessionModelSelected(generation, selected, previousModel, update.ModelSelectSource ?? "set")).ConfigureAwait(false);
            return Snapshot with { IsConfiguring = false };

            void Add(string type, Action<Utf8JsonWriter> fields)
            {
                work.ThrowIfCancellationRequested();
                var entry = Record(_codec, type, Identity(_nextEntryId, log.Header.Id, log.Entries.AddRange(entries)), parent, _clock, fields);
                entries.Add(entry); parent = entry.Id;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || _closing.IsCancellationRequested)
        { throw; }
        catch (SessionLogStoreException storage)
        {
            var fault = new PersistentAgentSessionFault(PersistentAgentSessionFailure.AppendFailed, storage.Failure,
                storage.MayHaveWritten, storage.DurableFlushCompleted);
            if (storage.MayHaveWritten || _store.IsPoisoned) lock (_gate) _fault ??= fault;
            throw new PersistentAgentSessionException(fault);
        }
        catch
        {
            if (writeAdmitted) lock (_gate) _fault ??= new(PersistentAgentSessionFailure.InvalidCommit);
            throw;
        }
        finally
        {
            if (commitHeld) _commits.Release();
            lock (_gate) if (ReferenceEquals(_active, idle)) { _active = null; _configuring = false; }
            idle.TrySetResult();
            _configurationCallback.Value = priorCallback;
        }
    }
    private sealed class NoopSink : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken cancellationToken) => ValueTask.CompletedTask; }

    // A command may replace its own idle input reservation, but must never await that callback's settlement.
    // No executable host/plugin work occurs under this reservation's state lock.
    internal ReplacementReservation ReserveReplacement()
    {
        lock (_gate)
        {
            ThrowAvailable();
            ThrowUserBashMutationLocked();
            var agent = _agent.Snapshot;
            if (_active is not null || _retrySettingsWrite is not null || _loadoutDrains.Count != 0 || _synchronousLoadoutWork != 0 || agent.IsRunning || agent.SteeringCount != 0 || agent.FollowUpCount != 0 ||
                _inputSubmission is not null && !IsExecutingInputCallback || !agent.PendingInputs.IsEmpty)
                throw new InvalidOperationException("Session replacement requires idle execution and empty pending input queues.");
            _replacing = true;
            return new(this);
        }
    }

    internal sealed class ReplacementReservation(PersistentAgentSession owner) : IDisposable
    {
        private bool committed, released;
        internal SessionCreationSetupWriter CreateSetupWriter(CancellationToken token)
        {
            lock (owner._gate) { ValidateCatalogAuthority(owner); return owner.CreateSetupWriter(this, token); }
        }
        internal void ValidateCatalogAuthority(PersistentAgentSession expected)
        {
            if (!ReferenceEquals(owner, expected) || released || committed || !owner._replacing ||
                owner._disposed || owner._admissionStopped || owner._retired || owner._fault is not null)
                throw new InvalidOperationException("Catalog publication requires the exact live retirement reservation.");
        }
        internal SessionRuntimeRegistry CaptureToolCatalogRegistry()
        {
            lock (owner._gate) { ValidateCatalogAuthority(owner); return owner._registry ?? throw Error(PersistentAgentSessionFailure.InvalidConfiguration); }
        }
        internal System.Collections.Immutable.ImmutableArray<string> CaptureActiveToolNames()
        {
            lock (owner._gate) { ValidateCatalogAuthority(owner); return owner._pendingActivation?.Names ??
                owner._configuration.Tools.Select(tool => tool.Name).ToImmutableArray(); }
        }
        internal Task<SessionToolCatalogReceipt> PublishToolCatalogAsync(SessionRuntimeRegistry expected,
            SessionRuntimeRegistry replacement, System.Collections.Immutable.ImmutableArray<string> activeNames, Action commit)
        {
            lock (owner._gate) ValidateCatalogAuthority(owner);
            return owner.AdmitToolCatalogPublication(expected, replacement, activeNames, commit, CancellationToken.None, this);
        }
        internal async Task DrainLoadoutDiagnosticsAsync(CancellationToken token)
        {
            SessionRuntimeRegistry? registry;
            lock (owner._gate)
            {
                if (released || committed || !owner._replacing || owner._disposed || owner._admissionStopped || owner._retired)
                    throw new InvalidOperationException("Staged diagnostic drain requires the original replacement reservation.");
                registry = owner._registry;
            }
            if (registry is null || !registry.HasLoadoutDiagnosticDrain) return;
            var prior = owner._inLoadoutDiagnosticDrain.Value; owner._inLoadoutDiagnosticDrain.Value = true;
            try { await registry.DrainLoadoutDiagnosticsAsync(token).ConfigureAwait(false); }
            finally { owner._inLoadoutDiagnosticDrain.Value = prior; }
        }
        internal async Task RetireWriterAsync()
        {
            lock (owner._gate)
            {
                if (released) throw new InvalidOperationException("Session replacement reservation is already released.");
                owner._retired = true; committed = true;
            }
            try { await owner._store.DisposeAsync().ConfigureAwait(false); }
            catch
            {
                lock (owner._gate) owner._fault ??= new(PersistentAgentSessionFailure.CleanupFailed);
                throw;
            }
        }
        public void Dispose()
        {
            lock (owner._gate)
            {
                if (released) return;
                released = true;
                if (!committed) owner._replacing = false;
            }
        }
    }

    /// <summary>Appends a bounded PiSharp extension-state custom record through the actual durable writer.
    /// ExpectedSessionId checks this coordinator only; the host must separately enforce attachment generation and owner authority.</summary>
    public Task<SessionExtensionEntryReceipt> AppendExtensionEntryAsync(string expectedSessionId,
        SessionExtensionEntryDraft draft, CancellationToken cancellationToken = default)
    {
        ValidateExtensionEntry(expectedSessionId, draft);
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource idle; CancellationToken inputAbort = default;
        lock (_gate)
        {
            ThrowAvailable(); ThrowInputMutation();
            if (!string.Equals(expectedSessionId, _acknowledgedLog.Header.Id, StringComparison.Ordinal))
                throw Error(PersistentAgentSessionFailure.StaleSession);
            var callback = _inputCallback.Value;
            if (callback is not null && (!ReferenceEquals(callback, _inputSubmission) || callback.Releasing))
                throw new InvalidOperationException("Input callback no longer has an admitted submission.");
            if (_active is not null || _inputSubmission is not null && !ReferenceEquals(callback, _inputSubmission))
                throw new InvalidOperationException("Session is already processing.");
            if (!_agent.Snapshot.PendingInputs.IsEmpty) throw Error(PersistentAgentSessionFailure.InvalidConfiguration);
            cancellationToken.ThrowIfCancellationRequested();
            if (callback is not null) { inputAbort = callback.Abort.Token; inputAbort.ThrowIfCancellationRequested(); }
            idle = new(TaskCreationOptions.RunContinuationsAsynchronously); _active = idle; _appendingExtensionEntry = true;
        }
        return AppendExtensionEntryCoreAsync(draft, cancellationToken, inputAbort, idle);
    }

    private async Task<SessionExtensionEntryReceipt> AppendExtensionEntryCoreAsync(SessionExtensionEntryDraft draft,
        CancellationToken token, CancellationToken inputAbort, TaskCompletionSource idle)
    {
        var commitHeld = false; var writeAdmitted = false;
        var priorCallback = _configurationCallback.Value; _configurationCallback.Value = idle;
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _closing.Token, inputAbort);
            var work = cancellation.Token;
            await _commits.WaitAsync(work).ConfigureAwait(false); commitHeld = true;
            SessionContextProjection previous; SessionLogStoreSnapshot log;
            lock (_gate) { previous = _context; log = _acknowledgedLog; }
            work.ThrowIfCancellationRequested();
            var id = Identity(_nextEntryId, log.Header.Id, log.Entries);
            work.ThrowIfCancellationRequested();
            var entry = ExtensionRecord(draft, id, previous.LeafId, _clock);
            SessionContextProjection prospective;
            try { prospective = _projector.Project(log.Entries.Add(entry), entry.Id, work); }
            catch (SessionContextProjectionException error) when (error.Failure == SessionContextProjectionFailure.ResourceLimit)
            { throw Error(PersistentAgentSessionFailure.ExtensionEntryLimitExceeded); }
            work.ThrowIfCancellationRequested();
            writeAdmitted = true;
            var acknowledged = await _store.AppendAsync([entry], work).ConfigureAwait(false);
            if (!acknowledged.CheckpointAcknowledged) throw Error(PersistentAgentSessionFailure.InvalidCommit);
            lock (_gate) { _acknowledgedLog = acknowledged.Snapshot; _context = prospective; }
            // Source appendEntry emits entry_appended after the append; a listener failure cannot undo the committed entry.
            writeAdmitted = false; await PublishAppendedAsync(acknowledged.Entries).ConfigureAwait(false);
            return new(acknowledged.Entries.Single(), acknowledged, prospective);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || _closing.IsCancellationRequested || inputAbort.IsCancellationRequested)
        { throw; }
        catch (SessionLogStoreException storage)
        {
            if (storage.Failure == SessionLogStoreFailure.ResourceLimit && !storage.MayHaveWritten)
                throw Error(PersistentAgentSessionFailure.ExtensionEntryLimitExceeded);
            var fault = new PersistentAgentSessionFault(PersistentAgentSessionFailure.AppendFailed, storage.Failure,
                storage.MayHaveWritten, storage.DurableFlushCompleted);
            if (storage.MayHaveWritten || _store.IsPoisoned) lock (_gate) _fault ??= fault;
            throw new PersistentAgentSessionException(fault);
        }
        catch
        {
            if (writeAdmitted) lock (_gate) _fault ??= new(PersistentAgentSessionFailure.InvalidCommit);
            throw;
        }
        finally
        {
            if (commitHeld) _commits.Release();
            lock (_gate) if (ReferenceEquals(_active, idle)) { _active = null; _appendingExtensionEntry = false; }
            idle.TrySetResult(); _configurationCallback.Value = priorCallback;
        }
    }

    /// <summary>Appends a branch-relative edit and refreshes actual Agent history only after the store checkpoint.
    /// The owning host must independently validate the captured attachment generation.</summary>
    public Task<SessionContextEditReceipt> AppendContextEditAsync(string expectedSessionId,
        SessionContextEditDraft draft, CancellationToken cancellationToken = default,
        Func<SessionContextEditPreview, CancellationToken, ValueTask>? preflight = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource idle; CancellationToken inputAbort = default; ContextEditCancellation editCancellation;
        lock (_gate)
        {
            ThrowAvailable(); ThrowInputMutation();
            if (!string.Equals(expectedSessionId, _acknowledgedLog.Header.Id, StringComparison.Ordinal))
                throw Error(PersistentAgentSessionFailure.StaleSession);
            var callback = _inputCallback.Value;
            if (callback is not null && (!ReferenceEquals(callback, _inputSubmission) || callback.Releasing))
                throw new InvalidOperationException("Input callback no longer has an admitted submission.");
            if (_active is not null || _inputSubmission is not null && !ReferenceEquals(callback, _inputSubmission))
                throw new InvalidOperationException("Session is already processing.");
            if (!_agent.Snapshot.PendingInputs.IsEmpty) throw Error(PersistentAgentSessionFailure.InvalidConfiguration);
            cancellationToken.ThrowIfCancellationRequested();
            if (callback is not null) { inputAbort = callback.Abort.Token; inputAbort.ThrowIfCancellationRequested(); }
            editCancellation = new(); _contextEditCancellation = editCancellation;
            idle = new(TaskCreationOptions.RunContinuationsAsynchronously); _active = idle; _editingContext = true;
        }
        return AppendContextEditCoreAsync(draft, cancellationToken, inputAbort, idle, editCancellation, preflight);
    }

    private async Task<SessionContextEditReceipt> AppendContextEditCoreAsync(SessionContextEditDraft draft,
        CancellationToken token, CancellationToken inputAbort, TaskCompletionSource idle, ContextEditCancellation editCancellation,
        Func<SessionContextEditPreview, CancellationToken, ValueTask>? preflight)
    {
        var commitHeld = false; var writeAdmitted = false;
        var priorCallback = _configurationCallback.Value; _configurationCallback.Value = idle;
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _closing.Token, inputAbort, editCancellation.Abort.Token);
            var work = cancellation.Token;
            await _commits.WaitAsync(work).ConfigureAwait(false); commitHeld = true;
            SessionContextProjection previous; SessionLogStoreSnapshot log; AgentConfiguration configuration;
            lock (_gate) { previous = _context; log = _acknowledgedLog; configuration = _configuration; }
            var replacement = SessionContextEditValidator.Normalize(previous, draft, work);
            // Validate native wire shape/bounds before invoking either authoring callback.
            _ = ContextEditRecord(draft.TargetId, replacement, "context-edit-validation", previous.LeafId, () => 0);
            work.ThrowIfCancellationRequested();
            var id = Identity(_nextEntryId, log.Header.Id, log.Entries);
            work.ThrowIfCancellationRequested();
            var entry = ContextEditRecord(draft.TargetId, replacement, id, previous.LeafId, _clock);
            SessionContextProjection prospective;
            try { prospective = _projector.Project(log.Entries.Add(entry), entry.Id, work); }
            catch (SessionContextProjectionException error) when (error.Failure == SessionContextProjectionFailure.ResourceLimit)
            { throw new SessionContextEditException(SessionContextEditFailure.ResourceLimit); }
            await using (var probe = new NativeAgent(configuration, _clock, new NoopSink(), _agentOptions))
                probe.ConfigureAndReplaceMessages(configuration, SessionContextProjector.AgentMessages(prospective));
            if (preflight is not null) await preflight(new(entry, prospective, log), work).ConfigureAwait(false);
            work.ThrowIfCancellationRequested();
            writeAdmitted = true;
            var acknowledged = await _store.AppendAsync([entry], work).ConfigureAwait(false);
            if (!acknowledged.CheckpointAcknowledged) throw Error(PersistentAgentSessionFailure.InvalidCommit);
            lock (_gate)
            {
                _agent.ConfigureAndReplaceMessages(RecoveryConfiguration(configuration), SessionContextProjector.AgentMessages(prospective));
                _acknowledgedLog = acknowledged.Snapshot; _context = prospective;
            }
            return new(acknowledged.Entries.Single(), acknowledged, prospective);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || _closing.IsCancellationRequested || inputAbort.IsCancellationRequested || editCancellation.Abort.IsCancellationRequested)
        { throw; }
        catch (SessionLogStoreException storage)
        {
            if (storage.Failure == SessionLogStoreFailure.ResourceLimit && !storage.MayHaveWritten)
                throw new SessionContextEditException(SessionContextEditFailure.ResourceLimit);
            var fault = new PersistentAgentSessionFault(PersistentAgentSessionFailure.AppendFailed, storage.Failure,
                storage.MayHaveWritten, storage.DurableFlushCompleted);
            if (storage.MayHaveWritten || _store.IsPoisoned) lock (_gate) _fault ??= fault;
            throw new PersistentAgentSessionException(fault);
        }
        catch
        {
            if (writeAdmitted) lock (_gate) _fault ??= new(PersistentAgentSessionFailure.InvalidCommit);
            throw;
        }
        finally
        {
            if (commitHeld) _commits.Release();
            Task cancelIdle;
            lock (_gate)
            {
                if (ReferenceEquals(_contextEditCancellation, editCancellation)) _contextEditCancellation = null;
                cancelIdle = editCancellation.CancelUsers == 0 ? Task.CompletedTask : editCancellation.CancelIdle!.Task;
            }
            await cancelIdle.ConfigureAwait(false);
            editCancellation.Abort.Dispose();
            lock (_gate) if (ReferenceEquals(_active, idle)) { _active = null; _editingContext = false; }
            idle.TrySetResult(); _configurationCallback.Value = priorCallback;
        }
    }

    private SessionEntry ContextEditRecord(string targetId, JsonData replacement, string id, string? parent, Func<long> clock)
    {
        try
        {
            return Record(_codec, "context_edit", id, parent, clock, writer =>
            {
                writer.WriteString("targetId", targetId); writer.WritePropertyName("replacement");
                writer.WriteRawValue(replacement.ToString());
            }, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        }
        catch (SessionEntryCodecException error)
        {
            throw new SessionContextEditException(error.Failure is SessionEntryCodecFailure.CharacterLimit or
                SessionEntryCodecFailure.Utf8ByteLimit or SessionEntryCodecFailure.DepthLimit ?
                SessionContextEditFailure.ResourceLimit : SessionContextEditFailure.UnsupportedReplacement);
        }
    }

    private void ValidateExtensionEntry(string expectedSessionId, SessionExtensionEntryDraft draft)
    {
        if (string.IsNullOrEmpty(expectedSessionId) || expectedSessionId.Length > SessionExtensionEntryLimits.MaximumSessionIdCharacters ||
            draft is null || !ExtensionIdentifier(draft.ExtensionId) || !ExtensionIdentifier(draft.EntryKind) || draft.Data is null)
            throw Error(PersistentAgentSessionFailure.InvalidExtensionEntry);
        if (draft.SchemaVersion != SessionExtensionEntryLimits.CurrentSchemaVersion)
            throw Error(PersistentAgentSessionFailure.UnsupportedExtensionEntryVersion);
        var raw = draft.Data.ToString();
        if (raw.Length > SessionExtensionEntryLimits.MaximumDataCharacters ||
            Encoding.UTF8.GetByteCount(raw) > SessionExtensionEntryLimits.MaximumDataUtf8Bytes)
            throw Error(PersistentAgentSessionFailure.ExtensionEntryLimitExceeded);
        _ = ExtensionRecord(draft, "entry", null, () => 0);
    }

    private SessionEntry ExtensionRecord(SessionExtensionEntryDraft draft, string id, string? parent, Func<long> clock)
    {
        try
        {
            return Record(_codec, "custom", id, parent, clock, writer =>
            {
                writer.WriteString("customType", SessionExtensionEntryLimits.CustomType);
                writer.WriteStartObject("data"); writer.WriteString("extensionId", draft.ExtensionId);
                writer.WriteString("entryKind", draft.EntryKind); writer.WriteNumber("schemaVersion", draft.SchemaVersion);
                writer.WritePropertyName("data"); writer.WriteRawValue(draft.Data.ToString()); writer.WriteEndObject();
            });
        }
        catch (SessionEntryCodecException error)
        {
            throw Error(error.Failure is SessionEntryCodecFailure.CharacterLimit or SessionEntryCodecFailure.Utf8ByteLimit or
                SessionEntryCodecFailure.DepthLimit ? PersistentAgentSessionFailure.ExtensionEntryLimitExceeded :
                PersistentAgentSessionFailure.InvalidExtensionEntry);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        { throw Error(PersistentAgentSessionFailure.InvalidExtensionEntry); }
    }

    private static bool ExtensionIdentifier(string? value) =>
        value is { Length: > 0 } && value.Length <= SessionExtensionEntryLimits.MaximumIdentifierCharacters &&
        value.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-');

    /// <summary>The session host times assistant responses and tool executions like the source AgentSession
    /// (durationMs), with the system clock unless the caller supplied one.</summary>
    private static PersistentAgentSessionOptions Timed(PersistentAgentSessionOptions? options)
    {
        var configured = options ?? new();
        return configured.AgentOptions?.TimeProvider is not null ? configured :
            configured with { AgentOptions = (configured.AgentOptions ?? new()) with { TimeProvider = TimeProvider.System } };
    }

    private static (PersistentAgentSessionOptions, SessionContextProjector, SessionEntryCodec, NativeAgent, Bridge)
        Admit(string path, AgentConfiguration configuration, Func<long> clock, Func<string> nextEntryId,
            PersistentAgentSessionOptions? options)
    {
        ArgumentNullException.ThrowIfNull(clock); ArgumentNullException.ThrowIfNull(nextEntryId);
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path))
            throw Error(PersistentAgentSessionFailure.InvalidConfiguration);
        var configured = Timed(options);
        if (configured.UseLatestLeaf && configured.SelectedLeafId is not null ||
            configured.AgentOptions?.CancellationBehavior == AgentCancellationBehavior.Propagate)
            throw Error(PersistentAgentSessionFailure.InvalidConfiguration);
        var projector = new SessionContextProjector(configured.ContextOptions);
        var codec = new SessionEntryCodec(configured.SessionLogStoreOptions?.ReaderOptions?.CodecOptions);
        var bridge = new Bridge();
        var agent = new NativeAgent(configuration, clock, bridge, configured.AgentOptions);
        return (configured, projector, codec, agent, bridge);
    }

    private static void ValidateRuntimeContext(SessionContextProjection context, AgentConfiguration configuration)
    {
        if (context.ThinkingLevel != configuration.ThinkingLevel) throw Error(PersistentAgentSessionFailure.UnsupportedThinkingLevel);
        // Source getBranchSelection: a virtual model_change holds over the physical responses it routed.
        if (SessionBranchSelection.Select(context, configuration.Model) is { } model &&
            (model.Provider != configuration.Model.Provider || model.ModelId != configuration.Model.Id))
            throw Error(PersistentAgentSessionFailure.ModelMismatch);
    }

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
                if (_configuring || _inputSubmission is not null) throw new InvalidOperationException("Session input admission is already processing.");
                cancellationToken.ThrowIfCancellationRequested();
                _inputSubmission = reservation;
                // Like source prompt(), idle handlers see no streaming behavior.
                if (_active is null && options?.QueueOnly != true) input = input with { StreamingBehavior = null };
            }
        }
        catch { reservation.Abort.Dispose(); throw; }
        return SubmitInputCoreAsync(input, admission, options, cancellationToken, reservation);
    }

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
                    throw new InvalidOperationException("Active input requires steering or follow-up delivery.");
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
                            throw new InvalidOperationException("Active input requires steering or follow-up delivery.");
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
                            throw new InvalidOperationException("Active input requires steering or follow-up delivery.");
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
                    _operationPhase = SessionOperationPhase.Provider; _active = generation;
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
        ImmutableArray<TranscriptEntry> inputs = default, bool injectNextTurnCustom = false)
    {
        TaskCompletionSource idle;
        lock (_gate)
        {
            ThrowAvailable();
            ThrowUserBashMutationLocked();
            if (_active is not null || _inputSubmission is not null) throw new InvalidOperationException("Session is already processing.");
            token.ThrowIfCancellationRequested();
            if (injectNextTurnCustom) inputs = InjectNextTurnCustomLocked(inputs);
            _operationGeneration = checked(_operationGeneration + 1);
            // Source _runAgentPrompt: the run records the loadout; restored tools that did not register by now are dropped.
            _pendingToolNames = [];
            _runCancellation = new(); _operationPhase = SessionOperationPhase.Provider;
            idle = new(TaskCreationOptions.RunContinuationsAsynchronously); _active = idle;
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
        var selected = LoadoutRegistry().Resolve(_configuration.Model, messages, _configuration.ThinkingLevel, cancellationToken: token, prepareLoadout: false);
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
    { lock (_gate) { ThrowAvailable(); return _agent.Subscribe(sink); } }
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
        Task idle; Task input; Task diagnostics; Task settings; Task[] bash;
        lock (_gate) { idle = _active?.Task ?? Task.CompletedTask; input = _inputSubmission?.Idle.Task ?? Task.CompletedTask; diagnostics = LoadoutDiagnosticIdleLocked(); settings = RetrySettingsIdleLocked(); bash = CaptureUserBashCompletionsLocked(); }
        var settled = Task.WhenAll(new[] { agentIdle, idle, input, diagnostics, settings }.Concat(bash));
        return cancellationToken.CanBeCanceled ? settled.WaitAsync(cancellationToken) : settled;
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
                        writer.WriteRawValue(message.WireBody.Value.GetRawText());
                    }
                });
            // Reject unsupported selected influences/bounds before writing, without rewriting history.
            var nextContext = _projector.Project(log.Entries.Add(entry), entry.Id);
            ValidateRuntimeContext(nextContext, _configuration);
            if (_registry is not null) ValidateLoadout(nextContext.LlmMessages);
            return (entry, nextContext);
            }
            var acknowledged = await _store.AppendAsync([entry], CancellationToken.None).ConfigureAwait(false);
            if (!acknowledged.CheckpointAcknowledged) throw Error(PersistentAgentSessionFailure.InvalidCommit);
            lock (_gate)
            {
                _acknowledgedLog = acknowledged.Snapshot; _context = nextContext;
                if (role == "assistant") _lastAcknowledgedAssistantId = entry.Id;
                if (replacement is not null)
                {
                    // The running loop keeps the original; it continues from the persisted context once idle.
                    if (_replacedMessages.Count >= 1024) _replacedMessages.Clear();
                    _replacedMessages[original.WireBody.Value.GetRawText()] = replacement.WireBody; _agentHoldsReplacedMessages = true;
                }
            }
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
        if (_replacing) throw new InvalidOperationException("Session replacement is in progress.");
    }
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
        string id;
        try { id = nextEntryId(); } catch (Exception) { throw Error(PersistentAgentSessionFailure.InvalidCommit); }
        if (string.IsNullOrWhiteSpace(id) || id == headerId || entries.Any(entry => entry.Id == id))
            throw Error(PersistentAgentSessionFailure.InvalidCommit);
        return id;
    }
    private static SessionEntry Record(SessionEntryCodec codec, string type, string id, string? parentId,
        Func<long> clock, Action<Utf8JsonWriter> fields, string timestampFormat = "O")
    {
        string timestamp;
        try { timestamp = DateTimeOffset.FromUnixTimeMilliseconds(clock()).ToString(timestampFormat, CultureInfo.InvariantCulture); }
        catch (Exception) { throw Error(PersistentAgentSessionFailure.InvalidCommit); }
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject(); writer.WriteString("type", type); writer.WriteString("id", id);
            writer.WriteString("parentId", parentId); writer.WriteString("timestamp", timestamp);
            fields(writer); writer.WriteEndObject();
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
