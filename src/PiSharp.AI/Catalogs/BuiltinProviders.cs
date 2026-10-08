// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/providers/all.ts (builtinProviders order), the provider
// factories packages/ai/src/providers/*.ts (id, name, baseUrl, auth and APIs), packages/ai/src/auth/helpers.ts (envApiKeyAuth),
// packages/ai/src/providers/{anthropic,google-vertex,amazon-bedrock,cloudflare-auth}.ts (ambient auth) and
// packages/ai/src/models.ts (checkProviderAuth).
using System.Collections.Immutable;

namespace PiSharp.AI.Catalogs;

/// <summary>How a built-in provider's API-key method resolves (the <c>auth.apiKey</c> of its factory).</summary>
public enum ProviderAuthKind
{
    /// <summary>envApiKeyAuth: a stored key, else the first set variable.</summary>
    EnvironmentKey,
    /// <summary>anthropicApiKeyAuth: stored key, AUTH_TOKEN, OAUTH_TOKEN, API_KEY, then workload identity federation.</summary>
    Anthropic,
    /// <summary>google-vertex vertexAuth: an API key, else ADC with project and location.</summary>
    GoogleVertex,
    /// <summary>amazon-bedrock bedrockAuth: a stored key, bearer token, profile, IAM keys, ECS or web identity.</summary>
    AmazonBedrock,
    /// <summary>cloudflare-auth: API key and account id.</summary>
    CloudflareWorkersAI,
    /// <summary>cloudflare-auth: API key, account id and gateway id.</summary>
    CloudflareAIGateway,
    /// <summary>No API-key method; only an OAuth login authenticates (openai-codex).</summary>
    OAuthOnly
}

/// <summary>A stored <c>auth.json</c> credential as availability sees it. Secrets are never formatted.</summary>
public sealed record ProviderStoredCredential(string Type, string? Key = null, IReadOnlyDictionary<string, string>? Environment = null,
    ImmutableArray<string>? AvailableModelIds = null)
{
    public override string ToString() => $"ProviderStoredCredential ({Type}) [redacted]";
}

/// <summary>One built-in provider: identity, display name, provider base URL, API-key variables, auth kind and APIs.</summary>
public sealed record BuiltinProviderDefinition(string Id, string Name, string? BaseUrl, ImmutableArray<string> ApiKeyVariables,
    ProviderAuthKind Auth, string? OAuthName, ImmutableArray<string> Apis);

/// <summary>The built-in provider table in <c>builtinProviders()</c> order.</summary>
public static class BuiltinProviders
{
    private static BuiltinProviderDefinition P(string id, string name, string? baseUrl, string? variable, string? oauth, params string[] apis) =>
        new(id, name, baseUrl, variable is null ? [] : [variable], ProviderAuthKind.EnvironmentKey, oauth, [.. apis]);

    private const string Completions = "openai-completions", Messages = "anthropic-messages", Responses = "openai-responses";

