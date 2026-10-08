using System.Net;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Providers;
using PiSharp.Contracts;

internal static class NativeThinkingControlTests
{
    public static async Task WireSelection()
    {
        foreach (var route in new[] { "responses", "openrouter", "anthropic", "adaptive" })
        {
            using var handler = new Capture(); var model = Model(route);
            using var provider = Create(route, model, Metadata(model, adaptive: route == "adaptive"), handler);
            Check(ThinkingLevels.GetSupported(provider, model).SequenceEqual(["off", "minimal", "low", "medium", "high"]), "Unexpected native levels.");
            Check(ThinkingLevels.GetSupported(new ModelTransportRegistry([provider]), model).SequenceEqual(ThinkingLevels.GetSupported(provider, model)), "Routing erased thinking capability.");
            foreach (var level in new[] { "low", "high", "off" })
            {
                await Send(provider, new(model, []) { ThinkingLevel = level }, handler);
                using var json = JsonDocument.Parse(handler.Payload!); var raw = json.RootElement;
                if (route == "responses")
                {
                    if (level == "off") Check(raw.GetProperty("reasoning").GetProperty("effort").GetString() == "none" &&
                        !raw.GetProperty("reasoning").TryGetProperty("summary", out _), "Responses off wire mapping was lost.");
                    else
                    {
                        Check(raw.GetProperty("reasoning").GetProperty("effort").GetString() == level, "Responses effort was frozen.");
                        Check(raw.GetProperty("include").EnumerateArray().Any(item => item.GetString() == "reasoning.encrypted_content"), "Responses replay include lost.");
                    }
                    Check(raw.GetProperty("max_output_tokens").GetInt32() == 4096, "Responses output cap changed.");
                }
                else if (route == "openrouter")
                    Check(raw.GetProperty("reasoning").GetProperty("effort").GetString() == (level == "off" ? "none" : level), "OpenRouter wire mapping was lost.");
                else
                {
                    Check(raw.GetProperty("max_tokens").GetInt32() == 4096, "Thinking enlarged the native output cap.");
                    Check(raw.GetProperty("thinking").GetProperty("type").GetString() == (level == "off" ? "disabled" : route == "adaptive" ? "adaptive" : "enabled"), "Anthropic thinking selection was lost.");
                    if (level != "off" && route == "adaptive") Check(raw.GetProperty("output_config").GetProperty("effort").GetString() == level, "Adaptive effort was lost.");
                    if (level != "off" && route == "anthropic") Check(raw.GetProperty("thinking").GetProperty("budget_tokens").GetInt32() == (level == "low" ? 2048 : 3072), "Bounded thinking budget was lost.");
                }
            }
            Check(handler.Calls == 3, "Per-turn controls changed send ownership.");
            provider.Dispose(); Check(!handler.Disposed, "Borrowed handler was disposed.");
        }
    }

    public static Task Admission()
    {
        using var handler = new Capture(); var model = Model("responses");
        using var provider = Create("responses", model, Metadata(model, map: "{\"off\":null,\"medium\":null,\"xhigh\":\"xhigh\"}"), handler);
        Check(ThinkingLevels.GetSupported(provider, model).SequenceEqual(["minimal", "low", "high", "xhigh"]), "Null exclusion/extended admission changed.");
        foreach (var level in new[] { "off", "medium", "max", "invalid" })
            Throws<ArgumentException>(() => provider.StreamAsync(new(model, []) { ThinkingLevel = level }));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Throws<OperationCanceledException>(() => provider.StreamAsync(new(model, []) { ThinkingLevel = "high" }, cancelled.Token));
        using var legacy = NativeProviderFactory.CreateResponses(model, new("https://api.openai.com/v1/responses"), "offline-key", new(true), handler: handler);
        Throws<ArgumentException>(() => legacy.StreamAsync(new(model, []) { ThinkingLevel = "high" }));
        using var nonreasoning = NativeProviderFactory.CreateResponses(model, new("https://api.openai.com/v1/responses"), "offline-key", new(false),
            handler: handler, modelMetadata: Metadata(model, reasoning: false));
        Check(ThinkingLevels.GetSupported(nonreasoning, model).SequenceEqual(["off"]), "Nonreasoning model gained controls.");
        Throws<ArgumentException>(() => nonreasoning.StreamAsync(new(model, []) { ThinkingLevel = "low" }));
        var anthropic = Model("anthropic");
        using var bounded = Create("anthropic", anthropic, Metadata(anthropic), handler, 1024);
        Check(ThinkingLevels.GetSupported(bounded, anthropic).SequenceEqual(["off"]), "Unrepresentable thinking was advertised.");
        Throws<ArgumentException>(() => bounded.StreamAsync(new(anthropic, []) { ThinkingLevel = "low" }));
        Throws<ArgumentException>(() => Create("anthropic", anthropic, Metadata(anthropic, map: "{\"off\":null}"), handler, 1024));
        Throws<ArgumentException>(() => Create("responses", model, Metadata(model with { Id = "wrong" }), handler));
        Check(handler.Calls == 0, "Admission acquired HTTP.");
        return Task.CompletedTask;
    }

