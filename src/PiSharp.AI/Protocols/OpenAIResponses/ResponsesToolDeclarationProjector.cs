using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.OpenAIResponses;

public sealed record ResponsesToolDeclarationProjectionOptions(
    bool SupportsStrictMode = false, bool? Strict = false,
    int MaximumMessages = 256, int MaximumEntryCharacters = PiRequestBudget.RequestEntryCharacters, int MaximumInputCharacters = PiRequestBudget.RequestPayloadBytes,
    // openai-responses-shared.ts convertResponsesTools declares every tool: no tool or declaration count bound.
    int MaximumDeclarations = int.MaxValue, int MaximumActiveTools = int.MaxValue, int MaximumJsonDepth = 32,
    int MaximumOutputCharacters = PiRequestBudget.RequestPayloadBytes, int MaximumOutputBytes = PiRequestBudget.RequestPayloadBytes)
{
    /// <summary>Model compat <c>supportsOpenAIGrammarTools</c>: grammar tools become custom tools and replay as custom tool calls.</summary>
    public bool SupportsOpenAIGrammarTools { get; init; }
}

/// <summary>Replays owned system-message tool deltas into standard top-level Responses function tools.</summary>
public sealed class ResponsesToolDeclarationProjector
{
    private readonly ResponsesToolDeclarationProjectionOptions _options;
    private static readonly JsonSerializerOptions OutputJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly string[] UnsupportedStrictKeys = ["$ref", "$defs", "definitions", "allOf", "oneOf",
        "patternProperties", "dependentSchemas", "dependencies", "unevaluatedProperties", "propertyNames", "contains",
        "prefixItems", "not", "if", "then", "else"];

    public ResponsesToolDeclarationProjector(ResponsesToolDeclarationProjectionOptions? options = null)
    {
        _options = options ?? new();
        if (_options.MaximumMessages <= 0 || _options.MaximumEntryCharacters <= 0 || _options.MaximumInputCharacters <= 0 ||
            _options.MaximumDeclarations <= 0 || _options.MaximumActiveTools <= 0 || _options.MaximumJsonDepth is < 1 or > 64 ||
            _options.MaximumOutputCharacters < 2 || _options.MaximumOutputBytes < 2)
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid Responses tool declaration limits.");
    }

