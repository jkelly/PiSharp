using PiSharp.Contracts;

namespace PiSharp.Agent;

public abstract record AgentEvent;
public sealed record AssistantMessageEnded(AssistantMessage Message) : AgentEvent;
public sealed record ToolExecutionStarted(ToolInvocation Invocation) : AgentEvent;
public sealed record ToolExecutionUpdated(ToolInvocation Invocation, ToolResult PartialResult) : AgentEvent;
public sealed record ToolExecutionEnded(ToolOutcome Outcome) : AgentEvent;
public sealed record ToolResultMessageStarted(ToolResultMessage Message) : AgentEvent;
public sealed record ToolResultMessageEnded(ToolResultMessage Message) : AgentEvent;

/// <summary>Awaited delivery barrier. Source-compatible tool progress calls can overlap; each admitted call is joined before its tool end.</summary>
public interface IAgentEventSink
{
    ValueTask EmitAsync(AgentEvent observation, CancellationToken cancellationToken);
}
