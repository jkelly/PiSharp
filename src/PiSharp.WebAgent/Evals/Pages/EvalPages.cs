// Decision 0011 (run W5, E6): builds the Evals pages' view models (EvalViews) from the stores as they are now, so every number a page shows
// is a stored one: the leaderboard from ArenaService's voted battles through ArenaLeaderboards (an unvoted battle never reaches a page:
// the blind rule), suites, cases, runs, results, baselines and signals from EvalStore, worst and typical costs from SuiteService and the
// suite cost estimator, price changes and their effect on suites from Price Watch. A singleton (EvalsModule.AddPages); it reads services
// lazily, so a host without a unit's service still builds the pages that do not need it. The votes CSV (Export votes on the leaderboard) is
// written here too: voted battles only, every cell guarded against spreadsheet formulas.
using System.Globalization;
using System.Text;
using PiSharp.Web.Common;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Evals.Arena;
using PiSharp.WebAgent.Evals.Checks;
using PiSharp.WebAgent.Evals.Drift;
using PiSharp.WebAgent.Evals.Store;
using PiSharp.WebAgent.Evals.Suites;
using PiSharp.WebAgent.Shell;

namespace PiSharp.WebAgent.Evals.Pages;

/// <summary>The Evals pages' view models.</summary>
public sealed class EvalPages
{
    /// <summary>How many runs the suite page lists.</summary>
    public const int RecentRuns = 10;

    /// <summary>How many complete runs the baseline modal offers.</summary>
    public const int BaselineRuns = 20;

    private readonly IServiceProvider services;

    /// <summary>The pages over the app's services.</summary>
    public EvalPages(IServiceProvider services) => this.services = services ?? throw new ArgumentNullException(nameof(services));

    private SuiteService Suites => services.GetRequiredService<SuiteService>();

    private EvalStore Store => Suites.Store;

    private IModelCatalog? Catalog => services.GetService<IModelCatalog>();

