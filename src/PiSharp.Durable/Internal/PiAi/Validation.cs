// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/ai/src/utils/validation.ts.
//
// Port of pi-ai's validateToolArguments(), which the Harness's tool task calls with the call's arguments (harness/tool.ts `validate`):
//
//   if (toolCall.argumentsError) throw new Error(`Invalid arguments for tool "${toolCall.name}": ${toolCall.argumentsError}`);
//   const result = getValidator(tool.parameters).parse(structuredClone(toolCall.arguments));
//       // getValidator: compile(schema, { formats, coerce: true, unresolvedRefs: "reject" }), one per schema object
//   if (result.ok) return result.value;
//   throw new Error(`Validation failed for tool "${name}":\n  - <path>: <message>...\n\nReceived arguments:\n${JSON.stringify(arguments, null, 2)}`);
//
// PiSharp.Durable references only Contracts and AI (decision 0005 §4), so it validates with pi-schema's port in PiSharp.Contracts.Schema
// (the experimental JsonSchemaValidator), as PiSharp.Agent's ToolArgumentValidation does; the piai.validation.* cases compare the two.
// Chord's JSON values cross into pi-schema as JsonNode trees whose strings are .NET strings, so a lone surrogate survives, and come back the
// same way. Every schema gets the same treatment however it was built (pi-schema's builders, TypeBox, a plain JSON schema), so
// ToolSchemaOrigin no longer changes the result. A $ref that names nothing rejects only the values at its place; a schema that cannot be
// compiled (malformed, an invalid pattern) fails every call with the compile error's message. One validator is compiled per schema object,
// as upstream's WeakMap holds one.
//
// The Js helpers below are the JavaScript string and number conversions the durable tools print with (String.prototype.trim,
// Number.prototype.toString).
#nullable enable
#pragma warning disable PISHARP_SCHEMA001 // pi-schema's port is experimental

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Contracts;
using PiSharp.Contracts.Schema;
using PiSharp.Durable.Chord;
using JsonValue = PiSharp.Durable.Chord.JsonValue;
using NodeValue = System.Text.Json.Nodes.JsonValue;

namespace PiSharp.Durable.Internal.PiAi.Validation;

/// <summary>Outcome of <see cref="ToolArgumentValidation.Validate"/>.</summary>
internal sealed class ToolArgumentValidationResult
{
    internal ToolArgumentValidationResult(JsonValue? value, string? errorMessage)
    {
        Value = value;
        ErrorMessage = errorMessage;
    }

    /// <summary>True when upstream would return the (coerced) arguments instead of throwing.</summary>
    public bool IsValid => ErrorMessage is null;

    /// <summary>The validated and coerced arguments (JSON's view of them: a property coercion left undefined is dropped).</summary>
    public JsonValue? Value { get; }

    /// <summary>JSON.stringify(result) exactly as JavaScript prints it.</summary>
    public string? ArgumentsJson => Value?.ToJson();

    /// <summary>The exact upstream Error.message when validation fails.</summary>
    public string? ErrorMessage { get; }
}

/// <summary>Port of pi-ai's validateToolArguments (packages/ai/src/utils/validation.ts) on pi-schema.</summary>
internal static class ToolArgumentValidation
{
    private static readonly JsonSchemaCompileOptions ArgumentOptions = new()
    {
        Formats = JsonSchemaFormats.Standard,
        Coerce = true,
        UnresolvedRefs = JsonSchemaUnresolvedRefs.Reject,
    };

    private static readonly ConditionalWeakTable<JsonObjectValue, CoercingJsonSchemaValidator> Validators = new();

