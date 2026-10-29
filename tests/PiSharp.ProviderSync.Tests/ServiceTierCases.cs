// PiSharp-only (decision 0007): service tiers. A request's tier (ChatRequest.ServiceTier) reaches each protocol's own field: OpenAI and
// OpenRouter service_tier on Responses and Chat Completions, Anthropic service_tier and speed (with the fast-mode beta), Google
// serviceTier and Codex service_tier; the request's tier wins over a factory's configured one and combines with session scoping.
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.AI.Protocols.GoogleGenerativeAI;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Contracts;

internal static partial class Program
{
    private static void TierCheck(bool condition, string what) { if (!condition) throw new InvalidOperationException(what); }

    private static JsonElement TierBody(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static Task ServiceTierNames()
    {
        Equal("ultrafast", ServiceTiers.Normalize("Super-Speed")!);
        Equal("economy", ServiceTiers.Normalize("flex")!);
        Equal("normal", ServiceTiers.Normalize("default")!);
        TierCheck(ServiceTiers.Normalize("warp") is null, "unknown tier");
        Equal("default", ServiceTiers.OpenAI("normal")!);
        Equal("flex", ServiceTiers.OpenAI("economy")!);
        Equal("ultrafast", ServiceTiers.OpenAI("ultrafast")!);
        Equal("priority", ServiceTiers.Codex("fast")!);
        TierCheck(ServiceTiers.Codex("normal") is null, "codex normal sends nothing");
        Equal(("standard_only", (string?)null), ServiceTiers.Anthropic("normal"));
        Equal(((string?)null, "fast"), ServiceTiers.Anthropic("ultrafast"));
        Equal("priority", ServiceTiers.Google("fast")!);
        return Task.CompletedTask;
    }

    private static async Task ServiceTierCompletions()
    {
        var model = new ModelDescriptor("tier-model", "openai-completions", "openrouter");
        var endpoint = new Uri("https://openrouter.invalid/api/v1/chat/completions");
        var plain = new CompletionsKeyAuthRequestFactory(endpoint, model, null);
        async Task<JsonElement> Body(CompletionsKeyAuthRequestFactory factory, ChatRequest request)
        {
            using var http = factory.Create(request, Key);
            return TierBody(await http.Content!.ReadAsStringAsync());
        }
        TierCheck(!(await Body(plain, new(model, [Ask], 1))).TryGetProperty("service_tier", out _), "no tier by default");
        Equal("ultrafast", (await Body(plain, new(model, [Ask], 1) { ServiceTier = "ultrafast" })).GetProperty("service_tier").GetString()!);
        var configured = new CompletionsKeyAuthRequestFactory(endpoint, model, null, new() { ServiceTier = "flex" });
        Equal("flex", (await Body(configured, new(model, [Ask], 1))).GetProperty("service_tier").GetString()!);
        var both = await Body(configured, new(model, [Ask], 1) { ServiceTier = "priority", SessionId = "s1" });
        Equal("priority", both.GetProperty("service_tier").GetString()!);
        // Other providers on the OpenAI API (Groq here) take no service_tier values of these names.
        var groq = new ModelDescriptor("llama-tier", "openai-completions", "groq");
        var other = new CompletionsKeyAuthRequestFactory(new("https://groq.invalid/openai/v1/chat/completions"), groq, null);
        TierCheck(!(await Body(other, new(groq, [Ask], 1) { ServiceTier = "priority" })).TryGetProperty("service_tier", out _), "no tier for groq");
    }

    private static async Task ServiceTierResponses()
    {
        var model = new ModelDescriptor("gpt-tier", "openai-responses", "openai");
        var factory = new ResponsesKeyAuthRequestFactory(new("https://responses.invalid/v1/responses"), model, new(true));
        async Task<JsonElement> Body(ChatRequest request)
        {
            using var http = factory.Create(request, Key);
            return TierBody(await http.Content!.ReadAsStringAsync());
        }
        TierCheck(!(await Body(new(model, [Ask], 1))).TryGetProperty("service_tier", out _), "no tier by default");
        Equal("fast", (await Body(new(model, [Ask], 1) { ServiceTier = "fast" })).GetProperty("service_tier").GetString()!);
        Equal("default", (await Body(new(model, [Ask], 1) { ServiceTier = "normal", SessionId = "s2" })).GetProperty("service_tier").GetString()!);
        Equal("flex", (await Body(new(model, [Ask], 1) { ServiceTier = "economy" })).GetProperty("service_tier").GetString()!);
    }

    private static async Task ServiceTierAnthropic()
    {
        var options = new AnthropicMessagesRequestOptions(256);
        var opus = new ModelDescriptor("claude-opus-5-5", "anthropic-messages", "anthropic");
        async Task<(JsonElement Body, string? Beta)> Send(ModelDescriptor model, ChatRequest request)
        {
            var factory = new AnthropicMessagesKeyAuthRequestFactory(new("https://anthropic.invalid"), model, options);
            using var http = factory.Create(request, Key);
            var beta = http.Headers.TryGetValues("anthropic-beta", out var values) ? string.Join(",", values) : null;
            return (TierBody(await http.Content!.ReadAsStringAsync()), beta);
        }
        var plain = await Send(Haiku, new(Haiku, [Ask], 1));
        TierCheck(!plain.Body.TryGetProperty("service_tier", out _) && !plain.Body.TryGetProperty("speed", out _), "nothing by default");
        // Fast mode only on the models that serve it.
        var fast = await Send(opus, new(opus, [Ask], 1) { ServiceTier = "fast" });
        Equal("fast", fast.Body.GetProperty("speed").GetString()!);
        TierCheck(!fast.Body.TryGetProperty("service_tier", out _), "fast mode sends no service_tier");
        TierCheck(fast.Beta?.Contains("fast-mode-2026-02-01", StringComparison.Ordinal) == true, "fast-mode beta: " + fast.Beta);
        var haikuFast = await Send(Haiku, new(Haiku, [Ask], 1) { ServiceTier = "ultrafast" });
        TierCheck(!haikuFast.Body.TryGetProperty("speed", out _), "no speed for a model without fast mode");
        Equal("auto", haikuFast.Body.GetProperty("service_tier").GetString()!);
        TierCheck(haikuFast.Beta?.Contains("fast-mode", StringComparison.Ordinal) != true, "no fast-mode beta for haiku");
        var normal = await Send(Haiku, new(Haiku, [Ask], 1) { ServiceTier = "normal", SessionId = "s3" });
        Equal("standard_only", normal.Body.GetProperty("service_tier").GetString()!);
        TierCheck(!normal.Body.TryGetProperty("speed", out _), "normal is not fast");
        Equal("auto", (await Send(Haiku, new(Haiku, [Ask], 1) { ServiceTier = "priority" })).Body.GetProperty("service_tier").GetString()!);
        // An Anthropic-compatible provider (MiniMax) gets no Anthropic tier fields.
        var minimax = new ModelDescriptor("MiniMax-M3", "anthropic-messages", "minimax");
        var third = await Send(minimax, new(minimax, [Ask], 1) { ServiceTier = "fast" });
        TierCheck(!third.Body.TryGetProperty("speed", out _) && !third.Body.TryGetProperty("service_tier", out _), "no fields for minimax");
    }

    private static async Task ServiceTierGoogle()
    {
        async Task<JsonElement> Body(ChatRequest request, string? configured = null)
        {
            var options = new GoogleGenerativeAIOptions(GoogleMetadata(), Key) { ServiceTier = configured };
            using var http = await new GoogleKeyAuthRequestFactory(Gemini, options).CreateAsync(request);
            return TierBody(await http.Content!.ReadAsStringAsync());
        }
        TierCheck(!(await Body(new(Gemini, [Ask], 1))).TryGetProperty("serviceTier", out _), "no tier by default");
        Equal("flex", (await Body(new(Gemini, [Ask], 1) { ServiceTier = "economy" })).GetProperty("serviceTier").GetString()!);
        Equal("priority", (await Body(new(Gemini, [Ask], 1) { ServiceTier = "fast" }, configured: "flex")).GetProperty("serviceTier").GetString()!);
        Equal("flex", (await Body(new(Gemini, [Ask], 1), configured: "flex")).GetProperty("serviceTier").GetString()!);
    }
}
