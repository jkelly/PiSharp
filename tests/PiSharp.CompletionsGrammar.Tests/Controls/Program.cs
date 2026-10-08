using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.AI.Providers;
using PiSharp.Agent;
using PiSharp.Contracts;

if (args.Length != 2 || args[0] != "--report") throw new ArgumentException("Supply --report.");
var model = new ModelDescriptor("grammar-controls", "openai-completions", "openrouter");
const string key = "authored-inert-key";
const string toolJson = "{\"name\":\"emit\",\"description\":\"Inert grammar adapter.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"code\":{\"type\":\"string\"}},\"required\":[\"code\"]},\"constrainedSampling\":{\"type\":\"grammar\",\"variants\":{\"openai_lark\":\"start: /.+/\"}}}";
JsonData Tool(Action<JsonObject>? mutate = null) { var node = JsonNode.Parse(toolJson)!.AsObject(); mutate?.Invoke(node); return JsonData.Parse(node.ToJsonString()); }
TranscriptEntry System(JsonData tool) => new("system", JsonData.Parse("{\"role\":\"system\",\"content\":\"Instruction.\",\"toolsAdded\":["+tool+"],\"timestamp\":123}"));
var user = new TranscriptEntry("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"Question.\",\"timestamp\":123}"));
ChatRequest Request(JsonData? tool = null) => new(model, [System(tool ?? Tool()), user], 123);
CompletionsTranscriptProjectionOptions Projection(CompletionsToolDeclarationProjectionOptions? tools = null) => new(ToolDeclarations:tools ?? new() { SupportsOpenAIGrammarTools=true });
CompletionsKeyAuthRequestFactory Factory(CompletionsTranscriptProjectionOptions? projection = null, CompletionsKeyAuthRequestOptions? options = null) => new(new("https://grammar-controls.invalid/v1/chat/completions"), model, projection ?? Projection(), options);
var outcomes = new List<object>(); var passed=0; var failed=0;
void Check(bool value,string reason) { if(!value) throw new InvalidOperationException(reason); }
async Task Run(string name,Func<Task> action) { try { await action();passed++;outcomes.Add(new{name,passed=true});Console.WriteLine("PASS "+name); } catch(Exception error) { failed++;outcomes.Add(new{name,passed=false,error=error.ToString()});Console.Error.WriteLine("FAIL "+name+": "+error); } }
async Task Reject(ChatRequest request,CompletionsRequestFailure expected,CompletionsTranscriptProjectionOptions? projection=null,CompletionsKeyAuthRequestOptions? options=null)
{
    using var handler=new CounterHandler();using var client=new HttpClient(handler);var constructions=0;var payloads=0;var original=request.Messages.Select(e=>e.WireBody.ToString()).ToArray();
    var hooks=new CompletionsLifecycleHooks{OnPayload=(_,_,_)=>{payloads++;return ValueTask.FromResult<JsonData?>(null);}};
    var transport=CompletionsHttpSseTransport.FromAsyncRequestFactory(client,(r,t)=>{constructions++;return Factory(projection,options).CreateAsync(r,key,hooks,t);},new(){Hooks=hooks});
    using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));var result=await new ChatClient(transport).CompleteAsync(request,deadline.Token);
    Check(result.Failure is not null&&constructions==1&&payloads==0&&handler.Attempts==0,"Invalid grammar reached payload/send.");
    Check(request.Messages.Select(e=>e.WireBody.ToString()).SequenceEqual(original),"Admission mutated canonical history.");
    try { using var owned=Factory(projection,options).Create(request,key); } catch(CompletionsRequestException error) { Check(error.Failure==expected&&!error.Message.Contains("PRIVATE_",StringComparison.Ordinal),"Typed sanitized rejection differs: "+error.Failure);return; }
    throw new InvalidOperationException("Invalid grammar admitted.");
}
foreach(var (name,mutate) in new (string,Action<JsonObject>)[] {
    ("missing-variants",t=>t["constrainedSampling"]!.AsObject().Remove("variants")),
    ("empty-lark-only",t=>t["constrainedSampling"]!["variants"]!["openai_lark"]=" \t "),
    ("nonstring-lark-only",t=>t["constrainedSampling"]!["variants"]!["openai_lark"]=17),
    ("unknown-only-variant",t=>t["constrainedSampling"]!["variants"]=new JsonObject{["future"]="PRIVATE_GRAMMAR"}),
    ("nonobject-schema",t=>t["parameters"]=new JsonArray()),
    ("nonobject-schema-type",t=>t["parameters"]!["type"]="array"),
    ("missing-required",t=>t["parameters"]!.AsObject().Remove("required")),
    ("multiple-required",t=>t["parameters"]!["required"]=new JsonArray("code","other")),
    ("nonstring-required",t=>t["parameters"]!["required"]=new JsonArray(17)),
    ("required-property-missing",t=>t["parameters"]!["required"]=new JsonArray("other")),
    ("required-property-nonstring",t=>t["parameters"]!["properties"]!["code"]!["type"]="number")
}) await Run(name+"-zero-send",()=>Reject(Request(Tool(mutate)),CompletionsRequestFailure.UnsupportedContent));
foreach(var (name,arguments) in new[]{("missing","{}"),("nonstring","{\"code\":17}"),("null","{\"code\":null}")})
    await Run("grammar-replay-"+name+"-zero-send",()=>{
        var assistant=new TranscriptEntry("assistant",JsonData.Parse("{\"role\":\"assistant\",\"api\":\"openai-completions\",\"provider\":\"openrouter\",\"model\":\"grammar-controls\",\"stopReason\":\"toolUse\",\"content\":[{\"type\":\"toolCall\",\"id\":\"call\",\"name\":\"emit\",\"arguments\":"+arguments+"}],\"timestamp\":123}"));
        return Reject(Request() with{Messages=[System(Tool()),user,assistant]},CompletionsRequestFailure.InvalidTranscript);
    });
