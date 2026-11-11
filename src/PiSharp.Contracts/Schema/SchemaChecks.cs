// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/schema/src/checks.ts, packages/schema/src/annotations.ts, packages/schema/src/shared.ts
//
// Check factories: each returns a closure over its arguments only, so a compiled schema keeps no compile-time state alive. pi-schema's
// specialized closures (inlined leaf codes, declared-object and list fast paths, const and tagged unions) are performance work with the
// same results; this port keeps one closure per keyword group and reaches the same verdicts.
#nullable enable
#pragma warning disable PISHARP_SCHEMA001 // the engine uses its own experimental types

using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace PiSharp.Contracts.Schema;

internal delegate bool SchemaCheck(object? value);

internal delegate bool AnnotatingCheck(object? value, SchemaAnnotations a);

internal static class SchemaChecks
{
    public static readonly SchemaCheck Always = _ => true;
    public static readonly SchemaCheck Never = _ => false;

    /// <summary>The check of a <c>type</c> name; <c>bytes</c> (a byte array) extends JSON Schema for values that never pass through JSON.</summary>
    public static readonly IReadOnlyDictionary<string, SchemaCheck> TypeChecks = new Dictionary<string, SchemaCheck>(StringComparer.Ordinal)
    {
        ["string"] = v => v is string,
        ["number"] = Js.IsNumber,
        ["integer"] = Js.IsInteger,
        ["boolean"] = v => v is bool,
        ["null"] = v => v is null,
        ["array"] = v => v is JsArray,
        ["object"] = IsObject,
        ["bytes"] = v => v is byte[],
        ["bigint"] = v => v is BigInteger,
        ["undefined"] = v => v is JsUndefined,
    };

    /// <summary>Whether <paramref name="v"/> is a JSON object: not null, an array or bytes.</summary>
    public static bool IsObject(object? v) => v is JsObject;

    /// <summary>The regular expression of a <c>pattern</c> source: with the <c>u</c> flag, or without it when the source is invalid with it.</summary>
    public static Regex SharedRegex(string source)
    {
        if (JsRegex.TryCreate(source, true, out var unicode)) return unicode!;
        if (JsRegex.TryCreate(source, false, out var plain, out var reason)) return plain!;
        // new RegExp(source) throws V8's SyntaxError; its reason is known when the translation found the error.
        throw new JsonSchemaException(reason is null ? $"Invalid regular expression: /{source}/" : $"Invalid regular expression: /{source}/: {reason}");
    }

    /// <summary>All of <paramref name="parts"/>.</summary>
    public static SchemaCheck And(IReadOnlyList<SchemaCheck> parts) => parts.Count switch
    {
        0 => Always,
        1 => parts[0],
        2 => And2(parts[0], parts[1]),
        _ => AndAll(parts.ToArray()),
    };

    private static SchemaCheck And2(SchemaCheck a, SchemaCheck b) => v => a(v) && b(v);

    private static SchemaCheck AndAll(SchemaCheck[] parts) => v =>
    {
        foreach (var part in parts) if (!part(v)) return false;
        return true;
    };

    /// <summary>A check whose implementation is set later: the indirection a recursive reference goes through.</summary>
    public static SchemaCheck Indirect(StrongBox<SchemaCheck?> slot) => v =>
    {
        // A schema that refers to itself without consuming the value recurses without end, as in JavaScript (a RangeError there).
        RuntimeHelpers.EnsureSufficientExecutionStack();
        return slot.Value!(v);
    };

    /// <summary><paramref name="check"/> with <paramref name="uri"/> pushed onto the dynamic scope while it runs.</summary>
    public static SchemaCheck Scoped(DynamicScope scope, string uri, SchemaCheck check) => v =>
    {
        var list = scope.Current;
        list.Add(uri);
        try
        {
            return check(v);
        }
        finally
        {
            list.RemoveAt(list.Count - 1);
        }
    };

    /// <summary>Any of the named types.</summary>
    public static SchemaCheck TypeCheck(IReadOnlyList<object?> types)
    {
        var checks = types.Select(t => t is string name && TypeChecks.TryGetValue(name, out var c) ? c : Never).ToArray();
        if (checks.Length == 1) return checks[0];
        return v =>
        {
            foreach (var check in checks) if (check(v)) return true;
            return false;
        };
    }

