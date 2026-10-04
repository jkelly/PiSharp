using System.Collections.Immutable;
using System.Text;
using PiSharp.Sessions.Serialization;

namespace PiSharp.Sessions.Storage;

public sealed record SessionLogStoreOptions(SessionLogReaderOptions? ReaderOptions = null,
    int MaximumBatchRecords = 256, ISessionLogStorageFactory? StorageFactory = null);
public enum SessionLogStorageDurability { Unsupported, LocalFileFlush, VolatileMemory, DeferredLocalFile }
public enum SessionLogStoreFailure
{
    InvalidEntry, InvalidLog, ResourceLimit, OpenFailed, WriteFailed, FlushFailed, DurableFlushFailed,
    CheckpointFailed, CleanupFailed, Poisoned, Disposed
}
public sealed class SessionLogStoreException : Exception
{
    public SessionLogStoreFailure Failure { get; }
    public bool MayHaveWritten { get; }
    public bool DurableFlushCompleted { get; }
    internal SessionLogStoreException(SessionLogStoreFailure failure, bool mayHaveWritten = false,
        bool durableFlushCompleted = false) : base(failure switch
    {
        SessionLogStoreFailure.InvalidEntry => "Session append entries are invalid.",
        SessionLogStoreFailure.InvalidLog => "Session log cannot admit a writer.",
        SessionLogStoreFailure.ResourceLimit => "Session append exceeds configured limits.",
        SessionLogStoreFailure.Poisoned => "Session writer requires close and explicit inspection.",
        SessionLogStoreFailure.Disposed => "Session writer is closing or disposed.",
        _ => "Session storage operation failed."
    }) { Failure = failure; MayHaveWritten = mayHaveWritten; DurableFlushCompleted = durableFlushCompleted; }
}

/// <summary>Trusted host I/O seam. LocalFileFlush requires actual file flush-to-disk operations.
/// VolatileMemory and DeferredLocalFile acknowledge an owned memory checkpoint, never disk durability.</summary>
public interface ISessionLogStorage : IAsyncDisposable
{
    Stream ReadStream { get; }
    SessionLogStorageDurability Durability { get; }
    long Length { get; }
    void PositionForAppend(long expectedLength);
    ValueTask WriteAsync(ReadOnlyMemory<byte> bytes);
    ValueTask FlushAsync();
    void FlushToDisk();
    ValueTask BeforeCheckpointAsync();
}
public interface ISessionLogStorageFactory
{
    ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken cancellationToken);
}
public sealed record SessionLogStoreSnapshot(SessionEntry Header, ImmutableArray<SessionEntry> Entries,
    ImmutableDictionary<string, SessionEntry> ById, string? LeafId, long CommittedByteLength, long Sequence)
{
    public SessionLogStorageDurability StorageDurability { get; init; } = SessionLogStorageDurability.LocalFileFlush;
    public bool IsMaterialized => StorageDurability == SessionLogStorageDurability.LocalFileFlush;
}
public sealed record SessionLogAppendResult(bool Accepted, bool Flushed, bool DurableCheckpointAcknowledged,
    long Sequence, long ByteOffset, long ByteLength, ImmutableArray<SessionEntry> Entries, SessionLogStoreSnapshot Snapshot)
{
    public bool CheckpointAcknowledged => Accepted && Flushed && (DurableCheckpointAcknowledged ||
        Snapshot.StorageDurability is SessionLogStorageDurability.VolatileMemory or SessionLogStorageDurability.DeferredLocalFile);
}

/// <summary>One serialized writer lease. State is published only after the selected backend's checkpoint;
/// receipts explicitly distinguish volatile checkpoints from file-content durability.</summary>
public sealed class SessionLogStore : IAsyncDisposable
{
    private readonly SessionLogStoreOptions _options;
    private readonly SessionLogReaderOptions _readerOptions;
    private readonly SessionEntryCodec _codec;
    private readonly ISessionLogStorage _storage;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly object _lifecycle = new();
    private SessionLogStoreSnapshot _snapshot;
    private Task? _disposeTask;
    private volatile bool _closing;
    private volatile bool _poisoned;
    private bool _needsNewline;
    private int _physicalLines;
    public SessionLogStoreSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public bool IsPoisoned => _poisoned;
    public static ISessionLogStorageFactory DefaultStorageFactory { get; } = new FileStorageFactory();

