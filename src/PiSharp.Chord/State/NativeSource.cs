// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/state/native-source.ts
//
// Native sources (W4, slice 8 §2): in-process values that change, compared by identity (a writable, a registry's composed value,
// `ref.state`). Derives and registry tables depend on them and the batch marks them; the followed ones (writables, registry values) give
// their watches and observers one call per batch that changed them. Mappings:
// - NativeSource has a non-generic base (NativeSource) holding what a derive or a registry table reads of any source: its dependents and
//   its value as an object. Identity is JavaScript's (StateInternal.Identical).
// - NativeStart<T> is a class: a value, or a failure with its last good value (HasLast false for upstream's undefined).
// - The observers' Map is a Followers of entries, each with its cleanup: the registration on the observer's signal (an abort listener is
//   a CancellationTokenRegistration, removed when the observer goes).
// - NativeObservation<T> is a class holding the start value and Detach.
using System.Runtime.ExceptionServices;

namespace PiSharp.Chord.State;

/// <summary>
/// A synchronous observer of a native source (upstream <c>NativeObserver</c>, Durable's observer shape): <see cref="Advance"/> is called
/// inside the batch that changed the source, so it only records; <see cref="End"/> once, when the source ended (<c>ended</c>) or the
/// observer's context was aborted (<c>aborted</c>).
/// </summary>
/// <typeparam name="T">The source's value type.</typeparam>
public interface INativeObserver<T>
{
    /// <summary>The source's call of this batch: its new value, or a failure it may recover from.</summary>
    void Advance(NativeWatchCall<T> call, ChordContext context);

    /// <summary>The observer is detached for good with <paramref name="end"/>.</summary>
    void End(WatchEnd end);
}

/// <summary>What <c>observe</c> gives (upstream <c>NativeObservation</c>, Durable's <c>Observation</c>): the value the observer starts at,
/// and how to detach it.</summary>
/// <typeparam name="T">The source's value type.</typeparam>
public sealed class NativeObservation<T>
{
    private readonly Action detach;

    /// <summary>An observation starting at <paramref name="value"/> (upstream's is a plain <c>{ value, detach }</c>): public, so a native
    /// watchable built outside Chord, such as a hand-built registry value, can give one.</summary>
    /// <param name="value">The value the observer starts at.</param>
    /// <param name="detach">Detaches the observer.</param>
    public NativeObservation(T value, Action detach)
    {
        ArgumentNullException.ThrowIfNull(detach);
        Value = value;
        this.detach = detach;
    }

    /// <summary>The value the observer starts at.</summary>
    public T Value { get; }

    /// <summary>Detaches the observer; it gets no <c>End</c> call.</summary>
    public void Detach() => detach();
}

/// <summary>
/// What a new watch of a native source starts at (upstream <c>NativeStart</c>): its value, or while it is failed, the failure and its last
/// good value (none when it never had one: a watch cannot start then).
/// </summary>
internal sealed class NativeStart<T>
{
    private NativeStart(bool failed, T value, Exception? error, bool hasLast)
    {
        Failed = failed;
        Value = value;
        Error = error;
        HasLast = hasLast;
    }

    /// <summary>Whether the source is failed now.</summary>
    public bool Failed { get; }

    /// <summary>The value, or while failed the last good value (when <see cref="HasLast"/>).</summary>
    public T Value { get; }

    /// <summary>The failure, while failed.</summary>
    public Exception? Error { get; }

    /// <summary>Whether a failed source had a good value before (upstream <c>last !== undefined</c>).</summary>
    public bool HasLast { get; }

    /// <summary><c>{ kind: "value", value }</c>.</summary>
    public static NativeStart<T> Of(T value) => new(false, value, null, true);

    /// <summary><c>{ kind: "failed", error, last: { value } }</c>, or <c>last: undefined</c> when <paramref name="hasLast"/> is false.</summary>
    public static NativeStart<T> Failure(Exception error, bool hasLast, T last = default!) => new(true, hasLast ? last : default!, error, hasLast);
}

