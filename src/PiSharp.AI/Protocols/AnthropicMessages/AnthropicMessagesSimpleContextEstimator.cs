// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/utils/estimate.ts and packages/ai/src/utils/text.ts.
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Contracts.Compatibility;

namespace PiSharp.AI.Protocols.AnthropicMessages;

internal static class AnthropicMessagesSimpleContextEstimator
{
    // Pi 1.1.0 estimate.ts CHARS_PER_TOKEN; session compaction keeps its own four-character estimate.
    internal const double CharsPerToken = 3.5;
    internal static AnthropicMessagesContextUsageEstimate Estimate(ImmutableArray<TranscriptEntry> messages,
        AnthropicMessagesSimpleOptions options, CancellationToken token)
    {
        if (messages.Length > options.MaximumContextMessages) throw Fail(AnthropicMessagesSimpleFailure.ResourceLimit);
        try
        {
            long characters = 0; var latestPrefix = long.MinValue; int? last = null; var usageTokens = 0d;
            for (var i = 0; i < messages.Length; i++)
            {
                token.ThrowIfCancellationRequested(); var entry = messages[i];
                if (entry is null || entry.WireBody is null) throw Fail(AnthropicMessagesSimpleFailure.InvalidTranscript);
                characters += entry.WireBody.ToString().Length;
                if (characters > options.MaximumContextCharacters) throw Fail(AnthropicMessagesSimpleFailure.ResourceLimit);
                var message = entry.WireBody.Value;
                if (message.GetProperty("role").GetString() != entry.Role) throw Fail(AnthropicMessagesSimpleFailure.InvalidTranscript);
                var timestamp = message.GetProperty("timestamp").GetInt64();
                if (entry.Role == "assistant")
                {
                    var usage = message.GetProperty("usage"); var total = Counter(usage, "totalTokens");
                    if (total == 0) total = Counter(usage, "input") + Counter(usage, "output") + Counter(usage, "cacheRead") + Counter(usage, "cacheWrite");
                    if (timestamp >= latestPrefix && message.GetProperty("stopReason").GetString() is not ("aborted" or "error") && total > 0)
                    { last = i; usageTokens = total; }
                }
                latestPrefix = Math.Max(latestPrefix, timestamp);
            }
            // estimateContextTokens sums JavaScript numbers: a fractional usage count stays a fraction (tokens = usageTokens + trailingTokens).
            var trailing = 0d;
            for (var i = last is { } index ? index + 1 : 0; i < messages.Length; i++)
            { token.ThrowIfCancellationRequested(); trailing += MessageTokens(messages[i].WireBody.Value, options); }
            return last is null ? new(trailing, 0, trailing, null) : new(usageTokens + trailing, usageTokens, trailing, last);
        }
        catch (EcmaScriptJsonProjectionException) { throw Fail(AnthropicMessagesSimpleFailure.InvalidTranscript); }
        catch (OverflowException) { throw Fail(AnthropicMessagesSimpleFailure.ResourceLimit); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw Fail(AnthropicMessagesSimpleFailure.InvalidTranscript); }
    }

    private static int MessageTokens(JsonElement message, AnthropicMessagesSimpleOptions options)
    {
        var role = message.GetProperty("role").GetString(); var content = message.GetProperty("content");
        if (role == "system")
        {
            var parts = new List<string> { content.ValueKind == JsonValueKind.String ? content.GetString()! :
                string.Join("\n", content.EnumerateArray().Where(block => block.GetProperty("type").GetString() == "text")
                    .Select(block => block.GetProperty("text").GetString()!)) };
            if (message.TryGetProperty("sections", out var sections) && sections.ValueKind != JsonValueKind.Null)
            {
                // Object.values follows ECMAScript integer-key ordering.
                using var ordered = JsonDocument.Parse(JsonText(sections, options), PiSharp.Contracts.JsonData.DocumentOptions);
                foreach (var section in ordered.RootElement.EnumerateObject())
                    if (section.Value.ValueKind != JsonValueKind.Null) parts.Add(section.Value.GetString()!);
            }
            var tokens = TextTokens(string.Join("\n\n", parts.Where(part => part.Length > 0)));
            foreach (var name in new[] { "toolsAdded", "toolsRemoved" })
                if (message.TryGetProperty(name, out var tools) && tools.ValueKind != JsonValueKind.Null && tools.GetArrayLength() > 0)
                    tokens = checked(tokens + TextTokens(JsonText(tools, options)));
            return tokens;
        }
        long characters = 0;
        if (role is "user" or "toolResult")
        {
            if (content.ValueKind == JsonValueKind.String) return TextTokens(content.GetString()!);
            foreach (var block in content.EnumerateArray()) characters += block.GetProperty("type").GetString() switch
            { "text" => block.GetProperty("text").GetString()!.Length, "image" => 4800, _ => throw Fail(AnthropicMessagesSimpleFailure.InvalidTranscript) };
        }
        else if (role == "assistant")
            foreach (var block in content.EnumerateArray()) characters += block.GetProperty("type").GetString() switch
            {
                "text" => block.GetProperty("text").GetString()!.Length,
                "thinking" => block.GetProperty("thinking").GetString()!.Length,
                "toolCall" => block.GetProperty("name").GetString()!.Length + JsonText(block.GetProperty("arguments"), options).Length,
                _ => throw Fail(AnthropicMessagesSimpleFailure.InvalidTranscript)
            };
        else throw Fail(AnthropicMessagesSimpleFailure.InvalidTranscript);
        return checked((int)Math.Ceiling(characters / CharsPerToken));
    }
    private static double Counter(JsonElement usage, string name)
    {
        var count = JsonNumber.Read(usage.GetProperty(name));
        if (!double.IsFinite(count) || count < 0) throw Fail(AnthropicMessagesSimpleFailure.InvalidTranscript);
        return count;
    }
    private static int TextTokens(string text) => (int)Math.Ceiling(text.Length / CharsPerToken);
    private static string JsonText(JsonElement value, AnthropicMessagesSimpleOptions options) => EcmaScriptJsonProjection.Project(JsonData.FromElement(value), new(
        MaximumInputCharacters: options.MaximumContextCharacters, MaximumInputBytes: options.MaximumContextCharacters * 4,
        MaximumOutputCharacters: options.MaximumContextCharacters, MaximumOutputBytes: options.MaximumContextCharacters * 4,
        MaximumDepth: PiSharp.Contracts.JsonData.MaximumDepth, MaximumStringCharacters: options.MaximumContextCharacters));
    private static AnthropicMessagesSimpleException Fail(AnthropicMessagesSimpleFailure failure) => new(failure);
}
