// Decision 0011 (run W5, E6): the words and numbers the Evals pages print, in one place so every page says them the same way (mockups
// evals-leaderboard, evals-suites, evals-suite, evals-run, evals-drift). Money is micro-USD through DriftText.Money ($0.31, $0.0042 under a
// cent, "≈ ... list" for a subscription's list price); times are stored in UTC and shown in this computer's local time, as History shows
// them ("today 07:00", "Oct 20, 07:00"), since the app runs on the user's own computer. Drift levels map to the mockups' chips: ok (green
// dot), info (plain), suspect (warn), drift (danger).
using System.Globalization;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Evals.Drift;

namespace PiSharp.WebAgent.Evals.Pages;

/// <summary>Text helpers for the Evals pages.</summary>
public static class EvalText
{
    /// <summary>The intro under the Evals page header (mockups: every Evals page).</summary>
    public const string Intro = "Your own leaderboard from blind votes in Compare, and test suites that catch silent changes in quality, speed and price.";

    /// <summary>An amount: <c>$0.31</c>, or <c>$0.0042</c> under a cent.</summary>
    public static string Money(long micros) => DriftText.Money(micros);

    /// <summary>An amount at list price without the word: <c>≈ $0.74</c>.</summary>
    public static string ListMoney(long micros) => "≈ " + DriftText.Money(micros);

    /// <summary>A whole number with thousands separators: <c>1,240</c>.</summary>
    public static string Count(double value) => Math.Round(value).ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>A share as a decimal: <c>0.97</c>.</summary>
    public static string Rate(double share) => share.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>A percentage of a share: <c>54%</c>.</summary>
    public static string Percent(double share) => Math.Round(share * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>A CSS percentage for a track position: <c>50.1%</c>.</summary>
    public static string CssPercent(double percent) => Math.Clamp(percent, 0, 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    /// <summary>A duration in milliseconds: <c>2.1 s</c> or <c>850 ms</c>.</summary>
    public static string Duration(double milliseconds) => DriftText.Duration(milliseconds);

    /// <summary>A run's length: <c>6 min 12 s</c>, <c>41 s</c>, or <c>1 h 05 min</c>.</summary>
    public static string Elapsed(TimeSpan span)
    {
        var seconds = (long)Math.Max(0, Math.Round(span.TotalSeconds));
        if (seconds < 60) return seconds.ToString(CultureInfo.InvariantCulture) + " s";
        if (seconds < 3600) return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60} min {seconds % 60} s");
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600} h {seconds % 3600 / 60:00} min");
    }

