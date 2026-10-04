using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using static PiSharp.AI.Protocols.MistralConversations.MistralTextHttpSseTransport;

namespace PiSharp.AI.Protocols.MistralConversations;

internal sealed class TextState(ChatRequest request, MistralTextOptions options)
{
    private readonly StringBuilder text = new();
    private TokenUsage usage = TokenUsage.Zero;
    private string? responseId, rawStop;
    public bool Open { get; private set; }
    public string Text => text.ToString();
    public StopReason Reason { get; private set; } = StopReason.Pending;
    public string? ProviderError { get; private set; }
    public AssistantMessage Message(StopReason reason, string? error = null)
    {
        var extra = JsonFields.Empty;
        if (responseId is not null) extra = extra.Set("responseId", String(responseId));
        if (rawStop is not null) extra = extra.Set("rawStopReason", String(rawStop));
        if ((error ?? ProviderError) is { } message) extra = extra.Set("errorMessage", String(message));
        return new(request.Model.Api, request.Model.Provider, request.Model.Id, request.Timestamp,
            Open ? [new TextContent(Text)] : [], usage, reason, extra);
    }
    public ImmutableArray<StreamEvent> Convert(JsonData chunk)
    {
        // A chunk is published as one batch. Restore every staged mutation if admission
        // fails so cleanup can only close text whose start/deltas were published.
        var length = text.Length; var open = Open; var oldUsage = usage;
        var oldId = responseId; var oldStop = rawStop; var oldReason = Reason; var oldError = ProviderError;
        try { return Admit(() => ConvertCore(chunk)); }
        catch
        {
            text.Length = length; Open = open; usage = oldUsage;
            responseId = oldId; rawStop = oldStop; Reason = oldReason; ProviderError = oldError;
            throw;
        }
    }
    private ImmutableArray<StreamEvent> ConvertCore(JsonData chunk)
    {
        var value = chunk.Value; var frames = ImmutableArray.CreateBuilder<StreamEvent>();
        if (responseId is null && value.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Null)
        { if (id.ValueKind != JsonValueKind.String) throw Fail(NativeChatFailureCode.MalformedStream, "Invalid Mistral response id."); if (!string.IsNullOrEmpty(id.GetString())) responseId = id.GetString(); }
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
                Reason = reason switch { "stop" => StopReason.Stop, "length" or "model_length" => StopReason.Length, "tool_calls" => throw Fail(NativeChatFailureCode.UnsupportedFeature, "Mistral tools are unsupported."), _ => StopReason.Error };
                if (Reason == StopReason.Error) ProviderError = "Provider stopped with: " + reason;
            }
        }
        var delta = choice.GetProperty("delta"); if (delta.ValueKind != JsonValueKind.Object) throw Fail(NativeChatFailureCode.MalformedStream, "Invalid Mistral delta.");
        foreach (var field in delta.EnumerateObject())
        {
            if (field.Name == "tool_calls" && (field.Value.ValueKind == JsonValueKind.Null || field.Value.ValueKind == JsonValueKind.Array && field.Value.GetArrayLength() == 0)) continue;
            if (field.Name is not ("content" or "role")) throw Fail(NativeChatFailureCode.UnsupportedFeature, "Unsupported Mistral tool/reasoning delta.");
        }
        if (delta.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null)
        {
            if (content.ValueKind == JsonValueKind.String) Append(content.GetString()!);
            else if (content.ValueKind == JsonValueKind.Array) foreach (var part in content.EnumerateArray()) Append(part.ValueKind == JsonValueKind.String ? part.GetString()! : ReadTextPart(part));
            else throw Fail(NativeChatFailureCode.UnsupportedFeature, "Unsupported Mistral content delta.");
        }
        return frames.ToImmutable();
        void Append(string deltaText)
        {
            var clean = Sanitize(deltaText); if (clean.Length == 0) return;
            Limit(text.Length + (long)clean.Length, options.MaximumContentCharacters);
            if (!Open) { Open = true; frames.Add(Snapshot(new TextStarted(0, new TextContent("")))); }
            text.Append(clean); frames.Add(Snapshot(new TextDelta(0, clean)));
        }
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
        var cache = cached.ValueKind == JsonValueKind.Number && cached.TryGetDouble(out var n) && double.IsFinite(n) ? Math.Clamp(n, 0, prompt) : 0;
        if (cache != Math.Truncate(cache)) throw Fail(NativeChatFailureCode.UnsupportedFeature, "Nonintegral Mistral usage unsupported.");
        var input = prompt - (long)cache; if (total == 0) total = checked(input + output + (long)cache);
        var costs = new[] { (options.Costs.Input / 1_000_000) * input, (options.Costs.Output / 1_000_000) * output, (options.Costs.CacheRead / 1_000_000) * cache, (options.Costs.CacheWrite * 0 + options.Costs.Input * 2 * 0) / 1_000_000 };
        var sum = costs[0] + costs[1] + costs[2] + costs[3]; if (costs.Any(x => !double.IsFinite(x) || x > (double)decimal.MaxValue) || !double.IsFinite(sum) || sum > (double)decimal.MaxValue) throw Fail(NativeChatFailureCode.ResourceLimit, "Mistral cost exceeds numeric limits.");
        var binary = JsonData.Parse(JsonSerializer.Serialize(new { input = costs[0], output = costs[1], cacheRead = costs[2], cacheWrite = costs[3], total = sum }));
        usage = new(input, output, (long)cache, 0, total, new((decimal)costs[0], (decimal)costs[1], (decimal)costs[2], 0, (decimal)sum, SourceBinary64Cost: binary));
    }
    private static long Count(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var item) || item.ValueKind == JsonValueKind.Null) return 0;
        if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out var numeric) || !double.IsFinite(numeric) || numeric < 0 || numeric > 9_007_199_254_740_991 || numeric != Math.Truncate(numeric)) throw Fail(NativeChatFailureCode.UnsupportedFeature, "Unsupported Mistral usage number.");
        var number = (long)numeric;
        return number;
    }
    private static JsonData String(string value) => JsonData.Parse(JsonSerializer.Serialize(value));
}
