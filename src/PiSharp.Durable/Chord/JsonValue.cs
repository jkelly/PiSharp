// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/chord/src/json.ts (JsonValue, which v1.2.0 moved here from types.ts).
// Chord's values are plain JavaScript JSON; here they are immutable trees whose reference identity is the unit of structural sharing, so
// copyJson and isJsonValue are unnecessary, and JsonRepresentation is a compile-time mapping of TypeScript types with no C# counterpart.
using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.Durable.Chord;

/// <summary>The kinds of a strict-JSON value.</summary>
public enum JsonValueKind { Null, Boolean, Number, String, Array, Object }

/// <summary>
/// An immutable strict-JSON value with JavaScript semantics: numbers are finite binary64 values (<c>-0</c> serializes as <c>0</c>),
/// strings are any UTF-16 code units (a lone surrogate included), and object keys enumerate as JavaScript orders them (integer-index
/// keys ascending, then the other keys in insertion order). Equal values compare equal by <see cref="DeepEquals"/>; a value that was not
/// changed keeps its reference, which is how revisions share structure.
/// </summary>
public abstract class JsonValue
{
    private protected JsonValue() { }

    /// <summary>This value's kind.</summary>
    public abstract JsonValueKind Kind { get; }

    /// <summary><c>null</c>.</summary>
    public static JsonNullValue Null => JsonNullValue.Instance;
    /// <summary>A boolean.</summary>
    public static JsonBooleanValue Boolean(bool value) => value ? JsonBooleanValue.True : JsonBooleanValue.False;
    /// <summary>A finite number.</summary>
    public static JsonNumberValue Number(double value) => new(value);
    /// <summary>A string.</summary>
    public static JsonStringValue String(string value) => new(value);
    /// <summary>An array of the given items.</summary>
    public static JsonArrayValue Array(params IEnumerable<JsonValue> items) => new([.. items]);
    /// <summary>An object of the given properties, in order (a repeated key keeps its first position and takes the last value).</summary>
    public static JsonObjectValue Object(params IEnumerable<KeyValuePair<string, JsonValue>> properties) => JsonObjectValue.From(properties);

    /// <summary>Values convert implicitly from the corresponding .NET scalars.</summary>
    public static implicit operator JsonValue(bool value) => Boolean(value);
    /// <inheritdoc cref="op_Implicit(bool)"/>
    public static implicit operator JsonValue(double value) => Number(value);
    /// <inheritdoc cref="op_Implicit(bool)"/>
    public static implicit operator JsonValue(string value) => String(value);

