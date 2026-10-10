using System.Text;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.Sessions.Serialization;

public enum SessionEntryKind
{
    Header, Message, ThinkingLevelChange, ModelChange, Usage, Compaction, BranchSummary,
    Custom, CustomMessage, ContextEdit, Label, SessionInfo, Unknown
}

public enum SessionEntryCodecFailure
{
    CharacterLimit, Utf8ByteLimit, DepthLimit, MalformedJson, InvalidRecord, UnsupportedVersion,
    DuplicateProperty, UnsupportedUnicode, UnsupportedInteger
}

public sealed record SessionEntryCodecOptions(
    int MaximumRecordCharacters = 1_048_576, int MaximumUtf8Bytes = 4_194_304, int MaximumJsonDepth = 32);

public sealed class SessionEntryCodecException : Exception
{
    public SessionEntryCodecFailure Failure { get; }
    internal SessionEntryCodecException(SessionEntryCodecFailure failure) : base(failure switch
    {
        SessionEntryCodecFailure.CharacterLimit => "Session record exceeds the character limit.",
        SessionEntryCodecFailure.Utf8ByteLimit => "Session record exceeds the UTF-8 byte limit.",
        SessionEntryCodecFailure.DepthLimit => "Session record exceeds the JSON depth limit.",
        SessionEntryCodecFailure.MalformedJson => "Session record is not complete strict JSON.",
        SessionEntryCodecFailure.InvalidRecord => "Session record has an invalid required field shape.",
        SessionEntryCodecFailure.UnsupportedVersion => "Session header must declare version 3.",
        SessionEntryCodecFailure.DuplicateProperty => "Session record contains duplicate JSON properties.",
        SessionEntryCodecFailure.UnsupportedUnicode => "Session record contains unsupported Unicode.",
        SessionEntryCodecFailure.UnsupportedInteger => "Session record integer is outside the native profile.",
        _ => "Session record is invalid."
    }) => Failure = failure;
}

/// <summary>An immutable validated wire record. Unknown data has no projection or execution behavior.</summary>
public sealed class SessionEntry
{
    public SessionEntryKind Kind { get; }
    public string Type { get; }
    public string Id { get; }
    public string? ParentId { get; }
    public string Timestamp { get; }
    public JsonData WireBody { get; }
    public bool IsHeader => Kind == SessionEntryKind.Header;

    internal SessionEntry(SessionEntryKind kind, string type, string id, string? parentId,
        string timestamp, JsonData wireBody)
    {
        Kind = kind; Type = type; Id = id; ParentId = parentId; Timestamp = timestamp; WireBody = wireBody;
    }
}

