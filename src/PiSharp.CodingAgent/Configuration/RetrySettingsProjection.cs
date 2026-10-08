using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;

namespace PiSharp.CodingAgent.Configuration;

/// <summary>Pure retry settings projection. Does not admit or perform filesystem writes.</summary>
public static class RetrySettingsProjection
{
    /// <summary>Apply separately to each settings file before precedence merging.</summary>
    public static JsonData NormalizeLayer(JsonData layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (layer.Value.ValueKind != JsonValueKind.Object) throw new JsonException("Settings must be an object.");
        var owned = JsonNode.Parse(layer.ToString())!.AsObject();
        if (owned["retry"] is not JsonObject retry) return layer;
        if (retry["maxDelayMs"] is JsonValue legacy && legacy.GetValueKind() == JsonValueKind.Number)
        {
            var provider = retry["provider"];
            if (provider is not JsonObject existing || existing["maxRetryDelayMs"] is null)
            {
                var migrated = provider is JsonObject obj ? (JsonObject)obj.DeepClone() : new JsonObject();
                // JS object spread also preserves indexed properties of provider arrays.
                if (provider is JsonArray array)
                    for (var index = 0; index < array.Count; index++) migrated[index.ToString(System.Globalization.CultureInfo.InvariantCulture)] = array[index]?.DeepClone();
                migrated["maxRetryDelayMs"] = legacy.DeepClone();
                retry["provider"] = migrated;
            }
        }
        retry.Remove("maxDelayMs");
        return JsonData.Parse(owned.ToJsonString());
    }

    public static AgentRetryPolicy ReadEffective(JsonData mergedSettings)
    {
        ArgumentNullException.ThrowIfNull(mergedSettings);
        var root = mergedSettings.Value;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Settings must be an object.");
        if (!root.TryGetProperty("retry", out var retry) || retry.ValueKind == JsonValueKind.Null) return AgentRetryPolicy.Default;
        if (retry.ValueKind != JsonValueKind.Object) throw new JsonException("retry must be an object.");
        var enabled = true;
        if (retry.TryGetProperty("enabled", out var value) && value.ValueKind != JsonValueKind.Null)
        {
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JsonException("retry.enabled must be boolean.");
            enabled = value.GetBoolean();
        }
        var attempts = Integer(retry, "maxRetries", 3, int.MaxValue);
        return new(enabled, (int)attempts, Integer(retry, "baseDelayMs", 2000, AgentRetryPolicy.MaximumSafeInteger),
            Integer(retry, "maxAgentDelayMs", 60000, AgentRetryPolicy.MaximumSafeInteger));
    }

    private static long Integer(JsonElement retry, string name, long fallback, long maximum)
    {
        if (!retry.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number)
            || number < 0 || number > maximum || Math.Truncate(number) != number)
            throw new JsonException($"retry.{name} must be a nonnegative safe integer no greater than {maximum}.");
        return (long)number;
    }
}
