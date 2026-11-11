// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/app/folder-views.ts.
using System.Text.Json.Nodes;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Delta;
using PiSharp.Chord.State;

namespace PiSharp.Chord.Hosting.App;

internal sealed class FolderViews
{
    private readonly FilesIndex index;
    private readonly Batch batch;
    private readonly Dictionary<(string Space, string Folder), FilesSource> folders = [];
    private readonly Dictionary<string, FilesSource> summaries = new(StringComparer.Ordinal);
    private readonly object sync = new();
    internal FolderViews(FilesIndex index)
    {
        this.index = index;
        batch = index.PublicationBatch;
        Summary = new SummaryHandle(this);
        index.Changed += Changed;
    }
    internal IKeyedHandle Summary { get; }
    internal IWatchable<JsonNode?> Folder(string space, string folder)
    {
        FilesValues.Space(space); FilesValues.Path(folder, root: true);
        lock (sync)
        {
            var key = (space, folder);
            if (!folders.TryGetValue(key, out var source)) folders[key] = source = new(Listing(index.Current, space, folder), batch);
            return source;
        }
    }
    private FilesSource SummaryAt(string space)
    {
        FilesValues.Space(space);
        lock (sync)
        {
            if (!summaries.TryGetValue(space, out var source))
                summaries[space] = source = new(FilesValues.Node(index.Current.Spaces.GetValueOrDefault(space)?.Summary ?? new(0, 0, 0, [])), batch);
            return source;
        }
    }
    private static JsonNode? Listing(FilesSnapshot snapshot, string space, string folder)
    {
        var page = snapshot.List(space, folder, 500, null);
        return FilesValues.Node(new FolderListing(page.Items, page.Next is not null));
    }
    private void Changed(FilesSnapshot snapshot, ChordContext context)
    {
        batch.Within(() => { lock (sync)
        {
            foreach (var (key, source) in folders) source.Set(Listing(snapshot, key.Space, key.Folder), context);
            foreach (var (space, source) in summaries)
                source.Set(FilesValues.Node(snapshot.Spaces.GetValueOrDefault(space)?.Summary ?? new(0, 0, 0, [])), context);
        } }, context);
    }
    internal void Close()
    {
        index.Changed -= Changed;
        lock (sync)
        {
            foreach (var source in folders.Values.Concat(summaries.Values)) source.Close();
            folders.Clear(); summaries.Clear();
        }
    }
    private sealed class SummaryHandle(FolderViews views) : IKeyedHandle
    {
        public Task<JsonObject?> GetAsync(string key, ChordContext context)
        {
            context.AbortSignal?.ThrowIfCancellationRequested(); FilesValues.Space(key);
            var area = views.index.Current.Spaces.GetValueOrDefault(key);
            return Task.FromResult(area?.SummaryPresent == true ? FilesValues.Node(area.Summary)?.AsObject() : null);
        }
        public async IAsyncEnumerable<string> Keys(ChordContext context)
        {
            foreach (var key in views.index.Current.Spaces.Where(e => e.Value.SummaryPresent).Select(e => e.Key).Order(StringComparer.Ordinal))
            { context.AbortSignal?.ThrowIfCancellationRequested(); yield return key; }
            await Task.CompletedTask;
        }
        public async Task<IWatchHandle<JsonNode?>?> WatchAsync(string key, ChordContext context) =>
            await views.SummaryAt(key).WatchAsync(context).ConfigureAwait(false);
        public IWatchable<JsonNode?> At(string key) => views.SummaryAt(key);
        public Task<TResult> ChangeAsync<TResult>(string key, Func<JsonObject, TResult> change, ChordContext context) =>
            Task.FromException<TResult>(new InvalidOperationException("File summaries are read-only"));
        public Task ChangeAsync(string key, Action<JsonObject> change, ChordContext context) =>
            Task.FromException(new InvalidOperationException("File summaries are read-only"));
        public Task RetireAsync(string key, ChordContext context) => Task.FromException(new InvalidOperationException("File summaries are read-only"));
    }
}

internal sealed class FilesSource(JsonNode? initial, Batch batch) : Source(batch)
{
    private readonly object sync = new();
    private JsonNode? value = initial;
    private bool closed;
    private bool framed;
    public override JsonNode? Value { get { lock (sync) return value?.DeepClone(); } }
    internal void Set(JsonNode? next, ChordContext context)
    {
        lock (sync)
        {
            if (closed || JsonNode.DeepEquals(value, next)) return;
            value = next;
            framed = true;
            Changed();
        }
    }
    public override void Deliver(ChordContext context)
    {
        lock (sync)
        {
            if (closed || !framed) return;
            framed = false;
            Publish(value?.DeepClone(), [new ReplaceOp(value?.DeepClone())], false, context);
        }
    }
    internal void Close()
    {
        Batch.Within(() => { lock (sync)
        {
            closed = true;
            EndWatches();
        } }, ChordContext.Background);
    }
}
