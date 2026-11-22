// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/store/reads.ts
//
// The store's reads (slice 14 §3.2, §5): which reads read which table, whoever owns them, and which table each column set is bound to. A
// transaction that wrote tables stages ReadRefresh: inside its commit step, after its writes, it takes the reads over those tables from the
// index as it is then, runs the skip test, runs the others again and adopts their results in the commit's batch. Mappings:
// - Upstream's index runs on one JavaScript thread. Here the commit step reads it on the store's queue while the host binds sets and opens
//   and closes queries on its own thread, so the index is kept under one lock; watchers and closes are called outside it, on the caller's
//   thread (the host's, which is where an open query lives).
// - Retirable is IRetirable, which OpenQuery implements.
using PiSharp.Chord.Data;

namespace PiSharp.Chord.Store;

/// <summary>An open query as the index retires it: who offers it, and its close (upstream <c>Retirable</c>).</summary>
internal interface IRetirable
{
    /// <summary>Who offers the query (<c>QuerySpec.Owner</c>).</summary>
    object Owner { get; }

    /// <summary>Closes the query: its reads leave the index.</summary>
    void Close();
}

/// <summary>The index from a table's SQL name to the reads over it, the bindings of column sets, and the tables that are not ready (reads
/// over them are held: not run, their rows kept). One per store (upstream <c>Reads</c>).</summary>
internal sealed class Reads : IBindings
{
    /// <summary>A binding as the store keeps it: the table, and the object that owns it (which alone moves or drops it).</summary>
    private sealed record Binding(string Table, TableDefinition Definition, object Owner);

    private readonly Lock gate = new();
    private readonly Dictionary<string, HashSet<Read>> byTable = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Binding> sets = new(StringComparer.Ordinal);
    // The names each owner has bound: an activation asks for its own without walking every binding.
    private readonly Dictionary<object, List<string>> bound = new(ReferenceEqualityComparer.Instance);
    // The tables being migrated or not created yet; reads over them wait for Ready.
    private readonly HashSet<string> held = new(StringComparer.Ordinal);
    // Who is told when a set's binding or its table's readiness changes, by set name.
    private readonly Dictionary<string, List<Action>> watchers = new(StringComparer.Ordinal);
    // The open queries by who offers them (QuerySpec.Owner): closed when that one retires.
    private readonly Dictionary<object, List<IRetirable>> offered = new(ReferenceEqualityComparer.Instance);

    /// <summary>Enters <paramref name="read"/> under every table it uses: every commit writing one of them tests it from now on.</summary>
    internal void Add(Read read)
    {
        lock (gate)
        {
            foreach (var use in read.Plan.Uses)
            {
                if (!byTable.TryGetValue(use.Table, out var reads)) byTable[use.Table] = reads = new HashSet<Read>(ReferenceEqualityComparer.Instance);
                reads.Add(read);
            }
        }
    }

    /// <summary>Removes <paramref name="read"/>: no commit runs it again.</summary>
    internal void Delete(Read read)
    {
        lock (gate)
        {
            foreach (var use in read.Plan.Uses)
            {
                if (!byTable.TryGetValue(use.Table, out var reads)) continue;
                reads.Remove(read);
                if (reads.Count == 0) byTable.Remove(use.Table);
            }
        }
    }

    /// <summary><paramref name="query"/> is open: it closes when its owner retires.</summary>
    internal void Opened(IRetirable query)
    {
        lock (gate)
        {
            if (!offered.TryGetValue(query.Owner, out var queries)) offered[query.Owner] = queries = [];
            queries.Add(query);
        }
    }

    /// <summary><paramref name="query"/> was closed.</summary>
    internal void Closed(IRetirable query)
    {
        lock (gate)
        {
            if (!offered.TryGetValue(query.Owner, out var queries)) return;
            queries.RemoveAll(each => ReferenceEquals(each, query));
            if (queries.Count == 0) offered.Remove(query.Owner);
        }
    }

    /// <summary><paramref name="owner"/> offers its queries no more (a provider's attempt ended; slice 14 §3.2): their reads leave the
    /// index, so no commit runs a statement compiled for its tables as they were. Their readers keep their last values and read the
    /// successor's queries when it is live.</summary>
    internal void Retire(object owner)
    {
        IRetirable[] queries;
        lock (gate) queries = offered.TryGetValue(owner, out var open) ? [.. open] : [];
        foreach (var query in queries) query.Close();
    }

