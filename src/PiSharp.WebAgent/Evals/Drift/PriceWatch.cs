// Decision 0011 (run W5, E3): Price Watch (plan "Price Watch": catalog prices are snapshotted whenever a provider's catalog hash changes;
// the drift page lists the changes of the last 30 days with their effect on each suite, and a schedule whose worst case no longer fits its
// cap is paused). Check reads every provider's current prices (IPriceSource) and saves a snapshot only for a hash it has not seen last for
// that provider (IPriceSnapshotStore.Record is atomic), with the rates that moved; it runs when the app starts and every 10 minutes after
// (PriceWatchService), and raises Changed when a check saw a price move. Price Watch never pauses anything itself: the Suites unit (E4) owns
// the schedules, so SchedulesToPause tells it which active schedules to pause and why (SchedulePause over-cap or unknown-price, with the
// mockup's reason), and E4 saves the pause. The effect of one change on a suite is worked out in isolation: the suite's worst case with the
// changed model at the new snapshot's price, against the same with only the changed rate put back, every other model at the catalog's
// current price. A change that leaves the worst case alone says why when it can (cache reads are never reserved; mockup "worst case
// unchanged, because cache reads aren't reserved. Typical cost may drop slightly.").
using PiSharp.WebAgent.Contracts;

namespace PiSharp.WebAgent.Evals.Drift;

/// <summary>What one Price Watch check saw.</summary>
/// <param name="At">When it ran.</param>
/// <param name="Saved">The snapshots it saved (a provider whose hash was new), with their changes.</param>
public sealed record PriceCheck(DateTimeOffset At, IReadOnlyList<PriceRecorded> Saved)
{
    /// <summary>Every rate that moved, by provider.</summary>
    public IReadOnlyList<PriceChangeRow> Changes => [.. Saved.SelectMany(saved => saved.Changes)];
}

/// <summary>A schedule Price Watch says to pause: an active schedule whose suite's worst case no longer fits its cap, or that has a target or
/// judge without a known price.</summary>
/// <param name="SuiteId">The suite.</param>
/// <param name="SuiteName">Its name.</param>
/// <param name="Pause">The pause to save on its schedule (<see cref="SuiteSchedule.Paused"/>).</param>
/// <param name="Estimate">Its worst case now.</param>
public sealed record SchedulePauseNotice(string SuiteId, string SuiteName, SchedulePause Pause, SuiteEstimate Estimate);

/// <summary>What one price change does to one suite that uses the model (mockup _PriceChanges "Effect on suites").</summary>
/// <param name="SuiteId">The suite.</param>
/// <param name="SuiteName">Its name.</param>
/// <param name="WorstBeforeMicros">Its worst case with the old rate, or null when that had no known price.</param>
/// <param name="WorstAfterMicros">Its worst case with the new rate, or null when it has no known price now.</param>
/// <param name="CapMicros">Its suite-run cap, or null.</param>
/// <param name="Text">What the page says: <c>JSON extraction: worst $0.49 → $0.54, above its $0.50 cap.</c></param>
public sealed record PriceEffect(string SuiteId, string SuiteName, long? WorstBeforeMicros, long? WorstAfterMicros, long? CapMicros, string Text)
{
    /// <summary>Whether its worst case is now above its cap (or unknown).</summary>
    public bool OverCap => WorstAfterMicros is null || (CapMicros is { } cap && WorstAfterMicros > cap);

    /// <summary>Whether its schedule is on and so is (or will be) paused by this change: <see cref="OverCap"/> with an enabled
    /// schedule.</summary>
    public bool PausesSchedule { get; init; }
}

/// <summary>Price Watch: snapshots of the catalog's prices, the changes between them, and what they do to suites. A singleton; safe to call
/// from many threads.</summary>
public sealed class PriceWatch
{
    /// <summary>How far back the drift page lists price changes.</summary>
    public static TimeSpan ChangeWindow { get; } = TimeSpan.FromDays(30);

    private readonly IPriceSource source;
    private readonly IPriceSnapshotStore store;
    private readonly ISuiteCostEstimator? estimator;
    private readonly TimeProvider time;
    private readonly ILogger<PriceWatch>? log;
    private readonly Lock gate = new();
    private PriceCheck? last;

    /// <summary>A price watch over <paramref name="source"/>, saving to <paramref name="store"/>, with <paramref name="estimator"/> for the
    /// effect on suites (null: <see cref="SchedulesToPause"/> and <see cref="Effects"/> cannot be used).</summary>
    public PriceWatch(IPriceSource source, IPriceSnapshotStore store, ISuiteCostEstimator? estimator = null, TimeProvider? time = null, ILogger<PriceWatch>? log = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(store);
        this.source = source;
        this.store = store;
        this.estimator = estimator;
        this.time = time ?? TimeProvider.System;
        this.log = log;
    }

    /// <summary>Raised after a check that saw a rate move (not for a provider's first snapshot). Handlers run on the checking thread; one
    /// that throws is logged and the rest still run.</summary>
    public event Action<PriceCheck>? Changed;

