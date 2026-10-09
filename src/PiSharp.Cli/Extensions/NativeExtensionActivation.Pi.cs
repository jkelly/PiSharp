// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session.ts (bindExtensions: the session binds
// the loaded extensions' runner with its actions) and packages/coding-agent/src/core/extensions/runner.ts (bindCore).
using System.Collections.Immutable;
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
            () => admittedActivation?.InputAdmission ?? throw new NotSupportedException("The native input pipeline has not been bound."));
        IExtensionContextReadHost facadeCapabilities = new NativeExtensionRegistrationFacadeHost(facadeHost, registrationActions);
        var uiPrompts = uiProvider is null ? null : new NativeUiPromptEvents(uiProvider);
        var registry = new ExtensionRegistry(PiExtensionHost.RegistryOptions, uiPrompts ?? (IExtensionUiProvider)new UnavailableExtensionUiProvider(),
            sessionViews, facadeCapabilities);
        if (mcpServers is not null) { registry.McpServerHost = mcpServers; mcpServers.OwnerPath = pi.PathOfOwner; }
        try
        {
            await pi.ActivateAsync(registry, token).ConfigureAwait(false);
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

    /// <summary>The captured registry snapshot of the bound generation (Pi extension host reads, such as getCommands).</summary>
    internal ExtensionRegistrySnapshot? CurrentSnapshot => Binding?.Snapshot;
    internal ExtensionRegistry Registry => _registry;
    internal CancellationToken ClosingToken => _closing.Token;
}