    public static ImmutableArray<BuiltinProviderDefinition> All { get; } =
    [
        new("amazon-bedrock", "Amazon Bedrock", null, [], ProviderAuthKind.AmazonBedrock, null, ["bedrock-converse-stream"]),
        P("ant-ling", "Ant Ling", "https://api.ant-ling.com/v1", "ANT_LING_API_KEY", null, Completions),
        new("anthropic", "Anthropic", "https://api.anthropic.com",
            [ProviderEnvironmentKeys.AnthropicAuthToken, ProviderEnvironmentKeys.AnthropicOAuthToken, ProviderEnvironmentKeys.AnthropicApiKey],
            ProviderAuthKind.Anthropic, "Anthropic (Claude Pro/Max)", [Messages]),
        P("azure", "Azure", null, "AZURE_OPENAI_API_KEY", null, "azure-openai-responses", Completions),
        P("baseten", "Baseten", "https://inference.baseten.co/v1", "BASETEN_API_KEY", null, Completions),
        P("cerebras", "Cerebras", "https://api.cerebras.ai/v1", "CEREBRAS_API_KEY", null, Completions),
        new("cloudflare-ai-gateway", "Cloudflare AI Gateway", null, ["CLOUDFLARE_API_KEY"], ProviderAuthKind.CloudflareAIGateway, null,
            [Messages, Completions, Responses]),
        new("cloudflare-workers-ai", "Cloudflare Workers AI", null, ["CLOUDFLARE_API_KEY"], ProviderAuthKind.CloudflareWorkersAI, null,
            [Completions, "cloudflare-workers-ai-system-one"]),
        P("deepseek", "DeepSeek", "https://api.deepseek.com", "DEEPSEEK_API_KEY", null, Completions),
        P("fireworks", "Fireworks", "https://api.fireworks.ai/inference", "FIREWORKS_API_KEY", null, Messages, Completions),
        P("github-copilot", "GitHub Copilot", "https://api.individual.githubcopilot.com", "COPILOT_GITHUB_TOKEN", "GitHub Copilot", Messages, Completions, Responses),
        P("google", "Google", "https://generativelanguage.googleapis.com/v1beta", "GEMINI_API_KEY", null, "google-generative-ai"),
        new("google-vertex", "Google Vertex AI", null, ["GOOGLE_CLOUD_API_KEY"], ProviderAuthKind.GoogleVertex, null, ["google-vertex"]),
        P("groq", "Groq", "https://api.groq.com/openai/v1", "GROQ_API_KEY", null, Completions),
        P("huggingface", "Hugging Face", "https://router.huggingface.co/v1", "HF_TOKEN", null, Completions),
        P("kimi-coding", "Kimi For Coding", "https://api.kimi.com/coding", "KIMI_API_KEY", "Kimi Code (subscription)", Messages),
        P("meta", "Meta", "https://api.meta.ai/v1", "META_API_KEY", "Meta (Muse subscription)", Responses),
        P("minimax", "MiniMax", "https://api.minimax.io/anthropic", "MINIMAX_API_KEY", null, Messages),
        P("minimax-cn", "MiniMax CN", "https://api.minimaxi.com/anthropic", "MINIMAX_CN_API_KEY", null, Messages),
        P("mistral", "Mistral", "https://api.mistral.ai", "MISTRAL_API_KEY", null, "mistral-conversations"),
        P("moonshotai", "Moonshot AI", "https://api.moonshot.ai/v1", "MOONSHOT_API_KEY", null, Completions),
        P("moonshotai-cn", "Moonshot AI CN", "https://api.moonshot.cn/v1", "MOONSHOT_API_KEY", null, Completions),
        P("nvidia", "NVIDIA", "https://integrate.api.nvidia.com/v1", "NVIDIA_API_KEY", null, Completions),
        P("openai", "OpenAI", "https://api.openai.com/v1", "OPENAI_API_KEY", "OpenAI (ChatGPT subscription)", Responses, "openai-decisions"),
        new("openai-codex", "OpenAI Codex (legacy)", "https://chatgpt.com/backend-api", [], ProviderAuthKind.OAuthOnly,
            "OpenAI (ChatGPT Plus/Pro)", ["openai-codex-responses"]),
        P("opencode", "OpenCode Zen", null, "OPENCODE_API_KEY", null, Messages, "google-generative-ai", Completions, Responses, "typesafe-system-one"),
        P("opencode-go", "OpenCode Go", null, "OPENCODE_API_KEY", null, Messages, Completions, Responses),
        P("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", "OPENROUTER_API_KEY", "OpenRouter OAuth", Messages, Completions,
            "openrouter-images", "typesafe-system-one"),
        P("qwen-token-plan", "Qwen Token Plan", "https://token-plan.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1", "QWEN_TOKEN_PLAN_API_KEY", null, Completions),
        P("qwen-token-plan-cn", "Qwen Token Plan CN", "https://token-plan.cn-beijing.maas.aliyuncs.com/compatible-mode/v1", "QWEN_TOKEN_PLAN_CN_API_KEY", null, Completions),
        P("qwen-token-plan-individual", "Qwen Token Plan Individual", "https://token-plan.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1",
            "QWEN_TOKEN_PLAN_API_KEY", null, Completions),
        P("radius", "Radius", null, "RADIUS_API_KEY", "Radius", "pi-messages"),
        P("together", "Together", "https://api.together.ai/v1", "TOGETHER_API_KEY", null, Completions),
        P("typesafe", "TypeSafe", null, "TYPESAFE_API_KEY", null, "typesafe-system-one"),
        P("vercel-ai-gateway", "Vercel AI Gateway", "https://ai-gateway.vercel.sh", "AI_GATEWAY_API_KEY", null, Messages, "typesafe-system-one"),
        P("xai", "xAI", "https://api.x.ai/v1", "XAI_API_KEY", "xAI (Grok/X subscription)", Responses),
        P("xiaomi", "Xiaomi", "https://api.xiaomimimo.com/v1", "XIAOMI_API_KEY", null, Completions),
        P("xiaomi-token-plan-ams", "Xiaomi Token Plan AMS", "https://token-plan-ams.xiaomimimo.com/v1", "XIAOMI_TOKEN_PLAN_AMS_API_KEY", null, Completions),
        P("xiaomi-token-plan-cn", "Xiaomi Token Plan CN", "https://token-plan-cn.xiaomimimo.com/v1", "XIAOMI_TOKEN_PLAN_CN_API_KEY", null, Completions),
        P("xiaomi-token-plan-sgp", "Xiaomi Token Plan SGP", "https://token-plan-sgp.xiaomimimo.com/v1", "XIAOMI_TOKEN_PLAN_SGP_API_KEY", null, Completions),
        P("zai", "Z.AI", "https://api.z.ai/api/coding/paas/v4", "ZAI_API_KEY", null, Completions),
        P("zai-coding-cn", "Z.AI Coding CN", "https://open.bigmodel.cn/api/coding/paas/v4", "ZAI_CODING_CN_API_KEY", null, Completions),
    ];

