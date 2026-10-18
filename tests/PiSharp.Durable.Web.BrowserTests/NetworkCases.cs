using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

internal static partial class Program
{
    private static readonly Regex Selected = new(@"(^|\s)sel(\s|$)");

    /// <summary>The panel hotkeys: Ctrl on a Mac, Alt+Shift elsewhere (app.js reads navigator.platform).</summary>
    private static string Hotkey(string code) => (OperatingSystem.IsMacOS() ? "Control+" : "Alt+Shift+") + code;

    // The network: devices, the tree, sessions, selection, rails and layouts (PLAN.md Appendix C items 1-3, 18, 19, 21-27, 32 and 33).
    internal static IEnumerable<(string, Func<Task>)> NetworkCases() =>
    [
        // Items 1, 2 and 23: a device from dpi-network.yaml has no token until one is entered; a wrong token is refused; Check finds it online.
        Browse("browser.devices-yaml-token-and-check", async (hub, window) =>
        {
            var page = window.Page;
            await page.GotoAsync("/devices");
            await page.Locator("[data-action='import']").ClickAsync();
            await page.Locator("#im-yaml").FillAsync($"devices:\n  faux-copy:\n    url: {hub.DeviceUrl.ToString().TrimEnd('/')}\n");
            await page.Locator("[data-im-preview]").ClickAsync();
            await Expect(page.Locator("[data-im-rows]")).ToContainTextAsync("Will be added");
            await page.Locator("[data-im-apply]").ClickAsync();
            await page.ToastAsync("1 device imported");
            await Expect(page.Locator("[data-device-row='faux-copy']")).ToBeVisibleAsync();

            await page.GotoAsync("/");
            var copy = page.Locator(".tree-row.device[data-device='faux-copy']");
            await Expect(copy.Locator(".row-status")).ToHaveTextAsync("token rejected");
            await Expect(page.Locator("[data-online-summary]")).ToHaveTextAsync("1 of 2 online");
            await copy.Locator(".row-name").ClickAsync();
            var center = page.Locator("#offlineCenter");
            await Expect(center).ToContainTextAsync("faux-copy rejected its token");
            await Expect(center.Locator("#cmd-token")).ToHaveTextAsync("pisharp durable token");
            await center.Locator("a", new() { HasText = "Enter token" }).ClickAsync();

            await Expect(page.Locator("#modal-token")).ToBeVisibleAsync();
            await page.Locator("#tk-value").FillAsync("dpi_" + new string('x', 43));
            await page.Locator("#tk-form button[type='submit']").ClickAsync();
            await Expect(page.Locator("[data-tk-error]")).ToBeVisibleAsync();
            await page.Locator("#tk-value").FillAsync(hub.Token);
            await page.Locator("#tk-form button[type='submit']").ClickAsync();
            await Expect(page.Locator("#modal-token")).ToBeHiddenAsync();
            await page.ToastAsync("faux-copy is");

            await page.GotoAsync("/");
            await Expect(page.Locator("[data-online-summary]")).ToHaveTextAsync("2 of 2 online");
            copy = page.Locator(".tree-row.device[data-device='faux-copy']");
            await copy.HoverAsync();
            await copy.Locator("[data-action='check-device']").ClickAsync();
            await page.ToastAsync("faux-copy is online.");
        }),

        // Items 3, 18 and 25: the New session dialog, the empty agent, New agent, and the breadcrumb's kinds.
        Browse("browser.new-session-dialog-and-new-agent", async (hub, window) =>
        {
            var page = window.Page;
            await page.GotoAsync("/");
            await Expect(page.Locator("[data-connection]")).ToHaveTextAsync("Connected");
            await page.Locator("header [data-action='new-session']").ClickAsync();
            var dialog = page.Locator("#modal-new-session");
            await Expect(dialog).ToBeVisibleAsync();
            await Expect(page.Locator("#ns-device")).ToHaveValueAsync("local");
            await Expect(page.Locator("[data-dir-list] .dir-item").First).ToBeVisibleAsync();
            await Expect(page.Locator("#ns-cwd")).Not.ToHaveValueAsync("");
            await Expect(page.Locator("#ns-provider")).ToHaveValueAsync("faux");
            await Expect(page.Locator("#ns-model")).ToHaveValueAsync("echo");
            await page.Locator("#ns-name").FillAsync("release-notes");
            await page.Locator("[data-ns-create]").ClickAsync();
            await Expect(dialog).ToBeHiddenAsync();
            await page.ToastAsync("Session release-notes created on local");
            await Expect(page.Locator(".tree-row.session .row-name", new() { HasText = "release-notes" })).ToBeVisibleAsync();
            await Expect(page).ToHaveURLAsync(new Regex(@"/d/local/s/[^/]+/a/1$"));
            await page.AgentReadyAsync();
            await Expect(page.Locator("[data-empty-hero]")).ToBeVisibleAsync();
            var crumbs = page.Locator("[data-crumbs]");
            await Expect(crumbs).ToContainTextAsync("device local");
            await Expect(crumbs).ToContainTextAsync("workspace release-notes");
            await Expect(crumbs.Locator("[aria-current='page']")).ToContainTextAsync("agent ");

            await page.Locator("header [data-action='new-agent']").ClickAsync();
            await page.ToastAsync("New agent");
            await Expect(page).ToHaveURLAsync(new Regex(@"/a/(?!1$)\d+$"));
            await page.AgentReadyAsync();
            await Expect(page.Locator("[data-empty-hero]")).ToBeVisibleAsync();
            var link = Regex.Match(page.Url, @"/s/([^/]+)/a/(\d+)$");
            await Expect(page.Row(link.Groups[1].Value, long.Parse(link.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))).ToHaveClassAsync(Selected);
        }),

        // Item 21: arrows move, left and right collapse and expand, Enter opens.
        Browse("browser.tree-keyboard", async (hub, window) =>
        {
            var page = window.Page;
            var session = await hub.SessionAsync("keyboard");
            var helper = await hub.AgentAsync(session, "helper");
            await page.GotoAsync("/");
            await Expect(page.Row(session, helper)).ToBeVisibleAsync();
            await page.Locator(".tree-list").FocusAsync();
            await page.Keyboard.PressAsync("ArrowDown");
            await Expect(page.Locator(".tree-row.device[data-device='local']")).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("ArrowDown");
            var row = page.Row(session);
            await Expect(row).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("ArrowLeft");
            await Expect(row).ToHaveAttributeAsync("aria-expanded", "false");
            await Expect(page.Row(session, helper)).ToBeHiddenAsync();
            await page.Keyboard.PressAsync("ArrowRight");
            await Expect(row).ToHaveAttributeAsync("aria-expanded", "true");
            await Expect(page.Row(session, helper)).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("ArrowRight");
            await Expect(page.Row(session, 1)).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("ArrowDown");
            await Expect(page.Row(session, helper)).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Enter");
            await Expect(page).ToHaveURLAsync(new Regex(Regex.Escape(AgentPath(session, helper)) + "$"));
            await page.AgentReadyAsync();
            await Expect(page.Row(session, helper)).ToHaveClassAsync(Selected);
            await page.Keyboard.PressAsync("ArrowUp");
            await Expect(page.Row(session, 1)).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("ArrowLeft");
            await Expect(row).ToBeFocusedAsync();
        }),

        // Item 22: a stopped device shows offline with a stale tree and the command that starts it, and recovers by itself.
        Browse("browser.offline-device-recovers", async (hub, window) =>
        {
            var page = window.Page;
            var session = await hub.SessionAsync("resilient");
            await page.OpenAgentAsync(session);
            await page.SendAsync("before the outage");
            await Expect(page.Thread()).ToContainTextAsync("Echo: before the outage");

            await hub.StopDeviceAsync();
            var device = page.Locator(".tree-row.device[data-device='local']");
            await Expect(device).ToHaveAttributeAsync("data-status", "offline", new() { Timeout = 30_000 });
            await Expect(page.Row(session)).ToHaveClassAsync(new Regex(@"\bstale\b"));
            var center = page.Locator("#offlineCenter");
            await Expect(center).ToBeVisibleAsync();
            await Expect(center).ToContainTextAsync("local is offline");
            await Expect(center.Locator("#cmd-offline")).ToHaveTextAsync("pisharp durable serve");
            await Expect(page.Locator("#agentView")).ToBeHiddenAsync();

            await hub.RestartDeviceAsync();
            await Expect(device).ToHaveAttributeAsync("data-status", "online", new() { Timeout = 60_000 });
            await Expect(center).ToBeHiddenAsync();
            await page.AgentReadyAsync();
            await Expect(page.Row(session)).Not.ToHaveClassAsync(new Regex(@"\bstale\b"));
            await Expect(page.Thread()).ToContainTextAsync("Echo: before the outage");
            await page.SendAsync("after the outage");
            await Expect(page.Thread()).ToContainTextAsync("Echo: after the outage");
        }),

        // Item 27.
        Browse("browser.archive-session", async (hub, window) =>
        {
            var page = window.Page;
            var session = await hub.SessionAsync("old-work");
            await page.GotoAsync("/");
            var row = page.Row(session);
            await row.HoverAsync();
            await row.Locator("[data-action='archive-session']").ClickAsync();
            await Expect(page.Locator("[data-pr-title]")).ToHaveTextAsync("Archive old-work?");
            await page.PromptAsync(null);
            await page.ToastAsync("old-work archived on local");
            await Expect(row).ToHaveCountAsync(0);
        }),

        // Item 24: collapse buttons, the hotkeys, the rail's Open Tasks button, and the state surviving a reload.
        Browse("browser.rails-and-hotkeys", async (hub, window) =>
        {
            var page = window.Page;
            var session = await hub.SessionAsync("rails");
            await page.OpenAgentAsync(session);
            var workspace = page.Locator("#workspace");
            var treeCollapsed = new Regex(@"\btree-collapsed\b");
            var inspectorCollapsed = new Regex(@"\bins-collapsed\b");
            await page.Locator("#netTree .dn-pane-head [data-toggle-class='tree-collapsed']").ClickAsync();
            await Expect(workspace).ToHaveClassAsync(treeCollapsed);
            await page.ReloadAsync();
            await page.AgentReadyAsync();
            await Expect(workspace).ToHaveClassAsync(treeCollapsed);
            await page.Locator("#netTree .dn-rail [aria-label='Expand network']").ClickAsync();
            await Expect(workspace).Not.ToHaveClassAsync(treeCollapsed);

            await page.Keyboard.PressAsync(Hotkey("KeyN"));
            await Expect(workspace).ToHaveClassAsync(treeCollapsed);
            await page.Keyboard.PressAsync(Hotkey("KeyN"));
            await Expect(workspace).Not.ToHaveClassAsync(treeCollapsed);
            await page.Keyboard.PressAsync(Hotkey("KeyT"));
            await Expect(workspace).ToHaveClassAsync(inspectorCollapsed);
            await page.Locator("#inspector .dn-rail [aria-label='Open Tasks']").ClickAsync();
            await Expect(workspace).Not.ToHaveClassAsync(inspectorCollapsed);
            await Expect(page.Locator("#insPanes")).ToHaveAttributeAsync("data-active-pane", "ins-tasks");
            await Expect(page.Locator("[data-pane='ins-tasks']")).ToBeVisibleAsync();
        }),

        // Items 19 and 33: a deep link opens its agent; the selection survives a reload of / and opens a second tab on it.
        Browse("browser.deep-links-and-selection", async (hub, window) =>
        {
            var page = window.Page;
            var session = await hub.SessionAsync("links");
            var helper = await hub.AgentAsync(session, "helper");
            await page.OpenAgentAsync(session, helper);
            await Expect(page.Row(session, helper)).ToHaveClassAsync(Selected);
            await Expect(page.Locator("[data-crumbs] [aria-current='page']")).ToContainTextAsync("helper");
            await Expect(page).ToHaveTitleAsync(new Regex(@"^helper · links"));

            await page.GotoAsync("/");
            await Expect(page).ToHaveURLAsync(new Regex(Regex.Escape(AgentPath(session, helper)) + "$"));
            await page.AgentReadyAsync();
            await Expect(page.Row(session, helper)).ToHaveClassAsync(Selected);

            var second = await window.NewPageAsync();
            await second.GotoAsync("/");
            await Expect(second).ToHaveURLAsync(new Regex(Regex.Escape(AgentPath(session, helper)) + "$"));
            await second.AgentReadyAsync();
            await Expect(second.Row(session, helper)).ToHaveClassAsync(Selected);

            await second.OpenAgentAsync(session, 1);
            await Expect(second.Row(session, 1)).ToHaveClassAsync(Selected);
            await Expect(second.Locator("[data-crumbs] [aria-current='page']")).Not.ToContainTextAsync("helper");
            await second.GoBackAsync();
            await Expect(second).ToHaveURLAsync(new Regex(Regex.Escape(AgentPath(session, helper)) + "$"));
            await Expect(second.Row(session, helper)).ToHaveClassAsync(Selected);
        }),

        // Item 20: two tabs on one agent stream the same reply.
        Browse("browser.two-tabs-stream", async (hub, window) =>
        {
            var page = window.Page;
            var session = await hub.SessionAsync("streams");
            await page.OpenAgentAsync(session);
            var other = await window.NewPageAsync();
            await other.OpenAgentAsync(session);
            var words = string.Join(' ', Enumerable.Range(1, 60).Select(n => "word" + n));
            await page.SendAsync("slow: " + words);
            await Expect(other.Locator("[data-status='working']")).ToBeVisibleAsync();
            await Expect(other.Thread()).ToContainTextAsync("word1 word2 word3");
            await Expect(page.Thread()).ToContainTextAsync(words);
            await Expect(other.Thread()).ToContainTextAsync(words);
            await Expect(other.Locator("[data-status='working']")).ToBeHiddenAsync();
        }),

        // Items 26 and 32: at 390 px nothing scrolls sideways and the drawers open, in the dark theme and in the light one.
        Browse("browser.phone-width-and-themes", async (hub, window) =>
        {
            var page = window.Page;
            var session = await hub.SessionAsync("a-session-with-a-rather-long-name-for-a-phone");
            await page.OpenAgentAsync(session);
            await page.SendAsync("**Phone** check: a paragraph of ordinary words that has to wrap inside the narrow column of a phone screen.");
            await Expect(page.Thread()).ToContainTextAsync("Echo: Phone check");
            await page.SendAsync("!echo phone");
            await Expect(page.Thread()).ToContainTextAsync("Tool finished: bash");
            await Expect(page.Locator("html")).ToHaveAttributeAsync("data-bs-theme", "dark");
            var dark = await BackgroundLuminanceAsync(page);
            Check(dark < 0.2, $"the dark theme's page is dark ({dark:0.00})");
            await PhoneLayoutAsync(page, session, "dark");

            await page.Locator("header [data-theme-toggle]").ClickAsync();
            await Expect(page.Locator("html")).ToHaveAttributeAsync("data-bs-theme", "light");
            await page.ReloadAsync();
            await page.AgentReadyAsync();
            await Expect(page.Locator("html")).ToHaveAttributeAsync("data-bs-theme", "light");
            var light = await BackgroundLuminanceAsync(page);
            Check(light > 0.8, $"the light theme's page is light ({light:0.00})");
            await PhoneLayoutAsync(page, session, "light");
        }, width: 390, height: 844),
    ];

