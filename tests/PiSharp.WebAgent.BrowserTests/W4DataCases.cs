using System.Text.Json.Nodes;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W4, fix 2, tests-ci): Settings › Data & storage's script (data.js) in a browser, on faux, failing on any page or
// console error. Run history's dirty state: a changed field marks its row (.is-modified, "modified · was …"), the save bar counts and
// names the changes, setting a field back clears its mark, Discard reloads the stored values, Save keeps them (a toast says what they mean)
// and Reset to defaults sets the fields back without saving until Save. The danger zone: each action asks in #modal-data-confirm, whose
// button stays locked until DELETE is typed exactly (a wrong case does not unlock it, Cancel deletes nothing, Enter confirms), and each
// deletes only what it names: Delete chats keeps History and the key, Clear history keeps the key and the settings, Reset settings keeps
// the key (after writing a copy of the old settings into the data folder), and Delete credentials removes the stored key. A button with
// nothing left to delete is disabled.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> W4DataCases() =>
    [
        Browse("browser.w4.data.run-history-dirty-state-discard-save-and-reset", async (app, window) =>
        {
            await app.PlaygroundRunAsync("data dirty state");
            var page = window.Page;
            await page.GotoAsync("/settings/data#run-history");
            var form = page.Locator("form[data-history-form]");
            var bar = form.Locator("[data-savebar]");
            var retention = form.Locator("select[name='retentionDays']");
            var raw = form.Locator("input[name='storeRawPayloads']");
            var events = form.Locator("input[name='storeSseEvents']");
            var row = (string key) => form.Locator($"[data-setting-row='{key}']");
            var chip = (string key) => row(key).Locator("[data-modified-chip]");

            // Loaded: the defaults, nothing modified.
            await Expect(retention).ToHaveValueAsync("30");
            await Expect(raw).Not.ToBeCheckedAsync();
            await Expect(events).ToBeCheckedAsync();
            await Expect(bar).ToBeHiddenAsync();
            foreach (var key in new[] { "retentionDays", "storeRawPayloads", "storeSseEvents" }) await Expect(chip(key)).ToBeHiddenAsync();

            // Two changes: both rows are marked with what they were, and the save bar counts and names them.
            await retention.SelectOptionAsync("7");
            await Expect(row("retentionDays")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bis-modified\b"));
            await Expect(chip("retentionDays")).ToHaveTextAsync("modified · was 30 days");
            await Expect(form.Locator("[data-retention-note]")).ToBeHiddenAsync(); // No run is older than 7 days.
            await Expect(bar).ToBeVisibleAsync();
            await Expect(bar.Locator("[data-savebar-count]")).ToHaveTextAsync("1 unsaved change");
            await raw.CheckAsync();
            await Expect(chip("storeRawPayloads")).ToHaveTextAsync("modified · was off");
            await Expect(bar.Locator("[data-savebar-count]")).ToHaveTextAsync("2 unsaved changes");
            await Expect(bar.Locator("[data-savebar-names]")).ToHaveTextAsync("history.retentionDays · history.storeRawPayloads");

            // Set back by hand, a field is not modified any more.
            await retention.SelectOptionAsync("30");
            await Expect(chip("retentionDays")).ToBeHiddenAsync();
            await Expect(row("retentionDays")).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bis-modified\b"));
            await Expect(bar.Locator("[data-savebar-count]")).ToHaveTextAsync("1 unsaved change");

            // Discard: the stored values again.
            await bar.Locator("[data-discard]").ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.Load);
            await Expect(raw).Not.ToBeCheckedAsync();
            await Expect(bar).ToBeHiddenAsync();
            Equal(false, (await app.Get("/api/data/facts")).Body!["runs"]!["settings"]!["storeRawPayloads"]!.GetValue<bool>(), "nothing was saved");

            // Save: 90 days, no stream events.
            await retention.SelectOptionAsync("90");
            await events.UncheckAsync();
            await Expect(chip("storeSseEvents")).ToHaveTextAsync("modified · was on");
            await bar.Locator("[data-save]").ClickAsync();
            await page.ToastAsync("Settings saved");
            await page.ToastAsync("Runs are kept for 90 days.");
            await page.ToastAsync("Stream events are not stored for new runs.");
            await Expect(retention).ToHaveValueAsync("90");
            await Expect(events).Not.ToBeCheckedAsync();
            await Expect(bar).ToBeHiddenAsync();
            var saved = (await app.Get("/api/data/facts")).Body!["runs"]!["settings"]!;
            Equal(90, saved["retentionDays"]!.GetValue<int>(), "the saved retention");
            Equal(false, saved["storeSseEvents"]!.GetValue<bool>(), "the saved stream events switch");

            // Reset to defaults: the fields only, until Save.
            await form.Locator("[data-history-reset]").ClickAsync();
            await page.ToastAsync("Reset to defaults");
            await Expect(retention).ToHaveValueAsync("30");
            await Expect(events).ToBeCheckedAsync();
            await Expect(chip("retentionDays")).ToHaveTextAsync("modified · was 90 days");
            await Expect(chip("storeSseEvents")).ToHaveTextAsync("modified · was off");
            await Expect(bar.Locator("[data-savebar-count]")).ToHaveTextAsync("2 unsaved changes");
            Equal(90, (await app.Get("/api/data/facts")).Body!["runs"]!["settings"]!["retentionDays"]!.GetValue<int>(), "not saved yet");
            await bar.Locator("[data-save]").ClickAsync();
            await page.ToastAsync("Runs are kept for 30 days.");
            await Expect(bar).ToBeHiddenAsync();
            saved = (await app.Get("/api/data/facts")).Body!["runs"]!["settings"]!;
            Equal(30, saved["retentionDays"]!.GetValue<int>(), "the default retention");
            Equal(true, saved["storeSseEvents"]!.GetValue<bool>(), "stream events again");
        }),

        Browse("browser.w4.data.danger-zone-needs-delete-typed-and-each-deletes-only-what-it-names", async (app, window) =>
        {
            var secret = "sk-proj-PLANTED" + Guid.NewGuid().ToString("N") + "Dz9k";
            var key = await app.PostAsync("/api/credentials/accounts", new JsonObject { ["provider"] = "openai", ["apiKey"] = secret, ["label"] = "Danger key" });
            Equal(200, key.Status, "a stored key: " + key.Text.Replace(secret, "[secret]", StringComparison.Ordinal));
            var run = await app.PlaygroundRunAsync("danger zone run");
            var chat = await app.CreateChatAsync("danger zone chat", "w4-danger-" + Guid.NewGuid().ToString("N"));
            await app.WaitIdleAsync(chat);
            var history = await app.PostAsync("/api/data/history", new JsonObject { ["retentionDays"] = 90, ["storeRawPayloads"] = false, ["storeSseEvents"] = true });
            Equal(200, history.Status, "a changed setting: " + history.Text);
            async Task<int> KeysAsync() => (await app.Get("/api/credentials/accounts?provider=openai")).Body!["accounts"]!.AsArray()
                .Count(account => account?["label"]?.GetValue<string>() == "Danger key");
            async Task<int> ChatsAsync() => (await app.Get("/api/chats")).Body?["items"]?.AsArray().Count ?? 0;
            async Task<int?> RetentionAsync() => (await app.Get("/api/data/facts")).Body!["runs"]!["settings"]!["retentionDays"]?.GetValue<int>();

            var page = window.Page;
            await page.GotoAsync("/settings/data#danger-zone");
            var modal = page.Locator("#modal-data-confirm");
            var input = modal.Locator("[data-confirm-input]");
            var ok = modal.Locator("[data-confirm-ok]");
            foreach (var action in new[] { "history", "chats", "credentials", "settings" }) await Expect(page.Locator($"[data-danger='{action}']")).ToBeEnabledAsync();

            // Delete chats: locked until DELETE is typed exactly; Cancel deletes nothing.
            await page.Locator("[data-danger='chats']").ClickAsync();
            await page.ModalShownAsync("modal-data-confirm");
            await Expect(modal.Locator("[data-confirm-title]")).ToHaveTextAsync("Delete all chats?");
            await Expect(modal.Locator("[data-confirm-body]")).ToContainTextAsync("Runs in History stay.");
            await Expect(modal.Locator("[data-confirm-typed]")).ToBeVisibleAsync();
            await Expect(input).ToBeFocusedAsync();
            await Expect(ok).ToHaveTextAsync("Delete chats");
            await Expect(ok).ToBeDisabledAsync();
            await input.FillAsync("delete");
            await Expect(ok).ToBeDisabledAsync();
            await input.FillAsync("DELETE ");
            await Expect(ok).ToBeDisabledAsync();
            await input.FillAsync("DELETE");
            await Expect(ok).ToBeEnabledAsync();
            await modal.Locator("[data-bs-dismiss='modal']").ClickAsync();
            await Expect(modal).ToBeHiddenAsync();
            Equal(1, await ChatsAsync(), "Cancel deleted no chat");

            // Again, confirmed with Enter: the chat goes, History and the key stay.
            await page.Locator("[data-danger='chats']").ClickAsync();
            await page.ModalShownAsync("modal-data-confirm");
            await Expect(input).ToHaveValueAsync("");
            await Expect(ok).ToBeDisabledAsync();
            await input.FillAsync("DELETE");
            await input.PressAsync("Enter");
            await page.ToastAsync("Chats deleted");
            await page.ToastAsync("Moved 1 chat to the archive.");
            Equal(0, await ChatsAsync(), "no chat is listed");
            Equal(200, (await app.Get("/api/runs/" + run)).Status, "the run stays in History");
            Equal(1, await KeysAsync(), "the key stays");
            await Expect(page.Locator("[data-danger='chats']")).ToBeDisabledAsync();

            // Clear history: every run goes; the key and the settings stay.
            await W4DangerAsync(page, "history", "Clear all run history?", "Clear history");
            await page.ToastAsync("Run history cleared");
            Equal(404, (await app.Get("/api/runs/" + run)).Status, "the run is gone");
            Equal(1, await KeysAsync(), "the key stays");
            Equal(90, await RetentionAsync(), "the settings stay");
            await Expect(page.Locator("[data-danger='history']")).ToBeDisabledAsync();

            // Reset settings: back to the defaults after a copy; the key stays.
            await W4DangerAsync(page, "settings", "Reset all settings?", "Reset settings");
            await page.ToastAsync("Settings reset");
            await page.ToastAsync("settings-backup-");
            Equal(30, await RetentionAsync(), "the default retention");
            Equal(1, await KeysAsync(), "the key stays");
            var backups = Directory.EnumerateFiles(app.Data, "settings-backup-*.json").ToList();
            Equal(1, backups.Count, "one copy of the old settings in the data folder");
            Check((await File.ReadAllTextAsync(backups[0])).Contains("90", StringComparison.Ordinal), "the copy holds the old retention");
            Check(!(await File.ReadAllTextAsync(backups[0])).Contains(secret, StringComparison.Ordinal), "the copy holds no key");
            await Expect(page.Locator("select[name='retentionDays']")).ToHaveValueAsync("30");

            // Delete credentials: the stored key goes.
            await W4DangerAsync(page, "credentials", "Delete all credentials?", "Delete credentials");
            await page.ToastAsync("Credentials deleted");
            await page.ToastAsync("Deleted 1 stored credential.");
            Equal(0, await KeysAsync(), "the key is gone");
            await Expect(page.Locator("[data-danger='credentials']")).ToBeDisabledAsync();
            Check(!(await page.ContentAsync()).Contains(secret, StringComparison.Ordinal), "no key in the page");
        }),
    ];

    /// <summary>Runs the danger zone's <paramref name="action"/> through #modal-data-confirm (its title and OK text checked), with DELETE
    /// typed and OK clicked.</summary>
    private static async Task W4DangerAsync(IPage page, string action, string title, string okText)
    {
        var modal = page.Locator("#modal-data-confirm");
        await page.Locator($"[data-danger='{action}']").ClickAsync();
        await page.ModalShownAsync("modal-data-confirm");
        await Expect(modal.Locator("[data-confirm-title]")).ToHaveTextAsync(title);
        var ok = modal.Locator("[data-confirm-ok]");
        await Expect(ok).ToHaveTextAsync(okText);
        await Expect(ok).ToBeDisabledAsync();
        await modal.Locator("[data-confirm-input]").FillAsync("DELETE");
        await ok.ClickAsync();
    }
}
