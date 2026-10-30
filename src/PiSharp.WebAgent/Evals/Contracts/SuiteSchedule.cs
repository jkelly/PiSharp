// Decision 0011 (run W5, E0): a suite's schedule (mockup modal-suite-schedule, _SuiteScheduleModal: "Every few hours" (every N hours
// starting at a time), "Daily" (at a time) and "Weekly" (on a day at a time), "Local time on this computer"). This file says when the
// slots are; what the scheduler does with them is decision 0009's slot rules, applied by the Suites unit (E4): one run per (suite, slot),
// missed slots collapse into one (only Latest(now) is run), and a slot is skipped while the previous run is still going.
//
// Slots are wall-clock times in the schedule's time zone (this computer's when none is named). "Every N hours from 07:00" is every
// 07:00 + n·N hours, wrapped to the day, so every 6 hours from 07:00 is 07:00, 13:00, 19:00 and 01:00 ("4 runs a day", as the mockup says).
// A time that does not exist on a spring-forward day moves to the first minute after the gap; a time that happens twice on a fall-back day
// is its first occurrence, so a day never gets an extra run. Slots at or before EnabledAt never run: turning a schedule on never fires a
// slot that had already passed. A paused schedule (a price change raised the worst case above the cap) keeps its settings and runs nothing
// until the user raises the cap or shrinks the suite.
using System.Globalization;
using System.Text.Json.Serialization;

namespace PiSharp.WebAgent.Evals;

/// <summary>How a schedule repeats. Sent as <c>hours</c>, <c>daily</c>, <c>weekly</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScheduleRepeat>))]
public enum ScheduleRepeat
{
    /// <summary>Every <see cref="SuiteSchedule.EveryHours"/> hours from <see cref="SuiteSchedule.At"/>.</summary>
    [JsonStringEnumMemberName("hours")] Hours,

    /// <summary>Every day at <see cref="SuiteSchedule.At"/>.</summary>
    [JsonStringEnumMemberName("daily")] Daily,

    /// <summary>Every week on <see cref="SuiteSchedule.Day"/> at <see cref="SuiteSchedule.At"/>.</summary>
    [JsonStringEnumMemberName("weekly")] Weekly,
}

/// <summary>A day of a weekly schedule. Sent as <c>monday</c> to <c>sunday</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScheduleDay>))]
public enum ScheduleDay
{
    /// <summary>Monday.</summary>
    [JsonStringEnumMemberName("monday")] Monday,

    /// <summary>Tuesday.</summary>
    [JsonStringEnumMemberName("tuesday")] Tuesday,

    /// <summary>Wednesday.</summary>
    [JsonStringEnumMemberName("wednesday")] Wednesday,

    /// <summary>Thursday.</summary>
    [JsonStringEnumMemberName("thursday")] Thursday,

    /// <summary>Friday.</summary>
    [JsonStringEnumMemberName("friday")] Friday,

    /// <summary>Saturday.</summary>
    [JsonStringEnumMemberName("saturday")] Saturday,

    /// <summary>Sunday.</summary>
    [JsonStringEnumMemberName("sunday")] Sunday,
}

/// <summary>Why a schedule is paused.</summary>
/// <param name="Code"><see cref="OverCap"/> or <see cref="UnknownPrice"/>.</param>
/// <param name="Reason">What the page says (<c>A catalog price change raised its worst case to $0.54, above its $0.50 suite-run cap.</c>).</param>
/// <param name="At">When it was paused.</param>
public sealed record SchedulePause(string Code, string Reason, DateTimeOffset At)
{
    /// <summary>The worst case no longer fits the suite-run cap (a price change, or a case or target added).</summary>
    public const string OverCap = "over-cap";

    /// <summary>A target or judge has no known price, and a scheduled run cannot ask.</summary>
    public const string UnknownPrice = "unknown-price";
}

/// <summary>When a suite runs by itself.</summary>
public sealed record SuiteSchedule
{
    /// <summary>The most hours between runs of an "every few hours" schedule.</summary>
    public const int MaxEveryHours = 24;

    /// <summary>How it repeats (default daily).</summary>
    public ScheduleRepeat Repeat { get; init; } = ScheduleRepeat.Daily;

