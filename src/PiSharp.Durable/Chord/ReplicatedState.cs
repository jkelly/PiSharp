// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/chord/src/services/state.ts (StateSubscriber, ReplicatedStatePublisher,
// MutableReplicatedStateImpl, AttachedReplicatedStateImpl, attachReplicatedStateSource, ReplicatedStateReplica,
// serviceDeliveryContext) and the replicatedState factory of packages/chord/src/api.ts. In process only: the wire codec
// (state-codec.ts) and service RPC are not part of this file.
//
// Concurrency model. JavaScript runs all of this on one thread and relies on reentrancy guards; .NET callers may be on any thread, so
// each guard becomes an ownership flag taken under a short lock that is never held while user code runs (the one exception is a local
// state's change gate, below):
// - A state's gate makes every commit atomic: the next value, its sequence and its queued publication (and, for a local state, the
//   tracker adoption; for an attached state, the cursor check). A subscription registers and queues its hydration under the same gate,
//   so it hydrates at exactly one sequence and later sees only later sequences.
// - A local state's change gate serializes its tracker (upstream's #changing): it is held while a change's mutation runs, which is user
//   code, through the commit, and released before delivery. Another thread's change or replacement waits for it; the mutating thread's
//   own nested change or replacement is rejected, as upstream rejects a reentrant one.
// - Exactly one thread owns a state's publication loop at a time (upstream's #delivering). A commit made while the loop is owned, by
//   a reentrant callback or by another thread, is queued and delivered by the owner in sequence order, and the committer returns
//   without listener errors, as an upstream reentrant commit does. Exact (internal) listeners run on the owning thread, synchronously.
// - Exactly one thread runs a subscription's callbacks at a time (upstream's #running), so one subscription's callbacks never overlap
//   and see frames in order. A callback runs on the thread that delivered its frame or on the thread that completed the previous
//   callback's task: continuations run synchronously (ExecuteSynchronously), the deterministic stand-in for a promise's then. A task
//   that is already complete continues inline rather than after a microtask; that is the one ordering difference, and it only makes
//   delivery earlier.
// The default error reporter (upstream reportErrorAsync, which rethrows in a microtask as an uncaught error) raises
// ReplicatedState.UnhandledError synchronously instead.
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using PiSharp.Durable.Chord.Delta;

namespace PiSharp.Durable.Chord;

/// <summary>Factories for replicated states (upstream <c>replicatedState</c>) and the default error report.</summary>
public static class ReplicatedState
{
    /// <summary>
    /// Receives listener failures of states that have no error callback (upstream <c>reportErrorAsync</c>, which surfaces them as an
    /// uncaught error). Raised synchronously on the reporting thread; an exception thrown by a handler is ignored. With no handler the
    /// failure is written to <see cref="Trace"/>.
    /// </summary>
    public static event Action<Exception>? UnhandledError;

    /// <summary>
    /// Creates authoritative local state owning the immutable <paramref name="initial"/> value, which must be an object or an array
    /// (upstream <c>T extends object</c>).
    /// </summary>
    public static MutableReplicatedState Create(JsonValue initial) => new(initial);

    /// <summary>
    /// Attaches a publication-only state to one authoritative immutable source stream (<c>attachReplicatedStateSource</c>). It hydrates
    /// synchronously from the attachment snapshot, then activates the attachment, which drains buffered frames in order. If either step
    /// fails the attachment is disposed and the failure thrown.
    /// </summary>
    public static AttachedReplicatedState<T> Attach<T>(IReplicatedStateSource<T> source, ReplicatedStateSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var attachment = source.Attach() ?? throw new InvalidOperationException("Replicated state source returned no attachment");
        try
        {
            var state = new AttachedReplicatedState<T>(attachment, options ?? new ReplicatedStateSourceOptions());
            state.Activate();
            return state;
        }
        catch (Exception error)
        {
            try { attachment.Dispose(); }
            catch (Exception disposeError) { throw new AggregateException("Failed to attach replicated state source", error, disposeError); }
            throw;
        }
    }

