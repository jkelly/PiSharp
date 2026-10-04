// Derived from Pi d86654abb8862e201933517d6f1fce9f88dd117f (MIT),
// api/google-generative-ai.ts, google-shared.ts, transform-messages.ts and utils/transcript.ts.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.GoogleGenerativeAI;

/// <summary>Pure direct-stream SDK parameter projection. Inputs and full model JSON remain owned.</summary>
public static class GoogleRequestProjector
{
    public static JsonData Project(ChatRequest request, GoogleGenerativeAIOptions options)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(options); options.Validate();
        var model = options.ModelMetadata.Value;
        if (request.Model.Api != "google-generative-ai" || GoogleData.String(model, "api") != request.Model.Api ||
            GoogleData.String(model, "provider") != request.Model.Provider || GoogleData.String(model, "id") != request.Model.Id)
            throw GoogleData.Fail(GoogleFailure.Configuration);
        if (request.Messages.Sum(m => (long)Encoding.UTF8.GetByteCount(m.WireBody.ToString())) > options.MaximumPayloadBytes)
            throw GoogleData.Fail(GoogleFailure.ResourceLimit);
        var prompts = new List<string>(); var sections = new List<KeyValuePair<string, string>>();
        var tools = new List<JsonObject>(); var conversation = new List<JsonObject>();
        foreach (var message in request.Messages)
        {
            var body = JsonNode.Parse(message.WireBody.ToString()) as JsonObject ?? throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
            if (Text(body, "role") != message.Role) throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
            if (message.Role != "system") { conversation.Add(body); continue; }
            var text = ContentText(body["content"]); if (text.Length > 0) prompts.Add(text);
            if (body["sections"] is JsonObject patches)
                foreach (var patch in patches)
                {
                    var index = sections.FindIndex(x => x.Key == patch.Key);
                    if (patch.Value is null) { if (index >= 0) sections.RemoveAt(index); }
                    else { var next = new KeyValuePair<string, string>(patch.Key, patch.Value.GetValue<string>());
                        if (index < 0) sections.Add(next); else sections[index] = next; }
                }
            if (body["toolsRemoved"] is JsonArray removed)
                foreach (var item in removed) tools.RemoveAll(t => Text(t, "name") == Text(item, "name"));
            if (body["toolsAdded"] is JsonArray added)
                foreach (var item in added)
                {
                    var tool = item as JsonObject ?? throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
                    var index = tools.FindIndex(t => Text(t, "name") == Text(tool, "name"));
                    if (index < 0) tools.Add(tool); else tools[index] = tool;
                }
        }
        var prompt = string.Join("\n\n", prompts);
        foreach (var section in sections.Where(x => IsIndex(x.Key)).OrderBy(x => uint.Parse(x.Key, System.Globalization.CultureInfo.InvariantCulture))
            .Concat(sections.Where(x => !IsIndex(x.Key))))
        { if (section.Value.Length == 0) continue; if (prompt.Length > 0) prompt += "\n\n"; prompt += section.Value; }
        var contents = ConvertConversation(conversation, request.Model, model);
        var config = new JsonObject();
        if (options.Temperature is { } temperature) config["temperature"] = temperature;
        if (options.MaxTokens is { } maximum) config["maxOutputTokens"] = maximum;
        if (prompt.Length > 0) config["systemInstruction"] = Sanitize(prompt);
        if (tools.Count > 0)
        {
            var declarations = new JsonArray(); var strictAny = false;
            foreach (var tool in tools)
            {
                var parameters = tool["parameters"]?.DeepClone() ?? throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
                var sampling = tool["constrainedSampling"] as JsonObject;
                if (Text(sampling, "type") == "json_schema")
                {
                    var strict = Major(request.Model.Id) >= 3;
                    if (strict) try { MakeStrict(parameters); if (Text(parameters, "type") != "object") throw GoogleData.Fail(GoogleFailure.UnsupportedValue); }
                        catch (Exception error) when (error is GoogleGenerativeAIException or InvalidOperationException)
                        { strict = false; parameters = tool["parameters"]!.DeepClone(); }
                    if (!strict && Text(sampling, "strict") == "require") throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
                    strictAny |= strict;
                }
                var declaration = new JsonObject { ["name"] = Text(tool, "name"), ["parametersJsonSchema"] = parameters };
                if (tool.ContainsKey("description")) declaration["description"] = tool["description"]?.DeepClone();
                declarations.Add(declaration);
            }
            config["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = declarations });
            var mode = options.ToolChoice is "none" or "any" ? options.ToolChoice.ToUpperInvariant() :
                strictAny ? "VALIDATED" : options.ToolChoice is null ? null : "AUTO";
            if (mode is not null) config["toolConfig"] = new JsonObject { ["functionCallingConfig"] = new JsonObject { ["mode"] = mode } };
        }
        if (GoogleData.True(model, "reasoning") && options.Thinking is { } thinking)
        {
            var control = new JsonObject();
            if (thinking.Enabled)
            {
                control["includeThoughts"] = true;
                if (thinking.Level is { } level)
                {
                    if (level is not ("THINKING_LEVEL_UNSPECIFIED" or "MINIMAL" or "LOW" or "MEDIUM" or "HIGH"))
                        throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
                    control["thinkingLevel"] = level;
                }
                else if (thinking.BudgetTokens is { } budget) control["thinkingBudget"] = budget;
            }
            else
            {
                // Gemini 3 cannot always switch off; use its first declared supported level.
                if (UsesLevel(request.Model.Id) && model.TryGetProperty("thinkingLevelMap", out var levels) && levels.ValueKind == JsonValueKind.Object)
                {
                    var fallback = new[] { "off", "minimal", "low", "medium", "high", "xhigh", "max" }.FirstOrDefault(level =>
                        levels.TryGetProperty(level, out var mapped) ? mapped.ValueKind != JsonValueKind.Null : level is not ("xhigh" or "max")) ?? "off";
                    if (fallback == "off") control["thinkingBudget"] = 0;
                    else
                    {
                        var resolved = levels.TryGetProperty(fallback, out var mapped) && mapped.ValueKind == JsonValueKind.String ? mapped.GetString()!.ToLowerInvariant() : fallback;
                        if (resolved is not ("minimal" or "low" or "medium" or "high")) throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
                        control["thinkingLevel"] = resolved.ToUpperInvariant();
                    }
                }
                else control["thinkingBudget"] = 0;
            }
            config["thinkingConfig"] = control;
        }
        return GoogleData.Admit(JsonData.Parse(new JsonObject { ["model"] = request.Model.Id, ["contents"] = contents, ["config"] = config }.ToJsonString()), options);
    }

