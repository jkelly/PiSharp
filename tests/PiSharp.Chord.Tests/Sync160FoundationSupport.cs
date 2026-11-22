using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Delta;
using PiSharp.Chord.State;

// PiSharp-only (no upstream Pi source): the helpers of the chord-foundation package's 1.6.0 cases (Contracts, Data, Link, State and the
// telemetry sibling). They stand in for what the upstream tests build inline: a subscriber recording its calls, the test sources of
// test/state/derive.test.ts (Cell) and test/state/framed.test.ts (Fed), a dependent that reads when it is marked, and applyImmutable.
internal static partial class Program
{
    /// <summary>applyImmutable(taken, ops): the value a subscriber that had <paramref name="taken"/> has after <paramref name="ops"/>.</summary>
    internal static JsonNode? Sync160Applied(JsonNode? taken, ImmutableArray<Op> ops) => DeltaApply.Apply(taken?.DeepClone(), Sync160OpsJson(ops));

    /// <summary>The ops as their wire tuples: <c>[["s",["n"],4]]</c>.</summary>
    internal static JsonArray Sync160OpsJson(ImmutableArray<Op> ops) => new([.. ops.Select(op => (JsonNode?)op.ToJson())]);

    /// <summary>The ops of the value call <paramref name="call"/> (upstream <c>opsOf</c>).</summary>
    internal static ImmutableArray<Op> Sync160OpsOf(WatchCall<JsonNode?>? call) =>
        call is WatchValueCall<JsonNode?> value ? value.Ops : throw new InvalidOperationException("no value call");

    /// <summary>The value of the value call <paramref name="call"/>.</summary>
    internal static JsonNode? Sync160ValueOf(WatchCall<JsonNode?>? call) =>
        call is WatchValueCall<JsonNode?> value ? value.Value : throw new InvalidOperationException("no value call");

    /// <summary>The error of the failure call <paramref name="call"/>.</summary>
    internal static Exception Sync160ErrorOf(WatchCall<JsonNode?>? call) =>
        call is WatchFailedCall<JsonNode?> { Error: Exception error } ? error : throw new InvalidOperationException("no failure call");
}

/// <summary>A subscriber recording the calls it is fed, as a hold over a link takes them: inside the batch.</summary>
internal sealed class Sync160Recorder : ISubscriber
{
    public List<WatchCall<JsonNode?>> Calls { get; } = [];

    public int Ends { get; private set; }

    public void Advance(WatchCall<JsonNode?> call, ChordContext context) => Calls.Add(call);

    public void End(WatchEnd end) => Ends++;

    public void EndAfterDelivery() => Ends++;
}

/// <summary>A dependent that runs <paramref name="marked"/> when its source marks it (upstream's inline <c>{ invalidate, sourceRetired }</c>).</summary>
internal sealed class Sync160Dependent(Action marked) : IDependent
{
    public void Invalidate() => marked();

    public void SourceRetired(ReferenceSet<IDependent> visited)
    {
    }
}

/// <summary>A source a test sets inside a batch (upstream <c>Cell</c>); nobody watches it (its derives are what the tests follow).</summary>
internal sealed class Sync160Cell(double initial, Batch batch) : Source(batch)
{
    private JsonNode? current = JsonValue.Create(initial);

    public override JsonNode? Value => current;

    public void Put(double next)
    {
        current = JsonValue.Create(next);
        Changed();
    }

    public override void Deliver(ChordContext context)
    {
    }
}

/// <summary>The owner of a test's framed source: what it was told.</summary>
internal sealed class Sync160FramedOwner : IFramedOwner
{
    public string Label => "test";

    public List<Exception> Reports { get; } = [];

    public void Report(Exception error) => Reports.Add(error);
}

/// <summary>A framed source a test feeds, counting its deliveries (upstream <c>Fed</c>) and, when asked, the diffs it was asked for.</summary>
internal sealed class Sync160Fed(Batch batch, IFramedOwner owner) : FramedSource(batch, owner)
{
    public int Deliveries { get; private set; }

    /// <summary>When set, stands in for the structural diff: the seam a live query's row diff uses.</summary>
    public Func<JsonNode?, JsonNode?, ImmutableArray<Op>>? Differ { get; set; }

    public void Put(string json) => Set(JsonNode.Parse(json));

    public void Put(JsonNode? value) => Set(value);

    public void Fail(Exception error) => FailWith(error);

    public void Finish(Exception error) => End(error);

    public void Hush() => Quiet();

    public override void Deliver(ChordContext context)
    {
        Deliveries++;
        base.Deliver(context);
    }

    protected override ImmutableArray<Op> Diff(JsonNode? before, JsonNode? after) => Differ?.Invoke(before, after) ?? base.Diff(before, after);
}
