// Decision 0011 (run W5, E5): the Arena (blind Compare). The shuffle (EvalRandom.Permutation, a seeded Fisher–Yates) passes a uniformity
// chi-square test for 2, 3 and 4 contestants, and every battle's columns replay from its stored seed. Over the priced faux models in
// process: a request battle runs as one run group under source arena, held until every answer is in, then ready; a vote is checked (not
// before the answers are in, a letter of the battle, ranking only with 3 or more, one vote: the same again is answered, another refused);
// a stopped battle is void, revealed and never counted; a Spend Guard refusal leaves a battle that starts with the user's answer. Votes feed
// the rater (E1's leaderboards), and agent and request votes stay apart even under the same tag. End to end over the built app (--faux):
// before the vote no contestant's model, provider, run or group appears in any response the page gets (the battle page, its body fragment,
// the JSON and the SSE frames), with stream anyway too; after the vote the page reveals them, with metrics. An agent battle (faux agents
// in copies of a small project) names no model in its page, its JSON or its diffs before the vote, and R6's run page of it waits for the
// vote; Compare's setup pages carry the Blind switch. Fix 1 (arena): before any answer shows, not even the bare model id (echo) appears;
// outside the battle, History, /api/runs, the inspector, Settings › Data, History › Spend, the ledger's JSON and CSV and the Playground name
// no contestant until the vote (the ledger no run or group either), and R6's run page can't be opened past its gate (a blank ?run=, both
// parameters) nor its fragments read.
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using PiSharp.WebAgent.Compare;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Evals;
using PiSharp.WebAgent.Evals.Arena;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Spend;

internal static partial class Program
{
    // What would name a faux contestant: the model ids and names, provider/model, and the provider's logo.
    private static readonly string[] ArenaFauxNames = ["echo-small", "echo-thinking", "Echo small", "Echo thinking", "faux/echo", "providers/faux"];

    private static readonly string[] ArenaThree = ["echo", "echo-small", "echo-thinking"];

    // The bare faux model id (any case: echo, Echo), which names a contestant too wherever no answer or task text could hold it.
    private static readonly string[] ArenaBareNames = ["echo"];

