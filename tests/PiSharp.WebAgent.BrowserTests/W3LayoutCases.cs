using System.Text.Json.Nodes;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W3, T3): every W3 page on a phone, on priced faux (--faux-metered) so the spend parts show real numbers: at 375 × 812, in
// the dark theme (the default) and then in the light theme, no page scrolls sideways and no open dialog is wider than the screen. The app
// holds what the pages show when in use: a chat answered at a price (cost footers), a chat whose call a per-request cap stopped (the stop
// card and the spend banner), a cap, a custom provider, a stored key (a made-up value, so Setup's Default model step shows) and a finished
// setup (so its Done step shows). The pages: Settings › Spend, History › Spend, Settings › Custom providers, Settings › Appearance,
// Settings › About, the four Setup steps, a new chat with a draft over the cap, and both chats; the dialogs: Add a spend cap, the custom
// provider editor and the spend confirmation of a send the cap refused. W3 fix (tests-ci): the states and dialogs it used to leave out. A
// priced-faux slow: answer is killed mid-stream and the app restarted on the same data (as the History › Spend case does), so History ›
// Spend shows its uncertain banner and row, ?resolve= opens the Resolve dialog (on each of its choices), and the chat shows its uncertain
// footer; the cap dialog's one-provider and one-source panes and its Edit pane (a cap row's edit button); About's connectivity check after
// the click (its results pane); the custom provider's remove confirmation, cancelled; and the API key dialog of Setup's Connect step. Nothing
// is settled or removed.
internal static partial class Program
{
    private static readonly string[] W3PhonePages =
    [
        "/settings/spend", "/history/spend", "/settings/custom-providers", "/settings/appearance", "/settings/about",
        "/setup?step=welcome", "/setup?step=connect", "/setup?step=model", "/setup?step=done",
    ];

