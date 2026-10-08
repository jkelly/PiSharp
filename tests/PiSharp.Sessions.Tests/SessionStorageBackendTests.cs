using System.Text;
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Sessions.Import;
using PiSharp.Sessions.Lifecycle;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;
using PiSharp.Sessions.Tree;

internal static class SessionStorageBackendTests
{
    private const string Time = "2026-10-02T00:00:00.000Z";
    private static readonly SessionEntryCodec Codec = new();
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("session-backend memory acknowledges volatility reopens immutable checkpoints without filesystem", MemoryCheckpoint),
        ("session-backend memory owns exclusive writer leases and shares repeated close", MemoryLease),
        ("session-backend memory enforces namespace file byte and aggregate resident bounds", MemoryBounds),
        ("session-backend lazy metadata state and disposal stay unmaterialized until first user", LazyUser),
        ("session-backend lazy first assistant materializes the complete setup checkpoint", LazyAssistant),
        ("session-backend lazy first materialization preserves a racing unrelated destination", LazyRace),
        ("session-backend lazy exact copies retain blank whitespace and CRLF framing before and after conversation", LazyExactFraming),
        ("session-backend lazy file admission counts materialized pre-existing and pending namespace files", LazyFileBounds),
        ("session-backend branch publisher uses volatile and deferred receipts then real conversation publication", Branches),
        ("session-backend memory exact copy preserves all source bytes and refuses overwrite", MemoryCopy),
        ("session-backend memory catalog pagination cancellation and inert missing parent metadata", MemoryCatalog)
    ];
    private static SessionEntry Header(string cwd, string id = "same", string? parent = null) => Codec.Parse(JsonSerializer.Serialize(new
    { type = "session", version = 3, id, timestamp = Time, cwd, parentSession = parent }));
    private static SessionEntry State(string id, string? parent = null, string value = "setup") => Codec.Parse(
        "{\"type\":\"custom\",\"id\":" + JsonSerializer.Serialize(id) + ",\"parentId\":" + JsonSerializer.Serialize(parent) +
        ",\"timestamp\":\"" + Time + "\",\"customType\":\"fixture-state\",\"data\":{\"value\":" + JsonSerializer.Serialize(value) + ",\"opaque\":1.00e400}}");
    private static SessionEntry User(string id, string? parent) => Codec.Parse(JsonSerializer.Serialize(new
    { type = "message", id, parentId = parent, timestamp = Time, message = new { role = "user", content = "first accepted user", timestamp = 0 } }));
    private static SessionEntry Assistant(string id, string? parent) => Codec.Parse("{\"type\":\"message\",\"id\":" +
        JsonSerializer.Serialize(id) + ",\"parentId\":" + JsonSerializer.Serialize(parent) + ",\"timestamp\":\"" + Time + "\",\"message\":" +
        PiWireJson.WriteMessage(new AssistantMessage("api", "provider", "model", 0,
            [new TextContent("first assistant")], TokenUsage.Zero, StopReason.Stop)) + "}");
    private static string VirtualDirectory() => Path.Combine(Path.GetTempPath(), "pisharp-memory-" + Guid.NewGuid().ToString("N"));
    private static SessionLogStoreOptions Options(SessionStorageBackend backend) => new(StorageFactory: backend);
    private static async Task MemoryCheckpoint()
    {
        var root = VirtualDirectory(); var path = Path.Combine(root, "a.jsonl"); var backend = new SessionStorageBackend(root, SessionStorageMode.InMemory);
        SessionLogStoreSnapshot before;
        await using (var store = await SessionLogStore.CreateNewAsync(path, Header("foreign/cwd"), Options(backend)))
        {
            before = store.Snapshot;
            var result = await store.AppendAsync([State("state")]);
            Check(result.CheckpointAcknowledged && !result.DurableCheckpointAcknowledged && !result.Snapshot.IsMaterialized, "Memory claimed disk durability.");
            Equal(SessionLogStorageDurability.VolatileMemory, result.Snapshot.StorageDurability);
            Check(ReferenceEquals(result.Snapshot, store.Snapshot) && before.Entries.IsEmpty, "Memory acknowledgment or old snapshot changed.");
        }
        await using var reopened = await SessionLogStore.OpenAsync(path, Options(backend));
        Equal("state", reopened.Snapshot.LeafId); Equal("1.00e400", reopened.Snapshot.Entries[0].WireBody.Value.GetProperty("data").GetProperty("opaque").GetRawText());
        Check(!Directory.Exists(root) && !File.Exists(path), "In-memory sessions touched filesystem storage.");
    }
    private static async Task MemoryLease()
    {
        var root = VirtualDirectory(); var path = Path.Combine(root, "a.jsonl"); var backend = new SessionStorageBackend(root, SessionStorageMode.InMemory);
        var store = await SessionLogStore.CreateNewAsync(path, Header(root), Options(backend));
        Equal(1, backend.ActiveWriterCount);
        await Fails<SessionLogStoreException>(() => SessionLogStore.OpenAsync(path, Options(backend)));
        var one = store.DisposeAsync().AsTask(); var two = store.DisposeAsync().AsTask();
        Check(ReferenceEquals(one, two), "Repeated close created different join tasks."); await Task.WhenAll(one, two);
        Equal(0, backend.ActiveWriterCount);
        await using var reopened = await SessionLogStore.OpenAsync(path, Options(backend));
        await reopened.AppendAsync([State("after-close")]);
    }
    private static async Task MemoryBounds()
    {
        var root = VirtualDirectory(); var backend = new SessionStorageBackend(root, SessionStorageMode.InMemory, new(2, 1024, 4096));
        var path = Path.Combine(root, "a.jsonl");
        await using (var store = await SessionLogStore.CreateNewAsync(path, Header(root), Options(backend)))
        {
            var before = store.Snapshot;
            var error = await Fails<SessionLogStoreException>(() => store.AppendAsync([State("large", value: new string('x', 1024))]));
            Equal(SessionLogStoreFailure.WriteFailed, error.Failure); Check(store.IsPoisoned && ReferenceEquals(before, store.Snapshot), "Backend bound published rejected bytes.");
        }
        await using (var reopened = await SessionLogStore.OpenAsync(path, Options(backend))) Check(reopened.Snapshot.Entries.IsEmpty, "Rejected bytes became a memory checkpoint.");
        await using (var second = await SessionLogStore.CreateNewAsync(Path.Combine(root, "b.jsonl"), Header(root), Options(backend))) { }
        await Fails<SessionLogStoreException>(() => SessionLogStore.CreateNewAsync(Path.Combine(root, "c.jsonl"), Header(root), Options(backend)));
        await Fails<ArgumentException>(async () => { await backend.OpenAsync(Path.Combine(root, "nested", "outside.jsonl"), true, default); });
        var tight = new SessionStorageBackend(VirtualDirectory(), SessionStorageMode.InMemory, new(4, 512, 512));
        var tightPath = Path.Combine(tight.Directory, "tight.jsonl");
        await using var limited = await SessionLogStore.CreateNewAsync(tightPath, Header("cwd"), Options(tight));
        await Fails<SessionLogStoreException>(() => limited.AppendAsync([State("resident", value: new string('x', 130))]));
        Check(!Directory.Exists(root), "Resource bounds created a physical directory.");
    }
    private static async Task LazyUser()
    {
        using var directory = new LocalDirectory(); var path = Path.Combine(directory.Path, "lazy.jsonl");
        var backend = new SessionStorageBackend(directory.Path, SessionStorageMode.LazyLocal);
        await using (var store = await SessionLogStore.CreateNewAsync(path, Header(directory.Path), Options(backend)))
        {
            var result = await store.AppendAsync([State("setup")]);
            Check(result.CheckpointAcknowledged && !result.DurableCheckpointAcknowledged && !File.Exists(path), "Setup/state materialized lazy session.");
            Equal(SessionLogStorageDurability.DeferredLocalFile, result.Snapshot.StorageDurability);
        }
        Check(!File.Exists(path), "Disposal materialized lazy session.");
        await using (var store = await SessionLogStore.OpenAsync(path, Options(backend)))
        {
            var before = store.Snapshot;
            var accepted = await store.AppendAsync([User("user", "setup")]);
            Check(accepted.DurableCheckpointAcknowledged && accepted.Snapshot.IsMaterialized && File.Exists(path), "First accepted user was not durably materialized.");
            Check(!before.IsMaterialized && before.Entries.Length == 1, "Materialization rewrote old immutable checkpoint.");
            await store.AppendAsync([State("after", "user")]);
        }
        await using var actual = await SessionLogStore.OpenAsync(path);
        Check(actual.Snapshot.Entries.Select(entry => entry.Id).SequenceEqual(new[] { "setup", "user", "after" }), "First materialization lost accumulated setup or subsequent append.");
        Equal("1.00e400", actual.Snapshot.Entries[0].WireBody.Value.GetProperty("data").GetProperty("opaque").GetRawText());
    }
    private static async Task LazyAssistant()
    {
        using var directory = new LocalDirectory(); var path = Path.Combine(directory.Path, "assistant.jsonl");
        var backend = new SessionStorageBackend(directory.Path, SessionStorageMode.LazyLocal);
        await using (var store = await SessionLogStore.CreateNewAsync(path, Header(directory.Path), Options(backend)))
        {
            await store.AppendAsync([State("setup")]);
            var result = await store.AppendAsync([Assistant("assistant", "setup")]);
            Check(result.DurableCheckpointAcknowledged && File.Exists(path), "Assistant-first conversation remained unmaterialized.");
        }
        var read = await new SessionLogReader().ReadFileAsync(path); Equal(3, read.ValidatedPrefix.Length);
    }
    private static async Task LazyRace()
    {
        using var directory = new LocalDirectory(); var path = Path.Combine(directory.Path, "race.jsonl");
        var backend = new SessionStorageBackend(directory.Path, SessionStorageMode.LazyLocal);
        await using var store = await SessionLogStore.CreateNewAsync(path, Header(directory.Path), Options(backend));
        await File.WriteAllTextAsync(path, "unrelated racing bytes\n"); var before = store.Snapshot;
        var error = await Fails<SessionLogStoreException>(() => store.AppendAsync([User("user", null)]));
        Equal(SessionLogStoreFailure.FlushFailed, error.Failure);
        Check(!error.DurableFlushCompleted && store.IsPoisoned && ReferenceEquals(before, store.Snapshot), "Failed materialization published or claimed durability.");
        await store.DisposeAsync(); Equal("unrelated racing bytes\n", await File.ReadAllTextAsync(path)); Equal(0, backend.ActiveWriterCount);
    }
    private static async Task Branches()
    {
        foreach (var mode in new[] { SessionStorageMode.InMemory, SessionStorageMode.LazyLocal })
        {
            using var directory = mode == SessionStorageMode.LazyLocal ? new LocalDirectory() : null;
            var root = directory?.Path ?? VirtualDirectory(); var backend = new SessionStorageBackend(root, mode);
            var planner = new SessionBranchPlanner(); var publisher = new SessionBranchPublisher(fileSystem: backend);
            var path = Path.Combine(root, "empty.jsonl");
            await using (var branch = await publisher.PrepareAsync(planner.New("empty", Time, root, "missing-parent.jsonl"), path))
            { Check(branch.StorageDurability != SessionLogStorageDurability.LocalFileFlush && !File.Exists(path), "Empty branch eagerly published to disk."); branch.CommitAttachment(); }
            await using (var opened = await SessionLogStore.OpenAsync(path, Options(backend))) await opened.AppendAsync([User("root", null)]);
            var snapshot = await Read(backend, path);
            var copied = Path.Combine(root, "fork.jsonl");
            var plan = planner.Fork(new(Header(root, "empty"), snapshot.ValidatedPrefix.Skip(1).Select(record => record.Entry).ToImmutableArray(),
                "root", SessionForkPosition.At, "fork", Time, path));
            await using (var branch = await publisher.PrepareAsync(plan, copied))
            { Equal(mode == SessionStorageMode.LazyLocal, branch.StorageDurability == SessionLogStorageDurability.LocalFileFlush); branch.CommitAttachment(); }
            await using var reopened = await SessionLogStore.OpenAsync(copied, Options(backend)); Equal("root", reopened.Snapshot.LeafId);
        }
    }
    private static async Task LazyExactFraming()
    {
        using var directory = new LocalDirectory();
        var backend = new SessionStorageBackend(directory.Path, SessionStorageMode.LazyLocal);
        foreach (var conversation in new[] { false, true })
        {
            var source = Path.Combine(directory.Path, conversation ? "conversation-source.jsonl" : "setup-source.jsonl");
            var target = Path.Combine(directory.Path, conversation ? "conversation-copy.jsonl" : "setup-copy.jsonl");
            var bytes = Encoding.UTF8.GetBytes("\r\n \t\r\n" + Header(directory.Path).WireBody.Value.GetRawText() +
                "\r\n\t \r\n" + State("setup").WireBody.Value.GetRawText() + "\r\n\r\n" +
                (conversation ? User("user", "setup").WireBody.Value.GetRawText() + "\r\n \t\r\n" : " \t\r\n"));
            await File.WriteAllBytesAsync(source, bytes);
            var receipt = await new SessionCopyService(fileSystem: backend).CopyAsync(new(source, target));
            Check(receipt.Published, "Valid whitespace-framed exact copy did not publish.");
            Equal(conversation, File.Exists(target));
            Equal(conversation ? SessionLogStorageDurability.LocalFileFlush : SessionLogStorageDurability.DeferredLocalFile, receipt.StorageDurability);
            var copied = await Read(backend, target);
            Check(copied.Status == SessionLogReadStatus.Complete && copied.OriginalBytes.AsSpan().SequenceEqual(bytes), "Exact copy rewrote whitespace/CRLF source bytes.");
            await using var opened = await SessionLogStore.OpenAsync(target, Options(backend));
            Equal(conversation ? "user" : "setup", opened.Snapshot.LeafId);
            if (!conversation)
            {
                await opened.AppendAsync([User("first-user", "setup")]);
                Check(File.Exists(target), "Copied setup did not materialize on its first admitted conversation.");
                await opened.DisposeAsync();
                Check((await File.ReadAllBytesAsync(target)).AsSpan().StartsWith(bytes), "Materialization lost exact initial framing.");
            }
        }
        Equal(0, backend.ActiveWriterCount);
        Check(!Directory.EnumerateFiles(directory.Path).Any(path => Path.GetFileName(path).StartsWith(".pisharp-", StringComparison.Ordinal)), "Exact copy retained a temporary.");
    }
    private static async Task LazyFileBounds()
    {
        using var directory = new LocalDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "pre-existing.jsonl"), Header(directory.Path).WireBody.Value.GetRawText() + "\n");
        var backend = new SessionStorageBackend(directory.Path, SessionStorageMode.LazyLocal, new(3, 4096, 16384));
        var materialized = Path.Combine(directory.Path, "materialized.jsonl");
        await using (var store = await SessionLogStore.CreateNewAsync(materialized, Header(directory.Path), Options(backend)))
            await store.AppendAsync([User("user", null)]);
        var pending = Path.Combine(directory.Path, "pending.jsonl");
        await using (var store = await SessionLogStore.CreateNewAsync(pending, Header(directory.Path), Options(backend))) { }
        var rejected = Path.Combine(directory.Path, "excess.jsonl");
        await Fails<SessionLogStoreException>(() => SessionLogStore.CreateNewAsync(rejected, Header(directory.Path), Options(backend)));
        Check(!backend.FileExists(rejected) && backend.EnumerateFileNames(directory.Path).Count() == 3, "File admission leaked an excess session.");
        await using (var store = await SessionLogStore.OpenAsync(pending, Options(backend))) await store.AppendAsync([User("user", null)]);
        await Fails<SessionLogStoreException>(() => SessionLogStore.CreateNewAsync(rejected, Header(directory.Path), Options(backend)));
        await backend.DeleteOwnedAsync(materialized);
        await using (var store = await SessionLogStore.CreateNewAsync(rejected, Header(directory.Path), Options(backend))) { }
        Equal(3, backend.EnumerateFileNames(directory.Path).Count()); Equal(0, backend.ActiveWriterCount);
    }
    private static async Task MemoryCopy()
    {
        var root = VirtualDirectory(); var backend = new SessionStorageBackend(root, SessionStorageMode.InMemory);
        var source = Path.Combine(root, "source.jsonl"); var target = Path.Combine(root, "target.jsonl");
        await using (var store = await SessionLogStore.CreateNewAsync(source, Header(root), Options(backend))) await store.AppendAsync([State("opaque")]);
        var copy = await new SessionCopyService(fileSystem: backend).CopyAsync(new(source, target));
        Check(copy.Published && copy.StorageDurability == SessionLogStorageDurability.VolatileMemory && copy.OmittedRecords == 0 && copy.OmittedFields == 0,
            "Memory exact copy lost its publication or volatility receipt.");
        var before = await Read(backend, source); var after = await Read(backend, target);
        Check(before.OriginalBytes.AsSpan().SequenceEqual(after.OriginalBytes.AsSpan()), "Exact copy changed source bytes.");
        await Fails<SessionCopyException>(() => new SessionCopyService(fileSystem: backend).CopyAsync(new(source, target)));
        Check(!Directory.Exists(root) && backend.ActiveWriterCount == 0, "Memory copy touched disk or leaked a writer.");
    }
    private static async Task MemoryCatalog()
    {
        var root = VirtualDirectory(); var backend = new SessionStorageBackend(root, SessionStorageMode.InMemory);
        foreach (var name in new[] { "bad name.jsonl", "second.jsonl", "third.jsonl" })
            await using (var store = await SessionLogStore.CreateNewAsync(Path.Combine(root, name), Header("C:/FOREIGN/./cwd", parent: "never-open-missing-parent.jsonl"), Options(backend))) { }
        var catalog = new SessionCatalog([new("memory", root)], fileSystem: backend);
        var all = new List<SessionCatalogItem>(); string? cursor = null;
        do { var page = await catalog.ListAsync(new(1, cursor)); all.AddRange(page.Items); cursor = page.NextCursor; } while (cursor is not null);
        Equal(3, all.Count); Equal(3, all.Select(item => item.Key).Distinct().Count());
        Check(all.All(item => item.ParentSessionPath == "never-open-missing-parent.jsonl"), "Parent metadata was followed or lost.");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Fails<OperationCanceledException>(() => catalog.ListAsync(new(), canceled.Token));
        Check(!Directory.Exists(root) && backend.ActiveWriterCount == 0, "Memory listing acquired storage effects.");
    }
    private static async Task<SessionLogReadResult> Read(SessionStorageBackend backend, string path) =>
        await new SessionLogReader().ReadAsync(await backend.OpenReadAsync(path, default), leaveOpen: false);
    private static async Task<T> Fails<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new Exception("Expected " + typeof(T).Name); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}."); }
    private sealed class LocalDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pisharp-lazy-test-" + Guid.NewGuid().ToString("N"));
        public LocalDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
