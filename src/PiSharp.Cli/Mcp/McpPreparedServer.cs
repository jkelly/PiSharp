using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Resources;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Mcp;

namespace PiSharp.Cli.Mcp;

/// <summary>Host-owned replacement of prepared hooks, preserving unrelated native hooks and replacing retired registry hooks.</summary>
public delegate IPreparedToolHooks? McpPreparedHookComposer(SessionRuntimeRegistry current, ExtensionAgentBinding replacementBinding);

/// <summary>One explicitly admitted server bound to one actual session attachment and registry scope.
/// Initializes/list/calls through the supplied channel factory; publishes through the native prepared
/// tool pipeline and durable session catalog boundary. It acquires no process, client or credentials.
/// Registers its close body with the captured owning attachment; replacement and shutdown join withdrawal before retirement.</summary>
public sealed class McpPreparedServer : IAsyncDisposable
{
    private readonly ExtensionRegistry registry;
    private readonly RegistrationScope scope;
    private readonly ReplaceableAgentSession owner;
    private readonly AgentSessionAttachment attachment;
    private readonly IToolActionPolicy policy;
    private readonly ExtensionToolArgumentValidator validator;
    private readonly McpPreparedHookComposer composeHooks;
    private readonly McpServerRuntime runtime;
    private McpServerEntry serverEntry;
    private readonly SemaphoreSlim publications = new(1, 1);
    private readonly AsyncLocal<bool> inside = new();
    private readonly object admission = new();
    private readonly HashSet<Task> admitted = [];
    private readonly ReplaceableAgentSession.OwnedResourceLease resource;
    private bool closeRequested;
    private bool withdrawalAcknowledged;
    private ImmutableArray<string> registrationIds = [];
    private ImmutableHashSet<string> publishedNames = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
    /// <summary>Published names registered `direct`: a tool that becomes direct is activated, as the original activates direct tools on registration.</summary>
    private ImmutableHashSet<string> publishedDirect = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
    private long publishedRevision;
    private Exception? publicationFailure;

    public McpPreparedServer(McpServerEntry entry, ExtensionRegistry registry, RegistrationScope scope,
        ReplaceableAgentSession owner, IToolActionPolicy policy, ExtensionToolArgumentValidator validator,
        McpAdmittedChannelFactory channelFactory, McpRuntimeOptions options, McpPreparedHookComposer composeHooks)
    {
        ArgumentNullException.ThrowIfNull(entry); ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(scope); ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(policy); ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(channelFactory); ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(composeHooks);
        this.registry = registry; this.scope = scope; this.owner = owner; this.policy = policy; this.validator = validator; this.composeHooks = composeHooks;
        attachment = owner.Current;
        var captured = attachment.Session.CaptureToolCatalogRegistry();
        if (options.Generation != attachment.Generation || captured.InvocationOwnerGeneration != attachment.Generation ||
            !captured.UsesFinalActionPolicy(policy))
            throw new ArgumentException("MCP binding requires the captured attachment generation and native final-action policy.");
        runtime = new(entry, options, channelFactory, PublishAsync); serverEntry = entry;
        resource = owner.RegisterOwnedResource(attachment, CloseOwnedAsync, runtime.CloseAsync);
    }

