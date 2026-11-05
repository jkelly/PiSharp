// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/state/watchable.ts
//
// Chord's JSON watchables: sources (documents and derives, with their watches and dependent derives) and constants. A JSON value is a
// JsonNode (null is JSON null), immutable by convention as upstream's are. Mappings:
// - `watch(context)` is WatchAsync, whose task holds the watch or the error OpenWatch threw (upstream's async function rejects with it).
// - WatchStart is a class: a value, or a failure with its last good value (HasLast false for upstream's undefined: JSON null is a value).
// - The sets of watches, subscribers and dependents are Followers (Internal.cs), which keep upstream's Set order.
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Chord.Delta;

namespace PiSharp.Chord.State;

/// <summary>
/// A synchronous subscriber of a source (upstream <c>Subscriber</c>: a hold over a link, a reference's member): fed the source's call inside
/// the batch that made it, before the batch ends, so everything one commit changed can leave in one message. The watch's own sink shape.
/// </summary>
internal interface ISubscriber
{
    /// <summary>The source's call of this batch, in its batch's <paramref name="context"/>: a value, or a failure it may recover from; the
    /// next value after a failure is <c>[["r", value]]</c>.</summary>
    void Advance(WatchCall<JsonNode?> call, ChordContext context);

    /// <summary>The source ended for good with <paramref name="end"/>; nothing follows, and the subscriber is dropped.</summary>
    void End(WatchEnd end);

    /// <summary>The source ended for good after its last call: a subscriber delivering later (a watch) ends <c>ended</c> once it delivered
    /// what it was given, others end now.</summary>
    void EndAfterDelivery();
}

/// <summary>
/// What a new watch of a source starts at (upstream <c>WatchStart</c>, design §11): its value, or, while it is failed, the failure and its
/// last good value (none when it never had one: a watch cannot start then).
/// </summary>
internal sealed class WatchStart
{
    private WatchStart(bool failed, JsonNode? value, Exception? error, bool hasLast)
    {
        Failed = failed;
        Value = value;
        Error = error;
        HasLast = hasLast;
    }

    /// <summary>Whether the source is failed now.</summary>
    public bool Failed { get; }

    /// <summary>The value, or while failed the last good value (when <see cref="HasLast"/>).</summary>
    public JsonNode? Value { get; }

    /// <summary>The failure, while failed.</summary>
    public Exception? Error { get; }

    /// <summary>Whether a failed source had a good value before (upstream <c>last !== undefined</c>).</summary>
    public bool HasLast { get; }

    /// <summary><c>{ kind: "value", value }</c>.</summary>
    public static WatchStart Of(JsonNode? value) => new(false, value, null, true);

    /// <summary><c>{ kind: "failed", error, last }</c>; <paramref name="hasLast"/> false for <c>last: undefined</c>.</summary>
    public static WatchStart Failure(Exception error, bool hasLast, JsonNode? last) => new(true, hasLast ? last : null, error, hasLast);
}

/// <summary>What reads a source and is marked by its changes (upstream <c>Dependent</c>): a derive, or a reference's member forwarding to
/// derives.</summary>
internal interface IDependent
{
    /// <summary>The source changed in the current batch: flags only, no compute.</summary>
    void Invalidate();

    /// <summary>A source's document was retired in the current batch (rule 21); <paramref name="visited"/> keeps a diamond walked once.</summary>
    void SourceRetired(ReferenceSet<IDependent> visited);
}

/// <summary>
/// A changing value on a host (upstream <c>Source</c>: a singleton document, a derive, a replica): its watches and subscribers get one frame
/// per batch that changed it, and the derives reading it are marked dirty when it changes.
/// </summary>
internal abstract class Source : IWatchable<JsonNode?>, IDelivered
{
    // Its watches and subscribers, each set made by its first: most sources (a reference's members) never have one.
    private Followers<Watch<JsonNode?>>? watches;
    private Followers<ISubscriber>? subscribers;

    protected Source(Batch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        Batch = batch;
    }

    /// <summary>Derives reading this source (and references' members forwarding to some); each removes itself when it stops.</summary>
    public Followers<IDependent> Dependents { get; } = new();

    protected Batch Batch { get; }

    /// <inheritdoc/>
    public abstract JsonNode? Value { get; }

    /// <summary>Whether a watch or a subscriber follows this source.</summary>
    public bool Watched => (watches?.Count ?? 0) > 0 || (subscribers?.Count ?? 0) > 0;

    /// <inheritdoc/>
    public Task<IWatchHandle<JsonNode?>> WatchAsync(ChordContext context)
    {
        try
        {
            return Task.FromResult<IWatchHandle<JsonNode?>>(OpenWatch(context));
        }
        catch (Exception error)
        {
            return Task.FromException<IWatchHandle<JsonNode?>>(error);
        }
    }

    /// <summary>
    /// <see cref="WatchAsync"/> without the task, for references, which must register the watch before anything else runs. On a failed
    /// source it opens at the last good value and its first call is the failure; with no value to start at it throws the failure.
    /// </summary>
    public Watch<JsonNode?> OpenWatch(ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Batch.AssertOutside("watch");
        var start = Start();
        if (start.Failed && !start.HasLast) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(start.Error!);
        watches ??= new();
        var set = watches;
        Watch<JsonNode?>? watch = null;
        watch = new Watch<JsonNode?>(start.Value, () => set.Remove(watch!), Batch.ReportListener, schedule: Batch.Schedule);
        set.Add(watch);
        if (context.AbortSignal is { } signal) watch.ObserveAbort(signal);
        if (start.Failed) watch.Fail(start.Error, context);
        return watch;
    }

