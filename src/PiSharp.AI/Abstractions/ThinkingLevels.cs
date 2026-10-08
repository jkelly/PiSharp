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

    public static void Validate(IChatTransport transport, ModelDescriptor model, string level)
    {
        if (!GetSupported(transport, model).Contains(level, StringComparer.Ordinal))
            throw new ArgumentException("Unsupported native thinking level.", nameof(level));
    }
}
