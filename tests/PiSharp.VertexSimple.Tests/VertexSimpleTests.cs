using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.GoogleGenerativeAI;
using PiSharp.AI.Protocols.GoogleVertex;
using PiSharp.Contracts;
internal static class VertexSimpleTests
{
    internal static readonly List<(string Name,Task Original,Exception? Direct)> OriginalTasks=[];
    private static readonly Uri Endpoint=new("https://us-central1-aiplatform.googleapis.com/v1/projects/synthetic/locations/us-central1/publishers/google/models/gemini-2.5-flash:streamGenerateContent?alt=sse");
    private static ModelDescriptor Model(string id="gemini-2.5-flash")=>new(id,"google-vertex","google-vertex");
    private static JsonData Metadata(string id="gemini-2.5-flash",double window=10000,double maximum=8192)=>JsonData.Parse(JsonSerializer.Serialize(new
    {id,api="google-vertex",provider="google-vertex",reasoning=true,input=new[]{"text"},contextWindow=window,maxTokens=maximum,cost=new{input=1,output=2,cacheRead=0.5,cacheWrite=0}}));
    private static ChatRequest Request(ModelDescriptor model)=>new(model,[new TranscriptEntry("user",JsonData.Parse("{\"role\":\"user\",\"timestamp\":1,\"content\":[{\"type\":\"text\",\"text\":\"test\"}]}"))]);
    private static GoogleVertexSimpleOptions Options(string? reasoning=null,string id="gemini-2.5-flash")=>new(new(Endpoint,"SYNTHETIC_TOKEN",new(Metadata(id))),"synthetic","us-central1",reasoning);
    private static void Check(bool condition){if(!condition)throw new InvalidOperationException("Vertex Simple assertion.");}
    private sealed class Handler(bool held=false):HttpMessageHandler
    {
        internal readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<(Uri? Address,string? Authorization,JsonData Body)> Captures=[];
        internal Task<HttpResponseMessage>? Original;internal bool Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Original=Send();return Original;
            async Task<HttpResponseMessage> Send()
            {
                Captures.Add((request.RequestUri,request.Headers.Authorization?.ToString(),JsonData.Parse(await request.Content!.ReadAsStringAsync())));
                Entered.TrySetResult();if(held)await Release.Task;
                return new(HttpStatusCode.OK){Content=new StringContent("data: {\"responseId\":\"synthetic\",\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"vertex answer\"}]},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":1,\"candidatesTokenCount\":1,\"totalTokenCount\":2}}\n\n",Encoding.UTF8,"text/event-stream")};
            }
        }
        protected override void Dispose(bool disposing){Disposed=true;base.Dispose(disposing);}
    }
    private static async Task<StreamTerminalEvent> Drain(IChatTransport transport,ChatRequest request,CancellationToken token=default)
    {StreamTerminalEvent? terminal=null;await foreach(var frame in transport.StreamAsync(request,token))if(frame is StreamTerminalEvent end)terminal=end;return terminal??throw new InvalidOperationException("Terminal missing.");}
    // Pi 1.1.0 estimate.ts: "test" is ceil(4 / 3.5) = 2 tokens, so 10000 - 2 - 4096 = 5902 output tokens (5903 at four).
    internal static Task Resolution()
    {
        using var handler=new Handler();using var client=new HttpClient(handler,false);
        foreach(var (id,level,budget) in new[]{("gemini-2.5-pro","high",32768d),("gemini-2.5-flash-lite","minimal",128d),("other","low",-1d)})
        {
            var model=Model(id);var transport=new GoogleVertexSimpleTransport(client,model,Options(level,id));var resolution=transport.Resolve(Request(model));
            Check(resolution.MaxTokens==5902&&resolution.Thinking.Enabled&&resolution.Thinking.BudgetTokens==budget);
        }
        var modern=Model("gemini-3-flash");var modernTransport=new GoogleVertexSimpleTransport(client,modern,Options("medium",modern.Id));
        Check(modernTransport.Resolve(Request(modern)).Thinking.Level=="MEDIUM");
        var off=new GoogleVertexSimpleTransport(client,Model(),Options("off"));Check(!off.Resolve(Request(Model())).Thinking.Enabled);
        var custom=Options("minimal")with{ThinkingBudgets=JsonData.Parse("{\"minimal\":777}")};Check(new GoogleVertexSimpleTransport(client,Model(),custom).Resolve(Request(Model())).Thinking.BudgetTokens==777);
        var bounded=Options()with{MaximumContextMessages=1};var request=Request(Model())with{Messages=[..Request(Model()).Messages,..Request(Model()).Messages]};
        try{new GoogleVertexSimpleTransport(client,Model(),bounded).Resolve(request);throw new InvalidOperationException("Context cap ignored.");}catch(GoogleGenerativeAIException){}
        Check(handler.Captures.Count==0);return Task.CompletedTask;
    }
    internal static async Task Wire()
    {
        using var handler=new Handler();using var client=new HttpClient(handler,false);var model=Model();var options=Options("minimal");
        var transport=new GoogleVertexSimpleTransport(client,model,options);var result=await Drain(transport,Request(model));
        var capture=handler.Captures.Single();Check(capture.Address==Endpoint&&capture.Authorization=="Bearer SYNTHETIC_TOKEN");
        Check(result.Reason==StopReason.Stop&&result.Message.Api=="google-vertex"&&result.Message.Provider=="google-vertex");
        var generation=capture.Body.Value.GetProperty("generationConfig");Check(generation.GetProperty("maxOutputTokens").GetInt32()==5902&&generation.GetProperty("thinkingConfig").GetProperty("thinkingBudget").GetInt32()==128);
        Check(!handler.Disposed);
    }
    internal static async Task Admission()
    {
        using var handler=new Handler();using var client=new HttpClient(handler,false);var effects=0;
        var hooks=new GoogleGenerativeAIHooks{OnPayload=(_,_,_)=>{effects++;return ValueTask.FromResult<JsonData?>(null);}};
        var options=Options()with{DirectOptions=Options().DirectOptions with{Projection=Options().DirectOptions.Projection with{Hooks=hooks}}};
        foreach(var invalid in new[]{options with{Project=""},options with{Location=""},options with{DirectOptions=options.DirectOptions with{AccessToken=""}}})
        {try{_ = new GoogleVertexSimpleTransport(client,Model(),invalid);throw new InvalidOperationException("Invalid route accepted.");}catch(GoogleGenerativeAIException){}}
        using var cancel=new CancellationTokenSource();cancel.Cancel();var transport=new GoogleVertexSimpleTransport(client,Model(),options);
        try{await Drain(transport,Request(Model()),cancel.Token);throw new InvalidOperationException("Canceled Simple accepted.");}catch(OperationCanceledException){}
        Check(effects==0&&handler.Captures.Count==0);
    }
    internal static async Task Held(bool payload)
    {
        using var handler=new Handler(!payload);using var client=new HttpClient(handler,false);using var cancel=new CancellationTokenSource();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var callback=new TaskCompletionSource<JsonData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options=Options();if(payload)options=options with{DirectOptions=options.DirectOptions with{Projection=options.DirectOptions.Projection with{Hooks=new(){OnPayload=(_,_,_)=>{entered.TrySetResult();return new(callback.Task);}}}}};
        var drain=Drain(new GoogleVertexSimpleTransport(client,Model(),options),Request(Model()),cancel.Token);Exception? control=null;Exception? direct=null;Exception? originalError=null;StreamTerminalEvent? terminal=null;
        try{await (payload?entered.Task:handler.Entered.Task).WaitAsync(TimeSpan.FromSeconds(10));cancel.Cancel();Check(!drain.IsCompleted&&(payload?!callback.Task.IsCompleted:handler.Original is {IsCompleted:false}));}
        catch(Exception error){control=error;}
        finally
        {
            callback.TrySetResult(null);handler.Release.TrySetResult();try{terminal=await drain;}catch(Exception error){direct=error;}
            Task? original=payload?callback.Task:handler.Original;if(original is not null){try{await original;}catch(Exception error){originalError=error;}OriginalTasks.Add((payload?"payload-original":"http-send-original",original,originalError));}
            OriginalTasks.Add((payload?"payload-drain":"http-drain",drain,direct));
        }
        if(control is not null)throw control;Check(direct is null&&terminal is {Reason:StopReason.Aborted}&&(payload?handler.Captures.Count==0:handler.Captures.Count==1)&&!handler.Disposed);
    }
}
