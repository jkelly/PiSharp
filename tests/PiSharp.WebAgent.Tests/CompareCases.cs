// Decision 0011 (run W4, R2): Compare's request mode. In process over the priced faux models (--faux-metered): the planner adapts the
// shared options per column and says so (a thinking level clamped, maxTokens capped, a shared temperature not sent while a model thinks), an
// override a model does not take refuses the run before anything is reserved or sent; a 3-column Run all records one group (three runs.db
// rows of one group id, each reserved under the group's run, and Compare's own record), a re-run sends one column again into the group, and
// the group's reservation is all or nothing (a run cap that two columns fit and three do not refuses all three; an Ask cap asks once for the
// group, and the user's choice lets every column through). Over real routing with a fake provider (no request leaves the process), one
// failing column (an Anthropic 401) ends in error while the others complete, each concurrent column's request carries only its own
// provider's key (never the other's), and neither key reaches runs.db, its WAL, the log or the data folder. The columns and the Metrics tab
// render each state with model text through the sanitizing MarkdownRenderer; end to end, the built app serves GET /compare (and the agent
// stub) with nothing the CSP would block, and a Run all goes round trip through /api/compare, the runs' SSE and the fragments the page swaps in.
// W4 fix (compare): the estimate by Run all is the Spend Guard's group preflight, so a run or daily cap only the columns' total crosses is
// named before Run (in process and over HTTP), the ledger's group check is the reservation's rule and reserves nothing, a waiting approval
// answers the group's preflight without being used up, and a cap near its limit shows one amber chip at the group's total.
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Compare;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Routing;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Spend;

internal static partial class Program
{
    private static readonly string[] CompareScripts = ["compare.js", "compare-request.js"];

