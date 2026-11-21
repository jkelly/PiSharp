// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/host/route-admission.ts.
// A route's admission (slice 14 §2.3): the decision a route asks before it resolves an address or forwards a hold, so no host starts for
// a caller who will be denied. One decision per principal key and address with holds on the route, asked after `Authorize`, shared by
// those holds and released with the last. It gates the address's path: a hold is held back until its decision allows it, released
// upstream by a denial (its calls rejected forbidden/revoked), and forwarded again by the next allow (behind the path's fence). A decision
// lives with its holds on the routing host and takes no fate in a reload; its references follow the reference rules.
//
// Native shape. A decision is declared in a later turn than the hold that asks it (posted to the host's dispatcher): a hold arrives inside
// a link's message turn or a getter's call, and a decision's references are acquired outside both (upstream binds them synchronously,
// which its JavaScript turn allows). The hold is loading meanwhile, as before any first verdict.
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Remote;

namespace PiSharp.Chord.Host;

/// <summary>The decisions of one route, by address key and principal key.</summary>
internal sealed class RouteAdmission
{
    // Where a decision stands. Settling: its verdict is being settled (its live queries open), and Moved says something it reads changed
    // meanwhile, so what the settle read is of the past. Waiting: the last settle found it not decided (a source loads or failed): the next
    // change settles again. Settled: it reached an allow or a denial once, so it is read synchronously from then on, in the flush of the
    // batch that changed it. Released: its last hold left.
    private enum Stage { Settling, Waiting, Settled, Released }

    // One decision and the holds it is asked for. Verdict is what its holds were last told: nothing before its first check (they are
    // loading: nothing is resolved or forwarded for them). Forwarded are the holds its last allow forwarded: a decision that cannot be read
    // (a failed source) leaves them alone, however often it fails; only a denial takes them back.
    private sealed class Asked(RemotePath path)
    {
        internal Decision? Decision;
        internal readonly RemotePath Path = path;
        internal readonly HashSet<IUpstream> Holders = [];
        internal readonly HashSet<IUpstream> Forwarded = [];
        internal Verdict? Verdict;
        internal Stage Stage = Stage.Settling;
        // The settle running now (a later one replaces it), and whether something it reads changed while it ran.
        internal object? Settle;
        internal bool Moved;
    }

    // A route's decision reports no long wait: its holds show what it waits on.
    private sealed class NoReport : IBlocked
    {
        internal static readonly NoReport Instance = new();
        public void BlockedBy(Blocker? blocker)
        {
        }
    }

    private sealed class PathGate(RouteAdmission admission, Address address, RemotePath path) : IPathGate
    {
        public bool Joined(IUpstream holder) => admission.Joined(address, path, holder);
        public void Left(IUpstream holder) => admission.Left(address, holder);
        public void Unlinked() => admission.Unlinked(path);
    }

    /// <summary>The route whose admission binds the references made under a context: a hold of a type placed behind that very route would
    /// wait on itself, so the route refuses it <c>invalid/cycle</c>.</summary>
    private static readonly ContextKey<object> AdmittingKey = new("chord.route.admitting");

    private readonly ChordHost host;
    // What a hold behind this very route is refused by: the route itself, the value of AdmittingKey.
    private readonly object route;
    private readonly RouteAdmit admit;
    // The route's context, with the route named as the one admitting.
    private readonly ChordContext context;
    private readonly Dictionary<string, Asked> asked = new(StringComparer.Ordinal);

    internal RouteAdmission(ChordHost host, object route, RouteAdmit admit, ChordContext context)
    {
        this.host = host; this.route = route; this.admit = admit;
        this.context = ChordContext.WithValue(AdmittingKey, route, context);
    }

    /// <summary>How many decisions it holds now (one per principal key and address with holds).</summary>
    internal int Decisions => asked.Count;

    /// <summary>The decisions it holds now (for the tests' weak references).</summary>
    internal Decision[] DecisionsNow => [.. asked.Values.Select(entry => entry.Decision).OfType<Decision>()];

