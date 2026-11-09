// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 next/link/link.ts.
namespace PiSharp.Chord.Link;

/// <summary>An ordered, bidirectional channel of complete byte messages. One listener of each kind is retained.</summary>
public interface ILink
{
    /// <summary>Sends one message; a no-op after close. The caller must not mutate the memory while sending.</summary>
    void Send(ReadOnlyMemory<byte> message);
    /// <summary>Replaces the listener and delivers earlier messages first. Closed links retain no message listener.</summary>
    void OnMessage(Action<ReadOnlyMemory<byte>> listener);
    /// <summary>Closes both ends; undelivered messages are discarded.</summary>
    void Close();
    /// <summary>Replaces the close listener; invoked immediately if this end is already closed.</summary>
    void OnClose(Action listener);
}

/// <summary>Common listener, backlog and idempotent closure semantics for byte transports.</summary>
public abstract class LinkEnd : ILink
{
    private readonly object _gate = new();
    private readonly Queue<ReadOnlyMemory<byte>> _queued = new();
    private Action<ReadOnlyMemory<byte>>? _listener;
    private Action? _closeListener;
    private bool _closed, _draining;
    public bool Closed { get { lock(_gate) return _closed; } }
    public abstract void Send(ReadOnlyMemory<byte> message);
    public abstract void Close();
    public void OnMessage(Action<ReadOnlyMemory<byte>> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock(_gate) { if(_closed) return; _listener=listener; }
        Drain();
    }
    public void OnClose(Action listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        bool closed; lock(_gate) { closed=_closed; if(!closed) _closeListener=listener; }
        if(closed) listener();
    }
    protected void Deliver(ReadOnlyMemory<byte> message)
    {
        lock(_gate) { if(_closed) return; _queued.Enqueue(message); }
        Drain();
    }
    private void Drain()
    {
        lock(_gate) { if(_draining || _closed) return; _draining=true; }
        try
        {
            while(true)
            {
                Action<ReadOnlyMemory<byte>>? listener; ReadOnlyMemory<byte> message;
                lock(_gate)
                {
                    if(_closed || _listener is null || _queued.Count==0) { _draining=false; return; }
                    listener=_listener; message=_queued.Dequeue();
                }
                listener(message);
            }
        }
        catch { lock(_gate) _draining=false; throw; }
    }
    protected void Ended()
    {
        Action? listener;
        lock(_gate)
        {
            if(_closed) return;
            _closed=true; listener=_closeListener; _listener=null; _closeListener=null; _queued.Clear();
        }
        listener?.Invoke();
    }
}

/// <summary>Creates asynchronously delivered in-memory links, with FIFO delivery in both directions.</summary>
public static class MemoryLinkPair
{
    public static (ILink First, ILink Second) Create()
    {
        var dispatch=new AsyncFifo(); var a=new MemoryLink(dispatch); var b=new MemoryLink(dispatch); a.Peer=b; b.Peer=a; return (a,b);
    }
    private sealed class MemoryLink(AsyncFifo dispatch) : LinkEnd
    {
        internal MemoryLink? Peer;
        public override void Send(ReadOnlyMemory<byte> message)
        {
            var peer=Peer; if(Closed || peer is null) return;
            // A transport owns each sent message after this call.
            var owned=message.ToArray(); dispatch.Post(()=>peer.Deliver(owned));
        }
        public override void Close()
        {
            var peer=Peer; if(Closed) return; Peer=null; Ended();
            if(peer is not null) dispatch.Post(()=> { peer.Peer=null; peer.Ended(); });
        }
    }
}

internal sealed class AsyncFifo
{
    private readonly object _gate=new();
    private readonly Queue<Action> _queue=new();
    private bool _scheduled;
    internal void Post(Action work)
    {
        lock(_gate) { _queue.Enqueue(work); if(_scheduled) return; _scheduled=true; }
        ThreadPool.QueueUserWorkItem(_=>Drain());
    }
    private void Drain()
    {
        while(true)
        {
            Action work; lock(_gate) { if(_queue.Count==0) { _scheduled=false; return; } work=_queue.Dequeue(); }
            // User handlers are normally Channel, which reports protocol failures. Keep one throwing listener from
            // stranding the other queued transport notifications or crashing the ThreadPool process.
            try { work(); } catch { }
        }
    }
}
