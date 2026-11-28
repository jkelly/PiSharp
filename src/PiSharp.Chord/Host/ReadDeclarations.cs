// Pi fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT): packages/chord/src/host/read-declarations.ts (v1.7.0 updates).
// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/host/read-declarations.ts, and the declarations of
// packages/chord/src/host/{attempt,staging}.ts that use it (queries, liveQuery, count, list).
//
// The declarations of reads (slice 14 §3, §4): what self.Queries, self.LiveQuery, self.Count and self.List check and build. The checks are
// pure, so an attempt and staging's recording self make the same ones (a reload whose new code declares a read wrongly keeps the plugin on
// its old code); the builders make what an attempt opens. Mappings:
// - The declarations are typed here: a table is an ITable, a named query an INamedQuery, a page size a long. What upstream's checks catch
//   of untyped plugin code and the types still allow is checked (a table or a named query that is none of Chord's, a page size below one,
//   more or fewer than one source); what the types exclude cannot be written (a table handed to a reader, a named query handed to
//   self.Query).
// - Arguments are a JsonNode (null is JSON null) or a watchable of one made by Chord (a derive, a state member, a reference's member, a
//   constant).
// - A recording's handles are RecordingQueries and RecordingQuery (upstream INERT_NAMED): a recording says whether a named query is its
//   own factory's or a reference's.
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.State;
using PiSharp.Chord.Store;

namespace PiSharp.Chord.Host;

/// <summary>What <c>self.List</c> takes besides its name and sources (upstream <c>ListOptions</c>).</summary>
/// <param name="First">The rows of a page: what the first page and each <c>MoreAsync</c> load. A positive integer.</param>
public sealed record ListOptions(long First);

/// <summary>What <c>self.List</c> returns: the list's value for a state member, and <c>MoreAsync</c> for a method (upstream
/// <c>ListHandle</c>).</summary>
public interface IListHandle
{
    /// <summary>The loaded rows in order, and whether rows may follow them: <c>{ "rows": [...], "moreAfter": bool }</c>.</summary>
    IWatchable<JsonNode?> Value { get; }

    /// <summary>Loads one more page behind the last one; completes once its rows are in <see cref="Value"/>. Calls run one at a time. The
    /// caller's wait ends with <paramref name="context"/>; the load goes on.</summary>
    Task MoreAsync(ChordContext context);
}

/// <summary>Where an attempt's readers live: the store and batch, the object's label, its failure reports and its wake (upstream
/// <c>ReaderSite</c>).</summary>
/// <param name="Site">The store and the batch.</param>
/// <param name="Host">The host, whose dispatcher a list's loads run on.</param>
/// <param name="Label">The object's label.</param>
/// <param name="Report">Reports a failed run of a query after the reader went live (<c>query_failed</c>, once per episode).</param>
/// <param name="Wake">Tells the attempt, waiting on a reader, to open it again.</param>
internal sealed record ReaderSite(ReadSite Site, ChordHost Host, string Label, Action<string, Exception> Report, Action Wake);

internal static class ReadDeclarations
{
    /// <summary>
    /// Opens each of <paramref name="readers"/> in turn (slice 14 §3.2), named to <paramref name="episode"/> as <paramref name="label"/>
    /// while its first run is awaited, as one step of a parked attempt (or of an admission): ready once all have values; else parked on the
    /// first that is not, <c>loading</c> while its read has not run yet (a table it reads is being created or migrated), <c>failed</c>
    /// waiting on a read it waits on; or broken (its own code). Parked, it says why (the cause), as a parked dependency does. A reader that
    /// is not ready is followed by <paramref name="wake"/> from then on: its next adoption (its table ready, a good run after a failed one)
    /// checks again (upstream <c>openReaders</c>).
    /// </summary>
    internal static async Task<Parking> OpenReadersAsync(IReadOnlyList<IQueryReader> readers, IDependent wake, IBlocked episode, string label, ChordContext context)
    {
        foreach (var reader in readers)
        {
            episode.BlockedBy(new(label, "loading"));
            var opened = await reader.OpenAsync(context);
            if (opened is Opened.Ready) continue;
            reader.Source.Dependents.Add(wake);
            if (opened is Opened.Loading)
            {
                var loading = new ChordError("unavailable", $"{label} is loading");
                return new Parking.Parked(RefState.Loading, false, DataInternal.WaitingFor(label, loading));
            }
            episode.BlockedBy(new(label, "failed"));
            if (opened is Opened.Broken broken) return new Parking.Broken(DataInternal.AsChordError(broken.Error, label));
            var cause = DataInternal.WaitingFor(label, DataInternal.AsChordError(((Opened.Waiting)opened).Error, label));
            return new Parking.Parked(RefState.Failed(cause), true, cause);
        }
        return Parking.Go;
    }

