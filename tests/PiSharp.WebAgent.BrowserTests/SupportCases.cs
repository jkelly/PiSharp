using static Microsoft.Playwright.Assertions;

// The suite's own guard (decision 0011, run W2, WU10): a page error or a console error in any tab fails the case. This case plants one of
// each and checks that the guard reports both, so a page that logs an error can never pass unnoticed.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SupportCases() =>
    [
        Case("browser.support.a-page-or-console-error-fails-the-case", async () =>
        {
            await using var agent = await Agent.StartAsync("support");
            await using var window = await Window.OpenAsync(agent, 1440, 900);
            var page = window.Page;
            await page.GotoAsync("/chat/new");
            await Expect(page.Locator("[data-composer-input]")).ToBeVisibleAsync();
            window.NoErrors();

            var other = await window.NewPageAsync();
            await other.GotoAsync("/settings/providers");
            await page.EvaluateAsync("() => console.error('planted console error')");
            await other.EvaluateAsync("() => { setTimeout(() => { throw new Error('planted page error'); }, 0); }");
            await WaitUntilAsync(() => Task.FromResult(window.ErrorCount >= 2), "both planted errors are recorded");
            try
            {
                window.NoErrors();
            }
            catch (InvalidOperationException error)
            {
                Check(error.Message.Contains("tab 1 console error", StringComparison.Ordinal) && error.Message.Contains("planted console error", StringComparison.Ordinal),
                    "the console error is reported with its tab: " + error.Message);
                Check(error.Message.Contains("tab 2 page error", StringComparison.Ordinal) && error.Message.Contains("planted page error", StringComparison.Ordinal),
                    "the page error is reported with its tab: " + error.Message);
                return;
            }
            throw new InvalidOperationException("The guard passed a window whose tabs logged errors.");
        }),
    ];
}
