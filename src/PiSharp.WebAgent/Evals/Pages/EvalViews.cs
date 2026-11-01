// Decision 0011 (run W5, E6): the view models of the five Evals pages and their three modals (mockups evals-leaderboard, evals-suites,
// evals-suite, evals-run, evals-drift; modal-check-editor, modal-suite-schedule, modal-baseline). EvalPages builds them from the stores as
// they are now: the leaderboard from voted battles only (an unvoted battle never reaches a page), suites, runs, results, baselines and
// signals from webagent.db's eval tables, prices from Price Watch's snapshots. Nothing here is sample data; a page with nothing to show
// renders its empty state. Each record says which partial renders it.
using System.Globalization;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Evals.Checks;
using PiSharp.WebAgent.Evals.Drift;
using PiSharp.WebAgent.Evals.Suites;
using PiSharp.WebAgent.Shell;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Evals.Pages;

/// <summary>The three tabs of the Evals area (<c>_EvalsTabs</c>).</summary>
public enum EvalsTab
{
    /// <summary>GET /evals/leaderboard.</summary>
    Leaderboard,

    /// <summary>GET /evals/suites (and a suite's pages, which have no tabs).</summary>
    Suites,

    /// <summary>GET /evals/drift.</summary>
    Drift,
}

/// <summary>A model as the Evals pages name it: <c>provider/</c> faint, the model, and its settings line; with the provider's logo.</summary>
/// <param name="Identity">The provider, model and settings (never the account).</param>
/// <param name="ProviderName">The provider's display name (for the logo's label).</param>
/// <param name="LogoUrl">The provider's logo, or null for its initial.</param>
public sealed record EvalModelView(ModelIdentity Identity, string ProviderName, string? LogoUrl)
{
    /// <summary>"provider/".</summary>
    public string Prefix => Identity.Provider + "/";

    /// <summary>The model id.</summary>
    public string Model => Identity.Model;

    /// <summary>"provider/model".</summary>
    public string Name => Identity.Name;

    /// <summary>"thinking medium", or "default settings".</summary>
    public string Settings => Identity.SettingsLabel;
}

/// <summary>The 404 page of an unknown suite or run (Views/Evals/NotFound.cshtml).</summary>
/// <param name="What">"suite" or "run".</param>
/// <param name="Id">The id asked for.</param>
public sealed record EvalNotFound(string What, string Id);

// ----- leaderboard -----

/// <summary>GET /evals/leaderboard (Leaderboard.cshtml): the tag menu, the chosen mode and tag's table, or the empty state.</summary>
public sealed record LeaderboardPage
{
    /// <summary>The leaderboard's mode.</summary>
    public required ArenaMode Mode { get; init; }

    /// <summary>The chosen tag, or null when nothing is voted in <see cref="Mode"/>.</summary>
    public string? Tag { get; init; }

    /// <summary>Every leaderboard the votes make, request tags first.</summary>
    public IReadOnlyList<LeaderboardTag> Tags { get; init; } = [];

    /// <summary>The chosen leaderboard, or null.</summary>
    public Leaderboard? Board { get; init; }

    /// <summary>Its rows, best first.</summary>
    public IReadOnlyList<LeaderboardRowView> Rows { get; init; } = [];

    /// <summary>"You picked the left answer 54% of the time. ..." once the provisional threshold's number of pairs is decided, a line saying
    /// when it shows before that, or null before any decided pair.</summary>
    public string? PositionText { get; init; }

    /// <summary>Whether no battle is voted yet in any mode (<c>_LeaderboardEmpty</c>).</summary>
    public bool NoVotes => Tags.Count == 0;

    /// <summary>The page state for tests and the script: <c>empty</c>, <c>provisional</c>, <c>agent</c> or <c>default</c>.</summary>
    public string State => NoVotes ? "empty" : Mode == ArenaMode.Agent ? "agent" : Board is { Provisional: true } ? "provisional" : "default";

    /// <summary>The toolbar's tag button: <c>Tag: coding</c>.</summary>
    public string TagLabel => "Tag: " + (Tag ?? "none");

    /// <summary>Where Blind vote goes: Compare with its Blind switch on (in agent mode, Agent compare).</summary>
    public string BlindUrl => Mode == ArenaMode.Agent ? "/compare?mode=agent&blind=1" : "/compare?blind=1";

    /// <summary>The votes of this leaderboard as CSV.</summary>
    public string? CsvUrl => Tag is null ? null : EvalPages.VotesCsvUrl(Mode, Tag);

