// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): host.ts links/routes.
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Legacy;

/// <summary>Legacy host discovery, configured trust, reconnect and reload-aware routing.</summary>
public sealed class LegacyNetwork : IDisposable
{
    private readonly object gate = new();
    private readonly ILegacyReloadHost host;
    private readonly Action<Exception> report;
    private readonly Action<LegacyClusterPeer>? onPeer;
    private readonly Dictionary<string, LegacyRoute> routes = new(StringComparer.Ordinal);
    private readonly Dictionary<ILegacyLink, LegacyClusterPeer> peers = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<LegacyClusterPeer, int> deferred = [];
    private readonly string incarnation = Guid.NewGuid().ToString("N");
    private long origin;
    private bool closed;
    private TaskCompletionSource reloadEnded = Completed();
    private static TaskCompletionSource Completed() { var value = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); value.SetResult(); return value; }
    public LegacyNetwork(ILegacyReloadHost host, Action<Exception>? onError = null,
        TimeProvider? timeProvider = null, Action<LegacyClusterPeer>? onPeer = null)
    {
        this.host = host; report = error => { try { onError?.Invoke(error); } catch (Exception) { } }; this.onPeer = onPeer;
        TimeProvider = timeProvider ?? TimeProvider.System;
        Cluster = new LegacyCluster(this, host, report);
        host.Retired += Retired;
    }
    internal TimeProvider TimeProvider { get; }
    public LegacyCluster Cluster { get; }
    public LegacyClusterPeer? Upstream { get; private set; }
    public IReadOnlyList<LegacyClusterPeer> Peers { get { lock (gate) return peers.Values.ToArray(); } }
    public event Action<LegacyClusterPeer>? PeerAdded;
    public event Action<LegacyClusterPeer>? PeerRemoved;
    internal event Action<LegacyClusterPeer, LegacyFrame>? Frame;

    public LegacyClusterPeer Accept(ILegacyLink link, LegacyAcceptOptions options)
    {
        if (options.Kind is not ("host" or "client")) throw new ArgumentException("Unknown peer kind.", nameof(options));
        if (options.Kind == "host" && string.IsNullOrEmpty(options.Name)) throw new ArgumentException("Accepting a host needs its configured name.", nameof(options));
        return Add(link, options.LinkOptions, "downstream", options.Kind, options.Name, options.Principal, options.Restamp);
    }
    public LegacyClusterPeer Connect(ILegacyLink link, JsonNode? principal, LegacyConnectionOptions? options = null)
    {
        LegacyClusterPeer? previous;
        lock (gate) { ThrowIfClosed(); previous = Upstream; }
        previous?.Connection.Link.Close("replaced");
        var result = Add(link, options, "upstream", "host", null, principal);
        lock (gate) if (result.Connection.Open) Upstream = result;
        return result;
    }
    private LegacyClusterPeer Add(ILegacyLink link, LegacyConnectionOptions? options, string role, string kind, string? name, JsonNode? principal, Func<JsonNode?, JsonNode?>? restamp = null)
    {
        var localName = host.Name; var localKind = host.Kind; var localVersion = host.CodeVersion;
        LegacyClusterPeer peer;
        lock (gate)
        {
            ThrowIfClosed();
            if (peers.TryGetValue(link, out var existing)) return existing;
            var config = (options ?? new LegacyConnectionOptions()) with
            {
                Name = localName, Kind = localKind, CodeVersion = localVersion, Role = role,
                PeerKind = kind, PeerName = name, Principal = principal?.DeepClone(), TimeProvider = TimeProvider,
                OriginId = kind == "client" ? incarnation + ":" + ++origin : null,
                OnError = options?.OnError ?? report,
            };
            var connection = new LegacyConnection(link, config);
            peer = new(connection, role, kind, name, restamp);
            if (connection.Open)
            {
                peers.Add(link, peer);
                if (role == "upstream") Upstream = peer;
            }
        }
        // Session users register before the receive backlog is drained by these handlers.
        try { onPeer?.Invoke(peer); PeerAdded?.Invoke(peer); }
        catch
        {
            peer.Connection.Link.Close("peer admission failed"); Remove(link, peer, "peer admission failed"); throw;
        }
        peer.Connection.Received += frame => Frame?.Invoke(peer, frame);
        peer.Connection.Opened += hello => Hello(peer, checked((int)hello.Number("codeVersion")));
        peer.Connection.Closed += reason => Remove(link, peer, reason);
        return peer;
    }
    private void Remove(ILegacyLink link, LegacyClusterPeer peer, string reason)
    {
        lock (gate)
        {
            peers.Remove(link); deferred.Remove(peer);
            if (ReferenceEquals(Upstream, peer)) Upstream = null;
        }
        peer.End(reason); PeerRemoved?.Invoke(peer);
    }
    public void Route(string prefix, LegacyRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        lock (gate) { ThrowIfClosed(); routes[prefix] = route; }
    }
    /// <summary>Revokes old work before a new trusted principal can be used on an accepted connection.</summary>
    public void Invalidate(Func<JsonNode?, bool> matches)
    {
        foreach (var peer in Peers.Where(peer => peer.Role == "downstream" && peer.Connection.Open))
        {
            var principal = peer.Connection.Principal;
            if (!matches(principal)) continue;
            LegacyPeerSession.For(peer.Connection)?.Revoke();
            JsonNode? replacement;
            try { replacement = peer.Restamp?.Invoke(principal); }
            catch (Exception error) { report(error); replacement = null; }
            if (replacement is null) peer.Connection.Link.Close("principal invalidated");
            else peer.Connection.Restamp(replacement);
        }
    }
    private LegacyRoute? RouteFor(string name)
    {
        lock (gate) return routes.Where(pair => name.StartsWith(pair.Key, StringComparison.Ordinal))
            .OrderByDescending(pair => pair.Key.Length).Select(pair => pair.Value).FirstOrDefault();
    }
    /// <summary>Resolve own holds. Relayed holds must use ResolveRelayAsync so authorization cannot be bypassed.</summary>
    public async Task<LegacyConnection?> ResolveAsync(string name, JsonNode? principal, CancellationToken cancellationToken = default)
    {
        LegacyClusterPeer? direct;
        lock (gate)
        {
            ThrowIfClosed();
            if (Upstream?.Connection.Open == true) return Upstream.Connection;
            direct = peers.Values.FirstOrDefault(p => p.Role == "downstream" && p.Kind == "host" && p.Name == name && p.Connection.Open);
        }
        if (direct is not null) { await direct.WhenSyncedAsync(cancellationToken).ConfigureAwait(false); return direct.Connection; }
        return await ResolveRelayAsync(name, principal, () => true, cancellationToken).ConfigureAwait(false);
    }
    /// <summary>Authorize before and after asynchronous discovery. A spawned host is admitted even if this request expires.</summary>
    public async Task<LegacyConnection> ResolveRelayAsync(string name, JsonNode? principal, Func<bool>? still = null,
        CancellationToken cancellationToken = default)
    {
        still ??= () => true;
        var route = RouteFor(name) ?? throw new ChordException("unavailable", "no route to " + name);
        void Current() { cancellationToken.ThrowIfCancellationRequested(); if (!still()) throw new ChordException("gone", "principal invalidated while resolving"); }
        Current();
        if (!await route.Authorize(principal, name).ConfigureAwait(false)) throw ChordException.Forbidden(name + " refused for this principal");
        Current();
        // Discovery belongs to the host, not to the requesting reference. Never cancel it after it may have spawned.
        var link = await route.Resolve(name).ConfigureAwait(false);
        LegacyClusterPeer peer;
        lock (gate) peers.TryGetValue(link, out peer!);
        peer ??= Accept(link, new() { Kind = "host", Name = name, Principal = route.Principal(name) });
        Current();
        if (!await route.Authorize(principal, name).ConfigureAwait(false)) throw ChordException.Forbidden(name + " refused for this principal");
        Current();
        await peer.WhenSyncedAsync(cancellationToken).ConfigureAwait(false);
        Current(); return peer.Connection;
    }
    private void Hello(LegacyClusterPeer peer, int codeVersion)
    {
        peer.CodeVersion = codeVersion;
        if (peer.Role == "upstream" || peer.Kind == "client") { peer.MarkSynced(codeVersion); return; }
        var localVersion = host.CodeVersion;
        var hostReloading = host.Reloading;
        if (codeVersion > localVersion)
        {
            report(new ChordException("invalid", $"{peer.Name} runs code version {codeVersion}, ahead of {localVersion}"));
            peer.Connection.Link.Close("code version ahead of parent"); return;
        }
        lock (gate)
        {
            if (hostReloading || !reloadEnded.Task.IsCompleted)
            {
                if (codeVersion < localVersion) peer.FencedCodeVersions.Add(codeVersion);
                deferred[peer] = codeVersion; return;
            }
        }
        if (codeVersion == localVersion)
        {
            if (!TryMarkSynced(peer, codeVersion)) _ = Reconcile(peer);
        }
        else
        {
            peer.FencedCodeVersions.Add(codeVersion);
            _ = Reconcile(peer);
        }
    }
    private async Task Reconcile(LegacyClusterPeer peer)
    {
        try { await Cluster.ReconcileAsync(peer).ConfigureAwait(false); }
        catch (Exception error) { report(error); peer.Connection.Link.Close("reconciliation failed"); }
    }
    internal void BeginReload()
    {
        lock (gate)
        {
            ThrowIfClosed();
            if (!reloadEnded.Task.IsCompleted) throw new ChordException("unavailable", "a reload is already running", "reload_running");
            reloadEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
    internal void EndReload()
    {
        KeyValuePair<LegacyClusterPeer, int>[] waiting;
        lock (gate) { waiting = deferred.ToArray(); deferred.Clear(); reloadEnded.TrySetResult(); }
        foreach (var pair in waiting) if (pair.Key.Connection.Open) Hello(pair.Key, pair.Value);
    }
    internal Task WaitReloadAsync() { lock (gate) return reloadEnded.Task; }
    internal bool TryMarkSynced(LegacyClusterPeer peer, int codeVersion)
    {
        var currentVersion = host.CodeVersion;
        lock (gate)
        {
            // Use the same lock as BeginReload: a peer cannot become routable between the
            // participant snapshot and the reload marker without being reconciled afterwards.
            if (closed || !reloadEnded.Task.IsCompleted || codeVersion != currentVersion) return false;
            peer.MarkSynced(codeVersion);
        }
        AnnounceRetired(peer); return true;
    }
    internal void AnnounceRetired(LegacyClusterPeer peer)
    {
        if (host.HighestRetiredCodeVersion > 0) peer.Connection.Send(LegacyProtocol.Frame("retire", ("codeVersion", JsonValue.Create(host.HighestRetiredCodeVersion))));
    }
    private void Retired(int version)
    {
        foreach (var peer in Peers.Where(p => p.Role == "downstream" && p.Kind == "host" && p.Connection.Open))
            peer.Connection.Send(LegacyProtocol.Frame("retire", ("codeVersion", JsonValue.Create(version))));
    }
    private void ThrowIfClosed() { if (closed) throw new ObjectDisposedException(nameof(LegacyNetwork)); }
    public void Dispose()
    {
        lock (gate) { if (closed) return; closed = true; reloadEnded.TrySetResult(); }
        host.Retired -= Retired; Cluster.Dispose();
        foreach (var peer in Peers) peer.Connection.Link.Close("host closed");
        lock (gate) { peers.Clear(); routes.Clear(); deferred.Clear(); Upstream = null; }
    }
}
