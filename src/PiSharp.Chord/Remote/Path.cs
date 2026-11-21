// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/remote/path.ts.
// 1.6.0 (decision "References share one wire hold"): a path tells whether its route suspended it, counts a reference joining a held
// holder without a link as a new request, and carries the sharing table of the remote objects references of this host share on it
// (upstream keeps that table in a WeakMap keyed by the path; here it is the path's own, so a dropped route path takes it too).
// 1.6.0 (slice 14 §2.3): a route with an admission gates its paths: a holder is forwarded only while its decision allows it, released
// when it is denied, and forwarded again behind a fence.
using PiSharp.Chord.Data;
using PiSharp.Chord.Host;
using PiSharp.Telemetry;

namespace PiSharp.Chord.Remote;

internal interface IUpstream
{
    ITelemetrySpan? Span => null;
    /// <summary>Whom it acts for: a route's admission asks one decision per principal.</summary>
    Caller Caller { get; }
    void Attach(HostLink link);
    void Dropped(ChordError error);
    void Unreachable(ChordError error);
    /// <summary>Its path's gate holds it back with <paramref name="cause"/> (denied, or a decision that cannot be made): it is on no link
    /// from here on, and shows the cause.</summary>
    void Withheld(FailureCause cause);
    /// <summary>Its path's gate admits it: loading again (after what held it back) until its next binding.</summary>
    void Admitted();
}

/// <summary>What decides whether a path forwards a holder (a route's admission, one decision per principal and address). It forwards a
/// holder it held back with <c>RemotePath.Forward</c>, and takes one back with <c>RemotePath.Withhold</c>.</summary>
internal interface IPathGate
{
    /// <summary><paramref name="holder"/> joined the path: whether it is forwarded at once (its principal's decision allows it already).</summary>
    bool Joined(IUpstream holder);
    /// <summary><paramref name="holder"/> left the path.</summary>
    void Left(IUpstream holder);
    /// <summary>The path lost its link (its holders were told): the gate takes back, with <c>RemotePath.Withhold</c>, the holders it cannot
    /// forward on the next one, so a new link holds only what a decision that can be read allows.</summary>
    void Unlinked();
}