await Run("custom-tool-choice-empty-name-zero-send",()=>Reject(Request(),CompletionsRequestFailure.UnsupportedContent,options:new(ToolChoice:JsonData.Parse("{\"type\":\"custom\",\"custom\":{\"name\":\"\"}}"))));
await Run("allowed-tools-invalid-mode-zero-send",()=>Reject(Request(),CompletionsRequestFailure.UnsupportedContent,options:new(ToolChoice:JsonData.Parse("{\"type\":\"allowed_tools\",\"allowed_tools\":{\"mode\":\"PRIVATE_MODE\",\"tools\":[]}}"))));
await Run("allowed-tools-invalid-entry-zero-send",()=>Reject(Request(),CompletionsRequestFailure.UnsupportedContent,options:new(ToolChoice:JsonData.Parse("{\"type\":\"allowed_tools\",\"allowed_tools\":{\"mode\":\"auto\",\"tools\":[17]}}"))));
await Run("grammar-definition-utf8-output-budget-zero-send",()=>Reject(Request(Tool(t=>t["constrainedSampling"]!["variants"]!["openai_lark"]=new string('界',120))),CompletionsRequestFailure.ResourceLimit,Projection(new(MaximumOutputBytes:350){SupportsOpenAIGrammarTools=true})));
await Run("grammar-whole-declaration-history-budget-zero-send",()=>Reject(Request() with{Messages=[System(Tool()),user,System(Tool())]},CompletionsRequestFailure.ResourceLimit,Projection(new(MaximumDeclarations:1){SupportsOpenAIGrammarTools=true})));
await Run("grammar-final-expanded-payload-byte-budget-zero-send",async()=>{using var positive=Factory().Create(Request(),key);var raw=await positive.Content!.ReadAsStringAsync();await Reject(Request(),CompletionsRequestFailure.ResourceLimit,options:new(MaximumPayloadBytes:Encoding.UTF8.GetByteCount(raw)-1));});
await Run("grammar-disabled-invalid-variant-function-fallback",async()=>{using var owned=Factory(Projection(new())).Create(Request(Tool(t=>t["constrainedSampling"]!["variants"]=new JsonObject{["future"]="unsupported"})),key);using var json=JsonDocument.Parse(await owned.Content!.ReadAsStringAsync());Check(json.RootElement.GetProperty("tools")[0].GetProperty("type").GetString()=="function","Disabled grammar failed Source function fallback.");});
await Run("grammar-exact-declaration-output-byte-boundary",()=>{var request=Request();var unlimited=new CompletionsToolDeclarationProjector(new(){SupportsOpenAIGrammarTools=true}).Project(request);var bytes=Encoding.UTF8.GetByteCount(unlimited.ToString());var bounded=new CompletionsToolDeclarationProjector(new(MaximumOutputCharacters:unlimited.ToString().Length,MaximumOutputBytes:bytes){SupportsOpenAIGrammarTools=true});Check(bounded.Project(request).ToString()==unlimited.ToString(),"Exact grammar declaration quota rejected.");return Task.CompletedTask;});

