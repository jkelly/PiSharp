using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Resources;
using PiSharp.Rpc.Protocol;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Discovery;
using PiSharp.Extensions.Runtime.Loading;
using PiSharp.Extensions;
using PiSharp.Extensions.Events;
using PiSharp.Sessions.Context;
using PiSharp.Extensions.Facade.Context;
namespace PiSharp.Cli.Extensions;

/// <summary>Owns one approved published generation and its actual transactional registry.</summary>
internal sealed partial class NativeExtensionActivation : IAsyncDisposable, IPromptTemplateCommandAdmission, IRpcExtensionCommandCatalog
{
    private readonly object _gate = new();
    private readonly ExtensionRegistry _registry;
    private readonly PluginAssemblyLoader? _loader;
    private readonly CancellationTokenSource _closing = new();
    private readonly ImmutableDictionary<string, NativeToolObjectSchema> _schemas;
    private readonly NativeSessionSnapshotProvider _sessionViews;
    private readonly NativeExtensionContextFacadeHost _facadeHost;
    private readonly NativeExtensionRegistrationBridge? _registrationBridge;
    private readonly NativeExtensionConfiguration _configuration;
    private readonly Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? _reportInputDiagnostic;
    private readonly NativeLoadoutDiagnostics? _loadoutDiagnostics;
    private Task? _disposal;
    internal ImmutableDictionary<string, string> Targets { get; }
    internal ExtensionAgentBinding Binding { get; private set; } = null!;
    /// <summary>IMPL-I: resolveToolRenderers for the interactive mode's tool rows (resolvers in load order, then the tool's own).</summary>
    internal PiSharp.Extensions.ExtensionToolRenderers? ResolveToolRenderers(string toolName) =>
        Binding is null ? null : _registry.ResolveToolRenderers(Binding.Snapshot, toolName);
    internal IPromptInputAdmission InputAdmission { get; private set; } = null!;
    internal IPromptInputAdmission RawInputHandlers { get; private set; } = null!;

    private NativeExtensionActivation(NativeExtensionConfiguration configuration, ExtensionRegistry registry,
        PluginAssemblyLoader? loader, ExtensionRegistrySnapshot snapshot, ImmutableDictionary<string, NativeToolObjectSchema> schemas,
        NativeSessionSnapshotProvider sessionViews,
        Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? reportInputDiagnostic,
        NativeExtensionContextFacadeHost facadeHost, NativeExtensionRegistrationBridge? registrationBridge = null)
    {
        _configuration = configuration; _registry = registry; _loader = loader; _schemas = schemas; _sessionViews = sessionViews;
        _facadeHost = facadeHost;
        _registrationBridge = registrationBridge;
        _reportInputDiagnostic = reportInputDiagnostic;
        if (reportInputDiagnostic is not null) _loadoutDiagnostics = new(reportInputDiagnostic, _closing.Token);
        Targets = snapshot.Tools.Where(tool => configuration.EnabledTools.Contains(tool.Name, StringComparer.Ordinal) &&
                !configuration.DeniedTools.Contains(tool.Name, StringComparer.Ordinal))
            .ToImmutableDictionary(tool => tool.Name, tool => tool.OwnerId + "/" + tool.OwnerGeneration.ToString(CultureInfo.InvariantCulture) +
                "/" + tool.RegistrationId, StringComparer.Ordinal);
    }

