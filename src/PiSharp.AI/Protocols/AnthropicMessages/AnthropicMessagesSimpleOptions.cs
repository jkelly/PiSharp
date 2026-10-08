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
    public int MaximumContextMessages { get; init; } = 256;
    public int MaximumContextCharacters { get; init; } = 1_048_576;
    public override string ToString() => nameof(AnthropicMessagesSimpleOptions);
}

public sealed record AnthropicMessagesContextUsageEstimate(int Tokens, int UsageTokens, int TrailingTokens, int? LastUsageIndex);
public sealed record AnthropicMessagesSimpleResolution(AnthropicMessagesContextUsageEstimate ContextEstimate,
    int MaxTokens, bool ThinkingEnabled, int ThinkingBudgetTokens, string? Effort);
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
