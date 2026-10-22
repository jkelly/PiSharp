using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PiSharp.Web.Common;

// Decision 0011 (run W2, WU3): PiSharp.Web.Common, the Razor class library the hub and PiSharp Web Agent share. The guard, the renderers and
// the provider logos run in process; the shared static files are checked as the hub serves them (a child process, as in HubCases), and the
// library is checked for anything hub-specific.
internal static partial class Program
{
    private static string WebCommonDirectory()
    {
        var metadata = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value);
        return Path.GetFullPath(metadata["WebCommonProject"]!);
    }

    private static string HubDirectory()
    {
        var metadata = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value);
        return Path.GetFullPath(metadata["WebProject"]!);
    }

    /// <summary>One request through a <see cref="HostGuard"/> built from <paramref name="options"/>: the status, whether the request reached
    /// the app behind the guard, and the guard's own answer.</summary>
    private static async Task<(int Status, bool Passed, string Body)> GuardAsync(HostGuardOptions options, string method, string host, string path = "/",
        string? origin = null, string? contentType = null, long? length = null, string scheme = "http")
    {
        var (status, passed, body, _) = await GuardResponseAsync(options, method, host, path, origin, contentType, length, scheme);
        return (status, passed, body);
    }

    /// <summary><see cref="GuardAsync"/>, with the answer's headers.</summary>
    private static async Task<(int Status, bool Passed, string Body, IHeaderDictionary Headers)> GuardResponseAsync(HostGuardOptions options, string method,
        string host, string path = "/", string? origin = null, string? contentType = null, long? length = null, string scheme = "http")
    {
        var passed = false;
        var guard = new HostGuard(_ => { passed = true; return Task.CompletedTask; }, Options.Create(options));
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.Scheme = scheme;
        http.Request.Host = new HostString(host);
        http.Request.Path = path;
        if (origin is not null) http.Request.Headers.Origin = origin;
        if (contentType is not null) http.Request.ContentType = contentType;
        http.Request.ContentLength = length;
        using var body = new MemoryStream();
        http.Response.Body = body;
        await guard.InvokeAsync(http);
        return (http.Response.StatusCode, passed, Encoding.UTF8.GetString(body.ToArray()), http.Response.Headers);
    }

    internal static IEnumerable<(string, Func<Task>)> WebCommonCases() =>
    [
        Case("webcommon.guard-answers-loopback-and-configured-hosts-and-ports", async () =>
        {
            var loopback = new HostGuardOptions();
            foreach (var host in new[] { "localhost:5180", "127.0.0.1:5180", "[::1]:5180", "LOCALHOST", "127.0.0.1" })
            {
                var answer = await GuardAsync(loopback, "GET", host);
                Check(answer.Passed && answer.Status == 200, $"{host} is answered by default: {answer.Status}");
            }
            var foreign = await GuardAsync(loopback, "GET", "evil.example:5180");
            Equal(421, foreign.Status, "a foreign Host");
            Check(!foreign.Passed, "a foreign Host never reaches the app");
            Equal("Misdirected request: this server answers only localhost.", foreign.Body, "the default 421 body");
            Equal(421, (await GuardAsync(loopback, "GET", "127.0.0.2:5180")).Status, "another loopback address is not a known name");
            Equal(421, (await GuardAsync(loopback, "GET", "localhost.evil.example")).Status, "a name that only starts like localhost");

            var named = new HostGuardOptions { AllowedHosts = ["pisharp.lan", "[fe80::1]"], MisdirectedMessage = "Not here." };
            Check((await GuardAsync(named, "GET", "PiSharp.LAN:5180")).Passed, "an allowed host, in any case");
            Check((await GuardAsync(named, "GET", "[fe80::1]:5180")).Passed, "an allowed IPv6 host given with brackets");
            Check((await GuardAsync(named, "GET", "localhost:5180")).Passed, "the loopback names stay allowed");
            var refused = await GuardAsync(named, "GET", "other.lan");
            Equal((421, "Not here."), (refused.Status, refused.Body), "the configured 421 body");

            var ports = new HostGuardOptions { AllowedPorts = [5180, 443] };
            Check((await GuardAsync(ports, "GET", "127.0.0.1:5180")).Passed, "an allowed port");
            Equal(421, (await GuardAsync(ports, "GET", "127.0.0.1:5181")).Status, "another port");
            Equal(421, (await GuardAsync(ports, "GET", "localhost")).Status, "no port means 80 over http");
            Check((await GuardAsync(ports, "GET", "localhost", scheme: "https")).Passed, "no port means 443 over https");
            Throws<ArgumentException>(() => new HostGuard(_ => Task.CompletedTask, Options.Create(new HostGuardOptions { JsonPaths = ["api"] })),
                "a JSON path without a leading slash");
        }),

        Case("webcommon.guard-sends-the-content-security-policy", async () =>
        {
            Equal(0, (await GuardResponseAsync(new HostGuardOptions(), "GET", "localhost:5180")).Headers.ContentSecurityPolicy.Count, "no policy by default");
            var strict = new HostGuardOptions { ContentSecurityPolicy = HostGuardOptions.DefaultContentSecurityPolicy };
            var page = await GuardResponseAsync(strict, "GET", "localhost:5180");
            Equal(HostGuardOptions.DefaultContentSecurityPolicy, page.Headers.ContentSecurityPolicy.ToString(), "the policy on a page");
            foreach (var directive in new[] { "default-src 'self'", "script-src 'self'", "object-src 'none'", "frame-ancestors 'none'", "base-uri 'none'" })
                Check(HostGuardOptions.DefaultContentSecurityPolicy.Contains(directive + ";", StringComparison.Ordinal) ||
                    HostGuardOptions.DefaultContentSecurityPolicy.EndsWith(directive, StringComparison.Ordinal), "the default policy has " + directive);
            Check(!HostGuardOptions.DefaultContentSecurityPolicy.Contains("script-src 'self' 'unsafe", StringComparison.Ordinal), "no inline or eval scripts");
            Equal(0, (await GuardResponseAsync(strict, "GET", "evil.example")).Headers.ContentSecurityPolicy.Count, "a refused host gets only its 421");
        }),

        Case("webcommon.guard-refuses-cross-origin-and-non-json-writes", async () =>
        {
            var options = new HostGuardOptions();
            const string host = "127.0.0.1:5180";
            var cross = await GuardAsync(options, "POST", host, "/api/chats", origin: "http://evil.example", contentType: "application/json", length: 2);
            Equal((403, false), (cross.Status, cross.Passed), "a write from another origin");
            Equal(403, (await GuardAsync(options, "DELETE", host, "/api/chats/1", origin: "http://127.0.0.1:5181")).Status, "another port is another origin");
            Equal(403, (await GuardAsync(options, "PUT", host, "/settings", origin: "null")).Status, "an opaque origin");
            Check((await GuardAsync(options, "POST", host, "/api/chats", origin: "http://127.0.0.1:5180", contentType: "application/json; charset=utf-8", length: 2)).Passed,
                "a same-origin JSON write");
            Check((await GuardAsync(options, "GET", host, "/api/stream", origin: "http://evil.example")).Passed, "reads are not origin-checked");
            Check((await GuardAsync(options, "POST", host, "/api/chats/1/abort")).Passed, "a write without an Origin header (not from a browser page) or a body");

            var form = await GuardAsync(options, "POST", host, "/api/chats", contentType: "application/x-www-form-urlencoded", length: 5);
            Equal((415, false), (form.Status, form.Passed), "a form post to the API");
            Equal(415, (await GuardAsync(options, "PUT", host, "/api", contentType: "text/plain", length: 1)).Status, "the API root itself");
            Check((await GuardAsync(options, "POST", host, "/apiary", contentType: "text/plain", length: 1)).Passed, "a path that only starts with the same letters");
            Check((await GuardAsync(options, "POST", host, "/settings/save", contentType: "application/x-www-form-urlencoded", length: 5)).Passed,
                "a form post outside the JSON paths");

            var commands = new HostGuardOptions { JsonPaths = ["/api", "/commands"] };
            Equal(415, (await GuardAsync(commands, "POST", host, "/commands/run", contentType: "text/plain", length: 1)).Status, "a configured JSON path");
        }),

        Case("webcommon.markdown-renders-and-sanitizes", () =>
        {
            var markdown = new MarkdownRenderer();
            var html = markdown.Render("**bold** and `code` <script>alert(1)</script> <img src=x onerror=alert(1)>");
            Check(html.Contains("<strong>bold</strong>", StringComparison.Ordinal) && html.Contains("<code>code</code>", StringComparison.Ordinal), "Markdown: " + html);
            Check(!html.Contains("<script", StringComparison.OrdinalIgnoreCase) && !html.Contains("<img", StringComparison.OrdinalIgnoreCase),
                "raw HTML is never rendered: " + html);
            Check(html.Contains("&lt;script&gt;", StringComparison.Ordinal), "raw HTML shows as text: " + html);
            var links = markdown.Render("[site](https://example.com/a) [bad](javascript:alert(1)) <https://example.com/b>");
            Check(links.Contains("href=\"https://example.com/a\"", StringComparison.Ordinal) && links.Contains("href=\"https://example.com/b\"", StringComparison.Ordinal),
                "http links stay: " + links);
            Check(!links.Contains("javascript:", StringComparison.OrdinalIgnoreCase), "javascript: links go: " + links);
            Equal(Regex.Matches(links, "<a ").Count, Regex.Matches(links, "target=\"_blank\" rel=\"noopener noreferrer\"").Count,
                "every link opens a new tab without the opener: " + links);
            Check(markdown.Render("| a | b |\n|---|---|\n| 1 | 2 |").Contains("<table>", StringComparison.Ordinal), "pipe tables");
            Equal("", markdown.Render(null!), "no text");

            var clean = markdown.Sanitize("<p class=\"note\" onclick=\"x()\">hi</p><script>bad()</script><a href=\"javascript:x\">a</a><iframe src=\"https://example.com\"></iframe>");
            Check(clean.Contains("<p class=\"note\">hi</p>", StringComparison.Ordinal), "classes stay, handlers go: " + clean);
            Check(!clean.Contains("script", StringComparison.OrdinalIgnoreCase) && !clean.Contains("javascript:", StringComparison.OrdinalIgnoreCase) &&
                !clean.Contains("iframe", StringComparison.OrdinalIgnoreCase), "Sanitize removes what could run: " + clean);
        }),

        Case("webcommon.fragment-renderer-renders-library-partials", async () =>
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = Temp("fragments"), EnvironmentName = "Production" });
            var library = typeof(FragmentRenderer).Assembly;
            builder.Services.AddControllersWithViews().ConfigureApplicationPartManager(parts =>
            {
                if (!parts.ApplicationParts.OfType<AssemblyPart>().Any(part => part.Assembly == library))
                    foreach (var part in ApplicationPartFactory.GetApplicationPartFactory(library).GetApplicationParts(library)) parts.ApplicationParts.Add(part);
            });
            builder.Services.AddWebCommon();
            await using var app = builder.Build();
            var renderer = app.Services.GetRequiredService<FragmentRenderer>();
            Check(ReferenceEquals(renderer, app.Services.GetRequiredService<FragmentRenderer>()), "one renderer");
            Check(ReferenceEquals(app.Services.GetRequiredService<MarkdownRenderer>(), app.Services.GetRequiredService<MarkdownRenderer>()), "one Markdown renderer");

            var head = await renderer.RenderAsync("/Views/Shared/_WebCommonHead.cshtml", "test-theme");
            var order = new[] { WebCommonAssets.Favicon, WebCommonAssets.ThemeBootJs, WebCommonAssets.BootstrapCss, WebCommonAssets.BootstrapIconsCss,
                WebCommonAssets.ThemeCss, WebCommonAssets.ShellCss }.Select(url => head.IndexOf(url, StringComparison.Ordinal)).ToList();
            Check(order.All(index => index >= 0) && order.SequenceEqual(order.Order()), "the head links, in order: " + head);
            Check(head.Contains("data-theme-key=\"test-theme\"", StringComparison.Ordinal), "the model is the theme key: " + head);
            Check(!head.Contains("<script>", StringComparison.Ordinal), "no inline script, so a strict CSP holds: " + head);
            Check((await renderer.RenderAsync("/Views/Shared/_WebCommonHead.cshtml", null)).Contains("data-theme-key=\"pisharp-theme\"", StringComparison.Ordinal),
                "the default theme key");
            var missing = await ThrowsAsync<InvalidOperationException>(() => renderer.RenderAsync("~/Views/Nope/_Missing.cshtml", null), "a missing partial");
            Check(missing.Message.StartsWith("No view ~/Views/Nope/_Missing.cshtml", StringComparison.Ordinal), missing.Message);
        }),

        Case("webcommon.provider-logos-map-to-bundled-files", () =>
        {
            Equal("openai", ProviderLogos.Key("openai"), "openai");
            Equal("codex", ProviderLogos.Key("openai-codex"), "codex");
            Equal("githubcopilot", ProviderLogos.Key("github-copilot"), "copilot");
            Equal("kimi", ProviderLogos.Key("moonshot"), "moonshot");
            Equal(null, ProviderLogos.Key("my-gateway"), "a custom gateway has no mark");
            Equal(null, ProviderLogos.Url(null), "no provider");
            Equal("/_content/PiSharp.Web.Common/img/providers/anthropic.svg", ProviderLogos.Url("anthropic"), "the URL");
            var providers = Path.Join(WebCommonDirectory(), "wwwroot", "img", "providers");
            foreach (var id in new[] { "openai", "openai-codex", "anthropic", "google", "google-vertex", "azure-openai-responses", "amazon-bedrock",
                         "github-copilot", "openrouter", "xai", "mistral", "deepseek", "lmstudio", "ollama", "cloudflare-workers-ai", "kimi-coding", "moonshot" })
                Check(File.Exists(Path.Join(providers, ProviderLogos.Key(id) + ".svg")), $"{id}'s mark is bundled");
            Check(File.Exists(Path.Join(providers, "LICENSE")), "the logos' license is bundled");
        }),

        Case("webcommon.library-has-nothing-hub-specific", () =>
        {
            var library = typeof(FragmentRenderer).Assembly;
            var references = library.GetReferencedAssemblies().Select(name => name.Name!).Where(name => name.StartsWith("PiSharp", StringComparison.Ordinal)).ToList();
            Equal("", string.Join(", ", references), "PiSharp.Web.Common references no other PiSharp assembly");
            var root = WebCommonDirectory();
            var hubWords = new Regex(@"/api/devices|/api/ui-state|pisharp-durable|\bdn-[a-z]|HubOptions|DeviceRegistry|StreamMux|deviceApi", RegexOptions.IgnoreCase);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                         .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                             !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                             !file.Contains($"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                             Path.GetExtension(file) is ".cs" or ".cshtml" or ".js" or ".css"))
            {
                var match = hubWords.Match(File.ReadAllText(file));
                Check(!match.Success, $"{Path.GetRelativePath(root, file)} names the hub's {match.Value}");
            }
            // The hub keeps no copy of what moved, so the two cannot drift apart.
            var hub = HubDirectory();
            foreach (var moved in new[] { "Hub/FragmentRenderer.cs", "Hub/MarkdownRenderer.cs", "Hub/HostGuard.cs", "wwwroot/js/stream.js", "wwwroot/js/swap.js",
                         "wwwroot/css/pisharp-theme.css", "wwwroot/css/app.css", "wwwroot/lib", "wwwroot/img" })
                Check(!File.Exists(Path.Join(hub, moved)) && !Directory.Exists(Path.Join(hub, moved)), $"the hub still has {moved}");
        }),

        Case("webcommon.hub-serves-the-shared-static-files", async () =>
        {
            await using var hub = await Hub.StartAsync("webcommon-assets");
            var expected = new (string Path, string Type, string? Contains)[]
            {
                (WebCommonAssets.ThemeCss, "text/css", "--ps-bg"),
                (WebCommonAssets.ShellCss, "text/css", ".msg-content"),
                (WebCommonAssets.BootstrapCss, "text/css", "Bootstrap  v5.3.3"),
                (WebCommonAssets.BootstrapIconsCss, "text/css", "Bootstrap Icons v1.11.3"),
                (WebCommonAssets.Root + "/lib/bootstrap-icons/fonts/bootstrap-icons.woff2", "font/woff2", null),
                (WebCommonAssets.BootstrapJs, "text/javascript", null),
                (WebCommonAssets.StreamJs, "text/javascript", "export class MuxStream"),
                (WebCommonAssets.SwapJs, "text/javascript", "export function applyItems"),
                (WebCommonAssets.ThemeJs, "text/javascript", "export function initTheme"),
                (WebCommonAssets.ThemeBootJs, "text/javascript", "data-theme-key"),
                (WebCommonAssets.Mark, "image/svg+xml", null),
                (WebCommonAssets.Favicon, "image/svg+xml", null),
                (ProviderLogos.Url("openai")!, "image/svg+xml", null),
            };
            foreach (var (path, type, contains) in expected)
            {
                using var response = await hub.Http.GetAsync(new Uri(hub.Base, path));
                Equal(HttpStatusCode.OK, response.StatusCode, path);
                Equal(type, response.Content.Headers.ContentType?.MediaType, path + "'s type");
                if (contains is not null) Check((await response.Content.ReadAsStringAsync()).Contains(contains, StringComparison.Ordinal), $"{path} has {contains}");
            }
            foreach (var old in new[] { "/css/pisharp-theme.css", "/css/app.css", "/js/stream.js", "/js/swap.js", "/lib/bootstrap/css/bootstrap.min.css", "/img/mark.svg" })
                Equal(404, (await hub.Get(old)).Status, old + " moved to " + WebCommonAssets.Root);
            Equal(421, (await hub.SendAsync(HttpMethod.Get, WebCommonAssets.ThemeCss, host: "evil.example")).Status, "the guard runs before static files");
            using (var home = await hub.Http.GetAsync(hub.Base))
                Equal(HostGuardOptions.DefaultContentSecurityPolicy, string.Join(";", home.Headers.GetValues("Content-Security-Policy")), "the hub's policy");

            // Every local file a page links, and every module the hub's scripts import, is served.
            var pages = new[] { "/", "/devices", "/executions", "/research" };
            var files = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var page in pages)
            {
                var html = (await hub.Get(page)).Text;
                Check(html.Contains($"href=\"{WebCommonAssets.ShellCss}?v=", StringComparison.Ordinal), $"{page} links the shared shell, versioned");
                Check(html.Contains($"data-theme-key=\"pisharp-durable-theme\"", StringComparison.Ordinal), $"{page} keeps the hub's theme key");
                foreach (Match link in Regex.Matches(html, "(?:href|src)=\"(/[^\"?#]+\\.(?:css|js|svg|png|woff2?))(?:\\?[^\"]*)?\"")) files.Add(link.Groups[1].Value);
            }
            var scripts = new Queue<string>(files.Where(file => file.EndsWith(".js", StringComparison.Ordinal) && !file.StartsWith(WebCommonAssets.Root + "/lib/", StringComparison.Ordinal)));
            while (scripts.TryDequeue(out var script))
            {
                foreach (Match import in Regex.Matches((await hub.Get(script)).Text, @"(?:^|\n)\s*(?:import|export)\s[^;]*?from\s+'([^']+)'"))
                {
                    var module = new Uri(new Uri(hub.Base, script), import.Groups[1].Value).AbsolutePath;
                    if (files.Add(module)) scripts.Enqueue(module);
                }
            }
            Check(files.Contains(WebCommonAssets.StreamJs) && files.Contains(WebCommonAssets.SwapJs) && files.Contains(WebCommonAssets.ThemeJs),
                "the hub's scripts import the shared modules: " + string.Join(", ", files));
            foreach (var file in files) Equal(200, (await hub.Get(file)).Status, file);
        }),
    ];
}
