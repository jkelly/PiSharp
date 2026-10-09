using System.Collections.Immutable;
using System.Text;
using PiSharp.Contracts;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Facade.Context;

namespace PiSharp.Extensions.Runtime;

/// <summary>Experimental transactional descriptor registry. It does not load assemblies or grant trust.</summary>
public sealed partial class ExtensionRegistry : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly object identity = new();
    private readonly ExtensionRegistryOptions options;
    private readonly IExtensionUiProvider? uiProvider;
    private readonly IExtensionSessionViewProvider? sessionProvider;
    internal IExtensionContextReadHost? FacadeHost { get; }
    private readonly Dictionary<string, RegistrationScope> owners = new(StringComparer.Ordinal);
    private readonly List<RegistrationScope> ownerOrder = [];
    private ExtensionRegistrySnapshot snapshot;
    private long generation;
    private long revision;
    private long metadataCharacters;
    private int chargedRegistrations;
    private int dispatches;
    private bool closing;
    private TaskCompletionSource? disposal;

    /// <summary>Features available from this host configuration, including optional callback capabilities.</summary>
    public ImmutableArray<string> AvailableFeatures { get; }

    public ExtensionRegistry(ExtensionRegistryOptions? options = null, IExtensionUiProvider? uiProvider = null)
        : this(options, uiProvider, null) { }

    public ExtensionRegistry(ExtensionRegistryOptions? options, IExtensionUiProvider? uiProvider,
        IExtensionSessionViewProvider? sessionProvider)
        : this(options, uiProvider, sessionProvider, null) { }

    public ExtensionRegistry(ExtensionRegistryOptions? options, IExtensionUiProvider? uiProvider,
        IExtensionSessionViewProvider? sessionProvider, IExtensionContextReadHost? facadeHost)
    {
        this.options = options ?? new();
        this.uiProvider = uiProvider;
        this.sessionProvider = sessionProvider;
        FacadeHost = facadeHost;
        var supported = ExperimentalExtensionContract.Features;
        AvailableFeatures = sessionProvider is null ? supported.Remove(ExtensionSessionSnapshotLimits.Feature) :
            supported.Contains(ExtensionSessionSnapshotLimits.Feature) ? supported :
            supported.Add(ExtensionSessionSnapshotLimits.Feature);
        if (sessionProvider is not IExtensionSessionActionProvider)
            AvailableFeatures = AvailableFeatures.Remove(ExtensionSessionActionFeatures.DurableEntries).Remove(ExtensionSessionActionFeatures.Replacement);
        if (sessionProvider is not IExtensionSessionCreationProvider)
            AvailableFeatures = AvailableFeatures.Remove(ExtensionSessionActionFeatures.Creation);
        if (sessionProvider is not IExtensionSessionOpaqueViewProvider)
            AvailableFeatures = AvailableFeatures.Remove(ExtensionSessionActionFeatures.OpaqueRecords);
        if (sessionProvider is not IExtensionSessionCatalogProvider)
            AvailableFeatures = AvailableFeatures.Remove(ExtensionSessionActionFeatures.Catalog);
        if (sessionProvider is not IExtensionSessionContextEditProvider)
            AvailableFeatures = AvailableFeatures.Remove(ExtensionSessionActionFeatures.ContextEdits);
        if (sessionProvider is IExtensionSessionToolActivationProvider)
            AvailableFeatures = AvailableFeatures.Add(ExtensionToolActivationFeatures.Feature);
        AvailableFeatures = AvailableFeatures.Add(ExtensionToolRendererFeatures.Feature);
        RegistrationPolicy.ValidateOptions(this.options);
        snapshot = new(identity, revision, []);
    }

    public ExtensionRegistrySnapshot CaptureSnapshot() => Volatile.Read(ref snapshot);

    /// <summary>Whether dispatches resolve the current registrations (<see cref="ExtensionRegistryOptions.FollowCurrentSnapshot"/>).</summary>
    public bool FollowsCurrentSnapshot => options.FollowCurrentSnapshot;

    /// <summary>The revision a dispatch over <paramref name="captured"/> resolves: the current one when the registry follows its
    /// current snapshot (<see cref="ExtensionRegistryOptions.FollowCurrentSnapshot"/>), else the captured one.</summary>
    public ExtensionRegistrySnapshot Current(ExtensionRegistrySnapshot captured)
    {
        ArgumentNullException.ThrowIfNull(captured);
        return options.FollowCurrentSnapshot && ReferenceEquals(captured.RegistryIdentity, identity) ? CaptureSnapshot() : captured;
    }

    public Task<RegistrationScope> ActivateAsync(string ownerId, IPiSharpExtension extension,
        CancellationToken initializationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extension);
        if (!RegistrationPolicy.Identifier(ownerId, options.MaximumIdentifierCharacters))
            throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, "invalid-owner", "activate");
        initializationToken.ThrowIfCancellationRequested();
        RegistrationScope scope;
        lock (gate)
        {
            if (closing) throw Failure(ExtensionRegistrationFailure.InactiveScope, ownerId, "activate");
            if (owners.ContainsKey(ownerId)) throw Failure(ExtensionRegistrationFailure.DuplicateOwner, ownerId, "activate");
            if (owners.Count >= options.MaximumOwners || metadataCharacters + ownerId.Length > options.MaximumMetadataCharacters)
                throw Failure(ExtensionRegistrationFailure.LimitExceeded, ownerId, "activate");
            scope = new(this, ownerId, checked(++generation), extension);
            owners.Add(ownerId, scope);
            ownerOrder.Add(scope);
            metadataCharacters += ownerId.Length;
        }
        return scope.InitializeAsync(initializationToken);
    }

    internal IExtensionRegistration Register(RegistrationScope scope, ExtensionToolDescriptor descriptor)
    {
        const string operation = "register-tool";
        if (descriptor is null || !ValidNames(descriptor.RegistrationId, descriptor.Name) ||
            !RegistrationPolicy.Description(descriptor.Description, options) || descriptor.ExecuteAsync is null ||
            descriptor.PrepareInitialArgumentsAsync?.GetInvocationList().Length > 1 ||
            descriptor.PrepareLoadout?.GetInvocationList().Length > 1 || !Enum.IsDefined(descriptor.Exposure) ||
            descriptor.Namespace is { } grouping && (!RegistrationPolicy.Description(grouping.Name, options) ||
                grouping.Description is not null && !RegistrationPolicy.Description(grouping.Description, options) ||
                grouping.Instructions is not null && !RegistrationPolicy.Description(grouping.Instructions, options)) ||
            descriptor.PromptGuidelines.IsDefault || descriptor.PromptGuidelines.Length > options.MaximumRegistrationsPerOwner ||
            descriptor.PromptGuidelines.Any(guideline => !RegistrationPolicy.Description(guideline, options)) ||
            descriptor.Renderers is { } renderers && (renderers.RenderShell is { } shell && !Enum.IsDefined(shell) ||
                renderers.RenderCall?.GetInvocationList().Length > 1 || renderers.RenderResult?.GetInvocationList().Length > 1) ||
            !RegistrationPolicy.Json(descriptor.Parameters, options, requireObject: true) || !Enum.IsDefined(descriptor.ParametersOrigin) ||
            descriptor.ValidationParameters is { } validation && !RegistrationPolicy.Json(validation, options, requireObject: true) ||
            descriptor.ConstrainedSampling is { } sampling && !RegistrationPolicy.Json(sampling, options, requireObject: true))
            throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, scope.OwnerId, operation);
        return Add(scope, descriptor.RegistrationId, descriptor.Name, RegistrationKind.Tool, descriptor,
            (long)descriptor.RegistrationId.Length + descriptor.Name.Length + descriptor.Description.Length + descriptor.Parameters.ToString().Length +
                (descriptor.Namespace?.Name.Length ?? 0) + (descriptor.Namespace?.Description?.Length ?? 0) +
                (descriptor.Namespace?.Instructions?.Length ?? 0) + descriptor.PromptGuidelines.Sum(guideline => (long)guideline.Length) +
                (descriptor.ConstrainedSampling?.ToString().Length ?? 0),
            operation);
    }

    /// <summary>Source registerToolRenderer: one resolver, consulted in extension load order then registration order.</summary>
    internal IExtensionRegistration Register(RegistrationScope scope, ExtensionToolRendererDescriptor descriptor)
    {
        const string operation = "register-tool-renderer";
        if (descriptor is null || !ValidNames(descriptor.RegistrationId, ToolRendererTopic) || descriptor.Resolve is null ||
            descriptor.Resolve.GetInvocationList().Length != 1)
            throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, scope.OwnerId, operation);
        return Add(scope, descriptor.RegistrationId, ToolRendererTopic, RegistrationKind.ToolRenderer, descriptor,
            descriptor.RegistrationId.Length, operation);
    }

    private const string ToolRendererTopic = "tool_renderer";

    /// <summary>
    /// Source ExtensionRunner.resolveToolRenderers: the renderers for calls to <paramref name="toolName"/>, from the captured
    /// resolvers in load order, then the registered tool's own renderers, then <paramref name="fallback"/> (for example a
    /// built-in tool's renderers). Each resolver runs once at most, under its owner's callback lease.
    /// </summary>
    public ExtensionToolRenderers? ResolveToolRenderers(ExtensionRegistrySnapshot captured, string toolName,
        Func<ExtensionToolRenderers?>? fallback = null, CancellationToken operationToken = default)
    {
        ArgumentNullException.ThrowIfNull(captured);
        if (!RegistrationPolicy.Description(toolName, options) || toolName.Length == 0)
            throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, "registry", "resolve-tool-renderers");
        captured = Current(captured);
        var admission = Admit(captured, RegistrationKind.ToolRenderer, ToolRendererTopic, "resolve-tool-renderers", operationToken, default);
        try
        {
            var tool = captured.Entries.FirstOrDefault(entry => entry.Kind == RegistrationKind.Tool && entry.Name == toolName);
            ExtensionToolRenderers? Base() => tool is not null ? ((ExtensionToolDescriptor)tool.Descriptor).Renderers ?? fallback?.Invoke() : fallback?.Invoke();
            ExtensionToolRenderers? Resolve(int index)
            {
                if (index >= admission.Length) return Base();
                var (scope, entry) = admission[index];
                operationToken.ThrowIfCancellationRequested();
                scope.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
                using var frame = new CallbackFrame(scope);
                return ((ExtensionToolRendererDescriptor)entry.Descriptor).Resolve(toolName, () => Resolve(index + 1));
            }
            return Resolve(0);
        }
        finally { ReleaseAdmission(admission); }
    }

    internal IExtensionRegistration Register(RegistrationScope scope, ExtensionCommandDescriptor descriptor)
    {
        const string operation = "register-command";
        if (descriptor is null || !(options.AllowAnyCommandName
                ? RegistrationPolicy.Identifier(descriptor.RegistrationId, options.MaximumIdentifierCharacters) && RegistrationPolicy.CommandName(descriptor.Name, options.MaximumIdentifierCharacters)
                : ValidNames(descriptor.RegistrationId, descriptor.Name)) ||
            !RegistrationPolicy.Description(descriptor.Description, options) || descriptor.ExecuteAsync is null ||
            descriptor.GetArgumentCompletionsAsync?.GetInvocationList().Length > 1 ||
            descriptor.SourcePath is not null && !RegistrationPolicy.Description(descriptor.SourcePath, options))
            throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, scope.OwnerId, operation);
        return Add(scope, descriptor.RegistrationId, descriptor.Name, RegistrationKind.Command, descriptor,
            (long)descriptor.RegistrationId.Length + descriptor.Name.Length + descriptor.Description.Length +
                (descriptor.SourcePath?.Length ?? 0), operation);
    }

    internal IExtensionRegistration Register(RegistrationScope scope, ExtensionObservationDescriptor descriptor)
    {
        const string operation = "observe";
        if (descriptor is null || !ValidNames(descriptor.RegistrationId, descriptor.Topic) || descriptor.ObserveAsync is null)
            throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, scope.OwnerId, operation);
        return Add(scope, descriptor.RegistrationId, descriptor.Topic, RegistrationKind.Observation, descriptor,
            (long)descriptor.RegistrationId.Length + descriptor.Topic.Length, operation);
    }

    internal IExtensionRegistration Register(RegistrationScope scope, ExtensionBeforeAgentStartHandlerDescriptor descriptor)
    {
        const string operation = "register-before-agent-start-handler";
        if (descriptor is null || !ValidNames(descriptor.RegistrationId, "before_agent_start") || descriptor.HandleAsync is null)
            throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, scope.OwnerId, operation);
        return Add(scope, descriptor.RegistrationId, "before_agent_start", RegistrationKind.BeforeAgentStartHandler, descriptor,
            (long)descriptor.RegistrationId.Length + "before_agent_start".Length, operation);
    }

    internal IExtensionRegistration Register(RegistrationScope scope, ExtensionContextHandlerDescriptor descriptor)
    {
        const string operation = "register-context-handler";
        if (descriptor is null || !ValidNames(descriptor.RegistrationId, "context") || descriptor.HandleAsync is null)
            throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, scope.OwnerId, operation);
        return Add(scope, descriptor.RegistrationId, "context", RegistrationKind.ContextHandler, descriptor,
            (long)descriptor.RegistrationId.Length + "context".Length, operation);
    }

    internal IExtensionRegistration Register(RegistrationScope scope, ExtensionContextWithSystemHandlerDescriptor descriptor)
    {
        const string operation = "register-context-with-system-handler";
        if (descriptor is null || !ValidNames(descriptor.RegistrationId, "context_with_system") || descriptor.HandleAsync is null)
            throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, scope.OwnerId, operation);
        return Add(scope, descriptor.RegistrationId, "context_with_system", RegistrationKind.ContextWithSystemHandler, descriptor,
            (long)descriptor.RegistrationId.Length + "context_with_system".Length, operation);
    }

    internal IExtensionRegistration Register(RegistrationScope scope, ExtensionInputHandlerDescriptor descriptor)
    {
        const string operation = "register-input-handler";
        if (descriptor is null || !ValidNames(descriptor.RegistrationId, "input") || descriptor.HandleAsync is null)
            throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, scope.OwnerId, operation);
        return Add(scope, descriptor.RegistrationId, "input", RegistrationKind.InputHandler, descriptor,
            (long)descriptor.RegistrationId.Length + "input".Length, operation);
    }

    internal IExtensionRegistration Register(RegistrationScope scope, ExtensionToolCallHandlerDescriptor descriptor)
    {
        const string operation = "register-tool-call-handler";
        if (descriptor is null || !ValidNames(descriptor.RegistrationId, "tool_call") || descriptor.HandleAsync is null)
            throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, scope.OwnerId, operation);
        return Add(scope, descriptor.RegistrationId, "tool_call", RegistrationKind.ToolCallHandler, descriptor,
            (long)descriptor.RegistrationId.Length + "tool_call".Length, operation);
    }

    internal IExtensionRegistration Register(RegistrationScope scope, ExtensionToolResultHandlerDescriptor descriptor)
    {
        const string operation = "register-tool-result-handler";
        if (descriptor is null || !ValidNames(descriptor.RegistrationId, "tool_result") || descriptor.HandleAsync is null)
            throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, scope.OwnerId, operation);
        return Add(scope, descriptor.RegistrationId, "tool_result", RegistrationKind.ToolResultHandler, descriptor,
            (long)descriptor.RegistrationId.Length + "tool_result".Length, operation);
    }

    internal IExtensionRegistration Register(RegistrationScope scope, ExtensionSessionSwitchHandlerDescriptor descriptor)
    {
        const string operation = "register-session-switch-handler";
        if (descriptor is null || !ValidNames(descriptor.RegistrationId, "session_before_switch") || descriptor.HandleAsync is null)
            throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, scope.OwnerId, operation);
        return Add(scope, descriptor.RegistrationId, "session_before_switch", RegistrationKind.SessionSwitchHandler, descriptor,
            (long)descriptor.RegistrationId.Length + "session_before_switch".Length, operation);
    }

    /// <summary>Each admitted pre-action handler sees one captured generation. Cancel short-circuits;
    /// handler failure is propagated separately, allowing the staging owner to roll back without treating failure as a veto.</summary>
    public async ValueTask<bool> BeforeSessionSwitchAsync(ExtensionRegistrySnapshot captured, ExtensionSessionSwitchEvent proposal,
        CancellationToken operationToken = default, CancellationToken sessionToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var admission = Admit(captured, RegistrationKind.SessionSwitchHandler, "session_before_switch", "before-session-switch",
            operationToken, sessionToken);
        try
        {
            using var dispatchFrame = new CallbackFrame(admission.Select(item => item.Scope).Distinct().ToImmutableArray());
            foreach (var (scope, entry) in admission)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(operationToken, sessionToken, scope.ExtensionLifetimeCancellationToken);
                await using var context = await CreateUiContextAsync(scope, operationToken, sessionToken).ConfigureAwait(false);
                var decision = await ((ExtensionSessionSwitchHandlerDescriptor)entry.Descriptor).HandleAsync(proposal,
                    context.Context, linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                if (decision == ExtensionSessionSwitchDecision.Cancel) return false;
                if (decision != ExtensionSessionSwitchDecision.Continue)
                    throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, scope.OwnerId, "before-session-switch");
            }
            return true;
        }
        finally { ReleaseAdmission(admission); }
    }

    internal IExtensionRegistration Register(RegistrationScope scope, ExtensionSessionCreationHandlerDescriptor descriptor)
    {
        const string operation = "register-session-creation-handler";
        if (descriptor is null || !ValidNames(descriptor.RegistrationId, "session_before_creation") || descriptor.HandleAsync is null)
            throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, scope.OwnerId, operation);
        return Add(scope, descriptor.RegistrationId, "session_before_creation", RegistrationKind.SessionCreationHandler, descriptor,
            (long)descriptor.RegistrationId.Length + "session_before_creation".Length, operation);
    }

    public async ValueTask<bool> BeforeSessionCreationAsync(ExtensionRegistrySnapshot captured, ExtensionSessionCreationEvent proposal,
        CancellationToken operationToken = default, CancellationToken sessionToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var admission = Admit(captured, RegistrationKind.SessionCreationHandler, "session_before_creation", "before-session-creation",
            operationToken, sessionToken);
        try
        {
            using var dispatchFrame = new CallbackFrame(admission.Select(item => item.Scope).Distinct().ToImmutableArray());
            foreach (var (scope, entry) in admission)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(operationToken, sessionToken, scope.ExtensionLifetimeCancellationToken);
                await using var context = await CreateUiContextAsync(scope, operationToken, sessionToken).ConfigureAwait(false);
                var decision = await ((ExtensionSessionCreationHandlerDescriptor)entry.Descriptor).HandleAsync(proposal,
                    context.Context, linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                if (decision == ExtensionSessionSwitchDecision.Cancel) return false;
                if (decision != ExtensionSessionSwitchDecision.Continue)
                    throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, scope.OwnerId, "before-session-creation");
            }
            return true;
        }
        finally { ReleaseAdmission(admission); }
    }

    /// <summary>Leased synchronous preparation without a UI, session mutation scope or execution context.</summary>
    public ToolLoadoutChanges? PrepareToolLoadout(ExtensionRegistrySnapshot captured, string name, ToolLoadout loadout,
        CancellationToken operationToken = default, CancellationToken sessionToken = default)
    {
        ArgumentNullException.ThrowIfNull(loadout);
        var admission = Admit(captured, RegistrationKind.Tool, name, "prepare-loadout", operationToken, sessionToken);
        try
        {
            var (scope, entry) = admission.Single();
            using var frame = new CallbackFrame(scope);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(operationToken, sessionToken, scope.ExtensionLifetimeCancellationToken);
            linked.Token.ThrowIfCancellationRequested();
            var result = ((ExtensionToolDescriptor)entry.Descriptor).PrepareLoadout?.Invoke(loadout);
            linked.Token.ThrowIfCancellationRequested();
            return result;
        }
        finally { ReleaseAdmission(admission); }
    }

    private bool ValidNames(string id, string name) => RegistrationPolicy.Identifier(id, options.MaximumIdentifierCharacters) &&
        RegistrationPolicy.Identifier(name, options.MaximumIdentifierCharacters);

    private IExtensionRegistration Add(RegistrationScope scope, string id, string name, RegistrationKind kind,
        object descriptor, long characters, string operation)
    {
        lock (gate)
        {
            EnsureScope(scope, operation, allowInitializing: true);
            if (scope.Staged.ContainsId(id)) throw Failure(ExtensionRegistrationFailure.DuplicateRegistrationId, scope.OwnerId, operation);
            var reserved = kind == RegistrationKind.Tool ? options.ReservedToolNames : options.ReservedCommandNames;
            if ((kind is RegistrationKind.Tool or RegistrationKind.Command) && reserved.Contains(name, StringComparer.Ordinal))
                throw Failure(ExtensionRegistrationFailure.ReservedName, scope.OwnerId, operation);
            if ((kind is RegistrationKind.Tool or RegistrationKind.Command) &&
                (kind == RegistrationKind.Command && options.SuffixDuplicateCommandNames ? scope.Staged.ContainsName(kind, name) : ownerOrder.Any(owner => owner.Staged.ContainsName(kind, name))))
                throw Failure(ExtensionRegistrationFailure.DuplicateName, scope.OwnerId, operation);
            if (chargedRegistrations >= options.MaximumRegistrations ||
                scope.ChargedRegistrations >= options.MaximumRegistrationsPerOwner ||
                characters > options.MaximumMetadataCharacters || metadataCharacters + characters > options.MaximumMetadataCharacters)
                throw Failure(ExtensionRegistrationFailure.LimitExceeded, scope.OwnerId, operation);
            var entry = new RegistrationEntry
            {
                OwnerId = scope.OwnerId, OwnerGeneration = scope.OwnerGeneration, RegistrationId = id,
                Name = name, Kind = kind, Descriptor = descriptor, MetadataCharacters = (int)characters
            };
            scope.Staged.Add(entry);
            scope.ChargedRegistrations++;
            chargedRegistrations++;
            metadataCharacters += characters;
            if (scope.State == RegistrationScopeState.Active) Publish();
            return new RegistrationHandle(scope, entry);
        }
    }

    internal void Commit(RegistrationScope scope, CancellationToken initializationToken)
    {
        lock (gate)
        {
            EnsureScope(scope, "initialize", allowInitializing: true);
            initializationToken.ThrowIfCancellationRequested();
            scope.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
            scope.State = RegistrationScopeState.Active;
            Publish();
        }
    }

    internal void Remove(RegistrationScope scope, RegistrationEntry entry)
    {
        lock (gate)
        {
            // Entry identity prevents an old handle removing a new registration that reuses its ID.
            if (!scope.Staged.Remove(entry)) return;
            entry.Registered = false;
            RetireEventBusSubscription(entry);
            ReleaseChargeIfRetired(scope, entry);
            if (scope.State == RegistrationScopeState.Active) Publish();
        }
    }

    /// <summary>Removes every registration of the owner at once (its tools too unless <paramref name="keepTools"/>), in one publication.</summary>
    internal void Withdraw(RegistrationScope scope, bool keepTools)
    {
        lock (gate)
        {
            var removed = false;
            foreach (var entry in scope.Staged.Entries.ToArray())
            {
                if (keepTools && entry.Kind == RegistrationKind.Tool || !scope.Staged.Remove(entry)) continue;
                entry.Registered = false;
                RetireEventBusSubscription(entry);
                ReleaseChargeIfRetired(scope, entry);
                removed = true;
            }
            if (removed && scope.State == RegistrationScopeState.Active) Publish();
        }
    }

    private void ReleaseChargeIfRetired(RegistrationScope scope, RegistrationEntry entry)
    {
        if (entry.Registered || entry.Leases != 0 || !entry.Charged) return;
        entry.Charged = false;
        scope.ChargedRegistrations--;
        chargedRegistrations--;
        metadataCharacters -= entry.MetadataCharacters;
    }

    private void EnsureScope(RegistrationScope scope, string operation, bool allowInitializing = false)
    {
        if (closing || !owners.TryGetValue(scope.OwnerId, out var current) || !ReferenceEquals(current, scope) ||
            (scope.State != RegistrationScopeState.Active && !(allowInitializing && scope.State == RegistrationScopeState.Initializing)))
            throw Failure(ExtensionRegistrationFailure.InactiveScope, scope.OwnerId, operation);
    }

    private void Publish()
    {
        var entries = ownerOrder.Where(owner => owner.State == RegistrationScopeState.Active)
            .SelectMany(owner => owner.Staged.Entries).ToImmutableArray();
        Volatile.Write(ref snapshot, new(identity, checked(++revision), entries, CommandInvocationNames(entries)));
    }

    /// <summary>Pi runner resolveRegisteredCommands over the published commands (when <see cref="ExtensionRegistryOptions.SuffixDuplicateCommandNames"/>).</summary>
    internal ImmutableDictionary<RegistrationEntry, string>? CommandInvocationNames(ImmutableArray<RegistrationEntry> entries)
    {
        if (!options.SuffixDuplicateCommandNames) return null;
        var commands = entries.Where(entry => entry.Kind == RegistrationKind.Command).ToArray();
        var counts = commands.GroupBy(entry => entry.Name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal); var taken = new HashSet<string>(StringComparer.Ordinal);
        var names = ImmutableDictionary.CreateBuilder<RegistrationEntry, string>(ReferenceEqualityComparer.Instance);
        foreach (var command in commands)
        {
            var occurrence = seen[command.Name] = seen.GetValueOrDefault(command.Name) + 1;
            var invocation = counts[command.Name] > 1 ? $"{command.Name}:{occurrence}" : command.Name;
            if (taken.Contains(invocation))
            {
                var suffix = occurrence;
                do { suffix++; invocation = $"{command.Name}:{suffix}"; } while (taken.Contains(invocation));
            }
            taken.Add(invocation); names[command] = invocation;
        }
        return names.ToImmutable();
    }

    /// <summary>Initial-only pure argument preparation under the same owner/snapshot lease as execution.</summary>
    public async ValueTask<JsonData> PrepareToolArgumentsAsync(ExtensionRegistrySnapshot captured, string name, JsonData arguments,
        CancellationToken operationToken = default, CancellationToken sessionToken = default)
    {
        ValidateDispatchData(arguments, "prepare-tool-arguments");
        var admission = Admit(captured, RegistrationKind.Tool, name, "prepare-tool-arguments", operationToken, sessionToken);
        try
        {
            var (scope, entry) = admission.Single();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(operationToken, sessionToken, scope.ExtensionLifetimeCancellationToken);
            using var frame = new CallbackFrame(scope);
            var prepare = ((ExtensionToolDescriptor)entry.Descriptor).PrepareInitialArgumentsAsync;
            if (prepare is null) return arguments;
            // Preparation receives no host/UI context and cannot advertise a result or authorize an effect.
            JsonData result;
            // Source prepareToolCall catches what prepareArguments throws and reports its message as the error result.
            try { result = await prepare(arguments, linked.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is not (OperationCanceledException or ExtensionToolArgumentPreparationException))
            { throw new ExtensionToolArgumentPreparationException(error.Message, error); }
            linked.Token.ThrowIfCancellationRequested();
            if (!RegistrationPolicy.Json(result, options, requireObject: true))
                throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, scope.OwnerId, "prepared-tool-arguments");
            return result;
        }
        finally { ReleaseAdmission(admission); }
    }

    public async ValueTask<JsonData> InvokeToolAsync(ExtensionRegistrySnapshot captured, string name, JsonData arguments,
        CancellationToken operationToken = default, CancellationToken sessionToken = default)
    {
        ValidateDispatchData(arguments, "dispatch-tool");
        var admission = Admit(captured, RegistrationKind.Tool, name, "dispatch-tool", operationToken, sessionToken);
        try
        {
            var (scope, entry) = admission.Single();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(operationToken, sessionToken, scope.ExtensionLifetimeCancellationToken);
            using var frame = new CallbackFrame(scope);
            await using var context = await CreateUiContextAsync(new ExtensionToolContext(scope, operationToken, sessionToken)).ConfigureAwait(false);
            var result = await ((ExtensionToolDescriptor)entry.Descriptor).ExecuteAsync(arguments,
                (IExtensionToolContext)context.Context, linked.Token).ConfigureAwait(false);
            if (!RegistrationPolicy.Json(result, options))
                throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, scope.OwnerId, "tool-result");
            return result;
        }
        finally { ReleaseAdmission(admission); }
    }

    /// <summary>Dispatch with a real host invocation identity and an actual awaited single-sink update receipt.</summary>
    public ValueTask<JsonData> InvokeToolAsync(ExtensionRegistrySnapshot captured, string name, JsonData arguments,
        string toolCallId, ExtensionToolUpdateCallback onUpdate,
        CancellationToken operationToken = default, CancellationToken sessionToken = default) =>
        InvokeToolInvocationCoreAsync(captured, name, arguments, toolCallId, onUpdate, operationToken, sessionToken, null);

    /// <summary>Host supplies a generation-bound normal-pipeline broker for this actual invocation.</summary>
    public ValueTask<JsonData> InvokeToolWithBrokerAsync(ExtensionRegistrySnapshot captured, string name, JsonData arguments,
        string toolCallId, ExtensionToolUpdateCallback onUpdate, ExtensionToolBroker broker,
        CancellationToken operationToken = default, CancellationToken sessionToken = default)
    {
        ArgumentNullException.ThrowIfNull(broker);
        if (broker.Tools.IsDefault || broker.SessionGeneration <= 0 || broker.CallDepth < 0 || broker.ExecuteToolAsync is null ||
            broker.ExecuteToolAsync.GetInvocationList().Length != 1)
            throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, "registry", "dispatch-tool-broker");
        return InvokeToolInvocationCoreAsync(captured, name, arguments, toolCallId, onUpdate, operationToken, sessionToken, broker);
    }

    private async ValueTask<JsonData> InvokeToolInvocationCoreAsync(ExtensionRegistrySnapshot captured, string name, JsonData arguments,
        string toolCallId, ExtensionToolUpdateCallback onUpdate, CancellationToken operationToken,
        CancellationToken sessionToken, ExtensionToolBroker? broker)
    {
        ArgumentNullException.ThrowIfNull(onUpdate);
        if (!ValidInvocationId(toolCallId) || onUpdate.GetInvocationList().Length != 1)
            throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, "registry", "dispatch-tool-invocation");
        ValidateDispatchData(arguments, "dispatch-tool");
        var admission = Admit(captured, RegistrationKind.Tool, name, "dispatch-tool", operationToken, sessionToken);
        try
        {
            var (scope, entry) = admission.Single();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(operationToken, sessionToken, scope.ExtensionLifetimeCancellationToken);
            using var frame = new CallbackFrame(scope);
            var invocation = new ExtensionToolInvocationContext(scope, operationToken, sessionToken, toolCallId, onUpdate,
                partial => RegistrationPolicy.Json(partial, options, requireObject: true), broker);
            await using var context = await CreateUiContextAsync(invocation).ConfigureAwait(false);
            JsonData? result = null; Exception? primary = null;
            try
            {
                try
                {
                    result = await ((ExtensionToolDescriptor)entry.Descriptor).ExecuteAsync(arguments,
                        (IExtensionToolContext)context.Context, linked.Token).ConfigureAwait(false);
                }
                finally { invocation.StopInvocationAdmission(); }
                if (!RegistrationPolicy.Json(result, options))
                    throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, scope.OwnerId, "tool-result");
            }
            catch (Exception error) { primary = error; }
            try { await invocation.CloseUpdatesAsync().ConfigureAwait(false); }
            catch (Exception error)
            {
                if (primary is OperationCanceledException && error is OperationCanceledException && linked.IsCancellationRequested)
                    throw new OperationCanceledException("Extension tool callback and update delivery were cancelled.", new AggregateException(primary, error), linked.Token);
                if (primary is not null && !ReferenceEquals(primary, error)) throw new AggregateException(primary, error);
                throw;
            }
            if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
            linked.Token.ThrowIfCancellationRequested();
            return result!;
        }
        finally { ReleaseAdmission(admission); }
    }

    private bool ValidInvocationId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > options.MaximumJsonCharacters || value.Contains('\0')) return false;
        for (var index = 0; index < value.Length; index++)
            if (char.IsHighSurrogate(value[index])) { if (++index >= value.Length || !char.IsLowSurrogate(value[index])) return false; }
            else if (char.IsLowSurrogate(value[index])) return false;
        return true;
    }

    public async ValueTask InvokeCommandAsync(ExtensionRegistrySnapshot captured, string name, JsonData arguments,
        CancellationToken operationToken = default, CancellationToken sessionToken = default)
    {
        ValidateDispatchData(arguments, "dispatch-command");
        var admission = Admit(captured, RegistrationKind.Command, name, "dispatch-command", operationToken, sessionToken);
        try
        {
            var (scope, entry) = admission.Single();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(operationToken, sessionToken, scope.ExtensionLifetimeCancellationToken);
            using var frame = new CallbackFrame(scope);
            await using var context = await CreateUiContextAsync(new ExtensionCommandContext(scope, operationToken, sessionToken, captured)).ConfigureAwait(false);
            await ((ExtensionCommandDescriptor)entry.Descriptor).ExecuteAsync(arguments,
                (IExtensionCommandContext)context.Context, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
        }
        finally { ReleaseAdmission(admission); }
    }

    /// <summary>Completion borrows the same owner admission and joins cleanup before returning its owned value.</summary>
    public async ValueTask<JsonData> CompleteCommandAsync(ExtensionRegistrySnapshot captured, string name, string prefix,
        CancellationToken operationToken = default, CancellationToken sessionToken = default)
    {
        if (prefix is null || prefix.Length > options.MaximumJsonCharacters ||
            !RegistrationPolicy.Scalars(prefix))
            throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, "registry", "complete-command");
        var admission = Admit(captured, RegistrationKind.Command, name, "complete-command", operationToken, sessionToken);
        try
        {
            var (scope, entry) = admission.Single();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(operationToken, sessionToken, scope.ExtensionLifetimeCancellationToken);
            using var frame = new CallbackFrame(scope);
            var callback = ((ExtensionCommandDescriptor)entry.Descriptor).GetArgumentCompletionsAsync;
            var result = callback is null ? JsonData.Null : await callback(prefix, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            if (!RegistrationPolicy.Json(result, options) || result.Value.ValueKind is not
                (System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Array))
                throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, scope.OwnerId, "command-completion");
            return result;
        }
        finally { ReleaseAdmission(admission); }
    }

    /// <summary>Source session_compact topic only: snapshot all handlers, ignore returns, sequentially join and report/continue.
    /// No event abort signal is delivered. Context/session/extension lifetime capabilities retain their separate native bounds.</summary>
    public async ValueTask DispatchSessionCompactAsync(ExtensionRegistrySnapshot captured, JsonData observation,
        Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? reportDiagnostic = null)
    {
        ValidateDispatchData(observation, "dispatch-session-compact");
        if (observation.Value.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !observation.Value.TryGetProperty("type", out var type) || type.ValueKind != System.Text.Json.JsonValueKind.String || type.GetString() != "session_compact")
            throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, "registry", "dispatch-session-compact");
        var admission = Admit(captured, RegistrationKind.Observation, "session_compact", "dispatch-session-compact",
            CancellationToken.None, CancellationToken.None);
        try
        {
            using var dispatchFrame = new CallbackFrame(admission.Select(item => item.Scope).Distinct().ToImmutableArray());
            foreach (var (scope, entry) in admission)
            {
                try
                {
                    using var frame = new CallbackFrame(scope);
                    await using var context = await CreateUiContextAsync(scope, CancellationToken.None, CancellationToken.None).ConfigureAwait(false);
                    await ((ExtensionObservationDescriptor)entry.Descriptor).ObserveAsync(observation, context.Context,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Topic-specific failure continuation, including OCE and original context cleanup failure.
                    // Diagnostic/report failure has no authority to veto the committed checkpoint or later handlers.
                    if (reportDiagnostic is not null)
                        try { await reportDiagnostic(new("session_compact", scope.OwnerId, scope.OwnerGeneration,
                            entry.RegistrationId, ExtensionEventFailure.HandlerFailed), CancellationToken.None).ConfigureAwait(false); }
                        catch (Exception) { }
                }
            }
        }
        finally { ReleaseAdmission(admission); }
    }
    /// <summary>Post-replacement observation; startup and generic observation dispatch remain separate.</summary>
    public async ValueTask DispatchReplacementSessionStartAsync(ExtensionRegistrySnapshot captured, JsonData observation,
        Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? reportDiagnostic = null)
    {
        ValidateDispatchData(observation, "dispatch-replacement-session-start");
        if (observation.Value.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !observation.Value.TryGetProperty("type", out var type) || type.ValueKind != System.Text.Json.JsonValueKind.String || type.GetString() != "session_start")
            throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, "registry", "dispatch-replacement-session-start");
        var admission = Admit(captured, RegistrationKind.Observation, "session_start", "dispatch-replacement-session-start",
            CancellationToken.None, CancellationToken.None);
        try
        {
            using var dispatchFrame = new CallbackFrame(admission.Select(item => item.Scope).Distinct().ToImmutableArray());
            foreach (var (scope, entry) in admission)
            {
                try
                {
                    using var frame = new CallbackFrame(scope);
                    await using var context = await CreateUiContextAsync(scope, CancellationToken.None, CancellationToken.None).ConfigureAwait(false);
                    await ((ExtensionObservationDescriptor)entry.Descriptor).ObserveAsync(observation, context.Context,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Topic-specific failure continuation, including OCE and original context cleanup failure.
                    // Diagnostic/report failure has no authority to veto the committed replacement or later handlers.
                    if (reportDiagnostic is not null)
                        try { await reportDiagnostic(new("session_start", scope.OwnerId, scope.OwnerGeneration,
                            entry.RegistrationId, ExtensionEventFailure.HandlerFailed), CancellationToken.None).ConfigureAwait(false); }
                        catch (Exception) { }
                }
            }
        }
        finally { ReleaseAdmission(admission); }
    }
    public async ValueTask DispatchObservationsAsync(ExtensionRegistrySnapshot captured, string topic, JsonData observation,
        CancellationToken operationToken = default, CancellationToken sessionToken = default)
    {
        ValidateDispatchData(observation, "dispatch-observation");
        var admission = Admit(captured, RegistrationKind.Observation, topic, "dispatch-observation", operationToken, sessionToken);
        try
        {
            // Admission captures every selected handler before the first callback. Removal cannot change this dispatch.
            using var dispatchFrame = new CallbackFrame(admission.Select(item => item.Scope).Distinct().ToImmutableArray());
            foreach (var (scope, entry) in admission)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(operationToken, sessionToken, scope.ExtensionLifetimeCancellationToken);
                using var frame = new CallbackFrame(scope);
                await using var context = await CreateUiContextAsync(scope, operationToken, sessionToken).ConfigureAwait(false);
                try { await ((ExtensionObservationDescriptor)entry.Descriptor).ObserveAsync(observation,
                    context.Context, linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException error) when (error.CancellationToken.CanBeCanceled && error.CancellationToken.IsCancellationRequested &&
                    (error.CancellationToken == linked.Token || error.CancellationToken == operationToken || error.CancellationToken == sessionToken ||
                     error.CancellationToken == scope.ExtensionLifetimeCancellationToken || error.CancellationToken == context.Context.SessionCancellationToken))
                {
                    // Preserve the known admission origin across this callback's private linked token.
                    // A foreign OCE is never classified by an unrelated concurrent host cancellation.
                    var origin = operationToken.IsCancellationRequested ? operationToken : sessionToken.IsCancellationRequested ? sessionToken :
                        scope.ExtensionLifetimeCancellationToken.IsCancellationRequested ? scope.ExtensionLifetimeCancellationToken : context.Context.SessionCancellationToken;
                    throw new OperationCanceledException("Observation admission was canceled.", error, origin);
                }
            }
        }
        finally { ReleaseAdmission(admission); }
    }

    private void ValidateDispatchData(JsonData value, string operation)
    {
        if (!RegistrationPolicy.Json(value, options))
            throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, "registry", operation);
    }

    internal ValueTask<ExtensionUiContextLease> CreateUiContextAsync(RegistrationScope scope, CancellationToken operation, CancellationToken session) =>
        CreateUiContextAsync(new ExtensionContext(scope, operation, session));

    private async ValueTask<ExtensionUiContextLease> CreateUiContextAsync(ExtensionContext context, IExtensionSessionActionScope? suppliedActions = null)
    {
        // Adopt supplied authority before every policy setup/validation so failure owns its cleanup.
        var acquired = suppliedActions;
        ExtensionUiContextLease lease;
        try
        {
            if (sessionProvider is IExtensionSessionToolActivationProvider)
                context.ConfigureToolActivation(names =>
                {
                    if (names.IsDefault || names.Length > 4096 ||
                        names.Any(name => !RegistrationPolicy.Identifier(name, options.MaximumIdentifierCharacters)) ||
                        names.Sum(name => (long)name.Length) > options.MaximumJsonCharacters)
                        throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, context.OwnerId, "set-active-tools");
                });
            if (sessionProvider is IExtensionSessionCompactionProvider)
                context.ConfigureCompaction(summary =>
                {
                    const string operation = "append-compaction-summary";
                    if (summary is null || summary.Text is null || !RegistrationPolicy.Scalars(summary.Text) ||
                        summary.FirstKeptEntryId is not null && !RegistrationPolicy.Scalars(summary.FirstKeptEntryId) ||
                        (long)summary.Text.Length + (summary.FirstKeptEntryId?.Length ?? 4) + (summary.Details?.ToString().Length ?? 4) > options.MaximumJsonCharacters ||
                        summary.Details is not null && !RegistrationPolicy.Json(summary.Details, options))
                        throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, context.OwnerId, operation);
                    if (summary.Usage is not null)
                    {
                        var wire = PiWireJson.WriteMessage(new("summary", "summary", "summary", 0, [], summary.Usage, StopReason.Stop));
                        if (!RegistrationPolicy.Json(wire, options)) throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, context.OwnerId, operation);
                    }
                }, (staged, token) =>
                {
                    token.ThrowIfCancellationRequested(); ThrowIfContextCancelled(context);
                    var owned = OwnSessionSnapshot(staged, context.OwnerId);
                    token.ThrowIfCancellationRequested(); ThrowIfContextCancelled(context); return owned;
                });
            if (sessionProvider is IExtensionSessionContextEditProvider)
                context.ConfigureContextEdits((target, replacement) =>
                {
                    const string operation = "append-context-edit";
                    if (target is null || target.Length > options.MaximumJsonCharacters || !RegistrationPolicy.Scalars(target) ||
                        replacement is not null && !RegistrationPolicy.Json(replacement, options) ||
                        (long)target.Length + (replacement?.ToString().Length ?? 4) > options.MaximumJsonCharacters)
                        throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, context.OwnerId, operation);
                }, (staged, token) =>
                {
                    token.ThrowIfCancellationRequested(); ThrowIfContextCancelled(context);
                    var owned = OwnSessionSnapshot(staged, context.OwnerId);
                    token.ThrowIfCancellationRequested(); ThrowIfContextCancelled(context);
                    return owned;
                });
            if (suppliedActions is not null)
            {
                context.SetSessionActions(suppliedActions);
                context.SetSessionSnapshot(OwnSessionSnapshot(suppliedActions.Snapshot, context.OwnerId));
            }
            else if (sessionProvider is not null)
            {
                ThrowIfContextCancelled(context);
                var captured = sessionProvider.Capture(context);
                ThrowIfContextCancelled(context);
                if (captured is not null)
                {
                    var snapshot = OwnSessionSnapshot(captured, context.OwnerId);
                    context.SetSessionSnapshot(snapshot);
                    if (sessionProvider is IExtensionSessionActionProvider actions)
                    {
                        acquired = actions.OpenScope(context, snapshot);
                        context.SetSessionActions(acquired);
                    }
                }
                ThrowIfContextCancelled(context);
            }
            lease = new(context, uiProvider);
        }
        catch
        {
            if (acquired is not null) await acquired.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        if (context is ExtensionCommandContext command)
        {
            command.ValidateCreatedSnapshot = (staged, token) =>
            {
                token.ThrowIfCancellationRequested(); ThrowIfContextCancelledForReplacement(command);
                // Reuse the exact eventual policy, including the configured per-record/depth/identifier
                // bounds and explicit trusted-host opaque-number capability. No limits are raised.
                _ = OwnSessionSnapshot(staged, command.OwnerId);
                token.ThrowIfCancellationRequested(); ThrowIfContextCancelledForReplacement(command);
                return ValueTask.CompletedTask;
            };
            command.CreateFreshContext = async actions =>
            {
                ExtensionCommandContext fresh;
                try
                {
                    ThrowIfContextCancelledForReplacement(command);
                    fresh = new(command.Scope, command.OperationCancellationToken,
                        command.BaseSessionCancellationToken, command.Captured);
                }
                catch { await actions.DisposeAsync().ConfigureAwait(false); throw; }
                var freshLease = await CreateUiContextAsync(fresh, actions).ConfigureAwait(false);
                try { lease.OwnReplacement(freshLease); }
                catch { await freshLease.DisposeAsync().ConfigureAwait(false); throw; }
                return fresh;
            };
        }
        return lease;
    }

    private static void ThrowIfContextCancelledForReplacement(ExtensionContext context)
    {
        context.OperationCancellationToken.ThrowIfCancellationRequested();
        context.BaseSessionCancellationToken.ThrowIfCancellationRequested();
        context.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Trusted host preflight for lifecycle callbacks on an actual staged target. Applies the
    /// same immutable view policy as callback admission; it grants no context or action authority.</summary>
    public void ValidateSessionSnapshot(ExtensionSessionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot); cancellationToken.ThrowIfCancellationRequested();
        _ = OwnSessionSnapshot(snapshot, "host"); cancellationToken.ThrowIfCancellationRequested();
    }
    private ExtensionSessionSnapshot OwnSessionSnapshot(ExtensionSessionSnapshot captured, string ownerId)
    {
        const string operation = "capture-session-snapshot";
        if (!RegistrationPolicy.Identifier(captured.SessionId, options.MaximumIdentifierCharacters) ||
            captured.Generation < 1 || captured.SelectedLeafId is not null &&
                !RegistrationPolicy.Identifier(captured.SelectedLeafId, options.MaximumIdentifierCharacters) ||
            captured.BranchEntries.IsDefault || !Enum.IsDefined(captured.Persistence))
            throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, ownerId, operation);
        if (captured.BranchEntries.Length > options.MaximumSessionBranchEntries)
            throw Failure(ExtensionRegistrationFailure.LimitExceeded, ownerId, operation);
        long characters = (long)captured.SessionId.Length + (captured.SelectedLeafId?.Length ?? 0);
        long bytes = Encoding.UTF8.GetByteCount(captured.SessionId) +
            (long)(captured.SelectedLeafId is null ? 0 : Encoding.UTF8.GetByteCount(captured.SelectedLeafId));
        var owned = ImmutableArray.CreateBuilder<JsonData>(captured.BranchEntries.Length);
        foreach (var entry in captured.BranchEntries)
        {
            if (entry is null)
                throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, ownerId, operation);
            var raw = entry.ToString();
            characters += raw.Length;
            bytes += Encoding.UTF8.GetByteCount(raw);
            if (raw.Length > options.MaximumJsonCharacters ||
                characters > options.MaximumSessionCharacters ||
                bytes > options.MaximumSessionUtf8Bytes)
                throw Failure(ExtensionRegistrationFailure.LimitExceeded, ownerId, operation);
            if (!RegistrationPolicy.Json(entry, options, requireObject: true,
                retainOpaqueNumbers: sessionProvider is IExtensionSessionOpaqueViewProvider))
                throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, ownerId, operation);
            // JsonData owns its document. Copying the array also severs a host's mutable backing-array alias.
            owned.Add(entry);
        }
        if (characters > options.MaximumSessionCharacters ||
            bytes > options.MaximumSessionUtf8Bytes)
            throw Failure(ExtensionRegistrationFailure.LimitExceeded, ownerId, operation);
        return new(captured.SessionId, captured.Generation, captured.SelectedLeafId, owned.MoveToImmutable())
            { Persistence = captured.Persistence };
    }

    private static void ThrowIfContextCancelled(ExtensionContext context)
    {
        context.OperationCancellationToken.ThrowIfCancellationRequested();
        context.SessionCancellationToken.ThrowIfCancellationRequested();
        context.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
    }

    internal ImmutableArray<(RegistrationScope Scope, RegistrationEntry Entry)> AdmitCurrent(
        RegistrationKind kind, string name, string operation, CancellationToken operationToken, CancellationToken sessionToken,
        int maximumSelected, out ExtensionRegistrySnapshot captured)
    {
        lock (gate)
        {
            captured = snapshot;
            return Admit(captured, kind, name, operation, operationToken, sessionToken, maximumSelected);
        }
    }

    internal ImmutableArray<(RegistrationScope Scope, RegistrationEntry Entry)> Admit(ExtensionRegistrySnapshot captured,
        RegistrationKind kind, string name, string operation, CancellationToken operationToken, CancellationToken sessionToken,
        int maximumSelected = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(captured);
        if (!(kind == RegistrationKind.Command && options.AllowAnyCommandName ? RegistrationPolicy.CommandName(name, options.MaximumIdentifierCharacters)
                : RegistrationPolicy.Identifier(name, options.MaximumIdentifierCharacters)))
            throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, "registry", operation);
        operationToken.ThrowIfCancellationRequested();
        sessionToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (closing) throw Failure(ExtensionRegistrationFailure.InactiveScope, "registry", operation);
            if (!ReferenceEquals(captured.RegistryIdentity, identity))
                throw Failure(ExtensionRegistrationFailure.StaleSnapshot, "registry", operation);
            if (options.FollowCurrentSnapshot) captured = snapshot;
            var selected = captured.Entries.Where(entry => entry.Kind == kind && captured.NameOf(entry) == name).ToArray();
            if ((kind is RegistrationKind.Tool or RegistrationKind.Command) && selected.Length != 1)
                throw Failure(ExtensionRegistrationFailure.StaleSnapshot, "registry", operation);
            if (selected.Length > maximumSelected)
                throw Failure(ExtensionRegistrationFailure.LimitExceeded, "registry", operation);
            if (dispatches >= options.MaximumConcurrentDispatches)
                throw Failure(ExtensionRegistrationFailure.LimitExceeded, "registry", operation);
            var builder = ImmutableArray.CreateBuilder<(RegistrationScope, RegistrationEntry)>(selected.Length);
            foreach (var entry in selected)
            {
                if (!owners.TryGetValue(entry.OwnerId, out var scope) || scope.OwnerGeneration != entry.OwnerGeneration ||
                    scope.State != RegistrationScopeState.Active || !scope.Staged.Contains(entry))
                    throw Failure(ExtensionRegistrationFailure.StaleSnapshot, entry.OwnerId, operation);
                builder.Add((scope, entry));
            }
            // All checks finish before any lease is admitted.
            var admitted = builder.MoveToImmutable();
            dispatches++;
            foreach (var (scope, entry) in admitted)
            {
                if (scope.ActiveCallbacks++ == 0) scope.Idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
                entry.Leases++;
            }
            return admitted;
        }
    }

    internal void ReleaseAdmission(ImmutableArray<(RegistrationScope Scope, RegistrationEntry Entry)> admitted)
    {
        lock (gate)
        {
            foreach (var (scope, entry) in admitted)
            {
                entry.Leases--;
                ReleaseChargeIfRetired(scope, entry);
                if (--scope.ActiveCallbacks == 0) scope.Idle!.TrySetResult();
            }
            dispatches--;
        }
    }

    internal ValueTask DisposeScope(RegistrationScope scope)
    {
        TaskCompletionSource settlement;
        bool start;
        lock (gate)
        {
            (settlement, start) = CloseScope(scope);
            if (start) Publish();
        }
        if (start) scope.StartCleanup(settlement);
        return new(settlement.Task);
    }

    internal async ValueTask<RegistrationQuiescenceLease> QuiesceScopeAsync(RegistrationScope scope,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
            scope.ExtensionLifetimeCancellationToken);
        var pause = new object();
        Task drain;
        lock (gate)
        {
            EnsureScope(scope, "quiesce");
            linked.Token.ThrowIfCancellationRequested();
            scope.QuiescenceIdentity = pause;
            scope.State = RegistrationScopeState.Quiescing;
            // Keep descriptors and their owner/name/resource reservations. Old snapshots are rejected
            // by Admit's live state check; fresh snapshots omit this owner for the whole pause.
            Publish();
            drain = scope.ActiveCallbacks == 0 ? Task.CompletedTask : scope.Idle!.Task;
        }
        try
        {
            // Cancellation cancels this wait, not the actual callback task or its registry ownership.
            await drain.WaitAsync(linked.Token).ConfigureAwait(false);
            lock (gate)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (closing || scope.State != RegistrationScopeState.Quiescing ||
                    !ReferenceEquals(scope.QuiescenceIdentity, pause))
                    throw Failure(ExtensionRegistrationFailure.InactiveScope, scope.OwnerId, "quiesce");
                scope.State = RegistrationScopeState.Quiescent;
                return new(scope, pause);
            }
        }
        catch
        {
            ResumeScope(scope, pause);
            throw;
        }
    }

    internal void ResumeScope(RegistrationScope scope, object pause)
    {
        lock (gate)
        {
            if (!ReferenceEquals(scope.QuiescenceIdentity, pause)) return;
            scope.QuiescenceIdentity = null;
            if (closing || scope.State is not (RegistrationScopeState.Quiescing or RegistrationScopeState.Quiescent)) return;
            scope.State = RegistrationScopeState.Active;
            Publish();
        }
    }

    private (TaskCompletionSource Settlement, bool Start) CloseScope(RegistrationScope scope)
    {
        if (scope.Disposal is not null) return (scope.Disposal, false);
        scope.Disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Closing is irreversible. Neither a cancelled pause nor a retained old lease may resume it.
        scope.QuiescenceIdentity = null;
        scope.State = RegistrationScopeState.Closing;
        foreach (var entry in scope.Staged.Entries.ToArray())
        {
            scope.Staged.Remove(entry);
            entry.Registered = false;
            RetireEventBusSubscription(entry);
            ReleaseChargeIfRetired(scope, entry);
        }
        return (scope.Disposal, true);
    }

    internal Task WaitForCallbacks(RegistrationScope scope)
    {
        lock (gate) return scope.ActiveCallbacks == 0 ? Task.CompletedTask : scope.Idle!.Task;
    }

    internal void FinishCleanup(RegistrationScope scope, bool shutdownCompleted)
    {
        lock (gate)
        {
            // A failed plugin shutdown does not establish safe replacement. Retain its bounded owner reservation.
            if (shutdownCompleted && owners.TryGetValue(scope.OwnerId, out var current) && ReferenceEquals(current, scope))
            {
                owners.Remove(scope.OwnerId);
                ownerOrder.Remove(scope);
                metadataCharacters -= scope.OwnerId.Length;
            }
            scope.State = RegistrationScopeState.Disposed;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (CallbackFrame.IsExecuting(this))
            throw Failure(ExtensionRegistrationFailure.ReentrantDisposal, "registry", "dispose");
        TaskCompletionSource settlement;
        List<(RegistrationScope Scope, TaskCompletionSource Settlement, bool Start)>? scopes = null;
        lock (gate)
        {
            if (disposal is null)
            {
                closing = true;
                disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
                scopes = ownerOrder.Select(scope =>
                {
                    var close = CloseScope(scope);
                    return (scope, close.Settlement, close.Start);
                }).ToList();
                Publish();
            }
            settlement = disposal;
        }
        if (scopes is not null) _ = CleanupAllAsync(scopes, settlement);
        return new(settlement.Task);
    }

    private static async Task CleanupAllAsync(
        List<(RegistrationScope Scope, TaskCompletionSource Settlement, bool Start)> scopes, TaskCompletionSource settlement)
    {
        foreach (var (scope, completion, start) in scopes)
            if (start) scope.StartCleanup(completion);
        try
        {
            await Task.WhenAll(scopes.Select(item => item.Settlement.Task)).ConfigureAwait(false);
            settlement.TrySetResult();
        }
        catch
        {
            var errors = scopes.Select(item => item.Settlement.Task.Exception).Where(error => error is not null).Cast<Exception>();
            settlement.TrySetException(new ExtensionRegistrationException(ExtensionRegistrationFailure.CleanupFailed,
                "registry", "dispose", new AggregateException(errors)));
        }
    }

    private static ExtensionRegistrationException Failure(ExtensionRegistrationFailure failure, string owner, string operation) =>
        new(failure, owner, operation);
}