    private DateTimeOffset Now => (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();

    /// <summary>The votes CSV of a leaderboard.</summary>
    public static string VotesCsvUrl(ArenaMode mode, string tag) =>
        EvalRoutes.Leaderboard + "/votes.csv?mode=" + EvalJson.Wire(mode) + "&tag=" + Uri.EscapeDataString(tag);

    /// <summary>The check editor's pane of <paramref name="kind"/>.</summary>
    public static string CheckPane(CheckKind kind) => kind switch
    {
        CheckKind.Contains => "ce-contains",
        CheckKind.Regex => "ce-regex",
        CheckKind.JsonSchema => "ce-schema",
        CheckKind.Tool => "ce-tool",
        CheckKind.Limits => "ce-limits",
        _ => "ce-judge",
    };

    /// <summary>A model as the pages name it, with its provider's logo.</summary>
    public EvalModelView ModelOf(ModelIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var provider = Catalog?.FindProvider(identity.Provider);
        var logo = provider?.LogoKey is { } key ? ProviderLogos.KeyUrl(key) : ProviderLogos.Url(identity.Provider);
        return new EvalModelView(identity, provider?.DisplayName ?? identity.Provider, logo);
    }

    // ----- leaderboard -----

    /// <summary>GET /evals/leaderboard: <paramref name="mode"/> (request unless agent) and <paramref name="tag"/> (the mode's first when
    /// not given or not voted; a tag given without a mode picks the mode that has it).</summary>
    public LeaderboardPage Leaderboard(string? mode, string? tag)
    {
        var arena = services.GetRequiredService<ArenaService>();
        var boards = services.GetRequiredService<ArenaLeaderboards>();
        var votes = arena.Voted();
        var tags = boards.Tags(votes);
        var given = EvalJson.FromWire<ArenaMode>(string.IsNullOrWhiteSpace(mode) ? null : mode.Trim().ToLowerInvariant());
        var which = given ?? ArenaMode.Request;
        var chosen = TagOrNull(tag);
        if (given is null && chosen is not null && tags.FirstOrDefault(item => item.Tag == chosen) is { } named) which = named.Mode;
        if (chosen is not null && !tags.Any(item => item.Mode == which && item.Tag == chosen)) chosen = null;
        chosen ??= tags.FirstOrDefault(item => item.Mode == which)?.Tag;
        if (chosen is null && given is null && tags.Count > 0) (which, chosen) = (tags[0].Mode, tags[0].Tag);
        var board = chosen is null ? null : boards.Build(which, chosen, votes);
        var rows = new List<LeaderboardRowView>();
        if (board is not null)
        {
            var track = board.Track;
            foreach (var row in board.Rows)
            {
                var low = track.Percent(row.Model.Low);
                var high = track.Percent(row.Model.High);
                rows.Add(new LeaderboardRowView(row.Model, row.Metrics, ModelOf(row.Model.Identity), low, Math.Max(0, high - low), track.Percent(row.Model.Rating)));
            }
        }
        string? position = null;
        if (board is { Mode: ArenaMode.Request, Position.LeftShare: { } share })
            position = $"You picked the left answer {EvalText.Percent(share)} of the time. Answers are shuffled into columns for every vote.";
        return new LeaderboardPage { Mode = which, Tag = chosen, Tags = tags, Board = board, Rows = rows, PositionText = position };
    }

    /// <summary>The votes of one leaderboard as CSV (UTF-8, one row per voted battle, oldest first): when, the mode and tag, the battle,
    /// the vote, and each column's model once revealed. Null when the mode or tag is not valid.</summary>
    public string? VotesCsv(string? mode, string? tag)
    {
        if (EvalJson.FromWire<ArenaMode>(string.IsNullOrWhiteSpace(mode) ? "request" : mode.Trim().ToLowerInvariant()) is not { } which) return null;
        if (TagOrNull(tag) is not { } chosen) return null;
        var csv = new StringBuilder();
        csv.Append("voted_at,mode,tag,battle,vote,best,ranking,A,B,C,D\r\n");
        foreach (var (battle, vote) in services.GetRequiredService<ArenaService>().Voted(which))
        {
            if (vote.Tag != chosen) continue;
            var cells = new List<string>
            {
                EvalText.Iso(vote.Created), EvalJson.Wire(which), vote.Tag, battle.Id, EvalJson.Wire(vote.Kind), vote.Best ?? "",
                vote.Ranking is { } ranking ? string.Join(" > ", ranking) : "",
            };
            foreach (var letter in ArenaLetters.All)
                cells.Add(battle.At(letter) is { } contestant ? Contestant(contestant.Identity) : "");
            csv.Append(string.Join(',', cells.Select(Cell))).Append("\r\n");
        }
        return csv.ToString();

        static string Contestant(ModelIdentity identity) =>
            identity.SettingsLabel == "default settings" ? identity.Name : identity.Name + " · " + identity.SettingsLabel;
    }

    private static string? TagOrNull(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        try { return EvalTags.Normalize(tag); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>One CSV cell: quoted when it holds a comma, quote or line break, and with a leading quote mark when a spreadsheet would read
    /// it as a formula.</summary>
    private static string Cell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    // ----- suites -----

    /// <summary>GET /evals/suites.</summary>
    public SuitesPage SuitesList() => new([.. Store.Suites().Select(Card)]);

    private SuiteCardView Card(Suite suite)
    {
        var overview = Suites.Overview(suite);
        return new SuiteCardView
        {
            Overview = overview,
            Targets = TargetsOf(suite, overview.Signals),
            NextText = overview.Next is { } next ? "next: " + EvalText.Next(next, Now) : null,
            LastRun = overview.LastRun is { } run ? Line(run) : null,
        };
    }

    /// <summary>A run in one line, with its pass counts and the signals it raised.</summary>
    public RunLine Line(SuiteRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var results = Store.Results(run.Id);
        var signals = Store.RunSignals(run.Id);
        var level = signals.Count == 0 ? DriftLevel.Ok : signals.Max(signal => signal.Level);
        return new RunLine(run, EvalText.When(run.Started ?? run.Created, Now), results.Count(result => result.Passed), results.Count(result => result.IsFinished),
            level, signals.Count(signal => signal.Level == level));
    }

    private List<SuiteTargetView> TargetsOf(Suite suite, IReadOnlyList<DriftSignal> open)
    {
        var cases = Store.Cases(suite.Id);
        var estimator = services.GetService<ISuiteCostEstimator>();
        var views = new List<SuiteTargetView>();
        foreach (var target in suite.Targets)
        {
            BillingKind? billing = null;
            long? micros = null;
            if (estimator is not null && cases.Count > 0)
            {
                // The target's own share of the worst case: the estimate of a suite with this target alone (judges left out).
                var lines = estimator.Estimate(suite with { Targets = [target] }, cases).Lines.Where(line => !line.Judge).ToList();
                if (lines.Count > 0)
                {
                    billing = lines[0].Billing;
                    micros = lines.Sum(line => line.RunMicros);
                }
            }
            var mine = open.Where(signal => signal.TargetId == target.Id && signal.Cleared is null).OrderByDescending(signal => signal.Level).ToList();
            var level = DriftRules.Worst(mine);
            string? note = mine.FirstOrDefault() is { } worst
                ? DriftRules.Label(worst.Rule) + (worst.Effect is { } effect && worst.Rule is not DriftRule.ResponseModel ? " " + DriftText.Percent(effect) : "")
                : null;
            views.Add(new SuiteTargetView(target, ModelOf(target.Identity), AccountText(target), billing, micros, level, note));
        }
        return views;
    }

    private static string AccountText(SuiteTarget target) => string.IsNullOrWhiteSpace(target.Account) ? "Active account" : target.Account.Trim();

    // ----- one suite -----

    /// <summary>GET /evals/suites/{id}, or null when there is no such suite.</summary>
    public SuitePage? SuiteOf(string id)
    {
        if (Store.Suite(id) is not { } suite) return null;
        var overview = Suites.Overview(suite);
        var cases = Store.Cases(suite.Id);
        var baselines = overview.Baselines;
        var latest = overview.LastRun;
        var latestResults = latest is null ? [] : Store.Results(latest.Id);
        var caseViews = cases.Select(testCase => CaseView(testCase, latest, latestResults)).ToList();
        var runs = Store.Runs(suite.Id, RecentRuns).Select(run =>
        {
            var line = Line(run);
            var note = Store.RunSignals(run.Id).OrderByDescending(signal => signal.Level).FirstOrDefault() is { Level: DriftLevel.Drift or DriftLevel.Suspect } worst
                ? DriftRules.Label(worst.Rule) + (worst.Rule == DriftRule.ResponseModel ? " changed" : "")
                : null;
            return new SuiteRunRow(line, baselines.Any(baseline => baseline.RunId == run.Id), note);
        }).ToList();
        var (baselineText, baselineUrl) = BaselineOf(baselines);
        return new SuitePage
        {
            Overview = overview,
            Targets = TargetsOf(suite, overview.Signals),
            Cases = caseViews,
            Runs = runs,
            RunTotal = Store.RunCount(suite.Id),
            LastRun = latest is null ? null : Line(latest),
            Active = overview.ActiveRun is { } active ? Line(active) : null,
            BaselineText = baselineText,
            BaselineUrl = baselineUrl,
            JudgeText = JudgeText(overview.Estimate, cases),
            AddTarget = new ProviderModelSelect("st") { Small = true, ProviderField = "provider", ModelField = "model" },
        };
    }

    private static SuiteCaseView CaseView(TestCase testCase, SuiteRun? latest, IReadOnlyList<SuiteResult> results)
    {
        var meta = new List<string>();
        if (testCase.Tags.Count > 0) meta.Add(string.Join(", ", testCase.Tags));
        if (testCase.Spec.MaxTokens is { } max) meta.Add("maxTokens " + max.ToString("N0", CultureInfo.InvariantCulture));
        if (meta.Count == 0) meta.Add("no tags");
        string? text = null, title = null;
        var pending = false;
        if (latest is not null && latest.CaseIds.Contains(testCase.Id))
        {
            var mine = results.Where(result => result.CaseId == testCase.Id).ToList();
            var total = latest.Targets.Count * latest.K;
            if (latest.Active && mine.Any(result => !result.IsFinished && result.Status != ResultStatus.Cancelled))
            {
                pending = true;
                text = mine.Any(result => result.Status == ResultStatus.Running || result.IsFinished) ? "running" : "queued";
            }
            else
            {
                text = $"{mine.Count(result => result.Passed).ToString(CultureInfo.InvariantCulture)}/{total.ToString(CultureInfo.InvariantCulture)}";
                title = $"Answers that passed every check in run #{latest.Number.ToString(CultureInfo.InvariantCulture)}, across " +
                        $"{EvalText.Plural(latest.Targets.Count, "target", "targets")} × k {latest.K.ToString(CultureInfo.InvariantCulture)}";
            }
        }
        return new SuiteCaseView(testCase, string.Join(" · ", meta), text, title, pending);
    }

    private (string? Text, string? Url) BaselineOf(IReadOnlyList<Baseline> baselines)
    {
        if (baselines.Count == 0) return (null, null);
        var runs = baselines.Select(baseline => baseline.RunId).Distinct(StringComparer.Ordinal)
            .Select(Store.Run).OfType<SuiteRun>().OrderByDescending(run => run.Number).ToList();
        if (runs.Count == 0) return (null, null);
        var newest = runs[0];
        var text = runs.Count == 1
            ? $"Run #{newest.Number.ToString(CultureInfo.InvariantCulture)} · {EvalText.Day(newest.Finished ?? newest.Created, Now)}"
            : "Runs " + DriftText.List([.. runs.Select(run => "#" + run.Number.ToString(CultureInfo.InvariantCulture))]);
        return (text, EvalRoutes.Run(newest.Id));
    }

    private static string? JudgeText(SuiteEstimate estimate, IReadOnlyList<TestCase> cases)
    {
        var judges = estimate.Lines.Where(line => line.Judge).ToList();
        if (judges.Count == 0) return null;
        var judged = cases.Count(testCase => testCase.Checks.Any(check => check.Kind == CheckKind.Judge));
        var names = DriftText.List([.. judges.Select(line => line.Provider + "/" + line.Model).Distinct(StringComparer.Ordinal)]);
        var who = $"The LLM judge on {EvalText.Plural(judged, "case", "cases")} ({names})";
        if (judges.Any(line => line.Billing == BillingKind.Unknown)) return who + " has no known price, so a scheduled run cannot start until it has one.";
        var metered = judges.Where(line => line.Billing == BillingKind.Metered).Sum(line => line.RunMicros);
        var list = judges.Where(line => line.Billing == BillingKind.Subscription).Sum(line => line.RunMicros);
        if (metered == 0 && list == 0) return who + " adds no metered cost.";
        return who + " adds up to " + (metered > 0 ? EvalText.Money(metered) : EvalText.ListMoney(list) + " at list price") + " per run.";
    }

    // ----- one run -----

    /// <summary>GET /evals/runs/{id}, or null when there is no such run (or its suite is gone).</summary>
    public RunPage? RunOf(string id)
    {
        if (Store.Run(id) is not { } run || Store.Suite(run.SuiteId) is not { } suite) return null;
        var now = Now;
        var results = Store.Results(run.Id);
        var cases = Store.Cases(suite.Id).ToDictionary(testCase => testCase.Id, StringComparer.Ordinal);
        var signals = Store.RunSignals(run.Id);
        var baselineResults = new Dictionary<string, IReadOnlyList<SuiteResult>>(StringComparer.Ordinal);
        string? baselineRunId = null;
        var columns = new List<RunColumn>();
        foreach (var target in run.Targets)
        {
            var summary = TargetSummary.Of(target.Id, run.CaseIds.Count * run.K, results);
            // The baseline this run was compared with: its signals say; otherwise the target's current one (unless that is this run).
            var compared = signals.FirstOrDefault(signal => signal.TargetId == target.Id)?.BaselineRunId
                ?? Store.CurrentBaseline(suite.Id, target.Id)?.RunId;
            TargetSummary? before = null;
            IReadOnlyList<string> models = [];
            if (compared is not null && compared != run.Id && Store.Run(compared) is { } baseRun)
            {
                if (!baselineResults.TryGetValue(compared, out var earlier)) baselineResults[compared] = earlier = Store.Results(compared);
                before = TargetSummary.Of(target.Id, baseRun.CaseIds.Count * baseRun.K, earlier);
                models = Store.Baselines(suite.Id, currentOnly: false).LastOrDefault(baseline => baseline.TargetId == target.Id && baseline.RunId == compared)?.ResponseModels
                    ?? before.ResponseModels;
                baselineRunId ??= compared;
            }
            columns.Add(new RunColumn(target, ModelOf(target.Identity), summary, before, models));
        }
        var rows = run.CaseIds.Select(caseId =>
        {
            var name = cases.TryGetValue(caseId, out var testCase) ? testCase.Name : caseId;
            var cells = columns.Select(column => CellView(run, name, column, SuiteCell.Of(caseId, column.Target.Id, results))).ToList();
            return new RunGridRow(caseId, name, testCase?.Checks.Count ?? 0, cells);
        }).ToList();
        var signalViews = signals.OrderByDescending(signal => signal.Level).ThenBy(signal => signal.Id).Select(signal =>
        {
            var target = run.Target(signal.TargetId) ?? suite.Target(signal.TargetId);
            var model = ModelOf(target?.Identity ?? new ModelIdentity("unknown", signal.TargetId));
            string? confirmed = null;
            if (signal.Level == DriftLevel.Drift && signal.FirstRunId is { } first && first != run.Id && Store.Run(first) is { } firstRun)
                confirmed = $"Run #{firstRun.Number.ToString(CultureInfo.InvariantCulture)} saw it first, and this run confirms it.";
            else if (signal.Level == DriftLevel.Suspect && signal.Cleared is null)
                confirmed = "The next run confirms it or clears it.";
            return new RunSignalView(signal, model, confirmed);
        }).ToList();
        string? baselineText = null, baselineUrl = null;
        if (baselineRunId is not null && Store.Run(baselineRunId) is { } baseline)
        {
            baselineText = $"Run #{baseline.Number.ToString(CultureInfo.InvariantCulture)} · {EvalText.Day(baseline.Finished ?? baseline.Created, now)}";
            baselineUrl = EvalRoutes.Run(baseline.Id);
        }
        var start = run.Started ?? run.Created;
        string? length = run.Active
            ? EvalText.Elapsed(now - start)
            : run.Finished is { } end ? EvalText.Elapsed(end - start) : null;
        return new RunPage
        {
            Suite = suite,
            Run = run,
            Columns = columns,
            Rows = rows,
            Signals = signalViews,
            Finished = results.Count(result => result.IsFinished),
            Passed = results.Count(result => result.Passed),
            BaselineText = baselineText,
            BaselineUrl = baselineUrl,
            Started = EvalText.Stamp(start),
            Length = length,
            SuiteCases = Store.CaseCount(suite.Id),
        };
    }

    private static RunCellView CellView(SuiteRun run, string caseName, RunColumn column, SuiteCell cell)
    {
        var dots = new List<string>();
        foreach (var result in cell.Repeats)
        {
            dots.Add(result.Status switch
            {
                ResultStatus.Done when result.Passed => "ok",
                ResultStatus.Done when result.Verdict == CheckVerdict.Review => "warn",
                ResultStatus.Done or ResultStatus.Error => "danger",
                ResultStatus.Running => "live",
                _ => "pending",
            });
        }
        while (dots.Count < run.K) dots.Add("pending");
        var finished = cell.Repeats.Count(result => result.IsFinished);
        var answered = cell.Repeats.FirstOrDefault(result => result.IsFinished && !string.IsNullOrEmpty(result.CallRunId))
            ?? cell.Repeats.FirstOrDefault(result => !string.IsNullOrEmpty(result.CallRunId));
        var url = answered?.CallRunId is { } call ? "/runs/" + Uri.EscapeDataString(call) : null;
        var who = caseName + " on " + column.Model.Name;
        var money = cell.ListPrice ? EvalText.ListMoney(cell.CostMicros) + " list" : EvalText.Money(cell.CostMicros);
        var spokenMoney = cell.ListPrice ? EvalText.ListMoney(cell.CostMicros) + " at list price" : EvalText.Money(cell.CostMicros);
        var answers = EvalText.Plural(run.K, "answer", "answers");
        if (finished >= run.K)
        {
            var latency = cell.MedianLatencyMs is { } ms ? "median " + EvalText.Duration(ms) + " per answer · " : "";
            var score = cell.Score is { } value ? ", score " + EvalText.Rate(value) : "";
            return new RunCellView(cell, dots, url, "done", null, latency + answers + " " + money,
                $"{who}: {cell.Passed.ToString(CultureInfo.InvariantCulture)} of {run.K.ToString(CultureInfo.InvariantCulture)} passed{score}, " +
                (cell.MedianLatencyMs is { } spoken ? "median " + EvalText.Duration(spoken) + " per answer, " : "") + answers + " " + spokenMoney);
        }
        string note;
        string state;
        if (!run.Active)
        {
            state = "done";
            note = finished == 0 ? "not asked" : $"{finished.ToString(CultureInfo.InvariantCulture)} of {run.K.ToString(CultureInfo.InvariantCulture)} asked";
        }
        else if (finished > 0)
        {
            state = "running";
            note = $"{finished.ToString(CultureInfo.InvariantCulture)} of {run.K.ToString(CultureInfo.InvariantCulture)}" +
                   (cell.MedianLatencyMs is { } ms ? " · " + EvalText.Duration(ms) : "");
        }
        else if (cell.Repeats.Any(result => result.Status == ResultStatus.Running))
        {
            state = "running";
            note = "streaming";
        }
        else
        {
            state = "queued";
            note = "queued";
        }
        return new RunCellView(cell, dots, url, state, note, null,
            $"{who}: {finished.ToString(CultureInfo.InvariantCulture)} of {run.K.ToString(CultureInfo.InvariantCulture)} finished");
    }

    // ----- drift -----

    /// <summary>GET /evals/drift.</summary>
    public DriftPage DriftOf()
    {
        var now = Now;
        var all = Store.Suites();
        var views = new List<DriftSuiteView>();
        foreach (var suite in all)
        {
            var baselines = Store.Baselines(suite.Id);
            var signals = Store.Signals(suite.Id);
            if (baselines.Count == 0 && signals.Count == 0 && suite.Schedule is not { Enabled: true }) continue;
            var latest = Store.Runs(suite.Id, 1).FirstOrDefault();
            var latestComplete = latest is { Status: SuiteRunStatus.Complete } ? latest : Store.Runs(suite.Id, 20).FirstOrDefault(run => run.Status == SuiteRunStatus.Complete);
            var latestResults = latestComplete is null ? [] : Store.Results(latestComplete.Id);
            DateTimeOffset? next = null;
            if (suite.Schedule is { Active: true } schedule)
            {
                try { next = schedule.Next(now); }
                catch (InvalidOperationException) { }
            }
            var baselineResults = new Dictionary<string, IReadOnlyList<SuiteResult>>(StringComparer.Ordinal);
            var targets = new List<DriftTargetView>();
            foreach (var target in suite.Targets)
            {
                var mine = signals.Where(signal => signal.TargetId == target.Id).OrderByDescending(signal => signal.Level).ThenBy(signal => signal.Id).ToList();
                var baseline = baselines.FirstOrDefault(item => item.TargetId == target.Id);
                string text;
                if (baseline is null) text = "No baseline yet. Set one from a complete run, and later runs are compared with it.";
                else if (latestComplete is null || latestComplete.Id == baseline.RunId || latestComplete.Target(target.Id) is null) text = "No run since the baseline yet.";
                else
                {
                    TargetSummary? was = null;
                    if (Store.Run(baseline.RunId) is { } baseRun)
                    {
                        if (!baselineResults.TryGetValue(baseRun.Id, out var earlier)) baselineResults[baseRun.Id] = earlier = Store.Results(baseRun.Id);
                        was = TargetSummary.Of(target.Id, baseRun.CaseIds.Count * baseRun.K, earlier);
                    }
                    var after = TargetSummary.Of(target.Id, latestComplete.CaseIds.Count * latestComplete.K, latestResults);
                    text = "No changes against the baseline." + (was?.PassRate is { } a && after.PassRate is { } b ? $" Pass rate {EvalText.Rate(a)} → {EvalText.Rate(b)}." : "");
                }
                var lines = mine.Select(signal => new DriftSignalLine(signal, DriftText.Lineage(signal,
                    signal.FirstRunId is { } first ? Store.Run(first)?.Number : null, Store.Run(signal.RunId)?.Number))).ToList();
                targets.Add(new DriftTargetView(target, ModelOf(target.Identity), DriftRules.Worst(mine), lines, baseline, text));
            }
            var baseRuns = baselines.Select(baseline => baseline.RunId).Distinct(StringComparer.Ordinal).Select(Store.Run).OfType<SuiteRun>()
                .OrderByDescending(run => run.Number).ToList();
            string? baselineText = baseRuns.Count switch
            {
                0 => null,
                1 => $"baseline run #{baseRuns[0].Number.ToString(CultureInfo.InvariantCulture)} ({EvalText.Day(baseRuns[0].Finished ?? baseRuns[0].Created, now)})",
                _ => "baselines " + DriftText.List([.. baseRuns.Select(run => "#" + run.Number.ToString(CultureInfo.InvariantCulture))]),
            };
            views.Add(new DriftSuiteView
            {
                Suite = suite,
                BaselineText = baselineText,
                Latest = latest is null ? null : Line(latest),
                Paused = suite.Schedule is { Enabled: true, Paused: { } pause } ? pause : null,
                NextText = next is { } at ? EvalText.Next(at, now) : null,
                Targets = targets,
            });
        }
        var prices = new List<PriceChangeView>();
        if (services.GetService<PriceWatch>() is { } watch)
        {
            var suiteCases = all.Select(suite => new SuiteCases(suite, Store.Cases(suite.Id))).ToList();
            foreach (var row in watch.Changes().OrderByDescending(row => row.Change.Detected).ThenByDescending(row => row.Id))
            {
                IReadOnlyList<PriceEffect> effects;
                try { effects = watch.Effects(row, suiteCases); }
                catch (InvalidOperationException) { effects = []; }
                prices.Add(new PriceChangeView(row, EvalText.When(row.Change.Detected, now) + " · " + row.Change.Provider + " catalog changed", effects));
            }
        }
        return new DriftPage(views, prices);
    }

    // ----- modals -----

    /// <summary><c>_SuiteScheduleModal</c> for <paramref name="suiteId"/>, with the cap field at <paramref name="capMicros"/> (the saved cap
    /// when null, or for a suite that has none, SuiteService.SuggestedCap: the dialog opens on the fits pane, as the mockup does, not with a
    /// refusal before anything was typed); null when there is no such suite.</summary>
    public ScheduleModalView? ScheduleOf(string suiteId, long? capMicros = null)
    {
        if (Store.Suite(suiteId) is not { } suite) return null;
        var cases = Store.Cases(suite.Id);
        var estimate = Suites.Estimate(suite, cases);
        var cap = capMicros ?? suite.CapMicros ?? SuiteService.SuggestedCap(estimate);
        var refusal = SuiteService.Refusal(suite, cases.Count, estimate, cap);
        // Opened with no cap to show (none saved, none to suggest, nothing typed yet): an empty field is not an error until the user types
        // or saves (evals.js and the server refuse it then). A refusal no cap can lift still shows.
        if (refusal?.Code == ScheduleRefusal.NoCap && capMicros is null) refusal = null;
        var schedule = suite.Schedule ?? new SuiteSchedule { Repeat = ScheduleRepeat.Daily, At = "07:00" };
        var local = TimeZoneInfo.Local.Id;
        var zone = schedule.TimeZone ?? local;
        return new ScheduleModalView
        {
            Suite = suite,
            Cases = cases.Count,
            Estimate = estimate,
            Schedule = schedule,
            CapMicros = cap,
            Refusal = refusal,
            TimeZone = zone,
            ZoneText = string.Equals(zone, local, StringComparison.OrdinalIgnoreCase) ? "Local time on this computer." : $"Times are in {zone}.",
        };
    }

    /// <summary><c>_BaselineModal</c> for <paramref name="suiteId"/>, with <paramref name="runId"/> selected (the newest complete run when
    /// null or not one) and only <paramref name="targetId"/> checked when given; null when there is no such suite.</summary>
    public BaselineModalView? BaselineOf(string suiteId, string? runId = null, string? targetId = null)
    {
        if (Store.Suite(suiteId) is not { } suite) return null;
        var now = Now;
        var baselines = Store.Baselines(suite.Id);
        var signals = Store.Signals(suite.Id);
        var runs = Store.Runs(suite.Id, BaselineRuns * 2).Where(run => run.Status == SuiteRunStatus.Complete).Take(BaselineRuns).ToList();
        var options = runs.Select(run =>
        {
            var results = Store.Results(run.Id);
            var current = run.Targets.Count > 0 && run.Targets.All(target => baselines.Any(baseline => baseline.TargetId == target.Id && baseline.RunId == run.Id));
            var text = $"Run #{run.Number.ToString(CultureInfo.InvariantCulture)} · {EvalText.When(run.Finished ?? run.Created, now)} · " +
                       (current ? "current baseline" : results.Count(result => result.Passed).ToString("N0", CultureInfo.InvariantCulture) + " of " +
                        run.Answers.ToString("N0", CultureInfo.InvariantCulture) + " passed");
            return new BaselineRunOption(run.Id, text, [.. run.Targets.Select(target => target.Id)]);
        }).ToList();
        var selected = options.FirstOrDefault(option => option.RunId == runId)?.RunId ?? options.FirstOrDefault()?.RunId;
        var targets = suite.Targets.Select(target =>
        {
            var worst = signals.Where(signal => signal.TargetId == target.Id).OrderByDescending(signal => signal.Level).FirstOrDefault();
            var baseline = baselines.FirstOrDefault(item => item.TargetId == target.Id);
            var note = worst is not null ? EvalText.Level(worst.Level) + ": " + worst.Message : baseline is null ? "no baseline yet" : "ok";
            var check = targetId is not null ? target.Id == targetId : worst?.Level is DriftLevel.Drift or DriftLevel.Suspect || baseline is null;
            return new BaselineTargetOption(target, ModelOf(target.Identity), note, check);
        }).ToList();
        if (targetId is null && !targets.Any(target => target.Checked)) targets = [.. targets.Select(target => target with { Checked = true })];
        var baseRuns = baselines.Select(baseline => baseline.RunId).Distinct(StringComparer.Ordinal).Select(Store.Run).OfType<SuiteRun>()
            .OrderByDescending(run => run.Number).ToList();
        var currentText = baseRuns.Count switch
        {
            0 => "no baseline yet",
            1 => $"the current baseline is run #{baseRuns[0].Number.ToString(CultureInfo.InvariantCulture)} ({EvalText.Day(baseRuns[0].Finished ?? baseRuns[0].Created, now)})",
            _ => "the current baselines are runs " + DriftText.List([.. baseRuns.Select(run => "#" + run.Number.ToString(CultureInfo.InvariantCulture))]),
        };
        return new BaselineModalView { Suite = suite, Runs = options, Selected = selected, Targets = targets, CurrentText = currentText };
    }

    /// <summary><c>_CheckEditorModal</c> for the check <paramref name="checkId"/> of the case <paramref name="caseId"/>, or a new check of
    /// <paramref name="kind"/> (contains when not given) when <paramref name="checkId"/> is null; null when there is no such case or check.</summary>
    public CheckEditorView? CheckEditorOf(string caseId, string? checkId = null, string? kind = null)
    {
        if (Store.Case(caseId) is not { } testCase || Store.Suite(testCase.SuiteId) is not { } suite) return null;
        Check check;
        if (!string.IsNullOrWhiteSpace(checkId))
        {
            if (testCase.Checks.FirstOrDefault(item => item.Id == checkId) is not { } found) return null;
            check = found;
        }
        else check = new Check { Kind = EvalJson.FromWire<CheckKind>(kind?.Trim().ToLowerInvariant()) ?? CheckKind.Contains };
        var tools = (testCase.Spec.Tools ?? []).Select(tool => tool.Name)
            .Concat(check.Tool is { Length: > 0 } own ? [own] : []).Distinct(StringComparer.Ordinal).ToList();
        CheckResult? tried = null;
        string? note = null;
        if (check.Kind == CheckKind.Judge) note = "An LLM judge runs only in suite runs, where each judgement is a metered call; it is not tried here.";
        else if (testCase.Reference is not { } reference) note = "This case has no reference output to try the check on.";
        else if (check.Id is not null)
        {
            try { tried = CheckRunner.Evaluate(check, reference.ToAnswer(), CheckSubject.Reference); }
            catch (ArgumentException error) { note = error.Message; }
        }
        JudgeFamilyCheck? family = null;
        if (check.Judge is { } judge) family = RubricJudge.Family(judge, suite.Targets.Select(target => target.Identity));
        return new CheckEditorView
        {
            Case = testCase,
            Suite = suite,
            Check = check,
            Tools = tools,
            Judge = new ProviderModelSelect("cej") { Provider = check.Judge?.Provider, Model = check.Judge?.Model, ProviderField = "judgeProvider", ModelField = "judgeModel" },
            Tried = tried,
            TryNote = note,
            Family = family,
        };
    }
}
