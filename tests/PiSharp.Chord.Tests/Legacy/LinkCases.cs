using System.Text.Json.Nodes;
using PiSharp.Chord.Legacy;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> LegacyLinkCases()
    {
        yield return Case("legacy.link.backlog-fifo-close", async () =>
        {
            var (a,b) = LegacyMemoryLink.Pair();
            a.Send(LegacyProtocol.Frame("ping", ("n", JsonValue.Create(1))));
            a.Send(LegacyProtocol.Frame("ping", ("n", JsonValue.Create(2))));
            await Task.Delay(30);
            var seen = new System.Collections.Concurrent.ConcurrentQueue<long>(); b.Receive(frame => seen.Enqueue(frame.Number("n")));
            await LegacyUntil(() => seen.Count == 2);
            Sequence(new long[] {1,2}, seen, "early frames retained in order");
            var closes = 0; var closed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            b.OnClose(reason => { Interlocked.Increment(ref closes); closed.TrySetResult(reason); }); a.Close("finished"); a.Close();
            Equal("finished", await closed.Task.WaitAsync(TimeSpan.FromSeconds(5)), "peer receives bye reason");
            Check(b.Closed, "peer closed before notifying"); b.Close("again"); Equal(1, Volatile.Read(ref closes), "close once");
            string? late = null; b.OnClose(reason => late = reason); Equal("closed", late, "late close callback");
            Check(a.Send(LegacyProtocol.Frame("ping", ("n", JsonValue.Create(3)))) is null, "closed sends ignored");
        });
        yield return Case("legacy.link.ipc-errors-and-control", () =>
        {
            var ipc = new LegacyTestIpc(); var link = new LegacyIpcLink(ipc); var frames = new List<LegacyFrame>(); link.Receive(frames.Add);
            ipc.Deliver(new JsonObject { ["control"] = true }); Equal(0, frames.Count, "foreign IPC ignored");
            ipc.Deliver(new JsonObject { ["chord2"] = new JsonObject { ["t"] = "ping", ["n"] = 3 } }); Equal(1, frames.Count, "chord2 IPC accepted");
            string? reason = null; link.OnClose(value => reason = value); ipc.Fail = new IOException("broken"); ipc.Fail.Data["code"] = "EPIPE";
            link.Send(LegacyProtocol.Frame("ping", ("n", JsonValue.Create(4))));
            Equal("ipc send failed: EPIPE", reason, "failed writes close"); Check(link.Closed, "IPC closed");
        });
        yield return Case("legacy.link.websocket-packed-refusal", () =>
        {
            var socket = new LegacyTestSocket(); var link = new LegacyWebSocketLink(socket, true, false);
            var frame = LegacyProtocol.Frame("result", ("id", JsonValue.Create(1)), ("value", JsonValue.Create(new string('x',70000))));
            var size = link.Send(frame); Check(size is > 0 && socket.Sent.Count == 0, "pre-open queued"); socket.Open();
            Equal(1,socket.Sent.Count,"open flushes"); Check(socket.Sent[0].IsBinary,"large payload binary");
            socket.Deliver(socket.Sent[0]); Check(link.Closed,"untrusted packed input closes"); Equal(1000,socket.CloseCode,"normal transport close");
        });
        yield return Case("legacy.link.protocol-and-roles", () =>
        {
            var link = new LegacyTestLink(); LegacyProtocolMismatch? mismatch = null;
            using var connection = new LegacyConnection(link, new() { OnProtocolMismatch = value => mismatch = value });
            link.Deliver(LegacyProtocol.Frame("hello", ("protocol", JsonValue.Create(2)), ("name", JsonValue.Create("client")), ("kind", JsonValue.Create("client")), ("codeVersion", JsonValue.Create(1))));
            Equal(2,mismatch!.Peer,"mismatch version"); Equal("protocol-version 4", link.Reason,"mismatch close reason");
            var other = new LegacyTestLink(); using var secured = new LegacyConnection(other,new() { Principal = new JsonObject{["user"]="real"},OriginId="client-origin" });
            var spoof = new LegacyFrame(new JsonObject { ["principal"] = new JsonObject{["user"]="fake"}, ["origin"]="fake" });
            Json("{\"user\":\"real\"}", secured.PrincipalOf(spoof),"principal stamped"); Equal("client-origin", secured.OriginOf(spoof),"origin stamped");
            other.Deliver(LegacyProtocol.Frame("commit", ("reload", JsonValue.Create(1)))); Check(other.Closed,"child reload control refused");
        });
        yield return Case("legacy.link.heartbeat-and-ack-disposal", () =>
        {
            var time=new LegacyTestTime();var link=new LegacyTestLink();using var connection=new LegacyConnection(link,new(){TimeProvider=time});
            link.Deliver(LegacyProtocol.Frame("hello",("protocol",JsonValue.Create(4)),("name",JsonValue.Create("client")),("kind",JsonValue.Create("client")),("codeVersion",JsonValue.Create(1)),("acks",JsonValue.Create(true))));
            time.Advance(TimeSpan.Zero);Equal(1,link.Sent.Count(frame=>frame.Type=="ack"),"hello acknowledged after processing");
            time.Advance(TimeSpan.FromSeconds(15));Check(link.Sent.Any(frame=>frame.Type=="ping"),"keepalive ping");
            time.Advance(TimeSpan.FromSeconds(45));Check(link.Closed,"silent connection timed out");Equal("ping timeout",link.Reason,"timeout reason");
            Equal(0,time.ActiveTimers,"all timers disposed on close");
        });
        yield return Case("legacy.flow.window-cap-urgent-control", () =>
        {
            var link = new LegacyTestLink(); using var connection = new LegacyConnection(link, new() { Window=1, PendingCap=5 });
            link.Deliver(LegacyProtocol.Frame("hello",("protocol",JsonValue.Create(4)),("name",JsonValue.Create("client")),("kind",JsonValue.Create("client")),("codeVersion",JsonValue.Create(1)),("acks",JsonValue.Create(true))));
            var resets=0; var state = LegacyProtocol.Frame("state",("hold",JsonValue.Create(1)),("m",JsonValue.Create("text")),("as",JsonValue.Create("ops")),("ops",new JsonArray(new JsonArray("a",new JsonArray("x"),new string('z',30)))));
            connection.SendState(state,()=>resets++); Check(connection.Waiting.Bytes<=5,"pending ops bounded"); Equal(1,connection.Waiting.Slots,"one lazy reset");
            var urgent=0; connection.QueueUnit(2,()=>urgent++,urgent:true); Equal(1,urgent,"new hold bypasses window");
            connection.Send(LegacyProtocol.Frame("result",("id",JsonValue.Create(4)))); Check(link.Sent.Any(f=>f.Type=="result"),"result bypasses state window");
            var acknowledged=link.Sent.Count(f=>f.Type!="ack"); link.Deliver(LegacyProtocol.Frame("ack",("n",JsonValue.Create(acknowledged))));
            Equal(1,resets,"overflow reset built lazily"); Equal(0,connection.Waiting.Slots,"waiting drained");
        });
        yield return Case("legacy.flow.relay-resync-and-unsized", () =>
        {
            var link = new LegacyTestLink(); using var connection = new LegacyConnection(link,new(){Window=1,PendingCap=0});
            link.Deliver(LegacyProtocol.Frame("hello",("protocol",JsonValue.Create(4)),("name",JsonValue.Create("client")),("kind",JsonValue.Create("client")),("codeVersion",JsonValue.Create(1)),("acks",JsonValue.Create(true))));
            var resync=0; connection.SendState(LegacyProtocol.Frame("state",("hold",JsonValue.Create(1)),("m",JsonValue.Create("x")),("as",JsonValue.Create("ops")),("ops",new JsonArray(new JsonArray("r",3)))),()=>{},()=>resync++);
            Equal(1,resync,"relay asks reset"); Equal(0,connection.Waiting.Slots,"relay drops pending stream");
            var unsized = new LegacyTestLink{Sized=false}; using var local = new LegacyConnection(unsized,new(){Window=1});
            unsized.Deliver(LegacyProtocol.Frame("hello",("protocol",JsonValue.Create(4)),("name",JsonValue.Create("client")),("kind",JsonValue.Create("client")),("codeVersion",JsonValue.Create(1)),("acks",JsonValue.Create(true))));
            local.SendState(LegacyProtocol.Frame("state",("hold",JsonValue.Create(1)),("m",JsonValue.Create("x")),("as",JsonValue.Create("snap")),("value",JsonValue.Create(1))),()=>{});
            Equal(0,local.Waiting.Slots,"unsized never windowed");
        });
    }
    private static async Task LegacyUntil(Func<bool> predicate)
    {
        for(var i=0;i<500 && !predicate();i++) await Task.Delay(2);
        Check(predicate(),"legacy asynchronous condition reached");
    }
}
internal sealed class LegacyTestIpc : ILegacyIpcChannel
{
    public bool Connected=>true;
    public event Action<JsonNode?>? Message;
    public event Action? Disconnected;
    public Exception? Fail;
    public void Send(JsonObject message,Action<Exception?> completion)=>completion(Fail);
    public void Deliver(JsonNode value)=>Message?.Invoke(value);
    public void Disconnect()=>Disconnected?.Invoke();
}
internal sealed class LegacyTestSocket : ILegacyWebSocket
{
    public int ReadyState{get;private set;}
    public int CloseCode;
    public List<LegacyMessage> Sent{get;}=[];
    public event Action? Opened;
    public event Action<LegacyMessage>? Message;
    public event Action? Closed;
    public void Send(LegacyMessage message)=>Sent.Add(message);
    public void Open(){ReadyState=1;Opened?.Invoke();}
    public void Deliver(LegacyMessage message)=>Message?.Invoke(message);
    public void Close(int code){CloseCode=code;ReadyState=3;Closed?.Invoke();}
}
internal sealed class LegacyTestLink : ILegacyLink
{
    private Action<LegacyFrame>? receiver;
    private readonly List<Action<string>> close=[];
    public System.Collections.Concurrent.ConcurrentQueue<LegacyFrame> Sent{get;}=new();
    public bool Sized=true;
    public bool Closed{get;private set;}
    public string? Reason;
    public long? Send(LegacyFrame frame){if(Closed)return null;Sent.Enqueue(frame);return Sized?1:null;}
    public void Receive(Action<LegacyFrame> handler)=>receiver=handler;
    public void OnClose(Action<string> handler){if(Closed)handler("closed");else close.Add(handler);}
    public void Deliver(LegacyFrame frame)=>receiver?.Invoke(frame);
    public void Close(string reason="closed"){if(Closed)return;Closed=true;Reason=reason;foreach(var handler in close)handler(reason);}
}
internal sealed class LegacyTestTime : TimeProvider
{
    private DateTimeOffset now=DateTimeOffset.UnixEpoch;
    private readonly List<LegacyTimer> timers=[];
    public override DateTimeOffset GetUtcNow()=>now;
    public int ActiveTimers=>timers.Count(timer=>!timer.Disposed);
    public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period)
    {
        var timer=new LegacyTimer(this,callback,state);timers.Add(timer);timer.Change(dueTime,period);return timer;
    }
    public void Advance(TimeSpan elapsed)
    {
        var end=now+elapsed;
        for(var count=0;count<10000;count++)
        {
            var next=timers.Where(timer=>!timer.Disposed&&timer.Due is {} due&&due<=end).OrderBy(timer=>timer.Due).FirstOrDefault();
            if(next is null){now=end;return;}now=next.Due!.Value;next.Fire();
        }
        throw new InvalidOperationException("timer did not settle");
    }
    private sealed class LegacyTimer(LegacyTestTime owner,TimerCallback callback,object? state):ITimer
    {
        internal bool Disposed;
        internal DateTimeOffset? Due;
        private TimeSpan period;
        public bool Change(TimeSpan dueTime,TimeSpan nextPeriod)
        {if(Disposed)return false;period=nextPeriod;Due=dueTime==Timeout.InfiniteTimeSpan?null:owner.now+dueTime;return true;}
        internal void Fire(){Due=period<=TimeSpan.Zero?null:owner.now+period;callback(state);}
        public void Dispose(){Disposed=true;Due=null;}
        public ValueTask DisposeAsync(){Dispose();return ValueTask.CompletedTask;}
    }
}
