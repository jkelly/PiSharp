using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.AI;

public static class ThinkingLevels
{
    public static ImmutableArray<string> Ordered { get; } = ["off", "minimal", "low", "medium", "high", "xhigh", "max"];

    public static ImmutableArray<string> GetSupported(IChatTransport transport, ModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(model);
        ImmutableArray<string> levels = transport is IThinkingLevelTransport capability ? capability.GetSupportedThinkingLevels(model) : ["off"];
        if (levels.IsDefaultOrEmpty || !levels.SequenceEqual(Ordered.Where(level => levels.Contains(level, StringComparer.Ordinal)), StringComparer.Ordinal))
            throw new ArgumentException("Invalid native thinking capability.");
        return levels;
    }

    /// <summary>Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT) packages/ai/src/models.ts clampThinkingLevel: the requested level when
    /// supported, else the nearest supported level above it, else the nearest below, else the first supported level.</summary>
    public static string Clamp(ImmutableArray<string> available, string level)
    {
        ArgumentNullException.ThrowIfNull(level);
        if (available.IsDefaultOrEmpty) return "off";
        if (available.Contains(level, StringComparer.Ordinal)) return level;
        var requested = Ordered.IndexOf(level);
        if (requested < 0) return available[0];
        for (var index = requested; index < Ordered.Length; index++) if (available.Contains(Ordered[index], StringComparer.Ordinal)) return Ordered[index];
        for (var index = requested - 1; index >= 0; index--) if (available.Contains(Ordered[index], StringComparer.Ordinal)) return Ordered[index];
        return available[0];
    }

    public static void Validate(IChatTransport transport, ModelDescriptor model, string level)
    {
        if (!GetSupported(transport, model).Contains(level, StringComparer.Ordinal))
            throw new ArgumentException("Unsupported native thinking level.", nameof(level));
    }
}