    /// <summary>The reads over <paramref name="table"/>, as they are now; null for none.</summary>
    internal IReadOnlyCollection<Read>? On(string table)
    {
        lock (gate) return byTable.TryGetValue(table, out var reads) ? [.. reads] : null;
    }

    /// <summary>The reads over a table with a <c>host(...)</c> column of host kind <paramref name="kind"/>: what a host data change runs
    /// again.</summary>
    internal IReadOnlyList<Read> OnHostKind(string kind)
    {
        lock (gate)
        {
            var found = new List<Read>();
            var seen = new HashSet<Read>(ReferenceEqualityComparer.Instance);
            foreach (var reads in byTable.Values)
                foreach (var read in reads)
                    if (UsesHostKind(read, kind) && seen.Add(read)) found.Add(read);
            return found;
        }
    }

    /// <inheritdoc/>
    public Bound? Get(string set)
    {
        lock (gate) return sets.TryGetValue(set, out var binding) ? new Bound(binding.Table, binding.Definition) : null;
    }

    /// <summary>Binds <paramref name="set"/> to <paramref name="table"/> for <paramref name="owner"/> (its table declares <c>Exposes</c>). A
    /// set bound by another owner to another table is refused (declaration policy: two tables exposing one name), naming both.</summary>
    internal void Bind(string set, Bound table, object owner)
    {
        lock (gate)
        {
            var current = sets.GetValueOrDefault(set);
            if (current is not null && !ReferenceEquals(current.Owner, owner) && current.Table != table.Table)
                throw new ArgumentException($"column set \"{set}\" is exposed by tables \"{current.Table}\" and \"{table.Table}\"");
            if (current is not null && current.Table == table.Table && ReferenceEquals(current.Definition, table.Definition) && ReferenceEquals(current.Owner, owner))
                return;
            if (current is not null && bound.TryGetValue(current.Owner, out var before)) before.Remove(set);
            sets[set] = new Binding(table.Table, table.Definition, owner);
            if (!bound.TryGetValue(owner, out var names)) bound[owner] = names = [];
            if (!names.Contains(set)) names.Add(set);
        }
        Changed(set);
    }

    /// <summary>Drops <paramref name="owner"/>'s binding of <paramref name="set"/> (its new code exposes it no more, or it ended).</summary>
    internal void Unbind(string set, object owner)
    {
        lock (gate)
        {
            if (!sets.TryGetValue(set, out var current) || !ReferenceEquals(current.Owner, owner)) return;
            sets.Remove(set);
            if (bound.TryGetValue(owner, out var names))
            {
                names.Remove(set);
                if (names.Count == 0) bound.Remove(owner);
            }
        }
        Changed(set);
    }

    /// <summary>The sets <paramref name="owner"/> has bound.</summary>
    internal IReadOnlyList<string> BoundBy(object owner)
    {
        lock (gate) return bound.TryGetValue(owner, out var names) ? [.. names] : [];
    }

    /// <summary>Marks <paramref name="table"/> not ready (its owner's successor creates or migrates it): reads over it are held.</summary>
    internal void Hold(string table)
    {
        lock (gate) held.Add(table);
    }

    /// <summary><paramref name="table"/> is ready again: the reads over the sets bound to it compile and run once.</summary>
    internal void Ready(string table)
    {
        string[] changed;
        lock (gate)
        {
            if (!held.Remove(table)) return;
            changed = [.. sets.Where(entry => entry.Value.Table == table).Select(entry => entry.Key)];
        }
        foreach (var set in changed) Changed(set);
    }

    /// <summary>Whether a read over <paramref name="table"/> must wait.</summary>
    internal bool IsHeld(string table)
    {
        lock (gate) return held.Contains(table);
    }

    /// <summary>Calls <paramref name="changed"/> whenever <paramref name="set"/>'s binding or its table's readiness changes, until the
    /// returned release.</summary>
    internal Action Watch(string set, Action changed)
    {
        lock (gate)
        {
            if (!watchers.TryGetValue(set, out var list)) watchers[set] = list = [];
            list.Add(changed);
        }
        return () =>
        {
            lock (gate)
            {
                if (!watchers.TryGetValue(set, out var current)) return;
                current.Remove(changed);
                if (current.Count == 0) watchers.Remove(set);
            }
        };
    }

