using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using PiSharp.Durable;
using PiSharp.Durable.Hosting.Testing;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Controllers;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Faux;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Shell;

// Decision 0011 (run W2, WU4): the shell in process: the navigation matches the mockups and the routes, the faux catalog and the fallback
// registrations, the Provider -> Model partial over a catalog (hidden models, selection, no script needed, encoded text) and the recent
// chats partial, rendered with Web.Common's FragmentRenderer.
internal static partial class Program
{
    private sealed class TestCatalog(IReadOnlyList<CatalogProvider> providers, IReadOnlyDictionary<string, IReadOnlyList<CatalogModel>> models) : IModelCatalog
    {
        public IReadOnlyList<CatalogProvider> Providers => providers;

        public IReadOnlyList<CatalogModel> Models(string providerId) => models.GetValueOrDefault(providerId) ?? [];
    }

    private sealed class ListedChats(IReadOnlyList<RecentChat> chats) : IRecentChats
    {
        public int? Limit { get; private set; }

        public Task<IReadOnlyList<RecentChat>> ListAsync(int limit, CancellationToken cancellationToken = default)
        {
            Limit = limit;
            return Task.FromResult<IReadOnlyList<RecentChat>>([.. chats.Take(limit)]);
        }
    }

    private static TestCatalog SampleCatalog() => new(
        [
            new CatalogProvider("alpha", "Alpha AI", "openai", [ProviderAuthKind.ApiKey], BillingKind.Metered),
            new CatalogProvider("beta.v2", "Beta", null, [ProviderAuthKind.OAuth], BillingKind.Subscription),
            new CatalogProvider("gamma", "Gamma", null, [ProviderAuthKind.ApiKey], BillingKind.Unknown),
        ],
        new Dictionary<string, IReadOnlyList<CatalogModel>>
        {
            ["alpha"] =
            [
                new("alpha", "a1", "Alpha One") { Reasoning = true, ThinkingLevels = ["off", "minimal", "low", "medium", "high"], ContextWindow = 272_000, Input = ["text", "image"], Cost = new(2, 10) },
                new("alpha", "a2", "Alpha <b>Two</b>") { ContextWindow = 1_000_000, Cost = new(0.075, 0.3) },
                new("alpha", "a3", "Alpha Three") { ContextWindow = 8_000, Cost = new(1, 1) },
            ],
            ["beta.v2"] = [new("beta.v2", "b1", "Beta One") { ContextWindow = 64_000 }, new("beta.v2", "b2", "Beta Two") { ContextWindow = 128_000 }],
        });

    private static RenderHost CatalogHost(string name, IModelCatalog catalog, IRecentChats? chats = null) => RenderHost.Create(name, (services, root) =>
    {
        services.AddSingleton(catalog);
        services.AddSingleton<ISettingsStore>(new SettingsStore(new WebAgentDb(Path.Join(root, "webagent.db"), [CoreMigrations.AppInfo, SettingsStore.Migration])));
        services.AddSingleton(chats ?? new EmptyRecentChats());
    });

    private static string Encoded(string text) => HtmlEncoder.Default.Encode(text);

