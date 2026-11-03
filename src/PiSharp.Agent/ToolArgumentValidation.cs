// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/ai/src/utils/validation.ts (with TypeBox 1.3.27 Compile and its English error messages).
//
// Port of Pi's validateToolArguments() as of v1.5.0, on pi-schema (PiSharp.Contracts.Schema):
//
//   const result = getValidator(tool.parameters).parse(structuredClone(toolCall.arguments));
//       // getValidator: compile(schema, { formats, coerce: true, unresolvedRefs: "reject" }), one per schema object
//   if (result.ok) return result.value;
//   throw new Error(`Validation failed for tool "${name}":\n  - <path>: <message>...\n\nReceived arguments:\n${JSON.stringify(arguments, null, 2)}`);
//
// Every schema gets the same treatment however it was built (pi-schema's builders, TypeBox, a plain JSON schema), so
// ToolSchemaOrigin no longer changes the result. A $ref that names nothing rejects only the values at its place. A schema
// that cannot be compiled (malformed, an invalid pattern) fails every call with the compile error's message. The compiled
// validator is not cached: compiling is deterministic, so a cache would change only the cost. The argumentsError check
// that v1.5.0 runs first belongs to another sync row (ai.malformed-final-tool-arguments) and is not here, which is why the
// citation above stays at the earlier commit.
//
// The TypeBox 1.3.27 engine below (Compile's Check and Errors) no longer validates tool arguments: it stays for
// TypeBoxJsonSchema, which checks the configuration files and the theme schema as v1.5.0 still does with TypeBox. JS
// semantics it reproduces: TypeBox grapheme-based min/maxLength, maxErrors = 8 per error context, the JIT-only fast paths
// of Compile().Check (additionalProperties:false with all-required keys), "Maximum call stack size exceeded" for a
// self-referencing $ref. Not reproduced (documented limits): BigInt values/schemas, Type.Cyclic/Type.Ref with a context,
// $id/$schema-based URI resolution and remote refs, $recursiveRef/$dynamicRef (ignored), the "__proto__" property name,
// URL.canParse-based formats (url, iri, iri-reference: approximated with System.Uri), hostname/idn-hostname/idn-email
// (approximated), JS RegExp features without a .NET equivalent (Unicode script/binary properties, astral ranges inside
// classes), the exact text of JS SyntaxErrors for invalid patterns, and array holes.
#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Contracts;
using PiSharp.Contracts.Schema;

namespace PiSharp.Agent;

/// <summary>Outcome of <see cref="ToolArgumentValidation.Validate(string, JsonNode?, JsonNode?, bool)"/>.</summary>
public sealed class ToolArgumentValidationResult
{
    internal ToolArgumentValidationResult(JsonNode? arguments, string? argumentsJson, string? errorMessage, bool coerced = false)
    {
        Arguments = arguments;
        ArgumentsJson = argumentsJson;
        ErrorMessage = errorMessage;
        Coerced = coerced;
    }

    /// <summary>Whether coercion changed the arguments. When it did not, pi-schema returns the very value it was given, so the model's
    /// arguments stand as they are (JSON text keeps what JSON.stringify cannot print, such as a number too large for a double).</summary>
    internal bool Coerced { get; }

    /// <summary>True when upstream would return the (coerced) arguments instead of throwing.</summary>
    public bool IsValid => ErrorMessage is null;

    /// <summary>The validated and coerced arguments (a fresh node tree).</summary>
    public JsonNode? Arguments { get; }

    /// <summary>JSON.stringify(result) exactly as JS would print it (null when JS would produce undefined).</summary>
    public string? ArgumentsJson { get; }

    /// <summary>The exact upstream Error.message when validation fails.</summary>
    public string? ErrorMessage { get; }
}

/// <summary>Port of Pi's validateToolArguments (packages/ai/src/utils/validation.ts) on pi-schema semantics.</summary>
public static class ToolArgumentValidation
{
    /// <summary>Validates tool-call arguments. Since Pi v1.5.0 <paramref name="typeBoxSchema"/> no longer changes the result.</summary>
    public static ToolArgumentValidationResult Validate(string toolName, JsonNode? parameters, JsonNode? arguments, bool typeBoxSchema = false)
        => Run(toolName, Js.FromNode(parameters), Js.FromNode(arguments));

    /// <summary>Validates tool-call arguments against the parameter schema. Since Pi v1.5.0 <paramref name="origin"/> no longer changes the result.</summary>
    public static ToolArgumentValidationResult Validate(string toolName, JsonNode? parameters, JsonNode? arguments, ToolSchemaOrigin origin)
        => Run(toolName, Js.FromNode(parameters), Js.FromNode(arguments));

    /// <summary>Same as <see cref="Validate(string, JsonNode?, JsonNode?, ToolSchemaOrigin)"/> but takes JSON text parsed with JSON.parse semantics (lone surrogates, number rounding).</summary>
    public static ToolArgumentValidationResult ValidateJson(string toolName, string parametersJson, string argumentsJson, ToolSchemaOrigin origin)
        => Run(toolName, Js.Parse(parametersJson), Js.Parse(argumentsJson));

    /// <summary>JSON.stringify(value, null, indent) with JavaScript semantics (key order, number format, escaping). Returns null for JS undefined.</summary>
    public static string? Stringify(JsonNode? value, int indent = 0) => Js.Stringify(Js.FromNode(value), indent);

    private static ToolArgumentValidationResult Run(string toolName, object? schema, object? original)
    {
        try
        {
            var validator = SchemaValidator.Compile(schema, new SchemaOptions { Formats = SchemaFormats.All, Coerce = true, RejectUnresolvedRefs = true });
            var clone = Js.Clone(original); // structuredClone: tools never modify the transcript's arguments
            var (ok, value, errors) = validator.Parse(clone);
            if (ok) return new ToolArgumentValidationResult(Js.ToNode(value), Js.Stringify(value, 0), null, coerced: !ReferenceEquals(value, clone));

            var lines = string.Join("\n", errors.Select(e => $"  - {FormatValidationPath(e.Path)}: {e.Message}"));
            var received = Js.Stringify(original, 2) ?? "undefined";
            return new ToolArgumentValidationResult(null, null, $"Validation failed for tool \"{toolName}\":\n{lines}\n\nReceived arguments:\n{received}");
        }
        catch (JsonSchemaException error)
        {
            // compile() throws for a schema it cannot compile, a check that recurses without end throws a RangeError.
            return new ToolArgumentValidationResult(null, null, error.Message);
        }
    }

    /// <summary>A pi-schema error path (a JSON Pointer) as the dotted path the model sees: /edits/0/oldText -> edits.0.oldText.</summary>
    private static string FormatValidationPath(string pointer) => pointer.Length == 0
        ? "root"
        : string.Join(".", pointer[1..].Split('/').Select(segment => segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)));
}

internal sealed class ToolValidationFailure(string message) : Exception(message);

// =====================================================================================================================
// TypeBox schema engine (schema/engine/*.mjs): Check (Compile JIT semantics or dynamic Value.Check) and Errors
// =====================================================================================================================

internal sealed class SchemaError
{
    public required string Keyword { get; init; }
    public required string SchemaPath { get; init; }
    public required string InstancePath { get; init; }
    public required string Message { get; init; }
    public List<string>? RequiredProperties { get; init; }
}

internal class CheckContext
{
    private readonly List<(HashSet<double> Indices, HashSet<string> Keys)> _stack = [(new(), new(StringComparer.Ordinal))];

    public bool Push()
    {
        _stack.Add((new(), new(StringComparer.Ordinal)));
        return true;
    }

    public bool Pop()
    {
        _stack.RemoveAt(_stack.Count - 1);
        return true;
    }

    public bool AddIndex(double index)
    {
        GetIndices().Add(index);
        return true;
    }

    public bool AddKey(string key)
    {
        GetKeys().Add(key);
        return true;
    }

    public HashSet<double> GetIndices() => _stack[^1].Indices;
    public HashSet<string> GetKeys() => _stack[^1].Keys;

    public bool Merge(IEnumerable<CheckContext> results)
    {
        foreach (var context in results)
        {
            foreach (var i in context.GetIndices()) GetIndices().Add(i);
            foreach (var k in context.GetKeys()) GetKeys().Add(k);
        }

        return true;
    }
}

internal sealed class ErrorContext : CheckContext
{
    private const int MaxErrors = 8; // Settings.maxErrors
    public List<SchemaError> Errors { get; } = new();
    public bool AtCapacity() => Errors.Count >= MaxErrors;

    public bool AddError(SchemaError error)
    {
        if (!AtCapacity()) Errors.Add(error);
        return false;
    }
}

/// <summary>A compiled validator (typebox/compile Compile(schema)): Check with JIT semantics, Errors with the dynamic engine.</summary>
internal sealed class Validator
{
    private static readonly ConditionalWeakTable<object, Validator> Cache = new();
    private readonly object? _schema;
    private readonly bool _useUnevaluated;

    private Validator(object? schema)
    {
        _schema = schema;
        _useUnevaluated = SchemaEngine.HasUnevaluated(schema);
    }

