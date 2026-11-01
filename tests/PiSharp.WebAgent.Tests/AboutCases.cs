using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PiSharp.Web.Common;
using PiSharp.WebAgent.About;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Faux;
using PiSharp.WebAgent.Hosting;

// Decision 0011 (run W3, unit AP): Settings › About. The page shows what the running app measured (versions of the built assemblies, the
// loopback address it listens on, the faux catalog's numbers, an idle connectivity check); the diagnostics report and every About answer
// hold no planted secret (a stored key, an environment key, a sign-in's tokens, an account's label, email and identity, a base URL's user
// info and query, a secret echoed in a status reason); the connectivity check runs only on POST, through the credentials area's connection
// tester and a fake HttpMessageHandler (never the network), classifies ok, slow, failed and not tested, joins a check already running, and
// with --faux sends nothing and says so. The notices name the versions of the files Web.Common bundles.
internal static partial class Program
{
    /// <summary>A connection tester that answers from a function and records which accounts it tested.</summary>
    private sealed class ScriptedTester(Func<string, Task<ConnectionTestResult>> answer) : IConnectionTester
    {
        public ConcurrentQueue<string> Tested { get; } = new();

        public Task<ConnectionTestResult> TestAccountAsync(string accountId, CancellationToken cancellationToken = default)
        {
            Tested.Enqueue(accountId);
            return answer(accountId);
        }

        public Task<ConnectionTestResult> TestKeyAsync(string provider, string apiKey, IReadOnlyDictionary<string, string>? settings = null,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("The About check never tests an unsaved key.");
    }

    private static AboutPage SamplePage(WebAgentOptions options, IReadOnlyList<AccountInfo> accounts, ConnectivityRun? last = null) => new(
        new VersionFacts("1.2.0.1", "1.2.0.1", "1.2.0", "aaf86e0c", "Release", new DateTimeOffset(2026, 10, 29, 8, 0, 0, TimeSpan.Zero), ".NET 10.0.3", "10.0.3"),
        new HostFacts("Microsoft Windows 10.0.26300 · X64", ["http://127.0.0.1:5180"], true, DateTimeOffset.UtcNow.AddHours(-2), TimeSpan.FromMinutes(134),
            options.DataDirectory, options.Faux),
        new CatalogStats(3, 12, 5, 4, 1, null, ["faux"]),
        accounts, new DefaultsSettings("openai", "gpt-test", "medium"), new ToolsSettings(@"C:\work", ToolApprovalMode.AllowList, [new ToolAllowRule("bash", "git *")]),
        new AboutConnectivityView([], last, options.Faux, TimeZoneInfo.Utc), ThirdPartyComponents.All, options, DateTimeOffset.UtcNow);

    private static WebApplication AboutApp(string root, int port, CredentialsOptions credentials, ConcurrentQueue<string> logs)
    {
        var options = new WebAgentOptions { DataDirectory = Path.Join(root, "data") };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root, EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new ListLoggerProvider(logs));
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddSingleton(credentials);
        builder.Services.AddWebAgentCore(options);
        // The entry assembly is this test runner, so the app's controllers and views are added as parts, as RenderHost does.
        builder.Services.AddControllersWithViews().ConfigureApplicationPartManager(parts =>
        {
            foreach (var assembly in new[] { typeof(WebAgentHost).Assembly, typeof(FragmentRenderer).Assembly })
                if (!parts.ApplicationParts.OfType<AssemblyPart>().Any(part => part.Assembly == assembly))
                    foreach (var part in ApplicationPartFactory.GetApplicationPartFactory(assembly).GetApplicationParts(assembly))
                        parts.ApplicationParts.Add(part);
        });
        builder.Services.AddCredentialsArea(options);
        builder.Services.AddAboutArea(options);
        builder.Services.AddWebAgentFallbacks(options);
        var web = builder.Build();
        web.UseWebAgentCore();
        web.MapCredentialsArea();
        web.MapAboutArea();
        web.MapWebAgentCore();
        return web;
    }

