using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.OpenAICompletions;

/// <summary>An invocation-owned replay snapshot of the already materialized request, never a second payload callback.</summary>
internal sealed class CompletionsRetryRequest
{
    private readonly HttpMethod _method;
    private readonly Uri? _uri;
    private readonly Version _version;
    private readonly HttpVersionPolicy _versionPolicy;
    private readonly ImmutableArray<KeyValuePair<string, string[]>> _headers, _contentHeaders;
    private readonly ImmutableArray<KeyValuePair<string, object?>> _options;
    private readonly byte[]? _body;

    private CompletionsRetryRequest(HttpRequestMessage request, byte[]? body, CompletionsRetryOptions options)
    {
        _method = request.Method; _uri = request.RequestUri; _version = request.Version; _versionPolicy = request.VersionPolicy;
        var metadata = ImmutableArray.CreateBuilder<KeyValuePair<string, object?>>();
        foreach (var pair in request.Options)
        { if (metadata.Count >= options.MaximumRequestOptions) throw Limit(); metadata.Add(pair); }
        _options = metadata.ToImmutable(); _body = body;
        long characters = 0; var count = 0;
        ImmutableArray<KeyValuePair<string, string[]>> Capture(IEnumerable<KeyValuePair<string, IEnumerable<string>>> source)
        {
            var result = ImmutableArray.CreateBuilder<KeyValuePair<string, string[]>>();
            foreach (var pair in source)
            {
                if (++count > options.MaximumRequestHeaders) throw Limit();
                characters += pair.Key.Length; if (characters > options.MaximumRequestHeaderCharacters) throw Limit();
                var values = new List<string>();
                foreach (var value in pair.Value)
                { characters += value.Length + (values.Count == 0 ? 0 : 2); if (characters > options.MaximumRequestHeaderCharacters) throw Limit(); values.Add(value); }
                result.Add(new(pair.Key, values.ToArray()));
            }
            return result.ToImmutable();
        }
        _headers = Capture(request.Headers);
        _contentHeaders = request.Content is null ? [] : Capture(request.Content.Headers);
    }

    internal static async ValueTask<CompletionsRetryRequest> CaptureAsync(HttpRequestMessage request, CompletionsRetryOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); byte[]? bytes = null;
        if (request.Content is { } content)
        {
            if (content.Headers.ContentLength is { } length && length > options.MaximumRequestBodyBytes) throw Limit();
            await content.LoadIntoBufferAsync(options.MaximumRequestBodyBytes, token).ConfigureAwait(false);
            bytes = await content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            if (bytes.Length > options.MaximumRequestBodyBytes) throw Limit();
        }
        token.ThrowIfCancellationRequested(); return new(request, bytes, options);
    }

    internal HttpRequestMessage Create()
    {
        var request = new HttpRequestMessage(_method, _uri) { Version = _version, VersionPolicy = _versionPolicy };
        try
        {
            foreach (var pair in _options) request.Options.Set(new HttpRequestOptionsKey<object?>(pair.Key), pair.Value);
            foreach (var pair in _headers) if (!request.Headers.TryAddWithoutValidation(pair.Key, pair.Value)) throw Protocol();
            if (_body is not null)
            {
                request.Content = new ByteArrayContent(_body);
                foreach (var pair in _contentHeaders) if (!request.Content.Headers.TryAddWithoutValidation(pair.Key, pair.Value)) throw Protocol();
            }
            return request;
        }
        catch { request.Dispose(); throw; }
    }

    private static StreamLimitException Limit() => new("Completions retry request exceeds configured limits.");
    private static StreamProtocolException Protocol() => new("Invalid Completions replay request.");
}
