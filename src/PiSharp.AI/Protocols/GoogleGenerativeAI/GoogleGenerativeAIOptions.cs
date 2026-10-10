using System.Text;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.GoogleGenerativeAI;

/// <summary>Direct stream options. Credentials and environment are supplied by the caller.</summary>
public sealed record GoogleGenerativeAIOptions(JsonData ModelMetadata, string? ApiKey = null)
{
    public double? Temperature { get; init; }
    public double? MaxTokens { get; init; }
    public string? ToolChoice { get; init; }
    public GoogleThinkingOptions? Thinking { get; init; }
    public JsonData? Headers { get; init; }
    public int MaxRetries { get; init; }
    public int MaxRetryDelayMilliseconds { get; init; } = 60_000;
    /// <summary>Native timing seams. They do not enter SDK parameters or Pi wire JSON.</summary>
    public TimeProvider RetryTimeProvider { get; init; } = TimeProvider.System;
    public Func<double> RetryJitterSample { get; init; } = Random.Shared.NextDouble;
    /// <summary>Pi abe508e1 provider-retry.ts noRetryStatuses: statuses that fail at once although they would be retried.</summary>
    public System.Collections.Immutable.ImmutableArray<int> NoRetryStatuses { get; init; } = [];
    public GoogleGenerativeAIHooks Hooks { get; init; } = new();
    public Func<HttpResponseMessage, CancellationToken, ValueTask<Stream?>>? BodyReaderFactory { get; init; }
    public int ReadBufferBytes { get; init; } = 4096;
    public int MaximumFrameCharacters { get; init; } = PiRequestBudget.StreamCharacters;
    public int MaximumPayloadBytes { get; init; } = PiRequestBudget.RequestPayloadBytes;
    public int MaximumEvents { get; init; } = int.MaxValue;
    public long MaximumStreamCharacters { get; init; } = PiRequestBudget.StreamTotalCharacters;
    public int MaximumContentSlots { get; init; } = int.MaxValue;
    public int MaximumContentCharacters { get; init; } = PiRequestBudget.StreamCharacters;
    public int MaximumHeaders { get; init; } = 128;
    public int MaximumHeaderCharacters { get; init; } = 8192;
    public int MaximumErrorBytes { get; init; } = 1_048_576;
    public override string ToString() => nameof(GoogleGenerativeAIOptions);
    internal void Validate()
    {
        if (ModelMetadata is null || Hooks is null || MaxRetries is < 0 or > 32 || MaxRetryDelayMilliseconds < 0 ||
            RetryTimeProvider is null || RetryJitterSample is null || NoRetryStatuses.IsDefault || NoRetryStatuses.Length > 4096 ||
            ReadBufferBytes is < 1 or > 65536 ||
            MaximumFrameCharacters is < 1 or > PiRequestBudget.MaximumBound || MaximumPayloadBytes is < 1 or > PiRequestBudget.MaximumBound ||
            MaximumEvents < 1 || MaximumStreamCharacters is < 1 or > PiRequestBudget.MaximumBound ||
            MaximumContentSlots < 1 || MaximumContentCharacters is < 1 or > PiRequestBudget.MaximumBound ||
            MaximumHeaders is < 1 or > 4096 || MaximumHeaderCharacters is < 1 or > 65536 ||
            MaximumErrorBytes is < 1 or > 8388608)
            throw new ArgumentOutOfRangeException(nameof(GoogleGenerativeAIOptions));
        if (Temperature is { } t && !double.IsFinite(t) || MaxTokens is { } m && !double.IsFinite(m) ||
            Thinking?.BudgetTokens is { } b && !double.IsFinite(b))
            throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
    }
}

public sealed record GoogleThinkingOptions(bool Enabled, double? BudgetTokens = null, string? Level = null);

/// <summary>Payload observes Source SDK parameters, not the REST wire body. All callbacks are awaited.</summary>
public sealed record GoogleGenerativeAIHooks
{
    public Func<JsonData, GoogleModelObservation, CancellationToken, ValueTask<JsonData?>>? OnPayload { get; init; }
    public Func<JsonData, GoogleModelObservation, CancellationToken, ValueTask>? OnProviderStreamEvent { get; init; }
    /// <summary>Native response observation seam; pinned Google source does not invoke onResponse.</summary>
    public Func<GoogleResponseObservation, GoogleModelObservation, CancellationToken, ValueTask>? OnNativeResponse { get; init; }
    public Action<StreamEvent>? OnEventPublished { get; init; }
    /// <summary>Native observation after every owned release; kept outside Pi wire JSON.</summary>
    public Action<GoogleSettlementObservation>? OnNativeSettlement { get; init; }
    /// <summary>Native retry decision after rejected request/response/body ownership closes.</summary>
    public Action<GoogleRetryObservation>? OnRetry { get; init; }
}
public sealed record GoogleRetryObservation(int RetryIndex, int Status, TimeSpan Delay);
public sealed record GoogleResponseObservation(int Status, JsonData Headers);
public sealed record GoogleModelObservation(ModelDescriptor Identity, JsonData Raw)
{
    public override string ToString() => nameof(GoogleModelObservation);
}
public sealed record GoogleSettlementObservation(GoogleFailure? PrimaryFailure, bool CleanupFailed, bool Aborted);
public enum GoogleFailure { Configuration, MissingKey, UnsupportedValue, MalformedStream, UnexpectedEof, ResourceLimit, ProviderError, SourceFailed, CleanupFailed }
public sealed class GoogleGenerativeAIException(GoogleFailure failure, string message) : Exception(message)
{
    public GoogleFailure Failure { get; } = failure;
}

internal static class GoogleData
{
    internal static GoogleGenerativeAIException Fail(GoogleFailure failure) => new(failure, failure switch
    {
        GoogleFailure.MissingKey => "No API key for the Google provider.",
        GoogleFailure.UnexpectedEof => "Google stream ended without a finish reason",
        GoogleFailure.ResourceLimit => "Google data exceeds configured limits.",
        GoogleFailure.CleanupFailed => "Google invocation cleanup failed.",
        GoogleFailure.UnsupportedValue => "Google value is outside the supported native contract.",
        _ => "Invalid Google " + failure + "."
    });
    internal static string? String(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) &&
        item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    internal static bool True(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.True;
    internal static JsonData Admit(JsonData value, GoogleGenerativeAIOptions options)
    {
        if (Encoding.UTF8.GetByteCount(value.ToString()) > options.MaximumPayloadBytes) throw Fail(GoogleFailure.ResourceLimit);
        void Visit(JsonElement item)
        {
            if (item.ValueKind == JsonValueKind.Number && (!item.TryGetDouble(out var n) || !double.IsFinite(n)))
                throw Fail(GoogleFailure.UnsupportedValue);
            if (item.ValueKind == JsonValueKind.Object) foreach (var property in item.EnumerateObject()) Visit(property.Value);
            if (item.ValueKind == JsonValueKind.Array) foreach (var child in item.EnumerateArray()) Visit(child);
        }
        Visit(value.Value); return value;
    }
}
