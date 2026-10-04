using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.AI;

/// <summary>Strict, complete JSON object. Schema validation and authorization remain the tool pipeline's responsibility.</summary>
public sealed class FinalToolArguments
{
    public JsonData Json { get; }
    private FinalToolArguments(JsonData json) => Json = json;

    public static FinalToolArguments ParseStrict(string json)
    {
        var owned = JsonData.Parse(json);
        if (owned.Value.ValueKind != JsonValueKind.Object)
            throw new JsonException("Final tool arguments must be a complete JSON object.");
        return new(owned);
    }
}
