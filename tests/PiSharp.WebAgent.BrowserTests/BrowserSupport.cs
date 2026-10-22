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

    /// <summary>A case on a fresh app (faux, its own data and work folders) in a fresh browser profile (<see cref="Window"/>). Every case ends
    /// with no page error and no console error in any of its tabs.</summary>
    internal static (string, Func<Task>) Browse(string id, Func<Agent, Window, Task> run, int width = 1440, int height = 900, string approvalMode = "ask") =>
        Case(id, async () =>
        {
            await using var agent = await Agent.StartAsync(id.Replace("browser.", "", StringComparison.Ordinal), approvalMode);
            await using var window = await Window.OpenAsync(agent, width, height);
            try
            {
                await run(agent, window);
                window.NoErrors();
            }
            catch
            {
                await window.SaveAsync(id);
                throw;
            }
        });

    /// <summary>A browser profile on the app: its own local and session storage. Every tab records uncaught errors and every console error
    /// (failed loads, refused scripts or styles under the Content-Security-Policy, errors a script logs).</summary>
    internal sealed class Window : IAsyncDisposable
    {
        private readonly List<string> errors = [];
        private readonly List<IPage> pages = [];

        private Window(Agent agent, IBrowserContext context)
        {
            Agent = agent;
            Context = context;
        }

        public Agent Agent { get; }
        public IBrowserContext Context { get; }
        public IPage Page { get; private set; } = null!;

        public static async Task<Window> OpenAsync(Agent agent, int width, int height)
        {
            var context = await (await BrowserAsync()).NewContextAsync(new BrowserNewContextOptions
            {
                BaseURL = agent.Base.ToString(),
                ViewportSize = new ViewportSize { Width = width, Height = height },
                ReducedMotion = ReducedMotion.Reduce,
            });
            context.SetDefaultTimeout(15_000);
            var window = new Window(agent, context);
            window.Page = await window.NewPageAsync();
            return window;
        }

        /// <summary>Another tab of this profile.</summary>
        public async Task<IPage> NewPageAsync()
        {
            var page = await Context.NewPageAsync();
            var tab = pages.Count + 1;
            page.PageError += (_, error) => { lock (errors) errors.Add($"tab {tab} page error: {error}"); };
            page.Console += (_, message) =>
            {
                if (message.Type == "error") lock (errors) errors.Add($"tab {tab} console error at {message.Location}: {message.Text}");
            };
            pages.Add(page);
            return page;
        }

        /// <summary>The page and console errors recorded so far.</summary>
        public int ErrorCount { get { lock (errors) return errors.Count; } }

        public void NoErrors()
        {
            lock (errors) Check(errors.Count == 0, "page or console errors: " + string.Join(" | ", errors));
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
            await File.WriteAllTextAsync(Path.Join(Artifacts, id + "-app.log"), Agent.Log);
        }

        public async ValueTask DisposeAsync() => await Context.DisposeAsync();
    }

    // ─── Page helpers ────────────────────────────────────────────────────────

    /// <summary>The words <c>w001 w002 …</c> of a faux <c>slow:</c> answer (one word every 40 ms).</summary>
    internal static string Words(int count) => string.Join(' ', Enumerable.Range(1, count).Select(index => $"w{index:000}"));

    /// <summary>Opens a chat and waits until its stream is connected (the first view frame arrived).</summary>
    internal static async Task OpenChatAsync(this IPage page, string chatId, long? agent = null)
    {
        await page.GotoAsync("/chat/" + Uri.EscapeDataString(chatId) + (agent is { } id ? "?agent=" + id : ""));
        await page.ChatReadyAsync();
    }

    internal static async Task ChatReadyAsync(this IPage page)
    {
        await Expect(page.Locator("#chatLayout")).ToBeVisibleAsync();
        await Expect(page.Locator("#chatLayout")).ToHaveAttributeAsync("data-stream", "connected");
    }

    internal static ILocator Layout(this IPage page) => page.Locator("#chatLayout");

    /// <summary>Whether a chat page may reload when its busy flag outlives the run (see <see cref="IdleAsync"/>). The view frame's busy state
    /// includes the device's last-seen running flag (DeviceHostService.IsBusy, polled every 250 ms); AgentViewHub now renders again on
    /// DeviceHostService.ActivityChanged (W2 integration), so a page that keeps showing Stop after the answer ended is a failure again.</summary>
    internal static readonly bool ToleratePageBusyLag = false;

    /// <summary>The run is over: the server says the chat is idle and the page shows it (data-busy="false", Send instead of Stop).</summary>
    internal static async Task IdleAsync(this IPage page, Agent agent, string chatId)
    {
        await agent.WaitIdleAsync(chatId);
        try
        {
            await Expect(page.Layout()).ToHaveAttributeAsync("data-busy", "false", new() { Timeout = ToleratePageBusyLag ? 5_000 : 15_000 });
        }
        catch (PlaywrightException) when (ToleratePageBusyLag)
        {
            Console.Error.WriteLine($"note: chat {chatId} stayed busy in the page after its run ended (the busy-lag request); reloading it");
            await page.ReloadAsync();
            await page.ChatReadyAsync();
            await Expect(page.Layout()).ToHaveAttributeAsync("data-busy", "false");
        }
        await Expect(page.Locator("[data-composer] [data-send]")).ToBeVisibleAsync();
    }

    internal static ILocator Answers(this IPage page) => page.Locator("[data-thread] article.msg-assistant");

    internal static ILocator Thread(this IPage page) => page.Locator("[data-thread]");

    /// <summary>Types a message into the composer and presses Enter; returns once the app took it (the box empties).</summary>
    internal static async Task SendAsync(this IPage page, string text)
    {
        var box = page.Locator("[data-composer-input]");
        await box.FillAsync(text);
        await box.PressAsync("Enter");
        await Expect(box).ToHaveValueAsync("");
    }

    /// <summary>The newest toast's text has <paramref name="text"/>.</summary>
    internal static Task ToastAsync(this IPage page, string text) => Expect(page.Locator("#toasts .toast").Last).ToContainTextAsync(text);

    /// <summary>Whether the page scrolls sideways.</summary>
    internal static Task<bool> OverflowsAsync(this IPage page) =>
        page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth || document.body.scrollWidth > window.innerWidth");

    /// <summary>The elements wider than the viewport, for a failure message.</summary>
    internal static Task<string> WideElementsAsync(this IPage page) => page.EvaluateAsync<string>("""
        () => [...document.querySelectorAll('body *')]
          .filter((el) => { const r = el.getBoundingClientRect(); return r.width > 0 && r.right > window.innerWidth + 1; })
          .slice(0, 8)
          .map((el) => `${el.tagName.toLowerCase()}${el.id ? '#' + el.id : ''}.${[...el.classList].join('.')} (right ${Math.round(el.getBoundingClientRect().right)})`)
          .join(', ')
        """);
}
