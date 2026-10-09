namespace PiSharp.Contracts;

/// <summary>Immutable compact progress frames; snapshots are obtained from the reducer on demand.</summary>
public abstract record StreamEvent(JsonFields? ExtraProperties = null)
{
    /// <summary>Optional owned producer-facing emission snapshot; compact native wire serialization stays separate.</summary>
    public JsonData? SourceEmissionSnapshot { get; init; }
    /// <summary>Optional owned drain view at the selected provider-batch boundary; ordinary native frames omit it.</summary>
    public JsonData? SourceDrainSnapshot { get; init; }
}
public sealed record StreamStarted(AssistantMessage Partial, JsonFields? Properties = null) : StreamEvent(Properties);
public sealed record TextStarted(int ContentIndex, TextContent Content, JsonFields? Properties = null) : StreamEvent(Properties);
public sealed record TextDelta(int ContentIndex, string Delta, JsonFields? Properties = null) : StreamEvent(Properties);
public sealed record TextEnded(int ContentIndex, string Content, JsonFields? Properties = null) : StreamEvent(Properties);
public sealed record ThinkingStarted(int ContentIndex, ThinkingContent Content, JsonFields? Properties = null) : StreamEvent(Properties);
public sealed record ThinkingDelta(int ContentIndex, string Delta, JsonFields? Properties = null) : StreamEvent(Properties);
/// <summary>Replaces provisional thinking text and signature without closing the block. Native-only progress.</summary>
public sealed record ThinkingCheckpoint(int ContentIndex, string Content, JsonFields? Properties = null) : StreamEvent(Properties);
public sealed record ThinkingEnded(int ContentIndex, string Content, JsonFields? Properties = null) : StreamEvent(Properties);
public sealed record ToolCallStarted(int ContentIndex, ToolCallContent ToolCall, JsonFields? Properties = null) : StreamEvent(Properties);
/// <summary>Indexed wire content can start before its executable ID/name arrives. This is never a final tool call.</summary>
public sealed record ToolCallProvisionalStarted(int ContentIndex, ToolCallContent ToolCall, JsonFields? Properties = null) : StreamEvent(Properties);
/// <summary>Fills previously empty provisional identity; has no corresponding source push or execution authority.</summary>
public sealed record ToolCallHeaderUpdated(int ContentIndex, string Id, string Name, JsonFields? Properties = null) : StreamEvent(Properties);
public sealed record ToolCallCheckpoint(int ContentIndex, string Json, JsonFields? Properties = null) : StreamEvent(Properties);
public sealed record ToolCallDelta(int ContentIndex, string Delta, JsonFields? Properties = null) : StreamEvent(Properties);
/// <summary>
/// Complete authoritative call for an ended content block. Pi Messages may replace
/// its started identity when the receiving reducer explicitly admits that protocol.
/// Progress alone never grants tool execution authority; successful settlement and
/// the normal finalized-tool policy are still required.
/// </summary>
public sealed record ToolCallEnded(int ContentIndex, ToolCallContent ToolCall, JsonFields? Properties = null) : StreamEvent(Properties);
/// <summary>Closes a block the provider never stopped with its final content and no source push (Bedrock finalizeStreamingBlock:
/// a stream can settle without stopping every block; no *_end event is emitted for it). Native-only progress.</summary>
public sealed record ContentBlockFinalized(int ContentIndex, AssistantContent Content, JsonFields? Properties = null) : StreamEvent(Properties);
public abstract record StreamTerminalEvent(StopReason Reason, AssistantMessage Message, JsonFields? Properties = null)
    : StreamEvent(Properties)
{
    public NativeChatDiagnostic? NativeDiagnostic { get; init; }
    public NativeChatDiagnostic? NativeCleanupDiagnostic { get; init; }
    // Native qualification references only. Existing constructors and wire shapes are unchanged.
    [System.Text.Json.Serialization.JsonIgnore] public Exception? NativeSourceException { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public Task? NativeSourceTask { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public IReadOnlyList<Exception>? NativeCleanupExceptions { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public IReadOnlyList<Task>? NativeCleanupTasks { get; init; }
}
public sealed record StreamDone(StopReason Reason, AssistantMessage Message, JsonFields? Properties = null)
    : StreamTerminalEvent(Reason, Message, Properties);
public sealed record StreamError(StopReason Reason, AssistantMessage Message, JsonFields? Properties = null)
    : StreamTerminalEvent(Reason, Message, Properties);

public enum ChatFailureKind { Configuration, Provider, Cancelled, MalformedStream, UnexpectedEof, ResourceLimit }
public sealed record ChatFailure(ChatFailureKind Kind, string Message)
{
    public NativeChatDiagnostic? NativeDiagnostic { get; init; }
}
public sealed record ChatResult(AssistantMessage Message, ChatFailure? Failure = null)
{
    public NativeChatDiagnostic? NativeDiagnostic { get; init; }
    public NativeChatDiagnostic? NativeCleanupDiagnostic { get; init; }
}

public sealed class StreamProtocolException(string message) : Exception(message);
