// Decision 0011 (run W5, E1): what BradleyTerryRater answers for one leaderboard (one mode and one tag): every model's rating with its 95%
// interval and rank range, wins, losses and ties, how many votes and comparisons it has and whether it is provisional; and the user's
// left-position bias (mockup evals-leaderboard: _LeaderboardTable, "You picked the left answer 54% of the time"). Ratings are on the Elo
// scale centred on 1000 (the average model of the tag): a model 100 points higher wins about 64% of the time. Every number is finite or
// null, so the records serialize with EvalJson as they are.
using System.Globalization;
using System.Text.Json.Serialization;

namespace PiSharp.WebAgent.Evals;

/// <summary>One model's place on a leaderboard.</summary>
public sealed record RatedModel
{
    /// <summary>The model and its settings (never the account).</summary>
    public required ModelIdentity Identity { get; init; }

    /// <summary>The fitted strength in natural-log odds (0 is the tag's average; the MAP estimate).</summary>
    public double Strength { get; init; }

    /// <summary>The rating: 1000 + strength × 400 / ln 10.</summary>
    public double Rating { get; init; }

    /// <summary>The interval's lower end (rating points): the bootstrap's percentile, never narrower than the posterior's own interval.</summary>
    public double Low { get; init; }

    /// <summary>The interval's upper end (rating points).</summary>
    public double High { get; init; }

    /// <summary>The rank by rating (1 is the best; models whose ratings are equal share a rank).</summary>
    public int Rank { get; init; }

    /// <summary>The best likely rank: the 2.5th percentile of its ranks across the bootstrap samples and across as many posterior draws.</summary>
    public int RankLow { get; init; }

    /// <summary>The worst likely rank (the 97.5th percentile, likewise).</summary>
    public int RankHigh { get; init; }

    /// <summary>Pairs won (a pick of one of three is two wins).</summary>
    public int Wins { get; init; }

    /// <summary>Pairs lost.</summary>
    public int Losses { get; init; }

    /// <summary>Pairs tied (a tie and all bad).</summary>
    public int Ties { get; init; }

    /// <summary>The votes (battles) it was in.</summary>
    public int Votes { get; init; }

    /// <summary>Its comparisons: <see cref="Wins"/> + <see cref="Losses"/> + <see cref="Ties"/>.</summary>
    [JsonIgnore]
    public int Comparisons => Wins + Losses + Ties;

    /// <summary>Whether it has fewer comparisons than the provisional threshold (its rating will still move).</summary>
    public bool Provisional { get; init; }

    /// <summary>Half the interval's width, in rating points (the page's "± 104").</summary>
    [JsonIgnore]
    public double HalfWidth => (High - Low) / 2;

    /// <summary>The rank range as the page shows it: <c>1–2</c>, or <c>1</c> when it is one rank.</summary>
    [JsonIgnore]
    public string RankRange => RankLow == RankHigh
        ? RankLow.ToString(CultureInfo.InvariantCulture)
        : RankLow.ToString(CultureInfo.InvariantCulture) + "–" + RankHigh.ToString(CultureInfo.InvariantCulture);

    /// <summary>Wins, losses and ties as the page shows them: <c>11–4–2</c>.</summary>
    [JsonIgnore]
    public string Record => string.Create(CultureInfo.InvariantCulture, $"{Wins}–{Losses}–{Ties}");
}

/// <summary>The user's left-position bias: how much the left column of a pair wins beyond what the two models' strengths explain.</summary>
public sealed record PositionBias
{
    /// <summary>Whether the fit had a position parameter (<see cref="RaterOptions.PositionParameter"/>).</summary>
    public bool Fitted { get; init; }

    /// <summary>The position parameter in natural-log odds (positive: the left column is favoured), or null when not fitted or nothing was
    /// voted.</summary>
    public double? Estimate { get; init; }

    /// <summary>The parameter's interval, lower end, or null.</summary>
    public double? Low { get; init; }

    /// <summary>The parameter's interval, upper end, or null.</summary>
    public double? High { get; init; }

    /// <summary>How often the left of two equally good answers would be picked (0 to 1; 0.5 is no bias), or null.</summary>
    [JsonIgnore]
    public double? LeftWinRate => Estimate is { } gamma ? BradleyTerryRater.Logistic(gamma) : null;

    /// <summary>Decided pairs (not ties).</summary>
    public int Pairs { get; init; }

    /// <summary>Decided pairs the left column won.</summary>
    public int LeftWins { get; init; }

    /// <summary>The share of decided pairs the left column won (in a vote between two answers: how often the left answer was picked), or
    /// null when no pair was decided. The page's "You picked the left answer 54% of the time".</summary>
    [JsonIgnore]
    public double? LeftShare => Pairs == 0 ? null : (double)LeftWins / Pairs;

    /// <summary>Whether the bias is clear: the parameter's interval leaves out 0.</summary>
    [JsonIgnore]
    public bool Detected => Low is { } low && High is { } high && (low > 0 || high < 0);
}

/// <summary>A fitted leaderboard: one mode, one tag.</summary>
public sealed record Ratings
{
    /// <summary>Request or agent.</summary>
    public required ArenaMode Mode { get; init; }

    /// <summary>The tag (normalized).</summary>
    public required string Tag { get; init; }

    /// <summary>The bootstrap's seed.</summary>
    public long Seed { get; init; }

    /// <summary>Bootstrap samples taken (0 when there were no votes or none were asked for).</summary>
    public int Samples { get; init; }

    /// <summary>The intervals' confidence (0.95).</summary>
    public double Confidence { get; init; }

    /// <summary>The provisional threshold used.</summary>
    public int ProvisionalBelow { get; init; }

    /// <summary>The votes (battles) counted.</summary>
    public int Votes { get; init; }

    /// <summary>The pairs counted.</summary>
    public int Comparisons { get; init; }

    /// <summary>The models, best first (ties by identity key).</summary>
    public IReadOnlyList<RatedModel> Models { get; init; } = [];

    /// <summary>The left-position bias.</summary>
    public PositionBias Position { get; init; } = new();

    /// <summary>Whether some model is provisional (the toolbar's chip).</summary>
    [JsonIgnore]
    public bool Provisional => Models.Any(model => model.Provisional);

    /// <summary>The row of <paramref name="identity"/>, or null.</summary>
    public RatedModel? Of(ModelIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var key = identity.Normalize().Key;
        return Models.FirstOrDefault(model => string.Equals(model.Identity.Key, key, StringComparison.Ordinal));
    }
}