    /// <summary>
    /// Validates <paramref name="arguments"/> of a call to <paramref name="toolName"/> against <paramref name="parameters"/>; a call whose
    /// complete argument text was malformed carries <paramref name="argumentsError"/> (pi-ai <c>ToolCall.argumentsError</c>) and fails with
    /// it. Since Pi v1.5.0 <paramref name="origin"/> no longer changes the result.
    /// </summary>
    public static ToolArgumentValidationResult Validate(string toolName, JsonObjectValue parameters, JsonValue arguments,
        ToolSchemaOrigin origin = ToolSchemaOrigin.JsonSchema, string? argumentsError = null)
    {
        _ = origin;
        if (argumentsError is not null) return new(null, $"Invalid arguments for tool \"{toolName}\": {argumentsError}");
        return Run(toolName, () => Validators.GetValue(parameters, static schema => Compile(schema)), arguments);
    }

    /// <summary>Same as <see cref="Validate"/> on JSON text, parsed as JSON.parse parses it (lone surrogates, number rounding).</summary>
    public static ToolArgumentValidationResult ValidateJson(string toolName, string parametersJson, string argumentsJson,
        ToolSchemaOrigin origin = ToolSchemaOrigin.JsonSchema)
    {
        var parameters = JsonValue.Parse(parametersJson);
        var arguments = JsonValue.Parse(argumentsJson);
        // A schema that is not an object (true, false) is compiled for the one call.
        return parameters is JsonObjectValue schema ? Validate(toolName, schema, arguments, origin) : Run(toolName, () => Compile(parameters), arguments);
    }

    private static CoercingJsonSchemaValidator Compile(JsonValue schema) =>
        (CoercingJsonSchemaValidator)JsonSchemaValidator.Compile(SchemaValues.ToNode(schema), ArgumentOptions);

    private static ToolArgumentValidationResult Run(string toolName, Func<CoercingJsonSchemaValidator> validator, JsonValue arguments)
    {
        try
        {
            // Converting makes a copy (structuredClone): tools never modify the transcript's arguments.
            var result = validator().Parse(SchemaValues.ToNode(arguments));
            if (result.Ok) return new(SchemaValues.FromNode(result.Value), null);
            var lines = string.Join("\n", result.Errors.Select(error => $"  - {SchemaValues.DottedPath(error.Path)}: {error.Message}"));
            return new(null, $"Validation failed for tool \"{toolName}\":\n{lines}\n\nReceived arguments:\n{SchemaValues.Stringify(arguments, 2)}");
        }
        catch (JsonSchemaException error)
        {
            // compile() throws for a schema it cannot compile, a check that recurses without end throws a RangeError.
            return new(null, error.Message);
        }
    }
}

/// <summary>
/// The check of a tool's <c>structuredOutput</c> against its <c>structuredOutputSchema</c> (harness/tool.ts <c>structuredOutputError</c>):
/// <c>compile(schema, { formats, unresolvedRefs: "reject" })</c>, without coercion.
/// </summary>
internal sealed class StructuredOutputValidator
{
    private static readonly JsonSchemaCompileOptions Options = new()
    {
        Formats = JsonSchemaFormats.Standard,
        UnresolvedRefs = JsonSchemaUnresolvedRefs.Reject,
    };

    private readonly JsonSchemaValidator _validator;

    private StructuredOutputValidator(JsonSchemaValidator validator) => _validator = validator;

    /// <summary>Compiles <paramref name="schema"/>; throws <see cref="JsonSchemaException"/> when pi-schema cannot compile it.</summary>
    public static StructuredOutputValidator Compile(JsonObjectValue schema) => new(JsonSchemaValidator.Compile(SchemaValues.ToNode(schema), Options));

    /// <summary>Null when <paramref name="value"/> is valid; else its first error, at a dotted path (<c>root</c> for the value itself).</summary>
    public (string Path, string Message)? FirstError(JsonValue value)
    {
        var node = SchemaValues.ToNode(value);
        if (_validator.Check(node)) return null;
        var first = _validator.Errors(node).FirstOrDefault();
        var path = first is null ? "" : SchemaValues.DottedPath(first.Path);
        return (path.Length == 0 ? "root" : path, first?.Message ?? "invalid");
    }
}

