using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.AI.Transports;
using PiSharp.Contracts;

internal static class CompletionsBodyReadResultTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static readonly ModelDescriptor Model = new("body-result", "openai-completions", "openai");
    private const string Finish = "{\"choices\":[{\"delta\":{\"content\":\"\\u03c0\\ud83d\\ude00\"},\"finish_reason\":\"stop\"}]}";

    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        foreach (var test in CompletionsProviderSnapshotTests.Cases()) yield return test;
        foreach (var test in CompletionsResultExecutorTests.Cases()) yield return test;
        yield return ("completions-body-result.owned-bytes-absent-eof-and-repeat-without-read", OwnedResults);
        yield return ("completions-body-result.genuine-pinned-every-byte-and-eof-dtos", FrozenReadResults);
        yield return ("completions-body-result.inclusive-read-bound-and-invalid-count-admission", ReadBounds);
        yield return ("completions-body-result.single-active-read-cancel-release-and-self-join", Admission);
        yield return ("completions-body-result.cooperative-and-held-faulted-cancel-share-settlement", Cancellation);
        yield return ("completions-body-result.cancellation-closure-is-not-physical-eof-or-foreign-failure", CancellationClosure);
        yield return ("completions-body-result.actual-http-done-eof-tail-and-held-physical-cleanup", HttpResults);
        yield return ("completions-body-result.observer-fault-invalid-count-and-physical-fault-remain-fatal", HttpFaults);
    }

    private static async Task OwnedResults()
    {
        var calls = 0; Memory<byte> retained = default;
        var executor = new Executor((destination, _) =>
        {
            retained = destination;
            var bytes = ++calls == 1 ? new byte[] { 0, 127, 128, 255 } : calls == 2 ? new byte[] { 42 } : [];
            bytes.CopyTo(destination); return ValueTask.FromResult(bytes.Length);
        });
        var reader = new CompletionsBodyReader(executor, 4);
        try
        {
            var first = await reader.ReadAsync(); retained.Span.Fill(9);
            Check(!first.Done && first.Value.SequenceEqual(new byte[] { 0, 127, 128, 255 }), "The result borrowed the executor's mutable buffer.");
            EqualJson(JsonData.Parse("{\"value\":{\"value\":{\"0\":0,\"1\":127,\"2\":128,\"3\":255},\"done\":false},\"ownUndefinedPaths\":[]}").Value,
                first.Snapshot.Value);
            var second = await reader.ReadAsync(); var end = await reader.ReadAsync(); var again = await reader.ReadAsync();
            Check(second.Value.SequenceEqual(new byte[] { 42 }) && !second.Done && end.Done && end.Value.IsEmpty &&
                ReferenceEquals(end, again) && calls == 3 && reader.ReachedEof && !reader.Released && executor.Cancels == 0,
                "EOF presence, repeated read or independent lease authority changed.");
            EqualJson(JsonData.Parse("{\"value\":{\"done\":true},\"ownUndefinedPaths\":[\"/value\"]}").Value, end.Snapshot.Value);
            Check(first.Value.SequenceEqual(new byte[] { 0, 127, 128, 255 }), "A later read changed an owned earlier DTO.");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Throws<OperationCanceledException>(() => reader.ReadAsync(canceled.Token).AsTask());
            Check(calls == 3, "A canceled repeated-EOF read reached the executor.");
        }
        finally { reader.Release(); }
        Check(executor.Releases == 1, "Natural EOF fabricated cancellation or skipped release.");
    }

    private static async Task FrozenReadResults()
    {
        var file = Path.Combine(FindRepo(), "fixtures/reference/openai-completions-sdk-lifecycle/expected.json");
        var bytes = File.ReadAllBytes(file);
        Check(bytes.Length == 723_445 && Convert.ToHexStringLower(SHA256.HashData(bytes)) ==
            "829ec6d61f05e0064b6ca38799e264b6b4a6f7a2029d4f499e81d84b5475b81b", "The genuine SDK reference changed.");
        using var document = JsonDocument.Parse(bytes);
        Check(document.RootElement.GetProperty("sourceSha").GetString() == "d86654abb8862e201933517d6f1fce9f88dd117f", "Wrong source authority.");
        var source = document.RootElement.GetProperty("observations").GetProperty("cases")[6];
        Check(source.GetProperty("caseId").GetString() == "eof-later-bom-replacement-utf8-every-byte", "Wrong complete EOF fixture.");
        using var body = new MemoryStream(Convert.FromHexString(source.GetProperty("responseWire").GetProperty("hex").GetString()!));
        var reader = new CompletionsBodyReader(CompletionsResponseBodyReader.FromStream(body), 1);
        var expected = source.GetProperty("readerLedger").EnumerateArray()
            .Where(row => row.GetProperty("operation").GetString() == "reader-read-result").Select(row => row.GetProperty("result")).ToArray();
        try
        {
            for (var index = 0; index < expected.Length; index++)
            {
                var actual = await reader.ReadAsync(); var value = actual.Snapshot.Value;
                EqualJson(expected[index].GetProperty("value"), value.GetProperty("value"));
                EqualJson(expected[index].GetProperty("ownUndefinedPaths"), value.GetProperty("ownUndefinedPaths"));
                Check(actual.Done == (index == expected.Length - 1), "A source result was omitted or EOF invented.");
                var numbers = expected[index].GetProperty("numberBits").EnumerateArray().ToArray();
                Check(numbers.Length == actual.Value.Length, "The complete source numeric sidecar was not compared.");
                for (var offset = 0; offset < numbers.Length; offset++)
                    Check(numbers[offset].GetProperty("path").GetString() == "/value/" + offset &&
                        numbers[offset].GetProperty("hex").GetString() == BitConverter.DoubleToUInt64Bits(actual.Value[offset]).ToString("x16"),
                        "The actual DTO changed a source byte's binary64 observation.");
            }
            Check(body.Position == body.Length && reader.ReachedEof, "The bounded native read did not consume the complete actual source wire.");
        }
        finally { reader.Release(); }
        Check(body.CanRead, "Result-reader release disposed the separately owned physical body.");
    }

    private static async Task ReadBounds()
    {
        foreach (var maximum in new[] { 1, 4096, 65_536 })
        {
            var executor = new Executor((destination, _) =>
            { Check(destination.Length == maximum, "The executor received an unbounded/different destination."); destination.Span.Fill(255); return ValueTask.FromResult(maximum); });
            var reader = new CompletionsBodyReader(executor, maximum);
            try
            {
                var result = await reader.ReadAsync();
                Check(!result.Done && result.Value.Length == maximum && result.Value.All(value => value == 255), "Inclusive read bound lost actual bytes.");
                var snapshot = result.Snapshot;
                Check(snapshot.ToString().Length < 1_048_576 && snapshot.Value.GetProperty("value").GetProperty("value").EnumerateObject().Count() == maximum,
                    "The bounded JSON read view truncated byte members at a decoder-valid bound.");
            }
            finally { reader.Release(); }
        }
        foreach (var count in new[] { -1, 9 })
        {
            var executor = new Executor((_, _) => ValueTask.FromResult(count)); var reader = new CompletionsBodyReader(executor, 8);
            try { await Throws<InvalidOperationException>(() => reader.ReadAsync().AsTask()); Check(!reader.ReachedEof, "Invalid count became EOF."); }
            finally { await reader.CancelAsync(); reader.Release(); }
            Check(executor.Reads == 1 && executor.Cancels == 1 && executor.Releases == 1, "Invalid count abandoned owned cleanup.");
        }
        foreach (var maximum in new[] { 0, 65_537 })
            Denied<ArgumentOutOfRangeException>(() => _ = new CompletionsBodyReader(new Executor((_, _) => ValueTask.FromResult(0)), maximum));
    }

    private static async Task Admission()
    {
        var entered = Gate(); var release = Gate(); CompletionsBodyReader? reader = null;
        var executor = new Executor(async (destination, token) =>
        {
            Denied<InvalidOperationException>(() => _ = reader!.CancelAsync());
            Denied<InvalidOperationException>(() => reader!.Release());
            entered.TrySetResult(); await release.Task; destination.Span[0] = 7; return 1;
        });
        reader = new(executor, 1); var pending = reader.ReadAsync().AsTask();
        try
        {
            await entered.Task.WaitAsync(Deadline);
            await Throws<InvalidOperationException>(() => reader.ReadAsync().AsTask());
            Denied<InvalidOperationException>(reader.Release);
            var first = reader.CancelAsync().AsTask(); var second = reader.CancelAsync().AsTask();
            Check(ReferenceEquals(first, second) && !first.IsCompleted && executor.Reads == 1 && executor.Cancels == 1,
                "Cancellation did not retain the actual outstanding DTO read or share settlement.");
            Denied<InvalidOperationException>(reader.Release); release.TrySetResult();
            Check((await pending.WaitAsync(Deadline)).Value.Single() == 7, "Cancellation fabricated a result for a noncooperative read.");
            await Task.WhenAll(first, second).WaitAsync(Deadline); reader.Release();
            await Throws<InvalidOperationException>(() => reader.ReadAsync().AsTask());
            await Throws<InvalidOperationException>(() => reader.CancelAsync().AsTask());
            Denied<InvalidOperationException>(reader.Release);
        }
        finally { release.TrySetResult(); await Observe(pending); if (!reader.Released) { await reader.CancelAsync(); reader.Release(); } }
        Check(executor.Releases == 1, "Late authority reached the executor.");
    }

    private static async Task Cancellation()
    {
        using (var stop = new CancellationTokenSource())
        {
            var entered = Gate();
            var executor = new Executor(async (_, _) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, stop.Token); return 0; },
                () => { stop.Cancel(); return ValueTask.CompletedTask; });
            var reader = new CompletionsBodyReader(executor, 1); var read = reader.ReadAsync().AsTask();
            await entered.Task.WaitAsync(Deadline); await reader.CancelAsync();
            var closed = await read.WaitAsync(Deadline);
            Check(closed.Done && closed.Value.IsEmpty && !reader.ReachedEof && read.IsCompleted && executor.Cancels == 1 &&
                ReferenceEquals(closed, await reader.ReadAsync()), "Cooperative cancellation lost the closed DTO, invented physical EOF or left a read running.");
            EqualJson(JsonData.Parse("{\"value\":{\"done\":true},\"ownUndefinedPaths\":[\"/value\"]}").Value, closed.Snapshot.Value);
            reader.Release();
        }
        var release = Gate(); var held = Gate();
        var faulty = new Executor(async (destination, _) => { held.TrySetResult(); await release.Task; destination.Span[0] = 8; return 1; },
            () => ValueTask.FromException(new IOException("private-body-result-marker")));
        var owner = new CompletionsBodyReader(faulty, 1); var actual = owner.ReadAsync().AsTask();
        try
        {
            await held.Task.WaitAsync(Deadline); var cancellation = owner.CancelAsync().AsTask();
            Check(!cancellation.IsCompleted && !actual.IsCompleted, "Faulted cancel abandoned an already admitted read.");
            Denied<InvalidOperationException>(owner.Release); release.TrySetResult();
            Check((await actual.WaitAsync(Deadline)).Value.Single() == 8, "Actual noncooperative read bytes were changed.");
            await Throws<IOException>(() => cancellation); owner.Release();
            Check(faulty.Releases == 1, "Faulted cancellation skipped independent release.");
        }
        finally { release.TrySetResult(); await Observe(actual); if (!owner.Released) { await Observe(owner.CancelAsync().AsTask()); owner.Release(); } }
    }

    private static async Task CancellationClosure()
    {
        using var caller = new CancellationTokenSource(); var entered = Gate();
        var executor = new Executor(async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return 0; });
        var reader = new CompletionsBodyReader(executor, 1); var pending = reader.ReadAsync(caller.Token).AsTask();
        await entered.Task.WaitAsync(Deadline); caller.Cancel();
        var result = await pending.WaitAsync(Deadline);
        Check(result.Done && result.Value.IsEmpty && !reader.ReachedEof && executor.Cancels == 0,
            "The completed canceled read fabricated physical EOF or an executor cancellation effect.");
        await reader.CancelAsync(); reader.Release();
        Check(executor.Cancels == 1 && executor.Releases == 1, "Cancellation closure caused the owner to skip actual cancel/release.");

        var idle = new Executor((_, _) => throw new InvalidOperationException("Canceled idle reader must not read."));
        var idleReader = new CompletionsBodyReader(idle, 1); await idleReader.CancelAsync();
        Check((await idleReader.ReadAsync()).Done && !idleReader.ReachedEof && idle.Reads == 0 && idle.Cancels == 1,
            "An idle canceled reader invented input work or physical EOF.");
        idleReader.Release();

        var foreign = new Executor((_, _) => ValueTask.FromException<int>(new OperationCanceledException("foreign-private-body-marker")));
        var foreignReader = new CompletionsBodyReader(foreign, 1);
        await Throws<OperationCanceledException>(() => foreignReader.ReadAsync().AsTask());
        Check(!foreignReader.ReachedEof && foreign.Reads == 1 && foreign.Cancels == 0,
            "Foreign cancellation failure was admitted as a successful closed read.");
        await foreignReader.CancelAsync();
        Check((await foreignReader.ReadAsync()).Done && foreign.Reads == 1, "Actual cancellation did not close the failed reader.");
        foreignReader.Release();
    }

    private static async Task HttpResults()
    {
        foreach (var done in new[] { true, false })
        {
            var prefix = Wire(done ? [Finish, "[DONE]"] : [Finish]);
            var results = new List<CompletionsBodyReadResult>(); var providerCalls = 0;
            using var body = new HttpCleanupGateStream(done ? prefix.Concat(Wire("{ignored-tail}")).ToArray() : prefix);
            using var content = new StreamProbeContent(body);
            using var handler = new FakeHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
            using var client = new HttpClient(handler);
            var transport = new CompletionsHttpSseTransport(client, (_, _) => new(HttpMethod.Post, "https://body-result.invalid/chat/completions"),
                new(Framing: new(ReadBufferBytes: 1, EofBehavior: SseEofBehavior.DispatchPendingEvent) { Profile = SseFramingProfile.OpenAISdk719 })
                { OnBodyRead = results.Add, Hooks = new() { OnProviderStreamEvent = (_, _, _) => { providerCalls++; return ValueTask.CompletedTask; } } });
            await using var run = await transport.StartAsync(Request()); var drain = DrainSource(run);
            try
            {
                var semantic = await run.SourceResult.WaitAsync(Deadline); await body.CleanupEntered.Task.WaitAsync(Deadline);
                Check(results.SelectMany(result => result.Value).SequenceEqual(prefix) && results.Count(result => result.Done) == (done ? 0 : 1) &&
                    providerCalls == 1 && semantic.Snapshot.Raw.Value.GetProperty("value").GetProperty("stopReason").GetString() == "stop" &&
                    !run.CanonicalCompletion.IsCompleted && !run.CleanupCompletion.IsCompleted && !content.Disposed,
                    "The real HTTP DTO path consumed tail, lost EOF or bypassed the independent physical owner.");
                var first = results[0]; Check(first.Value.Single() == prefix[0], "The HTTP observer received bytes from a different read.");
                body.ReleaseCleanup.TrySetResult(); await drain.WaitAsync(Deadline);
                var canonical = await run.CanonicalCompletion.WaitAsync(Deadline);
                Check(canonical.Failure is null && (await run.CleanupCompletion).Succeeded && content.Disposed && body.AsyncDisposeCalls == 1 &&
                    !body.SyncDisposedBeforeAsync && canonical.Message.Content.OfType<TextContent>().Single().Text == "\u03c0\U0001f600" &&
                    first.Value.Single() == prefix[0], "Actual parsed Unicode/Stop, owned result or joined cleanup changed.");
            }
            finally { body.ReleaseCleanup.TrySetResult(); await run.DisposeAsync(); await Observe(drain); }
        }
    }

    private static async Task HttpFaults()
    {
        foreach (var mode in new[] { "observer", "invalid-count", "physical" })
        {
            var results = new List<CompletionsBodyReadResult>(); var providerCalls = 0; Executor? external = null;
            using var body = new ResultBody(Wire(Finish, "[DONE]"), failPhysical: mode == "physical");
            using var content = new StreamProbeContent(body);
            using var handler = new FakeHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
            using var client = new HttpClient(handler);
            var options = new CompletionsHttpSseOptions(Framing: new(ReadBufferBytes: 8, EofBehavior: SseEofBehavior.DispatchPendingEvent) { Profile = SseFramingProfile.OpenAISdk719 })
            {
                OnBodyRead = result => { results.Add(result); if (mode == "observer") throw new IOException("private-body-result-marker"); },
                Hooks = new() { OnProviderStreamEvent = (_, _, _) => { providerCalls++; return ValueTask.CompletedTask; } }
            };
            if (mode == "invalid-count") options = options with { BodyReaderFactory = (_, _) =>
                ValueTask.FromResult<ICompletionsResponseBodyReader>(external = new Executor((_, _) => ValueTask.FromResult(9))) };
            var transport = new CompletionsHttpSseTransport(client, (_, _) => new(HttpMethod.Post, "https://body-result.invalid/chat/completions"), options);
            await using var run = await new ChatClient(transport, capacity: 1).StartAsync(Request()); var frames = new List<StreamEvent>(); var drain = Drain(run, frames);
            try
            {
                await body.CleanupEntered.Task.WaitAsync(Deadline);
                Check(!run.Completion.IsCompleted && !content.Disposed, "Read/result/physical fault skipped joined cleanup.");
                body.ReleaseCleanup.TrySetResult(); await drain.WaitAsync(Deadline); var result = await run.Completion;
                Check(result.Failure?.Kind == ChatFailureKind.Provider && frames.OfType<StreamError>().Count() == 1 && content.Disposed &&
                    PiWireJson.WriteMessage(frames.OfType<StreamTerminalEvent>().Single().Message).ToString() == PiWireJson.WriteMessage(result.Message).ToString() &&
                    !PiWireJson.WriteMessage(result.Message).ToString().Contains("private-body-result-marker", StringComparison.Ordinal),
                    "A DTO/observer/physical failure gained success, inconsistent settlement or private exception text.");
                if (mode == "invalid-count") Check(results.Count == 0 && providerCalls == 0 && external!.Cancels == 1 && external.Releases == 1,
                    "Invalid bytes reached the DTO observer/parser or abandoned executor cleanup.");
                if (mode == "observer") Check(results.Count == 1 && providerCalls == 0, "Observer fault was retried or provider parsing ran first.");
            }
            finally { body.ReleaseCleanup.TrySetResult(); run.Cancel(); await run.DisposeAsync(); await Observe(drain); }
        }
    }

    private sealed class Executor(Func<Memory<byte>, CancellationToken, ValueTask<int>> read, Func<ValueTask>? cancel = null) : ICompletionsResponseBodyReader
    {
        public int Reads, Cancels, Releases;
        public ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token = default) { Reads++; return read(destination, token); }
        public ValueTask CancelAsync() { Cancels++; return cancel?.Invoke() ?? ValueTask.CompletedTask; }
        public void Release() => Releases++;
    }
    private sealed class ResultBody(byte[] bytes, bool failPhysical) : ProbeStream(bytes)
    {
        public TaskCompletionSource CleanupEntered { get; } = Gate();
        public TaskCompletionSource ReleaseCleanup { get; } = Gate();
        public override async ValueTask DisposeAsync()
        { CleanupEntered.TrySetResult(); await ReleaseCleanup.Task; Disposed = true; if (failPhysical) throw new IOException("private-body-result-marker"); }
    }
    private static ChatRequest Request() => new(Model, [new("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"owned\",\"timestamp\":1}"))], 1700000000000);
    private static byte[] Wire(params string[] values) => Encoding.UTF8.GetBytes(string.Concat(values.Select(value => "data: " + value + "\n\n")));
    private static async Task Drain(ChatRun run, List<StreamEvent> frames) { await foreach (var frame in run.ReadEventsAsync()) frames.Add(frame); }
    private static async Task DrainSource(CompletionsRun run) { await foreach (var _ in run.ReadSourceEventsAsync()) { } }
    private static string FindRepo()
    { for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent) if (File.Exists(Path.Combine(path.FullName, "PiSharp.slnx"))) return path.FullName; throw new InvalidOperationException("Repository not found."); }
    private static void EqualJson(JsonElement expected, JsonElement actual)
    {
        Check(expected.ValueKind == actual.ValueKind, "JSON kind/presence changed.");
        if (expected.ValueKind == JsonValueKind.Object)
        {
            var left = expected.EnumerateObject().ToArray(); var right = actual.EnumerateObject().ToDictionary(value => value.Name, value => value.Value);
            Check(left.Length == right.Count, "An own byte/value property was omitted/added.");
            foreach (var value in left) { Check(right.TryGetValue(value.Name, out var child), "An own property was absent."); EqualJson(value.Value, child); }
        }
        else if (expected.ValueKind == JsonValueKind.Array)
        {
            Check(expected.GetArrayLength() == actual.GetArrayLength(), "Presence array changed.");
            for (var index = 0; index < expected.GetArrayLength(); index++) EqualJson(expected[index], actual[index]);
        }
        else Check(expected.GetRawText() == actual.GetRawText(), "A byte, number token or EOF flag changed.");
    }
    private static void Denied<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action().WaitAsync(Deadline); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task Observe(Task task) { try { await task.WaitAsync(Deadline); } catch { } }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
