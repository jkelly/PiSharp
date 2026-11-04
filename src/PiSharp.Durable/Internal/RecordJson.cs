// Decoding and encoding helpers for the persisted record JSON of packages/durable/src/types.ts (no upstream counterpart: upstream
// records are plain JavaScript objects and its backends JSON.stringify/JSON.parse them).
using System.Collections.Immutable;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Internal;

/// <summary>Strict readers and an order-preserving writer for record JSON.</summary>
internal static class RecordJson
{
    /// <summary>The error for a value that is not a valid <paramref name="what"/>.</summary>
    public static FormatException Invalid(string what, string why) => new($"Invalid {what}: {why}");

    public static JsonObjectValue Object(JsonValue value, string what) =>
        value as JsonObjectValue ?? throw Invalid(what, "expected a JSON object");

    /// <summary>Rejects keys outside <paramref name="allowed"/>: record shapes are closed, as their TypeScript types are.</summary>
    public static void OnlyKeys(JsonObjectValue value, string what, params ReadOnlySpan<string> allowed)
    {
        foreach (var key in value.Keys)
        {
            var known = false;
            foreach (var candidate in allowed) if (string.Equals(candidate, key, StringComparison.Ordinal)) { known = true; break; }
            if (!known) throw Invalid(what, $"unexpected field \"{key}\"");
        }
    }

    public static JsonValue Required(JsonObjectValue value, string key, string what) =>
        value.TryGetValue(key, out var field) ? field : throw Invalid(what, $"missing field \"{key}\"");

    public static JsonValue? Optional(JsonObjectValue value, string key) => value.TryGetValue(key, out var field) ? field : null;

    public static void Absent(JsonObjectValue value, string key, string what)
    {
        if (value.ContainsKey(key)) throw Invalid(what, $"field \"{key}\" is not allowed here");
    }

    public static long Integer(JsonValue value, string key, string what)
    {
        if (value is JsonNumberValue { Value: var number } && Math.Floor(number) == number && Math.Abs(number) <= Ids.MaxSafeInteger)
            return (long)number;
        throw Invalid(what, $"field \"{key}\" must be a safe integer");
    }

    public static long Integer(JsonObjectValue value, string key, string what) => Integer(Required(value, key, what), key, what);

    public static long? OptionalInteger(JsonObjectValue value, string key, string what) =>
        Optional(value, key) is { } field ? Integer(field, key, what) : null;

    public static int Version(JsonObjectValue value, string key, string what)
    {
        var number = Integer(value, key, what);
        if (number is < 1 or > int.MaxValue) throw Invalid(what, $"field \"{key}\" must be a positive integer");
        return (int)number;
    }

    public static double? OptionalNumber(JsonObjectValue value, string key, string what) => Optional(value, key) switch
    {
        null => null,
        JsonNumberValue number => number.Value,
        _ => throw Invalid(what, $"field \"{key}\" must be a number")
    };

    public static string String(JsonObjectValue value, string key, string what) =>
        Required(value, key, what) is JsonStringValue text ? text.Value : throw Invalid(what, $"field \"{key}\" must be a string");

    public static string? OptionalString(JsonObjectValue value, string key, string what) => Optional(value, key) switch
    {
        null => null,
        JsonStringValue text => text.Value,
        _ => throw Invalid(what, $"field \"{key}\" must be a string")
    };

    public static bool Boolean(JsonObjectValue value, string key, string what) =>
        Required(value, key, what) is JsonBooleanValue boolean ? boolean.Value : throw Invalid(what, $"field \"{key}\" must be a boolean");

    public static JsonArrayValue Array(JsonValue value, string key, string what) =>
        value as JsonArrayValue ?? throw Invalid(what, $"field \"{key}\" must be an array");

    public static TId Id<TId>(JsonObjectValue value, string key, string what) where TId : struct, IDurableId<TId> =>
        TId.FromNumber(Integer(value, key, what));

    public static TId? OptionalId<TId>(JsonObjectValue value, string key, string what) where TId : struct, IDurableId<TId> =>
        Optional(value, key) is { } field ? TId.FromNumber(Integer(field, key, what)) : null;

    public static TEnum Enum<TEnum>(JsonObjectValue value, string key, string what) where TEnum : struct, System.Enum =>
        WireName.Parse<TEnum>(String(value, key, what), what);

