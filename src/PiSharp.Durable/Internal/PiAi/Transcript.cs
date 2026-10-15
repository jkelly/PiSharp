// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/ai/src/utils/transcript.ts (getCurrentTools, toToolDeclaration,
// declarationsEqual) over pi-ai wire JSON. PiSharp.AI's ProviderTranscript.CurrentTools is the same replay over System.Text.Json nodes and
// internal to PiSharp.AI; the test project compares the two on shared inputs.
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Internal.PiAi;

/// <summary>Transcript tool-state helpers (upstream <c>utils/transcript.ts</c>).</summary>
internal static class PiAiTranscript
{
    /// <summary>
    /// The tools available after applying every system message's <c>toolsRemoved</c> and then <c>toolsAdded</c> in order (upstream
    /// <c>getCurrentTools</c>). Like a JavaScript Map, a re-added name keeps its slot and a removed then re-added name appends.
    /// </summary>
    public static List<JsonObjectValue> GetCurrentTools(IEnumerable<JsonValue> messages)
    {
        var tools = new OrderedDictionary<string, JsonObjectValue>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message is not JsonObjectValue value || !(value.TryGetValue("role", out var role) && role is JsonStringValue { Value: "system" })) continue;
            if (value.TryGetValue("toolsRemoved", out var removed) && removed is JsonArrayValue removals)
                foreach (var tool in removals.OfType<JsonObjectValue>()) tools.Remove(Name(tool));
            if (value.TryGetValue("toolsAdded", out var added) && added is JsonArrayValue additions)
                foreach (var tool in additions.OfType<JsonObjectValue>()) tools[Name(tool)] = tool;
        }
        return [.. tools.Values];
    }

    /// <summary>A tool reduced to the fields the model sees: <c>{name, description, parameters, constrainedSampling?}</c> (upstream
    /// <c>toToolDeclaration</c>).</summary>
    public static JsonObjectValue ToToolDeclaration(JsonObjectValue tool)
    {
        var properties = new List<KeyValuePair<string, JsonValue>>
        {
            KeyValuePair.Create("name", tool.TryGetValue("name", out var name) ? name : JsonValue.Null),
            KeyValuePair.Create("description", tool.TryGetValue("description", out var description) ? description : JsonValue.Null),
            KeyValuePair.Create("parameters", tool.TryGetValue("parameters", out var parameters) ? parameters : JsonValue.Null),
        };
        if (tool.TryGetValue("constrainedSampling", out var constrained)) properties.Add(KeyValuePair.Create("constrainedSampling", constrained));
        return JsonValue.Object(properties);
    }

    /// <summary>The declaration of a registered tool: only the pi-ai <c>Tool</c> fields enter the transcript, <c>constrainedSampling</c>
    /// when the tool sets it.</summary>
    public static JsonObjectValue ToToolDeclaration(ToolRegistration tool) => JsonValue.Object(
    [
        KeyValuePair.Create("name", (JsonValue)tool.Name),
        KeyValuePair.Create("description", (JsonValue)tool.Description),
        KeyValuePair.Create("parameters", (JsonValue)tool.Parameters),
        .. tool.ConstrainedSampling is { } constrained ? [KeyValuePair.Create("constrainedSampling", constrained)] : Array.Empty<KeyValuePair<string, JsonValue>>(),
    ]);

    /// <summary>Whether two tools declare the same interface: their serialized declarations are equal (upstream
    /// <c>declarationsEqual</c>).</summary>
    public static bool DeclarationsEqual(JsonObjectValue left, JsonObjectValue right) =>
        string.Equals(ToToolDeclaration(left).ToJson(), ToToolDeclaration(right).ToJson(), StringComparison.Ordinal);

    private static string Name(JsonObjectValue tool) => tool.TryGetValue("name", out var name) && name is JsonStringValue text ? text.Value : "undefined";
}
