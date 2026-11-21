// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/link/channel.ts.
// 1.6.0: the call window is optional on both sides (ChannelOptions.Calls null sends a hello without calls; PeerCalls is null when the
// peer announced none), pings and pongs are counted, and Fence(then) runs then at the pong of its own ping (slice 14 §2.3).
// Threads: upstream is single-threaded, so a ping's number and its place on the wire always agree. Here a ping's number is taken and its
// bytes are written under one lock (_pings), as is the flush of what waited for the handshake, so the nth ping written is ping n and a
// fence never runs at the pong of an earlier ping than its own. A fence's then runs where the channel handles what it receives (the
// link's delivery, as IChannelHandler's calls do), outside the channel's locks: its owner moves to its own thread as it does for messages.
using PiSharp.Chord.Data;
using PiSharp.Telemetry;

namespace PiSharp.Chord.Link;

internal interface IChannelHandler
{
    void Opened();
    ValueTask MessageAsync(Message message);
    void Closed();
}

/// <param name="Calls">How many concurrent calls of one origin this side accepts, sent in <c>hello</c>; null: no window.</param>
internal sealed record ChannelOptions(ICodec Codec, string Incarnation, long? Calls, bool Trusted, Action<Exception> Report,
    TimeProvider? TimeProvider = null, ChordContext? Context = null, ITelemetryMetrics? Metrics = null);

