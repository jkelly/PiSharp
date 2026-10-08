using System.Collections.Concurrent;
using System.Net;
using System.Text;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.AI.Transports;
using PiSharp.Contracts;

internal static class CompletionsResultExecutorTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static readonly ChatRequest Request = new(new("result-executor", "openai-completions", "openai"), []);
    private const string Event = "data: {\"choices\":[{\"delta\":{\"content\":\"π😀\"},\"finish_reason\":\"stop\"}]}";
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("completions-result-executor.owned-empty-data-physical-eof-logical-close-and-bounds", OwnedResults);
        yield return ("completions-result-executor.single-active-read-shared-cancel-joins-late-result-and-fault", Lifecycle);
        yield return ("completions-result-executor.empty-chunks-between-every-utf8-byte-and-done-tail-in-both-views", () => HttpChunks(true));
        yield return ("completions-result-executor.empty-chunks-do-not-dispatch-pending-sse-before-physical-eof", () => HttpChunks(false));
        yield return ("completions-result-executor.consecutive-empty-budget-and-factory-admission-before-effects", EmptyBudget);
        yield return ("completions-result-executor.invalid-result-observer-fault-and-read-fault-join-owner", HttpFaults);
        yield return ("completions-result-executor.abort-after-empty-result-retains-noncooperative-physical-join", HeldAbort);
    }

    private static async Task OwnedResults()
    {
        var bytes = new byte[] { 0, 255 }; var data = CompletionsBodyReadResult.FromBytes(bytes); bytes.AsSpan().Fill(9);
        var empty = CompletionsBodyReadResult.FromBytes([]);
        Check(data.Value.SequenceEqual(new byte[] {0,255}) && !data.Done && !data.PhysicalEof && !empty.Done && empty.Value.IsEmpty && !empty.PhysicalEof,
            "Explicit results borrowed mutable bytes or merged empty data with EOF.");
        Check(empty.Snapshot.ToString() == "{\"value\":{\"value\":{},\"done\":false},\"ownUndefinedPaths\":[]}" &&
            CompletionsBodyReadResult.EndOfInput.Done && CompletionsBodyReadResult.EndOfInput.PhysicalEof &&
            CompletionsBodyReadResult.Closed.Done && !CompletionsBodyReadResult.Closed.PhysicalEof &&
            CompletionsBodyReadResult.Closed.Snapshot.ToString() == CompletionsBodyReadResult.EndOfInput.Snapshot.ToString(),
            "Empty value/closed own-undefined presence or physical EOF authority changed.");
        foreach (var physical in new[] {true,false})
        {
            var reads = 0;
            var executor = new Executor((_,_) => ValueTask.FromResult(++reads == 1 ? empty : reads == 2 ? data :
                physical ? CompletionsBodyReadResult.EndOfInput : CompletionsBodyReadResult.Closed));
            var reader = CompletionsBodyReader.FromResultReader(executor,2);
            Check(ReferenceEquals(await reader.ReadAsync(),empty) && !reader.ReachedEof && ReferenceEquals(await reader.ReadAsync(),data),
                "The bounded reader changed or ended an actual nonterminal result.");
            var closed = await reader.ReadAsync(); Check(closed.Done && reader.ReachedEof == physical &&
                ReferenceEquals(closed,await reader.ReadAsync()) && executor.Reads == 3, "Repeated closure acquired another executor result.");
            if (!physical) await reader.CancelAsync(); reader.Release();
            Check(executor.Cancels == (physical ? 0 : 1) && executor.Releases == 1, "Logical closure skipped cancel or natural EOF fabricated one.");
        }
        foreach (var maximum in new[] {1,65_536})
        {
            var executor = new Executor((limit,_) => { Check(limit == maximum,"The actual result executor received the wrong bound.");
                return ValueTask.FromResult(CompletionsBodyReadResult.FromBytes(new byte[maximum])); });
            var reader = CompletionsBodyReader.FromResultReader(executor,maximum);
            Check((await reader.ReadAsync()).Value.Length == maximum, "Inclusive result size was rejected."); reader.Release();
        }
        Denied<ArgumentOutOfRangeException>(() => CompletionsBodyReadResult.FromBytes(new byte[65_537]));
        foreach (var maximum in new[] {0,65_537}) Denied<ArgumentOutOfRangeException>(() =>
            CompletionsBodyReader.FromResultReader(new Executor((_,_)=>ValueTask.FromResult(empty)),maximum));
    }

    private static async Task Lifecycle()
    {
        foreach (var fail in new[] {false,true})
        {
            var entered=Gate(); var release=Gate(); CompletionsBodyReader? reader=null;
            var executor=new Executor(async (_,_)=>
            {
                Denied<InvalidOperationException>(()=>reader!.CancelAsync()); Denied<InvalidOperationException>(()=>reader!.Release());
                entered.TrySetResult(); await release.Task;
                if(fail)throw new IOException("PRIVATE_RESULT_READ"); return CompletionsBodyReadResult.FromBytes([42]);
            });
            reader=CompletionsBodyReader.FromResultReader(executor,1); var pending=reader.ReadAsync().AsTask();
            try
            {
                await entered.Task.WaitAsync(Deadline); await Throws<InvalidOperationException>(()=>reader.ReadAsync().AsTask());
                var cancel=reader.CancelAsync().AsTask(); var again=reader.CancelAsync().AsTask();
                Check(ReferenceEquals(cancel,again) && !cancel.IsCompleted && executor.Reads == 1 && executor.Cancels == 1,
                    "Explicit result cancellation abandoned the retained read or duplicated its executor operation.");
                Denied<InvalidOperationException>(reader.Release); release.TrySetResult();
                if(fail) { await Throws<IOException>(()=>pending); await Throws<IOException>(()=>cancel); }
                else { Check((await pending.WaitAsync(Deadline)).Value.Single() == 42,"A real noncooperative result was fabricated or changed."); await cancel.WaitAsync(Deadline); }
                var closed=await reader.ReadAsync(); Check(closed.Done && !closed.PhysicalEof && !reader.ReachedEof,"Canceled result granted physical EOF authority.");
                reader.Release(); Check(executor.Releases == 1,"Result executor release was not single-owner.");
            }
            finally {release.TrySetResult();await Observe(pending);if(!reader.Released){await Observe(reader.CancelAsync().AsTask());reader.Release();}}
        }
        using var stop=new CancellationTokenSource(); var waiting=Gate();
        var cooperative=new Executor(async (_,token)=>{waiting.TrySetResult();await Task.Delay(Timeout.Infinite,token);return CompletionsBodyReadResult.EndOfInput;});
        var owner=CompletionsBodyReader.FromResultReader(cooperative,1);var read=owner.ReadAsync(stop.Token).AsTask();await waiting.Task.WaitAsync(Deadline);stop.Cancel();
        Check((await read.WaitAsync(Deadline)).Done && !owner.ReachedEof,"Cooperative result cancellation became physical EOF.");
        await owner.CancelAsync();owner.Release();
    }

    private static async Task HttpChunks(bool done)
    {
        foreach(var source in new[]{false,true})
        {
            // The retained CRLF-DONE diagnostic exposed a separately coordinated
            // shared decoder boundary. These vectors qualify explicit empty chunks.
            var prefix=Encoding.UTF8.GetBytes(done ? Event+"\n\ndata: [DONE]\n\n" : Event+"\r\n");
            using var body=new Body(done ? prefix.Concat(Encoding.UTF8.GetBytes("TAIL_MUST_NOT_DECODE")).ToArray() : prefix);
            using var content=new StreamProbeContent(body);using var handler=Handler(content);using var client=new HttpClient(handler);
            InterleavedReader? executor=null;var results=new ConcurrentQueue<CompletionsBodyReadResult>();
            var transport=Transport(client,new(Framing:Framing())
            {MaximumConsecutiveEmptyBodyReads=2,OnBodyRead=results.Enqueue,BodyResultReaderFactory=(stream,_)=>
                ValueTask.FromResult<ICompletionsResponseBodyResultReader>(executor=new InterleavedReader(stream,2))});
            if(source)
            {
                await using var run=await transport.StartAsync(Request);await Drain(run).WaitAsync(Deadline);
                var result=await run.CanonicalCompletion.WaitAsync(Deadline);
                Check(result.Failure is null && result.Message.Content.OfType<TextContent>().Single().Text == "π😀", "Source empty chunks flushed UTF-8, ended early or changed output.");
            }
            else
            {
                var frames=await Collect(transport.StreamAsync(Request));
                Check(frames[^1] is StreamDone end && end.Message.Content.OfType<TextContent>().Single().Text == "π😀",
                    "Canonical empty chunks flushed UTF-8, ended early or changed output.");
            }
            Check(results.Any(x=>!x.Done && x.Value.IsEmpty) && results.SelectMany(x=>x.Value).SequenceEqual(prefix) &&
                results.Count(x=>x.Done) == (done ? 0 : 1) && results.Last().PhysicalEof == !done &&
                executor!.Cancels == (done ? 1 : 0) && executor.Releases == 1 && body.AsyncCloses == 1 && content.DisposeCalls == 1 && !handler.Disposed,
                "Empty/DONE/EOF results lost actual bytes, consumed tail, duplicated cleanup or took borrowed-client ownership.");
            foreach(var empty in results.Where(x=>!x.Done && x.Value.IsEmpty)) Check(empty.Snapshot.Value.GetProperty("ownUndefinedPaths").GetArrayLength()==0,
                "An empty nonterminal byte chunk acquired absent-value presence.");
        }
    }

    private static async Task EmptyBudget()
    {
        foreach(var empties in new[]{2,3})
        {
            using var body=new Body(Encoding.UTF8.GetBytes(Event+"\n\ndata: [DONE]\n\n"));using var content=new StreamProbeContent(body);
            using var handler=Handler(content);using var client=new HttpClient(handler);InterleavedReader? executor=null;var observed=0;
            var transport=Transport(client,new(){MaximumConsecutiveEmptyBodyReads=2,OnBodyRead=_=>observed++,BodyResultReaderFactory=(stream,_)=>
                ValueTask.FromResult<ICompletionsResponseBodyResultReader>(executor=new InterleavedReader(stream,empties))});
            await using var run=await transport.StartAsync(Request);await Drain(run).WaitAsync(Deadline);var result=await run.CanonicalCompletion;
            Check((result.Failure is null)==(empties==2) && executor!.Cancels == 1 && executor.Releases == 1 && content.DisposeCalls == 1 &&
                (empties==2 || observed == 3),"Consecutive-empty admission exceeded its bound, rejected the inclusive limit or lost cleanup.");
        }
        using var deniedHandler=new FakeHttpHandler((_,_)=>throw new InvalidOperationException("Invalid options must not send."));using var deniedClient=new HttpClient(deniedHandler);
        foreach(var limit in new[]{0,65_537})Denied<ArgumentOutOfRangeException>(()=>Transport(deniedClient,new(){MaximumConsecutiveEmptyBodyReads=limit}));
        _=Transport(deniedClient,new(){MaximumConsecutiveEmptyBodyReads=1});_=Transport(deniedClient,new(){MaximumConsecutiveEmptyBodyReads=65_536});
        Denied<ArgumentException>(()=>Transport(deniedClient,new(){BodyReaderFactory=(stream,_)=>ValueTask.FromResult(CompletionsResponseBodyReader.FromStream(stream)),
            BodyResultReaderFactory=(_,_)=>ValueTask.FromResult<ICompletionsResponseBodyResultReader>(new Executor((_,_)=>ValueTask.FromResult(CompletionsBodyReadResult.EndOfInput)))}));
        Check(deniedHandler.SendCalls==0,"Invalid result-factory admission performed an HTTP effect.");
    }

    private static async Task HttpFaults()
    {
        foreach(var stage in new[]{"null","oversize","read","observer"})
        {
            using var body=new Body(Encoding.UTF8.GetBytes(Event));using var content=new StreamProbeContent(body);using var handler=Handler(content);using var client=new HttpClient(handler);
            var executor=new Executor((_,_)=>stage=="read" ? ValueTask.FromException<CompletionsBodyReadResult>(new IOException("PRIVATE_RESULT_READ")) :
                ValueTask.FromResult(stage=="null" ? null! : CompletionsBodyReadResult.FromBytes(stage=="oversize" ? new byte[2] : [])));
            var observed=0;var transport=Transport(client,new(Framing:Framing()){BodyResultReaderFactory=(_,_)=>ValueTask.FromResult<ICompletionsResponseBodyResultReader>(executor),
                OnBodyRead=_=>{observed++;throw new IOException("PRIVATE_RESULT_OBSERVER");}});
            await using var run=await transport.StartAsync(Request);await Drain(run).WaitAsync(Deadline);var result=await run.CanonicalCompletion;
            Check(result.Failure is not null && executor.Reads==1 && executor.Cancels==1 && executor.Releases==1 && observed==(stage=="observer" ? 1 : 0) &&
                body.AsyncCloses==1 && content.DisposeCalls==1 && !PiWireJson.WriteMessage(result.Message).ToString().Contains("PRIVATE_RESULT",StringComparison.Ordinal),
                "An invalid/failed actual result reached decoder success, leaked private text or abandoned its response.");
        }
    }

    private static async Task HeldAbort()
    {
        using var body=new Body(Encoding.UTF8.GetBytes(Event),holdRead:true,holdClose:true);using var content=new StreamProbeContent(body);
        using var handler=Handler(content);using var client=new HttpClient(handler);InterleavedReader? executor=null;var results=new ConcurrentQueue<CompletionsBodyReadResult>();
        var transport=Transport(client,new(){OnBodyRead=results.Enqueue,BodyResultReaderFactory=(stream,_)=>
            ValueTask.FromResult<ICompletionsResponseBodyResultReader>(executor=new InterleavedReader(stream,1))});
        await using var run=await transport.StartAsync(Request);
        try
        {
            await body.ReadEntered.Task.WaitAsync(Deadline);
            await executor!.PhysicalReadEntered.Task.WaitAsync(Deadline);run.Cancel();await body.ReadCancelled.Task.WaitAsync(Deadline);
            var source=await run.SourceResult.WaitAsync(Deadline);
            Check(source.Snapshot.Raw.Value.GetProperty("value").GetProperty("stopReason").GetString()=="aborted" &&
                results.Any(x=>!x.Done && x.Value.IsEmpty) && !run.CanonicalCompletion.IsCompleted && !run.CleanupCompletion.IsCompleted &&
                !content.Disposed && body.ActiveReads==1 && body.AsyncCloses==0 && executor.Releases==1,
                "Logical source release after an empty chunk skipped physical cancellation or granted canonical authority.");
            body.ReleaseRead.TrySetResult();await body.CloseEntered.Task.WaitAsync(Deadline);
            Check(body.ActiveReads==0 && !body.CloseDuringRead && !run.CanonicalCompletion.IsCompleted && executor.Releases==1 &&
                results.Where(x=>x.Done).All(x=>!x.PhysicalEof),"Held empty-result abort invented physical EOF or disposed an active read.");
            body.ReleaseClose.TrySetResult();await run.DisposeAsync().AsTask().WaitAsync(Deadline);
            Check((await run.CanonicalCompletion).Failure?.Kind==ChatFailureKind.Cancelled && body.AsyncCloses==1 && content.DisposeCalls==1,
                "Empty-result abort lost its final joined ownership.");
        }
        finally {body.ReleaseRead.TrySetResult();body.ReleaseClose.TrySetResult();run.Cancel();await run.DisposeAsync();}
    }

    private static SseDecoderOptions Framing()=>new(ReadBufferBytes:1,EofBehavior:SseEofBehavior.DispatchPendingEvent){Profile=SseFramingProfile.OpenAISdk719};
    private static CompletionsHttpSseTransport Transport(HttpClient client,CompletionsHttpSseOptions options)=>new(client,(_,_)=>new(HttpMethod.Post,"https://result.invalid/completions"),options);
    private static FakeHttpHandler Handler(HttpContent content)=>new((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=content}));
    private static async Task Drain(CompletionsRun run){while(!(await run.NextAsync()).Done){}}
    private static async Task<List<StreamEvent>> Collect(IAsyncEnumerable<StreamEvent> source){var frames=new List<StreamEvent>();await foreach(var frame in source)frames.Add(frame);return frames;}
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static void Denied<T>(Action action)where T:Exception{try{action();}catch(T){return;}throw new InvalidOperationException("Expected "+typeof(T).Name);}
    private static async Task Throws<T>(Func<Task> action)where T:Exception{try{await action().WaitAsync(Deadline);}catch(T){return;}throw new InvalidOperationException("Expected "+typeof(T).Name);}
    private static async Task Observe(Task operation){try{await operation.WaitAsync(Deadline);}catch(IOException){}catch(InvalidOperationException){}}
    private sealed class Executor(Func<int,CancellationToken,ValueTask<CompletionsBodyReadResult>> read):ICompletionsResponseBodyResultReader
    {
        public int Reads{get;private set;}public int Cancels{get;private set;}public int Releases{get;private set;}
        public ValueTask<CompletionsBodyReadResult> ReadAsync(int maximumBytes,CancellationToken token=default){Reads++;return read(maximumBytes,token);}
        public ValueTask CancelAsync(){Cancels++;return ValueTask.CompletedTask;}public void Release(){Releases++;}
    }
    private sealed class InterleavedReader(Stream body,int empties):ICompletionsResponseBodyResultReader
    {
        private readonly ICompletionsResponseBodyReader _physical=CompletionsResponseBodyReader.FromStream(body);private readonly int _empties=empties;private int _empty=empties;
        public int Cancels{get;private set;}public int Releases{get;private set;}public TaskCompletionSource PhysicalReadEntered{get;}=Gate();
        public async ValueTask<CompletionsBodyReadResult> ReadAsync(int maximumBytes,CancellationToken token=default)
        {
            token.ThrowIfCancellationRequested();if(_empty-- > 0)return CompletionsBodyReadResult.FromBytes([]);
            PhysicalReadEntered.TrySetResult();var bytes=new byte[maximumBytes];var count=await _physical.ReadAsync(bytes,token);_empty=_empties;
            return count==0 ? CompletionsBodyReadResult.EndOfInput : CompletionsBodyReadResult.FromBytes(bytes.AsSpan(0,count));
        }
        public ValueTask CancelAsync(){Cancels++;return _physical.CancelAsync();}public void Release(){Releases++;_physical.Release();}
    }
    private sealed class Body(byte[] bytes,bool holdRead=false,bool holdClose=false):Stream
    {
        private int _offset;private Task? _closing;public int ActiveReads{get;private set;}public int AsyncCloses{get;private set;}public bool CloseDuringRead{get;private set;}
        public TaskCompletionSource ReadEntered{get;}=Gate();public TaskCompletionSource ReleaseRead{get;}=Gate();public TaskCompletionSource ReadCancelled{get;}=Gate();
        public TaskCompletionSource CloseEntered{get;}=Gate();public TaskCompletionSource ReleaseClose{get;}=Gate();
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;public override long Length=>throw new NotSupportedException();
        public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override async ValueTask<int> ReadAsync(Memory<byte> destination,CancellationToken token=default)
        {
            ActiveReads++;try{using var registration=token.Register(()=>ReadCancelled.TrySetResult());ReadEntered.TrySetResult();if(holdRead)await ReleaseRead.Task;
                var count=Math.Min(destination.Length,bytes.Length-_offset);bytes.AsMemory(_offset,count).CopyTo(destination);_offset+=count;return count;}
            finally{ActiveReads--;}
        }
        public override ValueTask DisposeAsync()=>new(_closing??=CloseAsync());
        private async Task CloseAsync(){AsyncCloses++;CloseDuringRead|=ActiveReads!=0;CloseEntered.TrySetResult();if(holdClose)await ReleaseClose.Task;}
        protected override void Dispose(bool disposing){if(disposing)CloseDuringRead|=ActiveReads!=0;base.Dispose(disposing);}
        public override void Flush()=>throw new NotSupportedException();public override int Read(byte[] bytes,int offset,int count)=>throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();
        public override void Write(byte[] bytes,int offset,int count)=>throw new NotSupportedException();
    }
}
