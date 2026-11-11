// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): next/host/route.ts.
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Link;
using PiSharp.Chord.Remote;
using PiSharp.Telemetry;

namespace PiSharp.Chord.Host;

public sealed record ResolvedLink(ILink Link, LinkOptions Options);
public sealed class RouteOptions
{
    public required Func<JsonObject, Address, bool> Authorize { get; init; }
    public required Func<Address, ChordContext, Task<ResolvedLink>> Resolve { get; init; }
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
        internal bool Resolving, Suspended;
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
    internal HostRoute(ChordHost host, HostKind kind, RouteOptions options, ChordContext context)
    {
        this.host = host; this.kind = kind; this.options = options;
        stopped = CancellationTokenSource.CreateLinkedTokenSource(context.AbortSignal ?? CancellationToken.None);
        this.context = ChordContext.WithAbortSignal(stopped.Token, context);
    }
    internal IEnumerable<IUpstream> Holders => addresses.Values.SelectMany(x => x.Path.Holders);
    internal RemotePath Path(JsonNode? parameters, JsonObject? principal)
    {
        var address = DataInternal.CheckAddress(kind.Kind, ContractsInternal.KindParams(kind), parameters, context);
        if (principal is null || !options.Authorize(principal, address)) throw new ChordError("forbidden", $"{address.Key}: not authorized");
        if (!addresses.TryGetValue(address.Key, out var routed))
        {
            routed = new(address); var captured = routed;
            routed.Path = new RemotePath((change, holder) => Changed(captured, change, holder)); addresses.Add(address.Key, routed);
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
            if (routed.Path.Held && routed.Path.Link is null && !routed.Resolving) _ = Resolve(routed);
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
        else if (routed.Suspended) routed.Path.Unreachable(Suspended(routed));
        else if (routed.Failure is { } failure) routed.Path.Unreachable(failure);
        else _ = Resolve(routed);
    }
    private async Task Resolve(Routed routed, ITelemetrySpan? trigger = null)
    {
        if (routed.Resolving || host.Closed) return;
        routed.Resolving = true;
        OpenSpan? span = null;
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
            var link = host.MakeLink(resolved.Link, resolved.Options, LinkRole.Route, context);
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
            Settle(routed, DataInternal.AsChordError(error, routed.Address.Key));
        }
        finally { routed.Span = null; span?.End(); }
    }
    private void Settle(Routed routed, ChordError error)
    {
        if (!routed.Path.Held) { addresses.Remove(routed.Address.Key); return; }
        routed.Failure = error; routed.Path.Unreachable(routed.Suspended ? Suspended(routed) : error);
    }
}