    internal static JsonObject WireBody(JsonData parameters)
    {
        var root = JsonNode.Parse(parameters.ToString()) as JsonObject ?? throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
        var config = root["config"] as JsonObject ?? throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
        var body = new JsonObject { ["contents"] = root["contents"]?.DeepClone() ?? throw GoogleData.Fail(GoogleFailure.UnsupportedValue) };
        var generation = new JsonObject();
        foreach (var field in config)
        {
            if (field.Key == "systemInstruction")
                body[field.Key] = field.Value is JsonValue ? new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = field.Value.GetValue<string>() }) } : field.Value?.DeepClone();
            else if (field.Key is "tools" or "toolConfig") body[field.Key] = field.Value?.DeepClone();
            else if (field.Key is "temperature" or "maxOutputTokens" or "thinkingConfig") generation[field.Key] = field.Value?.DeepClone();
            else throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
        }
        if (generation.Count > 0) body["generationConfig"] = generation;
        return body;
    }
    private static JsonArray ConvertConversation(List<JsonObject> messages, ModelDescriptor model, JsonElement metadata)
    {
        var result = new JsonArray(); var pending = new List<JsonObject>(); var answered = new HashSet<string>(StringComparer.Ordinal);
        var images = metadata.TryGetProperty("input", out var input) && input.EnumerateArray().Any(x => x.GetString() == "image");
        var includeId = model.Id.StartsWith("claude-", StringComparison.Ordinal) || model.Id.StartsWith("gpt-oss-", StringComparison.Ordinal) || Major(model.Id) >= 3;
        // transformMessages normalizes the entire history before filtering failed turns or
        // inserting missing results. Keep mappings only for changed foreign IDs and bind
        // each real result at its original position; synthetic results are already normalized.
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (Text(message, "role") == "assistant" && includeId &&
                !(Text(message, "provider") == model.Provider && Text(message, "model") == model.Id && Text(message, "api") == model.Api))
            {
                foreach (var block in message["content"] as JsonArray ?? new JsonArray())
                {
                    if (Text(block, "type") != "toolCall") continue;
                    var id = Text(block, "id") ?? throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
                    var normalized = Regex.Replace(id, "[^a-zA-Z0-9_-]", "_")[..Math.Min(id.Length, 64)];
                    if (normalized == id) continue;
                    ids[id] = normalized;
                    block!["id"] = normalized;
                }
            }
            else if (Text(message, "role") == "toolResult" && Text(message, "toolCallId") is { } resultId && ids.TryGetValue(resultId, out var resultIdNormalized))
                message["toolCallId"] = resultIdNormalized;
        }
        void Response(JsonObject tool, JsonArray blocks, bool error)
        {
            var texts = new List<string>(); var imageParts = new JsonArray(); var previousPlaceholder = false;
            foreach (var block in blocks)
            {
                if (Text(block, "type") == "image")
                {
                    if (images) imageParts.Add(Image(block!));
                    else if (!previousPlaceholder) texts.Add("(tool image omitted: model does not support images)");
                    previousPlaceholder = true;
                }
                else { var text = Text(block, "text") ?? throw GoogleData.Fail(GoogleFailure.UnsupportedValue); texts.Add(text); previousPlaceholder = text == "(tool image omitted: model does not support images)"; }
            }
            var value = string.Join("\n", texts);
            var response = new JsonObject { ["name"] = Text(tool, "toolName") ?? Text(tool, "name"),
                ["response"] = new JsonObject { [error ? "error" : "output"] = value.Length > 0 ? Sanitize(value) : imageParts.Count > 0 ? "(see attached image)" : "" } };
            var id = Text(tool, "toolCallId") ?? Text(tool, "id") ?? "";
            if (includeId) response["id"] = id;
            var multimodal = Major(model.Id) is null or >= 3;
            if (imageParts.Count > 0 && multimodal) response["parts"] = imageParts.DeepClone();
            var part = new JsonObject { ["functionResponse"] = response };
            if (result.LastOrDefault() is JsonObject last && Text(last, "role") == "user" &&
                last["parts"] is JsonArray parts && parts.Any(p => p is JsonObject obj && obj.ContainsKey("functionResponse"))) parts.Add(part);
            else result.Add(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(part) });
            if (imageParts.Count > 0 && !multimodal)
            {
                var separate = new JsonArray(new JsonObject { ["text"] = "Tool result image:" });
                foreach (var image in imageParts) separate.Add(image!.DeepClone());
                result.Add(new JsonObject { ["role"] = "user", ["parts"] = separate });
            }
        }
        void ClosePending()
        {
            foreach (var call in pending)
                if (!answered.Contains(Text(call, "id")!)) Response(call, new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "No result provided" }), true);
            pending.Clear(); answered.Clear();
        }
        foreach (var message in messages)
        {
            var role = Text(message, "role"); var content = message["content"];
            if (role is "user" or "assistant") ClosePending();
            if (role == "user")
            {
                var parts = new JsonArray(); var previousPlaceholder = false;
                if (content is JsonValue scalar) parts.Add(new JsonObject { ["text"] = Sanitize(scalar.GetValue<string>()) });
                else foreach (var block in content as JsonArray ?? new JsonArray())
                {
                    if (Text(block, "type") == "image")
                    {
                        if (images) parts.Add(Image(block!));
                        else if (!previousPlaceholder) parts.Add(new JsonObject { ["text"] = "(image omitted: model does not support images)" });
                        previousPlaceholder = true;
                    }
                    else { var text = Text(block, "text") ?? throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
                        parts.Add(new JsonObject { ["text"] = Sanitize(text) }); previousPlaceholder = text == "(image omitted: model does not support images)"; }
                }
                if (parts.Count > 0) result.Add(new JsonObject { ["role"] = "user", ["parts"] = parts });
            }
            else if (role == "assistant")
            {
                if (Text(message, "stopReason") is "error" or "aborted") continue;
                var same = Text(message, "provider") == model.Provider && Text(message, "model") == model.Id && Text(message, "api") == model.Api;
                var parts = new JsonArray();
                foreach (var block in content as JsonArray ?? new JsonArray())
                {
                    var type = Text(block, "type");
                    var signature = same ? Text(block, type == "text" ? "textSignature" : type == "thinking" ? "thinkingSignature" : "thoughtSignature") : null;
                    if (!ValidSignature(signature)) signature = null;
                    if (type is "text" or "thinking")
                    {
                        if (type == "thinking" && block?["redacted"]?.GetValue<bool>() == true && !same) continue;
                        var text = Text(block, type == "text" ? "text" : "thinking") ?? "";
                        if (IsBlank(text) && signature is null) continue;
                        var part = new JsonObject { ["text"] = Sanitize(text) };
                        if (type == "thinking" && same) part["thought"] = true;
                        if (signature is not null) part["thoughtSignature"] = signature;
                        parts.Add(part);
                    }
                    else if (type == "toolCall")
                    {
                        var id = Text(block, "id") ?? throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
                        var call = new JsonObject { ["name"] = Text(block, "name"), ["args"] = block!["arguments"]?.DeepClone() ?? new JsonObject() };
                        if (includeId) call["id"] = id;
                        var part = new JsonObject { ["functionCall"] = call }; if (signature is not null) part["thoughtSignature"] = signature;
                        parts.Add(part); var pendingCall = (JsonObject)block!.DeepClone(); pending.Add(pendingCall);
                    }
                    else throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
                }
                if (parts.Count > 0) result.Add(new JsonObject { ["role"] = "model", ["parts"] = parts });
            }
            else if (role == "toolResult")
            {
                var id = Text(message, "toolCallId") ?? ""; answered.Add(id);
                Response(message, content as JsonArray ?? new JsonArray(), message["isError"]?.GetValue<bool>() == true);
            }
            else throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
        }
        ClosePending(); return result;
    }
    private static JsonObject Image(JsonNode block) => new() { ["inlineData"] = new JsonObject { ["mimeType"] = Text(block, "mimeType"), ["data"] = Text(block, "data") } };
    private static string? Text(JsonNode? value, string name) => value is JsonObject obj && obj[name] is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;
    private static string ContentText(JsonNode? content) => content is JsonValue value ? value.GetValue<string>() :
        string.Join("\n", (content as JsonArray ?? new JsonArray()).Where(x => Text(x, "type") == "text").Select(x => Text(x, "text") ?? ""));
    internal static int? Major(string id) => Regex.Match(id.ToLowerInvariant(), @"^gemini(?:-live)?-(\d+)") is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n) ? n : null;
    private static bool UsesLevel(string id)
    {
        var normalized = id.ToLowerInvariant();
        return Regex.IsMatch(normalized, @"gemini-3(?:\.[0-9]+)?-(?:pro|flash)|gemma-?4") || normalized is "gemini-flash-latest" or "gemini-flash-lite-latest";
    }
    private static bool ValidSignature(string? value) => value is { Length: > 0 } && value.Length % 4 == 0 && Regex.IsMatch(value, @"^[A-Za-z0-9+/]+={0,2}$");
    private static bool IsIndex(string key) => uint.TryParse(key, System.Globalization.NumberStyles.None,
        System.Globalization.CultureInfo.InvariantCulture, out var index) && index != uint.MaxValue &&
        index.ToString(System.Globalization.CultureInfo.InvariantCulture) == key;
    private static bool IsBlank(string text) => text.All(c => c is '\t' or '\n' or '\v' or '\f' or '\r' or ' ' or '\u00A0' or '\u1680' or
        '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000' or '\uFEFF' || c is >= '\u2000' and <= '\u200A');
    private static string Sanitize(string text)
    {
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { result.Append(c).Append(text[++i]); }
            else result.Append(char.IsSurrogate(c) ? '\uFFFD' : c);
        }
        return result.ToString();
    }
    private static void MakeStrict(JsonNode node)
    {
        var schema = node as JsonObject ?? throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
        foreach (var name in new[] { "$ref", "$defs", "definitions", "allOf", "oneOf", "patternProperties", "dependentSchemas", "dependencies", "unevaluatedProperties", "propertyNames", "contains", "prefixItems", "not", "if", "then", "else" })
            if (schema.ContainsKey(name)) throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
        if (schema.ContainsKey("anyOf"))
        {
            if (schema["anyOf"] is not JsonArray variants || variants.Count == 0) throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
            foreach (var variant in variants)
            {
                if (IsStructuredSchema(variant))
                    throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
                MakeStrict(variant ?? throw GoogleData.Fail(GoogleFailure.UnsupportedValue));
            }
        }
        if (schema.ContainsKey("items")) MakeStrict(schema["items"] ?? throw GoogleData.Fail(GoogleFailure.UnsupportedValue));
        if (Text(schema, "type") != "object")
        {
            if (schema.ContainsKey("properties")) throw GoogleData.Fail(GoogleFailure.UnsupportedValue); return;
        }
        if (schema.ContainsKey("additionalProperties") && schema["additionalProperties"]?.ToJsonString() != "false")
            throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
        if (schema.ContainsKey("properties") && schema["properties"] is not JsonObject) throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
        var properties = schema["properties"] as JsonObject ?? new JsonObject();
        if (schema.ContainsKey("required") && schema["required"] is not JsonArray) throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
        var required = (schema["required"] as JsonArray ?? new JsonArray()).Select(x => x?.GetValue<string>() ?? throw GoogleData.Fail(GoogleFailure.UnsupportedValue)).ToHashSet(StringComparer.Ordinal);
        if (required.Any(name => !properties.ContainsKey(name))) throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
        foreach (var property in properties.ToArray())
        {
            MakeStrict(property.Value ?? throw GoogleData.Fail(GoogleFailure.UnsupportedValue));
            if (!required.Contains(property.Key) && !AllowsNull(property.Value!))
                properties[property.Key] = new JsonObject { ["anyOf"] = new JsonArray(property.Value!.DeepClone(), new JsonObject { ["type"] = "null" }) };
        }
        schema["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray());
        schema["additionalProperties"] = false;
    }
    // Source checks structured shapes only in incoming anyOf branches; do not
    // globally rewrite or reject array-valued types that its other paths allow.
    private static bool IsStructuredSchema(JsonNode? node)
    {
        if (node is not JsonObject schema) return false;
        var type = Text(schema, "type");
        return type is "object" or "array" ||
            schema["type"] is JsonArray types && types.Any(value =>
                value is JsonValue scalar && scalar.TryGetValue<string>(out var name) && name is "object" or "array") ||
            schema.ContainsKey("properties") || schema.ContainsKey("items");
    }
    private static bool AllowsNull(JsonNode node) => node is JsonObject obj &&
        (Text(obj, "type") == "null" || obj["type"] is JsonArray types && types.Any(x => x?.ToJsonString() == "\"null\"") ||
        obj.ContainsKey("const") && obj["const"] is null || obj["enum"] is JsonArray values && values.Any(x => x is null) ||
        obj["anyOf"] is JsonArray variants && variants.Any(x => x is not null && AllowsNull(x)));
}