    /// <summary>Compile(schema). Throws <see cref="ToolValidationFailure"/> when the JS build would throw (invalid regular expression).</summary>
    public static Validator Compile(object? schema)
    {
        if (schema is not null && schema is not string && schema is not double && schema is not bool && Cache.TryGetValue(schema, out var cached)) return cached;
        var invalid = SchemaEngine.FindInvalidPattern(schema);
        if (invalid is not null) throw new ToolValidationFailure($"Invalid regular expression: /{invalid}/u");
        var v = new Validator(schema);
        if (schema is JsObject or JsArray) Cache.AddOrUpdate(schema, v);
        return v;
    }

    /// <summary>getSubSchemaValidator(): undefined (null) for non-object schemas (WeakMap key) or when Compile throws.</summary>
    public static Validator? TryGetSubSchemaValidator(object? schema)
    {
        if (schema is not (JsObject or JsArray)) return null;
        try
        {
            return Compile(schema);
        }
        catch (ToolValidationFailure)
        {
            return null;
        }
    }

    public bool Check(object? value) => new SchemaEngine(_schema, jit: true, _useUnevaluated).Check(value);

    public List<SchemaError> Errors(object? value)
    {
        if (Check(value)) return new List<SchemaError>();
        return new SchemaEngine(_schema, jit: false, useUnevaluated: true).Errors(value);
    }

    /// <summary>Value.Check(schema, value) - the dynamic (non-JIT) engine.</summary>
    public static bool ValueCheck(object? schema, object? value) => new SchemaEngine(schema, jit: false, useUnevaluated: true).Check(value);
}

/// <param name="root">Root schema (refs resolve against it).</param>
/// <param name="jit">Compile().Check semantics (BuildSchema code generation) instead of the dynamic CheckSchema interpreter.</param>
/// <param name="useUnevaluated">BuildContext.UseUnevaluated(): the schema contains unevaluatedProperties/unevaluatedItems somewhere.</param>
internal sealed class SchemaEngine(object? root, bool jit, bool useUnevaluated)
{
    private int _depth;

    /// <summary>engine/_context.mjs HasUnevaluated: any (nested) unevaluatedItems/unevaluatedProperties schema keyword.</summary>
    public static bool HasUnevaluated(object? schema) => schema switch
    {
        JsArray a => a.Any(HasUnevaluated),
        JsObject o => (o.HasOwn("unevaluatedItems") && o.GetOwn("unevaluatedItems") is JsObject or bool) ||
                      (o.HasOwn("unevaluatedProperties") && o.GetOwn("unevaluatedProperties") is JsObject or bool) ||
                      o.Keys.Any(k => HasUnevaluated(o.GetOwn(k))),
        _ => false,
    };

    public bool Check(object? value) => CheckSchema(new CheckContext(), root, value);