    internal static IEnumerable<(string, Func<Task>)> AboutCases() =>
    [
        Case("about.report-holds-no-secret-name-or-token", () =>
        {
            var key = Planted("sk-proj-");
            var token = Planted("eyJ");
            var label = "Label" + Planted("L");
            var email = Planted("mail") + "@example.test";
            var identity = Planted("acct-");
            var userInfo = Planted("pw");
            var query = Planted("q");
            var options = new WebAgentOptions { DataDirectory = Path.Join(Path.GetTempPath(), "pisharp-webagent", "report-sample") };
            AccountInfo[] accounts =
            [
                new() { Id = "acc-0000000000000001", Provider = "openai", Label = label, Kind = AccountKind.ApiKey, IsActive = true, Hint = SecretText.Mask(key),
                    Settings = new Dictionary<string, string> { ["baseUrl"] = $"https://me:{userInfo}@gateway.example.test/v1?key={query}" } },
                new() { Id = "acc-0000000000000002", Provider = "openai-codex", Label = label + "2", Kind = AccountKind.OAuth, IsActive = true, Identity = identity,
                    Details = new Dictionary<string, string> { ["plan"] = "plus", ["email"] = email }, ExpiresAt = DateTimeOffset.UtcNow.AddDays(9),
                    Status = AccountStatus.Expired, StatusReason = "The provider said: invalid token " + token },
                new() { Id = "env-anthropic-anthropic_api_key", Provider = "anthropic", Label = "ANTHROPIC_API_KEY", Kind = AccountKind.Environment, IsActive = true,
                    Source = "ANTHROPIC_API_KEY", Hint = SecretText.Mask(key) },
                new() { Id = "acc-0000000000000003", Provider = "github-copilot", Label = label + "3", Kind = AccountKind.OAuth,
                    Details = new Dictionary<string, string> { ["host"] = "github.com" } },
            ];
            var run = new ConnectivityRun(DateTimeOffset.UtcNow, false, null,
            [
                new("openai", "OpenAI", "acc-0000000000000001", AccountKind.ApiKey, ConnectivityOutcome.Ok, "GET gateway.example.test/v1/models · 200 · 3 models") { LatencyMs = 120 },
                new("anthropic", "Anthropic", "env-anthropic-anthropic_api_key", AccountKind.Environment, ConnectivityOutcome.Failed, "401 invalid x-api-key " + key),
            ]);
            var report = SamplePage(options, accounts, run).Report;
            NoSecret(report, "the diagnostics report", key, token, label, email, identity, userInfo, query, SecretText.Mask(key));
            Check(!report.Contains("git *", StringComparison.Ordinal), "no allow-list command");
            Check(report.StartsWith(DiagnosticsReport.Title + " · ", StringComparison.Ordinal), "the title first");
            Check(report.Contains(DiagnosticsReport.Promise, StringComparison.Ordinal), "the promise");
            foreach (var part in new[] { "app             1.2.0.1 · Release · commit aaf86e0c", "pisharp         1.2.0.1 (tracks Pi 1.2.0)", "http://127.0.0.1:5180 (loopback only)",
                         "uptime          2h 14m", "openai          apiKey       connected    active  stored, encrypted · baseUrl https://gateway.example.test",
                         "ANTHROPIC_API_KEY", "plan plus", "host github.com", "defaultModel    openai/gpt-test", "toolApproval    allowList (1 rule)", @"cwd             C:\work",
                         "built-in        not read (--faux)", "not built in    faux", "connectivity    checked", "1 of 2 reachable", "120 ms", options.CredentialsPath })
                Check(report.Contains(part, StringComparison.Ordinal), "the report has " + part + ":\n" + report);
            Check(!report.Contains('\r'), "one kind of line break");
            Check(SamplePage(options, []).Report.Contains("connectivity    not checked since the server started", StringComparison.Ordinal), "before the first check");
            Check(SamplePage(options, []).Report.Contains("accounts (secrets, names and emails omitted)\n(none)", StringComparison.Ordinal), "no account");
            Equal("pisharp-webagent-diagnostics-2026-10-29.txt", DiagnosticsReport.FileName(new DateTimeOffset(2026, 10, 29, 23, 0, 0, TimeSpan.Zero)), "the file name");
        }),

        Case("about.facts-read-the-running-build", () =>
        {
            var (version, commit) = AboutFacts.VersionOf(typeof(AboutFacts).Assembly);
            Check(version.Length > 0 && char.IsDigit(version[0]), "the app's version: " + version);
            Check(commit is null || Regex.IsMatch(commit, "^[0-9a-f]{8}$"), "an 8-digit commit or none: " + commit);
            Equal("1.2.0", AboutFacts.TracksPi("1.2.0.1"), "the Pi release of a PiSharp version");
            Equal("1.1.0", AboutFacts.TracksPi("1.1.0-preview.2"), "a prerelease");
            Check(AboutFacts.IsLoopback("http://127.0.0.1:5180") && AboutFacts.IsLoopback("http://localhost:5180") && AboutFacts.IsLoopback("http://[::1]:5180"), "loopback");
            Check(!AboutFacts.IsLoopback("http://0.0.0.0:5180") && !AboutFacts.IsLoopback("http://192.168.1.4:5180") && !AboutFacts.IsLoopback("nonsense"), "not loopback");
            Equal("2d 3h", AboutFacts.Duration(TimeSpan.FromHours(51)), "days");
            Equal("2h 14m", AboutFacts.Duration(TimeSpan.FromMinutes(134)), "hours");
            Equal("5m 12s", AboutFacts.Duration(TimeSpan.FromSeconds(312)), "minutes");
            Equal("40s", AboutFacts.Duration(TimeSpan.FromSeconds(40)), "seconds");
            var facts = new AboutFacts(new WebAgentOptions { DataDirectory = @"C:\data", Faux = true });
            Equal(version, facts.Versions.App, "the versions are the assembly's");
            Equal(AboutFacts.TracksPi(facts.Versions.PiSharp), facts.Versions.TracksPi, "PiSharp tracks a Pi release");
            Check(facts.Versions.Runtime.StartsWith(".NET ", StringComparison.Ordinal), "the runtime: " + facts.Versions.Runtime);
            var host = facts.Host();
            Check(host.Addresses.Count == 0 && !host.LoopbackOnly, "without a server, no address is claimed");
            Check(host.Uptime >= TimeSpan.Zero && host.Faux, "uptime and the faux switch");

            var faux = new FauxModelCatalog(FauxModelCatalog.CreateModels());
            var stats = CatalogFacts.Read(faux, new ModelsSettings(["faux/echo"]), new WebAgentOptions { DataDirectory = @"C:\data", Faux = true });
            var models = faux.Models(FauxModelCatalog.Provider.Id);
            Equal(1, stats.Providers, "one provider");
            Equal(models.Count, stats.ChatModels, "its models");
            Equal(models.Count(model => model.Reasoning), stats.Reasoning, "reasoning models");
            Equal(1, stats.Hidden, "hidden models");
            Check(stats.Builtin is null && stats.NotBuiltin.SequenceEqual(["faux"]), "with --faux the built-in catalog is not read");
            var live = CatalogFacts.Read(faux, new ModelsSettings([]), new WebAgentOptions { DataDirectory = @"C:\data" });
            Check(live.Builtin is { Shards: > 0, Providers: > 0, ChatModels: > 0 } builtin && Regex.IsMatch(builtin.ManifestSha256, "^[0-9a-f]{64}$"),
                "the built-in catalog's shards, providers, models and manifest hash");
            Check(live.NotBuiltin.SequenceEqual(["faux"]), "faux is not a built-in provider");
        }),

        Case("about.notices-name-the-bundled-versions", () =>
        {
            var common = WebProjectFile("..", "PiSharp.Web.Common", "wwwroot");
            var bootstrap = File.ReadAllText(Path.Join(common, "lib", "bootstrap", "css", "bootstrap.min.css"))[..400];
            Check(bootstrap.Contains("Bootstrap  v" + ThirdPartyComponents.BootstrapVersion + " ", StringComparison.Ordinal), "Bootstrap's header: " + bootstrap[..120]);
            var icons = File.ReadAllText(Path.Join(common, "lib", "bootstrap-icons", "bootstrap-icons.min.css"))[..300];
            Check(icons.Contains("Bootstrap Icons v" + ThirdPartyComponents.BootstrapIconsVersion + " ", StringComparison.Ordinal), "Bootstrap Icons' header");
            var logos = File.ReadAllText(Path.Join(common, "img", "providers", "README.md"));
            Check(logos.Contains("**" + ThirdPartyComponents.LobeIconsVersion + "**", StringComparison.Ordinal), "the provider logos' README");
            var all = ThirdPartyComponents.All;
            foreach (var name in new[] { "Markdig", "HtmlSanitizer", "AngleSharp", "Microsoft.Data.Sqlite", "SQLitePCLRaw" })
                Check(all.Single(component => component.Name == name).Version is { Length: > 0 } version && char.IsDigit(version[0]), name + "'s version is read from its assembly");
            Check(all[0].Name.StartsWith("Pi ", StringComparison.Ordinal) && all[0].License == "MIT", "Pi first, MIT");
        }),

        Case("about.connectivity-check-classifies-joins-and-goes-offline", async () =>
        {
            await using var host = new VaultHost("about-check");
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var provider in new[] { "openai", "anthropic", "google", "openrouter" })
                ids[provider] = (await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest(provider, Planted("sk-")))).Account.Id;
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tester = new ScriptedTester(async id =>
            {
                await gate.Task;
                var provider = host.Accounts.Get(id)!.Provider;
                return provider switch
                {
                    "openai" => new ConnectionTestResult(true, "Connected") { StatusCode = 200, LatencyMs = 180, ModelCount = 17, Endpoint = "GET https://api.openai.com/v1/models" },
                    "anthropic" => new ConnectionTestResult(true, "Connected") { StatusCode = 200, LatencyMs = 1_240, ModelCount = 1, Endpoint = "GET https://api.anthropic.com/v1/models" },
                    "google" => new ConnectionTestResult(false, "401 · the key was rejected") { StatusCode = 401, LatencyMs = 90 },
                    _ => new ConnectionTestResult(false, "A connection test for openrouter is not available yet.") { Supported = false },
                };
            });
            var check = new ConnectivityCheck(host.Accounts, tester, new FauxModelCatalog(FauxModelCatalog.CreateModels()), host.Options);
            Equal(4, check.Targets().Count, "every provider's active account");
            Equal(null, check.Last, "nothing before a check");
            var first = check.RunAsync();
            var second = check.RunAsync();
            Check(!first.IsCompleted, "the check waits on the tester");
            gate.SetResult();
            var run = await first;
            Check(ReferenceEquals(run, await second), "a click while a check runs joins it");
            Equal(4, tester.Tested.Count, "each account tested once");
            var outcome = run.Results.ToDictionary(result => result.Provider, result => result.Outcome);
            Equal(ConnectivityOutcome.Ok, outcome["openai"], "ok");
            Equal(ConnectivityOutcome.Slow, outcome["anthropic"], "slower than a second");
            Equal(ConnectivityOutcome.Failed, outcome["google"], "refused");
            Equal(ConnectivityOutcome.NotTested, outcome["openrouter"], "no known test");
            Equal("GET api.openai.com/v1/models · 200 · 17 models", run.Results.Single(r => r.Provider == "openai").Detail, "the request and its answer");
            Equal("Slow: GET api.anthropic.com/v1/models · 200 · 1 model", run.Results.Single(r => r.Provider == "anthropic").Detail, "slow is said");
            Equal("2 of 3 reachable · 1 not tested", run.Summary, "the summary");
            Check(ReferenceEquals(run, check.Last), "kept as the last check");
            var again = await check.RunAsync();
            Check(!ReferenceEquals(run, again), "a later click runs a new check");
            Equal(8, tester.Tested.Count, "the second check tested again");

            var offlineTester = new ScriptedTester(_ => throw new InvalidOperationException("an offline check sent a test"));
            var offline = new ConnectivityCheck(host.Accounts, offlineTester, new FauxModelCatalog(FauxModelCatalog.CreateModels()),
                new WebAgentOptions { DataDirectory = host.Options.DataDirectory, Faux = true });
            var none = await offline.RunAsync();
            Check(none.Offline && none.Results.Count == 0 && none.Message == ConnectivityCheck.OfflineMessage, "offline: nothing tested, and it says so");
            Equal(ConnectivityCheck.OfflineMessage, none.Summary, "the summary says why");
            Equal(0, offlineTester.Tested.Count, "nothing sent");
        }),

        Case("about.connectivity-runs-only-on-post-through-the-fake-handler", async () =>
        {
            var root = Temp("about-inproc");
            var logs = new ConcurrentQueue<string>();
            var http = new FakeOAuth();
            var envKey = Planted("sk-proj-");
            var storedKey = Planted("sk-ant-api03-");
            http.On("GET https://api.openai.com/v1/models", request => request.Header("authorization") == "Bearer " + envKey
                ? FakeOAuth.Json(200, new JsonObject { ["data"] = new JsonArray(new JsonObject { ["id"] = "m1" }, new JsonObject { ["id"] = "m2" }) })
                : FakeOAuth.Json(401, new JsonObject { ["error"] = "wrong key" }));
            http.On("GET https://api.anthropic.com/v1/models", _ =>
                FakeOAuth.Json(401, new JsonObject { ["error"] = new JsonObject { ["message"] = "invalid x-api-key " + storedKey } }));
            var environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["OPENAI_API_KEY"] = envKey };
            var port = FreePort();
            var web = AboutApp(root, port, new CredentialsOptions
            {
                Environment = name => environment.TryGetValue(name, out var value) ? value : null, FileExists = _ => false, Home = root,
                CreateHttp = () => new HttpMessageInvoker(http, disposeHandler: false), TestTimeout = TimeSpan.FromSeconds(10),
            }, logs);
            await web.StartAsync();
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
                var answers = new StringBuilder();
                async Task<(int Status, string Text, HttpResponseMessage Response)> Send(HttpMethod method, string path, string? json = null)
                {
                    using var request = new HttpRequestMessage(method, path);
                    if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    var response = await client.SendAsync(request);
                    var text = await response.Content.ReadAsStringAsync();
                    answers.AppendLine(text);
                    return ((int)response.StatusCode, text, response);
                }
                Equal(200, (await Send(HttpMethod.Post, "/api/credentials/accounts", new JsonObject { ["provider"] = "anthropic", ["apiKey"] = storedKey, ["label"] = "Work key" }.ToJsonString())).Status,
                    "a stored key");

                var page = await Send(HttpMethod.Get, "/settings/about");
                Equal(200, page.Status, "GET /settings/about: " + Cut(page.Text));
                Check(page.Text.Contains("Not checked since the server started.", StringComparison.Ordinal), "idle until clicked");
                Check(Regex.IsMatch(page.Text, "Checks (openai|anthropic) (·|&#xB7;) (openai|anthropic)<"), "the idle pane names what a check tests");
                Check(page.Text.Contains("embedded · hash verified", StringComparison.Ordinal), "live: the built-in catalog is described");
                Check(page.Response.Headers.CacheControl?.NoStore == true, "the page is no-store");
                var listed = await Send(HttpMethod.Get, "/api/about/connectivity");
                Equal(200, listed.Status, "GET /api/about/connectivity");
                var before = JsonNode.Parse(listed.Text)!;
                Equal(2, before["targets"]!.AsArray().Count, "two targets");
                Check(before["last"] is null && before["offline"]!.GetValue<bool>() == false, "no check yet, and online");
                var diagnostics = await Send(HttpMethod.Get, "/api/about/diagnostics?download=1");
                Equal(200, diagnostics.Status, "GET /api/about/diagnostics");
                Equal("text/plain", diagnostics.Response.Content.Headers.ContentType?.MediaType, "plain text");
                Check(diagnostics.Response.Content.Headers.ContentDisposition?.DispositionType == "attachment" &&
                    Regex.IsMatch(diagnostics.Response.Content.Headers.ContentDisposition.FileName ?? "", "pisharp-webagent-diagnostics-\\d{4}-\\d{2}-\\d{2}\\.txt"), "a download");
                Check(diagnostics.Text.Contains("OPENAI_API_KEY", StringComparison.Ordinal) && diagnostics.Text.Contains("anthropic", StringComparison.Ordinal), "the accounts by kind");
                Check(!diagnostics.Text.Contains("Work key", StringComparison.Ordinal), "no account name");
                Equal(0, http.Requests.Count, "neither the page, the targets nor the report sent a request");
                Equal(405, (await Send(HttpMethod.Put, "/api/about/connectivity")).Status, "only POST runs a check");

                var posted = await Send(HttpMethod.Post, "/api/about/connectivity");
                Equal(200, posted.Status, "POST /api/about/connectivity: " + posted.Text);
                var answer = JsonNode.Parse(posted.Text)!;
                var results = answer["last"]!["results"]!.AsArray().ToDictionary(result => result!["provider"]!.GetValue<string>(), result => result!);
                Equal("ok", results["openai"]["outcome"]!.GetValue<string>(), "openai answered through the fake handler");
                Equal(2, results["openai"]["modelCount"]!.GetValue<int>(), "its models");
                Equal("environment", results["openai"]["kind"]!.GetValue<string>(), "the environment account");
                Check(results["openai"]["detail"]!.GetValue<string>().Contains("key from OPENAI_API_KEY", StringComparison.Ordinal), "says where the key came from");
                Equal("failed", results["anthropic"]["outcome"]!.GetValue<string>(), "anthropic refused the key");
                Equal("1 of 2 reachable", answer["last"]!["summary"]!.GetValue<string>(), "the summary");
                var html = answer["html"]!.GetValue<string>();
                Check(html.Contains("data-connectivity-result=\"openai\" data-outcome=\"ok\"", StringComparison.Ordinal) &&
                    html.Contains("data-connectivity-result=\"anthropic\" data-outcome=\"failed\"", StringComparison.Ordinal), "the results pane");
                Check(answer["report"]!.GetValue<string>().Contains("connectivity    checked", StringComparison.Ordinal), "the report with the check");
                var sent = http.Requests.Select(request => request.Key).Order().ToList();
                Equal("GET https://api.anthropic.com/v1/models,GET https://api.openai.com/v1/models", string.Join(",", sent), "two free models-list requests, nothing else");
                Check(http.Requests.All(request => request.Method == "GET" && request.Body is null), "no request has a body (no completion)");
                var after = JsonNode.Parse((await Send(HttpMethod.Get, "/api/about/connectivity")).Text)!;
                Check(after["last"] is JsonObject, "the last check is kept");
                Equal(2, http.Requests.Count, "reading it sent nothing");
                var reloaded = await Send(HttpMethod.Get, "/settings/about");
                Check(reloaded.Text.Contains("data-state=\"results\"", StringComparison.Ordinal), "the page shows the last check");

                NoSecret(answers.ToString(), "an About answer", envKey, storedKey, SecretText.Mask(envKey), SecretText.Mask(storedKey));
                NoSecret(string.Join("\n", logs), "the log", envKey, storedKey);
            }
            finally
            {
                await web.StopAsync();
                await web.DisposeAsync();
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("about.page-renders-what-the-faux-app-measured", async () =>
        {
            await using var app = await WebAgentApp.StartAsync("about-page");
            var page = await app.Get("/settings/about");
            Equal(200, page.Status, "GET /settings/about: " + Cut(page.Text));
            var html = page.Text;
            CheckShell("/settings/about", html);
            Check(Regex.IsMatch(html, "<title>About (·|&#xB7;) Settings · PiSharp Web Agent</title>"), "the title");
            Check(!html.Contains("Coming in a later milestone", StringComparison.Ordinal), "not the placeholder");
            Check(html.Contains("<a class=\"active\" aria-current=\"page\" href=\"/settings/about\" data-settings-nav=\"about\">", StringComparison.Ordinal), "About is the active item");
            var (version, commit) = AboutFacts.VersionOf(typeof(AboutFacts).Assembly);
            Check(html.Contains($"data-about-version>v{version}</span>", StringComparison.Ordinal), "the app's version");
            if (commit is not null) Check(html.Contains(commit, StringComparison.Ordinal), "the commit");
            Check(html.Contains(Html(app.Origin), StringComparison.Ordinal), "the address it listens on");
            Check(html.Contains("loopback only", StringComparison.Ordinal), "loopback only");
            Check(html.Contains("offline · faux models", StringComparison.Ordinal) && html.Contains("faux catalog", StringComparison.Ordinal), "offline is said");
            var faux = new FauxModelCatalog(FauxModelCatalog.CreateModels());
            var models = faux.Models(FauxModelCatalog.Provider.Id);
            foreach (var (metric, value) in new[] { ("providers", 1), ("chat", models.Count), ("reasoning", models.Count(m => m.Reasoning)),
                         ("image-input", models.Count(m => m.Input.Contains("image"))), ("hidden", 0) })
                Check(html.Contains($"data-metric=\"{metric}\">{value}</div>", StringComparison.Ordinal), $"the {metric} metric is the catalog's {value}");
            Check(html.Contains("Not checked since the server started.", StringComparison.Ordinal) && html.Contains(Html(ConnectivityCheck.OfflineMessage), StringComparison.Ordinal),
                "the idle pane, offline");
            Check(html.Contains("data-connectivity-run", StringComparison.Ordinal) && html.Contains("data-pane=\"running\" hidden", StringComparison.Ordinal), "a button, the running pane hidden");
            Check(html.Contains("<pre id=\"diagReport\" class=\"about-report\" tabindex=\"0\" data-diagnostics-report>" + DiagnosticsReport.Title, StringComparison.Ordinal), "the report");
            Check(html.Contains("data-copy=\"#diagReport\"", StringComparison.Ordinal) && html.Contains("href=\"/api/about/diagnostics?download=1\"", StringComparison.Ordinal), "copy and download");
            foreach (var link in new[] { AboutPage.DocsUrl, AboutPage.RepositoryUrl, AboutPage.LicenseUrl, AboutPage.NoticesUrl })
                Check(html.Contains($"href=\"{link}\"", StringComparison.Ordinal), "links to " + link);
            Check(html.Contains("<script type=\"module\" src=\"/js/about.js", StringComparison.Ordinal), "about.js");
            Check(page.Response.Headers.CacheControl?.NoStore == true, "no-store");
            Equal(200, (await app.Get("/js/about.js")).Status, "about.js is served");
        }),

        Case("about.faux-check-is-offline-and-nothing-leaks", async () =>
        {
            var envKey = Planted("sk-proj-");
            var root = Temp("about-faux");
            try
            {
                await using var app = await WebAgentApp.StartAsync(root, new AppStart
                {
                    DataDirectory = Path.Join(root, "data"),
                    Environment = new Dictionary<string, string> { ["OPENAI_API_KEY"] = envKey },
                });
                var storedKey = Planted("sk-ant-api03-");
                var added = await app.SendAsync(HttpMethod.Post, "/api/credentials/accounts",
                    new JsonObject { ["provider"] = "anthropic", ["apiKey"] = storedKey, ["label"] = "Planted label" }, origin: app.Origin);
                Equal(200, added.Status, "a stored key, offline: " + added.Text);
                var answers = new StringBuilder();
                var before = await app.Get("/api/about/connectivity");
                answers.AppendLine(before.Text);
                Check(before.Body!["offline"]!.GetValue<bool>() && before.Body["last"] is null, "offline, not checked");
                Equal(403, (await app.SendAsync(HttpMethod.Post, "/api/about/connectivity", origin: "http://evil.example")).Status, "a cross-origin click is refused");
                var posted = await app.SendAsync(HttpMethod.Post, "/api/about/connectivity", origin: app.Origin);
                answers.AppendLine(posted.Text);
                Equal(200, posted.Status, "POST: " + posted.Text);
                var last = posted.Body!["last"]!;
                Check(last["offline"]!.GetValue<bool>() && last["results"]!.AsArray().Count == 0, "nothing tested");
                Equal(ConnectivityCheck.OfflineMessage, last["message"]!.GetValue<string>(), "and it says so");
                Check(posted.Body["html"]!.GetValue<string>().Contains(Html(ConnectivityCheck.OfflineMessage), StringComparison.Ordinal), "in the results pane");
                var report = await app.Get("/api/about/diagnostics");
                answers.AppendLine(report.Text);
                Equal(200, report.Status, "the report");
                Check(report.Response.Content.Headers.ContentDisposition is null, "shown, not downloaded, without ?download=1");
                Check(report.Response.Headers.CacheControl?.NoStore == true, "no-store");
                Check(report.Text.Contains("offline (--faux)", StringComparison.Ordinal) && report.Text.Contains("anthropic", StringComparison.Ordinal), "the mode and the accounts");
                var page = await app.Get("/settings/about");
                answers.AppendLine(page.Text);
                Check(page.Text.Contains("data-state=\"results\"", StringComparison.Ordinal), "the page shows the offline check");
                NoSecret(answers.ToString(), "an About answer", envKey, storedKey, SecretText.Mask(storedKey), "Planted label");
                NoSecret(app.Log, "the log", envKey, storedKey);
            }
            finally { DeleteTemp(root); }
        }),
    ];
}
