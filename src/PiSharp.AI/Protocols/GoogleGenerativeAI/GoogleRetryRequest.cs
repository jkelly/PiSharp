using System.Collections.Immutable;

namespace PiSharp.AI.Protocols.GoogleGenerativeAI;

/// <summary>Invocation-owned snapshot of the prepared native factory request. Payload executes once.</summary>
internal sealed class GoogleRetryRequest
{
    private readonly HttpMethod _method;
    private readonly Uri? _uri;
    private readonly Version _version;
    private readonly HttpVersionPolicy _versionPolicy;
    private readonly byte[] _body;
    private readonly ImmutableArray<KeyValuePair<string, string[]>> _headers, _contentHeaders;
    private GoogleRetryRequest(HttpRequestMessage request, byte[] body, GoogleGenerativeAIOptions options)
    {
        _method = request.Method; _uri = request.RequestUri; _version = request.Version; _versionPolicy = request.VersionPolicy; _body = body;
        var count = 0;
        ImmutableArray<KeyValuePair<string, string[]>> Capture(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers)
        {
            var result = ImmutableArray.CreateBuilder<KeyValuePair<string, string[]>>();
            foreach (var pair in headers)
            {
                // Content-Length can be materialized by HttpContent buffering in addition
                // to the already admitted factory headers.
                if (++count > options.MaximumHeaders + 1) throw GoogleData.Fail(GoogleFailure.ResourceLimit);
                var values = pair.Value.ToArray();
                if (pair.Key.Length + values.Sum(v => (long)v.Length) + Math.Max(0, values.Length - 1) * 2 > options.MaximumHeaderCharacters)
                    throw GoogleData.Fail(GoogleFailure.ResourceLimit);
                result.Add(new(pair.Key, values));
            }
            return result.ToImmutable();
        }
        _headers = Capture(request.Headers); _contentHeaders = Capture(request.Content!.Headers);
    }
    internal static async ValueTask<GoogleRetryRequest> CaptureAsync(HttpRequestMessage request, GoogleGenerativeAIOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var content = request.Content ?? throw GoogleData.Fail(GoogleFailure.SourceFailed);
        await content.LoadIntoBufferAsync(options.MaximumPayloadBytes, token).ConfigureAwait(false);
        var bytes = await content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        if (bytes.Length > options.MaximumPayloadBytes) throw GoogleData.Fail(GoogleFailure.ResourceLimit);
        token.ThrowIfCancellationRequested(); return new(request, bytes, options);
    }
    internal HttpRequestMessage Create()
    {
        var request = new HttpRequestMessage(_method, _uri) { Version = _version, VersionPolicy = _versionPolicy };
        try
        {
            request.Content = new ByteArrayContent(_body);
            foreach (var pair in _headers)
                if (!request.Headers.TryAddWithoutValidation(pair.Key, pair.Value)) throw GoogleData.Fail(GoogleFailure.Configuration);
            foreach (var pair in _contentHeaders)
                if (!request.Content.Headers.TryAddWithoutValidation(pair.Key, pair.Value)) throw GoogleData.Fail(GoogleFailure.Configuration);
            return request;
        }
        catch { request.Dispose(); throw; }
    }
}