    public List<SchemaError> Errors(object? value)
    {
        var context = new ErrorContext();
        ErrorSchema(context, "#", "", root, value);
        return context.Errors;
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Keyword guards (schema/types/*.mjs)
    // -----------------------------------------------------------------------------------------------------------------
    private static bool IsSchemaObject(object? v) => v is JsObject;
    private static bool IsSchemaBoolean(object? v) => v is bool;
    private static bool IsSchema(object? v) => v is JsObject or bool;
    private static object? P(object? s, string k) => Js.Prop(s, k);
    private static bool Has(object? s, string k) => Js.HasOwnProp(s, k);
    private static bool IsNumberKw(object? s, string k) => Has(s, k) && Js.IsNumber(P(s, k));
    private static bool IsSchemaKw(object? s, string k) => Has(s, k) && IsSchema(P(s, k));
    private static bool IsSchemaArrayKw(object? s, string k) => Has(s, k) && P(s, k) is JsArray a && a.All(IsSchema);
    private static bool IsSchemaMapKw(object? s, string k) => Has(s, k) && Js.IsObject(P(s, k)) && Js.Values(P(s, k)).All(IsSchema);

    private static bool IsType(object? s) => Has(s, "type") && (P(s, "type") is string || (P(s, "type") is JsArray a && a.All(x => x is string)));
    private static bool IsRequired(object? s) => Has(s, "required") && P(s, "required") is JsArray a && a.All(x => x is string);
    private static bool IsAdditionalProperties(object? s) => IsSchemaKw(s, "additionalProperties");
    private static bool IsDependencies(object? s) => Has(s, "dependencies") && Js.IsObject(P(s, "dependencies")) &&
        Js.Values(P(s, "dependencies")).All(v => IsSchema(v) || (v is JsArray a && a.All(x => x is string)));
    private static bool IsDependentRequired(object? s) => Has(s, "dependentRequired") && Js.IsObject(P(s, "dependentRequired")) &&
        Js.Values(P(s, "dependentRequired")).All(v => v is JsArray a && a.All(x => x is string));
    private static bool IsDependentSchemas(object? s) => IsSchemaMapKw(s, "dependentSchemas");
    private static bool IsPatternProperties(object? s) => IsSchemaMapKw(s, "patternProperties");
    private static bool IsProperties(object? s) => IsSchemaMapKw(s, "properties");
    private static bool IsPropertyNames(object? s) => Has(s, "propertyNames") && (Js.IsObject(P(s, "propertyNames")) || IsSchema(P(s, "propertyNames")));
    private static bool IsItems(object? s) => Has(s, "items") && (IsSchema(P(s, "items")) || (P(s, "items") is JsArray a && a.All(IsSchema)));
    private static bool IsItemsSized(object? s) => IsItems(s) && P(s, "items") is JsArray;
    private static bool IsPrefixItems(object? s) => IsSchemaArrayKw(s, "prefixItems");
    private static bool IsUniqueItems(object? s) => Has(s, "uniqueItems") && P(s, "uniqueItems") is bool;
    private static bool IsFormat(object? s) => Has(s, "format") && P(s, "format") is string;
    private static bool IsPattern(object? s) => Has(s, "pattern") && P(s, "pattern") is string;
    private static bool IsRef(object? s) => Has(s, "$ref") && P(s, "$ref") is string;
    private static bool IsConst(object? s) => Has(s, "const");
    private static bool IsEnum(object? s) => Has(s, "enum") && P(s, "enum") is JsArray;

    // -----------------------------------------------------------------------------------------------------------------
    // Check
    // -----------------------------------------------------------------------------------------------------------------
    private bool CheckSchemaPushStack(CheckContext context, object? schema, object? value)
        => context.Push() && CheckSchema(context, schema, value) && context.Pop(); // Pop skipped on failure (as upstream)

    private bool CheckSchema(CheckContext context, object? schema, object? value)
    {
        if (++_depth > 2000)
        {
            _depth = 0;
            throw new ToolValidationFailure("Maximum call stack size exceeded");
        }

        try
        {
            if (IsSchemaBoolean(schema)) return (bool)schema!;
            var s = schema;
            return (!IsType(s) || CheckType(s, value)) &&
                   (!(value is JsObject) || CheckObjectKeywords(context, s, (JsObject)value!)) &&
                   (!(value is JsArray) || CheckArrayKeywords(context, s, (JsArray)value!)) &&
                   (!(value is string) || CheckStringKeywords(s, (string)value!)) &&
                   (!Js.IsNumber(value) || CheckNumberKeywords(s, (double)value!)) &&
                   (!IsRef(s) || CheckRef(context, s, value)) &&
                   (!IsConst(s) || CheckConst(s, value)) &&
                   (!IsEnum(s) || CheckEnum(s, value)) &&
                   (!IsSchemaKw(s, "if") || CheckIf(context, s, value)) &&
                   (!IsSchemaKw(s, "not") || CheckNot(context, s, value)) &&
                   (!IsSchemaArrayKw(s, "allOf") || CheckAllOf(context, s, value)) &&
                   (!IsSchemaArrayKw(s, "anyOf") || CheckAnyOf(context, s, value)) &&
                   (!IsSchemaArrayKw(s, "oneOf") || CheckOneOf(context, s, value)) &&
                   (!IsSchemaKw(s, "unevaluatedItems") || !(value is JsArray) || CheckUnevaluatedItems(context, s, (JsArray)value!)) &&
                   (!IsSchemaKw(s, "unevaluatedProperties") || !Js.IsObject(value) || CheckUnevaluatedProperties(context, s, value!));
        }
        finally
        {
            _depth--;
        }
    }

    private static bool CheckTypeName(string type, object? value) => type switch
    {
        "object" => value is JsObject,
        "array" => value is JsArray,
        "boolean" => value is bool,
        "integer" => Js.IsInteger(value),
        "number" => Js.IsNumber(value),
        "null" => value is null,
        "string" => value is string,
        "bigint" => false,
        "constructor" => value is JsFunction, // native Object.prototype methods stringify as "[native code]"
        "function" => value is JsFunction,
        "symbol" => false,
        "undefined" => value is JsUndefined,
        "void" => value is JsUndefined,
        _ => true,
    };

    private static bool CheckType(object? s, object? value)
        => P(s, "type") is JsArray types ? types.Any(t => CheckTypeName((string)t!, value)) : CheckTypeName((string)P(s, "type")!, value);

    private bool CheckObjectKeywords(CheckContext context, object? s, JsObject value)
        => (!IsRequired(s) || CheckRequired(s, value)) &&
           (!IsAdditionalProperties(s) || CheckAdditionalProperties(context, s, value)) &&
           (!IsDependencies(s) || CheckDependencies(context, s, value)) &&
           (!IsDependentRequired(s) || CheckDependentRequired(s, value)) &&
           (!IsDependentSchemas(s) || CheckDependentSchemas(context, s, value)) &&
           (!IsPatternProperties(s) || CheckPatternProperties(context, s, value)) &&
           (!IsProperties(s) || CheckProperties(context, s, value)) &&
           (!IsPropertyNames(s) || CheckPropertyNames(context, s, value)) &&
           (!IsNumberKw(s, "minProperties") || value.Keys.Count >= (double)P(s, "minProperties")!) &&
           (!IsNumberKw(s, "maxProperties") || value.Keys.Count <= (double)P(s, "maxProperties")!);

    private static bool CheckRequired(object? s, JsObject value) => ((JsArray)P(s, "required")!).All(k => Js.HasPropertyKey(value, (string)k!));

    private static IEnumerable<string> RequiredList(object? s) => IsRequired(s) ? ((JsArray)P(s, "required")!).Select(k => (string)k!) : [];

    private bool CheckProperties(CheckContext context, object? s, JsObject value)
    {
        var required = RequiredList(s).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, propSchema) in Js.Entries(P(s, "properties")))
        {
            var isProperty = !Js.HasPropertyKey(value, key) || (CheckSchemaPushStack(context, propSchema, Js.Get(value, key)) && context.AddKey(key));
            var ok = required.Contains(key) ? isProperty : Js.IsUndefined(Js.Get(value, key)) || isProperty;
            if (!ok) return false;
        }

        return true;
    }

    // additionalProperties: GetPropertiesPattern => key is a property key or matches a patternProperties pattern.
    private static bool IsKnownProperty(object? s, string key)
    {
        if (IsPatternProperties(s))
        {
            foreach (var pattern in Js.OwnPropertyNames(P(s, "patternProperties")))
            {
                if (JsRegex.Test(pattern, true, key)) return true;
            }
        }

        if (IsProperties(s))
        {
            foreach (var k in Js.OwnPropertyNames(P(s, "properties")))
            {
                if (string.Equals(k, key, StringComparison.Ordinal)) return true;
            }
        }

        return false;
    }

    private bool CheckAdditionalProperties(CheckContext context, object? s, JsObject value)
    {
        var ap = P(s, "additionalProperties");
        if (jit)
        {
            // IsAdditionalPropertiesIgnored (only outside unevaluated contexts; a true-like schema is always valid anyway)
            if (!useUnevaluated && (ap is true || (ap is JsObject apo && apo.Count == 0))) return true;
            // CanAdditionalPropertiesFast
            if (IsRequired(s) && IsProperties(s) && !IsPatternProperties(s) && ap is false &&
                Js.OwnPropertyNames(P(s, "properties")).Count == ((JsArray)P(s, "required")!).Count)
            {
                return value.Keys.Count == ((JsArray)P(s, "required")!).Count;
            }
        }

        foreach (var key in value.Keys.ToList())
        {
            var ok = IsKnownProperty(s, key) || (CheckSchemaPushStack(context, ap, value.GetOwn(key)) && context.AddKey(key));
            if (!ok) return false;
        }

        return true;
    }

    private bool CheckPatternProperties(CheckContext context, object? s, JsObject value)
    {
        foreach (var (pattern, schema) in Js.Entries(P(s, "patternProperties")))
        {
            foreach (var (key, prop) in Js.Entries(value))
            {
                if (JsRegex.Test(pattern, true, key) && !(CheckSchemaPushStack(context, schema, prop) && context.AddKey(key))) return false;
            }
        }

        return true;
    }

    private bool CheckPropertyNames(CheckContext context, object? s, JsObject value)
    {
        foreach (var key in value.Keys.ToList())
        {
            if (!CheckSchema(context, P(s, "propertyNames"), key)) return false;
        }

        return true;
    }

    private bool CheckDependencies(CheckContext context, object? s, JsObject value)
    {
        var isLength = value.Keys.Count == 0;
        var isEvery = true;
        foreach (var (key, dep) in Js.Entries(P(s, "dependencies")))
        {
            var ok = !Js.HasPropertyKey(value, key) || (dep is JsArray keys
                ? keys.All(k => Js.HasPropertyKey(value, (string)k!))
                : CheckSchema(context, dep, value));
            if (!ok)
            {
                isEvery = false;
                break;
            }
        }

        return isLength || isEvery;
    }

    private static bool CheckDependentRequired(object? s, JsObject value)
    {
        var isLength = value.Keys.Count == 0;
        var isEvery = Js.Entries(P(s, "dependentRequired")).All(e =>
            !Js.HasPropertyKey(value, e.Key) || ((JsArray)e.Value!).All(k => Js.HasPropertyKey(value, (string)k!)));
        return isLength || isEvery;
    }

    private bool CheckDependentSchemas(CheckContext context, object? s, JsObject value)
    {
        var isLength = value.Keys.Count == 0;
        var isEvery = true;
        foreach (var (key, dep) in Js.Entries(P(s, "dependentSchemas")))
        {
            if (!(!Js.HasPropertyKey(value, key) || CheckSchema(context, dep, value)))
            {
                isEvery = false;
                break;
            }
        }

        return isLength || isEvery;
    }

    private bool CheckArrayKeywords(CheckContext context, object? s, JsArray value)
        => (!IsSchemaKw(s, "additionalItems") || CheckAdditionalItems(context, s, value)) &&
           (!IsSchemaKw(s, "contains") || CheckContains(context, s, value)) &&
           (!IsItems(s) || CheckItems(context, s, value)) &&
           (!IsNumberKw(s, "maxContains") || CheckMaxContains(context, s, value)) &&
           (!IsNumberKw(s, "maxItems") || value.Count <= (double)P(s, "maxItems")!) &&
           (!IsNumberKw(s, "minContains") || CheckMinContains(context, s, value)) &&
           (!IsNumberKw(s, "minItems") || value.Count >= (double)P(s, "minItems")!) &&
           (!IsPrefixItems(s) || CheckPrefixItems(context, s, value)) &&
           (!IsUniqueItems(s) || CheckUniqueItems(s, value));

    private bool CheckAdditionalItems(CheckContext context, object? s, JsArray value)
    {
        if (!(IsItems(s) && P(s, "items") is JsArray items)) return true;
        for (var i = 0; i < value.Count; i++)
        {
            if (!(i < items.Count || (CheckSchemaPushStack(context, P(s, "additionalItems"), value[i]) && context.AddIndex(i)))) return false;
        }

        return true;
    }

    private static bool ContainsValid(object? s) => !(IsNumberKw(s, "minContains") && (double)P(s, "minContains")! == 0);

    private bool CheckContains(CheckContext context, object? s, JsArray value)
    {
        if (!ContainsValid(s)) return true;
        if (value.Count == 0) return false;
        var result = false;
        for (var i = 0; i < value.Count; i++)
        {
            if (CheckSchema(context, P(s, "contains"), value[i]) && context.AddIndex(i)) result = true;
        }

        return result;
    }

    private bool CheckMinContains(CheckContext context, object? s, JsArray value)
    {
        if (!IsSchemaKw(s, "contains")) return true;
        var count = 0;
        for (var i = 0; i < value.Count; i++)
        {
            if (CheckSchema(context, P(s, "contains"), value[i]) && context.AddIndex(i)) count++;
        }

        return count >= (double)P(s, "minContains")!;
    }

    private bool CheckMaxContains(CheckContext context, object? s, JsArray value)
    {
        if (!IsSchemaKw(s, "contains")) return true;
        var count = value.Count(item => CheckSchema(context, P(s, "contains"), item));
        return count <= (double)P(s, "maxContains")!;
    }

    private bool CheckItems(CheckContext context, object? s, JsArray value)
    {
        if (IsItemsSized(s))
        {
            var items = (JsArray)P(s, "items")!;
            for (var i = 0; i < items.Count; i++)
            {
                if (!(value.Count <= i || (CheckSchemaPushStack(context, items[i], value[i]) && context.AddIndex(i)))) return false;
            }

            return true;
        }

        var offset = IsPrefixItems(s) ? ((JsArray)P(s, "prefixItems")!).Count : 0;
        for (var i = offset; i < value.Count; i++)
        {
            if (!(CheckSchemaPushStack(context, P(s, "items"), value[i]) && context.AddIndex(i))) return false;
        }

        return true;
    }

    private bool CheckPrefixItems(CheckContext context, object? s, JsArray value)
    {
        if (value.Count == 0) return true;
        var prefix = (JsArray)P(s, "prefixItems")!;
        for (var i = 0; i < prefix.Count; i++)
        {
            if (!(value.Count <= i || (CheckSchemaPushStack(context, prefix[i], value[i]) && context.AddIndex(i)))) return false;
        }

        return true;
    }

    private static bool CheckUniqueItems(object? s, JsArray value)
    {
        if (P(s, "uniqueItems") is false) return true;
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value) set.Add(Hash(item));
        return set.Count == value.Count;
    }

    /// <summary>Canonical structural key standing in for TypeBox's FNV1A-64 Hashing.Hash (object keys sorted, numbers by IEEE bits).</summary>
    private static string Hash(object? value)
    {
        var sb = new StringBuilder();
        void Walk(object? v)
        {
            switch (v)
            {
                case null: sb.Append('N'); break;
                case JsUndefined: sb.Append('U'); break;
                case bool b: sb.Append(b ? "T" : "F"); break;
                case double d: sb.Append('D').Append(BitConverter.DoubleToInt64Bits(d).ToString(CultureInfo.InvariantCulture)).Append(';'); break;
                case string str: sb.Append('S').Append(str.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(str); break;
                case JsFunction f: sb.Append("Fn").Append(f.Name.Length).Append(':').Append(f.Name); break;
                case JsArray a:
                    sb.Append('[');
                    foreach (var item in a) Walk(item);
                    sb.Append(']');
                    break;
                case JsObject o:
                    sb.Append('{');
                    foreach (var k in o.Keys.OrderBy(k => k, StringComparer.Ordinal))
                    {
                        Walk(k);
                        Walk(o.GetOwn(k));
                    }

                    sb.Append('}');
                    break;
            }
        }

        Walk(value);
        return sb.ToString();
    }

    private static bool CheckStringKeywords(object? s, string value)
        => (!IsNumberKw(s, "maxLength") || Graphemes.IsMaxLength(value, (double)P(s, "maxLength")!)) &&
           (!IsNumberKw(s, "minLength") || Graphemes.IsMinLength(value, (double)P(s, "minLength")!)) &&
           (!IsFormat(s) || Formats.Test((string)P(s, "format")!, value)) &&
           (!IsPattern(s) || JsRegex.Test((string)P(s, "pattern")!, true, value));

    private static bool CheckNumberKeywords(object? s, double value)
        => (!IsNumberKw(s, "exclusiveMaximum") || value < (double)P(s, "exclusiveMaximum")!) &&
           (!IsNumberKw(s, "exclusiveMinimum") || value > (double)P(s, "exclusiveMinimum")!) &&
           (!IsNumberKw(s, "maximum") || value <= (double)P(s, "maximum")!) &&
           (!IsNumberKw(s, "minimum") || value >= (double)P(s, "minimum")!) &&
           (!IsNumberKw(s, "multipleOf") || IsMultipleOf(value, (double)P(s, "multipleOf")!));

    /// <summary>Guard.IsMultipleOf.</summary>
    private static bool IsMultipleOf(double dividend, double divisor)
    {
        const double tolerance = 1e-10;
        if (!double.IsFinite(dividend)) return true;
        if (Math.Floor(dividend) == dividend && (1 / divisor) % 1 == 0) return true;
        var mod = dividend % divisor;
        var min = MathMinJs(MathMinJs(Math.Abs(mod), Math.Abs(mod - divisor)), Math.Abs(mod + divisor));
        return min < tolerance;
    }

    private static double MathMinJs(double a, double b) => double.IsNaN(a) || double.IsNaN(b) ? double.NaN : Math.Min(a, b);

    private bool CheckRef(CheckContext context, object? s, object? value)
    {
        var target = ResolveRef((string)P(s, "$ref")!);
        var next = new CheckContext();
        var result = IsSchema(target) && CheckSchema(next, target, value);
        if (result) context.Merge([next]);
        return result;
    }

    private static bool CheckConst(object? s, object? value)
    {
        var c = P(s, "const");
        return Js.IsValueLike(c) ? Js.StrictEquals(value, c) : DeepEqual(value, c);
    }

    private static bool CheckEnum(object? s, object? value)
        => ((JsArray)P(s, "enum")!).Any(option => Js.IsValueLike(option) ? Js.StrictEquals(value, option) : DeepEqual(value, option));

    /// <summary>Guard.IsDeepEqual(left, right).</summary>
    private static bool DeepEqual(object? left, object? right)
    {
        if (left is JsArray la)
        {
            return right is JsArray ra && la.Count == ra.Count && la.Select((v, i) => DeepEqual(v, ra[i])).All(x => x);
        }

        if (Js.IsObject(left))
        {
            if (!Js.IsObject(right)) return false;
            var keys = Js.OwnPropertyNames(left);
            return keys.Count == Js.OwnPropertyNames(right).Count && keys.All(k => DeepEqual(Js.Get(left, k), Js.Get(right, k)));
        }

        return Js.StrictEquals(left, right);
    }

    private bool CheckIf(CheckContext context, object? s, object? value)
    {
        var thenSchema = IsSchemaKw(s, "then") ? P(s, "then") : true;
        var elseSchema = IsSchemaKw(s, "else") ? P(s, "else") : true;
        return CheckSchema(context, P(s, "if"), value) ? CheckSchema(context, thenSchema, value) : CheckSchema(context, elseSchema, value);
    }

    private bool CheckNot(CheckContext context, object? s, object? value)
    {
        var next = new CheckContext();
        var isSchema = !CheckSchema(next, P(s, "not"), value);
        if (jit) return isSchema; // Reducer merges only passing contexts (none)
        return isSchema && context.Merge([next]);
    }

    private bool CheckAllOf(CheckContext context, object? s, object? value)
    {
        var all = (JsArray)P(s, "allOf")!;
        var results = new List<CheckContext>();
        foreach (var sub in all)
        {
            var next = new CheckContext();
            if (CheckSchema(next, sub, value)) results.Add(next);
        }

        return results.Count == all.Count && context.Merge(results);
    }

    private bool CheckAnyOf(CheckContext context, object? s, object? value)
    {
        var results = new List<CheckContext>();
        foreach (var sub in (JsArray)P(s, "anyOf")!)
        {
            var next = new CheckContext();
            if (CheckSchema(next, sub, value)) results.Add(next);
        }

        return results.Count > 0 && context.Merge(results);
    }

    private bool CheckOneOf(CheckContext context, object? s, object? value)
    {
        var results = new List<CheckContext>();
        foreach (var sub in (JsArray)P(s, "oneOf")!)
        {
            var next = new CheckContext();
            if (CheckSchema(next, sub, value)) results.Add(next);
        }

        return results.Count == 1 && context.Merge(results);
    }

    private bool CheckUnevaluatedItems(CheckContext context, object? s, JsArray value)
    {
        var indices = context.GetIndices();
        for (var i = 0; i < value.Count; i++)
        {
            if (!((indices.Contains(i) || CheckSchema(context, P(s, "unevaluatedItems"), value[i])) && context.AddIndex(i))) return false;
        }

        return true;
    }

    private bool CheckUnevaluatedProperties(CheckContext context, object? s, object value)
    {
        var keys = context.GetKeys();
        foreach (var (key, prop) in Js.Entries(value))
        {
            if (!(keys.Contains(key) || (CheckSchema(context, P(s, "unevaluatedProperties"), prop) && context.AddKey(key)))) return false;
        }

        return true;
    }

    // -----------------------------------------------------------------------------------------------------------------
    // $ref resolution (schema/resolve/resolve.mjs, without $id / remote documents)
    // -----------------------------------------------------------------------------------------------------------------
    private object? ResolveRef(string reference)
    {
        var hash = reference.IndexOf('#');
        if (hash < 0) return false; // no fragment: only resolvable through $id / remote context (not modelled)
        var rawFragment = reference[(hash + 1)..];
        if (rawFragment.Length == 0) return root; // href ends with '#': the (lexical) root itself
        string fragment;
        try
        {
            fragment = Uri.UnescapeDataString(rawFragment);
        }
        catch (UriFormatException)
        {
            return false;
        }

        var anchorOnly = hash == 0 && !fragment.StartsWith('/');
        object? Match(JsObject node)
        {
            if (anchorOnly) return P(node, "$anchor") is string a && a == fragment ? node : Js.Undefined;
            if (!fragment.StartsWith('/')) return Js.Undefined;
            return PointerGet(node, fragment);
        }

        object? FromValue(object? node)
        {
            if (node is JsObject o)
            {
                var m = Match(o);
                if (!Js.IsUndefined(m)) return m;
                object? result = Js.Undefined;
                foreach (var k in o.Keys)
                {
                    if (k is "const" or "enum") continue;
                    var r = FromValue(o.GetOwn(k));
                    if (!Js.IsUndefined(r)) result = r;
                }

                return result;
            }

            if (node is JsArray arr)
            {
                object? result = Js.Undefined;
                foreach (var item in arr)
                {
                    var r = FromValue(item);
                    if (!Js.IsUndefined(r)) result = r;
                }

                return result;
            }

            return Js.Undefined;
        }

        var target = FromValue(root);
        return Js.IsUndefined(target) ? false : target;
    }

    private static object? PointerGet(object? value, string pointer)
    {
        var indices = pointer.Split('/').Select(i => i.Replace("~1", "/").Replace("~0", "~")).ToList();
        if (indices.Count > 0 && indices[0].Length == 0) indices.RemoveAt(0);
        var current = value;
        foreach (var index in indices)
        {
            if (!Js.IsObject(current) || index is "__proto__" or "constructor" or "prototype") return Js.Undefined;
            current = Js.Get(current, index);
        }

        return current;
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Errors (dynamic engine, exhaustive evaluation, Settings.maxErrors = 8 per context)
    // -----------------------------------------------------------------------------------------------------------------
    private static string Num(object? v) => v is double d ? Js.NumberToString(d) : Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";

    private static SchemaError E(string keyword, string schemaPath, string instancePath, string message, List<string>? required = null)
        => new() { Keyword = keyword, SchemaPath = schemaPath, InstancePath = instancePath, Message = message, RequiredProperties = required };

    private bool ErrorSchemaPushStack(ErrorContext context, string schemaPath, string instancePath, object? schema, object? value)
        => context.Push() && ErrorSchema(context, schemaPath, instancePath, schema, value) && context.Pop();

    private bool ErrorSchema(ErrorContext context, string schemaPath, string instancePath, object? schema, object? value)
    {
        if (context.AtCapacity()) return false;
        if (++_depth > 2000)
        {
            _depth = 0;
            throw new ToolValidationFailure("Maximum call stack size exceeded");
        }

        try
        {
            if (IsSchemaBoolean(schema))
            {
                return (bool)schema! || context.AddError(E("boolean", schemaPath, instancePath, "schema is false"));
            }

            var s = schema;
            var r = true;
            r &= !IsType(s) || ErrorType(context, schemaPath, instancePath, s, value);
            if (value is JsObject o)
            {
                var g = true;
                g &= !IsRequired(s) || ErrorRequired(context, schemaPath, instancePath, s, o);
                g &= !IsAdditionalProperties(s) || ErrorAdditionalProperties(context, schemaPath, instancePath, s, o);
                g &= !IsDependencies(s) || ErrorDependencies(context, schemaPath, instancePath, s, o);
                g &= !IsDependentRequired(s) || ErrorDependentRequired(context, schemaPath, instancePath, s, o);
                g &= !IsDependentSchemas(s) || ErrorDependentSchemas(context, schemaPath, instancePath, s, o);
                g &= !IsPatternProperties(s) || ErrorPatternProperties(context, schemaPath, instancePath, s, o);
                g &= !IsProperties(s) || ErrorProperties(context, schemaPath, instancePath, s, o);
                g &= !IsPropertyNames(s) || ErrorPropertyNames(context, schemaPath, instancePath, s, o);
                g &= !IsNumberKw(s, "minProperties") || o.Keys.Count >= (double)P(s, "minProperties")! ||
                     context.AddError(E("minProperties", schemaPath, instancePath, $"must not have fewer than {Num(P(s, "minProperties"))} properties"));
                g &= !IsNumberKw(s, "maxProperties") || o.Keys.Count <= (double)P(s, "maxProperties")! ||
                     context.AddError(E("maxProperties", schemaPath, instancePath, $"must not have more than {Num(P(s, "maxProperties"))} properties"));
                r &= g;
            }

            if (value is JsArray a)
            {
                var g = true;
                g &= !IsSchemaKw(s, "additionalItems") || ErrorAdditionalItems(context, schemaPath, instancePath, s, a);
                g &= !IsSchemaKw(s, "contains") || CheckContains(context, s, a) ||
                     context.AddError(E("contains", schemaPath, instancePath, "must contain at least 1 valid item"));
                g &= !IsItems(s) || ErrorItems(context, schemaPath, instancePath, s, a);
                g &= !IsNumberKw(s, "maxContains") || CheckMaxContains(context, s, a) ||
                     context.AddError(E("contains", schemaPath, instancePath, "must contain at least 1 valid item"));
                g &= !IsNumberKw(s, "maxItems") || a.Count <= (double)P(s, "maxItems")! ||
                     context.AddError(E("maxItems", schemaPath, instancePath, $"must not have more than {Num(P(s, "maxItems"))} items"));
                g &= !IsNumberKw(s, "minContains") || CheckMinContains(context, s, a) ||
                     context.AddError(E("contains", schemaPath, instancePath, "must contain at least 1 valid item"));
                g &= !IsNumberKw(s, "minItems") || a.Count >= (double)P(s, "minItems")! ||
                     context.AddError(E("minItems", schemaPath, instancePath, $"must not have fewer than {Num(P(s, "minItems"))} items"));
                g &= !IsPrefixItems(s) || ErrorPrefixItems(context, schemaPath, instancePath, s, a);
                g &= !IsUniqueItems(s) || CheckUniqueItems(s, a) ||
                     context.AddError(E("uniqueItems", schemaPath, instancePath, "must not have duplicate items"));
                r &= g;
            }

            if (value is string str)
            {
                var g = true;
                g &= !IsNumberKw(s, "maxLength") || Graphemes.IsMaxLength(str, (double)P(s, "maxLength")!) ||
                     context.AddError(E("maxLength", schemaPath, instancePath, $"must not have more than {Num(P(s, "maxLength"))} characters"));
                g &= !IsNumberKw(s, "minLength") || Graphemes.IsMinLength(str, (double)P(s, "minLength")!) ||
                     context.AddError(E("minLength", schemaPath, instancePath, $"must not have fewer than {Num(P(s, "minLength"))} characters"));
                g &= !IsFormat(s) || Formats.Test((string)P(s, "format")!, str) ||
                     context.AddError(E("format", schemaPath, instancePath, $"must match format \"{(string)P(s, "format")!}\""));
                g &= !IsPattern(s) || JsRegex.Test((string)P(s, "pattern")!, true, str) ||
                     context.AddError(E("pattern", schemaPath, instancePath, $"must match pattern \"{(string)P(s, "pattern")!}\""));
                r &= g;
            }

            if (Js.IsNumber(value))
            {
                var d = (double)value!;
                var g = true;
                g &= !IsNumberKw(s, "exclusiveMaximum") || d < (double)P(s, "exclusiveMaximum")! ||
                     context.AddError(E("exclusiveMaximum", schemaPath, instancePath, $"must be < {Num(P(s, "exclusiveMaximum"))}"));
                g &= !IsNumberKw(s, "exclusiveMinimum") || d > (double)P(s, "exclusiveMinimum")! ||
                     context.AddError(E("exclusiveMinimum", schemaPath, instancePath, $"must be > {Num(P(s, "exclusiveMinimum"))}"));
                g &= !IsNumberKw(s, "maximum") || d <= (double)P(s, "maximum")! ||
                     context.AddError(E("maximum", schemaPath, instancePath, $"must be <= {Num(P(s, "maximum"))}"));
                g &= !IsNumberKw(s, "minimum") || d >= (double)P(s, "minimum")! ||
                     context.AddError(E("minimum", schemaPath, instancePath, $"must be >= {Num(P(s, "minimum"))}"));
                g &= !IsNumberKw(s, "multipleOf") || IsMultipleOf(d, (double)P(s, "multipleOf")!) ||
                     context.AddError(E("multipleOf", schemaPath, instancePath, $"must be multiple of {Num(P(s, "multipleOf"))}"));
                r &= g;
            }

            r &= !IsRef(s) || ErrorRef(context, instancePath, s, value);
            r &= !IsConst(s) || CheckConst(s, value) || context.AddError(E("const", schemaPath, instancePath, "must be equal to constant"));
            r &= !IsEnum(s) || CheckEnum(s, value) || context.AddError(E("enum", schemaPath, instancePath, "must be equal to one of the allowed values"));
            r &= !IsSchemaKw(s, "if") || ErrorIf(context, schemaPath, instancePath, s, value);
            r &= !IsSchemaKw(s, "not") || CheckNot(context, s, value) || context.AddError(E("not", schemaPath, instancePath, "must not be valid"));
            r &= !IsSchemaArrayKw(s, "allOf") || ErrorAllOf(context, schemaPath, instancePath, s, value);
            r &= !IsSchemaArrayKw(s, "anyOf") || ErrorAnyOf(context, schemaPath, instancePath, s, value);
            r &= !IsSchemaArrayKw(s, "oneOf") || ErrorOneOf(context, schemaPath, instancePath, s, value);
            r &= !IsSchemaKw(s, "unevaluatedItems") || !(value is JsArray) || ErrorUnevaluatedItems(context, schemaPath, instancePath, s, (JsArray)value!);
            r &= !IsSchemaKw(s, "unevaluatedProperties") || !Js.IsObject(value) || ErrorUnevaluatedProperties(context, schemaPath, instancePath, s, value!);
            return r;
        }
        finally
        {
            _depth--;
        }
    }

    private static bool ErrorType(ErrorContext context, string schemaPath, string instancePath, object? s, object? value)
    {
        if (CheckType(s, value)) return true;
        var type = P(s, "type");
        var message = type is string t ? $"must be {t}" : $"must be either {string.Join(" or ", ((JsArray)type!).Select(x => (string)x!))}";
        return context.AddError(E("type", schemaPath, instancePath, message));
    }

    private static bool ErrorRequired(ErrorContext context, string schemaPath, string instancePath, object? s, JsObject value)
    {
        var missing = new List<string>();
        foreach (var k in (JsArray)P(s, "required")!)
        {
            if (!Js.HasPropertyKey(value, (string)k!)) missing.Add((string)k!);
        }

        return missing.Count == 0 || context.AddError(E("required", schemaPath, instancePath, $"must have required properties {string.Join(", ", missing)}", missing));
    }

    private bool ErrorAdditionalProperties(ErrorContext context, string schemaPath, string instancePath, object? s, JsObject value)
    {
        var ap = P(s, "additionalProperties");
        var all = true;
        foreach (var key in value.Keys.ToList())
        {
            var ok = IsKnownProperty(s, key) ||
                     (ErrorSchemaPushStack(context, $"{schemaPath}/additionalProperties", $"{instancePath}/{key}", ap, value.GetOwn(key)) && context.AddKey(key));
            if (!ok) all = false;
        }

        return all || context.AddError(E("additionalProperties", schemaPath, instancePath, "must not have additional properties"));
    }

    private bool ErrorDependencies(ErrorContext context, string schemaPath, string instancePath, object? s, JsObject value)
    {
        var isLength = value.Keys.Count == 0;
        var isEvery = true;
        foreach (var (key, dep) in Js.Entries(P(s, "dependencies")))
        {
            bool ok;
            if (!Js.HasPropertyKey(value, key)) ok = true;
            else if (dep is JsArray keys)
            {
                ok = true;
                foreach (var dk in keys)
                {
                    if (Js.HasPropertyKey(value, (string)dk!)) continue;
                    context.AddError(E("dependencies", schemaPath, instancePath,
                        $"must have properties {string.Join(", ", keys.Select(x => (string)x!))} when property {key} is present"));
                    ok = false;
                    break; // Array.prototype.every stops at the first failure
                }
            }
            else ok = ErrorSchema(context, $"{schemaPath}/dependencies/{key}", instancePath, dep, value);

            if (!ok) isEvery = false;
        }

        return isLength || isEvery;
    }

    private static bool ErrorDependentRequired(ErrorContext context, string schemaPath, string instancePath, object? s, JsObject value)
    {
        var isLength = value.Keys.Count == 0;
        var isEvery = true;
        foreach (var (key, deps) in Js.Entries(P(s, "dependentRequired")))
        {
            if (!Js.HasPropertyKey(value, key)) continue;
            var keys = (JsArray)deps!;
            var entryOk = true;
            foreach (var dk in keys)
            {
                if (Js.HasPropertyKey(value, (string)dk!)) continue;
                context.AddError(E("dependentRequired", schemaPath, instancePath,
                    $"must have properties {string.Join(", ", keys.Select(x => (string)x!))} when property {key} is present"));
                entryOk = false;
            }

            if (!entryOk) isEvery = false;
        }

        return isLength || isEvery;
    }

    private bool ErrorDependentSchemas(ErrorContext context, string schemaPath, string instancePath, object? s, JsObject value)
    {
        var isLength = value.Keys.Count == 0;
        var isEvery = true;
        foreach (var (key, dep) in Js.Entries(P(s, "dependentSchemas")))
        {
            if (!(!Js.HasPropertyKey(value, key) || ErrorSchema(context, $"{schemaPath}/dependentSchemas/{key}", instancePath, dep, value))) isEvery = false;
        }

        return isLength || isEvery;
    }

    private bool ErrorPatternProperties(ErrorContext context, string schemaPath, string instancePath, object? s, JsObject value)
    {
        var all = true;
        foreach (var (pattern, schema) in Js.Entries(P(s, "patternProperties")))
        {
            foreach (var (key, prop) in Js.Entries(value))
            {
                var ok = !JsRegex.Test(pattern, true, key) ||
                         (ErrorSchemaPushStack(context, $"{schemaPath}/patternProperties/{pattern}", $"{instancePath}/{key}", schema, prop) && context.AddKey(key));
                if (!ok) all = false;
            }
        }

        return all;
    }

    private bool ErrorProperties(ErrorContext context, string schemaPath, string instancePath, object? s, JsObject value)
    {
        var required = RequiredList(s).ToHashSet(StringComparer.Ordinal);
        var all = true;
        foreach (var (key, propSchema) in Js.Entries(P(s, "properties")))
        {
            bool IsProperty() => !Js.HasPropertyKey(value, key) ||
                                 (ErrorSchemaPushStack(context, $"{schemaPath}/properties/{key}", $"{instancePath}/{key}", propSchema, Js.Get(value, key)) && context.AddKey(key));
            var ok = required.Contains(key) ? IsProperty() : Js.IsUndefined(Js.Get(value, key)) || IsProperty();
            if (!ok) all = false;
        }

        return all;
    }

    private bool ErrorPropertyNames(ErrorContext context, string schemaPath, string instancePath, object? s, JsObject value)
    {
        var invalid = new List<string>();
        foreach (var key in value.Keys.ToList())
        {
            if (!ErrorSchema(context, $"{schemaPath}/propertyNames", $"{instancePath}/{key}", P(s, "propertyNames"), key)) invalid.Add(key);
        }

        return invalid.Count == 0 || context.AddError(E("propertyNames", schemaPath, instancePath, $"property names {string.Join(", ", invalid)} are invalid"));
    }

    private bool ErrorAdditionalItems(ErrorContext context, string schemaPath, string instancePath, object? s, JsArray value)
    {
        if (!(IsItems(s) && P(s, "items") is JsArray items)) return true;
        for (var i = 0; i < value.Count; i++)
        {
            if (!(i < items.Count || (ErrorSchemaPushStack(context, $"{schemaPath}/additionalItems", $"{instancePath}/{i}", P(s, "additionalItems"), value[i]) && context.AddIndex(i))))
            {
                return false; // G.Every short-circuits
            }
        }

        return true;
    }

    private bool ErrorItems(ErrorContext context, string schemaPath, string instancePath, object? s, JsArray value)
    {
        var all = true;
        if (IsItemsSized(s))
        {
            var items = (JsArray)P(s, "items")!;
            for (var i = 0; i < items.Count; i++)
            {
                var ok = value.Count <= i || (ErrorSchemaPushStack(context, $"{schemaPath}/items/{i}", $"{instancePath}/{i}", items[i], value[i]) && context.AddIndex(i));
                if (!ok) all = false;
            }

            return all;
        }

        var offset = IsPrefixItems(s) ? ((JsArray)P(s, "prefixItems")!).Count : 0;
        for (var i = offset; i < value.Count; i++)
        {
            var ok = ErrorSchemaPushStack(context, $"{schemaPath}/items", $"{instancePath}/{i}", P(s, "items"), value[i]) && context.AddIndex(i);
            if (!ok) all = false;
        }

        return all;
    }

    private bool ErrorPrefixItems(ErrorContext context, string schemaPath, string instancePath, object? s, JsArray value)
    {
        if (value.Count == 0) return true;
        var prefix = (JsArray)P(s, "prefixItems")!;
        var all = true;
        for (var i = 0; i < prefix.Count; i++)
        {
            var ok = value.Count <= i || (ErrorSchemaPushStack(context, $"{schemaPath}/prefixItems/{i}", $"{instancePath}/{i}", prefix[i], value[i]) && context.AddIndex(i));
            if (!ok) all = false;
        }

        return all;
    }

    private bool ErrorRef(ErrorContext context, string instancePath, object? s, object? value)
    {
        var target = ResolveRef((string)P(s, "$ref")!);
        var next = new ErrorContext();
        var result = IsSchema(target) && ErrorSchema(next, "#", instancePath, target, value);
        if (result) context.Merge([next]);
        if (!result) foreach (var e in next.Errors) context.AddError(e);
        return result;
    }

    private bool ErrorIf(ErrorContext context, string schemaPath, string instancePath, object? s, object? value)
    {
        var thenSchema = IsSchemaKw(s, "then") ? P(s, "then") : true;
        var elseSchema = IsSchemaKw(s, "else") ? P(s, "else") : true;
        var trueContext = new ErrorContext();
        var isIf = ErrorSchema(trueContext, $"{schemaPath}/if", instancePath, P(s, "if"), value)
            ? ErrorSchema(trueContext, $"{schemaPath}/then", instancePath, thenSchema, value) ||
              context.AddError(E("if", schemaPath, instancePath, "must match \"then\" schema"))
            : ErrorSchema(context, $"{schemaPath}/else", instancePath, elseSchema, value) ||
              context.AddError(E("if", schemaPath, instancePath, "must match \"else\" schema"));
        if (isIf) context.Merge([trueContext]);
        return isIf;
    }

    private bool ErrorAllOf(ErrorContext context, string schemaPath, string instancePath, object? s, object? value)
    {
        var all = (JsArray)P(s, "allOf")!;
        var failed = new List<ErrorContext>();
        var results = new List<CheckContext>();
        for (var i = 0; i < all.Count; i++)
        {
            var next = new ErrorContext();
            if (ErrorSchema(next, $"{schemaPath}/allOf/{i}", instancePath, all[i], value)) results.Add(next);
            else failed.Add(next);
        }

        var isAllOf = results.Count == all.Count && context.Merge(results);
        if (!isAllOf) foreach (var f in failed) foreach (var e in f.Errors) context.AddError(e);
        return isAllOf;
    }

    private bool ErrorAnyOf(ErrorContext context, string schemaPath, string instancePath, object? s, object? value)
    {
        var any = (JsArray)P(s, "anyOf")!;
        var failed = new List<ErrorContext>();
        var results = new List<CheckContext>();
        for (var i = 0; i < any.Count; i++)
        {
            var next = new ErrorContext();
            if (ErrorSchema(next, $"{schemaPath}/anyOf/{i}", instancePath, any[i], value)) results.Add(next);
            else failed.Add(next);
        }

        var isAnyOf = results.Count > 0 && context.Merge(results);
        if (!isAnyOf) foreach (var f in failed) foreach (var e in f.Errors) context.AddError(e);
        return isAnyOf || context.AddError(E("anyOf", schemaPath, instancePath, "must match a schema in anyOf"));
    }

    private bool ErrorOneOf(ErrorContext context, string schemaPath, string instancePath, object? s, object? value)
    {
        var one = (JsArray)P(s, "oneOf")!;
        var failed = new List<ErrorContext>();
        var passed = new List<CheckContext>();
        for (var i = 0; i < one.Count; i++)
        {
            var next = new ErrorContext();
            if (ErrorSchema(next, $"{schemaPath}/oneOf/{i}", instancePath, one[i], value)) passed.Add(next);
            else failed.Add(next);
        }

        var isOneOf = passed.Count == 1 && context.Merge(passed);
        if (!isOneOf && passed.Count == 0) foreach (var f in failed) foreach (var e in f.Errors) context.AddError(e);
        return isOneOf || context.AddError(E("oneOf", schemaPath, instancePath, "must match exactly one schema in oneOf"));
    }

    private bool ErrorUnevaluatedItems(ErrorContext context, string schemaPath, string instancePath, object? s, JsArray value)
    {
        var indices = context.GetIndices();
        var all = true;
        for (var i = 0; i < value.Count; i++)
        {
            var next = new ErrorContext();
            var ok = (indices.Contains(i) || ErrorSchema(next, schemaPath, instancePath, P(s, "unevaluatedItems"), value[i])) && context.AddIndex(i);
            if (!ok) all = false;
        }

        return all || context.AddError(E("unevaluatedItems", schemaPath, instancePath, "must not have unevaluated items"));
    }

    private bool ErrorUnevaluatedProperties(ErrorContext context, string schemaPath, string instancePath, object? s, object value)
    {
        var keys = context.GetKeys();
        var all = true;
        foreach (var (key, prop) in Js.Entries(value))
        {
            var next = new ErrorContext();
            var ok = keys.Contains(key) || (ErrorSchema(next, schemaPath, instancePath, P(s, "unevaluatedProperties"), prop) && context.AddKey(key));
            if (!ok) all = false;
        }

        return all || context.AddError(E("unevaluatedProperties", schemaPath, instancePath, "must not have unevaluated properties"));
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Compile-time regex validity (the JS build constructs every RegExp eagerly and throws on invalid patterns)
    // -----------------------------------------------------------------------------------------------------------------
    public static string? FindInvalidPattern(object? schema)
    {
        string? found = null;
        void Walk(object? node)
        {
            if (found is not null) return;
            if (node is JsArray arr)
            {
                foreach (var item in arr) Walk(item);
                return;
            }

            if (node is not JsObject o) return;
            if (o.GetOwn("pattern") is string p && !JsRegex.TryCreate(p, true, out _))
            {
                found = p;
                return;
            }

            if (o.GetOwn("patternProperties") is JsObject pp)
            {
                foreach (var k in pp.Keys)
                {
                    if (!JsRegex.TryCreate(k, true, out _))
                    {
                        found = k;
                        return;
                    }
                }
            }

            foreach (var k in o.Keys)
            {
                if (k is "const" or "enum" or "default" or "examples") continue;
                Walk(o.GetOwn(k));
            }
        }

        Walk(schema);
        return found;
    }
}

// =====================================================================================================================
// guard/string.mjs grapheme counting
// =====================================================================================================================

internal static class Graphemes
{
    private static bool IsBetween(int v, int min, int max) => v >= min && v <= max;
    private static bool IsZeroWidthJoiner(int v) => v == 0x200D;
    private static bool IsHighSurrogate(int v) => IsBetween(v, 0xD800, 0xDBFF);
    private static bool IsRegionalIndicator(int v) => IsBetween(v, 0x1F1E6, 0x1F1FF);
    private static bool IsVariationSelector(int v) => IsBetween(v, 0xFE00, 0xFE0F);

    private static bool IsCombiningMark(int v) => IsBetween(v, 0x0300, 0x036F) || IsBetween(v, 0x1AB0, 0x1AFF) ||
                                                  IsBetween(v, 0x1DC0, 0x1DFF) || IsBetween(v, 0xFE20, 0xFE2F);

    private static int CodePointLength(int v) => v > 0xFFFF ? 2 : 1;

    private static int ConsumeModifiers(string value, int index)
    {
        while (index < value.Length)
        {
            var point = Js.CodePointAt(value, index);
            if (IsCombiningMark(point) || IsVariationSelector(point)) index += CodePointLength(point);
            else break;
        }

        return index;
    }

    private static int NextGraphemeClusterIndex(string value, int clusterStart)
    {
        var startCp = Js.CodePointAt(value, clusterStart);
        var clusterEnd = clusterStart + CodePointLength(startCp);
        clusterEnd = ConsumeModifiers(value, clusterEnd);
        while (clusterEnd < value.Length - 1 && IsZeroWidthJoiner(Js.CodePointAt(value, clusterEnd)))
        {
            var nextCp = Js.CodePointAt(value, clusterEnd + 1);
            clusterEnd += 1 + CodePointLength(nextCp);
            clusterEnd = ConsumeModifiers(value, clusterEnd);
        }

        if (IsRegionalIndicator(startCp) && clusterEnd < value.Length && IsRegionalIndicator(Js.CodePointAt(value, clusterEnd)))
        {
            clusterEnd += CodePointLength(Js.CodePointAt(value, clusterEnd));
        }

        return clusterEnd;
    }

    private static bool IsGraphemeCodePoint(int value) => value >= 0x0300 &&
                                                          (IsHighSurrogate(value) || IsCombiningMark(value) || IsVariationSelector(value) || IsZeroWidthJoiner(value));

    private static int CharCodeAt(string s, int i) => i >= 0 && i < s.Length ? s[i] : -1;

    private static bool IsMinLengthSegmented(string value, double minLength)
    {
        var count = 0;
        var index = 0;
        while (index < value.Length)
        {
            index = NextGraphemeClusterIndex(value, index);
            if (++count >= minLength) return true;
        }

        return false;
    }

    private static bool IsMaxLengthSegmented(string value, double maxLength)
    {
        var count = 0;
        var index = 0;
        while (index < value.Length)
        {
            index = NextGraphemeClusterIndex(value, index);
            if (++count > maxLength) return false;
        }

        return true;
    }

    public static bool IsMinLength(string value, double minLength)
    {
        if (minLength == 0) return true;
        if (value.Length < minLength) return false;
        var index = 0;
        while (true)
        {
            if (IsGraphemeCodePoint(CharCodeAt(value, index))) return IsMinLengthSegmented(value, minLength);
            if (++index >= minLength) return true;
        }
    }

    public static bool IsMaxLength(string value, double maxLength)
    {
        if (value.Length <= maxLength) return true;
        var index = 0;
        while (true)
        {
            if (IsGraphemeCodePoint(CharCodeAt(value, index))) return IsMaxLengthSegmented(value, maxLength);
            if (++index > maxLength) return false;
        }
    }
}

// =====================================================================================================================
// format/*.mjs
// =====================================================================================================================

internal static class Formats
{
    private const RegexOptions Ci = RegexOptions.CultureInvariant;
    private const RegexOptions CiI = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
    private static readonly int[] Days = [0, 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
    private static readonly Regex Date = new(@"^([0-9]{4})-([0-9]{2})-([0-9]{2})\z", Ci);
    private static readonly Regex Time = new(@"^([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.[0-9]+)?(?:([Zz])|([+-])([0-9]{2}):([0-9]{2}))?\z", Ci);
    private static readonly Regex Duration = new(@"^P(([0-9]+Y([0-9]+M([0-9]+D)?)?|[0-9]+M([0-9]+D)?|[0-9]+D)(T([0-9]+H([0-9]+M([0-9]+S)?)?|[0-9]+M([0-9]+S)?|[0-9]+S))?|T([0-9]+H([0-9]+M([0-9]+S)?)?|[0-9]+M([0-9]+S)?|[0-9]+S)|[0-9]+W)\z", Ci);
    private static readonly Regex Email = new(@"^(?:[a-z0-9!#$%&'*+/=?^_`{|}~-]+(?:\.[a-z0-9!#$%&'*+/=?^_`{|}~-]+)*|""(?:[^""\\]|\\[\x20-\x7e])*"")@(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)*|\[(?:IPv6:[a-f0-9:]+|(?:25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])(?:\.(?:25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])){3})\])\z", CiI);
    private static readonly Regex IPv4 = new(@"^(?:(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\.){3}(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\z", Ci);
    private const string V4 = @"(?:(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\.){3}(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])";
    private static readonly Regex IPv6 = new(@"^(?:(?:(?:[0-9a-f]{1,4}:){6}|::(?:[0-9a-f]{1,4}:){5}|(?:[0-9a-f]{1,4})?::(?:[0-9a-f]{1,4}:){4}|(?:(?:[0-9a-f]{1,4}:)?[0-9a-f]{1,4})?::(?:[0-9a-f]{1,4}:){3}|(?:(?:[0-9a-f]{1,4}:){0,2}[0-9a-f]{1,4})?::(?:[0-9a-f]{1,4}:){2}|(?:(?:[0-9a-f]{1,4}:){0,3}[0-9a-f]{1,4})?::[0-9a-f]{1,4}:|(?:(?:[0-9a-f]{1,4}:){0,4}[0-9a-f]{1,4})?::)(?:[0-9a-f]{1,4}:[0-9a-f]{1,4}|" + V4 + @")|(?:(?:[0-9a-f]{1,4}:){0,5}[0-9a-f]{1,4})?::[0-9a-f]{1,4}|(?:(?:[0-9a-f]{1,4}:){0,6}[0-9a-f]{1,4})?::)\z", CiI);
    private static readonly Regex Uuid = new(@"^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}\z", CiI);
    private static readonly Regex JsonPointer = new(@"^(?:/(?:[^~/]|~0|~1)*)*\z", Ci);
    private static readonly Regex JsonPointerUriFragment = new(@"^#(?:/(?:[a-z0-9_\-.!$&'()*+,;:=@]|%[0-9a-f]{2}|~0|~1)*)*\z", CiI);
    private static readonly Regex RelativeJsonPointer = new(@"^(?:0|[1-9][0-9]*)(?:#|(?:/(?:[^~/]|~0|~1)*)*)\z", Ci);
    private static readonly Regex UriTemplate = new(@"^(?:(?:[^\x00-\x20""<>%\\^`{|}\x7f]|%[0-9a-f]{2})|\{[+#./;?&=,!@|]?(?:[a-z0-9_]|%[0-9a-f]{2})+(?:\.(?:[a-z0-9_]|%[0-9a-f]{2})+)*(?::[1-9][0-9]{0,3}|\*)?(?:,(?:[a-z0-9_]|%[0-9a-f]{2})+(?:\.(?:[a-z0-9_]|%[0-9a-f]{2})+)*(?::[1-9][0-9]{0,3}|\*)?)*\})*\z", CiI);

    private const string H16 = "[0-9a-f]{1,4}";
    private static readonly string Ip6Literal =
        $@"(?:(?:{H16}:){{6}}(?:{H16}:{H16}|{V4})|::(?:{H16}:){{5}}(?:{H16}:{H16}|{V4})|(?:{H16})?::(?:{H16}:){{4}}(?:{H16}:{H16}|{V4})|(?:(?:{H16}:){{0,1}}{H16})?::(?:{H16}:){{3}}(?:{H16}:{H16}|{V4})|(?:(?:{H16}:){{0,2}}{H16})?::(?:{H16}:){{2}}(?:{H16}:{H16}|{V4})|(?:(?:{H16}:){{0,3}}{H16})?::{H16}:(?:{H16}:{H16}|{V4})|(?:(?:{H16}:){{0,4}}{H16})?::(?:{H16}:{H16}|{V4})|(?:(?:{H16}:){{0,5}}{H16})?::{H16}|(?:(?:{H16}:){{0,6}}{H16})?::)";
    private const string Pct = "%[0-9a-f]{2}";
    private static readonly string Authority =
        $@"(?:(?:[-a-z0-9._~!$&'()*+,;=:]|{Pct})*@)?(?:\[(?:{Ip6Literal}|v[0-9a-f]+\.[-a-z0-9._~!$&'()*+,;=:]+)\]|{V4}|(?:[-a-z0-9._~!$&'()*+,;=]|{Pct})*)(?::[0-9]*)?";
    private static readonly string Seg = $@"(?:[-a-z0-9._~!$&'()*+,;=:@]|{Pct})";
    private static readonly string QueryFragment = $@"(?:\?(?:[-a-z0-9._~!$&'()*+,;=:@/?]|{Pct})*)?(?:#(?:[-a-z0-9._~!$&'()*+,;=:@/?]|{Pct})*)?";
    private static readonly Regex Uri = new(
        $@"^[a-z][a-z0-9+\-.]*:(?://{Authority}(?:/{Seg}*)*|/(?:{Seg}+(?:/{Seg}*)*)?|{Seg}+(?:/{Seg}*)*)?{QueryFragment}\z", CiI);
    private static readonly Regex UriReference = new(
        $@"^(?:[a-z][a-z0-9+\-.]*:(?://{Authority}(?:/{Seg}*)*|/(?:{Seg}+(?:/{Seg}*)*)?|{Seg}+(?:/{Seg}*)*)?|(?://{Authority}(?:/{Seg}*)*|/(?:{Seg}+(?:/{Seg}*)*)?|(?:[-a-z0-9._~!$&'()*+,;=@]|{Pct})+(?:/{Seg}*)*)?){QueryFragment}\z", CiI);
    private static readonly Regex InvalidIriChars = new(@"[\x00-\x20<>\^`{|}\\]", Ci);
    private static readonly Regex InvalidPercent = new("%(?![0-9a-fA-F]{2})", Ci);
    private static readonly Regex InvalidIriRefChars = new(@"[\x00-\x20\x7F\\]|%(?![0-9a-fA-F]{2})", Ci);
    private static readonly Regex MalformedScheme = new(@"^[a-zA-Z][a-zA-Z0-9+\-.]*//", Ci);
    private static readonly Regex AsciiLabel = new(@"^[a-zA-Z0-9](?:[a-zA-Z0-9-]*[a-zA-Z0-9])?\z", Ci);

    public static bool Test(string format, string value) => format switch
    {
        "date-time" => IsDateTime(value),
        "date" => IsDate(value),
        "duration" => Duration.IsMatch(value),
        "email" => Email.IsMatch(value),
        "hostname" => IsHostname(value),
        "idn-email" => IsIdnEmail(value),
        "idn-hostname" => IsIdnHostname(value),
        "ipv4" => IPv4.IsMatch(value),
        "ipv6" => IPv6.IsMatch(value),
        "iri-reference" => !InvalidIriRefChars.IsMatch(value) && !MalformedScheme.IsMatch(value) && CanParseUrl(value, true),
        "iri" => !InvalidIriChars.IsMatch(value) && !InvalidPercent.IsMatch(value) && CanParseUrl(value, false),
        "json-pointer-uri-fragment" => JsonPointerUriFragment.IsMatch(value),
        "json-pointer" => JsonPointer.IsMatch(value),
        "regex" => JsRegex.TryCreate(value, true, out _),
        "relative-json-pointer" => RelativeJsonPointer.IsMatch(value),
        "time" => IsTime(value),
        "uri-reference" => UriReference.IsMatch(value),
        "uri-template" => UriTemplate.IsMatch(value),
        "uri" => Uri.IsMatch(value),
        "url" => CanParseUrl(value, false),
        "uuid" => Uuid.IsMatch(value),
        _ => true,
    };

    private static bool IsLeapYear(int year) => year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);

    private static bool IsDate(string value)
    {
        var m = Date.Match(value);
        if (!m.Success) return false;
        var year = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var day = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        return month >= 1 && month <= 12 && day >= 1 && day <= (month == 2 && IsLeapYear(year) ? 29 : Days[month]);
    }

    private static bool IsTime(string value)
    {
        var m = Time.Match(value);
        if (!m.Success) return false;
        if (!m.Groups[4].Success && !m.Groups[5].Success) return false;
        var hr = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var min = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var sec = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        if (hr > 23 || min > 59 || sec > 60) return false;
        if (m.Groups[5].Success)
        {
            var tzH0 = int.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture);
            var tzM0 = int.Parse(m.Groups[7].Value, CultureInfo.InvariantCulture);
            if (tzH0 > 23 || tzM0 > 59) return false;
        }

        if (sec < 60) return true;
        var tzSign = m.Groups[5].Value == "-" ? -1 : 1;
        var tzH = m.Groups[6].Success ? int.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture) : 0;
        var tzM = m.Groups[7].Success ? int.Parse(m.Groups[7].Value, CultureInfo.InvariantCulture) : 0;
        var totalUtcMin = hr * 60 + min - tzSign * (tzH * 60 + tzM);
        return (totalUtcMin % 1440 + 1440) % 1440 == 1439;
    }

    private static bool IsDateTime(string value)
    {
        var parts = value.Split('T', 't');
        return parts.Length == 2 && IsDate(parts[0]) && IsTime(parts[1]);
    }

    // Approximation of format/idna hostname (ASCII LDH labels; xn-- labels accepted when Punycode-decodable).
    private static bool IsHostname(string value)
    {
        if (value.Length == 0 || value.Length > 253) return false;
        if (value[^1] == '.') return false;
        return value.Split('.').All(label =>
        {
            if (label.Length == 0 || label.Length > 63) return false;
            if (label.StartsWith("xn--", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    new IdnMapping().GetUnicode(label);
                    return true;
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }

            return AsciiLabel.IsMatch(label) && !(label.Length >= 4 && label[2] == '-' && label[3] == '-');
        });
    }

    // Approximation of format/idna idn-hostname.
    private static bool IsIdnHostname(string value)
    {
        try
        {
            var ascii = new IdnMapping { UseStd3AsciiRules = true }.GetAscii(value.Normalize(NormalizationForm.FormC));
            return IsHostname(ascii);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static readonly Regex IdnEmail = new(
        @"^(?:[A-Za-z0-9!#$%&'*+/=?^_`{|}~\u0080-\uFFFF-]+(?:\.[A-Za-z0-9!#$%&'*+/=?^_`{|}~\u0080-\uFFFF-]+)*|""(?:[^""\\]|\\.)*"")@[\p{L}\p{N}](?:[\p{L}\p{N}-]{0,62})(?<!-)(?:\.[\p{L}\p{N}](?:[\p{L}\p{N}-]{0,62})(?<!-))*\z",
        CiI);

    private static bool IsIdnEmail(string value) => IdnEmail.IsMatch(value.Normalize(NormalizationForm.FormC));

    // Approximation of the WHATWG URL.canParse used by url/iri/iri-reference.
    private static bool CanParseUrl(string value, bool relativeToExample)
    {
        if (relativeToExample) return System.Uri.TryCreate(new System.Uri("http://example.com"), value, out _);
        return System.Uri.TryCreate(value, UriKind.Absolute, out _);
    }
}
