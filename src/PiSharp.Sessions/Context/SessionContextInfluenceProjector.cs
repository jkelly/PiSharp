using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;

namespace PiSharp.Sessions.Context;

// Pure stored-entry semantics from buildContextEntries/projectContextEntry and convertToLlm.
internal static class SessionContextInfluenceProjector
{
    private const string CompactionPrefix = "The conversation history before this point was compacted into the following summary:\n\n<summary>\n";
    private const string BranchPrefix = "The following is a summary of a branch that this conversation came back from:\n\n<summary>\n";

    internal static ImmutableArray<SessionEntry> SelectEntries(ImmutableArray<SessionEntry> path, CancellationToken cancellationToken)
    {
        var latest = -1;
        for (var index = 0; index < path.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (path[index].Kind == SessionEntryKind.Compaction) latest = index;
        }
        if (latest < 0) return path;
        var selected = ImmutableArray.CreateBuilder<SessionEntry>(); selected.Add(path[latest]);
        var firstKept = path[latest].WireBody.Value.GetProperty("firstKeptEntryId").GetString();
        var retaining = false;
        for (var index = 0; index < latest; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = path[index];
            if (entry.Id == firstKept) retaining = true;
            if (retaining && !(entry.Kind == SessionEntryKind.Message &&
                entry.WireBody.Value.GetProperty("message").GetProperty("role").GetString() == "system")) selected.Add(entry);
        }
        for (var index = latest + 1; index < path.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested(); selected.Add(path[index]);
        }
        return selected.ToImmutable();
    }

