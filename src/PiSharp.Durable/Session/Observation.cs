// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/session/observation.ts (CommittedStateSource,
// SessionSourceAttachment, CommittedWatch, RETIREMENT_OPERATIONS, the 100-frame watch limit).
//
// Upstream defers every attachment drain and watch delivery with queueMicrotask. A .NET process has no microtask queue, so the owner
// passes a scheduler (Session.Defer): work deferred while a commit publishes runs on the Session line right after that commit's
// publication and before the commit's task completes, which is where upstream's microtasks run; work deferred from outside the line
// (a watch started by its caller) becomes its own line job, never run inline. Callbacks run outside every lock here. A watch listener
// continues on whatever thread completes its task, and the watch then delivers its next buffered frame there.
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

namespace PiSharp.Durable;

/// <summary>Defers an observer's delivery work (upstream <c>queueMicrotask</c>); see the file header.</summary>
internal delegate void ObservationScheduler(Action work);

/// <summary>A Session-side observer of one committed incarnation or view.</summary>
internal interface ICommittedObserver<T>
{
    /// <summary>Forwards one committed revision; a null value retires the observer.</summary>
    void Advance(T value, ImmutableArray<Op> ops, DurableContext context);

    /// <summary>Ends the observation because the Session closes.</summary>
    void CloseSession();
}

/// <summary>Constants shared by the observers.</summary>
internal static class Observation
{
    /// <summary>Maximum exact committed frames retained behind one unavailable watch listener.</summary>
    public const int MaxPendingWatchFrames = 100;

    /// <summary>Canonical terminal update for a retired document incarnation: <c>[["r", null]]</c>.</summary>
    public static readonly ImmutableArray<Op> RetirementOperations = [new ReplaceOp(JsonValue.Null)];
}

/// <summary>
/// Session-to-Chord bridge owned one-to-one by one attached state: a document, or a conversation view (upstream
/// <c>CommittedStateSource</c>). A null value retires it.
/// </summary>
internal sealed class CommittedStateSource<T> : IReplicatedStateSource<T>, ICommittedObserver<T> where T : class?
{
    private readonly Lock gate = new();
    private readonly List<SessionSourceAttachment<T>> attachments = [];
    private readonly ObservationScheduler schedule;
    private Action? release;
    // Released on disposal.
    private T? value;
    private long cursor;
    private bool retired;
    private bool closed;

    public CommittedStateSource(T value, Action release, ObservationScheduler schedule)
    {
        this.value = value;
        this.release = release;
        this.schedule = schedule;
    }

    public IReplicatedStateSourceAttachment<T> Attach()
    {
        lock (gate)
        {
            if (closed) throw new InvalidOperationException("State source is closed");
            SessionSourceAttachment<T>? attachment = null;
            attachment = new SessionSourceAttachment<T>(new ReplicatedStateSnapshot<T>(value!, cursor), schedule, () =>
            {
                bool last;
                lock (gate) last = attachments.Remove(attachment!) && attachments.Count == 0;
                if (last) FinishDisposal();
            });
            attachments.Add(attachment);
            return attachment;
        }
    }

    public void Advance(T value, ImmutableArray<Op> ops, DurableContext context)
    {
        SessionSourceAttachment<T>[] targets;
        ReplicatedStateSourceFrame<T> frame;
        lock (gate)
        {
            if (closed || retired) return;
            this.value = value;
            cursor += 1;
            if (value is null) retired = true;
            frame = new ReplicatedStateSourceFrame<T>(cursor, value, ops, context);
            targets = [.. attachments];
        }
        foreach (var attachment in targets) attachment.Publish(frame);
    }

    public void CloseSession()
    {
        SessionSourceAttachment<T>[] targets;
        lock (gate)
        {
            if (closed) return;
            targets = [.. attachments];
        }
        foreach (var attachment in targets) attachment.Dispose();
        FinishDisposal();
    }

    private void FinishDisposal()
    {
        Action? done;
        lock (gate)
        {
            if (closed) return;
            closed = true;
            value = default;
            done = release;
            release = null;
        }
        done?.Invoke();
    }
}

