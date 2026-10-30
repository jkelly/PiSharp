using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PiSharp.WebAgent.AgentCompare.Runner;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Evals;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Runs;
using static System.FormattableString;

// Decision 0011 (run W5, E1): the Bradley-Terry rater. Synthetic votes are drawn from known strengths with EvalRandom (fixed seeds, so every
// statistic below is the same on every run): the fit recovers the strengths and its intervals cover them; the order votes come in changes
// nothing, to the bit; a tie is half a win and sparse votes stay finite; rankings split into weighted pairs; a biased voter's left-position
// bias is found and an unbiased one's is not; intervals widen as votes get fewer (and never collapse on a single vote); request and agent
// leaderboards stay apart and unvoted battles never count; the seed is derived and stored; and each row's cost and speed are medians of the
// voted answers' run records.
internal static partial class Program
{
    private static readonly DateTimeOffset RaterNow = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private static readonly ModelIdentity[] RaterModels =
    [
        new("faux", "alpha"),
        new("faux", "bravo", "medium"),
        new("openai", "charlie"),
        new("anthropic", "delta", null, 0.7),
    ];

    internal static IEnumerable<(string, Func<Task>)> EvalRaterCases() =>
    [
        Case("evals.rater.recovers-seeded-strengths", () =>
        {
            var rater = new BradleyTerryRater();
            double[] truth = [-0.8, -0.25, 0.3, 0.75];

            // Many votes between two answers: every strength comes back within 0.15 (about 26 rating points), in the right order.
            var board = rater.Rate(ArenaMode.Request, "coding", RaterPairs(RaterSimulate(101, truth, 2400)));
            Equal(2400, board.Votes, "every vote counted");
            Equal(4, board.Models.Count, "four models");
            for (var model = 0; model < truth.Length; model++)
            {
                var row = board.Of(RaterModels[model])!;
                Check(Math.Abs(row.Strength - truth[model]) < 0.15, Invariant($"{row.Identity.Name}: {row.Strength:0.000} recovers {truth[model]}"));
                Check(Math.Abs(row.Rating - BradleyTerryRater.RatingOf(row.Strength)) < 1e-9, "the rating is 1000 + strength × 400/ln 10");
                Equal(4 - model, row.Rank, "rank of " + row.Identity.Name);
                Check(row.Low < row.Rating && row.Rating < row.High, "the interval holds the rating");
                Check(!row.Provisional, "not provisional with hundreds of comparisons");
            }
            Equal("anthropic/delta;temperature=0.7,openai/charlie,faux/bravo;thinking=medium,faux/alpha",
                string.Join(",", board.Models.Select(row => row.Identity.Key)), "best first");
            Check(Math.Abs(board.Models.Sum(row => row.Strength)) < 1e-9, "the prior centres the strengths on 0, so 1000 is the tag's average");

            // Picks of one of two to four answers (Plackett-Luce choices) recover the same order.
            var mixed = rater.Rate(ArenaMode.Request, "coding", RaterPairs(RaterSimulate(102, truth, 1500, maxContestants: 4)));
            for (var model = 0; model < truth.Length; model++)
                Check(Math.Abs(mixed.Of(RaterModels[model])!.Strength - truth[model]) < 0.2, "a pick of up to four recovers " + RaterModels[model].Name);
            Equal("4,3,2,1", string.Join(",", RaterModels.Select(identity => mixed.Of(identity)!.Rank)), "the order from picks");

            // The 95% intervals cover the truth: 12 independent sets of 300 votes, 48 intervals.
            var covered = 0;
            for (var set = 0; set < 12; set++)
            {
                var sample = rater.Rate(ArenaMode.Request, "coding", RaterPairs(RaterSimulate(200 + set, truth, 300)), new RaterOptions { Samples = 200 });
                for (var model = 0; model < truth.Length; model++)
                {
                    var row = sample.Of(RaterModels[model])!;
                    var rating = BradleyTerryRater.RatingOf(truth[model]);
                    if (row.Low <= rating && rating <= row.High) covered++;
                }
            }
            Check(covered >= 43, $"the intervals cover the true rating in {covered} of 48 (expected about 46)");
        }),

        Case("evals.rater.vote-order-makes-no-difference", () =>
        {
            var rater = new BradleyTerryRater();
            var votes = RaterSimulate(301, [-0.6, 0.1, 0.2, 0.3], 240, maxContestants: 4, tieShare: 0.15, kinds: true, bias: 0.3);
            Check(votes.Any(vote => vote.Vote.Kind == VoteKind.Rank) && votes.Any(vote => vote.Vote.Kind == VoteKind.Tie)
                && votes.Any(vote => vote.Vote.Kind == VoteKind.AllBad) && votes.Any(vote => vote.Vote.Kind == VoteKind.Pick), "every kind of vote");
            var expected = EvalJson.Serialize(Leaderboards.Build(ArenaMode.Request, "coding", votes, rater, null).Ratings);
            var orders = new List<IEnumerable<VotedBattle>> { Enumerable.Reverse(votes) };
            for (var seed = 1; seed <= 3; seed++)
            {
                var shuffled = votes.ToList();
                new EvalRandom(seed).Shuffle(shuffled);
                orders.Add(shuffled);
            }
            foreach (var order in orders)
                Equal(expected, EvalJson.Serialize(Leaderboards.Build(ArenaMode.Request, "coding", order, rater, null).Ratings), "the same ratings, intervals and ranks, to the bit");

            // Pairs given one by one in any order: the same again.
            var pairs = RaterPairs(votes).ToList();
            new EvalRandom(9).Shuffle(pairs);
            Equal(expected, EvalJson.Serialize(rater.Rate(ArenaMode.Request, "coding", pairs)), "shuffled pairs");
            Equal(EvalJson.Serialize(rater.Rate(ArenaMode.Request, "coding", pairs)), EvalJson.Serialize(rater.Rate(ArenaMode.Request, "coding", pairs)), "the same call twice");
        }),

        Case("evals.rater.ties-count-half-and-sparse-votes-stay-finite", () =>
        {
            var rater = new BradleyTerryRater();
            ModelIdentity a = RaterModels[0], b = RaterModels[1];
            // A beats B once and B beats A once (A on the left both times) is, for the fit, exactly two ties.
            var split = rater.Rate(ArenaMode.Request, "coding", RaterPairs([RaterVote(1, [a, b], VoteKind.Pick, "A"), RaterVote(2, [a, b], VoteKind.Pick, "B")]));
            var tied = rater.Rate(ArenaMode.Request, "coding", RaterPairs([RaterVote(1, [a, b], VoteKind.Tie), RaterVote(2, [a, b], VoteKind.Tie)]));
            foreach (var identity in new[] { a, b })
                Check(Math.Abs(split.Of(identity)!.Strength - tied.Of(identity)!.Strength) < 1e-12, "a tie is half a win: " + identity.Name);
            Check(Math.Abs(split.Position.Estimate!.Value - tied.Position.Estimate!.Value) < 1e-12, "and the same for the position parameter");
            Equal("1–1–0", split.Of(a)!.Record, "won one, lost one");
            Equal("0–0–2", tied.Of(a)!.Record, "two ties");
            Equal(0, tied.Position.Pairs, "ties decide no pair");

            // All bad counts as a tie.
            var bad = rater.Rate(ArenaMode.Request, "coding", RaterPairs([RaterVote(1, [a, b], VoteKind.AllBad), RaterVote(2, [a, b], VoteKind.AllBad)]));
            Equal(EvalJson.Serialize(tied.Models), EvalJson.Serialize(bad.Models), "all bad is a tie");

            // Ten wins out of ten would be an infinite rating by maximum likelihood; the prior keeps it finite and modest.
            var sweep = rater.Rate(ArenaMode.Request, "coding", RaterPairs([.. Enumerable.Range(1, 10)
                .Select(index => RaterVote(index, index % 2 == 0 ? [a, b] : [b, a], VoteKind.Pick, index % 2 == 0 ? "A" : "B"))]));
            var winner = sweep.Of(a)!;
            var loser = sweep.Of(b)!;
            Check(double.IsFinite(winner.Rating) && double.IsFinite(winner.High) && double.IsFinite(loser.Low), "finite ratings and intervals");
            Check(winner.Rating > loser.Rating && winner.Rating < 1400 && loser.Rating > 600, Invariant($"a sweep is a clear but bounded gap: {winner.Rating:0} vs {loser.Rating:0}"));
            Equal("10–0–0", winner.Record, "ten wins");
            Check(winner.Provisional is false && sweep.Of(b)!.Provisional is false, "ten comparisons is not provisional");
            Check(Math.Abs(sweep.Position.Estimate!.Value) < 1e-9, "left and right won as often: no position effect");
        }),

        Case("evals.rater.rankings-split-into-weighted-pairs", () =>
        {
            var rater = new BradleyTerryRater();
            Equal(1.0, BradleyTerryRater.PairWeight(VoteKind.Pick, 3), "a pick's pairs weigh 1");
            Equal(1.0, BradleyTerryRater.PairWeight(VoteKind.Tie, 2), "any vote of two answers weighs 1");
            Equal(2.0 / 3, BradleyTerryRater.PairWeight(VoteKind.Rank, 3), "a ranking of three: three pairs of 2/3, total 2");
            Equal(0.5, BradleyTerryRater.PairWeight(VoteKind.Rank, 4), "a ranking of four: six pairs of 1/2, total 3");
            Equal(0.5, BradleyTerryRater.PairWeight(VoteKind.AllBad, 4), "all bad of four: the same total as a pick");
            Throws<ArgumentOutOfRangeException>(() => BradleyTerryRater.PairWeight(VoteKind.Pick, 5), "five answers");

            ModelIdentity a = RaterModels[0], b = RaterModels[1], c = RaterModels[2];
            // Three rankings A > B > C (columns A, B, C) weigh each pair 3 × 2/3 = 2: the same as two votes on each pair.
            var ranked = rater.Rate(ArenaMode.Request, "coding", RaterPairs([.. Enumerable.Range(1, 3).Select(index => RaterVote(index, [a, b, c], VoteKind.Rank, ranking: ["A", "B", "C"]))]));
            var paired = rater.Rate(ArenaMode.Request, "coding", RaterPairs(
            [
                RaterVote(11, [a, b], VoteKind.Pick, "A"), RaterVote(12, [a, b], VoteKind.Pick, "A"),
                RaterVote(13, [a, c], VoteKind.Pick, "A"), RaterVote(14, [a, c], VoteKind.Pick, "A"),
                RaterVote(15, [b, c], VoteKind.Pick, "A"), RaterVote(16, [b, c], VoteKind.Pick, "A"),
            ]));
            foreach (var identity in new[] { a, b, c })
                Check(Math.Abs(ranked.Of(identity)!.Strength - paired.Of(identity)!.Strength) < 1e-9, "a ranking is its pairs, weighted: " + identity.Name);
            Equal("6–0–0|3–3–0|0–6–0", string.Join("|", new[] { a, b, c }.Select(identity => ranked.Of(identity)!.Record)), "a ranking's pairs are wins and losses");
            Equal(3, ranked.Of(b)!.Votes, "three votes each");
            Equal(9, ranked.Comparisons, "nine pairs");
            Equal("1,2,3", string.Join(",", new[] { a, b, c }.Select(identity => ranked.Of(identity)!.Rank)), "ranked in order");

            // Picking one of three counts as two wins for it, and says nothing about the other two.
            var picked = rater.Rate(ArenaMode.Agent, "agent", RaterPairs([RaterVote(1, [a, b, c], VoteKind.Pick, "B", mode: ArenaMode.Agent, tag: "agent")]));
            Equal("2–0–0", picked.Of(b)!.Record, "the pick");
            Equal("0–1–0|0–1–0", picked.Of(a)!.Record + "|" + picked.Of(c)!.Record, "the others");
            Equal(1, picked.Of(b)!.Rank, "first");
            Equal(2, picked.Of(a)!.Rank, "the other two share a rank");
            Equal(2, picked.Of(c)!.Rank, "the other two share a rank");
            Check(Math.Abs(picked.Of(a)!.Rating - picked.Of(c)!.Rating) < 1e-6, "with the same rating");
            Check(picked.Models.All(row => row.RankLow <= row.Rank && row.Rank <= row.RankHigh), "each rank range holds its rank");
        }),

        Case("evals.rater.position-bias-is-detected-on-a-biased-set", () =>
        {
            var rater = new BradleyTerryRater();
            double[] truth = [-0.5, -0.1, 0.2, 0.4];
            var options = new RaterOptions { Samples = 200 };
            int biasedFound = 0, unbiasedFound = 0;
            double estimates = 0, shares = 0;
            for (var set = 0; set < 20; set++)
            {
                var biased = rater.Rate(ArenaMode.Request, "coding", RaterPairs(RaterSimulate(400 + set, truth, 500, bias: 0.5)), options).Position;
                if (biased.Detected) biasedFound++;
                estimates += biased.Estimate!.Value;
                shares += biased.LeftShare!.Value;
                Check(biased.Fitted && biased.Low < biased.Estimate && biased.Estimate < biased.High, "an interval around the estimate");
                if (rater.Rate(ArenaMode.Request, "coding", RaterPairs(RaterSimulate(500 + set, truth, 500)), options).Position.Detected) unbiasedFound++;
            }
            Check(biasedFound >= 18, $"a voter who favours the left (logit 0.5, 62% between equals) is found in {biasedFound} of 20 sets");
            Check(unbiasedFound <= 3, $"an unbiased voter is flagged in {unbiasedFound} of 20 sets (about 1 expected)");
            Check(Math.Abs(estimates / 20 - 0.5) < 0.1, Invariant($"the average estimate {estimates / 20:0.000} is near 0.5"));
            Check(shares / 20 > 0.56, Invariant($"the left column won {shares / 20:P0} of decided pairs"));

            var one = rater.Rate(ArenaMode.Request, "coding", RaterPairs(RaterSimulate(400, truth, 500, bias: 0.5)));
            Check(one.Position.LeftWinRate is > 0.55 and < 0.7, "between two equal answers the left one wins about 62%");
            Equal(500, one.Position.Pairs, "every two-answer pick decides one pair");

            var off = rater.Rate(ArenaMode.Request, "coding", RaterPairs(RaterSimulate(400, truth, 500, bias: 0.5)), new RaterOptions { PositionParameter = false, Samples = 50 });
            Check(!off.Position.Fitted && off.Position.Estimate is null && !off.Position.Detected, "the position parameter is optional");
            Equal(one.Position.LeftWins, off.Position.LeftWins, "the raw share is kept either way");
        }),

        Case("evals.rater.intervals-widen-with-fewer-votes", () =>
        {
            var rater = new BradleyTerryRater();
            double[] truth = [-0.6, -0.2, 0.2, 0.6];
            double Width(int votes) => rater.Rate(ArenaMode.Request, "coding", RaterPairs(RaterSimulate(600, truth, votes))).Models.Average(row => row.HalfWidth);
            var few = Width(16);
            var some = Width(120);
            var many = Width(800);
            Check(few > some && some > many, Invariant($"the average ± shrinks with votes: {few:0} > {some:0} > {many:0}"));

            var sparse = rater.Rate(ArenaMode.Request, "coding", RaterPairs(RaterSimulate(600, truth, 12)));
            Check(sparse.Provisional && sparse.Models.All(row => row.Provisional), "12 votes among four models: every row provisional (below 10 comparisons)");
            Check(!rater.Rate(ArenaMode.Request, "coding", RaterPairs(RaterSimulate(600, truth, 800))).Provisional, "800 votes: none provisional");
            Check(sparse.Models.Average(row => row.RankHigh - row.RankLow) > 1, "rank ranges are wide with few votes");

            // One vote: the bootstrap alone would collapse (one vote resampled is always that vote); the posterior keeps it honest.
            var single = rater.Rate(ArenaMode.Request, "coding", RaterPairs([RaterVote(1, [RaterModels[0], RaterModels[1]], VoteKind.Pick, "A")]));
            foreach (var row in single.Models)
            {
                Check(row.HalfWidth > 100, Invariant($"one vote is a wide interval: ± {row.HalfWidth:0}"));
                Equal("1–2", row.RankRange, "either could be first");
                Check(row.Provisional, "provisional");
            }
            Equal(5, rater.Rate(ArenaMode.Request, "coding", [], new RaterOptions { ProvisionalBelow = 5 }).ProvisionalBelow, "the threshold is kept");
            Equal(3, RaterOptions.From(new EvalsSettings(ProvisionalBelow: 3)).ProvisionalBelow, "Settings' evals section sets the threshold");
            Check(!rater.Rate(ArenaMode.Request, "coding", RaterPairs(RaterSimulate(600, truth, 12)), new RaterOptions { ProvisionalBelow = 2 }).Provisional,
                "a lower threshold makes them settled");
        }),

        Case("evals.rater.request-and-agent-tags-are-kept-apart", () =>
        {
            var rater = new BradleyTerryRater();
            ModelIdentity a = RaterModels[0], b = RaterModels[1], c = RaterModels[2];
            List<VotedBattle> votes =
            [
                RaterVote(1, [a, b], VoteKind.Pick, "A", tag: "agent"),
                RaterVote(2, [a, b], VoteKind.Pick, "A", tag: "Agent "),
                RaterVote(3, [a, b], VoteKind.Pick, "B", mode: ArenaMode.Agent, tag: "agent"),
                RaterVote(4, [c, b], VoteKind.Pick, "B", mode: ArenaMode.Agent, tag: "agent"),
                RaterVote(5, [a, c], VoteKind.Tie, tag: "coding"),
            ];
            var tags = Leaderboards.Tags(votes);
            Equal("request:agent:2 votes · 2 models|request:coding:1 vote · 2 models|agent:agent:2 votes · 3 models",
                string.Join("|", tags.Select(tag => EvalJson.Wire(tag.Mode) + ":" + tag.Tag + ":" + tag.Summary)), "request tags, then agent tags");
            Equal(RaterNow.AddMinutes(3), tags[2].LastVote, "the latest vote of a tag (battle 4)");

            var request = Leaderboards.Build(ArenaMode.Request, "agent", votes, rater, null);
            var agent = Leaderboards.Build(ArenaMode.Agent, "agent", votes, rater, null);
            Equal(2, request.Votes, "request votes only");
            Equal("2–0–0", request.Ratings.Of(a)!.Record, "request mode: alpha won both");
            Equal(2, agent.Votes, "agent votes only");
            Equal("0–1–0", agent.Ratings.Of(a)!.Record, "agent mode: alpha lost its one");
            Equal("2–0–0", agent.Ratings.Of(b)!.Record, "agent mode: bravo won both");
            Check(request.Ratings.Seed != agent.Ratings.Seed, "each leaderboard has its own seed");

            // The rater itself refuses a mix.
            Throws<ArgumentException>(() => rater.Rate(ArenaMode.Request, "agent", RaterPairs(votes.Where(vote => vote.Vote.Tag.Trim().Equals("agent", StringComparison.OrdinalIgnoreCase)))), "two modes in one fit");
            Throws<ArgumentException>(() => rater.Rate(ArenaMode.Request, "coding", RaterPairs(votes.Where(vote => vote.Battle.Mode == ArenaMode.Request))), "two tags in one fit");

            // Only voted battles count: a held, ready or void battle with a vote row, and a vote that does not fit its battle, are skipped.
            var ready = RaterVote(6, [a, b], VoteKind.Pick, "A", tag: "agent");
            var voided = RaterVote(7, [a, b], VoteKind.Pick, "A", tag: "agent");
            var unfit = RaterVote(8, [a, b], VoteKind.Pick, "A", tag: "agent");
            List<VotedBattle> stray =
            [
                .. votes,
                ready with { Battle = ready.Battle with { Status = ArenaStatus.Ready } },
                voided with { Battle = voided.Battle with { Status = ArenaStatus.Void } },
                unfit with { Vote = unfit.Vote with { Pick = "D" } },
            ];
            var guarded = Leaderboards.Build(ArenaMode.Request, "agent", stray, rater, null);
            Equal(2, guarded.Votes, "still two votes");
            Equal(3, guarded.Skipped, "three skipped");
            Equal(EvalJson.Serialize(request.Ratings), EvalJson.Serialize(guarded.Ratings), "the same leaderboard");
            var empty = Leaderboards.Build(ArenaMode.Request, "writing", votes, rater, null);
            Check(empty.Empty && !empty.Provisional && empty.Votes == 0 && empty.Position.Estimate is null, "a tag with no votes is empty");
            Equal("0 votes · 0 models", empty.Summary, "its summary");
        }),

        Case("evals.rater.seed-is-derived-and-stored", () =>
        {
            var rater = new BradleyTerryRater();
            var votes = RaterSimulate(700, [-0.4, 0.0, 0.1, 0.3], 60);
            var seed = BradleyTerryRater.SeedOf(ArenaMode.Request, "coding");
            Equal(seed, BradleyTerryRater.SeedOf(ArenaMode.Request, " Coding "), "the tag is normalized first");
            Check(seed is >= 0 and <= EvalRandom.MaxSeed, "a 53-bit seed");
            Check(seed != BradleyTerryRater.SeedOf(ArenaMode.Agent, "coding"), "the mode changes it");
            var board = rater.Rate(ArenaMode.Request, "coding", RaterPairs(votes));
            Equal(seed, board.Seed, "the result names its seed");
            Equal(RaterOptions.DefaultSamples, board.Samples, "500 bootstrap samples");
            Equal(0.95, board.Confidence, "95% intervals");

            var reseeded = rater.Rate(ArenaMode.Request, "coding", RaterPairs(votes), new RaterOptions { Seed = 12345 });
            Equal(12345L, reseeded.Seed, "a given seed is used");
            Equal(string.Join(",", board.Models.Select(row => row.Rating)), string.Join(",", reseeded.Models.Select(row => row.Rating)), "the seed moves only the intervals");
            Equal(EvalJson.Serialize(reseeded), EvalJson.Serialize(rater.Rate(ArenaMode.Request, "coding", RaterPairs(votes), new RaterOptions { Seed = 12345 })), "the same seed, the same result");

            var analytic = rater.Rate(ArenaMode.Request, "coding", RaterPairs(votes), new RaterOptions { Samples = 0 });
            Check(analytic.Samples == 0 && analytic.Models.All(row => row.HalfWidth > 0), "no bootstrap: the posterior's intervals");

            // The record round-trips through JSON (every number finite), and the scale is Elo's.
            var json = EvalJson.Serialize(board);
            Equal(json, EvalJson.Serialize(EvalJson.Deserialize<Ratings>(json)), "a JSON round trip");
            Check(!json.Contains("NaN", StringComparison.Ordinal) && !json.Contains("Infinity", StringComparison.Ordinal), "finite numbers");
            Check(Math.Abs(BradleyTerryRater.WinProbability(1100, 1000) - 0.64) < 0.001, "100 points higher wins about 64% of the time");
            Equal(1000.0, BradleyTerryRater.RatingOf(0), "strength 0 is 1000");
            Check(Math.Abs(BradleyTerryRater.StrengthOf(BradleyTerryRater.RatingOf(0.42)) - 0.42) < 1e-12, "rating and strength convert both ways");
            Check(Math.Abs(BradleyTerryRater.InverseNormal(0.975) - 1.959964) < 1e-6, "the normal quantile");
            Throws<ArgumentException>(() => rater.Rate(ArenaMode.Request, "coding", [], new RaterOptions { Samples = -1 }), "a negative sample count");
            Throws<ArgumentException>(() => rater.Rate(ArenaMode.Request, "coding", [], new RaterOptions { PriorSigma = 0 }), "a zero prior");
            Throws<ArgumentException>(() => rater.Rate(ArenaMode.Request, "coding", [], new RaterOptions { Seed = EvalRandom.MaxSeed + 1 }), "a seed above 53 bits");

            var row = board.Models[0];
            Check(row.RankRange == row.RankLow.ToString(CultureInfo.InvariantCulture) || row.RankRange == $"{row.RankLow}–{row.RankHigh}", "the rank range text");
            var track = LeaderboardTrack.Of(board.Models);
            Check(track.Min <= 750 && track.Max >= 1250 && track.Min % 50 == 0 && track.Max % 50 == 0, "the track holds 750 to 1250 in steps of 50");
            Check(board.Models.All(model => track.Min <= model.Low && model.High <= track.Max), "and every interval");
            Equal(50.0, LeaderboardTrack.Default.Percent(1000), "1000 is the middle of the default track");
            Equal(100.0, LeaderboardTrack.Default.Percent(5000), "clamped");
        }),

        Case("evals.rater.metrics-come-from-the-run-records", () =>
        {
            var root = Temp("evals-rater-metrics");
            try
            {
                var runs = new RunStore(new RunsDb(Path.Join(root, RunsDb.FileName)));
                var webagent = new WebAgentDb(Path.Join(root, "webagent.db"), [CoreMigrations.AppInfo, AgentCompareStore.Migration]);
                webagent.Migrate();
                var agents = new AgentCompareStore(webagent);
                var metrics = new ArenaMetrics(runs, agents);
                var rater = new BradleyTerryRater();
                ModelIdentity a = RaterModels[0], b = RaterModels[1];

                // Request mode: three voted battles; alpha's answers cost 300, 100 and 200 micro-USD with 30, 10 and 20 ms to the first token;
                // bravo is a subscription (list price); one of bravo's runs is gone and one failed, so it has one measured answer.
                RaterRun(runs, "r-a1", 300, 30, 50.0, 1000, 400);
                RaterRun(runs, "r-a2", 100, 10, 70.0, 1200, 600);
                RaterRun(runs, "r-a3", 200, 20, 60.0, 1100, 500);
                RaterRun(runs, "r-b1", 900, 90, 20.0, 1000, 800, billing: "subscription");
                RaterRun(runs, "r-b3", 50, 5, 10.0, 10, 10, status: RunStatus.Error);
                List<VotedBattle> votes =
                [
                    RaterVote(1, [a, b], VoteKind.Pick, "A", runs: ["r-a1", "r-b1"]),
                    RaterVote(2, [b, a], VoteKind.Pick, "B", runs: ["r-b2", "r-a2"]),
                    RaterVote(3, [a, b], VoteKind.Tie, runs: ["r-a3", "r-b3"]),
                ];
                var board = Leaderboards.Build(ArenaMode.Request, "coding", votes, rater, metrics.Of);
                var alpha = board.Rows.Single(row => row.Model.Identity.SameAs(a)).Metrics;
                Equal(3, alpha.Answers, "three answers measured");
                Equal(200L, alpha.MedianCostMicros, "the median cost");
                Equal(20L, alpha.MedianTtftMs, "TTFT p50");
                Equal(60.0, alpha.MedianTokensPerSecond, "the median tokens/s");
                Equal(1100.0, alpha.MedianInputTokens, "the median input tokens");
                Equal(500.0, alpha.MedianOutputTokens, "the median output tokens");
                Check(!alpha.ListPrice && !alpha.MixedBilling, "metered");
                var bravo = board.Rows.Single(row => row.Model.Identity.SameAs(b)).Metrics;
                Equal(1, bravo.Answers, "a missing run and a failed one are not measured");
                Equal(900L, bravo.MedianCostMicros, "its one cost");
                Check(bravo.ListPrice, "a subscription's cost is a list price");
                Equal("2–0–1|0–2–1", board.Ratings.Of(a)!.Record + "|" + board.Ratings.Of(b)!.Record, "the votes");

                // Agent mode: the model's row has the run time, its calls the cost; agent compare's store has the check, files and turns.
                var group = "cmp_raterrun0000000001";
                RaterAgentModel(runs, group + "-a", 240_000, calls: [(120, 1000, 300), (80, 900, 200)]);
                RaterAgentModel(runs, group + "-b", 180_000, calls: [(60, 500, 100)]);
                agents.Add(new AgentCompareRun
                {
                    Id = group,
                    RequestId = "rater-1",
                    Spec = new AgentCompareSpec { Task = "t", ProjectPath = root, Instructions = "i", Models = [] },
                    Order = ["a", "b"],
                    Created = RaterNow,
                    Models =
                    [
                        new AgentCompareModelState
                        {
                            Slot = "a",
                            Check = new AgentCheckResult { State = AgentCheckState.Passed, Command = "dotnet test", Summary = "39 passed" },
                            Diff = new AgentDiffSummary { Files = 3, Summary = "3 files" },
                        },
                        new AgentCompareModelState
                        {
                            Slot = "b",
                            Check = new AgentCheckResult { State = AgentCheckState.Failed, Command = "dotnet test", Summary = "1 failed · 37 passed" },
                            Diff = new AgentDiffSummary { Files = 1, Summary = "1 file" },
                        },
                    ],
                });
                for (var turn = 0; turn < 12; turn++) agents.AddTurn(group + "-a", "op-" + turn);
                var agentVote = RaterVote(9, [a, b], VoteKind.Pick, "A", mode: ArenaMode.Agent, tag: "agent", runs: [group + "-a", group + "-b"]);
                agentVote = agentVote with { Battle = agentVote.Battle with { GroupId = group } };
                var agentBoard = Leaderboards.Build(ArenaMode.Agent, "agent", [agentVote], rater, metrics.Of);
                var first = agentBoard.Rows[0];
                Check(first.Model.Identity.SameAs(a), "the picked run is first");
                Equal(200L, first.Metrics.MedianCostMicros, "cost per run: its calls' total");
                Equal(240_000L, first.Metrics.MedianDurationMs, "run time");
                Equal(1900.0, first.Metrics.MedianInputTokens, "its calls' input tokens");
                Equal(12.0, first.Metrics.MedianTurns, "turns");
                Equal(3.0, first.Metrics.MedianFilesChanged, "files changed");
                Equal("1/1 passed:39 passed", $"{first.Metrics.ChecksPassed}/{first.Metrics.Checks} {first.Metrics.LatestCheck}:{first.Metrics.LatestCheckSummary}", "its check");
                Check(first.Metrics.MedianTtftMs is null && first.Metrics.MedianTokensPerSecond is null, "no TTFT or tokens/s for an agent run");
                var second = agentBoard.Rows[1].Metrics;
                Equal(AgentCheckState.Failed, second.LatestCheck, "the other's check failed");
                Equal(0, second.ChecksPassed, "none passed");

                // The app's service measures the same way, with Settings' provisional threshold.
                var services = new ServiceCollection();
                services.AddSingleton(runs);
                services.AddSingleton(agents);
                EvalsModule.AddEvalsArea(services, new WebAgentOptions { DataDirectory = Path.Join(root, "unused") });
                using var provider = services.BuildServiceProvider();
                var leaderboards = provider.GetRequiredService<ArenaLeaderboards>();
                Check(ReferenceEquals(provider.GetRequiredService<BradleyTerryRater>(), provider.GetRequiredService<BradleyTerryRater>()), "one rater");
                var served = leaderboards.Build(ArenaMode.Request, "coding", votes);
                Equal(EvalJson.Serialize(board), EvalJson.Serialize(served), "the service's leaderboard is the same");
                Equal(EvalsSettings.DefaultProvisionalBelow, served.Ratings.ProvisionalBelow, "no settings store: the default threshold");
                Equal("request:coding", string.Join(",", leaderboards.Tags(votes).Select(tag => EvalJson.Wire(tag.Mode) + ":" + tag.Tag)), "its tags");
                // W5 E5: other units' migrations (the arena's 2500) are registered by the same module; the rater's block stays empty.
                Equal("2000,2001,2002,2003,2004", string.Join(",", services.Where(service => service.ServiceType == typeof(IWebAgentMigration))
                    .Select(service => ((IWebAgentMigration)service.ImplementationInstance!).Id).Where(id => id < 2200).Order()), "the rater adds no migration");
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                DeleteTemp(root);
            }
        }),
    ];