    /// <summary>
    /// JavaScript key order for a record whose object history is known: the keys in <paramref name="order"/> (the keys the object had when
    /// it was created or decoded) come first, in that order, and keys added later follow in <paramref name="json"/>'s order, as a spread
    /// (<c>{ ...current, entry }</c>) appends a new key and keeps an existing key's position. A default <paramref name="order"/> keeps
    /// <paramref name="json"/> as it is.
    /// </summary>
    public static JsonObjectValue Ordered(JsonObjectValue json, ImmutableArray<string> order)
    {
        if (order.IsDefaultOrEmpty) return json;
        var builder = JsonObjectValue.CreateBuilder();
        foreach (var key in order) if (json.TryGetValue(key, out var value) && !builder.ContainsKey(key)) builder[key] = value;
        foreach (var (key, value) in json) if (!builder.ContainsKey(key)) builder[key] = value;
        return builder.ToImmutable();
    }

    /// <summary>A telemetry carrier (upstream <c>TelemetryCarrier</c>, an object of string values) as JSON, in its key order; null when
    /// absent.</summary>
    public static JsonObjectValue? Carrier(IReadOnlyDictionary<string, string>? carrier)
    {
        if (carrier is null) return null;
        var builder = JsonObjectValue.CreateBuilder();
        foreach (var (key, value) in carrier) builder[key] = JsonValue.String(value);
        return builder.ToImmutable();
    }

    /// <summary>Reads an optional telemetry carrier: an object whose values are strings, in its key order.</summary>
    public static IReadOnlyDictionary<string, string>? OptionalCarrier(JsonObjectValue value, string key, string what)
    {
        return Optional(value, key) is { } field ? CarrierOf(field, key, what) : null;
    }

    /// <summary>Telemetry carriers as a JSON array; null when absent.</summary>
    public static JsonArrayValue? Carriers(IReadOnlyList<IReadOnlyDictionary<string, string>>? carriers) =>
        carriers is null ? null : JsonValue.Array(carriers.Select(carrier => (JsonValue)Carrier(carrier)!));

    /// <summary>Reads an optional array of telemetry carriers.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string>>? OptionalCarriers(JsonObjectValue value, string key, string what)
    {
        if (Optional(value, key) is not { } field) return null;
        if (field is not JsonArrayValue items) throw Invalid(what, $"\"{key}\" must be an array");
        var carriers = new List<IReadOnlyDictionary<string, string>>(items.Count);
        foreach (var item in items) carriers.Add(CarrierOf(item, key, what));
        return carriers;
    }

    private static OrderedDictionary<string, string> CarrierOf(JsonValue field, string key, string what)
    {
        if (field is not JsonObjectValue fields) throw Invalid(what, $"\"{key}\" must hold carrier objects");
        var carrier = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, item) in fields)
            carrier.Add(name, item is JsonStringValue text ? text.Value : throw Invalid(what, $"\"{key}\" values must be strings"));
        return carrier;
    }

    /// <summary>The keys of a decoded object, as <see cref="Ordered"/> takes them.</summary>
    public static ImmutableArray<string> KeysOf(JsonObjectValue value) => [.. value.Keys];

    /// <summary>An order-preserving object writer that skips absent (null) fields, as <c>JSON.stringify</c> skips <c>undefined</c>.</summary>
    public sealed class Writer
    {
        private readonly JsonObjectValue.Builder builder = JsonObjectValue.CreateBuilder();

        public Writer Add(string key, JsonValue? value)
        {
            if (value is not null) builder[key] = value;
            return this;
        }

        public Writer Add(string key, string? value) => Add(key, value is null ? null : JsonValue.String(value));

        public Writer Number(string key, double? value) => Add(key, value is { } number ? JsonValue.Number(number) : null);

        public Writer Add(string key, bool value) => Add(key, JsonValue.Boolean(value));

        public JsonObjectValue Build() => builder.ToImmutable();
    }
}

/// <summary>The persisted names of enum members: upstream's camelCase string literals (<c>AsOf</c> is <c>"asOf"</c>).</summary>
internal static class WireName
{
    /// <summary>The persisted name of <paramref name="value"/>.</summary>
    public static string Of<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value), value, $"Invalid {typeof(TEnum).Name}");
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>The member persisted as <paramref name="text"/>.</summary>
    public static TEnum Parse<TEnum>(string text, string what) where TEnum : struct, Enum =>
        TryParse<TEnum>(text, out var value) ? value : throw RecordJson.Invalid(what, $"unknown {typeof(TEnum).Name} \"{text}\"");

    /// <summary>Reads a persisted name.</summary>
    public static bool TryParse<TEnum>(string text, out TEnum value) where TEnum : struct, Enum
    {
        foreach (var candidate in Enum.GetValues<TEnum>())
            if (string.Equals(Of(candidate), text, StringComparison.Ordinal)) { value = candidate; return true; }
        value = default;
        return false;
    }
}
