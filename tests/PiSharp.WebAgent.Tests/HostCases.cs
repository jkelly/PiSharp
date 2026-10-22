using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PiSharp.Web.Common;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Shell;

// Decision 0011 (run W2, WU4): the Web Agent's host as a child process (WebAgentApp): it starts offline with the faux switch on loopback,
// refuses a non-loopback --urls, guards every request (421/403/415 and the Content-Security-Policy), keeps its data where --data or
// PISHARP_WEBAGENT_DATA say, migrates webagent.db once, creates its Data Protection key ring, and renders every shell page without a server
// error; plus the command line and binding in process.
internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> HostCases() =>
    [
        Case("host.starts-offline-with-faux-on-loopback", async () =>
        {
            await using var app = await WebAgentApp.StartAsync("start");
            var health = await app.Get("/api/health");
            Equal(200, health.Status, "health");
            Equal("ok", health.Body!["status"]!.GetValue<string>(), "status");
            Equal(true, health.Body!["faux"]!.GetValue<bool>(), "the faux switch is on");
            Check(!app.Log.Contains("fail:", StringComparison.Ordinal) && !app.Log.Contains("Exception", StringComparison.Ordinal), "a clean start: " + app.Log);
        }),

        Case("host.binds-127.0.0.1-5180-unless-urls-says-otherwise", () =>
        {
            var options = new WebAgentOptions { DataDirectory = Temp("bind") };
            try
            {
                var builder = WebAgentHost.CreateBuilder(options, []);
                Equal(WebAgentHost.DefaultUrls, builder.WebHost.GetSetting(WebHostDefaults.ServerUrlsKey), "the default address");
                Equal("http://127.0.0.1:5180", WebAgentHost.DefaultUrls, "the documented address");
                Equal(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), Path.TrimEndingDirectorySeparator(builder.Environment.ContentRootPath),
                    "the content root is the output folder");
                var chosen = WebAgentHost.CreateBuilder(options, ["--urls", "http://localhost:5999"]);
                Equal("http://localhost:5999", chosen.WebHost.GetSetting(WebHostDefaults.ServerUrlsKey), "--urls");
                Throws<InvalidOperationException>(() => WebAgentHost.CreateBuilder(options, ["--urls", "http://0.0.0.0:5180"]), "a non-loopback --urls");
            }
            finally { DeleteTemp(options.DataDirectory); }
            return Task.CompletedTask;
        }),

        Case("host.refuses-to-listen-beyond-loopback", async () =>
        {
            WebAgentHost.CheckLoopbackUrls("http://127.0.0.1:5180;http://localhost:5181; https://[::1]:5182");
            foreach (var url in new[] { "http://0.0.0.0:5180", "http://*:5180", "http://+:5180", "http://192.168.1.20:5180", "http://[::]:5180",
                         "http://localhost.evil.example:5180", "http://127.0.0.2:5180", "ftp://127.0.0.1:21", "127.0.0.1:5180",
                         "http://127.0.0.1:5180;http://0.0.0.0:5181" })
                Throws<InvalidOperationException>(() => WebAgentHost.CheckLoopbackUrls(url), url);

            var root = Temp("refuse");
            try
            {
                var data = Path.Join(root, "data");
                var (exitCode, output) = await WebAgentApp.RunToExitAsync(new AppStart { DataDirectory = data }, ["--urls", "http://0.0.0.0:5199"]);
                Equal(2, exitCode, "the app refuses to start: " + output);
                Check(output.Contains("PiSharp Web Agent: --urls \"http://0.0.0.0:5199\" is not this computer", StringComparison.Ordinal), "it says why: " + output);
                Check(!output.Contains("Unhandled exception", StringComparison.Ordinal), "without a stack trace: " + output);
                Check(!Directory.Exists(data), "nothing is written before the refusal");
                var noFolder = await WebAgentApp.RunToExitAsync(new AppStart { EnvironmentData = data, Arguments = ["--data"] }, ["--urls", "http://127.0.0.1:5199"]);
                Equal(2, noFolder.ExitCode, "--data without a folder: " + noFolder.Output);
                Check(noFolder.Output.Contains("--data needs a folder", StringComparison.Ordinal), "it says why: " + noFolder.Output);
                Check(!Directory.Exists(data), "and starts nothing");
            }
            finally { DeleteTemp(root); }
        }),

        Case("host.guard-answers-421-403-415-and-sends-the-csp", async () =>
        {
            await using var app = await WebAgentApp.StartAsync("guard");
            var foreign = await app.SendAsync(HttpMethod.Get, "/api/health", host: "evil.example");
            Equal(421, foreign.Status, "a foreign Host");
            Equal(WebAgentHost.MisdirectedMessage, foreign.Text, "the 421 body");
            Equal(421, (await app.SendAsync(HttpMethod.Get, "/playground", host: "rebind.example:" + app.Base.Port)).Status, "a rebinding Host on a page");
            Equal(200, (await app.SendAsync(HttpMethod.Get, "/api/health", host: "localhost:" + app.Base.Port)).Status, "localhost is this computer");
            Equal(403, (await app.SendAsync(HttpMethod.Post, "/api/health", new JsonObject(), origin: "http://evil.example")).Status, "a cross-origin write");
            Equal(403, (await app.SendAsync(HttpMethod.Post, "/settings/data", new JsonObject(), origin: "http://evil.example")).Status,
                "a cross-origin write to a page");
            Equal(415, (await app.SendAsync(HttpMethod.Post, "/api/health", contentType: "text/plain", text: "x=1")).Status, "a plain-text API write");
            Equal(415, (await app.SendAsync(HttpMethod.Post, "/api/health", contentType: "application/x-www-form-urlencoded", text: "x=1",
                origin: app.Origin)).Status, "a form post to the API");
            var through = await app.SendAsync(HttpMethod.Post, "/api/health", new JsonObject(), origin: app.Origin);
            Check(through.Status is not (421 or 403 or 415), "a same-origin JSON write passes the guard: " + through.Status);
            var page = await app.Get("/playground");
            Equal(HostGuardOptions.DefaultContentSecurityPolicy, string.Join("", page.Response.Headers.GetValues("Content-Security-Policy")), "the CSP");
        }),

        Case("host.data-folder-from-the-data-argument", async () =>
        {
            await using var app = await WebAgentApp.StartAsync("data-arg");
            var options = new WebAgentOptions { DataDirectory = Path.Join(app.Root, "data") };
            Check(File.Exists(options.DatabasePath), "webagent.db");
            Check(Directory.Exists(options.DeviceDirectory), "device\\");
            Check(Directory.Exists(options.KeysDirectory), "keys\\");
            Equal(Path.Join(options.DataDirectory, "credentials.json"), options.CredentialsPath, "credentials.json's place");
        }),

        Case("host.data-folder-from-the-environment-and-data-wins", async () =>
        {
            var root = Temp("data-env");
            try
            {
                var fromEnvironment = Path.Join(root, "env");
                await using (var app = await WebAgentApp.StartAsync(root, new AppStart { EnvironmentData = fromEnvironment }))
                    Check(File.Exists(Path.Join(fromEnvironment, "webagent.db")), "PISHARP_WEBAGENT_DATA is used without --data");
                var ignored = Path.Join(root, "env-ignored");
                var fromArgument = Path.Join(root, "arg");
                await using (var app = await WebAgentApp.StartAsync(root, new AppStart { EnvironmentData = ignored, DataDirectory = fromArgument }))
                {
                    Check(File.Exists(Path.Join(fromArgument, "webagent.db")), "--data is used");
                    Check(!Directory.Exists(ignored), "--data overrides PISHARP_WEBAGENT_DATA");
                }
            }
            finally { DeleteTemp(root); }
        }),

        Case("host.command-line-and-default-data-folder", () =>
        {
            var localAppData = Path.Join(Path.GetTempPath(), "pisharp-webagent", "fake-localappdata");
            Equal(Path.GetFullPath(Path.Join(localAppData, "PiSharpWebAgent")), WebAgentOptions.ResolveDataDirectory(null, null, localAppData),
                "%LOCALAPPDATA%\\PiSharpWebAgent by default");
            Equal(Path.GetFullPath(Path.Join(localAppData, "PiSharpWebAgent")), WebAgentOptions.ResolveDataDirectory("  ", "", localAppData), "blank counts as missing");
            Equal(Path.GetFullPath("from-env"), WebAgentOptions.ResolveDataDirectory(null, "from-env", localAppData), "the environment variable");
            Equal(Path.GetFullPath("from-cli"), WebAgentOptions.ResolveDataDirectory("from-cli", "from-env", localAppData), "--data first");

            string? Env(string name) => name == WebAgentOptions.DataEnvironmentVariable ? Path.Join(localAppData, "env") : null;
            var (options, rest) = WebAgentOptions.FromCommandLine(["--faux", "--data", "folder", "--urls", "http://127.0.0.1:1", "--Logging:LogLevel:Default=Warning"], Env, localAppData);
            Equal(true, options.Faux, "--faux");
            Equal(Path.GetFullPath("folder"), options.DataDirectory, "--data <folder>");
            Equal("--urls|http://127.0.0.1:1|--Logging:LogLevel:Default=Warning", string.Join("|", rest), "the rest goes to ASP.NET Core");
            Equal(Path.Join(options.DataDirectory, "device"), options.DeviceDirectory, "device\\");
            Equal(Path.Join(options.DataDirectory, "keys"), options.KeysDirectory, "keys\\");
            Equal(Path.Join(options.DataDirectory, "webagent.db"), options.DatabasePath, "webagent.db");
            Equal(Path.Join(options.DataDirectory, "credentials.json"), options.CredentialsPath, "credentials.json");

            Equal(Path.GetFullPath(Path.Join(localAppData, "env")), WebAgentOptions.FromCommandLine([], Env, localAppData).Options.DataDirectory, "the environment");
            Equal(false, WebAgentOptions.FromCommandLine([], _ => null, localAppData).Options.Faux, "real models by default");
            Equal(Path.GetFullPath("eq"), WebAgentOptions.FromCommandLine(["--data=eq"], _ => null, localAppData).Options.DataDirectory, "--data=<folder>");
            Equal(false, WebAgentOptions.FromCommandLine(["--faux=false"], _ => null, localAppData).Options.Faux, "--faux=false");
            Equal(true, WebAgentOptions.FromCommandLine(["--faux=true"], _ => null, localAppData).Options.Faux, "--faux=true");
            Throws<ArgumentException>(() => WebAgentOptions.FromCommandLine(["--data"], _ => null, localAppData), "--data without a folder");
            Throws<ArgumentException>(() => WebAgentOptions.FromCommandLine(["--data", "--faux"], _ => null, localAppData), "--data followed by a switch");
            Throws<ArgumentException>(() => WebAgentOptions.FromCommandLine(["--data="], _ => null, localAppData), "--data= without a folder");
            Throws<ArgumentException>(() => WebAgentOptions.FromCommandLine(["--faux=maybe"], _ => null, localAppData), "--faux=maybe");
        }),

        Case("host.migrates-on-start-and-a-restart-applies-nothing", async () =>
        {
            var root = Temp("restart");
            try
            {
                var data = Path.Join(root, "data");
                var options = new WebAgentOptions { DataDirectory = data };
                await using (var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = data })) await app.StopAsync();
                var first = ReadDb(options.DatabasePath, "SELECT id, applied_at FROM schema_migrations ORDER BY id;", r => (r.GetInt32(0), r.GetInt64(1)));
                Equal("1,100,200,300", string.Join(",", first.Select(row => row.Item1)), "core, accounts, chats and settings migrations");
                Equal(300L, ReadDb(options.DatabasePath, "PRAGMA user_version;", r => r.GetInt64(0)).Single(), "user_version");
                Equal("wal", ReadDb(options.DatabasePath, "PRAGMA journal_mode;", r => r.GetString(0)).Single(), "WAL");
                var keys = Directory.GetFiles(options.KeysDirectory, "key-*.xml");
                await using (var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = data }))
                    Equal(200, (await app.Get("/api/health")).Status, "the restart");
                var second = ReadDb(options.DatabasePath, "SELECT id, applied_at FROM schema_migrations ORDER BY id;", r => (r.GetInt32(0), r.GetInt64(1)));
                Equal(string.Join(",", first), string.Join(",", second), "nothing is applied twice");
                Equal(string.Join(",", keys), string.Join(",", Directory.GetFiles(options.KeysDirectory, "key-*.xml")), "the same key ring");
            }
            finally { DeleteTemp(root); }
        }),

        Case("host.creates-the-data-protection-key-ring", async () =>
        {
            await using var app = await WebAgentApp.StartAsync("keys");
            var keys = Directory.GetFiles(Path.Join(app.Root, "data", "keys"), "key-*.xml");
            Equal(1, keys.Length, "one key, created at start");
            var xml = await File.ReadAllTextAsync(keys[0]);
            if (OperatingSystem.IsWindows())
            {
                Check(xml.Contains("DpapiXmlDecryptor", StringComparison.Ordinal), "the key is protected with DPAPI");
                Check(xml.Contains("encryptedSecret", StringComparison.Ordinal), "no plain master key");
            }

            // The same registration in process: a value protected by one start is read by the next (the ring is persisted, not per process).
            var options = new WebAgentOptions { DataDirectory = Temp("keys-in-process") };
            try
            {
                string Protect(string text)
                {
                    using var provider = Services(options);
                    return provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect(text);
                }
                string Unprotect(string text)
                {
                    using var provider = Services(options);
                    return provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(text);
                }
                var sealedText = Protect("a secret");
                Check(!sealedText.Contains("a secret", StringComparison.Ordinal), "protected");
                Equal("a secret", Unprotect(sealedText), "read back by another start");
                Equal(1, Directory.GetFiles(options.KeysDirectory, "key-*.xml").Length, "in keys\\");
            }
            finally { DeleteTemp(options.DataDirectory); }
        }),

        Case("host.every-shell-page-renders-without-server-errors", async () =>
        {
            await using var app = await WebAgentApp.StartAsync("pages");
            var links = ShellNav.Primary.Append(ShellNav.SettingsLink).Concat(ShellNav.SettingsGroups.SelectMany(group => group.Items))
                .Select(item => item.Href).Distinct().ToList();
            var rendered = 0;
            foreach (var href in links)
            {
                var page = await app.Get(href);
                Check(page.Status < 500, $"{href}: {page.Status} {Cut(page.Text)}");
                var later = LaterFeature.Pages.GetValueOrDefault(href) ??
                    (href.StartsWith("/settings/", StringComparison.Ordinal) ? LaterFeature.SettingsPages.GetValueOrDefault(href["/settings/".Length..]) : null);
                if (later is not null)
                {
                    Equal(200, page.Status, href);
                    Check(page.Text.Contains("Coming in a later milestone", StringComparison.Ordinal), href + " is the neutral page");
                    Check(page.Text.Contains($"<title>{Html(later.Title)}", StringComparison.Ordinal), href + " title");
                    Check(page.Text.Contains(Html(later.Summary), StringComparison.Ordinal), href + " says what it will do");
                }
                else Check(page.Status is 200 or 404, $"{href}: a page of another unit renders or is not there yet ({page.Status})");
                if (page.Status != 200) continue;
                rendered++;
                CheckShell(href, page.Text);
                Equal(HostGuardOptions.DefaultContentSecurityPolicy, string.Join("", page.Response.Headers.GetValues("Content-Security-Policy")), href + " CSP");
            }
            Check(rendered >= LaterFeature.Pages.Count + LaterFeature.SettingsPages.Count, "every later-milestone page rendered");

            var playground = (await app.Get("/playground")).Text;
            Check(Regex.IsMatch(playground, "<a class=\"nav-link active\" aria-current=\"page\" href=\"/playground\" data-nav=\"playground\">"), "Playground is the active item");
            Check(playground.Contains("data-bs-target=\"#modal-shortcuts\"", StringComparison.Ordinal), "the shortcuts button on a main page");
            Check(playground.Contains("<h1 class=\"app-title\">Playground</h1>", StringComparison.Ordinal), "the top bar title");

            var spend = (await app.Get("/settings/spend")).Text;
            Check(spend.Contains("<nav class=\"app-breadcrumb\" aria-label=\"Breadcrumb\"><a href=\"/settings/providers\">Settings</a>", StringComparison.Ordinal) &&
                spend.Contains("<span class=\"current\">Spend</span>", StringComparison.Ordinal), "the settings breadcrumb");
            Check(spend.Contains("class=\"active\" aria-current=\"page\" href=\"/settings/spend\" data-settings-nav=\"spend\"", StringComparison.Ordinal), "Spend is the active settings item");
            Check(Regex.IsMatch(spend, "class=\"nav-link active\" aria-current=\"page\" href=\"/settings/providers\" data-nav=\"settings\""), "Settings is the active sidebar item");
            Check(!spend.Contains("data-bs-target=\"#modal-shortcuts\"", StringComparison.Ordinal), "no shortcuts button on settings pages, as in the mockups");
            Check(spend.Contains("<div class=\"settings-content \">", StringComparison.Ordinal) || spend.Contains("<div class=\"settings-content\">", StringComparison.Ordinal),
                "the page sits in .settings-content");
            Equal(404, (await app.Get("/settings/no-such-page")).Status, "an unknown settings page");
            Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
        }),

        Case("host.serves-the-shell-assets", async () =>
        {
            await using var app = await WebAgentApp.StartAsync("assets");
            var page = (await app.Get("/playground")).Text;
            var assets = Regex.Matches(page, "(?:href|src)=\"(/[^\"?#]+\\.(?:css|js|svg))(?:\\?[^\"]*)?\"").Select(match => match.Groups[1].Value)
                .Concat(["/js/shell.js", WebCommonAssets.ThemeJs]).Distinct().ToList();
            foreach (var expected in new[] { "/css/site.css", "/js/site.js", WebCommonAssets.ThemeCss, WebCommonAssets.ShellCss, WebCommonAssets.BootstrapCss,
                         WebCommonAssets.BootstrapIconsCss, WebCommonAssets.BootstrapJs, WebCommonAssets.ThemeBootJs, WebCommonAssets.Mark, WebCommonAssets.Favicon })
                Check(assets.Contains(expected), "the page links " + expected);
            foreach (var asset in assets)
            {
                var answer = await app.Get(asset);
                Equal(200, answer.Status, asset);
                var type = answer.Response.Content.Headers.ContentType?.MediaType ?? "";
                var expected = Path.GetExtension(asset) switch { ".css" => "text/css", ".js" => "text/javascript", _ => "image/svg+xml" };
                Equal(expected, type, asset + " type");
                Equal("no-cache", answer.Response.Headers.CacheControl?.ToString(), asset + " is revalidated, so an update is never masked by a cached module");
            }
            var shell = (await app.Get("/js/shell.js")).Text;
            Check(shell.Contains("from '/_content/PiSharp.Web.Common/js/theme.js'", StringComparison.Ordinal), "shell.js uses Web.Common's theme");
        }),
    ];

    /// <summary>The layout's fixed parts on <paramref name="href"/>: the sidebar in the mockups' order, the top bar, the shared modals and the
    /// toast area; and nothing the Content-Security-Policy would block (inline script or handlers).</summary>
    private static void CheckShell(string href, string html)
    {
        Check(html.TrimStart().StartsWith("<!doctype html>", StringComparison.Ordinal), $"{href}: the doctype comes first (no quirks mode): {Cut(html)}");
        var header = Regex.Match(html, "<header class=\"app-topbar\">(.*?)</header>", RegexOptions.Singleline);
        Check(header.Success && Regex.IsMatch(header.Groups[1].Value, "<h1 class=\"app-title\">|<nav class=\"app-breadcrumb\""),
            href + ": the title or breadcrumb is in the top bar");
        Equal(1, Regex.Matches(html, "app-breadcrumb|app-title\"").Count, href + ": one title, rendered once");
        var labels = Regex.Matches(html, "data-nav=\"[^\"]+\"><i class=\"bi [^\"]+\"></i><span>([^<]+)</span>").Select(match => match.Groups[1].Value).ToList();
        Equal("New chat|Playground|Compare|Tool Bench|History|Evals|Presets|Settings", string.Join("|", labels), href + " sidebar");
        foreach (var part in new[] { "<aside class=\"app-sidebar offcanvas-md offcanvas-start\"", "data-recent-chats", "<header class=\"app-topbar\">", "data-theme-toggle",
                     "id=\"modal-shortcuts\"", "id=\"modal-confirm\"", "id=\"toasts\"", "id=\"toast-template\"", "<main class=\"app-content", "data-theme-key=\"pisharp-theme\"" })
            Check(html.Contains(part, StringComparison.Ordinal), $"{href} has {part}");
        var openIndex = html.IndexOf("data-recent-chats", StringComparison.Ordinal);
        var settingsIndex = html.IndexOf("data-nav=\"settings\"", StringComparison.Ordinal);
        Check(openIndex < settingsIndex, href + ": the recent chats come before Settings");
        foreach (Match script in Regex.Matches(html, "<script\\b[^>]*>"))
            Check(script.Value.Contains("src=", StringComparison.Ordinal), $"{href}: no inline script ({script.Value})");
        Check(!Regex.IsMatch(html, "\\son[a-z]+\\s*=", RegexOptions.IgnoreCase), href + ": no inline event handlers");
    }

    private static ServiceProvider Services(WebAgentOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWebAgentCore(options);
        return services.BuildServiceProvider();
    }

    private static string Html(string text) => System.Text.Encodings.Web.HtmlEncoder.Default.Encode(text);

    private static string Cut(string text) => text.Length > 400 ? text[..400] + "…" : text;
}
