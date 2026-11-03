using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace PiSharp.Tools.Processes;

/// <summary>Retains the original joined process receipt and each original pipe's independently bounded bytes.</summary>
public sealed record SeparatedProcessRunResult(ProcessRunResult Process,
    ProcessStructuredOutput StandardOutput, ProcessStructuredOutput StandardError)
{
    /// <summary>The request's <see cref="ProcessRequest.CaptureLimitBytes"/> was reached: the runner stopped the process, and each
    /// pipe keeps only its complete UTF-8 characters within the limit.</summary>
    [Experimental("PISHARP_EXEC001")] public bool CaptureLimitReached { get; init; }
}

public interface ISeparatedProcessRunner : IProcessRunner
{
    ValueTask<SeparatedProcessRunResult> RunSeparatedAsync(ProcessRequest request, CancellationToken cancellationToken = default);
}

// Only the corresponding original pipe pump appends to each buffer; snapshots occur after both pumps join. A combined limit is
// shared by both pumps, so its appends are serialized.
public sealed class SeparatedProcessCapture : IDisposable
{
    private readonly int _maximumBytes;
    private readonly long? _combinedLimit;
    private readonly object _gate = new();
    private readonly MemoryStream _stdout = new();
    private readonly MemoryStream _stderr = new();
    private bool _stdoutTruncated, _stderrTruncated;
    public SeparatedProcessCapture(int maximumBytes)
    {
        if (maximumBytes is < 1 or > 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _maximumBytes = maximumBytes;
    }
    /// <summary>A capture that is also bounded by both pipes together (source execCommand maxBuffer). Reaching that limit, or either
    /// pipe's own bound, sets <see cref="LimitReached"/> so the runner stops the process.</summary>
    [Experimental("PISHARP_EXEC001")]
    public SeparatedProcessCapture(int maximumBytes, long combinedLimit) : this(maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(combinedLimit);
        _combinedLimit = combinedLimit;
    }
    [Experimental("PISHARP_EXEC001")] public bool LimitReached { get; private set; }
    public void Append(bool standardError, ReadOnlySpan<byte> bytes)
    {
        if (_combinedLimit is null) { AppendCore(standardError, bytes); return; }
        lock (_gate) AppendCore(standardError, bytes);
    }
    private void AppendCore(bool standardError, ReadOnlySpan<byte> bytes)
    {
        if (LimitReached) return;
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
    }
    public SeparatedProcessRunResult Result(ProcessRunResult original) => new(original,
        new(Decode(_stdout), _stdoutTruncated || LimitReached), new(Decode(_stderr), _stderrTruncated || LimitReached))
    { CaptureLimitReached = LimitReached };
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
    public void Dispose() { _stdout.Dispose(); _stderr.Dispose(); }
}