string Wire(object call,string? finish="tool_calls") => "data: "+JsonSerializer.Serialize(new{id="grammar-response",objectKind="unused",choices=new[]{new{delta=new{tool_calls=new[]{call}},finish_reason=finish}}}).Replace("\"objectKind\":\"unused\",","")+"\n\ndata: [DONE]\n\n";
object Custom(object? input,string id="call",string name="emit",int index=0)=>new{index,id,type="custom",custom=new{name,input}};
await Run("custom-nonstring-input-no-effect",()=>Negative(Wire(Custom(17)),providerFailure:true));
await Run("custom-null-input-is-empty-positive-policy-denial",()=>Negative(Wire(Custom(null)),deny:true));
await Run("custom-unfinished-eof-no-effect",()=>Negative(Wire(Custom("unfinished"),null),providerFailure:true));
await Run("custom-missing-final-identity-no-effect",()=>Negative(Wire(Custom("input",id:"")),providerFailure:true));
await Run("custom-input-content-budget-no-effect",()=>Negative(Wire(Custom(new string('x',4096))),providerFailure:true,wireOptions:new(MaximumContentCharacters:512){SupportsOpenAIGrammarTools=true}));
await Run("custom-stream-depth-budget-no-effect",()=>Negative(Wire(Custom("input")),providerFailure:true,wireOptions:new(MaximumJsonDepth:3){SupportsOpenAIGrammarTools=true}));
await Run("custom-disabled-wire-unsupported-no-effect",()=>Negative(Wire(Custom("input")),providerFailure:true,wireOptions:new()));
await Run("custom-length-finish-truncated-no-effect",()=>Negative(Wire(Custom("input"),"length")));
await Run("custom-policy-denial-no-effect",()=>Negative(Wire(Custom("input")),deny:true));
await Run("custom-adapter-schema-denial-no-policy-or-effect",()=>Negative(Wire(Custom("input")),invalidAdapter:true));
await Run("custom-transformed-arguments-revalidated-no-policy-or-effect",()=>Negative(Wire(Custom("input")),invalidTransform:true));
await Run("custom-assistant-commit-failure-no-policy-or-effect",()=>Negative(Wire(Custom("input")),failSink:true));
await Run("custom-cancel-during-owned-cleanup-no-policy-or-effect",()=>Negative(Wire(Custom("input")),cancelCleanup:true));

