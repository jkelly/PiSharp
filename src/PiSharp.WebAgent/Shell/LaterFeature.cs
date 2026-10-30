// Decision 0011 (run W2, WU4): the pages the sidebar and settings nav link to that later milestones build (Playground, Compare, Tool Bench and
// History in W4, Evals in W5, Presets later; Settings' Data & storage). Each opens one neutral "Coming in a later milestone" page, so every
// link works and nothing pretends to exist. The summaries are the mockups' own page descriptions. W3 built Settings' Custom providers,
// Spend, Appearance and About, and removed their entries.
namespace PiSharp.WebAgent.Shell;

/// <summary>A page a later milestone builds.</summary>
/// <param name="Id">The sidebar or settings id (<see cref="ShellNav"/>).</param>
/// <param name="Title">The page's name.</param>
/// <param name="Icon">The Bootstrap Icons class.</param>
/// <param name="Summary">What the page will do.</param>
/// <param name="Settings">Whether it is a settings page (shown inside <c>_SettingsLayout</c>).</param>
public sealed record LaterFeature(string Id, string Title, string Icon, string Summary, bool Settings = false)
{
    /// <summary>The sidebar pages, by route.</summary>
    public static IReadOnlyDictionary<string, LaterFeature> Pages { get; } = new Dictionary<string, LaterFeature>(StringComparer.OrdinalIgnoreCase)
    {
        ["/evals/leaderboard"] = new(ShellNav.Evals, "Evals", "bi-clipboard-check",
            "Your own leaderboard from blind votes in Compare, and test suites that catch silent changes in quality, speed and price."),
    };

    /// <summary>The settings pages, by settings id.</summary>
    public static IReadOnlyDictionary<string, LaterFeature> SettingsPages { get; } = new Dictionary<string, LaterFeature>(StringComparer.OrdinalIgnoreCase)
    {
    };
}
