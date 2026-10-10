using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text;
using PiSharp.AI.Protocols.ProviderShared;
using PiSharp.AI.Providers;
using PiSharp.AI.Transports;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.OpenAICompletions;

public sealed record CompletionsHttpSseOptions(int MaximumDataEvents = int.MaxValue, int MaximumDataCharacters = PiRequestBudget.StreamCharacters,
    long MaximumTotalDataCharacters = PiRequestBudget.StreamTotalCharacters, int MaximumJsonDepth = PiSharp.Contracts.JsonData.MaximumDepth, SseDecoderOptions? Framing = null)
{
    public CompletionsLifecycleHooks? Hooks { get; init; }
    public CompletionsResponseBodyReaderFactory? BodyReaderFactory { get; init; }
    public CompletionsResponseBodyResultReaderFactory? BodyResultReaderFactory { get; init; }
    public int MaximumConsecutiveEmptyBodyReads { get; init; } = 4096;
    /// <summary>Observes the actual owned read result used by the decoder, after executor settlement.</summary>
    public Action<CompletionsBodyReadResult>? OnBodyRead { get; init; }
    /// <summary>Observes actual source-input byte enqueues and EOF. Canonical input remains demand-driven.</summary>
    public Action<CompletionsInputPublication>? OnInputPublished { get; init; }
    public int MaximumResponseHeaders { get; init; } = 128;
    public int MaximumResponseHeaderCharacters { get; init; } = 8192;
    public int MaximumTotalResponseHeaderCharacters { get; init; } = 32_768;
    public int MaximumResponseMetadataBytes { get; init; } = 131_072;
    public int SourceEventCapacity { get; init; } = 32;
    public int MaximumSourceValueCharacters { get; init; } = 8_388_608;
    public int MaximumSourceValueBytes { get; init; } = 8_388_608;
    public CompletionsRetryOptions? Retry { get; init; }
    /// <summary>Per-attempt response-header deadline, matching the SDK fetch timeout. It does not time the returned SSE body.</summary>
    public int? RequestTimeoutMilliseconds { get; init; }
}

/// <summary>Injected HTTP/SSE composition. Borrows the client and owns each independently returned request.</summary>
public sealed class CompletionsHttpSseTransport : IChatTransport
{
    private readonly Func<ChatRequest, CancellationToken, ValueTask<HttpRequestMessage>> _requestFactory;
    private readonly CompletionsHttpSseOptions _options;
    private readonly HttpClient _client;
    private readonly OpenAICompletionsWireSource _completions;
    private readonly bool _sdkFraming;
    private readonly SseDecoderOptions _framing;

    public CompletionsHttpSseTransport(HttpClient client,
        Func<ChatRequest, CancellationToken, HttpRequestMessage> requestFactory,
        CompletionsHttpSseOptions? options = null, OpenAICompletionsWireOptions? completionsOptions = null)
        : this(client, Wrap(client, requestFactory), options, completionsOptions, true) { }

    /// <summary>Explicit awaited request construction; preserves the existing constructor's delegate and target-typed options.</summary>
    public static CompletionsHttpSseTransport FromAsyncRequestFactory(HttpClient client,
        Func<ChatRequest, CancellationToken, ValueTask<HttpRequestMessage>> requestFactory,
        CompletionsHttpSseOptions? options = null, OpenAICompletionsWireOptions? completionsOptions = null) =>
        new(client, requestFactory, options, completionsOptions, true);

