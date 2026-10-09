using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.AI.Protocols.ProviderShared;
using PiSharp.Contracts;
using PiSharp.Contracts.Compatibility;

namespace PiSharp.AI.Protocols.GoogleGenerativeAI;

/// <summary>
/// Bounded REST stream reader with the framing of @google/genai 2.21.0 <c>ApiClient.processStreamResponse</c> (which the
/// google-generative-ai.ts and google-vertex.ts streams consume) and its <c>generateContentStream</c> JSON decoding:
/// <list type="bullet">
/// <item>Each read chunk is UTF-8 decoded in streaming mode (a leading byte-order mark dropped, an incomplete trailing sequence
/// never flushed) and first tried whole as JSON: an object whose <c>error.code</c> is in [400, 600) throws
/// <c>got status: ...</c> (<see cref="ProviderErrorText.GoogleStreamChunkError"/>).</item>
/// <item>The chunk is appended to a buffer split at the earliest of <c>\n\n</c>, <c>\r\r</c> and <c>\r\n\r\n</c>; a trimmed event that
/// starts with <c>data:</c> yields <c>JSON.parse</c> of the trimmed rest (its SyntaxError text when that fails); other events are
/// skipped.</item>
/// <item>At the end of the body any non-blank remainder throws <c>Incomplete JSON segment at the end</c>.</item>
/// </list>
/// Borrows the stream.
/// </summary>
public sealed class GoogleSseDecoder(GoogleGenerativeAIOptions options)
{
    private int _claimed;
    public async IAsyncEnumerable<JsonData> DecodeAsync(Stream input, [EnumeratorCancellation] CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(input); options.Validate();
        if (Interlocked.Exchange(ref _claimed, 1) != 0) throw new InvalidOperationException("Google decoder has one reader.");
        var bytes = new byte[options.ReadBufferBytes]; var encoding = new UTF8Encoding(false, false); var decoder = encoding.GetDecoder();
        var chars = new char[encoding.GetMaxCharCount(bytes.Length)];
        var buffer = new StringBuilder(); long total = 0; var events = 0; var first = true;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var count = await input.ReadAsync(bytes.AsMemory(), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (count == 0)
            {
                // TextDecoder.decode(value, { stream: true }) is never flushed: held partial bytes are dropped.
                if (ProviderErrorText.EcmaTrim(buffer.ToString()).Length > 0)
                    throw new GoogleGenerativeAIException(GoogleFailure.MalformedStream, "Incomplete JSON segment at the end");
                yield break;
            }
            var decoded = decoder.GetChars(bytes, 0, count, chars, 0, flush: false);
            total += decoded; if (total > options.MaximumStreamCharacters) throw GoogleData.Fail(GoogleFailure.ResourceLimit);
            var start = 0;
            if (first && decoded > 0) { first = false; if (chars[0] == '﻿') start = 1; }
            var chunk = new string(chars, start, decoded - start);
            if (ProviderErrorText.GoogleStreamChunkError(chunk) is { } apiError)
                throw new GoogleGenerativeAIException(GoogleFailure.ProviderError, apiError);
            // The delimiter with the earliest start is also the first to complete (none of the three can start inside another and
            // end before it), so scanning appended characters for a completed delimiter splits exactly where the SDK's indexOf does.
            foreach (var c in chunk)
            {
                buffer.Append(c);
                if (buffer.Length > options.MaximumFrameCharacters) throw GoogleData.Fail(GoogleFailure.ResourceLimit);
                var length = Delimiter(buffer); if (length == 0) continue;
                var trimmed = ProviderErrorText.EcmaTrim(buffer.ToString(0, buffer.Length - length)); buffer.Clear();
                if (!trimmed.StartsWith("data:", StringComparison.Ordinal)) continue;
                if (++events > options.MaximumEvents) throw GoogleData.Fail(GoogleFailure.ResourceLimit);
                yield return GoogleData.Admit(Parse(ProviderErrorText.EcmaTrim(trimmed[5..])), options);
            }
        }
    }

    /// <summary>The length of the delimiter (\n\n, \r\r or \r\n\r\n) that <paramref name="buffer"/> now ends with, or 0.</summary>
    private static int Delimiter(StringBuilder buffer)
    {
        var n = buffer.Length; if (n < 2) return 0;
        var last = buffer[n - 1]; var previous = buffer[n - 2];
        if (last == '\n' && n >= 4 && previous == '\r' && buffer[n - 3] == '\n' && buffer[n - 4] == '\r') return 4;
        return last == previous && last is '\n' or '\r' ? 2 : 0;
    }

    /// <summary>HttpResponse.json(): JSON.parse of the event text, failing with the engine's SyntaxError message.</summary>
    private static JsonData Parse(string json)
    {
        // JSON.parse semantics: duplicate names keep the last value.
        try { return StreamingJson.JsonParse(json); }
        catch (JsonException)
        { throw new GoogleGenerativeAIException(GoogleFailure.MalformedStream, JsJsonSyntax.Describe(json, "Invalid Google streamed JSON.")); }
    }
}
