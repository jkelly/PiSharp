using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

internal static partial class Program
{
    // The inspector: Session (model, thinking, context bar), State, Tasks, Tools and Extend (PLAN.md Appendix C items 10-15 and 17).
    internal static IEnumerable<(string, Func<Task>)> InspectorCases() =>
    [
        // Items 10 and 17: Provider -> Model, the Tools tab's signatures, the usage table, and a thinking level that persists.
        Browse("browser.model-picker-tools-and-thinking", async (hub, window) =>
        {
            var page = window.Page;
            var session = await hub.SessionAsync("models");
            await page.OpenAgentAsync(session);
            await Expect(page.Locator(".model-button .mb-name")).ToHaveTextAsync("Echo");
            await page.Locator("[data-action='pick-model']").ClickAsync();
            var dialog = page.Locator("#modal-model-picker");
            await Expect(dialog).ToBeVisibleAsync();
            await Expect(page.Locator("#mp-provider")).ToHaveValueAsync("faux");
            await Expect(page.Locator("#mp-model")).ToHaveValueAsync("echo");
            await page.Locator("#mp-model").SelectOptionAsync("echo-small");
            await Expect(page.Locator("[data-mp-model-line]")).ToContainTextAsync("40K context");
            await page.Locator("#mp-form button[type='submit']").ClickAsync();
            await Expect(dialog).ToBeHiddenAsync();
            await page.ToastAsync("Model saved: Echo small. Thinking");
            await Expect(page.Locator(".model-button .mb-name")).ToHaveTextAsync("Echo small");

            await page.TabAsync("tools");
            var tool = page.Locator("details[data-tool='start_session']");
            await tool.Locator("summary").ClickAsync();
            await Expect(tool.Locator("pre.signature")).ToContainTextAsync("function start_session(args: {");

            await page.TabAsync("session");
            await Expect(page.Locator(".usage-table tbody tr")).ToHaveCountAsync(4);
            await Expect(page.Locator(".usage-table [data-stat='input']")).ToHaveTextAsync("0");
            await page.Locator("#thinkingLevel").SelectOptionAsync("high");
            await page.ToastAsync("Thinking high");
            await page.ReloadAsync();
            await page.AgentReadyAsync();
            await Expect(page.Locator("#thinkingLevel")).ToHaveValueAsync("high");
            await Expect(page.Locator(".model-button .mb-name")).ToHaveTextAsync("Echo small");
        }),

        // Item 11.
        Browse("browser.read-only", async (hub, window) =>
        {
            var page = window.Page;
            var session = await hub.SessionAsync("read-only");
            await page.OpenAgentAsync(session);
            await page.TabAsync("tools");
            var disabled = page.Locator("details[data-tool='bash'] .ps-chip").Filter(new() { HasText = "disabled" });
            await Expect(disabled).ToHaveCountAsync(0);
            await page.Locator("[data-action-change='read-only']").CheckAsync();
            await page.ToastAsync("Read only on");
            await Expect(disabled).ToBeVisibleAsync();
            await page.SendAsync("!pwd");
            await Expect(page.Thread()).ToContainTextAsync("Tool bash is not available");

            await page.Locator("[data-action-change='read-only']").UncheckAsync();
            await page.ToastAsync("Read only off");
            await Expect(disabled).ToHaveCountAsync(0);
            await page.SendAsync("!echo writes-again");
            await Expect(page.Thread()).ToContainTextAsync("Tool finished: bash: writes-again");
        }),

        // Item 12: two extension cards; the command guard blocks rm -rf; a prompt checkbox persists after a reload.
        Browse("browser.extend-guard-and-prompts", async (hub, window) =>
        {
            var page = window.Page;
            var session = await hub.SessionAsync("extend");
            await page.OpenAgentAsync(session);
            await page.TabAsync("extend");
            await Expect(page.Locator(".ext-card")).ToHaveCountAsync(2);
            var guard = page.Locator("input[data-extension='command-guard']");
            await guard.CheckAsync();
            await page.ToastAsync("on for");
            await Expect(page.Locator(".ext-card[data-extension='command-guard']")).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\boff\b"));
            await page.SendAsync("!rm -rf ./nothing-here");
            await Expect(page.Thread()).ToContainTextAsync("Blocked by the command guard");

            var prompt = page.Locator($"input[data-prompt='{Hub.PromptFile}']");
            await Expect(prompt).Not.ToBeCheckedAsync();
            await prompt.CheckAsync();
            await page.ToastAsync(Hub.PromptFile + " checked");
            await page.ReloadAsync();
            await page.AgentReadyAsync();
            await page.TabAsync("extend");
            await Expect(prompt).ToBeCheckedAsync();
            await Expect(guard).ToBeCheckedAsync();
        }),

        // Item 13: Increment /counter, Save JSON, a change made elsewhere, and Load latest.
        Browse("browser.app-state", async (hub, window) =>
        {
            var page = window.Page;
            var session = await hub.SessionAsync("state");
            await page.OpenAgentAsync(session);
            await page.TabAsync("state");
            var editor = page.Locator("[data-state-tab]");
            var add = editor.Locator("[data-state-fields] > .sf-add");
            await Expect(add).ToBeVisibleAsync();
            await add.Locator("input").FillAsync("counter");
            await add.Locator("select").SelectOptionAsync("number");
            await add.Locator("[data-sf='add-field']").ClickAsync();
            var counter = editor.Locator("[aria-label='Value of /counter']");
            await Expect(counter).ToHaveValueAsync("0");
            await editor.Locator("[aria-label='Increment /counter']").ClickAsync();
            await Expect(counter).ToHaveValueAsync("1");

            var json = page.Locator("#stateJson");
            await Expect(json).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("\"counter\": 1"));
            await json.FillAsync("{\n  \"counter\": 1,\n  \"note\": \"from the editor\"\n}");
            await Expect(editor.Locator("[data-state-dirty]")).ToBeVisibleAsync();
            await Expect(counter).ToBeDisabledAsync();
            await editor.Locator("[data-action='state-save']").ClickAsync();
            await page.ToastAsync("Shared App State saved");
            await Expect(editor.Locator("[aria-label='Value of /note']")).ToHaveValueAsync("from the editor");
            await Expect(editor.Locator("[data-state-dirty]")).ToBeHiddenAsync();

            await json.FillAsync("{\n  \"counter\": 1,\n  \"note\": \"edited here\"\n}");
            var outside = await hub.Post($"/api/devices/local/v1/sessions/{session}/agents/1/state",
                new JsonObject { ["operation"] = "increment", ["path"] = "/counter", ["value"] = 1 });
            Check(outside.Status is 200, "a change made elsewhere: " + outside.Text);
            var conflict = editor.Locator("[data-state-conflict]");
            await Expect(conflict).ToBeVisibleAsync();
            await Expect(conflict).ToContainTextAsync("State changed elsewhere");
            await Expect(editor.Locator("[data-action='state-save']")).ToBeDisabledAsync();
            await editor.Locator("[data-action='state-load']").ClickAsync();
            await page.ToastAsync("Loaded revision");
            await Expect(conflict).ToBeHiddenAsync();
            await Expect(json).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("\"counter\": 2"));
            await Expect(json).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("from the editor"));
            await Expect(counter).ToHaveValueAsync("2");
        }),

        // Item 14: Workspace snapshot completes its 4 phases; Change watch stops; Release check waits and is approved after a reload; a run the
        // agent starts says so; Stop all clears the tasks.
        Browse("browser.jobs", async (hub, window) =>
        {
            var page = window.Page;
            var session = await hub.SessionAsync("jobs");
            await page.OpenAgentAsync(session);
            await page.TabAsync("tasks");
            await page.Locator("article.job-def[data-job='workspace-snapshot'] [data-action='run-job']").ClickAsync();
            await page.ToastAsync("Workspace snapshot started");
            var snapshot = page.JobCard("Workspace snapshot");
            await Expect(snapshot.Locator(".job-head .ps-chip")).ToHaveTextAsync("Completed", new() { Timeout = 60_000 });
            await Expect(snapshot.Locator(".phase-chip.done")).ToHaveCountAsync(4);
            await Expect(snapshot).ToContainTextAsync("started by you");
            await Expect(page.Locator("article.job-def[data-job='workspace-snapshot'] .job-def-run")).ToContainTextAsync("Completed");

            await page.Locator("article.job-def[data-job='change-watch'] [data-action='run-job']").ClickAsync();
            var watch = page.JobCard("Change watch");
            await Expect(watch.Locator(".job-head .ps-chip")).ToHaveTextAsync("Running", new() { Timeout = 30_000 });
            await watch.Locator("[data-action='stop-task']").ClickAsync();
            await page.ToastAsync("Stopped");
            await Expect(watch.Locator(".job-head .ps-chip")).ToHaveTextAsync("Stopped", new() { Timeout = 30_000 });

            await page.Locator("article.job-def[data-job='release-check'] [data-action='run-job']").ClickAsync();
            var release = page.JobCard("Release check");
            await Expect(release.Locator(".job-head .ps-chip")).ToHaveTextAsync("Waiting for you", new() { Timeout = 90_000 });
            await Expect(page.Locator(".pending-panel")).ToContainTextAsync("Release check");
            await page.ReloadAsync();
            await page.AgentReadyAsync();
            await Expect(release.Locator(".job-head .ps-chip")).ToHaveTextAsync("Waiting for you");
            await release.Locator(".job-approval [data-action='approve-job'][data-approved='true']").ClickAsync();
            await page.ToastAsync("Approved");
            await Expect(release.Locator(".job-head .ps-chip")).ToHaveTextAsync("Completed", new() { Timeout = 60_000 });
            await Expect(page.Locator(".pending-panel")).ToHaveCountAsync(0);

            // durable-pi shows an agent-started run's card "started by the agent". Here the run_durable_job tool appends no anchor entry (only
            // HostedSession.RunJobAsync does), so the run shows in its tool result and the Jobs tab; decision 0008 lists the missing card.
            await page.SendAsync("""tool: run_durable_job {"job":"workspace-snapshot"}""");
            await Expect(page.Thread()).ToContainTextAsync("Tool finished: run_durable_job: Started Workspace snapshot");
            var activity = page.Locator("[data-activity-count]");
            await Expect(activity).ToHaveTextAsync("Nothing running", new() { Timeout = 30_000 });
            await page.TabAsync("session");
            await page.TabAsync("tasks");
            await Expect(page.Locator("article.job-def[data-job='workspace-snapshot'] .job-def-run")).ToContainTextAsync("Run #2: Completed");

            await page.TabAsync("tasks");
            await page.Locator("article.job-def[data-job='change-watch'] [data-action='run-job']").ClickAsync();
            await Expect(activity).Not.ToHaveTextAsync("Nothing running", new() { Timeout = 30_000 });
            await page.Locator("[data-action='stop-all']").ClickAsync();
            await page.ToastAsync("including background jobs");
            await Expect(activity).ToHaveTextAsync("Nothing running", new() { Timeout = 30_000 });
            await Expect(page.Locator("[data-live-panel='tasks']")).ToContainTextAsync("Nothing running");
        }),

        // Item 15: the auto marker sits where the window puts it, ArrowLeft moves it 1024 tokens, it persists, and Details toggles automatic
        // compaction.
        Browse("browser.context-bar", async (hub, window) =>
        {
            var page = window.Page;
            var session = await hub.SessionAsync("context");
            await page.OpenAgentAsync(session);
            var tab = page.Locator("[data-session-tab]");
            int Attribute(string? value) => int.Parse(value!, CultureInfo.InvariantCulture);
            var size = Attribute(await tab.GetAttributeAsync("data-window"));
            var reserve = Attribute(await tab.GetAttributeAsync("data-reserve"));
            var background = Attribute(await tab.GetAttributeAsync("data-background"));
            Equal(128_000, size, "faux/echo's window");
            Check(background > 0, "automatic compaction starts in the background before the wait");
            var auto = size - reserve - background;
            var marker = page.Locator(".ctx-marker.auto");
            await Expect(marker).ToHaveAttributeAsync("aria-valuenow", auto.ToString(CultureInfo.InvariantCulture));
            await Expect(page.Locator(".ctx-marker.wait")).ToHaveAttributeAsync("aria-valuenow", (size - reserve).ToString(CultureInfo.InvariantCulture));

            await marker.FocusAsync();
            await page.Keyboard.PressAsync("ArrowLeft");
            var moved = (auto - 1024).ToString(CultureInfo.InvariantCulture);
            await Expect(marker).ToHaveAttributeAsync("aria-valuenow", moved);
            await page.ToastAsync("Compaction settings saved for this session");
            await page.ReloadAsync();
            await page.AgentReadyAsync();
            await Expect(page.Locator(".ctx-marker.auto")).ToHaveAttributeAsync("aria-valuenow", moved);

            var enabled = await tab.GetAttributeAsync("data-enabled") == "true";
            await page.Locator("[data-action='compaction-details']").ClickAsync();
            var dialog = page.Locator("#modal-compaction");
            await Expect(dialog).ToBeVisibleAsync();
            var toggle = page.Locator("#cmp-auto");
            if (enabled) await Expect(toggle).ToBeCheckedAsync(); else await Expect(toggle).Not.ToBeCheckedAsync();
            await toggle.SetCheckedAsync(!enabled);
            await page.ToastAsync(enabled ? "Automatic compaction off." : "Automatic compaction on.");
            await Expect(tab).ToHaveAttributeAsync("data-enabled", enabled ? "false" : "true");
            await dialog.Locator(".modal-footer [data-bs-dismiss='modal']").ClickAsync();
            await Expect(dialog).ToBeHiddenAsync();
        }),
    ];
}
