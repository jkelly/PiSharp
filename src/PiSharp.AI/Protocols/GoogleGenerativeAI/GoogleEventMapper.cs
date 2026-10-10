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
        // @google/genai generateContentResponseFromMldev/FromVertex keeps only the response fields it knows: a JSON value that is not
        // an object (null, array, string, number) carries none, and a frame's `error` member is dropped (an in-stream error is only
        // raised for a whole read chunk, in the decoder). Neither ends the stream.
        var value = chunk.Value; if (value.ValueKind != JsonValueKind.Object) return [];
        var frames = new List<StreamEvent>();
        var responseId = GoogleData.String(value, "responseId");
        if (!_extra.TryGet("responseId", out _) && !string.IsNullOrEmpty(responseId))
        { Charge(responseId.Length); _extra = _extra.Set("responseId", JsonData.Parse(JsonSerializer.Serialize(responseId))); }
        // google-generative-ai.ts reads `chunk.candidates?.[0]` and iterates `candidate.content.parts` as given (the SDK copies the
        // candidate's content unchanged): a non-array candidates value has no first candidate (an object answers its "0" member),
        // a string parts value iterates characters that carry nothing, any other non-iterable value throws its TypeError, and a
        // part that is not an object carries nothing (null throws on `.text`).
        JsonElement candidate = default;
        if (value.TryGetProperty("candidates", out var candidates))
        {
            if (candidates.ValueKind == JsonValueKind.Array) { if (candidates.GetArrayLength() > 0) candidate = candidates[0]; }
            else if (candidates.ValueKind == JsonValueKind.Object && candidates.TryGetProperty("0", out var first)) candidate = first;
        }
        if (candidate.ValueKind == JsonValueKind.Object && candidate.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.Object && content.TryGetProperty("parts", out var parts) &&
            Truthy(parts) && parts.ValueKind != JsonValueKind.String)
        {
            if (parts.ValueKind != JsonValueKind.Array)
                throw new GoogleGenerativeAIException(GoogleFailure.MalformedStream, "candidate.content.parts is not iterable");
            foreach (var part in parts.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Null)
                    throw new GoogleGenerativeAIException(GoogleFailure.MalformedStream, "Cannot read properties of null (reading 'text')");
                if (part.ValueKind != JsonValueKind.Object) continue;
                if (part.TryGetProperty("text", out var textValue))
                {
                    // `part.text !== undefined`: JSON null and other values join the block as JavaScript strings (`text += part.text`).
                    var text = ProviderShared.ProviderErrorText.JsString(textValue); var thinking = GoogleData.True(part, "thought");
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
                // google-generative-ai.ts: `if (part.functionCall)` (any truthy value), name = functionCall.name || "", id = the truthy
                // functionCall.id unless a block already has it, else `${functionCall.name}_${Date.now()}_${++counter}` (the raw name,
                // so a missing one reads "undefined"), arguments = functionCall.args ?? {}.
                if (part.TryGetProperty("functionCall", out var call) && Truthy(call))
                {
                    End(frames);
                    JsonElement? Field(string field) => call.ValueKind == JsonValueKind.Object && call.TryGetProperty(field, out var found) ? found : null;
                    var nameValue = Field("name");
                    var name = nameValue is { } named && Truthy(named) ? ProviderShared.ProviderErrorText.JsString(named) : "";
                    // A nameless call (name "", missing, or a non-object functionCall) is pushed as upstream pushes it, with name "" and an id
                    // "_<ms>_<n>" or "undefined_<ms>_<n>" (owner decision 13); the agent answers it with "Tool  not found".
                    var provided = Field("id") is { } given && Truthy(given) ? ProviderShared.ProviderErrorText.JsString(given) : null;
                    var id = provided is null || _blocks.OfType<ToolCallContent>().Any(x => x.Id == provided)
                        ? (nameValue is { } rawName ? ProviderShared.ProviderErrorText.JsString(rawName) : "undefined") + "_" + _request.Timestamp + "_" +
                            Interlocked.Increment(ref _toolCounter)
                        : provided;
                    // google-generative-ai.ts: arguments = part.functionCall.args ?? {} (whatever JSON value the chunk carried), and the
                    // delta is JSON.stringify(arguments).
                    var raw = Field("args") is { ValueKind: not JsonValueKind.Null } arguments ? arguments.GetRawText() : "{}";
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
                JsonFields.Empty.Set("reasoning", JsonData.Parse(thought.ToString(System.Globalization.CultureInfo.InvariantCulture)))) { ExtrasBeforeTotal = true };
        }
        return frames;
    }
    /// <summary>JavaScript truthiness of a parsed JSON value.</summary>
    private static bool Truthy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined => false,
        JsonValueKind.String => value.GetString()!.Length != 0,
        JsonValueKind.Number => value.GetDouble() != 0,
        _ => true
    };
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
        // google-vertex.ts names its provider in this text; google-generative-ai.ts does not.
        if (_reason == StopReason.Pending) return Error(_request.Model.Api == "google-vertex"
            ? new GoogleGenerativeAIException(GoogleFailure.UnexpectedEof, "Google Vertex stream ended without a finish reason")
            : GoogleData.Fail(GoogleFailure.UnexpectedEof), false);
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
