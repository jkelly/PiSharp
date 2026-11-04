// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/watch.ts (WatchEnd, WatchCall, NativeWatchCall, WatchHandle,
// Watchable, NativeWatchable, NativeWatchHandle, the shared WatchLifecycle, Watch and NativeWatch), which packages/chord/src/index.ts
// exports. Watch handles: the watch types and the one implementation of their lifecycle, for JSON and native values. Durable's document,
// view, task graph and event watches are built on them (work list area watches).
//
// Mappings:
// - Upstream's tagged unions are C# records: WatchCall<T> is WatchValueCall<T> or WatchFailedCall<T>, NativeWatchCall<T> is
//   NativeWatchValueCall<T> or NativeWatchFailedCall<T>, each with its Kind; WatchEnd is one record with a WatchEndKind and an Error. A
//   failure's error is upstream's `unknown`, so it is an object (a listener's throw is always an Exception in .NET).
// - Structural interfaces take the I prefix (IWatchHandle, IWatchable, INativeWatchable, INativeWatchHandle); a promise is a task.
// - An AbortSignal is a CancellationToken: ObserveAbort registers on it and the registration is dropped when the watch ends.
// - Upstream defers delivery with queueMicrotask. .NET has no microtask queue, so a watch takes a scheduler (`schedule`, which runs the
//   work it is given later and never inline), by default the thread pool; a Durable Session passes its line (Session.Defer), and tests
//   pass a queue they flush. Frames advanced before the scheduled work runs fold into one call, as frames advanced in one synchronous
//   stretch do upstream. The scheduler is a reference into the source too, so termination drops it. start() schedules a drain only when
//   something is pending: upstream's drain of nothing does nothing, and on a Session line it would be a job per watch.
// - Callers may be on any thread: one short lock guards a watch's state and is never held while the listener, detach, report or the
//   scheduler runs. Exactly one drain runs at a time (upstream's #running); it is released in the same lock that finds nothing pending,
//   so a frame advanced on another thread schedules the next drain. A listener's task continues on the thread that completes it, and the
//   watch takes the next call there.
// - The root replacement of a folded overflow or a recovery is `[["r", value]]`: a value that is not a JsonValue needs `json`, its JSON
//   form (a conversation view's ToJson()). NativeWatch's `replace` and Watch's `json` run under the watch's lock and must not call back
//   into it.
// - A report that throws is contained (ReplicatedState.UnhandledError) so the watch still ends; upstream lets it escape the delivery.
using System.Collections.Immutable;
using PiSharp.Durable.Chord.Delta;

namespace PiSharp.Durable.Chord;

/// <summary>Why a watch ended, for good (upstream <c>WatchEnd["kind"]</c>).</summary>
public enum WatchEndKind
{
    /// <summary><c>stop()</c> was called.</summary>
    Stopped,
    /// <summary>The context the watch was acquired with was aborted.</summary>
    Aborted,
    /// <summary>The source closed (its owner ended or closed).</summary>
    Ended,
    /// <summary>The listener threw or its task failed; <see cref="WatchEnd.Error"/> holds the exception.</summary>
    ListenerError,
    /// <summary>The source failed for good; <see cref="WatchEnd.Error"/> holds what failed it.</summary>
    Failed,
}

/// <summary>
/// Terminal result of one watch, tagged by <see cref="Kind"/> (upstream <c>WatchEnd</c>). A watch ends only for good; a failure its source
/// recovers from is a failed call (<see cref="WatchFailedCall{T}"/>), and the watch stays open.
/// </summary>
public sealed record WatchEnd
{
    private WatchEnd(WatchEndKind kind, object? error)
    {
        Kind = kind;
        Error = error;
    }

    /// <summary><c>{ kind: "stopped" }</c>.</summary>
    public static WatchEnd Stopped { get; } = new(WatchEndKind.Stopped, null);

    /// <summary><c>{ kind: "aborted" }</c>.</summary>
    public static WatchEnd Aborted { get; } = new(WatchEndKind.Aborted, null);

    /// <summary><c>{ kind: "ended" }</c>.</summary>
    public static WatchEnd Ended { get; } = new(WatchEndKind.Ended, null);

    /// <summary>Why the watch ended.</summary>
    public WatchEndKind Kind { get; }

    /// <summary>The listener's exception for <see cref="WatchEndKind.ListenerError"/>, what failed the source for
    /// <see cref="WatchEndKind.Failed"/>; otherwise null.</summary>
    public object? Error { get; }

