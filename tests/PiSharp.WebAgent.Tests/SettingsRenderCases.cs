using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using PiSharp.Web.Common;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Settings;

// Decision 0011 (run W2, WU9): the Settings area in process. ProviderDirectory over a fake account store and sign-in list (catalog, built-in
// and account-only providers; each provider's state, key form and sign-in methods; Anthropic never signs in); the /api/settings input rules;
// the Models page's filters and paging; and the settings partials rendered with Web.Common's FragmentRenderer: account and sign-in cards
// (connected, expired, environment, a second key), the credential modals online and offline, the provider row and the model table, with
// names as text and no secret field holding a value.
internal static partial class Program
{
    /// <summary>A read-only account store over a fixed list (writes are not part of these cases).</summary>
    private sealed class ListedAccounts(IReadOnlyList<AccountInfo> accounts) : IAccountStore
    {
        public event EventHandler<AccountChangedEventArgs>? Changed { add { } remove { } }

        public IReadOnlyList<AccountInfo> List(string? provider = null) => [.. accounts.Where(account => provider is null || account.Provider == provider)];

        public AccountInfo? Get(string accountId) => accounts.FirstOrDefault(account => account.Id == accountId);

        public AccountInfo? Active(string provider) => accounts.FirstOrDefault(account => account.Provider == provider && account.IsActive);

