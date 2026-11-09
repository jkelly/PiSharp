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
    /// <summary>Send a full independent request without consuming or updating provider continuation state.
    /// Cache warming and other replay-only requests set this flag.</summary>
    public bool DisableContinuation { get; init; }
    /// <summary>PiSharp-only: the service tier for this request (<see cref="ServiceTiers"/>: auto, normal, economy, priority, fast,
    /// ultrafast); each protocol maps it to its own field, and protocols without tiers ignore it. Null keeps the transport's own.</summary>
    [System.Diagnostics.CodeAnalysis.Experimental("PISHARP_TIERS001")]
    public string? ServiceTier { get; init; }
    /// <summary>Source StreamOptions.identity (identity.ts): how the request identifies the calling app, merged field by field
    /// over the transport's own identity. Null keeps the transport's (by default a detailed User-Agent and no attribution).</summary>
    [System.Diagnostics.CodeAnalysis.Experimental("PISHARP_IDENTITY001")]
    public ClientIdentity? Identity { get; init; }
    /// <summary>Source ProviderRequestOptions.telemetry (types.ts): the explicit parent span of this logical request's telemetry. A
    /// <see cref="ChatRun"/> records a <c>pi.ai.request</c> span under it, with metrics through its <c>Metrics</c> capability
    /// (<see cref="AiTelemetry"/>). Transports receive it with the request but never serialize it. Null records nothing.</summary>
    [System.Diagnostics.CodeAnalysis.Experimental("PISHARP_TELEMETRY001")]
    public PiSharp.Telemetry.ITelemetrySpan? Telemetry { get; init; }
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