/// <summary>Chord JSON values on pi-schema's side, and the JavaScript printing both sides share.</summary>
internal static class SchemaValues
{
    /// <summary>The value as a JsonNode tree (JSON null is a null node).</summary>
    public static JsonNode? ToNode(JsonValue value) => value switch
    {
        JsonNullValue => null,
        JsonBooleanValue boolean => NodeValue.Create(boolean.Value),
        JsonNumberValue number => NodeValue.Create(number.Value),
        JsonStringValue text => NodeValue.Create(text.Value),
        JsonArrayValue array => new JsonArray([.. array.Select(ToNode)]),
        JsonObjectValue properties => ToObject(properties),
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static JsonObject ToObject(JsonObjectValue properties)
    {
        var node = new JsonObject();
        foreach (var (key, item) in properties) node[key] = ToNode(item);
        return node;
    }

    /// <summary>A JsonNode tree pi-schema returned, as a Chord value.</summary>
    public static JsonValue FromNode(JsonNode? node) => node switch
    {
        null => JsonValue.Null,
        JsonObject properties => JsonValue.Object(properties.Select(property => KeyValuePair.Create(property.Key, FromNode(property.Value)))),
        JsonArray items => JsonValue.Array(items.Select(FromNode)),
        NodeValue value when value.TryGetValue<string>(out var text) => text,
        NodeValue value when value.TryGetValue<bool>(out var boolean) => boolean,
        NodeValue value when value.TryGetValue<double>(out var number) => number,
        _ => JsonValue.Parse(node.ToJsonString()),
    };

    /// <summary>A pi-schema error path (a JSON Pointer) as the dotted path the model sees: /edits/0/oldText -> edits.0.oldText, "" -> root.</summary>
    public static string DottedPath(string pointer) => pointer.Length == 0
        ? "root"
        : string.Join(".", pointer[1..].Split('/').Select(segment => segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)));

    /// <summary><c>JSON.stringify(value, null, indent)</c>: Chord values hold no undefined, so every member prints.</summary>
    public static string Stringify(JsonValue value, int indent)
    {
        if (indent <= 0) return value.ToJson();
        var builder = new StringBuilder();
        Write(builder, value, new string(' ', indent), "");
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, JsonValue value, string gap, string indent)
    {
        switch (value)
        {
            case JsonArrayValue { Count: > 0 } array:
            {
                var inner = indent + gap;
                builder.Append('[');
                for (var index = 0; index < array.Count; index++)
                {
                    builder.Append(index == 0 ? "\n" : ",\n").Append(inner);
                    Write(builder, array[index], gap, inner);
                }
                builder.Append('\n').Append(indent).Append(']');
                break;
            }
            case JsonObjectValue { Count: > 0 } properties:
            {
                var inner = indent + gap;
                builder.Append('{');
                var first = true;
                foreach (var (key, item) in properties)
                {
                    builder.Append(first ? "\n" : ",\n").Append(inner);
                    first = false;
                    JsonUtf16.Quote(builder, key);
                    builder.Append(": ");
                    Write(builder, item, gap, inner);
                }
                builder.Append('\n').Append(indent).Append('}');
                break;
            }
            default:
                value.Write(builder);
                break;
        }
    }
}

/// <summary>JavaScript's string and number conversions.</summary>
internal static class Js
{
    /// <summary>Number.prototype.toString() / String(number).</summary>
    public static string NumberToString(double value) =>
        double.IsNaN(value) ? "NaN" : double.IsPositiveInfinity(value) ? "Infinity" : double.IsNegativeInfinity(value) ? "-Infinity" : JsonNumber.Text(value);

    private static bool IsJsWhitespace(char c) => c is '\t' or '\n' or '\v' or '\f' or '\r' or ' ' or '\u00A0' or '\u1680'
        or (>= '\u2000' and <= '\u200A') or '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000' or '\uFEFF';

    /// <summary>String.prototype.trim().</summary>
    public static string Trim(string s)
    {
        var start = 0;
        var end = s.Length;
        while (start < end && IsJsWhitespace(s[start])) start++;
        while (end > start && IsJsWhitespace(s[end - 1])) end--;
        return s[start..end];
    }
}
