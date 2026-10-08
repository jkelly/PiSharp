using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using PiSharp.AI.Transports;

string? report = null;
var requireNodeAbsent = false;
var strictCompletionsLifecycle = false;
string? filter = null;
for (var index = 0; index < args.Length; index++)
{
    if (args[index] == "--report" && index + 1 < args.Length) report = Path.GetFullPath(args[++index]);
    else if (args[index] == "--require-node-absent") requireNodeAbsent = true;
    else if (args[index] == "--strict-completions-lifecycle") strictCompletionsLifecycle = true;
    else if (args[index] == "--filter" && index + 1 < args.Length) filter = args[++index];
    else throw new ArgumentException($"Unknown or incomplete option: {args[index]}");
}
var tests = new List<(string Name, Func<Task> Run)>
{
    ("native-provider.explicit-routes-and-ownership", NativeProviderFactoryTests.ExplicitRoutesAndOwnership),
    ("native-provider.admission-and-cancellation", NativeProviderFactoryTests.AdmissionAndCancellation),
    ("native-provider.responses-early-disposal", NativeProviderFactoryTests.ResponsesEarlyDisposal),
    ("native-provider.responses-send-cancellation", NativeProviderFactoryTests.ResponsesSendCancellation),
    ("native-thinking.per-request-provider-wire-selection", NativeThinkingControlTests.WireSelection),
    ("native-thinking.capability-admission-and-native-cap", NativeThinkingControlTests.Admission),
    ("native-thinking.metadata-free-off-clears-frozen-controls", NativeThinkingControlTests.MetadataFreeOff),
    ("native-thinking.effective-completions-wire-capability", NativeThinkingControlTests.EffectiveCompletionsCapability),
    ("native-thinking.discarded-template-own-field-capability", NativeThinkingControlTests.DiscardedTemplateCapability),
    ("sse.every-byte-split-utf8-and-framing", EverySplit),
    ("sse.tiny-read-buffers", TinyBuffers),
    ("sse.lf-crlf-cr-and-mixed-boundaries", LineEndings),
    ("sse.single-leading-bom", Bom),
    ("sse.fields-comments-spaces-and-data-join", Fields),
    ("sse.event-type-reset-and-empty-data", EventTypes),
    ("sse.id-persistence-null-and-reset", Ids),
    ("sse.retry-ascii-digits-and-big-integers", Retry),
    ("sse.control-only-state", ControlOnly),
    ("sse.eof-standard-discards-pending", StandardEof),
    ("sse.eof-opt-in-dispatch", DispatchEof),
    ("sse.invalid-utf8-replacement-and-strict", InvalidUtf8),
    ("sse.line-limits-all-fields", LineLimits),
    ("sse.event-limits-and-replacement-accounting", EventLimits),
    ("sse.budgets-reset-between-events", ResetBudgets),
    ("sse.cancellation-before-read", CancelBeforeRead),
    ("sse.cancellation-during-read", CancelBlockedRead),
    ("sse.cancellation-buffered-events", CancelBuffered),
    ("sse.early-disposal-and-leave-open", EarlyDisposal),
    ("sse.disposal-awaits-owned-stream-cleanup", AwaitCleanup),
    ("sse.read-failure-cleans-up", ReadFailure),
    ("sse.options-and-single-enumeration", OptionsAndReuse),
    ("sse.strict-utf8-complete-prefix-independent-of-chunks", StrictPrefixes),
    ("sse.strict-utf8-early-stop-before-invalid-byte-in-same-read", StrictEarlyStop),
    ("sse.event-budget-charges-committed-and-pending-ids-before-read", DualIdBeforeRead),
    ("sse.event-budget-shared-id-and-replacement-boundaries", DualIdBoundaries),
    ("http.headers-request-and-borrowed-ownership", HttpRequestAndOwnership),
    ("http.incremental-utf8-without-content-buffering", HttpFragmentedUtf8),
    ("http.options-validated-before-send", HttpOptions),
    ("http.cancellation-before-send", HttpCancelBeforeSend),
    ("http.cancellation-during-send", HttpCancelSend),
    ("http.cancellation-during-body-acquisition", HttpCancelAcquire),
    ("http.cancellation-during-read", HttpCancelRead),
    ("http.success-awaits-body-cleanup-before-response", HttpSuccessCleanup),
    ("http.early-disposal-awaits-body-cleanup-before-response", HttpEarlyCleanup),
    ("http.cancel-awaits-body-cleanup-before-response", () => HttpFaultCleanup(cancel: true)),
    ("http.read-fault-awaits-body-cleanup-before-response", () => HttpFaultCleanup(cancel: false)),
    ("http.rejected-status-does-not-open-or-read-error-body", HttpRejection),
    ("http.read-fault-disposes-body-and-response-once", HttpReadFault),
    ("http.decoder-fault-disposes-body-and-response", HttpDecoderFault),
    ("http.body-cleanup-fault-still-disposes-response", HttpCleanupFault),
    ("http.send-fault-does-not-retry-or-dispose-borrowed-inputs", HttpSendFault),
    ("responses-http.fragmented-mapping-and-owned-cleanup", ResponsesHttpSseTests.FragmentedMappingAndOwnedCleanup),
    ("responses-http.invalid-data-termination-and-status-cannot-succeed", ResponsesHttpSseTests.InvalidDataAndTerminationCannotSucceed),
    ("responses-http.admission-limits-and-options", ResponsesHttpSseTests.AdmissionLimitsAndOptions),
    ("responses-http.cooperative-cancellation-awaits-cleanup", ResponsesHttpSseTests.CooperativeCancellationAwaitsCleanup),
    ("responses-http.early-disposal-awaits-cleanup", ResponsesHttpSseTests.EarlyDisposalAwaitsCleanup),
    ("responses-http.factory-send-source-faults-and-fresh-requests", ResponsesHttpSseTests.FactorySendAndSourceFaults)
};
ResponsesKeyAuthRequestFactoryTests.Register(tests);
tests.AddRange(ResponsesReasoningTests.Cases());
tests.AddRange(ResponsesToolChoiceTests.Cases());
tests.AddRange(ResponsesCacheRetentionTests.Cases());
tests.AddRange(ResponsesModelCacheCompatibilityTests.Cases());
tests.AddRange(ResponsesServiceTierTests.Cases());
tests.AddRange(ResponsesIncompleteTests.Cases());
tests.AddRange(ResponsesToolDeclarationDifferentialTests.Cases());
tests.AddRange(AnthropicMessagesHttpSseTransportTests.Cases());
tests.AddRange(CompletionsLifecycleHookTests.Cases());
tests.AddRange(CompletionsHttpSseTransportTests.Cases());
tests.AddRange(CompletionsSourceRunTests.Cases());
tests.AddRange(CompletionsStartDeliveryTests.Cases());
tests.AddRange(OpenAISdkSseFramingTests.Cases());
tests.AddRange(CompletionsLifecycleDifferentialTests.Cases());
tests.AddRange(DetachedChatReaderTests.Cases());
tests.AddRange(CompletionsResponseBodyReaderTests.Cases());
tests.AddRange(CompletionsBodyReadResultTests.Cases());
tests.AddRange(CompletionsResponseInputTests.Cases());
tests.AddRange(CompletionsNativeDiagnosticTests.Cases());
tests.AddRange(PiMessagesNativeDiagnosticTests.Cases());
tests.AddRange(CompletionsAuthRetryTests.Cases());
tests.AddRange(CompletionsAbortOrderingTests.Cases());
tests.AddRange(CompletionsToolImageFlowTests.Cases());
if (strictCompletionsLifecycle)
{
    tests.Clear();
    tests.AddRange(CompletionsLifecycleDifferentialTests.StrictCases());
}
if (filter is not null)
{
    if (strictCompletionsLifecycle) throw new ArgumentException("The full strict lifecycle inventory cannot be filtered.");
    tests = tests.Where(test => test.Name.StartsWith(filter, StringComparison.Ordinal)).ToList();
    if (tests.Count == 0) throw new ArgumentException("The test filter selected no cases.");
}
if (requireNodeAbsent) tests.Add(("sse.runtime.node-unavailable-on-path", NodeUnavailable));
var results = new List<object>();
var failed = 0;
var admitted = 0;
foreach (var (name, run) in tests)
{
    admitted++;
    var ownedCacheCase = name.StartsWith("responses-cache.", StringComparison.Ordinal) ||
        name.StartsWith("responses-model-cache.", StringComparison.Ordinal);
    Task? originalCacheTask = null;
    try
    {
        if (ownedCacheCase)
        {
            originalCacheTask = run(); // Invoke exactly once; synchronous throws retain no invented Task.
            await originalCacheTask; // The outer native owner bounds and joins the process.
        }
        else if (PiMessagesNativeDiagnosticTests.OwnsCase(name))
            await PiMessagesNativeDiagnosticTests.RunOwnedCaseAsync(name, run, report);
        else if (name.StartsWith("responses-reasoning.", StringComparison.Ordinal) ||
            name.StartsWith("responses-incomplete.", StringComparison.Ordinal) ||
            name.StartsWith("responses-tool-choice.", StringComparison.Ordinal) ||
            name.StartsWith("responses-tier.", StringComparison.Ordinal) ||
            name.StartsWith("native-thinking.", StringComparison.Ordinal))
            await run(); // These cases retain their original operations through owned cleanup.
        else await run().WaitAsync(TimeSpan.FromSeconds(name == "completions-lifecycle.native-owned-observation.all-14" ? 140 : 10));
        Console.WriteLine($"PASS {name}");
        if (ownedCacheCase) results.Add(new { testId = name, status = "passed", originalCase = CacheCaseObservation(originalCacheTask, null) });
        else results.Add(new { testId = name, status = "passed" });
    }
    catch (Exception exception)
    {
        failed++; Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
        if (ownedCacheCase) results.Add(new { testId = name, status = "failed", error = exception.Message, originalCase = CacheCaseObservation(originalCacheTask, exception) });
        else results.Add(new { testId = name, status = PiMessagesNativeDiagnosticTests.DeadlineExceeded ? "incomplete" : "failed", error = exception.Message });
        if (PiMessagesNativeDiagnosticTests.DeadlineExceeded) break;
    }
}
foreach (var (name, _) in tests.Skip(admitted)) results.Add(new { testId = name, status = "unexecuted" });
Console.WriteLine($"SSE transport tests: {admitted - failed} passed, {failed} failed, {tests.Count - admitted} unexecuted.");
if (report is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(report)!);
    await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
    {
        schemaVersion = 1, scope = "native-sse-and-injected-http-transport", sourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f",
        upstreamParity = strictCompletionsLifecycle ? "all-14-sdk-lifecycle-differentials-executed" : "not-assessed", nodeAbsentRequested = requireNodeAbsent,
        responsesHttpSseScope = "authored-injected-http-sse-to-parsed-responses-composition",
        piMessagesIntegration = new { scope = "authored-real-http-native-diagnostics",
            status = PiMessagesNativeDiagnosticTests.DeadlineExceeded ? "INCOMPLETE_NONPASSING" : "authored-regressions",
            incompleteReceipt = PiMessagesNativeDiagnosticTests.IncompleteReceipt, runtimeAcceptance = false },
        filter, tests = results, passed = admitted - failed, failed, unexecuted = tests.Count - admitted
    }, new JsonSerializerOptions { WriteIndented = true }));
}
return failed == 0 ? 0 : 1;

