// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/app/files.ts.
using PiSharp.Chord.Documents;
using PiSharp.Chord.State;
using PiSharp.Chord.Store;
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Host;

namespace PiSharp.Chord.Hosting.App;

/// <summary>Immutable file versions and resumable uploads, backed by a Chord store and a private files directory.</summary>
public sealed class FilesService : IAsyncDisposable
{
    private readonly FilesIndex index;
    private readonly Blobs blobs;
    private readonly Uploads uploads;
    private readonly FolderViews views;
    private readonly FilesCaps caps;
    private readonly TimeProvider clock;
    private readonly string run;
    private readonly string uploadsDirectory;
    private readonly object queueLock = new();
    private Task tail = Task.CompletedTask;
    private Task? closing;
    private bool closed;
    private ITimer? sweepTimer;
    public string DirectoryPath { get; }
    public string BlobsDirectory => blobs.DirectoryPath;
    public IKeyedHandle Summary => views.Summary;
    public long StoredBytes => index.Current.Bytes;
    private FilesService(ChordStore store, string directory, FilesCaps caps, int maxUploads, string? hostKey, TimeProvider clock)
    {
        if (caps.MaxFile < 0 || caps.MaxTotal < 0 || maxUploads < 1) throw new ArgumentOutOfRangeException(nameof(caps));
        this.caps = caps; this.clock = clock;
        DirectoryPath = Path.GetFullPath(directory);
        uploadsDirectory = Path.Combine(DirectoryPath, ".uploads");
        run = Path.Combine(uploadsDirectory, FilesValues.Token());
        index = new(store, hostKey);
        blobs = new(Path.Combine(DirectoryPath, "blobs"));
        uploads = new(run, maxUploads, clock);
        views = new(index);
    }
    public static async Task<FilesService> OpenAsync(ChordStore store, string directory, ChordContext context,
        FilesCaps? caps = null, int maxUploads = 64, string? hostKey = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        var service = new FilesService(store, directory, caps ?? new(), maxUploads, hostKey, timeProvider ?? TimeProvider.System);
        try
        {
            Directory.CreateDirectory(service.DirectoryPath); Blobs.RejectLink(service.DirectoryPath);
            Directory.CreateDirectory(service.uploadsDirectory); Blobs.RejectLink(service.uploadsDirectory);
            await service.index.InitializeAsync(context).ConfigureAwait(false);
            foreach (var previous in Directory.EnumerateDirectories(service.uploadsDirectory))
                Blobs.RemoveOwnedDirectory(previous, service.uploadsDirectory);
            Directory.CreateDirectory(service.run);
            service.blobs.Sweep(service.index.Current.Blobs);
            service.sweepTimer = service.clock.CreateTimer(_ => service.StartSweep(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            return service;
        }
        catch { service.views.Close(); service.index.Close(); throw; }
    }
    private void StartSweep()
    {
        lock (queueLock) if (closed) return;
        _ = SweepAsync().ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }
    public Task SweepAsync() => Turn(async () => { await uploads.ExpireAsync(TimeSpan.FromHours(1)).ConfigureAwait(false); return true; });
    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();
    private Task<T> Turn<T>(Func<Task<T>> work, bool ending = false)
    {
        lock (queueLock)
        {
            if (closed && !ending) return Task.FromException<T>(new ObjectDisposedException(nameof(FilesService)));
            var result = tail.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            tail = result.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return result;
        }
    }
    private void Room(long adding, long fileSize)
    {
        if (fileSize > caps.MaxFile) throw FilesValues.TooLarge($"A file exceeds the cap of {caps.MaxFile} bytes");
        if (adding > caps.MaxTotal - index.Current.Bytes - uploads.Size) throw FilesValues.TooLarge($"Files exceed the cap of {caps.MaxTotal} bytes");
    }
    private static void CheckPiece(int size)
    { if (size > FilesValues.MaxPiece) throw FilesValues.TooLarge("A call takes at most 4 MiB"); }
    public Task<FileInfo> PutAsync(FileWrite write, ReadOnlyMemory<byte> bytes, ChordContext context)
    {
        FilesValues.Write(write); CheckPiece(bytes.Length);
        var data = bytes.ToArray(); var owned = FilesValues.Clone(write);
        return Turn(async () =>
        {
            context.AbortSignal?.ThrowIfCancellationRequested();
            if (index.Current.Key(owned.Space, owned.Key) is { } known) return FilesValues.Clone(known);
            Room(data.Length, data.Length);
            var id = FilesValues.Token(); var temp = Path.Combine(run, "put-" + id);
            var admitted = false;
            try
            {
                await blobs.WriteAsync(id, data, temp).ConfigureAwait(false);
                return await index.ChangeAsync(s => s.Key(owned.Space, owned.Key) is { } found
                    ? FilesValues.Clone(found) : s.Add(owned, id, data.Length, Now), context, beforeWrites: () => admitted = true).ConfigureAwait(false);
            }
            catch { if (!admitted) blobs.Remove([id]); throw; }
            finally { File.Delete(temp); }
        });
    }
    public Task<BeginUpload> BeginAsync(FileWrite write, ChordContext context)
    {
        FilesValues.Write(write); var owned = FilesValues.Clone(write);
        return Turn<BeginUpload>(async () =>
        {
            context.AbortSignal?.ThrowIfCancellationRequested();
            if (index.Current.Key(write.Space, write.Key) is { } known) return new("exists", File: FilesValues.Clone(known));
            if (uploads.Find(owned) is { } underway) return new("upload", underway);
            if (index.Current.Spaces.GetValueOrDefault(owned.Space)?.Folders.Any(f => f.Path == owned.Path) == true)
                FilesValues.Conflict(owned.Space, owned.Path);
            return new("upload", await uploads.BeginAsync(owned).ConfigureAwait(false));
        });
    }
    public async Task AppendAsync(string upload, long offset, ReadOnlyMemory<byte> bytes, ChordContext context)
    {
        CheckPiece(bytes.Length); var owned = bytes.ToArray();
        var written = await Turn(() =>
        {
            context.AbortSignal?.ThrowIfCancellationRequested();
            return Task.FromResult(uploads.Accept(upload, offset, owned, Room));
        }).ConfigureAwait(false);
        try { await written.ConfigureAwait(false); }
        catch
        {
            try { await Turn(async () => { await uploads.CancelAsync(upload).ConfigureAwait(false); return true; }).ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
            throw;
        }
    }
    public async Task<long> UploadedAsync(string upload, ChordContext context)
    {
        var status = await Turn(() =>
        { context.AbortSignal?.ThrowIfCancellationRequested(); return Task.FromResult(uploads.Uploaded(upload)); }).ConfigureAwait(false);
        await status.Written.ConfigureAwait(false);
        return status.Size;
    }
    public Task<FileInfo> FinishAsync(string upload, ChordContext context) => Turn(async () =>
    {
        context.AbortSignal?.ThrowIfCancellationRequested();
        var id = FilesValues.Token(); blobs.Prepare();
        var finished = await uploads.FinishAsync(upload, blobs.PathOf(id)).ConfigureAwait(false);
        var admitted = false;
        try
        {
            if (index.Current.Key(finished.Data.Space, finished.Data.Key) is { } known)
            { blobs.Remove([id]); return FilesValues.Clone(known); }
            return await index.ChangeAsync(s => s.Add(finished.Data, id, finished.Size, Now), context,
                beforeWrites: () => admitted = true).ConfigureAwait(false);
        }
        catch { if (!admitted) blobs.Remove([id]); throw; }
    });
    public Task CancelAsync(string upload, ChordContext context) => Turn(async () =>
    { context.AbortSignal?.ThrowIfCancellationRequested(); await uploads.CancelAsync(upload).ConfigureAwait(false); return true; });
    public Task<byte[]> ReadAsync(string id, long offset, int length, ChordContext context)
    {
        lock (queueLock) ObjectDisposedException.ThrowIf(closed, this);
        context.AbortSignal?.ThrowIfCancellationRequested();
        if (offset < 0 || length < 0) throw FilesValues.Invalid("Invalid read range"); CheckPiece(length);
        var row = index.Current.Row(id) ?? throw FilesValues.Invalid($"No file {id}");
        return blobs.ReadAsync(row.Blob, row.Info.Size, offset, length);
    }
    public Task<FileInfo?> FileAsync(string id, ChordContext context) => Read(context, s => s.File(id) is { } file ? FilesValues.Clone(file) : null);
    public Task<FileInfo?> GetAsync(string space, string path, ChordContext context, long? version = null)
    {
        FilesValues.Space(space); FilesValues.Path(path);
        if (version < 1) throw FilesValues.Invalid("Version must be positive");
        return Read(context, s => s.Spaces.GetValueOrDefault(space)?.Files.Where(f => f.Info.Path == path && (version is null || f.Info.Version == version))
            .MaxBy(f => f.Info.Version)?.Info is { } file ? FilesValues.Clone(file) : null);
    }
    public Task<FileInfo?> ByKeyAsync(string space, string key, ChordContext context)
    {
        FilesValues.Space(space); if (string.IsNullOrEmpty(key)) throw FilesValues.Invalid("Key must not be empty");
        return Read(context, s => s.Key(space, key) is { } f ? FilesValues.Clone(f) : null);
    }
    public Task<FilesPage<FileInfo>> ByRefAsync(string space, string reference, ChordContext context, int limit = 100, string? after = null)
    {
        FilesValues.Space(space); if (string.IsNullOrEmpty(reference)) throw FilesValues.Invalid("Reference must not be empty");
        return Read(context, s => FilesSnapshot.Page((s.Spaces.GetValueOrDefault(space)?.Files ?? []).Where(f => f.Info.Ref == reference)
            .Select(f => FilesValues.Clone(f.Info)).OrderBy(f => f.Created).ThenBy(f => f.Id, StringComparer.Ordinal), limit, after, f => f.Id));
    }
    public Task<FilesPage<FileInfo>> VersionsAsync(string space, string path, ChordContext context, int limit = 100, string? after = null)
    {
        FilesValues.Space(space); FilesValues.Path(path);
        return Read(context, s => FilesSnapshot.Page((s.Spaces.GetValueOrDefault(space)?.Files ?? []).Where(f => f.Info.Path == path)
            .Select(f => FilesValues.Clone(f.Info)).OrderByDescending(f => f.Version), limit, after, f => f.Id));
    }
    public Task<FilesPage<FolderEntry>> ListAsync(string space, string folder, ChordContext context, int limit = 100, string? after = null)
    { FilesValues.Space(space); FilesValues.Path(folder, root: true); return Read(context, s => s.List(space, folder, limit, after)); }
    public IWatchable<JsonNode?> Folder(string space, string folder) => views.Folder(space, folder);
    public Task<FolderInfo> MkdirAsync(string space, string path, ChordContext context)
    { FilesValues.Space(space); FilesValues.Path(path); return Change(s => s.MakeFolders(space, path, Now), context); }
    public Task MoveAsync(string space, string from, string to, ChordContext context)
    {
        FilesValues.Space(space); FilesValues.Path(from); FilesValues.Path(to);
        return Change(s =>
        {
            var area = s.Space(space);
            if (FilesValues.Under(to, from) || area.Files.Any(f => f.Info.Path == to) || area.Folders.Any(f => f.Path == to)) FilesValues.Conflict(space, to);
            if (!area.Files.Any(f => f.Info.Path == from) && !area.Folders.Any(f => f.Path == from)) throw FilesValues.Invalid($"No path {from}");
            s.MakeFolders(space, FilesValues.Parent(to), Now);
            string Rename(string path) => to + path[from.Length..];
            // Old marks stay forever; target marks merge even when no version survives at that old path.
            foreach (var mark in area.Marks.Where(m => FilesValues.Under(m.Key, from)).ToArray())
                area.Marks[Rename(mark.Key)] = Math.Max(mark.Value, area.Marks.GetValueOrDefault(Rename(mark.Key)));
            for (var i = 0; i < area.Files.Count; i++)
                if (FilesValues.Under(area.Files[i].Info.Path, from)) area.Files[i] = area.Files[i] with { Info = area.Files[i].Info with { Path = Rename(area.Files[i].Info.Path) } };
            for (var i = 0; i < area.Folders.Count; i++)
                if (FilesValues.Under(area.Folders[i].Path, from)) area.Folders[i] = area.Folders[i] with { Path = Rename(area.Folders[i].Path) };
            return true;
        }, context);
    }
    public Task<FileInfo> CopyAsync(string id, string space, string path, ChordContext context)
    {
        FilesValues.Space(space); FilesValues.Path(path);
        return Change(s =>
        {
            var row = s.Row(id) ?? throw FilesValues.Invalid($"No file {id}"); var f = row.Info;
            return s.Add(new(space, path, f.Mime, f.Source, f.Ref, Meta: f.Meta), row.Blob, f.Size, Now);
        }, context);
    }
    public Task RemoveAsync(string id, ChordContext context) => Change(s =>
    { foreach (var area in s.Spaces.Values) area.Files.RemoveAll(f => f.Id == id); return true; }, context);
    public Task RemovePathAsync(string space, string path, ChordContext context)
    {
        FilesValues.Space(space); FilesValues.Path(path);
        return Change(s =>
        {
            if (s.Spaces.TryGetValue(space, out var area))
            { area.Files.RemoveAll(f => FilesValues.Under(f.Info.Path, path)); area.Folders.RemoveAll(f => FilesValues.Under(f.Path, path)); }
            return true;
        }, context);
    }
    public Task RemoveSpaceAsync(string space, ChordContext context)
    {
        FilesValues.Space(space);
        return Change(s =>
        {
            // Keep high-water marks across removal and later recreation of the same space.
            if (s.Spaces.TryGetValue(space, out var area)) { area.Files.Clear(); area.Folders.Clear(); area.SummaryPresent = false; }
            return true;
        }, context);
    }
    private Task<T> Read<T>(ChordContext context, Func<FilesSnapshot, T> read) => Turn(() =>
    { context.AbortSignal?.ThrowIfCancellationRequested(); return Task.FromResult(read(index.Current)); });
    private Task<T> Change<T>(Func<FilesSnapshot, T> mutation, ChordContext context) => Turn(async () =>
    {
        var before = index.Current.Blobs.Keys.ToArray();
        var result = await index.ChangeAsync(mutation, context).ConfigureAwait(false);
        blobs.Remove(before.Where(id => !index.Current.Blobs.ContainsKey(id)));
        return result;
    });
    public Task<IReadOnlyList<FilesBlob>> SnapshotBlobsAsync(ChordContext context) => Read<IReadOnlyList<FilesBlob>>(context,
        s => s.Blobs.Select(b => new FilesBlob(b.Key, blobs.PathOf(b.Key), b.Value.Size)).ToArray());
    public ValueTask DisposeAsync()
    {
        lock (queueLock)
        {
            if (closing is not null) return new(closing);
            closed = true; sweepTimer?.Dispose();
            closing = Turn(async () =>
            {
                try { await uploads.CloseAsync().ConfigureAwait(false); Blobs.RemoveOwnedDirectory(run, uploadsDirectory); }
                finally { views.Close(); index.Close(); }
                return true;
            }, ending: true);
            return new(closing);
        }
    }
}

/// <summary>The app-kit Files and FilesFolder contracts and their serving facet.</summary>
public static class FilesFacet
{
    private static JsonNode FileSchema => Schemas.Obj(("id", Schemas.Str()), ("space", Schemas.Str()), ("path", Schemas.Str()),
        ("version", Schemas.Int()), ("size", Schemas.Int()), ("mime", Schemas.Str()), ("source", Schemas.Str()),
        ("created", Schemas.Int()), ("ref", Schemas.Opt(Schemas.Str())), ("key", Schemas.Opt(Schemas.Str())), ("meta", Schemas.Opt(Schemas.Unknown())));
    private static JsonNode FolderSchema => Schemas.Obj(("space", Schemas.Str()), ("path", Schemas.Str()), ("created", Schemas.Int()));
    private static JsonNode EntrySchema => Schemas.Union(
        Schemas.Obj(("kind", Schemas.Lit("folder")), ("folder", FolderSchema)),
        Schemas.Obj(("kind", Schemas.Lit("file")), ("file", FileSchema)));
    private static JsonNode SummarySchema => Schemas.Obj(("files", Schemas.Int()), ("versions", Schemas.Int()),
        ("bytes", Schemas.Int()), ("newest", Schemas.Arr(FileSchema)));
    private static JsonNode WriteSchema(bool bytes = false)
    {
        var fields = new List<(string, Property)>
        {
            ("space", Schemas.Str()), ("path", Schemas.Str()), ("mime", Schemas.Str()), ("source", Schemas.Str()),
            ("ref", Schemas.Opt(Schemas.Str(min: 1))), ("key", Schemas.Opt(Schemas.Str(min: 1))),
            ("meta", Schemas.Opt(Schemas.Unknown())), ("created", Schemas.Opt(Schemas.Int(min: 0))),
        };
        if (bytes) fields.Add(("bytes", Schemas.Bytes()));
        return Schemas.Obj([.. fields]);
    }
    private static JsonNode PageSchema(JsonNode item) => Schemas.Obj(("items", Schemas.Arr(item)), ("next", Schemas.Opt(Schemas.Str())));
    private static JsonNode PageArgs(params (string, Property)[] first) => Schemas.Obj([.. first,
        ("after", Schemas.Opt(Schemas.Str())), ("limit", Schemas.Opt(Schemas.Int(min: 1, max: 1000)))]);
    public static Contract Files { get; } = Contract.Local("chord.app.files", new()
    {
        Version = 2, Params = [], Members = new()
        {
            ["summary"] = Schemas.Keyed(SummarySchema),
            ["put"] = Schemas.Method(WriteSchema(true), FileSchema),
            ["begin"] = Schemas.Method(WriteSchema(), Schemas.Union(
                Schemas.Obj(("kind", Schemas.Lit("upload")), ("upload", Schemas.Str())),
                Schemas.Obj(("kind", Schemas.Lit("exists")), ("file", FileSchema)))),
            ["append"] = Schemas.Method(Schemas.Obj(("upload", Schemas.Str()), ("offset", Schemas.Int(min: 0)), ("bytes", Schemas.Bytes()))),
            ["uploaded"] = Schemas.Method(Schemas.Str(), Schemas.Int()),
            ["finish"] = Schemas.Method(Schemas.Str(), FileSchema), ["cancel"] = Schemas.Method(Schemas.Str()),
            ["read"] = Schemas.Method(Schemas.Obj(("id", Schemas.Str()), ("offset", Schemas.Int(min: 0)), ("length", Schemas.Int(min: 0))), Schemas.Bytes()),
            ["file"] = Schemas.Method(Schemas.Str(), Schemas.Union(FileSchema, Schemas.Nul())),
            ["get"] = Schemas.Method(Schemas.Obj(("space", Schemas.Str()), ("path", Schemas.Str()), ("version", Schemas.Opt(Schemas.Int(min: 1)))), Schemas.Union(FileSchema, Schemas.Nul())),
            ["byKey"] = Schemas.Method(Schemas.Obj(("space", Schemas.Str()), ("key", Schemas.Str(min: 1))), Schemas.Union(FileSchema, Schemas.Nul())),
            ["byRef"] = Schemas.Method(PageArgs(("space", Schemas.Str()), ("ref", Schemas.Str(min: 1))), PageSchema(FileSchema)),
            ["versions"] = Schemas.Method(PageArgs(("space", Schemas.Str()), ("path", Schemas.Str())), PageSchema(FileSchema)),
            ["list"] = Schemas.Method(PageArgs(("space", Schemas.Str()), ("folder", Schemas.Str())), PageSchema(EntrySchema)),
            ["mkdir"] = Schemas.Method(Schemas.Obj(("space", Schemas.Str()), ("path", Schemas.Str())), FolderSchema),
            ["move"] = Schemas.Method(Schemas.Obj(("space", Schemas.Str()), ("from", Schemas.Str()), ("to", Schemas.Str()))),
            ["copy"] = Schemas.Method(Schemas.Obj(("id", Schemas.Str()), ("space", Schemas.Str()), ("path", Schemas.Str())), FileSchema),
            ["remove"] = Schemas.Method(Schemas.Str()), ["removePath"] = Schemas.Method(Schemas.Obj(("space", Schemas.Str()), ("path", Schemas.Str()))),
            ["removeSpace"] = Schemas.Method(Schemas.Str()),
        },
    });
    public static Contract FilesFolder { get; } = Contract.Local("chord.app.files.folder", new()
    {
        Version = 1, Params = new() { ["space"] = Schemas.Str(), ["folder"] = Schemas.Str() },
        Members = new() { ["listing"] = Schemas.Obj(("entries", Schemas.Arr(EntrySchema)), ("more", Schemas.Bool())) },
    });
    public static Facet Create(FilesService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return Facet.Define((host, _) =>
        {
            host.Serve(Files, (_, _, _) => new(new Dictionary<string, object?>
            {
                ["summary"] = service.Summary,
                ["put"] = JsonMethod(async (a, c) => await service.PutAsync(Write(a), Bytes(a["bytes"]), c).ConfigureAwait(false)),
                ["begin"] = JsonMethod(async (a, c) => await service.BeginAsync(Write(a), c).ConfigureAwait(false)),
                ["append"] = (HostMethod)(async (a, c) => { var j = Obj(a); await service.AppendAsync(Text(j["upload"]), Number(j["offset"]), Bytes(j["bytes"]), c.Context).ConfigureAwait(false); return null; }),
                ["uploaded"] = (HostMethod)(async (a, c) => JsonValue.Create(await service.UploadedAsync(Text(a as JsonNode), c.Context).ConfigureAwait(false))),
                ["finish"] = (HostMethod)(async (a, c) => FilesValues.Node(await service.FinishAsync(Text(a as JsonNode), c.Context).ConfigureAwait(false))),
                ["cancel"] = (HostMethod)(async (a, c) => { await service.CancelAsync(Text(a as JsonNode), c.Context).ConfigureAwait(false); return null; }),
                ["read"] = (HostMethod)(async (a, c) => { var j = Obj(a); return JsonValue.Create(await service.ReadAsync(Text(j["id"]), Number(j["offset"]), Length(j["length"]), c.Context).ConfigureAwait(false)); }),
                ["file"] = (HostMethod)(async (a, c) => FilesValues.Node(await service.FileAsync(Text(a as JsonNode), c.Context).ConfigureAwait(false))),
                ["get"] = JsonMethod(async (a, c) => await service.GetAsync(Text(a["space"]), Text(a["path"]), c, a["version"] is null ? null : Number(a["version"])).ConfigureAwait(false)),
                ["byKey"] = JsonMethod(async (a, c) => await service.ByKeyAsync(Text(a["space"]), Text(a["key"]), c).ConfigureAwait(false)),
                ["byRef"] = JsonMethod(async (a, c) => await service.ByRefAsync(Text(a["space"]), Text(a["ref"]), c, Limit(a), OptionalText(a["after"])).ConfigureAwait(false)),
                ["versions"] = JsonMethod(async (a, c) => await service.VersionsAsync(Text(a["space"]), Text(a["path"]), c, Limit(a), OptionalText(a["after"])).ConfigureAwait(false)),
                ["list"] = JsonMethod(async (a, c) => await service.ListAsync(Text(a["space"]), Text(a["folder"]), c, Limit(a), OptionalText(a["after"])).ConfigureAwait(false)),
                ["mkdir"] = JsonMethod(async (a, c) => await service.MkdirAsync(Text(a["space"]), Text(a["path"]), c).ConfigureAwait(false)),
                ["move"] = JsonMethod(async (a, c) => { await service.MoveAsync(Text(a["space"]), Text(a["from"]), Text(a["to"]), c).ConfigureAwait(false); return null; }),
                ["copy"] = JsonMethod(async (a, c) => await service.CopyAsync(Text(a["id"]), Text(a["space"]), Text(a["path"]), c).ConfigureAwait(false)),
                ["remove"] = (HostMethod)(async (a, c) => { await service.RemoveAsync(Text(a as JsonNode), c.Context).ConfigureAwait(false); return null; }),
                ["removePath"] = JsonMethod(async (a, c) => { await service.RemovePathAsync(Text(a["space"]), Text(a["path"]), c).ConfigureAwait(false); return null; }),
                ["removeSpace"] = (HostMethod)(async (a, c) => { await service.RemoveSpaceAsync(Text(a as JsonNode), c.Context).ConfigureAwait(false); return null; }),
            }));
            host.Serve(FilesFolder, (parameters, _, _) => new(new Dictionary<string, object?>
            { ["listing"] = service.Folder(Text(parameters["space"]), Text(parameters["folder"])) }));
        });
    }
    private static HostMethod JsonMethod(Func<JsonObject, ChordContext, Task<object?>> method) =>
        async (args, context) => FilesValues.Node(await method(Obj(args), context.Context).ConfigureAwait(false));
    private static JsonObject Obj(object? value) => value as JsonObject ?? throw FilesValues.Invalid("Expected an object");
    private static string Text(JsonNode? value) => ChordJson.AsString(value) ?? throw FilesValues.Invalid("Expected a string");
    private static string? OptionalText(JsonNode? value) => value is null ? null : Text(value);
    private static long Number(JsonNode? value) => value is not null ? checked((long)(ChordJson.AsNumber(value) ?? throw FilesValues.Invalid("Expected an integer"))) : throw FilesValues.Invalid("Expected an integer");
    private static int Limit(JsonObject value) => value["limit"] is null ? 100 : checked((int)Number(value["limit"]));
    private static int Length(JsonNode? value)
    {
        var length = Number(value);
        if (length > FilesValues.MaxPiece) throw FilesValues.TooLarge("A read takes at most 4 MiB");
        return checked((int)length);
    }
    private static ReadOnlyMemory<byte> Bytes(JsonNode? value) => value is JsonValue j && j.TryGetValue<byte[]>(out var data) ? data :
        value is JsonValue m && m.TryGetValue<ReadOnlyMemory<byte>>(out var memory) ? memory : throw FilesValues.Invalid("Expected bytes");
    private static FileWrite Write(JsonObject value) => new(Text(value["space"]), Text(value["path"]), Text(value["mime"]), Text(value["source"]),
        OptionalText(value["ref"]), OptionalText(value["key"]), value["meta"]?.DeepClone(), value["created"] is null ? null : Number(value["created"]));
}
