using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Providers;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Compaction;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;
using PiSharp.Rpc;
using PiSharp.Rpc.Protocol;

internal static class SessionRecoveryOriginalCriteriaTests
{
    private static readonly ModelDescriptor Model=new("recovery-summary-model","openai-completions","fixture");
    public static (string Name,Func<Task> Run)[] Cases()=>
    [
        ("session-recovery-original actual summary wire cancellation joins physical cleanup then throws caller cancellation",TransportCancellation),
        ("session-recovery-original actual summary error without cancellation stays failed after physical cleanup",TransportFailure),
        ("session-recovery-original actual summary tool call cannot acquire execution or checkpoint authority",TransportTool),
        ("session-recovery-original actual aborted summary keeps durable omission billing and ordered one final settlement",RecoveryCancellation),
        ("session-recovery-original actual cancellation cleanup failure remains failed rather than hidden by cancellation",TransportCancellationCleanup),
        ("session-recovery-original native diagnostic retry records share accepted public generation and survive omission reopen",DiagnosticRetryCorrelation)
    ];

    private static SessionSummaryRequest Request()=>new(SessionSummaryKind.History,Model,"owned system","owned conversation",128,null,"summary-session");
    private static async Task TransportCancellation()
    {
        var wire=new Wire(cancel:true);using var cancel=new CancellationTokenSource();
        var generator=new TransportSessionSummaryGenerator(_=>wire.Transport,()=>123);var run=generator.GenerateAsync(Request(),cancel.Token).AsTask();
        try
        {
            await Within(wire.Entered.Task);cancel.Cancel();await Within(wire.CleanupEntered.Task);
            Check(!run.IsCompleted,"Actual summary adapter returned before its owned physical cleanup.");Equal(0,wire.Cleanups);
            wire.Release.TrySetResult();var error=await ThrowsAsync<OperationCanceledException>(()=>run);
            Equal(cancel.Token,error.CancellationToken);Equal(1,wire.Cleanups);Equal(1,wire.Requests);
        }
        finally{wire.Release.TrySetResult();await Ignore(run);}
    }
    private static async Task TransportFailure()
    {
        var wire=new Wire(failure:true);var generator=new TransportSessionSummaryGenerator(_=>wire.Transport,()=>123);
        var error=await ThrowsAsync<SessionCompactionException>(()=>generator.GenerateAsync(Request()).AsTask());
        Equal(SessionCompactionFailure.SummaryFailed,error.Failure);Equal(1,wire.Cleanups);Equal(1,wire.Requests);
    }
    private static async Task TransportCancellationCleanup()
    {
        var wire=new Wire(cancel:true,failCleanup:true);using var cancel=new CancellationTokenSource();
        var generator=new TransportSessionSummaryGenerator(_=>wire.Transport,()=>123);var run=generator.GenerateAsync(Request(),cancel.Token).AsTask();
        try
        {
            await Within(wire.Entered.Task);cancel.Cancel();await Within(wire.CleanupEntered.Task);
            Check(!run.IsCompleted,"Actual failed cancellation cleanup detached from summary completion.");
            wire.Release.TrySetResult();var error=await ThrowsAsync<SessionCompactionException>(()=>run);
            Equal(SessionCompactionFailure.SummaryFailed,error.Failure);Equal(1,wire.Cleanups);Equal(1,wire.Requests);
            Check(!error.ToString().Contains("private cancellation cleanup marker",StringComparison.Ordinal),"Summary failure exposed private physical cleanup text.");
        }
        finally{wire.Release.TrySetResult();await Ignore(run);}
    }