    private static readonly ImmutableDictionary<string, BuiltinProviderDefinition> ById =
        All.ToImmutableDictionary(provider => provider.Id, StringComparer.Ordinal);

    public static bool TryGet(string id, out BuiltinProviderDefinition definition) => ById.TryGetValue(id, out definition!);

    /// <summary>Source checkProviderAuth for a built-in provider without overlays: the auth source label, or null when the provider has no
    /// usable credential. Ambient credentials are only probed (never acquired); no secret is returned.</summary>
    public static string? CheckAuth(BuiltinProviderDefinition provider, ProviderStoredCredential? stored, Func<string, string?> environment,
        Func<string, bool>? fileExists = null, string? home = null)
    {
        ArgumentNullException.ThrowIfNull(provider); ArgumentNullException.ThrowIfNull(environment);
        if (stored?.Type == "oauth") return provider.OAuthName is null ? null : "OAuth";
        var credential = stored?.Type == "api_key" ? stored : null;
        string? Env(string name) => ProviderEnvironmentKeys.Value(environment, name);
        switch (provider.Auth)
        {
            case ProviderAuthKind.OAuthOnly: return null;
            case ProviderAuthKind.EnvironmentKey:
                if (credential?.Key is { Length: > 0 }) return "stored credential";
                return provider.ApiKeyVariables.FirstOrDefault(name => Env(name) is not null);
            case ProviderAuthKind.Anthropic:
                if (credential?.Key is { Length: > 0 }) return "stored credential";
                foreach (var name in provider.ApiKeyVariables) if (Env(name) is not null) return name;
                return Env("ANTHROPIC_FEDERATION_RULE_ID") is not null && Env("ANTHROPIC_ORGANIZATION_ID") is not null &&
                    Env("ANTHROPIC_IDENTITY_TOKEN_FILE") is not null ? "workload identity federation" : null;
            case ProviderAuthKind.GoogleVertex:
            {
                if (credential?.Key is { Length: > 0 }) return "stored credential";
                if (Env("GOOGLE_CLOUD_API_KEY") is not null) return "GOOGLE_CLOUD_API_KEY";
                string? Scoped(string name) => credential?.Environment is { } scoped && scoped.TryGetValue(name, out var value) ? value : null;
                var adcPath = Scoped("GOOGLE_APPLICATION_CREDENTIALS") ?? Env("GOOGLE_APPLICATION_CREDENTIALS") ??
                    Path.Combine(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "gcloud", "application_default_credentials.json");
                var project = Scoped("GOOGLE_CLOUD_PROJECT") ?? Env("GOOGLE_CLOUD_PROJECT") ?? Env("GCLOUD_PROJECT");
                var location = Scoped("GOOGLE_CLOUD_LOCATION") ?? Env("GOOGLE_CLOUD_LOCATION");
                return (fileExists ?? File.Exists)(adcPath) && project is not null && location is not null
                    ? credential is not null ? "stored credential" : "gcloud application default credentials" : null;
            }
            case ProviderAuthKind.AmazonBedrock:
                if (credential?.Key is { Length: > 0 }) return "stored credential";
                if (Env("AWS_BEARER_TOKEN_BEDROCK") is not null) return "AWS_BEARER_TOKEN_BEDROCK";
                if (credential?.Environment?.TryGetValue("AWS_PROFILE", out var profile) == true && profile is { Length: > 0 }) return "stored credential";
                if (Env("AWS_PROFILE") is not null) return "AWS_PROFILE";
                if (Env("AWS_ACCESS_KEY_ID") is not null && Env("AWS_SECRET_ACCESS_KEY") is not null) return "AWS access keys";
                if (Env("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI") is not null || Env("AWS_CONTAINER_CREDENTIALS_FULL_URI") is not null) return "ECS task role";
                return Env("AWS_WEB_IDENTITY_TOKEN_FILE") is not null ? "web identity token" : null;
            default:
            {
                // cloudflare-auth resolveValue: a stored credential's key and env values win over the environment.
                string? Value(string name) => credential is not null && (name == "CLOUDFLARE_API_KEY" ? credential.Key :
                    credential.Environment?.GetValueOrDefault(name)) is { } stored ? stored : Env(name);
                var gateway = provider.Auth == ProviderAuthKind.CloudflareAIGateway;
                if (string.IsNullOrEmpty(Value("CLOUDFLARE_API_KEY")) || string.IsNullOrEmpty(Value("CLOUDFLARE_ACCOUNT_ID")) ||
                    gateway && string.IsNullOrEmpty(Value("CLOUDFLARE_GATEWAY_ID"))) return null;
                return credential is not null ? "stored credential" : "CLOUDFLARE_API_KEY";
            }
        }
    }
}
