using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Hosting.Testing;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Evals;
using PiSharp.WebAgent.Evals.Checks;
using PiSharp.WebAgent.Evals.Drift;
using PiSharp.WebAgent.Evals.Store;
using PiSharp.WebAgent.Evals.Suites;
using PiSharp.WebAgent.Faux;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Spend;
using ChordJson = PiSharp.Durable.Chord.JsonValue;
using ChordObject = PiSharp.Durable.Chord.JsonObjectValue;
using EvalCheck = PiSharp.WebAgent.Evals.Check;

// Decision 0011 (run W5, E4): the eval store, suite runs, the scheduler and the /api/evals suites, cases, runs and drift endpoints, over
// priced faux models only (FauxPricing's list price through a catalog whose price a test can raise, and a transport decorator that names the
// answering model as a provider's responseModel). The brief's cases: missed schedule slots collapse into one; a cap below the worst case
// refuses the schedule; a price rise pauses it (Price Watch's change, through the scheduler); a scripted transport that changes
// responseModel is flagged drift, and the confirming run carries it on. Also: a run is one group reservation whose holds are all released,
// with every call guarded and recorded as source drift under the run's group; a slot is skipped while the previous run is going; cancel and
// recovery (retried on each tick until every crashed run is marked); a Spend Guard refusal starts nothing, and its Ask answer starts the
// same run id; the typical cost counts full runs only; accepting a baseline twice adds nothing; responseModel on run records (runs.db
// migration 2); and the JSON API in the built app. W5 fix 2 (tests-ci): a run's drift signals are stored before it reads complete, held by
// a trigger on eval_runs that counts them inside the update that marks the run complete, and by the run page's JSON polled while it ends.
internal static partial class Program
{
    /// <summary>The test clock's start: 09:10 UTC.</summary>
    private static readonly DateTimeOffset SuStart = new(2026, 10, 30, 9, 10, 0, TimeSpan.Zero);

    /// <summary>Every hour on the hour, in UTC.</summary>
    private static SuiteSchedule SuHourly => new() { Repeat = ScheduleRepeat.Hours, EveryHours = 1, At = "00:00", TimeZone = "UTC", Enabled = true };

