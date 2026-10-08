// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/simple-options.ts (resolveSamplingParams)
// and packages/ai/src/models.ts (getSupportedThinkingLevels/clampThinkingLevel).
using System.Collections.Immutable;
using System.Text.Json;

namespace PiSharp.AI.Protocols;

/// <summary>
/// Source model <c>samplingParamsByThinkingLevel</c> selection for the OpenAI-compatible request factories.
/// Callers merge model <c>samplingParams</c>, then the selected level's parameters, then request parameters, per key.
/// </summary>
internal static class ThinkingLevelSampling
{
    /// <summary>Clamps a requested level to the levels the source model metadata supports.</summary>
    internal static string Clamp(bool reasoning, JsonElement thinkingLevelMap, string level)
    {
        var levels = ThinkingLevels.Ordered;
        ImmutableArray<string> available = !reasoning ? ["off"] : levels.Where(candidate =>
        {
            var present = thinkingLevelMap.ValueKind == JsonValueKind.Object && thinkingLevelMap.TryGetProperty(candidate, out _);
            if (present && thinkingLevelMap.GetProperty(candidate).ValueKind == JsonValueKind.Null) return false;
            return candidate is not ("xhigh" or "max") || present;
        }).ToImmutableArray();
        if (available.Contains(level, StringComparer.Ordinal)) return level;
        var index = levels.IndexOf(level);
        if (index >= 0)
        {
            for (var next = index; next < levels.Length; next++) if (available.Contains(levels[next], StringComparer.Ordinal)) return levels[next];
            for (var previous = index - 1; previous >= 0; previous--) if (available.Contains(levels[previous], StringComparer.Ordinal)) return levels[previous];
        }
        return available.IsEmpty ? "off" : available[0];
    }

    /// <summary>
    /// Selects the parameters of the effective (clamped) level. Returns false for a malformed map: it must be an object whose
    /// values are objects or null. An absent map, level or null entry selects nothing (default element).
    /// </summary>
    internal static bool TrySelect(JsonElement byLevel, bool reasoning, JsonElement thinkingLevelMap, string level, out JsonElement selected)
    {
        selected = default;
        if (byLevel.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return true;
        if (byLevel.ValueKind != JsonValueKind.Object) return false;
        foreach (var entry in byLevel.EnumerateObject())
            if (entry.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)) return false;
        if (byLevel.TryGetProperty(Clamp(reasoning, thinkingLevelMap, level), out var value) && value.ValueKind == JsonValueKind.Object) selected = value;
        return true;
    }
}