    /// <summary>The leaderboard of <paramref name="tag"/> in <paramref name="mode"/>.</summary>
    public static string Url(ArenaMode mode, string tag) => EvalRoutes.Leaderboard + "?mode=" + EvalJson.Wire(mode) + "&tag=" + Uri.EscapeDataString(tag);
}

/// <summary>One row of <c>_LeaderboardTable</c>: the rating and the medians, with where the interval falls on the track.</summary>
/// <param name="Rated">The rating, interval, rank range and record.</param>
/// <param name="Metrics">The medians of the answers (or runs) voted on.</param>
/// <param name="Model">The model.</param>
/// <param name="TrackLeft">The interval's left end on the track (percent).</param>
/// <param name="TrackWidth">The interval's width on the track (percent).</param>
/// <param name="TrackMark">The rating on the track (percent).</param>
public sealed record LeaderboardRowView(RatedModel Rated, ModelMetrics Metrics, EvalModelView Model, double TrackLeft, double TrackWidth, double TrackMark);

// ----- suites -----

/// <summary>GET /evals/suites (Suites.cshtml): a card per suite, the paused notices, or the empty state.</summary>
/// <param name="Cards">Every suite, by name.</param>
public sealed record SuitesPage(IReadOnlyList<SuiteCardView> Cards)
{
    /// <summary>The suites whose schedule is paused.</summary>
    public IReadOnlyList<SuiteCardView> Paused => [.. Cards.Where(card => card.Paused is not null)];

    /// <summary>Whether there is no suite (<c>_SuitesEmpty</c>).</summary>
    public bool Empty => Cards.Count == 0;

    /// <summary>The page state: <c>empty</c>, <c>paused</c> or <c>default</c>.</summary>
    public string State => Empty ? "empty" : Paused.Count > 0 ? "paused" : "default";
}

/// <summary>One <c>_SuiteCard</c>.</summary>
public sealed record SuiteCardView
{
    /// <summary>The suite, its case count, estimate, next slot, last and active run, baselines and open signals.</summary>
    public required SuiteOverview Overview { get; init; }

    /// <summary>Its targets.</summary>
    public IReadOnlyList<SuiteTargetView> Targets { get; init; } = [];

    /// <summary>"next: tomorrow 07:00", or null when the schedule is off or paused.</summary>
    public string? NextText { get; init; }

    /// <summary>Its newest run, or null.</summary>
    public RunLine? LastRun { get; init; }

    /// <summary>The suite.</summary>
    public Suite Suite => Overview.Suite;

    /// <summary>Its schedule's pause, or null.</summary>
    public SchedulePause? Paused => Overview.Suite.Schedule is { Enabled: true, Paused: { } pause } ? pause : null;
}

/// <summary>A run in one line (a suite card's Last run, the suite header): its number, when, how many passed and its drift.</summary>
/// <param name="Run">The run.</param>
/// <param name="When">"today 07:00".</param>
/// <param name="Passed">Answers that passed.</param>
/// <param name="Finished">Answers finished.</param>
/// <param name="Level">The worst level of the signals it raised.</param>
/// <param name="SignalCount">How many signals at that level it raised.</param>
public sealed record RunLine(SuiteRun Run, string When, int Passed, int Finished, DriftLevel Level, int SignalCount)
{
    /// <summary>Its page.</summary>
    public string Url => EvalRoutes.Run(Run.Id);

    /// <summary>"Run #21".</summary>
    public string Title => "Run #" + Run.Number.ToString(CultureInfo.InvariantCulture);

    /// <summary>The chip: "1 drift", "2 suspect", "info", "ok", or the status of a run that did not complete.</summary>
    public string Chip => Run.Active ? "running"
        : Run.Status != SuiteRunStatus.Complete ? EvalText.Status(Run.Status)
        : Level is DriftLevel.Drift or DriftLevel.Suspect ? SignalCount.ToString(CultureInfo.InvariantCulture) + " " + EvalText.Level(Level)
        : EvalText.Level(Level);

    /// <summary>The chip's tone.</summary>
    public string? Tone => Run.Active ? null : Run.Status != SuiteRunStatus.Complete ? EvalText.StatusTone(Run.Status) : EvalText.Tone(Level);

