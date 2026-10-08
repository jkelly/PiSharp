// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/settings-manager.ts.
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.CodingAgent.ToolSelection;
using PiSharp.Contracts;

namespace PiSharp.CodingAgent.Configuration;

/// <summary>Pure defaultTools projection. Null means no configured selection.</summary>
public static class StartupToolSelection
{
    public static ImmutableArray<string>? Resolve(JsonData values)
    {
        if (!values.Value.TryGetProperty("defaultTools", out var configured)) return null;
        var entries = configured.ValueKind == JsonValueKind.Array
            ? configured.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()!).ToArray() : Array.Empty<string>();
        var plain = entries.Where(value => !ToolNamePatterns.IsModifier(value)).ToArray();
        return ToolNamePatterns.ApplyModifiers(plain.Length > 0 || entries.Length == 0 ? plain : ToolNamePatterns.DefaultToolNames, entries);
    }
}