/// <summary>One attachment of a <see cref="CommittedStateSource{T}"/> (upstream <c>SessionSourceAttachment</c>).</summary>
internal sealed class SessionSourceAttachment<T>(ReplicatedStateSnapshot<T> snapshot, ObservationScheduler schedule, Action release)
    : IReplicatedStateSourceAttachment<T>
{
    private readonly Lock gate = new();
    private readonly Queue<ReplicatedStateSourceFrame<T>> frames = new();
    private Action? release = release;
    private Action<ReplicatedStateSourceFrame<T>>? listener;
    private bool activated;
    private bool disposed;
    private bool scheduled;
    private bool delivering;

    public ReplicatedStateSnapshot<T> Snapshot { get; } = snapshot;

    public void Activate(Action<ReplicatedStateSourceFrame<T>> listener)
    {
        lock (gate)
        {
            if (activated) throw new InvalidOperationException("State attachment is already active");
            if (disposed) throw new InvalidOperationException("State attachment is disposed");
            activated = true;
            this.listener = listener;
        }
        Drain();
    }

    public void Publish(ReplicatedStateSourceFrame<T> frame)
    {
        lock (gate)
        {
            if (disposed) return;
            frames.Enqueue(frame);
            if (!activated || delivering || scheduled) return;
            scheduled = true;
        }
        schedule(() =>
        {
            lock (gate)
            {
                scheduled = false;
                if (disposed) return;
            }
            try { Drain(); }
            catch (Exception)
            {
                // Chord's frame listener contains source-contract failures. Isolate an unexpected direct listener failure to this
                // attachment as well.
                Dispose();
            }
        });
    }

    public void Dispose()
    {
        Action? done;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            frames.Clear();
            listener = null;
            done = release;
            release = null;
        }
        done?.Invoke();
    }

    private void Drain()
    {
        Action<ReplicatedStateSourceFrame<T>> deliver;
        lock (gate)
        {
            if (listener is null || delivering || disposed) return;
            deliver = listener;
            delivering = true;
        }
        try
        {
            while (true)
            {
                ReplicatedStateSourceFrame<T> frame;
                lock (gate)
                {
                    if (disposed || !frames.TryDequeue(out frame!)) break;
                }
                deliver(frame);
            }
        }
        finally
        {
            lock (gate) delivering = false;
        }
    }
}

/// <summary>
/// Serialized exact-frame watch bound to one document incarnation or conversation view (upstream <c>CommittedWatch</c>). A null value
/// retires it.
/// </summary>
internal sealed class CommittedWatch<T> : IWatchHandle<T>, ICommittedObserver<T> where T : JsonValue?
{
    private readonly Lock gate = new();
    private readonly Action detach;
    private readonly Func<T>? replace;
    private readonly ObservationScheduler schedule;
    // Completed off the terminating thread, so an awaiting caller never runs inside a close listener or a line job.
    private readonly TaskCompletionSource<WatchEnd> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<WatchFrame> pending = [];
    private T value;
    private Func<T, ImmutableArray<Op>, DurableContext, Task>? listener;
    private bool started;
    private bool scheduled;
    private bool running;
    private bool detached;
    private bool retired;
    private WatchEnd? end;
    private bool resolved;
    private CancellationTokenRegistration cancellation;

    /// <summary><paramref name="replace"/> gives the value an overflow delivers; by default the newest value.</summary>
    public CommittedWatch(T value, Action detach, ObservationScheduler schedule, Func<T>? replace = null)
    {
        this.value = value;
        this.detach = detach;
        this.schedule = schedule;
        this.replace = replace;
    }

    public T Value { get { lock (gate) return value; } }

    public Task<WatchEnd> Closed => closed.Task;

    public void Start(Func<T, ImmutableArray<Op>, DurableContext, Task> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        bool deliver;
        lock (gate)
        {
            if (started) throw new InvalidOperationException("Watch is already started");
            if (end is not null) throw new InvalidOperationException("Watch is stopped");
            started = true;
            this.listener = listener;
            deliver = pending.Count > 0;
        }
        if (deliver) Schedule();
    }

