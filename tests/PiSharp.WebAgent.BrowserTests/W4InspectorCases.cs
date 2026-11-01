using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W4, fix 2, tests-ci): the Run inspector's script (inspector.js and history-actions.js) in a browser, on priced faux
// (--faux-metered), failing on any page or console error. A faux run sends no HTTP request, so it has no raw payloads: the Raw SSE tab and
// the HAR download say so (not stored), and Run JSON is the download it has. On such a run: Copy run id and Copy messages put the exact
// text on the clipboard; Collapse deltas switches the normalized events between one summary row and every delta; Download › Run JSON saves
// <id>.json with the run's request, output and events; Re-run sends the stored request again as a new run and opens its inspector; Delete
// asks, then goes back to History without the run. The raw payload tabs (the Raw SSE filter, its event-family toggles and Wrap, Show
// response and the HAR) need a provider's HTTP request, which faux never makes (RunEngine stores no capture for the faux API), so they are
// not covered here.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> W4InspectorCases() =>
    [
        Browse("browser.w4.inspector.copy-collapse-download-rerun-and-delete-a-faux-run", AgentMode.FauxMetered, async (app, window) =>
        {
            await window.Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);
            var prompt = "slow: " + Words(24);
            var id = await app.PlaygroundRunAsync(prompt, system: "Answer in one line.");
            var page = window.Page;
            await page.GotoAsync("/runs/" + id);
            var inspector = page.Locator($"[data-run-inspector='{id}']");
            await Expect(inspector).ToHaveAttributeAsync("data-state", "default");

            // Copy: the run id, and the stored messages.
            await inspector.Locator($"[data-run-header] [data-copy='{id}']").ClickAsync();
            await page.ToastAsync("Copied");
            Equal(id, await page.EvaluateAsync<string>("() => navigator.clipboard.readText()"), "the copied run id");
            await inspector.Locator("[data-copy='#runMessages']").ClickAsync();
            await WaitUntilAsync(async () => (await page.EvaluateAsync<string>("() => navigator.clipboard.readText()")).Contains("w024", StringComparison.Ordinal),
                "the messages are on the clipboard");
            Equal((await page.Locator("#runMessages").TextContentAsync())!.Trim(), (await page.EvaluateAsync<string>("() => navigator.clipboard.readText()")).Trim(),
                "the copied messages are the pane's text");

            // A faux run has no raw payloads, and the raw tabs say so.
            await page.Locator("[data-run-tab='sse']").ClickAsync();
            await Expect(page.Locator("[data-raw-none='sse']")).ToContainTextAsync("not stored");
            await Expect(page.Locator("[data-raw-none='sse']")).ToContainTextAsync("offline faux model");
            await Expect(page.Locator("[data-sse-filter]")).ToHaveCountAsync(0);

            // Collapse deltas: one summary row, or every delta.
            await page.Locator("[data-run-tab='events']").ClickAsync();
            var events = page.Locator("[data-run-events]");
            var toggle = page.Locator("[data-events-collapse]");
            await Expect(toggle).ToBeCheckedAsync();
            await Expect(events).ToHaveAttributeAsync("data-collapsed", "true");
            var extra = events.Locator(".sse-row[data-extra='true']");
            var summary = events.Locator(".sse-row[data-summary='true']");
            Check(await extra.CountAsync() > 1, "the slow answer streamed several deltas");
            await Expect(extra.First).ToBeHiddenAsync();
            await Expect(summary.First).ToBeVisibleAsync();
            await toggle.UncheckAsync();
            await Expect(events).ToHaveAttributeAsync("data-collapsed", "false");
            await Expect(extra.First).ToBeVisibleAsync();
            await Expect(summary.First).ToBeHiddenAsync();
            await toggle.CheckAsync();
            await Expect(extra.First).ToBeHiddenAsync();

            // Download: no HAR without raw payloads; Run JSON has the request, the output and the events.
            var download = page.Locator("[data-run-header] .dropdown:has([data-run-download]) > [data-bs-toggle='dropdown']");
            await download.ClickAsync();
            await Expect(page.Locator("[data-run-header] .dropdown-item.disabled", new() { HasText = "HAR" })).ToContainTextAsync("not stored");
            await Expect(page.Locator($"[data-run-download='{id}.har']")).ToHaveCountAsync(0);
            var json = await page.RunAndWaitForDownloadAsync(() => page.Locator($"[data-run-download='{id}.json']").ClickAsync());
            Equal(id + ".json", json.SuggestedFilename, "the Run JSON file name");
            var saved = Path.Join(app.Root, "run.json");
            await json.SaveAsAsync(saved);
            var run = JsonNode.Parse(await File.ReadAllTextAsync(saved))!;
            var text = run.ToJsonString();
            Check(text.Contains(id, StringComparison.Ordinal), "the file is this run's: " + text[..Math.Min(300, text.Length)]);
            Check(text.Contains("Answer in one line.", StringComparison.Ordinal) && text.Contains("w024", StringComparison.Ordinal), "with its request and output");
            Check(text.Contains("text_delta", StringComparison.Ordinal), "and its events");

            // Re-run: the stored request again, as a new run whose inspector opens.
            await inspector.Locator($"[data-run-rerun='{id}']").ClickAsync();
            await Expect(page).Not.ToHaveURLAsync(new Regex("/runs/" + id + "$"), new() { Timeout = 15_000 });
            await Expect(page).ToHaveURLAsync(new Regex(@"/runs/r[0-9a-f]+$"));
            await page.ToastAsync("Re-run started");
            var second = new Uri(page.Url).AbsolutePath["/runs/".Length..];
            Check(second != id, "a new run");
            Equal("completed", await app.RunEndedAsync(second), "the re-run completed");
            await Expect(page.Locator($"[data-run-inspector='{second}']")).ToBeVisibleAsync();
            await page.ReloadAsync();
            await Expect(page.Locator($"[data-run-inspector='{second}']")).ToHaveAttributeAsync("data-state", "default");
            await Expect(page.Locator("#runMessages")).ToContainTextAsync("w024");
            await Expect(page.Locator("#runMessages")).ToContainTextAsync("Answer in one line.");

            // Delete from the inspector: a confirm, then History without the run.
            await page.Locator($"[data-run-header] [data-run-delete='{second}']").ClickAsync();
            await page.ModalShownAsync("modal-confirm");
            await Expect(page.Locator("#modal-confirm")).ToContainTextAsync("Delete this run?");
            await page.Locator("#modal-confirm [data-confirm-ok]").ClickAsync();
            await Expect(page).ToHaveURLAsync(new Regex(@"/history$"));
            await page.ToastAsync("Run deleted");
            await Expect(page.Locator($"tr#run-{second}")).ToHaveCountAsync(0);
            await Expect(page.Locator($"tr#run-{id}")).ToBeVisibleAsync();
            Equal(404, (await app.Get("/api/runs/" + second)).Status, "the re-run is gone");
        }),
    ];
}
