// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/schema/src/coerce.ts (with TypeBox 1.3.27's Value.Convert rules, MIT)
//
// Coercion: turns a loosely typed value (a model's tool call arguments) into the value a schema most likely meant, before it is checked.
// Three passes, in this order:
// 1. Optional nulls: a null in an optional property whose schema rejects null is dropped, as if omitted.
// 2. TypeBox 1.3.27's Value.Convert rules keyed on JSON Schema structure ($ref, const, enum, anyOf/oneOf, allOf, type, items/prefixItems,
//    properties/patternProperties): null becomes "null" for a string, "3.5" becomes 3 for an integer, a single value becomes a one-item
//    array, and a union takes the first branch whose conversion passes.
// 3. Primitive coercion by type (also type arrays), as in Ajv's coerceTypes.
// A value that cannot be coerced is left as it is. Every pass is copy-on-write: an object or array is copied only when one of its members
// changes, so an unchanged value comes back as the same reference and the input is never modified. One coercer serves one coerce() call.
#nullable enable
#pragma warning disable PISHARP_SCHEMA001 // the engine uses its own experimental types

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace PiSharp.Contracts.Schema;

internal sealed class SchemaCoercer(SchemaCompiler c, object? schema)
{
    private static readonly Regex BigIntLiteral = new(@"^-?(0|[1-9][0-9]*)n\z", RegexOptions.CultureInvariant);
    private static readonly Regex BigIntDecimal = new(@"^-?(0|[1-9][0-9]*)\.[0-9]+\z", RegexOptions.CultureInvariant);
    private static readonly Regex BigIntInteger = new(@"^-?(0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant);
    private static readonly BigInteger MaxSafe = new(9007199254740991);
    private static readonly BigInteger MinSafe = new(-9007199254740991);

    /// <summary>
    /// The (schema node, value) pairs pass 2 is converting through a re-entry point, as a stack: a recursive schema that meets a value no
    /// branch accepts would otherwise convert it without end. A pair already on the stack leaves the value unchanged.
    /// </summary>
    private readonly List<(JsObject Node, object? Value)> _entered = [];

    /// <summary>How often a re-entry was cut short; results computed across one are not memoized.</summary>
    private int _cuts;

    /// <summary>Results of $ref targets by (node, value): a recursive union whose branches all fail would convert the same children once per branch.</summary>
    private Dictionary<JsObject, Dictionary<object, object?>>? _memo;

    /// <summary><paramref name="value"/> coerced: a copy along the changed paths only, or <paramref name="value"/> itself when nothing changes.</summary>
    public object? Coerce(object? value)
    {
        var @base = c.R.RootBase;
        var withoutNulls = DropOptionalNulls(value, schema, @base);
        return CoerceTypes(ConvertOnly(withoutNulls), schema, @base);
    }

    /// <summary>Pass 2 alone, as TypeBox's Value.Convert.</summary>
    public object? ConvertOnly(object? value)
    {
        try
        {
            return Convert(schema, c.R.RootBase, value);
        }
        finally
        {
            _memo = null;
            _cuts = 0;
        }
    }

    private object? Reenter(JsObject node, string @base, object? value, bool memoize)
    {
        foreach (var (enteredNode, enteredValue) in _entered)
        {
            if (ReferenceEquals(enteredNode, node) && SameValue(enteredValue, value))
            {
                _cuts++;
                return value;
            }
        }

        var key = MemoKey(value);
        if (memoize && _memo is not null && _memo.TryGetValue(node, out var known) && known.TryGetValue(key, out var hit)) return hit;
        var cuts = _cuts;
        _entered.Add((node, value));
        object? result;
        try
        {
            result = Convert(node, @base, value);
        }
        finally
        {
            _entered.RemoveAt(_entered.Count - 1);
        }

        if (memoize && _cuts == cuts)
        {
            _memo ??= new Dictionary<JsObject, Dictionary<object, object?>>(ReferenceEqualityComparer.Instance);
            if (!_memo.TryGetValue(node, out var values)) _memo[node] = values = new Dictionary<object, object?>(MemoComparer.Instance);
            values[key] = result;
        }

        return result;
    }

    /// <summary>Whether <paramref name="value"/> passes the schema <paramref name="node"/>; null when <paramref name="node"/> cannot be compiled (or checked).</summary>
    private bool? Passes(object? node, string @base, object? value)
    {
        try
        {
            return c.Check(node, BaseOf(node, @base))(value);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string BaseOf(object? node, string @base) => node is JsObject o ? c.R.BaseFor(o, @base) : @base;

    // ---------------------------------------------------------------------------------------------------------------
    // Pass 1: optional nulls
    // ---------------------------------------------------------------------------------------------------------------

    private object? DropOptionalNulls(object? value, object? schemaNode, string @base)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (schemaNode is not JsObject node) return value;
        var b = BaseOf(node, @base);
        if (value is JsArray array)
        {
            return MapItems(array, (item, i) => ItemSchemaAt(node, i) is var itemSchema && itemSchema is JsUndefined ? item : DropOptionalNulls(item, itemSchema, b));
        }

        if (value is not JsObject o) return value;
        var properties = node.GetOwn("properties") as JsObject;
        var patterns = Patterns(node);
        var additional = node.GetOwn("additionalProperties") as JsObject;
        if (patterns.Count == 0 && additional is null && properties is null) return value;
        properties ??= new JsObject();
        var required = node.GetOwn("required") is JsArray list ? list : new JsArray();
        JsObject? output = null;
        void Set(string key, object? next)
        {
            if (Js.StrictEquals(next, Js.Get(output ?? o, key))) return;
            output ??= ShallowCopy(o);
            output.Set(key, next);
        }

        foreach (var key in properties.Keys)
        {
            if (!o.HasOwn(key)) continue;
            var property = properties.GetOwn(key);
            var isRef = property is JsObject p && p.GetOwn("$ref") is string;
            if (o.GetOwn(key) is null && !required.Any(r => Js.StrictEquals(r, key)) && !isRef && Passes(property, b, null) == false)
            {
                output ??= ShallowCopy(o);
                output.Remove(key);
                continue;
            }

            Set(key, DropOptionalNulls(o.GetOwn(key), property, b));
        }

        // Values of pattern and additional properties are walked for their own optional nulls; the keys themselves are not optional
        // properties, so they stay.
        if (patterns.Count > 0 || additional is not null)
        {
            foreach (var key in o.Keys)
            {
                var matched = properties.HasOwn(key);
                foreach (var (pattern, item) in patterns)
                {
                    if (!pattern.IsMatch(key)) continue;
                    matched = true;
                    Set(key, DropOptionalNulls((output ?? o).GetOwn(key), item, b));
                }

                if (!matched && additional is not null) Set(key, DropOptionalNulls(o.GetOwn(key), additional, b));
            }
        }

        return output ?? o;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Pass 2: TypeBox's conversion rules
    // ---------------------------------------------------------------------------------------------------------------

    private object? Convert(object? schemaNode, string @base, object? value)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (schemaNode is not JsObject node) return value;
        var b = BaseOf(node, @base);
        if (node.GetOwn("$ref") is string @ref)
        {
            ResolvedSchema target;
            try
            {
                target = c.R.Resolve(@ref, b);
            }
            catch (JsonSchemaException)
            {
                return value;
            }

            return target.Node is JsObject targetNode ? Reenter(targetNode, target.Base, value, true) : value;
        }

        return ConvertNode(node, b, value);
    }

    /// <summary>One schema node's conversion: its first keyword among const, enum, the unions, allOf and type.</summary>
    private object? ConvertNode(JsObject node, string b, object? value)
    {
        if (node.HasOwn("const")) return ConvertLiteral(node.GetOwn("const"), value);
        if (node.GetOwn("enum") is JsArray values) return ConvertEnum(node, values, b, value);
        // A union or intersection node may also declare members (plain JSON Schema; TypeBox's never do): they convert after the branches.
        if (node.GetOwn("anyOf") is JsArray anyOf) return ConvertUnion(node, anyOf, b, value);
        if (node.GetOwn("oneOf") is JsArray oneOf) return ConvertUnion(node, oneOf, b, value);
        if (node.GetOwn("allOf") is JsArray allOf)
        {
            var next = value;
            foreach (var member in allOf) next = Convert(member, b, next);
            return ConvertMembers(node, b, next);
        }

        switch (node.GetOwn("type"))
        {
            case "string":
                return TryString(value, out var s) ? s : value;
            case "number":
                return TryNumber(value, out var n) ? n : value;
            case "integer":
                return TryNumber(value, out var i) ? Math.Truncate(i) : value;
            case "boolean":
                return TryBoolean(value, out var flag) ? flag : value;
            case "null":
                return TryNull(value) ? null : value;
            case "array":
                return ConvertArray(node, b, value);
            case "object":
                return ConvertObject(node, b, value);
            case JsUndefined:
                if (SchemaCompiler.Defined(node, "properties") || SchemaCompiler.Defined(node, "patternProperties")) return ConvertObject(node, b, value);
                if (SchemaCompiler.Defined(node, "items") || SchemaCompiler.Defined(node, "prefixItems")) return ConvertArray(node, b, value);
                return value;
            default:
                return value;
        }
    }

    /// <summary>An enum is a union of its values: a member stays, else the first value the input converts to.</summary>
    private object? ConvertEnum(JsObject node, JsArray values, string @base, object? value)
    {
        if (values.Any(member => Js.StrictEquals(member, value))) return value;
        foreach (var member in values)
        {
            var candidate = ConvertLiteral(member, value);
            if (!Js.StrictEquals(candidate, value) && Passes(node, @base, candidate) == true) return candidate;
        }

        return value;
    }

    /// <summary>A value one branch accepts stays; else the first branch's conversion that the whole union accepts.</summary>
    private object? ConvertUnion(JsObject node, JsArray branches, string @base, object? value)
    {
        if (branches.Any(branch => Passes(branch, @base, value) == true)) return ConvertMembers(node, @base, value);
        foreach (var branch in branches)
        {
            var candidate = ConvertMembers(node, @base, Convert(branch, @base, value));
            if (Passes(node, @base, candidate) == true) return candidate;
        }

        return ConvertMembers(node, @base, value);
    }

    /// <summary>The object members of a union or intersection node, when it declares any.</summary>
    private object? ConvertMembers(JsObject node, string @base, object? value)
    {
        var declares = SchemaCompiler.Defined(node, "properties") || SchemaCompiler.Defined(node, "patternProperties") ||
                       SchemaCompiler.Defined(node, "additionalProperties");
        return declares ? ConvertObject(node, @base, value) : value;
    }

    private object? ConvertArray(JsObject node, string @base, object? value)
    {
        var items = node.GetOwn("items");
        var prefix = items as JsArray ?? node.GetOwn("prefixItems") as JsArray;
        if (prefix is not null)
        {
            // A tuple converts its items and never wraps a single value.
            if (value is not JsArray tuple) return value;
            var rest = items is JsArray ? Js.Undefined : items;
            return MapItems(tuple, (item, i) =>
            {
                var itemSchema = i < prefix.Count ? prefix[i] : rest;
                return itemSchema is JsUndefined ? item : Convert(itemSchema, @base, item);
            });
        }

        if (value is JsArray array) return items is JsUndefined ? array : MapItems(array, (item, _) => Convert(items, @base, item));
        // The wrapped single value is converted by the item schema with the same value: a re-entry point.
        return new JsArray { items is JsObject itemNode ? Reenter(itemNode, @base, value, false) : value };
    }

    private object? ConvertObject(JsObject node, string @base, object? value)
    {
        if (value is not JsObject o) return value;
        var properties = node.GetOwn("properties") as JsObject ?? new JsObject();
        var patterns = Patterns(node);
        var additional = node.GetOwn("additionalProperties") as JsObject;
        JsObject? output = null;
        foreach (var key in o.Keys)
        {
            var current = o.GetOwn(key);
            // An undefined property counts as absent.
            if (current is JsUndefined) continue;
            var next = current;
            var matched = properties.HasOwn(key);
            if (matched) next = Convert(properties.GetOwn(key), @base, next);
            foreach (var (pattern, item) in patterns)
            {
                if (!pattern.IsMatch(key)) continue;
                matched = true;
                next = Convert(item, @base, next);
            }

            if (!matched && additional is not null) next = Convert(additional, @base, next);
            if (Js.StrictEquals(next, current)) continue;
            output ??= ShallowCopy(o);
            output.Set(key, next);
        }

        return output ?? o;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Pass 3: primitive coercion by type
    // ---------------------------------------------------------------------------------------------------------------

    private object? CoerceTypes(object? value, object? schemaNode, string @base)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (schemaNode is not JsObject node) return value;
        var b = BaseOf(node, @base);
        var next = value;
        if (node.GetOwn("allOf") is JsArray allOf) foreach (var member in allOf) next = CoerceTypes(next, member, b);
        if (node.GetOwn("anyOf") is JsArray anyOf) next = CoerceUnion(next, anyOf, b);
        if (node.GetOwn("oneOf") is JsArray oneOf) next = CoerceUnion(next, oneOf, b);
        var types = TypesOf(node);
        var matchesUnionMember = types.Count > 1 && types.Any(type => MatchesType(next, type));
        if (types.Count > 0 && !matchesUnionMember)
        {
            foreach (var type in types)
            {
                var candidate = CoercePrimitive(next, type);
                if (Js.StrictEquals(candidate, next)) continue;
                next = candidate;
                break;
            }
        }

        if (types.Contains("object") && next is JsObject o) next = CoerceProperties(o, node, b);
        if (types.Contains("array") && next is JsArray a) next = CoerceItems(a, node, b);
        return next;
    }

    private object? CoerceUnion(object? value, JsArray branches, string @base)
    {
        foreach (var branch in branches) if (Passes(branch, @base, value) == true) return value;
        foreach (var branch in branches)
        {
            var coerced = CoerceTypes(value, branch, @base);
            if (Passes(branch, @base, coerced) == true) return coerced;
        }

        return value;
    }

    private JsObject CoerceProperties(JsObject value, JsObject node, string @base)
    {
        var properties = node.GetOwn("properties") as JsObject ?? new JsObject();
        var patterns = Patterns(node);
        var additional = node.GetOwn("additionalProperties") as JsObject;
        JsObject? output = null;
        void Set(string key, object? next)
        {
            if (Js.StrictEquals(next, Js.Get(output ?? value, key))) return;
            output ??= ShallowCopy(value);
            output.Set(key, next);
        }

        foreach (var key in properties.Keys)
        {
            // `key in value`: an inherited Object.prototype member counts, and its coercion leaves it as it is.
            if (Js.In(value, key)) Set(key, CoerceTypes(Js.Get(value, key), properties.GetOwn(key), @base));
        }

        foreach (var key in value.Keys)
        {
            var matched = properties.HasOwn(key);
            foreach (var (pattern, item) in patterns)
            {
                if (!pattern.IsMatch(key)) continue;
                matched = true;
                Set(key, CoerceTypes((output ?? value).GetOwn(key), item, @base));
            }

            if (!matched && additional is not null) Set(key, CoerceTypes(value.GetOwn(key), additional, @base));
        }

        return output ?? value;
    }

    private JsArray CoerceItems(JsArray value, JsObject node, string @base) =>
        MapItems(value, (item, i) => ItemSchemaAt(node, i) is JsObject itemSchema ? CoerceTypes(item, itemSchema, @base) : item);

    // ---------------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The schema of an array's item at <paramref name="index"/>: a tuple's (<c>prefixItems</c>, or a draft-7 <c>items</c> array) for its
    /// prefix, <c>items</c> after <c>prefixItems</c>, or <c>items</c> for every item. Undefined when nothing applies.
    /// </summary>
    private static object? ItemSchemaAt(JsObject node, int index)
    {
        var items = node.GetOwn("items");
        if (items is JsArray tuple) return index < tuple.Count ? tuple[index] : Js.Undefined;
        if (node.GetOwn("prefixItems") is JsArray prefix && index < prefix.Count) return prefix[index];
        return items;
    }

    /// <summary><paramref name="items"/> mapped; the same array when every item maps to itself.</summary>
    private static JsArray MapItems(JsArray items, Func<object?, int, object?> map)
    {
        JsArray? output = null;
        for (var i = 0; i < items.Count; i++)
        {
            var next = map(items[i], i);
            if (Js.StrictEquals(next, items[i])) continue;
            output ??= new JsArray(items);
            output[i] = next;
        }

        return output ?? items;
    }

    private static List<(Regex Pattern, object? Schema)> Patterns(JsObject node) =>
        SchemaCompiler.Entries(node.GetOwn("patternProperties")).Select(e => (SchemaChecks.SharedRegex(e.Key), e.Value)).ToList();

    private static JsObject ShallowCopy(JsObject o)
    {
        var copy = new JsObject();
        foreach (var key in o.Keys) copy.Set(key, o.GetOwn(key));
        return copy;
    }

    // Object.is.
    private static bool SameValue(object? a, object? b) => a is double x && b is double y
        ? x.Equals(y) && (x != 0 || double.IsNegative(x) == double.IsNegative(y))
        : Js.StrictEquals(a, b);

    private static object MemoKey(object? value) => value ?? NullKey.Instance;

    private sealed class NullKey
    {
        public static readonly NullKey Instance = new();
    }

    // A Map key: primitives by SameValueZero, objects by reference.
    private sealed class MemoComparer : IEqualityComparer<object>
    {
        public static readonly MemoComparer Instance = new();

        public new bool Equals(object? a, object? b) => a switch
        {
            double x when b is double y => x.Equals(y) || x == y,
            string x when b is string y => string.Equals(x, y, StringComparison.Ordinal),
            bool x when b is bool y => x == y,
            _ => ReferenceEquals(a, b),
        };

        public int GetHashCode(object o) => o switch
        {
            double d => d == 0 ? 0 : d.GetHashCode(),
            string s => StringComparer.Ordinal.GetHashCode(s),
            bool b => b.GetHashCode(),
            _ => RuntimeHelpers.GetHashCode(o),
        };
    }

    // -----------------------------------------------------------------------------------------------------------------
    // TypeBox's conversions of one value (value/convert/try in TypeBox 1.3.27)
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>A literal (const, an enum value) taken from a value of another type that converts to exactly it.</summary>
    private static object? ConvertLiteral(object? literal, object? value)
    {
        if (Js.StrictEquals(literal, value)) return value;
        switch (literal)
        {
            case string text:
                return TryString(value, out var s) && s == text ? s : value;
            case double number:
                return TryNumber(value, out var n) && n == number ? n : value;
            case bool flag:
                return TryBoolean(value, out var b) && b == flag ? b : value;
            default:
                return value;
        }
    }

    private static bool TryString(object? value, out string result)
    {
        switch (value)
        {
            case bool b:
                result = b ? "true" : "false";
                return true;
            case double d when double.IsFinite(d):
                result = Js.NumberToString(d);
                return true;
            case null:
                result = "null";
                return true;
            case string s:
                result = s;
                return true;
            case JsUndefined:
                result = "";
                return true;
            default:
                result = "";
                return false;
        }
    }

    private static bool TryNumber(object? value, out double result)
    {
        switch (value)
        {
            case bool b:
                result = b ? 1 : 0;
                return true;
            case double d when double.IsFinite(d):
                result = d;
                return true;
            case null or JsUndefined:
                result = 0;
                return true;
            case string s:
            {
                var coerced = Js.StringToNumber(s);
                if (double.IsFinite(coerced))
                {
                    result = coerced;
                    return true;
                }

                var lowercase = LowerCase(s);
                if (lowercase == "false")
                {
                    result = 0;
                    return true;
                }

                if (lowercase == "true")
                {
                    result = 1;
                    return true;
                }

                if (BigIntOfString(s) is { } big && big <= MaxSafe && big >= MinSafe)
                {
                    result = (double)big;
                    return true;
                }

                result = 0;
                return false;
            }
            default:
                result = 0;
                return false;
        }
    }

    /// <summary>TypeBox's TryBigInt of a string: <c>12n</c>, <c>12.5</c> (truncated) or <c>12</c>.</summary>
    private static BigInteger? BigIntOfString(string value)
    {
        if (BigIntLiteral.IsMatch(value)) return BigInteger.Parse(value[..^1], System.Globalization.CultureInfo.InvariantCulture);
        if (BigIntDecimal.IsMatch(value)) return BigInteger.Parse(value.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture);
        if (BigIntInteger.IsMatch(value)) return BigInteger.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        return null;
    }

    private static bool TryBoolean(object? value, out bool result)
    {
        result = false;
        switch (value)
        {
            case bool b:
                result = b;
                return true;
            case double d when double.IsFinite(d):
                if (d == 0) return true;
                if (d == 1)
                {
                    result = true;
                    return true;
                }

                return false;
            case null or JsUndefined:
                return true;
            case string s:
            {
                var lowercase = LowerCase(s);
                if (lowercase == "false" || s == "0") return true;
                if (lowercase == "true" || s == "1")
                {
                    result = true;
                    return true;
                }

                return false;
            }
            default:
                return false;
        }
    }

    private static bool TryNull(object? value) => value switch
    {
        bool b => !b,
        double d when double.IsFinite(d) => d == 0,
        null or JsUndefined => true,
        string s => LowerCase(s) is "undefined" or "null" || s is "" or "0",
        _ => false,
    };

    // String.prototype.toLowerCase for the keyword comparisons: U+0130 lowercases to "i" and a combining dot, as in JavaScript.
    private static string LowerCase(string s) => s.Replace(char.ConvertFromUtf32(0x130), "i" + char.ConvertFromUtf32(0x307), StringComparison.Ordinal).ToLowerInvariant();

    private static List<string> TypesOf(JsObject node) => node.GetOwn("type") switch
    {
        string type => [type],
        JsArray list => list.OfType<string>().ToList(),
        _ => [],
    };

    private static bool MatchesType(object? value, string type) => type switch
    {
        "number" => value is double,
        "integer" => Js.IsInteger(value),
        "boolean" => value is bool,
        "string" => value is string,
        "null" => value is null,
        "array" => value is JsArray,
        "object" => value is JsObject or byte[],
        _ => false,
    };

    private static object? CoercePrimitive(object? value, string type)
    {
        switch (type)
        {
            case "number":
            case "integer":
            {
                if (value is null) return 0d;
                if (value is string s && Js.Trim(s).Length > 0)
                {
                    var parsed = Js.StringToNumber(s);
                    if (type == "number" ? double.IsFinite(parsed) : Js.IsInteger(parsed)) return parsed;
                }

                if (value is bool b) return b ? 1d : 0d;
                return value;
            }
            case "boolean":
                if (value is null) return false;
                if (value is "true" || value is 1d) return true;
                if (value is "false" || value is 0d) return false;
                return value;
            case "string":
                if (value is null) return "";
                if (value is double d) return Js.NumberToString(d);
                if (value is bool flag) return flag ? "true" : "false";
                return value;
            case "null":
                return value is "" || value is 0d || value is false ? null : value;
            default:
                return value;
        }
    }
}
