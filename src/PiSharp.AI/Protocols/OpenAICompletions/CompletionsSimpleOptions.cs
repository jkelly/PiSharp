using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.OpenAICompletions;

/// <summary>Provider-owned simple options. Original model JSON and requested level are independent of Agent metadata.</summary>
public sealed record CompletionsSimpleOptions(JsonData ModelMetadata, string? Reasoning = null)
{
    public string? ApiKey { get; init; }
    public CompletionsKeyAuthRequestOptions DirectOptions { get; init; } = new();
    public CompletionsHttpSseOptions HttpOptions { get; init; } = new();
    public string? TransportPreference { get; init; }
    /// <summary>Bounded opaque data retained by Simple; the Completions SSE provider does not serialize it.</summary>
    public JsonData? TelemetryContext { get; init; }
    /// <summary>Bounded caller metadata. Pi's Completions provider forwards it without adding request fields.</summary>
    public JsonData? Metadata { get; init; }
    /// <summary>Scoped environment data. Explicit DirectOptions.CacheRetention takes precedence; the borrowed client is not reconfigured.</summary>
    public JsonData? Environment { get; init; }
    /// <summary>Per-attempt HTTP response-header timeout, forwarded into the actual transport.</summary>
    public int? TimeoutMilliseconds { get; init; }
    /// <summary>Retained Simple option; Completions uses SSE and does not apply this WebSocket deadline.</summary>
    public int? WebsocketConnectTimeoutMilliseconds { get; init; }
    public int MaximumContextMessages { get; init; } = 4096;
    public int MaximumContextCharacters { get; init; } = PiRequestBudget.RequestPayloadBytes;

    // No keys, model headers or opaque metadata enter diagnostic formatting.
    public override string ToString() => nameof(CompletionsSimpleOptions);
}

public sealed record CompletionsContextUsageEstimate(double Tokens, double UsageTokens, double TrailingTokens, int? LastUsageIndex);

/// <summary>Pure current-transcript resolution; it grants no send, publication or tool authority.</summary>
public sealed record CompletionsSimpleResolution(CompletionsContextUsageEstimate ContextEstimate, double MaxTokens,
    ImmutableArray<string> SupportedLevels, string? ResolvedThinkingLevel, string? ReasoningEffort);
