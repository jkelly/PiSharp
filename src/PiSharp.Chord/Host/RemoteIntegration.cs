// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): next/host/host.ts, revocation.ts.
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Link;
using PiSharp.Chord.Remote;
using PiSharp.Telemetry;

namespace PiSharp.Chord.Host;

/// <summary>The next protocol's envelope codec, independent of legacy protocol 4.</summary>
public enum LinkCodec { Binary, Text }
public sealed class LinkOptions
{
    public required JsonObject Principal { get; init; }
    public bool Trusted { get; init; }
    public LinkCodec Codec { get; init; } = LinkCodec.Binary;
    public PartLimits? Limits { get; init; }
}

public sealed partial class ChordHost
{
    private readonly HashSet<HostLink> remoteLinks = [];
    private readonly Dictionary<string, HostRoute> remoteRoutes = new(StringComparer.Ordinal);
    private readonly RemotePath parentPath = new((_, _) => { });
    private readonly RemotePath unplacedPath = new((_, _) => { });
    internal readonly Dictionary<long, ProviderSide> Relays = [];
    private long nextRelay;
    private bool remoteInitialized;
    private ITimer? heartbeatTimer;
    private readonly ConditionalWeakTable<JsonObject, HashSet<string>> revocationKeys = new();

    private void InitializeRemote()
    {
        if (remoteInitialized) return;
        remoteInitialized = true;
        RemoteTarget = (contract, parameters, caller, context) =>
        {
            if (contract.Kind != ContractKind.Remote) return null;
            var address = ContractsInternal.DataOf(contract).HostAddress!(parameters.Value);
            if (address.Equals(Address)) return null;
            var path = PathOf(new WireAddress(address.Kind, address.Params), caller, null);
            return path is null ? null : new RemoteObject(this, contract, parameters, address, caller, path, context);
        };
        Closing += CloseRemote;
        var interval = TimeSpan.FromTicks(Math.Min(Options.HeartbeatPing.Ticks, Options.HeartbeatClose.Ticks) / 10);
        if (interval > TimeSpan.Zero)
        {
            var last = Options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            heartbeatTimer = Options.TimeProvider.CreateTimer(_ => Dispatcher.Post(() =>
            {
                var now = Options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                var late = now - last - (long)interval.TotalMilliseconds;
                var paused = late >= interval.TotalMilliseconds ? late : 0;
                last = now;
                foreach (var link in remoteLinks.ToArray()) link.Tick(now, paused);
            }), null, interval, interval);
        }
    }

    /// <summary>Accepts a child. Identity fields sent by this peer cannot impersonate another caller.</summary>
    public void Accept(ILink link, LinkOptions options, ChordContext context) => Dispatcher.Invoke(() =>
    {
        AssertOpen(); InitializeRemote(); MakeLink(link, options, LinkRole.Child, context);
    });

    /// <summary>Connects this host to its parent. A live parent cannot be replaced.</summary>
    public void Connect(ILink link, LinkOptions options, ChordContext context) => Dispatcher.Invoke(() =>
    {
        AssertOpen(); InitializeRemote();
        if (parentPath.Link is { Open: true }) throw new InvalidOperationException("This host already has an open parent link");
        var made = MakeLink(link, options, LinkRole.Parent, context);
        if (made.Open) parentPath.Connected(made);
        ResolveUnbound();
    });

