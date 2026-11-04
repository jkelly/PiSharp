using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Data;
using PiSharp.Chord.Delta;
using PiSharp.Chord.State;
using PiSharp.Telemetry;

// The state layer's hand-built host (area cv2-state). Upstream's state tests build their values through createHost (packages/chord/src/next
// /host, phase 6): a host serving local types whose factories declare documents (defineState, the documents areas), derives
// (self.derive), writables (self.writable) and registries (the registries area). Until those land, the state cases run against hand-built
// values with the same wiring: one Batch per host, a queue scheduler that delivered() flushes (upstream's setTimeout(0) after microtasks),
// a singleton document standing in for defineState's (StateDoc), derives attached as an activation attaches them, and a writable's owner
// standing in for its attempt (StateOwner). Each case file says which of its cases wait for phase 6 for the parts only the host has.
internal sealed class StateHost
{
    public StateHost(ITelemetryMetrics? metrics = null) => Batch = new Batch(Errors.Add, metrics, Jobs.Post);

    /// <summary>The host's scheduler: watch deliveries and <c>soon</c> commits wait here until <see cref="Delivered"/>.</summary>
    public StateJobs Jobs { get; } = new();

    /// <summary>What the batch reported (the host's <c>onError</c>).</summary>
    public List<BatchError> Errors { get; } = [];

    public Batch Batch { get; }

    /// <summary>Upstream's <c>delivered()</c>: runs every queued delivery, and what those queue, until nothing is left.</summary>
    public void Delivered() => Jobs.Flush();

    /// <summary>A singleton document at <paramref name="initial"/>'s value.</summary>
    public StateDoc Doc(string initial) => new(Batch, initial);

    /// <summary>A derive over <paramref name="sources"/>, attached as an activation attaches one; <paramref name="report"/> gets its
    /// compute's failures (the owner's <c>derive_failed</c>).</summary>
    public Derive Derive(object[] sources, Func<IReadOnlyList<object?>, JsonNode?> compute, Action<Exception>? report = null)
    {
        var derive = new Derive(sources, compute, Batch);
        derive.Attach((source, _) => source, report ?? (_ => { }));
        return derive;
    }

    /// <summary>A writable of <paramref name="owner"/>'s attempt.</summary>
    public WritableSource<T> Writable<T>(T initial, StateOwner owner) => new(initial, owner, Batch);
}

/// <summary>A queue scheduler: work posted waits until <see cref="Flush"/>, which runs it in order, and what it posts, until none is left.</summary>
internal sealed class StateJobs
{
    private readonly Queue<Action> queue = new();

    public int Pending => queue.Count;

    public void Post(Action work)
    {
        lock (queue) queue.Enqueue(work);
    }

    public void Flush()
    {
        for (var rounds = 0; rounds < 100_000; rounds++)
        {
            Action? next;
            lock (queue)
            {
                if (!queue.TryDequeue(out next)) return;
            }
            next();
        }
        throw new InvalidOperationException("the jobs never settle");
    }
}

/// <summary>
/// A singleton document as a JSON source (defineState's, documents areas): <see cref="Change"/> commits a new revision in a commit of its
/// own, or <see cref="Adopt"/> inside the commit running now; its frame is the new value with the ops from the old one (diffRevisions, the
/// ops upstream's tracker emits for these edits). <see cref="Retire"/> retires it: the address shows the initial value again, and the
/// derives reading it carry the flag (rule 21).
/// </summary>
internal sealed class StateDoc : Source
{
    private readonly JsonNode? initial;
    private JsonNode? value;
    private ImmutableArray<Op>.Builder ops = ImmutableArray.CreateBuilder<Op>();
    private bool retired;

    public StateDoc(Batch batch, string initial)
        : base(batch)
    {
        this.initial = JsonNode.Parse(initial);
        value = this.initial?.DeepClone();
    }

    public override JsonNode? Value => value;

    /// <summary>A commit of its own whose adoption sets the value to <paramref name="next"/> (a copy of the current value it may edit).</summary>
    public void Change(Func<JsonNode?, JsonNode?> next, ChordContext context) => Batch.Commit(() => Adopt(next), context);

    /// <summary>Sets <paramref name="json"/> in a commit of its own.</summary>
    public void Set(string json, ChordContext context) => Change(_ => JsonNode.Parse(json), context);

    /// <summary>Adopts a new revision inside the commit running now.</summary>
    public void Adopt(Func<JsonNode?, JsonNode?> next)
    {
        var before = value;
        value = next(before?.DeepClone());
        // Only a watched document is listed for a frame: an unwatched one keeps no ops for a later watch, which starts at its value.
        if (Watched) ops.AddRange(DeltaDiff.DiffRevisions(before, value));
        else ops.Clear();
        Changed();
    }

    /// <summary>Retires the document in a commit of its own: its value is the initial one again.</summary>
    public void Retire(ChordContext context) => Batch.Commit(() =>
    {
        var before = value;
        value = initial?.DeepClone();
        // Only a watched document is listed for a frame: an unwatched one keeps no ops for a later watch, which starts at its value.
        if (Watched) ops.AddRange(DeltaDiff.DiffRevisions(before, value));
        else ops.Clear();
        retired = true;
        Changed();
        RetiredInBatch();
    }, context);

    public override void Deliver(ChordContext context)
    {
        var frame = ops.ToImmutable();
        var wasRetired = retired;
        ops = ImmutableArray.CreateBuilder<Op>();
        retired = false;
        Publish(value, frame, wasRetired, context);
    }
}

/// <summary>A writable's owner standing in for its attempt: its context, and the seal check that throws <c>released</c> once it ended.</summary>
internal sealed class StateOwner(string name, ChordContext? context = null) : IWritableOwner
{
    public bool Ended { get; set; }

    public ChordContext Context { get; } = context ?? ChordContext.Background;

    /// <summary>The error the attempt ends with, which its writables keep (<c>released/ended</c>).</summary>
    public ChordError EndError() => new("released", $"{name}: ended", "ended");

    public void AssertUsable()
    {
        if (Ended) throw EndError();
    }
}