static object CacheCaseObservation(Task? original, Exception? caught) => new
{
    originalCreated = original is not null,
    originalTaskJoined = original?.IsCompleted ?? caught is not null,
    originalStatus = original?.Status.ToString(),
    completedSuccessfully = original?.IsCompletedSuccessfully ?? false,
    faulted = original?.IsFaulted ?? false,
    actuallyCanceled = original?.IsCanceled ?? false,
    synchronousFailure = original is null && caught is not null,
    originalFaultGraph = original?.Exception is { } aggregate ? CacheFaultGraph(aggregate) : null,
    caughtFaultGraph = caught is not null ? CacheFaultGraph(caught) : null
};

static object CacheFaultGraph(Exception root)
{
    var seen = new Dictionary<Exception, int>(ReferenceEqualityComparer.Instance);
    object Visit(Exception error)
    {
        if (seen.TryGetValue(error, out var reference)) return new { reference };
        var id = seen.Count; seen.Add(error, id);
        return new
        {
            id, type = error.GetType().AssemblyQualifiedName, message = error.Message,
            hresult = error.HResult, stackTrace = error.StackTrace,
            innerException = error.InnerException is { } inner ? Visit(inner) : null,
            innerExceptions = error is AggregateException aggregate ? aggregate.InnerExceptions.Select(Visit).ToArray() : []
        };
    }
    return Visit(root);
}