    /// <summary>A byte array within the length bounds.</summary>
    public static SchemaCheck BytesCheck(double min, double max) => v => v is byte[] b && b.Length >= min && b.Length <= max;

    /// <summary>The number of Unicode code points of <paramref name="s"/> (an unpaired surrogate counts as one).</summary>
    public static int CodePoints(string s)
    {
        var n = 0;
        for (var i = 0; i < s.Length; i++, n++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
        }

        return n;
    }

    private static bool LengthOk(string s, double min, double max)
    {
        var units = s.Length;
        if (units < min) return false;
        if (units < 2 * min && CodePoints(s) < min) return false;
        return units <= max || CodePoints(s) <= max;
    }

    /// <summary>The string keywords (other values pass): length bounds, pattern and format.</summary>
    public static SchemaCheck StringCheck(double min, double max, Regex? re, Func<string, bool>? format) => v =>
    {
        if (v is not string s) return true;
        return LengthOk(s, min, max) && (re is null || re.IsMatch(s)) && (format is null || format(s));
    };

    /// <summary>The number keywords (other values pass).</summary>
    public static SchemaCheck NumberCheck(object min, object max, object xmin, object xmax, object? multipleOf) => v =>
    {
        if (v is not (double or BigInteger)) return true;
        return CompareNumbers(v, min) is >= 0 && CompareNumbers(v, max) is <= 0 &&
            CompareNumbers(v, xmin) is > 0 && CompareNumbers(v, xmax) is < 0 &&
            (multipleOf is null || IsMultiple(v, multipleOf));
    };

    // Relational comparison without rounding a bigint through double. Null is an unordered NaN.
    public static int? CompareNumbers(object left, object right)
    {
        if (left is double a && right is double b) return double.IsNaN(a) || double.IsNaN(b) ? null : a.CompareTo(b);
        if (left is BigInteger x && right is BigInteger y) return x.CompareTo(y);
        if (left is double) return CompareNumbers(right, left) is { } reversed ? -reversed : null;
        var big = (BigInteger)left;
        var number = (double)right;
        if (double.IsNaN(number)) return null;
        if (double.IsPositiveInfinity(number)) return -1;
        if (double.IsNegativeInfinity(number)) return 1;
        var truncated = new BigInteger(number);
        var compared = big.CompareTo(truncated);
        return compared != 0 ? compared : number > Math.Truncate(number) ? -1 : number < Math.Truncate(number) ? 1 : 0;
    }

    public static bool IsMultiple(object value, object divisor)
    {
        if (value is double v && divisor is double d) return IsMultiple(v, d);
        if (value is double vn && !Js.IsInteger(vn) || divisor is double dn && !Js.IsInteger(dn)) return false;
        var integer = value is BigInteger vb ? vb : new BigInteger((double)value);
        var by = divisor is BigInteger db ? db : new BigInteger((double)divisor);
        return !by.IsZero && integer % by == 0;
    }

    /// <summary>Whether <paramref name="v"/> is a multiple of <paramref name="d"/>, exactly for decimals.</summary>
    public static bool IsMultiple(double v, double d)
    {
        var q = v / d;
        if (Js.IsInteger(q)) return true;
        if (!double.IsFinite(q)) return v % d == 0; // C#'s % on doubles is JavaScript's
        var (vc, ve) = Decimal(v);
        var (dc, de) = Decimal(d);
        return ve >= de
            ? (vc * BigInteger.Pow(10, ve - de)) % dc == 0
            : vc % (dc * BigInteger.Pow(10, de - ve)) == 0;
    }