async Task Negative(string wire,bool providerFailure=false,bool deny=false,bool invalidAdapter=false,bool invalidTransform=false,bool failSink=false,bool cancelCleanup=false,OpenAICompletionsWireOptions? wireOptions=null)
{
    var body=new OwnedBody(wire,cancelCleanup);var content=new BodyContent(body);var requests=new List<HttpRequestMessage>();
    using var handler=new CounterHandler(message=>{requests.Add(message);return new(HttpStatusCode.OK){Content=content};});using var client=new HttpClient(handler);var factory=Factory();
    var adapter=new Adapter(invalidAdapter);var policy=new Policy(!deny);var preflight=0;var commits=0;
    var transforms=invalidTransform?new ToolActionTransform[]{(_,action,_)=>ValueTask.FromResult(action with{Arguments=JsonData.Parse("{\"code\":17}")})}:[];
    var invoker=new ToolInvoker([adapter],policy,transforms);var scheduler=new ToolBatchScheduler([new("emit",invoker)],new Hooks(()=>preflight++));
    var transport=new CompletionsHttpSseTransport(client,(r,t)=>factory.Create(r,key,t),completionsOptions:wireOptions ?? factory.ResolvedWireOptions);
    var turn=new TurnRunner(new ChatClient(transport,capacity:1),scheduler);using var canceled=new CancellationTokenSource();
    var sink=new Sink(async(e,t)=>{if(e is AssistantMessageEnded){Check(body.Disposed&&content.Disposed,"Commit preceded owned HTTP cleanup.");Check(adapter.Executions==0&&policy.Authorizations==0&&preflight==0,"Effect escaped assistant commit.");commits++;if(failSink)throw new InvalidOperationException("Authored commit failure.");}await Task.CompletedTask;});
    var running=turn.RunAsync(Request(),sink,canceled.Token);TurnResult? result=null;Exception? caught=null;
    try
    {
        if(cancelCleanup){await Task.WhenAny(body.CleanupEntered.Task,running).WaitAsync(TimeSpan.FromSeconds(5));Check(body.CleanupEntered.Task.IsCompleted&&!running.IsCompleted&&commits==0&&adapter.Executions==0,"Cleanup barrier missing.");canceled.Cancel();body.ReleaseCleanup.TrySetResult();}
        try{result=await running.WaitAsync(TimeSpan.FromSeconds(5));}catch(Exception error){caught=error;}
        Check(handler.Attempts==1&&body.Disposals==1&&body.Disposed&&content.Disposed&&adapter.Executions==0,"Negative path escaped effect or cleanup ownership.");
        if(failSink)Check(caught is InvalidOperationException&&commits==1&&policy.Authorizations==0&&preflight==0,"Failed commit admitted work.");
        else if(cancelCleanup)Check(caught is OperationCanceledException&&commits==0&&policy.Authorizations==0&&preflight==0,"Canceled cleanup admitted work.");
        else{Check(caught is null&&result is not null&&commits==1,"Negative turn did not settle.");if(providerFailure)Check(result!.Chat.Failure is not null&&result.Chat.Message.StopReason==StopReason.Error&&preflight==0&&policy.Authorizations==0,"Failed provider admitted tools.");else if(invalidAdapter||invalidTransform)Check(preflight==1&&policy.Authorizations==0,"Invalid final adapter action reached policy.");else if(deny)Check(preflight==1&&policy.Authorizations==1,"Policy denial skipped authorization.");else Check(result!.Chat.Message.StopReason==StopReason.Length&&policy.Authorizations==0,"Truncated grammar was executable.");}
        foreach(var request in requests){try{_=await request.Content!.ReadAsStringAsync();throw new InvalidOperationException("Request escaped disposal.");}catch(ObjectDisposedException){}}
        outcomes.Add(new{negativeObservation=true,providerFailure,deny,invalidAdapter,invalidTransform,failSink,cancelCleanup,preflight,commits,policy.Authorizations,adapter.Executions,body.Disposals,stopReason=result?.Chat.Message.StopReason.ToString(),nativeFailure=result?.Chat.Failure?.ToString(),caughtType=caught?.GetType().FullName});
    }
    finally{body.ReleaseCleanup.TrySetResult();canceled.Cancel();try{await running.WaitAsync(TimeSpan.FromSeconds(5));}catch{}}
}
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);await File.WriteAllTextAsync(args[1],JsonSerializer.Serialize(new{schemaVersion=1,passed,failed,observations=outcomes,productionAiDll=Identity(typeof(ChatClient).Assembly),productionAgentDll=Identity(typeof(TurnRunner).Assembly),executedTestDll=Identity(Assembly.GetExecutingAssembly()),fullGate=false,phaseAcceptance=false},new JsonSerializerOptions{WriteIndented=true})+"\n");Console.WriteLine($"Grammar controls: {passed} passed, {failed} failed.");return failed==0?0:1;
static object Identity(Assembly assembly){var b=File.ReadAllBytes(assembly.Location);return new{path=assembly.Location,bytes=b.Length,sha256=Convert.ToHexStringLower(SHA256.HashData(b)),productVersion=assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion};}
sealed class Adapter(bool deny):IPreparedToolAdapter{public string Name=>"emit";public int Executions;public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation i,CancellationToken t)=>ValueTask.FromResult(new PreparedToolAction(Name,"grammar",PreparedToolActionKind.Path,"authored-inert-target",i.Call.Arguments,[],null,ImmutableDictionary<string,string>.Empty));public ValueTask<bool> ValidateAsync(PreparedToolAction a,CancellationToken t)=>ValueTask.FromResult(!deny&&a.Arguments.Value.TryGetProperty("code",out var value)&&value.ValueKind==JsonValueKind.String);public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction a,CancellationToken t){Executions++;return ValueTask.FromResult(ToolResult.Success("Unexpected effect."));}}
sealed class Policy(bool allow):IToolActionPolicy{public int Authorizations;public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation i,PreparedToolAction a,CancellationToken t){Authorizations++;return ValueTask.FromResult(new ToolActionAuthorization(allow));}}
sealed class Hooks(Action before):IToolHooks{public ValueTask<ToolPreflightDecision> BeforeExecutionAsync(ToolInvocation i,CancellationToken t){before();return ValueTask.FromResult(ToolPreflightDecision.Allow);}public ValueTask<ToolResult> AfterExecutionAsync(ToolInvocation i,ToolResult r,CancellationToken t)=>ValueTask.FromResult(r);}
sealed class Sink(Func<AgentEvent,CancellationToken,ValueTask> emit):IAgentEventSink{public ValueTask EmitAsync(AgentEvent e,CancellationToken t)=>emit(e,t);}
sealed class CounterHandler(Func<HttpRequestMessage,HttpResponseMessage>? response=null):HttpMessageHandler{public int Attempts;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken t){Attempts++;return Task.FromResult(response?.Invoke(r)??new(HttpStatusCode.OK){Content=new StringContent("data: [DONE]\n\n")});}}
sealed class BodyContent(OwnedBody body):HttpContent{public bool Disposed;protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken t)=>Task.FromResult<Stream>(body);protected override Task SerializeToStreamAsync(Stream s,TransportContext? c)=>throw new InvalidOperationException("Unexpected buffering.");protected override bool TryComputeLength(out long l){l=0;return false;}protected override void Dispose(bool disposing){Disposed|=disposing;base.Dispose(disposing);}}
sealed class OwnedBody(string wire,bool gated):MemoryStream(Encoding.UTF8.GetBytes(wire),false){public TaskCompletionSource CleanupEntered=new(TaskCreationOptions.RunContinuationsAsynchronously),ReleaseCleanup=new(TaskCreationOptions.RunContinuationsAsynchronously);public bool Disposed;public int Disposals;private Task? _closing;public override ValueTask DisposeAsync()=>new(_closing??=CloseCore());private async Task CloseCore(){Disposals++;CleanupEntered.TrySetResult();if(gated)await ReleaseCleanup.Task;Disposed=true;base.Dispose(true);}}
