using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Contracts;

if (args.Length != 2 || args[0] != "--report") throw new ArgumentException("Supply --report.");
var model = new ModelDescriptor("anchored-controls", "openai-completions", "openrouter");
const string key = "authored-inert-key";
const string alpha = "{\"name\":\"alpha\",\"description\":\"Alpha.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"number\"}},\"required\":[\"value\"]}}";
const string inspect = "{\"name\":\"inspect\",\"description\":\"Inspect.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"number\"}},\"required\":[\"value\"]}}";
TranscriptEntry System(string tools, string extra = "") => new("system", JsonData.Parse("{\"role\":\"system\",\"content\":\"Instruction.\",\"toolsAdded\":" + tools + extra + ",\"timestamp\":123}"));
ChatRequest Request(TranscriptEntry later) => new(model, [System("["+alpha+"]"), new("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"Question.\",\"timestamp\":123}")), later], 123);
var request = Request(System("["+inspect+"]"));
CompletionsTranscriptProjectionOptions Projection(CompletionsToolDeclarationProjectionOptions? tools = null) => new(SupportsMidConversationSystemMessages:true, ToolDeclarations:tools) { SupportsMidConversationToolAdditions = true };
CompletionsKeyAuthRequestFactory Factory(CompletionsTranscriptProjectionOptions? projection = null, CompletionsKeyAuthRequestOptions? options = null) => new(new("https://anchored-controls.invalid/v1/chat/completions"), model, projection ?? Projection(), options);
void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
var outcomes = new List<object>(); var passed = 0; var failed = 0;
async Task Run(string name, Func<Task> action) { try { await action(); passed++; outcomes.Add(new { name, passed=true }); Console.WriteLine("PASS "+name); } catch(Exception error) { failed++; outcomes.Add(new { name, passed=false, error=error.ToString() }); Console.Error.WriteLine("FAIL "+name+": "+error); } }
async Task Rejected(ChatRequest input, CompletionsRequestFailure expected, CompletionsTranscriptProjectionOptions? projection = null, CompletionsKeyAuthRequestOptions? options = null)
{
    using var handler = new CounterHandler(); using var client = new HttpClient(handler); var constructions=0; var payloads=0;
    var original=input.Messages.Select(entry=>entry.WireBody.ToString()).ToArray();
    var hooks=new CompletionsLifecycleHooks { OnPayload=(_,_,_)=>{payloads++;return ValueTask.FromResult<JsonData?>(null);} };
    var transport=CompletionsHttpSseTransport.FromAsyncRequestFactory(client,(r,t)=>{constructions++;return Factory(projection,options).CreateAsync(r,key,hooks,t);},new(){Hooks=hooks});
    using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));var result=await new ChatClient(transport).CompleteAsync(input,deadline.Token);
    Check(result.Failure is not null&&handler.Attempts==0&&constructions==1&&payloads==0,"Rejected additions reached callbacks/send.");
    Check(input.Messages.Select(entry=>entry.WireBody.ToString()).SequenceEqual(original),"Admission changed canonical input.");
    try { using var unused=Factory(projection,options).Create(input,key); } catch(CompletionsRequestException error) { Check(error.Failure==expected&&!error.Message.Contains("PRIVATE_",StringComparison.Ordinal),"Typed/safe failure changed: "+error.Failure);return; }
    throw new InvalidOperationException("Invalid additions admitted.");
}
await Run("later-tool-shape-zero-send",()=>Rejected(Request(System("{\"PRIVATE_MARKER\":true}")),CompletionsRequestFailure.UnsupportedContent));
await Run("later-null-tool-list-zero-send",()=>Rejected(Request(System("null")),CompletionsRequestFailure.UnsupportedContent));
await Run("later-tool-name-empty-zero-send",()=>Rejected(Request(System("["+inspect.Replace("\"inspect\"","\"\"")+"]")),CompletionsRequestFailure.UnsupportedContent));
await Run("later-tool-schema-shape-zero-send",()=>Rejected(Request(System("[{\"name\":\"inspect\",\"description\":\"PRIVATE_MARKER\",\"parameters\":[]}]")),CompletionsRequestFailure.UnsupportedContent));
await Run("later-null-removal-zero-send",()=>Rejected(Request(System("["+inspect+"]",",\"toolsRemoved\":null")),CompletionsRequestFailure.UnsupportedContent));
await Run("later-invalid-removal-zero-send",()=>Rejected(Request(System("["+inspect+"]",",\"toolsRemoved\":[{\"name\":\"\"}]")),CompletionsRequestFailure.UnsupportedContent));
await Run("invalid-earlier-declaration-still-validated-zero-send",()=>Rejected(request with { Messages=[System("[{\"name\":\"PRIVATE_MARKER\",\"description\":\"bad\",\"parameters\":[]}]"),request.Messages[1],System("["+inspect+"]",",\"toolsRemoved\":[{\"name\":\"PRIVATE_MARKER\"}]")] },CompletionsRequestFailure.UnsupportedContent));
await Run("duplicate-json-carrier-rejection-before-provider-zero-send",()=>{
    using var handler=new CounterHandler();
    try { _=JsonData.Parse("{\"role\":\"system\",\"content\":\"\",\"toolsAdded\":[],\"toolsAdded\":[],\"timestamp\":123}"); }
    catch(JsonException) { Check(handler.Attempts==0,"Rejected JSON entered a send.");return Task.CompletedTask; }
    throw new InvalidOperationException("Duplicate JSON escaped the canonical carrier.");
});
await Run("later-role-mismatch-zero-send",()=>Rejected(Request(new("system",JsonData.Parse("{\"role\":\"user\",\"content\":\"PRIVATE_MARKER\",\"timestamp\":123}"))),CompletionsRequestFailure.InvalidTranscript));
await Run("whole-history-declaration-budget-zero-send",()=>Rejected(request,CompletionsRequestFailure.ResourceLimit,Projection(new(MaximumDeclarations:1))));
await Run("whole-history-active-tools-budget-zero-send",()=>Rejected(request,CompletionsRequestFailure.ResourceLimit,Projection(new(MaximumActiveTools:1))));
await Run("whole-history-message-budget-zero-send",()=>Rejected(request,CompletionsRequestFailure.ResourceLimit,Projection(new(MaximumMessages:2))));
await Run("tool-bearing-message-output-count-budget-zero-send",()=>Rejected(request,CompletionsRequestFailure.ResourceLimit,Projection() with {MaximumOutputMessages=3}));
await Run("later-tool-depth-budget-zero-send",()=>Rejected(request,CompletionsRequestFailure.ResourceLimit,Projection() with {MaximumJsonDepth=4}));
await Run("require-strict-unsupported-addition-zero-send",()=>Rejected(Request(System("["+inspect[..^1]+",\"constrainedSampling\":{\"type\":\"json_schema\",\"strict\":\"require\"}}]")),CompletionsRequestFailure.UnsupportedContent));
await Run("model-addition-capability-invalid-shape-zero-send",()=>Rejected(request,CompletionsRequestFailure.InvalidConfiguration,options:new(){ModelMetadata=JsonData.Parse("{\"id\":\"anchored-controls\",\"api\":\"openai-completions\",\"provider\":\"openrouter\",\"baseUrl\":\"https://anchored-controls.invalid/v1\",\"reasoning\":false,\"input\":[\"text\"],\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0},\"compat\":{\"supportsMidConvoToolAdditions\":\"PRIVATE_MARKER\"}}")}));
// E93 retains the original grammar rejection; the new frozen provider/Agent probes qualify the supported composition.
await Run("grammar-enabled-anchored-composition-function-control",async()=>{
    var factory=Factory(options:new(){ModelMetadata=JsonData.Parse("{\"id\":\"anchored-controls\",\"api\":\"openai-completions\",\"provider\":\"openrouter\",\"baseUrl\":\"https://anchored-controls.invalid/v1\",\"reasoning\":false,\"input\":[\"text\"],\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0},\"compat\":{\"supportsMidConvoSystemMessages\":true,\"supportsMidConvoToolAdditions\":true,\"supportsOpenAIGrammarTools\":true}}")});
    using var owned=factory.Create(request,key);using var json=JsonDocument.Parse(await owned.Content!.ReadAsStringAsync());var value=json.RootElement;
    Check(factory.ResolvedWireOptions.SupportsOpenAIGrammarTools&&value.GetProperty("tools").GetArrayLength()==1&&value.GetProperty("messages")[2].GetProperty("tools")[0].GetProperty("type").GetString()=="function","Grammar capability changed anchored ordinary functions.");
});
await Run("final-expanded-anchored-payload-budget-zero-send",async()=>{
    using var message=Factory().Create(request,key);var raw=await message.Content!.ReadAsStringAsync();
    await Rejected(request,CompletionsRequestFailure.ResourceLimit,options:new(MaximumPayloadBytes:Encoding.UTF8.GetByteCount(raw)-1));
});
await Run("exact-output-boundary-positive",async()=>{
    var projected=new CompletionsTranscriptProjector(Projection()).Project(request);var length=Encoding.UTF8.GetByteCount(projected.ToString());
    var bounded=Projection() with {MaximumOutputBytes=length,MaximumOutputCharacters=projected.ToString().Length,MaximumOutputMessages=4};
    Check(new CompletionsTranscriptProjector(bounded).Project(request).ToString()==projected.ToString(),"Exact message/output quota rejected.");
    await Rejected(request,CompletionsRequestFailure.ResourceLimit,bounded with {MaximumOutputBytes=length-1});
});
await Run("reused-factory-concurrent-owned-additions",async()=>{
    var factory=Factory();var originals=request.Messages.Select(entry=>entry.WireBody.ToString()).ToArray();var ready=0;var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var bodies=await Task.WhenAll(Enumerable.Range(0,4).Select(_=>Task.Run(async()=>{if(Interlocked.Increment(ref ready)==4)release.TrySetResult();await release.Task.WaitAsync(TimeSpan.FromSeconds(5));using var owned=factory.Create(request,key);return await owned.Content!.ReadAsStringAsync();})));
    Check(bodies.All(body=>body==bodies[0])&&request.Messages.Select(entry=>entry.WireBody.ToString()).SequenceEqual(originals),"Concurrent factory changed declaration state.");
    using var json=JsonDocument.Parse(bodies[0]);var value=json.RootElement;
    Check(value.GetProperty("tools").GetArrayLength()==1&&value.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString()=="alpha","Initial request tools changed.");
    Check(value.GetProperty("messages")[2].GetProperty("role").GetString()=="system"&&value.GetProperty("messages")[2].GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString()=="inspect","Addition was not anchored.");
});
await Run("pre-cancel-zero-construction-and-send",async()=>{
    using var handler=new CounterHandler();using var client=new HttpClient(handler);using var canceled=new CancellationTokenSource();canceled.Cancel();var calls=0;
    var transport=new CompletionsHttpSseTransport(client,(input,token)=>{calls++;return Factory().Create(input,key,token);});
    var result=await new ChatClient(transport).CompleteAsync(request,canceled.Token);Check(result.Message.StopReason==StopReason.Aborted&&handler.Attempts==0&&calls==0,"Canceled additions were constructed/sent.");
});
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);await File.WriteAllTextAsync(args[1],JsonSerializer.Serialize(new{schemaVersion=1,passed,failed,observations=outcomes,productionAiDll=Identity(typeof(ChatClient).Assembly),executedTestDll=Identity(Assembly.GetExecutingAssembly()),fullGate=false,phaseAcceptance=false},new JsonSerializerOptions{WriteIndented=true})+"\n");
Console.WriteLine($"Completions anchored controls: {passed} passed, {failed} failed.");return failed==0?0:1;
static object Identity(Assembly assembly){var bytes=File.ReadAllBytes(assembly.Location);return new{path=assembly.Location,bytes=bytes.Length,sha256=Convert.ToHexStringLower(SHA256.HashData(bytes)),productVersion=assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion};}
sealed class CounterHandler:HttpMessageHandler
{internal int Attempts;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){Attempts++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("data: [DONE]\n\n")});}}