    internal static async Task<NativeExtensionActivation> LoadAsync(NativeExtensionPreflight preflight, CancellationToken token,
        IExtensionUiProvider? uiProvider = null,
        Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? reportInputDiagnostic = null,
        NativeExtensionContextFacadeHost? configuredFacadeHost = null,
        IExtensionContextReadHost? configuredFacadeCapabilities = null,
        IExtensionRegistrationActionHost? configuredRegistrationActions = null,
        PromptTemplateCatalogSnapshot? configuredPromptTemplates = null,
        NativeExtensionInitializerInstallation? configuredInitializerInstallation = null,
        Func<ExtensionRegistry, PiSharp.Cli.Extensions.Execution.NativeExtensionExecInstallation>? configuredExecInstallation = null,
        PiSharp.Cli.Mcp.McpRegisteredServers? mcpServers = null)
    {
        if (configuredExecInstallation is not null && configuredExecInstallation.GetInvocationList().Length != 1)
            throw new ArgumentException("One explicitly supplied execution installation factory required.");
        var sessionViews = new NativeSessionSnapshotProvider();
        var facadeHost = configuredFacadeHost ?? new NativeExtensionContextFacadeHost();
        NativeExtensionActivation? admittedActivation = null;
        PiSharp.Cli.Extensions.Execution.NativeExtensionExecInstallation? execInstallation = null;
        if (configuredFacadeCapabilities is not null && configuredRegistrationActions is not null)
            throw new ArgumentException("Supply one explicit combined facade capability, or the attached host and registration action capability.");
        var registrationActions = configuredRegistrationActions ?? new NativeExistingSessionRegistrationActions(facadeHost,
            () => admittedActivation?.InputAdmission ?? throw new NotSupportedException("The native input pipeline has not been bound."),
            configuredPromptTemplates is null ? null : () =>
            {
                var activation = admittedActivation ?? throw new NotSupportedException("The native input pipeline has not been bound.");
                return new PromptTemplateInputAdmission(configuredPromptTemplates, PromptTemplateInputOperation.Prompt,
                    activation.RawInputHandlers, activation, expandTemplates: true);
            });
        if (configuredExecInstallation is not null && configuredFacadeCapabilities is not null)
            throw new ArgumentException("Execution callback bridge requires the loader-owned combined facade; custom wrappers need explicit aggregate composition.");
        var executionContext = configuredExecInstallation is null ? null :
            new PiSharp.Cli.Extensions.Execution.NativeExtensionExecContextHost(facadeHost, () => execInstallation);
        IExtensionContextReadHost facadeCapabilities = configuredFacadeCapabilities ??
            new NativeExtensionRegistrationFacadeHost(facadeHost, registrationActions, executionContext);
        var uiPrompts = uiProvider is null ? null : new NativeUiPromptEvents(uiProvider);
        var registry = new ExtensionRegistry(new() { MaximumOwners = 1, MaximumRegistrations = 64, MaximumRegistrationsPerOwner = 64 },
            uiPrompts ?? (IExtensionUiProvider)new UnavailableExtensionUiProvider(), sessionViews, facadeCapabilities);
        // pi.registerMcpServer(): registrations made while the extension loads are read when the session starts.
        if (mcpServers is not null) { registry.McpServerHost = mcpServers; mcpServers.OwnerPath = _ => preflight.Configuration.Package; }
        var loader = new PluginAssemblyLoader(new() { SnapshotParentDirectory = preflight.Configuration.SnapshotRoot,
            MaximumOwnedPackages = 1 });
        NativeExtensionRegistrationBridge? registrationBridge = null;
        PiSharp.Cli.Mcp.McpExtensionRegistrationBridge? mcpBridge = null;
        Task<LoadedExtension>? loadOriginal = null;
        try
        {
            configuredInitializerInstallation?.Validate();
            if (configuredInitializerInstallation is not null)
            {
                var candidateBridge = configuredInitializerInstallation.CreateRegistrationBridge(registry) ?? throw new InvalidOperationException("No admitted registration bridge returned.");
                if (!candidateBridge.IsBoundTo(registry)) throw new InvalidOperationException("Registration factory returned a foreign registry bridge.");
                registrationBridge = candidateBridge;
                var candidateMcp = configuredInitializerInstallation.CreateMcpBridge?.Invoke(registry);
                if (configuredInitializerInstallation.CreateMcpBridge is not null && (candidateMcp is null || !candidateMcp.IsBoundTo(registry)))
                    throw new InvalidOperationException("MCP factory returned no matching native registry bridge.");
                mcpBridge = candidateMcp;
            }
            if (configuredExecInstallation is not null)
            {
                token.ThrowIfCancellationRequested();
                try { execInstallation = configuredExecInstallation(registry) ?? throw new InvalidOperationException("No execution installation returned."); }
                catch (OperationCanceledException callbackFailure)
                { throw new PiSharp.Cli.Extensions.Execution.NativeExtensionExecFault("synchronous installation factory", null, callbackFailure); }
                if (!execInstallation.IsBoundTo(registry)) throw new InvalidOperationException("Execution installation returned a foreign registry.");
            }
            loadOriginal = loader.LoadAsync(preflight.Metadata, preflight.Configuration.Package, ExtensionSourceScope.Explicit,
                NativeExtensionConfiguration.Scope, preflight.Inspection, preflight.Execution, registry, token,
                configuredInitializerInstallation is null && execInstallation is null ? null : (owner, extension, current) =>
                {
                    var supplied = execInstallation?.Decorate(registry, extension) ?? extension;
                    return configuredInitializerInstallation is null ? registry.ActivateAsync(owner, supplied, current) : ActivateConfiguredOwnerAsync(
                        configuredInitializerInstallation, registrationBridge!, mcpBridge, preflight.Configuration.Package, owner, supplied, current);
                },
                registrationBridge is null ? null : scope => RetireConfiguredOwnerAsync(registrationBridge, mcpBridge, scope));
            await loadOriginal.ConfigureAwait(false);
            var snapshot = registry.CaptureSnapshot();
            if (snapshot.Registrations.Any(entry => entry.Kind is not ("Tool" or "Command" or "InputHandler" or "ToolCallHandler" or "ToolResultHandler" or "Observation" or "SessionSwitchHandler" or "SessionCreationHandler" or "ContextHandler" or "ContextWithSystemHandler" or "BeforeAgentStartHandler" or "SessionBeforeTreeHandler" or "ToolRenderer" or "UserBashHandler" or "EventHandler")) ||
                preflight.Configuration.EnabledTools.Any(name => snapshot.Tools.Count(tool => tool.Name == name) != 1) ||
                preflight.Configuration.EnabledCommands.Any(name => snapshot.Commands.Count(command => command.Name == name) != 1))
                throw new NativeExtensionException(NativeExtensionFailure.InvalidConfiguration);
            var schemas = snapshot.Tools.ToImmutableDictionary(tool => tool.Name, tool => NativeToolObjectSchema.Read(tool.Parameters), StringComparer.Ordinal);
            token.ThrowIfCancellationRequested();
            admittedActivation = new(preflight.Configuration, registry, loader, snapshot, schemas, sessionViews, reportInputDiagnostic, facadeHost, registrationBridge)
                { UiPrompts = uiPrompts, McpServers = mcpServers };
            return admittedActivation;
        }
        catch (Exception original)
        {
            var failures = new List<Exception>();
            if (loadOriginal?.Exception is { } loadFault) failures.Add(loadFault);
            await CollectNativeCleanupAsync(loader.DisposeAsync, failures).ConfigureAwait(false);
            await CollectNativeCleanupAsync(registry.DisposeAsync, failures).ConfigureAwait(false);
            try { registrationBridge?.Dispose(); } catch (Exception error) { failures.Add(error); }
            if (failures.Count > 0) throw new NativeExtensionException(NativeExtensionFailure.CleanupFailed, new AggregateException(failures.Prepend(original)));
            if (original is OperationCanceledException && token.IsCancellationRequested) throw;
            if (original is NativeExtensionException) throw;
            throw new NativeExtensionException(NativeExtensionFailure.ActivationFailed, original);
        }
    }

