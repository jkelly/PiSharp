// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/state/derive.ts
//
// Derives (§12): values computed from watchables, pulled when read and marked dirty by commits; and mapShared, which keeps unchanged
// elements identical so a derive's diff stays cheap. Mappings:
// - A derive's sources are objects, as upstream's are unknown until attach resolves them: DeriveSource is a Source or a Constant, and
//   DeriveInput those or a NativeSource. compute takes their values (a JSON source's JsonNode, a native source's value) and returns JSON.
// - `before !== after` and the diff's identity are JavaScript's (DeltaDiff.Strict: containers by reference, primitives by value), so a
//   derive that computes the same number again counts no diff, as upstream's does.
// - A JsonNode has one parent, so a derive's value cannot share a subtree with its previous value: a JsonNode result of mapShared is reused
//   by identity, but a derive that puts it into its own JsonArray must copy it (DeepClone) when it already has a parent (the previous
//   value). The derive's diff is then structural (Delta/Diff.cs): its ops are upstream's for reconstructed values.
// - A failed or detached derive rethrows its error object (ExceptionDispatchInfo), so every read throws the same error, as upstream's do.
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using PiSharp.Chord.Delta;

namespace PiSharp.Chord.State;

/// <summary>
/// A derive (upstream <c>Derive</c>): <c>compute</c> over its sources' values, recomputed on read when dirty, so each compute runs at most
/// once per batch and after its sources. A commit only marks it; the batch recomputes it only when something watched depends on it.
/// </summary>
internal sealed class Derive : Source, IDependent
{
    private static readonly ImmutableArray<Op> NoOps = [];

    private readonly Func<IReadOnlyList<object?>, JsonNode?> compute;
    // The sources as declared, until Attach resolves them.
    private IReadOnlyList<object>? declared;
    private object[] sources = [];
    // Where it stands (upstream DeriveState): dirty (a source changed since the last compute, or there was none), clean with its value,
    // failed (its compute threw: it throws its error until a source it reads changes or is re-bound, then recomputes), or detached (its
    // object ended: it throws its object's error for good).
    private DeriveState state = DeriveState.Dirty;
    private JsonNode? value;
    private Exception? error;
    // The value this derive's watches have, kept from its marking to its frame in the batch (upstream #framed).
    private Framed framed = Framed.None;
    private JsonNode? framedValue;
    // Whether a source's document was retired in the current batch while this derive is watched (rule 21).
    private bool retired;
    // Reports a failure of its compute to its owner (Attach); once per failure episode, which a value ends.
    private Action<Exception>? report;
    private bool failing;
    // Its last good value, which a watch opened while it is failed starts at (none before the first).
    private bool hasGood;
    private JsonNode? good;

    private enum DeriveState
    {
        Dirty,
        Clean,
        Failed,
        Detached,
    }

    // Upstream's #framed: undefined (not marked), NO_VALUE (its watches had no value: a failure) or the value.
    private enum Framed
    {
        None,
        NoValue,
        Value,
    }

