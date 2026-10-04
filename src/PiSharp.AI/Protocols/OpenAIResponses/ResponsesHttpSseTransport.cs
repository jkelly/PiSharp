using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI.Transports;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.OpenAIResponses;

public sealed record ResponsesHttpSseOptions(
    int MaximumDataEvents = 4096,
    int MaximumDataCharacters = 65_536,
    long MaximumTotalDataCharacters = 1_048_576,
    int MaximumJsonDepth = 32,
    SseDecoderOptions? Framing = null);

/// <summary>
/// Injected HTTP/SSE to the parsed Responses text/tool/reasoning adapter. Borrows the client;
/// owns each factory-returned request until its response/body cleanup has settled.
/// The delegate overload supplies no request policy; the factory-bound overload derives pricing fallback
/// from explicit immutable request options. No retry or provider SDK is supplied.
/// </summary>
public sealed class ResponsesHttpSseTransport : IChatTransport
{
    private readonly Func<ChatRequest, HttpRequestMessage> _requestFactory;
    private readonly ResponsesHttpSseOptions _options;
    private readonly HttpSseTransport _http;
    private readonly ResponsesTextToolTransport _responses;
    private readonly string? _requestedServiceTier;

    /// <summary>Binds request creation and pricing fallback to the factory's immutable options. Borrows client.</summary>
    public ResponsesHttpSseTransport(HttpClient client, ResponsesKeyAuthRequestFactory requestFactory, string explicitApiKey,
        ResponsesHttpSseOptions? options = null, ResponsesTextToolOptions? responsesOptions = null)
        : this(client, BindFactory(requestFactory, explicitApiKey), options, BindOptions(requestFactory, responsesOptions)) { }

    private static Func<ChatRequest, HttpRequestMessage> BindFactory(ResponsesKeyAuthRequestFactory factory, string key)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(key);
        return request => factory.Create(request, key);
    }

    private static ResponsesTextToolOptions BindOptions(ResponsesKeyAuthRequestFactory factory, ResponsesTextToolOptions? options)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (options?.ServiceTier is { } supplied && supplied != factory.RequestedServiceTier)
            throw new ResponsesKeyAuthRequestException(ResponsesKeyAuthRequestFailure.InvalidConfiguration);
        return (options ?? new()) with { ServiceTier = factory.RequestedServiceTier };
    }
    public ResponsesHttpSseTransport(HttpClient client, Func<ChatRequest, HttpRequestMessage> requestFactory,
        ResponsesHttpSseOptions? options = null, ResponsesTextToolOptions? responsesOptions = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(requestFactory);
        _requestFactory = requestFactory;
        _requestedServiceTier = responsesOptions?.ServiceTier;
        _options = options ?? new();
        if (_options.MaximumDataEvents <= 0 || _options.MaximumDataCharacters <= 0 ||
            _options.MaximumTotalDataCharacters <= 0 || _options.MaximumJsonDepth is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(options), "Responses SSE limits must be positive; JSON depth must be at most 64.");
        var framing = _options.Framing ?? new(RejectInvalidUtf8: true);
        if (!framing.RejectInvalidUtf8)
            throw new ArgumentException("Responses SSE requires strict UTF-8 decoding.", nameof(options));
        _http = new(client, framing);
        _responses = new(ReadDtosAsync, responsesOptions);
    }

    public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
        _responses.StreamAsync(request, cancellationToken);

    private async IAsyncEnumerable<JsonData> ReadDtosAsync(ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The factory transfers ownership only when it returns. A fresh request is required per enumeration.
        using var ownedRequest = _requestFactory(request) ?? throw Protocol();
        cancellationToken.ThrowIfCancellationRequested();
        if (ownedRequest.Options.TryGetValue(ResponsesKeyAuthRequestFactory.RequestedServiceTierKey, out var tier) &&
            tier.RequestedTier != _requestedServiceTier)
            throw new ResponsesKeyAuthRequestException(ResponsesKeyAuthRequestFailure.InvalidConfiguration);
        var eventCount = 0;
        long totalCharacters = 0;
        // This scope settles body/response cleanup before the outer owned request is disposed.
        await using var events = _http.SendAsync(ownedRequest, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (await MoveNextAsync(events).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = events.Current.Data;
            if (eventCount >= _options.MaximumDataEvents || data.Length > _options.MaximumDataCharacters ||
                data.Length > _options.MaximumTotalDataCharacters - totalCharacters) throw Limit();
            eventCount++;
            totalCharacters += data.Length;
            // Authored exact sentinel policy. Mapper final validation still requires response.completed.
            if (data == "[DONE]") yield break;
            yield return ParseDto(data);
        }
    }

    private static async ValueTask<bool> MoveNextAsync(IAsyncEnumerator<SseEvent> events)
    {
        try { return await events.MoveNextAsync().ConfigureAwait(false); }
        catch (SseDecodeException error)
        {
            if (error.Failure == SseDecodeFailure.InvalidUtf8) throw Protocol();
            throw Limit();
        }
    }

    private JsonData ParseDto(string data)
    {
        try
        {
            // The parser has its own hard cap. Configured admission depth is checked before cloning.
            using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 64 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Protocol();
            Validate(document.RootElement, 0);
            // The owned value also rejects duplicate decoded property names at every object depth.
            return JsonData.FromElement(document.RootElement);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        { throw Protocol(); }
    }

    private void Validate(JsonElement value, int depth)
    {
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            if (depth >= _options.MaximumJsonDepth) throw Limit();
            if (value.ValueKind == JsonValueKind.Object)
                foreach (var property in value.EnumerateObject())
                {
                    ValidateUnicode(property.Name);
                    Validate(property.Value, depth + 1);
                }
            else
                foreach (var child in value.EnumerateArray()) Validate(child, depth + 1);
        }
        else if (value.ValueKind == JsonValueKind.String) ValidateUnicode(value.GetString()!);
    }

    private static void ValidateUnicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[++index])) throw Protocol();
            }
            else if (char.IsLowSurrogate(value[index])) throw Protocol();
        }
    }

    private static StreamProtocolException Protocol() => new("Invalid Responses SSE JSON data.");
    private static StreamLimitException Limit() => new("Responses SSE data exceeds configured limits.");
}
