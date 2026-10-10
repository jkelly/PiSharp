using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.Contracts;

internal static partial class Program
{
    /// <summary>A scripted login interaction: each prompt is answered by the callback; events are recorded.</summary>
    internal sealed class ScriptedInteraction(Func<AuthPrompt, IReadOnlyList<AuthEvent>, string> answer) : IProviderAuthInteraction
    {
        public readonly ConcurrentQueue<AuthEvent> Events = new();
        public readonly ConcurrentQueue<AuthPrompt> Prompts = new();
        public Task<string> PromptAsync(AuthPrompt prompt, CancellationToken cancellationToken)
        {
            Prompts.Enqueue(prompt);
            if (prompt.Kind == AuthPromptKind.ManualCode && answer(prompt, [.. Events]) is var code && code == "<wait>")
            {
                var waiting = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                cancellationToken.Register(() => waiting.TrySetException(new OperationCanceledException("Login cancelled")));
                return waiting.Task;
            }
            return Task.FromResult(answer(prompt, [.. Events]));
        }
        public void Notify(AuthEvent authEvent) => Events.Enqueue(authEvent);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }

    private static string QueryOf(string url, string name) => OAuthFlows.Query(new Uri(url).Query, name)!;
    private static Func<string, string?> NoEnvironment => _ => null;

    private static async Task CodexOAuthFlows()
    {
        var time = new FixedTime(1_800_000_000_000);
        var http = new FakeHttp();
        var tokens = new Queue<string>([Jwt("""{"https://api.openai.com/auth":{"chatgpt_account_id":"acct-1"}}"""), Jwt("""{"https://api.openai.com/auth":{"chatgpt_account_id":"acct-2"}}"""),
            Jwt("""{"https://api.openai.com/auth":{"chatgpt_account_id":"acct-3"}}""")]);
        http.On(request => request.Url.StartsWith("https://auth.example/oauth/token", StringComparison.Ordinal) && tokens.Count > 0,
            _ => Json(JsonSerializer.Serialize(new { access_token = tokens.Dequeue(), refresh_token = "refresh-" + tokens.Count, expires_in = 3600 })));
        var port = FreePort();
        var oauth = new OpenAICodexOAuth(new HttpMessageInvoker(http, disposeHandler: false), NoEnvironment, time, "https://auth.example", port);
        // Browser login, completed by pasting the redirect URL with the state.
        var browser = new ScriptedInteraction((prompt, events) => prompt.Kind == AuthPromptKind.Select ? "browser"
            : $"http://localhost:1455/auth/callback?code=code-1&state={QueryOf(events.Single(e => e.Kind == AuthEventKind.AuthUrl).Url!, "state")}");
        var credential = await oauth.LoginAsync(browser, new(AgentName: "PiSharp"), default);
        var url = browser.Events.Single(e => e.Kind == AuthEventKind.AuthUrl).Url!;
        Check(url.StartsWith("https://auth.example/oauth/authorize?response_type=code&client_id=app_EMoamEEZ73f0CkXaXp7hrann&redirect_uri=http%3A%2F%2Flocalhost%3A1455%2Fauth%2Fcallback&scope=openid+profile+email+offline_access&code_challenge=", StringComparison.Ordinal) &&
            url.EndsWith("&id_token_add_organizations=true&codex_cli_simplified_flow=true&originator=PiSharp", StringComparison.Ordinal) && QueryOf(url, "state").Length == 32, "authorize url " + url);
        Check(credential.ProviderData["accountId"] == "acct-1" && credential.Refresh == "refresh-2" && credential.ExpiresUnixMilliseconds == 1_800_003_600_000, "browser credential");
        var exchange = http.All.Single(request => request.Body.Contains("grant_type=authorization_code", StringComparison.Ordinal));
        Check(exchange.Body.StartsWith("grant_type=authorization_code&client_id=app_EMoamEEZ73f0CkXaXp7hrann&code=code-1&code_verifier=", StringComparison.Ordinal) &&
            exchange.Body.EndsWith("&redirect_uri=http%3A%2F%2Flocalhost%3A1455%2Fauth%2Fcallback", StringComparison.Ordinal), "exchange body");
        Equal("State mismatch", (await Throws<InvalidOperationException>(() => oauth.LoginAsync(new ScriptedInteraction((prompt, _) => prompt.Kind == AuthPromptKind.Select ? "browser" : "code#other-state"), null, default))).Message, "state mismatch");
        // Device code: pending (403) then the authorization code, exchanged with the device redirect.
        var pending = 0;
        http.OnUrl("https://auth.example/api/accounts/deviceauth/usercode", _ => Json("""{"device_auth_id":"dev-1","user_code":"ABCD-EFGH","interval":"1"}"""))
            .OnUrl("https://auth.example/api/accounts/deviceauth/token", _ => ++pending == 1 ? Text("", HttpStatusCode.Forbidden) : Json("""{"authorization_code":"auth-code","code_verifier":"server-verifier"}"""));
        var device = new ScriptedInteraction((prompt, _) => "device_code");
        var deviceCredential = await oauth.LoginAsync(device, null, default);
        var shown = device.Events.Single(e => e.Kind == AuthEventKind.DeviceCode);
        Check(shown is { UserCode: "ABCD-EFGH", VerificationUri: "https://auth.example/codex/device", IntervalSeconds: 1, ExpiresInSeconds: 900 } && deviceCredential.ProviderData["accountId"] == "acct-2", "device credential");
        Check(http.All.Last(request => request.Body.Contains("grant_type=authorization_code", StringComparison.Ordinal)).Body ==
            "grant_type=authorization_code&client_id=app_EMoamEEZ73f0CkXaXp7hrann&code=auth-code&code_verifier=server-verifier&redirect_uri=https%3A%2F%2Fauth.example%2Fdeviceauth%2Fcallback", "device exchange");
        // Refresh: the refresh grant keeps the account id claim.
        var refreshed = await oauth.RefreshAsync("openai-codex", deviceCredential, default);
        Check(refreshed.ProviderData["accountId"] == "acct-3", "refreshed account");
        Equal("grant_type=refresh_token&refresh_token=refresh-1&client_id=app_EMoamEEZ73f0CkXaXp7hrann", http.All.Last().Body, "refresh body");
        Equal(new ProviderModelAuth(refreshed.Access).ApiKey, oauth.ToAuth(refreshed).ApiKey, "toAuth apiKey");
        // A token without the claim is refused.
        http.OnUrl("https://auth.example/oauth/token", _ => Json("""{"access_token":"opaque","refresh_token":"r","expires_in":1}"""));
        tokens.Clear();
        Equal("Failed to extract accountId from token", (await Throws<InvalidOperationException>(() => oauth.RefreshAsync("openai-codex", refreshed, default))).Message, "missing claim");
    }