    internal void Bind(IToolActionPolicy policy, ToolInvokerOptions limits)
    {
        if (Binding is not null) throw new InvalidOperationException("Native generation is already bound.");
        _boundPolicy = policy; _boundLimits = limits;
        Binding = new(_registry, policy, (tool, arguments, token) =>
        {
            token.ThrowIfCancellationRequested(); _closing.Token.ThrowIfCancellationRequested();
            // Pi extension tools validate (and coerce) their arguments with upstream's validateToolArguments in their own runtime.
            return ValueTask.FromResult(Pi is not null || _schemas.TryGetValue(tool.Name, out var schema) && schema.Validate(arguments));
        }, invokerOptions: limits, options: (Pi is null ? new PiSharp.Extensions.Agent.ExtensionAgentBindingOptions() : PiSharp.Cli.Commands.PiPayloadBudget.PiBinding(new())) with
        {
            RestoreSystemMessage = (messages, token) => new SessionSystemReplay().Replay(messages, token).CurrentMessage,
            ReadSystemPrompt = (messages, token) => new SessionSystemReplay().Replay(messages, token).Prompt,
            ReportEventDiagnostic = _reportInputDiagnostic,
            ReportLoadoutDiagnostic = CaptureLoadoutDiagnostic
        }, sessionCancellationToken: _closing.Token);
        _loadoutDiagnostics?.Bind(Binding.Snapshot);
        UiPrompts?.Bind(_registry, Binding.Snapshot, _reportInputDiagnostic);
        BindMcpServersChange();
        // Capture exactly the binding revision, never a second per-operation handler set.
        // runner.ts emitInput runs every input handler with the whole text and every image (the Pi entry lifts the profile bounds).
        var input = Pi is null
            ? new RegisteredExtensionInputAdmission(_registry, Binding.Snapshot, sessionCancellationToken: _closing.Token, reportDiagnostic: _reportInputDiagnostic)
            : new RegisteredExtensionInputAdmission(_registry, Binding.Snapshot, PiSharp.Cli.Commands.PiPayloadBudget.PiEventDispatch, int.MaxValue,
                _closing.Token, _reportInputDiagnostic);
        RawInputHandlers = input;
        InputAdmission = new CommandInputAdmission(this, input);
    }

