using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.CodingAgent;

/// <summary>Metadata uses the existing session observation bus; it does not start a provider operation.
/// The bus generation is not part of the source event payload.</summary>
public sealed record SessionInfoChanged(long OperationGeneration, string? Name) : SessionOperationEvent(OperationGeneration)
{
    public JsonData ToJson() => Name is null
        ? JsonData.Parse("{\"type\":\"session_info_changed\"}")
        : JsonData.Parse(JsonSerializer.Serialize(new { type = "session_info_changed", name = Name }));

    internal static string? Normalize(string value)
    {
        var start = 0; var end = value.Length;
        while (start < end && Space(value[start])) start++;
        while (end > start && Space(value[end - 1])) end--;
        return start == end ? null : value[start..end];
        static bool Space(char character) => character is '\t' or '\n' or '\v' or '\f' or '\r' or ' ' or '\u00A0' or '\u1680' or
            >= '\u2000' and <= '\u200A' or '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000' or '\uFEFF';
    }
}
