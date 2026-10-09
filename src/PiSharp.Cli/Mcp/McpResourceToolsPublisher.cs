// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/mcp/index.ts (serversWithResources,
// resourceServers, syncResourceTools), packages/coding-agent/src/extensions/mcp/resources.ts (createMcpResourceToolDefinitions) and
// packages/coding-agent/src/utils/output-files.ts (writeOutputFile: `pi-mcp-<random hex><extension>` in the temp directory, 0600).
using System.Collections.Immutable;
using System.Security.Cryptography;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Resources;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Mcp;

/// <summary>The resource tools of a production session (`list_mcp_resources`, `list_mcp_resource_templates`,
/// `read_mcp_resource`). They reach every enabled server whose exposure is not `hidden` and that offers resources, and are
/// registered with the widest exposure among those servers (`direct`, then `codemode` or `deferred`) once the first such
/// server connects, the way syncResourceTools re-registers them; without such servers they are withdrawn. The tools are
/// published through the session's durable catalog boundary like a server's tools, from their own registry scope, and
/// their calls are granted by exact name and target.</summary>
internal sealed class McpResourceToolsPublisher
{
    internal const string RegistrationPrefix = "mcp.resources";
    internal static readonly ImmutableArray<string> ToolNames =
        [PiSharp.Extensions.Runtime.Mcp.Resources.McpResourceTools.ListResources, PiSharp.Extensions.Runtime.Mcp.Resources.McpResourceTools.ListTemplates,
            PiSharp.Extensions.Runtime.Mcp.Resources.McpResourceTools.ReadResource];
    private readonly ExtensionRegistry registry;
    private readonly RegistrationScope scope;
    private readonly IToolActionPolicy policy;
    private readonly ExtensionToolArgumentValidator validator;
    private readonly McpPreparedHookComposer composeHooks;
    private readonly McpOwnedResourceDispatch dispatch;
    private readonly SemaphoreSlim publications = new(1, 1);
    private readonly object gate = new();
    private readonly Dictionary<string, (McpPreparedServer Server, McpExposure Exposure)> servers = new(StringComparer.Ordinal);
    private ReplaceableAgentSession? owner;
    private AgentSessionAttachment? attachment;
    private ImmutableArray<string> registrationIds = [];
    private ImmutableHashSet<string> publishedNames = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
    private McpExposure? published;
    private bool closed;

    internal McpResourceToolsPublisher(ExtensionRegistry registry, RegistrationScope scope, IToolActionPolicy policy,
        ExtensionToolArgumentValidator validator, McpPreparedHookComposer composeHooks, McpCallGrants grants)
    {
        this.registry = registry; this.scope = scope; this.policy = policy; this.validator = validator; this.composeHooks = composeHooks;
        dispatch = new(Capture, SaveAsync);
        var target = scope.OwnerId + "/" + scope.OwnerGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/";
        foreach (var name in ToolNames) grants.AdmitExact(name, target + RegistrationPrefix + "." + name);
    }

    /// <summary>Binds the dispatch and the publication to the attachment; the owner's shutdown records nothing, a replacement
    /// (reload) or explicit close withdraws the tools.</summary>
    internal void Bind(ReplaceableAgentSession actualOwner, AgentSessionAttachment actualAttachment)
    {
        lock (gate)
        {
            if (owner is not null) throw new InvalidOperationException("Resource tools are bound once.");
            owner = actualOwner; attachment = actualAttachment;
        }
        dispatch.Bind(actualOwner, actualAttachment, scope);
        actualOwner.RegisterOwnedResource(actualAttachment, CloseAsync);
    }

    /// <summary>The servers the tools reach changed: <paramref name="server"/> connected with or without resources, was disabled
    /// (null), or its exposure changed. Publishes the tools when their exposure changes.</summary>
    internal async Task UpdateAsync(string name, McpPreparedServer? server, McpExposure exposure, bool hasResources, CancellationToken token)
    {
        lock (gate)
        {
            if (closed) return;
            if (server is not null && hasResources && exposure != McpExposure.Hidden) servers[name] = (server, exposure);
            else servers.Remove(name);
        }
        await PublishAsync(token).ConfigureAwait(false);
    }

    /// <summary>The widest exposure of the servers the tools reach; null (hidden) when there are none.</summary>
    private McpExposure? Widest()
    {
        lock (gate)
        {
            var exposures = servers.Values.Select(row => row.Exposure).ToHashSet();
            return exposures.Contains(McpExposure.Direct) ? McpExposure.Direct : exposures.Contains(McpExposure.Codemode) ? McpExposure.Codemode
                : exposures.Contains(McpExposure.Deferred) ? McpExposure.Deferred : null;
        }
    }

    private IReadOnlyList<McpResourceServer> Capture(IExtensionToolInvocationContext invocation)
    {
        (McpPreparedServer Server, McpExposure Exposure)[] reached;
        lock (gate) reached = [.. servers.OrderBy(row => row.Key, StringComparer.Ordinal).Select(row => row.Value)];
        var captured = new List<McpResourceServer>();
        // A server that dropped its connection is left out until it connects again.
        foreach (var (server, _) in reached)
            try { captured.Add(server.CaptureResourceServer(invocation)); }
            catch (InvalidOperationException) { }
        return captured;
    }

