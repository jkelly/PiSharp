// Decision 0011 (run W4, R7): the Tool Bench's checks. A tool definition is checked before the bench keeps it: a name of letters, digits,
// _ and - (up to 64), an object schema for its parameters ("type": "object" when set), properties that are schemas, required names that
// are properties, known type names, non-empty enums, patterns that compile, and a schema Pi's own argument check can use. A call's
// arguments are checked with Pi's validateToolArguments port (PiSharp.Agent.ToolArgumentValidation: what a Pi agent would accept, with its
// coercions), as a list of violations with JSON pointers. A mock matches a call when its "match" object is a subset of the arguments.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Agent;

namespace PiSharp.WebAgent.Bench;

/// <summary>The outcome of a tool definition check.</summary>
/// <param name="Errors">What is wrong (empty when the definition is valid).</param>
/// <param name="Properties">How many properties the schema has.</param>
/// <param name="Required">How many are required.</param>
/// <param name="AdditionalProperties">The root's additionalProperties when it is a boolean, or null.</param>
public sealed record BenchToolCheck(IReadOnlyList<string> Errors, int Properties, int Required, bool? AdditionalProperties)
{
    /// <summary>Whether the definition is valid.</summary>
    public bool Valid => Errors.Count == 0;

    /// <summary>A line for the editor: "2 properties · 1 required · additionalProperties false".</summary>
    public string Summary
    {
        get
        {
            var text = $"{Properties} {(Properties == 1 ? "property" : "properties")} · {Required} required";
            return AdditionalProperties is { } extra ? text + " · additionalProperties " + (extra ? "true" : "false") : text;
        }
    }
}

/// <summary>Checks of tool definitions, arguments and mock matches.</summary>
public static partial class BenchSchema
{
    /// <summary>The largest parameters schema, as JSON.</summary>
    public const int MaxSchemaBytes = 256 * 1024;

    /// <summary>The longest description.</summary>
    public const int MaxDescription = 10_000;

    /// <summary>The deepest schema.</summary>
    public const int MaxDepth = 32;

    private static readonly string[] TypeNames = ["string", "number", "integer", "boolean", "object", "array", "null"];
    private static readonly string[] SchemaKeywords = ["additionalProperties", "not", "if", "then", "else", "contains", "propertyNames", "unevaluatedProperties", "unevaluatedItems", "additionalItems"];
    private static readonly string[] ListKeywords = ["anyOf", "oneOf", "allOf", "prefixItems"];
    private static readonly string[] CountKeywords = ["minLength", "maxLength", "minItems", "maxItems", "minProperties", "maxProperties"];
    private static readonly string[] NumberKeywords = ["minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf"];

    /// <summary>The empty parameters schema.</summary>
    public static JsonObject EmptyParameters() => new() { ["type"] = "object", ["properties"] = new JsonObject() };

    /// <summary>Checks a tool definition.</summary>
    public static BenchToolCheck CheckTool(string? name, string? description, JsonNode? parameters)
    {
        var errors = new List<string>();
        var trimmed = name?.Trim() ?? "";
        if (!ToolName().IsMatch(trimmed)) errors.Add("The name is letters, digits, _ and -, up to 64 characters.");
        if (description is { Length: > MaxDescription }) errors.Add($"The description is at most {MaxDescription:N0} characters.");
        var properties = 0;
        var required = 0;
        bool? extra = null;
        if (parameters is null) return new BenchToolCheck(errors, 0, 0, null);
        if (parameters is not JsonObject schema)
        {
            errors.Add("The parameters are a JSON Schema object.");
            return new BenchToolCheck(errors, 0, 0, null);
        }
        if (System.Text.Encoding.UTF8.GetByteCount(schema.ToJsonString()) > MaxSchemaBytes) errors.Add($"The parameters' schema is over {MaxSchemaBytes / 1024} KB.");
        if (schema["type"] is { } type && !(type is JsonValue value && value.TryGetValue<string>(out var kind) && kind == "object"))
            errors.Add("The parameters' schema has type \"object\".");
        var names = schema["properties"] is JsonObject props ? props.Select(pair => pair.Key).ToList() : [];
        properties = names.Count;
        if (schema["required"] is JsonArray list)
        {
            required = list.Count;
            foreach (var item in list)
                if (item is JsonValue entry && entry.TryGetValue<string>(out var text) && !names.Contains(text))
                    errors.Add($"required names \"{text}\", which is not one of the properties.");
        }
        if (schema["additionalProperties"] is JsonValue additional && additional.TryGetValue<bool>(out var flag)) extra = flag;
        Walk(schema, "", errors, 0);
        if (errors.Count == 0)
        {
            try
            {
                // Compiles the schema the way Pi's agent would before it runs the tool (an empty call; a missing required argument is fine).
                _ = ToolArgumentValidation.Validate(trimmed, schema, new JsonObject());
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                errors.Add("Pi's argument check cannot use this schema: " + error.Message);
            }
        }
        return new BenchToolCheck(errors, properties, required, extra);
    }

