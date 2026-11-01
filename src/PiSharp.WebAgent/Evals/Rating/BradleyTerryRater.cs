// Decision 0011 (run W5, E1): the Arena's rater (approved plan, Flagship C, "How it works"). One leaderboard is one mode and one tag: request
// and agent votes never meet, and Rate refuses a mix. The model is Bradley-Terry with ties counted as half a win: the left column of a pair
// wins with probability logistic(strength(left) − strength(right) + position), where the optional position parameter measures the user's
// left-position bias (columns are shuffled for every battle, so it is identifiable). The fit is the MAP estimate under a Gaussian prior on
// every strength (and on the position parameter), so sparse votes stay finite and the strengths centre on 0 by themselves; Newton's method
// with a backtracking line search finds it (the log posterior is strictly concave). Votes become pairs through ArenaBattle.Comparisons: a
// pick's pairs weigh 1 each (picking one of three is two wins), while a ranking's, a tie's and all bad's pairs weigh 2/n each, so every vote
// of n answers carries the same total weight, n − 1, whatever its kind ("rankings split into weighted pairs"). Intervals and rank ranges come
// from 500 bootstrap samples that resample whole votes with replacement, drawn from EvalRandom with a stored or derived seed; because the
// percentile bootstrap collapses with a handful of votes (one vote resampled is always that vote), each interval is never narrower than the
// posterior's own (MAP ± z·sd from the curvature, for the strength relative to the tag's average), and each rank range also covers the ranks
// of as many seeded draws from that posterior; with many votes the two agree and the bootstrap decides. Results never depend on vote
// order: votes are processed in battle-id order, models in identity-key order, and pairs in column order, so the same votes give the same
// bits. Ratings are on the Elo scale centred on 1000: 1000 + strength × 400 / ln 10.
using System.Security.Cryptography;
using System.Text;

namespace PiSharp.WebAgent.Evals;

/// <summary>The Bradley-Terry rater: MAP strengths with a Gaussian prior, ties as half a win, weighted ranking pairs, an optional position
/// parameter and seeded bootstrap intervals. Stateless and thread-safe.</summary>
public sealed class BradleyTerryRater
{
    /// <summary>The rating of the tag's average model.</summary>
    public const double Center = 1000;

    /// <summary>Rating points per natural-log odds unit: 400 / ln 10 (about 173.7), so 100 points is a 64% win rate.</summary>
    public static double Scale { get; } = 400 / Math.Log(10);

    // Two strengths this close are the same rank. Far below a rating point (Scale × 1e-6 ≈ 0.0002), far above the fit's convergence noise.
    private const double RankTolerance = 1e-6;
    private const int MaxIterations = 200;

    /// <summary>The rating of strength <paramref name="strength"/> (natural-log odds).</summary>
    public static double RatingOf(double strength) => Center + Scale * strength;

    /// <summary>The strength of rating <paramref name="rating"/>.</summary>
    public static double StrengthOf(double rating) => (rating - Center) / Scale;

    /// <summary>The probability that a model rated <paramref name="rating"/> beats one rated <paramref name="other"/> (no position bias).</summary>
    public static double WinProbability(double rating, double other) => Logistic((rating - other) / Scale);

    /// <summary>The logistic function, 1 / (1 + e^−x), computed without overflow.</summary>
    public static double Logistic(double x)
    {
        if (x >= 0) return 1 / (1 + Math.Exp(-x));
        var e = Math.Exp(x);
        return e / (1 + e);
    }

    /// <summary>How much each pair of a vote weighs: 1 for a pick (and for any vote of two answers); 2/n for a ranking, a tie or all bad of n
    /// answers, so every vote carries a total weight of n − 1.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="contestants"/> is not 2 to 4.</exception>
    public static double PairWeight(VoteKind kind, int contestants)
    {
        if (contestants is < ArenaLetters.MinContestants or > ArenaLetters.MaxContestants)
            throw new ArgumentOutOfRangeException(nameof(contestants), contestants, "A battle has 2 to 4 contestants.");
        return kind == VoteKind.Pick || contestants == 2 ? 1.0 : 2.0 / contestants;
    }