    /// <summary>Structural equality as JavaScript deep equality sees JSON: objects compare by key set and values, not key order.</summary>
    public static bool DeepEquals(JsonValue? left, JsonValue? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Kind != right.Kind) return false;
        switch (left)
        {
            case JsonBooleanValue boolean: return boolean.Value == ((JsonBooleanValue)right).Value;
            case JsonNumberValue number: return number.Value == ((JsonNumberValue)right).Value;
            case JsonStringValue text: return string.Equals(text.Value, ((JsonStringValue)right).Value, StringComparison.Ordinal);
            case JsonArrayValue array:
            {
                var other = (JsonArrayValue)right;
                if (array.Count != other.Count) return false;
                for (var index = 0; index < array.Count; index++) if (!DeepEquals(array[index], other[index])) return false;
                return true;
            }
            case JsonObjectValue obj:
            {
                var other = (JsonObjectValue)right;
                if (obj.Count != other.Count) return false;
                foreach (var (key, value) in obj)
                    if (!other.TryGetValue(key, out var otherValue) || !DeepEquals(value, otherValue)) return false;
                return true;
            }
            default: return true; // null
        }
    }

    /// <summary><c>JSON.stringify(value)</c>: compact, with JavaScript number and string formatting.</summary>
    public string ToJson()
    {
        var builder = new StringBuilder();
        Write(builder);
        return builder.ToString();
    }

    /// <summary>Appends <c>JSON.stringify(value)</c>.</summary>
    public void Write(StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        switch (this)
        {
            case JsonNullValue: builder.Append("null"); break;
            case JsonBooleanValue boolean: builder.Append(boolean.Value ? "true" : "false"); break;
            case JsonNumberValue number: builder.Append(PiSharp.Contracts.JsonNumber.Text(number.Value)); break;
            case JsonStringValue text: JsonUtf16.Quote(builder, text.Value); break;
            case JsonArrayValue array:
                builder.Append('[');
                for (var index = 0; index < array.Count; index++) { if (index > 0) builder.Append(','); array[index].Write(builder); }
                builder.Append(']');
                break;
            case JsonObjectValue obj:
                builder.Append('{');
                var first = true;
                foreach (var (key, value) in obj)
                {
                    if (!first) builder.Append(',');
                    first = false;
                    JsonUtf16.Quote(builder, key); builder.Append(':'); value.Write(builder);
                }
                builder.Append('}');
                break;
        }
    }

    /// <inheritdoc/>
    public override string ToString() => ToJson();

    /// <summary><c>JSON.parse(text)</c>: strict JSON, an escaped lone surrogate kept as that code unit, a repeated key keeping its first
    /// position and the last value. Throws <see cref="FormatException"/> with V8's SyntaxError message for invalid text.</summary>
    public static JsonValue Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (PiSharp.Contracts.Compatibility.JsJsonSyntax.Error(text) is { } error) throw new FormatException(error);
        var parser = new Parser(text);
        var value = parser.ParseValue();
        parser.SkipWhitespace();
        return value;
    }

    /// <summary>The value of a System.Text.Json element (lone surrogates kept, as <see cref="JsonUtf16"/> reads them).</summary>
    public static JsonValue FromElement(JsonElement element) => element.ValueKind switch
    {
        System.Text.Json.JsonValueKind.Null => Null,
        System.Text.Json.JsonValueKind.True => JsonBooleanValue.True,
        System.Text.Json.JsonValueKind.False => JsonBooleanValue.False,
        System.Text.Json.JsonValueKind.Number => Number(element.GetDouble()),
        System.Text.Json.JsonValueKind.String => String(JsonUtf16.GetString(element)),
        System.Text.Json.JsonValueKind.Array => new JsonArrayValue([.. element.EnumerateArray().Select(FromElement)]),
        System.Text.Json.JsonValueKind.Object => JsonObjectValue.From(element.EnumerateObject()
            .Select(property => KeyValuePair.Create(JsonUtf16.GetName(property), FromElement(property.Value)))),
        _ => throw new ArgumentException("Value is not strict JSON.", nameof(element))
    };

    /// <summary>The value of a <see cref="JsonData"/>.</summary>
    public static JsonValue FromData(JsonData data) => FromElement(data.Value);

    /// <summary>This value as <see cref="JsonData"/> (parsed from its <c>JSON.stringify</c> text).</summary>
    public JsonData ToData() => JsonData.Parse(ToJson());

    private sealed class Parser(string text)
    {
        private int position;

        public void SkipWhitespace()
        {
            while (position < text.Length && text[position] is ' ' or '\t' or '\n' or '\r') position++;
        }

        public JsonValue ParseValue()
        {
            SkipWhitespace();
            switch (text[position])
            {
                case '{': return ParseObject();
                case '[': return ParseArray();
                case '"': return String(ParseString());
                case 't': position += 4; return JsonBooleanValue.True;
                case 'f': position += 5; return JsonBooleanValue.False;
                case 'n': position += 4; return Null;
                default: return ParseNumber();
            }
        }

        private JsonObjectValue ParseObject()
        {
            position++;
            var builder = JsonObjectValue.CreateBuilder();
            SkipWhitespace();
            if (text[position] == '}') { position++; return builder.ToImmutable(); }
            while (true)
            {
                SkipWhitespace();
                var key = ParseString();
                SkipWhitespace(); position++; // ':'
                builder[key] = ParseValue();
                SkipWhitespace();
                if (text[position++] == '}') return builder.ToImmutable();
            }
        }

        private JsonArrayValue ParseArray()
        {
            position++;
            var items = ImmutableArray.CreateBuilder<JsonValue>();
            SkipWhitespace();
            if (text[position] == ']') { position++; return new JsonArrayValue(items.ToImmutable()); }
            while (true)
            {
                items.Add(ParseValue());
                SkipWhitespace();
                if (text[position++] == ']') return new JsonArrayValue(items.ToImmutable());
            }
        }

        private string ParseString()
        {
            position++; // opening quote
            var builder = new StringBuilder();
            while (true)
            {
                var character = text[position++];
                if (character == '"') return builder.ToString();
                if (character != '\\') { builder.Append(character); continue; }
                var escape = text[position++];
                builder.Append(escape switch
                {
                    '"' => '"', '\\' => '\\', '/' => '/', 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t',
                    _ => (char)int.Parse(text.AsSpan(position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                });
                if (escape == 'u') position += 4;
            }
        }

        private JsonNumberValue ParseNumber()
        {
            var start = position;
            while (position < text.Length && text[position] is '-' or '+' or '.' or 'e' or 'E' or (>= '0' and <= '9')) position++;
            return Number(double.Parse(text.AsSpan(start, position - start), NumberStyles.Float, CultureInfo.InvariantCulture));
        }
    }
}

/// <summary><c>null</c>.</summary>
public sealed class JsonNullValue : JsonValue
{
    internal static readonly JsonNullValue Instance = new();
    private JsonNullValue() { }
    /// <inheritdoc/>
    public override JsonValueKind Kind => JsonValueKind.Null;
}

/// <summary>A boolean.</summary>
public sealed class JsonBooleanValue : JsonValue
{
    internal static readonly JsonBooleanValue True = new(true), False = new(false);
    private JsonBooleanValue(bool value) { Value = value; }
    /// <summary>The boolean.</summary>
    public bool Value { get; }
    /// <inheritdoc/>
    public override JsonValueKind Kind => JsonValueKind.Boolean;
}

/// <summary>A finite binary64 number.</summary>
public sealed class JsonNumberValue : JsonValue
{
    internal JsonNumberValue(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentException("Value contains a non-finite number and is not strict JSON", nameof(value));
        Value = value;
    }
    /// <summary>The number.</summary>
    public double Value { get; }
    /// <inheritdoc/>
    public override JsonValueKind Kind => JsonValueKind.Number;
}

/// <summary>A string of UTF-16 code units.</summary>
public sealed class JsonStringValue : JsonValue
{
    internal JsonStringValue(string value) { Value = value ?? throw new ArgumentNullException(nameof(value)); }
    /// <summary>The string.</summary>
    public string Value { get; }
    /// <inheritdoc/>
    public override JsonValueKind Kind => JsonValueKind.String;
}

/// <summary>An immutable array.</summary>
public sealed class JsonArrayValue : JsonValue, IReadOnlyList<JsonValue>
{
    /// <summary>The empty array.</summary>
    public static readonly JsonArrayValue Empty = new([]);
    internal JsonArrayValue(ImmutableArray<JsonValue> items)
    {
        if (items.IsDefault) throw new ArgumentNullException(nameof(items));
        foreach (var item in items) ArgumentNullException.ThrowIfNull(item);
        Items = items;
    }
    /// <summary>The items.</summary>
    public ImmutableArray<JsonValue> Items { get; }
    /// <inheritdoc/>
    public override JsonValueKind Kind => JsonValueKind.Array;
    /// <inheritdoc/>
    public int Count => Items.Length;
    /// <inheritdoc/>
    public JsonValue this[int index] => Items[index];
    /// <inheritdoc/>
    public IEnumerator<JsonValue> GetEnumerator() => ((IEnumerable<JsonValue>)Items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>An immutable object whose keys enumerate as JavaScript orders them: integer-index keys ascending, then insertion order.</summary>
public sealed class JsonObjectValue : JsonValue, IReadOnlyDictionary<string, JsonValue>
{
    /// <summary>The empty object.</summary>
    public static readonly JsonObjectValue Empty = new(ImmutableDictionary<string, JsonValue>.Empty.WithComparers(StringComparer.Ordinal), []);

    private readonly ImmutableDictionary<string, JsonValue> values;
    private readonly ImmutableList<string> named; // non-index keys, in insertion order
    private string[]? order;

    private JsonObjectValue(ImmutableDictionary<string, JsonValue> values, ImmutableList<string> named)
    {
        this.values = values; this.named = named;
    }

    /// <summary>An object of the given properties (a repeated key keeps its first position and takes the last value).</summary>
    public static JsonObjectValue From(IEnumerable<KeyValuePair<string, JsonValue>> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var builder = CreateBuilder();
        foreach (var (key, value) in properties) builder[key] = value;
        return builder.ToImmutable();
    }

    /// <summary>A builder starting empty.</summary>
    public static Builder CreateBuilder() => new(Empty);

    /// <summary>A builder starting from this object.</summary>
    public Builder ToBuilder() => new(this);

    /// <summary>Whether <paramref name="key"/> is an array index as JavaScript defines it: a canonical integer below 2^32 - 1.</summary>
    public static bool IsArrayIndex(string key)
    {
        if (key.Length == 0 || key.Length > 10 || (key.Length > 1 && key[0] == '0')) return false;
        foreach (var character in key) if (character is < '0' or > '9') return false;
        return ulong.Parse(key, CultureInfo.InvariantCulture) < uint.MaxValue;
    }

    /// <summary>The keys in JavaScript enumeration order.</summary>
    public IReadOnlyList<string> Keys => order ??= Order();
    IEnumerable<string> IReadOnlyDictionary<string, JsonValue>.Keys => Keys;
    /// <inheritdoc/>
    public IEnumerable<JsonValue> Values => Keys.Select(key => values[key]);
    /// <inheritdoc/>
    public override JsonValueKind Kind => JsonValueKind.Object;
    /// <inheritdoc/>
    public int Count => values.Count;
    /// <inheritdoc/>
    public JsonValue this[string key] => values[key];
    /// <inheritdoc/>
    public bool ContainsKey(string key) => values.ContainsKey(key);
    /// <inheritdoc/>
    public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out JsonValue value) => values.TryGetValue(key, out value);
    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<string, JsonValue>> GetEnumerator() { foreach (var key in Keys) yield return KeyValuePair.Create(key, values[key]); }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>This object with <paramref name="key"/> set (an existing key keeps its position).</summary>
    public JsonObjectValue SetItem(string key, JsonValue value)
    {
        ArgumentNullException.ThrowIfNull(key); ArgumentNullException.ThrowIfNull(value);
        if (values.TryGetValue(key, out var existing) && ReferenceEquals(existing, value)) return this;
        var added = !values.ContainsKey(key) && !IsArrayIndex(key);
        return new(values.SetItem(key, value), added ? named.Add(key) : named);
    }

    /// <summary>This object without <paramref name="key"/>.</summary>
    public JsonObjectValue Remove(string key)
    {
        if (!values.ContainsKey(key)) return this;
        return new(values.Remove(key), IsArrayIndex(key) ? named : named.Remove(key, StringComparer.Ordinal));
    }

    private string[] Order()
    {
        var indices = values.Keys.Where(IsArrayIndex).Select(key => (Key: key, Index: uint.Parse(key, CultureInfo.InvariantCulture)))
            .OrderBy(item => item.Index).Select(item => item.Key);
        return [.. indices, .. named];
    }

    /// <summary>A mutable builder that keeps JavaScript key order.</summary>
    public sealed class Builder
    {
        private readonly ImmutableDictionary<string, JsonValue>.Builder values;
        private readonly ImmutableList<string>.Builder named;

        internal Builder(JsonObjectValue start)
        {
            values = start.values.ToBuilder(); named = start.named.ToBuilder();
        }

        /// <summary>The number of keys.</summary>
        public int Count => values.Count;

        /// <summary>Gets or sets a key; setting a new key appends it, setting an existing key keeps its position.</summary>
        public JsonValue this[string key]
        {
            get => values[key];
            set
            {
                ArgumentNullException.ThrowIfNull(key); ArgumentNullException.ThrowIfNull(value);
                if (!values.ContainsKey(key) && !IsArrayIndex(key)) named.Add(key);
                values[key] = value;
            }
        }

        /// <summary>Whether the key is present.</summary>
        public bool ContainsKey(string key) => values.ContainsKey(key);
        /// <summary>Reads a key.</summary>
        public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out JsonValue value) => values.TryGetValue(key, out value);

        /// <summary>Removes a key.</summary>
        public bool Remove(string key)
        {
            if (!values.Remove(key)) return false;
            if (!IsArrayIndex(key)) named.Remove(key);
            return true;
        }

        /// <summary>The immutable object.</summary>
        public JsonObjectValue ToImmutable() => new(values.ToImmutable(), named.ToImmutable());
    }
}
