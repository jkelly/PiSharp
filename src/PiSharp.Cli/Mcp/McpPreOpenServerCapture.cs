using System.Collections.Immutable;
using System.Runtime.CompilerServices;
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

/// <summary>One fresh admitted discovery, acquired before historical declaration resolution.
/// Calls remain disabled until its reserved target attachment is bound. Owns no ambient client,
/// process, credentials, host registry or retired attachment.</summary>
public sealed class McpPreOpenServerCapture : IAsyncDisposable
{
    private static readonly ConditionalWeakTable<ExtensionRegistry, object> admittedRegistries = new();
    private readonly object gate = new();
    private readonly ExtensionRegistry extensions;
    private readonly RegistrationScope scope;
    private readonly SessionRuntimeRegistry initial;
    private readonly IToolActionPolicy policy;
    private readonly ExtensionToolArgumentValidator validator;
    private readonly McpPreparedHookComposer compose;
    private readonly McpRuntimeOptions options;
    private readonly McpServerRuntime runtime;
    private readonly AsyncLocal<bool> invoking = new();
    private readonly AsyncLocal<bool> settling = new();
    private readonly HashSet<Task> invocations = [];
    private readonly Lazy<Task> unboundClose;
    private Task? closeOriginal, boundBodyOriginal;
    private readonly CancellationTokenSource lifetime = new();
    private ImmutableArray<string> registrationIds = [];
    private ImmutableHashSet<string> names = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
    private ImmutableDictionary<string, IPreparedToolAdapter> leafAdapters = ImmutableDictionary<string, IPreparedToolAdapter>.Empty.WithComparers(StringComparer.Ordinal);
    private SessionRuntimeRegistry? captured;
    private ReplaceableAgentSession? owner;
    private AgentSessionAttachment? attachment;
    private ReplaceableAgentSession.OwnedResourceLease? resource;
    private bool transferred, closing;

    private McpPreOpenServerCapture(McpServerEntry entry, ExtensionRegistry extensions, RegistrationScope scope,
        SessionRuntimeRegistry initial, IToolActionPolicy policy, ExtensionToolArgumentValidator validator,
        McpAdmittedChannelFactory factory, McpRuntimeOptions options, McpPreparedHookComposer compose)
    {
        this.extensions = extensions; this.scope = scope; this.initial = initial; this.policy = policy;
        this.validator = validator; this.options = options; this.compose = compose;
        runtime = new(entry, options, factory, StageDiscoveredAsync);
        unboundClose = new(CloseUnboundAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public SessionRuntimeRegistry Registry
    { get { lock (gate) return captured ?? throw new InvalidOperationException("Actual MCP discovery has not completed."); } }
    public McpServerToolSnapshot CatalogSnapshot => runtime.Snapshot.Catalog;
    public long ReservedGeneration => options.Generation;

    /// <summary>Borrow the existing runtime only after its actual attachment is bound. The resource
    /// dispatcher owns its dedicated extension scope; this capture owns the server/session fence.</summary>
    public McpResourceServer CaptureResourceServer(IExtensionToolInvocationContext invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation); RefuseReentry();
        scope.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (closing || closeOriginal is not null || owner is null || attachment is null ||
                !ReferenceEquals(owner.Current, attachment) || attachment.LifetimeToken.IsCancellationRequested ||
                invocation.SessionGeneration != attachment.Generation || attachment.Generation != options.Generation ||
                runtime.Snapshot.Generation != options.Generation || string.IsNullOrWhiteSpace(invocation.OwnerId) ||
                invocation.OwnerGeneration <= 0 || string.IsNullOrWhiteSpace(invocation.ToolCallId))
                throw new InvalidOperationException("Resource capture requires this capture's live bound attachment and runtime generation.");
            return runtime.CaptureResourceServer(invocation);
        }
    }

