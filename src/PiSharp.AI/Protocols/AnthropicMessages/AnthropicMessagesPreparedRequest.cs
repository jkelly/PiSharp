using System.Text;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.AnthropicMessages;

/// <summary>One owned payload preparation, with no HTTP resource allocated yet.
/// Only admitted provider factories construct it. The hook receives Payload, never this owner.</summary>
public sealed class AnthropicMessagesPreparedRequest
{
    public JsonData Payload { get; }
    private readonly Func<JsonData,CancellationToken,HttpRequestMessage> _complete;
    private readonly AnthropicMessagesKeyAuthRequestOptions _limits;
    private int _used;

    internal AnthropicMessagesPreparedRequest(JsonData payload, AnthropicMessagesKeyAuthRequestOptions limits,
        Func<JsonData,CancellationToken,HttpRequestMessage> complete, CancellationToken token)
    { _limits = limits; _complete = complete; Payload = Admit(payload, false, token); }

    internal HttpRequestMessage Complete(JsonData? replacement, CancellationToken token)
    {
        Claim();
        return CompleteClaimed(replacement, token);
    }
    internal void Claim()
    { if (Interlocked.CompareExchange(ref _used, 1, 0) != 0) throw new InvalidOperationException("Prepared Anthropic request already consumed."); }
    internal HttpRequestMessage CompleteClaimed(JsonData? replacement, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref _used, 2, 1) != 1) throw new InvalidOperationException("Prepared Anthropic request is not claimed.");
        return _complete(replacement is null ? Payload : Admit(replacement, true, token), token);
    }

    internal static JsonData Project(JsonData payload, Func<JsonProperty,string> raw, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in payload.Value.EnumerateObject())
            { token.ThrowIfCancellationRequested(); writer.WritePropertyName(property.Name); writer.WriteRawValue(raw(property), skipInputValidation: true); }
            writer.WriteEndObject();
        }
        return JsonData.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private JsonData Admit(JsonData payload, bool forceStream, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (payload.Value.ValueKind != JsonValueKind.Object) throw Invalid();
        if (Encoding.UTF8.GetByteCount(payload.ToString()) > _limits.MaximumPayloadBytes) throw Limit();
        Check(payload.Value, 0, token);
        if (payload.Value.TryGetProperty("max_tokens", out var maximum) &&
            (maximum.ValueKind != JsonValueKind.Number || !maximum.TryGetDouble(out var number) || !double.IsFinite(number) || Math.Abs(number) > _limits.MaximumTokenMagnitude)) throw Invalid();
        if (payload.Value.TryGetProperty("betas", out var betas) &&
            (betas.ValueKind != JsonValueKind.Array || betas.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String ||
                value.GetString()!.Any(character => character < ' ' || character > '~')))) throw Invalid();
        if (!forceStream) return payload;
        // Pi spreads replacement then forces stream:true. Preserve all other raw JSON values.
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in payload.Value.EnumerateObject())
            { token.ThrowIfCancellationRequested(); if (property.Name != "stream") property.WriteTo(writer); else writer.WriteBoolean("stream", true); }
            if (!payload.Value.TryGetProperty("stream", out _)) writer.WriteBoolean("stream", true);
            writer.WriteEndObject();
        }
        if (buffer.Length > _limits.MaximumPayloadBytes) throw Limit();
        return JsonData.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private void Check(JsonElement value, int depth, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            if (++depth > _limits.MaximumPayloadDepth) throw Limit();
            if (value.ValueKind == JsonValueKind.Object)
                foreach (var property in value.EnumerateObject()) { Unicode(property.Name); Check(property.Value, depth, token); }
            else foreach (var item in value.EnumerateArray()) Check(item, depth, token);
        }
        else if (value.ValueKind == JsonValueKind.String) Unicode(value.GetString()!);
        else if (value.ValueKind == JsonValueKind.Number && (!value.TryGetDouble(out var number) || !double.IsFinite(number))) throw Invalid();
    }
    private static void Unicode(string value)
    {
        for (var i = 0; i < value.Length; i++)
            if (char.IsHighSurrogate(value[i])) { if (++i >= value.Length || !char.IsLowSurrogate(value[i])) throw Invalid(); }
            else if (char.IsLowSurrogate(value[i])) throw Invalid();
    }
    private static AnthropicMessagesKeyAuthRequestException Invalid() => new(AnthropicMessagesKeyAuthRequestFailure.InvalidRequest);
    private static AnthropicMessagesKeyAuthRequestException Limit() => new(AnthropicMessagesKeyAuthRequestFailure.ResourceLimit);
}
