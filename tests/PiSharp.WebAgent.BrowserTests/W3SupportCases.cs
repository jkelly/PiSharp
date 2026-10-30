using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W3, T3): what the W3 browser cases run on. W3App starts the Web Agent as a child process the way Agent does (a temporary
// data folder, a free loopback port, an environment without this machine's provider keys, tokens or URL settings, Tools working in the case's
// own folder), in one of three modes: --faux; --faux-metered (still offline, but the faux provider is Metered at a list price and reports
// usage, so the Spend Guard has something to reserve); or Live, the real routing with nothing connected, for the first-run setup wizard. Live
// also blanks every provider variable and AWS or Google setting the app could read as an account, and points HTTP_PROXY, HTTPS_PROXY and
// ALL_PROXY at a closed loopback port, so a request that tried to leave this computer would fail at once instead of reaching a provider.
// KillAndRestartAsync crashes the app (Process.Kill of its tree) and starts it again on the same data, on a new port.
//
// W3Window is Window's profile on a W3App: every tab records uncaught errors and console errors, and the case fails on any of them, with one
// narrow exception a case must declare: the browser logs "Failed to load resource: the server responded with a status of 409" for a guarded
// send the Spend Guard refused (S5 answers such a send 409 with the confirm modal's markup; nothing was posted). ExpectRefusal(path) lets that
// exact message through for requests to that path only; the case then checks how many it saw. The support case below plants an allowed 409,
// a 409 on another path and a 404, and checks that only the allowed one passes.
internal static partial class Program
{
    /// <summary>How a <see cref="W3App"/> runs.</summary>
    internal enum W3Mode
    {
        /// <summary><c>--faux</c>: the free faux provider, no provider code path.</summary>
        Faux,

        /// <summary><c>--faux-metered</c>: still offline, but the faux provider is Metered at a list price and reports usage.</summary>
        FauxMetered,

        /// <summary>The real routing with nothing connected (a first run); outbound requests go to a closed loopback proxy.</summary>
        Live,
    }

    /// <summary>The Web Agent as a child process for a W3 case (see the file's header).</summary>
    internal sealed class W3App : IAsyncDisposable
    {
        private readonly W3Mode mode;
        private readonly StringBuilder log = new();
        private Process process;

        private W3App(string root, W3Mode mode, Process process, Uri baseUri)
        {
            Root = root;
            this.mode = mode;
            this.process = process;
            Base = baseUri;
        }

        public string Root { get; }

        public string Data => Path.Join(Root, "data");

        public string Work => Path.Join(Root, "work");

        /// <summary>The app's address; a restart moves it to a new port.</summary>
        public Uri Base { get; private set; }

        public HttpClient Http { get; } = new() { Timeout = TimeSpan.FromSeconds(60) };

        /// <summary>The app's console output so far, across restarts.</summary>
        public string Log { get { lock (log) return log.ToString(); } }

        /// <summary>Starts the app in <paramref name="mode"/> on a fresh data folder, with Tools working in the case's <c>work</c> folder
        /// (<paramref name="approvalMode"/>).</summary>
        public static async Task<W3App> StartAsync(string name, W3Mode mode, string approvalMode = "ask")
        {
            var root = Temp("w3-" + name);
            Directory.CreateDirectory(Path.Join(root, "work"));
            W3App? app = null;
            try
            {
                var (process, baseUri) = Launch(root, mode, null);
                app = new W3App(root, mode, process, baseUri);
                app.Follow(process);
                await app.ReadyAsync();
                var tools = await app.PostAsync("/api/settings/tools",
                    new JsonObject { ["workingDirectory"] = app.Work, ["approvalMode"] = approvalMode, ["allowList"] = new JsonArray() });
                Equal(200, tools.Status, "the working directory is the case's own: " + tools.Text);
                var health = await app.Get("/api/health");
                Equal(mode != W3Mode.Live, health.Body?["faux"]?.GetValue<bool>() == true, "the faux switch");
                Equal(mode == W3Mode.FauxMetered, health.Body?["fauxMetered"]?.GetValue<bool>() == true, "the priced faux switch");
                return app;
            }
            catch
            {
                if (app is not null) await app.DisposeAsync();
                else DeleteFolder(root);
                throw;
            }
        }

        private static (Process Process, Uri Base) Launch(string root, W3Mode mode, StringBuilder? log)
        {
            var port = FreePort();
            var baseUri = new Uri($"http://127.0.0.1:{port}/");
            var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add(W3AppDll());
            if (mode == W3Mode.Faux) info.ArgumentList.Add("--faux");
            if (mode == W3Mode.FauxMetered) info.ArgumentList.Add("--faux-metered");
            foreach (var argument in new[] { "--data", Path.Join(root, "data"), "--urls", baseUri.GetLeftPart(UriPartial.Authority), "--Logging:LogLevel:Default=Warning" })
                info.ArgumentList.Add(argument);
            foreach (var variable in info.Environment.Keys.ToList())
                if (Unwanted(variable, mode)) info.Environment.Remove(variable);
            if (mode == W3Mode.Live)
            {
                // Nothing leaves this computer: a closed port on loopback is every proxy, and nothing bypasses it.
                foreach (var proxy in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" }) info.Environment[proxy] = "http://127.0.0.1:9";
                info.Environment["NO_PROXY"] = "";
                info.Environment["no_proxy"] = "";
                // No ambient AWS or Google credentials from this machine's files either.
                info.Environment["AWS_SHARED_CREDENTIALS_FILE"] = Path.Join(root, "no-aws-credentials");
                info.Environment["AWS_CONFIG_FILE"] = Path.Join(root, "no-aws-config");
                info.Environment["CLOUDSDK_CONFIG"] = Path.Join(root, "no-gcloud");
            }
            var process = Process.Start(info)!;
            return (process, baseUri);
        }

        /// <summary>Whether the child must not see <paramref name="variable"/>: provider keys, tokens and secrets, URL and port settings, the data
        /// folder and the environment name; in Live mode also every AWS, Google, Azure or Cloudflare setting.</summary>
        private static bool Unwanted(string variable, W3Mode mode) =>
            variable.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase) || variable.EndsWith("_TOKEN", StringComparison.OrdinalIgnoreCase) ||
            variable.EndsWith("_SECRET", StringComparison.OrdinalIgnoreCase) || variable.EndsWith("_URLS", StringComparison.OrdinalIgnoreCase) ||
            variable.EndsWith("HTTP_PORTS", StringComparison.OrdinalIgnoreCase) || variable.EndsWith("HTTPS_PORTS", StringComparison.OrdinalIgnoreCase) ||
            variable.Equals("PISHARP_WEBAGENT_DATA", StringComparison.OrdinalIgnoreCase) ||
            variable.Equals("ASPNETCORE_ENVIRONMENT", StringComparison.OrdinalIgnoreCase) || variable.Equals("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase) ||
            (mode == W3Mode.Live && (variable.EndsWith("_KEY", StringComparison.OrdinalIgnoreCase) || variable.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
                variable.StartsWith("AWS_", StringComparison.OrdinalIgnoreCase) || variable.StartsWith("GOOGLE_", StringComparison.OrdinalIgnoreCase) ||
                variable.StartsWith("GCLOUD_", StringComparison.OrdinalIgnoreCase) || variable.StartsWith("AZURE_", StringComparison.OrdinalIgnoreCase) ||
                variable.StartsWith("CLOUDFLARE_", StringComparison.OrdinalIgnoreCase) || variable.Contains("PROXY", StringComparison.OrdinalIgnoreCase)));

        /// <summary>The built app: <c>src/PiSharp.WebAgent/bin/&lt;configuration&gt;/net10.0/PiSharp.WebAgent.dll</c>.</summary>
        private static string W3AppDll()
        {
            var metadata = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value);
            return Path.GetFullPath(Path.Join(metadata["WebProject"]!, "bin", metadata["Configuration"]!, "net10.0", "PiSharp.WebAgent.dll"));
        }

        private void Follow(Process child)
        {
            child.OutputDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
            child.ErrorDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
            child.BeginOutputReadLine();
            child.BeginErrorReadLine();
        }

        private Task ReadyAsync() => WaitUntilAsync(async () =>
        {
            if (process.HasExited) throw new InvalidOperationException($"The app exited ({process.ExitCode}): " + Log);
            try { return (await Http.GetAsync(new Uri(Base, "/api/health"))).IsSuccessStatusCode; }
            catch (HttpRequestException) { return false; }
        }, "the app answers /api/health", TimeSpan.FromSeconds(60));

        /// <summary>Kills the app's process tree (no shutdown: a crash) and starts it again on the same data, on a new port
        /// (<see cref="Base"/> changes; open a new <see cref="W3Window"/> on it).</summary>
        public async Task KillAndRestartAsync()
        {
            await StopAsync();
            process.Dispose();
            var (next, baseUri) = Launch(Root, mode, null);
            process = next;
            Base = baseUri;
            Follow(next);
            await ReadyAsync();
        }

        public async Task<(int Status, JsonNode? Body, string Text)> SendAsync(HttpMethod method, string path, JsonNode? body = null)
        {
            using var request = new HttpRequestMessage(method, new Uri(Base, path));
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await Http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            JsonNode? json = null;
            try { json = text.Length > 0 && text[0] is '{' or '[' ? JsonNode.Parse(text) : null; } catch (System.Text.Json.JsonException) { /* Not JSON. */ }
            return ((int)response.StatusCode, json, text);
        }

        public Task<(int Status, JsonNode? Body, string Text)> Get(string path) => SendAsync(HttpMethod.Get, path);

        public Task<(int Status, JsonNode? Body, string Text)> PostAsync(string path, JsonNode body) => SendAsync(HttpMethod.Post, path, body);

        /// <summary>Creates a chat in <see cref="Work"/> with its first message (no spend check); returns its id.</summary>
        public async Task<string> CreateChatAsync(string text, string requestId)
        {
            var created = await PostAsync("/api/chats", new JsonObject { ["requestId"] = requestId, ["text"] = text, ["cwd"] = Work });
            Equal(200, created.Status, "created: " + created.Text);
            return created.Body!["id"]!.GetValue<string>();
        }

        /// <summary>Waits until the chat's run is over.</summary>
        public Task WaitIdleAsync(string chatId) => WaitUntilAsync(async () => (await Get("/api/chats/" + chatId)).Body?["running"]?.GetValue<bool>() == false,
            $"chat {chatId} is idle", TimeSpan.FromSeconds(60));

        /// <summary>The ledger's rows (<c>GET /api/spend/ledger</c>), newest first.</summary>
        public async Task<List<JsonObject>> LedgerAsync()
        {
            var ledger = await Get("/api/spend/ledger?size=100");
            Equal(200, ledger.Status, "the ledger: " + ledger.Text);
            return [.. ledger.Body!["items"]!.AsArray().Select(item => item!.AsObject())];
        }

        private async Task StopAsync()
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            process.Dispose();
            Http.Dispose();
            DeleteFolder(Root);
        }
    }

    /// <summary>A text field of a ledger item's reservation (<c>op</c>, <c>state</c>, <c>run</c>, ...), or null.</summary>
    internal static string? Reservation(JsonObject item, string name) =>
        item["reservation"]?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static void DeleteFolder(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* A temporary directory. */ }
    }

    /// <summary>A case on a fresh <see cref="W3App"/> in <paramref name="mode"/> and a fresh browser profile (<see cref="W3Window"/>); it ends
    /// with no page error and no console error but the refusals it declared.</summary>
    internal static (string, Func<Task>) BrowseW3(string id, W3Mode mode, Func<W3App, W3Window, Task> run, int width = 1440, int height = 900) =>
        Case(id, async () =>
        {
            await using var app = await W3App.StartAsync(id.Replace("browser.w3.", "", StringComparison.Ordinal), mode);
            await using var window = await W3Window.OpenAsync(app, width, height);
            try
            {
                await run(app, window);
                window.NoErrors();
            }
            catch
            {
                await window.SaveAsync(id);
                throw;
            }
        });

    /// <summary>A browser profile on a <see cref="W3App"/> (its address when opened), recording every tab's page and console errors.</summary>
    internal sealed class W3Window : IAsyncDisposable
    {
        /// <summary>The console text of a request the server answered 409 (the browser logs it as an error).</summary>
        public const string Refused = "Failed to load resource: the server responded with a status of 409";

        private readonly List<string> errors = [];
        private readonly List<string> refusals = [];
        private readonly List<string> refusable = [];
        private readonly List<IPage> pages = [];
        private readonly List<W3Window> others = [];

        private W3Window(W3App app, IBrowserContext context, Uri baseUri)
        {
            App = app;
            Context = context;
            Base = baseUri;
        }

        public W3App App { get; }

        public IBrowserContext Context { get; }

        /// <summary>The app address this profile opened on.</summary>
        public Uri Base { get; }

        public IPage Page { get; private set; } = null!;

        public static async Task<W3Window> OpenAsync(W3App app, int width, int height, ColorScheme? scheme = null)
        {
            var context = await (await BrowserAsync()).NewContextAsync(new BrowserNewContextOptions
            {
                BaseURL = app.Base.ToString(),
                ViewportSize = new ViewportSize { Width = width, Height = height },
                ReducedMotion = ReducedMotion.Reduce,
                ColorScheme = scheme,
            });
            context.SetDefaultTimeout(15_000);
            var window = new W3Window(app, context, app.Base);
            window.Page = await window.NewPageAsync();
            return window;
        }

        /// <summary>Another profile on the app's current address (after a restart), whose errors this window reports too.</summary>
        public async Task<W3Window> ReopenAsync(int width = 1440, int height = 900)
        {
            var other = await OpenAsync(App, width, height);
            lock (refusable) other.refusable.AddRange(refusable);
            others.Add(other);
            return other;
        }

        /// <summary>Lets the browser's 409 message through for requests to <paramref name="path"/> (a guarded send the Spend Guard refused).</summary>
        public void ExpectRefusal(string path)
        {
            lock (refusable) refusable.Add(path);
        }

        /// <summary>The 409 messages let through so far (each with its URL).</summary>
        public IReadOnlyList<string> Refusals { get { lock (errors) return [.. refusals, .. others.SelectMany(other => other.Refusals)]; } }

        public async Task<IPage> NewPageAsync()
        {
            var page = await Context.NewPageAsync();
            var tab = pages.Count + 1;
            page.PageError += (_, error) => { lock (errors) errors.Add($"tab {tab} page error: {error}"); };
            page.Console += (_, message) =>
            {
                if (message.Type != "error") return;
                var path = LocationPath(message.Location);
                bool allowed;
                lock (refusable) allowed = message.Text.StartsWith(Refused, StringComparison.Ordinal) && refusable.Contains(path, StringComparer.Ordinal);
                lock (errors)
                {
                    if (allowed) refusals.Add(path);
                    else errors.Add($"tab {tab} console error at {message.Location}: {message.Text}");
                }
            };
            pages.Add(page);
            return page;
        }

        public int ErrorCount { get { lock (errors) return errors.Count + others.Sum(other => other.ErrorCount); } }

        public void NoErrors()
        {
            lock (errors) Check(errors.Count == 0, "page or console errors: " + string.Join(" | ", errors));
            foreach (var other in others) other.NoErrors();
        }

        public async Task SaveAsync(string id)
        {
            if (Artifacts is null) return;
            Directory.CreateDirectory(Artifacts);
            var windows = new List<W3Window> { this };
            windows.AddRange(others);
            for (var w = 0; w < windows.Count; w++)
            {
                var window = windows[w];
                for (var i = 0; i < window.pages.Count; i++)
                {
                    if (window.pages[i].IsClosed) continue;
                    var name = Path.Join(Artifacts, id + (w == 0 ? "" : "-window" + (w + 1)) + (i == 0 ? "" : "-tab" + (i + 1)));
                    try
                    {
                        await window.pages[i].ScreenshotAsync(new PageScreenshotOptions { Path = name + ".png", FullPage = true });
                        await File.WriteAllTextAsync(name + ".html", await window.pages[i].ContentAsync());
                    }
                    catch (PlaywrightException) { /* The page went away. */ }
                }
                lock (window.errors) if (window.errors.Count > 0) File.AppendAllLines(Path.Join(Artifacts, id + "-errors.txt"), window.errors);
            }
            await File.WriteAllTextAsync(Path.Join(Artifacts, id + "-app.log"), App.Log);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var other in others) await other.DisposeAsync();
            await Context.DisposeAsync();
        }
    }

    // ─── Page helpers ────────────────────────────────────────────────────────

    /// <summary>Stores <paramref name="theme"/> as this browser's choice (as Settings › Appearance and the top bar's toggle do) and reloads.</summary>
    internal static async Task UseThemeAsync(this IPage page, string theme)
    {
        await page.EvaluateAsync("(theme) => window.PiSharpAppearance.setTheme(theme)", theme);
        await page.ReloadAsync();
        await Expect(page.Locator("html")).ToHaveAttributeAsync("data-bs-theme", theme);
    }

    /// <summary>The page shows <paramref name="theme"/>, does not scroll sideways, and no open dialog sticks out of the viewport.</summary>
    internal static async Task FitsPhoneAsync(this IPage page, string what, string theme)
    {
        await Expect(page.Locator("html")).ToHaveAttributeAsync("data-bs-theme", theme);
        if (await page.OverflowsAsync())
            throw new InvalidOperationException($"{what} ({theme}) scrolls sideways at {page.ViewportSize?.Width}px: " + await page.WideElementsAsync());
        var dialog = await page.EvaluateAsync<string?>("""
            () => {
              for (const el of document.querySelectorAll('.modal.show .modal-dialog')) {
                const r = el.getBoundingClientRect();
                if (r.left < -1 || r.right > window.innerWidth + 1) return `${el.parentElement.id} (${Math.round(r.left)}..${Math.round(r.right)})`;
              }
              return null;
            }
            """);
        Check(dialog is null, $"{what} ({theme}): the open dialog is wider than the screen: {dialog}");
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

    /// <summary>The path of a console message's location (<c>http://127.0.0.1:5000/api/chats:0:0</c> → <c>/api/chats</c>), or empty.</summary>
    internal static string LocationPath(string? location)
    {
        var url = Regex.Match(location ?? "", @"^(?<url>.+?)(?::\d+:\d+)?$").Groups["url"].Value;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : "";
    }

    private static IEnumerable<(string, Func<Task>)> W3SupportCases() =>
    [
        Case("browser.w3.support.only-a-declared-refusal-passes", async () =>
        {
            await using var app = await W3App.StartAsync("support", W3Mode.FauxMetered);
            await using var window = await W3Window.OpenAsync(app, 1440, 900);
            var cap = await app.PostAsync("/api/spend/caps", new JsonObject { ["scope"] = "everything", ["period"] = "request", ["limit"] = "0.01", ["action"] = "block" });
            Equal(200, cap.Status, "a one-cent cap per request: " + cap.Text);
            var page = window.Page;
            await page.GotoAsync("/chat/new");
            await Expect(page.Locator("[data-composer-input]")).ToBeVisibleAsync();

            // A guarded send the cap refuses (409 from the declared path) passes; a 409 from another path and a 404 do not.
            window.ExpectRefusal("/api/chats");
            var statuses = await page.EvaluateAsync<int[]>("""
                async () => {
                  const json = { 'Content-Type': 'application/json' };
                  const refused = await fetch('/api/chats', { method: 'POST', headers: json, body: JSON.stringify({ requestId: 'support-1', text: 'over the cap', spend: { check: true } }) });
                  const conflict = await fetch('/api/spend/caps', { method: 'POST', headers: json, body: JSON.stringify({ scope: 'everything', period: 'request', limit: '0.02', action: 'block' }) });
                  const missing = await fetch('/no-such-page');
                  return [refused.status, conflict.status, missing.status];
                }
                """);
            Equal("409,409,404", string.Join(",", statuses), "the planted answers");
            await WaitUntilAsync(() => Task.FromResult(window.ErrorCount >= 2 && window.Refusals.Count >= 1), "every answer is recorded");
            Equal("/api/chats", string.Join(",", window.Refusals), "the declared refusal is let through");
            Equal(0, (await app.Get("/api/chats")).Body?["items"]?.AsArray().Count ?? 0, "the refused send created no chat");
            try
            {
                window.NoErrors();
            }
            catch (InvalidOperationException error)
            {
                Check(error.Message.Contains("/api/spend/caps", StringComparison.Ordinal) && error.Message.Contains("409", StringComparison.Ordinal),
                    "a refusal from another path is reported: " + error.Message);
                Check(error.Message.Contains("no-such-page", StringComparison.Ordinal) && error.Message.Contains("404", StringComparison.Ordinal),
                    "a missing page is reported: " + error.Message);
                Check(!error.Message.Contains("/api/chats:", StringComparison.Ordinal), "the declared refusal is not: " + error.Message);
                return;
            }
            throw new InvalidOperationException("The guard passed a window whose tab logged unexpected errors.");
        }),
    ];
}