    internal HostLink MakeLink(ILink link, LinkOptions options, LinkRole role, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(link); ArgumentNullException.ThrowIfNull(options);
        if (ChordJson.AsString(options.Principal["kind"]) is null) throw new ArgumentException("A link principal needs a kind", nameof(options));
        var made = new HostLink(this, link, options, role, context);
        remoteLinks.Add(made);
        made.Start();
        if (!made.Open) remoteLinks.Remove(made);
        return made;
    }
    internal void LinkOpened(HostLink link) { foreach (var route in remoteRoutes.Values) route.Opened(link); }
    internal void Unlink(HostLink link)
    {
        remoteLinks.Remove(link);
        foreach (var relay in Relays.Values.ToArray()) relay.Unlinked(link);
        parentPath.Disconnected(link);
        foreach (var route in remoteRoutes.Values.ToArray()) route.Closed(link);
    }
    internal long RelayOrigin(ProviderSide provider)
    {
        if (nextRelay == MessageCheck.MaxSafeInteger) throw new InvalidOperationException("Relay origins exhausted");
        var id = ++nextRelay; Relays.Add(id, provider); return id;
    }
    internal RemotePath? PathOf(WireAddress address, Caller caller, HostLink? arriving)
    {
        if (remoteRoutes.TryGetValue(address.Kind, out var route)) return route.Path(address.Params, caller.Principal);
        // A hold arriving from the parent never returns to it; route links can forward to a different parent.
        if (arriving is null) return parentPath;
        return arriving.Role != LinkRole.Parent && parentPath.Linked ? parentPath : null;
    }
    internal RemotePath UnplacedPath => unplacedPath;
    internal RemotePath? Repath(Contract contract, JsonObject parameters, Caller caller)
    {
        var address = ContractsInternal.DataOf(contract).HostAddress!(parameters);
        return PathOf(new WireAddress(address.Kind, address.Params), caller, null);
    }

    /// <summary>Revokes work selected by this host's RevokedBy keys, then propagates to routes.</summary>
    public void Revoke(JsonNode? revocation, ChordContext context) => Dispatcher.Invoke(() =>
    {
        AssertOpen(); InitializeRemote(); context.AbortSignal?.ThrowIfCancellationRequested();
        var key = DataInternal.CanonicalJson(revocation, context);
        var ownKey = Options.Principal is { } own ? DataInternal.PrincipalKey(own, context) : null;
        bool Revoked(JsonObject principal) => Options.RevokedBy is not null &&
            (ownKey is null || ownKey != DataInternal.PrincipalKey(principal, context)) &&
            revocationKeys.GetValue(principal, p => Options.RevokedBy(p)
                .Select(item => DataInternal.CanonicalJson(item, context)).ToHashSet(StringComparer.Ordinal)).Contains(key);
        var error = new ChordError("forbidden", "principal revoked", "revoked");
        var count = Objects.Values.Count(obj => obj.Served.Contract.Kind == ContractKind.Remote && obj.Caller.Principal is { } principal && Revoked(principal));
        RevokeLocal(Revoked, error);
        foreach (var path in remoteRoutes.Values.SelectMany(route => route.Holders).Concat(parentPath.Holders).ToArray())
            if (path is RemoteObject remote && remote.Caller.Principal is { } principal && Revoked(principal)) remote.Revoke(error);
        foreach (var link in remoteLinks.ToArray()) link.RevokeUnbound(Revoked, error);
        foreach (var link in remoteLinks.ToArray()) if (link.Role == LinkRole.Route) link.Send(new RevokeMessage(revocation?.DeepClone()));
        foreach (var link in remoteLinks.ToArray()) if (link.Role == LinkRole.Child && Revoked(link.Principal)) { count++; link.Close(); }
        if (!ChordTelemetry.IsUntraced(context.Telemetry))
        {
            var span = new OpenSpan(context.Telemetry, "chord.revoke", Attributes());
            span.Span.SetAttributes(new SpanAttributes { ["count"] = (double)count }); span.End();
        }
    });
    private void CloseRemote()
    {
        heartbeatTimer?.Dispose(); heartbeatTimer = null;
        foreach (var link in remoteLinks.ToArray()) link.Close();
        foreach (var route in remoteRoutes.Values) route.Stop();
        foreach (var holder in parentPath.Holders.Concat(unplacedPath.Holders))
            if (holder is RemoteObject remote) remote.Lost(HostErrors.Closed());
        remoteLinks.Clear(); remoteRoutes.Clear(); Relays.Clear();
    }
}
