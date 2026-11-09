using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Chord.Delta;
using PiSharp.Chord.State;

namespace PiSharp.Chord.Jobs;

internal sealed class Running(Batch batch, string label) : Source(batch)
{
    private volatile List<JsonObject>? entries = [];
    private ImmutableArray<Op> ops = [];
    public override JsonNode Value => new JsonArray([.. Current.Select(x => x.DeepClone())]);
    private List<JsonObject> Current => entries ?? throw new ChordError("released", $"{label}: ended", "ended");
    public void Add(JsonObject entry)
    {
        var next = new List<JsonObject>(Current) { entry }; entries = next;
        Mark(new SpliceOp([], next.Count - 1, 0, [entry]));
    }
    public void Replace(JsonObject before, JsonObject after)
    {
        var next = new List<JsonObject>(Current); var index = next.IndexOf(before); if (index < 0) return;
        next[index] = after; entries = next; Mark(new SetOp([index], after));
    }
    public void StateChanged(JsonObject before, JsonObject after, ImmutableArray<Op> changes)
    {
        var next = new List<JsonObject>(Current); var index = next.IndexOf(before); if (index < 0) return;
        next[index] = after; entries = next;
        foreach (var op in changes) Mark(Under(index, op));
    }
    public void Remove(JsonObject entry)
    {
        var next = new List<JsonObject>(Current); var index = next.IndexOf(entry); if (index < 0) return;
        next.RemoveAt(index); entries = next; Mark(new SpliceOp([], index, 1, []));
    }
    private void Mark(Op op) { if (Watched) ops = ops.Add(op); Changed(); }
    public override void Deliver(ChordContext context) { var frame = ops; ops = []; Publish(Value, frame, false, context); }
    public void End() { entries = null; ops = []; EndWatches(); }
    private static Op Under(int index, Op op)
    {
        ImmutableArray<PathSegment> path = op is PathOp at ? [index, "state", .. at.Path] : [index, "state"];
        return op switch
        {
            ReplaceOp r => new SetOp(path, r.Value), SetOp s => new SetOp(path, s.Value), DeleteOp => new DeleteOp(path),
            AppendOp a => new AppendOp(path, a.Text), TruncateOp t => new TruncateOp(path, t.Count),
            SpliceOp s => new SpliceOp(path, s.Index, s.Remove, s.Items), MoveOp m => new MoveOp(path, m.Permutation),
            _ => throw new InvalidOperationException("Unknown delta operation"),
        };
    }
}
