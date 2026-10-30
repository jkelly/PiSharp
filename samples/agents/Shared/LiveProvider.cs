using System.Text.Json;
using PiSharp.AI.Providers;
using PiSharp.Contracts;

namespace PiSharp.Samples;

public static class LiveProvider
{
    public static string DefaultApi(string provider) => provider switch
    {
        "openai" => "openai-responses",
        "anthropic" => "anthropic-messages",
        _ => "openai-completions"
    };

    public static NativeHttpModelProvider Create(SampleContext context, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(context.Model)) throw new ArgumentException("Live mode requires --model or PISHARP_SAMPLE_MODEL.");
        var key = Environment.GetEnvironmentVariable(context.KeyEnvironment);
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException($"Credential environment variable {context.KeyEnvironment} is not set.");
        var model = new ModelDescriptor(context.Model, context.Api, context.Provider);
        var baseUrl = context.BaseUrl.Length > 0 ? context.BaseUrl : context.Provider switch
        { "openai" => "https://api.openai.com/v1", "openrouter" => "https://openrouter.ai/api/v1", "anthropic" => "https://api.anthropic.com", _ => throw new ArgumentException("Custom providers require --base-url.") };
        var uri = new Uri(baseUrl);
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) throw new ArgumentException("Provider endpoints require HTTPS (or loopback HTTP).");
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) throw new ArgumentException("Provider base URLs must not include credentials, query strings, or fragments.");
        var metadata = JsonData.Parse(JsonSerializer.Serialize(new { id = context.Model, name = context.Model, api = context.Api, provider = context.Provider,
            baseUrl, reasoning = false, input = new[] { "text", "image" }, cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0 }, contextWindow = 128000, maxTokens = 4096 }));
        // Rates here are unknown, not catalog prices. The runner reports provider token counts, never an estimated bill.
        return context.Api switch
        {
            "openai-responses" when context.Provider == "openai" => NativeProviderFactory.CreateResponses(model,
                NativeProviderFactory.CatalogEndpoint(baseUrl, "/responses"), key, new(Reasoning: false) { ModelSupportsImages = true }, new(MaxOutputTokens: 4096), handler),
            "openai-completions" when context.Provider is "openai" or "openrouter" => NativeProviderFactory.CreateCompletions(model,
                NativeProviderFactory.CatalogEndpoint(baseUrl, "/chat/completions"), key, new(SupportsDeveloperRole: context.Provider == "openai") { ModelSupportsImages = true },
                new(MaxTokens: 4096, SupportsStore: false, MaxTokensField: "max_tokens"), handler),
            "openai-completions" => NativeProviderFactory.CreateCatalogCompletions(model, baseUrl, key, new(SupportsDeveloperRole: false) { ModelSupportsImages = true },
                new(MaxTokens: 4096, SupportsStore: false, SupportsLongCacheRetention: false, MaxTokensField: "max_tokens"), metadata, handler, thinkingProfile: false),
            "anthropic-messages" when context.Provider == "anthropic" => NativeProviderFactory.CreateAnthropic(model, uri, key, new(4096), null, handler),
            _ => throw new ArgumentException("Use openai-responses, openai-completions, or anthropic-messages with its corresponding provider.")
        };
    }
}