    internal static IEnumerable<(string, Func<Task>)> ArenaCases() =>
    [
        Case("arena.shuffle.passes-a-uniformity-chi-square-test", () =>
        {
            // Critical values of chi-square at p = 0.001 for n! − 1 degrees of freedom (1, 5, 23): a fair shuffle fails 1 time in 1,000.
            var critical = new Dictionary<int, double> { [2] = 10.828, [3] = 20.515, [4] = 49.728 };
            var seeds = new Random(20261030);
            foreach (var count in new[] { 2, 3, 4 })
            {
                var orders = Enumerable.Range(1, count).Aggregate(1, (total, item) => total * item);
                var samples = orders * 1000;
                var seen = new Dictionary<string, int>(StringComparer.Ordinal);
                var positions = new int[count, count];
                for (var sample = 0; sample < samples; sample++)
                {
                    var seed = seeds.NextInt64(0, EvalRandom.MaxSeed);
                    var permutation = new EvalRandom(seed).Permutation(count);
                    Check(permutation.Order().SequenceEqual(Enumerable.Range(0, count)), "a permutation of every column");
                    Check(new EvalRandom(seed).Permutation(count).SequenceEqual(permutation), "the same seed gives the same order");
                    var key = string.Join(",", permutation);
                    seen[key] = seen.GetValueOrDefault(key) + 1;
                    for (var position = 0; position < count; position++) positions[permutation[position], position]++;
                }
                Equal(orders, seen.Count, $"every order of {count} appears");
                var expected = (double)samples / orders;
                var chi = seen.Values.Sum(observed => (observed - expected) * (observed - expected) / expected);
                Check(chi < critical[count], $"{count} contestants: chi-square {chi.ToString("0.00", CultureInfo.InvariantCulture)} < {critical[count]} (df {orders - 1})");
                // Each model lands in each column about equally often (a looser check of the same uniformity, per column).
                var perCell = (double)samples / count;
                for (var model = 0; model < count; model++)
                    for (var position = 0; position < count; position++)
                        Check(Math.Abs(positions[model, position] - perCell) < perCell * 0.1, $"model {model} in column {position}: {positions[model, position]} of {samples}");
            }
        }),

        Case("arena.request.a-battle-is-a-shuffled-arena-group-held-until-every-answer-is-in", async () =>
        {
            await using var host = ArenaHost.Faux("arena-group");
            var draft = ArenaDraft("What is two plus two?", ArenaThree) with { Thinking = "low", MaxTokens = 2_000 };
            // A per-column override is cleared: a blind round uses the shared settings.
            draft = draft with { Columns = [draft.Columns[0], draft.Columns[1], draft.Columns[2] with { Thinking = "high", Temperature = 0.3 }] };
            var orders = new HashSet<string>(StringComparer.Ordinal);
            for (var round = 0; round < 8; round++)
            {
                var start = await host.Arena.CreateRequestAsync(draft);
                Check(start.Posted, "the round starts: " + start.Refusal?.Message);
                var battle = start.Battle;
                Check(EvalIds.IsValid(EvalIds.Battle, battle.Id) && battle.GroupId is not null && battle.Contestants.All(item => item.RunId is not null), "a battle with its group and runs");
                var order = new EvalRandom(battle.Seed).Permutation(3);
                for (var position = 0; position < 3; position++)
                    Equal(ArenaThree[order[position]], battle.Contestants[position].Identity.Model, $"column {ArenaLetters.Of(position)} holds the model its stored seed put there");
                orders.Add(string.Join(",", order));
                Equal("low", battle.Contestants.Single(item => item.Identity.Model == "echo-thinking").Identity.Thinking, "the override was cleared: the shared level applies");
                Equal(null, battle.Contestants.Single(item => item.Identity.Model == "echo-thinking").Identity.Temperature, "and no temperature override");
                foreach (var contestant in battle.Contestants)
                {
                    var record = await RunsFinishedAsync(host.Runs, contestant.RunId!);
                    Equal(RunStatus.Completed, record.Status, "each answer completes");
                    Equal("arena", record.Source, "under source arena (brief rule 4)");
                    Equal(battle.GroupId, record.GroupId, "in the battle's run group");
                    Equal(ArenaService.RunTitle, record.Title, "titled without a model");
                }
                var ready = host.Arena.Require(battle.Id);
                Equal(ArenaStatus.Ready, ready.Status, "every answer in: voting opens");
                var progress = host.Arena.Progress(ready);
                Check(progress.Finished == 3 && progress.Answers.All(answer => answer.Done && answer.Text is { Length: > 0 }), "with every answer's text");
            }
            Check(orders.Count > 1, "rounds are shuffled differently: " + string.Join(" | ", orders));

            // Held: a slow answer keeps the battle held, and its text stays hidden unless stream anyway is on.
            var slow = await host.Arena.CreateRequestAsync(ArenaDraft("slow: " + string.Join(' ', Enumerable.Range(1, 60).Select(item => "w" + item)), ["echo", "echo-small"]));
            var held = host.Arena.Require(slow.Battle.Id);
            Equal(ArenaStatus.Held, held.Status, "held while answers run");
            Check(host.Arena.Progress(held).Answers.All(answer => answer.Text is null), "no text while held");
            var streaming = host.Arena.SetStreamAnyway(held.Id, true);
            await WaitUntilAsync(() => Task.FromResult(host.Arena.Progress(host.Arena.Require(held.Id)).Answers.Any(answer => answer.Text is { Length: > 0 })
                || host.Arena.Require(held.Id).Status != ArenaStatus.Held), "stream anyway shows text as it comes", TimeSpan.FromSeconds(20));
            Check(streaming.StreamAnyway, "stream anyway is stored");
            foreach (var contestant in slow.Battle.Contestants) await RunsFinishedAsync(host.Runs, contestant.RunId!);
            Equal(ArenaStatus.Ready, host.Arena.Require(held.Id).Status, "then ready");

            // Two columns of the same model with the same settings rate nothing: refused before anything is made or sent.
            var before = host.Runs.Count();
            var same = await ThrowsAsync<ArgumentException>(() => host.Arena.CreateRequestAsync(ArenaDraft("same", ["echo", "echo"])), "the same model twice");
            Check(same.Message.Contains("same model", StringComparison.Ordinal), same.Message);
            Equal(before, host.Runs.Count(), "nothing ran");
            await ThrowsAsync<ArgumentException>(() => host.Arena.CreateRequestAsync(ArenaDraft("one", ["echo"])), "one model is not a battle");
        }),

        Case("arena.vote.rules-stop-and-void", async () =>
        {
            await using var host = ArenaHost.Faux("arena-vote");
            var slow = await host.Arena.CreateRequestAsync(ArenaDraft("slow: " + string.Join(' ', Enumerable.Range(1, 50).Select(item => "v" + item)), ArenaThree));
            var id = slow.Battle.Id;
            var early = Throws<InvalidOperationException>(() => host.Arena.Vote(id, VoteKind.Pick, "A", null, "coding"), "no vote while held");
            Check(early.Message.Contains("every answer", StringComparison.Ordinal), early.Message);
            foreach (var contestant in slow.Battle.Contestants) await RunsFinishedAsync(host.Runs, contestant.RunId!);
            Equal(ArenaStatus.Ready, host.Arena.Require(id).Status, "ready");
            Throws<ArgumentException>(() => host.Arena.Vote(id, VoteKind.Pick, "D", null, "coding"), "D is not a column of three");
            Throws<ArgumentException>(() => host.Arena.Vote(id, VoteKind.Rank, null, ["A", "A", "B"], "coding"), "a ranking names each column once");
            Throws<ArgumentException>(() => host.Arena.Vote(id, VoteKind.Pick, "A", null, "bad/tag"), "a tag is checked");
            var voted = host.Arena.Vote(id, VoteKind.Rank, null, ["c", "A", "b"], " Coding ");
            Check(voted.Saved && voted.Battle.Status == ArenaStatus.Voted && voted.Vote.Tag == "coding" && voted.Vote.Ranking!.SequenceEqual(["C", "A", "B"]),
                "a ranking is saved, normalized, and the battle revealed");
            var again = host.Arena.Vote(id, VoteKind.Rank, null, ["C", "A", "B"], "coding");
            Check(!again.Saved, "the same vote again is answered, not stored twice");
            Throws<InvalidOperationException>(() => host.Arena.Vote(id, VoteKind.Tie, null, null, "coding"), "a second, different vote is refused");
            Equal(1, ReadDb(host.Database, "SELECT COUNT(*) FROM eval_votes WHERE battle_id = $id".Replace("$id", "'" + id + "'", StringComparison.Ordinal), reader => reader.GetInt32(0)).Single(), "one vote row");
            Equal("coding", host.Arena.ProposedTag(ArenaMode.Request), "the next request battle proposes the tag used last");
            Equal(EvalTags.Agent, host.Arena.ProposedTag(ArenaMode.Agent), "agent battles still propose agent");

            // A two-answer battle cannot be ranked.
            var two = await host.Arena.CreateRequestAsync(ArenaDraft("two", ["echo", "echo-small"]));
            foreach (var contestant in two.Battle.Contestants) await RunsFinishedAsync(host.Runs, contestant.RunId!);
            var rank = Throws<ArgumentException>(() => host.Arena.Vote(two.Battle.Id, VoteKind.Rank, null, ["A", "B"], "coding"), "ranking two");
            Check(rank.Message.Contains("3 or more", StringComparison.Ordinal), rank.Message);

            // Stop: running answers are cancelled, the battle is void (revealed) and can never be voted on or counted.
            var stopped = await host.Arena.CreateRequestAsync(ArenaDraft("slow: " + string.Join(' ', Enumerable.Range(1, 200).Select(item => "s" + item)), ["echo", "echo-small"]));
            var stop = await host.Arena.StopAsync(stopped.Battle.Id);
            Equal(ArenaStatus.Void, stop.Status, "stopped: void");
            foreach (var contestant in stopped.Battle.Contestants) Equal(RunStatus.Aborted, (await RunsFinishedAsync(host.Runs, contestant.RunId!)).Status, "its answers were cancelled");
            Throws<InvalidOperationException>(() => host.Arena.Vote(stopped.Battle.Id, VoteKind.Pick, "A", null, "coding"), "a void battle takes no vote");
            Check(host.Arena.Voted().All(item => item.Battle.Id != stopped.Battle.Id), "and never counts");

            // An answer that fails voids the battle once every answer ended.
            var failing = await host.Arena.CreateRequestAsync(ArenaDraft("error: the provider broke", ["echo", "echo-small"]));
            foreach (var contestant in failing.Battle.Contestants) await RunsFinishedAsync(host.Runs, contestant.RunId!);
            Equal(ArenaStatus.Void, host.Arena.Require(failing.Battle.Id).Status, "a failed answer: void");
            var page = await host.Pages.PageAsync(failing.Battle.Id);
            Check(page is { Revealed: true } && page.Columns.All(column => column.Reveal is not null && column.Error is not null), "void is revealed, with what went wrong");
        }),

        Case("arena.request.a-refused-round-starts-with-the-users-answer", async () =>
        {
            await using var host = ArenaHost.Faux("arena-spend");
            var ledger = host.Services.GetRequiredService<ISpendLedger>();
            await ledger.SaveCapAsync(new SpendCap(0, CapScope.Everything, null, CapPeriod.Request, 1, CapAction.Ask));
            var asked = await host.Arena.CreateRequestAsync(ArenaDraft("ask first", ["echo", "echo-small"]));
            Check(asked is { Posted: false, Refusal.Code: SpendErrors.Ask }, "an Ask cap asks: " + asked.Refusal?.Code);
            Equal(0L, host.Runs.Count(), "nothing ran");
            var waiting = host.Arena.Require(asked.Battle.Id);
            Check(waiting.Status == ArenaStatus.Held && waiting.Contestants.All(item => item.RunId is null), "the battle waits, with no runs");
            var started = await host.Arena.StartAsync(waiting.Id, new RunSpendAnswer(SpendChoice.ProceedOnce));
            Check(started.Posted && started.Battle.Contestants.All(item => item.RunId is not null), "proceed once starts it: " + started.Refusal?.Message);
            foreach (var contestant in started.Battle.Contestants) await RunsFinishedAsync(host.Runs, contestant.RunId!);
            Equal(ArenaStatus.Ready, host.Arena.Require(waiting.Id).Status, "and it runs to ready");
            await ThrowsAsync<InvalidOperationException>(() => host.Arena.StartAsync(waiting.Id, null), "a started battle does not start twice");
            var dismissed = await host.Arena.CreateRequestAsync(ArenaDraft("ask again", ["echo", "echo-small"]));
            Check(!dismissed.Posted && host.Arena.Discard(dismissed.Battle.Id) && host.Arena.Get(dismissed.Battle.Id) is null, "a dismissed refusal is discarded");
        }),

        Case("arena.votes-feed-the-rater-and-agent-and-request-tags-stay-apart", async () =>
        {
            await using var host = ArenaHost.Faux("arena-rater");
            var store = host.Arena.Store;
            var request = new[] { new ModelIdentity("faux", "echo"), new ModelIdentity("faux", "echo-small"), new ModelIdentity("faux", "echo-thinking", "low") };
            var agent = new[] { new ModelIdentity("faux", "agent-one"), new ModelIdentity("faux", "agent-two") };
            string Ready(ArenaMode mode, ModelIdentity[] models, int rotate)
            {
                var battle = store.Add(new ArenaBattle
                {
                    Id = EvalIds.New(EvalIds.Battle),
                    Mode = mode,
                    Seed = rotate,
                    Contestants = [.. models.Select((model, index) => new ArenaContestant(index, models[(index + rotate) % models.Length]))],
                    Created = DateTimeOffset.UtcNow,
                }, new ArenaRound());
                // A battle with no group is void as soon as it is read back: these stand for battles whose answers are in.
                return store.Update(battle.Id, current => current with { Status = ArenaStatus.Ready })!.Id;
            }
            // The first request model wins every request vote; the second agent model wins every agent vote, all under the tag "coding".
            for (var round = 0; round < 6; round++)
            {
                var id = Ready(ArenaMode.Request, request, round);
                var battle = store.Get(id)!;
                var winner = battle.Contestants.Single(item => item.Identity.SameAs(request[0])).Letter;
                Check(host.Arena.Vote(id, VoteKind.Pick, winner, null, "coding").Saved, "a request vote");
            }
            for (var round = 0; round < 4; round++)
            {
                var id = Ready(ArenaMode.Agent, agent, round);
                var battle = store.Get(id)!;
                var winner = battle.Contestants.Single(item => item.Identity.SameAs(agent[1])).Letter;
                Check(host.Arena.Vote(id, VoteKind.Pick, winner, null, round % 2 == 0 ? "coding" : null).Saved, "an agent vote (the default tag: agent)");
            }
            var leaderboards = host.Services.GetRequiredService<ArenaLeaderboards>();
            var votes = host.Arena.Voted();
            Equal(10, votes.Count, "every vote reaches the rater");
            var requestBoard = leaderboards.Build(ArenaMode.Request, "coding", votes);
            Check(requestBoard.Rows.Count == 3 && requestBoard.Rows.All(row => request.Any(model => model.SameAs(row.Model.Identity))), "the request board has only request models");
            Check(requestBoard.Rows[0].Model.Identity.SameAs(request[0]) && requestBoard.Rows[0].Model.Wins == 12, "the request winner leads, with its wins: " + requestBoard.Rows[0].Model.Record);
            var agentCoding = leaderboards.Build(ArenaMode.Agent, "coding", votes);
            Check(agentCoding.Rows.Count == 2 && agentCoding.Rows.All(row => agent.Any(model => model.SameAs(row.Model.Identity))), "the agent board of the same tag has only agent models");
            Check(agentCoding.Rows[0].Model.Identity.SameAs(agent[1]) && agentCoding.Votes == 2, "agent coding: its own two votes");
            var agentDefault = leaderboards.Build(ArenaMode.Agent, EvalTags.Agent, votes);
            Equal(2, agentDefault.Votes, "agent votes without a tag went to agent");
            var tags = leaderboards.Tags(votes);
            Check(tags.Any(tag => tag is { Mode: ArenaMode.Request, Tag: "coding", Votes: 6 }) && tags.Any(tag => tag is { Mode: ArenaMode.Agent, Tag: "coding", Votes: 2 })
                && tags.Any(tag => tag is { Mode: ArenaMode.Agent, Tag: "agent", Votes: 2 }), "the tags are per mode: " + string.Join(", ", tags.Select(tag => tag.Mode + "/" + tag.Tag + "/" + tag.Votes)));
            Check(host.Arena.Tags(ArenaMode.Request).SequenceEqual(["coding", ArenaService.DefaultTag]) && host.Arena.Tags(ArenaMode.Agent).Contains(EvalTags.Agent), "the vote bar's tags are per mode");
            // The result does not depend on the order the votes were cast.
            var reversed = leaderboards.Build(ArenaMode.Request, "coding", votes.Reverse());
            Check(reversed.Rows.Select(row => row.Model.Identity.Key + "=" + row.Model.Rating.ToString("0.000", CultureInfo.InvariantCulture))
                .SequenceEqual(requestBoard.Rows.Select(row => row.Model.Identity.Key + "=" + row.Model.Rating.ToString("0.000", CultureInfo.InvariantCulture))), "vote order makes no difference");
            Check(ArenaPages.LeftShare(votes) is { } share && share is > 0 and < 1, "the left-position share is measured");
        }),

        Case("arena.request.no-contestant-name-before-the-vote-in-html-json-or-sse", async () =>
        {
            await using var app = await WebAgentApp.StartAsync("arena-blind");
            var compare = await app.Get("/compare");
            Check(compare.Text.Contains("id=\"cmpBlind\"", StringComparison.Ordinal) && compare.Text.Contains("data-arena-blind=\"request\"", StringComparison.Ordinal)
                && compare.Text.Contains("/css/arena.css", StringComparison.Ordinal), "Compare's setup has the Blind switch");
            var prompt = "slow: " + string.Join(' ', Enumerable.Range(1, 80).Select(item => "word" + item));
            var created = await app.SendAsync(HttpMethod.Post, EvalRoutes.ApiArena, ArenaDraftJson(prompt, ArenaThree), origin: app.Origin);
            Equal(200, created.Status, "created: " + created.Text);
            Check(created.Body?["posted"]?.GetValue<bool>() == true, "posted: " + created.Text);
            var id = created.Body!["battleId"]!.GetValue<string>();
            var url = created.Body["url"]!.GetValue<string>();
            Equal("/compare/arena/" + id, url, "the battle's page");
            var database = Path.Join(app.Root, "data", "webagent.db");
            var row = ReadDb(database, $"SELECT group_id, data FROM eval_battles WHERE id = '{id}'", reader => (reader.GetString(0), reader.GetString(1))).Single();
            var battle = EvalJson.Deserialize<ArenaBattle>(row.Item2);
            var secrets = ArenaFauxNames.Concat([row.Item1]).Concat(battle.Contestants.Select(item => item.RunId!)).ToList();
            // Fix 1 (arena): before any answer shows, not even the bare model id (echo, Echo) appears.
            var spoken = secrets.Concat(ArenaBareNames).ToList();
            ArenaBlind(created.Text, spoken, "the create answer");

            // The event stream, read to its end (it ends once the battle is no longer held), while the page is read held.
            var events = ArenaSseAsync(app, $"{EvalRoutes.ApiArena}/{id}/events");
            var page = await app.Get(url);
            Equal(200, page.Status, "the battle page");
            var main = ArenaMain(page.Text);
            ArenaBlind(page.Text, spoken, "the held page");
            var heldBody = await app.Get(url + "/body");
            Equal(200, heldBody.Status, "the held body fragment");
            ArenaBlind(ArenaWithoutAnswers(heldBody.Text), spoken, "the body fragment while held");
            Check(!main.Contains("faux", StringComparison.OrdinalIgnoreCase) && !main.Contains("provider-logo", StringComparison.Ordinal), "the held page's main region names no provider and shows no logo");
            Check(page.Text.Contains("Held until all 3 answers finish", StringComparison.Ordinal) && Regex.Matches(page.Text, "data-arena-col=\"[ABC]\"").Count == 3,
                "three held columns, A to C");
            Check(page.Text.Contains("Metrics and cost hidden until you vote", StringComparison.Ordinal) && !page.Text.Contains("data-arena-metrics", StringComparison.Ordinal), "metrics hidden");
            Check(page.Text.Contains("data-arena-vote=\"pick\"", StringComparison.Ordinal) && page.Text.Contains("Voting opens when all 3 answers are in", StringComparison.Ordinal), "the vote bar, waiting");
            Check(!page.Text.Contains("<script>", StringComparison.Ordinal) && page.Text.Contains("/js/arena.js", StringComparison.Ordinal), "its module, no inline script");
            NoRazorText(page.Text, "the battle page");
            var json = await app.Get($"{EvalRoutes.ApiArena}/{id}");
            Equal("held", json.Body?["status"]?.GetValue<string>(), "held: " + json.Text);
            ArenaBlind(json.Text, spoken, "the battle JSON");
            Check(json.Body?["revealed"] is null, "nothing revealed");

            var stream = await events;
            ArenaBlind(stream, secrets, "the SSE frames");
            Check(stream.Contains("event: state", StringComparison.Ordinal) && stream.Contains("\"status\":\"ready\"", StringComparison.Ordinal), "frames up to ready: " + Cut(stream));
            Check(!stream.Contains("faux", StringComparison.OrdinalIgnoreCase) && !stream.Contains("echo", StringComparison.OrdinalIgnoreCase), "no frame names a provider or model");

            var ready = await app.Get(url);
            ArenaBlind(ready.Text, secrets, "the ready page");
            ArenaBlind(ArenaWithoutAnswers(ready.Text), spoken, "the ready page outside its answers");
            Check(!ArenaMain(ready.Text).Contains("faux", StringComparison.OrdinalIgnoreCase), "the ready page's main region names no provider");
            Equal(3, Regex.Matches(ready.Text, "data-arena-answer").Count, "every answer shows");
            Check(ready.Text.Contains("word80", StringComparison.Ordinal), "the answers show");
            Check(WebUtility.HtmlDecode(ready.Text).Contains("3 of 3 finished · order shuffled by the server", StringComparison.Ordinal), "the status: " + Cut(ArenaMain(ready.Text)));
            var fragment = await app.Get(url + "/body");
            Check(fragment.Status == 200 && fragment.Text.TrimStart().StartsWith("<div class=\"cmp-views\" data-arena-battle", StringComparison.Ordinal), "the body fragment: " + Cut(fragment.Text));
            ArenaBlind(fragment.Text, secrets, "the body fragment");
            ArenaBlind(ArenaWithoutAnswers(fragment.Text), spoken, "the body fragment outside its answers");

            // Fix 1 (arena): outside the battle nothing names a contestant before the vote either (History, /api/runs, the inspector,
            // History › Spend, the ledger's JSON and CSV, the Playground), and the ledger names no run or group of the round.
            var column = battle.Contestants[0].RunId!;
            await ArenaOutsideBlindAsync(app, [.. ArenaFauxNames, .. ArenaBareNames], [row.Item1, .. battle.Contestants.Select(item => item.RunId!)], column,
                "echo-small", "the ready request battle");
            var playground = await app.Get("/playground?run=" + column);
            Equal(200, playground.Status, "the Playground answers");
            Check(WebUtility.HtmlDecode(playground.Text).Contains(PiSharp.WebAgent.Playground.PlaygroundPages.BlindNotice, StringComparison.Ordinal),
                "the Playground refuses a blind battle's run: " + Cut(ArenaMain(playground.Text)));
            ArenaBlind(playground.Text, [row.Item1, .. battle.Contestants.Select(item => item.RunId!), "word80"], "the Playground of a blind battle's run");
            Equal(404, (await app.Get($"/playground/runs/{column}/output")).Status, "the Playground's output fragment of a blind battle's run");
            Equal(404, (await app.Get($"/playground/runs/{column}/error")).Status, "the Playground's error fragment of a blind battle's run");
            var early = await app.SendAsync(HttpMethod.Post, $"{EvalRoutes.ApiArena}/{id}/vote", new JsonObject { ["kind"] = "pick", ["pick"] = "E", ["tag"] = "coding" }, origin: app.Origin);
            Equal(400, early.Status, "a letter the battle does not have: " + early.Text);
            ArenaBlind(early.Text, secrets, "a refused vote");

            var vote = await app.SendAsync(HttpMethod.Post, $"{EvalRoutes.ApiArena}/{id}/vote", new JsonObject { ["kind"] = "pick", ["pick"] = "B", ["tag"] = "coding" }, origin: app.Origin);
            Equal(200, vote.Status, "voted: " + vote.Text);
            Check(vote.Body?["status"]?.GetValue<string>() == "voted" && vote.Body["revealed"]!.AsArray().Count == 3, "the vote reveals: " + vote.Text);
            var picked = vote.Body!["revealed"]!.AsArray().Single(item => item!["letter"]!.GetValue<string>() == "B")!["model"]!.GetValue<string>();
            Equal(battle.At("B")!.Identity.Model, picked, "B was the model the stored shuffle put there");
            var revealed = await app.Get(url);
            Check(revealed.Text.Contains("Vote saved", StringComparison.Ordinal) && revealed.Text.Contains("Your pick", StringComparison.Ordinal)
                && revealed.Text.Contains("faux/echo-small", StringComparison.Ordinal) && revealed.Text.Contains("faux/echo-thinking", StringComparison.Ordinal), "names revealed");
            Check(revealed.Text.Contains("data-arena-metrics", StringComparison.Ordinal) && revealed.Text.Contains("TTFT", StringComparison.Ordinal), "with metrics");
            Check(WebUtility.HtmlDecode(revealed.Text).Contains("Added to coding · 1 vote", StringComparison.Ordinal) && revealed.Text.Contains("data-pin-run=\"" + battle.At("B")!.RunId + "\"", StringComparison.Ordinal),
                "added to the tag, and Pin as test case names the picked answer's run");
            NoRazorText(revealed.Text, "the revealed page");
            var board = await app.Get(EvalRoutes.ApiLeaderboard + "?mode=request&tag=coding");
            Equal(200, board.Status, "the leaderboard: " + board.Text);
            var rows = board.Body!["leaderboard"]!["rows"]!.AsArray();
            Equal(3, rows.Count, "three models rated");
            var top = rows[0]!["model"]!["identity"]!["model"]!.GetValue<string>();
            Equal(picked, top, "the pick leads");
            var runs = ReadDb(Path.Join(app.Root, "data", RunsDb.FileName), $"SELECT source FROM runs WHERE group_id = '{row.Item1}'", reader => reader.GetString(0));
            Check(runs.Count == 3 && runs.All(source => source == "arena"), "three runs under source arena: " + string.Join(",", runs));
            // Once voted, the round is itself again everywhere.
            Equal(200, (await app.Get("/api/runs/" + column)).Status, "the run's JSON after the vote");
            Check(!(await app.Get("/runs/" + column)).Text.Contains("data-run-blind", StringComparison.Ordinal), "the inspector after the vote");
            Check((await app.Get("/api/runs")).Body!["items"]!.AsArray().All(item => item?["blind"] is null), "/api/runs unmasks the round after the vote");
            Check((await app.Get("/api/spend/ledger")).Body!["items"]!.AsArray().All(item => item?["blind"] is null), "the ledger unmasks the round after the vote");
            Check(WebUtility.HtmlDecode((await app.Get("/playground?run=" + column)).Text).Contains("word80", StringComparison.Ordinal), "the Playground opens the run after the vote");

            // Stream anyway: the text so far shows while held, still with no name; Stop all then voids the round and reveals it.
            var second = await app.SendAsync(HttpMethod.Post, EvalRoutes.ApiArena, ArenaDraftJson("slow: " + string.Join(' ', Enumerable.Range(1, 150).Select(item => "part" + item)), ["echo", "echo-small"]), origin: app.Origin);
            var secondId = second.Body!["battleId"]!.GetValue<string>();
            var on = await app.SendAsync(HttpMethod.Post, $"{EvalRoutes.ApiArena}/{secondId}/stream-anyway", new JsonObject { ["on"] = true }, origin: app.Origin);
            Check(on.Status == 200 && on.Body?["streamAnyway"]?.GetValue<bool>() == true, "stream anyway: " + on.Text);
            var secondRow = ReadDb(database, $"SELECT group_id, data FROM eval_battles WHERE id = '{secondId}'", reader => (reader.GetString(0), reader.GetString(1))).Single();
            var secondSecrets = ArenaFauxNames.Concat([secondRow.Item1]).Concat(EvalJson.Deserialize<ArenaBattle>(secondRow.Item2).Contestants.Select(item => item.RunId!)).ToList();
            string? live = null;
            await WaitUntilAsync(async () => Regex.IsMatch(live = (await app.Get($"/compare/arena/{secondId}/body")).Text, "data-arena-streaming>[^<]*part\\d+"),
                "the text so far shows while held", TimeSpan.FromSeconds(20));
            ArenaBlind(live!, secondSecrets, "a streaming body");
            Check(live!.Contains("data-arena-status", StringComparison.Ordinal) && WebUtility.HtmlDecode(live).Contains("streaming as they come", StringComparison.Ordinal), "the status says so");
            var stopped = await app.SendAsync(HttpMethod.Post, $"{EvalRoutes.ApiArena}/{secondId}/stop", new JsonObject(), origin: app.Origin);
            Check(stopped.Status == 200 && stopped.Body?["status"]?.GetValue<string>() == "void" && stopped.Body["revealed"] is JsonArray, "stopped: void and revealed: " + stopped.Text);
            var voidPage = await app.Get($"/compare/arena/{secondId}");
            Check(voidPage.Text.Contains("Not counted", StringComparison.Ordinal) && voidPage.Text.Contains("faux/echo-small", StringComparison.Ordinal), "the void page says so, with names");
            Equal(404, (await app.Get("/compare/arena/ab" + new string('0', 32))).Status, "an unknown battle");
        }),

        Case("arena.agent.no-contestant-name-before-the-vote-and-the-run-page-waits", async () =>
        {
            await using var app = await CompareApp.StartAsync("arena-agent", ToolApprovalMode.Auto);
            var setup = await app.App.Get("/compare?mode=agent");
            Check(setup.Text.Contains("id=\"acBlind\"", StringComparison.Ordinal) && setup.Text.Contains("data-arena-blind=\"agent\"", StringComparison.Ordinal), "the agent setup has the Blind switch");
            var created = await app.App.SendAsync(HttpMethod.Post, EvalRoutes.ApiArena, new JsonObject
            {
                ["mode"] = "agent",
                ["agent"] = new JsonObject
                {
                    ["requestId"] = "arena-agent-1",
                    ["task"] = "!printf done > result.txt",
                    ["projectPath"] = app.Project,
                    ["models"] = Models("echo", "echo-small"),
                    ["isolation"] = "copy",
                    ["approval"] = "auto",
                    ["checkCommand"] = "exit 0",
                },
            }, origin: app.App.Origin);
            Equal(200, created.Status, "created: " + created.Text);
            var id = created.Body!["battleId"]!.GetValue<string>();
            var database = Path.Join(app.Data, "webagent.db");
            var row = ReadDb(database, $"SELECT group_id, data FROM eval_battles WHERE id = '{id}'", reader => (reader.GetString(0), reader.GetString(1))).Single();
            var battle = EvalJson.Deserialize<ArenaBattle>(row.Item2);
            Equal(ArenaMode.Agent, battle.Mode, "an agent battle");
            Equal(EvalTags.Agent, battle.Tag, "proposing the agent tag");
            // Fix 1 (arena): the task and check name no model, so not even the bare id (echo, Echo) may appear before the vote.
            var names = new[] { "echo-small", "Echo small", "faux/echo", "providers/faux" }.Concat(ArenaBareNames).ToList();
            var keys = new[] { row.Item1, row.Item1 + "-a", row.Item1 + "-b" };
            var secrets = names.Concat([row.Item1]).ToList();
            ArenaBlind(created.Text, secrets, "the create answer");
            var events = ArenaSseAsync(app.App, $"{EvalRoutes.ApiArena}/{id}/events", TimeSpan.FromSeconds(120));
            var again = await app.App.SendAsync(HttpMethod.Post, EvalRoutes.ApiArena, new JsonObject
            {
                ["mode"] = "agent",
                ["agent"] = new JsonObject
                {
                    ["requestId"] = "arena-agent-1",
                    ["task"] = "!printf done > result.txt",
                    ["projectPath"] = app.Project,
                    ["models"] = Models("echo", "echo-small"),
                    ["isolation"] = "copy",
                    ["approval"] = "auto",
                },
            }, origin: app.App.Origin);
            Equal(id, again.Body?["battleId"]?.GetValue<string>(), "a repeated create answers the same battle");
            var order = new EvalRandom(battle.Seed).Permutation(2);
            for (var position = 0; position < 2; position++)
                Equal(new[] { "echo", "echo-small" }[order[position]], battle.Contestants[position].Identity.Model, "slot " + ArenaLetters.Of(position) + " holds what the seed put there");

            var named = await app.App.Get("/compare?mode=agent&run=" + row.Item1);
            Check(named.Text.Contains("data-arena-blind-run", StringComparison.Ordinal) && named.Text.Contains("/compare/arena/" + id, StringComparison.Ordinal), "R6's run page waits for the vote");
            ArenaBlind(named.Text, ["echo-small", "Echo small", "faux/echo"], "R6's run page of an unvoted battle");
            var copy = await app.App.Get("/compare?mode=agent&from=" + row.Item1);
            Check(copy.Text.Contains("data-arena-blind-run", StringComparison.Ordinal), "and so does New run from it");
            // Fix 1 (arena): the gate is on the id the page opens, so a blank ?run= or both parameters can't get past it.
            foreach (var gated in new[] { "&run=%20&from=" + row.Item1, "&run=+&from=" + row.Item1, "&run=" + row.Item1 + "&from=" + row.Item1, "&from=" + row.Item1 + "&run=" })
            {
                var answer = await app.App.Get("/compare?mode=agent" + gated);
                Check(answer.Status == 200 && answer.Text.Contains("data-arena-blind-run", StringComparison.Ordinal) && !answer.Text.Contains("data-agent-compare=", StringComparison.Ordinal),
                    $"?mode=agent{gated} waits for the vote: " + Cut(ArenaMain(answer.Text)));
                ArenaBlind(answer.Text, names, $"?mode=agent{gated}");
            }
            foreach (var fragment in new[] { "", "/workspace", "/diff?slot=a", "/keep?slot=b" })
            {
                var answer = await app.App.Get($"/compare/agent/runs/{row.Item1}{fragment}");
                Equal(409, answer.Status, $"agent compare's run fragment {fragment} waits for the vote: " + Cut(answer.Text));
                ArenaBlind(answer.Text, secrets, $"agent compare's run fragment {fragment}");
            }

            JsonNode? state = null;
            await WaitUntilAsync(async () =>
            {
                state = (await app.App.Get($"{EvalRoutes.ApiArena}/{id}")).Body;
                ArenaBlind(state?.ToJsonString() ?? "", secrets, "the battle JSON while it runs");
                return state?["status"]?.GetValue<string>() is "ready" or "void";
            }, "both agents finish", TimeSpan.FromSeconds(90));
            Equal("ready", state!["status"]!.GetValue<string>(), "ready: " + state.ToJsonString());
            var stream = await events;
            ArenaBlind(stream, secrets, "the agent battle's SSE frames");
            Check(stream.Contains("\"status\":\"ready\"", StringComparison.Ordinal), "frames up to ready: " + Cut(stream));
            var page = await app.App.Get("/compare/arena/" + id);
            ArenaBlind(ArenaWithoutAnswers(page.Text), secrets, "the agent battle page");
            Check(!ArenaMain(page.Text).Contains("faux", StringComparison.OrdinalIgnoreCase), "its main region names no provider");
            Check(page.Text.Contains("Agent A", StringComparison.Ordinal) && page.Text.Contains("Turns, time and cost hidden until you vote", StringComparison.Ordinal)
                && page.Text.Contains("data-arena-diff=\"A\"", StringComparison.Ordinal) && page.Text.Contains("id=\"modal-agent-diff\"", StringComparison.Ordinal), "blind agent columns with View diff");
            Check(page.Text.Contains("Vote on the diffs and check results", StringComparison.Ordinal) && page.Text.Contains("value=\"agent\" selected", StringComparison.Ordinal), "the vote bar, tag agent");
            NoRazorText(page.Text, "the agent battle page");
            var diff = await app.App.Get($"/compare/arena/{id}/diff?letter=B");
            Equal(200, diff.Status, "the diff: " + Cut(diff.Text));
            ArenaBlind(diff.Text, secrets, "the blind diff");
            Check(diff.Text.Contains("Changes in B<", StringComparison.Ordinal), "the diff's title is the letter alone: " + Cut(diff.Text));
            Check(diff.Text.Contains("result.txt", StringComparison.Ordinal), "the diff lists the changed file: " + Cut(diff.Text));
            ArenaBlind(diff.Text, ["data-run=", row.Item1], "the diff (no run)");
            Check(diff.Text.Contains("data-arena-diff=\"A\"", StringComparison.Ordinal) && !diff.Text.Contains("data-ac-keep", StringComparison.Ordinal), "its switch asks the arena, and Keep waits for the vote");

            // Fix 1 (arena): outside the battle, nothing names a contestant before the vote: History, /api/runs, the inspector of a model,
            // Settings › Data's workspaces, History › Spend and the ledger's JSON and CSV (which name no run or model key of it either).
            await ArenaOutsideBlindAsync(app.App, names, keys, row.Item1 + "-a", "echo-small", "the ready agent battle");
            var data = await app.App.Get("/settings/data");
            Equal(200, data.Status, "Settings › Data");
            ArenaBlind(data.Text, names, "Settings › Data before the vote");
            Check(WebUtility.HtmlDecode(data.Text) is var dataText && dataText.Contains("blind round · workspace a", StringComparison.Ordinal)
                && dataText.Contains("blind round · workspace b", StringComparison.Ordinal),
                "Settings › Data lists the round's workspaces without their models: " + Cut(ArenaMain(data.Text)));

            var vote = await app.App.SendAsync(HttpMethod.Post, $"{EvalRoutes.ApiArena}/{id}/vote", new JsonObject { ["kind"] = "tie" }, origin: app.App.Origin);
            Equal(200, vote.Status, "voted: " + vote.Text);
            Equal("agent", vote.Body?["vote"]?["tag"]?.GetValue<string>(), "the default tag is agent");
            var revealed = await app.App.Get("/compare/arena/" + id);
            Check(revealed.Text.Contains("faux/echo-small", StringComparison.Ordinal) && revealed.Text.Contains("You voted Tie", StringComparison.Ordinal)
                && revealed.Text.Contains("/compare?mode=agent&amp;run=" + row.Item1, StringComparison.Ordinal), "revealed, with the run's own page for Keep");
            var named2 = await app.App.Get("/compare?mode=agent&run=" + row.Item1);
            Check(!named2.Text.Contains("data-arena-blind-run", StringComparison.Ordinal) && named2.Text.Contains("data-agent-compare=\"run\"", StringComparison.Ordinal), "after the vote R6's page shows the run");
            Equal(200, (await app.App.Get($"/compare/agent/runs/{row.Item1}")).Status, "and its fragment");
            Check(WebUtility.HtmlDecode((await app.App.Get("/settings/data")).Text).Contains("faux/echo-small", StringComparison.Ordinal), "Settings › Data names the models after the vote");
            Check(!(await app.App.Get($"/runs/{row.Item1}-a")).Text.Contains("data-run-blind", StringComparison.Ordinal), "the inspector opens a model after the vote");
            var board = await app.App.Get(EvalRoutes.ApiLeaderboard + "?mode=agent&tag=agent");
            Check(board.Body?["leaderboard"]?["rows"]?.AsArray().Count == 2, "the agent leaderboard: " + board.Text);
            var requestBoard = await app.App.Get(EvalRoutes.ApiLeaderboard + "?mode=request&tag=agent");
            Check(requestBoard.Body?["leaderboard"]?["rows"]?.AsArray().Count is null or 0, "the request board of the same tag has no agent votes: " + requestBoard.Text);
        }),
    ];

