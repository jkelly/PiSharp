using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.AI.Providers;
using PiSharp.Contracts;

// Root-only synthetic controls. No shared Program.cs, packages, credentials or live sends.
internal static class AnthropicHookChecks
{
    private sealed class Row(Task task,int id,string alias)
    { public Task Actual=task;public int Id=id;public List<string> Aliases=[alias];public bool Joined;public Exception? Direct;public AggregateException? Aggregate;public Task? Join; }
    private static readonly Dictionary<Task,Row> Originals=new(ReferenceEqualityComparer.Instance);
    private static readonly List<Exception> Faults=[];
    private static readonly ModelDescriptor Model=new("inert-model","anthropic-messages","anthropic");
    private static readonly ChatRequest Request=new(Model,[new("user",JsonData.Parse("{\"role\":\"user\",\"content\":\"synthetic\",\"timestamp\":123}"))],123);
    private static T Own<T>(string alias,T actual)where T:Task{if(Originals.TryGetValue(actual,out var row)){row.Aliases.Add(alias);return actual;}Check(Originals.Count<4096,"task bound");Originals.Add(actual,new(actual,Originals.Count+1,alias));return actual;}
    private static Task Join(Row row)=>row.Join??=JoinCore(row);
    private static async Task JoinCore(Row row){try{await row.Actual.ConfigureAwait(false);}catch(Exception error){row.Direct=error;row.Aggregate=row.Actual.Exception;Faults.Add(error);if(row.Aggregate is not null)Faults.Add(row.Aggregate);}finally{row.Joined=true;}}
    private static async Task Settle(){while(Originals.Values.FirstOrDefault(row=>!row.Joined) is {} row)await Join(row).ConfigureAwait(false);}
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private static readonly List<TaskCompletionSource> Gates=[];
    private static readonly List<IAsyncEnumerator<StreamEvent>> Iterators=[];
    private static TaskCompletionSource Gate(){var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);Gates.Add(gate);return gate;}
    private static IAsyncEnumerator<StreamEvent> Iterator(IAsyncEnumerable<StreamEvent> source){var iterator=source.GetAsyncEnumerator();Iterators.Add(iterator);return iterator;}
    private static void Retain(StreamEvent frame){if(frame is StreamTerminalEvent terminal){if(terminal.NativeSourceTask is {} task)Own("terminal.source",task);if(terminal.NativeSourceException is {} error)Faults.Add(error);if(terminal.NativeCleanupTasks is {} tasks)foreach(var cleanup in tasks)Own("terminal.cleanup",cleanup);if(terminal.NativeCleanupExceptions is {} errors)Faults.AddRange(errors);}}
    private static async Task Retire(){foreach(var gate in Gates)gate.TrySetResult();await Settle().ConfigureAwait(false);foreach(var iterator in Iterators){try{await Own("emergency.consumer.dispose",iterator.DisposeAsync().AsTask()).ConfigureAwait(false);}catch(Exception error){Faults.Add(error);}}await Settle().ConfigureAwait(false);}
    private static async Task<List<StreamEvent>> Drain(IAsyncEnumerable<StreamEvent> source,string name)
    {
        var iterator=Iterator(source);var frames=new List<StreamEvent>();
        try{while(await Own(name+".next",iterator.MoveNextAsync().AsTask()).ConfigureAwait(false)){Check(frames.Count<32,"frame bound");frames.Add(iterator.Current);Retain(iterator.Current);}}
        finally{await Own(name+".dispose",iterator.DisposeAsync().AsTask()).ConfigureAwait(false);}return frames;
    }
    private static AnthropicMessagesHttpSseTransport Transport(HttpClient client,AnthropicMessagesHooks hooks)=>new(client,(_,_)=>new(HttpMethod.Post,"https://anthropic.invalid/authored"){Content=new StringContent("{}")},hooks:hooks);
    private static async Task<int> Main()
    {
        Check(Environment.GetEnvironmentVariable("PISHARP_ROOT_ANTHROPIC_HOOKS")=="approved-r1304","disabled root lease required");
        var cases=new List<object>();
        try
        {
            // A held response callback proves that physical preparation has happened but no start/read can escape.
            {
                var entered=Gate();var release=Gate();var body=new Body();using var client=new HttpClient(new Handler(body));
                var callback=Own("response-order.callback",release.Task);
                var transport=Transport(client,new((info,model,_)=>{Check(info.Status==200&&info.Headers["x-authored"]=="yes"&&ReferenceEquals(model,Model),"owned response snapshot/model");entered.SetResult();return callback;}));
                var iterator=Iterator(transport.StreamAsync(Request));var first=Own("response-order.first",iterator.MoveNextAsync().AsTask());
                await Own("response-order.entered",entered.Task).ConfigureAwait(false);Check(!first.IsCompleted&&body.Reads==0&&!body.Disposed,"response hook before start/read/dispose");
                release.SetResult();Check(await first.ConfigureAwait(false)&&iterator.Current is StreamStarted,"first start after callback");
                var rest=new List<StreamEvent>();while(await Own("response-order.next",iterator.MoveNextAsync().AsTask()).ConfigureAwait(false)){rest.Add(iterator.Current);Retain(iterator.Current);}
                await Own("response-order.dispose",iterator.DisposeAsync().AsTask()).ConfigureAwait(false);Check(rest.Last() is StreamDone&&body.Disposed,"successful cleanup");cases.Add(new{name="response-before-start",passed=true});
            }
            {
                var error=new InvalidOperationException("authored response hook failure");var actual=Own("response-failure.callback",Task.FromException(error));var body=new Body();using var client=new HttpClient(new Handler(body));
                var frames=await Own("response-failure.drain",Drain(Transport(client,new((_,_,_)=>actual)).StreamAsync(Request),"response-failure")).ConfigureAwait(false);
                var terminal=frames.OfType<StreamError>().Single();Check(frames.Count==1&&body.Reads==0&&body.Disposed,"hook rejection no start/read; cleanup");Check(ReferenceEquals(terminal.NativeSourceTask,actual)&&ReferenceEquals(terminal.NativeSourceException,error),"exact callback failure retained");cases.Add(new{name="response-failure-no-start",passed=true});
            }
            foreach(var lateFault in new[]{false,true})
            {
                var name=lateFault?"held-response-cancel-late-fault":"held-response-cancel-late-success";using var abort=new CancellationTokenSource();var entered=Gate();var release=Gate();var actual=Own(name+".callback",release.Task);var error=new IOException("authored late hook failure");var body=new Body();using var client=new HttpClient(new Handler(body));
                var iterator=Iterator(Transport(client,new((_,_,_)=>{entered.SetResult();return actual;})).StreamAsync(Request,abort.Token));var first=Own(name+".first",iterator.MoveNextAsync().AsTask());
                await Own(name+".entered",entered.Task).ConfigureAwait(false);abort.Cancel();Check(!first.IsCompleted&&!actual.IsCompleted&&!body.Disposed,"abort cannot settle an actual held callback");
                if(lateFault)release.SetException(error);else release.SetResult();Check(await first.ConfigureAwait(false)&&iterator.Current is StreamError{Reason:StopReason.Aborted},"abort terminal after callback settled");
                var terminal=(StreamError)iterator.Current;Retain(terminal);if(lateFault)Check(ReferenceEquals(terminal.NativeSourceTask,actual)&&ReferenceEquals(terminal.NativeSourceException,error),"late hook fault exact references");
                Check(body.Disposed&&terminal.NativeCleanupTasks!.All(task=>task.IsCompleted),"cleanup before terminal");while(await Own(name+".next",iterator.MoveNextAsync().AsTask()).ConfigureAwait(false)){}await Own(name+".dispose",iterator.DisposeAsync().AsTask()).ConfigureAwait(false);cases.Add(new{name,passed=true});
            }
            {
                var entered=Gate();var release=Gate();var callback=Own("provider-order.callback",release.Task);var body=new Body();using var client=new HttpClient(new Handler(body));
                var hooks=new AnthropicMessagesHooks(OnProviderStreamEvent:(dto,_,_)=>{if(dto.Value.GetProperty("type").GetString()=="content_block_delta"){entered.SetResult();return callback;}return Task.CompletedTask;});
                var iterator=Iterator(Transport(client,hooks).StreamAsync(Request));Check(await Own("provider-order.start",iterator.MoveNextAsync().AsTask()).ConfigureAwait(false)&&iterator.Current is StreamStarted,"start");
                var start=(StreamStarted)iterator.Current;Check(await Own("provider-order.block",iterator.MoveNextAsync().AsTask()).ConfigureAwait(false)&&iterator.Current is TextStarted,"block start");var next=Own("provider-order.delta",iterator.MoveNextAsync().AsTask());
                await Own("provider-order.entered",entered.Task).ConfigureAwait(false);Check(!next.IsCompleted&&start.Partial.Content.IsEmpty,"provider hook before mutation/delta");release.SetResult();Check(await next.ConfigureAwait(false)&&iterator.Current is TextDelta{Delta:"hello"},"one delta after callback");
                var frames=new List<StreamEvent>();while(await Own("provider-order.next",iterator.MoveNextAsync().AsTask()).ConfigureAwait(false)){frames.Add(iterator.Current);Retain(iterator.Current);}Check(frames.Last() is StreamDone,"done");await Own("provider-order.dispose",iterator.DisposeAsync().AsTask()).ConfigureAwait(false);cases.Add(new{name="provider-before-mutation",passed=true});
            }
            {
                var error=new IOException("authored provider hook failure");var actual=Own("provider-failure.callback",Task.FromException(error));var body=new Body();using var client=new HttpClient(new Handler(body));
                var frames=await Own("provider-failure.drain",Drain(Transport(client,new(OnProviderStreamEvent:(_,_,_)=>actual)).StreamAsync(Request),"provider-failure")).ConfigureAwait(false);var terminal=frames.OfType<StreamError>().Single();Check(frames.Count==2&&frames[0] is StreamStarted&&terminal.Message.Content.IsEmpty&&terminal.Message.Usage.TotalTokens==0,"provider rejection before message mutation");Check(ReferenceEquals(terminal.NativeSourceTask,actual)&&ReferenceEquals(terminal.NativeSourceException,error)&&body.Disposed,"provider fault retained and owner cleaned");cases.Add(new{name="provider-failure-before-mutation",passed=true});
            }
            {
                var bodyFault=new IOException("authored async body dispose failure");var responseFault=new IOException("authored response dispose failure");var body=new Body(bodyFault);using var client=new HttpClient(new Handler(body,responseFault));
                var frames=await Own("cleanup-siblings.drain",Drain(Transport(client,new()).StreamAsync(Request),"cleanup-siblings")).ConfigureAwait(false);var terminal=frames.OfType<StreamError>().Single();
                Check(terminal.NativeCleanupExceptions!.Any(error=>ReferenceEquals(error,bodyFault))&&terminal.NativeCleanupExceptions!.Any(error=>ReferenceEquals(error,responseFault)),"both body and response cleanup siblings retained");Check(terminal.NativeCleanupTasks!.Any(task=>ReferenceEquals(task,body.DisposeTask))&&terminal.NativeCleanupTasks!.All(task=>task.IsCompleted),"actual cleanup tasks settled");cases.Add(new{name="cleanup-fault-siblings",passed=true});
            }
            {
                var bodyFault=new IOException("authored early-dispose body fault");var responseFault=new IOException("authored early-dispose response fault");var body=new Body(bodyFault);using var client=new HttpClient(new Handler(body,responseFault));var iterator=Iterator(Transport(client,new()).StreamAsync(Request));Check(await Own("early-dispose.first",iterator.MoveNextAsync().AsTask()).ConfigureAwait(false)&&iterator.Current is StreamStarted,"early start");
                var disposal=Own("early-dispose.actual",iterator.DisposeAsync().AsTask());Exception? rejection=null;try{await disposal.ConfigureAwait(false);}catch(Exception error){rejection=error;}Check(rejection is AggregateException aggregate&&aggregate.InnerExceptions.Any(error=>ReferenceEquals(error,bodyFault))&&aggregate.InnerExceptions.Any(error=>ReferenceEquals(error,responseFault)),"early consumer disposal retains cleanup siblings");cases.Add(new{name="early-dispose-cleanup-faults",passed=true});
            }
            {
                var seen=false;var body=new Body();using var handler=new Handler(body,expectedUri:"https://api.anthropic.com/v1/messages?beta=true");using var provider=NativeProviderFactory.CreateAnthropic(Model,new("https://api.anthropic.com/"),"pisharp-authored-inert-key-noncredential",new(8192),handler:handler,hooks:new((_,model,_)=>{Check(ReferenceEquals(model,Model),"factory model");seen=true;return Task.CompletedTask;}));var frames=await Own("factory-wiring.drain",Drain(provider.StreamAsync(Request),"factory-wiring")).ConfigureAwait(false);Check(seen&&frames.Last() is StreamDone,"appended factory hooks wired");cases.Add(new{name="factory-hooks-wiring",passed=true});
            }
            foreach(var providerHook in new[]{false,true})foreach(var unmatched in new[]{false,true})
            {
                var name=(providerHook?"provider":"response")+".sync-oce."+(unmatched?"unmatched":"clear");using var foreign=new CancellationTokenSource();foreign.Cancel();var cause=new OperationCanceledException("authored synchronous hook OCE",null,unmatched?foreign.Token:default);var body=new Body();using var client=new HttpClient(new Handler(body));
                var hooks=providerHook?new AnthropicMessagesHooks(OnProviderStreamEvent:(_,_,_)=>throw cause):new AnthropicMessagesHooks(OnResponse:(_,_,_)=>throw cause);
                var frames=await Own(name+".drain",Drain(Transport(client,hooks).StreamAsync(Request),name)).ConfigureAwait(false);var terminal=frames.OfType<StreamError>().Single();
                Check(terminal.Reason==StopReason.Error&&terminal.NativeSourceException is AnthropicMessagesHookInvocationException envelope&&ReferenceEquals(envelope.InnerException,cause),"sync OCE is source fault retaining exact cause");Check(terminal.NativeSourceTask is null||terminal.NativeSourceTask.IsFaulted,"no false canceled callback/wrapper classification");Check(frames.OfType<StreamStarted>().Count()==(providerHook?1:0),"synchronous invocation phase order");cases.Add(new{name,passed=true});
            }
            foreach(var providerHook in new[]{false,true})foreach(var canceledTask in new[]{false,true})
            {
                var name=(providerHook?"provider":"response")+".actual-task."+(canceledTask?"canceled":"faulted-oce");using var foreign=new CancellationTokenSource();foreign.Cancel();var cause=new OperationCanceledException("authored returned-task OCE",null,foreign.Token);var actual=Own(name+".callback",canceledTask?Task.FromCanceled(foreign.Token):Task.FromException(cause));var body=new Body();using var client=new HttpClient(new Handler(body));
                var hooks=providerHook?new AnthropicMessagesHooks(OnProviderStreamEvent:(_,_,_)=>actual):new AnthropicMessagesHooks(OnResponse:(_,_,_)=>actual);var frames=await Own(name+".drain",Drain(Transport(client,hooks).StreamAsync(Request),name)).ConfigureAwait(false);var terminal=frames.OfType<StreamError>().Single();
                Check(terminal.Reason==StopReason.Error&&ReferenceEquals(terminal.NativeSourceTask,actual)&&actual.IsCanceled==canceledTask&&actual.IsFaulted!=canceledTask,"actual task classification retained without owned abort");Check(terminal.NativeSourceException is OperationCanceledException cancellation&&cancellation.CancellationToken==foreign.Token,"actual cancellation cause/token retained");if(!canceledTask)Check(ReferenceEquals(terminal.NativeSourceException,cause),"faulted-task exact OCE object; no invocation wrapper");cases.Add(new{name,passed=true});
            }
            foreach(var providerHook in new[]{false,true})foreach(var firstFaulted in new[]{false,true})
            {
                var name=(providerHook?"provider":"response")+".multicast."+(firstFaulted?"first-faulted":"first-held");var release=Gate();var first=Own(name+".authored-control-not-invoked",firstFaulted?Task.FromException(new IOException("authored never-invoked multicast task")):release.Task);var calls=0;var requests=0;var body=new Body();using var handler=new Handler(body);using var client=new HttpClient(handler,false);AnthropicMessagesHooks hooks;
                if(providerHook){Func<JsonData,ModelDescriptor,CancellationToken,Task> hook=(_,_,_)=>{calls++;return first;};hook+=(_,_,_)=>{calls++;return Task.CompletedTask;};hooks=new(OnProviderStreamEvent:hook);}else{Func<AnthropicMessagesResponseInfo,ModelDescriptor,CancellationToken,Task> hook=(_,_,_)=>{calls++;return first;};hook+=(_,_,_)=>{calls++;return Task.CompletedTask;};hooks=new(OnResponse:hook);}
                Exception? rejection=null;try{_ = new AnthropicMessagesHttpSseTransport(client,(_,_)=>{requests++;return new(HttpMethod.Post,"https://anthropic.invalid/authored");},hooks:hooks);}catch(Exception error){rejection=error;}
                Check(rejection is ArgumentException&&calls==0&&requests==0&&handler.Sends==0&&body.Reads==0&&!body.Disposed,"multicast rejected before request/send/body/callback effects");release.TrySetResult();cases.Add(new{name,passed=true});
            }
            await Retire().ConfigureAwait(false);Console.WriteLine(JsonSerializer.Serialize(new{schemaVersion=1,cases,originals=Originals.Values.Select(View),faultGraph=Graph(),allOriginalsJoined=Originals.Values.All(row=>row.Joined)}));return 0;
        }
        catch(Exception error){Faults.Add(error);await Retire().ConfigureAwait(false);Console.Error.WriteLine(JsonSerializer.Serialize(new{failed=true,cases,originals=Originals.Values.Select(View),faultGraph=Graph()}));return 1;}
    }
    private static readonly Dictionary<Exception,int> FaultIds=new(ReferenceEqualityComparer.Instance);
    private static int? FaultId(Exception? error){if(error is null)return null;if(FaultIds.TryGetValue(error,out var id))return id;if(FaultIds.Count==1024)return null;id=FaultIds.Count;FaultIds.Add(error,id);return id;}
    private static object View(Row row)=>new{actualTaskId=row.Id,aliases=row.Aliases,row.Joined,status=row.Actual.Status.ToString(),directFaultId=FaultId(row.Direct),aggregateFaultId=FaultId(row.Aggregate)};
    private static object Graph(){var queue=new Queue<Exception>();var seen=new HashSet<Exception>(ReferenceEqualityComparer.Instance);var nodes=new List<object>();var edges=0;var truncated=0;int? Add(Exception error){var id=FaultId(error);if(id is null){truncated++;return null;}if(seen.Add(error))queue.Enqueue(error);return id;}var roots=Faults.Select(Add).ToArray();while(queue.TryDequeue(out var error)){var children=error is AggregateException aggregate?aggregate.InnerExceptions.ToArray():error.InnerException is {} child?[child]:Array.Empty<Exception>();var refs=new List<int?>();foreach(var childError in children){if(edges++<4096)refs.Add(Add(childError));else truncated++;}nodes.Add(new{id=FaultId(error),type=error.GetType().FullName,error.Message,error.StackTrace,children=refs});}return new{roots,nodes,truncated};}
    private const string Wire="event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"authored\",\"model\":\"inert-model\",\"usage\":{\"input_tokens\":11,\"output_tokens\":0}}}\n\nevent: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\nevent: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hello\"}}\n\nevent: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\nevent: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\nevent: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
    private sealed class Body(Exception? disposeFault=null):MemoryStream(Encoding.UTF8.GetBytes(Wire))
    {
        public int Reads;public bool Disposed;public Task? DisposeTask;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default){Reads++;return new(Own("body.read",base.ReadAsync(buffer,token).AsTask()));}
        public override ValueTask DisposeAsync(){if(DisposeTask is null){Disposed=true;DisposeTask=Own("body.dispose",disposeFault is null?Task.CompletedTask:Task.FromException(disposeFault));}return new(DisposeTask!);}
        protected override void Dispose(bool disposing){if(disposing)Disposed=true;base.Dispose(disposing);}
    }
    private sealed class Content(Body body,Exception? fault):HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync()=>Own("body.acquire",Task.FromResult<Stream>(body));
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token)=>CreateContentReadStreamAsync();
        protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context)=>throw new NotSupportedException("authored stream acquisition only");
        protected override bool TryComputeLength(out long length){length=body.Length;return true;}
        protected override void Dispose(bool disposing){base.Dispose(disposing);if(disposing&&fault is not null)throw fault;}
    }
    private sealed class Handler(Body body,Exception? responseFault=null,string expectedUri="https://anthropic.invalid/authored"):HttpMessageHandler
    {
        public int Sends;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {Check(request.RequestUri?.AbsoluteUri==expectedUri,"authored injected-handler endpoint only");Sends++;var response=new HttpResponseMessage(HttpStatusCode.OK){Content=new Content(body,responseFault)};response.Headers.Add("x-authored","yes");return Own("http.send",Task.FromResult(response));}
    }
}
