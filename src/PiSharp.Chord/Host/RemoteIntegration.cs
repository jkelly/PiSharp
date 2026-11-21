// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/host/host.ts (links, paths, #bindRemote, #sharingKey), revocation.ts.
// 1.6.0: remote references of this host with one sharing key share one remote object and so one wire hold (decision "References share
// one wire hold"); a route's link tells its resolver the peer's incarnation at the handshake.
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
        BindRemote = binding =>
        {
            var contract = binding.Contract;
            if (contract.Kind != ContractKind.Remote) return false;
            var address = ContractsInternal.DataOf(contract).HostAddress!(binding.Parameters.Value);
            if (address.Equals(Address)) return false;
            var path = PathOf(new WireAddress(address.Kind, address.Params), binding.Caller, null);
            if (path is null) return false;
            BindOver(binding, path, address);
            return true;
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

    /// <summary>
    /// Binds <paramref name="binding"/> to a remote object over <paramref name="path"/> (decision "References share one wire hold"): the
    /// one this host holds for its sharing key, joined with no message (on a path whose route failed to resolve here, a join is a new
    /// request), or a new one, stored and bound before it attaches (a resolver re-entering finds it, and a reference it makes and releases
    /// there is not its last). A <c>self.Get</c> reference (its hold carries its dependent's chain), one on a suspended path, and one
    /// whose key's object a provider or relay left failed waiting (a new hold is a new request there: a relay's failed route resolves
    /// again for it) hold on their own, as before.
    /// </summary>
    private void BindOver(Binding binding, RemotePath path, Address address)
    {
        var context = binding.AcquisitionContext; var dependent = binding.Dependent;
        var key = dependent is null && !path.Suspended ? SharingKey(binding, address, context) : null;
        var found = key is null ? null : path.Shared?.GetValueOrDefault(key);
        var waiting = found?.State is { Kind: "failed", Cause.Kind: FailureKind.Waiting };
        if (found is not null && !(waiting && path.Link is not null))
        {
            binding.Bind(found);
            if (waiting) path.Requested(found);
            // A joining getter links to the hold while it is open (shared work).
            if (found.Span is { } span && context.Telemetry is ITelemetrySpan getter && !ChordTelemetry.IsUntraced(getter)) getter.AddLink(new(span));
            return;
        }
        // Owned entries: only the object an entry maps to takes it out, so an ended object never removes a newer one.
        var table = key is not null && found is null ? path.Shared ??= new(StringComparer.Ordinal) : null;
        RemoteObject? remote = null;
        Action? unindex = table is null ? null : () => { if (ReferenceEquals(table.GetValueOrDefault(key!), remote)) table.Remove(key!); };
        remote = new RemoteObject(this, binding.Contract, binding.Parameters, address, binding.Caller, path, dependent, context.Telemetry, unindex);
        table?[key!] = remote;
        binding.Bind(remote);
        if (!binding.Ended && ReferenceEquals(binding.Target, remote)) path.Attach(remote);
    }
    // The key under which references of this host share a remote object: its type, version and fingerprint, its params' identity key, the
    // address its placement computed (HostAddress is not in the fingerprint), and its caller (its principal's JSON as the wire carries it,
    // and the incarnation it acts with); every part escaped (JoinKey).
    private static string SharingKey(Binding binding, Address address, ChordContext context)
    {
        var type = binding.Contract; var caller = binding.Caller;
        var placed = ContractsInternal.JoinKey([address.Kind, DataInternal.CanonicalJson(address.Params, context)]);
        return ContractsInternal.JoinKey([
            type.Name, type.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), type.Fingerprint, binding.Parameters.Key, placed,
            caller.Principal is null ? "-" : "+" + DataInternal.PrincipalJson(caller.Principal, context), caller.Incarnation,
        ]);
    }

    internal HostLink MakeLink(ILink link, LinkOptions options, LinkRole role, ChordContext context, Action<string>? opened = null)
    {
        ArgumentNullException.ThrowIfNull(link); ArgumentNullException.ThrowIfNull(options);
        if (ChordJson.AsString(options.Principal["kind"]) is null) throw new ArgumentException("A link principal needs a kind", nameof(options));
        var made = new HostLink(this, link, options, role, context, opened);
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
        // While the host closes, its remote objects end with it (CloseRemote, then every reference): none goes loading on the way.
        if (Closed) return;
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
    /// <summary>The host's open links (its accepted children, its parent, its routes' links).</summary>
    internal IReadOnlyCollection<HostLink> Links => remoteLinks;
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
        // The remote objects first: each ends every reference sharing it with the revocation (their running calls reject forbidden), before
        // the references are ended one by one, which would release them as an ordinary release does.
        foreach (var path in remoteRoutes.Values.SelectMany(route => route.Holders).Concat(parentPath.Holders).Concat(unplacedPath.Holders).ToArray())
            if (path is RemoteObject remote && remote.Caller.Principal is { } principal && Revoked(principal)) remote.Revoke(error);
        RevokeLocal(Revoked, error);
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
        foreach (var holder in remoteRoutes.Values.SelectMany(route => route.Holders).Concat(parentPath.Holders).Concat(unplacedPath.Holders).ToArray())
            if (holder is RemoteObject remote) remote.Lost(HostErrors.Closed());
        remoteLinks.Clear(); remoteRoutes.Clear(); Relays.Clear();
    }
}
