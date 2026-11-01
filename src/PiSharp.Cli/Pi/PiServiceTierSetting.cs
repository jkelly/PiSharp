// PiSharp-only (no upstream Pi source): the service tier of the running session (decision 0007). It starts from --tier or the merged
// serviceTier setting, /tier changes it, and every request reads it when it is sent (ChatRequest.ServiceTier).
using PiSharp.AI;

namespace PiSharp.Cli.Pi;

/// <summary>The session's service tier (<see cref="ServiceTiers"/>); null sends none, so providers use their default.</summary>
internal static class PiServiceTierSetting
{
    private static volatile string? s_value;

    /// <summary>Starts the session's tier: an unknown or empty name leaves it unset.</summary>
    internal static void Start(string? tier) => s_value = ServiceTiers.Normalize(tier);

    /// <summary>Sets the tier; null clears it. Throws for an unknown tier name.</summary>
    internal static string? Set(string? tier)
    {
        if (tier is null) { s_value = null; return null; }
        var normalized = ServiceTiers.Normalize(tier) ?? throw new ArgumentException(
            $"Unknown service tier \"{tier}\". Valid tiers: {string.Join(", ", ServiceTiers.All)}.");
        s_value = normalized;
        return normalized;
    }

    internal static string? Current() => s_value;
}
