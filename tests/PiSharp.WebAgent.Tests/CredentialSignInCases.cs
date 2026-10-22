using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.WebAgent.Credentials;

// Decision 0011 (run W2, WU5): the sign-in flows against fake OAuth endpoints (FakeOAuth: an HTTP handler that answers OpenAI's and
// GitHub's sign-in endpoints from memory, so nothing reaches the network) and the real loopback callback on a free port: the ChatGPT
// browser sign-in through the callback, and by pasting the redirect URL when the port is taken (a wrong state fails); OpenAI's device
// code; GitHub Copilot's device code; signing in again updates the same account and refuses another identity; Anthropic, unknown
// providers and offline mode are refused; cancelling, replacing and timing out a flow store nothing. And the connection tests: GET only,
// a rejected key scrubbed from the message, unsupported and offline answers without a request, a sign-in checked without a request
// unless a refresh is due (a failed refresh marks the account expired).
internal static partial class Program
{
    /// <summary>A recorded request to <see cref="FakeOAuth"/>.</summary>
    internal sealed record FakeRequest(string Method, Uri Url, IReadOnlyDictionary<string, string> Headers, string? Body)
    {
        public string Key => Method + " " + Url.GetLeftPart(UriPartial.Path);

        public string? Form(string name) => Body is null ? null : OAuthFlows.Query(Body, name);

        public string? Header(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out var value) ? value : null;
    }

    /// <summary>OpenAI's and GitHub's sign-in endpoints and the providers' models lists, answered from memory. An unrouted request is a 404
    /// (and recorded), never a network call.</summary>
    internal sealed class FakeOAuth : HttpMessageHandler
    {
        public const string AuthBase = "https://auth.test";

        private readonly ConcurrentDictionary<string, Func<FakeRequest, HttpResponseMessage>> routes = new(StringComparer.Ordinal);

        public ConcurrentQueue<FakeRequest> Requests { get; } = new();

        /// <summary>Thrown for every request when set (an unreachable server).</summary>
        public Exception? Failure { get; set; }

        public void On(string key, Func<FakeRequest, HttpResponseMessage> answer) => routes[key] = answer;

        public void On(string key, int status, JsonNode body) => On(key, _ => Json(status, body));