    /// <summary>The bootstrap seed of a leaderboard: the first 53 bits of SHA-256 over its mode and tag, so the same votes always give the
    /// same intervals without storing anything else.</summary>
    public static long SeedOf(ArenaMode mode, string tag)
    {
        var text = "pisharp.evals.rater/" + EvalJson.Wire(mode) + "/" + EvalTags.Normalize(tag);
        return BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0) & EvalRandom.MaxSeed;
    }

    /// <summary>Rates the models of one leaderboard from its <paramref name="comparisons"/> (<see cref="ArenaBattle.Comparisons"/> of each
    /// counted vote).</summary>
    /// <param name="mode">The leaderboard's mode; every comparison must have it.</param>
    /// <param name="tag">The leaderboard's tag; every comparison must have it.</param>
    /// <param name="comparisons">The pairs of every counted vote, in any order.</param>
    /// <param name="options">How to fit and sample (the plan's defaults when null).</param>
    /// <exception cref="ArgumentException">A comparison is for another mode or tag, or the pairs of a battle do not fit together.</exception>
    public Ratings Rate(ArenaMode mode, string tag, IEnumerable<ArenaComparison> comparisons, RaterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(comparisons);
        if (!Enum.IsDefined(mode)) throw new ArgumentException($"Unknown arena mode {(int)mode}.");
        var board = EvalTags.Normalize(tag);
        var settings = (options ?? new RaterOptions()).Normalize();
        var seed = settings.Seed ?? SeedOf(mode, board);
        var data = Prepare(mode, board, comparisons);
        var models = data.Keys.Length;
        if (data.Battles.Length == 0)
        {
            return new Ratings
            {
                Mode = mode, Tag = board, Seed = seed, Samples = 0, Confidence = settings.Confidence, ProvisionalBelow = settings.ProvisionalBelow,
                Position = new PositionBias { Fitted = settings.PositionParameter },
            };
        }

        var problem = new Problem(models, settings.PositionParameter, settings.PriorSigma, settings.PositionSigma, data.Used);
        foreach (var battle in data.Battles) problem.Add(battle.Pairs, 1);
        var point = problem.Fit(new double[problem.Dimension], out var curvature);
        var factor = Cholesky(curvature, problem.Dimension);
        var spread = PosteriorSpread(factor, problem.Dimension, models);

        // The bootstrap: whole votes resampled with replacement, battles in id order. One EvalRandom makes every draw, the bootstrap's and
        // then the posterior's.
        var random = new EvalRandom(seed);
        var samples = settings.Samples;
        var drawn = new double[samples][];
        if (samples > 0)
        {
            var count = data.Battles.Length;
            var picks = new int[count];
            for (var sample = 0; sample < samples; sample++)
            {
                Array.Clear(picks);
                for (var draw = 0; draw < count; draw++) picks[random.Next(count)]++;
                problem.Clear();
                for (var index = 0; index < count; index++) if (picks[index] > 0) problem.Add(data.Battles[index].Pairs, picks[index]);
                drawn[sample] = problem.Fit(point, out _);
            }
        }

        var alpha = (1 - settings.Confidence) / 2;
        var z = InverseNormal(1 - alpha);
        var low = new double[models];
        var high = new double[models];
        for (var model = 0; model < models; model++)
        {
            var sd = spread[model];
            low[model] = point[model] - z * sd;
            high[model] = point[model] + z * sd;
            if (samples > 0)
            {
                var sorted = Column(drawn, model);
                low[model] = Math.Min(low[model], Quantile(sorted, alpha));
                high[model] = Math.Max(high[model], Quantile(sorted, 1 - alpha));
            }
        }

        // Rank ranges: the bootstrap's, together with those of as many draws from the posterior (500 when there is no bootstrap), so a
        // handful of votes still gives every rank they cannot rule out.
        var ranks = RanksOf(point, models);
        var rankLow = (int[])ranks.Clone();
        var rankHigh = (int[])ranks.Clone();
        WidenRanks(drawn, models, alpha, rankLow, rankHigh);
        var posterior = new double[samples > 0 ? samples : RaterOptions.DefaultSamples][];
        var normal = new double[problem.Dimension];
        for (var draw = 0; draw < posterior.Length; draw++)
        {
            for (var index = 0; index < normal.Length; index++) normal[index] = Gaussian(random);
            var offset = SolveUpper(factor, normal, problem.Dimension);
            for (var index = 0; index < offset.Length; index++) offset[index] += point[index];
            posterior[draw] = offset;
        }
        WidenRanks(posterior, models, alpha, rankLow, rankHigh);

        var rows = new List<RatedModel>(models);
        for (var model = 0; model < models; model++)
        {
            var tally = data.Tallies[model];
            rows.Add(new RatedModel
            {
                Identity = data.Identities[model],
                Strength = point[model],
                Rating = RatingOf(point[model]),
                Low = RatingOf(low[model]),
                High = RatingOf(high[model]),
                Rank = ranks[model],
                RankLow = rankLow[model],
                RankHigh = rankHigh[model],
                Wins = tally.Wins,
                Losses = tally.Losses,
                Ties = tally.Ties,
                Votes = tally.Votes,
                Provisional = tally.Wins + tally.Losses + tally.Ties < settings.ProvisionalBelow,
            });
        }
        rows.Sort((left, right) => left.Rank != right.Rank ? left.Rank.CompareTo(right.Rank) : string.CompareOrdinal(left.Identity.Key, right.Identity.Key));

        var position = new PositionBias { Fitted = settings.PositionParameter, Pairs = data.Decided, LeftWins = data.LeftWins };
        if (settings.PositionParameter)
        {
            var gamma = point[models];
            var gammaLow = gamma - z * spread[models];
            var gammaHigh = gamma + z * spread[models];
            if (samples > 0)
            {
                var sorted = Column(drawn, models);
                gammaLow = Math.Min(gammaLow, Quantile(sorted, alpha));
                gammaHigh = Math.Max(gammaHigh, Quantile(sorted, 1 - alpha));
            }
            position = position with { Estimate = gamma, Low = gammaLow, High = gammaHigh };
        }

        return new Ratings
        {
            Mode = mode,
            Tag = board,
            Seed = seed,
            Samples = samples,
            Confidence = settings.Confidence,
            ProvisionalBelow = settings.ProvisionalBelow,
            Votes = data.Battles.Length,
            Comparisons = data.Comparisons,
            Models = rows,
            Position = position,
        };
    }

    // ─── Preparing the votes ────────────────────────────────────────────────

    private readonly record struct Pair(int Index, double Weight, double Score);

    private sealed record Battle(string Id, Pair[] Pairs);

    private sealed class Tally
    {
        public int Wins;
        public int Losses;
        public int Ties;
        public int Votes;
    }

    private sealed record Prepared(string[] Keys, ModelIdentity[] Identities, Battle[] Battles, int[] Used, Tally[] Tallies, int Comparisons,
        int Decided, int LeftWins);

    private static Prepared Prepare(ArenaMode mode, string tag, IEnumerable<ArenaComparison> comparisons)
    {
        var byBattle = new Dictionary<string, List<(ArenaComparison Pair, ModelIdentity Left, ModelIdentity Right)>>(StringComparer.Ordinal);
        var identities = new Dictionary<string, ModelIdentity>(StringComparer.Ordinal);
        foreach (var comparison in comparisons)
        {
            if (comparison is null) throw new ArgumentException("A comparison is empty.");
            if (comparison.Mode != mode)
                throw new ArgumentException($"Request and agent votes are rated apart: a {EvalJson.Wire(comparison.Mode)} vote is not on the {EvalJson.Wire(mode)} leaderboard.");
            if (!string.Equals(EvalTags.Normalize(comparison.Tag), tag, StringComparison.Ordinal))
                throw new ArgumentException($"A vote tagged \"{comparison.Tag}\" is not on the \"{tag}\" leaderboard.");
            var battleId = EvalIds.Require(EvalIds.Battle, comparison.BattleId, "battle");
            var left = (comparison.Left ?? throw new ArgumentException("A comparison needs its left model.")).Normalize();
            var right = (comparison.Right ?? throw new ArgumentException("A comparison needs its right model.")).Normalize();
            identities.TryAdd(left.Key, left);
            identities.TryAdd(right.Key, right);
            if (!byBattle.TryGetValue(battleId, out var pairs)) byBattle[battleId] = pairs = [];
            pairs.Add((comparison, left, right));
        }

        var keys = identities.Keys.Order(StringComparer.Ordinal).ToArray();
        var models = keys.Length;
        var indexOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < models; index++) indexOf[keys[index]] = index;
        var tallies = Enumerable.Range(0, models).Select(_ => new Tally()).ToArray();
        var used = new SortedSet<int>();
        var battles = new List<Battle>(byBattle.Count);
        int comparisonCount = 0, decided = 0, leftWins = 0;
        foreach (var battleId in byBattle.Keys.Order(StringComparer.Ordinal))
        {
            var pairs = byBattle[battleId];
            var first = pairs[0].Pair;
            if (first.Contestants is < ArenaLetters.MinContestants or > ArenaLetters.MaxContestants)
                throw new ArgumentException($"Battle {battleId} has {first.Contestants} contestants; a battle has 2 to 4.");
            var weight = PairWeight(first.Kind, first.Contestants);
            var columns = new string?[first.Contestants];
            var seen = new HashSet<(int, int)>();
            var built = new List<(int LeftPosition, int RightPosition, Pair Pair)>(pairs.Count);
            foreach (var (pair, left, right) in pairs)
            {
                if (pair.Kind != first.Kind || pair.Contestants != first.Contestants)
                    throw new ArgumentException($"The pairs of battle {battleId} disagree on the vote.");
                if (pair.LeftPosition < 0 || pair.RightPosition <= pair.LeftPosition || pair.RightPosition >= first.Contestants)
                    throw new ArgumentException($"Battle {battleId} has a pair of columns that is not left to right.");
                if (!seen.Add((pair.LeftPosition, pair.RightPosition)))
                    throw new ArgumentException($"Battle {battleId} compares columns {ArenaLetters.Of(pair.LeftPosition)} and {ArenaLetters.Of(pair.RightPosition)} twice.");
                if (pair.LeftScore is not (0.0 or 0.5 or 1.0)) throw new ArgumentException($"A pair of battle {battleId} scores {pair.LeftScore}; a pair scores 0, 0.5 or 1.");
                if (string.Equals(left.Key, right.Key, StringComparison.Ordinal))
                    throw new ArgumentException($"Battle {battleId} compares {left.Name} with itself.");
                foreach (var (position, key) in new[] { (pair.LeftPosition, left.Key), (pair.RightPosition, right.Key) })
                {
                    if (columns[position] is null) columns[position] = key;
                    else if (!string.Equals(columns[position], key, StringComparison.Ordinal))
                        throw new ArgumentException($"Battle {battleId} puts two models in column {ArenaLetters.Of(position)}.");
                }
                built.Add((pair.LeftPosition, pair.RightPosition, new Pair(indexOf[left.Key] * models + indexOf[right.Key], weight, pair.LeftScore)));
            }
            if (columns.Where(key => key is not null).Distinct(StringComparer.Ordinal).Count() != columns.Count(key => key is not null))
                throw new ArgumentException($"Battle {battleId} has the same model in two columns.");
            built.Sort((x, y) => x.LeftPosition != y.LeftPosition ? x.LeftPosition.CompareTo(y.LeftPosition) : x.RightPosition.CompareTo(y.RightPosition));
            foreach (var (_, _, pair) in built)
            {
                used.Add(pair.Index);
                var leftModel = pair.Index / models;
                var rightModel = pair.Index % models;
                comparisonCount++;
                if (pair.Score == 0.5)
                {
                    tallies[leftModel].Ties++;
                    tallies[rightModel].Ties++;
                }
                else
                {
                    decided++;
                    if (pair.Score == 1)
                    {
                        leftWins++;
                        tallies[leftModel].Wins++;
                        tallies[rightModel].Losses++;
                    }
                    else
                    {
                        tallies[leftModel].Losses++;
                        tallies[rightModel].Wins++;
                    }
                }
            }
            foreach (var key in columns.Where(key => key is not null).Distinct(StringComparer.Ordinal)) tallies[indexOf[key!]].Votes++;
            battles.Add(new Battle(battleId, [.. built.Select(entry => entry.Pair)]));
        }
        return new Prepared(keys, [.. keys.Select(key => identities[key])], [.. battles], [.. used], tallies, comparisonCount, decided, leftWins);
    }

    // ─── The fit ────────────────────────────────────────────────────────────

    // The log posterior of one set of (weighted) pairs, over the models' strengths and, when fitted, the position parameter (the last
    // coordinate). W and S hold, for each ordered pair of models (left × models + right), the total weight and the total weighted left score.
    private sealed class Problem
    {
        private readonly int models;
        private readonly bool position;
        private readonly double priorPrecision;
        private readonly double positionPrecision;
        private readonly int[] used;
        private readonly double[] weights;
        private readonly double[] scores;

        public Problem(int models, bool position, double priorSigma, double positionSigma, int[] used)
        {
            this.models = models;
            this.position = position;
            priorPrecision = 1 / (priorSigma * priorSigma);
            positionPrecision = 1 / (positionSigma * positionSigma);
            this.used = used;
            weights = new double[models * models];
            scores = new double[models * models];
            Dimension = models + (position ? 1 : 0);
        }

        public int Dimension { get; }

        public void Clear()
        {
            Array.Clear(weights);
            Array.Clear(scores);
        }

        public void Add(Pair[] pairs, int count)
        {
            foreach (var pair in pairs)
            {
                var weight = count * pair.Weight;
                weights[pair.Index] += weight;
                scores[pair.Index] += weight * pair.Score;
            }
        }

        // Newton's method from start, with a backtracking (Armijo) line search; curvature is the negative Hessian at the result.
        public double[] Fit(double[] start, out double[] curvature)
        {
            var size = Dimension;
            var x = (double[])start.Clone();
            var gradient = new double[size];
            curvature = new double[size * size];
            var value = Evaluate(x, gradient, curvature);
            var candidate = new double[size];
            for (var iteration = 0; iteration < MaxIterations; iteration++)
            {
                var step = Solve(Cholesky(curvature, size), gradient, size);
                var slope = 0.0;
                for (var index = 0; index < size; index++) slope += gradient[index] * step[index];
                if (!(slope > 1e-24)) break;
                var length = 1.0;
                double next;
                while (true)
                {
                    for (var index = 0; index < size; index++) candidate[index] = x[index] + length * step[index];
                    next = Evaluate(candidate, null, null);
                    if (next >= value + 1e-4 * length * slope) break;
                    length /= 2;
                    if (length < 1e-12) return Finish(x, gradient, curvature);
                }
                var change = 0.0;
                for (var index = 0; index < size; index++) change = Math.Max(change, Math.Abs(candidate[index] - x[index]));
                (x, candidate) = (candidate, x);
                value = Evaluate(x, gradient, curvature);
                if (change < 1e-12) break;
            }
            return x;
        }

        private double[] Finish(double[] x, double[] gradient, double[] curvature)
        {
            Evaluate(x, gradient, curvature);
            return x;
        }

        private double Evaluate(double[] x, double[]? gradient, double[]? curvature)
        {
            var size = Dimension;
            if (gradient is not null) Array.Clear(gradient);
            if (curvature is not null) Array.Clear(curvature);
            var gamma = position ? x[models] : 0;
            var total = 0.0;
            foreach (var index in used)
            {
                var weight = weights[index];
                if (weight <= 0) continue;
                var score = scores[index];
                var left = index / models;
                var right = index % models;
                var difference = x[left] - x[right] + gamma;
                total += score * LogLogistic(difference) + (weight - score) * LogLogistic(-difference);
                if (gradient is null || curvature is null) continue;
                var p = Logistic(difference);
                var g = score - weight * p;
                var h = weight * p * (1 - p);
                gradient[left] += g;
                gradient[right] -= g;
                curvature[left * size + left] += h;
                curvature[right * size + right] += h;
                curvature[left * size + right] -= h;
                curvature[right * size + left] -= h;
                if (!position) continue;
                gradient[models] += g;
                curvature[models * size + models] += h;
                curvature[left * size + models] += h;
                curvature[models * size + left] += h;
                curvature[right * size + models] -= h;
                curvature[models * size + right] -= h;
            }
            for (var model = 0; model < models; model++)
            {
                total -= 0.5 * priorPrecision * x[model] * x[model];
                if (gradient is null || curvature is null) continue;
                gradient[model] -= priorPrecision * x[model];
                curvature[model * size + model] += priorPrecision;
            }
            if (position)
            {
                total -= 0.5 * positionPrecision * gamma * gamma;
                if (gradient is not null && curvature is not null)
                {
                    gradient[models] -= positionPrecision * gamma;
                    curvature[models * size + models] += positionPrecision;
                }
            }
            return total;
        }
    }

    // log(logistic(x)), without overflow.
    private static double LogLogistic(double x) => x >= 0 ? -Math.Log(1 + Math.Exp(-x)) : x - Math.Log(1 + Math.Exp(x));

    // The posterior's standard deviation of each model's strength relative to the models' average (the rating is relative: the likelihood
    // never sees a shift of every strength, so only the prior pins the average down, and its uncertainty is no part of a rating), and of the
    // position parameter (the last coordinate, when fitted). Laplace: the covariance is the inverse of the curvature at the MAP, whose
    // Cholesky factor is given.
    private static double[] PosteriorSpread(double[] factor, int size, int models)
    {
        var covariance = new double[size][];
        var unit = new double[size];
        for (var index = 0; index < size; index++)
        {
            Array.Clear(unit);
            unit[index] = 1;
            covariance[index] = Solve(factor, unit, size);
        }
        var rowMeans = new double[models];
        var grandMean = 0.0;
        for (var row = 0; row < models; row++)
        {
            for (var column = 0; column < models; column++) rowMeans[row] += covariance[row][column];
            rowMeans[row] /= models;
            grandMean += rowMeans[row];
        }
        grandMean /= models;
        var spread = new double[size];
        for (var model = 0; model < models; model++)
            spread[model] = Math.Sqrt(Math.Max(0, covariance[model][model] - 2 * rowMeans[model] + grandMean));
        for (var index = models; index < size; index++) spread[index] = Math.Sqrt(Math.Max(0, covariance[index][index]));
        return spread;
    }

    // The lower Cholesky factor of a symmetric positive definite matrix (row-major). The prior makes the curvature positive definite.
    private static double[] Cholesky(double[] matrix, int size)
    {
        var factor = new double[size * size];
        for (var column = 0; column < size; column++)
        {
            var diagonal = matrix[column * size + column];
            for (var k = 0; k < column; k++) diagonal -= factor[column * size + k] * factor[column * size + k];
            if (!(diagonal > 0)) throw new InvalidOperationException("The rater's curvature is not positive definite.");
            var root = Math.Sqrt(diagonal);
            factor[column * size + column] = root;
            for (var row = column + 1; row < size; row++)
            {
                var sum = matrix[row * size + column];
                for (var k = 0; k < column; k++) sum -= factor[row * size + k] * factor[column * size + k];
                factor[row * size + column] = sum / root;
            }
        }
        return factor;
    }

    // Solves L L^T x = right, where L is the lower Cholesky factor.
    private static double[] Solve(double[] factor, double[] right, int size)
    {
        var y = new double[size];
        for (var row = 0; row < size; row++)
        {
            var sum = right[row];
            for (var k = 0; k < row; k++) sum -= factor[row * size + k] * y[k];
            y[row] = sum / factor[row * size + row];
        }
        return SolveUpper(factor, y, size);
    }

    // Solves L^T x = y. With y standard normal, x is a draw with covariance (L L^T)^-1: the posterior's, when L factors the curvature.
    private static double[] SolveUpper(double[] factor, double[] y, int size)
    {
        var x = new double[size];
        for (var row = size - 1; row >= 0; row--)
        {
            var sum = y[row];
            for (var k = row + 1; k < size; k++) sum -= factor[k * size + row] * x[k];
            x[row] = sum / factor[row * size + row];
        }
        return x;
    }

    // ─── Statistics ─────────────────────────────────────────────────────────

    // Competition ranks (1 is the best; equal strengths share a rank).
    private static int[] RanksOf(double[] strengths, int models)
    {
        var ranks = new int[models];
        for (var model = 0; model < models; model++)
        {
            var above = 0;
            for (var other = 0; other < models; other++) if (strengths[other] > strengths[model] + RankTolerance) above++;
            ranks[model] = 1 + above;
        }
        return ranks;
    }

    // Widens each model's rank range to the confidence's percentiles of its ranks across draws (each a vector of strengths).
    private static void WidenRanks(double[][] draws, int models, double alpha, int[] low, int[] high)
    {
        if (draws.Length == 0) return;
        var ranks = new int[models][];
        for (var model = 0; model < models; model++) ranks[model] = new int[draws.Length];
        for (var draw = 0; draw < draws.Length; draw++)
        {
            var drawn = RanksOf(draws[draw], models);
            for (var model = 0; model < models; model++) ranks[model][draw] = drawn[model];
        }
        var lowIndex = (int)Math.Floor(alpha * (draws.Length - 1));
        var highIndex = (int)Math.Ceiling((1 - alpha) * (draws.Length - 1));
        for (var model = 0; model < models; model++)
        {
            Array.Sort(ranks[model]);
            low[model] = Math.Min(low[model], ranks[model][lowIndex]);
            high[model] = Math.Max(high[model], ranks[model][highIndex]);
        }
    }

    // A standard normal draw (Box-Muller; 1 - u keeps the logarithm finite).
    private static double Gaussian(EvalRandom random)
    {
        var radius = Math.Sqrt(-2 * Math.Log(1 - random.NextDouble()));
        return radius * Math.Cos(2 * Math.PI * random.NextDouble());
    }

    private static double[] Column(double[][] rows, int column)
    {
        var values = new double[rows.Length];
        for (var row = 0; row < rows.Length; row++) values[row] = rows[row][column];
        Array.Sort(values);
        return values;
    }

    // The p-quantile of sorted values, interpolating between order statistics (Hyndman and Fan's type 7).
    private static double Quantile(double[] sorted, double p)
    {
        if (sorted.Length == 1) return sorted[0];
        var h = (sorted.Length - 1) * p;
        var lower = (int)Math.Floor(h);
        var upper = Math.Min(lower + 1, sorted.Length - 1);
        return sorted[lower] + (h - lower) * (sorted[upper] - sorted[lower]);
    }

    /// <summary>The standard normal quantile of <paramref name="p"/>, for 0 &lt; p &lt; 1 (Acklam's rational approximation, relative error
    /// below 1.2e-9: plenty for an interval's width).</summary>
    internal static double InverseNormal(double p)
    {
        if (!(p > 0 && p < 1)) throw new ArgumentOutOfRangeException(nameof(p), p, "A probability between 0 and 1.");
        double[] a = [-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00];
        double[] b = [-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01];
        double[] c = [-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00];
        double[] d = [7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00];
        const double Tail = 0.02425;
        if (p < Tail)
        {
            var q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        if (p > 1 - Tail)
        {
            var q = Math.Sqrt(-2 * Math.Log(1 - p));
            return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        var centred = p - 0.5;
        var r = centred * centred;
        return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * centred / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
    }
}
