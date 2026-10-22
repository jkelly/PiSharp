using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Shell;

// Decision 0011 (run W2, WU5): /api/credentials end to end. The built app runs as a child process with --faux (offline), a temporary data
// folder and one planted environment key: adding, listing, renaming, replacing, activating, testing and removing an API key account
// through the API never returns the key, every answer is no-store, sign-in is refused offline, Anthropic sign-in is refused, and the
// planted secrets appear in no API answer, no rendered page, the app's log or any file of the data folder. HostGuard refuses cross-origin
// and non-JSON writes. The sign-in endpoints also run in process (Kestrel on a free loopback port) against the fake OAuth endpoints, so
// the device code flow is driven over HTTP as the page drives it.
internal static partial class Program
{
    private static readonly string[] CredentialPages = ["/settings/providers", "/settings/accounts", "/settings/models", "/chat/new", "/playground"];

    internal static IEnumerable<(string, Func<Task>)> CredentialAppCases() =>
    [
        Case("credentials.app-api-round-trip-never-returns-a-secret", async () =>
        {
            var envKey = Planted("sk-");
            var root = Temp("cred-app");
            try
            {
                await using var app = await WebAgentApp.StartAsync(root, new AppStart
                {
                    DataDirectory = Path.Join(root, "data"),
                    Environment = new Dictionary<string, string> { ["OPENAI_API_KEY"] = envKey },
                });
                var key = Planted("sk-proj-");
                var key2 = Planted("sk-proj-");
                var answers = new StringBuilder();
                async Task<(int Status, JsonNode? Body)> Call(HttpMethod method, string path, JsonNode? body = null)
                {
                    var response = await app.SendAsync(method, path, body, origin: method == HttpMethod.Get ? null : app.Origin);
                    answers.AppendLine(response.Text);
                    Check(response.Response.Headers.CacheControl?.NoStore == true, path + " is no-store");
                    return (response.Status, response.Body);
                }

                var listed = await Call(HttpMethod.Get, "/api/credentials/accounts");
                Equal(200, listed.Status, "list");
                var environment = listed.Body!["accounts"]!.AsArray().Single()!;
                Equal("environment", environment["kind"]!.GetValue<string>(), "the variable is an environment account");
                Check(environment["readOnly"]!.GetValue<bool>() && environment["isActive"]!.GetValue<bool>(), "read-only and active");
                Equal(SecretText.Mask(envKey), environment["hint"]!.GetValue<string>(), "masked");

                var added = await Call(HttpMethod.Post, "/api/credentials/accounts", new JsonObject
                {
                    ["provider"] = "openai", ["apiKey"] = key, ["label"] = "Work", ["settings"] = new JsonObject { ["baseUrl"] = "https://proxy.example.test/v1" },
                });
                Equal(200, added.Status, "add");
                var id = added.Body!["account"]!["id"]!.GetValue<string>();
                Check(added.Body!["becameActive"]!.GetValue<bool>(), "a stored key comes before the variable");
                Equal("apiKey", added.Body!["account"]!["kind"]!.GetValue<string>(), "kind");
                Equal(SecretText.Mask(key), added.Body!["account"]!["hint"]!.GetValue<string>(), "the hint");

                Equal("Team", (await Call(HttpMethod.Post, $"/api/credentials/accounts/{id}/rename", new JsonObject { ["label"] = "Team" })).Body!["label"]!.GetValue<string>(), "rename");
                Equal(SecretText.Mask(key2), (await Call(HttpMethod.Post, $"/api/credentials/accounts/{id}/key", new JsonObject { ["apiKey"] = key2 })).Body!["hint"]!.GetValue<string>(), "replace");
                var envId = environment["id"]!.GetValue<string>();
                Check((await Call(HttpMethod.Post, $"/api/credentials/accounts/{envId}/activate")).Body!["isActive"]!.GetValue<bool>(), "activate the variable");
                Equal(envId, (await Call(HttpMethod.Get, "/api/credentials/accounts?provider=openai")).Body!["accounts"]!.AsArray()
                    .Single(account => account!["isActive"]!.GetValue<bool>())!["id"]!.GetValue<string>(), "one active account");
                var tested = await Call(HttpMethod.Post, $"/api/credentials/accounts/{id}/test");
                Check(tested.Status == 200 && !tested.Body!["supported"]!.GetValue<bool>(), "offline: no test runs");
                var keyTest = await Call(HttpMethod.Post, "/api/credentials/test", new JsonObject { ["provider"] = "openai", ["apiKey"] = key });
                Check(keyTest.Status == 200 && !keyTest.Body!["supported"]!.GetValue<bool>(), "offline: no key test runs");

                var methods = await Call(HttpMethod.Get, "/api/credentials/signin");
                Check(methods.Body!["methods"]!.AsArray().All(method => !method!["available"]!.GetValue<bool>()), "no sign-in offline");
                Check(methods.Body!["methods"]!.AsArray().Any(method => method!["method"]!.GetValue<string>() == "deviceCode"), "methods by name");
                var signIn = await Call(HttpMethod.Post, "/api/credentials/signin", new JsonObject { ["provider"] = "openai-codex", ["method"] = "browser" });
                Equal(409, signIn.Status, "sign-in offline");
                Equal(CredentialsOptions.OfflineMessage, signIn.Body!["error"]!.GetValue<string>(), "says why");
                var anthropic = await Call(HttpMethod.Post, "/api/credentials/signin", new JsonObject { ["provider"] = "anthropic", ["method"] = "browser" });
                Equal(409, anthropic.Status, "no Anthropic sign-in");
                Equal(400, (await Call(HttpMethod.Post, "/api/credentials/signin", new JsonObject { ["provider"] = "openai-codex", ["method"] = "telepathy" })).Status, "an unknown method");

                var bad = await Call(HttpMethod.Post, "/api/credentials/accounts", new JsonObject { ["provider"] = "openai", ["apiKey"] = key + " " + key });
                Equal(400, bad.Status, "a malformed key");
                Check(bad.Body!["error"]!.GetValue<string>().Contains("spaces", StringComparison.Ordinal), "says why");
                Equal(409, (await Call(HttpMethod.Post, "/api/credentials/accounts", new JsonObject { ["provider"] = "openai-codex", ["apiKey"] = key })).Status,
                    "a sign-in-only provider takes no key");
                Equal(404, (await Call(HttpMethod.Get, "/api/credentials/accounts/acc-none")).Status, "an unknown account");
                Equal(409, (await Call(HttpMethod.Post, $"/api/credentials/accounts/{envId}/remove")).Status, "a read-only account");
                Equal(404, (await Call(HttpMethod.Get, "/api/credentials/signin/sgn-none")).Status, "an unknown sign-in");

                // Every page of the shell, with the accounts in place, holds no secret.
                var pages = new StringBuilder();
                foreach (var href in ShellNav.Primary.Append(ShellNav.SettingsLink).Concat(ShellNav.SettingsGroups.SelectMany(group => group.Items))
                    .Select(item => item.Href).Concat(CredentialPages).Distinct())
                {
                    var page = await app.Get(href);
                    Check(page.Status < 500, $"{href}: {page.Status}");
                    pages.AppendLine(page.Text);
                }

                var removed = await Call(HttpMethod.Post, $"/api/credentials/accounts/{id}/remove");
                Equal(200, removed.Status, "remove");
                Equal(envId, removed.Body!["active"]!["id"]!.GetValue<string>(), "the variable is still active");
                Equal(1, (await Call(HttpMethod.Get, "/api/credentials/accounts")).Body!["accounts"]!.AsArray().Count, "the stored account is gone");

                NoSecret(answers.ToString(), "an API answer", key, key2, envKey);
                NoSecret(pages.ToString(), "a rendered page", key, key2, envKey);
                await app.StopAsync();
                NoSecret(app.Log, "the app's log", key, key2, envKey);
                foreach (var secret in new[] { key, key2, envKey })
                    Check(FilesHolding(Path.Join(root, "data"), secret).Count == 0, "a planted secret is on disk: " + string.Join(", ", FilesHolding(Path.Join(root, "data"), secret)));
                Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("credentials.app-guard-refuses-cross-origin-and-non-json-writes", async () =>
        {
            await using var app = await WebAgentApp.StartAsync("cred-guard");
            var key = Planted("sk-");
            var body = new JsonObject { ["provider"] = "openai", ["apiKey"] = key };
            Equal(403, (await app.SendAsync(HttpMethod.Post, "/api/credentials/accounts", body, origin: "http://evil.example")).Status, "a cross-origin write");
            Equal(415, (await app.SendAsync(HttpMethod.Post, "/api/credentials/accounts", origin: app.Origin, contentType: "text/plain",
                text: "provider=openai&apiKey=" + key)).Status, "a form post");
            Equal(415, (await app.SendAsync(HttpMethod.Post, "/api/credentials/accounts", origin: app.Origin, contentType: "application/x-www-form-urlencoded",
                text: "provider=openai&apiKey=" + key)).Status, "an HTML form's encoding");
            Equal(421, (await app.SendAsync(HttpMethod.Get, "/api/credentials/accounts", host: "evil.example")).Status, "a foreign host (DNS rebinding)");
            Equal(400, (await app.SendAsync(HttpMethod.Post, "/api/credentials/accounts", origin: app.Origin, text: "{ not json")).Status, "malformed JSON");
            Equal(400, (await app.SendAsync(HttpMethod.Post, "/api/credentials/accounts", origin: app.Origin, text: "null")).Status, "no body");
            var listed = await app.Get("/api/credentials/accounts");
            Equal(0, listed.Body!["accounts"]!.AsArray().Count, "nothing was stored");
            NoSecret(app.Log, "the log", key);
        }),

        Case("credentials.app-signin-endpoints-drive-a-device-code-flow", async () =>
        {
            var root = Temp("cred-inproc");
            var logs = new ConcurrentQueue<string>();
            var http = new FakeOAuth();
            var access = CodexJwt("acct-http");
            var refresh = Planted("rt-");
            CodexDevice(http);
            CodexTokens(http, "dac", access, refresh, verifier: "dcv");
            var port = FreePort();
            var options = new WebAgentOptions { DataDirectory = Path.Join(root, "data") };
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root, EnvironmentName = "Production" });
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new ListLoggerProvider(logs));
            builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
            builder.Services.AddSingleton(new CredentialsOptions
            {
                Environment = _ => null, FileExists = _ => false, Home = root, CreateHttp = () => new HttpMessageInvoker(http, disposeHandler: false),
                OpenAIAuthBaseUrl = FakeOAuth.AuthBase, CodexCallbackPort = FreePort(),
            });
            builder.Services.AddWebAgentCore(options);
            builder.Services.AddCredentialsArea(options);
            var web = builder.Build();
            web.UseWebAgentCore();
            web.MapCredentialsArea();
            await web.StartAsync();
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
                var answers = new StringBuilder();
                async Task<JsonNode> Send(HttpMethod method, string path, JsonNode? body = null, int status = 200)
                {
                    using var request = new HttpRequestMessage(method, path);
                    if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                    using var response = await client.SendAsync(request);
                    var text = await response.Content.ReadAsStringAsync();
                    answers.AppendLine(text);
                    Equal(status, (int)response.StatusCode, $"{method} {path}: {text}");
                    return JsonNode.Parse(text)!;
                }
                var started = await Send(HttpMethod.Post, "/api/credentials/signin", new JsonObject { ["provider"] = "openai-codex", ["method"] = "deviceCode", ["label"] = "Laptop" });
                var flowId = started["flowId"]!.GetValue<string>();
                Equal("deviceCode", started["method"]!.GetValue<string>(), "the method by name");
                Equal("ABCD-EFGH", started["userCode"]!.GetValue<string>(), "the user code");
                Equal(FakeOAuth.AuthBase + "/codex/device", started["verificationUrl"]!.GetValue<string>(), "the verification page");
                JsonNode status = started;
                await WaitUntilAsync(async () => (status = await Send(HttpMethod.Get, "/api/credentials/signin/" + flowId))["finished"]!.GetValue<bool>(),
                    "the flow finishes", TimeSpan.FromSeconds(30));
                Equal("succeeded", status["state"]!.GetValue<string>(), "succeeded: " + status["message"]);
                Equal("oauth", status["account"]!["kind"]!.GetValue<string>(), "a sign-in account");
                Equal("Laptop", status["account"]!["label"]!.GetValue<string>(), "named");
                Equal(409, (int)(await client.PostAsync($"/api/credentials/signin/{flowId}/code",
                    new StringContent("{\"code\":\"x\"}", Encoding.UTF8, "application/json"))).StatusCode, "a finished flow takes no code");
                var accounts = await Send(HttpMethod.Get, "/api/credentials/accounts?provider=openai-codex");
                Equal(1, accounts["accounts"]!.AsArray().Count, "listed");
                Check(accounts["accounts"]![0]!["isActive"]!.GetValue<bool>(), "active");

                var codex = (await Send(HttpMethod.Post, "/api/credentials/signin", new JsonObject { ["provider"] = "openai-codex", ["method"] = "browser" }));
                Check(codex["authorizeUrl"]!.GetValue<string>().StartsWith(FakeOAuth.AuthBase, StringComparison.Ordinal), "a browser flow answers its authorize URL");
                var cancelId = codex["flowId"]!.GetValue<string>();
                await Send(HttpMethod.Post, $"/api/credentials/signin/{cancelId}/cancel");
                Equal("cancelled", await WaitForState(cancelId), "cancelled over HTTP");

                NoSecret(answers.ToString(), "an API answer", access, refresh);
                NoSecret(string.Join("\n", logs), "the log", access, refresh);

                async Task<string> WaitForState(string id)
                {
                    JsonNode current = codex;
                    await WaitUntilAsync(async () => (current = await Send(HttpMethod.Get, "/api/credentials/signin/" + id))["finished"]!.GetValue<bool>(), "cancelled");
                    return current["state"]!.GetValue<string>();
                }
            }
            finally
            {
                await web.StopAsync();
                await web.DisposeAsync();
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),
    ];
}
