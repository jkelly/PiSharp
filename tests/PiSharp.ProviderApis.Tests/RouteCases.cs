using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Providers;
using PiSharp.Cli.Authentication;
using PiSharp.Contracts;

internal static partial class Program
{
    private const string CopilotToken = "tid=1;exp=99;proxy-ep=proxy.individual.githubcopilot.com;";

    private static async Task<Recorded> RouteRequest(string provider, string id, ImmutableArray<TranscriptEntry> messages, Func<ValueTask<ProviderRequestAuth>> auth,
        ImmutableDictionary<string, string>? environment = null)
    {
        var row = CatalogRow(provider, id); var http = new FakeHttp();
        http.OnUrl("https://", _ => Json("""{"error":{"message":"fake peer refuses"}}""", HttpStatusCode.BadRequest));
        using var route = NativeProviderFactory.CreateProviderRoute(Descriptor(row), row.Raw, new(_ => auth()) { MaxTokens = 512, Environment = environment }, http);
        var events = await Collect(route, new(Descriptor(row), messages, 1));
        Check(events[^1] is StreamError && http.All.Count == 1, "the fake peer refuses the request: " + ErrorMessage(events[^1]) + " requests=" + http.All.Count + " " + ((StreamTerminalEvent)events[^1]).NativeSourceException);
        return http.All.Single();
    }

    private static ImmutableArray<TranscriptEntry> UserTurn(bool image = false) => image
        ? [Entry("""{"role":"user","content":[{"type":"text","text":"look"},{"type":"image","data":"AAEC","mimeType":"image/png"}],"timestamp":1}""")]
        : [Entry("""{"role":"user","content":"Hi","timestamp":1}""")];

    private static ImmutableArray<TranscriptEntry> AgentTurn() =>
    [
        Entry("""{"role":"user","content":"Hi","timestamp":1}"""),
        Entry("""{"role":"assistant","content":[{"type":"toolCall","id":"call_1","name":"read","arguments":{}}],"api":"openai-completions","provider":"github-copilot","model":"kimi-k3","usage":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"totalTokens":0,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0}},"stopReason":"toolUse","timestamp":2}"""),
        Entry("""{"role":"toolResult","toolCallId":"call_1","toolName":"read","content":[{"type":"text","text":"ok"}],"isError":false,"timestamp":3}""")
    ];

    private static async Task CopilotRequests()
    {
        ValueTask<ProviderRequestAuth> Auth() => ValueTask.FromResult(new ProviderRequestAuth(CopilotToken, ProviderHeaderPolicies.CopilotBaseUrl(CopilotToken, null)));
        // Anthropic Messages: Bearer auth (the SDK's authToken), no x-api-key, model headers and the dynamic Copilot headers.
        var anthropic = await RouteRequest("github-copilot", "claude-sonnet-4.6", UserTurn(image: true), Auth);
        Equal("https://api.individual.githubcopilot.com/v1/messages?beta=true", anthropic.Url, "anthropic url");
        Check(anthropic.Header("authorization") == "Bearer " + CopilotToken && anthropic.Header("x-api-key") is null &&
            anthropic.Header("x-initiator") == "user" && anthropic.Header("openai-intent") == "conversation-edits" &&
            anthropic.Header("copilot-vision-request") == "true" && anthropic.Header("user-agent") == "GitHubCopilotChat/0.35.0" &&
            anthropic.Header("editor-version") == "vscode/1.107.0" && anthropic.Header("editor-plugin-version") == "copilot-chat/0.35.0" &&
            anthropic.Header("copilot-integration-id") == "vscode-chat" && anthropic.Header("anthropic-version") == "2023-06-01", "anthropic copilot headers");
        // Completions after a tool result: an agent-initiated request without images.
        var completions = await RouteRequest("github-copilot", "kimi-k3", AgentTurn(), Auth);
        Equal("https://api.individual.githubcopilot.com/chat/completions", completions.Url, "completions url");
        Check(completions.Header("authorization") == "Bearer " + CopilotToken && completions.Header("x-initiator") == "agent" &&
            completions.Header("copilot-vision-request") is null && completions.Header("openai-intent") == "conversation-edits", "completions copilot headers");
        var responses = await RouteRequest("github-copilot", "gpt-5.5", UserTurn(), Auth);
        Equal("https://api.individual.githubcopilot.com/responses", responses.Url, "responses url");
        Check(responses.Header("authorization") == "Bearer " + CopilotToken && responses.Header("x-initiator") == "user", "responses copilot headers");
        // The enterprise proxy endpoint named by the token.
        var enterprise = await RouteRequest("github-copilot", "kimi-k3", UserTurn(),
            () => ValueTask.FromResult(new ProviderRequestAuth("tid=2;proxy-ep=proxy.enterprise.githubcopilot.com;", ProviderHeaderPolicies.CopilotBaseUrl("tid=2;proxy-ep=proxy.enterprise.githubcopilot.com;", "corp.example"))));
        Equal("https://api.enterprise.githubcopilot.com/chat/completions", enterprise.Url, "enterprise url");
        Equal("agent", ProviderHeaderPolicies.CopilotInitiator(AgentTurn()), "initiator");
    }

