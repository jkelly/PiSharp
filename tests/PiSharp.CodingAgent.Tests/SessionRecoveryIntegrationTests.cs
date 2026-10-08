using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Compaction;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;
using PiSharp.Rpc;
using PiSharp.Rpc.Protocol;
using PiSharp.Cli.Interactive;

internal static class SessionRecoveryIntegrationTests
{
    private static readonly ModelDescriptor Model=new("recovery-model","openai-responses","fixture");
    private static readonly SessionEntryCodec Codec=new();
    public static (string Name,Func<Task> Run)[] Cases()=>
    [
        ("session-recovery overflow commits omission summary fresh context usage and reopen before final settlement",Overflow),
        ("session-recovery early length omits synthetic failed tools and never executes truncated calls",LengthTools),
        ("session-recovery rejection after successful tool retains its result and never replays effect",CompletedEffect),
        ("session-recovery awaited lowlevel end and queued followup remain inside one accepted operation",EndListenerAndQueue),
        ("session-recovery failed summary keeps durable omission without checkpoint or hidden retry",FailedSummary),
        ("session-recovery second overflow exhausts exactly one compact retry without another omission",RetryBound),
        ("session-recovery last boundary input and awaited final listener keep operation busy and reject selfwait",SettlementBoundary),
        ("session-recovery direct abort held summary cleanup joins one cancelled final settlement",CancelSummary),
        ("session-recovery classifier consumes all65 actual unchanged whole source helper schedules and24patterns",ClassifierSource),
        ("session-recovery submitted input generation overflow rejects atomically and leaves writer usable",AdmissionOverflow),
        ("session-recovery preomission cancellation keeps acknowledgments and does not poison writer",CancelBeforeOmission),
        ("session-recovery final listener fault awaits every listener then closes operation with truthful fault",FinalListenerFault),
        ("session-recovery final settlement closes queue admission before effects",SettlementFence),
        ("session-recovery disabled configuration preserves original provider failure without hidden work",Disabled),
        ("session-recovery RPC fresh retry resets message delta boundary and settles exactly once",RpcRetry),
        ("session-recovery RPC and frontend distinguish held recovery from idle and join final output",RpcRecoveryState),
        ("session-recovery RPC abort joins held summary cleanup before response and final settlement",RpcAbort),
        ("session-recovery replacement rejects busy retry then bars retired writes after cleanup and fresh switch",Replacement),
        ("session-recovery final cancellation callback drain remains busy until actual abort user joins",FinalCancellationDrain)
    ];
    private static async Task FinalCancellationDrain()
    {
        using var files=new Files();var clock=new Clock();var transport=new Script(clock,[Message(),Message()]);await using var session=await Create(files,clock,transport);
        var finalEntered=Gate();var finalRelease=Gate();var callbackEntered=Gate();var callbackRelease=Gate();var finals=0;
        using var lease=session.SubscribeOperationEvents(new OperationSink(async(e,_)=>
        {if(e is SessionOperationSettled&&Interlocked.Increment(ref finals)==1){finalEntered.TrySetResult();await finalRelease.Task;}}));
        var run=session.PromptAsync(Input("actual held cancellation callback"));Task<bool>? abort=null;
        var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
        var authorityField=typeof(PersistentAgentSession).GetField("_runCancellation",flags)!;
        var gate=typeof(PersistentAgentSession).GetField("_gate",flags)!.GetValue(session)!;
        CancellationTokenRegistration registration=default;
        try
        {
            await Within(finalEntered.Task);
            // Test-only attachment to the actual cancellation authority. No counters, phases or reservations are changed.
            object authority;lock(gate)authority=authorityField.GetValue(session)!;
            var actualAbort=(CancellationTokenSource)authority.GetType().GetField("Abort",flags)!.GetValue(authority)!;
            registration=actualAbort.Token.Register(()=>{callbackEntered.TrySetResult();callbackRelease.Task.GetAwaiter().GetResult();});
            using(ExecutionContext.SuppressFlow())abort=Task.Run(session.Abort);
            await Within(callbackEntered.Task);finalRelease.TrySetResult();
            var deadline=DateTime.UtcNow.AddSeconds(10);
            while(true)
            {
                bool released;lock(gate)released=authorityField.GetValue(session) is null;
                if(released)break;
                Check(DateTime.UtcNow<deadline,"Final delivery did not reach actual cancellation callback drain.");await Task.Yield();
            }
            Check(!abort.IsCompleted&&!run.IsCompleted&&!session.WaitForIdleAsync().IsCompleted,"Held real cancellation callback did not retain settlement.");
            var before=session.Snapshot;Check(before.IsProcessingOperation&&!before.Agent.IsRunning&&before.OperationPhase==SessionOperationPhase.Settlement,"Snapshot reported idle while actual cancellation callbacks still owned the operation.");
            var queue=JsonSerializer.Serialize(session.GetPendingInputQueueSnapshot());
            Throws<InvalidOperationException>(()=>session.PromptAsync(Input("busy prompt")));Throws<InvalidOperationException>(()=>session.FollowUp(Input("busy followup")));Throws<InvalidOperationException>(()=>session.Steer(Input("busy steering")));
            await ThrowsAsync<InvalidOperationException>(()=>session.SubmitInputAsync(new("busy submitted",PromptInputSource.Rpc,StreamingBehavior:PromptInputStreamingBehavior.FollowUp)));
            Equal(queue,JsonSerializer.Serialize(session.GetPendingInputQueueSnapshot()));Equal(before.Log,session.Snapshot.Log);Equal(1,transport.Requests.Count);
            callbackRelease.TrySetResult();Check(await Within(abort),"Actual abort failed to observe the held authority.");await Within(run);
            Check(!session.Snapshot.IsProcessingOperation&&session.Snapshot.OperationPhase==SessionOperationPhase.Idle&&session.WaitForIdleAsync().IsCompleted,"Joined callbacks retained a busy snapshot.");
            await Within(session.PromptAsync(Input("writer usable after callback join")));Equal(2,transport.Requests.Count);Equal(2,finals);Check(session.Snapshot.Fault is null,"Callback drain poisoned the writer.");
        }
        finally{finalRelease.TrySetResult();callbackRelease.TrySetResult();if(abort is not null)await Ignore(abort);await Ignore(run);registration.Dispose();}
    }
    private static async Task AdmissionOverflow()
    {
        using var files=new Files();var clock=new Clock();var transport=new Script(clock,[Message()]);await using var session=await Create(files,clock,transport);
        var field=typeof(PersistentAgentSession).GetField("_operationGeneration",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
        field.SetValue(session,long.MaxValue);var before=session.Snapshot;var bytes=await ReadOwnedBytes(files.Path);
        await ThrowsAsync<OverflowException>(()=>session.SubmitInputAsync(new("overflow admission",PromptInputSource.Rpc)));
        Equal(0,transport.Requests.Count);Equal(before.Log,session.Snapshot.Log);Check((await ReadOwnedBytes(files.Path)).SequenceEqual(bytes),"Overflow admission wrote history.");
        Check(!session.Snapshot.IsProcessingOperation&&!session.Snapshot.IsAdmittingInput&&session.WaitForIdleAsync().IsCompleted,"Overflow retained an operation/admission reservation.");
        field.SetValue(session,0L);var accepted=await Within(session.SubmitInputAsync(new("valid admission",PromptInputSource.Rpc)));
        Equal(SubmittedInputDisposition.Started,accepted.Disposition);Equal(1L,session.Snapshot.OperationGeneration);Equal(1,transport.Requests.Count);
    }
    private static async Task CancelBeforeOmission()
    {
        using var files=new Files();var clock=new Clock();var transport=new Script(clock,[Error(),Message()]);var summary=new Summary();await using var session=await Create(files,clock,transport);Configure(session,summary);
        using var cancel=new CancellationTokenSource();var entered=Gate();var release=Gate();var events=new List<SessionOperationEvent>();
        using var lease=session.SubscribeOperationEvents(new OperationSink(async(e,_)=>{events.Add(e);if(e is SessionRecoveryStarted){entered.TrySetResult();await release.Task;}}));
        var run=session.PromptAsync(Input("cancel before omission"),cancel.Token);
        try
        {
            await Within(entered.Task);var acknowledged=session.Snapshot.Log;cancel.Cancel();Check(!run.IsCompleted,"Cancellation detached the recovery listener.");release.TrySetResult();
            await ThrowsAsync<OperationCanceledException>(()=>run);Equal(acknowledged,session.Snapshot.Log);Equal(0,summary.Calls);Equal(0,session.Snapshot.Log.Entries.Count(e=>e.Kind==SessionEntryKind.ContextEdit));
            Equal("cancelled",events.OfType<SessionOperationSettled>().Single().Status);Check(session.Snapshot.Fault is null&&!session.Snapshot.IsProcessingOperation,"Safe cancellation poisoned or retained the writer.");
            session.ConfigureAutomaticRecovery(null);await Within(session.PromptAsync(Input("writer remains usable")));Equal(2,transport.Requests.Count);
        }
        finally{release.TrySetResult();await Ignore(run);}
    }
    private static async Task FinalListenerFault()
    {
        using var files=new Files();var clock=new Clock();var transport=new Script(clock,[Message()]);await using var session=await Create(files,clock,transport);
        var expected=new IOException("authored final listener fault");var entered=Gate();var release=Gate();var first=0;var second=0;
        using var a=session.SubscribeOperationEvents(new OperationSink((e,_)=>{if(e is SessionOperationSettled){first++;throw expected;}return ValueTask.CompletedTask;}));
        using var b=session.SubscribeOperationEvents(new OperationSink(async(e,_)=>{if(e is SessionOperationSettled){second++;entered.TrySetResult();await release.Task;}}));
        var run=session.PromptAsync(Input("final delivery"));
        try
        {
            await Within(entered.Task);Check(!run.IsCompleted&&!session.WaitForIdleAsync().IsCompleted,"A throwing listener detached remaining required delivery.");
            release.TrySetResult();Check(ReferenceEquals(expected,await ThrowsAsync<IOException>(()=>run)),"Final listener failure identity changed.");
            Equal(1,first);Equal(1,second);Equal(PersistentAgentSessionFailure.RunFailed,session.Snapshot.Fault!.Failure);
            Check(!session.Snapshot.IsProcessingOperation&&session.WaitForIdleAsync().IsCompleted,"Listener failure retained a busy operation.");
            Throws<PersistentAgentSessionException>(()=>session.PromptAsync(Input("must inspect fault")));
        }
        finally{release.TrySetResult();await Ignore(run);}
    }
    private static async Task SettlementFence()
    {
        using var files=new Files();var clock=new Clock();var transport=new Script(clock,[Message()]);await using var session=await Create(files,clock,transport);
        var entered=Gate();var release=Gate();using var lease=session.SubscribeOperationEvents(new OperationSink(async(e,_)=>{if(e is SessionOperationSettled){entered.TrySetResult();await release.Task;}}));
        var run=session.PromptAsync(Input("fenced settlement"));
        try
        {
            await Within(entered.Task);var snapshot=session.Snapshot;var queue=session.GetPendingInputQueueSnapshot();
            Throws<InvalidOperationException>(()=>session.Steer(Input("too late steering")));Throws<InvalidOperationException>(()=>session.FollowUp(Input("too late followup")));
            await ThrowsAsync<InvalidOperationException>(()=>session.SubmitInputAsync(new("too late submitted",PromptInputSource.Rpc,StreamingBehavior:PromptInputStreamingBehavior.FollowUp)));
            Equal(JsonSerializer.Serialize(queue),JsonSerializer.Serialize(session.GetPendingInputQueueSnapshot()));Equal(snapshot.Log,session.Snapshot.Log);Equal(1,transport.Requests.Count);
            release.TrySetResult();await Within(run);session.FollowUp(Input("next operation"));Equal(1,session.GetPendingInputQueueSnapshot().FollowUpMessages.Length);
        }
        finally{release.TrySetResult();await Ignore(run);}
    }
    private static async Task Disabled()
    {
        using var files=new Files();var clock=new Clock();var transport=new Script(clock,[Error()]);var summary=new Summary();await using var session=await Create(files,clock,transport);
        session.ConfigureAutomaticCompaction(summary,new(true,128,80),100_000,recoveryDesiredMaxOutput:1000);Check(session.Snapshot.AutoRecoveryEnabled,"Atomic automatic recovery configuration did not enable recovery.");
        session.ConfigureAutomaticCompaction(null);Check(!session.Snapshot.AutoRecoveryEnabled,"Disabling automatic work retained recovery authority.");
        var events=new List<SessionOperationEvent>();using var lease=session.SubscribeOperationEvents(new OperationSink((e,_)=>{events.Add(e);return ValueTask.CompletedTask;}));
        var result=await Within(session.PromptAsync(Input("disabled recovery")));Equal(AgentLoopStopReason.ChatFailure,result.Reason);Equal("failed",events.OfType<SessionOperationSettled>().Single().Status);
        Equal(1,transport.Requests.Count);Equal(0,summary.Calls);Equal(0,events.OfType<SessionRecoveryStarted>().Count());Equal(0,session.Snapshot.Log.Entries.Count(e=>e.Kind==SessionEntryKind.ContextEdit));
    }
    private static readonly JsonData ModelWire=JsonData.Parse("{\"id\":\"recovery-model\",\"api\":\"openai-responses\",\"provider\":\"fixture\",\"name\":\"Authored recovery fixture\",\"baseUrl\":\"https://offline.invalid\",\"reasoning\":false,\"input\":[\"text\"],\"contextWindow\":100000,\"maxTokens\":1000,\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0}}");
    private static RpcSessionDispatcher Dispatcher(PersistentAgentSession session,CaptureStream capture,Clock clock)=>new(session,new JsonlWriter(capture),clock.Next,[new(Model,ModelWire)],sessionOwnership:RpcSessionOwnership.Borrowed);
    private static JsonData Command(string id,string type,string? text=null)=>JsonData.Parse(JsonSerializer.Serialize(new{id,type,message=text}));
    private static async Task RpcRetry()
    {
        using var files=new Files();var clock=new Clock();var transport=new Script(clock,[Error(),Message()]);var summary=new Summary();await using var session=await Create(files,clock,transport);Configure(session,summary);
        using var capture=new CaptureStream();await using var dispatcher=Dispatcher(session,capture,clock);
        await Within(dispatcher.SubmitAsync(Command("prompt","prompt","RPC recovery")));await Within(dispatcher.WaitForIdleAsync());
        var records=capture.Records();var ends=records.Where(r=>Type(r)=="agent_end").ToArray();Equal(2,ends.Length);
        Equal(2,ends[0].Value.GetProperty("messages").GetArrayLength());Equal(1,ends[1].Value.GetProperty("messages").GetArrayLength());
        Equal("stop",ends[1].Value.GetProperty("messages")[0].GetProperty("stopReason").GetString());
        Equal(1,records.Count(r=>Type(r)=="pisharp_recovery_started"));Equal(1,records.Count(r=>Type(r)=="pisharp_recovery_ended"));Equal(1,records.Count(r=>Type(r)=="pisharp_operation_settled"));Equal(1,records.Count(r=>Type(r)=="agent_settled"));
        Check(Array.FindIndex(records,r=>Type(r)=="pisharp_operation_settled")<Array.FindIndex(records,r=>Type(r)=="agent_settled"),"RPC declared idle before coordinator delivery.");
        Equal(2,transport.Requests.Count);Equal(1,summary.Calls);Equal(2,transport.Cleanups);
    }
    private static async Task RpcRecoveryState()
    {
        using var files=new Files();var clock=new Clock();var transport=new Script(clock,[Error(),Message()]);var summary=new PausedSummary();await using var session=await Create(files,clock,transport);Configure(session,summary);
        using var capture=new CaptureStream("pisharp_operation_settled");await using var dispatcher=Dispatcher(session,capture,clock);
        var admitted=dispatcher.SubmitAsync(Command("prompt","prompt","held RPC recovery"));
        try
        {
            await Within(admitted);await Within(summary.Entered.Task);await Within(dispatcher.SubmitAsync(Command("state","get_state")));
            var response=capture.Records().Single(r=>Type(r)=="response"&&r.Value.GetProperty("id").GetString()=="state");var state=response.Value.GetProperty("data");
            Check(!state.GetProperty("isStreaming").GetBoolean()&&state.GetProperty("isCompacting").GetBoolean()&&state.GetProperty("pisharpOperationActive").GetBoolean(),"RPC recovery state falsely reported idle.");Equal("Compaction",state.GetProperty("pisharpOperationPhase").GetString());
            using var display=new StringWriter();using var frontend=new InteractiveSessionFrontend(display);frontend.Bind((_,_)=>Task.CompletedTask);await frontend.ObserveAsync(response,default);
            Check(display.ToString().Contains("[state] compacting",StringComparison.Ordinal)&&!display.ToString().Contains("[state] idle",StringComparison.Ordinal),"Frontend lost continuing recovery state.");
            Check(!dispatcher.WaitForIdleAsync().IsCompleted,"RPC settled during held summary.");summary.Release.TrySetResult();await Within(capture.Entered.Task);
            Check(!dispatcher.WaitForIdleAsync().IsCompleted&&!session.WaitForIdleAsync().IsCompleted&&session.Snapshot.IsProcessingOperation,"Final output flush was detached from SDK/RPC settlement.");
            Equal(0,capture.Records().Count(r=>Type(r)=="agent_settled"));capture.Release.TrySetResult();await Within(dispatcher.WaitForIdleAsync());Equal(1,capture.Records().Count(r=>Type(r)=="agent_settled"));
        }
        finally{summary.Release.TrySetResult();capture.Release.TrySetResult();session.Abort();await Ignore(admitted);await Ignore(dispatcher.WaitForIdleAsync());}
    }
    private static async Task RpcAbort()
    {
        using var files=new Files();var clock=new Clock();var transport=new Script(clock,[Error()]);var summary=new HeldSummary();await using var session=await Create(files,clock,transport);Configure(session,summary);
        using var capture=new CaptureStream();await using var dispatcher=Dispatcher(session,capture,clock);await Within(dispatcher.SubmitAsync(Command("prompt","prompt","abort recovery")));
        Task? abort=null;
        try
        {
            await Within(summary.Entered.Task);abort=dispatcher.SubmitAsync(Command("abort","abort"));await Within(summary.CleanupEntered.Task);
            Check(!abort.IsCompleted&&!dispatcher.WaitForIdleAsync().IsCompleted,"RPC abort detached summary cleanup.");Equal(0,capture.Records().Count(r=>Type(r)=="agent_settled"));
            summary.Release.TrySetResult();await Within(abort);await Within(dispatcher.WaitForIdleAsync());Equal(1,summary.Cleanups);Equal(1,transport.Requests.Count);
            Equal("cancelled",capture.Records().Single(r=>Type(r)=="pisharp_operation_settled").Value.GetProperty("status").GetString());
            Equal(0,session.Snapshot.Log.Entries.Count(e=>e.Kind==SessionEntryKind.Compaction));Equal(1,session.Snapshot.Log.Entries.Count(e=>e.Kind==SessionEntryKind.ContextEdit));
        }
        finally{summary.Release.TrySetResult();session.Abort();if(abort is not null)await Ignore(abort);await Ignore(dispatcher.WaitForIdleAsync());}
    }
    private static async Task Replacement()
    {
        using var files=new Files();using var targetFiles=new Files();var clock=new Clock();var oldTransport=new Script(clock,[Error()]);var freshTransport=new Script(clock,[Message()]);
        var target=await Create(targetFiles,clock,freshTransport);await target.DisposeAsync();var initial=await Create(files,clock,oldTransport);var summary=new HeldSummary();Configure(initial,summary);var opens=0;
        // Both stores declare one workspace, as required by the existing replacement contract.
        await using var owner=new ReplaceableAgentSession(initial,async(request,token)=>{opens++;return await PersistentAgentSession.OpenAsync(request.Path,new(Model,freshTransport,[]),clock.Next,clock.Id,cancellationToken:token);});
        var stale=owner.Current;var run=initial.PromptAsync(Input("replace during recovery"));
        try
        {
            await Within(summary.Entered.Task);await ThrowsAsync<InvalidOperationException>(()=>owner.SwitchAsync(stale,new(targetFiles.Path)));Equal(0,opens);Equal(stale,owner.Current);
            initial.Abort();await Within(summary.CleanupEntered.Task);Check(!run.IsCompleted,"Retirement abandoned summary cleanup.");summary.Release.TrySetResult();await Within(run);
            // Open a target in the same declared workspace; its path itself may reside in another store.
            var raw=File.ReadAllText(targetFiles.Path);raw=raw.Replace(JsonSerializer.Serialize(targetFiles.Directory),JsonSerializer.Serialize(files.Directory),StringComparison.Ordinal);File.WriteAllText(targetFiles.Path,raw);
            var replacement=await Within(owner.SwitchAsync(stale,new(targetFiles.Path)));Equal(2L,replacement!.Current.Generation);Equal(1,opens);
            var freshBytes=await ReadOwnedBytes(targetFiles.Path);await ThrowsAsync<InvalidOperationException>(()=>owner.AppendExtensionEntryAsync(stale,new("sample.recovery","state",1,JsonData.EmptyObject)));
            Check((await ReadOwnedBytes(targetFiles.Path)).SequenceEqual(freshBytes),"Stale recovery authority appended to the replacement writer.");
            await Within(replacement.Current.Session.PromptAsync(Input("fresh context")));Equal(1,freshTransport.Requests.Count);Check(!freshTransport.Requests[0].Messages.Any(IsError),"Old failed recovery leaked into replacement context.");Equal(1,summary.Cleanups);
        }
        finally{summary.Release.TrySetResult();initial.Abort();await Ignore(run);}
    }
    private sealed class PausedSummary:ISessionSummaryGenerator
    {
        public readonly TaskCompletionSource Entered=Gate(),Release=Gate();
        public async ValueTask<SessionGeneratedSummary> GenerateAsync(SessionSummaryRequest request,CancellationToken token=default)
        {Entered.TrySetResult();await Release.Task.WaitAsync(token);return new("paused authored summary",TokenUsage.Zero);}
    }
    private sealed class CaptureStream(string? heldType=null):Stream
    {
        private readonly object gate=new();private readonly List<JsonData> records=[];private string? last;
        internal readonly TaskCompletionSource Entered=Gate(),Release=Gate();internal JsonData[] Records(){lock(gate)return records.ToArray();}
        public override bool CanRead=>false;public override bool CanSeek=>false;public override bool CanWrite=>true;public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken token=default)
        {token.ThrowIfCancellationRequested();var r=JsonData.Parse(System.Text.Encoding.UTF8.GetString(buffer.Span));lock(gate){if(records.Count>=128)throw new InvalidOperationException("Capture bound");records.Add(r);last=Type(r);}return ValueTask.CompletedTask;}
        public override async Task FlushAsync(CancellationToken token){string? kind;lock(gate)kind=last;if(kind==heldType&&heldType is not null){Entered.TrySetResult();await Release.Task.WaitAsync(token);}}
        public override void Flush()=>throw new NotSupportedException();public override int Read(byte[]b,int o,int c)=>throw new NotSupportedException();public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long l)=>throw new NotSupportedException();public override void Write(byte[]b,int o,int c)=>throw new NotSupportedException();
    }
    private static string Type(JsonData record)=>record.Value.GetProperty("type").GetString()!;
    private static async Task<byte[]> ReadOwnedBytes(string path)
    {
        // The durable writer is still held. This observer admits its existing write access.
        await using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        using var bytes=new MemoryStream();await input.CopyToAsync(bytes);return bytes.ToArray();
    }
    private static async Task<T> ThrowsAsync<T>(Func<Task> callback)where T:Exception
    {try{await Within(callback());}catch(T error){return error;}throw new InvalidOperationException("Expected "+typeof(T).Name);}
    private static async Task Overflow()
    {
        using var files=new Files();var clock=new Clock();var transport=new Script(clock,[Error(),Message()]);var summary=new Summary();
        ImmutableArray<TranscriptEntry> expected;
        await using(var session=await Create(files,clock,transport))
        {
            Configure(session,summary);var events=new List<SessionOperationEvent>();using var lease=session.SubscribeOperationEvents(new OperationSink((e,_)=>{events.Add(e);return ValueTask.CompletedTask;}));
            var result=await Within(session.PromptAsync(Input("fresh prompt")));
            Equal(2,transport.Requests.Count);Equal(2,result.Turns.Length);Equal(1,summary.Calls);Equal(2,transport.Cleanups);
            Equal(AgentLoopStopReason.Completed,result.Reason);Equal(1,session.Snapshot.Log.Entries.Count(e=>e.Kind==SessionEntryKind.ContextEdit));
            Equal(1,session.Snapshot.Log.Entries.Count(e=>e.Kind==SessionEntryKind.Compaction));
            Check(!transport.Requests[1].Messages.Any(IsError),"Fresh retry still contains failed assistant.");
            Check(transport.Requests[1].Messages.Any(m=>m.Role=="user"&&m.WireBody.ToString().Contains("fresh prompt",StringComparison.Ordinal)),"Retry lost admitted prompt.");
            Check(events.OfType<SessionRecoveryStarted>().Single().WillRetry&&events.OfType<SessionRecoveryEnded>().Single().WillRetry,"Recovery disposition missing.");
            Equal(1,events.OfType<SessionOperationSettled>().Count());Equal("completed",events.OfType<SessionOperationSettled>().Single().Status);
            Check(!session.Snapshot.IsProcessingOperation&&session.Snapshot.OperationPhase==SessionOperationPhase.Idle,"Session did not settle.");
            expected=session.Snapshot.Context.LlmMessages;
            var raw=session.Snapshot.Log.Entries.Where(e=>e.Kind==SessionEntryKind.Message).Select(e=>e.WireBody.Value.GetProperty("message")).ToArray();
            Equal(1,raw.Count(m=>m.TryGetProperty("stopReason",out var s)&&s.GetString()=="error"));
            var history=new SessionHistoryProjector().Project(session.Snapshot.Log.Entries,session.Snapshot.Context.LeafId);
            Check(history.SessionStatistics.Totals!.Cost>=.4,"Raw failed attempt or summary billing vanished.");
        }
        await using var reopened=await PersistentAgentSession.OpenAsync(files.Path,new(Model,new Script(clock,[Message()]),[]),clock.Next,clock.Id);
        Equal(string.Join('\n',expected.Select(m=>m.WireBody.ToString())),string.Join('\n',reopened.Snapshot.Context.LlmMessages.Select(m=>m.WireBody.ToString())));
        Equal(1,reopened.Snapshot.Log.Entries.Count(e=>e.Kind==SessionEntryKind.ContextEdit));
    }
    private static async Task LengthTools()
    {
        using var files=new Files();var clock=new Clock();var effect=new Effect();var transport=new Script(clock,[Message(StopReason.Length,tool:true),Message()]);var summary=new Summary();
        await using var session=await Create(files,clock,transport,[new("write",effect)]);Configure(session,summary);
        var result=await Within(session.PromptAsync(Input("length prompt")));
        Equal(0,effect.Calls);Equal(2,transport.Requests.Count);Equal(2,result.Turns.Length);Equal(1,summary.Calls);
        Equal(2,session.Snapshot.Log.Entries.Count(e=>e.Kind==SessionEntryKind.ContextEdit));
        Check(!transport.Requests[1].Messages.Any(m=>m.Role=="toolResult"),"Synthetic truncated result leaked into fresh context.");
        Check(!transport.Requests[1].Messages.Where(m=>m.Role=="assistant").Any(m=>PiWireJson.ReadMessage(m.WireBody.Value).StopReason==StopReason.Length),"Length assistant survived omission.");
    }
    private static async Task CompletedEffect()
    {
        using var files=new Files();var clock=new Clock();var effect=new Effect();var transport=new Script(clock,[Message(StopReason.ToolUse,tool:true),Error(),Message()]);var summary=new Summary();
        await using var session=await Create(files,clock,transport,[new("write",effect)]);Configure(session,summary,keep:1_000);
        var result=await Within(session.PromptAsync(Input("perform effect then provider rejects")));
        Equal(1,effect.Calls);Equal(3,transport.Requests.Count);Equal(3,result.Turns.Length);
        Check(transport.Requests[2].Messages.Any(m=>m.Role=="toolResult"&&m.WireBody.ToString().Contains("effect receipt",StringComparison.Ordinal)),"Fresh retry lost successful effect result.");
        Equal(1,session.Snapshot.Log.Entries.Count(e=>e.Kind==SessionEntryKind.ContextEdit));
    }
    private static async Task EndListenerAndQueue()
    {
        using var files=new Files();var clock=new Clock();var transport=new Script(clock,[Error(),Message(),Message()]);var summary=new Summary();
        await using var session=await Create(files,clock,transport);Configure(session,summary);var entered=Gate();var release=Gate();var first=true;
        using var lowlevel=session.Subscribe(new AgentSink(async (e,_)=>{if(e is AgentLoopEnded&&first){first=false;entered.TrySetResult();await release.Task;session.FollowUp(Input("queued at end listener"));}}));
        var events=new List<SessionOperationEvent>();using var lease=session.SubscribeOperationEvents(new OperationSink((e,_)=>{events.Add(e);return ValueTask.CompletedTask;}));
        var run=session.PromptAsync(Input("initial"));
        try
        {
            await Within(entered.Task);Equal(0,summary.Calls);Check(!run.IsCompleted&&!session.WaitForIdleAsync().IsCompleted,"Lowlevel end prematurely settled operation.");
            release.TrySetResult();var result=await Within(run);Equal(3,transport.Requests.Count);Equal(3,result.Turns.Length);Equal(1,events.OfType<SessionOperationSettled>().Count());
            Check(transport.Requests[2].Messages.Any(m=>m.Role=="user"&&m.WireBody.ToString().Contains("queued at end listener",StringComparison.Ordinal)),"Queued input lost across repair.");
        }
        finally{release.TrySetResult();await Ignore(run);}
    }
    private static async Task FailedSummary()
    {
        using var files=new Files();var clock=new Clock();var transport=new Script(clock,[Error()]);var summary=new Summary(fail:true);
        await using var session=await Create(files,clock,transport);Configure(session,summary);
        var result=await Within(session.PromptAsync(Input("keep omissions")));
        Equal(AgentLoopStopReason.ChatFailure,result.Reason);Equal(1,summary.Calls);Equal(1,transport.Requests.Count);
        Equal(1,session.Snapshot.Log.Entries.Count(e=>e.Kind==SessionEntryKind.ContextEdit));Equal(0,session.Snapshot.Log.Entries.Count(e=>e.Kind==SessionEntryKind.Compaction));
        Check(!session.Snapshot.Context.LlmMessages.Any(IsError)&&session.Snapshot.Fault is null,"Safe failed summary lost omission or poisoned writer.");
    }
    private static async Task RetryBound()
    {
        using var files=new Files();var clock=new Clock();var transport=new Script(clock,[Error(),Error()]);var summary=new Summary();
        await using var session=await Create(files,clock,transport);Configure(session,summary);var events=new List<SessionOperationEvent>();using var lease=session.SubscribeOperationEvents(new OperationSink((e,_)=>{events.Add(e);return ValueTask.CompletedTask;}));
        var result=await Within(session.PromptAsync(Input("one retry")));
        Equal(2,transport.Requests.Count);Equal(1,summary.Calls);Equal(AgentLoopStopReason.ChatFailure,result.Reason);
        Equal(1,session.Snapshot.Log.Entries.Count(e=>e.Kind==SessionEntryKind.ContextEdit));Equal("retry-exhausted",events.OfType<SessionRecoveryEnded>().Last().Status);
        Check(session.Snapshot.Context.LlmMessages.Any(IsError),"Second failed attempt was silently hidden.");
    }
    private static async Task SettlementBoundary()
    {
        using var files=new Files();var clock=new Clock();var transport=new Script(clock,[Message(),Message()]);await using var session=await Create(files,clock,transport);
        var boundaries=0;session.ConfigureAutomaticRecovery(null,_=>{if(++boundaries==1)session.FollowUp(Input("last boundary"));return ValueTask.CompletedTask;});
        var entered=Gate();var release=Gate();var settled=0;
        using var lease=session.SubscribeOperationEvents(new OperationSink(async (e,_)=>
        {
            if(e is SessionOperationSettled)
            {settled++;Check(session.Snapshot.IsProcessingOperation,"Final listener saw idle before delivery.");Throws<InvalidOperationException>(()=>session.WaitForIdleAsync());entered.TrySetResult();await release.Task;}
        }));
        var run=session.PromptAsync(Input("ordinary"));
        try
        {
            await Within(entered.Task);Equal(2,transport.Requests.Count);Equal(2,boundaries);Equal(1,settled);
            Check(!run.IsCompleted&&!session.WaitForIdleAsync().IsCompleted,"Final awaited listener did not hold settlement.");
            Throws<InvalidOperationException>(()=>session.PromptAsync(Input("concurrent")));
            release.TrySetResult();await Within(run);Check(!session.Snapshot.IsProcessingOperation,"Final delivery failed to release operation.");
        }
        finally{release.TrySetResult();await Ignore(run);}
    }
    private static async Task CancelSummary()
    {
        using var files=new Files();var clock=new Clock();var transport=new Script(clock,[Error()]);var summary=new HeldSummary();await using var session=await Create(files,clock,transport);Configure(session,summary);
        var events=new List<SessionOperationEvent>();using var lease=session.SubscribeOperationEvents(new OperationSink((e,_)=>{events.Add(e);return ValueTask.CompletedTask;}));
        var run=session.PromptAsync(Input("cancel summary"));
        try
        {
            await Within(summary.Entered.Task);Check(session.Snapshot.IsProcessingOperation&&!session.Snapshot.Agent.IsRunning,"Coordinator recovery phase missing.");
            Check(session.Abort(),"Direct abort missed between-run summary.");await Within(summary.CleanupEntered.Task);
            Check(!run.IsCompleted&&!session.WaitForIdleAsync().IsCompleted,"Cancellation abandoned owned summary cleanup.");
            summary.Release.TrySetResult();await Within(run);Equal(1,summary.Cleanups);Equal(1,transport.Requests.Count);
            Equal(0,session.Snapshot.Log.Entries.Count(e=>e.Kind==SessionEntryKind.Compaction));Equal(1,session.Snapshot.Log.Entries.Count(e=>e.Kind==SessionEntryKind.ContextEdit));
            Equal("cancelled",events.OfType<SessionOperationSettled>().Single().Status);
        }
        finally{summary.Release.TrySetResult();session.Abort();await Ignore(run);}
    }
    private static void Configure(PersistentAgentSession session,ISessionSummaryGenerator generator,double keep=80)
    {session.ConfigureAutomaticCompaction(generator,new(true,128,keep),contextWindow:100_000);session.ConfigureAutomaticRecovery(1_000);}
    private static async Task<PersistentAgentSession> Create(Files files,Clock clock,Script transport,ImmutableArray<ToolDefinition> tools=default)
    {
        var header=Codec.Parse(JsonSerializer.Serialize(new{type="session",version=3,id="recovery",timestamp="1970-01-01T00:00:00.000Z",cwd=files.Directory}));
        await using(var store=await SessionLogStore.CreateNewAsync(files.Path,header))
        {
            var first=Entry("first-user",null,Input(new string('y',8_000)));var answer=Entry("first-assistant","first-user",new("assistant",PiWireJson.WriteMessage(Message() with{Timestamp=10})));
            var a=Entry("old-user","first-assistant",Input(new string('x',8_000)));var b=Entry("old-assistant","old-user",new("assistant",PiWireJson.WriteMessage(Message() with{Timestamp=10})));
            await store.AppendAsync([first,answer,a,b]);
        }
        var session=await PersistentAgentSession.OpenAsync(files.Path,new(Model,transport,tools.IsDefault?[]:tools),clock.Next,clock.Id);
        session.Subscribe(new AgentSink((e,_)=>
        {
            if(e is AgentLoopEnded end)
            {
                var last=end.Result.Turns[^1].Result.Chat.Message;var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
                Console.WriteLine("RECOVERY-DIAGNOSTIC "+JsonSerializer.Serialize(new{end.Result.Reason,enabled=session.Snapshot.AutoCompactionEnabled,desired=typeof(PersistentAgentSession).GetField("_recoveryDesiredOutput",flags)!.GetValue(session),runtimeModel=session.Snapshot.Agent.Model,
                    overflow=SessionRecoveryClassifier.IsContextOverflow(last),length=SessionRecoveryClassifier.IsRecoverableLength(last,1_000),matches=session.Snapshot.Context.ContextEntries.Count(c=>c.Messages.Any(m=>JsonElement.DeepEquals(m.WireBody.Value,PiWireJson.WriteMessage(last).Value))),kinds=session.Snapshot.Context.Ancestry.Select(c=>c.Kind.ToString()),
                    turns=end.Result.Turns.Select(t=>new{t.Result.Chat.Failure,message=PiWireJson.WriteMessage(t.Result.Chat.Message).Value}),projected=session.Snapshot.Context.ContextEntries.Select(c=>new{c.SourceEntry.Id,messages=c.Messages.Select(m=>m.WireBody.Value)})}));
            }
            return ValueTask.CompletedTask;
        }));
        return session;
    }
    private static Task ClassifierSource()
    {
        var dir=new DirectoryInfo(AppContext.BaseDirectory);while(dir is not null&&!System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName,"fixtures","pi-v0.99.1","session-recovery")))dir=dir.Parent;
        Check(dir is not null,"Source fixture repository not found.");var root=System.IO.Path.Combine(dir!.FullName,"fixtures","pi-v0.99.1","session-recovery");
        using var manifest=JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(root,"manifest.json")));
        foreach(var row in manifest.RootElement.GetProperty("files").EnumerateArray())
        {var f=System.IO.Path.Combine(root,row.GetProperty("path").GetString()!);Equal(row.GetProperty("bytes").GetInt64(),new FileInfo(f).Length);Equal(row.GetProperty("sha256").GetString(),Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))));}
        using var source=JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(root,"classifier-source.json")));var data=source.RootElement;
        Equal("d86654abb8862e201933517d6f1fce9f88dd117f",data.GetProperty("sourceSha").GetString());Equal("9a7200aac0cce1769e3acdf01e66048fea0ee62f3a93a3dac08055c41a33e2cb",data.GetProperty("sourceFileSha256").GetString());Equal(24,data.GetProperty("sourcePatterns").GetArrayLength());Equal(65,data.GetProperty("cases").GetArrayLength());
        foreach(var row in data.GetProperty("cases").EnumerateArray())
        {var message=PiWireJson.ReadMessage(row.GetProperty("message"));Equal(row.GetProperty("sourceOverflow").GetBoolean(),SessionRecoveryClassifier.IsContextOverflow(message,row.GetProperty("contextWindow").GetDouble()));Equal(row.GetProperty("sourceRecoverableLength").GetBoolean(),SessionRecoveryClassifier.IsRecoverableLength(message,row.GetProperty("desiredMaxOutput").GetDouble()));}
        return Task.CompletedTask;
    }
    private static SessionEntry Entry(string id,string? parent,TranscriptEntry m)=>Codec.Parse(JsonSerializer.Serialize(new{type="message",id,parentId=parent,timestamp="1970-01-01T00:00:00.010Z",message=m.WireBody.Value}));
    private static TranscriptEntry Input(string text)=>new("user",JsonData.Parse(JsonSerializer.Serialize(new{role="user",content=text,timestamp=10})));
    private static AssistantMessage Message(StopReason stop=StopReason.Stop,bool tool=false)=>new(Model.Api,Model.Provider,Model.Id,0,
        tool?[new ToolCallContent("call-effect","write",JsonData.Parse("{\"path\":\"owned-fake\"}"))]:[new TextContent("provider answer")],
        new(100,1,0,0,101,new(.1m,.001m,0,0,.101m)),stop);
    private static AssistantMessage Error()=>Message(StopReason.Error) with {ExtraProperties=JsonFields.Empty.Set("errorMessage",JsonData.Parse("\"input exceeds the context window\""))};
    private static bool IsError(TranscriptEntry m)=>m.Role=="assistant"&&PiWireJson.ReadMessage(m.WireBody.Value).StopReason==StopReason.Error;
    private sealed class Clock{private long ticks=1_000_000;private int id;public long Next()=>Interlocked.Increment(ref ticks);public string Id()=>"recovery-"+Interlocked.Increment(ref id);}
    private sealed class Files:IDisposable
    {
        public string Directory{get;}=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"PiSharp-recovery-"+Guid.NewGuid().ToString("N"));public string Path=>System.IO.Path.Combine(Directory,"session.jsonl");
        public Files()=>System.IO.Directory.CreateDirectory(Directory);
        public void Dispose(){var target=System.IO.Path.GetFullPath(Directory);if(System.IO.Path.GetDirectoryName(target)!=System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()))||!System.IO.Path.GetFileName(target).StartsWith("PiSharp-recovery-",StringComparison.Ordinal))throw new InvalidOperationException("Invalid owned cleanup path.");System.IO.Directory.Delete(target,true);}
    }
    private sealed class Script(Clock clock,AssistantMessage[] schedule):IChatTransport
    {
        public List<ChatRequest> Requests{get;}=[];public int Cleanups;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request,[EnumeratorCancellation]CancellationToken token=default)
        {
            token.ThrowIfCancellationRequested();var final=schedule[Requests.Count] with{Timestamp=clock.Next()};Requests.Add(request);
            try
            {
                await Task.Yield();yield return new StreamStarted(final with{Content=[],StopReason=StopReason.Pending});
                for(var i=0;i<final.Content.Length;i++)if(final.Content[i] is ToolCallContent call){yield return new ToolCallStarted(i,call with{Arguments=JsonData.EmptyObject});yield return new ToolCallEnded(i,call);}else if(final.Content[i] is TextContent text){yield return new TextStarted(i,new(""));yield return new TextEnded(i,text.Text);}
                if(final.StopReason==StopReason.Error)yield return new StreamError(final.StopReason,final);else yield return new StreamDone(final.StopReason,final);
            }
            finally{Cleanups++;}
        }
    }
    private sealed class Summary(bool fail=false):ISessionSummaryGenerator
    {
        public int Calls;public ValueTask<SessionGeneratedSummary> GenerateAsync(SessionSummaryRequest request,CancellationToken token=default)
        {token.ThrowIfCancellationRequested();Calls++;return fail?ValueTask.FromException<SessionGeneratedSummary>(new InvalidOperationException("authored failure")):ValueTask.FromResult(new SessionGeneratedSummary("authored history summary",new(0,10,0,0,10,new(0,.2m,0,0,.2m))));}
    }
    private sealed class HeldSummary:ISessionSummaryGenerator
    {
        public TaskCompletionSource Entered=Gate(),CleanupEntered=Gate(),Release=Gate();public int Cleanups;
        public async ValueTask<SessionGeneratedSummary> GenerateAsync(SessionSummaryRequest request,CancellationToken token=default)
        {Entered.TrySetResult();try{await Task.Delay(Timeout.Infinite,token);throw new InvalidOperationException("unreachable");}finally{CleanupEntered.TrySetResult();await Release.Task;Cleanups++;}}
    }
    private sealed class Effect:IToolExecutor{public int Calls;public ValueTask<ToolResult> ExecuteAsync(ToolInvocation i,CancellationToken token){token.ThrowIfCancellationRequested();Calls++;return ValueTask.FromResult(ToolResult.Success("effect receipt"));}}
    private sealed class AgentSink(Func<AgentEvent,CancellationToken,ValueTask> callback):IAgentEventSink{public ValueTask EmitAsync(AgentEvent e,CancellationToken t)=>callback(e,t);}
    private sealed class OperationSink(Func<SessionOperationEvent,CancellationToken,ValueTask> callback):ISessionOperationEventSink{public ValueTask EmitAsync(SessionOperationEvent e,CancellationToken t)=>callback(e,t);}
    private static TaskCompletionSource Gate()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<T> Within<T>(Task<T> task)=>await task.WaitAsync(TimeSpan.FromSeconds(10));private static async Task Within(Task task)=>await task.WaitAsync(TimeSpan.FromSeconds(10));
    private static async Task Ignore(Task task){try{await Within(task);}catch(Exception){}}
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}private static void Equal<T>(T expected,T actual)=>Check(EqualityComparer<T>.Default.Equals(expected,actual),$"Expected {expected}; actual {actual}.");
    private static void Throws<T>(Action callback)where T:Exception{try{callback();}catch(T){return;}throw new InvalidOperationException("Expected "+typeof(T).Name);}
}
