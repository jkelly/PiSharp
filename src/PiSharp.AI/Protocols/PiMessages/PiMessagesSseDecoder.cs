// Pi-specific framing follows Pi d86654abb8862e201933517d6f1fce9f88dd117f
// packages/ai/src/api/pi-messages.ts readPiMessagesEvents/parsePiMessagesEvent (MIT).
using System.Runtime.CompilerServices;
using System.Text;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.PiMessages;

/// <summary>First-data-line Pi framing. Borrows input; the invocation owns reader/response disposal.</summary>
public sealed class PiMessagesSseDecoder
{
    private readonly PiMessagesOptions _options;
    private int _claimed;
    public PiMessagesSseDecoder(PiMessagesOptions options) { ArgumentNullException.ThrowIfNull(options); options.Validate(); _options = options; }
    public async IAsyncEnumerable<JsonData> DecodeAsync(Stream input, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (Interlocked.Exchange(ref _claimed, 1) != 0) throw new InvalidOperationException("Pi Messages decoder has one reader.");
        var bytes = new byte[_options.ReadBufferBytes]; var encoding = new UTF8Encoding(false, false); var decoder = encoding.GetDecoder();
        var characters = new char[encoding.GetMaxCharCount(bytes.Length)]; var buffer = new StringBuilder();
        long total = 0; var events = 0; var firstCharacter = true;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await input.ReadAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var decoded = decoder.GetChars(bytes, 0, count, characters, 0, flush: count == 0);
            total += decoded; if (total > _options.MaximumTotalDataCharacters) throw PiMessagesData.Fail(PiMessagesFailure.ResourceLimit);
            var offset = 0;
            if (decoded > 0 && firstCharacter) { firstCharacter = false; if (characters[0] == '\uFEFF') offset = 1; }
            buffer.Append(characters, offset, decoded - offset); buffer.Replace("\r\n", "\n");
            var raw = buffer.ToString(); var consumed = 0;
            while (true)
            {
                var split = raw.IndexOf("\n\n", consumed, StringComparison.Ordinal); if (split < 0) break;
                var frame = raw[consumed..split]; consumed = split + 2;
                if (frame.Length > _options.MaximumFrameCharacters) throw PiMessagesData.Fail(PiMessagesFailure.ResourceLimit);
                var value = Parse(frame);
                if (value is not null) { if (++events > _options.MaximumDataEvents) throw PiMessagesData.Fail(PiMessagesFailure.ResourceLimit); yield return value; }
            }
            if (consumed != 0) buffer.Remove(0, consumed);
            if (buffer.Length > _options.MaximumFrameCharacters) throw PiMessagesData.Fail(PiMessagesFailure.ResourceLimit);
            if (count != 0) continue;
            if (TrimJs(buffer.ToString()).Length != 0)
            {
                var value = Parse(buffer.ToString());
                if (value is not null) { if (++events > _options.MaximumDataEvents) throw PiMessagesData.Fail(PiMessagesFailure.ResourceLimit); yield return value; }
            }
            yield break;
        }
    }
    private JsonData? Parse(string frame)
    {
        foreach (var line in frame.Split('\n'))
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = TrimJs(line[5..]);
            return data.Length == 0 || data == "[DONE]" ? null : PiMessagesData.Admit(JsonData.Parse(data), _options);
        }
        return null;
    }
    private static string TrimJs(string value)
    {
        static bool White(char c) => c is '\t' or '\n' or '\v' or '\f' or '\r' or ' ' or '\u00A0' or '\u1680' or '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000' or '\uFEFF' || c is >= '\u2000' and <= '\u200A';
        var start = 0; var end = value.Length; while (start < end && White(value[start])) start++; while (end > start && White(value[end - 1])) end--;
        return value[start..end];
    }
}