    public static async Task<McpPreOpenServerCapture> AcquireAsync(McpServerEntry entry,
        ExtensionRegistry freshExtensionRegistry, RegistrationScope scope, SessionRuntimeRegistry baseRuntimeRegistry,
        IToolActionPolicy finalPolicy, ExtensionToolArgumentValidator argumentValidator,
        McpAdmittedChannelFactory admittedChannelFactory, McpRuntimeOptions reservedTargetOptions,
        McpPreparedHookComposer hookComposer, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(entry); ArgumentNullException.ThrowIfNull(freshExtensionRegistry);
        ArgumentNullException.ThrowIfNull(scope); ArgumentNullException.ThrowIfNull(baseRuntimeRegistry);
        ArgumentNullException.ThrowIfNull(finalPolicy); ArgumentNullException.ThrowIfNull(argumentValidator);
        ArgumentNullException.ThrowIfNull(admittedChannelFactory); ArgumentNullException.ThrowIfNull(reservedTargetOptions);
        ArgumentNullException.ThrowIfNull(hookComposer);
        token.ThrowIfCancellationRequested();
        if (!entry.Config.Enabled || baseRuntimeRegistry.InvocationOwnerGeneration is not null ||
            !baseRuntimeRegistry.UsesFinalActionPolicy(finalPolicy) || !freshExtensionRegistry.CaptureSnapshot().Tools.IsEmpty)
            throw new ArgumentException("Pre-open discovery requires enabled admission, a fresh unbound registry/scope and the exact final policy.");
        scope.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
        lock (admittedRegistries)
        {
            if (admittedRegistries.TryGetValue(freshExtensionRegistry, out _))
                throw new InvalidOperationException("A pre-open MCP registry cannot be reused after an acquisition or retired generation.");
            admittedRegistries.Add(freshExtensionRegistry, new());
        }
        var capture = new McpPreOpenServerCapture(entry, freshExtensionRegistry, scope, baseRuntimeRegistry,
            finalPolicy, argumentValidator, admittedChannelFactory, reservedTargetOptions, hookComposer);
        try
        {
            await capture.runtime.ConnectAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _ = capture.Registry;
            return capture;
        }
        catch (Exception original)
        {
            try { await capture.CloseAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException("MCP pre-open acquisition and original cleanup failed.", original, cleanup); }
            ExceptionDispatchInfo.Capture(original).Throw(); throw;
        }
    }

