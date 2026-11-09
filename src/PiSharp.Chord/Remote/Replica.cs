// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): next/remote/replica.ts.
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
        value = updated; hasLast = true; error = null; decoder = staged; ops = changes;
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
