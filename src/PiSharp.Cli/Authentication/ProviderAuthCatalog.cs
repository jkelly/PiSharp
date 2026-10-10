// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/providers/all.ts (builtinProviders), each provider's auth
// (providers/*.ts: envApiKeyAuth names and variables, amazon-bedrock.ts bedrockAuth, google-vertex.ts vertexAuth,
// cloudflare-auth.ts cloudflareWorkersAIAuth/cloudflareAIGatewayAuth, the lazyOAuth flows), auth/helpers.ts (envApiKeyAuth login)
// and auth/oauth/load.ts; packages/coding-agent/src/extensions/llama/provider.ts (the built-in llama.cpp extension's api-key auth).
using System.Collections.Immutable;
using PiSharp.AI.Authentication.OAuth;

namespace PiSharp.Cli.Authentication;

/// <summary>How an api-key login prompts: a secret key, Cloudflare's key plus ids, Bedrock's method select, Vertex's, or the llama.cpp
/// server URL and optional key.</summary>
internal enum ApiKeyLoginKind { Secret, CloudflareWorkersAI, CloudflareAIGateway, AmazonBedrock, GoogleVertex, LlamaCpp }

/// <summary>The inputs an OAuth flow is constructed from.</summary>
internal sealed record OAuthFlowContext(HttpMessageInvoker Http, Func<string, string?> Environment, TimeProvider? Time, string? CallbackHost,
    int AnthropicCallbackPort, Func<string, bool>? IsCopilotCatalogModel);

internal sealed record ProviderOAuthDescriptor(string Name, bool IsSubscription, string? LoginLabel, Func<OAuthFlowContext, IProviderOAuth> Create);

/// <summary>One provider's auth: the api-key method (name, environment variables, login prompts) and an optional OAuth method.</summary>
internal sealed record ProviderAuthEntry(string Id, string Name, string ApiKeyName, ImmutableArray<string> EnvironmentVariables,
    ApiKeyLoginKind ApiKeyLogin, ProviderOAuthDescriptor? OAuth);

internal static class ProviderAuthCatalog
{
    private static ProviderAuthEntry Key(string id, string name, string keyName, params string[] variables) =>
        new(id, name, keyName, [.. variables], ApiKeyLoginKind.Secret, null);

    private static ProviderOAuthDescriptor Anthropic() => new("Anthropic (Claude Pro/Max)", true, null,
        context => new AnthropicProviderOAuth(() => new AnthropicOAuth(context.Http, context.Time, context.CallbackHost, context.AnthropicCallbackPort)));