    /// <summary>"100 of 108 passed", or "64 of 108 answers" while it runs.</summary>
    public string Summary => Run.Active
        ? string.Create(CultureInfo.InvariantCulture, $"{Finished:N0} of {Run.Answers:N0} answers")
        : string.Create(CultureInfo.InvariantCulture, $"{Passed:N0} of {Run.Answers:N0} passed");
}

/// <summary>A suite's target as the suite card, the suite page and the drift page show it.</summary>
/// <param name="Target">The target.</param>
/// <param name="Model">Its model.</param>
/// <param name="Account">"Active account", or the account id it is pinned to.</param>
/// <param name="Billing">How it is billed, from the suite's estimate, or null when the estimate has no line for it.</param>
/// <param name="RunMicros">Its worst case per run, or null.</param>
/// <param name="Level">The worst of its open signals.</param>
/// <param name="Note">Its worst open signal's rule, with the change when there is one ("Latency +62%"), or null.</param>
public sealed record SuiteTargetView(SuiteTarget Target, EvalModelView Model, string Account, BillingKind? Billing, long? RunMicros, DriftLevel Level, string? Note);

// ----- one suite -----

/// <summary>GET /evals/suites/{id} (Suite.cshtml): <c>_SuiteHeader</c>, the running card, <c>_SuiteTargets</c>, <c>_SuiteCases</c>,
/// <c>_SuiteRuns</c>.</summary>
public sealed record SuitePage
{
    /// <summary>The suite's overview.</summary>
    public required SuiteOverview Overview { get; init; }

    /// <summary>Its targets.</summary>
    public IReadOnlyList<SuiteTargetView> Targets { get; init; } = [];

    /// <summary>Its cases, in order.</summary>
    public IReadOnlyList<SuiteCaseView> Cases { get; init; } = [];

    /// <summary>Its newest runs.</summary>
    public IReadOnlyList<SuiteRunRow> Runs { get; init; } = [];

    /// <summary>How many runs it has in all.</summary>
    public int RunTotal { get; init; }

    /// <summary>Its newest run, or null.</summary>
    public RunLine? LastRun { get; init; }

    /// <summary>The run still going, or null.</summary>
    public RunLine? Active { get; init; }

    /// <summary>"Run #12 · Oct 12" for the current baseline (the newest of its targets'), or null.</summary>
    public string? BaselineText { get; init; }

    /// <summary>The current baseline's run page, or null.</summary>
    public string? BaselineUrl { get; init; }

    /// <summary>The judge lines of the estimate ("The LLM judge on one case ... adds up to $0.0046 per run").</summary>
    public string? JudgeText { get; init; }

    /// <summary>Add target's Provider → Model selects.</summary>
    public required ProviderModelSelect AddTarget { get; init; }

    /// <summary>The suite.</summary>
    public Suite Suite => Overview.Suite;

    /// <summary>Whether another target fits (at most <see cref="Suite.MaxTargets"/>).</summary>
    public bool CanAddTarget => Suite.Targets.Count < Suite.MaxTargets;

    /// <summary>The page state: <c>running</c> or <c>default</c>.</summary>
    public string State => Active is null ? "default" : "running";

    /// <summary>The suite's targets as the JSON the suite API takes (for Add and Remove target).</summary>
    public string TargetsJson => EvalJson.Serialize(Suite.Targets);
}

/// <summary>A row of <c>_SuiteCases</c>.</summary>
/// <param name="Case">The case.</param>
/// <param name="Meta">"coding, regex · maxTokens 2,000".</param>
/// <param name="Latest">"9/9" for the newest run that asked it, "queued" or "running" while that run goes, or null.</param>
/// <param name="LatestTitle">What <paramref name="Latest"/> counts.</param>
/// <param name="Pending">Whether <paramref name="Latest"/> is a running run's state rather than a count.</param>
public sealed record SuiteCaseView(TestCase Case, string Meta, string? Latest, string? LatestTitle, bool Pending)
{
    /// <summary>The inspector page of the run it was pinned from (its reference run), or null.</summary>
    public string? ReferenceUrl => Case.Reference?.RunId is { Length: > 0 } run ? "/runs/" + Uri.EscapeDataString(run) : Case.Origin.RunId is { Length: > 0 } origin ? "/runs/" + Uri.EscapeDataString(origin) : null;
}

/// <summary>A row of <c>_SuiteRuns</c>.</summary>
/// <param name="Line">The run.</param>
/// <param name="Baseline">Whether it is a current baseline of some target.</param>
/// <param name="Note">Its worst signal's rule ("Response model changed"), or null.</param>
public sealed record SuiteRunRow(RunLine Line, bool Baseline, string? Note);