    private static async Task DiagnosticRetryCorrelation()
    {
        foreach(var retryFails in new[]{false,true})
        {
            using var files=new Files();var ids=0;var timestamp=1_000_000L;var codec=new SessionEntryCodec();
            var header=codec.Parse(JsonSerializer.Serialize(new{type="session",version=3,id="diagnostic-recovery",timestamp="1970-01-01T00:00:00.000Z",cwd=files.Directory}));
            var provider=new DiagnosticAttempts(retryFails);await using var session=await PersistentAgentSession.CreateAsync(files.Path,header,new(Model,provider,[]),()=>Interlocked.Increment(ref timestamp),()=>"correlation-"+Interlocked.Increment(ref ids));
            TranscriptEntry Input(string text)=>new("user",JsonData.Parse(JsonSerializer.Serialize(new{role="user",content=text,timestamp=10})));
            await Within(session.PromptAsync(Input(new string('x',8000))));await Within(session.PromptAsync(Input(new string('y',8000))));
            var wire=new Wire();session.ConfigureAutomaticCompaction(new TransportSessionSummaryGenerator(_=>wire.Transport,()=>123),new(true,128,80),contextWindow:100_000);session.ConfigureAutomaticRecovery(1000);
            var finals=new List<SessionOperationSettled>();using var lease=session.SubscribeOperationEvents(new OperationSink((e,_)=>{if(e is SessionOperationSettled settled)finals.Add(settled);return ValueTask.CompletedTask;}));
            using var output=new MemoryStream();var model=JsonData.Parse(JsonSerializer.Serialize(new{id=Model.Id,api=Model.Api,provider=Model.Provider,name="Authored diagnostic recovery",baseUrl="https://offline.invalid",reasoning=false,input=new[]{"text"},contextWindow=100_000,maxTokens=1000,cost=new{input=0,output=0,cacheRead=0,cacheWrite=0}}));
            await using var dispatcher=new RpcSessionDispatcher(session,new JsonlWriter(output),()=>Interlocked.Increment(ref timestamp),[new(Model,model)],sessionOwnership:RpcSessionOwnership.Borrowed);
            await Within(dispatcher.SubmitAsync(JsonData.Parse("{\"type\":\"prompt\",\"id\":\"diagnostic-retry\",\"message\":\"retry with typed native diagnostics\"}")));await Within(dispatcher.WaitForIdleAsync());
            Equal(3L,session.Snapshot.OperationGeneration);Equal(4L,session.Snapshot.Agent.Generation);Equal(4,provider.Requests.Count);Equal(4,provider.Cleanups);
            Equal(1,wire.Requests);Equal(1,wire.Cleanups);Equal(1,finals.Count);Equal(3L,finals.Single().OperationGeneration);
            var native=session.GetNativeDiagnostics();Equal(retryFails?2:1,native.Entries.Length);Equal(0,native.Issues.Length);
            Check(native.Entries.All(row=>row.OperationGeneration==session.Snapshot.OperationGeneration),"A retry diagnostic acquired a low-level Agent generation instead of the accepted operation.");
            Equal(NativeChatFailureCode.ProviderError,native.Entries[0].Diagnostic!.Code);
            if(retryFails)Equal(NativeChatFailureCode.UnexpectedEof,native.Entries[1].Diagnostic!.Code);
            var records=System.Text.Encoding.UTF8.GetString(output.ToArray()).Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(JsonData.Parse).ToArray();
            string Type(JsonData record)=>record.Value.GetProperty("type").GetString()!;
            var notifications=records.Where(record=>Type(record)=="pisharp_chat_diagnostic").ToArray();Equal(native.Entries.Length,notifications.Length);
            for(var i=0;i<notifications.Length;i++)
            {
                var body=notifications[i].Value.GetProperty("data");Equal(3L,body.GetProperty("operationGeneration").GetInt64());
                Equal(native.Entries[i].AssistantEntryId,body.GetProperty("assistantEntryId").GetString());Equal(native.Entries[i].RecordEntryId,body.GetProperty("recordEntryId").GetString());
                Equal(native.Entries[i].Diagnostic!.Code.ToString(),body.GetProperty("diagnostic").GetProperty("code").GetString());
            }
            Equal(2,records.Count(record=>Type(record)=="agent_end"));Equal(1,records.Count(record=>Type(record)=="agent_settled"));Equal(1,records.Count(record=>Type(record)=="pisharp_operation_settled"));
            Check(Array.FindLastIndex(records,record=>Type(record)=="pisharp_chat_diagnostic")<Array.FindIndex(records,record=>Type(record)=="pisharp_operation_settled"),"Final SDK settlement preceded its acknowledged retry diagnostic.");
            Check(!dispatcher.Completion.IsCompleted,"A typed retry diagnostic made the RPC route unusable.");
            await Within(dispatcher.SubmitAsync(JsonData.Parse("{\"type\":\"get_state\",\"id\":\"diagnostic-state\"}")));
            var raw=session.Snapshot.Context.Ancestry;
            foreach(var row in native.Entries)
            {
                var assistant=raw.Single(entry=>entry.Id==row.AssistantEntryId);var record=raw.Single(entry=>entry.Id==row.RecordEntryId);
                Equal(SessionEntryKind.Message,assistant.Kind);Equal(SessionEntryKind.Custom,record.Kind);
                Check(raw.IndexOf(assistant)<raw.IndexOf(record),"Native record preceded its acknowledged assistant.");
            }
            Equal(1,session.Snapshot.Log.Entries.Count(entry=>entry.Kind==SessionEntryKind.ContextEdit));Equal(1,session.Snapshot.Log.Entries.Count(entry=>entry.Kind==SessionEntryKind.Compaction));
            Check(!session.Snapshot.Context.ContextEntries.Any(entry=>entry.SourceEntry.Id==native.Entries[0].AssistantEntryId&&!entry.Messages.IsEmpty),"First rejected assistant survived canonical recovery omission.");
            Equal(404d,new SessionHistoryProjector().Project(session.Snapshot.Log.Entries,session.Snapshot.Context.LeafId).BranchStatistics.Totals!.Total);
            Check(session.Snapshot.Context.LlmMessages.All(message=>!message.WireBody.Value.TryGetProperty("openAICompletionsFailure",out _)),"Native diagnostics leaked into executable source input.");
            var expected=NativeSessionDiagnosticProjector.ToWire(native).ToString();await dispatcher.DisposeAsync();await session.DisposeAsync();
            await using var reopened=await PersistentAgentSession.OpenAsync(files.Path,new(Model,new DiagnosticAttempts(false),[]),()=>123,()=>"unused");
            Equal(expected,NativeSessionDiagnosticProjector.ToWire(reopened.GetNativeDiagnostics()).ToString());
            Equal(404d,new SessionHistoryProjector().Project(reopened.Snapshot.Log.Entries,reopened.Snapshot.Context.LeafId).BranchStatistics.Totals!.Total);
        }
    }
    private static async Task TransportTool()
    {
        var wire=new Wire(tool:true);var generator=new TransportSessionSummaryGenerator(_=>wire.Transport,()=>123);
        var error=await ThrowsAsync<SessionCompactionException>(()=>generator.GenerateAsync(Request()).AsTask());
        Equal(SessionCompactionFailure.SummaryFailed,error.Failure);Equal(1,wire.Cleanups);Equal(1,wire.Requests);
        Equal(2,wire.Chunks); // Actual tool-call completion is data; the summary generator owns no executor.
    }