    /// <summary>Context for synthetic service deliveries without a caller (<c>serviceDeliveryContext</c>): hydrations.</summary>
    internal static DurableContext ServiceDeliveryContext => DurableContext.Background;

    internal static void ReportUnhandled(Exception error)
    {
        var handlers = UnhandledError;
        if (handlers is null)
        {
            Trace.TraceError("Unobserved replicated state failure: " + error);
            return;
        }
        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((Action<Exception>)handler)(error); }
            catch (Exception) { /* a failing reporter must not break delivery */ }
        }
    }

    internal static void ThrowCollected(List<Exception> errors, string message)
    {
        if (errors.Count == 1) ExceptionDispatchInfo.Throw(errors[0]);
        if (errors.Count > 1) throw new AggregateException(message, errors);
    }

    internal static Func<T, DurableContext, ReplicatedStateDelivery, Task?> Listener<T>(Action<T, DurableContext, ReplicatedStateDelivery> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return (value, context, delivery) =>
        {
            listener(value, context, delivery);
            return null;
        };
    }

    internal static Func<T, DurableContext, ReplicatedStateDelivery, Task?> Listener<T>(Func<T, DurableContext, ReplicatedStateDelivery, Task> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return listener;
    }
}

/// <summary>
/// Authoritative in-process state (upstream <c>MutableReplicatedState</c>). Revisions are published atomically with a sequence; the
/// initial value is sequence 0. A <see cref="Tracker"/> owns the revision sequence: <see cref="Change(DurableContext, Action{Draft})"/>
/// drafts a transaction over the current value and <see cref="Replace"/> takes ownership of a whole new root.
/// </summary>
/// <remarks>
/// Changes and replacements of one state are serialized by a per-state change gate held while the mutation runs (upstream relies on its
/// single thread): another thread's change waits for it, while the mutating thread itself is rejected if it changes or replaces the state
/// again from inside the mutation. The gate is released before listeners run, so a listener may change the state; that revision is
/// queued and delivered in sequence order by the delivery owner. A mutation must therefore not block on another thread that changes the
/// same state.
/// </remarks>
public sealed class MutableReplicatedState : IReplicatedState<JsonValue>, IReplicatedStateInternals<JsonValue>
{
    private const string SynchronousOnly = "Replicated state change callbacks must be synchronous";

    private readonly ReplicatedStatePublisher<JsonValue> publisher;
    private readonly Tracker tracker;
    // Serializes every use of the tracker (draft, prepare, adopt). Held while a mutation runs, never while listeners run.
    private readonly Lock changeGate = new();
    // Guarded by changeGate: true while a mutation runs on the gate's owning thread (upstream's #changing).
    private bool changing;

    internal MutableReplicatedState(JsonValue initial)
    {
        RequireContainer(initial, nameof(initial));
        tracker = Tracker.Track(initial);
        publisher = new ReplicatedStatePublisher<JsonValue>(initial, ReplicatedState.ReportUnhandled);
    }

    /// <summary>The current immutable value.</summary>
    public JsonValue Value => publisher.Value;