    private static CompareDraft ArenaDraft(string prompt, IEnumerable<string> models) =>
        new() { Prompt = prompt, Columns = [.. models.Select(model => new CompareColumnDraft { Provider = "faux", Model = model })] };

    private static JsonObject ArenaDraftJson(string prompt, IEnumerable<string> models) => new()
    {
        ["mode"] = "request",
        ["draft"] = new JsonObject
        {
            ["prompt"] = prompt,
            ["columns"] = new JsonArray([.. models.Select(model => (JsonNode)new JsonObject { ["provider"] = "faux", ["model"] = model })]),
        },
    };

    /// <summary>Fails when <paramref name="text"/> names any of <paramref name="secrets"/> (ignoring case).</summary>
    private static void ArenaBlind(string raw, IEnumerable<string> secrets, string what)
    {
        // Read as the browser would: a name could hide behind character references.
        var text = WebUtility.HtmlDecode(raw);
        foreach (var secret in secrets)
        {
            var at = text.IndexOf(secret, StringComparison.OrdinalIgnoreCase);
            Check(at < 0, $"{what} names no contestant, but has \"{secret}\": …{text.Substring(Math.Max(0, at - 80), Math.Min(text.Length - Math.Max(0, at - 80), 200))}…");
        }
    }

    /// <summary><paramref name="html"/> without the answers' text (the content of <c>data-arena-answer</c> and <c>data-arena-streaming</c>
    /// elements), which is the models' own words: what is left is what the app itself says.</summary>
    private static string ArenaWithoutAnswers(string html)
    {
        foreach (var marker in new[] { "data-arena-answer", "data-arena-streaming" })
        {
            var from = 0;
            while (html.IndexOf(marker, from, StringComparison.Ordinal) is var at and >= 0)
            {
                var open = html.IndexOf('>', at);
                if (open < 0) break;
                // The element is a div: find its end tag, counting the divs inside it.
                var depth = 1;
                var cursor = open + 1;
                var end = html.Length;
                while (depth > 0)
                {
                    var nextOpen = html.IndexOf("<div", cursor, StringComparison.Ordinal);
                    var nextClose = html.IndexOf("</div>", cursor, StringComparison.Ordinal);
                    if (nextClose < 0) break;
                    if (nextOpen >= 0 && nextOpen < nextClose)
                    {
                        depth++;
                        cursor = nextOpen + 4;
                    }
                    else
                    {
                        depth--;
                        end = nextClose;
                        cursor = nextClose + 6;
                    }
                }
                html = html[..(open + 1)] + html[end..];
                from = open + 1;
            }
        }
        return html;
    }

