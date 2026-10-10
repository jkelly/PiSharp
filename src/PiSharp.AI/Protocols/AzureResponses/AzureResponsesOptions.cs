using System.Collections.Immutable;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.AzureResponses;

/// <summary>Explicit configuration only; no environment, token-provider or credential-store acquisition.</summary>
public sealed record AzureResponsesOptions(JsonData ModelMetadata, ResponsesTranscriptProjectionOptions Projection)
{
    public string? AzureBaseUrl { get; init; }
    public string? AzureResourceName { get; init; }
    public string? AzureApiVersion { get; init; }
    public string? AzureDeploymentName { get; init; }
    /// <summary>Caller-supplied values for the four Azure endpoint/version/deployment configuration variables only.</summary>
    public ImmutableDictionary<string, string?> ConfigurationValues { get; init; } = ImmutableDictionary<string, string?>.Empty;
    public JsonData? Headers { get; init; }
    public double? MaxTokens { get; init; }
    public double? Temperature { get; init; }
    public string? SessionId { get; init; }
    public string? ReasoningEffort { get; init; }
    public string? ReasoningSummary { get; init; }
    public JsonData? ToolChoice { get; init; }
    public JsonData? SamplingParams { get; init; }
    public AzureResponsesHooks Hooks { get; init; } = new();
    public int? TimeoutMilliseconds { get; init; }
    public int MaximumPayloadBytes { get; init; } = PiRequestBudget.RequestPayloadBytes;
    public int MaximumJsonDepth { get; init; } = PiSharp.Contracts.JsonData.MaximumDepth;
    public int MaximumConfigurationCharacters { get; init; } = 65_536;
    public int MaximumHeaderCharacters { get; init; } = 8192;
    public int MaximumHeaders { get; init; } = 128;
    public int MaximumKeyCharacters { get; init; } = 4096;
    public int MaximumErrorBodyBytes { get; init; } = 65_536;
    public ResponsesHttpSseOptions HttpOptions { get; init; } = new();
    public ResponsesTextToolOptions StreamOptions { get; init; } = new();
    public override string ToString() => nameof(AzureResponsesOptions);
}
public sealed record AzureResponsesHooks(
    Func<JsonData, ModelDescriptor, CancellationToken, ValueTask<JsonData?>>? OnPayload = null,
    Func<AzureResponsesResponseInfo, ModelDescriptor, CancellationToken, ValueTask>? OnResponse = null,
    Func<JsonData, ModelDescriptor, CancellationToken, ValueTask>? OnProviderStreamEvent = null);
public sealed record AzureResponsesResponseInfo(int Status, ImmutableDictionary<string, string> Headers);
public enum AzureResponsesFailure { Configuration, Request, Credential, UnsupportedOptions, ResourceLimit }
public sealed class AzureResponsesException : Exception
{
    public AzureResponsesFailure Failure { get; }
    internal AzureResponsesException(AzureResponsesFailure failure) : base(failure switch
    {
        AzureResponsesFailure.Request => "Azure Responses request does not match the configured model.",
        AzureResponsesFailure.Credential => "Invalid explicit Azure Responses API key.",
        AzureResponsesFailure.UnsupportedOptions => "Unsupported Azure Responses configuration.",
        AzureResponsesFailure.ResourceLimit => "Azure Responses input exceeds configured limits.",
        _ => "Invalid Azure Responses configuration."
    }) => Failure = failure;
}
