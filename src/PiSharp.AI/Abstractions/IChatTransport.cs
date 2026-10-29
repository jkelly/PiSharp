using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.AI;

public sealed record ChatRequest(ModelDescriptor Model, ImmutableArray<TranscriptEntry> Messages, long Timestamp = 0)
{
    /// <summary>Explicit native control; null retains a direct transport's existing options.</summary>
    public string? ThinkingLevel { get; init; }
    /// <summary>Source StreamOptions.sessionId (agent.sessionId): the session the request belongs to, for prompt-cache keys, session
    /// affinity headers and provider session headers. A transport configured with its own session id keeps it.</summary>
    public string? SessionId { get; init; }
    /// <summary>PiSharp-only: the service tier for this request (<see cref="ServiceTiers"/>: auto, normal, economy, priority, fast,
    /// ultrafast); each protocol maps it to its own field, and protocols without tiers ignore it. Null keeps the transport's own.</summary>
    [System.Diagnostics.CodeAnalysis.Experimental("PISHARP_TIERS001")]
    public string? ServiceTier { get; init; }
}

/// <summary>Explicit per-model native request support, independent of catalog/UI claims.</summary>
public interface IThinkingLevelTransport
{
    ImmutableArray<string> GetSupportedThinkingLevels(ModelDescriptor model);
}

/// <summary>Transport emits normalized immutable progress and a terminal event; it never invokes tools.</summary>
public interface IChatTransport
{
    IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Provider identity and catalog are separate from wire transport.</summary>
public interface IModelProvider
{
    string ProviderId { get; }
    IReadOnlyList<ModelDescriptor> Models { get; }
    IChatTransport Transport { get; }
}
