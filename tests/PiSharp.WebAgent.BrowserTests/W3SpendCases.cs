using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W3, T3): the Spend Guard in a browser, on the priced faux provider (--faux-metered: offline, Metered at a list price,
// usage from text length). Settings › Spend adds a cap through its modal, edits it and removes it. A one-cent cap per request blocks a send
// from the chat and from a new chat: the composer's chip already says so, Send opens modal-spend-confirm on the cause's pane with Proceed
// once disabled and no shorter output offered (the input alone is over a cent), and Cancel posts nothing; at five cents the input fits, and
// "Cap output" sends the same message once on the same model (the guard never switches it). A call
// the guard stops inside a run (an Ask cap, a message sent without the pre-send check) shows its stop card and banner; Review and continue…
// opens the stop's own modal, and Proceed once continues the run with "Continue.". History › Spend shows a running request's reservation;
// the server is then killed mid-answer, and after the restart the chat's footer links the now uncertain spend to Resolve…, which opens
// History › Spend's modal and settles it at the estimate. The browser logs every 409 a refused send gets; each case declares those it expects
// (W3Window.ExpectRefusal) and checks their number.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> W3SpendCases() =>
    [
        BrowseW3("browser.w3.spend.settings-adds-edits-and-removes-a-cap", W3Mode.FauxMetered, async (app, window) =>
        {
            var page = window.Page;
            await page.GotoAsync("/settings/spend");
            await Expect(page.Locator("[data-caps-empty]")).ToBeVisibleAsync();
            await page.Locator("[data-caps-empty] [data-cap-new]").ClickAsync();
            var modal = page.Locator("#modal-spend-cap");
            await Expect(modal).ToBeVisibleAsync();
            await Expect(modal.Locator("[data-cap-amount-new]")).ToBeVisibleAsync();

            // The priced faux provider is metered, so it is offered as one provider; the cap here is for everything metered, per UTC month.
            await modal.Locator("#capScope").SelectOptionAsync("provider");
            await Expect(modal.Locator("[data-cap-scope-pane='provider']")).ToBeVisibleAsync();
            await Expect(modal.Locator("#capProvider option[value='faux']")).ToHaveCountAsync(1);
            await modal.Locator("#capScope").SelectOptionAsync("everything");
            await modal.Locator("label[for='capWinMonth']").ClickAsync();
            await modal.Locator("#capAmount").FillAsync("5");
            await modal.Locator("label[for='capBlock']").ClickAsync();
            await modal.Locator("[data-cap-save]").ClickAsync();

            var row = page.Locator("[data-cap-row]");
            await Expect(row).ToHaveCountAsync(1);
            await Expect(row).ToContainTextAsync("$0.00 / $5.00");
            await Expect(row.Locator("td").Nth(2)).ToHaveTextAsync("Block");
            await page.ToastAsync("Cap saved");
            var caps = (await app.Get("/api/spend/caps")).Body!["caps"]!.AsArray();
            Equal(1, caps.Count, "one cap");
            Equal("everything|month|5000000|block|True", Cap(caps[0]!), "as chosen");

            await row.Locator("[data-cap-edit]").ClickAsync();
            await Expect(modal.Locator("[data-cap-amount-edit]")).ToBeVisibleAsync();
            await Expect(modal.Locator("[data-cap-amount-edit]")).ToHaveValueAsync("5.00");
            await Expect(modal.Locator("input#capEditBlock")).ToBeCheckedAsync();
            await modal.Locator("[data-cap-amount-edit]").FillAsync("7.50");
            await modal.Locator("label[for='capEditAsk']").ClickAsync();
            await modal.Locator("[data-cap-save]").ClickAsync();
            await Expect(row).ToContainTextAsync("$0.00 / $7.50");
            await Expect(row.Locator("td").Nth(2)).ToHaveTextAsync("Ask");
            Equal("everything|month|7500000|ask|True", Cap((await app.Get("/api/spend/caps")).Body!["caps"]!.AsArray().Single()!), "edited");

            await row.Locator("[data-cap-edit]").ClickAsync();
            await modal.Locator("[data-cap-remove]").ClickAsync();
            await Expect(page.Locator("[data-caps-empty]")).ToBeVisibleAsync();
            await page.ToastAsync("Cap removed");
            Equal(0, (await app.Get("/api/spend/caps")).Body!["caps"]!.AsArray().Count, "removed");
        }),

        BrowseW3("browser.w3.spend.a-one-cent-cap-blocks-a-send-without-switching-the-model", W3Mode.FauxMetered, async (app, window) =>
        {
            var id = await app.CreateChatAsync("hello before the cap", "before");
            await app.WaitIdleAsync(id);
            var chat = (await app.Get("/api/chats/" + id)).Body!;
            var (provider, model) = (chat["provider"]!.GetValue<string>(), chat["model"]!.GetValue<string>());
            Equal(1, (await app.LedgerAsync()).Count, "the first answer's request");
            var capped = await app.PostAsync("/api/spend/caps", new JsonObject { ["scope"] = "everything", ["period"] = "request", ["limit"] = "0.01", ["action"] = "block" });
            Equal(200, capped.Status, "a one-cent Block cap per request: " + capped.Text);
            window.ExpectRefusal($"/api/chats/{id}/messages");
            window.ExpectRefusal("/api/chats");

            var page = window.Page;
            await page.OpenChatAsync(id);
            var shownModel = await page.Locator("[data-model-name]").First.TextContentAsync();
            // The chip by Send already says the next request would not fit.
            await Expect(page.Locator("[data-composer] [data-spend-over]")).ToHaveTextAsync("Over request cap");
            var box = page.Locator("[data-composer-input]");
            await box.FillAsync("one more, please");
            await box.PressAsync("Enter");
            var confirm = page.Locator("#modal-spend-confirm");
            await Expect(confirm).ToBeVisibleAsync();
            var content = confirm.Locator("[data-spend-confirm-content]");
            await Expect(content).ToHaveAttributeAsync("data-verdict", "block");
            await Expect(content).ToHaveAttributeAsync("data-pane", "sc-request");
            await Expect(confirm.Locator(".modal-footer button", new() { HasTextString = "Proceed once" })).ToBeDisabledAsync();
            await Expect(confirm).ToContainTextAsync("PiSharp never switches your model or account");
            // One cent does not even hold the input (the system prompt and the tools), so no shorter output is offered either.
            await Expect(confirm).ToContainTextAsync("the input alone is over the cap");
            await Expect(confirm.Locator("[data-spend-choice='cap-output']")).ToHaveCountAsync(0);
            await confirm.Locator(".modal-footer [data-spend-choice='cancel']").ClickAsync();
            await Expect(confirm).ToBeHiddenAsync();

            // Cancel posted nothing: the draft stays, no message, no reservation, the same model.
            await Expect(box).ToHaveValueAsync("one more, please");
            Equal(1, Mentions((await app.Get($"/chat/{id}/export.md")).Text, "### You"), "nothing was posted");
            Equal(1, (await app.LedgerAsync()).Count, "nothing was reserved");
            Equal(1, window.Refusals.Count, "one refused send");
            var blocked = (await app.Get("/api/chats/" + id)).Body!;
            Equal(provider + "/" + model, blocked["provider"]!.GetValue<string>() + "/" + blocked["model"]!.GetValue<string>(), "the block switched nothing");

            // At five cents per request the input fits but the default 8,192-token reservation does not: capping the output so it fits
            // sends the same message once, on the same provider and model.
            var oneCent = (await app.Get("/api/spend/caps")).Body!["caps"]!.AsArray().Single()!["id"]!.GetValue<long>();
            Equal(200, (await app.SendAsync(HttpMethod.Post, $"/api/spend/caps/{oneCent}/remove")).Status, "the one-cent cap removed");
            capped = await app.PostAsync("/api/spend/caps", new JsonObject { ["scope"] = "everything", ["period"] = "request", ["limit"] = "0.05", ["action"] = "block" });
            Equal(200, capped.Status, "a five-cent Block cap per request: " + capped.Text);
            await box.PressAsync("Enter");
            await Expect(confirm).ToBeVisibleAsync();
            await Expect(content).ToHaveAttributeAsync("data-verdict", "block");
            await Expect(confirm.Locator(".modal-footer button", new() { HasTextString = "Proceed once" })).ToBeDisabledAsync();
            var cap = confirm.Locator("[data-spend-choice='cap-output']");
            await Expect(cap).ToBeVisibleAsync();
            var tokens = int.Parse(await cap.GetAttributeAsync("data-max-output-tokens") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
            Check(tokens > 0, "a shorter output fits the cap");
            await cap.ClickAsync();
            await Expect(box).ToHaveValueAsync("");
            await Expect(page.Answers().Last).ToContainTextAsync("Echo: one more, please");
            await app.WaitIdleAsync(id);
            Equal(2, Mentions((await app.Get($"/chat/{id}/export.md")).Text, "### You"), "the message was posted once");
            var ledger = await app.LedgerAsync();
            Equal(2, ledger.Count, "one more request");
            var sent = ledger[0];
            Equal(provider + "/" + model, Reservation(sent, "provider") + "/" + Reservation(sent, "model"), "sent to the chat's own model");
            Check(Reservation(sent, "note")?.Contains("output capped at", StringComparison.Ordinal) == true, "with the output capped: " + sent.ToJsonString());
            Equal("settled", Reservation(sent, "state"), "and settled");
            var after = (await app.Get("/api/chats/" + id)).Body!;
            Equal(provider + "/" + model, after["provider"]!.GetValue<string>() + "/" + after["model"]!.GetValue<string>(), "the chat's model is unchanged");
            await Expect(page.Locator("[data-model-name]").First).ToHaveTextAsync(shownModel ?? "");
            Equal(2, window.Refusals.Count, "the send was checked first");

            // A new chat is refused the same way, and none is created.
            await page.GotoAsync("/chat/new");
            await Expect(page.Locator("[data-composer] [data-spend-over]")).ToHaveTextAsync("Over request cap");
            await page.Locator("[data-composer-input]").FillAsync("a new chat over the cap");
            await page.Locator("[data-composer-input]").PressAsync("Enter");
            await Expect(confirm).ToBeVisibleAsync();
            await Expect(content).ToHaveAttributeAsync("data-verdict", "block");
            await confirm.Locator(".modal-footer [data-spend-choice='cancel']").ClickAsync();
            await Expect(confirm).ToBeHiddenAsync();
            Check(new Uri(page.Url).AbsolutePath == "/chat/new", "still on the new chat page: " + page.Url);
            Equal(1, (await app.Get("/api/chats")).Body!["items"]!.AsArray().Count, "no chat was created");
            Equal(3, window.Refusals.Count, "three refused sends in all");
        }),

        BrowseW3("browser.w3.spend.a-call-stopped-inside-a-run-continues-after-review", W3Mode.FauxMetered, async (app, window) =>
        {
            var id = await app.CreateChatAsync("hello before the cap", "before");
            await app.WaitIdleAsync(id);
            var capped = await app.PostAsync("/api/spend/caps", new JsonObject { ["scope"] = "everything", ["period"] = "request", ["limit"] = "0.01", ["action"] = "ask" });
            Equal(200, capped.Status, "a one-cent Ask cap per request: " + capped.Text);
            // Sent without the pre-send check (as a script would): the guard stops the call inside the run, before anything reaches the model.
            var posted = await app.PostAsync($"/api/chats/{id}/messages", new JsonObject { ["requestId"] = "unchecked", ["text"] = "past the cap" });
            Equal(200, posted.Status, "posted: " + posted.Text);
            await app.WaitIdleAsync(id);

            var page = window.Page;
            await page.OpenChatAsync(id);
            var stop = page.Thread().Locator("[data-spend-stop]");
            await Expect(stop).ToHaveCountAsync(1);
            await Expect(stop).ToHaveAttributeAsync("data-code", "spend_ask");
            await Expect(page.Thread().Locator("[data-failure='spend']")).ToHaveCountAsync(1);
            await Expect(page.Locator("[data-banner='spend']")).ToBeVisibleAsync();
            var review = page.Locator("[data-banner='spend'] [data-action='spend-review']");
            await Expect(review).ToHaveTextAsync("Review and continue…");
            await Expect(page.Locator("[data-banner='spend'] a[href='/settings/spend#caps']")).ToHaveCountAsync(1);
            var ledger = await app.LedgerAsync();
            Equal(1, ledger.Count, "the stopped call reserved nothing");

            await review.ClickAsync();
            var confirm = page.Locator("#modal-spend-confirm");
            await Expect(confirm).ToBeVisibleAsync();
            await Expect(confirm.Locator("[data-spend-confirm-content]")).ToHaveAttributeAsync("data-verdict", "ask");
            var proceed = confirm.Locator("[data-spend-choice='proceed-once']");
            await Expect(proceed).ToBeEnabledAsync();
            await proceed.ClickAsync();
            await page.ToastAsync("Continuing");
            await Expect(page.Answers().Last).ToContainTextAsync("Echo: Continue.");
            await app.WaitIdleAsync(id);
            var export = (await app.Get($"/chat/{id}/export.md")).Text;
            Equal(3, Mentions(export, "### You"), "the run continued once, with one \"Continue.\": " + export);
            Equal(1, Mentions(export, "Echo: Continue."), "and one answer to it");
            ledger = await app.LedgerAsync();
            Equal(2, ledger.Count, "the continued call was reserved once");
            Check(Reservation(ledger[0], "note")?.Contains("sent once", StringComparison.Ordinal) == true, "after the user's one-time approval: " + ledger[0].ToJsonString());
            Equal("settled", Reservation(ledger[0], "state"), "and settled");
            Equal(0, window.Refusals.Count, "Review asked the stop's own modal; nothing was refused");
        }),

        BrowseW3("browser.w3.spend.history-shows-the-reservation-and-resolve-settles-it", W3Mode.FauxMetered, async (app, window) =>
        {
            var id = await app.CreateChatAsync("slow: " + Words(150), "slow");
            JsonObject reserved = null!;
            await WaitUntilAsync(async () => (await app.LedgerAsync()) is [var only] && Reservation(reserved = only, "state") == "reserved",
                "the running request's reservation", TimeSpan.FromSeconds(30));
            var op = Reservation(reserved, "op")!;
            Equal(id, Reservation(reserved, "run"), "the chat's request");

            var page = window.Page;
            await page.GotoAsync("/history/spend");
            var row = page.Locator($"[data-spend-row='{op}']");
            await Expect(row).ToHaveAttributeAsync("data-status", "reserved");
            await Expect(row).ToContainTextAsync("faux/");

            // The server dies mid-answer; after the restart the reservation is uncertain and the chat links it to Resolve….
            await app.KillAndRestartAsync();
            var after = await window.ReopenAsync();
            var second = after.Page;
            await app.WaitIdleAsync(id);
            Equal("uncertain", Reservation((await app.LedgerAsync()).Single(), "state"), "uncertain after the restart");
            await second.OpenChatAsync(id);
            var resolve = second.Locator($"a[data-spend-resolve][href='/history/spend?resolve={Uri.EscapeDataString(op)}']");
            await Expect(resolve.First).ToBeVisibleAsync();
            await Expect(second.Locator("[data-spend-mark='uncertain']").First).ToBeVisibleAsync();
            await resolve.First.ClickAsync();
            await Expect(second).ToHaveURLAsync(new Regex(@"/history/spend\?resolve="));
            var modal = second.Locator("#modal-resolve-reservation");
            await Expect(modal).ToBeVisibleAsync();
            await Expect(modal).ToContainTextAsync("Resolve uncertain spend");
            await Expect(modal).ToContainTextAsync("stream dropped, no usage reported");
            await Expect(modal.Locator("input#rrEstimate")).ToBeCheckedAsync();
            await modal.Locator("[data-rr-submit]:visible").ClickAsync();
            await second.ToastAsync("Reservation resolved");
            await Expect(second.Locator($"[data-spend-row='{op}']")).ToHaveAttributeAsync("data-status", "estimate");
            Check(!second.Url.Contains("resolve=", StringComparison.Ordinal), "the address no longer opens the modal: " + second.Url);
            var settled = (await app.LedgerAsync()).Single();
            Equal("settled|estimate|true", $"{Reservation(settled, "state")}|{Reservation(settled, "settlement")}|{settled["reservation"]!["resolvedByUser"]}",
                "settled at the estimate by the user");
        }),
    ];

    /// <summary>A cap of <c>GET /api/spend/caps</c> as <c>scope|period|limitMicros|action|enabled</c>.</summary>
    private static string Cap(JsonNode cap) =>
        $"{cap["scope"]}|{cap["period"]}|{cap["limitMicros"]}|{cap["action"]}|{(cap["enabled"]?.GetValue<bool>() == true ? "True" : "False")}";

    /// <summary>How many times <paramref name="part"/> occurs in <paramref name="text"/>.</summary>
    private static int Mentions(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }
}