    /// <summary>The builders <c>self.Queries</c> was given, checked (declaration policy): each has a builder, and the argument schema of
    /// one that brings its own is closed (upstream <c>namedBuilders</c>).</summary>
    internal static List<(string Name, QueryBuilder Builder, Snapshot? Args)> NamedBuilders(string label, IReadOnlyDictionary<string, QueryBuilder>? builders)
    {
        if (builders is null) throw new ArgumentException($"{label}: self.queries: builders by name");
        var named = new List<(string, QueryBuilder, Snapshot?)>();
        foreach (var (name, entry) in builders)
        {
            if (entry?.Build is null) throw new ArgumentException($"{label}: self.queries: \"{name}\" has no builder");
            // The builder is plugin code: what it returns is checked when a read opens.
            named.Add((name, entry, entry.Args is null ? null : ContractsInternal.QueryArguments(entry.Args, $"{label}: query \"{name}\": args")));
        }
        return named;
    }

    /// <summary>The page size of a live query or a list: a positive integer (a bound is required; upstream <c>firstOf</c>).</summary>
    internal static long FirstOf(string label, string declaration, long? first)
    {
        if (first is not { } rows || rows < 1 || rows > StoreInternal.MaxSafeInteger)
            throw new ArgumentException($"{label}: self.{declaration}: {{ first: n }} is required, a positive integer");
        return rows;
    }

