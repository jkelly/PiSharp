using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Agent;
using PiSharp.Contracts;

if(args.Length!=4||args[0]!="--fixture"||args[2]!="--report")throw new ArgumentException("Supply --fixture/--report.");
var bytes=File.ReadAllBytes(args[1]);if(Convert.ToHexStringLower(SHA256.HashData(bytes))!="5a542f36b593e9abc93bcdbe2d232f6762376e41094dacecea7adb25c54cd682")throw new InvalidOperationException("Complete genuine Agent fixture changed.");
using var document=JsonDocument.Parse(bytes);var capture=document.RootElement;
if(capture.GetProperty("sourceSha").GetString()!="d86654abb8862e201933517d6f1fce9f88dd117f"||capture.GetProperty("observations").GetArrayLength()!=8||capture.GetProperty("sourceOrSdkEdits").GetInt32()!=0||capture.GetProperty("prohibitedNetworkCalls").GetInt32()!=0)throw new InvalidOperationException("Source Agent authority changed.");
var evidence=new List<object>();var passed=0;var failed=0;
foreach(var source in capture.GetProperty("observations").EnumerateArray())
{
    var id=source.GetProperty("profile").GetProperty("id").GetString()!;
    try { evidence.Add(await RoundTrip(source));passed++;Console.WriteLine("PASS "+id); }
    catch(Exception error){failed++;evidence.Add(new{id,passed=false,error=error.ToString(),wholeGenuineSourceProfile=source.Clone()});Console.Error.WriteLine("FAIL "+id+": "+error);}
}
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[3]))!);await File.WriteAllTextAsync(args[3],JsonSerializer.Serialize(new{schemaVersion=1,passed,failed,fixtureSha256=Convert.ToHexStringLower(SHA256.HashData(bytes)),observations=evidence,productionAiDll=Identity(typeof(ChatClient).Assembly),productionAgentDll=Identity(typeof(AgentLoopRunner).Assembly),executedTestDll=Identity(Assembly.GetExecutingAssembly()),fullAgentApiAcceptance=false,fullGate=false,phaseAcceptance=false},new JsonSerializerOptions{WriteIndented=true})+"\n");
Console.WriteLine($"Grammar Agent boundary: {passed} passed, {failed} failed.");return failed==0?0:1;

