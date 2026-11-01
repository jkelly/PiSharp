// Decision 0011 (run W5, E0): the evals defaults (section "evals" in webagent.db). The approved plan's "Defaults chosen": Arena answers are
// held until all finish, with a "stream anyway" toggle (StreamAnyway is that toggle's starting state); a leaderboard row is provisional
// with fewer than 10 comparisons; suites repeat each case 3 times. No page edits these yet (the mockups show no Settings › Evals); the units
// read them through ISettingsStore.Get<EvalsSettings>() so a later settings page changes behaviour without code changes.
using PiSharp.WebAgent.Contracts;

namespace PiSharp.WebAgent.Evals;

/// <summary>The evals defaults (section <c>evals</c>).</summary>
/// <param name="StreamAnyway">Whether a blind battle streams its answers as they come (default off: held until all finish).</param>
/// <param name="ProvisionalBelow">A leaderboard row with fewer comparisons than this is provisional (1 to 1,000; default 10).</param>
/// <param name="DefaultK">The repetitions a new suite starts with (1 to <see cref="Suite.MaxK"/>; default 3).</param>
public sealed record EvalsSettings(
    bool StreamAnyway = false,
    int ProvisionalBelow = EvalsSettings.DefaultProvisionalBelow,
    int DefaultK = Suite.DefaultK) : ISettingsSection<EvalsSettings>
{
    /// <summary>The default provisional threshold: 10 comparisons.</summary>
    public const int DefaultProvisionalBelow = 10;

    /// <inheritdoc/>
    public static string SectionName => "evals";

    /// <inheritdoc/>
    public static EvalsSettings CreateDefault() => new();

    /// <inheritdoc/>
    public EvalsSettings Normalize()
    {
        if (ProvisionalBelow is < 1 or > 1000) throw new ArgumentException("Mark rows provisional below 1 to 1,000 comparisons.");
        if (DefaultK is < 1 or > Suite.MaxK) throw new ArgumentException($"k is 1 to {Suite.MaxK}.");
        return this;
    }
}
