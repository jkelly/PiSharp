using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Import;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class SessionCopyServiceTests
{
    private const string Time = "2024-01-01T00:00:00.000Z";
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("session exact copy preserves immutable source bytes opaque tokens and all branches", ExactCopyAndBranches),
        ("session compatible copy preserves every opaque field with explicit zero omissions", CompatibleRetention),
        ("session explicit copied v1 identities compaction and v2 hook migration retain receipts", LegacyCopies),
        ("session future damaged UTF8 and malformed forests inspect without publication", BlockedInspection),
        ("session copy source aliases destination collisions and traversal-like IDs have no overwrite", PathsAndCollisions),
        ("session copy exact input output line record and Unicode budgets precede effects", Budgets),
        ("session copy cancellation joins owned reads temp disposal and cleanup before returning", CancellationAndCleanup),
        ("session copy IO faults uncertain publication and late cancellation retain honest receipts", FaultsAndPublication),
        ("session archive cancellation joins held cleanup and reports post-effect publication honestly", ArchiveCleanup)
    ];

    private static async Task ArchiveCleanup()
    {
        using var files=new Files();var bytes=Utf8.GetBytes("{bad}\nopaque archive payload");await File.WriteAllBytesAsync(files.Source,bytes);
        var entered=Gate();var disposing=Gate();var release=Gate();using var caller=new CancellationTokenSource();
        var io=new FaultFiles{WriteWrap=stream=>new FaultStream(stream){WriteEntered=entered},ReadWrap=stream=>new FaultStream(stream){DisposeEntered=disposing,DisposeRelease=release}};
        var operation=new SessionCopyService(fileSystem:io).CopyAsync(new(files.Source,files.Destination,SessionCopyFormat.NativeArchiveExact),caller.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));caller.Cancel();await disposing.Task.WaitAsync(TimeSpan.FromSeconds(5));Check(!operation.IsCompleted,"Archive detached source cleanup.");release.TrySetResult();
            var canceled=await ThrowsAsync<SessionCopyCanceledException>(async()=>await operation);Check(canceled.CancellationToken==caller.Token&&!canceled.TemporaryMayRemain,"Archive cancellation lost ownership.");Check(!File.Exists(files.Destination),"Canceled archive published.");NoTemps(files);
        }
        finally{caller.Cancel();release.TrySetResult();try{await operation;}catch(OperationCanceledException){}}
        var uncertain=await ThrowsAsync<SessionCopyException>(()=>new SessionCopyService(fileSystem:new FaultFiles{PublishAfterEffectFault=true}).CopyAsync(new(files.Source,files.Destination,SessionCopyFormat.NativeArchiveExact)));
        Equal(SessionCopyPublication.Uncertain,uncertain.Publication);Check(File.Exists(files.Destination),"Uncertain archive fixture did not publish its effect.");Check((await File.ReadAllBytesAsync(files.Destination)).AsSpan().SequenceEqual(bytes),"Archival post-effect bytes changed.");Equal(Hash(bytes),Hash(await File.ReadAllBytesAsync(files.Source)));NoTemps(files);
    }
    private static async Task ExactCopyAndBranches()
    {
        using var files = new Files(); var bytes = Current(files);
        await File.WriteAllBytesAsync(files.Source, bytes);
        var original = Hash(bytes);
        var result = await new SessionCopyService().CopyAsync(new(files.Source, files.Destination));
        Equal(SessionCopyStatus.Published, result.Status); Equal(original, result.Inspection.SourceSha256); Equal(original, result.OutputSha256);
        Check(result.Inspection.OriginalBytes.AsSpan().SequenceEqual(bytes), "Inspection changed original bytes.");
        Check((await File.ReadAllBytesAsync(files.Destination)).AsSpan().SequenceEqual(bytes), "Exact copy reformatted JSONL.");
        Equal(original, Hash(await File.ReadAllBytesAsync(files.Source))); NoTemps(files);
        await using var store = await SessionLogStore.OpenAsync(files.Destination);
        Equal("../../source-id", store.Snapshot.Header.Id); Equal(4, store.Snapshot.Entries.Length);
        var projector = new SessionContextProjector();
        var left = projector.Project(store.Snapshot.Entries, "../left"); var right = projector.Project(store.Snapshot.Entries, "right");
        Equal("left", left.Messages[^1].WireBody.Value.GetProperty("content").GetString());
        Equal("right", right.Messages[^1].WireBody.Value.GetProperty("content").GetString());
        Check(!left.Ancestry.Any(entry => entry.Id == "right"), "Copied branch included a sibling.");
        var owned = result.Inspection.OriginalBytes;
        await File.WriteAllBytesAsync(files.Source, Utf8.GetBytes("changed later"));
        Check(owned.AsSpan().SequenceEqual(bytes), "Inspection borrowed a later source mutation.");
    }

    private static async Task CompatibleRetention()
    {
        using var files = new Files(); var bytes = Current(files); await File.WriteAllBytesAsync(files.Source, bytes);
        var result = await new SessionCopyService().CopyAsync(new(files.Source, files.Destination, SessionCopyFormat.CompatibleCurrentJsonl));
        Equal(SessionCopyStatus.Published, result.Status); Equal(0, result.OmittedRecords); Equal(0, result.OmittedFields);
        Check(result.Diagnostics.Any(item => item.Code == SessionCopyDiagnosticCode.SemanticCompatibilityUnverified), "Compatibility became an unverified parity claim.");
        var output = await new SessionLogReader().ReadFileAsync(files.Destination);
        Equal(SessionLogReadStatus.Complete, output.Status);
        var opaque = output.ValidatedPrefix[^1].Entry.WireBody.Value.GetProperty("opaque");
        Equal("9007199254740993", opaque.GetProperty("wide").GetRawText()); Equal("1.2300e+2", opaque.GetProperty("scaled").GetRawText());
        Equal("1e400", opaque.GetProperty("huge").GetRawText()); Equal("-0", opaque.GetProperty("negativeZero").GetRawText());
        Equal(JsonValueKind.Null, opaque.GetProperty("nil").ValueKind); Check(!opaque.TryGetProperty("absent", out _), "Missing data became null.");
        Equal("System.Type, Fake.Assembly", opaque.GetProperty("native").GetProperty("$type").GetString());
        Equal("unopened:/private/secret-image", opaque.GetProperty("image").GetString());
        Equal(Hash(bytes), Hash(await File.ReadAllBytesAsync(files.Source))); NoTemps(files);
    }

    private static async Task LegacyCopies()
    {
        using var files = new Files(); var service = new SessionCopyService();
        var compaction = """{"type":"compaction","timestamp":"2024-01-01T00:00:00.000Z","summary":"summary","firstKeptEntryIndex":1,"tokensBefore":10,"opaque":{"n":1e400,"nil":null}}""";
        var source = Utf8.GetBytes(Header(files, null) + "\r\n" + LegacyUser() + "\n" + compaction);
        await File.WriteAllBytesAsync(files.Source, source);
        var blocked = await service.CopyAsync(new(files.Source, files.Destination, SessionCopyFormat.CompatibleCurrentJsonl));
        Equal(SessionCopyStatus.Blocked, blocked.Status); Check(blocked.Inspection.Migration!.Diagnostics.Any(item => item.Code == SessionEntryMigrationCode.IdPlanRequired), "V1 silently generated IDs.");
        var converted = await service.CopyAsync(new(files.Source, files.Destination, SessionCopyFormat.CompatibleCurrentJsonl, ["u", "c"]));
        Equal(SessionCopyStatus.Published, converted.Status); Check(converted.Inspection.Migration!.VersionDefaulted, "Missing v1 version was lost.");
        Check(converted.Inspection.Migration.Receipts.Any(item => item.Transform == SessionEntryMigrationTransform.CompactionBoundary), "Compaction rewrite lacked a receipt.");
        await using (var reopened = await SessionLogStore.OpenAsync(files.Destination))
        {
            Equal("u", reopened.Snapshot.Entries[1].ParentId);
            Equal("u", reopened.Snapshot.Entries[1].WireBody.Value.GetProperty("firstKeptEntryId").GetString());
        }
        Equal(Hash(source), Hash(await File.ReadAllBytesAsync(files.Source)));
        var exactLegacy = await service.CopyAsync(new(files.Source, files.File("legacy-exact.jsonl"), V1EntryIds: ["u", "c"]));
        Equal(SessionCopyStatus.Blocked, exactLegacy.Status); Check(!File.Exists(exactLegacy.DestinationPath), "Exact legacy copy pretended to be runnable current data.");
        const string hook = """{"type":"message","id":"hook","parentId":null,"timestamp":"2024-01-01T00:00:00.000Z","message":{"role":"hookMessage","timestamp":7,"customType":"plugin-state","content":"text","display":false,"details":{"n":1e400,"nil":null}}}""";
        var v2 = Utf8.GetBytes(Header(files, "2") + "\n" + hook + "\n"); await File.WriteAllBytesAsync(files.Source, v2);
        var second = await service.CopyAsync(new(files.Source, files.File("v2.jsonl"), SessionCopyFormat.CompatibleCurrentJsonl));
        Equal(SessionCopyStatus.Published, second.Status); Equal(2L, second.Inspection.SourceVersion);
        Equal("custom", second.Inspection.CurrentRecords[1].WireBody.Value.GetProperty("message").GetProperty("role").GetString());
        Check(second.Inspection.Migration!.Receipts.Any(item => item.Field == "/message/role"), "Hook conversion lacked its receipt.");
        Equal(Hash(v2), Hash(await File.ReadAllBytesAsync(files.Source))); NoTemps(files);
    }

    private static async Task BlockedInspection()
    {
        using var files = new Files(); var faults = new FaultFiles(); var service = new SessionCopyService(fileSystem: faults);
        var future = Utf8.GetBytes(Header(files, "4") + "\n" + User("r", null, "future retained"));
        await File.WriteAllBytesAsync(files.Source, future);
        var inspected = await service.InspectAsync(files.Source);
        Check(!inspected.CanPublishCurrent && inspected.OriginalBytes.AsSpan().SequenceEqual(future), "Future inspection discarded source bytes.");
        Check(inspected.Migration!.Diagnostics.Any(item => item.Code == SessionEntryMigrationCode.FutureVersion), "Future version was inferred as current.");
        foreach (var bytes in new[]
        {
            future, Utf8.GetBytes(Header(files, "3") + "\n{bad}\n" + User("child", "lost", "descendant")),
            Utf8.GetBytes(Header(files, "3") + "\n" + User("a", "missing", "orphan")),
            Utf8.GetBytes(Header(files, "3") + "\n" + User("a", "b", "forward") + "\n" + User("b", null, "parent")),
            Utf8.GetBytes(Header(files, "3") + "\n" + User("a", "b", "cycle") + "\n" + User("b", "a", "cycle")),
            Utf8.GetBytes(Header(files, "3") + "\n" + User("a", null, "duplicate") + "\n" + User("a", null, "duplicate")),
            Utf8.GetBytes(Header(files, "3") + "\n{\"type\":"),
            Utf8.GetBytes(Header(files, "3") + "\n").Concat(new byte[] { 0xc3 }).ToArray(),
            Array.Empty<byte>()
        })
        {
            await File.WriteAllBytesAsync(files.Source, bytes);
            var result = await service.CopyAsync(new(files.Source, files.Destination, SessionCopyFormat.CompatibleCurrentJsonl));
            Equal(SessionCopyStatus.Blocked, result.Status); Check(!File.Exists(files.Destination), "Blocked source published a runnable copy.");
            Equal(Hash(bytes), Hash(await File.ReadAllBytesAsync(files.Source)));
        }
        Equal(0, faults.Created); NoTemps(files);
    }

    private static async Task PathsAndCollisions()
    {
        using var files = new Files(); var bytes = Current(files); await File.WriteAllBytesAsync(files.Source, bytes);
        Check(bytes.Length > Utf8.GetString(bytes).Length, "Unicode fixture did not exercise actual multibyte UTF-8.");
        var service = new SessionCopyService();
        foreach (var request in new[]
        {
            new SessionCopyRequest(files.Source, files.Source), new SessionCopyRequest("relative.jsonl", files.Destination),
            new SessionCopyRequest(files.Source, Path.Combine(files.Root, "..", Path.GetFileName(files.Root), "alias.jsonl")),
            new SessionCopyRequest(files.Source, files.Root)
        })
            Equal(SessionCopyFailure.InvalidRequest, (await ThrowsAsync<SessionCopyException>(() => service.CopyAsync(request))).Failure);
        var attributes = File.GetAttributes(files.Source); File.SetAttributes(files.Source, attributes | FileAttributes.ReadOnly);
        try { Equal(SessionCopyStatus.Published, (await service.CopyAsync(new(files.Source, files.Destination))).Status); }
        finally { File.SetAttributes(files.Source, attributes); }
        var repeated = await ThrowsAsync<SessionCopyException>(() => service.CopyAsync(new(files.Source, files.Destination)));
        Equal(SessionCopyFailure.InvalidRequest, repeated.Failure); Equal(Hash(bytes), Hash(await File.ReadAllBytesAsync(files.Destination)));
        var competing = files.File("competing.jsonl");
        var faultFiles = new FaultFiles { BeforePublish = async (_, destination) => await File.WriteAllTextAsync(destination, "owned competing bytes") };
        var collision = await ThrowsAsync<SessionCopyException>(() => new SessionCopyService(fileSystem: faultFiles).CopyAsync(new(files.Source, competing)));
        Equal(SessionCopyFailure.PublishFailed, collision.Failure); Equal("owned competing bytes", await File.ReadAllTextAsync(competing));
        Equal(Hash(bytes), Hash(await File.ReadAllBytesAsync(files.Source))); NoTemps(files);
    }

    private static async Task Budgets()
    {
        using var files = new Files(); var bytes = Current(files); await File.WriteAllBytesAsync(files.Source, bytes);
        var exact = new SessionCopyService(new(new(MaximumInputBytes: bytes.Length), bytes.Length));
        Equal(SessionCopyStatus.Published, (await exact.CopyAsync(new(files.Source, files.Destination))).Status);
        var faults = new FaultFiles();
        foreach (var options in new[]
        {
            new SessionCopyOptions(new(MaximumInputBytes: bytes.Length - 1)),
            new SessionCopyOptions(new(MaximumLineBytes: 20)), new SessionCopyOptions(new(MaximumRecords: 2)),
            new SessionCopyOptions(new(MaximumLines: 2)), new SessionCopyOptions(MaximumOutputBytes: bytes.Length - 1)
        })
        {
            var result = await new SessionCopyService(options, faults).CopyAsync(new(files.Source, files.File(Guid.NewGuid().ToString("N") + ".jsonl")));
            Equal(SessionCopyStatus.Blocked, result.Status);
        }
        Equal(0, faults.Created); Equal(Hash(bytes), Hash(await File.ReadAllBytesAsync(files.Source))); NoTemps(files);
    }

    private static async Task CancellationAndCleanup()
    {
        using var files = new Files(); var bytes = Current(files); await File.WriteAllBytesAsync(files.Source, bytes);
        var beforeFiles = new FaultFiles(); using var before = new CancellationTokenSource(); before.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => new SessionCopyService(fileSystem: beforeFiles).CopyAsync(new(files.Source, files.Destination), before.Token));
        Equal(0, beforeFiles.Opened); Equal(0, beforeFiles.Created);
        foreach (var atWrite in new[] { false, true })
        {
            var entered = Gate(); var disposeEntered = Gate(); var disposeRelease = Gate();
            var io = new FaultFiles
            {
                ReadWrap = source => new FaultStream(source) { ReadEntered = atWrite ? null : entered, DisposeEntered = disposeEntered, DisposeRelease = disposeRelease },
                WriteWrap = stream => new FaultStream(stream) { WriteEntered = atWrite ? entered : null }
            };
            using var caller = new CancellationTokenSource();
            var copy = new SessionCopyService(fileSystem: io).CopyAsync(new(files.Source, files.Destination), caller.Token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); caller.Cancel();
                await disposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!copy.IsCompleted, "Cancellation returned before source owned cleanup settled.");
                disposeRelease.TrySetResult();
                var canceled = await ThrowsAsync<SessionCopyCanceledException>(async () => await copy.WaitAsync(TimeSpan.FromSeconds(5)));
                Check(canceled.CancellationToken == caller.Token && !canceled.TemporaryMayRemain, "Canceled copy lost ownership diagnostics.");
                Check(!File.Exists(files.Destination), "Prepublication cancellation published a destination.");
                NoTemps(files); Equal(Hash(bytes), Hash(await File.ReadAllBytesAsync(files.Source)));
            }
            finally
            {
                caller.Cancel(); disposeRelease.TrySetResult();
                try { await copy; } catch (OperationCanceledException) { }
            }
        }
    }

    private static async Task FaultsAndPublication()
    {
        using var files = new Files(); var bytes = Current(files); await File.WriteAllBytesAsync(files.Source, bytes);
        var faultIndex = 0;
        foreach (var (io, expected) in new[]
        {
            (new FaultFiles { ReadWrap = source => new FaultStream(source) { ReadFault = true, DisposeFault = true } }, SessionCopyFailure.ReadFailed),
            (new FaultFiles { ReadWrap = source => new FaultStream(source) { ForeignCancel = true } }, SessionCopyFailure.ReadFailed),
            (new FaultFiles { CreateFault = true }, SessionCopyFailure.CreateFailed),
            (new FaultFiles { WriteWrap = stream => new FaultStream(stream) { WriteFault = true } }, SessionCopyFailure.WriteFailed),
            (new FaultFiles { WriteWrap = stream => new FaultStream(stream) { FlushFault = true } }, SessionCopyFailure.FlushFailed),
            (new FaultFiles { WriteWrap = stream => new FaultStream(stream) { DisposeFault = true } }, SessionCopyFailure.CleanupFailed)
        })
        {
            var failure = await ThrowsAsync<SessionCopyException>(() => new SessionCopyService(fileSystem: io).CopyAsync(new(files.Source, files.Destination)));
            Equal(expected, failure.Failure); Check(failure.InnerException is null && !failure.Message.Contains("private", StringComparison.Ordinal), "Diagnostic leaked trusted I/O payload.");
            if (faultIndex++ == 0) Check(!failure.CleanupFailures.IsEmpty, "Source cleanup failure masked or disappeared behind the primary read fault.");
            Check(!File.Exists(files.Destination), "Prepublication fault reported hidden publication.");
            if (io.ReadWrap is not null && expected == SessionCopyFailure.ReadFailed && !io.Created.Equals(0)) throw new InvalidOperationException("Read fault created temp.");
            NoTemps(files);
        }
        var cleanupFault = await ThrowsAsync<SessionCopyException>(() => new SessionCopyService(fileSystem: new FaultFiles
        {
            WriteWrap = stream => new FaultStream(stream) { WriteFault = true }, DeleteAfterEffectFault = true
        }).CopyAsync(new(files.Source, files.Destination)));
        Equal(SessionCopyFailure.WriteFailed, cleanupFault.Failure);
        Check(cleanupFault.TemporaryMayRemain && !cleanupFault.CleanupFailures.IsEmpty, "Uncertain temp deletion masked its primary write failure.");
        using var late = new CancellationTokenSource();
        var lateIo = new FaultFiles { AfterPublish = () => { late.Cancel(); return ValueTask.CompletedTask; } };
        var published = await new SessionCopyService(fileSystem: lateIo).CopyAsync(new(files.Source, files.Destination), late.Token);
        Equal(SessionCopyStatus.Published, published.Status); Check(late.IsCancellationRequested, "Late cancellation control was not delivered.");
        var uncertainPath = files.File("uncertain.jsonl");
        var uncertain = await ThrowsAsync<SessionCopyException>(() => new SessionCopyService(fileSystem: new FaultFiles { PublishAfterEffectFault = true })
            .CopyAsync(new(files.Source, uncertainPath)));
        Equal(SessionCopyPublication.Uncertain, uncertain.Publication); Check(File.Exists(uncertainPath), "Post-effect fault fixture failed to publish.");
        var cleanupPath = files.File("cleanup.jsonl");
        var cleanup = await new SessionCopyService(fileSystem: new FaultFiles { ReadWrap = source => new FaultStream(source) { DisposeFault = true } })
            .CopyAsync(new(files.Source, cleanupPath));
        Equal(SessionCopyStatus.PublishedWithCleanupFailure, cleanup.Status); Check(cleanup.Published && cleanup.OutputSha256 == Hash(bytes), "Cleanup failure lost the known published receipt.");
        Equal(Hash(bytes), Hash(await File.ReadAllBytesAsync(files.Source))); NoTemps(files);
    }

    private static string Header(Files files, string? version) => "{\"type\":\"session\",\"id\":\"../../source-id\",\"timestamp\":" +
        JsonSerializer.Serialize(Time) + ",\"cwd\":" + JsonSerializer.Serialize(files.Root) + (version is null ? "" : ",\"version\":" + version) + ",\"optional\":null}";
    private static string User(string id, string? parent, string text) => "{\"type\":\"message\",\"id\":" + JsonSerializer.Serialize(id) +
        ",\"parentId\":" + JsonSerializer.Serialize(parent) + ",\"timestamp\":" + JsonSerializer.Serialize(Time) +
        ",\"message\":{\"role\":\"user\",\"timestamp\":1,\"content\":" + JsonSerializer.Serialize(text) + "}}";
    private static string LegacyUser() => "{\"type\":\"message\",\"timestamp\":" + JsonSerializer.Serialize(Time) +
        ",\"message\":{\"role\":\"user\",\"timestamp\":1,\"content\":\"legacy\",\"opaque\":-0.00e+09}}";
    private static byte[] Current(Files files)
    {
        const string opaque = """{ "type":"future_state","id":"opaque","parentId":"r","timestamp":"opaque", "opaque":{"wide":9007199254740993,"scaled":1.2300e+2,"huge":1e400,"negativeZero":-0,"nil":null,"native":{"$type":"System.Type, Fake.Assembly","$delegate":"unrestored"},"image":"unopened:/private/secret-image","text":"\u03c0\ud83d\ude42"} }""";
        return Utf8.GetBytes(" \t\r\n" + Header(files, "3") + "\r\n" + User("r", null, "root") +
            "\n" + User("../left", "r", "left") + "\r\n\n" + User("right", "r", "right") +
            "\n" + opaque.Replace("\\u03c0\\ud83d\\ude42", "\u03c0\U0001f642", StringComparison.Ordinal));
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void NoTemps(Files files) => Check(!Directory.EnumerateFiles(files.Root, ".pisharp-copy-*.tmp").Any(), "Owned temporary file remained after settled operation.");
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Expected " + expected + ", observed " + actual + ".");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task<T> ThrowsAsync<T>(Func<Task> run) where T : Exception
    { try { await run(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name + "."); }

    private sealed class Files : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pisharp-copy-" + Guid.NewGuid().ToString("N"));
        public string Source => File("source.jsonl"); public string Destination => File("destination.jsonl");
        public Files() => Directory.CreateDirectory(Root);
        public string File(string name) => Path.Combine(Root, name);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
    private sealed class FaultFiles : ISessionCopyFileSystem
    {
        public Func<Stream, Stream>? ReadWrap { get; init; }
        public Func<Stream, Stream>? WriteWrap { get; init; }
        public Func<string, string, ValueTask>? BeforePublish { get; init; }
        public Func<ValueTask>? AfterPublish { get; init; }
        public bool CreateFault { get; init; }
        public bool PublishAfterEffectFault { get; init; }
        public bool DeleteAfterEffectFault { get; init; }
        public int Opened { get; private set; } public int Created { get; private set; }
        public async ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
        { Opened++; var stream = await SessionCopyService.LocalFileSystem.OpenReadAsync(path, cancellationToken); return ReadWrap?.Invoke(stream) ?? stream; }
        public async ValueTask<Stream> CreateNewTemporaryAsync(string path)
        { Created++; if (CreateFault) throw new IOException("private create cause"); var stream = await SessionCopyService.LocalFileSystem.CreateNewTemporaryAsync(path); return WriteWrap?.Invoke(stream) ?? stream; }
        public async ValueTask PublishNewAsync(string temporaryPath, string destinationPath)
        {
            if (BeforePublish is not null) await BeforePublish(temporaryPath, destinationPath);
            await SessionCopyService.LocalFileSystem.PublishNewAsync(temporaryPath, destinationPath);
            if (AfterPublish is not null) await AfterPublish();
            if (PublishAfterEffectFault) throw new IOException("private checkpoint cause");
        }
        public async ValueTask DeleteTemporaryAsync(string path)
        {
            await SessionCopyService.LocalFileSystem.DeleteTemporaryAsync(path);
            if (DeleteAfterEffectFault) throw new IOException("private cleanup cause");
        }
    }
    private sealed class FaultStream(Stream inner) : Stream
    {
        public bool ReadFault { get; init; } public bool ForeignCancel { get; init; }
        public bool WriteFault { get; init; } public bool FlushFault { get; init; } public bool DisposeFault { get; init; }
        public TaskCompletionSource? ReadEntered { get; init; } public TaskCompletionSource? WriteEntered { get; init; }
        public TaskCompletionSource? DisposeEntered { get; init; } public TaskCompletionSource? DisposeRelease { get; init; }
        public override bool CanRead => inner.CanRead; public override bool CanWrite => inner.CanWrite; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (ReadFault) throw new IOException("private read cause");
            if (ForeignCancel) throw new OperationCanceledException("private foreign cancellation");
            if (ReadEntered is not null) { ReadEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
            return await inner.ReadAsync(buffer, cancellationToken);
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            if (WriteFault || WriteEntered is not null) await inner.WriteAsync(bytes[..Math.Min(5, bytes.Length)], cancellationToken);
            if (WriteFault) throw new IOException("private partial write cause");
            if (WriteEntered is not null) { WriteEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
            await inner.WriteAsync(bytes, cancellationToken);
        }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        { await inner.FlushAsync(cancellationToken); if (FlushFault) throw new IOException("private flush cause"); }
        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync(); DisposeEntered?.TrySetResult();
            if (DisposeRelease is not null) await DisposeRelease.Task;
            if (DisposeFault) throw new IOException("private cleanup cause");
            GC.SuppressFinalize(this);
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
