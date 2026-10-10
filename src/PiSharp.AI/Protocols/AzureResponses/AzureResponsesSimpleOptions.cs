using System.Collections.Immutable;

namespace PiSharp.AI.Protocols.AzureResponses;

/// <summary>Azure Simple options over an explicit direct profile and borrowed client.
/// ApiKey is supplied by the owning host; no environment or credential lookup occurs.</summary>
public sealed record AzureResponsesSimpleOptions(AzureResponsesOptions DirectOptions,
    string? ApiKey = null, string? Reasoning = null)
{
    public int MaximumContextMessages { get; init; } = PiRequestBudget.RequestMessages;
    public int MaximumContextCharacters { get; init; } = PiRequestBudget.RequestPayloadBytes;
    public override string ToString() => nameof(AzureResponsesSimpleOptions);
}

public sealed record AzureSimpleContextEstimate(double Tokens, double UsageTokens, double TrailingTokens, int? LastUsageIndex);
public sealed record AzureResponsesSimpleResolution(AzureSimpleContextEstimate ContextEstimate, double MaxTokens,
    ImmutableArray<string> SupportedThinkingLevels, string? ClampedReasoning, string? ReasoningEffort);
