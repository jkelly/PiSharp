// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/bedrock-converse-stream.ts (handleContentBlockStart,
// handleContentBlockDelta, handleContentBlockStop, handleMetadata, mapStopReason, flushRedactedContent, finalizeStreamingBlock) and
// models.ts (calculateCost).
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.Bedrock;

/// <summary>One stream's converter from decoded ConverseStream events to native frames. Never executes tools.</summary>
internal sealed class BedrockEventState
{
    private const string RedactedPlaceholder = "[Reasoning redacted]";
    private sealed class Block
    {
        internal required string Kind;
        internal int? BedrockIndex;
        internal readonly StringBuilder Text = new();
        internal readonly StringBuilder Signature = new();
        internal bool Redacted;
        internal List<byte[]>? RedactedChunks;
        internal string Id = "", Name = "";
        internal readonly StringBuilder PartialJson = new();
        internal bool Ended;
    }

    private readonly BedrockModel _model;
    private readonly ChatRequest _request;
    private readonly AssistantStreamReducer _reducer;
    private readonly List<Block> _blocks = [];
    private StopReason _stopReason = StopReason.Pending;
    private string? _rawStopReason, _stopError;
    private TokenUsage _usage = TokenUsage.Zero;

    internal BedrockEventState(BedrockModel model, ChatRequest request, BedrockConverseOptions options)
    {
        _model = model; _request = request;
        _reducer = new(Initial(), new(options.MaximumContentBlocks, options.MaximumResponseCharacters));
    }

    internal bool Started => _reducer.HasStarted;

    private AssistantMessage Initial() => new(_model.Descriptor.Api, _model.Descriptor.Provider, _model.Id, _request.Timestamp, [], TokenUsage.Zero, StopReason.Pending);

    internal StreamEvent Start()
    {
        var start = new StreamStarted(Initial());
        _reducer.Apply(start); return start;
    }

    private void Emit(StreamEvent frame, List<StreamEvent> output) { _reducer.Apply(frame); output.Add(frame); }

    internal void Handle(AwsEventStreamMessage message, List<StreamEvent> output)
    {
        var messageType = message.HeaderString(":message-type");
        if (messageType == "error")
            throw new AwsEventStreamProtocolError(message.HeaderString(":error-code") ?? "UnknownError", message.HeaderString(":error-message") ?? "UnknownError");
        var payload = message.Payload.Length == 0 ? new JsonObject() : JsonNode.Parse(message.Payload) as JsonObject ?? new JsonObject();
        if (messageType == "exception")
        {
            // A modeled exception member (internalServerException, throttlingException, ...) is thrown with its shape name.
            var type = message.HeaderString(":exception-type") ?? "UnknownError";
            var name = type.Length == 0 ? type : char.ToUpperInvariant(type[0]) + type[1..];
            var text = (payload["message"] ?? payload["Message"]) is JsonValue value && value.TryGetValue<string>(out var parsed) ? parsed : "UnknownError";
            throw new BedrockServiceException(name, text);
        }
        if (messageType != "event") return;
        switch (message.HeaderString(":event-type"))
        {
            case "messageStart":
                if (Text(payload, "role") != "assistant") throw new InvalidOperationException("Unexpected assistant message start but got user message start instead");
                if (!_reducer.HasStarted) output.Add(Start());
                break;
            case "contentBlockStart":
                EnsureStarted(output);
                if (payload["start"]?["toolUse"] is JsonObject toolUse)
                {
                    var block = new Block { Kind = "toolCall", BedrockIndex = Index(payload), Id = Text(toolUse, "toolUseId") ?? "", Name = Text(toolUse, "name") ?? "" };
                    _blocks.Add(block);
                    Emit(new ToolCallStarted(_blocks.Count - 1, new(block.Id, block.Name, JsonData.EmptyObject)), output);
                }
                break;
            case "contentBlockDelta":
                EnsureStarted(output);
                Delta(Index(payload), payload["delta"] as JsonObject, output);
                break;
            case "contentBlockStop":
            {
                var index = _blocks.FindIndex(block => block.BedrockIndex == Index(payload));
                if (index < 0) break;
                var block = _blocks[index]; block.BedrockIndex = null;
                End(index, output);
                break;
            }
            case "messageStop":
                _rawStopReason = Text(payload, "stopReason");
                (_stopReason, _stopError) = _rawStopReason switch
                {
                    "end_turn" or "stop_sequence" => (StopReason.Stop, (string?)null),
                    "max_tokens" or "model_context_window_exceeded" => (StopReason.Length, null),
                    "tool_use" => (StopReason.ToolUse, null),
                    null => (StopReason.Error, null),
                    var other => (StopReason.Error, "Provider stopped with: " + other)
                };
                break;
            case "metadata":
                if (payload["usage"] is JsonObject usage) Usage(usage);
                break;
        }
    }

