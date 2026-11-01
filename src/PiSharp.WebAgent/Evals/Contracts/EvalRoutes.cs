// Decision 0011 (run W5, E0): the Evals URLs, so units that run in parallel link to each other's pages and APIs without guessing. Pages are
// the brief's (E6 maps them); each API prefix belongs to one unit, which maps everything under it (as other areas do: JSON POSTs,
// idempotent, a POST .../delete rather than DELETE): suites, cases and runs to E4 (store, scheduler, runner), arena and leaderboard to E5,
// pins to E7. A unit adds paths only under its own prefix.
namespace PiSharp.WebAgent.Evals;

/// <summary>The Evals pages and API prefixes.</summary>
public static class EvalRoutes
{
    /// <summary>The sidebar's Evals item: the leaderboard.</summary>
    public const string Home = "/evals";

    /// <summary>The leaderboard page (<c>evals-leaderboard</c>); <c>?tag=coding</c> picks a tag.</summary>
    public const string Leaderboard = "/evals/leaderboard";

    /// <summary>The suites page (<c>evals-suites</c>).</summary>
    public const string Suites = "/evals/suites";

    /// <summary>The drift page (<c>evals-drift</c>).</summary>
    public const string Drift = "/evals/drift";

    /// <summary>A suite's page (<c>evals-suite</c>).</summary>
    public static string Suite(string suiteId) => $"/evals/suites/{Uri.EscapeDataString(suiteId)}";

    /// <summary>A suite run's page (<c>evals-run</c>).</summary>
    public static string Run(string runId) => $"/evals/runs/{Uri.EscapeDataString(runId)}";

    /// <summary>The leaderboard of one tag (the page's default mode, request).</summary>
    public static string LeaderboardOf(string tag) => $"{Leaderboard}?tag={Uri.EscapeDataString(tag)}";

    /// <summary>The leaderboard of one tag in one mode: request and agent votes are separate leaderboards, even under the same tag.</summary>
    public static string LeaderboardOf(ArenaMode mode, string tag) => $"{Leaderboard}?mode={EvalJson.Wire(mode)}&tag={Uri.EscapeDataString(tag)}";

    /// <summary>Suites: list, create, change, delete, schedule, run now, accept a baseline (E4).</summary>
    public const string ApiSuites = "/api/evals/suites";

    /// <summary>Test cases: read, change, delete, move (E4).</summary>
    public const string ApiCases = "/api/evals/cases";

    /// <summary>Suite runs: read, cancel, results download (E4).</summary>
    public const string ApiRuns = "/api/evals/runs";

    /// <summary>Drift signals and price changes (E4, from E3's analysis).</summary>
    public const string ApiDrift = "/api/evals/drift";

    /// <summary>Blind battles: start, read (blind until voted), vote, reveal (E5).</summary>
    public const string ApiArena = "/api/evals/arena";

    /// <summary>Leaderboard data per tag (E5).</summary>
    public const string ApiLeaderboard = "/api/evals/leaderboard";

    /// <summary>Pin as test case, from any page (E7).</summary>
    public const string ApiPins = "/api/evals/pins";
}