    internal static ImmutableArray<TranscriptEntry> ProjectEntry(SessionEntry entry, JsonElement? replacement, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (replacement is { ValueKind: JsonValueKind.Null }) return [];
        var body = entry.WireBody.Value;
        var result = ImmutableArray.CreateBuilder<TranscriptEntry>();
        switch (entry.Kind)
        {
            case SessionEntryKind.Message:
                Add(JsonData.FromElement(body.GetProperty("message"))); break;
            case SessionEntryKind.CustomMessage:
                Add(JsonData.Parse("{\"role\":\"custom\",\"customType\":" + body.GetProperty("customType").GetRawText() +
                    ",\"content\":" + body.GetProperty("content").GetRawText() + ",\"display\":" + body.GetProperty("display").GetRawText() +
                    (body.TryGetProperty("details", out var details) ? ",\"details\":" + details.GetRawText() : "") +
                    ",\"timestamp\":" + Timestamp(entry.Timestamp) + "}")); break;
            case SessionEntryKind.BranchSummary:
                if (body.GetProperty("summary").GetString()!.Length != 0)
                    Add(JsonData.Parse("{\"role\":\"branchSummary\",\"summary\":" + body.GetProperty("summary").GetRawText() +
                        ",\"fromId\":" + body.GetProperty("fromId").GetRawText() + ",\"timestamp\":" + Timestamp(entry.Timestamp) + "}"));
                break;
            case SessionEntryKind.Compaction:
                if (body.TryGetProperty("systemMessage", out var system) && system.ValueKind != JsonValueKind.Null)
                {
                    // The codec owns this optional subtree but does not validate its known message shape.
                    if (system.ValueKind != JsonValueKind.Object || !system.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String || role.GetString() != "system" ||
                        !system.TryGetProperty("content", out var content) || content.ValueKind is not (JsonValueKind.String or JsonValueKind.Array) ||
                        !system.TryGetProperty("timestamp", out var timestamp) || timestamp.ValueKind != JsonValueKind.Number)
                        throw Failure(SessionContextProjectionFailure.UnsupportedMessage);
                    Add(JsonData.FromElement(system));
                }
                Add(JsonData.Parse("{\"role\":\"compactionSummary\",\"summary\":" + body.GetProperty("summary").GetRawText() +
                    ",\"tokensBefore\":" + body.GetProperty("tokensBefore").GetRawText() + ",\"timestamp\":" + Timestamp(entry.Timestamp) + "}"));
                break;
        }
        return result.ToImmutable();

        void Add(JsonData message)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var role = message.Value.GetProperty("role").GetString()!;
            if (replacement is { } edit && role is ("user" or "assistant" or "toolResult" or "custom"))
            {
                var content = edit.GetProperty("content");
                var raw = content.ValueKind == JsonValueKind.String && role is ("assistant" or "toolResult") ? TextContent(content.GetRawText()) : content.GetRawText();
                message = ReplaceContent(message.Value, raw, cancellationToken);
            }
            result.Add(new(role, message));
        }
    }

    internal static TranscriptEntry? ToLlm(TranscriptEntry message)
    {
        var body = message.WireBody.Value;
        switch (message.Role)
        {
            case "system": case "user": case "assistant": case "toolResult": return message;
            case "custom":
                var content = body.GetProperty("content");
                return User(body, content.ValueKind == JsonValueKind.String ? TextContent(content.GetRawText()) : content.GetRawText());
            case "branchSummary": return User(body, TextContent(JsonSerializer.Serialize(BranchPrefix + body.GetProperty("summary").GetString() + "</summary>")));
            case "compactionSummary": return User(body, TextContent(JsonSerializer.Serialize(CompactionPrefix + body.GetProperty("summary").GetString() + "\n</summary>")));
            case "bashExecution":
                if (OptionalBoolean(body, "excludeFromContext")) return null;
                return User(body, TextContent(JsonSerializer.Serialize(BashText(body))));
            default: return null;
        }
    }

    private static string BashText(JsonElement body)
    {
        var text = new StringBuilder("Ran `").Append(body.GetProperty("command").GetString()).Append("`\n");
        var output = body.GetProperty("output").GetString()!;
        if (output.Length != 0) text.Append("```\n").Append(output).Append("\n```");
        else text.Append("(no output)");
        if (body.GetProperty("cancelled").GetBoolean()) text.Append("\n\n(command cancelled)");
        else if (body.TryGetProperty("exitCode", out var exit) && exit.ValueKind != JsonValueKind.Null)
        {
            var code = exit.GetInt64();
            if (code is < -9_007_199_254_740_991 or > 9_007_199_254_740_991)
                throw Failure(SessionContextProjectionFailure.UnsupportedMessage);
            if (code != 0) text.Append("\n\nCommand exited with code ").Append(code.ToString(CultureInfo.InvariantCulture));
        }
        if (body.TryGetProperty("fullOutputPath", out var path) && path.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw Failure(SessionContextProjectionFailure.UnsupportedMessage);
        if (body.GetProperty("truncated").GetBoolean() && path.ValueKind == JsonValueKind.String && path.GetString()!.Length != 0)
            text.Append("\n\n[Output truncated. Full output: ").Append(path.GetString()).Append(']');
        return text.ToString();
    }

    private static bool OptionalBoolean(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return false;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Failure(SessionContextProjectionFailure.UnsupportedMessage);
        return value.GetBoolean();
    }
    private static TranscriptEntry User(JsonElement original, string content) => new("user", JsonData.Parse(
        "{\"role\":\"user\",\"content\":" + content + ",\"timestamp\":" + original.GetProperty("timestamp").GetRawText() + "}"));
    private static string TextContent(string textJson) => "[{\"type\":\"text\",\"text\":" + textJson + "}]";
    private static JsonData ReplaceContent(JsonElement message, string replacement, CancellationToken cancellationToken)
    {
        var raw = new StringBuilder("{"); var separator = "";
        foreach (var property in message.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            raw.Append(separator).Append(JsonSerializer.Serialize(property.Name)).Append(':')
                .Append(property.Name == "content" ? replacement : property.Value.GetRawText()); separator = ",";
        }
        return JsonData.Parse(raw.Append('}').ToString());
    }
    private static string Timestamp(string value)
    {
        string[] formats = ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"];
        if (!DateTimeOffset.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
            throw Failure(SessionContextProjectionFailure.UnsupportedTimestamp);
        return timestamp.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
    }
    private static SessionContextProjectionException Failure(SessionContextProjectionFailure failure) => new(failure);
}
