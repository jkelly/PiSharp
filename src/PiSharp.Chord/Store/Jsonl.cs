// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/jsonl.ts
//
// Plain JSONL for archives (slice 6a §13): one part per line, UTF-8. The store yields and takes parts; this is the encoding it ships
// (others, compressed ones, come later without touching the applier). Upstream's AsyncIterable<Uint8Array> is an
// IAsyncEnumerable<ReadOnlyMemory<byte>> both ways; a part is written as JSON.stringify writes it (ChordJson.Stringify) and read as
// JSON.parse reads it (a JsonDocument made into a mutable tree by JsonUtf16.MutableNode, which keeps a lone surrogate's escape and a
// repeated name's last value). TextEncoder is UTF-8 (a lone surrogate would be
// U+FFFD, but JSON.stringify escapes one); TextDecoder("utf-8", { fatal: true }) is a strict UTF-8 decoder that, as TextDecoder does,
// drops a byte order mark at the start of the stream; String.prototype.trim's whitespace decides which lines are blank.
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Contracts;

namespace PiSharp.Chord.Store;

public static partial class Tables
{
    /// <summary><paramref name="parts"/> (an export's, or any archive parts) as JSONL bytes, in chunks of whole lines (upstream
    /// <c>encodeJsonl</c>).</summary>
    public static async IAsyncEnumerable<ReadOnlyMemory<byte>> EncodeJsonl(IAsyncEnumerable<JsonObject> parts,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var lines = new StringBuilder();
        await foreach (var part in parts.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            ChordJson.Stringify(lines, part);
            lines.Append('\n');
            if (lines.Length >= StoreInternal.JsonlChunk)
            {
                yield return Encoding.UTF8.GetBytes(lines.ToString());
                lines.Clear();
            }
        }
        if (lines.Length > 0) yield return Encoding.UTF8.GetBytes(lines.ToString());
    }

    /// <summary>The values of JSONL <paramref name="bytes"/>, one per nonempty line, for <see cref="ImportStoreAsync(Backend,
    /// IAsyncEnumerable{JsonNode}, ChordContext, StoreOptions)"/> to check (a trust boundary: bytes that are no UTF-8 and a line that is no
    /// JSON are <c>invalid/args</c>, naming the line; upstream <c>decodeJsonl</c>).</summary>
    public static async IAsyncEnumerable<JsonNode?> DecodeJsonl(IAsyncEnumerable<ReadOnlyMemory<byte>> bytes,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetDecoder();
        var rest = "";
        long line = 0;
        var started = false;
        JsonNode? Parse(string text)
        {
            try
            {
                // Built from the document, so a repeated name keeps its last value, as JSON.parse does.
                using var document = JsonDocument.Parse(text, JsonData.DocumentOptions);
                return JsonUtf16.MutableNode(document.RootElement);
            }
            catch (JsonException)
            {
                throw DataInternal.Invalid("args", $"jsonl: line {line} is not JSON");
            }
        }
        string Decode(ReadOnlySpan<byte> chunk, bool flush)
        {
            string text;
            try
            {
                var chars = new char[decoder.GetCharCount(chunk, flush)];
                text = new string(chars, 0, decoder.GetChars(chunk, chars, flush));
            }
            catch (DecoderFallbackException)
            {
                throw DataInternal.Invalid("args", $"jsonl: line {line + 1} is not UTF-8");
            }
            // TextDecoder drops a byte order mark at the start of the stream.
            if (!started && text.Length > 0)
            {
                started = true;
                if (text[0] == '\uFEFF') text = text[1..];
            }
            return text;
        }
        await foreach (var chunk in bytes.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            rest += Decode(chunk.Span, flush: false);
            var start = 0;
            for (var end = rest.IndexOf('\n'); end >= 0; end = rest.IndexOf('\n', start))
            {
                var text = rest[start..end];
                start = end + 1;
                line++;
                if (!StoreInternal.IsBlank(text)) yield return Parse(text);
            }
            rest = rest[start..];
        }
        rest += Decode([], flush: true);
        line++;
        if (!StoreInternal.IsBlank(rest)) yield return Parse(rest);
    }
}

internal static partial class StoreInternal
{
    /// <summary>The UTF-16 length a chunk of encoded lines collects before it is yielded (upstream <c>CHUNK</c>, compared with a
    /// JavaScript string's length).</summary>
    internal const int JsonlChunk = 64 * 1024;

    /// <summary>Whether <paramref name="text"/> is empty after <c>String.prototype.trim</c> (ECMAScript's WhiteSpace and
    /// LineTerminator).</summary>
    internal static bool IsBlank(string text)
    {
        foreach (var character in text)
        {
            if (character is not ('\t' or '\n' or '\v' or '\f' or '\r' or ' ' or '\u00A0' or '\u1680' or (>= '\u2000' and <= '\u200A')
                or '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000' or '\uFEFF')) return false;
        }
        return true;
    }
}
