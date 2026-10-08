using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Contracts;

if (args.Length is not (2 or 4) || args[0] != "--report" || args.Length == 4 && args[2] != "--thinking-fixture") throw new ArgumentException("Supply --report and optional --thinking-fixture.");
var model = new ModelDescriptor("anthropic/binding-controls", "openai-completions", "openrouter");
var request = new ChatRequest(model, [new("system", JsonData.Parse("{\"role\":\"system\",\"content\":\"Control instruction.\",\"timestamp\":123}")), new("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"Control question.\",\"timestamp\":123}"))], 123);
var baseModel = JsonNode.Parse("{\"type\":\"chat\",\"id\":\"anthropic/binding-controls\",\"api\":\"openai-completions\",\"provider\":\"openrouter\",\"baseUrl\":\"https://binding-controls.invalid/v1\",\"reasoning\":true,\"input\":[\"text\"],\"cost\":{\"input\":2.75,\"output\":4.25,\"cacheRead\":0.4,\"cacheWrite\":1.125},\"opaque\":{\"keep\":null}}")!.AsObject();
const string key = "authored-inert-key";
var outcomes = new List<object>(); var passed = 0; var failed = 0;
void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
JsonData Mutated(Action<JsonObject> mutation) { var node = baseModel.DeepClone().AsObject(); mutation(node); return JsonData.Parse(node.ToJsonString()); }
CompletionsKeyAuthRequestFactory Factory(CompletionsKeyAuthRequestOptions options) => new(new("https://binding-controls.invalid/v1/chat/completions"), model, options: options);
async Task Run(string name, Func<Task> action)
{
    try { await action(); passed++; outcomes.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; outcomes.Add(new { name, passed = false, error = error.ToString() }); Console.Error.WriteLine("FAIL " + name + ": " + error); }
}
async Task Rejected(CompletionsKeyAuthRequestOptions options, CompletionsRequestFailure expected)
{
    using var handler = new CounterHandler(); using var client = new HttpClient(handler); var factoryCalls = 0; var payloadCalls = 0;
    var originals = request.Messages.Select(entry => entry.WireBody.ToString()).ToArray();
    var hooks = new CompletionsLifecycleHooks { OnPayload = (_, _, _) => { payloadCalls++; return ValueTask.FromResult<JsonData?>(null); } };
    var transport = CompletionsHttpSseTransport.FromAsyncRequestFactory(client, (input, token) => { factoryCalls++; return Factory(options).CreateAsync(input, key, hooks, token); }, new() { Hooks = hooks });
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var result = await new ChatClient(transport).CompleteAsync(request, deadline.Token);
    Check(result.Failure is not null && handler.Attempts == 0 && factoryCalls == 1 && payloadCalls == 0 && request.Messages.Select(entry => entry.WireBody.ToString()).SequenceEqual(originals), "Rejected binding escaped admission or changed canonical input.");
    Check(!result.Message.ExtraProperties!.Values["errorMessage"].ToString().Contains("PRIVATE_", StringComparison.Ordinal), "Private metadata entered failure output.");
    try { using var unused = Factory(options).Create(request, key); }
    catch (CompletionsRequestException error) { Check(error.Failure == expected && !error.Message.Contains("PRIVATE_", StringComparison.Ordinal), "Typed/safe binding admission changed."); return; }
    throw new InvalidOperationException("Invalid or unsupported binding was admitted.");
}
async Task SourceThinkingBody(string id)
{
    var fixture = args.Length == 4 ? args[3] : null;
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); fixture is null && directory is not null; directory = directory.Parent)
    {
        var candidate = Path.Combine(directory.FullName, "fixtures/reference/completions-thinking.json");
        if (File.Exists(candidate)) fixture = candidate;
    }
    if (fixture is null) throw new InvalidOperationException("Supply the complete thinking Source fixture with --thinking-fixture.");
    var bytes = await File.ReadAllBytesAsync(fixture);
    Check(Convert.ToHexStringLower(SHA256.HashData(bytes)) == "e872e4581a019e438d96b06440e72d063f4f43f88b5926a1398f39342f312848", "Complete thinking Source authority changed.");
    using var document = JsonDocument.Parse(bytes);
    var source = document.RootElement.GetProperty("directObservations").EnumerateArray().Single(p=>p.GetProperty("id").GetString()==id);
    var profile = source.GetProperty("case"); var rawModel = JsonData.FromElement(profile.GetProperty("model")); var input = profile.GetProperty("options");
    var descriptor = new ModelDescriptor(rawModel.Value.GetProperty("id").GetString()!, "openai-completions", rawModel.Value.GetProperty("provider").GetString()!);
    var turn = source.GetProperty("turns")[0];
    var canonical = new ChatRequest(descriptor, [..turn.GetProperty("context").GetProperty("messages").EnumerateArray().Select(p=>new TranscriptEntry(p.GetProperty("role").GetString()!,JsonData.FromElement(p)))], 123);
    var before = canonical.Messages.Select(p=>p.WireBody.ToString()).ToArray(); var beforeModel = rawModel.ToString();
    var factory = new CompletionsKeyAuthRequestFactory(new(turn.GetProperty("requests")[0].GetProperty("url").GetString()!), descriptor,
        options:new(MaxTokens:input.GetProperty("maxTokens").GetDouble(),Temperature:input.GetProperty("temperature").GetDouble(),ReasoningEffort:input.GetProperty("reasoningEffort").GetString(),CacheRetention:CompletionsCacheRetention.None){ModelMetadata=rawModel});
    using var owned = factory.Create(canonical, "authored-inert-thinking-key");
    using var body = JsonDocument.Parse(await owned.Content!.ReadAsStringAsync());
    Check(JsonElement.DeepEquals(body.RootElement, turn.GetProperty("requests")[0].GetProperty("bodyJson")), "Whole genuine Source thinking body differs.");
    Check(canonical.Messages.Select(p=>p.WireBody.ToString()).SequenceEqual(before) && rawModel.ToString()==beforeModel, "Thinking projection changed canonical input or model metadata.");
}
foreach (var (name, mutate) in new (string, Action<JsonObject>)[] {
    ("identity-id", node => node["id"]="PRIVATE_DIFFERENT"), ("identity-api", node=>node["api"]="PRIVATE_API"),
    ("identity-provider", node=>node["provider"]="PRIVATE_PROVIDER"), ("identity-endpoint", node=>node["baseUrl"]="https://other.invalid/v1"),
    ("reasoning-shape", node=>node["reasoning"]="PRIVATE_VALUE"), ("input-shape", node=>node["input"]=new JsonArray("PRIVATE_MODALITY")),
    ("compat-shape", node=>node["compat"]=new JsonArray()), ("compat-bool", node=>node["compat"]=new JsonObject{["supportsStore"]="PRIVATE_VALUE"}),
    ("compat-max-field", node=>node["compat"]=new JsonObject{["maxTokensField"]="PRIVATE_FIELD"}),
    ("compat-affinity-format", node=>node["compat"]=new JsonObject{["sessionAffinityFormat"]="PRIVATE_FORMAT"}),
    ("cache-format", node=>node["compat"]=new JsonObject{["cacheControlFormat"]="PRIVATE_FORMAT"}),
    ("routing-shape", node=>node["compat"]=new JsonObject{["openRouterRouting"]=new JsonArray("PRIVATE_ROUTE")}),
    ("model-sampling-shape", node=>node["samplingParams"]=new JsonArray("PRIVATE_VALUE")),
    ("cost-negative", node=>node["cost"]!["input"]=-1), ("cost-shape", node=>node["cost"]!["output"]="PRIVATE_RATE"),
    ("headers-shape", node=>node["headers"]=new JsonArray("PRIVATE_HEADER")),
}) await Run("bound-invalid-"+name+"-zero-actual-send",()=>Rejected(new(){ModelMetadata=Mutated(mutate)},CompletionsRequestFailure.InvalidConfiguration));
// Historical unsupported-mode/budget outcomes remain in the accepted base evidence.
// These capabilities now have complete genuine Source body/stream fixtures.
await Run("bound-source-supported-thinking-budget-flag",()=>SourceThinkingBody("budget-default-high"));
// The original grammar rejection remains in the accepted2ba/e93 frozen evidence.
await Run("bound-supported-grammar-flag-without-declarations",async()=>
{
    var factory=Factory(new(){ModelMetadata=Mutated(node=>node["compat"]=new JsonObject{["supportsOpenAIGrammarTools"]=true})});
    using var owned=factory.Create(request,key);using var json=JsonDocument.Parse(await owned.Content!.ReadAsStringAsync());
    Check(factory.ResolvedWireOptions.SupportsOpenAIGrammarTools&&json.RootElement.GetProperty("messages").GetArrayLength()==2,"Grammar model binding changed the existing transcript.");
});
await Run("bound-source-supported-thinking-budget-field",()=>SourceThinkingBody("budget-field-thinking_budget-zai"));
// The original anchored-mode rejection is preserved in2ba's frozen evidence. This
// criterion is now Source-backed; the38-profile frozen probe exercises actual additions.
await Run("bound-supported-additions-flag-without-mid-system-falls-back",async()=>
{
    using var owned=Factory(new(){ModelMetadata=Mutated(node=>node["compat"]=new JsonObject{["supportsMidConvoToolAdditions"]=true})}).Create(request,key);
    using var json=JsonDocument.Parse(await owned.Content!.ReadAsStringAsync());
    Check(json.RootElement.GetProperty("messages").GetArrayLength()==2,"Inactive addition capability changed existing messages.");
});
await Run("bound-source-supported-qwen-thinking-mode",()=>SourceThinkingBody("qwen-requested-unmapped"));
foreach (var (name, mutate) in new (string, Action<JsonObject>)[] {
    ("budget-flag-shape", node=>node["compat"]=new JsonObject{["supportsThinkingTokenBudget"]="PRIVATE_BOOL"}),
    ("tool-stream-shape", node=>node["compat"]=new JsonObject{["zaiToolStream"]="PRIVATE_BOOL"}),
    ("model-token-ceiling-shape", node=>node["maxTokens"]="PRIVATE_CEILING"),
    ("template-object-shape", node=>node["compat"]=new JsonObject{["thinkingFormat"]="chat-template",["chatTemplateKwargs"]=new JsonArray()}),
    ("template-array-value", node=>node["compat"]=new JsonObject{["thinkingFormat"]="chat-template",["chatTemplateKwargs"]=new JsonObject{["field"]=new JsonArray()}}),
    ("template-missing-variable", node=>node["compat"]=new JsonObject{["thinkingFormat"]="chat-template",["chatTemplateKwargs"]=new JsonObject{["field"]=new JsonObject()}}),
    ("template-unknown-variable", node=>node["compat"]=new JsonObject{["thinkingFormat"]="baseten",["chatTemplateArgs"]=new JsonObject{["field"]=new JsonObject{["$var"]="PRIVATE_VARIABLE"}}}),
    ("template-omit-shape", node=>node["compat"]=new JsonObject{["thinkingFormat"]="chat-template",["chatTemplateKwargs"]=new JsonObject{["field"]=new JsonObject{["$var"]="thinking.enabled",["omitWhenOff"]="PRIVATE_BOOL"}}}),
}) await Run("bound-invalid-thinking-"+name+"-zero-actual-send",()=>Rejected(new(){ModelMetadata=Mutated(mutate)},CompletionsRequestFailure.InvalidConfiguration));
foreach (var (name, budget) in new[]{("array","[]"),("scalar","123"),("null-value","{\"high\":null}"),("string-value","{\"high\":\"PRIVATE_BUDGET\"}"),("nonfinite-value","{\"high\":1e400}"),("unknown-level","{\"PRIVATE_LEVEL\":1}")})
    await Run("bound-invalid-thinking-budget-"+name+"-zero-actual-send",()=>Rejected(new(){ModelMetadata=JsonData.Parse(baseModel.ToJsonString()),ThinkingBudgets=JsonData.Parse(budget)},CompletionsRequestFailure.InvalidConfiguration));
