using System.Net;
using System.Reflection;
using System.Text.Json;
using PiSharp.AI.Transports;

// Root-only source-authored probes of both released preparation seams.
internal static class AnthropicPreparationChecks
{
    private sealed class Row(Task task,int id,string alias){public Task Task=task;public int Id=id;public List<string> Aliases=[alias];public bool Joined;public Exception? Direct;public AggregateException? Aggregate;public Task? Join;}
    private static readonly Dictionary<Task,Row> Rows=new(ReferenceEqualityComparer.Instance);
    private static readonly List<Exception> Faults=[];
    private static readonly Dictionary<Exception,int> FaultIds=new(ReferenceEqualityComparer.Instance);
    private static T Own<T>(string alias,T task)where T:Task{if(Rows.TryGetValue(task,out var prior)){prior.Aliases.Add(alias);return task;}if(Rows.Count>=256)throw new InvalidOperationException("task bound");Rows.Add(task,new(task,Rows.Count+1,alias));return task;}
    private static Task Join(Row row)=>row.Join??=JoinCore(row);
    private static async Task JoinCore(Row row){try{await row.Task.ConfigureAwait(false);}catch(Exception error){row.Direct=error;row.Aggregate=row.Task.Exception;Faults.Add(error);if(row.Aggregate is not null)Faults.Add(row.Aggregate);}finally{row.Joined=true;}}
    private static async Task Settle(){while(Rows.Values.FirstOrDefault(row=>!row.Joined) is {} row)await Join(row).ConfigureAwait(false);}
    private static void Check(bool ok,string label){if(!ok)throw new InvalidOperationException(label);}
    private static Task Prepare(HttpSseTransport transport,string method,HttpRequestMessage request,Func<HttpResponseMessage,CancellationToken,ValueTask>? inspect,CancellationToken token)
    {
        // No shim: reflection obtains the real internal ValueTask and its actual AsTask original.
        var member=typeof(HttpSseTransport).GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)??throw new InvalidOperationException("pinned preparation seam missing");
        var arguments=method=="PrepareAsync"?new object?[]{request,inspect,token}:new object?[]{request,token};
        var value=member.Invoke(transport,arguments)??throw new InvalidOperationException("missing ValueTask");
        return Own(method+".actual",(Task)(value.GetType().GetMethod("AsTask",Type.EmptyTypes)!.Invoke(value,null)??throw new InvalidOperationException("missing actual task")));
    }
    private static async Task<int> Main()
    {
        Check(Environment.GetEnvironmentVariable("PISHARP_ROOT_ANTHROPIC_PREPARATION")=="approved-r1319","disabled root lease required");var cases=new List<object>();
        try
        {
            foreach(var method in new[]{"PrepareAsync","PrepareOwnedAsync"})
            {
                var cleanup=new IOException("authored rejection cleanup fault");var content=new Content(cleanup);using var handler=new Handler(content,HttpStatusCode.ServiceUnavailable);using var client=new HttpClient(handler,false);using var request=new HttpRequestMessage(HttpMethod.Post,"https://sse.invalid/authored");
                var actual=Prepare(new(client),method,request,null,default);await Join(Rows[actual]).ConfigureAwait(false);var error=Rows[actual].Direct;Check(error is AggregateException aggregate&&aggregate.InnerExceptions.Count==2&&aggregate.InnerExceptions[0] is HttpSseRejectedException{StatusCode:HttpStatusCode.ServiceUnavailable}&&ReferenceEquals(aggregate.InnerExceptions[1],cleanup),"rejection and exact cleanup objects");Check(content.Disposals==1&&content.Acquisitions==0&&handler.Sends==1,"one send/dispose; no body claim");cases.Add(new{name=method+".rejection-cleanup",passed=true});
            }
            // Exact inspection failure identity through canonical preparation; no body acquisition occurs.
            {
                var primary=new IOException("authored inspection fault");var cleanup=new IOException("authored inspection cleanup fault");var content=new Content(cleanup);using var handler=new Handler(content,HttpStatusCode.OK);using var client=new HttpClient(handler,false);using var request=new HttpRequestMessage(HttpMethod.Post,"https://sse.invalid/authored");
                var callback=Own("inspection.actual",Task.FromException(primary));var actual=Prepare(new(client),"PrepareAsync",request,(_,_)=>new ValueTask(callback),default);await Join(Rows[actual]).ConfigureAwait(false);Check(Rows[actual].Direct is AggregateException aggregate&&ReferenceEquals(aggregate.InnerExceptions[0],primary)&&ReferenceEquals(aggregate.InnerExceptions[1],cleanup),"exact inspection/disposal siblings");Check(content.Disposals==1&&content.Acquisitions==0,"inspection cleanup once without body");cases.Add(new{name="PrepareAsync.inspection-cleanup",passed=true});
            }
            foreach(var failCleanup in new[]{false,true})
            {
                using var abort=new CancellationTokenSource();var primary=new OperationCanceledException("authored inspection cancellation",null,abort.Token);var cleanup=new IOException("authored cancellation cleanup fault");var content=new Content(failCleanup?cleanup:null);using var handler=new Handler(content,HttpStatusCode.OK);using var client=new HttpClient(handler,false);using var request=new HttpRequestMessage(HttpMethod.Post,"https://sse.invalid/authored");
                Func<HttpResponseMessage,CancellationToken,ValueTask> inspect=(_,_)=>{abort.Cancel();throw primary;};var actual=Prepare(new(client),"PrepareAsync",request,inspect,abort.Token);await Join(Rows[actual]).ConfigureAwait(false);
                if(failCleanup)Check(actual.IsFaulted&&Rows[actual].Direct is AggregateException aggregate&&ReferenceEquals(aggregate.InnerExceptions[0],primary)&&ReferenceEquals(aggregate.InnerExceptions[1],cleanup),"cancellation/cleanup exact cause identity");else Check(actual.IsCanceled&&ReferenceEquals(Rows[actual].Direct,primary),"successful cleanup preserves cancellation object/classification");Check(content.Disposals==1&&content.Acquisitions==0,"cancel cleanup exactly once/no read");cases.Add(new{name="PrepareAsync.cancel."+(failCleanup?"cleanup-fault":"clean"),passed=true});
            }
            foreach(var failCleanup in new[]{false,true})
            {
                using var abort=new CancellationTokenSource();var cleanup=new IOException("authored owned cancellation cleanup fault");var content=new Content(failCleanup?cleanup:null);using var handler=new Handler(content,HttpStatusCode.OK,abort);using var client=new HttpClient(handler,false);using var request=new HttpRequestMessage(HttpMethod.Post,"https://sse.invalid/authored");var actual=Prepare(new(client),"PrepareOwnedAsync",request,null,abort.Token);await Join(Rows[actual]).ConfigureAwait(false);
                if(failCleanup)Check(actual.IsFaulted&&Rows[actual].Direct is AggregateException aggregate&&aggregate.InnerExceptions[0] is OperationCanceledException ownedCleanupCancellation&&ownedCleanupCancellation.CancellationToken==abort.Token&&ReferenceEquals(aggregate.InnerExceptions[1],cleanup),"owned cancellation plus cleanup retained");else Check(actual.IsCanceled&&Rows[actual].Direct is OperationCanceledException ownedCancellation&&ownedCancellation.CancellationToken==abort.Token,"owned cancellation remains canceled");Check(content.Disposals==1&&content.Acquisitions==0&&handler.Sends==1,"owned cancel cleanup once/no detached send or body");cases.Add(new{name="PrepareOwnedAsync.cancel."+(failCleanup?"cleanup-fault":"clean"),passed=true});
            }
            await Settle().ConfigureAwait(false);Console.WriteLine(JsonSerializer.Serialize(new{cases,originals=Rows.Values.Select(View),faultGraph=Graph(),allOriginalsJoined=Rows.Values.All(row=>row.Joined)}));return 0;
        }
        catch(Exception error){Faults.Add(error);await Settle().ConfigureAwait(false);Console.Error.WriteLine(JsonSerializer.Serialize(new{failed=true,cases,originals=Rows.Values.Select(View),faultGraph=Graph()}));return 1;}
    }
    private static int? Id(Exception? error){if(error is null)return null;if(FaultIds.TryGetValue(error,out var id))return id;if(FaultIds.Count==1024)return null;id=FaultIds.Count;FaultIds.Add(error,id);return id;}
    private static object View(Row row)=>new{actualTaskId=row.Id,aliases=row.Aliases,row.Joined,status=row.Task.Status.ToString(),directFaultId=Id(row.Direct),aggregateFaultId=Id(row.Aggregate)};
    private static object Graph(){var queue=new Queue<Exception>();var seen=new HashSet<Exception>(ReferenceEqualityComparer.Instance);var nodes=new List<object>();var edges=0;var truncated=0;int? Add(Exception error){var id=Id(error);if(id is null){truncated++;return null;}if(seen.Add(error))queue.Enqueue(error);return id;}var roots=Faults.Select(Add).ToArray();while(queue.TryDequeue(out var error)){var children=error is AggregateException a?a.InnerExceptions.ToArray():error.InnerException is {} child?[child]:Array.Empty<Exception>();var refs=new List<int?>();foreach(var childError in children){if(edges++<4096)refs.Add(Add(childError));else truncated++;}nodes.Add(new{id=Id(error),type=error.GetType().FullName,error.Message,error.StackTrace,children=refs});}return new{roots,nodes,truncated};}
    private sealed class Content(Exception? fault):HttpContent
    {
        public int Disposals,Acquisitions;
        protected override Task<Stream> CreateContentReadStreamAsync(){Acquisitions++;throw new InvalidOperationException("no body claim during preparation failure");}
        protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context)=>throw new InvalidOperationException("no body read");
        protected override bool TryComputeLength(out long length){length=0;return true;}
        protected override void Dispose(bool disposing){if(disposing)Disposals++;base.Dispose(disposing);if(disposing&&fault is not null)throw fault;}
    }
    private sealed class Handler(Content content,HttpStatusCode status,CancellationTokenSource? abort=null):HttpMessageHandler
    {
        public int Sends;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {Check(request.RequestUri?.AbsoluteUri=="https://sse.invalid/authored","inert handler only");Sends++;var actual=Own("http.send",Task.FromResult(new HttpResponseMessage(status){Content=content}));abort?.Cancel();return actual;}
    }
}
