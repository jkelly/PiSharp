// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/utils/json-parse.ts.
// The partial stage ports partial-json 0.1.7 parse() with Allow.ALL, Copyright (c) 2023 Promplate Dev Team (MIT).
using System.Globalization;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.AI;

/// <summary>
/// <c>parseStreamingJson</c> as every Pi provider runs it when a tool call ends: <c>JSON.parse</c>, then <c>JSON.parse(repairJson(text))</c>
/// when the repair changed the text, then partial-json's <c>parse</c> of the text and of its repair, then <c>{}</c>. It never throws for
/// malformed input. The result is any JSON value (an array, string, number, boolean or null passes through as upstream returns it) and is
/// owned as its <c>JSON.stringify</c> text: duplicate names keep the last value at the first position, array-index names come first in
/// ascending order, and numbers are binary64 (non-finite ones become null). A name or string value keeps every code unit, a lone
/// surrogate as its escape (see <see cref="JsonUtf16"/>). One representation limit remains: a value nested deeper than
/// <see cref="JsonData.MaximumDepth"/> levels is a <see cref="JsonException"/>.
/// </summary>
public static class StreamingJson
{
    private const int MaximumDepth = JsonData.MaximumDepth;

    public static JsonData Parse(string? partialJson)
    {
        var text = Stringify(ParseValue(partialJson));
        try { return JsonData.Parse(text); }
        catch (JsonException) { throw TooDeep(); }
    }

    /// <summary>
    /// <c>JSON.parse(text)</c> with the same ownership as <see cref="Parse"/>: any JSON value, duplicate names keep the last value,
    /// lone surrogates of string values are kept. A text JSON.parse rejects is a <see cref="JsonException"/> carrying V8's SyntaxError message.
    /// </summary>
    public static JsonData JsonParse(string text) => JsonParse(text, out _, out _);

    /// <summary><see cref="JsonParse(string)"/>, also returning what the owned value cannot show of top-level object members, for
    /// callers that echo them: the exact (not well-formed) string of a member with a lone surrogate, and the binary64 of a member
    /// whose number is beyond binary64's range (owned as null, as JSON.stringify writes it, though <c>String(value)</c> is
    /// "Infinity" or "-Infinity").</summary>
    public static JsonData JsonParse(string text, out IReadOnlyDictionary<string, string>? loneSurrogateMembers,
        out IReadOnlyDictionary<string, double>? nonFiniteMembers)
    {
        var value = Syntax(text);
        loneSurrogateMembers = null; nonFiniteMembers = null;
        if (value is ObjectValue obj)
        {
            Dictionary<string, string>? members = null; Dictionary<string, double>? numbers = null;
            foreach (var key in obj.Order)
            {
                if (obj.Properties[key] is StringValue member && !ReferenceEquals(ToWellFormed(member.Text), member.Text))
                    (members ??= new(StringComparer.Ordinal))[key] = member.Text;
                else if (obj.Properties[key] is NumberValue number && !double.IsFinite(number.Number))
                    (numbers ??= new(StringComparer.Ordinal))[key] = number.Number;
            }
            loneSurrogateMembers = members; nonFiniteMembers = numbers;
        }
        var owned = Stringify(value);
        try { return JsonData.Parse(owned); }
        catch (JsonException) { throw TooDeep(); }
    }

    /// <summary><c>JSON.stringify(JSON.parse(text))</c> exactly, lone surrogates escaped as JavaScript writes them. A text JSON.parse
    /// rejects is a <see cref="JsonException"/>.</summary>
    public static string JsonReformat(string text) => Stringify(Syntax(text));