    private static async Task ChatGPTOAuthFlow()
    {
        var time = new FixedTime(1_800_000_000_000);
        var http = new FakeHttp().OnUrl("https://auth.example/api/accounts/oauth/token", request => Json(JsonSerializer.Serialize(new
        {
            access_token = "chatgpt-access", refresh_token = "chatgpt-refresh", expires_in = 3600, id_token = "id",
            scope = "openid chatgpt.tokens.use.direct resource.invoke"
        })));
        var port = FreePort();
        var oauth = new OpenAIChatGPTOAuth(new HttpMessageInvoker(http, disposeHandler: false), NoEnvironment, time, "https://auth.example", port);
        Equal("Sign in with ChatGPT requires a device ID (UUID) for this installation",
            (await Throws<InvalidOperationException>(() => oauth.LoginAsync(new ScriptedInteraction((_, _) => ""), null, default))).Message, "device id required");
        var deviceId = "0F8FAD5B-D9CB-469F-A165-70867728950E";
        // The browser callback: a GET on the loopback server carries the code, state and the issued client id.
        var interaction = new ScriptedInteraction((prompt, events) => "<wait>");
        var login = oauth.LoginAsync(interaction, new(() => deviceId), default);
        string? url = null;
        for (var attempt = 0; attempt < 500 && url is null; attempt++) { url = interaction.Events.FirstOrDefault(e => e.Kind == AuthEventKind.AuthUrl)?.Url; await Task.Delay(10); }
        Check(url!.StartsWith("https://auth.example/api/accounts/authorize?client_id=dynamic_agent_client&agent_name_hint=Pi&ext_agent_host_id=urn%3Auuid%3A0f8fad5b-d9cb-469f-a165-70867728950e&response_type=code&redirect_uri=http%3A%2F%2F127.0.0.1%3A" + port, StringComparison.Ordinal), "authorize " + url);
        using (var browser = new HttpClient())
        {
            var wrong = await browser.GetAsync($"http://127.0.0.1:{port}/auth/callback?code=c&state=wrong&client_id=x");
            Equal(HttpStatusCode.BadRequest, wrong.StatusCode, "wrong state page");
            var page = await browser.GetStringAsync($"http://127.0.0.1:{port}/auth/callback?code=c1&state={QueryOf(url, "state")}&client_id=client-xyz");
            Check(page.Contains("ChatGPT authentication completed. You can close this window.", StringComparison.Ordinal), "success page");
        }
        var credential = await login;
        Check(credential.ProviderData["clientId"] == "client-xyz" && credential.ProviderJson["scopes"].ToString() == """["openid","chatgpt.tokens.use.direct","resource.invoke"]""" &&
            credential.ExpiresUnixMilliseconds == 1_800_000_000_000 + 3_600_000 - 180_000, "chatgpt credential");
        var exchange = http.All.Single().Body;
        Check(exchange.StartsWith("grant_type=authorization_code&client_id=client-xyz&code=c1&code_verifier=", StringComparison.Ordinal) &&
            exchange.EndsWith($"&redirect_uri=http%3A%2F%2F127.0.0.1%3A{port}%2Fauth%2Fcallback&resource=https%3A%2F%2Fapi.openai.com%2Fv1", StringComparison.Ordinal), "exchange " + exchange);
        var refreshed = await oauth.RefreshAsync("openai", credential, default);
        Equal("grant_type=refresh_token&client_id=client-xyz&refresh_token=chatgpt-refresh&resource=https%3A%2F%2Fapi.openai.com%2Fv1", http.All.Last().Body, "refresh");
        Equal("https://auth.example/api/accounts/oauth/token", http.All.Last().Url, "refresh url");
        Check(refreshed.ProviderData["clientId"] == "client-xyz" && refreshed.ProviderJson.ContainsKey("scopes"), "refreshed credential keeps clientId and scopes");
        // One refresh registry: every upstream OAuth provider refreshes a stored credential (auth/oauth/load.ts).
        var registered = PiSharp.Cli.Extensions.NativeExtensionModelOperations.DefaultOAuthRefreshes(NoEnvironment, () => new HttpClient(), time);
        Equal("anthropic,github-copilot,kimi-coding,meta,openai,openai-codex,openrouter,radius,xai", string.Join(",", registered.Keys.Order(StringComparer.Ordinal)), "registered refreshes");
        Equal("Stored OpenAI OAuth credential does not contain an issued client ID; reconnect ChatGPT",
            (await Throws<InvalidOperationException>(() => oauth.RefreshAsync("openai", new("a", "r", 1), default))).Message, "no client id");
    }

