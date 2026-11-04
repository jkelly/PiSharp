// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/session/session.ts, in part: SyncObserver, GuardedObserver,
// guardObserver, ENDED, closeEnds, closeEndOf and observation, which Session.cs, Harness/View.cs and Harness/TaskGraph.cs attach with.
// Pi v1.5.0 sync, area observers (8b0caefc2, with v1.5.0's shapes): Session.observeDoc(), Harness.observeConversation() and
// Harness.observeTaskGraph() attach a host's Observer<T> behind GuardedObserver<T>, and the document, view and task graph watches are
// observers of the same sources (WatchObserver<T>).
//
// Adaptations:
// - Upstream's structural observer types are classes: SyncObserver<T> is what a source holds, a watch (WatchObserver<T>) or a guarded host
//   observer. closeEndOf's `instanceof Watch` is SyncObserver<T>.IsWatch, and the owner-ended functions a document source takes (endWatch,
//   endGuarded) are SyncObserver<T>.OwnerEnded.
// - A host's advance may return a task (Observer<T>'s second constructor), as upstream's may return a promise: a task that faults or is
//   cancelled is the observer's throw, after the fact (Session.Observe, upstream's Promise.resolve(result).catch).
// - The Session line is not one event loop: a host may detach from any thread, so the guard drops what it holds with one atomic exchange.
//   A call already running when another thread detaches still returns normally; no call starts once the detach returned.
// - toError is not needed: a .NET throw is always an Exception.
using PiSharp.Durable.Chord;

namespace PiSharp.Durable;

/// <summary>
/// A synchronous observer as a Durable source attaches it (upstream <c>SyncObserver</c>): a watch (<see cref="WatchObserver{T}"/>) or a
/// guarded host observer (<see cref="GuardedObserver{T}"/>). Sources call it on the Session line, outside every lock of theirs.
/// </summary>
internal abstract class SyncObserver<T>
{
    /// <summary>One change, unfolded.</summary>
    public abstract void Advance(WatchCall<T> call, DurableContext context);

    /// <summary>The end, once.</summary>
    public abstract void End(WatchEnd end);

    /// <summary>Whether the Session's close ends it as an app's watch (<c>ended</c>) rather than as an observer (upstream
    /// <c>closeEndOf</c>'s <c>instanceof Watch</c>).</summary>
    public virtual bool IsWatch => false;

    /// <summary>The observed owner ended (a task document's task) right after the call of its last change (upstream <c>endGuarded</c>):
    /// an observer ends <c>ended</c> at once.</summary>
    public virtual void OwnerEnded() => End(CloseEnds.OwnerEnd);
}

/// <summary>A Chord watch as a source's observer (upstream <c>createWatch</c>): its listener gets the calls folded and serialized.</summary>
internal sealed class WatchObserver<T>(Watch<T> watch) : SyncObserver<T>
{
    /// <summary>The watch.</summary>
    public Watch<T> Watch => watch;

    public override void Advance(WatchCall<T> call, DurableContext context) => watch.Advance(call, context);

    public override void End(WatchEnd end) => watch.End(end);

    public override bool IsWatch => true;

    /// <summary>A task document's watch delivers the retirement that ends its task, then ends (upstream <c>endWatch</c>).</summary>
    public override void OwnerEnded() => watch.EndAfterDelivery();
}

/// <summary>
/// A host's <see cref="Observer{T}"/> behind the rule for observers that run at a commit's publication (upstream <c>guardObserver</c>):
/// one that throws, or whose advance returns a task that faults, is reported, detached, and ended <see cref="WatchEndKind.ListenerError"/>
/// with the error, so the throw never reaches the publication; nothing reaches it after its detach or end, and from then on the guard
/// references neither it nor its source. The constructor's detach is the source's; <see cref="Detach"/> is the observation's.
/// </summary>
internal sealed class GuardedObserver<T> : SyncObserver<T>, IDetachable
{
    private Attached? attached;

    /// <param name="observer">The host's observer.</param>
    /// <param name="detach">The source's detach, run once.</param>
    /// <param name="report">Reports an error of the observer, with the context of the call that threw, if any.</param>
    public GuardedObserver(Observer<T> observer, Action detach, Action<Exception, DurableContext?> report) =>
        attached = new Attached(observer, detach, report);

    public override void Advance(WatchCall<T> call, DurableContext context)
    {
        if (Volatile.Read(ref attached) is not { } current) return;
        try
        {
            var result = current.Observer.Advance(call, context);
            // A task that faults is the observer's throw, after the fact.
            if (result is not null) Session.Observe(result, error => Fault(current, error, context));
        }
        catch (Exception error)
        {
            Fault(current, error, context);
        }
    }

    public override void End(WatchEnd end)
    {
        if (Release() is { } ending) Session.Contained(() => ending.Observer.End(end), error => ending.Report(error, null));
    }

    /// <summary>The observation's detach: idempotent.</summary>
    public void Detach() => Release();

    /// <summary>Detaches from the source once, and hands back what it was attached with.</summary>
    private Attached? Release()
    {
        var released = Interlocked.Exchange(ref attached, null);
        released?.Detach();
        return released;
    }

    /// <summary>Reports a throw of <paramref name="current"/>'s advance, then detaches and ends it, unless that happened meanwhile.</summary>
    private void Fault(Attached current, Exception error, DurableContext context)
    {
        current.Report(error, context);
        if (Release() is null) return;
        var end = WatchEnd.ListenerError(error);
        Session.Contained(() => current.Observer.End(end), endError => current.Report(endError, null));
    }

    private sealed record Attached(Observer<T> Observer, Action Detach, Action<Exception, DurableContext?> Report);
}

/// <summary>
/// How a Session's close ends what follows it (upstream <c>closeEnds</c>, decision "Slice 8 S1"), made once per close: an app's watch
/// <c>ended</c>; a synchronous observer (an adapter following the Session, which may open it again) <c>failed</c> with "session closed", so
/// it follows the next Session; <c>ended</c> stays for an observed owner that ended (<see cref="OwnerEnd"/>). A failed Session ends both
/// <c>failed</c> with its error.
/// </summary>
internal readonly record struct CloseEnds(WatchEnd Watch, WatchEnd Observer)
{
    /// <summary>The end of an observer whose observed owner ended (upstream <c>ENDED</c>).</summary>
    public static WatchEnd OwnerEnd => WatchEnd.Ended;

    /// <summary>The ends of one close; <paramref name="failure"/> is the error that failed the Session, if any. The "session closed" error
    /// is new each time.</summary>
    public static CloseEnds Of(Exception? failure)
    {
        if (failure is not null)
        {
            var failed = WatchEnd.Failed(failure);
            return new CloseEnds(failed, failed);
        }
        return new CloseEnds(WatchEnd.Ended, WatchEnd.Failed(new InvalidOperationException("session closed")));
    }

    /// <summary>The end these give a follower: a watch's, or an observer's (upstream <c>closeEndOf</c>).</summary>
    public WatchEnd For(bool isWatch) => isWatch ? Watch : Observer;
}