    /// <summary>JSON.stringify of a string: only <c>"</c>, <c>\</c>, control characters and lone surrogates are escaped.</summary>
    public static string JsonQuote(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length + 2);
        Quote(builder, text);
        return builder.ToString();
    }

    private static Value Syntax(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try { return JsonParseValue(text); }
        catch (SyntaxError) { throw new JsonException(PiSharp.Contracts.Compatibility.JsJsonSyntax.Describe(text, "Unexpected token in JSON")); }
    }

    /// <summary><c>JSON.stringify(parseStreamingJson(partialJson))</c>, lone surrogates escaped as JavaScript writes them.</summary>
    public static string ParseToJson(string? partialJson) => Stringify(ParseValue(partialJson));

    /// <summary>json-parse.ts <c>repairJson</c>: escapes raw control characters in strings and doubles backslashes before invalid escapes.</summary>
    public static string RepairJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var repaired = new StringBuilder(json.Length);
        var inString = false;
        for (var index = 0; index < json.Length; index++)
        {
            var character = json[index];
            if (!inString)
            {
                repaired.Append(character);
                if (character == '"') inString = true;
                continue;
            }
            if (character == '"') { repaired.Append(character); inString = false; continue; }
            if (character == '\\')
            {
                if (index + 1 >= json.Length) { repaired.Append("\\\\"); continue; }
                var next = json[index + 1];
                if (next == 'u')
                {
                    var digits = JsSlice(json, index + 2, index + 6);
                    if (digits.Length == 4 && digits.All(char.IsAsciiHexDigit)) { repaired.Append("\\u").Append(digits); index += 5; continue; }
                }
                if (next is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't' or 'u') { repaired.Append('\\').Append(next); index += 1; continue; }
                repaired.Append("\\\\");
                continue;
            }
            if (character <= 0x1f)
                repaired.Append(character switch
                {
                    '\b' => "\\b", '\f' => "\\f", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                    _ => "\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture)
                });
            else repaired.Append(character);
        }
        return repaired.ToString();
    }

    private static Value ParseValue(string? partialJson)
    {
        if (string.IsNullOrEmpty(partialJson) || JsTrim(partialJson).Length == 0) return new ObjectValue();
        try { return JsonParseValue(partialJson); }
        catch (SyntaxError)
        {
            var repaired = RepairJson(partialJson);
            if (repaired != partialJson)
                try { return JsonParseValue(repaired); }
                catch (SyntaxError) { /* parseJsonWithRepair rethrows; parseStreamingJson moves on to partial-json. */ }
        }
        try
        {
            var result = PartialParse(partialJson);
            return result is NullValue ? new ObjectValue() : result;
        }
        catch (SyntaxError)
        {
            try
            {
                var repaired = RepairJson(partialJson);
                var result = PartialParse(repaired);
                return result is NullValue ? new ObjectValue() : result;
            }
            catch (SyntaxError) { return new ObjectValue(); }
        }
    }

    // --- JavaScript values -------------------------------------------------------------------------------------------------------------

    private abstract class Value;
    private sealed class NullValue : Value { internal static readonly NullValue Instance = new(); }
    private sealed class BoolValue(bool value) : Value { internal readonly bool Bool = value; }
    private sealed class NumberValue(double value) : Value { internal readonly double Number = value; }
    private sealed class StringValue(string value) : Value { internal readonly string Text = value; }
    private sealed class ArrayValue : Value { internal readonly List<Value> Items = []; }
    private sealed class ObjectValue : Value
    {
        internal readonly List<string> Order = [];
        internal readonly Dictionary<string, Value> Properties = new(StringComparer.Ordinal);
        // null: Object.prototype. A NullValue prototype ends the chain without the __proto__ accessor.
        internal Value? Prototype;

        // CreateDataProperty (JSON.parse): an own property, even for "__proto__".
        internal void Define(string key, Value value)
        {
            if (!Properties.ContainsKey(key)) Order.Add(key);
            Properties[key] = value;
        }

        // obj[key] = value (partial-json): "__proto__" reaches Object.prototype's accessor unless an own or inherited data
        // property named "__proto__" exists first, or the chain was cut by a null prototype.
        internal void Assign(string key, Value value)
        {
            if (key != "__proto__" || Properties.ContainsKey(key)) { Define(key, value); return; }
            for (var link = Prototype; ; )
            {
                switch (link)
                {
                    case null:
                        if (value is ObjectValue or ArrayValue or NullValue) Prototype = value;
                        return;
                    case NullValue: Define(key, value); return;
                    case ObjectValue parent:
                        if (parent.Properties.ContainsKey(key)) { Define(key, value); return; }
                        link = parent.Prototype; continue;
                    default: link = null; continue; // Array.prototype -> Object.prototype
                }
            }
        }
    }

    private sealed class SyntaxError : Exception;
    private static SyntaxError Syntax() => new();
    private static JsonException TooDeep() => new($"Tool-call arguments nest deeper than {MaximumDepth} levels.");

    // --- JSON.parse ------------------------------------------------------------------------------------------------------------------

    private static Value JsonParseValue(string text)
    {
        var reader = new JsonReader(text);
        reader.White();
        var result = reader.Read(0);
        reader.White();
        if (!reader.AtEnd) throw Syntax();
        return result;
    }

    private sealed class JsonReader(string text)
    {
        private int position;
        internal bool AtEnd => position >= text.Length;

        internal void White() { while (position < text.Length && text[position] is ' ' or '\t' or '\n' or '\r') position++; }

        internal Value Read(int depth)
        {
            if (position >= text.Length) throw Syntax();
            var character = text[position];
            switch (character)
            {
                case '{':
                {
                    if (depth >= MaximumDepth) throw TooDeep();
                    position++; White(); var result = new ObjectValue();
                    if (Take('}')) return result;
                    while (true)
                    {
                        if (position >= text.Length || text[position] != '"') throw Syntax();
                        var name = ReadString(); White();
                        if (!Take(':')) throw Syntax();
                        White(); result.Define(name, Read(depth + 1)); White();
                        if (Take('}')) return result;
                        if (!Take(',')) throw Syntax();
                        White();
                    }
                }
                case '[':
                {
                    if (depth >= MaximumDepth) throw TooDeep();
                    position++; White(); var result = new ArrayValue();
                    if (Take(']')) return result;
                    while (true)
                    {
                        result.Items.Add(Read(depth + 1)); White();
                        if (Take(']')) return result;
                        if (!Take(',')) throw Syntax();
                        White();
                    }
                }
                case '"': return new StringValue(ReadString());
                case 't': Literal("true"); return new BoolValue(true);
                case 'f': Literal("false"); return new BoolValue(false);
                case 'n': Literal("null"); return NullValue.Instance;
                default:
                    if (character == '-' || char.IsAsciiDigit(character)) return ReadNumber();
                    throw Syntax();
            }
        }

        private void Literal(string literal)
        {
            if (text.Length - position < literal.Length || string.CompareOrdinal(text, position, literal, 0, literal.Length) != 0) throw Syntax();
            position += literal.Length;
        }

        private NumberValue ReadNumber()
        {
            var start = position;
            Take('-');
            if (!Take('0'))
            {
                if (position >= text.Length || text[position] is < '1' or > '9') throw Syntax();
                while (Digit()) position++;
            }
            if (Take('.')) { if (!Digit()) throw Syntax(); while (Digit()) position++; }
            if (position < text.Length && text[position] is 'e' or 'E')
            {
                position++;
                if (position < text.Length && text[position] is '+' or '-') position++;
                if (!Digit()) throw Syntax();
                while (Digit()) position++;
            }
            return new(double.Parse(text.AsSpan(start, position - start), NumberStyles.Float, CultureInfo.InvariantCulture));
        }

        private string ReadString()
        {
            position++;
            var value = new StringBuilder();
            while (position < text.Length)
            {
                var character = text[position++];
                if (character == '"') return value.ToString();
                if (character < 0x20) throw Syntax();
                if (character == '\\')
                {
                    if (position >= text.Length) throw Syntax();
                    character = text[position++] switch
                    {
                        '"' => '"', '\\' => '\\', '/' => '/', 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t',
                        'u' => Unicode(),
                        _ => throw Syntax()
                    };
                }
                value.Append(character);
            }
            throw Syntax();
        }

        private char Unicode()
        {
            if (text.Length - position < 4) throw Syntax();
            var value = 0;
            for (var index = 0; index < 4; index++)
            {
                var character = text[position++];
                if (!char.IsAsciiHexDigit(character)) throw Syntax();
                value = value * 16 + Convert.ToInt32(character.ToString(), 16);
            }
            return (char)value;
        }

        private bool Digit() => position < text.Length && char.IsAsciiDigit(text[position]);
        private bool Take(char character)
        {
            if (position >= text.Length || text[position] != character) return false;
            position++; return true;
        }
    }

    // --- partial-json 0.1.7 parse(jsonString, Allow.ALL) --------------------------------------------------------------------------------
    // Every PartialJSON/MalformedJSON/SyntaxError is a SyntaxError here: the containers catch all of them alike, and so does
    // parseStreamingJson around the whole call.

    private static Value PartialParse(string jsonString)
    {
        var trimmed = JsTrim(jsonString);
        if (trimmed.Length == 0) throw Syntax();
        return new PartialReader(trimmed).ParseAny(0);
    }

    private sealed class PartialReader(string s)
    {
        private readonly int length = s.Length;
        private int index;

        private char? At(int at) => at >= 0 && at < length ? s[at] : null;

        internal Value ParseAny(int depth)
        {
            SkipBlank();
            if (index >= length) throw Syntax();
            if (s[index] == '"') return ParseStr();
            if (s[index] == '{') return ParseObj(depth);
            if (s[index] == '[') return ParseArr(depth);
            if (Literal("null", 0)) { index += 4; return NullValue.Instance; }
            if (Literal("true", 0)) { index += 4; return new BoolValue(true); }
            if (Literal("false", 0)) { index += 5; return new BoolValue(false); }
            if (Literal("Infinity", 0)) { index += 8; return new NumberValue(double.PositiveInfinity); }
            if (Literal("-Infinity", 1)) { index += 9; return new NumberValue(double.NegativeInfinity); }
            if (Literal("NaN", 0)) { index += 3; return new NumberValue(double.NaN); }
            return ParseNum();
        }

        // jsonString.substring(index, index + n) === literal || (length - index < n && literal.startsWith(jsonString.substring(index)))
        private bool Literal(string literal, int minimumRemaining)
        {
            var remaining = length - index;
            if (remaining >= literal.Length) return string.CompareOrdinal(s, index, literal, 0, literal.Length) == 0;
            return remaining > minimumRemaining && literal.StartsWith(s[index..], StringComparison.Ordinal);
        }

        private Value ParseStr()
        {
            var start = index;
            var escape = false;
            index++;
            while (index < length && (s[index] != '"' || (escape && s[index - 1] == '\\')))
            {
                escape = s[index] == '\\' ? !escape : false;
                index++;
            }
            if (At(index) == '"')
            {
                index++;
                return JsonParseValue(JsSubstring(s, start, index - (escape ? 1 : 0)));
            }
            try { return JsonParseValue(JsSubstring(s, start, index - (escape ? 1 : 0)) + "\""); }
            catch (SyntaxError) { return JsonParseValue(JsSubstring(s, start, s.LastIndexOf('\\')) + "\""); }
        }

        private ObjectValue ParseObj(int depth)
        {
            if (depth >= MaximumDepth) throw TooDeep();
            index++;
            SkipBlank();
            var obj = new ObjectValue();
            try
            {
                while (At(index) != '}')
                {
                    SkipBlank();
                    if (index >= length) return obj;
                    var key = PropertyKey(ParseStr());
                    SkipBlank();
                    index++;
                    try { obj.Assign(key, ParseAny(depth + 1)); }
                    catch (SyntaxError) { return obj; }
                    SkipBlank();
                    if (At(index) == ',') index++;
                }
            }
            catch (SyntaxError) { return obj; }
            index++;
            return obj;
        }

        private ArrayValue ParseArr(int depth)
        {
            if (depth >= MaximumDepth) throw TooDeep();
            index++;
            var arr = new ArrayValue();
            try
            {
                while (At(index) != ']')
                {
                    var before = index;
                    arr.Items.Add(ParseAny(depth + 1));
                    SkipBlank();
                    if (At(index) == ',') index++;
                    // partial-json would spin here forever; no input reaches it, but a native loop must not.
                    if (index == before) return arr;
                }
            }
            catch (SyntaxError) { return arr; }
            index++;
            return arr;
        }

        private Value ParseNum()
        {
            if (index == 0)
            {
                if (s == "-") throw Syntax();
                try { return JsonParseValue(s); }
                catch (SyntaxError)
                {
                    try { return JsonParseValue(JsSubstring(s, 0, s.LastIndexOf('e'))); }
                    catch (SyntaxError) { /* throwMalformedError */ }
                    throw Syntax();
                }
            }
            var start = index;
            if (At(index) == '-') index++;
            while (index < length && ",]}".IndexOf(s[index]) < 0) index++;
            try { return JsonParseValue(JsSubstring(s, start, index)); }
            catch (SyntaxError)
            {
                if (JsSubstring(s, start, index) == "-") throw Syntax();
                return JsonParseValue(JsSubstring(s, start, s.LastIndexOf('e')));
            }
        }

        private void SkipBlank()
        {
            while (index < length && s[index] is ' ' or '\n' or '\r' or '\t') index++;
        }
    }

    // ToPropertyKey of a JSON.parse result (always a string for the texts parseStr can accept).
    private static string PropertyKey(Value value) => value switch
    {
        StringValue text => text.Text,
        NullValue => "null",
        BoolValue flag => flag.Bool ? "true" : "false",
        NumberValue number => double.IsNaN(number.Number) ? "NaN" : double.IsPositiveInfinity(number.Number) ? "Infinity" :
            double.IsNegativeInfinity(number.Number) ? "-Infinity" : NumberText(number.Number),
        ArrayValue array => string.Join(",", array.Items.Select(item => item is NullValue ? "" : PropertyKey(item))),
        _ => "[object Object]"
    };

    // --- JSON.stringify -------------------------------------------------------------------------------------------------------------

    // Names and string values keep every code unit: a lone surrogate is written as its JSON.stringify escape, which a JsonData carries
    // (see JsonUtf16).
    private static string Stringify(Value value)
    {
        var builder = new StringBuilder();
        Write(builder, value);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, Value value)
    {
        switch (value)
        {
            case NullValue: builder.Append("null"); break;
            case BoolValue flag: builder.Append(flag.Bool ? "true" : "false"); break;
            case NumberValue number: builder.Append(NumberText(number.Number)); break;
            case StringValue text: Quote(builder, text.Text); break;
            case ArrayValue array:
                builder.Append('[');
                for (var index = 0; index < array.Items.Count; index++)
                {
                    if (index > 0) builder.Append(',');
                    Write(builder, array.Items[index]);
                }
                builder.Append(']');
                break;
            case ObjectValue obj:
                var members = obj.Order.Select((key, position) => (key, position, arrayIndex: ArrayIndex(key)))
                    .OrderBy(item => item.arrayIndex is null ? 1 : 0).ThenBy(item => item.arrayIndex ?? 0).ThenBy(item => item.position)
                    .Select(item => item.key).ToList();
                builder.Append('{');
                for (var index = 0; index < members.Count; index++)
                {
                    if (index > 0) builder.Append(',');
                    Quote(builder, members[index]); builder.Append(':'); Write(builder, obj.Properties[members[index]]);
                }
                builder.Append('}');
                break;
        }
    }

    private static string ToWellFormed(string text)
    {
        StringBuilder? builder = null;
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])) { builder?.Append(text, index, 2); index++; continue; }
            if (char.IsSurrogate(text[index]))
            {
                builder ??= new StringBuilder(text, 0, index, text.Length);
                builder.Append((char)0xFFFD);
                continue;
            }
            builder?.Append(text[index]);
        }
        return builder?.ToString() ?? text;
    }

    private static uint? ArrayIndex(string name)
    {
        if (name.Length is 0 or > 10 || name.Length > 1 && name[0] == '0') return null;
        ulong value = 0;
        foreach (var character in name)
        {
            if (!char.IsAsciiDigit(character)) return null;
            value = value * 10 + (uint)(character - '0');
        }
        return value < uint.MaxValue ? (uint)value : null;
    }

    private static void Quote(StringBuilder builder, string text)
    {
        builder.Append('"');
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            switch (character)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                        builder.Append(character).Append(text[++index]);
                    else if (character < 0x20 || char.IsSurrogate(character))
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else builder.Append(character);
                    break;
            }
        }
        builder.Append('"');
    }

    /// <summary>JSON.stringify of a binary64: Number::toString for finite values, <c>null</c> otherwise.</summary>
    private static string NumberText(double value) => JsonNumber.Text(value);

    // --- String.prototype helpers ----------------------------------------------------------------------------------------------------

    private static bool IsJsWhiteSpace(char character) => character is '\t' or '\n' or '\v' or '\f' or '\r' or ' ' or '\u00a0' or '\u1680'
        or (>= '\u2000' and <= '\u200a') or '\u2028' or '\u2029' or '\u202f' or '\u205f' or '\u3000' or '\ufeff';

    private static string JsTrim(string text)
    {
        var start = 0; var end = text.Length;
        while (start < end && IsJsWhiteSpace(text[start])) start++;
        while (end > start && IsJsWhiteSpace(text[end - 1])) end--;
        return text[start..end];
    }

    // String.prototype.substring: clamps both ends into [0, length] and swaps them when reversed.
    private static string JsSubstring(string text, int start, int end)
    {
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, 0, text.Length);
        return start <= end ? text[start..end] : text[end..start];
    }

    // String.prototype.slice with non-negative bounds.
    private static string JsSlice(string text, int start, int end)
    {
        start = Math.Min(start, text.Length);
        end = Math.Min(end, text.Length);
        return start < end ? text[start..end] : "";
    }
}
