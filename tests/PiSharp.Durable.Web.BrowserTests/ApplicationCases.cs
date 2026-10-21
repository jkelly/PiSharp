using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0010, M2–M5: the hub's policy, budget, source, research and preference pages and the artifact cards' feedback controls, at
// desktop width and at 390 px.
internal static partial class Program
{
    private const string ClassifierSpec = """
        {"title":"Relevance","kind":"classifier","model":{"provider":"faux","id":"judge"},"instructions":"Is the release relevant to us?",
         "inputSchema":{"type":"object","additionalProperties":false,"required":["title"],"properties":{"title":{"type":"string","maxLength":200}}},
         "labels":[{"id":"relevant","description":"It matters"},{"id":"ignore","description":"It does not"}],
         "thresholds":{"auto":0.9,"confirm":0.5}}
        """;

    private const string ReleasesFeed = """
        <?xml version="1.0"?><rss version="2.0"><channel><title>Releases</title>
        <item><guid isPermaLink="false">r1</guid><title>Release 1.0</title><link>https://example.com/r/1</link><description>The first release.</description></item>
        <item><guid isPermaLink="false">r2</guid><title>Release 2.0</title><link>https://example.com/r/2</link><description>The second release.</description></item>
        </channel></rss>
        """;

    /// <summary>The page has finished its work: not busy, and showing the supported view without an error.</summary>
    private static async Task IdleAsync(IPage page)
    {
        await Expect(page.Locator("[data-automation]")).Not.ToHaveAttributeAsync("aria-busy", "true");
        await Expect(page.Locator("[data-supported]")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-error]")).ToBeHiddenAsync();
    }

