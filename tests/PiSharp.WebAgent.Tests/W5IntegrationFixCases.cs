// Decision 0011 (run W5, integration fix 1): the cases that would have caught the W5 integration review's findings in its integration area.
//   - Blind means blind (brief rule 3) outside the battle page too: while a request battle waits for its vote, History › Runs (its rows,
//     totals, counts and filter options), GET /api/runs (items and total), /api/runs/{id}, its events and raw payloads, the inspector, the
//     re-run spec, HAR and JSON downloads, History's exports and the Playground's ?run= name none of its contestants; once the vote is in,
//     they show again, pinned (so runs.db's retention keeps the leaderboard's metrics). RunBlinds.Visible leaves out a hidden agent compare
//     run's whole tree (its top row, its models' rows and their calls) and a hidden request group, and nothing else.
//   - An agent battle is recorded under source arena (brief rule 4: AgentCompareRunner takes the source and title), and agent compare's JSON
//     API, event stream, diff, keep preview, writes and run fragments answer its run 409 (blind) until the vote; the list leaves it out.
//   - A blind round's Spend Guard refusal (its message and the confirm modal) names neither provider nor model.
//   - A suite run's baseline pins its answers' runs.
//   - Settings › Tools lists "Always allowed this session" (every chat's grants) and revokes one; no Settings page promises a later
//     milestone any more.
// The suite runner's calls sent on their holds are covered where the run is: EvalSuiteCases (one ledger row per call, the hold's) and
// EvalRecoveryCases (a crash leaves the cut-off calls' holds uncertain, never released). Faux models only; nothing calls a provider.
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Evals;
using PiSharp.WebAgent.Evals.Suites;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Spend;

internal static partial class Program
{
    private static readonly string[] W5fBlindPair = ["echo-small", "echo-thinking"];