    // Only the runtime's actual initialize/tools-list publication enters this private callback.
    private ValueTask<McpCatalogPublicationReceipt> StageDiscoveredAsync(McpCatalogPublication publication, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (captured is not null || closing || publication.Current.Generation != options.Generation || publication.Current.Revision != 1)
                throw new InvalidOperationException("Pre-open capture accepts exactly one fresh discovered catalog.");
        }
        var existing = initial.RegisteredTools.Select(tool => tool.Adapter.Name).ToImmutableHashSet(StringComparer.Ordinal);
        if (publication.Tools.Any(tool => existing.Contains(tool.Name)))
            throw new InvalidOperationException("Discovered MCP tool collides with an existing executable binding.");
        var descriptors = publication.Tools.Select(tool => new ExtensionToolDescriptor(tool.Name, tool.Name,
            tool.Description, tool.Parameters, (arguments, context, cancellation) => InvokeAsync(tool.OriginalName, arguments, context, cancellation))
            { Namespace = tool.Namespace, Exposure = tool.Exposure, DefaultActive = tool.Exposure == ToolExposure.Direct, Annotations = tool.Annotations }).ToImmutableArray();
        var plan = extensions.PrepareToolCatalogReplacement(scope, [], descriptors, extensions.CaptureSnapshot());
        var binding = new ExtensionAgentBinding(extensions, policy, validator, options: new() { ActiveToolNames = [] },
            sessionCancellationToken: lifetime.Token, capturedSnapshot: plan.PreviewSnapshot);
        var added = binding.Registrations.Select((tool, index) =>
            new SessionRegisteredTool(binding.RegisteredToolDeclarations[index], binding.Adapters[index])
            { Namespace = tool.Namespace, Exposure = tool.Exposure, DefaultActive = tool.DefaultActive, IsExtension = true, Annotations = tool.Annotations,
                PrepareLoadout = binding.GetLoadoutPreparation(tool.Name) }).ToImmutableArray();
        var replacement = initial.WithToolCatalog(initial.RegisteredTools.AddRange(added), compose(initial, binding));
        var stagedIds = descriptors.Select(tool => tool.RegistrationId).ToImmutableArray();
        var stagedNames = descriptors.Select(tool => tool.Name).ToImmutableHashSet(StringComparer.Ordinal);
        var stagedAdapters = binding.Registrations.Select((tool, index) => KeyValuePair.Create(tool.Name, binding.Adapters[index])).ToImmutableDictionary(StringComparer.Ordinal);
        token.ThrowIfCancellationRequested();
        // This publishes only the private staged extension catalog, never historical/session declarations.
        // The owning open path still must resolve exact historical schemas before exposing an attachment.
        lock (gate)
        {
            plan.Commit(); registrationIds = stagedIds; names = stagedNames; leafAdapters = stagedAdapters;
            captured = replacement;
        }
        return ValueTask.FromResult(new McpCatalogPublicationReceipt(publication.Current.Generation, publication.Current.Revision, true));
    }

    /// <summary>Transfer resources once into a fresh SessionRuntimeLease before open. This does not enable calls.</summary>
    public IAsyncDisposable TransferRuntimeOwnership()
    {
        lock (gate)
        {
            if (captured is null || transferred || closing) throw new InvalidOperationException("MCP runtime ownership cannot be transferred twice or after close.");
            transferred = true; return new RuntimeOwnership(this);
        }
    }

    /// <summary>The host reserves Generation before acquisition and binds this exact admitted attachment
    /// before exposing it. The stop body initiates real channel cancellation before session idle joins.</summary>
    public void BindOwner(ReplaceableAgentSession actualOwner, AgentSessionAttachment actualAttachment)
    {
        ArgumentNullException.ThrowIfNull(actualOwner); ArgumentNullException.ThrowIfNull(actualAttachment);
        RefuseReentry();
        lock (gate)
        {
            if (!transferred || closing || owner is not null || captured is null ||
                !ReferenceEquals(actualOwner.Current, actualAttachment) || actualAttachment.Generation != options.Generation)
                throw new InvalidOperationException("MCP capture cannot bind a reused, untransferred or different attachment generation.");
            actualAttachment.LifetimeToken.ThrowIfCancellationRequested();
            var actual = actualOwner.CaptureToolCatalogRegistryForBinding(actualAttachment);
            if (actual.InvocationOwnerGeneration != options.Generation || !actual.UsesFinalActionPolicy(policy) ||
                captured.RegisteredTools.Where(tool => names.Contains(tool.Adapter.Name)).Any(expected =>
                    !actual.UsesCapturedToolBinding(expected.Adapter.Name, expected.Declaration, leafAdapters[expected.Adapter.Name])))
                throw new InvalidOperationException("Attachment did not retain the exact discovered executable catalog.");
            // Register first: rejection leaves this capture unbound and independently closeable.
            resource = actualOwner.RegisterOwnedResource(actualAttachment, CloseBoundAsync, runtime.CloseAsync);
            owner = actualOwner; attachment = actualAttachment;
        }
    }

    private async ValueTask<JsonData> InvokeAsync(string originalName, JsonData arguments,
        IExtensionToolContext context, CancellationToken token)
    {
        AgentSessionAttachment bound; ReplaceableAgentSession actualOwner;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (closing || attachment is null || owner is null) throw new InvalidOperationException("Pre-open MCP calls are disabled until actual owner binding.");
            bound = attachment; actualOwner = owner;
            invocations.Add(completion.Task);
        }
        var prior = invoking.Value; invoking.Value = true;
        try
        {
            bound.LifetimeToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(actualOwner.Current, bound) || context is not IExtensionToolInvocationContext invocation ||
                invocation.OwnerId != scope.OwnerId || invocation.OwnerGeneration != scope.OwnerGeneration || invocation.SessionGeneration != options.Generation)
                throw new InvalidOperationException("MCP adapter belongs to a different or retired invocation generation.");
            return await runtime.CallToolAsync(originalName, arguments, invocation, token).ConfigureAwait(false);
        }
        finally
        {
            invoking.Value = prior;
            lock (gate) { invocations.Remove(completion.Task); completion.TrySetResult(); }
        }
    }

    public Task CloseAsync()
    {
        RefuseReentry();
        lock (gate)
        {
            if (closeOriginal is not null) return closeOriginal;
            // Automatic retirement retains the owning resource's complete stop/withdrawal receipt.
            // The bound body alone cannot report a failed physical stop to this public caller.
            if (resource is not null) { var original = resource.CloseAsync(); closing = true; return closeOriginal = original; }
            closing = true; return closeOriginal = unboundClose.Value;
        }
    }
    public ValueTask DisposeAsync() => new(CloseAsync());
    private sealed class RuntimeOwnership(McpPreOpenServerCapture capture) : IAsyncDisposable
    {
        private Task? boundJoin;
        public ValueTask DisposeAsync()
        {
            capture.RefuseReentry();
            lock (capture.gate)
                if (capture.boundBodyOriginal is { } original) return new(boundJoin ??= JoinOwnedBodyAsync(original));
            return capture.DisposeAsync();
        }
        private static async Task JoinOwnedBodyAsync(Task original)
        {
            await Task.Yield();
            try { await original.ConfigureAwait(false); }
            catch (Exception) { /* The registered owning resource retains this exact bound-body fault. */ }
        }
    }
    private void RefuseReentry()
    { if (invoking.Value || settling.Value) throw new InvalidOperationException("MCP callback cannot await its own captured resource settlement."); }

    private async Task CloseUnboundAsync()
    {
        // Yield keeps runtime cancellation callbacks outside the capture gate.
        await Task.Yield();
        var prior = settling.Value; settling.Value = true;
        try { await CloseUnboundBodyAsync().ConfigureAwait(false); }
        finally { settling.Value = prior; }
    }
    private async Task CloseUnboundBodyAsync()
    {
        var errors = new List<Exception>();
        try { await runtime.CloseAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        try { RemovePrivateRegistrations(); } catch (Exception error) { errors.Add(error); }
        try { lifetime.Dispose(); } catch (Exception error) { errors.Add(error); }
        Rethrow(errors);
    }
    private Task CloseBoundAsync(ReplaceableAgentSession.OwnedResourceRetirementTransaction transaction)
    {
        lock (gate)
        {
            if (!ReferenceEquals(transaction.Attachment, attachment)) throw new InvalidOperationException("MCP withdrawal requires its exact admitted attachment transaction.");
            closing = true; return boundBodyOriginal ??= CloseBoundCoreAsync(transaction);
        }
    }
    private async Task CloseBoundCoreAsync(ReplaceableAgentSession.OwnedResourceRetirementTransaction transaction)
    {
        // Record the actual body task before callbacks; runtime release can join it without reentering owning close.
        await Task.Yield();
        var prior = settling.Value; settling.Value = true;
        try { await CloseBoundBodyAsync(transaction).ConfigureAwait(false); }
        finally { settling.Value = prior; }
    }
    private async Task CloseBoundBodyAsync(ReplaceableAgentSession.OwnedResourceRetirementTransaction transaction)
    {
        Task[] originals;
        lock (gate)
        {
            closing = true; originals = invocations.ToArray();
        }
        var errors = new List<Exception>();
        foreach (var original in originals)
            try { await original.ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        if (!registrationIds.IsEmpty)
        {
            try
            {
                await transaction.PrepareAndPublishCatalogAsync((expected, active, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    var plan = extensions.PrepareToolCatalogReplacement(scope, registrationIds, [], extensions.CaptureSnapshot());
                    var emptyBinding = new ExtensionAgentBinding(extensions, policy, validator, options: new() { ActiveToolNames = [] },
                        capturedSnapshot: plan.PreviewSnapshot);
                    var replacement = expected.WithToolCatalog(expected.RegisteredTools.Where(tool => !names.Contains(tool.Adapter.Name)).ToImmutableArray(),
                        compose(expected, emptyBinding));
                    return ValueTask.FromResult(new PreparedSessionToolCatalog(replacement, active.Where(name => !names.Contains(name)).ToImmutableArray(),
                        () => { plan.Commit(); registrationIds = []; }));
                }).ConfigureAwait(false);
            }
            catch (Exception error) { errors.Add(error); }
        }
        try { lifetime.Dispose(); } catch (Exception error) { errors.Add(error); }
        Rethrow(errors);
    }
    private void RemovePrivateRegistrations()
    {
        if (registrationIds.IsEmpty) return;
        extensions.PrepareToolCatalogReplacement(scope, registrationIds, [], extensions.CaptureSnapshot()).Commit();
        registrationIds = [];
    }
    private static void Rethrow(List<Exception> errors)
    {
        if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1) throw new AggregateException(errors);
    }
}