    private static async Task CloudflareRequests()
    {
        var env = ImmutableDictionary<string, string>.Empty.Add("CLOUDFLARE_ACCOUNT_ID", "acct").Add("CLOUDFLARE_GATEWAY_ID", "gw");
        var workers = await RouteRequest("cloudflare-workers-ai", "@cf/deepseek-ai/deepseek-v4-flash-0731", UserTurn(),
            () => ValueTask.FromResult(new ProviderRequestAuth("cf-key")), env);
        Equal("https://api.cloudflare.com/client/v4/accounts/acct/ai/v1/chat/completions", workers.Url, "workers url");
        Equal("Bearer cf-key", workers.Header("authorization"), "workers bearer");
        var gatewayHeaders = ProviderHeaderPolicies.CloudflareGatewayHeaders("cf-key");
        var anthropic = await RouteRequest("cloudflare-ai-gateway", "claude-fable-5", UserTurn(), () => ValueTask.FromResult(new ProviderRequestAuth(null, null, gatewayHeaders)), env);
        Equal("https://gateway.ai.cloudflare.com/v1/acct/gw/anthropic/v1/messages?beta=true", anthropic.Url, "gateway anthropic url");
        Check(anthropic.Header("cf-aig-authorization") == "Bearer cf-key" && anthropic.Header("x-api-key") is null && anthropic.Header("authorization") is null, "gateway anthropic auth");
        var responses = await RouteRequest("cloudflare-ai-gateway", "gpt-4.1", UserTurn(), () => ValueTask.FromResult(new ProviderRequestAuth(null, null, gatewayHeaders)), env);
        Equal("https://gateway.ai.cloudflare.com/v1/acct/gw/openai/responses", responses.Url, "gateway responses url");
        Check(responses.Header("cf-aig-authorization") == "Bearer cf-key" && responses.Header("authorization") is null, "gateway responses auth");
        var completions = await RouteRequest("cloudflare-ai-gateway", "workers-ai/@cf/deepseek-ai/deepseek-v4-flash-0731", UserTurn(),
            () => ValueTask.FromResult(new ProviderRequestAuth(null, null, gatewayHeaders)), env);
        Equal("https://gateway.ai.cloudflare.com/v1/acct/gw/compat/chat/completions", completions.Url, "gateway completions url");
        Check(completions.Header("authorization") is null && completions.Header("cf-aig-authorization") == "Bearer cf-key", "gateway completions auth");
        Equal("https://gateway.ai.cloudflare.com/v1/acct/{CLOUDFLARE_GATEWAY_ID}/x",
            ProviderHeaderPolicies.ResolveCloudflareBaseUrl("https://gateway.ai.cloudflare.com/v1/{CLOUDFLARE_ACCOUNT_ID}/{CLOUDFLARE_GATEWAY_ID}/x",
                ImmutableDictionary<string, string>.Empty.Add("CLOUDFLARE_ACCOUNT_ID", "acct")), "partial placeholders");
        // An unresolved placeholder refuses the request instead of sending it.
        var row = CatalogRow("cloudflare-workers-ai", "@cf/deepseek-ai/deepseek-v4-flash-0731");
        using var unresolved = NativeProviderFactory.CreateProviderRoute(Descriptor(row), row.Raw, new(_ => ValueTask.FromResult(new ProviderRequestAuth("k"))), new FakeHttp());
        Equal("Unresolved provider base URL placeholder.", ErrorMessage((await Collect(unresolved, new(Descriptor(row), UserTurn(), 1)))[^1]), "unresolved placeholder");
    }

