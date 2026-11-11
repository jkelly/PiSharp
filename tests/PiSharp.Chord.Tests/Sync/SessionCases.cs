// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/sync/client.ts, packages/chord/src/sync/state.ts, packages/chord/src/sync/remote.ts.
// Authored native regression; not an upstream execution capture.
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Store;
using PiSharp.Chord.Sync;

// Authored native regressions based on Pi v1.5.2 test/sync/sync.test.ts.
internal static partial class Program
{
    private static readonly ChordContext SyncContext = ChordContext.Background;
    private const string SyncSession = "session-1";

    private sealed class SyncMemoryState : ISyncStateStore
    {
        internal readonly Dictionary<string, SyncState> Values = new(StringComparer.Ordinal);
        public Task<SyncState?> GetAsync(string id, ChordContext context) => Task.FromResult(Values.GetValueOrDefault(id));
        public Task SetAsync(string id, SyncState state, ChordContext context)
        {
            Values[id] = state;
            return Task.CompletedTask;
        }
    }

    // Bodies are intentionally collected only by this tiny test remote; production adapters are streamed.
    private class SyncMemoryRemote : ISessionSyncMaintenance
    {
        internal readonly Dictionary<string, Dictionary<long, (BatchInfo Slot, byte[] Bytes)>> Slots = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<long, byte[]>> snapshots = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonObject> meta = new(StringComparer.Ordinal);
        internal bool LoseNextResponse;
        internal long? RequireEpoch;
        internal long? LastEpoch;
        internal bool RejectWithoutReading;
        internal long? MissingAfter;

        public async Task<PutBatchResult> PutBatchAsync(string id, long after, long to, string device,
            IAsyncEnumerable<ReadOnlyMemory<byte>> body, ChordContext context, PutBatchOptions? options = null)
        {
            LastEpoch = options?.Epoch;
            if (RequireEpoch is { } epoch && options?.Epoch != epoch) return new PutBatchResult.StaleEpoch(epoch);
            if (RejectWithoutReading) return new PutBatchResult.Exists(to, "other");
            if (!Slots.TryGetValue(id, out var slots)) Slots[id] = slots = [];
            if (slots.TryGetValue(after, out var found)) return new PutBatchResult.Exists(found.Slot.To, found.Slot.Device);
            var head = SyncRemote.Head(await ListAsync(id, context));
            if (head != after) return new PutBatchResult.NotHead(head);
            using var bytes = new MemoryStream();
            await foreach (var part in body.WithCancellation(context.AbortSignal ?? default)) await bytes.WriteAsync(part);
            slots[after] = (new BatchInfo(after, to, device, Archive: options?.Archive == true), bytes.ToArray());
            if (LoseNextResponse) { LoseNextResponse = false; throw new IOException("response lost"); }
            return new PutBatchResult.Created();
        }

        public Task<IAsyncEnumerable<ReadOnlyMemory<byte>>?> GetBatchAsync(string id, long after, ChordContext context)
        {
            if (MissingAfter == after) return Task.FromResult<IAsyncEnumerable<ReadOnlyMemory<byte>>?>(null);
            return Task.FromResult<IAsyncEnumerable<ReadOnlyMemory<byte>>?>(
                Slots.TryGetValue(id, out var slots) && slots.TryGetValue(after, out var slot) && !slot.Slot.Folded
                    ? Chunks(slot.Bytes) : null);
        }

        public Task<IAsyncEnumerable<ReadOnlyMemory<byte>>?> GetSnapshotAsync(string id, long to, ChordContext context) =>
            Task.FromResult<IAsyncEnumerable<ReadOnlyMemory<byte>>?>(
                snapshots.TryGetValue(id, out var held) && held.TryGetValue(to, out var bytes) ? Chunks(bytes) : null);

        public Task<RemoteListing> ListAsync(string id, ChordContext context) => Task.FromResult(new RemoteListing(
            Slots.TryGetValue(id, out var slots) ? [.. slots.Values.Select(slot => slot.Slot)] : [],
            snapshots.TryGetValue(id, out var held) ? [.. held.Keys] : []));

