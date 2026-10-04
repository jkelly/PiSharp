using System.Text.Json.Serialization;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.MistralConversations;

public sealed record MistralTokenCosts(double Input, double Output, double CacheRead, double CacheWrite);
public sealed record MistralTextOptions(Uri BaseUrl, bool SupportsText, MistralTokenCosts Costs, string UserAgent)
{
    [JsonIgnore] public string? ApiKey { get; init; }
    public double? Temperature { get; init; }
    public double? MaxTokens { get; init; }
    public int TimeoutMilliseconds { get; init; } = 60_000;
    public int MaximumPayloadBytes { get; init; } = 1_048_576;
    public int MaximumFrameCharacters { get; init; } = 1_048_576;
    public int MaximumTotalCharacters { get; init; } = 8_388_608;
    public int MaximumContentCharacters { get; init; } = 1_048_576;
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
        if (BaseUrl is null || !BaseUrl.IsAbsoluteUri || BaseUrl.Scheme is not ("http" or "https") || BaseUrl.UserInfo.Length != 0 || BaseUrl.Query.Length != 0 || BaseUrl.Fragment.Length != 0 ||
            BaseUrl.AbsoluteUri.Length > 8192 || !SupportsText || Costs is null || new[] { Costs.Input, Costs.Output, Costs.CacheRead, Costs.CacheWrite }.Any(x => !double.IsFinite(x) || x < 0) ||
            string.IsNullOrWhiteSpace(UserAgent) || UserAgent.Length > MaximumHeaderCharacters || UserAgent.Contains('\r') || UserAgent.Contains('\n') ||
            Temperature is { } t && !double.IsFinite(t) || MaxTokens is { } m && !double.IsFinite(m) ||
            TimeoutMilliseconds is < 1 or > 3_600_000 || MaximumPayloadBytes is < 1 or > 8_388_608 || MaximumFrameCharacters is < 1 or > 8_388_608 ||
            MaximumTotalCharacters is < 1 or > 67_108_864 || MaximumContentCharacters is < 1 or > 8_388_608 || MaximumErrorBytes is < 1 or > 8_388_608 ||
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
