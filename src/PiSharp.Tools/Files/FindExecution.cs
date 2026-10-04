using System.Collections.Immutable;

namespace PiSharp.Tools.Files;

public sealed record FindExecutionRequest(string Pattern, string SearchPath, int Limit,
    ImmutableArray<string> Ignore, int MaximumResults, int MaximumTotalPathCharacters);

public enum FindExecutionProfile { CustomGlob, Fd }

/// <summary>
/// Explicit trusted executor for a declared Pi find profile; custom glob is the default, fd is opt-in.
/// No implicit fd acquisition is allowed.
/// SupportsPattern must reject syntax outside the executor's implemented domain without changing its meaning.
/// FindAsync honors the exact pattern, root, profile-specific ignore filters and limit; returns paths in original executor order.
/// It settles every original enumeration/process/stream/cleanup operation before returning or throwing, even on cancellation.
/// This borrowed executor is retained by its host, which owns any final disposal after invocation joins.
/// </summary>
public interface IFindExecutor
{
    FindExecutionProfile Profile => FindExecutionProfile.CustomGlob;
    bool SupportsPattern(string pattern);
    ValueTask<ImmutableArray<string>> FindAsync(FindExecutionRequest request, CancellationToken cancellationToken);
}