    /// <summary>Hours between runs, 1 to 24 (<see cref="ScheduleRepeat.Hours"/> only).</summary>
    public int? EveryHours { get; init; }

    /// <summary>The wall-clock time, <c>HH:mm</c> (24-hour): the time of a daily or weekly run, or the first run of an hours schedule.</summary>
    public string At { get; init; } = "07:00";

    /// <summary>The day of a weekly schedule (<see cref="ScheduleRepeat.Weekly"/> only).</summary>
    public ScheduleDay? Day { get; init; }

    /// <summary>Whether it runs at all (Turn off clears it).</summary>
    public bool Enabled { get; init; }

    /// <summary>The time zone id the times are in, or null for this computer's.</summary>
    public string? TimeZone { get; init; }

    /// <summary>When it was last turned on or changed: slots at or before it never run. Null counts every slot.</summary>
    public DateTimeOffset? EnabledAt { get; init; }

    /// <summary>Why it is paused, or null when it is not.</summary>
    public SchedulePause? Paused { get; init; }

    /// <summary>Whether its slots run now: enabled and not paused.</summary>
    [JsonIgnore]
    public bool Active => Enabled && Paused is null;

    /// <summary><see cref="At"/> as a time.</summary>
    [JsonIgnore]
    public TimeOnly Time => ParseTime(At) ?? throw new InvalidOperationException($"\"{At}\" is not a time.");

    /// <summary>How the pages say it: <c>Every 6 hours from 07:00</c>, <c>Daily at 07:00</c>, <c>Weekly, Monday 07:00</c>.</summary>
    [JsonIgnore]
    public string Label => Repeat switch
    {
        ScheduleRepeat.Hours => EveryHours == 1 ? $"Every hour from {At}" : $"Every {EveryHours} hours from {At}",
        ScheduleRepeat.Weekly => $"Weekly, {Day} {At}",
        _ => $"Daily at {At}",
    };

    /// <summary>The wall-clock times of a day it runs at, earliest first (one for daily and weekly).</summary>
    public IReadOnlyList<TimeOnly> TimesOfDay()
    {
        var start = Time;
        if (Repeat != ScheduleRepeat.Hours) return [start];
        var hours = EveryHours is >= 1 and <= MaxEveryHours ? EveryHours.Value : MaxEveryHours;
        var count = (24 + hours - 1) / hours;
        return [.. Enumerable.Range(0, count).Select(index => start.Add(TimeSpan.FromHours(index * hours))).Distinct().Order()];
    }

    /// <summary>The most runs one day can have (for the daily-cap check: "4 runs a day can reserve up to ...").</summary>
    [JsonIgnore]
    public int MaxRunsPerDay => TimesOfDay().Count;