    /// <summary>Whether <paramref name="holding"/> binds a reference of this route's own admission (which may hold nothing behind the
    /// route).</summary>
    internal bool Admitting(ChordContext holding) => ReferenceEquals(holding.Value(AdmittingKey), route);

    /// <summary>The gate of <paramref name="address"/>'s path.</summary>
    internal IPathGate Gate(Address address, RemotePath path) => new PathGate(this, address, path);

    // The path lost its link. A hold is forwarded on a new link only under an allow that can be read now (fail closed: a new link is new
    // work at the address's host, perhaps its start): the holds forwarded under a last allow whose source has failed since are taken back,
    // failed waiting on it, until the source recovers. A source that only loads keeps its allow.
    private void Unlinked(RemotePath path)
    {
        foreach (var each in asked.Values.ToArray())
        {
            if (!ReferenceEquals(each.Path, path) || each.Verdict is Verdict.Allowed) continue;
            foreach (var holder in each.Forwarded.ToArray())
            {
                each.Forwarded.Remove(holder);
                Tell(each, holder);
            }
        }
    }

    // A holder joined the address's path: its principal's decision is asked (once, however many holds share it); whether it allows now, so
    // the hold is forwarded at once. A decision that cannot be declared (a throwing admit, a refused request, a hold behind this route)
    // fails the hold broken.
    private bool Joined(Address address, RemotePath path, IUpstream holder)
    {
        // A hold routed over a route acts for a principal (HostRoute.Path refused one without).
        var principal = holder.Caller.Principal!;
        var key = Key(address, principal);
        if (asked.TryGetValue(key, out var found))
        {
            found.Holders.Add(holder);
            if (found.Verdict is Verdict.Allowed) { found.Forwarded.Add(holder); return true; }
            Tell(found, holder);
            return false;
        }
        var made = new Asked(path);
        made.Holders.Add(holder);
        asked.Add(key, made);
        // In a turn of its own: its references are acquired outside the turn that brought the hold (see the file comment).
        host.Dispatcher.Post(() => Declare(made, address, principal));
        return false;
    }

    // Declares the decision of one principal and address (its admit runs, its references are acquired), and settles it.
    private void Declare(Asked made, Address address, JsonObject principal)
    {
        if (made.Stage == Stage.Released || host.Closed) return;
        var decision = new Decision(new(host, $"route {address.Key}",
            // The admission's references act as the host.
            host.OwnCaller, null, () => Moved(made)));
        made.Decision = decision;
        try { decision.Declare(self => admit(principal, address, self), context); }
        catch (Exception error)
        {
            made.Stage = Stage.Waiting;
            made.Verdict = new Verdict.Undecided(new Parking.Broken(DataInternal.AsChordError(error, address.Key)));
            foreach (var holder in made.Holders.ToArray()) Tell(made, holder);
            return;
        }
        _ = SettleAsync(made);
    }

    // A holder left: its decision is released with its last hold.
    private void Left(Address address, IUpstream holder)
    {
        var key = Key(address, holder.Caller.Principal!);
        if (!asked.TryGetValue(key, out var found) || !found.Holders.Remove(holder)) return;
        found.Forwarded.Remove(holder);
        if (found.Holders.Count > 0) return;
        found.Stage = Stage.Released; found.Settle = null;
        asked.Remove(key);
        // In a turn of its own: a hold leaves inside a batch or a link's turn, and a reference is not released there.
        if (found.Decision is { } decision) host.Dispatcher.Post(decision.Release);
    }

    // The key of the decision for a principal at an address.
    private string Key(Address address, JsonObject principal) => $"{address.Key}\n{host.PrincipalKeyOf(principal, context)}";

    // Something a decision reads changed: read again at once when it decided before; remembered while it settles (what that settle read
    // is of the past); else its verdict is settled again.
    private void Moved(Asked moved)
    {
        switch (moved.Stage)
        {
            case Stage.Settled:
                Apply(moved, moved.Decision!.Now(context));
                return;
            case Stage.Settling:
                moved.Moved = true;
                return;
            case Stage.Waiting:
                _ = SettleAsync(moved);
                return;
            default:
                // Its last hold left while a source still told it: nobody is left to tell.
                return;
        }
    }