    public Task<WatchEnd> StopAsync()
    {
        Terminate(WatchEnd.Stopped);
        return closed.Task;
    }

    /// <summary>Stops the watch when <paramref name="context"/> is cancelled (upstream <c>observeCancellation</c>).</summary>
    public void ObserveCancellation(DurableContext context)
    {
        lock (gate)
        {
            if (cancellation != default) throw new InvalidOperationException("Watch cancellation is already installed");
            if (end is not null) return;
        }
        var registration = context.CancellationToken.UnsafeRegister(static state => ((CommittedWatch<T>)state!).Cancel(), this);
        bool dispose;
        lock (gate)
        {
            dispose = resolved;
            if (!dispose) cancellation = registration;
        }
        if (dispose) registration.Unregister();
        if (context.IsCancellationRequested) Cancel();
    }

    public void Cancel() => Terminate(WatchEnd.Cancelled);

    public void CloseSession() => Terminate(WatchEnd.SessionClosed);

    public void Advance(T value, ImmutableArray<Op> ops, DurableContext context)
    {
        bool deliver;
        lock (gate)
        {
            if (end is not null || retired) return;
            if (value is null) retired = true;
            if (pending.Count >= Observation.MaxPendingWatchFrames)
            {
                pending.Clear();
                var replacement = replace is null ? value : replace();
                pending.Add(new WatchFrame(replacement, [new ReplaceOp((JsonValue?)replacement ?? JsonValue.Null)], context));
            }
            else
            {
                pending.Add(new WatchFrame(value, ops, context));
            }
            deliver = started;
        }
        if (deliver) Schedule();
    }

    private void Schedule()
    {
        lock (gate)
        {
            if (scheduled || running || end is not null) return;
            scheduled = true;
        }
        schedule(() =>
        {
            lock (gate) scheduled = false;
            _ = DrainAsync();
        });
    }

    private async Task DrainAsync()
    {
        Func<T, ImmutableArray<Op>, DurableContext, Task> deliver;
        lock (gate)
        {
            if (running || end is not null || !started)
            {
                deliver = null!;
            }
            else
            {
                running = true;
                deliver = listener!;
            }
        }
        if (deliver is null)
        {
            FinishIfReady();
            return;
        }
        try
        {
            while (true)
            {
                WatchFrame frame;
                lock (gate)
                {
                    if (end is not null || pending.Count == 0) break;
                    frame = pending[0];
                    pending.RemoveAt(0);
                    value = frame.Value;
                }
                var deliveryContext = frame.Context.WithoutCancellation();
                try
                {
                    var task = deliver(frame.Value, frame.Ops, deliveryContext);
                    if (task is not null) await task.ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    Terminate(new WatchEnd(WatchEndReason.ListenerError, error));
                    break;
                }
                if (frame.Value is null)
                {
                    Terminate(WatchEnd.Retired);
                    break;
                }
            }
        }
        finally
        {
            bool again;
            lock (gate)
            {
                running = false;
                again = end is null && pending.Count > 0;
            }
            if (again) Schedule();
            FinishIfReady();
        }
    }

    private void Terminate(WatchEnd reason)
    {
        bool detachNow;
        lock (gate)
        {
            if (end is not null) return;
            end = reason;
            detachNow = !detached;
            detached = true;
            pending.Clear();
        }
        if (detachNow) detach();
        FinishIfReady();
    }

    private void FinishIfReady()
    {
        WatchEnd result;
        CancellationTokenRegistration registration;
        lock (gate)
        {
            if (resolved || end is null) return;
            resolved = true;
            result = end;
            registration = cancellation;
            cancellation = default;
        }
        registration.Unregister();
        closed.TrySetResult(result);
    }

    private readonly record struct WatchFrame(T Value, ImmutableArray<Op> Ops, DurableContext Context);
}
