// Decision 0011 (run W5, E1): the Bradley-Terry rater's knobs. The defaults are the approved plan's: a Gaussian prior on every model's strength
// (sd 1 in natural-log odds, about 174 rating points), so a handful of votes cannot produce an extreme rating; an optional position parameter
// (on by default, with a tighter prior, sd 0.5) that measures how much the user favours the left column; 500 seeded bootstrap samples for 95%
// intervals and rank ranges; and "provisional" below 10 comparisons (EvalsSettings.ProvisionalBelow). The seed defaults to one derived from
// the leaderboard's mode and tag (BradleyTerryRater.SeedOf), so a leaderboard is reproducible from the stored votes alone (brief rule 5).
namespace PiSharp.WebAgent.Evals;

/// <summary>How <see cref="BradleyTerryRater"/> fits and samples. Every value has the plan's default.</summary>
public sealed record RaterOptions
{
    /// <summary>The default bootstrap sample count: 500.</summary>
    public const int DefaultSamples = 500;

    /// <summary>The most bootstrap samples a fit takes.</summary>
    public const int MaxSamples = 10_000;

    /// <summary>The standard deviation of the Gaussian prior on each model's strength, in natural-log odds (default 1, about 174 rating
    /// points). Smaller keeps sparse ratings closer to 1000.</summary>
    public double PriorSigma { get; init; } = 1.0;

    /// <summary>Whether the fit has a position parameter (the user's left-position bias). Default on.</summary>
    public bool PositionParameter { get; init; } = true;

    /// <summary>The standard deviation of the Gaussian prior on the position parameter, in natural-log odds (default 0.5).</summary>
    public double PositionSigma { get; init; } = 0.5;

    /// <summary>Bootstrap samples (votes resampled with replacement), 0 to <see cref="MaxSamples"/>; default 500. Rank ranges also take as many
    /// draws from the posterior. With 0, intervals come from the posterior's curvature alone and rank ranges from 500 posterior draws.</summary>
    public int Samples { get; init; } = DefaultSamples;

    /// <summary>The bootstrap's seed (0 to <see cref="EvalRandom.MaxSeed"/>), or null for <see cref="BradleyTerryRater.SeedOf"/> of the
    /// leaderboard's mode and tag.</summary>
    public long? Seed { get; init; }

    /// <summary>The intervals' confidence, 0.5 to 0.999 (default 0.95).</summary>
    public double Confidence { get; init; } = 0.95;

    /// <summary>A model with fewer comparisons than this is provisional (1 to 1,000; default <see cref="EvalsSettings.DefaultProvisionalBelow"/>).</summary>
    public int ProvisionalBelow { get; init; } = EvalsSettings.DefaultProvisionalBelow;

    /// <summary>The defaults, with <paramref name="settings"/>' provisional threshold when given.</summary>
    public static RaterOptions From(EvalsSettings? settings) =>
        settings is null ? new RaterOptions() : new RaterOptions { ProvisionalBelow = settings.ProvisionalBelow };

    /// <summary>The same options, checked.</summary>
    /// <exception cref="ArgumentException">A value is out of range; the message says which.</exception>
    public RaterOptions Normalize()
    {
        if (!double.IsFinite(PriorSigma) || PriorSigma is <= 0 or > 10) throw new ArgumentException("The prior's standard deviation is above 0 and at most 10.");
        if (!double.IsFinite(PositionSigma) || PositionSigma is <= 0 or > 10) throw new ArgumentException("The position prior's standard deviation is above 0 and at most 10.");
        if (Samples is < 0 or > MaxSamples) throw new ArgumentException($"Take 0 to {MaxSamples:N0} bootstrap samples.");
        if (Seed is { } seed && (seed < 0 || seed > EvalRandom.MaxSeed)) throw new ArgumentException("A seed is 0 to 2^53 − 1.");
        if (!double.IsFinite(Confidence) || Confidence is < 0.5 or > 0.999) throw new ArgumentException("The confidence is 0.5 to 0.999.");
        if (ProvisionalBelow is < 1 or > 1000) throw new ArgumentException("Mark rows provisional below 1 to 1,000 comparisons.");
        return this;
    }
}