    internal void CaptureLoadoutDiagnostic(string toolName, Exception failure) => _loadoutDiagnostics?.Capture(toolName, failure);
    internal ValueTask DrainLoadoutDiagnosticsAsync(CancellationToken token) =>
        _loadoutDiagnostics?.DrainAsync(token) ?? ValueTask.CompletedTask;
    internal void RefuseReporterInitiatedDisposal() => _loadoutDiagnostics?.RefuseReporterInitiatedDisposal();

    internal void AttachOwner(ReplaceableAgentSession owner)
    {
        _sessionViews.Attach(owner);
        _facadeHost.Attach(owner);
        Pi?.AttachSession(owner);
        var compaction = new NativeSessionCompactionObservationBinding(_registry, Binding.Snapshot, _reportInputDiagnostic);
        compaction.Attach(owner, owner.Current);
        var metadata = new NativeSessionInfoChangedBinding(_registry, Binding.Snapshot, _reportInputDiagnostic);
        metadata.Attach(owner, owner.Current);
        var settled = new NativeAgentSettledObservationBinding(_registry, Binding.Snapshot, _reportInputDiagnostic);
        settled.Attach(owner, owner.Current);
        var sessionEvents = new NativeSessionEventBinding(_registry, Binding.Snapshot, _reportInputDiagnostic, ModelWire);
        sessionEvents.Attach(owner, owner.Current);
        var replacementStart = new NativeReplacementSessionStartBinding(_registry, Binding.Snapshot, _reportInputDiagnostic);
        _sessionViews.BeforeSwitch = new NativeSessionBeforeSwitchBinding(_registry, Binding.Snapshot, _closing.Token).BeforeSwitchAsync;
        _sessionViews.AfterSwitch = async replacement =>
        {
            compaction.Attach(owner, replacement.Current);
            metadata.Attach(owner, replacement.Current);
            settled.Attach(owner, replacement.Current);
            sessionEvents.Attach(owner, replacement.Current);
            Pi?.InstallInputGate(replacement.Current.Session);
            await replacementStart.PublishAsync(owner, replacement).ConfigureAwait(false);
        };
        owner.BeforeReplacement = _sessionViews.BeforeSwitch;
        owner.AfterReplacement = _sessionViews.AfterSwitch;
        owner.BeforeRetirement = new NativeSessionShutdownBinding(_registry, Binding.Snapshot, _reportInputDiagnostic).PublishAsync;
        owner.ValidateTargetAttachment = (previous, target, token) =>
        {
            var state = target.Snapshot;
            _registry.ValidateSessionSnapshot(new(state.Log.Header.Id, checked(previous.Generation + 1), state.Context.LeafId,
                state.Context.Ancestry.Select(entry => entry.WireBody).ToImmutableArray()), token);
            return ValueTask.CompletedTask;
        };
        owner.BeforeCreation = async (previous, request, token) =>
        {
            var entry = request.Kind == AgentSessionCreationKind.Clone ? previous.Session.Snapshot.Context.LeafId : request.EntryId;
            if (!await _registry.BeforeSessionCreationAsync(Binding.Snapshot,
                new(previous.Session.Snapshot.Log.Header.Id, (ExtensionSessionCreationKind)request.Kind, entry, request.ParentSession),
                token, _closing.Token).ConfigureAwait(false)) return false;
            var observation = request.Kind == AgentSessionCreationKind.New
                ? JsonData.Parse(JsonSerializer.Serialize(new { type = "session_before_switch", reason = "new" }))
                : JsonData.Parse(JsonSerializer.Serialize(new { type = "session_before_fork", entryId = entry,
                    position = request.Kind == AgentSessionCreationKind.ForkBefore ? "before" : "at" }));
            await _registry.DispatchObservationsAsync(Binding.Snapshot,
                request.Kind == AgentSessionCreationKind.New ? "session_before_switch" : "session_before_fork", observation,
                token, _closing.Token).ConfigureAwait(false);
            return true;
        };
    }
    internal async ValueTask DispatchSessionStartAsync(string reason, CancellationToken token)
    {
        await DrainLoadoutDiagnosticsAsync(token).ConfigureAwait(false);
        await _registry.DispatchObservationsAsync(Binding.Snapshot, "session_start",
            JsonData.Parse(JsonSerializer.Serialize(new { type = "session_start", reason })), token, _closing.Token).ConfigureAwait(false);
    }