    /// <summary>
    /// Atomically publishes one synchronous draft mutation (upstream <c>change(context, mutate)</c>). <paramref name="mutate"/> receives
    /// the root draft (an object or array draft, as the root is); every draft is revoked when it returns, so held handles throw. The
    /// change is prepared into its exact compact operations (string append/truncate, splices, permutations, sets and deletes) and the
    /// adopted revision is published with them; untouched subtrees keep their references, and a change that leaves the value deeply equal
    /// publishes nothing. Values placed through the draft are immutable, so a placement is a copy by value.
    /// </summary>
    /// <remarks>
    /// If <paramref name="mutate"/> throws, the draft is aborted, nothing is published and the exception propagates. An <c>async</c>
    /// lambda or method is rejected before it runs (upstream rejects a callback returning a promise); changing or replacing this state
    /// from inside <paramref name="mutate"/> throws <see cref="InvalidOperationException"/> and, unless caught there, rolls the outer
    /// change back. Once published, exact-listener failures are thrown: one as itself, several as an <see cref="AggregateException"/>; the
    /// revision stays committed.
    /// </remarks>
    public void Change(DurableContext context, Action<Draft> mutate)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(mutate);
        // An async lambda bound to Action is async void: its continuation would run against revoked drafts and fail unobserved.
        var asynchronous = mutate.Method.IsDefined(typeof(AsyncStateMachineAttribute), inherit: false);
        ChangeCore(context, draft =>
        {
            mutate(draft);
            return null;
        }, asynchronous);
    }

    /// <summary>
    /// <see cref="Change(DurableContext, Action{Draft})"/> for a mutation that returns a value, which is ignored, as upstream ignores a
    /// callback's return value. A returned awaitable (<see cref="Task"/>, <see cref="ValueTask"/>, <see cref="ValueTask{TResult}"/> or
    /// another <see cref="IAsyncResult"/>) is upstream's PromiseLike: its failure is observed, the draft is aborted and the change throws
    /// <see cref="InvalidOperationException"/> ("must be synchronous").
    /// </summary>
    public void Change(DurableContext context, Func<Draft, object?> mutate)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(mutate);
        ChangeCore(context, mutate, asynchronous: false);
    }

    /// <summary>
    /// Atomically takes ownership of <paramref name="value"/> as the next revision (upstream <c>replace</c>, through the tracker's
    /// <c>prepareReplace</c>): the new value is <paramref name="value"/> itself, published as <c>[["r", value]]</c>, exactly as upstream
    /// publishes it (upstream does not diff a replacement). A deeply equal replacement keeps the current value and publishes nothing.
    /// Throws <see cref="InvalidOperationException"/> when called from inside a change mutation. Listener failures as in
    /// <see cref="Change(DurableContext, Action{Draft})"/>.
    /// </summary>
    public void Replace(DurableContext context, JsonValue value)
    {
        ArgumentNullException.ThrowIfNull(context);
        RequireContainer(value, nameof(value));
        bool deliver;
        lock (changeGate)
        {
            if (changing) throw new InvalidOperationException("Replicated state cannot be replaced from a change callback");
            deliver = Commit(tracker.PrepareReplace(value), context);
        }
        if (deliver) ReplicatedState.ThrowCollected(publisher.Deliver(), "Replicated state listeners failed");
    }

    /// <inheritdoc/>
    public IDisposable Subscribe(Action<JsonValue, DurableContext, ReplicatedStateDelivery> listener) => publisher.Subscribe(ReplicatedState.Listener(listener));

    /// <inheritdoc/>
    public IDisposable Subscribe(Func<JsonValue, DurableContext, ReplicatedStateDelivery, Task> listener) => publisher.Subscribe(ReplicatedState.Listener(listener));

    (JsonValue Value, long Sequence) IReplicatedStateInternals<JsonValue>.Snapshot() => publisher.Snapshot();

    IDisposable IReplicatedStateInternals<JsonValue>.SubscribeSource(Action<IReadOnlyList<Op>, long, DurableContext> listener) => publisher.SubscribeSource(listener);

    private void ChangeCore(DurableContext context, Func<Draft, object?> mutate, bool asynchronous)
    {
        bool deliver;
        lock (changeGate)
        {
            if (changing) throw new InvalidOperationException("Replicated state cannot be changed reentrantly from a change callback");
            if (asynchronous) throw new InvalidOperationException(SynchronousOnly);
            changing = true;
            Prepared prepared;
            try
            {
                var change = tracker.BeginChange();
                try
                {
                    var outcome = mutate(change.State);
                    if (IsAwaitable(outcome))
                    {
                        Observe(outcome!);
                        throw new InvalidOperationException(SynchronousOnly);
                    }
                    prepared = change.Prepare();
                }
                catch
                {
                    change.Abort();
                    throw;
                }
            }
            finally
            {
                changing = false;
            }
            deliver = Commit(prepared, context);
        }
        if (deliver) ReplicatedState.ThrowCollected(publisher.Deliver(), "Replicated state listeners failed");
    }

    // Adoption and publication commit together under the publisher's gate; a no-op is adopted (the tracker revision advances) but not
    // published. Returns whether the caller owns delivery; the caller delivers after releasing the change gate.
    private bool Commit(Prepared prepared, DurableContext context) => publisher.Commit(_ =>
    {
        tracker.Adopt(prepared);
        return prepared.Ops.IsEmpty ? null : (prepared.Value, prepared.Ops);
    }, context);

    private static bool IsAwaitable(object? outcome) =>
        outcome is IAsyncResult or ValueTask ||
        outcome?.GetType() is { IsGenericType: true } type && type.GetGenericTypeDefinition() == typeof(ValueTask<>);

    // Upstream's `void Promise.resolve(outcome).catch(() => undefined)`: a later failure is observed, never reported as unobserved.
    // A ValueTask<TResult> or other IAsyncResult has no failure to observe here.
    private static void Observe(object outcome)
    {
        var task = outcome switch
        {
            Task pending => pending,
            ValueTask pending => pending.AsTask(),
            _ => null,
        };
        task?.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static void RequireContainer(JsonValue value, string parameter)
    {
        ArgumentNullException.ThrowIfNull(value, parameter);
        if (value is not (JsonObjectValue or JsonArrayValue)) throw new ArgumentException("Replicated state root must be an object or an array", parameter);
    }
}