    /// <summary>The last check this process ran, or null before the first.</summary>
    public PriceCheck? LastCheck
    {
        get { lock (gate) return last; }
    }

    /// <summary>Reads every provider's current prices and saves a snapshot for each whose hash differs from its latest one.</summary>
    public PriceCheck Check()
    {
        PriceCheck check;
        lock (gate)
        {
            var now = time.GetUtcNow();
            var saved = new List<PriceRecorded>();
            foreach (var snapshot in source.Snapshots(now))
                if (store.Record(snapshot) is { } recorded) saved.Add(recorded);
            check = new PriceCheck(now, saved);
            last = check;
        }
        foreach (var recorded in check.Saved)
            log?.LogInformation("Price Watch saved a {Provider} price snapshot ({Hash}) with {Changes} price changes.", recorded.Snapshot.Provider,
                recorded.Snapshot.CatalogSha256[..Math.Min(12, recorded.Snapshot.CatalogSha256.Length)], recorded.Changes.Count);
        if (check.Changes.Count > 0 && Changed is { } handlers)
            foreach (var handler in handlers.GetInvocationList().Cast<Action<PriceCheck>>())
            {
                try { handler(check); }
                catch (Exception error) when (error is not OutOfMemoryException) { log?.LogWarning(error, "A Price Watch change handler failed."); }
            }
        return check;
    }

    /// <summary>The changes detected in the last <paramref name="window"/> (default <see cref="ChangeWindow"/>), newest first.</summary>
    public IReadOnlyList<PriceChangeRow> Changes(TimeSpan? window = null) => store.Changes(time.GetUtcNow() - (window ?? ChangeWindow));