    internal ImmutableArray<IPreparedToolAdapter> EnabledAdapters => Binding.Adapters
        .Where(adapter => IsEnabledTool(adapter.Name)).ToImmutableArray();
    private bool IsEnabledTool(string name) => Pi is not null || _configuration.EnabledTools.Contains(name, StringComparer.Ordinal);
    /// <summary>Pi extensions register commands after their factory returned (and on reload): commands resolve in the registry's
    /// current snapshot. A published native generation keeps its bound snapshot.</summary>
    private ExtensionRegistrySnapshot CommandSnapshot => Pi is not null ? _registry.CaptureSnapshot() : Binding.Snapshot;
    private bool IsEnabledCommand(string name) => Pi is not null ? CommandSnapshot.Commands.Any(command => command.Name == name)
        : _configuration.EnabledCommands.Contains(name, StringComparer.Ordinal);

    internal ExtensionSessionSnapshot? CaptureShutdownSessionSnapshot(AgentSessionAttachment? attached)
        => NativeSessionShutdownBinding.CaptureSnapshot(_registry, attached);

    internal ValueTask<bool> DispatchSessionShutdownAsync(ExtensionSessionSnapshot? retained) =>
        DispatchSessionShutdownAsync(retained, "quit");

    internal ValueTask<bool> DispatchSessionShutdownAsync(ExtensionSessionSnapshot? retained, string reason)
    {
        if (reason is not ("quit" or "reload")) throw new ArgumentException("Unsupported native shutdown reason.", nameof(reason));
        return
        _registry.DispatchSessionShutdownAsync(Binding.Snapshot,
            JsonData.Parse(JsonSerializer.Serialize(new { type = "session_shutdown", reason })), _reportInputDiagnostic, retained);
    }
    internal ImmutableArray<ExtensionToolRegistrationInfo> EnabledRegistrations => Binding.Registrations
        .Where(tool => IsEnabledTool(tool.Name)).ToImmutableArray();
    internal ImmutableArray<JsonData> EnabledDeclarations => Binding.Registrations
        .Where(tool => IsEnabledTool(tool.Name)).Select(tool =>
            JsonData.Parse(JsonSerializer.Serialize(new { name = tool.Name, description = tool.Description, parameters = tool.Parameters.Value }, DeclarationJson)))
        .ToImmutableArray();
    /// <summary>A tool's parameter schema keeps every JSON level Pi carries (JsonData.MaximumDepth), past the serializer's default 64.</summary>
    internal static JsonSerializerOptions DeclarationJson => JsonData.SerializerOptions;

