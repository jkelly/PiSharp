// Decision 0011 (run W3, S0): money and time windows as every Spend Guard unit counts them. Amounts are micro-USD integers (1 USD =
// 1,000,000), as in the durable host's BudgetLedger, so sums and comparisons are exact; arithmetic on prices is done in decimal and rounded
// once (a worst case up, an actual cost to the nearest micro-USD). Days and months are UTC: a day starts at 00:00 UTC, a month on the 1st at
// 00:00 UTC, whatever the computer's time zone. A rate of $r per million tokens is r micro-USD per token, so a cost in micro-USD is
// tokens x rate.
using System.Globalization;

namespace PiSharp.WebAgent.Spend;

/// <summary>Micro-USD amounts: conversion, rounding, parsing and display.</summary>
public static class SpendMoney
{
    /// <summary>Micro-USD in one US dollar.</summary>
    public const long MicrosPerDollar = 1_000_000;

    /// <summary>The largest amount a cap or an entered amount may hold: $1,000,000.</summary>
    public const long MaxMicros = 1_000_000 * MicrosPerDollar;

    /// <summary><paramref name="micros"/> (possibly fractional) rounded to the nearest micro-USD, halves away from zero: an actual cost.</summary>
    /// <exception cref="OverflowException">Beyond a <see cref="long"/>.</exception>
    public static long Round(decimal micros) => checked((long)decimal.Round(micros, 0, MidpointRounding.AwayFromZero));

    /// <summary><paramref name="micros"/> (possibly fractional) rounded up to a whole micro-USD: a worst case, never below what it covers.</summary>
    /// <exception cref="OverflowException">Beyond a <see cref="long"/>.</exception>
    public static long Ceiling(decimal micros) => checked((long)decimal.Ceiling(micros));

    /// <summary>US dollars to micro-USD, rounded to the nearest micro-USD.</summary>
    public static long FromUsd(decimal usd) => Round(usd * MicrosPerDollar);

    /// <summary>US dollars (a pi-ai binary64 cost) to micro-USD, rounded to the nearest micro-USD; a non-finite value is 0.</summary>
    public static long FromUsd(double usd) => double.IsFinite(usd) ? FromUsd((decimal)usd) : 0;

    /// <summary>Micro-USD to US dollars, exactly.</summary>
    public static decimal ToUsd(long micros) => (decimal)micros / MicrosPerDollar;

    /// <summary>The cost in micro-USD (unrounded) of <paramref name="tokens"/> at <paramref name="perMillion"/> US dollars per million tokens.</summary>
    public static decimal Cost(double tokens, double perMillion) =>
        double.IsFinite(tokens) && double.IsFinite(perMillion) ? (decimal)tokens * (decimal)perMillion : 0m;

    /// <summary>An amount for the pages: <c>$1.84</c>; four decimals under a dollar when <paramref name="decimals"/> is null (as the chat's
    /// message footers show costs: <c>$0.0357</c>), else exactly <paramref name="decimals"/> (0 to 6). A cost above zero too small for the
    /// places reads <c>&lt;$0.0001</c> (or <c>&lt;$0.01</c>), not <c>$0.0000</c>.</summary>
    public static string Format(long micros, int? decimals = 2)
    {
        var usd = ToUsd(micros);
        var places = decimals ?? (Math.Abs(usd) < 1 ? 4 : 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(places, 0, nameof(decimals));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(places, 6, nameof(decimals));
        var format = places == 0 ? "#,0" : "#,0." + new string('0', places);
        // W5 live test: Claude Haiku's one-sentence answer cost 40 micro-USD, which four decimals showed as $0.0000, as if it were free.
        if (micros > 0 && decimal.Round(usd, places, MidpointRounding.AwayFromZero) == 0)
            return "<$" + (1m / (decimal)Math.Pow(10, places)).ToString(format, CultureInfo.InvariantCulture);
        var text = Math.Abs(usd).ToString(format, CultureInfo.InvariantCulture);
        return (usd < 0 ? "-$" : "$") + text;
    }

    /// <summary>A subscription's list price, never counted toward money caps: <c>≈ $0.21 list</c>.</summary>
    public static string FormatList(long micros, int? decimals = 2) => "≈ " + Format(micros, decimals) + " list";

    /// <summary>Reads an amount the user typed (<c>1.50</c>, <c>$1.50</c>, <c>1,000</c>), in US dollars, as micro-USD rounded to the nearest
    /// micro-USD. False for anything else, a negative amount or one above <see cref="MaxMicros"/>.</summary>
    public static bool TryParseUsd(string? text, out long micros)
    {
        micros = 0;
        var value = text?.Trim() ?? "";
        if (value.StartsWith('$')) value = value[1..].TrimStart();
        if (value.Length == 0 || !decimal.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var usd))
            return false;
        if (usd < 0 || usd > ToUsd(MaxMicros)) return false;
        micros = FromUsd(usd);
        return true;
    }
}

/// <summary>The UTC windows of <see cref="CapPeriod.Day"/> and <see cref="CapPeriod.Month"/> caps.</summary>
public static class SpendWindows
{
    /// <summary>The start of the UTC day that holds <paramref name="at"/>.</summary>
    public static DateTimeOffset DayStart(DateTimeOffset at)
    {
        var utc = at.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);
    }

    /// <summary>The start of the UTC month that holds <paramref name="at"/>.</summary>
    public static DateTimeOffset MonthStart(DateTimeOffset at)
    {
        var utc = at.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
    }

    /// <summary>The window of <paramref name="period"/> that holds <paramref name="at"/>, start inclusive and end exclusive: the UTC day or the
    /// UTC month; null for <see cref="CapPeriod.Request"/> and <see cref="CapPeriod.Run"/>, which have no time window.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End)? Of(CapPeriod period, DateTimeOffset at) => period switch
    {
        CapPeriod.Day => (DayStart(at), DayStart(at).AddDays(1)),
        CapPeriod.Month => (MonthStart(at), MonthStart(at).AddMonths(1)),
        _ => null,
    };
}