    /// <summary>builtinProviders() order.</summary>
    public static ImmutableArray<ProviderAuthEntry> All { get; } =
    [
        new("amazon-bedrock", "Amazon Bedrock", "AWS credentials or bearer token", ["AWS_BEARER_TOKEN_BEDROCK", "AWS_PROFILE", "AWS_ACCESS_KEY_ID"], ApiKeyLoginKind.AmazonBedrock, null),
        Key("ant-ling", "Ant Ling", "Ant Ling API key", "ANT_LING_API_KEY"),
        new("anthropic", "Anthropic", "Anthropic API key", ["ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_OAUTH_TOKEN", "ANTHROPIC_API_KEY"], ApiKeyLoginKind.Secret, Anthropic()),
        Key("azure", "Azure", "Azure OpenAI API key", "AZURE_OPENAI_API_KEY"),
        Key("baseten", "Baseten", "Baseten API key", "BASETEN_API_KEY"),
        Key("cerebras", "Cerebras", "Cerebras API key", "CEREBRAS_API_KEY"),
        new("cloudflare-ai-gateway", "Cloudflare AI Gateway", "Cloudflare API key", ["CLOUDFLARE_API_KEY"], ApiKeyLoginKind.CloudflareAIGateway, null),
        new("cloudflare-workers-ai", "Cloudflare Workers AI", "Cloudflare API key", ["CLOUDFLARE_API_KEY"], ApiKeyLoginKind.CloudflareWorkersAI, null),
        Key("deepseek", "DeepSeek", "DeepSeek API key", "DEEPSEEK_API_KEY"),
        Key("fireworks", "Fireworks", "Fireworks API key", "FIREWORKS_API_KEY"),
        Key("github-copilot", "GitHub Copilot", "GitHub Copilot token", "COPILOT_GITHUB_TOKEN") with
        { OAuth = new("GitHub Copilot", true, null, context => new GitHubCopilotOAuth(context.Http, context.IsCopilotCatalogModel, context.Time)) },
        Key("google", "Google", "Gemini API key", "GEMINI_API_KEY"),
        new("google-vertex", "Google Vertex AI", "Google Cloud credentials", ["GOOGLE_CLOUD_API_KEY"], ApiKeyLoginKind.GoogleVertex, null),
        Key("groq", "Groq", "Groq API key", "GROQ_API_KEY"),
        Key("huggingface", "Hugging Face", "Hugging Face token", "HF_TOKEN"),
        Key("kimi-coding", "Kimi For Coding", "Kimi API key", "KIMI_API_KEY") with
        { OAuth = new("Kimi Code (subscription)", true, "Sign in with Kimi Code", context => new KimiCodingOAuth(context.Http, context.Environment, context.Time)) },
        Key("meta", "Meta", "Meta Model API key", "META_API_KEY") with
        { OAuth = new("Meta (Muse subscription)", true, "Sign in with Meta", context => new MetaOAuth(context.Http, context.Time)) },
        Key("minimax", "MiniMax", "MiniMax API key", "MINIMAX_API_KEY"),
        Key("minimax-cn", "MiniMax CN", "MiniMax CN API key", "MINIMAX_CN_API_KEY"),
        Key("mistral", "Mistral", "Mistral API key", "MISTRAL_API_KEY"),
        Key("moonshotai", "Moonshot AI", "Moonshot AI API key", "MOONSHOT_API_KEY"),
        Key("moonshotai-cn", "Moonshot AI CN", "Moonshot AI API key", "MOONSHOT_API_KEY"),
        Key("nvidia", "NVIDIA", "NVIDIA API key", "NVIDIA_API_KEY"),
        Key("openai", "OpenAI", "OpenAI API key", "OPENAI_API_KEY") with
        { OAuth = new("OpenAI (ChatGPT subscription)", true, "Sign in with ChatGPT", context => new OpenAIChatGPTOAuth(context.Http, context.Environment, context.Time)) },
        // openai-codex has OAuth only (no api-key auth).
        new("openai-codex", "OpenAI Codex (legacy)", "", [], ApiKeyLoginKind.Secret,
            new("OpenAI (ChatGPT Plus/Pro)", true, null, context => new OpenAICodexOAuth(context.Http, context.Environment, context.Time))),
        Key("opencode", "OpenCode Zen", "OpenCode API key", "OPENCODE_API_KEY"),
        Key("opencode-go", "OpenCode Go", "OpenCode API key", "OPENCODE_API_KEY"),
        Key("openrouter", "OpenRouter", "OpenRouter API key", "OPENROUTER_API_KEY") with
        { OAuth = new("OpenRouter OAuth", false, "Sign in with OpenRouter", context => new OpenRouterOAuth(context.Http, context.Environment, context.Time)) },
        Key("qwen-token-plan", "Qwen Token Plan", "Qwen Token Plan API key", "QWEN_TOKEN_PLAN_API_KEY"),
        Key("qwen-token-plan-cn", "Qwen Token Plan CN", "Qwen Token Plan CN API key", "QWEN_TOKEN_PLAN_CN_API_KEY"),
        Key("qwen-token-plan-individual", "Qwen Token Plan Individual", "Qwen Token Plan Individual API key", "QWEN_TOKEN_PLAN_API_KEY"),
        Key("radius", "Radius", "Radius API key", "RADIUS_API_KEY") with
        { OAuth = new("Radius", false, null, context => new RadiusOAuth(context.Http, "Radius", RadiusOAuth.DefaultGateway, context.Time)) },
        Key("together", "Together", "Together API key", "TOGETHER_API_KEY"),
        Key("typesafe", "TypeSafe", "TypeSafe API key", "TYPESAFE_API_KEY"),
        Key("vercel-ai-gateway", "Vercel AI Gateway", "Vercel AI Gateway API key", "AI_GATEWAY_API_KEY"),
        Key("xai", "xAI", "xAI API key", "XAI_API_KEY") with
        { OAuth = new("xAI (Grok/X subscription)", true, "Sign in with SuperGrok or X Premium", context => new XaiOAuth(context.Http, context.Time)) },
        Key("xiaomi", "Xiaomi", "Xiaomi API key", "XIAOMI_API_KEY"),
        Key("xiaomi-token-plan-ams", "Xiaomi Token Plan AMS", "Xiaomi Token Plan AMS API key", "XIAOMI_TOKEN_PLAN_AMS_API_KEY"),
        Key("xiaomi-token-plan-cn", "Xiaomi Token Plan CN", "Xiaomi Token Plan CN API key", "XIAOMI_TOKEN_PLAN_CN_API_KEY"),
        Key("xiaomi-token-plan-sgp", "Xiaomi Token Plan SGP", "Xiaomi Token Plan SGP API key", "XIAOMI_TOKEN_PLAN_SGP_API_KEY"),
        Key("zai", "Z.AI", "Z.AI API key", "ZAI_API_KEY"),
        Key("zai-coding-cn", "Z.AI Coding CN", "Z.AI Coding CN API key", "ZAI_CODING_CN_API_KEY"),
    ];