/// <summary>
/// A synchronously hydrated publication-only state backed by one source attachment (upstream <c>AttachedReplicatedState</c>). It
/// publishes the source's exact value and operation references; it never applies or re-diffs them.
/// </summary>
/// <typeparam name="T">The immutable value type.</typeparam>
public sealed class AttachedReplicatedState<T> : IReplicatedState<T>, IReplicatedStateInternals<T>, IDisposable
{
    private const long MaxSafeInteger = 9_007_199_254_740_991;
    private readonly IReplicatedStateSourceAttachment<T> attachment;
    private readonly ReplicatedStatePublisher<T> publisher;
    private readonly Action<Exception>? onError;
    // Guarded by the publisher's gate: only read and written inside a commit.
    private long cursor;
    private int disposed;

    internal AttachedReplicatedState(IReplicatedStateSourceAttachment<T> attachment, ReplicatedStateSourceOptions options)
    {
        var snapshot = attachment.Snapshot;
        AssertCursor(snapshot.Cursor, "snapshot");
        this.attachment = attachment;
        cursor = snapshot.Cursor;
        onError = options.OnError;
        publisher = new ReplicatedStatePublisher<T>(snapshot.Value, Report);
    }

    /// <summary>The last published value; it stays readable after disposal.</summary>
    public T Value => publisher.Value;

    /// <inheritdoc/>
    public IDisposable Subscribe(Action<T, DurableContext, ReplicatedStateDelivery> listener) => publisher.Subscribe(ReplicatedState.Listener(listener));

    /// <inheritdoc/>
    public IDisposable Subscribe(Func<T, DurableContext, ReplicatedStateDelivery, Task> listener) => publisher.Subscribe(ReplicatedState.Listener(listener));

    /// <summary>Idempotently releases the source attachment. The last published value remains readable.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        attachment.Dispose();
    }

    (T Value, long Sequence) IReplicatedStateInternals<T>.Snapshot() => publisher.Snapshot();

    IDisposable IReplicatedStateInternals<T>.SubscribeSource(Action<IReadOnlyList<Op>, long, DurableContext> listener) => publisher.SubscribeSource(listener);

    internal void Activate() => attachment.Activate(Receive);

    private void Receive(ReplicatedStateSourceFrame<T> frame)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        try
        {
            if (frame?.Ops is null || frame.Context is null) throw new InvalidOperationException("Replicated state source frame is incomplete");
            AssertCursor(frame.Cursor, "frame");
            var errors = publisher.Publish(_ =>
            {
                if (Volatile.Read(ref disposed) != 0) return null;
                var expected = cursor + 1;
                if (frame.Cursor != expected)
                    throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                        $"Replicated state source cursor has a gap: expected {expected}, received {frame.Cursor}"));
                cursor = frame.Cursor;
                return (frame.Value, frame.Ops);
            }, frame.Context);
            if (errors.Count == 1) Report(errors[0]);
            else if (errors.Count > 1) Report(new AggregateException("Replicated state listeners failed", errors));
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private void Fail(Exception error)
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { attachment.Dispose(); }
        catch (Exception disposeError)
        {
            Report(new AggregateException("Replicated state source contract failed", error, disposeError));
            return;
        }
        Report(error);
    }

    private void Report(Exception error)
    {
        try { (onError ?? ReplicatedState.ReportUnhandled)(error); }
        catch (Exception reportError) { ReplicatedState.ReportUnhandled(reportError); }
    }

    private static void AssertCursor(long value, string kind)
    {
        if (value is > MaxSafeInteger or < -MaxSafeInteger)
            throw new InvalidOperationException($"Replicated state source {kind} cursor must be a safe integer");
    }
}

