// Pi d86654abb8862e201933517d6f1fce9f88dd117f (MIT): api/azure-openai-responses.ts.
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.AI.Transports;
using PiSharp.Contracts;
using PiSharp.Contracts.Compatibility;

namespace PiSharp.AI.Protocols.AzureResponses;

/// <summary>One explicit-key HTTP attempt. Borrows the client; joins owned request, response and body before settlement.</summary>
public sealed class AzureResponsesTransport : IChatTransport
{
    private readonly HttpClient _client;
    private readonly AzureResponsesRequestFactory _factory;
    private readonly string _key;
    private readonly SseDecoderOptions _framing;
    private readonly AzureResponsesOptions _options;

    public AzureResponsesTransport(HttpClient client, AzureResponsesRequestFactory factory, string explicitApiKey)
    {
        ArgumentNullException.ThrowIfNull(client); ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(explicitApiKey);
        _client = client; _factory = factory; _key = explicitApiKey; _options = factory.Options;
        var http = _options.HttpOptions;
        if (http.MaximumDataEvents <= 0 || http.MaximumDataCharacters <= 0 || http.MaximumTotalDataCharacters <= 0 || http.MaximumJsonDepth is < 1 or > 64)
            throw new ArgumentException("Invalid Azure Responses SSE limits.", nameof(factory));
        _framing = http.Framing ?? new(RejectInvalidUtf8: true);
        if (!_framing.RejectInvalidUtf8) throw new ArgumentException("Azure Responses requires strict UTF-8.", nameof(factory));
        _ = new SseDecoder(_framing);
        _ = new ResponsesTextToolTransport((_, _) => Empty(), _options.StreamOptions);
    }