        public static HttpResponseMessage Json(int status, JsonNode body) =>
            new((HttpStatusCode)status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var header in request.Headers) headers[header.Key.ToLowerInvariant()] = string.Join(", ", header.Value);
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var recorded = new FakeRequest(request.Method.Method, request.RequestUri!, headers, body);
            Requests.Enqueue(recorded);
            if (Failure is { } failure) throw failure;
            return routes.TryGetValue(recorded.Key, out var answer) ? answer(recorded) : Json(404, new JsonObject { ["error"] = "no fake route for " + recorded.Key });
        }
    }

    /// <summary>An unsigned JWT like ChatGPT's access token: the account id and plan under the auth claim, the email under the profile claim.</summary>
    internal static string CodexJwt(string accountId, string plan = "plus", string email = "me@example.test")
    {
        static string Part(string text) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(text));
        var payload = new JsonObject
        {
            ["https://api.openai.com/auth"] = new JsonObject { ["chatgpt_account_id"] = accountId, ["chatgpt_plan_type"] = plan },
            ["https://api.openai.com/profile"] = new JsonObject { ["email"] = email },
            ["nonce"] = Planted("n"),
        };
        return Part("{\"alg\":\"none\",\"typ\":\"JWT\"}") + "." + Part(payload.ToJsonString()) + "." + Part(Planted("sig"));
    }

    /// <summary>The ChatGPT token endpoint: an authorization code for <paramref name="code"/> answers <paramref name="access"/> and
    /// <paramref name="refresh"/>; anything else is a 400.</summary>
    internal static void CodexTokens(FakeOAuth http, string code, string access, string refresh, string? verifier = null) =>
        http.On("POST " + FakeOAuth.AuthBase + "/oauth/token", request =>
            request.Form("grant_type") == "authorization_code" && request.Form("code") == code && (verifier is null || request.Form("code_verifier") == verifier) &&
            !string.IsNullOrEmpty(request.Form("code_verifier"))
                ? FakeOAuth.Json(200, new JsonObject { ["access_token"] = access, ["refresh_token"] = refresh, ["expires_in"] = 3600, ["id_token"] = "x.y.z" })
                : FakeOAuth.Json(400, new JsonObject { ["error"] = "invalid_grant", ["error_description"] = "bad code " + request.Form("code") }));

    /// <summary>OpenAI's device code endpoints: pending once, then the authorization code <c>dac</c> with verifier <c>dcv</c>.</summary>
    internal static void CodexDevice(FakeOAuth http)
    {
        var polls = 0;
        http.On("POST " + FakeOAuth.AuthBase + "/api/accounts/deviceauth/usercode", 200,
            new JsonObject { ["device_auth_id"] = "dev-1", ["user_code"] = "ABCD-EFGH", ["interval"] = "1" });
        http.On("POST " + FakeOAuth.AuthBase + "/api/accounts/deviceauth/token", _ => Interlocked.Increment(ref polls) == 1
            ? FakeOAuth.Json(403, new JsonObject { ["error"] = new JsonObject { ["code"] = "deviceauth_authorization_pending" } })
            : FakeOAuth.Json(200, new JsonObject { ["authorization_code"] = "dac", ["code_verifier"] = "dcv" }));
    }

    /// <summary>GitHub's device code, the Copilot token exchange and the Copilot models list. <paramref name="pending"/> polls answer
    /// authorization_pending first (-1: forever).</summary>
    internal static void Copilot(FakeOAuth http, string githubToken, string copilotToken, int pending = 0)
    {
        var polls = 0;
        http.On("POST https://github.com/login/device/code", 200, new JsonObject
        {
            ["device_code"] = "gh-device", ["user_code"] = "WXYZ-1234", ["verification_uri"] = "https://github.com/login/device", ["interval"] = 1, ["expires_in"] = 900,
        });
        http.On("POST https://github.com/login/oauth/access_token", request =>
            pending < 0 || Interlocked.Increment(ref polls) <= pending || request.Form("device_code") != "gh-device"
                ? FakeOAuth.Json(200, new JsonObject { ["error"] = "authorization_pending" })
                : FakeOAuth.Json(200, new JsonObject { ["access_token"] = githubToken, ["token_type"] = "bearer" }));
        http.On("GET https://api.github.com/copilot_internal/v2/token", request => request.Header("authorization") == "Bearer " + githubToken
            ? FakeOAuth.Json(200, new JsonObject { ["token"] = copilotToken, ["expires_at"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() })
            : FakeOAuth.Json(401, new JsonObject { ["message"] = "Bad credentials" }));
        http.On("GET https://api.individual.githubcopilot.com/models", request => request.Header("authorization") == "Bearer " + copilotToken
            ? FakeOAuth.Json(200, new JsonObject
            {
                ["data"] = new JsonArray(
                    new JsonObject { ["id"] = "gpt-test", ["model_picker_enabled"] = true, ["policy"] = new JsonObject { ["state"] = "enabled" } },
                    new JsonObject { ["id"] = "claude-test", ["model_picker_enabled"] = true, ["policy"] = new JsonObject { ["state"] = "enabled" } },
                    new JsonObject { ["id"] = "hidden", ["model_picker_enabled"] = false }),
            })
            : FakeOAuth.Json(401, new JsonObject { ["message"] = "unauthorized" }));
    }

    internal static string CopilotToken() => "tid=" + Planted("t") + ";exp=1;proxy-ep=proxy.individual.githubcopilot.com;sig=" + Planted("s");

    internal static async Task<SignInStatus> FinishedAsync(ISignInFlows flows, string flowId, TimeSpan? limit = null)
    {
        SignInStatus? status = null;
        await WaitUntilAsync(() => Task.FromResult((status = flows.Get(flowId)) is { Finished: true }), "sign-in " + flowId + " finishes", limit ?? TimeSpan.FromSeconds(30));
        return status!;
    }

    private static string StatusJson(SignInStatus status) => JsonSerializer.Serialize(status, CredentialVault.JsonOptions);

    internal static IEnumerable<(string, Func<Task>)> CredentialSignInCases() =>
    [
        Case("credentials.signin-codex-browser-callback-stores-the-account", async () =>
        {
            var port = FreePort();
            await using var host = new VaultHost("cred-codex-browser", setup => setup.CallbackPort = port);
            var access = CodexJwt("acct-1");
            var refresh = Planted("rt-");
            CodexTokens(host.Http, "good-code", access, refresh);
            Equal(3, host.SignIn.Methods.Count(method => method.Available), "three methods online");

            var started = await host.SignIn.StartAsync(new SignInRequest("openai-codex", SignInMethod.Browser));
            Equal(SignInState.Waiting, started.State, "waiting for the browser: " + started.Message);
            Check(started.CallbackListening && started.AcceptsCode, "the callback listens, and a paste is accepted too");
            Check(started.AuthorizeUrl!.StartsWith(FakeOAuth.AuthBase + "/oauth/authorize?", StringComparison.Ordinal), "the authorize URL: " + started.AuthorizeUrl);
            var query = new Uri(started.AuthorizeUrl).Query;
            Equal("S256", OAuthFlows.Query(query, "code_challenge_method"), "PKCE");
            Equal(OpenAICodexOAuth.RedirectUri, OAuthFlows.Query(query, "redirect_uri"), "the registered redirect");
            Equal(OpenAICodexOAuth.RedirectUri, started.CallbackUrl, "the page knows the redirect URL");
            Check(OAuthFlows.Query(query, "code_verifier") is null, "the verifier stays on the server");
            var state = OAuthFlows.Query(query, "state")!;

            using var browser = new HttpClient();
            var callback = await browser.GetAsync($"http://127.0.0.1:{port}/auth/callback?code=good-code&state={state}");
            Equal(HttpStatusCode.OK, callback.StatusCode, "the callback page");
            Check((await callback.Content.ReadAsStringAsync()).Contains("Authentication successful", StringComparison.Ordinal), "the success page");
            var done = await FinishedAsync(host.SignIn, started.FlowId);
            Equal(SignInState.Succeeded, done.State, "succeeded: " + done.Message);
            Check(done.BecameActive && !done.Reauthenticated, "a first sign-in becomes active");
            var account = done.Account!;
            Equal(AccountKind.OAuth, account.Kind, "a sign-in");
            Equal("acct-1", account.Identity, "the ChatGPT account id is the identity");
            Equal("ChatGPT Plus", account.Label, "the default label");
            Equal("plus", account.Details["plan"], "plan");
            Equal("me@example.test", account.Details["email"], "email");
            Equal("", account.Hint, "no hint for a sign-in");
            Check(account.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(50), "the expiry");
            var credential = (await host.Secrets.ReadAsync(account.Id))!.OAuth!;
            Equal(access, credential.Access, "the access token is stored");
            Equal(refresh, credential.Refresh, "the refresh token is stored");
            Equal("acct-1", credential.ProviderData["accountId"], "with the flow's fields");
            var exchange = host.Http.Requests.Single(request => request.Key == "POST " + FakeOAuth.AuthBase + "/oauth/token");
            Equal(OpenAICodexOAuth.RedirectUri, exchange.Form("redirect_uri"), "the exchange names the redirect");

            NoSecret(StatusJson(done) + StatusJson(started), "the flow's status", access, refresh, "good-code");
            NoSecret(host.LogText, "the log", access, refresh);
            Check(FilesHolding(host.Options.DataDirectory, access).Count == 0 && FilesHolding(host.Options.DataDirectory, refresh).Count == 0, "no token on disk in plain text");
            await WaitUntilAsync(async () =>
            {
                try { using var probe = new TcpClient(); await probe.ConnectAsync(IPAddress.Loopback, port); return false; }
                catch (SocketException) { return true; }
            }, "the callback port is released");
        }),

        Case("credentials.signin-codex-paste-when-the-port-is-taken", async () =>
        {
            var port = FreePort();
            var squatter = new TcpListener(IPAddress.Loopback, port);
            squatter.Start();
            try
            {
                await using var host = new VaultHost("cred-codex-paste", setup => setup.CallbackPort = port);
                var access = CodexJwt("acct-paste");
                CodexTokens(host.Http, "pasted-code", access, Planted("rt-"));
                var first = await host.SignIn.StartAsync(new SignInRequest("openai-codex", SignInMethod.Browser));
                Check(!first.CallbackListening && first.AcceptsCode, "the port is taken, so the page asks for a paste");
                Check(first.Message!.Contains("in use", StringComparison.Ordinal), "and says why: " + first.Message);
                var wrong = await host.SignIn.SubmitCodeAsync(first.FlowId, OpenAICodexOAuth.RedirectUri + "?code=pasted-code&state=not-the-state");
                Equal(SignInState.Failed, wrong.State, "a wrong state fails");
                Check(wrong.Message!.Contains("State mismatch", StringComparison.Ordinal), "and says so: " + wrong.Message);
                Equal(0, host.Accounts.List("openai-codex").Count, "nothing stored");
                Check(!host.Http.Requests.Any(request => request.Url.AbsolutePath == "/oauth/token"), "no code was exchanged");
                await ThrowsAsync<InvalidOperationException>(() => host.SignIn.SubmitCodeAsync(first.FlowId, "again"), "a finished flow takes no code");

                var second = await host.SignIn.StartAsync(new SignInRequest("openai-codex", SignInMethod.Browser) { Label = "Home" });
                var state = OAuthFlows.Query(new Uri(second.AuthorizeUrl!).Query, "state");
                await ThrowsAsync<ArgumentException>(() => host.SignIn.SubmitCodeAsync(second.FlowId, "   "), "an empty paste");
                await ThrowsAsync<KeyNotFoundException>(() => host.SignIn.SubmitCodeAsync("sgn-none", "x"), "an unknown flow");
                var pasted = await host.SignIn.SubmitCodeAsync(second.FlowId, $"{OpenAICodexOAuth.RedirectUri}?code=pasted-code&state={state}");
                if (!pasted.Finished) pasted = await FinishedAsync(host.SignIn, second.FlowId);
                Equal(SignInState.Succeeded, pasted.State, "the pasted redirect URL signs in: " + pasted.Message);
                Equal("Home", pasted.Account!.Label, "the chosen name");
                NoSecret(StatusJson(pasted), "the status", access, "pasted-code");
            }
            finally { squatter.Stop(); }
        }),

        Case("credentials.signin-codex-device-code", async () =>
        {
            await using var host = new VaultHost("cred-codex-device");
            var access = CodexJwt("acct-device", plan: "pro");
            CodexDevice(host.Http);
            CodexTokens(host.Http, "dac", access, Planted("rt-"), verifier: "dcv");
            var started = await host.SignIn.StartAsync(new SignInRequest("openai-codex", SignInMethod.DeviceCode));
            Check(started.State is SignInState.Waiting or SignInState.Working or SignInState.Succeeded, "started: " + started.State + " " + started.Message);
            Equal("ABCD-EFGH", started.UserCode, "the user code");
            Equal(FakeOAuth.AuthBase + "/codex/device", started.VerificationUrl, "the verification page");
            Equal(1.0, started.PollSeconds, "the poll interval");
            Check(started.AuthorizeUrl is null && !started.CallbackListening, "no browser callback");
            if (!started.Finished)
                await ThrowsAsync<InvalidOperationException>(() => host.SignIn.SubmitCodeAsync(started.FlowId, "code"), "a device flow takes no paste");
            var done = await FinishedAsync(host.SignIn, started.FlowId);
            Equal(SignInState.Succeeded, done.State, "succeeded: " + done.Message);
            Equal("ChatGPT Pro", done.Account!.Label, "the plan names it");
            Equal(FakeOAuth.AuthBase + "/deviceauth/callback", host.Http.Requests.Single(request => request.Url.AbsolutePath == "/oauth/token").Form("redirect_uri"),
                "the device code's redirect");
            Equal(access, (await host.Secrets.ReadAsync(done.Account.Id))!.OAuth!.Access, "stored");
        }),

        Case("credentials.signin-copilot-device-code-and-re-sign-in-updates-the-same-account", async () =>
        {
            await using var host = new VaultHost("cred-copilot");
            var githubToken = Planted("gho_");
            var copilotToken = CopilotToken();
            Copilot(host.Http, githubToken, copilotToken, pending: 1);
            await ThrowsAsync<ArgumentException>(() => host.SignIn.StartAsync(new SignInRequest("github-copilot", SignInMethod.Browser)), "Copilot has no browser sign-in");
            await ThrowsAsync<ArgumentException>(() => host.SignIn.StartAsync(new SignInRequest("github-copilot", SignInMethod.DeviceCode) { Domain = "not a domain!!" }), "a bad domain");
            var started = await host.SignIn.StartAsync(new SignInRequest("github-copilot", SignInMethod.DeviceCode));
            Equal("WXYZ-1234", started.UserCode, "the user code");
            Equal("https://github.com/login/device", started.VerificationUrl, "GitHub's page");
            Check(started.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(901), "the code's expiry");
            var done = await FinishedAsync(host.SignIn, started.FlowId);
            Equal(SignInState.Succeeded, done.State, "succeeded: " + done.Message);
            var account = done.Account!;
            Equal("GitHub Copilot", account.Label, "label");
            Equal("github.com", account.Details["host"], "host");
            Equal("2", account.Details["models"], "the models the plan offers");
            Equal(null, account.Identity, "GitHub Copilot reports no identity");
            var stored = (await host.Secrets.ReadAsync(account.Id))!.OAuth!;
            Equal(copilotToken, stored.Access, "the Copilot token");
            Equal(githubToken, stored.Refresh, "the GitHub token is the refresh token");
            Equal("2", GitHubCopilotOAuth.AvailableModels(stored)!.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), "available models are kept");
            Check(host.Http.Requests.Where(request => request.Url.Host.EndsWith("githubcopilot.com", StringComparison.Ordinal)).All(request => request.Method == "GET"),
                "no completion, no policy change: the catalog has none of these models");

            // The connection test lists the models with the stored Copilot token.
            var before = host.Http.Requests.Count;
            var test = await host.Tester.TestAccountAsync(account.Id);
            Check(test.Ok && test.Supported, "the test passes: " + test.Message);
            Equal(3, test.ModelCount, "every model the list returns is counted");
            var sent = host.Http.Requests.Skip(before).ToList();
            Check(sent.Count == 1 && sent[0].Method == "GET" && sent[0].Url.AbsolutePath == "/models", "one GET of the models list: " + string.Join(", ", sent.Select(r => r.Key)));
            Equal("vscode-chat", sent[0].Header("Copilot-Integration-Id"), "Copilot's headers");
            Equal(true, host.Accounts.Get(account.Id)!.LastTest?.Ok, "the result is kept on the account");

            // Signing in again replaces the credential of the same account.
            await host.Accounts.MarkExpiredAsync(account.Id, "revoked");
            var githubToken2 = Planted("gho_");
            var copilotToken2 = CopilotToken();
            Copilot(host.Http, githubToken2, copilotToken2);
            var again = await host.SignIn.StartAsync(new SignInRequest("github-copilot", SignInMethod.DeviceCode) { AccountId = account.Id });
            var redone = await FinishedAsync(host.SignIn, again.FlowId);
            Equal(SignInState.Succeeded, redone.State, "succeeded again: " + redone.Message);
            Check(redone.Reauthenticated, "reported as signing in again");
            Equal(account.Id, redone.Account!.Id, "the same account, so its chats keep working");
            Equal(1, host.Accounts.List("github-copilot").Count, "no second account");
            Equal(AccountStatus.Connected, redone.Account.Status, "connected again");
            Equal(copilotToken2, (await host.Secrets.ReadAsync(account.Id))!.OAuth!.Access, "the new token");
            NoSecret(StatusJson(redone) + StatusJson(done) + host.LogText, "statuses and the log", githubToken, githubToken2, copilotToken, copilotToken2);
            Check(FilesHolding(host.Options.DataDirectory, githubToken2).Count == 0, "no plain token on disk");
        }),

        Case("credentials.signin-again-keeps-the-account-and-refuses-another-identity", async () =>
        {
            await using var host = new VaultHost("cred-identity");
            async Task<SignInStatus> SignIn(string identity, string? accountId = null)
            {
                CodexDevice(host.Http);
                CodexTokens(host.Http, "dac", CodexJwt(identity), Planted("rt-"));
                var started = await host.SignIn.StartAsync(new SignInRequest("openai-codex", SignInMethod.DeviceCode) { AccountId = accountId });
                return await FinishedAsync(host.SignIn, started.FlowId);
            }
            var first = await SignIn("acct-1");
            var id = first.Account!.Id;
            var firstAccess = (await host.Secrets.ReadAsync(id))!.OAuth!.Access;
            var same = await SignIn("acct-1");
            Check(same.State == SignInState.Succeeded && same.Reauthenticated, "the same identity reconnects: " + same.Message);
            Equal(id, same.Account!.Id, "the same account");
            var sameAccess = (await host.Secrets.ReadAsync(id))!.OAuth!.Access;
            Check(sameAccess != firstAccess, "with the new tokens");

            var other = await SignIn("acct-2", accountId: id);
            Equal(SignInState.Failed, other.State, "another identity is refused when signing an account in again");
            Check(other.Message!.Contains("different account", StringComparison.Ordinal), "and says so: " + other.Message);
            Equal(sameAccess, (await host.Secrets.ReadAsync(id))!.OAuth!.Access, "nothing was changed");
            Equal(1, host.Accounts.List("openai-codex").Count, "no account added");

            var added = await SignIn("acct-2");
            Check(added.State == SignInState.Succeeded && !added.Reauthenticated && !added.BecameActive, "a new identity is a new account, not active");
            Equal(2, host.Accounts.List("openai-codex").Count, "two sign-ins");
            Equal(id, host.Accounts.Active("openai-codex")!.Id, "the active account did not move");

            var keyAccount = (await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("openai", Planted("sk-")))).Account.Id;
            await ThrowsAsync<InvalidOperationException>(() => host.SignIn.StartAsync(new SignInRequest("openai-codex", SignInMethod.DeviceCode) { AccountId = keyAccount }),
                "an API key account is not signed in");
            await ThrowsAsync<KeyNotFoundException>(() => host.SignIn.StartAsync(new SignInRequest("openai-codex", SignInMethod.DeviceCode) { AccountId = "acc-gone" }),
                "an unknown account");
        }),

        Case("credentials.signin-refuses-anthropic-unknown-providers-and-offline", async () =>
        {
            await using (var host = new VaultHost("cred-refuse"))
            {
                var anthropic = await ThrowsAsync<InvalidOperationException>(() => host.SignIn.StartAsync(new SignInRequest("anthropic", SignInMethod.Browser)), "Anthropic");
                Check(anthropic.Message.Contains("API key", StringComparison.Ordinal), "Anthropic is API key or Bedrock only: " + anthropic.Message);
                await ThrowsAsync<ArgumentException>(() => host.SignIn.StartAsync(new SignInRequest("openai", SignInMethod.Browser)), "openai signs in nowhere here");
                await ThrowsAsync<ArgumentException>(() => host.SignIn.StartAsync(new SignInRequest("faux", SignInMethod.DeviceCode)), "faux");
                await ThrowsAsync<ArgumentException>(() => host.SignIn.StartAsync(new SignInRequest("openai-codex", (SignInMethod)7)), "an unknown method");
                await ThrowsAsync<ArgumentException>(() => host.SignIn.StartAsync(new SignInRequest("openai-codex", SignInMethod.DeviceCode) { Domain = "ghe.example" }), "a domain for Codex");
                Check(!host.SignIn.Methods.Any(method => method.Provider == "anthropic"), "no Anthropic sign-in is offered");
                Equal(0, host.Http.Requests.Count, "nothing was sent");
                Equal(null, host.SignIn.Get("sgn-none"), "an unknown flow");
                Equal(null, host.SignIn.Cancel("sgn-none"), "cancelling an unknown flow");
            }
            await using (var offline = new VaultHost("cred-offline", setup => setup.Offline = true))
            {
                var error = await ThrowsAsync<InvalidOperationException>(() => offline.SignIn.StartAsync(new SignInRequest("openai-codex", SignInMethod.Browser)), "offline");
                Equal(CredentialsOptions.OfflineMessage, error.Message, "the offline message");
                Check(offline.SignIn.Methods.All(method => !method.Available), "every method is unavailable offline");
                var added = await offline.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("openai", Planted("sk-")));
                var test = await offline.Tester.TestAccountAsync(added.Account.Id);
                Check(!test.Supported && !test.Ok && test.Message == CredentialsOptions.OfflineMessage, "no test offline");
                var keyTest = await offline.Tester.TestKeyAsync("openai", Planted("sk-"));
                Check(!keyTest.Supported, "no key test offline");
                Equal(0, offline.Http.Requests.Count, "nothing was sent offline");
            }
        }),

        Case("credentials.signin-cancel-replace-and-timeout-store-nothing", async () =>
        {
            var port = FreePort();
            await using (var host = new VaultHost("cred-cancel", setup => setup.CallbackPort = port))
            {
                Copilot(host.Http, Planted("gho_"), CopilotToken(), pending: -1);
                var started = await host.SignIn.StartAsync(new SignInRequest("github-copilot", SignInMethod.DeviceCode));
                Equal(SignInState.Waiting, started.State, "waiting for the code");
                host.SignIn.Cancel(started.FlowId);
                var cancelled = await FinishedAsync(host.SignIn, started.FlowId);
                Equal(SignInState.Cancelled, cancelled.State, "cancelled");
                Check(cancelled.Message!.Contains("nothing was stored", StringComparison.Ordinal), cancelled.Message);
                Equal(0, host.Accounts.List("github-copilot").Count, "nothing stored");

                var a = await host.SignIn.StartAsync(new SignInRequest("openai-codex", SignInMethod.Browser));
                Check(a.CallbackListening, "the first flow listens");
                var b = await host.SignIn.StartAsync(new SignInRequest("openai-codex", SignInMethod.Browser));
                var replaced = host.SignIn.Get(a.FlowId)!;
                Equal(SignInState.Cancelled, replaced.State, "a newer sign-in of the provider replaces the older");
                Check(replaced.Message!.Contains("Replaced", StringComparison.Ordinal), replaced.Message);
                Check(b.CallbackListening && b.State == SignInState.Waiting, "the newer one gets the port");
                host.SignIn.Cancel(b.FlowId);
                Equal(SignInState.Cancelled, (await FinishedAsync(host.SignIn, b.FlowId)).State, "cancelled too");
            }
            await using (var host = new VaultHost("cred-timeout", setup => setup.SignInTimeout = TimeSpan.FromSeconds(2)))
            {
                Copilot(host.Http, Planted("gho_"), CopilotToken(), pending: -1);
                var started = await host.SignIn.StartAsync(new SignInRequest("github-copilot", SignInMethod.DeviceCode));
                var timedOut = await FinishedAsync(host.SignIn, started.FlowId, TimeSpan.FromSeconds(20));
                Equal(SignInState.Cancelled, timedOut.State, "given up");
                Check(timedOut.Message!.Contains("timed out", StringComparison.Ordinal), timedOut.Message);
                Equal(0, host.Accounts.List().Count, "nothing stored");
            }
        }),

        Case("credentials.connection-test-lists-models-with-a-get-only", async () =>
        {
            await using var host = new VaultHost("cred-test-get", setup => setup.Environment["ANTHROPIC_AUTH_TOKEN"] = "sk-ant-auth-token-0000000001");
            var models = new JsonObject { ["data"] = new JsonArray(new JsonObject { ["id"] = "a" }, new JsonObject { ["id"] = "b" }, new JsonObject { ["id"] = "c" }) };
            host.Http.On("GET https://api.openai.com/v1/models", 200, models);
            host.Http.On("GET https://proxy.example.test/v1/models", 200, models);
            host.Http.On("GET https://api.anthropic.com/v1/models", 200, new JsonObject { ["data"] = new JsonArray(new JsonObject { ["id"] = "claude" }) });
            host.Http.On("GET https://generativelanguage.googleapis.com/v1beta/models", 200, new JsonObject { ["models"] = new JsonArray(new JsonObject(), new JsonObject()) });
            var key = Planted("sk-proj-");

            var result = await host.Tester.TestKeyAsync("openai", key);
            Check(result.Ok && result.Supported, "connected: " + result.Message);
            Equal(3, result.ModelCount, "models");
            Equal(200, result.StatusCode, "status");
            Equal("GET https://api.openai.com/v1/models", result.Endpoint, "the endpoint");
            var sent = host.Http.Requests.Single();
            Check(sent.Method == "GET" && sent.Body is null, "a GET without a body");
            Equal("Bearer " + key, sent.Header("Authorization"), "the key as a bearer token");

            var added = await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("openai", key) { Settings = new Dictionary<string, string> { ["baseUrl"] = "https://proxy.example.test/v1/" } });
            var stored = await host.Tester.TestAccountAsync(added.Account.Id);
            Equal("GET https://proxy.example.test/v1/models", stored.Endpoint, "a baseUrl setting is honoured");
            var last = host.Accounts.Get(added.Account.Id)!.LastTest!;
            Check(last.Ok && last.ModelCount == 3, "kept on the account");

            var anthropicKey = Planted("sk-ant-api03-");
            Check((await host.Tester.TestKeyAsync("anthropic", anthropicKey)).Ok, "anthropic");
            var anthropic = host.Http.Requests.Last();
            Equal(anthropicKey, anthropic.Header("x-api-key"), "x-api-key");
            Equal(ConnectionTester.AnthropicVersion, anthropic.Header("anthropic-version"), "anthropic-version");
            Equal(null, anthropic.Header("Authorization"), "no bearer token");
            var bearer = await host.Tester.TestAccountAsync(CredentialVault.EnvironmentId("anthropic", "ANTHROPIC_AUTH_TOKEN"));
            Check(bearer.Ok, "an ANTHROPIC_AUTH_TOKEN account: " + bearer.Message);
            Equal("Bearer sk-ant-auth-token-0000000001", host.Http.Requests.Last().Header("Authorization"), "is a bearer token");
            var google = await host.Tester.TestKeyAsync("google", "AIza" + Planted("g"));
            Check(google.Ok && google.ModelCount == 2, "google");
            Check(host.Http.Requests.Last().Header("x-goog-api-key")!.StartsWith("AIza", StringComparison.Ordinal), "x-goog-api-key");
            Check(host.Http.Requests.All(request => request.Method == "GET" && request.Body is null && request.Url.AbsolutePath.EndsWith("/models", StringComparison.Ordinal)),
                "every test request is a GET of a models list: " + string.Join(", ", host.Http.Requests.Select(request => request.Key)));
            NoSecret(ToJson(host.Accounts.List()) + host.LogText, "accounts and log", key, anthropicKey);
        }),

        Case("credentials.connection-test-scrubs-a-rejected-key", async () =>
        {
            await using var host = new VaultHost("cred-test-401");
            var key = Planted("sk-proj-");
            host.Http.On("GET https://api.openai.com/v1/models", 401, new JsonObject
            {
                ["error"] = new JsonObject { ["message"] = $"Incorrect API key provided: {key}. You can find your API key at https://platform.openai.com/account/api-keys.", ["code"] = "invalid_api_key" },
            });
            var added = await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("openai", key));
            var result = await host.Tester.TestAccountAsync(added.Account.Id);
            Check(!result.Ok && result.Supported, "rejected");
            Equal(401, result.StatusCode, "401");
            Check(result.Message.Contains("rejected the credential", StringComparison.Ordinal), "says what it means: " + result.Message);
            Check(result.Message.Contains("Incorrect API key provided", StringComparison.Ordinal), "keeps the provider's words: " + result.Message);
            NoSecret(result.Message, "the message", key);
            NoSecret(ToJson(host.Accounts.Get(added.Account.Id)!) + host.LogText, "the account and the log", key);
            Check(FilesHolding(host.Options.DataDirectory, key).Count == 0, "not on disk");
            Equal(AccountStatus.Connected, host.Accounts.Get(added.Account.Id)!.Status, "a test changes no account by itself");

            host.Http.Failure = new HttpRequestException($"Connection refused while sending {key}");
            var unreachable = await host.Tester.TestKeyAsync("openai", key);
            Check(!unreachable.Ok && unreachable.Message.StartsWith("Could not reach", StringComparison.Ordinal), unreachable.Message);
            NoSecret(unreachable.Message, "an unreachable server's message", key);
            await ThrowsAsync<ArgumentException>(() => host.Tester.TestKeyAsync("openai", "two words"), "a malformed key");
            await ThrowsAsync<KeyNotFoundException>(() => host.Tester.TestAccountAsync("acc-none"), "an unknown account");
        }),

        Case("credentials.connection-test-sign-ins-and-unsupported-providers", async () =>
        {
            await using var host = new VaultHost("cred-test-signin", setup => setup.Environment["AWS_PROFILE"] = "default");
            var azure = await host.Tester.TestKeyAsync("azure", Planted("az"));
            Check(!azure.Supported && azure.Message.Contains("not available", StringComparison.Ordinal), "no known models endpoint: " + azure.Message);
            var ambient = await host.Tester.TestAccountAsync("ambient-amazon-bedrock");
            Check(!ambient.Supported, "ambient credentials are not tested here");
            var settingsOnly = await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("amazon-bedrock", null) { Settings = new Dictionary<string, string> { ["AWS_REGION"] = "us-east-1" } });
            Check(!(await host.Tester.TestAccountAsync(settingsOnly.Account.Id)).Supported, "a settings-only account");
            Equal(0, host.Http.Requests.Count, "none of these sent anything");

            var valid = await host.Vault.SaveSignInAsync("openai-codex", new OAuthCredentialSnapshot(CodexJwt("acct-t"), Planted("rt-"),
                DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(), new Dictionary<string, string> { ["accountId"] = "acct-t" }), "acct-t",
                new Dictionary<string, string>(), "ChatGPT", null, default);
            var ok = await host.Tester.TestAccountAsync(valid.Account.Id);
            Check(ok.Ok && ok.Message.StartsWith("Signed in", StringComparison.Ordinal), "a valid sign-in: " + ok.Message);
            Equal(0, host.Http.Requests.Count, "checked without a request");

            host.Http.On("POST " + FakeOAuth.AuthBase + "/oauth/token", 400, new JsonObject { ["error"] = "invalid_grant", ["error_description"] = "Refresh token revoked" });
            var refresh = Planted("rt-");
            var due = await host.Vault.SaveSignInAsync("openai-codex", new OAuthCredentialSnapshot(CodexJwt("acct-due"), refresh,
                DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(), new Dictionary<string, string> { ["accountId"] = "acct-due" }), "acct-due",
                new Dictionary<string, string>(), "ChatGPT", null, default);
            var failed = await host.Tester.TestAccountAsync(due.Account.Id);
            Check(!failed.Ok && failed.Message.Contains("sign in again", StringComparison.Ordinal), "a revoked sign-in: " + failed.Message);
            Equal(AccountStatus.Expired, host.Accounts.Get(due.Account.Id)!.Status, "is marked expired");
            var sent = host.Http.Requests.Single();
            Equal("refresh_token", sent.Form("grant_type"), "the only request was the refresh, at the auth server");
            NoSecret(failed.Message + ToJson(host.Accounts.List()) + host.LogText, "message, accounts and log", refresh);
        }),
    ];
}
