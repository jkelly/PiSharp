// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/host/tiling.ts
//
// A list's pages (slice 14 §4): the tiling of a query's order between fixed bookmarks. Page 1 is (start, b1], page 2 (b1, b2], and the
// last page is open at its end while no row is known behind it. Each page is one read of the open query; pages are cut and merged when
// their runs are adopted, from the rows just read, with no I/O, so no frame loses a loaded row. Mappings:
// - A position is the values of the order's columns as the store returned them (a string, a long or a double); two are ordered as SQLite
//   orders them (text by code point, numbers numerically).
// - Threads: a tiling belongs to its site's batch, that is to the host's dispatcher (Store/OpenQuery.cs): it is made, started, told of
//   adoptions and closed there.
using PiSharp.Chord.Store;

namespace PiSharp.Chord.Host;

/// <summary>Who a tiling tells: its list (upstream <c>TilingOwner</c>).</summary>
internal interface ITilingOwner
{
    /// <summary>A run of <paramref name="tiling"/> was adopted; called inside a batch.</summary>
    void Adopted(Tiling tiling);

    /// <summary>A binding of its query changed (a set moved, its table ready after a migration): rows and their order may be others in
    /// every page. Its pages are not read again one by one (the first adoption would cut and merge the others with the rows they had); its
    /// list reads the target again as one new tiling.</summary>
    void Rebound(Tiling tiling);
}

/// <summary>The pages of a list for one target (a query and its arguments): one open query, a read of it per page, the rows each returned,
/// and the tiling rules (upstream <c>Tiling</c>).</summary>
internal sealed class Tiling
{
    /// <summary>How many rows a bounded page reads at most, in pages (slice 14 §4 "Overflow").</summary>
    internal const int MaxPages = 4;

    private readonly OpenQuery query;
    private readonly long first;
    private readonly Order order;
    private readonly ITilingOwner owner;
    private List<Page> pages = [];

    /// <summary>One page: the rows after <c>After</c> through <c>Through</c> (the last page has none: it is open at its end).</summary>
    private sealed class Page(IReadOnlyList<object?>? after, IReadOnlyList<object?>? through)
    {
        internal IReadOnlyList<object?>? After => after;

        internal IReadOnlyList<object?>? Through => through;

        internal OpenRead Read { get; set; } = null!;

        /// <summary>Its rows as its last run (or the cut that made it) left them; null until its first run.</summary>
        internal IReadOnlyList<IReadOnlyDictionary<string, object?>>? Rows { get; set; }

        /// <summary>Whether rows of its range are unread behind <see cref="Rows"/>: its run was cut by its limit.</summary>
        internal bool More { get; set; }

        /// <summary>Why its last run could not read it, until a run of it succeeds.</summary>
        internal Exception? Failure { get; set; }
    }

    internal Tiling(ReadSite site, LiveTarget target, long first, ITilingOwner owner, ChordContext context)
    {
        Target = target;
        query = new OpenQuery(site, target.Spec, ReadShape.Rows, () => owner.Rebound(this), context);
        this.first = first;
        order = target.Spec.Query.Order;
        this.owner = owner;
    }

    internal LiveTarget Target { get; }

    /// <summary>What bookmarks of this tiling are positions of: the table, the order's columns and its direction.</summary>
    internal string Signature => StoreInternal.SqliteJson([Target.Spec.Table, string.Join("\u0000", order.Columns), order.Direction == OrderDirection.Asc ? "asc" : "desc"]);

    /// <summary>The bookmarks: the upper bound of every bounded page.</summary>
    internal IReadOnlyList<IReadOnlyList<object?>> Bookmarks => [.. pages.Where(page => page.Through is not null).Select(page => page.Through!)];

    /// <summary>Whether the last page is open at its end: no row is known behind what the list shows.</summary>
    internal bool Open => pages.Count == 0 || pages[^1].Through is null;

    /// <summary>Whether rows may follow what <see cref="Rows"/> gives: a bounded last page, or a page that has not run yet.</summary>
    internal bool MoreAfter => !Open || !Complete;

    /// <summary>Whether every page has run.</summary>
    internal bool Complete => pages.All(page => page.Rows is not null);

    /// <summary>The failure of the first page whose last run failed: the list is failed while any page is.</summary>
    internal Exception? Failure => pages.FirstOrDefault(page => page.Failure is not null)?.Failure;

    /// <summary>Whether its query was closed under it: its provider's attempt ended (slice 14 §3.2). It keeps its rows and loads nothing;
    /// the list reads the successor's query next.</summary>
    internal bool Retired => query.Closed;

    /// <summary>The context its rows are decoded under: its query's.</summary>
    internal ChordContext Context => query.Context;

