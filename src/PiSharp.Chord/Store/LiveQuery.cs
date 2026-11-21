// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/store/live-query.ts
//
// Live queries (decision "Live queries over an object's own tables"; slice 14 §3): the rows of a query as a JSON watchable, kept current by
// the store's reads. self.Query(table, q, limit) is the owner's own unnamed window (its value the rows); self.LiveQuery(namedQuery, args,
// first) reads a named query (its value { rows, more }). Both are this source over one open read; a commit's run is published in that
// commit's batch as the diff from the previous rows (W1). Mappings:
// - LiveTarget is a record of the spec and the decode function; LiveShape a closed record hierarchy. Upstream's rows shape carries a
//   `present(rows, more)` function; its two uses are the rows themselves and `{ rows, more }`, which here are LiveShape.Rows.WithMore false
//   and true, because the frame's diff must know where the rows are in the value.
// - Row identity. Upstream reuses the JavaScript object of a row whose stored columns did not change, so diffRevisions compares unchanged
//   rows by identity and a window shifted by one row is one insert and one removal. A JsonNode has one parent, so no node is shared between
//   two values here (State/Framed.cs, the differ contract): DecodedRows keeps each decoded row once, with a token that stands for its
//   identity, every value given to Set is built of copies, and Diff (the seam FramedSource leaves) computes the frame from the tokens of
//   the two values instead of walking them: unchanged rows cost one comparison, a changed row is diffed alone.
// - Threads: a live query belongs to its site's batch (the host); see OpenQuery.cs.
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Chord.Delta;
using PiSharp.Chord.State;

namespace PiSharp.Chord.Store;

/// <summary>What a live source reads: a query to keep open, and how a stored row of it becomes the value's row (decoded, projected,
/// mapped); a row the code cannot read throws (upstream <c>LiveTarget</c>).</summary>
/// <param name="Spec">The query to keep open.</param>
/// <param name="Decode">A stored row as the value's row: a node of its own, with no parent.</param>
internal sealed record LiveTarget(QuerySpec Spec, Func<IReadOnlyDictionary<string, object?>, ChordContext, JsonNode?> Decode);

/// <summary>What a live query reads and shows: the rows of a range (as they are, or as <c>{ rows, more }</c>), or a count (upstream
/// <c>LiveShape</c>).</summary>
internal abstract record LiveShape
{
    private LiveShape()
    {
    }

    /// <summary>The rows of <paramref name="Range"/>: the value is the array of rows, or <c>{ "rows": [...], "more": bool }</c> with
    /// <paramref name="WithMore"/>.</summary>
    internal sealed record Rows(ReadRange Range, bool WithMore = false) : LiveShape;

    /// <summary>The number of rows of the query's condition.</summary>
    internal sealed record Count : LiveShape;
}

internal static partial class StoreInternal
{
    /// <summary>Refuses what the types cannot (declaration policy): a query of another table than <paramref name="table"/>, and a
    /// <paramref name="limit"/> that is no positive integer (a live query must be bounded: one run reads at most <paramref name="limit"/>
    /// rows; upstream <c>checkLiveQuery</c>).</summary>
    internal static void CheckLiveQuery(TableDefinition table, Query query, long limit, string label)
    {
        if (!ReferenceEquals(query?.Table, table))
            throw DataInternal.Invalid("args", $"{label}: self.query: a query of table \"{query?.Table.Name}\" over \"{table.Name}\"");
        if (limit < 1 || limit > MaxSafeInteger)
            throw DataInternal.Invalid("args", $"{label}: self.query: a limit is required, a positive integer");
    }

