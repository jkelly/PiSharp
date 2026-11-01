// Decision 0011 (run W4, R3): the models of History › Runs (Views/Runs/Index.cshtml and its partials) and the text helpers both pages
// share. Every figure comes from runs.db as stored: tokens and costs are the provider's report (or the Spend Guard's settlement), times are
// the app's local time, and a value that was not recorded is a dash, never a guess.
using System.Globalization;
using PiSharp.WebAgent.Chat;

namespace PiSharp.WebAgent.Runs;

/// <summary>The model of <c>Views/Runs/Index.cshtml</c> (GET /history).</summary>
public sealed record RunHistoryPage
{
    /// <summary>The filter the page shows.</summary>
    public required RunHistoryFilter Filter { get; init; }

    /// <summary>The rows of this page (top-level runs, newest first).</summary>
    public IReadOnlyList<RunHistoryRow> Rows { get; init; } = [];

    /// <summary>How many top-level runs match the filter.</summary>
    public long Total { get; init; }

    /// <summary>How many top-level runs runs.db holds.</summary>
    public long TotalAll { get; init; }

    /// <summary>The totals strip.</summary>
    public required RunTotalsView Totals { get; init; }

    /// <summary>The providers runs.db has calls for.</summary>
    public IReadOnlyList<RunFilterOption> Providers { get; init; } = [];

    /// <summary>The chosen provider's models.</summary>
    public IReadOnlyList<RunFilterOption> Models { get; init; } = [];

    /// <summary>The saved filters.</summary>
    public IReadOnlyList<RunSavedFilterView> Saved { get; init; } = [];

    /// <summary>runs.db's path.</summary>
    public string DbPath { get; init; } = "";

    /// <summary>runs.db's size on disk.</summary>
    public long SizeBytes { get; init; }

    /// <summary>The retention, as the empty state says it (<c>retention 30 days</c>, <c>kept forever</c>).</summary>
    public string RetentionText { get; init; } = "";

    /// <summary>How many top-level runs are pinned (Clear history can keep them).</summary>
    public long PinnedCount { get; init; }

    /// <summary>Whether runs.db holds no run at all.</summary>
    public bool Empty => TotalAll == 0;

    /// <summary>The number of pages.</summary>
    public int PageCount => Total == 0 ? 1 : (int)Math.Ceiling(Total / (double)Filter.Size);

    /// <summary>The first row's position on this page (1-based), or 0.</summary>
    public long FirstShown => Total == 0 ? 0 : (long)(Filter.Page - 1) * Filter.Size + 1;

    /// <summary>The last row's position on this page.</summary>
    public long LastShown => Math.Min(Total, FirstShown + Rows.Count - 1);

    /// <summary>The page links: numbers, with null for a gap.</summary>
    public IReadOnlyList<int?> PageLinks
    {
        get
        {
            var count = PageCount;
            var page = Math.Min(Filter.Page, count);
            var shown = new SortedSet<int> { 1, count };
            for (var at = page - 1; at <= page + 1; at++) if (at >= 1 && at <= count) shown.Add(at);
            if (page <= 3) for (var at = 2; at <= Math.Min(3, count); at++) shown.Add(at);
            var links = new List<int?>();
            var last = 0;
            foreach (var number in shown)
            {
                if (number - last > 1) links.Add(null);
                links.Add(number);
                last = number;
            }
            return links;
        }
    }
}

/// <summary>A select option.</summary>
public sealed record RunFilterOption(string Value, string Text);

/// <summary>A saved filter as the menu lists it.</summary>
public sealed record RunSavedFilterView(string Name, string Query, string Href, string Description, string Icon, bool Current);

/// <summary>The totals strip (<c>_RunTotals</c>).</summary>
/// <param name="Filtered">Whether a filter is set (totals of the matching runs) or not (the last 30 days).</param>
/// <param name="Share">The share of each source (<c>Chat 61% · Playground 18%</c>), or null.</param>
/// <param name="Metrics">Runs, Tokens in, Tokens out, Est. cost, Avg TTFT, Error rate.</param>
public sealed record RunTotalsView(bool Filtered, string? Share, IReadOnlyList<RunTotalMetric> Metrics)
{
    /// <summary>The strip's label.</summary>
    public string Label => Filtered ? "Totals · matching runs" : "Totals · last 30 days";
}