    // Synthetic votes from known strengths: each battle draws its contestants and their columns with EvalRandom; a vote between two answers
    // picks the left one with probability logistic(left − right + bias) (or ties, tieShare of the time); a vote among three or four picks by
    // Plackett-Luce (or, with kinds, sometimes ranks every answer, ties or calls them all bad).
    private static List<VotedBattle> RaterSimulate(long seed, double[] strengths, int count, double bias = 0, int maxContestants = 2,
        double tieShare = 0, bool kinds = false, ArenaMode mode = ArenaMode.Request, string tag = "coding")
    {
        var random = new EvalRandom(seed);
        var votes = new List<VotedBattle>(count);
        for (var index = 0; index < count; index++)
        {
            var size = maxContestants == 2 ? 2 : 2 + random.Next(maxContestants - 1);
            var order = random.Permutation(strengths.Length).Take(size).ToArray();
            var columns = order.Select(model => RaterModels[model]).ToArray();
            VotedBattle vote;
            if (random.NextDouble() < tieShare)
            {
                vote = RaterVote(index + 1, columns, kinds && random.Next(2) == 0 ? VoteKind.AllBad : VoteKind.Tie, mode: mode, tag: tag);
            }
            else if (size == 2)
            {
                var left = BradleyTerryRater.Logistic(strengths[order[0]] - strengths[order[1]] + bias);
                vote = RaterVote(index + 1, columns, VoteKind.Pick, random.NextDouble() < left ? "A" : "B", mode: mode, tag: tag);
            }
            else
            {
                // Plackett-Luce: choose the best among those left, in proportion to e^strength.
                var remaining = Enumerable.Range(0, size).ToList();
                var ranking = new List<string>();
                while (remaining.Count > 0)
                {
                    var total = remaining.Sum(column => Math.Exp(strengths[order[column]]));
                    var draw = random.NextDouble() * total;
                    var chosen = remaining[^1];
                    foreach (var column in remaining)
                    {
                        draw -= Math.Exp(strengths[order[column]]);
                        if (draw < 0) { chosen = column; break; }
                    }
                    ranking.Add(ArenaLetters.Of(chosen));
                    remaining.Remove(chosen);
                }
                vote = kinds && random.Next(3) == 0
                    ? RaterVote(index + 1, columns, VoteKind.Rank, ranking: ranking, mode: mode, tag: tag)
                    : RaterVote(index + 1, columns, VoteKind.Pick, ranking[0], mode: mode, tag: tag);
            }
            votes.Add(vote);
        }
        return votes;
    }

