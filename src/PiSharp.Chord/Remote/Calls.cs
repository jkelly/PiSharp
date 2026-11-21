// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/remote/calls.ts.
// The call window (decision "Call windows only for untrusted links"): at most the peer's N calls of one origin in flight on a link;
// further calls wait in one FIFO queue, sent in order as replies free slots. A peer that announced no window takes every call at once.
namespace PiSharp.Chord.Remote;

/// <summary>A call waiting for a slot: what sends it once it has one.</summary>
internal interface IQueuedCall
{
    void Send();
}

/// <summary>
/// The calls of one origin on one link: a link's consumer side for this host's own calls, or a relay's queue of one downstream origin's
/// calls towards one upstream link. <see cref="Opened"/> sets the peer's N from its <c>hello</c>; before it every call waits, and a peer
/// that announced none (a trusted link) gets every call at once.
/// </summary>
internal sealed class CallWindow<T> where T : class, IQueuedCall
{
    // The peer's N: 0 until its hello arrived, long.MaxValue when it announced no window.
    private long limit;
    private long inFlight;
    private readonly List<T> waiting = [];

    internal int Waiting => waiting.Count;
    internal long InFlight => inFlight;

    /// <summary>Sends <paramref name="call"/> now when a slot is free and nothing waits, otherwise queues it behind the earlier calls.</summary>
    internal void Submit(T call)
    {
        if (waiting.Count == 0 && inFlight < limit) { inFlight++; call.Send(); return; }
        waiting.Add(call);
    }

    /// <summary>The peer's window arrived with its <c>hello</c> (null: none announced): waiting calls go out.</summary>
    internal void Opened(long? calls) { limit = calls ?? long.MaxValue; Drain(); }

    /// <summary>A call in flight ended: its slot is free. A caller taking waiting calls out at the same time takes them out first, so the
    /// slot never sends one of them.</summary>
    internal void Freed() { inFlight--; Drain(); }

    /// <summary>Takes a waiting call out (its caller cancelled); whether it waited.</summary>
    internal bool Remove(T call) => waiting.Remove(call);

    /// <summary>Takes every waiting call <paramref name="matches"/> out and returns them, in order.</summary>
    internal List<T> RemoveWhere(Func<T, bool> matches)
    {
        var removed = new List<T>();
        for (var index = 0; index < waiting.Count;)
        {
            if (!matches(waiting[index])) { index++; continue; }
            removed.Add(waiting[index]); waiting.RemoveAt(index);
        }
        return removed;
    }

    /// <summary>Takes every waiting call out and returns them (the link closed).</summary>
    internal List<T> Clear() { var removed = waiting.ToList(); waiting.Clear(); return removed; }

    private void Drain()
    {
        while (inFlight < limit && waiting.Count > 0)
        {
            var call = waiting[0]; waiting.RemoveAt(0); inFlight++; call.Send();
        }
    }
}
