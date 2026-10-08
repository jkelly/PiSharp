// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/types.ts.
using System.Collections.Immutable;

namespace PiSharp.Contracts;

public enum StopReason { Pending, Stop, Length, ToolUse, Error, Aborted, Deferred }

public abstract record AssistantContent(JsonFields? ExtraProperties);
public sealed record TextContent(string Text, JsonFields? Properties = null) : AssistantContent(Properties);
public sealed record ThinkingContent(string Thinking, JsonFields? Properties = null) : AssistantContent(Properties);
public sealed record ToolCallContent(string Id, string Name, JsonData Arguments, JsonFields? Properties = null)
    : AssistantContent(Properties);

public sealed record UsageCost(decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite, decimal Total,
    JsonFields? ExtraProperties = null, JsonData? SourceBinary64Cost = null);
public sealed record TokenUsage(long Input, long Output, long CacheRead, long CacheWrite, long TotalTokens,
    UsageCost Cost, JsonFields? ExtraProperties = null)
{
    public static TokenUsage Zero { get; } = new(0, 0, 0, 0, 0, new(0, 0, 0, 0, 0));
}

/// <summary>Native value contract. Pi wire casing and optional fields are owned by PiWireJson.</summary>
public sealed record AssistantMessage(
    string Api,
    string Provider,
    string Model,
    long Timestamp,
    ImmutableArray<AssistantContent> Content,
    TokenUsage Usage,
    StopReason StopReason,
    JsonFields? ExtraProperties = null)
{
    /// <summary>Milliseconds from Timestamp until the response ended, measured with a monotonic clock by the
    /// chat run that saw the response start. Absent for legacy messages and responses that started elsewhere.</summary>
    public long? DurationMs { get; init; }
}

public sealed record ModelDescriptor(string Id, string Api, string Provider);

/// <summary>Other transcript roles retain their full wire body until their typed native contracts are introduced.</summary>
public sealed record TranscriptEntry(string Role, JsonData WireBody);
