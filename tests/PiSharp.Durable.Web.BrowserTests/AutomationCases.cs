using System.Text.Json.Nodes;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> AutomationCases() =>
    [
        Browse("browser.automation.schedules-and-scoped-token-management", async (hub, window) =>
        {
            var session = await hub.SessionAsync("recurring");
            var page = window.Page;
            await page.GotoAsync("/schedules");
            await Expect(page.Locator("[data-session] option")).ToHaveCountAsync(1);
            await Expect(page.Locator("[data-session]")).ToHaveValueAsync(session);
            await Expect(page.Locator("[data-create-button]")).ToBeEnabledAsync();
            await page.Locator("[data-job]").SelectOptionAsync("workspace-snapshot");
            await page.Locator("[data-interval]").FillAsync("1");
            await page.Locator("[data-create-button]").ClickAsync();
            var row = page.Locator("[data-schedules] tr");
            await Expect(row).ToHaveCountAsync(1);
            await Expect(row).ToContainTextAsync("Enabled");
            await row.GetByRole(AriaRole.Button, new() { Name = "Pause", Exact = true }).ClickAsync();
            await Expect(row).ToContainTextAsync("Paused");
            await row.GetByRole(AriaRole.Button, new() { Name = "Resume", Exact = true }).ClickAsync();
            await Expect(row).ToContainTextAsync("Enabled");
            await row.Locator("[data-row-interval]").FillAsync("2");
            await row.GetByRole(AriaRole.Button, new() { Name = "Update", Exact = true }).ClickAsync();
            await page.ReloadAsync();
            await Expect(row.Locator("[data-row-interval]")).ToHaveValueAsync("2");
            if (Artifacts is { } artifacts)
            {
                Directory.CreateDirectory(artifacts);
                await page.ScreenshotAsync(new() { Path = Path.Join(artifacts, "schedules-desktop.png"), FullPage = true });
            }
            await page.Locator("[data-token-label]").FillAsync("Build bot");
            await page.Locator("[data-token-hours]").FillAsync("1");
            await page.GetByRole(AriaRole.Button, new() { Name = "Issue token" }).ClickAsync();
            await Expect(page.Locator("[data-issued]")).ToBeVisibleAsync();
            var secret = await page.Locator("[data-issued-token]").TextContentAsync();
            Check(secret?.StartsWith("dpi_trigger_", StringComparison.Ordinal) == true, "a scoped token was returned once");
            var response = await page.APIRequest.GetAsync("/api/devices/local/v1/trigger-credentials");
            Check(!(await response.TextAsync()).Contains(secret!, StringComparison.Ordinal), "listing contains no token secret");
            await page.ReloadAsync();
            await Expect(page.Locator("[data-credentials]")).ToContainTextAsync("Build bot");
            await Expect(page.Locator("[data-issued]")).ToBeHiddenAsync();
            await page.Locator("[data-revoke]").ClickAsync();
            await Expect(page.Locator("[data-credentials] tr")).ToHaveCountAsync(0);
            page.Dialog += (_, dialog) => _ = dialog.AcceptAsync();
            await row.GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
            await Expect(row).ToHaveCountAsync(0);
            await Expect(page.Locator("[data-schedules-empty]")).ToBeVisibleAsync();
            await Expect(page.Locator("[data-error]")).ToBeHiddenAsync();
        }),
        Browse("browser.automation.execution-history-and-audit-details", async (hub, window) =>
        {
            var session = await hub.SessionAsync("history");
            var started = await hub.Post($"/api/devices/local/v1/sessions/{session}/agents/1/jobs", new JsonObject { ["job"] = "workspace-snapshot" });
            var task = started.Body!["taskId"]!.GetValue<long>();
            var page = window.Page;
            await page.GotoAsync($"/executions?device=local&session={session}&task={task}");
            await Expect(page.Locator("[data-detail]")).ToContainTextAsync("Completion audit: passed");
            await Expect(page.Locator("[data-detail]")).ToContainTextAsync("saved-result: passed");
            await Expect(page.Locator("[data-executions]")).ToContainTextAsync("Workspace snapshot");
            await Expect(page.Locator("[data-executions]")).ToContainTextAsync("completed");
            // The detail renders before its artifact cards load; the page is inert until both are done.
            await Expect(page.Locator("[data-automation]")).Not.ToHaveAttributeAsync("aria-busy", "true");
            await page.Locator("[data-query]").FillAsync("nothing-matches-this");
            await page.GetByRole(AriaRole.Button, new() { Name = "Refresh", Exact = true }).ClickAsync();
            await Expect(page.Locator("[data-history-empty]")).ToBeVisibleAsync();
            await Expect(page.Locator("[data-automation]")).Not.ToHaveAttributeAsync("aria-busy", "true");
            await page.Locator("[data-query]").FillAsync("");
            await page.Locator("[data-kind]").SelectOptionAsync("job");
            await page.Locator("[data-status]").SelectOptionAsync("completed");
            await page.GetByRole(AriaRole.Button, new() { Name = "Refresh", Exact = true }).ClickAsync();
            await Expect(page.Locator("[data-executions] tr")).ToHaveCountAsync(1);
            await page.ReloadAsync();
            await Expect(page.Locator("[data-detail]")).ToContainTextAsync("Completion audit: passed");
            await Expect(page.Locator("[data-error]")).ToBeHiddenAsync();
            if (Artifacts is { } artifacts)
            {
                Directory.CreateDirectory(artifacts);
                await page.ScreenshotAsync(new() { Path = Path.Join(artifacts, "executions-desktop.png"), FullPage = true });
            }
        }),
        Browse("browser.automation.phone-layout", async (hub, window) =>
        {
            var session = await hub.SessionAsync("phone");
            await hub.Post("/api/devices/local/v1/schedules", new JsonObject { ["sessionId"] = session, ["jobId"] = "workspace-snapshot", ["everyMinutes"] = 60 });
            var started = await hub.Post($"/api/devices/local/v1/sessions/{session}/agents/1/jobs", new JsonObject { ["job"] = "workspace-snapshot" });
            var task = started.Body!["taskId"]!.GetValue<long>();
            foreach (var path in new[] { "/schedules", "/executions" })
            {
                await window.Page.GotoAsync(path == "/executions" ? $"{path}?device=local&session={session}&task={task}" : path);
                await Expect(window.Page.Locator("[data-session] option")).ToHaveCountAsync(1);
                if (path == "/executions") await Expect(window.Page.Locator("[data-detail]")).ToContainTextAsync("Completion audit: passed");
                else await Expect(window.Page.Locator("[data-schedules] tr")).ToHaveCountAsync(1);
                await window.Page.NoSidewaysScrollAsync("no sideways scroll on " + path);
                await Expect(window.Page.Locator("[data-error]")).ToBeHiddenAsync();
                if (Artifacts is { } artifacts)
                {
                    Directory.CreateDirectory(artifacts);
                    await window.Page.ScreenshotAsync(new() { Path = Path.Join(artifacts, path[1..] + "-phone.png"), FullPage = true });
                }
            }
        }, width: 390, height: 844),
    ];
}
