// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/host/named-query.ts
//
// Named queries (slice 14 §3.1): what self.Queries(table, options, builders) returns, one OwnQuery per name, and what a reference shows for
// a `queries` member, one ReferenceQuery per name. A reader (self.LiveQuery, self.Count, self.List, PageAsync) asks either for the
// provider's declaration, checks its arguments, builds the query and reads it through the store. Mappings:
// - A row a mapping gets is a map of column to JSON value (Store/Rows.cs), a partner row the same; an item is a JSON value. The item a
//   mapping returns becomes a row of a value: a node of its own (State/Framed.cs). One that already has a parent is copied.
// - Upstream's builders are `name: builder` or `name: [argsSchema, builder]`; here a QueryBuilder with or without Args.
// - PageAsync may be called from any thread: it runs on the host's dispatcher, as a reference's call does.
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Store;

namespace PiSharp.Chord.Host;

/// <summary>Maps a row (and, for a joined query, its partner: the projected columns of the joined set, decoded; empty without a join) to
/// the item readers get (upstream <c>QueriesOptions.map</c>).</summary>
public delegate JsonNode? QueryMap(IReadOnlyDictionary<string, JsonNode?> row, IReadOnlyDictionary<string, JsonNode?> partner);

/// <summary>What <c>self.Queries</c> takes besides the table and the builders (upstream <c>QueriesOptions</c>).</summary>
/// <param name="Map">Maps a row (and a joined query's partner) to the item readers get; the row itself when left out.</param>
/// <param name="Reads">The columns of the table <paramref name="Map"/> gets: only these (a change of another is no change of the item).
/// All by default.</param>
/// <param name="PartnerReads">The columns of a joined set <paramref name="Map"/> gets. All by default.</param>
public sealed record QueriesOptions(QueryMap? Map = null, IReadOnlyList<string>? Reads = null, IReadOnlyList<string>? PartnerReads = null);

/// <summary>One named query's builder: a pure function of the query's arguments returning a query of the table, joined or not. A query the
/// contract lists brings no <paramref name="Args"/> (the contract has its argument schema); one private to its owner brings its own
/// (upstream <c>name: builder</c> and <c>name: [argsSchema, builder]</c>).</summary>
public sealed record QueryBuilder(Func<JsonNode?, Query> Build, JsonNode? Args = null);

/// <summary>A named query as readers hold it: its owner's declaration, or a reference's member (upstream <c>NamedQueryHandle</c>).</summary>
internal abstract class NamedQueryHandle : INamedQuery
{
    /// <summary>The query in errors and reports: its name, with its member for a reference's.</summary>
    internal abstract string Label { get; }

    /// <summary>The provider's declaration; throws while the provider is not live (a reference's state says why).</summary>
    internal abstract OwnQuery Provided();

    /// <summary>Calls <paramref name="moved"/> whenever another instance of the provider is live, until the returned release.</summary>
    internal abstract Action Follow(Action moved);

    /// <summary>The host whose dispatcher its reads run on.</summary>
    private protected abstract ChordHost Host { get; }

    /// <inheritdoc/>
    public Task<QueryPage> PageAsync(JsonNode? args, long limit, ChordContext context, PageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ChordHost host;
        try { host = Host; }
        catch (Exception error) { return Task.FromException<QueryPage>(error); }
        return host.Dispatcher.RunAsync(() => Provided().PageOfAsync(args, limit, context, options?.After));
    }
}

/// <summary>A named query as its owner declared it: the table, the builder, the schema its arguments are checked by (the contract's when
/// the query is listed there, its own otherwise), the columns its mapping gets and the mapping (upstream <c>OwnQuery</c>).</summary>
internal sealed class OwnQuery : NamedQueryHandle
{
    private static readonly IReadOnlyDictionary<string, JsonNode?> NoPartner = new Dictionary<string, JsonNode?>();
    private static readonly Action NeverMoves = static () => { };

    private readonly ChordHost host;
    private readonly Func<JsonNode?, Query> builder;
    private readonly Projection projection;
    private readonly QueryMap? map;
    // The schema of its arguments: its own (a private query), or the contract's once its member was checked.
    private Snapshot? args;
    private bool listed;

    internal OwnQuery(ChordHost host, string owner, string name, TableHandle handle, QueryBuilder builder, QueriesOptions options, Snapshot? args)
    {
        this.host = host;
        Label = $"{owner}: query \"{name}\"";
        Handle = handle;
        this.builder = builder.Build;
        projection = new Projection(options.Reads, options.PartnerReads);
        map = options.Map;
        this.args = args;
    }

