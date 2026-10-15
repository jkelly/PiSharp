using System.Runtime.CompilerServices;
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.AI.Protocols.ProviderShared;
using PiSharp.AI.Transports;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.AnthropicMessages;

/// <summary>The SDK reads every event of a stream, of any size: no event count; one event keeps the per-content memory bound and the
/// whole stream the total one.</summary>
public sealed record AnthropicMessagesHttpSseOptions(int MaximumDataEvents = int.MaxValue, int MaximumDataCharacters = PiRequestBudget.StreamCharacters,
    long MaximumTotalDataCharacters = PiRequestBudget.StreamTotalCharacters, int MaximumJsonDepth = PiSharp.Contracts.JsonData.MaximumDepth, SseDecoderOptions? Framing = null);

/// <summary>One configured HTTP/SSE send per enumeration, composed with the accepted Anthropic DTO mapper. Borrows the client.</summary>
public sealed class AnthropicMessagesHttpSseTransport : IChatTransport
{
    private readonly Func<ChatRequest, CancellationToken, HttpRequestMessage> _requestFactory;
    private readonly Func<ChatRequest, CancellationToken, AnthropicMessagesPreparedRequest>? _preparedFactory;
    private readonly AnthropicMessagesHttpSseOptions _options;
    private readonly HttpSseTransport _http;
    private readonly AnthropicMessagesTransport _messages;
    private readonly AnthropicMessagesOptions? _messagesOptions;
    private readonly AnthropicMessagesHooks? _hooks;
    private readonly SseDecoderOptions _framing;
    private static readonly HashSet<string> MessageEvents = new(StringComparer.Ordinal)
    { "message_start", "message_delta", "message_stop", "content_block_start", "content_block_delta", "content_block_stop" };

    public AnthropicMessagesHttpSseTransport(HttpClient client,
        Func<ChatRequest, CancellationToken, HttpRequestMessage> requestFactory,
        AnthropicMessagesHttpSseOptions? options = null, AnthropicMessagesOptions? messagesOptions = null,
        AnthropicMessagesHooks? hooks = null)
        : this(client, requestFactory, options, messagesOptions, hooks, null) { }