    /// <summary>The tool, checked and trimmed: a built-in gets PiSharp's own declaration, mocks get names.</summary>
    /// <exception cref="ArgumentException">The definition or a mock is not valid.</exception>
    public static BenchTool NormalizeTool(BenchTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var name = tool.Name?.Trim() ?? "";
        string? description;
        JsonObject parameters;
        if (tool.Builtin)
        {
            var declaration = BenchTemplates.Builtin(name) ?? throw new ArgumentException($"{name} is not one of PiSharp's built-in tools.");
            description = declaration.Description;
            parameters = (JsonObject)declaration.Parameters!.DeepClone();
        }
        else
        {
            var check = CheckTool(name, tool.Description, tool.Parameters);
            if (!check.Valid) throw new ArgumentException($"Tool {(name.Length == 0 ? "(no name)" : name)}: {string.Join(" ", check.Errors)}");
            description = string.IsNullOrWhiteSpace(tool.Description) ? null : tool.Description.Trim();
            parameters = tool.Parameters is null ? EmptyParameters() : (JsonObject)tool.Parameters.DeepClone();
        }
        var mocks = tool.Mocks ?? [];
        if (mocks.Count > BenchMock.MaxPerTool) throw new ArgumentException($"Tool {name}: at most {BenchMock.MaxPerTool} mock results.");
        var normalized = new List<BenchMock>();
        for (var index = 0; index < mocks.Count; index++)
        {
            var mock = mocks[index] ?? throw new ArgumentException($"Tool {name}: mock {index + 1} is empty.");
            var label = string.IsNullOrWhiteSpace(mock.Name) ? $"Mock {index + 1}" : mock.Name.Trim();
            if (label.Length > 80) throw new ArgumentException($"Tool {name}: a mock's name is at most 80 characters.");
            if ((mock.Text ?? "").Length > BenchMock.MaxText) throw new ArgumentException($"Tool {name}: mock \"{label}\" is over {BenchMock.MaxText / 1024} KB.");
            normalized.Add(mock with { Name = label, Text = mock.Text ?? "", Match = mock.Match is { Count: > 0 } match ? (JsonObject)match.DeepClone() : null });
        }
        return tool with { Name = name, Description = description, Parameters = parameters, Mocks = normalized };
    }