    internal override string Label { get; }

    internal TableHandle Handle { get; }

    private protected override ChordHost Host => host;

    /// <summary>Whether its arguments have a schema: its own, or the contract's.</summary>
    internal bool Checked => args is not null;

    internal override OwnQuery Provided() => this;

    internal override Action Follow(Action moved) => NeverMoves;

    /// <summary>The contract lists this query with the argument schema <paramref name="schema"/> (the member check): a listed query brings
    /// no schema of its own (declaration policy).</summary>
    internal void Listed(Snapshot schema)
    {
        if (listed) return;
        if (args is not null) throw new ArgumentException($"{Label}: the contract has its argument schema; give only the builder");
        args = schema;
        listed = true;
    }

    /// <summary>What reading this query for <paramref name="given"/> reads: the arguments checked (a trust boundary), the query built and
    /// checked.</summary>
    internal LiveTarget Target(JsonNode? given, ChordContext context)
    {
        var query = Build(given, context);
        return new LiveTarget(new QuerySpec(query, Handle.Sql.Table, projection, Handle), (stored, decoding) => Decode(query, stored, decoding));
    }

    /// <summary>One page, read once: not live.</summary>
    internal async Task<QueryPage> PageOfAsync(JsonNode? given, long limit, ChordContext context, string? after)
    {
        Handle.Owner.AssertLive();
        var query = Build(given, context);
        var plan = new ReadPlan(query, Handle.Sql.Table, projection, ReadShape.Rows, Handle.Store.ReadIndex);
        var page = await StoreInternal.ReadPageAsync(Handle.Store, plan, limit, after, context).ConfigureAwait(false);
        return new QueryPage([.. page.Stored.Select(row => Decode(query, row, context))], page.Next);
    }

    private Query Build(JsonNode? given, ChordContext context)
    {
        // Declaration policy: a query the contract does not list brings its schema.
        var schema = args ?? throw new ArgumentException($"{Label}: a private query brings its argument schema");
        DataInternal.Check(DataInternal.ValidatorFor(schema, context), given, "args", context);
        // The builder is plugin code: what it returns is checked.
        Query? built;
        using (host.RetainCallback(builder, Handle.Owner is ChordObject owner ? owner.Served.DeclaringFacet : null)) built = builder(given);
        if (built is null || !ReferenceEquals(built.Table, Handle.Definition))
            throw new ArgumentException($"{Label}: the builder returns a query of table \"{Handle.Definition.Name}\"");
        return built;
    }

    /// <summary>A stored row as readers get it: the row (and partner) decoded and mapped. The statement also selects what the store needs
    /// (the key, the order and match columns): the mapping gets only the columns <c>Reads</c> and <c>PartnerReads</c> list, so a column it
    /// does not declare is absent, at once and visibly.</summary>
    private JsonNode? Decode(Query query, IReadOnlyDictionary<string, object?> stored, ChordContext context)
    {
        var own = projection.Reads is { } reads ? Projected(stored, reads) : stored;
        var row = Handle.Definition.Rows.Decode(own, context);
        // A table's rows hold its columns' values (scalars and JSON); a mapping's result is the item (its contract's).
        if (map is null)
        {
            var item = new JsonObject();
            foreach (var (column, cell) in row) item[column] = cell;
            return item;
        }
        var partner = query.Joined is { } joined ? PartnerRow(joined.Set, projection.PartnerReads, stored) : NoPartner;
        var mapped = map(row, partner);
        return mapped?.Parent is null ? mapped : mapped.DeepClone();
    }

    /// <summary>The columns <paramref name="names"/> of <paramref name="stored"/>, each <c>json(...)</c> one with its version column.</summary>
    private static Dictionary<string, object?> Projected(IReadOnlyDictionary<string, object?> stored, IReadOnlyList<string> names)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            row[name] = stored.GetValueOrDefault(name);
            if (stored.TryGetValue(StoreInternal.VersionColumn(name), out var version) && version is not null) row[StoreInternal.VersionColumn(name)] = version;
        }
        return row;
    }

    /// <summary>The joined set's columns <paramref name="names"/> (all without) of <paramref name="stored"/> (named
    /// <c>&lt;set&gt;:&lt;column&gt;</c> there), decoded.</summary>
    private static Dictionary<string, JsonNode?> PartnerRow(ColumnSet set, IReadOnlyList<string>? names, IReadOnlyDictionary<string, object?> stored)
    {
        var row = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var name in names ?? set.ColumnNames)
        {
            // A set's columns are scalar (DefineColumns refuses json(...)).
            if (stored.TryGetValue(StoreInternal.PartnerName(set.Name, name), out var value) && value is not null && set.ByName.TryGetValue(name, out var column))
                row[name] = StoreInternal.DecodeScalar(column, value);
        }
        return row;
    }
}

