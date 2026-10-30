using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using PiSharp.AI;
using PiSharp.Durable.Hosting.Testing;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Controllers;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Faux;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Routing;
using PiSharp.WebAgent.Settings;
using PiSharp.WebAgent.Setup;
using BuiltinProviders = PiSharp.AI.Catalogs.BuiltinProviders;
using ProviderEnvironmentKeys = PiSharp.AI.Catalogs.ProviderEnvironmentKeys;

// Decision 0011 (run W3, SU): the setup wizard (GET /setup) and its first-run redirect. In process: the setup settings section (a missing
// section is "never ended", times are UTC, it round-trips through ISettingsStore), which requests the redirect applies to (GET or HEAD of
// /, /chat and /chat/new only), SetupState's decisions (redirect only while nothing is connected and the wizard never ended, never with
// --faux; finishing saves the default model through the Defaults section with the thinking level clamped as Pi clamps it, and refuses an
// unconnected provider or a model the catalog does not list), the page model (the step falls back to one that is reachable; featured
// providers first; variables and connections), and the wizard's partials rendered with Web.Common's FragmentRenderer for each step and
// state (default and nothing connected, online and offline, with and without catalog models). End to end (the built app as a child
// process, a temporary data folder and no provider variable of this machine): a first run sends the front doors to /setup until the
// wizard is skipped; a stored key ends the redirect and Save and continue stores the default model (no planted key in any answer, page,
// log or file); with --faux the wizard is reachable but never forced.
internal static partial class Program
{
    private const string SetupOrigin = "http://127.0.0.1:5180";

    private static TestCatalog SetupCatalog() => new(
        [
            new CatalogProvider("anthropic", "Anthropic", "anthropic", [ProviderAuthKind.ApiKey], BillingKind.Metered),
            new CatalogProvider("openai", "OpenAI", "openai", [ProviderAuthKind.ApiKey], BillingKind.Metered),
            new CatalogProvider(SignInFlows.CodexProvider, "OpenAI Codex", "openai", [ProviderAuthKind.OAuth], BillingKind.Subscription),
            new CatalogProvider("silent", "Silent", null, [ProviderAuthKind.ApiKey], BillingKind.Metered),
        ],
        new Dictionary<string, IReadOnlyList<CatalogModel>>
        {
            ["anthropic"] = [new("anthropic", "claude-a", "Claude A") { Reasoning = true, ThinkingLevels = ["minimal", "low", "medium", "high", "xhigh", "max"] }],
            ["openai"] =
            [
                new("openai", "gpt-x", "GPT <b>X</b>") { Reasoning = true, ThinkingLevels = ["off", "low", "high"], ContextWindow = 400_000 },
                new("openai", "gpt-plain", "GPT Plain") { ContextWindow = 128_000 },
                new("openai", "gpt-hidden", "GPT Hidden") { ContextWindow = 128_000 },
            ],
            [SignInFlows.CodexProvider] = [new(SignInFlows.CodexProvider, "codex-1", "Codex One") { Reasoning = true, ThinkingLevels = ["low", "medium", "high"] }],
        });

    private static AccountInfo SetupKey(string provider = "openai", string hint = "sk-…AbCd") =>
        SettingsAccount("acc-" + provider, provider, "Work", AccountKind.ApiKey, active: true, hint: hint);

    private static AccountInfo SetupCodex(DateTimeOffset now) =>
        SettingsAccount("acc-codex", SignInFlows.CodexProvider, "Laptop", AccountKind.OAuth, active: true) with
        {
            Details = new Dictionary<string, string> { ["plan"] = "plus" }, ExpiresAt = now.AddDays(9),
        };

    private static ProviderDirectory SetupDirectory(IReadOnlyList<AccountInfo> accounts, bool offline = false) =>
        new(SetupCatalog(), new ListedAccounts(accounts),
            new ListedSignIns(offline ? [.. SettingsMethods.Select(method => method with { Available = false })] : SettingsMethods),
            new CredentialsOptions { Offline = offline });

    private static SettingsStore SetupStore(string root) =>
        new(new WebAgentDb(Path.Join(root, "webagent.db"), [CoreMigrations.AppInfo, SettingsStore.Migration]));

    private static SetupState SetupStateOf(ProviderDirectory directory, ISettingsStore store, bool faux = false) =>
        new(directory, store, SetupCatalog(), new WebAgentOptions { DataDirectory = Path.GetTempPath(), Faux = faux });

    private static CredentialModals SetupModals(bool offline = false) => new(offline, 1455, SettingsController.DefaultCallbackHost);

    private static RenderHost SetupHost(string name) => RenderHost.Create(name, (services, root) =>
    {
        services.AddSingleton<IModelCatalog>(SetupCatalog());
        services.AddSingleton<ISettingsStore>(SetupStore(root));
    });

    private static Task<string> RenderSetupAsync(RenderHost host, ProviderDirectory directory, string? step, bool offline = false, DateTimeOffset? now = null)
    {
        var page = SetupPage.Build(directory, host.Services.GetRequiredService<ISettingsStore>(), host.Services.GetRequiredService<IModelCatalog>(),
            SetupModals(offline), SetupOrigin, @"C:\data\PiSharpWebAgent", step, now);
        return host.Renderer.RenderAsync("/Views/Setup/_SetupWizard.cshtml", page);
    }

    /// <summary>The start tag holding <paramref name="marker"/>.</summary>
    private static string TagWith(string html, string marker)
    {
        var at = html.IndexOf(marker, StringComparison.Ordinal);
        Check(at >= 0, "the markup holds " + marker);
        var start = html.LastIndexOf('<', at);
        return html[start..(html.IndexOf('>', at) + 1)];
    }

