using System.Text.Json.Nodes;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W3, T3): every W3 page on a phone, on priced faux (--faux-metered) so the spend parts show real numbers: at 375 × 812, in
// the dark theme (the default) and then in the light theme, no page scrolls sideways and no open dialog is wider than the screen. The app
// holds what the pages show when in use: a chat answered at a price (cost footers), a chat whose call a per-request cap stopped (the stop
// card and the spend banner), a cap, a custom provider, a stored key (a made-up value, so Setup's Default model step shows) and a finished
// setup (so its Done step shows). The pages: Settings › Spend, History › Spend, Settings › Custom providers, Settings › Appearance,
// Settings › About, the four Setup steps, a new chat with a draft over the cap, and both chats; the dialogs: Add a spend cap, the custom
// provider editor and the spend confirmation of a send the cap refused.
internal static partial class Program
{
    private static readonly string[] W3PhonePages =
    [
        "/settings/spend", "/history/spend", "/settings/custom-providers", "/settings/appearance", "/settings/about",
        "/setup?step=welcome", "/setup?step=connect", "/setup?step=model", "/setup?step=done",
    ];

    private static IEnumerable<(string, Func<Task>)> W3LayoutCases() =>
    [
        BrowseW3("browser.w3.layout.every-w3-page-fits-a-phone-in-both-themes", W3Mode.FauxMetered, async (app, window) =>
        {
            // What the pages show when in use.
            var key = await app.PostAsync("/api/credentials/accounts",
                new JsonObject { ["provider"] = "openai", ["apiKey"] = "sk-proj-PLANTED" + Guid.NewGuid().ToString("N") + "Ph0ne" });
            Equal(200, key.Status, "a stored key: " + key.Text);
            var finished = await app.PostAsync("/api/setup/finish", new JsonObject { ["useAsDefault"] = false });
            Equal(200, finished.Status, "setup finished: " + finished.Text);
            var provider = await app.PostAsync("/api/custom-providers", new JsonObject
            {
                ["id"] = "phone-box", ["name"] = "Phone box", ["baseUrl"] = "http://127.0.0.1:9/v1", ["api"] = "openai-completions",
                ["key"] = new JsonObject { ["source"] = "none" },
                ["models"] = new JsonArray(new JsonObject { ["id"] = "phone-model", ["name"] = "Phone model", ["contextWindow"] = 128000, ["maxTokens"] = 16384 }),
            });
            Equal(200, provider.Status, "a custom provider: " + provider.Text);
            var answered = await app.CreateChatAsync("slow: " + Words(30), "phone-answered");
            await app.WaitIdleAsync(answered);
            var stopped = await app.CreateChatAsync("first", "phone-stopped");
            await app.WaitIdleAsync(stopped);
            var cap = await app.PostAsync("/api/spend/caps", new JsonObject { ["scope"] = "everything", ["period"] = "request", ["limit"] = "0.01", ["action"] = "ask" });
            Equal(200, cap.Status, "a per-request cap: " + cap.Text);
            var past = await app.PostAsync($"/api/chats/{stopped}/messages", new JsonObject { ["requestId"] = "phone-past", ["text"] = "past the cap" });
            Equal(200, past.Status, "posted without a check: " + past.Text);
            await app.WaitIdleAsync(stopped);
            Check((await app.LedgerAsync()).Count >= 2, "the ledger has rows to show");
            window.ExpectRefusal("/api/chats");

            var page = window.Page;
            foreach (var theme in new[] { "dark", "light" })
            {
                await page.GotoAsync("/settings/spend");
                if (theme == "light") await page.UseThemeAsync("light");
                foreach (var path in W3PhonePages)
                {
                    await page.GotoAsync(path);
                    if (path.StartsWith("/setup?step=", StringComparison.Ordinal))
                        await Expect(page.Locator("[data-setup-wizard]")).ToHaveAttributeAsync("data-setup-current", path["/setup?step=".Length..]);
                    await page.FitsPhoneAsync(path, theme);
                }

                await page.OpenChatAsync(answered);
                await Expect(page.Answers().Last).ToContainTextAsync("w030");
                await Expect(page.Locator("[data-spend-mark]").First).ToBeVisibleAsync();
                await page.FitsPhoneAsync("/chat/" + answered + " (cost footers)", theme);
                await page.OpenChatAsync(stopped);
                await Expect(page.Locator("[data-spend-stop]")).ToBeVisibleAsync();
                await Expect(page.Locator("[data-banner='spend']")).ToBeVisibleAsync();
                await page.FitsPhoneAsync("/chat/" + stopped + " (a stopped call)", theme);

                // A new chat with a draft over the cap: the estimate and its chip, then the confirmation the refused send opens.
                await page.GotoAsync("/chat/new");
                var box = page.Locator("[data-composer-input]");
                await box.FillAsync("a draft on a phone");
                await Expect(page.Locator("[data-composer] [data-spend-over]")).ToBeVisibleAsync();
                await page.FitsPhoneAsync("/chat/new (a draft over the cap)", theme);
                await box.PressAsync("Enter");
                var confirm = page.Locator("#modal-spend-confirm");
                await Expect(page.Locator("#modal-spend-confirm.show")).ToBeVisibleAsync();
                await page.FitsPhoneAsync("the spend confirmation", theme);
                await confirm.Locator(".modal-footer [data-spend-choice='cancel']").ClickAsync();
                await Expect(confirm).ToBeHiddenAsync();

                // The dialogs of Settings › Spend and Settings › Custom providers.
                await page.GotoAsync("/settings/spend");
                await page.Locator("[data-cap-new] >> visible=true").First.ClickAsync();
                await Expect(page.Locator("#modal-spend-cap.show")).ToBeVisibleAsync();
                await page.FitsPhoneAsync("Add a spend cap", theme);
                await page.Keyboard.PressAsync("Escape");
                await Expect(page.Locator("#modal-spend-cap")).ToBeHiddenAsync();
                await page.GotoAsync("/settings/custom-providers");
                await Expect(page.Locator("[data-cp-row='phone-box']")).ToBeVisibleAsync();
                await page.Locator("[data-cp-edit='phone-box']").ClickAsync();
                await Expect(page.Locator("#modal-custom-provider.show")).ToBeVisibleAsync();
                foreach (var tab in new[] { "general", "models", "advanced", "preview" })
                {
                    await page.Locator($"#modal-custom-provider [data-cp-tab='{tab}']").ClickAsync();
                    await page.FitsPhoneAsync("the custom provider editor › " + tab, theme);
                }
                await page.Keyboard.PressAsync("Escape");
                await Expect(page.Locator("#modal-custom-provider")).ToBeHiddenAsync();
            }
            Equal(2, window.Refusals.Count, "one refused send per theme");
            Equal(2, (await app.Get("/api/chats")).Body!["items"]!.AsArray().Count, "the refused sends created no chat");
        }, width: 375, height: 812),
    ];
}