// ----- one run -----

/// <summary>GET /evals/runs/{id} (Run.cshtml) and its live fragment (<c>_RunLive</c>): <c>_EvalRunHeader</c>, the drift notices and
/// <c>_RunGrid</c>.</summary>
public sealed record RunPage
{
    /// <summary>The suite (as it is now).</summary>
    public required Suite Suite { get; init; }

    /// <summary>The run.</summary>
    public required SuiteRun Run { get; init; }

    /// <summary>The run's columns: its targets, with their totals and baselines.</summary>
    public IReadOnlyList<RunColumn> Columns { get; init; } = [];

    /// <summary>The grid's rows: the run's cases.</summary>
    public IReadOnlyList<RunGridRow> Rows { get; init; } = [];

    /// <summary>The signals this run raised (open or not), worst first.</summary>
    public IReadOnlyList<RunSignalView> Signals { get; init; } = [];

    /// <summary>Answers finished.</summary>
    public int Finished { get; init; }

    /// <summary>Answers passed.</summary>
    public int Passed { get; init; }

    /// <summary>"Run #12 · Oct 12" for the baseline this run was compared with, or null.</summary>
    public string? BaselineText { get; init; }

    /// <summary>That baseline's run page, or null.</summary>
    public string? BaselineUrl { get; init; }

    /// <summary>When it started (or was made), for the header.</summary>
    public required string Started { get; init; }

    /// <summary>"6 min 12 s" (Duration) or "3 min 41 s" (Elapsed while it runs).</summary>
    public string? Length { get; init; }

    /// <summary>The suite's cases now: a run of one case of several says so.</summary>
    public int SuiteCases { get; init; }

    /// <summary>Whether it is still going.</summary>
    public bool Running => Run.Active;

    /// <summary>"Run #21".</summary>
    public string Title => "Run #" + Run.Number.ToString(CultureInfo.InvariantCulture);

    /// <summary>Finished answers as a percentage, for the progress bar.</summary>
    public int Progress => Run.Answers == 0 ? 0 : (int)Math.Round(100.0 * Finished / Run.Answers);

    /// <summary>The page state: <c>running</c> or <c>default</c>.</summary>
    public string State => Running ? "running" : "default";
}

/// <summary>One column of the run grid: a target with its totals and its baseline's.</summary>
/// <param name="Target">The target.</param>
/// <param name="Model">Its model.</param>
/// <param name="Summary">Its totals in this run.</param>
/// <param name="Baseline">Its baseline run's totals, or null.</param>
/// <param name="BaselineModels">The response models its baseline recorded.</param>
public sealed record RunColumn(SuiteTarget Target, EvalModelView Model, TargetSummary Summary, TargetSummary? Baseline, IReadOnlyList<string> BaselineModels);

/// <summary>One row of the run grid: a case and its cell on each target.</summary>
/// <param name="CaseId">The case id.</param>
/// <param name="Name">The case's name (or its id when it was deleted since).</param>
/// <param name="CheckCount">How many checks it has now.</param>
/// <param name="Cells">Its cells, in column order.</param>
public sealed record RunGridRow(string CaseId, string Name, int CheckCount, IReadOnlyList<RunCellView> Cells);

/// <summary>One cell of the run grid: a case on a target over its k repeats.</summary>
/// <param name="Cell">The repeats and totals.</param>
/// <param name="Dots">One status-dot class per repeat (<c>ok</c>, <c>danger</c>, <c>warn</c>, <c>live</c>, <c>pending</c>).</param>
/// <param name="Url">The inspector page of its first answered repeat, or null.</param>
/// <param name="State">"done", "running" (some answered or streaming) or "queued".</param>
/// <param name="Note">"1 of 3 · 9.3 s", "streaming", "queued" while the run goes; null when done.</param>
/// <param name="Title">The hover text: "median 10.0 s per answer · 3 answers $0.0159".</param>
/// <param name="Label">The accessible name.</param>
public sealed record RunCellView(SuiteCell Cell, IReadOnlyList<string> Dots, string? Url, string State, string? Note, string? Title, string Label);

/// <summary>A drift signal as the run page tells it.</summary>
/// <param name="Signal">The signal.</param>
/// <param name="Model">The target's model.</param>
/// <param name="Confirmed">"Run #20 saw it first, and this run confirms it.", or null.</param>
public sealed record RunSignalView(DriftSignal Signal, EvalModelView Model, string? Confirmed);

