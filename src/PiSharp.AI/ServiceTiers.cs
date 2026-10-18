// PiSharp-only (no upstream Pi source): the service tiers a session can select, and their mapping to each provider API's own field.
// OpenAI and OpenRouter: service_tier (auto, default, flex, priority, fast, ultrafast); Anthropic Messages: service_tier (auto,
// standard_only) and speed: "fast"; OpenAI Codex: service_tier (flex, priority; the backend rejects "fast"); Google: serviceTier
// (standard, flex, priority).
using System.Collections.Immutable;

namespace PiSharp.AI;

/// <summary>The service tiers PiSharp offers and their wire values per API.</summary>
public static class ServiceTiers
{
    public const string Auto = "auto";
    public const string Normal = "normal";
    public const string Economy = "economy";
    public const string Priority = "priority";
    public const string Fast = "fast";
    public const string Ultrafast = "ultrafast";

    /// <summary>The tiers, cheapest and slowest first after <c>auto</c>.</summary>
    public static ImmutableArray<string> All { get; } = [Auto, Normal, Economy, Priority, Fast, Ultrafast];

    /// <summary>One-line descriptions for pickers and help.</summary>
    public static string Describe(string tier) => tier switch
    {
        Auto => "provider decides",
        Normal => "standard capacity and price",
        Economy => "flex: about half price, slower, may queue (OpenAI, OpenRouter, Google)",
        Priority => "priority capacity: steadier and faster, about 2x price",
        Fast => "fast mode: highest output speed (OpenAI fast, Anthropic speed:fast)",
        Ultrafast => "super speed: OpenRouter/OpenAI ultrafast endpoints, else fast",
        _ => "",
    };

    /// <summary>The canonical tier a name spells (case-insensitive; also <c>default</c>/<c>standard</c>, <c>flex</c>,
    /// <c>super</c>/<c>superspeed</c>/<c>super-speed</c>/<c>turbo</c>), or null for an unknown name.</summary>
    public static string? Normalize(string? name) => name?.Trim().ToLowerInvariant().Replace('_', '-') switch
    {
        null or "" => null,
        "auto" => Auto,
        "normal" or "default" or "standard" or "off" or "none" => Normal,
        "economy" or "flex" or "cheap" or "eco" => Economy,
        "priority" or "prio" => Priority,
        "fast" or "speed" => Fast,
        "ultrafast" or "ultra" or "super" or "superspeed" or "super-speed" or "turbo" => Ultrafast,
        _ => null,
    };

    /// <summary>OpenAI and OpenRouter <c>service_tier</c> (Responses and Chat Completions).</summary>
    public static string? OpenAI(string? tier) => Normalize(tier) switch
    {
        Auto => "auto", Normal => "default", Economy => "flex", Priority => "priority", Fast => "fast", Ultrafast => "ultrafast", _ => null,
    };

    /// <summary><see cref="OpenAI(string?)"/> for a request to <paramref name="provider"/>: only OpenAI and OpenRouter take these values,
    /// so other providers that speak the OpenAI APIs (Groq, DeepSeek, xAI, local servers) get no <c>service_tier</c>.</summary>
    public static string? OpenAI(string? tier, string? provider) => provider is "openai" or "openrouter" ? OpenAI(tier) : null;

    /// <summary>Whether a model serves Anthropic fast mode (<c>speed: "fast"</c>): Claude Opus 5.5, Opus 5 and Opus 4.8, by Anthropic or
    /// OpenRouter id. Other models reject the field.</summary>
    public static bool SupportsAnthropicFastMode(string? modelId) => modelId is { } id &&
        (id.Contains("opus-5", StringComparison.OrdinalIgnoreCase) || id.Contains("opus-4-8", StringComparison.OrdinalIgnoreCase) ||
         id.Contains("opus-4.8", StringComparison.OrdinalIgnoreCase));

    /// <summary>OpenAI Codex <c>service_tier</c>: <c>priority</c> for every faster tier (the backend rejects <c>fast</c>), <c>flex</c> for
    /// economy, and nothing for auto and normal.</summary>
    public static string? Codex(string? tier) => Normalize(tier) switch
    {
        Economy => "flex", Priority or Fast or Ultrafast => "priority", _ => null,
    };

    /// <summary>Anthropic Messages: <c>service_tier</c> and <c>speed</c>. Fast mode (<c>speed: "fast"</c>) serves fast and ultrafast;
    /// priority uses priority capacity where the organization has it (<c>auto</c>); normal and economy stay on standard capacity.</summary>
    public static (string? ServiceTier, string? Speed) Anthropic(string? tier) => Normalize(tier) switch
    {
        Auto => ("auto", null),
        Normal or Economy => ("standard_only", null),
        Priority => ("auto", null),
        Fast or Ultrafast => (null, "fast"),
        _ => (null, null),
    };

    /// <summary><see cref="Anthropic(string?)"/> for a request to <paramref name="provider"/>'s <paramref name="modelId"/>: only Anthropic
    /// and OpenRouter take the fields (other Anthropic-compatible providers get none), and a model without fast mode gets priority
    /// capacity (<c>service_tier: auto</c>) for fast and ultrafast instead of a <c>speed</c> it would reject.</summary>
    public static (string? ServiceTier, string? Speed) Anthropic(string? tier, string? provider, string? modelId)
    {
        if (provider is not ("anthropic" or "openrouter")) return (null, null);
        var fields = Anthropic(tier);
        return fields.Speed is not null && !SupportsAnthropicFastMode(modelId) ? ("auto", null) : fields;
    }

    /// <summary>Google Gemini <c>serviceTier</c>.</summary>
    public static string? Google(string? tier) => Normalize(tier) switch
    {
        Normal => "standard", Economy => "flex", Priority or Fast or Ultrafast => "priority", _ => null,
    };
}