    /// <summary>Checks a list's name (non-empty, once per object: its bookmarks are kept by it) and remembers it in
    /// <paramref name="listed"/> (upstream <c>listName</c>).</summary>
    internal static string ListName(string label, string? name, HashSet<string> listed)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException($"{label}: self.list: a list has a name");
        if (!listed.Add(name)) throw new ArgumentException($"{label}: list \"{name}\" is declared twice");
        return name;
    }

    /// <summary>The one source of a list (lists over several sources are not built yet; upstream <c>oneSource</c>).</summary>
    internal static (INamedQuery Query, object? Args) OneSource(string label, IReadOnlyList<(INamedQuery Query, object? Args)>? sources) =>
        sources is { Count: 1 } ? sources[0] : throw new ArgumentException($"{label}: self.list: one source, [[namedQuery, args]]");

    /// <summary>The refusal of what <c>self.LiveQuery</c>, <c>self.Count</c> or <c>self.List</c> (<paramref name="declaration"/>) was given
    /// as its named query (upstream <c>namedQueryExpected</c>).</summary>
    internal static ArgumentException NamedQueryExpected(string label, string declaration) =>
        new($"{label}: self.{declaration} reads a named query; a table of this object is read with self.query");

    /// <summary>The refusal of a table given to <c>self.Query</c> or <c>self.Queries</c> (<paramref name="declaration"/>) that the factory
    /// did not declare (upstream <c>undeclaredTable</c>).</summary>
    internal static ArgumentException UndeclaredTable(string label, string declaration) =>
        new($"{label}: self.{declaration}: the table is not one this factory declared");

    /// <summary>Where the readers <paramref name="label"/> declares on <paramref name="host"/> live (an attempt's, an admission's): a
    /// reader needs the host's store. <paramref name="wake"/> tells whoever waits on a reader to open it again (upstream
    /// <c>readerSite</c>).</summary>
    internal static ReaderSite ReaderSite(ChordHost host, string label, Action wake)
    {
        if (host.Store is not { } store) throw new ArgumentException($"{label}: reading a named query needs a store");
        return new ReaderSite(new ReadSite(store, host.Batch), host, label,
            (query, error) => host.Report(new("query_failed", error, Object: label, Query: query)), wake);
    }

    /// <summary>The reader of <c>self.LiveQuery(query, args, first)</c>: its value the first rows as <c>{ rows, more }</c> (upstream
    /// <c>rowsReader</c>).</summary>
    internal static (Reader Reader, LiveQuery Source) RowsReader(ReaderSite at, INamedQuery? query, object? args, long first) =>
        LiveReader(at, "liveQuery", query, args, new LiveShape.Rows(new ReadRange(FirstOf(at.Label, "liveQuery", first)), WithMore: true));

    /// <summary>The reader of <c>self.LiveQuery</c> (shape rows) or of <c>self.Count(query, args)</c> (count; upstream
    /// <c>liveReader</c>).</summary>
    internal static (Reader Reader, LiveQuery Source) LiveReader(ReaderSite at, string declaration, INamedQuery? query, object? args, LiveShape shape)
    {
        var given = GivenQueryOf(at, declaration, query, args);
        var source = new LiveQuery(at.Site, new ReaderOwner(at, given), shape);
        return (new Reader(given, new LiveReading(source), at.Wake), source);
    }

    /// <summary>The reader of <c>self.List(name, [(query, args)], options)</c>; <paramref name="memory"/> is what the object keeps of the
    /// list (upstream <c>listReader</c>).</summary>
    internal static (Reader Reader, ListSource List) ListReader(ReaderSite at, string name, (INamedQuery Query, object? Args) source, long first, ListMemory memory)
    {
        var given = GivenQueryOf(at, $"list \"{name}\"", source.Query, source.Args);
        var list = new ListSource(at.Site, new ReaderOwner(at, given), first, memory, at.Host.Dispatcher.RunAsync);
        return (new Reader(given, list, at.Wake), list);
    }

    /// <summary>The arguments a reader was given: JSON (their schema checks them when the read opens) or a watchable of it.</summary>
    internal static GivenArguments ArgumentsOf(string label, string declaration, object? args) => ChordHost.ResolveSource(args) switch
    {
        null => new GivenArguments.Fixed(null),
        JsonNode node => new GivenArguments.Fixed(node),
        Source or Constant => new GivenArguments.Following(ChordHost.ResolveSource(args)!),
        _ => throw new ArgumentException($"{label}: self.{declaration}: arguments are JSON, or a JSON watchable of this host"),
    };

    // The named query a reader was given with its arguments, checked: a named query of Chord's (a table of the object is the old form's
    // argument), and arguments that are JSON or a watchable of them.
    private static GivenQuery GivenQueryOf(ReaderSite at, string declaration, INamedQuery? query, object? args)
    {
        if (query is not NamedQueryHandle handle) throw NamedQueryExpected(at.Label, declaration);
        return new GivenQuery(handle, ArgumentsOf(at.Label, declaration, args), at.Site.Batch);
    }

    // Who a reader's source belongs to: its object, reporting failures under the query's name.
    private sealed class ReaderOwner(ReaderSite at, GivenQuery given) : IFramedOwner
    {
        public string Label => at.Label;

        public void Report(Exception error) => at.Report(given.Handle.Label, error);
    }
}

internal sealed partial class Self
{
    /// <summary>Its readers of named queries (<c>self.LiveQuery</c>, <c>self.Count</c>, <c>self.List</c>), opened once its tables are
    /// prepared and its dependencies live, closed at its end.</summary>
    internal readonly List<Reader> Readers = [];
    // The names of the lists its factory declared: one list per name.
    private readonly HashSet<string> listed = new(StringComparer.Ordinal);

