using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;

namespace PiSharp.Sessions.Compaction;

/// <summary>Pinned source chars/4 heuristic, with provider usage invalidated by later context edits or compaction.</summary>
public static class SessionCompactionTokenEstimator
{
    public static double EstimateTokens(TranscriptEntry message)
    {
        ArgumentNullException.ThrowIfNull(message); var body = message.WireBody.Value; double chars = 0;
        if (message.Role is "system" or "user" or "custom" or "toolResult")
        {
            chars = Content(body.GetProperty("content"));
            if (message.Role == "system")
            {
                if (body.TryGetProperty("sections", out var sections) && sections.ValueKind == JsonValueKind.Object)
                    foreach (var section in sections.EnumerateObject())
                        if (section.Value.ValueKind == JsonValueKind.String) chars += Length(section.Value);
                if (body.TryGetProperty("toolsAdded", out var tools) && tools.ValueKind != JsonValueKind.Null) chars += SessionSummaryJson.Stringify(tools).Length;
            }
        }
        else if (message.Role == "assistant")
        {
            foreach (var block in body.GetProperty("content").EnumerateArray())
                chars += block.GetProperty("type").GetString() switch
                {
                    "text" => Length(block.GetProperty("text")),
                    "thinking" => Length(block.GetProperty("thinking")),
                    "toolCall" => Length(block.GetProperty("name")) + SessionSummaryJson.Stringify(block.GetProperty("arguments")).Length,
                    _ => 0
                };
        }
        else if (message.Role == "bashExecution") chars = Length(body.GetProperty("command")) + Length(body.GetProperty("output"));
        else if (message.Role is "branchSummary" or "compactionSummary") chars = Length(body.GetProperty("summary"));
        return Math.Ceiling(chars / 4);
    }
    public static SessionContextUsageEstimate EstimateContextTokens(ImmutableArray<TranscriptEntry> messages)
    {
        if (messages.IsDefault) throw new SessionCompactionException(SessionCompactionFailure.InvalidMessage);
        double usageTokens = 0; int? last = null;
        for (var i = messages.Length - 1; i >= 0; i--)
        {
            var message = messages[i]; var body = message.WireBody.Value;
            if (message.Role != "assistant" || !body.TryGetProperty("stopReason", out var stop) || stop.GetString() is "error" or "aborted" ||
                !body.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) continue;
            var total = Number(usage, "totalTokens");
            var candidate = total != 0 ? total : Number(usage, "input") + Number(usage, "output") + Number(usage, "cacheRead") + Number(usage, "cacheWrite");
            if (candidate <= 0) continue;
            usageTokens = candidate; last = i; break;
        }
        double trailing = 0; for (var i = last is null ? 0 : last.Value + 1; i < messages.Length; i++) trailing += EstimateTokens(messages[i]);
        if (!double.IsFinite(usageTokens + trailing)) throw new SessionCompactionException(SessionCompactionFailure.UnsupportedNumber);
        return new(usageTokens + trailing, usageTokens, trailing, last);
    }
    public static SessionContextUsageEstimate EstimateProjectedContextTokens(SessionContextProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection); var estimate = EstimateContextTokens(projection.Messages);
        if (estimate.LastUsageIndex is { } index)
        {
            var current = 0; string? usageEntry = null;
            foreach (var entry in projection.ContextEntries)
            { current += entry.Messages.Length; if (index < current) { usageEntry = entry.SourceEntry.Id; break; } }
            var usageIndex = -1; var invalidating = -1;
            for (var i = 0; i < projection.Ancestry.Length; i++)
            {
                if (projection.Ancestry[i].Id == usageEntry) usageIndex = i;
                if (projection.Ancestry[i].Kind is SessionEntryKind.ContextEdit or SessionEntryKind.Compaction) invalidating = i;
            }
            if (usageIndex > invalidating) return estimate;
        }
        double tokens = 0; var system = new SessionSystemReplay().Replay(projection.Messages).CurrentMessage;
        if (system is not null) tokens += EstimateTokens(system);
        foreach (var message in projection.Messages) if (message.Role != "system") tokens += EstimateTokens(message);
        return new(tokens, 0, tokens, null);
    }
    public static bool ShouldCompact(double contextTokens, double contextWindow, SessionCompactionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings); settings.Validate();
        if (!double.IsFinite(contextTokens) || contextTokens < 0 || !double.IsFinite(contextWindow) || contextWindow <= 0)
            throw new SessionCompactionException(SessionCompactionFailure.InvalidSettings);
        return settings.Enabled && contextTokens > contextWindow - settings.ReserveTokens;
    }
    private static double Content(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return Length(content);
        if (content.ValueKind != JsonValueKind.Array) throw new SessionCompactionException(SessionCompactionFailure.InvalidMessage);
        double chars = 0;
        foreach (var block in content.EnumerateArray())
            if (block.GetProperty("type").GetString() == "image") chars += 4800;
            else if (block.GetProperty("type").GetString() == "text" && block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String) chars += Length(text);
        return chars;
    }
    // A JavaScript string's length in UTF-16 code units, as the source heuristic counts it. JsonElement.GetString rejects an escaped
    // lone surrogate, which JSON.parse keeps (a prompt or tool result may hold one), so the length is counted from the raw JSON text.
    private static int Length(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) return value.GetString()!.Length;
        var raw = value.GetRawText(); var length = 0;
        for (var index = 1; index < raw.Length - 1; index++) { if (raw[index] == '\\') index += raw[index + 1] == 'u' ? 5 : 1; length++; }
        return length;
    }
    private static double Number(JsonElement body, string key)
    {
        if (!body.TryGetProperty(key, out var token) || !token.TryGetDouble(out var number) || !double.IsFinite(number) || number < 0)
            throw new SessionCompactionException(SessionCompactionFailure.UnsupportedNumber);
        return number;
    }
}
