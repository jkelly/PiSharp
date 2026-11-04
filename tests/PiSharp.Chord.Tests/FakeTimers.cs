// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/support.ts (fakeTimers, flushed)
//
// Upstream's fakeTimers() fakes setTimeout, clearTimeout and Date for each test (vi.useFakeTimers({ now: 0 })), and its tests drive them
// with vi.advanceTimersByTime and count them with vi.getTimerCount. The C# code under test takes a TimeProvider (Documents/Residency.cs),
// so the fake is one: FakeTimers starts at the Unix epoch (Date.now() 0), and AdvanceTimersByTime fires every timer due within the step in
// due order (creation order among equals), the ones a callback arms included, as vitest's does; a timer armed with a zero delay fires at the
// next advance, never inside CreateTimer. flushed() (setImmediate: every pending promise callback ran) has no exact counterpart, since
// continuations run on the thread pool: Flushed gives them a short real delay, enough for a negative check ("nothing ran yet").
internal sealed class FakeTimers : TimeProvider
{
    private readonly Lock gate = new();
    private readonly List<FakeTimer> pending = [];
    private long now;
    private long created;

    /// <summary>The timers armed and not fired, changed off or disposed (upstream <c>vi.getTimerCount()</c>).</summary>
    public int TimerCount
    {
        get
        {
            lock (gate) return pending.Count;
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate) return DateTimeOffset.UnixEpoch.AddMilliseconds(now);
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new FakeTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock <paramref name="ms"/> forward, firing each timer as the clock reaches it (upstream
    /// <c>vi.advanceTimersByTime</c>).</summary>
    public void AdvanceTimersByTime(double ms)
    {
        long target;
        lock (gate) target = now + (long)ms;
        while (true)
        {
            FakeTimer? next;
            lock (gate)
            {
                next = pending.Where(timer => timer.Due <= target).OrderBy(timer => timer.Due).ThenBy(timer => timer.Order).FirstOrDefault();
                if (next is null)
                {
                    now = target;
                    return;
                }
                now = Math.Max(now, next.Due);
                // A one-shot timer leaves before its callback runs; a periodic one comes back a period later.
                if (next.Period is { } period) next.Due += period;
                else pending.Remove(next);
            }
            next.Fire();
        }
    }

    /// <summary>Upstream's <c>flushed()</c>: lets the continuations queued so far run (a short real delay).</summary>
    public static Task Flushed() => Task.Delay(50);

    private sealed class FakeTimer(FakeTimers owner, TimerCallback callback, object? state) : ITimer
    {
        public long Due { get; set; }

        public long Order { get; private set; }

        public long? Period { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                owner.pending.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan) return true;
                Due = owner.now + (long)Math.Max(0, dueTime.TotalMilliseconds);
                Period = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : (long)period.TotalMilliseconds;
                Order = owner.created++;
                owner.pending.Add(this);
                return true;
            }
        }

        public void Dispose()
        {
            lock (owner.gate) owner.pending.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
