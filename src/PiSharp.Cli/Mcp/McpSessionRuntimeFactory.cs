using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Extensions.Mcp.Configuration;

namespace PiSharp.Cli.Mcp;

/// <summary>Native evidence of a synchronous disposal fault or the complete faulted Task original.
/// A faulted OCE remains an IOException rather than becoming async cancellation.</summary>
public sealed class McpFactoryDisposalException(string owner, Task? original, Exception evidence)
    : IOException($"MCP factory {owner} disposal failed: {evidence.Message}", evidence)
{
    public string Owner { get; } = owner;
    public Task? Original { get; } = original;
}

/// <summary>Cancellation proven by the actual disposal Task state; retains that native original.</summary>
public sealed class McpFactoryDisposalCancellation(string owner, Task original, OperationCanceledException evidence)
    : OperationCanceledException($"MCP factory {owner} disposal canceled.", evidence, evidence.CancellationToken)
{
    public string Owner { get; } = owner;
    public Task Original { get; } = original;
}

/// <summary>Actual host acquisition for one cwd and reserved generation. The provider owns cleanup
/// of allocations made before returning this admission. Configuration never supplies authority.</summary>
public sealed record McpSessionRuntimeAdmission(SessionRuntimeRegistry NativeRegistry,
    IAsyncDisposable NativeResources, IAsyncDisposable DiscoveryResources, IToolActionPolicy ExactPolicy, McpServerCatalog Catalog,
    ImmutableArray<McpServerActivationAdmission> Servers, bool AutoEnableCodemode,
    McpDiscoveryCatalogPreparation PrepareDiscovery)
{
    /// <summary>Optional explicit resource registration. Its registry/scope lifetime belongs to
    /// the admitted native/discovery resources, including failed acquisition cleanup.</summary>
    public McpAdmittedResourceRegistration? ResourceRegistration { get; init; }
    /// <summary>Optional owning profile binder, invoked on the same admitted attachment lease
    /// after MCP activation binding. Its resources belong to NativeResources.</summary>
    public Action<ReplaceableAgentSession, AgentSessionAttachment>? BindProfileView { get; init; }
}

/// <summary>Assembles explicitly admitted native and MCP resources before historical resolution.</summary>
public sealed class McpSessionRuntimeFactory
{
    private readonly Func<string, long, CancellationToken, ValueTask<McpSessionRuntimeAdmission>> acquire;
    public McpSessionRuntimeFactory(Func<string, long, CancellationToken, ValueTask<McpSessionRuntimeAdmission>> acquire)
    {
        ArgumentNullException.ThrowIfNull(acquire);
        if (acquire.GetInvocationList().Length != 1) throw new ArgumentException("One owning acquisition is required.", nameof(acquire));
        this.acquire = acquire;
    }

