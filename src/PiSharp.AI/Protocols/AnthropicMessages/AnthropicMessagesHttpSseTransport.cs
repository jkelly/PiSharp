using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI.Transports;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.AnthropicMessages;

public sealed record AnthropicMessagesHttpSseOptions(int MaximumDataEvents = 4096, int MaximumDataCharacters = 65_536,
    long MaximumTotalDataCharacters = 1_048_576, int MaximumJsonDepth = 32, SseDecoderOptions? Framing = null);

/// <summary>One configured HTTP/SSE send per enumeration, composed with the accepted Anthropic DTO mapper. Borrows the client.</summary>
public sealed class AnthropicMessagesHttpSseTransport : IChatTransport
{
    private readonly Func<ChatRequest, CancellationToken, HttpRequestMessage> _requestFactory;
    private readonly AnthropicMessagesHttpSseOptions _options;
    private readonly HttpSseTransport _http;
    private readonly AnthropicMessagesTransport _messages;
    private static readonly HashSet<string> MessageEvents = new(StringComparer.Ordinal)
    { "message_start", "message_delta", "message_stop", "content_block_start", "content_block_delta", "content_block_stop" };
    private static readonly JsonData ProviderError = JsonData.Parse("{\"type\":\"error\",\"error\":{}}");

    public AnthropicMessagesHttpSseTransport(HttpClient client,
        Func<ChatRequest, CancellationToken, HttpRequestMessage> requestFactory,
        AnthropicMessagesHttpSseOptions? options = null, AnthropicMessagesOptions? messagesOptions = null)
    {
        ArgumentNullException.ThrowIfNull(client); ArgumentNullException.ThrowIfNull(requestFactory);
        _requestFactory = requestFactory; _options = options ?? new();
        if (_options.MaximumDataEvents <= 0 || _options.MaximumDataCharacters <= 0 || _options.MaximumTotalDataCharacters <= 0 ||
            _options.MaximumJsonDepth is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(options), "Invalid Anthropic HTTP/SSE limits.");
        var framing = _options.Framing ?? new(RejectInvalidUtf8: true, EofBehavior: SseEofBehavior.DispatchPendingEvent);
        if (!framing.RejectInvalidUtf8 || framing.EofBehavior != SseEofBehavior.DispatchPendingEvent)
            throw new ArgumentException("Anthropic SSE requires strict UTF-8 and pending-event dispatch at EOF.", nameof(options));
        _http = new(client, framing); _messages = new(ReadDtosAsync, messagesOptions);
    }
    public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
        _messages.StreamAsync(request, cancellationToken);

    private async IAsyncEnumerable<JsonData> ReadDtosAsync(ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var ownedRequest = _requestFactory(request, cancellationToken) ?? throw Protocol();
        cancellationToken.ThrowIfCancellationRequested();
        var count = 0; long characters = 0;
        // The shared enumerator's finally owns and awaits body/response cleanup before the request scope ends.
        await using var events = _http.SendAsync(ownedRequest, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (await events.MoveNextAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = events.Current; var data = frame.Data;
            if (count++ >= _options.MaximumDataEvents || data.Length > _options.MaximumDataCharacters ||
                data.Length > _options.MaximumTotalDataCharacters - characters) throw Limit();
            characters += data.Length;
            // Pinned iterateAnthropicEvents checks the named error before parsing and ignores all other unnamed/unknown frames.
            if (frame.EventType == "error") { yield return ProviderError; yield break; }
            if (!MessageEvents.Contains(frame.EventType)) continue;
            yield return Parse(data);
        }
    }
    private JsonData Parse(string data)
    {
        try
        {
            using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 64 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Protocol();
            Check(document.RootElement, 0);
            return JsonData.FromElement(document.RootElement); // Clones and rejects decoded duplicate property names.
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { throw Protocol(); }
    }
    private void Check(JsonElement value, int depth)
    {
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            if (++depth > _options.MaximumJsonDepth) throw Limit();
            if (value.ValueKind == JsonValueKind.Object)
                foreach (var property in value.EnumerateObject()) { Unicode(property.Name); Check(property.Value, depth); }
            else foreach (var item in value.EnumerateArray()) Check(item, depth);
        }
        else if (value.ValueKind == JsonValueKind.String) Unicode(value.GetString()!);
    }
    private static void Unicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
            if (char.IsHighSurrogate(value[index]))
            { if (index + 1 >= value.Length || !char.IsLowSurrogate(value[++index])) throw Protocol(); }
            else if (char.IsLowSurrogate(value[index])) throw Protocol();
    }
    private static StreamProtocolException Protocol() => new("Invalid Anthropic SSE JSON data.");
    private static StreamLimitException Limit() => new("Anthropic SSE data exceeds configured limits.");
}
