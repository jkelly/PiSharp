using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class SessionLogStoreTests
{
    private const string HeaderJson = """{"type":"session","version":3,"id":"session","timestamp":"header-time","cwd":"explicit/path"}""";
    private static readonly SessionEntryCodec Codec = new();
    private static SessionEntry Header() => Codec.Parse(HeaderJson);
    private static SessionEntry Entry(string id, string? parent = null) => Codec.Parse("{\"type\":\"custom\",\"id\":" +
        System.Text.Json.JsonSerializer.Serialize(id) + ",\"parentId\":" + System.Text.Json.JsonSerializer.Serialize(parent) +
        ",\"timestamp\":\"entry-time\",\"customType\":\"state\",\"data\":{\"opaque\":1e400}}");
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("session store creates appends durably publishes immutable snapshots and reopens", CreateAppendReopen),
        ("session store refuses overwrite damaged future invalid graphs and fake durability", Admission),
        ("session store preserves nonterminated existing bytes and appends explicit LF", NonterminatedFraming),
        ("session store serializes commits and shares awaited disposal with queued writers", SerializationAndDisposal),
        ("session store partial write flush durable flush and checkpoint faults poison lease", FaultStages),
        ("session store prewrite cancellation preserves bytes late cancellation acknowledges commit", Cancellation),
        ("session store inclusive batch record line and total growth limits reject before writes", GrowthLimits),
        ("session store Windows lease excludes an independent child writer process", IndependentWriterLease)
    ];

    public static async Task CreateAppendReopen()
    {
        var path = TempPath();
        try
        {
            await using (var store = await SessionLogStore.CreateNewAsync(path, Header()))
            {
                var before = store.Snapshot; Equal(0, before.Entries.Length); Equal(HeaderJson.Length + 1L, before.CommittedByteLength);
                var committed = await store.AppendAsync([Entry("one"), Entry("two", "one")]);
                Check(committed.Accepted && committed.Flushed && committed.DurableCheckpointAcknowledged, "Checkpoint omitted a stage.");
                Equal(1L, committed.Sequence); Equal(before.CommittedByteLength, committed.ByteOffset);
                Equal("two", committed.Snapshot.LeafId); Equal(2, committed.Snapshot.ById.Count); Equal(2, committed.Entries.Length);
                Check(ReferenceEquals(store.Snapshot, committed.Snapshot), "Published snapshot differs from acknowledged checkpoint.");
                Equal(0, before.ById.Count); Equal(0, before.Entries.Length);
                var bytes = await ReadBytesAsync(path); Equal((long)bytes.Length, committed.Snapshot.CommittedByteLength);
                Equal(HeaderJson + "\n" + Codec.Serialize(Entry("one")) + "\n" + Codec.Serialize(Entry("two", "one")) + "\n", Encoding.UTF8.GetString(bytes));
            }
            await using var reopened = await SessionLogStore.OpenAsync(path);
            Equal(2, reopened.Snapshot.Entries.Length); Equal("two", reopened.Snapshot.LeafId);
            var next = await reopened.AppendAsync([Entry("three", "two")]); Equal("three", next.Snapshot.LeafId);
        }
        finally { Delete(path); }
    }

    public static async Task Admission()
    {
        var path = TempPath(); var missing = TempPath();
        try
        {
            await File.WriteAllTextAsync(path, HeaderJson + "\n"); var original = SHA256.HashData(await ReadBytesAsync(path));
            await Fails(SessionLogStore.CreateNewAsync(path, Header()), SessionLogStoreFailure.OpenFailed);
            Check(SHA256.HashData(await ReadBytesAsync(path)).AsSpan().SequenceEqual(original), "CreateNew overwrote existing bytes.");
            await Fails(SessionLogStore.CreateNewAsync(missing, Entry("wrong-header")), SessionLogStoreFailure.InvalidEntry);
            Check(!File.Exists(missing), "Header validation created a file.");
            foreach (var source in new[] { HeaderJson + "\n{bad-middle}\n" + Codec.Serialize(Entry("descendant")), HeaderJson.Replace("\"version\":3", "\"version\":4", StringComparison.Ordinal) })
            {
                await File.WriteAllTextAsync(path, source); original = SHA256.HashData(await ReadBytesAsync(path));
                await Fails(SessionLogStore.OpenAsync(path), SessionLogStoreFailure.InvalidLog);
                Check(SHA256.HashData(await ReadBytesAsync(path)).AsSpan().SequenceEqual(original), "Writer admission repaired imported source.");
            }
            await File.WriteAllTextAsync(path, HeaderJson + "\n");
            await using (var store = await SessionLogStore.OpenAsync(path))
            {
                var size = store.Snapshot.CommittedByteLength;
                await Fails(store.AppendAsync([Entry("reserved"), Entry("bad", "missing-parent")]), SessionLogStoreFailure.InvalidEntry);
                await Fails(store.AppendAsync([Entry("duplicate"), Entry("duplicate")]), SessionLogStoreFailure.InvalidEntry);
                await Fails(store.AppendAsync([]), SessionLogStoreFailure.InvalidEntry);
                var pendingMessage = PiSharp.Contracts.PiWireJson.WriteMessage(new PiSharp.Contracts.AssistantMessage("api", "provider", "model", 0,
                    [], PiSharp.Contracts.TokenUsage.Zero, PiSharp.Contracts.StopReason.Pending));
                var baseEntry = Entry("pending").WireBody.ToString().Replace("\"type\":\"custom\"", "\"type\":\"message\"", StringComparison.Ordinal);
                var pending = Codec.Parse(baseEntry[..^1] + ",\"message\":" + pendingMessage + "}");
                await Fails(store.AppendAsync([pending]), SessionLogStoreFailure.InvalidEntry);
                Equal(size, store.Snapshot.CommittedByteLength); Equal(0, store.Snapshot.Entries.Length);
                await store.AppendAsync([Entry("reserved")]);
                await Fails(store.AppendAsync([Entry("reserved")]), SessionLogStoreFailure.InvalidEntry);
            }
            var memory = new MemoryFactory();
            await Fails(SessionLogStore.CreateNewAsync(missing, Header(), new(StorageFactory: memory)), SessionLogStoreFailure.OpenFailed);
            Equal(0, memory.Storage.WriteCalls); Check(memory.Storage.Disposed, "Rejected fake durable storage leaked.");
        }
        finally { Delete(path); Delete(missing); }
    }

    public static async Task NonterminatedFraming()
    {
        var path = TempPath(); var original = Encoding.UTF8.GetBytes(" \t" + HeaderJson + "\r\n" + Codec.Serialize(Entry("old")));
        try
        {
            await File.WriteAllBytesAsync(path, original);
            await using (var store = await SessionLogStore.OpenAsync(path))
            {
                Equal((long)original.Length, store.Snapshot.CommittedByteLength);
                var appended = await store.AppendAsync([Entry("new", "old")]);
                Equal((long)original.Length, appended.ByteOffset);
            }
            var bytes = await ReadBytesAsync(path);
            Check(bytes.AsSpan(0, original.Length).SequenceEqual(original), "Existing framing or bytes were rewritten.");
            Equal("\n" + Codec.Serialize(Entry("new", "old")) + "\n", Encoding.UTF8.GetString(bytes.AsSpan(original.Length)));
            var read = await new SessionLogReader().ReadFileAsync(path); Equal(SessionLogReadStatus.Complete, read.Status); Equal(3, read.ValidatedPrefix.Length);
        }
        finally { Delete(path); }
    }

    public static async Task SerializationAndDisposal()
    {
        var path = TempPath(); var factory = new FaultFactory();
        SessionLogStore? store = null;
        try
        {
            store = await SessionLogStore.CreateNewAsync(path, Header(), new(StorageFactory: factory));
            factory.Storage!.GateWrite = true; factory.Storage.GateCleanup = true;
            var one = store.AppendAsync([Entry("one")]); await factory.Storage.WriteEntered.Task;
            var two = store.AppendAsync([Entry("two", "one")]); Equal(0, store.Snapshot.Entries.Length);
            var closeOne = store.DisposeAsync().AsTask(); var closeTwo = store.DisposeAsync().AsTask();
            Check(ReferenceEquals(closeOne, closeTwo), "Concurrent disposal created distinct cleanup tasks.");
            Check(!one.IsCompleted && !two.IsCompleted && !closeOne.IsCompleted, "Writer/disposal ignored its gate.");
            factory.Storage.WriteRelease.SetResult(); var committed = await one; Equal("one", committed.Snapshot.LeafId);
            await Fails(two, SessionLogStoreFailure.Disposed); await factory.Storage.CleanupEntered.Task;
            Check(!closeOne.IsCompleted && !closeTwo.IsCompleted, "Shared disposal completed before resource cleanup.");
            factory.Storage.CleanupRelease.SetResult(); await Task.WhenAll(closeOne, closeTwo); Equal(1, factory.Storage.DisposeCalls);
            await Fails(store.AppendAsync([Entry("later", "one")]), SessionLogStoreFailure.Disposed);
            store = await SessionLogStore.OpenAsync(path, new(StorageFactory: factory));
            factory.Storage!.Failure = Fault.Cleanup;
            var failedCloseOne = store.DisposeAsync().AsTask(); var failedCloseTwo = store.DisposeAsync().AsTask();
            Check(ReferenceEquals(failedCloseOne, failedCloseTwo), "Failed disposal was not shared.");
            await Fails(failedCloseOne, SessionLogStoreFailure.CleanupFailed); await Fails(failedCloseTwo, SessionLogStoreFailure.CleanupFailed);
            Equal(1, factory.Storage.DisposeCalls);
        }
        finally
        {
            factory.Storage?.WriteRelease.TrySetResult(); factory.Storage?.CleanupRelease.TrySetResult();
            if (store is not null) { try { await store.DisposeAsync(); } catch (Exception) { } } Delete(path);
        }
    }

    public static async Task FaultStages()
    {
        foreach (var fault in new[] { Fault.PartialWrite, Fault.Flush, Fault.Durable, Fault.Checkpoint })
        {
            var path = TempPath(); var factory = new FaultFactory(); SessionLogStore? store = null;
            try
            {
                store = await SessionLogStore.CreateNewAsync(path, Header(), new(StorageFactory: factory));
                var before = store.Snapshot; factory.Storage!.Failure = fault;
                var expected = fault switch { Fault.PartialWrite => SessionLogStoreFailure.WriteFailed, Fault.Flush => SessionLogStoreFailure.FlushFailed,
                    Fault.Durable => SessionLogStoreFailure.DurableFlushFailed, _ => SessionLogStoreFailure.CheckpointFailed };
                var error = await Fails(store.AppendAsync([Entry("uncertain")]), expected);
                Check(error.MayHaveWritten, "Post-write failure claimed no possible effects.");
                Equal(fault == Fault.Checkpoint, error.DurableFlushCompleted);
                Check(store.IsPoisoned && ReferenceEquals(before, store.Snapshot), "Failed append published state or retained a usable lease.");
                await Fails(store.AppendAsync([Entry("forbidden")]), SessionLogStoreFailure.Poisoned);
                factory.Storage.Failure = Fault.None; await store.DisposeAsync();
                var bytes = await ReadBytesAsync(path); Check(bytes.AsSpan(0, HeaderJson.Length + 1).SequenceEqual(Encoding.UTF8.GetBytes(HeaderJson + "\n")), "Fault corrupted prior committed header.");
                var inspected = await new SessionLogReader().ReadFileAsync(path);
                if (fault == Fault.PartialWrite) Equal(SessionLogReadStatus.RecoveryRequired, inspected.Status);
                if (fault == Fault.Checkpoint)
                {
                    Equal(2, inspected.ValidatedPrefix.Length);
                    await using var adopted = await SessionLogStore.OpenAsync(path); Equal("uncertain", adopted.Snapshot.LeafId);
                }
            }
            finally { if (store is not null) { try { await store.DisposeAsync(); } catch (Exception) { } } Delete(path); }
        }
    }

    public static async Task Cancellation()
    {
        var path = TempPath(); var neverCreated = TempPath(); var factory = new FaultFactory(); SessionLogStore? store = null;
        try
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Cancelled(SessionLogStore.CreateNewAsync(neverCreated, Header(), cancellationToken: cancelled.Token), cancelled.Token);
            Check(!File.Exists(neverCreated), "Precreation cancellation created a file.");
            store = await SessionLogStore.CreateNewAsync(path, Header(), new(StorageFactory: factory)); var before = await ReadBytesAsync(path);
            await Cancelled(store.AppendAsync([Entry("prewrite")], cancelled.Token), cancelled.Token);
            Check((await ReadBytesAsync(path)).AsSpan().SequenceEqual(before), "Prewrite cancellation changed bytes.");
            using var late = new CancellationTokenSource(); Task? close = null;
            factory.Storage!.GateWrite = true;
            var append = store.AppendAsync([Entry("committed")], late.Token); await factory.Storage.WriteEntered.Task;
            using var registration = late.Token.Register(() => close = store!.DisposeAsync().AsTask()); late.Cancel();
            factory.Storage.WriteRelease.SetResult(); var result = await append;
            Check(result.DurableCheckpointAcknowledged && !store.IsPoisoned, "Late cancellation erased known durable commit.");
            await close!; Equal("committed", store.Snapshot.LeafId);
        }
        finally
        {
            factory.Storage?.WriteRelease.TrySetResult(); if (store is not null) { try { await store.DisposeAsync(); } catch (Exception) { } }
            Delete(path); Delete(neverCreated);
        }
    }

    public static async Task GrowthLimits()
    {
        var path = TempPath(); var first = Entry("first"); var headerBytes = Encoding.UTF8.GetByteCount(HeaderJson + "\n");
        var entryBytes = Encoding.UTF8.GetByteCount(Codec.Serialize(first) + "\n");
        try
        {
            var options = new SessionLogStoreOptions(new(MaximumInputBytes: headerBytes + entryBytes, MaximumRecords: 2, MaximumLines: 2), MaximumBatchRecords: 1);
            await using (var store = await SessionLogStore.CreateNewAsync(path, Header(), options))
            {
                await Fails(store.AppendAsync([first, Entry("second", "first")]), SessionLogStoreFailure.ResourceLimit);
                var accepted = await store.AppendAsync([first]); Equal(headerBytes + entryBytes, (int)accepted.Snapshot.CommittedByteLength);
                var before = await ReadBytesAsync(path);
                await Fails(store.AppendAsync([Entry("second", "first")]), SessionLogStoreFailure.ResourceLimit);
                Check((await ReadBytesAsync(path)).AsSpan().SequenceEqual(before), "Growth rejection wrote bytes.");
            }
            Delete(path);
            foreach (var limit in new SessionLogReaderOptions[]
            {
                new(MaximumRecords: 1), new(MaximumLines: 1), new(MaximumLineBytes: Encoding.UTF8.GetByteCount(HeaderJson)),
                new(CodecOptions: new(MaximumRecordCharacters: HeaderJson.Length))
            })
            {
                await using (var store = await SessionLogStore.CreateNewAsync(path, Header(), new(limit)))
                {
                    var before = await ReadBytesAsync(path);
                    await Fails(store.AppendAsync([first]), SessionLogStoreFailure.ResourceLimit);
                    Check(!store.IsPoisoned && store.Snapshot.Entries.IsEmpty, "Preflight limit poisoned or changed state.");
                    Check((await ReadBytesAsync(path)).AsSpan().SequenceEqual(before), "Independent limit rejection wrote bytes.");
                }
                Delete(path);
            }
        }
        finally { Delete(path); }
    }

    public static async Task IndependentWriterLease()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Writer lease fixture requires Windows local files.");
        var path = TempPath();
        try
        {
            await using (var store = await SessionLogStore.CreateNewAsync(path, Header()))
            {
                var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing test process path.");
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(SessionLogStoreTests).Assembly.Location);
                start.ArgumentList.Add("--session-store-lease-probe"); start.ArgumentList.Add(path);
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Child writer did not start.");
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(); Equal(0, process.ExitCode); Equal("writer-lease-denied", (await output).Trim()); Equal("", (await error).Trim());
            }
            await using var afterRelease = await SessionLogStore.OpenAsync(path); Equal("session", afterRelease.Snapshot.Header.Id);
        }
        finally { Delete(path); }
    }

    // Root runner hook: handle exactly --session-store-lease-probe <path> before ordinary test argument parsing.
    public static async Task<int> RunLeaseProbeAsync(string path)
    {
        try { await using var unexpected = await SessionLogStore.OpenAsync(path); return 1; }
        catch (SessionLogStoreException error) when (error.Failure == SessionLogStoreFailure.OpenFailed)
        { Console.WriteLine("writer-lease-denied"); return 0; }
    }
    private static string TempPath() => Path.Combine(Path.GetTempPath(), "pisharp-store-" + Guid.NewGuid().ToString("N") + ".jsonl");
    private static async Task<byte[]> ReadBytesAsync(string path)
    {
        // A reader must share the already-open writer's access while itself requesting only read access.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            8_192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var bytes = new MemoryStream(); await stream.CopyToAsync(bytes); return bytes.ToArray();
    }
    private static void Delete(string path) { if (File.Exists(path)) File.Delete(path); }
    private static async Task<SessionLogStoreException> Fails(Task task, SessionLogStoreFailure expected)
    {
        try { await task; }
        catch (SessionLogStoreException error)
        { Equal(expected, error.Failure); Check(error.InnerException is null && !error.Message.Contains("secret-private-payload", StringComparison.Ordinal), "Failure exposed storage details."); return error; }
        throw new InvalidOperationException("Expected storage failure.");
    }
    private static async Task Cancelled<T>(Task<T> task, CancellationToken token)
    { try { await task; } catch (OperationCanceledException error) { Equal(token, error.CancellationToken); return; } throw new InvalidOperationException("Expected cancellation."); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
    private enum Fault { None, PartialWrite, Flush, Durable, Checkpoint, Cleanup }
    private sealed class FaultFactory : ISessionLogStorageFactory
    {
        public FaultStorage? Storage { get; private set; }
        public async ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token)
        { Storage = new(await SessionLogStore.DefaultStorageFactory.OpenAsync(path, createNew, token)); return Storage; }
    }
    private sealed class FaultStorage(ISessionLogStorage inner) : ISessionLogStorage
    {
        public Fault Failure { get; set; }
        public bool GateWrite { get; set; }
        public bool GateCleanup { get; set; }
        public int DisposeCalls { get; private set; }
        public TaskCompletionSource WriteEntered { get; } = Gate(); public TaskCompletionSource WriteRelease { get; } = Gate();
        public TaskCompletionSource CleanupEntered { get; } = Gate(); public TaskCompletionSource CleanupRelease { get; } = Gate();
        public Stream ReadStream => inner.ReadStream; public SessionLogStorageDurability Durability => inner.Durability; public long Length => inner.Length;
        public void PositionForAppend(long length) => inner.PositionForAppend(length);
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes)
        {
            if (GateWrite) { WriteEntered.TrySetResult(); await WriteRelease.Task; }
            if (Failure == Fault.PartialWrite) { await inner.WriteAsync(bytes[..3]); throw new IOException("secret-private-payload"); }
            await inner.WriteAsync(bytes);
        }
        public ValueTask FlushAsync() => Failure == Fault.Flush ? throw new IOException("secret-private-payload") : inner.FlushAsync();
        public void FlushToDisk() { if (Failure == Fault.Durable) throw new IOException("secret-private-payload"); inner.FlushToDisk(); }
        public ValueTask BeforeCheckpointAsync() => Failure == Fault.Checkpoint ? throw new IOException("secret-private-payload") : inner.BeforeCheckpointAsync();
        public async ValueTask DisposeAsync()
        { DisposeCalls++; if (GateCleanup) { CleanupEntered.TrySetResult(); await CleanupRelease.Task; } await inner.DisposeAsync(); if (Failure == Fault.Cleanup) throw new IOException("secret-private-payload"); }
    }
    private sealed class MemoryFactory : ISessionLogStorageFactory
    {
        public MemoryStorage Storage { get; } = new();
        public ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token) => ValueTask.FromResult<ISessionLogStorage>(Storage);
    }
    private sealed class MemoryStorage : ISessionLogStorage
    {
        private readonly MemoryStream _stream = new(); public int WriteCalls { get; private set; } public bool Disposed { get; private set; }
        public Stream ReadStream => _stream; public SessionLogStorageDurability Durability => SessionLogStorageDurability.Unsupported; public long Length => _stream.Length;
        public void PositionForAppend(long length) => _stream.Position = length;
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes) { WriteCalls++; return _stream.WriteAsync(bytes); }
        public ValueTask FlushAsync() => ValueTask.CompletedTask; public void FlushToDisk() { }
        public ValueTask BeforeCheckpointAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { Disposed = true; return _stream.DisposeAsync(); }
    }
}