// ----- drift -----

/// <summary>GET /evals/drift (Drift.cshtml): a <c>_DriftSuite</c> per suite Drift Watch follows, <c>_PriceChanges</c>, or
/// <c>_DriftEmpty</c>.</summary>
/// <param name="Suites">The suites with a baseline, an open signal or an active schedule.</param>
/// <param name="Prices">The catalog price changes of the last 30 days, newest first.</param>
public sealed record DriftPage(IReadOnlyList<DriftSuiteView> Suites, IReadOnlyList<PriceChangeView> Prices)
{
    /// <summary>Whether Drift Watch follows no suite yet.</summary>
    public bool Empty => Suites.Count == 0;

    /// <summary>The page state: <c>empty</c>, <c>paused</c> or <c>default</c>.</summary>
    public string State => Empty ? "empty" : Suites.Any(suite => suite.Paused is not null) ? "paused" : "default";
}

/// <summary>One <c>_DriftSuite</c>.</summary>
public sealed record DriftSuiteView
{
    /// <summary>The suite.</summary>
    public required Suite Suite { get; init; }

    /// <summary>"baseline run #12 (Oct 12)", or null.</summary>
    public string? BaselineText { get; init; }

    /// <summary>Its newest run, or null.</summary>
    public RunLine? Latest { get; init; }

    /// <summary>Its schedule's pause, or null.</summary>
    public SchedulePause? Paused { get; init; }

    /// <summary>"Monday, Oct 26 at 07:00" for its next slot, or null.</summary>
    public string? NextText { get; init; }

    /// <summary>Its targets.</summary>
    public IReadOnlyList<DriftTargetView> Targets { get; init; } = [];
}

/// <summary>One target row of <c>_DriftSuite</c>.</summary>
/// <param name="Target">The target.</param>
/// <param name="Model">Its model.</param>
/// <param name="Level">The worst of its open signals.</param>
/// <param name="Signals">Its open signals, worst first.</param>
/// <param name="Baseline">Its current baseline, or null.</param>
/// <param name="Text">The row's sentence when no signal is open.</param>
public sealed record DriftTargetView(SuiteTarget Target, EvalModelView Model, DriftLevel Level, IReadOnlyList<DriftSignalLine> Signals, Baseline? Baseline, string Text);

/// <summary>An open signal of a <c>_DriftSuite</c> row, with where it stands.</summary>
/// <param name="Signal">The signal.</param>
/// <param name="Lineage">"Run #20 saw it first and run #21 saw it again.", "Waiting for the next run to confirm it.", or empty
/// (<see cref="DriftText.Lineage"/>).</param>
public sealed record DriftSignalLine(DriftSignal Signal, string Lineage);

/// <summary>One row of <c>_PriceChanges</c>.</summary>
/// <param name="Row">The stored change.</param>
/// <param name="Detected">"today 06:12".</param>
/// <param name="Effects">What it does to each suite that uses the model.</param>
public sealed record PriceChangeView(PriceChangeRow Row, string Detected, IReadOnlyList<PriceEffect> Effects);

// ----- modals -----

/// <summary><c>_SuiteScheduleModal</c> (modal-suite-schedule) for one suite: the repeat panes, the worst and typical cost, and the cap with
/// its fits or refused pane.</summary>
public sealed record ScheduleModalView
{
    /// <summary>The suite.</summary>
    public required Suite Suite { get; init; }

    /// <summary>Its case count.</summary>
    public int Cases { get; init; }

    /// <summary>Its worst and typical cost per run.</summary>
    public required SuiteEstimate Estimate { get; init; }

    /// <summary>The schedule the panes start from: the saved one, or daily at 07:00.</summary>
    public required SuiteSchedule Schedule { get; init; }

    /// <summary>The cap the field starts with (the query's, the saved one, or for a suite without one the suggested cap,
    /// <see cref="SuiteService.SuggestedCap"/>), or none.</summary>
    public long? CapMicros { get; init; }

    /// <summary>Why a schedule with <see cref="CapMicros"/> would be refused, or null when it fits (or when no cap is in the field yet and
    /// nothing else refuses it: an empty field is refused only once the user types or saves).</summary>
    public ScheduleRefusal? Refusal { get; init; }

    /// <summary>The time zone the times are in (the saved one, or this computer's).</summary>
    public required string TimeZone { get; init; }

    /// <summary>"Local time on this computer." or the saved zone.</summary>
    public required string ZoneText { get; init; }

