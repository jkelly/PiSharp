// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/utils/transcript.ts (collapseSystemMessages,
// getCurrentSystemMessage, getCurrentTools, getInitialSystemMessage), utils/text.ts (contentText, getSystemMessageText),
// api/transform-messages.ts (transformMessages), utils/estimate.ts (estimateContextTokens), api/constrained-sampling.ts
// (makeStrictJsonSchema, resolveJsonSchemaStrictSampling) and models.ts (getSupportedThinkingLevels, clampThinkingLevel).
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;
using PiSharp.Contracts.Compatibility;

namespace PiSharp.AI.Protocols.ProviderShared;

public sealed class ProviderTranscriptException(string message) : Exception(message);

/// <summary>Public access to the catalog thinking-level helpers (models.ts getSupportedThinkingLevels, clampThinkingLevel).</summary>
public static class ProviderTranscriptAccess
{
    public static ImmutableArray<string> SupportedThinkingLevels(JsonData metadata) => ProviderTranscript.SupportedThinkingLevels(metadata.Value);
    public static string ClampThinkingLevel(JsonData metadata, string level) => ProviderTranscript.ClampThinkingLevel(metadata.Value, level);
}

/// <summary>Owned transcript helpers shared by the Bedrock and Codex ports. Every method works on copies.</summary>
internal static class ProviderTranscript
{
    internal static readonly ImmutableArray<string> ExtendedThinkingLevels = ["off", "minimal", "low", "medium", "high", "xhigh", "max"];
    private const double CharsPerToken = 3.5;
    private const double ImageChars = 4800;

    internal static List<JsonObject> Parse(ImmutableArray<TranscriptEntry> messages)
    {
        if (messages.IsDefault) throw new ProviderTranscriptException("Transcript is missing.");
        var result = new List<JsonObject>(messages.Length);
        foreach (var entry in messages)
        {
            // A tool call's arguments may hold lone surrogates (TranscriptSurrogates keeps them): such an entry becomes a tree that holds them.
            if (entry?.WireBody is not { } wire || (JsonUtf16.HasEscapedSurrogate(wire.ToString()) ? JsonUtf16.MutableNode(wire.Value)
                : JsonNode.Parse(wire.ToString(), documentOptions: PiSharp.Contracts.JsonData.DocumentOptions)) is not JsonObject body)
                throw new ProviderTranscriptException("Invalid transcript entry.");
            if (body["role"] is JsonValue role && role.TryGetValue<string>(out var name) && name != entry.Role)
                throw new ProviderTranscriptException("Transcript role mismatch.");
            body["role"] = entry.Role;
            result.Add(body);
        }
        return result;
    }

    internal static string Role(JsonObject message) => message["role"]?.GetValue<string>() ?? "";

    /// <summary>contentText: a string, or the "\n"-joined text blocks.</summary>
    internal static string ContentText(JsonNode? content) => content switch
    {
        null => "",
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonArray blocks => string.Join("\n", blocks.OfType<JsonObject>().Where(block => Text(block, "type") == "text").Select(block => Text(block, "text") ?? "")),
        _ => ""
    };

    internal static string? Text(JsonObject? node, string name) =>
        node?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>ECMAScript own-key order: array-index keys ascending, then the other keys in insertion order.</summary>
    internal static IEnumerable<KeyValuePair<string, T>> JsOrder<T>(IEnumerable<KeyValuePair<string, T>> entries)
    {
        var list = entries.ToList();
        static bool IsIndex(string key) => key.Length > 0 && (key == "0" || key[0] != '0') && key.All(char.IsAsciiDigit) &&
            ulong.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value < uint.MaxValue;
        return list.Where(pair => IsIndex(pair.Key)).OrderBy(pair => ulong.Parse(pair.Key, CultureInfo.InvariantCulture))
            .Concat(list.Where(pair => !IsIndex(pair.Key)));
    }

    /// <summary>getSystemMessageText: content then section values (non-null), joined with blank lines.</summary>
    internal static string SystemText(JsonObject message)
    {
        var parts = new List<string> { ContentText(message["content"]) };
        if (message["sections"] is JsonObject sections)
            foreach (var (_, value) in JsOrder(sections))
                if (value is JsonValue text && text.TryGetValue<string>(out var section)) parts.Add(section);
        return string.Join("\n\n", parts.Where(part => part.Length > 0));
    }

