using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Evals;
using PiSharp.WebAgent.Evals.Drift;
using PiSharp.WebAgent.Faux;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Routing;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Spend;
using EvalCheck = PiSharp.WebAgent.Evals.Check;
using FrozenModelCatalog = PiSharp.AI.Catalogs.FrozenModelCatalog;
using static System.FormattableString;

// Decision 0011 (run W5, E3): Drift Watch and Price Watch. The statistics are exact (the sign test against binomial sums, type-7
// percentiles, SimHash distances); seeded simulations of a 16-case suite at k 3 show what the plan asks of the rules: over 1,000 stable
// trials (a fresh baseline and two runs each) drift is raised in at most 1% of them, and a fall in pass rate from 0.9 to 0.5 is drift by the
// second run in at least 90% of trials; every rule's message matches the drift mockup's wording, a suspect signal becomes drift only when
// the next run raises it again (a broken case only for the same case), a response-model change is drift at once and carries on, info is
// replaced each run, and a new baseline clears everything. Price Watch saves a snapshot when, and only when, a provider's catalog hash
// changes (a built-in provider's is its shard's ProviderJsonSha256), lists the rates that moved, prices a suite run as the Spend Guard does,
// and says which schedules to pause and what each change does to each suite. Everything is offline: fake catalogs, the faux app.
internal static partial class Program
{
    private static readonly SuiteTarget DwTarget = new("dwp", "dw-model");
    private static readonly DateTimeOffset DwNow = new(2026, 10, 30, 6, 12, 0, TimeSpan.Zero);
    private static readonly double[] DwZipf = DwCumulative(80);