    internal static IEnumerable<(string, Func<Task>)> W5IntegrationFixCases() =>
    [
        Case("w5-integration.blind.a-request-battle-stays-out-of-history-runs-and-exports-until-the-vote", async () =>
        {
            await using var app = await WebAgentApp.StartAsync("w5f-blind");
            var own = await HistoryRunAsync(app, new JsonObject
            {
                ["provider"] = "faux", ["model"] = "echo",
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = "an ordinary run" }),
            });
            var created = await app.SendAsync(HttpMethod.Post, EvalRoutes.ApiArena, ArenaDraftJson("which model am I", W5fBlindPair), origin: app.Origin);
            Check(created.Status == 200 && created.Body?["posted"]?.GetValue<bool>() == true, "the battle started: " + created.Text);
            var battleId = created.Body!["battleId"]!.GetValue<string>();
            var battlePage = "/compare/arena/" + battleId;
            await WaitUntilAsync(async () => (await app.Get($"{EvalRoutes.ApiArena}/{battleId}")).Body?["status"]?.GetValue<string>() == "ready",
                "both answers are in", TimeSpan.FromSeconds(30));
            var database = Path.Join(app.Root, "data", "webagent.db");
            var row = ReadDb(database, $"SELECT group_id, data FROM eval_battles WHERE id = '{battleId}'", reader => (reader.GetString(0), reader.GetString(1))).Single();
            var contestants = EvalJson.Deserialize<ArenaBattle>(row.Item2).Contestants.Select(item => item.RunId!).ToList();
            Equal(2, contestants.Count, "two contestants' runs");
            var secrets = new List<string>(W5fBlindPair) { "Echo small", "Echo thinking", row.Item1 };
            secrets.AddRange(contestants);

            // Lists: History › Runs (rows, totals, filter options) and /api/runs agree on the one ordinary run.
            var history = await app.Get("/history");
            Equal(200, history.Status, "History");
            ArenaBlind(history.Text, secrets, "History › Runs during the round");
            Check(history.Text.Contains("data-run-row=\"" + own + "\"", StringComparison.Ordinal), "the ordinary run is listed");
            Equal(1, Regex.Matches(history.Text, "data-run-row=\"").Count, "and nothing else");
            var arenaOnly = await app.Get("/history?source=arena");
            ArenaBlind(arenaOnly.Text, secrets, "History filtered to the arena");
            var list = await app.Get("/api/runs?limit=100");
            Equal(1L, list.Body?["total"]?.GetValue<long>(), "/api/runs counts the ordinary run alone: " + list.Text);
            ArenaBlind(list.Text, secrets, "/api/runs during the round");
            var grouped = await app.Get("/api/runs?group=" + row.Item1);
            Check(grouped.Body?["items"]?.AsArray().Count == 0 && grouped.Body["total"]?.GetValue<long>() == 0, "asking for the round's group finds nothing: " + grouped.Text);

            // One run: JSON answers 409 blind with the battle's page, the inspector shows the notice, downloads refuse.
            foreach (var id in contestants)
            {
                foreach (var path in new[] { $"/api/runs/{id}", $"/api/runs/{id}/raw", $"/api/runs/{id}/events", $"/api/history/runs/{id}/spec",
                             $"/api/history/runs/{id}/har", $"/api/history/runs/{id}/json" })
                {
                    var answer = await app.Get(path);
                    Equal(409, answer.Status, path + ": " + answer.Text);
                    Check(answer.Body?["error"]?["code"]?.GetValue<string>() == RunBlinds.Code && answer.Body["error"]!["battle"]?.GetValue<string>() == battlePage,
                        path + " says the run is blind and links its battle: " + answer.Text);
                    ArenaBlind(answer.Text, secrets.Where(secret => secret != id), path);
                }
                var inspector = await app.Get("/runs/" + id);
                Equal(200, inspector.Status, "the inspector answers with a notice");
                Check(inspector.Text.Contains("data-run-blind", StringComparison.Ordinal) && inspector.Text.Contains("href=\"" + battlePage + "\"", StringComparison.Ordinal),
                    "the inspector's blind notice links the battle");
                ArenaBlind(ArenaMain(inspector.Text), secrets, "the inspector during the round");
                var playground = await app.Get("/playground?run=" + id);
                Check(WebUtility.HtmlDecode(playground.Text).Contains("part of a blind round", StringComparison.Ordinal)
                    && !playground.Text.Contains("which model am I", StringComparison.Ordinal), "the Playground does not load a blind run");
            }
            var csv = await app.Get("/api/history/export?format=csv");
            ArenaBlind(csv.Text, secrets, "the CSV export");
            Check(csv.Text.Contains(own, StringComparison.Ordinal), "the export has the ordinary run");
            var chosen = await app.Get($"/api/history/export?format=json&ids={string.Join(',', contestants)},{own}");
            ArenaBlind(chosen.Text, secrets, "an export of chosen runs");
            Check(chosen.Text.Contains(own, StringComparison.Ordinal), "a chosen export keeps the visible run");

            // The vote reveals them where they were, pinned.
            var vote = await app.SendAsync(HttpMethod.Post, $"{EvalRoutes.ApiArena}/{battleId}/vote", new JsonObject { ["kind"] = "pick", ["pick"] = "A" }, origin: app.Origin);
            Equal(200, vote.Status, "voted: " + vote.Text);
            var after = await app.Get("/api/runs?limit=100");
            Equal(3L, after.Body?["total"]?.GetValue<long>(), "after the vote /api/runs counts the round's runs too: " + after.Text);
            var revealed = await app.Get("/history");
            Check(contestants.All(id => revealed.Text.Contains("data-run-row=\"" + id + "\"", StringComparison.Ordinal)), "History lists them");
            foreach (var id in contestants)
            {
                Equal(200, (await app.Get($"/api/runs/{id}")).Status, "the run answers after the vote");
                Check(!(await app.Get("/runs/" + id)).Text.Contains("data-run-blind", StringComparison.Ordinal), "the inspector shows it after the vote");
            }
            var pinned = ReadDb(Path.Join(app.Root, "data", RunsDb.FileName), $"SELECT id, pinned FROM runs WHERE group_id = '{row.Item1}'",
                reader => (reader.GetString(0), reader.GetInt64(1)));
            Check(pinned.Count == 2 && pinned.All(item => item.Item2 == 1), "a vote pins the contestants' runs: " + string.Join(",", pinned));
            Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
        }),

        Case("w5-integration.blind.visible-leaves-out-a-hidden-tree-and-nothing-else", () =>
        {
            var root = Temp("w5f-visible");
            try
            {
                var store = new RunStore(new RunsDb(Path.Join(root, RunsDb.FileName)));
                var now = DateTimeOffset.UtcNow;
                store.Insert(RunsRow("acrun", now, kind: RunKind.Parent) with { Source = "arena" });
                store.Insert(RunsRow("acrun-A", now, kind: RunKind.Parent) with { Source = "arena", ParentId = "acrun" });
                store.Insert(RunsRow("turn1", now) with { Source = "arena", ParentId = "acrun-A" });
                store.Insert(RunsRow("req1", now) with { Source = "arena", GroupId = "g-blind" });
                store.Insert(RunsRow("judge", now) with { Source = "drift", GroupId = "g-open", ParentId = "kept" });
                store.Insert(RunsRow("kept", now) with { GroupId = "g-open" });
                store.Insert(RunsRow("alone", now));
                var hidden = new[] { "acrun", "g-blind" };
                var shown = store.List(new RunQuery { HiddenGroups = hidden, Limit = 100 });
                Equal("alone,judge,kept", string.Join(",", shown.Items.Select(run => run.Id).Order(StringComparer.Ordinal)), "only the runs no round hides");
                Equal(3L, shown.Total, "and its total agrees");
                Equal(3L, store.Totals(new RunQuery { HiddenGroups = hidden }).Count, "so do History's totals");
                Equal(7L, store.List(new RunQuery { HiddenGroups = [], Limit = 100 }).Total, "nothing hidden: everything");
                var (sql, parameters) = RunBlinds.Visible("r", hidden);
                Check(sql.StartsWith("NOT (", StringComparison.Ordinal) && parameters.Count == 2 && sql.Contains("r.parent_id", StringComparison.Ordinal), "the condition: " + sql);
                Equal("1 = 1", RunBlinds.Visible("r", []).Sql, "no hidden group: no condition");
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            }
            finally
            {
                DeleteTemp(root);
            }
            return Task.CompletedTask;
        }),

        Case("w5-integration.blind.an-agent-battle-is-arena-and-agent-compare-answers-its-run-409-until-the-vote", async () =>
        {
            await using var app = await CompareApp.StartAsync("w5f-agent", ToolApprovalMode.Auto);
            var created = await app.App.SendAsync(HttpMethod.Post, EvalRoutes.ApiArena, new JsonObject
            {
                ["mode"] = "agent",
                ["agent"] = new JsonObject
                {
                    ["requestId"] = "w5f-agent-1",
                    ["task"] = "!echo done > result.txt",
                    ["projectPath"] = app.Project,
                    ["models"] = Models("echo", "echo-small"),
                    ["isolation"] = "copy",
                    ["approval"] = "auto",
                },
            }, origin: app.App.Origin);
            Equal(200, created.Status, "created: " + created.Text);
            var battleId = created.Body!["battleId"]!.GetValue<string>();
            var battlePage = "/compare/arena/" + battleId;
            var run = ReadDb(Path.Join(app.Data, "webagent.db"), $"SELECT group_id FROM eval_battles WHERE id = '{battleId}'", reader => reader.GetString(0)).Single();
            var secrets = new[] { "echo-small", "Echo small", "faux/echo", "providers/faux" };

            var list = await app.App.Get("/api/compare/agent");
            Check(list.Status == 200 && !list.Text.Contains(run, StringComparison.Ordinal), "agent compare's list leaves the battle's run out: " + Cut(list.Text));
            foreach (var path in new[] { $"/api/compare/agent/{run}", $"/api/compare/agent/{run}/events", $"/api/compare/agent/{run}/diff?slot=A",
                         $"/api/compare/agent/{run}/keep-preview?slot=A" })
            {
                var answer = await app.App.Get(path);
                Equal(409, answer.Status, path + ": " + Cut(answer.Text));
                Check(answer.Body?["error"]?["code"]?.GetValue<string>() == RunBlinds.Code && answer.Body["error"]!["battle"]?.GetValue<string>() == battlePage,
                    path + " is blind: " + answer.Text);
                ArenaBlind(answer.Text, secrets, path);
            }
            var stop = await app.App.SendAsync(HttpMethod.Post, $"/api/compare/agent/{run}/stop", new JsonObject(), origin: app.App.Origin);
            Equal(409, stop.Status, "a write answers 409 too: " + stop.Text);
            foreach (var path in new[] { $"/compare/agent/runs/{run}", $"/compare/agent/runs/{run}/workspace", $"/compare/agent/runs/{run}/diff?slot=A",
                         $"/compare/agent/runs/{run}/keep?slot=A" })
            {
                var fragment = await app.App.Get(path);
                Equal(409, fragment.Status, path + ": " + Cut(fragment.Text));
                Check(fragment.Text.Contains(battlePage, StringComparison.Ordinal), path + " links the battle: " + Cut(fragment.Text));
                ArenaBlind(fragment.Text, secrets, path);
            }
            var inspector = await app.App.Get("/runs/" + run);
            Check(inspector.Text.Contains("data-run-blind", StringComparison.Ordinal), "the inspector of its top row shows the notice");
            var runs = await app.App.Get("/api/runs?limit=100");
            Check(!runs.Text.Contains(run, StringComparison.Ordinal), "/api/runs leaves its rows out: " + Cut(runs.Text));

            await WaitUntilAsync(async () => (await app.App.Get($"{EvalRoutes.ApiArena}/{battleId}")).Body?["status"]?.GetValue<string>() is "ready" or "void",
                "both agents finish", TimeSpan.FromSeconds(90));
            var rows = ReadDb(Path.Join(app.Data, RunsDb.FileName), $"SELECT id, source, COALESCE(parent_id, ''), title FROM runs WHERE id = '{run}' OR parent_id = '{run}' " +
                $"OR parent_id IN (SELECT id FROM runs WHERE parent_id = '{run}')", reader => (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? "" : reader.GetString(3)));
            Check(rows.Count >= 3 && rows.All(item => item.Item2 == "arena"), "the battle's rows (top, models and calls) are source arena: " + string.Join("; ", rows));
            Check(rows.Single(item => item.Item1 == run).Item4 is { Length: > 0 } title && !title.Contains("echo", StringComparison.OrdinalIgnoreCase),
                "its top row's title names no model");
            var spend = ReadDb(Path.Join(app.Data, "webagent.db"), $"SELECT DISTINCT source FROM spend_reservations WHERE run LIKE '{run}%'", reader => reader.GetString(0));
            Check(spend.Count > 0 && spend.All(source => source == "arena"), "its Spend Guard calls are arena's: " + string.Join(",", spend));

            var vote = await app.App.SendAsync(HttpMethod.Post, $"{EvalRoutes.ApiArena}/{battleId}/vote", new JsonObject { ["kind"] = "tie" }, origin: app.App.Origin);
            Equal(200, vote.Status, "voted: " + vote.Text);
            Equal(200, (await app.App.Get($"/api/compare/agent/{run}")).Status, "after the vote the run answers");
            Equal(200, (await app.App.Get($"/compare/agent/runs/{run}")).Status, "and its fragment");
            Check((await app.App.Get("/api/compare/agent")).Text.Contains(run, StringComparison.Ordinal), "and the list has it");
            Equal(1L, ReadDb(Path.Join(app.Data, RunsDb.FileName), $"SELECT pinned FROM runs WHERE id = '{run}'", reader => reader.GetInt64(0)).Single(),
                "the vote pins the agent battle's run (its models and calls with it)");
        }),

        Case("w5-integration.blind.a-blind-rounds-spend-refusal-names-no-model", async () =>
        {
            var root = Temp("w5f-spend");
            try
            {
                await using var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = Path.Join(root, "data"), Arguments = ["--faux-metered"] });
                var cap = await app.SendAsync(HttpMethod.Post, "/api/spend/caps",
                    new JsonObject { ["scope"] = "everything", ["period"] = "request", ["limit"] = "0.01", ["action"] = "ask" }, origin: app.Origin);
                Check(cap.Status == 200, "a per-request Ask cap: " + cap.Text);
                var draft = ArenaDraftJson("over the cap", W5fBlindPair);
                draft["draft"]!["maxTokens"] = 4096;
                var asked = await app.SendAsync(HttpMethod.Post, EvalRoutes.ApiArena, draft, origin: app.Origin);
                Check(asked.Status == 200 && asked.Body?["posted"]?.GetValue<bool>() == false, "the round is asked about: " + asked.Text);
                var html = asked.Body!["spend"]?["html"]?.GetValue<string>() ?? "";
                Check(html.Contains("modal-spend-confirm", StringComparison.Ordinal), "with the confirm modal: " + Cut(asked.Text));
                // The answer's fields without the modal (whose JSON escapes would read as text: ">Choose"), then the modal as HTML.
                var fields = asked.Body.DeepClone().AsObject();
                fields["spend"]!["html"] = null;
                ArenaBlind(fields.ToJsonString(), ["echo", "faux", "Echo small", "Echo thinking"], "the blind round's refusal");
                ArenaBlind(html, ["echo", "faux"], "the blind round's confirm modal");
                Check(WebUtility.HtmlDecode(html).Contains(SpendPages.BlindModel, StringComparison.Ordinal), "the modal says a blind round's model instead");
            }
            finally
            {
                DeleteTemp(root);
            }
        }),

        Case("w5-integration.suites.accepting-a-baseline-pins-its-answers", async () =>
        {
            await using var host = SuHost.Create("w5f-baseline");
            var (suite, _) = SuSeed(host, "Pinned baseline", targets: [new SuiteTarget("faux", "echo"), new SuiteTarget("faux", "echo-thinking")], k: 1, cases: 2);
            var start = await host.Runner.StartAsync(suite.Id, new SuiteRunRequest());
            var run = await SuFinished(host, start.Run!.Id);
            Equal(SuiteRunStatus.Complete, run.Status, "complete");
            var results = host.Store.Results(run.Id);
            Check(results.All(result => host.Runs.Get(result.CallRunId!)?.Pinned == false), "answers are not pinned before a baseline");
            var echo = run.Targets.Single(target => target.Model == "echo");
            host.Suites.AcceptBaseline(suite.Id, run.Id, [echo.Id], replaceReferences: false);
            Check(results.Where(result => result.TargetId == echo.Id).All(result => host.Runs.Get(result.CallRunId!)?.Pinned == true),
                "the accepted target's answers are pinned, so runs.db's retention keeps them");
            Check(results.Where(result => result.TargetId != echo.Id).All(result => host.Runs.Get(result.CallRunId!)?.Pinned == false), "the other target's are not");
        }),

        Case("w5-integration.settings.tools-lists-and-revokes-session-grants", async () =>
        {
            await using var chat = await ChatApp.StartAsync("w5f-grants");
            var id = await chat.CreateAsync("hello", "r1");
            await chat.WaitForAsync(id, "Echo: hello");
            var empty = await chat.App.Get("/settings/tools");
            Check(empty.Text.Contains("data-session-grants", StringComparison.Ordinal) && !Regex.IsMatch(empty.Text, "data-session-grants-empty[^>]*hidden"),
                "no grant yet, and the card says so");
            Check(!empty.Text.Contains("later milestone", StringComparison.OrdinalIgnoreCase), "Tools promises no later milestone");
            Equal(200, (await chat.PostAsync($"/api/chats/{id}/messages", new JsonObject { ["requestId"] = "m1", ["text"] = MarkerCommand })).Status, "sent");
            JsonNode? approvals = null;
            await WaitUntilAsync(async () => (approvals = (await chat.App.Get($"/api/chats/{id}/approvals")).Body!["items"])!.AsArray().Count == 1, "the call asks",
                TimeSpan.FromSeconds(30));
            var task = approvals![0]!["taskId"]!.GetValue<long>();
            Equal(200, (await chat.PostAsync($"/api/chats/{id}/approvals/{task}", new JsonObject { ["decision"] = "allowChat" })).Status, "always allowed in this chat");
            await WaitUntilAsync(async () => (await chat.App.Get($"/api/chats/{id}/grants")).Body?["items"]?.AsArray().Count == 1, "the grant is kept", TimeSpan.FromSeconds(30));
            var granted = await chat.App.Get("/settings/tools");
            var rows = Regex.Matches(granted.Text, "data-session-grant data-chat=\"([^\"]+)\" data-tool=\"([^\"]+)\"");
            Equal(1, rows.Count, "Settings › Tools lists the grant: " + Cut(granted.Text));
            Equal(id, rows[0].Groups[1].Value, "under its chat");
            Equal("bash", rows[0].Groups[2].Value, "for bash");
            Check(granted.Text.Contains("href=\"/chat/" + id + "\"", StringComparison.Ordinal) && granted.Text.Contains("data-grant-revoke", StringComparison.Ordinal), "with its chat and Revoke");
            Check(Regex.IsMatch(granted.Text, "data-session-grants-empty[^>]*hidden"), "the empty line hides");
            var script = await chat.App.Get("/js/settings-forms.js");
            Check(script.Text.Contains("data-grant-revoke", StringComparison.Ordinal) && script.Text.Contains("/grants/revoke", StringComparison.Ordinal), "the page's script revokes");
            var command = WebUtility.HtmlDecode(Regex.Match(granted.Text, "data-session-grant [^>]*data-command=\"([^\"]*)\"").Groups[1].Value);
            var revoked = await chat.PostAsync($"/api/chats/{id}/grants/revoke", new JsonObject { ["tool"] = "bash", ["command"] = command.Length == 0 ? null : command });
            Check(revoked.Status == 200 && revoked.Body?["revoked"]?.GetValue<bool>() == true, "what the button posts revokes it: " + revoked.Text);
            var after = await chat.App.Get("/settings/tools");
            Check(!after.Text.Contains("data-session-grant ", StringComparison.Ordinal), "the grant is gone from Settings");
        }),
    ];
}