    /// <summary>getCurrentTools: system tool deltas replayed in order (toolsRemoved, then toolsAdded per message).</summary>
    internal static List<JsonObject> CurrentTools(IEnumerable<JsonObject> messages)
    {
        var tools = new List<JsonObject>();
        foreach (var message in messages.Where(message => Role(message) == "system"))
        {
            if (message["toolsRemoved"] is JsonArray removed)
                foreach (var tool in removed.OfType<JsonObject>()) tools.RemoveAll(existing => Text(existing, "name") == Text(tool, "name"));
            if (message["toolsAdded"] is JsonArray added)
                foreach (var tool in added.OfType<JsonObject>())
                {
                    var index = tools.FindIndex(existing => Text(existing, "name") == Text(tool, "name"));
                    // Map.set keeps the original insertion slot for an existing key.
                    if (index >= 0) tools[index] = (JsonObject)tool.DeepClone(); else tools.Add((JsonObject)tool.DeepClone());
                }
        }
        return tools;
    }

    /// <summary>getCurrentSystemMessage: base prompts appended, sections patched by name, current tools.</summary>
    internal static JsonObject? CurrentSystemMessage(IReadOnlyList<JsonObject> messages)
    {
        var content = new List<string>(); var sections = new List<KeyValuePair<string, string>>(); double? timestamp = null; var found = false;
        foreach (var message in messages.Where(message => Role(message) == "system"))
        {
            found = true;
            timestamp ??= message["timestamp"] is JsonValue stamp && stamp.TryGetValue<double>(out var number) ? number : 0;
            var text = ContentText(message["content"]); if (text.Length > 0) content.Add(text);
            if (message["sections"] is JsonObject patches)
                foreach (var (name, value) in JsOrder(patches))
                {
                    var index = sections.FindIndex(pair => pair.Key == name);
                    if (value is null) { if (index >= 0) sections.RemoveAt(index); }
                    else if (value is JsonValue stringValue && stringValue.TryGetValue<string>(out var updated))
                    { if (index >= 0) sections[index] = new(name, updated); else sections.Add(new(name, updated)); }
                }
        }
        var tools = CurrentTools(messages);
        if (!found && tools.Count == 0) return null;
        var head = new JsonObject { ["role"] = "system", ["content"] = string.Join("\n\n", content) };
        if (sections.Count > 0) { var values = new JsonObject(); foreach (var (name, value) in sections) values[name] = value; head["sections"] = values; }
        if (tools.Count > 0) head["toolsAdded"] = new JsonArray(tools.Select(tool => (JsonNode)tool.DeepClone()).ToArray());
        head["timestamp"] = timestamp ?? 0;
        return head;
    }

    /// <summary>collapseSystemMessages: the replayed system message leads; later system messages are dropped.</summary>
    internal static List<JsonObject> CollapseSystemMessages(IReadOnlyList<JsonObject> messages)
    {
        var head = CurrentSystemMessage(messages);
        var result = messages.Where(message => Role(message) != "system").ToList();
        if (head is not null) result.Insert(0, head);
        return result;
    }

