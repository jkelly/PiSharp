// Event conversion follows Pi d86654abb8862e201933517d6f1fce9f88dd117f
// packages/ai/src/api/pi-messages.ts createEventConverter/createErrorEvent (MIT).
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.PiMessages;

/// <summary>One invocation's indexed converter. Final arguments are authoritative; no tools execute here.</summary>
public sealed class PiMessagesEventMapper
{
    private readonly ChatRequest _request;
    private readonly PiMessagesOptions _options;
    private readonly JsonObject _partial;
    private readonly JsonArray _content = new();
    private readonly Dictionary<int, string> _toolJson = [];
    private readonly HashSet<string> _undefined = new(StringComparer.Ordinal);
    private readonly AssistantStreamReducer _reducer;
    private bool _started, _terminal;
    public PiMessagesEventMapper(ChatRequest request, PiMessagesOptions options)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(options); options.Validate();
        if (request.Model.Api != "pi-messages") throw PiMessagesData.Fail(PiMessagesFailure.InvalidRequest);
        _request = request; _options = options;
        var initial = new AssistantMessage(request.Model.Api, request.Model.Provider, request.Model.Id, request.Timestamp, [], TokenUsage.Zero, StopReason.Pending);
        _partial = new JsonObject { ["role"] = "assistant", ["content"] = _content, ["api"] = request.Model.Api, ["provider"] = request.Model.Provider,
            ["model"] = request.Model.Id, ["usage"] = JsonNode.Parse(PiWireJson.WriteMessage(initial).ToString())!["usage"]!.DeepClone(),
            ["stopReason"] = "pending", ["timestamp"] = request.Timestamp };
        _reducer = new(initial, new(options.MaximumContentSlots, options.MaximumContentCharacters), allowPiMessagesIdentityReplacement: true);
    }
    public PiMessagesValueObservation Current => new(Own(_partial), _undefined.Order(StringComparer.Ordinal).ToImmutableArray());
    public bool IsTerminal => _terminal;
    public StreamEvent Convert(JsonData dto)
    {
        try { return ConvertCore(dto); }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        { throw new PiMessagesException(PiMessagesFailure.MalformedStream, error.Message); }
    }
    private StreamEvent ConvertCore(JsonData dto)
    {
        if (_terminal) throw PiMessagesData.Fail(PiMessagesFailure.MalformedStream);
        var value = PiMessagesData.Admit(dto, _options).Value; var type = PiMessagesData.String(value, "type");
        StreamEvent frame;
        if (type == "start")
        {
            if (_started) throw PiMessagesData.Fail(PiMessagesFailure.UnsupportedContent);
            _started = true; frame = new StreamStarted(Message());
        }
        else if (type is "done" or "error")
        {
            var reason = PiWireJson.ReadStopReason(PiMessagesData.String(value, "reason"));
            if (type == "done" && reason is not (StopReason.Stop or StopReason.Length or StopReason.ToolUse) ||
                type == "error" && reason is not (StopReason.Error or StopReason.Aborted)) throw PiMessagesData.Fail(PiMessagesFailure.MalformedStream);
            _partial["stopReason"] = PiWireJson.StopReasonName(reason); _partial["usage"] = Node(value.GetProperty("usage"));
            Optional(_partial, value, "responseId", "responseId", "/responseId");
            if (type == "error") Optional(_partial, value, "errorMessage", "errorMessage", "/errorMessage");
            if (value.TryGetProperty("providerThinkingLevel", out var thinking)) _partial["providerThinkingLevel"] = Node(thinking);
            if (value.TryGetProperty("rewrite", out var rewrite) && rewrite.ValueKind != JsonValueKind.Null)
                Diagnostic(new JsonObject { ["type"] = "pi_messages_rewrite", ["timestamp"] = _request.Timestamp, ["details"] = Node(rewrite) });
            var message = Message(); frame = type == "done" ? new StreamDone(reason, message) : new StreamError(reason, message)
            { NativeDiagnostic = new(NativeChatAdapter.PiMessages, reason == StopReason.Aborted ?
                NativeChatFailureCode.Cancelled : NativeChatFailureCode.ProviderError) }; _terminal = true;
        }
        else
        {
            if (!_started) throw PiMessagesData.Fail(PiMessagesFailure.UnsupportedContent);
            var index = value.GetProperty("contentIndex").GetInt32();
            if (index < 0 || index >= _options.MaximumContentSlots) throw PiMessagesData.Fail(PiMessagesFailure.ResourceLimit);
            switch (type)
            {
                case "text_start":
                    Add(index, new JsonObject { ["type"] = "text", ["text"] = "" }); frame = new TextStarted(index, new("")); break;
                case "thinking_start":
                    Add(index, new JsonObject { ["type"] = "thinking", ["thinking"] = "" }); frame = new ThinkingStarted(index, new("")); break;
                case "toolcall_start":
                    var id = PiMessagesData.String(value, "id"); var name = PiMessagesData.String(value, "toolName");
                    Add(index, new JsonObject { ["type"] = "toolCall", ["id"] = id, ["name"] = name, ["arguments"] = new JsonObject() });
                    _toolJson.Add(index, ""); frame = new ToolCallStarted(index, new(id, name, JsonData.EmptyObject)); break;
                case "text_delta":
                    var textDelta = PiMessagesData.String(value, "delta"); var text = Block(index, "text"); text["text"] = text["text"]!.GetValue<string>() + textDelta;
                    frame = new TextDelta(index, textDelta); break;
                case "thinking_delta":
                    var thinkingDelta = PiMessagesData.String(value, "delta"); var block = Block(index, "thinking"); block["thinking"] = block["thinking"]!.GetValue<string>() + thinkingDelta;
                    frame = new ThinkingDelta(index, thinkingDelta); break;
                case "toolcall_delta":
                    var delta = PiMessagesData.String(value, "delta"); var call = Block(index, "toolCall"); var raw = _toolJson[index] + delta;
                    call["arguments"] = JsonNode.Parse(StreamingJson.Parse(raw).ToString()); _toolJson[index] = raw; frame = new ToolCallDelta(index, delta); break;
                case "text_end":
                    var endedText = Block(index, "text"); endedText["text"] = PiMessagesData.String(value, "content");
                    Optional(endedText, value, "contentSignature", "textSignature", "/content/" + index + "/textSignature");
                    frame = new TextEnded(index, endedText["text"]!.GetValue<string>(), PiWireJson.ReadContent(Own(endedText).Value).ExtraProperties); break;
                case "thinking_end":
                    var endedThinking = Block(index, "thinking"); endedThinking["thinking"] = PiMessagesData.String(value, "content");
                    Optional(endedThinking, value, "contentSignature", "thinkingSignature", "/content/" + index + "/thinkingSignature");
                    Optional(endedThinking, value, "redacted", "redacted", "/content/" + index + "/redacted");
                    frame = new ThinkingEnded(index, endedThinking["thinking"]!.GetValue<string>(), PiWireJson.ReadContent(Own(endedThinking).Value).ExtraProperties); break;
                case "toolcall_end":
                    var endedCall = Block(index, "toolCall"); var finalCall = value.GetProperty("toolCall");
                    if (finalCall.ValueKind != JsonValueKind.Object) throw PiMessagesData.Fail(PiMessagesFailure.MalformedStream);
                    // Pinned Pi uses Object.assign: the final arguments are any JSON value it carries, or the streamed
                    // parseStreamingJson value when it carries none. Merge into a detached candidate so
                    // an invalid type/identity cannot overwrite the accepted partial.
                    var mergedCall = endedCall.DeepClone().AsObject();
                    foreach (var property in finalCall.EnumerateObject()) mergedCall[property.Name] = Node(property.Value);
                    var mergedValue = Own(mergedCall).Value;
                    if (PiMessagesData.String(mergedValue, "type") != "toolCall")
                        throw PiMessagesData.Fail(PiMessagesFailure.MalformedStream);
                    var authoritativeCall = (ToolCallContent)PiWireJson.ReadContent(mergedValue);
                    frame = new ToolCallEnded(index, authoritativeCall);
                    // Final identity, arguments, duplicate IDs, live block and content
                    // limits are validated before the source partial accepts replacement.
                    _reducer.Apply(frame); _content[index] = mergedCall; _toolJson.Remove(index);
                    return Capture(frame, value, type);
                default: throw PiMessagesData.Fail(PiMessagesFailure.UnsupportedContent);
            }
        }
        _reducer.Apply(frame); return Capture(frame, value, type);
    }
    public StreamError Error(Exception error, bool aborted) => Error(error, aborted, null);
    internal StreamError Error(Exception error, bool aborted, NativeChatFailureCode? nativeCode)
    {
        var reason = aborted ? StopReason.Aborted : StopReason.Error;
        var properties = JsonFields.Empty.Set("errorMessage", JsonData.Parse(JsonSerializer.Serialize(error.Message)));
        var message = new AssistantMessage(_request.Model.Api, _request.Model.Provider, _request.Model.Id, _request.Timestamp, [], TokenUsage.Zero, reason, properties);
        if (!aborted && error is PiMessagesException { DiagnosticDetails: { } details } responseError)
        {
            var diagnosticError = new JsonObject { ["name"] = "PiMessagesResponseError", ["message"] = error.Message };
            if (responseError.Code is { } code) diagnosticError["code"] = code;
            var diagnostics = new JsonArray(new JsonObject { ["type"] = "pi_messages_response_failure", ["timestamp"] = _request.Timestamp,
                ["error"] = diagnosticError, ["details"] = JsonNode.Parse(details.ToString()) });
            message = message with { ExtraProperties = properties.Set("diagnostics", Own(diagnostics)) };
        }
        var diagnostic = nativeCode is { } failureCode ? new NativeChatDiagnostic(NativeChatAdapter.PiMessages, failureCode) :
            PiMessagesData.Diagnostic(error, aborted);
        var frame = new StreamError(reason, message) { NativeDiagnostic = diagnostic }; _terminal = true;
        try { return (StreamError)CaptureError(frame); }
        catch (PiMessagesException errorLimit) when (errorLimit.Failure == PiMessagesFailure.ResourceLimit)
        {
            // A bounded error path must still settle when an exception/diagnostic itself exceeds the envelope limit.
            var bounded = message with { ExtraProperties = JsonFields.Empty.Set("errorMessage", JsonData.Parse("\"Pi Messages error exceeds configured limits.\"")) };
            return new StreamError(reason, bounded) { NativeDiagnostic = diagnostic };
        }
    }
    private StreamEvent Capture(StreamEvent frame, JsonElement original, string type)
    {
        if (!_options.CaptureSourceSnapshots) return frame;
        JsonObject value; var member = type == "done" ? "message" : type == "error" ? "error" : "partial";
        if (type is "done" or "error") value = new JsonObject { ["type"] = type, ["reason"] = _partial["stopReason"]!.DeepClone(), [member] = _partial.DeepClone() };
        else
        {
            value = Node(original)!.AsObject(); value["partial"] = _partial.DeepClone();
            if (type == "toolcall_end") value["toolCall"] = _content[original.GetProperty("contentIndex").GetInt32()]!.DeepClone();
        }
        var paths = _undefined.Select(p => "/" + member + p).Order(StringComparer.Ordinal).ToArray();
        return frame with { SourceEmissionSnapshot = Own(new JsonObject { ["value"] = value, ["ownUndefinedPaths"] = JsonSerializer.SerializeToNode(paths) }) };
    }
    private StreamEvent CaptureError(StreamError frame) => !_options.CaptureSourceSnapshots ? frame : frame with
    { SourceEmissionSnapshot = Own(new JsonObject { ["value"] = new JsonObject { ["type"] = "error", ["reason"] = PiWireJson.StopReasonName(frame.Reason), ["error"] = JsonNode.Parse(PiWireJson.WriteMessage(frame.Message).ToString()) }, ["ownUndefinedPaths"] = new JsonArray() }) };
    private void Add(int index, JsonObject block) { if (index != _content.Count) throw PiMessagesData.Fail(PiMessagesFailure.UnsupportedContent); _content.Add(block); }
    private JsonObject Block(int index, string type)
    { if (index >= _content.Count || _content[index] is not JsonObject block || block["type"]?.GetValue<string>() != type) throw PiMessagesData.Fail(PiMessagesFailure.MalformedStream); return block; }
    private void Optional(JsonObject target, JsonElement source, string from, string to, string path)
    { if (source.TryGetProperty(from, out var field)) { target[to] = Node(field); _undefined.Remove(path); } else { target.Remove(to); _undefined.Add(path); } }
    private void Diagnostic(JsonObject diagnostic) { if (_partial["diagnostics"] is not JsonArray values) { values = new(); _partial["diagnostics"] = values; } values.Add(diagnostic); }
    private AssistantMessage Message() => PiWireJson.ReadMessage(Own(_partial).Value);
    private static JsonNode? Node(JsonElement value) => JsonNode.Parse(value.GetRawText());
    private JsonData Own(JsonNode value) => PiMessagesData.Admit(JsonData.Parse(value.ToJsonString()), _options);
}
