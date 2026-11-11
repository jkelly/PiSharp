// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/sync/files.ts, packages/chord/src/sync/filesystem.ts.
// Authored native regression; not an upstream execution capture.
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Sync;

// Pi v1.5.2 sync/filesystem.ts and state.ts contracts, with native fault-injection coverage.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SyncFileCases()
    {
        var context = ChordContext.Background;
        return
        [
            Case("sync.files.session-identifiers", () =>
            {
                foreach (var id in new[] { "a", "_abc", "-abc", "A.2-b_c" }) Equal(id, SyncPaths.CheckSessionId(id), "safe id");
                foreach (var id in new[] { "", ".", "..", ".hidden", "../escape", "x/y", "x\\y", "x:y", "a\n", "é" })
                    Throws<ArgumentException>(() => SyncPaths.CheckSessionId(id), "unsafe id");
            }),
            Case("sync.files.missing-json-is-distinct-from-empty-json", () => SyncFileDirectory(async (path, fs) =>
            {
                var remote = new FileSystemSyncRemote(fs, path);
                Equal<JsonObject?>(null, await remote.GetMetaAsync("s", context), "missing metadata");
                Equal<IAsyncEnumerable<ReadOnlyMemory<byte>>?>(null, await remote.GetBatchAsync("s", 0, context), "missing slot");
                Directory.CreateDirectory(Path.Combine(path, "s", "slots"));
                await File.WriteAllTextAsync(Path.Combine(path, "s", "meta.json"), "");
                await File.WriteAllTextAsync(Path.Combine(path, "s", "slots", "0000000000000000.json"), "");
                Check(await ChordSupport.Rejection(remote.GetMetaAsync("s", context)) is System.Text.Json.JsonException, "empty metadata is malformed");
                Check(await ChordSupport.Rejection(remote.GetBatchAsync("s", 0, context)) is System.Text.Json.JsonException, "empty slot is malformed");
            })),
            Case("sync.files.publish-list-and-refuse-without-reading", () => SyncFileDirectory(async (path, fs) =>
            {
                var remote = new FileSystemSyncRemote(fs, path);
                Check(await remote.PutBatchAsync("s", 0, 3, "a", SyncFileBody("abc"), context) is PutBatchResult.Created, "created");
                var existing = await remote.PutBatchAsync("s", 0, 9, "b", SyncFilePoison(), context);
                Equal(new PutBatchResult.Exists(3, "a"), existing, "same slot remains occupied");
                Equal(new PutBatchResult.NotHead(3), await remote.PutBatchAsync("s", 1, 9, "b", SyncFilePoison(), context), "not head");
                Check(await remote.PutBatchAsync("s", 3, 4, "a", SyncFileBody("d"), context, new(Epoch: 12)) is PutBatchResult.Created,
                    "filesystem remote ignores epochs");
                var listing = await remote.ListAsync("s", context);
                Sequence(new long[] { 0, 3 }, listing.Batches.Select(batch => batch.After), "ordered slots");
                Equal(4L, SyncRemote.Head(listing), "head");
                Equal("abc", await SyncFileText((await remote.GetBatchAsync("s", 0, context))!), "body round trip");
                Check(File.Exists(Path.Combine(path, "s", "slots", "0000000000000000.json")), "fixed width slot");
                Json("""{"after":0,"to":3,"device":"a"}""", JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(path, "s", "slots", "0000000000000000.json"))), "wire slot");
                Equal<IAsyncEnumerable<ReadOnlyMemory<byte>>?>(null, await remote.GetBatchAsync("s", 4, context), "missing slot");
            })),
            Case("sync.files.fold-snapshot-and-session-enumeration", () => SyncFileDirectory(async (path, fs) =>
            {
                var remote = new FileSystemSyncRemote(fs, path);
                await remote.PutBatchAsync("z", 0, 3, "a", SyncFileBody("archive"), context, new(Archive: true));
                await remote.PutSnapshotAsync("z", 3, SyncFileBody("snapshot"), context);
                await remote.PutMetaAsync("z", new() { ["name"] = "Zed" }, context);
                await remote.PutMetaAsync("a", new() { ["name"] = "First" }, context);
                Directory.CreateDirectory(Path.Combine(path, ".invalid"));
                await File.WriteAllTextAsync(Path.Combine(path, "regular-file"), "");
                await remote.FoldBatchesAsync("z", [0, 10], context);
                await remote.FoldBatchesAsync("z", [0], context);
                var listing = await remote.ListAsync("z", context);
                Equal(new BatchInfo(0, 3, "a", Folded: true, Archive: true), listing.Batches.Single(), "folded tombstone");
                Equal(3L, SyncRemote.Head(listing), "tombstone preserves head");
                Equal<IAsyncEnumerable<ReadOnlyMemory<byte>>?>(null, await remote.GetBatchAsync("z", 0, context), "folded body absent");
                Equal(new PutBatchResult.Exists(3, "a"), await remote.PutBatchAsync("z", 0, 7, "b", SyncFilePoison(), context), "folded slot occupied");
                Equal("snapshot", await SyncFileText((await remote.GetSnapshotAsync("z", 3, context))!), "snapshot content");
                var sessions = await remote.SessionsAsync("ignored", context);
                Sequence(new[] { "a", "z" }, sessions.Sessions.Select(session => session.Id), "sorted valid directories only");
                Equal("", sessions.Cursor, "no cursor"); Check(!sessions.More, "single page");
                Json("""{"name":"Zed"}""", sessions.Sessions[1].Meta, "session metadata");
                await remote.DeleteSnapshotsAsync("z", [3, 8], context);
                Equal(0, (await remote.ListAsync("z", context)).Snapshots.Count, "snapshots deleted");
                Equal<IAsyncEnumerable<ReadOnlyMemory<byte>>?>(null, await remote.GetSnapshotAsync("z", 3, context), "deleted snapshot missing");
            })),
            Case("sync.files.body-error-cleans-temp-and-never-publishes", () => SyncFileDirectory(async (path, fs) =>
            {
                var remote = new FileSystemSyncRemote(fs, path);
                var disposed = false;
                async IAsyncEnumerable<ReadOnlyMemory<byte>> Broken()
                {
                    try { yield return Encoding.UTF8.GetBytes("partial"); await Task.Yield(); throw new IOException("broken source"); }
                    finally { disposed = true; }
                }
                Check(await ChordSupport.Rejection(remote.PutBatchAsync("s", 0, 2, "a", Broken(), context)) is IOException, "source error propagates");
                Check(disposed, "source released");
                Equal(0, (await remote.ListAsync("s", context)).Batches.Count, "no partial slot");
                Equal(0, Directory.GetFiles(path, "*.tmp", SearchOption.AllDirectories).Length, "no partial temp");
                Check(await remote.PutBatchAsync("s", 0, 2, "a", SyncFileBody("good"), context) is PutBatchResult.Created, "retry succeeds");
            })),
            Case("sync.files.slot-rename-failure-leaves-body-invisible", () => SyncFileDirectory(async (path, fs) =>
            {
                var remote = new FileSystemSyncRemote(fs, path);
                fs.FailRename = destination => destination.EndsWith(".json", StringComparison.Ordinal);
                Check(await ChordSupport.Rejection(remote.PutBatchAsync("s", 0, 2, "a", SyncFileBody("complete"), context)) is IOException, "rename failure");
                Equal("complete", await File.ReadAllTextAsync(Path.Combine(path, "s", "batches", "0000000000000000.batch")), "body published before slot");
                Equal(0, (await remote.ListAsync("s", context)).Batches.Count, "slot never advertised");
                Equal<IAsyncEnumerable<ReadOnlyMemory<byte>>?>(null, await remote.GetBatchAsync("s", 0, context), "orphan body hidden");
                Equal(0, Directory.GetFiles(path, "*.tmp", SearchOption.AllDirectories).Length, "failed rename temp removed");
                fs.FailRename = null;
                await remote.PutBatchAsync("s", 0, 2, "a", SyncFileBody("retry"), context);
                Equal("retry", await SyncFileText((await remote.GetBatchAsync("s", 0, context))!), "retry replaces orphan");
            })),
            Case("sync.files.cancelled-write-cleans-source-and-temp", () => SyncFileDirectory(async (path, fs) =>
            {
                var remote = new FileSystemSyncRemote(fs, path);
                using var cancellation = new CancellationTokenSource();
                var cancelledContext = ChordContext.WithAbortSignal(cancellation.Token, context);
                var disposed = false;
                async IAsyncEnumerable<ReadOnlyMemory<byte>> CancelBody()
                {
                    try
                    {
                        yield return Encoding.UTF8.GetBytes("first");
                        cancellation.Cancel();
                        await Task.Yield();
                        yield return Encoding.UTF8.GetBytes("last");
                    }
                    finally { disposed = true; }
                }
                Check(await ChordSupport.Rejection(remote.PutBatchAsync("s", 0, 1, "a", CancelBody(), cancelledContext)) is OperationCanceledException, "cancel propagated");
                Check(disposed, "cancelled source released");
                Equal(0, Directory.GetFiles(path, "*.tmp", SearchOption.AllDirectories).Length, "cleanup ignores cancelled context");
                Equal(0, (await remote.ListAsync("s", context)).Batches.Count, "cancelled body invisible");
            })),
            Case("sync.files.streaming-is-bounded-and-closes-readers", () => SyncFileDirectory(async (path, fs) =>
            {
                var remote = new FileSystemSyncRemote(fs, path);
                var bytes = new byte[700_000];
                for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
                await remote.PutSnapshotAsync("s", 5, SyncFiles.Once(bytes), context);
                var direct = (await fs.OpenBinaryReaderAsync(Path.Combine(path, "s", "snapshots", "0000000000000005.batch"), null, context)).Unwrap();
                try
                {
                    var large = (await direct.ReadAsync(0, 400_000, context)).Unwrap();
                    Equal(400_000, large.Length, "native reader honors requested length beyond remote chunk size");
                    Check(bytes.AsSpan(0, 400_000).SequenceEqual(large.Span), "native reader exact large read");
                    Equal(100_000, (await direct.ReadAsync(600_000, 400_000, context)).Unwrap().Length, "native reader short only at EOF");
                }
                finally { await direct.CloseAsync(context); }
                var unread = await remote.GetSnapshotAsync("s", 5, context);
                Check(unread is not null, "body exists"); Equal(0, fs.OpenReaders, "unconsumed body holds no handle");
                var result = new MemoryStream();
                await foreach (var chunk in unread!) { Check(chunk.Length <= 256 * 1024, "bounded chunk"); result.Write(chunk.Span); }
                Check(bytes.AsSpan().SequenceEqual(result.ToArray()), "stream exact bytes"); Equal(0, fs.OpenReaders, "EOF releases reader");
                var body = (await remote.GetSnapshotAsync("s", 5, context))!;
                await using (var enumerator = body.GetAsyncEnumerator())
                { Check(await enumerator.MoveNextAsync(), "first chunk"); Equal(1, fs.OpenReaders, "reader while consuming"); }
                Equal(0, fs.OpenReaders, "early exit releases reader");
                using var cancellation = new CancellationTokenSource();
                body = (await remote.GetSnapshotAsync("s", 5, context))!;
                await using (var enumerator = body.GetAsyncEnumerator(cancellation.Token))
                {
                    Check(await enumerator.MoveNextAsync(), "first cancellation chunk"); cancellation.Cancel();
                    Check(await ChordSupport.Rejection(enumerator.MoveNextAsync().AsTask()) is OperationCanceledException, "enumeration cancellation");
                }
                Equal(0, fs.OpenReaders, "cancellation releases reader");
                fs.FailRead = true;
                body = (await remote.GetSnapshotAsync("s", 5, context))!;
                Check(await ChordSupport.Rejection(SyncFileText(body)) is IOException, "reader failure");
                Equal(0, fs.OpenReaders, "reader failure releases reader");
            })),
            Case("sync.files.concurrent-publications-have-one-winner", () => SyncFileDirectory(async (path, fs) =>
            {
                var first = new FileSystemSyncRemote(fs, path); var second = new FileSystemSyncRemote(fs, path);
                var results = await Task.WhenAll(first.PutBatchAsync("s", 0, 1, "a", SyncFileBody("a"), context),
                    second.PutBatchAsync("s", 0, 2, "b", SyncFileBody("b"), context));
                Equal(1, results.Count(result => result is PutBatchResult.Created), "one winner");
                Equal(1, results.Count(result => result is PutBatchResult.Exists), "one refused");
                var slot = (await first.ListAsync("s", context)).Batches.Single();
                Equal(slot.Device, await SyncFileText((await first.GetBatchAsync("s", 0, context))!), "slot and body same winner");
            })),
            Case("sync.files.state-roundtrip-and-atomic-failure", () => SyncFileDirectory(async (path, fs) =>
            {
                var state = new FileSyncStateStore(fs, path);
                Equal<SyncState?>(null, await state.GetAsync("s", context), "missing state");
                await state.SetAsync("s", new(3, 8), context);
                Equal(new SyncState(3, 8), await state.GetAsync("s", context), "interrupted pull state");
                Json("""{"syncedHead":3,"pulling":8}""", JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(path, "s.json"))), "lower camel state");
                fs.FailRename = _ => true;
                Check(await ChordSupport.Rejection(state.SetAsync("s", new(10), context)) is IOException, "state publish fails");
                Equal(new SyncState(3, 8), await state.GetAsync("s", context), "old state survives failure");
                Equal(0, Directory.GetFiles(path, "*.tmp").Length, "state temp cleaned");
                fs.FailRename = null;
                await state.SetAsync("s", new(null), context);
                Json("{}", JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(path, "s.json"))), "absent fields omitted");
                await state.DeleteAsync("s", context); await state.DeleteAsync("s", context);
                Equal<SyncState?>(null, await state.GetAsync("s", context), "deleted state");
                Check(await ChordSupport.Rejection(state.SetAsync("../escape", new(1), context)) is ArgumentException, "state id rejects traversal");
            })),
            Case("sync.files.native-error-classification", () => SyncFileDirectory(async (path, fs) =>
            {
                Check((await fs.ReadTextFileAsync(Path.Combine(path, "missing"), context)).IsNotFound, "native missing text");
                Check((await fs.OpenBinaryReaderAsync(Path.Combine(path, "missing"), null, context)).IsNotFound, "native missing reader");
                Check(FileResult<bool>.Failure(new SyncFileException("not_found", "missing")).IsNotFound, "portable missing classification");
                Check(!FileResult<bool>.Failure(new IOException("failure")).IsNotFound, "other errors preserved");
                using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
                var result = await fs.WriteFileAsync(Path.Combine(path, "cancelled"), ReadOnlyMemory<byte>.Empty,
                    ChordContext.WithAbortSignal(cancellation.Token, context));
                Check(result.Exception is OperationCanceledException, "native cancellation result");
                Check(!File.Exists(Path.Combine(path, "cancelled")), "cancelled write never starts");
            }))
        ];
    }

    private static async Task SyncFileDirectory(Func<string, SyncFileTestSystem, Task> run)
    {
        var directory = Directory.CreateTempSubdirectory("chord-sync-");
        try { await run(directory.FullName, new SyncFileTestSystem()); }
        finally { directory.Delete(true); }
    }
    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> SyncFileBody(string text)
    { yield return Encoding.UTF8.GetBytes(text); await Task.CompletedTask; }
    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> SyncFilePoison()
    {
        await Task.Yield();
        throw new InvalidOperationException("Refused uploads must not consume their body");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
    private static async Task<string> SyncFileText(IAsyncEnumerable<ReadOnlyMemory<byte>> body)
    {
        using var output = new MemoryStream();
        await foreach (var chunk in body) output.Write(chunk.Span);
        return Encoding.UTF8.GetString(output.ToArray());
    }
    private sealed class SyncFileTestSystem : ISyncFileSystem
    {
        private readonly LocalSyncFileSystem native = new();
        public Func<string, bool>? FailRename { get; set; }
        public bool FailRead { get; set; }
        public int OpenReaders { get; private set; }
        public Task<FileResult<string>> JoinPathAsync(IReadOnlyList<string> parts, ChordContext context) => native.JoinPathAsync(parts, context);
        public Task<FileResult<string>> ReadTextFileAsync(string path, ChordContext context) => native.ReadTextFileAsync(path, context);
        public Task<FileResult<ReadOnlyMemory<byte>>> ReadBinaryFileAsync(string path, ChordContext context) => native.ReadBinaryFileAsync(path, context);
        public async Task<FileResult<ISyncFileReader>> OpenBinaryReaderAsync(string path, SyncFileReaderOptions? options, ChordContext context)
        {
            var result = await native.OpenBinaryReaderAsync(path, options, context);
            if (!result.Ok) return result;
            OpenReaders++;
            return FileResult<ISyncFileReader>.Success(new CountedReader(this, result.Unwrap()));
        }
        public Task<FileResult<bool>> WriteFileAsync(string path, ReadOnlyMemory<byte> content, ChordContext context) => native.WriteFileAsync(path, content, context);
        public Task<FileResult<bool>> AppendFileAsync(string path, ReadOnlyMemory<byte> content, ChordContext context) => native.AppendFileAsync(path, content, context);
        public Task<FileResult<bool>> RenameFileAsync(string sourcePath, string destinationPath, ChordContext context)
            => FailRename?.Invoke(destinationPath) == true ? Task.FromResult(FileResult<bool>.Failure(new IOException("Injected rename failure")))
                : native.RenameFileAsync(sourcePath, destinationPath, context);
        public Task<FileResult<IReadOnlyList<SyncFileInfo>>> ListDirAsync(string path, ChordContext context) => native.ListDirAsync(path, context);
        public Task<FileResult<bool>> CreateDirAsync(string path, SyncCreateDirectoryOptions? options, ChordContext context) => native.CreateDirAsync(path, options, context);
        public Task<FileResult<bool>> RemoveAsync(string path, SyncRemoveOptions? options, ChordContext context) => native.RemoveAsync(path, options, context);
        private sealed class CountedReader(SyncFileTestSystem owner, ISyncFileReader reader) : ISyncFileReader
        {
            private bool closed;
            public Task<FileResult<ReadOnlyMemory<byte>>> ReadAsync(long offset, int length, ChordContext context)
                => owner.FailRead ? Task.FromResult(FileResult<ReadOnlyMemory<byte>>.Failure(new IOException("Injected read failure")))
                    : reader.ReadAsync(offset, length, context);
            public async Task CloseAsync(ChordContext context)
            {
                if (closed) return;
                closed = true;
                try { await reader.CloseAsync(context); } finally { owner.OpenReaders--; }
            }
        }
    }
}