    /// <summary><c>{ kind: "listener_error", error }</c>.</summary>
    public static WatchEnd ListenerError(Exception error) => new(WatchEndKind.ListenerError, error ?? throw new ArgumentNullException(nameof(error)));

    /// <summary><c>{ kind: "failed", error }</c>: the source failed for good with <paramref name="error"/>.</summary>
    public static WatchEnd Failed(object? error) => new(WatchEndKind.Failed, error);
}

/// <summary>Whether a watch call is a value or a failure (upstream <c>WatchCall["kind"]</c>).</summary>
public enum WatchCallKind
{
    /// <summary>A new value.</summary>
    Value,
    /// <summary>The source cannot compute its value now; the watch stays open.</summary>
    Failed,
}

/// <summary>
/// One call of a JSON watch's listener, and one frame a source hands its watch, tagged by <see cref="Kind"/> (upstream <c>WatchCall</c>):
/// a <see cref="WatchValueCall{T}"/> or a <see cref="WatchFailedCall{T}"/>. Plain data: the frame's context is the listener's second
/// argument, never part of the call.
/// </summary>
/// <typeparam name="T">The watched value type.</typeparam>
public abstract record WatchCall<T>
{
    private protected WatchCall() { }

    /// <summary>The call's kind.</summary>
    public abstract WatchCallKind Kind { get; }
}

/// <summary>
/// A value call (upstream <c>{ kind: "value", value, ops, retired }</c>): the newest value, the ops that turn the previous value into it,
/// and whether the document the watch follows was retired since the previous value call. A retirement does not end the watch:
/// <paramref name="Value"/> is then what the address shows after it (the source decides; a Durable document keeps the last value the watch
/// was given, with no ops), and a document created there later arrives as <c>[["r", value]]</c>, possibly in the same call.
/// </summary>
/// <param name="Value">The newest value.</param>
/// <param name="Ops">The ops from the previous value to <paramref name="Value"/>.</param>
/// <param name="Retired">Whether the followed document was retired since the previous value call.</param>
public sealed record WatchValueCall<T>(T Value, ImmutableArray<Op> Ops, bool Retired = false) : WatchCall<T>
{
    /// <inheritdoc/>
    public override WatchCallKind Kind => WatchCallKind.Value;
}

/// <summary>
/// A failed call (upstream <c>{ kind: "failed", error }</c>): the source cannot compute its value now; the watch stays open, and the next
/// value arrives as <c>[["r", value]]</c>. <see cref="IWatchHandle{T}.Value"/> keeps the last value.
/// </summary>
/// <param name="Error">What failed.</param>
public sealed record WatchFailedCall<T>(object? Error) : WatchCall<T>
{
    /// <inheritdoc/>
    public override WatchCallKind Kind => WatchCallKind.Failed;
}

/// <summary>One call of a native watch's listener (upstream <c>NativeWatchCall</c>): the new value, or a failure its source may recover
/// from, as in <see cref="WatchCall{T}"/>.</summary>
/// <typeparam name="T">The watched value type.</typeparam>
public abstract record NativeWatchCall<T>
{
    private protected NativeWatchCall() { }

    /// <summary>The call's kind.</summary>
    public abstract WatchCallKind Kind { get; }
}

/// <summary>A native value call (upstream <c>{ kind: "value", value }</c>).</summary>
/// <param name="Value">The new value.</param>
public sealed record NativeWatchValueCall<T>(T Value) : NativeWatchCall<T>
{
    /// <inheritdoc/>
    public override WatchCallKind Kind => WatchCallKind.Value;
}

/// <summary>A native failed call (upstream <c>{ kind: "failed", error }</c>); the watch stays open.</summary>
/// <param name="Error">What failed.</param>
public sealed record NativeWatchFailedCall<T>(object? Error) : NativeWatchCall<T>
{
    /// <inheritdoc/>
    public override WatchCallKind Kind => WatchCallKind.Failed;
}

/// <summary>
/// Serialized observation of an immutable value (upstream <c>WatchHandle</c>): one listener call at a time; changes made meanwhile arrive
/// folded into the next call.
/// </summary>
/// <typeparam name="T">The immutable value type.</typeparam>
public interface IWatchHandle<T>
{
    /// <summary>The acquisition revision before <see cref="Start"/>; the latest delivered immutable revision afterward (a failed call keeps
    /// it).</summary>
    T Value { get; }

