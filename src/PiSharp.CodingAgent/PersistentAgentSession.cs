// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/coding-agent/src/core/agent-session.ts (durationMs, agent_settled.aborted).
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
    /// <summary>Run an existing session in this directory without changing its stored header.</summary>
    public string? WorkingDirectoryOverride { get; init; }
    public PiSharp.CodingAgent.ToolSelection.AllowedToolSelection? LifetimeToolSelection { get; init; }
    /// <summary>agent-session.ts prompt: run for an idle prompt after input handlers and expansion, before anything is persisted or a
    /// run starts, with the session's current model. A <see cref="SessionPromptRejectedException"/> it throws rejects the prompt.</summary>
    public Func<ModelDescriptor, CancellationToken, ValueTask>? PromptPreflight { get; init; }
    /// <summary>The placeholder a session without a selected model runs with (sdk.ts createAgentSession with no model): a new session
    /// records no <c>model_change</c> for it.</summary>
    public ModelDescriptor? UnselectedModel { get; init; }
    /// <summary>main.ts buildSessionOptions <c>options.model</c> (--model): the model every session opened or created through these
    /// options runs on, instead of the one its branch records.</summary>
    public ModelDescriptor? SelectedModel { get; init; }
    /// <summary>sdk.ts createAgentSession for a session without messages: its thinking level for the model it runs on (given the
    /// model's supported levels; null keeps the restored level). When set, opening such a session records the model and that level.</summary>
    public Func<ModelDescriptor, ImmutableArray<string>, string?>? NewSessionThinkingLevel { get; init; }
    /// <summary>sdk.ts findInitialModel (with main.ts's scoped pick) for a session without messages, when no <see cref="SelectedModel"/>
    /// is set: the model it records and runs on (null, or a model without a binding, keeps the restored or current model).</summary>
    public Func<CancellationToken, ValueTask<ModelDescriptor?>>? NewSessionModel { get; init; }
}
/// <summary>A prompt the session refused before admitting it (agent-session.ts prompt validation); nothing was persisted.</summary>
public sealed class SessionPromptRejectedException(string message) : Exception(message);
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
/// <summary>The session refused an operation because an owner operation holds its replacement reservation (a resource close, reload
/// or switch); the owner can tell it from other refusals and join that operation (<see cref="ReplaceableAgentSession.AccessSessionAsync"/>).</summary>
internal sealed class SessionReplacementInProgressException() : InvalidOperationException("Session replacement is in progress.");
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
        /// <summary>The input became a run (<c>_active</c>): input submitted while it starts is busy input, not a second admission.</summary>
        public bool RunStarted;
    }

    private sealed class Bridge : IAgentEventSink
    {
        public PersistentAgentSession? Owner;
        public ValueTask<TranscriptEntry> NormalizeInput(ModelDescriptor model, TranscriptEntry message, CancellationToken token) =>
            Owner?.InputNormalizer is { } normalize ? normalize(model, message, token) : ValueTask.FromResult(message);
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
    public string WorkingDirectory { get; private set; }

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
    public bool IsExecutingInputCallback
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
            // A kept thinking level (tree navigation) is the session's level, as agent.state.thinkingLevel is upstream.
            lock (_gate) return new(_agent.Snapshot, _acknowledgedLog, _toleratedThinking is { } recorded && _context.ThinkingLevel == recorded
                ? _context with { ThinkingLevel = _configuration.ThinkingLevel } : _context, _fault, _disposed, _configuring)
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
            // sdk.ts: a new session records its initial model (when there is one) and thinking level.
            var model = configuration.Model == configured.UnselectedModel ? null : Record(codec, "model_change", Identity(nextEntryId, header.Id, []), null, clock, writer =>
            {
                writer.WriteString("provider", configuration.Model.Provider);
                writer.WriteString("modelId", configuration.Model.Id);
            });
            var thinking = Record(codec, "thinking_level_change", Identity(nextEntryId, header.Id, model is null ? [] : [model]), model?.Id,
                clock, writer => writer.WriteString("thinkingLevel", configuration.ThinkingLevel));
            ImmutableArray<SessionEntry> initial = model is null ? [thinking] : [model, thinking];
            var context = projector.Project(initial, thinking.Id, cancellationToken);
            store = await SessionLogStore.CreateNewAsync(path, header, configured.SessionLogStoreOptions, cancellationToken).ConfigureAwait(false);
            // Once the file/header creation is admitted, complete the initial metadata checkpoint.
            await store.AppendAsync(initial, CancellationToken.None).ConfigureAwait(false);
            return new(path, store, agent, bridge, projector, codec, context, configuration, clock, nextEntryId,
                configured.AgentOptions, configured.SessionLogStoreOptions?.ReaderOptions) { PromptPreflight = configured.PromptPreflight };
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
                configured.AgentOptions, configured.SessionLogStoreOptions?.ReaderOptions)
            { PromptPreflight = configured.PromptPreflight,
                WorkingDirectory = configured.WorkingDirectoryOverride ?? store.Snapshot.Header.WireBody.Value.GetProperty("cwd").GetString()! };
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
        // sdk.ts createAgentSession: a new session's first thinking_level_change already holds the resolved level (the CLI level,
        // else the per-model or default setting, clamped), not the model default followed by a second change.
        string? thinkingLevel = null;
        if (options?.NewSessionThinkingLevel is { } initialThinking)
            try { thinkingLevel = initialThinking(initialModel, registry.GetSupportedThinkingLevels(initialModel)); }
            catch (SessionRuntimeRegistryException error) when (error.Failure == SessionRuntimeRegistryFailure.UnknownModel) { }
        thinkingLevel ??= registry.GetDefaultThinkingLevel(initialModel);
        // sdk.ts createAgentSession: the level is clamped to the model's capabilities (clampThinkingLevel), never refused.
        thinkingLevel = ThinkingLevels.Clamp(registry.GetSupportedThinkingLevels(initialModel), thinkingLevel);
        var selection = await registry.PrepareAndDrainAsync(() => registry.Resolve(initialModel, [], thinkingLevel, cancellationToken), cancellationToken).ConfigureAwait(false);
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
            var acquired = await runtimeForWorkingDirectory(configured.WorkingDirectoryOverride ?? store.Snapshot.Header.WireBody.Value.GetProperty("cwd").GetString()!, cancellationToken).ConfigureAwait(false)
                ?? throw Error(PersistentAgentSessionFailure.InvalidConfiguration);
            acquired.Claim(); runtime = acquired;
            var registry = runtime.Registry.RetainToolSelection(configured.LifetimeToolSelection);
            cancellationToken.ThrowIfCancellationRequested();
            // Without initial names, or with the default tools (Pi 1.5.0 usesDefaultTools), the branch's loadout is restored by name
            // with the current bindings; explicit initial names replace it.
            var loadout = await registry.PrepareAndDrainAsync(() => registry.ResolveRestored(context, fallbackModel, cancellationToken,
                registry.InitialActiveToolNames, configured.SelectedModel), cancellationToken).ConfigureAwait(false);
            // sdk.ts createAgentSession: a session without messages (an empty or header-only file, a new session) records its model
            // and thinking level at once (appendModelChange, appendThinkingLevelChange); a lazy store keeps them until it is written.
            if (configured.NewSessionThinkingLevel is { } initialThinking && !context.Messages.Any(message => message.Role != "system"))
            {
                var model = loadout.Selection.Configuration.Model;
                // main.ts buildSessionOptions + sdk.ts findInitialModel: without --model, a session without messages takes the scoped
                // models' pick, else the saved default, else the first available model, not the model in use.
                if (configured.SelectedModel is null && configured.NewSessionModel is { } choose && await choose(cancellationToken).ConfigureAwait(false) is { } chosen)
                {
                    try { _ = registry.GetSupportedThinkingLevels(chosen); model = chosen; }
                    catch (SessionRuntimeRegistryException error) when (error.Failure == SessionRuntimeRegistryFailure.UnknownModel) { /* Not bound: keep. */ }
                }
                var supported = registry.GetSupportedThinkingLevels(model);
                // sdk.ts: the recorded thinking_level_change holds the level clamped to the model (clampThinkingLevel).
                var level = ThinkingLevels.Clamp(supported, initialThinking(model, supported) ?? (model == loadout.Selection.Configuration.Model
                    ? loadout.Selection.Configuration.ThinkingLevel : registry.GetDefaultThinkingLevel(model)));
                var header = store.Snapshot.Header; var existing = store.Snapshot.Entries;
                var modelEntry = Record(codec, "model_change", Identity(nextEntryId, header.Id, existing), context.LeafId, clock, writer =>
                {
                    writer.WriteString("provider", model.Provider);
                    writer.WriteString("modelId", model.Id);
                });
                var thinkingEntry = Record(codec, "thinking_level_change", Identity(nextEntryId, header.Id, existing.Add(modelEntry)), modelEntry.Id,
                    clock, writer => writer.WriteString("thinkingLevel", level));
                var appended = await store.AppendAsync([modelEntry, thinkingEntry], cancellationToken).ConfigureAwait(false);
                if (!appended.CheckpointAcknowledged) throw Error(PersistentAgentSessionFailure.InvalidCommit);
                context = projector.Project(appended.Snapshot.Entries, thinkingEntry.Id, cancellationToken);
                loadout = await registry.PrepareAndDrainAsync(() => registry.ResolveRestored(context, model, cancellationToken,
                    registry.InitialActiveToolNames, model), cancellationToken).ConfigureAwait(false);
            }
            var selection = loadout.Selection;
            var bridge = new Bridge();
            agent = new(selection.Configuration, clock, bridge, WithInputNormalizer(configured.AgentOptions, bridge));
            agent.ReplaceMessages(SessionContextProjector.AgentMessages(context));
            cancellationToken.ThrowIfCancellationRequested();
            opened = new(path, store, agent, bridge, projector, codec, context, selection.Configuration, clock, nextEntryId,
                configured.AgentOptions, configured.SessionLogStoreOptions?.ReaderOptions)
            { _registry = registry, _runtimeLease = runtime, PromptPreflight = configured.PromptPreflight,
                WorkingDirectory = configured.WorkingDirectoryOverride ?? store.Snapshot.Header.WireBody.Value.GetProperty("cwd").GetString()!,
                _toleratedSelection = Divergent(context, selection.Configuration.Model),
                // sdk.ts createAgentSession: a recorded level the model cannot run opens clamped (agent.state.thinkingLevel) and is not
                // rewritten; the branch's level is tolerated, as after tree navigation, until a change names the session's.
                _toleratedThinking = context.ThinkingLevel != selection.Configuration.ThinkingLevel ? context.ThinkingLevel : null };
            var restored = selection.Configuration.Tools.Select(tool => tool.Name).ToImmutableArray();
            // Source constructor: the initial names (_buildRuntime) or the transcript's loadout (_restoreToolsFromTranscript) are
            // applied in memory and the file is not written; the next request records a loadout that differs from the recorded one.
            // Restored tools that are not registered yet, such as MCP tools whose server is still connecting, stay pending.
            opened.RestoreUnrecordedTools(loadout.RequiresRecord || !restored.SequenceEqual(opened.RecordedActiveToolNames(context, cancellationToken), StringComparer.Ordinal),
                loadout.Pending, loadout.Restored);
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
            var current = _configuration.Tools.Select(tool => tool.Name).ToImmutableArray();
            var selection = registry.Resolve(WithUnrecordedLoadout(_context, current, default), _configuration.Model, tolerated: _toleratedSelection,
                thinkingLevel: KeptThinking(_configuration), activeOrder: current);
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
            // agent-session.ts setModel/setThinkingLevel/setActiveToolsByName apply at once from an extension command's handler (the
            // admitted input's own callback, outside any agent run).
            if (_active is not null || _inputSubmission is not null && !(_inputSubmission is { Releasing: false } input && ReferenceEquals(_inputCallback.Value, input)))
                throw new InvalidOperationException("Session is already processing.");
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
            SessionContextProjection context; SessionLogStoreSnapshot log; object? priorPromptRevision; string effectiveThinking;
            lock (_gate) { context = _context; log = _acknowledgedLog; priorPromptRevision = _acknowledgedPromptRevision; effectiveThinking = _configuration.ThinkingLevel; }
            var entries = ImmutableArray.CreateBuilder<SessionEntry>();
            var parent = context.LeafId;
            if (update.Model is { } model)
                Add("model_change", writer =>
                {
                    writer.WriteString("provider", model.Provider);
                    writer.WriteString("modelId", model.Id);
                });
            // Source setThinkingLevel: the level is clamped to the (new) model's capabilities (_clampThinkingLevel), and setModel
            // applies the current level clamped to the new model; only a change of the session's level (which tree navigation may
            // have kept over the branch's) is recorded.
            var requestedThinking = update.ThinkingLevel ?? (update.Model is not null ? effectiveThinking : null);
            var clampedThinking = requestedThinking is null ? null
                : ThinkingLevels.Clamp(_registry!.GetSupportedThinkingLevels(update.Model ?? _configuration.Model), requestedThinking);
            if (clampedThinking is { } level && level != effectiveThinking)
                Add("thinking_level_change", writer => writer.WriteString("thinkingLevel", level));
            // Source setActiveToolsByName: a selection applies in memory, and the next request records its difference from the declared
            // tools (declareToolChanges) with the prompt sections it changes. setModel/setThinkingLevel write only their own entries.
            // Pi 1.5.0 _applyToolLoadout: tools that stay active keep their order and new ones are appended.
            var currentNames = _configuration.Tools.Select(tool => tool.Name).ToImmutableArray();
            var selectedTools = update.ActiveToolNames is { } activeNames
                ? SessionRuntimeRegistry.KeepOrder(currentNames, _registry!.NormalizeActiveTools(activeNames, work)) : (ImmutableArray<string>?)null;
            if (update.SystemMessage is { } system)
            {
                if (system.Role != "system" || system.WireBody is null)
                    throw Error(PersistentAgentSessionFailure.InvalidConfiguration);
                Add("message", writer =>
                {
                    writer.WritePropertyName("message"); writer.WriteRawValue(system.WireBody.Value.GetRawText(), skipInputValidation: true);
                });
            }
            var prospective = _projector.Project(log.Entries.AddRange(entries), parent, work);
            if (entries.Count == 0 && (selectedTools is null || selectedTools.Value.SequenceEqual(currentNames, StringComparer.Ordinal)))
            {
                work.ThrowIfCancellationRequested();
                if (update.ActiveToolNames is not null) lock (_gate)
                { work.ThrowIfCancellationRequested(); if (_pendingActivation is not null) PrepareActivationRestoration(_configuration)(); }
                return Snapshot with { IsConfiguring = false };
            }
            // The loadout the next request records precedes this update's entries in every resolution.
            var resolved = prospective with { LlmMessages = selectedTools is { } selectedNames
                ? WithLoadoutRecord(_registry!, prospective.LlmMessages, selectedNames, work, context.LlmMessages.Length)
                : WithUnrecordedLoadout(prospective.LlmMessages, currentNames, work, context.LlmMessages.Length) };
            var unrecordedAfter = selectedTools is { } loadout ? _registry!.CreateToolChangeMessage(context.LlmMessages, loadout, 0, work) is not null : (bool?)null;
            var targetThinking = clampedThinking ?? effectiveThinking;
            var selection = await PrepareAndDrainLoadoutAsync(() => _registry!.Resolve(resolved, update.Model ?? _configuration.Model, work,
                thinkingLevel: resolved.ThinkingLevel != targetThinking ? targetThinking : null, activeOrder: selectedTools ?? currentNames), work).ConfigureAwait(false);
            if (selectedTools is { } expected && !selection.Configuration.Tools.Select(tool => tool.Name).SequenceEqual(expected, StringComparer.Ordinal))
                throw Error(PersistentAgentSessionFailure.InvalidConfiguration);
            // A stored selection only carries provider/modelId; preserve exact API matching for an explicitly requested model.
            if (update.Model is { } requested && selection.Configuration.Model != requested)
                throw Error(PersistentAgentSessionFailure.InvalidConfiguration);
            await using (var probe = new NativeAgent(selection.Configuration, _clock, new NoopSink(), _agentOptions))
                probe.ConfigureAndReplaceMessages(selection.Configuration, SessionContextProjector.AgentMessages(prospective));
            work.ThrowIfCancellationRequested();
            Action restoreActivation;
            lock (_gate) restoreActivation = PrepareActivationRestoration(selection.Configuration);
            work.ThrowIfCancellationRequested();
            lock (_gate)
                if (!ReferenceEquals(_context, context) || !ReferenceEquals(_acknowledgedLog, log) || !ReferenceEquals(_active, idle) ||
                    !ReferenceEquals(_acknowledgedPromptRevision, priorPromptRevision))
                    throw new InvalidOperationException("Prompt configuration reservation changed.");
            writeAdmitted = entries.Count != 0;
            var acknowledged = entries.Count == 0 ? null : await _store.AppendAsync(entries.ToImmutable(), work).ConfigureAwait(false);
            if (acknowledged is { CheckpointAcknowledged: false }) throw Error(PersistentAgentSessionFailure.InvalidCommit);
            ModelDescriptor previousModel; long generation;
            lock (_gate)
            {
                previousModel = _configuration.Model; generation = _operationGeneration;
                _agent.ConfigureAndReplaceMessages(RecoveryConfiguration(selection.Configuration), SessionContextProjector.AgentMessages(prospective));
                if (update.ActiveToolNames is not null)
                    SelectPendingToolsLocked(_configuration.Tools.Select(tool => tool.Name).ToImmutableArray(),
                        selection.Configuration.Tools.Select(tool => tool.Name).ToImmutableArray());
                _configuration = selection.Configuration;
                if (acknowledged is not null) { _acknowledgedLog = acknowledged.Snapshot; _context = prospective; }
                _toleratedThinking = prospective.ThinkingLevel != selection.Configuration.ThinkingLevel ? prospective.ThinkingLevel : null;
                if (unrecordedAfter is { } unrecordedLoadout) _unrecordedLoadout = unrecordedLoadout;
                restoreActivation();
            }
            // Source setModel/setThinkingLevel: thinking_level_changed (and thinking_level_select) when the level changed,
            // then model_select when the model changed. Listener failures cannot undo the committed configuration.
            writeAdmitted = false;
            if (entries.Any(entry => entry.Type == "thinking_level_change"))
                await EmitOperationAsync(new SessionThinkingLevelChanged(generation, selection.Configuration.ThinkingLevel, effectiveThinking)).ConfigureAwait(false);
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
    internal ReplacementReservation ReserveReplacement() => ReserveReplacementCore(shutdown: false)!;
    // Shutdown discards queued input and a fault is the faulting operation's: null retires owned
    // resources without catalog publication authority instead of reporting a second cleanup failure.
    internal ReplacementReservation? ReserveShutdownRetirement() => ReserveReplacementCore(shutdown: true);
    private ReplacementReservation? ReserveReplacementCore(bool shutdown)
    {
        lock (_gate)
        {
            var agent = _agent.Snapshot;
            if (shutdown && (_fault is not null || agent.SteeringCount != 0 || agent.FollowUpCount != 0 || !agent.PendingInputs.IsEmpty)) return null;
            ThrowAvailable();
            ThrowUserBashMutationLocked();
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
                writer.WriteRawValue(replacement.ToString(), skipInputValidation: true);
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
                writer.WritePropertyName("data"); writer.WriteRawValue(draft.Data.ToString(), skipInputValidation: true); writer.WriteEndObject();
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
        var agent = new NativeAgent(configuration, clock, bridge, WithInputNormalizer(configured.AgentOptions, bridge));
        return (configured, projector, codec, agent, bridge);
    }

    /// <param name="tolerated">The selection the branch recorded when the session was opened on another model (sdk.ts: a fallback
    /// model, or --model); it stands until a response or a model_change on the branch names the session's model.</param>
    /// <param name="toleratedThinking">The thinking level the branch records when tree navigation kept the session's own level.</param>
    private static void ValidateRuntimeContext(SessionContextProjection context, AgentConfiguration configuration, SessionContextModel? tolerated = null,
        string? toleratedThinking = null)
    {
        if (context.ThinkingLevel != configuration.ThinkingLevel && context.ThinkingLevel != toleratedThinking)
            throw Error(PersistentAgentSessionFailure.UnsupportedThinkingLevel);
        // Source getBranchSelection: a virtual model_change holds over the physical responses it routed.
        if (SessionBranchSelection.Select(context, configuration.Model) is { } model &&
            (model.Provider != configuration.Model.Provider || model.ModelId != configuration.Model.Id) && model != tolerated)
            throw Error(PersistentAgentSessionFailure.ModelMismatch);
    }

    /// <summary>The branch selection a session opened on another model tolerates (see <see cref="ValidateRuntimeContext"/>).</summary>
    private SessionContextModel? _toleratedSelection;
    private string? _toleratedThinking;
    /// <summary>The thinking level a re-resolution keeps while the branch records another (see <see cref="_toleratedThinking"/>).</summary>
    private string? KeptThinking(AgentConfiguration configuration) => _toleratedThinking is null ? null : configuration.ThinkingLevel;
    private static SessionContextModel? Divergent(SessionContextProjection context, ModelDescriptor model) =>
        SessionBranchSelection.Select(context, model) is { } recorded && (recorded.Provider != model.Provider || recorded.ModelId != model.Id) ? recorded : null;

}
