using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using PiSharp.AI;
using PiSharp.AI.Providers;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Contracts;

internal static class DetachedChatReaderTests
{
    private static readonly ModelDescriptor Model = new("detached-model", "openai-completions", "openai");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private const string TextChunk = "{ \"error\": -0, \"opaque\":1.0, \"choices\":[{\"delta\":{\"content\":\"\\u03c0\\ud83d\\ude00\"}}] }";
    private const string FinishChunk = "{\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}";

    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("detached-reader.default-start-retains-cancel-on-reader-return", DefaultControl);
        yield return ("detached-reader.actual-http-continues-and-completion-joins-owned-cleanup", HttpContinuation);
        yield return ("detached-reader.full-channel-handoff-releases-blocked-provider-with-bounded-drain", BoundedHandoff);
        yield return ("detached-reader.explicit-reader-cancellation-joins-held-cleanup", ReaderCancellation);
        yield return ("detached-reader.caller-cancellation-after-detachment-and-shared-disposal", CallerCancellation);
        yield return ("detached-reader.throwing-cancellation-callback-and-noncooperative-cleanup-join", ThrowingCancellation);
        yield return ("detached-reader.reader-exclusivity-and-completed-run-buffer-ownership", ReaderExclusivity);
        yield return ("detached-reader.actual-callback-and-cleanup-faults-retain-consistent-error", HttpFailures);
    }

    private static async Task DefaultControl()
    {
        var source = new OwnedProvider(gateRead: true);
        await using var run = await new ChatClient(source, capacity: 1).StartAsync(Request());
        var reader = run.ReadEventsAsync().GetAsyncEnumerator();
        try
        {
            await ReadStart(reader); await source.ReadEntered.Task.WaitAsync(Deadline);
            await reader.DisposeAsync().AsTask().WaitAsync(Deadline);
            var result = await run.Completion.WaitAsync(Deadline);
            Check(result.Failure?.Kind == ChatFailureKind.Cancelled && result.Message.StopReason == StopReason.Aborted &&
                source.WorkToken.IsCancellationRequested && source.DeltasProduced == 0 && source.Closes == 1,
                "The existing StartAsync reader-return cancellation contract changed.");
        }
        finally { await Close(run, reader, source); }
    }

    private static async Task HttpContinuation()
    {
        using var fixture = new HttpFixture();
        await using var run = await new ChatClient(fixture.Observed, capacity: 1).StartWithDetachedReaderAsync(Request());
        var reader = run.ReadEventsAsync().GetAsyncEnumerator();
        try
        {
            await ReadStart(reader); await fixture.Body.ReadEntered.Task.WaitAsync(Deadline);
            await reader.DisposeAsync().AsTask().WaitAsync(Deadline);
            Check(!fixture.Content.AcquisitionToken.IsCancellationRequested && fixture.ProviderCalls == 0 && !run.Completion.IsCompleted,
                "Returning the opted-in reader canceled or completed the actual HTTP producer.");
            fixture.Body.ReleaseRead.TrySetResult(); await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline);
            Check(fixture.ProviderCalls == 2 && fixture.Dto!.ToString() == TextChunk && !run.Completion.IsCompleted &&
                fixture.Observed.Terminal is null && !fixture.Content.Disposed,
                "Detached progress changed raw DTO ownership or allowed final settlement before body cleanup.");
            fixture.Body.ReleaseCleanup.TrySetResult(); var result = await run.Completion.WaitAsync(Deadline);
            Check(result.Failure is null && result.Message.StopReason == StopReason.Stop &&
                result.Message.Content.OfType<TextContent>().Single().Text == "\u03c0\U0001f600",
                "Actual detached HTTP producer did not finish with its real text/Stop result.");
            Check(fixture.Body.AsyncCloses == 1 && !fixture.Body.SyncBeforeAsync && fixture.Content.Disposed && !fixture.Handler.Disposed,
                "Detached completion skipped owned cleanup or disposed the borrowed client.");
            EqualTerminal(fixture.Observed.Terminal, result); await fixture.CheckRequestDisposed();
        }
        finally { fixture.Release(); run.Cancel(); await reader.DisposeAsync(); await run.DisposeAsync(); }
    }

    private static async Task BoundedHandoff()
    {
        const int deltas = 1024;
        var source = new OwnedProvider(deltas: deltas, holdCleanup: true);
        await using var run = await new ChatClient(source, capacity: 1).StartWithDetachedReaderAsync(Request());
        var reader = run.ReadEventsAsync().GetAsyncEnumerator();
        try
        {
            await ReadStart(reader); await source.FirstDeltaProduced.Task.WaitAsync(Deadline);
            Check(source.DeltasProduced == 1 && !source.CleanupEntered.Task.IsCompleted,
                "Producer overtook the original capacity-one channel before reader handoff.");
            await reader.DisposeAsync().AsTask().WaitAsync(Deadline);
            await source.CleanupEntered.Task.WaitAsync(Deadline);
            Check(source.DeltasProduced == deltas && !run.Completion.IsCompleted && !source.WorkToken.IsCancellationRequested,
                "Owned handoff left a blocked write, canceled the continuing producer, or published early Completion.");
            source.ReleaseCleanup.TrySetResult(); var result = await run.Completion.WaitAsync(Deadline);
            Check(result.Failure is null && result.Message.Content.OfType<TextContent>().Single().Text == new string('x', deltas) && source.Closes == 1,
                "The real bounded discarded progress failed to produce the complete canonical result.");
            await run.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
        finally { await Close(run, reader, source); }
    }

    private static async Task ReaderCancellation()
    {
        var source = new OwnedProvider(gateRead: true, holdCleanup: true);
        using var readerCancellation = new CancellationTokenSource();
        await using var run = await new ChatClient(source, capacity: 1).StartWithDetachedReaderAsync(Request());
        var reader = run.ReadEventsAsync(readerCancellation.Token).GetAsyncEnumerator(); Task<bool>? pending = null;
        try
        {
            await ReadStart(reader); await source.ReadEntered.Task.WaitAsync(Deadline);
            pending = reader.MoveNextAsync().AsTask(); readerCancellation.Cancel();
            await source.CleanupEntered.Task.WaitAsync(Deadline);
            Check(!pending.IsCompleted && !run.Completion.IsCompleted && source.WorkToken.IsCancellationRequested,
                "Explicit reader cancellation detached or settled before its actual held cleanup joined.");
            var disposing = run.DisposeAsync().AsTask(); Check(!disposing.IsCompleted, "Run disposal failed to join the canceled producer.");
            source.ReleaseCleanup.TrySetResult();
            var canceled = false; try { _ = await pending.WaitAsync(Deadline); } catch (OperationCanceledException) { canceled = true; }
            await disposing.WaitAsync(Deadline);
            Check(canceled && (await run.Completion).Failure?.Kind == ChatFailureKind.Cancelled && source.Closes == 1,
                "Canceled reader/producer settlement lost its explicit cancellation or cleanup ownership.");
        }
        finally
        {
            source.ReleaseRead.TrySetResult(); source.ReleaseCleanup.TrySetResult(); run.Cancel();
            if (pending is not null) await Observe(pending); await reader.DisposeAsync(); await run.DisposeAsync();
        }
    }

    private static async Task CallerCancellation()
    {
        var source = new OwnedProvider(gateRead: true, holdCleanup: true);
        using var caller = new CancellationTokenSource();
        await using var run = await new ChatClient(source, capacity: 1).StartWithDetachedReaderAsync(Request(), caller.Token);
        var reader = run.ReadEventsAsync().GetAsyncEnumerator();
        try
        {
            await ReadStart(reader); await source.ReadEntered.Task.WaitAsync(Deadline); await reader.DisposeAsync(); caller.Cancel();
            await source.CleanupEntered.Task.WaitAsync(Deadline);
            var first = run.DisposeAsync().AsTask(); var second = run.DisposeAsync().AsTask();
            Check(!first.IsCompleted && !second.IsCompleted && !run.Completion.IsCompleted,
                "Caller cancellation or concurrent disposal escaped owned cleanup after detachment.");
            source.ReleaseCleanup.TrySetResult(); await Task.WhenAll(first, second).WaitAsync(Deadline);
            var result = await run.Completion;
            Check(result.Failure?.Kind == ChatFailureKind.Cancelled && result.Message.StopReason == StopReason.Aborted &&
                source.Closes == 1 && source.DeltasProduced == 0,
                "Detachment removed caller cancellation authority or duplicated physical cleanup.");
        }
        finally { await Close(run, reader, source); }
    }

    private static async Task ThrowingCancellation()
    {
        var source = new OwnedProvider(gateRead: true, holdCleanup: true, throwCancellation: true);
        await using var run = await new ChatClient(source, capacity: 1).StartWithDetachedReaderAsync(Request());
        var reader = run.ReadEventsAsync().GetAsyncEnumerator();
        try
        {
            await ReadStart(reader); await source.ReadEntered.Task.WaitAsync(Deadline); await reader.DisposeAsync(); run.Cancel();
            await source.CleanupEntered.Task.WaitAsync(Deadline);
            var first = run.DisposeAsync().AsTask(); var second = run.DisposeAsync().AsTask();
            Check(!first.IsCompleted && !second.IsCompleted && !run.Completion.IsCompleted && source.CancellationCallbacks == 1,
                "Throwing cancellation callback skipped the retained disposal/cleanup join.");
            source.ReleaseCleanup.TrySetResult(); await Task.WhenAll(first, second).WaitAsync(Deadline);
            var result = await run.Completion;
            Check(result.Failure?.Kind == ChatFailureKind.Cancelled && run.CleanupFailure?.Message == "Provider cancellation callback failed." &&
                source.Closes == 1 && !PiWireJson.WriteMessage(result.Message).ToString().Contains("private-detached-marker", StringComparison.Ordinal),
                "Detached cancellation lost its sanitized diagnostic or changed authoritative settlement.");
        }
        finally { await Close(run, reader, source); }
    }

    private static async Task ReaderExclusivity()
    {
        var source = new OwnedProvider(gateRead: true);
        await using (var run = await new ChatClient(source, capacity: 1).StartWithDetachedReaderAsync(Request()))
        {
            var reader = run.ReadEventsAsync().GetAsyncEnumerator();
            try
            {
                await ReadStart(reader); await source.ReadEntered.Task.WaitAsync(Deadline); await reader.DisposeAsync();
                await using var second = run.ReadEventsAsync().GetAsyncEnumerator(); var denied = false;
                try { _ = await second.MoveNextAsync(); } catch (InvalidOperationException) { denied = true; }
                Check(denied && !source.WorkToken.IsCancellationRequested, "Detachment admitted a second caller reader or canceled the producer.");
                source.ReleaseRead.TrySetResult(); Check((await run.Completion.WaitAsync(Deadline)).Failure is null, "Reader exclusivity disturbed actual detached completion.");
            }
            finally { await Close(run, reader, source); }
        }
        var immediate = new ImmediateProvider();
        await using (var run = await new ChatClient(immediate, capacity: 1).StartWithDetachedReaderAsync(Request()))
        {
            var result = await run.Completion.WaitAsync(Deadline);
            await using var reader = run.ReadEventsAsync().GetAsyncEnumerator(); await ReadStart(reader);
            Check(await reader.MoveNextAsync() && reader.Current is StreamDone && !await reader.MoveNextAsync(),
                "A dormant detached drain stole an already completed run's caller-owned buffered frames.");
            EqualTerminal(immediate.Terminal, result); Check(immediate.Closes == 1, "Immediate provider cleanup was not joined exactly once.");
        }
    }

    private static async Task HttpFailures()
    {
        foreach (var fault in new[] { "callback", "cleanup" })
        {
            using var fixture = new HttpFixture(failCallback: fault == "callback", failCleanup: fault == "cleanup");
            await using var run = await new ChatClient(fixture.Observed, capacity: 1).StartWithDetachedReaderAsync(Request());
            var reader = run.ReadEventsAsync().GetAsyncEnumerator();
            try
            {
                await ReadStart(reader); await fixture.Body.ReadEntered.Task.WaitAsync(Deadline); await reader.DisposeAsync(); fixture.Body.ReleaseRead.TrySetResult();
                await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline);
                Check(!run.Completion.IsCompleted && fixture.Observed.Terminal is null && !fixture.Content.Disposed,
                    "Actual detached callback/cleanup fault escaped before owned cleanup joined.");
                fixture.Body.ReleaseCleanup.TrySetResult(); var result = await run.Completion.WaitAsync(Deadline);
                Check(result.Failure?.Kind == ChatFailureKind.Provider && result.Message.StopReason == StopReason.Error &&
                    fixture.Body.AsyncCloses == 1 && fixture.Content.Disposed && !fixture.Handler.Disposed,
                    "Detached mode suppressed a real callback/cleanup fault or lost physical ownership.");
                EqualTerminal(fixture.Observed.Terminal, result);
                Check(!PiWireJson.WriteMessage(result.Message).ToString().Contains("private-detached-marker", StringComparison.Ordinal), "Private fault detail escaped native admission.");
            }
            finally { fixture.Release(); run.Cancel(); await reader.DisposeAsync(); await run.DisposeAsync(); }
        }
    }

    private sealed class OwnedProvider(int deltas = 1, bool gateRead = false, bool holdCleanup = false, bool throwCancellation = false) : IChatTransport
    {
        public TaskCompletionSource ReadEntered { get; } = Gate();
        public TaskCompletionSource ReleaseRead { get; } = Gate();
        public TaskCompletionSource FirstDeltaProduced { get; } = Gate();
        public TaskCompletionSource CleanupEntered { get; } = Gate();
        public TaskCompletionSource ReleaseCleanup { get; } = Gate();
        public CancellationToken WorkToken;
        public int DeltasProduced, Closes, CancellationCallbacks;

        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            WorkToken = token;
            using var callback = throwCancellation ? token.Register(() => { CancellationCallbacks++; throw new IOException("private-detached-marker"); }) : default;
            try
            {
                yield return new StreamStarted(Message(request, "", StopReason.Pending));
                ReadEntered.TrySetResult(); if (gateRead) await ReleaseRead.Task.WaitAsync(token);
                token.ThrowIfCancellationRequested(); yield return new TextStarted(0, new(""));
                for (var index = 0; index < deltas; index++)
                { token.ThrowIfCancellationRequested(); DeltasProduced++; FirstDeltaProduced.TrySetResult(); yield return new TextDelta(0, "x"); }
                yield return new TextEnded(0, new string('x', deltas));
            }
            finally { Closes++; CleanupEntered.TrySetResult(); if (holdCleanup) await ReleaseCleanup.Task; }
            yield return new StreamDone(StopReason.Stop, Message(request, new string('x', deltas), StopReason.Stop));
        }
    }

    private sealed class ImmediateProvider : IChatTransport
    {
        public int Closes; public StreamDone? Terminal;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            try
            {
                await Task.CompletedTask; token.ThrowIfCancellationRequested(); yield return new StreamStarted(Message(request, "", StopReason.Pending));
                Terminal = new(StopReason.Stop, Message(request, "", StopReason.Stop)); yield return Terminal;
            }
            finally { Closes++; }
        }
    }

    private sealed class RecordingTransport(IChatTransport inner) : IChatTransport
    {
        public StreamTerminalEvent? Terminal;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { await foreach (var frame in inner.StreamAsync(request, token)) { if (frame is StreamTerminalEvent terminal) Terminal = terminal; yield return frame; } }
    }

    private sealed class HttpFixture : IDisposable
    {
        public GateBody Body { get; }
        public StreamProbeContent Content { get; }
        public FakeHttpHandler Handler { get; }
        public RecordingTransport Observed { get; }
        public JsonData? Dto;
        public int ProviderCalls;
        private readonly HttpClient _client;
        private HttpRequestMessage? _request;
        public HttpFixture(bool failCallback = false, bool failCleanup = false)
        {
            Body = new(Encoding.UTF8.GetBytes("data: " + TextChunk + "\n\ndata: " + FinishChunk + "\n\ndata: [DONE]\n\n"), failCleanup);
            Content = new(Body); Handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = Content })); _client = new(Handler);
            var hooks = new CompletionsLifecycleHooks { OnProviderStreamEvent = (dto, _, _) =>
            {
                ProviderCalls++; if (dto.Value.TryGetProperty("opaque", out _)) Dto = dto;
                if (failCallback) throw new IOException("private-detached-marker"); return ValueTask.CompletedTask;
            } };
            var factory = new CompletionsKeyAuthRequestFactory(new("https://owned-detached.invalid/v1/chat/completions"), Model);
            Observed = new(CompletionsHttpSseTransport.FromAsyncRequestFactory(_client, async (request, token) =>
                _request = await factory.CreateAsync(request, "authored-inert-noncredential", hooks, token), new() { Hooks = hooks }, CompletionsSourceEventProjection.CaptureOwnedSnapshots()));
        }
        public void Release() { Body.ReleaseRead.TrySetResult(); Body.ReleaseCleanup.TrySetResult(); }
        public async Task CheckRequestDisposed()
        {
            var request = _request ?? throw new InvalidOperationException("Actual owned request was not constructed."); var disposed = false;
            try { _ = await request.Content!.ReadAsByteArrayAsync(); } catch (ObjectDisposedException) { disposed = true; }
            Check(disposed, "Detached producer retained its owned HTTP request after settlement.");
        }
        public void Dispose() { Release(); _client.Dispose(); Content.Dispose(); Body.Dispose(); }
    }

    private sealed class GateBody(byte[] bytes, bool failCleanup) : ProbeStream(bytes)
    {
        private bool _readStarted;
        public TaskCompletionSource ReadEntered { get; } = Gate();
        public TaskCompletionSource ReleaseRead { get; } = Gate();
        public TaskCompletionSource CleanupEntered { get; } = Gate();
        public TaskCompletionSource ReleaseCleanup { get; } = Gate();
        public int AsyncCloses; public bool SyncBeforeAsync;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { if (!_readStarted) { _readStarted = true; ReadEntered.TrySetResult(); await ReleaseRead.Task.WaitAsync(token); } return await base.ReadAsync(buffer, token); }
        public override async ValueTask DisposeAsync()
        { AsyncCloses++; CleanupEntered.TrySetResult(); await ReleaseCleanup.Task; Disposed = true; if (failCleanup) throw new IOException("private-detached-marker"); }
        protected override void Dispose(bool disposing) { if (disposing) SyncBeforeAsync |= !Disposed; base.Dispose(disposing); }
    }

    private static ChatRequest Request() => new(Model, [new("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"owned\",\"timestamp\":1}"))], 1700000000000);
    private static AssistantMessage Message(ChatRequest request, string text, StopReason reason) =>
        new(request.Model.Api, request.Model.Provider, request.Model.Id, request.Timestamp, text.Length == 0 ? [] : [new TextContent(text)], TokenUsage.Zero, reason);
    private static async Task ReadStart(IAsyncEnumerator<StreamEvent> reader) =>
        Check(await reader.MoveNextAsync().AsTask().WaitAsync(Deadline) && reader.Current is StreamStarted, "Actual run did not deliver its initial Start.");
    private static void EqualTerminal(StreamTerminalEvent? terminal, ChatResult result) =>
        Check(terminal is not null && PiWireJson.WriteMessage(terminal.Message).ToString() == PiWireJson.WriteMessage(result.Message).ToString(), "Actual terminal and authoritative Completion disagree.");
    private static async Task Close(ChatRun run, IAsyncEnumerator<StreamEvent> reader, OwnedProvider source)
    { source.ReleaseRead.TrySetResult(); source.ReleaseCleanup.TrySetResult(); run.Cancel(); await reader.DisposeAsync(); await run.DisposeAsync(); }
    private static async Task Observe(Task task) { try { await task; } catch (OperationCanceledException) { } }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
