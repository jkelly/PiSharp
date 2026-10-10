using System.Text.Json.Serialization;
using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.MistralConversations;

public sealed record MistralTokenCostTier(double InputTokensAbove, double Input, double Output, double CacheRead, double CacheWrite);
public sealed record MistralTokenCosts(double Input, double Output, double CacheRead, double CacheWrite)
{ public ImmutableArray<MistralTokenCostTier> Tiers { get; init; } = []; }
public sealed record MistralTextOptions(Uri BaseUrl, bool SupportsText, MistralTokenCosts Costs, string UserAgent)
{
    [JsonIgnore] public string? ApiKey { get; init; }
    public double? Temperature { get; init; }
    public double? MaxTokens { get; init; }
    public int TimeoutMilliseconds { get; init; } = 60_000;
    public int MaximumPayloadBytes { get; init; } = PiRequestBudget.RequestPayloadBytes;
    public int MaximumFrameCharacters { get; init; } = PiRequestBudget.StreamCharacters;
    public int MaximumTotalCharacters { get; init; } = PiRequestBudget.StreamTotalCharacters;
    public int MaximumContentCharacters { get; init; } = PiRequestBudget.StreamCharacters;
    /// <summary>Content parts of one request message (images, replayed parts, tool result parts). mistral.ts converts every part: no
    /// count bound by default; the payload bytes stay the request's memory bound.</summary>
    public int MaximumContentBlocks { get; init; } = int.MaxValue;
    /// <summary>Content blocks (text, thinking, tool calls) of one streamed response. mistral.ts pushes every block onto output.content:
    /// no count bound by default; the content characters stay the response's memory bound.</summary>
    public int MaximumResponseContentBlocks { get; init; } = int.MaxValue;
    public JsonData? ToolChoice { get; init; }
    public string? PromptMode { get; init; }
    public string? ReasoningEffort { get; init; }
    public ImmutableDictionary<string, string?>? ModelHeaders { get; init; }
    public ImmutableDictionary<string, string?>? Headers { get; init; }
    public bool Reasoning { get; init; }
    public bool SupportsImages { get; init; }
    public bool SupportsMidConversationSystemMessages { get; init; }
    public ImmutableDictionary<string, string?>? ThinkingLevelMap { get; init; }
    public string? SessionId { get; init; }
    public bool CachePrompt { get; init; } = true;
    public int MaximumErrorBytes { get; init; } = 1_048_576;
    public int MaximumJsonDepth { get; init; } = 32;
    public int MaximumHeaders { get; init; } = 128;
    public int MaximumHeaderCharacters { get; init; } = 8192;
    public int MaximumTotalHeaderCharacters { get; init; } = 32768;
    public Func<JsonData, ModelDescriptor, CancellationToken, ValueTask<JsonData?>>? OnPayload { get; init; }
    public Func<JsonData, ModelDescriptor, CancellationToken, ValueTask>? OnResponse { get; init; }
    public Func<JsonData, ModelDescriptor, CancellationToken, ValueTask>? OnProviderStreamEvent { get; init; }
    public override string ToString() => nameof(MistralTextOptions);
    internal void Validate()
    {
        if (PromptMode is not (null or "reasoning") || ReasoningEffort is not (null or "none" or "low" or "medium" or "high" or "max") ||
            OnPayload?.GetInvocationList().Length > 1 || OnResponse?.GetInvocationList().Length > 1 || OnProviderStreamEvent?.GetInvocationList().Length > 1)
            throw new MistralTextException(NativeChatFailureCode.UnsupportedFeature, "Unsupported Mistral direct option or callback binding.");
        foreach (var headers in new[] { ModelHeaders, Headers })
            if (headers is not null && (headers.Count > MaximumHeaders || headers.Any(pair =>
                string.IsNullOrEmpty(pair.Key) || pair.Key.Length > MaximumHeaderCharacters ||
                pair.Key.Any(character => !char.IsAsciiLetterOrDigit(character) && !"!#$%&'*+-.^_`|~".Contains(character)) ||
                pair.Value is { } value && (value.Length > MaximumHeaderCharacters || value.Contains('\r') || value.Contains('\n'))) ||
                headers.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Count))
                throw new MistralTextException(NativeChatFailureCode.UnsupportedFeature, "Unsupported Mistral header configuration.");
        if (ThinkingLevelMap is { } map && (!Reasoning || map.Any(pair => pair.Key is not ("off" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max") ||
            pair.Value is { Length: 0 or > 128 })) || SessionId?.Length > 4096 || SessionId?.Contains('\r') == true || SessionId?.Contains('\n') == true)
            throw new MistralTextException(NativeChatFailureCode.UnsupportedFeature, "Unsupported Mistral reasoning/cache configuration.");
        if (BaseUrl is null || !BaseUrl.IsAbsoluteUri || BaseUrl.Scheme is not ("http" or "https") || BaseUrl.UserInfo.Length != 0 || BaseUrl.Query.Length != 0 || BaseUrl.Fragment.Length != 0 ||
            BaseUrl.AbsoluteUri.Length > 8192 || !SupportsText || Costs is null || new[] { Costs.Input, Costs.Output, Costs.CacheRead, Costs.CacheWrite }.Any(x => !double.IsFinite(x) || x < 0) ||
            Costs.Tiers.IsDefault || Costs.Tiers.Any(tier => tier is null || double.IsNaN(tier.InputTokensAbove) || new[] { tier.Input, tier.Output, tier.CacheRead, tier.CacheWrite }.Any(x => !double.IsFinite(x) || x < 0)) ||
            string.IsNullOrWhiteSpace(UserAgent) || UserAgent.Length > MaximumHeaderCharacters || UserAgent.Contains('\r') || UserAgent.Contains('\n') ||
            Temperature is { } t && !double.IsFinite(t) || MaxTokens is { } m && !double.IsFinite(m) ||
            TimeoutMilliseconds is < 1 or > 3_600_000 || MaximumPayloadBytes is < 1 or > PiRequestBudget.MaximumBound || MaximumFrameCharacters is < 1 or > PiRequestBudget.MaximumBound ||
            MaximumTotalCharacters is < 1 or > PiRequestBudget.MaximumBound || MaximumContentCharacters is < 1 or > PiRequestBudget.MaximumBound || MaximumContentBlocks < 1 || MaximumResponseContentBlocks < 1 || MaximumErrorBytes is < 1 or > 8_388_608 ||
            MaximumJsonDepth is < 1 or > 64 || MaximumHeaders is < 4 or > 4096 || MaximumHeaderCharacters is < 1 or > 65536 || MaximumTotalHeaderCharacters is < 4 or > 1_048_576)
            throw new MistralTextException(NativeChatFailureCode.UnsupportedFeature, "Unsupported Mistral text configuration.");
    }
}
public sealed class MistralTextException(NativeChatFailureCode code, string message) : Exception(message)
{
    public NativeChatFailureCode Code { get; } = code;
}
public static class MistralNativeDiagnostics
{
    public static NativeChatDiagnostic FromCode(NativeChatFailureCode code) => new(NativeChatAdapter.MistralConversations, code);
    public static NativeChatDiagnostic FromException(Exception error) => FromCode(error switch
    {
        MistralTextException value => value.Code,
        _ => NativeChatFailureCode.SourceFailed,
    });
}