/// <summary>One metric of the totals strip.</summary>
public sealed record RunTotalMetric(string Label, string Value, string? Unit, string Sub, string? Title = null, string? Tone = null);

/// <summary>A chip: text, a tone (<c>ok</c>, <c>info</c>, <c>danger</c>, <c>warn</c>, <c>outline</c>, <c>accent</c>) and an icon.</summary>
public sealed record RunChip(string Text, string Tone, string? Icon = null);

/// <summary>A two-line table cell: the top line, the faint bottom line and a tooltip.</summary>
public sealed record RunCell(string Top, string? Bottom = null, string? Title = null, bool Faint = false);

/// <summary>One row of <c>_RunTable</c>: a top-level run (a provider call, or a run that groups calls such as an agent compare run).</summary>
public sealed record RunHistoryRow
{
    /// <summary>The run (without its options' detail, messages and output).</summary>
    public required RunRecord Run { get; init; }

    /// <summary>Its local time (<c>10:44:07</c>).</summary>
    public required string Time { get; init; }

    /// <summary>Its day (<c>Today</c>, <c>Yesterday</c>, <c>Oct 8</c>).</summary>
    public required string Day { get; init; }

    /// <summary>The source's label.</summary>
    public required string SourceLabel { get; init; }

    /// <summary>The source's icon.</summary>
    public required string SourceIcon { get; init; }

    /// <summary>The line under the model (<c>thinking medium · 2 tools</c>), or empty.</summary>
    public string ModelSub { get; init; } = "";

    /// <summary>The prompt (the run's title).</summary>
    public string Prompt { get; init; } = "";

    /// <summary>The tools the answer called (<c>edit, bash</c>), or null.</summary>
    public string? ToolCalls { get; init; }

    /// <summary>The line under the prompt (<c>id · session 9c1e4b72</c>).</summary>
    public string Sub { get; init; } = "";

    /// <summary>Tokens (in / out) or turns.</summary>
    public required RunCell Tokens { get; init; }

    /// <summary>The cost.</summary>
    public required RunCell Cost { get; init; }

    /// <summary>TTFT / total.</summary>
    public required RunCell Latency { get; init; }

    /// <summary>The stop chip.</summary>
    public required RunChip Stop { get; init; }

    /// <summary>The line under the stop chip (<c>HTTP 429</c>), or null.</summary>
    public string? StopSub { get; init; }

    /// <summary>Whether <see cref="StopSub"/> is an error.</summary>
    public bool StopSubDanger { get; init; }

    /// <summary>Whether this row groups calls (an agent compare run): it expands to <see cref="Models"/>.</summary>
    public bool IsGroup { get; init; }

    /// <summary>The grouped models (or calls).</summary>
    public IReadOnlyList<RunHistoryModel> Models { get; init; } = [];

    /// <summary>W5: the Arena battle (held or ready) this run belongs to, whose vote reveals it, or null. A blind row names no model, shows no
    /// tokens, cost or timings (<see cref="Run"/> is masked) and links to the battle.</summary>
    public string? Blind { get; init; }

    /// <summary>The inspector (a blind row: its battle).</summary>
    public string InspectHref => Blind is { } battle ? Evals.Arena.ArenaBlindText.Href(battle) : "/runs/" + Uri.EscapeDataString(Run.Id);

    /// <summary>Whether Re-run and Load into playground apply (the run stored its request: not a chat's call or a recorded durable call; not
    /// blind).</summary>
    public bool Replayable => Blind is null && RunHistoryText.Replayable(Run);

    /// <summary>The Playground with this run's request.</summary>
    public string PlaygroundHref => "/playground?run=" + Uri.EscapeDataString(Run.Id);

    /// <summary>The chat a chat's call belongs to, or null.</summary>
    public string? ChatHref => Run.Source == "chat" && !string.IsNullOrEmpty(Run.Session) ? "/chat/" + Uri.EscapeDataString(Run.Session) : null;

