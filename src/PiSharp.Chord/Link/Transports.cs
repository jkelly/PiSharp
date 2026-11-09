using System.Buffers.Binary;
using System.Net.WebSockets;

namespace PiSharp.Chord.Link;

/// <summary>A complete binary message transport compatible with browser/Node WebSocket event adapters.</summary>
public interface IWebSocketPort
{
    int ReadyState { get; }
    event Action? Opened;
    event Action<object?>? Message;
    event Action? Closed;
    event Action? Error;
    void Send(ReadOnlyMemory<byte> message);
    void Close();
}

/// <summary>One binary WebSocket message per link message. Connecting sockets buffer outgoing messages.</summary>
public sealed class WebSocketLink : LinkEnd
{
    private readonly object _gate=new();
    private IWebSocketPort? _socket;
    private readonly Queue<ReadOnlyMemory<byte>> _sendQueue=new();
    private bool _connecting, _sending;
    public WebSocketLink(IWebSocketPort socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        if(socket.ReadyState is not (0 or 1)) { Ended(); return; }
        _socket=socket;
        _connecting=socket.ReadyState==0;
        socket.Opened+=OnOpen; socket.Message+=OnMessageEvent; socket.Closed+=OnSocketClose; socket.Error+=OnError;
    }
    private void OnOpen()
    {
        lock(_gate) _connecting=false;
        DrainSends();
    }
    private void OnMessageEvent(object? data)
    {
        if(data is ReadOnlyMemory<byte> memory) Deliver(memory);
        else if(data is byte[] bytes) Deliver(bytes);
        else Close();
    }
    public override void Send(ReadOnlyMemory<byte> message)
    {
        lock(_gate) { if(_socket is null) return; _sendQueue.Enqueue(message.ToArray()); }
        DrainSends();
    }
    private void DrainSends()
    {
        lock(_gate) { if(_sending || _connecting || _socket is null) return; _sending=true; }
        while(true)
        {
            IWebSocketPort socket; ReadOnlyMemory<byte> message;
            lock(_gate)
            {
                if(_socket is null || _connecting || _sendQueue.Count==0) { _sending=false; return; }
                socket=_socket; message=_sendQueue.Dequeue();
            }
            try { socket.Send(message); } catch { Close(); }
        }
    }
    public override void Close()
    {
        var socket=End(); if(socket is null) return;
        try { socket.Close(); } catch { OnSocketClose(); }
    }
    private void OnError() => End();
    private void OnSocketClose()
    {
        // Close/error handlers remain installed until actual socket close, including close while connecting.
        try { End(); }
        finally
        {
            var socket=_socketForClose; if(socket is not null) { socket.Closed-=OnSocketClose; socket.Error-=OnError; _socketForClose=null; }
        }
    }
    private IWebSocketPort? _socketForClose;
    private IWebSocketPort? End()
    {
        IWebSocketPort? socket; lock(_gate) { socket=_socket; if(socket is null) return null; _socket=null; _connecting=false; _sendQueue.Clear(); _socketForClose=socket; }
        socket.Opened-=OnOpen; socket.Message-=OnMessageEvent; Ended(); return socket;
    }
    /// <summary>Wraps an already connected .NET WebSocket. Closes on an oversized inbound message or more than
    /// <paramref name="maxQueuedBytes"/> waiting outbound bytes; the frame currently sending is excluded.</summary>
    public static WebSocketLink FromWebSocket(WebSocket socket, int maxMessageBytes=16*1024*1024, long maxQueuedBytes=4*1024*1024)
    {
        var port=new SystemWebSocketPort(socket,maxMessageBytes,maxQueuedBytes); var link=new WebSocketLink(port); port.Start(); return link;
    }

    private sealed class SystemWebSocketPort : IWebSocketPort
    {
        private readonly WebSocket _socket;
        private readonly int _max;
        private readonly long _maxQueued;
        private readonly CancellationTokenSource _stop=new();
        private readonly object _sendGate=new();
        private readonly Queue<byte[]> _pending=new();
        private bool _writing;
        private long _queuedBytes;
        public SystemWebSocketPort(WebSocket socket,int max,long maxQueued)
        {
            if(max<0) throw new ArgumentOutOfRangeException(nameof(max));
            if(maxQueued<0) throw new ArgumentOutOfRangeException(nameof(maxQueued));
            _socket=socket; _max=max; _maxQueued=maxQueued;
        }
        internal void Start() { _=Run(); }
        public int ReadyState=>_socket.State==WebSocketState.Open ? 1 : _socket.State==WebSocketState.Connecting ? 0 : 3;
        public event Action? Opened { add { } remove { } }
        public event Action<object?>? Message;
        public event Action? Closed;
        public event Action? Error;
        public void Send(ReadOnlyMemory<byte> message)
        {
            byte[]? first=null; bool overflow=false;
            lock(_sendGate)
            {
                if(_stop.IsCancellationRequested) throw new IOException("WebSocket closed");
                // The frame already sending is excluded from the pending-byte cap, as in next-http.
                if(!_writing) { _writing=true; first=message.ToArray(); }
                else if(message.Length>_maxQueued-_queuedBytes) overflow=true;
                else { _pending.Enqueue(message.ToArray()); _queuedBytes+=message.Length; }
            }
            if(overflow) Close(); else if(first is not null) _=Write(first);
        }
        public void Close()
        {
            lock(_sendGate) { _pending.Clear(); _queuedBytes=0; }
            if(_stop.IsCancellationRequested) return;
            _stop.Cancel(); _socket.Abort(); Closed?.Invoke();
        }
        private async Task Run()
        {
            // Ensure adapter subscribers are attached before a synchronously completed receive is delivered.
            await Task.Yield();
            try
            {
                var buffer=new byte[8192];
                while(!_stop.IsCancellationRequested)
                {
                    using var message=new MemoryStream(); ValueWebSocketReceiveResult frame;
                    do
                    {
                        frame=await _socket.ReceiveAsync(buffer.AsMemory(),_stop.Token).ConfigureAwait(false);
                        if(frame.MessageType==WebSocketMessageType.Close) return;
                        if(frame.MessageType!=WebSocketMessageType.Binary || frame.Count>_max-message.Length) { Close(); return; }
                        message.Write(buffer,0,frame.Count);
                    } while(!frame.EndOfMessage);
                    Message?.Invoke(message.ToArray());
                }
            }
            catch(Exception) when(_stop.IsCancellationRequested) { }
            catch { Error?.Invoke(); }
            finally { Close(); }
        }
        private async Task Write(byte[] first)
        {
            try
            {
                var bytes=first;
                while(true)
                {
                    await _socket.SendAsync(bytes.AsMemory(),WebSocketMessageType.Binary,true,_stop.Token).ConfigureAwait(false);
                    lock(_sendGate)
                    {
                        if(_pending.Count==0 || _stop.IsCancellationRequested) { _writing=false; return; }
                        bytes=_pending.Dequeue(); _queuedBytes-=bytes.Length;
                    }
                }
            }
            catch(Exception) when(_stop.IsCancellationRequested) { }
            catch { Error?.Invoke(); Close(); }
        }
    }
}