        public Task<AccountAdded> AddApiKeyAsync(ApiKeyAccountRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AccountInfo> ReplaceApiKeyAsync(string accountId, string? apiKey, IReadOnlyDictionary<string, string>? settings = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AccountInfo> RenameAsync(string accountId, string label, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AccountInfo> SetActiveAsync(string accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AccountRemoved> RemoveAsync(string accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AccountInfo> MarkExpiredAsync(string accountId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Sign-in methods only; no flow runs.</summary>
    private sealed class ListedSignIns(IReadOnlyList<SignInMethodInfo> methods) : ISignInFlows
    {
        public IReadOnlyList<SignInMethodInfo> Methods => methods;

        public Task<SignInStatus> StartAsync(SignInRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public SignInStatus? Get(string flowId) => null;

        public Task<SignInStatus> SubmitCodeAsync(string flowId, string input, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public SignInStatus? Cancel(string flowId) => null;
    }

    private static AccountInfo SettingsAccount(string id, string provider, string label, AccountKind kind, AccountStatus status = AccountStatus.Connected,
        bool active = false, string hint = "") =>
        new() { Id = id, Provider = provider, Label = label, Kind = kind, Status = status, IsActive = active, Hint = hint, Created = DateTimeOffset.UtcNow, Updated = DateTimeOffset.UtcNow };

    private static IReadOnlyList<AccountInfo> SettingsAccounts() =>
    [
        SettingsAccount("acc-codex1", "openai-codex", "Laptop", AccountKind.OAuth, active: true) with
        {
            Identity = "acct-1234567890abcdef", Details = new Dictionary<string, string> { ["plan"] = "plus", ["email"] = "me@example.test" },
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        },
        SettingsAccount("acc-codex2", "openai-codex", "Old <i>laptop</i>", AccountKind.OAuth, AccountStatus.Expired) with { StatusReason = "The refresh token was revoked." },
        SettingsAccount("acc-copilot", "github-copilot", "GitHub", AccountKind.OAuth, AccountStatus.Expired, active: true),
        SettingsAccount("acc-key1", "openai", "Work", AccountKind.ApiKey, active: true, hint: "sk-…AbCd") with
        {
            Settings = new Dictionary<string, string> { ["baseUrl"] = "https://proxy.example.test/v1" },
        },
        SettingsAccount("acc-key2", "openai", "Second", AccountKind.ApiKey, hint: "sk-…EfGh"),
        SettingsAccount("env-openai-openai_api_key", "openai", "OPENAI_API_KEY", AccountKind.Environment, hint: "sk-…WxYz") with { Source = "OPENAI_API_KEY" },
        SettingsAccount("acc-custom", "custom-x", "Custom", AccountKind.ApiKey, active: true, hint: "ck-…1234"),
    ];

    private static readonly SignInMethodInfo[] SettingsMethods =
    [
        new(SignInFlows.CodexProvider, SignInMethod.Browser, "Sign in with ChatGPT", "Opens auth.openai.com.", true),
        new(SignInFlows.CodexProvider, SignInMethod.DeviceCode, "Use a device code", "For a remote machine.", true),
        new(SignInFlows.CopilotProvider, SignInMethod.DeviceCode, "Sign in with GitHub", "Enter a code on github.com.", true),
    ];

    private static TestCatalog SettingsCatalog() => new(
        [
            new CatalogProvider("alpha", "Alpha AI", "openai", [ProviderAuthKind.ApiKey], BillingKind.Metered),
            new CatalogProvider("openai", "OpenAI", "openai", [ProviderAuthKind.ApiKey], BillingKind.Metered),
            new CatalogProvider("offline", "Offline", null, [ProviderAuthKind.None], BillingKind.Free),
        ],
        new Dictionary<string, IReadOnlyList<CatalogModel>>
        {
            ["alpha"] = [new("alpha", "a1", "Alpha One") { ContextWindow = 8_000 }],
            ["openai"] =
            [
                new("openai", "gpt-x", "GPT <b>X</b>") { Reasoning = true, ThinkingLevels = ["off", "low", "high"], ContextWindow = 400_000, Input = ["text", "image"], Cost = new(1.25, 10) },
                new("openai", "gpt-mini", "GPT Mini") { ContextWindow = 1_000_000, Cost = new(0.25, 2) },
            ],
            ["offline"] = [.. Enumerable.Range(1, 60).Select(index => new CatalogModel("offline", $"m{index:00}", $"Model {index:00}") { ContextWindow = 32_000 })],
        });

    private static ProviderDirectory SettingsDirectory(bool offline = false) =>
        new(SettingsCatalog(), new ListedAccounts(SettingsAccounts()), new ListedSignIns(offline ? [.. SettingsMethods.Select(method => method with { Available = false })] : SettingsMethods),
            new CredentialsOptions { Offline = offline });

    private static ModelsQuery Query(string text) => ModelsQuery.From(new QueryCollection(QueryHelpers.ParseQuery(text)));

    internal static IEnumerable<(string, Func<Task>)> SettingsRenderCases() =>
    [
        Case("settings.provider-directory-merges-catalog-builtins-and-accounts", () =>
        {
            var directory = SettingsDirectory();
            var list = directory.List();
            Equal("alpha,openai,offline", string.Join(",", list.Take(3).Select(entry => entry.Id)), "the catalog's providers come first");
            Equal("custom-x", list[^1].Id, "a provider only an account knows comes last");
            Equal(list.Count, list.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count(), "each provider once");
            Check(list.Any(entry => entry.Id == "amazon-bedrock") && list.Any(entry => entry.Id == "anthropic"), "the built-in providers");

            ProviderEntry Get(string id) => directory.Find(id) ?? throw new InvalidOperationException("No provider " + id);
            var openai = Get("openai");
            Equal(ProviderState.Connected, openai.State, "openai: a stored key is active");
            Equal("acc-key1", openai.Active?.Id, "its active account is the one the store marks");
            Equal(3, openai.Accounts.Count, "every account of openai, the variable included");
            Equal("acc-key1,acc-key2", string.Join(",", openai.StoredAccounts.Select(account => account.Id)), "the stored ones");
            Equal((ApiKeyForm.Single, "apiKey", false), (openai.KeyForm, openai.AuthTags, openai.SignsIn), "a key provider");
            Equal(ProviderLogos.Url("openai"), openai.LogoUrl, "its mark");
            Equal(2, openai.Models.Count, "its catalog models");

            var codex = Get(SignInFlows.CodexProvider);
            Equal((ProviderState.Connected, ApiKeyForm.None, true, true), (codex.State, codex.KeyForm, codex.SignInOnly, codex.SignsIn), "Codex signs in only");
            Equal("oauth", codex.AuthTags, "its filter word");
            Equal("ChatGPT Plus/Pro", codex.SubscriptionName, "its plan name");
            var copilot = Get(SignInFlows.CopilotProvider);
            Equal(ProviderState.Expired, copilot.State, "an expired active sign-in");
            Equal("expired", copilot.StateName, "the filter word");

            var anthropic = Get("anthropic");
            Equal((ProviderState.NotConfigured, ApiKeyForm.Single, false), (anthropic.State, anthropic.KeyForm, anthropic.SignsIn), "Anthropic: an API key, no sign-in");
            Check(anthropic.Description.Contains("API key or through Amazon Bedrock", StringComparison.Ordinal), "says why");
            Equal(ApiKeyForm.Bedrock, Get("amazon-bedrock").KeyForm, "Bedrock's form");
            Equal("cloud", Get("amazon-bedrock").AuthTags, "a cloud provider");
            Equal(ApiKeyForm.Vertex, Get("google-vertex").KeyForm, "Vertex's form");
            Equal(ApiKeyForm.Azure, Get("azure").KeyForm, "Azure's form");
            Equal(ProviderLogos.KeyUrl("azure"), Get("azure").LogoUrl, "Azure's mark");
            Equal(ApiKeyForm.Cloudflare, Get("cloudflare-ai-gateway").KeyForm, "Cloudflare's form");

            var offline = Get("offline");
            Equal((true, ProviderState.Connected, ApiKeyForm.None, BillingKind.Free), (offline.NoCredentials, offline.State, offline.KeyForm, offline.Billing), "no credentials needed");
            Equal(ProviderState.NotConfigured, Get("alpha").State, "a catalog provider without an account");
            var custom = Get("custom-x");
            Equal((false, ProviderState.Connected, (string?)null), (custom.Builtin, custom.State, custom.LogoUrl), "an account-only provider");

            Equal(null, directory.Find("no-such-provider"), "an unknown provider");
            Equal(null, directory.Find(" "), "a blank id");
            Equal("custom-x,offline,openai,openai-codex", string.Join(",", directory.ConnectedIds().Order(StringComparer.Ordinal)), "the connected providers");
            Check(!directory.Offline && SettingsDirectory(offline: true).Offline, "offline follows the credentials options");
            Check(!SettingsDirectory(offline: true).Find("openai-codex")!.SignInAvailable, "offline, no sign-in is available");
        }),

        Case("settings.endpoint-rules-check-input", () =>
        {
            var catalog = SampleCatalog();
            Equal(ToolApprovalMode.AllowList, SettingsEndpoints.ParseMode("allow-list"), "allow-list");
            Equal(ToolApprovalMode.AllowList, SettingsEndpoints.ParseMode("allowList"), "allowList");
            Equal(ToolApprovalMode.Ask, SettingsEndpoints.ParseMode(" "), "blank asks");
            Equal(ToolApprovalMode.Auto, SettingsEndpoints.ParseMode("AUTO"), "any case");
            Throws<ArgumentException>(() => SettingsEndpoints.ParseMode("never"), "an unknown mode");

            Equal(new DefaultsSettings("alpha", "a1", "high"), SettingsEndpoints.CheckDefaults(new DefaultsBody("alpha", "a1", "high"), catalog), "a catalog model");
            Equal(new DefaultsSettings(), SettingsEndpoints.CheckDefaults(new DefaultsBody(null, null, null), catalog), "nothing chosen");
            Throws<ArgumentException>(() => SettingsEndpoints.CheckDefaults(new DefaultsBody("alpha", "zz", null), catalog), "a model the catalog does not list");
            Throws<ArgumentException>(() => SettingsEndpoints.CheckDefaults(new DefaultsBody(null, "a1", null), catalog), "a model without a provider");
            Throws<ArgumentException>(() => SettingsEndpoints.CheckDefaults(new DefaultsBody("alpha", "a1", "turbo"), catalog), "an unknown level");

            var folder = Temp("set-rules");
            try
            {
                var tools = SettingsEndpoints.CheckTools(new ToolsBody(" " + folder + " ", "allowList",
                    [new AllowRuleBody("bash", "  git status  "), new AllowRuleBody("write", " "), new AllowRuleBody("edit", null)]));
                Equal(folder, tools.WorkingDirectory, "the folder, trimmed");
                Equal("bash:git status|write:|edit:", string.Join("|", (tools.AllowList ?? []).Select(rule => rule.Tool + ":" + rule.Command)), "clean rules");
                Equal(ToolsSettings.DefaultWorkingDirectory, SettingsEndpoints.CheckTools(new ToolsBody(null, null, null)).EffectiveWorkingDirectory, "no folder: home");
                Throws<ArgumentException>(() => SettingsEndpoints.CheckTools(new ToolsBody("relative", null, null)), "a relative folder");
                Throws<ArgumentException>(() => SettingsEndpoints.CheckTools(new ToolsBody(Path.Join(folder, "missing"), null, null)), "a missing folder");
                Throws<ArgumentException>(() => SettingsEndpoints.CheckTools(new ToolsBody(null, null, [new AllowRuleBody("Bash!", null)])), "a malformed tool");
                Throws<ArgumentException>(() => SettingsEndpoints.CheckTools(new ToolsBody(null, null, [null])), "an empty entry");
                Throws<ArgumentException>(() => SettingsEndpoints.CheckTools(new ToolsBody(null, null, [new AllowRuleBody("write", "*.md")])), "a pattern on another tool");
                Throws<ArgumentException>(() => SettingsEndpoints.CheckTools(new ToolsBody(null, null,
                    [new AllowRuleBody("bash", new string('x', SettingsEndpoints.MaximumCommandLength + 1))])), "a long pattern");
                Throws<ArgumentException>(() => SettingsEndpoints.CheckTools(new ToolsBody(null, null,
                    [.. Enumerable.Range(0, SettingsEndpoints.MaximumAllowRules + 1).Select(index => new AllowRuleBody("bash", "c" + index))])), "too many entries");
            }
            finally
            {
                DeleteTemp(folder);
            }

            Equal("alpha/a1,alpha/a2,alpha/a3", string.Join(",", SettingsEndpoints.VisibilityKeys(new VisibilityBody(null, "alpha", true), catalog)), "a provider's models");
            Equal("beta.v2/b1", string.Join(",", SettingsEndpoints.VisibilityKeys(new VisibilityBody([" beta.v2/b1", "beta.v2/b1"], null, true), catalog)), "trimmed, once");
            Throws<ArgumentException>(() => SettingsEndpoints.VisibilityKeys(new VisibilityBody([], null, true), catalog), "nothing named");
            Throws<ArgumentException>(() => SettingsEndpoints.VisibilityKeys(new VisibilityBody(["alpha/zz"], null, true), catalog), "an unknown model");
            Throws<ArgumentException>(() => SettingsEndpoints.VisibilityKeys(new VisibilityBody(["/a1"], null, true), catalog), "no provider");
            Throws<ArgumentException>(() => SettingsEndpoints.VisibilityKeys(new VisibilityBody(null, "nope", true), catalog), "an unknown provider");
            Equal("bash,edit,write", string.Join(",", SettingsEndpoints.ApprovalTools), "the tools the allow-list offers");
        }),

        Case("settings.models-page-filters-and-pages", () =>
        {
            var providers = SettingsDirectory().List();
            var defaults = new DefaultsSettings("openai", "gpt-x", "low");
            var visibility = new ModelsSettings(["openai/gpt-mini", "offline/m02"]);
            var page = ModelsPage.Build(providers, defaults, visibility, Query(""), offline: false);
            Equal(62, page.Matches, "connected providers only by default: openai's 2 and offline's 60");
            Equal((2, 1, 50, 2), (page.Pages, page.Page, page.Shown, page.ConnectedProviders), "50 a page");
            Equal((63, 62, 60), (page.CatalogTotal, page.Usable, page.Enabled), "the counts");
            var gpt = page.Groups.SelectMany(group => group.Rows).Single(row => row.Key == "openai/gpt-x");
            Check(gpt.IsDefault && !gpt.Hidden, "the default model");
            Equal("low", page.DefaultThinking, "the default level");
            Equal(12, ModelsPage.Build(providers, defaults, visibility, Query("?page=2"), false).Shown, "the second page");
            Equal(2, ModelsPage.Build(providers, defaults, visibility, Query("?page=99"), false).Page, "a page past the end is the last");
            Equal(63, ModelsPage.Build(providers, defaults, visibility, Query("?f=1"), false).Matches, "a submitted form without Connected lists every provider");
            Equal(61, ModelsPage.Build(providers, defaults, visibility, Query("?f=1&enabled=1"), false).Matches, "enabled only");
            Equal("openai/gpt-x", string.Join(",", ModelsPage.Build(providers, defaults, visibility, Query("?reasoning=1"), false).Groups.SelectMany(group => group.Rows).Select(row => row.Key)),
                "reasoning");
            Equal(1, ModelsPage.Build(providers, defaults, visibility, Query("?vision=1"), false).Matches, "vision");
            Equal(1, ModelsPage.Build(providers, defaults, visibility, Query("?long=1"), false).Matches, "1M context");
            Equal(60, ModelsPage.Build(providers, defaults, visibility, Query("?free=1"), false).Matches, "free");
            Equal(2, ModelsPage.Build(providers, defaults, visibility, Query("?q=GPT"), false).Matches, "search");
            Equal(1, ModelsPage.Build(providers, defaults, visibility, Query("?q=offline/m07"), false).Matches, "search by key");
            Equal(60, ModelsPage.Build(providers, defaults, visibility, Query("?provider=offline"), false).Matches, "one provider");

            var query = Query("?f=1&q=a b&provider=openai&provider=offline&enabled=on&page=3");
            Check(query.Filtered && !query.ConnectedOnly && query.EnabledOnly && query.Page == 3, "read from the query string");
            Equal("?f=1&q=a%20b&provider=openai&provider=offline&enabled=1&page=2", query.Href(2), "paging links keep the filters");
            Equal("?f=1&connected=1", Query("").Href(1), "the defaults");
            Equal(1, Query("?page=-4").Page, "a bad page");
        }),

        Case("settings.render-cards-and-modals", async () =>
        {
            await using var host = RenderHost.Create("set-render", (services, root) => services.AddSingleton<IModelCatalog>(SettingsCatalog()));
            Task<string> Render(string view, object model) => host.Renderer.RenderAsync("/Views/Settings/" + view + ".cshtml", model);
            var directory = SettingsDirectory();
            var openai = directory.Find("openai")!;
            var codex = directory.Find(SignInFlows.CodexProvider)!;

            var modals = await Render("_CredentialModals", new CredentialModals(false, 1455, "127.0.0.1"));
            foreach (var part in new[] { "id=\"modal-api-key\"", "id=\"modal-chatgpt-signin\"", "id=\"modal-oauth-signin\"", "id=\"modal-rename-account\"",
                         "data-signin-modal=\"chatgpt\" data-callback-port=\"1455\"", "127.0.0.1:1455", "Use your ChatGPT Plus or Pro subscription for Codex models." })
                Check(modals.Contains(part, StringComparison.Ordinal), "the modals have " + part);
            Check(Regex.IsMatch(modals, "data-signin-start=\"browser\">"), "online, sign-in can start");
            Check(!Regex.IsMatch(modals, "data-signin-provider=\"anthropic\"|data-provider=\"anthropic\""), "no Anthropic sign-in");
            foreach (Match secret in Regex.Matches(modals, "<input[^>]*data-key-secret[^>]*>"))
                Check(secret.Value.Contains("type=\"password\"", StringComparison.Ordinal) && !secret.Value.Contains("value=", StringComparison.Ordinal), "a secret field is empty: " + secret.Value);
            Check(!Regex.IsMatch(modals, "\\son[a-z]+\\s*=") && !modals.Contains("<script", StringComparison.Ordinal), "no script in the modals");
            var offlineModals = await Render("_CredentialModals", new CredentialModals(true, 1455, "127.0.0.1"));
            Check(Regex.IsMatch(offlineModals, "data-signin-start=\"browser\" disabled=\"disabled\""), "offline, sign-in cannot start");
            Check(Regex.IsMatch(offlineModals, "data-key-test-button[^>]*disabled|disabled[^>]*data-key-test-button"), "offline, no connection test");

            var active = await Render("_AccountCard", new AccountCard(openai, openai.Accounts.Single(account => account.Id == "acc-key1"), false));
            Check(active.Contains("<span class=\"ps-mono\" data-account-hint>" + Html("sk-…AbCd") + "</span>", StringComparison.Ordinal), "the masked hint");
            Check(active.Contains("data-account-card=\"acc-key1\" data-account-status=\"connected\"", StringComparison.Ordinal), "the card");
            Check(active.Contains("<span class=\"status-dot ok\"></span>Active</span>", StringComparison.Ordinal), "the active chip");
            Check(!active.Contains("data-account-action=\"activate\"", StringComparison.Ordinal), "no Make active on the active account");
            Check(active.Contains("data-key-account=\"acc-key1\"", StringComparison.Ordinal) && active.Contains("&quot;baseUrl&quot;", StringComparison.Ordinal),
                "Replace key carries the account and its settings");
            Check(active.Contains("data-account-action=\"remove\" data-account-id=\"acc-key1\"", StringComparison.Ordinal), "Remove key");
            Check(Regex.IsMatch(active, "data-account-action=\"test\" data-account-id=\"acc-key1\" data-account-label=\"Work\"\\s*>"), "Test runs online");
            var other = await Render("_AccountCard", new AccountCard(openai, openai.Accounts.Single(account => account.Id == "acc-key2"), true));
            Check(other.Contains("data-account-action=\"activate\" data-account-id=\"acc-key2\"", StringComparison.Ordinal), "Make active on another account");
            Check(Regex.IsMatch(other, "data-account-action=\"test\"[^>]*disabled=\"disabled\""), "offline, no test");
            var environment = await Render("_AccountCard", new AccountCard(openai, openai.Accounts.Single(account => account.Kind == AccountKind.Environment), false));
            Check(!environment.Contains("data-account-action=\"remove\"", StringComparison.Ordinal) && !environment.Contains("data-account-action=\"rename\"", StringComparison.Ordinal),
                "a variable is read-only");
            Check(environment.Contains(Html("env · OPENAI_API_KEY"), StringComparison.Ordinal), "its chip names the variable");

            var expired = await Render("_AccountCard", new AccountCard(codex, codex.Accounts.Single(account => account.Id == "acc-codex2"), false));
            Check(expired.Contains("data-account-status=\"expired\"", StringComparison.Ordinal) && expired.Contains(">expired</span>", StringComparison.Ordinal), "an expired sign-in");
            Check(expired.Contains("Old &lt;i&gt;laptop&lt;/i&gt;", StringComparison.Ordinal) && !expired.Contains("<i>laptop", StringComparison.Ordinal), "labels are text");
            Check(Regex.IsMatch(expired, "data-bs-target=\"#modal-chatgpt-signin\" data-signin-provider=\"openai-codex\"[^>]*data-signin-account=\"acc-codex2\""), "Reconnect signs in again");
            Check(!expired.Contains("data-account-action=\"activate\"", StringComparison.Ordinal), "an expired account cannot be made active");

            var card = await Render("_OAuthCard", codex);
            Check(card.Contains("data-oauth-card=\"openai-codex\"", StringComparison.Ordinal) && card.Contains("ChatGPT Plus/Pro", StringComparison.Ordinal), "the Codex card");
            Check(card.Contains("<span class=\"status-dot ok\"></span>connected</span>", StringComparison.Ordinal), "connected");
            Check(card.Contains("Browser PKCE", StringComparison.Ordinal) && card.Contains("Device code", StringComparison.Ordinal), "both methods");
            var copilotCard = await Render("_OAuthCard", directory.Find(SignInFlows.CopilotProvider)!);
            Check(copilotCard.Contains("sign-in expired", StringComparison.Ordinal), "an expired sign-in");

            var anthropicRow = await Render("_ProviderRow", directory.Find("anthropic")!);
            Check(anthropicRow.Contains("data-key-provider=\"anthropic\"", StringComparison.Ordinal) && !anthropicRow.Contains("data-signin-provider", StringComparison.Ordinal),
                "Anthropic connects with a key, never a sign-in");
            var openaiRow = await Render("_ProviderRow", openai);
            Check(Regex.IsMatch(openaiRow, "data-provider-row=\"openai\" data-state=\"connected\" data-auth=\"apiKey\""), "the row's filter data");
            Check(openaiRow.Contains(Html("sk-…AbCd"), StringComparison.Ordinal), "the active key's hint");

            var models = await Render("_ModelCatalogTable", ModelsPage.Build(directory.List(), new DefaultsSettings("openai", "gpt-x"), new ModelsSettings(["openai/gpt-mini"]),
                Query("?provider=openai"), false));
            Check(models.Contains("GPT &lt;b&gt;X&lt;/b&gt;", StringComparison.Ordinal) && !models.Contains("<b>X", StringComparison.Ordinal), "model names are text");
            Check(!Toggle(models, "openai/gpt-mini").Contains("checked", StringComparison.Ordinal) && Toggle(models, "openai/gpt-x").Contains("checked", StringComparison.Ordinal),
                "the switches follow the saved visibility");
            var details = Regex.Match(models, "data-model=\"([^\"]+)\"");
            Check(details.Success, "the details button carries the model");
            var json = JsonNode.Parse(System.Net.WebUtility.HtmlDecode(details.Groups[1].Value))!;
            Equal("openai/gpt-x", json["key"]!.GetValue<string>(), "its key");
            Equal("stored key · sk-…AbCd", json["credential"]!.GetValue<string>(), "the credential as its chip says it, masked");
            Equal("metered", json["billing"]!.GetValue<string>(), "billing");
            Check(json["isDefault"]!.GetValue<bool>() && !json["hidden"]!.GetValue<bool>(), "default and shown");
        }),
    ];
}
