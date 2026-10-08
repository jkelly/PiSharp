// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/env-api-keys.ts and packages/ai/src/utils/provider-env.ts.
using System.Collections.Immutable;

namespace PiSharp.AI.Catalogs;

/// <summary>
/// The environment API key map of every built-in provider (env-api-keys.ts). Lookups take a caller-supplied reader; an empty
/// value is absent, as with <c>process.env</c> truthiness. Nothing here reads the process environment by itself.
/// </summary>
public static class ProviderEnvironmentKeys
{
    public const string AnthropicAuthToken = "ANTHROPIC_AUTH_TOKEN";
    public const string AnthropicOAuthToken = "ANTHROPIC_OAUTH_TOKEN";
    public const string AnthropicApiKey = "ANTHROPIC_API_KEY";
    /// <summary>The marker upstream returns for ambient credentials (Vertex ADC, the AWS credential chain).</summary>
    public const string AuthenticatedMarker = "<authenticated>";

    private static readonly ImmutableDictionary<string, string> Map = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ant-ling"] = "ANT_LING_API_KEY",
        ["qwen-token-plan"] = "QWEN_TOKEN_PLAN_API_KEY",
        ["qwen-token-plan-cn"] = "QWEN_TOKEN_PLAN_CN_API_KEY",
        ["qwen-token-plan-individual"] = "QWEN_TOKEN_PLAN_API_KEY",
        ["openai"] = "OPENAI_API_KEY",
        ["azure"] = "AZURE_OPENAI_API_KEY",
        ["nvidia"] = "NVIDIA_API_KEY",
        ["deepseek"] = "DEEPSEEK_API_KEY",
        ["google"] = "GEMINI_API_KEY",
        ["google-vertex"] = "GOOGLE_CLOUD_API_KEY",
        ["groq"] = "GROQ_API_KEY",
        ["cerebras"] = "CEREBRAS_API_KEY",
        ["xai"] = "XAI_API_KEY",
        ["typesafe"] = "TYPESAFE_API_KEY",
        ["radius"] = "RADIUS_API_KEY",
        ["openrouter"] = "OPENROUTER_API_KEY",
        ["vercel-ai-gateway"] = "AI_GATEWAY_API_KEY",
        ["zai"] = "ZAI_API_KEY",
        ["zai-coding-cn"] = "ZAI_CODING_CN_API_KEY",
        ["mistral"] = "MISTRAL_API_KEY",
        ["minimax"] = "MINIMAX_API_KEY",
        ["minimax-cn"] = "MINIMAX_CN_API_KEY",
        ["moonshotai"] = "MOONSHOT_API_KEY",
        ["moonshotai-cn"] = "MOONSHOT_API_KEY",
        ["huggingface"] = "HF_TOKEN",
        ["fireworks"] = "FIREWORKS_API_KEY",
        ["together"] = "TOGETHER_API_KEY",
        ["baseten"] = "BASETEN_API_KEY",
        ["opencode"] = "OPENCODE_API_KEY",
        ["opencode-go"] = "OPENCODE_API_KEY",
        ["kimi-coding"] = "KIMI_API_KEY",
        ["meta"] = "META_API_KEY",
        ["cloudflare-workers-ai"] = "CLOUDFLARE_API_KEY",
        ["cloudflare-ai-gateway"] = "CLOUDFLARE_API_KEY",
        ["xiaomi"] = "XIAOMI_API_KEY",
        ["xiaomi-token-plan-cn"] = "XIAOMI_TOKEN_PLAN_CN_API_KEY",
        ["xiaomi-token-plan-ams"] = "XIAOMI_TOKEN_PLAN_AMS_API_KEY",
        ["xiaomi-token-plan-sgp"] = "XIAOMI_TOKEN_PLAN_SGP_API_KEY",
    }.ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>Source getApiKeyEnvVars: the API key variables of a provider, or null for providers without one.</summary>
    public static IReadOnlyList<string>? GetApiKeyVariables(string provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider == "github-copilot") return ImmutableArray.Create("COPILOT_GITHUB_TOKEN");
        // ANTHROPIC_AUTH_TOKEN participates in discovery and status; GetEnvironmentApiKey skips it (it travels as a Bearer header).
        if (provider == "anthropic") return ImmutableArray.Create(AnthropicAuthToken, AnthropicOAuthToken, AnthropicApiKey);
        return Map.TryGetValue(provider, out var name) ? ImmutableArray.Create(name) : (IReadOnlyList<string>?)null;
    }

    /// <summary>Source findEnvKeys: the configured API key variables (never ambient AWS or Google credentials), or null.</summary>
    public static IReadOnlyList<string>? FindEnvironmentKeys(string provider, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (GetApiKeyVariables(provider) is not { } names) return null;
        var found = names.Where(name => Value(environment, name) is not null).ToImmutableArray();
        return found.IsEmpty ? null : (IReadOnlyList<string>)found;
    }

    /// <summary>Source getEnvApiKey: the provider's environment API key, <see cref="AuthenticatedMarker"/> for ambient Vertex ADC or
    /// AWS credentials, else null. <paramref name="fileExists"/> and <paramref name="home"/> serve the Vertex ADC file probe.</summary>
    public static string? GetEnvironmentApiKey(string provider, Func<string, string?> environment, Func<string, bool>? fileExists = null, string? home = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (FindEnvironmentKeys(provider, environment) is { } keys)
        {
            var name = provider == "anthropic" ? keys.FirstOrDefault(key => key != AnthropicAuthToken) : keys[0];
            if (name is not null) return Value(environment, name);
        }
        if (provider == "google-vertex")
        {
            var exists = fileExists ?? File.Exists;
            var explicitPath = Value(environment, "GOOGLE_APPLICATION_CREDENTIALS");
            var credentials = explicitPath is not null ? exists(explicitPath) :
                exists(Path.Combine(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "gcloud", "application_default_credentials.json"));
            var project = Value(environment, "GOOGLE_CLOUD_PROJECT") ?? Value(environment, "GCLOUD_PROJECT");
            if (credentials && project is not null && Value(environment, "GOOGLE_CLOUD_LOCATION") is not null) return AuthenticatedMarker;
        }
        if (provider == "amazon-bedrock" && (Value(environment, "AWS_PROFILE") is not null ||
            Value(environment, "AWS_ACCESS_KEY_ID") is not null && Value(environment, "AWS_SECRET_ACCESS_KEY") is not null ||
            Value(environment, "AWS_BEARER_TOKEN_BEDROCK") is not null || Value(environment, "AWS_CONTAINER_CREDENTIALS_RELATIVE_URI") is not null ||
            Value(environment, "AWS_CONTAINER_CREDENTIALS_FULL_URI") is not null || Value(environment, "AWS_WEB_IDENTITY_TOKEN_FILE") is not null))
            return AuthenticatedMarker;
        return null;
    }

    /// <summary>Source getProviderEnvValue: an empty value is absent.</summary>
    public static string? Value(Func<string, string?> environment, string name) =>
        environment(name) is { Length: > 0 } value ? value : null;
}
