// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/telemetry/src/index.ts.
// telemetryNow clock with native TimeProvider integration.
using System.Runtime.CompilerServices;

namespace PiSharp.Telemetry;

/// <summary>The span clock: epoch milliseconds at microsecond resolution, anchored to the monotonic clock.
/// A wall-clock step exceeding ten milliseconds resets the anchor. All adapters share the clock for a provider.</summary>
public static class TelemetryClock
{
    private static readonly ConditionalWeakTable<TimeProvider, Clock> Clocks = new();

    public static double Now(TimeProvider? timeProvider = null) =>
        Clocks.GetValue(timeProvider ?? TimeProvider.System, provider => new Clock(provider)).Now();

    /// <summary>JavaScript Math.round semantics at microsecond resolution (ties toward positive infinity).</summary>
    public static double RoundMillisecondsToMicrosecond(double milliseconds) => Math.Floor(milliseconds * 1000 + 0.5) / 1000;

    private sealed class Clock
    {
        private readonly TimeProvider provider;
        private readonly object gate = new();
        private long stamp;
        private double anchor;
        internal Clock(TimeProvider provider)
        {
            this.provider = provider;
            stamp = provider.GetTimestamp();
            anchor = provider.GetUtcNow().ToUnixTimeMilliseconds();
        }
        internal double Now()
        {
            lock (gate)
            {
                var current = provider.GetTimestamp();
                var time = anchor + provider.GetElapsedTime(stamp, current).TotalMilliseconds;
                var wall = provider.GetUtcNow().ToUnixTimeMilliseconds();
                if (Math.Abs(time - wall) > 10) { stamp = current; anchor = wall; time = wall; }
                return RoundMillisecondsToMicrosecond(time);
            }
        }
    }
}
