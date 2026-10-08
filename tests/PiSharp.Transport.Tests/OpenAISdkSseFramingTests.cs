using System.Collections.Immutable;
using System.Net;
using System.Numerics;
using System.Text;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.AI.Transports;
using PiSharp.Contracts;

internal static class OpenAISdkSseFramingTests
{
    private const string Finish = """{"choices":[{"delta":{},"finish_reason":"stop"}]}""";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static readonly ModelDescriptor Model = new("sdk-framing", "openai-completions", "openai");
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("sdk-sse.legacy-constructors-deconstruction-and-standard-behavior", LegacyAndStandard);
        yield return ("sdk-sse.null-empty-named-no-data-reset-and-eof", NamesAndEof);
        yield return ("sdk-sse.every-byte-split-replacement-utf8-and-line-reset-bom", Utf8AndBom);
        yield return ("sdk-sse.inclusive-bounds-ignored-controls-and-standard-current-id", BoundsAndControls);
        yield return ("sdk-sse.default-completions-real-http-names-done-and-replacement", CompletionsComposition);
        yield return ("sdk-sse.pull-cancellation-and-sentinel-join-owned-cleanup", Ownership);
    }

    private static SseDecoderOptions Sdk(int buffer = 4096, int line = 65_536, int size = 1_048_576) =>
        new(buffer, line, size, false, SseEofBehavior.DispatchPendingEvent) { Profile = SseFramingProfile.OpenAISdk719 };
    private static SseDecoderOptions StrictStandard() => new(RejectInvalidUtf8: true, EofBehavior: SseEofBehavior.DispatchPendingEvent);
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static string Data(string value) => "data: " + value + "\n\n";

    private static async Task LegacyAndStandard()
    {
        // These are old positional consumer forms, including exact-arity deconstruction.
        var options = new SseDecoderOptions(8, 128, 256, false, SseEofBehavior.DiscardPendingEvent);
        var (buffer, line, size, strict, eof) = options;
        Equal(8, buffer); Equal(128, line); Equal(256, size); Equal(false, strict); Equal(SseEofBehavior.DiscardPendingEvent, eof);
        var legacy = new SseEvent("custom", "a", "kept", new BigInteger(12));
        var (type, data, id, retry) = legacy;
        Equal("custom", type); Equal("a", data); Equal("kept", id); Equal<BigInteger?>(12, retry);
        var decoder = new SseDecoder(options);
        var rows = await Drain(decoder.DecodeAsync(new ProbeStream(Bytes("id: kept\nretry: 12\nevent: discarded\n\nevent: custom\ndata: a\n\ndata: pending"))));
        Equal(1, rows.Count); Equal(legacy, rows[0]); Equal<string?>(null, rows[0].EventName);
        Equal("kept", decoder.State.LastEventId); Equal<BigInteger?>(12, decoder.State.RetryMilliseconds);
        // Standard strips one stream-leading BOM and retains replacement decoding and discard-at-EOF defaults.
        var standard = await Decode(Bytes("\uFEFFdata: a\n\n\uFEFFdata: ignored\n\ndata: tail"), new());
        Equal(1, standard.Count); Equal("a", standard[0].Data);
        var replacement = await Decode([.. Bytes("data: "), 0xff, .. Bytes("\n\n")], new());
        Equal("\uFFFD", replacement.Single().Data);
        Equal(0, (await Decode(Bytes("event: error\n\n"), new())).Count);
    }

    private static async Task NamesAndEof()
    {
        foreach (var ending in new[] { "\n", "\r\n", "\r" })
        {
            var wire = string.Join(ending, new[] { "", ":before", "unknown: ignored", "", "event:", "", ":after", "",
                "data: one", "", "data: two", "", "event: first", "event: final", "", "data", "",
                "event: message", "data: x", "data:y", "", "event:  raw:name\t\0", "", "event: thread.run" });
            var rows = await Decode(Bytes(wire), Sdk(buffer: 1));
            Equal(7, rows.Count);
            Row(rows[0], "", "one"); Row(rows[1], null, "two"); Row(rows[2], "final", ""); Row(rows[3], null, "");
            Row(rows[4], "message", "x\ny"); Row(rows[5], " raw:name\t\0", ""); Row(rows[6], "thread.run", "");
        }
        foreach (var ending in new[] { "", "\n", "\n\n" })
        {
            Row((await Decode(Bytes("data: {}" + ending), Sdk())).Single(), null, "{}");
            Row((await Decode(Bytes("event: error" + ending), Sdk())).Single(), "error", "");
            Row((await Decode(Bytes("data:" + ending), Sdk())).Single(), null, "");
            Equal(0, (await Decode(Bytes("event:" + ending), Sdk())).Count);
        }
        var reset = await Decode(Bytes("event: named\n\nevent:\n\ndata: {}\n\ndata: []\n\n"), Sdk());
        Equal(3, reset.Count); Row(reset[0], "named", ""); Row(reset[1], "", "{}"); Row(reset[2], null, "[]");
        // A BOM-only final line completes the pending event; the subsequent EOF flush must not duplicate it.
        var bomEof = await Decode(Bytes("data: {}\n\uFEFF"), Sdk(buffer: 1));
        Equal(1, bomEof.Count); Row(bomEof[0], null, "{}");
        Equal(0, (await Decode(Bytes("\uFEFF"), Sdk())).Count);
    }

    private static async Task Utf8AndBom()
    {
        byte[] wire = [.. Bytes("\uFEFFdata: {\"text\":\"\u03b1\U0001f642"), 0xff,
            .. Bytes("\uFEFF\"}\r\n\r\n\uFEFFevent: error\r\n\uFEFFdata: {}\r\n\r\n\uFEFF\uFEFFdata: ignored\n\ndata: last")];
        var expectedData = "{\"text\":\"\u03b1\U0001f642\uFFFD\uFEFF\"}";
        for (var split = 1; split < wire.Length; split++)
        {
            var body = new ProbeStream(wire, split, wire.Length - split);
            var rows = await Drain(new SseDecoder(Sdk()).DecodeAsync(body));
            Equal(3, rows.Count); Row(rows[0], null, expectedData); Row(rows[1], "error", "{}"); Row(rows[2], null, "last");
            Check(body.Disposed, "Fragmented SDK decoder retained its owned input.");
        }
        for (var buffer = 1; buffer <= 8; buffer++)
        {
            var rows = await Decode(wire, Sdk(buffer)); Equal(3, rows.Count); Equal(expectedData, rows[0].Data);
        }
        var truncated = await Decode([.. Bytes("data: "), 0xe2], Sdk(buffer: 1));
        Row(truncated.Single(), null, "\uFFFD");
        var interrupted = await Decode([.. Bytes("data: "), 0xe2, .. Bytes("\r\ndata: "), 0x80, .. Bytes("\n\n")], Sdk(buffer: 1));
        Row(interrupted.Single(), null, "\uFFFD\n\uFFFD");
        var standardFailure = await ThrowsAsync<SseDecodeException>(() => Decode(wire, StrictStandard()));
        Equal(SseDecodeFailure.InvalidUtf8, standardFailure.Failure);
    }

    private static async Task BoundsAndControls()
    {
        Equal("a", (await Decode(Bytes("data: a\n\n"), Sdk(line: 7, size: 2))).Single().Data);
        Equal("a", (await Decode(Bytes("\uFEFFdata: a\n\n"), Sdk(line: 8, size: 2))).Single().Data);
        Equal(SseDecodeFailure.LineLimit, (await ThrowsAsync<SseDecodeException>(() => Decode(Bytes("\uFEFFdata: a\n\n"), Sdk(line: 7, size: 2)))).Failure);
        Equal(SseDecodeFailure.LineLimit, (await ThrowsAsync<SseDecodeException>(() => Decode(Bytes(":1234567\n"), Sdk(line: 7)))).Failure);
        Row((await Decode(Bytes("event: x\ndata:a\n\n"), Sdk(size: 3))).Single(), "x", "a");
        Equal(SseDecodeFailure.EventLimit, (await ThrowsAsync<SseDecodeException>(() => Decode(Bytes("event: x\ndata:a\n\n"), Sdk(size: 2)))).Failure);
        Equal("a\nb", (await Decode(Bytes("data:a\ndata:b\n\n"), Sdk(size: 4))).Single().Data);
        Equal(SseDecodeFailure.EventLimit, (await ThrowsAsync<SseDecodeException>(() => Decode(Bytes("data:a\ndata:b\n\n"), Sdk(size: 3)))).Failure);
        var ignored = string.Concat(Enumerable.Repeat(":x\nid: 1234567890\nretry: 99999999\nunknown: x\n\n", 100)) + "data:a\n\n";
        var sdk = new SseDecoder(Sdk(buffer: 1, line: 16, size: 2));
        Row((await Drain(sdk.DecodeAsync(new ProbeStream(Bytes(ignored))))).Single(), null, "a");
        Equal("", sdk.State.LastEventId); Equal<BigInteger?>(null, sdk.State.RetryMilliseconds);
        var replaced = await Decode(Bytes("event: long\nevent: x\n\ndata:a\n\n"), Sdk(size: 4));
        Equal(2, replaced.Count); Row(replaced[0], "x", ""); Row(replaced[1], null, "a");
        // Existing Standard pending + committed ID accounting remains active in the same decoder implementation.
        var standard = new SseDecoder(new(MaximumEventCharacters: 4)); var admitted = new List<SseEvent>();
        var idFailure = await ThrowsAsync<SseDecodeException>(() => DrainInto(standard.DecodeAsync(new ProbeStream(Bytes("id: aa\ndata:x\n\nid: b\ndata:x\n\n"))), admitted));
        Equal(SseDecodeFailure.EventLimit, idFailure.Failure); Equal(1, admitted.Count); Equal("aa", admitted[0].LastEventId);
        using var handler = new FakeHttpHandler((_, _) => throw new InvalidOperationException("Unexpected HTTP send.")); using var client = new HttpClient(handler);
        foreach (var options in new[] { Sdk() with { RejectInvalidUtf8 = true }, Sdk() with { EofBehavior = SseEofBehavior.DiscardPendingEvent },
            Sdk() with { Profile = (SseFramingProfile)99 }, Sdk() with { MaximumLineCharacters = 0 }, Sdk() with { MaximumEventCharacters = 0 }, Sdk() with { ReadBufferBytes = 65_537 } })
        {
            Throws<ArgumentException>(() => new SseDecoder(options));
            Throws<ArgumentException>(() => new CompletionsHttpSseTransport(client, (_, _) => new(HttpMethod.Post, "https://sdk-framing.invalid/"), new(Framing: options)));
        }
        Equal(0, handler.SendCalls);
    }

    private static async Task CompletionsComposition()
    {
        foreach (var suffix in new[] { "event: error\n\n", "event: thread.run\n\n", "event: message\n\n", "event: error",
            "data:\n\n", "event: error\ndata: {}\n\n", "\uFEFFevent: error\ndata: {}\n\n", "data: [DONE]suffix\n\n", "data:  [DONE]\n\n" })
        {
            using var fixture = new Fixture(new ProbeStream(Bytes(Data(Finish) + suffix), 1));
            Failed(await new ChatClient(fixture.Transport).CompleteAsync(Request()));
            Check(fixture.Content.Disposed && !fixture.Handler.Disposed, "Default framing failure lost HTTP ownership.");
        }
        foreach (var wire in new[] { "data: " + Finish, Data(Finish), "event:\n\n" + Data(Finish),
            "event: thread.run\ndata: {\"error\":{}}\n\n" + Data(Finish),
            Data(Finish) + "event: error\ndata: [DONE]\n\n{ignored-after-done",
            Data(Finish) + "event: thread.run\ndata: [DONE]" })
        {
            using var fixture = new Fixture(new ProbeStream(Bytes(wire), 1, 2, 3));
            var result = await new ChatClient(fixture.Transport).CompleteAsync(Request());
            Equal<ChatFailure?>(null, result.Failure); Equal(StopReason.Stop, result.Message.StopReason);
        }
        using (var explicitStandard = new Fixture(new ProbeStream(Bytes(Data(Finish) + "event: error\n\n")), new(Framing: StrictStandard())))
            Equal<ChatFailure?>(null, (await new ChatClient(explicitStandard.Transport).CompleteAsync(Request())).Failure);
        byte[] replacement = [.. Bytes("data: {\"choices\":[{\"delta\":{\"content\":\""), 0xff, .. Bytes("\"}}]}\n\n" + Data(Finish))];
        using (var fixture = new Fixture(new ProbeStream(replacement, 1)))
        {
            var result = await new ChatClient(fixture.Transport).CompleteAsync(Request()); Equal<ChatFailure?>(null, result.Failure);
            Equal("\uFFFD", ((TextContent)result.Message.Content.Single()).Text);
        }
        using (var strict = new Fixture(new ProbeStream(replacement, 1), new(Framing: StrictStandard())))
            Failed(await new ChatClient(strict.Transport).CompleteAsync(Request()));
        // Even an empty-data named frame consumes a wrapper frame slot before malformed-JSON admission.
        using (var limited = new Fixture(new ProbeStream(Bytes(Data(Finish) + "event: error\n\n")), new(MaximumDataEvents: 1)))
            Failed(await new ChatClient(limited.Transport).CompleteAsync(Request()));
    }

    private static async Task Ownership()
    {
        var decoderBody = new HeldCleanupStream(Bytes("event: only\n\ndata: later\n\n"), 13);
        var iterator = new SseDecoder(Sdk(buffer: 1)).DecodeAsync(decoderBody).GetAsyncEnumerator();
        Check(await iterator.MoveNextAsync(), "SDK named-only frame was absent."); Row(iterator.Current, "only", "");
        var reads = decoderBody.ReadCalls; var closing = iterator.DisposeAsync().AsTask();
        try
        {
            await decoderBody.CleanupEntered.Task.WaitAsync(Deadline); Check(!closing.IsCompleted, "SDK early return abandoned owned stream cleanup.");
            Equal(reads, decoderBody.ReadCalls); decoderBody.ReleaseCleanup.TrySetResult(); await closing.WaitAsync(Deadline);
            Equal(1, decoderBody.AsyncDisposals);
        }
        finally { decoderBody.ReleaseCleanup.TrySetResult(); await closing; }
        var borrowed = new ProbeStream(Bytes("data:{}")); await Drain(new SseDecoder(Sdk()).DecodeAsync(borrowed, leaveOpen: true));
        Check(!borrowed.Disposed, "SDK decoder disposed a borrowed input."); await borrowed.DisposeAsync();

        foreach (var fault in new[] { false, true })
        {
            var body = new HeldCleanupStream(Bytes(Data(Finish) + "event: error\ndata: [DONE]\n\n")) { BlockAfterBytes = true, FaultCleanup = fault };
            using var fixture = new Fixture(body); using var cancellation = new CancellationTokenSource();
            var pending = new ChatClient(fixture.Transport).CompleteAsync(Request(), cancellation.Token);
            try
            {
                await body.CleanupEntered.Task.WaitAsync(Deadline);
                Check(!pending.IsCompleted && !fixture.Content.Disposed, "Completions terminal overtook held body cleanup.");
                Check(!body.TrailingReadEntered.Task.IsCompleted, "Exact named DONE requested a trailing body read.");
                _ = await fixture.Requests.Single().Content!.ReadAsStringAsync();
                body.ReleaseCleanup.TrySetResult(); var result = await pending.WaitAsync(Deadline);
                if (fault) Failed(result); else Equal<ChatFailure?>(null, result.Failure);
                Equal(1, body.AsyncDisposals); Check(fixture.Content.Disposed && !body.SyncBeforeAsync, "Response cleanup preceded asynchronous body settlement.");
                await ThrowsAsync<ObjectDisposedException>(() => fixture.Requests.Single().Content!.ReadAsStringAsync());
                Check(!fixture.Handler.Disposed, "Transport disposed its borrowed client.");
            }
            finally { cancellation.Cancel(); body.ReleaseCleanup.TrySetResult(); await Observe(pending); }
        }
        var blockedBody = new HeldCleanupStream([]) { BlockRead = true }; using var blocked = new Fixture(blockedBody); using var canceled = new CancellationTokenSource();
        var running = new ChatClient(blocked.Transport).CompleteAsync(Request(), canceled.Token);
        try
        {
            await blockedBody.ReadEntered.Task.WaitAsync(Deadline); canceled.Cancel(); await blockedBody.CleanupEntered.Task.WaitAsync(Deadline);
            Check(!running.IsCompleted && !blocked.Content.Disposed, "Cancellation abandoned owned SDK-profile cleanup.");
            blockedBody.ReleaseCleanup.TrySetResult(); var result = await running.WaitAsync(Deadline);
            Equal(ChatFailureKind.Cancelled, result.Failure!.Kind); Equal(StopReason.Aborted, result.Message.StopReason);
            Equal(1, blockedBody.AsyncDisposals); Check(blocked.Content.Disposed && !blockedBody.SyncBeforeAsync, "Canceled cleanup ownership changed.");
        }
        finally { canceled.Cancel(); blockedBody.ReleaseCleanup.TrySetResult(); await Observe(running); }
    }

    private sealed class Fixture : IDisposable
    {
        public StreamProbeContent Content { get; }
        public FakeHttpHandler Handler { get; }
        public List<HttpRequestMessage> Requests { get; } = [];
        public CompletionsHttpSseTransport Transport { get; }
        private readonly HttpClient _client;
        public Fixture(Stream body, CompletionsHttpSseOptions? options = null)
        {
            Content = new(body); Handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = Content }));
            _client = new(Handler); Transport = new(_client, (_, _) =>
            { var request = new HttpRequestMessage(HttpMethod.Post, "https://sdk-framing.invalid/") { Content = new StringContent("{}") }; Requests.Add(request); return request; }, options);
        }
        public void Dispose() { _client.Dispose(); Content.Dispose(); }
    }
    private sealed class HeldCleanupStream(byte[] bytes, params int[] chunks) : ProbeStream(bytes, chunks)
    {
        public TaskCompletionSource ReadEntered { get; } = Gate();
        public TaskCompletionSource TrailingReadEntered { get; } = Gate();
        public TaskCompletionSource CleanupEntered { get; } = Gate();
        public TaskCompletionSource ReleaseCleanup { get; } = Gate();
        public bool BlockRead, BlockAfterBytes, FaultCleanup, SyncBeforeAsync;
        public int AsyncDisposals;
        private Task? _cleanup;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadEntered.TrySetResult();
            if (BlockRead) { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
            var count = await base.ReadAsync(buffer, cancellationToken);
            if (count == 0 && BlockAfterBytes) { TrailingReadEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
            return count;
        }
        public override ValueTask DisposeAsync() => new(_cleanup ??= CloseCoreAsync());
        private async Task CloseCoreAsync()
        { AsyncDisposals++; CleanupEntered.TrySetResult(); await ReleaseCleanup.Task; Disposed = true; if (FaultCleanup) throw new IOException("private cleanup fault"); }
        protected override void Dispose(bool disposing) { if (disposing) SyncBeforeAsync |= !Disposed; base.Dispose(disposing); }
    }
    private static ChatRequest Request() => new(Model, ImmutableArray<TranscriptEntry>.Empty, 123);
    private static async Task<List<SseEvent>> Decode(byte[] bytes, SseDecoderOptions options)
    { var body = new ProbeStream(bytes); var rows = await Drain(new SseDecoder(options).DecodeAsync(body)); Check(body.Disposed, "SSE decoder retained owned input."); return rows; }
    private static async Task<List<SseEvent>> Drain(IAsyncEnumerable<SseEvent> source) { var rows = new List<SseEvent>(); await DrainInto(source, rows); return rows; }
    private static async Task DrainInto(IAsyncEnumerable<SseEvent> source, List<SseEvent> rows) { await foreach (var row in source) rows.Add(row); }
    private static void Row(SseEvent row, string? name, string data)
    { Equal(name, row.EventName); Equal(string.IsNullOrEmpty(name) ? "message" : name, row.EventType); Equal(data, row.Data); Equal("", row.LastEventId); Equal<BigInteger?>(null, row.RetryMilliseconds); }
    private static void Failed(ChatResult result)
    {
        Check(result.Failure is not null && result.Message.StopReason == StopReason.Error, "SDK-profile failure authorized a successful terminal.");
        Check(result.NativeDiagnostic is not null, "Native acquired-source failure classification was absent.");
        Equal("SourceFailed", result.NativeDiagnostic!.Code.ToString()); Equal(result.NativeDiagnostic, result.Failure!.NativeDiagnostic);
        Check(!(result.Message.ExtraProperties?.TryGet("openAICompletionsFailure", out _) ?? false), "Native classification entered the Pi message.");
        Check(!result.Failure.Message.Contains("private", StringComparison.Ordinal), "Private source/cleanup payload entered diagnostics.");
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Observe(Task task) { try { await task; } catch { } }
    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "SDK framing values differ.");
}