static byte[] Bytes(string input) => Encoding.UTF8.GetBytes(input);
static async Task<List<SseEvent>> Decode(string text, SseDecoderOptions? options = null)
{
    var stream = new ProbeStream(Bytes(text));
    var result = await Drain(new SseDecoder(options).DecodeAsync(stream));
    Assert(stream.Disposed, "Owned stream was not disposed.");
    return result;
}
static async Task<List<SseEvent>> Drain(IAsyncEnumerable<SseEvent> source)
{
    var events = new List<SseEvent>(); await foreach (var message in source) events.Add(message); return events;
}
static async Task EverySplit()
{
    var bytes = Bytes("\uFEFF:id ignored\r\nevent: delta\r\nid: call-文\r\nretry: 123\r\ndata: 文😀\r\ndata: x\u2028y\r\n\r\ndata: second\r\n\r\n");
    var expected = new SseEvent[] { new("delta", "文😀\nx\u2028y", "call-文", 123), new("message", "second", "call-文", 123) };
    for (var split = 1; split < bytes.Length; split++)
    {
        var stream = new ProbeStream(bytes, split, bytes.Length - split);
        var actual = await Drain(new SseDecoder().DecodeAsync(stream));
        Sequence(expected, actual); Assert(stream.Disposed, "Split-stream cleanup missing.");
    }
}
static async Task TinyBuffers()
{
    for (var size = 1; size <= 8; size++)
    {
        var messages = await Decode("data: 文😀é\n\n", new(ReadBufferBytes: size));
        Equal("文😀é", messages.Single().Data);
    }
}
static async Task LineEndings()
{
    foreach (var separator in new[] { "\n", "\r\n", "\r" })
    {
        var messages = await Decode($"data: a{separator}data: b{separator}{separator}data: c{separator}{separator}", new(ReadBufferBytes: 1));
        Sequence(new[] { "a\nb", "c" }, messages.Select(value => value.Data));
    }
    Sequence(new[] { "a", "b", "c" }, (await Decode("data:a\r\n\rdata:b\n\r\ndata:c\r\r", new(ReadBufferBytes: 1))).Select(value => value.Data));
}
static async Task Bom()
{
    Equal("\uFEFFinside", (await Decode("\uFEFFdata: \uFEFFinside\n\n", new(ReadBufferBytes: 1))).Single().Data);
    Equal("visible", (await Decode("\uFEFF\uFEFFdata: ignored\n\ndata: visible\n\n", new(ReadBufferBytes: 1))).Single().Data);
}
static async Task Fields()
{
    var messages = await Decode(":comment\nunknown: ignored\nDATA: ignored\ndata:  leading\ndata:\tvalue:tail\ndata\ndata: trailing \n\n");
    Equal(" leading\n\tvalue:tail\n\ntrailing ", messages.Single().Data);
}
static async Task EventTypes()
{
    var messages = await Decode("event: ignored\n\nevent: first\nevent: final\ndata:\n\ndata: text\n\nevent:\ndata: last\n\n");
    Sequence(new[] { "final", "message", "message" }, messages.Select(value => value.EventType));
    Sequence(new[] { "", "text", "last" }, messages.Select(value => value.Data));
}
static async Task Ids()
{
    var messages = await Decode("id: first\ndata: a\n\nid: bad\0id\ndata: b\n\ndata: c\n\nid\ndata: d\n\n");
    Sequence(new[] { "first", "first", "first", "" }, messages.Select(value => value.LastEventId));
}
static async Task Retry()
{
    var huge = new string('9', 100);
    var decoder = new SseDecoder();
    var messages = await Drain(decoder.DecodeAsync(new ProbeStream(Bytes($"retry: 00012\ndata: a\n\nretry: +1\nretry: 1.5\nretry: ١\nretry:\ndata: b\n\nretry: {huge}\ndata: c\n\n"))));
    Equal<BigInteger?>(12, messages[0].RetryMilliseconds); Equal<BigInteger?>(12, messages[1].RetryMilliseconds);
    Equal<BigInteger?>(BigInteger.Parse(huge), messages[2].RetryMilliseconds);
    Equal(messages[2].RetryMilliseconds, decoder.State.RetryMilliseconds);
}
static async Task ControlOnly()
{
    var decoder = new SseDecoder();
    var messages = await Drain(decoder.DecodeAsync(new ProbeStream(Bytes("id: checkpoint\nretry: 42\n\n"))));
    Equal(0, messages.Count); Equal("checkpoint", decoder.State.LastEventId); Equal<BigInteger?>(42, decoder.State.RetryMilliseconds);
    var pending = new SseDecoder();
    await Drain(pending.DecodeAsync(new ProbeStream(Bytes("id: uncommitted\nretry: 7\n"))));
    Equal("", pending.State.LastEventId); Equal<BigInteger?>(7, pending.State.RetryMilliseconds);
}
static async Task StandardEof()
{
    foreach (var tail in new[] { "data: tail", "data: tail\n", "event: t\ndata: tail\r\n" })
        Equal(0, (await Decode(tail)).Count);
    Equal("first", (await Decode("data: first\n\ndata: discarded")).Single().Data);
}
static async Task DispatchEof()
{
    foreach (var tail in new[] { "data: tail", "data: tail\n" })
        Equal("tail", (await Decode(tail, new(EofBehavior: SseEofBehavior.DispatchPendingEvent))).Single().Data);
    Equal(0, (await Decode("event: no-data", new(EofBehavior: SseEofBehavior.DispatchPendingEvent))).Count);
}
static async Task InvalidUtf8()
{
    var bytes = Bytes("data: ").Concat(new byte[] { 0xFF }).Concat(Bytes("\n\n")).ToArray();
    Equal("�", (await Drain(new SseDecoder(new(ReadBufferBytes: 1)).DecodeAsync(new ProbeStream(bytes)))).Single().Data);
    var strict = new ProbeStream(bytes);
    var exception = await ThrowsAsync<SseDecodeException>(() => Drain(new SseDecoder(new(RejectInvalidUtf8: true)).DecodeAsync(strict)));
    Equal(SseDecodeFailure.InvalidUtf8, exception.Failure); Assert(strict.Disposed, "Strict error did not release stream.");
    var truncated = Bytes("data: ").Concat(new byte[] { 0xE2, 0x82 }).ToArray();
    Equal("�", (await Drain(new SseDecoder(new(ReadBufferBytes: 1, EofBehavior: SseEofBehavior.DispatchPendingEvent)).DecodeAsync(new ProbeStream(truncated)))).Single().Data);
    Equal(SseDecodeFailure.InvalidUtf8, (await ThrowsAsync<SseDecodeException>(() => Drain(new SseDecoder(new(RejectInvalidUtf8: true)).DecodeAsync(new ProbeStream(truncated))))).Failure);
}
static async Task LineLimits()
{
    Equal("123", (await Decode("data:123\n\n", new(MaximumLineCharacters: 8))).Single().Data);
    foreach (var line in new[] { "data:1234", ":12345678", "unknown:x" })
    {
        var stream = new ProbeStream(Bytes(line + "\n\n"));
        var error = await ThrowsAsync<SseDecodeException>(() => Drain(new SseDecoder(new(MaximumLineCharacters: 8)).DecodeAsync(stream)));
        Equal(SseDecodeFailure.LineLimit, error.Failure); Assert(stream.Disposed, "Oversized line retained owned stream.");
    }
}
static async Task EventLimits()
{
    Equal("abc", (await Decode("data:abc\n\n", new(MaximumEventCharacters: 4))).Single().Data);
    foreach (var payload in new[] { "data:abc\ndata:x\n\n", "event:abcde\n", "id:abcde\n", "retry:12345\n" })
        Equal(SseDecodeFailure.EventLimit, (await ThrowsAsync<SseDecodeException>(() => Decode(payload, new(MaximumEventCharacters: 4)))).Failure);
    Equal("abc", (await Decode("event:aaaaaaaa\nevent:x\nid:bbbbbbbb\nid:y\ndata:abc\n\n", new(MaximumEventCharacters: 10))).Single().Data);
    Equal(SseDecodeFailure.EventLimit, (await ThrowsAsync<SseDecodeException>(() => Decode("event:aa\nid:bb\nretry:12\ndata:\n\n", new(MaximumEventCharacters: 6)))).Failure);
}
static async Task ResetBudgets()
{
    Equal(100, (await Decode(string.Concat(Enumerable.Repeat("data:abc\n\n", 100)), new(MaximumEventCharacters: 4))).Count);
}
static async Task CancelBeforeRead()
{
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    var stream = new ProbeStream(Bytes("data:x\n\n"));
    await ThrowsAsync<OperationCanceledException>(() => Drain(new SseDecoder().DecodeAsync(stream, cancellationToken: cancellation.Token)));
    Equal(0, stream.ReadCalls); Assert(stream.Disposed, "Pre-read cancellation leaked input.");
}
static async Task CancelBlockedRead()
{
    using var cancellation = new CancellationTokenSource(); var stream = new BlockingStream();
    var task = Drain(new SseDecoder().DecodeAsync(stream, cancellationToken: cancellation.Token));
    await stream.ReadEntered.Task; cancellation.Cancel();
    await ThrowsAsync<OperationCanceledException>(() => task); Assert(stream.Disposed, "Blocked-read cancellation leaked input.");
}
static async Task CancelBuffered()
{
    using var cancellation = new CancellationTokenSource(); var stream = new ProbeStream(Bytes("data:a\n\ndata:b\n\n"));
    await using var reader = new SseDecoder().DecodeAsync(stream, cancellationToken: cancellation.Token).GetAsyncEnumerator();
    Assert(await reader.MoveNextAsync(), "Missing first event."); cancellation.Cancel();
    await ThrowsAsync<OperationCanceledException>(async () => { await reader.MoveNextAsync(); });
    Assert(stream.Disposed, "Cancellation failed to release buffered stream.");
}
static async Task EarlyDisposal()
{
    foreach (var leaveOpen in new[] { false, true })
    {
        var stream = new ProbeStream(Bytes("data:a\n\ndata:b\n\n"), 8, 8);
        var reader = new SseDecoder().DecodeAsync(stream, leaveOpen).GetAsyncEnumerator();
        Assert(await reader.MoveNextAsync(), "Missing first event."); Equal(1, stream.ReadCalls);
        await reader.DisposeAsync(); Equal(1, stream.ReadCalls); Equal(!leaveOpen, stream.Disposed);
        if (leaveOpen) await stream.DisposeAsync();
    }
}
static async Task AwaitCleanup()
{
    var stream = new CleanupGateStream(Bytes("data:a\n\n"));
    var reader = new SseDecoder().DecodeAsync(stream).GetAsyncEnumerator();
    Assert(await reader.MoveNextAsync(), "Missing first event.");
    var cleanup = reader.DisposeAsync().AsTask(); await stream.CleanupEntered.Task;
    var premature = cleanup.IsCompleted; stream.ReleaseCleanup.TrySetResult(); await cleanup;
    Assert(!premature, "Decoder returned before owned stream cleanup completed."); Assert(stream.Disposed, "Cleanup did not finish.");
}
static async Task ReadFailure()
{
    var stream = new ReadFailureStream();
    await ThrowsAsync<IOException>(() => Drain(new SseDecoder().DecodeAsync(stream)));
    Assert(stream.Disposed, "Read failure leaked input.");
}
static async Task OptionsAndReuse()
{
    foreach (var options in new[] { new SseDecoderOptions(ReadBufferBytes: 0), new(ReadBufferBytes: 65_537), new(MaximumLineCharacters: 0), new(MaximumEventCharacters: 0), new(EofBehavior: (SseEofBehavior)99) })
        Throws<ArgumentOutOfRangeException>(() => new SseDecoder(options));
    var decoder = new SseDecoder(); await Drain(decoder.DecodeAsync(new ProbeStream(Bytes("data:a\n\n"))));
    var second = new ProbeStream(Bytes("data:b\n\n"));
    await ThrowsAsync<InvalidOperationException>(() => Drain(decoder.DecodeAsync(second)));
    Assert(!second.Disposed, "Rejected second enumeration took ownership of its input."); await second.DisposeAsync();
}
static HttpRequestMessage HttpRequest() => new(HttpMethod.Post, "https://synthetic.invalid/stream");
static async Task StrictPrefixes()
{
    byte[][] tails = [[0xFF], [0xC0, 0xAF], [0xED, 0xA0, 0x80], [0xF4, 0x90, 0x80, 0x80], [0xE2, 0x28, 0xA1], [0xE2, 0x82], [0xF0, 0x9F, 0x99]];
    foreach (var data in new[] { "complete", "\u6587\U0001F600" })
    foreach (var tail in tails)
    {
        var bytes = Bytes($"data:{data}\n\n").Concat(tail).ToArray();
        async Task Verify(int readBuffer, params int[] chunks)
        {
            var stream = new ProbeStream(bytes, chunks); var seen = new List<SseEvent>();
            var error = await ThrowsAsync<SseDecodeException>(async () =>
            {
                await foreach (var message in new SseDecoder(new(ReadBufferBytes: readBuffer, RejectInvalidUtf8: true)).DecodeAsync(stream)) seen.Add(message);
            });
            Equal(SseDecodeFailure.InvalidUtf8, error.Failure); Equal(data, seen.Single().Data);
            Assert(stream.Disposed, "Strict prefix failure leaked stream.");
        }
        for (var size = 1; size <= bytes.Length + 1; size++) await Verify(size);
        for (var split = 1; split < bytes.Length; split++) await Verify(4096, split, bytes.Length - split);
    }
}
static async Task StrictEarlyStop()
{
    var stream = new ProbeStream(Bytes("data:complete\n\n").Concat(new byte[] { 0xFF }).ToArray());
    var reader = new SseDecoder(new(RejectInvalidUtf8: true)).DecodeAsync(stream).GetAsyncEnumerator();
    Assert(await reader.MoveNextAsync(), "Strict mode lost completed prefix in the same read as an invalid byte.");
    Equal("complete", reader.Current.Data); await reader.DisposeAsync();
    Equal(1, stream.ReadCalls); Assert(stream.Disposed, "Strict early stop leaked stream.");
}
static async Task DualIdBeforeRead()
{
    var decoder = new SseDecoder(new(MaximumEventCharacters: 5));
    var stream = new FaultOnSecondReadStream(Bytes("id:12345\n\nid:\ndata:1234\n"));
    Equal(SseDecodeFailure.EventLimit, (await ThrowsAsync<SseDecodeException>(() => Drain(decoder.DecodeAsync(stream)))).Failure);
    Equal(1, stream.ReadCalls); Equal("12345", decoder.State.LastEventId);
    Assert(stream.Disposed, "Dual-ID limit failure leaked stream.");
}
static async Task DualIdBoundaries()
{
    Equal("", (await Decode("id:12345\n\ndata:\n\n", new(MaximumEventCharacters: 6))).Single().Data);
    var replacement = await Decode("id:abc\n\nid:d\ndata:x\n\ndata:1234\n\n", new(MaximumEventCharacters: 6));
    Sequence(new[] { "x", "1234" }, replacement.Select(value => value.Data));
    Sequence(new[] { "d", "d" }, replacement.Select(value => value.LastEventId));
    Equal(SseDecodeFailure.EventLimit, (await ThrowsAsync<SseDecodeException>(() => Decode("id:abc\n\nid:d\ndata:x\n", new(MaximumEventCharacters: 5)))).Failure);
    Equal(SseDecodeFailure.EventLimit, (await ThrowsAsync<SseDecodeException>(() => Decode("id:abc\n\nid:abc\n", new(MaximumEventCharacters: 5)))).Failure);
    Equal(0, (await Decode("id:abc\n\nid:abc\n\n", new(MaximumEventCharacters: 6))).Count);
}
static async Task HttpRequestAndOwnership()
{
    var bodies = new List<ProbeStream>(); var contents = new List<StreamProbeContent>();
    using var request = HttpRequest(); request.Content = new StringContent("{\"model\":\"fixture\"}", Encoding.UTF8, "application/json");
    request.Headers.TryAddWithoutValidation("X-Projection", ["first", "second"]);
    request.Content.Headers.TryAddWithoutValidation("X-Content-Projection", "unchanged");
    request.Version = HttpVersion.Version20; request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
    request.Options.Set(new HttpRequestOptionsKey<string>("fixture"), "opaque");
    using var handler = new FakeHttpHandler(async (sent, token) =>
    {
        Assert(ReferenceEquals(request, sent) || sent.RequestUri == new Uri("https://synthetic.invalid/second"), "Request was cloned or URI changed.");
        if (ReferenceEquals(request, sent))
        {
            Equal(HttpMethod.Post, sent.Method); Equal(HttpVersion.Version20, sent.Version);
            Equal(HttpVersionPolicy.RequestVersionExact, sent.VersionPolicy);
            Sequence(new[] { "first", "second" }, sent.Headers.GetValues("X-Projection"));
            Equal("unchanged", sent.Content!.Headers.GetValues("X-Content-Projection").Single());
            Equal("{\"model\":\"fixture\"}", await sent.Content.ReadAsStringAsync(token));
            Assert(sent.Options.TryGetValue(new HttpRequestOptionsKey<string>("fixture"), out var opaque) && opaque == "opaque", "Request options changed.");
            Equal("application/json", sent.Content.Headers.ContentType!.MediaType);
            Equal("from-client", sent.Headers.GetValues("X-Default").Single());
        }
        var body = new ProbeStream(Encoding.UTF8.GetBytes("data:ok\n\n")); bodies.Add(body);
        var content = new StreamProbeContent(body); contents.Add(content);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = sent };
    });
    using var client = new HttpClient(handler); client.DefaultRequestHeaders.TryAddWithoutValidation("X-Default", "from-client");
    var transport = new HttpSseTransport(client);
    Equal("ok", (await Drain(transport.SendAsync(request))).Single().Data);
    Equal("{\"model\":\"fixture\"}", await request.Content.ReadAsStringAsync());
    request.Headers.TryAddWithoutValidation("X-Still-Owned", "yes");
    using var second = new HttpRequestMessage(HttpMethod.Get, "https://synthetic.invalid/second");
    Equal("ok", (await Drain(transport.SendAsync(second))).Single().Data);
    Equal(2, handler.SendCalls); Assert(!handler.Disposed, "Transport disposed its borrowed client or handler.");
    Assert(bodies.All(value => value.Disposed) && contents.All(value => value.Disposed), "Responses leaked.");
}
static async Task HttpFragmentedUtf8()
{
    var body = new ProbeStream(Bytes("\uFEFFevent:delta\r\ndata:\u6587\U0001F600\r\ndata:x\r\n\r\n"));
    var content = new StreamProbeContent(body);
    using var handler = new FakeHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
    using var client = new HttpClient(handler); using var request = HttpRequest();
    using var cancellation = new CancellationTokenSource();
    var events = await Drain(new HttpSseTransport(client, new(ReadBufferBytes: 1)).SendAsync(request, cancellation.Token));
    Equal(new SseEvent("delta", "\u6587\U0001F600\nx", "", null), events.Single());
    Equal(cancellation.Token, content.AcquisitionToken); Equal(1, content.AcquireCalls); Equal(0, content.SerializeCalls);
    Assert(body.ReadCalls > 20 && body.Disposed && content.Disposed, "Fragmented response was buffered or retained.");
}
static Task HttpOptions()
{
    using var handler = new FakeHttpHandler((_, _) => throw new InvalidOperationException("Unexpected send."));
    using var client = new HttpClient(handler);
    foreach (var options in new[] { new SseDecoderOptions(ReadBufferBytes: 0), new(MaximumLineCharacters: 0), new(MaximumEventCharacters: 0), new(EofBehavior: (SseEofBehavior)99) })
        Throws<ArgumentOutOfRangeException>(() => new HttpSseTransport(client, options));
    Throws<ArgumentNullException>(() => new HttpSseTransport(null!));
    Throws<ArgumentNullException>(() => new HttpSseTransport(client).SendAsync(null!));
    Equal(0, handler.SendCalls); return Task.CompletedTask;
}
static async Task HttpCancelBeforeSend()
{
    using var handler = new FakeHttpHandler((_, _) => throw new InvalidOperationException("Unexpected send."));
    using var client = new HttpClient(handler); using var request = HttpRequest();
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    await ThrowsAsync<OperationCanceledException>(() => Drain(new HttpSseTransport(client).SendAsync(request, cancellation.Token)));
    Equal(0, handler.SendCalls); Assert(!handler.Disposed, "Borrowed client was disposed.");
}
static async Task HttpCancelSend()
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var handler = new FakeHttpHandler(async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); });
    using var client = new HttpClient(handler); using var request = HttpRequest(); using var cancellation = new CancellationTokenSource();
    var run = Drain(new HttpSseTransport(client).SendAsync(request, cancellation.Token));
    await entered.Task; cancellation.Cancel(); await ThrowsAsync<OperationCanceledException>(() => run);
    Equal(1, handler.SendCalls); Assert(!handler.Disposed, "Send cancellation disposed borrowed client.");
}
static async Task HttpCancelAcquire()
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var content = new StreamProbeContent(null, async token => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); });
    using var handler = new FakeHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
    using var client = new HttpClient(handler); using var request = HttpRequest(); using var cancellation = new CancellationTokenSource();
    var run = Drain(new HttpSseTransport(client).SendAsync(request, cancellation.Token));
    await entered.Task; cancellation.Cancel(); await ThrowsAsync<OperationCanceledException>(() => run);
    Equal(cancellation.Token, content.AcquisitionToken); Assert(content.Disposed, "Acquisition cancellation retained response."); Equal(1, handler.SendCalls);
}
static async Task HttpCancelRead()
{
    var body = new BlockingStream(); var content = new StreamProbeContent(body);
    using var handler = new FakeHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
    using var client = new HttpClient(handler); using var request = HttpRequest(); using var cancellation = new CancellationTokenSource();
    var run = Drain(new HttpSseTransport(client).SendAsync(request, cancellation.Token));
    await body.ReadEntered.Task; cancellation.Cancel(); await ThrowsAsync<OperationCanceledException>(() => run);
    Assert(body.Disposed && content.Disposed, "Read cancellation leaked response/body."); Equal(1, handler.SendCalls);
}
static async Task HttpSuccessCleanup()
{
    var body = new HttpCleanupGateStream(Bytes("data:a\n\n")); var content = new StreamProbeContent(body);
    using var handler = new FakeHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
    using var client = new HttpClient(handler); using var request = HttpRequest();
    var run = Drain(new HttpSseTransport(client).SendAsync(request)); await body.CleanupEntered.Task;
    var premature = run.IsCompleted || content.Disposed || body.SyncDisposeCalls != 0;
    body.ReleaseCleanup.TrySetResult(); Equal("a", (await run).Single().Data);
    Assert(!premature, "Response disposed or send completed before asynchronous body cleanup.");
    Assert(body.Disposed && content.Disposed && !body.SyncDisposedBeforeAsync, "Cleanup order was reversed.");
    Equal(1, body.AsyncDisposeCalls); Equal(1, body.SyncDisposeCalls);
}
static async Task HttpEarlyCleanup()
{
    var body = new HttpCleanupGateStream(Bytes("data:a\n\ndata:b\n\n"), 8, 8); var content = new StreamProbeContent(body);
    using var handler = new FakeHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
    using var client = new HttpClient(handler); using var request = HttpRequest();
    var reader = new HttpSseTransport(client).SendAsync(request).GetAsyncEnumerator();
    Assert(await reader.MoveNextAsync(), "Missing HTTP event."); Equal(1, body.ReadCalls);
    var cleanup = reader.DisposeAsync().AsTask(); await body.CleanupEntered.Task;
    var premature = cleanup.IsCompleted || content.Disposed || body.SyncDisposeCalls != 0;
    body.ReleaseCleanup.TrySetResult(); await cleanup;
    Assert(!premature && !body.SyncDisposedBeforeAsync && content.Disposed, "Early disposal did not await body then response.");
    Equal(1, body.ReadCalls); Equal(1, body.AsyncDisposeCalls); Equal(1, body.SyncDisposeCalls);
}
static async Task HttpFaultCleanup(bool cancel)
{
    var body = new HttpCleanupGateStream([]) { BlockRead = cancel, FailRead = !cancel }; var content = new StreamProbeContent(body);
    using var handler = new FakeHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
    using var client = new HttpClient(handler); using var request = HttpRequest(); using var cancellation = new CancellationTokenSource();
    var run = Drain(new HttpSseTransport(client).SendAsync(request, cancellation.Token));
    if (cancel) { await body.ReadEntered.Task; cancellation.Cancel(); }
    await body.CleanupEntered.Task;
    var premature = run.IsCompleted || content.Disposed || body.SyncDisposeCalls != 0;
    body.ReleaseCleanup.TrySetResult();
    if (cancel) await ThrowsAsync<OperationCanceledException>(() => run); else await ThrowsAsync<IOException>(() => run);
    Assert(!premature && content.Disposed && !body.SyncDisposedBeforeAsync, "Fault propagated or response disposed before body cleanup.");
    Equal(1, body.AsyncDisposeCalls); Equal(1, body.SyncDisposeCalls); Equal(1, handler.SendCalls);
}
static async Task HttpRejection()
{
    foreach (var status in new[] { HttpStatusCode.MovedPermanently, HttpStatusCode.BadRequest, HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable })
    {
        var body = new ProbeStream(Bytes("secret-error-payload")); var content = new StreamProbeContent(body);
        using var handler = new FakeHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = content, ReasonPhrase = "secret-reason" }));
        using var client = new HttpClient(handler); using var request = HttpRequest();
        var error = await ThrowsAsync<HttpSseRejectedException>(() => Drain(new HttpSseTransport(client).SendAsync(request)));
        Equal<HttpStatusCode?>(status, error.StatusCode); Equal($"HTTP SSE request rejected with status {(int)status}.", error.Message);
        Equal(0, content.AcquireCalls); Equal(0, content.SerializeCalls); Equal(0, body.ReadCalls);
        Assert(content.Disposed, "Rejected response was retained."); Equal(1, handler.SendCalls);
    }
}
static async Task HttpReadFault()
{
    var body = new ReadFailureStream(); var content = new StreamProbeContent(body);
    using var handler = new FakeHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
    using var client = new HttpClient(handler); using var request = HttpRequest();
    await ThrowsAsync<IOException>(() => Drain(new HttpSseTransport(client).SendAsync(request)));
    Assert(body.Disposed && content.Disposed, "Read fault leaked response/body."); Equal(1, handler.SendCalls); Equal(1, content.DisposeCalls);
}
static async Task HttpDecoderFault()
{
    var body = new ProbeStream(Bytes("data:oversized\n\n")); var content = new StreamProbeContent(body);
    using var handler = new FakeHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
    using var client = new HttpClient(handler); using var request = HttpRequest();
    Equal(SseDecodeFailure.LineLimit, (await ThrowsAsync<SseDecodeException>(() => Drain(new HttpSseTransport(client, new(MaximumLineCharacters: 5)).SendAsync(request)))).Failure);
    Assert(body.Disposed && content.Disposed, "Framing fault leaked response/body."); Equal(1, handler.SendCalls);
}
static async Task HttpCleanupFault()
{
    var body = new HttpCleanupFailureStream(); var content = new StreamProbeContent(body);
    using var handler = new FakeHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
    using var client = new HttpClient(handler); using var request = HttpRequest();
    Equal("Synthetic asynchronous cleanup failure.", (await ThrowsAsync<IOException>(() => Drain(new HttpSseTransport(client).SendAsync(request)))).Message);
    Assert(content.Disposed, "Body cleanup fault skipped response disposal."); Equal(1, body.AsyncDisposeCalls); Equal(1, content.DisposeCalls);
}
static async Task HttpSendFault()
{
    using var handler = new FakeHttpHandler((_, _) => throw new HttpRequestException("Synthetic send fault."));
    using var client = new HttpClient(handler); using var request = HttpRequest(); request.Content = new StringContent("borrowed");
    await ThrowsAsync<HttpRequestException>(() => Drain(new HttpSseTransport(client).SendAsync(request)));
    Equal(1, handler.SendCalls); Assert(!handler.Disposed, "Send fault disposed borrowed client."); Equal("borrowed", await request.Content.ReadAsStringAsync());
}
static Task NodeUnavailable()
{
    foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        foreach (var executable in new[] { "node", "node.exe", "node.cmd", "node.bat" })
            Assert(!File.Exists(Path.Combine(directory, executable)), "Node exists on the transport test PATH.");
    return Task.CompletedTask;
}
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static void Equal<T>(T expected, T actual) => Assert(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual) => Assert(expected.SequenceEqual(actual), "Ordered sequence differs.");
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}
static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T exception) { return exception; } throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

