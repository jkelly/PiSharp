using PiSharp.Contracts;

namespace PiSharp.Agent;

/// <summary>Immutable normalized chat observation, including its terminal frame; not a transcript commit.</summary>
public sealed record TurnStreamObserved(StreamEvent Event) : AgentEvent;

/// <summary>One explicit chat request and tool batch. Continuation is a hint, not another request.</summary>
public sealed record TurnResult(ChatResult Chat, ToolBatchResult Tools, ChatFailure? CleanupFailure = null);