    public async ValueTask<SessionRuntimeLease> AcquireAsync(string cwd, long generation, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(cwd) || !Path.IsPathFullyQualified(cwd)) throw new ArgumentException("An actual absolute cwd is required.", nameof(cwd));
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
        token.ThrowIfCancellationRequested();
        var admission = await acquire(cwd, generation, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Host acquisition returned no owned admission.");
        McpAdmittedActivationHost? activation = null;
        SessionRuntimeLease? transferred = null;
        try
        {
            ArgumentNullException.ThrowIfNull(admission.NativeResources);
            ArgumentNullException.ThrowIfNull(admission.DiscoveryResources);
            if (ReferenceEquals(admission.DiscoveryResources, admission.NativeResources)) throw new ArgumentException("Native and discovery resource owners must be independent.");
            ArgumentNullException.ThrowIfNull(admission.NativeRegistry); ArgumentNullException.ThrowIfNull(admission.PrepareDiscovery);
            if (admission.BindProfileView?.GetInvocationList().Length > 1)
                throw new ArgumentException("One owning profile view binder is required.");
            if (admission.Servers.IsDefault) throw new ArgumentException("Initialized server admissions are required.");
            var servers = admission.Servers.Select(server =>
            {
                ArgumentNullException.ThrowIfNull(server); ArgumentNullException.ThrowIfNull(server.AcquireCapture);
                if (server.AcquireCapture.GetInvocationList().Length != 1) throw new ArgumentException("One server capture acquisition is required.");
                return server with { AcquireCapture = async (entry, current, cancellation) =>
                {
                    var capture = await server.AcquireCapture(entry, current, cancellation).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("Server acquisition returned no capture.");
                    if (capture.ReservedGeneration == generation) return capture;
                    var mismatch = new InvalidOperationException("Server acquisition changed the reserved target generation.");
                    var mismatchFailures = new List<Exception> { mismatch };
                    await JoinDisposalAsync(capture, "mismatched capture", mismatchFailures).ConfigureAwait(false);
                    Rethrow(mismatchFailures); throw mismatch;
                }};
            }).ToImmutableArray();
            McpPreparedDiscoveryCatalog Prepare(McpToolCatalogPlan plan, SessionRuntimeRegistry current)
            {
                var prepared = admission.PrepareDiscovery(plan, current)
                    ?? throw new InvalidOperationException("Discovery preparation returned no catalog.");
                if (prepared.Identities.IsDefault || prepared.Identities.Any(identity => identity is null || identity.ReservedGeneration != generation))
                    throw new InvalidOperationException("Discovery identities changed the reserved target generation.");
                return prepared;
            }
            if (admission.PrepareDiscovery.GetInvocationList().Length != 1) throw new ArgumentException("One discovery preparation is required.");
            activation = await McpAdmittedActivationHost.AcquireAsync(admission.Catalog, servers,
                admission.NativeRegistry, admission.AutoEnableCodemode, admission.ExactPolicy, Prepare, token,
                admission.ResourceRegistration).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            transferred = activation.TransferRuntimeOwnership();
            return new SessionRuntimeLease(activation.Registry.WithInitialToolSelectionFromCatalog(),
                new Resources(transferred, admission.DiscoveryResources, admission.NativeResources), (owner, attachment) =>
                {
                    activation.BindOwner(owner, attachment);
                    admission.BindProfileView?.Invoke(owner, attachment);
                });
        }
        catch (Exception original)
        {
            var failures = new List<Exception> { original };
            if (transferred is not null)
                await JoinDisposalAsync(transferred, "transferred activation", failures).ConfigureAwait(false);
            else if (activation is not null)
                await JoinDisposalAsync(activation, "activation", failures).ConfigureAwait(false);
            if (admission.DiscoveryResources is { } discovery && !ReferenceEquals(discovery, admission.NativeResources))
                await JoinDisposalAsync(discovery, "discovery", failures).ConfigureAwait(false);
            if (admission.NativeResources is { } native)
                await JoinDisposalAsync(native, "native", failures).ConfigureAwait(false);
            Rethrow(failures); throw;
        }
    }

    private sealed class Resources(SessionRuntimeLease activation, IAsyncDisposable discovery, IAsyncDisposable native) : IAsyncDisposable
    {
        private readonly object gate = new();
        private readonly AsyncLocal<bool> inside = new();
        private Task? close;
        public ValueTask DisposeAsync()
        {
            if (inside.Value) throw new InvalidOperationException("Runtime cleanup cannot join itself.");
            lock (gate) { close ??= CloseAsync(); return new(close); }
        }
        private async Task CloseAsync()
        {
            await Task.Yield(); var prior = inside.Value; inside.Value = true;
            var failures = new List<Exception>();
            try
            {
                await JoinDisposalAsync(activation, "activation", failures).ConfigureAwait(false);
                await JoinDisposalAsync(discovery, "discovery", failures).ConfigureAwait(false);
                await JoinDisposalAsync(native, "native", failures).ConfigureAwait(false);
            }
            finally { inside.Value = prior; }
            Rethrow(failures);
        }
    }

    internal static async Task JoinDisposalAsync(IAsyncDisposable resource, string owner, List<Exception> failures)
    {
        Task original;
        try { original = resource.DisposeAsync().AsTask(); }
        catch (Exception synchronous)
        {
            // No Task exists, so even OCE is synchronous fault evidence.
            failures.Add(new McpFactoryDisposalException(owner, null, synchronous)); return;
        }
        try { await original.ConfigureAwait(false); }
        catch (Exception selected)
        {
            failures.Add(original.IsCanceled && selected is OperationCanceledException cancellation
                ? new McpFactoryDisposalCancellation(owner, original, cancellation)
                : new McpFactoryDisposalException(owner, original, (Exception?)original.Exception ?? selected));
        }
    }
    internal static void Rethrow(List<Exception> failures)
    {
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException(failures);
    }
}
