// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/env/decode.ts.
// Decoding that matches `new TextDecoder().decode(bytes)` of the whole input when the input arrives in pieces. Node's streaming decoder with
// BOM handling can drop a U+FEFF that follows a chunk boundary, so upstream turns BOM handling off and drops a leading mark itself. .NET's
// UTF-8 decoder never drops a byte-order mark and replaces each maximal invalid subpart with one U+FFFD, as the WHATWG decoder does
// (env.line-scan pins both against Node-captured output).
using System.Text;

namespace PiSharp.Durable.Env;

/// <summary>Upstream <c>rangeDecoder</c> and <c>startsWithBom</c>.</summary>
public static class Utf8Decoding
{
    private static readonly UTF8Encoding Encoding = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>A streaming decoder for a byte range that keeps a byte-order mark; callers that start at the beginning of a file skip a
    /// leading mark themselves.</summary>
    public static Decoder RangeDecoder() => Encoding.GetDecoder();

    /// <summary>Whether decoding the whole input drops its first three bytes as a byte-order mark.</summary>
    public static bool StartsWithBom(ReadOnlySpan<byte> firstBytes) =>
        firstBytes.Length >= 3 && firstBytes[0] == 0xef && firstBytes[1] == 0xbb && firstBytes[2] == 0xbf;

    /// <summary>The text for <paramref name="bytes"/>, holding back an incomplete character unless <paramref name="flush"/>.</summary>
    internal static string Decode(Decoder decoder, ReadOnlySpan<byte> bytes, bool flush)
    {
        var count = decoder.GetCharCount(bytes, flush);
        if (count == 0)
        {
            // Still advance the decoder's state over the bytes.
            if (!bytes.IsEmpty || flush) decoder.GetChars(bytes, Span<char>.Empty, flush);
            return "";
        }
        var chars = new char[count];
        var written = decoder.GetChars(bytes, chars, flush);
        return new string(chars, 0, written);
    }

    /// <summary>The UTF-8 byte length of decoded text.</summary>
    internal static long ByteCount(string text) => text.Length == 0 ? 0 : Encoding.GetByteCount(text);
}

/// <summary>Decodes one stream chunk by chunk exactly like decoding all of it at once, dropping a leading byte-order mark (upstream
/// <c>StreamDecoder</c>).</summary>
public sealed class StreamDecoder
{
    private readonly Decoder decoder = Utf8Decoding.RangeDecoder();
    private bool started;

    /// <summary>Text for <paramref name="bytes"/>, holding back an incomplete character.</summary>
    public string Decode(ReadOnlySpan<byte> bytes) => Started(Utf8Decoding.Decode(decoder, bytes, flush: false));

    /// <summary>The end of the stream: what an incomplete final character decodes to.</summary>
    public string Finish() => Started(Utf8Decoding.Decode(decoder, ReadOnlySpan<byte>.Empty, flush: true));

    private string Started(string text)
    {
        if (started || text.Length == 0) return text;
        started = true;
        // U+FEFF encodes only as EF BB BF, so a leading U+FEFF is exactly a byte-order mark at the stream's start.
        return text[0] == '﻿' ? text[1..] : text;
    }
}
