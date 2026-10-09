// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/core/session-manager.ts (buildSessionPath, buildContextEntries,
// sessionEntryToContextMessages, getLatestCompactionEntry), coding-agent/src/core/messages.ts (createCustomMessage,
// createBranchSummaryMessage, createCompactionSummaryMessage) and coding-agent/src/core/agent-session.ts (parseSkillBlock).
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Cli.Interactive.Mode.Components;

namespace PiSharp.Cli.Interactive.Mode;


/// <summary>Session entries are Pi's JSON entries as the RPC host returns them (get_entries).</summary>
internal static partial class SessionEntries
{
    public static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    public static string? Type(JsonObject entry) => Str(entry["type"]);
    public static string? Id(JsonObject entry) => Str(entry["id"]);
    public static string? ParentId(JsonObject entry) => Str(entry["parentId"]);

    /// <summary>Date.parse of an ISO timestamp in epoch milliseconds (NaN on failure, as <c>new Date(x).getTime()</c>).</summary>
    public static double EpochMs(string? timestamp) =>
        timestamp is not null && DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUnixTimeMilliseconds() : double.NaN;

    private static JsonNode? Timestamp(string? timestamp)
    {
        var ms = EpochMs(timestamp);
        return double.IsNaN(ms) ? null : JsonValue.Create((long)ms);
    }

    public static JsonObject? GetLatestCompactionEntry(IReadOnlyList<JsonObject> entries)
    {
        for (var i = entries.Count - 1; i >= 0; i--) if (Type(entries[i]) == "compaction") return entries[i];
        return null;
    }

    public static List<JsonObject> BuildSessionPath(IReadOnlyList<JsonObject> entries, string? leafId, bool leafIsNull = false)
    {
        if (leafIsNull) return [];
        var index = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var entry in entries) if (Id(entry) is { } id) index[id] = entry;
        JsonObject? leaf = null;
        if (leafId is not null) index.TryGetValue(leafId, out leaf);
        leaf ??= entries.Count > 0 ? entries[^1] : null;
        if (leaf is null) return [];
        var path = new List<JsonObject>();
        var visited = new HashSet<JsonObject>(ReferenceEqualityComparer.Instance);
        for (var current = leaf; current is not null && visited.Add(current);)
        {
            path.Add(current);
            current = ParentId(current) is { } parent && index.TryGetValue(parent, out var next) ? next : null;
        }
        path.Reverse();
        return path;
    }

    public static List<JsonObject> BuildContextEntries(IReadOnlyList<JsonObject> entries, string? leafId, bool leafIsNull = false)
    {
        var path = BuildSessionPath(entries, leafId, leafIsNull);
        JsonObject? compaction = null;
        foreach (var entry in path) if (Type(entry) == "compaction") compaction = entry;
        if (compaction is null) return path;
        var compactionIdx = path.FindIndex(entry => Id(entry) == Id(compaction));
        if (compactionIdx < 0) return path;
        var context = new List<JsonObject> { compaction };
        var foundFirstKept = false;
        var firstKept = Str(compaction["firstKeptEntryId"]);
        for (var i = 0; i < compactionIdx; i++)
        {
            var entry = path[i];
            if (Id(entry) == firstKept) foundFirstKept = true;
            if (foundFirstKept && !(Type(entry) == "message" && Str(entry["message"]?["role"]) == "system")) context.Add(entry);
        }
        context.AddRange(path.Skip(compactionIdx + 1));
        return context;
    }

    public static JsonObject CreateCustomMessage(string customType, JsonNode? content, bool display, JsonNode? details, string? timestamp)
    {
        var message = new JsonObject { ["role"] = "custom", ["customType"] = customType, ["content"] = content?.DeepClone() ?? new JsonArray(), ["display"] = display };
        if (details is not null) message["details"] = details.DeepClone();
        message["timestamp"] = Timestamp(timestamp);
        return message;
    }

    public static JsonObject CreateBranchSummaryMessage(string summary, string? fromId, string? timestamp) =>
        new() { ["role"] = "branchSummary", ["summary"] = summary, ["fromId"] = fromId, ["timestamp"] = Timestamp(timestamp) };

    public static JsonObject CreateCompactionSummaryMessage(string summary, double tokensBefore, string? timestamp) =>
        new() { ["role"] = "compactionSummary", ["summary"] = summary, ["tokensBefore"] = tokensBefore, ["timestamp"] = Timestamp(timestamp) };

    public static List<JsonObject> SessionEntryToContextMessages(JsonObject entry)
    {
        switch (Type(entry))
        {
            case "message":
            {
                if (entry["message"] is not JsonObject message) return [];
                var role = Str(message["role"]);
                var copy = (JsonObject)message.DeepClone();
                if (role == "system" && message["content"] is null) { copy["content"] = ""; return [copy]; }
                if (role is "user" or "assistant" or "toolResult" && message["content"] is null) { copy["content"] = new JsonArray(); return [copy]; }
                return [copy];
            }
            case "custom_message":
                return [CreateCustomMessage(Str(entry["customType"]) ?? "", entry["content"] ?? new JsonArray(), entry["display"] is JsonValue d && d.TryGetValue<bool>(out var display) && display,
                    entry["details"], Str(entry["timestamp"]))];
            case "branch_summary" when Str(entry["summary"]) is { Length: > 0 } summary:
                return [CreateBranchSummaryMessage(summary, Str(entry["fromId"]), Str(entry["timestamp"]))];
            case "compaction":
            {
                var summary = CreateCompactionSummaryMessage(Str(entry["summary"]) ?? "", entry["tokensBefore"] is JsonValue t && t.TryGetValue<double>(out var tokens) ? tokens : 0, Str(entry["timestamp"]));
                return entry["systemMessage"] is JsonObject system ? [(JsonObject)system.DeepClone(), summary] : [summary];
            }
            default:
                return [];
        }
    }

    [GeneratedRegex("^<skill name=\"([^\"]+)\" location=\"([^\"]+)\">\\n([\\s\\S]*?)\\n</skill>(?:\\n\\n([\\s\\S]+))?$", RegexOptions.CultureInvariant)]
    private static partial Regex SkillBlockPattern();

    public static ParsedSkillBlock? ParseSkillBlock(string text)
    {
        var match = SkillBlockPattern().Match(text);
        if (!match.Success) return null;
        var userMessage = match.Groups[4].Success ? PiSharp.Tui.Pi.TextUtils.JsTrim(match.Groups[4].Value) : "";
        return new(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value, userMessage.Length > 0 ? userMessage : null);
    }

    /// <summary>The text of a user message: string content, or its text blocks joined.</summary>
    public static string GetUserMessageText(JsonObject message)
    {
        if (Str(message["role"]) != "user") return "";
        if (message["content"] is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        if (message["content"] is not JsonArray blocks) return "";
        return string.Concat(blocks.OfType<JsonObject>().Where(block => Str(block["type"]) == "text").Select(block => Str(block["text"]) ?? ""));
    }
}