    /// <summary>The providers extensions registered with an <c>oauth</c> config in the current run (model-runtime.ts registerProvider:
    /// their OAuth method joins /login and the stored credentials' resolution).</summary>
    private static readonly AsyncLocal<Func<IEnumerable<ProviderAuthEntry>>?> ExtensionEntries = new();
    internal static Func<IEnumerable<ProviderAuthEntry>>? Extensions { get => ExtensionEntries.Value; set => ExtensionEntries.Value = value; }

    /// <summary>The built-in extensions' providers (extensions/index.ts builtInExtensions): llama.cpp's api-key method (provider.ts).</summary>
    public static ImmutableArray<ProviderAuthEntry> BuiltinExtensions { get; } =
    [
        new(PiSharp.Cli.Llama.LlamaCatalog.ProviderId, "llama.cpp", "llama.cpp server", ["LLAMA_BASE_URL"], ApiKeyLoginKind.LlamaCpp, null)
    ];

    /// <summary>Every provider's auth: the built-in ones (an extension's OAuth replacing a built-in provider's), the built-in extensions'
    /// providers, then extension providers.</summary>
    public static IEnumerable<ProviderAuthEntry> Entries()
    {
        var extensions = Extensions?.Invoke().ToList() ?? [];
        foreach (var builtin in All.Concat(BuiltinExtensions)) yield return extensions.FirstOrDefault(entry => entry.Id == builtin.Id) ?? builtin;
        foreach (var extension in extensions) if (!All.Concat(BuiltinExtensions).Any(builtin => builtin.Id == extension.Id)) yield return extension;
    }

    public static ProviderAuthEntry? Find(string id) => Entries().FirstOrDefault(entry => entry.Id == id);

    /// <summary>The built-in provider's auth only.</summary>
    internal static ProviderAuthEntry? Builtin(string id) => All.FirstOrDefault(entry => entry.Id == id);

    /// <summary>A login option: one provider and one auth method (getLoginProviderOptions).</summary>
    internal sealed record LoginOption(ProviderAuthEntry Provider, string AuthType)
    {
        public string Name => Provider.Name;
        public string? LoginLabel => AuthType == "oauth" ? Provider.OAuth?.LoginLabel : null;
    }

    public static IEnumerable<LoginOption> LoginOptions(string? authType = null)
    {
        foreach (var provider in Entries())
        {
            if (provider.OAuth is not null && authType is null or "oauth") yield return new(provider, "oauth");
            if (provider.ApiKeyName.Length != 0 && authType is null or "api_key") yield return new(provider, "api_key");
        }
    }

    /// <summary>findLoginProviderOptions: options whose provider id or name equals the reference, ignoring case.</summary>
    public static List<LoginOption> FindLoginOptions(string reference)
    {
        var normalized = reference.Trim().ToLowerInvariant();
        if (normalized.Length == 0) return [];
        return [.. LoginOptions().Where(option => option.Provider.Id.ToLowerInvariant() == normalized || option.Provider.Name.ToLowerInvariant() == normalized)];
    }

    /// <summary>The provider's display name (Provider.name); unknown ids display as themselves.</summary>
    public static string DisplayName(string id) => Find(id)?.Name ?? id;
}