    private void EnsureStarted(List<StreamEvent> output) { if (!_reducer.HasStarted) output.Add(Start()); }

    private void Delta(int index, JsonObject? delta, List<StreamEvent> output)
    {
        var position = _blocks.FindIndex(block => block.BedrockIndex == index);
        var block = position >= 0 ? _blocks[position] : null;
        if (delta?["text"] is JsonValue textValue && textValue.TryGetValue<string>(out var text))
        {
            // Text blocks have no contentBlockStart; the first delta opens one.
            if (block is null)
            {
                block = new Block { Kind = "text", BedrockIndex = index }; _blocks.Add(block); position = _blocks.Count - 1;
                Emit(new TextStarted(position, new("")), output);
            }
            if (block.Kind == "text" && !block.Ended) { block.Text.Append(text); Emit(new TextDelta(position, text), output); }
        }
        else if (delta?["toolUse"] is JsonObject toolUse && block is { Kind: "toolCall", Ended: false })
        {
            var input = Text(toolUse, "input") ?? "";
            block.PartialJson.Append(input);
            Emit(new ToolCallDelta(position, input), output);
        }
        else if (delta?["reasoningContent"] is JsonObject reasoning)
        {
            if (block is null)
            {
                block = new Block { Kind = "thinking", BedrockIndex = index }; _blocks.Add(block); position = _blocks.Count - 1;
                Emit(new ThinkingStarted(position, new("", Signature(""))), output);
            }
            if (block.Kind != "thinking" || block.Ended) return;
            if (Text(reasoning, "text") is { Length: > 0 } thinking) { block.Text.Append(thinking); Emit(new ThinkingDelta(position, thinking), output); }
            // A signature or an opaque redacted payload, never both.
            if (Text(reasoning, "signature") is { Length: > 0 } signature && !block.Redacted) block.Signature.Append(signature);
            if (Text(reasoning, "redactedContent") is { Length: > 0 } redacted)
            {
                if (!block.Redacted)
                {
                    block.Redacted = true; block.Signature.Clear();
                    block.Text.Append(RedactedPlaceholder); Emit(new ThinkingDelta(position, RedactedPlaceholder), output);
                }
                (block.RedactedChunks ??= []).Add(Convert.FromBase64String(redacted));
            }
        }
    }

    private void End(int index, List<StreamEvent> output)
    {
        var block = _blocks[index];
        if (block.Ended) return;
        Flush(block); block.Ended = true;
        switch (block.Kind)
        {
            case "text": Emit(new TextEnded(index, block.Text.ToString()), output); break;
            case "thinking": Emit(new ThinkingEnded(index, block.Text.ToString(), ThinkingProperties(block)), output); break;
            case "toolCall": Emit(new ToolCallEnded(index, new(block.Id, block.Name, Arguments(block))), output); break;
        }
    }

    private static void Flush(Block block)
    {
        if (block.RedactedChunks is not { } chunks) return;
        block.Signature.Clear().Append(Convert.ToBase64String(chunks.SelectMany(chunk => chunk).ToArray()));
        block.RedactedChunks = null;
    }

    // bedrock-converse-stream.ts: block.arguments = parseStreamingJson(block.partialJson), whatever JSON value that is.
    private static JsonData Arguments(Block block) => StreamingJson.Parse(block.PartialJson.ToString());

    private static JsonFields Signature(string value) => JsonFields.Empty.Set("thinkingSignature", JsonData.Parse(JsonSerializer.Serialize(value)));
    private static JsonFields ThinkingProperties(Block block)
    {
        var fields = Signature(block.Signature.ToString());
        return block.Redacted ? fields.Set("redacted", JsonData.Parse("true")) : fields;
    }

