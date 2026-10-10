using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.AnthropicMessages;

/// <summary>Explicit provider-local Simple configuration, resolved against each current transcript.</summary>
public sealed record AnthropicMessagesSimpleOptions(JsonData ModelMetadata, AnthropicMessagesRequestOptions ProjectionOptions,
    string? Reasoning = null)
{
    public string? ApiKey { get; init; }
    public int? MaxTokens { get; init; }
    public JsonData? ThinkingBudgets { get; init; }
    public JsonData? ToolChoice { get; init; }
    public AnthropicMessagesKeyAuthRequestOptions KeyAuthOptions { get; init; } = new();
    public AnthropicMessagesHttpSseOptions HttpOptions { get; init; } = new();
    public AnthropicMessagesOptions? MessagesOptions { get; init; }
    public AnthropicMessagesHooks? Hooks { get; init; }
    public int MaximumContextMessages { get; init; } = PiRequestBudget.RequestMessages;
    public int MaximumContextCharacters { get; init; } = PiRequestBudget.RequestPayloadBytes;
    public override string ToString() => nameof(AnthropicMessagesSimpleOptions);
}

/// <summary>estimate.ts ContextUsageEstimate: JavaScript numbers (a fractional usage count stays a fraction).</summary>
public sealed record AnthropicMessagesContextUsageEstimate(double Tokens, double UsageTokens, double TrailingTokens, int? LastUsageIndex);
public sealed record AnthropicMessagesSimpleResolution(AnthropicMessagesContextUsageEstimate ContextEstimate,
    double MaxTokens, bool ThinkingEnabled, double ThinkingBudgetTokens, string? Effort);
public enum AnthropicMessagesSimpleFailure { InvalidConfiguration, InvalidTranscript, ResourceLimit }
public sealed class AnthropicMessagesSimpleException : Exception
{
    public AnthropicMessagesSimpleFailure Failure { get; }
    internal AnthropicMessagesSimpleException(AnthropicMessagesSimpleFailure failure) : base(failure switch
    {
        AnthropicMessagesSimpleFailure.InvalidTranscript => "Invalid Anthropic Simple transcript.",
        AnthropicMessagesSimpleFailure.ResourceLimit => "Anthropic Simple input exceeds configured limits.",
        _ => "Invalid Anthropic Simple configuration."
    }) => Failure = failure;
}
