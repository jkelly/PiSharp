// Decision 0011 (run W5, E3): the statistics Drift Watch uses. The pass-rate and judge-score rules use an exact one-sided sign test over cases
// (the plan's table): each case that got worse is a "+", each that got better a "-", ties are left out, and the p-value is the chance of at
// least that many "+" signs out of n when either sign is equally likely, P(X >= worse) for X ~ Binomial(n, 1/2), summed exactly from the
// binomial terms (through log factorials, so large suites stay finite). Speed rules compare a median with the baseline's 10th to 90th
// percentile range, by linear interpolation between order statistics (Hyndman and Fan's type 7, as spreadsheets compute PERCENTILE.INC).
// Nothing here draws a random number: the same results give the same numbers whatever their order.
namespace PiSharp.WebAgent.Evals.Drift;

/// <summary>The sign test, percentiles and medians of Drift Watch.</summary>
public static class DriftStatistics
{
    /// <summary>The exact one-sided p-value of the sign test: the chance of <paramref name="worse"/> or more "worse" signs out of
    /// <paramref name="worse"/> + <paramref name="better"/> when each sign is equally likely. 1 when there are no signs.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A count is negative.</exception>
    public static double SignTest(int worse, int better)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(worse);
        ArgumentOutOfRangeException.ThrowIfNegative(better);
        var n = worse + better;
        if (n == 0 || worse == 0) return 1;
        var logFactorial = new double[n + 1];
        for (var index = 1; index <= n; index++) logFactorial[index] = logFactorial[index - 1] + Math.Log(index);
        var logHalf = n * Math.Log(2);
        var sum = 0d;
        for (var count = worse; count <= n; count++)
            sum += Math.Exp(logFactorial[n] - logFactorial[count] - logFactorial[n - count] - logHalf);
        return Math.Clamp(sum, 0, 1);
    }

    /// <summary>The <paramref name="quantile"/> (0 to 1) of <paramref name="values"/> by linear interpolation between order statistics
    /// (type 7); null when there are none.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The quantile is outside 0 to 1.</exception>
    public static double? Percentile(IEnumerable<double> values, double quantile)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!double.IsFinite(quantile) || quantile is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(quantile), "A quantile is 0 to 1.");
        var sorted = values.Where(double.IsFinite).Order().ToArray();
        if (sorted.Length == 0) return null;
        var position = (sorted.Length - 1) * quantile;
        var below = (int)Math.Floor(position);
        var above = Math.Min(below + 1, sorted.Length - 1);
        return sorted[below] + (position - below) * (sorted[above] - sorted[below]);
    }

    /// <summary>The median of <paramref name="values"/> (the mean of the middle two for an even count), or null when there are none.</summary>
    public static double? Median(IEnumerable<double> values) => TargetSummary.Median(values);
}
