using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class SessionLogReaderTests
{
    private const string Header = """{"type":"session","version":3,"id":"session-id","timestamp":"header-time","cwd":"unresolved/path"}""";
    private const string Entry = """{"type":"custom","id":"entry-id","parentId":null,"timestamp":"entry-time","customType":"state","data":null}""";
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("session reader LF CRLF blanks final record and exact immutable byte ranges", FramingAndOwnership),
        ("session reader distinguishes incomplete EOF JSON and UTF8 from malformed records", InterruptedTails),
        ("session reader stops at middle damage and retains hidden descendant bytes", MiddleDamage),
        ("session reader diagnoses missing invalid empty future legacy and duplicate headers", HeaderPolicies),
        ("session reader enforces exact byte line record and codec limits", Limits),
        ("session reader waits for owned cleanup and sanitizes read/disposal faults", FaultAndCleanup),
        ("session reader cancellation awaits cleanup and honors caller stream ownership", CancellationAndOwnership),
        ("session reader reads readonly files repeatably without changing source hashes", ReadOnlyFiles)
    ];

    public static async Task FramingAndOwnership()
    {
        var unicodeEntry = Entry.Replace("\"data\":null", "\"data\":\"\u03C0\U0001F600\"", StringComparison.Ordinal);
        var headerLine = Bytes(Header + "\r\n"); var blankLine = Bytes(" \t\r\n");
        var entryLine = Bytes(unicodeEntry + "\n"); var finalLine = Bytes(Entry.Replace("entry-id", "last-id", StringComparison.Ordinal));
        var input = Join(headerLine, blankLine, entryLine, finalLine);
        var stream = new ProbeStream(input) { ChunkBytes = 1 };
        var result = await new SessionLogReader().ReadAsync(stream);
        Equal(SessionLogReadStatus.Complete, result.Status); Check(result.SourceComplete, "EOF was not observed.");
        Equal(3, result.ValidatedPrefix.Length); Equal(input.Length, result.ValidatedPrefixByteLength);
        Check(result.OriginalBytes.AsSpan().SequenceEqual(input), "Original framing/UTF8 bytes changed.");
        Equal(0, stream.DisposeCalls); Equal(Header, result.Header!.WireBody.ToString());
        var first = result.ValidatedPrefix[0]; Equal(1, first.LineNumber); Equal(0, first.ByteOffset);
        Equal(Bytes(Header).Length, first.ContentByteLength); Equal(headerLine.Length, first.PhysicalByteLength);
        Check(first.HasLineTerminator, "CRLF header lost its terminator.");
        var second = result.ValidatedPrefix[1]; Equal(3, second.LineNumber); Equal(headerLine.Length + blankLine.Length, second.ByteOffset);
        Equal(entryLine.Length - 1, second.ContentByteLength); Equal(entryLine.Length, second.PhysicalByteLength);
        Equal("\u03C0\U0001F600", second.Entry.WireBody.Value.GetProperty("data").GetString());
        var last = result.ValidatedPrefix[2]; Equal(4, last.LineNumber); Check(!last.HasLineTerminator, "Final record was given a newline.");
        Diagnostic(result, SessionLogDiagnosticCode.FinalLineWithoutNewline, 4, last.ByteOffset, finalLine.Length);
        var expectedFirst = result.OriginalBytes[0]; input[0] = 0;
        var mutableCopy = result.OriginalBytes.ToArray(); mutableCopy[0] = 1;
        Equal(expectedFirst, result.OriginalBytes[0]); Equal(Header, result.Header!.WireBody.ToString());
        var changedSnapshot = result.OriginalBytes.SetItem(0, 2);
        Equal(expectedFirst, result.OriginalBytes[0]); Equal((byte)2, changedSnapshot[0]);
        await stream.DisposeAsync();
        using var positioned = new MemoryStream(Bytes("ignored" + Header)); positioned.Position = Bytes("ignored").Length;
        var relative = await new SessionLogReader().ReadAsync(positioned);
        Equal(0, relative.ValidatedPrefix[0].ByteOffset); Check(relative.OriginalBytes.AsSpan().SequenceEqual(Bytes(Header)), "Reader rewound caller stream.");
    }

    public static async Task InterruptedTails()
    {
        var prefix = Bytes(Header + "\n" + Entry + "\n");
        foreach (var tail in new[] { "{", "{\"type\":\"custom\",\"id\":\"unfinished", "{\"opaque\":[1e" })
        {
            var result = await Read(Join(prefix, Bytes(tail)));
            Equal(2, result.ValidatedPrefix.Length); Equal(prefix.Length, result.ValidatedPrefixByteLength);
            Equal(SessionLogReadStatus.RecoveryRequired, result.Status);
            Diagnostic(result, SessionLogDiagnosticCode.IncompleteFinalJson, 3, prefix.Length, Bytes(tail).Length);
            Check(result.SourceComplete, "Incomplete EOF was confused with a bounded capture.");
        }
        var malformed = await Read(Join(prefix, Bytes("{\"type\":!}")));
        Diagnostic(malformed, SessionLogDiagnosticCode.InvalidRecord, 3, prefix.Length, Bytes("{\"type\":!}").Length);
        var terminated = await Read(Join(prefix, Bytes("{\n")));
        Diagnostic(terminated, SessionLogDiagnosticCode.InvalidRecord, 3, prefix.Length, 2);
        var incompleteUtf8 = Join(Bytes("{\"data\":\""), new byte[] { 0xF0, 0x9F });
        var utf8Tail = await Read(Join(prefix, incompleteUtf8));
        Diagnostic(utf8Tail, SessionLogDiagnosticCode.IncompleteFinalUtf8, 3, prefix.Length, incompleteUtf8.Length);
        var terminatedUtf8 = await Read(Join(prefix, incompleteUtf8, Bytes("\n")));
        Diagnostic(terminatedUtf8, SessionLogDiagnosticCode.InvalidUtf8, 3, prefix.Length, incompleteUtf8.Length + 1);
        var invalidUtf8 = await Read(Join(prefix, new byte[] { 0xFF }));
        Diagnostic(invalidUtf8, SessionLogDiagnosticCode.InvalidUtf8, 3, prefix.Length, 1);
    }

    public static async Task MiddleDamage()
    {
        var prefix = Bytes(Header + "\n" + Entry + "\r\n");
        foreach (var bad in new[] { "{secret-private-payload}", Entry.Replace("\"data\":null", "\"data\":{\"x\":1,\"x\":2}", StringComparison.Ordinal) })
        {
            var badLine = Bytes(bad + "\n"); var descendants = Bytes(Entry.Replace("entry-id", "descendant", StringComparison.Ordinal) + "\n");
            var input = Join(prefix, badLine, descendants);
            var first = await Read(input); var repeated = await Read(input);
            Equal(2, first.ValidatedPrefix.Length); Equal(prefix.Length, first.ValidatedPrefixByteLength);
            Equal(SessionLogReadStatus.RecoveryRequired, first.Status);
            Diagnostic(first, SessionLogDiagnosticCode.InvalidRecord, 3, prefix.Length, badLine.Length);
            Check(first.OriginalBytes.AsSpan().SequenceEqual(input), "Descendant bytes were silently dropped.");
            Check(first.Diagnostics.SequenceEqual(repeated.Diagnostics), "Repeated inspection changed diagnostics.");
            Equal(first.ValidatedPrefixByteLength, repeated.ValidatedPrefixByteLength);
        }
    }

    public static async Task HeaderPolicies()
    {
        var empty = await Read([]); Diagnostic(empty, SessionLogDiagnosticCode.EmptyLog, 1, 0, 0);
        var blank = await Read(Bytes(" \r\n\t")); Diagnostic(blank, SessionLogDiagnosticCode.MissingHeader, 1, 0, 4);
        var missing = await Read(Bytes(Entry + "\n")); Diagnostic(missing, SessionLogDiagnosticCode.MissingHeader, 1, 0, Bytes(Entry + "\n").Length);
        var malformed = await Read(Bytes("{}\n")); Diagnostic(malformed, SessionLogDiagnosticCode.InvalidHeader, 1, 0, 3);
        var emptyHeader = Header.Replace("session-id", "", StringComparison.Ordinal);
        Diagnostic(await Read(Bytes(emptyHeader)), SessionLogDiagnosticCode.EmptyHeader, 1, 0, Bytes(emptyHeader).Length);
        var duplicate = await Read(Bytes(Header + "\n" + Header + "\n" + Entry));
        Equal(1, duplicate.ValidatedPrefix.Length); Equal(Bytes(Header + "\n").Length, duplicate.ValidatedPrefixByteLength);
        Diagnostic(duplicate, SessionLogDiagnosticCode.UnexpectedHeader, 2, Bytes(Header + "\n").Length, Bytes(Header + "\n").Length);
        var futureHeader = Header.Replace("\"version\":3", "\"version\":4", StringComparison.Ordinal);
        var futureBytes = Bytes(futureHeader + "\n" + Entry);
        var future = await Read(futureBytes);
        Equal(SessionLogReadStatus.UnsupportedVersion, future.Status); Equal<long?>(4, future.DetectedVersion);
        Equal(0, future.ValidatedPrefix.Length); Equal<SessionEntry?>(null, future.Header); Equal(0, future.ValidatedPrefixByteLength);
        Equal(futureHeader, future.OpaqueHeader!.ToString()); Check(future.OriginalBytes.AsSpan().SequenceEqual(futureBytes), "Future source bytes changed.");
        Diagnostic(future, SessionLogDiagnosticCode.FutureHeaderVersion, 1, 0, Bytes(futureHeader + "\n").Length);
        foreach (var header in new[] { Header.Replace("\"version\":3", "\"version\":2", StringComparison.Ordinal), Header.Replace("\"version\":3,", "", StringComparison.Ordinal) })
        {
            var legacy = await Read(Bytes(header)); Equal(SessionLogReadStatus.UnsupportedVersion, legacy.Status);
            Diagnostic(legacy, SessionLogDiagnosticCode.UnsupportedHeaderVersion, 1, 0, Bytes(header).Length);
        }
        var bom = Join(new byte[] { 0xEF, 0xBB, 0xBF }, Bytes(Header));
        Diagnostic(await Read(bom), SessionLogDiagnosticCode.Utf8Bom, 1, 0, bom.Length);
        var finalHeader = await Read(Bytes(Header)); Equal(SessionLogReadStatus.Complete, finalHeader.Status);
        Diagnostic(finalHeader, SessionLogDiagnosticCode.FinalLineWithoutNewline, 1, 0, Bytes(Header).Length);
    }

    public static async Task Limits()
    {
        var input = Bytes(Header + "\n" + Entry + "\n");
        var exact = await Read(input, new(MaximumInputBytes: input.Length, MaximumLineBytes: Bytes(Entry).Length,
            MaximumLines: 2, MaximumRecords: 2, ReadBufferBytes: 1));
        Equal(SessionLogReadStatus.Complete, exact.Status); Check(exact.SourceComplete, "Exact input limit was not EOF-probed."); Equal(2, exact.ValidatedPrefix.Length);
        var limited = await Read(Join(input, Bytes("extra unread payload")), new(MaximumInputBytes: input.Length, ReadBufferBytes: 65_536));
        Equal(SessionLogReadStatus.ResourceLimit, limited.Status); Check(!limited.SourceComplete, "Oversized capture claimed full source retention.");
        Equal(input.Length + 1, limited.OriginalBytes.Length); Equal(2, limited.ValidatedPrefix.Length);
        Diagnostic(limited, SessionLogDiagnosticCode.InputByteLimit, 0, input.Length, 1);
        var cutRecord = await Read(input, new(MaximumInputBytes: input.Length - 2));
        Equal(1, cutRecord.ValidatedPrefix.Length); Equal(Bytes(Header + "\n").Length, cutRecord.ValidatedPrefixByteLength);
        var line = await Read(input, new(MaximumLineBytes: Bytes(Entry).Length - 1));
        Equal(SessionLogReadStatus.ResourceLimit, line.Status); Equal(1, line.ValidatedPrefix.Length);
        Diagnostic(line, SessionLogDiagnosticCode.LineByteLimit, 2, Bytes(Header + "\n").Length, Bytes(Entry + "\n").Length);
        var records = await Read(input, new(MaximumRecords: 1));
        Diagnostic(records, SessionLogDiagnosticCode.RecordCountLimit, 2, Bytes(Header + "\n").Length, Bytes(Entry + "\n").Length);
        var physical = Bytes(Header + "\n\n" + Entry + "\n");
        var lines = await Read(physical, new(MaximumLines: 2));
        Diagnostic(lines, SessionLogDiagnosticCode.LineCountLimit, 3, Bytes(Header + "\n\n").Length, Bytes(Entry + "\n").Length);
        var codec = await Read(input, new(CodecOptions: new(MaximumRecordCharacters: Header.Length - 1)));
        Diagnostic(codec, SessionLogDiagnosticCode.CodecLimit, 1, 0, Bytes(Header + "\n").Length);
        foreach (var options in new[] { new SessionLogReaderOptions(MaximumInputBytes: 0), new SessionLogReaderOptions(MaximumInputBytes: int.MaxValue),
            new SessionLogReaderOptions(MaximumLineBytes: 0), new SessionLogReaderOptions(MaximumLines: 0), new SessionLogReaderOptions(MaximumRecords: 0),
            new SessionLogReaderOptions(ReadBufferBytes: 0), new SessionLogReaderOptions(ReadBufferBytes: 65_537) })
            Throws<ArgumentOutOfRangeException>(() => new SessionLogReader(options));
    }

    public static async Task FaultAndCleanup()
    {
        var reader = new SessionLogReader();
        var owned = new ProbeStream(Bytes(Header)) { GateDispose = true };
        var success = reader.ReadAsync(owned, leaveOpen: false);
        await owned.DisposeEntered.Task; Check(!success.IsCompleted, "Reader returned before owned cleanup.");
        owned.DisposeRelease.SetResult(); Equal(SessionLogReadStatus.Complete, (await success).Status); Check(owned.DisposeFinished.Task.IsCompleted, "Cleanup did not finish.");
        foreach (var alsoThrowDispose in new[] { false, true })
        {
            var faulty = new ProbeStream(Bytes(Header)) { ThrowRead = true, ThrowOnReadCall = 2, ChunkBytes = 16, ThrowDispose = alsoThrowDispose, GateDispose = true };
            var failed = reader.ReadAsync(faulty, leaveOpen: false);
            await faulty.DisposeEntered.Task; Check(!failed.IsCompleted, "Read failure escaped before cleanup.");
            faulty.DisposeRelease.SetResult(); await ReadFailure(failed, SessionLogReadFailure.ReadFailed);
            Equal(2, faulty.ReadCalls);
            Check(faulty.DisposeFinished.Task.IsCompleted, "Read error skipped disposal completion.");
        }
        var cleanupFailure = new ProbeStream(Bytes(Header)) { ThrowDispose = true };
        await ReadFailure(reader.ReadAsync(cleanupFailure, leaveOpen: false), SessionLogReadFailure.CleanupFailed);
        using var uncancelledCaller = new CancellationTokenSource();
        var unrelatedCancellation = new ProbeStream(Bytes(Header)) { ThrowUnrelatedCancellation = true, GateDispose = true };
        var unrelatedRun = reader.ReadAsync(unrelatedCancellation, leaveOpen: false, uncancelledCaller.Token);
        await unrelatedCancellation.DisposeEntered.Task;
        Check(!unrelatedRun.IsCompleted, "Unrelated cancellation escaped before cleanup.");
        unrelatedCancellation.DisposeRelease.SetResult();
        await ReadFailure(unrelatedRun, SessionLogReadFailure.ReadFailed);
        Check(!uncancelledCaller.IsCancellationRequested && unrelatedCancellation.DisposeFinished.Task.IsCompleted,
            "Unrelated stream cancellation was treated as caller cancellation or skipped cleanup.");
        var callerOwned = new ProbeStream(Bytes(Header)) { ThrowRead = true };
        await ReadFailure(reader.ReadAsync(callerOwned), SessionLogReadFailure.ReadFailed); Equal(0, callerOwned.DisposeCalls);
        await callerOwned.DisposeAsync();
    }

    public static async Task CancellationAndOwnership()
    {
        var reader = new SessionLogReader();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var before = new ProbeStream(Bytes(Header));
        await Cancelled(reader.ReadAsync(before, leaveOpen: false, cancelled.Token), cancelled.Token); Equal(0, before.ReadCalls); Equal(1, before.DisposeCalls);
        var retained = new ProbeStream(Bytes(Header));
        await Cancelled(reader.ReadAsync(retained, cancellationToken: cancelled.Token), cancelled.Token); Equal(0, retained.DisposeCalls); await retained.DisposeAsync();
        using var pendingCancellation = new CancellationTokenSource();
        var pending = new ProbeStream(Bytes(Header)) { GateRead = true, GateDispose = true, ThrowDispose = true };
        var run = reader.ReadAsync(pending, leaveOpen: false, pendingCancellation.Token);
        await pending.ReadEntered.Task; pendingCancellation.Cancel(); await pending.DisposeEntered.Task;
        Check(!run.IsCompleted, "Cancellation returned while owned cleanup was pending.");
        pending.DisposeRelease.SetResult(); await Cancelled(run, pendingCancellation.Token); Check(pending.DisposeFinished.Task.IsCompleted, "Cancellation cleanup did not finish.");
        using var duringCleanup = new CancellationTokenSource();
        var cleanup = new ProbeStream(Bytes(Header)) { GateDispose = true };
        var completedRead = reader.ReadAsync(cleanup, leaveOpen: false, duringCleanup.Token);
        await cleanup.DisposeEntered.Task; duringCleanup.Cancel(); cleanup.DisposeRelease.SetResult(); await Cancelled(completedRead, duringCleanup.Token);
    }

    public static async Task ReadOnlyFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), "pisharp-session-reader-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var bytes = Bytes(Header + "\r\n" + Entry); var expectedHash = SHA256.HashData(bytes);
        try
        {
            await File.WriteAllBytesAsync(path, bytes); File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            var reader = new SessionLogReader(); var first = await reader.ReadFileAsync(path); var again = await reader.ReadFileAsync(path);
            Equal(SessionLogReadStatus.Complete, first.Status); Equal(2, first.ValidatedPrefix.Length);
            Check(first.OriginalBytes.AsSpan().SequenceEqual(bytes), "Readonly file bytes changed in the result.");
            Check(first.Diagnostics.SequenceEqual(again.Diagnostics), "Repeated file reads changed diagnostics.");
            Check(SHA256.HashData(await File.ReadAllBytesAsync(path)).AsSpan().SequenceEqual(expectedHash), "Readonly source was modified.");
            Check((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0, "Reader changed source attributes.");
            await ReadFailure(reader.ReadFileAsync(path + ".absent"), SessionLogReadFailure.ReadFailed);
        }
        finally
        {
            if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
        }
    }

    private static async Task<SessionLogReadResult> Read(byte[] input, SessionLogReaderOptions? options = null)
    { using var stream = new MemoryStream(input); return await new SessionLogReader(options).ReadAsync(stream); }
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static byte[] Join(params byte[][] values) => values.SelectMany(value => value).ToArray();
    private static void Diagnostic(SessionLogReadResult result, SessionLogDiagnosticCode code, int line, int offset, int length)
    {
        var item = result.Diagnostics.Single(value => value.Code == code);
        Equal(line, item.LineNumber); Equal(offset, item.ByteOffset); Equal(length, item.ByteLength);
        Check(!item.Message.Contains("secret-private-payload", StringComparison.Ordinal), "Diagnostic exposed rejected bytes.");
    }
    private static async Task ReadFailure(Task<SessionLogReadResult> run, SessionLogReadFailure expected)
    {
        try { await run; }
        catch (SessionLogReadException error)
        { Equal(expected, error.Failure); Check(error.InnerException is null && !error.Message.Contains("secret-private-payload", StringComparison.Ordinal), "I/O failure exposed source details."); return; }
        throw new InvalidOperationException("Expected session log read failure.");
    }
    private static async Task Cancelled(Task<SessionLogReadResult> run, CancellationToken expectedToken)
    { try { await run; } catch (OperationCanceledException error) { Equal(expectedToken, error.CancellationToken); return; } throw new InvalidOperationException("Expected cancellation."); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }

    private sealed class ProbeStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        public bool GateRead { get; init; }
        public bool GateDispose { get; init; }
        public bool ThrowRead { get; init; }
        public bool ThrowUnrelatedCancellation { get; init; }
        public int ThrowOnReadCall { get; init; } = 1;
        public bool ThrowDispose { get; init; }
        public int ChunkBytes { get; init; } = int.MaxValue;
        public int ReadCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public TaskCompletionSource ReadEntered { get; } = Gate();
        public TaskCompletionSource ReadRelease { get; } = Gate();
        public TaskCompletionSource DisposeEntered { get; } = Gate();
        public TaskCompletionSource DisposeRelease { get; } = Gate();
        public TaskCompletionSource DisposeFinished { get; } = Gate();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++; ReadEntered.TrySetResult();
            if (GateRead) await ReadRelease.Task.WaitAsync(cancellationToken);
            if (ThrowUnrelatedCancellation) throw new OperationCanceledException("secret-private-payload", new CancellationToken(true));
            if (ThrowRead && ReadCalls >= ThrowOnReadCall) throw new IOException("secret-private-payload");
            return await _inner.ReadAsync(buffer[..Math.Min(buffer.Length, ChunkBytes)], cancellationToken);
        }
        public override async ValueTask DisposeAsync()
        {
            DisposeCalls++; DisposeEntered.TrySetResult();
            try
            {
                if (GateDispose) await DisposeRelease.Task;
                await _inner.DisposeAsync();
                if (ThrowDispose) throw new IOException("secret-private-payload");
            }
            finally { DisposeFinished.TrySetResult(); }
            GC.SuppressFinalize(this);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
