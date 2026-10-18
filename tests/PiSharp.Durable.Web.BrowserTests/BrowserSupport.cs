using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

internal static partial class Program
{
    private static IPlaywright? playwright;
    private static IBrowser? browser;

    /// <summary>Where failed cases leave a screenshot and the page's HTML; null keeps nothing.</summary>
    internal static string? Artifacts { get; private set; }

    /// <summary>One headless Chromium for the whole run.</summary>
    private static async Task<IBrowser> BrowserAsync()
    {
        if (browser is not null) return browser;
        playwright = await Playwright.CreateAsync();
        SetDefaultExpectTimeout(15_000);
        return browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    private static async Task CloseBrowserAsync()
    {
        if (browser is not null) await browser.DisposeAsync();
        playwright?.Dispose();
    }

    /// <summary>A case on a fresh hub and device, in a fresh browser profile (<see cref="Window"/>). Every case ends with Appendix C item 28:
    /// no page errors in any of its tabs.</summary>
    internal static (string, Func<Task>) Browse(string id, Func<Hub, Window, Task> run, int width = 1440, int height = 900) => Case(id, async () =>
    {
        await using var hub = await Hub.StartAsync(id.Replace("browser.", "", StringComparison.Ordinal));
        await using var window = await Window.OpenAsync(hub, width, height);
        try
        {
            await run(hub, window);
            window.NoPageErrors();
        }
        catch
        {
            await window.SaveAsync(id);
            throw;
        }
    });

    /// <summary>A browser profile on the hub: its own local and session storage. Every tab records uncaught errors.</summary>
    internal sealed class Window : IAsyncDisposable
    {
        private readonly List<string> errors = [];
        private readonly List<IPage> pages = [];

        private Window(Hub hub, IBrowserContext context)
        {
            Hub = hub;
            Context = context;
        }

        public Hub Hub { get; }
        public IBrowserContext Context { get; }
        public IPage Page { get; private set; } = null!;

        public static async Task<Window> OpenAsync(Hub hub, int width, int height)
        {
            var context = await (await BrowserAsync()).NewContextAsync(new BrowserNewContextOptions
            {
                BaseURL = hub.Base.ToString(),
                ViewportSize = new ViewportSize { Width = width, Height = height },
                ReducedMotion = ReducedMotion.Reduce,
            });
            context.SetDefaultTimeout(15_000);
            var window = new Window(hub, context);
            window.Page = await window.NewPageAsync();
            return window;
        }

        /// <summary>Another tab of this profile.</summary>
        public async Task<IPage> NewPageAsync()
        {
            var page = await Context.NewPageAsync();
            page.PageError += (_, error) => { lock (errors) errors.Add(error); };
            pages.Add(page);
            return page;
        }

        public void NoPageErrors()
        {
            lock (errors) Check(errors.Count == 0, "page errors: " + string.Join(" | ", errors));
        }

        public async Task SaveAsync(string id)
        {
            if (Artifacts is null) return;
            Directory.CreateDirectory(Artifacts);
            for (var i = 0; i < pages.Count; i++)
            {
                if (pages[i].IsClosed) continue;
                var name = Path.Join(Artifacts, id + (i == 0 ? "" : "-tab" + (i + 1)));
                try
                {
                    await pages[i].ScreenshotAsync(new PageScreenshotOptions { Path = name + ".png", FullPage = true });
                    await File.WriteAllTextAsync(name + ".html", await pages[i].ContentAsync());
                }
                catch (PlaywrightException) { /* The page went away. */ }
            }
            lock (errors) if (errors.Count > 0) File.WriteAllLines(Path.Join(Artifacts, id + "-errors.txt"), errors);
            await File.WriteAllTextAsync(Path.Join(Artifacts, id + "-hub.log"), Hub.Log);
        }

        public async ValueTask DisposeAsync() => await Context.DisposeAsync();
    }

    // ─── Page helpers ────────────────────────────────────────────────────────

    internal static string AgentPath(string session, long agent = 1) => $"/d/local/s/{session}/a/{agent}";

    /// <summary>Opens an agent by its deep link and waits for its first view and the Session tab.</summary>
    internal static async Task OpenAgentAsync(this IPage page, string session, long agent = 1)
    {
        await page.GotoAsync(AgentPath(session, agent));
        await page.AgentReadyAsync();
    }

    internal static async Task AgentReadyAsync(this IPage page)
    {
        await Expect(page.Locator("#chatScroll")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-panel='session'] [data-session-tab]")).ToBeAttachedAsync();
        await Expect(page.Locator("[data-connection]")).ToHaveTextAsync("Connected");
    }

    /// <summary>Types a message into the composer and presses Enter; returns once the device took it (the box empties).</summary>
    internal static async Task SendAsync(this IPage page, string text)
    {
        var box = page.Locator("#composerInput");
        await box.FillAsync(text);
        await box.PressAsync("Enter");
        await Expect(box).ToHaveValueAsync("");
    }

    internal static ILocator Thread(this IPage page) => page.Locator("#thread");

    /// <summary>The newest toast reads <paramref name="text"/> (a substring).</summary>
    internal static Task ToastAsync(this IPage page, string text) => Expect(page.Locator("#toast .toast-text")).ToContainTextAsync(text);

    /// <summary>Shows an inspector tab: session, state, tasks, tools or extend.</summary>
    internal static async Task TabAsync(this IPage page, string name)
    {
        await page.Locator($".ins-tabs [data-show-pane='ins-{name}']").ClickAsync();
        await Expect(page.Locator("#insPanes")).ToHaveAttributeAsync("data-active-pane", "ins-" + name);
    }

    /// <summary>A row of the network tree.</summary>
    internal static ILocator Row(this IPage page, string session, long? agent = null) => agent is { } id
        ? page.Locator($".tree-row.agent[data-session='{session}'][data-agent='{id}']")
        : page.Locator($".tree-row.session[data-session='{session}']");

    /// <summary>A job card in the chat.</summary>
    internal static ILocator JobCard(this IPage page, string title) => page.Locator("#thread .job-card").Filter(new LocatorFilterOptions { HasText = title });

    /// <summary>Submits the prompt dialog (rename, fork, archive) with <paramref name="value"/> in its field when it has one.</summary>
    internal static async Task PromptAsync(this IPage page, string? value)
    {
        var dialog = page.Locator("#modal-prompt");
        await Expect(dialog).ToBeVisibleAsync();
        if (value is not null) await page.Locator("#pr-input").FillAsync(value);
        await page.Locator("[data-pr-submit]").ClickAsync();
        await Expect(dialog).ToBeHiddenAsync();
    }

    /// <summary>Whether the page scrolls sideways.</summary>
    internal static Task<bool> OverflowsAsync(this IPage page) =>
        page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth || document.body.scrollWidth > window.innerWidth");
}