/// <summary>What a derive or a registry table reads of any native source: its dependents, and its value as an object.</summary>
internal abstract class NativeSource
{
    /// <summary>Derives and registry tables reading this source; each removes itself when it stops.</summary>
    public Followers<IDependent> Dependents { get; } = new();

    /// <summary>The current value, as a derive reads it (<c>.value</c>).</summary>
    public abstract object? UntypedValue { get; }

    /// <summary>Its value now as a registry contribution reads it (<c>current()</c>): throws its failure while it is failed.</summary>
    public abstract object? UntypedCurrent();
}

/// <summary>
/// A native source as derives and registry tables read it (upstream <c>NativeSource</c>): its value (<see cref="Start"/> while it may fail),
/// the dependents a change marks (<see cref="Changed"/>, inside a batch), and the host's batch until it ended. How its watches get their
/// calls is the subclass's (<see cref="FollowedSource{T}"/> for writables and registry values; <c>ref.state</c> gives each state to its
/// watches).
/// </summary>
internal abstract class NativeSource<T> : NativeSource, INativeWatchable<T>
{
    /// <param name="batch">The host's batch; null makes the source ended (a released reference's state).</param>
    protected NativeSource(Batch? batch) => Batch = batch;

    /// <inheritdoc/>
    public abstract T Value { get; }

    /// <inheritdoc/>
    public override object? UntypedValue => Value;

    /// <inheritdoc/>
    public abstract Task<INativeWatchHandle<T>> WatchAsync(ChordContext context);

    /// <summary>What a new watch starts at; a source that can fail and recover tells its failure.</summary>
    public virtual NativeStart<T> Start() => NativeStart<T>.Of(Value);

    /// <summary>
    /// Its value now as a contribution reads it: throws its failure while it is failed, where <see cref="Value"/> keeps the last good value
    /// for readers (a registry whose <c>compose</c> threw).
    /// </summary>
    public virtual T Current() => Value;

    /// <inheritdoc/>
    public override object? UntypedCurrent() => Current();

    /// <summary>The host's batch, null once the source ended (an ended source keeps no host).</summary>
    protected Batch? Batch { get; private set; }

    /// <summary>Records a change inside the current batch: every dependent marked.</summary>
    protected virtual void Changed()
    {
        foreach (var dependent in Dependents.Items) dependent.Invalidate();
    }

    /// <summary>Ends the source for good: its dependents dropped, the batch let go.</summary>
    protected virtual void End()
    {
        Batch = null;
        Dependents.Clear();
    }
}

/// <summary>
/// A native source its followers get through the batch (upstream <c>FollowedSource</c>: a writable, a registry value): a change lists it for
/// one frame when someone follows it; the frame is its value then, delivered only when it is another value (identity) than the one its
/// followers have, or after a failure, whose call comes once per episode.
/// </summary>
internal abstract class FollowedSource<T> : NativeSource<T>, IDelivered
{
    private readonly Followers<NativeWatch<T>> watches = new();
    // Synchronous observers and Chord's own subscribers (retryOn), fed inside the batch, each with its cleanup.
    private readonly Followers<ObserverEntry> observers = new();
    // What the followers have; null while nobody follows (so no old value is kept).
    private Shown? shown;
    // Listed for a frame in the current batch.
    private bool listed;

    protected FollowedSource(Batch? batch)
        : base(batch)
    {
    }

    /// <summary>Whether a watch or an observer follows it (a registry value also while readers wait for its first value).</summary>
    public virtual bool Watched => watches.Count > 0 || observers.Count > 0;

    /// <inheritdoc/>
    public override Task<INativeWatchHandle<T>> WatchAsync(ChordContext context)
    {
        try
        {
            return Task.FromResult<INativeWatchHandle<T>>(OpenWatch(context));
        }
        catch (Exception error)
        {
            return Task.FromException<INativeWatchHandle<T>>(error);
        }
    }

