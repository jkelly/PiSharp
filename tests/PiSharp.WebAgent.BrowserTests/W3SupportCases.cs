using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W3, T3; W3 fix): the page helpers of the W3 browser cases, and the support case of the refusal count. The W3 cases run
// on the shared Agent (Agent.cs: --faux, --faux-metered, or a sealed live app) and Window (BrowserSupport.cs), which T3's W3App and W3Window
// were merged into. A send the Spend Guard refuses is answered 200 {posted: false} (nothing was posted), so the browser logs no error for it
// and every case still fails on any page or console error; Window.Refusals counts those answers. The support case below plants a refused
// send, a 409 on another path and a 404, and checks that the refusal is counted and logs nothing while the other two still fail the case.
internal static partial class Program
{
    // ─── Page helpers ────────────────────────────────────────────────────────

    /// <summary>Stores <paramref name="theme"/> as this browser's choice (as Settings › Appearance and the top bar's toggle do) and reloads.</summary>
    internal static async Task UseThemeAsync(this IPage page, string theme)
    {
        await page.EvaluateAsync("(theme) => window.PiSharpAppearance.setTheme(theme)", theme);
        await page.ReloadAsync();
        await Expect(page.Locator("html")).ToHaveAttributeAsync("data-bs-theme", theme);
    }

    /// <summary>The page shows <paramref name="theme"/>, does not scroll sideways, and no open dialog or dropdown menu sticks out of the
    /// viewport (a menu placed past the left edge makes no scrollbar, so the page alone would not show it; W3 fix, tests-ci).</summary>
    internal static async Task FitsPhoneAsync(this IPage page, string what, string theme)
    {
        await Expect(page.Locator("html")).ToHaveAttributeAsync("data-bs-theme", theme);
        if (await page.OverflowsAsync())
            throw new InvalidOperationException($"{what} ({theme}) scrolls sideways at {page.ViewportSize?.Width}px: " + await page.WideElementsAsync());
        var dialog = await page.EvaluateAsync<string?>("""
            () => {
              for (const el of document.querySelectorAll('.modal.show .modal-dialog, .dropdown-menu.show')) {
                const r = el.getBoundingClientRect();
                const name = el.classList.contains('modal-dialog') ? el.parentElement.id : '.' + [...el.classList].join('.');
                if (r.left < -1 || r.right > window.innerWidth + 1) return `${name} (${Math.round(r.left)}..${Math.round(r.right)})`;
              }
              return null;
            }
            """);
        Check(dialog is null, $"{what} ({theme}): the open dialog or menu does not fit the screen: {dialog}");
    }

    /// <summary>Whether any file under <paramref name="folder"/> holds <paramref name="secret"/> in plain text (UTF-8 or UTF-16), read while
    /// the app may still have them open.</summary>
    internal static List<string> FilesWith(string folder, string secret)
    {
        var found = new List<string>();
        var utf8 = Encoding.UTF8.GetBytes(secret);
        var utf16 = Encoding.Unicode.GetBytes(secret);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            byte[] bytes;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                bytes = copy.ToArray();
            }
            catch (IOException) { continue; }
            if (bytes.AsSpan().IndexOf(utf8) >= 0 || bytes.AsSpan().IndexOf(utf16) >= 0) found.Add(Path.GetRelativePath(folder, file));
        }
        return found;
    }

    private static IEnumerable<(string, Func<Task>)> W3SupportCases() =>
    [
        Case("browser.w3.support.a-refused-send-is-counted-and-logs-nothing", async () =>
        {
            await using var app = await Agent.StartAsync("w3.support", AgentMode.FauxMetered);
            await using var window = await Window.OpenAsync(app, 1440, 900);
            var cap = await app.PostAsync("/api/spend/caps", new JsonObject { ["scope"] = "everything", ["period"] = "request", ["limit"] = "0.01", ["action"] = "block" });
            Equal(200, cap.Status, "a one-cent cap per request: " + cap.Text);
            var page = window.Page;
            await page.GotoAsync("/chat/new");
            await Expect(page.Locator("[data-composer-input]")).ToBeVisibleAsync();

            // A guarded send the cap refuses is a 200 {posted: false}: counted, never logged. A 409 from another path and a 404 still are.
            var answers = await page.EvaluateAsync<string[]>("""
                async () => {
                  const json = { 'Content-Type': 'application/json' };
                  const refused = await fetch('/api/chats', { method: 'POST', headers: json, body: JSON.stringify({ requestId: 'support-1', text: 'over the cap', spend: { check: true } }) });
                  const body = await refused.json();
                  const conflict = await fetch('/api/spend/caps', { method: 'POST', headers: json, body: JSON.stringify({ scope: 'everything', period: 'request', limit: '0.02', action: 'block' }) });
                  const missing = await fetch('/no-such-page');
                  return [String(refused.status), String(body.posted), body.code, String(conflict.status), String(missing.status)];
                }
                """);
            Equal("200,false,spend_blocked,409,404", string.Join(",", answers), "the planted answers");
            await WaitUntilAsync(() => Task.FromResult(window.ErrorCount >= 2 && window.Refusals.Count >= 1), "every answer is recorded");
            Equal("/api/chats", string.Join(",", window.Refusals), "the refused send is counted");
            Equal(0, (await app.Get("/api/chats")).Body?["items"]?.AsArray().Count ?? 0, "the refused send created no chat");
            try
            {
                window.NoErrors();
            }
            catch (InvalidOperationException error)
            {
                Check(error.Message.Contains("/api/spend/caps", StringComparison.Ordinal) && error.Message.Contains("409", StringComparison.Ordinal),
                    "a 409 from another path is reported: " + error.Message);
                Check(error.Message.Contains("no-such-page", StringComparison.Ordinal) && error.Message.Contains("404", StringComparison.Ordinal),
                    "a missing page is reported: " + error.Message);
                Check(!error.Message.Contains("/api/chats", StringComparison.Ordinal), "the refused send logs nothing: " + error.Message);
                return;
            }
            throw new InvalidOperationException("The guard passed a window whose tab logged unexpected errors.");
        }),
    ];
}
