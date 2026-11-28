// Pi fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT): packages/ai/src/utils/sse.ts.
using System.Runtime.CompilerServices;
using System.Text;

namespace PiSharp.AI.Protocols.ProviderShared;

/// <summary>TextDecoder/SSE data fields for Pi's fetch-based providers. Framing limits cover comments and other fields too.</summary>
internal static class ProviderSseData
{
    internal static async IAsyncEnumerable<string> ReadAsync(Func<Memory<byte>, CancellationToken, ValueTask<int>> read,
        int readBufferBytes, int maximumFrameCharacters, long maximumTotalCharacters, Func<Exception> limitExceeded,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var bytes = new byte[readBufferBytes]; var encoding = new UTF8Encoding(false, false); var decoder = encoding.GetDecoder();
        var characters = new char[encoding.GetMaxCharCount(bytes.Length)]; var line = new StringBuilder();
        var data = new List<string>();
        long total = 0;
        long frameCharacters = 0;
        var firstCharacter = true;
        var skipLf = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await read(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var decoded = decoder.GetChars(bytes, 0, count, characters, 0, flush: count == 0);
            total += decoded;
            if (total > maximumTotalCharacters) throw limitExceeded();
            for (var i = 0; i < decoded; i++)
            {
                var c = characters[i];
                if (firstCharacter) { firstCharacter = false; if (c == '\uFEFF') continue; }
                if (skipLf) { skipLf = false; if (c == '\n') continue; }
                if (c is '\r' or '\n')
                {
                    skipLf = c == '\r';
                    if (line.Length > 0) ReadLine();
                    else if (Flush() is { } item) yield return item;
                    continue;
                }
                if (++frameCharacters > maximumFrameCharacters) throw limitExceeded();
                line.Append(c);
            }
            if (count != 0) continue;
            if (line.Length != 0) ReadLine();
            if (Flush() is { } last) yield return last;
            yield break;
        }

        void ReadLine()
        {
            var text = line.ToString();
            line.Clear();
            if (text == "data") data.Add("");
            else if (text.StartsWith("data:", StringComparison.Ordinal))
                data.Add(text[(text.StartsWith("data: ", StringComparison.Ordinal) ? 6 : 5)..]);
            frameCharacters++; // The separator between consecutive field lines is part of the frame.
        }

        string? Flush()
        {
            frameCharacters = 0;
            if (data.Count == 0) return null;
            var value = string.Join('\n', data);
            data.Clear();
            return value;
        }
    }
}