    IReadOnlyDictionary<string, INamedQuery> IStoreSelf.Queries(ITable table, QueriesOptions options, IReadOnlyDictionary<string, QueryBuilder> builders)
    {
        Declare("queries");
        var label = Attempt.Owner.Label;
        // Plugin code may pass any table: only a handle this attempt declared is taken (its object's own table).
        if (table is not TableHandle handle || !Tables.Contains(handle)) throw ReadDeclarations.UndeclaredTable(label, "queries");
        // The mapping is the plugin's; Chord hands it decoded rows. One named query per builder.
        return new OwnQueries([.. ReadDeclarations.NamedBuilders(label, builders).Select(each =>
            new KeyValuePair<string, OwnQuery>(each.Name, new OwnQuery(Host, label, each.Name, handle, each.Builder, options ?? new QueriesOptions(), each.Args)))]);
    }

    IWatchable<JsonNode?> ISelf.LiveQuery(INamedQuery query, object? args, long first)
    {
        Declare("liveQuery");
        var (reader, source) = ReadDeclarations.RowsReader(ReaderSite(), query, args, first);
        Readers.Add(reader);
        return source;
    }

    IWatchable<JsonNode?> ISelf.Count(INamedQuery query, object? args)
    {
        Declare("count");
        var (reader, source) = ReadDeclarations.LiveReader(ReaderSite(), "count", query, args, new LiveShape.Count());
        Readers.Add(reader);
        return source;
    }

    IListHandle ISelf.List(string name, IReadOnlyList<(INamedQuery Query, object? Args)> sources, ListOptions options)
    {
        Declare("list");
        var at = ReaderSite();
        var first = ReadDeclarations.FirstOf(at.Label, "list", options?.First);
        var source = ReadDeclarations.OneSource(at.Label, sources);
        // Checked last: a list refused for another reason does not take its name.
        var unique = ReadDeclarations.ListName(at.Label, name, listed);
        try
        {
            var (reader, list) = ReadDeclarations.ListReader(at, unique, source, first, Attempt.Owner.ListMemoryOf(unique));
            Readers.Add(reader);
            return new ListHandle(list);
        }
        catch { listed.Remove(unique); throw; }
    }

    // Where this attempt's readers live.
    private ReaderSite ReaderSite() => ReadDeclarations.ReaderSite(Host, Attempt.Owner.Label, Attempt.Wake);

    // Its readers end with it: no commit runs their reads again, their members' reads throw the object's error.
    internal void CloseReaders(Exception error)
    {
        foreach (var reader in Readers) reader.Close(error);
    }

    /// <summary>The named queries its tables offered are offered no more (slice 14 §3.2): their readers' reads leave the store's index,
    /// keep their last values and read the successor's queries when it is live.</summary>
    internal void RetireOffered()
    {
        foreach (var table in Tables) table.Store.ReadIndex.Retire(table);
    }

    private sealed class ListHandle(ListSource list) : IListHandle
    {
        public IWatchable<JsonNode?> Value => list;

        public Task MoreAsync(ChordContext context) => list.MoreAsync(context);
    }
}

internal sealed partial class RecordingSelf
{
    private readonly HashSet<string> listed = new(StringComparer.Ordinal);

    // Named queries, their readers and lists: the checks an attempt makes on its declarations, then inert handles. A recorded handle says
    // what it stands for (a table, a named query of this object, a reference's), so a named query that is none of Chord's is refused here
    // as an attempt refuses it. Staging has no effects: nothing is compiled, read or bound, and no bookmark is touched.
    IReadOnlyDictionary<string, INamedQuery> IStoreSelf.Queries(ITable table, QueriesOptions options, IReadOnlyDictionary<string, QueryBuilder> builders)
    {
        Check("queries");
        if (table is not RecordingTable token || token.Definition is null || !tables.Values.Contains(token.Definition))
            throw ReadDeclarations.UndeclaredTable(owner.Label, "queries");
        return new RecordingQueries(ReadDeclarations.NamedBuilders(owner.Label, builders).ToDictionary(each => each.Name, each => each.Args is not null, StringComparer.Ordinal));
    }

