// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/app/files.ts, packages/chord/src/node/app/files-copy.ts, packages/chord/src/node/app/uploads.ts.
// Authored native regression; not an upstream execution capture.
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Data;
using PiSharp.Chord.Host;
using PiSharp.Chord.Hosting.App;
using PiSharp.Chord.Store;
using PiSharp.Chord.Sqlite;
using AppFile = PiSharp.Chord.Hosting.App.FileInfo;

internal static class FilesCases
{
    private static readonly ChordContext Context = ChordContext.Background;
    internal static async Task RunAsync()
    {
        // The managed runner can set owner ACLs in the workspace; system TEMP may deny WRITE_DAC.
        var root = Path.GetFullPath(Path.Combine("artifacts", "chord-files", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            await MetadataAndUploads(root);
            await PersistenceAndCopy(root);
            await FolderWindow(root);
            await ExpiryAndCancellation(root);
            await UnknownCommitPreservesBlobs(root);
            await CopyRetainsSourceSnapshot(root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    private static FileWrite Write(string path, string? key = null, string space = "shared") => new(space, path, "text/plain", "test", "source-ref", key,
        new JsonObject { ["nested"] = new JsonArray(1, "value", true) });
    private static async Task<ChordStore> Store(string root, string name) =>
        (ChordStore)await Layout.OpenStoreAsync(Path.Combine(root, name + ".sqlite"), Context);
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("Files: " + message); }
    private static async Task<ChordError> Refuses(Func<Task> work, string code = "invalid")
    {
        try { await work(); }
        catch (ChordError error) { Check(error.Code == code, "wrong refusal: " + error); return error; }
        throw new Exception("Files: expected refusal " + code);
    }
    private static async Task MetadataAndUploads(string root)
    {
        await using var store = await Store(root, "basic");
        await using var files = await FilesService.OpenAsync(store, Path.Combine(root, "basic-files"), Context, new(1024, 16), 2);
        var a = await files.PutAsync(Write("docs/a", "same"), Encoding.UTF8.GetBytes("one"), Context);
        var same = await files.PutAsync(Write("elsewhere", "same"), Encoding.UTF8.GetBytes("ignored"), Context);
        Check(a.Id == same.Id && files.StoredBytes == 3, "per-space idempotency");
        Check(Encoding.UTF8.GetString(await files.ReadAsync(a.Id, 1, 99, Context)) == "ne", "range read");
        await Refuses(() => files.PutAsync(Write("docs"), new byte[] { 1 }, Context));
        await Refuses(() => files.PutAsync(Write("docs/a/child"), new byte[] { 1 }, Context));
        foreach (var path in new[] { "", "/root", "a//b", "a/../b", "a/./b", "a/", "a\0b" })
            await Refuses(() => files.PutAsync(Write(path), new byte[] { 1 }, Context));
        await Refuses(() => files.PutAsync(Write("valid", space: "Bad"), new byte[] { 1 }, Context));
        var begin = await files.BeginAsync(Write("large", "upload-key"), Context);
        Check((await files.BeginAsync(Write("other", "upload-key"), Context)).Upload == begin.Upload, "in-flight key resumes");
        var second = await files.BeginAsync(Write("second"), Context);
        await Refuses(() => files.BeginAsync(Write("third"), Context), "unavailable");
        await files.CancelAsync(second.Upload!, Context);
        var chunk = new byte[] { 4, 5, 6 };
        var append1 = files.AppendAsync(begin.Upload!, 0, chunk, Context);
        chunk[0] = 99; // Accepted input belongs to the caller only until AppendAsync is called.
        var append2 = files.AppendAsync(begin.Upload!, 3, new byte[] { 7, 8 }, Context);
        Check(await files.UploadedAsync(begin.Upload!, Context) == 5, "uploaded waits for accepted writes");
        await Task.WhenAll(append1, append2);
        var competing = await files.BeginAsync(Write("competing"), Context);
        await files.AppendAsync(competing.Upload!, 0, new byte[8], Context);
        await Refuses(() => files.AppendAsync(begin.Upload!, 5, new byte[1], Context), "too_large");
        await files.CancelAsync(competing.Upload!, Context);
        var mismatch = await Refuses(() => files.AppendAsync(begin.Upload!, 4, new byte[] { 9 }, Context));
        Check(mismatch.Details?["offset"]?.GetValue<long>() == 5, "append reports accepted offset");
        await Refuses(() => files.AppendAsync(begin.Upload!, 5, new byte[9], Context), "too_large");
        var finished = await files.FinishAsync(begin.Upload!, Context);
        Check((await files.ReadAsync(finished.Id, 0, 100, Context)).SequenceEqual(new byte[] { 4, 5, 6, 7, 8 }), "ordered immutable accepted chunks");
        Check((await files.BeginAsync(Write("irrelevant", "upload-key"), Context)).File?.Id == finished.Id, "finished key returns existing version");
        var copy = await files.CopyAsync(a.Id, "shared", "copy", Context);
        Check(files.StoredBytes == 8, "copy shares blob bytes");
        await files.RemoveAsync(a.Id, Context);
        Check(files.StoredBytes == 8 && (await files.ReadAsync(copy.Id, 0, 10, Context)).Length == 3, "refcounts retain shared blob");
        await files.RemoveAsync(copy.Id, Context);
        Check(files.StoredBytes == 5, "last reference releases blob");
        var summary = await files.Summary.GetAsync("shared", Context);
        Check(summary?["files"]?.GetValue<int>() == 1 && summary["versions"]?.GetValue<int>() == 1 && summary["bytes"]?.GetValue<int>() == 5, "summary reflects committed metadata");
        // Exercise the public facet, including byte-leaf arguments and keyed summary state.
        await using var host = Hosts.Create(new() { OnError = e => throw new Exception(e.Kind), Store = store });
        await host.LoadAsync([new("files", FilesFacet.Create(files))], Context);
        using var reference = await host.GetAsync(FilesFacet.Files, new(), Context);
        var args = new JsonObject { ["space"] = "public", ["path"] = "x", ["mime"] = "text/plain", ["source"] = "test", ["bytes"] = JsonValue.Create(new byte[] { 1, 2 }) };
        var result = (JsonObject)(await reference.CallAsync("put", args, Context))!;
        Check(result["version"]?.GetValue<int>() == 1, "facet put");
        var raw = (JsonValue)(await reference.CallAsync("read", new JsonObject { ["id"] = result["id"]!.GetValue<string>(), ["offset"] = 0, ["length"] = 2 }, Context))!;
        Check(raw.GetValue<byte[]>().SequenceEqual(new byte[] { 1, 2 }), "facet read byte payload");
        await Refuses(() => reference.CallAsync("read", new JsonObject { ["id"] = result["id"]!.GetValue<string>(), ["offset"] = 0, ["length"] = 2_147_483_648L }, Context), "too_large");
        using var folder = await host.GetAsync(FilesFacet.FilesFolder, new() { ["space"] = "public", ["folder"] = "" }, Context);
        Check(folder.Member("listing").Value?["entries"]?.AsArray().Count == 1, "native folder member resolves through a reference");
        var summaryWatch = await reference.KeyedMember("summary").WatchAsync("public", Context);
        Check(summaryWatch?.Value?["files"]?.GetValue<int>() == 1, "native keyed summary resolves through a reference");
        if (summaryWatch is not null) await summaryWatch.StopAsync();
    }
    private static async Task PersistenceAndCopy(string root)
    {
        var directory = Path.Combine(root, "persist-files");
        AppFile original;
        await using (var store = await Store(root, "persist"))
        await using (var files = await FilesService.OpenAsync(store, directory, Context))
        {
            original = await files.PutAsync(Write("old"), new byte[] { 1 }, Context);
            for (var i = 0; i < 5; i++) await files.PutAsync(Write("target"), new byte[] { 2 }, Context);
            await files.RemovePathAsync("shared", "target", Context);
            await files.MoveAsync("shared", "old", "target", Context);
            Check((await files.GetAsync("shared", "target", Context))?.Version == 1, "move keeps version");
            Check((await files.PutAsync(Write("target"), new byte[] { 3 }, Context)).Version == 6, "move merges target highwater");
            await files.RemoveSpaceAsync("shared", Context);
            Check(await files.Summary.GetAsync("shared", Context) is null, "removeSpace removes summary");
            Check((await files.PutAsync(Write("target"), new byte[] { 4 }, Context)).Version == 7, "removeSpace preserves highwater");
            await files.BeginAsync(Write("unfinished"), Context);
        }
        // An interrupted process can leave both an old upload run and an uncommitted blob.
        var oldRun = Path.Combine(directory, ".uploads", "orphan"); Directory.CreateDirectory(oldRun);
        await File.WriteAllTextAsync(Path.Combine(oldRun, "partial"), "uncommitted");
        var orphan = Path.Combine(directory, "blobs", new string('a', 36)); await File.WriteAllTextAsync(orphan, "orphan");
        await using var reopenedStore = await Store(root, "persist");
        await using var reopened = await FilesService.OpenAsync(reopenedStore, directory, Context);
        Check(!Directory.Exists(oldRun) && !File.Exists(orphan), "startup sweeps uncommitted data");
        Check((await reopened.GetAsync("shared", "target", Context))?.Version == 7, "metadata survives store reopen");
        await using var target = await Store(root, "copy");
        var copiedDirectory = Path.Combine(root, "copied-files");
        await CopyFiles.CopyAsync(reopenedStore, directory, target, copiedDirectory, Context);
        await using var copied = await FilesService.OpenAsync(target, copiedDirectory, Context);
        var copiedFile = await copied.GetAsync("shared", "target", Context);
        Check(copiedFile?.Version == 7 && (await copied.ReadAsync(copiedFile.Id, 0, 1, Context))[0] == 4, "copy retains metadata and blobs");
        await Refuses(() => CopyFiles.CopyAsync(reopenedStore, directory, target, copiedDirectory, Context));
        // A source read failure must clean only the new destination staging area.
        var retained = await reopened.SnapshotBlobsAsync(Context);
        File.Delete(retained[0].Path);
        var failedTarget = Path.Combine(root, "failed-copy");
        await using var emptyTarget = await Store(root, "empty-target");
        try { await CopyFiles.CopyAsync(reopenedStore, directory, emptyTarget, failedTarget, Context); throw new Exception("Files: expected missing source"); }
        catch (FileNotFoundException) { }
        Check(!Directory.Exists(failedTarget) && !Directory.EnumerateDirectories(root, ".files-copy-*").Any(), "copy failure owns its cleanup");
        Check(original.Version == 1, "stable original result");
    }
    private static async Task FolderWindow(string root)
    {
        await using var store = await Store(root, "folders");
        await using var files = await FilesService.OpenAsync(store, Path.Combine(root, "folder-files"), Context);
        var source = files.Folder("shared", "");
        var watch = await source.WatchAsync(Context);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watch.Start((_, _) =>
        {
            if (source.Value?["more"]?.GetValue<bool>() == true) observed.TrySetResult();
            return Task.CompletedTask;
        });
        try
        {
            for (var i = 0; i < 501; i++) await files.MkdirAsync("shared", i.ToString("D3"), Context);
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(source.Value?["entries"]?.AsArray().Count == 500, "folder view window");
            var first = await files.ListAsync("shared", "", Context, 500);
            var second = await files.ListAsync("shared", "", Context, 500, first.Next);
            Check(first.Items.Count == 500 && second.Items.Count == 1 && second.Next is null, "listing pagination");
            await files.PutAsync(Write("zzz"), new byte[] { 1 }, Context);
            Check((await files.ListAsync("shared", "", Context, 1000)).Items[^1].Kind == "file", "folders precede files");
        }
        finally { await watch.StopAsync(); }
    }
    private static async Task ExpiryAndCancellation(string root)
    {
        await using var store = await Store(root, "expiry");
        var clock = new ManualClock();
        await using var files = await FilesService.OpenAsync(store, Path.Combine(root, "expiry-files"), Context, new(10, 10), 1, timeProvider: clock);
        var started = await files.BeginAsync(Write("expired", "key"), Context);
        await files.AppendAsync(started.Upload!, 0, new byte[] { 1, 2, 3 }, Context);
        clock.Now += TimeSpan.FromHours(1);
        await files.SweepAsync();
        await Refuses(() => files.UploadedAsync(started.Upload!, Context));
        var replacement = await files.BeginAsync(Write("new", "key"), Context);
        Check(replacement.Upload != started.Upload, "expiry releases upload key and capacity");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try
        {
            await files.PutAsync(Write("cancelled"), new byte[] { 1 }, ChordContext.WithAbortSignal(cancelled.Token, Context));
            throw new Exception("Files: expected cancellation");
        }
        catch (OperationCanceledException) { }
        Check(await files.GetAsync("shared", "cancelled", Context) is null && files.StoredBytes == 0, "pre-admission cancellation leaves no version");
    }
    private sealed class ManualClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1_000_000);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static async Task<(ChordStore Store, ControlledDatabase Database)> ControlledStore(string root, string name)
    {
        var backend = await Layout.OpenBackendAsync(Path.Combine(root, name + ".sqlite"));
        var database = new ControlledDatabase(backend.Db);
        return ((ChordStore)await Tables.OpenStoreAsync(new Backend(database), Context), database);
    }
    private static async Task UnknownCommitPreservesBlobs(string root)
    {
        var directory = Path.Combine(root, "uncertain-put-files");
        var (store, database) = await ControlledStore(root, "uncertain-put");
        await using (store)
        await using (var files = await FilesService.OpenAsync(store, directory, Context))
        {
            database.ThrowAfterNextTransaction = true;
            var error = await Refuses(() => files.PutAsync(Write("committed"), new byte[] { 11, 12 }, Context), "unavailable");
            Check(error.Reason == "storage_failed", "injected post-COMMIT failure reaches caller");
        }
        await using var reopenedStore = await Store(root, "uncertain-put");
        await using var reopened = await FilesService.OpenAsync(reopenedStore, directory, Context);
        var committed = await reopened.GetAsync("shared", "committed", Context);
        Check(committed is not null && (await reopened.ReadAsync(committed.Id, 0, 2, Context)).SequenceEqual(new byte[] { 11, 12 }),
            "successful COMMIT followed by failure never removes referenced put bytes");

        var uploadDirectory = Path.Combine(root, "uncertain-upload-files");
        var (uploadStore, uploadDatabase) = await ControlledStore(root, "uncertain-upload");
        await using (uploadStore)
        await using (var files = await FilesService.OpenAsync(uploadStore, uploadDirectory, Context))
        {
            var started = await files.BeginAsync(Write("uploaded"), Context);
            await files.AppendAsync(started.Upload!, 0, new byte[] { 13 }, Context);
            uploadDatabase.ThrowAfterNextTransaction = true;
            await Refuses(() => files.FinishAsync(started.Upload!, Context), "unavailable");
        }
        await using (var verifiedStore = await Store(root, "uncertain-upload"))
        await using (var verified = await FilesService.OpenAsync(verifiedStore, uploadDirectory, Context))
        {
            var file = await verified.GetAsync("shared", "uploaded", Context);
            Check(file is not null && (await verified.ReadAsync(file.Id, 0, 1, Context))[0] == 13,
                "successful COMMIT followed by failure never removes referenced upload bytes");
        }

        var copiedDirectory = Path.Combine(root, "uncertain-copy-files");
        var (target, targetDatabase) = await ControlledStore(root, "uncertain-copy");
        await using (target)
        {
            targetDatabase.ThrowAfterNextTransaction = true;
            await Refuses(() => CopyFiles.CopyAsync(reopenedStore, directory, target, copiedDirectory, Context), "unavailable");
        }
        Check(Directory.Exists(copiedDirectory), "uncertain copy retains installed directory");
        await using var copyStore = await Store(root, "uncertain-copy");
        await using var copy = await FilesService.OpenAsync(copyStore, copiedDirectory, Context);
        var copyFile = await copy.GetAsync("shared", "committed", Context);
        Check(copyFile is not null && (await copy.ReadAsync(copyFile.Id, 0, 2, Context)).SequenceEqual(new byte[] { 11, 12 }),
            "successful COMMIT followed by failure never removes referenced copied bytes");
    }
    private static async Task CopyRetainsSourceSnapshot(string root)
    {
        var (source, database) = await ControlledStore(root, "retained-source");
        await using (source)
        await using (var files = await FilesService.OpenAsync(source, Path.Combine(root, "retained-source-files"), Context))
        await using (var target = await Store(root, "retained-target"))
        {
            var file = await files.PutAsync(Write("held"), new byte[] { 21 }, Context);
            var pause = database.PauseAfterNextTransaction = new();
            var destination = Path.Combine(root, "retained-target-files");
            var copying = CopyFiles.CopyAsync(source, files.DirectoryPath, target, destination, Context);
            await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var removal = files.RemoveAsync(file.Id, Context);
            Check(!removal.IsCompleted, "source removal cannot overtake a held copy snapshot");
            pause.Resume.TrySetResult();
            await copying;
            await removal;
            await using var copied = await FilesService.OpenAsync(target, destination, Context);
            Check((await copied.ReadAsync(file.Id, 0, 1, Context))[0] == 21, "copy keeps its source snapshot through blob reads");
        }
    }
    private sealed class DatabasePause
    {
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class ControlledDatabase(ISqliteDatabase inner) : ISqliteDatabase
    {
        internal bool ThrowAfterNextTransaction;
        internal DatabasePause? PauseAfterNextTransaction;
        public async Task<T> TransactionAsync<T>(Func<ISqliteExecutor, Task<T>> callback)
        {
            var pause = PauseAfterNextTransaction; PauseAfterNextTransaction = null;
            var fail = ThrowAfterNextTransaction; ThrowAfterNextTransaction = false;
            var result = await inner.TransactionAsync(callback);
            if (pause is not null) { pause.Reached.TrySetResult(); await pause.Resume.Task; }
            if (fail) throw new IOException("Injected failure after successful COMMIT");
            return result;
        }
        public Task ExecAsync(string sql) => inner.ExecAsync(sql);
        public Task RunAsync(string sql, params object?[] parameters) => inner.RunAsync(sql, parameters);
        public Task<long> RunCountedAsync(string sql, params object?[] parameters) => inner.RunCountedAsync(sql, parameters);
        public Task<IReadOnlyDictionary<string, object?>?> GetAsync(string sql, params object?[] parameters) => inner.GetAsync(sql, parameters);
        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> AllAsync(string sql, params object?[] parameters) => inner.AllAsync(sql, parameters);
        public Task CloseAsync() => inner.CloseAsync();
    }
}