    private SessionLogStore(ISessionLogStorage storage, SessionLogStoreOptions options, SessionLogStoreSnapshot snapshot,
        bool needsNewline, int physicalLines)
    {
        _storage = storage; _options = options; _readerOptions = options.ReaderOptions ?? new();
        _codec = new(_readerOptions.CodecOptions); _snapshot = snapshot; _needsNewline = needsNewline; _physicalLines = physicalLines;
    }

    public static async Task<SessionLogStore> CreateNewAsync(string path, SessionEntry validatedHeader,
        SessionLogStoreOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path); ArgumentNullException.ThrowIfNull(validatedHeader);
        var configured = ValidateOptions(options);
        var bounds = configured.ReaderOptions ?? new(); var codec = new SessionEntryCodec(bounds.CodecOptions);
        SessionEntry header;
        try { header = codec.Read(validatedHeader.WireBody.Value); }
        catch (SessionEntryCodecException error) { throw EntryError(error); }
        if (!header.IsHeader || header.Id.Length == 0) throw Error(SessionLogStoreFailure.InvalidEntry);
        var bytes = Encode(codec, header, bounds);
        if (bytes.Length > bounds.MaximumInputBytes) throw Error(SessionLogStoreFailure.ResourceLimit);
        cancellationToken.ThrowIfCancellationRequested();
        ISessionLogStorage storage;
        // Creation is already an effect: shield caller cancellation through the initial header checkpoint.
        try { storage = await (configured.StorageFactory ?? DefaultStorageFactory).OpenAsync(path, true, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { throw Error(SessionLogStoreFailure.OpenFailed); }
        var store = new SessionLogStore(storage, configured, new(header, [], ImmutableDictionary<string, SessionEntry>.Empty,
            null, 0, 0), false, 0);
        try
        {
            ValidateStorage(storage);
            await store.CommitBytesAsync(bytes, store._snapshot with { CommittedByteLength = bytes.Length }, 1).ConfigureAwait(false);
            return store;
        }
        catch
        {
            try { await store.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
            throw;
        }
    }

    public static async Task<SessionLogStore> OpenAsync(string path, SessionLogStoreOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var configured = ValidateOptions(options); var bounds = configured.ReaderOptions ?? new();
        cancellationToken.ThrowIfCancellationRequested();
        ISessionLogStorage storage;
        try { storage = await (configured.StorageFactory ?? DefaultStorageFactory).OpenAsync(path, false, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (Exception) { throw Error(SessionLogStoreFailure.OpenFailed); }
        try
        {
            ValidateStorage(storage); storage.ReadStream.Position = 0;
            var log = await new SessionLogReader(bounds).ReadAsync(storage.ReadStream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!log.SourceComplete || log.Status != SessionLogReadStatus.Complete || log.Header is null) throw Error(SessionLogStoreFailure.InvalidLog);
            var index = ImmutableDictionary.CreateBuilder<string, SessionEntry>(StringComparer.Ordinal);
            var entries = ImmutableArray.CreateBuilder<SessionEntry>();
            foreach (var record in log.ValidatedPrefix.Skip(1))
            {
                try { ValidateIdentity(record.Entry, index); }
                catch (SessionLogStoreException) { throw Error(SessionLogStoreFailure.InvalidLog); }
                index.Add(record.Entry.Id, record.Entry); entries.Add(record.Entry);
            }
            var owned = entries.ToImmutable();
            var snapshot = new SessionLogStoreSnapshot(log.Header, owned, index.ToImmutable(), owned.IsEmpty ? null : owned[^1].Id,
                log.OriginalBytes.Length, 0) { StorageDurability = storage.Durability };
            var lines = 0; foreach (var value in log.OriginalBytes) if (value == '\n') lines++;
            var needsNewline = !log.OriginalBytes.IsEmpty && log.OriginalBytes[^1] != '\n'; if (needsNewline) lines++;
            cancellationToken.ThrowIfCancellationRequested();
            return new(storage, configured, snapshot, needsNewline, lines);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { try { await storage.DisposeAsync().ConfigureAwait(false); } catch (Exception) { } throw new OperationCanceledException(cancellationToken); }
        catch (Exception error)
        {
            try { await storage.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
            if (error is SessionLogStoreException) throw;
            throw Error(SessionLogStoreFailure.OpenFailed);
        }
    }

    public async Task<SessionLogAppendResult> AppendAsync(ImmutableArray<SessionEntry> entries,
        CancellationToken cancellationToken = default)
    {
        if (entries.IsDefaultOrEmpty) throw Error(SessionLogStoreFailure.InvalidEntry);
        if (entries.Length > _options.MaximumBatchRecords) throw Error(SessionLogStoreFailure.ResourceLimit);
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_closing) throw Error(SessionLogStoreFailure.Disposed);
            if (_poisoned) throw Error(SessionLogStoreFailure.Poisoned);
            cancellationToken.ThrowIfCancellationRequested();
            var previous = Snapshot; var index = previous.ById.ToBuilder();
            var canonical = ImmutableArray.CreateBuilder<SessionEntry>(entries.Length);
            using var encoded = new MemoryStream();
            if (_needsNewline) encoded.WriteByte((byte)'\n');
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry is null || entry.IsHeader) throw Error(SessionLogStoreFailure.InvalidEntry);
                SessionEntry owned;
                try { owned = _codec.Read(entry.WireBody.Value); }
                catch (SessionEntryCodecException error) { throw EntryError(error); }
                ValidateIdentity(owned, index); index.Add(owned.Id, owned); canonical.Add(owned);
                var bytes = Encode(_codec, owned, _readerOptions);
                if (encoded.Length + bytes.Length > _readerOptions.MaximumInputBytes - previous.CommittedByteLength)
                    throw Error(SessionLogStoreFailure.ResourceLimit);
                encoded.Write(bytes);
            }
            if (previous.Entries.Length + entries.Length + 1 > _readerOptions.MaximumRecords ||
                (long)_physicalLines + entries.Length > _readerOptions.MaximumLines)
                throw Error(SessionLogStoreFailure.ResourceLimit);
            var accepted = canonical.MoveToImmutable(); var bytesToWrite = encoded.ToArray();
            var next = new SessionLogStoreSnapshot(previous.Header, previous.Entries.AddRange(accepted), index.ToImmutable(),
                accepted[^1].Id, previous.CommittedByteLength + bytesToWrite.Length, previous.Sequence + 1);
            cancellationToken.ThrowIfCancellationRequested();
            await CommitBytesAsync(bytesToWrite, next, _physicalLines + accepted.Length).ConfigureAwait(false);
            next = Snapshot;
            return new(true, true, next.IsMaterialized, next.Sequence, previous.CommittedByteLength, bytesToWrite.Length, accepted, next);
        }
        finally { _writer.Release(); }
    }

    private async Task CommitBytesAsync(byte[] bytes, SessionLogStoreSnapshot next, int physicalLines)
    {
        var stage = SessionLogStoreFailure.WriteFailed; var attempted = false; var durable = false;
        try
        {
            _storage.PositionForAppend(Snapshot.CommittedByteLength);
            attempted = true; await _storage.WriteAsync(bytes).ConfigureAwait(false);
            stage = SessionLogStoreFailure.FlushFailed; await _storage.FlushAsync().ConfigureAwait(false);
            stage = SessionLogStoreFailure.DurableFlushFailed; _storage.FlushToDisk();
            durable = _storage.Durability == SessionLogStorageDurability.LocalFileFlush;
            stage = SessionLogStoreFailure.CheckpointFailed; await _storage.BeforeCheckpointAsync().ConfigureAwait(false);
            _needsNewline = false; _physicalLines = physicalLines;
            Volatile.Write(ref _snapshot, next with { StorageDurability = _storage.Durability });
        }
        catch (Exception)
        { _poisoned = true; throw new SessionLogStoreException(stage, attempted, durable); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycle)
        {
            _closing = true;
            _disposeTask ??= DisposeCoreAsync();
            return new(_disposeTask);
        }
    }
    private async Task DisposeCoreAsync()
    {
        // Yield ensures trusted disposal I/O never executes while the lifecycle lock is held.
        await Task.Yield(); await _writer.WaitAsync().ConfigureAwait(false);
        try { await _storage.DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { throw Error(SessionLogStoreFailure.CleanupFailed); }
        finally { _writer.Release(); }
    }

    private static SessionLogStoreOptions ValidateOptions(SessionLogStoreOptions? options)
    {
        var configured = options ?? new();
        if (configured.MaximumBatchRecords <= 0) throw new ArgumentOutOfRangeException(nameof(options), "Invalid session append limit.");
        _ = new SessionLogReader(configured.ReaderOptions); return configured;
    }
    private static void ValidateStorage(ISessionLogStorage storage)
    {
        if (storage is null || storage.Durability is not (SessionLogStorageDurability.LocalFileFlush or
                SessionLogStorageDurability.VolatileMemory or SessionLogStorageDurability.DeferredLocalFile) ||
            !storage.ReadStream.CanRead || !storage.ReadStream.CanSeek) throw Error(SessionLogStoreFailure.OpenFailed);
    }
    private static void ValidateIdentity(SessionEntry entry, ImmutableDictionary<string, SessionEntry>.Builder index)
    {
        if (entry.IsHeader || entry.Id.Length == 0 || index.ContainsKey(entry.Id) ||
            (entry.ParentId is not null && !index.ContainsKey(entry.ParentId))) throw Error(SessionLogStoreFailure.InvalidEntry);
        if (entry.Kind == SessionEntryKind.Message && entry.WireBody.Value.GetProperty("message").TryGetProperty("role", out var role) &&
            role.GetString() == "assistant" && entry.WireBody.Value.GetProperty("message").GetProperty("stopReason").GetString() == "pending")
            throw Error(SessionLogStoreFailure.InvalidEntry);
    }
    private static byte[] Encode(SessionEntryCodec codec, SessionEntry entry, SessionLogReaderOptions bounds)
    {
        var text = codec.Serialize(entry); var count = Encoding.UTF8.GetByteCount(text);
        if (count > bounds.MaximumLineBytes || count >= bounds.MaximumInputBytes) throw Error(SessionLogStoreFailure.ResourceLimit);
        var bytes = Encoding.UTF8.GetBytes(text + "\n");
        try { _ = codec.ParseUtf8(bytes.AsSpan(0, bytes.Length - 1)); }
        catch (SessionEntryCodecException) { throw Error(SessionLogStoreFailure.ResourceLimit); }
        return bytes;
    }
    private static SessionLogStoreException Error(SessionLogStoreFailure failure) => new(failure);
    private static SessionLogStoreException EntryError(SessionEntryCodecException error) => Error(error.Failure is
        SessionEntryCodecFailure.CharacterLimit or SessionEntryCodecFailure.Utf8ByteLimit or SessionEntryCodecFailure.DepthLimit
        ? SessionLogStoreFailure.ResourceLimit : SessionLogStoreFailure.InvalidEntry);

    private sealed class FileStorageFactory : ISessionLogStorageFactory
    {
        public ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ISessionLogStorage>(new FileStorage(new FileStream(path,
                createNew ? FileMode.CreateNew : FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 8_192,
                FileOptions.Asynchronous | FileOptions.SequentialScan)));
        }
    }
    private sealed class FileStorage(FileStream stream) : ISessionLogStorage
    {
        public Stream ReadStream => stream;
        public SessionLogStorageDurability Durability => SessionLogStorageDurability.LocalFileFlush;
        public long Length => stream.Length;
        public void PositionForAppend(long expectedLength)
        { if (stream.Length != expectedLength) throw new IOException(); stream.Position = expectedLength; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes) => stream.WriteAsync(bytes, CancellationToken.None);
        public ValueTask FlushAsync() => new(stream.FlushAsync(CancellationToken.None));
        public void FlushToDisk() => stream.Flush(flushToDisk: true);
        public ValueTask BeforeCheckpointAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
