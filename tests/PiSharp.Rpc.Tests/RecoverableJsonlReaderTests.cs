using System.Text;
using PiSharp.Contracts;
using PiSharp.Rpc;

internal static class RecoverableJsonlReaderTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("rpc-jsonl.recoverable-malformed-and-blank-frames-resume-with-owned-raw-records", Recovery);
        yield return ("rpc-jsonl.eof-admissions-and-original-fail-fast-single-enumeration", FinalFramesAndFailFast);
        yield return ("rpc-jsonl.hardening-and-resource-failures-remain-terminal-after-recovery", FatalFailures);
        yield return ("rpc-jsonl.recovery-retains-pull-cancellation-and-awaited-shared-cleanup", Cleanup);
    }

    // Pinned jsonl.ts emits every LF-delimited line, including empty lines; rpc-mode.ts catches each JSON.parse failure.
    private static async Task Recovery()
    {
        const string raw = "{\"id\":\"a\\n\u2028\u2029\U0001F642\",\"type\":\"get_state\",\"opaque\":{\"n\":1.0,\"huge\":1e400,\"wide\":9007199254740993,\"nil\":null}}";
        var bytes = Encoding.UTF8.GetBytes("\n\r\n \t\n{private-rejected-input}\n" + raw + "\r\n{broken\n{\"next\":true}\n");
        for (var split = 1; split < bytes.Length; split++)
        {
            var stream = new InputStream(bytes, split: split);
            var values = await Read(new(stream, new(ReadBufferBytes: 7)));
            Equal(7, values.Count);
            foreach (var index in new[] { 0, 1, 2, 3, 5 }) Rejected(values[index], JsonlTransportFailure.MalformedJson, final: false);
            Equal(raw, Accepted(values[4], final: false).ToString());
            Equal("a\n\u2028\u2029\U0001F642", values[4].Record!.Value.GetProperty("id").GetString());
            Equal("1.0", values[4].Record!.Value.GetProperty("opaque").GetProperty("n").GetRawText());
            Equal("1e400", values[4].Record!.Value.GetProperty("opaque").GetProperty("huge").GetRawText());
            Check(!values[4].Record!.Value.GetProperty("opaque").TryGetProperty("absent", out _), "Missing value was inserted.");
            Equal("null", values[4].Record!.Value.GetProperty("opaque").GetProperty("nil").GetRawText());
            Equal("{\"next\":true}", Accepted(values[6], final: false).ToString()); Equal(0, stream.DisposeCalls);
            Check(values[3].FailureMessage is not null && !values[3].FailureMessage!.Contains("private", StringComparison.Ordinal),
                "Rejected payload leaked into an admission diagnostic.");
        }
        var bytewise = new InputStream(bytes, maximumRead: 1);
        var owned = await Read(new(bytewise, new(ReadBufferBytes: 1), JsonlStreamOwnership.Owned));
        Equal(1, bytewise.DisposeCalls); Equal(raw, owned[4].Record!.ToString());
        var scalars = await Read(new(new InputStream(Encoding.UTF8.GetBytes("[]\nnull\ntrue\n1.0\n\"text\"\n{}\n"))));
        Equal(6, scalars.Count);
        foreach (var value in scalars.Take(5)) Rejected(value, JsonlTransportFailure.InvalidRecord, final: false);
        Accepted(scalars[5], final: false);
    }

    private static async Task FinalFramesAndFailFast()
    {
        // onEnd emits only a nonempty remaining buffer; one terminal CR is removed before JSON.parse.
        foreach (var (input, failure) in new (string, JsonlTransportFailure?)[]
        {
            ("{}", null), ("{}\r", null), ("{", JsonlTransportFailure.PartialFinalFrame),
            ("{\"broken\":", JsonlTransportFailure.PartialFinalFrame), (" \t", JsonlTransportFailure.PartialFinalFrame),
            ("\r", JsonlTransportFailure.PartialFinalFrame), ("null", JsonlTransportFailure.InvalidRecord)
        })
        {
            var values = await Read(new(new InputStream(Encoding.UTF8.GetBytes(input)))); Equal(1, values.Count);
            if (failure is { } rejected) Rejected(values[0], rejected, final: true); else Accepted(values[0], final: true);
        }
        Equal(0, (await Read(new(new InputStream([])))).Count);
        var terminated = await Read(new(new InputStream(Encoding.UTF8.GetBytes("{}\n"))));
        Equal(1, terminated.Count); Accepted(terminated[0], final: false);
        var blank = await Read(new(new InputStream(Encoding.UTF8.GetBytes("\r\n"))));
        Equal(1, blank.Count); Rejected(blank[0], JsonlTransportFailure.MalformedJson, final: false);
        var trailing = await Read(new(new InputStream(Encoding.UTF8.GetBytes("{}\n{broken"))));
        Equal(2, trailing.Count); Accepted(trailing[0], final: false); Rejected(trailing[1], JsonlTransportFailure.PartialFinalFrame, final: true);

        var failFast = new JsonlReader(new InputStream(Encoding.UTF8.GetBytes("{broken}\n{}\n")));
        await using var original = failFast.ReadAllAsync().GetAsyncEnumerator();
        Equal(JsonlTransportFailure.MalformedJson, (await Throws<JsonlTransportException>(async () => { await original.MoveNextAsync(); })).Failure);
        await Throws<InvalidOperationException>(() => Read(failFast));
        var finalFailFast = new JsonlReader(new InputStream(Encoding.UTF8.GetBytes("{")));
        await using var final = finalFailFast.ReadAllAsync().GetAsyncEnumerator();
        Equal(JsonlTransportFailure.PartialFinalFrame, (await Throws<JsonlTransportException>(async () => { await final.MoveNextAsync(); })).Failure);
        var mixed = new JsonlReader(new InputStream(Encoding.UTF8.GetBytes("{}\n{}\n")));
        await using var first = mixed.ReadAdmissionsAsync().GetAsyncEnumerator();
        Check(await first.MoveNextAsync(), "First admission missing.");
        await using var second = mixed.ReadAllAsync().GetAsyncEnumerator();
        await Throws<InvalidOperationException>(async () => { await second.MoveNextAsync(); });
        Check(await first.MoveNextAsync(), "Rejected second enumeration changed the active reader.");
    }

    private static async Task FatalFailures()
    {
        foreach (var (raw, failure, depth) in new[]
        {
            ("{\"a\":1,\"\\u0061\":2}\n", JsonlTransportFailure.DuplicateProperty, 32),
            ("{\"nested\":{\"x\":0,\"\\u0078\":1}}\n", JsonlTransportFailure.DuplicateProperty, 32),
            ("{\"text\":\"\\uD800\"}\n", JsonlTransportFailure.InvalidUnicode, 32),
            ("{\"\\uDC00\":0}\n", JsonlTransportFailure.InvalidUnicode, 32),
            ("{\"nested\":{}}\n", JsonlTransportFailure.DepthLimit, 1),
            ("{{{invalid\n", JsonlTransportFailure.DepthLimit, 2)
        }) await TerminalAfterRecovery(Encoding.UTF8.GetBytes(raw), failure, new(MaximumJsonDepth: depth));
        await TerminalAfterRecovery([.. Encoding.UTF8.GetBytes("{\"text\":\""), 0xC3, (byte)'"', (byte)'}', (byte)'\n'],
            JsonlTransportFailure.InvalidUtf8, new());
        await TerminalAfterRecovery([.. Encoding.UTF8.GetBytes("{\"text\":\""), 0xF0, 0x9F], JsonlTransportFailure.InvalidUtf8, new(), addLaterRecord: false);
        await TerminalAfterRecovery(Encoding.UTF8.GetBytes("{\"long\":true}\n"), JsonlTransportFailure.FrameLimit, new(MaximumFrameBytes: 5));
        var exact = await Read(new(new InputStream(Encoding.UTF8.GetBytes("{}\r\n\n{}")), new(MaximumFrameBytes: 2)));
        Equal(3, exact.Count); Accepted(exact[0], false); Rejected(exact[1], JsonlTransportFailure.MalformedJson, false); Accepted(exact[2], true);
    }
    private static async Task TerminalAfterRecovery(byte[] dangerous, JsonlTransportFailure failure,
        JsonlTransportOptions options, bool addLaterRecord = true)
    {
        var stream = new InputStream([ (byte)'\n', .. dangerous, .. (addLaterRecord ? Encoding.UTF8.GetBytes("{\"later\":true}\n") : Array.Empty<byte>()) ]);
        var reader = new JsonlReader(stream, options, JsonlStreamOwnership.Owned);
        await using var iterator = reader.ReadAdmissionsAsync().GetAsyncEnumerator();
        Check(await iterator.MoveNextAsync(), "Recoverable prefix missing."); Rejected(iterator.Current, JsonlTransportFailure.MalformedJson, false);
        Equal(failure, (await Throws<JsonlTransportException>(async () => { await iterator.MoveNextAsync(); })).Failure);
        Equal(1, stream.DisposeCalls); await Throws<InvalidOperationException>(() => Read(reader));
    }

    private static async Task Cleanup()
    {
        var paused = new InputStream(Encoding.UTF8.GetBytes("\n{}\n"), maximumRead: 1);
        var pull = new JsonlReader(paused, new(ReadBufferBytes: 1));
        await using (var iterator = pull.ReadAdmissionsAsync().GetAsyncEnumerator())
        {
            Check(await iterator.MoveNextAsync(), "Blank frame missing."); var reads = paused.ReadCalls;
            await Task.Yield(); Equal(reads, paused.ReadCalls);
            await pull.DisposeAsync(); Equal(0, paused.DisposeCalls);
            await Throws<ObjectDisposedException>(async () => { await iterator.MoveNextAsync(); });
        }
        foreach (var ownership in new[] { JsonlStreamOwnership.Borrowed, JsonlStreamOwnership.Owned })
        {
            var stream = new InputStream(Encoding.UTF8.GetBytes("\n")) { HoldReadAtEof = true, HoldDispose = ownership == JsonlStreamOwnership.Owned };
            var reader = new JsonlReader(stream, ownership: ownership);
            await using var iterator = reader.ReadAdmissionsAsync().GetAsyncEnumerator();
            Check(await iterator.MoveNextAsync(), "Recoverable frame missing before gated read.");
            var active = iterator.MoveNextAsync().AsTask(); await stream.EofReadStarted.Task.WaitAsync(Deadline);
            var first = reader.DisposeAsync().AsTask(); var second = reader.DisposeAsync().AsTask();
            await stream.ReadFinallyStarted.Task.WaitAsync(Deadline);
            Check(ReferenceEquals(first, second) && !first.IsCompleted && !active.IsCompleted,
                "Recoverable reader disposal skipped active ReadAsync finally."); Equal(0, stream.DisposeCalls);
            stream.ReadFinallyRelease.TrySetResult();
            if (ownership == JsonlStreamOwnership.Owned)
            {
                await stream.DisposeStarted.Task.WaitAsync(Deadline); Check(!first.IsCompleted && !active.IsCompleted, "Owned disposal was not awaited.");
                stream.DisposeRelease.TrySetResult();
            }
            await Task.WhenAll(first, second).WaitAsync(Deadline); await Throws<OperationCanceledException>(() => active);
            Check(stream.ReadFinallyFinished, "Reader closed before active cleanup."); Equal(ownership == JsonlStreamOwnership.Owned ? 1 : 0, stream.DisposeCalls);
        }
        var canceledStream = new InputStream(Encoding.UTF8.GetBytes("\n")) { HoldReadAtEof = true };
        var canceledReader = new JsonlReader(canceledStream, ownership: JsonlStreamOwnership.Owned);
        using var cancellation = new CancellationTokenSource();
        await using var canceled = canceledReader.ReadAdmissionsAsync().GetAsyncEnumerator(cancellation.Token);
        Check(await canceled.MoveNextAsync(), "Admission missing before cancellation."); var reading = canceled.MoveNextAsync().AsTask();
        await canceledStream.EofReadStarted.Task.WaitAsync(Deadline); cancellation.Cancel();
        await canceledStream.ReadFinallyStarted.Task.WaitAsync(Deadline); Check(!reading.IsCompleted, "Cancellation skipped asynchronous read-finally work.");
        canceledStream.ReadFinallyRelease.TrySetResult(); await Throws<OperationCanceledException>(() => reading); Equal(1, canceledStream.DisposeCalls);
    }

    private static async Task<List<JsonlFrameAdmission>> Read(JsonlReader reader)
    { var values = new List<JsonlFrameAdmission>(); await foreach (var value in reader.ReadAdmissionsAsync()) values.Add(value); return values; }
    private static JsonData Accepted(JsonlFrameAdmission value, bool final)
    { Check(value.IsAccepted && value.Record is not null && value.Failure is null && value.FailureMessage is null, "Invalid successful admission."); Equal(final, value.IsFinalFrame); return value.Record!; }
    private static void Rejected(JsonlFrameAdmission value, JsonlTransportFailure failure, bool final)
    { Check(!value.IsAccepted && value.Record is null && value.FailureMessage is not null, "Invalid rejected admission."); Equal(failure, value.Failure!.Value); Equal(final, value.IsFinalFrame); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action().WaitAsync(Deadline); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Values differ.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class InputStream(byte[] input, int maximumRead = int.MaxValue, int split = -1) : Stream
    {
        private int _position;
        public bool HoldReadAtEof { get; init; } public bool HoldDispose { get; init; }
        public int ReadCalls { get; private set; } public int DisposeCalls { get; private set; }
        public bool ReadFinallyFinished { get; private set; }
        public TaskCompletionSource EofReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadFinallyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadFinallyRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposeRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++; cancellationToken.ThrowIfCancellationRequested();
            if (HoldReadAtEof && _position == input.Length)
            {
                EofReadStarted.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                finally { ReadFinallyStarted.TrySetResult(); await ReadFinallyRelease.Task; ReadFinallyFinished = true; }
            }
            var available = input.Length - _position; if (split > _position) available = Math.Min(available, split - _position);
            var count = Math.Min(buffer.Length, Math.Min(maximumRead, available));
            input.AsMemory(_position, count).CopyTo(buffer); _position += count; return count;
        }
        public override async ValueTask DisposeAsync()
        { DisposeCalls++; DisposeStarted.TrySetResult(); if (HoldDispose) await DisposeRelease.Task; }
        public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }
}
