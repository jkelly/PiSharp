using PiSharp.Chord.Link;

namespace PiSharp.Chord.Hosting.Workers;

/// <summary>Preserves the link's one protocol listener while its process owner also observes closure.</summary>
internal sealed class ObservedLink : ILink
{
    private readonly ILink inner;
    private readonly object sync = new();
    private Action? listener;
    private Action<bool>? observer;
    private bool closed, requested;
    internal ObservedLink(ILink inner) { this.inner = inner; inner.OnClose(Closed); }
    internal void Observe(Action action)
        => Observe(_ => action());
    internal void Observe(Action<bool> action)
    { bool ended, local; lock (sync) { observer = action; ended = closed; local = requested; } if (ended) action(local); }
    public void Send(ReadOnlyMemory<byte> message) => inner.Send(message);
    public void OnMessage(Action<ReadOnlyMemory<byte>> listener) => inner.OnMessage(listener);
    public void Close() { lock (sync) requested = true; inner.Close(); }
    public void OnClose(Action action)
    { bool ended; lock (sync) { listener = action; ended = closed; } if (ended) action(); }
    private void Closed()
    {
        Action? protocol; Action<bool>? owner; bool local;
        lock (sync) { if (closed) return; closed = true; protocol = listener; owner = observer; local = requested; listener = null; observer = null; }
        // Record process ownership before a routed holder synchronously asks for a replacement.
        try { owner?.Invoke(local); } finally { protocol?.Invoke(); }
    }
}