    // A voted battle: the given models in columns A, B, ... (battle ids from the number, so they sort like the votes were cast).
    private static VotedBattle RaterVote(int number, IReadOnlyList<ModelIdentity> columns, VoteKind kind, string? pick = null,
        IReadOnlyList<string>? ranking = null, ArenaMode mode = ArenaMode.Request, string tag = "coding", IReadOnlyList<string>? runs = null)
    {
        var id = EvalIds.Battle + number.ToString("x32", CultureInfo.InvariantCulture);
        var at = RaterNow.AddMinutes(number - 1);
        var battle = new ArenaBattle
        {
            Id = id,
            Mode = mode,
            Tag = tag,
            Status = ArenaStatus.Voted,
            Seed = number,
            Contestants = [.. columns.Select((identity, position) => new ArenaContestant(position, identity, runs?[position]))],
            Created = at,
            Voted = at,
        };
        return new VotedBattle(battle, new ArenaVote { BattleId = id, Mode = mode, Tag = tag, Kind = kind, Pick = pick, Ranking = ranking, Created = at });
    }

    private static IEnumerable<ArenaComparison> RaterPairs(IEnumerable<VotedBattle> votes) =>
        votes.SelectMany(vote => vote.Battle.Comparisons(vote.Vote));

    private static void RaterRun(RunStore runs, string id, long cost, long ttft, double speed, double input, double output, string billing = "metered",
        string status = RunStatus.Completed) =>
        runs.Insert(new RunRecord
        {
            Id = id,
            Op = "op-" + id,
            Source = "arena",
            Status = status,
            Provider = "faux",
            Model = "faux",
            Output = new JsonObject { ["role"] = "assistant" },
            OutputText = "answer",
            Usage = new RunUsage(input, output, 0, 0, input + output),
            CostMicros = cost,
            Billing = billing,
            TtftMs = ttft,
            DurationMs = ttft * 10,
            TokensPerSecond = speed,
            Created = RaterNow,
            Finished = RaterNow,
            Updated = RaterNow,
        });

    private static void RaterAgentModel(RunStore runs, string key, long duration, IReadOnlyList<(long Cost, double Input, double Output)> calls)
    {
        runs.Insert(new RunRecord
        {
            Id = key,
            Op = "agent-compare:" + key,
            Kind = RunKind.Parent,
            Source = "agent-compare",
            Status = RunStatus.Completed,
            Provider = "faux",
            Model = "faux",
            Billing = "metered",
            DurationMs = duration,
            Created = RaterNow,
            Finished = RaterNow,
            Updated = RaterNow,
        });
        for (var index = 0; index < calls.Count; index++)
        {
            runs.Insert(new RunRecord
            {
                Id = key + "-call-" + index,
                Op = key + "-op-" + index,
                Source = "agent-compare",
                Status = RunStatus.Completed,
                Provider = "faux",
                Model = "faux",
                ParentId = key,
                Usage = new RunUsage(calls[index].Input, calls[index].Output, 0, 0, calls[index].Input + calls[index].Output),
                CostMicros = calls[index].Cost,
                Created = RaterNow,
                Finished = RaterNow,
                Updated = RaterNow,
            });
        }
    }
}
