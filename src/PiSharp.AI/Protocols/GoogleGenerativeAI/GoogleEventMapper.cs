// Pinned Pi api/google-generative-ai.ts response mapping (MIT). Native progress is immutable.
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;
using PiSharp.Contracts.Compatibility;

namespace PiSharp.AI.Protocols.GoogleGenerativeAI;

internal sealed class GoogleEventMapper
{
    private static long _toolCounter;
    private readonly ChatRequest _request;
    private readonly GoogleGenerativeAIOptions _options;
    private readonly List<AssistantContent> _blocks = [];
    private int _active = -1;
    private TokenUsage _usage = TokenUsage.Zero;
    private StopReason _reason = StopReason.Pending;
    private JsonFields _extra = JsonFields.Empty;
    private long _characters;
    internal GoogleFailure? FailureKind { get; private set; }
    internal GoogleEventMapper(ChatRequest request, GoogleGenerativeAIOptions options) { _request = request; _options = options; }
    internal StreamStarted Start() => new(Message(StopReason.Pending));
    private AssistantMessage Message(StopReason reason) => new(_request.Model.Api, _request.Model.Provider, _request.Model.Id,
        _request.Timestamp, _blocks.ToImmutableArray(), _usage, reason, _extra);
    private void Charge(long n)
    {
        if (n > _options.MaximumContentCharacters - _characters) throw GoogleData.Fail(GoogleFailure.ResourceLimit);
        _characters += n;
    }
    private void Add(AssistantContent block)
    {
        if (_blocks.Count >= _options.MaximumContentSlots) throw GoogleData.Fail(GoogleFailure.ResourceLimit);
        _blocks.Add(block);
    }
    private void End(List<StreamEvent> frames)
    {
        if (_active < 0) return;
        var block = _blocks[_active];
        if (block is TextContent text) frames.Add(new TextEnded(_active, text.Text, text.ExtraProperties));
        else if (block is ThinkingContent thinking) frames.Add(new ThinkingEnded(_active, thinking.Thinking, thinking.ExtraProperties));
        _active = -1;
    }
    internal IReadOnlyList<StreamEvent> Convert(JsonData chunk)
    {
        var value = chunk.Value; if (value.ValueKind != JsonValueKind.Object) throw GoogleData.Fail(GoogleFailure.MalformedStream);
        if (value.TryGetProperty("error", out _)) throw GoogleData.Fail(GoogleFailure.ProviderError);
        var frames = new List<StreamEvent>();
        var responseId = GoogleData.String(value, "responseId");
        if (!_extra.TryGet("responseId", out _) && !string.IsNullOrEmpty(responseId))
        { Charge(responseId.Length); _extra = _extra.Set("responseId", JsonData.Parse(JsonSerializer.Serialize(responseId))); }
        JsonElement candidate = default;
        if (value.TryGetProperty("candidates", out var candidates))
        {
            if (candidates.ValueKind != JsonValueKind.Array) throw GoogleData.Fail(GoogleFailure.MalformedStream);
            if (candidates.GetArrayLength() > 0) candidate = candidates[0];
        }
        if (candidate.ValueKind == JsonValueKind.Object && candidate.TryGetProperty("content", out var content) &&
            content.TryGetProperty("parts", out var parts))
        {
            if (parts.ValueKind != JsonValueKind.Array) throw GoogleData.Fail(GoogleFailure.MalformedStream);
            foreach (var part in parts.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object) throw GoogleData.Fail(GoogleFailure.MalformedStream);
                if (part.TryGetProperty("text", out var textValue))
                {
                    if (textValue.ValueKind != JsonValueKind.String) throw GoogleData.Fail(GoogleFailure.MalformedStream);
                    var text = textValue.GetString()!; var thinking = GoogleData.True(part, "thought");
                    if (_active < 0 || thinking != (_blocks[_active] is ThinkingContent))
                    {
                        End(frames); _active = _blocks.Count;
                        if (thinking) { var block = new ThinkingContent(""); Add(block); frames.Add(new ThinkingStarted(_active, block)); }
                        else { var block = new TextContent(""); Add(block); frames.Add(new TextStarted(_active, block)); }
                    }
                    Charge(text.Length);
                    var signature = GoogleData.String(part, "thoughtSignature");
                    var previous = _blocks[_active].ExtraProperties ?? JsonFields.Empty;
                    var signatureName = thinking ? "thinkingSignature" : "textSignature";
                    if (!string.IsNullOrEmpty(signature))
                    {
                        var oldLength = previous.TryGet(signatureName, out var old) ? old!.Value.GetString()!.Length : 0;
                        Charge(signature.Length - oldLength);
                        previous = previous.Set(signatureName, JsonData.Parse(JsonSerializer.Serialize(signature)));
                    }
                    if (thinking)
                    {
                        var old = (ThinkingContent)_blocks[_active];
                        _blocks[_active] = new ThinkingContent(old.Thinking + text, previous);
                        frames.Add(new ThinkingDelta(_active, text));
                    }
                    else
                    {
                        var old = (TextContent)_blocks[_active];
                        _blocks[_active] = new TextContent(old.Text + text, previous);
                        frames.Add(new TextDelta(_active, text));
                    }
                }
                if (part.TryGetProperty("functionCall", out var call))
                {
                    if (call.ValueKind != JsonValueKind.Object) throw GoogleData.Fail(GoogleFailure.MalformedStream);
                    End(frames);
                    var name = GoogleData.String(call, "name") ?? "";
                    // Native execution requires non-empty identity, even though Source can push an empty name.
                    if (name.Length == 0) throw GoogleData.Fail(GoogleFailure.MalformedStream);
                    var id = GoogleData.String(call, "id");
                    if (string.IsNullOrEmpty(id) || _blocks.OfType<ToolCallContent>().Any(x => x.Id == id))
                        id = $"{name}_{_request.Timestamp}_{Interlocked.Increment(ref _toolCounter)}";
                    // google-generative-ai.ts: arguments = part.functionCall.args ?? {} (whatever JSON value the chunk carried), and the
                    // delta is JSON.stringify(arguments).
                    var raw = !call.TryGetProperty("args", out var arguments) || arguments.ValueKind == JsonValueKind.Null ? "{}" : arguments.GetRawText();
                    var delta = StreamingJson.ParseToJson(raw);
                    var args = StreamingJson.Parse(raw);
                    var properties = JsonFields.Empty; var signature = GoogleData.String(part, "thoughtSignature");
                    if (!string.IsNullOrEmpty(signature)) properties = properties.Set("thoughtSignature", JsonData.Parse(JsonSerializer.Serialize(signature)));
                    Charge(id.Length + name.Length + delta.Length + (signature?.Length ?? 0));
                    var index = _blocks.Count; var tool = new ToolCallContent(id, name, args, properties);
                    Add(tool); frames.Add(new ToolCallStarted(index, tool)); frames.Add(new ToolCallDelta(index, delta)); frames.Add(new ToolCallEnded(index, tool));
                }
                // Pinned stream ignores inlineData/image output; this adapter likewise does not claim image generation.
            }
        }
        var finish = GoogleData.String(candidate, "finishReason");
        if (!string.IsNullOrEmpty(finish))
        {
            var previousLength = _extra.TryGet("rawStopReason", out var previous) ? previous!.Value.GetString()!.Length : 0;
            Charge(finish.Length - previousLength);
            _extra = _extra.Set("rawStopReason", JsonData.Parse(JsonSerializer.Serialize(finish)));
            _reason = finish switch { "STOP" => _blocks.OfType<ToolCallContent>().Any() ? StopReason.ToolUse : StopReason.Stop,
                "MAX_TOKENS" => StopReason.Length, _ => StopReason.Error };
        }
        if (value.TryGetProperty("usageMetadata", out var usage))
        {
            var cached = Count(usage, "cachedContentTokenCount"); var thought = Count(usage, "thoughtsTokenCount");
            var inputTokens = checked(Count(usage, "promptTokenCount") - cached);
            var outputTokens = checked(Count(usage, "candidatesTokenCount") + thought);
            var total = Count(usage, "totalTokenCount"); var costs = _options.ModelMetadata.Value.GetProperty("cost");
            // Pi abe508 models.ts calculateCost through the shared tier selection; Google reports no cache writes.
            if (costs.TryGetProperty("tiers", out var tiers) && tiers.ValueKind != JsonValueKind.Null && PromptLengthPricing.TrySelect(tiers.EnumerateArray(),
                candidate => candidate.GetProperty("inputTokensAbove").GetDouble(), (double)inputTokens, cached, 0d, out var tier)) costs = tier;
            var i = Cost(costs, "input", inputTokens); var o = Cost(costs, "output", outputTokens);
            var r = Cost(costs, "cacheRead", cached); var w = Cost(costs, "cacheWrite", 0);
            var sum = i + o + r + w; if (!double.IsFinite(sum)) throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
            var binary64 = JsonData.Parse(new JsonObject { ["input"] = i, ["output"] = o, ["cacheRead"] = r, ["cacheWrite"] = w, ["total"] = sum }.ToJsonString());
            _usage = new(inputTokens, outputTokens, cached, 0, total,
                new(binary64.Value.GetProperty("input").GetDecimal(), binary64.Value.GetProperty("output").GetDecimal(),
                    binary64.Value.GetProperty("cacheRead").GetDecimal(), binary64.Value.GetProperty("cacheWrite").GetDecimal(),
                    binary64.Value.GetProperty("total").GetDecimal(), SourceBinary64Cost: binary64),
                JsonFields.Empty.Set("reasoning", JsonData.Parse(thought.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }
        return frames;
    }
    private static long Count(JsonElement usage, string name)
    {
        if (!usage.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return 0;
        if (!value.TryGetInt64(out var n)) throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
        return n;
    }
    private static double Cost(JsonElement costs, string name, long count)
    {
        var result = costs.GetProperty(name).GetDouble() / 1_000_000 * count;
        if (!double.IsFinite(result) || result < 0 || result > (double)decimal.MaxValue)
            throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
        return result;
    }
    internal IReadOnlyList<StreamEvent> EndContent() { var frames = new List<StreamEvent>(); End(frames); return frames; }
    internal StreamTerminalEvent Finish()
    {
        if (_reason == StopReason.Pending) return Error(GoogleData.Fail(GoogleFailure.UnexpectedEof), false);
        if (_reason == StopReason.Error)
        {
            var raw = _extra.TryGet("rawStopReason", out var value) ? value!.Value.GetString() : "";
            return Error(new GoogleGenerativeAIException(GoogleFailure.ProviderError, "Provider stopped with: " + raw), false);
        }
        return new StreamDone(_reason, Message(_reason));
    }
    internal StreamError Error(Exception error, bool aborted)
    {
        FailureKind = error is GoogleGenerativeAIException knownFailure ? knownFailure.Failure :
            error is JsonException ? GoogleFailure.MalformedStream : GoogleFailure.SourceFailed;
        var reason = aborted ? StopReason.Aborted : StopReason.Error;
        // Do not retain arbitrary callback/transport exception text or inner exceptions containing credentials.
        var message = aborted ? "Request was aborted" : error is GoogleGenerativeAIException known ? known.Message :
            error is JsonException ? "Invalid Google streamed JSON." : "Google provider operation failed.";
        var extra = _extra.Set("errorMessage", JsonData.Parse(JsonSerializer.Serialize(message)));
        return new(reason, Message(reason) with { ExtraProperties = extra })
        { NativeDiagnostic = GoogleNativeDiagnostics.FromException(error, aborted) };
    }
}

/// <summary>One bounded native mapping for the Google producer and shared run fallback.</summary>
internal static class GoogleNativeDiagnostics
{
    internal static NativeChatDiagnostic FromCode(NativeChatFailureCode code) =>
        new(NativeChatAdapter.GoogleGenerativeAI, code);

    internal static NativeChatDiagnostic FromException(Exception error, bool aborted = false) =>
        FromCode(aborted ? NativeChatFailureCode.Cancelled : error switch
        {
            GoogleGenerativeAIException known => known.Failure switch
            {
                GoogleFailure.MalformedStream => NativeChatFailureCode.MalformedStream,
                GoogleFailure.UnexpectedEof => NativeChatFailureCode.UnexpectedEof,
                GoogleFailure.ResourceLimit => NativeChatFailureCode.ResourceLimit,
                GoogleFailure.ProviderError => NativeChatFailureCode.ProviderError,
                GoogleFailure.CleanupFailed => NativeChatFailureCode.CleanupFailed,
                // The closed native domain has no credential/configuration code. These fail
                // admission within this explicitly supported provider contract.
                GoogleFailure.Configuration or GoogleFailure.MissingKey or GoogleFailure.UnsupportedValue =>
                    NativeChatFailureCode.UnsupportedFeature,
                _ => NativeChatFailureCode.SourceFailed
            },
            StreamLimitException => NativeChatFailureCode.ResourceLimit,
            JsonException or StreamProtocolException => NativeChatFailureCode.MalformedStream,
            EcmaScriptJsonProjectionException projection => projection.Failure == EcmaScriptJsonProjectionFailure.ResourceLimit
                ? NativeChatFailureCode.ResourceLimit : NativeChatFailureCode.MalformedStream,
            _ => NativeChatFailureCode.SourceFailed
        });
}
