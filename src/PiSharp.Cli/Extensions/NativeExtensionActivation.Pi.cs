// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session.ts (bindExtensions: the session binds
// the loaded extensions' runner with its actions) and packages/coding-agent/src/core/extensions/runner.ts (bindCore).
using System.Collections.Immutable;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Cli.Extensions.Pi;
using PiSharp.Extensions;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Facade.Context;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Extensions;

internal sealed partial class NativeExtensionActivation
{
    /// <summary>The Pi extensions (TypeScript/JavaScript in the Node host) this activation binds, or null for a published native
    /// extension generation.</summary>
    internal PiExtensionHost? Pi { get; private init; }

    /// <summary>Binds the extensions a Pi-style run loaded (<see cref="PiExtensionHost"/>) to a session: one registry owner per
    /// extension, in load order, with every tool and command enabled as upstream enables them. Upstream has no approval step for
    /// extensions; the run's discovery (settings, packages, project trust) is the admission.</summary>
    internal static async Task<NativeExtensionActivation> LoadPiAsync(PiExtensionHost pi, CancellationToken token,
        IExtensionUiProvider? uiProvider = null, Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? reportInputDiagnostic = null,
        PiSharp.Cli.Mcp.McpRegisteredServers? mcpServers = null)
    {
        ArgumentNullException.ThrowIfNull(pi);
        var sessionViews = new NativeSessionSnapshotProvider();
        var facadeHost = new NativeExtensionContextFacadeHost();
        NativeExtensionActivation? admittedActivation = null;
        var registrationActions = new NativeExistingSessionRegistrationActions(facadeHost,
            () => admittedActivation?.InputAdmission ?? throw new NotSupportedException("The native input pipeline has not been bound."))
        // agent-session.ts sendUserMessage -> prompt: no text or image-count bound beyond the request-entry memory bounds.
        { InputLimits = PiSharp.Cli.Commands.PiPayloadBudget.PiExtensionInput };
        IExtensionContextReadHost facadeCapabilities = new NativeExtensionRegistrationFacadeHost(facadeHost, registrationActions);
        var uiPrompts = uiProvider is null ? null : new NativeUiPromptEvents(uiProvider);
        var registry = new ExtensionRegistry(PiExtensionHost.RegistryOptions, uiPrompts ?? (IExtensionUiProvider)new UnavailableExtensionUiProvider(),
            sessionViews, facadeCapabilities);
        if (mcpServers is not null) { registry.McpServerHost = mcpServers; mcpServers.OwnerPath = pi.PathOfOwner; }
        // registerProvider with classifiers or images from native extensions; the run's model registry follows them.
        registry.ModelOperationProviderHost = pi.NativeModelProviders;
        if (pi.RunModelOperations is { } runModels) facadeHost.BindRunModelOperations(runModels);
        try
        {
            await pi.ActivateAsync(registry, token, session: true).ConfigureAwait(false);
            var snapshot = registry.CaptureSnapshot();
            var configuration = new NativeExtensionConfiguration(pi.BridgeDirectory, pi.BridgeDirectory, pi.BridgeDirectory, pi.BridgeDirectory,
                [.. snapshot.Tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal)])
            { EnabledCommands = [.. snapshot.Commands.Select(command => command.Name).Distinct(StringComparer.Ordinal)] };
            token.ThrowIfCancellationRequested();
            admittedActivation = new(configuration, registry, null, snapshot, ImmutableDictionary<string, NativeToolObjectSchema>.Empty, sessionViews,
                reportInputDiagnostic, facadeHost) { UiPrompts = uiPrompts, McpServers = mcpServers, Pi = pi };
            pi.BindActivation(admittedActivation, registry, registrationActions, facadeHost);
            return admittedActivation;
        }
        catch (Exception original)
        {
            var failures = new List<Exception>();
            await CollectNativeCleanupAsync(registry.DisposeAsync, failures).ConfigureAwait(false);
            if (failures.Count > 0) throw new NativeExtensionException(NativeExtensionFailure.CleanupFailed, new AggregateException(failures.Prepend(original)));
            throw;
        }
    }

    private IToolActionPolicy? _boundPolicy;
    private ToolInvokerOptions? _boundLimits;
    private readonly SemaphoreSlim _piToolPublications = new(1, 1);

    /// <summary>An extension tool action's target is the current registration of a Pi extension tool with that name.</summary>
    internal bool IsCurrentToolTarget(string name, string target) => _registry.CaptureSnapshot().Tools.Any(tool => tool.Name == name &&
        (tool.OwnerId.StartsWith("pi-extension-", StringComparison.Ordinal) || tool.OwnerId.StartsWith("pi-native-", StringComparison.Ordinal)) &&
        tool.OwnerId + "/" + tool.OwnerGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + tool.RegistrationId == target);

    /// <summary>runtime.refreshTools(): replaces one Pi extension's tools in the live session's catalog (a registerTool after the factory
    /// returned, or a reload). The previous registrations of the owner leave the session's catalog; new direct tools are activated, as
    /// on registration. A run in progress takes the catalog at its next request.</summary>
    internal async Task PublishPiToolsAsync(PiSharp.CodingAgent.ReplaceableAgentSession owner, RegistrationScope scope, ImmutableArray<string> previousIds,
        ImmutableHashSet<string> previousNames, ImmutableArray<ExtensionToolDescriptor> descriptors, CancellationToken token)
    {
        var policy = _boundPolicy ?? throw new InvalidOperationException("The Pi extensions are not bound to a session yet.");
        await _piToolPublications.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var current = owner.Current;
            ValueTask<PiSharp.CodingAgent.PreparedSessionToolCatalog> Prepare(PiSharp.CodingAgent.SessionRuntimeRegistry expected, ImmutableArray<string> activeNames,
                CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested();
                var plan = _registry.PrepareToolCatalogReplacement(scope, previousIds, descriptors, _registry.CaptureSnapshot());
                var binding = new PiSharp.Extensions.Agent.ExtensionAgentBinding(_registry, policy, (_, _, _) => ValueTask.FromResult(true),
                    invokerOptions: _boundLimits, sessionCancellationToken: current.LifetimeToken,
                    options: PiSharp.Cli.Commands.PiPayloadBudget.PiBinding(new() { ActiveToolNames = [] }),
                    capturedSnapshot: plan.PreviewSnapshot);
                var nextNames = descriptors.Select(tool => tool.Name).ToImmutableHashSet(StringComparer.Ordinal);
                var retained = expected.RegisteredTools.Where(tool => !previousNames.Contains(tool.Adapter.Name) && !nextNames.Contains(tool.Adapter.Name)).ToImmutableArray();
                var added = binding.Registrations.Where(tool => nextNames.Contains(tool.Name) && tool.OwnerId == scope.OwnerId).Select(tool =>
                {
                    var index = binding.Registrations.IndexOf(tool);
                    return new PiSharp.CodingAgent.SessionRegisteredTool(binding.RegisteredToolDeclarations[index], binding.Adapters[index],
                        tool.SequentialExecution ? PiSharp.Agent.ToolExecutionMode.Sequential : PiSharp.Agent.ToolExecutionMode.Parallel)
                    {
                        Exposure = tool.Exposure, Namespace = tool.Namespace, DefaultActive = tool.DefaultActive, IsExtension = true, Annotations = tool.Annotations,
                        PromptGuidelines = tool.PromptGuidelines, PromptSnippet = tool.PromptSnippet, OutputSchema = tool.OutputSchema,
                        PrepareLoadout = binding.GetLoadoutPreparation(tool.Name)
                    };
                }).ToImmutableArray();
                var replacement = expected.WithToolCatalog(retained.AddRange(added), expected.PreparedToolHooks);
                var available = replacement.RegisteredTools.Select(tool => tool.Adapter.Name).ToImmutableHashSet(StringComparer.Ordinal);
                var active = activeNames.Where(available.Contains)
                    .Concat(added.Where(tool => !previousNames.Contains(tool.Adapter.Name) && ToolExposureSemantics.ActivatesOnRegistration(tool.Exposure, tool.DefaultActive))
                        .Select(tool => tool.Adapter.Name))
                    .Distinct(StringComparer.Ordinal).ToImmutableArray();
                return ValueTask.FromResult(new PiSharp.CodingAgent.PreparedSessionToolCatalog(replacement, active, () => plan.Commit()));
            }
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await owner.PrepareAndPublishToolCatalogAsync(current, (expected, cancellation) => Prepare(expected, current.Session.GetActiveTools(), cancellation), token)
                        .ConfigureAwait(false);
                    return;
                }
                // A run can start between the idle wait and the publication; nothing was committed, so publish again.
                catch (InvalidOperationException) when (attempt < 64 && !token.IsCancellationRequested && ReferenceEquals(owner.Current, current)) { }
            }
        }
        finally { _piToolPublications.Release(); }
    }

    /// <summary>The captured registry snapshot of the bound generation (Pi extension host reads, such as getCommands).</summary>
    internal ExtensionRegistrySnapshot? CurrentSnapshot => Binding?.Snapshot;
    internal ExtensionRegistry Registry => _registry;
    internal CancellationToken ClosingToken => _closing.Token;
}
