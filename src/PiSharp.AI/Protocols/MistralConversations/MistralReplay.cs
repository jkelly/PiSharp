// Pi v0.99.1 d86654abb8862e201933517d6f1fce9f88dd117f: utils/transcript.ts, utils/text.ts, api/transform-messages.ts.
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;
using PiSharp.Contracts.Compatibility;

namespace PiSharp.AI.Protocols.MistralConversations;

public sealed partial class MistralTextHttpSseTransport
{
    private ImmutableArray<TranscriptEntry> ResolveReplay(ChatRequest request, MistralToolIds ids)
    {
        if (request.Messages.IsDefault || request.Messages.Length > PiRequestBudget.RequestMessages) throw Fail(NativeChatFailureCode.ResourceLimit, "Mistral replay message limit.");
        long size = 0;
        foreach (var entry in request.Messages)
        {
            if (entry is null || entry.WireBody is null) throw Fail(NativeChatFailureCode.UnsupportedFeature, "Invalid Mistral replay entry.");
            var body = entry.WireBody.Value; size += body.GetRawText().Length; Limit(size, options.MaximumContentCharacters); CheckDepth(body, 0);
            Limit(System.Text.Encoding.UTF8.GetByteCount(body.GetRawText()), options.MaximumPayloadBytes);
            if (body.ValueKind != JsonValueKind.Object || body.TryGetProperty("role", out var role) && role.GetString() != entry.Role)
                throw Fail(NativeChatFailureCode.UnsupportedFeature, "Invalid Mistral replay transcript.");
            if (entry.Role == "system")
            {
                // Pi v1.1.0 reads only the fields it uses and ignores any others (for example "offlineApi": null written
                // by earlier sessions), so a resumed session replays instead of failing.
                if (body.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    foreach (var part in content.EnumerateArray()) _ = ReadTextPart(part);
            }
        }
        var resolved = options.SupportsMidConversationSystemMessages ? request.Messages : CollapseSystems(request.Messages);
        var transformed = new List<TranscriptEntry>(); var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in resolved)
        {
            var body = JsonNode.Parse(entry.WireBody.ToString())!.AsObject();
            body["content"] ??= new JsonArray();
            if ((entry.Role is "assistant" or "toolResult") && body["content"] is not JsonArray)
                throw Fail(NativeChatFailureCode.UnsupportedFeature, "Mistral replay requires content blocks.");
            if (!options.SupportsImages && (entry.Role is "user" or "toolResult") && body["content"] is JsonArray input)
                body["content"] = DowngradeImages(input, entry.Role == "toolResult");
            if (entry.Role == "assistant")
            {
                var source = entry.WireBody.Value;
                var same = source.TryGetProperty("provider", out var provider) && provider.GetString() == model.Provider &&
                    source.TryGetProperty("api", out var api) && api.GetString() == model.Api && source.TryGetProperty("model", out var identity) && identity.GetString() == model.Id;
                var parts = new JsonArray();
                foreach (var part in body["content"]!.AsArray())
                {
                    Limit(parts.Count + 1, options.MaximumContentBlocks);
                    var block = part!.AsObject(); var type = block["type"]!.GetValue<string>();
                    if (type == "thinking")
                    {
                        var text = block["thinking"]?.GetValue<string>() ?? "";
                        var redacted = block["redacted"]?.GetValue<bool>() == true;
                        if (redacted) { if (same) parts.Add(block.DeepClone()); continue; }
                        if (same && !string.IsNullOrEmpty(block["thinkingSignature"]?.GetValue<string>())) { parts.Add(block.DeepClone()); continue; }
                        if (text.Trim(EcmaWhitespace).Length == 0) continue;
                        parts.Add(same ? block.DeepClone() : new JsonObject { ["type"] = "text", ["text"] = text }); continue;
                    }
                    if (type == "text" && !same) { parts.Add(new JsonObject { ["type"] = "text", ["text"] = block["text"]!.GetValue<string>() }); continue; }
                    if (type == "toolCall" && !same)
                    {
                        var call = block.DeepClone().AsObject(); call.Remove("thoughtSignature");
                        var original = call["id"]!.GetValue<string>(); var mapped = ids.Normalize(original);
                        if (mapped != original) { normalized[original] = mapped; call["id"] = mapped; }
                        parts.Add(call); continue;
                    }
                    parts.Add(block.DeepClone());
                }
                body["content"] = parts;
            }
            else if (entry.Role == "toolResult" && normalized.TryGetValue(body["toolCallId"]!.GetValue<string>(), out var mapped)) body["toolCallId"] = mapped;
            transformed.Add(new(entry.Role, JsonData.Parse(JsonUtf16.ToJsonString(body))));
        }
        var result = ImmutableArray.CreateBuilder<TranscriptEntry>(); var pending = new List<JsonObject>();
        var existing = new HashSet<string>(StringComparer.Ordinal); var held = new List<TranscriptEntry>();
        void Close()
        {
            foreach (var call in pending)
                if (!existing.Contains(call["id"]!.GetValue<string>()))
                {
                    Limit(result.Count + 1, PiRequestBudget.RequestItems);
                    result.Add(new("toolResult", JsonData.Parse(new JsonObject
                {
                    ["role"] = "toolResult", ["toolCallId"] = call["id"]!.GetValue<string>(), ["toolName"] = call["name"]!.GetValue<string>(),
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "No result provided" }), ["isError"] = true,
                    // Synthetic timestamp never enters the Mistral wire. Use the accepted request's clock value.
                    ["timestamp"] = request.Timestamp
                }.ToJsonString())));
                }
            pending.Clear(); existing.Clear(); result.AddRange(held); held.Clear();
        }
        foreach (var entry in transformed)
        {
            var body = entry.WireBody.Value;
            if (entry.Role == "assistant")
            {
                Close();
                if (body.TryGetProperty("stopReason", out var stop) && stop.GetString() is "error" or "aborted") continue;
                pending.AddRange(JsonNode.Parse(body.GetProperty("content").GetRawText())!.AsArray()
                    .Where(block => block!["type"]!.GetValue<string>() == "toolCall").Select(block => block!.AsObject()));
                result.Add(entry);
            }
            else if (entry.Role == "toolResult") { existing.Add(body.GetProperty("toolCallId").GetString()!); result.Add(entry); }
            else if (entry.Role == "system" && pending.Count > 0) held.Add(entry);
            else { if (entry.Role == "user") Close(); result.Add(entry); }
        }
        Close(); Limit(result.Count, PiRequestBudget.RequestItems); return result.ToImmutable();
    }
    private ImmutableArray<TranscriptEntry> CollapseSystems(ImmutableArray<TranscriptEntry> messages)
    {
        var texts = new List<string>(); var order = new List<string>(); var sections = new Dictionary<string, string>(StringComparer.Ordinal); var found = false;
        foreach (var entry in messages.Where(entry => entry.Role == "system"))
        {
            found = true; var body = entry.WireBody.Value; var text = ContentText(body);
            if (text.Length > 0) texts.Add(text);
            if (body.TryGetProperty("sections", out var updates) && updates.ValueKind != JsonValueKind.Null)
                foreach (var section in OrderedSections(updates))
                    if (section.Value.ValueKind == JsonValueKind.Null) { sections.Remove(section.Name); order.Remove(section.Name); }
                    else { if (!sections.ContainsKey(section.Name)) order.Add(section.Name); sections[section.Name] = section.Value.GetString()!; }
        }
        var output = ImmutableArray.CreateBuilder<TranscriptEntry>();
        if (found)
        {
            var values = new JsonObject(); foreach (var name in order) values[name] = sections[name];
            output.Add(new("system", JsonData.Parse(new JsonObject { ["role"] = "system", ["content"] = string.Join("\n\n", texts), ["sections"] = values }.ToJsonString())));
        }
        output.AddRange(messages.Where(entry => entry.Role != "system")); return output.ToImmutable();
    }
    private static string ContentText(JsonElement body)
    {
        if (!body.TryGetProperty("content", out var content) || content.ValueKind == JsonValueKind.Null) return "";
        return content.ValueKind == JsonValueKind.String ? content.GetString()! : string.Join("\n", content.EnumerateArray()
            .Where(block => block.GetProperty("type").GetString() == "text").Select(block => block.GetProperty("text").GetString()!));
    }
    private string SystemText(JsonElement body, bool update)
    {
        var parts = new List<string>(); var content = ContentText(body); if (content.Length > 0) parts.Add(content);
        if (body.TryGetProperty("sections", out var sections) && sections.ValueKind != JsonValueKind.Null)
            foreach (var section in OrderedSections(sections))
                if (update) parts.Add(section.Value.ValueKind == JsonValueKind.Null ? $"Removed system prompt section \"{section.Name}\"." :
                    $"Updated system prompt section \"{section.Name}\":\n\n{section.Value.GetString()}");
                else if (section.Value.ValueKind != JsonValueKind.Null) parts.Add(section.Value.GetString()!);
        return string.Join("\n\n", parts.Where(part => part.Length > 0));
    }
    private IEnumerable<JsonProperty> OrderedSections(JsonElement value) => JsonData.Parse(ReplayJson(value)).Value.EnumerateObject();
    private string ReplayJson(JsonElement value)
    {
        try { return EcmaScriptJsonProjection.Project(JsonData.FromElement(value), new(
            MaximumInputCharacters: options.MaximumContentCharacters, MaximumInputBytes: options.MaximumPayloadBytes,
            MaximumOutputCharacters: options.MaximumContentCharacters, MaximumOutputBytes: options.MaximumPayloadBytes,
            MaximumDepth: options.MaximumJsonDepth, MaximumStringCharacters: options.MaximumContentCharacters)); }
        catch (EcmaScriptJsonProjectionException error) { throw Fail(error.Failure == EcmaScriptJsonProjectionFailure.ResourceLimit ?
            NativeChatFailureCode.ResourceLimit : NativeChatFailureCode.UnsupportedFeature, "Unsupported Mistral replay JSON."); }
    }
}
