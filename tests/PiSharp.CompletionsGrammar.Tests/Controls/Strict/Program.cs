using System.Reflection;
using System.Security.Cryptography;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Providers;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Contracts;

if(args.Length!=2||args[0]!="--report")throw new ArgumentException("Supply --report.");
var model=new ModelDescriptor("strict-options","openai-completions","openrouter");
const string tool="{\"name\":\"strict_function\",\"description\":\"Inert\",\"parameters\":{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}},\"required\":[\"value\"]},\"constrainedSampling\":{\"type\":\"json_schema\",\"strict\":\"require\"}}";
TranscriptEntry System(string tools)=>new("system",JsonData.Parse("{\"role\":\"system\",\"content\":\"\",\"timestamp\":123,\"toolsAdded\":"+tools+"}"));
TranscriptEntry User(string text="Hello")=>new("user",JsonData.Parse(new JsonObject{["role"]="user",["content"]=text,["timestamp"]=123}.ToJsonString()));
var request=new ChatRequest(model,[System("["+tool+"]"),User()],123);
var outcomes=new List<object>();var passed=0;var failed=0;
void Check(bool v,string m){if(!v)throw new InvalidOperationException(m);}
async Task Run(string id,Func<Task> test){try{await test();passed++;outcomes.Add(new{id,passed=true});Console.WriteLine("PASS "+id);}catch(Exception e){failed++;outcomes.Add(new{id,passed=false,error=e.ToString()});Console.Error.WriteLine("FAIL "+id+": "+e);}}
CompletionsTranscriptProjectionOptions Projection(bool grammar=true,bool strict=true,int messages=256,int entries=65_536,int active=128,int declarations=1024)=>new(ToolDeclarations:new(SupportsStrictMode:strict,MaximumMessages:messages,MaximumEntryCharacters:entries,MaximumDeclarations:declarations,MaximumActiveTools:active){SupportsOpenAIGrammarTools=grammar},MaximumMessages:messages,MaximumEntryCharacters:entries);
CompletionsKeyAuthRequestFactory Factory(CompletionsTranscriptProjectionOptions? projection=null,CompletionsKeyAuthRequestOptions? options=null)=>new(new("https://strict-options.invalid/v1/chat/completions"),model,projection??Projection(),options);
async Task Positive(string id,ChatRequest input,CompletionsTranscriptProjectionOptions? projection=null,CompletionsKeyAuthRequestOptions? options=null,bool clone=false)
{
    var original=input.Messages.Select(m=>m.WireBody.ToString()).ToArray();var factory=Factory(projection,options);using var admitted=factory.Create(input,"inert-key");var admittedBody=JsonData.Parse(await admitted.Content!.ReadAsStringAsync()).Value.Clone();
    using var handler=new Handler();using var client=new HttpClient(handler);var wire=clone?factory.ResolvedWireOptions with{MaximumChunks=3}:factory.ResolvedWireOptions;
    var transport=new CompletionsHttpSseTransport(client,(r,t)=>factory.Create(r,"inert-key",t),completionsOptions:wire);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));var result=await new ChatClient(transport).CompleteAsync(input,deadline.Token);
    outcomes.Add(new{boundaryObservation=true,id,admittedWholeBody=admittedBody,actualWholeBody=handler.Body,handler.Attempts,stopReason=result.Message.StopReason.ToString(),failure=result.Failure?.ToString(),canonicalUnchanged=input.Messages.Select(m=>m.WireBody.ToString()).SequenceEqual(original),wireWithClone=clone});
    Check(result.Failure is null&&result.Message.StopReason==StopReason.Stop&&handler.Attempts==1,"Factory-admitted declarations did not complete exactly one send.");Check(handler.Body is{} sent&&JsonElement.DeepEquals(sent,admittedBody),"Complete sent body differs from the admitted body.");Check(input.Messages.Select(m=>m.WireBody.ToString()).SequenceEqual(original),"Mapping mutated canonical input.");
}
async Task Rejected(string id,ChatRequest input,CompletionsRequestFailure expected,CompletionsTranscriptProjectionOptions projection)
{
    using var handler=new Handler();using var client=new HttpClient(handler);var factory=Factory(projection);var transport=new CompletionsHttpSseTransport(client,(r,t)=>factory.Create(r,"inert-key",t),completionsOptions:factory.ResolvedWireOptions);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));var result=await new ChatClient(transport).CompleteAsync(input,deadline.Token);Check(result.Failure is not null&&handler.Attempts==0,"Invalid declarations reached send.");try{using var owned=factory.Create(input,"inert-key");}catch(CompletionsRequestException e){Check(e.Failure==expected,"Typed admission changed: "+e.Failure);outcomes.Add(new{boundaryObservation=true,id,zeroActualSends=handler.Attempts==0,typedFailure=e.Failure.ToString()});return;}throw new InvalidOperationException("Invalid declarations admitted.");
}
await Run("strict-required-function-grammar-off-control",()=>Positive("off",request,Projection(grammar:false)));
await Run("strict-required-function-grammar-enabled",()=>Positive("enabled",request));
await Run("strict-required-function-wire-with-clone",()=>Positive("clone",request,clone:true));
await Run("strict-required-function-model-bound-capabilities",()=>Positive("metadata",request,options:new(){ModelMetadata=JsonData.Parse("{\"id\":\"strict-options\",\"api\":\"openai-completions\",\"provider\":\"openrouter\",\"baseUrl\":\"https://strict-options.invalid/v1\",\"reasoning\":false,\"input\":[\"text\"],\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0},\"compat\":{\"supportsStrictMode\":true,\"supportsOpenAIGrammarTools\":true}}")}));
const string custom="{\"name\":\"emit\",\"description\":\"Inert grammar\",\"parameters\":{\"type\":\"object\",\"properties\":{\"code\":{\"type\":\"string\"}},\"required\":[\"code\"]},\"constrainedSampling\":{\"type\":\"grammar\",\"variants\":{\"openai_lark\":\"start: /.+/\"}}}";
await Run("strict-required-function-and-custom-declaration",()=>Positive("mixed",request with{Messages=[System("["+tool+","+custom+"]"),User()]}));
var manyMessages=request with{Messages=[System("["+tool+"]"),..Enumerable.Range(0,256).Select(_=>User())]};
await Run("configured-raised-message-limit-retained-by-wire",()=>Positive("messages",manyMessages,Projection(messages:300)));
var manyTools="["+string.Join(",",Enumerable.Range(0,129).Select(i=>"{\"name\":\"tool_"+i+"\",\"description\":\"Inert\",\"parameters\":{\"type\":\"object\",\"properties\":{},\"required\":[]}}"))+"]";
var manyToolsRequest=request with{Messages=[System(manyTools),User()]};
await Run("configured-raised-active-declaration-limit-retained-by-wire",()=>Positive("tools",manyToolsRequest,Projection(active:130,declarations:130)));
await Run("configured-raised-entry-limit-retained-by-wire",()=>Positive("entry",request with{Messages=[System("["+tool+"]"),User(new string('x',70_000))]},Projection(entries:75_000)));
await Run("strict-required-still-rejected-when-capability-false",()=>Rejected("strict-invalid",request,CompletionsRequestFailure.UnsupportedContent,Projection(strict:false)));
await Run("configured-lowered-message-limit-still-zero-send",()=>Rejected("messages-invalid",manyMessages,CompletionsRequestFailure.ResourceLimit,Projection(messages:256)));
await Run("configured-lowered-active-limit-still-zero-send",()=>Rejected("tools-invalid",manyToolsRequest,CompletionsRequestFailure.ResourceLimit,Projection(active:128,declarations:130)));
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);await File.WriteAllTextAsync(args[1],JsonSerializer.Serialize(new{schemaVersion=1,passed,failed,observations=outcomes,productionAiDll=Identity(typeof(ChatClient).Assembly),executedTestDll=Identity(Assembly.GetExecutingAssembly()),fullGate=false,phaseAcceptance=false},new JsonSerializerOptions{WriteIndented=true})+"\n");Console.WriteLine($"Strict grammar composition: {passed} passed, {failed} failed.");return failed==0?0:1;
static object Identity(Assembly a){var b=File.ReadAllBytes(a.Location);return new{path=a.Location,bytes=b.Length,sha256=Convert.ToHexStringLower(SHA256.HashData(b)),productVersion=a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion};}
sealed class Handler:HttpMessageHandler{public int Attempts;public JsonElement? Body;protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken t){Attempts++;Body=JsonData.Parse(await r.Content!.ReadAsStringAsync(t)).Value.Clone();return new(HttpStatusCode.OK){Content=new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n")};}}