    private void Changed(string set)
    {
        Action[] told;
        lock (gate) told = watchers.TryGetValue(set, out var list) ? [.. list] : [];
        foreach (var changed in told) changed();
    }

    // Called under the lock.
    private bool UsesHostKind(Read read, string kind)
    {
        var query = read.Plan.Query;
        if (query.Table.HostColumn?.Host?.Kind.Kind == kind) return true;
        // A joined or exists table with a host column of the kind: found through the bindings of the sets it names.
        var named = StoreInternal.SetsOf(query);
        return read.Plan.Uses.Any(use => use.Role != UseRole.Own && named.Any(set =>
            sets.TryGetValue(set, out var binding) && binding.Table == use.Table && binding.Definition.HostColumn?.Host?.Kind.Kind == kind));
    }
}

internal static partial class StoreInternal
{
    /// <summary>The names of the column sets <paramref name="query"/> reads besides its own table: the joined one and every
    /// <c>exists</c>' (upstream <c>setsOf</c>).</summary>
    internal static IReadOnlyList<string> SetsOf(Query query)
    {
        var sets = new List<string>();
        void Named(string set)
        {
            if (!sets.Contains(set)) sets.Add(set);
        }
        void Collect(WhereNode condition)
        {
            foreach (var exists in ExistsIn(condition))
            {
                Named(exists.Set);
                if (exists.Inner is not null) Collect(exists.Inner);
            }
        }
        if (query.Joined is not null) Named(query.Joined.Set.Name);
        if (query.Condition is not null) Collect(query.Condition);
        return sets;
    }
}

/// <summary>The refresh of a transaction's reads: <see cref="Write"/> runs in the commit step after the commit's own writes (the last write
/// of the last phase) and chooses the reads there, from the index at that moment; <see cref="Adopt"/> hands each run to its read, inside the
/// commit's batch. A refused step adopts nothing. A transaction stages it as its own change (upstream <c>ReadRefresh</c>).</summary>
internal sealed class ReadRefresh
{
    private readonly Reads reads;
    private readonly IReadOnlyList<string> tables;
    private readonly Func<IReadOnlyList<TableChange>> changes;
    private readonly ChordContext context;
    private readonly List<(Read Read, ReadRun Run)> runs = [];

    /// <param name="reads">The store's reads.</param>
    /// <param name="tables">The SQL names of the tables the transaction wrote.</param>
    /// <param name="changes">Gives its writes; asked only when a read over one of the tables exists in the step.</param>
    /// <param name="context">Counts the skips, probes and runs.</param>
    internal ReadRefresh(Reads reads, IReadOnlyList<string> tables, Func<IReadOnlyList<TableChange>> changes, ChordContext context)
    {
        this.reads = reads;
        this.tables = tables;
        this.changes = changes;
        this.context = context;
        Write = new StagedWrite(WriteOrder.Write, RefreshAsync);
    }

    /// <summary>The staged write that reads; it writes nothing and records nothing in the outbox.</summary>
    internal IStagedWrite Write { get; }

    private async Task<IReadOnlyList<StoreWrite>> RefreshAsync(Step step)
    {
        // A commit applied again (a rolled-back step replayed) counts only its last application's runs.
        runs.Clear();
        var chosen = new List<Read>();
        var seen = new HashSet<Read>(ReferenceEqualityComparer.Instance);
        foreach (var table in tables)
            foreach (var read in reads.On(table) ?? [])
                if (seen.Add(read)) chosen.Add(read);
        if (chosen.Count == 0) return [];
        var written = changes();
        foreach (var read in chosen)
        {
            // Held: a table it reads is being created or migrated; it runs once when the table is ready.
            if (read.Plan.Uses.Any(use => reads.IsHeld(use.Table))) continue;
            if (await StoreInternal.SkipsAsync(read, written, step, context).ConfigureAwait(false))
            {
                context.Count("chord.query.skips");
                continue;
            }
            var run = await read.RunAsync(step, context).ConfigureAwait(false);
            read.Ran(step.Transaction, run);
            runs.Add((read, run));
        }
        return [];
    }

    /// <summary>Hands each run to its read, inside the commit's batch.</summary>
    internal void Adopt()
    {
        foreach (var (read, run) in runs) read.Adopt(run);
    }

    /// <summary>The transaction committed.</summary>
    internal void Committed()
    {
    }

    /// <summary>The transaction was discarded.</summary>
    internal void Discarded()
    {
    }
}
