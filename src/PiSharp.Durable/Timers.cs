// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/timers.ts, and SetTimer of packages/durable/src/types.ts.
// setTimeout becomes a TimeProvider timer: HarnessOptions.TimeProvider when set, otherwise the system's, so fake time drives the default
// timers as vi fake timers drive setTimeout. A .NET timer never keeps a process alive, so a background timer needs no unref and the default
// never declines one; the Cloudflare Workers branch has no counterpart. A callback runs on a thread-pool thread, where a throw would end
// the process: the Session's and scheduler's callbacks catch what they run, and the native timer adds nothing.
namespace PiSharp.Durable;

/// <summary>Options of one <see cref="SetTimer"/> call.</summary>
/// <param name="Background">A timer that only frees memory, which the host may decline.</param>
public readonly record struct TimerOptions(bool Background);

/// <summary>
/// A host timer, given as <see cref="HarnessOptions.SetTimer"/>: call <paramref name="callback"/> once, about <paramref name="delayMs"/>
/// from now, and return an action that disarms it (upstream <c>SetTimer</c>). Background timers only free memory, and a host may decline
/// one by returning null, for example where a pending timer keeps the process from hibernating; the Harness then frees that memory at
/// later commits. Other timers pace or resume work: one the host throws on or declines is armed on the default timer instead. A timer
/// should not fire early; callers check their clock again when it fires. The callback may run on any thread.
/// </summary>
public delegate Action? SetTimer(Action callback, double delayMs, TimerOptions options);

/// <summary>The default timers (upstream <c>nativeTimer</c>).</summary>
internal static class NativeTimer
{
    /// <summary>Longest delay one timer step takes (upstream <c>MAX_TIMER_DELAY</c>); a longer one is waited for in several steps.</summary>
    internal const double MaxTimerDelay = 2_147_483_647;

    /// <summary>The system's timers.</summary>
    public static SetTimer System { get; } = Of(TimeProvider.System);

    /// <summary>One-shot timers of <paramref name="time"/> that never fire early, also for delays beyond <see cref="MaxTimerDelay"/>; never
    /// declines one.</summary>
    public static SetTimer Of(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return (callback, delayMs, _) => new Armed(time, callback, delayMs > 0 ? delayMs : 0).Disarm;
    }

    /// <summary>One armed timer: the step pending now, replaced at each step until the last one runs the callback.</summary>
    private sealed class Armed
    {
        private readonly Lock gate = new();
        private readonly TimeProvider time;
        private readonly Action callback;
        private ITimer? step;
        private bool disarmed;

        public Armed(TimeProvider time, Action callback, double delayMs)
        {
            this.time = time;
            this.callback = callback;
            Arm(delayMs);
        }

        private void Arm(double remaining)
        {
            lock (gate)
            {
                if (disarmed) return;
                // A timer that fires synchronously runs its step inside CreateTimer, before the assignment; that step already armed the next.
                var next = time.CreateTimer(_ => Fire(remaining), null, TimeSpan.FromMilliseconds(Math.Min(remaining, MaxTimerDelay)),
                    Timeout.InfiniteTimeSpan);
                step ??= next;
            }
        }

        private void Fire(double remaining)
        {
            lock (gate)
            {
                if (disarmed) return;
                step?.Dispose();
                step = null;
            }
            if (remaining > MaxTimerDelay) Arm(remaining - MaxTimerDelay);
            else callback();
        }

        public void Disarm()
        {
            ITimer? pending;
            lock (gate)
            {
                disarmed = true;
                pending = step;
                step = null;
            }
            pending?.Dispose();
        }
    }
}

/// <summary>Timers the Session and Harness arm through the host's <see cref="SetTimer"/> (upstream <c>Timers</c> and
/// <c>hostTimers</c>).</summary>
internal sealed class HostTimers
{
    private readonly SetTimer arm;
    private readonly SetTimer native;
    private readonly Action<Exception> report;
    private int reported;

    /// <summary>
    /// Wraps a host <paramref name="setTimer"/>, by default <paramref name="native"/>. A foreground timer must fire, or work stalls: one the
    /// host throws on or declines is armed on <paramref name="native"/>, and the first such failure is reported. A background one the host
    /// throws on is reported and declined.
    /// </summary>
    public HostTimers(SetTimer? setTimer, SetTimer native, Action<Exception> report)
    {
        this.native = native;
        arm = setTimer ?? native;
        this.report = report;
    }

    /// <summary>A timer that paces or resumes work; when the host throws or declines it, the default timer arms it instead.</summary>
    public Action Foreground(Action callback, double delayMs)
    {
        try
        {
            if (arm(callback, delayMs, new TimerOptions(Background: false)) is { } disarm) return disarm;
            FallBack(new InvalidOperationException("HarnessOptions.setTimer declined a timer that paces work; using setTimeout"));
        }
        catch (Exception error)
        {
            FallBack(error);
        }
        return native(callback, delayMs, new TimerOptions(Background: false)) ?? (() => { });
    }

    /// <summary>A timer that only frees memory; null when the host declines it.</summary>
    public Action? Background(Action callback, double delayMs)
    {
        try
        {
            return arm(callback, delayMs, new TimerOptions(Background: true));
        }
        catch (Exception error)
        {
            report(error);
            return null;
        }
    }

    private void FallBack(Exception error)
    {
        if (Interlocked.Exchange(ref reported, 1) != 0) return;
        report(error);
    }
}