    public static async Task MetadataFreeOff()
    {
        foreach (var route in new[] { "responses", "completions", "openrouter", "anthropic", "adaptive" })
        {
            using var handler = new Capture(); var model = Model(route);
            using var provider = route switch
            {
                "responses" => NativeProviderFactory.CreateResponses(model, new("https://api.openai.com/v1/responses"), "offline-key", new(true),
                    new(ReasoningEffort: "high", ReasoningSummary: "detailed", ThinkingLevelMap: JsonData.Parse("{\"off\":\"high\"}")), handler),
                "completions" or "openrouter" => NativeProviderFactory.CreateCompletions(model, new(route == "openrouter"
                    ? "https://openrouter.ai/api/v1/chat/completions" : "https://api.openai.com/v1/chat/completions"), "offline-key", new(Reasoning: true),
                    new(ReasoningEffort: "high") { ThinkingLevelMap = JsonData.Parse("{\"off\":\"high\"}") }, handler),
                _ => NativeProviderFactory.CreateAnthropic(model, new("https://api.anthropic.com/"), "offline-key",
                    new(4096, ModelReasoning: true, ThinkingEnabled: true, ForceAdaptiveThinking: route == "adaptive", ThinkingBudgetTokens: 2048, Effort: "high"), handler: handler)
            };
            Check(ThinkingLevels.GetSupported(provider, model).SequenceEqual(["off"]), "Frozen options invented reasoning capability.");
            foreach (var control in new string?[] { null, "off", null })
            {
                await Send(provider, new(model, []) { ThinkingLevel = control }, handler);
                using var json = JsonDocument.Parse(handler.Payload!); var raw = json.RootElement;
                if (route == "responses")
                {
                    Check(raw.GetProperty("reasoning").GetProperty("effort").GetString() == (control is null ? "high" : "none"), "Responses explicit off retained frozen effort.");
                    Check(raw.GetProperty("reasoning").TryGetProperty("summary", out _) == (control is null), "Responses explicit off retained summary.");
                    Check(raw.TryGetProperty("include", out _) == (control is null), "Responses explicit off retained enabled encrypted include.");
                }
                else if (route == "completions")
                    Check(control is null ? raw.GetProperty("reasoning_effort").GetString() == "high" : !raw.TryGetProperty("reasoning_effort", out _), "Completions explicit off retained frozen effort.");
                else if (route == "openrouter")
                    Check(raw.GetProperty("reasoning").GetProperty("effort").GetString() == (control is null ? "high" : "none"), "OpenRouter explicit off retained frozen effort.");
                else
                {
                    Check(raw.GetProperty("thinking").GetProperty("type").GetString() == (control is not null ? "disabled" : route == "adaptive" ? "adaptive" : "enabled"), "Anthropic explicit off retained enabled thinking.");
                    Check(raw.TryGetProperty("output_config", out _) == (control is null && route == "adaptive"), "Anthropic off retained adaptive effort.");
                    Check(raw.GetProperty("max_tokens").GetInt32() == 4096, "Legacy off changed output cap.");
                }
            }
            Check(handler.Calls == 3, "Null/off selection changed HTTP ownership.");
        }
        using var rejectedHandler = new Capture();
        Throws<ArgumentException>(() => NativeProviderFactory.CreateAnthropic(Model("anthropic"), new("https://api.anthropic.com/"), "offline-key",
            new(4096, ModelReasoning: true, ThinkingEnabled: true, SupportsThinkingOff: false), handler: rejectedHandler));
        var routerModel = Model("openrouter");
        Throws<ArgumentException>(() => NativeProviderFactory.CreateCompletions(routerModel, new("https://openrouter.ai/api/v1/chat/completions"), "offline-key",
            new(Reasoning: true), new(ReasoningEffort: "high") { ModelMetadata = Metadata(routerModel, map: "{\"off\":null}") }, rejectedHandler));
        Check(rejectedHandler.Calls == 0, "Unrepresentable off acquired HTTP.");
    }

