// PiSharp-only (decision 0010): the bounded JSON Schema subset of typed job and policy inputs. It is deliberately small: a definition that
// uses anything else is refused when the host opens, and an input with an unknown field is refused, so nothing is silently ignored.
using System.Text.RegularExpressions;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Hosting;

/// <summary>Checks schemas of the supported subset and normalizes inputs against them (defaults filled in, values validated).</summary>
public static class InputSchema
{
    /// <summary>The deepest nesting of a schema or an input.</summary>
    public const int MaxDepth = 8;

    /// <summary>The longest string a schema may allow.</summary>
    public const int MaxStringLength = 100_000;

    /// <summary>The longest array a schema may allow.</summary>
    public const int MaxArrayLength = 1000;

    private static readonly Regex SecretRef = new("^secret:[A-Za-z0-9._-]{1,100}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static readonly IReadOnlyDictionary<string, string[]> Keywords = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["object"] = ["properties", "required", "additionalProperties"],
        ["string"] = ["maxLength", "minLength", "enum", "pattern", "format"],
        ["number"] = ["minimum", "maximum"],
        ["integer"] = ["minimum", "maximum"],
        ["boolean"] = [],
        ["array"] = ["items", "maxItems", "minItems"],
    };

    /// <summary>The schema's problems as JSON Pointers into the schema; empty when it is a valid object schema of the subset.</summary>
    public static IReadOnlyList<FieldError> Check(JsonObjectValue schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var errors = new List<FieldError>();
        CheckSchema(schema, "", 0, errors);
        if (errors.Count == 0 && Jobs.String(schema, "type") != "object") errors.Add(new FieldError("/type", "must be object at the root"));
        return errors;
    }

    /// <summary>Validates and normalizes <paramref name="input"/> (null is an empty object).</summary>
    /// <exception cref="HostValidationException">The input does not match; <see cref="HostValidationException.Fields"/> are input
    /// paths.</exception>
    public static JsonObjectValue Normalize(JsonObjectValue schema, JsonObjectValue? input, string what = "Invalid input")
    {
        var errors = new List<FieldError>();
        var normalized = Value(schema, input ?? JsonObjectValue.Empty, "", 0, errors);
        if (errors.Count > 0 || normalized is not JsonObjectValue result) throw HostValidationException.Of(what, errors);
        return result;
    }

    /// <summary>The problems of <paramref name="value"/> against <paramref name="schema"/>; empty when it matches.</summary>
    public static IReadOnlyList<FieldError> Validate(JsonObjectValue schema, JsonValue value)
    {
        var errors = new List<FieldError>();
        Value(schema, value, "", 0, errors);
        return errors;
    }

