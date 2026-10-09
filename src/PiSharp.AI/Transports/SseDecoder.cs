using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace PiSharp.AI.Transports;

public enum SseEofBehavior { DiscardPendingEvent, DispatchPendingEvent }
public enum SseFramingProfile { Standard, OpenAISdk719 }
public enum SseDecodeFailure { InvalidUtf8, LineLimit, EventLimit }

public sealed class SseDecodeException(SseDecodeFailure failure, string message) : IOException(message)
{
    public SseDecodeFailure Failure { get; } = failure;
}

public sealed record SseDecoderOptions(
    int ReadBufferBytes = 4096,
    int MaximumLineCharacters = PiRequestBudget.StreamCharacters,
    int MaximumEventCharacters = PiRequestBudget.StreamCharacters,
    bool RejectInvalidUtf8 = false,
    SseEofBehavior EofBehavior = SseEofBehavior.DiscardPendingEvent)
{
    /// <summary>Opt-in framing semantics. OpenAISdk719 requires replacement UTF-8 and pending-event dispatch.</summary>
    public SseFramingProfile Profile { get; init; } = SseFramingProfile.Standard;
}

public sealed record SseStreamState(string LastEventId, BigInteger? RetryMilliseconds);
public sealed record SseEvent(string EventType, string Data, string LastEventId, BigInteger? RetryMilliseconds)
{
    /// <summary>SDK-profile event field: null when absent, empty when explicitly empty. Standard leaves this null.</summary>
    public string? EventName { get; init; }
}

/// <summary>Incremental UTF-8 SSE framing only. It does not interpret JSON, invoke providers or reconnect.</summary>
public sealed class SseDecoder
{
    private readonly SseDecoderOptions _options;
    private readonly StringBuilder _line = new();
    private readonly StringBuilder _data = new();
    private string _eventType = "";
    private bool _hasEventName;
    private string _idBuffer = "";
    private int _retryCharacters;
    private bool _atStart = true;
    private bool _skipLineFeed;
    private int _readerClaimed;
    private SseStreamState _state = new("", null);

