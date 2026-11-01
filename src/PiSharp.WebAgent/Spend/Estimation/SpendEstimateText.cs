// Decision 0011 (run W3, S1): how an estimate reads on the pages (the composer's estimate by Send, the confirm modal, Settings › Spend's
// "How the worst case is worked out"), so every page says it the same way and every worked example adds up. The chip shows the typical
// cost when the ledger has one, else the worst case: "≈ $0.03", "≈ $0.21 list" for a subscription, nothing for a free request, "price
// unknown" without a price. The formula is the estimate's own arithmetic, "12,000 × $2.00/M + 8,192 × $10.00/M = $0.0240 + $0.0819 =
// $0.1059": its parts are shown to as many decimals (4, or 2 from $1, up to 6) as it takes for them to add up to the worst case shown; in
// the rare case even micro-USD do not (the total is rounded up once), the input part is rounded up and the output part is the rest, so the
// equation shown always adds up and each part is within one micro-USD of its exact cost.
using System.Globalization;
using PiSharp.WebAgent.Contracts;

namespace PiSharp.WebAgent.Spend;

/// <summary>Short texts of an estimate for the pages.</summary>
public static class SpendEstimateText
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>The estimate by Send: <c>≈ $0.03</c> (the typical cost, else the worst case; four decimals under a cent), <c>≈ $0.21 list</c>
    /// for a subscription, <c>price unknown</c> without a price; null for a request that costs nothing (free, $0, or a subscription
    /// without a list price).</summary>
    public static string? Chip(SpendEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        if (estimate.Billing == BillingKind.Free) return null;
        if (estimate.WorstMicros is not { } worst) return estimate.Billing == BillingKind.Subscription ? null : "price unknown";
        var shown = estimate.TypicalMicros ?? worst;
        if (shown <= 0 && worst <= 0) return null;
        var text = "≈ " + SpendMoney.Format(shown, shown is > 0 and < 10_000 ? 4 : 2);
        return estimate.Billing == BillingKind.Subscription ? text + " list" : text;
    }

    /// <summary>The worst case worked out: <c>12,000 × $2.50/M + 8,192 × $10.00/M = $0.0300 + $0.0819 = $0.1119</c> (the input at the
    /// higher of the input and cache-write rates of the tier used). Null without a price, or for a free request.</summary>
    public static string? Formula(SpendEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        if (estimate.Billing == BillingKind.Free || estimate.Price is not { } price || estimate.WorstMicros is not { } total) return null;
        var input = SpendMoney.Cost(estimate.InputTokens, price.WorstInput);
        var output = SpendMoney.Cost(estimate.OutputTokens, price.Output);
        var first = total >= SpendMoney.MicrosPerDollar ? 2 : 4;
        var places = -1;
        for (var candidate = first; candidate <= 6 && places < 0; candidate++)
            if (Usd(input, candidate) + Usd(output, candidate) == Usd(total, candidate)) places = candidate;
        if (places < 0)
        {
            // Even micro-USD do not add up (the total is rounded up once, each part has a fraction): the input part rounded up, and the
            // output part the rest of the total, each within one micro-USD of its exact value.
            places = 6;
            input = decimal.Ceiling(input);
            output = total - input;
        }
        return $"{Tokens(estimate.InputTokens)} × {Rate(price.WorstInput)} + {Tokens(estimate.OutputTokens)} × {Rate(price.Output)} = " +
            $"{Money(input, places)} + {Money(output, places)} = {Money(total, places)}";
    }

    /// <summary>Why the input rate is a price tier's, or null when the base rates apply: <c>Above 100K input tokens, Claude Haiku 5.5 uses its
    /// higher price tier: $0.50 / $2.50 per 1M.</c>, or, within 10% below the boundary, that the worst case assumes the tier.</summary>
    public static string? TierNote(SpendEstimate estimate, string? modelName = null)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        if (estimate.Price is not { TierInputTokensAbove: { } above } price) return null;
        var name = string.IsNullOrWhiteSpace(modelName) ? estimate.Model : modelName;
        var rates = $"{RateAmount(price.Input)} / {RateAmount(price.Output)} per 1M";
        return estimate.InputTokens > above
            ? $"Above {Threshold(above)} input tokens, {name} uses its higher price tier: {rates}."
            : $"Within 10% of {name}'s {Threshold(above)} input-token tier boundary, so the worst case uses its higher price tier: {rates}.";
    }

    /// <summary>Where the output tokens of the worst case came from (<see cref="SpendEstimate.OutputBasis"/>).</summary>
    public static string OutputNote(SpendEstimate estimate, string? modelName = null)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        var tokens = Tokens(estimate.OutputTokens);
        return estimate.OutputBasis switch
        {
            OutputTokenBasis.Request => $"The request asks for at most {tokens} output tokens.",
            OutputTokenBasis.Chosen => $"You capped the output at {tokens} tokens.",
            OutputTokenBasis.ModelMaximum => $"{(string.IsNullOrWhiteSpace(modelName) ? estimate.Model : modelName)} writes at most {tokens} output tokens.",
            _ => $"The request doesn't set maxTokens, so the worst case uses the default reservation cap of {tokens} output tokens.",
        };
    }

    /// <summary>A rate per million tokens: <c>$2.00/M</c>, <c>$0.075/M</c>.</summary>
    public static string Rate(double perMillion) => RateAmount(perMillion) + "/M";

    /// <summary>A rate without the unit: <c>$2.00</c>, <c>$0.075</c> (two decimals, or as many as it has, up to six).</summary>
    public static string RateAmount(double perMillion)
    {
        var value = double.IsFinite(perMillion) ? decimal.Round((decimal)perMillion, 6, MidpointRounding.AwayFromZero) : 0m;
        return "$" + value.ToString(decimal.Round(value, 2) == value ? "#,0.00" : "#,0.00####", Invariant);
    }

    /// <summary>A whole token count: <c>12,000</c>.</summary>
    public static string Tokens(double tokens) =>
        (double.IsFinite(tokens) ? (decimal)Math.Ceiling(Math.Max(0, tokens)) : 0m).ToString("#,0", Invariant);

    /// <summary>A tier threshold: <c>100K</c>, <c>272K</c>, <c>1M</c>, <c>1.25M</c>; other counts as <see cref="Tokens"/>.</summary>
    public static string Threshold(double tokens)
    {
        if (!double.IsFinite(tokens)) return Tokens(0);
        if (tokens >= 1_000_000 && tokens % 10_000 == 0) return ((decimal)tokens / 1_000_000).ToString("0.##", Invariant) + "M";
        if (tokens >= 1_000 && tokens % 1_000 == 0) return ((decimal)tokens / 1_000).ToString("0", Invariant) + "K";
        return Tokens(tokens);
    }

    private static decimal Usd(decimal micros, int places) => decimal.Round(micros / SpendMoney.MicrosPerDollar, places, MidpointRounding.AwayFromZero);

    private static string Money(decimal micros, int places)
    {
        var usd = Usd(micros, places);
        return (usd < 0 ? "-$" : "$") + Math.Abs(usd).ToString("#,0." + new string('0', places), Invariant);
    }
}
