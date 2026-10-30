// Decision 0011 (run W4, R3): History › Runs and the Run inspector. The built app renders /history empty, then with priced faux runs
// (default), with each filter (filtered, with chips and "N of M runs") and the inspector of a faux run (default: its raw tabs say "not
// stored", since an offline model sends no HTTP request) and of a tool call; /runs/{op} goes to the run and an unknown run is a 404; the
// Spend ledger links a run's row to its inspector. In process, an agent compare run is one row that expands to its models and its
// inspector lists them; a failed model's inspector shows the error state. Bulk delete, export (CSV, JSON, chosen ids), Pin, Clear history,
// re-run specs and saved filters go through the app's APIs. Over real routing with a fake provider and a planted key, a run without raw
// payloads says "not stored", and with raw payloads the Authorization header always shows redacted and no page, HAR, JSON or export
// carries the key. Nothing the CSP would block is on any page.
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Runs;

internal static partial class Program
{
    private static readonly string[] HistoryTabs = ["request", "response", "sse", "events", "timing", "usage"];

    internal static IEnumerable<(string, Func<Task>)> HistoryCases() =>
    [
        Case("history.page.renders-empty-default-filtered-and-the-inspector", async () =>
        {
            var root = Temp("hist-page");
            try
            {
                await using var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = Path.Join(root, "data"), Arguments = ["--faux-metered"] });

                // Empty.
                var empty = await app.Get("/history");
                Equal(200, empty.Status, "GET /history");
                Check(empty.Response.Headers.CacheControl?.NoStore == true, "never cached");
                var html = empty.Text;
                HistoryNoInline("/history (empty)", html);
                Check(!html.Contains("Coming in a later milestone", StringComparison.Ordinal), "History is no longer the later-milestone page");
                Check(html.Contains("<h1 class=\"app-title\">History</h1>", StringComparison.Ordinal), "the top bar title");
                Check(html.Contains("<a class=\"nav-link active\" aria-current=\"page\" href=\"/history\">Runs</a>", StringComparison.Ordinal)
                    && html.Contains("<a class=\"nav-link\" href=\"/history/spend\">Spend</a>", StringComparison.Ordinal), "the History tabs, Runs active");
                Check(html.Contains("data-run-history data-state=\"empty\"", StringComparison.Ordinal) && html.Contains("data-runs-empty", StringComparison.Ordinal)
                    && html.Contains("No runs <em>yet</em>", StringComparison.Ordinal), "the empty state: " + Cut(html));
                Check(html.Contains("data-run-totals", StringComparison.Ordinal), "the totals strip");
                Check(!html.Contains("data-run-table", StringComparison.Ordinal) && !html.Contains("data-run-filters", StringComparison.Ordinal), "no table or filters while empty");
                Check(Regex.IsMatch(html, "<script type=\"module\" src=\"/js/history\\.js\\?v=") && Regex.IsMatch(html, "<link href=\"/css/history\\.css\\?v="), "its module and stylesheet");
                Check(html.Contains("id=\"modal-test-case\"", StringComparison.Ordinal) && !html.Contains("data-pin-run=", StringComparison.Ordinal),
                    "W5 (E7): the Pin as test case modal is hosted, with nothing to pin yet");
                foreach (var asset in new[] { "/js/history.js", "/js/history-actions.js", "/js/inspector.js", "/css/history.css" })
                {
                    var file = await app.Get(asset);
                    Equal(200, file.Status, asset);
                    Check(!file.Text.Contains("innerHTML", StringComparison.Ordinal) && !file.Text.Contains("insertAdjacentHTML", StringComparison.Ordinal), asset + " writes text, never HTML");
                    if (asset.EndsWith(".css", StringComparison.Ordinal))
                        Check(file.Text.Contains(".sse-row[data-extra=\"true\"]", StringComparison.Ordinal) && !file.Text.Contains(".sse-row[data-extra]", StringComparison.Ordinal),
                            "Collapse deltas hides only the extra deltas (Razor renders a null data- attribute as empty, so [data-extra] would match every row)");
                }

                // Two runs: an answer and a tool call.
                var answer = await HistoryRunAsync(app, new JsonObject
                {
                    ["provider"] = "faux",
                    ["model"] = "echo",
                    ["source"] = "playground",
                    ["systemPrompt"] = "Be brief.",
                    ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = "hello history" }),
                });
                var tool = await HistoryRunAsync(app, new JsonObject
                {
                    ["provider"] = "faux",
                    ["model"] = "echo",
                    ["source"] = "playground",
                    ["tools"] = new JsonArray(new JsonObject { ["name"] = "get_weather", ["parameters"] = new JsonObject { ["type"] = "object" } }),
                    ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = "tool: get_weather {\"city\":\"Oslo\"}" }),
                });

                var page = (await app.Get("/history")).Text;
                HistoryNoInline("/history", page);
                Check(page.Contains("data-run-history data-state=\"default\"", StringComparison.Ordinal), "the default state");
                Check(page.Contains($"data-run-row=\"{answer}\"", StringComparison.Ordinal) && page.Contains($"data-run-row=\"{tool}\"", StringComparison.Ordinal), "a row per run: " + Cut(page));
                Check(page.Contains("data-bulk-bar hidden", StringComparison.Ordinal) && page.Contains("data-run-pagination", StringComparison.Ordinal)
                    && page.Contains("data-run-filters", StringComparison.Ordinal), "the filters, the hidden bulk bar and the pagination");
                Check(page.Contains("hello history", StringComparison.Ordinal) && page.Contains("→ get_weather", StringComparison.Ordinal), "the prompts, and the tools a run called");
                Check(page.Contains($"href=\"/runs/{answer}\"", StringComparison.Ordinal) && page.Contains($"data-run-rerun=\"{answer}\"", StringComparison.Ordinal)
                    && page.Contains($"href=\"/playground?run={answer}\"", StringComparison.Ordinal) && page.Contains($"data-run-delete=\"{answer}\"", StringComparison.Ordinal),
                    "Inspect, Re-run, Load into playground and Delete");
                Check(page.Contains("data-metric=\"Runs\"", StringComparison.Ordinal), "the totals");
                Check(page.Contains($"data-pin-run=\"{answer}\" data-pin-source=\"history\"", StringComparison.Ordinal), "W5 (E7): Pin as test… on a completed run's menu");

                // Filtered.
                var tools = (await app.Get("/history?status=toolUse")).Text;
                Check(tools.Contains("data-run-history data-state=\"filtered\"", StringComparison.Ordinal) && tools.Contains("data-run-chips", StringComparison.Ordinal), "the filtered state");
                Check(tools.Contains($"data-run-row=\"{tool}\"", StringComparison.Ordinal) && !tools.Contains($"data-run-row=\"{answer}\"", StringComparison.Ordinal), "status=toolUse keeps the tool call");
                Check(tools.Contains("1 of 2 runs", StringComparison.Ordinal), "N of M runs");
                var search = (await app.Get("/history?q=hello+history")).Text;
                Check(search.Contains($"data-run-row=\"{answer}\"", StringComparison.Ordinal) && !search.Contains($"data-run-row=\"{tool}\"", StringComparison.Ordinal), "search");
                var byId = (await app.Get("/history?q=" + Uri.EscapeDataString(tool))).Text;
                Check(byId.Contains($"data-run-row=\"{tool}\"", StringComparison.Ordinal) && !byId.Contains($"data-run-row=\"{answer}\"", StringComparison.Ordinal), "search by run id");
                Check((await app.Get("/history?source=chat")).Text.Contains("data-run-none", StringComparison.Ordinal), "no chat runs");
                var model = (await app.Get("/history?provider=faux&model=echo")).Text;
                Check(model.Contains($"data-run-row=\"{answer}\"", StringComparison.Ordinal) && model.Contains($"data-run-row=\"{tool}\"", StringComparison.Ordinal)
                    && model.Contains("name=\"model\"", StringComparison.Ordinal), "provider and model");
                Check((await app.Get("/history?from=2000-01-01&to=2000-01-02")).Text.Contains("data-run-none", StringComparison.Ordinal), "a date range with no runs");
                var today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                Check((await app.Get($"/history?from={today}&to={today}")).Text.Contains($"data-run-row=\"{answer}\"", StringComparison.Ordinal), "today's runs");
                Equal(200, (await app.Get("/history?page=99&size=50&status=nonsense&from=bad")).Status, "a bad query is ignored, not an error");

                // The inspector of a faux run: no HTTP request, so the raw tabs say not stored.
                var inspect = await app.Get("/runs/" + answer);
                Equal(200, inspect.Status, "GET /runs/{id}");
                var inspector = inspect.Text;
                HistoryNoInline("/runs/{id}", inspector);
                Check(inspector.Contains($"data-run-inspector=\"{answer}\" data-state=\"default\"", StringComparison.Ordinal), "the default state: " + Cut(inspector));
                Check(inspector.Contains("<nav class=\"app-breadcrumb\" aria-label=\"Breadcrumb\">", StringComparison.Ordinal) && inspector.Contains($"Run {answer}</h1>", StringComparison.Ordinal),
                    "the breadcrumb History › Run id");
                foreach (var tab in HistoryTabs)
                    Check(inspector.Contains($"id=\"run-pane-{tab}\"", StringComparison.Ordinal), "the " + tab + " tab");
                Check(inspector.Contains("Raw request not stored", StringComparison.Ordinal) && inspector.Contains("Raw SSE not stored", StringComparison.Ordinal)
                    && inspector.Contains("offline faux model", StringComparison.Ordinal), "the raw tabs say not stored, and why");
                Check(inspector.Contains("Echo: hello history", StringComparison.Ordinal), "the final message as stored");
                Check(inspector.Contains("data-run-events", StringComparison.Ordinal) && inspector.Contains("data-run-waterfall", StringComparison.Ordinal), "the events and the timing");
                Check(inspector.Contains("id=\"modal-view-code\"", StringComparison.Ordinal) && inspector.Contains($"Reproduce run {answer}", StringComparison.Ordinal), "View code of its request");
                Check(inspector.Contains($"data-run-rerun=\"{answer}\"", StringComparison.Ordinal) && inspector.Contains($"href=\"/playground?run={answer}\"", StringComparison.Ordinal)
                    && inspector.Contains($"data-run-delete=\"{answer}\"", StringComparison.Ordinal) && inspector.Contains("data-run-after-delete=\"/history\"", StringComparison.Ordinal),
                    "Re-run, Load into playground and Delete (back to History)");
                Check(inspector.Contains($"/api/history/runs/{answer}/json", StringComparison.Ordinal) && !inspector.Contains($"/api/history/runs/{answer}/har", StringComparison.Ordinal),
                    "Run JSON, and no HAR without raw payloads");
                Check(inspector.Contains($"data-pin-run=\"{answer}\" data-pin-source=\"inspector\"", StringComparison.Ordinal) && inspector.Contains("id=\"modal-test-case\"", StringComparison.Ordinal),
                    "W5 (E7): ⋯ Pin as test… and its modal");
                Check(Regex.IsMatch(inspector, "<script type=\"module\" src=\"/js/inspector\\.js\\?v="), "its module");

                var toolPage = (await app.Get("/runs/" + tool)).Text;
                Check(toolPage.Contains($"data-run-inspector=\"{tool}\" data-state=\"toolcall\"", StringComparison.Ordinal) && toolPage.Contains("data-run-toolcall", StringComparison.Ordinal)
                    && toolPage.Contains("get_weather", StringComparison.Ordinal) && toolPage.Contains("Continue in Playground", StringComparison.Ordinal), "the tool call state");

                var record = await app.Get("/api/runs/" + answer);
                var op = record.Body?["run"]?["op"]?.GetValue<string>() ?? record.Body?["op"]?.GetValue<string>();
                Check(op is not null, "the run's op: " + record.Text);
                var byOp = await app.Get("/runs/" + Uri.EscapeDataString(op!));
                Check(byOp.Status == 200 && byOp.Response.RequestMessage?.RequestUri?.AbsolutePath == "/runs/" + answer, "/runs/{op} goes to /runs/{id}");
                var missing = await app.Get("/runs/nope");
                Equal(404, missing.Status, "an unknown run");
                Check(missing.Text.Contains("There is no run <span class=\"ps-mono text-break\">nope</span>", StringComparison.Ordinal) && missing.Text.Contains("href=\"/history\"", StringComparison.Ordinal),
                    "says so, with the way back");
                Equal(404, (await app.Get($"/api/history/runs/{answer}/har")).Status, "no HAR without raw payloads");
                var json = await app.Get($"/api/history/runs/{answer}/json");
                Equal(200, json.Status, "Run JSON");
                Check(json.Response.Content.Headers.ContentDisposition?.FileName?.Trim('"') == answer + ".json" && json.Body?["id"]?.GetValue<string>() == answer
                    && json.Body["events"] is JsonArray { Count: > 0 }, "the run with its events, as a file: " + Cut(json.Text));

                // History › Spend links a run's ledger row to its inspector.
                var spend = (await app.Get("/history/spend")).Text;
                Check(Regex.IsMatch(spend, $"href=\"/runs/{Regex.Escape(answer)}\"[^>]*data-ledger-run"), "a run's ledger row opens the inspector: " + Cut(spend));
                Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("history.groups-an-agent-compare-run-and-renders-the-error-state", async () =>
        {
            await using var host = RunsHost.Faux("hist-group");
            await using var render = RenderHost.Create("hist-group-render", (_, _) => { });
            var store = host.Store;
            var now = DateTimeOffset.UtcNow;
            RunRecord Row(string id, int minutes, string kind = RunKind.Call) => new()
            {
                Id = id,
                Op = "op-" + id,
                Kind = kind,
                Source = "playground",
                Status = RunStatus.Completed,
                StopReason = "stop",
                Provider = "faux",
                Model = "echo",
                Title = "hello " + id,
                Created = now.AddMinutes(-minutes),
                Started = now.AddMinutes(-minutes),
                Updated = now.AddMinutes(-minutes),
                DurationMs = 1200,
            };
            store.Insert(Row("solo", 1));
            store.Insert(Row("grp", 5, RunKind.Parent) with { Source = "agent-compare", Provider = "", Model = "", Title = "Refactor the parser", StopReason = null, DurationMs = 61_000 });
            store.Insert(Row("grp-a", 5) with { Source = "agent-compare", ParentId = "grp", GroupId = "grp", Notes = "3 turns" });
            store.Insert(Row("grp-b", 5) with
            {
                Source = "agent-compare",
                ParentId = "grp",
                GroupId = "grp",
                Model = "echo-thinking",
                Status = RunStatus.Error,
                StopReason = "error",
                Error = "429 Too Many Requests: usage limit <reached>",
            });

            var pages = new RunHistoryPages(host.Services);
            var page = pages.Page(RunHistoryFilter.ParseQuery(null));
            Equal("grp,solo", string.Join(",", page.Rows.Select(row => row.Run.Id).Order(StringComparer.Ordinal)), "one row per top-level run");
            var group = page.Rows.Single(row => row.Run.Id == "grp");
            Check(group.IsGroup && group.Models.Count == 2, "the agent compare run groups its models");
            var table = await render.Renderer.RenderAsync("/Views/Runs/_RunTable.cshtml", page);
            HistoryNoInline("_RunTable", table);
            Check(table.Contains("2 models", StringComparison.Ordinal) && table.Contains("data-run-models=\"grp\"", StringComparison.Ordinal)
                && table.Contains("data-bs-target=\"#run-grp-models\"", StringComparison.Ordinal), "one row that expands to its models: " + Cut(table));
            Check(table.Contains("href=\"/runs/grp-a\"", StringComparison.Ordinal) && table.Contains("href=\"/runs/grp-b\"", StringComparison.Ordinal)
                && !table.Contains("data-run-row=\"grp-a\"", StringComparison.Ordinal), "each model inspects its own run, and is not a row of its own");
            Check(table.Contains("href=\"/settings/data#compare-workspaces\"", StringComparison.Ordinal), "Workspaces");
            var thinking = pages.Page(RunHistoryFilter.ParseQuery("provider=faux&model=echo-thinking"));
            Equal("grp", string.Join(",", thinking.Rows.Select(row => row.Run.Id)), "a model filter finds the group by its model");
            var errors = pages.Page(RunHistoryFilter.ParseQuery("status=error"));
            Check(errors.Rows.All(row => row.Run.Id != "solo"), "status=error leaves out the completed run");

            var inspector = new RunInspectorPages(host.Services);
            var groupView = await inspector.ViewAsync(store.Get("grp")!, "http://127.0.0.1:5180");
            Equal("group", groupView.State, "a grouped run's inspector");
            Equal(2, groupView.Children.Count, "lists what it groups");
            var groupHtml = await render.Renderer.RenderAsync("/Views/Runs/Details.cshtml", groupView);
            HistoryNoInline("Details (group)", groupHtml);
            Check(groupHtml.Contains("data-run-children", StringComparison.Ordinal) && groupHtml.Contains("data-run-child=\"grp-a\"", StringComparison.Ordinal)
                && groupHtml.Contains("data-run-child=\"grp-b\"", StringComparison.Ordinal) && !groupHtml.Contains("data-run-tabs", StringComparison.Ordinal), "its models instead of the tabs");

            var failedView = await inspector.ViewAsync(store.Get("grp-b")!, "http://127.0.0.1:5180");
            Equal("error", failedView.State, "a failed model's inspector");
            Equal("/runs/grp", failedView.ParentHref, "with its parent");
            var failed = await render.Renderer.RenderAsync("/Views/Runs/Details.cshtml", failedView);
            HistoryNoInline("Details (error)", failed);
            Check(failed.Contains("data-state=\"error\"", StringComparison.Ordinal) && failed.Contains("data-run-error", StringComparison.Ordinal)
                && failed.Contains("data-show-tab=\"response\"", StringComparison.Ordinal), "the error banner with Show response: " + Cut(failed));
            Check(failed.Contains("usage limit &lt;reached&gt;", StringComparison.Ordinal) && !failed.Contains("<reached>", StringComparison.Ordinal), "the message, encoded");
            Check(failed.Contains("HTTP 429", StringComparison.Ordinal), "the HTTP status in the heading");
            Check(failed.Contains("href=\"/runs/grp\"", StringComparison.Ordinal) && failed.Contains("Open parent run", StringComparison.Ordinal), "the way to its parent");
            Check(failed.Contains("Raw request not stored", StringComparison.Ordinal), "not stored");
            Check(!failed.Contains("Pin as test", StringComparison.Ordinal), "no Pin as test (W5)");

            Equal(3, store.Delete(["grp"]), "deleting the group takes its models");
            Check(store.Get("grp-a") is null && store.Get("solo") is not null, "and nothing else");
        }),

        Case("history.api.bulk-delete-export-pin-clear-rerun-and-saved-filters", async () =>
        {
            await using var app = await WebAgentApp.StartAsync("hist-api");
            var ids = new List<string>();
            foreach (var text in new[] { "first", "second", "third, with \"quotes\"" })
                ids.Add(await HistoryRunAsync(app, new JsonObject
                {
                    ["provider"] = "faux",
                    ["model"] = "echo",
                    ["source"] = "playground",
                    ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = text }),
                }));

            // Export.
            var csv = await app.Get("/api/history/export?format=csv");
            Equal(200, csv.Status, "CSV export");
            Check(csv.Response.Content.Headers.ContentDisposition?.FileName?.Trim('"') is { } csvName && csvName.StartsWith("pisharp-runs-", StringComparison.Ordinal) && csvName.EndsWith(".csv", StringComparison.Ordinal),
                "a CSV file: " + csv.Response.Content.Headers.ContentDisposition);
            Check(ids.All(id => csv.Text.Contains(id, StringComparison.Ordinal)), "every run: " + Cut(csv.Text));
            Check(csv.Response.Headers.CacheControl?.NoStore == true, "never cached");
            var all = await app.Get("/api/history/export?format=json");
            Equal(3, all.Body?["count"]?.GetValue<int>(), "JSON export: " + Cut(all.Text));
            var chosen = await app.Get($"/api/history/export?format=json&ids={ids[0]},{ids[1]}");
            Equal(2, chosen.Body?["count"]?.GetValue<int>(), "the chosen runs: " + Cut(chosen.Text));
            var filtered = await app.Get("/api/history/export?format=json&q=second");
            Equal(1, filtered.Body?["count"]?.GetValue<int>(), "the runs matching the filters");
            var withRaw = await app.Get("/api/history/export?format=json-raw");
            Check(withRaw.Status == 200 && withRaw.Body?["rawPayloads"]?.GetValue<bool>() == true && withRaw.Body["runs"]?[0]?["events"] is JsonArray, "JSON with raw payloads and events");
            Equal(400, (await app.Get("/api/history/export?format=xml")).Status, "an unknown format");

            // Re-run.
            var spec = await app.Get($"/api/history/runs/{ids[0]}/spec");
            Equal(200, spec.Status, "the stored request: " + spec.Text);
            Check(spec.Body?["spec"]?["provider"]?.GetValue<string>() == "faux" && spec.Body["spec"]?["messages"] is JsonArray { Count: 1 }, "provider, model and messages: " + spec.Text);
            Equal(404, (await app.Get("/api/history/runs/nope/spec")).Status, "an unknown run");
            var again = await app.SendAsync(HttpMethod.Post, "/api/runs", spec.Body!["spec"]!.DeepClone(), origin: app.Origin);
            Check(again.Body?["posted"]?.GetValue<bool>() == true, "the stored request runs again: " + again.Text);
            var rerun = again.Body!["id"]!.GetValue<string>();
            await RunsSseAsync(app, $"/api/runs/{rerun}/events");
            Check((await app.Get("/runs/" + rerun)).Text.Contains("Echo: first", StringComparison.Ordinal), "with the same answer");

            // Bulk delete.
            var deleted = await app.SendAsync(HttpMethod.Post, "/api/runs/delete", new JsonObject { ["ids"] = new JsonArray(ids[0], ids[1]) }, origin: app.Origin);
            Equal(2, deleted.Body?["deleted"]?.GetValue<int>(), "bulk delete: " + deleted.Text);
            var after = (await app.Get("/history")).Text;
            Check(!after.Contains($"data-run-row=\"{ids[0]}\"", StringComparison.Ordinal) && !after.Contains($"data-run-row=\"{ids[1]}\"", StringComparison.Ordinal)
                && after.Contains($"data-run-row=\"{ids[2]}\"", StringComparison.Ordinal), "the deleted runs are gone");
            Equal(404, (await app.Get("/runs/" + ids[0])).Status, "and their inspector");

            // Saved filters.
            var saved = await app.SendAsync(HttpMethod.Post, "/api/history/filters", new JsonObject { ["name"] = "  Faux   only ", ["query"] = "provider=faux&page=3" }, origin: app.Origin);
            Equal(200, saved.Status, "save a filter: " + saved.Text);
            Equal(("Faux only", "provider=faux"), (saved.Body?["name"]?.GetValue<string>(), saved.Body?["query"]?.GetValue<string>()), "named and normalized");
            Check((await app.Get("/history")).Text.Contains("data-saved-filter=\"Faux only\"", StringComparison.Ordinal), "under Saved filters");
            Equal(400, (await app.SendAsync(HttpMethod.Post, "/api/history/filters", new JsonObject { ["name"] = "Nothing", ["query"] = "page=2" }, origin: app.Origin)).Status, "a filter with nothing set");
            var managed = await app.SendAsync(HttpMethod.Post, "/api/history/filters/manage", new JsonObject
            {
                ["filters"] = new JsonArray(new JsonObject { ["name"] = "Echo runs", ["query"] = "provider=faux&model=echo" }),
            }, origin: app.Origin);
            Check(managed.Body?["filters"] is JsonArray { Count: 1 } list && list[0]?["name"]?.GetValue<string>() == "Echo runs", "manage keeps what was sent: " + managed.Text);
            var savedPage = (await app.Get("/history?provider=faux&model=echo")).Text;
            Check(savedPage.Contains("data-saved-filter=\"Echo runs\"", StringComparison.Ordinal) && !savedPage.Contains("data-saved-filter=\"Faux only\"", StringComparison.Ordinal), "renamed and removed");

            // Pin, then Clear history.
            var pin = await app.SendAsync(HttpMethod.Post, $"/api/runs/{ids[2]}/pin", new JsonObject { ["pinned"] = true }, origin: app.Origin);
            Equal(200, pin.Status, "pin");
            Check((await app.Get("/history")).Text.Contains($"data-run-pin=\"{ids[2]}\" data-pinned=\"true\"", StringComparison.Ordinal), "the row offers Unpin");
            var keep = await app.SendAsync(HttpMethod.Post, "/api/history/clear", new JsonObject { ["keepPinned"] = true }, origin: app.Origin);
            Equal(1, keep.Body?["deleted"]?.GetValue<int>(), "Clear history keeps the pinned run: " + keep.Text);
            Check((await app.Get("/runs/" + ids[2])).Status == 200 && (await app.Get("/runs/" + rerun)).Status == 404, "the pinned run stays, the rest go");
            var clear = await app.SendAsync(HttpMethod.Post, "/api/history/clear", new JsonObject { ["keepPinned"] = false }, origin: app.Origin);
            Equal(1, clear.Body?["deleted"]?.GetValue<int>(), "Clear history without keeping pinned runs");
            Check((await app.Get("/history")).Text.Contains("data-run-history data-state=\"empty\"", StringComparison.Ordinal), "the empty state again");
            Equal(403, (await app.SendAsync(HttpMethod.Post, "/api/history/clear", new JsonObject { ["keepPinned"] = false }, origin: "http://evil.example")).Status, "a cross-origin write is refused");
            Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
        }),

        Case("history.inspector.raw-payloads-redacted-or-not-stored-and-no-secret", async () =>
        {
            await using var host = RunsHost.Routing("hist-raw");
            await using var render = RenderHost.Create("hist-raw-render", (_, _) => { });
            var key = Planted("sk-proj-");
            await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("openai", key));
            var model = ModelOf(host.Services.GetRequiredService<IModelCatalog>(), "openai", "openai-responses", "gpt-4.1");
            RunSpec Spec(string text) => new() { Provider = "openai", Model = model, Source = "playground", Messages = [new RunMessage { Text = text }] };
            var inspector = new RunInspectorPages(host.Services);
            var history = new RunHistoryPages(host.Services);

            // Raw payloads off (the default): every raw tab says not stored.
            var plainStart = await host.Engine.StartAsync(Spec("no raw please"));
            var plain = await RunsFinishedAsync(host.Store, plainStart.Id!);
            Equal(RunStatus.Completed, plain.Status, "completed: " + plain.Error);
            Equal(RunRawState.None, plain.RawState, "no raw payloads");
            var plainView = await inspector.ViewAsync(plain, "http://127.0.0.1:5180");
            Check(plainView.RawNote?.Contains("Raw payloads were off", StringComparison.Ordinal) == true, "why: " + plainView.RawNote);
            var plainTabs = await render.Renderer.RenderAsync("/Views/Runs/_RunTabs.cshtml", plainView);
            Check(plainTabs.Contains("Raw request not stored", StringComparison.Ordinal) && plainTabs.Contains("Raw response not stored", StringComparison.Ordinal)
                && plainTabs.Contains("Raw SSE not stored", StringComparison.Ordinal) && plainTabs.Contains("Store raw payloads", StringComparison.Ordinal), "not stored: " + Cut(plainTabs));
            Check(plainTabs.Contains("data-raw-none=\"request\"", StringComparison.Ordinal) && !plainTabs.Contains("data-raw-request", StringComparison.Ordinal), "no raw request is shown");
            Check(plainTabs.Contains("Request as runs.db stored it", StringComparison.Ordinal) && plainTabs.Contains("no raw please", StringComparison.Ordinal), "the stored request still is");
            Check(inspector.Har(plain) is null, "no HAR");
            Check(plainView.Metrics.Single(metric => metric.Label == "TTFB").Value == "—", "TTFB needs raw payloads");

            // Raw payloads on: the Authorization header shows redacted, and the key is nowhere.
            await host.Services.GetRequiredService<ISettingsStore>().SaveAsync(new RunHistorySettings(StoreRawPayloads: true));
            var rawStart = await host.Engine.StartAsync(Spec("raw please"));
            var raw = await RunsFinishedAsync(host.Store, rawStart.Id!);
            Equal(RunRawState.Stored, raw.RawState, "raw payloads stored");
            var view = await inspector.ViewAsync(raw, "http://127.0.0.1:5180");
            Check(view.RawNote is null && view.Requests.Count == 1 && view.Responses.Count == 1 && view.Sse.Count > 0, "the request, response and SSE");
            var authorization = view.Requests[0].Headers.SingleOrDefault(header => header.Name == "authorization");
            Check(authorization is { Redacted: true, Value: "" }, "Authorization shows redacted: " + string.Join(",", view.Requests[0].Headers.Select(header => header.Name)));
            Check(view.SseGroups.Count > 0 && view.Waterfall.Any(row => row.Label == "TTFB"), "the event families and the waterfall from the timing marks");
            var tabs = await render.Renderer.RenderAsync("/Views/Runs/_RunTabs.cshtml", view);
            var header = await render.Renderer.RenderAsync("/Views/Runs/_RunHeader.cshtml", view);
            var details = await render.Renderer.RenderAsync("/Views/Runs/Details.cshtml", view);
            HistoryNoInline("_RunTabs", tabs + header + details);
            Check(Regex.IsMatch(tabs, "<tr data-header=\"authorization\"><td>authorization</td><td><span class=\"redacted\""), "the redacted header: " + Cut(tabs));
            Check(tabs.Contains("data-raw-request", StringComparison.Ordinal) && tabs.Contains("raw please", StringComparison.Ordinal) && tabs.Contains("data-sse-row", StringComparison.Ordinal)
                && tabs.Contains("response.output_text.delta", StringComparison.Ordinal), "the raw request and the raw SSE");
            Check(tabs.Contains("data-raw-response", StringComparison.Ordinal) && tabs.Contains("data-tone=\"ok\">200", StringComparison.Ordinal), "the response");
            Check(header.Contains($"/api/history/runs/{raw.Id}/har", StringComparison.Ordinal), "Download HAR");
            NoSecret(tabs + header + details, "the inspector", key);

            var har = inspector.Har(raw);
            Check(har?["log"]?["entries"] is JsonArray { Count: 1 }, "a HAR entry per attempt: " + har?.ToJsonString());
            NoSecret(har!.ToJsonString(), "the HAR", key);
            Check(har.ToJsonString().Contains("text/event-stream", StringComparison.Ordinal) || har.ToJsonString().Contains("response.output_text.delta", StringComparison.Ordinal), "with the stream");
            NoSecret(inspector.RunJson(raw).ToJsonString(), "the Run JSON", key);
            foreach (var format in RunHistoryPages.ExportFormats)
            {
                var file = history.Export(format, RunHistoryFilter.ParseQuery(null), null);
                var text = Encoding.UTF8.GetString(file.Content);
                Check(text.Contains(raw.Id, StringComparison.Ordinal), format + " has the run");
                NoSecret(text, "the " + format + " export", key);
            }
            var historyPage = history.Page(RunHistoryFilter.ParseQuery(null));
            var listHtml = await render.Renderer.RenderAsync("/Views/Runs/_RunTable.cshtml", historyPage);
            NoSecret(listHtml, "the run table", key);
            Check(history.RerunSpec(raw.Id)?.ToJsonString() is { } rerun && !rerun.Contains("PLANTED", StringComparison.Ordinal) && rerun.Contains("raw please", StringComparison.Ordinal), "the re-run spec carries no key");
            NoSecret(host.LogText, "the log", key);
        }),
    ];

    /// <summary>Posts <paramref name="spec"/> to /api/runs and waits for the run to end; returns its id.</summary>
    private static async Task<string> HistoryRunAsync(WebAgentApp app, JsonObject spec)
    {
        var posted = await app.SendAsync(HttpMethod.Post, "/api/runs", spec, origin: app.Origin);
        Check(posted.Status == 200 && posted.Body?["posted"]?.GetValue<bool>() == true, "posted: " + posted.Text);
        var id = posted.Body!["id"]!.GetValue<string>();
        var frames = await RunsSseAsync(app, $"/api/runs/{id}/events");
        Equal("status", frames[^1].Event, "the run ends");
        return id;
    }

    /// <summary>Nothing the Content-Security-Policy (script-src 'self') would block: no inline script, no inline event handler.</summary>
    private static void HistoryNoInline(string where, string html)
    {
        var razor = Regex.Match(html, "@(if|foreach|for|Model|row|run)\b");
        Check(!razor.Success, $"{where}: no Razor code rendered as text ({(razor.Success ? html.Substring(Math.Max(0, razor.Index - 40), Math.Min(80, html.Length - Math.Max(0, razor.Index - 40))) : "")})");
        foreach (Match script in Regex.Matches(html, "<script\\b[^>]*>"))
            Check(script.Value.Contains("src=", StringComparison.Ordinal), $"{where}: no inline script ({script.Value})");
        var handler = Regex.Match(html, "<[^>]+\\son[a-z]+\\s*=", RegexOptions.IgnoreCase);
        Check(!handler.Success, $"{where}: no inline event handlers ({(handler.Success ? handler.Value : "")})");
    }
}