    private static async Task RecoveryCancellation()
    {
        using var files=new Files();var ids=0;var timestamp=1_000_000L;var codec=new SessionEntryCodec();
        var header=codec.Parse(JsonSerializer.Serialize(new{type="session",version=3,id="original-recovery",timestamp="1970-01-01T00:00:00.000Z",cwd=files.Directory}));
        SessionEntry Message(string id,string? parent,TranscriptEntry message)=>codec.Parse(JsonSerializer.Serialize(new{type="message",id,parentId=parent,timestamp="1970-01-01T00:00:00.010Z",message=message.WireBody.Value}));
        TranscriptEntry Input(string text)=>new("user",JsonData.Parse(JsonSerializer.Serialize(new{role="user",content=text,timestamp=10})));
        AssistantMessage Answer(StopReason reason=StopReason.Stop)=>new(Model.Api,Model.Provider,Model.Id,10,[new TextContent("owned prior answer")],new(100,1,0,0,101,new(.1m,.001m,0,0,.101m)),reason);
        await using(var store=await SessionLogStore.CreateNewAsync(files.Path,header))
        {
            await store.AppendAsync([Message("u1",null,Input(new string('x',8000))),Message("a1","u1",new("assistant",PiWireJson.WriteMessage(Answer()))),
                Message("u2","a1",Input(new string('y',8000))),Message("a2","u2",new("assistant",PiWireJson.WriteMessage(Answer())))]);
        }
        var failed=Answer(StopReason.Error) with{ExtraProperties=JsonFields.Empty.Set("errorMessage",JsonData.Parse("\"input exceeds the context window\""))};
        var provider=new Attempt(failed);await using var session=await PersistentAgentSession.OpenAsync(files.Path,new(Model,provider,[]),()=>Interlocked.Increment(ref timestamp),()=>"original-"+Interlocked.Increment(ref ids));
        var wire=new Wire(cancel:true);session.ConfigureAutomaticCompaction(new TransportSessionSummaryGenerator(_=>wire.Transport,()=>Interlocked.Increment(ref timestamp)),new(true,128,80),contextWindow:100_000);session.ConfigureAutomaticRecovery(1000);
        var events=new List<string>();var operationEvents=new List<SessionOperationEvent>();
        using var low=session.Subscribe(new AgentSink((e,_)=>
        {
            if(e is AgentLoopEnded){Equal(0,session.Snapshot.Log.Entries.Count(x=>x.Kind==SessionEntryKind.ContextEdit));events.Add("agent_end");}
            return ValueTask.CompletedTask;
        }));
        using var native=session.SubscribeOperationEvents(new OperationSink((e,_)=>
        {
            operationEvents.Add(e);
            if(e is SessionRecoveryStarted){Equal(0,session.Snapshot.Log.Entries.Count(x=>x.Kind==SessionEntryKind.ContextEdit));events.Add("recovery_start");}
            if(e is SessionRecoveryEnded ended){Equal(1,wire.Cleanups);events.Add("recovery_end:"+ended.Status);}
            if(e is SessionOperationSettled settled)events.Add("settled:"+settled.Status);
            return ValueTask.CompletedTask;
        }));
        var run=session.PromptAsync(Input("real summary cancellation"));
        try
        {
            await Within(wire.Entered.Task);Equal(1,session.Snapshot.Log.Entries.Count(x=>x.Kind==SessionEntryKind.ContextEdit));
            Check(!session.Snapshot.Context.LlmMessages.Any(m=>m.Role=="assistant"&&PiWireJson.ReadMessage(m.WireBody.Value).StopReason==StopReason.Error),"Failed attempt remained executable during actual summary generation.");
            Equal(303d,new SessionHistoryProjector().Project(session.Snapshot.Log.Entries,session.Snapshot.Context.LeafId).BranchStatistics.Totals!.Total);
            Equal(1,provider.Requests);Check(session.Snapshot.IsProcessingOperation&&session.Snapshot.OperationPhase==SessionOperationPhase.Compaction,"Actual summary lost operation ownership.");
            Check(session.Abort(),"Actual summary abort authority was absent.");await Within(wire.CleanupEntered.Task);
            Check(!run.IsCompleted&&!session.WaitForIdleAsync().IsCompleted,"Recovery final settlement escaped held actual source cleanup.");
            wire.Release.TrySetResult();await Within(run);Equal(1,wire.Requests);Equal(1,wire.Cleanups);Equal(1,provider.Requests);Equal(1,provider.Cleanups);
            Equal(0,session.Snapshot.Log.Entries.Count(x=>x.Kind==SessionEntryKind.Compaction));Equal(1,session.Snapshot.Log.Entries.Count(x=>x.Kind==SessionEntryKind.ContextEdit));
            Equal("cancelled",session.Snapshot.LastAutomaticCompaction!.Disposition);Equal("cancelled",operationEvents.OfType<SessionRecoveryEnded>().Single().Status);
            Equal("cancelled",operationEvents.OfType<SessionOperationSettled>().Single().Status);Equal(1,operationEvents.OfType<SessionOperationSettled>().Count());
            Check(events.SequenceEqual(new[]{"agent_end","recovery_start","recovery_end:cancelled","settled:cancelled"}),"Actual transport cancellation changed durable recovery/final event order.");
            Check(session.Snapshot.Fault is null&&!session.Snapshot.IsProcessingOperation,"Joined cancelled summary retained a poisoned or busy writer.");
            var expected=session.Snapshot.Context.LlmMessages.Select(m=>m.WireBody.ToString()).ToArray();var leaf=session.Snapshot.Context.LeafId;
            await session.DisposeAsync();await using var reopened=await PersistentAgentSession.OpenAsync(files.Path,new(Model,new Attempt(failed),[]),()=>123,()=>"unused");
            Equal(leaf,reopened.Snapshot.Context.LeafId);Check(expected.SequenceEqual(reopened.Snapshot.Context.LlmMessages.Select(m=>m.WireBody.ToString())),"Cancelled-summary reopen lost the canonical omission.");
            Equal(303d,new SessionHistoryProjector().Project(reopened.Snapshot.Log.Entries,reopened.Snapshot.Context.LeafId).BranchStatistics.Totals!.Total);
        }
        finally{wire.Release.TrySetResult();session.Abort();await Ignore(run);}
    }

