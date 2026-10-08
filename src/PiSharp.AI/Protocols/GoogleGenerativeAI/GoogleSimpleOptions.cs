using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.GoogleGenerativeAI;

/// <summary>Explicit Google Simple options over the existing direct native owner. No implicit credential reads.</summary>
public sealed record GoogleSimpleOptions(GoogleGenerativeAIOptions DirectOptions, string? Reasoning = null)
{
    public JsonData? ThinkingBudgets { get; init; }
    public int MaximumContextMessages { get; init; } = 4096;
    public int MaximumContextCharacters { get; init; } = PiRequestBudget.RequestPayloadBytes;
    public override string ToString() => nameof(GoogleSimpleOptions);
}

public sealed record GoogleContextUsageEstimate(double Tokens, double UsageTokens, double TrailingTokens, int? LastUsageIndex);

/// <summary>Pure transcript resolution; it grants no HTTP, publication or tool authority.</summary>
public sealed record GoogleSimpleResolution(GoogleContextUsageEstimate ContextEstimate, double MaxTokens,
    ImmutableArray<string> SupportedLevels, string? ClampedReasoning, string? ResolvedThinkingLevel, GoogleThinkingOptions Thinking);