    public Task<McpRuntimeSnapshot> ConnectAsync(CancellationToken token = default)
        => RunAsync(() => runtime.ConnectAsync(token), token);
    public Task<McpRuntimeSnapshot> RefreshToolsAsync(CancellationToken token = default)
        => RunAsync(() => runtime.RefreshToolsAsync(token), token);
    /// <summary>Drops the connection and connects again, republishing the tools (`/mcp` reconnect, after a sign-in).</summary>
    public Task<McpRuntimeSnapshot> ReconnectAsync(CancellationToken token = default)
        => RunAsync(() => runtime.ReconnectAsync(token), token);
    /// <summary>Drops the connection without reconnecting (sign-out); the tools stay registered and the next call reconnects.</summary>
    public Task DisconnectAsync(CancellationToken token = default)
        => RunAsync(async () => { await runtime.DisconnectAsync(token).ConfigureAwait(false); return runtime.Snapshot; }, token);
    /// <summary>index.ts setEnabled and setExposure without a new connection or lease: a disabled server disconnects and withdraws its
    /// tools; an enabled one registers its tools again with the new exposures when connected, and connects when
    /// <paramref name="connect"/> (re-enable) and it is not.</summary>
    public Task<McpRuntimeSnapshot> ReconfigureAsync(McpServerEntry next, bool connect, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(next);
        return RunAsync(async () =>
        {
            var previous = serverEntry; serverEntry = next;
            try { return await runtime.ReconfigureAsync(next, connect, token).ConfigureAwait(false); }
            catch (ArgumentException) { serverEntry = previous; throw; }
        }, token);
    }
    /// <summary>The server's configuration as the session uses it.</summary>
    public McpServerEntry Entry => serverEntry;
    /// <summary>runtime.ts withClient markNeedsAuth: called with a call's failure; the host marks a server that needs a sign-in.</summary>
    public Action<Exception>? CallFailed { get; init; }
    /// <summary>How many resources and templates the server lists (fetchResources), for `/mcp`.</summary>
    public async Task<(int Resources, int Templates)> CountResourcesAsync(CancellationToken token = default)
    {
        var counts = (0, 0);
        await RunAsync(async () => { counts = await runtime.CountResourcesAsync(token).ConfigureAwait(false); return runtime.Snapshot; }, token).ConfigureAwait(false);
        return counts;
    }
    /// <summary>The runtime's current catalog state (connected, tools, instructions, resources).</summary>
    public McpRuntimeSnapshot Snapshot => runtime.Snapshot;
    /// <summary>Called by the actual prepared resource dispatch after its resource-scope admission.
    /// The resource scope can differ from this server's tool scope; both belong to the captured attachment.</summary>
    public McpResourceServer CaptureResourceServer(IExtensionToolInvocationContext invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation); RefuseReentry(); ValidateAttachment();
        scope.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
        if (invocation.SessionGeneration != attachment.Generation || string.IsNullOrWhiteSpace(invocation.OwnerId) ||
            invocation.OwnerGeneration <= 0 || string.IsNullOrWhiteSpace(invocation.ToolCallId))
            throw new InvalidOperationException("Resource capture requires the actual prepared attachment invocation.");
        lock (admission)
        {
            if (closeRequested) throw new ObjectDisposedException(nameof(McpPreparedServer));
            if (runtime.Snapshot.Generation != attachment.Generation) throw new InvalidOperationException("Resource runtime generation differs from its owning attachment.");
            return runtime.CaptureResourceServer(invocation);
        }
    }
    public bool CatalogWithdrawalAcknowledged { get { lock (admission) return withdrawalAcknowledged; } }
    /// <summary>Whether closing with the owner's shutdown records the withdrawal of this server's tools in the session. The
    /// original records nothing at session_shutdown (production sessions set false), so a resumed session's loadout still names
    /// the tools and declares them again once the server connects.</summary>
    public bool DurableWithdrawalOnShutdown { get; init; } = true;
    /// <summary>Whether calls return tools.ts convertMcpResult's tool result (model content, details, the CallToolResult as
    /// structuredContent, isError) instead of the raw CallToolResult. Production sessions set it.</summary>
    public bool ConvertResults { get; init; }
    public Task CloseAsync()
    {
        RefuseReentry();
        lock (admission)
        {
            var original = resource.CloseAsync();
            closeRequested = true;
            return original;
        }
    }
    public ValueTask DisposeAsync() => new(CloseAsync());

    private async Task<McpRuntimeSnapshot> RunAsync(Func<Task<McpRuntimeSnapshot>> run, CancellationToken token)
    {
        RefuseReentry(); ValidateAttachment(); token.ThrowIfCancellationRequested();
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (admission)
        {
            if (closeRequested) throw new ObjectDisposedException(nameof(McpPreparedServer));
            admitted.Add(settled.Task);
        }
        try { return await run().ConfigureAwait(false); }
        finally { lock (admission) admitted.Remove(settled.Task); settled.TrySetResult(); }
    }

    private Task CloseOwnedAsync(ReplaceableAgentSession.OwnedResourceRetirementTransaction transaction)
    {
        Task[] originals;
        lock (admission) { closeRequested = true; originals = admitted.ToArray(); }
        return CloseCoreAsync(originals, transaction);
    }

    private async Task CloseCoreAsync(Task[] originals, ReplaceableAgentSession.OwnedResourceRetirementTransaction transaction)
    {
        var failures = new List<Exception>();
        var prior = inside.Value; inside.Value = true;
        try
        {
            // The owning stop phase already initiated and joined the actual runtime close before this reservation.
            // Its original failure is retained by the resource owner; do not count it again in withdrawal.
            await Task.WhenAll(originals).ConfigureAwait(false);
            // index.ts session_shutdown records nothing: the tools leave with the session. Only a close while the session
            // continues (disable, reconnect with a new configuration, reload) withdraws them durably.
            var withdrew = registrationIds.IsEmpty || transaction.IsSessionShutdown && !DurableWithdrawalOnShutdown;
            // Withdrawal is host-owned; runtime close only marks its borrowed metadata disconnected.
            // Last committed ownership is retained until the same durable catalog boundary acknowledges.
            if (!withdrew)
            {
                try
                {
                    var current = runtime.Snapshot;
                    var next = current with { Revision = checked(publishedRevision + 1), OriginalTools = [],
                        Catalog = current.Catalog with { Tools = [], Connected = false } };
                    await PublishCoreAsync(new(next, [], []), CancellationToken.None, withdrawal: true, transaction).ConfigureAwait(false);
                    withdrew = true;
                }
                catch (Exception error) { failures.Add(error); }
            }
            if (withdrew) { lock (admission) withdrawalAcknowledged = true; }
            if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1) throw new AggregateException(failures);

        }
        finally { inside.Value = prior; }
    }

    private void RefuseReentry()
    { if (inside.Value) throw new InvalidOperationException("An MCP callback cannot await its owning catalog/server operation."); }
    private void ValidateAttachment()
    {
        attachment.LifetimeToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(owner.Current, attachment)) throw new InvalidOperationException("MCP server belongs to a retired session attachment.");
    }

    private ValueTask<McpCatalogPublicationReceipt> PublishAsync(McpCatalogPublication publication, CancellationToken token)
        => PublishCoreAsync(publication, token, withdrawal: false);

    private async ValueTask<McpCatalogPublicationReceipt> PublishCoreAsync(McpCatalogPublication publication, CancellationToken token,
        bool withdrawal, ReplaceableAgentSession.OwnedResourceRetirementTransaction? transaction = null)
    {
        // Private withdrawal runs inside the owning close guard. External/runtime publications
        // still refuse owning callback reentry; this branch cannot be requested by a caller.
        if (!withdrawal) RefuseReentry();
        await publications.WaitAsync(token).ConfigureAwait(false);
        var prior = inside.Value; inside.Value = true;
        try
        {
            ValidateAttachment(); token.ThrowIfCancellationRequested();
            // Close never replays a failed incoming catalog. It removes only the captured, actually
            // committed subset after joining that operation; a faulted/retired session still refuses it.
            if (!withdrawal && publicationFailure is not null) throw new InvalidOperationException("Previous publication needs owning-host resolution.", publicationFailure);
            if (publication.Current.Generation != attachment.Generation || publication.Current.Revision <= publishedRevision)
                throw new InvalidOperationException("Stale MCP catalog publication.");
            ValueTask<PreparedSessionToolCatalog> Prepare(SessionRuntimeRegistry expected, ImmutableArray<string> activeNames, CancellationToken cancellation)
            {
            cancellation.ThrowIfCancellationRequested();
            var current = registry.CaptureSnapshot();
            var descriptors = publication.Tools.Select(tool => new ExtensionToolDescriptor(tool.Name, tool.Name,
                tool.Description, tool.Parameters, (arguments, context, callCancellation) => ExecuteAsync(tool.OriginalName, arguments, context, callCancellation))
                { Namespace = tool.Namespace, Exposure = tool.Exposure, DefaultActive = tool.Exposure == ToolExposure.Direct, Annotations = tool.Annotations }).ToImmutableArray();
            var nextIds = descriptors.Select(tool => tool.RegistrationId).ToImmutableArray();
            var nextNames = descriptors.Select(tool => tool.Name).ToImmutableHashSet(StringComparer.Ordinal);
            var plan = registry.PrepareToolCatalogReplacement(scope, registrationIds, descriptors, current);
            // mcp/index.ts registers every tool the server lists, with any result size the session admits (no 128-tool binding bound).
            var binding = new ExtensionAgentBinding(registry, policy, validator, sessionCancellationToken: attachment.LifetimeToken,
                invokerOptions: McpSessionHost.BindingInvokerOptions, options: PiSharp.Cli.Commands.PiPayloadBudget.PiBinding(new() { ActiveToolNames = [] }),
                capturedSnapshot: plan.PreviewSnapshot);
            // Keep every unrelated native/extension registration. Replace only this server's prior names.
            var retained = expected.RegisteredTools.Where(tool => !publishedNames.Contains(tool.Adapter.Name)).ToImmutableArray();
            var added = binding.Registrations.Where(tool => nextNames.Contains(tool.Name)).Select(tool =>
            {
                var index = binding.Registrations.IndexOf(tool);
                return new SessionRegisteredTool(binding.RegisteredToolDeclarations[index], binding.Adapters[index])
                    { Exposure = tool.Exposure, Namespace = tool.Namespace, DefaultActive = tool.DefaultActive, IsExtension = true, Annotations = tool.Annotations,
                        PrepareLoadout = binding.GetLoadoutPreparation(tool.Name) };
            }).ToImmutableArray();
            var replacement = expected.WithToolCatalog(retained.AddRange(added), composeHooks(expected, binding));
            var available = replacement.RegisteredTools.Select(tool => tool.Adapter.Name).ToImmutableHashSet(StringComparer.Ordinal);
            var nextDirect = added.Where(tool => tool.Exposure == ToolExposure.Direct).Select(tool => tool.Adapter.Name).ToImmutableHashSet(StringComparer.Ordinal);
            var active = activeNames.Where(available.Contains).Concat(added.Where(tool =>
                tool.DefaultActive && tool.Exposure == ToolExposure.Direct && !publishedDirect.Contains(tool.Adapter.Name)).Select(tool => tool.Adapter.Name))
                .Distinct(StringComparer.Ordinal).ToImmutableArray();
            return ValueTask.FromResult(new PreparedSessionToolCatalog(replacement, active, () =>
            {
                plan.Commit();
                registrationIds = nextIds; publishedNames = nextNames; publishedDirect = nextDirect; publishedRevision = publication.Current.Revision;
            }));
            }
            if (withdrawal)
            {
                if (transaction is null || !ReferenceEquals(transaction.Attachment, attachment))
                    throw new InvalidOperationException("Withdrawal requires the captured owning resource reservation.");
                await transaction.PrepareAndPublishCatalogAsync(Prepare).ConfigureAwait(false);
            }
            else
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        await owner.PrepareAndPublishToolCatalogAsync(attachment,
                            (expected, cancellation) => Prepare(expected, attachment.Session.GetActiveTools(), cancellation), token).ConfigureAwait(false);
                        break;
                    }
                    // A run (or another catalog change) can start between the idle wait and the publication; nothing was committed,
                    // so the publication is tried again (during that run, or at the next idle boundary).
                    catch (InvalidOperationException) when (attempt < 64 && !token.IsCancellationRequested &&
                        !attachment.LifetimeToken.IsCancellationRequested && ReferenceEquals(owner.Current, attachment)) { }
                }
            return new(publication.Current.Generation, publication.Current.Revision, true);
        }
        catch (Exception error) { publicationFailure ??= error; throw; }
        finally { inside.Value = prior; publications.Release(); }
    }

    private async ValueTask<JsonData> ExecuteAsync(string toolName, JsonData arguments, IExtensionToolContext context, CancellationToken token)
    {
        ValidateAttachment();
        if (context is not IExtensionToolInvocationContext invocation || invocation.OwnerId != scope.OwnerId ||
            invocation.OwnerGeneration != scope.OwnerGeneration || invocation.SessionGeneration != attachment.Generation)
            throw new InvalidOperationException("MCP calls require the admitted native invocation and captured owner/session generation.");
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (admission)
        {
            if (closeRequested) throw new ObjectDisposedException(nameof(McpPreparedServer));
            admitted.Add(settled.Task);
        }
        var prior = inside.Value; inside.Value = true;
        try
        {
            if (!ConvertResults) return await runtime.CallToolAsync(toolName, arguments, invocation, token).ConfigureAwait(false);
            JsonData raw;
            try
            {
                // index.ts getClient: a call prepared before a disable or exposure change resolves the server's state now.
                var current = serverEntry;
                if (!current.Config.Enabled) throw new InvalidOperationException($"MCP server \"{current.Name}\" is disabled.");
                if (McpConfigurationReader.GetToolExposure(current.Config, toolName) == McpExposure.Hidden ||
                    runtime.Snapshot.Catalog is { Connected: true } catalog && !catalog.Tools.Any(tool => tool.Name == toolName))
                    throw new InvalidOperationException($"MCP tool \"{current.Name}/{toolName}\" is no longer available.");
                raw = await runtime.CallToolAsync(toolName, arguments, invocation, token).ConfigureAwait(false);
            }
            // A call that fails reaches the model as an error with the reason, as the original's tool pipeline reports a thrown error.
            catch (Exception failure) when (!token.IsCancellationRequested && failure is not OperationCanceledException)
            {
                // runtime.ts withClient: a server that rejects the call for authentication drops its connection and needs a sign-in.
                if (McpServerManager.NeedsSignIn(failure, serverEntry))
                {
                    try { await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception) { /* The next call connects again. */ }
                    try { CallFailed?.Invoke(failure); } catch (Exception) { /* The state follows on the next change. */ }
                    return McpToolResults.Failure(serverEntry, toolName,
                        new InvalidOperationException(PiSharp.Extensions.Runtime.Mcp.Authentication.McpProviderTokenAuthentication.SignInRequiredMessage(serverEntry)));
                }
                return McpToolResults.Failure(serverEntry, toolName, failure);
            }
            var readable = runtime.Snapshot.Catalog.HasResources && serverEntry.Config.Exposure != McpExposure.Hidden;
            return await McpToolResults.ConvertAsync(serverEntry.Name, toolName, raw, readable,
                (data, extension, cancellation) => McpResourceToolsPublisher.SaveAsync(data, extension, null!, cancellation), token).ConfigureAwait(false);
        }
        finally { inside.Value = prior; lock (admission) admitted.Remove(settled.Task); settled.TrySetResult(); }
    }
}
