namespace PiSharp.Cli.Interactive;

// Used only by the physical input producer. A held paint must not prevent the
// second distinct press from requesting shutdown. Repeat/release are filtered
// before this clock is advanced; a replacement starts a new gesture lifetime.
internal sealed class TerminalInterruptGesture(TimeProvider clock)
{
    private long? previous;
    private long generation;
    internal bool Press(long currentGeneration)
    {
        var now = clock.GetTimestamp();
        var shutdown = previous is { } before && generation == currentGeneration &&
            clock.GetElapsedTime(before, now) is var elapsed && elapsed >= TimeSpan.Zero &&
            elapsed < TimeSpan.FromMilliseconds(500);
        previous = now; generation = currentGeneration;
        return shutdown;
    }
}
