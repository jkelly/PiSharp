using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.AnthropicMessages;

/// <summary>Owned status/header snapshot. The callback never owns the physical response.</summary>
public sealed record AnthropicMessagesResponseInfo(int Status, ImmutableDictionary<string,string> Headers);

/// <summary>Optional awaited observations. Provider DTOs are immutable native values.</summary>
public sealed record AnthropicMessagesHooks(
    Func<AnthropicMessagesResponseInfo,ModelDescriptor,CancellationToken,Task>? OnResponse = null,
    Func<JsonData,ModelDescriptor,CancellationToken,Task>? OnProviderStreamEvent = null)
{
    /// <summary>Inspect an owned pre-encoding payload. Null keeps it; an owned object replaces it.
    /// Available only with the structured prepared-request transport route.</summary>
    public Func<JsonData,ModelDescriptor,CancellationToken,Task<JsonData?>>? OnPayload { get; init; }
}

/// <summary>A synchronous cancellation throw is a source fault; no callback Task was returned.</summary>
public sealed class AnthropicMessagesHookInvocationException(string hook,OperationCanceledException cause)
    : Exception("Anthropic hook threw cancellation before returning a task.",cause)
{
    public string Hook { get; } = hook;
}
