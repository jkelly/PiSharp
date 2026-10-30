using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.WebAgent.Credentials;

// Decision 0011 (run W2, WU9): the settings pages and /api/settings end to end. The built app runs as a child process with --faux (offline),
// a temporary data folder and, where a case says so, one planted environment key. Every M0 settings page renders in the shell (no inline
// script or handler, no-store); a key added, renamed, replaced, made active and removed through /api/credentials shows on the pages as its
// masked hint only, and no page, API answer, log line or data file ever holds a planted secret; Defaults, Tools and model visibility save,
// reset and refuse invalid input with 400; "forget a provider" removes its stored accounts and shows its hidden models again; the folder
// browser lists a temporary tree. HostGuard covers /api/settings writes.
internal static partial class Program
{
    private static readonly string[] SettingsScripts = ["settings.js", "settings-api.js", "settings-keys.js", "settings-signin.js", "settings-models.js", "settings-forms.js"];

    /// <summary>The model switch (<c>&lt;input … data-model-toggle="key"&gt;</c>) of <paramref name="key"/> in <paramref name="html"/>.</summary>
    private static string Toggle(string html, string key)
    {
        var match = Regex.Match(html, $"<input [^>]*data-model-toggle=\"{Regex.Escape(key)}\"[^>]*>");
        Check(match.Success, "no switch for " + key);
        return match.Value;
    }

