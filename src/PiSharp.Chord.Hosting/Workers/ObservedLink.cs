// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/node/workers/process.ts `TellingLink`.
// A worker's link that also tells its process owner who closed it (this side or the child's), and that its owner can end silently at the
// child's exit (`End`, upstream `TellingLink.end`): the exit tells, so the close is told as neither a lost nor a closed link.
using PiSharp.Chord.Link;

namespace PiSharp.Chord.Hosting.Workers;

/// <summary>Preserves the link's one protocol listener while its process owner also observes closure.</summary>
internal sealed class ObservedLink : ILink
{
    private readonly ILink inner;
    private readonly object sync = new();
    private readonly TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action? listener;
    private Action<bool>? observer;
    private bool closed, requested, silent;
    internal ObservedLink(ILink inner) { this.inner = inner; inner.OnClose(Closed); }
    /// <summary>Completes once the link closed and its protocol listener returned (the host has handled the close: the calls the peer
    /// made are aborted and nothing it sent is handled any more).</summary>
    internal Task Ended => ended.Task;
    internal void Observe(Action action)
        => Observe(_ => action());
    internal void Observe(Action<bool> action)
    { bool done, local; lock (sync) { observer = action; done = closed && !silent; local = requested; } if (done) action(local); }
    public void Send(ReadOnlyMemory<byte> message) => inner.Send(message);
    public void OnMessage(Action<ReadOnlyMemory<byte>> listener) => inner.OnMessage(listener);
    public void Close() { lock (sync) requested = true; inner.Close(); }
    /// <summary>Closes the link without telling its owner: its child exited, which the exit tells. A link already closed stays as it was
    /// told. Wait for <see cref="Ended"/> to know the host has handled the close.</summary>
    internal void End()
    {
        lock (sync) { if (closed || requested) return; silent = true; }
        inner.Close();
    }
    public void OnClose(Action action)
    { bool done; lock (sync) { listener = action; done = closed; } if (done) action(); }
    private void Closed()
    {
        Action? protocol; Action<bool>? owner; bool local;
        lock (sync)
        {
            if (closed) return;
            closed = true; protocol = listener; owner = silent ? null : observer; local = requested; listener = null; observer = null;
        }
        // Record process ownership before a routed holder synchronously asks for a replacement.
        try { owner?.Invoke(local); }
        finally
        {
            try { protocol?.Invoke(); }
            finally { ended.TrySetResult(); }
        }
    }
}
