// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/countdown-timer.ts.
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>
/// Reusable countdown timer for dialog components.
/// Upstream uses the global <c>setInterval</c>; here the interval runs on the TUI's <see cref="UiLoop"/> (or, without a TUI, on a
/// thread-pool timer posting to the creating thread's synchronization context). <c>setInterval</c> overrides the scheduler (tests).
/// </summary>
internal sealed class CountdownTimer : IDisposable
{
    private IDisposable? intervalId;
    private int remainingSeconds;
    private readonly ITui? tui;
    private readonly Action<int> onTick;
    private readonly Action onExpire;

    public CountdownTimer(double timeoutMs, ITui? tui, Action<int> onTick, Action onExpire, Func<Action, double, IDisposable>? setInterval = null)
    {
        this.tui = tui;
        this.onTick = onTick;
        this.onExpire = onExpire;
        remainingSeconds = (int)Math.Ceiling(timeoutMs / 1000);
        this.onTick(remainingSeconds);

        setInterval ??= tui is not null ? tui.Loop.SetInterval : DefaultSetInterval;
        intervalId = setInterval(() =>
        {
            remainingSeconds--;
            this.onTick(remainingSeconds);
            this.tui?.RequestRender();

            if (remainingSeconds <= 0)
            {
                Dispose();
                this.onExpire();
            }
        }, 1000);
    }

    public void Dispose()
    {
        if (intervalId is not null)
        {
            intervalId.Dispose();
            intervalId = null;
        }
    }

    private static IDisposable DefaultSetInterval(Action action, double milliseconds)
    {
        var context = SynchronizationContext.Current;
        var cancelled = 0;
        var period = TimeSpan.FromMilliseconds(milliseconds);
        var timer = new Timer(_ =>
        {
            if (context is null) { if (Volatile.Read(ref cancelled) == 0) action(); }
            else context.Post(_ => { if (Volatile.Read(ref cancelled) == 0) action(); }, null);
        }, null, period, period);
        return new Handle(() => { Volatile.Write(ref cancelled, 1); timer.Dispose(); });
    }

    private sealed class Handle(Action dispose) : IDisposable { public void Dispose() => dispose(); }
}
