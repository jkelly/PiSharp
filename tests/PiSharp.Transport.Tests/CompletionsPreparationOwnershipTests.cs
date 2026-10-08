using System.Collections.Concurrent;
using System.Net;
using System.Text;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Contracts;

internal static class CompletionsPreparationOwnershipTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static readonly ChatRequest Request = new(new("preparation", "openai-completions", "openai"), []);
    private static readonly byte[] Wire = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"early\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("completions-preparation.source-enqueue-before-awaited-hook-canonical-zero-acquisition", Ordering);
        yield return ("completions-preparation.hook-rejection-retains-noncooperative-prefetch-before-physical-close", () => InterruptedHook(false, false));
        yield return ("completions-preparation.hook-cancellation-retains-prefetch-and-private-late-read-fault", () => InterruptedHook(true, true));
        yield return ("completions-preparation.canonical-hook-rejection-and-both-status-rejections-zero-acquisition", Rejections);
        yield return ("completions-preparation.early-acquisition-cancellation-retains-owner-until-operation-settles", AcquisitionCancellation);
        yield return ("completions-preparation.early-acquisition-and-prefetch-faults-join-single-owner", EarlyFaults);
    }

    private static async Task Ordering()
    {
        foreach (var source in new[] { true, false })
        {
            using var body = new Body(Wire); using var content = new StreamProbeContent(body);
            using var handler = Handler(content); using var client = new HttpClient(handler);
            var entered = Gate(); var release = Gate(); var observed = new ConcurrentQueue<CompletionsInputPublication>(); var readerCalls = 0;
            var transport = Transport(client, new()
            {
                OnInputPublished = observed.Enqueue,
                BodyReaderFactory = (stream, _) => { readerCalls++; return ValueTask.FromResult(CompletionsResponseBodyReader.FromStream(stream)); },
                Hooks = new() { OnResponse = async (metadata, model, token) =>
                {
                    Check(metadata.Status == 200 && metadata.Headers.Value.GetProperty("x-owned").GetString() == "physical" && model == Request.Model,
                        "Early preparation replaced the actual response observation.");
                    entered.TrySetResult(); await release.Task.WaitAsync(token);
                } }
            });
            CompletionsRun? run = null; Task? drain = null; var frames = new List<StreamEvent>();
            try
            {
                if (source) { run = await transport.StartAsync(Request); drain = Drain(run); }
                else drain = Collect(transport.StreamAsync(Request), frames);
                await entered.Task.WaitAsync(Deadline);
                Check(content.AcquireCalls == (source ? 1 : 0) && body.Reads == (source ? 1 : 0) && readerCalls == 0 &&
                    observed.Count == (source ? 1 : 0) && frames.Count == 0 && !content.Disposed && !body.Disposed,
                    "Response observation did not retain source-only bounded early acquisition.");
                if (source)
                    Check(observed.Single().Offset == 0 && observed.Single().Value.SequenceEqual(Wire) && !observed.Single().PhysicalEof &&
                        run!.SourceEmissions.Length == 0 && !run.SourceResult.IsCompleted, "Start escaped the hook or the early enqueue did not own actual bytes.");
                release.TrySetResult(); await drain!.WaitAsync(Deadline);
                Check(content.AcquireCalls == 1 && readerCalls == 1 && body.AsyncCloses == 1 && !body.CloseDuringRead && content.DisposeCalls == 1 && !handler.Disposed,
                    "Successful preparation duplicated or changed physical/borrowed ownership.");
                if (source) Check((await run!.CanonicalCompletion).Failure is null, "Source-only preparation lost completion.");
                else Check(frames[0] is StreamStarted && frames[^1] is StreamDone, "Canonical preparation order changed.");
            }
            finally { release.TrySetResult(); if (run is not null) { run.Cancel(); await run.DisposeAsync(); } if (drain is not null) await Observe(drain); }
        }
    }

    private static async Task InterruptedHook(bool cancel, bool lateReadFault)
    {
        using var stop = new CancellationTokenSource();
        using var body = new Body(Wire, holdRead: true, ignoreCancellation: true, holdClose: true, failRead: lateReadFault);
        using var content = new StreamProbeContent(body); using var handler = Handler(content); using var client = new HttpClient(handler);
        var entered = Gate(); var reject = Gate(); var observed = new ConcurrentQueue<CompletionsInputPublication>(); var readerCalls = 0;
        var transport = Transport(client, new()
        {
            OnInputPublished = observed.Enqueue,
            BodyReaderFactory = (stream, _) => { readerCalls++; return ValueTask.FromResult(CompletionsResponseBodyReader.FromStream(stream)); },
            Hooks = new() { OnResponse = async (_, _, token) =>
            { entered.TrySetResult(); await reject.Task.WaitAsync(token); throw new IOException("PRIVATE_HOOK_FAILURE"); } }
        });
        await using var run = await transport.StartAsync(Request, stop.Token);
        try
        {
            await entered.Task.WaitAsync(Deadline); await body.ReadEntered.Task.WaitAsync(Deadline);
            Check(content.AcquireCalls == 1 && body.ActiveReads == 1 && readerCalls == 0 && observed.IsEmpty,
                "A held early pull was not retained before response inspection.");
            if (cancel) stop.Cancel(); else reject.TrySetResult();
            var result = await run.SourceResult.WaitAsync(Deadline); await body.ReadCancelled.Task.WaitAsync(Deadline);
            Check(result.Snapshot.Raw.Value.GetProperty("value").GetProperty("stopReason").GetString() == (cancel ? "aborted" : "error") &&
                !run.SourceEmissions.Any(x => x.Raw.Value.GetProperty("value").GetProperty("type").GetString() == "start") &&
                !run.CleanupCompletion.IsCompleted && !run.CanonicalCompletion.IsCompleted && !content.Disposed && !body.Disposed && body.AsyncCloses == 0,
                "A response-hook failure crossed the source/physical ownership boundary or published Start.");
            var disposal = run.DisposeAsync().AsTask(); Check(!disposal.IsCompleted, "Run disposal abandoned the noncooperative early pull.");
            body.ReleaseRead.TrySetResult(); await body.CloseEntered.Task.WaitAsync(Deadline);
            Check(body.ActiveReads == 0 && !body.CloseDuringRead && observed.IsEmpty && !content.Disposed && !disposal.IsCompleted && !run.CanonicalCompletion.IsCompleted,
                "Late bytes/fault escaped input cancellation or bypassed held physical cleanup.");
            body.ReleaseClose.TrySetResult(); await Task.WhenAll(disposal, run.CanonicalCompletion).WaitAsync(Deadline);
            var canonical = await run.CanonicalCompletion.WaitAsync(Deadline); var serialized = PiWireJson.WriteMessage(canonical.Message).ToString();
            var cleanup = await run.CleanupCompletion.WaitAsync(Deadline);
            Check(canonical.Failure is not null && body.AsyncCloses == 1 && content.DisposeCalls == 1 && readerCalls == 0 &&
                cleanup.Succeeded == !lateReadFault && (lateReadFault ? cleanup.Failure?.Kind == ChatFailureKind.Provider : cleanup.Failure is null) &&
                !serialized.Contains("PRIVATE_HOOK_FAILURE", StringComparison.Ordinal) && !serialized.Contains("PRIVATE_EARLY_READ", StringComparison.Ordinal),
                "Interrupted early ownership duplicated cleanup, gained success or leaked exception text.");
        }
        finally { reject.TrySetResult(); body.ReleaseRead.TrySetResult(); body.ReleaseClose.TrySetResult(); run.Cancel(); await run.DisposeAsync(); }
    }

    private static async Task Rejections()
    {
        foreach (var source in new[] { true, false })
        foreach (var status in new[] { HttpStatusCode.OK, HttpStatusCode.BadRequest })
        {
            if (source && status == HttpStatusCode.OK) continue;
            using var body = new Body(Wire); using var content = new StreamProbeContent(body);
            using var handler = Handler(content, status); using var client = new HttpClient(handler);
            var hookCalls = 0; var observed = new List<CompletionsInputPublication>();
            var transport = Transport(client, new() { OnInputPublished = observed.Add, Hooks = new() { OnResponse = (_, _, _) =>
                { hookCalls++; throw new IOException("PRIVATE_REJECTED_HOOK"); } } });
            if (source)
            {
                await using var run = await transport.StartAsync(Request); await Drain(run).WaitAsync(Deadline);
                Check((await run.CanonicalCompletion).Failure?.Kind == ChatFailureKind.Provider, "Rejected source status gained success.");
            }
            else
            {
                var frames = new List<StreamEvent>(); await Collect(transport.StreamAsync(Request), frames).WaitAsync(Deadline);
                Check(!frames.OfType<StreamStarted>().Any() && frames[^1] is StreamError, "Canonical rejection changed Start order.");
            }
            Check(content.AcquireCalls == 0 && content.SerializeCalls == 0 && observed.Count == 0 && body.Reads == 0 && body.AsyncCloses == 0 &&
                content.DisposeCalls == 1 && hookCalls == (status == HttpStatusCode.OK ? 1 : 0),
                "Canonical hook/status rejection opened, prefetched or buffered the rejected body.");
        }
    }

    private static async Task AcquisitionCancellation()
    {
        foreach (var ignoreCancellation in new[] { false, true })
        {
            using var stop = new CancellationTokenSource(); using var body = new Body(Wire, holdClose: true);
            var entered = Gate(); var release = Gate(); var canceled = Gate();
            using var content = new StreamProbeContent(body, async token =>
            {
                using var registration = token.Register(() => canceled.TrySetResult()); entered.TrySetResult();
                if (ignoreCancellation) await release.Task; else await release.Task.WaitAsync(token); return body;
            });
            using var handler = Handler(content); using var client = new HttpClient(handler); var hooks = 0; var publications = 0;
            var transport = Transport(client, new() { OnInputPublished = _ => publications++, Hooks = new() { OnResponse = (_, _, _) =>
                { hooks++; return ValueTask.CompletedTask; } } });
            await using var run = await transport.StartAsync(Request, stop.Token); var drain = Drain(run);
            try
            {
                await entered.Task.WaitAsync(Deadline); stop.Cancel();
                Check(content.AcquisitionToken.IsCancellationRequested, "Early acquisition did not receive actual cancellation.");
                if (ignoreCancellation)
                {
                    await canceled.Task.WaitAsync(Deadline);
                    Check(!run.SourceResult.IsCompleted && !run.CanonicalCompletion.IsCompleted && !content.Disposed && body.AsyncCloses == 0,
                        "Early cancellation abandoned the actual pending body acquisition.");
                    release.TrySetResult(); await body.CloseEntered.Task.WaitAsync(Deadline);
                    Check((await run.SourceResult.WaitAsync(Deadline)).Snapshot.Raw.Value.GetProperty("value").GetProperty("stopReason").GetString() == "aborted" &&
                        !run.CanonicalCompletion.IsCompleted && !content.Disposed && body.Reads == 0,
                        "Canceled acquisition created a physical pull or crossed held cleanup.");
                    body.ReleaseClose.TrySetResult();
                }
                await drain.WaitAsync(Deadline);
                Check((await run.CanonicalCompletion.WaitAsync(Deadline)).Failure?.Kind == ChatFailureKind.Cancelled && hooks == 0 && publications == 0 &&
                    content.AcquireCalls == 1 && content.DisposeCalls == 1 && !body.CloseDuringRead,
                    "Early acquisition cancellation gained hooks, input publications or duplicate response ownership.");
            }
            finally { release.TrySetResult(); body.ReleaseClose.TrySetResult(); run.Cancel(); await run.DisposeAsync(); await Observe(drain); }
        }
    }

    private static async Task EarlyFaults()
    {
        foreach (var stage in new[] { "acquire", "read", "observe" })
        {
            using var body = new Body(Wire, failRead: stage == "read");
            using var content = new StreamProbeContent(body, stage == "acquire" ? _ => throw new IOException("PRIVATE_ACQUIRE") : null);
            using var handler = Handler(content); using var client = new HttpClient(handler); var hooks = 0;
            var transport = Transport(client, new() { OnInputPublished = stage == "observe" ? _ => throw new IOException("PRIVATE_OBSERVE") : null,
                Hooks = new() { OnResponse = (_, _, _) => { hooks++; return ValueTask.CompletedTask; } } });
            await using var run = await transport.StartAsync(Request); await Drain(run).WaitAsync(Deadline);
            var result = await run.CanonicalCompletion.WaitAsync(Deadline);
            Check(result.Failure?.Kind == ChatFailureKind.Provider && content.AcquireCalls == 1 && content.DisposeCalls == 1 &&
                body.AsyncCloses == (stage == "acquire" ? 0 : 1) && hooks == (stage == "acquire" ? 0 : 1) && !body.CloseDuringRead &&
                !PiWireJson.WriteMessage(result.Message).ToString().Contains("PRIVATE_", StringComparison.Ordinal),
                "An early acquisition/prefetch fault lost its owner, succeeded or leaked private text.");
        }
    }

    private static CompletionsHttpSseTransport Transport(HttpClient client, CompletionsHttpSseOptions options) =>
        new(client, (_, _) => new(HttpMethod.Post, "https://preparation.invalid/completions"), options);
    private static FakeHttpHandler Handler(HttpContent content, HttpStatusCode status = HttpStatusCode.OK) =>
        new((_, _) => { var response = new HttpResponseMessage(status) { Content = content }; response.Headers.Add("X-Owned", "physical"); return Task.FromResult(response); });
    private static async Task Drain(CompletionsRun run) { while (!(await run.NextAsync()).Done) { } }
    private static async Task Collect(IAsyncEnumerable<StreamEvent> source, List<StreamEvent> frames) { await foreach (var frame in source) frames.Add(frame); }
    private static async Task Observe(Task task) { try { await task.WaitAsync(Deadline); } catch (IOException) { } catch (InvalidOperationException) { } }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Body(byte[] bytes, bool holdRead = false, bool ignoreCancellation = false, bool holdClose = false, bool failRead = false) : Stream
    {
        private int _offset; private Task? _closing;
        public int Reads { get; private set; } public int ActiveReads { get; private set; } public int AsyncCloses { get; private set; }
        public bool Disposed { get; private set; } public bool CloseDuringRead { get; private set; }
        public TaskCompletionSource ReadEntered { get; } = Gate(); public TaskCompletionSource ReleaseRead { get; } = Gate();
        public TaskCompletionSource ReadCancelled { get; } = Gate(); public TaskCompletionSource CloseEntered { get; } = Gate();
        public TaskCompletionSource ReleaseClose { get; } = Gate();
        public override bool CanRead => !Disposed; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token = default)
        {
            Reads++; ActiveReads++;
            try
            {
                using var registration = token.Register(() => ReadCancelled.TrySetResult()); ReadEntered.TrySetResult();
                if (holdRead) { if (ignoreCancellation) await ReleaseRead.Task; else await ReleaseRead.Task.WaitAsync(token); }
                if (failRead) throw new IOException("PRIVATE_EARLY_READ");
                var count = Math.Min(destination.Length, bytes.Length - _offset); bytes.AsMemory(_offset, count).CopyTo(destination); _offset += count; return count;
            }
            finally { ActiveReads--; }
        }
        public override ValueTask DisposeAsync() => new(_closing ??= CloseAsync());
        private async Task CloseAsync() { AsyncCloses++; CloseDuringRead |= ActiveReads != 0; CloseEntered.TrySetResult(); if (holdClose) await ReleaseClose.Task; Disposed = true; }
        protected override void Dispose(bool disposing) { if (disposing) { CloseDuringRead |= ActiveReads != 0; Disposed = true; } base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException(); public override int Read(byte[] bytes, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] bytes, int offset, int count) => throw new NotSupportedException();
    }
}