    public JsonData Project(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (request.Messages.IsDefault) throw Failure(ResponsesProjectionFailure.InvalidTranscript);
            if (request.Messages.Length > _options.MaximumMessages) throw Failure(ResponsesProjectionFailure.ResourceLimit);
            var active = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            var order = new List<string>();
            long inputCharacters = 0; var declarations = 0;
            foreach (var message in request.Messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (message is null || message.WireBody is null) throw Failure(ResponsesProjectionFailure.InvalidTranscript);
                var body = message.WireBody.Value; var length = body.GetRawText().Length;
                if (length > _options.MaximumEntryCharacters || length > _options.MaximumInputCharacters - inputCharacters)
                    throw Failure(ResponsesProjectionFailure.ResourceLimit);
                inputCharacters += length;
                CheckJson(body, 0, cancellationToken);
                if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("role", out var role) ||
                    role.ValueKind != JsonValueKind.String || Text(role) != message.Role)
                    throw Failure(ResponsesProjectionFailure.InvalidTranscript);
                if (message.Role != "system") continue;
                if (body.TryGetProperty("toolsRemoved", out var removed))
                    foreach (var reference in Array(removed))
                    {
                        if (++declarations > _options.MaximumDeclarations) throw Failure(ResponsesProjectionFailure.ResourceLimit);
                        var name = Name(reference);
                        if (active.Remove(name)) order.Remove(name);
                    }
                if (body.TryGetProperty("toolsAdded", out var added))
                    foreach (var declaration in Array(added))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (++declarations > _options.MaximumDeclarations) throw Failure(ResponsesProjectionFailure.ResourceLimit);
                        var name = Name(declaration);
                        var projected = Convert(declaration, cancellationToken);
                        if (!active.ContainsKey(name))
                        {
                            if (active.Count >= _options.MaximumActiveTools) throw Failure(ResponsesProjectionFailure.ResourceLimit);
                            order.Add(name);
                        }
                        active[name] = projected;
                    }
            }
            var output = new JsonArray(); long characters = 2; long bytes = 2;
            foreach (var name in order)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = active[name]; var raw = item.ToJsonString(OutputJson);
                var separator = output.Count == 0 ? 0 : 1;
                characters += raw.Length + separator; bytes += Encoding.UTF8.GetByteCount(raw) + separator;
                if (characters > _options.MaximumOutputCharacters || bytes > _options.MaximumOutputBytes)
                    throw Failure(ResponsesProjectionFailure.ResourceLimit);
                using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 64 });
                CheckJson(document.RootElement, 1, cancellationToken);
                output.Add(item);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return JsonData.Parse(output.ToJsonString(OutputJson));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw Failure(ResponsesProjectionFailure.UnsupportedContent); }
    }

    private JsonObject Convert(JsonElement declaration, CancellationToken token)
    {
        var name = Name(declaration);
        if (declaration.TryGetProperty("type", out var kind) && Text(kind) != "function")
            throw Failure(ResponsesProjectionFailure.UnsupportedContent);
        var description = Text(declaration.GetProperty("description"));
        var parameters = declaration.GetProperty("parameters");
        if (parameters.ValueKind != JsonValueKind.Object) throw Failure(ResponsesProjectionFailure.UnsupportedContent);
        var strict = _options.Strict;
        JsonObject? strictParameters = null;
        // Pi abe508 openai-responses-shared.ts convertResponsesTools: a supported grammar makes a custom tool.
        if (ResponsesGrammar.Resolve(declaration, _options.SupportsOpenAIGrammarTools) is { } grammar)
            return new JsonObject { ["type"] = "custom", ["name"] = name, ["description"] = description, ["format"] = new JsonObject
                { ["type"] = "grammar", ["syntax"] = grammar.Syntax, ["definition"] = grammar.Definition } };
        if (declaration.TryGetProperty("constrainedSampling", out var sampling) && sampling.ValueKind != JsonValueKind.False)
        {
            // Pi d866 constrained-sampling.ts: grammar is ignored when custom grammar tools are unsupported.
            if (sampling.ValueKind != JsonValueKind.Object)
                throw Failure(ResponsesProjectionFailure.UnsupportedContent);
            var samplingType = Text(sampling.GetProperty("type"));
            if (samplingType == "grammar") return FunctionTool(name, description, parameters, strict, token);
            if (samplingType != "json_schema")
                throw Failure(ResponsesProjectionFailure.UnsupportedContent);
            var preference = Text(sampling.GetProperty("strict"));
            if (preference is not ("prefer" or "require")) throw Failure(ResponsesProjectionFailure.UnsupportedContent);
            if (_options.SupportsStrictMode)
            {
                try { strictParameters = MakeStrict(parameters, token); strict = true; }
                catch (StrictSchemaException)
                { if (preference == "require") throw Failure(ResponsesProjectionFailure.UnsupportedContent); }
            }
            else if (preference == "require") throw Failure(ResponsesProjectionFailure.UnsupportedContent);
        }
        return FunctionTool(name, description, parameters, strict, token, strictParameters);
    }

    private JsonObject FunctionTool(string name, string description, JsonElement parameters, bool? strict,
        CancellationToken token, JsonObject? strictParameters = null)
    {
        if (strict == true && strictParameters is null)
        {
            try { strictParameters = MakeStrict(parameters, token); }
            catch (StrictSchemaException) { throw Failure(ResponsesProjectionFailure.UnsupportedContent); }
        }
        var result = new JsonObject { ["type"] = "function", ["name"] = name, ["description"] = description,
            ["parameters"] = strictParameters ?? JsonNode.Parse(parameters.GetRawText()) };
        if (_options.SupportsStrictMode) result["strict"] = strict is null ? null : JsonValue.Create(strict.Value);
        return result;
    }

    private static JsonObject MakeStrict(JsonElement parameters, CancellationToken token)
    {
        var root = JsonNode.Parse(parameters.GetRawText())!.AsObject();
        MakeNodeStrict(root, token);
        if (!IsType(root, "object")) throw new StrictSchemaException();
        return root;
    }

    private static void MakeNodeStrict(JsonNode? node, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (node is not JsonObject schema || UnsupportedStrictKeys.Any(schema.ContainsKey)) throw new StrictSchemaException();
        if (schema.ContainsKey("anyOf"))
        {
            if (schema["anyOf"] is not JsonArray variants || variants.Count == 0) throw new StrictSchemaException();
            foreach (var variant in variants)
            {
                if (variant is JsonObject item && (IsType(item, "object") || IsType(item, "array") ||
                    item["type"] is JsonArray types && types.Any(value => value is JsonValue text && text.TryGetValue<string>(out var type) && type is "object" or "array") ||
                    item.ContainsKey("properties") || item.ContainsKey("items"))) throw new StrictSchemaException();
                MakeNodeStrict(variant, token);
            }
        }
        if (schema.ContainsKey("items")) MakeNodeStrict(schema["items"], token);
        if (schema.ContainsKey("properties") && !IsType(schema, "object")) throw new StrictSchemaException();
        if (!IsType(schema, "object")) return;
        if (schema.ContainsKey("additionalProperties") && schema["additionalProperties"]?.ToJsonString() != "false")
            throw new StrictSchemaException();
        if (schema.ContainsKey("properties") && schema["properties"] is not JsonObject) throw new StrictSchemaException();
        if (schema.ContainsKey("required") && (schema["required"] is not JsonArray requiredValues ||
            requiredValues.Any(value => value is not JsonValue text || !text.TryGetValue<string>(out _)))) throw new StrictSchemaException();
        var properties = schema["properties"] as JsonObject ?? new JsonObject();
        var names = properties.Select((property, index) => (property.Key, index, numeric: ArrayIndex(property.Key)))
            .OrderBy(item => item.numeric is null ? 1 : 0).ThenBy(item => item.numeric ?? 0).ThenBy(item => item.index)
            .Select(item => item.Key).ToArray();
        HashSet<string> required = schema["required"] is JsonArray original ? original.Select(value => value!.GetValue<string>()).ToHashSet(StringComparer.Ordinal) : new(StringComparer.Ordinal);
        if (required.Any(name => !properties.ContainsKey(name))) throw new StrictSchemaException();
        foreach (var name in names)
        {
            var property = properties[name]; MakeNodeStrict(property, token);
            if (!required.Contains(name) && !AllowsNull(property))
            {
                properties.Remove(name);
                properties[name] = new JsonObject { ["anyOf"] = new JsonArray(property, new JsonObject { ["type"] = "null" }) };
            }
        }
        schema["required"] = new JsonArray(names.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
        schema["additionalProperties"] = false;
    }

    private static bool AllowsNull(JsonNode? node) => node is JsonObject schema && (IsType(schema, "null") ||
        schema["type"] is JsonArray types && types.Any(value => value is JsonValue text && text.TryGetValue<string>(out var type) && type == "null") ||
        schema.ContainsKey("const") && schema["const"] is null || schema["enum"] is JsonArray values && values.Any(value => value is null) ||
        schema["anyOf"] is JsonArray variants && variants.Any(AllowsNull));
    private static bool IsType(JsonObject schema, string expected) => schema["type"] is JsonValue value && value.TryGetValue<string>(out var type) && type == expected;
    private static uint? ArrayIndex(string name) => uint.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var value) &&
        value != uint.MaxValue && name == value.ToString(CultureInfo.InvariantCulture) ? value : null;
    private static JsonElement.ArrayEnumerator Array(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() :
        throw Failure(ResponsesProjectionFailure.UnsupportedContent);
    private static string Name(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Failure(ResponsesProjectionFailure.UnsupportedContent);
        var name = Text(value.GetProperty("name"));
        return !string.IsNullOrWhiteSpace(name) ? name : throw Failure(ResponsesProjectionFailure.UnsupportedContent);
    }
    private static string Text(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) throw Failure(ResponsesProjectionFailure.UnsupportedContent);
        try { return value.GetString()!; }
        catch (InvalidOperationException) { throw Failure(ResponsesProjectionFailure.UnsupportedUnicode); }
    }
    private void CheckJson(JsonElement value, int depth, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            if (depth >= _options.MaximumJsonDepth) throw Failure(ResponsesProjectionFailure.ResourceLimit);
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw Failure(ResponsesProjectionFailure.UnsupportedContent);
                    CheckString(property.Name); CheckJson(property.Value, depth + 1, token);
                }
            }
            else foreach (var child in value.EnumerateArray()) CheckJson(child, depth + 1, token);
        }
        else if (value.ValueKind == JsonValueKind.String) CheckString(Text(value));
    }
    private static void CheckString(string value)
    {
        for (var index = 0; index < value.Length; index++)
            if (char.IsSurrogate(value[index]) && (!char.IsHighSurrogate(value[index]) || index + 1 == value.Length || !char.IsLowSurrogate(value[++index])))
                throw Failure(ResponsesProjectionFailure.UnsupportedUnicode);
    }
    private static ResponsesProjectionException Failure(ResponsesProjectionFailure failure) => new(failure);
    private sealed class StrictSchemaException : Exception { }
}

// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/constrained-sampling.ts
// (createGrammarToolInputProperties, getGrammarToolInput) and packages/ai/src/utils/transcript.ts (getDeclaredTools).
internal static class ResponsesGrammar
{
    internal static OpenAICompletions.CompletionsGrammar? Resolve(JsonElement tool, bool enabled)
    {
        try { return OpenAICompletions.CompletionsGrammar.Resolve(tool, enabled); }
        catch (OpenAICompletions.CompletionsRequestException) { throw new ResponsesProjectionException(ResponsesProjectionFailure.UnsupportedContent); }
    }

    // Every system toolsAdded definition, removals ignored and the last definition of a name winning, whose grammar is supported.
    internal static Dictionary<string, string> InputProperties(ChatRequest request, bool enabled, CancellationToken token)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!enabled) return properties;
        var declared = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var entry in request.Messages)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Role == "system" && entry.WireBody.Value.TryGetProperty("toolsAdded", out var added) && added.ValueKind == JsonValueKind.Array)
                foreach (var tool in added.EnumerateArray())
                    if (tool.ValueKind == JsonValueKind.Object && tool.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                        declared[name.GetString()!] = tool;
        }
        foreach (var (name, tool) in declared) if (Resolve(tool, true) is { } grammar) properties[name] = grammar.InputProperty;
        return properties;
    }

    // getGrammarToolInput: the grammar input property must hold a string.
    internal static string Input(JsonElement arguments, string property) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(property, out var input) && input.ValueKind == JsonValueKind.String
            ? input.GetString()! : throw new ResponsesProjectionException(ResponsesProjectionFailure.UnsupportedContent);
}