    /// <summary>What a new watch starts at; a source that can fail and recover tells its failure (<see cref="WatchStart"/>).</summary>
    public virtual WatchStart Start() => WatchStart.Of(Value);

    /// <summary>Feeds <paramref name="subscriber"/> every frame from now on, inside each batch; the caller took the current value first.</summary>
    public void Subscribe(ISubscriber subscriber)
    {
        ArgumentNullException.ThrowIfNull(subscriber);
        (subscribers ??= new()).Add(subscriber);
    }

    public void Unsubscribe(ISubscriber subscriber) => subscribers?.Remove(subscriber);

    /// <summary>Delivers this batch's frame to its watches; called once per batch that listed it with <see cref="Changed"/>.</summary>
    public abstract void Deliver(ChordContext context);

    /// <summary>Records a change in the current batch: listed for a frame when watched, and every dependent marked dirty.</summary>
    protected void Changed()
    {
        if (Watched) Batch.Changed(this);
        foreach (var derive in Dependents.Items) derive.Invalidate();
    }

    /// <summary>
    /// Records a retirement of this source's document in the current batch: every derive reading it, directly or through other derives,
    /// carries the flag on in its frame (rule 21). Called after <see cref="Changed"/>.
    /// </summary>
    protected void RetiredInBatch()
    {
        // Allocated only for a retirement, which is rare.
        var visited = new ReferenceSet<IDependent>();
        foreach (var derive in Dependents.Items) derive.SourceRetired(visited);
    }

    /// <summary>Queues one frame on every watch and feeds it to every subscriber.</summary>
    protected void Publish(JsonNode? value, ImmutableArray<Op> ops, bool retired, ChordContext context) =>
        Forward(new WatchValueCall<JsonNode?>(value, ops, retired), context);

    /// <summary>Queues <paramref name="call"/> on every watch and feeds it to every subscriber, as it is (a reference's member passes one
    /// on).</summary>
    protected void Forward(WatchCall<JsonNode?> call, ChordContext context)
    {
        if (watches is not null)
            foreach (var watch in watches.Items) watch.Advance(call, context);
        if (subscribers is not null)
            foreach (var subscriber in subscribers.Items) subscriber.Advance(call, context);
    }

    /// <summary>
    /// Gives every watch and subscriber one failure call (design §11): the source cannot compute its value now and may recover; they stay
    /// open, and its next value is <c>[["r", value]]</c>.
    /// </summary>
    protected void FailWatches(Exception error, ChordContext context) =>
        Forward(new WatchFailedCall<JsonNode?>(StateInternal.HandedOut(error)), context);

    /// <summary>
    /// Ends every watch and subscriber for good once what they were given is delivered (a followed runtime ended after a last call):
    /// <c>ended</c>; nothing follows.
    /// </summary>
    protected void EndWatchesAfterDelivery()
    {
        if (watches is not null)
            foreach (var watch in watches.Items) watch.EndAfterDelivery();
        if (subscribers is null || subscribers.Count == 0) return;
        foreach (var subscriber in subscribers.Clear()) subscriber.EndAfterDelivery();
    }

    /// <summary>Ends every watch and subscriber for good (its owner ended): <c>ended</c>; nothing follows.</summary>
    protected void EndWatches()
    {
        // Ending detaches, which removes the watch from the set (the walk reads the array as it was) and runs no listener.
        if (watches is not null)
            foreach (var watch in watches.Items) watch.End(WatchEnd.Ended);
        if (subscribers is null || subscribers.Count == 0) return;
        // A subscriber may unsubscribe as it ends.
        foreach (var subscriber in subscribers.Clear()) subscriber.End(WatchEnd.Ended);
    }
}

/// <summary>The watchable behind <see cref="Watchables.Constant"/> (upstream <c>Constant</c>): its watches never get a frame and end only by
/// stop or cancellation.</summary>
internal sealed class Constant : IWatchable<JsonNode?>
{
    public Constant(JsonNode? value) => Value = value;

    /// <inheritdoc/>
    public JsonNode? Value { get; }

    /// <inheritdoc/>
    public Task<IWatchHandle<JsonNode?>> WatchAsync(ChordContext context)
    {
        try
        {
            return Task.FromResult<IWatchHandle<JsonNode?>>(OpenWatch(context));
        }
        catch (Exception error)
        {
            return Task.FromException<IWatchHandle<JsonNode?>>(error);
        }
    }

    /// <summary>As <see cref="Source.OpenWatch"/>; nothing is delivered, so nothing is reported.</summary>
    public Watch<JsonNode?> OpenWatch(ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var watch = new Watch<JsonNode?>(Value, static () => { }, static _ => { });
        if (context.AbortSignal is { } signal) watch.ObserveAbort(signal);
        return watch;
    }
}

/// <summary>State for apps (upstream <c>state/index.ts</c>'s functions): constants and the derive helper <see cref="MapShared"/>.</summary>
public static partial class Watchables
{
    /// <summary>A watchable that never changes (upstream <c>constant</c>, start data, §18); usable as a state member and as a derive source.
    /// The constant holds <paramref name="value"/> as it was given.</summary>
    public static IWatchable<JsonNode?> Constant(JsonNode? value) => new Constant(value);
}
