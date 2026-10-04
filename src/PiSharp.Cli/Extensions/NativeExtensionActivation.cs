using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.CodingAgent;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Discovery;
using PiSharp.Extensions.Runtime.Loading;
using PiSharp.Extensions;
using PiSharp.Extensions.Events;
using PiSharp.Sessions.Context;

namespace PiSharp.Cli.Extensions;

/// <summary>Owns one approved published generation and its actual transactional registry.</summary>
internal sealed class NativeExtensionActivation : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly ExtensionRegistry _registry;
    private readonly PluginAssemblyLoader _loader;
    private readonly CancellationTokenSource _closing = new();
    private readonly ImmutableDictionary<string, NativeToolObjectSchema> _schemas;
    private readonly NativeSessionSnapshotProvider _sessionViews;
    private readonly NativeExtensionConfiguration _configuration;
    private readonly Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? _reportInputDiagnostic;
    private Task? _disposal;
    internal ImmutableDictionary<string, string> Targets { get; }
    internal ExtensionAgentBinding Binding { get; private set; } = null!;
    internal IPromptInputAdmission InputAdmission { get; private set; } = null!;

    private NativeExtensionActivation(NativeExtensionConfiguration configuration, ExtensionRegistry registry,
        PluginAssemblyLoader loader, ExtensionRegistrySnapshot snapshot, ImmutableDictionary<string, NativeToolObjectSchema> schemas,
        NativeSessionSnapshotProvider sessionViews,
        Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? reportInputDiagnostic)
    {
        _configuration = configuration; _registry = registry; _loader = loader; _schemas = schemas; _sessionViews = sessionViews;
        _reportInputDiagnostic = reportInputDiagnostic;
        Targets = snapshot.Tools.Where(tool => configuration.EnabledTools.Contains(tool.Name, StringComparer.Ordinal) &&
                !configuration.DeniedTools.Contains(tool.Name, StringComparer.Ordinal))
            .ToImmutableDictionary(tool => tool.Name, tool => tool.OwnerId + "/" + tool.OwnerGeneration.ToString(CultureInfo.InvariantCulture) +
                "/" + tool.RegistrationId, StringComparer.Ordinal);
    }

    internal static async Task<NativeExtensionActivation> LoadAsync(NativeExtensionPreflight preflight, CancellationToken token,
        IExtensionUiProvider? uiProvider = null,
        Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? reportInputDiagnostic = null)
    {
        var sessionViews = new NativeSessionSnapshotProvider();
        var registry = new ExtensionRegistry(new() { MaximumOwners = 1, MaximumRegistrations = 64, MaximumRegistrationsPerOwner = 64 },
            uiProvider ?? new UnavailableExtensionUiProvider(), sessionViews);
        var loader = new PluginAssemblyLoader(new() { SnapshotParentDirectory = preflight.Configuration.SnapshotRoot,
            MaximumOwnedPackages = 1 });
        try
        {
            await loader.LoadAsync(preflight.Metadata, preflight.Configuration.Package, ExtensionSourceScope.Explicit,
                NativeExtensionConfiguration.Scope, preflight.Inspection, preflight.Execution, registry, token).ConfigureAwait(false);
            var snapshot = registry.CaptureSnapshot();
            if (snapshot.Registrations.Any(entry => entry.Kind is not ("Tool" or "Command" or "InputHandler" or "ToolCallHandler" or "ToolResultHandler" or "Observation" or "SessionSwitchHandler" or "SessionCreationHandler" or "ContextHandler" or "ContextWithSystemHandler" or "BeforeAgentStartHandler")) ||
                preflight.Configuration.EnabledTools.Any(name => snapshot.Tools.Count(tool => tool.Name == name) != 1) ||
                preflight.Configuration.EnabledCommands.Any(name => snapshot.Commands.Count(command => command.Name == name) != 1))
                throw new NativeExtensionException(NativeExtensionFailure.InvalidConfiguration);
            var schemas = snapshot.Tools.ToImmutableDictionary(tool => tool.Name, tool => NativeToolObjectSchema.Read(tool.Parameters), StringComparer.Ordinal);
            token.ThrowIfCancellationRequested();
            return new(preflight.Configuration, registry, loader, snapshot, schemas, sessionViews, reportInputDiagnostic);
        }
        catch (Exception original)
        {
            var failures = new List<Exception>();
            try { await loader.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            try { await registry.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            if (failures.Count > 0) throw new NativeExtensionException(NativeExtensionFailure.CleanupFailed, new AggregateException(failures.Prepend(original)));
            if (original is OperationCanceledException && token.IsCancellationRequested) throw;
            if (original is NativeExtensionException) throw;
            throw new NativeExtensionException(NativeExtensionFailure.ActivationFailed, original);
        }
    }

    internal void Bind(IToolActionPolicy policy, ToolInvokerOptions limits)
    {
        if (Binding is not null) throw new InvalidOperationException("Native generation is already bound.");
        Binding = new(_registry, policy, (tool, arguments, token) =>
        {
            token.ThrowIfCancellationRequested(); _closing.Token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_schemas.TryGetValue(tool.Name, out var schema) && schema.Validate(arguments));
        }, invokerOptions: limits, options: new()
        {
            RestoreSystemMessage = (messages, token) => new SessionSystemReplay().Replay(messages, token).CurrentMessage,
            ReadSystemPrompt = (messages, token) => new SessionSystemReplay().Replay(messages, token).Prompt,
            ReportEventDiagnostic = _reportInputDiagnostic
        }, sessionCancellationToken: _closing.Token);
        // Capture exactly the binding revision, never a second per-operation handler set.
        var input = new RegisteredExtensionInputAdmission(_registry, Binding.Snapshot, sessionCancellationToken: _closing.Token,
            reportDiagnostic: _reportInputDiagnostic);
        InputAdmission = new CommandInputAdmission(this, input);
    }

    internal void AttachOwner(ReplaceableAgentSession owner)
    {
        _sessionViews.Attach(owner);
        var compaction = new NativeSessionCompactionObservationBinding(_registry, Binding.Snapshot, _reportInputDiagnostic);
        compaction.Attach(owner, owner.Current);
        var replacementStart = new NativeReplacementSessionStartBinding(_registry, Binding.Snapshot, _reportInputDiagnostic);
        _sessionViews.BeforeSwitch = new NativeSessionBeforeSwitchBinding(_registry, Binding.Snapshot, _closing.Token).BeforeSwitchAsync;
        _sessionViews.AfterSwitch = async replacement =>
        {
            compaction.Attach(owner, replacement.Current);
            await replacementStart.PublishAsync(owner, replacement).ConfigureAwait(false);
        };
        owner.BeforeReplacement = _sessionViews.BeforeSwitch;
        owner.AfterReplacement = _sessionViews.AfterSwitch;
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
        await _registry.DispatchObservationsAsync(Binding.Snapshot, "session_start",
            JsonData.Parse(JsonSerializer.Serialize(new { type = "session_start", reason })), token, _closing.Token).ConfigureAwait(false);
    }

    internal ImmutableArray<IPreparedToolAdapter> EnabledAdapters => Binding.Adapters
        .Where(adapter => _configuration.EnabledTools.Contains(adapter.Name, StringComparer.Ordinal)).ToImmutableArray();
    internal ImmutableArray<ExtensionToolRegistrationInfo> EnabledRegistrations => Binding.Registrations
        .Where(tool => _configuration.EnabledTools.Contains(tool.Name, StringComparer.Ordinal)).ToImmutableArray();
    internal ImmutableArray<JsonData> EnabledDeclarations => Binding.Registrations
        .Where(tool => _configuration.EnabledTools.Contains(tool.Name, StringComparer.Ordinal)).Select(tool =>
            JsonData.Parse(JsonSerializer.Serialize(new { name = tool.Name, description = tool.Description, parameters = tool.Parameters.Value })))
        .ToImmutableArray();

    internal JsonData CommandCatalog => Binding.Snapshot.CommandCatalog;
    internal ValueTask<JsonData> CompleteCommandAsync(string name, string prefix, CancellationToken token)
    {
        if (!_configuration.EnabledCommands.Contains(name, StringComparer.Ordinal)) throw new NativeExtensionException(NativeExtensionFailure.InvalidConfiguration);
        return _registry.CompleteCommandAsync(Binding.Snapshot, name, prefix, token, _closing.Token);
    }

    private sealed class CommandInputAdmission(NativeExtensionActivation owner, IPromptInputAdmission inputHandlers) : IPromptInputAdmission
    {
        public async ValueTask<PromptInputDecision> ReduceAsync(PromptInput input, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); owner._closing.Token.ThrowIfCancellationRequested();
            // Extension-injected input bypasses interactive command parsing just as it bypasses the original source handler.
            if (input.Source != PromptInputSource.Extension && input.Text.StartsWith('/'))
            {
                var separator = input.Text.IndexOf(' '); var name = separator < 0 ? input.Text[1..] : input.Text[1..separator];
                if (owner._configuration.EnabledCommands.Contains(name, StringComparer.Ordinal))
                {
                    var arguments = separator < 0 ? "" : input.Text[(separator + 1)..];
                    await owner._registry.InvokeCommandAsync(owner.Binding.Snapshot, name,
                        JsonData.Parse(JsonSerializer.Serialize(arguments)), token, owner._closing.Token).ConfigureAwait(false);
                    return new(PromptInputAction.Handled);
                }
            }
            return await inputHandlers.ReduceAsync(input, token).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
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
        try { await _loader.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        try { await _registry.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        try { _closing.Dispose(); } catch (Exception error) { failures.Add(error); }
        if (failures.Count > 0) settlement.TrySetException(new NativeExtensionException(NativeExtensionFailure.CleanupFailed, new AggregateException(failures)));
        else settlement.TrySetResult();
    }

}
