using System.Globalization;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.MistralConversations;

public sealed partial class MistralTextHttpSseTransport
{
    private static readonly ImmutableArray<string> ThinkingLevels = ["off", "minimal", "low", "medium", "high", "xhigh", "max"];
    public ImmutableArray<string> GetSupportedThinkingLevels(ModelDescriptor requested)
    {
        if (requested != model) throw Fail(NativeChatFailureCode.UnsupportedFeature, "Mistral reasoning model differs from bound model.");
        if (!options.Reasoning) return ["off"];
        return ThinkingLevels.Where(level =>
        {
            var mapped = options.ThinkingLevelMap?.ContainsKey(level) == true;
            return mapped ? options.ThinkingLevelMap![level] is not null : level is not ("xhigh" or "max");
        }).ToImmutableArray();
    }
    private void AddSimpleOptions(JsonObject payload, ChatRequest request)
    {
        var level = request.ThinkingLevel;
        if (level is not null)
        {
            var available = GetSupportedThinkingLevels(request.Model); var requestedIndex = ThinkingLevels.IndexOf(level);
            if (!available.Contains(level))
                level = requestedIndex < 0 ? available.FirstOrDefault() ?? "off" :
                    ThinkingLevels.Skip(requestedIndex).FirstOrDefault(candidate => available.Contains(candidate)) ??
                    ThinkingLevels.Take(requestedIndex).Reverse().FirstOrDefault(candidate => available.Contains(candidate)) ?? available.FirstOrDefault() ?? "off";
        }
        if (options.Reasoning)
        {
            if (options.ThinkingLevelMap is { } map)
            {
                map.TryGetValue(level is null or "off" ? "off" : level, out var effort);
                if (level is not (null or "off")) effort ??= "high";
                if (effort is not null) payload["reasoningEffort"] = effort;
            }
            else if (level is not (null or "off")) payload["promptMode"] = "reasoning";
        }
        if (options.CachePrompt && !string.IsNullOrEmpty(options.SessionId)) payload["promptCacheKey"] = options.SessionId;
        if (options.PromptMode is { } mode) payload["promptMode"] = mode;
        if (options.ReasoningEffort is { } directEffort) payload["reasoningEffort"] = directEffort;
    }
    private JsonArray ProjectTools(ChatRequest request)
    {
        try
        {
            var systems = request.Messages.Where(entry => entry.Role == "system" &&
                (entry.WireBody.Value.TryGetProperty("toolsAdded", out _) || entry.WireBody.Value.TryGetProperty("toolsRemoved", out _))).Select(entry =>
            {
                var body = new JsonObject { ["role"] = "system" };
                foreach (var field in new[] { "toolsAdded", "toolsRemoved" })
                    if (entry.WireBody.Value.TryGetProperty(field, out var tools)) body[field] = JsonNode.Parse(tools.GetRawText(), documentOptions: PiSharp.Contracts.JsonData.DocumentOptions);
                // Mistral supports JSON-schema constrained sampling only. Original grammar configs are ignored.
                if (body["toolsAdded"] is JsonArray added)
                    foreach (var declaration in added.OfType<JsonObject>())
                        if (declaration["constrainedSampling"] is JsonObject sampling &&
                            sampling["type"] is JsonValue type && type.TryGetValue<string>(out var kind) && kind == "grammar")
                            declaration.Remove("constrainedSampling");
                return new TranscriptEntry("system", JsonData.Parse(body.ToJsonString()));
            }).ToImmutableArray();
            var declarations = new ResponsesToolDeclarationProjector(new(SupportsStrictMode: true, Strict: false,
                MaximumInputCharacters: options.MaximumContentCharacters, MaximumOutputBytes: options.MaximumPayloadBytes,
                MaximumJsonDepth: options.MaximumJsonDepth)).Project(request with { Messages = systems });
            var result = new JsonArray();
            foreach (var declaration in declarations.Value.EnumerateArray())
            {
                var function = JsonNode.Parse(declaration.GetRawText(), documentOptions: PiSharp.Contracts.JsonData.DocumentOptions)!.AsObject(); function.Remove("type");
                result.Add(new JsonObject { ["type"] = "function", ["function"] = function });
            }
            return result;
        }
        catch (ResponsesProjectionException) { throw Fail(NativeChatFailureCode.UnsupportedFeature, "Unsupported Mistral tool declaration."); }
    }
    private void ValidateReplayPart(JsonElement part)
    {
        if (part.ValueKind == JsonValueKind.Object && part.GetProperty("type").GetString() == "thinking")
        {
            foreach (var text in part.GetProperty("thinking").EnumerateArray()) _ = ReadTextPart(text);
        }
        else _ = ReadTextPart(part);
    }
    private static JsonNode Choice(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var choice = value.GetString();
            if (choice is "auto" or "none" or "any" or "required") return JsonValue.Create(choice)!;
        }
        if (value.ValueKind == JsonValueKind.Object && value.GetProperty("type").GetString() == "function" &&
            value.EnumerateObject().All(p => p.Name is "type" or "function"))
        {
            var function = value.GetProperty("function");
            if (function.ValueKind == JsonValueKind.Object && function.EnumerateObject().All(p => p.Name == "name") &&
                function.GetProperty("name").ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(function.GetProperty("name").GetString()))
                return JsonNode.Parse(value.GetRawText(), documentOptions: PiSharp.Contracts.JsonData.DocumentOptions)!;
        }
        throw Fail(NativeChatFailureCode.UnsupportedFeature, "Unsupported Mistral tool choice.");
    }
    private JsonObject? ProjectToolHistory(TranscriptEntry entry)
    {
        var body = entry.WireBody.Value;
        var content = body.GetProperty("content"); var parts = new JsonArray();
        if (content.ValueKind != JsonValueKind.Array) throw Fail(NativeChatFailureCode.UnsupportedFeature, "Mistral replay requires content blocks.");
        if (body.TryGetProperty("role", out var role) && role.GetString() != entry.Role)
            throw Fail(NativeChatFailureCode.UnsupportedFeature, "Mistral replay role mismatch.");
        if (entry.Role == "assistant")
        {
            var calls = new JsonArray();
            foreach (var block in content.EnumerateArray())
            {
                var type = block.GetProperty("type").GetString();
                if (type == "text")
                {
                    var text = block.GetProperty("text").GetString()!;
                    if (text.Trim(EcmaWhitespace).Length > 0) parts.Add(new JsonObject { ["type"] = "text", ["text"] = Sanitize(text) });
                }
                else if (type == "thinking")
                {
                    var thinking = block.GetProperty("thinking").GetString()!;
                    if (thinking.Trim(EcmaWhitespace).Length > 0) parts.Add(new JsonObject { ["type"] = "thinking",
                        ["thinking"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = Sanitize(thinking) }) });
                }
                else if (type == "toolCall")
                {
                    var args = block.GetProperty("arguments");
                    if (args.ValueKind != JsonValueKind.Object) throw Fail(NativeChatFailureCode.UnsupportedFeature, "Mistral tool arguments must be objects.");
                    // The SDK writes the call as convertMessages builds it: id, type, function, index.
                    calls.Add(new JsonObject { ["id"] = block.GetProperty("id").GetString(), ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = block.GetProperty("name").GetString(), ["arguments"] = ReplayJson(args) }, ["index"] = 0 });
                }
                else throw Fail(NativeChatFailureCode.UnsupportedFeature, "Unsupported Mistral assistant replay content.");
            }
            if (parts.Count == 0 && calls.Count == 0) return null;
            var message = new JsonObject { ["role"] = "assistant", ["prefix"] = false };
            if (parts.Count > 0) message["content"] = parts;
            if (calls.Count > 0) message["toolCalls"] = calls;
            return message;
        }
        var images = new JsonArray(); var texts = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            Limit(texts.Count + images.Count + 1, options.MaximumContentBlocks);
            var projected = ProjectInputPart(block);
            if (projected["type"]!.GetValue<string>() == "text") texts.Add(projected["text"]!.GetValue<string>());
            else images.Add(projected);
        }
        var textResult = string.Join("\n", texts).Trim(EcmaWhitespace);
        var isError = body.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True;
        parts.Add(new JsonObject { ["type"] = "text", ["text"] = (isError ? "[tool error] " : "") +
            (textResult.Length == 0 ? images.Count > 0 ? "(see attached image)" : "(no tool output)" : textResult) });
        foreach (var image in images) parts.Add(image!.DeepClone());
        return new() { ["role"] = "tool", ["toolCallId"] = body.GetProperty("toolCallId").GetString(),
            ["name"] = body.GetProperty("toolName").GetString(), ["content"] = parts };
    }
    private static void ValidateToolCalls(JsonElement calls)
    {
        foreach (var call in calls.EnumerateArray())
        {
            if (call.EnumerateObject().Any(p => p.Name is not ("id" or "type" or "function" or "index")) ||
                call.GetProperty("type").GetString() != "function" || string.IsNullOrEmpty(call.GetProperty("id").GetString()))
                throw Fail(NativeChatFailureCode.UnsupportedFeature, "Unsupported Mistral replay tool call.");
            var function = call.GetProperty("function");
            if (function.EnumerateObject().Any(p => p.Name is not ("name" or "arguments")) ||
                function.GetProperty("name").GetString() is null || function.GetProperty("arguments").ValueKind != JsonValueKind.String)
                throw Fail(NativeChatFailureCode.UnsupportedFeature, "Unsupported Mistral replay tool function.");
        }
    }
}

