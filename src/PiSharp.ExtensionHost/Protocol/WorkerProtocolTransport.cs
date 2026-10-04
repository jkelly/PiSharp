using System.Text;

namespace PiSharp.ExtensionHost.Protocol;

/// <summary>Borrowed I/O only. LF is the delimiter; one preceding CR is stripped; any nonempty EOF tail fails.</summary>
public sealed class WorkerProtocolTransport
{
    private readonly TextReader? _reader; private readonly TextWriter? _writer;
    private readonly Stream? _input; private readonly Stream? _output;
    private readonly WorkerProtocolOptions _options; private readonly WorkerFrameCodec _codec;
    private readonly char[] _characters; private readonly byte[] _bytes; private int _position, _available;
    private int _readClaimed;
    public WorkerProtocolTransport(TextReader reader, TextWriter writer, WorkerProtocolOptions? options = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader)); _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _options = options ?? new(); _options.Validate(); _codec = new(_options);
        _characters = new char[_options.ReadBufferSize]; _bytes = [];
    }
    public WorkerProtocolTransport(Stream input, Stream output, WorkerProtocolOptions? options = null)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input)); _output = output ?? throw new ArgumentNullException(nameof(output));
        if (!input.CanRead || !output.CanWrite) throw new ArgumentException("Worker streams need read/write capabilities.");
        _options = options ?? new(); _options.Validate(); _codec = new(_options);
        _bytes = new byte[_options.ReadBufferSize]; _characters = [];
    }
    public WorkerFrameCodec Codec => _codec;
    internal WorkerProtocolOptions Options => _options;
    /// <summary>Standalone pull admission. A connection must exclusively own this reader while running.</summary>
    public async ValueTask<WorkerMessage?> ReadMessageAsync(CancellationToken cancellationToken = default)
    { var frame = await ReadAsync(cancellationToken).ConfigureAwait(false); return frame?.Message; }
    internal async ValueTask<(WorkerMessage Message, int Bytes)?> ReadAsync(CancellationToken token)
    {
        if (Interlocked.Exchange(ref _readClaimed, 1) != 0) throw new InvalidOperationException("Concurrent protocol reads are not supported.");
        try
        {
            var text = _reader is null ? null : new StringBuilder(Math.Min(_options.MaximumFrameBytes, 4096));
            using var bytes = _reader is null ? new MemoryStream(Math.Min(_options.MaximumFrameBytes, 4096)) : null;
            while (true)
            {
                if (_position == _available)
                {
                    _available = _reader is { } reader ? await reader.ReadAsync(_characters.AsMemory(), token).ConfigureAwait(false) :
                        await _input!.ReadAsync(_bytes.AsMemory(), token).ConfigureAwait(false);
                    _position = 0;
                    if (_available == 0)
                    {
                        if ((text?.Length ?? bytes!.Length) != 0) throw new WorkerProtocolException(WorkerProtocolFailure.PartialFinalFrame);
                        return null;
                    }
                }
                token.ThrowIfCancellationRequested();
                var c = _reader is null ? (int)_bytes[_position++] : _characters[_position++];
                if (c == '\n')
                {
                    if (text is not null)
                    {
                        if (text.Length > 0 && text[^1] == '\r') text.Length--;
                        var raw = text.ToString(); return (_codec.Decode(raw), WorkerFrameCodec.Utf8.GetByteCount(raw) + 1);
                    }
                    var length = checked((int)bytes!.Length); var buffer = bytes.GetBuffer();
                    if (length > 0 && buffer[length - 1] == '\r') length--;
                    return (_codec.Decode(buffer.AsSpan(0, length)), length + 1);
                }
                var count = text?.Length ?? bytes!.Length;
                if (count >= _options.MaximumFrameBytes && !(count == _options.MaximumFrameBytes && c == '\r'))
                    throw new WorkerProtocolException(WorkerProtocolFailure.FrameLimit);
                if (text is not null) text.Append((char)c); else bytes!.WriteByte((byte)c);
            }
        }
        finally { Volatile.Write(ref _readClaimed, 0); }
    }
    internal async ValueTask WriteAsync(string frame, CancellationToken token)
    {
        if (_writer is { } writer)
        { await writer.WriteAsync(frame.AsMemory(), token).ConfigureAwait(false); await writer.FlushAsync(token).ConfigureAwait(false); }
        else
        { await _output!.WriteAsync(WorkerFrameCodec.Utf8.GetBytes(frame), token).ConfigureAwait(false); await _output.FlushAsync(token).ConfigureAwait(false); }
        token.ThrowIfCancellationRequested();
    }
}
