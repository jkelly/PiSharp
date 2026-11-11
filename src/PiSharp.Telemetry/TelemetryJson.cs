// PiSharp-only: the JSON forms of the pi-telemetry values, as JavaScript's JSON.stringify writes them (renderTrace's attribute text,
// carriers, snapshots and serializable schemas). Number and string formatting follow PiSharp.Contracts/Schema/JsValues.cs (Js).
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiSharp.Telemetry;

/// <summary>Number.prototype.toString() and JSON.stringify of strings, for text that must match upstream's.</summary>
internal static class JsText
{
    /// <summary>String(number): the shortest round-trip digits, in JavaScript's fixed or exponent layout.</summary>
    public static string Number(double d)
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
            s = (k == 1 ? digits : digits[..1] + "." + digits[1..]) + "e" + (ex < 0 ? "-" : "+") + Math.Abs(ex).ToString(CultureInfo.InvariantCulture);
        }
        return negative ? "-" + s : s;
    }

    /// <summary>JSON.stringify(number): non-finite numbers become null.</summary>
    public static string JsonNumber(double d) => double.IsFinite(d) ? Number(d) : "null";

    /// <summary>JSON.stringify(string), lone surrogates escaped as well-formed JSON.stringify does.</summary>
    public static void Quote(StringBuilder sb, string s)
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
                    if (c < 0x20 || char.IsLowSurrogate(c) || char.IsHighSurrogate(c) && (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])))
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else if (char.IsHighSurrogate(c)) { sb.Append(c).Append(s[i + 1]); i++; }
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    public static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        Quote(sb, s);
        return sb.ToString();
    }

    /// <summary>JSON.stringify of an attribute value.</summary>
    public static void Write(StringBuilder sb, AttributeValue value)
    {
        switch (value.Type)
        {
            case TelemetryAttributeType.String: Quote(sb, value.GetString()); break;
            case TelemetryAttributeType.Number: sb.Append(JsonNumber(value.GetNumber())); break;
            case TelemetryAttributeType.Boolean: sb.Append(value.GetBoolean() ? "true" : "false"); break;
            case TelemetryAttributeType.StringArray: Join(sb, value.GetStrings(), static (b, item) => Quote(b, item)); break;
            case TelemetryAttributeType.NumberArray: Join(sb, value.GetNumbers(), static (b, item) => b.Append(JsonNumber(item))); break;
            case TelemetryAttributeType.StringNumberArray: Join(sb, value.GetStringNumbers(), Write); break;
            default: Join(sb, value.GetBooleans(), static (b, item) => b.Append(item ? "true" : "false")); break;
        }
    }

    private static void Join<T>(StringBuilder sb, ImmutableArray<T> items, Action<StringBuilder, T> write)
    {
        sb.Append('[');
        for (var i = 0; i < items.Length; i++)
        {
            if (i > 0) sb.Append(',');
            write(sb, items[i]);
        }
        sb.Append(']');
    }

    /// <summary>A number written with the digits JSON.stringify would use.</summary>
    public static void WriteNumber(Utf8JsonWriter writer, double value)
    {
        if (double.IsFinite(value)) writer.WriteRawValue(Number(value), skipInputValidation: true);
        else writer.WriteNullValue();
    }
}

/// <summary>Writes attribute values as JSON primitives and arrays; reads them back by JSON kind (an empty array reads as string[]).</summary>
internal sealed class AttributeValueJsonConverter : JsonConverter<AttributeValue>
{
    public override AttributeValue? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null: return null;
            case JsonTokenType.String: return reader.GetString()!;
            case JsonTokenType.Number: return reader.GetDouble();
            case JsonTokenType.True: return true;
            case JsonTokenType.False: return false;
            case JsonTokenType.StartArray:
                var strings = ImmutableArray.CreateBuilder<string>();
                var numbers = ImmutableArray.CreateBuilder<double>();
                var booleans = ImmutableArray.CreateBuilder<bool>();
                var mixed = ImmutableArray.CreateBuilder<AttributeValue>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    switch (reader.TokenType)
                    {
                        case JsonTokenType.String:
                            var text = reader.GetString()!; strings.Add(text); mixed.Add(text); break;
                        case JsonTokenType.Number:
                            var number = reader.GetDouble(); numbers.Add(number); mixed.Add(number); break;
                        case JsonTokenType.True or JsonTokenType.False: booleans.Add(reader.GetBoolean()); break;
                        default: throw new JsonException("Attribute arrays hold strings, numbers or booleans.");
                    }
                }
                var kinds = (strings.Count > 0 ? 1 : 0) + (numbers.Count > 0 ? 1 : 0) + (booleans.Count > 0 ? 1 : 0);
                if (kinds > 1)
                {
                    if (booleans.Count != 0) throw new JsonException("Boolean attribute arrays hold one element type.");
                    return AttributeValue.FromStringNumberArray(mixed);
                }
                return numbers.Count > 0 ? numbers.ToImmutable() : booleans.Count > 0 ? booleans.ToImmutable() : strings.ToImmutable();
            default: throw new JsonException("Attribute values are strings, numbers, booleans or arrays of one of them.");
        }
    }

    public override void Write(Utf8JsonWriter writer, AttributeValue value, JsonSerializerOptions options)
    {
        switch (value.Type)
        {
            case TelemetryAttributeType.String: writer.WriteStringValue(value.GetString()); break;
            case TelemetryAttributeType.Number: JsText.WriteNumber(writer, value.GetNumber()); break;
            case TelemetryAttributeType.Boolean: writer.WriteBooleanValue(value.GetBoolean()); break;
            default:
                writer.WriteStartArray();
                if (value.Type == TelemetryAttributeType.StringArray) foreach (var item in value.GetStrings()) writer.WriteStringValue(item);
                else if (value.Type == TelemetryAttributeType.NumberArray) foreach (var item in value.GetNumbers()) JsText.WriteNumber(writer, item);
                else if (value.Type == TelemetryAttributeType.StringNumberArray) foreach (var item in value.GetStringNumbers()) Write(writer, item, options);
                else foreach (var item in value.GetBooleans()) writer.WriteBooleanValue(item);
                writer.WriteEndArray();
                break;
        }
    }
}

