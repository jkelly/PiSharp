// Decision 0011 (run W5, E1): from stored votes to the leaderboard the page shows (mockup evals-leaderboard: _LeaderboardToolbar's tag menu
// with request tags and agent tags apart and "32 votes · 4 models", the provisional chip, _LeaderboardTable's rows, the left-position line).
// A battle counts when it is voted (never held, ready or void: nothing of an unvoted battle may reach a page, brief rule 3) and its vote fits
// it; a stored row that does not is skipped and counted, never fatal. One leaderboard is one mode and one tag. Leaderboards is pure (the
// caller passes how to measure a contestant); ArenaLeaderboards is the app's service over it, measuring from runs.db. E5 stores the battles
// and votes (eval_battles, eval_votes) and serves /api/evals/leaderboard; E6 renders the page.
using System.Globalization;

namespace PiSharp.WebAgent.Evals;

/// <summary>A battle with its vote, as the store reads them.</summary>
/// <param name="Battle">The battle.</param>
/// <param name="Vote">Its one vote.</param>
public sealed record VotedBattle(ArenaBattle Battle, ArenaVote Vote);

/// <summary>One leaderboard of the tag menu: a mode and a tag, with how many votes and models it has.</summary>
/// <param name="Mode">Request or agent: the menu's two groups.</param>
/// <param name="Tag">The tag.</param>
/// <param name="Votes">The votes counted.</param>
/// <param name="Models">The models rated.</param>
/// <param name="LastVote">When the latest vote was cast.</param>
public sealed record LeaderboardTag(ArenaMode Mode, string Tag, int Votes, int Models, DateTimeOffset LastVote)
{
    /// <summary>"32 votes · 4 models", as the toolbar shows it.</summary>
    public string Summary => string.Create(CultureInfo.InvariantCulture,
        $"{Votes:N0} {(Votes == 1 ? "vote" : "votes")} · {Models:N0} {(Models == 1 ? "model" : "models")}");
}

/// <summary>One row of a leaderboard: the model's rating and its cost and speed.</summary>
/// <param name="Model">The rating, interval, rank range, record and provisional flag.</param>
/// <param name="Metrics">The medians of the answers voted on.</param>
public sealed record LeaderboardRow(RatedModel Model, ModelMetrics Metrics);

/// <summary>The scale a page draws the rows' intervals on (_LeaderboardTable's <c>ci-track</c>): 750 to 1250 rating points, widened in
/// steps of 50 to hold every interval.</summary>
/// <param name="Min">The track's left end (rating points).</param>
/// <param name="Max">The track's right end.</param>
public sealed record LeaderboardTrack(double Min, double Max)
{
    /// <summary>The track with no rows: 750 to 1250.</summary>
    public static LeaderboardTrack Default { get; } = new(750, 1250);

    /// <summary>The track that holds every interval of <paramref name="models"/>.</summary>
    public static LeaderboardTrack Of(IEnumerable<RatedModel> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        double min = Default.Min, max = Default.Max;
        foreach (var model in models)
        {
            if (double.IsFinite(model.Low)) min = Math.Min(min, Math.Floor(model.Low / 50) * 50);
            if (double.IsFinite(model.High)) max = Math.Max(max, Math.Ceiling(model.High / 50) * 50);
        }
        return new LeaderboardTrack(min, max);
    }

    /// <summary>Where <paramref name="rating"/> falls on the track, 0 to 100 (percent from the left).</summary>
    public double Percent(double rating) => Math.Clamp((rating - Min) / (Max - Min) * 100, 0, 100);
}

/// <summary>One leaderboard (one mode, one tag): the ratings, each row's metrics and the left-position bias.</summary>
public sealed record Leaderboard
{
    /// <summary>Request or agent.</summary>
    public required ArenaMode Mode { get; init; }

    /// <summary>The tag.</summary>
    public required string Tag { get; init; }

    /// <summary>The fit: ratings, intervals, rank ranges, the bias, the seed.</summary>
    public required Ratings Ratings { get; init; }

