using System.Collections.Immutable;

namespace PiSharp.Tools.Files;

public sealed record GrepExecutionRequest(string Pattern, string SearchPath, string? Glob, bool IgnoreCase, bool Literal, int Limit);
public sealed record GrepMatch(string Path, int LineNumber, string LineText);
/// <summary>Borrowed, explicitly admitted ripgrep capability. Every call joins original process, streams and cleanup;
/// returns only complete bounded zero-context JSON match events in original order. Host retains disposal ownership.</summary>
public interface IGrepExecutor
{
    ValueTask<ImmutableArray<GrepMatch>> GrepAsync(GrepExecutionRequest request, CancellationToken cancellationToken);
}