        public async Task<SessionChanges> SessionsAsync(string? cursor, ChordContext context)
        {
            var result = new List<SessionSummary>();
            foreach (var id in Slots.Keys.Union(meta.Keys).Order(StringComparer.Ordinal))
                result.Add(new(id, SyncRemote.Head(await ListAsync(id, context)), await GetMetaAsync(id, context)));
            return new(result, "", false);
        }

        public Task PutMetaAsync(string id, JsonObject value, ChordContext context)
        {
            meta[id] = (JsonObject)value.DeepClone();
            return Task.CompletedTask;
        }
        public Task<JsonObject?> GetMetaAsync(string id, ChordContext context) =>
            Task.FromResult(meta.TryGetValue(id, out var value) ? (JsonObject?)value.DeepClone() : null);

        public async Task PutSnapshotAsync(string id, long to, IAsyncEnumerable<ReadOnlyMemory<byte>> body, ChordContext context)
        {
            using var bytes = new MemoryStream();
            await foreach (var part in body.WithCancellation(context.AbortSignal ?? default)) await bytes.WriteAsync(part);
            if (!snapshots.TryGetValue(id, out var held)) snapshots[id] = held = [];
            held[to] = bytes.ToArray();
        }
        public Task FoldBatchesAsync(string id, IReadOnlyList<long> afters, ChordContext context)
        {
            foreach (var after in afters)
                if (Slots.TryGetValue(id, out var slots) && slots.TryGetValue(after, out var held))
                    slots[after] = (held.Slot with { Folded = true }, []);
            return Task.CompletedTask;
        }
        public Task DeleteSnapshotsAsync(string id, IReadOnlyList<long> seqs, ChordContext context)
        {
            if (snapshots.TryGetValue(id, out var held)) foreach (var seq in seqs) held.Remove(seq);
            return Task.CompletedTask;
        }
        private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks(byte[] bytes,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            for (var offset = 0; offset < bytes.Length; offset += 4093)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return bytes.AsMemory(offset, Math.Min(4093, bytes.Length - offset));
            }
        }
    }

    private static Task<UploadResult> SyncUpload(IStore source, SyncMemoryRemote remote, SyncMemoryState state,
        string device = "laptop", long? epoch = null) => SessionSync.UploadSessionAsync(new UploadOptions
        { SessionId = SyncSession, Device = device, Source = source, Store = remote, State = state, Context = SyncContext, Epoch = epoch });

    private static Task<PullResult> SyncPull(LocalCopy local, SyncMemoryRemote remote, SyncMemoryState state,
        ChordContext? context = null) => SessionSync.PullSessionAsync(new PullOptions
        { SessionId = SyncSession, Local = local, Store = remote, State = state, Context = context ?? SyncContext });

    private static async Task SyncCommit(ChordStore store, string key, int bytes = 0)
    {
        var value = new JsonObject { ["key"] = key, ["pad"] = new string('x', bytes) };
        await store.CommitAsync([StoreInternal.CreateDocument(new("app.sync", "chord", "notes", key),
            new(1, "", value), ChordJson.Stringify(value))], CommitFence.Open, SyncContext);
    }

    private static async Task<T> SyncFails<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    private static IEnumerable<(string, Func<Task>)> SyncSessionCases() =>
    [
        Case("sync.session.history-selects-snapshot-and-resumes-inside-batch", () =>
        {
            var listing = new RemoteListing([new(0, 5, "a", true), new(5, 10, "a", true), new(10, 15, "b")], [5, 10]);
            Equal(15L, SyncRemote.Head(listing), "remote head includes folded slots");
            var restore = SyncRemote.History(listing);
            Equal(10L, restore.Snapshot, "latest snapshot");
            Sequence(new long[] { 10 }, restore.Batches.Select(batch => batch.After), "after snapshot");
            var resume = SyncRemote.History(listing, 12);
            Equal(null, resume.Snapshot, "resume has no snapshot");
            Sequence(new long[] { 10 }, resume.Batches.Select(batch => batch.After), "resume containing batch");
            Throws<ArgumentException>(() => SyncRemote.Head(new([new(0, 0, "x")], [])), "malformed listing does not loop");
        }),
        Case("sync.session.upload-trims-idles-and-restores-every-write", async () =>
        {
            await using var source = await StoreBatchEveryKind();
            var remote = new SyncMemoryRemote(); var state = new SyncMemoryState();
            Equal(new UploadResult.Uploaded(source.LastSeq), await SyncUpload(source, remote, state), "uploaded");
            Equal(source.LastSeq, source.Outbox!.Floor, "trim acknowledged only");
            Equal(new UploadResult.Idle(), await SyncUpload(source, remote, state), "idle");
            var pulled = await SyncPull(new LocalCopy.Empty(new(new TestSqliteDatabase(":memory:"))), remote, new());
            await using var target = pulled.Store;
            SameParts(await Exported(source), await Exported(target), "all kinds restored");
            Equal(target.LastSeq, target.Outbox!.Floor, "pulled commits trimmed");
            Equal(new UploadResult.Idle(), await SyncUpload(target, remote, new SyncMemoryState
            { Values = { [SyncSession] = new SyncState(target.LastSeq) } }, "viewer"), "no echo upload");
        }),
        Case("sync.session.handoff-and-competing-writers-diverge", async () =>
        {
            await using var laptop = await MemoryStore(true);
            var remote = new SyncMemoryRemote(); var laptopState = new SyncMemoryState(); var phoneState = new SyncMemoryState();
            await SyncCommit(laptop, "start"); await SyncUpload(laptop, remote, laptopState);
            var restored = await SyncPull(new LocalCopy.Empty(new(new TestSqliteDatabase(":memory:"))), remote, phoneState);
            await using var phone = (ChordStore)restored.Store;
            await SyncCommit(phone, "phone"); await SyncUpload(phone, remote, phoneState, "phone");
            await SyncPull(new LocalCopy.Store(laptop), remote, laptopState);
            SameParts(await Exported(laptop), await Exported(phone), "handoff");
            var forkAt = laptop.LastSeq;
            await SyncCommit(laptop, "laptop-own"); await SyncCommit(phone, "phone-own");
            await SyncFails<UnsyncedCommitsError>(() => SyncPull(new LocalCopy.Store(phone), remote, phoneState));
            await SyncUpload(laptop, remote, laptopState);
            Equal(new UploadResult.Diverged(forkAt), await SyncUpload(phone, remote, phoneState, "phone"), "first writer keeps slot");
        }),
        Case("sync.session.lost-response-is-idempotent-and-epoch-is-forwarded", async () =>
        {
            await using var source = await MemoryStore(true);
            await SyncCommit(source, "one");
            var remote = new SyncMemoryRemote { LoseNextResponse = true }; var state = new SyncMemoryState();
            await SyncFails<IOException>(() => SyncUpload(source, remote, state));
            Equal(0, state.Values.Count, "failed response not acknowledged locally");
            Equal(0L, source.Outbox!.Floor, "failed response not trimmed");
            await SyncCommit(source, "two");
            Equal(new UploadResult.Uploaded(source.LastSeq), await SyncUpload(source, remote, state), "retry same device and continue");
            Equal(2, remote.Slots[SyncSession].Count, "one slot per range");
            await SyncCommit(source, "three"); remote.RequireEpoch = 9;
            Equal(new UploadResult.StaleEpoch(2, 9), await SyncUpload(source, remote, state, epoch: 8), "stale epoch");
            Equal(8L, remote.LastEpoch, "sent epoch");
            Equal(new UploadResult.Uploaded(3), await SyncUpload(source, remote, state, epoch: 9), "current epoch");
        }),
        Case("sync.session.lost-local-commits-must-pull-before-upload", async () =>
        {
            await using var source = await MemoryStore(true); await using var behind = await MemoryStore(true);
            var remote = new SyncMemoryRemote(); var state = new SyncMemoryState();
            await SyncCommit(source, "one"); await SyncCommit(behind, "one");
            await SyncCommit(source, "two"); await SyncCommit(source, "three"); await SyncUpload(source, remote, state);
            Equal(new UploadResult.Behind(1, 3), await SyncUpload(behind, remote, state), "behind");
            await SyncPull(new LocalCopy.Store(behind), remote, state);
            SameParts(await Exported(source), await Exported(behind), "lost commits recovered");
        }),
        Case("sync.session.no-outbox-and-trimmed-history-are-refused", async () =>
        {
            await using var plain = await MemoryStore();
            await SyncFails<OutboxUnavailableError>(() => SyncUpload(plain, new(), new()));
            await using var trimmed = await MemoryStore(true); await SyncCommit(trimmed, "one");
            await trimmed.Outbox!.TrimAsync(1, SyncContext);
            await SyncFails<OutboxUnavailableError>(() => SessionSync.ReadOutboxAsync(trimmed, 0, 100, SyncContext));
        }),
        Case("sync.session.archive-bootstrap-and-rejected-unread-export-release", async () =>
        {
            await using var source = await MemoryStore(true); await SyncCommit(source, "old");
            await source.Outbox!.TrimAsync(source.LastSeq, SyncContext); await SyncCommit(source, "new");
            var remote = new SyncMemoryRemote(); var state = new SyncMemoryState();
            await SyncUpload(source, remote, state);
            Check(remote.Slots[SyncSession][0].Slot.Archive, "first slot is archive");
            var restored = await SyncPull(new LocalCopy.Empty(new(new TestSqliteDatabase(":memory:"))), remote, new());
            await using var target = restored.Store;
            SameParts(await Exported(source), await Exported(target), "archive bootstrap");
            var rejected = new SyncMemoryRemote { RejectWithoutReading = true };
            Equal(new UploadResult.Diverged(0), await SyncUpload(source, rejected, new()), "archive refused");
            await SyncCommit(source, "after-reject").WaitAsync(TimeSpan.FromSeconds(3));
        }),
        Case("sync.session.large-history-is-batched-and-partial-pull-resumes", async () =>
        {
            await using var source = await MemoryStore(true);
            for (var index = 0; index < 24; index++) await SyncCommit(source, "large-" + index, 1024 * 1024);
            var remote = new SyncMemoryRemote(); await SyncUpload(source, remote, new());
            Check(remote.Slots[SyncSession].Count >= 4, "8MiB bound creates multiple slots");
            var db = new TestSqliteDatabase(":memory:"); await using var target = await StoreOver(db, true);
            using var cancel = new CancellationTokenSource();
            db.AfterTransaction = () => { cancel.Cancel(); db.AfterTransaction = null; return Task.CompletedTask; };
            var state = new SyncMemoryState();
            await SyncFails<OperationCanceledException>(() => SyncPull(new LocalCopy.Store(target), remote, state,
                ChordContext.WithAbortSignal(cancel.Token, SyncContext)));
            Check(target.LastSeq > 0 && target.LastSeq < source.LastSeq, "committed a bounded chunk before interruption");
            Check(state.Values[SyncSession].Pulling >= target.LastSeq, "pull intent preserves shared prefix");
            Equal(new UploadResult.Idle(), await SyncUpload(target, remote, state, "viewer"), "no echo of partial pull");
            await SyncPull(new LocalCopy.Store(target), remote, state);
            SameParts(await Exported(source), await Exported(target), "resumed without duplication");
        }),
        Case("sync.session.fold-restores-and-preserves-taken-slots", async () =>
        {
            await using var source = await MemoryStore(true);
            var remote = new SyncMemoryRemote(); var state = new SyncMemoryState();
            await SyncCommit(source, "one"); await SyncUpload(source, remote, state);
            var staleState = new SyncMemoryState();
            var copy = await SyncPull(new LocalCopy.Empty(new(new TestSqliteDatabase(":memory:"))), remote, staleState);
            await using var stale = copy.Store;
            await SyncCommit(source, "two"); await SyncUpload(source, remote, state);
            Equal(2L, await SessionSync.FoldSessionAsync(new FoldOptions
            { SessionId = SyncSession, Store = remote, Scratch = new(new TestSqliteDatabase(":memory:")), Context = SyncContext }), "fold head");
            Check(remote.Slots[SyncSession].Values.All(entry => entry.Slot.Folded), "slots tombstoned");
            await SyncFails<HistoryUnavailableError>(() => SyncPull(new LocalCopy.Store(stale), remote, staleState));
            var restored = await SyncPull(new LocalCopy.Empty(new(new TestSqliteDatabase(":memory:"))), remote, new());
            await using var target = restored.Store;
            SameParts(await Exported(source), await Exported(target), "snapshot restored");
            remote.MissingAfter = 2;
            await SyncCommit(source, "three"); await SyncUpload(source, remote, state);
            await SyncFails<HistoryUnavailableError>(() => SyncPull(new LocalCopy.Store(target), remote,
                new SyncMemoryState { Values = { [SyncSession] = new SyncState(2) } }));
        }),
        Case("sync.session.filesystem-remote-state-and-snapshot-roundtrip", async () =>
        {
            var directory = Directory.CreateTempSubdirectory("chord-sync-session-");
            try
            {
                var fs = new LocalSyncFileSystem();
                var remote = new FileSystemSyncRemote(fs, Path.Combine(directory.FullName, "remote"));
                var writerState = new FileSyncStateStore(fs, Path.Combine(directory.FullName, "writer"));
                var readerState = new FileSyncStateStore(fs, Path.Combine(directory.FullName, "reader"));
                await using var source = await MemoryStore(true);
                async Task<UploadResult> Upload() => await SessionSync.UploadSessionAsync(new()
                {
                    SessionId = SyncSession, Device = "writer", Source = source, Store = remote, State = writerState, Context = SyncContext,
                });
                await SyncCommit(source, "first");
                Equal(new UploadResult.Uploaded(1), await Upload(), "first filesystem upload");
                Equal(new SyncState(1), await writerState.GetAsync(SyncSession, SyncContext), "persisted upload state");
                var pull = await SessionSync.PullSessionAsync(new()
                {
                    SessionId = SyncSession, Store = remote, State = readerState, Context = SyncContext,
                    Local = new LocalCopy.Empty(new(new TestSqliteDatabase(":memory:"))),
                });
                await using var target = pull.Store;
                SameParts(await Exported(source), await Exported(target), "filesystem replay");
                await SyncCommit(source, "second"); await Upload();
                await SessionSync.PullSessionAsync(new()
                {
                    SessionId = SyncSession, Store = remote, State = readerState, Context = SyncContext, Local = new LocalCopy.Store(target),
                });
                Equal(new SyncState(2), await readerState.GetAsync(SyncSession, SyncContext), "persisted pull state");
                Equal(2L, await SessionSync.FoldSessionAsync(new()
                {
                    SessionId = SyncSession, Store = remote, Scratch = new(new TestSqliteDatabase(":memory:")), Context = SyncContext,
                }), "filesystem snapshot head");
                var snapshot = await SessionSync.PullSessionAsync(new()
                {
                    SessionId = SyncSession, Store = remote,
                    State = new FileSyncStateStore(fs, Path.Combine(directory.FullName, "fresh")), Context = SyncContext,
                    Local = new LocalCopy.Empty(new(new TestSqliteDatabase(":memory:"))),
                });
                await using var restored = snapshot.Store;
                SameParts(await Exported(source), await Exported(restored), "filesystem snapshot restore");
            }
            finally { directory.Delete(true); }
        }),
    ];
}
