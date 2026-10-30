// Decision 0011 (run W2, WU4): the pages the sidebar links to that later milestones build. Each opens one neutral "Coming in a later
// milestone" page, so every link works and nothing pretends to exist. The summaries are the mockups' own page descriptions. W3 built
// Settings' Custom providers, Spend, Appearance and About, and W4 Playground, Compare, Tool Bench, History, Presets and Settings' Data &
// storage, each removing its entry; W4's T4 removed the settings placeholder (its route, list and view) once no settings page used it. Evals
// (W5) is the one left.
namespace PiSharp.WebAgent.Shell;

/// <summary>A page a later milestone builds.</summary>
/// <param name="Id">The sidebar id (<see cref="ShellNav"/>).</param>
/// <param name="Title">The page's name.</param>
/// <param name="Icon">The Bootstrap Icons class.</param>
/// <param name="Summary">What the page will do.</param>
public sealed record LaterFeature(string Id, string Title, string Icon, string Summary)
{
    /// <summary>The sidebar pages, by route.</summary>
    public static IReadOnlyDictionary<string, LaterFeature> Pages { get; } = new Dictionary<string, LaterFeature>(StringComparer.OrdinalIgnoreCase)
    {
        ["/evals/leaderboard"] = new(ShellNav.Evals, "Evals", "bi-clipboard-check",
            "Your own leaderboard from blind votes in Compare, and test suites that catch silent changes in quality, speed and price."),
    };
}
