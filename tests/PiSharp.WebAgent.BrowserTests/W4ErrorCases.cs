using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W4, integration fixes): the request pages' error paths in a browser, on priced faux (--faux-metered), failing on any
// page or console error. The faux models end a prompt "error: <message>" with a provider error (PiSharp.Durable.Hosting's
// FauxDurableModels), so each path runs offline: the Playground's output shows the error card, its Details open #modal-error-details with
// the provider's message; every Compare column shows its error and the group still ends; the Tool Bench's failed turn ends the bench in
// its error state with the message on the timeline. At 375 × 812, in both themes, the error card and #modal-error-details fit the screen.
internal static partial class Program
{
    private const string FauxError = "The faux upstream refused this request.";

    private static IEnumerable<(string, Func<Task>)> W4ErrorCases() =>
    [
        Browse("browser.w4.errors.a-provider-error-shows-on-the-playground-compare-and-bench", AgentMode.FauxMetered, async (app, window) =>
        {
            var page = window.Page;

            // The Playground: the error card, then its Details.
            await page.GotoAsync("/playground");
            var output = page.Locator("[data-run-output]");
            await page.Locator("[data-msg][data-role='user'] [data-msg-text]").First.FillAsync("error: " + FauxError);
            await page.Locator("[data-run]").ClickAsync();
            await Expect(output).ToHaveAttributeAsync("data-state", "error", new() { Timeout = 30_000 });
            var card = output.Locator("[data-output-error]");
            await Expect(card).ToContainTextAsync(FauxError);
            var id = await page.QueryAsync("run");
            Equal("error", await app.RunEndedAsync(id), "the run ended in an error");
            var details = await page.OpenModalAsync(card.Locator("[data-error-details]"), "modal-error-details");
            await Expect(details).ToContainTextAsync(FauxError);
            await page.DismissModalAsync("modal-error-details");

            // Compare: every column fails on its own, and the group ends.
            await page.GotoAsync("/compare");
            var columns = page.Locator("[data-compare-col]");
            await Expect(columns).ToHaveCountAsync(2);
            await page.Locator("#cmpPrompt").FillAsync("error: " + FauxError);
            await page.Locator("[data-run-all]").ClickAsync();
            for (var i = 0; i < 2; i++)
            {
                await Expect(columns.Nth(i)).ToHaveAttributeAsync("data-state", "error", new() { Timeout = 30_000 });
                await Expect(columns.Nth(i).Locator("[data-col-error]")).ToContainTextAsync(FauxError);
                Equal("error", await app.RunEndedAsync((await columns.Nth(i).GetAttributeAsync("data-run-id"))!), $"column {i + 1}'s run ended in an error");
            }

            // The Tool Bench: a tool from a template, then a turn that fails.
            await page.GotoAsync("/bench");
            var bench = page.Locator("[data-bench]");
            await page.Locator("[data-bench-part='tools'] [data-bs-toggle='dropdown'] >> visible=true").First.ClickAsync();
            var editor = await page.OpenModalAsync(page.Locator("[data-bench-template*='get_weather'] >> visible=true").First, "modal-tool-editor");
            await editor.Locator("[data-tool-submit]").ClickAsync();
            await Expect(editor).ToBeHiddenAsync();
            await page.Locator("#benchUserMsg").FillAsync("error: " + FauxError);
            await page.Locator("[data-bench-add-run]").ClickAsync();
            await Expect(bench).ToHaveAttributeAsync("data-bench-state", "error", new() { Timeout = 30_000 });
            await Expect(page.Locator("[data-bench-part='timeline']")).ToContainTextAsync(FauxError);
        }),

        Browse("browser.w4.errors.the-error-details-fit-a-phone-in-both-themes", AgentMode.FauxMetered, async (app, window) =>
        {
            var failed = await app.PlaygroundRunAsync("error: " + FauxError);
            Equal("error", await app.RunEndedAsync(failed), "a failed Playground run");
            var page = window.Page;
            foreach (var theme in new[] { "dark", "light" })
            {
                await page.GotoAsync("/playground?run=" + failed);
                if (theme == "light") await page.UseThemeAsync("light");
                var output = page.Locator("[data-run-output]");
                await Expect(output).ToHaveAttributeAsync("data-state", "error");
                await Expect(output.Locator("[data-output-error]")).ToContainTextAsync(FauxError);
                await page.FitsPhoneAsync("Playground › the error card", theme);
                var details = await page.OpenModalAsync(output.Locator("[data-error-details] >> visible=true").First, "modal-error-details");
                await Expect(details).ToContainTextAsync(FauxError);
                await page.FitsPhoneAsync("Playground › modal-error-details", theme);
                await page.DismissModalAsync("modal-error-details");
            }
        }, width: 375, height: 812),
    ];
}