    /// <summary>Installs the sole asynchronous listener. It is never invoked inline. Throws when already started or stopped.</summary>
    void Start(Func<WatchCall<T>, DurableContext, Task> listener);

    /// <summary>Idempotently stops future callbacks and returns this watch's terminal result.</summary>
    Task<WatchEnd> StopAsync();

    /// <summary>Completes when the watch terminates; an already-running callback remains caller-owned.</summary>
    Task<WatchEnd> Closed { get; }
}

/// <summary>
/// A value that changes and can be watched (upstream <c>Watchable</c>): documents, derives, members of references. Values are immutable by
/// convention (JSON for state); each watch starts at the current value and then gets the changes, folded while its listener is busy.
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
public interface IWatchable<T>
{
    /// <summary>The current value.</summary>
    T Value { get; }

    /// <summary>A watch holding the current value, then its changes; aborting the context ends it.</summary>
    Task<IWatchHandle<T>> WatchAsync(DurableContext context);
}

/// <summary>
/// An in-process value that changes and can be watched (upstream <c>NativeWatchable</c>); it may hold anything, exceptions and delegates
/// included. A change is a new value by identity, delivered without ops.
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
public interface INativeWatchable<T>
{
    /// <summary>The current value.</summary>
    T Value { get; }

    /// <summary>A watch holding the current value, then one frame per new value; aborting the context ends it.</summary>
    Task<INativeWatchHandle<T>> WatchAsync(DurableContext context);
}

/// <summary>Serialized observation of an in-process value (upstream <c>NativeWatchHandle</c>): as <see cref="IWatchHandle{T}"/>, but each
/// new value is its own call, in order, without ops.</summary>
/// <typeparam name="T">The value type.</typeparam>
public interface INativeWatchHandle<T>
{
    /// <summary>The acquisition value before <see cref="Start"/>; the latest delivered value afterward (a failed call keeps it).</summary>
    T Value { get; }

    /// <summary>Installs the sole asynchronous listener. It is never invoked inline. Throws when already started or stopped.</summary>
    void Start(Func<NativeWatchCall<T>, DurableContext, Task> listener);

    /// <summary>Idempotently stops future callbacks and returns this watch's terminal result.</summary>
    Task<WatchEnd> StopAsync();

    /// <summary>Completes when the watch terminates; an already-running callback remains caller-owned.</summary>
    Task<WatchEnd> Closed { get; }
}

/// <summary>
/// The lifecycle both watch handles share (upstream's internal <c>WatchLifecycle</c>): serialized delivery and termination. A source feeds
/// it and ends it explicitly; termination drops every reference the watch holds into its source, so a stopped watch that is still
/// referenced keeps nothing but its last value alive. Only <see cref="Watch{T}"/> and <see cref="NativeWatch{T}"/> derive from it.
/// </summary>
/// <typeparam name="T">The watched value type.</typeparam>
/// <typeparam name="TCall">The listener's call type.</typeparam>
public abstract class WatchLifecycle<T, TCall> where TCall : class
{
    private static readonly Action<Action> ThreadPoolSchedule = static work => ThreadPool.UnsafeQueueUserWorkItem(static run => run(), work, preferLocal: false);

    private readonly TaskCompletionSource<WatchEnd> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action run;
    private Action? detach;
    private Action<Exception>? report;
    private Action<Action>? schedule;
    private T value;
    private Func<TCall, DurableContext, Task>? listener;
    private bool started;
    private bool scheduled;
    private bool running;
    // Set by EndAfterDelivery(): the source sends nothing more; the watch ends `ended` once it delivered the rest.
    private bool ending;
    // Set once, by Terminate().
    private WatchEnd? end;
    private bool abortInstalled;
    private CancellationTokenRegistration abortRegistration;

    private protected WatchLifecycle(T value, Action detach, Action<Exception> report, Action<Action>? schedule)
    {
        ArgumentNullException.ThrowIfNull(detach);
        ArgumentNullException.ThrowIfNull(report);
        this.value = value;
        this.detach = detach;
        this.report = report;
        this.schedule = schedule ?? ThreadPoolSchedule;
        run = Run;
    }

    /// <summary>Guards the watch's state, the subclass's pending frames included.</summary>
    private protected Lock Gate { get; } = new();