await Run("bound-duplicate-thinking-budget-rejected-at-json-contract-before-factory",()=>
{
    using var handler=new CounterHandler();var factoryCalls=0;var originals=request.Messages.Select(p=>p.WireBody.ToString()).ToArray();
    try {var budgets=JsonData.Parse("{\"high\":1,\"high\":2}");factoryCalls++;using var unused=Factory(new(){ThinkingBudgets=budgets}).Create(request,key);}
    catch(JsonException){Check(factoryCalls==0&&handler.Attempts==0&&request.Messages.Select(p=>p.WireBody.ToString()).SequenceEqual(originals),"Duplicate budget JSON crossed its strict value boundary.");return Task.CompletedTask;}
    throw new InvalidOperationException("Duplicate budget JSON was admitted.");
});
await Run("bound-unknown-thinking-mode-zero-actual-send",()=>Rejected(new(){ModelMetadata=Mutated(node=>node["compat"]=new JsonObject{["thinkingFormat"]="PRIVATE_FORMAT"})},CompletionsRequestFailure.UnsupportedContent));
await Run("bound-unknown-thinking-budget-field-zero-actual-send",()=>Rejected(new(){ModelMetadata=Mutated(node=>node["compat"]=new JsonObject{["thinkingTokenBudgetField"]="PRIVATE_FIELD"})},CompletionsRequestFailure.UnsupportedContent));
await Run("bound-unrepresentable-wire-rate-zero-actual-send",()=>Rejected(new(){ModelMetadata=Mutated(node=>node["cost"]!["input"]=JsonNode.Parse("1e-40"))},CompletionsRequestFailure.UnsupportedContent));
await Run("bound-metadata-byte-limit-zero-actual-send",()=>Rejected(new(MaximumPayloadBytes:1024){ModelMetadata=Mutated(node=>node["opaque"]=new string('é',900))},CompletionsRequestFailure.ResourceLimit));
await Run("bound-routing-depth-limit-zero-actual-send",()=>Rejected(new(MaximumPayloadDepth:4){ModelMetadata=Mutated(node=>node["compat"]=new JsonObject{["openRouterRouting"]=JsonNode.Parse("{\"opaque\":{\"deep\":{\"beyond\":1}}}")})},CompletionsRequestFailure.ResourceLimit));
await Run("bound-request-sampling-shape-zero-actual-send",()=>Rejected(new(){ModelMetadata=JsonData.Parse(baseModel.ToJsonString()),SamplingParams=JsonData.Parse("[]")},CompletionsRequestFailure.InvalidConfiguration));
await Run("bound-request-sampling-byte-limit-zero-actual-send",()=>Rejected(new(MaximumPayloadBytes:1024){ModelMetadata=JsonData.Parse(baseModel.ToJsonString()),SamplingParams=JsonData.Parse(JsonSerializer.Serialize(new{opaque=new string('é',900)}))},CompletionsRequestFailure.ResourceLimit));
await Run("bound-model-sampling-final-payload-limit-zero-actual-send",async()=>
{
    var raw=Mutated(node=>{node.Remove("type");node.Remove("opaque");node["samplingParams"]=new JsonObject{["opaque"]=new string('x',600)};});
    using var reference=Factory(new(){ModelMetadata=raw}).Create(request,key);var full=await reference.Content!.ReadAsStringAsync();
    var modelBytes=Encoding.UTF8.GetByteCount(raw.ToString());var payloadBytes=Encoding.UTF8.GetByteCount(full);Check(payloadBytes>modelBytes,"Final-payload quota control lacks expansion beyond admitted metadata.");
    var options=new CompletionsKeyAuthRequestOptions(MaximumPayloadBytes:(modelBytes+payloadBytes)/2){ModelMetadata=raw};
    _=Factory(options); // Positive constructor admission independently precedes the failing Create/send path.
    await Rejected(options,CompletionsRequestFailure.ResourceLimit);
});
await Run("bound-reused-factory-owned-metadata-sampling-and-wire-options",async()=>
{
    var node=baseModel.DeepClone().AsObject();node["samplingParams"]=new JsonObject{["top_p"]=0.8};node["compat"]=new JsonObject{["openRouterRouting"]=new JsonObject{["only"]=new JsonArray("original")}};
    var raw=JsonData.Parse(node.ToJsonString());var original=raw.ToString();var options=new CompletionsKeyAuthRequestOptions(SessionId:"owned-session"){ModelMetadata=raw,SamplingParams=JsonData.Parse("{\"top_p\":0.6}")};var factory=Factory(options);
    node["cost"]!["input"]=99;node["samplingParams"]!["top_p"]=0.1;node["compat"]!["openRouterRouting"]!["only"]=new JsonArray("changed");
    var wireClone=factory.ResolvedWireOptions with {MaximumChunks=1,Rates=new(99,99,99,99)};
    Check(factory.ResolvedWireOptions.MaximumChunks!=wireClone.MaximumChunks&&factory.ResolvedWireOptions.Rates!.Input==2.75m,"Wire clone rewrote bound rates or limits.");
    var ready=0;var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var bodies=await Task.WhenAll(Enumerable.Range(0,4).Select(_=>Task.Run(async()=>{if(Interlocked.Increment(ref ready)==4)release.TrySetResult();await release.Task.WaitAsync(TimeSpan.FromSeconds(5));using var owned=factory.Create(request,key);return await owned.Content!.ReadAsStringAsync();})));
    Check(bodies.All(body=>body==bodies[0])&&raw.ToString()==original,"Reusable bound factory borrowed mutable caller state.");using var body=JsonDocument.Parse(bodies[0]);
    Check(body.RootElement.GetProperty("top_p").GetDouble()==0.6&&body.RootElement.GetProperty("provider").GetProperty("only")[0].GetString()=="original","Sampling precedence or owned routing changed.");
});
await Run("bound-pre-canceled-run-zero-construction-and-send",async()=>
{
    using var handler=new CounterHandler();using var client=new HttpClient(handler);using var canceled=new CancellationTokenSource();canceled.Cancel();var calls=0;
    var transport=new CompletionsHttpSseTransport(client,(input,token)=>{calls++;return Factory(new(){ModelMetadata=JsonData.Parse(baseModel.ToJsonString())}).Create(input,key,token);});
    var result=await new ChatClient(transport).CompleteAsync(request,canceled.Token);Check(result.Message.StopReason==StopReason.Aborted&&handler.Attempts==0&&calls==0,"Pre-cancel created or sent a bound request.");
});
// Run the exact existing mapper groups, including both no-finish text/tool inference cases.
var compatibility=Assembly.Load("PiSharp.Compatibility.Tests");
var wireCases=(IEnumerable<(string Name,Func<Task> Run)>)compatibility.GetType("OpenAICompletionsWireTests",true)!.GetMethod("Cases",BindingFlags.Public|BindingFlags.Static)!.Invoke(null,null)!;
foreach(var test in wireCases)await Run(test.Name,test.Run);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
await File.WriteAllTextAsync(args[1],JsonSerializer.Serialize(new{schemaVersion=1,passed,failed,observations=outcomes,productionAiDll=Identity(typeof(ChatClient).Assembly),executedTestDll=Identity(Assembly.GetExecutingAssembly()),sameExistingWireGroupsExecuted=6,fullGate=false,phaseAcceptance=false},new JsonSerializerOptions{WriteIndented=true})+"\n");
Console.WriteLine($"OpenRouter binding controls: {passed} passed, {failed} failed.");return failed==0?0:1;
static object Identity(Assembly assembly){var bytes=File.ReadAllBytes(assembly.Location);return new{path=assembly.Location,bytes=bytes.Length,sha256=Convert.ToHexStringLower(SHA256.HashData(bytes)),productVersion=assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion};}
sealed class CounterHandler:HttpMessageHandler
{
    internal int Attempts;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){Attempts++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("data: [DONE]\n\n")});}
}
