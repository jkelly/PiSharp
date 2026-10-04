using System.Text;
using PiSharp.Agent.Tools;

namespace PiSharp.Tools.Processes;

internal sealed class ProcessOutputLimitException : Exception { }

/// <summary>Single-consumer bounded capture. Raw spill and structured bytes stay separate from decoded model text.</summary>
internal sealed class ShellOutputAccumulator : IAsyncDisposable
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, false);
    private readonly Decoder _decoder = Utf8.GetDecoder();
    private readonly string _spillPath;
    private readonly ProcessRunnerOptions _options;
    private readonly IProcessOutputStorage _storage;
    private readonly MemoryStream _prefix = new();
    private readonly byte[] _head, _tail;
    private int _headCount, _tailCount, _tailNext;
    private int _rawBytes, _decodedBytes, _tailBytes, _completedLines, _totalLines, _lastLineBytes;
    private bool _openLine, _lineBoundary = true, _initialText = true, _finished, _outputIoFailed;
    private string _text = string.Empty;
    private Stream? _spill;
    private string? _createdPath;
    private Task? _close;

    public ShellOutputAccumulator(string spillPath, ProcessRunnerOptions options, IProcessOutputStorage storage)
    {
        _spillPath = spillPath; _options = options; _storage = storage;
        _head = new byte[options.StructuredMaxBytes / 2];
        _tail = new byte[options.StructuredMaxBytes - _head.Length];
    }

    public async ValueTask AppendAsync(ReadOnlyMemory<byte> bytes)
    {
        if (_finished) throw new InvalidOperationException("Output capture is finished.");
        var remaining = _options.MaximumRawBytes - _rawBytes;
        var admitted = bytes[..Math.Min(bytes.Length, remaining)];
        if (!admitted.IsEmpty)
        {
            _rawBytes += admitted.Length;
            RetainStructured(admitted.Span);
            Decode(admitted.Span, flush: false);
            try
            {
                if (_spill is not null || NeedsSpill())
                {
                    await EnsureSpillAsync().ConfigureAwait(false);
                    await _spill!.WriteAsync(admitted, CancellationToken.None).ConfigureAwait(false);
                }
                else await _prefix.WriteAsync(admitted, CancellationToken.None).ConfigureAwait(false);
            }
            catch { _outputIoFailed = true; throw; }
        }
        if (admitted.Length != bytes.Length) throw new ProcessOutputLimitException();
    }

    public async ValueTask FinishAsync()
    {
        if (_finished) return;
        _finished = true;
        Decode([], flush: true);
        if (!_outputIoFailed && NeedsSpill()) await EnsureSpillAsync().ConfigureAwait(false);
        if (_spill is not null) await _spill.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public ProcessOutputSnapshot Snapshot()
    {
        var content = _text;
        if (!_lineBoundary)
        {
            var newline = content.IndexOf('\n');
            if (newline >= 0) content = content[(newline + 1)..];
        }
        var tail = ToolOutputTruncator.Tail(content, new(_options.ModelMaxLines, _options.ModelMaxBytes));
        var truncated = _totalLines > _options.ModelMaxLines || _decodedBytes > _options.ModelMaxBytes;
        tail = tail with
        {
            Truncated = truncated,
            TruncatedBy = truncated ? tail.TruncatedBy ?? (_decodedBytes > _options.ModelMaxBytes
                ? ToolOutputTruncationLimit.Bytes : ToolOutputTruncationLimit.Lines) : null,
            TotalLines = _totalLines, TotalBytes = _decodedBytes
        };
        return new(tail.Content, tail, _rawBytes, _lastLineBytes, _createdPath);
    }

    public ProcessStructuredOutput Structured()
    {
        var tail = TailBytes();
        if (_rawBytes <= _options.StructuredMaxBytes)
        {
            var whole = new byte[_headCount + tail.Length];
            _head.AsSpan(0, _headCount).CopyTo(whole); tail.CopyTo(whole, _headCount);
            return new(DecodeWhole(whole), false);
        }
        var decoder = Utf8.GetDecoder();
        var chars = new char[Utf8.GetMaxCharCount(_headCount)];
        var used = decoder.GetChars(_head, 0, _headCount, chars, 0, flush: false);
        var head = StripBom(new string(chars, 0, used));
        var start = 0;
        while (start < tail.Length && (tail[start] & 0xc0) == 0x80) start++;
        var suffix = DecodeWhole(tail.AsSpan(start));
        var omitted = _rawBytes - _options.StructuredMaxBytes;
        return new(head + "\n\n[... " + omitted.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            " bytes omitted ...]\n\n" + suffix, true);
    }

    private void RetainStructured(ReadOnlySpan<byte> bytes)
    {
        var first = Math.Min(bytes.Length, _head.Length - _headCount);
        bytes[..first].CopyTo(_head.AsSpan(_headCount)); _headCount += first;
        bytes = bytes[first..];
        if (bytes.Length >= _tail.Length)
        {
            bytes[^_tail.Length..].CopyTo(_tail); _tailCount = _tail.Length; _tailNext = 0;
            return;
        }
        var beforeWrap = Math.Min(bytes.Length, _tail.Length - _tailNext);
        bytes[..beforeWrap].CopyTo(_tail.AsSpan(_tailNext));
        bytes[beforeWrap..].CopyTo(_tail);
        _tailNext = (_tailNext + bytes.Length) % _tail.Length;
        _tailCount = Math.Min(_tail.Length, _tailCount + bytes.Length);
    }

    private byte[] TailBytes()
    {
        var bytes = new byte[_tailCount];
        var start = _tailCount == _tail.Length ? _tailNext : 0;
        var first = Math.Min(bytes.Length, _tail.Length - start);
        _tail.AsSpan(start, first).CopyTo(bytes);
        _tail.AsSpan(0, bytes.Length - first).CopyTo(bytes.AsSpan(first));
        return bytes;
    }

    private void Decode(ReadOnlySpan<byte> bytes, bool flush)
    {
        var chars = new char[Utf8.GetMaxCharCount(bytes.Length)];
        var count = _decoder.GetChars(bytes, chars, flush);
        if (count == 0) return;
        var text = new string(chars, 0, count);
        if (_initialText) { _initialText = false; text = StripBom(text); }
        if (text.Length == 0) return;
        var length = Utf8.GetByteCount(text);
        _decodedBytes += length; _tailBytes += length; _text += text;
        if (_tailBytes > _options.ModelMaxBytes * 4)
        {
            var encoded = Utf8.GetBytes(_text);
            var start = encoded.Length - _options.ModelMaxBytes * 2;
            while (start < encoded.Length && (encoded[start] & 0xc0) == 0x80) start++;
            _lineBoundary = start == 0 ? _lineBoundary : encoded[start - 1] == 0x0a;
            _text = Utf8.GetString(encoded.AsSpan(start)); _tailBytes = encoded.Length - start;
        }
        var newlineCount = 0; var lastNewline = -1;
        for (var index = 0; index < text.Length; index++)
            if (text[index] == '\n') { newlineCount++; lastNewline = index; }
        if (newlineCount == 0) { _lastLineBytes += length; _openLine = true; }
        else
        {
            _completedLines += newlineCount;
            _lastLineBytes = Utf8.GetByteCount(text.AsSpan(lastNewline + 1));
            _openLine = lastNewline + 1 < text.Length;
        }
        _totalLines = _completedLines + (_openLine ? 1 : 0);
    }

    private bool NeedsSpill() => _rawBytes > _options.ModelMaxBytes ||
        _decodedBytes > _options.ModelMaxBytes || _totalLines > _options.ModelMaxLines;

    private async ValueTask EnsureSpillAsync()
    {
        if (_spill is not null) return;
        var stream = await _storage.CreateNewAsync(_spillPath).ConfigureAwait(false);
        if (stream is null) throw new IOException("Output storage returned no stream.");
        _spill = stream; _createdPath = _spillPath;
        if (!stream.CanWrite) throw new IOException("Output storage is not writable.");
        if (_prefix.Length > 0)
            await stream.WriteAsync(_prefix.GetBuffer().AsMemory(0, checked((int)_prefix.Length)), CancellationToken.None).ConfigureAwait(false);
        _prefix.SetLength(0);
    }

    public ValueTask DisposeAsync() => new(_close ??= CloseAsync());
    private async Task CloseAsync()
    {
        try { if (_spill is not null) await _spill.DisposeAsync().ConfigureAwait(false); }
        finally { _spill = null; _prefix.Dispose(); }
    }
    private static string DecodeWhole(ReadOnlySpan<byte> bytes) => StripBom(Utf8.GetString(bytes));
    private static string StripBom(string text) => text.StartsWith('\ufeff') ? text[1..] : text;
}