    /// <summary>The acquisition value before <see cref="Start"/>; the latest delivered value afterward (a failed call keeps it).</summary>
    public T Value
    {
        get
        {
            lock (Gate) return value;
        }
    }

    /// <summary>Completes when the watch terminates; an already-running callback remains caller-owned.</summary>
    public Task<WatchEnd> Closed => closed.Task;

    /// <summary>Installs the sole asynchronous listener. It is never invoked inline. Throws when already started or stopped.</summary>
    public void Start(Func<TCall, DurableContext, Task> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        bool pending;
        lock (Gate)
        {
            if (started) throw new InvalidOperationException("Watch is already started");
            if (end is not null) throw new InvalidOperationException("Watch is stopped");
            started = true;
            this.listener = listener;
            pending = PendingContext is not null;
        }
        if (pending) Schedule();
    }

    /// <summary>Idempotently stops future callbacks and returns this watch's terminal result.</summary>
    public Task<WatchEnd> StopAsync()
    {
        Terminate(WatchEnd.Stopped);
        return closed.Task;
    }

    /// <summary>Ends the watch <see cref="WatchEndKind.Aborted"/> when <paramref name="signal"/> is cancelled (upstream
    /// <c>observeAbort</c>); installed once. An already-cancelled signal ends it at once.</summary>
    public void ObserveAbort(CancellationToken signal)
    {
        lock (Gate)
        {
            if (abortInstalled) throw new InvalidOperationException("Watch abort is already installed");
            if (end is not null) return;
            abortInstalled = true;
        }
        // An already-cancelled token runs the callback here, outside the lock.
        var registration = signal.UnsafeRegister(static state => ((WatchLifecycle<T, TCall>)state!).Abort(), this);
        bool keep;
        lock (Gate)
        {
            keep = end is null;
            if (keep) abortRegistration = registration;
        }
        if (!keep) registration.Unregister();
    }

    /// <summary>Ends the watch <see cref="WatchEndKind.Aborted"/>: its acquisition context was aborted.</summary>
    public void Abort() => Terminate(WatchEnd.Aborted);

    /// <summary>
    /// Ends the watch with <paramref name="end"/> at once, dropping what is pending: the source closed (<see cref="WatchEndKind.Ended"/>),
    /// or failed for good (<see cref="WatchEndKind.Failed"/>). A failure the source may recover from is a failed call instead.
    /// </summary>
    public void End(WatchEnd end)
    {
        ArgumentNullException.ThrowIfNull(end);
        Terminate(end);
    }

    /// <summary>
    /// The source sends nothing more after its last frames, such as a retirement: take no more frames, deliver what is pending, then end
    /// <see cref="WatchEndKind.Ended"/>, after the running call returns, if one runs. Until then the watch stays attached, so
    /// <see cref="End"/>, <see cref="StopAsync"/>, an abort and a throwing listener still end it first; a watch never started keeps what is
    /// pending until one of them or <see cref="Start"/>.
    /// </summary>
    public void EndAfterDelivery()
    {
        lock (Gate)
        {
            if (end is not null || ending) return;
            ending = true;
            if (running || PendingContext is not null) return;
        }
        Terminate(WatchEnd.Ended);
    }

    /// <summary>Whether the watch still takes frames: it has not ended, and its source has not ended. Read under <see cref="Gate"/>.</summary>
    private protected bool IsOpen => end is null && !ending;

    /// <summary>The context of the newest frame the next call covers, or null when nothing is pending. Read under <see cref="Gate"/>.</summary>
    private protected abstract DurableContext? PendingContext { get; }

    /// <summary>Takes the next call; called under <see cref="Gate"/>, only while something is pending.</summary>
    private protected abstract TCall Next();

    /// <summary>The value a call delivers, when it is a value call.</summary>
    private protected abstract bool TryGetValue(TCall call, out T value);

    /// <summary>Drops everything pending and every reference into the source; called under <see cref="Gate"/>, once, on termination.</summary>
    private protected abstract void Release();

    /// <summary>Something became pending: delivers it once started, never inline. Called outside <see cref="Gate"/>.</summary>
    private protected void Pending() => Schedule();

    private void Schedule()
    {
        Action<Action>? post;
        lock (Gate)
        {
            if (!started || scheduled || running || end is not null) return;
            scheduled = true;
            post = schedule;
        }
        post!(run);
    }