    internal static IEnumerable<(string, Func<Task>)> HostShellCases() =>
    [
        Case("host.shell-navigation-matches-the-mockups-and-routes", () =>
        {
            Equal("New chat|Playground|Compare|Tool Bench|History|Evals|Presets", string.Join("|", ShellNav.Primary.Select(item => item.Label)), "the sidebar");
            Equal("Settings", ShellNav.SettingsLink.Label, "the sidebar footer");
            Equal("Connections: Providers, Accounts & sign-ins, Models, Custom providers | Agent: Defaults, Tools & workspace, Spend | App: Appearance, Data & storage, About",
                string.Join(" | ", ShellNav.SettingsGroups.Select(group => group.Label + ": " + string.Join(", ", group.Items.Select(item => item.Label)))), "the settings nav");
            Equal("/chat/new", ShellNav.Primary[0].Href, "New chat");
            Equal(null, ShellNav.SettingsItem("nope"), "an unknown settings id");

            // W4 T4: every settings page is built, so the settings placeholder (its route and view) is gone. W5 E6: Evals is built, so the
            // sidebar placeholder lost its route; the W5 integration then removed its parts (ShellController, LaterFeature, Views/Shell/Later).
            var app = typeof(ShellNav).Assembly;
            Check(app.GetType("PiSharp.WebAgent.Controllers.ShellController") is null, "the shell keeps no placeholder controller");
            Check(app.GetType("PiSharp.WebAgent.Shell.LaterFeature") is null, "no later-milestone page list");
            Check(app.GetTypes().All(type => type.FullName?.Contains("Views_Shell_Later", StringComparison.Ordinal) != true), "no later-milestone view");
        }),

        Case("host.faux-catalog-and-fallback-registrations", () =>
        {
            var models = FauxModelCatalog.CreateModels();
            var catalog = new FauxModelCatalog(models);
            var provider = catalog.Providers.Single();
            Equal(("faux", BillingKind.Free, "None"), (provider.Id, provider.Billing, string.Join(",", provider.AuthKinds)), "one offline provider");
            Equal("echo,echo-small,echo-thinking", string.Join(",", catalog.Models("faux").Select(model => model.Id)), "its models");
            foreach (var model in catalog.Models("faux"))
            {
                Check(models.GetModel("faux", model.Id) is not null, model.Id + " resolves through IDurableModels");
                Check(model.ThinkingLevels.All(level => PiSharp.AI.ThinkingLevels.Ordered.Contains(level)), model.Id + " thinking levels");
                Check(model.ContextWindow > 0 && model.MaxTokens > 0, model.Id + " limits");
            }
            var thinking = ((IModelCatalog)catalog).FindModel("faux", "echo-thinking")!;
            Check(thinking.Reasoning, "echo-thinking reasons");
            Equal("off,minimal,low,medium,high", string.Join(",", thinking.ThinkingLevels), "its levels");
            Equal("off", string.Join(",", ((IModelCatalog)catalog).FindModel("faux", "echo")!.ThinkingLevels), "echo does not think");
            Equal(0, catalog.Models("openai").Count, "no other provider");
            Equal("200K context · reasoning · free", CatalogText.Summary(provider, thinking), "its summary");

            var options = new WebAgentOptions { DataDirectory = Path.Join(Path.GetTempPath(), "pisharp-webagent", "never-created") };
            using (var plain = new ServiceCollection().AddWebAgentFallbacks(options).BuildServiceProvider())
            {
                Check(plain.GetRequiredService<IModelCatalog>() is FauxModelCatalog, "the faux catalog until routing registers one");
                Check(ReferenceEquals(plain.GetRequiredService<IDurableModels>(), plain.GetRequiredService<FauxDurableModels>()), "the faux models until routing registers them");
                Check(plain.GetRequiredService<IRecentChats>() is EmptyRecentChats, "no chats until the chat area registers them");
            }
            var routed = new FauxDurableModels();
            var real = SampleCatalog();
            var areas = new ServiceCollection().AddSingleton<IDurableModels>(routed).AddSingleton<IModelCatalog>(real).AddSingleton<IRecentChats>(new ListedChats([]));
            using (var registered = areas.AddWebAgentFallbacks(options).BuildServiceProvider())
            {
                Check(ReferenceEquals(registered.GetRequiredService<IModelCatalog>(), real), "an area's catalog is kept");
                Check(ReferenceEquals(registered.GetRequiredService<IDurableModels>(), routed), "an area's models are kept");
                Check(registered.GetRequiredService<IRecentChats>() is ListedChats, "an area's chats are kept");
            }
            var fauxAreas = new ServiceCollection().AddSingleton<IDurableModels>(routed).AddSingleton<IModelCatalog>(real);
            using var faux = fauxAreas.AddWebAgentFallbacks(new WebAgentOptions { DataDirectory = options.DataDirectory, Faux = true }).BuildServiceProvider();
            Check(faux.GetRequiredService<IModelCatalog>() is FauxModelCatalog, "the faux switch replaces any catalog");
            Check(faux.GetRequiredService<IDurableModels>() is FauxDurableModels fauxModels && !ReferenceEquals(fauxModels, routed) && fauxModels.Models.Length == 3,
                "and any models: no provider code path");
            Equal(1, faux.GetServices<IDurableModels>().Count(), "only the faux models are registered");
        }),

        Case("host.catalog-text", () =>
        {
            Equal("272K|1M|1.5M|900|128K|2M", string.Join("|", new long[] { 272_000, 1_000_000, 1_500_000, 900, 128_000, 2_000_000 }.Select(CatalogText.Tokens)), "tokens");
            Equal("$2|$0.75|$0.075|$1.50|$0|$15", string.Join("|", new[] { 2, 0.75, 0.075, 1.5, 0, 15 }.Select(CatalogText.Dollars)), "dollars");
            Equal("beta-v2|a_b-c|---", string.Join("|", new[] { "beta.v2", "a_b-c", "/ <" }.Select(CatalogText.IdPart)), "id parts");
            var catalog = SampleCatalog();
            var alpha = catalog.Providers[0];
            Equal("272K context · reasoning · images · $2 / $10 per 1M tokens", CatalogText.Summary(alpha, catalog.Models("alpha")[0]), "metered");
            Equal("1M context · $0.075 / $0.30 per 1M tokens", CatalogText.Summary(alpha, catalog.Models("alpha")[1]), "metered, cents");
            Equal("128K context · included in your subscription", CatalogText.Summary(catalog.Providers[1], catalog.Models("beta.v2")[1]), "subscription");
            Equal("price unknown", CatalogText.Summary(alpha, new CatalogModel("alpha", "x", "X")), "no cost and no context");
        }),

        Case("host.provider-model-select-renders-linked-lists", async () =>
        {
            await using var host = CatalogHost("pms", SampleCatalog());
            await host.Services.GetRequiredService<ISettingsStore>().SaveAsync(new ModelsSettings(["alpha/a3"]));

            var html = await host.RenderAsync("_ProviderModelSelect", new ProviderModelSelect("dm") { Provider = "beta.v2", Model = "b2", Small = true });
            Check(html.Contains("<div class=\"row g-2\" data-provider-model=\"dm\">", StringComparison.Ordinal), "the root");
            Check(html.Contains("<select class=\"form-select form-select-sm\" id=\"dm-provider\" name=\"provider\" data-provider-select>", StringComparison.Ordinal), "the provider list");
            Check(html.Contains("<option value=\"alpha\">Alpha AI</option>", StringComparison.Ordinal), "a provider");
            Check(html.Contains("<option value=\"beta.v2\" selected=\"selected\">Beta</option>", StringComparison.Ordinal), "the selected provider");
            Check(!html.Contains("gamma", StringComparison.Ordinal), "a provider without models is left out");
            Check(html.Contains("<div data-pane=\"dm-alpha\" data-provider=\"alpha\" hidden=\"hidden\">", StringComparison.Ordinal), "another provider's pane is hidden");
            Check(html.Contains("<select class=\"form-select form-select-sm\" id=\"dm-model-alpha\" name=\"model\" data-model-select disabled=\"disabled\">", StringComparison.Ordinal),
                "and its list does not post");
            Check(html.Contains("<div data-pane=\"dm-beta-v2\" data-provider=\"beta.v2\">", StringComparison.Ordinal), "the selected provider's pane shows");
            Check(html.Contains("<select class=\"form-select form-select-sm\" id=\"dm-model-beta-v2\" name=\"model\" data-model-select>", StringComparison.Ordinal), "and posts");
            Check(Regex.IsMatch(html, "<option value=\"b2\" selected=\"selected\" data-summary=\"[^\"]*\" data-thinking=\"off\">Beta Two</option>"), "the selected model");
            Check(html.Contains($"<div class=\"form-text\" data-model-summary>{Encoded("128K context · included in your subscription")}</div>", StringComparison.Ordinal),
                "the selected model's summary");
            Check(html.Contains($"data-summary=\"{Encoded("272K context · reasoning · images · $2 / $10 per 1M tokens")}\" data-thinking=\"off minimal low medium high\">Alpha One",
                StringComparison.Ordinal), "a model's summary and thinking levels");
            Check(html.Contains("Alpha &lt;b&gt;Two&lt;/b&gt;", StringComparison.Ordinal) && !html.Contains("<b>Two", StringComparison.Ordinal), "names are text");
            Check(!html.Contains("value=\"a3\"", StringComparison.Ordinal), "a hidden model is left out");
            Equal(2, Regex.Matches(html, "name=\"model\"").Count, "one model list per provider");
            Check(!html.Contains("<script", StringComparison.Ordinal), "no script in the partial");

            var hiddenSelected = await host.RenderAsync("_ProviderModelSelect", new ProviderModelSelect("cm") { Provider = "alpha", Model = "a3" });
            Check(Regex.IsMatch(hiddenSelected, "<option value=\"a3\" selected=\"selected\""), "the selected model shows even when hidden");
            Check(hiddenSelected.Contains("<select class=\"form-select\" id=\"cm-provider\"", StringComparison.Ordinal), "normal size");
            var all = await host.RenderAsync("_ProviderModelSelect", new ProviderModelSelect("all") { IncludeHidden = true });
            Check(all.Contains("value=\"a3\"", StringComparison.Ordinal), "IncludeHidden lists hidden models");
            Check(Regex.IsMatch(all, "<option value=\"alpha\" selected=\"selected\">") && Regex.IsMatch(all, "<option value=\"a1\" selected=\"selected\""),
                "no selection picks the first provider and its first model");
            var unknown = await host.RenderAsync("_ProviderModelSelect", new ProviderModelSelect("u") { Provider = "nope", Model = "b2" });
            Check(Regex.IsMatch(unknown, "<option value=\"alpha\" selected=\"selected\">") && Regex.IsMatch(unknown, "<option value=\"a1\" selected=\"selected\""),
                "an unknown provider falls back to the first");
            var offered = await host.RenderAsync("_ProviderModelSelect", new ProviderModelSelect("o")
            {
                Providers = ["beta.v2"], ProviderField = "defaultProvider", ModelField = "defaultModel",
            });
            Check(!offered.Contains("value=\"alpha\"", StringComparison.Ordinal), "only the offered providers");
            Check(offered.Contains("name=\"defaultProvider\"", StringComparison.Ordinal) && offered.Contains("name=\"defaultModel\"", StringComparison.Ordinal), "field names");

            await using var empty = CatalogHost("pms-empty", new TestCatalog([], new Dictionary<string, IReadOnlyList<CatalogModel>>()));
            var none = await empty.RenderAsync("_ProviderModelSelect", new ProviderModelSelect("n"));
            Check(none.Contains("<option>No providers available</option>", StringComparison.Ordinal) && none.Contains("disabled", StringComparison.Ordinal), "an empty catalog");
            Check(!none.Contains("name=\"provider\"", StringComparison.Ordinal), "nothing posts");
        }),

        Case("host.recent-chats-render-as-text-with-running-dots", async () =>
        {
            var chats = new ListedChats(
            [
                new RecentChat("a/b", "Fix <script>alert(1)</script>", DateTimeOffset.UtcNow, Running: true),
                new RecentChat("c", "Plain", DateTimeOffset.UtcNow.AddHours(-1), Running: false),
            ]);
            await using var host = CatalogHost("recent", SampleCatalog(), chats);
            var html = await host.RenderAsync("_RecentChats", null);
            Equal(ShellNav.RecentChatLimit, chats.Limit, "the sidebar asks for its limit");
            Check(html.Contains("href=\"/chat/a%2Fb\" data-chat=\"a/b\" data-running>", StringComparison.Ordinal), "a running chat, its id escaped in the link");
            Check(html.Contains("<span class=\"status-dot live ms-auto\" aria-hidden=\"true\"></span><span class=\"visually-hidden\">, running</span>", StringComparison.Ordinal),
                "its live dot");
            Check(html.Contains("Fix &lt;script&gt;alert(1)&lt;/script&gt;", StringComparison.Ordinal) && !html.Contains("<script", StringComparison.Ordinal), "titles are text");
            Check(html.Contains("<a class=\"chat-link\" href=\"/chat/c\" data-chat=\"c\">Plain</a>", StringComparison.Ordinal), "an idle chat");
            Check(!html.Contains("No chats yet", StringComparison.Ordinal), "no empty note");

            await using var empty = CatalogHost("recent-empty", SampleCatalog());
            Check((await empty.RenderAsync("_RecentChats", null)).Contains("<p class=\"recent-empty\">No chats yet</p>", StringComparison.Ordinal), "the empty note");
        }),

        Case("host.shell-assets-are-in-the-project", () =>
        {
            var project = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(WebAgentApp.AppDll()))))!;
            var site = File.ReadAllText(Path.Join(project, "wwwroot", "css", "site.css"));
            foreach (var selector in new[] { ".app-sidebar", ".app-topbar", ".app-breadcrumb", ".chat-layout", ".settings-layout", ".settings-nav", ".settings-savebar",
                         ".setting-row.is-modified", ".model-table", ".lvl", ".provider-row", ".ps-stepper", ".device-code", ".tree-node", ".run-break", ".recent-empty",
                         ".confirm-icon.is-danger", ".toast-container" })
                Check(site.Contains(selector, StringComparison.Ordinal), "site.css has " + selector);
            foreach (var mockupOnly in new[] { ".mock-note", ".mockup-floater", ".state-switcher", "[data-state]" })
                Check(!site.Contains(mockupOnly, StringComparison.Ordinal), "site.css leaves out the mockup helper " + mockupOnly);
            var shell = File.ReadAllText(Path.Join(project, "wwwroot", "js", "shell.js"));
            foreach (var export in new[] { "export function showToast", "export function confirmAction", "export function providerModelValue", "export function initShell" })
                Check(shell.Contains(export, StringComparison.Ordinal), "shell.js: " + export);
            Check(!shell.Contains("innerHTML", StringComparison.Ordinal), "shell.js never writes HTML");
        }),
    ];
}
