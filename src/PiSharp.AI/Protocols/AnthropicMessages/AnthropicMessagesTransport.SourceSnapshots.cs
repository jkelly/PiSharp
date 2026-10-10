using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.AnthropicMessages;

public sealed partial class AnthropicMessagesTransport
{
    private sealed partial class State
    {
        // Observation only: never feed preview arguments or source scratch fields
        // into the native reducer or final-tool admission. DTO processing emits
        // at most one frame, so its completed state is this emission's state.
        public StreamEvent CaptureEmission(StreamEvent frame)
        {
            if (!_options.CaptureSourceEmissionSnapshots) return frame;
            try
            {
                if (frame is StreamTerminalEvent terminal)
                    return frame with { SourceEmissionSnapshot = WithOriginalCost(AnthropicSourceEventProjection.WriteTerminal(terminal),
                        terminal is StreamDone ? "message" : "error", terminal.Message.Usage) };
                var snapshot = _reducer.Snapshot();
                var content = snapshot.Content.ToBuilder();
                foreach (var pair in _slots)
                {
                    var slot = pair.Value;
                    var block = content[slot.Index];
                    var properties = block.ExtraProperties ?? JsonFields.Empty;
                    if (!slot.Ended)
                        properties = properties.Set("index", JsonData.Parse(pair.Key.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    if (block is ThinkingContent thinking)
                        block = new ThinkingContent(thinking.Thinking, properties.Set("thinkingSignature", TextData(slot.Signature.ToString())));
                    else if (block is ToolCallContent tool)
                    {
                        if (!slot.Ended)
                        {
                            var raw = _reducer.GetToolJsonPreview(slot.Index);
                            properties = properties.Set("partialJson", TextData(raw));
                            // Initial input survives until the first input_json_delta.
                            // Empty fragments still replace input with {} in Pi.
                            var arguments = _sourceToolDeltaIndices.Contains(slot.Index)
                                ? StreamingJson.Parse(raw)
                                : tool.Arguments;
                            block = new ToolCallContent(tool.Id, tool.Name, arguments, properties);
                        }
                        else block = new ToolCallContent(tool.Id, tool.Name, tool.Arguments, properties);
                    }
                    else if (block is TextContent text) block = new TextContent(text.Text, properties);
                    content[slot.Index] = block;
                }
                var partial = snapshot with { Content = content.ToImmutable(), Usage = _usage, StopReason = _reason, ExtraProperties = _properties };
                // Source starts have no direct payload: the writer validates their
                // source-shaped indexed block without changing the emitted native frame.
                StreamEvent projection = frame switch
                {
                    StreamStarted => new StreamStarted(partial, frame.ExtraProperties),
                    TextStarted started => new TextStarted(started.ContentIndex, (TextContent)partial.Content[started.ContentIndex], frame.ExtraProperties),
                    ThinkingStarted started => new ThinkingStarted(started.ContentIndex, (ThinkingContent)partial.Content[started.ContentIndex], frame.ExtraProperties),
                    ToolCallStarted started => new ToolCallStarted(started.ContentIndex, (ToolCallContent)partial.Content[started.ContentIndex], frame.ExtraProperties),
                    _ => frame
                };
                return frame with { SourceEmissionSnapshot = WithOriginalCost(PiWireJson.WriteSourceEvent(projection, partial), "partial", partial.Usage) };
            }
            catch (JsonException) { return frame; }
            // Absence is an explicit unsupported observation, never a fabricated
            // empty snapshot or a change to native stream failure/cancellation.
        }

        private readonly HashSet<int> _sourceToolDeltaIndices = [];

        private JsonData WithOriginalCost(JsonData sourceEvent, string messageField, TokenUsage usage)
        {
            // Replace only the owned observational JSON after normal typed serialization.
            // Never attach a disagreeing raw cost to native UsageCost.SourceBinary64Cost.
            // ReadUsage can reject after modifying scratch _cacheWrite1h. Use the
            // cache split committed with this exact usage, including failure terminals.
            var cacheWrite1h = usage.ExtraProperties is { } properties && properties.TryGet("cacheWrite1h", out var count)
                ? count!.Value.GetInt64() : 0;
            var node = JsonNode.Parse(sourceEvent.ToString(), documentOptions: PiSharp.Contracts.JsonData.DocumentOptions)!;
            node[messageField]!["usage"]!["cost"] = JsonNode.Parse(
                OriginalAnthropicUsageCostProjection.Create(_rates, usage, cacheWrite1h).ToString(), documentOptions: PiSharp.Contracts.JsonData.DocumentOptions);
            return JsonData.Parse(node.ToJsonString());
        }
    }
}
