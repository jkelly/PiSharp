// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/sync/client.ts, packages/chord/src/sync/remote.ts,
// packages/chord/src/sync/filesystem.ts.
// Authored native regressions from the Pi 1.6.0 additions to packages/chord/test/sync/sync.test.ts; not an upstream execution capture.
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Store;
using PiSharp.Chord.Sync;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SyncSession160Cases()
    {
        var context = ChordContext.Background;
        static LocalCopy Empty() => new LocalCopy.Empty(new(new TestSqliteDatabase(":memory:")));
        Task<PullResult> Pull(ISessionSyncStore remote, LocalCopy local, ISyncStateStore state) => SessionSync.PullSessionAsync(new()
        { SessionId = SyncSession, Store = remote, Local = local, State = state, Context = context });
        Task<UploadResult> Upload(ISessionSyncUploader remote, IStore source, ISyncStateStore state) => SessionSync.UploadSessionAsync(new()
        { SessionId = SyncSession, Device = "laptop", Source = source, Store = remote, State = state, Context = context });
        return
        [
            // sync.test.ts "restores from the newest archive slot along the chain, past an older snapshot; a copy gets the chain as it is".
            Case("sync.session.160.history-restores-from-the-newest-archive-slot", () =>
            {
                var archived = new BatchInfo(10, 12, "b", Archive: true);
                var listing = new RemoteListing([new(0, 4, "a", Folded: true), new(4, 10, "a"), archived, new(12, 15, "b")], [4]);
                var restore = SyncRemote.History(listing);
                Equal<long?>(null, restore.Snapshot, "a restore past the snapshot starts at the archive slot");
                Sequence(new[] { archived, new BatchInfo(12, 15, "b") }, restore.Batches, "the archive slot and what follows it");
                Equal(15L, restore.Head, "the head");
                var copy = SyncRemote.History(listing, 4);
                Equal<long?>(null, copy.Snapshot, "a copy takes no snapshot");
                Sequence(new[] { new BatchInfo(4, 10, "a"), archived, new BatchInfo(12, 15, "b") }, copy.Batches, "a copy gets the chain as it is");
                Equal(15L, copy.Head, "the copy's head");
                // Without an archive slot the newest snapshot still starts a restore.
                var plain = SyncRemote.History(new([new(0, 4, "a", Folded: true), new(4, 10, "a"), new(10, 12, "b")], [4]));
                Equal<long?>(4, plain.Snapshot, "the newest snapshot"); Sequence(new long[] { 4, 10 }, plain.Batches.Select(batch => batch.After), "after the snapshot");
            }),
            // sync.test.ts "goes on from a full state after the outbox was turned off and on again". At 1.5.2 every upload of such a
            // store failed with OutboxUnavailableError (the outbox starts past the synced head).
            Case("sync.session.160.goes-on-from-a-full-state-after-the-outbox-was-off-and-on", async () =>
            {
                var directory = Directory.CreateTempSubdirectory("chord-sync-gap-");
                try
                {
                    var file = Path.Combine(directory.FullName, "session.sqlite");
                    var remote = new FileSystemSyncRemote(new LocalSyncFileSystem(), Path.Combine(directory.FullName, "remote"));
                    var state = new SyncMemoryState();
                    long synced;
                    await using (var first = await StoreOver(new TestSqliteDatabase(file), true))
                    {
                        for (var index = 0; index < 4; index++) await SyncCommit(first, "first-" + index);
                        synced = first.LastSeq;
                        Equal(new UploadResult.Uploaded(synced), await Upload(remote, first, state), "the first upload");
                    }
                    // A replica in step with the remote now.
                    var replicaState = new SyncMemoryState();
                    await using var replica = (await Pull(remote, Empty(), replicaState)).Store;
                    // Off: the outbox's commits are dropped, and the store commits on without one.
                    await using (var off = await StoreOver(new TestSqliteDatabase(file), false))
                        for (var index = 0; index < 3; index++) await SyncCommit(off, "off-" + index);
                    // On again: the outbox starts at the store's head, past what the remote holds.
                    await using var source = await StoreOver(new TestSqliteDatabase(file), true);
                    Check(source.Outbox!.Floor > synced, "the outbox turned on again starts past the synced head");
                    Equal(new UploadResult.Uploaded(source.LastSeq), await Upload(remote, source, state), "the upload after the gap");
                    Equal(new BatchInfo(synced, source.LastSeq, "laptop", Archive: true), (await remote.ListAsync(SyncSession, context)).Batches[^1],
                        "the slot after the remote's head is an archive slot");
                    // Commits after it are batches again.
                    var archived = source.LastSeq;
                    for (var index = 0; index < 3; index++) await SyncCommit(source, "on-" + index);
                    Equal(new UploadResult.Uploaded(source.LastSeq), await Upload(remote, source, state), "the upload after the archive slot");
                    Equal(new BatchInfo(archived, source.LastSeq, "laptop"), (await remote.ListAsync(SyncSession, context)).Batches[^1], "a batch slot again");
                    // A restore starts from the archive slot; a copy behind it cannot apply it and restores fresh.
                    await using (var fresh = (await Pull(remote, Empty(), new SyncMemoryState())).Store)
                        SameParts(await Exported(source), await Exported(fresh), "a fresh restore from the archive slot");
                    await SyncFails<HistoryUnavailableError>(() => Pull(remote, new LocalCopy.Store(replica), replicaState));
                    // Folding covers every slot, also those before the archive slot, and restores the same.
                    Equal<long?>(source.LastSeq, await SessionSync.FoldSessionAsync(new()
                    { SessionId = SyncSession, Store = remote, Scratch = new(new TestSqliteDatabase(":memory:")), Context = context }), "the fold's head");
                    Check((await remote.ListAsync(SyncSession, context)).Batches.All(batch => batch.Folded), "a slot before the archive slot was not folded");
                    await using var folded = (await Pull(remote, Empty(), new SyncMemoryState())).Store;
                    SameParts(await Exported(source), await Exported(folded), "a restore from the folded history");
                }
                finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); directory.Delete(true); }
            }),
            // sync.test.ts "finds its own slots when its sync state is lost, and goes on from a full state after them".
            Case("sync.session.160.finds-its-own-slots-when-its-sync-state-is-lost", async () =>
            {
                var directory = Directory.CreateTempSubdirectory("chord-sync-lost-");
                try
                {
                    var file = Path.Combine(directory.FullName, "session.sqlite");
                    var remote = new SyncMemoryRemote();
                    await using (var first = await StoreOver(new TestSqliteDatabase(file), true))
                    {
                        for (var index = 0; index < 4; index++) await SyncCommit(first, "first-" + index);
                        await Upload(remote, first, new SyncMemoryState());
                    }
                    await using (var off = await StoreOver(new TestSqliteDatabase(file), false))
                        for (var index = 0; index < 6; index++) await SyncCommit(off, "off-" + index);
                    await using var source = await StoreOver(new TestSqliteDatabase(file), true);
                    // No sync state: the archive at 0 finds this device's slot, and the next archive goes after it.
                    Equal(new UploadResult.Uploaded(source.LastSeq), await Upload(remote, source, new SyncMemoryState()), "the upload without sync state");
                    Sequence(new[] { false, true }, remote.Slots[SyncSession].Values.OrderBy(entry => entry.Slot.After).Select(entry => entry.Slot.Archive),
                        "its first slot stays; an archive slot follows it");
                    await using var fresh = (await Pull(remote, Empty(), new SyncMemoryState())).Store;
                    SameParts(await Exported(source), await Exported(fresh), "a fresh restore");
                }
                finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); directory.Delete(true); }
            }),
            // sync.test.ts "closes a body's file at its end, and on return() also before the first read". The native directory remote
            // opens a body's file at its first read (upstream opens it in getBatch and closes it on return()): a body that is never
            // read holds no file, and the enumerator's DisposeAsync is upstream's return().
            Case("sync.files.160.body-closes-its-file-at-its-end-and-when-it-is-returned", () => SyncFileDirectory(async (path, fs) =>
            {
                var remote = new FileSystemSyncRemote(fs, path);
                await remote.PutBatchAsync("s", 0, 3, "a", SyncFileBody("abc"), context);
                Equal("abc", await SyncFileText((await remote.GetBatchAsync("s", 0, context))!), "the body is read to its end");
                Equal(0, fs.OpenReaders, "a body read to its end closed its file");
                var unread = (await remote.GetBatchAsync("s", 0, context))!;
                Equal(0, fs.OpenReaders, "a body that was not read holds no file");
                var returned = unread.GetAsyncEnumerator();
                await returned.DisposeAsync(); await returned.DisposeAsync();
                Equal(0, fs.OpenReaders, "a body returned before its first read holds no file");
                Check(!await returned.MoveNextAsync(), "a returned body reads nothing more");
                Equal(0, fs.OpenReaders, "a returned body opened its file");
                var started = (await remote.GetBatchAsync("s", 0, context))!.GetAsyncEnumerator();
                Check(await started.MoveNextAsync(), "the body has a first chunk");
                Equal(1, fs.OpenReaders, "a body being read holds its file");
                await started.DisposeAsync();
                Equal(0, fs.OpenReaders, "a body returned while it was read closed its file");
            })),
            // sync.test.ts "closes a body a pull read and then refused", over the directory remote.
            Case("sync.session.160.pull-leaves-no-file-open-for-a-body-it-refused", () => SyncFileDirectory(async (path, fs) =>
            {
                var remote = new FileSystemSyncRemote(fs, path);
                await using var laptop = await MemoryStore(true);
                await SyncCommit(laptop, "one"); await SyncCommit(laptop, "two");
                await Upload(remote, laptop, new SyncMemoryState());
                var replicaState = new SyncMemoryState();
                await using var replica = (await Pull(remote, Empty(), replicaState)).Store;
                // An archive slot after the replica's head: a full state, which does not apply on top of its store.
                var head = SyncRemote.Head(await remote.ListAsync(SyncSession, context));
                await remote.PutBatchAsync(SyncSession, head, head + 1, "laptop", SyncCodec.EncodeBatch(Sync160NoCommits()), context, new(Archive: true));
                await SyncFails<HistoryUnavailableError>(() => Pull(remote, new LocalCopy.Store(replica), replicaState));
                Equal(0, fs.OpenReaders, "a refused body left its file open");
            })),
            // The same, for a remote whose bodies hold something from the moment they are returned (a connection): PullSessionAsync
            // disposes the enumerator of every body it read, also of one it refused before its first read. At 1.5.2 a refused body's
            // enumerator was never made, so nothing released it.
            Case("sync.session.160.pull-disposes-every-body-it-read-also-a-refused-one", async () =>
            {
                var remote = new Sync160HeldRemote();
                await using var laptop = await MemoryStore(true);
                await SyncCommit(laptop, "one"); await SyncCommit(laptop, "two");
                await Upload(remote, laptop, new SyncMemoryState());
                var replicaState = new SyncMemoryState();
                await using var replica = (await Pull(remote, Empty(), replicaState)).Store;
                Check(remote.Bodies.Count > 0 && remote.Bodies.All(body => body.Released == 1 && body.Reads > 0), "a body a pull applied was not released once");
                var head = SyncRemote.Head(await remote.ListAsync(SyncSession, context));
                await remote.PutBatchAsync(SyncSession, head, head + 1, "laptop", SyncCodec.EncodeBatch(Sync160NoCommits()), context, new(Archive: true));
                var before = remote.Bodies.Count;
                await SyncFails<HistoryUnavailableError>(() => Pull(remote, new LocalCopy.Store(replica), replicaState));
                var refused = remote.Bodies.Skip(before).ToArray();
                Equal(1, refused.Length, "the pull read the archive slot's body");
                Equal(0, refused[0].Reads, "a refused body was read"); Equal(1, refused[0].Released, "a refused body was not released");
                Check(remote.Bodies.All(body => body.Released == 1), "a body was left held or released twice");
            }),
        ];
    }

    private static async IAsyncEnumerable<OutboxCommit> Sync160NoCommits()
    {
        await Task.CompletedTask;
        yield break;
    }

    /// <summary>A memory remote whose batch bodies count as held from <c>GetBatchAsync</c> until their enumerator is disposed.</summary>
    private sealed class Sync160HeldRemote : SyncMemoryRemote, ISessionSyncMaintenance
    {
        internal readonly List<HeldBody> Bodies = [];

        async Task<IAsyncEnumerable<ReadOnlyMemory<byte>>?> ISessionSyncStore.GetBatchAsync(string id, long after, ChordContext context)
        {
            var body = await GetBatchAsync(id, after, context);
            if (body is null) return null;
            var held = new HeldBody(body);
            Bodies.Add(held);
            return held;
        }

        internal sealed class HeldBody(IAsyncEnumerable<ReadOnlyMemory<byte>> inner) : IAsyncEnumerable<ReadOnlyMemory<byte>>
        {
            internal int Reads, Released;
            public IAsyncEnumerator<ReadOnlyMemory<byte>> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
                new Enumerator(this, inner.GetAsyncEnumerator(cancellationToken));
            private sealed class Enumerator(HeldBody owner, IAsyncEnumerator<ReadOnlyMemory<byte>> inner) : IAsyncEnumerator<ReadOnlyMemory<byte>>
            {
                public ReadOnlyMemory<byte> Current => inner.Current;
                public ValueTask<bool> MoveNextAsync() { owner.Reads++; return inner.MoveNextAsync(); }
                public async ValueTask DisposeAsync() { owner.Released++; await inner.DisposeAsync(); }
            }
        }
    }
}