    // Brings a decision to a verdict (its live queries open). A verdict is told only when nothing it reads changed while it was settled: a
    // settle is asynchronous, and a denial arriving between its read and its continuation must not be answered with the allow it read
    // before (that would resolve the address, start its host and leave the hold live with nobody left to correct it). It settles again
    // until one is stable.
    private async Task SettleAsync(Asked settling)
    {
        var mine = new object();
        settling.Settle = mine; settling.Stage = Stage.Settling;
        for (;;)
        {
            settling.Moved = false;
            Verdict verdict;
            try { verdict = await settling.Decision!.SettleAsync(NoReport.Instance, context); }
            catch (Exception error) { verdict = new Verdict.Undecided(new Parking.Broken(DataInternal.AsChordError(error, "route admission"))); }
            // Released meanwhile, or another settle took over: nobody is told.
            if (settling.Stage != Stage.Settling || !ReferenceEquals(settling.Settle, mine)) return;
            if (settling.Moved) continue;
            settling.Settle = null;
            settling.Stage = verdict is Verdict.Allowed or Verdict.Denied ? Stage.Settled : Stage.Waiting;
            Apply(settling, verdict);
            return;
        }
    }

    // A decision says verdict now. An allow forwards every hold not forwarded yet; a denial takes every hold back (released upstream, its
    // calls rejected forbidden/revoked). A decision that cannot be made (a source failed or ended, a broken one) fails the holds held back
    // and leaves the ones bound on the path's link alone, at its first failure and at every later one; without a link (it dropped, its next
    // one is on its way) the forwarded ones are taken back too, since nothing is forwarded on a new link without an allow that can be
    // read. A source that only loads tells nobody (the last decision stands; before the first, the holds stay loading).
    private void Apply(Asked applied, Verdict verdict)
    {
        var before = applied.Verdict;
        if (ReferenceEquals(verdict, before)) return;
        applied.Verdict = verdict;
        if (verdict is Verdict.Allowed or Verdict.Denied)
            foreach (var holder in applied.Holders)
                if (holder.Span is { } span) ChordTelemetry.Admitted(span, verdict is Verdict.Allowed);
        if (verdict is Verdict.Allowed)
        {
            foreach (var holder in applied.Holders.ToArray())
            {
                if (!applied.Forwarded.Add(holder)) continue;
                applied.Path.Forward(holder);
            }
            return;
        }
        if (verdict is Verdict.Denied) applied.Forwarded.Clear();
        if (Held(verdict) is not { } cause) return;
        // Not bound anywhere: the holds forwarded under the last allow would be forwarded on the next link.
        if (applied.Path.Link is null) applied.Forwarded.Clear();
        // The same failure again (its source changed and still fails so): nothing new to tell the holds held back.
        var again = Held(before) is { } told && DataInternal.SameCause(told, cause);
        foreach (var holder in applied.Holders.ToArray())
        {
            if (applied.Forwarded.Contains(holder) || again && applied.Path.Withholds(holder)) continue;
            applied.Path.Withhold(holder, cause);
        }
    }

    // Tells a holder, held back, what holds it back now; nothing while its decision only loads.
    private static void Tell(Asked told, IUpstream holder)
    {
        if (Held(told.Verdict) is { } cause) told.Path.Withhold(holder, cause);
    }

    // What holds a hold back under a verdict: a denial, a source it waits on, a broken decision; nothing while it loads.
    private static FailureCause? Held(Verdict? verdict) => verdict switch
    {
        Verdict.Denied denied => new FailureCause(FailureKind.Denied, denied.Error),
        Verdict.Undecided { Parking: Parking.Broken broken } => new FailureCause(FailureKind.Broken, broken.Error),
        Verdict.Undecided { Parking: Parking.Parked { Shown.Cause: { } cause } } => cause,
        _ => null,
    };
}