    /// <summary>An agent compare run's page, or null (a blind one opens its battle instead).</summary>
    public string? CompareHref => Blind is null ? RunHistoryText.AgentCompareHref(Run) : null;

    /// <summary>The id of the collapse row of <see cref="Models"/>.</summary>
    public string ModelsId => "run-" + Run.Id + "-models";
}

/// <summary>One model (or call) of a grouped row.</summary>
/// <param name="Letter">The slot letter (<c>A</c>), or a number.</param>
/// <param name="Provider">The provider.</param>
/// <param name="Model">The model.</param>
/// <param name="Note">What follows the model (<c>thinking medium</c>), or null.</param>
/// <param name="Line">The outcome line (<c>finished · 9 turns · 3m 12s · $0.21</c>).</param>
/// <param name="Check">The check chip, or null.</param>
/// <param name="Href">Its inspector.</param>
public sealed record RunHistoryModel(string Letter, string Provider, string Model, string? Note, string Line, RunChip? Check, string Href);

/// <summary>Text helpers of History › Runs and the Run inspector.</summary>
public static class RunHistoryText
{
    /// <summary>Whether <paramref name="run"/> stored its request so it can be re-run or loaded into the Playground.</summary>
    public static bool Replayable(RunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run.Kind == RunKind.Call && run.Source != "chat" && run.MessagesRef is null;
    }

