// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/remote/path.ts.
// 1.6.0 (decision "References share one wire hold"): a path tells whether its route suspended it, counts a reference joining a held
// holder without a link as a new request, and carries the sharing table of the remote objects references of this host share on it
// (upstream keeps that table in a WeakMap keyed by the path; here it is the path's own, so a dropped route path takes it too). The path
// gate of a route's admission (withhold, forward, wanted) is chord-admission's.
using PiSharp.Chord.Data;
using PiSharp.Telemetry;

namespace PiSharp.Chord.Remote;

internal interface IUpstream
{
    ITelemetrySpan? Span => null;
    void Attach(HostLink link);
    void Dropped(ChordError error);
    void Unreachable(ChordError error);
}

internal sealed class RemotePath(Action<string, IUpstream?> changed, Func<bool>? suspended = null)
{
    private readonly HashSet<IUpstream> holders = [];
    /// <summary>The remote objects references of this host share on this path, by sharing key; each entry owned by its object, which takes
    /// it out. Made by the first shared object.</summary>
    internal Dictionary<string, PiSharp.Chord.Remote.RemoteObject>? Shared;
    /// <summary>Whether its route suspended it: a new remote reference then holds on its own instead of sharing a held object's state. A
    /// path without a route (the parent's, the host's unplaced one) never is.</summary>
    internal bool Suspended => suspended?.Invoke() == true;
    internal HostLink? Link { get; private set; }
    internal bool Linked { get; private set; }
    internal bool Held => holders.Count != 0;
    internal IUpstream[] Holders => [.. holders];
    internal void Attach(IUpstream holder)
    {
        holders.Add(holder);
        if (Link is { } link) holder.Attach(link); else changed("attached", holder);
    }
    /// <summary>A reference joined <paramref name="holder"/>, which the path holds already: without a link it counts as a new request, as
    /// an attach does (a route whose resolve failed resolves again).</summary>
    internal void Requested(IUpstream holder) { if (Link is null) changed("attached", holder); }
    internal void Detach(IUpstream holder)
    {
        if (holders.Remove(holder) && holders.Count == 0) changed("released", null);
    }
    internal void Connected(HostLink link)
    {
        Link = link; Linked = true;
        foreach (var holder in Holders) holder.Attach(link);
    }
    internal void Disconnected(HostLink link)
    {
        if (!ReferenceEquals(Link, link)) return;
        Link = null;
        var error = new ChordError("unavailable", "the link closed");
        foreach (var holder in Holders) holder.Dropped(error);
        changed("dropped", null);
    }
    internal void Unreachable(ChordError error) { foreach (var holder in Holders) holder.Unreachable(error); }
}
