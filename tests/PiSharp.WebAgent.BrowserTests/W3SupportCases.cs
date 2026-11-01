using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W3, T3; W3 fix): the page helpers of the W3 browser cases, and the support case of the refusal count. The W3 cases run
// on the shared Agent (Agent.cs: --faux, --faux-metered, or a sealed live app) and Window (BrowserSupport.cs), which T3's W3App and W3Window
// were merged into. A send the Spend Guard refuses is answered 200 {posted: false} (nothing was posted), so the browser logs no error for it
// and every case still fails on any page or console error; Window.Refusals counts those answers. The support case below plants a refused
// send, a 409 on another path and a 404, and checks that the refusal is counted and logs nothing while the other two still fail the case.
// W4 L3: two waits the flaky cases lacked. ModalShownAsync waits until a Bootstrap modal has finished opening before a case presses Escape
// (Bootstrap ignores hide() while the show transition runs, so an early Escape left the dialog open), and SettledLedgerAsync waits until the
// ledger rows a case reads have ended (the guard settles a call as its stream closes, which can come a moment after the answer is stored).
// W4 fix 2: FitsPhoneAsync also checks that no open dialog's footer button comes within 16 px of the dialog's sides (FooterEdgeGapAsync).
internal static partial class Program
{
    // ─── Page helpers ────────────────────────────────────────────────────────

    /// <summary>Waits until the Bootstrap modal <paramref name="id"/> has finished opening (<c>shown.bs.modal</c>: its show transition is
    /// over and its focus trap holds the focus), so Escape closes it. Bootstrap ignores <c>hide()</c>, and so Escape, while the modal is
    /// still opening (W4 L3).</summary>
    internal static async Task ModalShownAsync(this IPage page, string id)
    {
        await Expect(page.Locator($"#{id}.show")).ToBeVisibleAsync();
        var shown = await page.EvaluateAsync<bool>("""
            (id) => new Promise((resolve) => {
              const el = document.getElementById(id);
              const modal = window.bootstrap?.Modal.getInstance(el);
              if (!el || !modal) { resolve(false); return; }
              const settled = () => el.classList.contains('show') && !modal._isTransitioning && el.contains(document.activeElement);
              if (settled()) { resolve(true); return; }
              const timer = setTimeout(() => resolve(settled()), 10000);
              el.addEventListener('shown.bs.modal', () => { clearTimeout(timer); resolve(true); }, { once: true });
            })
            """, id);
        Check(shown, $"#{id} finished opening (shown.bs.modal)");
    }

    /// <summary>Presses Escape on the open modal <paramref name="id"/> once it has finished opening, and waits until it is closed.</summary>
    internal static async Task EscapeModalAsync(this IPage page, string id)
    {
        await page.ModalShownAsync(id);
        await page.Keyboard.PressAsync("Escape");
        await Expect(page.Locator("#" + id)).ToBeHiddenAsync();
    }

    /// <summary>The ledger's rows (newest first) once it holds <paramref name="count"/> and none of them is still reserved: the guard settles
    /// a call as its stream closes, a moment after the durable engine may have stored the answer (W4 L3).</summary>
    internal static async Task<List<JsonObject>> SettledLedgerAsync(this Agent app, int count)
    {
        List<JsonObject> rows = [];
        await WaitUntilAsync(async () => (rows = await app.LedgerAsync()).Count == count && rows.All(row => Reservation(row, "state") != "reserved"),
            $"the ledger holds {count} ended row(s)", TimeSpan.FromSeconds(30));
        return rows;
    }

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
        var footer = await page.FooterEdgeGapAsync();
        Check(footer is null, $"{what} ({theme}): a dialog's footer button sits too close to the dialog's edge: {footer}");
    }

    /// <summary>The smallest gap allowed between a dialog footer's buttons and the dialog's sides (W4 fix 2: the footer lost its padding and
    /// its last button sat 5 px from the edge; with the 16 px gutter and the buttons' .25rem margin it is 20 px, as the body's).</summary>
    internal const int FooterEdgeGap = 16;

    /// <summary>The first open dialog whose footer has a visible control closer than <see cref="FooterEdgeGap"/> px to the dialog's left or
    /// right edge, described; null when every open dialog's footer keeps its gutter.</summary>
    internal static Task<string?> FooterEdgeGapAsync(this IPage page) => page.EvaluateAsync<string?>("""
        (min) => {
          for (const content of document.querySelectorAll('.modal.show .modal-content')) {
            const box = content.getBoundingClientRect();
            for (const footer of content.querySelectorAll('.modal-footer')) {
              for (const control of footer.querySelectorAll('button, a, .btn')) {
                const r = control.getBoundingClientRect();
                if (r.width === 0 || r.height === 0 || getComputedStyle(control).visibility === 'hidden') continue;
                const left = Math.round(r.left - box.left), right = Math.round(box.right - r.right);
                if (left < min || right < min) {
                  const name = content.closest('.modal')?.id ?? '?';
                  return `#${name} "${(control.textContent || '').trim()}" left ${left} px, right ${right} px`;
                }
              }
            }
          }
          return null;
        }
        """, FooterEdgeGap);

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