/// <summary>
/// A cold read-only state used by service consumers until a complete snapshot arrives (upstream <c>ReplicatedStateReplica</c>). It
/// hydrates from a base batch and applies validated update batches in strict sequence; any invalid batch or gap clears it. Upstream also
/// runs a strict-JSON revision validator here; immutable <see cref="JsonValue"/> trees are strict, acyclic JSON by construction.
/// </summary>
internal sealed class ReplicatedStateReplica(Action<Exception> reportError) : IReplicatedState<JsonValue>
{
    private readonly Lock gate = new();
    private readonly List<StateSubscriber<JsonValue>> subscribers = [];
    private JsonValue? value;
    private long? sequence;

    public JsonValue? Value { get { lock (gate) return value; } }

    public IDisposable Subscribe(Action<JsonValue, DurableContext, ReplicatedStateDelivery> listener) => SubscribeCore(ReplicatedState.Listener(listener));

    public IDisposable Subscribe(Func<JsonValue, DurableContext, ReplicatedStateDelivery, Task> listener) => SubscribeCore(ReplicatedState.Listener(listener));

    private IDisposable SubscribeCore(Func<JsonValue, DurableContext, ReplicatedStateDelivery, Task?> listener)
    {
        var subscriber = new StateSubscriber<JsonValue>(listener, reportError);
        lock (gate)
        {
            subscribers.Add(subscriber);
            if (value is not null)
                subscriber.Push(new StateDelivery<JsonValue>(value, ReplicatedState.ServiceDeliveryContext, ReplicatedStateDelivery.Hydrate(sequence!.Value)));
        }
        subscriber.Drain();
        return new Subscription(() =>
        {
            subscriber.Close();
            lock (gate) subscribers.Remove(subscriber);
        });
    }

    public void Hydrate(long sequence, IReadOnlyList<Op> ops, DurableContext context)
    {
        StateSubscriber<JsonValue>[] targets;
        lock (gate)
        {
            JsonValue next;
            try
            {
                if (!DeltaValidation.IsBase(ops)) throw new InvalidOperationException("Replicated state snapshot is not a base operation batch");
                next = DeltaApply.Apply(null, ops);
            }
            catch
            {
                ClearLocked();
                throw;
            }
            this.sequence = sequence;
            value = next;
            targets = PushAll(context, ReplicatedStateDelivery.Hydrate(sequence));
        }
        foreach (var subscriber in targets) subscriber.Drain();
    }

    public void Update(long sequence, IReadOnlyList<Op> ops, DurableContext context)
    {
        StateSubscriber<JsonValue>[] targets;
        lock (gate)
        {
            if (this.sequence is not { } current || value is null) throw new InvalidOperationException("Replicated state received an update before hydration");
            if (sequence != current + 1)
            {
                ClearLocked();
                throw new InvalidOperationException("Replicated state update sequence has a gap");
            }
            JsonValue next;
            try { next = DeltaApply.Apply(value, ops); }
            catch
            {
                ClearLocked();
                throw;
            }
            this.sequence = sequence;
            value = next;
            targets = PushAll(context, ReplicatedStateDelivery.Update(sequence));
        }
        foreach (var subscriber in targets) subscriber.Drain();
    }

    /// <summary>Forgets the value (a disconnect): pending deliveries are dropped, running callbacks finish, subscriptions stay.</summary>
    public void Clear()
    {
        lock (gate) ClearLocked();
    }