    /// <summary>The time zone the times are in: <see cref="TimeZone"/>, or this computer's (also when that id is no longer known).</summary>
    public TimeZoneInfo Zone() =>
        TimeZone is not null && TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone, out var zone) ? zone : TimeZoneInfo.Local;

    /// <summary>The latest slot at or before <paramref name="atOrBefore"/> that counts (after <see cref="EnabledAt"/>), or null when there is
    /// none. Whether it is enabled or paused is not checked here. A scheduler runs only this one, so missed slots collapse into one.</summary>
    /// <param name="atOrBefore">The time.</param>
    /// <param name="zone">The time zone, or null for <see cref="Zone"/>.</param>
    public DateTimeOffset? Latest(DateTimeOffset atOrBefore, TimeZoneInfo? zone = null)
    {
        zone ??= Zone();
        var times = TimesOfDay();
        var today = TimeZoneInfo.ConvertTime(atOrBefore, zone).Date;
        for (var day = today.AddDays(1); day >= today.AddDays(-8); day = day.AddDays(-1))
        {
            if (!RunsOn(day)) continue;
            for (var index = times.Count - 1; index >= 0; index--)
            {
                var slot = Instant(day + times[index].ToTimeSpan(), zone);
                if (slot > atOrBefore) continue;
                return EnabledAt is { } enabled && slot <= enabled ? null : slot;
            }
        }
        return null;
    }

    /// <summary>The first slot after <paramref name="after"/> that counts (after <see cref="EnabledAt"/>): "next: tomorrow".</summary>
    /// <param name="after">The time.</param>
    /// <param name="zone">The time zone, or null for <see cref="Zone"/>.</param>
    public DateTimeOffset Next(DateTimeOffset after, TimeZoneInfo? zone = null)
    {
        zone ??= Zone();
        if (EnabledAt is { } enabled && enabled > after) after = enabled;
        var times = TimesOfDay();
        var today = TimeZoneInfo.ConvertTime(after, zone).Date;
        for (var day = today.AddDays(-1); day <= today.AddDays(9); day = day.AddDays(1))
        {
            if (!RunsOn(day)) continue;
            foreach (var time in times)
            {
                var slot = Instant(day + time.ToTimeSpan(), zone);
                if (slot > after) return slot;
            }
        }
        throw new InvalidOperationException("A schedule always has a slot within a week.");
    }

    /// <summary>The same schedule, checked: the time as <c>HH:mm</c>, only its repeat's fields kept, the time zone known.</summary>
    /// <exception cref="ArgumentException">Something is not allowed; the message says what, for the page to show.</exception>
    public SuiteSchedule Normalize()
    {
        if (!Enum.IsDefined(Repeat)) throw new ArgumentException($"Unknown repeat {(int)Repeat}.");
        var time = ParseTime(At) ?? throw new ArgumentException($"\"{At}\" is not a time: use HH:mm, such as 07:00.");
        int? hours = null;
        ScheduleDay? day = null;
        switch (Repeat)
        {
            case ScheduleRepeat.Hours:
                if (EveryHours is not (>= 1 and <= MaxEveryHours)) throw new ArgumentException($"Run every 1 to {MaxEveryHours} hours.");
                hours = EveryHours;
                break;
            case ScheduleRepeat.Weekly:
                if (Day is not { } weekday || !Enum.IsDefined(weekday)) throw new ArgumentException("Choose the day of the week.");
                day = weekday;
                break;
        }
        var zone = string.IsNullOrWhiteSpace(TimeZone) ? null : TimeZone.Trim();
        if (zone is not null && !TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _)) throw new ArgumentException($"\"{zone}\" is not a time zone of this computer.");
        if (Paused is { } paused && (string.IsNullOrWhiteSpace(paused.Code) || string.IsNullOrWhiteSpace(paused.Reason)))
            throw new ArgumentException("A paused schedule says why.");
        return this with
        {
            EveryHours = hours,
            Day = day,
            At = time.ToString("HH:mm", CultureInfo.InvariantCulture),
            TimeZone = zone,
            Paused = Paused is { } pause ? pause with { Code = pause.Code.Trim(), Reason = pause.Reason.Trim() } : null,
        };
    }

    private bool RunsOn(DateTime day) => Repeat != ScheduleRepeat.Weekly || Day is { } weekday && day.DayOfWeek == ToDayOfWeek(weekday);

    /// <summary>The .NET day of <paramref name="day"/>.</summary>
    public static DayOfWeek ToDayOfWeek(ScheduleDay day) => day == ScheduleDay.Sunday ? DayOfWeek.Sunday : (DayOfWeek)((int)day + 1);

    /// <summary>The instant of the wall-clock time <paramref name="local"/> in <paramref name="zone"/>: a time in a spring-forward gap moves
    /// to the first minute after it; a time that happens twice is its first occurrence.</summary>
    public static DateTimeOffset Instant(DateTime local, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var time = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        for (var minutes = 0; zone.IsInvalidTime(time) && minutes < 24 * 60; minutes++) time = time.AddMinutes(1);
        if (zone.IsAmbiguousTime(time)) return new DateTimeOffset(time, zone.GetAmbiguousTimeOffsets(time).Max());
        return new DateTimeOffset(time, zone.GetUtcOffset(time));
    }

    private static TimeOnly? ParseTime(string? text) =>
        TimeOnly.TryParseExact(text?.Trim(), ["HH:mm", "H:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? new TimeOnly(time.Hour, time.Minute) : null;
}