    internal static IEnumerable<(string, Func<Task>)> EvalSuiteCases() =>
    [
        Case("evals.suites.store-round-trips-and-writes-are-idempotent", async () =>
        {
            await using var host = SuHost.Create("su-store");
            var draft = new Suite
            {
                Id = EvalIds.New(EvalIds.Suite), Name = "  Regex edge cases ", Targets = [new SuiteTarget("faux", "echo")], K = 2, CapMicros = 500_000,
                Schedule = SuHourly,
            };
            var (suite, created) = host.Suites.CreateSuite(draft);
            Check(created && suite.Name == "Regex edge cases" && suite.Schedule is { Enabled: false } && suite.Created == SuStart, "made, trimmed, its schedule off");
            var (again, createdAgain) = host.Suites.CreateSuite(draft with { Name = "Another name" });
            Check(!createdAgain && again.Name == "Regex edge cases", "a create sent twice returns the suite it made");
            Equal(1, host.Store.Suites().Count, "one suite");
            Equal(0, host.Store.ScheduledSuites().Count, "a schedule is turned on only by SetSchedule");
            Throws<ArgumentException>(() => host.Suites.CreateSuite(draft with { Id = EvalIds.New(EvalIds.Suite), K = 11 }), "k above 10");

            var first = host.Suites.CreateCase(SuCase(suite.Id, "First", "one")).Case;
            var second = host.Suites.CreateCase(SuCase(suite.Id, "Second", "two")).Case;
            var third = host.Suites.CreateCase(SuCase(suite.Id, "Third", "three")).Case;
            Check(!host.Suites.CreateCase(first with { Name = "Changed" }).Created && host.Store.Case(first.Id)!.Name == "First", "a case sent twice is made once");
            string Names() => string.Join(",", host.Store.Cases(suite.Id).Select(testCase => testCase.Name));
            Equal("First,Second,Third", Names(), "in the order added");
            Check(host.Suites.MoveCase(third.Id, 0) && host.Suites.MoveCase(third.Id, 0), "moved, twice to the same place");
            Equal("Third,First,Second", Names(), "moved first");
            Check(!host.Suites.MoveCase(EvalIds.New(EvalIds.Case), 1), "no such case");
            Equal("k1", host.Store.Case(first.Id)!.Checks.Single().Id, "checks get their ids");
            var other = host.Suites.CreateSuite(new Suite { Id = EvalIds.New(EvalIds.Suite), Name = "Other" }).Suite;
            Throws<ArgumentException>(() => host.Suites.CreateCase(first with { SuiteId = other.Id }), "a case id of another suite");
            Throws<KeyNotFoundException>(() => host.Suites.CreateCase(SuCase(EvalIds.New(EvalIds.Suite), "Lost", "x")), "a case of no suite");
            Throws<ArgumentException>(() => host.Suites.CreateCase(SuCase(suite.Id, "Bad judge", "x",
                [new EvalCheck { Kind = CheckKind.Judge, Judge = new JudgeSpec("faux", "nope", "Score it.") }])), "a judge model that does not exist");
            var renamed = host.Suites.UpdateCase(second.Id, testCase => testCase with { Name = "Second, renamed", Tags = ["regex"], SuiteId = other.Id });
            Check(renamed is { Name: "Second, renamed" } && renamed.SuiteId == suite.Id && host.Store.Case(second.Id)!.Tags.SequenceEqual(["regex"]),
                "updated; its suite is kept");
            Check(host.Suites.DeleteCase(first.Id) && !host.Suites.DeleteCase(first.Id), "deleted once");
            Equal(2, host.Store.CaseCount(suite.Id), "two left");
            var changed = host.Suites.UpdateSuite(suite.Id, current => current with { Name = "Regex", K = 3, Id = other.Id });
            Check(changed is { Name: "Regex", K: 3 } && changed.Id == suite.Id && host.Store.Suite(suite.Id)!.K == 3, "suite updated; its id is kept");
            Equal("Other,Regex", string.Join(",", host.Store.Suites().Select(item => item.Name)), "listed by name");

            var slot = new ScheduleSlot(suite.Id, SuStart, SlotOutcome.SkippedRunning, null, "Run #1 was still going.", SuStart);
            Check(host.Store.TryRecordSlot(slot) && !host.Store.TryRecordSlot(slot with { Outcome = SlotOutcome.SkippedOverCap }), "one decision per suite and slot");
            Equal(SlotOutcome.SkippedRunning, host.Store.Slot(suite.Id, SuStart)?.Outcome, "the first decision stays");
            Throws<ArgumentException>(() => host.Store.TryRecordSlot(slot with { Slot = SuStart.AddHours(1), Outcome = SlotOutcome.Started }),
                "a started slot is written with its run");
            Check(host.Suites.DeleteSuite(suite.Id) && !host.Suites.DeleteSuite(suite.Id), "the suite deleted once");
            Check(host.Store.CaseCount(suite.Id) == 0 && host.Store.Slot(suite.Id, SuStart) is null, "its cases and slots went with it");
        }),

        Case("evals.suites.run-now-reserves-one-group-and-records-every-answer", async () =>
        {
            await using var host = SuHost.Create("su-run");
            var (suite, cases) = SuSeed(host, "Two targets", targets: [new SuiteTarget("faux", "echo"), new SuiteTarget("faux", "echo-thinking")], k: 2, cases: 2);
            var judge = new EvalCheck { Kind = CheckKind.Judge, Judge = new JudgeSpec("faux", "echo-small", "Score how well the answer restates the question.") };
            host.Suites.UpdateCase(cases[0].Id, testCase => testCase with { Checks = [.. testCase.Checks, judge] });
            var estimate = host.Suites.Estimate(suite, host.Store.Cases(suite.Id));
            Check(estimate is { WorstMicros: > 0, TypicalMicros: null, HasUnknownPrice: false }, "a metered worst case, no typical cost yet: " + estimate.WorstMicros);

            var runId = EvalIds.New(EvalIds.Run);
            var start = await host.Runner.StartAsync(suite.Id, new SuiteRunRequest { RunId = runId });
            Check(start is { Posted: true, Refused: null, Spend: null, Run: { Number: 1, Status: SuiteRunStatus.Running, Trigger: SuiteRunTrigger.Manual } } &&
                start.Run.Id == runId && start.Run.GroupId == runId, "started as run #1, its own reservation group: " + start.Refused?.Message + start.Spend?.Message);
            Equal(estimate.WorstMicros, start.Run!.ReservedMicros, "reserved the worst case shown (judge calls included), all at once");
            var retried = await host.Runner.StartAsync(suite.Id, new SuiteRunRequest { RunId = runId });
            Check(retried is { Posted: true } && retried.Run!.Id == runId, "a retried Run now finds the run it started");

            var run = await SuFinished(host, runId);
            Check(run is { Status: SuiteRunStatus.Complete, Problem: null, Finished: not null }, $"complete: {EvalJson.Wire(run.Status)} {run.Problem}");
            Equal(1, host.Store.RunCount(suite.Id), "one run");
            var results = host.Store.Results(runId);
            Equal(8, results.Count, "2 cases × 2 targets × k 2");
            Check(results.All(result => result is { Status: ResultStatus.Done, CallRunId: not null, Verdict: CheckVerdict.Passed, Score: 1, Fingerprint: not null, CostMicros: > 0 }),
                "every answer done, checked and priced: " + string.Join("; ", results.Select(result => $"{EvalJson.Wire(result.Status)} {result.Error}")));
            Check(results.All(result => result.Text!.StartsWith("Echo: ", StringComparison.Ordinal)), "the answers' text");
            Equal(2, results.Select(result => result.TargetId).Distinct().Count(), "both targets");
            var judged = results.Where(result => result.CaseId == cases[0].Id).ToList();
            Check(judged.Count == 4 && judged.All(result => result.Checks.Count == 2 && result.Checks[1] is { Kind: CheckKind.Judge, Verdict: CheckVerdict.Passed, JudgeRunId: not null }),
                "the judge scored every answer of its case");

            var calls = host.Runs.List(new RunQuery { GroupId = runId }).Items;
            Equal(12, calls.Count, "8 answers and 4 judge calls, each recorded");
            Check(calls.All(call => call is { Source: "drift", Status: RunStatus.Completed, SpendOp: not null }), "guarded, recorded as drift, completed");
            var answerIds = results.Select(result => result.CallRunId!).ToHashSet(StringComparer.Ordinal);
            Check(judged.All(result => host.Runs.Get(result.Checks[1].JudgeRunId!)?.ParentId == result.CallRunId), "a judge call is recorded under its answer");
            Check(calls.Where(call => answerIds.Contains(call.Id)).All(call => call.Title is "Case 1" or "Case 2"), "an answer is titled with its case");

            // W5 integration fix 1: each call is sent on its own hold (RunEngine.StartReservedAsync), so the ledger has one row per call,
            // the hold's, settled at the call's actual cost: no released hold beside a second reservation of the same call.
            var rows = (await host.Ledger.QueryAsync(new SpendLedgerQuery { Run = runId, Limit = SpendLedgerQuery.MaxLimit })).Items;
            var holds = rows.Where(row => row.Op.StartsWith(SuiteRunner.HoldPrefix + runId + "-", StringComparison.Ordinal)).ToList();
            Equal(12, holds.Count, "one hold per call in the group reservation");
            Equal(12, rows.Count, "and no other row: every call was sent on its hold: " + string.Join(", ", rows.Select(row => row.Op)));
            Check(holds.All(row => row is { State: ReservationState.Settled, Source: SpendSource.Drift }), "every hold settled as drift by its call");
            Check(calls.All(call => call.SpendOp is { } op && op == call.Op && op.StartsWith(SuiteRunner.HoldPrefix + runId + "-", StringComparison.Ordinal)),
                "every recorded call names its hold as its ledger row");
            Equal(12, calls.Select(call => call.SpendOp).Distinct(StringComparer.Ordinal).Count(), "a hold per call");
            var spent = holds;
            Equal(spent.Sum(row => row.ActualMicros ?? 0), run.CostMicros, "the run's cost is what its calls settled");
            Check(run.CostMicros > 0 && run.CostMicros <= run.ReservedMicros && run.ListMicros == 0, $"within its reservation: {run.CostMicros} of {run.ReservedMicros}");
            Equal(run.CostMicros, host.Suites.Estimate(suite, host.Store.Cases(suite.Id)).TypicalMicros, "the typical cost is the median of finished runs");

            var json = SuiteEndpoints.RunJson(runId, host.Store, host.Runner);
            Check(json["run"]?["status"]?.GetValue<string>() == "complete" && json["results"] is JsonArray { Count: 8 } && json["running"]?.GetValue<bool>() == false,
                "the run page's JSON: " + json.ToJsonString()[..Math.Min(400, json.ToJsonString().Length)]);
        }),

        Case("evals.suites.scheduler-collapses-missed-slots-into-one", async () =>
        {
            await using var host = SuHost.Create("su-slots");
            var (suite, _) = SuSeed(host, "Hourly regex");
            var save = host.Suites.SetSchedule(suite.Id, SuHourly, 1_000_000);
            Check(save is { Saved: true, Refused: null, Suite.Schedule: { Enabled: true, Paused: null } }, "turned on: " + save.Refused?.Message);
            Equal(SuStart, save.Suite.Schedule!.EnabledAt, "from now");
            Equal(SuStart.AddMinutes(50), save.Next, "next: 10:00");
            Equal(1, host.Store.ScheduledSuites().Count, "scheduled");
            Equal(0, (await host.Scheduler.TickAsync()).Count, "09:00 was before it was turned on");

            host.Clock.Advance(TimeSpan.FromHours(3)); // 12:10: the 10:00, 11:00 and 12:00 slots were missed (the app was not running)
            var decisions = await host.Scheduler.TickAsync();
            var first = decisions.Single();
            Check(first is { Outcome: SlotOutcome.Started, RunId: not null, Detail: null }, "one run started: " + EvalJson.Wire(first.Outcome) + " " + first.Detail);
            Equal(SuAt(12), first.Slot, "for the latest slot only");
            var run = await SuFinished(host, first.RunId!);
            Check(run is { Status: SuiteRunStatus.Complete, Trigger: SuiteRunTrigger.Schedule, Number: 1 } && run.Slot == SuAt(12), "a complete scheduled run #1");
            Equal(0, (await host.Scheduler.TickAsync()).Count, "a decided slot is never decided again");
            Equal(1, host.Store.RunCount(suite.Id), "three missed slots, one run");
            Equal(1, host.Store.Slots(suite.Id).Count, "one slot row");
            var twice = await host.Runner.StartAsync(suite.Id, new SuiteRunRequest { Trigger = SuiteRunTrigger.Schedule, Slot = SuAt(12) });
            Check(twice is { Posted: false, Refused.Code: SuiteRunRefusal.SlotTaken }, "the runner never runs a slot twice: " + twice.Refused?.Code);
            Equal(1, host.Store.RunCount(suite.Id), "still one run");

            // A slot is skipped while the previous run is still going.
            host.Script.Hold();
            host.Clock.Advance(TimeSpan.FromHours(1)); // 13:10
            var second = (await host.Scheduler.TickAsync()).Single();
            Check(second is { Outcome: SlotOutcome.Started } && second.Slot == SuAt(13), "the 13:00 slot started");
            await WaitUntilAsync(() => Task.FromResult(host.Script.Asked.Count >= 2), "the second run's call is in flight");
            host.Clock.Advance(TimeSpan.FromHours(1)); // 14:10
            var skipped = (await host.Scheduler.TickAsync()).Single();
            Check(skipped is { Outcome: SlotOutcome.SkippedRunning, RunId: null, Detail: "Run #2 was still going." } && skipped.Slot == SuAt(14),
                "the 14:00 slot skipped: " + skipped.Detail);
            var manual = await host.Runner.StartAsync(suite.Id, new SuiteRunRequest());
            Check(manual is { Posted: false, Refused: { Code: SuiteRunRefusal.Running, Message: "Run #2 of this suite is still going." } }, "Run now waits too");
            host.Script.Release();
            Equal(SuiteRunStatus.Complete, (await SuFinished(host, second.RunId!)).Status, "run #2 finished");
            Equal(0, (await host.Scheduler.TickAsync()).Count, "the skipped slot is not run late");
            host.Clock.Advance(TimeSpan.FromHours(1)); // 15:10
            var third = (await host.Scheduler.TickAsync()).Single();
            Check(third is { Outcome: SlotOutcome.Started } && third.Slot == SuAt(15), "the 15:00 slot ran");
            Equal(3, (await SuFinished(host, third.RunId!)).Number, "as run #3");
            Equal("15:started,14:skipped-running,13:started,12:started",
                string.Join(",", host.Store.Slots(suite.Id).Select(item => $"{item.Slot.UtcDateTime.Hour}:{EvalJson.Wire(item.Outcome)}")), "the slot log");
            Equal(SuAt(16), host.Suites.Overview(host.Suites.RequireSuite(suite.Id)).Next, "next: 16:00");

            var off = host.Suites.SetSchedule(suite.Id, null, null);
            Check(off is { Saved: true, Suite.Schedule: { Enabled: false, Repeat: ScheduleRepeat.Hours } } && off.Suite.CapMicros == 1_000_000, "turned off; settings and cap kept");
            host.Clock.Advance(TimeSpan.FromHours(2));
            Equal(0, (await host.Scheduler.TickAsync()).Count, "a schedule turned off runs nothing");
        }),

        Case("evals.suites.cap-below-the-worst-case-refuses-the-schedule", async () =>
        {
            await using var host = SuHost.Create("su-cap");
            var (suite, cases) = SuSeed(host, "Capped", k: 2, cap: null);
            var estimate = host.Suites.Estimate(suite, cases);
            var worst = estimate.WorstMicros;
            Check(worst > 0 && !estimate.HasUnknownPrice && estimate.TypicalMicros is null, "a metered worst case: " + worst);

            var noCap = host.Suites.SetSchedule(suite.Id, SuHourly, null);
            Check(noCap is { Saved: false, Refused.Code: ScheduleRefusal.NoCap }, "a schedule needs a cap: " + noCap.Refused?.Code);
            var below = host.Suites.SetSchedule(suite.Id, SuHourly, worst - 1);
            var refused = below.Refused;
            Check(!below.Saved && refused is { Code: ScheduleRefusal.OverCap } && refused.WorstMicros == worst && refused.CapMicros == worst - 1,
                "a cap $0.000001 below the worst case is refused: " + refused?.Code);
            var atLeast = (worst + 9_999) / 10_000 * 10_000;
            Equal($"The worst case, {DriftText.Money(worst)}, is above the {DriftText.Money(worst - 1)} cap. Raise the cap to at least {DriftText.Money(atLeast)}, " +
                "lower k, or set a lower maxTokens on the cases.", refused!.Message, "says what fits");
            Equal(worst, below.Estimate.WorstMicros, "with the worst case it showed");
            var stored = host.Store.Suite(suite.Id)!;
            Check(stored.Schedule is null && stored.CapMicros is null && host.Store.ScheduledSuites().Count == 0, "nothing saved");
            var empty = host.Suites.CreateSuite(new Suite { Id = EvalIds.New(EvalIds.Suite), Name = "Empty", Targets = [new SuiteTarget("faux", "echo")] }).Suite;
            Check(host.Suites.SetSchedule(empty.Id, SuHourly, 1_000_000) is { Saved: false, Refused: { Code: ScheduleRefusal.CannotRun, Message: "Add a test case first." } },
                "a suite without cases cannot be scheduled");

            var fits = host.Suites.SetSchedule(suite.Id, SuHourly, worst);
            Check(fits is { Saved: true, Refused: null, Suite.Schedule.Enabled: true } && fits.Suite.CapMicros == worst, "a cap equal to the worst case fits");
            var bigger = host.Suites.UpdateSuite(suite.Id, current => current with { K = 3 });
            Check(bigger.Schedule is { Enabled: true, Paused.Code: SchedulePause.OverCap }, "an edit that stops fitting pauses the schedule");
            Equal($"Its worst case of {DriftText.Money(host.Suites.Estimate(bigger, cases).WorstMicros)} is above its {DriftText.Money(worst)} suite-run cap.",
                bigger.Schedule!.Paused!.Reason, "and says why");
            host.Clock.Advance(TimeSpan.FromHours(2));
            Equal(0, (await host.Scheduler.TickAsync()).Count, "a paused schedule runs nothing");
            var manual = await host.Runner.StartAsync(suite.Id, new SuiteRunRequest());
            Check(manual is { Posted: false, Refused.Code: ScheduleRefusal.OverCap }, "a run by hand is refused over the cap too: " + manual.Refused?.Code);
            Equal(0, (await host.Ledger.QueryAsync(new SpendLedgerQuery { Limit = SpendLedgerQuery.MaxLimit })).Items.Count, "before anything was reserved");
            var resumed = host.Suites.UpdateSuite(suite.Id, current => current with { K = 2 });
            Check(resumed.Schedule is { Enabled: true, Paused: null } && resumed.Schedule.EnabledAt == host.Clock.GetUtcNow(), "an edit that fits again resumes it from now");
            Equal(0, (await host.Scheduler.TickAsync()).Count, "slots missed while paused never run");

            host.Catalog.Unpriced.Add("echo");
            var unknown = host.Suites.SetSchedule(suite.Id, SuHourly, worst);
            Check(unknown is { Saved: false, Refused.Code: ScheduleRefusal.UnknownPrice } && unknown.Refused.Message.StartsWith("faux/echo has no known price", StringComparison.Ordinal),
                "a model without a price cannot be scheduled: " + unknown.Refused?.Message);
            var unpriced = host.Store.Suite(suite.Id)!;
            Equal(ScheduleRefusal.UnknownPrice, SuiteService.Refusal(unpriced, cases.Count, host.Suites.Estimate(unpriced, cases), null)?.Code,
                "with no cap either, the unknown price is what refuses it: no cap would lift it");
            host.Clock.Advance(TimeSpan.FromHours(1));
            var skipped = (await host.Scheduler.TickAsync()).Single();
            Check(skipped is { Outcome: SlotOutcome.SkippedUnknownPrice } && host.Store.Suite(suite.Id)!.Schedule is { Paused.Code: SchedulePause.UnknownPrice },
                "the scheduler pauses a schedule whose price went unknown, and skips the slot: " + EvalJson.Wire(skipped.Outcome));
            Equal(0, host.Store.RunCount(suite.Id), "no run");
        }),

        Case("evals.suites.price-rise-pauses-the-schedule", async () =>
        {
            await using var host = SuHost.Create("su-price");
            var scheduler = host.Scheduler; // listens to Price Watch
            var watch = host.Services.GetRequiredService<PriceWatch>();
            Check(watch.Check() is { Saved.Count: 1, Changes.Count: 0 }, "the first snapshot is not a change");
            var (suite, cases) = SuSeed(host, "Price watched", k: 2);
            var worst = host.Suites.Estimate(suite, cases).WorstMicros;
            var cap = worst * 3 / 2;
            Check(host.Suites.SetSchedule(suite.Id, SuHourly, cap).Saved, "fits its cap");
            Equal(0, watch.Check().Changes.Count, "no change, nothing paused");
            Check(host.Store.Suite(suite.Id)!.Schedule is { Paused: null }, "still active");

            host.Catalog.Scale = 2;
            var check = watch.Check();
            Check(check.Changes.Count > 0 && check.Changes.All(row => row.Change.Rise), "Price Watch saw the rise: " + check.Changes.Count);
            var after = host.Suites.Estimate(suite, cases).WorstMicros;
            Check(after > cap, $"the worst case rose above the cap: {after} > {cap}");
            var schedule = host.Store.Suite(suite.Id)!.Schedule!;
            Check(schedule is { Enabled: true, Paused.Code: SchedulePause.OverCap }, "the schedule is paused, still on");
            Equal($"A catalog price change raised its worst case to {DriftText.Money(after)}, above its {DriftText.Money(cap)} suite-run cap.",
                schedule.Paused!.Reason, "the mockup's reason");
            Equal(SuStart, schedule.Paused.At, "paused when the change was seen");
            var drift = SuiteEndpoints.DriftJson(host.Suites, host.Services);
            Check(drift.ToJsonString().Contains("A catalog price change raised its worst case", StringComparison.Ordinal), "the Drift page says so");

            host.Clock.Advance(TimeSpan.FromHours(3));
            Equal(0, (await scheduler.TickAsync()).Count, "a paused schedule runs nothing, never over its cap");
            Equal(0, host.Store.RunCount(suite.Id), "no run");
            var resumed = host.Suites.UpdateSuite(suite.Id, current => current with { CapMicros = after });
            Check(resumed.Schedule is { Paused: null } && resumed.Schedule.EnabledAt == host.Clock.GetUtcNow(), "raising the cap resumes it from now");
            Equal(0, (await scheduler.TickAsync()).Count, "the slots missed while paused never run");
            host.Clock.Advance(TimeSpan.FromHours(1));
            var next = (await scheduler.TickAsync()).Single();
            Check(next is { Outcome: SlotOutcome.Started }, "the next slot runs at the new price: " + next.Detail);
            var run = await SuFinished(host, next.RunId!);
            Check(run.Status == SuiteRunStatus.Complete && run.ReservedMicros <= after, $"within the cap: {run.ReservedMicros} of {after}");
        }),

        Case("evals.suites.response-model-change-is-drift-after-the-confirming-run", async () =>
        {
            await using var host = SuHost.Create("su-drift");
            var (suite, _) = SuSeed(host, "Response model", k: 2, cases: 2);
            var target = suite.Targets.Single();
            host.Models.ResponseModel = "echo-2026-05";
            var first = await SuRunNow(host, suite.Id);
            Check(host.Store.Results(first.Id).All(result => result.ResponseModel == "echo-2026-05"), "each answer names its model");
            Equal("echo-2026-05", host.Runs.Get(host.Store.Results(first.Id)[0].CallRunId!)!.ResponseModel, "the run record carries responseModel");
            Equal(0, host.Store.Signals(suite.Id).Count, "no baseline, no drift");

            var accepted = host.Suites.AcceptBaseline(suite.Id, first.Id, null, replaceReferences: false);
            Check(accepted.Accepted.Single() is { } baseline && baseline.RunId == first.Id && baseline.TargetId == target.Id &&
                baseline.ResponseModels.SequenceEqual(["echo-2026-05"]), "run #1 is the baseline, with its response model");
            Equal(0, host.Suites.AcceptBaseline(suite.Id, first.Id, null, false).Accepted.Count, "accepting it again changes nothing");
            Throws<ArgumentException>(() => host.Suites.AcceptBaseline(suite.Id, first.Id, ["t000000000000"], false), "not one of its targets");

            var steady = await SuRunNow(host, suite.Id);
            Check(host.Store.Signals(suite.Id).All(signal => signal.Rule != DriftRule.ResponseModel), "the same model: no response-model signal");
            Equal(2, steady.Number, "run #2");

            host.Models.ResponseModel = "echo-2026-09";
            var changed = await SuRunNow(host, suite.Id);
            var raised = host.Store.Signals(suite.Id, target.Id).Single(signal => signal.Rule == DriftRule.ResponseModel);
            Check(raised is { Level: DriftLevel.Drift } && raised.RunId == changed.Id && raised.BaselineRunId == first.Id, "run #3 raised response-model drift");
            Equal("The response model changed from echo-2026-05 to echo-2026-09.", raised.Message, "says what changed");
            Equal(raised.Id, host.Store.RunSignals(changed.Id).Single(signal => signal.Rule == DriftRule.ResponseModel).Id, "listed with the run");

            var confirming = await SuRunNow(host, suite.Id);
            var carried = host.Store.Signals(suite.Id, target.Id).Where(signal => signal.Rule == DriftRule.ResponseModel).Single();
            Check(carried is { Level: DriftLevel.Drift } && carried.RunId == confirming.Id && carried.FirstRunId == changed.Id,
                "the confirming run carries the drift on, remembering run #3");
            Equal("Run #3 saw it first and run #4 saw it again.", DriftText.Lineage(carried, changed.Number, confirming.Number), "the lineage line");
            var closed = host.Store.Signals(suite.Id, target.Id, openOnly: false).Single(signal => signal.Id == raised.Id);
            Equal(DriftClosed.SeenAgain, DriftClosed.Reason(closed), "run #3's signal closed as seen again");
            Equal(DriftLevel.Drift, host.Suites.Overview(host.Suites.RequireSuite(suite.Id)).Level, "the suite shows drift");
            Check(host.Runner.AnalyzeDrift(confirming.Id).All(analysis => analysis.AlreadyAnalysed), "analysing a run again changes nothing");
            Equal(1, host.Store.Signals(suite.Id).Count(signal => signal.Rule == DriftRule.ResponseModel), "still one open response-model signal");

            var reset = host.Suites.AcceptBaseline(suite.Id, confirming.Id, null, replaceReferences: true);
            Check(reset.Cleared.Any(signal => signal.Id == carried.Id) && reset.ReferencesReplaced == 2, "a new baseline clears it and replaces the references");
            Check(host.Store.Signals(suite.Id).Count == 0 && host.Store.Baselines(suite.Id).Single().RunId == confirming.Id, "nothing open; run #4 is the baseline");
            Equal(2, host.Store.Baselines(suite.Id, currentOnly: false).Count, "the old baseline is kept, replaced");
            Check(host.Store.Cases(suite.Id).All(testCase => testCase.Reference?.Text?.StartsWith("Echo: ", StringComparison.Ordinal) == true), "references from run #4");
        }),

        Case("evals.suites.drift-signals-are-stored-before-the-run-reads-complete", async () =>
        {
            // W5 fix 2 (tests-ci): the W5 integration moved the drift analysis ahead of storing the run as complete (SuiteRunner.Finish), so
            // the run and suite pages never show a finished run without its signals. Two probes hold that order. A trigger on eval_runs
            // records, inside the very UPDATE that marks the run complete, how many signals the run has stored by then: a reorder records
            // none, however fast the analysis is. And the run page's JSON (SuiteEndpoints.RunJson, what GET /api/evals/runs/{id} answers),
            // polled without a pause while the run ends, must carry the response-model drift in the first answer that reads complete.
            await using var host = SuHost.Create("su-drift-order");
            var (suite, _) = SuSeed(host, "Drift order", k: 2, cases: 2);
            var target = suite.Targets.Single();
            host.Models.ResponseModel = "echo-2026-05";
            var first = await SuRunNow(host, suite.Id);
            Check(host.Suites.AcceptBaseline(suite.Id, first.Id, null, replaceReferences: false).Accepted.Single().RunId == first.Id, "run #1 is the baseline");
            var db = Path.Join(host.Options.DataDirectory, "webagent.db");
            SuCompletionProbe(db);

            // The provider updates its model silently; run #2's answers are held until the poller runs.
            host.Models.ResponseModel = "echo-2026-09";
            host.Script.Hold();
            var start = await host.Runner.StartAsync(suite.Id, new SuiteRunRequest());
            Check(start is { Posted: true, Run: { Number: 2 } }, "run #2 started: " + start.Refused?.Message + start.Spend?.Message);
            var runId = start.Run!.Id;
            var going = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            var poller = Task.Run(() =>
            {
                try
                {
                    var deadline = DateTime.UtcNow.AddSeconds(60);
                    for (var polls = 1; DateTime.UtcNow < deadline; polls++)
                    {
                        var view = SuiteEndpoints.RunJson(runId, host.Store, host.Runner);
                        var status = view["run"]?["status"]?.GetValue<string>();
                        if (status == "complete") return (View: view, Polls: polls);
                        Check(status is "queued" or "running", "run #2 ends complete, not " + status);
                        going.TrySetResult(view);
                    }
                    throw new TimeoutException("Run #2 did not read complete within a minute.");
                }
                catch (Exception error)
                {
                    going.TrySetException(error);
                    throw;
                }
            });
            var before = await going.Task.WaitAsync(TimeSpan.FromSeconds(30));
            host.Script.Release();
            var (complete, polls) = await poller.WaitAsync(TimeSpan.FromSeconds(90));
            await SuFinished(host, runId);

            Check(before["signals"] is JsonArray { Count: 0 }, "no signal while the run goes: " + before["signals"]?.ToJsonString());
            var probed = ReadDb(db, $"SELECT response_model, signals FROM su_completion_probe WHERE run_id = '{runId}'",
                reader => (ResponseModel: reader.GetInt64(0), Signals: reader.GetInt64(1)));
            Check(probed.Count == 1, "the probe saw run #2 marked complete once: " + probed.Count);
            Check(probed[0].ResponseModel == 1 && probed[0].Signals >= 1,
                $"when run #2 was marked complete, its response-model signal was stored already ({probed[0].ResponseModel} of {probed[0].Signals} signals)");
            var signals = complete["signals"]!.AsArray();
            Check(signals.Any(signal => signal?["rule"]?.GetValue<string>() == "response-model" && signal["level"]?.GetValue<string>() == "drift" &&
                signal["targetId"]?.GetValue<string>() == target.Id),
                $"the first of {polls} polls that reads run #2 complete already carries its response-model drift: " + signals.ToJsonString());
            Equal(probed[0].Signals, (long)host.Store.RunSignals(runId).Count, "and nothing was stored for it after that");
            Equal(DriftLevel.Drift, host.Store.RunSignals(runId).Single(signal => signal.Rule == DriftRule.ResponseModel).Level, "drift at once");
        }),

        Case("evals.suites.cancel-keeps-finished-answers-and-releases-holds", async () =>
        {
            await using var host = SuHost.Create("su-cancel");
            var (suite, _) = SuSeed(host, "Cancelled", k: 2);
            host.Script.Hold();
            var start = await host.Runner.StartAsync(suite.Id, new SuiteRunRequest());
            var runId = start.Run!.Id;
            await WaitUntilAsync(() => Task.FromResult(host.Script.Asked.Count >= 2), "both calls in flight");
            Check(host.Runner.IsRunning(runId), "going");
            Throws<InvalidOperationException>(() => host.Suites.DeleteSuite(suite.Id), "a suite with a run going is not deleted");
            Check(host.Runner.Cancel(runId), "cancelled");
            var run = await SuFinished(host, runId);
            host.Script.Release();
            Check(run is { Status: SuiteRunStatus.Cancelled, Problem: "Cancelled. Its finished answers stay.", Finished: not null }, "cancelled: " + EvalJson.Wire(run.Status));
            Check(host.Store.Results(runId).All(result => result.Status == ResultStatus.Cancelled), "the unanswered results are cancelled");
            var rows = (await host.Ledger.QueryAsync(new SpendLedgerQuery { Run = runId, Limit = SpendLedgerQuery.MaxLimit })).Items;
            Check(rows.Count > 0 && rows.All(row => row.State != ReservationState.Reserved), "nothing left reserved: " + string.Join(",", rows.Select(row => row.State)));
            Check(!host.Runner.Cancel(runId), "an ended run cannot be cancelled");
            Equal(null, host.Suites.Typical(suite.Id), "a cancelled run is not a typical cost");
            Check(host.Suites.DeleteSuite(suite.Id), "deleted once it ended");
        }),

        Case("evals.suites.recovery-marks-a-run-interrupted-and-releases-its-holds", async () =>
        {
            await using var host = SuHost.Create("su-recover");
            var (suite, cases) = SuSeed(host, "Crashed");
            var target = suite.Targets.Single();
            var draft = SuiteRun.Start(suite, [cases[0].Id], 1, SuiteRunTrigger.Manual, null, SuStart);
            var left = host.Store.InsertRun(draft with { Status = SuiteRunStatus.Running, GroupId = draft.Id, Started = SuStart },
                [new SuiteResult { RunId = draft.Id, CaseId = cases[0].Id, TargetId = target.Id, Status = ResultStatus.Running }], SuStart)!;
            var executor = host.Services.GetRequiredService<GuardedRunExecutor>();
            var group = await executor.ReserveGroupAsync(
            [
                new SpendCall
                {
                    Provider = "faux", Model = "echo", Source = SpendSource.Drift, Run = left.Id, Op = SuiteRunner.HoldPrefix + left.Id + "-0",
                    InputBytes = 200, MaxTokens = 256,
                },
            ]);
            Check(group.Reserved, "a hold the crashed process left reserved");
            Equal(1, await host.Runner.RecoverAsync(), "one run recovered");
            var run = host.Store.Run(left.Id)!;
            Check(run is { Status: SuiteRunStatus.Interrupted, Problem: SuiteRunner.InterruptedProblem, Finished: not null }, "interrupted: " + EvalJson.Wire(run.Status));
            Equal(ResultStatus.Cancelled, host.Store.Results(left.Id).Single().Status, "its unfinished answer is cancelled");
            Equal(ReservationState.Released, (await host.Ledger.GetAsync(SuiteRunner.HoldPrefix + left.Id + "-0"))!.State, "its hold is released");
            Equal(0, await host.Runner.RecoverAsync(), "once per process");
            await group.DisposeAsync();
            var next = await SuRunNow(host, suite.Id);
            Check(next is { Status: SuiteRunStatus.Complete, Number: 2 }, "the next run is #2");
        }),

        Case("evals.suites.recovery-is-retried-until-every-crashed-run-is-marked", async () =>
        {
            await using var host = SuHost.Create("su-recover-retry");
            var (stuck, stuckCases) = SuSeed(host, "Stuck");
            var (other, otherCases) = SuSeed(host, "Other");
            SuiteRun Crashed(Suite suite, IReadOnlyList<TestCase> cases, DateTimeOffset at)
            {
                var draft = SuiteRun.Start(suite, [cases[0].Id], 1, SuiteRunTrigger.Manual, null, at);
                return host.Store.InsertRun(draft with { Status = SuiteRunStatus.Running, GroupId = draft.Id, Created = at, Started = at },
                    [new SuiteResult { RunId = draft.Id, CaseId = cases[0].Id, TargetId = suite.Targets.Single().Id, Status = ResultStatus.Running }], at)!;
            }
            var failing = Crashed(stuck, stuckCases, SuStart.AddMinutes(-2)); // the oldest: recovered first
            var fine = Crashed(other, otherCases, SuStart.AddMinutes(-1));
            // One bad row: the database refuses to save the first run (as a busy or failing write would) until the trigger is dropped.
            host.Store.Db.Execute($"CREATE TRIGGER su_refuse_recovery BEFORE UPDATE ON eval_runs WHEN OLD.id = '{failing.Id}' BEGIN SELECT RAISE(ABORT, 'database is locked'); END;");

            Equal(1, await host.Runner.RecoverAsync(), "one run's failure does not stop the others");
            Check(host.Store.Run(fine.Id) is { Status: SuiteRunStatus.Interrupted, Problem: SuiteRunner.InterruptedProblem }, "the other crashed run is interrupted");
            Equal(SuiteRunStatus.Running, host.Store.Run(failing.Id)!.Status, "the failing run is still marked going");
            Equal(0, await host.Runner.RecoverAsync(), "the pass did not finish, so the next call tries again (and fails again)");
            Equal(SuiteRunStatus.Running, host.Store.Run(failing.Id)!.Status, "still going");
            var refused = await host.Runner.StartAsync(stuck.Id, new SuiteRunRequest());
            Check(refused is { Posted: false, Refused.Code: SuiteRunRefusal.Running }, "while it is marked going, Run now is refused: " + refused.Refused?.Code);

            Check(host.Suites.SetSchedule(stuck.Id, SuHourly, 1_000_000).Saved, "scheduled");
            host.Store.Db.Execute("DROP TRIGGER su_refuse_recovery;");
            host.Clock.Advance(TimeSpan.FromHours(1)); // 10:10
            var decision = (await host.Scheduler.TickAsync()).Single();
            Check(decision is { Outcome: SlotOutcome.Started, RunId: not null },
                "the next tick marked the run interrupted first, so the 10:00 slot ran instead of being skipped as still running: " + EvalJson.Wire(decision.Outcome) + " " + decision.Detail);
            Check(host.Store.Run(failing.Id) is { Status: SuiteRunStatus.Interrupted, Problem: SuiteRunner.InterruptedProblem, Finished: not null }, "the crashed run is interrupted now");
            Equal(ResultStatus.Cancelled, host.Store.Results(failing.Id).Single().Status, "its unfinished answer is cancelled");
            Check(await SuFinished(host, decision.RunId!) is { Status: SuiteRunStatus.Complete, Number: 2 }, "the scheduled run is #2, complete");
            Equal(0, await host.Runner.RecoverAsync(), "a pass that finished is not run again");
            Equal(SuiteRunStatus.Interrupted, host.Store.Run(fine.Id)!.Status, "marked once");
        }),

        Case("evals.suites.typical-cost-counts-full-runs-only", async () =>
        {
            await using var host = SuHost.Create("su-typical");
            var (suite, cases) = SuSeed(host, "Typical", k: 2, cases: 2);
            var full = await SuRunNow(host, suite.Id);
            Check(full.CostMicros > 0, "a priced full run");
            Equal(full.CostMicros, host.Suites.Typical(suite.Id), "one full run: its cost");

            // "Run this case": a subset run costs less than a full run, and is not what a scheduled run costs.
            var start = await host.Runner.StartAsync(suite.Id, new SuiteRunRequest { CaseIds = [cases[0].Id] });
            var subset = await SuFinished(host, start.Run!.Id);
            Check(subset is { Status: SuiteRunStatus.Complete, CaseIds.Count: 1 } && subset.CostMicros > 0 && subset.CostMicros < full.CostMicros,
                $"a complete subset run, cheaper: {subset.CostMicros} < {full.CostMicros}");
            Equal(full.CostMicros, host.Suites.Typical(suite.Id), "the subset run is left out of the typical cost");
            Equal(full.CostMicros, host.Suites.Estimate(suite, host.Store.Cases(suite.Id)).TypicalMicros, "and out of the estimate's");
            var again = await SuRunNow(host, suite.Id);
            Equal((long)Math.Round(TargetSummary.Median([full.CostMicros, again.CostMicros])!.Value), host.Suites.Typical(suite.Id), "the median of the two full runs");

            // A run of an older shape is not what a run of the suite as it is now costs either.
            host.Suites.CreateCase(SuCase(suite.Id, "Case 3", "Write a SemVer 2.0.0 regex, variant 3."));
            Equal(null, host.Suites.Typical(suite.Id), "a case added: no full run of the suite as it is yet");
            var bigger = await SuRunNow(host, suite.Id);
            Check(bigger.CostMicros > full.CostMicros, $"a bigger run: {bigger.CostMicros}");
            Equal(bigger.CostMicros, host.Suites.Typical(suite.Id), "its cost");
            host.Suites.UpdateSuite(suite.Id, current => current with { K = 1 });
            Equal(null, host.Suites.Typical(suite.Id), "k lowered: no full run at k 1 yet");
        }),

        Case("evals.suites.accepting-a-baseline-twice-adds-nothing", async () =>
        {
            await using var host = SuHost.Create("su-accept");
            var (suite, _) = SuSeed(host, "Accepted", targets: [new SuiteTarget("faux", "echo"), new SuiteTarget("faux", "echo-thinking")], cases: 2);
            var targets = suite.Targets.Select(target => target.Id).ToList();
            int Rows() => host.Store.Baselines(suite.Id, currentOnly: false).Count;
            List<DateTimeOffset> Updated() => [.. host.Store.Cases(suite.Id).Select(testCase => testCase.Updated)];

            var first = await SuRunNow(host, suite.Id);
            var accepted = host.Suites.AcceptBaseline(suite.Id, first.Id, null, replaceReferences: true);
            Check(accepted is { Accepted.Count: 2, ReferencesReplaced: 2 } && accepted.Accepted.All(baseline => baseline.ReplacedReferences), "both targets, both cases' references");
            var references = Updated();
            host.Clock.Advance(TimeSpan.FromMinutes(1));
            var retried = host.Suites.AcceptBaseline(suite.Id, first.Id, null, replaceReferences: true);
            Check(retried is { Accepted.Count: 0, Cleared.Count: 0, ReferencesReplaced: 0 }, "the same accept again (a retried POST): nothing accepted, nothing replaced");
            Equal(2, Rows(), "one baseline row per target, no duplicate");
            Check(Updated().SequenceEqual(references), "no reference output rewritten");
            Equal(0, host.Suites.AcceptBaseline(suite.Id, first.Id, null, replaceReferences: false).Accepted.Count, "without replacing: nothing either");

            // A baseline accepted without its references, then accepted again replacing them: that is a change, made once.
            var second = await SuRunNow(host, suite.Id);
            Check(host.Suites.AcceptBaseline(suite.Id, second.Id, [targets[0]], replaceReferences: false) is { Accepted.Count: 1, ReferencesReplaced: 0 }, "the first target, no references");
            var upgraded = host.Suites.AcceptBaseline(suite.Id, second.Id, null, replaceReferences: true);
            Check(upgraded is { Accepted.Count: 2, ReferencesReplaced: 2 } && upgraded.Accepted.All(baseline => baseline.RunId == second.Id && baseline.ReplacedReferences),
                "both targets now replace the references (the first one's baseline did not)");
            Equal(0, host.Suites.AcceptBaseline(suite.Id, second.Id, null, replaceReferences: true).Accepted.Count, "and again: nothing");
            Equal(5, Rows(), "2 + 1 + 2 rows, one per change");
            Check(host.Store.Baselines(suite.Id).Count == 2 && host.Store.Baselines(suite.Id).All(baseline => baseline.RunId == second.Id), "the current baselines are run #2's");
        }),

        Case("evals.suites.spend-guard-refusal-starts-nothing", async () =>
        {
            await using var host = SuHost.Create("su-guard");
            var (suite, _) = SuSeed(host, "Guarded", k: 2);
            await host.Ledger.SaveCapAsync(new SpendCap(0, CapScope.Everything, null, CapPeriod.Request, 1, CapAction.Block));
            var blocked = await host.Runner.StartAsync(suite.Id, new SuiteRunRequest());
            Check(blocked is { Posted: false, Run: null, Spend.Code: SpendErrors.Blocked }, "a Block cap refuses the group: " + blocked.Spend?.Code + blocked.Refused?.Code);
            Equal(0, host.Store.RunCount(suite.Id), "no run");
            Check((await host.Ledger.QueryAsync(new SpendLedgerQuery { Limit = SpendLedgerQuery.MaxLimit })).Items.All(row => row.State != ReservationState.Reserved),
                "nothing reserved");
            var spend = await RunEndpoints.SpendAsync(blocked.Spend!, host.Services);
            Check(spend["verdict"]?.GetValue<string>() == "block" && spend["canProceed"]?.GetValue<bool>() == false, "the confirm modal's answer: " + spend.ToJsonString());

            foreach (var cap in await host.Ledger.CapsAsync()) await host.Ledger.RemoveCapAsync(cap.Id);
            await host.Ledger.SaveCapAsync(new SpendCap(0, CapScope.Everything, null, CapPeriod.Request, 1, CapAction.Ask));
            var asked = await host.Runner.StartAsync(suite.Id, new SuiteRunRequest());
            Check(asked is { Posted: false, Spend.Code: SpendErrors.Ask } && asked.Spend.Call.Run is not null, "an Ask cap asks: " + asked.Spend?.Code);
            var runId = asked.Spend!.Call.Run!;
            Equal(0, host.Store.RunCount(suite.Id), "still no run");
            var answered = await host.Runner.StartAsync(suite.Id, new SuiteRunRequest { RunId = runId, Spend = new RunSpendAnswer(SpendChoice.ProceedOnce) });
            Check(answered is { Posted: true } && answered.Run!.Id == runId, "proceed once starts the run under the asked id: " + answered.Spend?.Message);
            var run = await SuFinished(host, runId);
            Check(run.Status == SuiteRunStatus.Complete && host.Store.Results(runId).All(result => result.Status == ResultStatus.Done),
                "every call went through with the answer: " + string.Join("; ", host.Store.Results(runId).Select(result => result.Error)));

            Check(host.Suites.SetSchedule(suite.Id, SuHourly, 1_000_000).Saved, "scheduled");
            host.Clock.Advance(TimeSpan.FromHours(1));
            var skipped = (await host.Scheduler.TickAsync()).Single();
            Check(skipped is { Outcome: SlotOutcome.SkippedRefused, RunId: null } && skipped.Detail!.StartsWith("The Spend Guard refused the run's reservation: ", StringComparison.Ordinal),
                "a scheduled run cannot ask: its slot is skipped: " + skipped.Detail);
            Check(host.Store.Suite(suite.Id)!.Schedule is { Active: true }, "the schedule stays on for the next slot");
            Equal(1, host.Store.RunCount(suite.Id), "no scheduled run");
        }),

        Case("evals.suites.response-model-is-stored-on-run-records", async () =>
        {
            await using var host = SuHost.Create("su-records");
            var store = host.Runs;
            store.Insert(new RunRecord
            {
                Id = "r-su-1", Op = "op-r-su-1", Kind = RunKind.Call, Source = "playground", Status = RunStatus.Running, Provider = "faux", Model = "echo",
                Created = SuStart, Started = SuStart, Updated = SuStart,
            });
            Check(store.Finish("r-su-1", new RunOutcome { Status = RunStatus.Completed, Finished = SuStart, ResponseModel = "  echo-2026-05  " }), "finished");
            Equal("echo-2026-05", store.Get("r-su-1")!.ResponseModel, "kept, trimmed");
            Equal("echo-2026-06", RunStore.ResponseModelOf(null, new JsonObject { ["responseModel"] = "echo-2026-06" }), "read from the output when not given");
            Equal(null, RunStore.ResponseModelOf(" ", new JsonObject { ["responseModel"] = 5 }), "a number is not a model");
            Equal(200, RunStore.ResponseModelOf(new string('m', 300), null)!.Length, "cut at 200");
            Equal("echo-2026-05", store.List(new RunQuery()).Items.Single().ResponseModel, "listed with it");
            var path = Path.Join(host.Options.DataDirectory, RunsDb.FileName);
            Equal("1,2", string.Join(",", ReadDb(path, "SELECT id FROM schema_migrations ORDER BY id", reader => reader.GetInt64(0))), "runs.db migration 2");
            Check(ReadDb(path, "PRAGMA table_info(runs)", reader => reader.GetString(1)).Contains("response_model"), "the response_model column");
        }),

        Case("evals.suites.api-crud-schedule-run-and-baseline", async () =>
        {
            var root = Temp("su-api");
            try
            {
                await using var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = Path.Join(root, "data"), Arguments = ["--faux-metered"] });
                async Task<JsonNode> Post(string path, JsonObject body, int status = 200)
                {
                    var answer = await app.SendAsync(HttpMethod.Post, path, body, origin: app.Origin);
                    Equal(status, answer.Status, "POST " + path + ": " + answer.Text);
                    Check(answer.Response.Headers.CacheControl?.NoStore == true, "never cached: " + path);
                    return answer.Body!;
                }
                async Task<JsonNode> Get(string path)
                {
                    var answer = await app.Get(path);
                    Equal(200, answer.Status, "GET " + path + ": " + answer.Text);
                    return answer.Body!;
                }
                var suiteId = EvalIds.New(EvalIds.Suite);
                var api = EvalRoutes.ApiSuites + "/" + suiteId;
                var suiteBody = new JsonObject
                {
                    ["id"] = suiteId, ["name"] = "API suite", ["k"] = 2,
                    ["targets"] = new JsonArray(new JsonObject { ["provider"] = "faux", ["model"] = "echo" }),
                };
                Check((await Post(EvalRoutes.ApiSuites, suiteBody))["created"]?.GetValue<bool>() == true, "made");
                var again = await Post(EvalRoutes.ApiSuites, (JsonObject)suiteBody.DeepClone());
                Check(again["created"]?.GetValue<bool>() == false && again["suite"]?["id"]?.GetValue<string>() == suiteId, "the same create again: the same suite");
                Equal(403, (await app.SendAsync(HttpMethod.Post, EvalRoutes.ApiSuites, new JsonObject { ["name"] = "x" }, origin: "http://evil.example")).Status, "no cross-origin writes");
                Equal(400, (await app.SendAsync(HttpMethod.Post, EvalRoutes.ApiSuites, new JsonObject { ["name"] = " " }, origin: app.Origin)).Status, "a nameless suite");

                var caseId = EvalIds.New(EvalIds.Case);
                var caseBody = new JsonObject
                {
                    ["id"] = caseId, ["suiteId"] = suiteId, ["name"] = "Echo the question",
                    ["spec"] = new JsonObject
                    {
                        ["provider"] = "faux", ["model"] = "echo", ["maxTokens"] = 256,
                        ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = "Is 1.0.0-rc.1 valid SemVer?" }),
                    },
                    ["checks"] = new JsonArray(new JsonObject { ["kind"] = "contains", ["text"] = "SemVer" }),
                };
                Check((await Post(EvalRoutes.ApiCases, caseBody))["created"]?.GetValue<bool>() == true, "case made");
                Check((await Post(EvalRoutes.ApiCases, (JsonObject)caseBody.DeepClone()))["created"]?.GetValue<bool>() == false, "made once");
                var renamed = await Post(EvalRoutes.ApiCases + "/" + caseId, new JsonObject { ["name"] = "Echo the SemVer question" });
                Equal("Echo the SemVer question", renamed["case"]?["name"]?.GetValue<string>(), "renamed");
                Equal(404, (await app.Get(EvalRoutes.ApiCases + "/" + EvalIds.New(EvalIds.Case))).Status, "no such case");