    internal static IEnumerable<(string, Func<Task>)> CompareCases() =>
    [
        Case("compare.plan.shared-options-adapt-per-column-and-an-override-refuses", async () =>
        {
            await using var host = CompareHost.Faux("cmp-plan");
            var draft = new CompareDraft
            {
                Prompt = "plan me",
                Thinking = "xhigh",
                MaxTokens = 50_000,
                Temperature = 0.5,
                Columns =
                [
                    new CompareColumnDraft { Provider = "faux", Model = "echo" },
                    new CompareColumnDraft { Provider = "faux", Model = "echo-thinking" },
                    new CompareColumnDraft { Provider = "faux", Model = "echo-thinking", Thinking = "low" },
                ],
            };
            var plan = ComparePlanner.Plan(draft, host.Engine);
            Check(plan.Valid && plan.Columns.All(column => column.Ok), "every column can run: " + plan.Message);
            string Chips(int index) => string.Join("|", plan.Columns[index].Chips.Select(chip => chip.Text));
            Check(plan.Columns[0].Chips.Any(chip => chip is { Text: "thinking n/a", Kind: "not-sent" }), "a model that does not think says so: " + Chips(0));
            Equal(null, plan.Columns[0].Spec!.ThinkingLevel, "and is sent no level");
            Check(plan.Columns[1].Chips.Any(chip => chip.Text == "thinking xhigh → high"), "a shared level the model does not list is clamped, visibly: " + Chips(1));
            Equal("high", plan.Columns[1].Spec!.ThinkingLevel, "to the nearest level it lists");
            Check(plan.Columns[1].Chips.Any(chip => chip.Text == "maxTokens 32,000"), "maxTokens above the model's own cap is capped: " + Chips(1));
            Equal(32_000, plan.Columns[1].Spec!.MaxTokens, "to its cap");
            Check(plan.Columns[1].Chips.Any(chip => chip is { Text: "temperature n/a", Kind: "not-sent" }), "the shared temperature is not sent while it thinks: " + Chips(1));
            Equal(null, plan.Columns[1].Spec!.Temperature, "and is left out");
            Check(plan.Columns[2].Chips.Any(chip => chip is { Text: "thinking low", Tone: "accent", Kind: "override" }), "an override is marked: " + Chips(2));
            Equal("low", plan.Columns[2].Spec!.ThinkingLevel, "and sent as set");
            Check(plan.Specs.All(spec => spec.Source == "compare"), "every column is a Compare run");

            // An explicit override the model does not take: refused before anything is reserved or sent (rule 4).
            var refused = draft with { Thinking = "low", Columns = [draft.Columns[0], draft.Columns[1] with { Temperature = 0.7 }] };
            var bad = ComparePlanner.Plan(refused, host.Engine);
            Check(!bad.Valid && !bad.Columns[1].Ok && bad.Columns[0].Ok, "only the column with the override cannot run");
            Check(bad.Message!.Contains("Column 2", StringComparison.Ordinal) && bad.Message.Contains("nothing was sent", StringComparison.Ordinal), "it names the column: " + bad.Message);
            var error = await ThrowsAsync<CompareRefusedException>(() => host.Pages.RunAsync(refused), "Run all refuses it");
            Equal(1, error.Column?.Index, "the refusal names the column");
            Equal(0L, host.Store.Count(), "nothing recorded");
            Equal(0, ReadDb(host.Database, "SELECT COUNT(*) FROM spend_reservations", reader => reader.GetInt32(0)).Single(), "nothing reserved");

            var one = ComparePlanner.Plan(draft with { Columns = [draft.Columns[0]] }, host.Engine);
            Check(!one.Valid && one.Message!.Contains("at least 2", StringComparison.Ordinal), "a comparison has two columns or more: " + one.Message);
            var empty = ComparePlanner.Plan(draft with { Prompt = " " }, host.Engine);
            Check(!empty.Valid, "and a prompt: " + empty.Message);
            var unknown = ComparePlanner.Plan(draft with { Columns = [draft.Columns[0], new CompareColumnDraft { Provider = "faux", Model = "no-such-model" }] }, host.Engine);
            Check(!unknown.Valid && unknown.Message!.Contains("no-such-model", StringComparison.Ordinal), "an unknown model is named, never swapped: " + unknown.Message);

            // A column's new model that does not list its thinking override keeps the value on the page, not sent.
            var kept = host.Pages.DraftColumn(draft.Shared, new CompareColumnDraft { Provider = "faux", Model = "echo-small", Thinking = "medium" }, "c2", 3);
            Check(kept is { KeptThinking: "medium", Column.Thinking: null }, "kept, not sent: " + kept.KeptThinking);
            var parsed = CompareDraft.Parse(JsonNode.Parse("""{"prompt":"p","thinking":"off","columns":[{"provider":"faux","model":"echo","thinking":"inherit"},{"provider":"faux","model":"echo-small"}]}"""));
            Check(parsed is { Thinking: null } && parsed.Columns[0].Thinking is null, "off is no shared level, inherit no override");
            Throws<ArgumentException>(() => CompareDraft.Parse(JsonNode.Parse("""{"prompt":"p","columns":"x"}""")), "a malformed draft");
        }),

        Case("compare.run.three-columns-record-one-group", async () =>
        {
            await using var host = CompareHost.Faux("cmp-group");
            var draft = new CompareDraft
            {
                SystemPrompt = "Be brief.",
                Prompt = "hello compare",
                Columns =
                [
                    new CompareColumnDraft { Provider = "faux", Model = "echo" },
                    new CompareColumnDraft { Provider = "faux", Model = "echo-small" },
                    new CompareColumnDraft { Provider = "faux", Model = "echo-thinking", Thinking = "low" },
                ],
            };
            var started = await host.Pages.RunAsync(draft);
            Check(started.Start is { Posted: true, GroupId: not null } && started.Start.Ids.Count == 3, "Run all posts the three columns: " + started.Start.Refusal?.Message);
            var group = started.Start.GroupId!;
            var runs = new List<RunRecord>();
            foreach (var id in started.Start.Ids) runs.Add(await RunsFinishedAsync(host.Store, id));
            Check(runs.All(run => run is { Status: RunStatus.Completed, Source: "compare", CostMicros: > 0 } && run.GroupId == group),
                "each column is a completed Compare row of the group: " + string.Join(" | ", runs));
            Equal("faux/echo|faux/echo-small|faux/echo-thinking", string.Join("|", runs.Select(run => run.Provider + "/" + run.Model)), "in column order");
            Check(runs.All(run => run.OutputText?.Contains("hello compare", StringComparison.Ordinal) == true), "each its own answer: " + string.Join(" | ", runs.Select(run => run.OutputText)));
            Equal("low", runs[2].Options?["thinking"]?.GetValue<string>(), "the override was sent");
            Equal(3, host.Store.List(new RunQuery { GroupId = group }).Items.Count, "one group of three rows");
            var ledger = host.Services.GetRequiredService<ISpendLedger>();
            foreach (var run in runs) Equal(group, (await ledger.GetAsync(run.SpendOp!))!.Run, "each column reserved under the group's run");
            Equal(3, ReadDb(host.Database, $"SELECT COUNT(*) FROM spend_reservations WHERE run = '{group}'", reader => reader.GetInt32(0)).Single(), "three reservations, one run");
            Equal(1, ReadDb(host.Database, "SELECT COUNT(*) FROM compare_groups", reader => reader.GetInt32(0)).Single(), "one Compare record");
            var stored = host.Groups.Get(group)!;
            Equal(string.Join(",", started.Start.Ids), string.Join(",", stored.Columns.Select(column => column.RunId)), "the record's columns are the runs");
            Check(stored.Shared is { Prompt: "hello compare", SystemPrompt: "Be brief." } && stored.Columns[2].Chips.Any(chip => chip.Text == "thinking low"),
                "with the shared request and each column's chips");

            var metrics = host.Pages.Metrics(group)!;
            Equal(3, metrics.Ran.Count, "the Metrics tab has the three columns");
            Equal(1, metrics.Columns.Count(column => column.Fastest), "one is fastest");
            Check(metrics.Status.Text.StartsWith("3 of 3 done · fastest ", StringComparison.Ordinal), "the status line: " + metrics.Status.Text);
            Equal("TTFT|Total time|Tokens in|Tokens out|Tokens/s|Cost|stopReason|Thinking", string.Join("|", metrics.Rows.Select(row => row.Label)), "the rows");
            Check(metrics.Rows.Single(row => row.Label == "Thinking").Cells[2] is { Text: "low", Note: "override" }, "the level each run sent");
            var tokensIn = metrics.Rows.Single(row => row.Label == "Tokens in").Cells;
            Check(tokensIn.Select(cell => cell.Text).Distinct().Count() == 1 ? tokensIn.All(cell => !cell.Best) : tokensIn.Any(cell => cell.Best),
                "best per row among the completed columns (a tie between all marks none)");

            // Mark best, re-run one column into the group, remove one, reopen.
            Equal(runs[1].Id, host.Pages.MarkBest(group, runs[1].Id).Best, "marked best");
            Check(host.Pages.Metrics(group)!.Status.Text.Contains("best Echo small", StringComparison.Ordinal), "the status line names it");
            await ThrowsAsync<KeyNotFoundException>(() => Task.FromResult(host.Pages.MarkBest(group, "run-elsewhere")), "only a column of the group");
            var again = await host.Pages.RerunAsync(group, new CompareColumnDraft { Provider = "faux", Model = "echo-small" }, runs[1].Id, 1);
            Check(again.Start.Posted && again.Start.GroupId == group, "a re-run posts into the group: " + again.Start.Refusal?.Message);
            var rerun = await RunsFinishedAsync(host.Store, again.Start.Id!);
            Check(rerun.Status == RunStatus.Completed && rerun.GroupId == group && rerun.OutputText == "Echo: hello compare", "with the group's prompt: " + rerun);
            Equal(group, (await ledger.GetAsync(rerun.SpendOp!))!.Run, "reserved under the group too");
            var after = host.Groups.Get(group)!;
            Equal(rerun.Id, after.Columns[1].RunId, "the new run takes the column's place");
            Equal(null, after.Best, "the replaced run's mark goes with it");
            Equal(4, host.Store.List(new RunQuery { GroupId = group }).Items.Count, "the earlier run stays in History");
            var removed = host.Pages.RemoveColumn(group, runs[0].Id);
            Equal($"{rerun.Id},{runs[2].Id}", string.Join(",", removed.Columns.Select(column => column.RunId)), "Remove takes the column out of the comparison");
            var page = await host.Pages.PageAsync(group, null);
            Check(page is { GroupId: not null, Draft.Prompt: "hello compare", Columns.Count: 2, Running: false }, "?group= reopens the comparison");
            Equal(group, page.GroupId, "the same group");
            var missing = await host.Pages.PageAsync("g-none", null);
            Check(missing.GroupId is null && missing.Notice?.Contains("g-none", StringComparison.Ordinal) == true, "an unknown group opens a new comparison and says so");
            await ThrowsAsync<KeyNotFoundException>(() => host.Pages.RerunAsync("g-none", new CompareColumnDraft { Provider = "faux", Model = "echo" }, null, 0), "re-run needs the group");

            // A group whose runs are all deleted from History is pruned.
            host.Store.Delete(host.Store.List(new RunQuery { GroupId = group }).Items.Select(item => item.Id));
            Equal(null, host.Groups.Get(group), "pruned with its runs");
        }),

        Case("compare.run.the-group-reservation-is-all-or-nothing", async () =>
        {
            await using var host = CompareHost.Faux("cmp-all-or-none");
            var column = new CompareColumnDraft { Provider = "faux", Model = "echo" };
            var draft = new CompareDraft { Prompt = "all or nothing", MaxTokens = 4_000, Columns = [column, column, column] };
            var plan = ComparePlanner.Plan(draft, host.Engine).Columns[0].Plan!;
            var probe = await host.Services.GetRequiredService<GuardedRunExecutor>().Guard.PreflightAsync(new SpendCall
            {
                Provider = "faux",
                Model = "echo",
                Source = SpendSource.Compare,
                Run = "probe",
                InputBytes = CostEstimator.InputBytes(plan.Messages),
                MaxTokens = plan.Spec.MaxTokens,
            }, withApprovals: false);
            var worst = probe.Estimate.WorstMicros ?? throw new InvalidOperationException("the faux model is priced");
            Check(worst > 0, "one column's worst case: " + worst);
            var ledger = host.Services.GetRequiredService<ISpendLedger>();
            await ledger.SaveCapAsync(new SpendCap(0, CapScope.Everything, null, CapPeriod.Run, worst * 5 / 2, CapAction.Block));

            var refused = await host.Pages.RunAsync(draft);
            Check(refused.Start is { Posted: false, Refusal.Code: SpendErrors.Blocked } && refused.Group is null, "three columns over a run cap two fit: refused: " + refused.Start.Refusal?.Code);
            Check(refused.Start.Refusal!.Decision.Causes.Count > 0 && refused.Start.GroupId is not null, "with the guard's decision and the group id, for the confirm modal");
            Equal(0L, host.Store.Count(), "no column ran");
            Equal(0, ReadDb(host.Database, "SELECT COUNT(*) FROM spend_reservations WHERE state = 'reserved' OR counts = 1", reader => reader.GetInt32(0)).Single(),
                "no column holds or counts a reservation");
            Equal(0, ReadDb(host.Database, "SELECT COUNT(*) FROM compare_groups", reader => reader.GetInt32(0)).Single(), "and no Compare record");
            var json = await CompareModule.SpendJsonAsync(refused.Start.Refusal, host.Services);
            Check(json["verdict"]?.GetValue<string>() == "block" && json["canProceed"]?.GetValue<bool>() == false, "the modal's answer: " + json.ToJsonString());

            var two = await host.Pages.RunAsync(draft with { Columns = [column, column] });
            Check(two.Start is { Posted: true } && two.Start.Ids.Count == 2, "two columns fit: " + two.Start.Refusal?.Message);
            foreach (var id in two.Start.Ids) Equal(RunStatus.Completed, (await RunsFinishedAsync(host.Store, id)).Status, "and run");

            // An Ask cap asks once for the group; the user's choice lets every column through.
            var caps = await ledger.CapsAsync();
            foreach (var cap in caps) await ledger.RemoveCapAsync(cap.Id);
            await ledger.SaveCapAsync(new SpendCap(0, CapScope.Everything, null, CapPeriod.Request, worst / 2, CapAction.Ask));
            var asked = await host.Pages.RunAsync(draft);
            Check(asked.Start is { Posted: false, Refusal.Code: SpendErrors.Ask, GroupId: not null }, "an Ask cap asks: " + asked.Start.Refusal?.Code);
            Equal(2L, host.Store.Count(), "nothing more ran");
            var answered = await host.Pages.RunAsync(draft, asked.Start.GroupId, new RunSpendAnswer(SpendChoice.ProceedOnce));
            Check(answered.Start is { Posted: true } && answered.Start.GroupId == asked.Start.GroupId && answered.Start.Ids.Count == 3,
                "proceed once sends the whole group, under the same id: " + answered.Start.Refusal?.Message);
            foreach (var id in answered.Start.Ids) Equal(RunStatus.Completed, (await RunsFinishedAsync(host.Store, id)).Status, "every column ran");
            var refusedAgain = await host.Pages.RunAsync(draft);
            Check(refusedAgain.Start is { Posted: false }, "the approval was for that group once");
        }),

        Case("compare.run.one-failing-column-does-not-stop-the-others", async () =>
        {
            await using var host = CompareHost.Routing("cmp-failing");
            var openaiKey = Planted("sk-proj-");
            var anthropicKey = Planted("sk-ant-");
            var openaiAccount = (await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("openai", openaiKey))).Account.Id;
            var anthropicAccount = (await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("anthropic", anthropicKey))).Account.Id;
            var catalog = host.Services.GetRequiredService<IModelCatalog>();
            var gpt = ModelOf(catalog, "openai", "openai-responses", "gpt-4.1");
            var claude = ModelOf(catalog, "anthropic", "anthropic-messages", "claude-sonnet-4-5");
            host.Provider.Answer = request => request.Url.Host == "api.anthropic.com" ? FakeOAuth.Json(401, new JsonObject
                { ["type"] = "error", ["error"] = new JsonObject { ["type"] = "authentication_error", ["message"] = "invalid x-api-key" } }) : null;
            var draft = new CompareDraft
            {
                Prompt = "which one fails",
                Columns =
                [
                    new CompareColumnDraft { Provider = "openai", Model = gpt },
                    new CompareColumnDraft { Provider = "anthropic", Model = claude },
                    new CompareColumnDraft { Provider = "openai", Model = gpt, Temperature = 0.2 },
                ],
            };
            var started = await host.Pages.RunAsync(draft);
            Check(started.Start is { Posted: true } && started.Start.Ids.Count == 3, "posted: " + started.Start.Refusal?.Message);
            var runs = new List<RunRecord>();
            foreach (var id in started.Start.Ids) runs.Add(await RunsFinishedAsync(host.Store, id));
            Check(runs[0] is { Status: RunStatus.Completed, OutputText: "Hello world" } && runs[2] is { Status: RunStatus.Completed, OutputText: "Hello world" },
                "the OpenAI columns complete: " + runs[0] + " | " + runs[2]);
            Check(runs[1].Status == RunStatus.Error && AccountRouteErrors.IsAccountError(runs[1].Error), "the Anthropic column fails with the account error: " + runs[1].Error);
            NoSecret(runs[1].Error ?? "", "the error", anthropicKey);
            var temperatures = host.Provider.Requests.Where(request => request.Url.Host != "api.anthropic.com")
                .Select(request => JsonNode.Parse(request.Body!)!["temperature"]?.GetValue<double>()).ToList();
            Check(temperatures.Count == 2 && temperatures.Contains(0.2) && temperatures.Contains(null), "only the column with the override sent a temperature");
            Equal(1, host.Provider.Requests.Count(request => request.Url.Host == "api.anthropic.com"), "the failed column was sent once, never retried");

            var metrics = host.Pages.Metrics(started.Start.GroupId!)!;
            Check(metrics.Status is { Tone: "danger" } && metrics.Status.Text.StartsWith("2 done · 1 failed (anthropic", StringComparison.Ordinal), "the status line: " + metrics.Status.Text);
            var failed = metrics.Columns[1];
            Check(failed is { State: "error", AccountError: true, ErrorChip: not null }, "the column's error state: " + failed.ErrorChip);
            await using var render = RenderHost.Create("cmp-failing-render", (_, _) => { });
            var html = await render.Renderer.RenderAsync(ComparePages.ColumnPartial, failed);
            Check(html.Contains("data-col-error", StringComparison.Ordinal) && html.Contains("href=\"/settings/providers\"", StringComparison.Ordinal)
                && html.Contains("data-col-retry", StringComparison.Ordinal), "the error card: Fix and Retry: " + Cut(html));
            Check(html.Contains("not retried (PiSharp never retries a run by itself)", StringComparison.Ordinal), "never retried by itself");
            NoSecret(html, "the column", anthropicKey);

            // The three columns ran at once over real routing: each request carries its own provider's key and never the other one (a
            // credential crossed between concurrent columns fails here), and neither key is stored or logged.
            Equal($"{openaiAccount}|{anthropicAccount}|{openaiAccount}", string.Join("|", runs.Select(run => run.Account)), "each column ran on its provider's account");
            var requests = host.Provider.Requests.ToList();
            Equal(3, requests.Count, "one request per column: " + string.Join(", ", requests.Select(request => request.Key)));
            foreach (var request in requests)
            {
                var anthropic = request.Url.Host == "api.anthropic.com";
                var (own, other) = anthropic ? (anthropicKey, openaiKey) : (openaiKey, anthropicKey);
                var sent = string.Join("\n", request.Headers.Select(pair => pair.Key + ": " + pair.Value)) + "\n" + request.Body;
                var where = $"the {(anthropic ? "Anthropic" : "OpenAI")} request to {request.Url.Host}";
                Check(sent.Contains(own, StringComparison.Ordinal), where + " carries its own key");
                NoSecret(request.Url + "\n" + sent, where, other);
            }
            NoSecret(RunsFileText(host.Runs.Path), "runs.db", openaiKey, anthropicKey);
            NoSecret(host.LogText, "the log", openaiKey, anthropicKey);
            foreach (var key in new[] { openaiKey, anthropicKey })
                Check(FilesHolding(host.Options.DataDirectory, key).Count == 0, "a planted key is on disk in plain text: " + string.Join(", ", FilesHolding(host.Options.DataDirectory, key)));
        }),

        Case("compare.columns.render-each-state", async () =>
        {
            await using var host = CompareHost.Faux("cmp-render-runs");
            await using var render = RenderHost.Create("cmp-render", (_, _) => { });
            Task<string> Column(CompareColumnView view) => render.Renderer.RenderAsync(ComparePages.ColumnPartial, view);
            var shared = new CompareDraft { Thinking = "high", Temperature = 0.4 };

            var empty = await Column(host.Pages.DraftColumn(shared, new CompareColumnDraft { Provider = "faux", Model = "echo-thinking", Thinking = "minimal" }, "c3", 3));
            Check(empty.TrimStart().StartsWith("<section class=\"compare-col\"", StringComparison.Ordinal) && empty.Contains("data-key=\"c3\"", StringComparison.Ordinal),
                "one root with its key: " + Cut(empty));
            Check(empty.Contains("data-state=\"empty\"", StringComparison.Ordinal) && empty.Contains("data-col-empty", StringComparison.Ordinal), "empty: not run yet");
            Check(empty.Contains("id=\"cmp-c3-thinking\"", StringComparison.Ordinal) && Regex.IsMatch(empty, "<option value=\"minimal\" selected"), "the thinking override, set");
            Check(empty.Contains("inherit (high)", StringComparison.Ordinal), "inherit names the shared level");
            Check(Regex.IsMatch(empty, "id=\"cmp-c3-temperature\"[^>]*placeholder=\"inherit [(]0.4[)]\"") && empty.Contains("Temperature: not while this model thinks.", StringComparison.Ordinal),
                "a temperature the model takes only with thinking off: offered, with the reason (Run all refuses it while the column thinks)");
            var none = await Column(host.Pages.DraftColumn(shared, new CompareColumnDraft { Provider = "faux", Model = "echo-thinking", Temperature = 0.3 }, "c4", 3));
            Check(Regex.IsMatch(none, "id=\"cmp-c4-temperature\"[^>]*value=\"0.3\""), "an override keeps its value");
            Check(Regex.IsMatch(empty, "data-col-rerun disabled") || Regex.IsMatch(empty, "disabled=\"disabled\" data-col-rerun") || Regex.IsMatch(empty, "data-col-rerun[^>]*disabled"),
                "no Re-run before the group ran");
            var unknown = await Column(host.Pages.DraftColumn(shared, new CompareColumnDraft { Provider = "faux", Model = "gone" }, "c2", 2));
            Check(unknown.Contains("not available", StringComparison.Ordinal) && unknown.Contains("isn&#x27;t available", StringComparison.Ordinal) || unknown.Contains("isn't available", StringComparison.Ordinal),
                "a model PiSharp does not list says so, never swapped: " + Cut(unknown));

            var slow = await host.Pages.RunAsync(new CompareDraft
            {
                Prompt = "slow: " + string.Join(" ", Enumerable.Range(1, 300)),
                Columns = [new CompareColumnDraft { Provider = "faux", Model = "echo" }, new CompareColumnDraft { Provider = "faux", Model = "echo-small" }],
            });
            var group = slow.Start.GroupId!;
            await WaitUntilAsync(() => Task.FromResult(host.Services.GetRequiredService<RunLive>().Find(slow.Start.Ids[0])?.Items.Count > 3), "the slow columns stream");
            var live = host.Pages.GroupColumn(group, slow.Start.Ids[0], "c1")!;
            var streaming = await Column(live);
            Check(streaming.Contains("data-state=\"streaming\"", StringComparison.Ordinal) && streaming.Contains("data-stream-text", StringComparison.Ordinal)
                && streaming.Contains("stream-cursor", StringComparison.Ordinal), "streaming: the text so far with the cursor");
            Check(Regex.IsMatch(streaming, "data-last-seq=\"[1-9]") && Regex.IsMatch(streaming, "data-started=\"[1-9]"), "the last event seen and the start, to follow it");
            Check(streaming.Contains("data-col-stop", StringComparison.Ordinal) && !streaming.Contains("data-col-rerun", StringComparison.Ordinal), "Stop this column while it streams");
            Check(streaming.Contains("data-foot=\"ttft\"", StringComparison.Ordinal) && streaming.Contains("data-foot=\"elapsed\"", StringComparison.Ordinal) && !streaming.Contains("data-foot=\"tokens\"", StringComparison.Ordinal),
                "the foot while it streams: TTFT and the time so far, no token counts made up");
            var page = await host.Pages.PageAsync(group, null);
            Check(page.Running && page.Metrics.Status.Tone == "live", "the page is running: " + page.Metrics.Status.Text);
            Equal(2, host.Pages.Stop(group).Count, "Stop all stops both columns");
            var stoppedRun = await RunsFinishedAsync(host.Store, slow.Start.Ids[0]);
            await RunsFinishedAsync(host.Store, slow.Start.Ids[1]);
            Equal(RunStatus.Aborted, stoppedRun.Status, "aborted");
            var stopped = await Column(host.Pages.GroupColumn(group, stoppedRun.Id, "c1")!);
            Check(stopped.Contains("data-state=\"stopped\"", StringComparison.Ordinal) && stopped.Contains("Stopped before the answer finished", StringComparison.Ordinal), "stopped");
            Check(Regex.IsMatch(stopped, "data-col-rerun(?![^>]*disabled)"), "Re-run once the group ran");

            var done = await host.Pages.RunAsync(new CompareDraft
            {
                Prompt = "done",
                Columns = [new CompareColumnDraft { Provider = "faux", Model = "echo" }, new CompareColumnDraft { Provider = "faux", Model = "echo-small" }],
            });
            foreach (var id in done.Start.Ids) await RunsFinishedAsync(host.Store, id);
            host.Pages.MarkBest(done.Start.GroupId!, done.Start.Ids[0]);
            var bestView = host.Pages.GroupColumn(done.Start.GroupId!, done.Start.Ids[0], "c1")!;
            var completed = await Column(bestView);
            Check(completed.Contains("compare-col is-best", StringComparison.Ordinal) && completed.Contains("data-chip=\"best\"", StringComparison.Ordinal)
                && completed.Contains("aria-pressed=\"true\"", StringComparison.Ordinal), "best: marked");
            Check(completed.Contains("Echo: done", StringComparison.Ordinal) && completed.Contains("data-foot=\"total\"", StringComparison.Ordinal)
                && completed.Contains("data-foot=\"cost\"", StringComparison.Ordinal), "completed: the answer and the run's own figures");
            Check(completed.Contains($"href=\"/runs/{done.Start.Ids[0]}\"", StringComparison.Ordinal), "the inspector link names the run");

            // Model text goes through the sanitizing renderer.
            var marked = bestView with { Output = bestView.Output with { Run = bestView.Run! with { Output = JsonNode.Parse("""{"role":"assistant","content":[{"type":"text","text":"**bold** <script>alert(1)</script>"}]}""") } } };
            var sanitized = await Column(marked);
            Check(sanitized.Contains("<strong>bold</strong>", StringComparison.Ordinal) && !sanitized.Contains("<script>alert", StringComparison.Ordinal), "Markdown, sanitized: " + Cut(sanitized));
            var failedView = bestView with { Output = bestView.Output with { Run = bestView.Run! with { Status = RunStatus.Error, Error = "The provider said <no>.", Output = null, OutputText = null } } };
            var failed = await Column(failedView);
            Check(failed.Contains("data-state=\"error\"", StringComparison.Ordinal) && failed.Contains("The provider said &lt;no&gt;.", StringComparison.Ordinal), "error: the message, encoded");

            var metricsView = host.Pages.Metrics(done.Start.GroupId!)!;
            var metrics = await render.Renderer.RenderAsync(ComparePages.MetricsPartial, metricsView);
            Check(metrics.Contains("id=\"cmpMetricsTable\"", StringComparison.Ordinal) && metrics.Contains("data-copy-table=\"#cmpMetricsTable\"", StringComparison.Ordinal), "the table and Copy table");
            Check(metrics.Contains("Marked best by you: ", StringComparison.Ordinal), "the mark");
            Equal(metricsView.Rows.Any(row => row.Cells.Any(cell => cell.Best)), metrics.Contains("best-cell", StringComparison.Ordinal), "best per row in green");
            var quick = bestView with { Output = bestView.Output with { Run = bestView.Run! with { TtftMs = 100, DurationMs = 900 } } };
            var steady = bestView with { Key = "c2", Best = false, Output = bestView.Output with { Run = bestView.Run! with { Id = "run-other", TtftMs = 300, DurationMs = 400 } } };
            var picked = new CompareMetricsView { GroupId = done.Start.GroupId, Columns = [quick, steady] };
            Check(picked.Rows.Single(row => row.Label == "TTFT").Cells is [{ Best: true }, { Best: false }], "the lower TTFT is best");
            Check(picked.Rows.Single(row => row.Label == "Total time").Cells is [{ Best: false }, { Best: true }], "the lower total time is best");
            Check(picked.Rows.Single(row => row.Label == "Tokens in").Cells.All(cell => !cell.Best), "a tie marks none");
            var green = await render.Renderer.RenderAsync(ComparePages.MetricsPartial, picked);
            Equal(2, Regex.Matches(green, "class=\"best-cell\"").Count + Regex.Matches(green, "class=\"best-cell ").Count, "two cells in green: " + Cut(green));
            Check(Regex.IsMatch(metrics, "data-status-line=\"2 of 2 done"), "the status line for the page: " + metricsView.Status.Text);
            var noMetrics = await render.Renderer.RenderAsync(ComparePages.MetricsPartial, new CompareMetricsView());
            Check(noMetrics.Contains("No metrics", StringComparison.Ordinal) && !noMetrics.Contains("<table", StringComparison.Ordinal), "no metrics before a run: no figures made up");
            PlaygroundNoInline("_CompareColumn", empty + streaming + completed + sanitized + failed + metrics);
        }),

        Case("compare.page.renders-request-mode-and-the-agent-stub", async () =>
        {
            await using var app = await WebAgentApp.StartAsync("cmp-page");
            var page = await app.Get("/compare");
            Equal(200, page.Status, "GET /compare");
            var html = page.Text;
            PlaygroundNoInline("/compare", html);
            Check(!html.Contains("Coming in a later milestone", StringComparison.Ordinal), "not the placeholder");
            Check(Regex.IsMatch(html, "<a class=\"nav-link active\" aria-current=\"page\" href=\"/compare\" data-nav=\"compare\">"), "Compare is the active item");
            Check(html.Contains("<h1 class=\"app-title\">Compare <small", StringComparison.Ordinal), "the top bar title");
            Check(html.Contains("data-compare data-group=\"\"", StringComparison.Ordinal) || Regex.IsMatch(html, "data-compare(?: data-group=\"\")? data-max-columns=\"4\" data-min-columns=\"2\""),
                "a new comparison: " + Cut(Regex.Match(html, "<div class=\"cmp-page\"[^>]*>").Value));
            Equal(2, Regex.Matches(html, "<section class=\"compare-col[^\"]*\"[^>]*data-compare-col").Count, "two columns of the user's own models");
            Check(Regex.IsMatch(html, "data-compare-col data-key=\"c1\" data-provider=\"faux\""), "faux models in --faux mode");
            Check(html.Contains("data-compare-prompt", StringComparison.Ordinal) && html.Contains("id=\"cmpPrompt\"", StringComparison.Ordinal), "the shared prompt");
            foreach (var shared in new[] { "thinking", "maxTokens", "temperature" })
                Check(html.Contains($"data-shared=\"{shared}\"", StringComparison.Ordinal), "the shared option " + shared);
            Check(html.Contains("data-run-all", StringComparison.Ordinal) && html.Contains("data-estimate-host", StringComparison.Ordinal) && html.Contains("data-stop-all", StringComparison.Ordinal),
                "Run all, its estimate and Stop all");
            Check(Regex.IsMatch(html, "data-overcap hidden"), "the over-cap banner, hidden");
            Check(html.Contains("id=\"cmp-tab-responses\"", StringComparison.Ordinal) && html.Contains("id=\"cmp-tab-metrics\"", StringComparison.Ordinal), "the Responses and Metrics tabs");
            Check(html.Contains("No metrics <em>yet</em>", StringComparison.Ordinal), "no figures before a run");
            Check(html.Contains("id=\"cmpModeRequest\"", StringComparison.Ordinal) && html.Contains("id=\"cmpModeAgent\"", StringComparison.Ordinal), "the Request · Agent switch");
            // W5 E5: the Blind switch (blind rounds start at POST /api/evals/arena; ArenaCases covers them).
            Check(Regex.IsMatch(html, ">\\s*Blind\\s*<") && html.Contains("data-arena-blind=\"request\"", StringComparison.Ordinal), "the Blind switch (W5)");
            Check(Regex.IsMatch(html, "data-preset-load>") && Regex.IsMatch(html, "data-preset-save>") && !html.Contains("Presets are not available yet", StringComparison.Ordinal),
                "presets: enabled, for presets-host.js to wire");
            Check(html.Contains("data-compare-add", StringComparison.Ordinal) && html.Contains("2 slots left", StringComparison.Ordinal), "Add model, with the slots left");
            foreach (var modal in new[] { "modal-model-picker", "modal-spend-confirm", "modal-shortcuts" })
                Check(html.Contains($"id=\"{modal}\"", StringComparison.Ordinal), "the page hosts #" + modal);
            Check(html.Contains("data-provider-model=\"mp\"", StringComparison.Ordinal), "Provider → Model in the picker");
            Check(Regex.IsMatch(html, "<script type=\"module\" src=\"/js/compare\\.js\\?v="), "its module");
            Check(Regex.IsMatch(html, "<link href=\"/css/compare\\.css\\?v="), "its stylesheet");
            foreach (var script in CompareScripts)
            {
                var asset = await app.Get("/js/" + script);
                Equal(200, asset.Status, script);
                Check(!Regex.IsMatch(asset.Text, "\\.innerHTML\\s*=\\s*[^'\";\\n]*\\b(delta|text|message|answer)\\b"), script + " never writes model text as HTML");
            }
            Equal(200, (await app.Get("/css/compare.css")).Status, "compare.css");

            var agent = await app.Get("/compare?mode=agent");
            Equal(200, agent.Status, "?mode=agent");
            PlaygroundNoInline("/compare?mode=agent", agent.Text);
            Check(agent.Text.Contains("data-agent-mode", StringComparison.Ordinal) && Regex.IsMatch(agent.Text, "id=\"cmpModeAgent\"[^>]*checked"), "agent mode, switched");
            Check(!agent.Text.Contains("data-compare-grid", StringComparison.Ordinal), "no request columns in agent mode");
            var reopened = await app.Get("/compare?group=g-missing");
            Check(reopened.Status == 200 && reopened.Text.Contains("There is no Compare run g-missing", StringComparison.Ordinal), "an unknown group says so");
            Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
        }),

        Case("compare.run.http-round-trip", async () =>
        {
            await using var app = await WebAgentApp.StartAsync("cmp-http");
            JsonObject Draft(string prompt) => new()
            {
                ["systemPrompt"] = "Be brief.",
                ["prompt"] = prompt,
                ["thinking"] = "xhigh",
                ["columns"] = new JsonArray(
                    new JsonObject { ["provider"] = "faux", ["model"] = "echo" },
                    new JsonObject { ["provider"] = "faux", ["model"] = "echo-small" },
                    new JsonObject { ["provider"] = "faux", ["model"] = "echo-thinking" }),
            };
            var plan = await app.SendAsync(HttpMethod.Post, "/api/compare/plan", Draft("hello http"), origin: app.Origin);
            Equal(200, plan.Status, "the plan: " + plan.Text);
            Check(plan.Body?["valid"]?.GetValue<bool>() == true && plan.Body["columns"]?.AsArray().Count == 3, "three columns that can run: " + plan.Text);
            Check(plan.Body!["columns"]![2]!["chips"]!.AsArray().Any(chip => chip?["text"]?.GetValue<string>() == "thinking xhigh → high"), "the chips the page shows");
            Check(plan.Body["estimate"]?["html"] is not null, "the estimate by Run all");
            Check(plan.Response.Headers.CacheControl?.NoStore == true, "never cached");

            var posted = await app.SendAsync(HttpMethod.Post, "/api/compare/run", Draft("hello http"), origin: app.Origin);
            Check(posted.Status == 200 && posted.Body?["posted"]?.GetValue<bool>() == true, "Run all posts: " + posted.Text);
            var group = posted.Body!["groupId"]!.GetValue<string>();
            var ids = posted.Body["ids"]!.AsArray().Select(id => id!.GetValue<string>()).ToList();
            Equal(3, ids.Count, "three runs");
            Equal($"/api/runs/{ids[1]}/events", posted.Body["columns"]![1]!["events"]!.GetValue<string>(), "each column's events");
            foreach (var id in ids)
            {
                var frames = await RunsSseAsync(app, $"/api/runs/{id}/events");
                Equal(RunStatus.Completed, frames[^1].Data?["status"]?.GetValue<string>(), "each column completes");
            }
            var fragment = await app.Get($"/compare/groups/{group}/runs/{ids[1]}?key=c2");
            Equal(200, fragment.Status, "the column fragment");
            Check(fragment.Text.TrimStart().StartsWith("<section class=\"compare-col", StringComparison.Ordinal) && fragment.Text.Contains("data-key=\"c2\"", StringComparison.Ordinal),
                "one root with the page's key, for the page to swap in");
            Check(fragment.Text.Contains("data-state=\"completed\"", StringComparison.Ordinal) && fragment.Text.Contains("Echo: hello http", StringComparison.Ordinal), "the answer");
            Check(fragment.Response.Headers.CacheControl?.NoStore == true, "never cached");
            var metrics = await app.Get($"/compare/groups/{group}/metrics");
            Check(metrics.Status == 200 && metrics.Text.Contains("id=\"cmpMetricsTable\"", StringComparison.Ordinal) && metrics.Text.Contains("data-status-line=\"3 of 3 done", StringComparison.Ordinal),
                "the Metrics fragment: " + Cut(metrics.Text));
            Equal(404, (await app.Get("/compare/groups/g-none/metrics")).Status, "an unknown group");
            var best = await app.SendAsync(HttpMethod.Post, $"/api/compare/groups/{group}/best", new JsonObject { ["runId"] = ids[2] }, origin: app.Origin);
            Equal(ids[2], best.Body?["best"]?.GetValue<string>(), "Mark best");
            var reopened = (await app.Get("/compare?group=" + group)).Text;
            Check(reopened.Contains(">hello http</textarea>", StringComparison.Ordinal) && reopened.Contains(">Be brief.</textarea>", StringComparison.Ordinal), "?group= reopens the request");
            Equal(3, Regex.Matches(reopened, "data-compare-col data-key=").Count, "and its columns");
            Check(reopened.Contains($"data-group=\"{group}\"", StringComparison.Ordinal) && reopened.Contains("compare-col is-best", StringComparison.Ordinal), "and the mark");
            Check(reopened.Contains("1 slot left", StringComparison.Ordinal), "one slot left");

            var rerun = await app.SendAsync(HttpMethod.Post, $"/api/compare/groups/{group}/rerun",
                new JsonObject { ["column"] = new JsonObject { ["provider"] = "faux", ["model"] = "echo" }, ["replaces"] = ids[0], ["position"] = 0 }, origin: app.Origin);
            Check(rerun.Body?["posted"]?.GetValue<bool>() == true && rerun.Body["groupId"]?.GetValue<string>() == group, "Re-run posts into the group: " + rerun.Text);
            var rerunId = rerun.Body!["id"]!.GetValue<string>();
            Equal(RunStatus.Completed, (await RunsSseAsync(app, $"/api/runs/{rerunId}/events"))[^1].Data?["status"]?.GetValue<string>(), "and completes");
            var stored = await app.Get($"/api/compare/groups/{group}");
            Equal($"{rerunId},{ids[1]},{ids[2]}", string.Join(",", stored.Body!["columns"]!.AsArray().Select(column => column!["runId"]!.GetValue<string>())), "in the column's place");
            var draftColumn = await app.Get($"/compare/column?provider=faux&model=echo-small&key=c4&thinking=medium&count=4&group={group}");
            Check(draftColumn.Status == 200 && draftColumn.Text.Contains("data-kept-thinking=\"medium\"", StringComparison.Ordinal) && draftColumn.Text.Contains("data-key=\"c4\"", StringComparison.Ordinal),
                "a chosen model's column keeps an override it does not take: " + Cut(draftColumn.Text));
            Check(Regex.IsMatch(draftColumn.Text, "data-col-rerun(?![^>]*disabled)"), "and can be sent into the group");
            Equal(400, (await app.Get("/compare/column?provider=faux")).Status, "a column names its model");
            var removed = await app.SendAsync(HttpMethod.Post, $"/api/compare/groups/{group}/remove", new JsonObject { ["runId"] = ids[1] }, origin: app.Origin);
            Equal(2, removed.Body?["columns"]?.AsArray().Count, "Remove");

            var refused = await app.SendAsync(HttpMethod.Post, "/api/compare/run", new JsonObject
            {
                ["prompt"] = "x",
                ["thinking"] = "low",
                ["columns"] = new JsonArray(new JsonObject { ["provider"] = "faux", ["model"] = "echo" }, new JsonObject { ["provider"] = "faux", ["model"] = "echo-thinking", ["temperature"] = 0.9 }),
            }, origin: app.Origin);
            Check(refused.Status == 400 && refused.Body?["error"]?["column"]?.GetValue<int>() == 2, "an override the model does not take: 400 naming the column: " + refused.Text);
            Equal(400, (await app.SendAsync(HttpMethod.Post, "/api/compare/run", new JsonObject { ["prompt"] = "x", ["columns"] = new JsonArray() }, origin: app.Origin)).Status, "no columns");
            Equal(403, (await app.SendAsync(HttpMethod.Post, "/api/compare/run", Draft("x"), origin: "http://evil.example")).Status, "same origin only");

            // A slow group streams; Stop all cancels its columns.
            var slow = await app.SendAsync(HttpMethod.Post, "/api/compare/run", Draft("slow: " + string.Join(" ", Enumerable.Range(1, 400))), origin: app.Origin);
            var slowGroup = slow.Body!["groupId"]!.GetValue<string>();
            var slowIds = slow.Body["ids"]!.AsArray().Select(id => id!.GetValue<string>()).ToList();
            await RunsSseAsync(app, $"/api/runs/{slowIds[0]}/events", take: 3);
            var live = (await app.Get("/compare?group=" + slowGroup)).Text;
            Check(Regex.IsMatch(live, "data-state=\"streaming\"") && Regex.IsMatch(live, "data-stop-all(?! hidden)") && Regex.IsMatch(live, "data-run-all disabled"),
                "a reopened running group streams, with Stop all");
            var stop = await app.SendAsync(HttpMethod.Post, $"/api/compare/groups/{slowGroup}/stop", new JsonObject(), origin: app.Origin);
            Check(stop.Status == 200 && stop.Body?["stopped"]?.AsArray().Count >= 1, "Stop all: " + stop.Text);
            foreach (var id in slowIds)
            {
                var status = (await RunsSseAsync(app, $"/api/runs/{id}/events"))[^1].Data?["status"]?.GetValue<string>();
                Check(status is RunStatus.Aborted or RunStatus.Completed, "each column ends: " + status);
            }
            Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
        }),

        Case("compare.run.http-overcap-asks-for-the-group", async () =>
        {
            var root = Temp("cmp-overcap");
            try
            {
                await using var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = Path.Join(root, "data"), Arguments = ["--faux-metered"] });
                var cap = await app.SendAsync(HttpMethod.Post, "/api/spend/caps",
                    new JsonObject { ["scope"] = "everything", ["period"] = "request", ["limit"] = "0.01", ["action"] = "ask" }, origin: app.Origin);
                Check(cap.Status == 200, "a per-request Ask cap: " + cap.Text);
                var draft = new JsonObject
                {
                    ["prompt"] = "over the cap",
                    ["maxTokens"] = 4096,
                    ["columns"] = new JsonArray(new JsonObject { ["provider"] = "faux", ["model"] = "echo" }, new JsonObject { ["provider"] = "faux", ["model"] = "echo-small" }),
                };
                var page = (await app.Get("/compare")).Text;
                Check(page.Contains("data-estimate-host", StringComparison.Ordinal), "the estimate host");
                var plan = await app.SendAsync(HttpMethod.Post, "/api/compare/plan", draft.DeepClone(), origin: app.Origin);
                Check(plan.Body?["estimate"]?["over"]?.GetValue<string>() is { Length: > 0 } && plan.Body["estimate"]!["verdict"]?.GetValue<string>() == "ask",
                    "the estimate by Run all says the group would be asked about: " + plan.Text);
                var asked = await app.SendAsync(HttpMethod.Post, "/api/compare/run", draft.DeepClone(), origin: app.Origin);
                Check(asked.Status == 200 && asked.Body?["posted"]?.GetValue<bool>() == false && asked.Body["code"]?.GetValue<string>() == SpendErrors.Ask,
                    "the group is asked about, nothing sent: " + asked.Text);
                Check(asked.Body!["spend"]?["html"]?.GetValue<string>().Contains("modal-spend-confirm", StringComparison.Ordinal) == true, "with the confirm modal's content");
                Check(asked.Body["provider"]?.GetValue<string>() == "faux" && asked.Body["model"]?.GetValue<string>() is "echo" or "echo-small",
                    "and the refused column's model, for Choose a model: " + asked.Body["model"]);
                var group = asked.Body["groupId"]!.GetValue<string>();
                Equal(404, (await app.Get($"/api/compare/groups/{group}")).Status, "no group ran");
                var answered = draft.DeepClone().AsObject();
                answered["groupId"] = group;
                answered["spend"] = new JsonObject { ["choice"] = "proceed-once" };
                var sent = await app.SendAsync(HttpMethod.Post, "/api/compare/run", answered, origin: app.Origin);
                Check(sent.Body?["posted"]?.GetValue<bool>() == true && sent.Body["groupId"]?.GetValue<string>() == group, "the user's choice sends the whole group: " + sent.Text);
                foreach (var id in sent.Body!["ids"]!.AsArray().Select(id => id!.GetValue<string>()))
                    Equal(RunStatus.Completed, (await RunsSseAsync(app, $"/api/runs/{id}/events"))[^1].Data?["status"]?.GetValue<string>(), "each column ran");
                Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        // W4 fix (compare): the estimate by Run all checks the columns as one group (the Spend Guard's group preflight), so a run, daily or
        // monthly cap that only the group's total crosses shows before Run; it used to check each column alone and say nothing until Run
        // all's all-or-nothing reservation refused the group.
        Case("compare.estimate.a-cap-only-the-group-crosses-shows-before-run", async () =>
        {
            await using var host = CompareHost.Faux("cmp-group-preflight");
            var column = new CompareColumnDraft { Provider = "faux", Model = "echo" };
            var draft = new CompareDraft { Prompt = "group preflight", MaxTokens = 4_000, Columns = [column, column, column] };
            var plan = ComparePlanner.Plan(draft, host.Engine).Columns[0].Plan!;
            var executor = host.Services.GetRequiredService<GuardedRunExecutor>();
            var guard = executor.Guard;
            var ledger = host.Services.GetRequiredService<ISpendLedger>();
            SpendCall Call(string run, string op = "") => new()
            {
                Provider = "faux",
                Model = "echo",
                Source = SpendSource.Compare,
                Run = run,
                Op = op,
                InputBytes = CostEstimator.InputBytes(plan.Messages),
                MaxTokens = plan.Spec.MaxTokens,
            };
            static string Verdicts(IEnumerable<SpendDecision> decisions) => string.Join("|", decisions.Select(decision => decision.Verdict.ToString().ToLowerInvariant()));
            int Reservations() => ReadDb(host.Database, "SELECT COUNT(*) FROM spend_reservations", reader => reader.GetInt32(0)).Single();
            async Task ClearCapsAsync()
            {
                foreach (var cap in await ledger.CapsAsync()) await ledger.RemoveCapAsync(cap.Id);
            }
            var alone = await guard.PreflightAsync(Call("probe"), withApprovals: false);
            var worst = alone.Estimate.WorstMicros ?? throw new InvalidOperationException("the faux model is priced");
            Check(worst > 0, "one column's worst case: " + worst);

            // A run cap two columns fit and three do not: each column alone fits it; only the group's total crosses it.
            await ledger.SaveCapAsync(new SpendCap(0, CapScope.Everything, null, CapPeriod.Run, worst * 5 / 2, CapAction.Block));
            Equal(SpendVerdict.Allow, (await guard.PreflightAsync(Call("g-alone"), withApprovals: false)).Verdict, "one column alone fits the run cap");
            SpendCall[] calls = [Call("g-1", "c1"), Call("g-1", "c2"), Call("g-1", "c3")];
            var group = await guard.PreflightGroupAsync(calls, withApprovals: false);
            Equal("allow|allow|block", Verdicts(group.Items), "the third column crosses the run cap with the two before it counted");
            Check(!group.Allowed && group.Refused?.Verdict == SpendVerdict.Block, "the group would be refused");
            var cause = group.Items[2].Causes.Single();
            Check(cause is { Kind: SpendCauseKind.Cap, Cap.Period: CapPeriod.Run } && cause.GroupMicros == worst * 2 && cause.SpentMicros == worst * 2,
                $"the cause says what the columns before it add: {cause.GroupMicros} of {cause.SpentMicros}");
            Equal(null, group.Items[0].Causes.FirstOrDefault()?.GroupMicros, "a column that fits has no cause");
            Equal(0, Reservations(), "a group preflight reserves nothing");
            var requests = calls.Select(call => new SpendRequest { Op = call.Op, Source = call.Source, Run = call.Run, Estimate = alone.Estimate }).ToList();
            Equal("allow|allow|block", Verdicts((await ledger.CheckGroupAsync(requests)).Items), "the ledger's group check is the reservation's rule");
            Equal(0, Reservations(), "nor does the ledger's");
            await ThrowsAsync<ArgumentException>(() => guard.PreflightGroupAsync([Call("g-1", "c1"), Call("g-1", "c1")]), "two calls of a group never share an op");
            await ThrowsAsync<ArgumentException>(() => guard.PreflightGroupAsync([Call("g-1"), Call("g-2")]), "a group is one run");
            await ThrowsAsync<ArgumentException>(() => guard.PreflightGroupAsync([]), "a group has a call");

            // The estimate by Run all names the cap before Run, worded as the group's rather than the third column's.
            var view = CompareEstimate.Build([.. group.Items.Select(decision => new CompareColumnEstimate("Echo", "Faux", decision))]);
            Check(view is { Verdict: "block", Over: "Over run cap", Pane: "sc-chat", Chip: null }, $"the over-cap chip: {view.Verdict} {view.Over} {view.Pane}");
            Check(view.Label.StartsWith("The columns together are over the ", StringComparison.Ordinal), "worded as the group's: " + view.Label);
            Check(view.Tooltip.Contains(WebUtility.HtmlEncode($"Together they need up to {ChatSpendText.Money(worst * 3)}"), StringComparison.Ordinal) &&
                view.Tooltip.Contains("can&#39;t send the group as it is", StringComparison.Ordinal), "what they need and what Run all does: " + view.Tooltip);
            var planned = await host.Pages.EstimateAsync(draft);
            Check(planned is { Verdict: "block", Over: "Over run cap", Pane: "sc-chat" } && planned.Label.StartsWith("The columns together", StringComparison.Ordinal),
                $"the page's estimate says so before Run: {planned.Verdict} {planned.Over} {planned.Label}");
            Equal(0, Reservations(), "and reserves nothing");
            var refused = await host.Pages.RunAsync(draft);
            Check(refused.Start is { Posted: false, Refusal.Code: SpendErrors.Blocked }, "Run all's reservation agrees: " + refused.Start.Refusal?.Code);
            var two = await host.Pages.EstimateAsync(draft with { Columns = [column, column] });
            Check(two is { Verdict: "allow", Over: null, Chip: not null }, $"two columns fit: {two.Verdict} {two.Chip}");

            // A daily Ask cap only the group crosses: the chip asks first. A waiting approval of the run answers the group's preflight (unless
            // left out), and the preflight never uses it up: the group's reservation still finds it.
            await ClearCapsAsync();
            await ledger.SaveCapAsync(new SpendCap(0, CapScope.Everything, null, CapPeriod.Day, worst * 5 / 2, CapAction.Ask));
            var daily = await host.Pages.EstimateAsync(draft);
            Check(daily is { Verdict: "ask", Over: "Over daily cap", Pane: "sc-period" } && daily.Tooltip.Contains("already used today", StringComparison.Ordinal) &&
                daily.Tooltip.Contains("asks you first", StringComparison.Ordinal), $"a daily cap the group crosses asks before Run: {daily.Verdict} {daily.Over} {daily.Tooltip}");
            Equal("allow|allow|ask", Verdicts((await guard.PreflightGroupAsync(calls)).Items), "no approval waits yet");
            await guard.ApproveAsync(new SpendApproval("g-1", SpendChoice.ProceedOnce));
            Check((await guard.PreflightGroupAsync(calls)).Allowed, "the run's waiting approval answers the group once");
            Check(!(await guard.PreflightGroupAsync(calls, withApprovals: false)).Allowed, "not when approvals are left out (the estimate's check)");
            await using (var leases = await executor.ReserveGroupAsync(calls))
                Check(leases.Reserved, "the approval was still there for the group's reservation");
            Equal("released|released|released", string.Join("|", ReadDb(host.Database, "SELECT state FROM spend_reservations WHERE run = 'g-1' ORDER BY op",
                reader => reader.GetString(0))), "the leases never sent were released");

            // A cap near its limit: one amber chip, at the group's total, not one per column that passes the threshold.
            await ClearCapsAsync();
            await ledger.SaveCapAsync(new SpendCap(0, CapScope.Everything, null, CapPeriod.Day, worst * 11, CapAction.Block));
            for (var index = 0; index < 7; index++)
                Check((await ledger.ReserveAsync(new SpendRequest { Op = $"earlier-{index}", Source = SpendSource.Compare, Run = "earlier", Estimate = alone.Estimate })).Reserved,
                    "spent earlier today");
            var near = await guard.PreflightGroupAsync([Call("g-3"), Call("g-3"), Call("g-3")], withApprovals: false);
            Check(near.Allowed && near.Items[0].Warnings.Count == 0 && near.Items[1].Warnings.Count == 1 && near.Items[2].Warnings.Count == 1,
                "the second and third columns pass 80% of the daily cap: " + string.Join("|", near.Items.Select(item => item.Warnings.Count)));
            var amber = CompareEstimate.Build([.. near.Items.Select(decision => new CompareColumnEstimate("Echo", "Faux", decision))]);
            Equal(1, amber.Warnings.Count, "one chip for the cap: " + string.Join(" | ", amber.Warnings.Select(warning => warning.Text)));
            Check(amber.Warnings[0].Text.StartsWith($"{near.Items[2].Warnings[0].PercentShown}% of ", StringComparison.Ordinal) && amber is { Verdict: "allow", Over: null },
                "at the whole group's share: " + amber.Warnings[0].Text);
        }),

        Case("compare.estimate.http-a-run-cap-only-the-group-crosses-asks-before-run", async () =>
        {
            var echo = new JsonObject { ["provider"] = "faux", ["model"] = "echo" };
            var draft = new JsonObject { ["prompt"] = "the group's cap", ["maxTokens"] = 4096, ["columns"] = new JsonArray(echo.DeepClone(), echo.DeepClone(), echo.DeepClone()) };
            long worst;
            await using (var probe = CompareHost.Faux("cmp-group-http-probe"))
            {
                var plan = ComparePlanner.Plan(CompareDraft.Parse(draft), probe.Engine).Columns[0].Plan!;
                worst = (await probe.Services.GetRequiredService<GuardedRunExecutor>().Guard.PreflightAsync(new SpendCall
                {
                    Provider = "faux",
                    Model = "echo",
                    Source = SpendSource.Compare,
                    Run = "probe",
                    InputBytes = CostEstimator.InputBytes(plan.Messages),
                    MaxTokens = plan.Spec.MaxTokens,
                }, withApprovals: false)).Estimate.WorstMicros ?? throw new InvalidOperationException("the faux model is priced");
            }
            var root = Temp("cmp-group-http");
            try
            {
                await using var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = Path.Join(root, "data"), Arguments = ["--faux-metered"] });
                var limit = (worst * 5 / 2 / 1_000_000m).ToString("0.######", CultureInfo.InvariantCulture);
                var cap = await app.SendAsync(HttpMethod.Post, "/api/spend/caps",
                    new JsonObject { ["scope"] = "everything", ["period"] = "run", ["limit"] = limit, ["action"] = "ask" }, origin: app.Origin);
                Check(cap.Status == 200, "a per-run Ask cap two columns fit: " + cap.Text);
                var plan = await app.SendAsync(HttpMethod.Post, "/api/compare/plan", draft.DeepClone(), origin: app.Origin);
                var estimate = plan.Body?["estimate"];
                Check(estimate?["verdict"]?.GetValue<string>() == "ask" && estimate["over"]?.GetValue<string>() == "Over run cap" && estimate["pane"]?.GetValue<string>() == "sc-chat",
                    "the estimate by Run all asks before Run: " + plan.Text);
                var html = estimate!["html"]?.GetValue<string>() ?? "";
                Check(html.Contains("data-spend-over", StringComparison.Ordinal) && html.Contains("The columns together are over the", StringComparison.Ordinal),
                    "the over-cap chip, worded as the group's: " + html);
                var two = draft.DeepClone().AsObject();
                two["columns"]!.AsArray().RemoveAt(2);
                Equal("allow", (await app.SendAsync(HttpMethod.Post, "/api/compare/plan", two, origin: app.Origin)).Body?["estimate"]?["verdict"]?.GetValue<string>(),
                    "two columns fit");
                var asked = await app.SendAsync(HttpMethod.Post, "/api/compare/run", draft.DeepClone(), origin: app.Origin);
                Check(asked.Status == 200 && asked.Body?["posted"]?.GetValue<bool>() == false && asked.Body["code"]?.GetValue<string>() == SpendErrors.Ask,
                    "Run all's reservation agrees: asked, nothing sent: " + asked.Text);
                Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),
    ];

    /// <summary>The run engine with Compare (<see cref="CompareModule.AddCompareArea"/>) over a temporary data folder.</summary>
    internal sealed class CompareHost : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly string root;

        private CompareHost(ServiceProvider provider, string root, WebAgentOptions options, ProviderFake fake, ConcurrentQueue<string> logs)
        {
            this.provider = provider;
            this.root = root;
            Options = options;
            Provider = fake;
            Logs = logs;
        }

        public WebAgentOptions Options { get; }

        public IServiceProvider Services => provider;

        public RunEngine Engine => provider.GetRequiredService<RunEngine>();

        public RunStore Store => provider.GetRequiredService<RunStore>();

        public ComparePages Pages => provider.GetRequiredService<ComparePages>();

        public CompareStore Groups => provider.GetRequiredService<CompareStore>();

        public IAccountStore Accounts => provider.GetRequiredService<IAccountStore>();

        public ProviderFake Provider { get; }

        public RunsDb Runs => provider.GetRequiredService<RunsDb>();

        /// <summary>What the app logged (only <see cref="Routing"/> records it).</summary>
        public ConcurrentQueue<string> Logs { get; }

        public string LogText => string.Join("\n", Logs);

        public string Database => Path.Join(Options.DataDirectory, "webagent.db");

        /// <summary>Over the priced faux models (--faux --faux-metered).</summary>
        public static CompareHost Faux(string name)
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
            return Build(services, root, options, new ProviderFake(), new ConcurrentQueue<string>());
        }

        /// <summary>Over real routing with a fake provider as every provider request's handler (no request leaves the process).</summary>
        public static CompareHost Routing(string name)
        {
            var root = Temp(name);
            var options = new WebAgentOptions { DataDirectory = Path.Join(root, "data") };
            options.EnsureDirectories();
            var fake = new ProviderFake();
            var logs = new ConcurrentQueue<string>();
            var services = new ServiceCollection();
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug).AddProvider(new ListLoggerProvider(logs)));
            services.AddSingleton(RoutingCredentials(new Dictionary<string, string>(), new FakeOAuth(), root));
            services.AddSingleton(new RoutingOptions { ProviderHandler = fake, CodexTransport = "sse" });
            services.AddWebAgentCore(options);
            services.AddCredentialsArea(options);
            services.AddRoutingArea(options);
            services.AddWebAgentFallbacks(options);
            services.AddSpendArea(options);
            services.AddRunsArea(options);
            CompareModule.AddCompareArea(services, options);
            return Build(services, root, options, fake, logs);
        }

        private static CompareHost Build(IServiceCollection services, string root, WebAgentOptions options, ProviderFake fake, ConcurrentQueue<string> logs)
        {
            var provider = services.BuildServiceProvider();
            provider.GetRequiredService<WebAgentDb>().Migrate();
            return new CompareHost(provider, root, options, fake, logs);
        }

        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            SqliteClearPools();
            DeleteTemp(root);
        }
    }
}