    private CompletionsHttpSseTransport(HttpClient client,
        Func<ChatRequest, CancellationToken, ValueTask<HttpRequestMessage>> requestFactory,
        CompletionsHttpSseOptions? options, OpenAICompletionsWireOptions? completionsOptions, bool asyncFactory)
    {
        ArgumentNullException.ThrowIfNull(client); ArgumentNullException.ThrowIfNull(requestFactory);
        _requestFactory = requestFactory; _options = options ?? new();
        _options.Retry?.Validate();
        if (_options.MaximumDataEvents <= 0 || _options.MaximumDataCharacters <= 0 || _options.MaximumTotalDataCharacters <= 0 ||
            _options.MaximumJsonDepth is < 1 or > PiSharp.Contracts.JsonData.MaximumDepth || _options.MaximumResponseHeaders <= 0 ||
            _options.MaximumResponseHeaderCharacters <= 0 || _options.MaximumTotalResponseHeaderCharacters <= 0 ||
            _options.MaximumResponseMetadataBytes < 2 || _options.SourceEventCapacity is < 1 or > 4096 ||
            _options.MaximumSourceValueCharacters is < 2 or > 8_388_608 || _options.MaximumSourceValueBytes is < 2 or > 8_388_608 ||
            _options.MaximumConsecutiveEmptyBodyReads is < 1 or > 65_536 || _options.RequestTimeoutMilliseconds is < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid Completions HTTP/SSE limits.");
        if (_options.BodyReaderFactory is not null && _options.BodyResultReaderFactory is not null)
            throw new ArgumentException("Select one Completions body reader factory.", nameof(options));
        var framing = _options.Framing ?? new(EofBehavior: SseEofBehavior.DispatchPendingEvent)
        { Profile = SseFramingProfile.OpenAISdk719 };
        _framing = framing; _sdkFraming = framing.Profile == SseFramingProfile.OpenAISdk719;
        if (framing.EofBehavior != SseEofBehavior.DispatchPendingEvent ||
            (_sdkFraming ? framing.RejectInvalidUtf8 : !framing.RejectInvalidUtf8))
            throw new ArgumentException("Completions SSE requires pending-event dispatch and its profile's UTF-8 policy.", nameof(options));
        _ = new SseDecoder(framing); _client = client;
        _completions = new((request, token) => new InvocationChunks(this, request, token), completionsOptions);
    }

    public ValueTask<CompletionsRun> StartAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        var startup = new CompletionsStartupHandoff();
        return startup.Bind(new CompletionsRun(_completions, request, _options, cancellationToken,
            sourceView: true, awaitCanonicalDelivery: false, startup));
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var run = new CompletionsRun(_completions, request, _options, cancellationToken,
            sourceView: false, awaitCanonicalDelivery: true);
        await foreach (var frame in run.ReadCanonicalEventsAsync(CancellationToken.None).ConfigureAwait(false)) yield return frame;
    }

    private static Func<ChatRequest, CancellationToken, ValueTask<HttpRequestMessage>> Wrap(
        HttpClient client, Func<ChatRequest, CancellationToken, HttpRequestMessage> requestFactory)
    { ArgumentNullException.ThrowIfNull(client); ArgumentNullException.ThrowIfNull(requestFactory); return (request, token) => ValueTask.FromResult(requestFactory(request, token)); }

