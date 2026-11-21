// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/host/list.ts
//
// Lists (slice 14 §4): self.List(name, sources, options), a named query a screen loads more of. Its value is { rows, moreAfter }: the rows
// of its pages joined in order (Tiling.cs). Loaded rows stay live, "load more" sends only the new rows, and a row that moves (a list by
// last touch) leaves one page and enters another in the same commit: no gap, no duplicate. The object keeps the bookmarks across its
// attempts. Mappings:
// - The class is ListSource: List is System.Collections.Generic's.
// - The frame. Upstream's rows are the same JavaScript objects from one value to the next, so diffRevisions gives a narrow diff. A
//   JsonNode has one parent (State/Framed.cs): every value is built of copies of the decoded rows, and Diff computes the frame from the
//   rows' tokens (StoreInternal.DiffRows), as a live query does. The rows of two tilings share no decoded row; that one frame is the
//   structural diff of the two values, which sends nothing when the rows are equal.
// - Upstream chains its `more` calls on a promise and waits on a promise for the switch of tilings. Here the calls are chained as tasks on
//   the host's dispatcher (the `run` it is given), where the list lives; a caller on another thread is told there.
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Chord.Delta;
using PiSharp.Chord.State;
using PiSharp.Chord.Store;

namespace PiSharp.Chord.Host;

/// <summary>What an object keeps of a list across its attempts (a reload, a restart): the bookmarks, whether the last page was open at
/// its end, and the order they are positions of. It goes with the object (upstream <c>ListMemory</c>).</summary>
internal sealed class ListMemory
{
    internal string Signature { get; set; } = "";

    internal IReadOnlyList<IReadOnlyList<object?>> Bookmarks { get; set; } = [];

    internal bool Open { get; set; } = true;
}

/// <summary>A list as a JSON source, read as a reader reads (<see cref="IReading"/>): each read opens every page again under its bookmark,
/// for the new target, and the list switches in one frame once each has run; bookmarks of another order start it again with a first page
/// (upstream <c>List</c>).</summary>
internal sealed class ListSource : FramedSource, IReading, ITilingOwner
{
    private static readonly ImmutableArray<PathSegment> RowsPath = ["rows"];

    private readonly ReadSite site;
    private readonly long first;
    private readonly ListMemory memory;
    private readonly Func<Func<Task>, Task> run;
    private readonly DecodedRows rows = new();
    // The decoded rows of each value it was given, by the value: what Diff reads instead of the trees (State/Framed.cs, point 3).
    private readonly ConditionalWeakTable<JsonNode, Shown> shown = new();
    // The tiling its value is of, and the one that replaces it once every page of it ran.
    private Tiling? current;
    private Tiling? next;
    // Counts the tilings its value has been of: the rows of two tilings share no decoded row.
    private long tilings;
    // The MoreAsync calls in turn: one at a time per list.
    private Task loading = Task.CompletedTask;
    // Completed when the list has another current tiling, or none (a switch, a failure, its end).
    private TaskCompletionSource switched = NewSignal();

    /// <summary>What a value shows: its rows as they were decoded, whether rows may follow, and which of its tilings it is of.</summary>
    private sealed record Shown(IReadOnlyList<DecodedRow> Rows, bool MoreAfter, long Tiling);

    /// <param name="site">Where it lives.</param>
    /// <param name="owner">Its object, reporting failures under the query's name.</param>
    /// <param name="first">The rows of a page.</param>
    /// <param name="memory">What its object keeps of it.</param>
    /// <param name="run">Runs work on the site's batch's thread (the host's dispatcher), later, in the order given.</param>
    internal ListSource(ReadSite site, IFramedOwner owner, long first, ListMemory memory, Func<Func<Task>, Task> run)
        : base(site.Batch, owner)
    {
        this.site = site;
        this.first = first;
        this.memory = memory;
        this.run = run;
    }

    FramedSource IReading.Source => this;

    /// <summary>Reads <paramref name="target"/> from now on: its pages open under the bookmarks the list has (when they are positions of
    /// its order: another order starts again with a first page). The list shows its previous rows until every page of the new tiling ran,
    /// then switches in one frame. Completes once every page's load settled.</summary>
    public Task ReadAsync(LiveTarget target, ChordContext context)
    {
        if (Detached) return Task.CompletedTask;
        next?.Close();
        var tiling = new Tiling(site, target, first, this, context);
        next = tiling;
        var kept = memory.Signature == tiling.Signature;
        return tiling.StartAsync(kept ? memory.Bookmarks : [], !kept || memory.Open);
    }