    private static void CheckSchema(JsonObjectValue schema, string at, int depth, List<FieldError> errors)
    {
        if (depth > MaxDepth) { errors.Add(new FieldError(at, $"nests deeper than {MaxDepth}")); return; }
        var type = Jobs.String(schema, "type");
        if (type is null || !Keywords.TryGetValue(type, out var allowed))
        {
            errors.Add(new FieldError(at + "/type", "must be object, string, number, integer, boolean or array"));
            return;
        }
        foreach (var key in schema.Keys)
            if (key is not ("type" or "description" or "title" or "default") && !allowed.Contains(key))
                errors.Add(new FieldError(at + "/" + key, $"is not supported for {type} in this schema subset"));
        if (schema.TryGetValue("description", out var description) && description is not JsonStringValue { Value.Length: <= 2000 })
            errors.Add(new FieldError(at + "/description", "must be a string of at most 2000 characters"));
        if (schema.TryGetValue("title", out var title) && title is not JsonStringValue { Value.Length: <= 200 })
            errors.Add(new FieldError(at + "/title", "must be a string of at most 200 characters"));
        switch (type)
        {
            case "object":
                var properties = schema.TryGetValue("properties", out var props) ? props as JsonObjectValue : JsonObjectValue.Empty;
                if (properties is null) { errors.Add(new FieldError(at + "/properties", "must be an object")); break; }
                if (properties.Count > 100) errors.Add(new FieldError(at + "/properties", "has more than 100 properties"));
                foreach (var (name, child) in properties)
                {
                    if (name.Length is 0 or > 100) errors.Add(new FieldError(at + "/properties", "property names must have 1 to 100 characters"));
                    if (child is JsonObjectValue nested) CheckSchema(nested, at + "/properties/" + Escape(name), depth + 1, errors);
                    else errors.Add(new FieldError(at + "/properties/" + Escape(name), "must be a schema object"));
                }
                if (schema.TryGetValue("required", out var required))
                {
                    if (required is not JsonArrayValue names || names.Any(item => item is not JsonStringValue text || !properties.ContainsKey(text.Value)))
                        errors.Add(new FieldError(at + "/required", "must list declared properties"));
                }
                if (schema.TryGetValue("additionalProperties", out var extra) && extra is not JsonBooleanValue { Value: false })
                    errors.Add(new FieldError(at + "/additionalProperties", "may only be false"));
                break;
            case "string":
                var max = Whole(schema, "maxLength");
                if (max is null or < 1 or > MaxStringLength) errors.Add(new FieldError(at + "/maxLength", $"is required, 1 to {MaxStringLength}"));
                if (schema.ContainsKey("minLength") && (Whole(schema, "minLength") is not { } min || min < 0 || min > (max ?? 0)))
                    errors.Add(new FieldError(at + "/minLength", "must be 0 to maxLength"));
                if (schema.TryGetValue("enum", out var choices) &&
                    (choices is not JsonArrayValue { Count: >= 1 and <= 100 } list || list.Any(item => item is not JsonStringValue) ||
                     list.OfType<JsonStringValue>().Select(item => item.Value).Distinct(StringComparer.Ordinal).Count() != list.Count))
                    errors.Add(new FieldError(at + "/enum", "must list 1 to 100 distinct strings"));
                if (schema.TryGetValue("pattern", out var pattern))
                {
                    if (pattern is not JsonStringValue { Value.Length: >= 1 and <= 200 } regex) errors.Add(new FieldError(at + "/pattern", "must be 1 to 200 characters"));
                    else
                    {
                        try { _ = new Regex(regex.Value, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
                        catch (ArgumentException) { errors.Add(new FieldError(at + "/pattern", "is not a valid regular expression")); }
                    }
                }
                if (schema.TryGetValue("format", out var format) && format is not JsonStringValue { Value: "uri" or "secret-ref" })
                    errors.Add(new FieldError(at + "/format", "may be uri or secret-ref"));
                break;
            case "number" or "integer":
                foreach (var bound in new[] { "minimum", "maximum" })
                    if (schema.ContainsKey(bound) && Number(schema, bound) is null) errors.Add(new FieldError(at + "/" + bound, "must be a finite number"));
                if (Number(schema, "minimum") is { } low && Number(schema, "maximum") is { } high && low > high)
                    errors.Add(new FieldError(at + "/minimum", "must not exceed maximum"));
                break;
            case "array":
                if (Whole(schema, "maxItems") is null or < 0 or > MaxArrayLength) errors.Add(new FieldError(at + "/maxItems", $"is required, 0 to {MaxArrayLength}"));
                if (schema.ContainsKey("minItems") && (Whole(schema, "minItems") is not { } least || least < 0 || least > (Whole(schema, "maxItems") ?? 0)))
                    errors.Add(new FieldError(at + "/minItems", "must be 0 to maxItems"));
                if (schema.TryGetValue("items", out var item) && item is JsonObjectValue itemSchema) CheckSchema(itemSchema, at + "/items", depth + 1, errors);
                else errors.Add(new FieldError(at + "/items", "is required and must be a schema object"));
                break;
        }
        if (errors.Count == 0 && schema.TryGetValue("default", out var fallback))
        {
            var problems = new List<FieldError>();
            Value(schema.Remove("default"), fallback, "", depth, problems);
            if (problems.Count > 0) errors.Add(new FieldError(at + "/default", "does not match its own schema: " + problems[0].Message));
        }
    }

    private static JsonValue? Value(JsonObjectValue schema, JsonValue value, string at, int depth, List<FieldError> errors)
    {
        if (depth > MaxDepth) { errors.Add(new FieldError(at, $"nests deeper than {MaxDepth}")); return null; }
        switch (Jobs.String(schema, "type"))
        {
            case "object":
                if (value is not JsonObjectValue obj) { errors.Add(new FieldError(at, "must be an object")); return null; }
                var properties = schema.TryGetValue("properties", out var props) && props is JsonObjectValue declared ? declared : JsonObjectValue.Empty;
                var required = Jobs.Items(schema, "required").OfType<JsonStringValue>().Select(item => item.Value).ToHashSet(StringComparer.Ordinal);
                foreach (var key in obj.Keys)
                    if (!properties.ContainsKey(key)) errors.Add(new FieldError(at + "/" + Escape(key), "is not a known field"));
                var result = JsonObjectValue.CreateBuilder();
                foreach (var (name, child) in properties)
                {
                    var childSchema = (JsonObjectValue)child;
                    if (obj.TryGetValue(name, out var given) && given is not JsonNullValue)
                    {
                        if (Value(childSchema, given, at + "/" + Escape(name), depth + 1, errors) is { } normalized) result[name] = normalized;
                    }
                    else if (childSchema.TryGetValue("default", out var fallback)) result[name] = fallback;
                    else if (required.Contains(name)) errors.Add(new FieldError(at + "/" + Escape(name), "is required"));
                }
                return result.ToImmutable();
            case "string":
                if (value is not JsonStringValue text) { errors.Add(new FieldError(at, "must be a string")); return null; }
                if (text.Value.Length > (Whole(schema, "maxLength") ?? MaxStringLength)) errors.Add(new FieldError(at, $"must have at most {Whole(schema, "maxLength")} characters"));
                if (text.Value.Length < (Whole(schema, "minLength") ?? 0)) errors.Add(new FieldError(at, $"must have at least {Whole(schema, "minLength")} characters"));
                if (schema.TryGetValue("enum", out var choices) && choices is JsonArrayValue list && !list.OfType<JsonStringValue>().Any(choice => choice.Value == text.Value))
                    errors.Add(new FieldError(at, "must be one of " + string.Join(", ", list.OfType<JsonStringValue>().Select(choice => choice.Value))));
                if (Jobs.String(schema, "pattern") is { } pattern)
                {
                    try { if (!Regex.IsMatch(text.Value, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) errors.Add(new FieldError(at, "does not match the required pattern")); }
                    catch (RegexMatchTimeoutException) { errors.Add(new FieldError(at, "could not be checked against its pattern in time")); }
                }
                switch (Jobs.String(schema, "format"))
                {
                    case "uri" when !(Uri.TryCreate(text.Value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"):
                        errors.Add(new FieldError(at, "must be an absolute http or https URL")); break;
                    case "secret-ref" when !SecretRef.IsMatch(text.Value):
                        errors.Add(new FieldError(at, "must be a secret reference (secret:<name>), never a secret value")); break;
                }
                return text;
            case "number" or "integer":
                if (value is not JsonNumberValue number || !double.IsFinite(number.Value)) { errors.Add(new FieldError(at, "must be a finite number")); return null; }
                if (Jobs.String(schema, "type") == "integer" && number.Value != Math.Floor(number.Value)) errors.Add(new FieldError(at, "must be a whole number"));
                if (Number(schema, "minimum") is { } low && number.Value < low) errors.Add(new FieldError(at, $"must be at least {Format(low)}"));
                if (Number(schema, "maximum") is { } high && number.Value > high) errors.Add(new FieldError(at, $"must be at most {Format(high)}"));
                return number;
            case "boolean":
                if (value is not JsonBooleanValue) { errors.Add(new FieldError(at, "must be true or false")); return null; }
                return value;
            case "array":
                if (value is not JsonArrayValue array) { errors.Add(new FieldError(at, "must be an array")); return null; }
                if (array.Count > (Whole(schema, "maxItems") ?? MaxArrayLength)) errors.Add(new FieldError(at, $"must have at most {Whole(schema, "maxItems")} items"));
                if (array.Count < (Whole(schema, "minItems") ?? 0)) errors.Add(new FieldError(at, $"must have at least {Whole(schema, "minItems")} items"));
                var itemSchema = (JsonObjectValue)schema["items"];
                var items = new List<JsonValue>();
                for (var index = 0; index < Math.Min(array.Count, MaxArrayLength); index++)
                    if (Value(itemSchema, array[index], at + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), depth + 1, errors) is { } item) items.Add(item);
                return JsonValue.Array(items);
            default:
                errors.Add(new FieldError(at, "has no supported schema type"));
                return null;
        }
    }

    private static long? Whole(JsonObjectValue schema, string key) =>
        schema.TryGetValue(key, out var value) && value is JsonNumberValue number && number.Value == Math.Floor(number.Value) && double.IsFinite(number.Value) ? (long)number.Value : null;

    private static double? Number(JsonObjectValue schema, string key) =>
        schema.TryGetValue(key, out var value) && value is JsonNumberValue number && double.IsFinite(number.Value) ? number.Value : null;

    private static string Format(double value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Escape(string segment) => segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
