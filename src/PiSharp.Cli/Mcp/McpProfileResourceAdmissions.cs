using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using PiSharp.Agent;
using PiSharp.CodingAgent;

namespace PiSharp.Cli.Mcp;

/// <summary>The exact successful base acquisition supplied to one independent resource admission.</summary>
public sealed record McpProfileResourceAdmissionRequest(string Cwd, long Generation,
    SessionRuntimeRegistry NativeRegistry, IToolActionPolicy ExactPolicy, McpSessionRuntimeAdmission BaseAdmission);

/// <summary>Transfers one fresh registration and one independently closeable resource owner.
/// Resources becomes caller-owned on return even when metadata is rejected. It must own the
/// registration's scope/registry/saver lifetime, and must not borrow a previous acquisition.</summary>
public sealed record McpProfileResourceAdmissionResult(McpProfileResourceAdmissionRequest Request,
    McpAdmittedResourceRegistration Registration, IAsyncDisposable Resources);

/// <summary>Owns and joins allocations not transferred by a successful return.</summary>
public delegate ValueTask<McpProfileResourceAdmissionResult> McpProfileResourceAdmission(
    McpProfileResourceAdmissionRequest request, CancellationToken token);

/// <summary>Original synchronous or faulted acquisition evidence, including every Task fault.
/// A synchronous or faulted OCE is a fault, not evidence of native Task cancellation.</summary>
public sealed class McpProfileResourceAcquisitionException(string owner, Task? original, Exception evidence)
    : IOException($"MCP profile {owner} acquisition failed.", evidence)
{
    public string Owner { get; } = owner;
    public Task? Original { get; } = original;
}

public sealed class McpProfileResourceAcquisitionCancellation(string owner, Task original, OperationCanceledException evidence)
    : OperationCanceledException($"MCP profile {owner} acquisition canceled.", evidence, evidence.CancellationToken)
{
    public string Owner { get; } = owner;
    public Task Original { get; } = original;
}

/// <summary>Opt-in composition through the existing profile/factory activation pipeline.
/// No process, transport, credentials, filesystem saver or resource registry is acquired here.</summary>
public static class McpProfileResourceAdmissions
{
    public static McpProfileRuntimeAdmission WithResources(McpProfileRuntimeAdmission baseAdmission,
        McpProfileResourceAdmission resourceAdmission)
    {
        ArgumentNullException.ThrowIfNull(baseAdmission); ArgumentNullException.ThrowIfNull(resourceAdmission);
        if (baseAdmission.GetInvocationList().Length != 1 || resourceAdmission.GetInvocationList().Length != 1)
            throw new ArgumentException("Exactly one base and resource admission are required.");
        var registrations = new ConditionalWeakTable<McpAdmittedResourceRegistration, object>();
        var gate = new object();
        return Acquire;

        async ValueTask<McpSessionRuntimeAdmission> Acquire(string cwd, long generation,
            SessionRuntimeRegistry registry, IToolActionPolicy policy, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(registry); ArgumentNullException.ThrowIfNull(policy);
            if (string.IsNullOrWhiteSpace(cwd) || !Path.IsPathFullyQualified(cwd)) throw new ArgumentException("Actual absolute cwd required.", nameof(cwd));
            if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
            if (!registry.UsesFinalActionPolicy(policy) || registry.InvocationOwnerGeneration is not null)
                throw new InvalidOperationException("Resource admission requires the actual unbound native registry and final policy.");
            token.ThrowIfCancellationRequested();
            var acquired = await ReadOriginalAsync(() => baseAdmission(cwd, generation, registry, policy, token), "base").ConfigureAwait(false);
            McpProfileResourceAdmissionResult? resource = null;
            try
            {
                ValidateBase(acquired, registry, policy);
                token.ThrowIfCancellationRequested();
                var request = new McpProfileResourceAdmissionRequest(cwd, generation, registry, policy, acquired);
                resource = await ReadOriginalAsync(() => resourceAdmission(request, token), "resources").ConfigureAwait(false);
                if (resource is null || resource.Registration is null || resource.Resources is null)
                    throw new InvalidOperationException("Resource admission returned no complete owned registration.");
                if (!ReferenceEquals(resource.Request, request))
                    throw new InvalidOperationException("Resource admission changed its exact captured request identity.");
                if (ReferenceEquals(resource.Resources, acquired.NativeResources) || ReferenceEquals(resource.Resources, acquired.DiscoveryResources))
                    throw new InvalidOperationException("Resource ownership must be independent of both base owners.");
                lock (gate)
                {
                    if (registrations.TryGetValue(resource.Registration, out _))
                        throw new InvalidOperationException("Resource registration cannot be reused across admissions.");
                    registrations.Add(resource.Registration, new object());
                }
                token.ThrowIfCancellationRequested();
                // Preserve catalog, server admissions, exact policy, discovery callback and every
                // existing record property. The factory prepares resources before discovery once.
                return acquired with
                {
                    ResourceRegistration = resource.Registration,
                    NativeResources = new ResourceOwners(resource.Resources, acquired.NativeResources)
                };
            }
            catch (Exception original)
            {
                var failures = new List<Exception> { original };
                var joined = new HashSet<IAsyncDisposable>(ReferenceEqualityComparer.Instance);
                if (resource?.Resources is { } resources && !ReferenceEquals(resources, acquired?.NativeResources) &&
                    !ReferenceEquals(resources, acquired?.DiscoveryResources))
                    await JoinOnce(resources, "profile resources", joined, failures).ConfigureAwait(false);
                if (acquired?.DiscoveryResources is { } discovery)
                    await JoinOnce(discovery, "profile discovery", joined, failures).ConfigureAwait(false);
                if (acquired?.NativeResources is { } native)
                    await JoinOnce(native, "profile native", joined, failures).ConfigureAwait(false);
                Rethrow(failures); throw;
            }
        }
    }