    private void ClearLocked()
    {
        value = null;
        sequence = null;
        foreach (var subscriber in subscribers) subscriber.Clear();
    }

    // #deliverAll: enqueue for everyone before any callback can publish another revision reentrantly; the caller drains after.
    private StateSubscriber<JsonValue>[] PushAll(DurableContext context, ReplicatedStateDelivery delivery)
    {
        var frame = new StateDelivery<JsonValue>(value!, context, delivery);
        StateSubscriber<JsonValue>[] targets = [.. subscribers];
        foreach (var subscriber in targets) subscriber.Push(frame);
        return targets;
    }
}

/// <summary>One pending delivery.</summary>
internal readonly record struct StateDelivery<T>(T Value, DurableContext Context, ReplicatedStateDelivery Delivery);

/// <summary>One public subscription, independent of producer and other subscriber progress (upstream <c>StateSubscriber</c>).</summary>
internal sealed class StateSubscriber<T>(Func<T, DurableContext, ReplicatedStateDelivery, Task?> listener, Action<Exception> reportError)
{
    /// <summary>At most this many deliveries wait behind the running callback.</summary>
    internal const int MaxPending = 100;

    private readonly Lock gate = new();
    private readonly Queue<StateDelivery<T>> pending = new();
    private bool running;
    private bool started;
    private bool closed;

    public void Push(StateDelivery<T> frame)
    {
        lock (gate)
        {
            if (closed) return;
            if (pending.Count == MaxPending)
            {
                // A cold replica can queue updates reentrantly before this subscriber's first hydration starts: keep that hydration.
                var keep = !started;
                var hydration = keep ? pending.Peek() : default;
                pending.Clear();
                if (keep) pending.Enqueue(hydration);
            }
            pending.Enqueue(frame);
        }
    }

    public void Drain()
    {
        lock (gate)
        {
            if (running || closed) return;
            running = true;
        }
        Run();
    }

    public void Clear()
    {
        lock (gate) pending.Clear();
    }

    public void Close()
    {
        lock (gate)
        {
            closed = true;
            pending.Clear();
        }
    }

    private void Run()
    {
        while (true)
        {
            StateDelivery<T> frame;
            lock (gate)
            {
                if (!pending.TryDequeue(out frame))
                {
                    running = false;
                    return;
                }
                started = true;
            }
            Task? task;
            try { task = listener(frame.Value, frame.Context, frame.Delivery); }
            catch (Exception error)
            {
                Report(error);
                continue;
            }
            if (task is null) continue;
            if (task.IsCompleted)
            {
                if (!task.IsCompletedSuccessfully) Report(Failure(task));
                continue;
            }
            task.ContinueWith(static (completed, state) =>
            {
                var self = (StateSubscriber<T>)state!;
                if (!completed.IsCompletedSuccessfully) self.Report(Failure(completed));
                self.Resume();
            }, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return;
        }
    }

    private void Resume()
    {
        lock (gate) running = false;
        Drain();
    }

    private void Report(Exception error)
    {
        try { reportError(error); }
        catch (Exception reportFailure) { ReplicatedState.ReportUnhandled(reportFailure); }
    }

    private static Exception Failure(Task task) => task.Exception switch
    {
        { InnerExceptions.Count: 1 } aggregate => aggregate.InnerExceptions[0],
        { } aggregate => aggregate,
        null => new TaskCanceledException(task),
    };
}

/// <summary>Maintains local publication order independently of how revisions are produced (upstream <c>ReplicatedStatePublisher</c>).</summary>
internal sealed class ReplicatedStatePublisher<T>(T initial, Action<Exception> reportError)
{
    private readonly Lock gate = new();
    // Upstream's Map keeps insertion order; each entry remembers the sequence it hydrated at.
    private readonly List<(StateSubscriber<T> Subscriber, long Hydrated)> subscribers = [];
    private readonly List<Action<IReadOnlyList<Op>, long, DurableContext>> sources = [];
    private readonly Queue<Publication> publications = new();
    private T value = initial;
    private long sequence;
    private bool delivering;

    public T Value { get { lock (gate) return value; } }