    private static async IAsyncEnumerable<JsonData> Empty() { await Task.CompletedTask; yield break; }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fallback = new AssistantMessage(request.Model.Api, request.Model.Provider, request.Model.Id, request.Timestamp, [], TokenUsage.Zero, StopReason.Pending);
        var reducer = new AssistantStreamReducer(fallback, new(_options.StreamOptions.MaximumContentSlots, _options.StreamOptions.MaximumContentCharacters));
        HttpRequestMessage? ownedRequest = null;
        HttpSseTransport.OwnedResponse? owner = null;
        IAsyncEnumerator<StreamEvent>? mapped = null;
        CancellationTokenSource? timeout = null;
        var token = cancellationToken;
        Exception? failure = null, cleanup = null;
        StreamTerminalEvent? terminal = null;
        var settled = false;
        // The outer Azure enumeration is the sole physical response owner. DTO disposal joins framing only;
        // physical release follows mapper disposal, so release faults cannot overwrite its primary failure.
        try
        {
            try
            {
                ownedRequest = await _factory.CreateAsync(request, _key, token).ConfigureAwait(false);
                if (_options.TimeoutMilliseconds is { } milliseconds)
                { timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(milliseconds); token = timeout.Token; }
                token.ThrowIfCancellationRequested();
                var response = await _client.SendAsync(ownedRequest, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                owner = new(response, _framing); // Ownership transfers before cancellation or any callback.
                token.ThrowIfCancellationRequested();
                if (!response.IsSuccessStatusCode) throw new AzureHttpFailure(await ReadErrorAsync(owner, (int)response.StatusCode, token).ConfigureAwait(false));
                if (_options.Hooks.OnResponse is { } inspect)
                    await owner.InspectAsync(async (borrowed, ct) =>
                    {
                        var headers = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var header in borrowed.Headers.Concat(borrowed.Content.Headers))
                        {
                            var value = string.Join(", ", header.Value);
                            if (headers.Count >= _options.MaximumHeaders || header.Key.Length > _options.MaximumHeaderCharacters || value.Length > _options.MaximumHeaderCharacters)
                                throw new StreamLimitException("Azure Responses response headers exceed configured limits.");
                            headers[header.Key] = value;
                        }
                        await inspect(new((int)borrowed.StatusCode, headers.ToImmutable()), _factory.Model, ct).ConfigureAwait(false);
                    }, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var transferred = owner!;
                mapped = new ResponsesTextToolTransport((_, ct) => ReadDtosAsync(transferred, ct), _options.StreamOptions)
                    .StreamAsync(request, token).GetAsyncEnumerator(token);
            }
            catch (Exception error) { failure = error; }
            while (mapped is not null && failure is null)
            {
                StreamEvent? progress = null;
                try
                {
                    if (!await mapped.MoveNextAsync().ConfigureAwait(false))
                    { if (terminal is null) failure = new StreamProtocolException("Azure Responses stream ended without settlement."); break; }
                    progress = mapped.Current;
                    if (progress is StreamTerminalEvent final)
                    {
                        terminal = final;
                        if (final is StreamError)
                            failure = new AzureSettledFailure(final.Message.ExtraProperties?.TryGet("errorMessage", out var detail) == true && detail!.Value.ValueKind == JsonValueKind.String
                                ? detail.Value.GetString()! : "Azure Responses stream did not complete.");
                        break;
                    }
                    reducer.Apply(progress);
                }
                catch (Exception error) { failure = error; break; }
                yield return progress!;
            }
            settled = true;
        }
        finally
        {
            // Disposal of the original mapper joins its original DTO/SSE enumerator. No replacement pull is started.
            try { if (mapped is not null) await mapped.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { cleanup = error; }
            try { if (owner is not null) await owner.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { cleanup ??= error; }
            try { ownedRequest?.Dispose(); } catch (Exception error) { cleanup ??= error; }
            timeout?.Dispose();
            if (!settled && cleanup is not null) throw new IOException("Azure Responses cleanup failed.");
        }
        if (failure is null && token.IsCancellationRequested) failure = new OperationCanceledException(token);
        if (failure is null && cleanup is null && terminal is not null) { yield return terminal; yield break; }
        var primary = failure ?? cleanup!;
        var cancelled = cancellationToken.IsCancellationRequested;
        var message = primary switch
        {
            AzureHttpFailure http => http.Message,
            AzureSettledFailure settledFailure => settledFailure.Message,
            OperationCanceledException when cancelled => "Request was aborted",
            OperationCanceledException => "Azure Responses request timed out.",
            StreamLimitException or AzureResponsesException { Failure: AzureResponsesFailure.ResourceLimit } => "Azure Responses input exceeds configured limits.",
            StreamProtocolException or JsonException => "Invalid or incomplete Azure Responses stream.",
            AzureResponsesException azure => azure.Message,
            ResponsesProjectionException projection => projection.Message,
            _ when failure is null => "Azure Responses cleanup failed.",
            _ => "Azure Responses request failed."
        };
        var snapshot = terminal?.Message ?? reducer.Snapshot();
        var reason = cancelled ? StopReason.Aborted : StopReason.Error;
        var properties = (snapshot.ExtraProperties ?? JsonFields.Empty).Set("errorMessage", JsonData.Parse(JsonSerializer.Serialize(message)));
        if (cleanup is not null) properties = properties.Set("azureCleanupFailed", JsonData.Parse("true"));
        yield return new StreamError(reason, snapshot with { StopReason = reason, ExtraProperties = properties });
    }

    private async IAsyncEnumerable<JsonData> ReadDtosAsync(HttpSseTransport.OwnedResponse owner, [EnumeratorCancellation] CancellationToken token)
    {
        Exception? primary = null;
        {
            var count = 0; long total = 0; var limits = _options.HttpOptions;
            await using var frames = owner.ReadAsync(token).GetAsyncEnumerator(token);
            while (true)
            {
                JsonData? dto = null;
                try
                {
                    if (!await frames.MoveNextAsync().ConfigureAwait(false)) break;
                    token.ThrowIfCancellationRequested(); var data = frames.Current.Data;
                    if (++count > limits.MaximumDataEvents || data.Length > limits.MaximumDataCharacters || data.Length > limits.MaximumTotalDataCharacters - total)
                        throw new StreamLimitException("Azure Responses SSE data exceeds configured limits.");
                    total += data.Length;
                    if (data == "[DONE]") break;
                    using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = limits.MaximumJsonDepth });
                    if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                    dto = JsonData.FromElement(document.RootElement);
                    if (_options.Hooks.OnProviderStreamEvent is { } hook) await hook(dto, _factory.Model, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                }
                catch (SseDecodeException error)
                {
                    primary = error.Failure == SseDecodeFailure.InvalidUtf8 ? new StreamProtocolException("Invalid Azure Responses SSE UTF-8.")
                        : new StreamLimitException("Azure Responses SSE framing exceeds configured limits."); break;
                }
                catch (JsonException) { primary = new StreamProtocolException("Invalid Azure Responses SSE JSON data."); break; }
                catch (Exception error) { primary = error; break; }
                yield return dto!;
            }
        }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }

    private async Task<string> ReadErrorAsync(HttpSseTransport.OwnedResponse owner, int status, CancellationToken token)
    {
        var stream = await owner.AcquireBodyAsync(token).ConfigureAwait(false);
        using var bytes = new MemoryStream(); var buffer = new byte[Math.Min(4096, _options.MaximumErrorBodyBytes)];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false); if (read == 0) break;
            if (read > _options.MaximumErrorBodyBytes - bytes.Length) throw new StreamLimitException("Azure Responses HTTP error body exceeds configured limits.");
            bytes.Write(buffer, 0, read);
        }
        string raw;
        try { raw = new UTF8Encoding(false, true).GetString(bytes.ToArray()); }
        catch (DecoderFallbackException) { throw new StreamProtocolException("Invalid Azure Responses error body UTF-8."); }
        string? innerBody = null, sdkMessage = null;
        try
        {
            var body = JsonData.Parse(raw);
            if (body.Value.ValueKind != JsonValueKind.Object) throw new StreamProtocolException("Unsupported Azure Responses HTTP error shape.");
            var inner = body.Value;
            if (body.Value.TryGetProperty("error", out var supplied) && supplied.ValueKind != JsonValueKind.Null) inner = supplied;
            if (inner.ValueKind != JsonValueKind.Object) throw new StreamProtocolException("Unsupported Azure Responses HTTP error shape.");
            {
                var compact = EcmaScriptJsonProjection.Project(JsonData.FromElement(inner));
                if (inner.EnumerateObject().Any()) innerBody = Truncate(compact.Trim());
                sdkMessage = inner.TryGetProperty("message", out var text) && Truthy(text)
                    ? text.ValueKind == JsonValueKind.String ? text.GetString() : EcmaScriptJsonProjection.Project(JsonData.FromElement(text)) : compact;
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or EcmaScriptJsonProjectionException) { }
        sdkMessage = string.IsNullOrEmpty(sdkMessage ?? raw) ? $"{status} status code (no body)" : $"{status} {sdkMessage ?? raw}";
        var detail = innerBody is not null && !sdkMessage.Contains(innerBody, StringComparison.Ordinal) ? innerBody : sdkMessage;
        return $"Azure OpenAI API error ({status}): {detail}";
    }
    private static string Truncate(string text) => text.Length <= 4000 ? text : text[..4000] + $"... [truncated {text.Length - 4000} chars]";
    private static bool Truthy(JsonElement value) => value.ValueKind switch
    { JsonValueKind.Null or JsonValueKind.False => false, JsonValueKind.String => !string.IsNullOrEmpty(value.GetString()), JsonValueKind.Number => value.GetDouble() != 0, _ => true };
    private sealed class AzureHttpFailure(string message) : Exception(message);
    private sealed class AzureSettledFailure(string message) : Exception(message);
}