    private sealed class InvocationChunks(CompletionsHttpSseTransport owner, ChatRequest request, CancellationToken sourceToken)
        : IAsyncEnumerable<JsonData>, IAsyncEnumerator<JsonData>, ICompletionsOwnedEnumerator
    {
        private HttpRequestMessage? _request;
        private HttpSseTransport.OwnedResponse? _response;
        private IAsyncEnumerator<JsonData>? _events;
        private CancellationTokenSource? _linked;
        private CancellationToken _token;
        private CompletionsReaderStream? _reader;
        private Task<bool>? _pendingMove;
        private Task? _cancel;
        private Task? _sourceClose;
        private Task? _independentRelease;
        private Exception? _readerFailure;
        private Exception? _decoderFailure;
        private Exception? _preparationCleanupFailure;
        private bool _prepared;
        public CompletionsPublicFailure? PublicFailure { get; private set; }
        private bool _receivedDone;
        private ReaderCleanupFault _readerCleanupFault;
        private CompletionsStartupHandoff? _startup;
        private CompletionsResponseInput? _input;
        private Task? _inputCancel;
        private int _claimed, _disposed;
        private readonly TaskCompletionSource _disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public JsonData Current => _events!.Current;

        public IAsyncEnumerator<JsonData> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _claimed, 1) != 0) throw new InvalidOperationException("Completions HTTP chunks have one owner.");
            _token = sourceToken;
            if (cancellationToken != sourceToken && cancellationToken.CanBeCanceled)
                _token = (_linked = CancellationTokenSource.CreateLinkedTokenSource(sourceToken, cancellationToken)).Token;
            return this;
        }

        public void BindStartup(CompletionsStartupHandoff? startup) => _startup = startup;
        public async ValueTask PrepareAsync(CancellationToken cancellationToken)
        {
            if (_prepared) throw new InvalidOperationException("Completions HTTP preparation runs once.");
            _prepared = true;
            _token.ThrowIfCancellationRequested(); cancellationToken.ThrowIfCancellationRequested();
            _request = await owner._requestFactory(request, _token).ConfigureAwait(false) ?? throw Protocol();
            _token.ThrowIfCancellationRequested();
            var accepted = await SendPreparedAsync().ConfigureAwait(false);
            if (_startup is not null)
            {
                _response = new(accepted, owner._framing);
                _input = new(await _response.AcquireBodyAsync(_token).ConfigureAwait(false), owner._framing.ReadBufferBytes,
                    owner._options.OnInputPublished, _token, _startup.AwaitDeliveryAsync);
                if (owner._options.Hooks?.OnResponse is not null)
                    await _response.InspectAsync((response, token) => owner.ObserveResponseAsync(response, request.Model, token), _token)
                        .ConfigureAwait(false);
                _token.ThrowIfCancellationRequested();
            }
            else
            {
                try
                {
                    if (owner._options.Hooks?.OnResponse is not null)
                        await owner.ObserveResponseAsync(accepted, request.Model, _token).ConfigureAwait(false);
                    _token.ThrowIfCancellationRequested(); _response = new(accepted, owner._framing);
                }
                catch { DisposeRejected(accepted); throw; }
            }
            _events = owner.ReadChunksAsync(this, request.Model, _token).GetAsyncEnumerator(_token);
        }

        private async ValueTask<HttpResponseMessage> SendPreparedAsync()
        {
            var options = owner._options.Retry;
            var replay = options is { MaxRetries: > 0 }
                ? await CompletionsRetryRequest.CaptureAsync(_request!, options, _token).ConfigureAwait(false) : null;
            var retryIndex = 0;
            for (;;)
            {
                _token.ThrowIfCancellationRequested(); HttpResponseMessage? response = null;
                Exception failure; int? status; TimeSpan delay;
                try
                {
                    using var timeout = CreateRequestDeadline();
                    response = await owner._client.SendAsync(_request!, HttpCompletionOption.ResponseHeadersRead,
                        timeout?.Token ?? _token).ConfigureAwait(false);
                    _token.ThrowIfCancellationRequested();
                    if (response.IsSuccessStatusCode) return response;
                    status = (int)response.StatusCode; failure = new HttpSseRejectedException(response.StatusCode);
                }
                catch (HttpRequestException error) { failure = error; status = error.StatusCode is { } code ? (int)code : null; }
                catch (OperationCanceledException error) when (!_token.IsCancellationRequested) { failure = error; status = null; }
                catch { if (response is not null) DisposeRejected(response); throw; }
                try
                {
                    _token.ThrowIfCancellationRequested();
                    if (options is null || retryIndex >= options.MaxRetries || !CompletionsRetryPolicy.Retryable(options, status,
                        response is null ? null : CompletionsRetryPolicy.Header(response, "x-should-retry", owner._options.MaximumResponseHeaderCharacters)))
                    {
                        if (response is not null) failure = await owner.StatusFailureAsync(response, failure, _token).ConfigureAwait(false);
                        throw failure;
                    }
                    delay = CompletionsRetryPolicy.Delay(options, retryIndex, response, owner._options.MaximumResponseHeaderCharacters);
                }
                finally { if (response is not null) DisposeRejected(response); }
                // A fresh request owns each attempt. Payload callbacks and accepted-response
                // callbacks are outside this loop; no streamed result is ever replayed.
                try { _request!.Dispose(); } catch (Exception error) { _preparationCleanupFailure ??= error; throw; }
                _request = null;
                options.OnRetry?.Invoke(new(retryIndex, status, delay));
                _token.ThrowIfCancellationRequested();
                await Task.Delay(delay, options.TimeProvider, _token).ConfigureAwait(false);
                _token.ThrowIfCancellationRequested();
                _request = replay!.Create(); retryIndex++;
            }
        }

        private CancellationTokenSource? CreateRequestDeadline()
        {
            if (owner._options.RequestTimeoutMilliseconds is not { } milliseconds) return null;
            var deadline = CancellationTokenSource.CreateLinkedTokenSource(_token);
            deadline.CancelAfter(milliseconds);
            return deadline;
        }

        private void DisposeRejected(HttpResponseMessage response)
        {
            try { response.Dispose(); }
            catch (Exception error) { _preparationCleanupFailure ??= error; throw; }
        }

        public async ValueTask<CompletionsReaderStream> AcquireReaderAsync(CancellationToken token)
        {
            var body = _input ?? await _response!.AcquireBodyAsync(token).ConfigureAwait(false);
            if (owner._options.BodyResultReaderFactory is { } resultFactory)
            {
                var executor = await resultFactory(body, token).ConfigureAwait(false);
                _reader = CompletionsReaderStream.FromResultReader(executor ?? throw Protocol(), owner._framing.ReadBufferBytes,
                    owner._options.MaximumConsecutiveEmptyBodyReads, owner._options.OnBodyRead, _startup, _input is not null);
            }
            else
            {
                var executor = owner._options.BodyReaderFactory is { } factory
                    ? await factory(body, token).ConfigureAwait(false)
                    : CompletionsResponseBodyReader.FromStream(body);
                _reader = new(executor ?? throw Protocol(), owner._framing.ReadBufferBytes, owner._options.OnBodyRead, _startup, _input is not null);
            }
            token.ThrowIfCancellationRequested();
            return _reader;
        }

        public void ReceivedDone() => _receivedDone = true;

        public async ValueTask<bool> MoveNextAsync()
        {
            if (_events is null) throw new InvalidOperationException("Completions HTTP source was not prepared.");
            _pendingMove = _events.MoveNextAsync().AsTask();
            try { return await _pendingMove.WaitAsync(_token).ConfigureAwait(false); }
            catch (CompletionsPublicFailureException error) { PublicFailure = error.Failure; throw; }
        }

        public ValueTask CompleteSourceAsync(bool interrupted) => new(_sourceClose ??= CloseSourceCoreAsync(interrupted));

        private async Task CloseSourceCoreAsync(bool interrupted)
        {
            if (interrupted)
            {
                StartCancel();
                if (_input is not null && _reader is not null)
                {
                    try { if (_reader.TryReleaseIndependent()) return; }
                    catch (Exception error)
                    { _readerCleanupFault |= ReaderCleanupFault.Release; _readerFailure ??= error; return; }
                }
                _independentRelease = ReleaseInterruptedReaderAsync();
                // Pending decoder/read/cancel work remains retained by DisposeCoreAsync.
                return;
            }
            await JoinDecoderAsync().ConfigureAwait(false);
            StartCancel();
            if (_inputCancel is not null)
                try { await _inputCancel.ConfigureAwait(false); }
                catch (Exception) { } // Retained input failure is joined and reported by physical disposal.
            await JoinReaderAsync().ConfigureAwait(false);
            if ((_decoderFailure is not null || _readerFailure is not null) && !_receivedDone) throw Protocol();
        }

        private async Task ReleaseInterruptedReaderAsync()
        {
            if (_reader is null) return;
            // The decoder's read can cancel before a noncooperative prefetched
            // physical pull. Its outer lease retains that input ownership too.
            if (_inputCancel is not null)
                try { await _inputCancel.ConfigureAwait(false); }
                catch (Exception error) { _readerFailure ??= error; }
            try { await _reader.JoinActiveReadAsync().ConfigureAwait(false); }
            catch (Exception error) { _readerFailure ??= error; }
            try { _reader.TryReleaseIndependent(); }
            catch (Exception error) { _readerCleanupFault |= ReaderCleanupFault.Release; _readerFailure ??= error; }
        }

        private void StartCancel()
        {
            if (_input is not null && _inputCancel is null)
                try { _inputCancel = _input.CancelAsync().AsTask(); }
                catch (Exception error) { _inputCancel = Task.FromException(error); }
            // A canceled queue may return a real closed count before the outer
            // token callback runs. Its logical EOF is not physical body EOF.
            if (_reader is not null && (!_reader.ReachedEof || _input is { ReachedPhysicalEof: false }) && _cancel is null)
                try { _cancel = _reader.CancelAsync().AsTask(); }
                catch (Exception error) { _cancel = Task.FromException(error); }
        }

        private async Task JoinDecoderAsync()
        {
            if (_pendingMove is not null)
                try { await _pendingMove.ConfigureAwait(false); }
                catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
                catch (Exception error) { _decoderFailure ??= error; }
            if (_events is not null)
                try { await _events.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { _decoderFailure ??= error; }
            _events = null;
        }

        private async Task JoinReaderAsync()
        {
            if (_cancel is not null)
                try { await _cancel.ConfigureAwait(false); }
                catch (Exception error) { _readerCleanupFault |= ReaderCleanupFault.Cancellation; _readerFailure ??= error; }
            if (_reader is not null && !_reader.Released)
                try { _reader.Release(); }
                catch (Exception error) { _readerCleanupFault |= ReaderCleanupFault.Release; _readerFailure ??= error; }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _ = DisposeCoreAsync();
            return new(_disposal.Task);
        }

        private async Task DisposeCoreAsync()
        {
            Exception? failure = _preparationCleanupFailure;
            // Cancel can settle source semantics before an uncooperative read settles; all
            // admitted work is nevertheless joined before physical disposal/canonical finish.
            StartCancel();
            await JoinDecoderAsync().ConfigureAwait(false);
            if (_independentRelease is not null) await _independentRelease.ConfigureAwait(false);
            StartCancel(); await JoinReaderAsync().ConfigureAwait(false);
            // Re-awaiting an observed decoder/provider callback failure joins its task;
            // it does not turn that semantic failure into failed physical cleanup.
            // Exact DONE admits nonfatal reader Cancel/Release faults once every task is
            // joined. EOF/pre-DONE faults and actual physical disposal remain fatal.
            if (!_receivedDone && _readerCleanupFault != ReaderCleanupFault.None) failure ??= _readerFailure;
            if (_input is not null)
                try { await _input.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { failure ??= error; }
            // Reader-operation faults after exact DONE are retained internally and do not
            // change admitted protocol completion. Physical ownership faults remain fatal.
            try { if (_response is not null) await _response.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { failure ??= error; }
            try { _request?.Dispose(); } catch (Exception error) { failure ??= error; }
            try { _linked?.Dispose(); } catch (Exception error) { failure ??= error; }
            if (failure is null) _disposal.TrySetResult(); else _disposal.TrySetException(failure);
        }

        [Flags] private enum ReaderCleanupFault { None = 0, Cancellation = 1, Release = 2 }
    }

    private async ValueTask ObserveResponseAsync(HttpResponseMessage response, ModelDescriptor model, CancellationToken token)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal); long characters = 0;
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            token.ThrowIfCancellationRequested();
            if (headers.Count >= _options.MaximumResponseHeaders || header.Key.Length > _options.MaximumResponseHeaderCharacters) throw Limit();
            var name = header.Key.ToLowerInvariant(); var joined = new StringBuilder(); var hasValue = false;
            foreach (var part in header.Value)
            {
                token.ThrowIfCancellationRequested(); var separator = hasValue ? 2 : 0;
                if (part.Length + (long)separator > _options.MaximumResponseHeaderCharacters - joined.Length) throw Limit();
                if (separator != 0) joined.Append(", "); joined.Append(part); hasValue = true;
            }
            var text = joined.ToString();
            CompletionsJson.Unicode(name); CompletionsJson.Unicode(text);
            characters += name.Length + (long)text.Length;
            if (name.Length > _options.MaximumResponseHeaderCharacters ||
                text.Length > _options.MaximumResponseHeaderCharacters || characters > _options.MaximumTotalResponseHeaderCharacters ||
                !headers.TryAdd(name, text)) throw Limit();
        }
        var raw = JsonSerializer.Serialize(headers);
        if (Encoding.UTF8.GetByteCount(raw) > _options.MaximumResponseMetadataBytes) throw Limit();
        await _options.Hooks!.OnResponse!(new((int)response.StatusCode, JsonData.Parse(raw)), model, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    private async IAsyncEnumerable<JsonData> ReadChunksAsync(InvocationChunks invocation, ModelDescriptor model,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = 0; long characters = 0;
        var reader = await invocation.AcquireReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var events = new SseDecoder(_framing).DecodeAsync(reader, leaveOpen: true, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (await events.MoveNextAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = events.Current; var data = frame.Data;
            if (count++ >= _options.MaximumDataEvents || data.Length > _options.MaximumDataCharacters ||
                data.Length > _options.MaximumTotalDataCharacters - characters) throw Limit();
            characters += data.Length;
            // OpenAI SDK 7.19.0 checks this exact sentinel before JSON parsing, including named frames.
            // It ends acquisition; the provider mapper still independently validates finalization.
            if (data == "[DONE]") { invocation.ReceivedDone(); yield break; }
            var chunk = Parse(data, cancellationToken);
            var eventName = _sdkFraming ? frame.EventName : frame.EventType;
            if (eventName is not null && eventName.StartsWith("thread.", StringComparison.Ordinal))
            {
                // The SDK returns named thread events in an event/data envelope, not as completion chunks.
                if (eventName.Length > _options.MaximumDataCharacters) throw Limit();
                var name = JsonSerializer.Serialize(eventName); var body = chunk.ToString();
                if (name.Length + (long)body.Length + 18 > _options.MaximumDataCharacters) throw Limit();
                var raw = "{\"event\":" + name + ",\"data\":" + body + "}";
                chunk = Parse(raw, cancellationToken);
            }
            else if (eventName == "error" || chunk.Value.TryGetProperty("error", out var error) && Truthy(error))
            {
                // openai SDK Stream throws an APIError without a status; openai-completions.ts then shows
                // formatProviderError(normalizeProviderError(error)), which is that message.
                var streamError = ProviderErrorText.OpenAIStreamError(chunk.ToString(), eventName == "error")
                    ?? ("(no status code or body)", null);
                throw PublicFailure(streamError.Message, streamError.ErrorJson)
                    ?? new CompletionsPublicFailureException(new("(no status code or body)"));
            }
            if (_options.Hooks?.OnProviderStreamEvent is { } observe)
                await observe(chunk, model, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (_options.Hooks?.OnProviderStreamEventSnapshot is { } observeSnapshot)
            {
                var snapshot = new CompletionsSourceSnapshot(JsonData.Parse("{\"value\":" + chunk.ToString() + ",\"ownUndefinedPaths\":[]}"),
                    _options.MaximumSourceValueCharacters, _options.MaximumSourceValueBytes);
                cancellationToken.ThrowIfCancellationRequested();
                await observeSnapshot(snapshot, model, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            yield return chunk;
        }
    }

    private JsonData Parse(string data, CancellationToken token)
    {
        try
        {
            using var document = JsonDocument.Parse(data, PiSharp.Contracts.JsonData.DocumentOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Protocol();
            Check(document.RootElement, 0, token);
            return JsonData.FromElement(document.RootElement); // Owns all fields; decoded duplicates are rejected.
        }
        catch (JsonException) { throw new CompletionsPublicFailureException(new("Error reading response: malformed server-sent event JSON.")); }
        catch (InvalidOperationException) { throw Protocol(); }
    }
    private void Check(JsonElement value, int depth, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            if (++depth > _options.MaximumJsonDepth) throw Limit();
            if (value.ValueKind == JsonValueKind.Object)
                foreach (var property in value.EnumerateObject()) { Unicode(property.Name); Check(property.Value, depth, token); }
            else foreach (var item in value.EnumerateArray()) Check(item, depth, token);
        }
        else if (value.ValueKind == JsonValueKind.String) Unicode(value.GetString()!);
    }
    /// <summary>The openai SDK status error for a rejected response (<c>APIError.generate</c> over <c>response.text()</c>),
    /// shown as openai-completions.ts shows it. A body over the stream budget, or text public failure data cannot carry,
    /// keeps the status-only rejection.</summary>
    private async ValueTask<Exception> StatusFailureAsync(HttpResponseMessage response, Exception rejection, CancellationToken token)
    {
        var bytes = await ProviderErrorText.ReadBodyAsync(response,
            Math.Min(_options.MaximumTotalDataCharacters, CompletionsPublicFailure.MaximumCharacters), token).ConfigureAwait(false);
        if (bytes is null) return rejection;
        var error = ProviderErrorText.OpenAIStatus((int)response.StatusCode, ProviderErrorText.FetchText(bytes));
        return PublicFailure(ProviderErrorText.Format(error), error.Body) ?? rejection;
    }

    /// <summary>openai-completions.ts errorMessage: the formatted error plus <c>\n${error.error.metadata.raw}</c> when that
    /// OpenRouter detail is present and not already shown.</summary>
    private static CompletionsPublicFailureException? PublicFailure(string message, string? sdkErrorJson)
    {
        if (ProviderErrorText.MetadataRaw(sdkErrorJson) is { } raw && !message.Contains(raw, StringComparison.Ordinal)) message += "\n" + raw;
        try { return new(new CompletionsPublicFailure(message)); }
        catch (Exception error) when (error is ArgumentException or StreamProtocolException) { return null; }
    }

    private static bool Truthy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.False => false,
        JsonValueKind.String => value.GetString()!.Length != 0,
        JsonValueKind.Number => !value.TryGetDouble(out var number) || number != 0,
        _ => true
    };
    private static void Unicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
            if (char.IsHighSurrogate(value[index]))
            { if (++index >= value.Length || !char.IsLowSurrogate(value[index])) throw Protocol(); }
            else if (char.IsLowSurrogate(value[index])) throw Protocol();
    }
    private static StreamProtocolException Protocol() => new("Invalid Completions SSE JSON data.");
    private static StreamLimitException Limit() => new("Completions SSE data exceeds configured limits.");
}