    private static async Task CopilotOAuthFlow()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var http = new FakeHttp()
            .OnUrl("https://ghe.example.com/login/device/code", _ => Json("""{"device_code":"dc","user_code":"WXYZ-1234","verification_uri":"https://ghe.example.com/login/device","interval":1,"expires_in":900}"""))
            .OnUrl("https://ghe.example.com/login/oauth/access_token", _ => Json("""{"access_token":"ghu_token"}"""))
            .OnUrl("https://api.ghe.example.com/copilot_internal/v2/token", _ => Json($$"""{"token":"tid=1;exp=2;proxy-ep=proxy.enterprise.githubcopilot.com;","expires_at":{{now + 1800}}}"""))
            .OnUrl("https://api.enterprise.githubcopilot.com/models/claude-sonnet-4.6/policy", _ => Json("{}"))
            .OnUrl("https://api.enterprise.githubcopilot.com/models", _ => Json("""
                {"data":[
                {"id":"gpt-5.5","model_picker_enabled":true,"policy":{"state":"enabled"}},
                {"id":"claude-sonnet-4.6","model_picker_enabled":true,"policy":{"state":"unconfigured"}},
                {"id":"embedding","model_picker_enabled":true,"capabilities":{"supports":{"tool_calls":false}}},
                {"id":"hidden","model_picker_enabled":false,"policy":{"state":"enabled"}},
                {"id":"off","model_picker_enabled":true,"policy":{"state":"disabled"}}]}
                """));
        var oauth = new GitHubCopilotOAuth(new HttpMessageInvoker(http, disposeHandler: false), id => id == "claude-sonnet-4.6");
        var interaction = new ScriptedInteraction((prompt, _) => prompt.Kind == AuthPromptKind.Text ? "https://ghe.example.com/" : "");
        var credential = await oauth.LoginAsync(interaction, null, default);
        Check(interaction.Prompts.First() is { Kind: AuthPromptKind.Text, Message: "GitHub Enterprise URL/domain (blank for github.com)", Placeholder: "company.ghe.com" }, "domain prompt");
        Check(interaction.Events.Any(e => e is { Kind: AuthEventKind.DeviceCode, UserCode: "WXYZ-1234", VerificationUri: "https://ghe.example.com/login/device" }) &&
            interaction.Events.Any(e => e is { Kind: AuthEventKind.Progress, Message: "Enabling models..." }), "device code and progress events");
        Check(credential.Refresh == "ghu_token" && credential.ProviderData["enterpriseUrl"] == "ghe.example.com" &&
            credential.ExpiresUnixMilliseconds == (now + 1800) * 1000 - 300_000, "copilot credential");
        Equal("""["gpt-5.5","claude-sonnet-4.6"]""", credential.ProviderJson["availableModelIds"].ToString(), "available models (picker plus enabled policy)");
        var token = http.All.Single(request => request.Url.EndsWith("/copilot_internal/v2/token", StringComparison.Ordinal));
        Check(token.Header("authorization") == "Bearer ghu_token" && token.Header("user-agent") == "GitHubCopilotChat/0.35.0" &&
            token.Header("editor-version") == "vscode/1.107.0" && token.Header("copilot-integration-id") == "vscode-chat", "copilot token headers");
        var models = http.All.First(request => request.Url == "https://api.enterprise.githubcopilot.com/models");
        Equal("2026-06-01", models.Header("x-github-api-version"), "api version");
        var policy = http.All.Single(request => request.Url.EndsWith("/policy", StringComparison.Ordinal));
        Check(policy.Body == """{"state":"enabled"}""" && policy.Header("openai-intent") == "chat-policy" && policy.Header("x-interaction-type") == "chat-policy", "policy request");
        var poll = http.All.Single(request => request.Url.EndsWith("/access_token", StringComparison.Ordinal));
        Check(poll.Body == "client_id=Iv1.b507a08c87ecfe98&device_code=dc&grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", "poll body " + poll.Body);
        Equal("https://api.enterprise.githubcopilot.com", oauth.ToAuth(credential).BaseUrl, "toAuth base url");
        var refreshed = await oauth.RefreshAsync("github-copilot", credential, default);
        Equal("""["gpt-5.5","claude-sonnet-4.6"]""", refreshed.ProviderJson["availableModelIds"].ToString(), "refreshed models");
        Equal("https://copilot-api.corp.example", PiSharp.AI.Providers.ProviderHeaderPolicies.CopilotBaseUrl("no-proxy-token", "corp.example"), "enterprise fallback");
        Equal("https://api.individual.githubcopilot.com", PiSharp.AI.Providers.ProviderHeaderPolicies.CopilotBaseUrl(null, null), "individual default");
        Equal("Invalid GitHub Enterprise URL/domain", (await Throws<InvalidOperationException>(() => oauth.LoginAsync(new ScriptedInteraction((_, _) => "http://"), null, default))).Message, "invalid domain");
    }

    private static async Task SubscriptionOAuthFlows()
    {
        var http = new FakeHttp()
            .OnUrl("https://openrouter.ai/api/v1/auth/keys", _ => Json("""{"key":"sk-or-1"}"""))
            .OnUrl("https://kimi.example/api/oauth/device_authorization", _ => Json("""{"device_code":"kd","user_code":"KIMI","verification_uri":"https://kimi.example/d","verification_uri_complete":"https://kimi.example/d?c=KIMI","interval":1,"expires_in":60}"""))
            .OnUrl("https://kimi.example/api/oauth/token", request => request.Body.Contains("refresh_token", StringComparison.Ordinal)
                ? Json("""{"access_token":"kimi-2","refresh_token":"kr-2","expires_in":60}""") : Json("""{"access_token":"kimi-1","refresh_token":"kr-1","expires_in":60}"""))
            .OnUrl("https://auth.meta.com/oidc/device/authorization/", _ => Json("""{"device_code":"md","user_code":"META","verification_uri":"https://meta.example/d","interval":1}"""))
            .OnUrl("https://auth.meta.com/oidc/device/token/", _ => Json("""{"access_token":"identity"}"""))
            .OnUrl("https://api.meta.ai/muse-code/key", request => request.Header("authorization") == "Bearer identity" ? Json("""{"api_key":"meta-key"}""") : Json("{}", HttpStatusCode.Unauthorized))
            .OnUrl("https://auth.x.ai/oauth2/device/code", _ => Json("""{"device_code":"xd","user_code":"XAI","verification_uri":"https://x.ai/d","expires_in":60,"interval":1}"""))
            .OnUrl("https://auth.x.ai/oauth2/token", request => request.Body.StartsWith("grant_type=refresh_token", StringComparison.Ordinal)
                ? Json("""{"access_token":"xai-2"}""") : Json("""{"access_token":"xai-1","refresh_token":"xr","expires_in":3600}"""))
            .OnUrl("https://radius.example/v1/oauth/device", _ => Json("""{"device_code":"rd","user_code":"RAD","verification_uri":"https://radius.example/d","expires_in":60,"interval":1}"""))
            .OnUrl("https://radius.example/v1/oauth/token", _ => Json("""{"access_token":"radius-1","refresh_token":"rr","expires_in":600,"scope":"gateway offline_access"}"""));
        var invoker = new HttpMessageInvoker(http, disposeHandler: false);
        var time = new FixedTime(1_800_000_000_000);
        // OpenRouter: the pasted code is exchanged for a permanent key.
        var openRouter = await new OpenRouterOAuth(invoker, NoEnvironment, time).LoginAsync(new ScriptedInteraction((prompt, events) => "http://localhost/?code=or-code"), null, default);
        Check(openRouter is { Access: "sk-or-1", Refresh: "", ExpiresUnixMilliseconds: 9007199254740991 }, "openrouter key");
        Check(http.All.Single(request => request.Url.StartsWith("https://openrouter.ai", StringComparison.Ordinal)).Body.StartsWith("""{"code":"or-code","code_verifier":""", StringComparison.Ordinal), "openrouter exchange");
        // Kimi Code: device grant at the KIMI_CODE_OAUTH_HOST host; the token travels as an Authorization header.
        var kimiOAuth = new KimiCodingOAuth(invoker, name => name == "KIMI_CODE_OAUTH_HOST" ? "https://kimi.example/" : null, time, (_, _) => Task.CompletedTask);
        var kimiInteraction = new ScriptedInteraction((_, _) => "");
        var kimi = await kimiOAuth.LoginAsync(kimiInteraction, null, default);
        Check(kimi is { Access: "kimi-1", Refresh: "kr-1" } && kimiInteraction.Events.Single().VerificationUri == "https://kimi.example/d?c=KIMI", "kimi login");
        Equal("Bearer kimi-1", kimiOAuth.ToAuth(kimi).Headers!["Authorization"], "kimi header auth");
        Equal("kimi-2", (await kimiOAuth.RefreshAsync("kimi-coding", kimi, default)).Access, "kimi refresh");
        // Meta: identity token (refresh) minted into a day-long key; a 401 mint is a dead session.
        var metaOAuth = new MetaOAuth(invoker, time);
        var meta = await metaOAuth.LoginAsync(new ScriptedInteraction((_, _) => ""), null, default);
        Check(meta is { Access: "meta-key", Refresh: "identity", ExpiresUnixMilliseconds: 1_800_086_400_000 }, "meta key");
        Check((await Throws<InvalidOperationException>(() => metaOAuth.RefreshAsync("meta", new("k", "stale", 1), default))).Message
            .StartsWith("Meta session expired (status 401). Run `/login meta` to sign in again.", StringComparison.Ordinal), "meta dead session");
        // xAI: device grant; a refresh without a rotated refresh token keeps the old one.
        var xaiOAuth = new XaiOAuth(invoker, time);
        var xai = await xaiOAuth.LoginAsync(new ScriptedInteraction((_, _) => ""), null, default);
        Check(xai is { Access: "xai-1", Refresh: "xr", ExpiresUnixMilliseconds: 1_800_003_300_000 }, "xai login");
        var xaiRefreshed = await xaiOAuth.RefreshAsync("xai", xai, default);
        Check(xaiRefreshed is { Access: "xai-2", Refresh: "xr" } && xaiRefreshed.ExpiresUnixMilliseconds == 1_800_003_300_000, "xai refresh keeps refresh token");
        Check(http.All.Any(request => request.Body == "client_id=b1a00492-073a-47ea-816f-4c329264a828&scope=openid+profile+email+offline_access+grok-cli%3Aaccess+api%3Aaccess&referrer=pi"), "xai device request");
        // Radius: the device-code method against the configured gateway.
        var radiusOAuth = new RadiusOAuth(invoker, "Radius", "radius.example/", time);
        var radius = await radiusOAuth.LoginAsync(new ScriptedInteraction((prompt, _) => prompt.Kind == AuthPromptKind.Select ? "device-code" : ""), null, default);
        Check(radius is { Access: "radius-1", Refresh: "rr", ExpiresUnixMilliseconds: 1_800_000_540_000 } && radius.ProviderData["scope"] == "gateway offline_access", "radius login");
        Equal("https://radius.example", RadiusOAuth.NormalizeGateway("radius.example//"), "gateway normalization");
    }

    private static async Task LoopbackAndPoller()
    {
        var server = OAuthLoopbackServer<string>.Start("Example", "127.0.0.1", 0, "/cb", "state-1", (code, _) => code == "bad" ? throw new InvalidOperationException("exchange failed") : Task.FromResult("token-" + code), default);
        using var client = new HttpClient();
        Equal(HttpStatusCode.NotFound, (await client.GetAsync(server.RedirectUri.Replace("/cb", "/other"))).StatusCode, "route");
        Equal(HttpStatusCode.BadRequest, (await client.GetAsync(server.RedirectUri + "?code=x&state=wrong")).StatusCode, "state");
        Equal(HttpStatusCode.BadRequest, (await client.GetAsync(server.RedirectUri + "?state=state-1")).StatusCode, "missing code");
        var ok = await client.GetAsync(server.RedirectUri + "?code=abc&state=state-1");
        var page = await ok.Content.ReadAsStringAsync();
        Check(ok.StatusCode == HttpStatusCode.OK && page.Contains("<title>Authentication successful</title>", StringComparison.Ordinal) &&
            page.Contains("Signed in to Example. You may now close this page.", StringComparison.Ordinal), "success page");
        Equal((true, "token-abc"), await server.WaitAsync(), "completed value");
        Equal(HttpStatusCode.Conflict, (await client.GetAsync(server.RedirectUri + "?code=again&state=state-1")).StatusCode, "already handled");
        server.Close();
        var failing = OAuthLoopbackServer<string>.Start("Example", "127.0.0.1", 0, "/cb", null, (code, _) => throw new InvalidOperationException("exchange failed"), default);
        var failed = await client.GetAsync(failing.RedirectUri + "?code=bad");
        Check(failed.StatusCode == HttpStatusCode.BadGateway && (await failed.Content.ReadAsStringAsync()).Contains("Example sign-in failed.", StringComparison.Ordinal), "complete failure page");
        Equal("exchange failed", (await Throws<InvalidOperationException>(() => failing.WaitAsync())).Message, "complete failure");
        failing.Close();
        var denied = OAuthLoopbackServer<string>.Start("Example", "127.0.0.1", 0, "/cb", null, (code, _) => Task.FromResult(code), default);
        await client.GetAsync(denied.RedirectUri + "?error=access_denied&error_description=No+thanks");
        Equal("Example authorization failed: No thanks", (await Throws<InvalidOperationException>(() => denied.WaitAsync())).Message, "provider error");
        denied.Close();
        // The device poller: a timeout, and the slow_down variant of its message.
        Equal("Device flow timed out", (await Throws<InvalidOperationException>(() => OAuthFlows.PollDeviceCodeAsync<string>(
            _ => Task.FromResult(new OAuthFlows.DevicePollResult<string>(OAuthFlows.DevicePollStatus.Pending)), 1, 1.2, false, default))).Message, "timeout");
        Check((await Throws<InvalidOperationException>(() => OAuthFlows.PollDeviceCodeAsync<string>(
            _ => Task.FromResult(new OAuthFlows.DevicePollResult<string>(OAuthFlows.DevicePollStatus.SlowDown, IntervalSeconds: 1)), 1, 1.2, false, default))).Message
            .StartsWith("Device flow timed out after one or more slow_down responses.", StringComparison.Ordinal), "slow down timeout");
        Equal("denied", (await Throws<InvalidOperationException>(() => OAuthFlows.PollDeviceCodeAsync<string>(
            _ => Task.FromResult(new OAuthFlows.DevicePollResult<string>(OAuthFlows.DevicePollStatus.Failed, Message: "denied")), 1, 60, false, default))).Message, "failed");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Equal("Login cancelled", (await Throws<OperationCanceledException>(() => OAuthFlows.PollDeviceCodeAsync<string>(
            _ => Task.FromResult(new OAuthFlows.DevicePollResult<string>(OAuthFlows.DevicePollStatus.Pending)), 1, 60, true, cancel.Token))).Message, "cancelled");
    }
}