    /// <summary>transformMessages: image downgrade for non-vision models, cross-model thinking/tool-call normalization, errored
    /// assistant turns dropped and synthetic results for orphaned tool calls.</summary>
    internal static List<JsonObject> Transform(IReadOnlyList<JsonObject> messages, ModelDescriptor model, bool supportsImages,
        Func<string, string>? normalizeToolCallId, long timestamp)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var transformed = new List<JsonObject>();
        foreach (var original in messages)
        {
            var message = (JsonObject)original.DeepClone();
            if (message["content"] is null) message["content"] = new JsonArray();
            var role = Role(message);
            if (!supportsImages && (role == "user" || role == "toolResult") && message["content"] is JsonArray blocks)
                message["content"] = DowngradeImages(blocks, role == "user"
                    ? "(image omitted: model does not support images)" : "(tool image omitted: model does not support images)");
            if (role == "toolResult")
            {
                if (Text(message, "toolCallId") is { } id && map.TryGetValue(id, out var mapped)) message["toolCallId"] = mapped;
            }
            else if (role == "assistant" && message["content"] is JsonArray content)
            {
                var same = Text(message, "provider") == model.Provider && Text(message, "api") == model.Api && Text(message, "model") == model.Id;
                var next = new JsonArray();
                foreach (var block in content.OfType<JsonObject>())
                {
                    var type = Text(block, "type");
                    if (type == "thinking")
                    {
                        if (block["redacted"] is JsonValue redacted && redacted.TryGetValue<bool>(out var isRedacted) && isRedacted)
                        { if (same) next.Add(block.DeepClone()); continue; }
                        var thinking = Text(block, "thinking") ?? "";
                        if (same && !string.IsNullOrEmpty(Text(block, "thinkingSignature"))) { next.Add(block.DeepClone()); continue; }
                        if (thinking.Trim().Length == 0) continue;
                        next.Add(same ? block.DeepClone() : new JsonObject { ["type"] = "text", ["text"] = thinking });
                        continue;
                    }
                    if (type == "text") { next.Add(same ? block.DeepClone() : new JsonObject { ["type"] = "text", ["text"] = Text(block, "text") ?? "" }); continue; }
                    if (type == "toolCall")
                    {
                        var call = (JsonObject)block.DeepClone();
                        if (!same) call.Remove("thoughtSignature");
                        if (!same && normalizeToolCallId is not null && Text(call, "id") is { } callId)
                        {
                            var normalized = normalizeToolCallId(callId);
                            if (normalized != callId) { map[callId] = normalized; call["id"] = normalized; }
                        }
                        next.Add(call); continue;
                    }
                    next.Add(block.DeepClone());
                }
                message["content"] = next;
            }
            transformed.Add(message);
        }
        var result = new List<JsonObject>(); var pending = new List<JsonObject>(); var existing = new HashSet<string>(StringComparer.Ordinal); var held = new List<JsonObject>();
        void Close()
        {
            foreach (var call in pending)
                if (Text(call, "id") is { } id && !existing.Contains(id))
                    result.Add(new JsonObject
                    {
                        ["role"] = "toolResult", ["toolCallId"] = id, ["toolName"] = Text(call, "name") ?? "",
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "No result provided" }),
                        ["isError"] = true, ["timestamp"] = timestamp
                    });
            pending.Clear(); existing.Clear(); result.AddRange(held); held.Clear();
        }
        foreach (var message in transformed)
        {
            var role = Role(message);
            if (role == "assistant")
            {
                Close();
                if (Text(message, "stopReason") is "error" or "aborted") continue;
                var calls = (message["content"] as JsonArray)?.OfType<JsonObject>().Where(block => Text(block, "type") == "toolCall").ToList() ?? [];
                if (calls.Count > 0) { pending = calls; existing = new(StringComparer.Ordinal); }
                result.Add(message);
            }
            else if (role == "toolResult") { if (Text(message, "toolCallId") is { } id) existing.Add(id); result.Add(message); }
            else if (role == "system") { if (pending.Count > 0) held.Add(message); else result.Add(message); }
            else if (role == "user") { Close(); result.Add(message); }
            else result.Add(message);
        }
        Close();
        return result;
    }

    private static JsonArray DowngradeImages(JsonArray content, string placeholder)
    {
        var result = new JsonArray(); var previousWasPlaceholder = false;
        foreach (var block in content.OfType<JsonObject>())
        {
            if (Text(block, "type") == "image")
            {
                if (!previousWasPlaceholder) result.Add(new JsonObject { ["type"] = "text", ["text"] = placeholder });
                previousWasPlaceholder = true; continue;
            }
            result.Add(block.DeepClone());
            previousWasPlaceholder = Text(block, "text") == placeholder;
        }
        return result;
    }

    /// <summary>estimateContextTokens: the latest applicable assistant usage plus estimated trailing messages.</summary>
    internal static double EstimateContextTokens(IReadOnlyList<JsonObject> messages)
    {
        var latest = double.NegativeInfinity; int? last = null; double usageTokens = 0;
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            var stamp = message["timestamp"] is JsonValue value && value.TryGetValue<double>(out var number) ? number : 0;
            if (Role(message) == "assistant" && message["usage"] is JsonObject usage)
            {
                double Count(string name) => usage[name] is JsonValue count && count.TryGetValue<double>(out var parsed) ? parsed : 0;
                var total = Count("totalTokens"); if (total == 0) total = Count("input") + Count("output") + Count("cacheRead") + Count("cacheWrite");
                if (stamp >= latest && Text(message, "stopReason") is not ("aborted" or "error") && total > 0) { last = index; usageTokens = total; }
            }
            latest = Math.Max(latest, stamp);
        }
        double trailing = 0;
        for (var index = last is { } found ? found + 1 : 0; index < messages.Count; index++) trailing += MessageTokens(messages[index]);
        return usageTokens + trailing;
    }

    private static double MessageTokens(JsonObject message)
    {
        var role = Role(message);
        if (role == "system")
        {
            var tokens = Math.Ceiling(SystemText(message).Length / CharsPerToken);
            foreach (var name in new[] { "toolsAdded", "toolsRemoved" })
                if (message[name] is JsonArray tools && tools.Count > 0) tokens += Math.Ceiling(EcmaJson(tools).Length / CharsPerToken);
            return tokens;
        }
        var content = message["content"];
        if (role is "user" or "toolResult")
        {
            if (content is JsonValue text && text.TryGetValue<string>(out var value)) return Math.Ceiling(value.Length / CharsPerToken);
            double characters = 0;
            foreach (var block in (content as JsonArray)?.OfType<JsonObject>() ?? [])
                characters += Text(block, "type") == "text" ? (Text(block, "text") ?? "").Length : ImageChars;
            return Math.Ceiling(characters / CharsPerToken);
        }
        double assistant = 0;
        foreach (var block in (content as JsonArray)?.OfType<JsonObject>() ?? [])
            assistant += Text(block, "type") switch
            {
                "text" => (Text(block, "text") ?? "").Length,
                "thinking" => (Text(block, "thinking") ?? "").Length,
                _ => (Text(block, "name") ?? "").Length + EcmaJson(block["arguments"]).Length
            };
        return Math.Ceiling(assistant / CharsPerToken);
    }

    /// <summary>JSON.stringify of an owned value (ECMAScript key order and escaping).</summary>
    internal static string EcmaJson(JsonNode? node)
    {
        if (node is null) return "null";
        // A lone surrogate (a tool call's arguments) is written as JSON.stringify writes it.
        var text = JsonUtf16.ToJsonString(node);
        if (JsonUtf16.HasEscapedSurrogate(text)) return StreamingJson.JsonReformat(text);
        try { return EcmaScriptJsonProjection.Project(JsonData.Parse(text)); }
        catch (EcmaScriptJsonProjectionException) { return node.ToJsonString(); }
    }

    /// <summary>getSupportedThinkingLevels from catalog metadata.</summary>
    internal static ImmutableArray<string> SupportedThinkingLevels(JsonElement model)
    {
        if (!(model.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.True)) return ["off"];
        var map = model.TryGetProperty("thinkingLevelMap", out var levels) && levels.ValueKind == JsonValueKind.Object ? levels : default;
        return [.. ExtendedThinkingLevels.Where(level =>
        {
            var has = map.ValueKind == JsonValueKind.Object && map.TryGetProperty(level, out var mapped);
            if (has && map.GetProperty(level).ValueKind == JsonValueKind.Null) return false;
            return level is not ("xhigh" or "max") || has;
        })];
    }

    /// <summary>clampThinkingLevel: the requested level, else the next higher supported level, else the next lower.</summary>
    internal static string ClampThinkingLevel(JsonElement model, string level)
    {
        var available = SupportedThinkingLevels(model);
        if (available.Contains(level)) return level;
        var index = ExtendedThinkingLevels.IndexOf(level);
        if (index < 0) return available.FirstOrDefault() ?? "off";
        for (var next = index; next < ExtendedThinkingLevels.Length; next++) if (available.Contains(ExtendedThinkingLevels[next])) return ExtendedThinkingLevels[next];
        for (var next = index - 1; next >= 0; next--) if (available.Contains(ExtendedThinkingLevels[next])) return ExtendedThinkingLevels[next];
        return available.FirstOrDefault() ?? "off";
    }

    /// <summary>thinkingLevelMap[level]: a mapped string, explicit null (unsupported), or absent.</summary>
    internal static (bool Present, string? Value) MappedLevel(JsonElement model, string level) =>
        model.TryGetProperty("thinkingLevelMap", out var map) && map.ValueKind == JsonValueKind.Object && map.TryGetProperty(level, out var value)
            ? (true, value.ValueKind == JsonValueKind.String ? value.GetString() : null) : (false, null);

    private static readonly string[] UnsupportedStrictKeys = ["$ref", "$defs", "definitions", "allOf", "oneOf", "patternProperties",
        "dependentSchemas", "dependencies", "unevaluatedProperties", "propertyNames", "contains", "prefixItems", "not", "if", "then", "else"];

    private sealed class UnsupportedStrictSchema(string message) : Exception(message);

    /// <summary>resolveJsonSchemaStrictSampling: true when a json_schema tool can be sent strict; throws for "require" tools that cannot.</summary>
    internal static bool? ResolveStrict(JsonObject tool, bool supportsStrictMode)
    {
        if (tool["constrainedSampling"] is not JsonObject config || Text(config, "type") != "json_schema") return null;
        var name = Text(tool, "name");
        if (supportsStrictMode)
        {
            try { _ = MakeStrict(tool["parameters"]); return true; }
            catch (UnsupportedStrictSchema error)
            {
                if (Text(config, "strict") != "require") return null;
                throw new ProviderTranscriptException($"Tool \"{name}\" requires JSON-schema constrained sampling, but {error.Message}.");
            }
        }
        if (Text(config, "strict") == "require")
            throw new ProviderTranscriptException($"Tool \"{name}\" requires JSON-schema constrained sampling, but strict tools are unsupported.");
        return null;
    }

    /// <summary>getJsonSchemaToolParameters: the strict form when strict, else the declared parameters.</summary>
    internal static JsonNode? ToolParameters(JsonObject tool, bool? strict) => strict == true ? MakeStrict(tool["parameters"]) : tool["parameters"]?.DeepClone();

    internal static JsonObject MakeStrict(JsonNode? schema)
    {
        if (schema?.DeepClone() is not JsonObject cloned) throw new UnsupportedStrictSchema("root schema must have type object");
        Strict(cloned);
        if (!IsType(cloned, "object")) throw new UnsupportedStrictSchema("root schema must have type object");
        return cloned;
    }

    private static bool IsType(JsonObject schema, string type) => Text(schema, "type") == type;
    private static bool IsStructured(JsonNode? schema)
    {
        if (schema is not JsonObject value) return false;
        var types = value["type"] is JsonArray array ? array.Select(item => item?.GetValueKind() == JsonValueKind.String ? item.GetValue<string>() : null).ToList()
            : Text(value, "type") is { } single ? [single] : [];
        return types.Contains("object") || types.Contains("array") || value.ContainsKey("properties") || value.ContainsKey("items");
    }
    private static bool AllowsNull(JsonNode? schema)
    {
        if (schema is not JsonObject value) return false;
        if (Text(value, "type") == "null" || value["type"] is JsonArray types && types.Any(item => item?.GetValueKind() == JsonValueKind.String && item.GetValue<string>() == "null")) return true;
        if (value.ContainsKey("const") && value["const"] is null || value["enum"] is JsonArray values && values.Any(item => item is null)) return true;
        return value["anyOf"] is JsonArray variants && variants.Any(AllowsNull);
    }
    private static void Strict(JsonNode? node)
    {
        if (node is not JsonObject schema) throw new UnsupportedStrictSchema("boolean schemas are unsupported");
        foreach (var key in UnsupportedStrictKeys) if (schema.ContainsKey(key)) throw new UnsupportedStrictSchema(key + " schemas are unsupported");
        if (schema.ContainsKey("anyOf"))
        {
            if (schema["anyOf"] is not JsonArray variants || variants.Count == 0) throw new UnsupportedStrictSchema("anyOf must contain at least one schema");
            foreach (var variant in variants)
            {
                if (IsStructured(variant)) throw new UnsupportedStrictSchema("object and array unions are unsupported");
                Strict(variant);
            }
        }
        if (schema.ContainsKey("items"))
        {
            if (schema["items"] is JsonArray) throw new UnsupportedStrictSchema("tuple schemas are unsupported");
            Strict(schema["items"]);
        }
        var isObject = IsType(schema, "object");
        if (schema.ContainsKey("properties") && !isObject) throw new UnsupportedStrictSchema("properties require type object");
        if (!isObject) return;
        if (schema.ContainsKey("additionalProperties") && schema["additionalProperties"]?.GetValueKind() != JsonValueKind.False)
            throw new UnsupportedStrictSchema("schema-valued or true additionalProperties is unsupported");
        if (schema.ContainsKey("properties") && schema["properties"] is not JsonObject) throw new UnsupportedStrictSchema("object properties must be a schema map");
        if (schema.ContainsKey("required") && (schema["required"] is not JsonArray requiredArray || requiredArray.Any(item => item?.GetValueKind() != JsonValueKind.String)))
            throw new UnsupportedStrictSchema("object required must be a string array");
        var properties = schema["properties"] as JsonObject ?? [];
        var names = properties.Select(pair => pair.Key).ToList();
        var required = (schema["required"] as JsonArray)?.Select(item => item!.GetValue<string>()).ToHashSet(StringComparer.Ordinal) ?? [];
        if (required.Any(key => !names.Contains(key))) throw new UnsupportedStrictSchema("required contains an unknown property");
        foreach (var name in names)
        {
            var property = properties[name];
            Strict(property);
            if (!required.Contains(name) && !AllowsNull(property))
            {
                // Assignment keeps the property's slot; the replaced node is detached first.
                properties[name] = null;
                properties[name] = new JsonObject { ["anyOf"] = new JsonArray(property, new JsonObject { ["type"] = "null" }) };
            }
        }
        schema["required"] = new JsonArray(names.Select(name => (JsonNode)JsonValue.Create(name)).ToArray());
        schema["additionalProperties"] = false;
    }
}
