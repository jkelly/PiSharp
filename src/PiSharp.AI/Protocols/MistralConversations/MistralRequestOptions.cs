using System.Text.Json.Nodes;

namespace PiSharp.AI.Protocols.MistralConversations;

public sealed partial class MistralTextHttpSseTransport
{
    // Pi v0.99.1 api/mistral-conversations.ts:372-402, d86654a.
    // Map after the awaited hook, and only on the separately parsed wire object.
    // A camelCase field wins over an existing wire field, even when its value is null.
    private static void MapRequestOptions(JsonObject wire)
    {
        foreach (var (source, target) in new[]
        {
            ("topP", "top_p"), ("maxTokens", "max_tokens"), ("randomSeed", "random_seed"),
            ("responseFormat", "response_format"), ("toolChoice", "tool_choice"),
            ("presencePenalty", "presence_penalty"), ("frequencyPenalty", "frequency_penalty"),
            ("parallelToolCalls", "parallel_tool_calls"), ("reasoningEffort", "reasoning_effort"),
            ("promptMode", "prompt_mode"), ("promptCacheKey", "prompt_cache_key"), ("safePrompt", "safe_prompt")
        }) Remap(wire, source, target);

        if (wire["response_format"] is not JsonObject format) return;
        Remap(format, "jsonSchema", "json_schema");
        if (format["json_schema"] is JsonObject schema) Remap(schema, "schemaDefinition", "schema");
    }

    private static void Remap(JsonObject record, string source, string target)
    {
        if (!record.TryGetPropertyValue(source, out var value)) return;
        record.Remove(source);
        record[target] = value;
    }
}