    /// <summary>Fix 1 (arena): what the app shows outside a battle that is ready but not voted. History and <c>/api/runs</c> list its runs
    /// masked and name none of <paramref name="names"/>, and a filter on <paramref name="model"/> leaves them out; the inspector of
    /// <paramref name="run"/> is the blind notice, and its JSON, raw JSON and events answer 409 blind; History › Spend, <c>/api/spend/ledger</c>
    /// and its CSV name none of <paramref name="names"/> or <paramref name="keys"/> (run groups, run ids, model keys), list its requests as
    /// "Arena battle · blind", and leave them out of a filter on <paramref name="model"/>.</summary>
    private static async Task ArenaOutsideBlindAsync(WebAgentApp app, IReadOnlyList<string> names, IReadOnlyList<string> keys, string run, string model, string what)
    {
        var everything = names.Concat(keys).ToList();
        var history = await app.Get("/history");
        Equal(200, history.Status, what + ": History");
        ArenaBlind(history.Text, names, what + ": History");
        Check(history.Text.Contains("data-run-blind", StringComparison.Ordinal), what + ": History lists the round masked: " + Cut(ArenaMain(history.Text)));
        var runs = await app.Get("/api/runs");
        Equal(200, runs.Status, what + ": /api/runs " + runs.Text);
        ArenaBlind(runs.Text, names, what + ": /api/runs");
        Check(runs.Body!["items"]!.AsArray().Any(item => item?["blind"] is not null), what + ": /api/runs lists the round masked: " + Cut(runs.Text));
        foreach (var filter in new[] { "?model=" + model, "?provider=faux", "?q=" + model })
        {
            var filtered = await app.Get("/api/runs" + filter);
            Check(filtered.Status == 200 && filtered.Body!["items"]!.AsArray().All(item => item?["blind"] is null && !keys.Contains(item?["id"]?.GetValue<string>() ?? "")),
                $"{what}: /api/runs{filter} leaves the round out: " + Cut(filtered.Text));
        }
        var inspector = await app.Get("/runs/" + run);
        Check(inspector.Status == 200 && inspector.Text.Contains("data-run-blind", StringComparison.Ordinal), what + ": the inspector waits for the vote: " + Cut(ArenaMain(inspector.Text)));
        ArenaBlind(inspector.Text, everything, what + ": the inspector");
        foreach (var path in new[] { "", "/raw", "/events" })
        {
            var answer = await app.Get($"/api/runs/{run}{path}");
            Check(answer.Status == 409 && answer.Body?["error"]?["code"]?.GetValue<string>() == "blind", $"{what}: /api/runs/(run){path} answers 409 blind: " + Cut(answer.Text));
            ArenaBlind(answer.Text, everything, $"{what}: /api/runs/(run){path}");
        }
        var spend = await app.Get("/history/spend");
        Equal(200, spend.Status, what + ": History › Spend");
        ArenaBlind(ArenaMain(spend.Text), everything, what + ": History › Spend");
        var ledger = await app.Get("/api/spend/ledger");
        Equal(200, ledger.Status, what + ": /api/spend/ledger " + ledger.Text);
        ArenaBlind(ledger.Text, everything, what + ": /api/spend/ledger");
        var items = ledger.Body!["items"]!.AsArray();
        Check(items.Count > 0 && items.All(item => item?["blind"] is not null), what + ": the ledger lists the round's requests masked: " + Cut(ledger.Text));
        Check(spend.Text.Contains("data-spend-blind", StringComparison.Ordinal), what + ": History › Spend lists them masked");
        var csv = await app.Get("/api/spend/ledger.csv");
        Equal(200, csv.Status, what + ": the ledger CSV");
        ArenaBlind(csv.Text, everything, what + ": the ledger CSV");
        Check(csv.Text.Contains(ArenaBlindText.Title, StringComparison.Ordinal), what + ": the CSV lists them masked: " + Cut(csv.Text));
        foreach (var filter in new[] { "?provider=faux&model=" + model, "?provider=faux", "?q=" + model })
        {
            var narrowed = await app.Get("/api/spend/ledger" + filter);
            Check(narrowed.Status == 200 && narrowed.Body!["items"]!.AsArray().Count == 0, $"{what}: /api/spend/ledger{filter} leaves the round's requests out: " + Cut(narrowed.Text));
            var page = await app.Get("/history/spend" + filter);
            Check(page.Status == 200 && !page.Text.Contains("data-spend-blind", StringComparison.Ordinal), $"{what}: /history/spend{filter} leaves them out");
        }
    }