    /// <summary>The inspector of a History › Spend ledger row's run: <c>/runs/{id}</c> for a run engine's op (<c>run-&lt;id&gt;</c>), the
    /// op itself otherwise (the inspector finds a run by its op: an agent compare turn is recorded under its ledger op). A later send of an op
    /// (the guard's <c>op~n</c>) opens the op's run, which that send updated. Null for a chat's row (it opens the chat).</summary>
    public static string? LedgerRunHref(Spend.Reservation row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Source == Spend.SpendSource.Chat) return null;
        var op = SendOf(row.Op);
        if (op.StartsWith("run-", StringComparison.Ordinal) && op.Length > 4) return "/runs/" + Uri.EscapeDataString(op[4..]);
        return "/runs/" + Uri.EscapeDataString(op);
    }

    /// <summary><paramref name="op"/> without the Spend Guard's later-send suffix (<c>~2</c>, <c>~3</c>, ...): the op the run was recorded under.</summary>
    public static string SendOf(string op)
    {
        ArgumentNullException.ThrowIfNull(op);
        var at = op.LastIndexOf('~');
        return at > 0 && at < op.Length - 1 && op.AsSpan(at + 1).IndexOfAnyExceptInRange('0', '9') < 0 ? op[..at] : op;
    }

    /// <summary>The agent compare page of an agent compare run's top row (<c>/compare?mode=agent&amp;run={id}</c>: runs.db's parent row has the
    /// agent compare run's id), or null for any other run.</summary>
    public static string? AgentCompareHref(RunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run.Kind == RunKind.Parent && run.ParentId is null
            && (run.Source == Spend.SpendSources.Wire(Spend.SpendSource.AgentCompare) || run.Source == Spend.SpendSources.Wire(Spend.SpendSource.Arena))
            ? "/compare?mode=agent&run=" + Uri.EscapeDataString(run.Id) : null;
    }

    /// <summary>Micro-USD as dollars (four places under a cent).</summary>
    public static string Money(long micros) => ChatSpendText.Money(micros);

    /// <summary>A token count with thousands separators.</summary>
    public static string Tokens(double tokens) => tokens.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>A large count, short (<c>3,812</c>, <c>48.2K</c>, <c>3.91M</c>).</summary>
    public static string Compact(double value)
    {
        if (Math.Abs(value) < 10_000) return value.ToString("N0", CultureInfo.InvariantCulture);
        if (Math.Abs(value) < 1_000_000) return Three(value / 1_000) + "K";
        if (Math.Abs(value) < 1_000_000_000) return Three(value / 1_000_000) + "M";
        return Three(value / 1_000_000_000) + "B";
    }

    private static string Three(double value) => value.ToString(value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##", CultureInfo.InvariantCulture);

    /// <summary>Milliseconds as seconds (<c>1.42 s</c>).</summary>
    public static string Seconds(long ms) => (ms / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " s";

    /// <summary>A duration (<c>16.9 s</c>, <c>4m 05s</c>, <c>1h 02m</c>).</summary>
    public static string Duration(long ms)
    {
        if (ms < 60_000) return (ms / 1000.0).ToString(ms < 10_000 ? "0.0#" : "0.0", CultureInfo.InvariantCulture) + " s";
        var time = TimeSpan.FromMilliseconds(ms);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}h {time.Minutes:00}m"
            : $"{(int)time.TotalMinutes}m {time.Seconds:00}s";
    }

    /// <summary>A byte count (<c>18.4 KB</c>).</summary>
    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => bytes.ToString("N0", CultureInfo.InvariantCulture) + " B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        < 1024L * 1024 * 1024 => (bytes / 1024.0 / 1024).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / 1024.0 / 1024 / 1024).ToString("0.##", CultureInfo.InvariantCulture) + " GB",
    };

    /// <summary>The local time of <paramref name="time"/> (<c>10:44:07</c>).</summary>
    public static string Time(DateTimeOffset time) => time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>The local day of <paramref name="time"/> relative to <paramref name="now"/>: Today, Yesterday, <c>Oct 8</c>, or
    /// <c>2025-10-08</c> in another year.</summary>
    public static string Day(DateTimeOffset time, DateTimeOffset now)
    {
        var day = DateOnly.FromDateTime(time.ToLocalTime().DateTime);
        var today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        if (day == today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";
        return day.Year == today.Year ? day.ToString("MMM d", CultureInfo.InvariantCulture) : day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>The stop chip of a run: its stop reason, or its outcome.</summary>
    public static RunChip Stop(RunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run.Status switch
        {
            RunStatus.Running => new RunChip("running", "info", "bi-broadcast"),
            RunStatus.Error => new RunChip("error", "danger", "bi-x-octagon"),
            RunStatus.Aborted => new RunChip("aborted", "outline", "bi-stop-circle"),
            RunStatus.Interrupted => new RunChip("interrupted", "warn", "bi-exclamation-triangle"),
            _ => run.StopReason switch
            {
                "toolUse" => new RunChip("toolUse", "info", "bi-tools"),
                "length" => new RunChip("length", "warn", "bi-scissors"),
                "aborted" => new RunChip("aborted", "outline", "bi-stop-circle"),
                "error" => new RunChip("error", "danger", "bi-x-octagon"),
                { } reason => new RunChip(reason, "outline", "bi-check2-circle"),
                null => new RunChip("completed", "outline", "bi-check2-circle"),
            },
        };
    }

    /// <summary>The HTTP status an error message names (<c>429 …</c>, <c>HTTP 503</c>, <c>status 401</c>), or null.</summary>
    public static string? HttpStatus(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(error, @"^(?:HTTP\s*)?([45]\d\d)\b|\bHTTP\s*([45]\d\d)\b|\bstatus(?:\s+code)?\s*:?\s*([45]\d\d)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!match.Success) return null;
        return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
    }

    /// <summary>The cost cell of a call: what it settled at, by its billing.</summary>
    public static RunCell Cost(RunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.CostMicros is not { } micros)
            return new RunCell("—", null, run.Status == RunStatus.Running ? "The cost is known when the run ends." : "No cost was recorded for this run.", Faint: true);
        var money = Money(micros);
        return (run.Billing, run.CostKind) switch
        {
            ("free", _) => new RunCell(money, null, "Free: nothing counted toward your caps."),
            ("subscription", _) => new RunCell("≈ " + money, "list", "List price at catalog rates · billed through a subscription, not counted toward spend caps."),
            (_, "estimate") => new RunCell("≈ " + money, "est.", "Settled at an estimate from what was received."),
            (_, "uncertain") => new RunCell(money, "reserved", "The stream ended without usage: the reserved amount counts until you resolve it in History › Spend."),
            (_, "released") => new RunCell(money, null, "Released: nothing was charged."),
            _ => new RunCell(money, null, run.Billing == "metered" ? "Metered: counted toward your caps." : null),
        };
    }
}