    /// <summary>
    /// <see cref="WatchAsync"/> without the task. On a failed source it opens at the last good value and its first call is the failure;
    /// with no value to start at it throws the failure. An ended source gives a watch that has ended.
    /// </summary>
    public NativeWatch<T> OpenWatch(ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var batch = Batch;
        if (batch is null)
        {
            var ended = new NativeWatch<T>(Value, static () => { }, static _ => { });
            ended.End(WatchEnd.Ended);
            return ended;
        }
        batch.AssertOutside("watch");
        var start = Start();
        if (start.Failed && !start.HasLast) ExceptionDispatchInfo.Throw(start.Error!);
        Follow(start.Failed ? Shown.Failure : Shown.Of(start.Value));
        NativeWatch<T>? watch = null;
        watch = new NativeWatch<T>(start.Value, () => Unfollow(watch!), batch.ReportListener, schedule: batch.Schedule);
        watches.Add(watch);
        if (context.AbortSignal is { } signal) watch.ObserveAbort(signal);
        if (start.Failed) watch.Fail(StateInternal.HandedOut(start.Error), context);
        return watch;
    }

    /// <summary>
    /// Attaches a synchronous observer at the current value (Durable's <c>observeXXX</c>); it counts as following, so a lazy value is
    /// computed in the batch that changed it. Throws the failure when there is no value to start at; aborting <paramref name="context"/>
    /// detaches it (<c>aborted</c>); the source's end detaches it too (<c>ended</c>), its abort registration removed.
    /// </summary>
    public NativeObservation<T> Observe(INativeObserver<T> observer, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(context);
        Batch?.AssertOutside("observe");
        var start = Start();
        if (start.Failed && !start.HasLast) ExceptionDispatchInfo.Throw(start.Error!);
        var value = start.Value;
        if (Batch is null)
        {
            observer.End(WatchEnd.Ended);
            return new NativeObservation<T>(value, static () => { });
        }
        var signal = context.AbortSignal;
        if (signal is { IsCancellationRequested: true })
        {
            observer.End(WatchEnd.Aborted);
            return new NativeObservation<T>(value, static () => { });
        }
        Follow(start.Failed ? Shown.Failure : Shown.Of(start.Value));
        var entry = new ObserverEntry(observer);
        observers.Add(entry);
        // Registered once the observer follows: a signal cancelled meanwhile runs the callback here, which detaches it at once.
        if (signal is { } token)
        {
            entry.Registration = token.UnsafeRegister(static state =>
            {
                var (source, observed) = ((FollowedSource<T>, INativeObserver<T>))state!;
                source.Aborted(observed);
            }, (this, observer));
        }
        return new NativeObservation<T>(value, () => Unsubscribe(observer));
    }

    /// <summary>
    /// Feeds <paramref name="observer"/> every call from now on, inside each batch, also while the source has no value (Chord's own
    /// followers: <c>retryOn</c>). Nothing is delivered for the current value.
    /// </summary>
    public void Subscribe(INativeObserver<T> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        if (Batch is null) return;
        Follow(StartOrNone());
        observers.Add(new ObserverEntry(observer));
    }

    public void Unsubscribe(INativeObserver<T> observer)
    {
        var entry = Find(observer);
        if (entry is null || !observers.Remove(entry)) return;
        entry.Cleanup();
        SettleFollowers();
    }

    /// <summary>Delivers this batch's frame: the new value when the followers have another, or a failure once per episode.</summary>
    public void Deliver(ChordContext context)
    {
        listed = false;
        var current = shown;
        if (current is null) return;
        DeliverShown(current, context);
        // Readers waiting for a first value pulled it through this delivery: nothing is kept for them.
        SettleFollowers();
    }

    /// <summary>
    /// Records a change inside the current batch: every dependent marked, and the source listed for a frame when someone follows it (or
    /// <see cref="Watched"/> says so).
    /// </summary>
    protected override void Changed()
    {
        base.Changed();
        List();
    }

    /// <summary>Lists the source for a frame in the current batch when someone follows it (or <see cref="Watched"/> says so), its
    /// dependents left as they are (marked already).</summary>
    protected void List()
    {
        if (listed || !Watched || Batch is null) return;
        listed = true;
        // Only readers waiting for a first value follow it without a follower's record: they have none. Not computed here: marking runs
        // inside adoptions, the value is pulled in the delivery.
        shown ??= Shown.None;
        Batch.Changed(this);
    }