    /// <summary>What <paramref name="model"/> of <paramref name="provider"/> cost at <paramref name="at"/> by the snapshots (the latest taken
    /// at or before it, else the earliest), or null when no snapshot lists it. <see cref="ModelPrice.Cost"/> is null when it had no price.</summary>
    public ModelPrice? PriceAt(string provider, string model, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);
        return store.At(provider, at)?.Price(model);
    }

    /// <summary>The active schedules (enabled, not paused) to pause now: those whose worst case is above their suite-run cap
    /// (<see cref="SchedulePause.OverCap"/>) or that have a target or judge without a known price (<see cref="SchedulePause.UnknownPrice"/>).
    /// <paramref name="changes"/> (default: the last check's) says whether a price change caused it, for the reason.</summary>
    public IReadOnlyList<SchedulePauseNotice> SchedulesToPause(IEnumerable<SuiteCases> suites, DateTimeOffset? now = null, IReadOnlyList<PriceChangeRow>? changes = null)
    {
        ArgumentNullException.ThrowIfNull(suites);
        var costs = estimator ?? throw new InvalidOperationException("Price Watch has no suite cost estimator.");
        var at = now ?? time.GetUtcNow();
        changes ??= LastCheck?.Changes ?? [];
        var notices = new List<SchedulePauseNotice>();
        foreach (var (suite, cases) in suites)
        {
            if (suite.Schedule is not { Active: true }) continue;
            var estimate = costs.Estimate(suite, cases);
            if (estimate.HasUnknownPrice)
            {
                var unknown = estimate.Lines.Where(line => line.Billing == BillingKind.Unknown).Select(line => line.Provider + "/" + line.Model)
                    .Distinct(StringComparer.Ordinal).ToList();
                notices.Add(new SchedulePauseNotice(suite.Id, suite.Name, new SchedulePause(SchedulePause.UnknownPrice,
                    $"{DriftText.List(unknown)} {(unknown.Count == 1 ? "has" : "have")} no known price, and a scheduled run cannot ask.", at), estimate));
                continue;
            }
            if (suite.CapMicros is not { } cap || estimate.Fits(cap)) continue;
            var priced = changes.Any(row => row.Change.Rise && Uses(suite, cases, row.Change.Provider, row.Change.Model));
            var reason = priced
                ? $"A catalog price change raised its worst case to {DriftText.Money(estimate.WorstMicros)}, above its {DriftText.Money(cap)} suite-run cap."
                : $"Its worst case of {DriftText.Money(estimate.WorstMicros)} is above its {DriftText.Money(cap)} suite-run cap.";
            notices.Add(new SchedulePauseNotice(suite.Id, suite.Name, new SchedulePause(SchedulePause.OverCap, reason, at), estimate));
        }
        return notices;
    }

    /// <summary>What <paramref name="row"/> does to each of <paramref name="suites"/> that uses its model (as a target or a judge), in the
    /// order given; empty when none does.</summary>
    public IReadOnlyList<PriceEffect> Effects(PriceChangeRow row, IEnumerable<SuiteCases> suites)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(suites);
        var costs = estimator ?? throw new InvalidOperationException("Price Watch has no suite cost estimator.");
        var change = row.Change;
        var after = store.Get(row.SnapshotId)?.Price(change.Model)?.Cost ?? Rebuilt(change);
        var before = With(after, change.Field, change.Before);
        var key = SuiteCostEstimator.PriceKey(change.Provider, change.Model);
        var afterPrices = new Dictionary<string, CatalogCost?>(StringComparer.Ordinal) { [key] = after };
        var beforePrices = new Dictionary<string, CatalogCost?>(StringComparer.Ordinal) { [key] = before };
        var effects = new List<PriceEffect>();
        foreach (var (suite, cases) in suites)
        {
            if (!Uses(suite, cases, change.Provider, change.Model)) continue;
            var was = costs.Estimate(suite, cases, beforePrices);
            var now = costs.Estimate(suite, cases, afterPrices);
            long? worstBefore = was.HasUnknownPrice ? null : was.WorstMicros;
            long? worstAfter = now.HasUnknownPrice ? null : now.WorstMicros;
            var effect = new PriceEffect(suite.Id, suite.Name, worstBefore, worstAfter, suite.CapMicros, EffectText(suite, change, worstBefore, worstAfter));
            effects.Add(effect with { PausesSchedule = effect.OverCap && suite.Schedule is { Enabled: true } });
        }
        return effects;
    }

    /// <summary>Whether <paramref name="suite"/> sends to <paramref name="model"/> of <paramref name="provider"/>: as a target, or as the
    /// judge of one of its cases' checks.</summary>
    public static bool Uses(Suite suite, IReadOnlyList<TestCase> cases, string provider, string model)
    {
        ArgumentNullException.ThrowIfNull(suite);
        ArgumentNullException.ThrowIfNull(cases);
        return suite.Targets.Any(target => Same(target.Provider, provider) && Same(target.Model, model))
            || cases.Any(testCase => testCase.Checks.Any(check => check.Judge is { } judge && Same(judge.Provider, provider) && Same(judge.Model, model)));
    }

    private static bool Same(string? first, string second) => string.Equals(first?.Trim(), second, StringComparison.Ordinal);

    private static string EffectText(Suite suite, PriceChange change, long? before, long? after)
    {
        var name = suite.Name;
        if (after is null) return $"{name}: no known price now, so a scheduled run cannot start.";
        if (before is null) return $"{name}: worst {DriftText.Money(after.Value)} now that it has a price{Cap(suite, after.Value)}";
        if (before == after)
        {
            if (change.Field == PriceField.CacheRead)
                return $"{name}: worst case unchanged, because cache reads aren't reserved. Typical cost may {(change.Rise ? "rise" : "drop")} slightly.";
            if (change.Field == PriceField.CacheWrite || change.Field == PriceField.Input)
                return $"{name}: worst case unchanged, because the higher of the input and cache-write rates is reserved.";
            return $"{name}: worst case unchanged.";
        }
        return $"{name}: worst {DriftText.Money(before.Value)} → {DriftText.Money(after.Value)}{Cap(suite, after.Value)}";
    }

    private static string Cap(Suite suite, long worst) => suite.CapMicros is { } cap
        ? worst > cap ? $", above its {DriftText.Money(cap)} cap." : $", within its {DriftText.Money(cap)} cap."
        : ".";

    /// <summary>A cost with only the changed rate known (the snapshot cannot be read): the other rates are left at $0.</summary>
    private static CatalogCost? Rebuilt(PriceChange change) => change.After is null ? null : With(new CatalogCost(0, 0, 0, 0), change.Field, change.After);

    private static CatalogCost? With(CatalogCost? cost, PriceField field, double? rate)
    {
        if (rate is null) return null;
        var basis = cost ?? new CatalogCost(0, 0, 0, 0);
        return field switch
        {
            PriceField.Input => basis with { Input = rate.Value },
            PriceField.Output => basis with { Output = rate.Value },
            PriceField.CacheRead => basis with { CacheRead = rate.Value },
            _ => basis with { CacheWrite = rate.Value },
        };
    }
}

/// <summary>Runs <see cref="PriceWatch.Check"/> when the app starts and every <see cref="Interval"/> after. A check that fails is logged and
/// the next one runs on time.</summary>
public sealed class PriceWatchService(PriceWatch watch, TimeProvider? time = null, ILogger<PriceWatchService>? log = null) : BackgroundService
{
    /// <summary>How often prices are checked: every 10 minutes, so an edited models.json is noticed soon (a new app version is noticed at
    /// start).</summary>
    public static TimeSpan Interval { get; } = TimeSpan.FromMinutes(10);

    private readonly TaskCompletionSource firstCheck = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the first check has run (or failed).</summary>
    public Task FirstCheck => firstCheck.Task;

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var clock = time ?? TimeProvider.System;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { watch.Check(); }
            catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
            {
                log?.LogWarning(error, "Price Watch could not check catalog prices; it tries again in {Interval}.", Interval);
            }
            finally { firstCheck.TrySetResult(); }
            try { await Task.Delay(Interval, clock, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