    /// <summary>Owner decision 0004: request budgets follow Pi, so an image of about 4.5 MB reaches every A1 transport.</summary>
    private static async Task LargeImageRequests()
    {
        var image = Convert.ToBase64String(new byte[4_700_000]);
        ImmutableArray<TranscriptEntry> Big() => [Entry($$$"""{"role":"user","content":[{"type":"text","text":"look"},{"type":"image","data":"{{{image}}}","mimeType":"image/png"}],"timestamp":1}""")];
        ValueTask<ProviderRequestAuth> Auth() => ValueTask.FromResult(new ProviderRequestAuth(CopilotToken, ProviderHeaderPolicies.CopilotBaseUrl(CopilotToken, null)));
        foreach (var id in new[] { "claude-sonnet-4.6", "kimi-k3", "gpt-5.5" })
        {
            var request = await RouteRequest("github-copilot", id, Big(), Auth);
            Check(request.BodyBytes.Length > 6_000_000 && request.Body.Contains(image[..64], StringComparison.Ordinal), "copilot " + id + " carries the image");
        }
        var bedrock = Bedrock(SonnetRow);
        using (var bedrockRequest = await bedrock.Transport.CreateRequestAsync(new(bedrock.Model, Big(), 1)))
            Check((await bedrockRequest.Content!.ReadAsByteArrayAsync()).Length > 6_000_000, "bedrock carries the image");
        var codex = Codex();
        codex.Http.OnUrl("https://", _ => Sse("""{"type":"response.completed","response":{"status":"completed","output":[]}}"""));
        Check((await Collect(codex.Transport, new(codex.Model, Big(), 1)))[^1] is StreamDone && codex.Http.All.Single().BodyBytes.Length > 6_000_000, "codex carries the image");
        var vertexRow = CatalogRow("google-vertex", "gemini-2.5-flash"); var vertexHttp = new FakeHttp();
        vertexHttp.OnUrl("https://", _ => Json("""{"error":{"message":"fake peer refuses"}}""", HttpStatusCode.BadRequest));
        using (var vertex = NativeProviderFactory.CreateGoogleVertexRoute(Descriptor(vertexRow), vertexRow.Raw, _ => ValueTask.FromResult(new GoogleVertexRequestAuth(
            new("https://aiplatform.googleapis.com/v1beta1/publishers/google/models/gemini-2.5-flash:streamGenerateContent?alt=sse"), "vertex-key", true, "-", "-")), 512, false, vertexHttp))
        {
            var small = await Collect(vertex, new(Descriptor(vertexRow), UserTurn(true), 1));
            Check(vertexHttp.All.Count == 1, "small vertex " + ErrorMessage(small[^1]) + " " + ((StreamTerminalEvent)small[^1]).NativeSourceException);
            vertexHttp.Requests.Clear();
            var events = await Collect(vertex, new(Descriptor(vertexRow), Big(), 1));
            Check(vertexHttp.All.Count == 1 && vertexHttp.All.Single().BodyBytes.Length > 6_000_000, "vertex carries the image: " + ErrorMessage(events[^1]) + " requests=" + vertexHttp.All.Count + " " + string.Join(",", vertexHttp.All.Select(item => item.BodyBytes.Length)));
        }
    }

    private static void OpenCodeHeaders()
    {
        Equal("s-1", ProviderHeaderPolicies.WithOpenCodeSessionHeader("s-1", null)!["x-opencode-session"], "added");
        var caller = ImmutableDictionary<string, string?>.Empty.Add("X-OpenCode-Session", "mine");
        Check(ReferenceEquals(caller, ProviderHeaderPolicies.WithOpenCodeSessionHeader("s-1", caller)), "caller header kept");
        Check(ProviderHeaderPolicies.WithOpenCodeSessionHeader(null, null) is null, "no session id");
    }

