using System.Text.Json.Nodes;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W4, T4): the request pages in a browser, on priced faux (--faux-metered: offline, but each call is reserved and settled
// at a list price, so the cost metrics and the ledger hold real numbers), failing on any page or console error. A Playground run streams
// into the output pane (the stream text grows while the run's state is streaming) and then shows its metrics (TTFT, total, tokens per
// second, input and output tokens, the settled cost and the stop reason), and View code's footer then links "Open last run in inspector"
// (hidden before the first run); a second run keeps going when its tab closes, and a new tab on
// its ?run= address shows it whole. Compare adds a third column through the model picker, runs all three in one group (one ledger row per
// column, all under the group's run) and shows each answer with the metrics table. The Tool Bench adds a tool from a template, pauses on
// the model's call (no saved mock matches), takes a manual result and continues to the model's answer; the call log names the result
// manual. History lists the Playground and Compare runs with their sources, filters by source, opens the inspector on each tab, and
// bulk-deletes a selected run after its confirmation.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> W4RunCases() =>
    [
        Browse("browser.w4.playground.a-run-streams-shows-its-metrics-and-survives-a-closed-tab", AgentMode.FauxMetered, async (app, window) =>
        {
            var page = window.Page;
            await page.GotoAsync("/playground");
            var output = page.Locator("[data-run-output]");
            var user = page.Locator("[data-msg][data-role='user'] [data-msg-text]").First;
            await user.FillAsync("slow: " + Words(60));

            // View code before any run: the code is there, "Open last run in inspector" is not.
            var code = await page.OpenModalAsync(page.Locator("[data-view-code] >> visible=true").First, "modal-view-code");
            await Expect(code.Locator("[data-code='curl']")).ToContainTextAsync("/api/runs");
            await Expect(code.Locator("[data-code-inspector]")).ToBeHiddenAsync();
            await page.DismissModalAsync("modal-view-code");

            await page.Locator("[data-run]").ClickAsync();
            await Expect(output).ToHaveAttributeAsync("data-state", "streaming");
            var stream = output.Locator("[data-stream-text]");
            await Expect(stream).ToContainTextAsync("w001");
            var partial = await stream.InnerTextAsync();
            Check(!partial.Contains("w060", StringComparison.Ordinal), "the answer streams in (the first words show before the last): " + partial);
            var id = await page.QueryAsync("run");
            await Expect(output).ToHaveAttributeAsync("data-state", "completed", new() { Timeout = 30_000 });
            await Expect(output.Locator("[data-output-plain]")).ToContainTextAsync(Words(60));
            Equal("completed", await app.RunEndedAsync(id), "the run completed");

            // The metrics of the ended run: timings, token counts, the settled cost and the stop reason.
            foreach (var name in new[] { "TTFT", "Total", "Tokens/s", "Input", "Output" })
            {
                var value = (await page.MetricAsync(name)).Trim();
                Check(value.Length > 0 && !value.StartsWith('—'), $"{name} has a value: {value}");
            }
            Check((await page.MetricAsync("Output")).Trim() != "0", "output tokens were counted");
            Check((await page.MetricAsync("Cost")).Trim().StartsWith('$'), "the cost is priced: " + await page.MetricAsync("Cost"));
            Equal("stop", (await page.MetricAsync("stopReason")).Trim(), "the stop reason");
            var row = (await app.SettledLedgerAsync(1)).Single();
            Equal("playground", Reservation(row, "source"), "the ledger row is the Playground's");
            Equal(id, Reservation(row, "run"), "under the run's id");
            Equal("settled", Reservation(row, "state"), "and settled");
            await Expect(page.Locator("[data-open-inspector]")).ToHaveAttributeAsync("href", "/runs/" + id);

            // After the run, View code's footer has the mockup's "Open last run in inspector", to this run.
            await page.OpenModalAsync(page.Locator("[data-view-code] >> visible=true").First, "modal-view-code");
            var last = code.Locator("[data-code-inspector]");
            await Expect(last).ToBeVisibleAsync();
            await Expect(last).ToHaveAttributeAsync("href", "/runs/" + Uri.EscapeDataString(id));
            await Expect(last).ToHaveTextAsync("Open last run in inspector");
            await page.DismissModalAsync("modal-view-code");

            // A second run keeps going after its tab closes; a new tab on its address shows the whole answer.
            await user.FillAsync("slow: " + Words(100));
            await page.Locator("[data-run]").ClickAsync();
            await Expect(output).ToHaveAttributeAsync("data-state", "streaming");
            await Expect(stream).ToContainTextAsync("w002");
            string second = null!;
            await WaitUntilAsync(async () => (second = await page.QueryAsync("run")) != id, "the second run's address");
            Check(!(await stream.InnerTextAsync()).Contains("w100", StringComparison.Ordinal), "the tab closes while the run streams");
            await page.CloseAsync();
            var other = await window.NewPageAsync();
            await other.GotoAsync("/playground?run=" + Uri.EscapeDataString(second));
            var reopened = other.Locator("[data-run-output]");
            await Expect(reopened).ToHaveAttributeAsync("data-state", "completed", new() { Timeout = 30_000 });
            await Expect(reopened.Locator("[data-output-plain]")).ToContainTextAsync(Words(100));
            Equal("completed", await app.RunEndedAsync(second), "the run finished with no tab open");
            Equal(2, (await app.SettledLedgerAsync(2)).Count(item => Reservation(item, "source") == "playground"), "one ledger row per run");
        }),

        Browse("browser.w4.compare.three-columns-run-as-one-group", AgentMode.FauxMetered, async (app, window) =>
        {
            var page = window.Page;
            await page.GotoAsync("/compare");
            var columns = page.Locator("[data-compare-col]");
            await Expect(columns).ToHaveCountAsync(2);
            var picker = await page.OpenModalAsync(page.Locator("[data-col-add]"), "modal-model-picker");
            await picker.Locator("#mp-model-faux").SelectOptionAsync("echo-thinking");
            await picker.Locator("[type='submit']").ClickAsync();
            await Expect(picker).ToBeHiddenAsync();
            await Expect(columns).ToHaveCountAsync(3);
            await Expect(columns.Nth(2)).ToHaveAttributeAsync("data-model", "echo-thinking");

            await page.Locator("#cmpPrompt").FillAsync("Name three rivers.");
            await page.Locator("[data-run-all]").ClickAsync();
            var group = await page.QueryAsync("group");
            await Expect(page.Locator("[data-compare-status-text]")).ToContainTextAsync("3 of 3 done", new() { Timeout = 30_000 });
            for (var i = 0; i < 3; i++)
            {
                await Expect(columns.Nth(i)).ToHaveAttributeAsync("data-state", "completed");
                await Expect(columns.Nth(i)).ToHaveAttributeAsync("data-group", group);
                await Expect(columns.Nth(i)).ToContainTextAsync("Name three rivers.");
            }
            var runs = new List<string>();
            for (var i = 0; i < 3; i++) runs.Add((await columns.Nth(i).GetAttributeAsync("data-run-id"))!);
            Equal(3, runs.Distinct(StringComparer.Ordinal).Count(), "three runs");
            var metrics = page.Locator("[data-compare-metrics]");
            foreach (var model in new[] { "Echo", "Echo small", "Echo thinking" }) await Expect(metrics).ToContainTextAsync(model);
            await Expect(metrics).ToContainTextAsync("Tokens out");
            await Expect(metrics).ToContainTextAsync("$");

            // One reservation per column, all under the group's run, each settled.
            var rows = await app.SettledLedgerAsync(3);
            Check(rows.All(row => Reservation(row, "source") == "compare" && Reservation(row, "run") == group && Reservation(row, "state") == "settled"),
                "the columns' ledger rows share the group: " + string.Join(" | ", rows.Select(row => row.ToJsonString())));
            foreach (var run in runs) Equal("completed", await app.RunEndedAsync(run), $"column run {run} completed");

            // The address reopens the comparison.
            await page.ReloadAsync();
            await Expect(columns).ToHaveCountAsync(3);
            await Expect(page.Locator("[data-compare-status-text]")).ToContainTextAsync("3 of 3 done");
        }),

        Browse("browser.w4.bench.a-tool-call-pauses-for-a-manual-result", AgentMode.FauxMetered, async (app, window) =>
        {
            var page = window.Page;
            await page.GotoAsync("/bench");
            var bench = page.Locator("[data-bench]");
            await Expect(bench).ToBeVisibleAsync();

            // A tool from a template, through the tool editor.
            await page.Locator("[data-bench-part='tools'] [data-bs-toggle='dropdown'] >> visible=true").First.ClickAsync();
            var editor = await page.OpenModalAsync(page.Locator("[data-bench-template*='get_weather'] >> visible=true").First, "modal-tool-editor");
            await editor.Locator("[data-tool-submit]").ClickAsync();
            await Expect(editor).ToBeHiddenAsync();
            await Expect(page.Locator("[data-bench-part='tools']")).ToContainTextAsync("get_weather");

            // The model calls it; no saved mock matches, so the run waits for a result.
            await page.Locator("#benchUserMsg").FillAsync("tool: get_weather {\"location\":\"Oslo, Norway\"}");
            await page.Locator("[data-bench-add-run]").ClickAsync();
            await Expect(bench).ToHaveAttributeAsync("data-state", "awaiting", new() { Timeout = 30_000 });
            var awaiting = page.Locator("[data-awaiting]");
            await Expect(awaiting).ToBeVisibleAsync();
            await Expect(awaiting).ToContainTextAsync("get_weather");
            await Expect(awaiting).ToContainTextAsync("No saved mock matches");
            await Expect(page.Locator("[data-bench-part='timeline']")).ToContainTextAsync("Oslo, Norway");

            // A manual result, then the model's answer.
            await awaiting.Locator("[data-result-text]").FillAsync("{\"tempC\": 4, \"sky\": \"drizzle\"}");
            await awaiting.Locator("[data-result-send='continue']").ClickAsync();
            await Expect(bench).ToHaveAttributeAsync("data-bench-state", "finished", new() { Timeout = 30_000 });
            await Expect(page.Locator("[data-bench-part='timeline']")).ToContainTextAsync("Tool finished: get_weather");
            await Expect(page.Locator("[data-bench-part='timeline']")).ToContainTextAsync("drizzle");
            await Expect(page.Locator("[data-bench-part='log']")).ToContainTextAsync("manual");
            Check(!await awaiting.IsVisibleAsync(), "nothing waits any more");

            // Each turn was a guarded bench run: two, under one bench group.
            var rows = await app.SettledLedgerAsync(2);
            Check(rows.All(row => Reservation(row, "source") == "bench"), "bench rows: " + string.Join(" | ", rows.Select(row => row.ToJsonString())));
            Equal(1, rows.Select(row => Reservation(row, "run")).Distinct(StringComparer.Ordinal).Count(), "one bench group");
        }),

        Browse("browser.w4.history.lists-filters-inspects-and-deletes-runs", AgentMode.FauxMetered, async (app, window) =>
        {
            var playground = await app.PlaygroundRunAsync("history one", system: "Be brief.");
            var kept = await app.PlaygroundRunAsync("history two");
            var (group, columns) = await app.CompareGroupAsync("history compare", "echo", "echo-small");
            var page = window.Page;
            await page.GotoAsync("/history");
            var history = page.Locator("[data-run-history]");
            await Expect(history).ToHaveAttributeAsync("data-state", "default");
            foreach (var id in new[] { playground, kept }.Concat(columns))
                await Expect(page.Locator($"tr#run-{id}[data-run-row]")).ToHaveAttributeAsync("data-run-status", "completed");
            await Expect(page.Locator($"tr#run-{playground}")).ToContainTextAsync("Playground");
            await Expect(page.Locator($"tr#run-{columns[0]}")).ToContainTextAsync("Compare");

            // Filter by source: the address carries it, and only the Compare runs stay.
            await page.Locator("#fSource").SelectOptionAsync("compare");
            await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("source=compare"));
            await Expect(page.Locator("tr[data-run-row]")).ToHaveCountAsync(2);
            await Expect(page.Locator($"tr#run-{playground}")).ToHaveCountAsync(0);
            await page.GotoAsync("/history");

            // The inspector, from the row's Inspect link, on each tab.
            await page.Locator($"tr#run-{playground} a[title='Inspect']").ClickAsync();
            await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/runs/" + playground + "$"));
            var inspector = page.Locator($"[data-run-inspector='{playground}']");
            await Expect(inspector).ToHaveAttributeAsync("data-state", "default");
            await Expect(inspector).ToContainTextAsync("history one");
            foreach (var tab in new[] { "request", "response", "sse", "events", "timing", "usage" })
            {
                await page.Locator($"[data-run-tab='{tab}']").ClickAsync();
                await Expect(page.Locator($"#run-pane-{tab}")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bactive\b"));
            }
            // Raw payloads are off by default, and the page says so instead of showing anything made up.
            await page.Locator("[data-run-tab='request']").ClickAsync();
            await Expect(page.Locator("[data-raw-none='request']")).ToContainTextAsync("not stored");

            // Bulk delete: select one run, confirm, and it is gone; the others stay.
            await page.GotoAsync("/history");
            await page.Locator($"tr#run-{playground} [data-run-select]").CheckAsync();
            var bar = page.Locator("[data-bulk-bar]");
            await Expect(bar).ToBeVisibleAsync();
            await Expect(bar.Locator("[data-bulk-count]")).ToHaveTextAsync("1");
            await bar.Locator("[data-bulk-delete]").ClickAsync();
            await page.ModalShownAsync("modal-confirm");
            await Expect(page.Locator("#modal-confirm")).ToContainTextAsync("Delete this run?");
            await page.Locator("#modal-confirm [data-confirm-ok]").ClickAsync();
            await page.ToastAsync("Run deleted");
            await Expect(page.Locator($"tr#run-{playground}")).ToHaveCountAsync(0);
            await Expect(page.Locator($"tr#run-{kept}")).ToBeVisibleAsync();
            Equal(404, (await app.Get("/api/runs/" + playground)).Status, "the deleted run is gone from runs.db");
            Equal(200, (await app.Get("/api/runs/" + kept)).Status, "the other run stays");
            foreach (var column in columns) Equal(200, (await app.Get("/api/runs/" + column)).Status, $"the Compare run {column} of group {group} stays");
        }),
    ];
}