    public SseDecoder(SseDecoderOptions? options = null)
    {
        _options = options ?? new();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.ReadBufferBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.ReadBufferBytes, 65_536);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaximumLineCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaximumEventCharacters);
        if (!Enum.IsDefined(_options.EofBehavior)) throw new ArgumentOutOfRangeException(nameof(options));
        if (!Enum.IsDefined(_options.Profile)) throw new ArgumentOutOfRangeException(nameof(options));
        if (SdkProfile && (_options.RejectInvalidUtf8 || _options.EofBehavior != SseEofBehavior.DispatchPendingEvent))
            throw new ArgumentException("OpenAI SDK framing requires replacement UTF-8 and pending-event dispatch.", nameof(options));
    }

    private bool SdkProfile => _options.Profile == SseFramingProfile.OpenAISdk719;

    /// <summary>Immutable connection-state snapshot. Retry fields apply immediately; IDs commit at a blank line.</summary>
    public SseStreamState State => Volatile.Read(ref _state);

    /// <summary>One decoder supports one enumeration. By default it owns and asynchronously disposes the input.</summary>
    public async IAsyncEnumerable<SseEvent> DecodeAsync(Stream input, bool leaveOpen = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (Interlocked.Exchange(ref _readerClaimed, 1) != 0)
            throw new InvalidOperationException("An SSE decoder has exactly one input enumeration.");

        try
        {
            if (!input.CanRead) throw new ArgumentException("An SSE input stream must be readable.", nameof(input));
            var encoding = new UTF8Encoding(false, _options.RejectInvalidUtf8);
            var utf8 = encoding.GetDecoder();
            var bytes = new byte[_options.ReadBufferBytes];
            var characters = new char[encoding.GetMaxCharCount(bytes.Length)];

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = await input.ReadAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var offset = 0;
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // A throwing bulk decode can discard valid characters before a malformed byte.
                    // Optional strict mode advances byte by byte so complete prefixes survive any read split.
                    var segmentLength = _options.RejectInvalidUtf8 ? Math.Min(1, count - offset) : count;
                    var characterCount = DecodeUtf8(utf8, bytes, offset, segmentLength, characters, flush: count == 0);
                    for (var index = 0; index < characterCount; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var message = Consume(characters[index]);
                        if (message is not null) yield return message;
                    }
                    offset += segmentLength;
                }
                while (offset < count);
                if (count != 0) continue;

                if (_options.EofBehavior == SseEofBehavior.DispatchPendingEvent)
                {
                    if (_line.Length > 0)
                    {
                        var completedLine = ProcessLine();
                        if (completedLine is not null) yield return completedLine;
                    }
                    var trailing = Dispatch();
                    if (trailing is not null) yield return trailing;
                }
                break;
            }
        }
        finally
        {
            _line.Clear(); _data.Clear(); _eventType = ""; _hasEventName = false; _idBuffer = "";
            if (!leaveOpen) await input.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static int DecodeUtf8(Decoder decoder, byte[] bytes, int offset, int count, char[] characters, bool flush)
    {
        try { return decoder.GetChars(bytes, offset, count, characters, 0, flush); }
        catch (DecoderFallbackException)
        { throw new SseDecodeException(SseDecodeFailure.InvalidUtf8, "SSE input contains invalid UTF-8."); }
    }

    private SseEvent? Consume(char character)
    {
        if (_atStart)
        {
            _atStart = false;
            if (!SdkProfile && character == '\uFEFF') return null;
        }
        if (_skipLineFeed)
        {
            _skipLineFeed = false;
            if (character == '\n') return null;
        }
        if (character is '\r' or '\n')
        {
            _skipLineFeed = character == '\r';
            return ProcessLine();
        }
        if (_line.Length >= _options.MaximumLineCharacters)
            throw new SseDecodeException(SseDecodeFailure.LineLimit, "SSE line character limit exceeded.");
        _line.Append(character);
        return null;
    }

    private SseEvent? ProcessLine()
    {
        if (_line.Length == 0) return Dispatch();
        var line = _line.ToString();
        _line.Clear();
        // The SDK invokes a reset TextDecoder for each completed line, stripping that line's first BOM.
        // The incremental replacement decoder has no pending UTF-8 sequence across an ASCII line ending.
        if (SdkProfile && line[0] == '\uFEFF') line = line[1..];
        if (line.Length == 0) return Dispatch();
        if (line[0] == ':') return null;
        var delimiter = line.IndexOf(':');
        var field = delimiter < 0 ? line : line[..delimiter];
        var value = delimiter < 0 ? "" : line[(delimiter + 1)..];
        if (value.StartsWith(' ')) value = value[1..];

        switch (field)
        {
            case "data":
                CheckEventSize(_data.Length + (long)value.Length + 1, _eventType.Length, _idBuffer, _retryCharacters);
                _data.Append(value); _data.Append('\n');
                break;
            case "event":
                CheckEventSize(_data.Length, value.Length, _idBuffer, _retryCharacters);
                _eventType = value;
                if (SdkProfile) _hasEventName = true;
                break;
            case "id" when !SdkProfile && !value.Contains('\0'):
                CheckEventSize(_data.Length, _eventType.Length, value, _retryCharacters);
                _idBuffer = value;
                break;
            case "retry" when !SdkProfile && value.Length > 0 && value.All(character => character is >= '0' and <= '9'):
                CheckEventSize(_data.Length, _eventType.Length, _idBuffer, value.Length);
                var retry = BigInteger.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
                _retryCharacters = value.Length;
                Volatile.Write(ref _state, State with { RetryMilliseconds = retry });
                break;
        }
        return null;
    }

    private SseEvent? Dispatch()
    {
        // SDK blank records with neither a truthy name nor a data field preserve the pending empty name.
        if (SdkProfile && _eventType.Length == 0 && _data.Length == 0) return null;
        Volatile.Write(ref _state, State with { LastEventId = _idBuffer });
        SseEvent? message = null;
        if (_data.Length != 0 || SdkProfile)
        {
            if (_data.Length != 0) _data.Length--; // Remove only the final data-field LF.
            var state = State;
            message = new(_eventType.Length == 0 ? "message" : _eventType, _data.ToString(),
                state.LastEventId, state.RetryMilliseconds)
            { EventName = SdkProfile && _hasEventName ? _eventType : null };
        }
        _data.Clear(); _eventType = ""; _hasEventName = false;
        return message;
    }

    private void CheckEventSize(long dataCharacters, int typeCharacters, string pendingId, int retryCharacters)
    {
        var committedId = State.LastEventId;
        var distinctCommittedCharacters = ReferenceEquals(committedId, pendingId) ? 0 : committedId.Length;
        if (dataCharacters + typeCharacters + pendingId.Length + distinctCommittedCharacters + retryCharacters > _options.MaximumEventCharacters)
            throw new SseDecodeException(SseDecodeFailure.EventLimit, "SSE event character limit exceeded.");
    }
}