static async Task<object> RoundTrip(JsonElement source)
{
    var profile=source.GetProperty("profile");var id=profile.GetProperty("id").GetString()!;var property=profile.GetProperty("property").GetString()!;var rawModel=source.GetProperty("model");
    var model=new ModelDescriptor(rawModel.GetProperty("id").GetString()!,rawModel.GetProperty("api").GetString()!,rawModel.GetProperty("provider").GetString()!);
    var options=source.GetProperty("options");var requestOptions=new CompletionsKeyAuthRequestOptions(MaxTokens:options.GetProperty("maxTokens").GetDouble(),Temperature:options.GetProperty("temperature").GetDouble(),CacheRetention:CompletionsCacheRetention.Short,SessionId:options.GetProperty("sessionId").GetString()){ModelMetadata=JsonData.FromElement(rawModel)};
    var first=new OwnedBody(source.GetProperty("firstResponseWire").GetString()!,true);var second=new OwnedBody(source.GetProperty("secondResponseWire").GetString()!);var responses=new[]{new BodyContent(first),new BodyContent(second)};
    var bodies=new List<JsonElement>();var requests=new List<HttpRequestMessage>();var publications=new List<List<JsonElement>>();var hooks=new CompletionsLifecycleHooks{OnSourcePublished=item=>publications[^1].Add(item.Emission.Raw.Value.Clone())};
    using var handler=new Handler(async(message,token)=>{requests.Add(message);bodies.Add(JsonData.Parse(await message.Content!.ReadAsStringAsync(token)).Value.Clone());Check(requests.Count<=2,"Third provider request attempted.");return new(HttpStatusCode.OK){Content=responses[requests.Count-1]};});using var client=new HttpClient(handler);
    CompletionsKeyAuthRequestFactory factory;
    try{factory=new(new(source.GetProperty("requests")[0].GetProperty("url").GetString()!),model,options:requestOptions);}
    catch(CompletionsRequestException error){Check(handler.Attempts==0,"Rejected grammar metadata entered SendAsync.");throw new InvalidOperationException($"Actual constructor admission {error.Failure}; sends={handler.Attempts}; no run/effect was started.",error);}
    var transport=CompletionsHttpSseTransport.FromAsyncRequestFactory(client,(input,token)=>{publications.Add([]);return factory.CreateAsync(input,options.GetProperty("apiKey").GetString()!,hooks,token);},new(){Hooks=hooks},CompletionsSourceEventProjection.CaptureOwnedSnapshots(factory.ResolvedWireOptions));
    var adapters=source.GetProperty("toolExecutions").EnumerateArray().Select(e=>e.GetProperty("name").GetString()!).Distinct(StringComparer.Ordinal).Select(name=>new Adapter(name,property,source)).ToArray();var policy=new Policy();var invoker=new ToolInvoker(adapters,policy);var preflights=0;
    var toolHooks=new Hooks(()=>Interlocked.Increment(ref preflights));var scheduler=new ToolBatchScheduler(adapters.Select(a=>new ToolDefinition(a.Name,invoker)),toolHooks);
    var runner=new AgentLoopRunner(new TurnRunner(new ChatClient(transport,capacity:1),scheduler),model,()=>123);
    var assistantEntered=Gate();var assistantRelease=Gate();var resultEntered=Gate();var resultRelease=Gate();var toolMessages=new List<JsonElement>();var trace=new ConcurrentQueue<string>();
    var sink=new Sink(async(observation,token)=>{
        if(observation is AssistantMessageEnded&&requests.Count==1){Check(first.Disposed&&responses[0].Disposed,"Assistant commit preceded HTTP cleanup.");await ThrowsDisposed(requests[0]);trace.Enqueue("assistant-sink-enter");assistantEntered.TrySetResult();await assistantRelease.Task;trace.Enqueue("assistant-sink-release");}
        if(observation is ToolResultMessageEnded ended){toolMessages.Add(ToolResultMessageMaterializer.ToTranscript(ended.Message,123).WireBody.Value.Clone());trace.Enqueue("result-sink-enter:"+ended.Message.ToolCallId);resultEntered.TrySetResult();await resultRelease.Task;trace.Enqueue("result-sink-release:"+ended.Message.ToolCallId);}
    });
    var initial=source.GetProperty("initialMessages").EnumerateArray().Select(Entry).ToImmutableArray();var originals=initial.Select(entry=>entry.WireBody.ToString()).ToArray();
    using var cancellation=new CancellationTokenSource();var running=runner.RunAsync(initial,new((snapshot,_)=>ValueTask.FromResult(new ChatRequest(snapshot.Model,snapshot.Transcript,123))),sink,cancellation.Token);
    AgentLoopResult? result=null;try{
        await Task.WhenAny(first.CleanupEntered.Task,running).WaitAsync(TimeSpan.FromSeconds(5));Check(first.CleanupEntered.Task.IsCompleted,"Provider failed before the cleanup barrier.");Check(!running.IsCompleted&&!assistantEntered.Task.IsCompleted&&adapters.Sum(a=>a.Executions)==0&&policy.Authorizations==0&&preflights==0,"Cleanup did not guard commit/policy/effect.");trace.Enqueue("cleanup-release");first.ReleaseCleanup.TrySetResult();
        await assistantEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));Check(adapters.Sum(a=>a.Executions)==0&&policy.Authorizations==0&&preflights==0,"Tool escaped awaited assistant commit.");assistantRelease.TrySetResult();
        await resultEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));var calls=source.GetProperty("toolExecutions").GetArrayLength();Check(requests.Count==1&&!running.IsCompleted,"Continuation escaped tool-result commit.");Check(adapters.Sum(a=>a.Executions)==calls&&policy.Authorizations==calls&&preflights==calls,"Unexpected tool boundary counts.");foreach(var adapter in adapters)foreach(var action in adapter.Actions)Check(ReferenceEquals(action.Value,policy.Actions[action.Key]),"Policy/effect action identity changed.");resultRelease.TrySetResult();
        result=await running.WaitAsync(TimeSpan.FromSeconds(5));Check(result.Reason==AgentLoopStopReason.Completed&&result.Turns.Length==2&&requests.Count==2,"Agent did not complete two actual turns.");
        Check(result.Transcript.Take(initial.Length).Select(entry=>entry.WireBody.ToString()).SequenceEqual(originals),"Canonical initial history changed.");
        for(var i=0;i<2;i++){Check(JsonElement.DeepEquals(bodies[i],source.GetProperty("requests")[i].GetProperty("bodyJson")),"Whole Source SDK request differs at turn"+i);var expected=source.GetProperty("providerEmissions")[i];Check(publications[i].Count==expected.GetArrayLength(),"Whole provider publication count differs.");for(var n=0;n<publications[i].Count;n++)Check(JsonElement.DeepEquals(publications[i][n].GetProperty("value"),expected[n].GetProperty("value"))&&JsonElement.DeepEquals(publications[i][n].GetProperty("ownUndefinedPaths"),expected[n].GetProperty("ownUndefinedPaths")),"Whole provider publication differs at "+i+":"+n);}
        Check(toolMessages.Count==source.GetProperty("toolMessages").GetArrayLength(),"Tool message count differs.");for(var i=0;i<toolMessages.Count;i++)Check(JsonElement.DeepEquals(toolMessages[i],source.GetProperty("toolMessages")[i]),"Complete actual Source Agent tool-result message differs.");
        foreach(var call in result.Turns[0].Result.Chat.Message.Content.OfType<ToolCallContent>()){var expected=source.GetProperty("toolExecutions").EnumerateArray().Single(e=>e.GetProperty("id").GetString()==call.Id).GetProperty("args");Check(JsonElement.DeepEquals(call.Arguments.Value,expected),"Grammar raw input did not reach canonical JSON object exactly.");}
        Check(first.Disposals==1&&second.Disposals==1&&responses.All(r=>r.Disposed)&&!handler.Disposed,"Owned HTTP cleanup/borrowed-client lifetime differs.");
        var nativeTranscript=JsonSerializer.SerializeToElement(result.Transcript.Select(e=>e.WireBody.Value));var sourceTranscript=source.GetProperty("contexts")[1].EnumerateArray().Concat([source.GetProperty("result")[source.GetProperty("result").GetArrayLength()-1]]).Select(v=>v.Clone()).ToArray();
        return new{id,passed=true,actualPiAgentSource=true,actualNativeAgentLoop=true,wholeSourceRequests=source.GetProperty("requests").Clone(),nativeWholeBodies=bodies,sourceCompleteProviderPublications=source.GetProperty("providerEmissions").Clone(),nativeCompleteProviderPublications=publications,sourceCompleteAgentEvents=source.GetProperty("events").Clone(),sourceToolMessages=source.GetProperty("toolMessages").Clone(),nativeToolMessages=toolMessages,actualCalls=adapters.Sum(a=>a.Executions),policy.Authorizations,preflights,allFinalActionsIdentical=true,barrierTrace=trace.ToArray(),firstCleanupCount=first.Disposals,secondCleanupCount=second.Disposals,completeNativeCanonicalTranscript=nativeTranscript,completeSourceCanonicalTranscript=sourceTranscript,wholeAgentTranscriptMatches=JsonElement.DeepEquals(nativeTranscript,JsonSerializer.SerializeToElement(sourceTranscript)),originalSourceThinkingLevelMetadataQualificationOpen=true};
    }finally{assistantRelease.TrySetResult();resultRelease.TrySetResult();first.ReleaseCleanup.TrySetResult();cancellation.Cancel();try{await running.WaitAsync(TimeSpan.FromSeconds(5));}catch{} }
}
static TranscriptEntry Entry(JsonElement body)=>new(body.GetProperty("role").GetString()!,JsonData.FromElement(body));
static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
static TaskCompletionSource Gate()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
static object Identity(Assembly assembly){var b=File.ReadAllBytes(assembly.Location);return new{path=assembly.Location,bytes=b.Length,sha256=Convert.ToHexStringLower(SHA256.HashData(b)),productVersion=assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion};}
static async Task ThrowsDisposed(HttpRequestMessage request){try{_=await request.Content!.ReadAsStringAsync();}catch(ObjectDisposedException){return;}throw new InvalidOperationException("Request content was not disposed.");}
sealed class Adapter(string name,string property,JsonElement source):IInvocationPreparedToolAdapter
{
    public string Name=>name;public int Executions;public ConcurrentDictionary<string,PreparedToolAction> Actions=new(StringComparer.Ordinal);
    public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation,CancellationToken token)=>ValueTask.FromResult(new PreparedToolAction(Name,"grammar",PreparedToolActionKind.Path,"authored-inert-grammar-target",invocation.Call.Arguments,[],null,ImmutableDictionary<string,string>.Empty));
    public ValueTask<bool> ValidateAsync(PreparedToolAction action,CancellationToken token)=>ValueTask.FromResult(action.Arguments.Value.ValueKind==JsonValueKind.Object&&action.Arguments.Value.EnumerateObject().Count()==1&&action.Arguments.Value.TryGetProperty(property,out var value)&&value.ValueKind==JsonValueKind.String);
    public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action,CancellationToken token)=>throw new InvalidOperationException("Invocation identity discarded.");
    public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation,PreparedToolAction action,ToolProgressCallback progress,CancellationToken token){token.ThrowIfCancellationRequested();Actions[invocation.Call.Id]=action;Interlocked.Increment(ref Executions);return ValueTask.FromResult(ToolResult.FromJson(JsonData.FromElement(source.GetProperty("rawResults").EnumerateArray().Single(r=>r.GetProperty("id").GetString()==invocation.Call.Id).GetProperty("result"))));}
}
sealed class Policy:IToolActionPolicy{public int Authorizations;public ConcurrentDictionary<string,PreparedToolAction> Actions=new(StringComparer.Ordinal);public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation,PreparedToolAction action,CancellationToken token){token.ThrowIfCancellationRequested();Actions[invocation.Call.Id]=action;Interlocked.Increment(ref Authorizations);return ValueTask.FromResult(new ToolActionAuthorization(true));}}
sealed class Hooks(Action before):IToolHooks{public ValueTask<ToolPreflightDecision> BeforeExecutionAsync(ToolInvocation invocation,CancellationToken token){before();return ValueTask.FromResult(ToolPreflightDecision.Allow);}public ValueTask<ToolResult> AfterExecutionAsync(ToolInvocation invocation,ToolResult result,CancellationToken token)=>ValueTask.FromResult(result);}
sealed class Sink(Func<AgentEvent,CancellationToken,ValueTask> emit):IAgentEventSink{public ValueTask EmitAsync(AgentEvent e,CancellationToken token)=>emit(e,token);}
sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler{public int Attempts;public bool Disposed;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){Attempts++;return send(request,token);}protected override void Dispose(bool disposing){Disposed|=disposing;base.Dispose(disposing);}}
sealed class BodyContent(OwnedBody body):HttpContent{public bool Disposed;protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token)=>Task.FromResult<Stream>(body);protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context)=>throw new InvalidOperationException("Unexpected buffering.");protected override bool TryComputeLength(out long length){length=0;return false;}protected override void Dispose(bool disposing){Disposed|=disposing;base.Dispose(disposing);}}
sealed class OwnedBody(string wire,bool gated=false):MemoryStream(Encoding.UTF8.GetBytes(wire),false){public TaskCompletionSource CleanupEntered= new(TaskCreationOptions.RunContinuationsAsynchronously),ReleaseCleanup=new(TaskCreationOptions.RunContinuationsAsynchronously);public bool Disposed;public int Disposals;private Task? _closing;public override ValueTask DisposeAsync()=>new(_closing??=CloseCore());private async Task CloseCore(){Disposals++;CleanupEntered.TrySetResult();if(gated)await ReleaseCleanup.Task;Disposed=true;base.Dispose(true);}}
