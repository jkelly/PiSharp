using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Runtime;

namespace PiSharp.Cli.Mcp.Authentication;

/// <summary>Actual application initializer/generation composition. No ambient clients or stores.
/// The base supplier owns partial failures; every returned fresh owner transfers to this wrapper.</summary>
public sealed class McpDefaultOAuthApplicationInstallation
{
    private static readonly object custodyGate = new();
    private static readonly ConditionalWeakTable<IAsyncDisposable, object> adopted = new();
    private static readonly ConditionalWeakTable<McpDefaultOAuthHost, object> adoptedHosts = new();
    private readonly ImmutableDictionary<string, McpDefaultOAuthApplicationAdmission> routes;
    private readonly McpApplicationGenerationAcquisition acquireBase;
    private readonly object gate = new();
    private readonly Dictionary<(string Name, long Generation), Slot> slots = [];
    private readonly HashSet<long> reservedGenerations = [];
    private readonly List<McpDefaultOAuthHostOriginal> originals = [];
    private readonly Dictionary<Task, AggregateException?> aggregates = new(ReferenceEqualityComparer.Instance);
    private sealed class Frame(McpDefaultOAuthApplicationInstallation owner, Frame? parent, Frame? ancestry)
    { internal readonly McpDefaultOAuthApplicationInstallation Owner = owner; internal readonly Frame? Parent = parent; internal readonly Frame? Ancestry = ancestry; internal volatile bool Active = true; }
    private static readonly AsyncLocal<Frame?> logical = new();
    [ThreadStatic] private static Frame? physical;
    private sealed class Slot(McpServerEntry entry, long generation, McpDefaultOAuthHost host, McpAdmittedChannelFactory channel)
    {
        internal readonly McpServerEntry Entry = entry;
        internal readonly long Generation = generation;
        internal readonly McpDefaultOAuthHost Host = host;
        internal McpAdmittedChannelFactory Channel = channel;
        internal readonly CancellationTokenSource Lifetime = new();
        internal readonly HashSet<Task> Pending = new(ReferenceEqualityComparer.Instance);
        internal bool Retired;
    }
    private sealed class GenerationOwner(McpDefaultOAuthApplicationInstallation owner, IAsyncDisposable native, Slot[] owned) : IAsyncDisposable
    {
        private Task? close;
        public ValueTask DisposeAsync()
        {
            owner.RejectReentry(); foreach (var slot in owned) slot.Host.ValidateExternalOwnerCall();
            lock (owner.gate)
            {
                if (close is not null) return new(close);
                foreach (var slot in owned) { slot.Retired = true; owner.RemoveExactSlot(slot); }
                // Publish the same actual close original once, before callbacks can observe completion.
                var parent = logical.Value; var ancestry = physical;
                close = Task.Run(() => owner.CloseGenerationAsync(native, owned, parent, ancestry)); return new(close);
            }
        }
    }
    public McpDefaultOAuthApplicationInstallation(ImmutableArray<McpDefaultOAuthApplicationAdmission> admissions,
        McpApplicationGenerationAcquisition acquireBase)
    {
        ArgumentNullException.ThrowIfNull(acquireBase);
        if (admissions.IsDefault || admissions.Length > McpAdmittedActivationHost.MaximumServers || acquireBase.GetInvocationList().Length != 1)
            throw new ArgumentException("Finite single-owner generation capabilities required.");
        var map = ImmutableDictionary.CreateBuilder<string, McpDefaultOAuthApplicationAdmission>(StringComparer.Ordinal);
        foreach (var route in admissions)
        {
            ArgumentNullException.ThrowIfNull(route); ArgumentException.ThrowIfNullOrWhiteSpace(route.Name);
            if (route.ValidateConfiguration is null || route.AcquireResources is null || route.CreateHttpChannel is null ||
                route.ValidateConfiguration.GetInvocationList().Length != 1 || route.AcquireResources.GetInvocationList().Length != 1 ||
                route.CreateHttpChannel.GetInvocationList().Length != 1 || !map.TryAdd(route.Name, route))
                throw new ArgumentException("Exact named single-cast route capabilities required.");
        }
        routes = map.ToImmutable(); this.acquireBase = acquireBase;
    }
    public ImmutableArray<McpDefaultOAuthHostOriginal> CapturedOriginals { get { lock (gate) return originals.ToImmutableArray(); } }
    public ImmutableArray<McpApplicationServerAdmission> ServerAdmissions => routes.Values.OrderBy(route => route.Name, StringComparer.Ordinal)
        .Select(route => new McpApplicationServerAdmission(route.Name, McpTransportKind.Http, route.ValidateConfiguration,
            (entry, generation) => (actual, token) => AcquireChannel(entry, actual, generation, token))).ToImmutableArray();
    private void RejectReentry()
    {
        var frames = new Stack<Frame>(); var seen = new HashSet<Frame>(ReferenceEqualityComparer.Instance);
        if (logical.Value is { } current) frames.Push(current); if (physical is { } actual) frames.Push(actual);
        while (frames.Count != 0)
        {
            var frame = frames.Pop(); if (!seen.Add(frame)) continue;
            if (frame.Active && ReferenceEquals(frame.Owner, this)) throw new InvalidOperationException("Application generation callback cannot reenter its own acquisition/close owner.");
            if (frame.Parent is { } parent) frames.Push(parent); if (frame.Ancestry is { } ancestry) frames.Push(ancestry);
        }
    }
    private T Invoke<T>(Func<T> action)
    {
        var previous = physical; var frame = new Frame(this, logical.Value, previous); physical = frame;
        try { return action(); } finally { frame.Active = false; physical = previous; }
    }
    private void Record(string phase, Task? original, Exception? direct)
    {
        lock (gate)
        {
            AggregateException? aggregate = null;
            if (original is not null && !aggregates.TryGetValue(original, out aggregate))
            { aggregate = original.IsFaulted ? original.Exception : null; aggregates.Add(original, aggregate); }
            originals.Add(new(phase, original, aggregate, direct));
        }
    }
    private AggregateException? Cached(Task original) { lock (gate) return aggregates[original]; }
    private void RemoveExactSlot(Slot slot)
    {
        if (slots.TryGetValue((slot.Entry.Name, slot.Generation), out var current) && ReferenceEquals(current, slot))
            slots.Remove((slot.Entry.Name, slot.Generation));
    }
    private async Task<T> Observe<T>(string phase, Func<ValueTask<T>> callback, CancellationToken token)
    {
        Task<T>? original = null; Exception? direct = null; T result = default!;
        try { original = Invoke(callback).AsTask(); result = await original.ConfigureAwait(false); }
        catch (Exception error) { direct = error; }
        Record(phase, original, direct);
        if (direct is OperationCanceledException canceled && original is { IsCanceled: true } && token.IsCancellationRequested && canceled.CancellationToken == token)
            ExceptionDispatchInfo.Capture(canceled).Throw();
        if (direct is not null) throw new McpDefaultOAuthApplicationFailure(phase, CapturedOriginals,
            original is { IsFaulted: true } ? Cached(original)! : direct);
        // Do not observe cancellation here: actual acquired resources must transfer cleanup custody first.
        return result;
    }
    private async Task Join(string phase, Func<Task> acquire, List<Exception> failures)
    {
        Task? original = null; Exception? direct = null;
        try { original = Invoke(acquire) ?? throw new IOException("Disposal returned no original."); await original.ConfigureAwait(false); }
        catch (Exception error) { direct = error; }
        Record(phase, original, direct);
        if (direct is not null) failures.Add(original is { IsFaulted: true } ? Cached(original)! : direct);
    }
    private void Fail(List<Exception> failures)
    {
        if (failures.Count != 0) throw new McpDefaultOAuthApplicationFailure("ownership", CapturedOriginals,
            failures.Count == 1 ? failures[0] : new AggregateException("Every application acquisition/cleanup sibling retained.", failures));
    }
    public async ValueTask<McpApplicationGenerationResources> AcquireGenerationAsync(McpApplicationGenerationRequest request, CancellationToken token)
    {
        RejectReentry(); ArgumentNullException.ThrowIfNull(request); token.ThrowIfCancellationRequested();
        if (request.Generation < 1) throw new ArgumentException("Reserved generation required.");
        lock (gate) if (!reservedGenerations.Add(request.Generation)) throw new IOException("Application generation reservation is affine.");
        var previous = logical.Value; var frame = new Frame(this, previous, physical); logical.Value = frame;
        McpApplicationGenerationResources? resources = null; var ownsNative = false; var ownsDiscovery = false;
        var owned = new List<Slot>(); var failures = new List<Exception>();
        try
        {
            resources = await Observe("application:base-generation", () => acquireBase(request, token), token).ConfigureAwait(false)
                ?? throw new IOException("Generation supplier returned no resources.");
            // Check the INNER identities, before constructing any new wrapper. A new wrapper
            // must never hide a previously adopted live owner from the installer's weak table.
            lock (custodyGate)
            {
                if (resources.NativeResources is { } native && !adopted.TryGetValue(native, out _))
                { adopted.Add(native, new()); ownsNative = true; }
                if (resources.DiscoveryResources is { } discovery && !adopted.TryGetValue(discovery, out _))
                { adopted.Add(discovery, new()); ownsDiscovery = true; }
            }
            if (!ownsNative || !ownsDiscovery || ReferenceEquals(resources.NativeResources, resources.DiscoveryResources) || !ReferenceEquals(resources.Request, request))
                throw new IOException("Generation changed its actual request or reused an inner owner; previous live owners cannot be disposed.");
            token.ThrowIfCancellationRequested();
            if (request.Generation < 1 || resources.Servers.IsDefault) throw new ArgumentException("Reserved generation/exact owners required.");
            foreach (var server in request.Catalog.Servers.Where(server => server.Config.Enabled && routes.ContainsKey(server.Name)))
            {
                if (server.Config.Transport != McpTransportKind.Http || resources.Servers.Count(owner => ReferenceEquals(owner.Entry, server)) != 1)
                    throw new IOException("OAuth route requires one exact HTTP server owner.");
                var admitted = await Observe("application:oauth-resources:" + server.Name, () => routes[server.Name].AcquireResources(request, server, token), token).ConfigureAwait(false);
                ArgumentNullException.ThrowIfNull(admitted);
                if (!server.Config.Raw.Value.TryGetProperty("url", out var url) || url.ValueKind != System.Text.Json.JsonValueKind.String ||
                    !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var configured) || configured != admitted.Server)
                    throw new IOException("OAuth resources changed the exact configured endpoint.");
                // Resources are borrowed capability data; installation owns the new host immediately.
                var host = McpDefaultOAuthHost.Install(admitted);
                McpAdmittedChannelFactory channel;
                var slot = new Slot(server, request.Generation, host, (entry, cancellation) => throw new IOException("Channel not installed."));
                owned.Add(slot);
                lock (custodyGate) { if (adoptedHosts.TryGetValue(host, out _)) throw new IOException("OAuth host reused."); adoptedHosts.Add(host, new()); }
                token.ThrowIfCancellationRequested();
                channel = Invoke(() => routes[server.Name].CreateHttpChannel(server, request.Generation, host.CreateAuthenticatedRequestFactory(admitted.Http)))
                    ?? throw new IOException("Admitted channel factory absent.");
                if (channel.GetInvocationList().Length != 1) throw new ArgumentException("One physical channel factory required.");
                slot.Channel = channel;
            }
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                foreach (var slot in owned) if (slots.ContainsKey((slot.Entry.Name, slot.Generation))) throw new IOException("Generation already installed.");
                foreach (var slot in owned) slots.Add((slot.Entry.Name, slot.Generation), slot);
            }
            var combined = new GenerationOwner(this, resources.NativeResources, owned.ToArray());
            return resources with { NativeResources = combined };
        }
        catch (Exception error)
        {
            failures.Add(error);
            lock (gate) foreach (var slot in owned) { slot.Retired = true; RemoveExactSlot(slot); }
            foreach (var slot in owned)
            {
                await Join("application:rejected-oauth", () => slot.Host.DisposeAsync().AsTask(), failures).ConfigureAwait(false);
                try { slot.Lifetime.Dispose(); } catch (Exception cleanup) { failures.Add(cleanup); }
                Import(slot.Host);
            }
            if (ownsDiscovery && resources is not null) await Join("application:rejected-discovery", () => resources.DiscoveryResources.DisposeAsync().AsTask(), failures).ConfigureAwait(false);
            if (ownsNative && resources is not null) await Join("application:rejected-native", () => resources.NativeResources.DisposeAsync().AsTask(), failures).ConfigureAwait(false);
            Fail(failures); throw;
        }
        finally { frame.Active = false; logical.Value = previous; }
    }
    private ValueTask<IMcpAdmittedRequestChannel> AcquireChannel(McpServerEntry expected, McpServerEntry actual, long generation, CancellationToken token)
    {
        RejectReentry(); token.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!ReferenceEquals(expected, actual) || !slots.TryGetValue((expected.Name, generation), out var slot) ||
                slot.Retired || !ReferenceEquals(slot.Entry, actual)) throw new IOException("Exact generation OAuth host is absent/retired.");
            Task<IMcpAdmittedRequestChannel>? operation = null;
            var parent = logical.Value; var ancestry = physical;
            operation = Task.Run(() => RunChannelAsync(slot, token, parent, ancestry)); slot.Pending.Add(operation);
            return new(operation);
        }
    }
    private async Task<IMcpAdmittedRequestChannel> RunChannelAsync(Slot slot, CancellationToken token, Frame? parent, Frame? ancestry)
    {
        lock (gate) { } // The owning original is published before callbacks.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, slot.Lifetime.Token);
        var previous = logical.Value; var frame = new Frame(this, parent, ancestry); logical.Value = frame;
        IMcpAdmittedRequestChannel? channel = null;
        try
        {
            channel = await Observe("application:channel-acquire", () => slot.Channel(slot.Entry, linked.Token), linked.Token).ConfigureAwait(false)
                ?? throw new IOException("Actual channel absent.");
            lock (gate) { linked.Token.ThrowIfCancellationRequested(); if (slot.Retired) throw new IOException("Generation retired during channel acquisition."); }
            return channel;
        }
        catch (Exception error)
        {
            var failures = new List<Exception> { error };
            if (channel is not null) await Join("application:late-channel-close", channel.CloseAsync, failures).ConfigureAwait(false);
            Fail(failures); throw;
        }
        finally { frame.Active = false; logical.Value = previous; }
    }
    private void Import(McpDefaultOAuthHost host) { lock (gate) foreach (var row in host.CapturedOriginals) originals.Add(row); }
    private async Task CloseGenerationAsync(IAsyncDisposable native, Slot[] owned, Frame? parent, Frame? ancestry)
    {
        lock (gate) { } // The stable close original is published before invoking any callback.
        var previous = logical.Value; var frame = new Frame(this, parent, ancestry); logical.Value = frame; var failures = new List<Exception>();
        try
        {
            foreach (var slot in owned) await Join("application:generation-cancel", () => slot.Host.CancellationAdmission.CancelAsync(slot.Lifetime), failures).ConfigureAwait(false);
            foreach (var slot in owned)
            {
                Task[] pending; lock (gate) pending = slot.Pending.ToArray();
                foreach (var original in pending) await Join("application:channel-join", () => original, failures).ConfigureAwait(false);
                lock (gate) slot.Pending.Clear();
                await Join("application:oauth-close", () => slot.Host.DisposeAsync().AsTask(), failures).ConfigureAwait(false);
                Import(slot.Host); try { slot.Lifetime.Dispose(); } catch (Exception error) { failures.Add(error); }
            }
            await Join("application:base-native-close", () => native.DisposeAsync().AsTask(), failures).ConfigureAwait(false);
            Fail(failures);
        }
        finally { frame.Active = false; logical.Value = previous; }
    }
}
