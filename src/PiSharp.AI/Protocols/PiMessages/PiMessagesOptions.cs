using System.Text;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.PiMessages;

/// <summary>Provider-owned options. Model JSON and borrowed HTTP ownership remain distinct.</summary>
public sealed record PiMessagesOptions(JsonData ModelMetadata, string? ApiKey = null)
{
    public double? Temperature { get; init; }
    public double? MaxTokens { get; init; }
    public string? Reasoning { get; init; }
    public string? CacheRetention { get; init; }
    public string? SessionId { get; init; }
    public JsonData? ToolChoice { get; init; }
    public bool Debug { get; init; }
    public JsonData? Headers { get; init; }
    public JsonData? Environment { get; init; }
    public Func<string, string?>? EnvironmentLookup { get; init; }
    public PiMessagesLifecycleHooks Hooks { get; init; } = new();
    /// <summary>Optional reader acquisition seam; returns an invocation-owned stream or an absent body.</summary>
    public Func<HttpResponseMessage, CancellationToken, ValueTask<Stream?>>? BodyReaderFactory { get; init; }
    public int ReadBufferBytes { get; init; } = 4096;
    public int MaximumFrameCharacters { get; init; } = PiRequestBudget.StreamCharacters;
    public int MaximumDataEvents { get; init; } = 4096;
    public long MaximumTotalDataCharacters { get; init; } = PiRequestBudget.StreamTotalCharacters;
    public int MaximumPayloadBytes { get; init; } = PiRequestBudget.RequestPayloadBytes;
    public int MaximumContentSlots { get; init; } = int.MaxValue;
    public int MaximumContentCharacters { get; init; } = PiRequestBudget.StreamCharacters;
    public int MaximumJsonDepth { get; init; } = 32;
    public int MaximumHeaders { get; init; } = 128;
    public int MaximumHeaderCharacters { get; init; } = 8192;
    public int MaximumResponseErrorBytes { get; init; } = 1_048_576;
    public bool CaptureSourceSnapshots { get; init; } = true;
    public override string ToString() => nameof(PiMessagesOptions);

    internal void Validate()
    {
        if (ModelMetadata is null || Hooks is null || ReadBufferBytes is < 1 or > 65_536 || MaximumFrameCharacters is < 1 or > PiRequestBudget.MaximumBound ||
            MaximumDataEvents is < 1 or > 65_536 || MaximumTotalDataCharacters is < 1 or > PiRequestBudget.MaximumBound || MaximumPayloadBytes is < 1 or > PiRequestBudget.MaximumBound ||
            MaximumContentSlots < 1 || MaximumContentCharacters is < 1 or > PiRequestBudget.MaximumBound || MaximumJsonDepth is < 1 or > 64 ||
            MaximumHeaders is < 1 or > 4096 || MaximumHeaderCharacters is < 1 or > 65_536 || MaximumResponseErrorBytes is < 1 or > 8_388_608)
            throw new ArgumentOutOfRangeException(nameof(PiMessagesOptions), "Invalid Pi Messages limits.");
        if (Temperature is { } temperature && !double.IsFinite(temperature) || MaxTokens is { } maximum && !double.IsFinite(maximum))
            throw PiMessagesData.Fail(PiMessagesFailure.UnsupportedContent);
    }
}

public enum PiMessagesFailure { MissingKey, InvalidConfiguration, InvalidRequest, UnsupportedContent, MalformedStream, UnexpectedEof, ProviderError, Cancelled, ResourceLimit, SourceFailed, CleanupFailed }
public sealed class PiMessagesException(PiMessagesFailure failure, string message, JsonData? diagnosticDetails = null, string? code = null) : Exception(message)
{
    public PiMessagesFailure Failure { get; } = failure;
    public JsonData? DiagnosticDetails { get; } = diagnosticDetails;
    public string? Code { get; } = code;
}

internal static class PiMessagesData
{
    internal static NativeChatDiagnostic Diagnostic(Exception error, bool aborted = false) =>
        new(NativeChatAdapter.PiMessages, aborted ? NativeChatFailureCode.Cancelled : error switch
        {
            PiMessagesException { Failure: PiMessagesFailure.MalformedStream } => NativeChatFailureCode.MalformedStream,
            PiMessagesException { Failure: PiMessagesFailure.UnexpectedEof } => NativeChatFailureCode.UnexpectedEof,
            PiMessagesException { Failure: PiMessagesFailure.ResourceLimit } => NativeChatFailureCode.ResourceLimit,
            PiMessagesException { Failure: PiMessagesFailure.ProviderError } => NativeChatFailureCode.ProviderError,
            PiMessagesException { Failure: PiMessagesFailure.Cancelled } => NativeChatFailureCode.Cancelled,
            PiMessagesException { Failure: PiMessagesFailure.CleanupFailed } => NativeChatFailureCode.CleanupFailed,
            PiMessagesException { Failure: PiMessagesFailure.UnsupportedContent } => NativeChatFailureCode.UnsupportedFeature,
            StreamingJsonPreviewException { Failure: StreamingJsonPreviewFailure.CharacterLimit or StreamingJsonPreviewFailure.DepthLimit }
                => NativeChatFailureCode.ResourceLimit,
            StreamingJsonPreviewException { Failure: StreamingJsonPreviewFailure.UnsupportedNumber or StreamingJsonPreviewFailure.UnsupportedUnicode }
                => NativeChatFailureCode.UnsupportedFeature,
            StreamingJsonPreviewException { Failure: StreamingJsonPreviewFailure.DuplicateProperty } => NativeChatFailureCode.MalformedStream,
            StreamLimitException => NativeChatFailureCode.ResourceLimit,
            JsonException or StreamProtocolException => NativeChatFailureCode.MalformedStream,
            _ => NativeChatFailureCode.SourceFailed
        });
    internal static PiMessagesException Fail(PiMessagesFailure failure) => new(failure, failure switch
    {
        PiMessagesFailure.ResourceLimit => "Pi Messages data exceeds configured limits.",
        PiMessagesFailure.UnexpectedEof => "Pi Messages stream ended without a terminal event.",
        PiMessagesFailure.UnsupportedContent => "Pi Messages value is outside the supported native contract.",
        _ => "Invalid Pi Messages " + failure + "."
    });
    internal static JsonData Admit(JsonData data, PiMessagesOptions options)
    {
        var raw = data.ToString();
        if (raw.Length > options.MaximumPayloadBytes || Encoding.UTF8.GetByteCount(raw) > options.MaximumPayloadBytes) throw Fail(PiMessagesFailure.ResourceLimit);
        var owned = JsonData.Parse(raw);
        void Visit(JsonElement value, int depth)
        {
            if (depth > options.MaximumJsonDepth) throw Fail(PiMessagesFailure.ResourceLimit);
            if (value.ValueKind == JsonValueKind.Number && !double.IsFinite(value.GetDouble())) throw Fail(PiMessagesFailure.UnsupportedContent);
            if (value.ValueKind == JsonValueKind.Object) foreach (var item in value.EnumerateObject()) Visit(item.Value, depth + 1);
            else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Visit(item, depth + 1);
        }
        Visit(owned.Value, 0); return owned;
    }
    internal static string String(JsonElement value, string name) => value.GetProperty(name).GetString() ?? throw Fail(PiMessagesFailure.MalformedStream);
}