                var at = DateTime.UtcNow.AddHours(12).ToString("HH:mm", CultureInfo.InvariantCulture);
                var refused = await Post(api + "/schedule", new JsonObject
                    { ["enabled"] = true, ["repeat"] = "daily", ["at"] = at, ["timeZone"] = "UTC", ["capMicros"] = 1 });
                Check(refused["saved"]?.GetValue<bool>() == false && refused["refused"]?["code"]?.GetValue<string>() == "over-cap" &&
                    refused["estimate"]?["worstMicros"]?.GetValue<long>() > 1, "a $0.000001 cap is refused, with the worst case: " + refused.ToJsonString());
                var worst = refused["estimate"]!["worstMicros"]!.GetValue<long>();
                var saved = await Post(api + "/schedule", new JsonObject
                    { ["enabled"] = true, ["repeat"] = "daily", ["at"] = at, ["timeZone"] = "UTC", ["capMicros"] = worst });
                Check(saved["saved"]?.GetValue<bool>() == true && saved["next"] is not null && saved["schedule"]?["label"]?.GetValue<string>() == "Daily at " + at,
                    "saved with its next slot: " + saved.ToJsonString());

                var runId = EvalIds.New(EvalIds.Run);
                var started = await Post(api + "/run", new JsonObject { ["runId"] = runId });
                Check(started["posted"]?.GetValue<bool>() == true && started["run"]?["id"]?.GetValue<string>() == runId && started["url"]?.GetValue<string>() == EvalRoutes.Run(runId),
                    "run now: " + started.ToJsonString());
                Check((await Post(api + "/run", new JsonObject { ["runId"] = runId }))["run"]?["id"]?.GetValue<string>() == runId, "sent twice, one run");
                JsonNode? view = null;
                await WaitUntilAsync(async () =>
                {
                    view = await Get(EvalRoutes.ApiRuns + "/" + runId);
                    return view["run"]?["status"]?.GetValue<string>() is "complete" or "failed" or "cancelled";
                }, "the run ends", TimeSpan.FromSeconds(60));
                Check(view!["run"]?["status"]?.GetValue<string>() == "complete" && view["results"] is JsonArray { Count: 2 } results &&
                    results.All(result => result?["status"]?.GetValue<string>() == "done" && result["verdict"]?.GetValue<string>() == "passed"), "complete: " + view.ToJsonString());