    /// <summary>The ops that turn the rows <paramref name="before"/> into the rows <paramref name="after"/>, the array of them at
    /// <paramref name="path"/> of the value: rows with the same token are the same row (a splice or a move at most), a row that took
    /// another's place is diffed against it alone. Null when the rows are replaced whole (the caller replaces the value). The frame of a
    /// live query, and of anything else built of decoded rows (a list's pages joined).</summary>
    internal static ImmutableArray<Op>? DiffRows(IReadOnlyList<DecodedRow> before, IReadOnlyList<DecodedRow> after, ImmutableArray<PathSegment> path)
    {
        var oldIds = new JsonArray([.. before.Select(row => (JsonNode?)JsonValue.Create(row.Token))]);
        var newIds = new JsonArray([.. after.Select(row => (JsonNode?)JsonValue.Create(row.Token))]);
        var identities = DeltaDiff.DiffRevisions(oldIds, newIds);
        if (identities.IsEmpty) return [];
        var byToken = new Dictionary<long, DecodedRow>();
        foreach (var row in after) byToken[row.Token] = row;
        var working = before.ToList();
        var ops = ImmutableArray.CreateBuilder<Op>();
        foreach (var op in identities)
        {
            switch (op)
            {
                case ReplaceOp:
                    return null;
                case SpliceOp splice:
                {
                    List<DecodedRow> inserted = [.. splice.Items.Select(token => byToken[ChordJson.AsNumber(token) is { } number ? (long)number : 0])];
                    ops.Add(new SpliceOp(path, splice.Index, splice.Remove, [.. inserted.Select(row => row.Copy())]));
                    working.RemoveRange((int)splice.Index, (int)splice.Remove);
                    working.InsertRange((int)splice.Index, inserted);
                    break;
                }
                case MoveOp move:
                {
                    ops.Add(new MoveOp(path, move.Permutation));
                    var old = working.ToArray();
                    working = [.. move.Permutation.Select(index => old[index])];
                    break;
                }
                case SetOp { Path: [{ IsIndex: true } at] } set:
                {
                    var index = (int)at.Index;
                    var next = byToken[ChordJson.AsNumber(set.Value) is { } number ? (long)number : 0];
                    foreach (var rowOp in DeltaDiff.DiffRevisions(working[index].Row, next.Row)) ops.Add(AtRow(path.Add(index), rowOp));
                    working[index] = next;
                    break;
                }
                default:
                    // Number tokens give only replacements, root splices and moves, and sets at an index.
                    throw new InvalidOperationException("Unexpected row identity delta.");
            }
        }
        return ops.ToImmutable();
    }

    /// <summary><paramref name="op"/>, an op of one row, as an op of the value that holds the row at <paramref name="at"/>.</summary>
    private static Op AtRow(ImmutableArray<PathSegment> at, Op op) => op switch
    {
        ReplaceOp replace => new SetOp(at, replace.Value),
        SetOp set => new SetOp([.. at, .. set.Path], set.Value),
        DeleteOp delete => new DeleteOp([.. at, .. delete.Path]),
        AppendOp append => new AppendOp([.. at, .. append.Path], append.Text),
        TruncateOp truncate => new TruncateOp([.. at, .. truncate.Path], truncate.Count),
        SpliceOp splice => new SpliceOp([.. at, .. splice.Path], splice.Index, splice.Remove, splice.Items),
        MoveOp move => new MoveOp([.. at, .. move.Path], move.Permutation),
        _ => throw new InvalidOperationException("Unknown row delta."),
    };
}

/// <summary>A live query as a JSON source (a state member, a derive source, an <c>on</c> source): the rows of one range of a query, or its
/// count. <see cref="ReadAsync"/> gives it the query; a later one (arguments that follow a value, a provider's successor) shows the
/// previous value until the new read's first run is adopted, then switches in one frame (upstream <c>LiveQuery</c>).</summary>
internal sealed class LiveQuery : FramedSource
{
    private static readonly ImmutableArray<PathSegment> RowsPath = ["rows"];

    private readonly ReadSite site;
    private readonly LiveShape shape;
    private readonly DecodedRows rows = new();
    // The decoded rows of each value it was given, by the value: what Diff reads instead of the trees (State/Framed.cs, point 3).
    private readonly ConditionalWeakTable<JsonNode, Shown> shown = new();
    // The read its value is of, and the one that replaces it once its first run is adopted.
    private Reading? current;
    private Reading? next;
    // Counts the reads its value has been of: the rows of two reads share no decoded row.
    private long reads;

