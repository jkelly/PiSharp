using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Contracts;

internal static class CompletionsResponseBodyReaderTests
{
    private static readonly ModelDescriptor Model = new("reader-model", "openai-completions", "openai");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private const string Finish = "{\"choices\":[{\"delta\":{\"content\":\"\\u03c0\\ud83d\\ude00\"},\"finish_reason\":\"stop\"}]}";

    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("completions-reader.default-http-path-cancellation-and-physical-owner-join", DefaultPath);
        yield return ("completions-reader.actual-done-cancel-release-versus-eof-release-only", DoneAndEof);
        yield return ("completions-reader.active-read-and-late-lease-authority-admission", LeaseAdmission);
        yield return ("completions-reader.cooperative-cancellation-shares-actual-read-settlement", SharedCancellation);
        yield return ("completions-reader.throwing-cancel-callback-and-noncooperative-read-join", ThrowingCancellation);
        yield return ("completions-reader.external-result-count-bounds-before-decoding", ResultBounds);
        yield return ("completions-reader.exact-done-real-cancel-release-faults-with-successful-physical-cleanup", DoneReaderFaults);
        yield return ("completions-reader.predone-eof-and-physical-owner-faults-remain-fatal", FatalControls);
    }

    private static async Task DefaultPath()
    {
        using var fixture = new Fixture(Wire(Finish, "[DONE]"), gateRead: true, useFactory: false);
        using var caller = new CancellationTokenSource();
        await using var run = await new ChatClient(fixture.Observed, capacity: 1).StartAsync(Request(), caller.Token);
        var frames = new List<StreamEvent>(); var drain = Drain(run, frames);
        try
        {
            await fixture.Body.ReadEntered.Task.WaitAsync(Deadline); caller.Cancel();
            await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline);
            Check(!run.Completion.IsCompleted && !fixture.Content.Disposed && fixture.Body.LastReadToken.IsCancellationRequested,
                "Default reader cancellation failed to interrupt the actual read or join its physical owner.");
            fixture.Body.ReleaseCleanup.TrySetResult(); await drain.WaitAsync(Deadline); var result = await run.Completion;
            Check(result.Failure?.Kind == ChatFailureKind.Cancelled && fixture.Body.AsyncCloses == 1 && !fixture.Body.SyncBeforeAsync &&
                fixture.Content.Disposed && !fixture.Handler.Disposed, "Default actual lease changed cleanup/client authority.");
            EqualTerminal(frames.OfType<StreamTerminalEvent>().Single(), result);
        }
        finally { fixture.Release(); run.Cancel(); await run.DisposeAsync(); await Observe(drain); }
    }

    private static async Task DoneAndEof()
    {
        foreach (var done in new[] { true, false })
        {
            var prefix = Wire(done ? [Finish, "[DONE]"] : [Finish]);
            var input = done ? prefix.Concat(Encoding.UTF8.GetBytes("data: {private-ignored-tail}\n\n")).ToArray() : prefix;
            using var fixture = new Fixture(input, readBufferBytes: 1);
            await using var run = await new ChatClient(fixture.Observed, capacity: 1).StartAsync(Request());
            var frames = new List<StreamEvent>(); var drain = Drain(run, frames);
            try
            {
                await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline); var reader = fixture.Reader!;
                Check(reader.Cancels == (done ? 1 : 0) && reader.Releases == 1 && !reader.Locked &&
                    fixture.Body.BytesRead == prefix.Length && !run.Completion.IsCompleted && !fixture.Content.Disposed,
                    "DONE/EOF reader effects, ignored-tail consumption or physical cleanup order changed.");
                Check(reader.Operations.SequenceEqual(done ? ["cancel", "release"] : ["release"]), "Actual lease operation order changed.");
                fixture.Body.ReleaseCleanup.TrySetResult(); await drain.WaitAsync(Deadline); var result = await run.Completion;
                Check(result.Failure is null && result.Message.Content.OfType<TextContent>().Single().Text == "\u03c0\U0001f600" && fixture.Body.AsyncCloses == 1,
                    "Reader lifetime altered actual Unicode/Stop finalization.");
                EqualTerminal(fixture.Observed.Terminal, result);
            }
            finally { fixture.Release(); run.Cancel(); await run.DisposeAsync(); await Observe(drain); }
        }
    }

    private static async Task LeaseAdmission()
    {
        var body = new ControlledBody([1], gateRead: true); var reader = CompletionsResponseBodyReader.FromStream(body);
        Task<int>? pending = null;
        try
        {
            pending = reader.ReadAsync(new byte[1]).AsTask(); await body.ReadEntered.Task.WaitAsync(Deadline);
            await DeniedRead(() => reader.ReadAsync(new byte[1])); Denied(reader.Release);
            body.ReleaseRead.TrySetResult(); Check(await pending.WaitAsync(Deadline) == 1, "Admitted actual read was rewritten.");
            reader.Release();
            await DeniedRead(() => reader.ReadAsync(new byte[1])); await DeniedCancellation(reader.CancelAsync); Denied(reader.Release);
            Check(body.AsyncCloses == 0 && !body.Disposed, "Lease release disposed its independently owned physical body.");
        }
        finally
        {
            body.ReleaseRead.TrySetResult(); body.ReleaseCleanup.TrySetResult(); if (pending is not null) await Observe(pending);
            try { reader.Release(); } catch (InvalidOperationException) { } await body.DisposeAsync();
        }
    }

    private static async Task SharedCancellation()
    {
        var body = new ControlledBody([1], gateRead: true); var reader = CompletionsResponseBodyReader.FromStream(body);
        Task<int>? pending = null;
        try
        {
            pending = reader.ReadAsync(new byte[1]).AsTask(); await body.ReadEntered.Task.WaitAsync(Deadline);
            var first = reader.CancelAsync().AsTask(); var second = reader.CancelAsync().AsTask();
            await Task.WhenAll(first, second).WaitAsync(Deadline);
            var canceled = false; try { _ = await pending; } catch (OperationCanceledException) { canceled = true; }
            Check(canceled && body.LastReadToken.IsCancellationRequested && ReferenceEquals(first, second) && !body.Disposed,
                "Real read cancellation did not share its actual joined settlement or borrowed-body ownership.");
            reader.Release();
        }
        finally
        {
            body.ReleaseRead.TrySetResult(); body.ReleaseCleanup.TrySetResult(); if (pending is not null) await Observe(pending);
            try { reader.Release(); } catch (InvalidOperationException) { } await body.DisposeAsync();
        }
    }

    private static async Task ThrowingCancellation()
    {
        var body = new ControlledBody([1], gateRead: true, ignoreReadCancellation: true, throwCancellation: true);
        var reader = CompletionsResponseBodyReader.FromStream(body); Task<int>? pending = null;
        try
        {
            pending = reader.ReadAsync(new byte[1]).AsTask(); await body.ReadEntered.Task.WaitAsync(Deadline);
            var first = reader.CancelAsync().AsTask(); var second = reader.CancelAsync().AsTask();
            Check(!first.IsCompleted && !second.IsCompleted && body.CancellationCallbacks == 1,
                "Throwing callback abandoned the actual noncooperative read or duplicated cancellation.");
            Denied(reader.Release); body.ReleaseRead.TrySetResult(); Check(await pending.WaitAsync(Deadline) == 1, "Owned late read result was fabricated or skipped.");
            var failed = false; try { await first.WaitAsync(Deadline); } catch (AggregateException) { failed = true; }
            Check(failed && ReferenceEquals(first, second) && !body.Disposed, "Canceled-reader fault lost shared settlement or disposed the physical body.");
            await Observe(second); reader.Release();
        }
        finally
        {
            body.ReleaseRead.TrySetResult(); body.ReleaseCleanup.TrySetResult(); if (pending is not null) await Observe(pending);
            try { reader.Release(); } catch (InvalidOperationException) { } await body.DisposeAsync();
        }
    }

    private static async Task ResultBounds()
    {
        foreach (var count in new[] { -1, 9 })
        {
            using var fixture = new Fixture(Wire(Finish, "[DONE]"), readBufferBytes: 8, invalidCount: count);
            await using var run = await new ChatClient(fixture.Observed).StartAsync(Request()); var frames = new List<StreamEvent>(); var drain = Drain(run, frames);
            try
            {
                await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline); var reader = fixture.Reader!;
                Check(fixture.ProviderCalls == 0 && reader.Cancels == 1 && reader.Releases == 1 && !run.Completion.IsCompleted,
                    "Invalid executor count gained parsed provider effects or bypassed actual cleanup.");
                fixture.Body.ReleaseCleanup.TrySetResult(); await drain.WaitAsync(Deadline); var result = await run.Completion;
                Check(result.Failure?.Kind == ChatFailureKind.Provider && result.Message.StopReason == StopReason.Error && fixture.Body.AsyncCloses == 1,
                    "Negative/over-buffer result did not fail closed with its physical owner joined.");
                EqualTerminal(fixture.Observed.Terminal, result);
            }
            finally { fixture.Release(); run.Cancel(); await run.DisposeAsync(); await Observe(drain); }
        }
    }

    private static async Task DoneReaderFaults()
    {
        foreach (var operation in new[] { "cancel", "release", "both" })
        {
            using var fixture = new Fixture(Wire(Finish, "[DONE]"), failReaderCancel: operation is "cancel" or "both", failReaderRelease: operation is "release" or "both", gateCancel: true);
            await using var run = await new ChatClient(fixture.Observed, capacity: 1).StartAsync(Request()); var frames = new List<StreamEvent>(); var drain = Drain(run, frames);
            try
            {
                await fixture.CancelEntered.Task.WaitAsync(Deadline);
                Check(!run.Completion.IsCompleted && fixture.Observed.Terminal is null && !fixture.Content.Disposed && fixture.Reader!.Releases == 0,
                    "Source-compatible DONE skipped independently held actual cancellation or released early.");
                fixture.ReleaseCancel.TrySetResult(); await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline); var reader = fixture.Reader!;
                Check(reader.Cancels == 1 && reader.Releases == 1 && !reader.Locked &&
                    !run.Completion.IsCompleted && fixture.Observed.Terminal is null && !fixture.Content.Disposed,
                    "Nonfatal reader-operation policy skipped release/physical cleanup or published early final output.");
                fixture.Body.ReleaseCleanup.TrySetResult(); await drain.WaitAsync(Deadline); var result = await run.Completion;
                Check(result.Failure is null && result.Message.StopReason == StopReason.Stop && fixture.Body.AsyncCloses == 1 && fixture.Content.Disposed && !fixture.Handler.Disposed &&
                    !(result.Message.ExtraProperties?.TryGet("openAICompletionsFailure", out _) ?? false),
                    "Exact-DONE real reader fault changed semantic completion or fabricated canonical diagnostics.");
                EqualTerminal(fixture.Observed.Terminal, result); PrivateMarkerAbsent(result);
            }
            finally { fixture.Release(); run.Cancel(); await run.DisposeAsync(); await Observe(drain); }
        }
    }

    private static async Task FatalControls()
    {
        foreach (var mode in new[] { "predone", "eof", "physical", "combined", "physical-release", "physical-both" })
        {
            var wire = mode == "predone" ? Wire("{private-invalid}") : mode == "eof" ? Wire(Finish) : Wire(Finish, "[DONE]");
            using var fixture = new Fixture(wire, failReaderCancel: mode is "predone" or "combined" or "physical-both",
                failReaderRelease: mode is "eof" or "physical-release" or "physical-both", failPhysical: mode is "physical" or "combined" or "physical-release" or "physical-both");
            await using var run = await new ChatClient(fixture.Observed).StartAsync(Request()); var frames = new List<StreamEvent>(); var drain = Drain(run, frames);
            try
            {
                await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline);
                Check(!run.Completion.IsCompleted && fixture.Observed.Terminal is null && fixture.Reader!.Releases == 1,
                    "Fatal reader/physical control skipped its independent owned cleanup.");
                fixture.Body.ReleaseCleanup.TrySetResult(); await drain.WaitAsync(Deadline); var result = await run.Completion;
                Check(result.Failure?.Kind == ChatFailureKind.Provider && result.Message.StopReason == StopReason.Error && fixture.Content.Disposed && fixture.Body.AsyncCloses == 1,
                    "Pre-DONE, EOF release or physical disposal fault gained successful completion.");
                EqualTerminal(fixture.Observed.Terminal, result); PrivateMarkerAbsent(result);
            }
            finally { fixture.Release(); run.Cancel(); await run.DisposeAsync(); await Observe(drain); }
        }
    }

    private sealed class Fixture : IDisposable
    {
        public ControlledBody Body { get; }
        public StreamProbeContent Content { get; }
        public FakeHttpHandler Handler { get; }
        public RecordingTransport Observed { get; }
        public ProbedReader? Reader;
        public int ProviderCalls;
        public TaskCompletionSource CancelEntered { get; } = Gate();
        public TaskCompletionSource ReleaseCancel { get; } = Gate();
        private readonly HttpClient _client;

        public Fixture(byte[] wire, bool gateRead = false, bool useFactory = true, int readBufferBytes = 4096,
            int? invalidCount = null, bool failReaderCancel = false, bool failReaderRelease = false, bool gateCancel = false, bool failPhysical = false)
        {
            Body = new(wire, gateRead, failPhysical: failPhysical); Content = new(Body);
            Handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = Content })); _client = new(Handler);
            var hooks = new CompletionsLifecycleHooks { OnProviderStreamEvent = (_, _, _) => { ProviderCalls++; return ValueTask.CompletedTask; } };
            var framing = new PiSharp.AI.Transports.SseDecoderOptions(ReadBufferBytes: readBufferBytes, EofBehavior: PiSharp.AI.Transports.SseEofBehavior.DispatchPendingEvent)
                { Profile = PiSharp.AI.Transports.SseFramingProfile.OpenAISdk719 };
            var options = new CompletionsHttpSseOptions(Framing: framing) { Hooks = hooks };
            if (useFactory) options = options with { BodyReaderFactory = (body, token) =>
            {
                token.ThrowIfCancellationRequested(); var reader = new ProbedReader(body, this, invalidCount, failReaderCancel, failReaderRelease, gateCancel);
                Reader = reader; return ValueTask.FromResult<ICompletionsResponseBodyReader>(reader);
            } };
            var factory = new CompletionsKeyAuthRequestFactory(new("https://owned-reader.invalid/v1/chat/completions"), Model);
            Observed = new(new CompletionsHttpSseTransport(_client, (request, token) => factory.Create(request, "authored-inert-noncredential", token), options));
        }
        public void Release() { Body.ReleaseRead.TrySetResult(); Body.ReleaseCleanup.TrySetResult(); ReleaseCancel.TrySetResult(); }
        public void Dispose() { Release(); _client.Dispose(); Content.Dispose(); Body.Dispose(); }
    }

    private sealed class ProbedReader(Stream body, Fixture owner, int? invalidCount, bool failCancel, bool failRelease, bool gateCancel) : ICompletionsResponseBodyReader
    {
        private readonly ICompletionsResponseBodyReader _core = CompletionsResponseBodyReader.FromStream(body);
        public int Cancels, Releases;
        public bool Locked = true;
        public List<string> Operations { get; } = [];
        public ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token = default) =>
            invalidCount is { } count ? ValueTask.FromResult(count) : _core.ReadAsync(destination, token);
        public async ValueTask CancelAsync()
        {
            Cancels++; Operations.Add("cancel"); await _core.CancelAsync(); owner.CancelEntered.TrySetResult();
            if (gateCancel) await owner.ReleaseCancel.Task; if (failCancel) throw new IOException("private-reader-marker");
        }
        public void Release()
        { Releases++; _core.Release(); Locked = false; Operations.Add("release"); if (failRelease) throw new IOException("private-reader-marker"); }
    }

    private sealed class RecordingTransport(IChatTransport inner) : IChatTransport
    {
        public StreamTerminalEvent? Terminal;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { await foreach (var frame in inner.StreamAsync(request, token)) { if (frame is StreamTerminalEvent terminal) Terminal = terminal; yield return frame; } }
    }

    private sealed class ControlledBody(byte[] bytes, bool gateRead = false, bool ignoreReadCancellation = false, bool throwCancellation = false, bool failPhysical = false) : ProbeStream(bytes)
    {
        public TaskCompletionSource ReadEntered { get; } = Gate();
        public TaskCompletionSource ReleaseRead { get; } = Gate();
        public TaskCompletionSource CleanupEntered { get; } = Gate();
        public TaskCompletionSource ReleaseCleanup { get; } = Gate();
        public CancellationToken LastReadToken { get; private set; }
        public int AsyncCloses, CancellationCallbacks, BytesRead;
        public bool SyncBeforeAsync;
        public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token = default)
        {
            LastReadToken = token;
            using var callback = throwCancellation ? token.Register(() => { CancellationCallbacks++; throw new IOException("private-reader-marker"); }) : default;
            ReadEntered.TrySetResult();
            if (gateRead) { if (ignoreReadCancellation) await ReleaseRead.Task; else await ReleaseRead.Task.WaitAsync(token); }
            var count = await base.ReadAsync(destination, ignoreReadCancellation ? CancellationToken.None : token); BytesRead += count; return count;
        }
        public override async ValueTask DisposeAsync()
        { AsyncCloses++; CleanupEntered.TrySetResult(); await ReleaseCleanup.Task; Disposed = true; if (failPhysical) throw new IOException("private-reader-marker"); }
        protected override void Dispose(bool disposing) { if (disposing) SyncBeforeAsync |= !Disposed; base.Dispose(disposing); }
    }

    private static ChatRequest Request() => new(Model, [new("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"owned\",\"timestamp\":1}"))], 1700000000000);
    private static byte[] Wire(params string[] values) => Encoding.UTF8.GetBytes(string.Concat(values.Select(value => "data: " + value + "\n\n")));
    private static async Task Drain(ChatRun run, List<StreamEvent> frames) { await foreach (var frame in run.ReadEventsAsync()) frames.Add(frame); }
    private static void EqualTerminal(StreamTerminalEvent? terminal, ChatResult result) =>
        Check(terminal is not null && PiWireJson.WriteMessage(terminal.Message).ToString() == PiWireJson.WriteMessage(result.Message).ToString(), "Actual terminal/Completion mismatch.");
    private static void PrivateMarkerAbsent(ChatResult result) =>
        Check(!PiWireJson.WriteMessage(result.Message).ToString().Contains("private-reader-marker", StringComparison.Ordinal), "Reader/physical exception text leaked into canonical output.");
    private static void Denied(Action action)
    { var denied = false; try { action(); } catch (InvalidOperationException) { denied = true; } Check(denied, "Invalid active/late reader authority was admitted."); }
    private static async Task DeniedRead(Func<ValueTask<int>> action)
    { var denied = false; try { _ = await action(); } catch (InvalidOperationException) { denied = true; } Check(denied, "Invalid active/late read was admitted."); }
    private static async Task DeniedCancellation(Func<ValueTask> action)
    { var denied = false; try { await action(); } catch (InvalidOperationException) { denied = true; } Check(denied, "Invalid late cancellation was admitted."); }
    private static async Task Observe(Task task) { try { await task; } catch (OperationCanceledException) { } catch (AggregateException) { } }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
