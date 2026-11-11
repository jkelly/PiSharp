// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/app/files-index.ts.
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using PiSharp.Chord.Sqlite;
using PiSharp.Chord.Store;
using PiSharp.Chord.State;

namespace PiSharp.Chord.Hosting.App;

// The native adapter stores a single versioned document. The file rows, high-water marks, blob reference counts,
// usage and summaries therefore share the store's transaction and outbox sequence, including after a crash.
internal sealed class FilesIndex : IClaimant
{
    internal const string Owner = "chord.app.files";
    internal const string Fingerprint = "chord.app.files.index.v1";
    private readonly ChordStore store;
    private readonly DocumentAddress address;
    private readonly Batch standalone = new(_ => { }, null);
    internal Batch PublicationBatch => store.Batch ?? standalone;
    internal FilesSnapshot Current { get; private set; } = new();
    public string Label => Owner;
    internal event Action<FilesSnapshot, ChordContext>? Changed;
    internal FilesIndex(ChordStore store, string? hostKey)
    {
        this.store = store;
        address = Address(hostKey);
        store.Claims.Documents(Owner, Owner, this);
    }
    internal static DocumentAddress Address(string? hostKey) => new(Owner, Owner, "index", hostKey, hostKey);
    internal Task InitializeAsync(ChordContext context) => LoadAsync(context);
    private async Task LoadAsync(ChordContext context) => Current = (await ReadAsync(store, address, context).ConfigureAwait(false)).Snapshot;
    internal void Close() => store.Claims.Release(this);
    internal static Task<(DocumentRecord? Record, FilesSnapshot Snapshot)> ReadAsync(ChordStore store, DocumentAddress address, ChordContext context) =>
        store.ReadTransactionAsync(executor => ReadAsync(executor, address), context);
    private static async Task<(DocumentRecord? Record, FilesSnapshot Snapshot)> ReadAsync(ISqliteExecutor executor, DocumentAddress address)
    {
        var record = await DocumentEngine.FindDocumentAsync(executor, address).ConfigureAwait(false);
        if (record is null) return (null, new FilesSnapshot());
        if (record.Fingerprint != Fingerprint || record.Version != 1 || record.Host != address.Host)
            throw FilesValues.Invalid("Unsupported or incorrectly owned index document");
        var loaded = await DocumentEngine.LoadDocumentAsync(executor, record).ConfigureAwait(false);
        var snapshot = FilesValues.Decode<FilesSnapshot>(loaded.Value);
        snapshot.Validate();
        snapshot.Recalculate();
        return (record, snapshot);
    }
    internal async Task<T> ChangeAsync<T>(Func<FilesSnapshot, T> change, ChordContext context, bool requireAbsent = false, Action? beforeWrites = null)
    {
        var commit = new IndexCommit(address, change, requireAbsent, beforeWrites);
        var outcome = await store.CommitSeriesAsync([commit], (_, _, _) =>
        {
            void Adopt()
            {
                Current = commit.Snapshot!;
                Changed?.Invoke(Current, context);
            }
            if (store.Batch is { } batch) batch.Within(Adopt, context); else Adopt();
        }, context).ConfigureAwait(false);
        if (outcome.Refused is { } refused) ExceptionDispatchInfo.Capture(refused).Throw();
        return (T)commit.Result!;
    }
    private sealed class IndexCommit(DocumentAddress address, Delegate change, bool requireAbsent, Action? beforeWrites) : ISeriesCommit
    {
        public FilesSnapshot? Snapshot { get; private set; }
        public object? Result { get; private set; }
        private IStagedWrite[] writes = [new DeferredWrite()];
        public IReadOnlyList<IStagedWrite> Writes => writes;
        public async Task CheckAsync(Step step)
        {
            var (record, snapshot) = await ReadAsync(step, address).ConfigureAwait(false);
            if (requireAbsent && record is not null) throw FilesValues.Invalid("Target files index already exists");
            try { Result = change.DynamicInvoke(snapshot); }
            catch (System.Reflection.TargetInvocationException e) when (e.InnerException is not null)
            { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); }
            snapshot.Recalculate();
            Snapshot = snapshot;
            var value = FilesValues.Node(snapshot)!;
            var text = value.ToJsonString(FilesValues.Json);
            writes = [record is null
                ? StoreInternal.CreateDocument(address, new DocumentBase(1, Fingerprint, value), text)
                : StoreInternal.ChangeDocument(record.Id, record.Revision, new DocumentContent.Base(1, Fingerprint, value), text)];
            // From this point a backend/adoption exception may describe a COMMIT that did reach disk. Callers
            // retain installed bytes unless this read-only preparation never completed.
            beforeWrites?.Invoke();
        }
        private sealed class DeferredWrite : IStagedWrite
        {
            public WriteOrder Order => WriteOrder.Write;
            public Task<IReadOnlyList<StoreWrite>> ApplyAsync(Step step) => throw new InvalidOperationException("Unprepared files commit");
        }
    }
}

