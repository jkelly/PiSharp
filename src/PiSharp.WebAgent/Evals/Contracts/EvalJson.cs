// Decision 0011 (run W5, E0): how evals records are written, in webagent.db's `data` columns and in /api/evals JSON: SettingsStore.JsonOptions
// (camelCase, enums by their wire names, nulls left out), times as UTC Unix milliseconds in columns, and enum columns as the same wire
// names the JSON uses (EvalJson.Wire), so the migrations' CHECK lists, the API and the stored JSON never disagree.
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.WebAgent.Data;

namespace PiSharp.WebAgent.Evals;

/// <summary>The evals area's JSON and column conventions.</summary>
public static class EvalJson
{
    /// <summary>The options every evals record is written and read with (<see cref="SettingsStore.JsonOptions"/>).</summary>
    public static JsonSerializerOptions Options => SettingsStore.JsonOptions;

    /// <summary><paramref name="value"/> as JSON text.</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary><paramref name="value"/> as a JSON node.</summary>
    public static JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Options);

    /// <summary>The <typeparamref name="T"/> in <paramref name="json"/>.</summary>
    /// <exception cref="JsonException">It is not a <typeparamref name="T"/>.</exception>
    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"The JSON is not a {typeof(T).Name}.");

    /// <summary>The <typeparamref name="T"/> in <paramref name="json"/>, or null when it cannot be read (a store skips such a row).</summary>
    public static T? TryDeserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, Options); }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>The wire name of <paramref name="value"/> (<c>all-bad</c>, <c>skipped-over-cap</c>), as JSON and the columns use it.</summary>
    public static string Wire<T>(T value) where T : struct, Enum =>
        JsonSerializer.SerializeToNode(value, Options)?.GetValue<string>() ?? throw new ArgumentException($"{value} has no wire name.");

    /// <summary>The <typeparamref name="T"/> whose wire name is <paramref name="wire"/>, or null.</summary>
    public static T? FromWire<T>(string? wire) where T : struct, Enum
    {
        if (string.IsNullOrEmpty(wire)) return null;
        foreach (var value in Enum.GetValues<T>())
            if (string.Equals(Wire(value), wire, StringComparison.Ordinal)) return value;
        return null;
    }

    /// <summary><paramref name="at"/> as a column value: UTC Unix milliseconds.</summary>
    public static long ToColumn(DateTimeOffset at) => at.ToUnixTimeMilliseconds();

    /// <summary>A column value back as a time (UTC).</summary>
    public static DateTimeOffset FromColumn(long unixMilliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
}
