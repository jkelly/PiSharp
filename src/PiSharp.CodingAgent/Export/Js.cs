// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/export-html/index.ts (JSON.parse/JSON.stringify,
// String.prototype.replace and Number formatting semantics the exporter relies on, ECMA-262 behaviour as run by Node/V8).
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace PiSharp.CodingAgent.Export;

/// <summary>An ordered JavaScript object as produced by JSON.parse or an object literal.</summary>
public sealed class JsObject
{
    private readonly List<string> keys = [];
    private readonly Dictionary<string, object?> values = new(StringComparer.Ordinal);
    public IReadOnlyList<string> Keys => keys;
    public int Count => keys.Count;
    public bool Has(string key) => values.ContainsKey(key);
    /// <summary>Property read; a missing property is JavaScript undefined (<see cref="Js.Undefined"/>).</summary>
    public object? this[string key]
    {
        get => values.TryGetValue(key, out var value) ? value : Js.Undefined;
        set { if (!values.ContainsKey(key)) keys.Add(key); values[key] = value; }
    }
    public JsObject Set(string key, object? value) { this[key] = value; return this; }
    public void Remove(string key) { if (values.Remove(key)) keys.Remove(key); }
    /// <summary>Object spread: a shallow copy with the same property order.</summary>
    public JsObject Clone() { var copy = new JsObject(); foreach (var key in keys) copy[key] = values[key]; return copy; }
}

/// <summary>JavaScript value semantics used by the export port. Values are null, bool, double, string, List&lt;object?&gt;,
/// <see cref="JsObject"/> or <see cref="Undefined"/>.</summary>
public static class Js
{
    public sealed class UndefinedValue { internal UndefinedValue() { } public override string ToString() => "undefined"; }
    public static readonly UndefinedValue Undefined = new();
    public static bool IsUndefined(object? value) => ReferenceEquals(value, Undefined);

    /// <summary>Math.round: the nearest integer, ties toward +Infinity.</summary>
    public static double Round(double value)
    {
        if (!double.IsFinite(value)) return value;
        var floor = Math.Floor(value);
        return value - floor >= 0.5 ? floor + 1 : floor;
    }

    /// <summary>V8 Math.hypot: values normalized by the largest, Kahan-summed, sqrt times the largest.</summary>
    public static double Hypot(params double[] values)
    {
        if (values.Length == 0) return 0;
        var abs = new double[values.Length]; var nan = false; double max = 0;
        for (var i = 0; i < values.Length; i++)
        {
            if (double.IsNaN(values[i])) { nan = true; continue; }
            abs[i] = Math.Abs(values[i]); if (abs[i] > max) max = abs[i];
        }
        if (double.IsPositiveInfinity(max)) return double.PositiveInfinity;
        if (nan) return double.NaN;
        if (max == 0) return 0;
        double sum = 0, compensation = 0;
        foreach (var value in abs)
        {
            var n = value / max; var summand = n * n - compensation; var preliminary = sum + summand;
            compensation = preliminary - sum - summand; sum = preliminary;
        }
        return Math.Sqrt(sum) * max;
    }

    /// <summary>Math.max (NaN wins, -Infinity for no arguments).</summary>
    public static double Max(params double[] values)
    {
        var result = double.NegativeInfinity;
        foreach (var value in values) { if (double.IsNaN(value)) return double.NaN; if (value > result || value == 0 && result == 0 && !double.IsNegative(value)) result = value; }
        return result;
    }

    /// <summary>Math.min (NaN wins, +Infinity for no arguments).</summary>
    public static double Min(params double[] values)
    {
        var result = double.PositiveInfinity;
        foreach (var value in values) { if (double.IsNaN(value)) return double.NaN; if (value < result || value == 0 && result == 0 && double.IsNegative(value)) result = value; }
        return result;
    }

    /// <summary>Math.round(value).toString(16).padStart(2, "0").</summary>
    internal static string Hex2(double value) => IntegerToString(Round(value), 16).PadLeft(2, '0');