    private static IEnumerable<(string, Func<Task>)> W3LayoutCases() =>
    [
        Browse("browser.w3.layout.every-w3-page-fits-a-phone-in-both-themes", AgentMode.FauxMetered, async (app, window) =>
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

            // A request left uncertain: the app dies while a slow: answer streams, and after the restart its reservation is uncertain. The
            // browser profile opens on the restarted app's address.
            var dropped = await app.CreateChatAsync("slow: " + Words(150), "phone-dropped");
            string op = null!;
            await WaitUntilAsync(async () => (await app.LedgerAsync()).FirstOrDefault(item => Reservation(item, "run") == dropped) is { } row &&
                Reservation(row, "state") == "reserved" && (op = Reservation(row, "op")!) is not null, "the dropped request's reservation", TimeSpan.FromSeconds(30));
            await app.KillAndRestartAsync();
            var phone = await window.ReopenAsync(375, 812);
            await app.WaitIdleAsync(dropped);
            Equal("uncertain", Reservation((await app.LedgerAsync()).Single(item => Reservation(item, "op") == op), "state"), "uncertain after the restart");

            var stopped = await app.CreateChatAsync("first", "phone-stopped");
            await app.WaitIdleAsync(stopped);
            var cap = await app.PostAsync("/api/spend/caps", new JsonObject { ["scope"] = "everything", ["period"] = "request", ["limit"] = "0.01", ["action"] = "ask" });
            Equal(200, cap.Status, "a per-request cap: " + cap.Text);
            var past = await app.PostAsync($"/api/chats/{stopped}/messages", new JsonObject { ["requestId"] = "phone-past", ["text"] = "past the cap" });
            Equal(200, past.Status, "posted without a check: " + past.Text);
            await app.WaitIdleAsync(stopped);
            Check((await app.LedgerAsync()).Count >= 3, "the ledger has rows to show");
            var capId = (await app.Get("/api/spend/caps")).Body!["caps"]!.AsArray().Single()!["id"]!.GetValue<long>();

            var page = phone.Page;
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
                    if (path == "/setup?step=connect")
                    {
                        // The Settings area's API key dialog, which the Connect step's "More options" opens.
                        await page.Locator("[data-setup-key-modal] >> visible=true").First.ClickAsync();
                        await Expect(page.Locator("#modal-api-key.show")).ToBeVisibleAsync();
                        await page.FitsPhoneAsync("Setup › Connect › the API key dialog", theme);
                        await page.Keyboard.PressAsync("Escape");
                        await Expect(page.Locator("#modal-api-key")).ToBeHiddenAsync();
                    }
                }

                await page.OpenChatAsync(answered);
                await Expect(page.Answers().Last).ToContainTextAsync("w030");
                await Expect(page.Locator("[data-spend-mark]").First).ToBeVisibleAsync();
                await page.FitsPhoneAsync("/chat/" + answered + " (cost footers)", theme);
                await page.OpenChatAsync(stopped);
                await Expect(page.Locator("[data-spend-stop]")).ToBeVisibleAsync();
                await Expect(page.Locator("[data-banner='spend']")).ToBeVisibleAsync();
                await page.FitsPhoneAsync("/chat/" + stopped + " (a stopped call)", theme);
                await page.OpenChatAsync(dropped);
                await Expect(page.Locator("[data-spend-mark='uncertain']").First).ToBeVisibleAsync();
                await Expect(page.Locator("a[data-spend-resolve]").First).ToBeVisibleAsync();
                await page.FitsPhoneAsync("/chat/" + dropped + " (uncertain spend)", theme);

                // History › Spend with the uncertain request: its banner and row, then the Resolve dialog the address opens, on each choice.
                await page.GotoAsync("/history/spend");
                await Expect(page.Locator("[data-uncertain-banner]")).ToBeVisibleAsync();
                await Expect(page.Locator($"[data-spend-row='{op}']")).ToHaveAttributeAsync("data-status", "uncertain");
                await page.FitsPhoneAsync("History › Spend (an uncertain request)", theme);
                await page.GotoAsync("/history/spend?resolve=" + Uri.EscapeDataString(op));
                var resolve = page.Locator("#modal-resolve-reservation");
                await Expect(page.Locator("#modal-resolve-reservation.show")).ToBeVisibleAsync();
                await Expect(resolve).ToContainTextAsync("Resolve uncertain spend");
                await page.FitsPhoneAsync("Resolve uncertain spend", theme);
                foreach (var (choice, pane) in new[] { ("rrEstimate", "estimate"), ("rrActual", "amount"), ("rrRelease", "release") })
                {
                    await resolve.Locator($"label[for='{choice}']").ClickAsync();
                    await Expect(resolve.Locator($".modal-footer [data-rr-pane='{pane}']")).ToBeVisibleAsync();
                    await page.FitsPhoneAsync("Resolve uncertain spend › " + pane, theme);
                }
                await resolve.Locator(".modal-footer [data-bs-dismiss='modal']").ClickAsync();
                await Expect(resolve).ToBeHiddenAsync();

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
                foreach (var scope in new[] { "provider", "source" })
                {
                    await page.Locator("#modal-spend-cap #capScope").SelectOptionAsync(scope);
                    await Expect(page.Locator($"#modal-spend-cap [data-cap-scope-pane='{scope}']")).ToBeVisibleAsync();
                    await page.FitsPhoneAsync("Add a spend cap › one " + scope, theme);
                }
                await page.Keyboard.PressAsync("Escape");
                await Expect(page.Locator("#modal-spend-cap")).ToBeHiddenAsync();
                await page.Locator($"[data-cap-edit][data-cap-id='{capId}']").ClickAsync();
                await Expect(page.Locator("#modal-spend-cap.show [data-cap-amount-edit]")).ToBeVisibleAsync();
                await Expect(page.Locator("#modal-spend-cap [data-cap-amount-edit]")).ToHaveValueAsync("0.01");
                await page.FitsPhoneAsync("Edit cap", theme);
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
                await page.Locator("[data-cp-delete='phone-box']").ClickAsync();
                var remove = page.Locator("#modal-confirm");
                await Expect(page.Locator("#modal-confirm.show")).ToBeVisibleAsync();
                await Expect(remove).ToContainTextAsync("Delete custom provider 'phone-box'?");
                await page.FitsPhoneAsync("the custom provider's remove confirmation", theme);
                await remove.Locator(".modal-footer [data-bs-dismiss='modal']").ClickAsync();
                await Expect(remove).ToBeHiddenAsync();

                // About's connectivity check, only on the click: offline it answers its results pane at once.
                await page.GotoAsync("/settings/about");
                var check = page.Locator("[data-connectivity]");
                await page.RunAndWaitForResponseAsync(() => check.Locator("[data-connectivity-run]").ClickAsync(),
                    response => response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath == "/api/about/connectivity");
                await Expect(check).ToHaveAttributeAsync("data-state", "results");
                await Expect(check.Locator("[data-pane='results']")).ToBeVisibleAsync();
                await page.FitsPhoneAsync("Settings › About › connectivity results", theme);
            }
            await window.RefusedAsync(2, "one refused send per theme");
            Equal(3, (await app.Get("/api/chats")).Body!["items"]!.AsArray().Count, "the refused sends created no chat");
            Equal("uncertain", Reservation((await app.LedgerAsync()).Single(item => Reservation(item, "op") == op), "state"), "the Resolve dialog settled nothing");
            Equal(200, (await app.Get("/api/custom-providers/phone-box")).Status, "the cancelled confirmation removed nothing");
        }, width: 375, height: 812),

        // W3 fix (spend-ui): an Accounts row whose billing mode is detected as Unknown price, the longest choice ("Unknown price (detected)"),
        // which a fixed-width select used to clip ("Unknown price (detect" at 1440 px, less at 375). Live, so the catalog lists a custom
        // provider: one on a made-up gateway with no price and no key (nothing is called; the page only reads the catalog). At 375 × 812 in the
        // dark and then the light theme, and at 1440 × 900: the select shows its chosen option in full, inside its row, and nothing scrolls
        // sideways. The provider's Enter price menu (Unknown prices) stays on the screen too (W3 fix, tests-ci).
        Browse("browser.w3.layout.an-unknown-price-account-is-never-clipped", AgentMode.Live, async (app, window) =>
        {
            var provider = await app.PostAsync("/api/custom-providers", new JsonObject
            {
                ["id"] = "work-gateway", ["name"] = "Contoso LLM Gateway", ["baseUrl"] = "https://gateway.contoso.invalid/v1", ["api"] = "openai-completions",
                ["key"] = new JsonObject { ["source"] = "none" },
                ["models"] = new JsonArray(new JsonObject { ["id"] = "gateway-large", ["name"] = "Gateway large", ["contextWindow"] = 128000, ["maxTokens"] = 16384 }),
            });
            Equal(200, provider.Status, "a custom provider without a price: " + provider.Text);
            var page = window.Page;
            var select = page.Locator("[data-setting-row^='billing:work-gateway|'] select");
            foreach (var (theme, width, height) in new[] { ("dark", 375, 812), ("light", 375, 812), ("light", 1440, 900) })
            {
                await page.SetViewportSizeAsync(width, height);
                await page.GotoAsync("/settings/spend#accounts");
                if (theme == "light" && width == 375) await page.UseThemeAsync("light");
                await Expect(select).ToHaveValueAsync("unknown");
                await Expect(select.Locator("option:checked")).ToHaveTextAsync("Unknown price (detected)");
                await page.FitsPhoneAsync($"Settings › Spend › Accounts at {width}px", theme);
                await page.ShowsChoiceInFullAsync(select, $"the Unknown-price account's billing mode at {width}px ({theme})");

                // W3 fix (tests-ci): its Unknown prices row's Enter price menu stays on the screen.
                var enter = page.Locator("[data-setting-row='unknown:work-gateway'] [data-bs-toggle='dropdown']");
                await enter.ClickAsync();
                await Expect(page.Locator("[data-price-menu][data-provider='work-gateway']")).ToBeVisibleAsync();
                await page.FitsPhoneAsync($"Settings › Spend › Enter price at {width}px", theme);
                await enter.ClickAsync();
                await Expect(page.Locator("[data-price-menu][data-provider='work-gateway']")).ToBeHiddenAsync();
            }
        }, width: 375, height: 812),
    ];

    /// <summary>Whether <paramref name="select"/> shows its chosen option's text in full (the text fits between its paddings, the arrow
    /// included) and stays inside its row.</summary>
    internal static async Task ShowsChoiceInFullAsync(this IPage page, ILocator select, string what)
    {
        var fit = JsonNode.Parse(await select.EvaluateAsync<string>("""
            (select) => {
              const style = getComputedStyle(select);
              const context = document.createElement('canvas').getContext('2d');
              context.font = `${style.fontStyle} ${style.fontWeight} ${style.fontSize} ${style.fontFamily}`;
              const text = select.options[select.selectedIndex].text;
              const room = select.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight);
              const box = select.getBoundingClientRect();
              const row = select.closest('.provider-row').getBoundingClientRect();
              return JSON.stringify({ text, need: Math.ceil(context.measureText(text).width), room: Math.floor(room), left: box.left - row.left, right: row.right - box.right });
            }
            """))!.AsObject();
        var text = fit["text"]!.GetValue<string>();
        var need = fit["need"]!.GetValue<double>();
        var room = fit["room"]!.GetValue<double>();
        Check(need <= room + 1, $"{what}: \"{text}\" needs {need}px but the select has {room}px");
        Check(fit["left"]!.GetValue<double>() >= -1 && fit["right"]!.GetValue<double>() >= -1, $"{what}: the select sticks out of its row: {fit.ToJsonString()}");
    }
}