    /// <summary>The loaded rows in order, up to the first page that has none yet: a prefix, never a hole.</summary>
    internal List<IReadOnlyDictionary<string, object?>> Rows()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var page in pages)
        {
            if (page.Rows is null) break;
            rows.AddRange(page.Rows);
        }
        return rows;
    }

    /// <summary>Opens a page per bookmark, and the open last page when the list ended in one, and completes once each load settled. No
    /// bookmarks: one open page, the first.</summary>
    internal Task StartAsync(IReadOnlyList<IReadOnlyList<object?>> bookmarks, bool open)
    {
        IReadOnlyList<object?>? after = null;
        foreach (var through in bookmarks)
        {
            pages.Add(NewPage(after, through));
            after = through;
        }
        if (open || bookmarks.Count == 0) pages.Add(NewPage(after, null));
        return Task.WhenAll(pages.ToArray().Select(page => page.Read.LoadAsync()));
    }

    /// <summary>Loads the next page behind a bounded last page; with an open last page there is nothing more to load.</summary>
    internal Task MoreAsync()
    {
        if (pages.Count == 0 || pages[^1].Through is not { } through) return Task.CompletedTask;
        var page = NewPage(through, null);
        pages.Add(page);
        return page.Read.LoadAsync();
    }

    /// <summary>Closes its query: no commit runs its pages again.</summary>
    internal void Close()
    {
        query.Close();
        pages = [];
    }

    // A page of (after, through] that has not run: its read is registered, a load or a commit runs it.
    private Page NewPage(IReadOnlyList<object?>? after, IReadOnlyList<object?>? through)
    {
        var page = new Page(after, through);
        page.Read = query.Read(new ReadKind.Rows(Range(after, through)), new ReadSink(run => Adopt(page, run)));
        return page;
    }

    // A page of (after, through] whose rows are known (a cut, a merge): its read runs nothing.
    private Page PageOf(IReadOnlyList<object?>? after, IReadOnlyList<object?>? through, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var page = new Page(after, through) { Rows = rows };
        page.Read = query.ReadKnown(Range(after, through), rows, new ReadSink(run => Adopt(page, run)));
        return page;
    }

    // The range a page of (after, through] reads: the open last page one page; a bounded one everything between its bookmarks, up to the
    // overflow bound.
    private ReadRange Range(IReadOnlyList<object?>? after, IReadOnlyList<object?>? through) =>
        new(through is null ? first : first * MaxPages, after, through);

    // Takes a page's run, inside a batch. A run belongs to the interval it read, not to a page (slice 14 §4): the runs of one commit are
    // all computed before any is adopted, and an earlier adoption may have cut or merged the page meanwhile. The run then applies to
    // whatever pages cover its interval now.
    private void Adopt(Page page, ReadRun run)
    {
        var current = pages.Contains(page);
        if (run is not ReadRun.Rows found)
        {
            // A page's read is never a count. The pages that show the interval are failed until a run of them succeeds; a page that did not
            // run itself forgets its result, so the next commit over its tables runs it.
            var failure = run is ReadRun.Failed failed ? failed.Error : new InvalidOperationException("a list's page took a count");
            foreach (var each in current ? [page] : pages.Where(other => Overlap(other, page)).ToArray())
            {
                each.Failure = failure;
                each.Read.Forget();
            }
            owner.Adopted(this);
            return;
        }
        var (rows, more) = (found.Result.Rows, found.Result.More);
        if (current)
        {
            page.Rows = rows;
            page.More = more;
            page.Failure = null;
        }
        else Patch(page, rows, more);
        Retile();
        owner.Adopted(this);
    }

    // Applies the run of a retired page (the rows of its interval; more: the run was cut by its limit, so the interval is known only
    // through its last row) to the pages that cover the interval now. Each patched page's read is told its rows again: the skip test
    // reasons from the keys of what the page shows. A failed page takes the rows and stays without a result, so the next commit over its
    // tables runs it, whatever it writes. A page that lies behind what a cut run knows is loaded again; one the cut falls into keeps the
    // rows up to it, and Retile continues behind.
    private void Patch(Page retired, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, bool more)
    {
        var known = more ? Position(rows[^1]) : null;
        var patched = new List<Page>(pages.Count);
        foreach (var page in pages)
        {
            if (page.Rows is null || !Overlap(page, retired))
            {
                patched.Add(page);
                continue;
            }
            if (known is not null && page.After is not null && Compare(page.After, known) >= 0)
            {
                page.Read.Close();
                var again = NewPage(page.After, page.Through);
                _ = again.Read.LoadAsync();
                patched.Add(again);
                continue;
            }
            var kept = page.Rows.Where(row => !Inside(Position(row), retired.After, retired.Through));
            var taken = rows.Where(row => Inside(Position(row), page.After, page.Through));
            List<IReadOnlyDictionary<string, object?>> merged = [.. kept, .. taken];
            // A stable sort: rows of one position keep the order they were read in.
            merged = [.. merged.Select((row, index) => (row, index)).OrderBy(each => each, Comparer<(IReadOnlyDictionary<string, object?> Row, int Index)>.Create((a, b) =>
            {
                var compared = Compare(Position(a.Row), Position(b.Row));
                return compared != 0 ? compared : a.Index.CompareTo(b.Index);
            })).Select(each => each.row)];
            var cut = known is not null && (page.Through is null || Compare(page.Through, known) > 0);
            page.Rows = cut ? [.. merged.Where(row => Compare(Position(row), known!) <= 0)] : merged;
            page.More = page.More || cut;
            if (page.Failure is null) page.Read.Know(page.Rows);
            patched.Add(page);
        }
        pages = patched;
    }

    // Cuts and merges, from the rows the pages hold, with no I/O. The open last page with a row behind it, or grown past one page, gets
    // its bound at its last row (the list ends there until more); a bounded page past the overflow bound is cut and the rest of its range
    // loads next; a page of more than two pages' rows is cut into pages; neighbours with at most one page's rows together become one.
    private void Retile()
    {
        var size = (int)Math.Min(first, int.MaxValue / 2);
        var tiled = new List<Page>();
        foreach (var page in pages)
        {
            var rows = page.Rows;
            var open = page.Through is null;
            var full = rows is not null && (page.More || rows.Count > (open ? first : 2 * first));
            if (rows is null || page.Failure is not null || !full)
            {
                tiled.Add(page);
                continue;
            }
            page.Read.Close();
            // Rows of a bounded page's range are still unread behind its rows (overflow): the rest loads as a page of its own.
            var unread = !open && page.More;
            var through = open ? Position(rows[^1]) : page.Through;
            var after = page.After;
            for (var from = 0; from < rows.Count; from += size)
            {
                // The last cut takes what is left when that is at most two pages' rows, and ends where the page did.
                var last = rows.Count - from <= 2 * first;
                List<IReadOnlyDictionary<string, object?>> chunk = [.. last ? rows.Skip(from) : rows.Skip(from).Take(size)];
                var end = last && !unread ? through : Position(chunk[^1]);
                tiled.Add(PageOf(after, end, chunk));
                after = end;
                if (last) break;
            }
            if (unread)
            {
                var rest = NewPage(after, page.Through);
                tiled.Add(rest);
                _ = rest.Read.LoadAsync();
            }
        }
        pages = Merged(tiled);
    }

    // The pages with neighbours that hold at most one page's rows together merged into one.
    private List<Page> Merged(List<Page> tiled)
    {
        var merged = new List<Page>();
        foreach (var page in tiled)
        {
            var previous = merged.Count == 0 ? null : merged[^1];
            if (previous is null || !Mergeable(previous) || !Mergeable(page))
            {
                merged.Add(page);
                continue;
            }
            List<IReadOnlyDictionary<string, object?>> rows = [.. previous.Rows!, .. page.Rows!];
            if (rows.Count > first)
            {
                merged.Add(page);
                continue;
            }
            previous.Read.Close();
            page.Read.Close();
            merged[^1] = PageOf(previous.After, page.Through, rows);
        }
        return merged;
    }

    // Whether a page may merge with a neighbour: every row of its range is known.
    private static bool Mergeable(Page page) => page.Rows is not null && !page.More && page.Failure is null;

    // Whether the intervals of two pages share a position.
    private bool Overlap(Page a, Page b) => !Before(a, b) && !Before(b, a);

    private bool Before(Page x, Page y) => x.Through is not null && y.After is not null && Compare(x.Through, y.After) <= 0;

    // Whether a position is in the interval (after, through].
    private bool Inside(IReadOnlyList<object?> position, IReadOnlyList<object?>? after, IReadOnlyList<object?>? through)
    {
        if (after is not null && Compare(position, after) <= 0) return false;
        return through is null || Compare(position, through) <= 0;
    }

    // The position of a row in the order: its order columns' values.
    private List<object?> Position(IReadOnlyDictionary<string, object?> row) => [.. order.Columns.Select(name => row[name])];

    // Orders two positions as the list shows them: negative when a comes first.
    private int Compare(IReadOnlyList<object?> a, IReadOnlyList<object?> b)
    {
        for (var index = 0; index < a.Count; index++)
        {
            // Order columns are non-opt str or int ones (checked when the query was built).
            var compared = CompareStored(a[index], b[index]);
            if (compared != 0) return order.Direction == OrderDirection.Asc ? compared : -compared;
        }
        return 0;
    }

    // Orders two stored cells of one column as SQLite does: text by code point, numbers numerically.
    private static int CompareStored(object? a, object? b)
    {
        if (a is string text) return Math.Sign(Tables.CompareText(text, (string)b!));
        return StoreInternal.SqliteNumber(a!).CompareTo(StoreInternal.SqliteNumber(b!));
    }
}
