using System.Collections.Concurrent;
using System.Net;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Contracts;

internal static class CompletionsAbortOrderingTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static readonly ChatRequest Request = new(new("abort-order", "openai-completions", "openai"), []);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("completions-abort-order.source-release-precedes-error-with-held-read-and-cancel", () => SourceAbort(false, false));
        yield return ("completions-abort-order.rejected-cancel-remains-owned-after-source-release", () => SourceAbort(true, false));
        yield return ("completions-abort-order.release-fault-is-private-and-cleanup-stays-held", () => SourceAbort(false, true));
        yield return ("completions-abort-order.noncooperative-custom-reader-refuses-early-release-and-is-joined", RefusingReader);
        yield return ("completions-abort-order.canonical-reader-retains-joined-release-policy", CanonicalAbort);
    }

    private static async Task SourceAbort(bool failCancel, bool failRelease)
    {
        using var fixture = new Fixture(failCancel, failRelease);
        using var caller = new CancellationTokenSource();
        await using var run = await fixture.Transport.StartAsync(Request, caller.Token);
        var draining = Drain(run);
        try
        {
            await fixture.Body.ReadEntered.Task.WaitAsync(Deadline);
            await fixture.ReaderEntered.Task.WaitAsync(Deadline);
            caller.Cancel(); await fixture.CancelEntered.Task.WaitAsync(Deadline);
            var source = await run.SourceResult.WaitAsync(Deadline);
            await draining.WaitAsync(Deadline);
            var trace = fixture.Trace.ToArray();
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = 1, probe = "held-source-abort", failCancel, failRelease,
                trace, sourceSettled = run.SourceResult.IsCompleted, cleanupSettled = run.CleanupCompletion.IsCompleted, canonicalSettled = run.CanonicalCompletion.IsCompleted,
                successfulReaderReleases = fixture.Reader!.Releases, activePhysicalReads = fixture.Body.ActiveReads, responseDisposed = fixture.Content.Disposed }));
            Check(fixture.Reader!.Releases == 1 && Array.IndexOf(trace, "release") < Array.IndexOf(trace, "emit:error"),
                "Source Error was published before the actual prefetched-input reader release.");
            Check(source.Snapshot.Raw.Value.GetProperty("value").GetProperty("stopReason").GetString() == "aborted" &&
                !run.CleanupCompletion.IsCompleted && !run.CanonicalCompletion.IsCompleted && !fixture.Content.Disposed &&
                fixture.Body.ActiveReads == 1 && fixture.Body.AsyncCloses == 0,
                "Source reader release abandoned or granted authority over held physical read/cancel work.");
            PrivateAbsent(source.Snapshot.Raw.ToString());
            var closed = false;
            try { await fixture.Reader.ReadAsync(new byte[1]); } catch (InvalidOperationException) { closed = true; }
            Check(closed, "The released source lease admitted a new read while its prior work remained owned.");
            fixture.Body.ReleaseRead.TrySetResult(); fixture.ReleaseCancel.TrySetResult();
            await fixture.Body.CloseEntered.Task.WaitAsync(Deadline);
            Check(!run.CanonicalCompletion.IsCompleted && fixture.Body.ActiveReads == 0 && !fixture.Body.CloseDuringRead,
                "Physical cleanup ran during an active read or granted completion before its own join.");
            fixture.Body.ReleaseClose.TrySetResult();
            var canonical = await run.CanonicalCompletion.WaitAsync(Deadline);
            Check(canonical.Failure?.Kind == ChatFailureKind.Cancelled && canonical.NativeDiagnostic?.Code == NativeChatFailureCode.Cancelled &&
                (canonical.NativeCleanupDiagnostic?.Code == NativeChatFailureCode.CleanupFailed) == (failCancel || failRelease) &&
                fixture.Reader.Cancels == 1 && fixture.Reader.Releases == 1 && fixture.Body.AsyncCloses == 1 && fixture.Content.Disposed && !fixture.Handler.Disposed,
                "Joined abort lost cancellation, separate cleanup failure, single ownership or borrowed-client policy.");
            PrivateAbsent(PiWireJson.WriteMessage(canonical.Message).ToString());
        }
        finally { fixture.Release(); run.Cancel(); await run.DisposeAsync(); await draining; }
    }

    private static async Task RefusingReader()
    {
        using var fixture = new Fixture(refuseActiveRelease: true);
        using var caller = new CancellationTokenSource();
        await using var run = await fixture.Transport.StartAsync(Request, caller.Token);
        var draining = Drain(run);
        try
        {
            await fixture.ReaderEntered.Task.WaitAsync(Deadline); caller.Cancel();
            var source = await run.SourceResult.WaitAsync(Deadline);
            Check(source.Snapshot.Raw.Value.GetProperty("value").GetProperty("stopReason").GetString() == "aborted" && fixture.Reader!.Releases == 0 &&
                !run.CanonicalCompletion.IsCompleted && !fixture.Content.Disposed,
                "A noncooperative executor was force-released, abandoned, or prevented independent source abort.");
            fixture.ReleaseExecutor.TrySetResult(); fixture.Body.ReleaseRead.TrySetResult(); fixture.ReleaseCancel.TrySetResult();
            await fixture.Body.CloseEntered.Task.WaitAsync(Deadline); fixture.Body.ReleaseClose.TrySetResult();
            var result = await run.CanonicalCompletion.WaitAsync(Deadline);
            Check(result.Failure?.Kind == ChatFailureKind.Cancelled && fixture.Reader!.Releases == 1 && fixture.Body.AsyncCloses == 1 && !fixture.Body.CloseDuringRead,
                "A deferred executor release was lost or physical disposal overlapped its held read.");
            await draining.WaitAsync(Deadline);
        }
        finally { fixture.Release(); run.Cancel(); await run.DisposeAsync(); await draining; }
    }

    private static async Task CanonicalAbort()
    {
        using var fixture = new Fixture(); using var caller = new CancellationTokenSource();
        await using var run = await new ChatClient(fixture.Transport).StartAsync(Request, caller.Token);
        var frames = new ConcurrentQueue<StreamEvent>(); var draining = Consume();
        try
        {
            await fixture.ReaderEntered.Task.WaitAsync(Deadline); caller.Cancel(); await fixture.CancelEntered.Task.WaitAsync(Deadline);
            Check(fixture.Reader!.Releases == 0 && !run.Completion.IsCompleted && !frames.OfType<StreamTerminalEvent>().Any(),
                "Canonical physical-body release or terminal escaped its joined read/cancellation policy.");
            fixture.Body.ReleaseRead.TrySetResult(); fixture.ReleaseCancel.TrySetResult();
            await fixture.Body.CloseEntered.Task.WaitAsync(Deadline);
            Check(fixture.Reader.Releases == 1 && !run.Completion.IsCompleted && !fixture.Body.CloseDuringRead,
                "Canonical lease release did not join its physical read or escaped held body disposal.");
            fixture.Body.ReleaseClose.TrySetResult(); await draining.WaitAsync(Deadline);
            Check((await run.Completion).Failure?.Kind == ChatFailureKind.Cancelled && frames.OfType<StreamError>().Single().Reason == StopReason.Aborted,
                "Canonical abort changed terminal/result cancellation consistency.");
        }
        finally { fixture.Release(); run.Cancel(); await run.DisposeAsync(); await draining; }
        async Task Consume() { await foreach (var frame in run.ReadEventsAsync()) frames.Enqueue(frame); }
    }

    private static async Task Drain(CompletionsRun run) { while (!(await run.NextAsync()).Done) { } }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void PrivateAbsent(string value) => Check(!value.Contains("PRIVATE_ABORT_ORDER", StringComparison.Ordinal), "Private cleanup data escaped.");
    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _client;
        public HeldBody Body { get; } = new();
        public StreamProbeContent Content { get; }
        public FakeHttpHandler Handler { get; }
        public CompletionsHttpSseTransport Transport { get; }
        public Reader? Reader;
        public ConcurrentQueue<string> Trace { get; } = new();
        public readonly TaskCompletionSource ReaderEntered = Gate(), CancelEntered = Gate(), ReleaseCancel = Gate(), ReleaseExecutor = Gate();
        public Fixture(bool failCancel = false, bool failRelease = false, bool refuseActiveRelease = false)
        {
            Content = new(Body); Handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = Content })); _client = new(Handler);
            Transport = new(_client, (_, _) => new(HttpMethod.Post, "https://abort-order.invalid/v1/chat/completions"), new() {
                BodyReaderFactory = (body, _) => ValueTask.FromResult<ICompletionsResponseBodyReader>(Reader = new(body, this, failCancel, failRelease, refuseActiveRelease)),
                Hooks = new() { OnSourcePublished = publication => Trace.Enqueue("emit:" + publication.Emission.Raw.Value.GetProperty("value").GetProperty("type").GetString()) } });
        }
        public void Release() { ReleaseExecutor.TrySetResult(); ReleaseCancel.TrySetResult(); Body.ReleaseRead.TrySetResult(); Body.ReleaseClose.TrySetResult(); }
        public void Dispose() { Release(); _client.Dispose(); Content.Dispose(); Body.Dispose(); }
    }
    private sealed class Reader(Stream body, Fixture owner, bool failCancel, bool failRelease, bool refuseActiveRelease) : ICompletionsResponseBodyReader
    {
        private readonly ICompletionsResponseBodyReader _core = CompletionsResponseBodyReader.FromStream(body);
        private bool _active;
        public int Cancels, Releases;
        public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token = default)
        {
            _active = true; owner.ReaderEntered.TrySetResult();
            try { if (refuseActiveRelease) await owner.ReleaseExecutor.Task; return await _core.ReadAsync(destination, token); }
            finally { _active = false; }
        }
        public async ValueTask CancelAsync()
        {
            Cancels++; owner.Trace.Enqueue("cancel"); owner.CancelEntered.TrySetResult();
            await _core.CancelAsync(); await owner.ReleaseCancel.Task;
            if (failCancel) throw new IOException("PRIVATE_ABORT_ORDER_CANCEL");
        }
        public void Release()
        {
            if (refuseActiveRelease && _active) throw new InvalidOperationException("Explicit executor requires its admitted read join.");
            _core.Release(); Releases++; owner.Trace.Enqueue("release");
            if (failRelease) throw new IOException("PRIVATE_ABORT_ORDER_RELEASE");
        }
    }
    private sealed class HeldBody : Stream
    {
        private Task? _close;
        public int ActiveReads, AsyncCloses;
        public bool CloseDuringRead;
        public readonly TaskCompletionSource ReadEntered = Gate(), ReleaseRead = Gate(), CloseEntered = Gate(), ReleaseClose = Gate();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token = default)
        { Interlocked.Increment(ref ActiveReads); ReadEntered.TrySetResult(); try { await ReleaseRead.Task; return 0; } finally { Interlocked.Decrement(ref ActiveReads); } }
        public override ValueTask DisposeAsync() => new(_close ??= CloseAsync());
        private async Task CloseAsync() { AsyncCloses++; CloseDuringRead |= ActiveReads != 0; CloseEntered.TrySetResult(); await ReleaseClose.Task; }
        protected override void Dispose(bool disposing) { if (disposing) CloseDuringRead |= ActiveReads != 0; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
