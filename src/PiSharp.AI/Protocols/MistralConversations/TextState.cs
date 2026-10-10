using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using static PiSharp.AI.Protocols.MistralConversations.MistralTextHttpSseTransport;

namespace PiSharp.AI.Protocols.MistralConversations;

internal sealed class TextState(ChatRequest request, MistralTextOptions options)
{
    private readonly List<AssistantContent> blocks = [];
    private readonly Dictionary<string, (int Index, string Raw)> tools = new(StringComparer.Ordinal);
    private int currentText = -1;
    private long characters;
    private TokenUsage usage = TokenUsage.Zero;
    private string? responseId, rawStop;
    public StopReason Reason { get; private set; } = StopReason.Pending;
    public string? ProviderError { get; private set; }
    public AssistantMessage Message(StopReason reason, string? error = null)
    {
        var extra = JsonFields.Empty;
        if (responseId is not null) extra = extra.Set("responseId", String(responseId));
        if (rawStop is not null) extra = extra.Set("rawStopReason", String(rawStop));
        if ((error ?? ProviderError) is { } message) extra = extra.Set("errorMessage", String(message));
        return new(request.Model.Api, request.Model.Provider, request.Model.Id, request.Timestamp,
            blocks.ToImmutableArray(), usage, reason, extra);
    }
    public ImmutableArray<StreamEvent> Convert(JsonData chunk)
    {
        // A chunk is published as one batch. Restore every staged mutation if admission
        // fails so cleanup can only close text whose start/deltas were published.
        var oldBlocks = blocks.ToArray(); var oldTools = tools.ToArray(); var oldText = currentText;
        var oldCharacters = characters; var oldUsage = usage;
        var oldId = responseId; var oldStop = rawStop; var oldReason = Reason; var oldError = ProviderError;
        try { return Admit(() => ConvertCore(chunk)); }
        catch
        {
            blocks.Clear(); blocks.AddRange(oldBlocks); tools.Clear(); foreach (var item in oldTools) tools.Add(item.Key, item.Value);
            currentText = oldText; characters = oldCharacters; usage = oldUsage;
            responseId = oldId; rawStop = oldStop; Reason = oldReason; ProviderError = oldError;
            throw;
        }
    }
    private ImmutableArray<StreamEvent> ConvertCore(JsonData chunk)
    {
        var value = chunk.Value; var frames = ImmutableArray.CreateBuilder<StreamEvent>();
        if (responseId is null && value.TryGetProperty("id", out var responseIdValue) && responseIdValue.ValueKind != JsonValueKind.Null)
        { if (responseIdValue.ValueKind != JsonValueKind.String) throw Fail(NativeChatFailureCode.MalformedStream, "Invalid Mistral response id."); if (!string.IsNullOrEmpty(responseIdValue.GetString())) responseId = responseIdValue.GetString(); }
        if (value.TryGetProperty("usage", out var observed) && observed.ValueKind != JsonValueKind.Null) SetUsage(observed);
        var choices = value.GetProperty("choices"); if (choices.GetArrayLength() == 0) return frames.ToImmutable();
        var choice = choices[0];
        if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind != JsonValueKind.Null)
        {
            if (finish.ValueKind != JsonValueKind.String) throw Fail(NativeChatFailureCode.MalformedStream, "Invalid Mistral finish reason.");
            var reason = finish.GetString()!;
            if (reason.Length > 0)
            {
                rawStop = reason;
                Reason = reason switch { "stop" => StopReason.Stop, "length" or "model_length" => StopReason.Length, "tool_calls" => StopReason.ToolUse, _ => StopReason.Error };
                // Pi abe508e1 mistral-conversations.ts mapChatStopReason: Mistral reports transient server failures as
                // "error"; "server error" makes the message retryable.
                if (Reason == StopReason.Error) ProviderError = reason == "error" ? "Provider stopped with: error (server error)" : "Provider stopped with: " + reason;
            }
        }
        var delta = choice.GetProperty("delta"); if (delta.ValueKind != JsonValueKind.Object) throw Fail(NativeChatFailureCode.MalformedStream, "Invalid Mistral delta.");
        foreach (var field in delta.EnumerateObject())
        {
            if (field.Name == "tool_calls" && field.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Array) continue;
            if (field.Name is not ("content" or "role")) throw Fail(NativeChatFailureCode.UnsupportedFeature, "Unsupported Mistral tool/reasoning delta.");
        }
        if (delta.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null)
        {
            if (content.ValueKind == JsonValueKind.String) Append(content.GetString()!);
            else if (content.ValueKind == JsonValueKind.Array) foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.String) { Append(part.GetString()!); continue; }
                if (part.ValueKind != JsonValueKind.Object || !part.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                    throw Fail(NativeChatFailureCode.MalformedStream, "Invalid Mistral streamed content item.");
                if (type.GetString() == "thinking")
                {
                    if (!part.TryGetProperty("thinking", out var thinking) || thinking.ValueKind == JsonValueKind.Null) continue;
                    if (thinking.ValueKind != JsonValueKind.Array)
                        throw Fail(NativeChatFailureCode.MalformedStream, "Invalid Mistral streamed thinking.");
                    Append(string.Concat(thinking.EnumerateArray().Select(StreamText)), thinking: true);
                }
                else if (type.GetString() == "text") Append(StreamText(part));
                // Pi ignores other string content types; this grants no tool/media authority.
            }
            else throw Fail(NativeChatFailureCode.UnsupportedFeature, "Unsupported Mistral content delta.");
        }
        if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
            foreach (var call in calls.EnumerateArray())
            {
                EndText(frames);
                var index = call.TryGetProperty("index", out var ix) ? ix.GetInt32() : (int?)null;
                if (index < 0) throw Fail(NativeChatFailureCode.MalformedStream, "Invalid Mistral tool index.");
                var id = call.TryGetProperty("id", out var supplied) && supplied.ValueKind != JsonValueKind.Null ? supplied.GetString() : null;
                id = string.IsNullOrEmpty(id) || id == "null" ? MistralToolIds.Derive("toolcall:" + (index ?? 0), 0) : id;
                var key = index is { } number ? "index:" + number : "id:" + id;
                var function = call.GetProperty("function");
                if (!tools.TryGetValue(key, out var saved))
                {
                    Limit(blocks.Count + 1, options.MaximumResponseContentBlocks);
                    // mistral-conversations.ts keeps toolCall.function.name, an empty string included (owner decision 13); a missing one
                    // (undefined upstream) stays unrepresentable and fails as malformed.
                    var name = function.GetProperty("name").GetString();
                    if (name is null) throw Fail(NativeChatFailureCode.MalformedStream, "Missing Mistral tool name.");
                    characters += id.Length + name.Length; Limit(characters, options.MaximumContentCharacters);
                    saved = (blocks.Count, ""); tools.Add(key, saved);
                    var block = new ToolCallContent(id, name, JsonData.EmptyObject); blocks.Add(block);
                    frames.Add(Snapshot(new ToolCallStarted(saved.Index, block)));
                }
                // typeof arguments === "string" ? arguments : JSON.stringify(arguments || {})
                var fragment = !function.TryGetProperty("arguments", out var args) ? "{}"
                    : args.ValueKind == JsonValueKind.String ? args.GetString()!
                    : args.ValueKind is JsonValueKind.Null or JsonValueKind.False || args.ValueKind == JsonValueKind.Number && args.GetDouble() == 0 ? "{}"
                    : StreamingJson.ParseToJson(args.GetRawText());
                characters += fragment.Length; Limit(characters, options.MaximumContentCharacters);
                var raw = saved.Raw + fragment;
                // mistral-conversations.ts: block.arguments = parseStreamingJson(block.partialArgs) on every delta and at the end.
                var parsed = StreamingJson.Parse(raw);
                var previous = (ToolCallContent)blocks[saved.Index];
                blocks[saved.Index] = previous with { Arguments = parsed };
                tools[key] = (saved.Index, raw);
                frames.Add(Snapshot(new ToolCallDelta(saved.Index, fragment)));
            }
        return frames.ToImmutable();
        void Append(string deltaText, bool thinking = false)
        {
            var clean = Sanitize(deltaText); if (clean.Length == 0) return;
            characters += clean.Length; Limit(characters, options.MaximumContentCharacters);
            if (currentText >= 0 && (blocks[currentText] is ThinkingContent) != thinking) EndText(frames);
            if (currentText < 0)
            {
                Limit(blocks.Count + 1, options.MaximumResponseContentBlocks); currentText = blocks.Count;
                if (thinking) { blocks.Add(new ThinkingContent("")); frames.Add(Snapshot(new ThinkingStarted(currentText, new ThinkingContent("")))); }
                else { blocks.Add(new TextContent("")); frames.Add(Snapshot(new TextStarted(currentText, new TextContent("")))); }
            }
            if (thinking)
            { blocks[currentText] = new ThinkingContent(((ThinkingContent)blocks[currentText]).Thinking + clean); frames.Add(Snapshot(new ThinkingDelta(currentText, clean))); }
            else
            { blocks[currentText] = new TextContent(((TextContent)blocks[currentText]).Text + clean); frames.Add(Snapshot(new TextDelta(currentText, clean))); }
        }
    }
    private void EndText(ImmutableArray<StreamEvent>.Builder frames)
    {
        if (currentText < 0) return;
        frames.Add(Snapshot(blocks[currentText] is ThinkingContent thinking
            ? new ThinkingEnded(currentText, thinking.Thinking) : new TextEnded(currentText, ((TextContent)blocks[currentText]).Text))); currentText = -1;
    }
    private static string StreamText(JsonElement part)
    {
        if (part.ValueKind != JsonValueKind.Object)
            throw Fail(NativeChatFailureCode.MalformedStream, "Invalid Mistral streamed text item.");
        if (!part.TryGetProperty("text", out var text) || text.ValueKind == JsonValueKind.Null) return "";
        if (text.ValueKind != JsonValueKind.String)
            throw Fail(NativeChatFailureCode.MalformedStream, "Invalid Mistral streamed text.");
        return text.GetString()!;
    }
    public ImmutableArray<StreamEvent> Finish()
    {
        var frames = ImmutableArray.CreateBuilder<StreamEvent>(); EndText(frames);
        foreach (var item in tools.Values) frames.Add(Snapshot(new ToolCallEnded(item.Index, (ToolCallContent)blocks[item.Index])));
        return frames.ToImmutable();
    }
    private StreamEvent Snapshot(StreamEvent frame) => frame with { SourceEmissionSnapshot = PiWireJson.WriteMessage(Message(Reason)) };
    private void SetUsage(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Fail(NativeChatFailureCode.MalformedStream, "Invalid Mistral usage.");
        var prompt = Count(value, "prompt_tokens"); var output = Count(value, "completion_tokens"); var total = Count(value, "total_tokens");
        JsonElement cached = default;
        foreach (var path in new[] { new[] { "promptTokensDetails", "cachedTokens" }, new[] { "prompt_tokens_details", "cached_tokens" }, new[] { "promptTokenDetails", "cachedTokens" }, new[] { "prompt_token_details", "cached_tokens" }, new[] { "numCachedTokens" }, new[] { "num_cached_tokens" } })
        {
            var cursor = value; var found = true;
            foreach (var name in path) if (cursor.ValueKind == JsonValueKind.Object && cursor.TryGetProperty(name, out var child)) cursor = child; else { found = false; break; }
            if (found && cursor.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) { cached = cursor; break; }
        }
        // getMistralCachedPromptTokens: Math.min(promptTokens, Math.max(0, cachedTokens)) of a finite number, else 0.
        var cache = cached.ValueKind == JsonValueKind.Number && JsonNumber.Read(cached) is var n && double.IsFinite(n) ? Math.Min(prompt, Math.Max(0, n)) : 0;
        var input = Math.Max(0, prompt - cache); if (total == 0) total = input + output + cache + 0;
        // Pi abe508 models.ts calculateCost: a prompt-length tier prices the whole request; Mistral reports no cache writes.
        var rates = PromptLengthPricing.TrySelect(options.Costs.Tiers, candidate => candidate.InputTokensAbove, (double)input, cache, 0d, out var tier)
            ? new MistralTokenCosts(tier.Input, tier.Output, tier.CacheRead, tier.CacheWrite) : options.Costs;
        var costs = new[] { (rates.Input / 1_000_000) * input, (rates.Output / 1_000_000) * output, (rates.CacheRead / 1_000_000) * cache, (rates.CacheWrite * 0 + rates.Input * 2 * 0) / 1_000_000 };
        var sum = costs[0] + costs[1] + costs[2] + costs[3]; if (costs.Any(x => !double.IsFinite(x) || x > (double)decimal.MaxValue) || !double.IsFinite(sum) || sum > (double)decimal.MaxValue) throw Fail(NativeChatFailureCode.ResourceLimit, "Mistral cost exceeds numeric limits.");
        var binary = JsonData.Parse(JsonSerializer.Serialize(new { input = costs[0], output = costs[1], cacheRead = costs[2], cacheWrite = costs[3], total = sum }));
        // Typed decimals must describe the exact serialized binary64 values used by
        // PiWireJson snapshots. A direct double cast can round away their final digits.
        var wireCost = binary.Value;
        usage = new(input, output, cache, 0, total, new(wireCost.GetProperty("input").GetDecimal(),
            wireCost.GetProperty("output").GetDecimal(), wireCost.GetProperty("cacheRead").GetDecimal(),
            wireCost.GetProperty("cacheWrite").GetDecimal(), wireCost.GetProperty("total").GetDecimal(), SourceBinary64Cost: binary));
    }
    // mistral-conversations.ts keeps each count as the JavaScript number the chunk reports (a fraction included).
    private static double Count(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var item) || item.ValueKind == JsonValueKind.Null) return 0;
        var numeric = item.ValueKind == JsonValueKind.Number ? JsonNumber.Read(item) : double.NaN;
        if (!double.IsFinite(numeric)) throw Fail(NativeChatFailureCode.UnsupportedFeature, "Unsupported Mistral usage number.");
        var number = numeric;
        return number;
    }
    private static JsonData String(string value) => JsonData.Parse(JsonSerializer.Serialize(value));
}
