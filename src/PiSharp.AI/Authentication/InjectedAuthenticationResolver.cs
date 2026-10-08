using System.Collections.Immutable;

namespace PiSharp.AI.Authentication;

/// <summary>Pi v0.99.1 source-specific resolution. No stores, environment reads, providers or OAuth acquisition.</summary>
public static class InjectedAuthenticationResolver
{
    public const string AnthropicAuthToken = "ANTHROPIC_AUTH_TOKEN";
    public const string AnthropicOAuthToken = "ANTHROPIC_OAUTH_TOKEN";
    public const string AnthropicApiKey = "ANTHROPIC_API_KEY";
    private static readonly ImmutableDictionary<string, string> ApiKeyNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["github-copilot"] = "COPILOT_GITHUB_TOKEN",
        ["ant-ling"] = "ANT_LING_API_KEY", ["qwen-token-plan"] = "QWEN_TOKEN_PLAN_API_KEY",
        ["qwen-token-plan-cn"] = "QWEN_TOKEN_PLAN_CN_API_KEY", ["qwen-token-plan-individual"] = "QWEN_TOKEN_PLAN_API_KEY",
        ["openai"] = "OPENAI_API_KEY", ["azure"] = "AZURE_OPENAI_API_KEY", // Pi abe508e1 env-api-keys.ts: renamed from azure-openai-responses in 1.0.3.
        ["nvidia"] = "NVIDIA_API_KEY", ["deepseek"] = "DEEPSEEK_API_KEY", ["google"] = "GEMINI_API_KEY",
        ["google-vertex"] = "GOOGLE_CLOUD_API_KEY", ["groq"] = "GROQ_API_KEY", ["cerebras"] = "CEREBRAS_API_KEY",
        ["xai"] = "XAI_API_KEY", ["typesafe"] = "TYPESAFE_API_KEY", ["radius"] = "RADIUS_API_KEY",
        ["openrouter"] = "OPENROUTER_API_KEY", ["vercel-ai-gateway"] = "AI_GATEWAY_API_KEY", ["zai"] = "ZAI_API_KEY",
        ["zai-coding-cn"] = "ZAI_CODING_CN_API_KEY", ["mistral"] = "MISTRAL_API_KEY", ["minimax"] = "MINIMAX_API_KEY",
        ["minimax-cn"] = "MINIMAX_CN_API_KEY", ["moonshotai"] = "MOONSHOT_API_KEY", ["moonshotai-cn"] = "MOONSHOT_API_KEY",
        ["huggingface"] = "HF_TOKEN", ["fireworks"] = "FIREWORKS_API_KEY", ["together"] = "TOGETHER_API_KEY",
        ["baseten"] = "BASETEN_API_KEY", ["opencode"] = "OPENCODE_API_KEY", ["opencode-go"] = "OPENCODE_API_KEY",
        ["kimi-coding"] = "KIMI_API_KEY", ["meta"] = "META_API_KEY", ["cloudflare-workers-ai"] = "CLOUDFLARE_API_KEY",
        ["cloudflare-ai-gateway"] = "CLOUDFLARE_API_KEY", ["xiaomi"] = "XIAOMI_API_KEY",
        ["xiaomi-token-plan-cn"] = "XIAOMI_TOKEN_PLAN_CN_API_KEY", ["xiaomi-token-plan-ams"] = "XIAOMI_TOKEN_PLAN_AMS_API_KEY",
        ["xiaomi-token-plan-sgp"] = "XIAOMI_TOKEN_PLAN_SGP_API_KEY",
    }.ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>Original findEnvKeys order, including AUTH_TOKEN for discovery. Empty means no configured key.</summary>
    public static ImmutableArray<string> FindEnvKeys(string? provider, ProviderEnvironmentSnapshot environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        IEnumerable<string> names = provider == "anthropic" ? [AnthropicAuthToken, AnthropicOAuthToken, AnthropicApiKey]
            : provider is not null && ApiKeyNames.TryGetValue(provider, out var mappedName) ? [mappedName] : [];
        return names.Where(name => environment.GetValue(name) is not null).ToImmutableArray();
    }

    /// <summary>Environment-only source path. Deliberately skips AUTH_TOKEN; it is never an API key.</summary>
    public static AuthenticationResolution GetEnvApiKey(string? provider, ProviderEnvironmentSnapshot environment)
    {
        foreach (var name in FindEnvKeys(provider, environment))
        {
            if (name == AnthropicAuthToken) continue;
            return Found(AuthenticationKind.ApiKey, name == AnthropicOAuthToken
                ? AuthenticationOrigin.AnthropicOAuthEnvironmentToken : AuthenticationOrigin.EnvironmentApiKey,
                environment.GetValue(name)!, name);
        }
        // The original probes ADC/AWS here. This slice explicitly does not acquire ambient credentials.
        if (provider is "google-vertex" or "amazon-bedrock") return new(AuthenticationDiagnostic.AmbientAuthenticationUnsupported);
        return new(provider == "anthropic" || provider is not null && ApiKeyNames.ContainsKey(provider)
            ? AuthenticationDiagnostic.Missing : AuthenticationDiagnostic.UnknownProvider);
    }

    /// <summary>Original anthropicApiKeyAuth.resolve order, independently of GetEnvApiKey.</summary>
    public static async ValueTask<AuthenticationResolution> ResolveAnthropicApiKeyAsync(IInjectedEnvironmentLookup environment,
        Func<string, CancellationToken, ValueTask<StoredApiKeyCredential?>> storedCredentialLookup,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environment); ArgumentNullException.ThrowIfNull(storedCredentialLookup);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // The original resolve receives an already selected api_key credential. This injected lookup supplies only that type.
            var credential = await storedCredentialLookup("anthropic", cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (credential is { Key: { Length: > 0 } storedKey })
                return Found(AuthenticationKind.ApiKey, AuthenticationOrigin.StoredApiKey, storedKey, credentialEnvironment: credential.Environment);
            foreach (var name in new[] { AnthropicAuthToken, AnthropicOAuthToken, AnthropicApiKey })
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = await environment.ReadAsync(name, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(value)) continue;
                return Found(name == AnthropicAuthToken ? AuthenticationKind.BearerToken : AuthenticationKind.ApiKey,
                    name == AnthropicAuthToken ? AuthenticationOrigin.AnthropicAuthToken
                        : name == AnthropicOAuthToken ? AuthenticationOrigin.AnthropicOAuthEnvironmentToken : AuthenticationOrigin.EnvironmentApiKey,
                    value, name);
            }
            return new(AuthenticationDiagnostic.Missing);
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested && error.CancellationToken == cancellationToken)
        {
            // Injected exception text/inner exceptions may contain credential material.
            throw new OperationCanceledException("Injected authentication resolution cancelled.", cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A foreign/default lookup token does not establish cancellation of the caller.
            return new(AuthenticationDiagnostic.InjectedLookupFailed);
        }
        catch
        {
            // The pinned awaited rejection precedes the later success-path signal check.
            // Do not reclassify an unrelated failure just because the caller cancelled meanwhile.
            return new(AuthenticationDiagnostic.InjectedLookupFailed);
        }
    }

    private static AuthenticationResolution Found(AuthenticationKind kind, AuthenticationOrigin origin, string secret,
        string? environmentName = null, ProviderEnvironmentSnapshot? credentialEnvironment = null) =>
        new(AuthenticationDiagnostic.Resolved, new(kind, origin, secret, environmentName, credentialEnvironment));
}