    private static (BigInteger Coefficient, int Exponent) Decimal(double x)
    {
        var text = Js.NumberToString(Math.Abs(x));
        var e = text.IndexOf('e');
        var mantissa = e < 0 ? text : text[..e];
        var exponent = e < 0 ? 0 : int.Parse(text[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var point = mantissa.IndexOf('.');
        var fraction = point < 0 ? 0 : mantissa.Length - point - 1;
        return (BigInteger.Parse(mantissa.Replace(".", "", StringComparison.Ordinal), CultureInfo.InvariantCulture), exponent - fraction);
    }

    /// <summary>Only objects are checked by <paramref name="inner"/>; other values pass.</summary>
    public static SchemaCheck GuardObject(SchemaCheck inner) => v => v is not JsObject || inner(v);

    /// <summary>Only arrays are checked by <paramref name="inner"/>; other values pass.</summary>
    public static SchemaCheck GuardArray(SchemaCheck inner) => v => v is not JsArray || inner(v);

    /// <summary>
    /// Declared properties (optional undefined counts as absent), required keys the properties do not declare,
    /// pattern properties and additional properties. Other values pass.
    /// </summary>
    public static SchemaCheck ObjectCore(
        IReadOnlyList<(string Key, SchemaCheck Check, bool Required)> declared,
        IReadOnlyList<string> extraRequired,
        IReadOnlyList<(Regex Pattern, SchemaCheck Check)> patterns,
        SchemaCheck? additional)
    {
        var entries = declared.ToArray();
        var known = new HashSet<string>(entries.Select(e => e.Key), StringComparer.Ordinal);
        var extra = extraRequired.ToArray();
        var pats = patterns.ToArray();
        return v =>
        {
            if (v is not JsObject o) return true;
            foreach (var (key, check, required) in entries)
            {
                var x = o.GetOwn(key);
                if (x is JsUndefined)
                {
                    if (!required) continue;
                    if (!o.HasOwn(key)) return false;
                }

                if (!check(x)) return false;
            }

            foreach (var key in extra) if (!o.HasOwn(key)) return false;
            if (pats.Length == 0 && additional is null) return true;
            foreach (var key in o.Keys)
            {
                var matched = known.Contains(key);
                foreach (var (pattern, check) in pats)
                {
                    if (!pattern.IsMatch(key)) continue;
                    matched = true;
                    if (!check(o.GetOwn(key))) return false;
                }

                if (!matched && additional is not null && !additional(o.GetOwn(key))) return false;
            }

            return true;
        };
    }

    /// <summary>Every property name passes <paramref name="check"/> (<c>v</c> is an object).</summary>
    public static SchemaCheck PropertyNamesCheck(SchemaCheck check) => v =>
    {
        foreach (var key in ((JsObject)v!).Keys) if (!check(key)) return false;
        return true;
    };

    /// <summary><c>minProperties</c> and <c>maxProperties</c> (<c>v</c> is an object).</summary>
    public static SchemaCheck PropertyCountCheck(double min, double max) => v =>
    {
        var n = ((JsObject)v!).Count;
        return n >= min && n <= max;
    };

    /// <summary><c>dependentRequired</c> (<c>v</c> is an object).</summary>
    public static SchemaCheck DependentRequiredCheck(IReadOnlyList<(string Key, IReadOnlyList<object?> Required)> entries) => v =>
    {
        var o = (JsObject)v!;
        foreach (var (key, list) in entries)
        {
            if (!o.HasOwn(key)) continue;
            foreach (var r in list) if (!HasOwnKey(o, r)) return false;
        }

        return true;
    };

    /// <summary><c>dependentSchemas</c> (<c>v</c> is an object).</summary>
    public static SchemaCheck DependentSchemasCheck(IReadOnlyList<(string Key, SchemaCheck Check)> entries) => v =>
    {
        var o = (JsObject)v!;
        foreach (var (key, check) in entries) if (o.HasOwn(key) && !check(v)) return false;
        return true;
    };

    /// <summary><c>Object.hasOwn(o, key)</c> for a key of any JSON type (a non-string key is converted to a string).</summary>
    public static bool HasOwnKey(JsObject o, object? key) => o.HasOwn(JsString(key));

    /// <summary>Prefix items and the items after them (<c>v</c> is an array).</summary>
    public static SchemaCheck ItemsCheck(IReadOnlyList<SchemaCheck> prefix, SchemaCheck? rest)
    {
        var p = prefix.ToArray();
        return v =>
        {
            var list = (JsArray)v!;
            var m = Math.Min(list.Count, p.Length);
            for (var i = 0; i < m; i++) if (!p[i](list[i])) return false;
            if (rest is null) return true;
            for (var i = p.Length; i < list.Count; i++) if (!rest(list[i])) return false;
            return true;
        };
    }

    /// <summary><c>minItems</c> and <c>maxItems</c> (<c>v</c> is an array).</summary>
    public static SchemaCheck LengthCheck(double min, double max) => v => ((JsArray)v!).Count >= min && ((JsArray)v!).Count <= max;

    /// <summary><c>contains</c> with its bounds (<c>v</c> is an array).</summary>
    public static SchemaCheck ContainsCheck(SchemaCheck check, double min, double max) => v =>
    {
        var n = 0;
        foreach (var item in (JsArray)v!)
        {
            if (check(item) && ++n > max) return false;
        }

        return n >= min;
    };

    /// <summary><c>uniqueItems</c> (<c>v</c> is an array).</summary>
    public static bool UniqueCheck(object? v)
    {
        var list = (JsArray)v!;
        for (var i = 1; i < list.Count; i++)
        {
            for (var j = 0; j < i; j++) if (DeepEqual(list[i], list[j])) return false;
        }

        return true;
    }

    /// <summary><c>enum</c>: primitives by <c>===</c>, objects and arrays by deep equality.</summary>
    public static SchemaCheck EnumCheck(IReadOnlyList<object?> values)
    {
        var primitives = values.Where(x => x is not (JsObject or JsArray)).ToArray();
        var objects = values.Where(x => x is JsObject or JsArray).ToArray();
        return v =>
        {
            foreach (var p in primitives) if (Js.StrictEquals(p, v)) return true;
            return v is JsObject or JsArray && objects.Any(o => DeepEqual(v, o));
        };
    }

    /// <summary><c>const</c>.</summary>
    public static SchemaCheck ConstCheck(object? value)
    {
        if (value is not (JsObject or JsArray)) return v => Js.StrictEquals(v, value);
        return v => DeepEqual(v, value);
    }

    /// <summary><c>anyOf</c>.</summary>
    public static SchemaCheck AnyCheck(IReadOnlyList<SchemaCheck> checks)
    {
        var c = checks.ToArray();
        return v =>
        {
            foreach (var check in c) if (check(v)) return true;
            return false;
        };
    }

    /// <summary><c>oneOf</c>.</summary>
    public static SchemaCheck OneCheck(IReadOnlyList<SchemaCheck> checks)
    {
        var c = checks.ToArray();
        return v =>
        {
            var n = 0;
            foreach (var check in c) if (check(v) && ++n > 1) return false;
            return n == 1;
        };
    }

    /// <summary><c>not</c>.</summary>
    public static SchemaCheck NotCheck(SchemaCheck check) => v => !check(v);

    /// <summary><c>if</c>, <c>then</c> and <c>else</c>.</summary>
    public static SchemaCheck IfCheck(SchemaCheck cond, SchemaCheck then, SchemaCheck otherwise) => v => cond(v) ? then(v) : otherwise(v);

    /// <summary>JSON equality: same primitives, arrays with equal items in order, objects with the same keys and equal values.</summary>
    public static bool DeepEqual(object? a, object? b)
    {
        if (Js.StrictEquals(a, b)) return true;
        switch (a)
        {
            case JsArray x when b is JsArray y:
                if (x.Count != y.Count) return false;
                for (var i = 0; i < x.Count; i++) if (!DeepEqual(x[i], y[i])) return false;
                return true;
            case JsObject x when b is JsObject y:
                if (x.Count != y.Count) return false;
                foreach (var key in x.Keys) if (!y.HasOwn(key) || !DeepEqual(x.GetOwn(key), y.GetOwn(key))) return false;
                return true;
            case byte[] x when b is byte[] y:
                return x.AsSpan().SequenceEqual(y);
            default:
                return false;
        }
    }

    /// <summary><c>String(value)</c> for JSON values (an array joins its items with commas, an object is <c>[object Object]</c>).</summary>
    public static string JsString(object? value) => value switch
    {
        null => "null",
        JsUndefined => "undefined",
        bool b => b ? "true" : "false",
        double d => Js.NumberToString(d),
        string s => s,
        JsArray a => string.Join(",", a.Select(x => x is null or JsUndefined ? "" : JsString(x))),
        byte[] bytes => string.Join(",", bytes.Select(x => x.ToString(CultureInfo.InvariantCulture))),
        _ => "[object Object]",
    };

    /// <summary><c>list.join(separator)</c>: null and undefined items are empty.</summary>
    public static string JsJoin(IEnumerable<object?> list, string separator) =>
        string.Join(separator, list.Select(x => x is null or JsUndefined ? "" : JsString(x)));

    // -----------------------------------------------------------------------------------------------------------------
    // Annotation tracking for unevaluatedProperties and unevaluatedItems (annotations.ts)
    // -----------------------------------------------------------------------------------------------------------------

    public static readonly AnnotatingCheck AnnotatingTrue = (_, _) => true;
    public static readonly AnnotatingCheck AnnotatingFalse = (_, _) => false;

    /// <summary>The indirection a recursive reference goes through.</summary>
    public static AnnotatingCheck IndirectAnnotating(StrongBox<AnnotatingCheck?> slot) => (v, a) =>
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        return slot.Value!(v, a);
    };

    /// <summary>A plain check in annotating position: it evaluates nothing that unevaluated* sees.</summary>
    public static AnnotatingCheck IgnoreAnnotations(SchemaCheck check) => (v, _) => check(v);

    /// <summary>An annotating check run as a plain check, with fresh annotations.</summary>
    public static SchemaCheck WithFreshAnnotations(AnnotatingCheck check) => v => check(v, new SchemaAnnotations());

    /// <summary><paramref name="check"/> with <paramref name="uri"/> pushed onto the dynamic scope while it runs.</summary>
    public static AnnotatingCheck ScopedAnnotating(DynamicScope scope, string uri, AnnotatingCheck check) => (v, a) =>
    {
        var list = scope.Current;
        list.Add(uri);
        try
        {
            return check(v, a);
        }
        finally
        {
            list.RemoveAt(list.Count - 1);
        }
    };

    /// <summary>unevaluated* sees only the annotations of its own schema, never those of its siblings in the parent.</summary>
    public static AnnotatingCheck IsolateAnnotations(AnnotatingCheck check) => (v, a) =>
    {
        var b = new SchemaAnnotations();
        if (!check(v, b)) return false;
        a.Merge(b);
        return true;
    };

    /// <summary>The non-annotating keywords, then each annotating part in order.</summary>
    public static AnnotatingCheck AnnotateAll(SchemaCheck plain, IReadOnlyList<AnnotatingCheck> parts)
    {
        var p = parts.ToArray();
        return (v, a) =>
        {
            if (!plain(v)) return false;
            foreach (var part in p) if (!part(v, a)) return false;
            return true;
        };
    }

    /// <summary><c>properties</c>, <c>patternProperties</c> and <c>additionalProperties</c>.</summary>
    public static AnnotatingCheck AnnotateObject(
        IReadOnlyList<(string Key, SchemaCheck Check)> props,
        IReadOnlyList<(Regex Pattern, SchemaCheck Check)> patterns,
        SchemaCheck? additional)
    {
        var declared = new HashSet<string>(props.Select(p => p.Key), StringComparer.Ordinal);
        var entries = props.ToArray();
        var pats = patterns.ToArray();
        return (v, a) =>
        {
            if (v is not JsObject o) return true;
            var seen = new List<string>();
            foreach (var (key, check) in entries)
            {
                if (!o.HasOwn(key)) continue;
                if (!check(o.GetOwn(key))) return false;
                seen.Add(key);
            }

            foreach (var key in o.Keys)
            {
                var matched = declared.Contains(key);
                foreach (var (pattern, check) in pats)
                {
                    if (!pattern.IsMatch(key)) continue;
                    if (!check(o.GetOwn(key))) return false;
                    matched = true;
                    seen.Add(key);
                }

                if (!matched && additional is not null)
                {
                    if (!additional(o.GetOwn(key))) return false;
                    seen.Add(key);
                }
            }

            foreach (var key in seen) a.AddProp(key);
            return true;
        };
    }

    /// <summary><c>prefixItems</c>/<c>items</c>/<c>additionalItems</c> and <c>contains</c> with its bounds.</summary>
    public static AnnotatingCheck AnnotateArray(IReadOnlyList<SchemaCheck> prefix, SchemaCheck? rest, SchemaCheck? contains, double minContains, double maxContains)
    {
        var p = prefix.ToArray();
        return (v, a) =>
        {
            if (v is not JsArray list) return true;
            var m = Math.Min(list.Count, p.Length);
            for (var i = 0; i < m; i++) if (!p[i](list[i])) return false;
            if (rest is not null)
            {
                for (var i = p.Length; i < list.Count; i++) if (!rest(list[i])) return false;
                a.AllItems = true;
            }

            if (m > a.Items) a.Items = m;
            if (contains is not null)
            {
                var n = 0;
                for (var i = 0; i < list.Count; i++)
                {
                    if (!contains(list[i])) continue;
                    n++;
                    a.AddIndex(i);
                }

                if (n < minContains || n > maxContains) return false;
            }

            return true;
        };
    }

    /// <summary><c>anyOf</c> (or <c>oneOf</c> when <paramref name="exactlyOne"/>): every passing branch contributes its annotations.</summary>
    public static AnnotatingCheck AnnotateAny(IReadOnlyList<AnnotatingCheck> branches, bool exactlyOne)
    {
        var list = branches.ToArray();
        return (v, a) =>
        {
            var n = 0;
            foreach (var branch in list)
            {
                var b = new SchemaAnnotations();
                if (!branch(v, b)) continue;
                n++;
                a.Merge(b);
            }

            return exactlyOne ? n == 1 : n > 0;
        };
    }

    /// <summary><c>if</c>, <c>then</c> and <c>else</c>: a passing <c>if</c> contributes its annotations even without <c>then</c>.</summary>
    public static AnnotatingCheck AnnotateIf(AnnotatingCheck cond, AnnotatingCheck? then, AnnotatingCheck? otherwise) => (v, a) =>
    {
        var b = new SchemaAnnotations();
        if (cond(v, b))
        {
            a.Merge(b);
            return then is null || then(v, a);
        }

        return otherwise is null || otherwise(v, a);
    };

    /// <summary>One <c>dependentSchemas</c> entry.</summary>
    public static AnnotatingCheck AnnotateDependent(string key, AnnotatingCheck check) => (v, a) => v is not JsObject o || !o.HasOwn(key) || check(v, a);

    /// <summary><c>unevaluatedProperties</c>: every property no other keyword evaluated passes <paramref name="check"/>.</summary>
    public static AnnotatingCheck UnevaluatedProperties(SchemaCheck check) => (v, a) =>
    {
        if (v is not JsObject o || a.AllProps) return true;
        foreach (var key in o.Keys)
        {
            if (!(a.Props?.Contains(key) ?? false) && !check(o.GetOwn(key))) return false;
        }

        a.AllProps = true;
        return true;
    };

    /// <summary><c>unevaluatedItems</c>: every item no other keyword evaluated passes <paramref name="check"/>.</summary>
    public static AnnotatingCheck UnevaluatedItems(SchemaCheck check) => (v, a) =>
    {
        if (v is not JsArray list || a.AllItems) return true;
        for (var i = a.Items; i < list.Count; i++)
        {
            if (!(a.Indexes?.Contains(i) ?? false) && !check(list[i])) return false;
        }

        a.AllItems = true;
        return true;
    };
}

/// <summary>What a schema evaluated: property names (or all of them), the evaluated item prefix, items matched by <c>contains</c>.</summary>
internal sealed class SchemaAnnotations
{
    public HashSet<string>? Props;
    public bool AllProps;
    public int Items;
    public bool AllItems;
    public HashSet<int>? Indexes;

    public void Merge(SchemaAnnotations b)
    {
        if (b.Props is not null) foreach (var key in b.Props) AddProp(key);
        AllProps |= b.AllProps;
        if (b.Items > Items) Items = b.Items;
        AllItems |= b.AllItems;
        if (b.Indexes is not null) foreach (var i in b.Indexes) AddIndex(i);
    }

    public void AddProp(string key) => (Props ??= new HashSet<string>(StringComparer.Ordinal)).Add(key);

    public void AddIndex(int index) => (Indexes ??= new HashSet<int>()).Add(index);
}

/// <summary>The dynamic scope of one thread's check: the resource URIs entered, innermost last.</summary>
internal sealed class DynamicScope
{
    private readonly ThreadLocal<List<string>> _scope = new(() => new List<string>());

    public List<string> Current => _scope.Value!;
}