/// <summary>A named query of a reference's <c>queries</c> member: the provider's declaration of that name, while it is live (upstream
/// <c>ReferenceQuery</c>).</summary>
internal sealed class ReferenceQuery(Binding binding, string member, string name) : NamedQueryHandle
{
    internal override string Label => $"{binding.Label}: query \"{member}.{name}\"";

    private protected override ChordHost Host => binding.Host;

    internal override OwnQuery Provided() => binding.QueryMember(member, name);

    internal override Action Follow(Action moved) => binding.FollowInstance(moved);
}

/// <summary>The member check of <c>queries</c> members (upstream <c>checkQueries</c> and <c>ownQueries</c>).</summary>
internal static class QueryImplementations
{
    /// <summary>The named queries of <paramref name="value"/> when it is what <c>self.Queries</c> returned: a map of <see cref="OwnQuery"/>s.</summary>
    internal static IReadOnlyDictionary<string, OwnQuery>? OwnQueries(object? value)
    {
        if (value is OwnQueries own) return own.Queries;
        if (value is not IReadOnlyDictionary<string, INamedQuery> named || named.Values.Any(each => each is not OwnQuery)) return null;
        return named.ToDictionary(entry => entry.Key, entry => (OwnQuery)entry.Value, StringComparer.Ordinal);
    }

    /// <summary>Checks the <c>queries</c> member <paramref name="member"/>: it is what <c>self.Queries</c> returned (an object's own named
    /// queries: a reference's are not re-exposed), with a builder for every query the contract lists, which takes the contract's argument
    /// schema; a query the contract does not list brings its own. While staging (no attempt) a recording's inert queries are checked the
    /// same way, by what their builders brought.</summary>
    internal static void Check(string member, QueriesData listed, object? value, Attempt? attempt)
    {
        if (attempt is null && value is RecordingQueries { Builders: { } recorded })
        {
            foreach (var name in listed.Args.Keys)
            {
                if (!recorded.TryGetValue(name, out var brings)) throw new ArgumentException($"{member}: no builder for the listed query \"{name}\"");
                if (brings) throw new ArgumentException($"query \"{name}\": the contract has its argument schema; give only the builder");
            }
            foreach (var (name, brings) in recorded)
                if (!brings && !listed.Args.ContainsKey(name)) throw new ArgumentException($"{member}: query \"{name}\" is not listed and brings no argument schema");
            return;
        }
        var declared = attempt is null ? null : OwnQueries(value);
        if (declared is null) throw new ArgumentException($"{member}: a queries member is what self.queries returned");
        foreach (var (name, schema) in listed.Args)
        {
            if (!declared.TryGetValue(name, out var query)) throw new ArgumentException($"{member}: no builder for the listed query \"{name}\"");
            query.Listed(schema);
        }
        foreach (var (name, query) in declared)
            if (!query.Checked) throw new ArgumentException($"{member}: query \"{name}\" is not listed and brings no argument schema");
        attempt!.QueryMembers[member] = declared;
    }
}

/// <summary>What <c>self.Queries</c> returns: its named queries by name, in the builders' order.</summary>
internal sealed class OwnQueries(IReadOnlyList<KeyValuePair<string, OwnQuery>> entries) : IReadOnlyDictionary<string, INamedQuery>
{
    internal readonly IReadOnlyDictionary<string, OwnQuery> Queries = entries.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    public INamedQuery this[string key] => Queries.TryGetValue(key, out var query) ? query : throw new KeyNotFoundException($"no named query \"{key}\"");
    public IEnumerable<string> Keys => entries.Select(entry => entry.Key);
    public IEnumerable<INamedQuery> Values => entries.Select(entry => (INamedQuery)entry.Value);
    public int Count => entries.Count;
    public bool ContainsKey(string key) => Queries.ContainsKey(key);
    public bool TryGetValue(string key, out INamedQuery value)
    {
        var found = Queries.TryGetValue(key, out var query);
        value = query!;
        return found;
    }
    public IEnumerator<KeyValuePair<string, INamedQuery>> GetEnumerator() => entries.Select(entry => new KeyValuePair<string, INamedQuery>(entry.Key, entry.Value)).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed partial class Attempt
{
    /// <summary>Its <c>queries</c> members as checked: the named queries of each, by name.</summary>
    internal readonly Dictionary<string, IReadOnlyDictionary<string, OwnQuery>> QueryMembers = new(StringComparer.Ordinal);

    /// <summary>The named query <paramref name="name"/> of its <c>queries</c> member <paramref name="member"/>; an unknown one fails
    /// <c>invalid/member</c>.</summary>
    internal OwnQuery QueryMember(string member, string name) =>
        QueryMembers.TryGetValue(member, out var queries) && queries.TryGetValue(name, out var query)
            ? query : throw DataInternal.Invalid("member", $"{Owner.Label} has no query \"{member}.{name}\"");
}

internal sealed partial class ChordObject
{
    // What its lists keep across its attempts, by name; made by the first.
    private Dictionary<string, ListMemory>? lists;