                // Writes are idempotent: the same accept sent twice (a retry, a double click), replacing the references, is one baseline row.
                var accept = new JsonObject { ["runId"] = runId, ["replaceReferences"] = true };
                var baseline = await Post(api + "/baseline", (JsonObject)accept.DeepClone());
                Check(baseline["accepted"] is JsonArray { Count: 1 } && baseline["referencesReplaced"]?.GetValue<int>() == 1,
                    "accepted as the baseline, its answer the case's reference: " + baseline.ToJsonString());
                var replayed = await Post(api + "/baseline", (JsonObject)accept.DeepClone());
                Check(replayed["accepted"] is JsonArray { Count: 0 } && replayed["referencesReplaced"]?.GetValue<int>() == 0,
                    "the same accept sent twice: no new baseline, no reference rewritten: " + replayed.ToJsonString());
                long BaselineRows() => ReadDb(Path.Join(root, "data", "webagent.db"), "SELECT COUNT(*) FROM eval_baselines WHERE suite_id = '" + suiteId + "'",
                    reader => reader.GetInt64(0)).Single();
                Equal(1L, BaselineRows(), "one baseline row");
                Check((await Post(api + "/baseline", new JsonObject { ["runId"] = runId }))["accepted"] is JsonArray { Count: 0 }, "accepting twice: once");
                Equal(1L, BaselineRows(), "still one baseline row");
                var detail = await Get(api);
                Check(detail["overview"]?["estimate"]?["typicalMicros"]?.GetValue<long>() > 0 && detail["runs"] is JsonArray { Count: 1 } && detail["baselines"] is JsonArray { Count: 1 },
                    "the suite with its typical cost, runs and baseline: " + detail.ToJsonString()[..Math.Min(600, detail.ToJsonString().Length)]);
                var list = await Get(EvalRoutes.ApiSuites);
                Check(list["items"] is JsonArray { Count: 1 }, "listed");
                var drift = await Get(EvalRoutes.ApiDrift);
                Check(drift["suites"] is JsonArray suites && suites.Any(item => item?["suite"]?["id"]?.GetValue<string>() == suiteId), "on the drift page: " + drift.ToJsonString());
                Check((await Get(api + "/runs"))["total"]?.GetValue<int>() == 1, "one run listed");

