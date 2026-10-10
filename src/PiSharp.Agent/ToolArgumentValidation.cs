// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/utils/validation.ts (with TypeBox 1.3.27 Value.Convert, Compile and its English error messages).
//
// Port of Pi's validateToolArguments():
//
//   const args = structuredClone(toolCall.arguments);
//   normalizeOptionalNulls(args, tool.parameters);
//   Value.Convert(tool.parameters, args);                      // TypeBox 1.3.27, return value discarded
//   const validator = Compile(tool.parameters);
//   if (!Object.getOwnPropertySymbols(tool.parameters).includes(Symbol.for("TypeBox.Kind")))
//       coerceWithJsonSchema(args, tool.parameters) ...         // see note below
//   if (validator.Check(args)) return args;
//   throw new Error(`Validation failed for tool "${name}":\n  - <path>: <message>...\n\nReceived arguments:\n${JSON.stringify(arguments, null, 2)}`);
//
// Which schemas get which treatment (Pi 1.1.0 / TypeBox 1.3.27):
//  * TypeBox 1.x marks its schemas with a hidden, non-enumerable string property "~kind" (Type.Object -> "Object",
//    Type.String -> "String", Type.Optional adds "~optional", ...). It NEVER sets Symbol.for("TypeBox.Kind") (that was
//    TypeBox 0.x). So in Pi 1.1.0 the TYPEBOX_KIND check in validation.ts is always false and coerceWithJsonSchema runs
//    for every tool: built-in tools (read/bash/edit/write/grep/find/ls/powershell, built with `Type` from "typebox"),
//    extension tools (jiti aliases both "typebox" and "@sinclair/typebox" to the bundled TypeBox 1.x) and MCP tools
//    (coding-agent/src/extensions/mcp/tools.ts toParameters(): a plain JSON schema copy, `type` defaulted to "object"
//    and `properties` defaulted to {}).
//  * Value.Convert always runs, but it only acts on schema nodes carrying "~kind" (IsKind uses `'~kind' in schema`),
//    i.e. on TypeBox-built schemas (built-in and Type.*-built extension tools). For plain JSON schemas (MCP tools,
//    extensions passing raw objects) it is a no-op.
//  * ToolSchemaOrigin.LegacyTypeBoxKindSymbol models a schema object that does carry Symbol.for("TypeBox.Kind")
//    (an extension bundling its own TypeBox 0.x): coerceWithJsonSchema is skipped and Value.Convert is a no-op.
//
// In this port a schema is plain JSON. A "~kind" string property on a schema node is honoured exactly like TypeBox's
// hidden property (so a schema serialized with its TypeBox metadata replays exactly). When `typeBoxSchema: true` is
// passed and the schema carries no "~kind" annotations at all, kinds are inferred from the standard TypeBox shapes
// (see InferKind); {type:"string",enum:[...]} is treated as Type.Unsafe (StringEnum), i.e. no Value.Convert.
//
// JS semantics reproduced: object key order (integer-like keys first), JSON.stringify (number formatting such as 1e+21,
// lone-surrogate/control escaping), Number()/String() conversions, `key in obj` / obj[key] seeing Object.prototype
// members (toString, constructor, ...), structuredClone's DataCloneError, TypeBox grapheme-based min/maxLength, maxErrors
// = 8 per error context, the JIT-only fast paths of Compile().Check (additionalProperties:false with all-required keys),
// "Maximum call stack size exceeded" for self-referencing $ref.
//
// Not reproduced (documented limits): BigInt values/schemas, Type.Cyclic/Type.Ref with a context, finite
// TemplateLiterals (converted as String), Value.Convert of Intersect beyond Object/leaf kinds (TypeBox's Extends engine
// is reduced to Integer<Number, Literal<primitive, equal kinds, Array by items, Unsafe=Unknown), $id/$schema-based URI
// resolution and remote refs, $recursiveRef/$dynamicRef (ignored), the "__proto__" property name, URL.canParse-based
// formats (url, iri, iri-reference: approximated with System.Uri), hostname/idn-hostname/idn-email (approximated), JS
// RegExp features without a .NET equivalent (Unicode script/binary properties, astral ranges inside classes), the exact
// text of JS SyntaxErrors for invalid patterns, and array holes. When kinds are inferred (no "~kind"), Type.Any and
// Type.Unknown are indistinguishable ({}), which only matters inside Type.Intersect.
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

namespace PiSharp.Agent;

/// <summary>Outcome of <see cref="ToolArgumentValidation.Validate(string, JsonNode?, JsonNode?, bool)"/>.</summary>
public sealed class ToolArgumentValidationResult
{
    internal ToolArgumentValidationResult(JsonNode? arguments, string? argumentsJson, string? errorMessage)
    {
        Arguments = arguments;
        ArgumentsJson = argumentsJson;
        ErrorMessage = errorMessage;
    }

    /// <summary>True when upstream would return the (coerced) arguments instead of throwing.</summary>
    public bool IsValid => ErrorMessage is null;

    /// <summary>The validated, converted and coerced arguments (a fresh node tree; JS-undefined/function members dropped).</summary>
    public JsonNode? Arguments { get; }

    /// <summary>JSON.stringify(result) exactly as JS would print it (null when JS would produce undefined).</summary>
    public string? ArgumentsJson { get; }

    /// <summary>The exact upstream Error.message when validation fails.</summary>
    public string? ErrorMessage { get; }
}

/// <summary>Port of Pi's validateToolArguments (packages/ai/src/utils/validation.ts) on TypeBox 1.3.27 semantics.</summary>
public static class ToolArgumentValidation
{
    /// <summary>Validates tool-call arguments. <paramref name="typeBoxSchema"/>: the schema was built with TypeBox 1.x (see <see cref="ToolSchemaOrigin.TypeBox"/>).</summary>
    public static ToolArgumentValidationResult Validate(string toolName, JsonNode? parameters, JsonNode? arguments, bool typeBoxSchema = false)
        => Validate(toolName, parameters, arguments, typeBoxSchema ? ToolSchemaOrigin.TypeBox : ToolSchemaOrigin.JsonSchema);

    /// <summary>Validates tool-call arguments against the parameter schema.</summary>
    public static ToolArgumentValidationResult Validate(string toolName, JsonNode? parameters, JsonNode? arguments, ToolSchemaOrigin origin)
        => Run(toolName, Js.FromNode(parameters), Js.FromNode(arguments), origin);

    /// <summary>Same as <see cref="Validate(string, JsonNode?, JsonNode?, ToolSchemaOrigin)"/> but takes JSON text parsed with JSON.parse semantics (lone surrogates, number rounding).</summary>
    public static ToolArgumentValidationResult ValidateJson(string toolName, string parametersJson, string argumentsJson, ToolSchemaOrigin origin)
        => Run(toolName, Js.Parse(parametersJson), Js.Parse(argumentsJson), origin);

    /// <summary>JSON.stringify(value, null, indent) with JavaScript semantics (key order, number format, escaping). Returns null for JS undefined.</summary>
    public static string? Stringify(JsonNode? value, int indent = 0) => Js.Stringify(Js.FromNode(value), indent);

    /// <summary>Entry point on the internal JS value model (used by the differential harness).</summary>
    internal static ToolArgumentValidationResult RunModel(string toolName, object? schema, object? original, ToolSchemaOrigin origin)
        => Run(toolName, schema, original, origin);

    private static ToolArgumentValidationResult Run(string toolName, object? schema, object? original, ToolSchemaOrigin origin)
    {
        try
        {
            var result = ValidateCore(toolName, schema, original, origin);
            var json = Js.Stringify(result, 0);
            return new ToolArgumentValidationResult(Js.ToNode(result), json, null);
        }
        catch (ToolValidationFailure failure)
        {
            return new ToolArgumentValidationResult(null, null, failure.Message);
        }
    }

    private static object? ValidateCore(string toolName, object? schema, object? original, ToolSchemaOrigin origin)
    {
        var inferKinds = origin == ToolSchemaOrigin.TypeBox && !TypeBoxConvert.HasKindAnnotation(schema);
        var args = Js.Clone(original); // structuredClone
        Coercion.NormalizeOptionalNulls(args, schema);
        new TypeBoxConvert(inferKinds).FromType(schema, args); // Value.Convert(tool.parameters, args) - result discarded
        var validator = Validator.Compile(schema); // getValidator(tool.parameters)
        if (origin != ToolSchemaOrigin.LegacyTypeBoxKindSymbol)
        {
            var coerced = Coercion.CoerceWithJsonSchema(args, schema);
            if (!Js.StrictEquals(coerced, args))
            {
                if (Js.IsObject(args) && Js.IsObject(coerced))
                {
                    Js.ReplaceContents(args!, coerced!);
                }
                else
                {
                    return validator.Check(coerced) ? coerced : args;
                }
            }
        }

        if (validator.Check(args)) return args;

        var errors = validator.Errors(args);
        var lines = string.Join("\n", errors.Select(e => $"  - {FormatValidationPath(e)}: {e.Message}"));
        if (lines.Length == 0) lines = "Unknown validation error";
        var received = Js.Stringify(original, 2) ?? "undefined";
        throw new ToolValidationFailure($"Validation failed for tool \"{toolName}\":\n{lines}\n\nReceived arguments:\n{received}");
    }