    public JsonData CommandCatalog => CommandSnapshot.CommandCatalog;
    public ValueTask<JsonData> CompleteCommandAsync(string name, string prefix, CancellationToken token)
    {
        if (!IsEnabledCommand(name)) throw new NativeExtensionException(NativeExtensionFailure.InvalidConfiguration);
        return _registry.CompleteCommandAsync(CommandSnapshot, name, prefix, token, _closing.Token);
    }

    public bool IsRegisteredCommand(string text)
    {
        _closing.Token.ThrowIfCancellationRequested();
        if (!text.StartsWith('/')) return false;
        var separator = text.IndexOf(' '); var name = separator < 0 ? text[1..] : text[1..separator];
        return IsEnabledCommand(name);
    }

    public async ValueTask<bool> TryExecuteAsync(string text, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsRegisteredCommand(text)) return false;
        var separator = text.IndexOf(' '); var name = separator < 0 ? text[1..] : text[1..separator];
        var arguments = separator < 0 ? "" : text[(separator + 1)..];
        var lifecycleOrigin = BeginLifecycleOrigin();
        Task? invocationOriginal = null; Exception? invocationDirect = null;
        try
        {
            invocationOriginal = _registry.InvokeCommandAsync(CommandSnapshot, name,
                JsonData.Parse(JsonSerializer.Serialize(arguments)), token, _closing.Token).AsTask();
            await invocationOriginal.ConfigureAwait(false);
            return true;
        }
        catch (Exception error) { invocationDirect = error; throw; }
        finally { EndLifecycleOrigin(lifecycleOrigin, invocationOriginal, invocationDirect); }
    }

    private sealed class CommandInputAdmission(NativeExtensionActivation owner, IPromptInputAdmission inputHandlers) : IPromptInputAdmission
    {
        public async ValueTask<PromptInputDecision> ReduceAsync(PromptInput input, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); owner._closing.Token.ThrowIfCancellationRequested();
            // Extension-injected input bypasses interactive command parsing just as it bypasses the original source handler.
            if (input.Source != PromptInputSource.Extension && await owner.TryExecuteAsync(input.Text, token).ConfigureAwait(false))
                return new(PromptInputAction.Handled);
            return await inputHandlers.ReduceAsync(input, token).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        RefuseReporterInitiatedDisposal();
        TaskCompletionSource? settlement = null; Task pending;
        lock (_gate)
        {
            if (_disposal is null) { settlement = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposal = settlement.Task; }
            pending = _disposal;
        }
        if (settlement is not null) _ = CloseAsync(settlement);
        return new(pending);
    }
    private async Task CloseAsync(TaskCompletionSource settlement)
    {
        var failures = new List<Exception>();
        try { _closing.Cancel(); } catch (Exception error) { failures.Add(error); }
        // Retire before joining: no queued report may start as current, but every admitted original reporter must settle.
        try { await DrainLoadoutDiagnosticsAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        if (_loader is not null) await CollectNativeCleanupAsync(_loader.DisposeAsync, failures).ConfigureAwait(false);
        await CollectNativeCleanupAsync(_registry.DisposeAsync, failures).ConfigureAwait(false);
        try { _registrationBridge?.Dispose(); } catch (Exception error) { failures.Add(error); }
        try { _closing.Dispose(); } catch (Exception error) { failures.Add(error); }
        if (failures.Count > 0) settlement.TrySetException(new NativeExtensionException(NativeExtensionFailure.CleanupFailed, new AggregateException(failures)));
        else settlement.TrySetResult();
    }

}
