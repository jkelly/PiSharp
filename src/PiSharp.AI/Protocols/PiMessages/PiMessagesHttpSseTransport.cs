using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.PiMessages;

/// <summary>Pi POST/SSE composition. Borrows client; joins request, response and reader ownership before a terminal.</summary>
public sealed class PiMessagesHttpSseTransport : IChatTransport
{
    private readonly HttpClient _client;
    private readonly ModelDescriptor _model;
    private readonly PiMessagesOptions _options;
    private readonly PiMessagesKeyAuthRequestFactory _factory;
    public PiMessagesHttpSseTransport(HttpClient client, ModelDescriptor model, PiMessagesOptions options)
    {
        ArgumentNullException.ThrowIfNull(client); ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(options);
        _factory = new(model, options); _client = client; _model = model; _options = options;
    }
    public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
        => new Invocation(this, request, cancellationToken);
    private sealed class Invocation(PiMessagesHttpSseTransport owner, ChatRequest request, CancellationToken token) : IAsyncEnumerable<StreamEvent>
    {
        private int _claimed;
        public IAsyncEnumerator<StreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _claimed, 1) != 0) throw new InvalidOperationException("A Pi Messages invocation has one reader.");
            return owner.StreamCore(request, token).GetAsyncEnumerator(cancellationToken);
        }
    }
    private async IAsyncEnumerable<StreamEvent> StreamCore(ChatRequest request, [EnumeratorCancellation] CancellationToken token)
    {
        var mapper = new PiMessagesEventMapper(request, _options);
        HttpRequestMessage? ownedRequest = null; HttpResponseMessage? response = null; Stream? body = null;
        IAsyncEnumerator<JsonData>? events = null; StreamTerminalEvent? terminal = null; Exception? failure = null, cleanupFailure = null; var loopExited = false;
        try
        {
            try
            {
                ownedRequest = await _factory.CreateAsync(request, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                response = await _client.SendAsync(ownedRequest, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                var observation = Observe(response);
                if (_options.Hooks.OnResponse is { } responseHook)
                    await responseHook(observation, _model, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                body = _options.BodyReaderFactory is { } acquire ? await acquire(response, token).ConfigureAwait(false) :
                    await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw await ResponseError(response, body, request.Timestamp, token).ConfigureAwait(false);
                if (body is null) throw new PiMessagesException(PiMessagesFailure.SourceFailed, $"{_model.Provider} response has no body");
                events = new PiMessagesSseDecoder(_options).DecodeAsync(body, token).GetAsyncEnumerator(token);
            }
            catch (Exception error) { failure = error; }
            while (failure is null)
            {
                StreamEvent? frame = null; var moved = false;
                try
                {
                    token.ThrowIfCancellationRequested(); moved = await events!.MoveNextAsync().ConfigureAwait(false);
                    if (moved)
                    {
                        var value = events!.Current;
                        if (_options.Hooks.OnProviderStreamEvent is { } providerHook)
                            await providerHook(new(value, []), _model, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested(); frame = mapper.Convert(value);
                        if (frame is not StreamTerminalEvent) _options.Hooks.OnEventPublished?.Invoke(frame);
                    }
                }
                catch (Exception error) { failure = error; }
                if (failure is not null || !moved) break;
                if (frame is StreamTerminalEvent end) { terminal = end; break; }
                yield return frame!;
            }
            loopExited = true;
        }
        finally
        {
            // No detach, retry, fire-and-forget cancellation or borrowed-client disposal.
            // Early iterator return also waits for these owned releases.
            if (events is not null) try { await events.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { cleanupFailure ??= error; }
            if (body is not null) try { await body.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { cleanupFailure ??= error; }
            try { response?.Dispose(); } catch (Exception error) { cleanupFailure ??= error; }
            try { ownedRequest?.Dispose(); } catch (Exception error) { cleanupFailure ??= error; }
            if (!loopExited && cleanupFailure is not null) throw new PiMessagesException(PiMessagesFailure.CleanupFailed, "Pi Messages iterator cleanup failed.");
        }
        // A mapped provider error is already the primary failure. Cleanup must not
        // replace its content, usage, metadata or native diagnostic with a fresh error.
        // Successful tool terminals still lose execution authority if cleanup fails.
        var cleanupOnly = failure is null && terminal is not StreamError && cleanupFailure is not null;
        if (cleanupOnly) failure = cleanupFailure;
        if (token.IsCancellationRequested)
            terminal = mapper.Error(failure ?? cleanupFailure ?? new OperationCanceledException("This operation was aborted."), aborted: true);
        else if (failure is not null) terminal = mapper.Error(failure, aborted: false,
            nativeCode: cleanupOnly ? NativeChatFailureCode.CleanupFailed : null);
        else if (terminal is null) terminal = mapper.Error(new PiMessagesException(PiMessagesFailure.UnexpectedEof,
            $"{_model.Provider} stream ended without a terminal event"), aborted: false);
        if (cleanupFailure is not null) terminal = terminal! with
        { NativeCleanupDiagnostic = new(NativeChatAdapter.PiMessages, NativeChatFailureCode.CleanupFailed) };
        _options.Hooks.OnEventPublished?.Invoke(terminal!);
        yield return terminal!;
    }
    private PiMessagesValueObservation Observe(HttpResponseMessage response)
    {
        var values = response.Headers.Concat(response.Content.Headers).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        if (values.Length > _options.MaximumHeaders) throw PiMessagesData.Fail(PiMessagesFailure.ResourceLimit);
        var headers = new JsonObject();
        foreach (var field in values)
        {
            var name = field.Key.ToLowerInvariant(); var value = string.Join(", ", field.Value);
            if (name.Length + value.Length > _options.MaximumHeaderCharacters) throw PiMessagesData.Fail(PiMessagesFailure.ResourceLimit);
            headers[name] = value;
        }
        return new(JsonData.Parse(new JsonObject { ["status"] = (int)response.StatusCode, ["headers"] = headers }.ToJsonString()), []);
    }
    private async ValueTask<PiMessagesException> ResponseError(HttpResponseMessage response, Stream? bodyReader, long timestamp, CancellationToken token)
    {
        // Read error bytes through the real owned response stream with a bounded accumulation.
        var reader = bodyReader ?? Stream.Null;
        using var bytes = new MemoryStream(); var buffer = new byte[_options.ReadBufferBytes];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false); if (count == 0) break;
            if (count > _options.MaximumResponseErrorBytes - bytes.Length) throw PiMessagesData.Fail(PiMessagesFailure.ResourceLimit);
            bytes.Write(buffer, 0, count);
        }
        var body = new UTF8Encoding(false, false).GetString(bytes.ToArray()); JsonObject? errorBody = null;
        try
        {
            var parsed = JsonNode.Parse(body) as JsonObject;
            if (parsed?["error"] is JsonObject error) errorBody = error;
        }
        catch (JsonException) { }
        string? message = null, code = null;
        if (errorBody?["message"] is JsonValue messageValue && messageValue.TryGetValue<string>(out var text)) message = text;
        if (errorBody?["code"] is JsonValue codeValue && codeValue.TryGetValue<string>(out var authoredCode)) code = authoredCode;
        var suffix = string.IsNullOrEmpty(code) ? "" : " (" + code + ")";
        var publicMessage = $"{(int)response.StatusCode} {response.ReasonPhrase}: {message ?? body}{suffix}";
        var details = new JsonObject { ["version"] = 1, ["provider"] = _model.Provider, ["model"] = _model.Id,
            ["url"] = _factory.Endpoint.ToString(), ["status"] = (int)response.StatusCode, ["statusText"] = response.ReasonPhrase };
        if (errorBody is not null) details["error"] = errorBody.DeepClone();
        else details["body"] = body.Length > 8192 ? body[..8192] + "…" : body; // pi-messages.ts truncateDiagnosticString
        details["timestampMs"] = timestamp;
        return new(PiMessagesFailure.ProviderError, publicMessage, PiMessagesData.Admit(JsonData.Parse(details.ToJsonString()), _options), code);
    }
}