    private static string FormatValidationPath(SchemaError error)
    {
        if (error.Keyword == "required" && error.RequiredProperties is { Count: > 0 } required && required[0].Length > 0)
        {
            var basePath = ReplacePath(error.InstancePath);
            return basePath.Length > 0 ? $"{basePath}.{required[0]}" : required[0];
        }

        var path = ReplacePath(error.InstancePath);
        return path.Length > 0 ? path : "root";
    }

    private static string ReplacePath(string instancePath)
    {
        var p = instancePath.StartsWith('/') ? instancePath[1..] : instancePath;
        return p.Replace('/', '.');
    }
}

internal sealed class ToolValidationFailure(string message) : Exception(message);

// =====================================================================================================================
// JavaScript value model
// =====================================================================================================================

/// <summary>JS undefined.</summary>
internal sealed class JsUndefined
{
    public static readonly JsUndefined Instance = new();
    private JsUndefined() { }
}

/// <summary>A JS function value (only ever an inherited Object.prototype method looked up through `key in obj` / obj[key]).</summary>
internal sealed class JsFunction
{
    private static readonly Dictionary<string, JsFunction> Cache = new(StringComparer.Ordinal);
    private JsFunction(string name) => Name = name;
    public string Name { get; }

    public static JsFunction Get(string name)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(name, out var f)) Cache[name] = f = new JsFunction(name);
            return f;
        }
    }
}

/// <summary>Ordinary JS object: own string-keyed properties enumerated integer-index keys first (ascending), then insertion order.</summary>
internal sealed class JsObject
{
    private readonly Dictionary<string, object?> _map = new(StringComparer.Ordinal);
    private readonly List<string> _insertion = new();
    private List<string>? _keys;

    public int Count => _map.Count;

    public bool HasOwn(string key) => _map.ContainsKey(key);

    public object? GetOwn(string key) => _map.TryGetValue(key, out var v) ? v : JsUndefined.Instance;

    public void Set(string key, object? value)
    {
        if (!_map.ContainsKey(key))
        {
            _insertion.Add(key);
            _keys = null;
        }

        _map[key] = value;
    }

    public bool Remove(string key)
    {
        if (!_map.Remove(key)) return false;
        _insertion.Remove(key);
        _keys = null;
        return true;
    }

    public IReadOnlyList<string> Keys
    {
        get
        {
            if (_keys is not null) return _keys;
            var ints = new List<(uint, string)>();
            var strs = new List<string>();
            foreach (var k in _insertion)
            {
                if (Js.IsArrayIndex(k, out var idx)) ints.Add((idx, k));
                else strs.Add(k);
            }

            ints.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            var list = new List<string>(ints.Count + strs.Count);
            list.AddRange(ints.Select(i => i.Item2));
            list.AddRange(strs);
            return _keys = list;
        }
    }
}

internal sealed class JsArray : List<object?>
{
    public JsArray() { }
    public JsArray(IEnumerable<object?> items) : base(items) { }
}

internal static class Js
{
    public static readonly object Undefined = JsUndefined.Instance;

    // Object.prototype own properties (all functions). "__proto__" deliberately not modelled.
    private static readonly HashSet<string> InheritedObjectNames = new(StringComparer.Ordinal)
    {
        "constructor", "__defineGetter__", "__defineSetter__", "hasOwnProperty", "__lookupGetter__", "__lookupSetter__",
        "isPrototypeOf", "propertyIsEnumerable", "toString", "valueOf", "toLocaleString",
    };

    public static bool IsUndefined(object? v) => v is JsUndefined;
    public static bool IsNull(object? v) => v is null;
    public static bool IsBoolean(object? v) => v is bool;
    public static bool IsString(object? v) => v is string;
    public static bool IsFunction(object? v) => v is JsFunction;
    public static bool IsArray(object? v) => v is JsArray;
    public static bool IsObject(object? v) => v is JsObject or JsArray; // typeof "object" && !== null
    public static bool IsObjectNotArray(object? v) => v is JsObject;
    public static bool IsNumberType(object? v) => v is double; // typeof "number"
    public static bool IsNumber(object? v) => v is double d && double.IsFinite(d); // Number.isFinite
    public static bool IsInteger(object? v) => v is double d && double.IsFinite(d) && Math.Floor(d) == d;
    public static bool IsValueLike(object? v) => v is null or bool or double or string or JsUndefined;

    /// <summary>Truthiness (ToBoolean).</summary>
    public static bool Truthy(object? v) => v switch
    {
        null => false,
        JsUndefined => false,
        bool b => b,
        double d => !(d == 0 || double.IsNaN(d)),
        string s => s.Length > 0,
        _ => true,
    };

    /// <summary>JS ===.</summary>
    public static bool StrictEquals(object? a, object? b)
    {
        if (a is double da && b is double db) return da == db;
        if (a is string sa && b is string sb) return string.Equals(sa, sb, StringComparison.Ordinal);
        if (a is bool ba && b is bool bb) return ba == bb;
        if (a is null && b is null) return true;
        if (a is JsUndefined && b is JsUndefined) return true;
        return ReferenceEquals(a, b);
    }

