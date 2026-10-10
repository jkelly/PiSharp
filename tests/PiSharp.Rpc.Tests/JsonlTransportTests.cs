using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Rpc;

internal static class JsonlTransportTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("jsonl.every-byte-fragmentation-lf-crlf-unicode-and-owned-records", Fragmentation);
        yield return ("jsonl.exact-frame-depth-utf8-json-duplicate-and-final-frame-admission", Admission);
        yield return ("jsonl.complete-raw-token-writes-and-pre-effect-validation", Serialization);
        yield return ("jsonl.bounded-pending-writers-await-backpressure-without-interleaving", Backpressure);
        yield return ("jsonl.cancellation-broken-pipe-and-partial-write-stop-reuse", FailuresAndCancellation);
        yield return ("jsonl.borrowed-owned-and-repeated-disposal-share-awaited-cleanup", Cleanup);
    }

    private static async Task Fragmentation()
    {
        const string first = "{\"text\":\"a\u2028b\u2029文🙂\",\"n\":1.0,\"opaque\":{\"wide\":9007199254740993,\"nil\":null}}";
        var bytes = Encoding.UTF8.GetBytes(first + "\r\n{\"end\":true}\n{\"final\":\"no LF\"}");
        for (var split = 1; split < bytes.Length; split++)
        {
            var stream = new TestStream(bytes, split);
            var records = await Read(new(stream, new(ReadBufferBytes: 7)));
            Equal(3, records.Count); Equal(first, records[0].ToString());
            Equal("a\u2028b\u2029文🙂", records[0].Value.GetProperty("text").GetString());
            Equal("no LF", records[2].Value.GetProperty("final").GetString()); Equal(0, stream.DisposeCalls);
        }
        var tiny = new TestStream(bytes) { MaximumRead = 1 };
        var owned = new JsonlReader(tiny, new(ReadBufferBytes: 1), JsonlStreamOwnership.Owned);
        var kept = await Read(owned); Equal(first, kept[0].ToString()); Equal(1, tiny.DisposeCalls);
        Equal("9007199254740993", kept[0].Value.GetProperty("opaque").GetProperty("wide").GetRawText());

        var pull = new TestStream(Encoding.UTF8.GetBytes("{\"one\":1}\n{\"two\":2}\n")) { MaximumRead = 1 };
        await using var reader = new JsonlReader(pull, new(ReadBufferBytes: 1));
        await using var iterator = reader.ReadAllAsync().GetAsyncEnumerator();
        Check(await iterator.MoveNextAsync(), "First pull record missing."); var calls = pull.ReadCalls;
        await Task.Yield(); Equal(calls, pull.ReadCalls); // No background reader runs while the caller holds a record.
        Check(await iterator.MoveNextAsync(), "Second pull record missing."); Equal("1.0", kept[0].Value.GetProperty("n").GetRawText());
    }

    private static async Task Admission()
    {
        const string exact = "{\"text\":\"文🙂\"}"; var size = Encoding.UTF8.GetByteCount(exact);
        Equal(1, (await Read(new(new TestStream(Encoding.UTF8.GetBytes(exact + "\r\n")), new(MaximumFrameBytes: size)))).Count);
        await Fails(JsonlTransportFailure.FrameLimit, () => Read(new(new TestStream(Encoding.UTF8.GetBytes(exact + "\n")), new(MaximumFrameBytes: size - 1))));
        Equal(1, (await Read(new(new TestStream(Encoding.UTF8.GetBytes("{\"nested\":{}}\n")), new(MaximumJsonDepth: 2)))).Count);
        await Fails(JsonlTransportFailure.DepthLimit, () => Read(new(new TestStream(Encoding.UTF8.GetBytes("{\"nested\":{}}\n")), new(MaximumJsonDepth: 1))));
        Equal(1, (await Read(new(new TestStream(Encoding.UTF8.GetBytes("{\"text\":\"[[{{\"}\n")), new(MaximumJsonDepth: 1)))).Count);
        foreach (var (raw, failure) in new[]
        {
            ("{\n", JsonlTransportFailure.MalformedJson), ("{", JsonlTransportFailure.PartialFinalFrame),
            ("\n", JsonlTransportFailure.MalformedJson), ("{\"a\":1,}\n", JsonlTransportFailure.MalformedJson),
            ("/*x*/{}\n", JsonlTransportFailure.MalformedJson), ("{}\r{}\n", JsonlTransportFailure.MalformedJson),
            ("[]\n", JsonlTransportFailure.InvalidRecord), ("null\n", JsonlTransportFailure.InvalidRecord),
            ("{\"a\":1,\"\\u0061\":2}\n", JsonlTransportFailure.DuplicateProperty),
            ("{\"nested\":{\"x\":0,\"\\u0078\":0}}\n", JsonlTransportFailure.DuplicateProperty),
            ("{\"text\":\"\\uD800\"}\n", JsonlTransportFailure.InvalidUnicode),
            ("{\"\\uDC00\":0}\n", JsonlTransportFailure.InvalidUnicode),
            ("\uFEFF{}\n", JsonlTransportFailure.MalformedJson)
        }) await Fails(failure, () => Read(new(new TestStream(Encoding.UTF8.GetBytes(raw)))));
        foreach (var text in new[] { "{\"text\":\"\\uD83D\\uDE42\"}\n", "{\"text\":\"\\\\uD800\"}\n", "{}\r" })
            Equal(1, (await Read(new(new TestStream(Encoding.UTF8.GetBytes(text))))).Count);
        var invalid = new TestStream([.. Encoding.UTF8.GetBytes("{\"valid\":true}\n{\"text\":\""), 0xC3, (byte)'"', (byte)'}', (byte)'\n']);
        await using var prefixReader = new JsonlReader(invalid);
        await using var prefix = prefixReader.ReadAllAsync().GetAsyncEnumerator();
        Check(await prefix.MoveNextAsync(), "Valid prefix must survive a later invalid frame in the same read.");
        await Fails(JsonlTransportFailure.InvalidUtf8, async () => { await prefix.MoveNextAsync(); });
        foreach (var options in new[] { new JsonlTransportOptions(ReadBufferBytes: 0), new(ReadBufferBytes: 65_537),
            new(MaximumFrameBytes: 0), new(MaximumFrameBytes: int.MaxValue), new(MaximumJsonDepth: 0), new(MaximumJsonDepth: 1001), new(MaximumPendingWrites: 0) })
        {
            Throws<ArgumentOutOfRangeException>(() => new JsonlReader(new MemoryStream(), options));
            Throws<ArgumentOutOfRangeException>(() => new JsonlWriter(new MemoryStream(), options));
        }
    }

    private static async Task Serialization()
    {
        const string raw = "{\n \"text\": \"a\u2028b\u2029文🙂\\n\", \"n\": 1.0, \"opaque\": {\"wide\":9007199254740993,\"huge\":1e400,\"nil\":null}\n}";
        const string compact = "{\"text\":\"a\u2028b\u2029文🙂\\n\",\"n\":1.0,\"opaque\":{\"wide\":9007199254740993,\"huge\":1e400,\"nil\":null}}\n";
        var record = JsonData.Parse(raw); var stream = new TestStream();
        await using var writer = new JsonlWriter(stream, new(MaximumFrameBytes: Encoding.UTF8.GetByteCount(compact) - 1));
        await writer.WriteAsync(record); Equal(compact, Encoding.UTF8.GetString(stream.Output.ToArray()));
        Equal(raw, record.ToString()); Equal(1, stream.WriteCalls); Equal(1, stream.FlushCalls);
        var records = await Read(new(new TestStream(stream.Output.ToArray())));
        Equal("1.0", records[0].Value.GetProperty("n").GetRawText());
        Equal("1e400", records[0].Value.GetProperty("opaque").GetProperty("huge").GetRawText());
        var count = stream.WriteCalls;
        await Fails(JsonlTransportFailure.InvalidRecord, () => writer.WriteAsync(JsonData.Null).AsTask());
        await Fails(JsonlTransportFailure.InvalidUnicode, () => writer.WriteAsync(JsonData.Parse("{\"text\":\"\\uD800\"}")).AsTask());
        foreach (var permissiveRaw in new[] { "{\"outer\":{\"value\":1,},}", "{\"outer\":/*private-comment*/{\"value\":1}}" })
        {
            JsonData permissive;
            using (var document = JsonDocument.Parse(permissiveRaw, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }))
                permissive = JsonData.FromElement(document.RootElement);
            Equal(permissiveRaw, permissive.ToString());
            var failure = await ThrowsAsync<JsonlTransportException>(() => writer.WriteAsync(permissive).AsTask());
            Equal(JsonlTransportFailure.MalformedJson, failure.Failure);
            Check(!failure.Message.Contains("private-comment", StringComparison.Ordinal), "Strict-admission diagnostic exposed rejected raw syntax.");
            Equal(permissiveRaw, permissive.ToString()); Equal(count, stream.WriteCalls);
        }
        Equal(count, stream.WriteCalls); await writer.WriteAsync(JsonData.Parse("{}")); Equal(count + 1, stream.WriteCalls);
        await using var depthWriter = new JsonlWriter(new TestStream(), new(MaximumJsonDepth: 1));
        await Fails(JsonlTransportFailure.DepthLimit, () => depthWriter.WriteAsync(JsonData.Parse("{\"nested\":{}}")).AsTask());
    }

    private static async Task Backpressure()
    {
        var stream = new TestStream() { BlockWrites = true, BlockFlush = true };
        await using var writer = new JsonlWriter(stream, new(MaximumPendingWrites: 2));
        var first = writer.WriteAsync(JsonData.Parse("{\"id\":1}")).AsTask(); await stream.WriteStarted.Task.WaitAsync(Deadline);
        using var cancellation = new CancellationTokenSource();
        var queued = writer.WriteAsync(JsonData.Parse("{\"id\":2}"), cancellation.Token).AsTask();
        await Fails(JsonlTransportFailure.PendingWriteLimit, () => writer.WriteAsync(JsonData.Parse("{\"id\":3}")).AsTask());
        Check(!first.IsCompleted && !queued.IsCompleted, "Writer ignored stream backpressure."); Equal(1, stream.WriteCalls);
        cancellation.Cancel(); await ThrowsAsync<OperationCanceledException>(() => queued);
        var replacement = writer.WriteAsync(JsonData.Parse("{\"id\":4}")).AsTask();
        stream.WriteRelease.TrySetResult(); await stream.FlushStarted.Task.WaitAsync(Deadline);
        Check(!first.IsCompleted && stream.WriteCalls == 1, "Writer released record ownership before flush completed.");
        stream.FlushRelease.TrySetResult(); await Task.WhenAll(first, replacement).WaitAsync(Deadline);
        Equal(1, stream.MaximumActiveWrites); Equal("{\"id\":1}\n{\"id\":4}\n", Encoding.UTF8.GetString(stream.Output.ToArray()));
    }

    private static async Task FailuresAndCancellation()
    {
        using var before = new CancellationTokenSource(); before.Cancel();
        var untouched = new TestStream(); await using var untouchedWriter = new JsonlWriter(untouched);
        await ThrowsAsync<OperationCanceledException>(() => untouchedWriter.WriteAsync(JsonData.Parse("{}"), before.Token).AsTask()); Equal(0, untouched.WriteCalls);
        var untouchedRead = new TestStream(); await ThrowsAsync<OperationCanceledException>(() => Read(new(untouchedRead), before.Token)); Equal(0, untouchedRead.ReadCalls);

        var blocked = new TestStream() { BlockReads = true }; var reader = new JsonlReader(blocked, ownership: JsonlStreamOwnership.Owned);
        using var readCancel = new CancellationTokenSource(); var reading = Read(reader, readCancel.Token);
        await blocked.ReadStarted.Task.WaitAsync(Deadline); readCancel.Cancel(); await ThrowsAsync<OperationCanceledException>(() => reading); Equal(1, blocked.DisposeCalls);

        var broken = new TestStream() { FailWrite = true, PartialWriteBytes = 3, HoldDispose = true };
        var writer = new JsonlWriter(broken, ownership: JsonlStreamOwnership.Owned);
        var writing = writer.WriteAsync(JsonData.Parse("{\"id\":1}")).AsTask(); await broken.DisposeStarted.Task.WaitAsync(Deadline);
        Check(!writing.IsCompleted, "Broken-pipe return skipped awaited cleanup."); Equal(3L, broken.Output.Length);
        broken.DisposeRelease.TrySetResult(); await ThrowsAsync<IOException>(() => writing); Equal(1, broken.DisposeCalls);
        await ThrowsAsync<ObjectDisposedException>(() => writer.WriteAsync(JsonData.Parse("{}")).AsTask());

        var partial = new TestStream() { BlockWrites = true, PartialWriteBytes = 2 }; await using var partialWriter = new JsonlWriter(partial);
        using var writeCancel = new CancellationTokenSource(); var partialTask = partialWriter.WriteAsync(JsonData.Parse("{\"id\":1}"), writeCancel.Token).AsTask();
        await partial.WriteStarted.Task.WaitAsync(Deadline); writeCancel.Cancel(); await ThrowsAsync<OperationCanceledException>(() => partialTask);
        Equal(2L, partial.Output.Length); Equal(0, partial.DisposeCalls);
        await ThrowsAsync<ObjectDisposedException>(() => partialWriter.WriteAsync(JsonData.Parse("{}")).AsTask());
        var failedRead = new TestStream() { FailRead = true }; await ThrowsAsync<IOException>(() => Read(new(failedRead, ownership: JsonlStreamOwnership.Owned))); Equal(1, failedRead.DisposeCalls);
    }

    private static async Task Cleanup()
    {
        // Borrowing changes stream ownership, not the obligation to settle active async reads.
        foreach (var ownership in new[] { JsonlStreamOwnership.Borrowed, JsonlStreamOwnership.Owned })
        {
            var delayed = new TestStream() { BlockReads = true, HoldReadFinally = true };
            var delayedReader = new JsonlReader(delayed, ownership: ownership); var active = Read(delayedReader);
            await delayed.ReadStarted.Task.WaitAsync(Deadline);
            var close = delayedReader.DisposeAsync().AsTask(); var repeatedClose = delayedReader.DisposeAsync().AsTask();
            await delayed.ReadFinallyStarted.Task.WaitAsync(Deadline);
            Check(ReferenceEquals(close, repeatedClose) && !close.IsCompleted && !active.IsCompleted,
                "Reader disposal returned before cooperative ReadAsync finally settled.");
            Equal(0, delayed.DisposeCalls);
            delayed.ReadFinallyRelease.TrySetResult();
            await Task.WhenAll(close, repeatedClose).WaitAsync(Deadline); await ThrowsAsync<OperationCanceledException>(() => active);
            Check(delayed.ReadFinallyFinished, "Reader close completed before read-finally work.");
            Equal(ownership == JsonlStreamOwnership.Owned ? 1 : 0, delayed.DisposeCalls);
        }
        var callbackFailure = new TestStream() { BlockReads = true, HoldReadFinally = true, ThrowReadCancellationCallback = true };
        var callbackReader = new JsonlReader(callbackFailure, ownership: JsonlStreamOwnership.Owned); var callbackReading = Read(callbackReader);
        await callbackFailure.ReadStarted.Task.WaitAsync(Deadline); var callbackClose = callbackReader.DisposeAsync().AsTask();
        await callbackFailure.ReadFinallyStarted.Task.WaitAsync(Deadline);
        Check(!callbackClose.IsCompleted && callbackFailure.DisposeCalls == 0, "Throwing cancellation callback skipped read settlement.");
        callbackFailure.ReadFinallyRelease.TrySetResult();
        await ThrowsAsync<AggregateException>(() => callbackClose); await ThrowsAsync<AggregateException>(() => callbackReading);
        Equal(1, callbackFailure.DisposeCalls); Check(callbackFailure.ReadFinallyFinished, "Throwing cancellation callback skipped remaining cleanup.");

        JsonlReader? reentrantReader = null; Task? reentrantClose = null;
        var reentrant = new TestStream() { BlockReads = true, HoldReadFinally = true,
            OnReadCancellation = () => reentrantClose = reentrantReader!.DisposeAsync().AsTask() };
        reentrantReader = new(reentrant, ownership: JsonlStreamOwnership.Owned);
        using var reentrantCancel = new CancellationTokenSource(); var reentrantReading = Read(reentrantReader, reentrantCancel.Token);
        await reentrant.ReadStarted.Task.WaitAsync(Deadline); reentrantCancel.Cancel();
        await reentrant.ReadFinallyStarted.Task.WaitAsync(Deadline);
        Check(reentrantClose is not null && !reentrantClose.IsCompleted, "Cancellation callback failed to join active settlement.");
        reentrant.ReadFinallyRelease.TrySetResult();
        await reentrantClose!.WaitAsync(Deadline); await ThrowsAsync<OperationCanceledException>(() => reentrantReading);
        Equal(1, reentrant.DisposeCalls);

        var input = new TestStream(Encoding.UTF8.GetBytes("{}\n")) { HoldDispose = true };
        var reader = new JsonlReader(input, ownership: JsonlStreamOwnership.Owned); var reading = Read(reader);
        await input.DisposeStarted.Task.WaitAsync(Deadline); var first = reader.DisposeAsync().AsTask(); var second = reader.DisposeAsync().AsTask();
        Check(ReferenceEquals(first, second) && !first.IsCompleted && !reading.IsCompleted, "Reader disposal did not share awaited cleanup.");
        input.DisposeRelease.TrySetResult(); await Task.WhenAll(first, second, reading).WaitAsync(Deadline); Equal(1, input.DisposeCalls);
        await ThrowsAsync<InvalidOperationException>(() => Read(reader));

        var output = new TestStream() { BlockWrites = true, HoldDispose = true };
        var writer = new JsonlWriter(output, ownership: JsonlStreamOwnership.Owned);
        var write = writer.WriteAsync(JsonData.Parse("{}" )).AsTask(); await output.WriteStarted.Task.WaitAsync(Deadline);
        first = writer.DisposeAsync().AsTask(); second = writer.DisposeAsync().AsTask();
        await output.DisposeStarted.Task.WaitAsync(Deadline);
        Check(ReferenceEquals(first, second) && !first.IsCompleted && !write.IsCompleted, "Writer disposal did not share awaited cleanup.");
        output.DisposeRelease.TrySetResult(); await Task.WhenAll(first, second).WaitAsync(Deadline); await ThrowsAsync<OperationCanceledException>(() => write); Equal(1, output.DisposeCalls);

        var borrowed = new TestStream(); var borrowedWriter = new JsonlWriter(borrowed); await borrowedWriter.WriteAsync(JsonData.Parse("{}")); await borrowedWriter.DisposeAsync(); Equal(0, borrowed.DisposeCalls);
        var failed = new TestStream() { FailDispose = true }; var failedWriter = new JsonlWriter(failed, ownership: JsonlStreamOwnership.Owned);
        first = failedWriter.DisposeAsync().AsTask(); second = failedWriter.DisposeAsync().AsTask(); Check(ReferenceEquals(first, second), "Failed cleanup task identity changed.");
        await ThrowsAsync<IOException>(() => first); await ThrowsAsync<IOException>(() => second); Equal(1, failed.DisposeCalls);
    }

    private static async Task<List<JsonData>> Read(JsonlReader reader, CancellationToken cancellationToken = default)
    { var records = new List<JsonData>(); await foreach (var record in reader.ReadAllAsync(cancellationToken)) records.Add(record); return records; }
    private static async Task Fails(JsonlTransportFailure failure, Func<Task> action) => Equal(failure, (await ThrowsAsync<JsonlTransportException>(action)).Failure);
    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action().WaitAsync(Deadline); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static T Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Values differ.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class TestStream(byte[]? input = null, int split = -1) : Stream
    {
        private readonly byte[] _input = input ?? []; private int _position; private int _activeWrites;
        public MemoryStream Output { get; } = new();
        public int MaximumRead { get; init; } = int.MaxValue;
        public bool BlockReads { get; init; } public bool BlockWrites { get; init; } public bool BlockFlush { get; init; }
        public bool FailRead { get; init; } public bool FailWrite { get; init; } public bool HoldDispose { get; init; } public bool FailDispose { get; init; }
        public bool HoldReadFinally { get; init; } public bool ThrowReadCancellationCallback { get; init; }
        public Action? OnReadCancellation { get; init; }
        public bool ReadFinallyFinished { get; private set; }
        public int PartialWriteBytes { get; init; }
        public int ReadCalls { get; private set; } public int WriteCalls { get; private set; } public int FlushCalls { get; private set; }
        public int DisposeCalls { get; private set; } public int MaximumActiveWrites { get; private set; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadFinallyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadFinallyRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WriteRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FlushStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FlushRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposeRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            using var callback = ThrowReadCancellationCallback || OnReadCancellation is not null ? cancellationToken.Register(() =>
            {
                OnReadCancellation?.Invoke();
                if (ThrowReadCancellationCallback) throw new InvalidOperationException("Synthetic cancellation callback failure.");
            }) : default;
            ReadStarted.TrySetResult();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (FailRead) throw new IOException("Synthetic read failure.");
                if (BlockReads) await ReadRelease.Task.WaitAsync(cancellationToken);
                var available = _input.Length - _position; if (split > _position) available = Math.Min(available, split - _position);
                var count = Math.Min(buffer.Length, Math.Min(available, MaximumRead)); _input.AsMemory(_position, count).CopyTo(buffer); _position += count; return count;
            }
            finally
            {
                ReadFinallyStarted.TrySetResult(); if (HoldReadFinally) await ReadFinallyRelease.Task;
                ReadFinallyFinished = true;
            }
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCalls++; MaximumActiveWrites = Math.Max(MaximumActiveWrites, Interlocked.Increment(ref _activeWrites));
            try
            {
                var prefix = Math.Min(PartialWriteBytes, buffer.Length); await Output.WriteAsync(buffer[..prefix], cancellationToken);
                WriteStarted.TrySetResult(); if (FailWrite) throw new IOException("Synthetic broken pipe.");
                if (BlockWrites) await WriteRelease.Task.WaitAsync(cancellationToken);
                await Output.WriteAsync(buffer[prefix..], cancellationToken);
            }
            finally { Interlocked.Decrement(ref _activeWrites); }
        }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        { FlushCalls++; FlushStarted.TrySetResult(); if (BlockFlush) await FlushRelease.Task.WaitAsync(cancellationToken); }
        public override async ValueTask DisposeAsync()
        { DisposeCalls++; DisposeStarted.TrySetResult(); if (HoldDispose) await DisposeRelease.Task; if (FailDispose) throw new IOException("Synthetic cleanup failure."); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