    private void Run()
    {
        lock (Gate) scheduled = false;
        _ = DrainAsync();
    }

    /// <summary>
    /// Delivers until nothing is pending; what arrives during a call is taken after it returns. A watch whose source ended ends once it
    /// delivered the rest and its last call returned.
    /// </summary>
    private async Task DrainAsync()
    {
        lock (Gate)
        {
            if (running || end is not null || !started) return;
            running = true;
        }
        var released = false;
        try
        {
            while (true)
            {
                Func<TCall, DurableContext, Task>? deliver;
                TCall? call = null;
                DurableContext? frameContext;
                lock (Gate)
                {
                    deliver = end is null ? listener : null;
                    if (deliver is null) break;
                    frameContext = PendingContext;
                    if (frameContext is null)
                    {
                        if (!ending)
                        {
                            // Released in the lock that found nothing pending, so a frame advanced next schedules a new drain.
                            running = false;
                            released = true;
                            return;
                        }
                    }
                    else
                    {
                        call = Next();
                        if (TryGetValue(call, out var delivered)) value = delivered;
                    }
                }
                if (call is null)
                {
                    Terminate(WatchEnd.Ended);
                    break;
                }
                try
                {
                    var task = deliver(call, frameContext!.WithoutCancellation());
                    if (task is not null) await task.ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    Action<Exception>? reporter;
                    lock (Gate) reporter = end is null ? report : null;
                    if (reporter is not null)
                    {
                        Contain(reporter, error);
                        Terminate(WatchEnd.ListenerError(error));
                    }
                    break;
                }
            }
        }
        catch (Exception error)
        {
            // A throwing detach: upstream's drain would reject unobserved.
            ReplicatedState.ReportUnhandled(error);
        }
        finally
        {
            if (!released)
            {
                lock (Gate) running = false;
            }
        }
    }

    private static void Contain(Action<Exception> reporter, Exception error)
    {
        try { reporter(error); }
        catch (Exception reportError) { ReplicatedState.ReportUnhandled(reportError); }
    }

    /// <summary>Ends the watch, once: detaches, drops what is pending and every reference into the source, and completes
    /// <see cref="Closed"/>. A callback already running keeps running.</summary>
    private void Terminate(WatchEnd result)
    {
        Action? release;
        CancellationTokenRegistration registration;
        lock (Gate)
        {
            if (end is not null) return;
            end = result;
            release = detach;
            detach = null;
            report = null;
            listener = null;
            schedule = null;
            Release();
            registration = abortRegistration;
            abortRegistration = default;
            abortInstalled = false;
        }
        registration.Unregister();
        release?.Invoke();
        closed.TrySetResult(result);
    }
}

/// <summary>
/// The watch handle of a JSON source (upstream <c>Watch</c>). Frames that arrive before <see cref="WatchLifecycle{T, TCall}.Start"/> or
/// while the listener runs wait in one <see cref="PendingOps"/> queue; the next call gets the newest value, the folded ops of every frame
/// since the previous call (or <c>[["r", newest]]</c> after the queue overflowed), <c>Retired</c> when any of them was a retirement, and
/// the newest frame's context. A frame delivered alone is passed on as the source made it. A failure (<see cref="Fail"/>) drops the pending
/// frames and waits as one failed call; a value after it replaces it, and the first value call after a failure is <c>[["r", value]]</c>.
/// <c>detach</c> unsubscribes it from its source on termination; <c>report</c> gets a listener's throw, which also ends the watch.
/// </summary>
/// <typeparam name="T">The watched value type: JSON, or a view of it whose JSON form <c>json</c> gives.</typeparam>
public sealed class Watch<T> : WatchLifecycle<T, WatchCall<T>>, IWatchHandle<T>
{
    private readonly PendingOps ops = new();
    private Func<T, JsonValue>? json;
    // The source's call while it is the only frame pending: the call passes it on, with no allocation. A second frame drops it, so its ops
    // stay only as long as PendingOps keeps them (folded, or dropped by an overflow).
    private WatchValueCall<T>? lone;
    // The newest frame's value and context, while a call is pending; the context also marks that one is.
    private T newestValue = default!;
    private DurableContext? newestContext;
    // A pending frame was a retirement.
    private bool retired;
    // The newest frame was a failure: the next call is this.
    private WatchFailedCall<T>? failure;
    // A failure came after the last value call: the next value call replaces the root, and ops are not kept.
    private bool replace;

