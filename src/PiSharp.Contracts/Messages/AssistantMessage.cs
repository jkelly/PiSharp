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
    JsonFields? ExtraProperties = null);

public sealed record ModelDescriptor(string Id, string Api, string Provider);

/// <summary>Other transcript roles retain their full wire body until their typed native contracts are introduced.</summary>
public sealed record TranscriptEntry(string Role, JsonData WireBody);
