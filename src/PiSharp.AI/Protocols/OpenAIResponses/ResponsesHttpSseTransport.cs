using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Contracts.Compatibility;
using PiSharp.AI.Protocols.ProviderShared;
using PiSharp.AI.Transports;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.OpenAIResponses;

public sealed record ResponsesHttpSseOptions(
    int MaximumDataEvents = 4096,
    int MaximumDataCharacters = 65_536,
    long MaximumTotalDataCharacters = PiRequestBudget.StreamTotalCharacters,
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
    private readonly HttpClient _client;
    private readonly SseDecoderOptions _framing;
    private readonly ResponsesKeyAuthRequestFactory? _factory;
    private readonly ResponsesTextToolTransport _responses;
    private readonly string? _requestedServiceTier;

    /// <summary>Binds request creation and pricing fallback to the factory's immutable options. Borrows client.</summary>
    public ResponsesHttpSseTransport(HttpClient client, ResponsesKeyAuthRequestFactory requestFactory, string explicitApiKey,
        ResponsesHttpSseOptions? options = null, ResponsesTextToolOptions? responsesOptions = null)
        : this(client, BindFactory(requestFactory, explicitApiKey), options, BindOptions(requestFactory, responsesOptions)) { _factory = requestFactory; }

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
        _ = new SseDecoder(framing);
        _client = client; _framing = framing;
        _responses = new(PrepareDtosAsync, responsesOptions);
    }

    public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
        _responses.StreamAsync(request, cancellationToken);

    private async ValueTask<IAsyncEnumerator<JsonData>> PrepareDtosAsync(ChatRequest request, CancellationToken cancellationToken,
        ResponsesFailureContext context)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The factory transfers ownership only when it returns. A fresh request is required per enumeration.
        var ownedRequest = _requestFactory(request) ?? throw Protocol();
        HttpResponseMessage? response = null;
        PreparedDtos? prepared = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ownedRequest.Options.TryGetValue(ResponsesKeyAuthRequestFactory.RequestedServiceTierKey, out var tier) &&
                tier.RequestedTier != _requestedServiceTier)
                throw new ResponsesKeyAuthRequestException(ResponsesKeyAuthRequestFailure.InvalidConfiguration);
            var factory = _factory;
            if (factory is not null && factory.Policy.OnPayload is { } payloadHook)
            {
                var content = ownedRequest.Content ?? throw Protocol();
                var original = JsonData.Parse(await context.Source(() => new ValueTask<string>(content.ReadAsStringAsync(cancellationToken))).ConfigureAwait(false));
                var replacement = await context.Source(() => payloadHook(original, request.Model, cancellationToken)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (replacement is not null)
                {
                    factory.AdmitHookPayload(replacement, cancellationToken);
                    var next = new ByteArrayContent(Encoding.UTF8.GetBytes(replacement.ToString()));
                    foreach (var header in content.Headers)
                        if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) next.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    ownedRequest.Content = next;
                    context.Cleanup(() => content.Dispose());
                    if (context.CleanupExceptions.Count != 0) throw context.CleanupExceptions[0];
                }
            }
            response = await context.Source(() => new ValueTask<HttpResponseMessage>(_client.SendAsync(ownedRequest,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken))).ConfigureAwait(false);
            prepared = new(this, response, ownedRequest, request.Model, context, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!response.IsSuccessStatusCode)
            {
                var body = await ReadErrorBody(prepared, context, cancellationToken).ConfigureAwait(false);
                var prefix = request.Model.Provider == "openai" ? "OpenAI" : request.Model.Provider;
                var rejection = new HttpSseRejectedException(response.StatusCode);
                // openai-responses.ts: formatProviderError(normalizeProviderError(APIError), prefix + " API error").
                context.DisplayMessage = ProviderErrorText.Format(ProviderErrorText.OpenAIStatus((int)response.StatusCode, body), prefix + " API error");
                throw rejection;
            }
            if (_factory?.Policy.OnResponse is { } responseHook)
                await context.Source(() => responseHook(ResponseObservation(response), request.Model, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return prepared;
        }
        catch
        {
            if (prepared is not null) await prepared.DisposeAsync().ConfigureAwait(false);
            else { if (response is not null) context.Cleanup(() => response.Dispose()); context.Cleanup(() => ownedRequest.Dispose()); }
            throw;
        }
    }

    private sealed class PreparedDtos : IAsyncEnumerator<JsonData>
    {
        private readonly IAsyncEnumerator<JsonData> _reader;
        private readonly HttpResponseMessage _response;
        private readonly HttpRequestMessage _request;
        private readonly ResponsesFailureContext _context;
        private Stream? _body;
        internal ModelDescriptor Model { get; }
        internal PreparedDtos(ResponsesHttpSseTransport owner, HttpResponseMessage response, HttpRequestMessage request,
            ModelDescriptor model, ResponsesFailureContext context, CancellationToken token)
        {
            _response = response; _request = request; Model = model; _context = context;
            _reader = owner.ReadDtosAsync(this, context, token).GetAsyncEnumerator(token);
        }
        internal async ValueTask<Stream> Body(CancellationToken token) => _body ??=
            await _context.Source(() => new ValueTask<Stream>(_response.Content.ReadAsStreamAsync(token))).ConfigureAwait(false);
        public JsonData Current => _reader.Current;
        public ValueTask<bool> MoveNextAsync() => _reader.MoveNextAsync();
        public async ValueTask DisposeAsync()
        {
            await _context.Cleanup(() => _reader.DisposeAsync()).ConfigureAwait(false);
            if (_body is { } ownedBody) await _context.Cleanup(() => ownedBody.DisposeAsync()).ConfigureAwait(false);
            _context.Cleanup(() => _response.Dispose());
            _context.Cleanup(() => _request.Dispose());
        }
    }

    private async IAsyncEnumerable<JsonData> ReadDtosAsync(PreparedDtos prepared, ResponsesFailureContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var eventCount = 0;
        long totalCharacters = 0;
        var body = await prepared.Body(cancellationToken).ConfigureAwait(false);
        await using var events = new SseDecoder(_framing).DecodeAsync(body, leaveOpen: true, cancellationToken).GetAsyncEnumerator(cancellationToken);
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
            var dto = ParseDto(data);
            if (_factory?.Policy.OnProviderStreamEvent is { } rawHook)
                await context.Source(() => rawHook(dto, prepared.Model, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            yield return dto;
        }
    }

    private JsonData ResponseObservation(HttpResponseMessage response)
    {
        var headers = new JsonObject(); long total = 0; var count = 0;
        foreach (var field in response.Headers.Concat(response.Content.Headers))
        {
            var text = string.Join(", ", field.Value); total += field.Key.Length + text.Length;
            if (++count > _options.MaximumDataEvents || text.Length > _options.MaximumDataCharacters || total > _options.MaximumTotalDataCharacters) throw Limit();
            headers[field.Key.ToLowerInvariant()] = text;
        }
        return JsonData.Parse(new JsonObject { ["status"] = (int)response.StatusCode, ["headers"] = headers }.ToJsonString());
    }

    /// <summary>The SDK's <c>response.text()</c> of the rejected body: UTF-8 with replacement, a leading BOM removed.</summary>
    private async ValueTask<string> ReadErrorBody(PreparedDtos prepared, ResponsesFailureContext context, CancellationToken token)
    {
        var body = await prepared.Body(token).ConfigureAwait(false);
        using var reader = new StreamReader(body, new UTF8Encoding(false, false), detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        var buffer = new char[1024]; var text = new StringBuilder();
        while (true)
        {
            var read = await context.Source(() => reader.ReadAsync(buffer.AsMemory(), token)).ConfigureAwait(false);
            if (read == 0) break;
            if (read > _options.MaximumTotalDataCharacters - text.Length) throw Limit();
            text.Append(buffer, 0, read);
        }
        return ProviderErrorText.FetchText(text.ToString());
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
