using System.Collections.Concurrent;
using System.Net;
using System.Text;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Contracts;

internal static class CompletionsResponseInputTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        foreach (var test in CompletionsPreparationOwnershipTests.Cases()) yield return test;
        yield return ("completions-input.real-prefetch-partial-consumption-bounds-and-owned-enqueues", BoundedPackets);
        yield return ("completions-input.inclusive-byte-bound-invalid-count-and-observer-failure", LimitsAndFaults);
        yield return ("completions-input.noncooperative-refill-cancel-join-no-late-enqueue-and-borrowed-body", NoncooperativeRefill);
        yield return ("completions-input.actual-source-done-prefetch-tail-and-joined-physical-owner", SourceDone);
        yield return ("completions-input.actual-source-observer-and-read-faults-remain-private", SourceFaults);
        yield return ("completions-input.logical-reader-eof-does-not-skip-physical-cancel-authority", LogicalEof);
    }

    private static async Task BoundedPackets()
    {
        var observed = new List<CompletionsInputPublication>();
        using var body = new Body([1,2,3,4,5,6,7,8], 4);
        await using var input = new CompletionsResponseInput(body, 4, observed.Add);
        Check(body.Reads == 1 && input.BytesEnqueued == 4 && input.BufferedBytes == 4,
            "Construction did not perform one actual bounded enqueue.");
        var bytes = new byte[4];
        Check(await input.ReadAsync(bytes.AsMemory(0,1)) == 1 && bytes[0] == 1 && body.Reads == 1 && input.BufferedBytes == 3,
            "Partial consumption prefetched beyond the one-packet high-water mark.");
        Check(await input.ReadAsync(bytes.AsMemory(0,3)) == 3 && bytes[..3].SequenceEqual(new byte[] {2,3,4}) &&
            body.Reads == 2 && input.BytesEnqueued == 8 && input.BufferedBytes == 4,
            "Draining the packet failed to admit exactly one actual refill.");
        Check(await input.ReadAsync(bytes) == 4 && bytes.SequenceEqual(new byte[] {5,6,7,8}) && await input.ReadAsync(bytes) == 0 &&
            await input.ReadAsync(bytes) == 0 && body.Reads == 3 && input.ReachedPhysicalEof && input.BufferedBytes == 0 && input.PeakBufferedBytes == 4,
            "EOF, repeated closed reads or the inclusive queue bound changed.");
        Check(observed.Count == 3 && observed[0].Offset == 0 && observed[0].Value.SequenceEqual(new byte[] {1,2,3,4}) &&
            observed[1].Offset == 4 && observed[1].Value.SequenceEqual(new byte[] {5,6,7,8}) && observed[2].PhysicalEof && observed[2].Offset == 8,
            "Actual enqueue bytes/offsets were backfilled, mutated, duplicated or detached from EOF.");
        await input.DisposeAsync(); Check(!body.Disposed, "Input disposal took ownership of its borrowed physical body.");
    }

    private static async Task LimitsAndFaults()
    {
        foreach (var limit in new[] {1,65_536})
        {
            using var body = new Body(Enumerable.Range(0,limit).Select(x => (byte)x).ToArray(), limit);
            await using var input = new CompletionsResponseInput(body, limit);
            var bytes = new byte[limit]; Check(await input.ReadAsync(bytes) == limit && input.PeakBufferedBytes == limit,
                "The inclusive read boundary was rejected or exceeded.");
        }
        foreach (var limit in new[] {0,65_537})
        {
            using var body = new Body([1],1);
            var denied=false; try { _=new CompletionsResponseInput(body,limit); } catch(ArgumentOutOfRangeException) { denied=true; }
            Check(denied && body.Reads == 0, "Invalid admission acquired input resources.");
        }
        foreach (var observer in new[] {false,true})
        {
            using var body = new Body([1],1, invalidCount: !observer);
            var input = new CompletionsResponseInput(body,1, observer ? _ => throw new IOException("PRIVATE_INPUT_FAULT") : null);
            var failed=false; try { _=await input.ReadAsync(new byte[1]); } catch(Exception e) when(e is IOException or InvalidOperationException) { failed=true; }
            Check(failed, "Invalid count or observer failure entered successful input consumption.");
            await Observe(input.DisposeAsync().AsTask()); Check(!body.Disposed && input.BufferedBytes == 0,
                "Failed admission abandoned retained input or closed the borrowed body.");
        }
    }

    private static async Task NoncooperativeRefill()
    {
        var observed = new ConcurrentQueue<CompletionsInputPublication>();
        using var body = new Body([1,2],1, holdAt:1, ignoreCancellation:true);
        var input = new CompletionsResponseInput(body,1,observed.Enqueue); var bytes=new byte[1];
        try
        {
        Check(await input.ReadAsync(bytes) == 1 && bytes[0] == 1, "Control did not consume its real initial packet.");
        await body.ReadHeld.Task.WaitAsync(Deadline);
        var cancel=input.CancelAsync().AsTask(); var second=input.CancelAsync().AsTask();
        Check(ReferenceEquals(cancel,second) && !cancel.IsCompleted && body.Reads == 2 && observed.Count == 1 && !body.Disposed,
            "Cancellation abandoned, duplicated or disposed a noncooperative physical pull.");
        body.ReleaseRead.TrySetResult(); await Task.WhenAll(cancel,second).WaitAsync(Deadline); await input.DisposeAsync();
        Check(observed.Count == 1 && input.BytesEnqueued == 1 && input.BufferedBytes == 0 && !body.Disposed,
            "Late canceled bytes became an enqueue or crossed borrowed-body ownership.");
        }
        finally { body.ReleaseRead.TrySetResult(); await Observe(input.DisposeAsync().AsTask()); }
    }

    private static async Task SourceDone()
    {
        var prefix=Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"owned\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
        var bytes=prefix.Concat(new byte[] {(byte)'X',(byte)'Y'}).ToArray();
        using var body=new Body(bytes,1,holdAt:prefix.Length,ignoreCancellation:true,holdCleanup:true);
        using var content=new StreamProbeContent(body); using var handler=new FakeHttpHandler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=content}));
        using var client=new HttpClient(handler); var enqueues=new ConcurrentQueue<CompletionsInputPublication>(); var consumed=new ConcurrentQueue<CompletionsBodyReadResult>();
        var transport=new CompletionsHttpSseTransport(client,(_,_)=>new(HttpMethod.Post,"https://input.invalid/completions"),
            new(Framing:new(ReadBufferBytes:1,EofBehavior:PiSharp.AI.Transports.SseEofBehavior.DispatchPendingEvent){Profile=PiSharp.AI.Transports.SseFramingProfile.OpenAISdk719})
            {OnInputPublished=enqueues.Enqueue,OnBodyRead=consumed.Enqueue});
        await using var run=await transport.StartAsync(new(new("input","openai-completions","openai"),[])); var drain=Drain(run);
        try
        {
            await body.ReadHeld.Task.WaitAsync(Deadline);
            await body.ReadCancelled.Task.WaitAsync(Deadline);
            Check(!run.SourceResult.IsCompleted && !run.CanonicalCompletion.IsCompleted && !body.Disposed,
                "Normal DONE escaped the pending input cancellation join.");
            body.ReleaseRead.TrySetResult(); await body.CleanupEntered.Task.WaitAsync(Deadline);
            var source=await run.SourceResult.WaitAsync(Deadline);
            Check(consumed.SelectMany(x=>x.Value).SequenceEqual(prefix) && enqueues.SelectMany(x=>x.Value).SequenceEqual(prefix) &&
                source.Snapshot.Raw.Value.GetProperty("value").GetProperty("stopReason").GetString() == "stop" &&
                !run.CanonicalCompletion.IsCompleted && !content.Disposed,
                "Ignored late bytes entered a source DTO, enqueue, provider result or canonical authority.");
            body.ReleaseCleanup.TrySetResult(); await drain.WaitAsync(Deadline);
            Check((await run.CanonicalCompletion.WaitAsync(Deadline)).Failure is null && body.AsyncCloses == 1 && content.Disposed,
                "The input join lost the single physical cleanup owner.");
        }
        finally {body.ReleaseRead.TrySetResult();body.ReleaseCleanup.TrySetResult();run.Cancel();await run.DisposeAsync();await Observe(drain);}
    }

    private static async Task SourceFaults()
    {
        foreach(var observer in new[]{false,true})
        {
            using var body=new Body([1],1,failRead:!observer);
            using var content=new StreamProbeContent(body);using var handler=new FakeHttpHandler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=content}));using var client=new HttpClient(handler);
            var transport=new CompletionsHttpSseTransport(client,(_,_)=>new(HttpMethod.Post,"https://input.invalid/completions"),
                new(){OnInputPublished=observer ? _=>throw new IOException("PRIVATE_INPUT_FAULT") : null});
            await using var run=await transport.StartAsync(new(new("input","openai-completions","openai"),[]));var drain=Drain(run);
            await drain.WaitAsync(Deadline);var result=await run.CanonicalCompletion.WaitAsync(Deadline);
            Check(result.Failure is not null && body.AsyncCloses == 1 && content.Disposed &&
                !PiWireJson.WriteMessage(result.Message).ToString().Contains("PRIVATE_INPUT_FAULT",StringComparison.Ordinal),
                "An actual input fault gained success, leaked private text or abandoned cleanup.");
        }
    }

    private static async Task LogicalEof()
    {
        using var body=new Body([1],1,holdAt:0,holdCleanup:true);
        using var content=new StreamProbeContent(body);
        using var handler=new FakeHttpHandler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=content}));
        using var client=new HttpClient(handler); var reader=new LogicalReader(); var publications=new List<CompletionsInputPublication>();
        var transport=new CompletionsHttpSseTransport(client,(_,_)=>new(HttpMethod.Post,"https://input.invalid/completions"),
            new(){OnInputPublished=publications.Add,BodyReaderFactory=(_,_)=>ValueTask.FromResult<ICompletionsResponseBodyReader>(reader)});
        await using var run=await transport.StartAsync(new(new("input","openai-completions","openai"),[])); var drain=Drain(run);
        try
        {
            await reader.CancelEntered.Task.WaitAsync(Deadline);
            Check(reader.Reads == 1 && reader.Cancels == 1 && reader.Releases == 0 && publications.Count == 0 &&
                !run.SourceResult.IsCompleted && !run.CanonicalCompletion.IsCompleted,
                "The injected reader's real logical EOF skipped cancellation while physical input remained open.");
            reader.ReleaseCancel.TrySetResult(); await body.CleanupEntered.Task.WaitAsync(Deadline);
            Check((await run.SourceResult.WaitAsync(Deadline)).Snapshot.Raw.Value.GetProperty("value").GetProperty("stopReason").GetString() == "error" &&
                reader.Releases == 1 && !run.CanonicalCompletion.IsCompleted,
                "Logical EOF lost its admitted result or crossed held physical closure.");
            body.ReleaseCleanup.TrySetResult(); await drain.WaitAsync(Deadline);
            Check((await run.CanonicalCompletion.WaitAsync(Deadline)).Failure?.Kind == ChatFailureKind.Provider && body.AsyncCloses == 1 && content.Disposed,
                "Logical/physical EOF distinction abandoned final ownership.");
        }
        finally {reader.ReleaseCancel.TrySetResult();body.ReleaseRead.TrySetResult();body.ReleaseCleanup.TrySetResult();run.Cancel();await run.DisposeAsync();await Observe(drain);}
    }

    private sealed class LogicalReader : ICompletionsResponseBodyReader
    {
        public int Reads{get;private set;} public int Cancels{get;private set;} public int Releases{get;private set;}
        public TaskCompletionSource CancelEntered{get;}=Gate(); public TaskCompletionSource ReleaseCancel{get;}=Gate();
        public ValueTask<int> ReadAsync(Memory<byte> destination,CancellationToken token=default){Reads++;return ValueTask.FromResult(0);}
        public async ValueTask CancelAsync(){Cancels++;CancelEntered.TrySetResult();await ReleaseCancel.Task;}
        public void Release(){Releases++;}
    }

    private static async Task Drain(CompletionsRun run) {while(!(await run.NextAsync()).Done){} }
    private static async Task Observe(Task task) {try{await task.WaitAsync(Deadline);}catch(IOException){}catch(InvalidOperationException){} }
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private sealed class Body(byte[] bytes,int chunkBytes,int holdAt=-1,bool ignoreCancellation=false,bool holdCleanup=false,bool invalidCount=false,bool failRead=false):Stream
    {
        private int _offset; private Task? _closing;
        public int Reads{get;private set;}public int AsyncCloses{get;private set;}public bool Disposed{get;private set;}
        public TaskCompletionSource ReadHeld{get;}=Gate();public TaskCompletionSource ReleaseRead{get;}=Gate();
        public TaskCompletionSource ReadCancelled{get;}=Gate();
        public TaskCompletionSource CleanupEntered{get;}=Gate();public TaskCompletionSource ReleaseCleanup{get;}=Gate();
        public override bool CanRead=>!Disposed;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override async ValueTask<int> ReadAsync(Memory<byte> destination,CancellationToken token=default)
        {
            Reads++;if(_offset==holdAt)
            {
                using var registration=token.Register(()=>ReadCancelled.TrySetResult());
                ReadHeld.TrySetResult();if(ignoreCancellation)await ReleaseRead.Task;else await ReleaseRead.Task.WaitAsync(token);
            }
            if(failRead)throw new IOException("PRIVATE_INPUT_FAULT");if(invalidCount)return destination.Length+1;
            var count=Math.Min(chunkBytes,Math.Min(destination.Length,bytes.Length-_offset));bytes.AsMemory(_offset,count).CopyTo(destination);_offset+=count;return count;
        }
        public override ValueTask DisposeAsync()=>new(_closing??=CloseAsync());
        private async Task CloseAsync(){AsyncCloses++;CleanupEntered.TrySetResult();if(holdCleanup)await ReleaseCleanup.Task;Disposed=true;}
        protected override void Dispose(bool disposing){if(disposing)Disposed=true;base.Dispose(disposing);}
        public override void Flush()=>throw new NotSupportedException();public override int Read(byte[] b,int o,int c)=>throw new NotSupportedException();
        public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long l)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
    }
}