    /// <summary>Answers per run.</summary>
    public int AnswersPerRun => Suite.AnswersPerRun(Cases);

    /// <summary>The cap field's text: "2.00" (to the micro-dollar, so a saved cap reads back exactly), or empty.</summary>
    public string CapText => CapMicros is { } cap ? SpendMoney.ToUsd(cap).ToString("0.00####", CultureInfo.InvariantCulture) : "";

    /// <summary>Whether the refusal does not depend on the cap (no case or target, an unknown price): raising the cap does not help.</summary>
    public bool Blocked => Refusal?.Code is ScheduleRefusal.CannotRun or ScheduleRefusal.UnknownPrice;

    /// <summary>The pane shown first: <c>ss-refused</c> or <c>ss-fits</c>.</summary>
    public string CapPane => Refusal is null ? "ss-fits" : "ss-refused";

    /// <summary>The repeat pane shown first: <c>ss-hours</c>, <c>ss-daily</c> or <c>ss-weekly</c>.</summary>
    public string RepeatPane => Schedule.Repeat switch
    {
        ScheduleRepeat.Hours => "ss-hours",
        ScheduleRepeat.Weekly => "ss-weekly",
        _ => "ss-daily",
    };
}

/// <summary><c>_BaselineModal</c> (modal-baseline) for one suite: the complete runs to choose from and the targets to re-baseline.</summary>
public sealed record BaselineModalView
{
    /// <summary>The suite.</summary>
    public required Suite Suite { get; init; }

    /// <summary>Its complete runs, newest first.</summary>
    public IReadOnlyList<BaselineRunOption> Runs { get; init; } = [];

    /// <summary>The run selected first, or null when there is none to choose.</summary>
    public string? Selected { get; init; }

    /// <summary>Its targets, with their state.</summary>
    public IReadOnlyList<BaselineTargetOption> Targets { get; init; } = [];

    /// <summary>"the current baseline is run #12 (Oct 12)", or "no baseline yet".</summary>
    public required string CurrentText { get; init; }
}

/// <summary>A run option of the baseline modal: "Run #21 · Oct 21, 07:00 · 100 of 108 passed".</summary>
/// <param name="RunId">The run id.</param>
/// <param name="Text">The option's text.</param>
/// <param name="TargetIds">The targets it asked.</param>
public sealed record BaselineRunOption(string RunId, string Text, IReadOnlyList<string> TargetIds);

/// <summary>A target checkbox of the baseline modal.</summary>
/// <param name="Target">The target.</param>
/// <param name="Model">Its model.</param>
/// <param name="Note">"drift: ...", "info · ...", "ok", or "no baseline yet".</param>
/// <param name="Checked">Whether it starts checked (it drifted, was named, or has no baseline).</param>
public sealed record BaselineTargetOption(SuiteTarget Target, EvalModelView Model, string Note, bool Checked);

/// <summary><c>_CheckEditorModal</c> (modal-check-editor) for one check of one case (or a new one): a pane per check kind, the one being
/// edited filled in, and what it makes of the case's reference output.</summary>
public sealed record CheckEditorView
{
    /// <summary>The case.</summary>
    public required TestCase Case { get; init; }

    /// <summary>Its suite.</summary>
    public required Suite Suite { get; init; }

    /// <summary>The check being edited, or a new one of the chosen kind (no id).</summary>
    public required Check Check { get; init; }

    /// <summary>Whether it is a new check.</summary>
    public bool IsNew => Check.Id is null;

    /// <summary>The tools the case offers (for Calls a tool), and the check's own tool.</summary>
    public IReadOnlyList<string> Tools { get; init; } = [];

    /// <summary>The judge's Provider → Model selects.</summary>
    public required ProviderModelSelect Judge { get; init; }

    /// <summary>The check on the reference output, or null (no reference, a new check, or a judge).</summary>
    public CheckResult? Tried { get; init; }

    /// <summary>Why nothing was tried, or null.</summary>
    public string? TryNote { get; init; }

    /// <summary>The judge's same-family check against the suite's targets (a judge check only), or null.</summary>
    public JudgeFamilyCheck? Family { get; init; }

    /// <summary>The pane shown first: <c>ce-contains</c>, <c>ce-regex</c>, <c>ce-schema</c>, <c>ce-tool</c>, <c>ce-limits</c> or
    /// <c>ce-judge</c>.</summary>
    public string Pane => EvalPages.CheckPane(Check.Kind);
}
