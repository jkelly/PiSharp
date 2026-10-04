using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.Agent;

/// <summary>The single source createToolResultMessage projection used by Agent and its loop.</summary>
public static class ToolResultMessageMaterializer
{
    public static ToolResultMessage Create(ToolOutcome outcome, ToolResultValueOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var result = outcome.Result;
        var limits = ToolResultValueCodec.ValidateLimits(options ?? ToolResultValueOptions.ExecutionBoundary);
        ToolResultValueCodec.Validate(result, limits);
        var message = new ToolResultMessage(outcome.Invocation.Call.Id, outcome.Invocation.Call.Name,
            result.OwnedContent is ImmutableArray<TextContent> text ? text : [],
            result.Details, outcome.IsError) { Usage = NestedToolUsage.Combine(result.Usage, outcome.NestedUsage), ValueOptions = limits,
                NestedCalls = outcome.NestedCalls };
        if (result.OwnedContent is not ImmutableArray<TextContent>)
            message = message.WithOwnedContent(result.ContentValue);
        return result.HasProperty("details") ? message : message.WithoutDetails();
    }
    public static TranscriptEntry ToTranscript(ToolResultMessage message, long timestamp)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (string.IsNullOrWhiteSpace(message.ToolCallId) || string.IsNullOrWhiteSpace(message.ToolName) ||
            message.ToolCallId.Length > 65_536 || message.ToolName.Length > 65_536 ||
            !ToolResultValueCodec.ScalarText(message.ToolCallId) || !ToolResultValueCodec.ScalarText(message.ToolName))
            throw new InvalidOperationException("Invalid tool-result message identity.");
        var retained = message.OwnedContent is ImmutableArray<TextContent> text
            ? new ToolResult(text, message.Details) { Usage = message.Usage }
            : new ToolResult([], message.Details) { Usage = message.Usage }.WithProperty("content", (JsonData)message.OwnedContent);
        if (!message.HasDetails) retained = retained.WithProperty("details", null);
        if (message.NestedCalls is { } nested) retained = retained.WithProperty("nestedCalls", nested);
        ToolResultValueCodec.Validate(retained, message.ValueOptions);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject(); writer.WriteString("role", "toolResult");
            writer.WriteString("toolCallId", message.ToolCallId); writer.WriteString("toolName", message.ToolName);
            writer.WritePropertyName("content"); writer.WriteRawValue(message.ContentValue.ToString());
            if (message.HasDetails) { writer.WritePropertyName("details"); writer.WriteRawValue(message.Details.ToString()); }
            if (message.Usage is { } usage) { writer.WritePropertyName("usage"); writer.WriteRawValue(usage.ToString()); }
            if (message.NestedCalls is { } calls) { writer.WritePropertyName("nestedCalls"); writer.WriteRawValue(calls.ToString()); }
            writer.WriteBoolean("isError", message.IsError); writer.WriteNumber("timestamp", timestamp); writer.WriteEndObject();
        }
        return new("toolResult", JsonData.Parse(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length))));
    }
}
