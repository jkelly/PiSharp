using PiSharp.Cli.Mcp;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Registration;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Extensions;

/// <summary>Explicit profile-owned admissions. No initializer capabilities are inferred from disk.</summary>
internal sealed record NativeExtensionInitializerInstallation(
    Func<ExtensionRegistry, NativeExtensionRegistrationBridge> CreateRegistrationBridge,
    Action<IPiSharpExtension, NativeExtensionRegistrationFacade> BindRegistrations,
    Func<ExtensionRegistry, McpExtensionRegistrationBridge>? CreateMcpBridge = null,
    Action<IMcpServerRegistrationFacade>? BindMcp = null)
{
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(CreateRegistrationBridge); ArgumentNullException.ThrowIfNull(BindRegistrations);
        if (CreateRegistrationBridge.GetInvocationList().Length != 1 || BindRegistrations.GetInvocationList().Length != 1 ||
            (CreateMcpBridge is null) != (BindMcp is null) || BindMcp?.GetInvocationList().Length > 1 || CreateMcpBridge?.GetInvocationList().Length > 1)
            throw new ArgumentException("One paired caller-admitted initializer installation required.");
    }
}

internal sealed partial class NativeExtensionActivation
{
    internal NativeExtensionRegistrationBridge? RegistrationInstallation => _registrationBridge;
    private static async Task<RegistrationScope> ActivateConfiguredOwnerAsync(NativeExtensionInitializerInstallation installation,
        NativeExtensionRegistrationBridge bridge, McpExtensionRegistrationBridge? mcpBridge, string admittedExtensionPath, string ownerId,
        IPiSharpExtension originalExtension, CancellationToken token)
    {
        McpPreparedNativeInitialization? prepared = null;
        Task<NativeInitializedRegistrationOwner>? activation = null;
        Task<McpInitializedRegistrationOwner>? publication = null;
        RegistrationScope? installedScope = null;
        try
        {
            prepared = mcpBridge?.PrepareNativeInitialization(originalExtension, admittedExtensionPath, installation.BindMcp!);
            activation = bridge.ActivateOwnerAsync(ownerId, prepared?.DecoratedExtension ?? originalExtension,
                facade => installation.BindRegistrations(originalExtension, facade), token);
            var installed = await activation.ConfigureAwait(false);
            installedScope = installed.Scope;
            if (prepared is not null)
            {
                publication = prepared.CommitAsync(installed.Scope, installed.ActivationOriginal, token);
                await publication.ConfigureAwait(false);
            }
            return installed.Scope;
        }
        catch (Exception error)
        {
            var failures = new List<Exception> { error };
            if (activation?.Exception is { } activationFault) failures.Add(activationFault);
            if (publication?.Exception is { } publicationFault) failures.Add(publicationFault);
            try { prepared?.Abort(); } catch (Exception abortFault) { failures.Add(abortFault); }
            if (installedScope is not null)
                await CollectNativeCleanupAsync(() => new ValueTask(RetireConfiguredOwnerAsync(bridge, mcpBridge, installedScope)), failures).ConfigureAwait(false);
            if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            throw new AggregateException("Single native initializer and MCP publication originals.", failures);
        }
    }
    private static async Task RetireConfiguredOwnerAsync(NativeExtensionRegistrationBridge bridge,
        McpExtensionRegistrationBridge? mcpBridge, RegistrationScope scope)
    {
        var failures = new List<Exception>();
        await CollectNativeCleanupAsync(() => new ValueTask(bridge.RetireOwnerAsync(scope)), failures).ConfigureAwait(false);
        Task? scopeOriginal = null;
        try { scopeOriginal = scope.DisposeAsync().AsTask(); await scopeOriginal.ConfigureAwait(false); }
        catch (Exception error) { if (scopeOriginal?.Exception is { } aggregate) failures.Add(aggregate); failures.Add(error); }
        if (mcpBridge is not null && scopeOriginal is not null)
            await CollectNativeCleanupAsync(() => new ValueTask(mcpBridge.RetireClosedOwnerAsync(scope, scopeOriginal)), failures).ConfigureAwait(false);
        if (failures.Count != 0) throw new AggregateException("Native/catalog/MCP retirement originals.", failures);
    }
    private static async Task CollectNativeCleanupAsync(Func<ValueTask> invoke, List<Exception> failures)
    {
        Task? original = null;
        try { original = invoke().AsTask(); await original.ConfigureAwait(false); }
        catch (Exception error) { if (original?.Exception is { } aggregate) failures.Add(aggregate); failures.Add(error); }
    }
}