    public (T Value, long Sequence) Snapshot()
    {
        lock (gate) return (value, sequence);
    }

    public IDisposable Subscribe(Func<T, DurableContext, ReplicatedStateDelivery, Task?> listener)
    {
        var subscriber = new StateSubscriber<T>(listener, reportError);
        lock (gate)
        {
            subscribers.Add((subscriber, sequence));
            subscriber.Push(new StateDelivery<T>(value, ReplicatedState.ServiceDeliveryContext, ReplicatedStateDelivery.Hydrate(sequence)));
        }
        subscriber.Drain();
        return new Subscription(() =>
        {
            subscriber.Close();
            lock (gate) subscribers.RemoveAll(entry => ReferenceEquals(entry.Subscriber, subscriber));
        });
    }

    public IDisposable SubscribeSource(Action<IReadOnlyList<Op>, long, DurableContext> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        // A distinct wrapper per subscription, so two subscriptions of one delegate are independent and removal is exact.
        Action<IReadOnlyList<Op>, long, DurableContext> entry = (ops, published, context) => listener(ops, published, context);
        lock (gate) sources.Add(entry);
        return new Subscription(() =>
        {
            lock (gate) sources.Remove(entry);
        });
    }

    /// <summary>
    /// Commits the revision <paramref name="prepare"/> returns for the current value (null commits nothing; an exception commits nothing
    /// and propagates) and, unless another delivery owns the loop, delivers queued publications. Returns the isolated exact-listener
    /// failures of the publications this call delivered.
    /// </summary>
    public List<Exception> Publish(Func<T, (T Value, IReadOnlyList<Op> Ops)?> prepare, DurableContext context) =>
        Commit(prepare, context) ? Deliver() : [];

    /// <summary>
    /// The commit half of <see cref="Publish"/>: <paramref name="prepare"/> runs under the gate. Returns true when this call took
    /// ownership of the delivery loop; the caller must then call <see cref="Deliver"/>.
    /// </summary>
    public bool Commit(Func<T, (T Value, IReadOnlyList<Op> Ops)?> prepare, DurableContext context)
    {
        lock (gate)
        {
            if (prepare(value) is not { } revision) return false;
            value = revision.Value;
            sequence += 1;
            publications.Enqueue(new Publication(revision.Value, revision.Ops, sequence, context));
            if (delivering) return false;
            delivering = true;
            return true;
        }
    }

    /// <summary>The delivery half of <see cref="Publish"/>, run by the owner of the delivery loop until no publication is queued.</summary>
    public List<Exception> Deliver()
    {
        var errors = new List<Exception>();
        try
        {
            while (true)
            {
                Publication? publication;
                Action<IReadOnlyList<Op>, long, DurableContext>[] exact;
                lock (gate)
                {
                    if (!publications.TryDequeue(out publication))
                    {
                        delivering = false;
                        return errors;
                    }
                    exact = [.. sources];
                }
                foreach (var listener in exact)
                {
                    try { listener(publication.Ops, publication.Sequence, publication.Context); }
                    catch (Exception error) { errors.Add(error); }
                }
                // Snapshotted after the exact listeners, as upstream does: a subscription they add hydrates past this publication.
                (StateSubscriber<T> Subscriber, long Hydrated)[] targets;
                lock (gate) targets = [.. subscribers];
                var frame = new StateDelivery<T>(publication.Value, publication.Context, ReplicatedStateDelivery.Update(publication.Sequence));
                foreach (var (subscriber, hydrated) in targets)
                {
                    if (publication.Sequence <= hydrated) continue;
                    subscriber.Push(frame);
                    subscriber.Drain();
                }
            }
        }
        catch
        {
            lock (gate) delivering = false;
            throw;
        }
    }

    private sealed record Publication(T Value, IReadOnlyList<Op> Ops, long Sequence, DurableContext Context);
}

/// <summary>An idempotent unsubscribe.</summary>
internal sealed class Subscription(Action dispose) : IDisposable
{
    private Action? dispose = dispose;

    public void Dispose() => Interlocked.Exchange(ref dispose, null)?.Invoke();
}