    /// <summary>Clicks the page's Refresh until <paramref name="target"/> shows <paramref name="text"/>: these pages do not poll.</summary>
    private static async Task RefreshUntilAsync(IPage page, ILocator target, string text)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            await Expect(page.Locator("[data-automation]")).Not.ToHaveAttributeAsync("aria-busy", "true");
            if ((await target.AllTextContentsAsync()).Any(content => content.Contains(text, StringComparison.Ordinal))) return;
            await page.Locator("[data-refresh]").ClickAsync();
            await Task.Delay(250);
        }
        throw new TimeoutException($"The page never showed {text}.");
    }

    private static async Task ShotAsync(IPage page, string name)
    {
        if (Artifacts is not { } artifacts) return;
        Directory.CreateDirectory(artifacts);
        await page.ScreenshotAsync(new() { Path = Path.Join(artifacts, name + ".png"), FullPage = true });
    }

    /// <summary>A site on 127.0.0.1: an RSS feed for the sources page at <c>/feed.xml</c> and a page for research angles at <c>/notes</c>.</summary>
    private static async Task<(WebApplication App, string Origin)> SiteAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapGet("/feed.xml", () => Results.Text(ReleasesFeed, "application/rss+xml"));
        app.MapGet("/notes", () => Results.Text("<html><head><title>Upgrade notes</title></head><body><p>Version 2 costs nothing extra.</p></body></html>",
            "text/html"));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, address.TrimEnd('/'));
    }

    /// <summary>Runs research with two given angles through the hub's proxy and waits for it: two findings and the synthesis.</summary>
    private static async Task<long> ResearchRunAsync(Hub hub, string session)
    {
        var started = await hub.Post($"/api/devices/local/v1/sessions/{session}/research", new JsonObject
        {
            ["question"] = "Should we upgrade?",
            ["angles"] = new JsonArray(new JsonObject { ["title"] = "Cost" }, new JsonObject { ["title"] = "Risk" }),
        });
        var task = started.Body!["taskId"]!.GetValue<long>();
        await WaitUntilAsync(async () => (await hub.Get($"/api/devices/local/v1/sessions/{session}/executions/{task}")).Body?["run"]?["status"]?.GetValue<string>() == "completed",
            "the research completed");
        return task;
    }

    internal static IEnumerable<(string, Func<Task>)> ApplicationCases() =>
    [
        Browse("browser.applications.policies-publish-evaluate-and-decide", async (hub, window) =>
        {
            await hub.SessionAsync("judging");
            var page = window.Page;
            await page.GotoAsync("/policies");
            await IdleAsync(page);
            await Expect(page.Locator("[data-policies-empty]")).ToBeVisibleAsync();
            await Expect(page.Locator("[data-spec]")).Not.ToBeEmptyAsync();
            await page.Locator("[data-policy-id]").FillAsync("broken");
            await page.Locator("[data-spec]").FillAsync("""{"title":"Broken"}""");
            await page.GetByRole(AriaRole.Button, new() { Name = "Publish version" }).ClickAsync();
            await Expect(page.Locator("[data-error] code").First).ToBeVisibleAsync();

            await page.Locator("[data-policy-id]").FillAsync("relevance");
            await page.Locator("[data-spec]").FillAsync(ClassifierSpec);
            await page.GetByRole(AriaRole.Button, new() { Name = "Publish version" }).ClickAsync();
            await Expect(page.Locator("[data-published]")).ToHaveTextAsync("Published relevance v1.");
            await Expect(page.Locator("[data-error]")).ToBeHiddenAsync();
            await Expect(page.Locator("[data-policies] tr")).ToContainTextAsync("Relevance");
            await Expect(page.Locator("[data-policy-detail]")).ToContainTextAsync("Active version v1");

            await page.Locator("[data-evaluate-policy]").SelectOptionAsync("relevance");
            await page.Locator("[data-evaluate-title]").FillAsync("Release 2.0");
            await page.Locator("[data-evaluate-input]").FillAsync("""{"title":"Release 2.0"}""");
            await page.GetByRole(AriaRole.Button, new() { Name = "Evaluate", Exact = true }).ClickAsync();
            await Expect(page.Locator("[data-evaluated]")).ToContainTextAsync("Started run #");
            var row = page.Locator("[data-judgments] [data-judgment]");
            await RefreshUntilAsync(page, row, "pending");
            await Expect(row).ToContainTextAsync("relevant · 70%");
            await Expect(row).ToContainTextAsync("confirm");
            await row.Locator("[data-reason]").FillAsync("Ships our fix");
            await row.GetByRole(AriaRole.Button, new() { Name = "Approve", Exact = true }).ClickAsync();
            await Expect(row).ToContainTextAsync("approved");
            await Expect(row).ToContainTextAsync("Ships our fix");
            await Expect(page.Locator("[data-error]")).ToBeHiddenAsync();
            await ShotAsync(page, "policies-desktop");
        }),
        Browse("browser.applications.budgets-limits-and-reconciliation", async (hub, window) =>
        {
            hub.Device.Budgets.Reserve("browser-call", "manual/1", "faux", new Dictionary<string, long> { ["requests"] = 1 });
            hub.Device.Budgets.MarkUncertain("browser-call", "The test stopped it mid-call.");
            var page = window.Page;
            await page.GotoAsync("/budgets");
            await IdleAsync(page);
            var reservation = page.Locator("[data-reservations] [data-operation='browser-call']");
            await Expect(reservation).ToContainTextAsync("uncertain");
            await Expect(reservation).ToContainTextAsync("The test stopped it mid-call.");
            await page.Locator("[data-new-meter]").FillAsync("requests");
            await page.Locator("[data-new-limit]").FillAsync("50");
            await page.GetByRole(AriaRole.Button, new() { Name = "Save limits" }).ClickAsync();
            await IdleAsync(page);
            await Expect(page.Locator("[data-limit='requests']")).ToHaveValueAsync("50");
            await page.Locator("[data-new-meter]").FillAsync("money:USD");
            await page.Locator("[data-new-limit]").FillAsync("2.5");
            await page.GetByRole(AriaRole.Button, new() { Name = "Save limits" }).ClickAsync();
            await IdleAsync(page);
            await page.ReloadAsync();
            await IdleAsync(page);
            await Expect(page.Locator("[data-limit='requests']")).ToHaveValueAsync("50");
            await Expect(page.Locator("[data-limit='money:USD']")).ToHaveValueAsync("2.5");
            Equal(2_500_000L, hub.Device.Budgets.Limits().PerDay["money:USD"], "money is kept in micro-units");
            await ShotAsync(page, "budgets-desktop");
            page.Dialog += (_, dialog) => _ = dialog.AcceptAsync();
            await reservation.GetByRole(AriaRole.Button, new() { Name = "Not charged" }).ClickAsync();
            await Expect(page.Locator("[data-reservations-empty]")).ToBeVisibleAsync();
            Equal("released", hub.Device.Budgets.Get("browser-call")!.Status, "the call was reconciled as not charged");
            await Expect(page.Locator("[data-error]")).ToBeHiddenAsync();
        }),
        Browse("browser.applications.sources-add-check-edit-and-delete", async (hub, window) =>
        {
            var (site, origin) = await SiteAsync();
            await using var _ = site;
            var url = origin + "/feed.xml";
            await hub.SessionAsync("monitor");
            var page = window.Page;
            await page.GotoAsync("/sources");
            await IdleAsync(page);
            await Expect(page.Locator("[data-sources-empty]")).ToBeVisibleAsync();
            await page.Locator("[data-field='id']").FillAsync("releases");
            await page.Locator("[data-field='title']").FillAsync("Releases");
            await page.Locator("[data-field='url']").FillAsync(url);
            await page.Locator("[data-edit-submit]").ClickAsync();
            await Expect(page.Locator("[data-saved]")).ToHaveTextAsync("Added releases.");
            var row = page.Locator("[data-source-row='releases']");
            await Expect(row).ToContainTextAsync("Not checked yet");
            await Expect(page.Locator("[data-source-detail]")).ToContainTextAsync("No items yet. Run a check.");

            await page.Locator("#check-releases").CheckAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Start check" }).ClickAsync();
            await Expect(page.Locator("[data-checked]")).ToContainTextAsync("Started run #");
            await RefreshUntilAsync(page, row, "OK");
            await row.Locator("[data-source='releases']").ClickAsync();
            var detail = page.Locator("[data-source-detail]");
            await Expect(detail.GetByRole(AriaRole.Link, new() { Name = "Release 2.0" })).ToHaveAttributeAsync("href", "https://example.com/r/2");
            await Expect(detail.GetByRole(AriaRole.Link, new() { Name = "Release 2.0" })).ToHaveAttributeAsync("rel", "noopener noreferrer");
            await detail.Locator("summary").ClickAsync();
            await Expect(detail).ToContainTextAsync("fetched · HTTP 200");
            await Expect(page.Locator("[data-health-empty]")).ToBeVisibleAsync();
            await ShotAsync(page, "sources-desktop");

            await row.Locator("[data-edit-source='releases']").ClickAsync();
            await Expect(page.Locator("[data-field='id']")).ToHaveJSPropertyAsync("readOnly", true);
            await page.Locator("[data-field='title']").FillAsync("Release feed");
            await page.Locator("[data-edit-submit]").ClickAsync();
            await Expect(page.Locator("[data-saved]")).ToHaveTextAsync("Saved releases (revision 2).");
            await Expect(row).ToContainTextAsync("Release feed");
            await row.Locator("[data-toggle='releases']").ClickAsync();
            await Expect(row).ToContainTextAsync("disabled");
            await row.Locator("[data-toggle='releases']").ClickAsync();
            await Expect(row).Not.ToContainTextAsync("disabled");
            page.Dialog += (_, dialog) => _ = dialog.AcceptAsync();
            await row.Locator("[data-delete='releases']").ClickAsync();
            await Expect(page.Locator("[data-sources-empty]")).ToBeVisibleAsync();
            await Expect(page.Locator("[data-error]")).ToBeHiddenAsync();
        }),
        Browse("browser.applications.research-run-and-artifact-feedback", async (hub, window) =>
        {
            var (site, origin) = await SiteAsync();
            await using var _ = site;
            var session = await hub.SessionAsync("research");
            var page = window.Page;
            await page.GotoAsync("/research");
            await IdleAsync(page);
            await Expect(page.Locator("[data-sources-field]")).ToBeHiddenAsync();
            await Expect(page.Locator("[data-pages]")).ToHaveValueAsync("3");
            await page.Locator("[data-question]").FillAsync("Should we upgrade the framework?");
            await page.Locator("#angle-title-1").FillAsync("Cost");
            await page.Locator("#angle-prompt-1").FillAsync($"What does it cost? fetch: {origin}/notes");
            await page.Locator("#angle-title-2").FillAsync("Risk");
            await page.Locator("[data-pages]").FillAsync("1");
            await page.Locator("[data-origins]").FillAsync(origin);
            await page.GetByRole(AriaRole.Button, new() { Name = "Start research" }).ClickAsync();
            await Expect(page.Locator("[data-started]")).ToContainTextAsync("Started run #");
            var panel = page.Locator("[data-run]");
            await Expect(panel.Locator(".ps-chip").First).ToHaveTextAsync("completed", new() { Timeout = 30_000 });
            await Expect(panel.Locator("tbody tr")).ToHaveCountAsync(2);
            await Expect(panel.Locator(".d-grid > li")).ToHaveCountAsync(3);
            var cost = panel.Locator("tbody tr", new() { HasText = "Cost" });
            await Expect(cost).ToContainTextAsync("1 of 1");
            var fetched = cost.GetByRole(AriaRole.Link, new() { Name = new Uri(origin).Authority + "/notes" });
            await Expect(fetched).ToHaveAttributeAsync("href", origin + "/notes");
            await Expect(fetched).ToHaveAttributeAsync("rel", "noopener noreferrer");
            await Expect(panel.Locator("tbody tr", new() { HasText = "Risk" })).ToContainTextAsync("0 of 1");
            await Expect(page.Locator("[data-recent] tr")).ToHaveCountAsync(1);
            var task = long.Parse((await page.Locator("[data-started]").TextContentAsync())!.Split('#')[1].TrimEnd('.'), System.Globalization.CultureInfo.InvariantCulture);
            var execution = (await hub.Get($"/api/devices/local/v1/sessions/{session}/executions/{task}")).Body!;
            Equal(1, execution["run"]!["input"]!["retrieval"]!["maxFetches"]!.GetValue<int>(), "the page's fetch allowance reaches the run");
            Equal(origin, execution["run"]!["input"]!["retrieval"]!["origins"]![0]!.GetValue<string>(), "with the site it may browse");
            await ShotAsync(page, "research-desktop");

            await panel.GetByRole(AriaRole.Link, new() { Name = "Execution details" }).ClickAsync();
            await Expect(page.Locator("[data-automation]")).Not.ToHaveAttributeAsync("aria-busy", "true");
            var cards = page.Locator("[data-feedback]");
            await Expect(cards).ToHaveCountAsync(3);
            var card = cards.First;
            var state = card.Locator("[data-feedback-state]");
            await Expect(page.Locator(".artifact-evidence summary").First).ToHaveTextAsync("Evidence (1)");
            await card.GetByRole(AriaRole.Button, new() { Name = "More like this" }).ClickAsync();
            await Expect(state).ToHaveTextAsync("Saved: More like this · profile default r1");
            await Expect(card.GetByRole(AriaRole.Button, new() { Name = "More like this" })).ToHaveAttributeAsync("aria-pressed", "true");
            await card.Locator("[data-feedback-labels]").FillAsync("Upgrade, cost");
            await card.GetByRole(AriaRole.Button, new() { Name = "Label", Exact = true }).ClickAsync();
            await Expect(state).ToHaveTextAsync("Saved: More like this · labels cost, upgrade · profile default r2");
            await card.GetByRole(AriaRole.Button, new() { Name = "Undo" }).ClickAsync();
            await Expect(state).ToHaveTextAsync("Reverted to More like this · profile default r3");
            await card.Locator("[data-feedback-labels]").FillAsync(" , ");
            await card.GetByRole(AriaRole.Button, new() { Name = "Label", Exact = true }).ClickAsync();
            await Expect(state).ToHaveTextAsync("Name at least one label first.");
            await page.ReloadAsync();
            await Expect(page.Locator("[data-feedback]").First.Locator("[data-feedback-state]")).ToHaveTextAsync("Saved: More like this");
            await ShotAsync(page, "artifact-feedback-desktop");
        }),
        Browse("browser.applications.preferences-weights-examples-settings-and-reset", async (hub, window) =>
        {
            var session = await hub.SessionAsync("preferences");
            var task = await ResearchRunAsync(hub, session);
            var artifact = (await hub.Get($"/api/devices/local/v1/sessions/{session}/executions/{task}/artifacts")).Body!["items"]!.AsArray()[0]!["id"]!.GetValue<string>();
            var given = await hub.Post($"/api/devices/local/v1/sessions/{session}/artifacts/{artifact}/feedback",
                new JsonObject { ["requestId"] = "seed-1", ["action"] = "more", ["labels"] = new JsonArray("upgrade"), ["example"] = true });
            Equal(201, given.Status, "feedback: " + given.Text);
            var page = window.Page;
            await page.GotoAsync("/preferences");
            await IdleAsync(page);
            await Expect(page.Locator("[data-summary]")).ToContainTextAsync("revision 1");
            await Expect(page.Locator("[data-weights]")).ToContainTextAsync("upgrade");
            await Expect(page.Locator("[data-weights]")).ToContainTextAsync("+");
            await Expect(page.Locator("[data-examples] [data-example]")).ToHaveCountAsync(1);
            await Expect(page.Locator("[data-export]")).ToHaveAttributeAsync("download", "preferences-default.json");
            await ShotAsync(page, "preferences-desktop");
            await page.Locator("[data-setting='maxExamples']").FillAsync("5");
            await page.Locator("[data-settings]").GetByRole(AriaRole.Button).ClickAsync();
            await IdleAsync(page);
            await Expect(page.Locator("[data-summary]")).ToContainTextAsync("revision 2");
            await Expect(page.Locator("[data-setting='maxExamples']")).ToHaveValueAsync("5");
            await page.Locator("[data-forget]").ClickAsync();
            await Expect(page.Locator("[data-examples-empty]")).ToBeVisibleAsync();
            await Expect(page.Locator("[data-summary]")).ToContainTextAsync("revision 3");
            page.Dialog += (_, dialog) => _ = dialog.AcceptAsync();
            await page.Locator("[data-reset]").ClickAsync();
            await IdleAsync(page);
            await Expect(page.Locator("[data-weights]")).ToContainTextAsync("No weights yet.");
            await Expect(page.Locator("[data-error]")).ToBeHiddenAsync();
        }),
        Browse("browser.applications.an-older-device-shows-what-it-does-not-offer", async (hub, window) =>
        {
            var session = await hub.SessionAsync("older");
            var task = await ResearchRunAsync(hub, session);
            var page = window.Page;
            // A device from before decision 0010 reports no features. The hub must say what the device does not offer and call none of it.
            await page.RouteAsync(url => new Uri(url).AbsolutePath.EndsWith("/v1/device", StringComparison.Ordinal), async route =>
            {
                var response = await route.FetchAsync();
                var info = JsonNode.Parse(await response.TextAsync())!.AsObject();
                info.Remove("features");
                await route.FulfillAsync(new() { Response = response, Body = info.ToJsonString() });
            });
            var called = new ConcurrentQueue<string>();
            page.Request += (_, request) => called.Enqueue(new Uri(request.Url).AbsolutePath);
            foreach (var path in new[] { "/policies", "/budgets", "/sources", "/research", "/preferences" })
            {
                await page.GotoAsync(path);
                await Expect(page.Locator("[data-automation]")).Not.ToHaveAttributeAsync("aria-busy", "true");
                await Expect(page.Locator("[data-unsupported]")).ToBeVisibleAsync();
                await Expect(page.Locator("[data-unsupported]")).ToContainTextAsync("This device does not offer");
                await Expect(page.Locator("[data-supported]")).ToBeHiddenAsync();
                await Expect(page.Locator("[data-error]")).ToBeHiddenAsync();
                if (path == "/research") await ShotAsync(page, "older-device-research-desktop");
            }
            await page.GotoAsync($"/executions?device=local&session={session}&task={task}");
            await Expect(page.Locator("[data-detail] h2")).ToContainTextAsync($"#{task}");
            await Expect(page.Locator("[data-detail]")).ToContainTextAsync("Completion audit");
            await Expect(page.Locator("[data-artifacts]")).ToHaveCountAsync(0);
            await Expect(page.Locator("[data-feedback]")).ToHaveCountAsync(0);
            await Expect(page.Locator("[data-error]")).ToBeHiddenAsync();
            var offered = called.Where(path => Regex.IsMatch(path, "/v1/(policies|judgments|budgets|sources|retrieval|preferences)|/v1/sessions/[^/]+/research|/artifacts")).Distinct().ToList();
            Equal("", string.Join(", ", offered), "the hub called nothing the device does not offer");
        }),
        Browse("browser.applications.phone-layout", async (hub, window) =>
        {
            var session = await hub.SessionAsync("phone");
            Equal(201, (await hub.Post("/api/devices/local/v1/policies/relevance/versions", JsonNode.Parse(ClassifierSpec))).Status, "a policy");
            Equal(201, (await hub.Post("/api/devices/local/v1/sources", new JsonObject
            {
                ["id"] = "releases", ["kind"] = "feed", ["title"] = "Releases with a long title that has to wrap on a phone", ["url"] = "https://example.com/feed.xml",
            })).Status, "a source");
            var task = await ResearchRunAsync(hub, session);
            var page = window.Page;
            foreach (var path in new[] { "/policies", "/budgets", "/sources", $"/research?device=local&session={session}&task={task}", "/preferences",
                         $"/executions?device=local&session={session}&task={task}" })
            {
                await page.GotoAsync(path);
                await Expect(page.Locator("[data-automation]")).Not.ToHaveAttributeAsync("aria-busy", "true");
                await Expect(page.Locator("[data-error]")).ToBeHiddenAsync();
                var name = path.Split('?')[0][1..];
                if (name == "research") await Expect(page.Locator("[data-run] .ps-chip").First).ToHaveTextAsync("completed");
                if (name == "executions")
                {
                    var card = page.Locator("[data-feedback]").First;
                    await card.GetByRole(AriaRole.Button, new() { Name = "Dismiss" }).ClickAsync();
                    await Expect(card.Locator("[data-feedback-state]")).ToHaveTextAsync("Saved: Dismiss · profile default r1");
                }
                Check(!await page.OverflowsAsync(), "no sideways scroll on " + name);
                await ShotAsync(page, name + "-phone");
            }
        }, width: 390, height: 844),
    ];
}
