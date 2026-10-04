// Pi d86654abb8862e201933517d6f1fce9f88dd117f (MIT): utils/estimate.ts and utils/text.ts.
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Contracts.Compatibility;

namespace PiSharp.AI.Protocols.GoogleGenerativeAI;

internal static class GoogleSimpleContextEstimator
{
    internal static GoogleContextUsageEstimate Estimate(ImmutableArray<TranscriptEntry> messages, GoogleSimpleOptions options)
    {
        if (messages.Length > options.MaximumContextMessages) throw GoogleData.Fail(GoogleFailure.ResourceLimit);
        try
        {
            var latestPrefix = double.NegativeInfinity; int? last = null; var usageTokens = 0d; long characters = 0;
            for (var i = 0; i < messages.Length; i++)
            {
                var entry = messages[i];
                if (entry is null || entry.WireBody is null) throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
                var message = GoogleData.Admit(entry.WireBody, options.DirectOptions).Value;
                if (message.ValueKind != JsonValueKind.Object || GoogleData.String(message, "role") != entry.Role)
                    throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
                characters += entry.WireBody.ToString().Length;
                if (characters > options.MaximumContextCharacters) throw GoogleData.Fail(GoogleFailure.ResourceLimit);
                var timestamp = Number(message, "timestamp");
                if (entry.Role == "assistant")
                {
                    var usage = message.GetProperty("usage"); var total = Number(usage, "totalTokens");
                    if (total == 0) total = Number(usage, "input") + Number(usage, "output") + Number(usage, "cacheRead") + Number(usage, "cacheWrite");
                    if (!double.IsFinite(total)) throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
                    if (timestamp >= latestPrefix && GoogleData.String(message, "stopReason") is not ("aborted" or "error") && total > 0)
                    { last = i; usageTokens = total; }
                }
                latestPrefix = Math.Max(latestPrefix, timestamp);
            }
            var trailing = 0d;
            for (var i = last is { } index ? index + 1 : 0; i < messages.Length; i++) trailing += MessageTokens(messages[i].WireBody.Value, options);
            if (!double.IsFinite(usageTokens + trailing)) throw GoogleData.Fail(GoogleFailure.ResourceLimit);
            return new(usageTokens + trailing, usageTokens, trailing, last);
        }
        catch (EcmaScriptJsonProjectionException error)
        { throw GoogleData.Fail(error.Failure == EcmaScriptJsonProjectionFailure.ResourceLimit ? GoogleFailure.ResourceLimit : GoogleFailure.UnsupportedValue); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw GoogleData.Fail(GoogleFailure.UnsupportedValue); }
    }
    private static double MessageTokens(JsonElement message, GoogleSimpleOptions options)
    {
        var content = message.GetProperty("content"); var role = GoogleData.String(message, "role");
        if (role == "system")
        {
            var parts = new List<string> { content.ValueKind == JsonValueKind.String ? content.GetString()! :
                string.Join("\n", content.EnumerateArray().Where(b => GoogleData.String(b, "type") == "text").Select(b => b.GetProperty("text").GetString()!)) };
            if (message.TryGetProperty("sections", out var sections) && sections.ValueKind != JsonValueKind.Null)
                foreach (var section in sections.EnumerateObject()) if (section.Value.ValueKind != JsonValueKind.Null) parts.Add(section.Value.GetString()!);
            var tokens = TextTokens(string.Join("\n\n", parts.Where(p => p.Length > 0)));
            foreach (var name in new[] { "toolsAdded", "toolsRemoved" })
                if (message.TryGetProperty(name, out var tools) && tools.ValueKind != JsonValueKind.Null && tools.GetArrayLength() > 0)
                    tokens += TextTokens(JsonText(tools, options));
            return tokens;
        }
        if (role is "user" or "toolResult")
        {
            if (content.ValueKind == JsonValueKind.String) return TextTokens(content.GetString()!);
            var characters = 0d;
            foreach (var block in content.EnumerateArray()) characters += GoogleData.String(block, "type") switch
            { "text" => block.GetProperty("text").GetString()!.Length, "image" => 4800, _ => throw GoogleData.Fail(GoogleFailure.UnsupportedValue) };
            return Math.Ceiling(characters / 4);
        }
        if (role != "assistant") throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
        var assistantCharacters = 0d;
        foreach (var block in content.EnumerateArray()) assistantCharacters += GoogleData.String(block, "type") switch
        {
            "text" => block.GetProperty("text").GetString()!.Length,
            "thinking" => block.GetProperty("thinking").GetString()!.Length,
            "toolCall" => block.GetProperty("name").GetString()!.Length + JsonText(block.GetProperty("arguments"), options).Length,
            _ => throw GoogleData.Fail(GoogleFailure.UnsupportedValue)
        };
        return Math.Ceiling(assistantCharacters / 4);
    }
    private static string JsonText(JsonElement value, GoogleSimpleOptions options) => EcmaScriptJsonProjection.Project(JsonData.FromElement(value), new(
        MaximumInputCharacters: options.MaximumContextCharacters, MaximumInputBytes: options.MaximumContextCharacters * 4,
        MaximumOutputCharacters: options.MaximumContextCharacters, MaximumOutputBytes: options.MaximumContextCharacters * 4,
        MaximumDepth: 64, MaximumStringCharacters: options.MaximumContextCharacters));
    private static double TextTokens(string text) => Math.Ceiling(text.Length / 4d);
    internal static double Number(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.Number ||
            !field.TryGetDouble(out var number) || !double.IsFinite(number)) throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
        return number;
    }
}
