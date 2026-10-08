using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.CodingAgent.Configuration;

/// <summary>Pure v0.99.1 defaultTools projection. Null means no configured selection.</summary>
public static class StartupToolSelection
{
    public static ImmutableArray<string>? Resolve(JsonData values)
    {
        if (!values.Value.TryGetProperty("defaultTools", out var configured)) return null;
        var entries = configured.ValueKind == JsonValueKind.Array
            ? configured.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()!).ToArray() : Array.Empty<string>();
        static bool Modifier(string value) => value.StartsWith('+') || value.StartsWith('-');
        var plain = entries.Where(value => !Modifier(value)).ToList();
        var tools = plain.Count > 0 || entries.Length == 0 ? plain : new List<string> { "read", "bash", "edit", "write" };
        foreach (var entry in entries.Where(Modifier))
        {
            var name = entry[1..]; var index = tools.IndexOf(name);
            if (entry[0] == '+' && index < 0 && name.Length > 0) tools.Add(name);
            else if (entry[0] == '-' && index >= 0) tools.RemoveAt(index);
        }
        return tools.ToImmutableArray();
    }
}
