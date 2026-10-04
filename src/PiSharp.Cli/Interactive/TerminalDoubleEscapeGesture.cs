namespace PiSharp.Cli.Interactive;

internal sealed class TerminalDoubleEscapeGesture(TimeProvider clock)
{
    private long? previous;
    private long generation;
    internal bool Press(long currentGeneration)
    {
        var now = clock.GetTimestamp();
        var open = previous is { } before && generation == currentGeneration &&
            clock.GetElapsedTime(before, now) is var elapsed && elapsed >= TimeSpan.Zero && elapsed < TimeSpan.FromMilliseconds(500);
        previous = open ? null : now; generation = currentGeneration;
        return open;
    }
}
