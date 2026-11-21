// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/store/open-query.ts
//
// Open queries (slice 14 §3.2, §5): a query kept compiled against the store's bindings for as long as someone reads it, and the reads of
// it, one per range (a live query has one, a list one per page). The query compiles once into a plan; when a column set it names is bound,
// moved, dropped or its table becomes ready after a migration, it compiles again, its reads are registered for the new plan and its owner
// is told, which reads again (one read alone; a list every page as one new tiling: rows and order may be others everywhere). A query that
// cannot be compiled (a set nobody serves, a column that is not what it was written for) is a failed run of each read, not a throw.
// Mappings:
// - Upstream's `{ plan } | { error }` is the Compiled record with one of the two set.
// - Threads: an open query and its reads belong to their site's batch, that is to the host (State/Internal.cs): they are made, read, told
//   of a binding and closed there. Only LoadAsync leaves it, for the store's guarded read; it comes back through Batch.Within, and it is
//   there, not on the read's thread, that it checks whether a commit's run or a close overtook it.
using PiSharp.Chord.State;

namespace PiSharp.Chord.Store;

/// <summary>What an open query reads: a query of the table named <paramref name="Table"/>, and the columns its mapping gets (upstream
/// <c>QuerySpec</c>).</summary>
/// <param name="Query">The query.</param>
/// <param name="Table">The own table's SQL name.</param>
/// <param name="Projection">The columns the mapping gets.</param>
/// <param name="Owner">Who offers the query (the attempt's table handle): when it retires, the query's reads leave the index.</param>
internal sealed record QuerySpec(Query Query, string Table, Projection Projection, object Owner);

/// <summary>Where an open query lives: the store whose index its reads are in, and the batch adoptions outside a commit run in (upstream
/// <c>ReadSite</c>).</summary>
internal sealed record ReadSite(IChordStore Store, Batch Batch);

/// <summary>A query as it compiled: its plan, or why it does not compile.</summary>
internal sealed record Compiled(ReadPlan? Plan, Exception? Error);

/// <summary>A query compiled for the bindings as they are, with its open reads (upstream <c>OpenQuery</c>).</summary>
internal sealed class OpenQuery : IRetirable
{
    private readonly ReadShape shape;
    private readonly Action rebound;
    private readonly List<OpenRead> reads = [];
    // The releases of its binding watches; null once closed.
    private List<Action>? watches;

    /// <param name="site">Where it lives.</param>
    /// <param name="spec">What it reads.</param>
    /// <param name="shape">What its plan is read as.</param>
    /// <param name="rebound">Called whenever a binding changed and the query was compiled again: its reads are registered for the new plan
    /// and none has a result (a commit over their tables runs them); its owner reads again.</param>
    /// <param name="context">The context its reads run and recompile under.</param>
    internal OpenQuery(ReadSite site, QuerySpec spec, ReadShape shape, Action rebound, ChordContext context)
    {
        Site = site;
        Spec = spec;
        Context = context;
        this.shape = shape;
        this.rebound = rebound;
        var index = site.Store.ReadIndex;
        watches = [.. StoreInternal.SetsOf(spec.Query).Select(set => index.Watch(set, Recompile))];
        index.Opened(this);
        Compiled = Compile();
    }

    /// <summary>What it reads.</summary>
    internal QuerySpec Spec { get; }

    /// <summary>Where it lives.</summary>
    internal ReadSite Site { get; }

    /// <summary>The context its reads run and recompile under.</summary>
    internal ChordContext Context { get; }

    /// <summary>The plan while the query compiles, else why it does not.</summary>
    internal Compiled Compiled { get; private set; }

    /// <summary>Whether it was closed (by its reader, or because its owner retired).</summary>
    internal bool Closed => watches is null;

    object IRetirable.Owner => Spec.Owner;

    /// <summary>A read of <paramref name="what"/>, registered now (a commit in between runs it); <c>LoadAsync</c> runs it the first time. A
    /// read of a closed query is closed: it is never registered and never runs (a list whose provider retired cuts and loads pages of runs
    /// still in flight; no statement of the old plan may enter the index again).</summary>
    internal OpenRead Read(ReadKind what, IReadSink sink)
    {
        var read = new OpenRead(this, what, sink);
        if (!Closed) reads.Add(read);
        return read;
    }

    /// <summary>A read of <paramref name="range"/> whose rows are known already: every row of the range, as another read of this query
    /// returned them (a list's page cut from, or merged of, other pages). Nothing is read and its sink is not told.</summary>
    internal OpenRead ReadKnown(ReadRange range, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, IReadSink sink)
    {
        var read = Read(new ReadKind.Rows(range), sink);
        read.Know(rows);
        return read;
    }