    /// <summary>An agent run's time on the leaderboard: <c>4m 05s</c>, or <c>48s</c>.</summary>
    public static string RunTime(double milliseconds)
    {
        var seconds = (long)Math.Max(0, Math.Round(milliseconds / 1000));
        if (seconds < 60) return seconds.ToString(CultureInfo.InvariantCulture) + "s";
        if (seconds < 3600) return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}m {seconds % 60:00}s");
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600}h {seconds % 3600 / 60:00}m");
    }

    /// <summary>Output tokens per second, one decimal: <c>62.4</c>.</summary>
    public static string Speed(double tokensPerSecond) => tokensPerSecond.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>A rating: <c>1,100</c>.</summary>
    public static string Rating(double rating) => Math.Round(rating).ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>When something happened, in local time: <c>today 07:00</c>, <c>yesterday 07:00</c>, <c>Oct 20, 07:00</c>, or
    /// <c>2025-10-20 07:00</c> in another year.</summary>
    public static string When(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime();
        var day = DateOnly.FromDateTime(local.DateTime);
        var today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        var clock = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (day == today) return "today " + clock;
        if (day == today.AddDays(-1)) return "yesterday " + clock;
        return (day.Year == today.Year ? day.ToString("MMM d", CultureInfo.InvariantCulture) : day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) + ", " + clock;
    }

    /// <summary>The day of something, in local time: <c>today</c>, <c>Oct 12</c>, or <c>2025-10-12</c>.</summary>
    public static string Day(DateTimeOffset at, DateTimeOffset now)
    {
        var day = DateOnly.FromDateTime(at.ToLocalTime().DateTime);
        var today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        if (day == today) return "today";
        if (day == today.AddDays(-1)) return "yesterday";
        return day.Year == today.Year ? day.ToString("MMM d", CultureInfo.InvariantCulture) : day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>When a scheduled slot comes, in local time: <c>today 07:00</c>, <c>tomorrow 07:00</c>, <c>Mon Oct 26, 07:00</c>.</summary>
    public static string Next(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime();
        var day = DateOnly.FromDateTime(local.DateTime);
        var today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        var clock = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (day == today) return "today " + clock;
        if (day == today.AddDays(1)) return "tomorrow " + clock;
        return local.ToString(day.Year == today.Year ? "ddd MMM d" : "ddd yyyy-MM-dd", CultureInfo.InvariantCulture) + ", " + clock;
    }

    /// <summary>A local timestamp: <c>2026-10-21 07:00:02</c>.</summary>
    public static string Stamp(DateTimeOffset at) => at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>An ISO 8601 UTC timestamp for <c>&lt;time datetime&gt;</c>.</summary>
    public static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>The chip tone of a drift level: <c>ok</c>, none (info), <c>warn</c> (suspect), <c>danger</c> (drift).</summary>
    public static string? Tone(DriftLevel level) => level switch
    {
        DriftLevel.Ok => "ok",
        DriftLevel.Suspect => "warn",
        DriftLevel.Drift => "danger",
        _ => null,
    };

    /// <summary>The chip text of a drift level: <c>ok</c>, <c>info</c>, <c>suspect</c>, <c>drift</c>.</summary>
    public static string Level(DriftLevel level) => EvalJson.Wire(level);

    /// <summary>The chip title of a drift level.</summary>
    public static string LevelTitle(DriftLevel level) => level switch
    {
        DriftLevel.Ok => "No open signal against the baseline",
        DriftLevel.Info => "Information only: quality is unchanged",
        DriftLevel.Suspect => "Suspect: the next run confirms it or clears it",
        _ => "Drift: confirmed against the baseline",
    };

    /// <summary>A run's status chip text: <c>complete</c>, <c>running</c>, <c>queued</c>, <c>cancelled</c>, <c>failed</c>,
    /// <c>interrupted</c>.</summary>
    public static string Status(SuiteRunStatus status) => EvalJson.Wire(status);

    /// <summary>A run's status chip tone.</summary>
    public static string? StatusTone(SuiteRunStatus status) => status switch
    {
        SuiteRunStatus.Complete => "ok",
        SuiteRunStatus.Failed => "danger",
        SuiteRunStatus.Interrupted => "warn",
        SuiteRunStatus.Cancelled => "outline",
        _ => null,
    };

    /// <summary>How a run was started: <c>scheduled</c> or <c>run now</c>.</summary>
    public static string Trigger(SuiteRunTrigger trigger) => trigger == SuiteRunTrigger.Schedule ? "scheduled" : "run now";

    /// <summary>A billing kind as a target's account line says it.</summary>
    public static string Billing(BillingKind billing) => billing switch
    {
        BillingKind.Metered => "Metered",
        BillingKind.Subscription => "Subscription",
        BillingKind.Free => "Free",
        _ => "Unknown price",
    };

    /// <summary>"1 vote", "17 votes".</summary>
    public static string Plural(int count, string one, string many) =>
        count.ToString("N0", CultureInfo.InvariantCulture) + " " + (count == 1 ? one : many);

    /// <summary>A case's size line: <c>12 cases × 3 targets × k 3</c>.</summary>
    public static string Size(int cases, int targets, int k) =>
        $"{Plural(cases, "case", "cases")} × {Plural(targets, "target", "targets")} × k {k.ToString(CultureInfo.InvariantCulture)}";
}