    public static async Task EffectiveCompletionsCapability()
    {
        using var handler = new Capture(); var model = Model("openrouter");
        using var generic = Create("openrouter", model, Metadata(model, thinkingFormat: "openai", supportsEffort: false), handler);
        Check(ThinkingLevels.GetSupported(generic, model).SequenceEqual(["off"]), "OpenRouter generic no-effort profile advertised ignored controls.");
        foreach (var level in new[] { "minimal", "low", "medium", "high" })
            Throws<ArgumentException>(() => generic.StreamAsync(new(model, []) { ThinkingLevel = level }));
        Throws<ArgumentException>(() => Create("openrouter", model, Metadata(model, map: "{\"off\":null}", thinkingFormat: "openai", supportsEffort: false), handler));
        Check(handler.Calls == 0, "Unsupported effort admitted HTTP.");
        using var router = Create("openrouter", model, Metadata(model, thinkingFormat: "openrouter", supportsEffort: false), handler);
        await Send(router, new(model, []) { ThinkingLevel = "high" }, handler);
        using (var json = JsonDocument.Parse(handler.Payload!))
            Check(json.RootElement.GetProperty("reasoning").GetProperty("effort").GetString() == "high", "Effective OpenRouter format lost independent reasoning control.");
        using var budget = Create("openrouter", model, Metadata(model, thinkingFormat: "openai", supportsEffort: false, supportsBudget: true), handler);
        await Send(budget, new(model, []) { ThinkingLevel = "high" }, handler);
        using (var json = JsonDocument.Parse(handler.Payload!))
            Check(!json.RootElement.TryGetProperty("reasoning_effort", out _) && json.RootElement.GetProperty("thinking_token_budget").GetDouble() == 3072,
                "Effective budget control was ignored or unsupported effort was emitted.");
        using var zeroBudget = Create("openrouter", model, Metadata(model, thinkingFormat: "openai", supportsEffort: false, supportsBudget: true), handler, 1024);
        Check(ThinkingLevels.GetSupported(zeroBudget, model).SequenceEqual(["off"]), "Zero budget falsely advertised enabled control.");
    }

    public static async Task DiscardedTemplateCapability()
    {
        foreach (var format in new[] { "chat-template", "baseten" })
        {
            using var handler = new Capture(); var model = Model("openrouter");
            var discarded = JsonData.Parse("""{"__proto__":{"$var":"thinking.enabled"}}""");
            using var provider = Create("openrouter", model, Metadata(model, thinkingFormat: format, supportsEffort: false, template: discarded), handler);
            Check(ThinkingLevels.GetSupported(provider, model).SequenceEqual(["off"]), "Discarded template key advertised an enabled control.");
            foreach (var level in new[] { "minimal", "low", "medium", "high" })
                Throws<ArgumentException>(() => provider.StreamAsync(new(model, []) { ThinkingLevel = level }));
            Throws<ArgumentException>(() => Create("openrouter", model, Metadata(model, map: "{\"off\":null}", thinkingFormat: format,
                supportsEffort: false, template: discarded), handler));
            Check(handler.Calls == 0, "Discarded template control acquired HTTP.");
            await Send(provider, new(model, []) { ThinkingLevel = "off" }, handler);
            var field = format == "chat-template" ? "chat_template_kwargs" : "chat_template_args";
            using (var json = JsonDocument.Parse(handler.Payload!))
                Check(!json.RootElement.TryGetProperty(field, out _), "Discarded-only template emitted own fields.");

            var own = JsonData.Parse("""{"__proto__":{"$var":"thinking.enabled"},"enabled":{"$var":"thinking.enabled"},"constructor":{"$var":"thinking.enabled"},"prototype":{"$var":"thinking.effort"}}""");
            using var supported = Create("openrouter", model, Metadata(model, thinkingFormat: format, supportsEffort: false, template: own), handler);
            Check(ThinkingLevels.GetSupported(supported, model).SequenceEqual(["off", "minimal", "low", "medium", "high"]), "Own template controls were suppressed.");
            await Send(supported, new(model, []) { ThinkingLevel = "high" }, handler);
            using (var json = JsonDocument.Parse(handler.Payload!))
            {
                var template = json.RootElement.GetProperty(field);
                Check(!template.TryGetProperty("__proto__", out _) && template.GetProperty("enabled").GetBoolean() &&
                    template.GetProperty("constructor").GetBoolean() && template.GetProperty("prototype").GetString() == "high",
                    "Template own-field capability and projection disagreed.");
            }
        }
    }

