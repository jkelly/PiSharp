// Decision 0011 (run W5, E1): how a voted contestant's answer is measured for the leaderboard, from what was recorded (brief E1: "median
// cost, p50 time to first token and tokens/s per contestant come from the run records"). Request mode: the contestant's runs.db row (a
// completed call). Agent mode: the model's runs.db row (its run time and billing), the totals of its calls (rows whose parent is that row:
// cost and tokens), and agent compare's store for its check result, files changed and turns (the battle's group is the agent compare run,
// the contestant's run id is "<run>-<slot>"). A run that is gone (runs.db keeps unpinned rows 30 days) or not finished is not measured; the
// row then shows fewer answers, never a guess.
using PiSharp.WebAgent.AgentCompare.Runner;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Runs;

namespace PiSharp.WebAgent.Evals;

/// <summary>Measures voted contestants from runs.db (and agent compare's store). Resolves its stores on use; a missing store measures
/// nothing.</summary>
public sealed class ArenaMetrics
{
    private readonly Func<RunStore?> runs;
    private readonly Func<AgentCompareStore?> agents;

    /// <summary>Metrics over the app's stores.</summary>
    public ArenaMetrics(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        runs = () => services.GetService<RunStore>();
        agents = () => services.GetService<AgentCompareStore>();
    }

    /// <summary>Metrics over the given stores (tests, tools).</summary>
    public ArenaMetrics(RunStore? runs, AgentCompareStore? agents = null)
    {
        this.runs = () => runs;
        this.agents = () => agents;
    }

    /// <summary>How <paramref name="contestant"/> of <paramref name="battle"/> did, or null when its run is not recorded (or not finished).</summary>
    public ContestantMetrics? Of(ArenaBattle battle, ArenaContestant contestant)
    {
        ArgumentNullException.ThrowIfNull(battle);
        ArgumentNullException.ThrowIfNull(contestant);
        if (string.IsNullOrWhiteSpace(contestant.RunId) || runs() is not { } store) return null;
        var id = contestant.RunId.Trim();
        var run = store.Get(id);
        if (run is null) return null;
        if (battle.Mode == ArenaMode.Request)
            return run.Status == RunStatus.Completed ? ContestantMetrics.FromRun(run) : null;
        if (run.Status is RunStatus.Running) return null;
        var calls = store.Totals(new RunQuery { ParentId = id });
        AgentCompareModelState? state = null;
        int? turns = null;
        if (agents() is { } compare)
        {
            turns = compare.Turns(id);
            if (battle.GroupId is { Length: > 0 } group && id.StartsWith(group + "-", StringComparison.Ordinal))
                state = compare.Find(group)?.Model(id[(group.Length + 1)..]);
        }
        return ContestantMetrics.FromAgentRun(run, calls, state, turns);
    }
}

/// <summary>The app's leaderboards: the rater over stored votes, measured from runs.db, provisional per Settings' <c>evals</c> section.</summary>
public sealed class ArenaLeaderboards
{
    private readonly BradleyTerryRater rater;
    private readonly ArenaMetrics metrics;
    private readonly Func<EvalsSettings?> settings;

    /// <summary>Leaderboards over the app's services.</summary>
    public ArenaLeaderboards(BradleyTerryRater rater, ArenaMetrics metrics, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        this.rater = rater ?? throw new ArgumentNullException(nameof(rater));
        this.metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        settings = () => services.GetService<ISettingsStore>()?.Get<EvalsSettings>();
    }

    /// <summary>The tag menu: every leaderboard <paramref name="votes"/> make (<see cref="Leaderboards.Tags"/>).</summary>
    public IReadOnlyList<LeaderboardTag> Tags(IEnumerable<VotedBattle> votes) => Leaderboards.Tags(votes);

    /// <summary>The leaderboard of <paramref name="mode"/> and <paramref name="tag"/> from <paramref name="votes"/> (any mode or tag; others
    /// are left out), with each row's medians from the run records. <paramref name="options"/> defaults to the plan's, with Settings'
    /// provisional threshold.</summary>
    public Leaderboard Build(ArenaMode mode, string tag, IEnumerable<VotedBattle> votes, RaterOptions? options = null) =>
        Leaderboards.Build(mode, tag, votes, rater, metrics.Of, options ?? RaterOptions.From(settings()));
}
