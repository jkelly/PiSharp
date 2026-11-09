using System.IO.Pipes;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Chord.Link;
using PiSharp.Chord.Data;

internal static partial class Program
{
    private const string LinkIncarnation="0190f5a2-7c3e-7d4a-9b1c-2d3e4f5a6b7c";
    private static IEnumerable<(string,Func<Task>)> LinkWireCases()
    {
        yield return Case("link.wire.pinned-typescript-goldens",()=>
        {
            var root=Directory.GetCurrentDirectory();
            while(!File.Exists(Path.Combine(root,"tests/fixtures/chord-link-v150.json"))) root=Directory.GetParent(root)?.FullName ?? throw new Exception("fixture missing");
            var fixture=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"tests/fixtures/chord-link-v150.json")))!.AsObject();
            Equal("f9a26365568127461c47a07a37dcb42c140a3ac5",fixture["sourceSha"]!.GetValue<string>(),"source identity");
            foreach(var raw in fixture["rows"]!.AsArray())
            {
                var row=raw!.AsObject(); var id=row["id"]!.GetValue<int>();
                try
                {
                    var text=Convert.FromBase64String(row["text"]!.GetValue<string>()); var binary=Convert.FromBase64String(row["binary"]!.GetValue<string>());
                    var message=TextCodec.Instance.Decode(text); MessageCheck.Check(message);
                    Sequence(text,TextCodec.Instance.Encode(message),"text bytes "+id);
                    Sequence(binary,BinaryCodec.Instance.Encode(message),"binary bytes "+id);
                    MessageCheck.Check(BinaryCodec.Instance.Decode(binary));
                }
                catch(Exception error) { throw new InvalidOperationException("Pinned wire golden row "+id,error); }
            }
        });
        yield return Case("link.wire.malformed-binary",()=>
        {
            byte[][] values=[[],[255],[13,0],[2,128],[3,0,0,0,2],[1,0,0,1],[2,255,255,255,255,255,255,255,127], [5,255,255,127], [6,1,0,0,255,255,127]];
            foreach(var value in values) Throws<ArgumentException>(()=>BinaryCodec.Instance.Decode(value),Convert.ToHexString(value));
            Throws<DecoderFallbackException>(()=>BinaryCodec.Instance.Decode(new byte[]{1,0,1,255}),"invalid UTF8");
        });
        yield return Case("link.wire.malformed-text-and-envelope",()=>
        {
            string[] values=["[]","{}","[\"unknown\"]","[\"@\",1,[\"@\",2,[\"a\"]]]","[\"r\",0,[9,0]]","[\"r\",0,[1,\"%\"]]","[\"r\",0,[0,0],[1]]","[\"p\"]\nx"];
            foreach(var value in values) Throws<ArgumentException>(()=>TextCodec.Instance.Decode(Encoding.UTF8.GetBytes(value)),value);
            string[] badEnvelope=["[\"@\",0,[\"a\"]]","[\"@\",1,[\"p\"]]","[\"C\"]","[\"T\",0,17]","[\"R\",-1]","[\"h\",1,\"wrong\",0]","[\"e\",0,{\"kind\":\"plugin\",\"code\":\"a\",\"message\":\"m\",\"stack\":\"private\"}]"];
            foreach(var value in badEnvelope) Throws<ArgumentException>(()=>MessageCheck.Check(TextCodec.Instance.Decode(Encoding.UTF8.GetBytes(value))),value);
        });
        yield return Case("link.parts.bounds-unicode-and-segments",()=>
        {
            Equal(16L*1024*1024,Limits.ForLink(false).Part,"untrusted wire default"); Equal(long.MaxValue,Limits.ForLink(true).Decoded,"trusted decoded default");
            var unicode=new Part.Text("é😀\ud800"); Equal(9L,Parts.Size(unicode),"UTF8 replacement size");
            Throws<ChordError>(()=>Parts.Open(unicode,new Limits(8,100)),"wire bound");
            Throws<ChordError>(()=>Parts.Open(unicode,new Limits(100,8)),"decoded bound");
            var payload=Parts.Payload(new Dictionary<string,object?> { ["b"]=new byte[]{0,10,255},["n"]=null },true);
            Equal("{\"b\":{\"$bytes\":0},\"n\":null}",payload.Json,"segment marker");
            var parsed=(Dictionary<string,object?>)Parts.ParsePayload(new Part.Text(payload.Json),payload.Segments)!;
            Sequence(new byte[]{0,10,255},((ReadOnlyMemory<byte>)parsed["b"]!).ToArray(),"segment resolves");
            var nodePayload=Parts.Payload(new JsonObject { ["b"]=JsonValue.Create(new byte[]{1,2,3}) },true);
            Equal("{\"b\":{\"$bytes\":0}}",nodePayload.Json,"byte JsonValue becomes segment");
            Sequence(new byte[]{1,2,3},nodePayload.Segments![0].ToArray(),"byte JsonValue data");
            Throws<ChordError>(()=>Parts.Payload(new JsonObject { ["$bytes"]=0 },true),"reserved property");
            foreach(var bad in new[]{"{\"$bytes\":-1}","{\"$bytes\":0,\"x\":1}","{\"$bytes\":0.5}","{\"$bytes\":2}"})
                Throws<ArgumentException>(()=>Parts.ParsePayload(new Part.Text(bad),[new byte[]{1}]),"bad marker");
            Equal("{\"s\":\"\\ud800\"}",PiSharp.Chord.ChordJson.Stringify(Parts.Parse(new Part.Text("{\"s\":\"\\ud800\"}"))),"lone surrogate preserved");
        });
        yield return Case("link.parts.compression-and-bomb-limit",async()=>
        {
            var json="\""+new string('x',100000)+"\""; var part=Parts.Origin(json,NativeCompression.Instance);
            Check(part is Part.Deflated,"large compressible payload deflated");
            Equal(json,Parts.Text(Parts.Open(part,new Limits(10000,200000),NativeCompression.Instance)),"raw deflate roundtrip");
            Throws<ChordError>(()=>Parts.Open(part,new Limits(10000,99),NativeCompression.Instance),"bounded bomb");
            Equal("\"small\"",Parts.Text(Parts.Origin("\"small\"",NativeCompression.Instance)),"below threshold");
            var asyncPart=new Part.Deflated(((Part.Deflated)part).Value); await Parts.InflateAsync(asyncPart,200000,default);
            Equal(json,Parts.Text(Parts.Open(asyncPart,new Limits(10000,200000))),"async raw deflate roundtrip");
            var canceled=new Part.Deflated(((Part.Deflated)part).Value); using var stop=new CancellationTokenSource(); stop.Cancel();
            await Parts.InflateAsync(canceled,200000,stop.Token); Check(canceled.Inflated is null,"canceled inflate leaves no outcome");
            var active=new Part.Deflated(NativeCompression.Instance.Deflate(new byte[10*1024*1024])); using var midStop=new CancellationTokenSource();
            var inflating=Parts.InflateAsync(active,20*1024*1024,midStop.Token); midStop.Cancel(); await inflating;
            Check(active.Inflated is null,"close cancels active off-dispatcher inflate");
            var corruptAsync=new Part.Deflated(new byte[]{255}); await Parts.InflateAsync(corruptAsync,100,default);
            Check(corruptAsync.Inflated is InflateResult.Corrupt,"async corruption retained as outcome");
            Check(NativeCompression.Instance.Inflate(new byte[]{255},100) is InflateResult.Corrupt,"corrupt deflate");
            Check(NativeCompression.Instance.Inflate(Array.Empty<byte>(),100) is InflateResult.Corrupt,"empty deflate is truncated");
            var complete=NativeCompression.Instance.Deflate(Encoding.UTF8.GetBytes("hello hello hello"));
            Check(NativeCompression.Instance.Inflate(complete.AsMemory(0,complete.Length-1),100) is InflateResult.Corrupt,"missing final block bits");
            Check(NativeCompression.Instance.Inflate(NativeCompression.Instance.Deflate(new byte[]{1}),0) is InflateResult.TooLarge,"zero decoded limit");
            Check(NativeCompression.Instance.Inflate(NativeCompression.Instance.Deflate(Array.Empty<byte>()),0) is InflateResult.Bytes {Value.Length:0},"zero limit allows empty output");
        });
        yield return Case("link.memory.backlog-replacement-close",async()=>
        {
            var (a,b)=MemoryLinkPair.Create(); var values=new List<byte>();
            a.Send(new byte[]{1}); a.Send(new byte[]{2}); await Task.Delay(30);
            b.OnMessage(m=>values.Add(m.Span[0])); b.OnMessage(m=>values.Add((byte)(m.Span[0]*2))); a.Send(new byte[]{3});
            await WaitLink(()=>values.Count==3); Sequence(new byte[]{1,2,6},values,"backlog + replaced listener");
            var closes=0; b.OnClose(()=>closes++); a.Close(); await WaitLink(()=>closes==1);
            b.OnClose(()=>closes++); b.Close(); a.Send(new byte[]{4}); await Task.Delay(10); Equal(2,closes,"late close once");
            Sequence(new byte[]{1,2,6},values,"closed drops message");
        });
        yield return Case("link.memory.close-during-backlog",async()=>
        {
            var (a,b)=MemoryLinkPair.Create(); a.Send(new byte[]{1}); a.Send(new byte[]{2}); await Task.Delay(30);
            var count=0; b.OnMessage(_=> { count++; b.Close(); }); Equal(1,count,"remaining backlog dropped");
        });
        yield return Case("link.channel.handshake-queued-send-order",async()=>
        {
            var(a,b)=MemoryLinkPair.Create(); var errors=new List<Exception>(); var ah=new LinkHandler(); var bh=new LinkHandler();
            var ca=new Channel(a,ah,new(BinaryCodec.Instance,LinkIncarnation,256,false,errors.Add));
            var cb=new Channel(b,bh,new(BinaryCodec.Instance,LinkIncarnation,12,false,errors.Add));
            ca.Send(new ReleaseMessage(1)); ca.Start(); cb.Start();
            await WaitLink(()=>bh.Received.Count==1); Equal(12L,ca.PeerCalls,"peer calls"); Equal(1,ah.OpenCount,"opened once");
            Equal(1L,((ReleaseMessage)bh.Received[0]).Hold,"queued sends after hello"); Equal(0,errors.Count,"no protocol errors"); ca.Close();
        });
        yield return Case("link.channel.header-mismatch-duplicate-missing",async()=>
        {
            foreach(var first in new Message[]{new HelloMessage(4,LinkIncarnation,1),new PingMessage(),new HelloMessage(1,LinkIncarnation,1)})
            {
                var(a,b)=MemoryLinkPair.Create(); var errors=new List<Exception>(); var handler=new LinkHandler();
                var channel=new Channel(a,handler,new(BinaryCodec.Instance,LinkIncarnation,256,false,errors.Add)); channel.Start();
                b.Send(BinaryCodec.Instance.Encode(first));
                if(first is HelloMessage {Protocol:1}) { await WaitLink(()=>channel.Opened); b.Send(BinaryCodec.Instance.Encode(first)); }
                await WaitLink(()=>channel.Closed); Equal(1,errors.Count,"one handshake failure"); Equal(1,handler.CloseCount,"closed once");
            }
        });
        yield return Case("link.channel.async-order-and-close-cancellation",async()=>
        {
            var(a,b)=MemoryLinkPair.Create(); var errors=new List<Exception>(); var handler=new LinkHandler(); var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.OnMessage=async _=>await gate.Task; var ca=new Channel(a,handler,new(BinaryCodec.Instance,LinkIncarnation,256,false,errors.Add)); ca.Start();
            b.Send(BinaryCodec.Instance.Encode(new HelloMessage(1,LinkIncarnation,2))); await WaitLink(()=>ca.Opened);
            b.Send(BinaryCodec.Instance.Encode(new ReleaseMessage(1))); b.Send(BinaryCodec.Instance.Encode(new ReleaseMessage(2)));
            await WaitLink(()=>handler.Received.Count==1); await Task.Delay(20); Equal(1,handler.Received.Count,"second waits for async first");
            gate.SetResult(); await WaitLink(()=>handler.Received.Count==2); ca.Close(); Check(ca.ClosedToken.IsCancellationRequested,"close cancels inflate token");
            var(c,d)=MemoryLinkPair.Create(); var stopGate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var ch=new LinkHandler { OnMessage=async _=>await stopGate.Task };
            var cc=new Channel(c,ch,new(BinaryCodec.Instance,LinkIncarnation,2,false,errors.Add)); cc.Start(); d.Send(BinaryCodec.Instance.Encode(new HelloMessage(1,LinkIncarnation,1))); await WaitLink(()=>cc.Opened);
            d.Send(BinaryCodec.Instance.Encode(new ReleaseMessage(1))); d.Send(BinaryCodec.Instance.Encode(new ReleaseMessage(2))); await WaitLink(()=>ch.Received.Count==1);
            cc.Close(); stopGate.SetResult(); await Task.Delay(20); Equal(1,ch.Received.Count,"close drops queued async followup");
        });
        yield return Case("link.channel.trusted-still-checks-framing",async()=>
        {
            var(a,b)=MemoryLinkPair.Create(); var errors=new List<Exception>(); var handler=new LinkHandler(); var channel=new Channel(a,handler,new(TextCodec.Instance,LinkIncarnation,1,true,errors.Add)); channel.Start();
            b.Send(TextCodec.Instance.Encode(new HelloMessage(1,LinkIncarnation,1))); await WaitLink(()=>channel.Opened);
            b.Send(Encoding.UTF8.GetBytes("[\"R\",-1]")); await WaitLink(()=>handler.Received.Count==1); Equal(-1L,((ReleaseMessage)handler.Received[0]).Hold,"trusted skips envelope");
            b.Send(Encoding.UTF8.GetBytes("[\"R\"]")); await WaitLink(()=>channel.Closed); Equal(1,errors.Count,"framing still enforced");
        });
        yield return Case("link.channel.heartbeat-deterministic",()=>
        {
            var clock=new LinkClock(); var link=new ScriptLink(); var handler=new LinkHandler();
            var channel=new Channel(link,handler,new(BinaryCodec.Instance,LinkIncarnation,1,false,_=>throw new Exception("unexpected protocol failure"),clock));
            channel.Start(); link.Push(BinaryCodec.Instance.Encode(new HelloMessage(1,LinkIncarnation,2)));
            channel.Tick(14999,15000,30000); Equal(1,link.Sent.Count,"no early ping");
            channel.Tick(15000,15000,30000); channel.Tick(16000,15000,30000); Equal(2,link.Sent.Count,"one ping per silence");
            Check(BinaryCodec.Instance.Decode(link.Sent[1]) is PingMessage,"heartbeat ping");
            clock.Now=16000; link.Push(BinaryCodec.Instance.Encode(new PongMessage()));
            channel.Tick(30999,15000,30000); Check(!channel.Closed,"incoming traffic resets silence");
            channel.Tick(46000,15000,30000); Check(channel.Closed,"silent link closes"); Equal(1,handler.CloseCount,"close callback once");
            var never=new ScriptLink(); var greeting=new Channel(never,new LinkHandler(),new(BinaryCodec.Instance,LinkIncarnation,1,false,_=>{} ,new LinkClock())); greeting.Start();
            greeting.Tick(30000,15000,30000); Check(greeting.Closed,"handshake timeout closes too");
        });
        yield return Case("link.channel.backlog-and-already-closed",()=>
        {
            var link=new ScriptLink(); link.Push(BinaryCodec.Instance.Encode(new HelloMessage(1,LinkIncarnation,2)));
            var handler=new LinkHandler(); var channel=new Channel(link,handler,new(BinaryCodec.Instance,LinkIncarnation,1,false,_=>{}));
            channel.Start(); Equal(1,handler.OpenCount,"early hello in backlog");
            var closed=new ScriptLink(); closed.Close(); var late=new LinkHandler();
            var ch=new Channel(closed,late,new(BinaryCodec.Instance,LinkIncarnation,1,false,_=>{})); ch.Start();
            Equal(1,late.CloseCount,"already closed reports close"); Equal(0,late.OpenCount,"closed link does not open");
        });
        yield return Case("link.channel.async-rejection-closes-once",async()=>
        {
            var link=new ScriptLink(); var errors=new List<Exception>();
            var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler=new LinkHandler { OnMessage=async _=>await gate.Task };
            var channel=new Channel(link,handler,new(BinaryCodec.Instance,LinkIncarnation,1,false,errors.Add)); channel.Start();
            link.Push(BinaryCodec.Instance.Encode(new HelloMessage(1,LinkIncarnation,1))); link.Push(BinaryCodec.Instance.Encode(new ReleaseMessage(1)));
            link.Push(BinaryCodec.Instance.Encode(new PingMessage())); Equal(1,link.Sent.Count,"ping waits behind asynchronous message");
            gate.SetException(new InvalidDataException("inflate failed")); await WaitLink(()=>channel.Closed);
            Equal(1,handler.CloseCount,"one close"); Equal(1,errors.Count,"one report"); Equal(1,link.Sent.Count,"queued ping dropped on failure");
        });
        yield return Case("link.transports.websocket-and-node-failures",()=>
        {
            var socket=new FakeWebSocket(); var link=new WebSocketLink(socket); var closed=0; link.OnClose(()=>closed++);
            link.Send(new byte[]{1}); Equal(0,socket.Sent.Count,"connecting buffered"); socket.Open(); Equal(1,socket.Sent.Count,"open flush");
            socket.Receive("text"); Equal(1,closed,"text closes"); Equal(1,socket.CloseCount,"socket closed");
            var midSocket=new FakeWebSocket(); var mid=new WebSocketLink(midSocket); mid.Send(new byte[]{1}); mid.Send(new byte[]{2});
            midSocket.OnSend=midSocket.Close; midSocket.Open(); Equal(1,midSocket.Sent.Count,"close during flush drops remaining sends");
            var throwing=new FakeWebSocket(); var throwsLink=new WebSocketLink(throwing); throwing.Open(); throwing.ThrowSend=true;
            throwsLink.Send(new byte[]{1}); Check(throwsLink.Closed,"throwing WebSocket send closes");
            var errored=new FakeWebSocket(); var errorLink=new WebSocketLink(errored); errored.Fail(); Check(errorLink.Closed,"WebSocket error closes");
            var node=new FakeIpc(); var ipc=new NodeIpcLink(node); var count=0; ipc.OnMessage(_=>count++); node.Receive("application start"); node.Receive(new byte[]{1}); Equal(1,count,"nonbytes ignored");
            ipc.Send(new byte[]{1}); Check(!ipc.Closed,"backpressure not failure"); node.Fail=true; ipc.Send(new byte[]{1}); Check(ipc.Closed,"callback error closes");
            var gone=new FakeIpc(); gone.Disconnect(); var disconnected=new NodeIpcLink(gone); var late=0; disconnected.OnClose(()=>late++); Equal(1,late,"already disconnected IPC closes");
        });
        yield return Case("link.transports.framed-pipe-duplex",async()=>
        {
            var name="pisharp-link-"+Guid.NewGuid().ToString("N"); using var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
            using var client=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
            await Task.WhenAll(server.WaitForConnectionAsync(),client.ConnectAsync()); using var a=new FramedPipeLink(server,server); using var b=new FramedPipeLink(client,client);
            var received=new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously); b.OnMessage(m=>received.TrySetResult(m.ToArray())); a.Send(new byte[]{0,10,255});
            Sequence(new byte[]{0,10,255},await received.Task.WaitAsync(TimeSpan.FromSeconds(5)),"framed pipe bytes");
        });
        yield return Case("link.transports.framed-pipe-bad-length",async()=>
        {
            foreach(var bytes in new byte[][] { [0,0,0,5,1,2], [0,0,0], [0,1,0,0] })
            {
                using var input=new MemoryStream(bytes); using var output=new MemoryStream(); using var link=new FramedPipeLink(input,output,16);
                await WaitLink(()=>link.Closed); Check(link.Closed,"truncation or oversized header closes");
            }
        });
        yield return Case("link.transports.websocket-pending-byte-cap",async()=>
        {
            using var socket=new BlockedWebSocket(); var link=WebSocketLink.FromWebSocket(socket,1024,4);
            link.Send(new byte[10]); await socket.Sending.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!link.Closed,"active frame is excluded from waiting byte cap");
            link.Send(new byte[4]); Check(!link.Closed,"exact queued cap accepted");
            link.Send(new byte[1]); Check(link.Closed,"queued overflow closes slow socket");
        });
    }
    private static async Task WaitLink(Func<bool> ready)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while(!ready()) await Task.Delay(5,timeout.Token);
    }
    private sealed class LinkHandler : IChannelHandler
    {
        internal readonly List<Message> Received=[];
        internal int OpenCount,CloseCount;
        internal Func<Message,ValueTask>? OnMessage;
        public void Opened()=>OpenCount++;
        public ValueTask MessageAsync(Message m) { Received.Add(m); return OnMessage?.Invoke(m) ?? ValueTask.CompletedTask; }
        public void Closed()=>CloseCount++;
    }
    private sealed class FakeWebSocket : IWebSocketPort
    {
        public int ReadyState {get;private set;}
        public event Action? Opened; public event Action<object?>? Message; public event Action? Closed; public event Action? Error;
        internal List<byte[]> Sent=[]; internal int CloseCount;
        internal bool ThrowSend;
        internal Action? OnSend;
        public void Send(ReadOnlyMemory<byte> m) { if(ThrowSend) throw new IOException("send failed"); Sent.Add(m.ToArray()); OnSend?.Invoke(); }
        public void Close() {CloseCount++; ReadyState=3; Closed?.Invoke();}
        internal void Open(){ReadyState=1;Opened?.Invoke();}
        internal void Receive(object m)=>Message?.Invoke(m);
        internal void Fail()=>Error?.Invoke();
    }
    private sealed class FakeIpc : INodeIpcPort
    {
        public bool Connected {get;private set;}=true;
        public event Action<object?>? Message; public event Action? Disconnected;
        internal bool Fail;
        public bool Send(ReadOnlyMemory<byte> m,Action<Exception?> completed){completed(Fail ? new IOException("send failed") : null);return false;}
        public void Disconnect(){Connected=false;Disconnected?.Invoke();}
        internal void Receive(object m)=>Message?.Invoke(m);
    }
    private sealed class ScriptLink : LinkEnd
    {
        internal List<byte[]> Sent=[];
        public override void Send(ReadOnlyMemory<byte> bytes) { if(!Closed) Sent.Add(bytes.ToArray()); }
        public override void Close()=>Ended();
        internal void Push(ReadOnlyMemory<byte> bytes)=>Deliver(bytes);
    }
    private sealed class LinkClock : TimeProvider
    {
        internal long Now;
        public override DateTimeOffset GetUtcNow()=>DateTimeOffset.FromUnixTimeMilliseconds(Now);
    }
    private sealed class BlockedWebSocket : WebSocket
    {
        private WebSocketState _state=WebSocketState.Open;
        internal readonly TaskCompletionSource Sending=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _blocked=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override WebSocketCloseStatus? CloseStatus=>null;
        public override string? CloseStatusDescription=>null;
        public override WebSocketState State=>_state;
        public override string? SubProtocol=>null;
        public override void Abort(){_state=WebSocketState.Aborted;}
        public override void Dispose()=>Abort();
        public override Task CloseAsync(WebSocketCloseStatus closeStatus,string? statusDescription,CancellationToken cancellationToken) {Abort();return Task.CompletedTask;}
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus,string? statusDescription,CancellationToken cancellationToken) {Abort();return Task.CompletedTask;}
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,CancellationToken cancellationToken)
        {await _blocked.Task.WaitAsync(cancellationToken);return new(0,WebSocketMessageType.Close,true);}
        public override async Task SendAsync(ArraySegment<byte> buffer,WebSocketMessageType messageType,bool endOfMessage,CancellationToken cancellationToken)
        {Sending.TrySetResult();await _blocked.Task.WaitAsync(cancellationToken);}
    }
}