    private static ModelDescriptor Model(string route) => new("offline-thinking", route is "anthropic" or "adaptive" ? "anthropic-messages" :
        route == "responses" ? "openai-responses" : "openai-completions", route is "anthropic" or "adaptive" ? "anthropic" : route == "openrouter" ? "openrouter" : "openai");
    private static JsonData Metadata(ModelDescriptor model, bool adaptive = false, string map = "{}", bool reasoning = true,
        string? thinkingFormat = null, bool? supportsEffort = null, bool supportsBudget = false, JsonData? template = null) => JsonData.Parse(JsonSerializer.Serialize(new
    { id = model.Id, api = model.Api, provider = model.Provider, reasoning, contextWindow = 200000, maxTokens = 8192,
        baseUrl = model.Provider == "openrouter" ? "https://openrouter.ai/api/v1" : model.Provider == "anthropic" ? "https://api.anthropic.com" : "https://api.openai.com/v1",
        input = new[] { "text" }, cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0 },
        compat = new { forceAdaptiveThinking = adaptive, thinkingFormat, supportsReasoningEffort = supportsEffort, supportsThinkingTokenBudget = supportsBudget,
            chatTemplateKwargs = thinkingFormat == "chat-template" ? template?.Value : null,
            chatTemplateArgs = thinkingFormat == "baseten" ? template?.Value : null }, thinkingLevelMap = JsonData.Parse(map).Value }));
    private static NativeHttpModelProvider Create(string route, ModelDescriptor model, JsonData metadata, HttpMessageHandler handler, int cap = 4096) => route switch
    {
        "responses" => NativeProviderFactory.CreateResponses(model, new("https://api.openai.com/v1/responses"), "offline-key", new(true),
            new(SupportsMaxOutputTokens: true, MaxOutputTokens: cap), handler, metadata),
        "openrouter" => NativeProviderFactory.CreateCompletions(model, new("https://openrouter.ai/api/v1/chat/completions"), "offline-key", new(Reasoning: true),
            new(MaxTokens: cap, MaxTokensField: "max_tokens", SupportsStore: false), handler, metadata),
        _ => NativeProviderFactory.CreateAnthropic(model, new("https://api.anthropic.com/"), "offline-key", new(cap, ModelReasoning: true),
            new(MaxTokens: cap), handler, metadata)
    };
    private static async Task Send(NativeHttpModelProvider provider, ChatRequest request, Capture handler)
    {
        var calls = handler.Calls;
        handler.Payload = null;
        var frames = new List<StreamEvent>();
        HttpRequestException? rejection = null;
        try { await foreach (var frame in provider.StreamAsync(request)) frames.Add(frame); }
        catch (HttpRequestException error) when (request.Model.Api == "openai-responses" && error.StatusCode == HttpStatusCode.BadRequest)
        { rejection = error; }
        Check(handler.Calls == calls + 1 && handler.Payload is not null, "Expected exactly one captured HTTP 400 request.");
        // Responses propagates the status exception; the other native adapters settle with an error frame.
        if (request.Model.Api == "openai-responses")
        {
            Check(rejection is not null && frames.Count == 1 && frames[0] is StreamStarted,
                "Responses rejection must follow start without a terminal frame.");
            return;
        }
        Check(frames.Count > 0 && frames[^1] is StreamError && frames.OfType<StreamTerminalEvent>().Count() == 1 &&
            frames.Take(frames.Count - 1).All(frame => frame is StreamStarted) && frames.OfType<StreamStarted>().Count() <= 1,
            "HTTP rejection must end with exactly one error and no generated content or successful terminal.");
        var terminal = (StreamError)frames[^1];
        Check(terminal.Reason == StopReason.Error && terminal.Message.StopReason == StopReason.Error &&
            terminal.Message.Api == request.Model.Api && terminal.Message.Provider == request.Model.Provider &&
            terminal.Message.Model == request.Model.Id && terminal.Message.Content.IsEmpty && terminal.NativeCleanupDiagnostic is null,
            "Rejected stream lost its error state, identity, or clean settlement.");
        var properties = terminal.Message.ExtraProperties
            ?? throw new InvalidOperationException("Rejected stream omitted its error message.");
        if (!properties.TryGet("errorMessage", out var message) || message is null)
            throw new InvalidOperationException("Rejected stream omitted its error message.");
        Check(message.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(message.Value.GetString()),
            "Rejected stream omitted its error message.");
        if (request.Model.Api == "openai-completions")
            Check(terminal.NativeDiagnostic is { Adapter: NativeChatAdapter.OpenAICompletions, Code: NativeChatFailureCode.SourceFailed },
                "Completions HTTP rejection lost its source-failure diagnostic.");
        else
        {
            if (!properties.TryGet("anthropicFailure", out var failure) || failure is null)
                throw new InvalidOperationException("Anthropic HTTP rejection lost its source-failure diagnostic.");
            Check(request.Model.Api == "anthropic-messages" && failure.Value.GetString() == "SourceFailed",
                "Anthropic HTTP rejection lost its source-failure diagnostic.");
        }
    }
    private sealed class Capture : HttpMessageHandler
    {
        public int Calls; public string? Payload; public bool Disposed;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; Payload = await request.Content!.ReadAsStringAsync(token); return new(HttpStatusCode.BadRequest) { Content = new StringContent("offline") }; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action run) where T : Exception
    { try { run(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