    /// <summary>What its list <paramref name="name"/> keeps across its attempts: its bookmarks, so a reload's successor opens the pages
    /// the list had. It goes with the object.</summary>
    internal ListMemory ListMemoryOf(string name)
    {
        lists ??= new(StringComparer.Ordinal);
        if (!lists.TryGetValue(name, out var memory)) lists.Add(name, memory = new ListMemory());
        return memory;
    }

    /// <summary>The named query <paramref name="name"/> of <c>queries</c> member <paramref name="member"/> of the live object (slice 14
    /// §3.1): the live attempt's, or a restart's open old attempt's (its consumers saw it live); <c>not_ready</c> before the object went
    /// live and while it activates again, the cause while failed, its end's error once it ended.</summary>
    public OwnQuery QueryMember(string member, string name)
    {
        if (Current is { Live: true } live) return live.QueryMember(member, name);
        if (state.Kind == "loading" && OpenOldAttempt is { } old) return old.QueryMember(member, name);
        if (state.Kind == "ended") throw state.Error!;
        if (state.Cause is { } failure) throw failure.Error;
        throw new ChordError("not_ready", $"{Label}: not live yet");
    }
}

internal sealed partial class Binding
{
    // Its queries members as handed out, by member; made by the first.
    private Dictionary<string, IReadOnlyDictionary<string, INamedQuery>>? queryMembers;
    // Who reads its object's named queries: each told when another instance is live (FollowInstance).
    private List<Action>? instanceFollowers;
    // The instance its followers were told of last, held weakly: an ended instance is kept by nothing here.
    private WeakReference<object>? followedInstance;

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, INamedQuery> QueriesMember(string name) => Host.Dispatcher.Invoke(() =>
    {
        AssertUsable();
        if (!ContractsInternal.DataOf(Contract).Kinds.TryGetValue(name, out var kind) || kind != MemberKind.Queries) throw HostErrors.InvalidMember(name);
        queryMembers ??= new(StringComparer.Ordinal);
        if (!queryMembers.TryGetValue(name, out var queries))
        {
            // A queries member is a local contract's: its one variant lists the queries.
            var listed = ContractsInternal.CurrentVariants(Contract)[ContractsInternal.Local].Queries[name];
            queries = listed.Args.Keys.ToDictionary(query => query, query => (INamedQuery)new ReferenceQuery(this, name, query), StringComparer.Ordinal);
            queryMembers.Add(name, queries);
        }
        return queries;
    });

    /// <summary>The named query <paramref name="name"/> of <c>queries</c> member <paramref name="member"/> of the live object. Throws
    /// <c>released</c> once released, the failure's error while it is failed, <c>not_ready</c> before it is bound or while its object is
    /// not live.</summary>
    internal OwnQuery QueryMember(string member, string name)
    {
        AssertUsable();
        if (state.Cause is { } failure) throw failure.Error;
        if (Target is not { } target) throw new ChordError("not_ready", $"{Contract.Name}: not live yet");
        return target.QueryMember(member, name);
    }

    /// <summary>Calls <paramref name="moved"/> whenever its object has another live instance than the one it was told of last (a restart's
    /// successor: the readers of its named queries read that one's, slice 14 §3.2), until the returned release.</summary>
    internal Action FollowInstance(Action moved)
    {
        var followers = instanceFollowers ??= [];
        followers.Add(moved);
        if (followedInstance is null && Target?.Instance is { } instance) followedInstance = new(instance);
        return () => followers.Remove(moved);
    }

    // Tells the readers of its object's named queries of another live instance (not of none: they keep their rows).
    private void ToldOfInstance()
    {
        if (instanceFollowers is not { Count: > 0 } followers || Target?.Instance is not { } instance) return;
        if (followedInstance is not null && followedInstance.TryGetTarget(out var told) && ReferenceEquals(told, instance)) return;
        followedInstance = new(instance);
        foreach (var moved in followers.ToArray()) moved();
    }
}
