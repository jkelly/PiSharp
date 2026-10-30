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
        ["/playground"] = new(ShellNav.Playground, "Playground", "bi-sliders2",
            "Send one request with every option exposed, and see the output, usage and raw stream."),
        ["/compare"] = new(ShellNav.Compare, "Compare", "bi-layout-three-columns",
            "Run the same request on several models side by side, or blind, and vote."),
        ["/bench"] = new(ShellNav.Bench, "Tool Bench", "bi-tools",
            "Test how models call your tools, one step at a time."),
        ["/history"] = new(ShellNav.History, "History", "bi-clock-history",
            "Every model call from Chat, Playground, Compare and Tool Bench, with tokens, cost, latency and the raw request and response."),
        ["/evals/leaderboard"] = new(ShellNav.Evals, "Evals", "bi-clipboard-check",
            "Your own leaderboard from blind votes in Compare, and test suites that catch silent changes in quality, speed and price."),
        ["/presets"] = new(ShellNav.Presets, "Presets", "bi-bookmark",
            "Saved bundles of model, parameters, prompts and tools, to load into the Playground, Compare or Tool Bench."),
    };

    /// <summary>The settings pages, by settings id.</summary>
    public static IReadOnlyDictionary<string, LaterFeature> SettingsPages { get; } = new Dictionary<string, LaterFeature>(StringComparer.OrdinalIgnoreCase)
    {
    };
}
