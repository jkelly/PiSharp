using Microsoft.Playwright;
using PiSharp.Web.Common;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W2, WU3): the hub on PiSharp.Web.Common in a browser. Every file the pages load comes from _content/PiSharp.Web.Common/
// or the hub and is served, and the shared modules (swap.js, stream.js, theme.js) behave as the hub and the Web Agent rely on.
internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> WebCommonCases() =>
    [
        Browse("browser.web-common-assets-and-modules", async (hub, window) =>
        {
            var page = window.Page;
            var loaded = new List<string>();
            var failed = new List<string>();
            page.Response += (_, response) =>
            {
                lock (loaded)
                {
                    loaded.Add(new Uri(response.Url).AbsolutePath);
                    if (response.Status >= 400) failed.Add($"{response.Status} {response.Url}");
                }
            };
            var session = await hub.SessionAsync("assets");
            await page.OpenAgentAsync(session);
            await page.GotoAsync("/devices");
            await Expect(page.Locator("[data-connection]")).ToHaveTextAsync("Connected");
            await page.GotoAsync("/executions");
            await Expect(page.Locator("[data-automation='executions']")).ToBeAttachedAsync();
            lock (loaded)
            {
                Check(failed.Count == 0, "every file the pages load is served: " + string.Join(" | ", failed));
                foreach (var file in new[] { WebCommonAssets.ThemeCss, WebCommonAssets.ShellCss, WebCommonAssets.BootstrapCss, WebCommonAssets.BootstrapIconsCss,
                             WebCommonAssets.Root + "/lib/bootstrap-icons/fonts/bootstrap-icons.woff2", WebCommonAssets.BootstrapJs, WebCommonAssets.ThemeBootJs,
                             WebCommonAssets.ThemeJs, WebCommonAssets.StreamJs, WebCommonAssets.SwapJs, WebCommonAssets.Mark })
                    Check(loaded.Contains(file), $"the pages load {file}");
            }
            Check((await page.EvaluateAsync<string>("() => getComputedStyle(document.documentElement).getPropertyValue('--ps-bg')")).Trim().Length > 0,
                "the theme's tokens apply");

            // swap.js: upsert, remove and order by key, keeping open <details> open across a swap (inside an item, or the item itself), and
            // a panel swap that keeps its open details too.
            var swapped = await page.EvaluateAsync<string>("""
                async () => {
                  const { applyItems, swapHtml, element } = await import('/_content/PiSharp.Web.Common/js/swap.js');
                  const keys = (box) => [...box.children].map((el) => el.dataset.key).join(',');
                  const box = document.createElement('div');
                  applyItems(box, { upsert: [{ id: 'a', html: '<p data-key="a">A</p>' },
                    { id: 'b', html: '<details data-key="b" data-detail="b"><summary>B</summary></details>' },
                    { id: 'd', html: '<article data-key="d"><details data-detail="d1"><summary>D</summary></details></article>' }], order: ['b', 'a', 'd'] }, true);
                  const first = keys(box);
                  box.querySelectorAll('details').forEach((d) => { d.open = true; });
                  applyItems(box, { upsert: [{ id: 'b', html: '<details data-key="b" data-detail="b"><summary>B2</summary></details>' },
                    { id: 'd', html: '<article data-key="d"><details data-detail="d1"><summary>D2</summary></details></article>' },
                    { id: 'c', html: '<p data-key="c">C</p>' }], remove: ['a'], order: ['c', 'b', 'd'] }, false);
                  const open = [...box.querySelectorAll('details')].map((d) => d.open + ':' + d.querySelector('summary').textContent).join(',');
                  const unchanged = applyItems(box, { order: ['c', 'b', 'd'] }, false);
                  const panel = document.createElement('div');
                  swapHtml(panel, '<details data-detail="p"><summary>P</summary></details>');
                  panel.querySelector('details').open = true;
                  swapHtml(panel, '<details data-detail="p"><summary>P2</summary></details>');
                  return [first, keys(box), open, unchanged, panel.querySelector('details').open, element(' <i>x</i> ').tagName].join('|');
                }
                """);
            Equal("b,a,d|c,b,d|true:B2,true:D2|false|true|I", swapped, "applyItems and swapHtml");

            // stream.js: hello names the stream; a subscription PUT the server answers 404 (a forgotten stream) reopens it with a new id.
            var streamed = await page.EvaluateAsync<string>("""
                async () => {
                  const { MuxStream } = await import('/_content/PiSharp.Web.Common/js/stream.js');
                  const stream = new MuxStream();
                  const states = [];
                  const connected = () => new Promise((resolve) => { stream.status = (s) => { states.push(s); if (s === 'connected') resolve(); }; });
                  let wait = connected();
                  stream.start();
                  await wait;
                  const first = stream.streamId;
                  await stream.subscribe([]);
                  wait = connected();
                  stream.streamId = 'gone';
                  await stream.push();
                  await wait;
                  const second = stream.streamId;
                  stream.source.close();
                  return [first ? 'named' : 'unnamed', second && second !== 'gone' && second !== first ? 'reopened' : 'kept ' + second, states.join(',')].join('|');
                }
                """);
            Equal("named|reopened|connected,connected", streamed, "MuxStream");

            // theme-boot.js and theme.js: the toggle switches and stores the hub's key, and the stored theme applies before the page's scripts.
            await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme-key", "pisharp-durable-theme");
            await Expect(page.Locator("html")).ToHaveAttributeAsync("data-bs-theme", "dark");
            await page.Locator("header [data-theme-toggle]").ClickAsync();
            await Expect(page.Locator("html")).ToHaveAttributeAsync("data-bs-theme", "light");
            await Expect(page.Locator("header [data-theme-icon]")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bbi-sun\b"));
            Equal("light", await page.EvaluateAsync<string>("() => localStorage.getItem('pisharp-durable-theme')"), "the stored theme");
            await page.ReloadAsync();
            Equal("light", await page.EvaluateAsync<string>("() => document.documentElement.getAttribute('data-bs-theme')"), "the theme after a reload");
            await page.Locator("header [data-theme-toggle]").ClickAsync();
            await Expect(page.Locator("html")).ToHaveAttributeAsync("data-bs-theme", "dark");
            Equal("dark", await page.EvaluateAsync<string>("() => localStorage.getItem('pisharp-durable-theme')"), "the stored theme, back to dark");
        }),

        // stream.js: each PUT replaces the tab's whole subscription set, so one is in flight at a time. A deep link's hello PUTs no keys
        // just before the first network frame subscribes the agent; held back, it must still reach the hub first, or the hub ends up with
        // no keys and the agent's frames stop.
        Browse("browser.web-common-stream-puts-in-order", async (hub, window) =>
        {
            var page = window.Page;
            var session = await hub.SessionAsync("order");
            var puts = new List<string>();
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await page.RouteAsync(url => new Uri(url).AbsolutePath.StartsWith("/api/stream/", StringComparison.Ordinal), async route =>
            {
                bool first;
                lock (puts)
                {
                    first = puts.Count == 0;
                    puts.Add(route.Request.PostData ?? "");
                }
                if (first) await Task.Delay(1_000);
                var response = await route.FetchAsync();
                await route.FulfillAsync(new() { Response = response });
                if (first) held.TrySetResult();
            });
            await page.OpenAgentAsync(session);
            await held.Task.WaitAsync(TimeSpan.FromSeconds(15));
            lock (puts) Check(puts.Count >= 2 && !puts[0].Contains("/agent/", StringComparison.Ordinal),
                "the hello's PUT, without the agent, came first: " + string.Join(" | ", puts));
            await page.SendAsync("still subscribed");
            await Expect(page.Thread()).ToContainTextAsync("Echo: still subscribed");
        }),
    ];
}
