using PiSharp.Chord.Link;

namespace PiSharp.Chord.Hosting.Workers;

/// <summary>Preserves the link's one protocol listener while its process owner also observes closure.</summary>
internal sealed class ObservedLink : ILink
{
    private readonly ILink inner;
    private readonly object sync = new();
    private Action? listener, observer;
    private bool closed;
    internal ObservedLink(ILink inner) { this.inner = inner; inner.OnClose(Closed); }
    internal void Observe(Action action)
    { bool ended; lock (sync) { observer = action; ended = closed; } if (ended) action(); }
    public void Send(ReadOnlyMemory<byte> message) => inner.Send(message);
    public void OnMessage(Action<ReadOnlyMemory<byte>> listener) => inner.OnMessage(listener);
    public void Close() => inner.Close();
    public void OnClose(Action action)
    { bool ended; lock (sync) { listener = action; ended = closed; } if (ended) action(); }
    private void Closed()
    {
        Action? protocol, owner;
        lock (sync) { if (closed) return; closed = true; protocol = listener; owner = observer; listener = null; observer = null; }
        try { protocol?.Invoke(); } finally { owner?.Invoke(); }
    }
}