    /// <summary>Number.prototype.toString(radix) for integral values.</summary>
    internal static string IntegerToString(double value, int radix)
    {
        if (radix == 10 || !double.IsFinite(value)) return NumberToString(value);
        var negative = value < 0; var magnitude = Math.Abs(value);
        if (magnitude < 9007199254740992d)
        {
            var text = Convert.ToString((long)magnitude, radix);
            return negative && magnitude != 0 ? "-" + text : text;
        }
        var big = new System.Numerics.BigInteger(magnitude); var builder = new StringBuilder();
        while (big > 0) { builder.Insert(0, "0123456789abcdefghijklmnopqrstuvwxyz"[(int)(big % radix)]); big /= radix; }
        return (negative ? "-" : "") + builder;
    }

    /// <summary>Number::toString(10): shortest round-trip digits in JavaScript notation.</summary>
    public static string NumberToString(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "Infinity";
        if (double.IsNegativeInfinity(value)) return "-Infinity";
        if (value == 0) return "0";
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        var negative = text[0] == '-'; if (negative) text = text[1..];
        var exponent = 0; var e = text.IndexOfAny(['E', 'e']);
        if (e >= 0) { exponent = int.Parse(text[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture); text = text[..e]; }
        var dot = text.IndexOf('.');
        var digits = dot >= 0 ? text[..dot] + text[(dot + 1)..] : text; var point = dot >= 0 ? dot : text.Length;
        var lead = 0; while (lead < digits.Length - 1 && digits[lead] == '0') { lead++; point--; }
        digits = digits[lead..].TrimEnd('0'); if (digits.Length == 0) digits = "0";
        int n = point + exponent, k = digits.Length; string result;
        if (k <= n && n <= 21) result = digits + new string('0', n - k);
        else if (0 < n && n <= 21) result = digits[..n] + "." + digits[n..];
        else if (-6 < n && n <= 0) result = "0." + new string('0', -n) + digits;
        else
        {
            var power = n - 1; var sign = power < 0 ? "-" : "+";
            result = (k == 1 ? digits : digits[..1] + "." + digits[1..]) + "e" + sign + Math.Abs(power).ToString(CultureInfo.InvariantCulture);
        }
        return negative ? "-" + result : result;
    }

    /// <summary>String.prototype.trim: WhiteSpace (with U+FEFF and Zs) and LineTerminator, not U+0085.</summary>
    public static string Trim(string value)
    {
        static bool Space(char c) => c == (char)0xFEFF || c != (char)0x85 && char.IsWhiteSpace(c);
        int start = 0, end = value.Length;
        while (start < end && Space(value[start])) start++;
        while (end > start && Space(value[end - 1])) end--;
        return value[start..end];
    }

    /// <summary>ToBoolean.</summary>
    public static bool Truthy(object? value) => value switch
    {
        null => false, UndefinedValue => false, bool flag => flag, double number => !(number == 0 || double.IsNaN(number)),
        string text => text.Length != 0, _ => true
    };

    /// <summary>ToString for template literals and string concatenation.</summary>
    public static string ToJsString(object? value) => value switch
    {
        null => "null", UndefinedValue => "undefined", bool flag => flag ? "true" : "false", double number => NumberToString(number),
        string text => text, List<object?> array => string.Join(",", array.Select(item => item is null || item is UndefinedValue ? "" : ToJsString(item))),
        _ => "[object Object]"
    };

    /// <summary>String.prototype.replace with a string pattern: the first occurrence only, with GetSubstitution
    /// expanding $$, $&amp;, $` and $' in the replacement (no captures, so $n and $&lt; stay literal).</summary>
    public static string ReplaceFirst(string source, string pattern, string replacement)
    {
        var position = source.IndexOf(pattern, StringComparison.Ordinal);
        if (position < 0) return source;
        var builder = new StringBuilder(source.Length + replacement.Length);
        builder.Append(source, 0, position);
        for (var i = 0; i < replacement.Length; i++)
        {
            var character = replacement[i];
            if (character != '$' || i + 1 >= replacement.Length) { builder.Append(character); continue; }
            switch (replacement[i + 1])
            {
                case '$': builder.Append('$'); i++; break;
                case '&': builder.Append(pattern); i++; break;
                case '`': builder.Append(source, 0, position); i++; break;
                case '\'': builder.Append(source, position + pattern.Length, source.Length - position - pattern.Length); i++; break;
                default: builder.Append('$'); break;
            }
        }
        builder.Append(source, position + pattern.Length, source.Length - position - pattern.Length);
        return builder.ToString();
    }

    /// <summary>JSON.parse (no reviver). Throws <see cref="FormatException"/> for invalid JSON text.</summary>
    public static object? ParseJson(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parser = new Parser(text); parser.SkipWhitespace();
        var value = parser.Value(); parser.SkipWhitespace();
        if (parser.Position != text.Length) throw parser.Error();
        return value;
    }

    private sealed class Parser(string text)
    {
        internal int Position;
        internal FormatException Error() => new(Position < text.Length
            ? $"Unexpected token '{text[Position]}' in JSON at position {Position}" : "Unexpected end of JSON input");
        internal void SkipWhitespace() { while (Position < text.Length && text[Position] is ' ' or '\t' or '\n' or '\r') Position++; }
        internal object? Value()
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (Position >= text.Length) throw Error();
            switch (text[Position])
            {
                case '{':
                {
                    Position++; var result = new JsObject(); SkipWhitespace();
                    if (Position < text.Length && text[Position] == '}') { Position++; return result; }
                    while (true)
                    {
                        SkipWhitespace(); if (Position >= text.Length || text[Position] != '"') throw Error();
                        var key = String(); SkipWhitespace();
                        if (Position >= text.Length || text[Position] != ':') throw Error();
                        Position++; SkipWhitespace(); result[key] = Value(); SkipWhitespace();
                        if (Position < text.Length && text[Position] == ',') { Position++; continue; }
                        if (Position < text.Length && text[Position] == '}') { Position++; return result; }
                        throw Error();
                    }
                }
                case '[':
                {
                    Position++; var result = new List<object?>(); SkipWhitespace();
                    if (Position < text.Length && text[Position] == ']') { Position++; return result; }
                    while (true)
                    {
                        SkipWhitespace(); result.Add(Value()); SkipWhitespace();
                        if (Position < text.Length && text[Position] == ',') { Position++; continue; }
                        if (Position < text.Length && text[Position] == ']') { Position++; return result; }
                        throw Error();
                    }
                }
                case '"': return String();
                case 't': return Literal("true", true);
                case 'f': return Literal("false", false);
                case 'n': return Literal("null", null);
                default: return Number();
            }
        }
        private object? Literal(string word, object? value)
        {
            if (string.CompareOrdinal(text, Position, word, 0, word.Length) != 0) throw Error();
            Position += word.Length; return value;
        }
        private double Number()
        {
            var start = Position;
            if (Position < text.Length && text[Position] == '-') Position++;
            if (Position < text.Length && text[Position] == '0') Position++;
            else if (Position < text.Length && text[Position] is >= '1' and <= '9') while (Position < text.Length && char.IsAsciiDigit(text[Position])) Position++;
            else throw Error();
            if (Position < text.Length && text[Position] == '.')
            {
                Position++; if (Position >= text.Length || !char.IsAsciiDigit(text[Position])) throw Error();
                while (Position < text.Length && char.IsAsciiDigit(text[Position])) Position++;
            }
            if (Position < text.Length && text[Position] is 'e' or 'E')
            {
                Position++; if (Position < text.Length && text[Position] is '+' or '-') Position++;
                if (Position >= text.Length || !char.IsAsciiDigit(text[Position])) throw Error();
                while (Position < text.Length && char.IsAsciiDigit(text[Position])) Position++;
            }
            return double.Parse(text.AsSpan(start, Position - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        private string String()
        {
            Position++; var builder = new StringBuilder();
            while (true)
            {
                if (Position >= text.Length) throw Error();
                var character = text[Position];
                if (character == '"') { Position++; return builder.ToString(); }
                if (character < 0x20) throw Error();
                if (character != '\\') { builder.Append(character); Position++; continue; }
                Position++; if (Position >= text.Length) throw Error();
                switch (text[Position])
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (Position + 4 >= text.Length || !int.TryParse(text.AsSpan(Position + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                            throw Error();
                        builder.Append((char)code); Position += 4; break;
                    default: throw Error();
                }
                Position++;
            }
        }
    }

    /// <summary>JSON.stringify (no replacer or indentation): array-index keys first in ascending order, undefined
    /// properties omitted, undefined array items and non-finite numbers written as null.</summary>
    public static string Stringify(object? value)
    {
        var builder = new StringBuilder(); Write(builder, value); return builder.ToString();
    }

    /// <summary>JSON.stringify of a value that may be undefined (returns null for undefined).</summary>
    public static string? StringifyOrUndefined(object? value) => IsUndefined(value) ? null : Stringify(value);

    private static void Write(StringBuilder builder, object? value)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (value)
        {
            case null: builder.Append("null"); break;
            case UndefinedValue: builder.Append("null"); break;
            case bool flag: builder.Append(flag ? "true" : "false"); break;
            case double number: builder.Append(double.IsFinite(number) ? NumberToString(number) : "null"); break;
            case int number: builder.Append(number.ToString(CultureInfo.InvariantCulture)); break;
            case long number: builder.Append(NumberToString(number)); break;
            case string text: Quote(builder, text); break;
            case List<object?> array:
                builder.Append('[');
                for (var i = 0; i < array.Count; i++) { if (i > 0) builder.Append(','); Write(builder, array[i]); }
                builder.Append(']'); break;
            case JsObject obj:
            {
                builder.Append('{'); var first = true;
                foreach (var key in OrderedKeys(obj))
                {
                    var item = obj[key];
                    if (item is UndefinedValue) continue;
                    if (!first) builder.Append(','); first = false;
                    Quote(builder, key); builder.Append(':'); Write(builder, item);
                }
                builder.Append('}'); break;
            }
            default: throw new ArgumentException("Unsupported JavaScript value " + value.GetType().Name + ".");
        }
    }

    /// <summary>OrdinaryOwnPropertyKeys: integer indices ascending, then strings in creation order.</summary>
    public static IEnumerable<string> OrderedKeys(JsObject obj)
    {
        var indices = obj.Keys.Where(IsArrayIndex).OrderBy(key => ulong.Parse(key, CultureInfo.InvariantCulture)).ToList();
        return indices.Count == 0 ? obj.Keys : indices.Concat(obj.Keys.Where(key => !IsArrayIndex(key)));
    }

    private static bool IsArrayIndex(string key)
    {
        if (key.Length is 0 or > 10 || key.Length > 1 && key[0] == '0' || !key.All(char.IsAsciiDigit)) return false;
        return ulong.Parse(key, CultureInfo.InvariantCulture) < 4294967295UL;
    }

    /// <summary>QuoteJSONString (well-formed JSON.stringify).</summary>
    internal static void Quote(StringBuilder builder, string text)
    {
        builder.Append('"');
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\b': builder.Append("\\b"); break;
                case '\t': builder.Append("\\t"); break;
                case '\n': builder.Append("\\n"); break;
                case '\f': builder.Append("\\f"); break;
                case '\r': builder.Append("\\r"); break;
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                default:
                    if (c < 0x20) builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { builder.Append(c).Append(text[i + 1]); i++; }
                    else if (char.IsSurrogate(c)) builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else builder.Append(c);
                    break;
            }
        }
        builder.Append('"');
    }

    /// <summary>new Date().toISOString().</summary>
    public static string IsoString(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
