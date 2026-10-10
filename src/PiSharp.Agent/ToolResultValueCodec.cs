using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.Agent;

public sealed record ToolResultValueOptions(int MaximumCharacters = 65_536,
    int MaximumStructuredContentCharacters = 6 * 1024 * 1024 + 65_536,
    int MaximumContentBlocks = 128, int MaximumJsonDepth = 32,
    int MaximumRawCharacters = 7 * 1024 * 1024, int MaximumRawBytes = 28 * 1024 * 1024)
{
    /// <summary>Bounded outer execution admission accommodates the existing configured prepared adapters, including a
    /// Pi-sized image result (owner decision 0004: up to 4.5MB of base64 per image, as Pi's read tool sends).</summary>
    public static ToolResultValueOptions ExecutionBoundary { get; } = new(MaximumCharacters: 8 * 1024 * 1024,
        MaximumStructuredContentCharacters: 8 * 1024 * 1024 + 65_536, MaximumContentBlocks: 1024,
        MaximumRawCharacters: 24 * 1024 * 1024, MaximumRawBytes: 96 * 1024 * 1024);
    /// <summary>Result strings may hold lone surrogates, as a JavaScript string does (the Pi entry); otherwise they are refused.</summary>
    public bool KeepsLoneSurrogates { get; init; }
}