    private static void ValidateBase(McpSessionRuntimeAdmission? acquired, SessionRuntimeRegistry registry, IToolActionPolicy policy)
    {
        if (acquired is null || !ReferenceEquals(acquired.NativeRegistry, registry) || !ReferenceEquals(acquired.ExactPolicy, policy))
            throw new InvalidOperationException("Base admission changed the actual native registry or final policy.");
        ArgumentNullException.ThrowIfNull(acquired.NativeResources); ArgumentNullException.ThrowIfNull(acquired.DiscoveryResources);
        if (ReferenceEquals(acquired.NativeResources, acquired.DiscoveryResources))
            throw new InvalidOperationException("Native and discovery owners must be independent before composition.");
        if (acquired.ResourceRegistration is not null)
            throw new InvalidOperationException("Base admission already owns a resource registration.");
        ArgumentNullException.ThrowIfNull(acquired.Catalog); ArgumentNullException.ThrowIfNull(acquired.PrepareDiscovery);
        if (acquired.PrepareDiscovery.GetInvocationList().Length != 1 || acquired.Servers.IsDefault)
            throw new ArgumentException("Initialized server admissions and one discovery callback required.");
        foreach (var server in acquired.Servers)
        {
            ArgumentNullException.ThrowIfNull(server); ArgumentNullException.ThrowIfNull(server.ValidateConfiguration); ArgumentNullException.ThrowIfNull(server.AcquireCapture);
            if (server.ValidateConfiguration.GetInvocationList().Length != 1 || server.AcquireCapture.GetInvocationList().Length != 1)
                throw new ArgumentException("One configuration validator and capture acquisition per server required.");
        }
    }

    private static async Task<T> ReadOriginalAsync<T>(Func<ValueTask<T>> acquire, string owner)
    {
        Task<T> original;
        try { original = acquire().AsTask(); }
        catch (Exception synchronous) { throw new McpProfileResourceAcquisitionException(owner, null, synchronous); }
        try { return await original.ConfigureAwait(false); }
        catch (Exception selected)
        {
            if (original.IsCanceled && selected is OperationCanceledException cancellation)
                throw new McpProfileResourceAcquisitionCancellation(owner, original, cancellation);
            throw new McpProfileResourceAcquisitionException(owner, original, (Exception?)original.Exception ?? selected);
        }
    }

    private static async Task JoinOnce(IAsyncDisposable resource, string owner, HashSet<IAsyncDisposable> joined, List<Exception> failures)
    { if (joined.Add(resource)) await JoinDisposalAsync(resource, owner, failures).ConfigureAwait(false); }

    // Local proof helper: the existing factory helper is private. Capture exactly one native
    // disposal Task, join it, then inspect its full Exception/state instead of await's selection.
    private static async Task JoinDisposalAsync(IAsyncDisposable resource, string owner, List<Exception> failures)
    {
        Task original;
        try { original = resource.DisposeAsync().AsTask(); }
        catch (Exception synchronous) { failures.Add(new McpFactoryDisposalException(owner, null, synchronous)); return; }
        try { await original.ConfigureAwait(false); }
        catch (Exception selected)
        {
            failures.Add(original.IsCanceled && selected is OperationCanceledException cancellation
                ? new McpFactoryDisposalCancellation(owner, original, cancellation)
                : new McpFactoryDisposalException(owner, original, (Exception?)original.Exception ?? selected));
        }
    }

    private static void Rethrow(List<Exception> failures)
    {
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException(failures);
    }

    private sealed class ResourceOwners(IAsyncDisposable resources, IAsyncDisposable native) : IAsyncDisposable
    {
        private readonly object gate = new();
        private readonly AsyncLocal<bool> inside = new();
        private Task? close;
        public ValueTask DisposeAsync()
        {
            if (inside.Value) throw new InvalidOperationException("Resource owner cleanup cannot join itself.");
            lock (gate) { close ??= CloseAsync(); return new(close); }
        }
        private async Task CloseAsync()
        {
            await Task.Yield(); var prior = inside.Value; inside.Value = true;
            var failures = new List<Exception>();
            try
            {
                await JoinDisposalAsync(resources, "profile resources", failures).ConfigureAwait(false);
                await JoinDisposalAsync(native, "profile native", failures).ConfigureAwait(false);
            }
            finally { inside.Value = prior; }
            Rethrow(failures);
        }
    }
}