    public Derive(IReadOnlyList<object> sources, Func<IReadOnlyList<object?>, JsonNode?> compute, Batch batch)
        : base(batch)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(compute);
        declared = sources;
        this.compute = compute;
    }

    /// <inheritdoc/>
    public override JsonNode? Value
    {
        get
        {
            switch (state)
            {
                case DeriveState.Clean:
                    return value;
                case DeriveState.Dirty:
                    return Recompute();
                default:
                    ExceptionDispatchInfo.Throw(error!);
                    return null;
            }
        }
    }

    /// <summary>Its value, or while failed its failure and last good value (a detached derive throws).</summary>
    public override WatchStart Start()
    {
        try
        {
            return WatchStart.Of(Value);
        }
        catch (Exception thrown) when (state == DeriveState.Failed)
        {
            return WatchStart.Failure(thrown, hasGood, good);
        }
    }

    /// <summary>
    /// Starts reading its sources, as <paramref name="resolve"/> maps the declared ones (at activation); the first read computes.
    /// <paramref name="report"/> gets a throw of its compute, once per failure episode (a value ends it); a failing source's error is its
    /// source's to report.
    /// </summary>
    public void Attach(Func<object, int, object> resolve, Action<Exception> report)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(report);
        this.report = report;
        sources = [.. declared!.Select((source, index) => resolve(source, index))];
        declared = null;
        foreach (var source in sources) DependentsOf(source)?.Add(this);
    }

    /// <summary>Its object ended: no source keeps it, its watches end <c>ended</c>, reads throw <paramref name="error"/> (the object's).</summary>
    public void Detach(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        foreach (var source in sources) DependentsOf(source)?.Remove(this);
        sources = [];
        declared = null;
        state = DeriveState.Detached;
        value = null;
        this.error = error;
        framed = Framed.None;
        framedValue = null;
        EndWatches();
    }

    /// <summary>
    /// A source changed: mark this derive and its dependents dirty, flags only; a failed one too, which recomputes on its next read and heals
    /// when its sources give a value it can compute. An already dirty derive has dirty dependents (every read recomputes its sources first),
    /// so marking stops there.
    /// </summary>
    public void Invalidate()
    {
        if (state is DeriveState.Dirty or DeriveState.Detached) return;
        if (Watched)
        {
            framed = state == DeriveState.Clean ? Framed.Value : Framed.NoValue;
            framedValue = state == DeriveState.Clean ? value : null;
        }
        state = DeriveState.Dirty;
        value = null;
        error = null;
        Changed();
    }

    /// <summary>A source's document was retired in this batch: a watched derive's frame carries the flag, and the derives reading this one
    /// are told too.</summary>
    public void SourceRetired(ReferenceSet<IDependent> visited)
    {
        // Once per derive per retirement: a diamond of derives is walked in its size, not once per path.
        if (!visited.Add(this)) return;
        if (Watched) retired = true;
        foreach (var derive in Dependents.Items) derive.SourceRetired(visited);
    }

    /// <summary>
    /// Delivers the frame: the diff from the watches' value, a retirement even without ops (rule 21). A compute that throws gives one failure
    /// call when the watches had a value (one per failure episode), the watches stay open; a retirement waits for the next value.
    /// </summary>
    public override void Deliver(ChordContext context)
    {
        var before = framed;
        var beforeValue = framedValue;
        var wasRetired = retired;
        framed = Framed.None;
        framedValue = null;
        JsonNode? after;
        try
        {
            after = Value;
        }
        catch (Exception thrown)
        {
            // Still failed since the last call: nothing new for the watches.
            if (before != Framed.NoValue) FailWatches(thrown, context);
            return;
        }
        retired = false;
        var ops = NoOps;
        // Upstream's `after !== before`: a value is never identical to NO_VALUE or to an unmarked `undefined`.
        if (before != Framed.Value || !DeltaDiff.Strict(after, beforeValue))
        {
            Batch.Count("chord.derive.diffs");
            ops = before == Framed.Value ? DeltaDiff.DiffRevisions(beforeValue, after) : [new ReplaceOp(after)];
        }
        if (ops.Length > 0 || wasRetired) Publish(after, ops, wasRetired, context);
    }

    private static Followers<IDependent>? DependentsOf(object source) => source switch
    {
        Source json => json.Dependents,
        NativeSource native => native.Dependents,
        _ => null,
    };

    private static object? Read(object source) => source switch
    {
        Source json => json.Value,
        Constant constant => constant.Value,
        NativeSource native => native.UntypedValue,
        _ => throw new InvalidOperationException($"A derive reads a watchable of Chord's, not a {source.GetType().Name}"),
    };

    private JsonNode? Recompute() => Batch.Compute(() =>
    {
        try
        {
            Batch.Count("chord.derive.computes");
            // A failing source throws its own error here, so a derive over a failed derive fails with the same error.
            var values = new object?[sources.Length];
            for (var index = 0; index < sources.Length; index++) values[index] = Read(sources[index]);
            JsonNode? computed;
            try
            {
                computed = compute(values);
            }
            catch (Exception thrown)
            {
                if (!failing) report?.Invoke(StateInternal.HandedOut(thrown));
                failing = true;
                throw;
            }
            failing = false;
            state = DeriveState.Clean;
            value = computed;
            hasGood = true;
            good = computed;
            return computed;
        }
        catch (Exception thrown)
        {
            state = DeriveState.Failed;
            value = null;
            error = thrown;
            throw;
        }
    });
}

public static partial class Watchables
{
    /// <summary>
    /// Maps a list calling <paramref name="f"/> only for object elements not seen before (by identity), returning the previous result for
    /// the others; primitive elements (null, strings, value types and JSON primitives) are mapped every time (upstream <c>mapShared</c>).
    /// The cache is weak: dropped elements take their results along. A JsonNode result has one parent: a derive that puts results into its
    /// own JsonArray copies one that is already in another tree.
    /// </summary>
    public static Func<IReadOnlyList<TElement>, TResult[]> MapShared<TElement, TResult>(Func<TElement, TResult> f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var results = new ConditionalWeakTable<object, StrongBox<TResult>>();
        return elements =>
        {
            var mapped = new TResult[elements.Count];
            for (var index = 0; index < mapped.Length; index++)
            {
                var element = elements[index];
                // The decided rule: identity caching for objects only.
                if (element is null or string or JsonValue || element.GetType().IsValueType)
                {
                    mapped[index] = f(element);
                    continue;
                }
                if (!results.TryGetValue(element, out var result))
                {
                    result = new StrongBox<TResult>(f(element));
                    results.AddOrUpdate(element, result);
                }
                mapped[index] = result.Value!;
            }
            return mapped;
        };
    }
}
