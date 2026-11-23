using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace PiSharp.Tools.Processes;

/// <summary>Retains the original joined process receipt and each original pipe's captured bytes.</summary>
public sealed record SeparatedProcessRunResult(ProcessRunResult Process,
    ProcessStructuredOutput StandardOutput, ProcessStructuredOutput StandardError)
{
    /// <summary>The request's <see cref="ProcessRequest.CaptureLimitBytes"/> was exceeded: the runner stopped the process, and each
    /// pipe keeps only its complete UTF-8 characters within the limit.</summary>
    [Experimental("PISHARP_EXEC001")] public bool CaptureLimitReached { get; init; }
    /// <summary>True when a streaming stdout line filter discarded at least one non-whitespace line.</summary>
    internal bool StandardOutputFilteredNonEmpty { get; init; }
}

public interface ISeparatedProcessRunner : IProcessRunner
{
    ValueTask<SeparatedProcessRunResult> RunSeparatedAsync(ProcessRequest request, CancellationToken cancellationToken = default);
}

// Only the corresponding original pipe pump appends to each buffer; snapshots occur after both pumps join. A combined limit is
// shared by both pumps, so its appends are serialized.
public sealed class SeparatedProcessCapture : IDisposable
{
    // Same byte bound as native pi.exec, below both MemoryStream's and the runtime's string limit.
    internal const long MaximumCombinedBytes = 0x3FFFFFDF;
    private const int MaximumPendingLineBytes = 4 * 1024 * 1024;
    private readonly int _maximumBytes;
    private readonly long? _combinedLimit;
    private readonly Func<string, bool>? _stdoutLineFilter;
    private readonly int? _stdoutMatchLimit;
    private readonly object _gate = new();
    private readonly MemoryStream _stdout = new();
    private readonly MemoryStream _stderr = new();
    private readonly MemoryStream? _pendingStdoutLine;
    private bool _stdoutTruncated, _stderrTruncated, _stdoutMatchLimitReached, _stdoutFlushed, _stdoutFilteredNonEmpty;
    private int _stdoutMatchedLines;
    public SeparatedProcessCapture(int maximumBytes)
    {
        if (maximumBytes is < 1 or > 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _maximumBytes = maximumBytes;
    }
    /// <summary>A capture whose stdout lines are filtered as they stream before entering the bounded stdout buffer.</summary>
    internal SeparatedProcessCapture(int maximumBytes, Func<string, bool> standardOutputLineFilter, int? matchLimit = null) : this(maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(standardOutputLineFilter);
        if (matchLimit is <= 0) throw new ArgumentOutOfRangeException(nameof(matchLimit));
        _stdoutLineFilter = standardOutputLineFilter;
        _stdoutMatchLimit = matchLimit;
        _pendingStdoutLine = new MemoryStream();
    }
    /// <summary>A capture bounded by both pipes together (source execCommand maxBuffer). The explicit combined limit replaces
    /// the ordinary per-pipe bound; exceeding it sets <see cref="LimitReached"/> so the runner stops the process.
    /// Buffers grow only as bytes arrive. <paramref name="maximumBytes"/> retains its ordinary-bound validation.</summary>
    [Experimental("PISHARP_EXEC001")]
    public SeparatedProcessCapture(int maximumBytes, long combinedLimit) : this(maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(combinedLimit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(combinedLimit, MaximumCombinedBytes);
        _combinedLimit = combinedLimit;
        _maximumBytes = (int)combinedLimit;
    }
    [Experimental("PISHARP_EXEC001")] public bool LimitReached { get; private set; }
    internal bool ShouldStopProcess => LimitReached || _stdoutMatchLimitReached;
    public void Append(bool standardError, ReadOnlySpan<byte> bytes) => AppendAdmitted(standardError, bytes);

    // Return only the prefix admitted to an explicit capture. Pumps still drain both pipes, but must not send over-limit
    // bytes to the separate raw-output accumulator, where they could turn normal maxBuffer termination into an I/O failure.
    internal int AppendAdmitted(bool standardError, ReadOnlySpan<byte> bytes)
    {
        if (_combinedLimit is null) { return AppendCore(standardError, bytes); }
        lock (_gate) return AppendCore(standardError, bytes);
    }
    private int AppendCore(bool standardError, ReadOnlySpan<byte> bytes)
    {
        if (LimitReached) return 0;
        if (!standardError && _stdoutLineFilter is not null)
        {
            AppendFilteredStdout(bytes);
            return 0;
        }
        // The pipes share the combined limit; the read that crosses it keeps only the bytes within it.
        if (_combinedLimit is { } limit && _stdout.Length + _stderr.Length + bytes.Length > limit)
        { bytes = bytes[..(int)(limit - _stdout.Length - _stderr.Length)]; LimitReached = true; }
        var buffer = standardError ? _stderr : _stdout;
        var count = Math.Min(bytes.Length, _maximumBytes - (int)buffer.Length);
        buffer.Write(bytes[..count]);
        if (count != bytes.Length)
        {
            if (standardError) _stderrTruncated = true; else _stdoutTruncated = true;
            if (_combinedLimit is not null) LimitReached = true;
        }
        return _combinedLimit is null ? bytes.Length : count;
    }
    private void AppendFilteredStdout(ReadOnlySpan<byte> bytes)
    {
        if (_stdoutMatchLimitReached || _stdoutTruncated) return;
        var pending = _pendingStdoutLine!;
        var offset = 0;
        while (offset < bytes.Length)
        {
            var remaining = bytes[offset..];
            var newline = remaining.IndexOf((byte)'\n');
            var segment = newline < 0 ? remaining : remaining[..newline];
            if (pending.Length + segment.Length > MaximumPendingLineBytes)
            {
                _stdoutTruncated = true;
                return;
            }
            if (segment.Length > 0) pending.Write(segment);
            if (newline < 0) return;
            offset += newline + 1;
            ProcessPendingStdoutLine();
            pending.SetLength(0);
            if (_stdoutMatchLimitReached || _stdoutTruncated) return;
        }
    }
    private void ProcessPendingStdoutLine()
    {
        var pending = _pendingStdoutLine!;
        var length = (int)pending.Length;
        if (length == 0) return;
        var span = pending.GetBuffer().AsSpan(0, length);
        if (span[^1] == (byte)'\r') span = span[..^1];
        if (span.IsEmpty) return;
        if (span.Contains((byte)0))
        {
            if (_stdout.Length < _maximumBytes) _stdout.WriteByte(0);
            else _stdoutTruncated = true;
            return;
        }
        var line = Encoding.UTF8.GetString(span);
        if (!_stdoutLineFilter!(line))
        {
            if (!string.IsNullOrWhiteSpace(line)) _stdoutFilteredNonEmpty = true;
            return;
        }
        var needed = span.Length + 1;
        if (_stdout.Length + needed > _maximumBytes)
        {
            var room = _maximumBytes - (int)_stdout.Length;
            if (room > 0) _stdout.Write(span[..Math.Min(room, span.Length)]);
            _stdoutTruncated = true;
            return;
        }
        _stdout.Write(span);
        _stdout.WriteByte((byte)'\n');
        _stdoutMatchedLines++;
        if (_stdoutMatchLimit is { } cap && _stdoutMatchedLines >= cap) _stdoutMatchLimitReached = true;
    }
    internal ProcessRunnerOptions OutputOptions(ProcessRunnerOptions options) => _combinedLimit is { } limit
        ? options with { MaximumRawBytes = Math.Max(options.MaximumRawBytes, (int)limit) } : options;
    public SeparatedProcessRunResult Result(ProcessRunResult original)
    {
        if (_stdoutLineFilter is not null && !_stdoutFlushed)
        {
            _stdoutFlushed = true;
            if (!_stdoutMatchLimitReached && !_stdoutTruncated && _pendingStdoutLine is { Length: > 0 })
            {
                ProcessPendingStdoutLine();
                _pendingStdoutLine.SetLength(0);
            }
        }
        return new(original,
            new(Decode(_stdout), _stdoutTruncated || LimitReached), new(Decode(_stderr), _stderrTruncated || LimitReached))
        { CaptureLimitReached = LimitReached, StandardOutputFilteredNonEmpty = _stdoutFilteredNonEmpty };
    }
    // A stopped capture drops an incomplete UTF-8 suffix rather than decoding it as a replacement character (source execCommand).
    private string Decode(MemoryStream buffer)
    {
        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        return Encoding.UTF8.GetString(LimitReached ? bytes[..CompleteUtf8Length(bytes)] : bytes);
    }
    private static int CompleteUtf8Length(ReadOnlySpan<byte> bytes)
    {
        for (var back = 1; back <= Math.Min(3, bytes.Length); back++)
        {
            var value = bytes[^back];
            if ((value & 0xC0) == 0x80) continue;
            var length = value >= 0xF0 ? 4 : value >= 0xE0 ? 3 : value >= 0xC0 ? 2 : 1;
            return length > back ? bytes.Length - back : bytes.Length;
        }
        return bytes.Length;
    }
    public void Dispose() { _stdout.Dispose(); _stderr.Dispose(); _pendingStdoutLine?.Dispose(); }
}