    /// <summary>Nothing follows it any more (no watch, no observer): a subclass drops what it kept for followers.</summary>
    protected virtual void Unfollowed()
    {
    }

    /// <summary>Ends the source for good: watches and observers end <c>ended</c> (their abort registrations removed).</summary>
    protected override void End()
    {
        base.End();
        shown = null;
        // Ending detaches, which removes the watch from the set (the walk reads the array as it was) and runs no listener.
        foreach (var watch in watches.Items) watch.End(WatchEnd.Ended);
        foreach (var entry in observers.Clear())
        {
            entry.Cleanup();
            entry.Observer.End(WatchEnd.Ended);
        }
    }

    private void Aborted(INativeObserver<T> observer)
    {
        if (Find(observer) is null) return;
        Unsubscribe(observer);
        observer.End(WatchEnd.Aborted);
    }

    private ObserverEntry? Find(INativeObserver<T> observer)
    {
        foreach (var entry in observers.Items)
            if (ReferenceEquals(entry.Observer, observer)) return entry;
        return null;
    }

    private void DeliverShown(Shown current, ChordContext context)
    {
        var start = StartOrNone();
        if (start is { Failed: true })
        {
            // A failure reaches followers that had a value: those without one wait for the first.
            if (current.Kind != ShownKind.Value) return;
            shown = Shown.Failure;
            var error = StateInternal.HandedOut(start.Error);
            foreach (var watch in watches.Items) watch.Fail(error, context);
            var failed = new NativeWatchFailedCall<T>(error);
            foreach (var entry in observers.Items) entry.Observer.Advance(failed, context);
            return;
        }
        if (start is null || current.Kind == ShownKind.Value && StateInternal.Identical(current.Value, start.Value)) return;
        var value = start.Value;
        shown = Shown.Of(value);
        foreach (var watch in watches.Items) watch.Advance(value, context);
        // The array as it is now: an observer may detach itself in its call.
        var call = new NativeWatchValueCall<T>(value);
        foreach (var entry in observers.Items) entry.Observer.Advance(call, context);
    }

    /// <summary>Its start, null (upstream's <c>none</c>) while it has no value.</summary>
    private NativeStart<T>? StartOrNone()
    {
        var start = Start();
        return start.Failed && !start.HasLast ? null : start;
    }

    private void Follow(NativeStart<T>? start) => Follow(start is null ? Shown.None : start.Failed ? Shown.Failure : Shown.Of(start.Value));

    /// <summary>A follower comes: the first one records what followers have (a later one opened at the same current value).</summary>
    private void Follow(Shown start) => shown ??= start;

    private void Unfollow(NativeWatch<T> watch)
    {
        watches.Remove(watch);
        SettleFollowers();
    }

    /// <summary>No follower left: nothing is shown any more, so no old value is kept.</summary>
    private void SettleFollowers()
    {
        if (watches.Count > 0 || observers.Count > 0) return;
        shown = null;
        Unfollowed();
    }

    private enum ShownKind
    {
        Value,
        Failed,
        None,
    }

    /// <summary>What the watches and observers were last given (upstream <c>Shown</c>): a value, a failure (one call per failure episode),
    /// or nothing yet (a source with no value, followed for its first one).</summary>
    private sealed class Shown
    {
        private Shown(ShownKind kind, T value)
        {
            Kind = kind;
            Value = value;
        }

        public static Shown Failure { get; } = new(ShownKind.Failed, default!);

        public static Shown None { get; } = new(ShownKind.None, default!);

        public ShownKind Kind { get; }

        public T Value { get; }

        public static Shown Of(T value) => new(ShownKind.Value, value);
    }

    /// <summary>An observer and its cleanup: the registration on its context's signal, if any.</summary>
    private sealed class ObserverEntry(INativeObserver<T> observer)
    {
        public INativeObserver<T> Observer { get; } = observer;

        public CancellationTokenRegistration Registration { get; set; }

        public void Cleanup() => Registration.Unregister();
    }
}