    private void Usage(JsonObject usage)
    {
        long Count(string name) => usage[name] is JsonValue value && value.TryGetValue<long>(out var count) ? count : 0;
        long input = Count("inputTokens"), output = Count("outputTokens"), read = Count("cacheReadInputTokens"), write = Count("cacheWriteInputTokens");
        long longWrite = 0;
        foreach (var detail in (usage["cacheDetails"] as JsonArray)?.OfType<JsonObject>() ?? [])
            if (Text(detail, "ttl") == "1h" && detail["inputTokens"] is JsonValue tokens && tokens.TryGetValue<long>(out var count)) longWrite += count;
        var total = Count("totalTokens"); if (total == 0) total = input + output;
        // calculateCost: the prompt-length tier prices the whole request; 1h cache writes cost twice the input rate.
        var cost = _model.Cost;
        var rates = PromptLengthPricing.Select(cost.Input, cost.Output, cost.CacheRead, cost.CacheWrite, cost.Tiers, input, read, write);
        var costInput = rates.Input / 1_000_000m * input; var costOutput = rates.Output / 1_000_000m * output;
        var costRead = rates.CacheRead / 1_000_000m * read; var costWrite = (rates.CacheWrite * (write - longWrite) + rates.Input * 2 * longWrite) / 1_000_000m;
        var extras = usage.ContainsKey("cacheDetails")
            ? JsonFields.Empty.Set("cacheWrite1h", JsonData.Parse(longWrite.ToString(CultureInfo.InvariantCulture))) : null;
        _usage = new(input, output, read, write, total, new(costInput, costOutput, costRead, costWrite, costInput + costOutput + costRead + costWrite), extras);
    }

    /// <summary>The successful settlement: a stop reason is required and every open block is finalized.</summary>
    internal StreamTerminalEvent Complete(List<StreamEvent> output)
    {
        if (_stopReason == StopReason.Pending) throw new InvalidOperationException("Bedrock stream ended without a stop reason");
        if (_stopReason == StopReason.Error) throw new InvalidOperationException(_stopError ?? "An unknown error occurred");
        EnsureStarted(output);
        // Source finalizeStreamingBlock: blocks that never received contentBlockStop are finalized (redacted content flushed, the
        // streamed arguments kept) without an end event.
        for (var index = 0; index < _blocks.Count; index++)
        {
            var block = _blocks[index];
            if (block.Ended) continue;
            Flush(block); block.Ended = true;
            Emit(new ContentBlockFinalized(index, block.Kind switch
            {
                "text" => new TextContent(block.Text.ToString()),
                "thinking" => new ThinkingContent(block.Text.ToString(), ThinkingProperties(block)),
                _ => new ToolCallContent(block.Id, block.Name, Arguments(block))
            }), output);
        }
        var properties = JsonFields.Empty;
        if (_rawStopReason is not null) properties = properties.Set("rawStopReason", JsonData.Parse(JsonSerializer.Serialize(_rawStopReason)));
        var message = _reducer.Snapshot() with { Usage = _usage, StopReason = _stopReason, ExtraProperties = properties };
        var done = new StreamDone(_stopReason, message);
        _reducer.Apply(done);
        return done;
    }

    internal StreamError Fail(Exception error, bool aborted, string errorMessage, JsonObject? diagnostic)
    {
        var reason = aborted ? StopReason.Aborted : StopReason.Error;
        var content = ImmutableArray.CreateBuilder<AssistantContent>();
        foreach (var block in _blocks)
        {
            Flush(block);
            content.Add(block.Kind switch
            {
                "text" => new TextContent(block.Text.ToString()),
                "thinking" => new ThinkingContent(block.Text.ToString(), ThinkingProperties(block)),
                _ => new ToolCallContent(block.Id, block.Name, Arguments(block))
            });
        }
        var properties = JsonFields.Empty;
        if (_rawStopReason is not null) properties = properties.Set("rawStopReason", JsonData.Parse(JsonSerializer.Serialize(_rawStopReason)));
        properties = properties.Set("errorMessage", JsonData.Parse(JsonSerializer.Serialize(errorMessage)));
        if (diagnostic is not null)
            properties = properties.Set("diagnostics", JsonData.Parse(new JsonArray(new JsonObject
            { ["type"] = "bedrock_response_failure", ["timestamp"] = _request.Timestamp, ["details"] = diagnostic }).ToJsonString()));
        var message = Initial() with { Content = content.ToImmutable(), Usage = _usage, StopReason = reason, ExtraProperties = properties };
        return new StreamError(reason, message)
        { NativeSourceException = error };
    }

    private static int Index(JsonObject payload) => payload["contentBlockIndex"] is JsonValue value && value.TryGetValue<int>(out var index) ? index : 0;
    private static string? Text(JsonObject? node, string name) => node?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