/// <summary>An object of attribute values in insertion order; undefined (null) values are left out, as JSON.stringify leaves them out.</summary>
internal sealed class SpanAttributesJsonConverter : JsonConverter<SpanAttributes>
{
    private static readonly ConditionalWeakTable<JsonSerializerOptions, JsonConverter<AttributeValue>> Converters = new();
    // AttributeValue has one known built-in converter. Resolve explicit caller
    // overrides (including factories) without the reflection resolver fallback
    // in JsonSerializerOptions.GetConverter(Type).
    private static JsonConverter<AttributeValue> Values(JsonSerializerOptions options) => options.IsReadOnly
        ? Converters.GetValue(options, ResolveValues) : ResolveValues(options);

    private static JsonConverter<AttributeValue> ResolveValues(JsonSerializerOptions options)
    {
        foreach (var candidate in options.Converters)
        {
            if (!candidate.CanConvert(typeof(AttributeValue))) continue;
            var converter = candidate is JsonConverterFactory factory
                ? factory.CreateConverter(typeof(AttributeValue), options) : candidate;
            return converter as JsonConverter<AttributeValue>
                ?? throw new InvalidOperationException("The attribute-value converter must handle AttributeValue.");
        }
        if (options.TypeInfoResolver is not null && options.TryGetTypeInfo(typeof(AttributeValue), out var metadata))
            return (JsonConverter<AttributeValue>)metadata.Converter;
        return new AttributeValueJsonConverter();
    }

    public override SpanAttributes? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Attributes are a JSON object.");
        var attributes = new SpanAttributes();
        var values = Values(options);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString()!;
            reader.Read();
            attributes[name] = values.Read(ref reader, typeof(AttributeValue), options);
        }
        return attributes;
    }

    public override void Write(Utf8JsonWriter writer, SpanAttributes value, JsonSerializerOptions options)
    {
        var values = Values(options);
        writer.WriteStartObject();
        foreach (var (name, attribute) in value)
        {
            if (attribute is null) continue;
            writer.WritePropertyName(name);
            values.Write(writer, attribute, options);
        }
        writer.WriteEndObject();
    }
}

/// <summary><c>{ "status": "ok" }</c> or <c>{ "status": "error", "error"?: { "name", "message" } }</c>.</summary>
internal sealed class SpanStatusJsonConverter : JsonConverter<SpanStatus>
{
    public override SpanStatus? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("status", out var status)) throw new JsonException("A span status has a status.");
        if (status.ValueEquals("ok")) return SpanStatus.Ok;
        if (!status.ValueEquals("error")) throw new JsonException("A span status is ok or error.");
        if (!root.TryGetProperty("error", out var error) || error.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return SpanStatus.Failed();
        return SpanStatus.Failed(error.GetProperty("name").GetString()!, error.GetProperty("message").GetString()!);
    }

    public override void Write(Utf8JsonWriter writer, SpanStatus value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("status", value.IsError ? "error" : "ok");
        if (value.Error is { } error)
        {
            writer.WriteStartObject("error");
            writer.WriteString("name", error.Name);
            writer.WriteString("message", error.Message);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }
}

/// <summary>An enum written as upstream's string literal.</summary>
internal abstract class NamedEnumConverter<T>(params (T Value, string Name)[] names) : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        foreach (var (value, name) in names) if (name == text) return value;
        throw new JsonException($"Unknown {typeof(T).Name} '{text}'.");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        foreach (var (candidate, name) in names)
            if (EqualityComparer<T>.Default.Equals(candidate, value)) { writer.WriteStringValue(name); return; }
        throw new JsonException($"Unknown {typeof(T).Name} value.");
    }

    public static string NameOf(T value, (T Value, string Name)[] names)
    {
        foreach (var (candidate, name) in names) if (EqualityComparer<T>.Default.Equals(candidate, value)) return name;
        return value.ToString();
    }
}

internal sealed class AttributeTypeConverter() : NamedEnumConverter<TelemetryAttributeType>(Names)
{
    internal static readonly (TelemetryAttributeType, string)[] Names =
    [
        (TelemetryAttributeType.String, "string"), (TelemetryAttributeType.Number, "number"), (TelemetryAttributeType.Boolean, "boolean"),
        (TelemetryAttributeType.StringArray, "string[]"), (TelemetryAttributeType.NumberArray, "number[]"), (TelemetryAttributeType.BooleanArray, "boolean[]"),
        (TelemetryAttributeType.StringNumberArray, "(string|number)[]")
    ];
}

internal sealed class MetricKindConverter() : NamedEnumConverter<TelemetryMetricKind>(Names)
{
    internal static readonly (TelemetryMetricKind, string)[] Names =
        [(TelemetryMetricKind.Counter, "counter"), (TelemetryMetricKind.UpDownCounter, "upDownCounter"), (TelemetryMetricKind.Histogram, "histogram")];
}

internal sealed class CardinalityConverter() : NamedEnumConverter<TelemetryCardinality>((TelemetryCardinality.Low, "low"), (TelemetryCardinality.High, "high"));

internal sealed class ParentKindConverter() : NamedEnumConverter<TelemetryParentKind>(
    (TelemetryParentKind.Any, "any"), (TelemetryParentKind.RootOrExternal, "root_or_external"), (TelemetryParentKind.Spans, "spans"));