    private static async Task PhoneLayoutAsync(IPage page, string session, string theme)
    {
        Check(!await page.OverflowsAsync(), $"no sideways scroll at 390 px ({theme})");
        await page.Locator("header [aria-label='Open network']").ClickAsync();
        var tree = page.Locator("#netTree");
        await Expect(tree).ToHaveClassAsync(new Regex(@"\bshow\b"));
        await Expect(page.Row(session)).ToBeVisibleAsync();
        Check(!await page.OverflowsAsync(), $"no sideways scroll with the network drawer open ({theme})");
        await tree.Locator("[aria-label='Close network']").ClickAsync();
        await Expect(tree).Not.ToHaveClassAsync(new Regex(@"\bshow\b"));
        await Expect(tree).ToBeHiddenAsync();
        await page.Locator("header [aria-label='Open inspector']").ClickAsync();
        var inspector = page.Locator("#inspector");
        await Expect(inspector).ToHaveClassAsync(new Regex(@"\bshow\b"));
        await Expect(inspector.Locator("[data-session-tab] .ins-title")).ToBeVisibleAsync();
        Check(!await page.OverflowsAsync(), $"no sideways scroll with the inspector open ({theme})");
        await inspector.Locator("[aria-label='Close inspector']").ClickAsync();
        await Expect(inspector).ToBeHiddenAsync();
    }

    /// <summary>The relative luminance (0-1) of the agent view's background.</summary>
    private static Task<double> BackgroundLuminanceAsync(IPage page) => page.EvaluateAsync<double>("""
        () => {
          let el = document.querySelector('#agentView');
          let color = 'rgba(0, 0, 0, 0)';
          while (el) { color = getComputedStyle(el).backgroundColor; if (!/rgba\(.*, 0\)$/.test(color) && color !== 'transparent') break; el = el.parentElement; }
          const [r, g, b] = color.match(/\d+(\.\d+)?/g).slice(0, 3).map(Number).map((c) => c / 255).map((c) => c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4);
          return 0.2126 * r + 0.7152 * g + 0.0722 * b;
        }
        """);
}
