// Pi fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT): packages/ai/src/utils/sse.ts and
// packages/ai/src/api/pi-messages.ts readPiMessagesEvents/parsePiMessagesEvent (MIT).
using System.Runtime.CompilerServices;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.PiMessages;

/// <summary>Incremental Pi SSE framing. Borrows input; the invocation owns reader/response disposal.</summary>
public sealed class PiMessagesSseDecoder
{
    private readonly PiMessagesOptions _options;
    private int _claimed;
    public PiMessagesSseDecoder(PiMessagesOptions options) { ArgumentNullException.ThrowIfNull(options); options.Validate(); _options = options; }
    public async IAsyncEnumerable<JsonData> DecodeAsync(Stream input, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (Interlocked.Exchange(ref _claimed, 1) != 0) throw new InvalidOperationException("Pi Messages decoder has one reader.");
        var events = 0;
        await foreach (var frame in ProviderShared.ProviderSseData.ReadAsync(input.ReadAsync, _options.ReadBufferBytes,
            _options.MaximumFrameCharacters, _options.MaximumTotalDataCharacters, () => PiMessagesData.Fail(PiMessagesFailure.ResourceLimit),
            cancellationToken).ConfigureAwait(false))
        {
            var value = TrimJs(frame);
            if (value.Length == 0 || value == "[DONE]") continue;
            var parsed = StreamingJson.JsonParse(value);
            if (parsed.Value.ValueKind == System.Text.Json.JsonValueKind.Null) continue;
            if (++events > _options.MaximumDataEvents) throw PiMessagesData.Fail(PiMessagesFailure.ResourceLimit);
            yield return PiMessagesData.Admit(parsed, _options);
        }
    }
    private static string TrimJs(string value)
    {
        static bool White(char c) => c is '\t' or '\n' or '\v' or '\f' or '\r' or ' ' or '\u00A0' or '\u1680' or '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000' or '\uFEFF' || c is >= '\u2000' and <= '\u200A';
        var start = 0; var end = value.Length; while (start < end && White(value[start])) start++; while (end > start && White(value[end - 1])) end--;
        return value[start..end];
    }
}
