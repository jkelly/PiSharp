// Decision 0011 (run W2, WU4): the providers and chat models the Web Agent offers, as the pages need them (two linked dropdowns, Provider ->
// Model; the provider's logo and sign-in kinds; the model's thinking levels, limits, inputs and price). WU4 ships FauxModelCatalog (the faux
// provider, for --faux and until routing lands); WU6's RuntimeModelCatalog builds it from PiSharp.ModelRuntime's built-in catalog. Every
// model listed here must also resolve through IDurableModels.GetModel, or a reopened chat would be routed elsewhere.
namespace PiSharp.WebAgent.Contracts;

/// <summary>The catalog of providers and their chat models. A singleton; safe to call from many threads.</summary>
public interface IModelCatalog
{
    /// <summary>Every provider, in display order.</summary>
    IReadOnlyList<CatalogProvider> Providers { get; }

    /// <summary>The chat models of <paramref name="providerId"/>, in display order; empty for an unknown provider.</summary>
    IReadOnlyList<CatalogModel> Models(string providerId);

    /// <summary>The provider with id <paramref name="providerId"/>, or null.</summary>
    CatalogProvider? FindProvider(string providerId) => Providers.FirstOrDefault(provider => provider.Id == providerId);

    /// <summary>The model <paramref name="modelId"/> of <paramref name="providerId"/>, or null.</summary>
    CatalogModel? FindModel(string providerId, string modelId) => Models(providerId).FirstOrDefault(model => model.Id == modelId);
}

/// <summary>A way to give a provider credentials.</summary>
public enum ProviderAuthKind
{
    /// <summary>No credentials (the faux provider, a loopback server).</summary>
    None,

    /// <summary>An API key the user enters.</summary>
    ApiKey,

    /// <summary>A browser sign-in (OAuth with PKCE), such as ChatGPT for OpenAI Codex.</summary>
    OAuth,

    /// <summary>A device-code sign-in, such as GitHub Copilot.</summary>
    DeviceCode,

    /// <summary>An environment variable (read-only in the app; <c>ProviderEnvironmentKeys</c>).</summary>
    Environment,

    /// <summary>Credentials the provider's SDK finds itself, such as an AWS profile or Google application-default credentials.</summary>
    Ambient,
}

/// <summary>How a provider's use is paid for (the Spend Guard's billing modes).</summary>
public enum BillingKind
{
    /// <summary>Paid per token (API keys); counted in money.</summary>
    Metered,

    /// <summary>Included in a subscription (Codex, Copilot); shown at list price, never counted toward money caps.</summary>
    Subscription,

    /// <summary>Free (the faux provider, loopback servers).</summary>
    Free,

    /// <summary>No known price.</summary>
    Unknown,
}

/// <summary>A provider.</summary>
/// <param name="Id">The PiSharp provider id, such as <c>openai-codex</c>.</param>
/// <param name="DisplayName">The name shown, such as <c>OpenAI Codex</c>.</param>
/// <param name="LogoKey">The bundled logo (<c>PiSharp.Web.Common.ProviderLogos.Key</c>), or null for a neutral badge.</param>
/// <param name="AuthKinds">The ways to sign in, preferred first; empty or <see cref="ProviderAuthKind.None"/> when none is needed.</param>
/// <param name="Billing">How its use is paid for.</param>
public sealed record CatalogProvider(string Id, string DisplayName, string? LogoKey, IReadOnlyList<ProviderAuthKind> AuthKinds, BillingKind Billing);

/// <summary>A model's list price in US dollars per million tokens.</summary>
/// <param name="Input">Input tokens.</param>
/// <param name="Output">Output tokens.</param>
/// <param name="CacheRead">Cached input tokens read.</param>
/// <param name="CacheWrite">Input tokens written to the cache.</param>
public sealed record CatalogCost(double Input, double Output, double CacheRead = 0, double CacheWrite = 0);

/// <summary>A chat model.</summary>
/// <param name="Provider">The provider id.</param>
/// <param name="Id">The model id, unique within its provider.</param>
/// <param name="Name">The name shown.</param>
public sealed record CatalogModel(string Provider, string Id, string Name)
{
    /// <summary>Whether the model reasons (thinking).</summary>
    public bool Reasoning { get; init; }

    /// <summary>The thinking levels it supports, in PiSharp's order (<c>off</c>, <c>minimal</c>, <c>low</c>, <c>medium</c>, <c>high</c>,
    /// <c>xhigh</c>, <c>max</c>); <c>["off"]</c> for a model that does not reason.</summary>
    public IReadOnlyList<string> ThinkingLevels { get; init; } = ["off"];

    /// <summary>The context window, in tokens.</summary>
    public int ContextWindow { get; init; }

    /// <summary>The most output tokens one request may ask for.</summary>
    public int MaxTokens { get; init; }

    /// <summary>The input kinds it accepts: <c>text</c>, and <c>image</c> when it reads images.</summary>
    public IReadOnlyList<string> Input { get; init; } = ["text"];

    /// <summary>The list price, or null when unknown.</summary>
    public CatalogCost? Cost { get; init; }
}