    private static bool TagHidden(string tag) => Regex.IsMatch(tag, @"\shidden(=""hidden"")?(?=[\s>/])");

    private static bool StepShown(string html, string step) => !TagHidden(TagWith(html, $"<section data-setup-step=\"{step}\""));

    /// <summary>The text of the first element carrying <paramref name="attribute"/>.</summary>
    private static string TextOf(string html, string attribute)
    {
        var match = Regex.Match(html, Regex.Escape(attribute) + "[^>]*>([^<]*)<");
        Check(match.Success, "the markup holds " + attribute);
        return match.Groups[1].Value;
    }

    /// <summary>Every provider key variable and ambient credential setting blanked (an empty value is absent), so no credential of this
    /// machine connects a provider in the app under test.</summary>
    private static Dictionary<string, string> NoProviderEnvironment()
    {
        var blank = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in BuiltinProviders.All)
            foreach (var name in provider.ApiKeyVariables) blank[name] = "";
        foreach (var name in new[]
        {
            ProviderEnvironmentKeys.AnthropicOAuthToken, "AWS_PROFILE", "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_BEARER_TOKEN_BEDROCK",
            "AWS_CONTAINER_CREDENTIALS_RELATIVE_URI", "AWS_CONTAINER_CREDENTIALS_FULL_URI", "AWS_WEB_IDENTITY_TOKEN_FILE", "GOOGLE_APPLICATION_CREDENTIALS",
            "GOOGLE_CLOUD_PROJECT", "GCLOUD_PROJECT", "GOOGLE_CLOUD_LOCATION",
        })
            blank[name] = "";
        return blank;
    }

    private static HttpClient NoRedirects(WebAgentApp app) =>
        new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = app.Base, Timeout = TimeSpan.FromSeconds(60) };

    internal static IEnumerable<(string, Func<Task>)> SetupCases() =>
    [
        Case("setup.settings.a-missing-section-reads-as-never-ended", () =>
        {
            Equal("setup", SetupSettings.SectionName, "the section key");
            var fresh = SetupSettings.CreateDefault();
            Check(!fresh.Done && !fresh.Finished && fresh.At is null, "nothing ended yet");
            Equal(new SetupSettings(), new SetupSettings(null, DateTimeOffset.UtcNow).Normalize(), "a time without an outcome is dropped");
            var local = new DateTimeOffset(2026, 10, 29, 9, 30, 0, TimeSpan.FromHours(-5));
            var skipped = SetupSettings.Ended(SetupOutcome.Skipped, local);
            Check(skipped.Done && !skipped.Finished, "skipping ends the wizard without finishing it");
            Equal(TimeSpan.Zero, skipped.At!.Value.Offset, "times are UTC");
            Equal(local, skipped.At!.Value, "the same instant");
            Check(SetupSettings.Ended(SetupOutcome.Finished, local).Finished, "finished");
            Equal(TimeSpan.Zero, new SetupSettings(SetupOutcome.Finished, local).Normalize().At!.Value.Offset, "a stored time is read as UTC");
            Throws<ArgumentException>(() => new SetupSettings((SetupOutcome)7).Normalize(), "an unknown outcome");
        }),

        Case("setup.settings.round-trip-through-the-settings-store", async () =>
        {
            var root = Temp("setup-store");
            try
            {
                var store = SetupStore(root);
                Equal(new SetupSettings(), store.Get<SetupSettings>(), "nothing stored");
                var at = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
                await store.SaveAsync(SetupSettings.Ended(SetupOutcome.Skipped, at));
                Equal(new SetupSettings(SetupOutcome.Skipped, at), SetupStore(root).Get<SetupSettings>(), "the outcome persists");
                var raw = ReadDb(Path.Join(root, "webagent.db"), "SELECT value FROM settings WHERE section = 'setup'", reader => reader.GetString(0)).Single();
                Check(raw.Contains("\"outcome\":\"skipped\"", StringComparison.Ordinal), "stored as a camelCase string: " + raw);
                await store.ResetAsync<SetupSettings>();
                Check(!SetupStore(root).Get<SetupSettings>().Done, "a reset starts the first run again");
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("setup.redirect.applies-to-the-front-doors-only", () =>
        {
            static HttpRequest Request(string method, string path)
            {
                var context = new DefaultHttpContext();
                context.Request.Method = method;
                context.Request.Path = path;
                return context.Request;
            }

            foreach (var path in new[] { "/", "/chat", "/chat/", "/chat/new", "/chat/new/", "/Chat/New" })
                Check(SetupRedirect.Applies(Request("GET", path)), path + " is a front door");
            Check(SetupRedirect.Applies(Request("HEAD", "/chat/new")), "HEAD too");
            Check(!SetupRedirect.Applies(Request("POST", "/chat/new")), "never a write");
            foreach (var path in new[] { "/setup", "/settings", "/settings/providers", "/chat/abc123", "/chat/new/x", "/playground", "/api/health", "/api/setup", "/css/site.css" })
                Check(!SetupRedirect.Applies(Request("GET", path)), path + " passes");
            Equal("/setup", SetupModule.SetupPath, "the wizard's address");
        }),

        Case("setup.state.redirects-only-while-nothing-is-connected-and-the-wizard-never-ended", async () =>
        {
            var root = Temp("setup-state");
            try
            {
                var store = SetupStore(root);
                var empty = SetupStateOf(SetupDirectory([]), store);
                Check(empty.RedirectsToSetup(), "nothing connected and never ended");
                Check(!empty.HasConnection() && empty.Connected().Count == 0, "nothing is connected");
                Check(SetupEndpoints.Status(empty).RedirectsToSetup, "the status says so");
                Check(!SetupStateOf(SetupDirectory([], offline: true), store, faux: true).RedirectsToSetup(), "never with --faux");
                var connected = SetupStateOf(SetupDirectory([SetupKey()]), store);
                Check(!connected.RedirectsToSetup(), "a stored key ends it");
                Equal("openai", connected.Connected().Single().Id, "the connected provider");
                var environment = SettingsAccount("env-openai-openai_api_key", "openai", "OPENAI_API_KEY", AccountKind.Environment, active: true, hint: "sk-…WxYz") with
                {
                    Source = "OPENAI_API_KEY",
                };
                Check(!SetupStateOf(SetupDirectory([environment]), store).RedirectsToSetup(), "an environment variable ends it");
                Check(SetupStateOf(SetupDirectory([SetupKey() with { Status = AccountStatus.Expired }]), store).RedirectsToSetup(), "an expired account is no connection");

                var skipped = await empty.SkipAsync();
                Equal(SetupOutcome.Skipped, skipped.Outcome, "skipped");
                Check(!empty.RedirectsToSetup(), "skipping ends it, connected or not");
                var status = SetupEndpoints.Status(empty);
                Check(status.Outcome == SetupOutcome.Skipped && !status.RedirectsToSetup && status.Connected.Count == 0, "the status after a skip");
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("setup.state.finish-saves-the-default-model-through-the-settings-store", async () =>
        {
            var root = Temp("setup-finish");
            try
            {
                var store = SetupStore(root);
                var state = SetupStateOf(SetupDirectory([SetupKey()]), store);
                var finished = await state.FinishAsync(new SetupChoice("openai", "gpt-x", "medium", UseAsDefault: true));
                Check(finished.SavedDefaults, "the defaults were written");
                Equal(new DefaultsSettings("openai", "gpt-x", "high"), finished.Defaults, "medium is clamped up to high, as Pi clamps it");
                Equal(new DefaultsSettings("openai", "gpt-x", "high"), SetupStore(root).Get<DefaultsSettings>(), "saved as the Defaults section");
                Check(SetupStore(root).Get<SetupSettings>().Finished, "the wizard is finished");
                Equal(finished.Setup, SetupStore(root).Get<SetupSettings>(), "the answer is what was stored");

                var plain = await state.FinishAsync(new SetupChoice(" openai ", " gpt-plain ", null, UseAsDefault: true));
                Equal(new DefaultsSettings("openai", "gpt-plain", null), plain.Defaults, "no level: the model's default");
                var kept = await state.FinishAsync(new SetupChoice("openai", "gpt-x", "high", UseAsDefault: false));
                Check(!kept.SavedDefaults, "not used as the default");
                Equal(new DefaultsSettings("openai", "gpt-plain", null), store.Get<DefaultsSettings>(), "the defaults are kept");
                var none = await state.FinishAsync(new SetupChoice(null, null, null, UseAsDefault: true));
                Check(!none.SavedDefaults && none.Setup.Finished, "no model chosen: finished, defaults kept");
                Equal(new DefaultsSettings("openai", "gpt-plain", null), store.Get<DefaultsSettings>(), "still kept");
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("setup.state.finish-refuses-without-a-connection-or-for-another-provider", async () =>
        {
            var root = Temp("setup-refuse");
            try
            {
                var store = SetupStore(root);
                await ThrowsAsync<InvalidOperationException>(() => SetupStateOf(SetupDirectory([]), store).FinishAsync(new SetupChoice(null, null, null, false)),
                    "nothing connected");
                Equal(new SetupSettings(), store.Get<SetupSettings>(), "nothing recorded");
                var state = SetupStateOf(SetupDirectory([SetupKey()]), store);
                var other = await ThrowsAsync<ArgumentException>(() => state.FinishAsync(new SetupChoice("anthropic", "claude-a", "high", true)), "an unconnected provider");
                Check(other.Message.Contains("Anthropic is not connected", StringComparison.Ordinal), "the reason names the provider: " + other.Message);
                await ThrowsAsync<ArgumentException>(() => state.FinishAsync(new SetupChoice("openai", "gpt-nope", null, true)), "a model the catalog does not list");
                await ThrowsAsync<ArgumentException>(() => state.FinishAsync(new SetupChoice("openai", null, null, true)), "a provider without a model");
                await ThrowsAsync<ArgumentException>(() => state.FinishAsync(new SetupChoice("openai", "gpt-x", "turbo", true)), "an unknown thinking level");
                Equal(new DefaultsSettings(), store.Get<DefaultsSettings>(), "no default was written");
                Equal(new SetupSettings(), store.Get<SetupSettings>(), "and the wizard did not end");
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("setup.page.starts-on-a-reachable-step", async () =>
        {
            var root = Temp("setup-steps");
            try
            {
                var store = SetupStore(root);
                SetupStep StepOf(ProviderDirectory directory, string? step) => SetupPage.Build(directory, store, SetupCatalog(), SetupModals(), SetupOrigin, root, step).Step;
                var none = SetupDirectory([]);
                var some = SetupDirectory([SetupKey()]);
                Equal(SetupStep.Welcome, StepOf(none, null), "no step: Welcome");
                Equal(SetupStep.Welcome, StepOf(none, "bogus"), "an unknown step: Welcome");
                Equal(SetupStep.Connect, StepOf(none, " CONNECT "), "names are case-insensitive");
                Equal(SetupStep.Connect, StepOf(none, "model"), "Default model needs a connected provider");
                Equal(SetupStep.Model, StepOf(some, "model"), "connected: Default model");
                Equal(SetupStep.Model, StepOf(some, "done"), "Done needs a finished wizard");
                Equal(SetupStep.Connect, StepOf(none, "done"), "and a connection");
                await store.SaveAsync(SetupSettings.Ended(SetupOutcome.Skipped, DateTimeOffset.UtcNow));
                Equal(SetupStep.Model, StepOf(some, "done"), "skipping is not finishing");
                await store.SaveAsync(SetupSettings.Ended(SetupOutcome.Finished, DateTimeOffset.UtcNow));
                Equal(SetupStep.Done, StepOf(some, "done"), "finished: Done");
                Equal("model", SetupPage.StepName(SetupStep.Model), "step names");
                Equal<SetupStep?>(null, SetupPage.ParseStep("summary"), "an unknown name");
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("setup.page.lists-key-providers-variables-and-connections", async () =>
        {
            var root = Temp("setup-lists");
            try
            {
                var store = SetupStore(root);
                var now = DateTimeOffset.UtcNow;
                var environment = SettingsAccount("env-anthropic-anthropic_api_key", "anthropic", "ANTHROPIC_API_KEY", AccountKind.Environment, active: true,
                    hint: "sk-…WxYz") with { Source = "ANTHROPIC_API_KEY" };
                var page = SetupPage.Build(SetupDirectory([SetupKey(), SetupCodex(now), environment]), store, SetupCatalog(), SetupModals(), SetupOrigin, root, "connect", now);

                var keys = page.KeyProviders.Select(entry => entry.Id).ToList();
                Equal("anthropic,openai", string.Join(",", keys.Take(2)), "the featured providers lead, in the catalog's order");
                Check(!keys.Contains(SignInFlows.CodexProvider) && !keys.Contains(SignInFlows.CopilotProvider), "sign-ins are not API key providers");
                Check(!keys.Contains(FauxDurableModels.ProviderId), "a provider without credentials takes no key");
                var single = page.KeyProviders.Select(entry => entry.KeyForm == ApiKeyForm.Single).ToList();
                Check(single.SkipWhile(one => one).All(one => !one), "one-key providers, then cloud accounts");
                Check(page.KeyProviders.Any(entry => entry.KeyForm == ApiKeyForm.Bedrock), "cloud accounts are offered");

                var set = page.Variables.Single(variable => variable.Name == "ANTHROPIC_API_KEY");
                Check(set.Set, "the variable that connects Anthropic is set");
                Equal("ANTHROPIC_API_KEY", page.Variables[0].Name, "set variables come first");
                Check(page.Variables.Skip(1).All(variable => !variable.Set), "the others are not set");
                Check(page.Variables.All(variable => variable.Name != CredentialVault.ExcludedVariable), "the excluded OAuth token variable is never offered");
                Equal(SetupPage.VariableChips, page.Variables.Count, "chips up to the limit");
                Check(page.MoreVariables > 0, "the rest are counted");
                Equal(page.Variables.Count + page.MoreVariables, page.Variables.Select(variable => variable.Name).Distinct().Count() + page.MoreVariables, "each variable once");

                Equal("openai-codex,anthropic,openai", string.Join(",", page.ConnectedIds), "connections in the featured order");
                Check(page.CodexConnected && !page.NothingConnected, "connected");
                var codex = page.Connected[0];
                Equal("OAuth", codex.Lead, "a sign-in");
                Equal("1 model · expires in 9 days, refreshes automatically", codex.Rest, "its models and expiry");
                var key = page.Connected[2];
                Check(key.Lead == "sk-…AbCd" && key.LeadMono, "the masked hint, in mono");
                Equal("3 models", key.Rest, "no test yet, so no latency");
                Equal("stored key", key.Chip, "the chip");
                var variable = page.Connected[1];
                Check(variable.Lead == "ANTHROPIC_API_KEY" && variable.LeadMono, "the variable's name");
                Equal(5, page.ModelsAvailable, "every connected provider's models");
                await store.SaveAsync(new ModelsSettings(["openai/gpt-hidden"]));
                Equal(4, SetupPage.Build(SetupDirectory([SetupKey(), SetupCodex(now), environment]), store, SetupCatalog(), SetupModals(), SetupOrigin, root, null, now).ModelsAvailable,
                    "less the hidden ones");
                Equal("anthropic", page.Choice!.Provider.Id, "the first connected catalog provider");
                Equal("medium", page.Choice!.Thinking, "medium when nothing is saved");
                Equal("127.0.0.1:5180", page.Host, "the host the browser reached");
                Equal(BuiltinProviders.All.Length, page.BuiltinProviderCount, "the built-in provider count");

                Equal("0 connected", SetupText.ConnectedCount(0), "none");
                Equal("1 provider connected", SetupText.ConnectedCount(1), "one");
                Equal("2 providers connected", SetupText.ConnectedCount(2), "two");
                Equal("GPT <b>X</b> supports off to high; minimal, medium aren't available. The level is clamped when you switch to a model with fewer levels.",
                    SetupText.ThinkingNote(SetupCatalog().Models("openai")[0]), "a model with gaps");
                Equal("GPT Plain doesn't reason, so only off is available.", SetupText.ThinkingNote(SetupCatalog().Models("openai")[1]), "a model without reasoning");
                Equal("Claude A supports minimal to max; off isn't available. The level is clamped when you switch to a model with fewer levels.",
                    SetupText.ThinkingNote(SetupCatalog().Models("anthropic")[0]), "a model without off");
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("setup.render.welcome-step", async () =>
        {
            await using var host = SetupHost("setup-welcome");
            var html = await RenderSetupAsync(host, SetupDirectory([SetupKey()]), null);
            Check(html.Contains("data-setup-state=\"default\"", StringComparison.Ordinal), "something connected: the default state");
            Check(html.Contains("data-setup-current=\"welcome\"", StringComparison.Ordinal), "the current step");
            Check(StepShown(html, "welcome") && !StepShown(html, "connect") && !StepShown(html, "model") && !StepShown(html, "done"), "only Welcome shows");
            Check(TagWith(html, "data-setup-indicator=\"welcome\"").Contains("class=\"active\"", StringComparison.Ordinal), "the stepper marks Welcome");
            Check(html.Contains($"its {BuiltinProviders.All.Length} built-in providers", StringComparison.Ordinal), "the built-in provider count");
            Check(html.Contains("127.0.0.1:5180", StringComparison.Ordinal) && html.Contains(@"C:\data\PiSharpWebAgent", StringComparison.Ordinal), "the address and data folder");
            Check(!TagHidden(TagWith(html, "data-setup-skip")), "Skip setup");
            Check(!TagHidden(TagWith(html, "data-setup-go=\"connect\" data-setup-only=\"welcome\"")), "Get started");
            Check(TagHidden(TagWith(html, "data-setup-finish data-setup-only")), "Save and continue is for the Default model step");
        }),

        Case("setup.render.connect-step-with-nothing-connected", async () =>
        {
            await using var host = SetupHost("setup-nothing");
            var html = await RenderSetupAsync(host, SetupDirectory([]), "connect");
            Check(html.Contains("data-setup-state=\"nothing\"", StringComparison.Ordinal), "the nothing-connected state");
            Check(StepShown(html, "connect") && !StepShown(html, "welcome"), "the Connect step shows");
            Check(html.Contains("data-setup-nothing", StringComparison.Ordinal) && !html.Contains("data-setup-connected-list", StringComparison.Ordinal), "Nothing connected yet");
            Check(html.Contains("data-setup-continue-blocked", StringComparison.Ordinal) && html.Contains("Connect at least one provider first", StringComparison.Ordinal),
                "Continue is blocked, with the reason");
            Check(!Regex.IsMatch(html, "<button[^>]*data-setup-go=\"model\">Continue"), "nothing leads to the Default model step");
            Equal("0 connected", TextOf(html, "data-setup-count"), "the count");
            var signIn = TagWith(html, $"data-signin-provider=\"{SignInFlows.CodexProvider}\"");
            Check(!signIn.Contains("disabled", StringComparison.Ordinal) && signIn.Contains("data-bs-target=\"#modal-chatgpt-signin\"", StringComparison.Ordinal),
                "online, Sign in with ChatGPT opens the Settings area's modal");
            Check(!html.Contains("data-setup-codex-connected", StringComparison.Ordinal), "not signed in");
            Check(html.Contains("Adds 1 Codex model, including Codex One.", StringComparison.Ordinal), "the Codex models the catalog lists");
            var input = TagWith(html, "data-setup-key-input");
            Check(input.Contains("type=\"password\"", StringComparison.Ordinal) && !input.Contains("value=", StringComparison.Ordinal), "the key field is a password field and empty");
            Check(TagWith(html, "<option value=\"anthropic\"").Contains("selected", StringComparison.Ordinal), "the list starts on Anthropic");
            Check(html.Contains("<optgroup label=\"Cloud accounts\">", StringComparison.Ordinal), "cloud accounts are grouped");
            Check(TagWith(html, "data-setup-variable=\"ANTHROPIC_API_KEY\"").Contains("data-set=\"false\"", StringComparison.Ordinal), "variables that are not set");
            Check(html.Contains("data-setup-more-variables", StringComparison.Ordinal), "and how many more");
            Check(!html.Contains("data-offline-note", StringComparison.Ordinal), "online: no offline note");
            // W3 fix: the link opens the editor on the LM Studio template (custom-providers.js reads ?add= once).
            Check(html.Contains("href=\"/settings/custom-providers?add=lmstudio\"", StringComparison.Ordinal), "a local server is added in Settings › Custom providers");
        }),

        Case("setup.render.connect-step-lists-what-is-connected", async () =>
        {
            await using var host = SetupHost("setup-connected");
            var now = DateTimeOffset.UtcNow;
            var html = await RenderSetupAsync(host, SetupDirectory([SetupKey(), SetupCodex(now)]), "connect", now: now);
            Check(html.Contains("data-setup-state=\"default\"", StringComparison.Ordinal), "the default state");
            Check(html.Contains("data-setup-connected=\"openai\"", StringComparison.Ordinal) && html.Contains($"data-setup-connected=\"{SignInFlows.CodexProvider}\"", StringComparison.Ordinal),
                "a row per connected provider");
            Check(html.Contains("data-setup-codex-connected", StringComparison.Ordinal), "the ChatGPT card shows the plan instead of Sign in");
            Check(!html.Contains($"data-signin-provider=\"{SignInFlows.CodexProvider}\"", StringComparison.Ordinal), "no Sign in button");
            Check(html.Contains("AbCd", StringComparison.Ordinal) && html.Contains("expires in 9 days, refreshes automatically", StringComparison.Ordinal), "the connections' details");
            Check(Regex.IsMatch(html, "<button[^>]*data-setup-go=\"model\">Continue"), "Continue leads to the Default model step");
            Check(!html.Contains("data-setup-continue-blocked", StringComparison.Ordinal), "not blocked");
            Equal("2 providers connected", TextOf(html, "data-setup-count"), "the count");
            Check(!html.Contains("data-setup-nothing", StringComparison.Ordinal), "no empty panel");
        }),

        Case("setup.render.offline-disables-sign-in", async () =>
        {
            await using var host = SetupHost("setup-offline");
            var html = await RenderSetupAsync(host, SetupDirectory([], offline: true), "connect", offline: true);
            Check(html.Contains("data-offline-note", StringComparison.Ordinal), "the offline note");
            var signIn = TagWith(html, $"data-signin-provider=\"{SignInFlows.CodexProvider}\"");
            Check(signIn.Contains("disabled", StringComparison.Ordinal), "offline, Sign in with ChatGPT is disabled");
            Check(html.Contains("data-setup-key-form", StringComparison.Ordinal), "a key can still be stored");
        }),

        Case("setup.render.default-model-step", async () =>
        {
            await using var host = SetupHost("setup-model");
            await host.Services.GetRequiredService<ISettingsStore>().SaveAsync(new DefaultsSettings("openai", "gpt-x", "medium"));
            var html = await RenderSetupAsync(host, SetupDirectory([SetupKey()]), "model");
            Check(StepShown(html, "model") && !StepShown(html, "connect") && !StepShown(html, "done"), "the Default model step shows");
            var providers = html[html.IndexOf("id=\"sm-provider\"", StringComparison.Ordinal)..];
            providers = providers[..providers.IndexOf("</select>", StringComparison.Ordinal)];
            Check(providers.Contains("value=\"openai\"", StringComparison.Ordinal) && !providers.Contains("value=\"anthropic\"", StringComparison.Ordinal),
                "only connected providers are offered");
            Check(TagWith(html, "<option value=\"gpt-x\"").Contains("selected", StringComparison.Ordinal), "the saved default is chosen");
            Check(html.Contains("data-setup-saved-default", StringComparison.Ordinal) && html.Contains("openai/gpt-x", StringComparison.Ordinal), "and named");
            Check(!TagWith(html, "id=\"st-off\"").Contains("disabled", StringComparison.Ordinal), "off is supported");
            Check(TagWith(html, "id=\"st-minimal\"").Contains("disabled", StringComparison.Ordinal), "minimal is not");
            Check(TagWith(html, "id=\"st-high\"").Contains("checked", StringComparison.Ordinal), "medium is clamped to high");
            Check(!TagWith(html, "id=\"st-medium\"").Contains("checked", StringComparison.Ordinal), "only one level is chosen");
            Check(html.Contains("GPT &lt;b&gt;X&lt;/b&gt;", StringComparison.Ordinal) && !html.Contains("GPT <b>X</b>", StringComparison.Ordinal), "model names are text");
            Check(TagWith(html, "data-setup-use-default").Contains("checked", StringComparison.Ordinal), "Use as default starts on");
            Check(html.Contains("defaults.provider", StringComparison.Ordinal) && html.Contains("defaults.thinking", StringComparison.Ordinal), "the keys it saves");
            Check(!TagHidden(TagWith(html, "data-setup-finish data-setup-only")), "Save and continue");
            Check(TagHidden(TagWith(html, "data-setup-skip")), "Skip setup is for Welcome");
            Check(TagWith(html, "data-setup-indicator=\"connect\"").Contains("class=\"done\"", StringComparison.Ordinal), "earlier steps are done");
        }),

        Case("setup.render.default-model-step-without-catalog-models", async () =>
        {
            await using var host = SetupHost("setup-no-models");
            var html = await RenderSetupAsync(host, SetupDirectory([SetupKey("silent", "sk-…0000")]), "model");
            Check(StepShown(html, "model"), "the Default model step shows");
            Check(html.Contains("data-setup-no-models", StringComparison.Ordinal) && !html.Contains("data-setup-thinking", StringComparison.Ordinal),
                "no picker, and how to choose later");
            Check(!TagHidden(TagWith(html, "data-setup-finish data-setup-only")), "setup can still finish");
        }),

        Case("setup.render.done-step", async () =>
        {
            await using var host = SetupHost("setup-done");
            var store = host.Services.GetRequiredService<ISettingsStore>();
            await store.SaveAsync(new DefaultsSettings("openai", "gpt-x", "high"));
            await store.SaveAsync(new ModelsSettings(["openai/gpt-hidden"]));
            await store.SaveAsync(SetupSettings.Ended(SetupOutcome.Finished, DateTimeOffset.UtcNow));
            var html = await RenderSetupAsync(host, SetupDirectory([SetupKey()]), "done");
            Check(StepShown(html, "done") && !StepShown(html, "model"), "the Done step shows");
            Check(html.Contains("data-setup-finished=\"true\"", StringComparison.Ordinal), "finished");
            Equal("openai", TextOf(html, "data-setup-summary-providers"), "the providers");
            Equal("openai/gpt-x", TextOf(html, "data-setup-summary-model"), "the default model");
            Equal("high", TextOf(html, "data-setup-summary-thinking"), "the level");
            Equal("2", TextOf(html, "data-setup-summary-models"), "the models, less the hidden one");
            Equal(@"C:\data\PiSharpWebAgent", TextOf(html, "data-setup-summary-data"), "the data folder");
            Check(html.Contains("href=\"/chat/new\"", StringComparison.Ordinal) && html.Contains("href=\"/playground\"", StringComparison.Ordinal), "Start chatting, Open Playground");

            await store.ResetAsync<DefaultsSettings>();
            var unset = await RenderSetupAsync(host, SetupDirectory([SetupKey()]), "done");
            Equal("not set", TextOf(unset, "data-setup-summary-model"), "no default model");
            Equal("model default", TextOf(unset, "data-setup-summary-thinking"), "no level");
        }),

        Case("setup.app.first-run-sends-the-front-doors-to-setup-until-skipped", async () =>
        {
            var root = Temp("setup-app");
            try
            {
                await using var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = Path.Join(root, "data"), Faux = false, Environment = NoProviderEnvironment() });
                using var raw = NoRedirects(app);
                var status = await app.Get("/api/setup");
                Equal(200, status.Status, "the status");
                Check(status.Response.Headers.CacheControl?.NoStore == true, "the status is no-store");
                Equal(0, status.Body!["connected"]!.AsArray().Count, "nothing connected: " + status.Text);
                Check(status.Body!["redirectsToSetup"]!.GetValue<bool>() && !status.Body!["offline"]!.GetValue<bool>(), "a first run, online");
                Check(status.Body!["outcome"] is null, "never ended");

                foreach (var path in new[] { "/", "/chat", "/chat/new" })
                {
                    using var response = await raw.GetAsync(path);
                    Equal(HttpStatusCode.Redirect, response.StatusCode, path + " redirects");
                    Equal("/setup", response.Headers.Location?.OriginalString, path + " goes to the wizard");
                    Check(response.Headers.CacheControl?.NoStore == true, path + "'s redirect is not cached");
                }
                foreach (var path in new[] { "/settings/providers", "/api/health", "/css/setup.css", "/js/setup.js" })
                {
                    using var response = await raw.GetAsync(path);
                    Equal(HttpStatusCode.OK, response.StatusCode, path + " is not redirected");
                }

                var page = await app.Get("/setup");
                Equal(200, page.Status, "the wizard");
                Check(page.Response.Headers.CacheControl?.NoStore == true, "the wizard is no-store");
                Check(page.Text.Contains("data-setup-state=\"nothing\"", StringComparison.Ordinal) && page.Text.Contains("data-setup-current=\"welcome\"", StringComparison.Ordinal),
                    "Welcome, nothing connected");
                Check(page.Text.Contains("/css/setup.css?v=", StringComparison.Ordinal) && page.Text.Contains("/js/setup.js?v=", StringComparison.Ordinal), "its stylesheet and script");
                Check(page.Text.Contains("id=\"modal-api-key\"", StringComparison.Ordinal) && page.Text.Contains("id=\"modal-chatgpt-signin\"", StringComparison.Ordinal),
                    "the Settings area's credential modals");
                Check(!page.Text.Contains("<script>", StringComparison.Ordinal), "no inline script");
                Check((await app.Get("/setup?step=model")).Text.Contains("data-setup-current=\"connect\"", StringComparison.Ordinal), "Default model waits for a connection");

                var early = await app.SendAsync(HttpMethod.Post, "/api/setup/finish", new JsonObject(), origin: app.Origin);
                Equal(409, early.Status, "finishing needs a connection");
                Check(early.Body?["error"] is not null, "with the reason");
                Equal(403, (await app.SendAsync(HttpMethod.Post, "/api/setup/skip", new JsonObject(), origin: "http://evil.example")).Status, "a cross-origin skip");
                Equal(415, (await app.SendAsync(HttpMethod.Post, "/api/setup/skip", origin: app.Origin, contentType: "text/plain", text: "{}")).Status, "a non-JSON skip");
                Check((await app.Get("/api/setup")).Body!["redirectsToSetup"]!.GetValue<bool>(), "refused writes change nothing");

                var skipped = await app.SendAsync(HttpMethod.Post, "/api/setup/skip", new JsonObject(), origin: app.Origin);
                Equal(200, skipped.Status, "skip");
                Equal("skipped", skipped.Body!["setup"]!["outcome"]!.GetValue<string>(), "skipped");
                using (var home = await raw.GetAsync("/"))
                {
                    Equal(HttpStatusCode.Redirect, home.StatusCode, "/ still redirects");
                    Equal("/chat/new", home.Headers.Location?.OriginalString, "to a new chat, as before");
                }
                using (var chat = await raw.GetAsync("/chat/new"))
                    Equal(HttpStatusCode.OK, chat.StatusCode, "the new chat page opens");
                Equal(false, (await app.Get("/api/setup")).Body!["redirectsToSetup"]!.GetValue<bool>(), "the first run is over");
                Equal(200, (await app.Get("/setup")).Status, "the wizard stays reachable");
                await app.StopAsync();
                Check(!app.Log.Contains("fail:", StringComparison.Ordinal) && !app.Log.Contains("Exception", StringComparison.Ordinal), "a clean run: " + app.Log);
                var stored = ReadDb(Path.Join(root, "data", "webagent.db"), "SELECT value FROM settings WHERE section = 'setup'", reader => reader.GetString(0)).Single();
                Check(stored.Contains("\"outcome\":\"skipped\"", StringComparison.Ordinal), "the setup section holds the outcome: " + stored);
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("setup.app.a-key-ends-the-first-run-and-finish-saves-the-default-model", async () =>
        {
            var key = Planted("sk-proj-");
            var root = Temp("setup-key");
            try
            {
                await using var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = Path.Join(root, "data"), Faux = false, Environment = NoProviderEnvironment() });
                using var raw = NoRedirects(app);
                var answers = new StringBuilder();
                var pages = new StringBuilder();
                async Task<(int Status, JsonNode? Body)> Post(string path, JsonNode body)
                {
                    var response = await app.SendAsync(HttpMethod.Post, path, body, origin: app.Origin);
                    answers.AppendLine(response.Text);
                    return (response.Status, response.Body);
                }
                async Task<string> Page(string path)
                {
                    var response = await app.Get(path);
                    Equal(200, response.Status, path);
                    pages.AppendLine(response.Text);
                    return response.Text;
                }

                Check((await app.Get("/api/setup")).Body!["redirectsToSetup"]!.GetValue<bool>(), "a first run");
                var added = await Post("/api/credentials/accounts", new JsonObject { ["provider"] = "openai", ["apiKey"] = key });
                Equal(200, added.Status, "the wizard's key form posts to the credentials API");
                Equal(SecretText.Mask(key), added.Body!["account"]!["hint"]!.GetValue<string>(), "the answer holds the masked hint only");
                using (var chat = await raw.GetAsync("/chat/new"))
                    Equal(HttpStatusCode.OK, chat.StatusCode, "a connected provider ends the redirect");
                var status = (await app.Get("/api/setup")).Body!;
                Check(status["connected"]!.AsArray().Any(id => id!.GetValue<string>() == "openai") && status["outcome"] is null, "connected; the wizard itself has not ended");
                var connect = await Page("/setup?step=connect");
                Check(connect.Contains("data-setup-state=\"default\"", StringComparison.Ordinal) && connect.Contains("data-setup-connected=\"openai\"", StringComparison.Ordinal),
                    "the Connect step lists OpenAI");
                Check((await Page("/setup?step=model")).Contains("data-setup-current=\"model\"", StringComparison.Ordinal), "the Default model step opens");

                var catalog = new RuntimeModelCatalog();
                var model = catalog.Models("openai").First(item => item.ThinkingLevels.Contains("high", StringComparer.Ordinal));
                var other = catalog.Models("anthropic")[0];
                var refused = await Post("/api/setup/finish", new JsonObject { ["provider"] = "anthropic", ["model"] = other.Id, ["thinking"] = "high" });
                Equal(400, refused.Status, "an unconnected provider");
                Check(refused.Body!["error"]!.GetValue<string>().Contains("not connected", StringComparison.Ordinal), "with the reason");
                Equal(400, (await Post("/api/setup/finish", new JsonObject { ["provider"] = "openai", ["model"] = "no-such-model" })).Status, "a model the catalog does not list");
                Equal(415, (await app.SendAsync(HttpMethod.Post, "/api/setup/finish", origin: app.Origin, contentType: "text/plain", text: "{}")).Status, "JSON only");

                var finished = await Post("/api/setup/finish", new JsonObject { ["provider"] = "openai", ["model"] = model.Id, ["thinking"] = "high", ["useAsDefault"] = true });
                Equal(200, finished.Status, "Save and continue");
                Check(finished.Body!["savedDefaults"]!.GetValue<bool>(), "the defaults were written");
                Equal("finished", finished.Body!["setup"]!["outcome"]!.GetValue<string>(), "finished");
                var defaults = (await app.Get("/api/settings/defaults")).Body!;
                Equal("openai", defaults["provider"]!.GetValue<string>(), "Settings › Defaults has the provider");
                Equal(model.Id, defaults["model"]!.GetValue<string>(), "the model");
                Equal("high", defaults["thinking"]!.GetValue<string>(), "the level");
                var done = await Page("/setup?step=done");
                Check(done.Contains("data-setup-current=\"done\"", StringComparison.Ordinal), "Done");
                Equal("openai/" + model.Id, TextOf(done, "data-setup-summary-model"), "the summary names the default model");
                await Page("/chat/new");
                await Page("/settings/defaults");

                NoSecret(answers.ToString(), "an API answer", key);
                NoSecret(pages.ToString(), "a rendered page", key);
                await app.StopAsync();
                NoSecret(app.Log, "the app's log", key);
                Check(FilesHolding(Path.Join(root, "data"), key).Count == 0, "the planted key is on disk in plain text");
                Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
                var stored = ReadDb(Path.Join(root, "data", "webagent.db"), "SELECT value FROM settings WHERE section = 'defaults'", reader => reader.GetString(0)).Single();
                Check(stored.Contains(model.Id, StringComparison.Ordinal), "the Defaults section row holds the model: " + stored);
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("setup.app.faux-reaches-the-wizard-but-never-forces-it", async () =>
        {
            await using var app = await WebAgentApp.StartAsync("setup-faux");
            using var raw = NoRedirects(app);
            using (var home = await raw.GetAsync("/"))
                Equal("/chat/new", home.Headers.Location?.OriginalString, "/ opens a new chat");
            using (var chat = await raw.GetAsync("/chat/new"))
                Equal(HttpStatusCode.OK, chat.StatusCode, "never sent to the wizard");
            var status = (await app.Get("/api/setup")).Body!;
            Check(status["offline"]!.GetValue<bool>() && !status["redirectsToSetup"]!.GetValue<bool>(), "offline, no redirect");
            Check(status["connected"]!.AsArray().Any(id => id!.GetValue<string>() == FauxDurableModels.ProviderId), "the faux provider counts as connected");
            var page = await app.Get("/setup?step=connect");
            Equal(200, page.Status, "the wizard is reachable");
            Check(page.Text.Contains("data-setup-state=\"default\"", StringComparison.Ordinal) && page.Text.Contains("data-setup-connected=\"faux\"", StringComparison.Ordinal),
                "the default state, with the faux provider");
            Check(page.Text.Contains("data-offline-note", StringComparison.Ordinal), "the offline note");
            Check(TagWith(page.Text, $"data-signin-provider=\"{SignInFlows.CodexProvider}\"").Contains("disabled", StringComparison.Ordinal), "no sign-in offline");

            var finished = await app.SendAsync(HttpMethod.Post, "/api/setup/finish",
                new JsonObject { ["provider"] = FauxDurableModels.ProviderId, ["model"] = FauxModelCatalog.EchoThinking.Id, ["thinking"] = "max" }, origin: app.Origin);
            Equal(200, finished.Status, "finish");
            var expected = ThinkingLevels.Clamp([.. FauxModelCatalog.ReasoningLevels], "max");
            Equal(expected, finished.Body!["defaults"]!["thinking"]!.GetValue<string>(), "max is clamped to the model's highest level");
            var defaults = (await app.Get("/api/settings/defaults")).Body!;
            Equal(FauxModelCatalog.EchoThinking.Id, defaults["model"]!.GetValue<string>(), "saved as the default model");
            Check((await app.Get("/setup?step=done")).Text.Contains("data-setup-current=\"done\"", StringComparison.Ordinal), "Done");
            await app.StopAsync();
            Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
        }),
    ];
}