/// <summary>A Node advanced-serialization IPC bridge. Implementations must expose actual Uint8Array messages;</summary>
/// <remarks>This is deliberately distinct from a framed stream; Node child-process IPC is not stdio framing.</remarks>
public interface INodeIpcPort
{
    bool Connected { get; }
    bool Send(ReadOnlyMemory<byte> message, Action<Exception?> completed);
    event Action<object?>? Message;
    event Action? Disconnected;
    void Disconnect();
}
public sealed class NodeIpcLink : LinkEnd
{
    private INodeIpcPort? _port;
    public NodeIpcLink(INodeIpcPort port)
    {
        _port=port; if(!port.Connected) { End(); return; }
        port.Message+=Received; port.Disconnected+=End;
    }
    private void Received(object? message) { if(message is byte[] bytes) Deliver(bytes); else if(message is ReadOnlyMemory<byte> memory) Deliver(memory); }
    public override void Send(ReadOnlyMemory<byte> message)
    {
        var port=_port; if(port is null) return;
        try { port.Send(message,error=> { if(error is not null) End(); }); } catch { End(); }
    }
    public override void Close() { var port=_port; End(); if(port?.Connected==true) { try { port.Disconnect(); } catch { } } }
    private void End() { var port=Interlocked.Exchange(ref _port,null); if(port is not null) { port.Message-=Received; port.Disconnected-=End; } Ended(); }
}

/// <summary>A native stream transport: four-byte big-endian length, then exactly that many message bytes.</summary>
/// <remarks>Use the same framing at both ends. This is not Node's advanced-serialization IPC protocol.</remarks>
public sealed class FramedPipeLink : LinkEnd, IDisposable
{
    private readonly Stream _input,_output;
    private readonly bool _leaveOpen;
    private readonly int _max;
    private readonly CancellationTokenSource _stop=new();
    private readonly System.Threading.Channels.Channel<byte[]> _out=System.Threading.Channels.Channel.CreateUnbounded<byte[]>(new(){ SingleReader=true });
    public FramedPipeLink(Stream input,Stream output,int maxMessageBytes=16*1024*1024,bool leaveOpen=false)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(output);
        if(maxMessageBytes<0) throw new ArgumentOutOfRangeException(nameof(maxMessageBytes));
        _input=input; _output=output; _max=maxMessageBytes; _leaveOpen=leaveOpen; _=Read(); _=Write();
    }
    public override void Send(ReadOnlyMemory<byte> message)
    {
        if(Closed) return;
        if(message.Length>_max) { Close(); return; }
        _out.Writer.TryWrite(message.ToArray());
    }
    public override void Close()
    {
        if(Closed) return; Ended(); _stop.Cancel(); _out.Writer.TryComplete();
        if(!_leaveOpen) { _input.Dispose(); if(!ReferenceEquals(_input,_output)) _output.Dispose(); }
    }
    public void Dispose() => Close();
    private async Task Read()
    {
        await Task.Yield();
        try
        {
            var header=new byte[4];
            while(!_stop.IsCancellationRequested)
            {
                await _input.ReadExactlyAsync(header,_stop.Token).ConfigureAwait(false); var size=BinaryPrimitives.ReadUInt32BigEndian(header);
                if(size>_max) { Close(); return; }
                var bytes=new byte[(int)size]; await _input.ReadExactlyAsync(bytes,_stop.Token).ConfigureAwait(false); Deliver(bytes);
            }
        }
        catch(Exception e) when(e is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { Close(); }
    }
    private async Task Write()
    {
        try
        {
            var header=new byte[4];
            await foreach(var bytes in _out.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                BinaryPrimitives.WriteUInt32BigEndian(header,(uint)bytes.Length); await _output.WriteAsync(header,_stop.Token).ConfigureAwait(false);
                await _output.WriteAsync(bytes,_stop.Token).ConfigureAwait(false); await _output.FlushAsync(_stop.Token).ConfigureAwait(false);
            }
        }
        catch(Exception e) when(e is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { Close(); }
    }
}
