using System.Collections.Concurrent;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W3, T3): the first-run Setup wizard end to end, on a live app (no --faux) with nothing connected. The app's environment
// has no provider key, token, cloud setting or proxy setting of this machine, and every proxy is a closed port on loopback, so nothing
// leaves this computer; the routing's catalog is offline too. Opening / lands on /setup; Welcome leads to Connect, where Continue waits for a
// provider; a planted OpenAI key (a made-up value) is pasted and saved, which connects OpenAI; Default model saves the defaults; Done sums
// it up; Start chatting opens a new chat, and / no longer sends the user to setup. The key is posted once, in the body of the save, and is
// never in the DOM, a field after the save, a URL, the data folder in plain text or the app's log.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> W3SetupCases() =>
    [
        BrowseW3("browser.w3.setup.runs-end-to-end", W3Mode.Live, async (app, window) =>
        {
            var secret = "sk-proj-PLANTED" + Guid.NewGuid().ToString("N") + "Hk7Wq3";
            var page = window.Page;
            var requests = new ConcurrentBag<IRequest>();
            page.Request += (_, request) => requests.Add(request);

            var before = await app.Get("/api/setup");
            Equal(200, before.Status, "/api/setup: " + before.Text);
            Equal(true, before.Body?["redirectsToSetup"]?.GetValue<bool>(), "a live app with nothing connected sends the user to setup: " + before.Text);
            Equal(0, before.Body?["connected"]?.AsArray().Count, "nothing of this machine is connected: " + before.Text);

            // / lands on the wizard's Welcome step.
            await page.GotoAsync("/");
            Equal("/setup", new Uri(page.Url).AbsolutePath, "/ opens setup on the first run");
            var wizard = page.Locator("[data-setup-wizard]");
            await Expect(wizard).ToHaveAttributeAsync("data-setup-state", "nothing");
            await Expect(wizard).ToHaveAttributeAsync("data-setup-current", "welcome");
            await Expect(page.Locator("section[data-setup-step='welcome']")).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Get started" }).ClickAsync();

            // Connect: Continue waits for a provider.
            await Expect(wizard).ToHaveAttributeAsync("data-setup-current", "connect");
            await Expect(page.Locator("section[data-setup-step='connect']")).ToBeVisibleAsync();
            await Expect(page.Locator("section[data-setup-step='welcome']")).ToBeHiddenAsync();
            await Expect(page.Locator("[data-setup-continue-blocked]")).ToBeVisibleAsync();
            await Expect(page.Locator("[data-setup-continue-blocked] button")).ToBeDisabledAsync();
            await Expect(page.Locator("[data-setup-nothing]")).ToBeVisibleAsync();
            Check(new Uri(page.Url).Query.Contains("step=connect", StringComparison.Ordinal), "the step is in the address: " + page.Url);

            var form = page.Locator("[data-setup-key-form]");
            await form.Locator("[data-setup-key-provider]").SelectOptionAsync("openai");
            var key = form.Locator("[data-setup-key-input]");
            await Expect(key).ToBeVisibleAsync();
            await Expect(key).ToHaveAttributeAsync("type", "password");
            await key.FillAsync(secret);
            await form.Locator("[data-setup-key-save]").ClickAsync();
            await page.ToastAsync("Credential saved");
            await Expect(page.Locator("[data-setup-connected='openai']")).ToBeVisibleAsync();
            await Expect(wizard).ToHaveAttributeAsync("data-setup-current", "connect");
            await Expect(page.Locator("[data-setup-nothing]")).ToHaveCountAsync(0);
            await Expect(page.Locator("[data-setup-continue-blocked]")).ToHaveCountAsync(0);
            await Expect(page.Locator("[data-setup-key-input]")).ToHaveValueAsync("");
            await NoSecretInPageAsync(page, secret, "Setup › Connect after saving the key");

            // Default model: the connected provider's model, saved as the default.
            await page.Locator("[data-setup-footer]").GetByRole(AriaRole.Button, new() { Name = "Continue" }).ClickAsync();
            await Expect(wizard).ToHaveAttributeAsync("data-setup-current", "model");
            var model = page.Locator("section[data-setup-step='model']");
            await Expect(model).ToBeVisibleAsync();
            await Expect(model.Locator("[data-setup-model]")).ToBeVisibleAsync();
            await Expect(page.Locator("#setupUseDefault")).ToBeCheckedAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Save and continue" }).ClickAsync();

            // Done.
            await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/setup" && new Uri(url).Query.Contains("step=done", StringComparison.Ordinal));
            await page.ToastAsync("Defaults saved");
            await Expect(wizard).ToHaveAttributeAsync("data-setup-current", "done");
            await Expect(wizard).ToHaveAttributeAsync("data-setup-finished", "true");
            var done = page.Locator("section[data-setup-step='done']");
            await Expect(done).ToBeVisibleAsync();
            await Expect(done.Locator("[data-setup-summary-providers]")).ToContainTextAsync("openai");
            await Expect(done.Locator("[data-setup-summary-model]")).ToContainTextAsync("openai/");
            await Expect(done.Locator("[data-setup-summary-workspace]")).ToContainTextAsync(app.Work);
            await Expect(done.Locator("[data-setup-summary-data]")).ToContainTextAsync(app.Data);

            var status = await app.Get("/api/setup");
            Equal("finished", status.Body?["outcome"]?.ToString().ToLowerInvariant(), "the wizard is finished: " + status.Text);
            Equal(false, status.Body?["redirectsToSetup"]?.GetValue<bool>(), "setup is no longer forced");
            Check(status.Body?["connected"]?.AsArray().Select(id => id?.ToString()).Contains("openai") == true, "OpenAI is connected: " + status.Text);
            var defaults = await app.Get("/api/settings/defaults");
            Equal("openai", defaults.Body?["provider"]?.ToString(), "the default provider: " + defaults.Text);
            Check(!string.IsNullOrEmpty(defaults.Body?["model"]?.ToString()), "a default model was saved: " + defaults.Text);
            await Expect(done.Locator("[data-setup-summary-model]")).ToContainTextAsync("openai/" + defaults.Body!["model"]);

            // Start chatting; / no longer sends the user to setup.
            await done.GetByRole(AriaRole.Link, new() { Name = "Start chatting" }).ClickAsync();
            await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/chat/new");
            // The new chat starts on the saved default.
            await Expect(page.Locator("#chatLayout[data-chat-new]")).ToHaveAttributeAsync("data-provider", "openai");
            await Expect(page.Locator("#chatLayout[data-chat-new]")).ToHaveAttributeAsync("data-model", defaults.Body!["model"]!.ToString());
            await Expect(page.Locator("[data-composer-input]")).ToBeVisibleAsync();
            await page.GotoAsync("/");
            Check(new Uri(page.Url).AbsolutePath != "/setup", "/ opens the app now: " + page.Url);
            await NoSecretInPageAsync(page, secret, "the app after setup");

            // The key went to the server once, in the body of the save, and is kept encrypted.
            var sent = requests.Where(request => (request.PostData ?? "").Contains(secret, StringComparison.Ordinal)).ToList();
            Equal(1, sent.Count, "requests that carried the key");
            Equal("POST /api/credentials/accounts", sent[0].Method + " " + new Uri(sent[0].Url).AbsolutePath, "the save");
            Check(!requests.Any(request => Uri.UnescapeDataString(request.Url).Contains(secret, StringComparison.Ordinal)), "no URL carries the key");
            Equal(0, FilesWith(app.Data, secret).Count, "no file of the data folder holds the key in plain text");
            Check(!app.Log.Contains(secret, StringComparison.Ordinal), "the app's log leaves the key out");
            foreach (var path in new[] { "/api/setup", "/api/credentials/accounts", "/settings/accounts", "/settings/about", "/setup?step=done" })
            {
                var served = await app.Get(path);
                Check(!served.Text.Contains(secret, StringComparison.Ordinal), path + " leaves the key out");
            }
        }),
    ];
}
