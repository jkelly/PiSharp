using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Catalogs;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Models;
using PiSharp.Cli.Settings;
using PiSharp.Contracts;

// The CLI live route (SettingsModelSelection → LiveSessionSelection → LiveSessionConnection → NativeProviderFactory) for one model of
// every built-in provider, against a fake HTTP endpoint: models.ts createProvider sends each API to the model's baseUrl with the
// provider's environment key. Expectations are authored from the upstream API clients' base-URL handling.
internal static partial class Program
{
    internal static readonly List<string> LiveRoutes = [];
    private sealed record Seen(string Url, IReadOnlyDictionary<string, string> Headers, string? Body);

    private sealed class LiveEndpoint(Func<Seen, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly List<Seen> requests = [];
        internal Seen[] Snapshot() { lock (requests) return [.. requests]; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .ToDictionary(pair => pair.Key, pair => string.Join(",", pair.Value), StringComparer.OrdinalIgnoreCase);
            var seen = new Seen(request.RequestUri!.ToString(), headers, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            lock (requests) requests.Add(seen);
            return respond(seen);
        }
    }

    private const string CompletionsSse = "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
    private const string ResponsesSse = "data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"message\",\"id\":\"m\",\"content\":[]}}\n\n" +
        "data: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"item_id\":\"m\",\"delta\":\"ok\"}\n\n" +
        "data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"message\",\"id\":\"m\",\"content\":[{\"type\":\"output_text\",\"text\":\"ok\"}]}}\n\n" +
        "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"r\",\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":5,\"output_tokens\":3,\"total_tokens\":8}}}\n\n";
    private static string MessagesSse(string model)
    {
        static string Frame(string type, object value) => "event: " + type + "\ndata: " + JsonSerializer.Serialize(value) + "\n\n";
        return Frame("message_start", new { type = "message_start", message = new { id = "m", role = "assistant", model, content = Array.Empty<object>(), usage = new { input_tokens = 2, output_tokens = 0 } } })
            + Frame("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } })
            + Frame("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = "ok" } })
            + Frame("content_block_stop", new { type = "content_block_stop", index = 0 })
            + Frame("message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn" }, usage = new { output_tokens = 1 } })
            + Frame("message_stop", new { type = "message_stop" });
    }

    private static HttpResponseMessage Respond(Seen seen, string model)
    {
        string? body = seen.Url.Contains("/chat/completions", StringComparison.Ordinal) ? CompletionsSse :
            seen.Url.EndsWith("/responses", StringComparison.Ordinal) ? ResponsesSse :
            seen.Url.Contains("/v1/messages", StringComparison.Ordinal) ? MessagesSse(model) : null;
        return body is null
            ? new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"message\":\"authored fake endpoint\"}}", Encoding.UTF8, "application/json") }
            : new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
    }

    private static IEnumerable<(string, Func<Task>)> LiveRouteCases() =>
    [
        ("live.one-model-per-provider-against-fake-http", LiveEveryProvider),
        ("live.provider-api-routes-are-available-before-any-request", LiveUnsupported),
        ("live.models-json-custom-provider-headers-and-auth-header", LiveCustomProvider),
        ("live.cli-patterns-fallback-thinking-and-ambiguity", LivePatterns),
        ("live.scoped-models-and-settings-defaults", LiveScoped),
        ("live.legacy-pinned-parse-still-exact", Sync(LiveLegacyParse)),
    ];

    private static ChatRequest Request(ModelDescriptor model) =>
        new(model, [new("user", JsonData.Parse("""{"role":"user","content":"hello","timestamp":1}"""))], 1);

    private static async Task<StreamEvent?> Drive(IChatTransport transport, ModelDescriptor model)
    {
        StreamEvent? last = null;
        try { await foreach (var frame in transport.StreamAsync(Request(model))) last = frame; }
        catch (Exception error) when (error is not OperationCanceledException) { }
        return last;
    }

    private static string? LiveModelFor(string provider)
    {
        var chat = BuiltinModelCatalog.Get(provider).Models.Where(model => model.Type == CatalogModelType.Chat &&
            LiveSessionSelection.SupportedApi(provider, model.DeclaredApi)).ToList();
        if (chat.Count == 0) return null;
        return chat.FirstOrDefault(model => model.Id == ModelResolver.DefaultModelFor(provider))?.Id ?? chat[0].Id;
    }

    private static async Task LiveEveryProvider()
    {
        var routed = new List<string>();
        foreach (var provider in BuiltinProviders.All)
        {
            // IMPL-A1's provider APIs (AWS, OAuth and Cloudflare/Vertex routes) are pinned by PiSharp.ProviderApis.Tests.
            if (ProviderApiRoutes.Contains(provider.Id) || LiveModelFor(provider.Id) is not { } id) continue;
            var variable = ProviderEnvironmentKeys.GetApiKeyVariables(provider.Id)![^1];
            var key = "test-key-" + provider.Id;
            var endpoint = new LiveEndpoint(seen => Respond(seen, id));
            var runtime = new LiveSessionRuntime(Env((variable, key), ("AZURE_OPENAI_BASE_URL", "https://pisharp-res.openai.azure.com/openai/v1")), () => endpoint);
            var selection = await new SettingsModelSelection(null, provider.Id + "/" + id, null).ResolveAsync(null, runtime, null, false, CancellationToken.None);
            Equal(provider.Id, selection.Model.Provider, provider.Id + " provider"); Equal(id, selection.Model.Id, provider.Id + " model");
            if (provider.Id == "anthropic") { routed.Add("anthropic: resolved Anthropic route (AnthropicAuthSync/CliSync)"); continue; }
            var api = selection.Model.Api;
            using var connection = selection.Connect(runtime);
            var last = await Drive(connection.CreateTransport(), selection.Model);
            var seen = endpoint.Snapshot();
            Check(seen.Length >= 1, provider.Id + " sent no request");
            var request = seen[0];
            var baseUrl = provider.Id == "azure" ? "https://pisharp-res.openai.azure.com/openai/v1" : selection.Definition.BaseUrl.TrimEnd('/');
            var expected = api switch
            {
                "openai-completions" when provider.Id != "azure" => baseUrl + "/chat/completions", "openai-responses" => baseUrl + "/responses",
                "anthropic-messages" => baseUrl + "/v1/messages?beta=true", _ => null
            };
            if (expected is not null) Equal(expected, request.Url, provider.Id + " endpoint");
            else Check(request.Url.StartsWith(baseUrl, StringComparison.Ordinal), $"{provider.Id} endpoint {request.Url} outside {baseUrl}");
            Check(request.Headers.Values.Any(value => value.Contains(key, StringComparison.Ordinal)), provider.Id + " key not sent");
            Check(api == "google-generative-ai" ? request.Url.Contains(Uri.EscapeDataString(id).Replace("%2F", "/"), StringComparison.Ordinal) || request.Url.Contains(id, StringComparison.Ordinal)
                : request.Body!.Contains(JsonSerializer.Serialize(id), StringComparison.Ordinal) || provider.Id == "azure", provider.Id + " model not sent");
            if (api is "openai-completions" or "openai-responses" or "anthropic-messages" && provider.Id != "azure")
                Check(last is StreamDone, $"{provider.Id} {api} stream did not complete: {last?.GetType().Name}");
            routed.Add($"{provider.Id}: {api} {request.Url}");
        }
        Equal(35, routed.Count, "providers with a live route: " + string.Join("; ", routed));
        LiveRoutes.AddRange(routed);
    }

    private static readonly string[] ProviderApiRoutes = ["amazon-bedrock", "openai-codex", "github-copilot", "cloudflare-workers-ai", "cloudflare-ai-gateway", "google-vertex"];

    /// <summary>The APIs IMPL-A1 added are no longer refused with LiveApiUnavailable; selection itself sends nothing.</summary>
    private static async Task LiveUnsupported()
    {
        foreach (var (provider, variable) in new[] { ("amazon-bedrock", "AWS_PROFILE"), ("openai-codex", "OPENAI_API_KEY"), ("github-copilot", "COPILOT_GITHUB_TOKEN"),
            ("cloudflare-workers-ai", "CLOUDFLARE_API_KEY"), ("cloudflare-ai-gateway", "CLOUDFLARE_API_KEY"), ("google-vertex", "GOOGLE_CLOUD_API_KEY") })
        {
            var id = ModelResolver.DefaultModelFor(provider)!;
            var endpoint = new LiveEndpoint(seen => Respond(seen, id));
            var runtime = new LiveSessionRuntime(Env((variable, "k")), () => endpoint);
            try { await new SettingsModelSelection(null, provider + "/" + id, null).ResolveAsync(null, runtime, null, false, CancellationToken.None); }
            catch (LiveSessionException error) { Check(error.Code != "LiveApiUnavailable", provider + " still has no live route: " + error.Message); }
            Equal(0, endpoint.Snapshot().Length, provider + " requests");
        }
        // resolveCliModel knows providers from chat models only: a classifier-only provider is unknown to --provider.
        var classifier = await ThrowsAsync<LiveSessionException>(() => new SettingsModelSelection("typesafe", "anything", null)
            .ResolveAsync(null, new LiveSessionRuntime(Env(), () => null), null, false, CancellationToken.None), "classifier-only");
        Check(classifier.Code == "UnknownLiveModel" && classifier.Message == "Unknown provider \"typesafe\". Use --list-models to see available providers/models.", classifier.Message);
    }

    private static Task LiveCustomProvider() => WithTemp("live-custom", async root =>
    {
        var models = Path.Combine(root, "models.json");
        await File.WriteAllTextAsync(models, """
            {"providers":{"lab":{"name":"Lab","baseUrl":"http://127.0.0.1:8123/v1","apiKey":"${LAB_KEY}","api":"openai-completions","authHeader":true,
              "headers":{"X-Team":"$TEAM"},"compat":{"supportsDeveloperRole":false,"maxTokensField":"max_tokens"},
              "models":[{"id":"lab-coder","reasoning":false,"input":["text"],"contextWindow":32768,"maxTokens":4096,"headers":{"X-Model":"coder"}}]},
              "groq":{"baseUrl":"https://groq-proxy.invalid/openai/v1"}}}
            """);
        var endpoint = new LiveEndpoint(seen => Respond(seen, "lab-coder"));
        var runtime = new LiveSessionRuntime(Env(("LAB_KEY", "lab-secret"), ("TEAM", "red"), ("GROQ_API_KEY", "gk")), () => endpoint, ModelsPath: models);
        var selection = await new SettingsModelSelection(null, "lab-coder", null).ResolveAsync(null, runtime, null, false, CancellationToken.None);
        Equal(new ModelDescriptor("lab-coder", "openai-completions", "lab"), selection.Model, "bare id resolves the custom model");
        using (var connection = selection.Connect(runtime))
            Check(await Drive(connection.CreateTransport(), selection.Model) is StreamDone, "custom stream completes");
        var request = endpoint.Snapshot().Single();
        Equal("http://127.0.0.1:8123/v1/chat/completions", request.Url, "custom endpoint");
        Equal("Bearer lab-secret", request.Headers["Authorization"], "authHeader bearer");
        Equal("red", request.Headers["X-Team"], "provider header"); Equal("coder", request.Headers["X-Model"], "model header");
        using (var body = JsonDocument.Parse(request.Body!))
        {
            Equal("lab-coder", body.RootElement.GetProperty("model").GetString(), "body model");
            Check(body.RootElement.TryGetProperty("max_tokens", out _) && !body.RootElement.TryGetProperty("max_completion_tokens", out _), "compat maxTokensField");
        }
        // A built-in provider with a models.json baseUrl override leaves its fixed route and keeps its environment key.
        var groq = await new SettingsModelSelection("groq", "gpt-oss-120b", null).ResolveAsync(null, runtime, null, false, CancellationToken.None);
        using (var connection = groq.Connect(runtime)) await Drive(connection.CreateTransport(), groq.Model);
        var proxied = endpoint.Snapshot()[^1];
        Equal("https://groq-proxy.invalid/openai/v1/chat/completions", proxied.Url, "overridden base URL");
        Equal("Bearer gk", proxied.Headers["Authorization"], "groq key");
        // A missing key refuses the session before any request.
        var refused = await new SettingsModelSelection(null, "lab/lab-coder", null).ResolveAsync(null,
            new LiveSessionRuntime(Env(), () => endpoint, ModelsPath: models), null, false, CancellationToken.None);
        var missing = Throws<LiveSessionException>(() => refused.Connect(new LiveSessionRuntime(Env(), () => endpoint, ModelsPath: models)), "missing key");
        Equal("MissingLiveApiKey", missing.Code, "missing key code");
        Equal("Failed to resolve API key for provider \"lab\" from environment variable: LAB_KEY", missing.Message, "missing key message");
    });

    private static async Task LivePatterns()
    {
        var endpoint = new LiveEndpoint(seen => Respond(seen, "x"));
        var runtime = new LiveSessionRuntime(Env(("OPENAI_API_KEY", "ok"), ("GROQ_API_KEY", "gk")), () => endpoint);
        async Task<(LiveSessionSelection Selection, string Diagnostics)> Select(SettingsModelSelection request, LiveSessionRuntime? custom = null)
        {
            using var diagnostics = new StringWriter();
            var selection = await request.ResolveAsync(null, custom ?? runtime, diagnostics, false, CancellationToken.None);
            return (selection, diagnostics.ToString());
        }
        // gpt-4 exists for azure and openai; only openai is authenticated.
        Equal("openai/gpt-4", (await Select(new(null, "gpt-4", null))).Selection.Model.Provider + "/gpt-4", "sole authenticated provider");
        var ambiguous = await ThrowsAsync<LiveSessionException>(() => Select(new(null, "gpt-4", null), new LiveSessionRuntime(Env(), () => endpoint)), "ambiguous");
        Equal("Model \"gpt-4\" is ambiguous across providers: azure/gpt-4, openai/gpt-4. No matching provider is authenticated. Use --provider or provider/model.", ambiguous.Message, "ambiguous");
        var thinking = await Select(new(null, "groq/gpt-oss-120b:high", null));
        Check(thinking.Selection.Model.Id == "openai/gpt-oss-120b" && thinking.Selection.PatternThinkingLevel == "high", "fuzzy id with :high");
        var explicitThinking = await Select(new(null, "groq/gpt-oss-120b:high", null) { CliThinking = "low" });
        Equal<string?>(null, explicitThinking.Selection.PatternThinkingLevel, "--thinking takes precedence");
        var fallback = await Select(new("groq", "brand-new-model", "2048"));
        Equal("brand-new-model", fallback.Selection.Model.Id, "custom id on a known provider");
        Equal(2048, fallback.Selection.MaximumOutputTokens, "max output tokens");
        Equal("{\"type\":\"model_diagnostic\",\"level\":\"warning\",\"message\":\"Model \\u0022brand-new-model\\u0022 not found for provider \\u0022groq\\u0022. Using custom model id.\"}\n"
            .Replace("\n", Environment.NewLine), fallback.Diagnostics, "warning diagnostic");
        using (var connection = fallback.Selection.Connect(runtime)) await Drive(connection.CreateTransport(), fallback.Selection.Model);
        using (var body = JsonDocument.Parse(endpoint.Snapshot()[^1].Body!)) Equal("brand-new-model", body.RootElement.GetProperty("model").GetString(), "custom id sent");
        var unknown = await ThrowsAsync<LiveSessionException>(() => Select(new(null, "no-such-model-anywhere", null)), "unknown");
        Check(unknown.Code == "UnknownLiveModel" && unknown.Message == "Model \"no-such-model-anywhere\" not found. Use --list-models to see available models.", unknown.Message);
        Equal("LiveOutputLimit", (await ThrowsAsync<LiveSessionException>(() => Select(new("openai", "gpt-4-turbo", "8192")), "limit")).Code, "output limit");
    }

    private static async Task LiveScoped()
    {
        var runtime = new LiveSessionRuntime(Env(("OPENAI_API_KEY", "ok"), ("GROQ_API_KEY", "gk")), () => null);
        var settings = new PiSharp.CodingAgent.Configuration.StartupSettingsSnapshot(JsonData.Parse(
            """{"defaultProvider":"groq","defaultModel":"openai/gpt-oss-20b","enabledModels":["groq/openai/gpt-oss-*:low","openai/gpt-4.1"]}"""),
            PiSharp.Agent.AgentPendingInputMode.OneAtATime, PiSharp.Agent.AgentPendingInputMode.OneAtATime, []);
        var inScope = await new SettingsModelSelection(null, null, null).ResolveAsync(settings, runtime, null, false, CancellationToken.None);
        Equal("groq/openai/gpt-oss-20b", inScope.Model.Provider + "/" + inScope.Model.Id, "saved default inside the scope");
        Equal("low", inScope.PatternThinkingLevel, "scoped thinking level");
        Equal(4, inScope.ScopedModels.Length, "scope size");
        var patterns = await new SettingsModelSelection(null, null, null) { ModelPatterns = ["gpt-4.1", "missing-x"] }
            .ResolveAsync(settings, runtime, null, false, CancellationToken.None);
        Equal("openai/gpt-4.1", patterns.Model.Provider + "/" + patterns.Model.Id, "--models overrides enabledModels; first scoped");
        Equal("No models match pattern \"missing-x\"", patterns.Warnings.Single(), "scope warning");
        var continuing = await new SettingsModelSelection(null, null, null).ResolveAsync(settings, runtime, null, true, CancellationToken.None);
        Equal("openai/gpt-oss-20b", continuing.Model.Id, "continuing sessions use the saved default");
        var initial = await new SettingsModelSelection(null, null, null).ResolveAsync(null, runtime, null, false, CancellationToken.None);
        Equal("openai/gpt-5.5", initial.Model.Provider + "/" + initial.Model.Id, "first available provider default");
        // findInitialModel: a saved default that does not exist, or whose provider has no configured auth, falls back to the first
        // available model instead of being refused.
        foreach (var (provider, id, what) in new[] { ("openai", "no-such-model", "unknown default"), ("anthropic", "claude-sonnet-4-5", "default without auth") })
        {
            var saved = new PiSharp.CodingAgent.Configuration.StartupSettingsSnapshot(JsonData.Parse(JsonSerializer.Serialize(new { defaultProvider = provider, defaultModel = id })),
                PiSharp.Agent.AgentPendingInputMode.OneAtATime, PiSharp.Agent.AgentPendingInputMode.OneAtATime, []);
            var fallen = await new SettingsModelSelection(null, null, null).ResolveAsync(saved, runtime, null, false, CancellationToken.None);
            Equal("openai/gpt-5.5", fallen.Model.Provider + "/" + fallen.Model.Id, what);
        }
        var none = await ThrowsAsync<LiveSessionException>(() => new SettingsModelSelection(null, null, null)
            .ResolveAsync(null, new LiveSessionRuntime(Env(), () => null), null, false, CancellationToken.None), "nothing available");
        Check(none.Code == "NoLiveModel" && none.Message.StartsWith("No models available. Use /login", StringComparison.Ordinal), none.Message);
    }

    private static void LiveLegacyParse()
    {
        var parsed = LiveSessionSelection.Parse("deepseek", "deepseek-v4-pro", null);
        Check(parsed.Model == new ModelDescriptor("deepseek-v4-pro", "openai-completions", "deepseek") && parsed.Entry is null, "pinned parse for a new provider");
        Equal("UnknownLiveModel", Throws<LiveSessionException>(() => LiveSessionSelection.Parse("openai", "gpt-4o-mini-missing", null), "exact only").Code, "exact");
        Throws<SessionCommandException>(() => LiveSessionSelection.Parse("not-a-provider", "x", null), "unknown provider");
        Equal("google-vertex", LiveSessionSelection.Parse("google-vertex", "gemini-3.1-pro-preview", null).Model.Api, "vertex route (IMPL-A1)");
    }
}