internal sealed class MistralToolIds
{
    private readonly Dictionary<string, string> ids = new(StringComparer.Ordinal);
    private readonly HashSet<string> used = new(StringComparer.Ordinal);
    internal string Normalize(string id)
    {
        // An id-less call of another API replays with a derived id (deriveMistralToolCallId("", n)); a nameless one keeps name "" (owner decision 13).
        ArgumentNullException.ThrowIfNull(id);
        if (ids.TryGetValue(id, out var existing)) return existing;
        for (var attempt = 0; ; attempt++)
        {
            var result = Derive(id, attempt);
            if (used.Add(result)) { ids.Add(id, result); return result; }
        }
    }
    internal static string Derive(string id, int attempt)
    {
        var normalized = Regex.Replace(id, "[^a-zA-Z0-9]", "", RegexOptions.CultureInvariant);
        if (attempt == 0 && normalized.Length == 9) return normalized;
        var seed = normalized.Length == 0 ? id : normalized;
        if (attempt != 0) seed += ":" + attempt.ToString(CultureInfo.InvariantCulture);
        unchecked
        {
            uint h1 = 0xdeadbeef, h2 = 0x41c6ce57;
            foreach (var character in seed) { h1 = (h1 ^ character) * 2654435761u; h2 = (h2 ^ character) * 1597334677u; }
            h1 = (h1 ^ h1 >> 16) * 2246822507u ^ (h2 ^ h2 >> 13) * 3266489909u;
            h2 = (h2 ^ h2 >> 16) * 2246822507u ^ (h1 ^ h1 >> 13) * 3266489909u;
            var hash = Base36(h2) + Base36(h1); return hash[..Math.Min(9, hash.Length)];
        }
    }
    private static string Base36(uint number)
    { const string digits = "0123456789abcdefghijklmnopqrstuvwxyz"; var text = ""; do { text = digits[(int)(number % 36)] + text; number /= 36; } while (number != 0); return text; }
}
