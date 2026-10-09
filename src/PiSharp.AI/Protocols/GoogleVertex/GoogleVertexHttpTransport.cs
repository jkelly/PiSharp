using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.AI.Protocols.ProviderShared;
using PiSharp.Contracts;

using PiSharp.AI.Protocols.GoogleGenerativeAI;
namespace PiSharp.AI.Protocols.GoogleVertex;

/// <summary>Actual admitted-token Vertex HTTP/SSE consumer. Borrows the client; owns each request/response/body/reader. ADC and SDK endpoint construction are outside this profile.</summary>
public sealed class GoogleVertexHttpTransport : IChatTransport
{
    private readonly HttpClient _client;
    private readonly ModelDescriptor _model;
    private readonly GoogleGenerativeAIOptions _options;
    private readonly GoogleVertexRequestFactory _factory;
    public GoogleVertexHttpTransport(HttpClient client, ModelDescriptor model, GoogleVertexOptions options)
    {
        ArgumentNullException.ThrowIfNull(client); ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(options);
        options.Validate(model); _client = client; _model = model; _options = options.Projection; _factory = new(model, options);
    }
    public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
        new Invocation(this, request, cancellationToken);
    private sealed class Invocation(GoogleVertexHttpTransport owner, ChatRequest request, CancellationToken token) : IAsyncEnumerable<StreamEvent>
    {
        private int _claimed;
        public IAsyncEnumerator<StreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _claimed, 1) != 0) throw new InvalidOperationException("Google invocation has one reader.");
            return owner.Core(request, token).GetAsyncEnumerator(cancellationToken);
        }
    }
    private async IAsyncEnumerable<StreamEvent> Core(ChatRequest request, [EnumeratorCancellation] CancellationToken token)
    {
        var mapper = new GoogleEventMapper(request, _options);
        HttpRequestMessage? ownedRequest = null; HttpResponseMessage? response = null; Stream? body = null;
        IAsyncEnumerator<JsonData>? chunks = null; Exception? failure = null; Exception? cleanup = null;
        StreamTerminalEvent? terminal = null; var loopExited = false;
        try
        {
            try
            {
                ownedRequest = await _factory.CreateAsync(request, token).ConfigureAwait(false);
                var replay = _options.MaxRetries > 0 ? await GoogleRetryRequest.CaptureAsync(ownedRequest, _options, token).ConfigureAwait(false) : null;
                var retryIndex = 0;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var currentRequest = ownedRequest ?? throw GoogleData.Fail(GoogleFailure.SourceFailed);
                    // Original admitted operations remain owned even if injected work ignores cancellation.
                    response = await _client.SendAsync(currentRequest, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    if (_options.Hooks.OnNativeResponse is { } observe)
                        await observe(Response(response), new(_model, _options.ModelMetadata), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    body = _options.BodyReaderFactory is { } acquire ? await acquire(response, token).ConfigureAwait(false) :
                        await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    if (body is null) throw GoogleData.Fail(GoogleFailure.SourceFailed);
                    if (response.IsSuccessStatusCode)
                    {
                        token.ThrowIfCancellationRequested();
                        chunks = new GoogleSseDecoder(_options).DecodeAsync(body, token).GetAsyncEnumerator(token);
                        break;
                    }
                    var statusError = await StatusError(response, body, token).ConfigureAwait(false);
                    if (retryIndex >= _options.MaxRetries) throw statusError;
                    var delay = TimeSpan.Zero; Exception? planningFailure = null; var retryable = false;
                    try
                    {
                        retryable = GoogleRetryPolicy.Retryable(response, _options);
                        if (retryable) delay = GoogleRetryPolicy.Delay(_options, retryIndex);
                    }
                    catch (Exception error) { planningFailure = error; }
                    if (!retryable && planningFailure is null) throw statusError;
                    var status = (int)response.StatusCode;
                    cleanup = await ReleaseRejectedAttempt(currentRequest, response, body).ConfigureAwait(false);
                    ownedRequest = null; response = null; body = null;
                    if (planningFailure is not null) throw planningFailure;
                    if (cleanup is not null) throw statusError; // Original provider failure remains primary.
                    token.ThrowIfCancellationRequested();
                    _options.Hooks.OnRetry?.Invoke(new(retryIndex, status, delay));
                    token.ThrowIfCancellationRequested();
                    await Task.Delay(delay, _options.RetryTimeProvider, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    ownedRequest = replay!.Create(); retryIndex++;
                }
            }
            catch (Exception error) { failure = error; }
            if (failure is null)
            {
                StreamEvent? start = null;
                try { start = mapper.Start(); _options.Hooks.OnEventPublished?.Invoke(start); }
                catch (Exception error) { failure = error; }
                if (start is not null && failure is null) yield return start;
            }
            while (failure is null)
            {
                IReadOnlyList<StreamEvent>? frames = null; var moved = false;
                try
                {
                    token.ThrowIfCancellationRequested(); moved = await chunks!.MoveNextAsync().ConfigureAwait(false);
                    if (moved)
                    {
                        var chunk = chunks.Current;
                        if (_options.Hooks.OnProviderStreamEvent is { } hook)
                            await hook(chunk, new(_model, _options.ModelMetadata), token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested(); frames = mapper.Convert(chunk);
                    }
                    else frames = mapper.EndContent();
                }
                catch (Exception error) { failure = error; }
                if (failure is not null) break;
                foreach (var frame in frames!)
                {
                    try { token.ThrowIfCancellationRequested(); _options.Hooks.OnEventPublished?.Invoke(frame); }
                    catch (Exception error) { failure = error; }
                    if (failure is not null) break;
                    yield return frame;
                }
                if (failure is not null || !moved) break;
            }
            if (failure is null) terminal = mapper.Finish();
            loopExited = true;
        }
        finally
        {
            if (chunks is not null) try { await chunks.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { cleanup ??= error; }
            if (body is not null) try { await body.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { cleanup ??= error; }
            try { response?.Dispose(); } catch (Exception error) { cleanup ??= error; }
            try { ownedRequest?.Dispose(); } catch (Exception error) { cleanup ??= error; }
            if (!loopExited && cleanup is not null) throw GoogleData.Fail(GoogleFailure.CleanupFailed);
        }
        var cleanupOnly = failure is null && terminal is not StreamError && cleanup is not null;
        if (cleanupOnly) failure = GoogleData.Fail(GoogleFailure.CleanupFailed);
        if (token.IsCancellationRequested) terminal = mapper.Error(failure ?? cleanup ?? new OperationCanceledException(), true);
        else if (failure is not null) terminal = mapper.Error(failure, false);
        // A mapped provider error survives a secondary cleanup failure with its original partial.
        var primary = mapper.FailureKind;
        try { _options.Hooks.OnNativeSettlement?.Invoke(new(primary, cleanup is not null, token.IsCancellationRequested)); }
        catch (Exception error) { terminal = mapper.Error(error, token.IsCancellationRequested); }
        if (token.IsCancellationRequested && terminal!.Reason != StopReason.Aborted)
            terminal = mapper.Error(new OperationCanceledException(), true);
        if (cleanup is not null)
            terminal = terminal! with { NativeCleanupDiagnostic = GoogleNativeDiagnostics.FromCode(NativeChatFailureCode.CleanupFailed) };
        try { _options.Hooks.OnEventPublished?.Invoke(terminal!); }
        catch (Exception error) { terminal = mapper.Error(error, token.IsCancellationRequested); }
        if (token.IsCancellationRequested && terminal!.Reason != StopReason.Aborted)
            terminal = mapper.Error(new OperationCanceledException(), true);
        // A publication callback may replace the terminal; physical cleanup remains a
        // separate secondary diagnostic even when that callback itself fails.
        if (cleanup is not null)
            terminal = terminal! with { NativeCleanupDiagnostic = GoogleNativeDiagnostics.FromCode(NativeChatFailureCode.CleanupFailed) };
        yield return terminal!;
    }
    private static async ValueTask<Exception?> ReleaseRejectedAttempt(HttpRequestMessage request, HttpResponseMessage response, Stream body)
    {
        Exception? failure = null;
        try { await body.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failure = error; }
        try { response.Dispose(); } catch (Exception error) { failure ??= error; }
        try { request.Dispose(); } catch (Exception error) { failure ??= error; }
        return failure;
    }
    private GoogleResponseObservation Response(HttpResponseMessage response)
    {
        var headers = new JsonObject(); var count = 0;
        foreach (var field in response.Headers.Concat(response.Content.Headers))
        {
            var value = string.Join(", ", field.Value);
            if (++count > _options.MaximumHeaders || field.Key.Length + value.Length > _options.MaximumHeaderCharacters)
                throw GoogleData.Fail(GoogleFailure.ResourceLimit);
            headers[field.Key.ToLowerInvariant()] = value;
        }
        return new((int)response.StatusCode, JsonData.Parse(headers.ToJsonString()));
    }
    private async ValueTask<GoogleGenerativeAIException> StatusError(HttpResponseMessage response, Stream body, CancellationToken token)
    {
        using var bytes = new MemoryStream(); var buffer = new byte[_options.ReadBufferBytes];
        while (true)
        {
            var n = await body.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
            if (n == 0) break;
            if (n > _options.MaximumErrorBytes - bytes.Length) throw GoogleData.Fail(GoogleFailure.ResourceLimit);
            bytes.Write(buffer, 0, n);
        }
        // google-generative-ai.ts / google-vertex.ts show formatProviderError(normalizeProviderError(ApiError)): the @google/genai
        // message, JSON.stringify(errorBody), unchanged (ApiError has a status but no body field). A JSON-typed body that
        // response.json() rejects keeps the status-only text, since the engine SyntaxError text is not reproduced.
        return new(GoogleFailure.ProviderError, ProviderErrorText.GoogleStatus(response, bytes.ToArray())
            ?? $"Google request failed with HTTP {(int)response.StatusCode}.");
    }
}
