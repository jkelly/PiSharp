// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/host/route.ts.
// 1.6.0 here: a resolve's result may carry `opened` (the peer's incarnation at the handshake), and a route's path tells whether it is
// suspended (references sharing one wire hold). A route with an admission (slice 14 §2.3, Host/RouteAdmission.cs) resolves an address
// only for holds its decisions allow, and refuses a hold its own admission makes behind it (invalid/cycle).
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Link;
using PiSharp.Chord.Remote;
using PiSharp.Telemetry;

namespace PiSharp.Chord.Host;

/// <summary>What a route's <c>Resolve</c> returns. <paramref name="Opened"/> (Pi 1.6.0) is called once, when the link's handshake
/// completed, with the incarnation of the host at the other end: before anything else that host sent is handled and before this host sends
/// on the link; never for a link that closes before its handshake. It must not throw.</summary>
public sealed record ResolvedLink(ILink Link, LinkOptions Options, Action<string>? Opened = null);
public sealed class RouteOptions
{
    public required Func<JsonObject, Address, bool> Authorize { get; init; }
    public required Func<Address, ChordContext, Task<ResolvedLink>> Resolve { get; init; }
    /// <summary>
    /// The route's admission (Pi 1.6.0, slice 14 §2.3): asked once per principal and address with holds on the route, after
    /// <see cref="Authorize"/>, before anything is resolved or forwarded for that principal. It declares what it reads (references,
    /// registries, derives, live queries, acting as this host) and returns a watchable of <c>Admission.Allow()</c> or
    /// <c>Admission.Deny(reason)</c>: until its first allow a hold is loading; a denial fails the principal's holds to the address
    /// <c>failed</c> denied, releases them at the address's host and rejects their calls <c>forbidden/revoked</c>; a later allow shows them
    /// loading and forwards them again. While what it reads has failed, the holds bound on the address's link stay, new ones wait, and a
    /// new link of the address holds nothing until it can be read again (fail closed). It reads only contracts the app's API owns (a route
    /// is base code: it never reloads and keeps the contracts it was built with) and may hold nothing placed behind this route
    /// (<c>invalid/cycle</c>). With an admission, <see cref="Resolve"/> must return the link to the final provider that serves the
    /// address, never to another relay: a hold released by a denial is forwarded again under its id once a ping sent on the allow was
    /// answered, which proves the released hold's replies have all arrived only when the peer that answers is the one that sent them.
    /// </summary>
    public RouteAdmit? Admit { get; init; }
}
public sealed class RouteHandle
{
    private readonly HostRoute route;
    internal RouteHandle(HostRoute route) => this.route = route;
    public void Retry(Address address) => route.Control(address, false);
    public void Suspend(Address address) => route.Control(address, true);
}
public sealed partial class ChordHost
{
    public RouteHandle Route(HostKind kind, RouteOptions options, ChordContext context) => Dispatcher.Invoke(() =>
    {
        AssertOpen(); InitializeRemote();
        if (LoadStarted) throw new InvalidOperationException("Routes must be registered before loading plugins");
        if (remoteRoutes.ContainsKey(kind.Kind)) throw new InvalidOperationException($"A route for {kind.Kind} is already registered");
        var route = new HostRoute(this, kind, options, context); remoteRoutes.Add(kind.Kind, route);
        return new RouteHandle(route);
    });
}
internal sealed class HostRoute
{
    private sealed class Routed(Address address)
    {
        internal readonly Address Address = address;
        internal RemotePath Path = null!;
        // Retried: a Retry came while a resolve ran (native, see Control).
        internal bool Resolving, Suspended, Retried;
        internal OpenSpan? Span;
        internal ChordError? Failure;
    }
    private readonly ChordHost host;
    private readonly HostKind kind;
    private readonly RouteOptions options;
    private readonly ChordContext context;
    private readonly CancellationTokenSource stopped;
    private readonly Dictionary<string, Routed> addresses = new(StringComparer.Ordinal);
    private readonly Dictionary<HostLink, (Routed Routed, bool Greeted)> links = [];
    // Its admission's decisions, when it has an Admit.
    private readonly RouteAdmission? admission;
    internal HostRoute(ChordHost host, HostKind kind, RouteOptions options, ChordContext context)
    {
        this.host = host; this.kind = kind; this.options = options;
        stopped = CancellationTokenSource.CreateLinkedTokenSource(context.AbortSignal ?? CancellationToken.None);
        this.context = ChordContext.WithAbortSignal(stopped.Token, context);
        if (options.Admit is { } admit) admission = new RouteAdmission(host, this, admit, ChordContext.WithoutAbortSignal(context));
    }
    internal RouteAdmission? Admission => admission;
    internal IEnumerable<IUpstream> Holders => addresses.Values.SelectMany(x => x.Path.Holders);
    /// <summary>The path to the address with <paramref name="parameters"/>, for a hold acting as <paramref name="principal"/>: the params
    /// checked against the kind, then <c>Authorize</c> (<c>forbidden</c> when it refuses). A hold this route's own admission makes
    /// (<paramref name="holding"/> is the context it binds under) is refused <c>invalid/cycle</c>: it would wait on itself.</summary>
    internal RemotePath Path(JsonNode? parameters, JsonObject? principal, ChordContext? holding = null)
    {
        var address = DataInternal.CheckAddress(kind.Kind, ContractsInternal.KindParams(kind), parameters, context);
        if (principal is null || !options.Authorize(principal, address)) throw new ChordError("forbidden", $"{address.Key}: not authorized");
        if (holding is not null && admission?.Admitting(holding) == true)
            throw DataInternal.Invalid("cycle", $"{address.Key}: a route's admission holds a type placed behind its own route");
        if (!addresses.TryGetValue(address.Key, out var routed))
        {
            routed = new(address); var captured = routed;
            routed.Path = new RemotePath((change, holder) => Changed(captured, change, holder), () => captured.Suspended,
                admission is null ? null : path => admission.Gate(address, path));
            addresses.Add(address.Key, routed);
        }
        return routed.Path;
    }
    internal void Control(Address address, bool suspend) => host.Dispatcher.Invoke(() =>
    {
        if (address.Kind != kind.Kind) throw new ArgumentException("The address belongs to a different route", nameof(address));
        if (!addresses.TryGetValue(address.Key, out var routed)) return;
        if (suspend)
        {
            if (!routed.Path.Held) return;
            routed.Suspended = true;
            if (routed.Path.Link is null && !routed.Resolving) routed.Path.Unreachable(Suspended(routed));
        }
        else
        {
            routed.Suspended = false; routed.Failure = null;
            // A resolve running now may fail for what this retry clears (a crashed worker's start, rejected for the crash): its failure is
            // not recorded then, the address resolves again (native; upstream's single thread has no such window).
            if (routed.Resolving) routed.Retried = true;
            // Resolved only when a holder wants its link (one the route's admission allows; any without an admission).
            else if (routed.Path.Wanted && routed.Path.Link is null) _ = Resolve(routed);
        }
    });
    internal void Opened(HostLink link) { if (links.TryGetValue(link, out var entry)) links[link] = (entry.Routed, true); }
    internal void Closed(HostLink link)
    {
        if (!links.Remove(link, out var entry)) return;
        if (!entry.Greeted) entry.Routed.Failure = new ChordError("unavailable", $"{entry.Routed.Address.Key}: the link closed before its handshake");
        entry.Routed.Path.Disconnected(link);
    }
    internal void Stop() => stopped.Cancel();
    private static ChordError Suspended(Routed routed) => new("unavailable", $"{routed.Address.Key}: suspended");
    private void Changed(Routed routed, string change, IUpstream? holder)
    {
        if (host.Closed) return;
        if (change == "released")
        {
            routed.Suspended = false;
            if (routed.Path.Link is null && !routed.Resolving) addresses.Remove(routed.Address.Key);
        }
        else if (change == "attached")
        {
            if (routed.Suspended) holder!.Unreachable(Suspended(routed));
            else if (!routed.Resolving) _ = Resolve(routed, holder?.Span);
            else if (routed.Span is { } resolving && holder?.Span is { } waiting) waiting.AddLink(new(resolving.Span));
        }
        else if (!routed.Path.Held) addresses.Remove(routed.Address.Key);
        // Held only by holds its admission holds back: nothing resolves for them.
        else if (!routed.Path.Wanted) return;
        else if (routed.Suspended) routed.Path.Unreachable(Suspended(routed));
        else if (routed.Failure is { } failure) routed.Path.Unreachable(failure);
        else _ = Resolve(routed);
    }
    private async Task Resolve(Routed routed, ITelemetrySpan? trigger = null)
    {
        if (routed.Resolving || host.Closed) return;
        routed.Resolving = true;
        OpenSpan? span = null; var again = false;
        if (!ChordTelemetry.IsUntraced(host.Telemetry))
        {
            var attributes = host.Attributes(); attributes["kind"] = kind.Kind; attributes["address"] = ChordTelemetry.Capped(routed.Address.Key);
            span = new OpenSpan(host.Telemetry, "chord.resolve", attributes);
            routed.Span = span;
            if (trigger is not null) { span.Span.AddLink(new(trigger)); trigger.AddLink(new(span.Span)); }
        }
        try
        {
            var traced = span is null ? context : ChordContext.WithTelemetry(span.Span, context);
            var resolved = await options.Resolve(routed.Address, traced);
            routed.Resolving = false;
            if (host.Closed) { resolved.Link.Close(); return; }
            var link = host.MakeLink(resolved.Link, resolved.Options, LinkRole.Route, context, resolved.Opened);
            if (!link.Open) { Settle(routed, new ChordError("unavailable", $"{routed.Address.Key}: the link closed before its handshake")); return; }
            routed.Failure = null; links.Add(link, (routed, link.Greeted)); routed.Path.Connected(link);
            span?.Span.SetAttributes(new SpanAttributes { ["outcome"] = "linked" });
        }
        catch (Exception error)
        {
            if (span is not null) { ChordTelemetry.SpanFailed(span.Span, "resolve_failed", error); span.Span.SetAttributes(new SpanAttributes { ["outcome"] = "failed" }); }
            routed.Resolving = false;
            if (host.Closed) return;
            host.Report(new HostError("resolve_failed", error, routed.Address.Key));
            // Retried while it ran (Control): resolved again, unless suspended or released since.
            if (routed.Retried && !routed.Suspended && routed.Path.Held) again = true;
            else Settle(routed, DataInternal.AsChordError(error, routed.Address.Key));
        }
        finally { routed.Span = null; span?.End(); routed.Retried = false; }
        if (again && routed.Path.Wanted && routed.Path.Link is null) _ = Resolve(routed);
    }
    private void Settle(Routed routed, ChordError error)
    {
        if (!routed.Path.Held) { addresses.Remove(routed.Address.Key); return; }
        routed.Failure = error; routed.Path.Unreachable(routed.Suspended ? Suspended(routed) : error);
    }
}