    internal static IEnumerable<(string, Func<Task>)> EvalDriftCases() =>
    [
        Case("evals.drift.sign-test-and-percentiles-are-exact", () =>
        {
            Equal(0.03125, DriftStatistics.SignTest(5, 0), "5 worse, none better: 1/32");
            Check(Math.Abs(DriftStatistics.SignTest(7, 1) - 9.0 / 256) < 1e-15, "7 worse, 1 better: 9/256");
            Check(Math.Abs(DriftStatistics.SignTest(3, 3) - 42.0 / 64) < 1e-15, "3 and 3: 42/64");
            Check(Math.Abs(DriftStatistics.SignTest(20, 0) - Math.Pow(2, -20)) < 1e-18, "20 worse: 2^-20");
            Check(DriftStatistics.SignTest(60, 40) is > 0.0284 and < 0.0285, "60 of 100: 0.02844 (a large n stays exact)");
            Equal(1.0, DriftStatistics.SignTest(0, 4), "none worse");
            Equal(1.0, DriftStatistics.SignTest(0, 0), "no paired change");
            Equal(2.5, DriftStatistics.Percentile([4, 1, 3, 2], 0.5), "the median of four");
            Check(Math.Abs(DriftStatistics.Percentile([4, 1, 3, 2], 0.1)!.Value - 1.3) < 1e-12, "type 7: the 10th percentile of 1..4 is 1.3");
            Check(Math.Abs(DriftStatistics.Percentile([4, 1, 3, 2], 0.9)!.Value - 3.7) < 1e-12, "the 90th is 3.7");
            Equal(7.0, DriftStatistics.Percentile([7], 0.9), "one value");
            Check(DriftStatistics.Percentile([], 0.5) is null, "none");
            Equal(2.0, DriftStatistics.Median([3, 1, 2]), "the median of three");
        }),

        Case("evals.drift.answer-fingerprint-is-a-stable-simhash", () =>
        {
            Check(AnswerFingerprint.Of(null) is null && AnswerFingerprint.Of(" ... !? ") is null, "no words, no fingerprint");
            Equal(AnswerFingerprint.Of("The quick brown fox"), AnswerFingerprint.Of("the QUICK, brown fox!"), "case and punctuation do not count");
            var words = string.Join(" ", Enumerable.Range(0, 60).Select(index => "word" + (index * 7 % 23).ToString(CultureInfo.InvariantCulture)));
            var edited = words.Replace("word5 word12", "other nine", StringComparison.Ordinal);
            var unrelated = string.Join(" ", Enumerable.Range(0, 60).Select(index => "term" + (index * 5 % 29).ToString(CultureInfo.InvariantCulture)));
            var print = AnswerFingerprint.Of(words)!.Value;
            Equal(print, AnswerFingerprint.Of(words), "the same text, the same fingerprint");
            Equal(0, AnswerFingerprint.Distance(print, print), "no distance to itself");
            Equal(64, AnswerFingerprint.Distance(0, -1), "every bit");
            var near = AnswerFingerprint.Distance(print, AnswerFingerprint.Of(edited)!.Value);
            var far = AnswerFingerprint.Distance(print, AnswerFingerprint.Of(unrelated)!.Value);
            Check(near <= 12 && far >= 20 && near < far, $"a small edit stays near ({near} bits), another answer is far ({far} bits)");
        }),

        Case("evals.drift.stable-runs-raise-drift-in-at-most-1-percent", () =>
        {
            // A mixed suite: 16 cases from always-passing to flaky (0.6), six with an LLM judge, latency, speed and cost that vary per answer,
            // and answers drawn afresh each time from each case's own vocabulary. Each trial is a fresh baseline and two runs of the same model.
            // The seed is the first one tried; 6,000 trials with another seed put the rate at about 0.55% (most of it a flaky case failing all
            // three repeats twice running).
            var shapes = DwShapes([1.0, 0.97, 0.95, 0.9, 0.85, 0.8, 0.7, 0.6, 1.0, 0.97, 0.95, 0.9, 0.85, 0.8, 0.7, 0.6], judged: 6);
            var random = new EvalRandom(20261030);
            int drifted = 0, suspected = 0, informed = 0;
            var rules = new SortedDictionary<string, int>(StringComparer.Ordinal);
            const int Trials = 1000;
            for (var trial = 0; trial < Trials; trial++)
            {
                var suite = EvalIds.New(EvalIds.Suite);
                var (baselineRun, baselineResults) = DwSimulate(suite, 1, shapes, 3, random);
                var history = new DwHistory(baselineRun, baselineResults);
                var raised = new List<DriftSignal>();
                for (var number = 2; number <= 3; number++)
                {
                    var (run, results) = DwSimulate(suite, number, shapes, 3, random);
                    raised.AddRange(history.Analyze(run, results).Raised);
                }
                if (raised.Any(signal => signal.Level == DriftLevel.Drift))
                {
                    drifted++;
                    foreach (var rule in raised.Where(signal => signal.Level == DriftLevel.Drift).Select(signal => EvalJson.Wire(signal.Rule)).Distinct())
                        rules[rule] = rules.GetValueOrDefault(rule) + 1;
                }
                if (raised.Any(signal => signal.Level == DriftLevel.Suspect)) suspected++;
                if (raised.Any(signal => signal.Level == DriftLevel.Info)) informed++;
            }
            Console.Error.WriteLine($"evals.drift stable: drift in {drifted} of {Trials} trials ({string.Join(", ", rules.Select(pair => pair.Key + " " + pair.Value))}); " +
                $"a suspect signal in {suspected}, info in {informed}.");
            Check(drifted <= Trials / 100, $"drift in {drifted} of {Trials} stable trials (at most 1%)");
            Check(suspected > drifted, "suspect signals happen and most are not confirmed (the confirmation is what keeps drift rare)");
        }),

        Case("evals.drift.a-drop-from-0.9-to-0.5-is-caught-within-two-runs", () =>
        {
            var shapes = DwShapes([.. Enumerable.Repeat(0.9, 16)], judged: 0);
            var random = new EvalRandom(905);
            var dropped = new DwBehaviour { Pass = _ => 0.5 };
            const int Trials = 500;
            int caught = 0, first = 0;
            for (var trial = 0; trial < Trials; trial++)
            {
                var suite = EvalIds.New(EvalIds.Suite);
                var (baselineRun, baselineResults) = DwSimulate(suite, 1, shapes, 3, random);
                var history = new DwHistory(baselineRun, baselineResults);
                var (runA, resultsA) = DwSimulate(suite, 2, shapes, 3, random, dropped);
                var a = history.Analyze(runA, resultsA);
                if (a.Raised.Any(signal => signal.Rule == DriftRule.PassRate)) first++;
                var (runB, resultsB) = DwSimulate(suite, 3, shapes, 3, random, dropped);
                var b = history.Analyze(runB, resultsB);
                if (b.Level == DriftLevel.Drift && b.Open.Any(signal => signal.Level == DriftLevel.Drift && signal.Rule is DriftRule.PassRate or DriftRule.BrokenCase)) caught++;
            }
            Console.Error.WriteLine($"evals.drift drop 0.9 → 0.5: drift by the second run in {caught} of {Trials} trials; the first run was suspect in {first}.");
            Check(caught >= Trials * 9 / 10, $"caught in {caught} of {Trials} trials (at least 90%)");
        }),

        Case("evals.drift.pass-rate-suspect-is-confirmed-by-the-next-run", () =>
        {
            // The mockup's row: 8 cases at k 3, 22 → 17 of 24 answers, 5 cases worse and none better.
            var suite = EvalIds.New(EvalIds.Suite);
            var cases = DwIds(8);
            var baselineRun = DwRunOf(suite, 19, cases, 3);
            var history = new DwHistory(baselineRun, DwAnswers(baselineRun, cases, [3, 3, 3, 3, 3, 3, 2, 2]));
            var runA = DwRunOf(suite, 20, cases, 3);
            var a = history.Analyze(runA, DwAnswers(runA, cases, [2, 2, 2, 2, 2, 3, 2, 2]));
            Equal(1, a.Raised.Count, "one signal");
            var suspect = a.Raised[0];
            Equal(DriftRule.PassRate, suspect.Rule, "the pass-rate rule");
            Equal(DriftLevel.Suspect, suspect.Level, "suspect first");
            Equal("Pass rate fell from 0.92 to 0.71 (22 → 17 of 24 answers). 5 of 8 cases got worse and none improved (one-sided sign test, p = 0.031).",
                suspect.Message, "the mockup's message");
            Equal(0.03125, suspect.PValue, "the exact p-value");
            Equal(runA.Id, suspect.FirstRunId, "the run that saw it first");
            Equal(DriftLevel.Suspect, a.Level, "the target is suspect");
            Equal("Waiting for the next run to confirm it.", DriftText.Lineage(history.Open.Single(), 20, 20), "the row's lineage");
            Equal("", a.Comparison.OtherSignals([DriftRule.PassRate]), "no other numbers to show");

            var runB = DwRunOf(suite, 21, cases, 3);
            var b = history.Analyze(runB, DwAnswers(runB, cases, [2, 2, 2, 1, 2, 3, 2, 2]));
            Equal(1, b.Raised.Count, "one signal carries it on");
            var drift = b.Raised[0];
            Equal(DriftLevel.Drift, drift.Level, "confirmed: drift");
            Equal(runB.Id, drift.RunId, "raised by the confirming run");
            Equal(runA.Id, drift.FirstRunId, "remembering the run that saw it first");
            Check(drift.Message.StartsWith("Pass rate fell from 0.92 to 0.67 (22 → 16 of 24 answers).", StringComparison.Ordinal), "the confirming run's numbers: " + drift.Message);
            Equal(1, b.Closed.Count, "the suspect closes");
            Equal(DriftClosed.Confirmed, DriftClosed.Reason(b.Closed[0]), "as confirmed");
            Equal(runB.Id, b.Closed[0].Detail![DriftClosed.ByKey]!.GetValue<string>(), "by the confirming run");
            Equal(b.Closed[0].Created, suspect.Created, "the closed row is the original signal");
            Equal(DriftLevel.Drift, b.Level, "the target drifted");
            Equal("Run #20 saw it first and run #21 saw it again.", DriftText.Lineage(history.Open.Single(), 20, 21), "the drift row's lineage");

            // Drift stays open while later runs are fine, until a new baseline clears it.
            var runC = DwRunOf(suite, 22, cases, 3);
            var c = history.Analyze(runC, DwAnswers(runC, cases, [3, 3, 3, 3, 3, 3, 2, 2]));
            Check(c.Raised.Count == 0 && c.Closed.Count == 0 && c.Level == DriftLevel.Drift, "drift does not go away by itself");
            Equal("No changes against the baseline. Pass rate 0.92 → 0.92.", c.Comparison.OkText, "the numbers of a quiet run");
            var cleared = DriftAnalyzer.Clear(history.Open, runC.Id, DwNow);
            Equal(1, cleared.Count, "the new baseline closes it");
            Equal(DriftClosed.Baseline, DriftClosed.Reason(cleared[0]), "closed by the baseline");
            Equal(DriftLevel.Ok, DriftRules.Worst(cleared), "nothing open after");
        }),

        Case("evals.drift.an-unconfirmed-suspect-closes", () =>
        {
            var suite = EvalIds.New(EvalIds.Suite);
            var cases = DwIds(8);
            var baselineRun = DwRunOf(suite, 1, cases, 3);
            var history = new DwHistory(baselineRun, DwAnswers(baselineRun, cases, [3, 3, 3, 3, 3, 3, 2, 2]));
            var runA = DwRunOf(suite, 2, cases, 3);
            Equal(DriftLevel.Suspect, history.Analyze(runA, DwAnswers(runA, cases, [2, 2, 2, 2, 2, 3, 2, 2])).Level, "suspect");
            var runB = DwRunOf(suite, 3, cases, 3);
            var b = history.Analyze(runB, DwAnswers(runB, cases, [3, 3, 3, 2, 3, 3, 2, 2]));
            Equal(0, b.Raised.Count, "nothing new");
            Equal(DriftClosed.NotConfirmed, DriftClosed.Reason(b.Closed.Single()), "the suspect closes, not confirmed");
            Equal(DriftLevel.Ok, b.Level, "the target is ok again");
            Equal("Pass rate 0.92 → 0.88 (22 → 21 of 24), not significant.", b.Comparison.OtherSignals([]), "other signals");
            Check(history.Open.Count == 0, "nothing open");
        }),

        Case("evals.drift.response-model-change-is-drift-at-once-and-carries-on", () =>
        {
            var suite = EvalIds.New(EvalIds.Suite);
            var cases = DwIds(4);
            var baselineRun = DwRunOf(suite, 1, cases, 2);
            var baselineResults = DwAnswers(baselineRun, cases, [2, 2, 2, 2], "dw-model-2026-05");
            var history = new DwHistory(baselineRun, baselineResults, ["dw-model-2026-05"]);
            var runA = DwRunOf(suite, 20, cases, 2);
            var a = history.Analyze(runA, DwAnswers(runA, cases, [2, 2, 2, 2], "dw-model-2026-09"));
            var changed = a.Raised.Single();
            Equal(DriftRule.ResponseModel, changed.Rule, "the response-model rule");
            Equal(DriftLevel.Drift, changed.Level, "drift at once (the plan's table)");
            Equal("The response model changed from dw-model-2026-05 to dw-model-2026-09.", changed.Message, "the message");
            Equal(DriftLevel.Drift, a.Level, "the target drifted");

            var runB = DwRunOf(suite, 21, cases, 2);
            var b = history.Analyze(runB, DwAnswers(runB, cases, [2, 2, 2, 2], "dw-model-2026-09"));
            var again = b.Raised.Single();
            Equal(DriftLevel.Drift, again.Level, "still drift");
            Equal(runA.Id, again.FirstRunId, "the run that saw it first is kept");
            Equal(DriftClosed.SeenAgain, DriftClosed.Reason(b.Closed.Single()), "the earlier row closes as seen again");
            Equal(1, history.Open.Count, "one open signal");
            Equal("Run #20 saw it first and run #21 saw it again.", DriftText.Lineage(history.Open.Single(), 20, 21), "after the confirming run");

            var runC = DwRunOf(suite, 22, cases, 2);
            var mixed = DwAnswers(runC, cases, [2, 2, 2, 2], "dw-model-2026-05");
            mixed[0] = mixed[0] with { ResponseModel = "dw-model-2026-11" };
            var c = history.Analyze(runC, mixed);
            Equal("The response model changed: some answers came from dw-model-2026-11 instead of dw-model-2026-05.", c.Raised.Single().Message, "a partial change");
            Equal(runA.Id, c.Raised.Single().FirstRunId, "the same rule seen again carries the drift on");
            Equal(DriftClosed.SeenAgain, DriftClosed.Reason(c.Closed.Single()), "replacing the earlier row");
            var runD = DwRunOf(suite, 23, cases, 2);
            var d = history.Analyze(runD, DwAnswers(runD, cases, [2, 2, 2, 2], "dw-model-2026-05"));
            Check(d.Raised.Count == 0 && d.Closed.Count == 0 && d.Level == DriftLevel.Drift && history.Open.Count == 1, "drift is never closed by a run");

            // The baseline's own response models count, not only its results'.
            var accepted = new DwHistory(baselineRun, baselineResults, ["dw-model-2026-05", "dw-model-2026-09"]);
            Check(accepted.Analyze(runA, DwAnswers(runA, cases, [2, 2, 2, 2], "dw-model-2026-09")).Raised.Count == 0, "a model the baseline accepted is no change");
        }),

        Case("evals.drift.broken-case-is-confirmed-only-by-the-same-case", () =>
        {
            var suite = EvalIds.New(EvalIds.Suite);
            var cases = DwIds(4);
            var names = new Dictionary<string, string> { [cases[0]] = "Extract invoice total", [cases[1]] = "Parse ISO dates", [cases[2]] = "SemVer 2.0.0", [cases[3]] = "Unicode escapes" };
            var baselineRun = DwRunOf(suite, 1, cases, 3);
            var history = new DwHistory(baselineRun, DwAnswers(baselineRun, cases, [3, 3, 3, 3])) { Names = names };
            var runA = DwRunOf(suite, 2, cases, 3);
            var a = history.Analyze(runA, DwAnswers(runA, cases, [3, 0, 3, 3]));
            var broken = a.Raised.Single();
            Equal(DriftRule.BrokenCase, broken.Rule, "a broken case (one case cannot make the pass-rate test significant)");
            Equal(DriftLevel.Suspect, broken.Level, "suspect");
            Equal("“Parse ISO dates” passed all 3 repeats in the baseline and fails all 3 now.", broken.Message, "the mockup's message");
            Equal(cases[1], broken.Detail!["cases"]!.AsArray().Single()!.GetValue<string>(), "the case is recorded");

            var runB = DwRunOf(suite, 3, cases, 3);
            var b = history.Analyze(runB, DwAnswers(runB, cases, [3, 3, 0, 3]));
            Equal(DriftClosed.NotConfirmed, DriftClosed.Reason(b.Closed.Single()), "another case breaking does not confirm the first");
            Equal(DriftLevel.Suspect, b.Raised.Single().Level, "the other case is a new suspect");
            Equal("“SemVer 2.0.0” passed all 3 repeats in the baseline and fails all 3 now.", b.Raised.Single().Message, "named");

            var runC = DwRunOf(suite, 4, cases, 3);
            var c = history.Analyze(runC, DwAnswers(runC, cases, [3, 3, 0, 0]));
            var confirmed = c.Raised.Single(signal => signal.Rule == DriftRule.BrokenCase);
            Equal(DriftLevel.Drift, confirmed.Level, "the same case broken again: drift");
            Equal(runB.Id, confirmed.FirstRunId, "first seen by the run before");
            Equal("2 cases passed all their repeats in the baseline and fail all of them now: “SemVer 2.0.0” and “Unicode escapes”.", confirmed.Message,
                "several cases");

            // Below k 3 a flaky case passes all and fails all too often to call it broken.
            var twice = DwRunOf(suite, 1, cases, 2);
            var few = new DwHistory(twice, DwAnswers(twice, cases, [2, 2, 2, 2]));
            var runD = DwRunOf(suite, 2, cases, 2);
            Check(few.Analyze(runD, DwAnswers(runD, cases, [2, 0, 2, 2])).Raised.Count == 0, "no broken case at k 2");
        }),

        Case("evals.drift.judge-score-falls", () =>
        {
            var suite = EvalIds.New(EvalIds.Suite);
            var cases = DwIds(6);
            var baselineRun = DwRunOf(suite, 1, cases, 3);
            var history = new DwHistory(baselineRun, DwJudged(baselineRun, cases, 5));
            var runA = DwRunOf(suite, 2, cases, 3);
            var a = history.Analyze(runA, DwJudged(runA, cases, 3));
            var judge = a.Raised.Single();
            Equal(DriftRule.JudgeScore, judge.Rule, "the judge rule");
            Equal(DriftLevel.Suspect, judge.Level, "suspect");
            Equal("Judge score fell from 5.0 to 3.0 (out of 5). 6 of 6 judged cases scored lower and none higher (one-sided sign test, p = 0.016).",
                judge.Message, "the message");
            var runB = DwRunOf(suite, 3, cases, 3);
            Check(history.Analyze(runB, DwJudged(runB, cases, 4.75)).Raised.Count == 0, "a fall under half a point is not a signal");
        }),

        Case("evals.drift.latency-and-tokens-per-second-are-info", () =>
        {
            var suite = EvalIds.New(EvalIds.Suite);
            var cases = DwIds(11);
            long[] latencies = [4000, 4200, 4500, 4700, 4900, 5000, 5200, 5500, 5800, 6000, 6500];
            double[] speeds = [60, 70, 72, 75, 78, 80, 82, 85, 88, 90, 95];
            var baselineRun = DwRunOf(suite, 1, cases, 1);
            var baselineResults = DwAnswers(baselineRun, cases, [.. cases.Select(_ => 1)]).Select((result, index) => result with { DurationMs = latencies[index], TokensPerSecond = speeds[index] }).ToList();
            var history = new DwHistory(baselineRun, baselineResults);
            var runA = DwRunOf(suite, 2, cases, 1);
            var a = history.Analyze(runA, [.. DwAnswers(runA, cases, [.. cases.Select(_ => 1)]).Select(result => result with { DurationMs = 8100, TokensPerSecond = 35 })]);
            Equal(2, a.Raised.Count, "two signals");
            Check(a.Raised.All(signal => signal.Level == DriftLevel.Info), "both info");
            Equal("Latency +62%: median 5.0 s → 8.1 s, outside the baseline's 4.2–6.0 s range (10th to 90th percentile).",
                a.Raised.Single(signal => signal.Rule == DriftRule.Latency).Message, "the mockup's latency message");
            Equal("Tokens/s −56%: median 80 → 35, outside the baseline's 70–90 range (10th to 90th percentile).",
                a.Raised.Single(signal => signal.Rule == DriftRule.TokensPerSecond).Message, "the speed message");
            Equal(DriftLevel.Info, a.Level, "the target shows info");

            var runB = DwRunOf(suite, 3, cases, 1);
            var b = history.Analyze(runB, [.. DwAnswers(runB, cases, [.. cases.Select(_ => 1)]).Select(result => result with { DurationMs = 7000, TokensPerSecond = 80 })]);
            Check(b.Raised.Count == 0, "+40% is not a signal");
            Check(b.Closed.Count == 2 && b.Closed.All(signal => DriftClosed.Reason(signal) == DriftClosed.Superseded), "info is replaced by each run");
            Equal(DriftLevel.Ok, b.Level, "ok again");
            Equal("Pass rate 1.00 → 1.00 (11 → 11 of 11). Median latency 5.0 s → 7.0 s.", b.Comparison.OtherSignals([]), "other signals");
        }),

        Case("evals.drift.cost-splits-price-change-and-verbosity", () =>
        {
            var suite = EvalIds.New(EvalIds.Suite);
            var cases = DwIds(5);
            var baselineRun = DwRunOf(suite, 1, cases, 2);
            var baselineResults = DwAnswers(baselineRun, cases, [2, 2, 2, 2, 2]).Select(result => result with { InputTokens = 1000, OutputTokens = 1500, CostMicros = 16_000 }).ToList();
            var runA = DwRunOf(suite, 2, cases, 2);
            var resultsA = DwAnswers(runA, cases, [2, 2, 2, 2, 2]).Select(result => result with { InputTokens = 1000, OutputTokens = 1600, CostMicros = 21_000 }).ToList();
            var priced = new DwHistory(baselineRun, baselineResults).Analyze(runA, resultsA, new CatalogCost(1, 10), new CatalogCost(1, 12.5));
            var cost = priced.Raised.Single();
            Equal(DriftRule.Cost, cost.Rule, "the cost rule");
            Equal(DriftLevel.Suspect, cost.Level, "suspect");
            Equal("Cost per run +31%: $0.16 → $0.21 (price change +$0.04, verbosity +$0.01; output tokens per answer 1500 → 1600).", cost.Message, "split");
            Equal(40_000L, cost.Detail!["priceMicros"]!.GetValue<long>(), "price: 1,600 tokens × $2.50/M × 10 answers");
            Equal(10_000L, cost.Detail!["verbosityMicros"]!.GetValue<long>(), "verbosity: the rest");
            Equal(0.3125, cost.Effect, "the effect");

            var unpriced = new DwHistory(baselineRun, baselineResults).Analyze(runA, resultsA).Raised.Single();
            Equal("Cost per run +31%: $0.16 → $0.21 (output tokens per answer 1500 → 1600; no price history to split it into price and verbosity).", unpriced.Message,
                "without price history");

            var runB = DwRunOf(suite, 3, cases, 2);
            var small = new DwHistory(baselineRun, baselineResults).Analyze(runB, [.. DwAnswers(runB, cases, [2, 2, 2, 2, 2]).Select(result => result with { CostMicros = 19_200 })]);
            Check(small.Raised.Count == 0, "+20% is not a signal");
            Equal("Pass rate 1.00 → 1.00 (10 → 10 of 10). Cost per run $0.16 → $0.19.", small.Comparison.OtherSignals([]), "shown as another signal");
        }),

        Case("evals.drift.fingerprint-moves-when-answers-change", () =>
        {
            var suite = EvalIds.New(EvalIds.Suite);
            var shapes = DwShapes([1, 1, 1, 1, 1, 1], judged: 0);
            var random = new EvalRandom(77);
            var (baselineRun, baselineResults) = DwSimulate(suite, 1, shapes, 3, random);
            var history = new DwHistory(baselineRun, baselineResults);
            var (same, sameResults) = DwSimulate(suite, 2, shapes, 3, random);
            var steady = history.Analyze(same, sameResults);
            Check(steady.Raised.Count == 0, "answers drawn the same way stay within the spread: " + string.Join(" | ", steady.Raised.Select(signal => signal.Message)));
            Check(steady.Comparison.FingerprintCases == 6, "every case compared");
            var (moved, movedResults) = DwSimulate(suite, 3, shapes, 3, random, new DwBehaviour { Vocabulary = "n" });
            var changed = history.Analyze(moved, movedResults).Raised.Single();
            Equal(DriftRule.Fingerprint, changed.Rule, "the fingerprint rule");
            Equal(DriftLevel.Suspect, changed.Level, "suspect");
            Check(changed.Message.StartsWith("Answers changed: 6 of 6 cases answered outside the baseline's own spread (SimHash distance ", StringComparison.Ordinal),
                "the message: " + changed.Message);
            Equal(6, changed.Detail!["cases"]!.AsArray().Count, "the moved cases are recorded");
            var (pairRun, pairResults) = DwSimulate(suite, 1, shapes, 2, random);
            var (pairMoved, pairMovedResults) = DwSimulate(suite, 2, shapes, 2, random, new DwBehaviour { Vocabulary = "n" });
            var pair = new DwHistory(pairRun, pairResults).Analyze(pairMoved, pairMovedResults);
            Check(pair.Comparison.FingerprintCases == 0 && pair.Raised.All(signal => signal.Rule != DriftRule.Fingerprint), "two baseline answers are too few for a spread");
        }),

        Case("evals.drift.analysis-is-order-independent-idempotent-and-checked", () =>
        {
            var suite = EvalIds.New(EvalIds.Suite);
            var shapes = DwShapes([.. Enumerable.Repeat(0.9, 12)], judged: 4);
            var random = new EvalRandom(4242);
            var (baselineRun, baselineResults) = DwSimulate(suite, 1, shapes, 3, random);
            var (run, results) = DwSimulate(suite, 2, shapes, 3, random, new DwBehaviour { Pass = _ => 0.3, ResponseModel = "dw-model-2026-09" });
            var shuffled = results.ToList();
            random.Shuffle(shuffled);
            var reversedBaseline = baselineResults.AsEnumerable().Reverse().ToList();
            var first = new DwHistory(baselineRun, baselineResults).Analyze(run, results);
            var second = new DwHistory(baselineRun, reversedBaseline).Analyze(run, shuffled);
            Check(first.Raised.Count >= 2, "the run raises signals");
            Equal(string.Join("\n", first.Raised.Select(signal => signal.Message)), string.Join("\n", second.Raised.Select(signal => signal.Message)), "the order of results does not count");

            var history = new DwHistory(baselineRun, baselineResults);
            var analysed = history.Analyze(run, results);
            var again = history.Analyze(run, results);
            Check(again.AlreadyAnalysed && again.Raised.Count == 0 && again.Closed.Count == 0, "analysing a run again changes nothing");
            Equal(analysed.Level, again.Level, "the same level");

            var self = DriftAnalyzer.Analyze(new DriftInput(baselineRun, baselineResults, DwBaseline(baselineRun), baselineRun, baselineResults), DwNow);
            Check(self.Findings.Count == 0 && self.Raised.Count == 0, "the baseline run against itself finds nothing");

            var stranger = baselineRun with { SuiteId = EvalIds.New(EvalIds.Suite) };
            Throws<ArgumentException>(() => DriftAnalyzer.Analyze(new DriftInput(run, results, DwBaseline(baselineRun), stranger, baselineResults), DwNow), "another suite's baseline run");
            Throws<ArgumentException>(() => DriftAnalyzer.Analyze(new DriftInput(run, results, DwBaseline(run), baselineRun, baselineResults), DwNow), "a baseline of another run");

            // Other targets' results are ignored.
            var other = results.Select(result => result with { TargetId = "tother", Verdict = CheckVerdict.Failed, Status = ResultStatus.Done }).ToList();
            var withOthers = new DwHistory(baselineRun, baselineResults).Analyze(run, [.. results, .. other]);
            Equal(string.Join("\n", first.Raised.Select(signal => signal.Message)), string.Join("\n", withOthers.Raised.Select(signal => signal.Message)), "only the baseline's target counts");
        }),

        Case("evals.drift.price-snapshot-on-hash-change", () =>
        {
            var root = Temp("evals-drift-prices");
            try
            {
                var db = new WebAgentDb(Path.Join(root, "webagent.db"), [CoreMigrations.AppInfo, .. EvalMigrations.All]);
                db.Migrate();
                var store = new PriceSnapshotStore(db);
                var v1 = FrozenModelCatalog.ReadProviderJson("dwshard", DwProviderJson("dwshard", ("terra", 2, 12, 0.2, 2.5), ("luna", 0.5, 2, 0.05, 0.625)));
                var v2 = FrozenModelCatalog.ReadProviderJson("dwshard", DwProviderJson("dwshard", ("terra", 2, 14, 0.2, 2.5), ("luna", 0.5, 2, 0.05, 0.625)));
                Check(v1.ProviderJsonSha256 != v2.ProviderJsonSha256, "two catalog files");
                var current = v1;
                var clock = new DwClock(DwNow);
                var watch = new PriceWatch(new DwSource(now => [PriceSnapshots.From(current, now)]), store, time: clock);
                var events = new List<PriceCheck>();
                watch.Changed += events.Add;

                var first = watch.Check();
                Equal(1, first.Saved.Count, "the first check saves a snapshot");
                Equal(0, first.Changes.Count, "the first snapshot is not a change");
                Equal(v1.ProviderJsonSha256, first.Saved[0].Snapshot.CatalogSha256, "named by the shard's hash");
                Check(first.Saved[0].Snapshot.Id > 0 && first.Saved[0].Previous is null, "saved, with no previous");
                Equal("luna,terra", string.Join(",", first.Saved[0].Snapshot.Models.Select(model => model.Model)), "its chat models");
                Equal("dwshard.json", PriceSnapshots.FileName(first.Saved[0].Snapshot), "the file it names");

                clock.Now = DwNow.AddHours(1);
                Equal(0, watch.Check().Saved.Count, "the same hash saves nothing");
                Check(events.Count == 0, "no change yet");

                current = v2;
                clock.Now = DwNow.AddHours(2);
                var second = watch.Check();
                Equal(1, second.Saved.Count, "a new hash saves a snapshot");
                var row = second.Changes.Single();
                var change = row.Change;
                Equal("dwshard/terra/Output/12/14", Invariant($"{change.Provider}/{change.Model}/{change.Field}/{change.Before}/{change.After}"), "the rate that moved");
                Equal("output $12.00 → $14.00", change.Label, "the mockup's label");
                Equal(v1.ProviderJsonSha256 + ">" + v2.ProviderJsonSha256, change.BeforeSha256 + ">" + change.AfterSha256, "between the two files");
                Equal(DwNow.AddHours(2), change.Detected, "detected when the snapshot was taken");
                Equal(second.Saved[0].Snapshot.Id, row.SnapshotId, "beside its snapshot");
                Equal(1, events.Count, "Changed is raised for the check that saw a change");
                Equal(row.Id, events[0].Changes.Single().Id, "with the change");

                var path = Path.Join(root, "webagent.db");
                Equal(2L, ReadDb(path, "SELECT COUNT(*) FROM eval_price_snapshots", reader => reader.GetInt64(0)).Single(), "two snapshots");
                Equal("output|12|14", ReadDb(path, "SELECT field, before_rate, after_rate FROM eval_price_changes",
                    reader => Invariant($"{reader.GetString(0)}|{reader.GetDouble(1)}|{reader.GetDouble(2)}")).Single(), "one change row");

                var listed = watch.Changes().Single();
                Equal(row.Id, listed.Id, "listed");
                Equal(v1.ProviderJsonSha256, listed.Change.BeforeSha256, "the old hash comes from the snapshot before");
                Equal(12.0, watch.PriceAt("dwshard", "terra", DwNow.AddMinutes(30))!.Cost!.Output, "the price then");
                Equal(14.0, watch.PriceAt("dwshard", "terra", DwNow.AddHours(3))!.Cost!.Output, "the price now");
                Equal(12.0, watch.PriceAt("dwshard", "terra", DwNow.AddDays(-1))!.Cost!.Output, "before the first snapshot: the earliest");
                Check(watch.PriceAt("dwshard", "nope", DwNow) is null && watch.PriceAt("other", "terra", DwNow) is null, "unknown models and providers");

                current = v1;
                clock.Now = DwNow.AddHours(4);
                var back = watch.Check().Changes.Single();
                Equal("14>12", Invariant($"{back.Change.Before}>{back.Change.After}"), "a revert is a change too");
                Equal(1, watch.Changes(TimeSpan.FromMinutes(30)).Count, "the window");
                Equal(2, watch.Changes().Count, "the last 30 days, newest first");
                Equal(back.Id, watch.Changes()[0].Id, "newest first");
                Check(new PriceSnapshotStore(db).Record(PriceSnapshots.From(v1, DwNow.AddHours(5))) is null, "another store on the same file refuses the latest hash");
                Equal(3L, ReadDb(path, "SELECT COUNT(*) FROM eval_price_snapshots", reader => reader.GetInt64(0)).Single(), "three snapshots");
                Equal(watch.LastCheck!.At, DwNow.AddHours(4), "the last check");
            }
            finally { DeleteTemp(root); }
        }),

        Case("evals.drift.catalog-price-source-uses-the-shard-hash", () =>
        {
            var runtime = new RuntimeModelCatalog();
            var snapshots = new CatalogPriceSource(runtime).Snapshots(DwNow);
            var openai = snapshots.Single(snapshot => snapshot.Provider == "openai");
            Equal(PiSharp.Cli.Models.BuiltinModelCatalog.Get("openai").ProviderJsonSha256, openai.CatalogSha256, "a built-in provider is named by its shard's hash");
            Equal("openai.json", PriceSnapshots.FileName(openai), "openai.json changed");
            Check(openai.Models.Count > 0 && openai.Models.All(price => runtime.FindModel("openai", price.Model) is not null), "the routed models");
            Check(snapshots.Select(snapshot => snapshot.Provider).Distinct().Count() == snapshots.Count, "one snapshot per provider");

            var faux = new CatalogPriceSource(new FauxModelCatalog(FauxModelCatalog.CreateModels())).Snapshots(DwNow).Single();
            Equal("faux", faux.Provider, "the faux provider");
            Equal(PriceSnapshots.Sha256("faux", faux.Models), faux.CatalogSha256, "no shard: the hash of the prices");
            Equal("models.json", PriceSnapshots.FileName(faux), "no shard file");
            Equal(faux.CatalogSha256, new CatalogPriceSource(new FauxModelCatalog(FauxModelCatalog.CreateModels())).Snapshots(DwNow.AddDays(1)).Single().CatalogSha256,
                "the same prices, the same hash");
            var metered = new CatalogPriceSource(new FauxModelCatalog(FauxModelCatalog.CreateModels(), new CatalogCost(1, 2))).Snapshots(DwNow).Single();
            Check(metered.CatalogSha256 != faux.CatalogSha256, "a price change changes the hash");
            Equal(metered.Models.Count * 2, metered.ChangesSince(faux).Count, "input and output moved on every model");
            Equal(64, PriceSnapshots.Sha256("p", [new ModelPrice("m", null)]).Length, "lower-hex SHA-256");
            Check(PriceSnapshots.Sha256("p", [new ModelPrice("m", null)]) != PriceSnapshots.Sha256("p", [new ModelPrice("m", new CatalogCost(0, 0))]),
                "no price differs from a price of $0 in the hash");
        }),

        Case("evals.drift.suite-estimate-is-the-spend-guards-worst-case", () =>
        {
            var world = DwWorld();
            var suite = new Suite { Id = EvalIds.New(EvalIds.Suite), Name = "Regex edge cases", Targets = [new SuiteTarget("dwp", "big"), new SuiteTarget("faux", "echo")], K = 2 };
            var judge = new JudgeSpec("dwp", "judge", "Is it right?");
            List<TestCase> cases = [DwCase(suite, "short", "What is 2 + 2?", 1000), DwCase(suite, "long", new string('x', 3000)), DwCase(suite, "judged", "Explain SemVer.", judge: judge)];
            var estimate = world.Estimator.Estimate(suite, cases);
            var big = new SuiteTarget("dwp", "big");
            long expectedBig = cases.Sum(testCase => world.Spend.Estimate(new SpendEstimateInput("dwp", "big", null, big.Apply(testCase.Spec).RequestBytes(), testCase.Spec.MaxTokens)).WorstMicros!.Value) * 2;
            var judgeEach = new SpendPrice(1, 5).WorstMicros(JudgeSpec.MaxInputTokens, 300);
            Equal(expectedBig + judgeEach * 4, estimate.WorstMicros, "each case on each target k times, plus a judge call per answer, as the Spend Guard prices them");
            Equal("dwp/big/Metered/False/6|dwp/judge/Metered/True/4|faux/echo/Free/False/6",
                string.Join("|", estimate.Lines.Select(line => $"{line.Provider}/{line.Model}/{line.Billing}/{line.Judge}/{line.Calls}")), "lines, metered first");
            Equal(judgeEach * 4, estimate.Lines[1].RunMicros, "the judge line");
            Equal(judgeEach, estimate.Lines[1].EachMicros, "each judge call");
            Equal(0L, estimate.Lines[2].RunMicros, "free costs nothing");
            Check(estimate.TypicalMicros is null && estimate.ListMicros == 0 && !estimate.HasUnknownPrice, "no typical, no list, every price known");
            Check(estimate.Fits(estimate.WorstMicros) && !estimate.Fits(estimate.WorstMicros - 1), "fits a cap of its worst case");

            var key = SuiteCostEstimator.PriceKey("dwp", "big");
            var dearer = world.Estimator.Estimate(suite, cases, new Dictionary<string, CatalogCost?> { [key] = new CatalogCost(2, 12.5, 0.2, 2.5) });
            long expectedDearer = cases.Sum(testCase =>
            {
                var spec = big.Apply(testCase.Spec);
                return new SpendPrice(2, 12.5, 0.2, 2.5).WorstMicros(CostEstimator.InputTokens(spec.RequestBytes()), testCase.Spec.MaxTokens ?? 4096);
            }) * 2;
            Equal(expectedDearer + judgeEach * 4, dearer.WorstMicros, "a snapshot's price replaces the catalog's");
            Check(world.Estimator.Estimate(suite, cases, new Dictionary<string, CatalogCost?> { [key] = null }).HasUnknownPrice, "no price in the snapshot: unknown");
            var unpriced = world.Estimator.Estimate(suite with { Targets = [new SuiteTarget("dwp", "unpriced")] }, cases);
            Check(unpriced.HasUnknownPrice && unpriced.Lines.Single(line => line.Model == "unpriced").Billing == BillingKind.Unknown, "a metered model without a price is unknown");
            Equal(0L, world.Estimator.Estimate(suite with { Targets = [] }, cases).WorstMicros, "no targets, no calls");
            Equal(SuiteCostEstimator.PlannedBytes(null, cases[0].Spec), cases[0].Spec.RequestBytes(), "without the run engine, the stored request's size");
        }),

        Case("evals.drift.price-watch-pauses-schedules-and-explains-changes", () =>
        {
            var root = Temp("evals-drift-watch");
            try
            {
                var db = new WebAgentDb(Path.Join(root, "webagent.db"), [CoreMigrations.AppInfo, .. EvalMigrations.All]);
                db.Migrate();
                var world = DwWorld();
                var clock = new DwClock(DwNow.AddHours(-1));
                var watch = new PriceWatch(new CatalogPriceSource(world.Catalog), new PriceSnapshotStore(db), world.Estimator, clock);
                Equal(2, watch.Check().Saved.Count, "a snapshot of each provider");

                SuiteCases Make(string name, string model, long cap, bool enabled = true)
                {
                    var suite = new Suite
                    {
                        Id = EvalIds.New(EvalIds.Suite), Name = name, Targets = [new SuiteTarget("dwp", model)], K = 3, CapMicros = cap,
                        Schedule = new SuiteSchedule { Enabled = enabled, EnabledAt = DwNow.AddDays(-1) },
                    };
                    return new SuiteCases(suite, [DwCase(suite, "one", "Extract the total from: 3 × $4.50", 1000), DwCase(suite, "two", "Is 1.0.0-rc.1 valid SemVer?", 1000)]);
                }
                var regex = Make("Regex edge cases", "big", 2_000_000);
                var worstBefore = world.Estimator.Estimate(regex.Suite, regex.Cases).WorstMicros;
                var json = Make("JSON extraction", "big", worstBefore + 1);
                var other = Make("Other", "judge", 2_000_000);
                SuiteCases[] suites = [json, regex, other];
                Check(watch.SchedulesToPause(suites, DwNow).Count == 0, "everything fits before the change");

                world.Catalog.Replace(new CatalogModel("dwp", "big", "Big") { MaxTokens = 4096, Cost = new CatalogCost(2, 12, 0.2, 2.5) });
                clock.Now = DwNow;
                var check = watch.Check();
                var row = check.Changes.Single();
                Equal("output $10.00 → $12.00", row.Change.Label, "output 10 → 12");
                var worstAfter = world.Estimator.Estimate(json.Suite, json.Cases).WorstMicros;
                Check(worstAfter > worstBefore, "the worst case rose");

                var effects = watch.Effects(row, suites);
                Equal(2, effects.Count, "only suites that use the model");
                Equal($"JSON extraction: worst {DriftText.Money(worstBefore)} → {DriftText.Money(worstAfter)}, above its {DriftText.Money(worstBefore + 1)} cap.", effects[0].Text, "over its cap");
                Check(effects[0].OverCap && effects[0].PausesSchedule, "its schedule is paused");
                Equal(worstBefore, effects[0].WorstBeforeMicros, "the worst case before");
                Equal(worstAfter, effects[0].WorstAfterMicros, "after");
                Equal($"Regex edge cases: worst {DriftText.Money(worstBefore)} → {DriftText.Money(worstAfter)}, within its $2.00 cap.", effects[1].Text, "within its cap");
                Check(!effects[1].PausesSchedule, "not paused");

                var pauses = watch.SchedulesToPause(suites, DwNow);
                var pause = pauses.Single();
                Equal(json.Suite.Id, pause.SuiteId, "the JSON extraction schedule");
                Equal(SchedulePause.OverCap, pause.Pause.Code, "over its cap");
                Equal($"A catalog price change raised its worst case to {DriftText.Money(worstAfter)}, above its {DriftText.Money(worstBefore + 1)} suite-run cap.", pause.Pause.Reason,
                    "the mockup's reason");
                Equal(DwNow, pause.Pause.At, "when");
                Equal(worstAfter, pause.Estimate.WorstMicros, "with its estimate");
                Equal($"Its worst case of {DriftText.Money(worstAfter)} is above its {DriftText.Money(worstBefore + 1)} suite-run cap.",
                    watch.SchedulesToPause(suites, DwNow, []).Single().Pause.Reason, "without a price change to blame");

                var paused = json with { Suite = json.Suite with { Schedule = json.Suite.Schedule! with { Paused = pause.Pause } } };
                var off = Make("Off", "big", 1);
                off = off with { Suite = off.Suite with { Schedule = off.Suite.Schedule! with { Enabled = false } } };
                var unknown = Make("Unpriced", "unpriced", 2_000_000);
                var notices = watch.SchedulesToPause([paused, off, unknown], DwNow);
                var notice = notices.Single();
                Equal(unknown.Suite.Id, notice.SuiteId, "only the active schedule with an unknown price");
                Equal(SchedulePause.UnknownPrice, notice.Pause.Code, "unknown price");
                Equal("dwp/unpriced has no known price, and a scheduled run cannot ask.", notice.Pause.Reason, "the reason");

                world.Catalog.Replace(new CatalogModel("dwp", "big", "Big") { MaxTokens = 4096, Cost = new CatalogCost(2, 12, 0.1, 2.5) });
                clock.Now = DwNow.AddHours(1);
                var cache = watch.Check().Changes.Single();
                Equal(PriceField.CacheRead, cache.Change.Field, "a cache-read change");
                Equal("Regex edge cases: worst case unchanged, because cache reads aren't reserved. Typical cost may drop slightly.",
                    watch.Effects(cache, suites)[1].Text, "the mockup's wording");

                Equal(10.0, watch.PriceAt("dwp", "big", DwNow.AddMinutes(-30))!.Cost!.Output, "the price before");
                Equal(12.0, watch.PriceAt("dwp", "big", DwNow.AddMinutes(30))!.Cost!.Output, "the price after");
                Equal(2, watch.Changes().Count, "two changes");
                Throws<InvalidOperationException>(() => new PriceWatch(new CatalogPriceSource(world.Catalog), new PriceSnapshotStore(db)).SchedulesToPause(suites), "no estimator");
            }
            finally { DeleteTemp(root); }
        }),

        Case("evals.drift.module-registers-price-watch-and-the-app-takes-a-snapshot", async () =>
        {
            var services = new ServiceCollection();
            var unused = Temp("evals-drift-module-unused");
            DeleteTemp(unused);
            EvalsModule.AddEvalsArea(services, new WebAgentOptions { DataDirectory = unused });
            foreach (var type in new[] { typeof(IPriceSnapshotStore), typeof(IPriceSource), typeof(ISuiteCostEstimator), typeof(PriceWatch), typeof(PriceWatchService) })
                Equal(1, services.Count(service => service.ServiceType == type), "registered once: " + type.Name);
            Equal(1, services.Count(service => service.ServiceType == typeof(IHostedService) && service.ImplementationFactory?.Method.ReturnType == typeof(PriceWatchService)),
                "Price Watch runs as a hosted service");
            EvalsModule.AddEvalsArea(services, new WebAgentOptions { DataDirectory = unused });
            Equal(1, services.Count(service => service.ServiceType == typeof(PriceWatch)), "registering twice adds nothing");

            var root = Temp("evals-drift-app");
            try
            {
                var data = Path.Join(root, "data");
                await using (var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = data }))
                {
                    var path = Path.Join(data, "webagent.db");
                    List<string> rows = [];
                    await WaitUntilAsync(() =>
                    {
                        try { rows = ReadDb(path, "SELECT provider || ' ' || catalog_sha256 FROM eval_price_snapshots ORDER BY id", reader => reader.GetString(0)); }
                        catch (Microsoft.Data.Sqlite.SqliteException) { rows = []; }
                        return Task.FromResult(rows.Count > 0);
                    }, "Price Watch saves a snapshot when the app starts", TimeSpan.FromSeconds(30));
                    var faux = rows.Single(row => row.StartsWith("faux ", StringComparison.Ordinal));
                    var expected = new CatalogPriceSource(new FauxModelCatalog(FauxModelCatalog.CreateModels())).Snapshots(DwNow).Single().CatalogSha256;
                    Equal("faux " + expected, faux, "the faux catalog's prices, by their hash");
                    Equal(0L, ReadDb(path, "SELECT COUNT(*) FROM eval_price_changes", reader => reader.GetInt64(0)).Single(), "the first snapshot is no change");
                    await app.StopAsync();
                }
            }
            finally { DeleteTemp(root); }
        }),
    ];

    // ---- simulation ----

    /// <summary>One simulated case: how often it passes, whether a judge scores it (and how often a 5), and its typical latency, speed and
    /// size.</summary>
    private sealed record DwShape(string Id, int Index, double Pass, double? JudgeFive, double LatencyMs, double TokensPerSecond, double InputTokens, double OutputTokens);

    /// <summary>How the simulated model behaves in a run.</summary>
    private sealed record DwBehaviour
    {
        public Func<double, double> Pass { get; init; } = pass => pass;

        public string ResponseModel { get; init; } = "dw-model-2026-05";

        public string Vocabulary { get; init; } = "c";
    }

    private static List<DwShape> DwShapes(IReadOnlyList<double> passes, int judged) =>
        [.. passes.Select((pass, index) => new DwShape("c" + (index + 1).ToString("00", CultureInfo.InvariantCulture), index, pass, index < judged ? 0.8 : null,
            2500 + 400 * (index % 6), 60 + 5 * (index % 5), 600 + 150 * (index % 4), 300 + 40 * (index % 7)))];

    private static (SuiteRun Run, List<SuiteResult> Results) DwSimulate(string suiteId, int number, IReadOnlyList<DwShape> shapes, int k, EvalRandom random,
        DwBehaviour? behaviour = null)
    {
        behaviour ??= new DwBehaviour();
        var run = DwRunOf(suiteId, number, [.. shapes.Select(shape => shape.Id)], k);
        var results = new List<SuiteResult>(shapes.Count * k);
        foreach (var shape in shapes)
            for (var repetition = 1; repetition <= k; repetition++)
            {
                var passed = random.NextDouble() < behaviour.Pass(shape.Pass);
                var checks = new List<CheckResult> { new("k1", CheckKind.Contains, passed ? CheckVerdict.Passed : CheckVerdict.Failed) };
                if (shape.JudgeFive is { } five)
                {
                    var draw = random.NextDouble();
                    checks.Add(new CheckResult("k2", CheckKind.Judge, CheckVerdict.Passed, Score: draw < five ? 5 : draw < five + (1 - five) * 0.8 ? 4 : 3));
                }
                var text = DwText(random, behaviour.Vocabulary, shape.Index);
                var output = Math.Round(shape.OutputTokens * DwLogNormal(random, 0.2));
                results.Add(new SuiteResult
                {
                    RunId = run.Id, CaseId = shape.Id, TargetId = DwTarget.Id, Repetition = repetition, Status = ResultStatus.Done, Checks = checks,
                    Verdict = passed ? CheckVerdict.Passed : CheckVerdict.Failed, Score = SuiteResult.ScoreOf(checks), Text = text,
                    Fingerprint = AnswerFingerprint.Of(text), ResponseModel = behaviour.ResponseModel, InputTokens = shape.InputTokens, OutputTokens = output,
                    CostMicros = (long)Math.Round(shape.InputTokens * 1 + output * 10), DurationMs = (long)Math.Round(shape.LatencyMs * DwLogNormal(random, 0.25)),
                    TokensPerSecond = shape.TokensPerSecond * DwLogNormal(random, 0.15), Finished = run.Finished,
                });
            }
        return (run, results);
    }

    /// <summary>An answer of 40 to 60 words drawn from the case's own 80-word vocabulary (Zipf weights); another vocabulary prefix makes
    /// another model's answers.</summary>
    private static string DwText(EvalRandom random, string vocabulary, int caseIndex)
    {
        var words = 40 + random.Next(21);
        var text = new StringBuilder(words * 8);
        for (var index = 0; index < words; index++)
        {
            var rank = Array.BinarySearch(DwZipf, random.NextDouble());
            if (rank < 0) rank = ~rank;
            rank = Math.Min(rank, DwZipf.Length - 1);
            if (index > 0) text.Append(' ');
            text.Append(vocabulary).Append(caseIndex).Append('w').Append(rank);
        }
        return text.ToString();
    }

    private static double[] DwCumulative(int count)
    {
        var cumulative = new double[count];
        double sum = 0;
        for (var index = 0; index < count; index++) cumulative[index] = sum += 1.0 / (index + 1);
        for (var index = 0; index < count; index++) cumulative[index] /= sum;
        return cumulative;
    }

    private static double DwLogNormal(EvalRandom random, double sigma)
    {
        var first = 1 - random.NextDouble();
        var second = random.NextDouble();
        return Math.Exp(sigma * Math.Sqrt(-2 * Math.Log(first)) * Math.Cos(2 * Math.PI * second));
    }

    // ---- hand-made runs ----

    private static List<string> DwIds(int count) => [.. Enumerable.Range(1, count).Select(index => "c" + index.ToString("00", CultureInfo.InvariantCulture))];

    private static SuiteRun DwRunOf(string suiteId, int number, IReadOnlyList<string> caseIds, int k) => new()
    {
        Id = EvalIds.New(EvalIds.Run), SuiteId = suiteId, Number = number, Status = SuiteRunStatus.Complete, Targets = [DwTarget], CaseIds = [.. caseIds],
        K = k, Created = DwNow.AddHours(number), Started = DwNow.AddHours(number), Finished = DwNow.AddHours(number).AddMinutes(5),
    };

    private static Baseline DwBaseline(SuiteRun run, IReadOnlyList<string>? responseModels = null) => new()
    {
        SuiteId = run.SuiteId, TargetId = DwTarget.Id, RunId = run.Id, Identity = DwTarget.Identity, ResponseModels = responseModels ?? [], Accepted = run.Created,
    };

    private static SuiteResult DwAnswer(SuiteRun run, string caseId, int repetition, bool passed, string? responseModel = null) => new()
    {
        RunId = run.Id, CaseId = caseId, TargetId = DwTarget.Id, Repetition = repetition, Status = ResultStatus.Done,
        Checks = [new CheckResult("k1", CheckKind.Contains, passed ? CheckVerdict.Passed : CheckVerdict.Failed)],
        Verdict = passed ? CheckVerdict.Passed : CheckVerdict.Failed, Score = passed ? 1 : 0, ResponseModel = responseModel, Finished = run.Finished,
    };

    /// <summary>Each case's answers: the first <c>passes[i]</c> of its k repeats pass, the rest fail.</summary>
    private static List<SuiteResult> DwAnswers(SuiteRun run, IReadOnlyList<string> cases, IReadOnlyList<int> passes, string? responseModel = null) =>
        [.. cases.SelectMany((id, index) => Enumerable.Range(1, run.K).Select(repetition => DwAnswer(run, id, repetition, repetition <= passes[index], responseModel)))];

    /// <summary>Answers that all pass, each with a judge score of <paramref name="score"/> (a fraction is spread over the repeats: 4.75 is
    /// 5, 5, 4.25).</summary>
    private static List<SuiteResult> DwJudged(SuiteRun run, IReadOnlyList<string> cases, double score) =>
        [.. cases.SelectMany(id => Enumerable.Range(1, run.K).Select(repetition =>
        {
            var given = repetition < run.K || score == Math.Floor(score) ? Math.Ceiling(score) : score - (Math.Ceiling(score) - score) * (run.K - 1);
            return DwAnswer(run, id, repetition, true) with
            {
                Checks = [new CheckResult("k1", CheckKind.Contains, CheckVerdict.Passed), new CheckResult("k2", CheckKind.Judge, CheckVerdict.Passed, Score: given)],
            };
        }))];

    /// <summary>A target's analyses run after run, with the open signals stored between them as the suites unit would (ids assigned).</summary>
    private sealed class DwHistory
    {
        private readonly SuiteRun baselineRun;
        private readonly IReadOnlyList<SuiteResult> baselineResults;
        private long next;

        public DwHistory(SuiteRun baselineRun, IReadOnlyList<SuiteResult> baselineResults, IReadOnlyList<string>? responseModels = null)
        {
            this.baselineRun = baselineRun;
            this.baselineResults = baselineResults;
            Baseline = DwBaseline(baselineRun, responseModels);
        }

        public Baseline Baseline { get; }

        public List<DriftSignal> Open { get; private set; } = [];

        public IReadOnlyDictionary<string, string>? Names { get; init; }

        public DriftAnalysis Analyze(SuiteRun run, IReadOnlyList<SuiteResult> results, CatalogCost? baselinePrice = null, CatalogCost? currentPrice = null)
        {
            var analysis = DriftAnalyzer.Analyze(new DriftInput(run, results, Baseline, baselineRun, baselineResults)
            {
                Open = Open, CaseNames = Names, BaselinePrice = baselinePrice, CurrentPrice = currentPrice,
            }, run.Finished ?? run.Created);
            var raised = new HashSet<DriftSignal>(analysis.Raised, ReferenceEqualityComparer.Instance);
            Open = [.. analysis.Open.Where(signal => !raised.Contains(signal)), .. analysis.Raised.Select(signal => signal with { Id = ++next })];
            return analysis;
        }
    }

    // ---- prices ----

    private static byte[] DwProviderJson(string provider, params (string Id, double Input, double Output, double CacheRead, double CacheWrite)[] models)
    {
        var api = new JsonObject();
        foreach (var model in models)
            api["chat:" + model.Id] = new JsonObject
            {
                ["type"] = "chat", ["id"] = model.Id, ["provider"] = provider, ["api"] = "openai-completions", ["name"] = model.Id, ["baseUrl"] = "",
                ["input"] = new JsonArray("text"), ["reasoning"] = false, ["contextWindow"] = 128000, ["maxTokens"] = 4096,
                ["cost"] = new JsonObject { ["input"] = model.Input, ["output"] = model.Output, ["cacheRead"] = model.CacheRead, ["cacheWrite"] = model.CacheWrite },
            };
        return Encoding.UTF8.GetBytes(new JsonObject { ["openai-completions"] = api }.ToJsonString());
    }

    private static TestCase DwCase(Suite suite, string name, string question, int? maxTokens = null, JudgeSpec? judge = null) => new()
    {
        Id = EvalIds.New(EvalIds.Case), SuiteId = suite.Id, Name = name,
        Spec = new RunSpec { Provider = "dwp", Model = "big", Messages = [new RunMessage { Text = question }], MaxTokens = maxTokens },
        Checks = judge is null ? [new EvalCheck { Kind = CheckKind.Contains, Text = "4" }] : [new EvalCheck { Kind = CheckKind.Judge, Judge = judge }],
        Origin = new TestCaseOrigin(PinSource.History),
    };

    /// <summary>A metered provider <c>dwp</c> (big at $2/$10, a judge at $1/$5 with a 300-token maximum, a model without a price) and the free
    /// faux provider, with the Spend Guard's estimation services over them.</summary>
    private static (DwCatalog Catalog, CostEstimator Spend, SuiteCostEstimator Estimator) DwWorld()
    {
        var catalog = new DwCatalog()
            .Add(new CatalogProvider("dwp", "Drift Watch provider", null, [ProviderAuthKind.None], BillingKind.Metered),
                new CatalogModel("dwp", "big", "Big") { MaxTokens = 4096, Cost = new CatalogCost(2, 10, 0.2, 2.5) },
                new CatalogModel("dwp", "judge", "Judge") { MaxTokens = 300, Cost = new CatalogCost(1, 5) },
                new CatalogModel("dwp", "unpriced", "Unpriced") { MaxTokens = 4096 })
            .Add(FauxModelCatalog.Provider, new CatalogModel("faux", "echo", "Echo") { MaxTokens = 1000, Cost = new CatalogCost(0, 0) });
        var settings = new DwSettings();
        var prices = new ModelPrices(catalog, settings);
        var billing = new BillingModes(catalog, settings, prices);
        return (catalog, new CostEstimator(catalog, settings, billing, prices), new SuiteCostEstimator(catalog, settings, billing, prices));
    }

    private sealed class DwCatalog : IModelCatalog
    {
        private readonly List<CatalogProvider> providers = [];
        private readonly Dictionary<string, List<CatalogModel>> models = new(StringComparer.Ordinal);

        public DwCatalog Add(CatalogProvider provider, params CatalogModel[] list)
        {
            providers.Add(provider);
            models[provider.Id] = [.. list];
            return this;
        }

        public void Replace(CatalogModel model)
        {
            var list = models[model.Provider];
            list[list.FindIndex(listed => listed.Id == model.Id)] = model;
        }

        public IReadOnlyList<CatalogProvider> Providers => providers;

        public IReadOnlyList<CatalogModel> Models(string providerId) => models.TryGetValue(providerId, out var list) ? [.. list] : [];
    }

    private sealed class DwSettings : ISettingsStore
    {
        public event EventHandler<SettingsChangedEventArgs>? Changed { add { } remove { } }

        public T Get<T>() where T : class, ISettingsSection<T> => T.CreateDefault();

        public Task<T> SaveAsync<T>(T value, CancellationToken cancellationToken = default) where T : class, ISettingsSection<T> => throw new NotSupportedException();

        public Task<T> UpdateAsync<T>(Func<T, T> change, CancellationToken cancellationToken = default) where T : class, ISettingsSection<T> => throw new NotSupportedException();

        public Task<T> ResetAsync<T>(CancellationToken cancellationToken = default) where T : class, ISettingsSection<T> => throw new NotSupportedException();
    }

    private sealed class DwSource(Func<DateTimeOffset, IReadOnlyList<PriceSnapshot>> read) : IPriceSource
    {
        public IReadOnlyList<PriceSnapshot> Snapshots(DateTimeOffset now) => read(now);
    }

    private sealed class DwClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

}