    /// <summary><paramref name="read"/> was closed: the query keeps nothing of it.</summary>
    internal void Forget(OpenRead read) => reads.Remove(read);

    /// <summary>Its reads leave the index and it stops following bindings: no commit runs them again.</summary>
    public void Close()
    {
        if (watches is not { } releases) return;
        foreach (var release in releases) release();
        watches = null;
        Site.Store.ReadIndex.Closed(this);
        foreach (var read in reads.ToArray()) read.Close();
    }

    private Compiled Compile()
    {
        try
        {
            return new Compiled(new ReadPlan(Spec.Query, Spec.Table, Spec.Projection, shape, Site.Store.ReadIndex), null);
        }
        catch (Exception error)
        {
            return new Compiled(null, error);
        }
    }

    /// <summary>A binding changed: the query is compiled for the tables as they are now, and its owner reads again.</summary>
    private void Recompile()
    {
        if (watches is null) return;
        Compiled = Compile();
        foreach (var read in reads.ToArray()) read.Register();
        rebound();
    }
}

/// <summary>One read of an open query: registered with the store while the query compiles (upstream <c>OpenRead</c>).</summary>
internal sealed class OpenRead
{
    private readonly OpenQuery query;
    private readonly IReadSink sink;
    private bool closed;
    // Counts its adoptions and registrations: a first run that either overtook is dropped.
    private long generation;
    // Counts its registrations: a run belongs to the registration that computed it. A commit's step may have run the read before a binding
    // changed; that run is of a plan the query no longer has and is dropped when the commit adopts it. Closing registers nothing, so a run a
    // commit made before the close still reaches the sink.
    private long registration;

    internal OpenRead(OpenQuery query, ReadKind what, IReadSink sink)
    {
        this.query = query;
        What = what;
        this.sink = sink;
        closed = query.Closed;
        Register();
    }

    /// <summary>What it reads.</summary>
    internal ReadKind What { get; }

    /// <summary>The registered read, while the query compiles and this read is open.</summary>
    internal Read? Read { get; private set; }

    /// <summary>Enters a read of the query's current plan into the index (the previous one leaves it). Entered before its first run: a
    /// commit in between runs it (a read without a result is never skipped).</summary>
    internal void Register()
    {
        var index = query.Site.Store.ReadIndex;
        if (Read is not null) index.Delete(Read);
        Read = null;
        generation++;
        var compiled = query.Compiled;
        if (closed) return;
        var registered = ++registration;
        if (compiled.Plan is not { } plan) return;
        Read = new Read(plan, What, new ReadSink(run =>
        {
            if (registered != registration) return;
            generation++;
            sink.Adopt(run);
        }));
        index.Add(Read);
    }

    /// <summary>Takes <paramref name="rows"/> as its adopted result without running: every row of its range, known to its owner (a page
    /// whose rows a run of another range of the query just changed). Its sink is not told.</summary>
    internal void Know(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) => Read?.Know(rows);

    /// <summary>Its result is unknown from now on: the next commit over its tables runs it, whatever it writes.</summary>
    internal void Forget() => Read?.Forget();

    /// <summary>Runs it once, outside the commit queue, and adopts the run in a batch; completes with that run, null when a commit's run
    /// overtook it, a table it reads is held (it runs when the table is ready), or it was closed. Nothing throws: a query that does not
    /// compile and a run that fails are failed runs.</summary>
    internal async Task<ReadRun?> LoadAsync()
    {
        var (site, context, compiled) = (query.Site, query.Context, query.Compiled);
        if (closed) return null;
        if (compiled.Error is { } refused)
        {
            var failed = new ReadRun.Failed(refused);
            site.Batch.Within(() => sink.Adopt(failed), context);
            return failed;
        }
        var read = Read!;
        if (read.Plan.Uses.Any(use => site.Store.ReadIndex.IsHeld(use.Table))) return null;
        var began = generation;
        ReadRun run;
        try
        {
            run = await site.Store.ReadAsync(executor => read.FetchAsync(executor, context), context).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            run = new ReadRun.Failed(error);
        }
        // Decided where the read lives (the batch's thread), not on the store's: an adoption or a close may have come meanwhile.
        var overtaken = false;
        site.Batch.Within(() =>
        {
            if (generation != began)
            {
                overtaken = true;
                return;
            }
            read.Adopt(run);
        }, context);
        return overtaken ? null : run;
    }

    /// <summary>Leaves the index: no commit runs it again. A run a commit made already still reaches its sink.</summary>
    internal void Close()
    {
        if (closed) return;
        closed = true;
        generation++;
        if (Read is not null) query.Site.Store.ReadIndex.Delete(Read);
        Read = null;
        query.Forget(this);
    }
}