    /// <summary>A watch at <paramref name="value"/>, the acquisition revision.</summary>
    /// <param name="value">The acquisition revision.</param>
    /// <param name="detach">Unsubscribes the watch from its source; called once, on termination.</param>
    /// <param name="report">Gets a listener's throw, which also ends the watch.</param>
    /// <param name="json">The JSON form of a value for a root replacement; by default the value itself, which must then be a
    /// <see cref="JsonValue"/> or null (JSON null).</param>
    /// <param name="schedule">Runs delivery work later, never inline (upstream <c>queueMicrotask</c>); by default the thread pool.</param>
    public Watch(T value, Action detach, Action<Exception> report, Func<T, JsonValue>? json = null, Action<Action>? schedule = null)
        : base(value, detach, report, schedule)
    {
        this.json = json;
    }

    /// <summary>
    /// Queues the frame <paramref name="call"/>. A value's ops turn the previous value into it, and <c>Retired</c> says the document the
    /// watch follows was retired, which the next value call reports however many frames it folds; a failed call is <see cref="Fail"/>.
    /// </summary>
    public void Advance(WatchCall<T> call, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(context);
        if (call is WatchFailedCall<T> failed)
        {
            Queue(failed, context);
            return;
        }
        var valueCall = (WatchValueCall<T>)call;
        lock (Gate)
        {
            if (!IsOpen) return;
            if (replace)
            {
                failure = null;
            }
            else
            {
                lone = newestContext is null ? valueCall : null;
                ops.Push(valueCall.Ops);
            }
            newestValue = valueCall.Value;
            newestContext = context;
            if (valueCall.Retired) retired = true;
        }
        Pending();
    }

    /// <summary>
    /// Queues a failure the source may recover from: the pending frames are dropped (<c>Value</c> keeps the last delivered value), the
    /// listener gets a <see cref="WatchFailedCall{T}"/> with <paramref name="error"/>, and the next value arrives as <c>[["r", value]]</c>.
    /// </summary>
    public void Fail(object? error, DurableContext context) => Queue(new WatchFailedCall<T>(error), context);

    private void Queue(WatchFailedCall<T> call, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (Gate)
        {
            if (!IsOpen) return;
            ops.Take();
            lone = null;
            newestValue = default!;
            failure = call;
            newestContext = context;
            replace = true;
        }
        Pending();
    }

    private protected override DurableContext? PendingContext => newestContext;

    private protected override WatchCall<T> Next()
    {
        // After a failure the call is that failure, or the value after it; otherwise one field read decides.
        if (replace) return NextAfterFailure();
        var single = lone;
        // Set with the context, which says a frame is pending.
        var value = newestValue;
        var batch = ops.Take();
        var wasRetired = retired;
        lone = null;
        newestValue = default!;
        newestContext = null;
        retired = false;
        // One frame never folds or overflows: its call is the listener's.
        if (single is not null) return single;
        ImmutableArray<Op> folded = batch.Kind == PendingTakeKind.Ops ? batch.Ops : [new ReplaceOp(Json(value))];
        return new WatchValueCall<T>(value, folded, wasRetired);
    }

    /// <summary>The pending failure, or the value after one as a root replacement (no ops were kept since the failure).</summary>
    private WatchCall<T> NextAfterFailure()
    {
        var pendingFailure = failure;
        newestContext = null;
        if (pendingFailure is not null)
        {
            failure = null;
            return pendingFailure;
        }
        var value = newestValue;
        var wasRetired = retired;
        newestValue = default!;
        retired = false;
        replace = false;
        return new WatchValueCall<T>(value, [new ReplaceOp(Json(value))], wasRetired);
    }

    private protected override bool TryGetValue(WatchCall<T> call, out T value)
    {
        if (call is WatchValueCall<T> valueCall)
        {
            value = valueCall.Value;
            return true;
        }
        value = default!;
        return false;
    }

    private protected override void Release()
    {
        ops.Take();
        json = null;
        lone = null;
        newestValue = default!;
        newestContext = null;
        retired = false;
        failure = null;
        replace = false;
    }

    // Values of a JSON watch are JSON; T is the source's narrower or wider view of them (a typed document, a conversation view).
    private JsonValue Json(T value) => json is not null ? json(value)
        : value is null ? JsonValue.Null
        : value is JsonValue jsonValue ? jsonValue
        : throw new InvalidOperationException($"A watch of {typeof(T).Name} needs the JSON form of its values to replace the root");
}