    /// <summary>One of a live query's reads, open, with the query it is of and what it reads (upstream <c>Reading</c>).</summary>
    private sealed class Reading(OpenQuery query, LiveTarget target)
    {
        internal OpenQuery Query => query;

        internal LiveTarget Target => target;

        internal OpenRead Read { get; set; } = null!;
    }

    /// <summary>What a value shows: its rows as they were decoded, whether more followed, and which of its reads it is of.</summary>
    private sealed record Shown(IReadOnlyList<DecodedRow> Rows, bool More, long Read);

    internal LiveQuery(ReadSite site, IFramedOwner owner, LiveShape shape)
        : base(site.Batch, owner)
    {
        this.site = site;
        this.shape = shape;
    }

    /// <summary>The first run of the owner's own window, before its object goes live (activation, after its tables are prepared): a
    /// failure throws and fails the activation, which reports it.</summary>
    internal async Task LoadAsync(LiveTarget target, ChordContext context)
    {
        Quiet();
        // No ConfigureAwait(false): its state is the host's, and the failure is read where the host runs.
        await ReadAsync(target, context);
        if (Failure is { } failure) ExceptionDispatchInfo.Throw(failure);
    }

    /// <summary>Reads <paramref name="target"/> from now on (its shape's range of it, or its count). Completes with its first run once
    /// adopted (null: a commit's run came first, a table it reads is held, or the query was detached or given another read meanwhile).</summary>
    internal Task<ReadRun?> ReadAsync(LiveTarget target, ChordContext context)
    {
        if (Detached) return Task.FromResult<ReadRun?>(null);
        next?.Query.Close();
        Reading? reading = null;
        // A binding changed: its one read runs again (a failure is a failed run handed to it; nothing awaits the run).
        var query = new OpenQuery(site, target.Spec, shape is LiveShape.Count ? ReadShape.Count : ReadShape.Rows, () => _ = reading!.Read.LoadAsync(), context);
        reading = new Reading(query, target);
        ReadKind what = shape is LiveShape.Rows window ? new ReadKind.Rows(window.Range) : new ReadKind.Count();
        var opened = reading;
        reading.Read = query.Read(what, new ReadSink(run => Adopt(opened, run)));
        next = reading;
        return reading.Read.LoadAsync();
    }

    /// <summary>What it should read cannot be read (its arguments threw, its provider refused them): a failed member, as a failed run makes
    /// it. Its reads close: neither the read of the previous arguments nor a pending one may publish or heal the failure; the next
    /// <see cref="ReadAsync"/> does.</summary>
    internal void Fail(Exception error, ChordContext context)
    {
        if (Detached) return;
        Close();
        Batch.Within(() => FailWith(error), context);
    }

    /// <summary>Its attempt ended: no commit runs it again, reads throw <paramref name="error"/> (the object's), its watches end
    /// <c>ended</c>.</summary>
    internal void Detach(Exception error)
    {
        Close();
        End(error);
    }

    private void Close()
    {
        current?.Query.Close();
        next?.Query.Close();
        current = null;
        next = null;
    }

    /// <summary>Takes a run of one of its reads, inside a batch. The first run of a new read makes it the current one. A failed run, and a
    /// row the code cannot read, make the query a failed member.</summary>
    private void Adopt(Reading reading, ReadRun run)
    {
        if (Detached) return;
        if (ReferenceEquals(reading, next))
        {
            current?.Query.Close();
            current = reading;
            next = null;
            rows.Reset();
            reads++;
        }
        else if (!ReferenceEquals(reading, current)) return;
        switch (run)
        {
            case ReadRun.Failed failed:
                FailWith(failed.Error);
                break;
            case ReadRun.Count count:
                Set(JsonValue.Create(count.Number));
                break;
            case ReadRun.Rows found:
                Present(reading, found.Result);
                break;
        }
    }