    /// <summary>writeOutputFile("pi-mcp", extension, data): a new owner-only file in the temp directory.</summary>
    internal static async ValueTask<string> SaveAsync(ReadOnlyMemory<byte> data, string extension, PiSharp.Extensions.Mcp.Runtime.McpInvocationIdentity identity,
        CancellationToken token)
    {
        var path = Path.Combine(Path.GetTempPath(), "pi-mcp-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)) + extension);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var file = new FileStream(path, options);
        await file.WriteAsync(data, token).ConfigureAwait(false);
        return path;
    }

    private async Task PublishAsync(CancellationToken token, ReplaceableAgentSession.OwnedResourceRetirementTransaction? withdrawal = null)
    {
        await publications.WaitAsync(withdrawal is null ? token : CancellationToken.None).ConfigureAwait(false);
        try
        {
            ReplaceableAgentSession? currentOwner; AgentSessionAttachment? current;
            lock (gate) { currentOwner = owner; current = attachment; if (closed && withdrawal is null) return; }
            if (currentOwner is null || current is null) return;
            var next = withdrawal is null ? Widest() : null;
            if (next == published) return;
            ImmutableArray<ExtensionToolDescriptor> descriptors = next is { } exposure
                ? dispatch.CreateDescriptors(RegistrationPrefix, McpCatalogPlanner.ToToolExposure(exposure)) : [];
            ValueTask<PreparedSessionToolCatalog> Prepare(SessionRuntimeRegistry expected, ImmutableArray<string> activeNames, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested();
                var plan = registry.PrepareToolCatalogReplacement(scope, registrationIds, descriptors, registry.CaptureSnapshot());
                var binding = new ExtensionAgentBinding(registry, policy, validator, sessionCancellationToken: current.LifetimeToken,
                    options: new() { ActiveToolNames = [] }, capturedSnapshot: plan.PreviewSnapshot);
                var nextNames = descriptors.Select(tool => tool.Name).ToImmutableHashSet(StringComparer.Ordinal);
                var retained = expected.RegisteredTools.Where(tool => !publishedNames.Contains(tool.Adapter.Name)).ToImmutableArray();
                var added = binding.Registrations.Where(tool => nextNames.Contains(tool.Name)).Select(tool =>
                {
                    var index = binding.Registrations.IndexOf(tool);
                    return new SessionRegisteredTool(binding.RegisteredToolDeclarations[index], binding.Adapters[index])
                    { Exposure = tool.Exposure, Namespace = tool.Namespace, DefaultActive = tool.DefaultActive, IsExtension = true,
                        PrepareLoadout = binding.GetLoadoutPreparation(tool.Name) };
                }).ToImmutableArray();
                var replacement = expected.WithToolCatalog(retained.AddRange(added), composeHooks(expected, binding));
                var available = replacement.RegisteredTools.Select(tool => tool.Adapter.Name).ToImmutableHashSet(StringComparer.Ordinal);
                // Tools registered `direct` are activated, as on registration; a change away from `direct` leaves the declared set.
                var active = activeNames.Where(name => available.Contains(name) && !(publishedNames.Contains(name) && next != McpExposure.Direct))
                    .Concat(added.Where(tool => tool.DefaultActive && tool.Exposure == ToolExposure.Direct &&
                        (published != McpExposure.Direct || !publishedNames.Contains(tool.Adapter.Name))).Select(tool => tool.Adapter.Name))
                    .Distinct(StringComparer.Ordinal).ToImmutableArray();
                var nextIds = descriptors.Select(tool => tool.RegistrationId).ToImmutableArray();
                return ValueTask.FromResult(new PreparedSessionToolCatalog(replacement, active, () =>
                {
                    plan.Commit();
                    registrationIds = nextIds; publishedNames = nextNames; published = next;
                }));
            }
            if (withdrawal is not null) await withdrawal.PrepareAndPublishCatalogAsync(Prepare).ConfigureAwait(false);
            else
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        await currentOwner.PrepareAndPublishToolCatalogAsync(current,
                            (expected, cancellation) => Prepare(expected, current.Session.GetActiveTools(), cancellation), token).ConfigureAwait(false);
                        break;
                    }
                    // A run can start between the idle wait and the publication; nothing was committed, so publish again.
                    catch (InvalidOperationException) when (attempt < 64 && !token.IsCancellationRequested &&
                        !current.LifetimeToken.IsCancellationRequested && ReferenceEquals(currentOwner.Current, current)) { }
                }
        }
        finally { publications.Release(); }
    }

    private async Task CloseAsync(ReplaceableAgentSession.OwnedResourceRetirementTransaction transaction)
    {
        lock (gate) { closed = true; servers.Clear(); }
        // index.ts session_shutdown records nothing; a replacement withdraws the tools of this generation.
        if (transaction.IsSessionShutdown || registrationIds.IsEmpty) return;
        await PublishAsync(CancellationToken.None, transaction).ConfigureAwait(false);
    }
}