    /// <summary>The page's main region (the shell around it is the same for every page).</summary>
    private static string ArenaMain(string html)
    {
        var start = html.IndexOf("<main", StringComparison.Ordinal);
        var end = html.IndexOf("</main>", StringComparison.Ordinal);
        return start >= 0 && end > start ? html[start..end] : html;
    }

    /// <summary>Reads an event stream to its end (60 s at most).</summary>
    private static async Task<string> ArenaSseAsync(WebAgentApp app, string path, TimeSpan? within = null)
    {
        using var limit = new CancellationTokenSource(within ?? TimeSpan.FromSeconds(60));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(app.Base, path));
        using var response = await app.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
        Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType, "an event stream");
        await using var stream = await response.Content.ReadAsStreamAsync(limit.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(limit.Token);
    }

    /// <summary>The Arena in process over the priced faux models (the run engine, runs.db, the Spend Guard, Compare and the Evals area).</summary>
    private sealed class ArenaHost : IAsyncDisposable
    {
        private readonly ServiceProvider provider;

        private ArenaHost(ServiceProvider provider, string root, WebAgentOptions options)
        {
            this.provider = provider;
            Root = root;
            Options = options;
        }

        public string Root { get; }

        public WebAgentOptions Options { get; }

        public IServiceProvider Services => provider;

        public ArenaService Arena => provider.GetRequiredService<ArenaService>();

        public ArenaPages Pages => provider.GetRequiredService<ArenaPages>();

        public RunStore Runs => provider.GetRequiredService<RunStore>();

        public string Database => Path.Join(Options.DataDirectory, "webagent.db");

        public static ArenaHost Faux(string name)
        {
            var root = Temp(name);
            var options = new WebAgentOptions { DataDirectory = Path.Join(root, "data"), Faux = true, FauxMetered = true };
            options.EnsureDirectories();
            var services = new ServiceCollection().AddLogging();
            services.AddWebAgentCore(options);
            services.AddWebAgentFallbacks(options);
            services.AddSpendArea(options);
            services.AddRunsArea(options);
            CompareModule.AddCompareArea(services, options);
            EvalsModule.AddEvalsArea(services, options);
            var provider = services.BuildServiceProvider();
            provider.GetRequiredService<WebAgentDb>().Migrate();
            return new ArenaHost(provider, root, options);
        }

        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            SqliteClearPools();
            DeleteTemp(Root);
        }
    }
}
