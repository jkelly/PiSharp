using System.Text;
using System.Text.Json;
using PiSharp.Sessions.Lifecycle;

internal static class SessionCatalogTests
{
    private const string Time = "2026-10-02T00:00:00.000Z";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("session catalog separates store file and complete header identity without reading message bodies", IdentityAndHeaderOnly),
        ("session catalog paginates deterministic bounded headers and binds cursors to query and stores", Pagination),
        ("session catalog groups foreign Windows and Unix cwd lexically without resolving paths", CwdGroups),
        ("session catalog rejects malformed headers and unsafe names with bounded read diagnostics", InvalidHeadersAndBounds),
        ("session catalog resource limits and invalid queries never return partial success", LimitsAndQueries),
        ("session catalog cancellation waits for actual reader close before returning", CancellationJoins),
        ("session catalog reports cleanup faults after actual file stream close", CleanupFailure),
        ("session catalog rechecks exact opened header and does not follow parent metadata", HeaderChange)
    ];
    private static async Task IdentityAndHeaderOnly()
    {
        using var f = new Files(); var a = f.Directory("a"); var b = f.Directory("b");
        var body = new string('x', 2_000_000) + "not JSON or an image\n";
        var one = await f.Write(a, "same.jsonl", Header("same", "/work", "/missing/parent.jsonl") + "\n" + body);
        var two = await f.Write(b, "same.jsonl", Header("same", "/work", "/missing/parent.jsonl") + "\n" + body);
        var before = await File.ReadAllBytesAsync(one); var io = new CatalogReaders();
        var catalog = new SessionCatalog([new("a", a), new("b", b)], fileSystem: io);
        var page = await catalog.ListAsync(new()); Equal(2, page.Items.Length); Equal(0, page.SkippedFiles);
        Check(page.Items.Select(item => item.Key).Distinct().Count() == 2, "Same header ID/filename aliased separate stores.");
        Check(page.Items.All(item => item.SessionId == "same" && item.ParentSessionPath == "/missing/parent.jsonl"), "Header metadata changed.");
        Check(io.Readers.All(reader => reader.ReadBytes <= 512 && reader.Closed), "Discovery parsed the message body or retained a reader.");
        Check((await File.ReadAllBytesAsync(one)).SequenceEqual(before), "Listing rewrote imported bytes.");
        var selected = page.Items.Single(item => item.StoreId == "a"); Equal(selected, await catalog.FindAsync(selected.Key));
        Equal(two, page.Items.Single(item => item.StoreId == "b").Path);
    }
    private static async Task Pagination()
    {
        using var f = new Files(); var directory = f.Directory("store");
        for (var index = 0; index < 7; index++)
        {
            var file = await f.Write(directory, index + ".jsonl", Header("s" + index, "/work") + "\n");
            File.SetLastWriteTimeUtc(file, new DateTime(2026, 10, 2, 0, 0, index / 2, DateTimeKind.Utc));
        }
        var catalog = new SessionCatalog([new("s", directory)]); var keys = new List<string>(); string? cursor = null;
        do
        {
            var page = await catalog.ListAsync(new(2, cursor)); Check(page.Items.Length is > 0 and <= 2, "Page size changed.");
            keys.AddRange(page.Items.Select(item => item.Key)); cursor = page.NextCursor;
        } while (cursor is not null);
        Equal(7, keys.Count); Equal(7, keys.Distinct().Count());
        var all = await catalog.ListAsync(new(128)); Check(keys.SequenceEqual(all.Items.Select(item => item.Key)), "Cursor changed sort order.");
        var first = await catalog.ListAsync(new(2)); Check(first.NextCursor is not null, "No continuation cursor.");
        await Fails(() => catalog.ListAsync(new(2, first.NextCursor, "/work")), SessionCatalogFailure.InvalidQuery);
        await Fails(() => new SessionCatalog([new("other", directory)]).ListAsync(new(2, first.NextCursor)), SessionCatalogFailure.InvalidQuery);
        await f.Write(directory, "new.jsonl", Header("new", "/work") + "\n");
        Equal(8, (await catalog.ListAsync(new(128))).Items.Length); // Live rescan is explicit, rather than a stale snapshot.
    }
    private static async Task CwdGroups()
    {
        Equal(SessionCatalog.CwdGroup("c:\\Joe\\Work\\"), SessionCatalog.CwdGroup("C:/JOE/WORK"));
        Equal(SessionCatalog.CwdGroup("\\\\Server\\Share\\Repo\\"), SessionCatalog.CwdGroup("\\\\server\\share\\repo"));
        Equal("unix:/", SessionCatalog.CwdGroup("/"));
        Check(SessionCatalog.CwdGroup("/Work/") != SessionCatalog.CwdGroup("/work"), "Unix path case folded.");
        using var f = new Files(); var directory = f.Directory("s");
        await f.Write(directory, "one.jsonl", Header("one", "C:\\Joe\\Work\\") + "\n");
        await f.Write(directory, "two.jsonl", Header("two", "c:/joe/work") + "\n");
        await f.Write(directory, "three.jsonl", Header("three", "/Joe/Work") + "\n");
        var catalog = new SessionCatalog([new("s", directory)]);
        Equal(2, (await catalog.ListAsync(new(WorkingDirectory: "C:/JOE/WORK"))).Items.Length);
        Equal(1, (await catalog.ListAsync(new(WorkingDirectory: "/Joe/Work/"))).Items.Length);
    }
    private static async Task InvalidHeadersAndBounds()
    {
        using var f = new Files(); var directory = f.Directory("s");
        await f.Write(directory, "valid.jsonl", " \r\n\t\n" + Header("valid", "/work") + "\r\n{broken body");
        await f.Write(directory, "duplicate.jsonl", Header("dup", "/work").Replace("\"version\":3", "\"version\":3,\"version\":3"));
        await f.Write(directory, "large.jsonl", Header("large", new string('x', 2048)) + "\n");
        await f.Write(directory, "empty.jsonl", "");
        await f.Write(directory, "record.jsonl", "{\"type\":\"future\",\"id\":\"entry\"}\n");
        var invalid = f.Path(directory, "utf8.jsonl"); await File.WriteAllBytesAsync(invalid, [0xff, 0xfe, 0xff]);
        var io = new CatalogReaders { ExtraNames = ["../outside.jsonl", "unsafe\u001b.jsonl", "nested\\file.jsonl"] };
        var page = await new SessionCatalog([new("s", directory)], new(MaximumHeaderBytes: 512), io).ListAsync(new());
        Equal(1, page.Items.Length); Equal("valid", page.Items[0].SessionId); Equal(8, page.SkippedFiles);
        Check(io.Readers.All(reader => reader.ReadBytes <= 513 && reader.Closed), "Header bound read arbitrary body bytes.");
        Check(io.Opened.All(file => System.IO.Path.GetDirectoryName(file) == directory), "Unsafe catalog filename was opened.");
    }
    private static async Task LimitsAndQueries()
    {
        using var f = new Files(); var directory = f.Directory("s");
        await f.Write(directory, "a.jsonl", Header("a", "/work")); await f.Write(directory, "b.jsonl", Header("b", "/work"));
        var limited = new SessionCatalog([new("s", directory)], new(MaximumDirectoryEntries: 1));
        await Fails(() => limited.ListAsync(new()), SessionCatalogFailure.ResourceLimit);
        var catalog = new SessionCatalog([new("s", directory)]);
        foreach (var query in new[] { new SessionCatalogQuery(0), new(129), new(Cursor: "bad"), new(WorkingDirectory: "bad\ud800") })
            await Fails(() => catalog.ListAsync(query), SessionCatalogFailure.InvalidQuery);
        await Fails(() => catalog.FindAsync(new string('x', 64)), SessionCatalogFailure.InvalidQuery);
        await Fails(() => catalog.FindAsync(new string('a', 64)), SessionCatalogFailure.SessionNotFound);
        var unavailable = await new SessionCatalog([new("missing", System.IO.Path.Combine(f.Root, "not-created"))]).ListAsync(new());
        Equal(1, unavailable.UnavailableStores); Equal(0, unavailable.Items.Length);
        try { _ = new SessionCatalog([new("same", directory), new("same", f.Directory("other"))]); throw new Exception("Duplicate IDs accepted."); }
        catch (ArgumentException) { }
    }
    private static async Task CancellationJoins()
    {
        using var f = new Files(); var directory = f.Directory("s"); var file = await f.Write(directory, "a.jsonl", Header("a", "/work"));
        var io = new CatalogReaders { HoldRead = true, HoldClose = true }; using var cancellation = new CancellationTokenSource();
        var listing = new SessionCatalog([new("s", directory)], fileSystem: io).ListAsync(new(), cancellation.Token);
        await io.ReadEntered.Task.WaitAsync(Bound); cancellation.Cancel(); await io.CloseEntered.Task.WaitAsync(Bound);
        Check(!listing.IsCompleted && !io.Readers.Single().Closed, "Canceled listing escaped actual close.");
        io.ReleaseClose.TrySetResult();
        try { await listing.WaitAsync(Bound); throw new Exception("Canceled listing succeeded."); } catch (OperationCanceledException) { }
        Check(io.Readers.Single().Closed, "Reader survived cancellation.");
        using var exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    private static async Task CleanupFailure()
    {
        using var f = new Files(); var directory = f.Directory("s"); var file = await f.Write(directory, "a.jsonl", Header("a", "/work"));
        var io = new CatalogReaders { FailClose = true };
        await Fails(() => new SessionCatalog([new("s", directory)], fileSystem: io).ListAsync(new()), SessionCatalogFailure.CleanupFailed);
        Check(io.Readers.Single().Closed, "Synthetic close fault preceded actual stream close.");
        using var exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    private static async Task HeaderChange()
    {
        using var f = new Files(); var directory = f.Directory("s"); var file = await f.Write(directory, "a.jsonl", Header("a", "/work", "../../missing"));
        var catalog = new SessionCatalog([new("s", directory)]); var item = (await catalog.ListAsync(new())).Items.Single();
        var codec = new PiSharp.Sessions.Serialization.SessionEntryCodec(); catalog.ValidateOpenedHeader(item, codec.Parse(Header("a", "/work", "../../missing")));
        var changed = Header("a", "/work", "../../missing").Replace("\"version\":3", "\"version\":3,\"future\":1.00e400");
        await File.WriteAllTextAsync(file, changed);
        await Fails(() => catalog.FindAsync(item.Key), SessionCatalogFailure.SessionNotFound);
        try { catalog.ValidateOpenedHeader(item, codec.Parse(changed)); throw new Exception("Changed header accepted."); }
        catch (SessionCatalogException error) { Equal(SessionCatalogFailure.SessionChanged, error.Failure); }
    }
    private static string Header(string id, string cwd, string? parent = null) => JsonSerializer.Serialize(new
        { type = "session", version = 3, id, timestamp = Time, cwd, parentSession = parent });
    private static async Task Fails<T>(Func<Task<T>> action, SessionCatalogFailure expected)
    { try { await action(); throw new Exception("Expected catalog failure."); } catch (SessionCatalogException error) { Equal(expected, error.Failure); } }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Files : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pisharp-catalog-" + Guid.NewGuid().ToString("N"));
        private readonly List<string> files = [], directories = [];
        public Files() => System.IO.Directory.CreateDirectory(Root);
        public string Directory(string name) { var directory = System.IO.Path.Combine(Root, name); System.IO.Directory.CreateDirectory(directory); directories.Add(directory); return directory; }
        public string Path(string directory, string name) { var file = System.IO.Path.Combine(directory, name); files.Add(file); return file; }
        public async Task<string> Write(string directory, string name, string text) { var file = Path(directory, name); await File.WriteAllTextAsync(file, text, new UTF8Encoding(false, true)); return file; }
        public void Dispose()
        {
            foreach (var file in files.Distinct()) File.Delete(file);
            foreach (var directory in directories.Distinct().Reverse()) System.IO.Directory.Delete(directory);
            System.IO.Directory.Delete(Root);
        }
    }
    private sealed class CatalogReaders : ISessionCatalogFileSystem
    {
        public bool HoldRead, HoldClose, FailClose;
        public string[] ExtraNames = [];
        public readonly List<Reader> Readers = []; public readonly List<string> Opened = [];
        public readonly TaskCompletionSource ReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously),
            CloseEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), ReleaseClose = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsDirectoryLink(string directory) => (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0;
        public IEnumerable<string> EnumerateFileNames(string directory) => Directory.EnumerateFiles(directory).Select(System.IO.Path.GetFileName).Select(name => name!).Concat(ExtraNames);
        public SessionCatalogFileMetadata GetMetadata(string path) { var file = new FileInfo(path); return new(file.Length, file.LastWriteTimeUtc.Ticks, false); }
        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Opened.Add(path);
            var reader = new Reader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 512, FileOptions.Asynchronous), this);
            Readers.Add(reader); return ValueTask.FromResult<Stream>(reader);
        }
    }
    private sealed class Reader(FileStream inner, CatalogReaders owner) : Stream
    {
        public long ReadBytes; public bool Closed;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => inner.Length; public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            owner.ReadEntered.TrySetResult(); if (owner.HoldRead) await Task.Delay(Timeout.Infinite, token);
            var count = await inner.ReadAsync(buffer, token); ReadBytes += count; return count;
        }
        public override async ValueTask DisposeAsync()
        {
            owner.CloseEntered.TrySetResult(); if (owner.HoldClose) await owner.ReleaseClose.Task;
            await inner.DisposeAsync(); Closed = true;
            if (owner.FailClose) throw new IOException("authored private close cause");
            GC.SuppressFinalize(this);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