/// <summary>
/// Pure, bounded codec for one current-version JSON record. It owns no files, tree, migrations or effects.
/// Full JSON retention is independent of the narrower required-field validation profile.
/// </summary>
public sealed class SessionEntryCodec
{
    public const int CurrentVersion = 3;
    private readonly SessionEntryCodecOptions _options;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public SessionEntryCodec(SessionEntryCodecOptions? options = null)
    {
        _options = options ?? new();
        if (_options.MaximumRecordCharacters <= 0 || _options.MaximumUtf8Bytes <= 0 ||
            _options.MaximumJsonDepth is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid session record codec limits.");
    }

    public SessionEntry Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        CheckInput(json);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            return ReadValidated(document.RootElement);
        }
        catch (JsonException) { throw Failure(SessionEntryCodecFailure.MalformedJson); }
    }

    public SessionEntry ParseUtf8(ReadOnlySpan<byte> json)
    {
        if (json.Length > _options.MaximumUtf8Bytes) throw Failure(SessionEntryCodecFailure.Utf8ByteLimit);
        try { return Parse(StrictUtf8.GetString(json)); }
        catch (DecoderFallbackException) { throw Failure(SessionEntryCodecFailure.UnsupportedUnicode); }
    }

    public SessionEntry Read(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined) throw Failure(SessionEntryCodecFailure.InvalidRecord);
        // A caller's document may allow comments or trailing commas. Reparse its retained syntax strictly.
        return Parse(value.GetRawText());
    }

    /// <summary>Returns a compact JSON record without a line terminator, retaining numeric and string tokens.</summary>
    public string Serialize(SessionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // Revalidate against this codec's bounds; an entry may come from a differently configured codec.
        var raw = entry.WireBody.ToString();
        CheckInput(raw);
        var result = new StringBuilder(raw.Length);
        var quoted = false; var escaped = false;
        foreach (var character in raw)
        {
            if (quoted)
            {
                result.Append(character);
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') quoted = false;
            }
            else if (character == '"') { quoted = true; result.Append(character); }
            else if (character is not (' ' or '\t' or '\r' or '\n')) result.Append(character);
        }
        return result.ToString();
    }

    private void CheckInput(string json)
    {
        if (json.Length > _options.MaximumRecordCharacters) throw Failure(SessionEntryCodecFailure.CharacterLimit);
        CheckUnicode(json);
        var quoted = false; var escaped = false; var depth = 0;
        foreach (var character in json)
        {
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') quoted = false;
            }
            else if (character == '"') quoted = true;
            else if (character is '{' or '[')
            {
                if (++depth > _options.MaximumJsonDepth) throw Failure(SessionEntryCodecFailure.DepthLimit);
            }
            else if (character is '}' or ']') depth--;
        }
    }

    private SessionEntry ReadValidated(JsonElement value)
    {
        ValidateJson(value, 0);
        Object(value);
        var type = NonemptyString(value, "type");
        var id = String(value, "id");
        var timestamp = String(value, "timestamp");
        var kind = ClassifyType(type);
        string? parentId = null;
        if (kind == SessionEntryKind.Header)
        {
            if (!value.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt64(out var number) || number != CurrentVersion)
                throw Failure(SessionEntryCodecFailure.UnsupportedVersion);
            _ = String(value, "cwd");
        }
        else
        {
            var parent = Required(value, "parentId");
            if (parent.ValueKind == JsonValueKind.String) parentId = JsonUtf16.GetString(parent);
            else if (parent.ValueKind != JsonValueKind.Null) throw Failure(SessionEntryCodecFailure.InvalidRecord);
        }
        switch (kind)
        {
            case SessionEntryKind.Message: ValidateMessage(Required(value, "message")); break;
            case SessionEntryKind.ThinkingLevelChange: _ = String(value, "thinkingLevel"); break;
            case SessionEntryKind.ModelChange: _ = String(value, "provider"); _ = String(value, "modelId"); break;
            case SessionEntryKind.Usage:
                _ = String(value, "kind"); _ = String(value, "provider"); _ = String(value, "model");
                ValidateUsage(Required(value, "usage")); break;
            case SessionEntryKind.Compaction:
                _ = String(value, "summary"); _ = String(value, "firstKeptEntryId");
                Integer(value, "tokensBefore", nonnegative: true); break;
            case SessionEntryKind.BranchSummary: _ = String(value, "fromId"); _ = String(value, "summary"); break;
            case SessionEntryKind.Custom: _ = String(value, "customType"); break;
            case SessionEntryKind.CustomMessage:
                _ = String(value, "customType"); Boolean(value, "display");
                ValidateContent(Required(value, "content"), "user", allowString: true); break;
            case SessionEntryKind.ContextEdit:
                _ = String(value, "targetId"); var replacement = Required(value, "replacement");
                if (replacement.ValueKind != JsonValueKind.Null)
                {
                    Object(replacement);
                    ValidateContent(Required(replacement, "content"), "replacement", allowString: true);
                }
                break;
            case SessionEntryKind.Label: _ = String(value, "targetId"); break;
        }
        return new(kind, type, id, parentId, timestamp, JsonData.FromElement(value));
    }

    internal static SessionEntryKind ClassifyType(string type) => type switch
    {
        "session" => SessionEntryKind.Header, "message" => SessionEntryKind.Message,
        "thinking_level_change" => SessionEntryKind.ThinkingLevelChange,
        "model_change" => SessionEntryKind.ModelChange, "usage" => SessionEntryKind.Usage,
        "compaction" => SessionEntryKind.Compaction, "branch_summary" => SessionEntryKind.BranchSummary,
        "custom" => SessionEntryKind.Custom, "custom_message" => SessionEntryKind.CustomMessage,
        "context_edit" => SessionEntryKind.ContextEdit, "label" => SessionEntryKind.Label,
        "session_info" => SessionEntryKind.SessionInfo, _ => SessionEntryKind.Unknown
    };

    internal void ValidateOpaqueJson(JsonElement value) => ValidateJson(value, 0);

    private void ValidateJson(JsonElement value, int depth)
    {
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            if (++depth > _options.MaximumJsonDepth) throw Failure(SessionEntryCodecFailure.DepthLimit);
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    string name;
                    try { name = property.Name; }
                    catch (InvalidOperationException) { throw Failure(SessionEntryCodecFailure.UnsupportedUnicode); }
                    CheckUnicode(name);
                    if (!names.Add(name)) throw Failure(SessionEntryCodecFailure.DuplicateProperty);
                    ValidateJson(property.Value, depth);
                }
            }
            else foreach (var item in value.EnumerateArray()) ValidateJson(item, depth);
        }
        // session-manager.ts reads each line with JSON.parse, which keeps an escaped lone surrogate in a string value (and
        // JSON.stringify writes it back as its escape): the value is retained as that escape (see JsonUtf16).
        else if (value.ValueKind == JsonValueKind.Undefined) throw Failure(SessionEntryCodecFailure.InvalidRecord);
    }

    private static void ValidateMessage(JsonElement value)
    {
        Object(value);
        var role = NonemptyString(value, "role");
        // AgentMessage is extensible upstream. Unknown roles remain inert wire data.
        if (role is not ("system" or "user" or "assistant" or "toolResult" or "bashExecution" or
            "custom" or "branchSummary" or "compactionSummary")) return;
        Integer(value, "timestamp");
        switch (role)
        {
            case "system": case "user":
                ValidateContent(Required(value, "content"), role, allowString: true); break;
            case "assistant":
                ValidateContent(Required(value, "content"), role, allowString: false);
                _ = String(value, "api"); _ = String(value, "provider"); _ = String(value, "model");
                ValidateUsage(Required(value, "usage"));
                if (String(value, "stopReason") is not ("pending" or "stop" or "length" or "toolUse" or
                    "error" or "aborted" or "deferred")) throw Failure(SessionEntryCodecFailure.InvalidRecord);
                break;
            case "toolResult":
                _ = String(value, "toolCallId"); _ = String(value, "toolName"); Boolean(value, "isError");
                ValidateContent(Required(value, "content"), role, allowString: false); break;
            case "bashExecution":
                _ = String(value, "command"); _ = String(value, "output");
                Boolean(value, "cancelled"); Boolean(value, "truncated");
                if (value.TryGetProperty("exitCode", out var exitCode) && exitCode.ValueKind != JsonValueKind.Null)
                    Integer(value, "exitCode");
                break;
            case "custom":
                _ = String(value, "customType"); Boolean(value, "display");
                ValidateContent(Required(value, "content"), "user", allowString: true); break;
            case "branchSummary":
                _ = String(value, "summary"); var fromId = Required(value, "fromId");
                if (fromId.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    throw Failure(SessionEntryCodecFailure.InvalidRecord);
                break;
            case "compactionSummary": _ = String(value, "summary"); Integer(value, "tokensBefore", nonnegative: true); break;
        }
    }

    private static void ValidateContent(JsonElement value, string role, bool allowString)
    {
        if (allowString && value.ValueKind == JsonValueKind.String) return;
        if (value.ValueKind != JsonValueKind.Array) throw Failure(SessionEntryCodecFailure.InvalidRecord);
        foreach (var block in value.EnumerateArray())
        {
            Object(block);
            switch (NonemptyString(block, "type"))
            {
                case "text": _ = String(block, "text"); break;
                case "image":
                    if (role is "system" or "assistant") throw Failure(SessionEntryCodecFailure.InvalidRecord);
                    _ = String(block, "data"); _ = String(block, "mimeType"); break;
                case "thinking":
                    if (role is not ("assistant" or "replacement")) throw Failure(SessionEntryCodecFailure.InvalidRecord);
                    _ = String(block, "thinking"); break;
                case "toolCall":
                    if (role is not ("assistant" or "replacement")) throw Failure(SessionEntryCodecFailure.InvalidRecord);
                    _ = String(block, "id"); _ = String(block, "name"); Object(Required(block, "arguments")); break;
                // Future content variants remain opaque; this codec never projects them into model input.
            }
        }
    }

    private static void ValidateUsage(JsonElement value)
    {
        Object(value);
        // Counts are JavaScript numbers: a provider may report a fraction (or any other number) and Pi keeps it.
        foreach (var name in new[] { "input", "output", "cacheRead", "cacheWrite", "totalTokens" })
            if (Required(value, name).ValueKind != JsonValueKind.Number) throw Failure(SessionEntryCodecFailure.InvalidRecord);
        var cost = Required(value, "cost"); Object(cost);
        foreach (var name in new[] { "input", "output", "cacheRead", "cacheWrite", "total" })
            if (Required(cost, name).ValueKind != JsonValueKind.Number) throw Failure(SessionEntryCodecFailure.InvalidRecord);
    }

    private static JsonElement Required(JsonElement value, string name) => value.TryGetProperty(name, out var field)
        ? field : throw Failure(SessionEntryCodecFailure.InvalidRecord);
    private static void Object(JsonElement value)
    { if (value.ValueKind != JsonValueKind.Object) throw Failure(SessionEntryCodecFailure.InvalidRecord); }
    private static string String(JsonElement value, string name)
    {
        var field = Required(value, name);
        return field.ValueKind == JsonValueKind.String ? JsonUtf16.GetString(field) : throw Failure(SessionEntryCodecFailure.InvalidRecord);
    }
    private static string NonemptyString(JsonElement value, string name)
    {
        var text = String(value, name);
        return text.Length != 0 ? text : throw Failure(SessionEntryCodecFailure.InvalidRecord);
    }
    private static void Boolean(JsonElement value, string name)
    { if (Required(value, name).ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Failure(SessionEntryCodecFailure.InvalidRecord); }
    private static void Integer(JsonElement value, string name, bool nonnegative = false)
    {
        var field = Required(value, name);
        if (field.ValueKind != JsonValueKind.Number) throw Failure(SessionEntryCodecFailure.InvalidRecord);
        if (!field.TryGetInt64(out var number) || (nonnegative && number < 0))
            throw Failure(SessionEntryCodecFailure.UnsupportedInteger);
    }
    private static void CheckUnicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (++index >= value.Length || !char.IsLowSurrogate(value[index]))
                    throw Failure(SessionEntryCodecFailure.UnsupportedUnicode);
            }
            else if (char.IsLowSurrogate(value[index])) throw Failure(SessionEntryCodecFailure.UnsupportedUnicode);
        }
    }
    private static SessionEntryCodecException Failure(SessionEntryCodecFailure failure) => new(failure);
}
