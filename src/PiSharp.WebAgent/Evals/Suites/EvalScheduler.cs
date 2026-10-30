// Decision 0011 (run W5, E4): EvalScheduler, decision 0009's slot rules for suite schedules (mockup modal-suite-schedule: "Missed runs, for
// example while this computer was asleep, collapse into one. A run is skipped while the previous one is still going. EvalScheduler follows
// decision 0009's slot rules: one run per (schedule, slot).").
//
// Every 30 seconds (and once when the app starts), after marking runs a crash left behind interrupted (SuiteRunner.RecoverAsync, retried
// on each tick until it has marked them all), each enabled, unpaused schedule is
// looked at once: only its latest slot that counts (SuiteSchedule.Latest: after EnabledAt) is considered, so slots missed while the app was
// stopped collapse into that one, and a slot already decided is never decided again (one row per suite and slot in eval_schedule_slots;
// a started slot's row is written with its run, in one transaction). For an undecided slot:
//   - a run of the suite still going: skipped-running;
//   - a target or judge without a known price: the schedule is paused (unknown-price) and the slot skipped-unknown-price;
//   - the worst case above the suite-run cap: the schedule is paused (over-cap) and the slot skipped-over-cap;
//   - else the run starts (started); the Spend Guard refusing its reservation (another cap, an allowance gate) skips the slot
//     (skipped-refused, "listed in Spend" by the guard's stopped rows). The model and account are never switched.
// A price change Price Watch reports pauses every active schedule that no longer fits (PriceWatch.SchedulesToPause, with its words). A
// paused schedule runs nothing until an edit makes it fit again (SuiteService).
using PiSharp.WebAgent.Evals.Drift;
using PiSharp.WebAgent.Evals.Store;

namespace PiSharp.WebAgent.Evals.Suites;

/// <summary>Runs suites on their schedules. A singleton and a hosted service.</summary>
public sealed class EvalScheduler : BackgroundService
{
    /// <summary>How often schedules are looked at.</summary>
    public static TimeSpan Interval { get; } = TimeSpan.FromSeconds(30);

    private readonly EvalStore store;
    private readonly SuiteService suites;
    private readonly SuiteRunner runner;
    private readonly PriceWatch? watch;
    private readonly TimeProvider time;
    private readonly ILogger? log;
    private readonly SemaphoreSlim tickGate = new(1, 1);

    /// <summary>A scheduler over the store; with <paramref name="watch"/>, price changes pause schedules that stop fitting.</summary>
    public EvalScheduler(EvalStore store, SuiteService suites, SuiteRunner runner, PriceWatch? watch = null, TimeProvider? time = null,
        ILogger<EvalScheduler>? log = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(suites);
        ArgumentNullException.ThrowIfNull(runner);
        this.store = store;
        this.suites = suites;
        this.runner = runner;
        this.watch = watch;
        this.time = time ?? TimeProvider.System;
        this.log = log;
        if (watch is not null) watch.Changed += check => ApplyPriceChanges(check);
    }