    /// <summary>The schema violations of <paramref name="arguments"/> (Pi's argument check), or none.</summary>
    public static IReadOnlyList<BenchViolation> CheckArguments(string tool, JsonObject? parameters, JsonNode? arguments, bool builtin)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (arguments is not JsonObject) return [new BenchViolation("/", "The arguments are not a JSON object.")];
        ToolArgumentValidationResult result;
        try
        {
            result = ToolArgumentValidation.Validate(tool, parameters ?? EmptyParameters(), arguments, typeBoxSchema: builtin);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return [new BenchViolation("/", "The schema could not be checked: " + error.Message)];
        }
        return result.IsValid ? [] : Violations(result.ErrorMessage ?? "");
    }

    /// <summary>The violations in a Pi validation message ("  - path: message" lines).</summary>
    public static IReadOnlyList<BenchViolation> Violations(string message)
    {
        var violations = new List<BenchViolation>();
        var body = message.Split("\n\nReceived arguments:", 2)[0];
        foreach (var line in body.Split('\n').Skip(1))
        {
            if (!line.StartsWith("  - ", StringComparison.Ordinal)) continue;
            var text = line[4..];
            var colon = text.IndexOf(": ", StringComparison.Ordinal);
            var path = colon > 0 ? text[..colon] : "root";
            var what = colon > 0 ? text[(colon + 2)..] : text;
            violations.Add(new BenchViolation(path == "root" ? "/" : "/" + path.Replace('.', '/'), what));
        }
        if (violations.Count == 0 && message.Length > 0) violations.Add(new BenchViolation("/", message.Split('\n')[0]));
        return violations;
    }

    /// <summary>Whether <paramref name="match"/> is a subset of <paramref name="value"/> (objects by key, everything else equal).</summary>
    public static bool Matches(JsonNode? match, JsonNode? value)
    {
        switch (match)
        {
            case null:
                return value is null || (value is JsonValue nothing && nothing.GetValueKind() == JsonValueKind.Null);
            case JsonObject wanted:
                if (value is not JsonObject actual) return false;
                foreach (var (key, item) in wanted)
                    if (!actual.TryGetPropertyValue(key, out var found) || !Matches(item, found)) return false;
                return true;
            case JsonArray list:
                if (value is not JsonArray other || other.Count != list.Count) return false;
                for (var index = 0; index < list.Count; index++)
                    if (!Matches(list[index], other[index])) return false;
                return true;
            case JsonValue primitive:
                if (value is not JsonValue candidate) return false;
                var kind = primitive.GetValueKind();
                if (kind == JsonValueKind.Number && candidate.GetValueKind() == JsonValueKind.Number)
                    return primitive.TryGetValue<decimal>(out var a) && candidate.TryGetValue<decimal>(out var b) ? a == b : JsonNode.DeepEquals(primitive, candidate);
                return JsonNode.DeepEquals(primitive, candidate);
            default:
                return false;
        }
    }

    /// <summary>The arguments in one line: <c>city: "Oslo" · unit: "celsius"</c>.</summary>
    public static string ArgumentsLine(JsonObject? arguments, string? raw = null)
    {
        if (arguments is null) return raw is null ? "" : Cut(raw, 80);
        var parts = arguments.Take(4).Select(pair => $"{pair.Key}: {Cut(pair.Value?.ToJsonString(Compact) ?? "null", 48)}").ToList();
        if (arguments.Count > 4) parts.Add($"+{arguments.Count - 4}");
        return string.Join(" · ", parts);
    }

    /// <summary>The first argument's value as text, for the call log (<c>Oslo</c>), or null.</summary>
    public static string? FirstValue(JsonObject? arguments)
    {
        if (arguments is null || arguments.Count == 0) return null;
        var value = arguments.First().Value;
        var text = value is JsonValue item && item.TryGetValue<string>(out var said) ? said : value?.ToJsonString(Compact) ?? "null";
        return Cut(text, 24);
    }

    /// <summary>The parameters in one line: <c>city* · unit</c> (* required).</summary>
    public static string ParametersLine(JsonObject? parameters)
    {
        if (parameters?["properties"] is not JsonObject properties || properties.Count == 0) return "no parameters";
        var required = parameters["required"] is JsonArray list
            ? list.Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : null).OfType<string>().ToHashSet(StringComparer.Ordinal)
            : [];
        return string.Join(" · ", properties.Select(pair => required.Contains(pair.Key) ? pair.Key + "*" : pair.Key));
    }

    /// <summary>JSON indented for display.</summary>
    public static string Indented(JsonNode? node) => node?.ToJsonString(IndentedOptions) ?? "null";

    /// <summary>Text shown as JSON when it is JSON (indented), else as it is.</summary>
    public static string Pretty(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '[')) return text;
        try { return JsonNode.Parse(text) is { } node ? Indented(node) : text; }
        catch (JsonException) { return text; }
    }

    private static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonSerializerOptions IndentedOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Cut(string text, int length) => text.Length > length ? text[..(length - 1)] + "…" : text;

    private static void Walk(JsonNode? node, string path, List<string> errors, int depth)
    {
        if (errors.Count >= 20) return;
        if (depth > MaxDepth)
        {
            errors.Add($"{Where(path)} nests deeper than {MaxDepth} levels.");
            return;
        }
        if (node is JsonValue boolean && boolean.GetValueKind() is JsonValueKind.True or JsonValueKind.False) return;
        if (node is not JsonObject schema)
        {
            errors.Add($"{Where(path)} is not a schema (an object, or true or false).");
            return;
        }
        if (schema["type"] is { } type)
        {
            var names = type switch
            {
                JsonValue single when single.TryGetValue<string>(out var text) => [text],
                JsonArray many => many.Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : "").ToList(),
                _ => [""],
            };
            if (names.Count == 0 || names.Any(name => !TypeNames.Contains(name)))
                errors.Add($"{Where(path)}: \"type\" is one of {string.Join(", ", TypeNames)} (or a list of them).");
        }
        if (schema.TryGetPropertyValue("enum", out var values) && values is not JsonArray { Count: > 0 })
            errors.Add($"{Where(path)}: \"enum\" is a list of at least one value.");
        if (schema["pattern"] is { } pattern)
        {
            if (pattern is not JsonValue patternValue || !patternValue.TryGetValue<string>(out var regex)) errors.Add($"{Where(path)}: \"pattern\" is a string.");
            else
            {
                try { _ = new Regex(regex, RegexOptions.None, TimeSpan.FromSeconds(1)); }
                catch (ArgumentException error) { errors.Add($"{Where(path)}: the pattern does not compile ({error.Message})."); }
            }
        }
        if (schema.TryGetPropertyValue("properties", out var properties))
        {
            if (properties is not JsonObject map) errors.Add($"{Where(path)}: \"properties\" is an object of schemas.");
            else foreach (var (key, child) in map) Walk(child, path + "/properties/" + key, errors, depth + 1);
        }
        if (schema.TryGetPropertyValue("required", out var required))
        {
            if (required is not JsonArray list || list.Any(item => item is not JsonValue value || !value.TryGetValue<string>(out _)))
                errors.Add($"{Where(path)}: \"required\" is a list of property names.");
            else if (list.Select(item => item!.GetValue<string>()).Distinct(StringComparer.Ordinal).Count() != list.Count)
                errors.Add($"{Where(path)}: \"required\" names a property twice.");
        }
        if (schema.TryGetPropertyValue("items", out var items))
        {
            if (items is JsonArray tuple) for (var index = 0; index < tuple.Count; index++) Walk(tuple[index], $"{path}/items/{index}", errors, depth + 1);
            else Walk(items, path + "/items", errors, depth + 1);
        }
        foreach (var keyword in SchemaKeywords)
            if (schema.TryGetPropertyValue(keyword, out var child)) Walk(child, path + "/" + keyword, errors, depth + 1);
        foreach (var keyword in ListKeywords)
            if (schema.TryGetPropertyValue(keyword, out var child))
            {
                if (child is not JsonArray { Count: > 0 } options) errors.Add($"{Where(path)}: \"{keyword}\" is a list of at least one schema.");
                else for (var index = 0; index < options.Count; index++) Walk(options[index], $"{path}/{keyword}/{index}", errors, depth + 1);
            }
        foreach (var keyword in new[] { "$defs", "definitions" })
            if (schema[keyword] is JsonObject definitions)
                foreach (var (key, child) in definitions) Walk(child, $"{path}/{keyword}/{key}", errors, depth + 1);
        foreach (var keyword in CountKeywords)
            if (schema.TryGetPropertyValue(keyword, out var count) && !(count is JsonValue number && number.TryGetValue<long>(out var whole) && whole >= 0))
                errors.Add($"{Where(path)}: \"{keyword}\" is a whole number of 0 or more.");
        foreach (var keyword in NumberKeywords)
            if (schema.TryGetPropertyValue(keyword, out var bound) && !(bound is JsonValue number && number.GetValueKind() == JsonValueKind.Number))
                errors.Add($"{Where(path)}: \"{keyword}\" is a number.");
    }

    private static string Where(string path) => path.Length == 0 ? "The parameters' schema" : "parameters" + path;

    /// <summary>A tool name: letters, digits, _ and -, up to 64.</summary>
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    public static partial Regex ToolName();

    internal static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
