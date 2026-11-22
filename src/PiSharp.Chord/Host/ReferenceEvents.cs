// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/host/reference-events.ts.
// A reference's event members (decision "Events: fire-and-forget members"): what a reference exposes per event member (OnAsync only, never
// Emit), what an object re-exposing one implements its member with, and a binding's taps on the instance it is bound to. Per member a
// binding keeps its subscriptions and the re-exposing nodes it feeds, tapped on the instance's node from the counter value of its binding
// to that instance; it moves them to every new instance it binds (an older version's mapping; a variant without the member: none), and
// keeps them untapped while it points at nothing. Nothing missed is replayed.
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;

namespace PiSharp.Chord.Host;

/// <summary>A reference's event member: its listeners follow the reference's binding.</summary>
internal sealed class ReferenceEvents(Binding binding, string member) : IEventMember
{
    internal Binding Binding => binding;
    internal string Member => member;
    public Task<IEventSubscription> OnAsync(Action<ChordEvent> listener, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(listener); ArgumentNullException.ThrowIfNull(context);
        return binding.SubscribeEvents(member, listener, context);
    }
}

/// <summary>An object's event member re-exposing a reference's (<see cref="ReferenceEvents"/>): the reference feeds it what it receives,
/// value and stamp unchanged, from the attempt's members check until its end; its own taps are the object's bindings.</summary>
internal sealed class ReexposedEvents(ReferenceEvents source) : EventNode
{
    /// <summary>Fed by its reference from now on.</summary>
    internal void Attach() => source.Binding.ForwardEvents(source.Member, this);

    /// <summary>Its attempt ended: no longer fed, no binding taps it.</summary>
    internal void Detach() { source.Binding.UnforwardEvents(source.Member, this); Clear(); }
}

/// <summary>A binding's taps: per event member used (a subscription, a re-exposing node), tapped on the node of the instance it points
/// at, at its version; a member nobody uses any more is untapped and forgotten.</summary>
internal sealed class ReferenceEventTaps(long version, (IEventInstance Instance, long BoundAt)? at)
{
    private sealed class MemberTaps
    {
        internal readonly List<Subscription> Subscriptions = [];
        internal readonly List<EventNode> Forwards = [];
        internal (EventNode Node, Tap Tap)? Attached;
    }
    private readonly Dictionary<string, MemberTaps> members = new(StringComparer.Ordinal);

    /// <summary>Its binding points at <paramref name="next"/>'s instance now, bound at its counter value (or at nothing): every member
    /// moves there.</summary>
    internal void Retarget((IEventInstance Instance, long BoundAt)? next)
    {
        at = next;
        foreach (var (member, taps) in members) Place(member, taps);
    }
    internal void Subscribe(string member, Subscription subscription) => Taps(member).Subscriptions.Add(subscription);
    internal void Unsubscribe(string member, Subscription subscription)
    {
        if (!members.TryGetValue(member, out var taps)) return;
        taps.Subscriptions.Remove(subscription); ForgetUnused(member, taps);
    }
    /// <summary>Feeds <paramref name="node"/> (an object re-exposing this member) what <paramref name="member"/> receives.</summary>
    internal void Forward(string member, EventNode node) { var taps = Taps(member); if (!taps.Forwards.Contains(node)) taps.Forwards.Add(node); }
    internal void Unforward(string member, EventNode node)
    {
        if (!members.TryGetValue(member, out var taps)) return;
        taps.Forwards.Remove(node); ForgetUnused(member, taps);
    }
    /// <summary>Its binding ended: every subscription stops, nothing is tapped or fed any more.</summary>
    internal void End()
    {
        var all = members.Values.ToArray(); members.Clear();
        foreach (var taps in all)
        {
            if (taps.Attached is { } attached) attached.Node.Untap(attached.Tap);
            foreach (var subscription in taps.Subscriptions.ToArray()) subscription.Stop();
        }
    }
    private MemberTaps Taps(string member)
    {
        if (!members.TryGetValue(member, out var taps)) { members.Add(member, taps = new()); Place(member, taps); }
        return taps;
    }
    // Taps member on the node of the instance pointed at (none: a variant without it, or nothing pointed at).
    private void Place(string member, MemberTaps taps)
    {
        if (taps.Attached is { } previous) previous.Node.Untap(previous.Tap);
        taps.Attached = null;
        if (at is not { } current || current.Instance.EventNode(member, version) is not { } node) return;
        var tap = new Tap(current.BoundAt, (entry, flush) =>
        {
            // One entry for a member: its listeners' calls, and the nodes re-exposing it, which deliver to their own bindings.
            foreach (var subscription in taps.Subscriptions) flush.Call(subscription, entry);
            for (var index = 0; index < taps.Forwards.Count; index++) taps.Forwards[index].Deliver(entry, flush);
        });
        node.Tap(tap); taps.Attached = (node, tap);
    }
    private void ForgetUnused(string member, MemberTaps taps)
    {
        if (taps.Subscriptions.Count > 0 || taps.Forwards.Count > 0) return;
        members.Remove(member);
        if (taps.Attached is { } attached) attached.Node.Untap(attached.Tap);
    }
}
