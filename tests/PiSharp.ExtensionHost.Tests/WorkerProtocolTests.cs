using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Contracts;
using PiSharp.ExtensionHost.Protocol;

internal static class WorkerProtocolTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly WorkerFrameCodec Codec = new();
    private static WorkerValue Json(string raw) => WorkerValue.FromJson(JsonData.Parse(raw));
    public static IEnumerable<(string Name, Func<Task> Run)> Cases() => Cases(FindRepository());
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string repositoryRoot)
    {
        yield return ("worker-protocol.authored-golden-frames-and-rejections", () => Fixtures(repositoryRoot));
        yield return ("worker-protocol.strict-unicode-number-depth-count-and-wire-schema", CodecAdmission);
        yield return ("worker-protocol.every-byte-fragments-lf-crlf-nul-and-own-values", Framing);
        yield return ("worker-protocol.handshake-feature-negotiation-and-pre-handshake-admission", Handshake);
        yield return ("worker-protocol.nested-request-callback-request-full-duplex-progress", Nested);
        yield return ("worker-protocol.response-and-pending-slot-wait-for-actual-flush", Settlement);
        yield return ("worker-protocol.cancelled-sent-call-retains-unknown-outcome-and-admission", Cancellation);
        yield return ("worker-protocol.cancellation-crossing-a-settled-reply-is-harmless", CrossingCancellation);
        yield return ("worker-protocol.cancelled-queued-call-is-not-sent-and-retains-until-skip", QueuedCancellation);
        yield return ("worker-protocol.slow-writer-count-and-cooperative-shared-disposal", WriteBound);
        yield return ("worker-protocol.owner-revocation-opaque-handle-fences-and-callback-fault", Handles);
        yield return ("worker-protocol.callback-admission-and-live-owner-revocation-retain-slots", CallbackBound);
        yield return ("worker-protocol.registration-owner-counts-and-late-context-rejection", HandleCounts);
        yield return ("worker-protocol.duplicate-response-and-stale-generation-fail-closed", Correlations);
        yield return ("worker-protocol.progress-and-retained-byte-bounds", RetainedBounds);
        yield return ("worker-protocol.eof-and-transport-fault-join-active-callbacks", EndAndFault);
        yield return ("worker-protocol.shutdown-borrowed-io-and-shared-cleanup", Shutdown);
    }
    private static async Task Fixtures(string repo)
    {
        using var good = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, "fixtures/native/node-worker-protocol/frames.json")));
        Equal(8, good.RootElement.GetProperty("frames").GetArrayLength());
        foreach (var frame in good.RootElement.GetProperty("frames").EnumerateArray())
        {
            var decoded = Codec.Decode(frame.GetRawText()); var encoded = Codec.Encode(decoded);
            Check(encoded.EndsWith('\n') && encoded.Count(c => c == '\n') == 1, "Encoder emitted multiple physical frames.");
            using var encodedDocument = JsonDocument.Parse(encoded);
            EqualJson(frame, encodedDocument.RootElement, "/frame");
            var round = Codec.Decode(encoded[..^1]); Equal(decoded.Kind, round.Kind);
            Equal(decoded.Value?.Presence, round.Value?.Presence);
            if (decoded.Value?.Json is { } expected) EqualJson(expected.Value, round.Value!.Json!.Value, "/value/data");
        }
        using var bad = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, "fixtures/native/node-worker-protocol/rejections.json")));
        Equal(9, bad.RootElement.GetProperty("cases").GetArrayLength());
        foreach (var vector in bad.RootElement.GetProperty("cases").EnumerateArray())
        {
            var wire = vector.GetProperty("wire").GetString()!;
            var failure = Enum.Parse<WorkerProtocolFailure>(vector.GetProperty("failure").GetString()!);
            var transport = new WorkerProtocolTransport(new StringReader(wire), new StringWriter());
            await Fails(failure, async () => { _ = await transport.ReadMessageAsync(); });
        }
    }
    private static Task CodecAdmission()
    {
        const string prefix = """{"version":1,"kind":"response","workerGeneration":3,"sessionGeneration":7,"id":1,"value":{"presence":"json","data":""";
        foreach (var (data, failure) in new[]
        {
            ("{\"a\":1,\"\\u0061\":2}", WorkerProtocolFailure.DuplicateProperty),
            ("\"\\uD800\"", WorkerProtocolFailure.InvalidUnicode), ("\"\\uDC00\"", WorkerProtocolFailure.InvalidUnicode),
            ("1e400", WorkerProtocolFailure.NonFiniteNumber), ("NaN", WorkerProtocolFailure.MalformedFrame),
            ("[1,]", WorkerProtocolFailure.MalformedFrame)
        }) Throws(failure, () => Codec.Decode(prefix + data + "}}"));
        var raw = prefix + """{"n":1.0,"tiny":1e-400,"nil":null,"s":"\u0000a\u2028b\u2029\uD83D\uDC4B"}"""+ "}}";
        var owned = Codec.Decode(raw).Value!.Json!;
        Equal("1.0", owned.Value.GetProperty("n").GetRawText()); Equal("1e-400", owned.Value.GetProperty("tiny").GetRawText());
        Equal("\0a\u2028b\u2029👋", owned.Value.GetProperty("s").GetString());
        Equal(raw + "\n", Codec.Encode(Codec.Decode(raw)));
        Throws(WorkerProtocolFailure.DepthLimit, () => new WorkerFrameCodec(new(MaximumJsonDepth: 2)).Decode(raw));
        Throws(WorkerProtocolFailure.ValueCountLimit, () => new WorkerFrameCodec(new(MaximumJsonValues: 2)).Decode(raw));
        Throws(WorkerProtocolFailure.FrameLimit, () => new WorkerFrameCodec(new(MaximumFrameBytes: Encoding.UTF8.GetByteCount(raw) - 1)).Decode(raw));
        Throws(WorkerProtocolFailure.InvalidUtf8, () => Codec.Decode(new byte[] { 0xC0, 0xAF }));
        Throws(WorkerProtocolFailure.InvalidUnicode, () => Codec.Decode(prefix + "\"" + '\uD800' + "\"}}"));
        Throws(WorkerProtocolFailure.InvalidEnvelope, () => Codec.Decode("""{"version":1,"kind":"cancel","workerGeneration":3,"sessionGeneration":7,"id":9007199254740992}"""));
        Throws(WorkerProtocolFailure.InvalidEnvelope, () => Codec.Decode("""{"version":1,"kind":"response","workerGeneration":3,"sessionGeneration":7,"id":1,"value":{"presence":"undefined","data":null}}"""));
        Throws(WorkerProtocolFailure.InvalidEnvelope, () => Codec.Encode(new(WorkerMessageKind.Response, 3, 7, 1,
            Value: WorkerValue.Absent, Features: ["ignored"])));
        Throws(WorkerProtocolFailure.InvalidEnvelope, () => Codec.Encode(new(WorkerMessageKind.Shutdown, 3, 7, 1)));
        using (var document = JsonDocument.Parse("""{"n":1.00}"""))
        {
            var value = WorkerValue.FromJson(JsonData.FromElement(document.RootElement));
            Equal("1.00", value.Json!.Value.GetProperty("n").GetRawText());
        }
        return Task.CompletedTask;
    }
    private static async Task Framing()
    {
        var frame = Codec.Encode(new(WorkerMessageKind.Response, 3, 7, 1,
            Value: Json("{\"s\":\"\\u0000\uD83D\uDC4Ba\u2028b\u2029\",\"n\":1.00}")));
        var raw = Encoding.UTF8.GetBytes(frame[..^1] + "\r\n" + frame);
        for (var chunk = 1; chunk <= raw.Length; chunk++)
        {
            using var input = new FragmentedStream(raw, chunk); using var output = new MemoryStream();
            var transport = new WorkerProtocolTransport(input, output, new(ReadBufferSize: 7));
            var first = await transport.ReadMessageAsync(); var second = await transport.ReadMessageAsync();
            Equal("\0👋a\u2028b\u2029", first!.Value!.Json!.Value.GetProperty("s").GetString());
            Equal("1.00", second!.Value!.Json!.Value.GetProperty("n").GetRawText()); Equal(null, await transport.ReadMessageAsync());
            Equal(0, input.DisposeCalls); // Borrowed input was not closed by transport.
        }
        await Fails(WorkerProtocolFailure.PartialFinalFrame, async () =>
        { _ = await new WorkerProtocolTransport(new StringReader(frame[..^1]), new StringWriter()).ReadMessageAsync(); });
        var exact = Encoding.UTF8.GetByteCount(frame) - 1;
        _ = await new WorkerProtocolTransport(new StringReader(frame[..^1] + "\r\n"), new StringWriter(),
            new(MaximumFrameBytes: exact)).ReadMessageAsync();
        await Fails(WorkerProtocolFailure.FrameLimit, async () =>
        { _ = await new WorkerProtocolTransport(new StringReader(frame), new StringWriter(), new(MaximumFrameBytes: exact - 1)).ReadMessageAsync(); });
    }
    private static async Task Handshake()
    {
        await using var pair = new Pair();
        Throws(WorkerProtocolFailure.Handshake, () => pair.A.StartRequest("early", WorkerValue.Absent));
        await pair.Start();
        Check(pair.A.Ready.IsCompletedSuccessfully && pair.B.Ready.IsCompletedSuccessfully, "Handshake did not settle both peers.");
        Throws(WorkerProtocolFailure.InvalidEnvelope, () => new WorkerProtocolConnection(
            new WorkerProtocolTransport(new StringReader(""), new StringWriter()), 0, 7));
        Throws(WorkerProtocolFailure.InvalidEnvelope, () => new WorkerProtocolConnection(
            new WorkerProtocolTransport(new StringReader(""), new StringWriter()), 3, 7,
            features: Enumerable.Repeat("requests", 17)));
        var invalid = new Pair(featuresB: ["requests"]); invalid.ExpectedA = invalid.ExpectedB = WorkerProtocolFailure.Handshake;
        await Fails(WorkerProtocolFailure.Handshake, invalid.Start); await invalid.DisposeAsync();
        var repeated = new Pair(); await repeated.Start(); repeated.ExpectedA = WorkerProtocolFailure.Handshake;
        await repeated.WriterB.WriteAsync(Codec.Encode(new(WorkerMessageKind.Hello, 3, 7, Features: ["requests", "cancel", "tagged-values"])));
        await Fails(WorkerProtocolFailure.Handshake, () => repeated.A.Completion); await repeated.DisposeAsync();
    }
    private static async Task Nested()
    {
        Pair? pair = null; var trace = new ConcurrentQueue<string>(); var progress = new List<WorkerValue>();
        WorkerCallbackHandle? callback = null;
        await using var active = pair = new Pair(handlerB: async (request, token) =>
        {
            trace.Enqueue("B." + request.Method);
            if (request.Method == "outer")
            {
                Equal(WorkerValuePresence.Undefined, request.Value.Presence);
                var value = await pair!.B.RequestAsync("callback", WorkerValue.Absent, callback, token);
                trace.Enqueue("B.outer.return"); return value;
            }
            Equal("leaf", request.Method);
            await request.ReportProgressAsync(Json("""{"step":1,"nil":null}"""), token);
            return Json("""{"text":"\u0000a\u2028b\u2029👋","n":1.0}""");
        });
        callback = active.A.RegisterCallback("fixture-extension", 2, async (request, token) =>
        {
            trace.Enqueue("A.callback"); Equal(WorkerValuePresence.Absent, request.Value.Presence);
            var leaf = active.A.StartRequest("leaf", Json("""{"opaque":null}"""), cancellationToken: token);
            await foreach (var update in leaf.Progress.WithCancellation(token)) progress.Add(update);
            var value = await leaf.Result; trace.Enqueue("A.callback.return"); return value;
        });
        await active.Start();
        var final = await active.A.RequestAsync("outer", WorkerValue.Undefined).WaitAsync(Deadline);
        Equal("\0a\u2028b\u2029👋", final.Json!.Value.GetProperty("text").GetString());
        Equal("1.0", final.Json.Value.GetProperty("n").GetRawText()); Equal(1, progress.Count);
        Check(trace.SequenceEqual(["B.outer", "A.callback", "B.leaf", "A.callback.return", "B.outer.return"]), "Required nested partial order changed.");
        await Until(() => active.A.Snapshot.PendingCalls == 0 && active.B.Snapshot.PendingCalls == 0 &&
            active.A.Snapshot.ActiveCallbacks == 0 && active.B.Snapshot.ActiveCallbacks == 0);
    }
    private static async Task Settlement()
    {
        await using var pair = new Pair(optionsA: new(MaximumPendingCalls: 1)); await pair.Start();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.WriterA.FlushGate = gate;
        var call = pair.A.StartRequest("echo", Json("""{"n":1}"""));
        await Until(() => pair.WriterB.Frames.Any(text => text.Contains("\"kind\":\"response\"", StringComparison.Ordinal)));
        await Until(() => pair.A.Snapshot.BufferedBytes > pair.WriterA.Frames.Last().Length);
        Equal(1, pair.A.Snapshot.PendingCalls); Equal(1, pair.A.Snapshot.PendingWrites);
        Check(!call.Result.IsCompleted, "Response released admission before actual request flush.");
        Throws(WorkerProtocolFailure.PendingLimit, () => pair.A.StartRequest("second", WorkerValue.Absent));
        pair.WriterA.FlushGate = null; gate.SetResult(); _ = await call.Result.WaitAsync(Deadline);
        await Until(() => pair.A.Snapshot.PendingCalls == 0 && pair.A.Snapshot.PendingWrites == 0);
    }
    private static async Task Cancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pair = new Pair(optionsA: new(MaximumPendingCalls: 1), handlerB: async (_, _) =>
        { entered.TrySetResult(); await release.Task; return WorkerValue.Undefined; });
        await pair.Start(); using var stop = new CancellationTokenSource();
        var call = pair.A.StartRequest("held", WorkerValue.Absent, cancellationToken: stop.Token);
        await entered.Task.WaitAsync(Deadline); stop.Cancel();
        var error = await Fails(WorkerProtocolFailure.Cancelled, () => call.Result); Equal(WorkerOutcome.Unknown, error.Outcome);
        Equal(1, pair.A.Snapshot.PendingCalls); Equal(1, pair.B.Snapshot.ActiveCallbacks);
        Throws(WorkerProtocolFailure.PendingLimit, () => pair.A.StartRequest("overbound", WorkerValue.Absent));
        release.SetResult();
        await Until(() => pair.A.Snapshot.PendingCalls == 0 && pair.B.Snapshot.ActiveCallbacks == 0);
        _ = await pair.A.RequestAsync("after", WorkerValue.Absent).WaitAsync(Deadline);
    }
    private static async Task QueuedCancellation()
    {
        await using var pair = new Pair(optionsA: new(MaximumPendingCalls: 2)); await pair.Start();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.WriterA.WriteGate = gate;
        var first = pair.A.StartRequest("first", WorkerValue.Absent);
        await Until(() => pair.WriterA.BlockedWrites == 1);
        using var stop = new CancellationTokenSource();
        var queued = pair.A.StartRequest("queued", WorkerValue.Undefined, cancellationToken: stop.Token); stop.Cancel();
        var error = await Fails(WorkerProtocolFailure.Cancelled, () => queued.Result); Equal(WorkerOutcome.NotSent, error.Outcome);
        Equal(2, pair.A.Snapshot.PendingCalls);
        Throws(WorkerProtocolFailure.PendingLimit, () => pair.A.StartRequest("third", WorkerValue.Absent));
        pair.WriterA.WriteGate = null; gate.SetResult(); _ = await first.Result.WaitAsync(Deadline);
        await Until(() => pair.A.Snapshot.PendingCalls == 0);
        Check(!pair.WriterA.Frames.Any(text => text.Contains("\"method\":\"queued\"", StringComparison.Ordinal)), "Cancelled unsent request reached I/O.");
    }
    private static async Task CrossingCancellation()
    {
        await using var pair = new Pair(); await pair.Start();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.WriterA.FlushGate = gate; using var stop = new CancellationTokenSource();
        var call = pair.A.StartRequest("already-replied", WorkerValue.Absent, cancellationToken: stop.Token);
        await Until(() => pair.WriterB.Frames.Any(text => text.Contains("\"kind\":\"response\"", StringComparison.Ordinal)) &&
            pair.B.Snapshot.ActiveCallbacks == 0 && pair.A.Snapshot.BufferedBytes > pair.WriterA.Frames.Last().Length);
        stop.Cancel(); var cancelled = await Fails(WorkerProtocolFailure.Cancelled, () => call.Result);
        Equal(WorkerOutcome.Unknown, cancelled.Outcome);
        pair.WriterA.FlushGate = null; gate.SetResult();
        await Until(() => pair.A.Snapshot.PendingCalls == 0 && pair.A.Snapshot.PendingWrites == 0);
        // Its late cancel is ahead of this request on the same ordered writer.
        Equal(WorkerValuePresence.Undefined, (await pair.A.RequestAsync("next", WorkerValue.Undefined).WaitAsync(Deadline)).Presence);
        Check(!pair.B.Completion.IsCompleted, "Late cancellation poisoned the peer after an actual reply.");
        await pair.WriterA.WriteAsync(Codec.Encode(new(WorkerMessageKind.Cancel, 3, 7, 999)));
        pair.ExpectedB = WorkerProtocolFailure.Correlation;
        await Fails(WorkerProtocolFailure.Correlation, () => pair.B.Completion);
    }
    private static async Task WriteBound()
    {
        var pair = new Pair(optionsA: new(MaximumPendingWrites: 1)); await pair.Start();
        pair.WriterA.WriteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = pair.A.StartRequest("blocked", WorkerValue.Absent);
        await Until(() => pair.WriterA.BlockedWrites == 1);
        Throws(WorkerProtocolFailure.WriteLimit, () => pair.A.StartRequest("overbound", WorkerValue.Absent));
        Equal(1, pair.A.Snapshot.PendingWrites); Equal(1, pair.A.Snapshot.PendingCalls);
        var one = pair.A.DisposeAsync().AsTask(); var two = pair.A.DisposeAsync().AsTask(); Check(ReferenceEquals(one, two), "Dispose did not share cleanup.");
        await one.WaitAsync(Deadline); var failure = await Fails(WorkerProtocolFailure.Closed, () => call.Result); Equal(WorkerOutcome.Unknown, failure.Outcome);
        Equal(0, pair.A.Snapshot.PendingWrites); await pair.DisposeAsync();
    }
    private static async Task Handles()
    {
        var invoked = 0;
        await using var pair = new Pair(); var handle = pair.A.RegisterCallback("owner", 1, (_, _) => { invoked++; return ValueTask.FromResult(WorkerValue.Undefined); });
        await pair.Start();
        Equal(WorkerValuePresence.Undefined, (await pair.B.RequestAsync("callback", WorkerValue.Absent, handle)).Presence); Equal(1, invoked);
        Check(pair.A.RevokeOwner("owner", 1), "Expected owner revocation failed.");
        var stale = await Fails(WorkerProtocolFailure.RemoteError, () => pair.B.RequestAsync("callback", WorkerValue.Absent, handle));
        Equal("StaleHandle", stale.RemoteCode); Equal(WorkerOutcome.NotSent, stale.Outcome); Equal(1, invoked);
        Throws(WorkerProtocolFailure.StaleGeneration, () => pair.A.RegisterCallback("owner", 1, (_, _) => ValueTask.FromResult(WorkerValue.Absent)));
        var renewed = pair.A.RegisterCallback("owner", 2, (_, _) => throw new InvalidOperationException("fixture-secret-not-on-wire"));
        Check(renewed.CallbackId != handle.CallbackId && renewed.RegistrationId != handle.RegistrationId, "Opaque IDs were reused.");
        var error = await Fails(WorkerProtocolFailure.RemoteError, () => pair.B.RequestAsync("callback", WorkerValue.Absent, renewed));
        Equal("CallbackFailed", error.RemoteCode); Equal(WorkerOutcome.Unknown, error.Outcome);
        Check(!pair.WriterA.Frames.Any(text => text.Contains("fixture-secret", StringComparison.Ordinal)), "Callback exception text leaked to protocol.");
        Check(pair.A.RevokeOwner("owner", 2), "Expected renewed owner revocation failed.");
        var foreign = pair.A.RegisterCallback("owner", 3, (_, _) => throw new OperationCanceledException("foreign token"));
        var foreignError = await Fails(WorkerProtocolFailure.RemoteError, () => pair.B.RequestAsync("callback", WorkerValue.Absent, foreign));
        Equal("CallbackFailed", foreignError.RemoteCode); Equal(WorkerOutcome.Unknown, foreignError.Outcome);
    }
    private static async Task Correlations()
    {
        var duplicate = new Pair(); await duplicate.Start(); _ = await duplicate.A.RequestAsync("echo", WorkerValue.Absent);
        duplicate.ExpectedA = WorkerProtocolFailure.Correlation;
        await duplicate.WriterB.WriteAsync(Codec.Encode(new(WorkerMessageKind.Response, 3, 7, 1, Value: WorkerValue.Absent)));
        await Fails(WorkerProtocolFailure.Correlation, () => duplicate.A.Completion); await duplicate.DisposeAsync();
        var stale = new Pair(); await stale.Start(); stale.ExpectedA = WorkerProtocolFailure.StaleGeneration;
        await stale.WriterB.WriteAsync(Codec.Encode(new(WorkerMessageKind.Request, 3, 6, 1, "never", WorkerValue.Absent)));
        await Fails(WorkerProtocolFailure.StaleGeneration, () => stale.A.Completion); Equal(0, stale.A.Snapshot.ActiveCallbacks); await stale.DisposeAsync();
        var replaced = new Pair(); await replaced.Start(); replaced.ExpectedA = WorkerProtocolFailure.StaleGeneration;
        await Fails(WorkerProtocolFailure.StaleGeneration, () => replaced.A.InvalidateSessionAsync(8).AsTask());
        Throws(WorkerProtocolFailure.StaleGeneration, () => replaced.A.StartRequest("stale", WorkerValue.Absent)); await replaced.DisposeAsync();
    }
    private static async Task CallbackBound()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pair = new Pair(optionsA: new(MaximumCallbacks: 1));
        var handle = pair.A.RegisterCallback("owner", 1, async (_, _) =>
        { entered.TrySetResult(); await release.Task; return WorkerValue.Undefined; });
        await pair.Start(); var first = pair.B.StartRequest("held", WorkerValue.Absent, handle);
        await entered.Task.WaitAsync(Deadline);
        var overbound = await Fails(WorkerProtocolFailure.RemoteError, () => pair.B.RequestAsync("second", WorkerValue.Absent, handle));
        Equal("CallbackLimit", overbound.RemoteCode); Equal(WorkerOutcome.NotSent, overbound.Outcome);
        Check(pair.A.RevokeOwner("owner", 1), "Active owner was not revoked."); Equal(1, pair.A.Snapshot.ActiveCallbacks);
        var renewed = pair.A.RegisterCallback("owner", 2, (_, _) => ValueTask.FromResult(WorkerValue.Absent));
        Equal(1, pair.A.Snapshot.ActiveCallbacks); Check(renewed.CallbackId != handle.CallbackId, "Renewal reused a callback identity.");
        release.SetResult(); var cancelled = await Fails(WorkerProtocolFailure.RemoteError, () => first.Result);
        Equal("Cancelled", cancelled.RemoteCode); Equal(WorkerOutcome.Unknown, cancelled.Outcome);
        await Until(() => pair.A.Snapshot.ActiveCallbacks == 0);
    }
    private static async Task HandleCounts()
    {
        WorkerRequestContext? retained = null;
        await using var pair = new Pair(optionsA: new(MaximumHandles: 1, MaximumOwners: 1));
        var handle = pair.A.RegisterCallback("owner", 1, (request, _) => { retained = request; return ValueTask.FromResult(WorkerValue.Absent); });
        Throws(WorkerProtocolFailure.HandleLimit, () => pair.A.RegisterCallback("owner", 1, (_, _) => ValueTask.FromResult(WorkerValue.Absent)));
        await pair.Start(); _ = await pair.B.RequestAsync("save-context", WorkerValue.Absent, handle);
        await Until(() => pair.A.Snapshot.ActiveCallbacks == 0);
        var before = pair.WriterA.Frames.Count;
        await Fails(WorkerProtocolFailure.Correlation, () => retained!.ReportProgressAsync(WorkerValue.Undefined).AsTask());
        Equal(before, pair.WriterA.Frames.Count);
        Check(pair.A.RevokeOwner("owner", 1), "Expected revocation failed.");
        Throws(WorkerProtocolFailure.HandleLimit, () => pair.A.RegisterCallback("other-owner", 1, (_, _) => ValueTask.FromResult(WorkerValue.Absent)));
        _ = pair.A.RegisterCallback("owner", 2, (_, _) => ValueTask.FromResult(WorkerValue.Undefined));
    }
    private static async Task RetainedBounds()
    {
        var pair = new Pair(optionsA: new(MaximumProgressPerCall: 1), handlerB: async (request, token) =>
        { await request.ReportProgressAsync(Json("""{"step":1}"""), token); await request.ReportProgressAsync(Json("""{"step":2}"""), token); return WorkerValue.Absent; });
        await pair.Start(); pair.ExpectedA = WorkerProtocolFailure.ProgressLimit;
        var call = pair.A.StartRequest("progress", WorkerValue.Absent);
        await Fails(WorkerProtocolFailure.ProgressLimit, () => call.Result); await pair.DisposeAsync();
        var tight = new Pair(optionsA: new(MaximumBufferedBytes: 400)); await tight.Start();
        Throws(WorkerProtocolFailure.BufferedLimit, () => tight.A.StartRequest("large", Json("{\"s\":\"" + new string('x', 500) + "\"}")));
        Equal(0, tight.A.Snapshot.PendingCalls); Equal(0, tight.A.Snapshot.PendingWrites); await tight.DisposeAsync();
        await using var readerPair = new Pair(); await readerPair.Start();
        var settled = readerPair.A.StartRequest("one-reader", WorkerValue.Absent); _ = await settled.Result.WaitAsync(Deadline);
        await using var firstReader = settled.Progress.GetAsyncEnumerator(); Equal(false, await firstReader.MoveNextAsync());
        await using var secondReader = settled.Progress.GetAsyncEnumerator();
        try { _ = await secondReader.MoveNextAsync(); throw new Exception("Second progress reader was admitted."); }
        catch (InvalidOperationException error) { Equal("A worker call has one progress reader.", error.Message); }
    }
    private static async Task EndAndFault()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pair = new Pair(handlerA: async (_, token) =>
        { entered.TrySetResult(); try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return WorkerValue.Absent; } finally { exited.TrySetResult(); } });
        await pair.Start(); var call = pair.B.StartRequest("held", WorkerValue.Absent);
        await entered.Task.WaitAsync(Deadline); pair.ExpectedA = WorkerProtocolFailure.EndOfInput; pair.ReaderA.End();
        await Fails(WorkerProtocolFailure.EndOfInput, () => pair.A.Completion); await exited.Task.WaitAsync(Deadline);
        Equal(0, pair.A.Snapshot.ActiveCallbacks); await pair.DisposeAsync(); await Fails(WorkerProtocolFailure.Closed, () => call.Result);
        var broken = new Pair(); await broken.Start(); broken.WriterA.ThrowWrites = true; broken.ExpectedA = WorkerProtocolFailure.Transport;
        var sent = broken.A.StartRequest("fault", WorkerValue.Absent);
        var error = await Fails(WorkerProtocolFailure.Transport, () => sent.Result); Equal(WorkerOutcome.Unknown, error.Outcome);
        await Fails(WorkerProtocolFailure.Transport, () => broken.A.Completion); await broken.DisposeAsync();
    }
    private static async Task Shutdown()
    {
        var pair = new Pair(); await pair.Start(); var observed = pair.A.Completion;
        Check(!observed.IsCompleted, "Connection completion should be observable before termination.");
        await pair.A.ShutdownAsync().AsTask().WaitAsync(Deadline);
        await pair.B.Completion.WaitAsync(Deadline); await pair.DisposeAsync();
        Equal(0, pair.ReaderA.DisposeCalls); Equal(0, pair.ReaderB.DisposeCalls); Equal(0, pair.WriterA.DisposeCalls); Equal(0, pair.WriterB.DisposeCalls);
    }

    internal sealed class Pair : IAsyncDisposable
    {
        public PipeReader ReaderA { get; } = new(); public PipeReader ReaderB { get; } = new();
        public PipeWriter WriterA { get; } public PipeWriter WriterB { get; }
        public WorkerProtocolConnection A { get; } public WorkerProtocolConnection B { get; }
        public WorkerProtocolFailure? ExpectedA, ExpectedB;
        public Pair(WorkerRequestHandler? handlerA = null, WorkerRequestHandler? handlerB = null,
            WorkerProtocolOptions? optionsA = null, WorkerProtocolOptions? optionsB = null, string[]? featuresB = null)
        {
            WriterA = new(ReaderB); WriterB = new(ReaderA);
            A = new(new WorkerProtocolTransport(ReaderA, WriterA, optionsA), 3, 7, handlerA ?? Echo, optionsA);
            B = new(new WorkerProtocolTransport(ReaderB, WriterB, optionsB), 3, 7, handlerB ?? Echo, optionsB, featuresB);
        }
        private static ValueTask<WorkerValue> Echo(WorkerRequestContext request, CancellationToken _) => ValueTask.FromResult(request.Value);
        public Task Start() => Task.WhenAll(A.StartAsync(), B.StartAsync()).WaitAsync(Deadline);
        public async ValueTask DisposeAsync()
        {
            var a = A.DisposeAsync().AsTask(); var b = B.DisposeAsync().AsTask();
            await CheckCleanup(a, ExpectedA); await CheckCleanup(b, ExpectedB);
            Equal(0, ReaderA.DisposeCalls); Equal(0, ReaderB.DisposeCalls); Equal(0, WriterA.DisposeCalls); Equal(0, WriterB.DisposeCalls);
        }
        private static async Task CheckCleanup(Task task, WorkerProtocolFailure? expected)
        { if (expected is { } failure) await Fails(failure, () => task); else await task.WaitAsync(Deadline); }
    }
    internal sealed class PipeReader : TextReader
    {
        private readonly Channel<char> _input = Channel.CreateBounded<char>(16_384);
        public int DisposeCalls;
        public async ValueTask Add(ReadOnlyMemory<char> text, CancellationToken token)
        { for (var i = 0; i < text.Length; i++) await _input.Writer.WriteAsync(text.Span[i], token); }
        public void End() => _input.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (!await _input.Reader.WaitToReadAsync(cancellationToken)) return 0;
            var length = 0; while (length < buffer.Length && _input.Reader.TryRead(out var c)) buffer.Span[length++] = c; return length;
        }
        protected override void Dispose(bool disposing) { if (disposing) DisposeCalls++; base.Dispose(disposing); }
    }
    internal sealed class PipeWriter(PipeReader destination) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public ConcurrentQueue<string> Frames { get; } = new();
        public TaskCompletionSource? WriteGate, FlushGate;
        public TaskCompletionSource? WriteEntered, FlushEntered, WriteFinished, FlushFinished;
        public int BlockedWrites, DisposeCalls; public bool ThrowWrites;
        public override async Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            WriteEntered?.TrySetResult();
            if (ThrowWrites) throw new IOException("synthetic broken output");
            if (WriteGate is { } gate)
            { Interlocked.Increment(ref BlockedWrites); await gate.Task.WaitAsync(cancellationToken); }
            await destination.Add(buffer, cancellationToken); Frames.Enqueue(buffer.ToString()); WriteFinished?.TrySetResult();
        }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushEntered?.TrySetResult();
            if (FlushGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
            FlushFinished?.TrySetResult();
        }
        public override Task WriteAsync(string? value) => WriteAsync((value ?? "").AsMemory());
        protected override void Dispose(bool disposing) { if (disposing) DisposeCalls++; base.Dispose(disposing); }
    }
    private sealed class FragmentedStream(byte[] bytes, int chunk) : MemoryStream(bytes, writable: false)
    {
        public int DisposeCalls;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) DisposeCalls++; base.Dispose(disposing); }
    }
    private static string FindRepository()
    {
        for (var candidate = new DirectoryInfo(Directory.GetCurrentDirectory()); candidate is not null; candidate = candidate.Parent)
            if (File.Exists(Path.Combine(candidate.FullName, "schemas/extension-worker-v1.schema.json"))) return candidate.FullName;
        throw new InvalidOperationException("Run tests from the repository or call Cases(repositoryRoot).");
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        while (!condition()) { timeout.Token.ThrowIfCancellationRequested(); await Task.Delay(1, timeout.Token); }
    }
    private static void Throws(WorkerProtocolFailure expected, Action action)
    {
        try { action(); throw new InvalidOperationException("Expected protocol failure: " + expected); }
        catch (WorkerProtocolException error) { Equal(expected, error.Failure); }
    }
    private static async Task<WorkerProtocolException> Fails(WorkerProtocolFailure expected, Func<Task> action)
    {
        try { await action().WaitAsync(Deadline); throw new InvalidOperationException("Expected protocol failure: " + expected); }
        catch (WorkerProtocolException error) { Equal(expected, error.Failure); return error; }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void EqualJson(JsonElement expected, JsonElement actual, string pointer)
    {
        Equal(expected.ValueKind, actual.ValueKind);
        if (expected.ValueKind == JsonValueKind.Object)
        {
            var fields = expected.EnumerateObject().ToArray(); var received = actual.EnumerateObject().ToArray();
            Check(fields.Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(received.Select(p => p.Name).Order(StringComparer.Ordinal)),
                "Complete fieldset differs at " + pointer + ": " + expected.GetRawText() + " versus " + actual.GetRawText());
            foreach (var field in fields) EqualJson(field.Value, actual.GetProperty(field.Name), pointer + "/" + field.Name);
        }
        else if (expected.ValueKind == JsonValueKind.Array)
        {
            Equal(expected.GetArrayLength(), actual.GetArrayLength());
            for (var i = 0; i < expected.GetArrayLength(); i++) EqualJson(expected[i], actual[i], pointer + "/" + i);
        }
        else if (expected.ValueKind == JsonValueKind.String) Equal(expected.GetString(), actual.GetString());
        else Equal(expected.GetRawText(), actual.GetRawText());
    }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
}