    IWatchable<JsonNode?> ISelf.LiveQuery(INamedQuery query, object? args, long first)
    {
        Check("liveQuery");
        ReadDeclarations.FirstOf(owner.Label, "liveQuery", first);
        CheckNamed(query, "liveQuery");
        return RecordingValue.Instance;
    }

    IWatchable<JsonNode?> ISelf.Count(INamedQuery query, object? args)
    {
        Check("count");
        CheckNamed(query, "count");
        return RecordingValue.Instance;
    }

    IListHandle ISelf.List(string name, IReadOnlyList<(INamedQuery Query, object? Args)> sources, ListOptions options)
    {
        Check("list");
        ReadDeclarations.FirstOf(owner.Label, "list", options?.First);
        var (query, _) = ReadDeclarations.OneSource(owner.Label, sources);
        CheckNamed(query, $"list \"{ReadDeclarations.ListName(owner.Label, name, listed)}\"");
        return RecordingList.Instance;
    }

    // Refuses what a reader was given when it cannot be a named query: no handle of this self.
    private void CheckNamed(INamedQuery? query, string declaration)
    {
        if (query is not RecordingQuery) throw ReadDeclarations.NamedQueryExpected(owner.Label, declaration);
    }
}

/// <summary>What a recording <c>self.Queries</c> returns, and what a recording reference shows for a <c>queries</c> member: inert named
/// queries. Its own factory's knows its builders (whether each brought an argument schema), which the member check reads; a reference's
/// answers any name.</summary>
internal sealed class RecordingQueries(IReadOnlyDictionary<string, bool>? builders) : IReadOnlyDictionary<string, INamedQuery>, IRecordingValue
{
    /// <summary>A recording reference's <c>queries</c> member.</summary>
    internal static RecordingQueries OfReference { get; } = new(null);

    /// <summary>The builders by name, each with whether it brought its argument schema; null for a reference's member.</summary>
    internal IReadOnlyDictionary<string, bool>? Builders => builders;

    public INamedQuery this[string key] => builders is null || builders.ContainsKey(key) ? RecordingQuery.Instance : throw new KeyNotFoundException($"no named query \"{key}\"");
    public IEnumerable<string> Keys => builders?.Keys ?? [];
    public IEnumerable<INamedQuery> Values => Keys.Select(_ => (INamedQuery)RecordingQuery.Instance);
    public int Count => builders?.Count ?? 0;
    public bool ContainsKey(string key) => builders is null || builders.ContainsKey(key);
    public bool TryGetValue(string key, out INamedQuery value)
    {
        value = RecordingQuery.Instance;
        return ContainsKey(key);
    }
    public IEnumerator<KeyValuePair<string, INamedQuery>> GetEnumerator() => Keys.Select(key => new KeyValuePair<string, INamedQuery>(key, RecordingQuery.Instance)).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>An inert named query of a recording.</summary>
internal sealed class RecordingQuery : INamedQuery, IRecordingValue
{
    internal static RecordingQuery Instance { get; } = new();

    public Task<QueryPage> PageAsync(JsonNode? args, long limit, ChordContext context, PageOptions? options = null) =>
        throw new InvalidOperationException("a recording named query cannot be read before activation");
}

/// <summary>An inert list of a recording.</summary>
internal sealed class RecordingList : IListHandle, IRecordingValue
{
    internal static RecordingList Instance { get; } = new();

    public IWatchable<JsonNode?> Value => RecordingValue.Instance;

    public Task MoreAsync(ChordContext context) => throw new InvalidOperationException("a recording list cannot load before activation");
}
