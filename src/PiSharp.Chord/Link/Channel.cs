// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 next/link/channel.ts.
using PiSharp.Chord.Data;

namespace PiSharp.Chord.Link;

internal interface IChannelHandler
{
    void Opened();
    ValueTask MessageAsync(Message message);
    void Closed();
}

internal sealed record ChannelOptions(ICodec Codec, string Incarnation, long Calls, bool Trusted, Action<Exception> Report,
    TimeProvider? TimeProvider = null, ChordContext? Context = null);

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
    private bool _started, _opened, _closed, _processing, _pinged;
    private string _peer="";
    private long _peerCalls;
    private long _lastReceived;
    internal Channel(ILink link,IChannelHandler handler,ChannelOptions options)
    {
        _link=link; _handler=handler; _options=options; _time=options.TimeProvider ?? TimeProvider.System;
        _lastReceived=_time.GetUtcNow().ToUnixTimeMilliseconds();
        var hello=new HelloMessage(DataInternal.ProtocolVersion,options.Incarnation,options.Calls);
        MessageCheck.Check(hello); _hello=options.Codec.Encode(hello);
    }
    internal string Peer { get { lock(_gate) return _peer; } }
    internal long PeerCalls { get { lock(_gate) return _peerCalls; } }
    internal bool Opened { get { lock(_gate) return _opened && !_closed; } }
    internal bool Closed { get { lock(_gate) return _closed; } }
    internal CancellationToken ClosedToken=>_closedToken.Token;
    internal void Start()
    {
        lock(_gate) { if(_started) return; _started=true; if(_closed) return; }
        _link.Send(_hello); _link.OnMessage(Receive); _link.OnClose(Ended);
    }
    internal void Send(Message message)
    {
        lock(_gate) { if(_closed) return; }
        var bytes=_options.Codec.Encode(message);
        lock(_gate) { if(_closed) return; if(!_opened) { _outgoing.Enqueue(bytes); return; } }
        _link.Send(bytes);
    }
    internal void Close() { try { _link.Close(); } finally { Ended(); } }
    internal void Tick(long now,long ping,long close)
    {
        bool expire,send;
        lock(_gate)
        {
            if(_closed) return;
            var silent=now-_lastReceived; expire=silent>=close; send=!expire && silent>=ping && !_pinged && _opened;
            if(send) _pinged=true;
        }
        if(expire) Close(); else if(send) _link.Send(_options.Codec.Encode(new PingMessage()));
    }
    private void Receive(ReadOnlyMemory<byte> bytes)
    {
        lock(_gate)
        {
            if(_closed) return; _lastReceived=_time.GetUtcNow().ToUnixTimeMilliseconds(); _pinged=false;
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
                    lock(_gate) { if(_closed) return; _opened=true; _peer=hello.Incarnation; _peerCalls=hello.Calls; queued=_outgoing.ToArray(); _outgoing.Clear(); }
                    foreach(var pending in queued) { if(Closed) return; _link.Send(pending); }
                    if(!Closed) _handler.Opened();
                }
                else
                {
                    if(message is HelloMessage) throw new ArgumentException("handshake: a second hello");
                    if(message is PingMessage) _link.Send(_options.Codec.Encode(new PongMessage()));
                    else if(message is not PongMessage && !Closed) await _handler.MessageAsync(message);
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
    private void Ended()
    {
        lock(_gate) { if(_closed) return; _closed=true; _opened=false; _peer=""; _peerCalls=0; _incoming.Clear(); _outgoing.Clear(); }
        try { _closedToken.Cancel(); } finally { _handler.Closed(); }
    }
}