internal sealed class FilesSnapshot
{
    public Dictionary<string, FilesSpace> Spaces { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, BlobUse> Blobs { get; set; } = new(StringComparer.Ordinal);
    public long Bytes { get; set; }
    internal void Validate()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, space) in Spaces)
        {
            FilesValues.Space(name);
            var folders = new HashSet<string>(StringComparer.Ordinal);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var versions = new HashSet<(string, long)>();
            foreach (var folder in space.Folders)
            {
                FilesValues.Path(folder.Path);
                if (folder.Space != name || folder.Created < 0 || !folders.Add(folder.Path)) throw FilesValues.Invalid("Invalid stored folder");
            }
            foreach (var (path, mark) in space.Marks)
            {
                FilesValues.Path(path);
                if (mark is < 1 or > 9_007_199_254_740_991L) throw FilesValues.Invalid("Invalid stored path high-water mark");
            }
            foreach (var row in space.Files)
            {
                var file = row.Info;
                FilesValues.Write(new(file.Space, file.Path, file.Mime, file.Source, file.Ref, file.Key, file.Meta, file.Created));
                if (file.Space != name || file.Version < 1 || file.Size < 0 || file.Size > 9_007_199_254_740_991L || !ids.Add(file.Id) ||
                    !versions.Add((file.Path, file.Version)) || folders.Contains(file.Path) ||
                    (file.Key is not null && !keys.Add(file.Key)) || space.Marks.GetValueOrDefault(file.Path) < file.Version ||
                    row.Blob.Length != 36 || row.Blob.Any(c => !char.IsAsciiHexDigitLower(c)))
                    throw FilesValues.Invalid("Invalid stored file version");
            }
        }
        foreach (var group in Spaces.Values.SelectMany(s => s.Files).GroupBy(f => f.Blob))
            if (group.Select(f => f.Info.Size).Distinct().Skip(1).Any()) throw FilesValues.Invalid("Inconsistent stored blob size");
    }
    internal FilesSpace Space(string name)
    {
        if (!Spaces.TryGetValue(name, out var result)) Spaces[name] = result = new();
        return result;
    }
    internal FileInfo? File(string id) => Spaces.Values.SelectMany(s => s.Files).FirstOrDefault(f => f.Id == id)?.Info;
    internal FileRow? Row(string id) => Spaces.Values.SelectMany(s => s.Files).FirstOrDefault(f => f.Id == id);
    internal FileInfo? Key(string space, string? key) => key is null ? null :
        Spaces.GetValueOrDefault(space)?.Files.FirstOrDefault(f => f.Info.Key == key)?.Info;
    internal void Recalculate()
    {
        Blobs.Clear();
        foreach (var space in Spaces.Values)
        {
            var rows = space.Files;
            space.Summary = new(rows.Select(f => f.Info.Path).Distinct(StringComparer.Ordinal).LongCount(), rows.Count,
                rows.Sum(f => f.Info.Size), rows.Select(f => FilesValues.Clone(f.Info)).OrderByDescending(f => f.Created)
                .ThenByDescending(f => f.Id, StringComparer.Ordinal).Take(20).ToArray());
            foreach (var row in rows)
            {
                if (Blobs.TryGetValue(row.Blob, out var blob)) Blobs[row.Blob] = blob with { Refs = blob.Refs + 1 };
                else Blobs[row.Blob] = new(row.Info.Size, 1);
            }
        }
        Bytes = Blobs.Values.Sum(b => b.Size);
    }
    internal FileInfo Add(FileWrite write, string blob, long size, long now)
    {
        var space = Space(write.Space);
        space.SummaryPresent = true;
        if (space.Folders.Any(f => f.Path == write.Path)) FilesValues.Conflict(write.Space, write.Path);
        MakeFolders(write.Space, FilesValues.Parent(write.Path), now);
        var version = checked(Math.Max(space.Marks.GetValueOrDefault(write.Path),
            space.Files.Where(f => f.Info.Path == write.Path).Select(f => f.Info.Version).DefaultIfEmpty().Max()) + 1);
        var info = new FileInfo(FilesValues.Token(), write.Space, write.Path, version, size, write.Mime, write.Source,
            write.Created ?? now, write.Ref, write.Key, write.Meta?.DeepClone());
        space.Files.Add(new(info, blob));
        space.Marks[write.Path] = version;
        return FilesValues.Clone(info);
    }
    internal FolderInfo MakeFolders(string name, string path, long now)
    {
        var space = Space(name);
        if (path == "") return new(name, "", now);
        var segments = path.Split('/');
        for (var i = 1; i <= segments.Length; i++)
        {
            var folder = string.Join('/', segments.Take(i));
            if (space.Files.Any(f => f.Info.Path == folder)) FilesValues.Conflict(name, folder);
            if (!space.Folders.Any(f => f.Path == folder)) space.Folders.Add(new(name, folder, now));
        }
        return space.Folders.First(f => f.Path == path);
    }
    internal static FilesPage<T> Page<T>(IEnumerable<T> source, int limit, string? after, Func<T, string> key)
    {
        if (limit is < 1 or > 1000) throw FilesValues.Invalid("Page size must be from 1 to 1000");
        var items = source.ToArray();
        var start = 0;
        if (after is not null)
        {
            string decoded;
            try { decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(after)); }
            catch (FormatException) { throw FilesValues.Invalid("Invalid page cursor"); }
            start = Array.FindIndex(items, item => key(item) == decoded) + 1;
            if (start == 0) throw FilesValues.Invalid("Page cursor is no longer present");
        }
        var page = items.Skip(start).Take(limit).ToArray();
        return new(page, start + page.Length < items.Length
            ? Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(key(page[^1]))) : null);
    }
    internal FilesPage<FolderEntry> List(string space, string folder, int limit, string? after)
    {
        if (!Spaces.TryGetValue(space, out var value)) return new([]);
        var entries = value.Folders.Where(f => FilesValues.Parent(f.Path) == folder).OrderBy(f => f.Path, StringComparer.Ordinal)
            .Select(f => new FolderEntry("folder", f))
            .Concat(value.Files.Where(f => FilesValues.Parent(f.Info.Path) == folder).GroupBy(f => f.Info.Path)
                .Select(g => g.MaxBy(f => f.Info.Version)!).OrderBy(f => f.Info.Path, StringComparer.Ordinal)
                .Select(f => new FolderEntry("file", File: FilesValues.Clone(f.Info))));
        return Page(entries, limit, after, e => e.Kind + ":" + (e.Folder?.Path ?? e.File!.Path));
    }
}
internal sealed class FilesSpace
{
    public bool SummaryPresent { get; set; }
    public List<FileRow> Files { get; set; } = [];
    public List<FolderInfo> Folders { get; set; } = [];
    public Dictionary<string, long> Marks { get; set; } = new(StringComparer.Ordinal);
    public SpaceSummary Summary { get; set; } = new(0, 0, 0, []);
}
internal sealed record FileRow(FileInfo Info, string Blob)
{
    public string Id => Info.Id;
}
internal sealed record BlobUse(long Size, long Refs);