    /// <summary>The value of a run's rows; a row the code cannot read fails the member.</summary>
    private void Present(Reading reading, ReadResult result)
    {
        // A count's read returns counts.
        if (shape is not LiveShape.Rows window) return;
        try
        {
            var decoded = rows.Decode(result.Rows, reading.Target, reading.Query.Context);
            var items = new JsonArray([.. decoded.Select(row => row.Copy())]);
            JsonNode value = window.WithMore ? new JsonObject { ["rows"] = items, ["more"] = result.More } : items;
            shown.Add(value, new Shown(decoded, result.More, reads));
            Set(value);
        }
        catch (Exception error)
        {
            FailWith(error);
        }
    }

    /// <summary>The frame from the value its watches have to the one the batch left: by the rows' tokens when both are rows of one of its
    /// reads. Across two reads (another target decodes its own way) no row is shared, and the values are compared as upstream compares
    /// them then, structurally.</summary>
    protected override ImmutableArray<Op> Diff(JsonNode? before, JsonNode? after)
    {
        if (shape is not LiveShape.Rows window || before is null || after is null || !shown.TryGetValue(before, out var was) ||
            !shown.TryGetValue(after, out var now) || was.Read != now.Read)
            return base.Diff(before, after);
        if (StoreInternal.DiffRows(was.Rows, now.Rows, window.WithMore ? RowsPath : []) is not { } ops) return [new ReplaceOp(after)];
        return window.WithMore && was.More != now.More ? ops.Add(new SetOp(["more"], JsonValue.Create(now.More))) : ops;
    }
}

/// <summary>A row as a run decoded it, with the stored row it came from (upstream <c>Decoded</c>). <paramref name="Token"/> stands for the
/// row's identity: upstream's is the row object itself, which a value here cannot share (a JsonNode has one parent).</summary>
/// <param name="Stored">The stored row.</param>
/// <param name="Row">The decoded row: kept here, never given out; a value gets <see cref="Copy"/>.</param>
/// <param name="Token">The identity of this decoding.</param>
internal sealed record DecodedRow(IReadOnlyDictionary<string, object?> Stored, JsonNode? Row, long Token)
{
    /// <summary>A copy of the row for a value or a frame.</summary>
    internal JsonNode? Copy() => Row?.DeepClone();
}

/// <summary>The decoded rows of a live source's last value, by key: a row whose stored columns are all the same in the next run is that
/// value's row (one comparison per selected column), any other is decoded. So a frame, the diff from the value the watches had, compares
/// unchanged rows by identity (a window shifted by one row: one insert and one removal, not a comparison of every row with every other;
/// upstream <c>DecodedRows</c>).</summary>
internal sealed class DecodedRows
{
    private static long tokens;

    private Dictionary<string, DecodedRow> byKey = new(StringComparer.Ordinal);

    /// <summary>The rows of <paramref name="stored"/> as <paramref name="target"/> decodes them, unchanged ones reused; a row the code
    /// cannot read throws.</summary>
    internal List<DecodedRow> Decode(IReadOnlyList<IReadOnlyDictionary<string, object?>> stored, LiveTarget target, ChordContext context)
    {
        var key = target.Spec.Query.Table.Key;
        var decoded = new List<DecodedRow>(stored.Count);
        var next = new Dictionary<string, DecodedRow>(StringComparer.Ordinal);
        foreach (var each in stored)
        {
            var id = StoreInternal.Identity(key, each);
            var row = byKey.TryGetValue(id, out var before) && SameStored(before.Stored, each)
                ? before
                : new DecodedRow(each, target.Decode(each, context), Interlocked.Increment(ref tokens));
            decoded.Add(row);
            next[id] = row;
        }
        byKey = next;
        return decoded;
    }

    /// <summary>Forgets the last value's rows: another target decodes its own way, nothing of the previous one is reused.</summary>
    internal void Reset() => byKey = new Dictionary<string, DecodedRow>(StringComparer.Ordinal);

    /// <summary>Whether two stored rows of one statement have the same value in every column.</summary>
    private static bool SameStored(IReadOnlyDictionary<string, object?> a, IReadOnlyDictionary<string, object?> b)
    {
        foreach (var (column, value) in a)
            if (!b.TryGetValue(column, out var other) || !Equals(value, other)) return false;
        return true;
    }
}
