// The JavaScript value model the schema engine and PiSharp.Agent's TypeBox port share: JSON values as JavaScript sees them (numbers as
// doubles, own keys in JavaScript order, undefined), JSON.parse and JSON.stringify, Number() and String() of numbers, String.prototype.trim.
// Moved here from PiSharp.Agent/ToolArgumentValidation.cs (Pi v1.5.0 sync, schema area) so that pi-schema's port can use it.
#nullable enable

using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PiSharp.Contracts.Schema;

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

    /// <summary>structuredClone / TypeBox Clone for JSON-like values.</summary>
    public static object? Clone(object? value) => value switch
    {
        JsObject o => CloneObject(o),
        JsArray a => new JsArray(a.Select(Clone)),
        _ => value,
    };

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