/// <summary>Strict, bounded admission and complete source result serialization; no numeric rewriting.</summary>
public static class ToolResultValueCodec
{
    public static ToolResult Read(string json, ToolResultValueOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        var limits = Limits(options);
        if (json.Length > limits.MaximumRawCharacters || Encoding.UTF8.GetByteCount(json) > limits.MaximumRawBytes) throw Invalid();
        return Read(Strict(json), limits);
    }
    public static ToolResult Read(JsonData value, ToolResultValueOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        var limits = Limits(options);
        var raw = value.ToString();
        if (raw.Length > limits.MaximumRawCharacters || Encoding.UTF8.GetByteCount(raw) > limits.MaximumRawBytes)
            throw Invalid();
        var strict = Strict(raw);
        if (strict.Value.ValueKind != JsonValueKind.Object) throw Invalid();
        var properties = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.Ordinal);
        foreach (var property in strict.Value.EnumerateObject()) properties.Add(property.Name, JsonData.FromElement(property.Value));
        var result = ToolResult.FromProperties(properties.ToImmutable());
        Validate(result, limits);
        return result;
    }

    public static void Validate(ToolResult result, ToolResultValueOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var limits = Limits(options);
        long ordinary = result.Failure?.Message?.Length ?? 0, rawCharacters = 2, rawBytes = 2;
        var first = true;
        if (ordinary > limits.MaximumCharacters || result.Failure is { } failure &&
            (!Enum.IsDefined(failure.Kind) || !Text(failure.Message, limits.KeepsLoneSurrogates) || failure.Message.Contains('\0'))) throw Invalid();
        if (result.OwnedContent is ImmutableArray<TextContent> typed && (typed.IsDefault || typed.Length > limits.MaximumContentBlocks)) throw Invalid();
        if (result.OwnedContent is JsonData ownedContent)
        {
            var rawContent = ownedContent.ToString();
            if (rawContent.Length > limits.MaximumRawCharacters || Encoding.UTF8.GetByteCount(rawContent) > limits.MaximumRawBytes ||
                ownedContent.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Array) ||
                ownedContent.Value.ValueKind == JsonValueKind.Array && ownedContent.Value.GetArrayLength() > limits.MaximumContentBlocks) throw Invalid();
        }
        foreach (var (name, value) in result.OwnedProperties)
        {
            if (!ScalarText(name)) throw Invalid();
            string raw;
            if (value is ImmutableArray<TextContent> content)
            {
                long retained = 0;
                foreach (var block in content)
                {
                    if (block?.Text is null || !Text(block.Text, limits.KeepsLoneSurrogates)) throw Invalid();
                    retained += block.Text.Length;
                    foreach (var (key, extra) in (block.ExtraProperties ?? JsonFields.Empty).Values)
                        retained += key.Length + (long)extra.ToString().Length;
                }
                ordinary += retained;
                if (ordinary > limits.MaximumCharacters) throw Invalid();
                raw = ContentJson(content, limits.KeepsLoneSurrogates).ToString();
            }
            else
            {
                if (value is not JsonData json) throw Invalid();
                raw = json.ToString();
                if (name != "structuredContent") ordinary += name is "content" ? ContentCharacters(json, limits.KeepsLoneSurrogates) :
                    name == "details" ? raw.Length : name.Length + (long)raw.Length;
                if (name == "structuredContent" && raw.Length > limits.MaximumStructuredContentCharacters) throw Invalid();
            }
            var punctuation = first ? 1 : 2; first = false;
            rawCharacters += JsonSerializer.Serialize(name).Length + (long)raw.Length + punctuation;
            rawBytes += Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(name)) + (long)Encoding.UTF8.GetByteCount(raw) + punctuation;
            if (ordinary > limits.MaximumCharacters || rawCharacters > limits.MaximumRawCharacters || rawBytes > limits.MaximumRawBytes)
                throw Invalid();
            var strict = Strict(raw);
            // Fixed content array/block envelopes do not consume the metadata container budget.
            if (!ValidJson(strict.Value, 0, name == "content" ? limits.MaximumJsonDepth + 2 : limits.MaximumJsonDepth, limits.KeepsLoneSurrogates)) throw Invalid();
        }
        // Content is nullable/absent in the raw result; the typed compatibility view is [] in both cases.
        if (result.HasProperty("content")) _ = ContentCharacters(result.Property("content")!, limits.KeepsLoneSurrogates);
    }

    public static JsonData Write(ToolResult result, ToolResultValueOptions? options = null)
    {
        Validate(result, options);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject(); WriteAdmittedProperties(writer, result); writer.WriteEndObject();
        }
        return JsonData.Parse(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length)));
    }

    public static void WriteProperties(Utf8JsonWriter writer, ToolResult result, ToolResultValueOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Validate(result, options ?? ToolResultValueOptions.ExecutionBoundary);
        WriteAdmittedProperties(writer, result);
    }
    private static void WriteAdmittedProperties(Utf8JsonWriter writer, ToolResult result)
    {
        foreach (var (name, _) in result.OwnedProperties)
        { writer.WritePropertyName(name); writer.WriteRawValue(result.Property(name)!.ToString(), skipInputValidation: true); }
    }

    internal static long AdditionalCharacters(ToolResult result)
    {
        long characters = 0;
        foreach (var (name, value) in result.OwnedProperties)
            if (name is not ("content" or "details" or "structuredContent"))
                characters += name.Length + (long)((JsonData)value!).ToString().Length;
        return characters;
    }
    internal static ImmutableArray<TextContent> ReadContent(JsonData value)
    {
        if (value.Value.ValueKind == JsonValueKind.Null) return [];
        if (value.Value.ValueKind != JsonValueKind.Array) throw Invalid();
        if (value.Value.EnumerateArray().Any(block => block.ValueKind == JsonValueKind.Object &&
            block.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "image"))
            throw new InvalidOperationException("Image-bearing tool content requires ContentValue; the text-only Content accessor cannot represent it.");
        try { return value.Value.EnumerateArray().Select(block => PiWireJson.ReadContent(block) as TextContent ?? throw Invalid()).ToImmutableArray(); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException) { throw Invalid(); }
    }
    internal static JsonData EmptyContent { get; } = JsonData.Parse("[]");
    internal static JsonData ExecutionContent(JsonData? value) => value is null || value.Value.ValueKind == JsonValueKind.Null
        ? EmptyContent : value.Value.ValueKind == JsonValueKind.Array ? value : throw Invalid();
    internal static JsonData AdmitContent(JsonData value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var result = ToolResult.FromProperties(ImmutableDictionary.Create<string, object?>(StringComparer.Ordinal).Add("content", value));
        Validate(result, ToolResultValueOptions.ExecutionBoundary);
        return JsonData.FromElement(value.Value);
    }
    internal static JsonData ContentJson(ImmutableArray<TextContent> content, bool loneSurrogates = false)
    {
        if (content.IsDefault) throw Invalid();
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var block in content)
            {
                if (block?.Text is null || !Text(block.Text, loneSurrogates)) throw Invalid();
                writer.WriteRawValue(PiWireJson.WriteContent(block).ToString(), skipInputValidation: true);
            }
            writer.WriteEndArray();
        }
        return JsonData.Parse(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length)));
    }
    internal static long ContentCharacters(JsonData value, bool loneSurrogates = false)
    {
        if (value.Value.ValueKind == JsonValueKind.Null) return 0;
        if (value.Value.ValueKind != JsonValueKind.Array) throw Invalid();
        long result = 0;
        foreach (var block in value.Value.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) throw Invalid();
            var type = ContentString(block, "type", loneSurrogates);
            if (type == "text") result += ContentString(block, "text", loneSurrogates).Length;
            else if (type == "image") result += ContentString(block, "data", loneSurrogates).Length + (long)ContentString(block, "mimeType", loneSurrogates).Length;
            else throw Invalid();
            foreach (var property in block.EnumerateObject())
            {
                var name = JsonUtf16.GetName(property);
                if (name == "type" || type == "text" && name == "text" || type == "image" && name is "data" or "mimeType") continue;
                if (!Text(name, loneSurrogates)) throw Invalid();
                result += name.Length + (long)property.Value.GetRawText().Length;
            }
        }
        return result;
    }
    private static string ContentString(JsonElement block, string name, bool loneSurrogates)
    {
        if (!block.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) throw Invalid();
        var text = JsonUtf16.GetString(value);
        return Text(text, loneSurrogates) ? text : throw Invalid();
    }
    private static ToolResultValueOptions Limits(ToolResultValueOptions? options)
    {
        var result = options ?? new();
        if (result.MaximumCharacters <= 0 || result.MaximumStructuredContentCharacters <= 0 || result.MaximumContentBlocks <= 0 ||
            result.MaximumJsonDepth is < 1 or > PiSharp.Contracts.JsonData.MaximumDepth || result.MaximumRawCharacters <= 0 || result.MaximumRawBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        return result;
    }
    internal static ToolResultValueOptions ValidateLimits(ToolResultValueOptions options) => Limits(options);
    private static JsonData Strict(string raw)
    {
        try { return JsonData.Parse(raw); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException) { throw Invalid(); }
    }
    private static bool ValidJson(JsonElement value, int parentDepth, int maximumDepth, bool loneSurrogates)
    {
        if (value.ValueKind == JsonValueKind.String) return Text(JsonUtf16.GetString(value), loneSurrogates);
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetDouble(out var number) && double.IsFinite(number);
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False) return true;
        if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array) || parentDepth >= maximumDepth) return false;
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                var name = JsonUtf16.GetName(property);
                if (!names.Add(name) || !Text(name, loneSurrogates) || !ValidJson(property.Value, parentDepth + 1, maximumDepth, loneSurrogates)) return false;
            }
        }
        else foreach (var child in value.EnumerateArray()) if (!ValidJson(child, parentDepth + 1, maximumDepth, loneSurrogates)) return false;
        return true;
    }
    private static bool Text(string? text, bool loneSurrogates) => text is not null && (loneSurrogates || ScalarText(text));
    internal static bool ScalarText(string? text)
    {
        if (text is null) return false;
        for (var index = 0; index < text.Length; index++)
            if (char.IsHighSurrogate(text[index])) { if (++index >= text.Length || !char.IsLowSurrogate(text[index])) return false; }
            else if (char.IsLowSurrogate(text[index])) return false;
        return true;
    }
    private static InvalidOperationException Invalid() => new("Invalid or oversized owned tool result.");
}
