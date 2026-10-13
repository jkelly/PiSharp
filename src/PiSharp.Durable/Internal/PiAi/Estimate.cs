// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/utils/estimate.ts (calculateContextTokens, estimateMessageTokens and
// their helpers) over pi-ai wire JSON. JavaScript string lengths are UTF-16 code units, as .NET's are. Compaction (P11) and generation's
// threshold check use them.
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Internal.PiAi;

/// <summary>pi-ai token estimates (upstream <c>utils/estimate.ts</c>).</summary>
internal static class PiAiEstimate
{
    private const double CharsPerToken = 3.5;
    private const double EstimatedImageChars = 4800;

    /// <summary><c>usage.totalTokens || input + output + cacheRead + cacheWrite</c> (upstream <c>calculateContextTokens</c>).</summary>
    public static double CalculateContextTokens(JsonObjectValue usage)
    {
        var total = Number(usage, "totalTokens");
        // `||`: 0 and NaN are falsy.
        return total != 0 && !double.IsNaN(total)
            ? total
            : Number(usage, "input") + Number(usage, "output") + Number(usage, "cacheRead") + Number(usage, "cacheWrite");
    }

    /// <summary>Upstream <c>estimateTextTokens</c>.</summary>
    public static double EstimateTextTokens(string text) => Math.Ceiling(text.Length / CharsPerToken);

    /// <summary>Upstream <c>estimateMessageTokens</c>.</summary>
    public static double EstimateMessageTokens(JsonValue message)
    {
        var value = (JsonObjectValue)message;
        var role = value.TryGetValue("role", out var name) && name is JsonStringValue text ? text.Value : "";
        var content = value.TryGetValue("content", out var found) ? found : null;
        if (role == "system")
            return EstimateTextTokens(PiAiText.GetSystemMessageText(value)) + EstimateToolsTokens(value, "toolsAdded") +
                EstimateToolsTokens(value, "toolsRemoved");
        if (role is "user" or "toolResult") return Math.Ceiling(TextAndImageChars(content) / CharsPerToken);
        double chars = 0;
        foreach (var block in content as JsonArrayValue ?? JsonArrayValue.Empty)
        {
            if (block is not JsonObjectValue item) continue;
            var type = item.TryGetValue("type", out var kind) && kind is JsonStringValue t ? t.Value : "";
            if (type == "text") chars += Str(item, "text").Length;
            else if (type == "thinking") chars += Str(item, "thinking").Length;
            else chars += Str(item, "name").Length + (item.TryGetValue("arguments", out var arguments) ? arguments.ToJson() : "undefined").Length;
        }
        return Math.Ceiling(chars / CharsPerToken);
    }

    private static double TextAndImageChars(JsonValue? content)
    {
        if (content is JsonStringValue text) return text.Value.Length;
        double chars = 0;
        foreach (var block in content as JsonArrayValue ?? JsonArrayValue.Empty)
            chars += block is JsonObjectValue item && item.TryGetValue("type", out var type) && type is JsonStringValue { Value: "text" }
                ? Str(item, "text").Length
                : EstimatedImageChars;
        return chars;
    }

    private static double EstimateToolsTokens(JsonObjectValue message, string key) =>
        message.TryGetValue(key, out var tools) && tools is JsonArrayValue { Count: > 0 } list ? EstimateTextTokens(list.ToJson()) : 0;

    private static string Str(JsonObjectValue value, string key) => value.TryGetValue(key, out var field) && field is JsonStringValue text ? text.Value : "";

    private static double Number(JsonObjectValue value, string key) =>
        value.TryGetValue(key, out var field) && field is JsonNumberValue number ? number.Value : double.NaN;
}
