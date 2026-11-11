// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/app/uploads.ts.
namespace PiSharp.Chord.Hosting.App;

// All methods which accept or remove an upload run on the FilesService FIFO. Only the actual file writes run
// outside it. Each accepted append owns its copied bytes and a place on this upload's write chain.
internal sealed class Uploads(string run, int maximum, TimeProvider clock)
{
    private readonly Dictionary<string, Upload> entries = new(StringComparer.Ordinal);
    internal long Size => entries.Values.Sum(e => e.Size);
    internal string? Find(FileWrite data) => data.Key is null ? null : entries.FirstOrDefault(e =>
        e.Value.Data.Space == data.Space && e.Value.Data.Key == data.Key).Key;
    internal async Task<string> BeginAsync(FileWrite data)
    {
        if (entries.Count >= maximum) throw new PiSharp.Chord.Data.ChordError("unavailable", $"files: {maximum} uploads are under way", "busy");
        Directory.CreateDirectory(run);
        Blobs.RejectLink(run);
        var id = FilesValues.Token();
        var stream = new FileStream(Path.Combine(run, id), FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        entries.Add(id, new(data, stream, clock.GetUtcNow()));
        await Task.CompletedTask;
        return id;
    }
    internal Task Accept(string id, long offset, byte[] bytes, Action<long, long> room)
    {
        var entry = Get(id);
        if (entry.Failure is { } failure) throw FilesValues.Invalid($"Upload {id} failed: {failure.Message}");
        if (offset != entry.Size) throw FilesValues.Invalid($"Upload {id} is at offset {entry.Size}", new System.Text.Json.Nodes.JsonObject { ["offset"] = entry.Size });
        room(bytes.Length, checked(entry.Size + bytes.Length));
        entry.Size += bytes.Length;
        entry.Touched = clock.GetUtcNow();
        var owned = bytes.ToArray();
        var before = entry.Tail;
        return entry.Tail = WriteAsync();
        async Task WriteAsync()
        {
            try
            {
                await before.ConfigureAwait(false);
                await entry.Stream.WriteAsync(owned).ConfigureAwait(false);
            }
            catch (Exception error) { entry.Failure = error; throw; }
        }
    }
    internal (Task Written, long Size) Uploaded(string id)
    {
        var entry = Get(id);
        return (entry.Tail, entry.Size);
    }
    internal async Task<(FileWrite Data, long Size)> FinishAsync(string id, string destination)
    {
        var entry = Get(id);
        entries.Remove(id);
        try
        {
            await entry.Tail.ConfigureAwait(false);
            await entry.Stream.FlushAsync().ConfigureAwait(false);
            entry.Stream.Flush(true);
            await entry.Stream.DisposeAsync().ConfigureAwait(false);
            File.Move(Path.Combine(run, id), destination);
            return (entry.Data, entry.Size);
        }
        catch
        {
            await entry.Stream.DisposeAsync().ConfigureAwait(false);
            File.Delete(Path.Combine(run, id));
            throw;
        }
    }
    internal async Task CancelAsync(string id)
    {
        if (!entries.Remove(id, out var entry)) return;
        try { await entry.Tail.ConfigureAwait(false); } catch { /* The write's caller sees its original failure. */ }
        await entry.Stream.DisposeAsync().ConfigureAwait(false);
        File.Delete(Path.Combine(run, id));
    }
    internal async Task ExpireAsync(TimeSpan idle)
    {
        foreach (var id in entries.Where(e => e.Value.Failure is not null || clock.GetUtcNow() - e.Value.Touched >= idle).Select(e => e.Key).ToArray())
            await CancelAsync(id).ConfigureAwait(false);
    }
    internal async Task CloseAsync()
    {
        foreach (var id in entries.Keys.ToArray()) await CancelAsync(id).ConfigureAwait(false);
    }
    private Upload Get(string id) => entries.TryGetValue(id, out var entry) ? entry : throw FilesValues.Invalid($"No upload {id}");
    private sealed class Upload(FileWrite data, FileStream stream, DateTimeOffset touched)
    {
        internal FileWrite Data { get; } = data;
        internal FileStream Stream { get; } = stream;
        internal long Size;
        internal DateTimeOffset Touched = touched;
        internal Task Tail = Task.CompletedTask;
        internal Exception? Failure;
    }
}