    /// <summary>Structured provider preparation before encoding; never rereads arbitrary HTTP content.</summary>
    public static AnthropicMessagesHttpSseTransport FromPrepared(HttpClient client,
        Func<ChatRequest,CancellationToken,AnthropicMessagesPreparedRequest> prepare,
        AnthropicMessagesHttpSseOptions? options = null, AnthropicMessagesOptions? messagesOptions = null,
        AnthropicMessagesHooks? hooks = null)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        if (prepare.GetInvocationList().Length != 1) throw new ArgumentException("One preparation factory required.", nameof(prepare));
        return new(client, (request, token) => prepare(request, token).Complete(null, token), options, messagesOptions, hooks, prepare);
    }

    private AnthropicMessagesHttpSseTransport(HttpClient client,
        Func<ChatRequest,CancellationToken,HttpRequestMessage> requestFactory,
        AnthropicMessagesHttpSseOptions? options, AnthropicMessagesOptions? messagesOptions,
        AnthropicMessagesHooks? hooks, Func<ChatRequest,CancellationToken,AnthropicMessagesPreparedRequest>? preparedFactory)
    {
        ArgumentNullException.ThrowIfNull(client); ArgumentNullException.ThrowIfNull(requestFactory);
        if (hooks?.OnResponse is { } responseHook && responseHook.GetInvocationList().Length != 1 ||
            hooks?.OnProviderStreamEvent is { } providerHook && providerHook.GetInvocationList().Length != 1 ||
            hooks?.OnPayload is { } payloadHook && payloadHook.GetInvocationList().Length != 1)
            throw new ArgumentException("Anthropic hooks require single callbacks.",nameof(hooks));
        if (hooks?.OnPayload is not null && preparedFactory is null)
            throw new ArgumentException("OnPayload requires the structured FromPrepared route.", nameof(hooks));
        _preparedFactory = preparedFactory;
        _requestFactory = requestFactory; _options = options ?? new();
        if (_options.MaximumDataEvents <= 0 || _options.MaximumDataCharacters <= 0 || _options.MaximumTotalDataCharacters <= 0 ||
            _options.MaximumJsonDepth is < 1 or > PiSharp.Contracts.JsonData.MaximumDepth) throw new ArgumentOutOfRangeException(nameof(options), "Invalid Anthropic HTTP/SSE limits.");
        var framing = _options.Framing ?? new(RejectInvalidUtf8: true, EofBehavior: SseEofBehavior.DispatchPendingEvent);
        if (!framing.RejectInvalidUtf8 || framing.EofBehavior != SseEofBehavior.DispatchPendingEvent)
            throw new ArgumentException("Anthropic SSE requires strict UTF-8 and pending-event dispatch at EOF.", nameof(options));
        _http = new(client, framing) { Rejection = RejectAsync }; _messages = new((request, token) => ReadDtosAsync(request, new(), token), messagesOptions);
        _framing = framing; _messagesOptions = messagesOptions; _hooks = hooks;
    }
    public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        if (_hooks is not null) return StreamWithHooksAsync(request, cancellationToken);
        var diagnostics = new AnthropicRequestDiagnostics();
        return new AnthropicMessagesTransport((current, token) => ReadDtosAsync(current, diagnostics, token), _messagesOptions)
        { RequestDiagnostics = diagnostics }.StreamAsync(request, cancellationToken);
    }
    private TimeProvider Clock => _messagesOptions?.Clock ?? TimeProvider.System;

    private async IAsyncEnumerable<StreamEvent> StreamWithHooksAsync(ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = new HookSession(this, request, cancellationToken);
        StreamTerminalEvent? terminal = null;
        var enumerationFinished = false;
        try
        {
            if (await session.PrepareAsync().ConfigureAwait(false))
            {
                var mapper = new AnthropicMessagesTransport(session.ReadDtosAsync, _messagesOptions) { RequestDiagnostics = session.Diagnostics };
                var iterator = mapper.StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
                try
                {
                    while (await session.MoveNextAsync(iterator).ConfigureAwait(false))
                    {
                        var frame = iterator.Current;
                        if (frame is StreamTerminalEvent ended) terminal = ended;
                        else yield return frame;
                    }
                }
                finally { await session.DisposeIteratorAsync(iterator).ConfigureAwait(false); }
            }
            enumerationFinished = true;
        }
        finally
        {
            await session.CleanupAsync().ConfigureAwait(false);
            if (!enumerationFinished && session.CleanupErrors.Count != 0)
                throw new AggregateException("Anthropic stream cleanup failed.",session.CleanupErrors);
        }
        // Every callback and physical owner settles before the terminal is released.
        terminal ??= session.Failure(null, AnthropicMessagesFailure.SourceFailed);
        if (cancellationToken.IsCancellationRequested) terminal = session.Failure(terminal.Message, AnthropicMessagesFailure.Cancelled);
        else if (session.CleanupErrors.Count != 0) terminal = session.Failure(terminal.Message, AnthropicMessagesFailure.CleanupFailed);
        yield return terminal with
        {
            NativeSourceTask = session.PrimaryTask,
            NativeSourceException = session.PrimaryError,
            NativeCleanupTasks = session.CleanupTasks.ToArray(),
            NativeCleanupExceptions = session.CleanupErrors.ToArray()
        };
    }

    private sealed class HookSession(AnthropicMessagesHttpSseTransport transport, ChatRequest request, CancellationToken token)
    {
        private sealed class Original(Task task)
        { internal Task Task = task; internal bool Settled; internal object? Value; internal Exception? Direct; internal AggregateException? Aggregate; }
        private readonly Dictionary<Task,Original> _originals = new(ReferenceEqualityComparer.Instance);
        private HttpRequestMessage? _request;
        private HttpSseTransport.OwnedResponse? _response;
        private Stream? _body;
        internal Task? PrimaryTask;
        internal Exception? PrimaryError;
        internal List<Task> CleanupTasks = [];
        internal List<Exception> CleanupErrors = [];
        internal readonly AnthropicRequestDiagnostics Diagnostics = new();
        private Original Register(Task task) { if (!_originals.TryGetValue(task, out var row)) { row = new(task); _originals.Add(task,row); } return row; }
        private async Task<T> Await<T>(Task<T> actual)
        {
            var row = Register(actual);
            if (row.Settled) { if (row.Direct is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(row.Direct).Throw(); return (T)row.Value!; }
            try { var value = await actual.ConfigureAwait(false); row.Value = value; return value; }
            catch (Exception error) { row.Direct = error; row.Aggregate = actual.Exception; throw; }
            finally { row.Settled = true; }
        }
        private async Task Await(Task actual)
        {
            var row = Register(actual);
            if (row.Settled) { if (row.Direct is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(row.Direct).Throw(); return; }
            try { await actual.ConfigureAwait(false); }
            catch (Exception error) { row.Direct = error; row.Aggregate = actual.Exception; throw; }
            finally { row.Settled = true; }
        }
        private void Primary(Exception error,Task? actual = null) { PrimaryError ??= error; PrimaryTask ??= actual; }
        private static Task Invoke<T>(Func<T,ModelDescriptor,CancellationToken,Task> hook,T value,ModelDescriptor model,CancellationToken cancellation,string name)
        {
            // Only invocation is guarded here; returned Task faults/cancellation stay genuine.
            try { return hook(value,model,cancellation) ?? throw Protocol(); }
            catch (OperationCanceledException cause) { throw new AnthropicMessagesHookInvocationException(name,cause); }
        }
        internal async Task<bool> PrepareAsync()
        {
            Task? actual = null;
            try
            {
                token.ThrowIfCancellationRequested();
                var prepared = transport._hooks!.OnPayload is null ? null : transport._preparedFactory!(request, token) ?? throw Protocol();
                Func<CancellationToken, HttpRequestMessage>? fallback = null;
                // Pi 1.2.0: a request whose strict tool set Anthropic rejects is rebuilt once with strict "prefer" tools sent
                // non-strict, and onPayload sees the rebuilt payload too; a second rejection is the result.
                for (var attempt = 0; ; attempt++)
                {
                    if (prepared is not null)
                    {
                        token.ThrowIfCancellationRequested();
                        prepared.Claim();
                        Task<JsonData?>? callback = null;
                        JsonData? replacement;
                        try
                        {
                            try { callback = transport._hooks!.OnPayload!(prepared.Payload, request.Model, token) ?? throw Protocol(); }
                            catch (OperationCanceledException cause) { throw new AnthropicMessagesHookInvocationException("onPayload", cause); }
                            replacement = await Await(callback).ConfigureAwait(false);
                        }
                        catch (Exception error) { Primary(error, callback); throw; }
                        token.ThrowIfCancellationRequested();
                        _request = prepared.CompleteClaimed(replacement, token);
                    }
                    else _request = (attempt == 0 ? transport._requestFactory(request,token) : fallback!(token)) ?? throw Protocol();
                    actual = transport._http.PrepareOwnedAsync(_request,token).AsTask();
                    try { _response = await Await((Task<HttpSseTransport.OwnedResponse>)actual).ConfigureAwait(false); break; }
                    catch (Exception error) when (attempt == 0 && !token.IsCancellationRequested && AnthropicStrictToolsFallback.IsStrictToolLimitRejection(error) &&
                        (prepared is not null ? prepared.Fallback is not null : AnthropicStrictToolsFallback.FallbackOf(_request) is not null))
                    {
                        Diagnostics.Add(AnthropicStrictToolsFallback.Diagnostic(error, transport.Clock));
                        if (prepared is not null) prepared = prepared.Fallback!(token) ?? throw Protocol();
                        else fallback = AnthropicStrictToolsFallback.FallbackOf(_request);
                        var rejected = _request; _request = null; rejected.Dispose();
                    }
                }
                actual = _response.AcquireBodyAsync(token).AsTask();
                _body = await Await((Task<Stream>)actual).ConfigureAwait(false);
                // Acquisition claims the body but performs no SSE read or mapper mutation.
                if (transport._hooks!.OnResponse is { } hook)
                {
                    actual = _response.InspectAsync(async (physical,cancellation) =>
                    {
                        var headers = physical.Headers.Concat(physical.Content.Headers).GroupBy(h=>h.Key,StringComparer.OrdinalIgnoreCase)
                            .ToImmutableDictionary(group=>group.Key.ToLowerInvariant(),group=>string.Join(", ",group.SelectMany(h=>h.Value)),StringComparer.Ordinal);
                        Task? callback = null;
                        try { callback = Invoke(hook,new AnthropicMessagesResponseInfo((int)physical.StatusCode,headers),request.Model,cancellation,"onResponse");await Await(callback).ConfigureAwait(false); }
                        catch (Exception error) { Primary(error,callback); throw; }
                    },token).AsTask();
                    await Await(actual).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested(); return true;
            }
            catch (Exception error) { Primary(error,actual); return false; }
        }
        internal async Task<bool> MoveNextAsync(IAsyncEnumerator<StreamEvent> iterator)
        {
            var actual = iterator.MoveNextAsync().AsTask();
            try { return await Await(actual).ConfigureAwait(false); }
            catch (Exception error) { Primary(error,actual); return false; }
        }
        internal Task DisposeIteratorAsync(IAsyncEnumerator<StreamEvent> iterator)
        {
            try { return CleanupTask(iterator.DisposeAsync().AsTask()); }
            catch (Exception error) { CleanupErrors.Add(error); return Task.CompletedTask; }
        }
        internal async IAsyncEnumerable<JsonData> ReadDtosAsync(ChatRequest _,[EnumeratorCancellation] CancellationToken cancellation)
        {
            var count = 0; long characters = 0;
            var events = new SseDecoder(transport._framing).DecodeAsync(_body!,leaveOpen:true,cancellation).GetAsyncEnumerator(cancellation);
            try
            {
                while (true)
                {
                    var actual = events.MoveNextAsync().AsTask(); bool moved;
                    try { moved = await Await(actual).ConfigureAwait(false); }
                    catch (Exception error) { Primary(error,actual); throw; }
                    if (!moved) break;
                    cancellation.ThrowIfCancellationRequested();
                    var frame = events.Current; var data = frame.Data;
                    if (count++ >= transport._options.MaximumDataEvents || data.Length > transport._options.MaximumDataCharacters ||
                        data.Length > transport._options.MaximumTotalDataCharacters - characters) throw Limit();
                    characters += data.Length;
                    if (frame.EventType == "error") throw ErrorEvent(data);
                    if (!MessageEvents.Contains(frame.EventType)) continue;
                    var dto = transport.Parse(data);
                    if (transport._hooks!.OnProviderStreamEvent is { } hook)
                    {
                        Task? callback = null;
                        try { callback = Invoke(hook,dto,request.Model,cancellation,"onProviderStreamEvent");await Await(callback).ConfigureAwait(false); }
                        catch (Exception error) { Primary(error,callback); throw; }
                    }
                    cancellation.ThrowIfCancellationRequested();
                    yield return dto; // Hook completed before the mapper can mutate its state.
                }
            }
            finally
            {
                Task? disposal = null;
                try { disposal = events.DisposeAsync().AsTask(); } catch (Exception error) { CleanupErrors.Add(error); }
                if (disposal is not null) await CleanupTask(disposal).ConfigureAwait(false);
            }
        }
        private async Task CleanupTask(Task actual)
        {
            if (!CleanupTasks.Contains(actual,ReferenceEqualityComparer.Instance)) CleanupTasks.Add(actual);
            try { await Await(actual).ConfigureAwait(false); }
            catch (Exception error) { if (!CleanupErrors.Contains(error,ReferenceEqualityComparer.Instance)) CleanupErrors.Add(error);var aggregate = _originals[actual].Aggregate;if(aggregate is not null&&!CleanupErrors.Contains(aggregate,ReferenceEqualityComparer.Instance))CleanupErrors.Add(aggregate); }
        }
        internal async Task CleanupAsync()
        {
            // Cache the first body-disposal fault before the shared owner's finally can throw a response sibling.
            if (_body is not null)
                try { await CleanupTask(_body.DisposeAsync().AsTask()).ConfigureAwait(false); } catch (Exception error) { CleanupErrors.Add(error); }
            if (_response is not null)
                try { await CleanupTask(_response.DisposeAsync().AsTask()).ConfigureAwait(false); } catch (Exception error) { CleanupErrors.Add(error); }
            try { _request?.Dispose(); } catch (Exception error) { CleanupErrors.Add(error); }
        }
        // A request that fails before its response still carries the diagnostics recorded on the way (upstream output.diagnostics).
        private JsonFields WithDiagnostics(JsonFields properties) => Diagnostics.Items is { Count: > 0 } items
            ? properties.Set("diagnostics", JsonData.Parse("[" + string.Join(",", items.Select(item => item.ToString())) + "]")) : properties;
        internal StreamTerminalEvent Failure(AssistantMessage? partial,AnthropicMessagesFailure failure)
        {
            var reason = failure == AnthropicMessagesFailure.Cancelled ? StopReason.Aborted : StopReason.Error;
            // Upstream records providerThinkingLevel on the output before any request work, so pre-stream failures keep it.
            var properties = (partial?.ExtraProperties ?? WithDiagnostics(transport._messagesOptions?.ProviderThinkingLevel is { } level
                    ? JsonFields.Empty.Set("providerThinkingLevel", JsonData.Parse(JsonSerializer.Serialize(level))) : JsonFields.Empty))
                .Set("errorMessage",JsonData.Parse(JsonSerializer.Serialize(reason == StopReason.Aborted ? "Anthropic stream was cancelled." :
                    PrimaryError is ProviderDisplayException shown ? shown.Message : "Anthropic stream did not complete.")))
                .Set("anthropicFailure",JsonData.Parse(JsonSerializer.Serialize(failure.ToString())));
            var message = partial ?? new(request.Model.Api,request.Model.Provider,request.Model.Id,request.Timestamp,[],TokenUsage.Zero,reason);
            return new StreamError(reason,message with {StopReason=reason,ExtraProperties=properties});
        }
    }

    private async IAsyncEnumerable<JsonData> ReadDtosAsync(ChatRequest request, AnthropicRequestDiagnostics diagnostics,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ownedRequest = _requestFactory(request, cancellationToken) ?? throw Protocol();
        HttpSseTransport.OwnedResponse? owned = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            (ownedRequest, owned) = await SendWithStrictToolsFallbackAsync(ownedRequest, diagnostics, cancellationToken).ConfigureAwait(false);
            // The owned response's disposal awaits body/response cleanup before the request scope ends.
            await foreach (var dto in ReadOwnedAsync(owned, cancellationToken).ConfigureAwait(false)) yield return dto;
        }
        finally
        {
            try { if (owned is not null) await owned.DisposeAsync().ConfigureAwait(false); }
            finally { ownedRequest.Dispose(); }
        }
    }

    /// <summary>Pi 1.2.0 stream(): a rejected request whose strict tool set exceeds Anthropic's limits is sent once more with strict
    /// "prefer" tools non-strict (recording the anthropic_strict_tools_fallback diagnostic); a second rejection is the result.</summary>
    private async Task<(HttpRequestMessage Request, HttpSseTransport.OwnedResponse Response)> SendWithStrictToolsFallbackAsync(
        HttpRequestMessage first, AnthropicRequestDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        try { return (first, await _http.PrepareOwnedAsync(first, cancellationToken).ConfigureAwait(false)); }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && AnthropicStrictToolsFallback.IsStrictToolLimitRejection(error) &&
            AnthropicStrictToolsFallback.FallbackOf(first) is not null)
        {
            diagnostics.Add(AnthropicStrictToolsFallback.Diagnostic(error, Clock));
            var second = AnthropicStrictToolsFallback.FallbackOf(first)!(cancellationToken) ?? throw Protocol();
            try { var response = await _http.PrepareOwnedAsync(second, cancellationToken).ConfigureAwait(false); first.Dispose(); return (second, response); }
            catch { second.Dispose(); throw; }
        }
    }

    private async IAsyncEnumerable<JsonData> ReadOwnedAsync(HttpSseTransport.OwnedResponse owned, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var count = 0; long characters = 0;
        await using var events = owned.ReadAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (await events.MoveNextAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = events.Current; var data = frame.Data;
            if (count++ >= _options.MaximumDataEvents || data.Length > _options.MaximumDataCharacters ||
                data.Length > _options.MaximumTotalDataCharacters - characters) throw Limit();
            characters += data.Length;
            // Pinned iterateAnthropicEvents checks the named error before parsing and ignores all other unnamed/unknown frames.
            if (frame.EventType == "error") throw ErrorEvent(data);
            if (!MessageEvents.Contains(frame.EventType)) continue;
            yield return Parse(data);
        }
    }
    /// <summary>@anthropic-ai/sdk APIError for a rejected response: APIError.makeMessage over the whole parsed body
    /// (anthropic-messages.ts shows error.message unchanged). A body over the stream budget keeps the status-only rejection.</summary>
    private async ValueTask<Exception> RejectAsync(HttpResponseMessage response, CancellationToken token)
    {
        var bytes = await ProviderErrorText.ReadBodyAsync(response, _options.MaximumTotalDataCharacters, token).ConfigureAwait(false);
        if (bytes is null) return new HttpSseRejectedException(response.StatusCode);
        var text = ProviderErrorText.FetchText(bytes);
        return new ProviderDisplayException(ProviderErrorText.AnthropicStatus((int)response.StatusCode, text))
        { Status = (int)response.StatusCode, ErrorType = ProviderErrorText.AnthropicErrorType(text) };
    }

    // anthropic-messages.ts iterateAnthropicEvents: a named error event throws new Error(sse.data) before parsing.
    private static ProviderDisplayException ErrorEvent(string data) => new(data, inStream: true);

    private JsonData Parse(string data)
    {
        try
        {
            using var document = JsonDocument.Parse(data, PiSharp.Contracts.JsonData.DocumentOptions);
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
        // The SDK JSON.parse's each event: a string value keeps a lone surrogate (as its escape in the owned value).
        else if (value.ValueKind == JsonValueKind.String) _ = JsonUtf16.GetString(value);
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
