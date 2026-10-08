using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Providers;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class SessionNativeDiagnosticIntegrationTests
{
    private static readonly ModelDescriptor Model=new("diagnostic-model","openai-completions","fixture");
    public static (string Name,Func<Task> Run)[] Cases()=>
    [
        ("session-native-diagnostic actual wire failure custom checkpoint precedes observers idle and reopen",Acknowledged),
        ("session-native-diagnostic uncertain custom checkpoint faults writer before successful turn delivery",Uncertain),
        ("session-native-diagnostic cancellation during accepted checkpoint joins acknowledgment without poisoning",CancelCheckpoint),
        ("session-native-diagnostic real semantic and cleanup failures remain separate and private text is absent",Cleanup),
        ("session-native-diagnostic real held source cleanup prevents assistant diagnostic and idle publication",HeldCleanup),
        ("session-native-diagnostic successful original opaque message is unchanged and creates no native record",OpaqueSuccess)
    ];

    private static async Task Acknowledged()
    {
        using var files=new Files();var storage=new ObservedFactory();var wire=new Wire();var effect=new Effect();
        await using var session=await Create(files,wire.Transport,[new("effect",effect)],storage);
        var turns=0;using var lease=session.Subscribe(new Sink((e,_)=>{if(e is AgentLoopTurnEnded)turns++;return ValueTask.CompletedTask;}));
        var barrier=storage.Storage!.Arm(skip:2);var run=session.PromptAsync(Input());
        string? assistantId=null;
        try
        {
            await Within(barrier.Entered.Task);Equal(1,wire.Cleanups);Equal(1,wire.Requests);Equal(0,effect.Calls);Equal(0,turns);
            assistantId=session.Snapshot.Context.Ancestry.Last(e=>e.Kind==SessionEntryKind.Message).Id;
            Equal("assistant",session.Snapshot.Context.LlmMessages[^1].Role);Equal(0,session.GetNativeDiagnostics().Entries.Length);
            Check(!run.IsCompleted&&!session.WaitForIdleAsync().IsCompleted,"Held custom checkpoint published completion.");
            var acknowledged=session.Snapshot.Log.CommittedByteLength;
            Check(new FileInfo(files.Path).Length>acknowledged,"The controlled checkpoint did not contain real appended bytes.");
            Check(ReadShared(files.Path).Contains(NativeSessionDiagnosticProjector.CustomType,StringComparison.Ordinal),"Native record was not physically written before its held checkpoint.");
            barrier.Release.TrySetResult();await Within(run);Equal(1,turns);
            var row=session.GetNativeDiagnostics().Entries.Single();Equal(assistantId,row.AssistantEntryId);
            Equal(session.Snapshot.Agent.Generation,row.OperationGeneration);Equal(session.Snapshot.Context.LeafId,row.RecordEntryId);
            Equal(NativeChatFailureCode.MalformedStream,row.Diagnostic!.Code);Check(row.CleanupDiagnostic is null,"Unrelated cleanup code was fabricated.");
            Equal(new FileInfo(files.Path).Length,session.Snapshot.Log.CommittedByteLength);Equal(2,session.Snapshot.Context.LlmMessages.Length);
            Check(!session.Snapshot.Context.LlmMessages[^1].WireBody.Value.TryGetProperty("openAICompletionsFailure",out _),"Native failure leaked into a source assistant.");
            Check(session.Snapshot.Fault is null,"Acknowledged custom checkpoint poisoned the writer.");
        }
        finally{barrier.Release.TrySetResult();await Ignore(run);}
        var expected=NativeSessionDiagnosticProjector.ToWire(session.GetNativeDiagnostics()).ToString();
        await session.DisposeAsync();await using var reopened=await PersistentAgentSession.OpenAsync(files.Path,new(Model,new Wire().Transport,[]),()=>123,()=>"reopen-id");
        Equal(expected,NativeSessionDiagnosticProjector.ToWire(reopened.GetNativeDiagnostics()).ToString());
        Equal(assistantId,reopened.GetNativeDiagnostics().Entries.Single().AssistantEntryId);Equal(2,reopened.Snapshot.Context.LlmMessages.Length);
    }

    private static async Task Uncertain()
    {
        using var files=new Files();var storage=new ObservedFactory();var wire=new Wire();await using var session=await Create(files,wire.Transport,[],storage);
        var turns=0;using var lease=session.Subscribe(new Sink((e,_)=>{if(e is AgentLoopTurnEnded)turns++;return ValueTask.CompletedTask;}));
        var barrier=storage.Storage!.Arm(skip:2,fail:true);var run=session.PromptAsync(Input());
        try
        {
            await Within(barrier.Entered.Task);var before=session.Snapshot.Log;barrier.Release.TrySetResult();
            var error=await ThrowsAsync<PersistentAgentSessionException>(()=>run);
            Equal(PersistentAgentSessionFailure.AppendFailed,error.Fault.Failure);Equal(SessionLogStoreFailure.CheckpointFailed,error.Fault.StorageFailure);
            Check(error.Fault.MayHaveWritten&&error.Fault.DurableFlushCompleted,"Uncertain durable checkpoint lost its physical status.");
            Equal(before,session.Snapshot.Log);Equal(0,session.GetNativeDiagnostics().Entries.Length);Equal(0,turns);Equal(1,wire.Requests);
            Check(!error.ToString().Contains("private storage marker",StringComparison.Ordinal),"Private storage exception escaped the public fault.");
            await ThrowsAsync<PersistentAgentSessionException>(()=>session.PromptAsync(Input()));
            await Within(session.WaitForIdleAsync());Equal(1,wire.Requests);
        }
        finally{barrier.Release.TrySetResult();await Ignore(run);}
    }

    private static async Task CancelCheckpoint()
    {
        using var files=new Files();var storage=new ObservedFactory();var wire=new Wire();await using var session=await Create(files,wire.Transport,[],storage);
        using var cancel=new CancellationTokenSource();var barrier=storage.Storage!.Arm(skip:2);var run=session.PromptAsync(Input(),cancel.Token);
        try
        {
            await Within(barrier.Entered.Task);cancel.Cancel();Check(!run.IsCompleted&&!session.WaitForIdleAsync().IsCompleted,"Cancellation detached an accepted diagnostic append.");
            barrier.Release.TrySetResult();await Ignore(run);await Within(session.WaitForIdleAsync());
            Equal(1,session.GetNativeDiagnostics().Entries.Length);Equal(NativeChatFailureCode.MalformedStream,session.GetNativeDiagnostics().Entries.Single().Diagnostic!.Code);
            Check(session.Snapshot.Fault is null,"Shielded checkpoint cancellation poisoned the writer.");
            await Within(session.PromptAsync(Input()));Equal(2,wire.Requests);Equal(2,session.GetNativeDiagnostics().Entries.Length);
        }
        finally{barrier.Release.TrySetResult();await Ignore(run);}
    }

    private static async Task Cleanup()
    {
        using var files=new Files();var wire=new Wire(failCleanup:true);await using var session=await Create(files,wire.Transport,[]);
        await Within(session.PromptAsync(Input()));var row=session.GetNativeDiagnostics().Entries.Single();
        Equal(NativeChatFailureCode.MalformedStream,row.Diagnostic!.Code);Equal(NativeChatFailureCode.CleanupFailed,row.CleanupDiagnostic!.Code);
        var native=NativeSessionDiagnosticProjector.ToWire(session.GetNativeDiagnostics()).ToString();var raw=ReadShared(files.Path);
        Check(!native.Contains("private source cleanup marker",StringComparison.Ordinal)&&!raw.Contains("private source cleanup marker",StringComparison.Ordinal),"Private cleanup payload entered session data.");
        Equal(1,wire.Cleanups);Equal(1,wire.Requests);Check(session.Snapshot.Fault is null,"Closed provider diagnostics poisoned otherwise acknowledged history.");
    }

    private static async Task HeldCleanup()
    {
        using var files=new Files();var wire=new Wire(holdCleanup:true);await using var session=await Create(files,wire.Transport,[]);
        var run=session.PromptAsync(Input());
        try
        {
            await Within(wire.CleanupEntered.Task);Check(!run.IsCompleted&&!session.WaitForIdleAsync().IsCompleted,"Actual wire cleanup detached from session settlement.");
            Equal(1,session.Snapshot.Context.LlmMessages.Length);Equal("user",session.Snapshot.Context.LlmMessages.Single().Role);
            Equal(0,session.GetNativeDiagnostics().Entries.Length);Equal(0,wire.Cleanups);
            wire.CleanupRelease.TrySetResult();await Within(run);Equal(1,wire.Cleanups);Equal(1,session.GetNativeDiagnostics().Entries.Length);
        }
        finally{wire.CleanupRelease.TrySetResult();await Ignore(run);}
    }

    private static async Task OpaqueSuccess()
    {
        using var files=new Files();var opaque=new OpaqueTransport();await using var session=await Create(files,opaque,[]);
        await Within(session.PromptAsync(Input()));Equal(0,session.GetNativeDiagnostics().Entries.Length);
        var stored=session.Snapshot.Context.LlmMessages[^1].WireBody;
        Equal(PiWireJson.WriteMessage(opaque.Message).ToString(),stored.ToString());
        Equal("imported-unknown-code",stored.Value.GetProperty("openAICompletionsFailure").GetString());
        Check(stored.Value.GetProperty("opaque").GetProperty("null").ValueKind==JsonValueKind.Null,"Opaque source data was stripped.");
    }

    private static Task<PersistentAgentSession> Create(Files files,IChatTransport transport,ImmutableArray<ToolDefinition> tools,ObservedFactory? storage=null)
    {
        var codec=new SessionEntryCodec();var header=codec.Parse(JsonSerializer.Serialize(new{type="session",version=3,id="native-diagnostics",timestamp="1970-01-01T00:00:00.000Z",cwd=files.Directory}));
        var id=0;return PersistentAgentSession.CreateAsync(files.Path,header,new(Model,transport,tools),()=>123,()=>"diagnostic-"+Interlocked.Increment(ref id),
            storage is null?null:new(SessionLogStoreOptions:new(StorageFactory:storage)));
    }
    private static TranscriptEntry Input()=>new("user",JsonData.Parse("{\"role\":\"user\",\"content\":\"owned diagnostic test\",\"timestamp\":123}"));
    private sealed class Wire(bool failCleanup=false,bool holdCleanup=false)
    {
        public int Requests,Cleanups;public TaskCompletionSource CleanupEntered=Gate(),CleanupRelease=Gate();
        public IChatTransport Transport=>new OpenAICompletionsWireSource((_,token)=>Read(token));
        private async IAsyncEnumerable<JsonData> Read([EnumeratorCancellation]CancellationToken token)
        {
            Requests++;
            try{token.ThrowIfCancellationRequested();yield return JsonData.Parse("{\"choices\":\"malformed owned fixture\"}");await Task.Yield();}
            finally{CleanupEntered.TrySetResult();if(holdCleanup)await CleanupRelease.Task;Cleanups++;if(failCleanup)throw new IOException("private source cleanup marker");}
        }
    }
    private sealed class OpaqueTransport:IChatTransport
    {
        public AssistantMessage Message{get;}=new(Model.Api,Model.Provider,Model.Id,123,[new TextContent("unchanged source")],TokenUsage.Zero,StopReason.Stop)
        {ExtraProperties=JsonFields.Empty.Set("openAICompletionsFailure",JsonData.Parse("\"imported-unknown-code\"")).Set("opaque",JsonData.Parse("{\"null\":null,\"scale\":1.0}")).Set("thinkingLevel",JsonData.Parse("\"off\""))};
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request,[EnumeratorCancellation]CancellationToken token=default)
        {token.ThrowIfCancellationRequested();yield return new StreamStarted(Message with{Content=[],StopReason=StopReason.Pending});yield return new TextStarted(0,new(""));yield return new TextEnded(0,"unchanged source");yield return new StreamDone(StopReason.Stop,Message);await Task.Yield();}
    }
    private sealed class Effect:IToolExecutor{public int Calls;public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation,CancellationToken token){Calls++;return ValueTask.FromResult(ToolResult.Success("effect"));}}
    private sealed class Sink(Func<AgentEvent,CancellationToken,ValueTask> callback):IAgentEventSink{public ValueTask EmitAsync(AgentEvent e,CancellationToken t)=>callback(e,t);}
    private sealed class ObservedFactory:ISessionLogStorageFactory
    {
        public ObservedStorage? Storage;
        public async ValueTask<ISessionLogStorage> OpenAsync(string path,bool createNew,CancellationToken token)=>Storage=new(await SessionLogStore.DefaultStorageFactory.OpenAsync(path,createNew,token));
    }
    private sealed class Barrier(bool fail)
    {
        public TaskCompletionSource Entered=Gate(),Release=Gate();
        public async ValueTask Wait(){Entered.TrySetResult();await Release.Task;if(fail)throw new IOException("private storage marker");}
    }
    private sealed class ObservedStorage(ISessionLogStorage inner):ISessionLogStorage
    {
        private Barrier? _barrier;private int _skip;
        public Stream ReadStream=>inner.ReadStream;public SessionLogStorageDurability Durability=>inner.Durability;public long Length=>inner.Length;
        public void PositionForAppend(long expected)=>inner.PositionForAppend(expected);public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes)=>inner.WriteAsync(bytes);
        public ValueTask FlushAsync()=>inner.FlushAsync();public void FlushToDisk()=>inner.FlushToDisk();
        public Barrier Arm(int skip=0,bool fail=false){_skip=skip;return _barrier=new(fail);}
        public async ValueTask BeforeCheckpointAsync(){await inner.BeforeCheckpointAsync();if(_barrier is { } b&&_skip--<=0){_barrier=null;await b.Wait();}}
        public ValueTask DisposeAsync()=>inner.DisposeAsync();
    }
    private sealed class Files:IDisposable
    {
        public string Directory{get;}=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"PiSharp-native-diagnostic-"+Guid.NewGuid().ToString("N"));public string Path=>System.IO.Path.Combine(Directory,"session.jsonl");
        public Files()=>System.IO.Directory.CreateDirectory(Directory);
        public void Dispose(){var target=System.IO.Path.GetFullPath(Directory);if(System.IO.Path.GetDirectoryName(target)!=System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()))||!System.IO.Path.GetFileName(target).StartsWith("PiSharp-native-diagnostic-",StringComparison.Ordinal))throw new InvalidOperationException("Invalid owned cleanup path.");System.IO.Directory.Delete(target,true);}
    }
    private static TaskCompletionSource Gate()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string ReadShared(string path)
    {using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);using var reader=new StreamReader(file);return reader.ReadToEnd();}
    private static async Task Within(Task task)=>await task.WaitAsync(TimeSpan.FromSeconds(10));
    private static async Task Ignore(Task task){try{await Within(task);}catch(Exception){}}
    private static async Task<T> ThrowsAsync<T>(Func<Task> action)where T:Exception{try{await Within(action());}catch(T error){return error;}throw new InvalidOperationException("Expected "+typeof(T).Name);}
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static void Equal<T>(T expected,T actual)=>Check(EqualityComparer<T>.Default.Equals(expected,actual),$"Expected {expected}; actual {actual}.");
}
