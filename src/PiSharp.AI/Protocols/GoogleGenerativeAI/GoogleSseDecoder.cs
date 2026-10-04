using System.Runtime.CompilerServices;
using System.Text;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.GoogleGenerativeAI;

/// <summary>Bounded REST SSE reader. Borrows the stream; no SDK framing qualification is implied.</summary>
public sealed class GoogleSseDecoder(GoogleGenerativeAIOptions options)
{
    private int _claimed;
    public async IAsyncEnumerable<JsonData> DecodeAsync(Stream input, [EnumeratorCancellation] CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(input); options.Validate();
        if (Interlocked.Exchange(ref _claimed, 1) != 0) throw new InvalidOperationException("Google decoder has one reader.");
        var bytes = new byte[options.ReadBufferBytes]; var encoding = new UTF8Encoding(false, false); var decoder = encoding.GetDecoder();
        var chars = new char[encoding.GetMaxCharCount(bytes.Length)];
        var line = new StringBuilder(); var data = new StringBuilder();
        var frameCharacters = 0; long total = 0; var events = 0; var sawCr = false; var first = true;
        JsonData? CompleteLine()
        {
            var text = line.ToString(); line.Clear();
            if (text.Length == 0)
            {
                frameCharacters = 0;
                if (data.Length == 0) return null;
                var json = data.ToString(); data.Clear();
                if (json == "[DONE]") return null;
                if (++events > options.MaximumEvents) throw GoogleData.Fail(GoogleFailure.ResourceLimit);
                return GoogleData.Admit(JsonData.Parse(json), options);
            }
            if (text.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = text[5..]; if (value.StartsWith(' ')) value = value[1..];
                if (data.Length > 0) data.Append('\n'); data.Append(value);
            }
            return null;
        }
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var count = await input.ReadAsync(bytes.AsMemory(), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var decoded = decoder.GetChars(bytes, 0, count, chars, 0, flush: count == 0);
            total += decoded; if (total > options.MaximumStreamCharacters) throw GoogleData.Fail(GoogleFailure.ResourceLimit);
            for (var i = 0; i < decoded; i++)
            {
                var c = chars[i]; if (first) { first = false; if (c == '\uFEFF') continue; }
                if (sawCr) { sawCr = false; if (c == '\n') continue; }
                if (++frameCharacters > options.MaximumFrameCharacters) throw GoogleData.Fail(GoogleFailure.ResourceLimit);
                if (c is '\r' or '\n')
                {
                    if (c == '\r') sawCr = true;
                    var value = CompleteLine(); if (value is not null) yield return value;
                }
                else line.Append(c);
            }
            if (count != 0) continue;
            // This native REST profile accepts a final unterminated data frame.
            if (line.Length > 0) { var value = CompleteLine(); if (value is not null) yield return value; }
            if (data.Length > 0) { var value = CompleteLine(); if (value is not null) yield return value; }
            yield break;
        }
    }
}