    /// <summary>What it should read cannot be read (its arguments threw, its provider refused them): a failed member with its last rows.
    /// Its pages close: neither the previous target's pages nor a pending tiling may publish or heal the failure; the next
    /// <see cref="ReadAsync"/> does.</summary>
    public void Fail(Exception error, ChordContext context)
    {
        if (Detached) return;
        Close();
        Batch.Within(() => FailWith(error), context);
    }

    /// <summary>Its attempt ended: no commit runs its pages again, reads throw <paramref name="error"/>, its watches end.</summary>
    public void Detach(Exception error)
    {
        Close();
        End(error);
    }

    /// <summary>Loads one more page of <c>first</c> rows behind the last one; calls run one at a time. A call waits while the list is
    /// about to show other pages, and loads behind those: while a tiling is pending (new arguments, a changed binding; its pages may not
    /// have run yet: a table they read is held) and while the provider restarts (its query is closed, its successor's not read yet:
    /// nothing is read with the old code's statement). The caller's wait ends with <paramref name="context"/>; the load goes on.</summary>
    internal Task MoreAsync(ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var loaded = run(() =>
        {
            var mine = LoadMoreAsync(loading);
            // A failed load is its caller's; the next call runs after it either way.
            loading = mine.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return mine;
        });
        return context.AbortSignal is { CanBeCanceled: true } signal ? loaded.WaitAsync(signal) : loaded;
    }

    private async Task LoadMoreAsync(Task previous)
    {
        await previous;
        while (!Detached && (next is not null || current?.Retired == true)) await switched.Task;
        if (current is { } tiling) await tiling.MoreAsync();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Close()
    {
        current?.Close();
        next?.Close();
        next = null;
        Show(null);
    }

    // The tiling is the current one from now on; the MoreAsync calls waiting for another one go on.
    private void Show(Tiling? tiling)
    {
        current = tiling;
        var before = switched;
        switched = NewSignal();
        before.TrySetResult();
    }

    /// <summary>A binding of <paramref name="tiling"/>'s query changed: its target is read again as one new tiling under the list's
    /// bookmarks, and the list switches in one frame once every page of it ran, as for new arguments. A current tiling that a pending one
    /// is about to replace is left alone: the pending one reads again for itself.</summary>
    public void Rebound(Tiling tiling)
    {
        if (Detached) return;
        var pending = next;
        // A failed page fails the list, as any read's failure does; nothing awaits the loads.
        if (ReferenceEquals(tiling, pending) || (ReferenceEquals(tiling, current) && pending is null)) _ = ReadAsync(tiling.Target, tiling.Context);
    }

    /// <summary>A run of <paramref name="tiling"/> was adopted, inside a batch. A pending tiling becomes the current one once every page
    /// of it ran: never a first page alone, never rows of two targets. A failed page of it fails the list at once, and the previous
    /// target's pages close: they may not heal that failure. The current tiling's run makes the list's value, and its bookmarks are what
    /// the object keeps.</summary>
    public void Adopted(Tiling tiling)
    {
        if (Detached) return;
        if (ReferenceEquals(tiling, next))
        {
            if (tiling.Failure is null && !tiling.Complete) return;
            current?.Close();
            next = null;
            Show(tiling);
            rows.Reset();
            tilings++;
        }
        else if (!ReferenceEquals(tiling, current)) return;
        if (tiling.Failure is { } failure)
        {
            FailWith(failure);
            return;
        }
        // A page of the current tiling that has not run (its table was held when the list opened): no value yet.
        if (!Ready && !tiling.Complete) return;
        memory.Signature = tiling.Signature;
        memory.Bookmarks = tiling.Bookmarks;
        memory.Open = tiling.Open;
        try
        {
            var decoded = rows.Decode(tiling.Rows(), tiling.Target, tiling.Context);
            var value = new JsonObject { ["rows"] = new JsonArray([.. decoded.Select(row => row.Copy())]), ["moreAfter"] = tiling.MoreAfter };
            shown.Add(value, new Shown(decoded, tiling.MoreAfter, tilings));
            Set(value);
        }
        catch (Exception error)
        {
            FailWith(error);
        }
    }

    /// <summary>The frame from the value its watches have to the one the batch left: by the rows' tokens when both are rows of one of its
    /// tilings. Across two tilings (another target decodes its own way) no row is shared, and the values are compared structurally.</summary>
    protected override ImmutableArray<Op> Diff(JsonNode? before, JsonNode? after)
    {
        if (before is null || after is null || !shown.TryGetValue(before, out var was) || !shown.TryGetValue(after, out var now) || was.Tiling != now.Tiling)
            return base.Diff(before, after);
        if (StoreInternal.DiffRows(was.Rows, now.Rows, RowsPath) is not { } ops) return [new ReplaceOp(after)];
        return was.MoreAfter != now.MoreAfter ? ops.Add(new SetOp(["moreAfter"], JsonValue.Create(now.MoreAfter))) : ops;
    }
}