    /// <summary>Decision 0004: a stored "!command" api_key runs through the shared config-value resolver for every provider, as Pi's
    /// auth-storage read does; a command that resolves nothing leaves the key absent, so the environment variable applies.</summary>
    private static async Task StoredCommandKeys()
    {
        var directory = Temp("authjson-command"); var path = Path.Combine(directory, "auth.json");
        await File.WriteAllTextAsync(path, """{"groq":{"type":"api_key","key":"!echo stored-command-key"},"xai":{"type":"api_key","key":"!exit 1"}}""");
        var store = new AuthJsonCredentialStore(path);
        PiSharp.Cli.Authentication.ProviderLiveAuthentication Auth(string id, params (string Name, string Value)[] env) => new(
            PiSharp.Cli.Authentication.ProviderAuthCatalog.Find(id)!, store,
            new PiSharp.Cli.Authentication.LiveProcessEnvironment(name => env.FirstOrDefault(pair => pair.Name == name).Value, []), () => new HttpMessageInvoker(new HttpClientHandler()));
        Equal("stored-command-key", (await Auth("groq").ResolveAsync(default))?.ApiKey, "stored command key");
        Equal("env-xai", (await Auth("xai", ("XAI_API_KEY", "env-xai")).ResolveAsync(default))?.ApiKey, "failed command falls back to the environment");
    }

    private static async Task AuthJsonFields()
    {
        var directory = Temp("authjson"); var path = Path.Combine(directory, "auth.json");
        await File.WriteAllTextAsync(path, """{"github-copilot":{"type":"oauth","refresh":"gh","access":"cop","expires":5,"enterpriseUrl":"ghe.example.com","availableModelIds":["a","b"]}}""");
        var store = new AuthJsonCredentialStore(path);
        var copilot = await store.ReadAsync("github-copilot", default);
        Check(copilot!.ProviderData["enterpriseUrl"] == "ghe.example.com" && copilot.ProviderJson["availableModelIds"].ToString() == """["a","b"]""", "json fields read");
        Equal("a,b", string.Join(",", PiSharp.AI.Authentication.OAuth.GitHubCopilotOAuth.AvailableModels(copilot)!.Order()), "available set");
        await new PiSharp.AI.Authentication.OAuth.StoredOAuthLifecycle(store, new NoRefresh()).ReauthenticateAsync("openai",
            new("chatgpt", "r", 9, new Dictionary<string, string> { ["clientId"] = "c" }, new Dictionary<string, JsonData> { ["scopes"] = JsonData.Parse("""["x"]""") }));
        await store.WriteApiKeyAsync("cloudflare-ai-gateway", "cf-key", new Dictionary<string, string> { ["CLOUDFLARE_ACCOUNT_ID"] = "acct", ["CLOUDFLARE_GATEWAY_ID"] = "gw" }, default);
        await store.WriteApiKeyAsync("amazon-bedrock", null, null, default);
        Equal("""
            {
              "github-copilot": {
                "type": "oauth",
                "refresh": "gh",
                "access": "cop",
                "expires": 5,
                "enterpriseUrl": "ghe.example.com",
                "availableModelIds": [
                  "a",
                  "b"
                ]
              },
              "openai": {
                "type": "oauth",
                "refresh": "r",
                "access": "chatgpt",
                "expires": 9,
                "clientId": "c",
                "scopes": [
                  "x"
                ]
              },
              "cloudflare-ai-gateway": {
                "type": "api_key",
                "key": "cf-key",
                "env": {
                  "CLOUDFLARE_ACCOUNT_ID": "acct",
                  "CLOUDFLARE_GATEWAY_ID": "gw"
                }
              },
              "amazon-bedrock": {
                "type": "api_key"
              }
            }
            """.ReplaceLineEndings("\n"), await File.ReadAllTextAsync(path), "auth.json");
        var entry = await store.ReadEntryAsync("cloudflare-ai-gateway", default);
        Check(entry is { Type: "api_key", Key: "cf-key" } && entry.Environment!["CLOUDFLARE_GATEWAY_ID"] == "gw", "api key entry");
    }

    private sealed class NoRefresh : PiSharp.AI.Authentication.OAuth.IAdmittedOAuthRefresh
    {
        public Task<PiSharp.AI.Authentication.OAuth.OAuthCredentialSnapshot> RefreshAsync(string provider, PiSharp.AI.Authentication.OAuth.OAuthCredentialSnapshot current, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no refresh expected");
    }
}
