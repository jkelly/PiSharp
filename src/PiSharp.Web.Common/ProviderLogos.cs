// Decision 0011 (run W2, WU3), moved out of the hub's Display.ProviderLogo: which bundled Lobe Icons mark (wwwroot/img/providers, MIT; see its
// README and LICENSE) stands for a PiSharp provider id. Providers without a mark get null, and the page shows its own neutral badge (the
// design rules: never letter initials for a recognised brand, a network icon for custom gateways).
namespace PiSharp.Web.Common;

/// <summary>Provider ids to bundled logo marks.</summary>
public static class ProviderLogos
{
    /// <summary>The logo key (a file name under <c>img/providers</c>, without <c>.svg</c>) of <paramref name="provider"/>, or null.</summary>
    public static string? Key(string? provider) => provider switch
    {
        "openai" => "openai",
        "openai-codex" => "codex",
        "anthropic" => "anthropic",
        "google" => "google",
        "google-vertex" => "vertexai",
        "azure" or "azure-openai-responses" => "azure",
        "amazon-bedrock" => "bedrock",
        "github-copilot" => "githubcopilot",
        "openrouter" => "openrouter",
        "xai" => "xai",
        "mistral" => "mistral",
        "deepseek" => "deepseek",
        "lmstudio" => "lmstudio",
        "ollama" => "ollama",
        "cloudflare-workers-ai" or "cloudflare-ai-gateway" => "cloudflare",
        "kimi-coding" or "moonshot" or "moonshotai" or "moonshotai-cn" => "kimi",
        "meta" => "meta",
        _ => null,
    };

    /// <summary>The URL of <paramref name="provider"/>'s logo, or null when there is none.</summary>
    public static string? Url(string? provider) => Key(provider) is { } key ? KeyUrl(key) : null;

    /// <summary>The URL of the logo file named <paramref name="key"/> (as <see cref="Key"/> returns it).</summary>
    public static string KeyUrl(string key) => $"{WebCommonAssets.Root}/img/providers/{key}.svg";
}