    public static bool IsArrayIndex(string key, out uint index)
    {
        index = 0;
        if (key.Length == 0 || key.Length > 10) return false;
        if (key[0] == '0') return key.Length == 1;
        foreach (var c in key) if (c < '0' || c > '9') return false;
        if (!ulong.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var v) || v >= 4294967295UL) return false;
        index = (uint)v;
        return true;
    }

    /// <summary>`key in value` (own or inherited Object.prototype member) for plain objects.</summary>
    public static bool In(JsObject o, string key) => o.HasOwn(key) || InheritedObjectNames.Contains(key);

    /// <summary>TypeBox Guard.HasPropertyKey: hasOwnProperty for __proto__/constructor/prototype, `in` otherwise.</summary>
    public static bool HasPropertyKey(object? value, string key)
    {
        switch (value)
        {
            case JsObject o:
                if (key is "__proto__" or "constructor" or "prototype") return o.HasOwn(key);
                return In(o, key);
            case JsArray a:
                if (key == "length") return true;
                if (IsArrayIndex(key, out var i)) return i < a.Count;
                return key != "__proto__" && key != "constructor" && key != "prototype" && InheritedObjectNames.Contains(key);
            default:
                return false;
        }
    }

    /// <summary>value[key] for objects/arrays (inherited Object.prototype members yield a function).</summary>
    public static object? Get(object? value, string key)
    {
        switch (value)
        {
            case JsObject o:
                if (o.HasOwn(key)) return o.GetOwn(key);
                return InheritedObjectNames.Contains(key) ? JsFunction.Get(key) : Undefined;
            case JsArray a:
                if (key == "length") return (double)a.Count;
                if (IsArrayIndex(key, out var i)) return i < a.Count ? a[(int)i] : Undefined;
                return InheritedObjectNames.Contains(key) ? JsFunction.Get(key) : Undefined;
            default:
                return Undefined;
        }
    }

    /// <summary>Property read of a schema keyword (own properties only; keywords never collide with inherited names).</summary>
    public static object? Prop(object? schema, string key) => schema is JsObject o ? o.GetOwn(key) : Undefined;

    public static bool HasOwnProp(object? schema, string key) => schema is JsObject o && o.HasOwn(key);

    /// <summary>Object.getOwnPropertyNames.</summary>
    public static IReadOnlyList<string> OwnPropertyNames(object? value) => value switch
    {
        JsObject o => o.Keys,
        JsArray a => Enumerable.Range(0, a.Count).Select(i => i.ToString(CultureInfo.InvariantCulture)).Append("length").ToList(),
        string s => Enumerable.Range(0, s.Length).Select(i => i.ToString(CultureInfo.InvariantCulture)).Append("length").ToList(),
        _ => Array.Empty<string>(),
    };

    /// <summary>Object.keys / Object.entries key list (enumerable own string keys).</summary>
    public static IReadOnlyList<string> EnumerableKeys(object? value) => value switch
    {
        JsObject o => o.Keys,
        JsArray a => Enumerable.Range(0, a.Count).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList(),
        string s => Enumerable.Range(0, s.Length).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList(),
        _ => Array.Empty<string>(),
    };

    /// <summary>Object.entries.</summary>
    public static List<KeyValuePair<string, object?>> Entries(object? value)
    {
        var list = new List<KeyValuePair<string, object?>>();
        switch (value)
        {
            case JsObject o:
                foreach (var k in o.Keys) list.Add(new(k, o.GetOwn(k)));
                break;
            case JsArray a:
                for (var i = 0; i < a.Count; i++) list.Add(new(i.ToString(CultureInfo.InvariantCulture), a[i]));
                break;
            case string s:
                for (var i = 0; i < s.Length; i++) list.Add(new(i.ToString(CultureInfo.InvariantCulture), s[i].ToString()));
                break;
        }

        return list;
    }

    /// <summary>Object.values.</summary>
    public static IEnumerable<object?> Values(object? value) => Entries(value).Select(e => e.Value);

    /// <summary>obj[key] = value (own data property).</summary>
    public static void SetMember(object target, string key, object? value)
    {
        switch (target)
        {
            case JsObject o:
                o.Set(key, value);
                break;
            case JsArray a when IsArrayIndex(key, out var i):
                while (a.Count <= (int)i) a.Add(Undefined);
                a[(int)i] = value;
                break;
        }
    }

    /// <summary>for (key of Object.keys(target)) delete target[key]; Object.assign(target, source)</summary>
    public static void ReplaceContents(object target, object source)
    {
        var sourceEntries = Entries(source);
        switch (target)
        {
            case JsObject o:
                foreach (var k in o.Keys.ToList()) o.Remove(k);
                break;
            case JsArray a:
                for (var i = 0; i < a.Count; i++) a[i] = Undefined; // holes approximated as undefined
                break;
        }

        foreach (var (k, v) in sourceEntries) SetMember(target, k, v);
    }

    /// <summary>structuredClone / TypeBox Clone for JSON-like values.</summary>
    public static object? Clone(object? value) => value switch
    {
        JsObject o => CloneObject(o),
        JsArray a => new JsArray(a.Select(Clone)),
        _ => value,
    };

    /// <summary>structuredClone: like <see cref="Clone"/> but throws the DataCloneError message for function values.</summary>
    public static object? StructuredClone(object? value)
    {
        switch (value)
        {
            case JsFunction f:
                throw new ToolValidationFailure($"function {(f.Name == "constructor" ? "Object" : f.Name)}() {{ [native code] }} could not be cloned.");
            case JsObject o:
            {
                var c = new JsObject();
                foreach (var k in o.Keys) c.Set(k, StructuredClone(o.GetOwn(k)));
                return c;
            }
            case JsArray a:
                return new JsArray(a.Select(StructuredClone));
            default:
                return value;
        }
    }

    private static JsObject CloneObject(JsObject o)
    {
        var c = new JsObject();
        foreach (var k in o.Keys) c.Set(k, Clone(o.GetOwn(k)));
        return c;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Numbers
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Number.prototype.toString() / String(number).</summary>
    public static string NumberToString(double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (d == 0) return "0";
        if (double.IsPositiveInfinity(d)) return "Infinity";
        if (double.IsNegativeInfinity(d)) return "-Infinity";
        var negative = d < 0;
        var r = Math.Abs(d).ToString("R", CultureInfo.InvariantCulture);
        var exp = 0;
        var e = r.IndexOfAny(['E', 'e']);
        var mant = r;
        if (e >= 0)
        {
            exp = int.Parse(r[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            mant = r[..e];
        }

        var dot = mant.IndexOf('.');
        var digits = dot >= 0 ? mant.Remove(dot, 1) : mant;
        var n = (dot >= 0 ? dot : mant.Length) + exp;
        var lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') lead++;
        digits = digits[lead..];
        n -= lead;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) return "0";
        var k = digits.Length;
        string s;
        if (k <= n && n <= 21) s = digits + new string('0', n - k);
        else if (0 < n && n <= 21) s = digits[..n] + "." + digits[n..];
        else if (-6 < n && n <= 0) s = "0." + new string('0', -n) + digits;
        else
        {
            var ex = n - 1;
            var sign = ex < 0 ? "-" : "+";
            s = (k == 1 ? digits : digits[..1] + "." + digits[1..]) + "e" + sign + Math.Abs(ex).ToString(CultureInfo.InvariantCulture);
        }

        return negative ? "-" + s : s;
    }

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

    private static readonly Regex DecimalLiteral = new(@"^[+-]?(?:[0-9]+\.?[0-9]*|\.[0-9]+)(?:[eE][+-]?[0-9]+)?\z", RegexOptions.CultureInvariant);

    /// <summary>Number(string) / unary + on a string.</summary>
    public static double StringToNumber(string input)
    {
        var s = Trim(input);
        if (s.Length == 0) return 0;
        switch (s)
        {
            case "Infinity":
            case "+Infinity":
                return double.PositiveInfinity;
            case "-Infinity":
                return double.NegativeInfinity;
        }

        if (s.Length > 2 && s[0] == '0')
        {
            var radix = s[1] switch { 'x' or 'X' => 16, 'o' or 'O' => 8, 'b' or 'B' => 2, _ => 0 };
            if (radix != 0)
            {
                BigInteger acc = 0;
                for (var i = 2; i < s.Length; i++)
                {
                    var c = s[i];
                    var digit = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : 99;
                    if (digit >= radix) return double.NaN;
                    acc = acc * radix + digit;
                }

                return (double)acc;
            }
        }

        if (!DecimalLiteral.IsMatch(s)) return double.NaN;
        return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // JSON
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>JSON.stringify(value, null, indent). Returns null for undefined.</summary>
    public static string? Stringify(object? value, int indent)
    {
        var sb = new StringBuilder();
        var gap = indent > 0 ? new string(' ', Math.Min(indent, 10)) : "";
        return WriteValue(sb, value, gap, "") ? sb.ToString() : null;
    }

    private static bool WriteValue(StringBuilder sb, object? value, string gap, string indent)
    {
        switch (value)
        {
            case JsUndefined:
            case JsFunction:
                return false;
            case null:
                sb.Append("null");
                return true;
            case bool b:
                sb.Append(b ? "true" : "false");
                return true;
            case double d:
                sb.Append(double.IsFinite(d) ? NumberToString(d) : "null");
                return true;
            case string s:
                Quote(sb, s);
                return true;
            case JsArray a:
            {
                if (a.Count == 0)
                {
                    sb.Append("[]");
                    return true;
                }

                var inner = indent + gap;
                sb.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    if (gap.Length > 0) sb.Append('\n').Append(inner);
                    if (!WriteValue(sb, a[i], gap, inner)) sb.Append("null");
                }

                if (gap.Length > 0) sb.Append('\n').Append(indent);
                sb.Append(']');
                return true;
            }
            case JsObject o:
            {
                var inner = indent + gap;
                var any = false;
                sb.Append('{');
                foreach (var k in o.Keys)
                {
                    var v = o.GetOwn(k);
                    if (v is JsUndefined or JsFunction) continue;
                    if (any) sb.Append(',');
                    any = true;
                    if (gap.Length > 0) sb.Append('\n').Append(inner);
                    Quote(sb, k);
                    sb.Append(gap.Length > 0 ? ": " : ":");
                    WriteValue(sb, v, gap, inner);
                }

                if (any && gap.Length > 0) sb.Append('\n').Append(indent);
                sb.Append('}');
                return true;
            }
            default:
                return false;
        }
    }

    private static void Quote(StringBuilder sb, string s)
    {
        sb.Append('"');
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else if (char.IsHighSurrogate(c))
                    {
                        if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                        {
                            sb.Append(c).Append(s[i + 1]);
                            i++;
                        }
                        else sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else if (char.IsLowSurrogate(c))
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else sb.Append(c);

                    break;
            }
        }

        sb.Append('"');
    }

    /// <summary>JSON.parse (numbers become doubles; duplicate keys: last value wins at the first key's position).</summary>
    public static object? Parse(string text)
    {
        var p = new JsonTextParser(text);
        p.SkipWs();
        var v = p.ParseValue();
        p.SkipWs();
        if (!p.AtEnd) throw new FormatException("Unexpected trailing JSON content");
        return v;
    }

    private sealed class JsonTextParser(string text)
    {
        private int _pos;
        public bool AtEnd => _pos >= text.Length;

        public void SkipWs()
        {
            while (_pos < text.Length && text[_pos] is ' ' or '\t' or '\n' or '\r') _pos++;
        }

        public object? ParseValue()
        {
            if (AtEnd) throw new FormatException("Unexpected end of JSON");
            var c = text[_pos];
            switch (c)
            {
                case '{':
                {
                    _pos++;
                    var o = new JsObject();
                    SkipWs();
                    if (text[_pos] == '}')
                    {
                        _pos++;
                        return o;
                    }

                    while (true)
                    {
                        SkipWs();
                        var key = ParseString();
                        SkipWs();
                        Expect(':');
                        SkipWs();
                        o.Set(key, ParseValue());
                        SkipWs();
                        if (text[_pos] == ',')
                        {
                            _pos++;
                            continue;
                        }

                        Expect('}');
                        return o;
                    }
                }
                case '[':
                {
                    _pos++;
                    var a = new JsArray();
                    SkipWs();
                    if (text[_pos] == ']')
                    {
                        _pos++;
                        return a;
                    }

                    while (true)
                    {
                        SkipWs();
                        a.Add(ParseValue());
                        SkipWs();
                        if (text[_pos] == ',')
                        {
                            _pos++;
                            continue;
                        }

                        Expect(']');
                        return a;
                    }
                }
                case '"':
                    return ParseString();
                case 't':
                    ExpectWord("true");
                    return true;
                case 'f':
                    ExpectWord("false");
                    return false;
                case 'n':
                    ExpectWord("null");
                    return null;
                default:
                {
                    var start = _pos;
                    while (_pos < text.Length && text[_pos] is '-' or '+' or '.' or 'e' or 'E' or (>= '0' and <= '9')) _pos++;
                    if (start == _pos) throw new FormatException($"Unexpected character '{c}' in JSON");
                    return double.Parse(text.AsSpan(start, _pos - start), NumberStyles.Float, CultureInfo.InvariantCulture);
                }
            }
        }

        private void Expect(char c)
        {
            if (AtEnd || text[_pos] != c) throw new FormatException($"Expected '{c}' in JSON");
            _pos++;
        }

        private void ExpectWord(string w)
        {
            if (string.CompareOrdinal(text, _pos, w, 0, w.Length) != 0) throw new FormatException("Invalid JSON literal");
            _pos += w.Length;
        }

        private string ParseString()
        {
            Expect('"');
            var sb = new StringBuilder();
            while (true)
            {
                if (AtEnd) throw new FormatException("Unterminated JSON string");
                var c = text[_pos++];
                if (c == '"') return sb.ToString();
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                var e = text[_pos++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        sb.Append((char)int.Parse(text.AsSpan(_pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        _pos += 4;
                        break;
                    default: throw new FormatException("Invalid JSON escape");
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // JsonNode <-> JS model
    // ---------------------------------------------------------------------------------------------------------------

    public static object? FromNode(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject o:
            {
                var r = new JsObject();
                foreach (var (k, v) in o) r.Set(k, FromNode(v));
                return r;
            }
            case JsonArray a:
                return new JsArray(a.Select(FromNode));
            case JsonValue v:
            {
                if (v.TryGetValue<JsonElement>(out var el))
                {
                    return el.ValueKind switch
                    {
                        JsonValueKind.String => el.GetString(),
                        JsonValueKind.Number => double.Parse(el.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Null => null,
                        JsonValueKind.Object or JsonValueKind.Array => FromNode(JsonNode.Parse(el.GetRawText(), documentOptions: PiSharp.Contracts.JsonData.DocumentOptions)),
                        _ => Undefined,
                    };
                }

                if (v.TryGetValue<string>(out var s)) return s;
                if (v.TryGetValue<bool>(out var b)) return b;
                if (v.TryGetValue<double>(out var d)) return d;
                if (v.TryGetValue<long>(out var l)) return (double)l;
                if (v.TryGetValue<int>(out var i)) return (double)i;
                if (v.TryGetValue<decimal>(out var m)) return (double)m;
                if (v.TryGetValue<float>(out var f)) return double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                if (v.TryGetValue<ulong>(out var ul)) return (double)ul;
                if (v.TryGetValue<char>(out var ch)) return ch.ToString();
                return FromNode(JsonNode.Parse(v.ToJsonString(), documentOptions: PiSharp.Contracts.JsonData.DocumentOptions));
            }
            default:
                return Undefined;
        }
    }

    /// <summary>JSON-compatible node (undefined/function members dropped, -0 and non-finite normalized like JSON.stringify).</summary>
    public static JsonNode? ToNode(object? value)
    {
        switch (value)
        {
            case null:
            case JsUndefined:
            case JsFunction:
                return null;
            case bool b:
                return JsonValue.Create(b);
            case double d:
                return double.IsFinite(d) ? JsonValue.Create(d == 0 ? 0d : d) : null;
            case string s:
                return JsonValue.Create(s);
            case JsArray a:
            {
                var r = new JsonArray();
                foreach (var item in a) r.Add(ToNode(item));
                return r;
            }
            case JsObject o:
            {
                var r = new JsonObject();
                foreach (var k in o.Keys)
                {
                    var v = o.GetOwn(k);
                    if (v is JsUndefined or JsFunction) continue;
                    r[k] = ToNode(v);
                }

                return r;
            }
            default:
                return null;
        }
    }

    /// <summary>String.prototype.codePointAt (NaN modelled as -1).</summary>
    public static int CodePointAt(string s, int index)
    {
        if (index < 0 || index >= s.Length) return -1;
        var c = s[index];
        if (char.IsHighSurrogate(c) && index + 1 < s.Length && char.IsLowSurrogate(s[index + 1])) return char.ConvertToUtf32(c, s[index + 1]);
        return c;
    }

    /// <summary>String.prototype.toLowerCase() for the ASCII keyword comparisons used by TypeBox.</summary>
    public static string ToLower(string s) => s.ToLowerInvariant();
}

// =====================================================================================================================
// JS RegExp -> .NET Regex
// =====================================================================================================================

internal static class JsRegex
{
    private static readonly Dictionary<(string, bool), Regex> Cache = new();
    private const string WordClass = "a-zA-Z0-9_";
    private const string AstralPair = @"[\uD800-\uDBFF][\uDC00-\uDFFF]";
    private const string NotLineTerminator = @"[^\n\r\u2028\u2029]";
    private const string SpaceClass = @"\t\n\v\f\r \u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF";

    /// <summary>new RegExp(pattern, unicode ? "u" : "") - throws ArgumentException for patterns that cannot be compiled.</summary>
    public static Regex Create(string pattern, bool unicode)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue((pattern, unicode), out var cached)) return cached;
        }

        var regex = new Regex(Translate(pattern, unicode), RegexOptions.CultureInvariant);
        lock (Cache) Cache[(pattern, unicode)] = regex;
        return regex;
    }

    public static bool TryCreate(string pattern, bool unicode, out Regex? regex)
    {
        try
        {
            regex = Create(pattern, unicode);
            return true;
        }
        catch (ArgumentException)
        {
            regex = null;
            return false;
        }
    }

    public static bool Test(string pattern, bool unicode, string input) => Create(pattern, unicode).IsMatch(input);

    private static string Translate(string p, bool unicode)
    {
        var sb = new StringBuilder();
        var inClass = false;
        var classStart = 0;
        var classAstral = false; // u-mode class that can match a whole astral code point (negated / negated shorthand)
        var groupDepth = 0;
        // In u-mode a negated set matches one code point: let it consume a surrogate pair as a unit.
        string Negated(string set)
        {
            if (inClass)
            {
                classAstral = true;
                return set;
            }

            return unicode ? "(?:" + AstralPair + "|" + set + ")" : set;
        }

        for (var i = 0; i < p.Length; i++)
        {
            var c = p[i];
            if (c == '\\')
            {
                if (i + 1 >= p.Length) throw new ArgumentException("\\ at end of pattern");
                var d = p[++i];
                switch (d)
                {
                    case 'd': sb.Append(inClass ? "0-9" : "[0-9]"); break;
                    case 'D': sb.Append(Negated(inClass ? @"\D" : "[^0-9]")); break;
                    case 'w': sb.Append(inClass ? WordClass : "[" + WordClass + "]"); break;
                    case 'W': sb.Append(Negated(inClass ? @"\W" : "[^" + WordClass + "]")); break;
                    case 's': sb.Append(inClass ? SpaceClass : "[" + SpaceClass + "]"); break;
                    case 'S': sb.Append(Negated(inClass ? @"\S" : "[^" + SpaceClass + "]")); break;
                    case 'b':
                        sb.Append(inClass ? @"\x08" : $"(?:(?<=[{WordClass}])(?![{WordClass}])|(?<![{WordClass}])(?=[{WordClass}]))");
                        break;
                    case 'B':
                        sb.Append(inClass ? "B" : $"(?:(?<=[{WordClass}])(?=[{WordClass}])|(?<![{WordClass}])(?![{WordClass}]))");
                        break;
                    case 'u':
                        if (unicode && i + 1 < p.Length && p[i + 1] == '{')
                        {
                            var close = p.IndexOf('}', i + 2);
                            if (close < 0) throw new ArgumentException("Invalid Unicode escape");
                            var cp = int.Parse(p.AsSpan(i + 2, close - i - 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                            if (cp > 0x10FFFF) throw new ArgumentException("Invalid Unicode escape");
                            AppendCodePoint(sb, cp, inClass);
                            i = close;
                        }
                        else if (IsHex4(p, i + 1))
                        {
                            sb.Append("\\u").Append(p, i + 1, 4);
                            i += 4;
                        }
                        else
                        {
                            if (unicode) throw new ArgumentException("Invalid Unicode escape");
                            sb.Append('u');
                        }

                        break;
                    case 'p':
                    case 'P':
                        if (!unicode)
                        {
                            sb.Append(d);
                            break;
                        }

                        if (i + 1 >= p.Length || p[i + 1] != '{') throw new ArgumentException("Invalid property name");
                        var end = p.IndexOf('}', i + 2);
                        if (end < 0) throw new ArgumentException("Invalid property name");
                        var property = "\\" + d + "{" + MapProperty(p.Substring(i + 2, end - i - 2)) + "}";
                        sb.Append(d == 'P' ? Negated(property) : property);
                        i = end;
                        break;
                    case 'x':
                        if (i + 2 < p.Length && Uri.IsHexDigit(p[i + 1]) && Uri.IsHexDigit(p[i + 2]))
                        {
                            sb.Append("\\x").Append(p, i + 1, 2);
                            i += 2;
                        }
                        else
                        {
                            if (unicode) throw new ArgumentException("Invalid escape");
                            sb.Append('x');
                        }

                        break;
                    case 'c':
                        if (i + 1 < p.Length && char.IsAsciiLetter(p[i + 1]))
                        {
                            sb.Append("\\c").Append(p[i + 1]);
                            i++;
                        }
                        else sb.Append(@"\\c");

                        break;
                    case 'k':
                        sb.Append("\\k");
                        break;
                    case 't': sb.Append("\\t"); break;
                    case 'n': sb.Append("\\n"); break;
                    case 'v': sb.Append("\\v"); break;
                    case 'f': sb.Append("\\f"); break;
                    case 'r': sb.Append("\\r"); break;
                    case '0':
                        if (i + 1 < p.Length && char.IsAsciiDigit(p[i + 1]))
                        {
                            if (unicode) throw new ArgumentException("Invalid decimal escape");
                            sb.Append("\\0");
                        }
                        else sb.Append(@"\x00");

                        break;
                    default:
                        if (char.IsAsciiDigit(d))
                        {
                            sb.Append('\\').Append(d);
                        }
                        else if (char.IsAsciiLetter(d) || d == '_')
                        {
                            if (unicode) throw new ArgumentException("Invalid escape");
                            sb.Append(Regex.Escape(d.ToString()));
                        }
                        else if (char.IsHighSurrogate(d) && i + 1 < p.Length && char.IsLowSurrogate(p[i + 1]))
                        {
                            AppendLiteralPair(sb, d, p[++i], inClass, unicode);
                        }
                        else
                        {
                            if (unicode && "^$\\.*+?()[]{}|/-".IndexOf(d) < 0) throw new ArgumentException("Invalid escape");
                            sb.Append(Regex.Escape(d.ToString()));
                        }

                        break;
                }

                continue;
            }

            if (inClass)
            {
                if (c == ']')
                {
                    inClass = false;
                    sb.Append(']');
                    if (unicode && classAstral)
                    {
                        sb.Insert(classStart, "(?:" + AstralPair + "|");
                        sb.Append(')');
                    }
                }
                else if (c == '[')
                {
                    sb.Append("\\[");
                }
                else if (char.IsHighSurrogate(c) && i + 1 < p.Length && char.IsLowSurrogate(p[i + 1]))
                {
                    sb.Append(c).Append(p[++i]);
                }
                else sb.Append(c);

                continue;
            }

            switch (c)
            {
                case '[':
                {
                    var negate = i + 1 < p.Length && p[i + 1] == '^';
                    var next = i + (negate ? 2 : 1);
                    if (next < p.Length && p[next] == ']')
                    {
                        sb.Append(negate ? unicode ? "(?:" + AstralPair + @"|[\s\S])" : @"[\s\S]" : "(?!)");
                        i = next;
                        break;
                    }

                    inClass = true;
                    classStart = sb.Length;
                    classAstral = negate;
                    sb.Append(negate ? "[^" : "[");
                    i = next - 1;
                    break;
                }
                case '.':
                    sb.Append(unicode ? "(?:" + AstralPair + "|" + NotLineTerminator + ")" : NotLineTerminator);
                    break;
                case '$':
                    sb.Append(@"\z");
                    break;
                case '(':
                    groupDepth++;
                    sb.Append('(');
                    break;
                case ')':
                    if (groupDepth == 0) throw new ArgumentException("Unmatched ')'");
                    groupDepth--;
                    sb.Append(')');
                    break;
                case '{':
                {
                    var quantifier = QuantifierAt(p, i);
                    if (quantifier is not null)
                    {
                        sb.Append(quantifier);
                        i += quantifier.Length - 1;
                    }
                    else
                    {
                        if (unicode) throw new ArgumentException("Lone quantifier brackets");
                        sb.Append("\\{");
                    }

                    break;
                }
                case '}':
                    if (unicode) throw new ArgumentException("Lone quantifier brackets");
                    sb.Append("\\}");
                    break;
                case ']':
                    if (unicode) throw new ArgumentException("Lone ']'");
                    sb.Append("\\]");
                    break;
                case ' ':
                case '#':
                    sb.Append('\\').Append(c);
                    break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < p.Length && char.IsLowSurrogate(p[i + 1]))
                    {
                        AppendLiteralPair(sb, c, p[++i], false, unicode);
                    }
                    else sb.Append(c);

                    break;
            }
        }

        if (inClass) throw new ArgumentException("Unterminated character class");
        return sb.ToString();
    }

    private static string? QuantifierAt(string p, int i)
    {
        var m = Regex.Match(p.Substring(i), @"^\{[0-9]+(,[0-9]*)?\}");
        return m.Success ? m.Value : null;
    }

    private static bool IsHex4(string p, int start)
    {
        if (start + 4 > p.Length) return false;
        for (var k = 0; k < 4; k++) if (!Uri.IsHexDigit(p[start + k])) return false;
        return true;
    }

    private static void AppendLiteralPair(StringBuilder sb, char hi, char lo, bool inClass, bool unicode)
    {
        if (unicode && !inClass) sb.Append("(?:").Append(hi).Append(lo).Append(')');
        else sb.Append(hi).Append(lo);
    }

    private static void AppendCodePoint(StringBuilder sb, int cp, bool inClass)
    {
        if (cp <= 0xFFFF)
        {
            sb.Append("\\u").Append(cp.ToString("X4", CultureInfo.InvariantCulture));
            return;
        }

        var s = char.ConvertFromUtf32(cp);
        var pair = $"\\u{(int)s[0]:X4}\\u{(int)s[1]:X4}";
        sb.Append(inClass ? pair : "(?:" + pair + ")");
    }

    private static readonly Dictionary<string, string> GeneralCategories = new(StringComparer.Ordinal)
    {
        ["L"] = "L", ["Letter"] = "L", ["Lu"] = "Lu", ["Uppercase_Letter"] = "Lu", ["Ll"] = "Ll", ["Lowercase_Letter"] = "Ll",
        ["Lt"] = "Lt", ["Titlecase_Letter"] = "Lt", ["Lm"] = "Lm", ["Modifier_Letter"] = "Lm", ["Lo"] = "Lo", ["Other_Letter"] = "Lo",
        ["M"] = "M", ["Mark"] = "M", ["Combining_Mark"] = "M", ["Mn"] = "Mn", ["Nonspacing_Mark"] = "Mn", ["Mc"] = "Mc",
        ["Spacing_Mark"] = "Mc", ["Me"] = "Me", ["Enclosing_Mark"] = "Me", ["N"] = "N", ["Number"] = "N", ["Nd"] = "Nd",
        ["Decimal_Number"] = "Nd", ["digit"] = "Nd", ["Nl"] = "Nl", ["Letter_Number"] = "Nl", ["No"] = "No", ["Other_Number"] = "No",
        ["P"] = "P", ["Punctuation"] = "P", ["punct"] = "P", ["Pc"] = "Pc", ["Connector_Punctuation"] = "Pc", ["Pd"] = "Pd",
        ["Dash_Punctuation"] = "Pd", ["Ps"] = "Ps", ["Open_Punctuation"] = "Ps", ["Pe"] = "Pe", ["Close_Punctuation"] = "Pe",
        ["Pi"] = "Pi", ["Initial_Punctuation"] = "Pi", ["Pf"] = "Pf", ["Final_Punctuation"] = "Pf", ["Po"] = "Po",
        ["Other_Punctuation"] = "Po", ["S"] = "S", ["Symbol"] = "S", ["Sm"] = "Sm", ["Math_Symbol"] = "Sm", ["Sc"] = "Sc",
        ["Currency_Symbol"] = "Sc", ["Sk"] = "Sk", ["Modifier_Symbol"] = "Sk", ["So"] = "So", ["Other_Symbol"] = "So",
        ["Z"] = "Z", ["Separator"] = "Z", ["Zs"] = "Zs", ["Space_Separator"] = "Zs", ["Zl"] = "Zl", ["Line_Separator"] = "Zl",
        ["Zp"] = "Zp", ["Paragraph_Separator"] = "Zp", ["C"] = "C", ["Other"] = "C", ["Cc"] = "Cc", ["Control"] = "Cc",
        ["cntrl"] = "Cc", ["Cf"] = "Cf", ["Format"] = "Cf", ["Cs"] = "Cs", ["Surrogate"] = "Cs", ["Co"] = "Co",
        ["Private_Use"] = "Co", ["Cn"] = "Cn", ["Unassigned"] = "Cn",
    };

    private static readonly Dictionary<string, string> Scripts = new(StringComparer.Ordinal)
    {
        ["Greek"] = "IsGreekandCoptic", ["Grek"] = "IsGreekandCoptic", ["Cyrillic"] = "IsCyrillic", ["Cyrl"] = "IsCyrillic",
        ["Arabic"] = "IsArabic", ["Arab"] = "IsArabic", ["Hebrew"] = "IsHebrew", ["Hebr"] = "IsHebrew", ["Han"] = "IsCJKUnifiedIdeographs",
        ["Hani"] = "IsCJKUnifiedIdeographs", ["Hiragana"] = "IsHiragana", ["Hira"] = "IsHiragana", ["Katakana"] = "IsKatakana",
        ["Kana"] = "IsKatakana", ["Thai"] = "IsThai", ["Hangul"] = "IsHangulSyllables", ["Hang"] = "IsHangulSyllables",
    };

    private static string MapProperty(string name)
    {
        var eq = name.IndexOf('=');
        if (eq >= 0)
        {
            var key = name[..eq];
            var value = name[(eq + 1)..];
            if (key is "General_Category" or "gc" && GeneralCategories.TryGetValue(value, out var gc)) return gc;
            if (key is "Script" or "sc" or "Script_Extensions" or "scx" && Scripts.TryGetValue(value, out var sc)) return sc;
            throw new ArgumentException($"Unsupported Unicode property {name}");
        }

        if (GeneralCategories.TryGetValue(name, out var cat)) return cat;
        throw new ArgumentException($"Unsupported Unicode property {name}");
    }
}

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

// =====================================================================================================================
// TypeBox Value.Convert (value/convert/*.mjs)
// =====================================================================================================================

internal sealed class TypeBoxConvert(bool inferKinds)
{
    private static readonly Regex BigIntPattern = new(@"^-?(0|[1-9][0-9]*)n\z", RegexOptions.CultureInvariant);
    private static readonly Regex DecimalPattern = new(@"^-?(0|[1-9][0-9]*)\.[0-9]+\z", RegexOptions.CultureInvariant);
    private static readonly Regex IntegerPattern = new(@"^-?(0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant);
    private static readonly BigInteger MaxSafe = new(9007199254740991);
    private static readonly BigInteger MinSafe = new(-9007199254740991);

    public static bool HasKindAnnotation(object? schema)
    {
        switch (schema)
        {
            case JsObject o:
                if (o.HasOwn("~kind")) return true;
                foreach (var k in o.Keys) if (HasKindAnnotation(o.GetOwn(k))) return true;
                return false;
            case JsArray a:
                return a.Any(HasKindAnnotation);
            default:
                return false;
        }
    }

    private string? KindOf(object? type)
    {
        if (type is not JsObject o) return null;
        if (o.HasOwn("~kind")) return o.GetOwn("~kind") as string;
        return inferKinds ? InferKind(o) : null;
    }

    /// <summary>Kind inference for TypeBox-built schemas serialized without their hidden "~kind" metadata.</summary>
    private static string? InferKind(JsObject s)
    {
        if (s.GetOwn("anyOf") is JsArray) return "Union";
        if (s.GetOwn("allOf") is JsArray) return "Intersect";
        if (s.HasOwn("const")) return s.GetOwn("const") is string or double or bool ? "Literal" : null;
        if (s.HasOwn("enum")) return s.HasOwn("type") ? null : "Enum"; // {type, enum} = Type.Unsafe (StringEnum)
        if (s.GetOwn("$ref") is string) return "Ref";
        return s.GetOwn("type") switch
        {
            "string" => "String",
            "number" => "Number",
            "integer" => "Integer",
            "boolean" => "Boolean",
            "null" => "Null",
            "undefined" => "Undefined",
            "object" => s.HasOwn("patternProperties") ? "Record" : "Object",
            "array" => s.GetOwn("items") is JsArray ? "Tuple" : "Array",
            _ => null,
        };
    }

    private sealed class TryResult(object? value)
    {
        public object? Value { get; } = value;
    }

    public object? FromType(object? type, object? value)
    {
        switch (KindOf(type))
        {
            case "Array": return FromArray(type, value);
            case "Boolean": return TryBoolean(value) is { } b ? b.Value : value;
            case "Enum": return FromType(EvaluateEnum(type), value);
            case "Integer": return TryNumber(value) is { } i ? Math.Truncate((double)i.Value!) : value;
            case "Intersect": return EvaluateIntersect(type) is { } evaluated ? FromType(evaluated, value) : value;
            case "Literal": return FromLiteral(type, value);
            case "Null": return TryNull(value) is { } n ? n.Value : value;
            case "Number": return TryNumber(value) is { } num ? num.Value : value;
            case "Object": return FromObject(type, value);
            case "Record": return FromRecord(type, value);
            case "String":
            case "TemplateLiteral":
                return TryString(value) is { } s ? s.Value : value;
            case "Tuple": return FromTuple(type, value);
            case "Undefined":
            case "Void":
                return TryUndefined(value) is not null ? Js.Undefined : value;
            case "Union": return FromUnion(type, value);
            default: return value; // Any, Unknown, Never, Ref (no context), BigInt, Cyclic, Unsafe, plain JSON schema
        }
    }

    private object? FromArray(object? type, object? value)
    {
        var source = value as JsArray ?? new JsArray { value };
        var items = Js.Prop(type, "items");
        return new JsArray(source.Select(v => FromType(items, v)));
    }

    private object? FromLiteral(object? type, object? value)
    {
        var c = Js.Prop(type, "const");
        if (Js.StrictEquals(c, value)) return value;
        TryResult? r = c switch
        {
            bool => TryBoolean(value),
            double => TryNumber(value),
            string => TryString(value),
            _ => null,
        };
        return r is not null && Js.StrictEquals(c, r.Value) ? r.Value : value;
    }

    private object? FromObject(object? type, object? value)
    {
        if (value is not JsObject obj) return value;
        var entries = EntriesRegExp(Js.Prop(type, "properties"));
        var keys = obj.Keys.ToList();
        foreach (var (regex, property) in entries)
        {
            foreach (var key in keys)
            {
                if (!JsRegex.Test(regex, false, key) || IsOptionalUndefined(property, key, obj)) continue;
                obj.Set(key, FromType(property, Js.Get(obj, key)));
            }
        }

        return Js.HasOwnProp(type, "additionalProperties") && Js.IsObject(Js.Prop(type, "additionalProperties"))
            ? FromAdditionalProperties(entries, Js.Prop(type, "additionalProperties"), obj)
            : obj;
    }

    private object? FromRecord(object? type, object? value)
    {
        if (value is not JsObject obj) return value;
        var entries = EntriesRegExp(Js.Prop(type, "patternProperties"));
        var keys = obj.Keys.ToList();
        foreach (var (regex, schema) in entries)
        {
            foreach (var key in keys)
            {
                if (JsRegex.Test(regex, false, key)) obj.Set(key, FromType(schema, Js.Get(obj, key)));
            }
        }

        return Js.HasOwnProp(type, "additionalProperties") && Js.IsObject(Js.Prop(type, "additionalProperties"))
            ? FromAdditionalProperties(entries, Js.Prop(type, "additionalProperties"), obj)
            : obj;
    }

    private object? FromAdditionalProperties(List<(string Regex, object? Schema)> entries, object? additional, JsObject value)
    {
        var keys = value.Keys.ToList();
        foreach (var (regex, _) in entries)
        {
            foreach (var key in keys)
            {
                if (!JsRegex.Test(regex, false, key)) value.Set(key, FromType(additional, Js.Get(value, key)));
            }
        }

        return value;
    }

    private static List<(string Regex, object? Schema)> EntriesRegExp(object? map)
    {
        var list = new List<(string, object?)>();
        foreach (var key in Js.OwnPropertyNames(map))
        {
            var pattern = $"^{key}$";
            if (!JsRegex.TryCreate(pattern, false, out _)) throw new ToolValidationFailure($"Invalid regular expression: /{pattern}/");
            list.Add((pattern, Js.Get(map, key)));
        }

        return list;
    }

    private static bool IsOptionalUndefined(object? property, string key, JsObject value)
        => (property is JsObject or bool) && Js.HasPropertyKey(property, "~optional") && Js.IsUndefined(Js.Get(value, key));

    private object? FromTuple(object? type, object? value)
    {
        if (value is not JsArray arr) return value;
        if (Js.Prop(type, "items") is not JsArray items) return value;
        for (var i = 0; i < Math.Min(items.Count, arr.Count); i++) arr[i] = FromType(items[i], arr[i]);
        return arr;
    }

    private object? FromUnion(object? type, object? value)
    {
        if (Js.Prop(type, "anyOf") is not JsArray anyOf) return value;
        if (anyOf.Any(t => Validator.ValueCheck(t, value))) return value;
        var candidates = anyOf.Select(t => FromType(t, Js.Clone(value))).ToList();
        foreach (var candidate in candidates)
        {
            if (Validator.ValueCheck(type, candidate)) return Js.IsUndefined(candidate) ? value : candidate;
        }

        return value;
    }

    private static JsObject EvaluateEnum(object? type)
    {
        var anyOf = new JsArray();
        if (Js.Prop(type, "enum") is JsArray values)
        {
            foreach (var v in values)
            {
                var lit = new JsObject();
                lit.Set("type", v switch { string => "string", double => "number", bool => "boolean", _ => "null" });
                lit.Set("const", v);
                lit.Set("~kind", "Literal");
                anyOf.Add(lit);
            }
        }

        var union = new JsObject();
        union.Set("anyOf", anyOf);
        union.Set("~kind", "Union");
        return union;
    }

    // type/engine/evaluate/*.mjs: Evaluate(Intersect) = EvaluateUnion(Broaden(Distribute(allOf))). TypeBox's Compare is
    // built on its full Extends engine; here it is reduced to the kind relations tool schemas use (Integer within Number,
    // Literal within its primitive, equal kinds, Array by items), everything else is disjoint unless structurally equal.
    private object? EvaluateIntersect(object? type)
        => Js.Prop(type, "allOf") is JsArray all ? EvaluateIntersectTypes(all.ToList()) : null;

    private static JsObject Kinded(string kind, params (string Key, object? Value)[] members)
    {
        var o = new JsObject();
        foreach (var (k, v) in members) o.Set(k, v);
        o.Set("~kind", kind);
        return o;
    }

    private static JsObject Never() => Kinded("Never", ("not", new JsObject()));

    private object? EvaluateType(object? type) => KindOf(type) switch
    {
        "Enum" => EvaluateUnion(((JsArray)EvaluateEnum(type).GetOwn("anyOf")!).ToList()),
        "Intersect" => EvaluateIntersect(type),
        "Union" => EvaluateUnion(Js.Prop(type, "anyOf") is JsArray a ? a.ToList() : []),
        _ => type,
    };

    private object? EvaluateIntersectTypes(List<object?> types) => EvaluateUnion(Distribute(types));

    private List<object?> Distribute(List<object?> types, List<object?>? initial = null)
    {
        var result = initial ?? new List<object?>();
        foreach (var left in types)
        {
            if (KindOf(left) == "Union")
            {
                var next = new List<object?>();
                foreach (var member in (JsArray)Js.Prop(left, "anyOf")!) next.AddRange(Distribute([member], result));
                result = next;
            }
            else result = DistributeTypeOver(left, result);
        }

        return result;
    }

    private List<object?> DistributeTypeOver(object? type, List<object?> types)
        => types.Count == 0 ? [type] : types.Select(left => DistributeOperation(left, type)).ToList();

    private object? DistributeOperation(object? left, object? right)
    {
        var l = EvaluateType(left);
        var r = EvaluateType(right);
        return KindOf(l) == "Union" || KindOf(r) == "Union" ? EvaluateIntersectTypes([l, r]) : Narrow(l, r);
    }

    private object? Narrow(object? left, object? right)
    {
        var kl = KindOf(left);
        var kr = KindOf(right);
        if (kl is "Never" or "Any") return left;
        if (kl == "Unknown") return right;
        if (kr is "Never" or "Any") return right;
        if (kr == "Unknown") return left;
        if (kl == "Object" && kr == "Object") return Composite(left, right);
        if (kl == "Object") return left;
        if (kr == "Object") return right;
        return Compare(left, right) switch
        {
            CompareResult.LeftInside => left,
            CompareResult.RightInside => right,
            CompareResult.Equal => right,
            _ => Never(),
        };
    }

    private JsObject Composite(object? left, object? right)
    {
        var lp = Js.Prop(left, "properties");
        var rp = Js.Prop(right, "properties");
        var keys = Js.OwnPropertyNames(lp).Concat(Js.OwnPropertyNames(rp)).Distinct(StringComparer.Ordinal).ToList();
        var properties = new JsObject();
        foreach (var key in keys)
        {
            var inLeft = Js.HasPropertyKey(lp, key);
            var inRight = Js.HasPropertyKey(rp, key);
            properties.Set(key, inLeft && inRight ? CompositeProperty(Js.Get(lp, key), Js.Get(rp, key)) : inLeft ? Js.Get(lp, key) : inRight ? Js.Get(rp, key) : Never());
        }

        var required = new JsArray(properties.Keys.Where(k => !Js.HasPropertyKey(properties.GetOwn(k), "~optional")).Cast<object?>());
        var result = new JsObject();
        result.Set("type", "object");
        if (required.Count > 0) result.Set("required", required);
        result.Set("properties", properties);
        result.Set("~kind", "Object");
        return result;
    }

    private object? CompositeProperty(object? left, object? right)
    {
        var isOptional = Js.HasPropertyKey(left, "~optional") && Js.HasPropertyKey(right, "~optional");
        var evaluated = EvaluateIntersectTypes([left, right]);
        if (evaluated is not JsObject o) return evaluated;
        var copy = new JsObject();
        foreach (var k in o.Keys) if (k != "~optional") copy.Set(k, o.GetOwn(k));
        if (isOptional) copy.Set("~optional", true);
        return copy;
    }

    private object? EvaluateUnion(List<object?> types)
    {
        var broadened = Broaden(types);
        return broadened.Count == 1 ? broadened[0] : broadened.Count == 0 ? Never() : Kinded("Union", ("anyOf", new JsArray(broadened)));
    }

    private List<object?> Broaden(List<object?> types)
    {
        var result = new List<object?>();
        foreach (var type in types)
        {
            var evaluated = EvaluateType(type);
            var kind = KindOf(evaluated);
            if (kind is "Any" or "Unknown")
            {
                result = [evaluated];
                break;
            }

            if (kind == "Never") continue;
            if (kind == "Object")
            {
                result.Add(evaluated);
                continue;
            }

            result = BroadenFilter(evaluated, result);
        }

        var flattened = new List<object?>();
        foreach (var t in result)
        {
            if (KindOf(t) == "Union") flattened.AddRange(Broaden(((JsArray)Js.Prop(t, "anyOf")!).ToList()));
            else flattened.Add(t);
        }

        return flattened;
    }

    private List<object?> BroadenFilter(object? type, List<object?> types)
    {
        var kept = new List<object?>();
        foreach (var left in types)
        {
            var compare = Compare(type, left);
            if (compare is CompareResult.LeftInside or CompareResult.Equal) return types;
            if (compare == CompareResult.Disjoint) kept.Add(left);
        }

        kept.Add(type);
        return kept;
    }

    private enum CompareResult { Equal, Disjoint, LeftInside, RightInside }

    private bool IsUnsafe(object? type) => type is JsObject o && (o.HasOwn("~unsafe") || (inferKinds && !o.HasOwn("~kind") && KindOf(o) is null));

    private CompareResult Compare(object? left, object? right)
    {
        var a = Extends(left, right);
        var b = Extends(right, left);
        return a && b ? CompareResult.Equal : a ? CompareResult.LeftInside : b ? CompareResult.RightInside : CompareResult.Disjoint;
    }

    /// <summary>Simplified TypeBox Extends(left, right) for leaf kinds.</summary>
    private bool Extends(object? left, object? right)
    {
        // Canonical(): Type.Unsafe (hidden "~unsafe" key; kind-less nodes when kinds are inferred) compares as Unknown.
        var kl = IsUnsafe(left) ? "Unknown" : KindOf(left);
        var kr = IsUnsafe(right) ? "Unknown" : KindOf(right);
        if (kr is "Any" or "Unknown") return true;
        if (kl == "Never") return true;
        if (kl is "Any" or "Unknown") return false;
        if (kl == "Union") return ((JsArray)Js.Prop(left, "anyOf")!).All(m => Extends(m, right));
        if (kr == "Union") return ((JsArray)Js.Prop(right, "anyOf")!).Any(m => Extends(left, m));
        if (kl == "Literal")
        {
            var c = Js.Prop(left, "const");
            return kr switch
            {
                "Literal" => Js.StrictEquals(c, Js.Prop(right, "const")),
                "String" => c is string,
                "Number" => c is double,
                "Integer" => Js.IsInteger(c),
                "Boolean" => c is bool,
                _ => false,
            };
        }

        if (kl == "Integer" && kr is "Integer" or "Number") return true;
        if (kl is "String" or "Number" or "Boolean" or "Null" && kl == kr) return true;
        if (kl == "Array" && kr == "Array") return Extends(Js.Prop(left, "items"), Js.Prop(right, "items"));
        return kl is not null && kl == kr && Js.Stringify(left, 0) == Js.Stringify(right, 0);
    }

    // value/convert/try/*.mjs ------------------------------------------------------------------------------------------
    private static TryResult? TryBoolean(object? value) => value switch
    {
        bool b => new TryResult(b),
        double d when double.IsFinite(d) => d == 0 ? new TryResult(false) : d == 1 ? new TryResult(true) : null,
        null => new TryResult(false),
        string s => Js.ToLower(s) == "false" ? new TryResult(false)
            : Js.ToLower(s) == "true" ? new TryResult(true)
            : s == "0" ? new TryResult(false)
            : s == "1" ? new TryResult(true)
            : null,
        JsUndefined => new TryResult(false),
        _ => null,
    };

    private static TryResult? TryNumber(object? value)
    {
        switch (value)
        {
            case bool b: return new TryResult(b ? 1d : 0d);
            case double d when double.IsFinite(d): return new TryResult(d);
            case null: return new TryResult(0d);
            case string s:
            {
                var coerced = Js.StringToNumber(s);
                if (double.IsFinite(coerced)) return new TryResult(coerced);
                var lower = Js.ToLower(s);
                if (lower == "false") return new TryResult(0d);
                if (lower == "true") return new TryResult(1d);
                var big = TryBigInt(s);
                if (big is not null) return big.Value <= MaxSafe && big.Value >= MinSafe ? new TryResult((double)big.Value) : null;
                return null;
            }
            case JsUndefined: return new TryResult(0d);
            default: return null;
        }
    }

    private static BigInteger? TryBigInt(string value)
    {
        var lower = Js.ToLower(value);
        if (BigIntPattern.IsMatch(value)) return BigInteger.Parse(value[..^1], CultureInfo.InvariantCulture);
        if (DecimalPattern.IsMatch(value)) return BigInteger.Parse(value.Split('.')[0], CultureInfo.InvariantCulture);
        if (IntegerPattern.IsMatch(value)) return BigInteger.Parse(value, CultureInfo.InvariantCulture);
        if (lower == "false") return BigInteger.Zero;
        if (lower == "true") return BigInteger.One;
        return null;
    }

    private static TryResult? TryString(object? value) => value switch
    {
        bool b => new TryResult(b ? "true" : "false"),
        double d when double.IsFinite(d) => new TryResult(Js.NumberToString(d)),
        null => new TryResult("null"),
        string s => new TryResult(s),
        JsUndefined => new TryResult(""),
        _ => null,
    };

    private static bool IsNullLikeString(string s)
    {
        var lower = Js.ToLower(s);
        return lower == "undefined" || lower == "null" || s.Length == 0 || s == "0";
    }

    private static TryResult? TryNull(object? value) => value switch
    {
        bool b => b ? null : new TryResult(null),
        double d when double.IsFinite(d) => d == 0 ? new TryResult(null) : null,
        null => new TryResult(null),
        string s => IsNullLikeString(s) ? new TryResult(null) : null,
        JsUndefined => new TryResult(null),
        _ => null,
    };

    private static TryResult? TryUndefined(object? value) => value switch
    {
        bool b => b ? null : new TryResult(Js.Undefined),
        double d when double.IsFinite(d) => d == 0 ? new TryResult(Js.Undefined) : null,
        null => new TryResult(Js.Undefined),
        string s => IsNullLikeString(s) ? new TryResult(Js.Undefined) : null,
        JsUndefined => new TryResult(Js.Undefined),
        _ => null,
    };
}

// =====================================================================================================================
// validation.ts: normalizeOptionalNulls / coerceWithJsonSchema
// =====================================================================================================================

internal static class Coercion
{
    private static List<string> GetSchemaTypes(object? schema) => Js.Prop(schema, "type") switch
    {
        string t => [t],
        JsArray a => a.OfType<string>().ToList(),
        _ => [],
    };

    private static bool MatchesJsonType(object? value, string type) => type switch
    {
        "number" => value is double,
        "integer" => Js.IsInteger(value),
        "boolean" => value is bool,
        "string" => value is string,
        "null" => value is null,
        "array" => value is JsArray,
        "object" => value is JsObject,
        _ => false,
    };

    private static object? CoercePrimitiveByType(object? value, string type)
    {
        switch (type)
        {
            case "number":
            {
                if (value is null) return 0d;
                if (value is string s && Js.Trim(s).Length != 0)
                {
                    var parsed = Js.StringToNumber(s);
                    if (double.IsFinite(parsed)) return parsed;
                }

                if (value is bool b) return b ? 1d : 0d;
                return value;
            }
            case "integer":
            {
                if (value is null) return 0d;
                if (value is string s && Js.Trim(s).Length != 0)
                {
                    var parsed = Js.StringToNumber(s);
                    if (Js.IsInteger(parsed)) return parsed;
                }

                if (value is bool b) return b ? 1d : 0d;
                return value;
            }
            case "boolean":
            {
                if (value is null) return false;
                if (value is string s)
                {
                    if (s == "true") return true;
                    if (s == "false") return false;
                }

                if (value is double d)
                {
                    if (d == 1) return true;
                    if (d == 0) return false;
                }

                return value;
            }
            case "string":
            {
                if (value is null) return "";
                if (value is double d) return Js.NumberToString(d);
                if (value is bool b) return b ? "true" : "false";
                return value;
            }
            case "null":
            {
                if (value is "" || (value is double d && d == 0) || value is false) return null;
                return value;
            }
            default:
                return value;
        }
    }

    private static void ApplySchemaObjectCoercion(JsObject value, object? schema)
    {
        var properties = Js.Prop(schema, "properties");
        var definedKeys = Js.Truthy(properties) ? Js.EnumerableKeys(properties).ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        if (Js.Truthy(properties))
        {
            foreach (var (key, propertySchema) in Js.Entries(properties))
            {
                if (!Js.In(value, key)) continue;
                value.Set(key, CoerceWithJsonSchema(Js.Get(value, key), propertySchema));
            }
        }

        var additional = Js.Prop(schema, "additionalProperties");
        if (Js.Truthy(additional) && Js.IsObject(additional))
        {
            foreach (var (key, propertyValue) in Js.Entries(value))
            {
                if (definedKeys.Contains(key)) continue;
                value.Set(key, CoerceWithJsonSchema(propertyValue, additional));
            }
        }
    }

    private static void ApplySchemaArrayCoercion(JsArray value, object? schema)
    {
        var items = Js.Prop(schema, "items");
        if (items is JsArray tuple)
        {
            for (var index = 0; index < value.Count; index++)
            {
                var itemSchema = index < tuple.Count ? tuple[index] : Js.Undefined;
                if (!Js.Truthy(itemSchema)) continue;
                value[index] = CoerceWithJsonSchema(value[index], itemSchema);
            }

            return;
        }

        if (Js.Truthy(items) && Js.IsObject(items))
        {
            for (var index = 0; index < value.Count; index++) value[index] = CoerceWithJsonSchema(value[index], items);
        }
    }

    private static object? CoerceWithUnionSchema(object? value, JsArray schemas)
    {
        foreach (var schema in schemas)
        {
            var validator = Validator.TryGetSubSchemaValidator(schema);
            if (validator is not null && validator.Check(value)) return value;
        }

        foreach (var schema in schemas)
        {
            var candidate = Js.StructuredClone(value);
            var coerced = CoerceWithJsonSchema(candidate, schema);
            var validator = Validator.TryGetSubSchemaValidator(schema);
            if (validator is not null && validator.Check(coerced)) return coerced;
        }

        return value;
    }

    public static object? CoerceWithJsonSchema(object? value, object? schema)
    {
        var nextValue = value;
        if (Js.Prop(schema, "allOf") is JsArray allOf)
        {
            foreach (var nested in allOf) nextValue = CoerceWithJsonSchema(nextValue, nested);
        }

        if (Js.Prop(schema, "anyOf") is JsArray anyOf) nextValue = CoerceWithUnionSchema(nextValue, anyOf);
        if (Js.Prop(schema, "oneOf") is JsArray oneOf) nextValue = CoerceWithUnionSchema(nextValue, oneOf);

        var schemaTypes = GetSchemaTypes(schema);
        var matchesUnionMember = schemaTypes.Count > 1 && schemaTypes.Any(t => MatchesJsonType(nextValue, t));
        if (schemaTypes.Count > 0 && !matchesUnionMember)
        {
            foreach (var schemaType in schemaTypes)
            {
                var candidate = CoercePrimitiveByType(nextValue, schemaType);
                if (!Js.StrictEquals(candidate, nextValue))
                {
                    nextValue = candidate;
                    break;
                }
            }
        }

        if (schemaTypes.Contains("object") && nextValue is JsObject obj) ApplySchemaObjectCoercion(obj, schema);
        if (schemaTypes.Contains("array") && nextValue is JsArray arr) ApplySchemaArrayCoercion(arr, schema);
        return nextValue;
    }

    public static void NormalizeOptionalNulls(object? value, object? schema)
    {
        if (value is JsArray arr)
        {
            var items = Js.Prop(schema, "items");
            if (items is JsArray tuple)
            {
                for (var index = 0; index < arr.Count; index++)
                {
                    var itemSchema = index < tuple.Count ? tuple[index] : Js.Undefined;
                    if (Js.Truthy(itemSchema)) NormalizeOptionalNulls(arr[index], itemSchema);
                }
            }
            else if (Js.Truthy(items))
            {
                foreach (var item in arr) NormalizeOptionalNulls(item, items);
            }

            return;
        }

        if (value is not JsObject obj || !Js.Truthy(Js.Prop(schema, "properties"))) return;
        var required = Js.Prop(schema, "required") is JsArray r ? r.OfType<string>().ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, propertySchema) in Js.Entries(Js.Prop(schema, "properties")))
        {
            if (!Js.In(obj, key)) continue;
            if (Js.Get(obj, key) is null &&
                !required.Contains(key) &&
                Js.Prop(propertySchema, "$ref") is not string &&
                Validator.TryGetSubSchemaValidator(propertySchema) is { } v && !v.Check(null))
            {
                obj.Remove(key);
            }
            else
            {
                NormalizeOptionalNulls(Js.Get(obj, key), propertySchema);
            }
        }
    }
}
