using System.IO.Pipes;
using System.Text.Json.Nodes;
using PiSharp.Chord.Legacy;

internal static partial class Program
{
    private static LegacyContract LegacyCounterContract(string host = "server") => LegacyContract.Remote("test.legacy-counter",1,
        PiSharp.Chord.Contracts.Schemas.Obj(),LegacyPlacement.Named(host),new Dictionary<string,IReadOnlyDictionary<string,LegacyMember>>
        {
            ["main"]=new Dictionary<string,LegacyMember>
            {
                ["count"]=LegacyMember.State(PiSharp.Chord.Contracts.Schemas.Int()),
                ["add"]=LegacyMember.Method(PiSharp.Chord.Contracts.Schemas.Int(),PiSharp.Chord.Contracts.Schemas.Int()),
                ["echo"]=LegacyMember.Method(PiSharp.Chord.Contracts.Schemas.Unknown(),PiSharp.Chord.Contracts.Schemas.Unknown()),
                ["fail"]=LegacyMember.Method(),["wait"]=LegacyMember.Method(),["noop"]=LegacyMember.Method(),
            },
        });
    private static IEnumerable<(string,Func<Task>)> LegacySessionCases()
    {
        yield return Case("legacy.session.atomic-shape-hydration", async () =>
        {
            var schema = PiSharp.Chord.Contracts.Schemas.Int();
            var contract = LegacyContract.Remote("test.pair", 1, PiSharp.Chord.Contracts.Schemas.Obj(), LegacyPlacement.Named("server"),
                new Dictionary<string, IReadOnlyDictionary<string, LegacyMember>> { ["main"] = new Dictionary<string, LegacyMember> { ["a"] = LegacyMember.State(schema), ["b"] = LegacyMember.State(schema) } });
            var link = new LegacyTestLink(); using var connection = new LegacyConnection(link, new() { Name="client", Kind="client", Role="upstream" });
            using var session = new LegacyPeerSession(connection,new(){LocalName="client"}); using var reference = session.Hold(contract,new JsonObject());
            link.Deliver(LegacyProtocol.Frame("shape",("id",JsonValue.Create(reference.Id)),("variant",JsonValue.Create("main")),("members",new JsonArray("a","b")),("contract",JsonValue.Create(contract.Fingerprint)),("state",new JsonArray("a","b"))));
            link.Deliver(LegacyProtocol.Frame("state",("hold",JsonValue.Create(reference.Id)),("m",JsonValue.Create("a")),("as",JsonValue.Create("snap")),("value",JsonValue.Create(1))));
            Check(!reference.Ready.IsCompleted,"first snapshot alone does not hydrate");
            var early = Throws<ChordException>(()=>reference.Read("a"),"early read"); Equal("not_ready",early.Code,"early read code");
            link.Deliver(LegacyProtocol.Frame("state",("hold",JsonValue.Create(reference.Id)),("m",JsonValue.Create("b")),("as",JsonValue.Create("snap")),("value",JsonValue.Create(2))));
            await reference.Ready; Equal(1,reference.Read("a")!.GetValue<int>(),"a published"); Equal(2,reference.Read("b")!.GetValue<int>(),"b published");
        });
        yield return Case("legacy.session.transfer-cancel-error-release",async()=>
        {
            var contract=LegacyCounterContract(); var (a,b)=LegacyMemoryLink.Pair();
            using var serverConnection=new LegacyConnection(a,new(){Name="server"});
            using var clientConnection=new LegacyConnection(b,new(){Name="client",Kind="client",Role="upstream"});
            var supplied=new LegacyTestProvider(contract); var released=0;
            supplied.Disposed=()=>released++;
            using var server=new LegacyPeerSession(serverConnection,new(){LocalName="server",Provider=(_,_,_)=>Task.FromResult<ILegacyServedReference>(supplied)});
            using var client=new LegacyPeerSession(clientConnection,new(){LocalName="client"});
            using var reference=client.Hold(contract,new JsonObject()); await reference.Ready.WaitAsync(TimeSpan.FromSeconds(5));
            Equal("main",reference.Variant,"variant hydrated"); Equal(0,reference.Read("count")!.GetValue<int>(),"first snapshot");
            Equal(3,(await reference.CallAsync("add",JsonValue.Create(3)))!.GetValue<int>(),"remote method result");
            await LegacyUntil(()=>reference.Read("count")!.GetValue<int>()==3);
            var error=await LegacyThrows<ChordException>(()=>reference.CallAsync("fail")); Equal("forbidden",error.Code,"remote error code"); Equal("scope",error.Reason,"remote error reason");
            using var stop=new CancellationTokenSource(); var waiting=reference.CallAsync("wait",cancellationToken:stop.Token);
            await supplied.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
            var aborted=await LegacyThrows<ChordException>(()=>waiting); Equal("aborted",aborted.Code,"outbound canceled");
            await supplied.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            reference.Release(); await LegacyUntil(()=>released==1);
        });
        yield return Case("legacy.session.late-provider-and-call-limit",async()=>
        {
            var contract=LegacyCounterContract(); var (a,b)=LegacyMemoryLink.Pair();
            using var s=new LegacyConnection(a,new(){Name="server"}); using var c=new LegacyConnection(b,new(){Name="client",Kind="client",Role="upstream"});
            var admit=new TaskCompletionSource<ILegacyServedReference>(TaskCreationOptions.RunContinuationsAsynchronously);var provider=new LegacyTestProvider(contract);
            using var server=new LegacyPeerSession(s,new(){LocalName="server",Provider=(_,_,_)=>admit.Task,MaxCalls=1});
            using var client=new LegacyPeerSession(c,new(){LocalName="client"}); using var reference=client.Hold(contract,new JsonObject(),state:false);
            Check(!reference.Ready.IsCompleted,"hold waits for provider"); admit.SetResult(provider); await reference.Ready.WaitAsync(TimeSpan.FromSeconds(5));
            using var stop=new CancellationTokenSource(); var first=reference.CallAsync("wait",cancellationToken:stop.Token);
            await provider.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var error=await LegacyThrows<ChordException>(()=>reference.CallAsync("noop")); Equal("too_many_calls",error.Reason,"call ceiling enforced");
            stop.Cancel(); await LegacyThrows<ChordException>(()=>first);
        });
        yield return Case("legacy.session.relay-opaque-payload-origin",async()=>
        {
            var contract=LegacyCounterContract("worker");var(a,b)=LegacyMemoryLink.Pair();var(c,d)=LegacyMemoryLink.Pair();
            using var browserConnection=new LegacyConnection(a,new(){Name="browser",Kind="client",Role="upstream"});
            using var ingress=new LegacyConnection(b,new(){Name="relay",Principal=new JsonObject{["user"]="allowed"},OriginId="origin-7"});
            using var egress=new LegacyConnection(c,new(){Name="relay",Role="downstream",PeerKind="host",PeerName="worker"});
            using var workerConnection=new LegacyConnection(d,new(){Name="worker",Role="upstream"});
            JsonNode? principal=null;string? origin=null;var provider=new LegacyTestProvider(contract);
            using var worker=new LegacyPeerSession(workerConnection,new(){LocalName="worker",Provider=(frame,p,_)=>{principal=p;origin=workerConnection.OriginOf(frame);return Task.FromResult<ILegacyServedReference>(provider);}});
            using var outgoing=new LegacyPeerSession(egress,new(){LocalName="relay"});
            var admitted=0;var released=0;var sent=0;
            using var relay=new LegacyPeerSession(ingress,new(){LocalName="relay",RelayResolver=(_,_,_,_)=>Task.FromResult(egress),
                AdmitRoot=(root,_,token)=>{Check(root is null,"untrusted client root stripped before admission");Interlocked.Increment(ref admitted);return Task.FromResult(new LegacyRootLease(new("relay-owned",1),token,()=>Interlocked.Increment(ref released)));},
                RootSent=(root,link)=>{Equal("relay-owned",root.RootId,"owned root sent");Check(ReferenceEquals(link,egress),"root tracks child link");Interlocked.Increment(ref sent);}});
            using var browser=new LegacyPeerSession(browserConnection,new(){LocalName="browser"});
            using var reference=browser.Hold(contract,new JsonObject(),principal:new JsonObject{["user"]="spoof"},origin:"spoof");
            await reference.Ready.WaitAsync(TimeSpan.FromSeconds(5)); Json("{\"user\":\"allowed\"}",principal,"trusted relay principal"); Equal("origin-7",origin,"trusted relay origin");
            Equal(2,(await reference.CallAsync("add",JsonValue.Create(2),new LegacyRoot("spoof",999)))!.GetValue<int>(),"relayed result");
            Equal("relay-owned",provider.LastRoot?.RootId,"client cannot forward a root through relay");
            var large=new string('雪',70000);Equal(large,(await reference.CallAsync("echo",JsonValue.Create(large)))!.GetValue<string>(),"packed call and result relayed");
            await LegacyUntil(()=>released==2);Equal(2,admitted,"relay roots admitted");Equal(2,sent,"relay roots track destinations");
            reference.Release(); await LegacyUntil(()=>provider.IsDisposed);
        });
        yield return Case("legacy.link.native-pipe-transfer",async()=>
        {
            using var addresses=PrivatePipeDirectory.Create(); var name=addresses.Name("legacy");
            using var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            using var client=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
            var accepted=server.WaitForConnectionAsync(); await client.ConnectAsync(5000); await accepted;
            var a=new LegacyPipeLink(server);var b=new LegacyPipeLink(client);
            var received=new TaskCompletionSource<LegacyFrame>(TaskCreationOptions.RunContinuationsAsynchronously);b.Receive(frame=>received.TrySetResult(frame));
            a.Send(LegacyProtocol.Frame("result",("id",JsonValue.Create(1)),("value",JsonValue.Create(new string('雪',70000)))));
            var frame=await received.Task.WaitAsync(TimeSpan.FromSeconds(5)); Equal(new string('雪',70000),frame.UnpackPayload()!.GetValue<string>(),"actual pipe packed transfer");a.Close();b.Close();
        });
        yield return Case("legacy.link.native-websocket-transfer",async()=>
        {
            using var addresses=PrivatePipeDirectory.Create(); var name=addresses.Name("websocket");
            using var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            using var client=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
            var accepted=server.WaitForConnectionAsync(); await client.ConnectAsync(5000); await accepted;
            using var left=System.Net.WebSockets.WebSocket.CreateFromStream(server,true,null,TimeSpan.FromSeconds(10));
            using var right=System.Net.WebSockets.WebSocket.CreateFromStream(client,false,null,TimeSpan.FromSeconds(10));
            var a=LegacyNativeWebSocket.Create(left,compress:true);var b=LegacyNativeWebSocket.Create(right);
            var received=new TaskCompletionSource<LegacyFrame>(TaskCreationOptions.RunContinuationsAsynchronously);b.Receive(frame=>received.TrySetResult(frame));
            a.Send(LegacyProtocol.Frame("result",("id",JsonValue.Create(1)),("value",JsonValue.Create(new string('x',70000)))));
            var frame=await received.Task.WaitAsync(TimeSpan.FromSeconds(5)); Equal(new string('x',70000),frame.UnpackPayload()!.GetValue<string>(),"real WebSocket framing and compression");a.Close();b.Close();
        });
    }
    private static async Task<T> LegacyThrows<T>(Func<Task> operation) where T:Exception
    {try{await operation();}catch(T error){return error;}throw new InvalidOperationException("expected "+typeof(T).Name);}
}
internal sealed class LegacyTestProvider(LegacyContract contract):ILegacyServedReference
{
    private int count;
    public bool IsDisposed;
    public Action? Disposed;
    public LegacyRoot? LastRoot;
    public TaskCompletionSource Waiting{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Canceled{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string? Variant=>"main";
    public string ContractFingerprint=>contract.Fingerprint;
    public IReadOnlyList<string> Members=>["count","add","echo","fail","wait","noop"];
    public IReadOnlyDictionary<string,JsonNode?> State=>new Dictionary<string,JsonNode?>{["count"]=JsonValue.Create(count)};
    public event Action<LegacyFrame>? StateChanged;
    public async Task<LegacyCallResult> CallAsync(string member,bool hasArgs,JsonNode? args,LegacyRoot? root,CancellationToken cancellationToken)
    {
        LastRoot=root;
        if(member=="echo")return LegacyCallResult.From(args);
        if(member=="fail")throw new ChordException("forbidden","denied","scope");
        if(member=="wait")
        {
            Waiting.TrySetResult();try{await Task.Delay(Timeout.Infinite,cancellationToken);}catch(OperationCanceledException){Canceled.TrySetResult();throw;}return LegacyCallResult.Void;
        }
        if(member=="add")
        {
            count+=args!.GetValue<int>(); StateChanged?.Invoke(LegacyProtocol.Frame("state",("hold",JsonValue.Create(1)),("m",JsonValue.Create("count")),("as",JsonValue.Create("reset")),("value",JsonValue.Create(count))));
            return LegacyCallResult.From(JsonValue.Create(count));
        }
        return LegacyCallResult.Void;
    }
    public void Dispose(){if(IsDisposed)return;IsDisposed=true;Disposed?.Invoke();}
}