/// <summary>
/// The watch handle of a native source (upstream <c>NativeWatch</c>): every value in order, one call each; past 100 pending values they
/// collapse to one, <c>replace()</c> or by default the newest value. A failure (<see cref="Fail"/>) drops the pending values and waits as
/// one failed call; a value after it replaces it. <c>detach</c> and <c>report</c> as for <see cref="Watch{T}"/>.
/// </summary>
/// <typeparam name="T">The watched value type; values are delivered by identity.</typeparam>
public sealed class NativeWatch<T> : WatchLifecycle<T, NativeWatchCall<T>>, INativeWatchHandle<T>
{
    /// <summary>Maximum values retained behind one unavailable native watch listener (upstream <c>MAX_PENDING_NATIVE_VALUES</c>).</summary>
    private const int MaxPendingValues = 100;

    // Pending values in order, each with its frame's context, from `head` on. Values are kept raw: the call a listener gets is built when it
    // is taken, so a value an overflow drops costs no object. A read index takes a backlog in constant time per value; taken slots are
    // cleared at once, the list is emptied once all of it was taken, and its taken prefix dropped after 100 values.
    private readonly List<(T Value, DurableContext Context)> pending = [];
    private int head;
    private Func<T>? replace;
    // A pending failure and its frame's context; nothing else is pending meanwhile.
    private NativeWatchFailedCall<T>? failure;
    private DurableContext? failureContext;

    /// <summary>A watch at <paramref name="value"/>, the acquisition value.</summary>
    /// <param name="value">The acquisition value.</param>
    /// <param name="detach">Unsubscribes the watch from its source; called once, on termination.</param>
    /// <param name="report">Gets a listener's throw, which also ends the watch.</param>
    /// <param name="replace">The value an overflow of pending values collapses to; by default the newest value.</param>
    /// <param name="schedule">Runs delivery work later, never inline (upstream <c>queueMicrotask</c>); by default the thread pool.</param>
    public NativeWatch(T value, Action detach, Action<Exception> report, Func<T>? replace = null, Action<Action>? schedule = null)
        : base(value, detach, report, schedule)
    {
        this.replace = replace;
    }

    /// <summary>Queues <paramref name="value"/>, the source's new value; it replaces a pending failure.</summary>
    public void Advance(T value, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (Gate)
        {
            if (!IsOpen) return;
            failure = null;
            failureContext = null;
            if (pending.Count - head < MaxPendingValues)
            {
                pending.Add((value, context));
            }
            else
            {
                pending.Clear();
                head = 0;
                pending.Add((replace is null ? value : replace(), context));
            }
        }
        Pending();
    }

    /// <summary>
    /// Queues a failure the source may recover from: the pending values are dropped (<c>Value</c> keeps the last delivered value), and the
    /// listener gets a <see cref="NativeWatchFailedCall{T}"/> with <paramref name="error"/>.
    /// </summary>
    public void Fail(object? error, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (Gate)
        {
            if (!IsOpen) return;
            pending.Clear();
            head = 0;
            failure = new NativeWatchFailedCall<T>(error);
            failureContext = context;
        }
        Pending();
    }

    private protected override DurableContext? PendingContext => failureContext ?? (head < pending.Count ? pending[head].Context : null);

    private protected override NativeWatchCall<T> Next()
    {
        var pendingFailure = failure;
        if (pendingFailure is not null)
        {
            failure = null;
            failureContext = null;
            return pendingFailure;
        }
        var value = pending[head].Value;
        if (head + 1 == pending.Count)
        {
            pending.Clear();
            head = 0;
        }
        else if (head + 1 >= MaxPendingValues)
        {
            // A backlog that never drains: drop the taken prefix, at most once per 100 values, so the list stays bounded.
            pending.RemoveRange(0, head + 1);
            head = 0;
        }
        else
        {
            pending[head] = default;
            head++;
        }
        return new NativeWatchValueCall<T>(value);
    }

    private protected override bool TryGetValue(NativeWatchCall<T> call, out T value)
    {
        if (call is NativeWatchValueCall<T> valueCall)
        {
            value = valueCall.Value;
            return true;
        }
        value = default!;
        return false;
    }

    private protected override void Release()
    {
        pending.Clear();
        head = 0;
        replace = null;
        failure = null;
        failureContext = null;
    }
}
