using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Sessions.Lifecycle;
using PiSharp.CodingAgent;
using PiSharp.Sessions.Compaction;

namespace PiSharp.Rpc.Protocol;

internal sealed record RpcCommandEnvelope(string? Id, string Type, string? Message = null, JsonData? Images = null,
    string? StreamingBehavior = null, string? Mode = null, string? Since = null, SessionCatalogQuery? CatalogQuery = null,
    string? TargetId = null, JsonData? Replacement = null, long? ExpectedGeneration = null,
    SessionCompactionRequest? Compaction = null, SessionBranchSummaryRequest? BranchSummary = null, string? Provider = null, string? ModelId = null, string? ThinkingLevel = null);
internal sealed class RpcCommandException(string? id, string? command, string message) : Exception(message)
{ public string? Id { get; } = id; public string? Command { get; } = command; }

internal static class RpcCommandCodec
{
    // Complete pinned rpc-types.ts inventory; recognized unfinished commands cannot become Unknown command errors.
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "prompt", "steer", "follow_up", "abort", "clear_queue", "new_session", "get_state", "set_model", "cycle_model",
        "get_available_models", "set_thinking_level", "cycle_thinking_level", "get_available_thinking_levels",
        "set_steering_mode", "set_follow_up_mode", "compact", "set_auto_compaction", "set_auto_retry", "abort_retry",
        "bash", "abort_bash", "get_session_stats", "export_html", "switch_session", "fork", "clone", "get_fork_messages",
        "get_entries", "get_tree", "get_last_assistant_text", "set_session_name", "get_messages", "get_commands", "pisharp_complete_extension_command",
        "pisharp_list_sessions", "pisharp_resume_session", "pisharp_context_edit", "pisharp_compact", "pisharp_branch_summary", "pisharp_set_auto_compaction",
        "pisharp_restore_queue", "pisharp_interrupt", "pisharp_capture_navigation", "pisharp_select_navigation", "pisharp_retire_navigation"
    };
    internal static bool IsKnown(string command) => Known.Contains(command);

    internal static RpcCommandEnvelope Decode(JsonData input, RpcDispatchOptions options)
    {
        // rpc-mode.ts handleCommand(JSON.parse(line)): a number, string, boolean or array has no id or type, so the switch falls to
        // default and answers error(undefined, undefined, "Unknown command: undefined").
        if (input.Value.ValueKind != JsonValueKind.Object) throw new RpcCommandException(null, null, "Unknown command: undefined");
        try { Strict(input, options.MaximumCommandBytes, options.MaximumJsonDepth); }
        catch (JsonlTransportException) { throw new RpcCommandException(null, "parse", "Failed to parse command: invalid strict JSON object."); }
        var body = input.Value; string? id = null;
        // An id or type with a lone surrogate is echoed exactly (the owned record holds U+FFFD there).
        JsonlRecordCodec.ExactMembers.TryGetValue(input, out var exact);
        string Exact(string field, JsonElement value) => exact is not null && exact.TryGetValue(field, out var text) ? text : value.GetString()!;
        if (body.TryGetProperty("id", out var identity))
        {
            // rpc-mode.ts echoes command.id as it came: a non-string id is written back as that JSON value.
            if (identity.ValueKind != JsonValueKind.String) id = RawJson(identity);
            else if (identity.GetString()!.Length > options.MaximumIdCharacters)
                throw new RpcCommandException(null, "parse", "Command id must be a bounded string when present.");
            else id = Exact("id", identity);
        }
        // An object without a type reaches the same default branch: "Unknown command: undefined", with its id.
        if (!body.TryGetProperty("type", out var type)) throw new RpcCommandException(id, null, "Unknown command: undefined");
        // A non-string type matches no case: error(id, type, `Unknown command: ${type}`), the type echoed as its JSON value.
        if (type.ValueKind != JsonValueKind.String) throw new RpcCommandException(id, RawJson(type), "Unknown command: " + JsString(type));
        if (type.GetString()!.Length > options.MaximumCommandTypeCharacters)
            throw new RpcCommandException(id, "parse", "Command type must be a bounded string.");
        var name = Exact("type", type);
        try { _ = ErrorCore(id, name, "RPC command failed.", options); }
        catch (RpcDispatchException) { throw new RpcCommandException(null, "parse", "Command identity exceeds response limits."); }
        string Required(string field, int maximum)
        {
            if (!body.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
                throw new RpcCommandException(id, name, "Command requires a string " + field + ".");
            var text = value.GetString()!;
            if (text.Length > maximum) throw new RpcCommandException(id, name, "Command " + field + " exceeds configured limits.");
            return text;
        }
        string? Optional(string field, int maximum) => body.TryGetProperty(field, out _) ? Required(field, maximum) : null;
        if (name is "pisharp_capture_navigation" or "pisharp_select_navigation" or "pisharp_retire_navigation")
        {
            if (!body.TryGetProperty("generation", out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var expected) ||
                expected is < 1 or > 9_007_199_254_740_991)
                throw new RpcCommandException(id, name, "Navigation requires a captured positive integer generation.");
            var mode = Required("mode", 4);
            if (mode is not ("tree" or "fork")) throw new RpcCommandException(id, name, "Navigation mode is invalid.");
            if (id is null) throw new RpcCommandException(id, name, "Navigation requires a correlated command identity.");
            return new(id, name, Message: name == "pisharp_select_navigation" ? Required("viewId", 32) : null,
                Mode: mode, TargetId: name == "pisharp_select_navigation" ? Required("targetId", options.MaximumIdCharacters) : null,
                ExpectedGeneration: expected, Since: name == "pisharp_retire_navigation" ? Required("captureId", options.MaximumIdCharacters) : null);
        }
        if (name is "pisharp_restore_queue" or "pisharp_interrupt")
        {
            if (!body.TryGetProperty("generation", out var generation) || generation.ValueKind != JsonValueKind.Number ||
                !generation.TryGetInt64(out var expected) || expected is < 1 or > 9_007_199_254_740_991)
                throw new RpcCommandException(id, name, "Queue restoration requires a captured positive integer generation.");
            return new(id, name, Message: Required("currentText", 65_536), ExpectedGeneration: expected);
        }
        // Pi: an absent, null or empty outputPath selects the default file name.
        if (name == "export_html") return new(id, name, Message: body.TryGetProperty("outputPath", out var output) && output.ValueKind == JsonValueKind.Null
            ? null : Optional("outputPath", Math.Min(options.MaximumPromptCharacters, 4096)));
        if (name == "compact") return new(id, name,
            Compaction: new(SummaryOptions: new(CustomInstructions: Optional("customInstructions", Math.Min(options.MaximumPromptCharacters, 65_536)))));
        if (name is "set_auto_compaction" or "set_auto_retry")
        {
            if (!body.TryGetProperty("enabled", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new RpcCommandException(id, name, "Command requires a boolean enabled.");
            return new(id, name, Mode: enabled.GetBoolean() ? "enabled" : "disabled");
        }
        if (name is "pisharp_compact" or "pisharp_branch_summary" or "pisharp_set_auto_compaction")
        {
            if (!body.TryGetProperty("generation", out var generation) || generation.ValueKind != JsonValueKind.Number || !generation.TryGetInt64(out var expected) ||
                expected is < 1 or > 9_007_199_254_740_991) throw new RpcCommandException(id, name, "Summary requires a captured positive integer generation.");
            double Number(string field, double fallback)
            {
                if (!body.TryGetProperty(field, out var value)) return fallback;
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number) || number < 0 || number > 1_000_000_000)
                    throw new RpcCommandException(id, name, "Summary token settings must be bounded finite nonnegative numbers.");
                return number;
            }
            bool Boolean(string field)
            {
                if (!body.TryGetProperty(field, out var value)) return false;
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new RpcCommandException(id, name, "Summary flags must be booleans.");
                return value.GetBoolean();
            }
            var focus = Optional("customInstructions", Math.Min(options.MaximumPromptCharacters, 65_536));
            var summaryOptions = new SessionSummaryRequestOptions(CustomInstructions: focus);
            if (name == "pisharp_set_auto_compaction")
            {
                if (!body.TryGetProperty("enabled", out _)) throw new RpcCommandException(id, name, "Automatic summary configuration requires enabled.");
                return new(id, name, Mode: Boolean("enabled") ? "enabled" : "disabled", ExpectedGeneration: expected,
                    Compaction: new(new(ReserveTokens: Number("reserveTokens", 16_384), KeepRecentTokens: Number("keepRecentTokens", 20_000)),
                        Automatic: true, ContextWindow: Number("contextWindow", 128_000), SummaryOptions: summaryOptions));
            }
            if (name == "pisharp_branch_summary")
            {
                if (!body.TryGetProperty("targetId", out var target)) throw new RpcCommandException(id, name, "Branch summary requires an explicit targetId (null selects root).");
                var targetId = target.ValueKind == JsonValueKind.Null ? null : Required("targetId", options.MaximumIdCharacters);
                if (targetId is { Length: 0 }) throw new RpcCommandException(id, name, "Branch target cannot be empty.");
                return new(id, name, ExpectedGeneration: expected, BranchSummary: new(targetId, Number("contextWindow", 128_000),
                    Number("reserveTokens", 16_384), summaryOptions with { ReplaceBranchInstructions = Boolean("replaceInstructions") }));
            }
            var overrideBoundary = body.TryGetProperty("firstKeptEntryId", out var boundary);
            var firstKept = !overrideBoundary || boundary.ValueKind == JsonValueKind.Null ? null : Required("firstKeptEntryId", options.MaximumIdCharacters);
            if (firstKept is { Length: 0 }) throw new RpcCommandException(id, name, "Retained boundary cannot be empty.");
            return new(id, name, ExpectedGeneration: expected, Compaction: new(new(ReserveTokens: Number("reserveTokens", 16_384),
                KeepRecentTokens: Number("keepRecentTokens", 20_000)), Boolean("automatic"), Number("contextWindow", 128_000),
                summaryOptions, OverrideRetainedBoundary: overrideBoundary, FirstKeptEntryId: firstKept));
        }
        if (name == "pisharp_context_edit")
        {
            var target = Required("targetId", options.MaximumIdCharacters);
            if (target.Length == 0 || !body.TryGetProperty("replacement", out var replacement))
                throw new RpcCommandException(id, name, "Context edit requires targetId and an explicit replacement (null omits content).");
            if (!body.TryGetProperty("generation", out var generation) || generation.ValueKind != JsonValueKind.Number ||
                !generation.TryGetInt64(out var expected) || expected is < 1 or > 9_007_199_254_740_991)
                throw new RpcCommandException(id, name, "Context edit requires the captured positive integer generation.");
            return new(id, name, TargetId: target, Replacement: JsonData.FromElement(replacement), ExpectedGeneration: expected);
        }
        if (name is "prompt" or "steer" or "follow_up")
        {
            var message = Required("message", options.MaximumPromptCharacters); JsonData? images = null;
            if (body.TryGetProperty("images", out var value))
            {
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > options.MaximumImages)
                    throw new RpcCommandException(id, name, "Command images must be a bounded array.");
                foreach (var image in value.EnumerateArray())
                    if (image.ValueKind != JsonValueKind.Object || !image.TryGetProperty("type", out var kind) ||
                        kind.ValueKind != JsonValueKind.String || kind.GetString() != "image" ||
                        !image.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String ||
                        !image.TryGetProperty("mimeType", out var mime) || mime.ValueKind != JsonValueKind.String)
                        throw new RpcCommandException(id, name, "Command image requires type, data and mimeType strings.");
                images = JsonData.FromElement(value);
            }
            var behavior = name == "prompt" ? Optional("streamingBehavior", 16) : null;
            if (behavior is not (null or "steer" or "followUp"))
                throw new RpcCommandException(id, name, "Invalid streamingBehavior; use steer or followUp.");
            return new(id, name, message, images, behavior);
        }
        if (name is "set_steering_mode" or "set_follow_up_mode")
        {
            var mode = Required("mode", 32);
            if (mode is not ("all" or "one-at-a-time")) throw new RpcCommandException(id, name, "Invalid queue mode.");
            return new(id, name, Mode: mode);
        }
        if (name == "pisharp_complete_extension_command")
            return new(id, name, Message: Required("command", options.MaximumCommandTypeCharacters),
                Mode: Required("prefix", options.MaximumPromptCharacters));
        if (name == "pisharp_list_sessions")
        {
            var pageSize = 32;
            if (body.TryGetProperty("pageSize", out var size) && (size.ValueKind != JsonValueKind.Number || !size.TryGetInt32(out pageSize) || pageSize is < 1 or > 128))
                throw new RpcCommandException(id, name, "Session listing requires pageSize between 1 and 128.");
            return new(id, name, CatalogQuery: new(pageSize, Optional("cursor", 32768), Optional("cwd", 4096)));
        }
        if (name is "switch_session" or "pisharp_resume_session")
        {
            var path = Required(name == "switch_session" ? "sessionPath" : "catalogKey", name == "switch_session" ? options.MaximumPromptCharacters : 64);
            if (name == "switch_session" && (!Path.IsPathFullyQualified(path) || string.IsNullOrWhiteSpace(path)))
                throw new RpcCommandException(id, name, "Session switch requires an absolute sessionPath.");
            if (name == "pisharp_resume_session" && (path.Length != 64 || path.Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))))
                throw new RpcCommandException(id, name, "Session resume requires a listed catalogKey.");
            var leaf = Optional("leafId", options.MaximumIdCharacters);
            var root = body.TryGetProperty("root", out var selectedRoot) && selectedRoot.ValueKind == JsonValueKind.True;
            if (body.TryGetProperty("root", out selectedRoot) && selectedRoot.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || root && leaf is not null)
                throw new RpcCommandException(id, name, "Invalid session branch selection.");
            return new(id, name, Message: path, Mode: root || leaf is not null ? "selected" : "latest", Since: leaf);
        }
        if (name == "bash")
        {
            var shellCommand = Required("command", options.MaximumPromptCharacters);
            if (!body.TryGetProperty("excludeFromContext", out var excluded)) return new(id, name, Message: shellCommand);
            if (excluded.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new RpcCommandException(id, name, "Command excludeFromContext must be a boolean when present.");
            return new(id, name, Message: shellCommand, Mode: excluded.GetBoolean() ? "exclude" : "include");
        }
        if (name == "set_session_name") return new(id, name, Message: Required("name", Math.Min(options.MaximumPromptCharacters, 65_536)));
        if (name == "set_model")
        {
            var provider = Required("provider", options.MaximumCommandBytes);
            var modelId = Required("modelId", options.MaximumCommandBytes);
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(modelId) || provider.Any(char.IsControl) || modelId.Any(char.IsControl))
                throw new RpcCommandException(id, name, "Model selection requires bounded nonempty provider and modelId strings.");
            return new(id, name, Provider: provider, ModelId: modelId);
        }
        if (name == "set_thinking_level")
        {
            var level = Required("level", 16);
            if (level is not ("off" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max"))
                throw new RpcCommandException(id, name, "Thinking level is invalid.");
            return new(id, name, ThinkingLevel: level);
        }
        if (name == "new_session") return new(id, name, Message: Optional("parentSession", 4096));
        if (name == "fork")
        {
            var entry = Required("entryId", Math.Min(128, options.MaximumIdCharacters));
            var position = Optional("position", 6) ?? "before";
            if (entry.Length == 0 || position is not ("before" or "at"))
                throw new RpcCommandException(id, name, "Fork requires an entryId and a supported position.");
            return new(id, name, Message: entry, Mode: position);
        }
        return new(id, name, Since: name == "get_entries" ? Optional("since", options.MaximumIdCharacters) : null);
    }

    internal static TranscriptEntry User(RpcCommandEnvelope command, long timestamp, RpcDispatchOptions options) =>
        new("user", Build(writer =>
        {
            writer.WriteString("role", "user"); writer.WritePropertyName("content"); writer.WriteStartArray();
            writer.WriteStartObject(); writer.WriteString("type", "text"); writer.WriteString("text", command.Message); writer.WriteEndObject();
            if (command.Images is { } images) foreach (var image in images.Value.EnumerateArray()) writer.WriteRawValue(image.GetRawText());
            writer.WriteEndArray(); writer.WriteNumber("timestamp", timestamp);
        }, options.MaximumCommandBytes));

    internal static JsonData Success(RpcCommandEnvelope command, JsonData? data, RpcDispatchOptions options) => Build(writer =>
    {
        Header(writer, command.Id, command.Type); writer.WriteBoolean("success", true);
        if (data is not null) { writer.WritePropertyName("data"); writer.WriteRawValue(data.ToString()); }
    }, options.MaximumOutputBytes);
    internal static JsonData Error(string? id, string? command, string error, RpcDispatchOptions options)
    {
        try { return ErrorCore(id, command, error, options); }
        catch (RpcDispatchException) { return ErrorCore(id, command, "RPC command failed.", options); }
    }
    private static JsonData ErrorCore(string? id, string? command, string error, RpcDispatchOptions options) => Build(writer =>
    { Header(writer, id, command); writer.WriteBoolean("success", false); writer.WritePropertyName("error"); WriteEchoed(writer, error); }, options.MaximumOutputBytes);
    // JSON.stringify omits an undefined command (the type of a non-object or type-less command).
    private static void Header(Utf8JsonWriter writer, string? id, string? command)
    {
        if (id is not null) { writer.WritePropertyName("id"); WriteEchoed(writer, id); }
        writer.WriteString("type", "response");
        if (command is not null) { writer.WritePropertyName("command"); WriteEchoed(writer, command); }
    }

    // A non-string id or type travels through the dispatcher's string-typed identity as this marker plus its JSON text, and is written
    // back as that JSON value. The process-unique NUL-led prefix cannot be produced by an ordinary command string in practice.
    private static readonly string RawPrefix = "\0pisharp-raw-json:" + Guid.NewGuid().ToString("N") + ":";
    private static string RawJson(JsonElement value) => RawPrefix + value.GetRawText();
    private static void WriteEchoed(Utf8JsonWriter writer, string value)
    {
        if (value.StartsWith(RawPrefix, StringComparison.Ordinal)) writer.WriteRawValue(value[RawPrefix.Length..]);
        // Utf8JsonWriter refuses a lone surrogate; JSON.stringify writes it as an escape.
        else if (HasLoneSurrogate(value)) writer.WriteRawValue(PiSharp.AI.StreamingJson.JsonQuote(value), skipInputValidation: true);
        else writer.WriteStringValue(value);
    }
    private static bool HasLoneSurrogate(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1])) index++;
            else if (char.IsSurrogate(value[index])) return true;
        }
        return false;
    }
    // String(value) for a JSON value, as a template literal converts it.
    private static string JsString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.Null => "null", JsonValueKind.True => "true", JsonValueKind.False => "false",
        JsonValueKind.Number => PiSharp.AI.StreamingJson.ParseToJson(value.GetRawText()),
        JsonValueKind.Array => string.Join(",", value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.Null ? "" : JsString(item))),
        _ => "[object Object]"
    };
    internal static JsonData Event(string type, Action<Utf8JsonWriter>? fields, RpcDispatchOptions options) => Build(writer =>
    { writer.WriteString("type", type); fields?.Invoke(writer); }, options.MaximumOutputBytes);
    internal static JsonData Build(Action<Utf8JsonWriter> fields, int maximum)
    {
        var bytes = new BoundedBuffer(maximum);
        using (var writer = new Utf8JsonWriter(bytes)) { writer.WriteStartObject(); fields(writer); writer.WriteEndObject(); }
        return JsonData.Parse(Encoding.UTF8.GetString(bytes.WrittenSpan));
    }
    /// <summary>Source JSON.stringify text escaping (quotes as \", non-ASCII and HTML characters unescaped) for session event records.</summary>
    internal static JsonData SourceEvent(string type, Action<Utf8JsonWriter>? fields, RpcDispatchOptions options)
    {
        var bytes = new BoundedBuffer(options.MaximumOutputBytes);
        using (var writer = new Utf8JsonWriter(bytes, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        { writer.WriteStartObject(); writer.WriteString("type", type); fields?.Invoke(writer); writer.WriteEndObject(); }
        return JsonData.Parse(Encoding.UTF8.GetString(bytes.WrittenSpan));
    }
    internal static void Raw(Utf8JsonWriter writer, string field, JsonData value)
    { writer.WritePropertyName(field); writer.WriteRawValue(value.ToString()); }
    internal static void Messages(Utf8JsonWriter writer, string field, ImmutableArray<TranscriptEntry> messages, int maximum, int start = 0)
    {
        if (start < 0 || start > messages.Length || messages.Length - start > maximum) throw new RpcDispatchException(RpcDispatchFailure.ResourceLimit);
        writer.WritePropertyName(field); writer.WriteStartArray();
        for (var index = start; index < messages.Length; index++) writer.WriteRawValue(messages[index].WireBody.ToString());
        writer.WriteEndArray();
    }
    internal static string Text(TranscriptEntry message)
    {
        if (!message.WireBody.Value.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString()!;
        if (content.ValueKind != JsonValueKind.Array) return "";
        return string.Concat(content.EnumerateArray().Where(part => part.ValueKind == JsonValueKind.Object &&
            part.TryGetProperty("type", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "text")
            .Select(part => part.GetProperty("text").GetString()));
    }
    internal static void Queue(Utf8JsonWriter writer, AgentPendingInputQueueSnapshot queue)
    {
        Write("steering", queue.SteeringMessages); Write("followUp", queue.FollowUpMessages);
        void Write(string name, ImmutableArray<TranscriptEntry> messages)
        { writer.WritePropertyName(name); writer.WriteStartArray(); foreach (var message in messages) writer.WriteStringValue(Text(message)); writer.WriteEndArray(); }
    }
    internal static string Mode(AgentPendingInputMode mode) => mode == AgentPendingInputMode.All ? "all" : "one-at-a-time";
    internal static string TrimSource(string value)
    {
        var start = 0; var end = value.Length;
        while (start < end && Space(value[start])) start++;
        while (end > start && Space(value[end - 1])) end--;
        return value[start..end];
        static bool Space(char character) => character is '\t' or '\n' or '\v' or '\f' or '\r' or ' ' or '\u00A0' or '\u1680' or
            >= '\u2000' and <= '\u200A' or '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000' or '\uFEFF';
    }
    internal static void Strict(JsonData data, int maximumBytes, int maximumDepth) =>
        _ = JsonlRecordCodec.Encode(data, new(MaximumFrameBytes: maximumBytes, MaximumJsonDepth: maximumDepth));

    internal static ImmutableDictionary<ModelDescriptor, JsonData> Models(ImmutableArray<RpcModelDefinition> definitions,
        RpcDispatchOptions options)
    {
        if (definitions.IsDefaultOrEmpty || definitions.Length > options.MaximumModels)
            throw new RpcDispatchException(RpcDispatchFailure.InvalidModelDefinition);
        var result = ImmutableDictionary.CreateBuilder<ModelDescriptor, JsonData>(); long bytes = 0;
        foreach (var definition in definitions)
        {
            if (definition?.Model is null || definition.WireBody is null) throw new RpcDispatchException(RpcDispatchFailure.InvalidModelDefinition);
            bytes += Encoding.UTF8.GetByteCount(definition.WireBody.ToString());
            if (bytes > options.MaximumModelDefinitionBytes) throw new RpcDispatchException(RpcDispatchFailure.ResourceLimit);
            try { Strict(definition.WireBody, options.MaximumModelDefinitionBytes, options.MaximumJsonDepth); }
            catch (JsonlTransportException) { throw new RpcDispatchException(RpcDispatchFailure.InvalidModelDefinition); }
            var body = definition.WireBody.Value;
            bool String(string field, string? expected = null) => body.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String &&
                (expected is null || value.GetString() == expected);
            bool Number(JsonElement owner, string field) => owner.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number;
            if (!String("id", definition.Model.Id) || !String("api", definition.Model.Api) || !String("provider", definition.Model.Provider) ||
                !String("name") || !String("baseUrl") || !body.TryGetProperty("reasoning", out var reasoning) ||
                reasoning.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !body.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Array || input.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String) ||
                !Number(body, "contextWindow") || !Number(body, "maxTokens") || !body.TryGetProperty("cost", out var cost) ||
                cost.ValueKind != JsonValueKind.Object || !Number(cost, "input") || !Number(cost, "output") ||
                !Number(cost, "cacheRead") || !Number(cost, "cacheWrite") || !result.TryAdd(definition.Model, definition.WireBody))
                throw new RpcDispatchException(RpcDispatchFailure.InvalidModelDefinition);
        }
        return result.ToImmutable();
    }
    private sealed class BoundedBuffer(int maximum) : IBufferWriter<byte>
    {
        // Utf8JsonWriter reserves a minimum-sized span before it knows the final count. Keep that fixed reservation bounded as well as committed bytes.
        private const int ReservationBytes = 4096;
        private byte[] _bytes = new byte[Math.Min(maximum, 256)]; private int _length;
        internal ReadOnlySpan<byte> WrittenSpan => _bytes.AsSpan(0, _length);
        public void Advance(int count)
        {
            if (count < 0 || count > _bytes.Length - _length) throw new ArgumentOutOfRangeException(nameof(count));
            if (count > maximum - _length) throw new RpcDispatchException(RpcDispatchFailure.ResourceLimit);
            _length += count;
        }
        public Memory<byte> GetMemory(int sizeHint = 0) { Ensure(sizeHint); return _bytes.AsMemory(_length); }
        public Span<byte> GetSpan(int sizeHint = 0) { Ensure(sizeHint); return _bytes.AsSpan(_length); }
        private void Ensure(int sizeHint)
        {
            if (sizeHint < 0) throw new ArgumentOutOfRangeException(nameof(sizeHint));
            var required = (long)_length + Math.Max(1, sizeHint); var limit = Math.Min(int.MaxValue, (long)maximum + ReservationBytes);
            if (required > limit) throw new RpcDispatchException(RpcDispatchFailure.ResourceLimit);
            if (required > _bytes.Length) Array.Resize(ref _bytes, (int)Math.Min(limit, Math.Max(required, _bytes.Length * 2L)));
        }
    }
}