    internal static IEnumerable<(string, Func<Task>)> SettingsAppCases() =>
    [
        Case("settings.app-pages-render-in-the-shell", async () =>
        {
            await using var app = await WebAgentApp.StartAsync("set-pages");
            async Task<string> Page(string href)
            {
                var page = await app.Get(href);
                Equal(200, page.Status, href);
                Check(page.Response.Headers.CacheControl?.NoStore == true, href + " is no-store (it lists accounts)");
                CheckShell(href, page.Text);
                Check(Regex.IsMatch(page.Text, "<link href=\"/css/settings.css\\?v=[^\"]+\" rel=\"stylesheet\">"), href + " links settings.css");
                Check(Regex.IsMatch(page.Text, "<script type=\"module\" src=\"/js/settings.js\\?v=[^\"]+\"></script>"), href + " loads settings.js as a module");
                Check(!page.Text.Contains("data-signin-provider=\"anthropic\"", StringComparison.Ordinal), href + ": no Anthropic sign-in");
                Check(!Regex.IsMatch(page.Text, "\\?(state|modal|pane)="), href + ": none of the mockups' ?state= machinery");
                return page.Text;
            }

            var redirect = await app.Get("/settings");
            Equal("/settings/providers", redirect.Response.RequestMessage!.RequestUri!.AbsolutePath, "/settings opens Providers");

            var providers = await Page("/settings/providers");
            foreach (var part in new[] { "id=\"modal-api-key\"", "id=\"modal-chatgpt-signin\"", "id=\"modal-oauth-signin\"", "id=\"modal-rename-account\"",
                         "data-provider-filters", "data-provider-search", "data-provider-row=\"faux\"", "data-provider-row=\"openai\"", "data-provider-row=\"anthropic\"",
                         "href=\"/settings/providers/openai-codex\"", "class=\"settings-nav\"", "data-offline-note" })
                Check(providers.Contains(part, StringComparison.Ordinal), "Providers has " + part);
            Check(Regex.IsMatch(providers, "data-provider-row=\"faux\" data-state=\"connected\""), "faux needs no key, so it is connected");
            Check(Regex.IsMatch(providers, "data-provider-row=\"openai\" data-state=\"none\""), "openai has no account yet");
            Check(Regex.IsMatch(providers, "<input class=\"form-control secret-field\" id=\"apiKeyInput\" type=\"password\" autocomplete=\"off\" spellcheck=\"false\" data-key-secret>"),
                "the key field is a password field with no value");
            Check(providers.Contains("Use your ChatGPT Plus or Pro subscription for Codex models.", StringComparison.Ordinal), "the ChatGPT opt-in notice");
            Check(Regex.IsMatch(providers, "data-signin-start=\"browser\"[^>]*disabled"), "offline, sign-in cannot start");
            Check(Regex.IsMatch(providers, "data-provider-more hidden"), "the rest of the available providers wait behind Show more");

            var openai = await Page("/settings/providers/openai");
            foreach (var part in new[] { "data-forget-provider=\"openai\"", "data-key-provider=\"openai\"", "data-key-form=\"single\"", "data-key-variables=\"OPENAI_API_KEY\"",
                         "href=\"/settings/providers\"" })
                Check(openai.Contains(part, StringComparison.Ordinal), "openai's page has " + part);
            Check(Regex.IsMatch(openai, "data-forget-provider=\"openai\"[^>]*disabled"), "nothing to forget yet");
            var codex = await Page("/settings/providers/openai-codex");
            Check(codex.Contains("data-signin-provider=\"openai-codex\"", StringComparison.Ordinal) && codex.Contains("data-bs-target=\"#modal-chatgpt-signin\"", StringComparison.Ordinal),
                "Codex signs in with ChatGPT");
            Check(!codex.Contains("data-key-provider=\"openai-codex\"", StringComparison.Ordinal), "Codex takes no key");
            // W5 integration fix 1: no planned milestone builds these sections, so they say "Not in this version", never "later".
            Check(codex.Contains("Not in this version", StringComparison.Ordinal) && codex.Contains("id=\"usage-limits\"", StringComparison.Ordinal), "usage limits are not in this version");
            Check(!codex.Contains("later milestone", StringComparison.OrdinalIgnoreCase), "no Codex section promises a later milestone");
            var copilot = await Page("/settings/providers/github-copilot");
            Check(copilot.Contains("data-signin-provider=\"github-copilot\"", StringComparison.Ordinal) && copilot.Contains("data-bs-target=\"#modal-oauth-signin\"", StringComparison.Ordinal),
                "Copilot signs in with a device code");
            var anthropic = await Page("/settings/providers/anthropic");
            Check(anthropic.Contains("data-key-provider=\"anthropic\"", StringComparison.Ordinal), "Anthropic takes an API key");
            Check(!anthropic.Contains("data-bs-target=\"#modal-oauth-signin\" data-signin-provider=\"anthropic\"", StringComparison.Ordinal), "and offers no sign-in");
            Check((await Page("/settings/providers/amazon-bedrock")).Contains("data-key-form=\"bedrock\"", StringComparison.Ordinal), "Bedrock's own form");
            Check((await Page("/settings/providers/google-vertex")).Contains("data-key-form=\"vertex\"", StringComparison.Ordinal), "Vertex's own form");
            Check((await Page("/settings/providers/azure")).Contains("data-key-form=\"azure\"", StringComparison.Ordinal), "Azure's own form");
            Check((await Page("/settings/providers/cloudflare-ai-gateway")).Contains("data-key-form=\"cloudflare\"", StringComparison.Ordinal), "Cloudflare's own form");
            var faux = await Page("/settings/providers/faux");
            Check(faux.Contains("data-model-toggle=\"faux/echo\"", StringComparison.Ordinal), "a provider's models and their switches");
            Check(!faux.Contains("data-key-provider=\"faux\"", StringComparison.Ordinal), "faux needs no key");
            Equal(404, (await app.Get("/settings/providers/no-such-provider")).Status, "an unknown provider");

            var accounts = await Page("/settings/accounts");
            foreach (var part in new[] { "data-oauth-card=\"openai-codex\"", "data-oauth-card=\"github-copilot\"", "data-accounts-summary", "Anthropic doesn" })
                Check(accounts.Contains(part, StringComparison.Ordinal), "Accounts has " + part);
            Check(!accounts.Contains("data-oauth-card=\"anthropic\"", StringComparison.Ordinal), "no Anthropic sign-in card");

            var models = await Page("/settings/models");
            foreach (var part in new[] { "data-models-table", "data-model-toggle=\"faux/echo\"", "data-model-toggle=\"faux/echo-thinking\"", "id=\"modal-model-details\"",
                         "data-models-filter", "data-models-json", "data-models-bulk=\"hide\"" })
                Check(models.Contains(part, StringComparison.Ordinal), "Models has " + part);
            Check(models.Contains("settings-content is-wide", StringComparison.Ordinal), "the wide layout");
            var searched = await Page("/settings/models?f=1&q=thinking");
            Check(searched.Contains("data-model-toggle=\"faux/echo-thinking\"", StringComparison.Ordinal) && !searched.Contains("data-model-toggle=\"faux/echo\"", StringComparison.Ordinal),
                "the search filters on the server");
            Check((await Page("/settings/models?f=1&q=zzz-nothing")).Contains("data-models-empty", StringComparison.Ordinal), "no match");

            var defaults = await Page("/settings/defaults");
            foreach (var part in new[] { "data-settings-form=\"defaults\"", "data-endpoint=\"/api/settings/defaults\"", "data-provider-model=\"dm\"", "data-thinking-levels",
                         "data-savebar", "data-defaults-reset", "data-model-unset=\"true\"", "Not in this version" })
                Check(defaults.Contains(part, StringComparison.Ordinal), "Defaults has " + part);
            var tools = await Page("/settings/tools");
            foreach (var part in new[] { "data-settings-form=\"tools\"", "data-endpoint=\"/api/settings/tools\"", "data-folder-browser", "name=\"approvalMode\"",
                         "value=\"allowList\"", "data-allow-template", "data-confirm-mode", "Not in this version", "data-session-grants", "data-session-grants-empty" })
                Check(tools.Contains(part, StringComparison.Ordinal), "Tools has " + part);
            Check(Regex.IsMatch(tools, "name=\"approvalMode\" id=\"[^\"]+\" value=\"ask\" checked"), "ask is the default mode");
            foreach (var (name, page) in new[] { ("Defaults", defaults), ("Tools", tools), ("Codex", codex), ("OpenAI", openai) })
                Check(!page.Contains("later milestone", StringComparison.OrdinalIgnoreCase), name + " promises no later milestone");
            Check(!Regex.IsMatch(tools, "data-session-grants-empty[^>]*hidden"), "no chat has a grant yet, and the card says so");

            foreach (var name in SettingsScripts)
            {
                var script = await app.Get("/js/" + name);
                Equal(200, script.Status, name);
                Equal("text/javascript", script.Response.Content.Headers.ContentType?.MediaType, name + " type");
                foreach (var raw in new[] { "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "eval(" })
                    Check(!script.Text.Contains(raw, StringComparison.Ordinal), $"{name} never uses {raw}");
                Check(!Regex.IsMatch(script.Text, "[?&](state|modal|pane)="), name + ": none of mockup.js's query machinery");
            }
            Check((await app.Get("/js/settings.js")).Text.Contains("from './shell.js'", StringComparison.Ordinal), "settings.js shares shell.js with site.js");
            var css = await app.Get("/css/settings.css");
            Check(css.Status == 200 && css.Text.Contains(".settings-content [hidden]", StringComparison.Ordinal), "settings.css keeps [hidden] hidden");
            Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
        }),

        Case("settings.app-key-round-trip-shows-only-the-masked-hint", async () =>
        {
            var envKey = Planted("sk-");
            var root = Temp("set-keys");
            try
            {
                await using var app = await WebAgentApp.StartAsync(root, new AppStart
                {
                    DataDirectory = Path.Join(root, "data"),
                    Environment = new Dictionary<string, string> { ["OPENAI_API_KEY"] = envKey },
                });
                var key = Planted("sk-proj-");
                var key2 = Planted("sk-proj-");
                var googleKey = Planted("AIza");
                var seen = new StringBuilder();
                async Task<JsonNode?> Call(HttpMethod method, string path, JsonNode? body = null, int status = 200)
                {
                    var answer = await app.SendAsync(method, path, body, origin: method == HttpMethod.Get ? null : app.Origin);
                    seen.AppendLine(answer.Text);
                    Equal(status, answer.Status, $"{method} {path}: {answer.Text}");
                    Check(answer.Response.Headers.CacheControl?.NoStore == true, path + " is no-store");
                    return answer.Body;
                }
                async Task<string> Page(string href)
                {
                    var page = await app.Get(href);
                    Equal(200, page.Status, href);
                    CheckShell(href, page.Text);
                    seen.AppendLine(page.Text);
                    return page.Text;
                }

                var environment = (await Call(HttpMethod.Get, "/api/credentials/accounts?provider=openai"))!["accounts"]!.AsArray().Single()!;
                var envId = environment["id"]!.GetValue<string>();
                var before = await Page("/settings/providers");
                Check(Regex.IsMatch(before, "data-provider-row=\"openai\" data-state=\"env\""), "the variable connects openai");
                var envPage = await Page("/settings/providers/openai");
                Check(envPage.Contains($"data-account-card=\"{envId}\"", StringComparison.Ordinal), "the variable's card");
                Check(!envPage.Contains($"data-account-action=\"remove\" data-account-id=\"{envId}\"", StringComparison.Ordinal) &&
                    envPage.Contains("Unset it on the web host to remove it.", StringComparison.Ordinal), "a variable cannot be removed here");

                var added = await Call(HttpMethod.Post, "/api/credentials/accounts", new JsonObject
                {
                    ["provider"] = "openai", ["apiKey"] = key, ["label"] = "Work", ["settings"] = new JsonObject { ["baseUrl"] = "https://proxy.example.test/v1" },
                });
                var id = added!["account"]!["id"]!.GetValue<string>();
                var hint = added["account"]!["hint"]!.GetValue<string>();
                Equal(SecretText.Mask(key), hint, "the hint");
                Check(added["becameActive"]!.GetValue<bool>(), "the first stored key becomes active, ahead of the variable");

                var page = await Page("/settings/providers/openai");
                Check(page.Contains($"data-account-card=\"{id}\"", StringComparison.Ordinal), "the new account's card");
                Check(page.Contains($"<span class=\"ps-mono\" data-account-hint>{Html(hint)}</span>", StringComparison.Ordinal), "its masked hint");
                Check(page.Contains($"<option value=\"{id}\" selected=\"selected\">", StringComparison.Ordinal), "it is the active account");
                Check(page.Contains("https://proxy.example.test/v1", StringComparison.Ordinal), "its base URL setting");
                Check(page.Contains($"data-key-account=\"{id}\"", StringComparison.Ordinal) && page.Contains($"data-key-hint=\"{Html(hint)}\"", StringComparison.Ordinal),
                    "Replace key carries the account and its hint, not its key");
                Check(Regex.IsMatch(page, "data-forget-provider=\"openai\" data-provider-name=\"OpenAI\" data-forget-count=\"1\""), "one stored account to forget");
                Check(Regex.IsMatch(await Page("/settings/providers"), "data-provider-row=\"openai\" data-state=\"connected\""), "openai is connected by the key");

                Equal("Team", (await Call(HttpMethod.Post, $"/api/credentials/accounts/{id}/rename", new JsonObject { ["label"] = "Team" }))!["label"]!.GetValue<string>(), "rename");
                Check(Regex.IsMatch(await Page("/settings/providers/openai"), "<span class=\"fw-semibold text-break\" data-account-name>Team</span>"), "the page shows the new name");

                var second = await Call(HttpMethod.Post, "/api/credentials/accounts", new JsonObject { ["provider"] = "openai", ["apiKey"] = key2, ["label"] = "Personal" });
                var id2 = second!["account"]!["id"]!.GetValue<string>();
                Check(!second["becameActive"]!.GetValue<bool>(), "a second key never becomes active on its own");
                page = await Page("/settings/providers/openai");
                Check(page.Contains($"<option value=\"{id}\" selected=\"selected\">", StringComparison.Ordinal) && page.Contains($"<option value=\"{id2}\">", StringComparison.Ordinal),
                    "the first account stays active");
                Check(Regex.IsMatch(page, $"data-account-action=\"activate\"[^>]*data-account-id=\"{id2}\""), "Make active on the second");

                Check((await Call(HttpMethod.Post, $"/api/credentials/accounts/{id2}/activate"))!["isActive"]!.GetValue<bool>(), "activate");
                page = await Page("/settings/providers/openai");
                Check(page.Contains($"<option value=\"{id2}\" selected=\"selected\">", StringComparison.Ordinal), "the page shows the chosen account as active");

                var replaced = await Call(HttpMethod.Post, $"/api/credentials/accounts/{id}/key", new JsonObject { ["apiKey"] = key + "x" });
                Check(!replaced!.ToJsonString().Contains(key, StringComparison.Ordinal), "a replaced key is not echoed");

                var removed = await Call(HttpMethod.Post, $"/api/credentials/accounts/{id2}/remove");
                Check(removed!["active"] is not null, "another account becomes active when the active one goes");
                page = await Page("/settings/providers/openai");
                Check(!page.Contains($"data-account-card=\"{id2}\"", StringComparison.Ordinal), "the removed account's card is gone");

                await Call(HttpMethod.Post, "/api/credentials/accounts", new JsonObject { ["provider"] = "google", ["apiKey"] = googleKey });
                await Call(HttpMethod.Post, "/api/settings/models/visibility", new JsonObject { ["models"] = new JsonArray("faux/echo", "faux/echo-small"), ["hidden"] = true });
                var forgot = await Call(HttpMethod.Post, "/api/settings/providers/openai/forget");
                Equal(1, forgot!["removedAccounts"]!.GetValue<int>(), "the stored openai key is removed");
                Equal(0, forgot["shownModels"]!.GetValue<int>(), "openai had no hidden models");
                var left = (await Call(HttpMethod.Get, "/api/credentials/accounts?provider=openai"))!["accounts"]!.AsArray();
                Equal(envId, left.Single()!["id"]!.GetValue<string>(), "the variable is untouched");
                var fauxForgot = await Call(HttpMethod.Post, "/api/settings/providers/faux/forget");
                Equal(0, fauxForgot!["removedAccounts"]!.GetValue<int>(), "faux has no accounts");
                Equal(2, fauxForgot["shownModels"]!.GetValue<int>(), "its hidden models show again");
                Equal(0, (await Call(HttpMethod.Get, "/api/settings/models"))!["hidden"]!.AsArray().Count, "nothing is hidden now");
                Equal(1, (await Call(HttpMethod.Get, "/api/credentials/accounts?provider=google"))!["accounts"]!.AsArray().Count, "another provider keeps its key");
                Equal(404, (await app.SendAsync(HttpMethod.Post, "/api/settings/providers/no-such-provider/forget", origin: app.Origin)).Status, "an unknown provider");

                foreach (var href in new[] { "/settings/providers", "/settings/providers/google", "/settings/accounts", "/settings/models", "/settings/defaults", "/settings/tools" })
                    await Page(href);
                NoSecret(seen.ToString(), "a page or API answer", key, key + "x", key2, googleKey, envKey);
                await app.StopAsync();
                NoSecret(app.Log, "the app's log", key, key + "x", key2, googleKey, envKey);
                foreach (var secret in new[] { key, key2, googleKey, envKey })
                    Check(FilesHolding(Path.Join(root, "data"), secret).Count == 0, "a planted secret is on disk in plain text");
                Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("settings.app-settings-api-saves-resets-and-refuses", async () =>
        {
            var root = Temp("set-api");
            try
            {
                await using var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = Path.Join(root, "data") });
                async Task<JsonNode?> Call(HttpMethod method, string path, JsonNode? body = null, int status = 200)
                {
                    var answer = await app.SendAsync(method, path, body, origin: method == HttpMethod.Get ? null : app.Origin);
                    Equal(status, answer.Status, $"{method} {path} {body?.ToJsonString()}: {answer.Text}");
                    Check(answer.Response.Headers.CacheControl?.NoStore == true, path + " is no-store");
                    if (status >= 400) Check(answer.Body?["error"]?.GetValue<string>() is { Length: > 0 }, path + " says why");
                    return answer.Body;
                }
                var post = HttpMethod.Post;

                // Defaults.
                var defaults = await Call(HttpMethod.Get, "/api/settings/defaults");
                Check(defaults!["provider"] is null && defaults["model"] is null, "nothing chosen yet");
                var saved = await Call(post, "/api/settings/defaults", new JsonObject { ["provider"] = "faux", ["model"] = "echo-thinking", ["thinking"] = "high" });
                Equal("faux|echo-thinking|high", $"{saved!["provider"]}|{saved["model"]}|{saved["thinking"]}", "saved");
                var page = (await app.Get("/settings/defaults")).Text;
                Check(Regex.IsMatch(page, "<option value=\"echo-thinking\" selected=\"selected\""), "the page selects the default model");
                Check(page.Contains("data-model-unset=\"false\"", StringComparison.Ordinal), "a saved default model is no longer \"not set\"");
                Check(Regex.IsMatch(page, "name=\"thinking\" id=\"[^\"]+\" value=\"high\"[^>]*checked"), "and the thinking level");
                await Call(post, "/api/settings/defaults", new JsonObject { ["provider"] = "faux" }, 400);
                await Call(post, "/api/settings/defaults", new JsonObject { ["provider"] = "faux", ["model"] = "no-such-model" }, 400);
                await Call(post, "/api/settings/defaults", new JsonObject { ["provider"] = "openai", ["model"] = "gpt-4o" }, 400);
                await Call(post, "/api/settings/defaults", new JsonObject { ["provider"] = "faux", ["model"] = "echo", ["thinking"] = "ludicrous" }, 400);
                Equal("echo-thinking", (await Call(HttpMethod.Get, "/api/settings/defaults"))!["model"]!.GetValue<string>(), "a refused save changes nothing");
                Equal(415, (await app.SendAsync(post, "/api/settings/defaults", origin: app.Origin, contentType: "text/plain", text: "provider=faux")).Status, "a form post");
                Equal(403, (await app.SendAsync(post, "/api/settings/defaults", new JsonObject(), origin: "http://evil.example")).Status, "a cross-origin write");
                await Call(post, "/api/settings/defaults/reset");
                Check((await Call(HttpMethod.Get, "/api/settings/defaults"))!["model"] is null, "reset");

                // Tools & workspace.
                var work = Path.Join(root, "work");
                Directory.CreateDirectory(work);
                File.WriteAllText(Path.Join(work, "AGENTS.md"), "# test");
                var tools = await Call(post, "/api/settings/tools", new JsonObject
                {
                    ["workingDirectory"] = work, ["approvalMode"] = "allowList",
                    ["allowList"] = new JsonArray(new JsonObject { ["tool"] = "bash", ["command"] = "dotnet test *" }, new JsonObject { ["tool"] = "edit" }),
                });
                Equal("allowList", tools!["approvalMode"]!.GetValue<string>(), "the mode");
                Equal(Path.GetFullPath(work), Path.GetFullPath(tools["workingDirectory"]!.GetValue<string>()), "the folder");
                Equal(2, tools["allowList"]!.AsArray().Count, "the allow-list");
                page = (await app.Get("/settings/tools")).Text;
                Check(page.Contains($"value=\"{Html(work)}\"", StringComparison.Ordinal), "the page shows the folder");
                Check(Regex.IsMatch(page, "data-allow-rule data-tool=\"bash\" data-command=\"dotnet test \\*\""), "a bash rule");
                // Razor always writes data-* attributes, so a rule without a pattern has an empty one (settings-forms.js reads "" as none).
                Check(page.Contains("data-allow-rule data-tool=\"edit\" data-command=\"\">", StringComparison.Ordinal), "an edit rule, without a pattern");
                Check(Regex.IsMatch(page, "value=\"allowList\" checked"), "the mode");
                Check(page.Contains("AGENTS.md", StringComparison.Ordinal), "the folder's context file");
                await Call(post, "/api/settings/tools", new JsonObject { ["workingDirectory"] = "relative/folder" }, 400);
                await Call(post, "/api/settings/tools", new JsonObject { ["workingDirectory"] = Path.Join(root, "missing") }, 400);
                await Call(post, "/api/settings/tools", new JsonObject { ["approvalMode"] = "yolo" }, 400);
                await Call(post, "/api/settings/tools", new JsonObject { ["allowList"] = new JsonArray(new JsonObject { ["tool"] = "edit", ["command"] = "rm *" }) }, 400);
                await Call(post, "/api/settings/tools", new JsonObject { ["allowList"] = new JsonArray(new JsonObject { ["tool"] = "" }) }, 400);
                await Call(post, "/api/settings/tools", new JsonObject { ["allowList"] = new JsonArray(new JsonObject { ["tool"] = "bash", ["command"] = "a\nb" }) }, 400);
                Equal("allowList", (await Call(HttpMethod.Get, "/api/settings/tools"))!["approvalMode"]!.GetValue<string>(), "a refused save changes nothing");
                Equal("auto", (await Call(post, "/api/settings/tools", new JsonObject { ["approvalMode"] = "auto" }))!["approvalMode"]!.GetValue<string>(), "auto");
                Equal("ask", (await Call(post, "/api/settings/tools/reset"))!["approvalMode"]!.GetValue<string>(), "reset");

                // Model visibility.
                var hidden = await Call(post, "/api/settings/models/visibility", new JsonObject { ["models"] = new JsonArray("faux/echo"), ["hidden"] = true });
                Equal("faux/echo", string.Join(",", hidden!["hidden"]!.AsArray().Select(node => node!.GetValue<string>())), "hidden");
                Equal(1, hidden["changed"]!.GetValue<int>(), "one change");
                page = (await app.Get("/settings/models?f=1")).Text;
                Check(!Toggle(page, "faux/echo").Contains("checked", StringComparison.Ordinal), "its switch is off");
                Check(Toggle(page, "faux/echo-small").Contains("checked=\"checked\"", StringComparison.Ordinal), "another stays on");
                Check(page.Contains("&quot;faux/echo&quot;", StringComparison.Ordinal), "the saved section shows it");
                Check(!(await app.Get("/settings/models?f=1&enabled=1")).Text.Contains("data-model-toggle=\"faux/echo\"", StringComparison.Ordinal), "Enabled only leaves it out");
                var all = await Call(post, "/api/settings/models/visibility", new JsonObject { ["provider"] = "faux", ["hidden"] = true });
                Equal(3, all!["hidden"]!.AsArray().Count, "a provider's every model");
                Equal(0, (await Call(post, "/api/settings/models/visibility", new JsonObject { ["provider"] = "faux", ["hidden"] = false }))!["hidden"]!.AsArray().Count, "shown again");
                await Call(post, "/api/settings/models/visibility", new JsonObject { ["models"] = new JsonArray("openai/gpt-4o"), ["hidden"] = true }, 400);
                await Call(post, "/api/settings/models/visibility", new JsonObject { ["models"] = new JsonArray("no-slash"), ["hidden"] = true }, 400);
                await Call(post, "/api/settings/models/visibility", new JsonObject { ["provider"] = "nope", ["hidden"] = true }, 400);
                await Call(post, "/api/settings/models/visibility", new JsonObject { ["hidden"] = true }, 400);
                await Call(post, "/api/settings/models/visibility", null, 400);

                // The folder browser.
                var tree = Path.Join(root, "tree");
                Directory.CreateDirectory(Path.Join(tree, "repo", ".git"));
                Directory.CreateDirectory(Path.Join(tree, "docs"));
                File.WriteAllText(Path.Join(tree, "docs", "AGENTS.md"), "# docs");
                Directory.CreateDirectory(Path.Join(tree, ".hidden"));
                Directory.CreateDirectory(Path.Join(tree, ".pi"));
                File.WriteAllText(Path.Join(tree, ".pi", "settings.json"), "{}");
                File.WriteAllText(Path.Join(tree, "CLAUDE.md"), "# claude");
                var listing = await Call(HttpMethod.Get, "/api/settings/folders?path=" + Uri.EscapeDataString(tree));
                Equal(Path.GetFullPath(tree), listing!["path"]!.GetValue<string>(), "the folder");
                Equal(Path.GetFullPath(root), listing["parent"]!.GetValue<string>(), "its parent");
                Equal("docs:False:True|repo:True:False", string.Join("|", listing["folders"]!.AsArray().Select(folder =>
                    $"{folder!["name"]}:{folder["git"]!.GetValue<bool>()}:{folder["agents"]!.GetValue<bool>()}")), "subfolders, without dot folders");
                Equal("CLAUDE.md,.pi/settings.json", string.Join(",", listing["contextFiles"]!.AsArray().Select(file => file!.GetValue<string>())), "context files");
                Equal("tree", listing["crumbs"]!.AsArray().Last()!["name"]!.GetValue<string>(), "the breadcrumb ends at the folder");
                Check(!listing["truncated"]!.GetValue<bool>(), "not truncated");
                await Call(HttpMethod.Get, "/api/settings/folders?path=relative", null, 400);
                await Call(HttpMethod.Get, "/api/settings/folders?path=" + Uri.EscapeDataString(Path.Join(root, "missing")), null, 400);
                var top = await Call(HttpMethod.Get, "/api/settings/folders");
                Check(top!["folders"]!.AsArray().Count > 0, "the top level lists drives or the root");
                Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),
    ];
}
