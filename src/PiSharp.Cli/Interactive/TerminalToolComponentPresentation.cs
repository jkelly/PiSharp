namespace PiSharp.Cli.Interactive;

internal sealed class TerminalToolComponentPresentation(TerminalCustomComponentPresentation source, Func<bool> isCurrent)
{
    internal TerminalCustomComponentPresentation Source { get; } = source;
    internal TerminalCustomComponentRows? Cached;
    internal int CachedColumns;
    internal bool IsCurrent => isCurrent();
}
