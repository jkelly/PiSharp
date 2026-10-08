using System.Text;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.Agent;

public enum PromptInputSource { Interactive, Rpc, Extension }
public enum PromptInputStreamingBehavior { Steer, FollowUp }
public enum PromptInputAction { Continue, Transform, Handled }
public sealed record PromptInput(string Text, PromptInputSource Source = PromptInputSource.Interactive,
    JsonData? Images = null, PromptInputStreamingBehavior? StreamingBehavior = null);
public sealed record PromptInputDecision(PromptInputAction Action, string? Text = null, JsonData? Images = null);
public interface IPromptInputAdmission
{
    ValueTask<PromptInputDecision> ReduceAsync(PromptInput input, CancellationToken cancellationToken);
}
public enum SubmittedInputDisposition { Handled, Queued, Started }
public sealed record SubmittedInputResult(SubmittedInputDisposition Disposition, AgentLoopResult? Run = null);
public enum PromptInputAdmissionFailure { InvalidInput, ResourceLimit, HandlerFailed }
public sealed class PromptInputAdmissionException : Exception
{
    public PromptInputAdmissionFailure Failure { get; }
    public PromptInputAdmissionException(PromptInputAdmissionFailure failure) : base(failure switch
    {
        PromptInputAdmissionFailure.ResourceLimit => "Prompt input exceeds configured limits.",
        PromptInputAdmissionFailure.HandlerFailed => "Prompt input admission failed.",
        _ => "Prompt input is invalid."
    }) => Failure = failure;
}
/// <summary>Prompt admission bounds. Image and message defaults admit Pi-sized prompt images (owner decision 0004: a prompt
/// carrying images of up to Pi's 4.5MB of base64 each, within one request entry); the text bound is unchanged.</summary>
public sealed record PromptInputAdmissionOptions(int MaximumTextCharacters = 65_536, int MaximumImages = 16,
    int MaximumImageCharacters = PiSharp.AI.PiRequestBudget.RequestEntryCharacters,
    int MaximumImageBytes = PiSharp.AI.PiRequestBudget.RequestEntryCharacters, int MaximumJsonDepth = 32,
    int MaximumMessageCharacters = PiSharp.AI.PiRequestBudget.RequestEntryCharacters,
    int MaximumMessageBytes = PiSharp.AI.PiRequestBudget.RequestPayloadBytes)
{
    /// <summary>Explicit queue commands retain their supplied mode even while idle and never start a generation.</summary>
    public bool QueueOnly { get; init; }
    /// <summary>Trusted synchronous preflight over owned values, outside state locks. May be called again if the queue grows.</summary>
    public Action<TranscriptEntry, AgentPendingInputQueueSnapshot, PromptInputStreamingBehavior>? BeforeQueueCommit { get; init; }
}

/// <summary>Pure owned admission/materialization. No clock, effects, queue, or source image normalization.</summary>
public static class PromptInputValue
{
    public static PromptInput Own(PromptInput input, PromptInputAdmissionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var limits = Limits(options);
        if (!Enum.IsDefined(input.Source) || input.StreamingBehavior is { } behavior && !Enum.IsDefined(behavior) ||
            input.Text is null) throw Invalid();
        if (input.Text.Length > limits.MaximumTextCharacters) throw Bound();
        if (!Scalar(input.Text)) throw Invalid();
        JsonData? images = input.Images;
        if (images is not null)
        {
            var raw = images.ToString();
            if (raw.Length > limits.MaximumImageCharacters || Encoding.UTF8.GetByteCount(raw) > limits.MaximumImageBytes) throw Bound();
            try
            {
                using var parsed = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = limits.MaximumJsonDepth });
                images = JsonData.FromElement(parsed.RootElement);
                if (!JsonScalar(images.Value)) throw Invalid();
                if (images.Value.ValueKind != JsonValueKind.Null)
                {
                    if (images.Value.ValueKind != JsonValueKind.Array) throw Invalid();
                    if (images.Value.GetArrayLength() > limits.MaximumImages) throw Bound();
                    foreach (var image in images.Value.EnumerateArray())
                        if (image.ValueKind != JsonValueKind.Object || !image.TryGetProperty("type", out var type) ||
                            type.ValueKind != JsonValueKind.String || type.GetString() != "image" ||
                            !image.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String ||
                            !image.TryGetProperty("mimeType", out var mime) || mime.ValueKind != JsonValueKind.String) throw Invalid();
                }
            }
            catch (PromptInputAdmissionException) { throw; }
            catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException) { throw Invalid(); }
        }
        return input with { Images = images };
    }

    public static PromptInput Apply(PromptInput original, PromptInputDecision decision, PromptInputAdmissionOptions? options = null)
    {
        if (decision is null || !Enum.IsDefined(decision.Action)) throw Invalid();
        return decision.Action == PromptInputAction.Transform
            ? Own(original with { Text = decision.Text!, Images = decision.Images is null || decision.Images.Value.ValueKind == JsonValueKind.Null
                ? original.Images : decision.Images }, options)
            : original;
    }

    public static TranscriptEntry Message(PromptInput input, long timestamp, PromptInputAdmissionOptions? options = null)
    {
        var limits = Limits(options); input = Own(input, limits);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject(); writer.WriteString("role", "user"); writer.WriteStartArray("content");
            writer.WriteStartObject(); writer.WriteString("type", "text"); writer.WriteString("text", input.Text); writer.WriteEndObject();
            if (input.Images is { Value.ValueKind: JsonValueKind.Array } images)
                foreach (var image in images.Value.EnumerateArray()) writer.WriteRawValue(image.GetRawText());
            writer.WriteEndArray(); writer.WriteNumber("timestamp", timestamp); writer.WriteEndObject();
        }
        if (output.Length > limits.MaximumMessageBytes) throw Bound();
        var raw = Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
        if (raw.Length > limits.MaximumMessageCharacters) throw Bound();
        try
        {
            using var parsed = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = limits.MaximumJsonDepth });
            return new("user", JsonData.FromElement(parsed.RootElement));
        }
        catch (JsonException) { throw Bound(); }
    }

    private static PromptInputAdmissionOptions Limits(PromptInputAdmissionOptions? options)
    {
        var value = options ?? new();
        if (value.MaximumTextCharacters <= 0 || value.MaximumImages < 0 || value.MaximumImageCharacters <= 0 ||
            value.MaximumImageBytes <= 0 || value.MaximumJsonDepth is < 1 or > 64 ||
            value.MaximumMessageCharacters <= 0 || value.MaximumMessageBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid prompt input limits.");
        return value;
    }
    private static bool JsonScalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().All(p => Scalar(p.Name) && JsonScalar(p.Value)),
        JsonValueKind.Array => value.EnumerateArray().All(JsonScalar),
        JsonValueKind.String => Scalar(value.GetString()!),
        JsonValueKind.Number => value.TryGetDouble(out var number) && double.IsFinite(number),
        _ => value.ValueKind is JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False
    };
    private static bool Scalar(string value)
    {
        for (var index = 0; index < value.Length; index++)
            if (char.IsHighSurrogate(value[index])) { if (++index >= value.Length || !char.IsLowSurrogate(value[index])) return false; }
            else if (char.IsLowSurrogate(value[index])) return false;
        return true;
    }
    private static PromptInputAdmissionException Invalid() => new(PromptInputAdmissionFailure.InvalidInput);
    private static PromptInputAdmissionException Bound() => new(PromptInputAdmissionFailure.ResourceLimit);
}