                var off = await Post(api + "/schedule", new JsonObject { ["enabled"] = false });
                Check(off["saved"]?.GetValue<bool>() == true && off["suite"]?["schedule"]?["enabled"]?.GetValue<bool>() == false, "turned off");
                Check((await Post(EvalRoutes.ApiCases + "/" + caseId + "/delete", new JsonObject()))["deleted"]?.GetValue<bool>() == true, "case deleted");
                Check((await Post(EvalRoutes.ApiCases + "/" + caseId + "/delete", new JsonObject()))["deleted"]?.GetValue<bool>() == false, "deleting twice: once");
                Check((await Post(api + "/delete", new JsonObject()))["deleted"]?.GetValue<bool>() == true, "suite deleted");
                Equal(404, (await app.Get(api)).Status, "gone");
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),
    ];

    // ----- helpers -----

    /// <summary>A UTC time on the test day at <paramref name="hour"/>:00.</summary>
    private static DateTimeOffset SuAt(int hour) => new(SuStart.Year, SuStart.Month, SuStart.Day, hour, 0, 0, TimeSpan.Zero);

    /// <summary>A case of <paramref name="suiteId"/> asking faux/echo <paramref name="prompt"/> with at most 256 output tokens, checked for
    /// <c>Echo</c> unless <paramref name="checks"/> are given.</summary>
    private static TestCase SuCase(string suiteId, string name, string prompt, IReadOnlyList<EvalCheck>? checks = null) => new()
    {
        Id = EvalIds.New(EvalIds.Case),
        SuiteId = suiteId,
        Name = name,
        Spec = new RunSpec { Provider = "faux", Model = "echo", Messages = [new RunMessage { Role = "user", Text = prompt }], MaxTokens = 256 },
        Checks = checks ?? [new EvalCheck { Kind = CheckKind.Contains, Text = "Echo" }],
        Origin = new TestCaseOrigin(PinSource.Suite),
    };

    /// <summary>A suite on <paramref name="targets"/> (default faux/echo) with <paramref name="cases"/> cases named Case 1, Case 2, ...</summary>
    private static (Suite Suite, IReadOnlyList<TestCase> Cases) SuSeed(SuHost host, string name, IReadOnlyList<SuiteTarget>? targets = null, int k = 1,
        long? cap = 1_000_000, int cases = 1)
    {
        var suite = host.Suites.CreateSuite(new Suite { Id = EvalIds.New(EvalIds.Suite), Name = name, Targets = targets ?? [new SuiteTarget("faux", "echo")], K = k, CapMicros = cap }).Suite;
        var made = new List<TestCase>();
        for (var index = 1; index <= cases; index++)
            made.Add(host.Suites.CreateCase(SuCase(suite.Id, "Case " + index, $"Write a SemVer 2.0.0 regex, variant {index}.")).Case);
        return (host.Suites.RequireSuite(suite.Id), made);
    }

    /// <summary>The run <paramref name="runId"/> once it has ended (its drift stored).</summary>
    private static async Task<SuiteRun> SuFinished(SuHost host, string runId)
    {
        if (host.Runner.Completion(runId) is { } done) await done.WaitAsync(TimeSpan.FromSeconds(60));
        return host.Store.Run(runId) ?? throw new InvalidOperationException("There is no run " + runId + ".");
    }

    /// <summary>Runs the suite now and waits for it to end.</summary>
    private static async Task<SuiteRun> SuRunNow(SuHost host, string suiteId)
    {
        var start = await host.Runner.StartAsync(suiteId, new SuiteRunRequest());
        Check(start is { Posted: true, Run: not null }, "the run started: " + start.Refused?.Message + start.Spend?.Message);
        var run = await SuFinished(host, start.Run!.Id);
        Check(run.Status == SuiteRunStatus.Complete, $"run #{run.Number} complete: {EvalJson.Wire(run.Status)} {run.Problem}");
        return run;
    }

    /// <summary>Adds a probe to the webagent.db at <paramref name="db"/> (W5 fix 2): a trigger on eval_runs that, inside the UPDATE that marks
    /// a run complete, writes how many drift signals (and response-model ones) the run has stored by then into <c>su_completion_probe</c>.
    /// SQLite runs the trigger in the same statement as the update, so what it counts is exactly what a reader sees next to the complete
    /// run, whatever the timing.</summary>
    private static void SuCompletionProbe(string db)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = db, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE su_completion_probe (run_id TEXT NOT NULL, response_model INTEGER NOT NULL, signals INTEGER NOT NULL);
            CREATE TRIGGER su_completion_probe AFTER UPDATE OF status ON eval_runs
            WHEN NEW.status = 'complete' AND OLD.status <> 'complete'
            BEGIN
              INSERT INTO su_completion_probe (run_id, response_model, signals) VALUES (NEW.id,
                (SELECT COUNT(*) FROM eval_drift_signals WHERE run_id = NEW.id AND rule = 'response-model'),
                (SELECT COUNT(*) FROM eval_drift_signals WHERE run_id = NEW.id));
            END;
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>A clock that moves only when told to.</summary>
    internal sealed class SuClock(DateTimeOffset start) : TimeProvider
    {
        private long ticks = start.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }

    /// <summary>What the faux models were asked, and a gate that holds every answer until it is released.</summary>
    internal sealed class SuScript
    {
        private volatile TaskCompletionSource? gate;

        public ConcurrentQueue<string> Asked { get; } = new();

        public void Hold() => gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => Interlocked.Exchange(ref gate, null)?.TrySetResult();

        public async Task<string?> RespondAsync(FauxChat chat, CancellationToken cancellationToken)
        {
            Asked.Enqueue(chat.Text);
            if (gate is { } held) await held.Task.WaitAsync(cancellationToken);
            // The judge model (faux/echo-small) answers with a confident 4; every other model echoes.
            return chat.ModelId == "echo-small" ? "{\"label\": \"4\", \"confidence\": 0.9, \"rationale\": \"Restates it.\"}" : null;
        }
    }

    /// <summary>The priced faux catalog whose prices a test can scale (a catalog price change) or remove (an unknown price).</summary>
    internal sealed class SuCatalog(IModelCatalog inner) : IModelCatalog
    {
        private double scale = 1;

        public double Scale
        {
            get => Volatile.Read(ref scale);
            set => Volatile.Write(ref scale, value);
        }

        public ConcurrentBag<string> Unpriced { get; } = [];

        public IReadOnlyList<CatalogProvider> Providers => inner.Providers;

        public IReadOnlyList<CatalogModel> Models(string providerId)
        {
            var factor = Scale;
            return [.. inner.Models(providerId).Select(model => Unpriced.Contains(model.Id) ? model with { Cost = null }
                : model.Cost is { } cost ? model with { Cost = new CatalogCost(cost.Input * factor, cost.Output * factor, cost.CacheRead * factor, cost.CacheWrite * factor) }
                : model)];
        }
    }

    /// <summary>The priced faux models, naming <see cref="ResponseModel"/> as each answer's <c>responseModel</c> (as a provider's transport
    /// keeps it on the final message) when it is set.</summary>
    internal sealed class SuModels(MeteredFauxDurableModels inner) : IDurableModels
    {
        private volatile string? responseModel;

        public string? ResponseModel
        {
            get => responseModel;
            set => responseModel = value;
        }

        public DurableModel? GetModel(string provider, string modelId) => inner.GetModel(provider, modelId);

        public ImmutableArray<ChordObject> GetModelsOfType(string type, string? provider) => inner.GetModelsOfType(type, provider);

        public Task<ImmutableArray<ChordObject>> GetAvailableOfTypeAsync(string type, string? provider, CancellationToken cancellationToken) =>
            inner.GetAvailableOfTypeAsync(type, provider, cancellationToken);

        public ChordObject? GetModelOfType(string type, string provider, string id) => inner.GetModelOfType(type, provider, id);

        public Task<ChordObject> ClassifyAsync(ChordObject model, ChordObject context, CancellationToken cancellationToken) =>
            inner.ClassifyAsync(model, context, cancellationToken);

        public async IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<ChordJson> messages, DurableStreamOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var named = ResponseModel;
            await foreach (var item in inner.StreamSimple(model, messages, options, cancellationToken).ConfigureAwait(false))
                yield return item is StreamDone done && named is not null ? done with { Message = Named(done.Message, named) } : item;
        }

        public async Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<ChordJson> messages, DurableStreamOptions? options,
            CancellationToken cancellationToken)
        {
            StreamTerminalEvent? terminal = null;
            await foreach (var item in StreamSimple(model, messages, options, cancellationToken).ConfigureAwait(false))
                if (item is StreamTerminalEvent end) terminal = end;
            return terminal?.Message ?? throw new InvalidOperationException("The faux stream ended without a final message.");
        }

        public IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            inner.StreamDeferred(model, handle, cancellationToken);

        public Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            inner.FetchDeferredAsync(model, handle, cancellationToken);

        public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            inner.CancelDeferredAsync(model, handle, cancellationToken);

        private static AssistantMessage Named(AssistantMessage message, string name) => message with
        {
            ExtraProperties = (message.ExtraProperties ?? JsonFields.Empty).Set("responseModel", JsonData.Parse(JsonSerializer.Serialize(name))),
        };
    }

    /// <summary>The Web Agent's services in process (no web host): the Spend Guard, the run engine and the Evals area over
    /// <see cref="SuModels"/> and <see cref="SuCatalog"/>, on <see cref="SuClock"/>. Hosted services do not run; tests tick the scheduler.</summary>
    internal sealed class SuHost : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly string root;

        private SuHost(ServiceProvider provider, string root, WebAgentOptions options, SuClock clock, SuScript script, SuCatalog catalog, SuModels models)
        {
            this.provider = provider;
            this.root = root;
            Options = options;
            Clock = clock;
            Script = script;
            Catalog = catalog;
            Models = models;
        }

        public WebAgentOptions Options { get; }

        public SuClock Clock { get; }

        public SuScript Script { get; }

        public SuCatalog Catalog { get; }

        public SuModels Models { get; }

        public IServiceProvider Services => provider;

        public SuiteService Suites => provider.GetRequiredService<SuiteService>();

        public SuiteRunner Runner => provider.GetRequiredService<SuiteRunner>();

        public EvalScheduler Scheduler => provider.GetRequiredService<EvalScheduler>();

        public EvalStore Store => provider.GetRequiredService<EvalStore>();

        public RunStore Runs => provider.GetRequiredService<RunStore>();

        public ISpendLedger Ledger => provider.GetRequiredService<ISpendLedger>();

        public static SuHost Create(string name)
        {
            var root = Temp(name);
            // Faux without --faux-metered: the priced models and catalog are this host's own (below), registered before the guard wraps them.
            var options = new WebAgentOptions { DataDirectory = Path.Join(root, "data"), Faux = true };
            options.EnsureDirectories();
            var clock = new SuClock(SuStart);
            var script = new SuScript();
            var faux = new FauxDurableModels([FauxDurableModels.Echo, FauxDurableModels.EchoSmall, FauxModelCatalog.EchoThinking]) { RespondAsync = script.RespondAsync };
            var catalog = new SuCatalog(new FauxModelCatalog(faux, FauxPricing.Price));
            var models = new SuModels(new MeteredFauxDurableModels(faux, FauxPricing.Price));
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton(faux);
            services.AddWebAgentCore(options);
            services.AddWebAgentFallbacks(options);
            services.RemoveAll<IDurableModels>();
            services.RemoveAll<IModelCatalog>();
            services.AddSingleton<IModelCatalog>(catalog);
            services.AddSingleton<IDurableModels>(models);
            services.AddSpendArea(options);
            services.AddRunsArea(options);
            EvalsModule.AddEvalsArea(services, options);
            var provider = services.BuildServiceProvider();
            provider.GetRequiredService<WebAgentDb>().Migrate();
            return new SuHost(provider, root, options, clock, script, catalog, models);
        }

        public async ValueTask DisposeAsync()
        {
            Script.Release();
            await provider.DisposeAsync();
            SqliteClearPools();
            DeleteTemp(root);
        }
    }
}