    /// <summary>Decides the latest undecided slot of every active schedule (see the file comment).</summary>
    /// <returns>The decisions it made (started slots carry their run id).</returns>
    public async Task<IReadOnlyList<ScheduleSlot>> TickAsync(CancellationToken cancellationToken = default)
    {
        await tickGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // First, runs a crash left going (a no-op once a pass has marked them all): until they are marked interrupted, their suites'
            // slots would be skipped as still running.
            try { await runner.RecoverAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
            {
                log?.LogWarning(error, "Suite runs left going by the last session could not be marked interrupted yet; they are tried again at the next tick.");
            }
            var decisions = new List<ScheduleSlot>();
            foreach (var suite in store.ScheduledSuites())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (await DecideAsync(suite, cancellationToken).ConfigureAwait(false) is { } decision) decisions.Add(decision);
                }
                catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
                {
                    log?.LogWarning(error, "The schedule of suite {Suite} could not be checked; it is tried again at the next tick.", suite.Name);
                }
            }
            return decisions;
        }
        finally
        {
            tickGate.Release();
        }
    }

    /// <summary>Pauses every active schedule that <paramref name="check"/>'s price changes (default: Price Watch's last check) make stop
    /// fitting its cap, or that now has a model without a known price.</summary>
    /// <returns>What it paused.</returns>
    public IReadOnlyList<SchedulePauseNotice> ApplyPriceChanges(PriceCheck? check = null)
    {
        if (watch is null) return [];
        try
        {
            var active = store.ScheduledSuites().Where(suite => suite.Schedule is { Active: true })
                .Select(suite => new SuiteCases(suite, store.Cases(suite.Id))).ToList();
            if (active.Count == 0) return [];
            var notices = watch.SchedulesToPause(active, time.GetUtcNow(), check?.Changes);
            foreach (var notice in notices) suites.Pause(notice.SuiteId, notice.Pause);
            return notices;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log?.LogWarning(error, "Schedules could not be checked against the new prices.");
            return [];
        }
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        // The first tick marks the runs a crash left going interrupted (TickAsync), and later ticks retry it until it succeeds.
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) when (error is not OutOfMemoryException) { log?.LogWarning(error, "The suite scheduler's tick failed."); }
            try { await Task.Delay(Interval, time, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<ScheduleSlot?> DecideAsync(Suite suite, CancellationToken cancellationToken)
    {
        if (suite.Schedule is not { Active: true } schedule) return null;
        var now = time.GetUtcNow();
        if (schedule.Latest(now) is not { } slot || store.Slot(suite.Id, slot) is not null) return null;
        if (store.ActiveRun(suite.Id) is { } active)
            return Skip(suite, slot, SlotOutcome.SkippedRunning, $"Run #{active.Number} was still going.", now);
        var cases = store.Cases(suite.Id);
        if (suites.PauseFor(suite, cases, now) is { } pause)
        {
            suites.Pause(suite.Id, pause);
            return Skip(suite, slot, pause.Code == SchedulePause.UnknownPrice ? SlotOutcome.SkippedUnknownPrice : SlotOutcome.SkippedOverCap, pause.Reason, now);
        }
        var start = await runner.StartAsync(suite.Id, new SuiteRunRequest { Trigger = SuiteRunTrigger.Schedule, Slot = slot }, cancellationToken).ConfigureAwait(false);
        if (start.Posted && start.Run is { } run)
        {
            log?.LogInformation("Suite {Suite} started run #{Number} for its {Slot:u} slot.", suite.Name, run.Number, slot);
            return store.Slot(suite.Id, slot);
        }
        if (start.Spend is { } refusal)
            return Skip(suite, slot, SlotOutcome.SkippedRefused, "The Spend Guard refused the run's reservation: " + refusal.Message, now);
        return start.Refused?.Code switch
        {
            SuiteRunRefusal.SlotTaken => null,
            SuiteRunRefusal.Running => Skip(suite, slot, SlotOutcome.SkippedRunning, start.Refused.Message, now),
            ScheduleRefusal.OverCap => PauseAndSkip(suite, slot, SchedulePause.OverCap, SlotOutcome.SkippedOverCap, start.Refused.Message, now),
            ScheduleRefusal.UnknownPrice => PauseAndSkip(suite, slot, SchedulePause.UnknownPrice, SlotOutcome.SkippedUnknownPrice, start.Refused.Message, now),
            _ => Skip(suite, slot, SlotOutcome.SkippedRefused, start.Refused?.Message ?? "The run did not start.", now),
        };
    }

    private ScheduleSlot? PauseAndSkip(Suite suite, DateTimeOffset slot, string code, SlotOutcome outcome, string reason, DateTimeOffset now)
    {
        suites.Pause(suite.Id, new SchedulePause(code, reason, now));
        return Skip(suite, slot, outcome, reason, now);
    }

    private ScheduleSlot? Skip(Suite suite, DateTimeOffset slot, SlotOutcome outcome, string detail, DateTimeOffset now)
    {
        var decision = new ScheduleSlot(suite.Id, slot, outcome, null, detail, now);
        if (!store.TryRecordSlot(decision)) return null;
        log?.LogInformation("Suite {Suite} skipped its {Slot:u} slot ({Outcome}): {Detail}", suite.Name, slot, EvalJson.Wire(outcome), detail);
        return decision;
    }
}
