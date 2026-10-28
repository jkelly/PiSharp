// Decision 0012: Data travels between steps as Chord JSON inside task checkpoints. Chord numbers are doubles (Chord/JsonValue.cs), so a
// long above 2^53 or a decimal with more digits than a double holds would change silently; the codec refuses such values instead, naming
// their JSON path, and caps the serialized size.
using System.Globalization;
using System.Text.Json;
using JsonValueKind = System.Text.Json.JsonValueKind;
using PiSharp.Durable.Chord;

namespace PiSharp.CustomAgents.Workflows.Engine;

/// <summary>Serializes workflow values with System.Text.Json into Chord JSON, guarding precision and size.</summary>
internal sealed class DataCodec(JsonSerializerOptions options, int maxBytes)
{
    /// <summary>The default options: <see cref="JsonSerializerDefaults.Web"/>.</summary>
    public static JsonSerializerOptions DefaultOptions { get; } = Freeze(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public JsonSerializerOptions Options { get; } = options;
    public int MaxBytes { get; } = maxBytes;

    /// <summary><paramref name="value"/> as Chord JSON; throws <see cref="WorkflowDataException"/> on precision loss or over the cap.</summary>
    public JsonValue Serialize<T>(T value, string what = "data")
    {
        byte[] bytes;
        try { bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options); }
        catch (Exception error) when (error is NotSupportedException or JsonException or InvalidOperationException)
        {
            throw new WorkflowDataException($"The {what} cannot be serialized: {error.Message}", null, error);
        }
        if (bytes.Length > MaxBytes)
            throw new WorkflowDataException($"The {what} is {bytes.Length} bytes; the limit is {MaxBytes}.");
        using var document = JsonDocument.Parse(bytes);
        return FromElement(document.RootElement, "$");
    }

    /// <summary>A <typeparamref name="T"/> from Chord JSON.</summary>
    public T Deserialize<T>(JsonValue value, string what = "data")
    {
        try { return JsonSerializer.Deserialize<T>(value.ToJson(), Options)!; }
        catch (Exception error) when (error is NotSupportedException or JsonException or InvalidOperationException)
        {
            throw new WorkflowDataException($"The {what} cannot be read as {typeof(T).Name}: {error.Message}", null, error);
        }
    }

    /// <summary>A JSON element as Chord JSON, refusing numbers a double does not hold exactly.</summary>
    public static JsonValue FromElement(JsonElement element, string path) => element.ValueKind switch
    {
        JsonValueKind.Object => JsonValue.Object(element.EnumerateObject()
            .Select(property => KeyValuePair.Create(property.Name, FromElement(property.Value, path + "." + property.Name)))),
        JsonValueKind.Array => JsonValue.Array(element.EnumerateArray().Select((item, index) => FromElement(item, path + "[" + index + "]"))),
        JsonValueKind.String => JsonValue.String(element.GetString()!),
        JsonValueKind.Number => Number(element, path),
        JsonValueKind.True => JsonValue.Boolean(true),
        JsonValueKind.False => JsonValue.Boolean(false),
        _ => JsonValue.Null,
    };

    private static JsonValue Number(JsonElement element, string path)
    {
        var raw = element.GetRawText();
        var value = element.GetDouble();
        if (!double.IsFinite(value) || !Exact(raw, value))
            throw new WorkflowDataException(
                $"The number {raw} at {path} cannot be stored exactly (workflow numbers are doubles); use a string, or a smaller value.", path);
        return JsonValue.Number(value);
    }

    /// <summary>Whether the double <paramref name="value"/> parsed from <paramref name="raw"/> is the same number.</summary>
    private static bool Exact(string raw, double value)
    {
        const NumberStyles style = NumberStyles.Float;
        var shortest = value.ToString("R", CultureInfo.InvariantCulture);
        if (decimal.TryParse(raw, style, CultureInfo.InvariantCulture, out var written) &&
            decimal.TryParse(shortest, style, CultureInfo.InvariantCulture, out var stored))
            return written == stored;
        // Beyond decimal's range only doubles produce such numbers, and STJ writes doubles in round-trip form.
        return double.TryParse(raw, style, CultureInfo.InvariantCulture, out var parsed) && parsed == value;
    }

    private static JsonSerializerOptions Freeze(JsonSerializerOptions options)
    {
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