/// <summary>Framing/checks and ordered handshake only. Reference lifetimes and per-origin windows belong to its owner.</summary>
internal sealed class Channel
{
    private readonly ILink _link;
    private readonly IChannelHandler _handler;
    private readonly ChannelOptions _options;
    private readonly TimeProvider _time;
    private readonly object _gate=new();
    private readonly Queue<byte[]> _outgoing=new();
    private readonly Queue<ReadOnlyMemory<byte>> _incoming=new();
    private readonly CancellationTokenSource _closedToken=new();
    private readonly byte[] _hello;
    private readonly SchemaTelemetryCounter _bytes;
    private static readonly SpanAttributes Sent = new() { ["direction"] = "sent" };
    private static readonly SpanAttributes Received = new() { ["direction"] = "received" };
    private bool _started, _opened, _closed, _processing, _pinged;
    private string _peer="";
    private long? _peerCalls;
    // How many pings it sent and how many pongs it received: a fence waits for the pong of its own ping.
    private long _pingCount, _pongCount;
    // The fences waiting for their pong, oldest first; made by the first (most links never fence).
    private List<(long Ping, Action Then)>? _fences;
    // Held while a ping is numbered and written, and while the handshake's queue is flushed (see the file comment).
    private readonly object _pings=new();
    private long _lastReceived;
    internal Channel(ILink link,IChannelHandler handler,ChannelOptions options)
    {
        _link=link; _handler=handler; _options=options; _time=options.TimeProvider ?? TimeProvider.System;
        _bytes=ChordTelemetry.Metrics(options.Metrics ?? options.Context?.Telemetry.Metrics).Counter("chord.link.bytes");
        _lastReceived=_time.GetUtcNow().ToUnixTimeMilliseconds();
        var hello=new HelloMessage(DataInternal.ProtocolVersion,options.Incarnation,options.Calls);
        MessageCheck.Check(hello); _hello=options.Codec.Encode(hello);
    }
    internal string Peer { get { lock(_gate) return _peer; } }
    /// <summary>How many concurrent calls of one origin the peer accepts, from its <c>hello</c>: null when it announced no window, and
    /// before the channel is open (read it once <see cref="Opened"/>).</summary>
    internal long? PeerCalls { get { lock(_gate) return _opened && !_closed ? _peerCalls : null; } }
    internal bool Opened { get { lock(_gate) return _opened && !_closed; } }
    internal bool Closed { get { lock(_gate) return _closed; } }
    internal CancellationToken ClosedToken=>_closedToken.Token;
    internal void Start()
    {
        lock(_gate) { if(_started) return; _started=true; if(_closed) return; }
        Write(_hello); _link.OnMessage(Receive); _link.OnClose(Ended);
    }
    internal void Send(Message message)
    {
        lock(_gate) { if(_closed) return; }
        var bytes=_options.Codec.Encode(message);
        lock(_gate) { if(_closed) return; if(!_opened) { _outgoing.Enqueue(bytes); return; } }
        Write(bytes);
    }
    internal void Close() { try { _link.Close(); } finally { Ended(); } }
    internal void Tick(long now,long ping,long close) => Tick(now,0,ping,close);
    internal void Tick(long now,long paused,long ping,long close)
    {
        bool expire;
        lock(_pings)
        {
            bool send;
            lock(_gate)
            {
                if(_closed) return;
                if(paused>0) _lastReceived=Math.Min(_lastReceived+paused,now-ping);
                var silent=now-_lastReceived; expire=silent>=close; send=!expire && silent>=ping && !_pinged && _opened;
                if(send) { _pinged=true; _pingCount++; }
            }
            if(send) Write(_options.Codec.Encode(new PingMessage()));
        }
        if(expire) Close();
    }
    /// <summary>
    /// A fence (upstream <c>fence</c>, slice 14 §2.3): sends a <c>ping</c> and runs <paramref name="then"/> when its <c>pong</c> arrived. A
    /// link is ordered and the peer answers a <c>ping</c> after everything it handled before it, so whatever the peer sent before it saw the
    /// <c>ping</c> has arrived here by then. Pings and pongs are counted: a liveness <c>pong</c> in transit ends no fence. Returns what
    /// cancels it; a link that closes first runs none, and a fence asked of a closed channel never runs.
    /// </summary>
    internal Action Fence(Action then)
    {
        ArgumentNullException.ThrowIfNull(then);
        (long Ping, Action Then) fence;
        lock(_pings)
        {
            lock(_gate)
            {
                if(_closed) return static () => { };
                fence=(++_pingCount,then); (_fences??=[]).Add(fence);
            }
            Send(new PingMessage());
        }
        return () => { lock(_gate) _fences?.Remove(fence); };
    }
    /// <summary>A <c>pong</c> arrived: the fences of the pings up to it run, in order (one cancelled by an earlier one's run does not).</summary>
    private void Ponged()
    {
        lock(_gate) _pongCount++;
        while(true)
        {
            Action then;
            lock(_gate)
            {
                if(_closed || _fences is not { Count: >0 } fences || fences[0].Ping>_pongCount) return;
                then=fences[0].Then; fences.RemoveAt(0);
            }
            then();
        }
    }
    private void Receive(ReadOnlyMemory<byte> bytes)
    {
        lock(_gate)
        {
            if(_closed) return; _lastReceived=_time.GetUtcNow().ToUnixTimeMilliseconds(); _pinged=false;
            _bytes.Add(bytes.Length, Received);
            _incoming.Enqueue(bytes); if(_processing) return; _processing=true;
        }
        _=Drain();
    }
    private async Task Drain()
    {
        while(true)
        {
            ReadOnlyMemory<byte> bytes; bool greeting;
            lock(_gate)
            {
                if(_closed || _incoming.Count==0) { _processing=false; return; }
                bytes=_incoming.Dequeue(); greeting=!_opened;
            }
            try
            {
                Message message;
                try
                {
                    message=_options.Codec.Decode(bytes);
                    if(!_options.Trusted) { _options.Context?.Count("chord.link.checks"); MessageCheck.Check(message); }
                }
                catch(Exception error) when(greeting) { throw new ArgumentException($"handshake: the first message is no {_options.Codec.Name} message",error); }
                if(greeting)
                {
                    if(message is not HelloMessage hello) throw new ArgumentException($"handshake: {message.Kind} before hello");
                    if(hello.Protocol!=DataInternal.ProtocolVersion) throw new ArgumentException($"handshake: protocol {hello.Protocol}, this host speaks {DataInternal.ProtocolVersion}");
                    byte[][] queued;
                    lock(_pings)
                    {
                        lock(_gate) { if(_closed) return; _opened=true; _peer=hello.Incarnation; _peerCalls=hello.Calls; queued=_outgoing.ToArray(); _outgoing.Clear(); }
                        foreach(var pending in queued) { if(Closed) return; Write(pending); }
                    }
                    if(!Closed) _handler.Opened();
                }
                else
                {
                    if(message is HelloMessage) throw new ArgumentException("handshake: a second hello");
                    if(message is PingMessage) Write(_options.Codec.Encode(new PongMessage()));
                    else if(message is PongMessage) Ponged();
                    else if(!Closed) await _handler.MessageAsync(message);
                }
            }
            catch(Exception error) { Failed(error); return; }
        }
    }
    private void Failed(Exception error)
    {
        if(Closed) return;
        try { _options.Report(error); } finally { Close(); }
    }
    private void Write(ReadOnlyMemory<byte> bytes) { _bytes.Add(bytes.Length, Sent); _link.Send(bytes); }
    private void Ended()
    {
        lock(_gate) { if(_closed) return; _closed=true; _opened=false; _peer=""; _peerCalls=null; _fences=null; _incoming.Clear(); _outgoing.Clear(); }
        try { _closedToken.Cancel(); } finally { _handler.Closed(); }
    }
}
