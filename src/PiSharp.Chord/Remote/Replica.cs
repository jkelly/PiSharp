// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/remote/replica.ts.
// 1.6.0: a replica counts the values it took (Generation) and can be forked for another remote object of the same reference (decision
// "References share one wire hold").
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Chord.Delta;
using PiSharp.Chord.State;

namespace PiSharp.Chord.Remote;

internal sealed record MemberSnapshot(JsonNode? Value, Exception? Error = null);
internal sealed class Replica(Batch batch, MemberSnapshot initial) : Source(batch)
{
    private JsonNode? value = initial.Value;
    private Exception? error = initial.Error;
    private bool hasLast = initial.Error is null, ended, retired, retiredWhileFailed;
    private ImmutableArray<Op> ops = [];
    private WireDeltaDecoder decoder = new();
    // How many values it took (its first value included): a reference joining a shared remote object sees a member it found failed or
    // missing once this passes the count it found.
    private long generation = initial.Error is null ? 1 : 0;
    /// <summary>How many values it took: one per adoption (a snapshot's, a change's, a recovery's).</summary>
    internal long Generation => generation;
    /// <summary>A copy of it for another remote object of the same reference (a private retry after <c>too_large/part</c>): its state,
    /// last good value and count, a fresh decoder (that object's next snapshot resets it).</summary>
    internal Replica Fork(Batch other) => new(other, new MemberSnapshot(null))
    { value = value, error = error, hasLast = hasLast, ended = ended, generation = generation };
    public override JsonNode? Value => error is null ? value : throw error;
    public override WatchStart Start() => error is null ? WatchStart.Of(value) : WatchStart.Failure(error, hasLast, value);
    internal (JsonNode? Value, ImmutableArray<Op> Ops, WireDeltaDecoder Decoder) Prepare(JsonNode? wire)
    {
        if (ended) throw error!;
        var staged = decoder.Clone(); var decoded = staged.Decode(wire);
        if (error is not null && (decoded.IsEmpty || decoded[0] is not ReplaceOp)) throw new ArgumentException("part: a failed member recovers with a root replacement");
        var updated = DeltaApply.Apply(error is null ? value?.DeepClone() : null, new JsonArray(decoded.Select(x => (JsonNode?)x.ToJson()).ToArray()));
        return (updated, decoded, staged);
    }
    internal void Adopt(JsonNode? updated, ImmutableArray<Op> changes, WireDeltaDecoder staged, bool retirement)
    {
        value = updated; hasLast = true; error = null; decoder = staged; ops = changes; generation++;
        retired = retirement || retiredWhileFailed; retiredWhileFailed = false; Changed(); if (retired) RetiredInBatch();
    }
    internal void Reset(MemberSnapshot snapshot)
    {
        decoder = new();
        if (snapshot.Error is { } failed) Fail(failed);
        else Adopt(snapshot.Value, [new ReplaceOp(snapshot.Value)], decoder, false);
    }
    internal void Fail(Exception reason)
    {
        var wasFailed = error is not null; error = reason;
        if (!wasFailed) Changed();
    }
    internal void Retire()
    {
        if (error is not null) retiredWhileFailed = true;
        else Adopt(value, [], decoder, true);
    }
    internal void End(ChordError reason) { ended = true; error = reason; EndWatches(); }
    public override void Deliver(ChordContext context)
    {
        if (ended) EndWatches(); else if (error is not null) FailWatches(error, context); else Publish(value, ops, retired, context);
        ops = []; retired = false;
    }
}