    /// <summary>The rows, best first.</summary>
    public IReadOnlyList<LeaderboardRow> Rows { get; init; } = [];

    /// <summary>Stored votes of this mode and tag that were skipped: their battle is not voted, or the vote does not fit it.</summary>
    public int Skipped { get; init; }

    /// <summary>The votes counted.</summary>
    public int Votes => Ratings.Votes;

    /// <summary>Whether nothing is rated yet (the page shows _LeaderboardEmpty).</summary>
    public bool Empty => Rows.Count == 0;

    /// <summary>Whether some row is provisional (the toolbar's chip).</summary>
    public bool Provisional => Ratings.Provisional;

    /// <summary>The left-position bias.</summary>
    public PositionBias Position => Ratings.Position;

    /// <summary>The track to draw the intervals on.</summary>
    public LeaderboardTrack Track => LeaderboardTrack.Of(Ratings.Models);

    /// <summary>"32 votes · 4 models", as the toolbar shows it.</summary>
    public string Summary => new LeaderboardTag(Mode, Tag, Votes, Rows.Count, default).Summary;
}

/// <summary>Builds leaderboards from stored battles and votes.</summary>
public static class Leaderboards
{
    /// <summary>Every leaderboard <paramref name="votes"/> make, request tags first, then agent tags, each by tag name.</summary>
    public static IReadOnlyList<LeaderboardTag> Tags(IEnumerable<VotedBattle> votes)
    {
        ArgumentNullException.ThrowIfNull(votes);
        var tags = new Dictionary<(ArenaMode, string), (HashSet<string> Battles, HashSet<string> Models, DateTimeOffset Last)>();
        foreach (var (battle, vote, pairs) in Counted(votes, mode: null, tag: null, out _))
        {
            var key = (battle.Mode, pairs[0].Tag);
            if (!tags.TryGetValue(key, out var entry)) tags[key] = entry = (new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), vote.Created);
            entry.Battles.Add(battle.Id);
            foreach (var contestant in battle.Contestants) entry.Models.Add(contestant.Identity.Key);
            if (vote.Created > entry.Last) tags[key] = entry with { Last = vote.Created };
        }
        return [.. tags.OrderBy(entry => entry.Key.Item1).ThenBy(entry => entry.Key.Item2, StringComparer.Ordinal)
            .Select(entry => new LeaderboardTag(entry.Key.Item1, entry.Key.Item2, entry.Value.Battles.Count, entry.Value.Models.Count, entry.Value.Last))];
    }

    /// <summary>The comparisons of every counted vote of <paramref name="mode"/> and <paramref name="tag"/>, battles in id order.</summary>
    /// <param name="mode">The leaderboard's mode.</param>
    /// <param name="tag">The leaderboard's tag.</param>
    /// <param name="votes">Stored battles with their votes, any mode or tag, in any order.</param>
    /// <param name="skipped">Votes of this mode and tag that do not count (battle not voted, or the vote does not fit it).</param>
    public static IReadOnlyList<ArenaComparison> ComparisonsOf(ArenaMode mode, string tag, IEnumerable<VotedBattle> votes, out int skipped)
    {
        ArgumentNullException.ThrowIfNull(votes);
        return [.. Counted(votes, mode, EvalTags.Normalize(tag), out skipped).SelectMany(entry => entry.Pairs)];
    }

    /// <summary>The leaderboard of <paramref name="mode"/> and <paramref name="tag"/>.</summary>
    /// <param name="mode">Request or agent.</param>
    /// <param name="tag">The tag.</param>
    /// <param name="votes">Stored battles with their votes, any mode or tag, in any order (others are left out).</param>
    /// <param name="rater">The rater.</param>
    /// <param name="measure">How one contestant's answer did (cost, speed), or null to leave the metrics out; called once per contestant of
    /// each counted battle, battles in id order.</param>
    /// <param name="options">How to fit (the plan's defaults when null).</param>
    public static Leaderboard Build(ArenaMode mode, string tag, IEnumerable<VotedBattle> votes, BradleyTerryRater rater,
        Func<ArenaBattle, ArenaContestant, ContestantMetrics?>? measure, RaterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(votes);
        ArgumentNullException.ThrowIfNull(rater);
        var board = EvalTags.Normalize(tag);
        var counted = Counted(votes, mode, board, out var skipped);
        var ratings = rater.Rate(mode, board, counted.SelectMany(entry => entry.Pairs), options);
        var measured = new Dictionary<string, List<(DateTimeOffset At, string Battle, ContestantMetrics Metrics)>>(StringComparer.Ordinal);
        if (measure is not null)
        {
            foreach (var (battle, vote, _) in counted)
            {
                foreach (var contestant in battle.Contestants)
                {
                    if (measure(battle, contestant) is not { } metrics) continue;
                    if (!measured.TryGetValue(contestant.Identity.Key, out var list)) measured[contestant.Identity.Key] = list = [];
                    list.Add((vote.Created, battle.Id, metrics));
                }
            }
        }
        var rows = ratings.Models.Select(model => new LeaderboardRow(model, measured.TryGetValue(model.Identity.Key, out var list)
            ? ModelMetrics.Of(list.OrderBy(entry => entry.At).ThenBy(entry => entry.Battle, StringComparer.Ordinal).Select(entry => entry.Metrics))
            : ModelMetrics.None)).ToList();
        return new Leaderboard { Mode = mode, Tag = board, Ratings = ratings, Rows = rows, Skipped = skipped };
    }

    // The counted votes: normalized battles that are voted, whose vote fits them, of the mode and tag when given; in battle-id order. A battle
    // id that comes twice with different contents counts neither (a store keeps one vote per battle, so it is a caller's mistake).
    private static List<(ArenaBattle Battle, ArenaVote Vote, IReadOnlyList<ArenaComparison> Pairs)> Counted(IEnumerable<VotedBattle> votes,
        ArenaMode? mode, string? tag, out int skipped)
    {
        skipped = 0;
        var byId = new Dictionary<string, List<(ArenaBattle Battle, ArenaVote Vote, IReadOnlyList<ArenaComparison> Pairs, string Json)>>(StringComparer.Ordinal);
        foreach (var entry in votes)
        {
            if (entry?.Battle is null || entry.Vote is null) continue;
            if (mode is { } wanted && entry.Vote.Mode != wanted && entry.Battle.Mode != wanted) continue;
            string voteTag;
            try { voteTag = EvalTags.Normalize(entry.Vote.Tag); }
            catch (ArgumentException) { skipped += tag is null ? 1 : 0; continue; }
            if (tag is not null && !string.Equals(voteTag, tag, StringComparison.Ordinal)) continue;
            try
            {
                var battle = entry.Battle.Normalize();
                if (battle.Status != ArenaStatus.Voted) { skipped++; continue; }
                var vote = entry.Vote.Normalize(battle.Contestants.Count);
                var pairs = battle.Comparisons(vote);
                if (pairs.Count == 0) { skipped++; continue; }
                if (!byId.TryGetValue(battle.Id, out var list)) byId[battle.Id] = list = [];
                list.Add((battle, vote, pairs, EvalJson.Serialize(battle) + EvalJson.Serialize(vote)));
            }
            catch (ArgumentException)
            {
                skipped++;
            }
        }
        var counted = new List<(ArenaBattle, ArenaVote, IReadOnlyList<ArenaComparison>)>(byId.Count);
        foreach (var id in byId.Keys.Order(StringComparer.Ordinal))
        {
            var list = byId[id];
            if (list.Select(entry => entry.Json).Distinct(StringComparer.Ordinal).Count() > 1)
            {
                skipped += list.Count;
                continue;
            }
            counted.Add((list[0].Battle, list[0].Vote, list[0].Pairs));
        }
        return counted;
    }
}