class ProbeStream(byte[] bytes, params int[] chunks) : Stream
{
    private int _position;
    private int _chunkIndex;
    private int _remainingChunk;
    public bool Disposed { get; protected set; }
    public int ReadCalls { get; protected set; }
    public override bool CanRead => !Disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); ReadCalls++;
        if (_position == bytes.Length) return ValueTask.FromResult(0);
        if (_remainingChunk == 0) _remainingChunk = _chunkIndex < chunks.Length ? chunks[_chunkIndex++] : bytes.Length - _position;
        var count = Math.Min(buffer.Length, Math.Min(_remainingChunk, bytes.Length - _position));
        bytes.AsMemory(_position, count).CopyTo(buffer); _position += count; _remainingChunk -= count;
        return ValueTask.FromResult(count);
    }
    public override ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}
sealed class BlockingStream() : ProbeStream([])
{
    public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ReadCalls++; ReadEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); return 0;
    }
}
sealed class CleanupGateStream(byte[] bytes) : ProbeStream(bytes)
{
    public TaskCompletionSource CleanupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override async ValueTask DisposeAsync() { CleanupEntered.TrySetResult(); await ReleaseCleanup.Task; Disposed = true; }
}
sealed class ReadFailureStream() : ProbeStream([])
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new IOException("Synthetic read failure.");
}
sealed class FaultOnSecondReadStream(byte[] bytes) : ProbeStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (ReadCalls == 0) return base.ReadAsync(buffer, cancellationToken);
        ReadCalls++; throw new IOException("Synthetic second-read fault; the event budget should fail first.");
    }
}
sealed class FakeHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    public int SendCalls { get; private set; }
    public bool Disposed { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { SendCalls++; return send(request, cancellationToken); }
    protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
}
sealed class StreamProbeContent(Stream? body, Func<CancellationToken, Task<Stream>>? acquire = null) : HttpContent
{
    public int AcquireCalls { get; private set; }
    public int SerializeCalls { get; private set; }
    public int DisposeCalls { get; private set; }
    public bool Disposed => DisposeCalls != 0;
    public CancellationToken AcquisitionToken { get; private set; }
    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
    {
        AcquireCalls++; AcquisitionToken = cancellationToken;
        return acquire is null ? Task.FromResult(body!) : acquire(cancellationToken);
    }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    { SerializeCalls++; throw new InvalidOperationException("Response content was buffered before streaming."); }
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { DisposeCalls++; if (AcquireCalls == 0) body?.Dispose(); }
        base.Dispose(disposing);
    }
}
sealed class HttpCleanupGateStream(byte[] bytes, params int[] chunks) : ProbeStream(bytes, chunks)
{
    public bool BlockRead { get; init; }
    public bool FailRead { get; init; }
    public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CleanupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int AsyncDisposeCalls { get; private set; }
    public int SyncDisposeCalls { get; private set; }
    public bool SyncDisposedBeforeAsync { get; private set; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (FailRead) throw new IOException("Synthetic gated read fault.");
        if (BlockRead) { ReadEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
        return await base.ReadAsync(buffer, cancellationToken);
    }
    public override async ValueTask DisposeAsync()
    { AsyncDisposeCalls++; CleanupEntered.TrySetResult(); await ReleaseCleanup.Task; Disposed = true; }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { SyncDisposeCalls++; if (!Disposed) SyncDisposedBeforeAsync = true; }
        base.Dispose(disposing);
    }
}
sealed class HttpCleanupFailureStream() : ProbeStream([])
{
    public int AsyncDisposeCalls { get; private set; }
    public override ValueTask DisposeAsync() { AsyncDisposeCalls++; throw new IOException("Synthetic asynchronous cleanup failure."); }
}
