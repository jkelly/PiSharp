using System.Collections.Concurrent;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Settings in a browser (decision 0011, run W2, WU10): an API key typed into Settings › Providers › OpenAI is posted once, the page then shows
// only its masked hint, and the key is nowhere in the browser afterwards: not in the DOM, not in a form field, not in local or session
// storage, not in a URL, and not in any page or API answer the app serves. The key is planted (a made-up value in OpenAI's format); in faux
// mode nothing calls a provider, and the connection test is turned off.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SettingsCases() =>
    [
        Browse("browser.settings.an-api-key-shows-only-its-masked-hint", async (agent, window) =>
        {
            var secret = "sk-proj-PLANTED" + Guid.NewGuid().ToString("N") + "Zq9Lw2";
            var hint = "sk-proj-…" + secret[^4..];
            var page = window.Page;
            var requests = new ConcurrentBag<IRequest>();
            page.Request += (_, request) => requests.Add(request);

            await page.GotoAsync("/settings/providers/openai");
            await page.GetByRole(AriaRole.Button, new() { Name = "Store a key", Exact = true }).ClickAsync();
            var modal = page.Locator("#modal-api-key");
            await Expect(modal).ToBeVisibleAsync();
            await Expect(modal.Locator("[data-key-title]")).ToHaveTextAsync("OpenAI");
            var field = modal.Locator("#apiKeyInput");
            await Expect(field).ToHaveAttributeAsync("type", "password");
            await Expect(field).ToHaveValueAsync("");
            await Expect(modal.Locator("[data-key-test-button]")).ToBeDisabledAsync();
            await field.FillAsync(secret);
            await modal.Locator("#apiKeyLabel").FillAsync("Browser key");

            // Saving reloads the page, which then shows the new account's card.
            await modal.Locator("[data-key-save]").ClickAsync();
            await Expect(page.Locator("[data-account-hint]")).ToHaveTextAsync(hint);
            await Expect(page.Locator("[data-account-name]")).ToHaveTextAsync("Browser key");
            await page.ToastAsync("Stored as " + hint);
            await NoSecretInPageAsync(page, secret, "the provider page after saving");

            // Opened again, the modal starts empty: the key never comes back to the page.
            await page.GetByRole(AriaRole.Button, new() { Name = "Add key", Exact = true }).ClickAsync();
            await Expect(modal).ToBeVisibleAsync();
            await Expect(field).ToHaveValueAsync("");
            await Expect(field).ToHaveAttributeAsync("type", "password");
            await modal.Locator(".modal-footer [data-bs-dismiss='modal']").ClickAsync();
            await Expect(modal).ToBeHiddenAsync();

            // Settings › Accounts lists the subscription sign-ins; API keys stay under Providers. Neither page carries the key.
            await page.GotoAsync("/settings/accounts");
            await NoSecretInPageAsync(page, secret, "Settings › Accounts");
            await page.GotoAsync("/settings/providers");
            await Expect(page.Locator("[data-provider-row='openai']").First).ToHaveAttributeAsync("data-state", "connected");
            await NoSecretInPageAsync(page, secret, "Settings › Providers");

            // The browser sent the key exactly once, in the body of the request that stored it, and never in a URL.
            var sent = requests.Where(request => (request.PostData ?? "").Contains(secret, StringComparison.Ordinal)).ToList();
            Equal(1, sent.Count, "requests that carried the key");
            Equal("POST", sent[0].Method, "the storing request's method");
            Equal("/api/credentials/accounts", new Uri(sent[0].Url).AbsolutePath, "the storing request");
            Check(!requests.Any(request => request.Url.Contains(secret, StringComparison.Ordinal) ||
                Uri.UnescapeDataString(request.Url).Contains(secret, StringComparison.Ordinal)), "no URL carries the key");

            // What the app serves the browser, read on the server's side: the pages and the account listings.
            foreach (var path in new[] { "/settings/providers/openai", "/settings/accounts", "/settings/providers", "/chat/new",
                         "/api/credentials/accounts", "/api/credentials/accounts?provider=openai" })
            {
                var served = await agent.Get(path);
                Equal(200, served.Status, path);
                Check(!served.Text.Contains(secret, StringComparison.Ordinal), path + " leaves the key out");
                Check(!served.Text.Contains(secret["sk-proj-".Length..^4], StringComparison.Ordinal), path + " leaves out every hidden part of the key");
            }
            Check(!agent.Log.Contains(secret, StringComparison.Ordinal), "the app's log leaves the key out");
        }),
    ];

    /// <summary>The key is nowhere in the page: its HTML, its text, any field's value, local or session storage.</summary>
    private static async Task NoSecretInPageAsync(IPage page, string secret, string what)
    {
        Check(!(await page.ContentAsync()).Contains(secret, StringComparison.Ordinal), what + ": the HTML leaves the key out");
        var found = await page.EvaluateAsync<string[]>("""
            (secret) => {
              const where = [];
              if (document.body.innerText.includes(secret)) where.push('text');
              for (const field of document.querySelectorAll('input, textarea, select'))
                if (String(field.value ?? '').includes(secret)) where.push('field ' + (field.id || field.name || field.tagName));
              const dump = (store, name) => {
                try {
                  for (let i = 0; i < store.length; i++) {
                    const key = store.key(i);
                    if (String(key).includes(secret) || String(store.getItem(key)).includes(secret)) where.push(name + ' ' + key);
                  }
                } catch { /* storage off */ }
              };
              dump(window.localStorage, 'localStorage');
              dump(window.sessionStorage, 'sessionStorage');
              return where;
            }
            """, secret);
        Check(found.Length == 0, what + ": the key is in " + string.Join(", ", found));
    }
}