/// <summary>The parent link or one route address: its current link and its holders, held on that link and again on every next one. With a
/// <paramref name="gate"/>, only the holders it admits are held on the links; the others wait withheld, told by the gate.</summary>
internal sealed class RemotePath(Action<string, IUpstream?> changed, Func<bool>? suspended = null, Func<RemotePath, IPathGate>? gate = null)
{
    private readonly HashSet<IUpstream> holders = [];
    private IPathGate? gated;
    private bool gateMade;
    // Its gate, made at its first use (a gate knows its path).
    private IPathGate? Gate { get { if (!gateMade) { gateMade = true; gated = gate?.Invoke(this); } return gated; } }
    // The holders its gate holds back (undecided, denied, refused): on no link. Made by the first.
    private HashSet<IUpstream>? withheld;
    // The holders a denial released on the current link, each with what cancels its fence while one waits for its pong (Forward); gone
    // with the link. Made by the first.
    private Dictionary<IUpstream, Action?>? released;
    /// <summary>The remote objects references of this host share on this path, by sharing key; each entry owned by its object, which takes
    /// it out. Made by the first shared object.</summary>
    internal Dictionary<string, PiSharp.Chord.Remote.RemoteObject>? Shared;
    /// <summary>Whether its route suspended it: a new remote reference then holds on its own instead of sharing a held object's state. A
    /// path without a route (the parent's, the host's unplaced one) never is.</summary>
    internal bool Suspended => suspended?.Invoke() == true;
    internal HostLink? Link { get; private set; }
    internal bool Linked { get; private set; }
    internal bool Held => holders.Count != 0;
    /// <summary>Whether a holder wants a link: one its gate admits (every holder without a gate). A route resolves nothing for an address
    /// whose holders are all denied or undecided (slice 14 §2.3).</summary>
    internal bool Wanted => holders.Count > (withheld?.Count ?? 0);
    /// <summary>Whether its gate holds <paramref name="holder"/> back now (it is on no link).</summary>
    internal bool Withholds(IUpstream holder) => withheld?.Contains(holder) == true;
    /// <summary>How many holders a denial released on the current link (each waits for its next allow, or behind its fence).</summary>
    internal int Released => released?.Count ?? 0;
    internal IUpstream[] Holders => [.. holders];
    /// <summary>Holds <paramref name="holder"/> over this path: on the current link now, and on every next one. A gated path asks its gate
    /// first: a holder it does not admit yet is on no link until <see cref="Forward"/>.</summary>
    internal void Attach(IUpstream holder)
    {
        holders.Add(holder);
        if (Gate is { } asked)
        {
            // Withheld while its gate is asked: what the gate tells it meanwhile finds it on no link.
            (withheld ??= []).Add(holder);
            if (!asked.Joined(holder)) return;
            withheld.Remove(holder);
        }
        if (Link is { } link) holder.Attach(link); else changed("attached", holder);
    }
    /// <summary>A reference joined <paramref name="holder"/>, which the path holds already: without a link it counts as a new request, as
    /// an attach does (a route whose resolve failed resolves again).</summary>
    internal void Requested(IUpstream holder) { if (Link is null && !Withholds(holder)) changed("attached", holder); }
    internal void Detach(IUpstream holder)
    {
        if (!holders.Remove(holder)) return;
        withheld?.Remove(holder);
        Unfence(holder);
        Gate?.Left(holder);
        if (holders.Count == 0) changed("released", null);
    }
    /// <summary>
    /// Its gate admits <paramref name="holder"/> now: told so (one that was told what held it back is loading again until its next
    /// binding), then held on the current link, or the path asks for one. A holder a denial released on this very link is held again,
    /// under its id (relays never renumber), only behind a fence: the release, then a ping, and the hold after that ping's pong. A link is
    /// ordered and a peer answers a ping after everything it sent before it, so every reply of the released hold has arrived, and was
    /// dropped, before the hold exists again. This relies on the route's resolve returning the link to the final provider that serves the
    /// address, as the route contract requires: an intermediate relay would answer the ping before the final provider's old replies have
    /// drained through it.
    /// </summary>
    internal void Forward(IUpstream holder)
    {
        if (withheld?.Remove(holder) != true) return;
        holder.Admitted();
        if (Link is not { } link) { changed("attached", holder); return; }
        if (released is not { } fences || !fences.ContainsKey(holder)) { holder.Attach(link); return; }
        Action? cancel = null;
        var cancelled = false;
        var fence = link.Fence(() =>
        {
            // Cancelled, or the link dropped, in the turn its pong arrived: nothing is held.
            if (cancelled || !ReferenceEquals(released, fences)) return;
            fences.Remove(holder);
            holder.Attach(link);
        });
        cancel = () => { cancelled = true; fence(); };
        fences[holder] = cancel;
    }
    /// <summary>Its gate holds <paramref name="holder"/> back with <paramref name="cause"/> (denied, or a decision that cannot be made):
    /// told so, and released on the link it is held on. A fence still waiting for its pong ends: the next allow sends a new one.</summary>
    internal void Withhold(IUpstream holder, FailureCause cause)
    {
        if (!holders.Contains(holder)) return;
        var forwarded = (withheld ??= []).Add(holder);
        var fenced = released?.ContainsKey(holder) == true;
        if (fenced) released![holder]?.Invoke();
        if (Link is not null && (forwarded || fenced)) (released ??= [])[holder] = null;
        holder.Withheld(cause);
    }
    /// <summary><paramref name="link"/> is the path's link now: every holder its gate admits is held on it, under its current decision.</summary>
    internal void Connected(HostLink link)
    {
        Link = link; Linked = true;
        foreach (var holder in Holders) if (!Withholds(holder)) holder.Attach(link);
    }
    /// <summary><paramref name="link"/> closed: every holder held on it goes loading until the next link. Its fences go with it, and its
    /// gate takes back what it cannot forward now: the next link holds what the gate admits then.</summary>
    internal void Disconnected(HostLink link)
    {
        if (!ReferenceEquals(Link, link)) return;
        Link = null;
        if (released is { } fences) { released = null; foreach (var cancel in fences.Values) cancel?.Invoke(); }
        var error = new ChordError("unavailable", "the link closed");
        foreach (var holder in Holders) if (!Withholds(holder)) holder.Dropped(error);
        Gate?.Unlinked();
        changed("dropped", null);
    }
    /// <summary>Its address cannot be reached for now (a route's resolve failed, a suspension): every holder that wants its link fails
    /// waiting.</summary>
    internal void Unreachable(ChordError error) { foreach (var holder in Holders) if (!Withholds(holder)) holder.Unreachable(error); }
    // Forgets what a denial left of holder on the current link: its fence is cancelled.
    private void Unfence(IUpstream holder)
    {
        if (released is null || !released.Remove(holder, out var cancel)) return;
        cancel?.Invoke();
    }
}