    private sealed class Wire(bool cancel=false,bool failure=false,bool tool=false,bool failCleanup=false)
    {
        private bool FailCleanup=>failCleanup;
        public int Requests,Cleanups,Chunks;public TaskCompletionSource Entered=Gate(),CleanupEntered=Gate(),Release=Gate();
        public IChatTransport Transport=>new OpenAICompletionsWireSource((_,token)=>cancel?new CancellationChunks(this,token):Read(token));
        private sealed class CancellationChunks(Wire owner,CancellationToken factoryToken):IAsyncEnumerable<JsonData>
        {
            public IAsyncEnumerator<JsonData> GetAsyncEnumerator(CancellationToken token=default)
            {Equal(factoryToken,token);owner.Requests++;return new Enumerator(owner,token);}
            private sealed class Enumerator(Wire owner,CancellationToken token):IAsyncEnumerator<JsonData>
            {
                private int moved,disposed;
                public JsonData Current{get;}=JsonData.Parse("{\"choices\":[{\"delta\":{\"content\":\"owned summary\"},\"finish_reason\":null}]}");
                public async ValueTask<bool> MoveNextAsync()
                {
                    token.ThrowIfCancellationRequested();if(Interlocked.Increment(ref moved)==1){owner.Chunks++;return true;}
                    owner.Entered.TrySetResult();await Task.Delay(Timeout.Infinite,token);throw new InvalidOperationException("Unreachable pending read.");
                }
                public async ValueTask DisposeAsync()
                {
                    if(Interlocked.Exchange(ref disposed,1)!=0)throw new InvalidOperationException("Source disposed more than once.");
                    owner.CleanupEntered.TrySetResult();await owner.Release.Task;owner.Cleanups++;
                    if(owner.FailCleanup)throw new IOException("private cancellation cleanup marker");
                }
            }
        }
        private async IAsyncEnumerable<JsonData> Read([EnumeratorCancellation]CancellationToken token)
        {
            Requests++;
            try
            {
                token.ThrowIfCancellationRequested();Chunks++;
                yield return JsonData.Parse(failure?"{\"choices\":\"invalid owned shape\"}":tool?
                    "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"summary-call\",\"function\":{\"name\":\"never-execute\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}":
                    "{\"choices\":[{\"delta\":{\"content\":\"owned summary\"},\"finish_reason\":null}]}");
                if(cancel){Entered.TrySetResult();await Task.Delay(Timeout.Infinite,token);}
                else if(!failure){Chunks++;yield return JsonData.Parse("{\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}");}
            }
            finally{CleanupEntered.TrySetResult();if(cancel)await Release.Task;Cleanups++;if(failCleanup)throw new IOException("private cancellation cleanup marker");}
        }
    }
    private sealed class DiagnosticAttempts(bool retryFails):IChatTransport
    {
        public List<ChatRequest> Requests=[];public int Cleanups;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request,[EnumeratorCancellation]CancellationToken token=default)
        {
            var index=Requests.Count;Requests.Add(request);var failed=index==2||index==3&&retryFails;
            var final=new AssistantMessage(Model.Api,Model.Provider,Model.Id,request.Timestamp,[new TextContent("owned typed attempt "+index)],
                new(100,1,0,0,101,new(.1m,.001m,0,0,.101m)),failed?StopReason.Error:StopReason.Stop);
            if(failed)final=final with{ExtraProperties=JsonFields.Empty.Set("errorMessage",JsonData.Parse("\"input exceeds the context window\""))};
            try
            {
                token.ThrowIfCancellationRequested();yield return new StreamStarted(final with{Content=[],StopReason=StopReason.Pending});
                yield return new TextStarted(0,new(""));yield return new TextEnded(0,"owned typed attempt "+index);
                if(failed)yield return new StreamError(StopReason.Error,final){NativeDiagnostic=new(NativeChatAdapter.OpenAICompletions,index==2?NativeChatFailureCode.ProviderError:NativeChatFailureCode.UnexpectedEof)};
                else yield return new StreamDone(StopReason.Stop,final);await Task.Yield();
            }
            finally{Cleanups++;}
        }
    }
    private sealed class Attempt(AssistantMessage value):IChatTransport
    {
        public int Requests,Cleanups;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request,[EnumeratorCancellation]CancellationToken token=default)
        {
            Requests++;var final=value with{Timestamp=request.Timestamp};
            try{token.ThrowIfCancellationRequested();yield return new StreamStarted(final with{Content=[],StopReason=StopReason.Pending});yield return new TextStarted(0,new(""));yield return new TextEnded(0,"owned prior answer");yield return new StreamError(StopReason.Error,final);await Task.Yield();}
            finally{Cleanups++;}
        }
    }
    private sealed class AgentSink(Func<AgentEvent,CancellationToken,ValueTask> callback):IAgentEventSink{public ValueTask EmitAsync(AgentEvent e,CancellationToken t)=>callback(e,t);}
    private sealed class OperationSink(Func<SessionOperationEvent,CancellationToken,ValueTask> callback):ISessionOperationEventSink{public ValueTask EmitAsync(SessionOperationEvent e,CancellationToken t)=>callback(e,t);}
    private sealed class Files:IDisposable
    {
        public string Directory{get;}=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"PiSharp-original-recovery-"+Guid.NewGuid().ToString("N"));public string Path=>System.IO.Path.Combine(Directory,"session.jsonl");
        public Files()=>System.IO.Directory.CreateDirectory(Directory);
        public void Dispose(){var target=System.IO.Path.GetFullPath(Directory);if(System.IO.Path.GetDirectoryName(target)!=System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()))||!System.IO.Path.GetFileName(target).StartsWith("PiSharp-original-recovery-",StringComparison.Ordinal))throw new InvalidOperationException("Invalid owned cleanup path.");System.IO.Directory.Delete(target,true);}
    }
    private static TaskCompletionSource Gate()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Within(Task task)=>await task.WaitAsync(TimeSpan.FromSeconds(10));
    private static async Task Ignore(Task task){try{await Within(task);}catch(Exception){}}
    private static async Task<T> ThrowsAsync<T>(Func<Task> action)where T:Exception{try{await Within(action());}catch(T error){return error;}throw new InvalidOperationException("Expected "+typeof(T).Name);}
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static void Equal<T>(T expected,T actual)=>Check(EqualityComparer<T>.Default.Equals(expected,actual),$"Expected {expected}; actual {actual}.");
}
